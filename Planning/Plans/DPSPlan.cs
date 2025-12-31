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

            // ★ v3.0.87: Phase 5 진입 상태 로깅
            Main.LogDebug($"[DPS] Phase 5 entry: AP={remainingAP:F1}, HasHittable={situation.HasHittableEnemies}, " +
                $"HittableCount={situation.HittableEnemies?.Count ?? 0}, AvailableAttacks={situation.AvailableAttacks?.Count ?? 0}");

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

            // ★ v3.0.87: Phase 5 종료 후 상태 로깅
            if (!didPlanAttack)
            {
                Main.LogDebug($"[DPS] Phase 5 exit: No attacks planned. AP={remainingAP:F1}, HasHittable={situation.HasHittableEnemies}");
            }
            else
            {
                Main.LogDebug($"[DPS] Phase 5 exit: {attacksPlanned} attacks planned. AP={remainingAP:F1}");
            }

            // ★ Phase 5.5: GapCloser (공격 계획 실패 시)
            // ★ v3.0.86: 거리 조건 제거 - 적이 4m에 있어도 근접 사거리(2m)에 못 들어올 수 있음
            // 기존: NearestEnemyDistance > 5f → 적이 5m 이내면 스킵 (버그!)
            // 수정: 공격 계획 실패 시 무조건 GapCloser 시도 (GapCloser 자체가 유효성 검사)

            // ★ v3.0.87: Phase 5.5 진입 전 상태 로깅
            Main.LogDebug($"[DPS] Phase 5.5 check: didPlanAttack={didPlanAttack}, HasHittableEnemies={situation.HasHittableEnemies}, " +
                $"NearestEnemy={situation.NearestEnemy?.CharacterName ?? "null"}, Distance={situation.NearestEnemyDistance:F1}m, AP={remainingAP:F1}");

            if (!didPlanAttack && situation.NearestEnemy != null)
            {
                Main.Log($"[DPS] Phase 5.5: Trying GapCloser as fallback (attack failed)");
                var gapCloserAction = PlanGapCloser(situation, situation.NearestEnemy, ref remainingAP);
                if (gapCloserAction != null)
                {
                    actions.Add(gapCloserAction);
                    didPlanAttack = true;  // GapCloser도 공격으로 취급
                    Main.Log($"[DPS] GapCloser fallback: {gapCloserAction.Ability?.Name}");
                }
                else
                {
                    Main.LogDebug($"[DPS] Phase 5.5: GapCloser returned null");
                }
            }
            else if (didPlanAttack)
            {
                Main.LogDebug($"[DPS] Phase 5.5: Skipped - already planned attack");
            }
            else
            {
                Main.LogDebug($"[DPS] Phase 5.5: Skipped - NearestEnemy is null");
            }

            // Phase 6: PostFirstAction
            if (situation.HasPerformedFirstAction || didPlanAttack)
            {
                var postAction = PlanPostAction(situation, ref remainingAP);
                if (postAction != null)
                {
                    actions.Add(postAction);

                    // ★ v3.0.98: MP 회복 능력 예측 (Blueprint에서 직접 읽어옴)
                    // 이 능력이 MP를 회복해줌을 예측해서 Phase 8 이동 가능하게 함
                    float expectedMP = AbilityDatabase.GetExpectedMPRecovery(postAction.Ability);
                    if (expectedMP > 0)
                    {
                        remainingMP += expectedMP;
                        Main.Log($"[DPS] Phase 6: {postAction.Ability.Name} will restore ~{expectedMP:F0} MP (predicted MP={remainingMP:F1})");
                    }
                }
            }

            // ★ v3.0.96: Phase 6.5: 공격 불가 시 남은 버프 사용
            // 이전 버그: Hittable=0이면 버프 사용 못함
            // ★ v3.1.10: PreAttackBuff, HeroicAct, RighteousFury 제외 (공격 없으면 무의미)
            if (!didPlanAttack && remainingAP >= 1f && situation.AvailableBuffs.Count > 0)
            {
                Main.Log($"[DPS] Phase 6.5: No attack possible, using remaining buffs (AP={remainingAP:F1})");

                foreach (var buff in situation.AvailableBuffs)
                {
                    if (remainingAP < 1f) break;

                    // ★ v3.1.10: 공격 전 버프는 공격이 없으면 의미 없음
                    var timing = AbilityDatabase.GetTiming(buff);
                    if (timing == AbilityTiming.PreAttackBuff ||
                        timing == AbilityTiming.HeroicAct ||
                        timing == AbilityTiming.RighteousFury)
                    {
                        Main.LogDebug($"[DPS] Phase 6.5: Skip {buff.Name} (PreAttackBuff without attack)");
                        continue;
                    }

                    float cost = CombatAPI.GetAbilityAPCost(buff);
                    if (cost > remainingAP) continue;

                    if (CombatAPI.HasActiveBuff(situation.Unit, buff)) continue;

                    // ★ Self 또는 Ally 타겟 버프
                    var bp = buff.Blueprint;
                    if (bp?.CanTargetSelf != true && bp?.CanTargetFriends != true) continue;

                    var target = new TargetWrapper(situation.Unit);
                    string reason;
                    if (CombatAPI.CanUseAbilityOn(buff, target, out reason))
                    {
                        remainingAP -= cost;
                        actions.Add(PlannedAction.Buff(buff, situation.Unit, "Fallback buff - no attack available", cost));
                        Main.Log($"[DPS] Fallback buff: {buff.Name}");
                    }
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
            // ★ v3.0.89: 공격 계획 실패 시에도 이동 허용
            // ★ v3.0.99: MP 회복 예측 후 이동 가능 - situation.CanMove는 계획 시작 시점 기준
            //            Phase 6에서 MP 회복을 예측했으면 remainingMP > 0으로 이동 가능
            // ★ v3.1.01: predictedMP를 MovementAPI에 전달하여 reachable tiles 계산에 사용
            bool hasMoveInPlan = actions.Any(a => a.Type == ActionType.Move);
            bool needsMovement = situation.NeedsReposition || (!didPlanAttack && situation.HasLivingEnemies);
            // ★ v3.0.99: situation.CanMove는 계획 시작 시점 MP 기준, remainingMP는 예측된 MP 포함
            bool canMove = situation.CanMove || remainingMP > 0;

            Main.LogDebug($"[DPS] Phase 8 check: hasMoveInPlan={hasMoveInPlan}, NeedsReposition={situation.NeedsReposition}, " +
                $"didPlanAttack={didPlanAttack}, needsMovement={needsMovement}, CanMove={canMove}, MP={remainingMP:F1}");

            if (!hasMoveInPlan && needsMovement && canMove && remainingMP > 0)
            {
                Main.Log($"[DPS] Phase 8: Trying move (attack planned={didPlanAttack}, predictedMP={remainingMP:F1})");
                // ★ v3.0.89: 공격 실패 시 forceMove=true로 이동 강제
                bool forceMove = !didPlanAttack && situation.HasHittableEnemies;
                // ★ v3.1.00: MP 회복 예측 후 situation.CanMove=False여도 이동 가능
                // PlanMoveToEnemy 내부의 CanMove 체크를 우회
                bool bypassCanMoveCheck = !situation.CanMove && remainingMP > 0;
                // ★ v3.1.01: remainingMP를 MovementAPI에 전달하여 실제로 이동 가능한 타일 계산
                var moveOrGapCloser = PlanMoveOrGapCloser(situation, ref remainingAP, forceMove, bypassCanMoveCheck, remainingMP);
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

            // ★ v3.1.09: InitialAP/InitialMP 전달 (리플랜 감지용)
            return new TurnPlan(actions, priority, reasoning, situation.HPPercent, situation.NearestEnemyDistance,
                situation.HittableEnemies?.Count ?? 0, situation.CurrentAP, situation.CurrentMP);
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
            // ★ v3.1.10: 사용 가능한 공격이 없으면 공격 전 버프 사용 금지
            // 문제: 속사 같은 PreAttackBuff가 모든 공격 쿨다운일 때도 사용됨
            if (situation.AvailableAttacks == null || situation.AvailableAttacks.Count == 0)
            {
                Main.LogDebug("[DPS] PlanAttackBuff skipped: No available attacks");
                return null;
            }

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

            var enemies = situation.Enemies;
            if (enemies == null || enemies.Count == 0)
                return null;

            float currentAP = remainingAP;

            // ★ v3.0.97: 모든 적을 대상으로 특수 능력 시도
            // 이전: BestTarget만 사용 → 불타지 않은 적이면 DOTIntensify 스킵
            // 변경: 모든 적 순회하여 유효한 타겟 찾기
            foreach (var ability in situation.AvailableSpecialAbilities)
            {
                float cost = CombatAPI.GetAbilityAPCost(ability);
                if (cost > currentAP) continue;

                // 모든 적에 대해 이 능력 사용 가능 여부 확인
                foreach (var enemy in enemies)
                {
                    if (enemy == null || enemy.LifeState.IsDead) continue;

                    if (!SpecialAbilityHandler.CanUseSpecialAbilityEffectively(ability, enemy, enemies))
                        continue;

                    var targetWrapper = new TargetWrapper(enemy);
                    string reason;
                    if (CombatAPI.CanUseAbilityOn(ability, targetWrapper, out reason))
                    {
                        remainingAP -= cost;

                        string abilityType = AbilityDatabase.IsDOTIntensify(ability) ? "DoT Intensify" :
                                            AbilityDatabase.IsChainEffect(ability) ? "Chain Effect" : "Special";

                        Main.Log($"[DPS] {abilityType}: {ability.Name} -> {enemy.CharacterName}");
                        return PlannedAction.Attack(ability, enemy, $"{abilityType} on {enemy.CharacterName}", cost);
                    }
                }
            }

            return null;
        }

        #endregion
    }
}
