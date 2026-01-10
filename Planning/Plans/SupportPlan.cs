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

            // Phase 2: 아군 힐 (Confidence 기반 임계값 조정)
            // ★ v3.2.15: TeamBlackboard 기반 힐 대상 선택 (팀 전체 최적화)
            // ★ v3.2.20: 신뢰도가 낮으면 더 빨리 힐, 높으면 늦게 힐
            float confidence = GetTeamConfidence();
            float healThreshold = confidence > 0.7f ? 30f :  // 높은 신뢰도: HP<30%만 힐
                                  confidence > 0.3f ? 50f :  // 보통: HP<50%
                                                      70f;   // 낮은 신뢰도: HP<70%도 힐

            var woundedAlly = TeamBlackboard.Instance.GetMostWoundedAlly();
            if (woundedAlly == null || CombatAPI.GetHPPercent(woundedAlly) >= 80f)
            {
                woundedAlly = FindWoundedAlly(situation, healThreshold);  // Confidence 기반 임계값
            }
            if (woundedAlly != null)
            {
                var allyHealAction = PlanAllyHeal(situation, woundedAlly, ref remainingAP);
                if (allyHealAction != null)
                {
                    actions.Add(allyHealAction);
                }
            }

            // ★ v3.1.17: Phase 2.5 - AOE 힐 (부상 아군 2명 이상)
            var woundedAlliesForAoe = situation.Allies
                .Where(a => a != null && a.IsConscious)
                .Where(a => CombatAPI.GetHPPercent(a) < 70f)
                .ToList();

            if (woundedAlliesForAoe.Count >= 2)
            {
                var aoeHealAction = PlanAoEHeal(situation, ref remainingAP);
                if (aoeHealAction != null)
                {
                    actions.Add(aoeHealAction);
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

            // ★ v3.6.2: Phase 4.3 - AOE 버프 (아군 2명 이상 근처, 6타일 ≈ 8m)
            int nearbyAllies = situation.Allies.Count(a =>
                a != null && a.IsConscious &&
                CombatCache.GetDistanceInTiles(situation.Unit, a) <= 6f);

            if (nearbyAllies >= 2)
            {
                var aoeBuffAction = PlanAoEBuff(situation, ref remainingAP);
                if (aoeBuffAction != null)
                {
                    actions.Add(aoeBuffAction);
                }
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

            // ★ v3.5.37: Phase 5.5 - AOE 공격 기회 (Support도 DangerousAoE 사용)
            bool didPlanAttack = false;
            var pointAoEAttacks = situation.AvailableAttacks
                .Where(a => CombatAPI.IsPointTargetAbility(a) || AbilityDatabase.IsDangerousAoE(a))
                .ToList();
            if (situation.HasLivingEnemies && pointAoEAttacks.Count > 0)
            {
                bool useAoEOptimization = situation.CharacterSettings?.UseAoEOptimization ?? true;
                int minEnemies = situation.CharacterSettings?.MinEnemiesForAoE ?? 2;
                bool hasAoEOpportunity = false;

                if (useAoEOptimization)
                {
                    // 클러스터 기반 AOE 기회 탐지
                    foreach (var aoEAbility in pointAoEAttacks)
                    {
                        float aoERadius = CombatAPI.GetAoERadius(aoEAbility);
                        var clusters = ClusterDetector.FindClusters(situation.Enemies, aoERadius);
                        if (clusters.Any(c => c.Count >= minEnemies))
                        {
                            hasAoEOpportunity = true;
                            break;
                        }
                    }
                }
                else
                {
                    // ★ v3.6.2: 레거시 경로도 타일 단위로 통일 (6타일 ≈ 8m)
                    int nearbyEnemies = situation.Enemies.Count(e =>
                        e != null && e.IsConscious &&
                        CombatCache.GetDistanceInTiles(situation.Unit, e) <= 6f);
                    hasAoEOpportunity = nearbyEnemies >= minEnemies;
                }

                if (hasAoEOpportunity)
                {
                    var aoE = PlanAoEAttack(situation, ref remainingAP);
                    if (aoE != null)
                    {
                        actions.Add(aoE);
                        didPlanAttack = true;
                        Main.Log($"[Support] Phase 5.5: AOE attack planned");
                    }
                }
            }

            // ★ v3.0.57: Phase 6 - SequenceOptimizer 기반 최적 공격 시퀀스 선택
            // "현재 위치에서 공격" vs "후퇴 → 공격" 조합을 점수화하여 비교
            int attacksPlanned = 0;
            var plannedTargetIds = new HashSet<string>();
            var plannedAbilityGuids = new HashSet<string>();

            // ★ v3.6.14: AP >= 0 으로 완화 (bonus usage 공격은 0 AP로 사용 가능)
            while (remainingAP >= 0f && situation.HasHittableEnemies && attacksPlanned < MAX_ATTACKS_PER_PLAN)
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
            // ★ v3.5.80: didPlanAttack 전달
            if (situation.HasPerformedFirstAction || didPlanAttack)
            {
                var postAction = PlanPostAction(situation, ref remainingAP, didPlanAttack);
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
                    // ★ v3.5.22: TurnEnding, SpringAttack 능력도 폴백에서 제외
                    var timing = AbilityDatabase.GetTiming(buff);
                    if (timing == AbilityTiming.PreAttackBuff ||
                        timing == AbilityTiming.HeroicAct ||
                        timing == AbilityTiming.RighteousFury ||
                        timing == AbilityTiming.TurnEnding)
                    {
                        Main.LogDebug($"[Support] Phase 7.5: Skip {buff.Name} (timing={timing} not suitable for fallback)");
                        continue;
                    }

                    // ★ v3.5.22: SpringAttack 능력은 조건 충족 시에만 TurnEnding에서 사용
                    if (AbilityDatabase.IsSpringAttackAbility(buff))
                    {
                        Main.LogDebug($"[Support] Phase 7.5: Skip {buff.Name} (SpringAttack - use in TurnEnding only)");
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

            // ★ v3.5.35: Phase 8 (TurnEnding) → 맨 마지막으로 이동
            // TurnEnding 능력은 턴을 종료시키므로 다른 모든 행동 후에 계획해야 함

            // Phase 8.5: 행동 완료 후 안전 이동
            // ★ v3.2.25: 전선 기반 안전 거리 - 전선 앞에 있으면 후퇴 필요
            bool alreadyHasMoveAction = actions.Any(a => a.Type == ActionType.Move);

            // ★ v3.0.55: remainingMP 체크 - 계획된 능력들의 MP 코스트 반영
            // 화염 수류탄 등 ClearMPAfterUse 능력은 이미 remainingMP=0으로 설정됨
            if (remainingMP <= 0)
            {
                Main.LogDebug($"[Support] Skip safe retreat - no remaining MP after planned abilities");
            }

            if (!alreadyHasMoveAction && remainingMP > 0 && situation.CanMove && situation.PrefersRanged)
            {
                bool needsRetreat = false;
                string retreatReason = "";

                // 기존: 적이 가까우면 후퇴
                if (situation.NearestEnemy != null && situation.NearestEnemyDistance < situation.MinSafeDistance * 1.2f)
                {
                    needsRetreat = true;
                    retreatReason = $"enemy too close ({situation.NearestEnemyDistance:F1}m)";
                }

                // ★ v3.2.25: 전선 앞(0m 이상)에 있으면 후퇴 필요
                if (situation.InfluenceMap != null && situation.InfluenceMap.IsValid)
                {
                    float frontlineDist = situation.InfluenceMap.GetFrontlineDistance(situation.Unit.Position);
                    // 전선 앞에 있거나 전선에 너무 가까우면 (-5m 이상)
                    if (frontlineDist > -5f)
                    {
                        needsRetreat = true;
                        retreatReason = $"too close to frontline ({frontlineDist:F1}m)";
                    }
                }

                if (needsRetreat)
                {
                    var safeRetreatAction = PlanPostActionSafeRetreat(situation);
                    if (safeRetreatAction != null)
                    {
                        actions.Add(safeRetreatAction);
                        alreadyHasMoveAction = true;
                        Main.Log($"[Support] Post-action safe retreat: {retreatReason}");
                    }
                }
            }

            // ★ Phase 9: 이동 또는 GapCloser (공격 불가 시)
            // ★ v3.0.48: Support도 GapCloser 지원
            // ★ v3.0.55: remainingMP 체크 - 계획된 능력들의 MP 코스트 반영
            // ★ v3.0.90: 공격 계획 실패 시에도 이동 허용
            // ★ v3.0.99: MP 회복 예측 후 이동 가능
            // ★ v3.1.01: predictedMP를 MovementAPI에 전달하여 reachable tiles 계산에 사용
            // ★ v3.5.36: GapCloser도 이동으로 취급 (중복 계획 방지)
            bool hasMoveInPlan = actions.Any(a => a.Type == ActionType.Move ||
                (a.Type == ActionType.Attack && a.Ability != null && AbilityDatabase.IsGapCloser(a.Ability)));
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

                    // ★ v3.1.24: 이동 목적지 추출하여 Post-move 공격에 전달
                    if (reservedAP > 0 && situation.NearestEnemy != null)
                    {
                        UnityEngine.Vector3? moveDestination = moveOrGapCloser.Target?.Point;
                        var postMoveAttack = PlanPostMoveAttack(situation, situation.NearestEnemy, ref remainingAP, moveDestination);
                        if (postMoveAttack != null)
                        {
                            actions.Add(postMoveAttack);
                            Main.Log($"[Support] Added post-move attack (from destination={moveDestination.HasValue})");
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

            // ★ v3.1.24: Phase 10 - 최종 AP 활용 (모든 시도 실패 후)
            if (remainingAP >= 1f && actions.Count > 0)
            {
                var finalAction = PlanFinalAPUtilization(situation, ref remainingAP);
                if (finalAction != null)
                {
                    actions.Add(finalAction);
                    Main.Log($"[Support] Phase 10: Final AP utilization - {finalAction.Ability?.Name}");
                }
            }

            // ★ v3.5.35: Phase 11 - 턴 종료 스킬 (항상 마지막!)
            // TurnEnding 능력은 턴을 즉시 종료하므로 반드시 마지막에 배치
            var turnEndAction = PlanTurnEndingAbility(situation, ref remainingAP);
            if (turnEndAction != null)
            {
                actions.Add(turnEndAction);
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
            // ★ v3.5.88: 0 AP 공격 수 전달 (Break Through → Slash 감지용)
            int zeroAPAttackCount = CombatAPI.GetZeroAPAttacks(situation.Unit).Count;
            return new TurnPlan(actions, priority, reasoning, situation.HPPercent, situation.NearestEnemyDistance,
                situation.HittableEnemies?.Count ?? 0, situation.CurrentAP, situation.CurrentMP, zeroAPAttackCount);
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
            // ★ v3.2.15: 팀 전술에 따라 버프 대상 우선순위 조정
            var tactic = TeamBlackboard.Instance.CurrentTactic;
            var prioritizedTargets = new List<BaseUnitEntity>();

            if (tactic == TacticalSignal.Retreat)
            {
                // 후퇴: 가장 위험한 아군 우선 (생존 버프)
                var mostWounded = TeamBlackboard.Instance.GetMostWoundedAlly();
                if (mostWounded != null)
                {
                    prioritizedTargets.Add(mostWounded);
                    Main.LogDebug($"[Support] Phase 4: Retreat tactic - buff wounded ally {mostWounded.CharacterName}");
                }
            }
            else if (tactic == TacticalSignal.Attack)
            {
                // 공격: DPS 우선 버프 (HP 50% 이상인 DPS)
                foreach (var ally in situation.Allies.Where(a => a != null && !a.LifeState.IsDead))
                {
                    var settings = ModSettings.Instance?.GetOrCreateSettings(ally.UniqueId, ally.CharacterName);
                    if (settings?.Role == AIRole.DPS && CombatAPI.GetHPPercent(ally) > 50f)
                    {
                        prioritizedTargets.Add(ally);
                    }
                }
                if (prioritizedTargets.Count > 0)
                {
                    Main.LogDebug($"[Support] Phase 4: Attack tactic - buff DPS first");
                }
            }

            // 기본 우선순위 (Defend 또는 위에서 대상 없을 때): Tank > DPS > 본인 > 기타
            // 1. Tank 역할 먼저
            foreach (var ally in situation.Allies.Where(a => a != null && !a.LifeState.IsDead))
            {
                var settings = ModSettings.Instance?.GetOrCreateSettings(ally.UniqueId, ally.CharacterName);
                if (settings?.Role == AIRole.Tank && !prioritizedTargets.Contains(ally))
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
            if (!prioritizedTargets.Contains(situation.Unit))
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

                    // ★ v3.6.3: AoE 아군 피해 체크 (타일 단위로 수정)
                    if (attack.Blueprint?.CanTargetFriends == true)
                    {
                        bool allyNearTarget = situation.Allies.Any(ally =>
                            ally != null && !ally.LifeState.IsDead &&
                            CombatCache.GetDistanceInTiles(ally, target) < 3f);  // 3타일 ≈ 4m

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
