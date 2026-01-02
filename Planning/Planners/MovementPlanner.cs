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
using CompanionAI_v3.Settings;

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
        /// ★ v3.0.89: forceMove 파라미터 추가 - 공격 실패 시 이동 강제
        /// ★ v3.1.00: bypassCanMoveCheck 파라미터 추가 - MP 회복 예측 후 이동 허용
        /// ★ v3.1.01: predictedMP 파라미터 추가 - MovementAPI에 예측 MP 전달
        /// </summary>
        public static PlannedAction PlanMoveOrGapCloser(Situation situation, ref float remainingAP, string roleName, bool forceMove = false, bool bypassCanMoveCheck = false, float predictedMP = 0f)
        {
            // ★ v3.0.89: forceMove=true면 HasHittableEnemies 체크 스킵
            // 사용 사례: 원거리 fallback으로 Hittable=True인데 PreferMelee라서 공격 못함 → 이동 필요
            // ★ v3.1.29: 원거리 캐릭터가 위험 거리 내에 있으면 후퇴 이동 허용
            if (!forceMove && situation.HasHittableEnemies)
            {
                // 원거리가 위험하면 이동 허용 (공격 가능해도 후퇴 필요)
                bool isRangedInDanger = situation.PrefersRanged && situation.IsInDanger;
                if (!isRangedInDanger)
                    return null;
                Main.LogDebug($"[{roleName}] Ranged in danger - allowing movement despite hittable enemies");
            }
            if (!situation.HasLivingEnemies) return null;
            if (situation.NearestEnemy == null) return null;

            Main.LogDebug($"[{roleName}] PlanMoveOrGapCloser: forceMove={forceMove}, bypassCanMove={bypassCanMoveCheck}, predictedMP={predictedMP:F1}, PrefersRanged={situation.PrefersRanged}, Distance={situation.NearestEnemyDistance:F1}m");

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
            // ★ v3.1.01: bypassCanMoveCheck와 predictedMP 전달
            return PlanMoveToEnemy(situation, roleName, bypassCanMoveCheck, predictedMP);
        }

        /// <summary>
        /// GapCloser 계획 (모든 Role 공통)
        /// ★ v3.0.81: PointTarget 능력 지원 (Death from Above 등)
        /// ★ v3.0.87: 디버그 로깅 추가
        /// ★ v3.1.24: 첫 타겟 실패 시 다른 적 타겟도 시도
        /// </summary>
        public static PlannedAction PlanGapCloser(Situation situation, BaseUnitEntity target, ref float remainingAP, string roleName)
        {
            // ★ v3.0.87: 진입 로깅
            Main.LogDebug($"[{roleName}] PlanGapCloser: target={target?.CharacterName}, AP={remainingAP:F1}, attacks={situation.AvailableAttacks?.Count ?? 0}");

            var gapClosers = situation.AvailableAttacks
                .Where(a => AbilityDatabase.IsGapCloser(a))
                .ToList();

            if (gapClosers.Count == 0)
            {
                Main.LogDebug($"[{roleName}] PlanGapCloser: No GapClosers in AvailableAttacks");
                return null;
            }

            Main.LogDebug($"[{roleName}] PlanGapCloser: Found {gapClosers.Count} GapClosers: {string.Join(", ", gapClosers.Select(g => g.Name))}");

            foreach (var gapCloser in gapClosers)
            {
                float cost = CombatAPI.GetAbilityAPCost(gapCloser);
                if (cost > remainingAP)
                {
                    Main.LogDebug($"[{roleName}] PlanGapCloser: {gapCloser.Name} skipped - AP cost {cost:F1} > remaining {remainingAP:F1}");
                    continue;
                }

                var info = AbilityDatabase.GetInfo(gapCloser);
                if (info?.HPThreshold > 0 && situation.HPPercent < info.HPThreshold)
                {
                    Main.LogDebug($"[{roleName}] PlanGapCloser: {gapCloser.Name} skipped - HP {situation.HPPercent:F0}% < threshold {info.HPThreshold}%");
                    continue;
                }

                // ★ v3.0.81: PointTarget 능력 처리 (Death from Above 등)
                bool isPointTarget = info != null && (info.Flags & AbilityFlags.PointTarget) != 0;
                Main.LogDebug($"[{roleName}] PlanGapCloser: {gapCloser.Name} isPointTarget={isPointTarget}");

                // ★ v3.1.24: 첫 타겟 실패 시 다른 적들도 시도
                var targetsToTry = new List<BaseUnitEntity>();
                if (target != null) targetsToTry.Add(target);
                targetsToTry.AddRange(situation.Enemies.Where(e => e != target && e != null && e.IsConscious));

                foreach (var candidateTarget in targetsToTry)
                {
                    if (isPointTarget)
                    {
                        // ★ v3.1.28: 능력 정보 전달하여 범위 내 착지 위치 찾기
                        // ★ v3.4.02: P1 수정 - situation 전달하여 InfluenceMap 활용
                        var landingPosition = FindGapCloserLandingPosition(situation.Unit, candidateTarget, gapCloser, situation);
                        if (landingPosition.HasValue)
                        {
                            Main.LogDebug($"[{roleName}] PlanGapCloser: Landing position found at ({landingPosition.Value.x:F1},{landingPosition.Value.z:F1}) for {candidateTarget.CharacterName}");
                            var pointTarget = new TargetWrapper(landingPosition.Value);
                            string reason;
                            if (CombatAPI.CanUseAbilityOn(gapCloser, pointTarget, out reason))
                            {
                                remainingAP -= cost;
                                Main.Log($"[{roleName}] Position gap closer: {gapCloser.Name} -> near {candidateTarget.CharacterName} ({landingPosition.Value.x:F1},{landingPosition.Value.z:F1})");
                                return PlannedAction.PositionalAttack(gapCloser, landingPosition.Value, $"Jump to {candidateTarget.CharacterName}", cost);
                            }
                            else
                            {
                                Main.LogDebug($"[{roleName}] PlanGapCloser: {gapCloser.Name} -> {candidateTarget.CharacterName} failed: {reason}");
                            }
                        }
                        else
                        {
                            Main.LogDebug($"[{roleName}] PlanGapCloser: {gapCloser.Name} -> {candidateTarget.CharacterName} - no landing position");
                        }
                    }
                    else
                    {
                        // 일반 타겟 능력
                        var targetWrapper = new TargetWrapper(candidateTarget);
                        string reason;
                        if (CombatAPI.CanUseAbilityOn(gapCloser, targetWrapper, out reason))
                        {
                            remainingAP -= cost;
                            Main.Log($"[{roleName}] Gap closer: {gapCloser.Name} -> {candidateTarget.CharacterName}");
                            return PlannedAction.Attack(gapCloser, candidateTarget, $"Gap closer on {candidateTarget.CharacterName}", cost);
                        }
                        else
                        {
                            Main.LogDebug($"[{roleName}] PlanGapCloser: {gapCloser.Name} -> {candidateTarget.CharacterName} failed: {reason}");
                        }
                    }
                }
            }

            Main.LogDebug($"[{roleName}] PlanGapCloser: All GapClosers failed on all targets");
            return null;
        }

        /// <summary>
        /// ★ v3.0.81: 갭클로저 착지 위치 찾기
        /// ★ v3.1.28: 능력 범위 고려 - 스킬 범위 내에서만 착지 위치 선택
        /// ★ v3.4.02: P1 수정 - situation 파라미터 추가하여 InfluenceMap/PredictiveThreatMap 전달
        /// </summary>
        private static Vector3? FindGapCloserLandingPosition(BaseUnitEntity unit, BaseUnitEntity target, AbilityData gapCloserAbility, Situation situation = null)
        {
            // ★ v3.1.28: 능력 범위 확인
            float abilityRange = CombatAPI.GetAbilityRange(gapCloserAbility);
            Main.LogDebug($"[MovementPlanner] FindGapCloserLanding: ability={gapCloserAbility.Name}, range={abilityRange:F1}");

            // ★ v3.4.02: P1 수정 - InfluenceMap, role, PredictiveThreatMap 전달
            AIRole role = situation?.CharacterSettings?.Role ?? AIRole.Auto;
            var meleePosition = MovementAPI.FindMeleeAttackPositionSync(
                unit, target, 2f, 0f,
                situation?.InfluenceMap,
                role,
                situation?.PredictiveThreatMap);
            if (meleePosition != null)
            {
                // ★ v3.1.28: 착지 위치가 스킬 범위 내인지 확인
                float distToLanding = Vector3.Distance(unit.Position, meleePosition.Position);
                if (distToLanding <= abilityRange)
                {
                    Main.LogDebug($"[MovementPlanner] FindGapCloserLanding: melee position at dist={distToLanding:F1}m (within range)");
                    return meleePosition.Position;
                }
                else
                {
                    Main.LogDebug($"[MovementPlanner] FindGapCloserLanding: melee position at dist={distToLanding:F1}m exceeds ability range");
                }
            }

            // ★ v3.1.28: 폴백 - 적 방향으로 스킬 범위 내 위치 계산
            // 기존: 적 옆 위치 → 문제: 스킬 범위 무시
            // 수정: 캐스터에서 적 방향으로 범위의 90% 위치
            var direction = (target.Position - unit.Position).normalized;
            float targetDistance = Vector3.Distance(unit.Position, target.Position);

            if (targetDistance <= abilityRange)
            {
                // 적이 이미 범위 내 - 적 근처 착지
                var offset = (unit.Position - target.Position).normalized * 1.5f;
                var landingPos = target.Position + offset;
                Main.LogDebug($"[MovementPlanner] FindGapCloserLanding: target in range, landing near target");
                return landingPos;
            }
            else
            {
                // 적이 범위 밖 - 범위의 90% 지점으로
                var landingPos = unit.Position + direction * (abilityRange * 0.9f);
                Main.LogDebug($"[MovementPlanner] FindGapCloserLanding: target out of range ({targetDistance:F1}m), landing at {abilityRange * 0.9f:F1}m");
                return landingPos;
            }
        }

        /// <summary>
        /// 적에게 이동
        /// ★ v3.1.00: bypassCanMoveCheck 파라미터 추가
        /// ★ v3.1.01: predictedMP 파라미터 추가 - MovementAPI에 전달
        /// ★ v3.2.25: Role 추출하여 MovementAPI에 전달 - Frontline 기반 위치 점수
        /// </summary>
        public static PlannedAction PlanMoveToEnemy(Situation situation, string roleName, bool bypassCanMoveCheck = false, float predictedMP = 0f)
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
                // ★ v3.1.01: predictedMP가 있으면 chase move 허용
                if (situation.CurrentMP <= 0 && predictedMP <= 0)
                {
                    Main.LogDebug($"[{roleName}] PlanMoveToEnemy: Chase move blocked - no MP (predictedMP={predictedMP:F1})");
                    return null;
                }
            }
            else
            {
                // ★ v3.1.00: bypassCanMoveCheck=true면 CanMove 체크 스킵
                // MP 회복 능력(무모한 돌진 등) 계획 후 예측 MP로 이동 가능할 때 사용
                if (!bypassCanMoveCheck && !situation.CanMove)
                {
                    Main.LogDebug($"[{roleName}] PlanMoveToEnemy: CanMove=false, skipping");
                    return null;
                }
            }

            if (situation.NearestEnemy == null) return null;

            var unit = situation.Unit;
            var target = situation.NearestEnemy;

            // ★ v3.1.01: 실제 MP와 예측 MP 중 큰 값 사용
            float effectiveMP = Math.Max(situation.CurrentMP, predictedMP);

            // ★ v3.2.25: Role 추출 (Frontline 점수 적용용)
            AIRole role = situation.CharacterSettings?.Role ?? AIRole.Auto;
            Main.LogDebug($"[{roleName}] PlanMoveToEnemy: effectiveMP={effectiveMP:F1}, role={role}");

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

                // ★ v3.1.01: predictedMP 전달
                // ★ v3.2.00: influenceMap 전달
                // ★ v3.2.25: role 전달 (Frontline 점수)
                // ★ v3.4.00: predictiveMap 전달 (적 이동 예측)
                var bestPosition = MovementAPI.FindRangedAttackPositionSync(
                    unit,
                    situation.Enemies,
                    weaponRange,
                    situation.MinSafeDistance,
                    effectiveMP,
                    situation.InfluenceMap,
                    role,
                    situation.PredictiveThreatMap
                );

                if (bestPosition == null)
                {
                    Main.LogDebug($"[{roleName}] PlanMoveToEnemy: No safe ranged position found (effectiveMP={effectiveMP:F1})");
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

                // ★ v3.1.01: predictedMP 전달
                // ★ v3.2.00: influenceMap 전달
                // ★ v3.2.25: role 전달 (Frontline 점수)
                // ★ v3.4.00: predictiveMap 전달 (적 이동 예측)
                var bestPosition = MovementAPI.FindMeleeAttackPositionSync(
                    unit,
                    target,
                    meleeRange,
                    effectiveMP,
                    situation.InfluenceMap,
                    role,
                    situation.PredictiveThreatMap
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
        /// ★ v3.2.25: role 전달 (Frontline 점수)
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

            // ★ v3.2.25: Role 추출
            AIRole role = situation.CharacterSettings?.Role ?? AIRole.Auto;

            // ★ v3.0.60: MovementAPI 기반 실제 도달 가능한 타일 사용
            // ★ v3.2.00: influenceMap 전달
            // ★ v3.2.25: role 전달 (Frontline 점수)
            // ★ v3.4.00: predictiveMap 전달 (적 이동 예측)
            var retreatScore = MovementAPI.FindRetreatPositionSync(
                unit,
                situation.Enemies,
                situation.MinSafeDistance,
                0f,
                situation.InfluenceMap,
                role,
                situation.PredictiveThreatMap
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
        /// ★ v3.2.25: role 전달 (Frontline 점수)
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

            // ★ v3.2.25: Role 추출
            AIRole role = situation.CharacterSettings?.Role ?? AIRole.Auto;

            // ★ v3.0.60: PathfindingService 기반 실제 도달 가능 위치
            // ★ v3.2.00: influenceMap 전달
            // ★ v3.2.25: role 전달 (Frontline 점수)
            // ★ v3.4.00: predictiveMap 전달 (적 이동 예측)
            var retreatScore = MovementAPI.FindRetreatPositionSync(
                unit,
                situation.Enemies,
                situation.MinSafeDistance,
                0f,
                situation.InfluenceMap,
                role,
                situation.PredictiveThreatMap
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
            if (rangePreference != Settings.RangePreference.PreferRanged)
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
