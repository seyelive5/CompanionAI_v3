using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Enums;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using Kingmaker.Pathfinding;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning.Plans
{
    /// <summary>
    /// ★ v3.0.47: Support 전략
    /// ★ v3.8.67: SequenceOptimizer 제거 → Phase 순서 + UtilityScorer 감점으로 대체
    /// 힐 → 버프 → 디버프 → ClearMP 선제후퇴 → 안전 공격 → 후퇴
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

            // ★ v3.8.41: Phase 0 - 잠재력 초월 궁극기 (최우선)
            if (CombatAPI.HasFreeUltimateBuff(situation.Unit))
            {
                var ultimateAction = PlanUltimate(situation, ref remainingAP);
                if (ultimateAction != null)
                {
                    actions.Add(ultimateAction);
                    return new TurnPlan(actions, TurnPriority.Critical, "Support ultimate (Transcend Potential)");
                }
                // ★ v3.8.42: 궁극기 실패 시 즉시 EndTurn (WarhammerAbilityRestriction으로 다른 능력 사용 불가)
                Main.Log("[Support] Ultimate failed during Transcend Potential - ending turn");
                actions.Add(PlannedAction.EndTurn("Support no ultimate available"));
                return new TurnPlan(actions, TurnPriority.EndTurn, "Support ultimate failed (Transcend Potential)");
            }

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

            // ★ v3.7.02: Phase 1.75 - Familiar support (키스톤 확산 최우선)
            // ★ v3.7.07: 실제 사용된 능력 GUID 추적 (Phase 4에서 스킵 판단용)
            // ★ v3.7.09: 버프 + 디버프 (Raven Warp Relay) 모두 추적
            // ★ v3.7.12: 모든 사역마 능력 통합 (Priority Signal, Fast, Hex, Cycle, Roam)
            var usedKeystoneAbilityGuids = new HashSet<string>();
            bool usedWarpRelay = false;

            if (situation.HasFamiliar)
            {
                // ★ v3.7.12: 1. Servo-Skull Priority Signal (선제 버프)
                var prioritySignal = PlanFamiliarPrioritySignal(situation, ref remainingAP);
                if (prioritySignal != null)
                    actions.Add(prioritySignal);

                // ★ v3.7.12: 2. Mastiff Fast (Apprehend 전 이동 버프)
                var mastiffFast = PlanFamiliarFast(situation, ref remainingAP);
                if (mastiffFast != null)
                    actions.Add(mastiffFast);

                // 3. Relocate: 사역마를 최적 위치로 이동 (Mastiff 제외)
                var familiarRelocate = PlanFamiliarRelocate(situation, ref remainingAP);
                if (familiarRelocate != null)
                    actions.Add(familiarRelocate);

                // ★ v3.7.02: 4. 키스톤 능력 루프 (Servo-Skull/Raven)
                // ★ v3.7.09: 버프 + 디버프 (Raven Warp Relay) 모두 처리
                var keystoneActions = PlanAllFamiliarKeystoneBuffs(situation, ref remainingAP);
                if (keystoneActions.Count > 0)
                {
                    actions.AddRange(keystoneActions);
                    Main.Log($"[Support] Phase 1.75: {keystoneActions.Count} keystone abilities planned (buffs/debuffs)");

                    // ★ v3.7.07: 실제 사용된 능력 GUID 추적
                    foreach (var action in keystoneActions)
                    {
                        if (action.Ability?.Blueprint != null)
                        {
                            string guid = action.Ability.Blueprint.AssetGuid?.ToString();
                            if (!string.IsNullOrEmpty(guid))
                                usedKeystoneAbilityGuids.Add(guid);
                        }
                    }

                    // ★ v3.7.12: Raven Warp Relay 사용 여부 추적 (Cycle용)
                    usedWarpRelay = situation.FamiliarType == PetType.Raven;
                }

                // ★ v3.7.12: 5. Raven Cycle (Warp Relay 후 재시전)
                if (usedWarpRelay)
                {
                    var cycle = PlanFamiliarCycle(situation, ref remainingAP, usedWarpRelay);
                    if (cycle != null)
                        actions.Add(cycle);
                }

                // ★ v3.7.12: 6. Raven Hex (적 디버프)
                var hex = PlanFamiliarHex(situation, ref remainingAP);
                if (hex != null)
                    actions.Add(hex);

                // 7. Mastiff: Apprehend → JumpClaws → Claws → Roam (폴백 체인)
                // ★ v3.7.14: JumpClaws, Claws 폴백 추가
                var apprehend = PlanFamiliarApprehend(situation, ref remainingAP);
                if (apprehend != null)
                    actions.Add(apprehend);
                else
                {
                    var jumpClaws = PlanFamiliarJumpClaws(situation, ref remainingAP);
                    if (jumpClaws != null)
                        actions.Add(jumpClaws);
                    else
                    {
                        var mastiffClaws = PlanFamiliarClaws(situation, ref remainingAP);
                        if (mastiffClaws != null)
                            actions.Add(mastiffClaws);
                        else
                        {
                            var roam = PlanFamiliarRoam(situation, ref remainingAP);
                            if (roam != null)
                                actions.Add(roam);
                        }
                    }
                }

                // 8. Mastiff Protect: 위협받는 아군 호위
                var familiarProtect = PlanFamiliarProtect(situation, ref remainingAP);
                if (familiarProtect != null)
                    actions.Add(familiarProtect);

                // 9. Eagle Obstruct (Support에서도 사용)
                var obstruct = PlanFamiliarObstruct(situation, ref remainingAP);
                if (obstruct != null)
                    actions.Add(obstruct);

                // ★ v3.7.14: 10. Eagle Blinding Dive: 이동+실명 공격
                var blindingDive = PlanFamiliarBlindingDive(situation, ref remainingAP);
                if (blindingDive != null)
                    actions.Add(blindingDive);

                // 11. Eagle Screen: HP 낮은 아군 보호
                var familiarScreen = PlanFamiliarScreen(situation, ref remainingAP);
                if (familiarScreen != null)
                    actions.Add(familiarScreen);

                // 12. Eagle Aerial Rush: 돌진 공격 (경로상 적 타격)
                var aerialRush = PlanFamiliarAerialRush(situation, ref remainingAP);
                if (aerialRush != null)
                    actions.Add(aerialRush);

                // ★ v3.7.14: 13. Eagle Claws: 폴백 근접 공격
                if (blindingDive == null && aerialRush == null)
                {
                    var eagleClaws = PlanFamiliarClaws(situation, ref remainingAP);
                    if (eagleClaws != null)
                        actions.Add(eagleClaws);
                }
            }

            // ★ v3.8.67: ClearMP 선제 후퇴는 Phase 5.8에서 처리
            // 일반 후퇴는 Phase 8.5에서 처리

            // Phase 2: 아군 힐 (사용자 설정 + Confidence 보정)
            // ★ v3.2.15: TeamBlackboard 기반 힐 대상 선택 (팀 전체 최적화)
            // ★ v3.2.20: 신뢰도가 낮으면 더 빨리 힐, 높으면 늦게 힐
            // ★ v3.9.46: HealAtHPPercent 사용자 설정 연동 (UI 슬라이더 20-80%)
            float confidence = GetTeamConfidence();
            int userHealSetting = situation.CharacterSettings?.HealAtHPPercent ?? 50;
            // 사용자 설정값 기반 + Confidence 보정 (-20 ~ +20)
            float confidenceModifier = confidence > 0.7f ? -20f :   // 높은 신뢰도: 좀 더 보수적
                                       confidence > 0.3f ? 0f :     // 보통: 설정값 그대로
                                                           20f;    // 낮은 신뢰도: 좀 더 적극적
            float healThreshold = Math.Max(20f, Math.Min(80f, userHealSetting + confidenceModifier));

            // ★ v3.7.12: Vitality Signal (Servo-Skull AoE 힐) - 개별 힐보다 우선
            // 여러 아군이 부상 시 효율적
            if (situation.FamiliarType == PetType.ServoskullSwarm)
            {
                var vitalitySignal = PlanFamiliarVitalitySignal(situation, ref remainingAP);
                if (vitalitySignal != null)
                {
                    actions.Add(vitalitySignal);
                    Main.Log($"[Support] Phase 2: Vitality Signal (AoE heal) planned");
                }
            }

            var woundedAlly = TeamBlackboard.Instance.GetMostWoundedAlly();
            if (woundedAlly == null || CombatCache.GetHPPercent(woundedAlly) >= 80f)
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
                // ★ v3.9.46: 힐 실패 시 이동 후 힐 시도 (메디킷 Touch 사거리 대응)
                else if (remainingMP > 0)
                {
                    var moveHealActions = PlanMoveToHeal(situation, woundedAlly, ref remainingAP, remainingMP);
                    if (moveHealActions != null)
                    {
                        actions.AddRange(moveHealActions);
                        remainingMP = 0;  // 이동 소모 반영 (보수적)
                    }
                }
            }

            // ★ v3.1.17: Phase 2.5 - AOE 힐 (부상 아군 2명 이상)
            var woundedAlliesForAoe = situation.Allies
                .Where(a => a != null && a.IsConscious)
                .Where(a => CombatCache.GetHPPercent(a) < 70f)
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
            // ★ v3.7.07: 실제 사용된 키스톤 버프만 스킵 (실패한 건 아군에게 시전)
            // ★ v3.8.51: (버프,타겟) 쌍 추적으로 같은 버프를 여러 아군에게 사용 가능
            // ★ v3.8.16: 턴 부여 능력 중복 방지 (같은 대상에게 쳐부숴라 여러 번 계획 방지)
            var keystoneOnlyGuids = new HashSet<string>(usedKeystoneAbilityGuids);  // ★ v3.8.51: 키스톤 GUID만
            var plannedTurnGrantTargets = new HashSet<string>();  // ★ v3.8.16: 턴 부여 대상 추적
            var plannedBuffTargetPairs = new HashSet<string>();   // ★ v3.8.51: (buffGuid:targetId) 쌍
            while (remainingAP >= 1f)
            {
                var allyBuffAction = PlanAllyBuff(situation, ref remainingAP, keystoneOnlyGuids, plannedTurnGrantTargets, plannedBuffTargetPairs);
                if (allyBuffAction == null) break;

                // ★ v3.8.51: (버프, 타겟) 쌍 추적
                string buffGuid = allyBuffAction.Ability?.Blueprint?.AssetGuid?.ToString();
                var buffTarget = allyBuffAction.Target?.Entity as BaseUnitEntity;
                string targetId = buffTarget?.UniqueId ?? buffTarget?.CharacterName ?? "unknown";
                if (!string.IsNullOrEmpty(buffGuid))
                {
                    plannedBuffTargetPairs.Add($"{buffGuid}:{targetId}");
                }

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

            // ★ v3.5.37: Phase 5.5 - AOE 공격 기회 (모든 AoE 타입)
            // ★ v3.8.96: AvailableAoEAttacks 캐시 사용 + Unit-targeted AoE 추가
            bool didPlanAttack = false;
            // ★ v3.8.44: 공격 실패 이유 추적 (이동 Phase에 전달)
            var attackContext = new AttackPhaseContext();
            // ★ v3.9.28: 이동이 이미 계획됨 → AttackPlanner에 pending move 알림
            if (CollectionHelper.Any(actions, a => a.Type == ActionType.Move))
                attackContext.HasPendingMove = true;
            if (situation.HasLivingEnemies && situation.HasAoEAttacks)
            {
                bool useAoEOptimization = situation.CharacterSettings?.UseAoEOptimization ?? true;
                int minEnemies = situation.CharacterSettings?.MinEnemiesForAoE ?? 2;
                bool hasAoEOpportunity = false;

                if (useAoEOptimization)
                {
                    // ★ v3.8.96: 캐시된 AvailableAoEAttacks 사용 (인라인 LINQ 제거)
                    foreach (var aoEAbility in situation.AvailableAoEAttacks)
                    {
                        float aoERadius = CombatAPI.GetAoERadius(aoEAbility);
                        if (aoERadius <= 0) aoERadius = 5f;
                        var clusters = ClusterDetector.FindClusters(situation.Enemies, aoERadius);
                        if (clusters.Any(c => c.Count >= minEnemies))
                        {
                            hasAoEOpportunity = true;
                            if (Main.IsDebugEnabled) Main.LogDebug($"[Support] Phase 5.5: Cluster found for {aoEAbility.Name} (category={CombatAPI.GetAttackCategory(aoEAbility)})");
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
                    // Point-target AoE 시도
                    var aoE = PlanAoEAttack(situation, ref remainingAP);
                    if (aoE != null)
                    {
                        actions.Add(aoE);
                        didPlanAttack = true;
                        Main.Log($"[Support] Phase 5.5: Point-target AOE planned");
                    }

                    // ★ v3.8.96: Unit-targeted AoE 시도 (Burst, Scatter, 기타 모든 유닛 타겟 AoE)
                    if (!didPlanAttack)
                    {
                        var unitAoE = PlanUnitTargetedAoE(situation, ref remainingAP);
                        if (unitAoE != null)
                        {
                            actions.Add(unitAoE);
                            didPlanAttack = true;
                            Main.Log($"[Support] Phase 5.5b: Unit-targeted AOE planned");
                        }
                    }
                }
            }

            // ★ v3.9.08: Phase 5.5.5: AoE 재배치 (Phase 5.5/5.5b 실패 시)
            if (!didPlanAttack && remainingAP >= 1f && remainingMP > 0 && situation.HasAoEAttacks
                && !actions.Any(a => a.Type == ActionType.Move))
            {
                var (aoEMoveAction, aoEAttackAction) = PlanAoEWithReposition(
                    situation, ref remainingAP, ref remainingMP);
                if (aoEMoveAction != null && aoEAttackAction != null)
                {
                    actions.Add(aoEMoveAction);
                    actions.Add(aoEAttackAction);
                    didPlanAttack = true;

                    var moveDest = aoEMoveAction.MoveDestination ?? aoEMoveAction.Target?.Point;
                    if (moveDest.HasValue)
                        RecalculateHittableFromDestination(situation, moveDest.Value);

                    Main.Log($"[Support] Phase 5.5.5: AoE reposition planned");
                }
            }

            // ★ v3.8.67: Phase 5.8 - ClearMP 능력 사용 전 선제적 후퇴
            // ClearMP 능력 사용 후 MP=0이 되면 Phase 8.5 후퇴도 불가능하므로
            // 공격 전에 안전 위치로 이동해야 함 (BasePlan.PlanPreemptiveRetreatForClearMPAbility 활성화)
            if (!actions.Any(a => a.Type == ActionType.Move))
            {
                var clearMPRetreat = PlanPreemptiveRetreatForClearMPAbility(situation, ref remainingMP);
                if (clearMPRetreat != null)
                {
                    actions.Add(clearMPRetreat);
                    Main.Log($"[Support] Phase 5.8: Preemptive retreat before ClearMP ability");

                    // ★ v3.8.76: 후퇴 후 HittableEnemies 재계산 (Phase 6 공격 전)
                    var retreatDest = clearMPRetreat.MoveDestination ?? clearMPRetreat.Target?.Point;
                    if (retreatDest.HasValue)
                    {
                        RecalculateHittableFromDestination(situation, retreatDest.Value);
                    }
                }
            }

            // ══════════════════════════════════════════════════════════════
            // Phase 5.9: 전략 옵션 평가 (공격 전 이동 필요 여부 결정)
            // ★ v3.8.76: TacticalOptionEvaluator - Phase 5.8 ClearMP 후퇴와 협력
            // Phase 5.8이 이미 이동했으면 MoveToAttack은 자동 스킵됨
            // ══════════════════════════════════════════════════════════════
            // ★ v3.8.76: Phase 5.8에서 이미 이동했으면 전략 평가 자체를 스킵
            // ApplyTacticalStrategy 내부에서 RecalculateHittable이 실행되므로,
            // 이동을 추가하지 않을 건데 RecalculateHittable만 실행되면 HittableEnemies가 잘못됨
            bool alreadyMoved = actions.Any(a => a.Type == ActionType.Move);
            TacticalEvaluation tacticalEval = null;
            if (!alreadyMoved)
            {
                tacticalEval = EvaluateTacticalOptions(situation);
                if (tacticalEval != null && tacticalEval.WasEvaluated)
                {
                    bool shouldMoveBeforeAttack;
                    bool shouldDeferRetreat;
                    var tacticalMoveAction = ApplyTacticalStrategy(tacticalEval, situation,
                        out shouldMoveBeforeAttack, out shouldDeferRetreat);

                    if (tacticalMoveAction != null)
                    {
                        actions.Add(tacticalMoveAction);
                        Main.Log($"[Support] Phase 5.9: Tactical pre-attack move");
                    }
                }
            }

            // ★ v3.8.67: Phase 6 - 원거리 공격 계획
            // 기존 SequenceOptimizer 제거 → PlanSafeRangedAttack 직접 사용
            // ClearMP 안전/후퇴 판단은 UtilityScorer + Phase 5.8이 담당
            int attacksPlanned = 0;
            var plannedTargetIds = new HashSet<string>();
            var plannedAbilityGuids = new HashSet<string>();

            // ★ v3.6.14: AP >= 0 으로 완화 (bonus usage 공격은 0 AP로 사용 가능)
            while (remainingAP >= 0f && situation.HasHittableEnemies && attacksPlanned < MAX_ATTACKS_PER_PLAN)
            {
                var attackAction = PlanSafeRangedAttackFallback(situation, ref remainingAP, ref remainingMP,
                    excludeTargetIds: plannedTargetIds, excludeAbilityGuids: plannedAbilityGuids);
                if (attackAction == null) break;

                actions.Add(attackAction);
                didPlanAttack = true;
                attacksPlanned++;

                var targetEntity = attackAction.Target?.Entity as BaseUnitEntity;
                // ★ v3.6.22: Hittable 적이 2명 이상일 때만 타겟 제외
                if (targetEntity != null)
                {
                    if (situation.HittableEnemies.Count > 1)
                    {
                        plannedTargetIds.Add(targetEntity.UniqueId);
                    }
                    else
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[Support] Phase 6: Allow re-attack on {targetEntity.CharacterName} (only 1 hittable enemy)");
                    }
                }

                // ★ v3.8.30: 적이 1명일 때는 능력도 제외하지 않음 (동일 능력으로 재공격 허용)
                if (attackAction.Ability != null && situation.HittableEnemies.Count > 1)
                {
                    var guid = attackAction.Ability.Blueprint?.AssetGuid?.ToString();
                    if (!string.IsNullOrEmpty(guid))
                        plannedAbilityGuids.Add(guid);
                }
            }

            // ★ v3.8.44: 공격 실패 시 context 수집 (이동 Phase에서 활용)
            if (!didPlanAttack)
            {
                var probeTarget = situation.BestTarget ?? situation.HittableEnemies?.FirstOrDefault();
                if (probeTarget != null)
                {
                    SelectBestAttack(situation, probeTarget, null, attackContext);
                    if (Main.IsDebugEnabled) Main.LogDebug($"[Support] AttackContext probe: {attackContext}");
                }
            }

            // ★ v3.8.72: Hittable mismatch 사후 보정
            HandleHittableMismatch(situation, didPlanAttack, attackContext);

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
            // ★ v3.8.98: 근접 MoveOnly 전략 시 fallback 버프 스킵
            bool skipFallbackForMelee = !situation.PrefersRanged &&
                tacticalEval?.ChosenStrategy == TacticalStrategy.MoveOnly &&
                situation.HasLivingEnemies;

            if (skipFallbackForMelee)
            {
                Main.Log($"[Support] Phase 7.5: Skipping fallback buffs (melee MoveOnly — save for post-move attack)");
            }
            else if (!didPlanAttack && remainingAP >= 1f && situation.AvailableBuffs.Count > 0)
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
                        if (Main.IsDebugEnabled) Main.LogDebug($"[Support] Phase 7.5: Skip {buff.Name} (timing={timing} not suitable for fallback)");
                        continue;
                    }

                    // ★ v3.5.22: SpringAttack 능력은 조건 충족 시에만 TurnEnding에서 사용
                    if (AbilityDatabase.IsSpringAttackAbility(buff))
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[Support] Phase 7.5: Skip {buff.Name} (SpringAttack - use in TurnEnding only)");
                        continue;
                    }

                    float cost = CombatAPI.GetAbilityAPCost(buff);
                    if (cost > remainingAP) continue;

                    if (AllyStateCache.HasBuff(situation.Unit, buff)) continue;

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
                if (Main.IsDebugEnabled) Main.LogDebug($"[Support] Skip safe retreat - no remaining MP after planned abilities");
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
            // ★ v3.7.06: 사역마 Master는 아군 방향으로 이동 (버프 시전을 위해)
            bool hasMoveInPlan = actions.Any(a => a.Type == ActionType.Move ||
                (a.Type == ActionType.Attack && a.Ability != null && AbilityDatabase.IsGapCloser(a.Ability)));
            // ★ v3.8.45: 원거리 + AvailableAttacks=0 → 적에게 접근 무의미
            bool noAttackNoApproach = situation.PrefersRanged && situation.AvailableAttacks.Count == 0;
            // NeedsReposition도 noAttackNoApproach 적용
            bool needsMovement = (situation.NeedsReposition || (!didPlanAttack && situation.HasLivingEnemies)) && !noAttackNoApproach;
            bool canMove = situation.CanMove || remainingMP > 0;
            // ★ v3.9.22: GapCloser(돌격 등)는 AP 기반 — MP 없어도 사용 가능
            bool hasGapClosers = !situation.PrefersRanged &&
                situation.AvailableAttacks.Any(a => AbilityDatabase.IsGapCloser(a));

            // ★ v3.7.06: 사역마 Master가 사역마/아군과 너무 멀면 접근
            bool needsMoveToAlly = false;
            if (!hasMoveInPlan && canMove && remainingMP > 0 && situation.HasFamiliar &&
                (situation.FamiliarType == PetType.ServoskullSwarm || situation.FamiliarType == PetType.Raven))
            {
                // 사역마와의 거리 체크 (15m 이상이면 버프 시전 불가)
                float distToFamiliar = UnityEngine.Vector3.Distance(
                    situation.Unit.Position, situation.FamiliarPosition);
                if (distToFamiliar > 15f)
                {
                    needsMoveToAlly = true;
                    Main.Log($"[Support] Phase 9: Too far from familiar ({distToFamiliar:F1}m > 15m), moving toward allies");
                }
            }

            if (needsMoveToAlly && remainingMP > 0)
            {
                // 아군 밀집 지역 방향으로 이동
                var moveToAlly = PlanMoveTowardAllies(situation, remainingMP);
                if (moveToAlly != null)
                {
                    actions.Add(moveToAlly);
                    hasMoveInPlan = true;
                    Main.Log($"[Support] Phase 9: Moving toward allies for buff range");
                }
            }
            // ★ v3.9.22: GapCloser는 MP 없이도 진입 허용 (AP 기반 이동)
            else if (!hasMoveInPlan && needsMovement && ((canMove && remainingMP > 0) || hasGapClosers))
            {
                Main.Log($"[Support] Phase 9: Trying move (attack planned={didPlanAttack}, predictedMP={remainingMP:F1})");
                // ★ v3.0.90: 공격 실패 시 forceMove=true로 이동 강제
                // ★ v3.8.44: HasHittableEnemies → attackContext.ShouldForceMove (실패 이유 기반)
                bool forceMove = !didPlanAttack && attackContext.ShouldForceMove;
                if (Main.IsDebugEnabled) Main.LogDebug($"[Support] Phase 9: {attackContext}, forceMove={forceMove}");
                // ★ v3.1.00: MP 회복 예측 후 situation.CanMove=False여도 이동 가능
                bool bypassCanMoveCheck = !situation.CanMove && remainingMP > 0;
                // ★ v3.1.01: remainingMP를 MovementAPI에 전달
                // ★ v3.8.44: attackContext 전달 - 능력 사거리 기반 이동 위치 계산
                var moveOrGapCloser = PlanMoveOrGapCloser(situation, ref remainingAP, forceMove, bypassCanMoveCheck, remainingMP, attackContext);
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

            // ★ v3.8.74: Phase 8.7 - Tactical Reposition (공격 쿨다운 시 다음 턴 최적 위치)
            if (!hasMoveInPlan && noAttackNoApproach && remainingMP > 0 && situation.HasLivingEnemies)
            {
                var tacticalRepos = PlanTacticalReposition(situation, remainingMP);
                if (tacticalRepos != null)
                {
                    actions.Add(tacticalRepos);
                    hasMoveInPlan = true;
                    Main.Log($"[Support] Phase 8.7: Tactical reposition (all attacks on cooldown, MP={remainingMP:F1})");
                }
            }

            // Post-attack phase
            if ((situation.HasAttackedThisTurn || didPlanAttack) && remainingAP >= 1f)
            {
                var postAttackActions = PlanPostAttackActions(situation, ref remainingAP, skipMove: hasMoveInPlan);
                actions.AddRange(postAttackActions);
            }

            // ★ v3.1.24: Phase 10 - 최종 AP 활용 (모든 시도 실패 후)
            // ★ v3.9.06: actions.Count > 0 제한 제거 - DPSPlan v3.8.84와 통일
            // 디버프/마커는 다른 행동 없이도 팀에 기여
            if (remainingAP >= 1f)
            {
                var finalAction = PlanFinalAPUtilization(situation, ref remainingAP);
                if (finalAction != null)
                {
                    actions.Add(finalAction);
                    Main.Log($"[Support] Phase 10: Final AP utilization - {finalAction.Ability?.Name}");
                }
            }

            // ★ v3.8.68: Post-plan 공격 검증 + 복구 (TurnEnding 전에 실행)
            int removedAttacks = ValidateAndRemoveUnreachableAttacks(actions, situation, ref didPlanAttack, ref remainingAP);

            if (removedAttacks > 0 && !didPlanAttack)
            {
                // 모든 공격이 제거됨 → 복구 이동 시도
                bool hasRecoveryMove = actions.Any(a => a.Type == ActionType.Move);
                if (!hasRecoveryMove && situation.HasLivingEnemies && remainingMP > 0)
                {
                    Main.Log($"[Support] ★ Post-validation recovery: attempting movement (AP={remainingAP:F1}, MP={remainingMP:F1})");
                    var recoveryCtx = new AttackPhaseContext { RangeWasIssue = true };
                    bool bypassCanMoveCheck = !situation.CanMove && remainingMP > 0;
                    var recoveryMove = PlanMoveOrGapCloser(situation, ref remainingAP, true, bypassCanMoveCheck, remainingMP, recoveryCtx);
                    if (recoveryMove != null)
                    {
                        actions.Add(recoveryMove);
                        Main.Log($"[Support] ★ Post-validation recovery: movement planned");
                    }
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
            if (Main.IsDebugEnabled) Main.LogDebug($"[Support] Plan complete: AP={remainingAP:F1}, MP={remainingMP:F1} (started with {situation.CurrentMP:F1})");

            // ★ v3.1.09: InitialAP/InitialMP 전달 (리플랜 감지용)
            // ★ v3.5.88: 0 AP 공격 수 전달 (Break Through → Slash 감지용)
            int zeroAPAttackCount = CombatAPI.GetZeroAPAttacks(situation.Unit).Count;
            // ★ v3.9.26: NormalHittableCount 사용 — DangerousAoE 부풀림이 replan을 불필요하게 유발 방지
            return new TurnPlan(actions, priority, reasoning, situation.HPPercent, situation.NearestEnemyDistance,
                situation.NormalHittableCount, situation.CurrentAP, situation.CurrentMP, zeroAPAttackCount);
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

        /// <summary>
        /// ★ v3.9.46: 이동 후 힐 - 힐 사거리 밖의 아군에게 접근하여 힐
        /// Touch 사거리 메디킷 등을 위해 아군 근접 위치로 이동 후 힐 시전
        /// </summary>
        private List<PlannedAction> PlanMoveToHeal(Situation situation, BaseUnitEntity woundedAlly, ref float remainingAP, float remainingMP)
        {
            if (remainingMP <= 0) return null;
            if (situation.AvailableHeals.Count == 0) return null;

            var unit = situation.Unit;
            if (unit == null || woundedAlly == null) return null;

            // 가장 저렴한 사용 가능한 힐 능력 찾기
            AbilityData bestHeal = null;
            float bestHealCost = float.MaxValue;
            int healRange = 0;

            foreach (var heal in situation.AvailableHeals)
            {
                float cost = CombatAPI.GetAbilityAPCost(heal);
                if (cost > remainingAP) continue;

                if (cost < bestHealCost)
                {
                    bestHealCost = cost;
                    bestHeal = heal;
                    healRange = CombatAPI.GetAbilityRangeInTiles(heal);
                }
            }

            if (bestHeal == null) return null;

            // 현재 거리 확인 - 이미 사거리 내면 move-to-heal 불필요 (LOS 문제 등)
            float currentDistTiles = CombatAPI.GetDistanceInTiles(unit, woundedAlly);
            if (currentDistTiles <= healRange)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[Support] MoveToHeal: Already in range ({currentDistTiles:F1} <= {healRange}) but heal failed - likely LOS issue");
                return null;
            }

            // 도달 가능한 타일 획득
            var tiles = MovementAPI.FindAllReachableTilesSync(unit, remainingMP);
            if (tiles == null || tiles.Count == 0) return null;

            // 아군의 힐 사거리 내에 도달 가능한 타일 찾기
            UnityEngine.Vector3? bestPos = null;
            float bestDist = float.MaxValue;

            foreach (var kvp in tiles)
            {
                var cell = kvp.Value;
                if (!cell.IsCanStand) continue;

                var node = kvp.Key as CustomGridNodeBase;
                if (node == null) continue;

                var pos = node.Vector3Position;
                float distToAllyTiles = CombatAPI.MetersToTiles(
                    UnityEngine.Vector3.Distance(pos, woundedAlly.Position));

                // 힐 사거리 내이고, 가장 가까운 타일 선택
                if (distToAllyTiles <= healRange && distToAllyTiles < bestDist)
                {
                    bestDist = distToAllyTiles;
                    bestPos = pos;
                }
            }

            if (!bestPos.HasValue)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[Support] MoveToHeal: No reachable tile within heal range ({healRange} tiles) of {woundedAlly.CharacterName} (current dist={currentDistTiles:F1})");
                return null;
            }

            // ★ v3.5.10: 힐 대상 예약 (중복 힐 방지)
            TeamBlackboard.Instance.ReserveHeal(woundedAlly);

            // Move + Heal 계획
            remainingAP -= bestHealCost;

            var result = new List<PlannedAction>();
            result.Add(PlannedAction.Move(bestPos.Value,
                $"Move to heal {woundedAlly.CharacterName} (range={healRange} tiles)"));
            result.Add(PlannedAction.Heal(bestHeal, woundedAlly,
                $"Heal after move: {woundedAlly.CharacterName}", bestHealCost));

            Main.Log($"[Support] MoveToHeal: Moving {CombatAPI.MetersToTiles(UnityEngine.Vector3.Distance(unit.Position, bestPos.Value)):F1} tiles to heal {woundedAlly.CharacterName} ({bestHeal.Name}, range={healRange})");

            return result;
        }

        // ★ v3.7.93: PlanAllyBuff 메서드는 BasePlan으로 이동
        // SupportPlan에서 BasePlan.PlanAllyBuff(situation, ref remainingAP, usedKeystoneGuids) 호출

        /// <summary>
        /// ★ v3.8.67: Phase 6 공격 계획 (기존 폴백 → 메인 경로로 승격)
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

                    // ★ v3.8.64: AoESafetyChecker 통합 (간이 3타일 체크 → 게임 기반 스캐터 패턴)
                    if (attack.Blueprint?.CanTargetFriends == true)
                    {
                        if (!AoESafetyChecker.IsAoESafeForUnitTarget(attack, situation.Unit, target, situation.Allies))
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[Support] Fallback: Skipping {attack.Name} - ally in scatter zone");
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

        /// <summary>
        /// ★ v3.7.06: 사역마/아군 방향으로 이동 (버프 시전 범위 확보)
        /// </summary>
        private PlannedAction PlanMoveTowardAllies(Situation situation, float remainingMP)
        {
            if (remainingMP <= 0) return null;

            var unit = situation.Unit;
            if (unit == null) return null;

            // 목표 위치 결정: 사역마 위치 또는 아군 밀집 중심
            UnityEngine.Vector3 targetPos;
            string moveReason;

            if (situation.HasFamiliar && situation.Familiar != null)
            {
                // 사역마가 있으면 사역마 위치로 이동
                targetPos = situation.FamiliarPosition;
                var typeName = FamiliarAPI.GetFamiliarTypeName(situation.FamiliarType);
                moveReason = $"Move toward {typeName} for buff range";
            }
            else if (situation.Allies != null && situation.Allies.Any(a => a != null && !a.LifeState.IsDead))
            {
                // 아군 밀집 중심점 계산
                var livingAllies = situation.Allies.Where(a => a != null && !a.LifeState.IsDead).ToList();
                var centerX = livingAllies.Average(a => a.Position.x);
                var centerY = livingAllies.Average(a => a.Position.y);
                var centerZ = livingAllies.Average(a => a.Position.z);
                targetPos = new UnityEngine.Vector3(centerX, centerY, centerZ);
                moveReason = "Move toward ally cluster";
            }
            else
            {
                return null;
            }

            // 현재 거리 확인
            float currentDist = UnityEngine.Vector3.Distance(unit.Position, targetPos);
            if (currentDist <= 10f)  // 이미 충분히 가까움
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[Support] Already close to target position ({currentDist:F1}m)");
                return null;
            }

            // 도달 가능한 타일 획득
            var tiles = MovementAPI.FindAllReachableTilesSync(unit, remainingMP);
            if (tiles == null || tiles.Count == 0)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[Support] No reachable tiles for ally approach");
                return null;
            }

            // 목표 위치에 가장 가까운 타일 찾기
            UnityEngine.Vector3? bestPos = null;
            float bestDist = currentDist;  // 현재보다 가까워야 함

            foreach (var kvp in tiles)
            {
                var cell = kvp.Value;
                if (!cell.IsCanStand) continue;

                var node = kvp.Key as CustomGridNodeBase;
                if (node == null) continue;

                var pos = node.Vector3Position;
                float dist = UnityEngine.Vector3.Distance(pos, targetPos);

                // 현재보다 가깝고, 지금까지 중 최고면 선택
                if (dist < bestDist - 1f)  // 최소 1m 이상 가까워야 함
                {
                    bestDist = dist;
                    bestPos = pos;
                }
            }

            if (bestPos.HasValue)
            {
                float improvement = currentDist - bestDist;
                Main.Log($"[Support] Move toward allies: {currentDist:F1}m -> {bestDist:F1}m (improvement: {improvement:F1}m)");
                return PlannedAction.Move(bestPos.Value, moveReason);
            }

            if (Main.IsDebugEnabled) Main.LogDebug($"[Support] No better position toward allies found");
            return null;
        }

        #endregion
    }
}
