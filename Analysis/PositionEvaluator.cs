using System.Collections.Generic;
// using System.Linq; // ★ v3.9.10: LINQ 완전 제거 → CollectionHelper.MaxBy
using Kingmaker.EntitySystem.Entities;
using Kingmaker.View.Covers;
using UnityEngine;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Analysis
{
    /// <summary>
    /// 위치 평가기 - 전술적 위치 점수 계산
    /// </summary>
    public static class PositionEvaluator
    {
        /// <summary>
        /// 위치 점수
        /// </summary>
        public class PositionScore
        {
            public Vector3 Position;
            public float TotalScore;
            public float CoverScore;
            public float DistanceScore;
            public float ThreatScore;
            public float LOSScore;
            public float TurnOrderThreatScore;  // ★ v3.22.4: 턴 순서 기반 위협 가중
            public bool IsReachable;
        }

        /// <summary>
        /// 원거리 공격 최적 위치 찾기
        /// </summary>
        public static Vector3? FindRangedAttackPosition(
            BaseUnitEntity unit,
            List<BaseUnitEntity> enemies,
            List<BaseUnitEntity> allies,
            float minSafeDistance,
            float moveRange)
        {
            if (unit == null || enemies == null || enemies.Count == 0)
                return null;

            // ★ v3.22.4: 턴 순서 캐시 갱신 (EvaluatePosition에서 턴 순서 기반 위협 가중에 사용)
            TargetScorer.RefreshTurnOrderCache(unit);

            var candidates = new List<PositionScore>();
            var currentPos = unit.Position;

            // 여러 방향으로 탐색
            for (int angle = 0; angle < 360; angle += 30)
            {
                for (float dist = 2f; dist <= moveRange; dist += 2f)
                {
                    float rad = angle * Mathf.Deg2Rad;
                    var direction = new Vector3(Mathf.Cos(rad), 0, Mathf.Sin(rad));
                    var candidatePos = currentPos + direction * dist;

                    // ★ v3.7.64: BattlefieldGrid Walkable 체크
                    if (BattlefieldGrid.Instance.IsValid && !BattlefieldGrid.Instance.IsWalkable(candidatePos))
                        continue;

                    var score = EvaluatePosition(candidatePos, unit, enemies, allies, minSafeDistance);
                    if (score.TotalScore > 0)
                    {
                        candidates.Add(score);
                    }
                }
            }

            if (candidates.Count == 0)
                return null;

            // ★ v3.9.10: O(n log n) sort → O(n) MaxBy
            var best = Core.CollectionHelper.MaxBy(candidates, c => c.TotalScore);
            Main.LogDebug($"[PositionEval] Best position: {best.Position} (score={best.TotalScore:F1})");

            return best.Position;
        }

        /// <summary>
        /// 특정 위치 평가
        /// ★ v3.7.66: IsReachable 실제 구현 - BattlefieldGrid 검증
        /// </summary>
        public static PositionScore EvaluatePosition(
            Vector3 position,
            BaseUnitEntity unit,
            List<BaseUnitEntity> enemies,
            List<BaseUnitEntity> allies,
            float minSafeDistance)
        {
            // ★ v3.7.66: 실제 도달 가능성 체크 (BattlefieldGrid 기반)
            bool isReachable = true;
            var grid = BattlefieldGrid.Instance;
            if (grid != null && grid.IsValid)
            {
                var node = grid.GetNode(position);
                isReachable = node != null && grid.CanUnitStandOn(unit, node);
            }

            var score = new PositionScore
            {
                Position = position,
                IsReachable = isReachable
            };

            // 도달 불가능하면 최저 점수 반환
            if (!isReachable)
            {
                score.TotalScore = float.MinValue;
                return score;
            }

            // 1. 적과의 최소 거리 (멀수록 좋음, 안전 거리 이상이어야 함)
            float nearestEnemyDist = float.MaxValue;
            int enemiesInLOS = 0;

            foreach (var enemy in enemies)
            {
                if (enemy == null || enemy.LifeState.IsDead) continue;

                float dist = Vector3.Distance(position, enemy.Position);
                if (dist < nearestEnemyDist) nearestEnemyDist = dist;

                // ★ v3.6.2: LOS 체크 - 타일 단위로 통일 (12타일 ≈ 16m)
                float distTiles = CombatAPI.MetersToTiles(dist);
                if (distTiles <= 12f) enemiesInLOS++;
            }

            // 안전 거리 미달이면 점수 대폭 감소
            if (nearestEnemyDist < minSafeDistance)
            {
                score.DistanceScore = -50f;
            }
            else
            {
                score.DistanceScore = Mathf.Min(30f, nearestEnemyDist * 2f);
            }

            // 2. LOS 점수 (공격 가능한 적이 있어야 함)
            if (enemiesInLOS > 0)
            {
                score.LOSScore = 20f;
            }
            else
            {
                score.LOSScore = -30f;  // 공격 불가 위치
            }

            // 3. 엄폐 점수
            try
            {
                var coverType = LosCalculations.GetCoverType(position);
                switch (coverType)
                {
                    case LosCalculations.CoverType.Full:
                        score.CoverScore = 30f;
                        break;
                    case LosCalculations.CoverType.Half:
                        score.CoverScore = 15f;
                        break;
                    case LosCalculations.CoverType.Invisible:
                        score.CoverScore = 40f;
                        break;
                    default:
                        score.CoverScore = 0f;
                        break;
                }
            }
            catch
            {
                score.CoverScore = 0f;
            }

            // 4. 이동 거리 패널티 (가까울수록 좋음)
            float moveDistance = Vector3.Distance(unit.Position, position);
            score.ThreatScore = -moveDistance * 0.5f;

            // 5. ★ v3.22.4: 턴 순서 기반 위협 가중
            // 곧 행동할 적 근처 = 더 위험 (이동+공격 가능), 이미 행동한 적 근처 = 덜 위험
            float turnOrderThreat = 0f;
            if (TargetScorer._cachedTurnOrder != null && TargetScorer._cachedTurnOrder.Count > 0)
            {
                foreach (var enemy in enemies)
                {
                    if (enemy == null || enemy.LifeState.IsDead) continue;
                    float dist = Vector3.Distance(position, enemy.Position);
                    float threatRadius = minSafeDistance * SC.PositionTurnOrderThreatRadiusMult;
                    if (dist > threatRadius) continue;

                    float proximityFactor = 1f - (dist / threatRadius);
                    int turnPos = TargetScorer._cachedTurnOrder.IndexOf(enemy);
                    if (turnPos >= 0 && turnPos <= 2)
                    {
                        // 곧 행동할 적: position 0=-15, 1=-10, 2=-5
                        float urgency = (3 - turnPos) * SC.PositionTurnOrderUrgencyRate;
                        turnOrderThreat -= urgency * proximityFactor;
                    }
                    else if (enemy.Initiative?.ActedThisRound == true)
                    {
                        // 이미 행동한 적: 소폭 안전 보너스
                        turnOrderThreat += SC.PositionTurnOrderActedBonus * proximityFactor;
                    }
                }
            }
            score.TurnOrderThreatScore = turnOrderThreat;

            // 총점
            score.TotalScore = score.CoverScore + score.DistanceScore +
                              score.LOSScore + score.ThreatScore + score.TurnOrderThreatScore;

            return score;
        }

        /// <summary>
        /// 후퇴 위치 찾기
        /// </summary>
        public static Vector3? FindRetreatPosition(
            BaseUnitEntity unit,
            List<BaseUnitEntity> enemies,
            float minSafeDistance,
            float moveRange)
        {
            if (unit == null || enemies == null || enemies.Count == 0)
                return null;

            // ★ v3.22.4: 턴 순서 캐시 갱신 (후퇴 위치 점수에 턴 순서 반영)
            TargetScorer.RefreshTurnOrderCache(unit);

            // 적들의 중심점
            Vector3 enemyCenter = Vector3.zero;
            int count = 0;
            foreach (var enemy in enemies)
            {
                if (enemy == null || enemy.LifeState.IsDead) continue;
                enemyCenter += enemy.Position;
                count++;
            }
            if (count == 0) return null;
            enemyCenter /= count;

            // 적 반대 방향
            var retreatDir = (unit.Position - enemyCenter).normalized;
            if (retreatDir == Vector3.zero)
                retreatDir = Vector3.back;

            // 후보 위치들
            var candidates = new List<(Vector3 pos, float score)>();

            for (int angle = -60; angle <= 60; angle += 20)
            {
                var rotatedDir = Quaternion.Euler(0, angle, 0) * retreatDir;

                for (float dist = 3f; dist <= moveRange; dist += 3f)
                {
                    var candidatePos = unit.Position + rotatedDir * dist;

                    // ★ v3.7.64: BattlefieldGrid Walkable 체크
                    if (BattlefieldGrid.Instance.IsValid && !BattlefieldGrid.Instance.IsWalkable(candidatePos))
                        continue;

                    // 모든 적과 안전 거리 유지 확인
                    float nearestDist = float.MaxValue;
                    foreach (var enemy in enemies)
                    {
                        if (enemy == null || enemy.LifeState.IsDead) continue;
                        float d = Vector3.Distance(candidatePos, enemy.Position);
                        if (d < nearestDist) nearestDist = d;
                    }

                    if (nearestDist >= minSafeDistance)
                    {
                        float score = nearestDist * 2f - dist * 0.5f;

                        // ★ v3.22.4: 턴 순서 기반 후퇴 위치 보정
                        if (TargetScorer._cachedTurnOrder != null && TargetScorer._cachedTurnOrder.Count > 0)
                        {
                            foreach (var enemy in enemies)
                            {
                                if (enemy == null || enemy.LifeState.IsDead) continue;
                                float eDist = Vector3.Distance(candidatePos, enemy.Position);
                                float threatRadius = minSafeDistance * SC.PositionTurnOrderThreatRadiusMult;
                                if (eDist > threatRadius) continue;

                                float proximityFactor = 1f - (eDist / threatRadius);
                                int turnPos = TargetScorer._cachedTurnOrder.IndexOf(enemy);
                                if (turnPos >= 0 && turnPos <= 2)
                                {
                                    float urgency = (3 - turnPos) * SC.PositionTurnOrderUrgencyRate;
                                    score -= urgency * proximityFactor;
                                }
                            }
                        }

                        candidates.Add((candidatePos, score));
                    }
                }
            }

            if (candidates.Count == 0)
                return null;

            // ★ v3.9.10: O(n log n) sort → O(n) inline max
            var best = candidates[0];
            for (int i = 1; i < candidates.Count; i++)
            {
                if (candidates[i].score > best.score)
                    best = candidates[i];
            }
            Main.LogDebug($"[PositionEval] Retreat to: {best.pos} (score={best.score:F1})");

            return best.pos;
        }
    }
}
