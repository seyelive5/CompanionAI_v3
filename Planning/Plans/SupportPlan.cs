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
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning.Plans
{
    /// <summary>
    /// ★ v3.0.47: Support 전략
    /// ★ v3.0.57: SequenceOptimizer 기반 행동 조합 점수화 적용
    /// 힐 → 버프 → 디버프 → 안전 공격(최적화) → 후퇴
    /// </summary>
    public class SupportPlan : BasePlan
    {
        protected override string RoleName => "Support";

        public override TurnPlan CreatePlan(Situation situation, TurnState turnState)
        {
            var actions = new List<PlannedAction>();
            // ★ v3.0.68: 게임 AP 직접 사용
            float remainingAP = situation.CurrentAP;
            // ★ v3.0.55: MP 추적 - AP처럼 계획 단계에서 MP도 추적
            float remainingMP = situation.CurrentMP;

            float reservedAP = CalculateReservedAPForPostMoveAttack(situation);

            // ★ v3.0.57: Phase 0 제거 - SequenceOptimizer가 이동+공격 조합을 자동 비교
            // 기존 Phase 0의 휴리스틱 판단 대신, Phase 6에서 최적 시퀀스 선택

            // Phase 1: 긴급 자기 힐
            var selfHealAction = PlanEmergencyHeal(situation, ref remainingAP);
            if (selfHealAction != null)
            {
                actions.Add(selfHealAction);
                return new TurnPlan(actions, TurnPriority.Emergency, "Support emergency self-heal");
            }

            // Phase 1.5: 재장전
            var reloadAction = PlanReload(situation, ref remainingAP);
            if (reloadAction != null)
            {
                actions.Add(reloadAction);
            }

            // ★ v3.0.57: Phase 1.6 제거 - 이동은 Phase 6에서 SequenceOptimizer가 결정
            // 기존: 무조건 후퇴 → 공격
            // 신규: "현재 위치 공격" vs "후퇴 → 공격" 비교 후 최적 선택

            // Phase 2: 아군 힐 (HP < 50%)
            var woundedAlly = FindWoundedAlly(situation, 50f);
            if (woundedAlly != null)
            {
                var allyHealAction = PlanAllyHeal(situation, woundedAlly, ref remainingAP);
                if (allyHealAction != null)
                {
                    actions.Add(allyHealAction);
                }
            }

            // Phase 3: 선제적 자기 버프
            if (!situation.HasBuffedThisTurn && !situation.HasPerformedFirstAction)
            {
                var selfBuffAction = PlanBuffWithReservation(situation, ref remainingAP, reservedAP);
                if (selfBuffAction != null)
                {
                    actions.Add(selfBuffAction);
                }
            }

            // Phase 4: 아군 버프 (Tank > DPS > 기타 우선순위)
            var allyBuffAction = PlanAllyBuff(situation, ref remainingAP);
            if (allyBuffAction != null)
            {
                actions.Add(allyBuffAction);
            }

            // Phase 4.5: 위치 버프
            var usedPositionalBuffs = new HashSet<string>();
            int positionalBuffCount = 0;
            while (positionalBuffCount < MAX_POSITIONAL_BUFFS)
            {
                var positionalBuffAction = PlanPositionalBuff(situation, ref remainingAP, usedPositionalBuffs);
                if (positionalBuffAction == null) break;
                actions.Add(positionalBuffAction);
                positionalBuffCount++;
            }

            // Phase 4.6: Stratagem
            var stratagemAction = PlanStratagem(situation, ref remainingAP);
            if (stratagemAction != null)
            {
                actions.Add(stratagemAction);
            }

            // Phase 4.7: 마킹
            if (situation.AvailableMarkers.Count > 0 && situation.NearestEnemy != null)
            {
                var markerAction = PlanMarker(situation, situation.NearestEnemy, ref remainingAP);
                if (markerAction != null)
                {
                    actions.Add(markerAction);
                }
            }

            // Phase 5: 적 디버프
            if (situation.NearestEnemy != null)
            {
                var debuffAction = PlanDebuff(situation, situation.NearestEnemy, ref remainingAP);
                if (debuffAction != null)
                {
                    actions.Add(debuffAction);
                }
            }

            // ★ v3.0.57: Phase 6 - SequenceOptimizer 기반 최적 공격 시퀀스 선택
            // "현재 위치에서 공격" vs "후퇴 → 공격" 조합을 점수화하여 비교
            bool didPlanAttack = false;
            int attacksPlanned = 0;
            var plannedTargetIds = new HashSet<string>();
            var plannedAbilityGuids = new HashSet<string>();

            while (remainingAP >= 1f && situation.HasHittableEnemies && attacksPlanned < MAX_ATTACKS_PER_PLAN)
            {
                // 사용 가능한 원거리 공격 필터링
                var rangedAttacks = situation.AvailableAttacks
                    .Where(a => !a.IsMelee)
                    .Where(a => !AbilityDatabase.IsDangerousAoE(a))
                    .Where(a => !IsAbilityExcluded(a, plannedAbilityGuids))
                    .Where(a => CombatAPI.GetAbilityAPCost(a) <= remainingAP)
                    .ToList();

                if (rangedAttacks.Count == 0) break;

                // 타겟 후보 목록
                var candidateTargets = new List<BaseUnitEntity>();
                if (situation.BestTarget != null && !IsExcluded(situation.BestTarget, plannedTargetIds))
                    candidateTargets.Add(situation.BestTarget);

                foreach (var hittable in situation.HittableEnemies)
                {
                    if (hittable != null && !candidateTargets.Contains(hittable) && !IsExcluded(hittable, plannedTargetIds))
                        candidateTargets.Add(hittable);
                }

                if (candidateTargets.Count == 0) break;

                // ★ SequenceOptimizer로 최적 시퀀스 선택
                var optimalActions = SequenceOptimizer.GetOptimalAttackActions(
                    situation,
                    rangedAttacks,
                    candidateTargets.First(),  // 첫 번째 타겟으로 최적화
                    ref remainingAP,
                    ref remainingMP,
                    "Support-Seq"
                );

                // ★ v3.0.59: null vs 빈 리스트 구분
                // - null: 최적화 실패 → 폴백 실행
                // - 빈 리스트: "Skip attack" 결정 → 폴백 금지
                if (optimalActions == null)
                {
                    // 최적화 실패 시 폴백: 기존 로직
                    var fallbackAction = PlanSafeRangedAttackFallback(situation, ref remainingAP, ref remainingMP,
                        excludeTargetIds: plannedTargetIds, excludeAbilityGuids: plannedAbilityGuids);
                    if (fallbackAction == null) break;

                    actions.Add(fallbackAction);
                    didPlanAttack = true;
                    attacksPlanned++;

                    var targetEntity = fallbackAction.Target?.Entity as BaseUnitEntity;
                    if (targetEntity != null)
                        plannedTargetIds.Add(targetEntity.UniqueId);

                    if (fallbackAction.Ability != null)
                    {
                        var guid = fallbackAction.Ability.Blueprint?.AssetGuid?.ToString();
                        if (!string.IsNullOrEmpty(guid))
                            plannedAbilityGuids.Add(guid);
                    }
                }
                else if (optimalActions.Count == 0)
                {
                    // ★ v3.0.59: "Skip attack" 결정 - 공격 루프 종료
                    Main.Log("[Support] Skipping attack (optimizer safety decision)");
                    break;
                }
                else
                {
                    // 최적화 성공 - 시퀀스의 모든 행동 추가
                    actions.AddRange(optimalActions);
                    didPlanAttack = true;
                    attacksPlanned++;

                    // 사용된 타겟/능력 기록
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

                    // 시퀀스가 이동을 포함했으면 추가 공격 루프 종료
                    if (optimalActions.Any(a => a.Type == ActionType.Move))
                        break;
                }
            }

            // Phase 7: PostFirstAction
            if (situation.HasPerformedFirstAction || didPlanAttack)
            {
                var postAction = PlanPostAction(situation, ref remainingAP);
                if (postAction != null)
                {
                    actions.Add(postAction);

                    // ★ v3.0.98: MP 회복 능력 예측 (Blueprint에서 직접 읽어옴)
                    float expectedMP = AbilityDatabase.GetExpectedMPRecovery(postAction.Ability);
                    if (expectedMP > 0)
                    {
                        remainingMP += expectedMP;
                        Main.Log($"[Support] Phase 7: {postAction.Ability.Name} will restore ~{expectedMP:F0} MP (predicted MP={remainingMP:F1})");
                    }
                }
            }

            // ★ v3.0.96: Phase 7.5: 공격 불가 시 남은 버프 사용
            // ★ v3.1.10: PreAttackBuff, HeroicAct, RighteousFury 제외 (공격 없으면 무의미)
            if (!didPlanAttack && remainingAP >= 1f && situation.AvailableBuffs.Count > 0)
            {
                Main.Log($"[Support] Phase 7.5: No attack possible, using remaining buffs (AP={remainingAP:F1})");

                foreach (var buff in situation.AvailableBuffs)
                {
                    if (remainingAP < 1f) break;

                    // ★ v3.1.10: 공격 전 버프는 공격이 없으면 의미 없음
                    var timing = AbilityDatabase.GetTiming(buff);
                    if (timing == AbilityTiming.PreAttackBuff ||
                        timing == AbilityTiming.HeroicAct ||
                        timing == AbilityTiming.RighteousFury)
                    {
                        Main.LogDebug($"[Support] Phase 7.5: Skip {buff.Name} (PreAttackBuff without attack)");
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
                        Main.Log($"[Support] Fallback buff: {buff.Name}");
                    }
                }
            }

            // Phase 8: 턴 종료 스킬
            var turnEndAction = PlanTurnEndingAbility(situation, ref remainingAP);
            if (turnEndAction != null)
            {
                actions.Add(turnEndAction);
            }

            // Phase 8.5: 행동 완료 후 안전 이동
            bool alreadyHasMoveAction = actions.Any(a => a.Type == ActionType.Move);

            // ★ v3.0.55: remainingMP 체크 - 계획된 능력들의 MP 코스트 반영
            // 화염 수류탄 등 ClearMPAfterUse 능력은 이미 remainingMP=0으로 설정됨
            if (remainingMP <= 0)
            {
                Main.LogDebug($"[Support] Skip safe retreat - no remaining MP after planned abilities");
            }

            if (!alreadyHasMoveAction && remainingMP > 0 && situation.CanMove && situation.PrefersRanged)
            {
                if (situation.NearestEnemy != null && situation.NearestEnemyDistance < situation.MinSafeDistance * 1.2f)
                {
                    var safeRetreatAction = PlanPostActionSafeRetreat(situation);
                    if (safeRetreatAction != null)
                    {
                        actions.Add(safeRetreatAction);
                        alreadyHasMoveAction = true;
                        Main.Log($"[Support] Post-action safe retreat");
                    }
                }
            }

            // ★ Phase 9: 이동 또는 GapCloser (공격 불가 시)
            // ★ v3.0.48: Support도 GapCloser 지원
            // ★ v3.0.55: remainingMP 체크 - 계획된 능력들의 MP 코스트 반영
            // ★ v3.0.90: 공격 계획 실패 시에도 이동 허용
            // ★ v3.0.99: MP 회복 예측 후 이동 가능
            // ★ v3.1.01: predictedMP를 MovementAPI에 전달하여 reachable tiles 계산에 사용
            bool hasMoveInPlan = actions.Any(a => a.Type == ActionType.Move);
            bool needsMovement = situation.NeedsReposition || (!didPlanAttack && situation.HasLivingEnemies);
            bool canMove = situation.CanMove || remainingMP > 0;

            if (!hasMoveInPlan && needsMovement && canMove && remainingMP > 0)
            {
                Main.Log($"[Support] Phase 9: Trying move (attack planned={didPlanAttack}, predictedMP={remainingMP:F1})");
                // ★ v3.0.90: 공격 실패 시 forceMove=true로 이동 강제
                bool forceMove = !didPlanAttack && situation.HasHittableEnemies;
                // ★ v3.1.00: MP 회복 예측 후 situation.CanMove=False여도 이동 가능
                bool bypassCanMoveCheck = !situation.CanMove && remainingMP > 0;
                // ★ v3.1.01: remainingMP를 MovementAPI에 전달
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
                            Main.Log("[Support] Added post-move attack");
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
                actions.Add(PlannedAction.EndTurn("Support maintaining position"));
            }

            var priority = DeterminePriority(actions, situation);
            var reasoning = $"Support: {DetermineReasoning(actions, situation)}";

            // ★ v3.0.55: MP 추적 로깅
            Main.LogDebug($"[Support] Plan complete: AP={remainingAP:F1}, MP={remainingMP:F1} (started with {situation.CurrentMP:F1})");

            // ★ v3.1.09: InitialAP/InitialMP 전달 (리플랜 감지용)
            return new TurnPlan(actions, priority, reasoning, situation.HPPercent, situation.NearestEnemyDistance,
                situation.HittableEnemies?.Count ?? 0, situation.CurrentAP, situation.CurrentMP);
        }

        #region Support-Specific Methods

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
                    Main.Log($"[Support] Heal ally: {heal.Name} -> {ally.CharacterName}");
                    return PlannedAction.Heal(heal, ally, $"Heal {ally.CharacterName}", cost);
                }
            }

            return null;
        }

        private PlannedAction PlanAllyBuff(Situation situation, ref float remainingAP)
        {
            // 버프 대상 우선순위: Tank > DPS > 본인 > 기타
            var prioritizedTargets = new List<BaseUnitEntity>();

            // 1. Tank 역할 먼저
            foreach (var ally in situation.Allies.Where(a => a != null && !a.LifeState.IsDead))
            {
                var settings = ModSettings.Instance?.GetOrCreateSettings(ally.UniqueId, ally.CharacterName);
                if (settings?.Role == AIRole.Tank)
                    prioritizedTargets.Add(ally);
            }

            // 2. DPS 역할
            foreach (var ally in situation.Allies.Where(a => a != null && !a.LifeState.IsDead))
            {
                var settings = ModSettings.Instance?.GetOrCreateSettings(ally.UniqueId, ally.CharacterName);
                if (settings?.Role == AIRole.DPS && !prioritizedTargets.Contains(ally))
                    prioritizedTargets.Add(ally);
            }

            // 3. 본인
            prioritizedTargets.Add(situation.Unit);

            // 4. 나머지 아군
            foreach (var ally in situation.Allies.Where(a => a != null && !a.LifeState.IsDead))
            {
                if (!prioritizedTargets.Contains(ally))
                    prioritizedTargets.Add(ally);
            }

            foreach (var buff in situation.AvailableBuffs)
            {
                if (buff.Blueprint?.CanTargetFriends != true) continue;

                float cost = CombatAPI.GetAbilityAPCost(buff);
                if (cost > remainingAP) continue;

                foreach (var target in prioritizedTargets)
                {
                    if (CombatAPI.HasActiveBuff(target, buff)) continue;

                    var targetWrapper = new TargetWrapper(target);
                    string reason;
                    if (CombatAPI.CanUseAbilityOn(buff, targetWrapper, out reason))
                    {
                        remainingAP -= cost;
                        Main.Log($"[Support] Buff ally: {buff.Name} -> {target.CharacterName}");
                        return PlannedAction.Buff(buff, target, $"Buff {target.CharacterName}", cost);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// ★ v3.0.57: SequenceOptimizer 실패 시 폴백용
        /// ★ v3.0.49: Weapon != null 조건 제거 - 사이킥/수류탄 능력 허용
        /// ★ v3.0.50: AoE 아군 피해 체크 추가
        /// </summary>
        private PlannedAction PlanSafeRangedAttackFallback(Situation situation, ref float remainingAP, ref float remainingMP,
            HashSet<string> excludeTargetIds = null, HashSet<string> excludeAbilityGuids = null)
        {
            var rangedAttacks = situation.AvailableAttacks
                .Where(a => !a.IsMelee)
                .Where(a => !AbilityDatabase.IsDangerousAoE(a))
                .Where(a => !IsAbilityExcluded(a, excludeAbilityGuids))
                .OrderBy(a => CombatAPI.GetAbilityAPCost(a))
                .ToList();

            if (rangedAttacks.Count == 0) return null;

            var candidateTargets = new List<BaseUnitEntity>();

            if (situation.BestTarget != null && !IsExcluded(situation.BestTarget, excludeTargetIds))
                candidateTargets.Add(situation.BestTarget);

            foreach (var hittable in situation.HittableEnemies)
            {
                if (hittable != null && !candidateTargets.Contains(hittable) && !IsExcluded(hittable, excludeTargetIds))
                    candidateTargets.Add(hittable);
            }

            if (situation.NearestEnemy != null && !candidateTargets.Contains(situation.NearestEnemy) && !IsExcluded(situation.NearestEnemy, excludeTargetIds))
                candidateTargets.Add(situation.NearestEnemy);

            if (candidateTargets.Count == 0) return null;

            foreach (var target in candidateTargets)
            {
                var targetWrapper = new TargetWrapper(target);

                foreach (var attack in rangedAttacks)
                {
                    float cost = CombatAPI.GetAbilityAPCost(attack);
                    if (cost > remainingAP) continue;

                    // AoE 아군 피해 체크
                    if (attack.Blueprint?.CanTargetFriends == true)
                    {
                        bool allyNearTarget = situation.Allies.Any(ally =>
                            ally != null && !ally.LifeState.IsDead &&
                            CombatAPI.GetDistance(ally, target) < 4f);

                        if (allyNearTarget)
                        {
                            Main.LogDebug($"[Support] Fallback: Skipping {attack.Name} - ally near target");
                            continue;
                        }
                    }

                    string reason;
                    if (CombatAPI.CanUseAbilityOn(attack, targetWrapper, out reason))
                    {
                        remainingAP -= cost;

                        // ★ MP 추적
                        float mpCost = CombatAPI.GetAbilityMPCost(attack);
                        remainingMP -= mpCost;
                        if (remainingMP < 0) remainingMP = 0;

                        Main.Log($"[Support] Fallback attack: {attack.Name} -> {target.CharacterName}");
                        return PlannedAction.Attack(attack, target, $"Safe attack on {target.CharacterName}", cost);
                    }
                }
            }

            return null;
        }

        #endregion
    }
}
