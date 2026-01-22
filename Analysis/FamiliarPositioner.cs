using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Enums;
using UnityEngine;
using CompanionAI_v3.GameInterface;

namespace CompanionAI_v3.Analysis
{
    /// <summary>
    /// ★ v3.7.00: 사역마 위치 최적화 시스템
    /// - 4타일 반경 규칙 기반 최적 위치 계산
    /// - 사역마 타입별 다른 위치 전략
    /// </summary>
    public static class FamiliarPositioner
    {
        #region Constants

        /// <summary>
        /// 사역마 효과 반경 (타일)
        /// </summary>
        public const float EFFECT_RADIUS_TILES = 4f;

        /// <summary>
        /// Relocate가 필요한 최소 거리 (타일)
        /// ★ v3.7.22: 3f → 2f로 변경 (2타일 이동도 유의미한 커버리지 향상 가능)
        /// </summary>
        public const float MIN_RELOCATE_DISTANCE_TILES = 2f;

        /// <summary>
        /// 위치 평가를 위한 샘플링 간격 (미터)
        /// </summary>
        private const float POSITION_SAMPLE_INTERVAL = 2f;

        /// <summary>
        /// 최대 샘플링 반경 (미터)
        /// </summary>
        private const float MAX_SAMPLE_RADIUS = 15f;

        #endregion

        #region Position Score

        /// <summary>
        /// 위치 점수 결과
        /// </summary>
        public class PositionScore
        {
            public Vector3 Position { get; set; }
            public int AlliesInRange { get; set; }
            public int EnemiesInRange { get; set; }
            public float Score { get; set; }
            public string Reason { get; set; }

            public override string ToString()
            {
                return $"Pos=({Position.x:F1}, {Position.z:F1}), Allies={AlliesInRange}, Enemies={EnemiesInRange}, Score={Score:F1}";
            }
        }

        #endregion

        #region Main API

        /// <summary>
        /// 사역마 타입별 최적 위치 계산
        /// ★ v3.7.22: maxRangeMeters 파라미터 추가 - Relocate 사거리 제한
        /// </summary>
        public static PositionScore FindOptimalPosition(
            BaseUnitEntity master,
            PetType familiarType,
            List<BaseUnitEntity> allies,
            List<BaseUnitEntity> enemies,
            float maxRangeMeters = 0f)
        {
            if (master == null)
                return CreateDefaultPosition(master?.Position ?? Vector3.zero);

            try
            {
                return familiarType switch
                {
                    // 버프 확산형: 아군 중심
                    PetType.ServoskullSwarm => FindBuffCenterPosition(master, allies, enemies, maxRangeMeters),
                    PetType.Raven => FindBuffCenterPosition(master, allies, enemies, maxRangeMeters),

                    // 적 제어형: 위협적 적 근처
                    PetType.Mastiff => FindApprehendPosition(master, allies, enemies, maxRangeMeters),
                    PetType.Eagle => FindDisruptPosition(master, allies, enemies, maxRangeMeters),

                    // 기본값
                    _ => FindBuffCenterPosition(master, allies, enemies, maxRangeMeters)
                };
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[FamiliarPositioner] FindOptimalPosition error: {ex.Message}");
                return CreateDefaultPosition(master.Position);
            }
        }

        /// <summary>
        /// Relocate가 필요한지 판단
        /// ★ v3.7.22: 현재 위치 대비 커버리지 향상 기반으로 판단 (절대 점수 기준 제거)
        /// </summary>
        public static bool ShouldRelocate(
            BaseUnitEntity familiar,
            PositionScore optimalPosition,
            float currentDistanceToOptimal,
            int currentAlliesInRange = 0,
            int currentEnemiesInRange = 0)
        {
            if (familiar == null || optimalPosition == null)
                return false;

            // 1. 최소 거리 이상 떨어져 있어야 Relocate 의미 있음
            float distanceTiles = CombatAPI.MetersToTiles(currentDistanceToOptimal);
            if (distanceTiles < MIN_RELOCATE_DISTANCE_TILES)
                return false;

            // 2. 최적 위치에서 효과를 받을 유닛이 2명 이상이어야 의미 있음
            int totalAffected = optimalPosition.AlliesInRange + optimalPosition.EnemiesInRange;
            if (totalAffected < 2)
                return false;

            // ★ v3.7.22: 커버리지 향상 기준으로 판단
            // 현재 위치 대비 아군 또는 적 1명 이상 추가 커버 가능하면 Relocate 가치 있음
            int alliesGained = optimalPosition.AlliesInRange - currentAlliesInRange;
            int enemiesGained = optimalPosition.EnemiesInRange - currentEnemiesInRange;
            int netGain = alliesGained + enemiesGained;

            // 최소 1명 이상 추가 커버 가능해야 함
            if (netGain < 1)
            {
                Main.LogDebug($"[FamiliarPositioner] ShouldRelocate: No (no coverage gain: allies {currentAlliesInRange}→{optimalPosition.AlliesInRange}, enemies {currentEnemiesInRange}→{optimalPosition.EnemiesInRange})");
                return false;
            }

            Main.LogDebug($"[FamiliarPositioner] ShouldRelocate: Yes (distance={distanceTiles:F1} tiles, gain=+{netGain}, allies {currentAlliesInRange}→{optimalPosition.AlliesInRange}, enemies {currentEnemiesInRange}→{optimalPosition.EnemiesInRange})");
            return true;
        }

