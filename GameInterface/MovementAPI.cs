using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.AI;
using Kingmaker.AI.AreaScanning;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.View.Covers;
using Pathfinding;
using UnityEngine;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// 이동 API - 위치 평가 및 최적 위치 찾기
    /// </summary>
    public static class MovementAPI
    {
        #region Position Scoring

        public class PositionScore
        {
            public CustomGridNodeBase Node { get; set; }
            public Vector3 Position => Node?.Vector3Position ?? Vector3.zero;

            public float CoverScore { get; set; }
            public float DistanceScore { get; set; }
            public float ThreatScore { get; set; }
            public float AttackScore { get; set; }
            public float APCost { get; set; }

            public float TotalScore => CoverScore + DistanceScore - ThreatScore + AttackScore;

            public bool CanStand { get; set; }
            public bool HasLosToEnemy { get; set; }
            public int ProvokedAttacks { get; set; }
            public LosCalculations.CoverType BestCover { get; set; }

            public override string ToString() =>
                $"Pos({Position.x:F1},{Position.z:F1}) Score={TotalScore:F1}";
        }

        public enum MovementGoal
        {
            FindCover,
            MaintainDistance,
            ApproachEnemy,
            AttackPosition,
            Retreat,
            RangedAttackPosition
        }

        #endregion

        #region Tile Discovery

        public static Dictionary<GraphNode, WarhammerPathPlayerCell> FindAllReachableTilesSync(
            BaseUnitEntity unit,
            float? maxAP = null)
        {
            if (unit == null) return new Dictionary<GraphNode, WarhammerPathPlayerCell>();

            try
            {
                float ap = maxAP ?? unit.CombatState?.ActionPointsBlue ?? 0f;
                if (ap <= 0) return new Dictionary<GraphNode, WarhammerPathPlayerCell>();

                var agent = unit.View?.MovementAgent;
                if (agent == null) return new Dictionary<GraphNode, WarhammerPathPlayerCell>();

                var tiles = PathfindingService.Instance.FindAllReachableTiles_Blocking(
                    agent,
                    unit.Position,
                    ap,
                    ignoreThreateningAreaCost: false
                );

                Main.LogDebug($"[MovementAPI] {unit.CharacterName}: Found {tiles?.Count ?? 0} reachable tiles");
                return tiles ?? new Dictionary<GraphNode, WarhammerPathPlayerCell>();
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[MovementAPI] FindAllReachableTilesSync error: {ex.Message}");
                return new Dictionary<GraphNode, WarhammerPathPlayerCell>();
            }
        }

        #endregion

        #region Threat Detection

        /// <summary>
        /// ★ v3.0.62: AoE/함정 위협 점수 계산
        /// AiBrainHelper.TryFindThreats를 사용하여 해당 노드의 위협 평가
        /// </summary>
        public static float CalculateThreatScore(BaseUnitEntity unit, CustomGridNodeBase node)
        {
            if (unit == null || node == null) return 0f;

            float threatScore = 0f;

            try
            {
                var threats = AiBrainHelper.TryFindThreats(unit, node);
                if (threats == null) return 0f;

                // AoO 위협 (기습공격 유발)
                if (threats.aooUnits != null && threats.aooUnits.Count > 0)
                {
                    threatScore += threats.aooUnits.Count * 20f;
                    Main.LogDebug($"[MovementAPI] Node has {threats.aooUnits.Count} AoO threats");
                }

                // Overwatch 위협 (경계사격)
                if (threats.overwatchUnits != null && threats.overwatchUnits.Count > 0)
                {
                    threatScore += threats.overwatchUnits.Count * 25f;
                    Main.LogDebug($"[MovementAPI] Node has {threats.overwatchUnits.Count} Overwatch threats");
                }

                // AoE 위협 (화염, 독가스 등)
                if (threats.aes != null && threats.aes.Count > 0)
                {
                    threatScore += threats.aes.Count * 30f;
                    Main.LogDebug($"[MovementAPI] Node has {threats.aes.Count} AoE threats");
                }

                // 이동 시 데미지 AoE (화염 지대 등)
                if (threats.dmgOnMoveAes != null && threats.dmgOnMoveAes.Count > 0)
                {
                    threatScore += threats.dmgOnMoveAes.Count * 50f;
                    Main.LogDebug($"[MovementAPI] Node has {threats.dmgOnMoveAes.Count} damage-on-move AoE");
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[MovementAPI] CalculateThreatScore error: {ex.Message}");
            }

            return threatScore;
        }

        #endregion

        #region Position Evaluation

        public static List<PositionScore> EvaluateAllPositions(
            BaseUnitEntity unit,
            Dictionary<GraphNode, WarhammerPathAiCell> reachableTiles,
            List<BaseUnitEntity> enemies,
            MovementGoal goal,
            float targetDistance = 10f,
            float minSafeDistance = 5f)
        {
            var scores = new List<PositionScore>();
            if (unit == null || reachableTiles == null || reachableTiles.Count == 0)
                return scores;

            foreach (var kvp in reachableTiles)
            {
                var node = kvp.Key as CustomGridNodeBase;
                var cell = kvp.Value;

                if (node == null || !cell.IsCanStand)
                    continue;

                var score = EvaluatePosition(unit, node, cell, enemies, goal, targetDistance, minSafeDistance);
                scores.Add(score);
            }

            return scores.OrderByDescending(s => s.TotalScore).ToList();
        }

        public static PositionScore EvaluatePosition(
            BaseUnitEntity unit,
            CustomGridNodeBase node,
            WarhammerPathAiCell cell,
            List<BaseUnitEntity> enemies,
            MovementGoal goal,
            float targetDistance = 10f,
            float minSafeDistance = 5f)
        {
            var score = new PositionScore
            {
                Node = node,
                CanStand = cell.IsCanStand,
                APCost = cell.Length,
                ProvokedAttacks = cell.ProvokedAttacks,
                BestCover = LosCalculations.CoverType.None
            };

            if (enemies == null || enemies.Count == 0)
                return score;

            float totalCoverScore = 0f;
            float nearestEnemyDist = float.MaxValue;
            bool hasAnyLos = false;

            foreach (var enemy in enemies)
            {
                if (enemy == null || enemy.LifeState.IsDead) continue;

                var enemyNode = enemy.Position.GetNearestNodeXZ();
                if (enemyNode == null) continue;

                float dist = Vector3.Distance(node.Vector3Position, enemy.Position);
                if (dist < nearestEnemyDist) nearestEnemyDist = dist;

                try
                {
                    var los = LosCalculations.GetWarhammerLos(
                        enemyNode,
                        enemy.SizeRect,
                        node,
                        unit.SizeRect
                    );

                    if (los.CoverType != LosCalculations.CoverType.Invisible) hasAnyLos = true;

                    switch (los.CoverType)
                    {
                        case LosCalculations.CoverType.Invisible:
                            totalCoverScore += 40f;
                            break;
                        case LosCalculations.CoverType.Full:
                            totalCoverScore += 30f;
                            break;
                        case LosCalculations.CoverType.Half:
                            totalCoverScore += 15f;
                            break;
                    }

                    if (los.CoverType > score.BestCover)
                        score.BestCover = los.CoverType;
                }
                catch { }
            }

            score.CoverScore = totalCoverScore / Math.Max(1, enemies.Count);
            score.HasLosToEnemy = hasAnyLos;

            switch (goal)
            {
                case MovementGoal.FindCover:
                case MovementGoal.Retreat:
                    score.DistanceScore = Math.Min(30f, nearestEnemyDist * 2f);
                    break;

                case MovementGoal.MaintainDistance:
                    float distDiff = Math.Abs(nearestEnemyDist - targetDistance);
                    score.DistanceScore = Math.Max(0f, 20f - distDiff * 2f);
                    break;

                case MovementGoal.ApproachEnemy:
                    score.DistanceScore = Math.Max(0f, 30f - nearestEnemyDist * 2f);
                    break;

                case MovementGoal.AttackPosition:
                    if (nearestEnemyDist <= targetDistance && nearestEnemyDist >= 3f)
                        score.DistanceScore = 25f;
                    else if (nearestEnemyDist <= targetDistance)
                        score.DistanceScore = 15f;
                    else
                        score.DistanceScore = 0f;
                    break;

                case MovementGoal.RangedAttackPosition:
                    float weaponRange = targetDistance;

                    if (nearestEnemyDist < minSafeDistance)
                    {
                        score.DistanceScore = -50f + nearestEnemyDist * 5f;
                    }
                    else if (nearestEnemyDist <= weaponRange)
                    {
                        // ★ v3.0.46: Division by Zero 방지
                        float denominator = weaponRange - minSafeDistance;
                        float distRatio = denominator > 0.1f
                            ? (nearestEnemyDist - minSafeDistance) / denominator
                            : 0.5f;
                        score.DistanceScore = 20f + distRatio * 10f;
                    }
                    else
                    {
                        score.DistanceScore = Math.Max(0f, 10f - (nearestEnemyDist - weaponRange) * 2f);
                    }
                    break;
            }

            score.ThreatScore = cell.ProvokedAttacks * 20f + cell.EnteredAoE * 15f;

            if (hasAnyLos && nearestEnemyDist <= targetDistance)
                score.AttackScore = 20f;

            return score;
        }

        #endregion

        #region Best Position Finding

        /// <summary>
        /// ★ v3.0.62: AoE/위협 점수 통합
        /// </summary>
        public static PositionScore FindRangedAttackPositionSync(
            BaseUnitEntity unit,
            List<BaseUnitEntity> enemies,
            float weaponRange = 15f,
            float minSafeDistance = 5f)
        {
            var tiles = FindAllReachableTilesSync(unit);
            if (tiles == null || tiles.Count == 0)
            {
                Main.LogDebug($"[MovementAPI] {unit.CharacterName}: No reachable tiles");
                return null;
            }

            var aiCells = new Dictionary<GraphNode, WarhammerPathAiCell>();
            foreach (var kvp in tiles)
            {
                var playerCell = kvp.Value;
                var node = playerCell.Node as CustomGridNodeBase;
                if (node == null) continue;

                var aiCell = new WarhammerPathAiCell(
                    node.Vector3Position,
                    0,
                    playerCell.Length,
                    node,
                    null,
                    playerCell.IsCanStand,
                    0,
                    0,
                    0
                );
                aiCells[kvp.Key] = aiCell;
            }

            var scores = EvaluateAllPositions(unit, aiCells, enemies, MovementGoal.RangedAttackPosition, weaponRange, minSafeDistance);

            // ★ v3.0.62: 위협 점수 추가 (AoE, AoO, Overwatch)
            foreach (var score in scores)
            {
                score.ThreatScore += CalculateThreatScore(unit, score.Node);
            }

            // 위협 점수가 반영된 후 재정렬
            scores = scores.OrderByDescending(s => s.TotalScore).ToList();

            var best = scores.FirstOrDefault(s =>
                s.CanStand &&
                s.HasLosToEnemy &&
                s.DistanceScore >= 20f);

            if (best == null)
            {
                best = scores.FirstOrDefault(s =>
                    s.CanStand &&
                    s.HasLosToEnemy &&
                    s.DistanceScore > 0f);
            }

            if (best == null)
            {
                best = scores.FirstOrDefault(s =>
                    s.CanStand &&
                    s.HasLosToEnemy);
            }

            if (best != null)
            {
                Main.Log($"[MovementAPI] {unit.CharacterName}: Ranged position at ({best.Position.x:F1},{best.Position.z:F1}) - {best}");
            }

            return best;
        }

        /// <summary>
        /// ★ v3.0.74: 근접 공격 위치 찾기 (실제 도달 가능한 타일 기반)
        /// 적의 타일이 아닌, 적에게 인접한 공격 가능 위치 반환
        /// </summary>
        public static PositionScore FindMeleeAttackPositionSync(
            BaseUnitEntity unit,
            BaseUnitEntity target,
            float meleeRange = 2f)
        {
            if (unit == null || target == null) return null;

            var tiles = FindAllReachableTilesSync(unit);
            if (tiles == null || tiles.Count == 0)
            {
                Main.LogDebug($"[MovementAPI] {unit.CharacterName}: No reachable tiles for melee approach");
                return null;
            }

            var targetPos = target.Position;
            var unitPos = unit.Position;

            // 적 뒤쪽 방향 (플랭킹 보너스용)
            var flankDir = (targetPos - unitPos).normalized;

            var candidates = new List<PositionScore>();

            foreach (var kvp in tiles)
            {
                var playerCell = kvp.Value;
                var node = playerCell.Node as CustomGridNodeBase;
                if (node == null || !playerCell.IsCanStand) continue;

                var pos = node.Vector3Position;

                // 적과의 거리 계산
                float distToTarget = Vector3.Distance(pos, targetPos);

                // 근접 공격 사거리 내 타일만 선택 (보통 1-2m)
                // 적의 크기 고려 (대형 적은 더 넓은 공격 범위)
                float effectiveRange = meleeRange + (target.SizeRect.Width * 0.5f);
                if (distToTarget > effectiveRange) continue;

                // 적 위치와 거의 동일하면 스킵 (점유 타일)
                if (distToTarget < 0.5f) continue;

                // 점수 계산
                float score = 100f;  // 기본 점수

                // 1. 이동 거리 점수 (가까울수록 좋음 - MP 절약)
                float distFromUnit = Vector3.Distance(pos, unitPos);
                score -= distFromUnit * 2f;

                // 2. 플랭킹 보너스 (적 뒤쪽에서 공격)
                var attackDir = (targetPos - pos).normalized;
                float flankDot = Vector3.Dot(attackDir, flankDir);
                if (flankDot > 0.5f)  // 뒤쪽에서 공격
                    score += 15f;

                // 3. AoE/AoO 위협 점수 계산 (CalculateThreatScore가 AoO도 포함)
                float threatScore = CalculateThreatScore(unit, node);
                score -= threatScore;

                var posScore = new PositionScore
                {
                    Node = node,
                    CanStand = true,
                    APCost = playerCell.Length,
                    DistanceScore = score,
                    ThreatScore = threatScore
                };

                candidates.Add(posScore);
            }

            if (candidates.Count == 0)
            {
                Main.LogDebug($"[MovementAPI] {unit.CharacterName}: No melee attack positions within range");
                return null;
            }

            var best = candidates.OrderByDescending(c => c.TotalScore).First();
            float finalDist = Vector3.Distance(best.Position, targetPos);
            Main.Log($"[MovementAPI] {unit.CharacterName}: Melee position at ({best.Position.x:F1},{best.Position.z:F1}) " +
                $"dist={finalDist:F1}m, score={best.TotalScore:F1}");

            return best;
        }

        /// <summary>
        /// ★ v3.0.60: 후퇴 위치 찾기 (실제 도달 가능한 타일 기반)
        /// ★ v3.0.62: AoE/위협 점수 통합
        ///
        /// PositionEvaluator의 단순 Vector3 계산 대신 PathfindingService 사용
        /// </summary>
        public static PositionScore FindRetreatPositionSync(
            BaseUnitEntity unit,
            List<BaseUnitEntity> enemies,
            float minSafeDistance = 8f)
        {
            if (unit == null || enemies == null || enemies.Count == 0)
                return null;

            var tiles = FindAllReachableTilesSync(unit);
            if (tiles == null || tiles.Count == 0)
            {
                Main.LogDebug($"[MovementAPI] {unit.CharacterName}: No reachable tiles for retreat");
                return null;
            }

            // 적들의 중심점 계산
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

            // 후퇴 방향 (적 반대)
            var retreatDir = (unit.Position - enemyCenter).normalized;

            var candidates = new List<PositionScore>();

            foreach (var kvp in tiles)
            {
                var playerCell = kvp.Value;
                var node = playerCell.Node as CustomGridNodeBase;
                if (node == null || !playerCell.IsCanStand) continue;

                var pos = node.Vector3Position;

                // 모든 적과의 최소 거리 계산
                float nearestEnemyDist = float.MaxValue;
                foreach (var enemy in enemies)
                {
                    if (enemy == null || enemy.LifeState.IsDead) continue;
                    float d = Vector3.Distance(pos, enemy.Position);
                    if (d < nearestEnemyDist) nearestEnemyDist = d;
                }

                // 안전 거리 미달이면 스킵
                if (nearestEnemyDist < minSafeDistance) continue;

                // 점수 계산: 적에게서 멀수록 + 후퇴 방향 보너스
                float distScore = nearestEnemyDist * 2f;

                // 후퇴 방향과의 일치도
                var moveDir = (pos - unit.Position).normalized;
                float directionBonus = Vector3.Dot(moveDir, retreatDir) * 10f;

                // 이동 거리 패널티 (너무 멀면 MP 낭비)
                float moveDist = Vector3.Distance(unit.Position, pos);
                float moveDistPenalty = moveDist * 0.5f;

                var score = new PositionScore
                {
                    Node = node,
                    CanStand = true,
                    APCost = playerCell.Length,
                    DistanceScore = distScore + directionBonus - moveDistPenalty
                };

                // ★ v3.0.62: AoE/위협 점수 계산
                score.ThreatScore = CalculateThreatScore(unit, node);

                // 엄폐 점수 추가
                try
                {
                    var coverType = LosCalculations.GetCoverType(pos);
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
                    }
                }
                catch { }

                candidates.Add(score);
            }

            if (candidates.Count == 0)
            {
                Main.LogDebug($"[MovementAPI] {unit.CharacterName}: No safe retreat positions");
                return null;
            }

            var best = candidates.OrderByDescending(c => c.TotalScore).First();
            Main.LogDebug($"[MovementAPI] {unit.CharacterName}: Retreat to ({best.Position.x:F1},{best.Position.z:F1}) score={best.TotalScore:F1}");

            return best;
        }

        #endregion
    }
}
