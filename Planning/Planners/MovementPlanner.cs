using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using UnityEngine;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// ★ v3.0.47: 이동 관련 계획 담당
    /// - 이동, GapCloser, 후퇴, 안전 이동
    /// </summary>
    public static class MovementPlanner
    {
        /// <summary>
        /// ★ 이동 또는 GapCloser 계획 (공통화)
        /// 모든 Role에서 사용 - 근접 캐릭터가 적에게 도달 못하면 GapCloser 사용
        /// </summary>
        public static PlannedAction PlanMoveOrGapCloser(Situation situation, ref float remainingAP, string roleName)
        {
            if (situation.HasHittableEnemies) return null;
            if (!situation.HasLivingEnemies) return null;
            if (situation.NearestEnemy == null) return null;

            // ★ 먼저 GapCloser 시도 (근접 선호이고 적이 멀 때)
            if (!situation.PrefersRanged && situation.NearestEnemyDistance > 3f)
            {
                var gapCloserAction = PlanGapCloser(situation, situation.NearestEnemy, ref remainingAP, roleName);
                if (gapCloserAction != null)
                {
                    Main.Log($"[{roleName}] GapCloser instead of move: {gapCloserAction.Ability?.Name}");
                    return gapCloserAction;
                }
            }

            // GapCloser 없으면 일반 이동
            return PlanMoveToEnemy(situation, roleName);
        }

        /// <summary>
        /// GapCloser 계획 (모든 Role 공통)
        /// </summary>
        public static PlannedAction PlanGapCloser(Situation situation, BaseUnitEntity target, ref float remainingAP, string roleName)
        {
            var gapClosers = situation.AvailableAttacks
                .Where(a => AbilityDatabase.IsGapCloser(a))
                .ToList();

            if (gapClosers.Count == 0) return null;

            var targetWrapper = new TargetWrapper(target);

            foreach (var gapCloser in gapClosers)
            {
                float cost = CombatAPI.GetAbilityAPCost(gapCloser);
                if (cost > remainingAP) continue;

                var info = AbilityDatabase.GetInfo(gapCloser);
                if (info?.HPThreshold > 0 && situation.HPPercent < info.HPThreshold)
                    continue;

                string reason;
                if (CombatAPI.CanUseAbilityOn(gapCloser, targetWrapper, out reason))
                {
                    remainingAP -= cost;
                    Main.Log($"[{roleName}] Gap closer: {gapCloser.Name} -> {target.CharacterName}");
                    return PlannedAction.Attack(gapCloser, target, $"Gap closer on {target.CharacterName}", cost);
                }
            }

            return null;
        }

        /// <summary>
        /// 적에게 이동
        /// </summary>
        public static PlannedAction PlanMoveToEnemy(Situation situation, string roleName)
        {
            bool isChaseMove = false;

            if (situation.HasMovedThisTurn)
            {
                if (situation.AllowPostAttackMove)
                {
                    Main.Log($"[{roleName}] PlanMoveToEnemy: Post-attack move allowed");
                    isChaseMove = true;
                }
                else if (situation.AllowChaseMove)
                {
                    Main.Log($"[{roleName}] PlanMoveToEnemy: Chase move allowed");
                    isChaseMove = true;
                }
                else
                {
                    Main.LogDebug($"[{roleName}] PlanMoveToEnemy: Already moved this turn, skipping");
                    return null;
                }
            }

            if (isChaseMove)
            {
                if (situation.CurrentMP <= 0)
                {
                    Main.LogDebug($"[{roleName}] PlanMoveToEnemy: Chase move blocked - no MP");
                    return null;
                }
            }
            else
            {
                if (!situation.CanMove) return null;
            }

            if (situation.NearestEnemy == null) return null;

            var unit = situation.Unit;
            var target = situation.NearestEnemy;

            if (situation.PrefersRanged)
            {
                // ★ v3.0.73: MovementAPI 기반 타일 스코어링 사용
                // 기존: 단순 벡터 계산 (적에게 3m 접근) → 위험!
                // 수정: 엄폐, 안전거리, LOS 등 종합 점수화

                float weaponRange = 15f;
                try
                {
                    var primaryHand = unit.Body?.PrimaryHand;
                    if (primaryHand?.HasWeapon == true && !primaryHand.Weapon.Blueprint.IsMelee)
                    {
                        int optRange = primaryHand.Weapon.AttackOptimalRange;
                        if (optRange > 0 && optRange < 10000)
                            weaponRange = optRange;
                        else
                        {
                            int attackRange = primaryHand.Weapon.AttackRange;
                            if (attackRange > 0 && attackRange < 10000)
                                weaponRange = attackRange;
                        }
                    }
                }
                catch { }

                var bestPosition = MovementAPI.FindRangedAttackPositionSync(
                    unit,
                    situation.Enemies,
                    weaponRange,
                    situation.MinSafeDistance
                );

                if (bestPosition == null)
                {
                    Main.LogDebug($"[{roleName}] PlanMoveToEnemy: No safe ranged position found");
                    return null;
                }

                // 현재 위치와 거의 같으면 이동 불필요
                float moveDistance = Vector3.Distance(unit.Position, bestPosition.Position);
                if (moveDistance < 1f)
                {
                    Main.LogDebug($"[{roleName}] PlanMoveToEnemy: Already at optimal position");
                    return null;
                }

                Main.Log($"[{roleName}] Safe ranged position: ({bestPosition.Position.x:F1},{bestPosition.Position.z:F1}) " +
                    $"score={bestPosition.TotalScore:F1}, cover={bestPosition.BestCover}");
                return PlannedAction.Move(bestPosition.Position, $"Safe attack position");
            }
            else
            {
                // ★ v3.0.74: 근접 캐릭터도 MovementAPI 기반 타일 스코어링 사용
                // 기존: target.Position (적의 점유된 타일) → 도달 불가
                // 수정: 적에게 인접한 공격 가능 타일 찾기

                float meleeRange = 2f;  // 기본 근접 사거리
                try
                {
                    var primaryHand = unit.Body?.PrimaryHand;
                    if (primaryHand?.HasWeapon == true && primaryHand.Weapon.Blueprint.IsMelee)
                    {
                        int attackRange = primaryHand.Weapon.AttackRange;
                        if (attackRange > 0 && attackRange < 100)
                            meleeRange = attackRange;
                    }
                }
                catch { }

                var bestPosition = MovementAPI.FindMeleeAttackPositionSync(
                    unit,
                    target,
                    meleeRange
                );

                if (bestPosition == null)
                {
                    // 폴백: 적 위치 직접 사용 (게임이 경로 처리)
                    Main.LogDebug($"[{roleName}] PlanMoveToEnemy: No melee position found, falling back to target position");
                    return PlannedAction.Move(target.Position, $"Approach {target.CharacterName}");
                }

                // 현재 위치와 거의 같으면 이동 불필요
                float moveDistance = Vector3.Distance(unit.Position, bestPosition.Position);
                if (moveDistance < 1f)
                {
                    Main.LogDebug($"[{roleName}] PlanMoveToEnemy: Already at melee position");
                    return null;
                }

                Main.Log($"[{roleName}] Melee attack position: ({bestPosition.Position.x:F1},{bestPosition.Position.z:F1}) " +
                    $"score={bestPosition.TotalScore:F1}");
                return PlannedAction.Move(bestPosition.Position, $"Melee position near {target.CharacterName}");
            }
        }

        /// <summary>
        /// 후퇴 (원거리 캐릭터가 적과 너무 가까울 때)
        /// ★ v3.0.61: 현재 위치가 이미 안전하면 이동 불필요
        /// </summary>
        public static PlannedAction PlanRetreat(Situation situation)
        {
            if (situation.HasMovedThisTurn) return null;
            if (!situation.CanMove) return null;

            var unit = situation.Unit;
            var nearestEnemy = situation.NearestEnemy;
            if (nearestEnemy == null) return null;

            // ★ v3.0.61: 현재 위치가 이미 안전 거리 이상이면 후퇴 불필요
            if (situation.NearestEnemyDistance >= situation.MinSafeDistance)
            {
                Main.LogDebug($"[MovementPlanner] {unit.CharacterName}: Already safe, no retreat needed");
                return null;
            }

            // ★ v3.0.60: MovementAPI 기반 실제 도달 가능한 타일 사용
            var retreatScore = MovementAPI.FindRetreatPositionSync(
                unit,
                situation.Enemies,
                situation.MinSafeDistance
            );

            if (retreatScore == null)
            {
                Main.LogDebug($"[MovementPlanner] {unit.CharacterName}: No reachable retreat position");
                return null;
            }

            return PlannedAction.Move(retreatScore.Position, $"Retreat from {nearestEnemy.CharacterName}");
        }

        /// <summary>
        /// ★ v3.0.60: 행동 완료 후 안전 후퇴 (MovementAPI 기반)
        /// ★ v3.0.61: 현재 위치가 이미 안전하면 이동 불필요
        /// </summary>
        public static PlannedAction PlanPostActionSafeRetreat(Situation situation)
        {
            if (!situation.CanMove) return null;
            if (situation.CurrentMP <= 0) return null;

            var unit = situation.Unit;
            var nearestEnemy = situation.NearestEnemy;
            if (nearestEnemy == null) return null;

            // ★ v3.0.61: 현재 위치가 이미 안전 거리 이상이면 이동 불필요
            if (situation.NearestEnemyDistance >= situation.MinSafeDistance)
            {
                Main.LogDebug($"[MovementPlanner] {unit.CharacterName}: Already safe (dist={situation.NearestEnemyDistance:F1}m >= {situation.MinSafeDistance}m), no retreat needed");
                return null;
            }

            // ★ v3.0.60: PathfindingService 기반 실제 도달 가능 위치
            var retreatScore = MovementAPI.FindRetreatPositionSync(
                unit,
                situation.Enemies,
                situation.MinSafeDistance
            );

            if (retreatScore == null)
            {
                Main.LogDebug($"[MovementPlanner] {unit.CharacterName}: No reachable safe retreat position");
                return null;
            }

            // ★ v3.0.61: 최적 위치가 현재 위치보다 충분히 좋은지 확인
            float currentDistToEnemy = situation.NearestEnemyDistance;
            float newDistToEnemy = Vector3.Distance(retreatScore.Position, nearestEnemy.Position);

            // 이동 후 거리가 현재보다 최소 2m 이상 멀어지지 않으면 이동 가치 없음
            if (newDistToEnemy < currentDistToEnemy + 2f)
            {
                Main.LogDebug($"[MovementPlanner] {unit.CharacterName}: Retreat not worth it (current={currentDistToEnemy:F1}m, after={newDistToEnemy:F1}m)");
                return null;
            }

            return PlannedAction.Move(retreatScore.Position, $"Safe retreat from {nearestEnemy.CharacterName}");
        }

        /// <summary>
        /// 후퇴 필요 여부 확인
        /// </summary>
        public static bool ShouldRetreat(Situation situation)
        {
            var rangePreference = situation.RangePreference;
            if (rangePreference != Settings.RangePreference.PreferRanged &&
                rangePreference != Settings.RangePreference.MaintainRange)
                return false;

            return situation.NearestEnemyDistance < situation.MinSafeDistance;
        }

        #region Helper Methods

        private static Vector3 CalculateAveragePosition(IEnumerable<BaseUnitEntity> units)
        {
            var list = units.ToList();
            if (list.Count == 0) return Vector3.zero;

            Vector3 sum = Vector3.zero;
            foreach (var unit in list)
            {
                sum += unit.Position;
            }
            return sum / list.Count;
        }

        #endregion
    }
}