        #endregion

        #region Position Strategies

        /// <summary>
        /// 버프 확산형 (Servo-Skull, Psyber-Raven): 아군 중심 위치
        /// ★ v3.7.22: maxRangeMeters 파라미터 추가
        /// </summary>
        private static PositionScore FindBuffCenterPosition(
            BaseUnitEntity master,
            List<BaseUnitEntity> allies,
            List<BaseUnitEntity> enemies,
            float maxRangeMeters = 0f)
        {
            var validAllies = allies?.Where(a => a != null && a.IsConscious && !FamiliarAPI.IsFamiliar(a)).ToList()
                ?? new List<BaseUnitEntity>();

            if (validAllies.Count == 0)
                return CreateDefaultPosition(master.Position);

            // 아군들의 중심점 계산
            Vector3 centroid = CalculateCentroid(validAllies);

            // 중심점 주변에서 최적 위치 탐색
            var bestPosition = FindBestPositionAroundPoint(
                centroid,
                master,
                validAllies,
                enemies ?? new List<BaseUnitEntity>(),
                prioritizeAllies: true,
                maxRangeMeters: maxRangeMeters);

            Main.LogDebug($"[FamiliarPositioner] BuffCenter: {bestPosition}");
            return bestPosition;
        }

        /// <summary>
        /// Apprehend 타겟 (Cyber-Mastiff): 위협적 원거리 적 근처
        /// ★ v3.7.22: maxRangeMeters 파라미터 추가
        /// </summary>
        private static PositionScore FindApprehendPosition(
            BaseUnitEntity master,
            List<BaseUnitEntity> allies,
            List<BaseUnitEntity> enemies,
            float maxRangeMeters = 0f)
        {
            var validEnemies = enemies?.Where(e => e != null && e.IsConscious).ToList()
                ?? new List<BaseUnitEntity>();

            if (validEnemies.Count == 0)
                return CreateDefaultPosition(master.Position);

            // 위협적인 원거리 적/시전자 찾기
            // 근접 적은 이미 Tank가 처리하므로, 원거리 위협 우선
            var threateningEnemies = validEnemies
                .Where(e => IsRangedOrCasterEnemy(e))
                .OrderByDescending(e => EstimateThreat(e))
                .Take(3)
                .ToList();

            if (threateningEnemies.Count == 0)
                threateningEnemies = validEnemies.Take(3).ToList();

            // 위협적 적의 중심점
            Vector3 targetCenter = CalculateCentroid(threateningEnemies);

            // 해당 위치 근처에서 최적 위치 탐색
            var bestPosition = FindBestPositionAroundPoint(
                targetCenter,
                master,
                allies ?? new List<BaseUnitEntity>(),
                validEnemies,
                prioritizeAllies: false,
                maxRangeMeters: maxRangeMeters);

            bestPosition.Reason = $"Near threatening enemies ({threateningEnemies.Count})";
            Main.LogDebug($"[FamiliarPositioner] Apprehend: {bestPosition}");
            return bestPosition;
        }

