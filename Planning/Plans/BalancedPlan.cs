using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;

namespace CompanionAI_v3.Planning.Plans
{
    /// <summary>
    /// ★ v3.0.47: Balanced 전략
    /// ★ v3.0.57: SequenceOptimizer 기반 행동 조합 점수화 적용 (원거리 선호 시)
    /// 상황 적응형 - 모든 기능 통합, GapCloser 지원
    /// </summary>
    public class BalancedPlan : BasePlan
    {
        protected override string RoleName => "Balanced";

        public override TurnPlan CreatePlan(Situation situation, TurnState turnState)
        {
            var actions = new List<PlannedAction>();
            // ★ v3.0.68: 게임 AP 직접 사용
            float remainingAP = situation.CurrentAP;
            // ★ v3.0.55: MP 추적 - AP처럼 계획 단계에서 MP도 추적
            float remainingMP = situation.CurrentMP;
            string reasoning;

            float reservedAP = CalculateReservedAPForPostMoveAttack(situation);
            if (reservedAP > 0)
            {
                Main.Log($"[Balanced] Reserving {reservedAP:F1} AP for post-move attack");
            }

            // ★ v3.0.57: Phase 0 제거 - SequenceOptimizer가 이동+공격 조합을 자동 비교
            // 원거리 선호 캐릭터는 Phase 6에서 최적 시퀀스 선택

            // ========== Phase 1: 긴급 상황 ==========
            if (situation.IsHPCritical)
            {
                var healAction = PlanEmergencyHeal(situation, ref remainingAP);
                if (healAction != null)
                {
                    actions.Add(healAction);
                    reasoning = "Emergency heal - HP critical";
                    return new TurnPlan(actions, TurnPriority.Emergency, reasoning);
                }
            }

            // ========== Phase 2: 재장전 ==========
            if (situation.NeedsReload && situation.ReloadAbility != null)
            {
                var reloadAction = PlanReload(situation, ref remainingAP);
                if (reloadAction != null)
                {
                    actions.Add(reloadAction);
                }
            }

            // ★ v3.0.57: Phase 3 제거 - 이동은 Phase 6에서 SequenceOptimizer가 결정 (원거리 선호 시)
            // 근접 선호 캐릭터는 기존 로직 유지
            if (!situation.PrefersRanged && situation.IsInDanger && situation.CanMove)
            {
                // 근접 캐릭터는 위험해도 전진 유지 (후퇴 안 함)
            }

            // ========== Phase 4: 버프 ==========
            if (!situation.HasBuffedThisTurn && !situation.HasPerformedFirstAction)
            {
                var buffAction = PlanBuffWithReservation(situation, ref remainingAP, reservedAP);
                if (buffAction != null)
                {
                    actions.Add(buffAction);
                }
            }

            // Phase 4.5: 특수 능력 (DoT 강화, 연쇄 효과)
            var specialAction = PlanSpecialAbility(situation, ref remainingAP);
            if (specialAction != null)
            {
                actions.Add(specialAction);
            }

            // Phase 4.6: 아군 힐 (HP 낮은 아군)
            if (situation.MostWoundedAlly != null)
            {
                float allyHP = CombatAPI.GetHPPercent(situation.MostWoundedAlly);
                if (allyHP < 50f)
                {
                    var allyHealAction = PlanAllyHeal(situation, situation.MostWoundedAlly, ref remainingAP);
                    if (allyHealAction != null)
                    {
                        actions.Add(allyHealAction);
                    }
                }
            }

            // Phase 4.7: 위치 버프
            var usedPositionalBuffs = new HashSet<string>();
            int positionalBuffCount = 0;
            while (positionalBuffCount < MAX_POSITIONAL_BUFFS)
            {
                var positionalBuffAction = PlanPositionalBuff(situation, ref remainingAP, usedPositionalBuffs);
                if (positionalBuffAction == null) break;
                actions.Add(positionalBuffAction);
                positionalBuffCount++;
            }

            // Phase 4.8: Stratagem
            var stratagemAction = PlanStratagem(situation, ref remainingAP);
            if (stratagemAction != null)
            {
                actions.Add(stratagemAction);
            }

            // Phase 4.9: 마킹
            if (situation.AvailableMarkers.Count > 0 && situation.NearestEnemy != null)
            {
                var markerAction = PlanMarker(situation, situation.NearestEnemy, ref remainingAP);
                if (markerAction != null)
                {
                    actions.Add(markerAction);
                }
            }

            // ========== ★ Phase 5: 이동 또는 GapCloser (공격 불가 시) ==========
            // ★ v3.0.47: Balanced도 GapCloser 사용 - 핵심 수정점
            // ★ v3.0.55: remainingMP 체크 - 계획된 능력들의 MP 코스트 반영
            if (situation.NeedsReposition && situation.CanMove && remainingMP > 0)
            {
                var moveOrGapCloser = PlanMoveOrGapCloser(situation, ref remainingAP);
                if (moveOrGapCloser != null)
                {
                    actions.Add(moveOrGapCloser);

                    // 이동/GapCloser 후 공격 계획
                    if (reservedAP > 0 && situation.NearestEnemy != null)
                    {
                        var postMoveAttack = PlanPostMoveAttack(situation, situation.NearestEnemy, ref remainingAP);
                        if (postMoveAttack != null)
                        {
                            actions.Add(postMoveAttack);
                            Main.Log("[Balanced] Added post-move attack");
                        }
                    }
                }
            }

            // Phase 5.5: 디버프
            if (situation.NearestEnemy != null && remainingAP >= 1f)
            {
                var debuffAction = PlanDebuff(situation, situation.NearestEnemy, ref remainingAP);
                if (debuffAction != null)
                {
                    actions.Add(debuffAction);
                }
            }

            // ★ v3.0.57: Phase 6 - SequenceOptimizer 기반 최적 공격 시퀀스 선택 (원거리 선호 시)
            // 근접 선호 캐릭터는 기존 로직 사용
            bool didPlanAttack = false;
            int attacksPlanned = 0;
            var plannedTargetIds = new HashSet<string>();
            var plannedAbilityGuids = new HashSet<string>();

            while (remainingAP >= 1f && situation.HasHittableEnemies && attacksPlanned < MAX_ATTACKS_PER_PLAN)
            {
                if (situation.PrefersRanged)
                {
                    // ★ 원거리 선호: SequenceOptimizer 사용
                    var rangedAttacks = situation.AvailableAttacks
                        .Where(a => !a.IsMelee)
                        .Where(a => !AbilityDatabase.IsDangerousAoE(a))
                        .Where(a => !IsAbilityExcluded(a, plannedAbilityGuids))
                        .Where(a => CombatAPI.GetAbilityAPCost(a) <= remainingAP)
                        .ToList();

                    if (rangedAttacks.Count == 0) break;

                    var candidateTargets = new List<BaseUnitEntity>();
                    if (situation.BestTarget != null && !IsExcluded(situation.BestTarget, plannedTargetIds))
                        candidateTargets.Add(situation.BestTarget);

                    foreach (var hittable in situation.HittableEnemies)
                    {
                        if (hittable != null && !candidateTargets.Contains(hittable) && !IsExcluded(hittable, plannedTargetIds))
                            candidateTargets.Add(hittable);
                    }

                    if (candidateTargets.Count == 0) break;

                    // ★ v3.0.59: null vs 빈 리스트 구분
                    // - null: 최적화 실패 → 폴백 실행
                    // - 빈 리스트: "Skip attack" 결정 → 폴백 금지
                    var optimalActions = SequenceOptimizer.GetOptimalAttackActions(
                        situation,
                        rangedAttacks,
                        candidateTargets.First(),
                        ref remainingAP,
                        ref remainingMP,
                        "Balanced-Seq"
                    );

                    if (optimalActions == null)
                    {
                        // 최적화 실패 → 폴백: 기존 공격 로직
                        var attackAction = PlanAttack(situation, ref remainingAP, excludeTargetIds: plannedTargetIds, excludeAbilityGuids: plannedAbilityGuids);
                        if (attackAction == null) break;

                        actions.Add(attackAction);
                        didPlanAttack = true;
                        attacksPlanned++;

                        if (attackAction.Ability != null)
                        {
                            remainingMP -= CombatAPI.GetAbilityMPCost(attackAction.Ability);
                            if (remainingMP < 0) remainingMP = 0;

                            var guid = attackAction.Ability.Blueprint?.AssetGuid?.ToString();
                            if (!string.IsNullOrEmpty(guid))
                                plannedAbilityGuids.Add(guid);
                        }

                        var targetEntity = attackAction.Target?.Entity as BaseUnitEntity;
                        if (targetEntity != null)
                            plannedTargetIds.Add(targetEntity.UniqueId);
                    }
                    else if (optimalActions.Count == 0)
                    {
                        // ★ v3.0.59: "Skip attack" 결정 - 공격 루프 종료
                        Main.Log("[Balanced] Skipping attack (optimizer safety decision)");
                        break;
                    }
                    else
                    {
                        actions.AddRange(optimalActions);
                        didPlanAttack = true;
                        attacksPlanned++;

                        foreach (var action in optimalActions)
                        {
                            var targetEntity = action.Target?.Entity as BaseUnitEntity;
                            if (targetEntity != null)
                                plannedTargetIds.Add(targetEntity.UniqueId);

                            if (action.Ability != null)
                            {
                                var guid = action.Ability.Blueprint?.AssetGuid?.ToString();
                                if (!string.IsNullOrEmpty(guid))
                                    plannedAbilityGuids.Add(guid);
                            }
                        }

                        if (optimalActions.Any(a => a.Type == ActionType.Move))
                            break;
                    }
                }
                else
                {
                    // 근접 선호: 기존 로직 사용
                    var attackAction = PlanAttack(situation, ref remainingAP, excludeTargetIds: plannedTargetIds, excludeAbilityGuids: plannedAbilityGuids);
                    if (attackAction == null) break;

                    actions.Add(attackAction);
                    didPlanAttack = true;
                    attacksPlanned++;

                    if (attackAction.Ability != null)
                    {
                        remainingMP -= CombatAPI.GetAbilityMPCost(attackAction.Ability);
                        if (remainingMP < 0) remainingMP = 0;
                    }

                    var targetEntity = attackAction.Target?.Entity as BaseUnitEntity;
                    if (targetEntity != null)
                        plannedTargetIds.Add(targetEntity.UniqueId);

                    if (attackAction.Ability != null)
                    {
                        var guid = attackAction.Ability.Blueprint?.AssetGuid?.ToString();
                        if (!string.IsNullOrEmpty(guid))
                            plannedAbilityGuids.Add(guid);
                    }
                }
            }

            // ========== Phase 7: Post-Action (Run and Gun) ==========
            if (situation.HasPerformedFirstAction || didPlanAttack)
            {
                var postAction = PlanPostAction(situation, ref remainingAP);
                if (postAction != null)
                {
                    actions.Add(postAction);
                }
            }

            // Phase 7.5: 턴 종료 스킬
            var turnEndAction = PlanTurnEndingAbility(situation, ref remainingAP);
            if (turnEndAction != null)
            {
                actions.Add(turnEndAction);
            }

            // Phase 7.6: 행동 완료 후 안전 이동 (원거리 선호 시)
            bool hasMoveInPlan = actions.Any(a => a.Type == ActionType.Move);

            // ★ v3.0.55: remainingMP 체크 - 계획된 능력들의 MP 코스트 반영
            // 화염 수류탄 등 ClearMPAfterUse 능력은 이미 remainingMP=0으로 설정됨
            if (remainingMP <= 0)
            {
                Main.LogDebug($"[Balanced] Skip safe retreat - no remaining MP after planned abilities");
            }

            if (!hasMoveInPlan && remainingMP > 0 && situation.CanMove && situation.PrefersRanged)
            {
                if (situation.NearestEnemy != null && situation.NearestEnemyDistance < situation.MinSafeDistance * 1.2f)
                {
                    var safeRetreatAction = PlanPostActionSafeRetreat(situation);
                    if (safeRetreatAction != null)
                    {
                        actions.Add(safeRetreatAction);
                        hasMoveInPlan = true;
                        Main.Log("[Balanced] Post-action safe retreat");
                    }
                }
            }

            // Post-attack phase
            if ((situation.HasAttackedThisTurn || didPlanAttack) && remainingAP >= 1f)
            {
                var postAttackActions = PlanPostAttackActions(situation, ref remainingAP, skipMove: hasMoveInPlan);
                actions.AddRange(postAttackActions);
            }

            // ========== Phase 8: 턴 종료 ==========
            if (actions.Count == 0)
            {
                actions.Add(PlannedAction.EndTurn(GetEndTurnReason(situation)));
            }

            var priority = DeterminePriority(actions, situation);
            reasoning = $"Balanced: {DetermineReasoning(actions, situation)}";

            // ★ v3.0.55: MP 추적 로깅
            Main.LogDebug($"[Balanced] Plan complete: AP={remainingAP:F1}, MP={remainingMP:F1} (started with {situation.CurrentMP:F1})");

            return new TurnPlan(actions, priority, reasoning, situation.HPPercent, situation.NearestEnemyDistance, situation.HittableEnemies?.Count ?? 0);
        }

