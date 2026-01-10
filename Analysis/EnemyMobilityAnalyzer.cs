using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities;
using Pathfinding;
using UnityEngine;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Analysis
{
    /// <summary>
    /// ★ v3.4.00: 적 이동력 분석 데이터
    /// </summary>
    public class EnemyMobility
    {
        /// <summary>대상 적 유닛</summary>
        public BaseUnitEntity Enemy { get; set; }

        /// <summary>현재 이동력 (MP)</summary>
        public float MovementPoints { get; set; }

        /// <summary>도달 가능한 타일 위치들</summary>
        public List<Vector3> ReachableTiles { get; set; } = new List<Vector3>();

        /// <summary>최대 도달 거리 (MP + GapCloser 범위)</summary>
        public float MaxReach { get; set; }

        /// <summary>GapCloser 능력 보유 여부</summary>
        public bool HasGapCloser { get; set; }

        /// <summary>GapCloser 최대 범위</summary>
        public float GapCloserRange { get; set; }

        /// <summary>도달 가능한 타일 수</summary>
        public int TileCount => ReachableTiles?.Count ?? 0;
    }

    /// <summary>
    /// ★ v3.4.00: 적 이동력 분석기
    ///
    /// 적 유닛의 이동 가능 범위를 분석하여 다음 턴 위협 예측에 활용.
    /// PathfindingService를 사용하여 실제 도달 가능 타일 계산.
    /// 라운드별 캐싱으로 성능 최적화.
    /// </summary>
    public static class EnemyMobilityAnalyzer
    {
        #region Constants

        /// <summary>GapCloser 기본 범위 (미터)</summary>
        private const float DEFAULT_GAPCLOSER_RANGE = 8f;

        /// <summary>
        /// ★ v3.5.20: 설정에서 최대 분석 적 수 읽기 (기본값 8)
        /// </summary>
        private static int MaxEnemiesToAnalyze =>
            ModSettings.Instance?.MaxEnemiesToAnalyze ?? 8;

        /// <summary>
        /// ★ v3.5.20: 설정에서 적당 최대 타일 수 읽기 (기본값 100)
        /// </summary>
        private static int MaxTilesPerEnemy =>
            ModSettings.Instance?.MaxTilesPerEnemy ?? 100;

        #endregion

        #region Cache

        private static Dictionary<string, EnemyMobility> _mobilityCache = new Dictionary<string, EnemyMobility>();
        private static int _cacheRound = -1;

        #endregion

        #region Public API

        /// <summary>
        /// 단일 적 유닛의 이동력 분석
        /// </summary>
        public static EnemyMobility AnalyzeEnemy(BaseUnitEntity enemy)
        {
            if (enemy == null || enemy.LifeState.IsDead)
                return null;

            string unitId = GetUnitId(enemy);

            // 캐시 유효성 체크
            int currentRound = GetCurrentRound();
            if (_cacheRound != currentRound)
            {
                ClearCache();
                _cacheRound = currentRound;
            }

            // 캐시 히트
            if (_mobilityCache.TryGetValue(unitId, out var cached))
            {
                return cached;
            }

            try
            {
                var mobility = ComputeEnemyMobility(enemy);
                _mobilityCache[unitId] = mobility;
                return mobility;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[EnemyMobility] Error analyzing {enemy.CharacterName}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 모든 적 유닛의 이동력 분석
        /// </summary>
        public static List<EnemyMobility> AnalyzeAllEnemies(List<BaseUnitEntity> enemies)
        {
            if (enemies == null || enemies.Count == 0)
                return new List<EnemyMobility>();

            var results = new List<EnemyMobility>();

            // ★ v3.5.20: 성능을 위해 최대 수 제한 (설정에서 읽음)
            var enemiesToAnalyze = enemies
                .Where(e => e != null && !e.LifeState.IsDead)
                .Take(MaxEnemiesToAnalyze)
                .ToList();

            foreach (var enemy in enemiesToAnalyze)
            {
                var mobility = AnalyzeEnemy(enemy);
                if (mobility != null)
                {
                    results.Add(mobility);
                }
            }

            Main.LogDebug($"[EnemyMobility] Analyzed {results.Count}/{enemies.Count} enemies");
            return results;
        }

        /// <summary>
        /// 캐시 클리어
        /// </summary>
        public static void ClearCache()
        {
            _mobilityCache.Clear();
            _cacheRound = -1;
            Main.LogDebug("[EnemyMobility] Cache cleared");
        }

        /// <summary>
        /// 특정 위치가 적의 도달 범위 내인지 확인
        /// </summary>
        /// <param name="position">확인할 위치</param>
        /// <param name="mobility">적 이동력 정보</param>
        /// <param name="tolerance">허용 오차 (미터 단위, Vector3.Distance와 동일)</param>
        public static bool IsPositionReachableByEnemy(Vector3 position, EnemyMobility mobility, float tolerance = 2f)
        {
            if (mobility == null || mobility.ReachableTiles == null)
                return false;

            foreach (var tile in mobility.ReachableTiles)
            {
                if (Vector3.Distance(position, tile) <= tolerance)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 특정 위치가 적의 GapCloser 범위 내인지 확인
        /// </summary>
        public static bool IsPositionInGapCloserRange(Vector3 position, EnemyMobility mobility)
        {
            if (mobility == null || !mobility.HasGapCloser)
                return false;

            float distance = Vector3.Distance(position, mobility.Enemy.Position);
            return distance <= mobility.GapCloserRange;
        }

        #endregion

        #region Core Computation

        private static EnemyMobility ComputeEnemyMobility(BaseUnitEntity enemy)
        {
            var mobility = new EnemyMobility
            {
                Enemy = enemy,
                MovementPoints = GetEnemyMP(enemy)
            };

            // 1. GapCloser 능력 분석
            AnalyzeGapClosers(enemy, mobility);

            // 2. 도달 가능 타일 계산
            ComputeReachableTiles(enemy, mobility);

            // 3. 최대 도달 거리 계산
            mobility.MaxReach = CalculateMaxReach(mobility);

            Main.LogDebug($"[EnemyMobility] {enemy.CharacterName}: MP={mobility.MovementPoints:F1}, " +
                          $"Tiles={mobility.TileCount}, MaxReach={mobility.MaxReach:F1}m, " +
                          $"GapCloser={mobility.HasGapCloser}");

            return mobility;
        }

        private static void AnalyzeGapClosers(BaseUnitEntity enemy, EnemyMobility mobility)
        {
            try
            {
                // ★ v3.5.00: GetAbilities → GetAvailableAbilities
                var abilities = CombatAPI.GetAvailableAbilities(enemy);
                if (abilities == null) return;

                foreach (var ability in abilities)
                {
                    if (ability == null) continue;

                    // AbilityDatabase에서 GapCloser 확인
                    var timing = AbilityDatabase.GetTiming(ability);
                    if (timing == AbilityTiming.GapCloser)
                    {
                        mobility.HasGapCloser = true;

                        // 능력 범위 추출
                        float range = GetAbilityRange(ability);
                        if (range > mobility.GapCloserRange)
                        {
                            mobility.GapCloserRange = range;
                        }
                    }

                    // 블루프린트에서 돌진/도약 능력 휴리스틱 감지
                    if (IsGapCloserHeuristic(ability))
                    {
                        mobility.HasGapCloser = true;
                        float range = GetAbilityRange(ability);
                        if (range > mobility.GapCloserRange)
                        {
                            mobility.GapCloserRange = range;
                        }
                    }
                }

                // GapCloser 발견되었으나 범위 불명확하면 기본값 사용
                if (mobility.HasGapCloser && mobility.GapCloserRange <= 0)
                {
                    mobility.GapCloserRange = DEFAULT_GAPCLOSER_RANGE;
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[EnemyMobility] GapCloser analysis error: {ex.Message}");
            }
        }

        private static void ComputeReachableTiles(BaseUnitEntity enemy, EnemyMobility mobility)
        {
            if (mobility.MovementPoints <= 0)
            {
                // MP 없으면 현재 위치만
                mobility.ReachableTiles.Add(enemy.Position);
                return;
            }

            try
            {
                // PathfindingService를 사용하여 도달 가능 타일 계산
                var tiles = MovementAPI.FindAllReachableTilesSync(enemy, mobility.MovementPoints);

                if (tiles != null && tiles.Count > 0)
                {
                    // 위치 추출 (성능을 위해 최대 수 제한)
                    // ★ v3.5.20: 설정에서 타일 제한 읽음
                    int count = 0;
                    int maxTiles = MaxTilesPerEnemy;
                    foreach (var kvp in tiles)
                    {
                        if (count >= maxTiles) break;

                        var node = kvp.Key as CustomGridNodeBase;
                        if (node != null)
                        {
                            mobility.ReachableTiles.Add(node.Vector3Position);
                            count++;
                        }
                    }
                }
                else
                {
                    // 폴백: 현재 위치 + MP 기반 원형 추정
                    mobility.ReachableTiles.Add(enemy.Position);
                    AddEstimatedTiles(enemy.Position, mobility.MovementPoints, mobility.ReachableTiles);
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[EnemyMobility] Reachable tiles error: {ex.Message}");
                mobility.ReachableTiles.Add(enemy.Position);
            }
        }

        /// <summary>
        /// MP 기반 원형 추정 타일 추가 (PathfindingService 실패 시 폴백)
        /// </summary>
        private static void AddEstimatedTiles(Vector3 center, float mp, List<Vector3> tiles)
        {
            // 간단한 원형 샘플링 (8방향)
            float[] angles = { 0, 45, 90, 135, 180, 225, 270, 315 };

            foreach (float angle in angles)
            {
                float rad = angle * Mathf.Deg2Rad;
                Vector3 offset = new Vector3(Mathf.Cos(rad), 0, Mathf.Sin(rad)) * mp;
                tiles.Add(center + offset);
            }
        }

        private static float CalculateMaxReach(EnemyMobility mobility)
        {
            float maxDist = 0f;
            Vector3 origin = mobility.Enemy.Position;

            foreach (var tile in mobility.ReachableTiles)
            {
                float dist = Vector3.Distance(origin, tile);
                if (dist > maxDist)
                    maxDist = dist;
            }

            // GapCloser 범위 추가
            if (mobility.HasGapCloser)
            {
                maxDist += mobility.GapCloserRange;
            }

            return maxDist;
        }

        #endregion

        #region Helper Methods

        private static string GetUnitId(BaseUnitEntity unit)
        {
            return unit?.UniqueId ?? unit?.CharacterName ?? "unknown";
        }

        private static int GetCurrentRound()
        {
            try
            {
                // ★ v3.5.00: 정확한 게임 API 사용 (TurnState.cs 참조)
                return Kingmaker.Game.Instance?.TurnController?.CombatRound ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private static float GetEnemyMP(BaseUnitEntity enemy)
        {
            try
            {
                // ActionPointsBlue = MP (이동력)
                return enemy?.CombatState?.ActionPointsBlue ?? 0f;
            }
            catch
            {
                return 0f;
            }
        }

        private static float GetAbilityRange(AbilityData ability)
        {
            try
            {
                // ★ v3.5.00: CombatAPI.GetAbilityRange() 사용 (Range는 enum이라 직접 접근 불가)
                if (ability == null) return DEFAULT_GAPCLOSER_RANGE;
                float range = CombatAPI.GetAbilityRange(ability);
                return range > 0 ? range : DEFAULT_GAPCLOSER_RANGE;
            }
            catch
            {
                return DEFAULT_GAPCLOSER_RANGE;
            }
        }

        /// <summary>
        /// ★ v3.5.75: GapCloser 능력 감지 (v3.5.73 AttackCategory API 활용)
        /// </summary>
        private static bool IsGapCloserHeuristic(AbilityData ability)
        {
            try
            {
                if (ability == null) return false;

                // ★ v3.5.75: v3.5.73 AttackCategory API 활용
                if (CombatAPI.GetAttackCategory(ability) == AttackCategory.GapCloser)
                    return true;

                // 게임 네이티브 API
                if (ability.IsCharge)
                    return true;

                // Component 기반 휴리스틱 (이동 강제 능력) - 문자열 기반 name 체크 제거
                var blueprint = ability.Blueprint;
                if (blueprint == null) return false;

                var components = blueprint.ComponentsArray;
                if (components != null)
                {
                    foreach (var component in components)
                    {
                        string typeName = component?.GetType()?.Name ?? "";
                        if (typeName.Contains("Teleport") ||
                            typeName.Contains("Charge") ||
                            typeName.Contains("MoveToTarget"))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        #endregion
    }
}