        /// <summary>
        /// 적 교란형 (Cyber-Eagle): 적 밀집 지역
        /// ★ v3.7.22: maxRangeMeters 파라미터 추가
        /// </summary>
        private static PositionScore FindDisruptPosition(
            BaseUnitEntity master,
            List<BaseUnitEntity> allies,
            List<BaseUnitEntity> enemies,
            float maxRangeMeters = 0f)
        {
            var validEnemies = enemies?.Where(e => e != null && e.IsConscious).ToList()
                ?? new List<BaseUnitEntity>();

            if (validEnemies.Count == 0)
                return CreateDefaultPosition(master.Position);

            // 적 밀집 지역 찾기
            var bestClusterCenter = FindEnemyClusterCenter(validEnemies);

            // 해당 위치 근처에서 최적 위치 탐색
            var bestPosition = FindBestPositionAroundPoint(
                bestClusterCenter,
                master,
                allies ?? new List<BaseUnitEntity>(),
                validEnemies,
                prioritizeAllies: false,
                maxRangeMeters: maxRangeMeters);

            bestPosition.Reason = "Enemy cluster center";
            Main.LogDebug($"[FamiliarPositioner] Disrupt: {bestPosition}");
            return bestPosition;
        }

        #endregion

        #region Position Calculation Helpers

        /// <summary>
        /// 주어진 점 주변에서 최적 위치 탐색
        /// ★ v3.7.22: maxRangeMeters 추가 - 마스터로부터 이 범위 내 위치만 고려
        /// </summary>
        private static PositionScore FindBestPositionAroundPoint(
            Vector3 center,
            BaseUnitEntity master,
            List<BaseUnitEntity> allies,
            List<BaseUnitEntity> enemies,
            bool prioritizeAllies,
            float maxRangeMeters = 0f)
        {
            PositionScore best = null;
            float bestScore = float.MinValue;
            Vector3 masterPos = master?.Position ?? center;

            // ★ v3.7.22: maxRange가 0이면 제한 없음
            bool hasRangeLimit = maxRangeMeters > 0f;

            // 중심점에서 시작하여 나선형으로 탐색
            for (float radius = 0; radius <= MAX_SAMPLE_RADIUS; radius += POSITION_SAMPLE_INTERVAL)
            {
                if (radius == 0)
                {
                    // 중심점 평가
                    // ★ v3.7.22: 범위 체크
                    if (hasRangeLimit && Vector3.Distance(masterPos, center) > maxRangeMeters)
                    {
                        // 범위 밖이지만 일단 평가 (폴백용)
                        var score = EvaluatePosition(center, allies, enemies, prioritizeAllies);
                        score.Reason = "Out of range (fallback)";
                        if (best == null || score.Score > bestScore)
                        {
                            bestScore = score.Score;
                            best = score;
                        }
                    }
                    else
                    {
                        var score = EvaluatePosition(center, allies, enemies, prioritizeAllies);
                        if (score.Score > bestScore)
                        {
                            bestScore = score.Score;
                            best = score;
                        }
                    }
                }
                else
                {
                    // 원형으로 샘플링
                    int samples = Mathf.Max(4, Mathf.RoundToInt(radius * 2));
                    for (int i = 0; i < samples; i++)
                    {
                        float angle = (float)i / samples * Mathf.PI * 2;
                        Vector3 pos = center + new Vector3(
                            Mathf.Cos(angle) * radius,
                            0,
                            Mathf.Sin(angle) * radius);

                        // ★ v3.7.22: 범위 체크 - 범위 내 위치만 고려
                        if (hasRangeLimit && Vector3.Distance(masterPos, pos) > maxRangeMeters)
                            continue;

                        var score = EvaluatePosition(pos, allies, enemies, prioritizeAllies);
                        if (score.Score > bestScore)
                        {
                            bestScore = score.Score;
                            best = score;
                        }
                    }
                }
            }

            // ★ v3.7.22: 범위 내에 좋은 위치가 없으면, 마스터로부터 최적 방향으로 최대 거리 지점 사용
            if (best != null && hasRangeLimit && best.Reason == "Out of range (fallback)")
            {
                // 최적 중심점 방향으로 maxRange만큼 이동한 위치
                Vector3 direction = (center - masterPos).normalized;
                Vector3 clampedPos = masterPos + direction * maxRangeMeters;
                var clampedScore = EvaluatePosition(clampedPos, allies, enemies, prioritizeAllies);
                clampedScore.Reason = "Range-clamped position";
                Main.LogDebug($"[FamiliarPositioner] Optimal position out of range, using clamped position at {maxRangeMeters:F1}m");
                return clampedScore;
            }

            return best ?? CreateDefaultPosition(center);
        }