        #region Balanced-Specific Methods

        private PlannedAction PlanAllyHeal(Situation situation, BaseUnitEntity ally, ref float remainingAP)
        {
            if (situation.AvailableHeals.Count == 0) return null;

            var targetWrapper = new TargetWrapper(ally);

            foreach (var heal in situation.AvailableHeals)
            {
                float cost = CombatAPI.GetAbilityAPCost(heal);
                if (cost > remainingAP) continue;

                string reason;
                if (CombatAPI.CanUseAbilityOn(heal, targetWrapper, out reason))
                {
                    remainingAP -= cost;
                    Main.Log($"[Balanced] Heal ally: {heal.Name} -> {ally.CharacterName}");
                    return PlannedAction.Heal(heal, ally, $"Heal {ally.CharacterName}", cost);
                }
            }

            return null;
        }

        private new PlannedAction PlanSpecialAbility(Situation situation, ref float remainingAP)
        {
            if (situation.AvailableSpecialAbilities == null || situation.AvailableSpecialAbilities.Count == 0)
                return null;

            if (situation.BestTarget == null)
                return null;

            var target = situation.BestTarget;
            var enemies = situation.Enemies;
            var targetWrapper = new TargetWrapper(target);
            float currentAP = remainingAP;

            var scoredAbilities = situation.AvailableSpecialAbilities
                .Select(a => new
                {
                    Ability = a,
                    Score = SpecialAbilityHandler.GetSpecialAbilityEffectivenessScore(a, target, enemies),
                    Cost = CombatAPI.GetAbilityAPCost(a)
                })
                .Where(x => x.Cost <= currentAP && x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ToList();

            foreach (var entry in scoredAbilities)
            {
                var ability = entry.Ability;

                if (!SpecialAbilityHandler.CanUseSpecialAbilityEffectively(ability, target, enemies))
                    continue;

                string reason;
                if (CombatAPI.CanUseAbilityOn(ability, targetWrapper, out reason))
                {
                    remainingAP -= entry.Cost;

                    string abilityType = AbilityDatabase.IsDOTIntensify(ability) ? "DoT Intensify" :
                                        AbilityDatabase.IsChainEffect(ability) ? "Chain Effect" : "Special";

                    Main.Log($"[Balanced] {abilityType}: {ability.Name} -> {target.CharacterName}");
                    return PlannedAction.Attack(ability, target, $"{abilityType} on {target.CharacterName}", entry.Cost);
                }
            }

            return null;
        }

        #endregion
    }
}
