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
    /// ★ v3.0.47: Tank 전략
    /// 방어 자세 우선, 도발, 전선 유지, GapCloser로 적에게 돌진
    /// </summary>
    public class TankPlan : BasePlan
    {
        protected override string RoleName => "Tank";

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
                Main.Log($"[Tank] Reserving {reservedAP:F1} AP for post-move attack");
            }

            // Phase 1: 긴급 자기 힐
            var healAction = PlanEmergencyHeal(situation, ref remainingAP);
            if (healAction != null)
            {
                actions.Add(healAction);
                return new TurnPlan(actions, TurnPriority.Emergency, "Tank emergency heal");
            }

            // Phase 1.5: 재장전
            var reloadAction = PlanReload(situation, ref remainingAP);
            if (reloadAction != null)
            {
                actions.Add(reloadAction);
            }

            // Phase 2: 방어 자세 우선 (첫 행동 전)
            if (!situation.HasPerformedFirstAction)
            {
                var defenseAction = PlanDefensiveStanceWithReservation(situation, ref remainingAP, reservedAP);
                if (defenseAction != null)
                {
                    actions.Add(defenseAction);
                }
            }

            // Phase 3: 기타 선제적 버프
            if (!situation.HasBuffedThisTurn && !situation.HasPerformedFirstAction)
            {
                var buffAction = PlanBuffWithReservation(situation, ref remainingAP, reservedAP);
                if (buffAction != null)
                {
                    actions.Add(buffAction);
                }
            }

            // Phase 4: 도발 (근접 적 2명 이상)
            if (CountNearbyEnemies(situation, 5f) >= 2)
            {
                var tauntAction = PlanTaunt(situation, ref remainingAP);
                if (tauntAction != null)
                {
                    actions.Add(tauntAction);
                }
            }

            // Phase 4.5: 마킹 (공격 전 적 지정)
            if (situation.AvailableMarkers.Count > 0 && situation.NearestEnemy != null)
            {
                var markerAction = PlanMarker(situation, situation.NearestEnemy, ref remainingAP);
                if (markerAction != null)
                {
                    actions.Add(markerAction);
                }
            }

            // Phase 4.6: 위치 버프
            var usedPositionalBuffs = new HashSet<string>();
            int positionalBuffCount = 0;
            while (positionalBuffCount < MAX_POSITIONAL_BUFFS)
            {
                var positionalBuffAction = PlanPositionalBuff(situation, ref remainingAP, usedPositionalBuffs);
                if (positionalBuffAction == null) break;
                actions.Add(positionalBuffAction);
                positionalBuffCount++;
            }

            // Phase 4.7: Stratagem
            var stratagemAction = PlanStratagem(situation, ref remainingAP);
            if (stratagemAction != null)
            {
                actions.Add(stratagemAction);
            }

            // Phase 5: 공격 - 가까운 적 우선
            bool didPlanAttack = false;
            int attacksPlanned = 0;
            var plannedTargetIds = new HashSet<string>();
            var plannedAbilityGuids = new HashSet<string>();

            while (remainingAP >= 1f && situation.HasHittableEnemies && attacksPlanned < MAX_ATTACKS_PER_PLAN)
            {
                var attackAction = PlanAttack(situation, ref remainingAP, preferTarget: situation.NearestEnemy,
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

            // ★ Phase 8: 이동 또는 GapCloser (공격 가능한 적이 없을 때)
            // ★ v3.0.55: remainingMP 체크 - 계획된 능력들의 MP 코스트 반영
            bool hasMoveInPlan = actions.Any(a => a.Type == ActionType.Move);
            if (!hasMoveInPlan && !situation.HasHittableEnemies && situation.CanMove && remainingMP > 0 && situation.HasLivingEnemies)
            {
                // ★ v3.0.47: GapCloser 우선 시도 (Tank는 적에게 돌진)
                var moveOrGapCloser = PlanMoveOrGapCloser(situation, ref remainingAP);
                if (moveOrGapCloser != null)
                {
                    actions.Add(moveOrGapCloser);
                    hasMoveInPlan = true;

                    // 이동 후 공격 계획
                    if (reservedAP > 0 && situation.NearestEnemy != null)
                    {
                        var postMoveAttack = PlanPostMoveAttack(situation, situation.NearestEnemy, ref remainingAP);
                        if (postMoveAttack != null)
                        {
                            actions.Add(postMoveAttack);
                            Main.Log("[Tank] Added post-move attack");
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
                actions.Add(PlannedAction.EndTurn("Tank holding position"));
            }

            var priority = DeterminePriority(actions, situation);
            var reasoning = $"Tank: {DetermineReasoning(actions, situation)}";

            // ★ v3.0.55: MP 추적 로깅
            Main.LogDebug($"[Tank] Plan complete: AP={remainingAP:F1}, MP={remainingMP:F1} (started with {situation.CurrentMP:F1})");

            return new TurnPlan(actions, priority, reasoning, situation.HPPercent, situation.NearestEnemyDistance, situation.HittableEnemies?.Count ?? 0);
        }

        #region Tank-Specific Methods

        private new PlannedAction PlanDefensiveStanceWithReservation(Situation situation, ref float remainingAP, float reservedAP)
        {
            var target = new TargetWrapper(situation.Unit);

            foreach (var ability in situation.AvailableBuffs)
            {
                var info = AbilityDatabase.GetInfo(ability);
                if (info == null) continue;
                if (info.Timing != AbilityTiming.PreCombatBuff) continue;

                string bpName = ability.Blueprint?.name?.ToLower() ?? "";
                if (!bpName.Contains("defensive") && !bpName.Contains("stance") &&
                    !bpName.Contains("bulwark") && !bpName.Contains("guard"))
                    continue;

                float cost = CombatAPI.GetAbilityAPCost(ability);

                // 방어 자세는 필수 버프 - 예약 무시
                bool isEssential = IsEssentialBuff(ability, situation);
                if (!CanAffordBuffWithReservation(cost, remainingAP, reservedAP, isEssential))
                    continue;

                if (CombatAPI.HasActiveBuff(situation.Unit, ability)) continue;

                string reason;
                if (CombatAPI.CanUseAbilityOn(ability, target, out reason))
                {
                    remainingAP -= cost;
                    Main.Log($"[Tank] Defensive stance: {ability.Name}");
                    return PlannedAction.Buff(ability, situation.Unit, "Defensive stance priority", cost);
                }
            }

            return null;
        }

        private new PlannedAction PlanTaunt(Situation situation, ref float remainingAP)
        {
            var taunts = situation.AvailableBuffs
                .Where(a => AbilityDatabase.IsTaunt(a))
                .ToList();

            if (taunts.Count == 0) return null;

            foreach (var taunt in taunts)
            {
                float cost = CombatAPI.GetAbilityAPCost(taunt);
                if (cost > remainingAP) continue;

                if (CombatAPI.HasActiveBuff(situation.Unit, taunt)) continue;

                TargetWrapper target;
                if (taunt.Blueprint?.CanTargetSelf == true)
                {
                    target = new TargetWrapper(situation.Unit);
                }
                else if (situation.NearestEnemy != null)
                {
                    target = new TargetWrapper(situation.NearestEnemy);
                }
                else
                {
                    continue;
                }

                string reason;
                if (CombatAPI.CanUseAbilityOn(taunt, target, out reason))
                {
                    remainingAP -= cost;
                    Main.Log($"[Tank] Taunt: {taunt.Name}");
                    return PlannedAction.Buff(taunt, situation.Unit, "Taunt - enemies nearby", cost);
                }
            }

            return null;
        }

        #endregion
    }
}
