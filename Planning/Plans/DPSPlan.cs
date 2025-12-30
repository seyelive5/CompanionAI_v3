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
    /// ★ v3.0.47: DPS 전략
    /// Heroic Act, 마무리 스킬, 약한 적 우선, GapCloser 적극 활용
    /// </summary>
    public class DPSPlan : BasePlan
    {
        protected override string RoleName => "DPS";

        public override TurnPlan CreatePlan(Situation situation, TurnState turnState)
        {
            var actions = new List<PlannedAction>();
            // ★ v3.0.68: 게임 AP 직접 사용
            float remainingAP = situation.CurrentAP;
            // ★ v3.0.55: MP 추적 - AP처럼 계획 단계에서 MP도 추적
            float remainingMP = situation.CurrentMP;

            float reservedAP = CalculateReservedAPForPostMoveAttack(situation);
            if (reservedAP > 0)
            {
                Main.Log($"[DPS] Reserving {reservedAP:F1} AP for post-move attack");
            }

            // Phase 1: 긴급 자기 힐
            var healAction = PlanEmergencyHeal(situation, ref remainingAP);
            if (healAction != null)
            {
                actions.Add(healAction);
                return new TurnPlan(actions, TurnPriority.Emergency, "DPS emergency heal");
            }

            // Phase 1.5: 재장전
            var reloadAction = PlanReload(situation, ref remainingAP);
            if (reloadAction != null)
            {
                actions.Add(reloadAction);
            }

            // Phase 1.6: 원거리 캐릭터 후퇴
            if (ShouldRetreat(situation))
            {
                var retreatAction = PlanRetreat(situation);
                if (retreatAction != null)
                {
                    actions.Add(retreatAction);
                }
            }

            // Phase 2: Heroic Act (Momentum 175+)
            var heroicAction = PlanHeroicAct(situation, ref remainingAP);
            if (heroicAction != null)
            {
                actions.Add(heroicAction);
            }

            // Phase 3: 마무리 스킬 우선 (적 HP 30% 미만)
            var lowHPEnemy = FindLowHPEnemy(situation, 30f);
            if (lowHPEnemy != null)
            {
                var finisherAction = PlanFinisher(situation, lowHPEnemy, ref remainingAP);
                if (finisherAction != null)
                {
                    actions.Add(finisherAction);
                }
            }

            // Phase 4: 공격 버프 (첫 행동 전)
            if (!situation.HasPerformedFirstAction && !situation.HasBuffedThisTurn)
            {
                var buffAction = PlanAttackBuffWithReservation(situation, ref remainingAP, reservedAP);
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

            // Phase 4.6: 마킹
            if (situation.AvailableMarkers.Count > 0 && situation.BestTarget != null)
            {
                var markerAction = PlanMarker(situation, situation.BestTarget, ref remainingAP);
                if (markerAction != null)
                {
                    actions.Add(markerAction);
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

            // Phase 5: 공격 - 약한 적 우선
            bool didPlanAttack = false;
            int attacksPlanned = 0;
            var plannedTargetIds = new HashSet<string>();
            var plannedAbilityGuids = new HashSet<string>();

            while (remainingAP >= 1f && situation.HasHittableEnemies && attacksPlanned < MAX_ATTACKS_PER_PLAN)
            {
                var weakestEnemy = FindWeakestEnemy(situation, plannedTargetIds);
                var attackAction = PlanAttack(situation, ref remainingAP, preferTarget: weakestEnemy ?? situation.BestTarget,
                    excludeTargetIds: plannedTargetIds, excludeAbilityGuids: plannedAbilityGuids);
                if (attackAction == null) break;

                actions.Add(attackAction);
                didPlanAttack = true;
                attacksPlanned++;

                // ★ v3.0.55: MP 코스트 차감 (ClearMPAfterUse 능력은 999 반환 → MP=0)
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

            // ★ Phase 5.5: GapCloser (적이 멀리 있을 때)
            // DPS는 공격 후에도 GapCloser로 추격 가능
            if (!situation.HasHittableEnemies && situation.NearestEnemyDistance > 5f && situation.NearestEnemy != null)
            {
                var gapCloserAction = PlanGapCloser(situation, situation.NearestEnemy, ref remainingAP);
                if (gapCloserAction != null)
                {
                    actions.Add(gapCloserAction);
                    didPlanAttack = true;  // GapCloser도 공격으로 취급
                }
            }

            // Phase 6: PostFirstAction
            if (situation.HasPerformedFirstAction || didPlanAttack)
            {
                var postAction = PlanPostAction(situation, ref remainingAP);
                if (postAction != null)
                {
                    actions.Add(postAction);
                }
            }

            // Phase 7: 턴 종료 스킬
            var turnEndAction = PlanTurnEndingAbility(situation, ref remainingAP);
            if (turnEndAction != null)
            {
                actions.Add(turnEndAction);
            }

            // ★ Phase 8: 이동 또는 GapCloser (공격 불가 시)
            // ★ v3.0.55: remainingMP 체크 - 계획된 능력들의 MP 코스트 반영
            bool hasMoveInPlan = actions.Any(a => a.Type == ActionType.Move);
            if (!hasMoveInPlan && situation.NeedsReposition && situation.CanMove && remainingMP > 0)
            {
                var moveOrGapCloser = PlanMoveOrGapCloser(situation, ref remainingAP);
                if (moveOrGapCloser != null)
                {
                    actions.Add(moveOrGapCloser);
                    hasMoveInPlan = true;

                    if (reservedAP > 0 && situation.NearestEnemy != null)
                    {
                        var postMoveAttack = PlanPostMoveAttack(situation, situation.NearestEnemy, ref remainingAP);
                        if (postMoveAttack != null)
                        {
                            actions.Add(postMoveAttack);
                            Main.Log("[DPS] Added post-move attack");
                        }
                    }
                }
            }

            // Post-attack phase
            if ((situation.HasAttackedThisTurn || didPlanAttack) && remainingAP >= 1f)
            {
                var postAttackActions = PlanPostAttackActions(situation, ref remainingAP, skipMove: hasMoveInPlan);
                actions.AddRange(postAttackActions);
            }

            // 턴 종료
            if (actions.Count == 0)
            {
                actions.Add(PlannedAction.EndTurn("DPS no targets"));
            }

            var priority = DeterminePriority(actions, situation);
            var reasoning = $"DPS: {DetermineReasoning(actions, situation)}";

            // ★ v3.0.55: MP 추적 로깅
            Main.LogDebug($"[DPS] Plan complete: AP={remainingAP:F1}, MP={remainingMP:F1} (started with {situation.CurrentMP:F1})");

            return new TurnPlan(actions, priority, reasoning, situation.HPPercent, situation.NearestEnemyDistance, situation.HittableEnemies?.Count ?? 0);
        }

        #region DPS-Specific Methods

        private new PlannedAction PlanHeroicAct(Situation situation, ref float remainingAP)
        {
            var heroicAbilities = situation.AvailableBuffs
                .Where(a => AbilityDatabase.IsHeroicAct(a))
                .ToList();

            if (heroicAbilities.Count == 0) return null;

            var target = new TargetWrapper(situation.Unit);
            string unitId = situation.Unit.UniqueId;

            foreach (var heroic in heroicAbilities)
            {
                float cost = CombatAPI.GetAbilityAPCost(heroic);
                if (cost > remainingAP) continue;

                if (AbilityDatabase.IsSingleUse(heroic) &&
                    AbilityUsageTracker.WasUsedRecently(unitId, heroic, 6000))
                {
                    continue;
                }

                if (CombatAPI.HasActiveBuff(situation.Unit, heroic)) continue;

                string reason;
                if (CombatAPI.CanUseAbilityOn(heroic, target, out reason))
                {
                    AbilityUsageTracker.MarkUsed(unitId, heroic);
                    remainingAP -= cost;
                    Main.Log($"[DPS] Heroic Act: {heroic.Name}");
                    return PlannedAction.Buff(heroic, situation.Unit, "Heroic Act - high momentum", cost);
                }
            }

            return null;
        }

        private new PlannedAction PlanFinisher(Situation situation, BaseUnitEntity target, ref float remainingAP)
        {
            var finishers = situation.AvailableAttacks
                .Where(a => AbilityDatabase.IsFinisher(a))
                .ToList();

            if (finishers.Count == 0) return null;

            var targetWrapper = new TargetWrapper(target);

            // 1타 킬 가능한 마무리 스킬 우선
            foreach (var finisher in finishers)
            {
                float cost = CombatAPI.GetAbilityAPCost(finisher);
                if (cost > remainingAP) continue;

                bool canKill = CombatAPI.CanKillInOneHit(finisher, target);

                string reason;
                if (CombatAPI.CanUseAbilityOn(finisher, targetWrapper, out reason))
                {
                    if (canKill)
                    {
                        remainingAP -= cost;
                        int hp = CombatAPI.GetActualHP(target);
                        var (minDmg, maxDmg, _) = CombatAPI.GetDamagePrediction(finisher, target);
                        Main.Log($"[DPS] Finisher (KILL): {finisher.Name} -> {target.CharacterName} (HP={hp})");
                        return PlannedAction.Attack(finisher, target, $"Finisher KILL on {target.CharacterName}", cost);
                    }
                }
            }

            // 1타 킬 불가능해도 낮은 HP 적에게 사용
            foreach (var finisher in finishers)
            {
                float cost = CombatAPI.GetAbilityAPCost(finisher);
                if (cost > remainingAP) continue;

                string reason;
                if (CombatAPI.CanUseAbilityOn(finisher, targetWrapper, out reason))
                {
                    remainingAP -= cost;
                    Main.Log($"[DPS] Finisher: {finisher.Name} -> {target.CharacterName}");
                    return PlannedAction.Attack(finisher, target, $"Finisher on {target.CharacterName}", cost);
                }
            }

            return null;
        }

        private new PlannedAction PlanAttackBuffWithReservation(Situation situation, ref float remainingAP, float reservedAP)
        {
            var attackBuffs = situation.AvailableBuffs
                .Where(a => {
                    var timing = AbilityDatabase.GetTiming(a);
                    return timing == AbilityTiming.PreAttackBuff || timing == AbilityTiming.RighteousFury;
                })
                .ToList();

            if (attackBuffs.Count == 0) return null;

            float effectiveReservedAP = situation.HasHittableEnemies
                ? (situation.PrimaryAttack != null ? CombatAPI.GetAbilityAPCost(situation.PrimaryAttack) : 1f)
                : reservedAP;

            var target = new TargetWrapper(situation.Unit);

            foreach (var buff in attackBuffs)
            {
                if (AbilityDatabase.IsRunAndGun(buff)) continue;
                if (AbilityDatabase.IsPostFirstAction(buff)) continue;

                float cost = CombatAPI.GetAbilityAPCost(buff);

                bool isEssential = IsEssentialBuff(buff, situation);
                if (!CanAffordBuffWithReservation(cost, remainingAP, effectiveReservedAP, isEssential))
                    continue;

                if (CombatAPI.HasActiveBuff(situation.Unit, buff)) continue;

                string reason;
                if (CombatAPI.CanUseAbilityOn(buff, target, out reason))
                {
                    remainingAP -= cost;
                    Main.Log($"[DPS] Attack buff: {buff.Name}");
                    return PlannedAction.Buff(buff, situation.Unit, "Attack buff before strike", cost);
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

                    Main.Log($"[DPS] {abilityType}: {ability.Name} -> {target.CharacterName}");
                    return PlannedAction.Attack(ability, target, $"{abilityType} on {target.CharacterName}", entry.Cost);
                }
            }

            return null;
        }

        #endregion
    }
}