        /// <summary>
        /// 위치 점수 평가
        /// </summary>
        private static PositionScore EvaluatePosition(
            Vector3 position,
            List<BaseUnitEntity> allies,
            List<BaseUnitEntity> enemies,
            bool prioritizeAllies)
        {
            int alliesInRange = FamiliarAPI.CountAlliesInRadius(position, EFFECT_RADIUS_TILES, allies);
            int enemiesInRange = FamiliarAPI.CountEnemiesInRadius(position, EFFECT_RADIUS_TILES, enemies);

            float score;
            if (prioritizeAllies)
            {
                // 버프 확산: 아군 우선
                score = alliesInRange * 30f + enemiesInRange * 5f;

                // 3명 이상 아군이면 보너스
                if (alliesInRange >= 3) score += 50f;
            }
            else
            {
                // 적 제어: 적 우선
                score = enemiesInRange * 30f + alliesInRange * 5f;

                // 2명 이상 적이면 보너스
                if (enemiesInRange >= 2) score += 40f;
            }

            return new PositionScore
            {
                Position = position,
                AlliesInRange = alliesInRange,
                EnemiesInRange = enemiesInRange,
                Score = score,
                Reason = prioritizeAllies ? "Ally coverage" : "Enemy coverage"
            };
        }

        /// <summary>
        /// 유닛 목록의 중심점 계산
        /// </summary>
        private static Vector3 CalculateCentroid(List<BaseUnitEntity> units)
        {
            if (units == null || units.Count == 0)
                return Vector3.zero;

            Vector3 sum = Vector3.zero;
            int count = 0;

            foreach (var unit in units)
            {
                if (unit != null)
                {
                    sum += unit.Position;
                    count++;
                }
            }

            return count > 0 ? sum / count : Vector3.zero;
        }

        /// <summary>
        /// 적 클러스터 중심 찾기
        /// </summary>
        private static Vector3 FindEnemyClusterCenter(List<BaseUnitEntity> enemies)
        {
            if (enemies == null || enemies.Count == 0)
                return Vector3.zero;

            // 가장 많은 적이 모인 위치 찾기
            Vector3 bestCenter = enemies[0].Position;
            int bestCount = 0;

            foreach (var enemy in enemies)
            {
                int count = FamiliarAPI.CountEnemiesInRadius(enemy.Position, EFFECT_RADIUS_TILES, enemies);
                if (count > bestCount)
                {
                    bestCount = count;
                    bestCenter = enemy.Position;
                }
            }

            // 해당 적 주변 적들의 중심점 반환
            var nearbyEnemies = FamiliarAPI.GetEnemiesInRadius(bestCenter, EFFECT_RADIUS_TILES, enemies);
            return CalculateCentroid(nearbyEnemies);
        }

        /// <summary>
        /// 원거리/시전자 적인지 확인
        /// </summary>
        private static bool IsRangedOrCasterEnemy(BaseUnitEntity enemy)
        {
            if (enemy == null) return false;

            try
            {
                // 무기 확인 - 원거리 무기 보유 여부
                var weapon = enemy.Body?.PrimaryHand?.MaybeWeapon;
                if (weapon != null && weapon.Blueprint?.IsMelee == false)
                    return true;

                // 보조 무기도 확인
                var secondaryWeapon = enemy.Body?.SecondaryHand?.MaybeWeapon;
                if (secondaryWeapon != null && secondaryWeapon.Blueprint?.IsMelee == false)
                    return true;
            }
            catch
            {
                // 에러 시 false 반환
            }

            return false;
        }

        /// <summary>
        /// 적 위협도 추정
        /// </summary>
        private static float EstimateThreat(BaseUnitEntity enemy)
        {
            if (enemy == null) return 0f;

            float threat = 0f;

            try
            {
                // 기본 위협 (HP 기반)
                float hpRatio = enemy.Health.HitPointsLeft / (float)enemy.Health.MaxHitPoints;
                threat += (1f - hpRatio) * 10f;  // 체력 많이 남은 적이 더 위협적

                // 원거리면 추가 위협
                if (IsRangedOrCasterEnemy(enemy))
                    threat += 30f;
            }
            catch
            {
                threat = 10f;
            }

            return threat;
        }

        /// <summary>
        /// 기본 위치 생성 (마스터 근처)
        /// </summary>
        private static PositionScore CreateDefaultPosition(Vector3 position)
        {
            return new PositionScore
            {
                Position = position,
                AlliesInRange = 0,
                EnemiesInRange = 0,
                Score = 0f,
                Reason = "Default (no valid targets)"
            };
        }

        #endregion
    }
}
