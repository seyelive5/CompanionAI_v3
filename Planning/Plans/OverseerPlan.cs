using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Enums;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using Kingmaker.View.Covers;
using UnityEngine;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning.Plans
{
    /// <summary>
    /// ★ v3.7.91: Overseer 전략 (사역마 중심 전투)
    ///
    /// 핵심 차이점 (DPSPlan 대비):
    /// 1. Phase 순서: HeroicAct(Overcharge) FIRST → Familiar abilities (Momentum 활성화 후 WarpRelay)
    /// 2. 사역마 능력이 PRIMARY, 마스터 공격은 SECONDARY
    /// 3. 후퇴 시 사역마 스킬 사거리 내로 제한
    /// </summary>
    public class OverseerPlan : BasePlan
    {
        protected override string RoleName => "Overseer";

        public override TurnPlan CreatePlan(Situation situation, TurnState turnState)
        {
            var actions = new List<PlannedAction>();
            float remainingAP = situation.CurrentAP;
            float remainingMP = situation.CurrentMP;

            // 사역마가 없으면 DPS 폴백 (하지만 이 Plan이 선택되었다면 사역마가 있어야 함)
            if (!situation.HasFamiliar)
            {
                Main.LogDebug($"[Overseer] Warning: No familiar detected, unexpected state");
            }

            Main.LogDebug($"[Overseer] CreatePlan: AP={remainingAP:F1}, MP={remainingMP:F1}, " +
                $"FamiliarType={situation.FamiliarType}, HasFamiliar={situation.HasFamiliar}");

            // ★ v3.8.13: AP 예약 (다른 Role처럼 무기 공격용 AP 확보)
            float reservedAP = CalculateReservedAPForPostMoveAttack(situation);
            if (reservedAP > 0)
            {
                Main.Log($"[Overseer] Reserving {reservedAP:F1} AP for weapon attack");
            }

            // ★ v3.8.41: Phase 0 - 잠재력 초월 궁극기 (최우선)
            if (CombatAPI.HasFreeUltimateBuff(situation.Unit))
            {
                var ultimateAction = PlanUltimate(situation, ref remainingAP);
                if (ultimateAction != null)
                {
                    actions.Add(ultimateAction);
                    return new TurnPlan(actions, TurnPriority.Critical, "Overseer ultimate (Transcend Potential)");
                }
                // ★ v3.8.42: 궁극기 실패 시 즉시 EndTurn (WarhammerAbilityRestriction으로 다른 능력 사용 불가)
                Main.Log("[Overseer] Ultimate failed during Transcend Potential - ending turn");
                actions.Add(PlannedAction.EndTurn("Overseer no ultimate available"));
                return new TurnPlan(actions, TurnPriority.EndTurn, "Overseer ultimate failed (Transcend Potential)");
            }

            // ══════════════════════════════════════════════════════════════
            // Phase 1: Emergency Heal
            // ══════════════════════════════════════════════════════════════
            var healAction = PlanEmergencyHeal(situation, ref remainingAP);
            if (healAction != null)
            {
                actions.Add(healAction);
                return new TurnPlan(actions, TurnPriority.Emergency, "Overseer emergency heal");
            }

            // ══════════════════════════════════════════════════════════════
            // Phase 1.5: Reload
            // ══════════════════════════════════════════════════════════════
            var reloadAction = PlanReload(situation, ref remainingAP);
            if (reloadAction != null)
            {
                actions.Add(reloadAction);
            }

            // ══════════════════════════════════════════════════════════════
            // Phase 2: HeroicAct/Overcharge FIRST ★핵심★
            // Raven WarpRelay 전에 Momentum 활성화 필수!
            // DPSPlan에서는 Phase 1.75 Familiar → Phase 2 HeroicAct 순서라 콤보 실패
            // OverseerPlan에서는 HeroicAct를 먼저 실행하여 Momentum 확보
            // ══════════════════════════════════════════════════════════════
            var heroicAction = PlanHeroicAct(situation, ref remainingAP);
            bool heroicActPlanned = heroicAction != null;  // ★ v3.8.01: 계획됨 여부 추적
            if (heroicActPlanned)
            {
                actions.Add(heroicAction);
                Main.Log($"[Overseer] Phase 2: HeroicAct planned (Momentum will be active for WarpRelay)");
            }

            // 이번 턴 WarpRelay 사용 여부 추적
            bool usedWarpRelay = false;

            // ★ v3.7.93: 키스톤 루프에서 실제 성공한 능력 GUID 추적 (아군 버프 Phase에서 중복 방지)
            var usedKeystoneAbilityGuids = new HashSet<string>();

            // ══════════════════════════════════════════════════════════════
            // Phase 3: Familiar Abilities (PRIMARY DAMAGE/UTILITY)
            // 사역마가 주력 딜링 - 마스터는 보조 역할
            // ══════════════════════════════════════════════════════════════
            if (situation.HasFamiliar)
            {
                // ────────────────────────────────────────────────────────────
                // 3.1: Servo-Skull Priority Signal (방어력 상승 + 적 주의 분산)
                // ────────────────────────────────────────────────────────────
                if (situation.FamiliarType == PetType.ServoskullSwarm)
                {
                    var prioritySignal = PlanFamiliarPrioritySignal(situation, ref remainingAP);
                    if (prioritySignal != null)
                    {
                        actions.Add(prioritySignal);
                        Main.Log($"[Overseer] Phase 3.1: Priority Signal");
                    }
                }

                // ────────────────────────────────────────────────────────────
                // 3.2: Mastiff Fast (이동 버프 - Apprehend 전 사용)
                // ────────────────────────────────────────────────────────────
                if (situation.FamiliarType == PetType.Mastiff)
                {
                    var mastiffFast = PlanFamiliarFast(situation, ref remainingAP);
                    if (mastiffFast != null)
                    {
                        actions.Add(mastiffFast);
                        Main.Log($"[Overseer] Phase 3.2: Mastiff Fast");
                    }
                }

                // ────────────────────────────────────────────────────────────
                // 3.3: Familiar Relocate (최적 위치로 이동 - Mastiff 제외)
                // ────────────────────────────────────────────────────────────
                var familiarRelocate = PlanFamiliarRelocate(situation, ref remainingAP);
                if (familiarRelocate != null)
                {
                    actions.Add(familiarRelocate);
                    Main.Log($"[Overseer] Phase 3.3: Familiar Relocate");
                }

                // ────────────────────────────────────────────────────────────
                // 3.4: Keystone Abilities (Extrapolation/WarpRelay) ★핵심★
                // Phase 2에서 HeroicAct로 Momentum 활성화했으므로 WarpRelay AOE 전파 가능!
                // ★ v3.8.01: heroicActPlanned 전달 - 계획 단계에서 Momentum 있는 것으로 간주
                // ────────────────────────────────────────────────────────────
                var keystoneActions = PlanAllFamiliarKeystoneBuffs(situation, ref remainingAP, heroicActPlanned);
                if (keystoneActions.Count > 0)
                {
                    actions.AddRange(keystoneActions);
                    Main.Log($"[Overseer] Phase 3.4: {keystoneActions.Count} keystone abilities planned");

                    // ★ v3.7.93: 실제 사용된 능력 GUID 추적 (아군 버프에서 중복 방지)
                    foreach (var action in keystoneActions)
                    {
                        if (action.Ability?.Blueprint != null)
                        {
                            string guid = action.Ability.Blueprint.AssetGuid?.ToString();
                            if (!string.IsNullOrEmpty(guid))
                                usedKeystoneAbilityGuids.Add(guid);
                        }
                    }

                    // Raven이면 WarpRelay 사용됨
                    usedWarpRelay = situation.FamiliarType == PetType.Raven;
                }

                // ────────────────────────────────────────────────────────────
                // 3.5: Raven Cycle (WarpRelay 후 재시전)
                // ────────────────────────────────────────────────────────────
                if (usedWarpRelay)
                {
                    var cycle = PlanFamiliarCycle(situation, ref remainingAP, usedWarpRelay);
                    if (cycle != null)
                    {
                        actions.Add(cycle);
                        Main.Log($"[Overseer] Phase 3.5: Raven Cycle");
                    }
                }

                // ────────────────────────────────────────────────────────────
                // 3.5.5: Raven Aggressive Relocate (버프 배포 후 적 밀집 지역으로 이동)
                // ★ v3.8.13: WarpRelay 사용 후 아군 버프 충분하면 적 클러스터로 재배치
                // ────────────────────────────────────────────────────────────
                if (usedWarpRelay && situation.FamiliarType == PetType.Raven)
                {
                    var aggressiveRelocate = PlanRavenAggressiveRelocate(situation, ref remainingAP);
                    if (aggressiveRelocate != null)
                    {
                        actions.Add(aggressiveRelocate);
                        Main.Log($"[Overseer] Phase 3.5.5: Raven aggressive relocate to enemy cluster");
                    }
                }

                // ────────────────────────────────────────────────────────────
                // 3.6: Raven Hex (적 디버프)
                // ────────────────────────────────────────────────────────────
                if (situation.FamiliarType == PetType.Raven)
                {
                    var hex = PlanFamiliarHex(situation, ref remainingAP);
                    if (hex != null)
                    {
                        actions.Add(hex);
                        Main.Log($"[Overseer] Phase 3.6: Raven Hex");
                    }
                }

                // ────────────────────────────────────────────────────────────
                // 3.7: Mastiff Attack Chain: Apprehend → JumpClaws → Claws → Roam
                // ────────────────────────────────────────────────────────────
                if (situation.FamiliarType == PetType.Mastiff)
                {
                    var apprehend = PlanFamiliarApprehend(situation, ref remainingAP);
                    if (apprehend != null)
                    {
                        actions.Add(apprehend);
                        Main.Log($"[Overseer] Phase 3.7: Mastiff Apprehend");
                    }
                    else
                    {
                        var jumpClaws = PlanFamiliarJumpClaws(situation, ref remainingAP);
                        if (jumpClaws != null)
                        {
                            actions.Add(jumpClaws);
                            Main.Log($"[Overseer] Phase 3.7: Mastiff JumpClaws");
                        }
                        else
                        {
                            var claws = PlanFamiliarClaws(situation, ref remainingAP);
                            if (claws != null)
                            {
                                actions.Add(claws);
                                Main.Log($"[Overseer] Phase 3.7: Mastiff Claws");
                            }
                            else
                            {
                                var roam = PlanFamiliarRoam(situation, ref remainingAP);
                                if (roam != null)
                                {
                                    actions.Add(roam);
                                    Main.Log($"[Overseer] Phase 3.7: Mastiff Roam");
                                }
                            }
                        }
                    }

                    // Mastiff Protect (공격 후 부상 아군 호위)
                    var protect = PlanFamiliarProtect(situation, ref remainingAP);
                    if (protect != null)
                    {
                        actions.Add(protect);
                        Main.Log($"[Overseer] Phase 3.7: Mastiff Protect");
                    }
                }

                // ────────────────────────────────────────────────────────────
                // 3.8: Eagle Abilities
                // ────────────────────────────────────────────────────────────
                if (situation.FamiliarType == PetType.Eagle)
                {
                    // Obstruct Vision (시야 방해)
                    var obstruct = PlanFamiliarObstruct(situation, ref remainingAP);
                    if (obstruct != null)
                    {
                        actions.Add(obstruct);
                        Main.Log($"[Overseer] Phase 3.8: Eagle Obstruct");
                    }

                    // Blinding Dive (이동+실명 공격)
                    var blindingDive = PlanFamiliarBlindingDive(situation, ref remainingAP);
                    if (blindingDive != null)
                    {
                        actions.Add(blindingDive);
                        Main.Log($"[Overseer] Phase 3.8: Eagle Blinding Dive");
                    }

                    // Aerial Rush (돌진 공격)
                    var aerialRush = PlanFamiliarAerialRush(situation, ref remainingAP);
                    if (aerialRush != null)
                    {
                        actions.Add(aerialRush);
                        Main.Log($"[Overseer] Phase 3.8: Eagle Aerial Rush");
                    }

                    // Claws 폴백 (BlindingDive, AerialRush 둘 다 실패 시)
                    if (blindingDive == null && aerialRush == null)
                    {
                        var eagleClaws = PlanFamiliarClaws(situation, ref remainingAP);
                        if (eagleClaws != null)
                        {
                            actions.Add(eagleClaws);
                            Main.Log($"[Overseer] Phase 3.8: Eagle Claws (fallback)");
                        }
                    }

                    // Screen (아군 보호)
                    var screen = PlanFamiliarScreen(situation, ref remainingAP);
                    if (screen != null)
                    {
                        actions.Add(screen);
                        Main.Log($"[Overseer] Phase 3.8: Eagle Screen");
                    }
                }

                // ────────────────────────────────────────────────────────────
                // 3.9: Servo-Skull Vitality Signal (AoE 힐)
                // ────────────────────────────────────────────────────────────
                if (situation.FamiliarType == PetType.ServoskullSwarm)
                {
                    var vitalitySignal = PlanFamiliarVitalitySignal(situation, ref remainingAP);
                    if (vitalitySignal != null)
                    {
                        actions.Add(vitalitySignal);
                        Main.Log($"[Overseer] Phase 3.9: Vitality Signal");
                    }
                }
            }

            // ══════════════════════════════════════════════════════════════
            // Phase 4: Support Buffs (위치 버프 등)
            // ══════════════════════════════════════════════════════════════
            var usedBuffGuids = new HashSet<string>();
            int positionalBuffCount = 0;
            while (positionalBuffCount < MAX_POSITIONAL_BUFFS && remainingAP >= 1f)
            {
                var positionalBuff = PlanPositionalBuff(situation, ref remainingAP, usedBuffGuids);
                if (positionalBuff == null) break;

                actions.Add(positionalBuff);
                positionalBuffCount++;
                Main.Log($"[Overseer] Phase 4: Positional Buff #{positionalBuffCount}");
            }

            // Stratagem
            var stratagemAction = PlanStratagem(situation, ref remainingAP);
            if (stratagemAction != null)
            {
                actions.Add(stratagemAction);
                Main.Log($"[Overseer] Phase 4: Stratagem");
            }

            // Marker
            if (situation.AvailableMarkers.Count > 0 && situation.NearestEnemy != null)
            {
                var markerAction = PlanMarker(situation, situation.NearestEnemy, ref remainingAP);
                if (markerAction != null)
                {
                    actions.Add(markerAction);
                    Main.Log($"[Overseer] Phase 4: Marker");
                }
            }

            // ══════════════════════════════════════════════════════════════
            // Phase 4.5: Ally Buffs (쳐부숴라!, 잠재력 초월 등) ★v3.7.93 신규★
            // 키스톤 루프에서 사역마에게 실패한 버프를 아군에게 시전
            // ★ v3.8.16: 턴 부여 능력 중복 방지 (같은 대상에게 쳐부숴라 여러 번 계획 방지)
            // ★ v3.8.16: 인위적 3개 제한 제거 (SupportPlan과 일관성 + 자연 종료 조건 충분)
            // ══════════════════════════════════════════════════════════════
            var usedAllyBuffGuids = new HashSet<string>(usedKeystoneAbilityGuids);
            var plannedTurnGrantTargets = new HashSet<string>();  // ★ v3.8.16: 턴 부여 대상 추적
            int allyBuffCount = 0;
            while (remainingAP >= 1f)  // ★ v3.8.16: 제한 제거 - AP/null반환으로 자연 종료
            {
                var allyBuffAction = PlanAllyBuff(situation, ref remainingAP, usedAllyBuffGuids, plannedTurnGrantTargets);
                if (allyBuffAction == null) break;

                // 계획된 버프 GUID 추가 (무한 루프 방지)
                string buffGuid = allyBuffAction.Ability?.Blueprint?.AssetGuid?.ToString();
                if (!string.IsNullOrEmpty(buffGuid))
                {
                    usedAllyBuffGuids.Add(buffGuid);
                }

                actions.Add(allyBuffAction);
                allyBuffCount++;
                Main.Log($"[Overseer] Phase 4.5: Ally Buff #{allyBuffCount} - {allyBuffAction.Ability?.Name}");
            }

            // ══════════════════════════════════════════════════════════════
            // Phase 5: Safe Ranged Attack (SECONDARY)
            // 사역마가 주력이므로 마스터는 안전한 원거리 공격만
            // ══════════════════════════════════════════════════════════════
            bool didPlanAttack = false;
            // ★ v3.8.44: 공격 실패 이유 추적 (이동 Phase에 전달)
            var attackContext = new AttackPhaseContext();
            var plannedTargetIds = new HashSet<string>();
            var plannedAbilityGuids = new HashSet<string>();
            int attacksPlanned = 0;

            while (remainingAP >= 0f && situation.HasHittableEnemies && attacksPlanned < MAX_ATTACKS_PER_PLAN)
            {
                // ★ v3.8.44: attackContext 전달 - 실패 이유 기록
                var attackAction = PlanAttack(situation, ref remainingAP, attackContext,
                    excludeTargetIds: plannedTargetIds,
                    excludeAbilityGuids: plannedAbilityGuids);

                if (attackAction == null) break;

                actions.Add(attackAction);
                didPlanAttack = true;
                attacksPlanned++;

                // MP 차감
                if (attackAction.Ability != null)
                {
                    remainingMP -= CombatAPI.GetAbilityMPCost(attackAction.Ability);
                    if (remainingMP < 0) remainingMP = 0;
                }

                // 타겟/능력 제외 목록 업데이트
                // ★ v3.8.30: 적이 1명일 때는 타겟/능력 모두 제외하지 않음 (동일 능력으로 재공격 허용)
                var targetEntity = attackAction.Target?.Entity as BaseUnitEntity;
                if (targetEntity != null && situation.HittableEnemies.Count > 1)
                    plannedTargetIds.Add(targetEntity.UniqueId);

                if (attackAction.Ability != null && situation.HittableEnemies.Count > 1)
                {
                    var guid = attackAction.Ability.Blueprint?.AssetGuid?.ToString();
                    if (!string.IsNullOrEmpty(guid))
                        plannedAbilityGuids.Add(guid);
                }
            }

            if (didPlanAttack)
            {
                Main.Log($"[Overseer] Phase 5: {attacksPlanned} attacks planned");
            }

            // ══════════════════════════════════════════════════════════════
            // Phase 6: PostAction (Run and Gun 등)
            // ══════════════════════════════════════════════════════════════
            if (situation.HasPerformedFirstAction || didPlanAttack)
            {
                var postAction = PlanPostAction(situation, ref remainingAP, didPlanAttack);
                if (postAction != null)
                {
                    actions.Add(postAction);

                    // MP 회복 예측
                    float expectedMP = AbilityDatabase.GetExpectedMPRecovery(postAction.Ability);
                    if (expectedMP > 0)
                    {
                        remainingMP += expectedMP;
                        Main.Log($"[Overseer] Phase 6: {postAction.Ability.Name} will restore ~{expectedMP:F0} MP");
                    }
                }
            }

            // ══════════════════════════════════════════════════════════════
            // Phase 7: Retreat (사역마 사거리 내) ★핵심★
            // 일반 후퇴와 달리 사역마 스킬 사거리 내로 제한
            // ★ v3.8.13: RangePreference 반영 - 근접 선호시 후퇴 안 함
            // ══════════════════════════════════════════════════════════════
            bool hasMoveInPlan = actions.Any(a => a.Type == ActionType.Move ||
                (a.Type == ActionType.Attack && a.Ability != null && AbilityDatabase.IsGapCloser(a.Ability)));

            // ★ v3.8.13: 근접 선호시 후퇴하지 않음 (ShouldRetreat는 PreferRanged만 true)
            bool shouldRetreat = ShouldRetreat(situation);

            if (!hasMoveInPlan && shouldRetreat && remainingMP > 0)
            {
                var retreatAction = PlanOverseerRetreat(situation, remainingMP);
                if (retreatAction != null)
                {
                    actions.Add(retreatAction);
                    hasMoveInPlan = true;
                    Main.Log($"[Overseer] Phase 7: Retreat within familiar ability range");
                }
            }

            // ══════════════════════════════════════════════════════════════
            // Phase 8: Movement (필요시) ★v3.7.97: 사역마 사거리 내로 제한★
            // ★ v3.8.13: 거리 선호 반영 - 근접 선호시 적 접근, 원거리 선호시 거리 유지
            // ══════════════════════════════════════════════════════════════
            bool canMove = situation.CanMove || remainingMP > 0;
            // ★ v3.8.45: 원거리 + AvailableAttacks=0 → 적에게 접근 무의미
            bool noAttackNoApproach = situation.PrefersRanged && situation.AvailableAttacks.Count == 0;
            // NeedsReposition도 noAttackNoApproach 적용
            bool needsMovement = (situation.NeedsReposition || (!didPlanAttack && situation.HasLivingEnemies)) && !noAttackNoApproach;

            // ★ v3.8.14: 근접 선호시 적에게 접근 필요 여부 체크
            // 핵심 변경: HasHittableEnemies가 아닌 HasMeleeHittableEnemies를 사용
            // 이유: 폴백(원거리)으로 Hittable이 되어도 근접 캐릭터는 적에게 접근해야 함
            bool prefersApproach = situation.RangePreference == RangePreference.PreferMelee;
            bool needsApproach = prefersApproach && situation.HasLivingEnemies && !situation.HasMeleeHittableEnemies;

            if (!hasMoveInPlan && (needsMovement || needsApproach) && canMove && remainingMP > 0)
            {
                // ★ v3.8.13: 근접 선호시 적 접근, 아니면 사역마 사거리 내 이동
                PlannedAction moveAction;
                if (needsApproach)
                {
                    // 근접 선호: 적에게 접근 (일반 이동 로직 사용)
                    moveAction = PlanMoveToEnemy(situation);
                    if (moveAction != null)
                    {
                        Main.Log($"[Overseer] Phase 8: Approach enemy (PreferMelee)");
                    }
                }
                else
                {
                    // 원거리/적응형: 사역마 사거리 내에서 안전한 위치로 이동
                    // ★ v3.8.44: HasHittableEnemies → attackContext.ShouldForceMove (실패 이유 기반)
                    moveAction = PlanOverseerMovement(situation, remainingMP, !didPlanAttack && attackContext.ShouldForceMove);
                    if (moveAction != null)
                    {
                        Main.Log($"[Overseer] Phase 8: Movement (within familiar range)");
                    }
                }

                if (moveAction != null)
                {
                    actions.Add(moveAction);

                    // ★ v3.8.47: 이동 후 공격 (Post-Move Attack)
                    // 근접 접근 후 즉시 공격 시도 - DPS/Tank/Support와 동일 패턴
                    // 문제: 근접 선호 오버시어가 이동만 하고 공격하지 않음
                    // 원인: Phase 5(공격) → Phase 8(이동) 순서라 이동 후 공격 기회 없음
                    if (needsApproach && remainingAP > 0 && situation.NearestEnemy != null)
                    {
                        UnityEngine.Vector3? moveDestination = moveAction.Target?.Point;
                        var postMoveAttack = PlanPostMoveAttack(situation, situation.NearestEnemy, ref remainingAP, moveDestination);
                        if (postMoveAttack != null)
                        {
                            actions.Add(postMoveAttack);
                            didPlanAttack = true;
                            Main.Log($"[Overseer] Phase 8: Post-move attack after approach");
                        }
                    }
                }
            }

            // ══════════════════════════════════════════════════════════════
            // Phase 8.5: 원거리 안전 후퇴 ★v3.8.45★
            // Phase 7 후퇴가 실행되지 않은 경우의 안전망
            // ══════════════════════════════════════════════════════════════
            bool hasMoveAfterPhase8 = actions.Any(a => a.Type == ActionType.Move ||
                (a.Type == ActionType.Attack && a.Ability != null && AbilityDatabase.IsGapCloser(a.Ability)));

            if (!hasMoveAfterPhase8 && remainingMP > 0 && situation.CanMove && situation.PrefersRanged)
            {
                bool needsSafeRetreat = false;
                string retreatReason = "";

                if (situation.NearestEnemy != null && situation.NearestEnemyDistance < situation.MinSafeDistance * 1.2f)
                {
                    needsSafeRetreat = true;
                    retreatReason = $"enemy too close ({situation.NearestEnemyDistance:F1}m)";
                }

                if (situation.InfluenceMap != null && situation.InfluenceMap.IsValid)
                {
                    float frontlineDist = situation.InfluenceMap.GetFrontlineDistance(situation.Unit.Position);
                    if (frontlineDist > -5f)
                    {
                        needsSafeRetreat = true;
                        retreatReason = $"too close to frontline ({frontlineDist:F1}m)";
                    }
                }

                if (needsSafeRetreat)
                {
                    var safeRetreatAction = PlanPostActionSafeRetreat(situation);
                    if (safeRetreatAction != null)
                    {
                        actions.Add(safeRetreatAction);
                        Main.Log($"[Overseer] Phase 8.5: Post-action safe retreat: {retreatReason}");
                    }
                }
            }

            // ══════════════════════════════════════════════════════════════
            // Phase 9: Final AP Utilization
            // ══════════════════════════════════════════════════════════════
            if (remainingAP >= 1f && actions.Count > 0)
            {
                var finalAction = PlanFinalAPUtilization(situation, ref remainingAP);
                if (finalAction != null)
                {
                    actions.Add(finalAction);
                    Main.Log($"[Overseer] Phase 9: Final AP utilization");
                }
            }

            // ══════════════════════════════════════════════════════════════
            // Phase 10: Turn Ending (항상 마지막)
            // ══════════════════════════════════════════════════════════════
            var turnEndAction = PlanTurnEndingAbility(situation, ref remainingAP);
            if (turnEndAction != null)
            {
                actions.Add(turnEndAction);
            }

            // 행동 없으면 턴 종료
            if (actions.Count == 0)
            {
                actions.Add(PlannedAction.EndTurn("Overseer maintaining position"));
            }

            var priority = DeterminePriority(actions, situation);
            var reasoning = $"Overseer: {DetermineReasoning(actions, situation)}";

            Main.LogDebug($"[Overseer] Plan complete: {actions.Count} actions, AP={remainingAP:F1}, MP={remainingMP:F1}");

            int zeroAPAttackCount = CombatAPI.GetZeroAPAttacks(situation.Unit).Count;
            return new TurnPlan(actions, priority, reasoning, situation.HPPercent, situation.NearestEnemyDistance,
                situation.HittableEnemies?.Count ?? 0, situation.CurrentAP, situation.CurrentMP, zeroAPAttackCount);
        }

        #region Overseer-Specific Methods

        /// <summary>
        /// ★ v3.7.91: 사역마 사거리 내 후퇴
        /// ★ v3.7.98: 사역마가 멀면 사역마 쪽으로 이동
        /// ★ v3.8.13: 진동 방지 - 후퇴도 충분한 개선 필요
        /// </summary>
        private PlannedAction PlanOverseerRetreat(Situation situation, float remainingMP)
        {
            // 사역마 스킬 최대 사거리 조회
            float maxFamiliarRange = FamiliarAPI.GetMaxFamiliarAbilityRange(situation.Unit);
            float currentDistToFamiliar = Vector3.Distance(situation.Unit.Position, situation.FamiliarPosition);
            Vector3 currentPos = situation.Unit.Position;
            Main.LogDebug($"[Overseer] PlanOverseerRetreat: maxFamiliarRange={maxFamiliarRange:F1}m, currentDist={currentDistToFamiliar:F1}m");

            // 도달 가능한 타일 조회
            var tiles = MovementAPI.FindAllReachableTilesSync(situation.Unit, remainingMP);
            if (tiles == null || tiles.Count == 0)
            {
                Main.LogDebug($"[Overseer] PlanOverseerRetreat: No reachable tiles, using standard retreat");
                return PlanRetreat(situation);
            }

            Vector3? bestPos = null;
            float bestScore = float.MinValue;

            // ★ v3.8.13: 현재 위치에서 가장 가까운 적과의 거리 (후퇴 효과 검증용)
            float currentNearestEnemyDist = situation.NearestEnemyDistance;

            // ★ v3.7.98: 사역마 쪽으로 이동하는 폴백 위치도 추적
            Vector3? closestToFamiliarPos = null;
            float closestToFamiliarDist = float.MaxValue;

            foreach (var kvp in tiles)
            {
                var cell = kvp.Value;
                if (!cell.IsCanStand) continue;

                var node = kvp.Key as CustomGridNodeBase;
                if (node == null) continue;

                var pos = node.Vector3Position;

                // 사역마와의 거리 체크
                float distToFamiliar = Vector3.Distance(pos, situation.FamiliarPosition);

                // ★ v3.7.98: 사역마에 가장 가까운 위치 추적 (폴백용)
                if (distToFamiliar < closestToFamiliarDist)
                {
                    closestToFamiliarDist = distToFamiliar;
                    closestToFamiliarPos = pos;
                }

                // 사역마 스킬 사거리 밖은 일단 스킵
                if (distToFamiliar > maxFamiliarRange)
                {
                    continue;
                }

                // ★ v3.7.99: 스코어 계산 (엄폐/안전 포함)
                float score = 0f;

                // 1. 엄폐 점수 (후퇴 시 엄폐 더 중요)
                try
                {
                    var coverType = LosCalculations.GetCoverType(pos);
                    switch (coverType)
                    {
                        case LosCalculations.CoverType.Full:
                            score += 35f;  // 완전 엄폐 (후퇴 시 가중)
                            break;
                        case LosCalculations.CoverType.Half:
                            score += 18f;  // 절반 엄폐
                            break;
                        case LosCalculations.CoverType.Invisible:
                            score += 40f;  // 은신 (최고)
                            break;
                    }
                }
                catch { /* 엄폐 계산 실패 무시 */ }

                // 2. 안전도 계산 (적들과의 거리 기반)
                // ★ v3.7.99: 모든 적과의 거리를 고려한 위협 계산
                float totalThreat = 0f;
                float nearestEnemyDist = float.MaxValue;
                foreach (var enemy in situation.Enemies)
                {
                    float distToEnemy = Vector3.Distance(pos, enemy.Position);
                    if (distToEnemy < nearestEnemyDist)
                        nearestEnemyDist = distToEnemy;

                    // 가까운 적일수록 위협도 높음 (10m 이내 = 고위협)
                    if (distToEnemy < 10f)
                        totalThreat += (10f - distToEnemy) * 2f;
                    else if (distToEnemy < 20f)
                        totalThreat += (20f - distToEnemy) * 0.5f;
                }
                score -= totalThreat;  // 위협도 페널티

                // 3. 적과의 최소 거리 (후퇴 시 멀수록 좋음)
                score += nearestEnemyDist * 1.5f;  // 적과 멀수록 보너스

                // 4. 사역마와 가까울수록 보너스
                score -= distToFamiliar * 0.3f;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestPos = pos;
                }
            }

            // ★ v3.8.13: 진동 방지 - 후퇴 후 적과의 거리가 최소 2m 이상 멀어져야 의미 있음
            if (bestPos.HasValue)
            {
                float newNearestEnemyDist = float.MaxValue;
                foreach (var enemy in situation.Enemies)
                {
                    float d = Vector3.Distance(bestPos.Value, enemy.Position);
                    if (d < newNearestEnemyDist) newNearestEnemyDist = d;
                }

                float distImprovement = newNearestEnemyDist - currentNearestEnemyDist;
                float distFromCurrent = Vector3.Distance(bestPos.Value, currentPos);

                // 후퇴 효과 검증: 적과 2m 이상 멀어지거나, 현재보다 5m 이상 이동해야 함
                if (distImprovement < 2f && distFromCurrent < 5f)
                {
                    Main.LogDebug($"[Overseer] PlanOverseerRetreat: Not worth it (enemy dist improvement={distImprovement:F1}m, move dist={distFromCurrent:F1}m)");
                    return null;
                }

                Main.Log($"[Overseer] Retreat within {maxFamiliarRange:F0}m of familiar (enemy dist +{distImprovement:F1}m)");
                return PlannedAction.Move(bestPos.Value, $"Safe retreat (within {maxFamiliarRange:F0}m of familiar)");
            }

            // ★ v3.7.98: 사역마 사거리 내 위치가 없으면 사역마 쪽으로 이동
            if (closestToFamiliarPos.HasValue && closestToFamiliarDist < currentDistToFamiliar)
            {
                Main.Log($"[Overseer] Retreating toward familiar (current={currentDistToFamiliar:F1}m → {closestToFamiliarDist:F1}m)");
                return PlannedAction.Move(closestToFamiliarPos.Value, $"Retreat toward familiar ({closestToFamiliarDist:F1}m)");
            }

            // 사역마 쪽으로도 이동 불가면 표준 후퇴
            Main.LogDebug($"[Overseer] PlanOverseerRetreat: Cannot reach familiar, using standard retreat");
            return PlanRetreat(situation);
        }

        /// <summary>
        /// ★ v3.7.97: 사역마 사거리 내 이동 (공격 위치 탐색)
        /// ★ v3.7.98: 사역마가 멀면 사역마 쪽으로 이동
        /// ★ v3.8.13: 진동 방지 - 현재 위치 대비 충분한 개선이 없으면 이동 안 함
        /// </summary>
        private PlannedAction PlanOverseerMovement(Situation situation, float remainingMP, bool needsAttackPosition)
        {
            // 사역마 스킬 최대 사거리 조회
            float maxFamiliarRange = FamiliarAPI.GetMaxFamiliarAbilityRange(situation.Unit);
            float currentDistToFamiliar = Vector3.Distance(situation.Unit.Position, situation.FamiliarPosition);
            Main.LogDebug($"[Overseer] PlanOverseerMovement: maxFamiliarRange={maxFamiliarRange:F1}m, currentDist={currentDistToFamiliar:F1}m, needsAttackPosition={needsAttackPosition}");

            // 도달 가능한 타일 조회
            var tiles = MovementAPI.FindAllReachableTilesSync(situation.Unit, remainingMP);
            if (tiles == null || tiles.Count == 0)
            {
                Main.LogDebug($"[Overseer] PlanOverseerMovement: No reachable tiles");
                return null;
            }

            Vector3? bestPos = null;
            float bestScore = float.MinValue;
            Vector3 currentPos = situation.Unit.Position;

            // ★ v3.8.13: 현재 위치 점수 계산 (진동 방지용)
            float currentPosScore = CalculatePositionScore(currentPos, situation, maxFamiliarRange, needsAttackPosition);

            // ★ v3.7.98: 사역마 쪽으로 이동하는 폴백 위치도 추적
            Vector3? closestToFamiliarPos = null;
            float closestToFamiliarDist = float.MaxValue;

            foreach (var kvp in tiles)
            {
                var cell = kvp.Value;
                if (!cell.IsCanStand) continue;

                var node = kvp.Key as CustomGridNodeBase;
                if (node == null) continue;

                var pos = node.Vector3Position;

                // 사역마와의 거리 체크
                float distToFamiliar = Vector3.Distance(pos, situation.FamiliarPosition);

                // ★ v3.7.98: 사역마에 가장 가까운 위치 추적 (폴백용)
                if (distToFamiliar < closestToFamiliarDist)
                {
                    closestToFamiliarDist = distToFamiliar;
                    closestToFamiliarPos = pos;
                }

                // 사역마 스킬 사거리 밖은 일단 스킵 (최적 위치 탐색에서 제외)
                if (distToFamiliar > maxFamiliarRange)
                {
                    continue;
                }

                // ★ v3.7.99: 스코어 계산 (엄폐/안전 포함)
                float score = 0f;

                // 1. 엄폐 점수 (★ v3.7.99: LosCalculations 기반)
                try
                {
                    var coverType = LosCalculations.GetCoverType(pos);
                    switch (coverType)
                    {
                        case LosCalculations.CoverType.Full:
                            score += 25f;  // 완전 엄폐
                            break;
                        case LosCalculations.CoverType.Half:
                            score += 12f;  // 절반 엄폐
                            break;
                        case LosCalculations.CoverType.Invisible:
                            score += 30f;  // 은신 (최고)
                            break;
                    }
                }
                catch { /* 엄폐 계산 실패 무시 */ }

                // 2. 안전도 계산 (적들과의 거리 기반)
                // ★ v3.7.99: 모든 적과의 거리를 고려한 위협 계산
                float totalThreat = 0f;
                float nearestEnemyDist = float.MaxValue;
                foreach (var enemy in situation.Enemies)
                {
                    float distToEnemy = Vector3.Distance(pos, enemy.Position);
                    if (distToEnemy < nearestEnemyDist)
                        nearestEnemyDist = distToEnemy;

                    // 가까운 적일수록 위협도 높음 (10m 이내 = 고위협)
                    if (distToEnemy < 10f)
                        totalThreat += (10f - distToEnemy) * 1.5f;  // 이동 시에는 약간 낮은 가중치
                    else if (distToEnemy < 20f)
                        totalThreat += (20f - distToEnemy) * 0.3f;
                }
                score -= totalThreat;  // 위협도 페널티

                // 3. 공격 위치가 필요한 경우: 적에게 적절한 거리 유지
                if (needsAttackPosition && situation.BestTarget != null)
                {
                    float distToTarget = Vector3.Distance(pos, situation.BestTarget.Position);
                    // 10-25m가 이상적 (원거리 공격 가능, 근접 위험 회피)
                    if (distToTarget >= 10f && distToTarget <= 25f)
                        score += 20f;
                    else if (distToTarget < 10f)
                        score -= (10f - distToTarget) * 3f;  // 너무 가까우면 큰 페널티
                    else
                        score += 5f;  // 그 외 거리
                }

                // 4. 사역마와 가까울수록 보너스
                score += (maxFamiliarRange - distToFamiliar) * 0.5f;

                // 5. 현재 위치와 너무 비슷하면 이동 의미 없음
                float distFromCurrent = Vector3.Distance(pos, currentPos);
                if (distFromCurrent < 3f)
                {
                    score -= 20f;  // 제자리 이동 페널티
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestPos = pos;
                }
            }

            // ★ v3.8.13: 진동 방지 - 새 위치가 현재 위치보다 충분히 좋아야만 이동
            const float MIN_SCORE_IMPROVEMENT = 15f;  // 최소 15점 이상 개선되어야 이동

            if (bestPos.HasValue)
            {
                float scoreImprovement = bestScore - currentPosScore;
                float distFromCurrent = Vector3.Distance(bestPos.Value, currentPos);

                // 이동 거리가 짧거나 점수 개선이 미미하면 이동 안 함
                if (distFromCurrent < 4f && scoreImprovement < MIN_SCORE_IMPROVEMENT)
                {
                    Main.LogDebug($"[Overseer] PlanOverseerMovement: Not worth moving (dist={distFromCurrent:F1}m, improvement={scoreImprovement:F1})");
                    return null;
                }

                float finalDist = Vector3.Distance(bestPos.Value, situation.FamiliarPosition);
                Main.Log($"[Overseer] Movement within {maxFamiliarRange:F0}m of familiar (dist={finalDist:F1}m, improvement={scoreImprovement:F1})");
                return PlannedAction.Move(bestPos.Value, $"Attack position (within {maxFamiliarRange:F0}m of familiar)");
            }

            // ★ v3.7.98: 사역마 사거리 내 위치가 없으면 사역마 쪽으로 이동
            if (closestToFamiliarPos.HasValue && closestToFamiliarDist < currentDistToFamiliar)
            {
                // 현재보다 사역마에 가까워지는 경우에만 이동
                Main.Log($"[Overseer] Moving toward familiar (current={currentDistToFamiliar:F1}m → {closestToFamiliarDist:F1}m)");
                return PlannedAction.Move(closestToFamiliarPos.Value, $"Move toward familiar ({closestToFamiliarDist:F1}m)");
            }

            Main.LogDebug($"[Overseer] PlanOverseerMovement: No valid position (familiar too far: {currentDistToFamiliar:F1}m)");
            return null;
        }

        /// <summary>
        /// ★ v3.8.13: 레이븐 공격적 재배치 (버프 배포 후 적 밀집 지역으로 이동)
        /// </summary>
        private PlannedAction PlanRavenAggressiveRelocate(Situation situation, ref float remainingAP)
        {
            // Raven Relocate 능력 찾기
            var relocate = situation.FamiliarAbilities?
                .FirstOrDefault(a => FamiliarAbilities.IsRelocateAbility(a));

            if (relocate == null)
            {
                Main.LogDebug($"[Overseer] RavenAggressiveRelocate: No relocate ability");
                return null;
            }

            // AP 비용 확인
            float apCost = CombatAPI.GetAbilityAPCost(relocate);
            if (remainingAP < apCost)
            {
                Main.LogDebug($"[Overseer] RavenAggressiveRelocate: Not enough AP ({remainingAP:F1} < {apCost:F1})");
                return null;
            }

            // 아군 버프 커버리지 확인 (60% 이상이어야 공격 모드)
            var raven = FamiliarAPI.GetFamiliar(situation.Unit);
            if (raven == null) return null;

            var validAllies = situation.Allies?
                .Where(a => a != null && a.IsConscious && !FamiliarAPI.IsFamiliar(a))
                .ToList() ?? new List<BaseUnitEntity>();

            int alliesInRavenRange = FamiliarAPI.CountAlliesInRadius(
                raven.Position, FamiliarPositioner.EFFECT_RADIUS_TILES, validAllies);

            float buffCoverage = validAllies.Count > 0 ? (float)alliesInRavenRange / validAllies.Count : 0f;

            // 버프 커버리지가 충분하지 않으면 공격 모드 진입 안 함
            if (buffCoverage < 0.5f || alliesInRavenRange < 2)
            {
                Main.LogDebug($"[Overseer] RavenAggressiveRelocate: Buff coverage too low ({buffCoverage:P0}, {alliesInRavenRange} allies)");
                return null;
            }

            // 적 밀집 지역 중심 찾기
            var validEnemies = situation.Enemies?
                .Where(e => e != null && e.IsConscious)
                .ToList() ?? new List<BaseUnitEntity>();

            if (validEnemies.Count < 2)
            {
                Main.LogDebug($"[Overseer] RavenAggressiveRelocate: Not enough enemies ({validEnemies.Count})");
                return null;
            }

            // 적 클러스터 중심 계산
            Vector3 enemyCenter = Vector3.zero;
            foreach (var enemy in validEnemies)
                enemyCenter += enemy.Position;
            enemyCenter /= validEnemies.Count;

            // 현재 레이븐 위치와 적 클러스터 거리 확인
            float distToEnemyCluster = Vector3.Distance(raven.Position, enemyCenter);
            if (distToEnemyCluster < 5f)
            {
                Main.LogDebug($"[Overseer] RavenAggressiveRelocate: Already near enemy cluster ({distToEnemyCluster:F1}m)");
                return null;
            }

            // 적 클러스터 근처에서 최적 위치 찾기 (Relocate 사거리 내)
            float maxRange = CombatAPI.GetAbilityRange(relocate);
            var optimalPos = FamiliarPositioner.FindOptimalPosition(
                situation.Unit,
                PetType.Raven,
                validAllies,
                validEnemies,
                maxRange);

            if (optimalPos == null || optimalPos.EnemiesInRange < 2)
            {
                Main.LogDebug($"[Overseer] RavenAggressiveRelocate: No good enemy cluster position");
                return null;
            }

            // LOS/타겟 가능 여부 확인
            string reason;
            if (CombatAPI.CanUseAbilityOnPoint(relocate, optimalPos.Position, out reason))
            {
                remainingAP -= apCost;
                Main.Log($"[Overseer] ★ Raven aggressive relocate to enemy cluster ({optimalPos.EnemiesInRange} enemies in range)");
                return PlannedAction.PositionalBuff(
                    relocate,
                    optimalPos.Position,
                    $"Raven to enemy cluster ({optimalPos.EnemiesInRange} enemies)",
                    apCost);
            }

            // ★ v3.8.13: 최적 위치 불가 시 적 클러스터 방향으로 최대한 가까운 위치 찾기
            Main.LogDebug($"[Overseer] RavenAggressiveRelocate: Optimal blocked ({reason}), finding closest valid position");

            Vector3? bestFallbackPos = null;
            float bestFallbackDist = float.MaxValue;
            int bestFallbackEnemies = 0;

            // 적 클러스터 방향으로 이동 가능한 위치 탐색 (그리드 검색)
            const float TILE_SIZE = 1.35f;
            int searchRadius = (int)(maxRange / TILE_SIZE);
            Vector3 unitPos = situation.Unit.Position;

            // 레이븐 현재 위치에서 적 클러스터 방향으로 검색
            Vector3 direction = (enemyCenter - raven.Position).normalized;

            for (int dx = -searchRadius; dx <= searchRadius; dx++)
            {
                for (int dz = -searchRadius; dz <= searchRadius; dz++)
                {
                    Vector3 pos = unitPos + new Vector3(dx * TILE_SIZE, 0, dz * TILE_SIZE);

                    // 유닛 사거리 내인지 확인
                    float distFromUnit = Vector3.Distance(pos, unitPos);
                    if (distFromUnit > maxRange)
                        continue;

                    string posReason;
                    if (!CombatAPI.CanUseAbilityOnPoint(relocate, pos, out posReason))
                        continue;

                    float distToCluster = Vector3.Distance(pos, enemyCenter);
                    int enemiesNear = validEnemies.Count(e => Vector3.Distance(pos, e.Position) <= FamiliarPositioner.EFFECT_RADIUS_TILES * 1.5f);

                    // 현재 레이븐 위치보다 적 클러스터에 가까워야 함
                    if (distToCluster < distToEnemyCluster && distToCluster < bestFallbackDist)
                    {
                        bestFallbackDist = distToCluster;
                        bestFallbackPos = pos;
                        bestFallbackEnemies = enemiesNear;
                    }
                }
            }

            if (bestFallbackPos.HasValue)
            {
                remainingAP -= apCost;
                Main.Log($"[Overseer] ★ Raven relocate toward enemy cluster (fallback, {bestFallbackEnemies} enemies nearby, dist={bestFallbackDist:F1}m)");
                return PlannedAction.PositionalBuff(
                    relocate,
                    bestFallbackPos.Value,
                    $"Raven toward enemy cluster ({bestFallbackDist:F1}m)",
                    apCost);
            }

            Main.LogDebug($"[Overseer] RavenAggressiveRelocate: No valid fallback position found");
            return null;
        }

        /// <summary>
        /// ★ v3.8.13: 위치 점수 계산 (진동 방지용 - 현재 위치와 새 위치 비교)
        /// </summary>
        private float CalculatePositionScore(Vector3 pos, Situation situation, float maxFamiliarRange, bool needsAttackPosition)
        {
            float score = 0f;

            // 1. 엄폐 점수
            try
            {
                var coverType = LosCalculations.GetCoverType(pos);
                switch (coverType)
                {
                    case LosCalculations.CoverType.Full:
                        score += 25f;
                        break;
                    case LosCalculations.CoverType.Half:
                        score += 12f;
                        break;
                    case LosCalculations.CoverType.Invisible:
                        score += 30f;
                        break;
                }
            }
            catch { }

            // 2. 위협도 계산
            float nearestEnemyDist = float.MaxValue;
            foreach (var enemy in situation.Enemies)
            {
                float distToEnemy = Vector3.Distance(pos, enemy.Position);
                if (distToEnemy < nearestEnemyDist)
                    nearestEnemyDist = distToEnemy;

                if (distToEnemy < 10f)
                    score -= (10f - distToEnemy) * 1.5f;
                else if (distToEnemy < 20f)
                    score -= (20f - distToEnemy) * 0.3f;
            }

            // 3. 공격 위치 보너스
            if (needsAttackPosition && situation.BestTarget != null)
            {
                float distToTarget = Vector3.Distance(pos, situation.BestTarget.Position);
                if (distToTarget >= 10f && distToTarget <= 25f)
                    score += 20f;
                else if (distToTarget < 10f)
                    score -= (10f - distToTarget) * 3f;
                else
                    score += 5f;
            }

            // 4. 사역마 거리 보너스
            float distToFamiliar = Vector3.Distance(pos, situation.FamiliarPosition);
            if (distToFamiliar <= maxFamiliarRange)
                score += (maxFamiliarRange - distToFamiliar) * 0.5f;
            else
                score -= 50f;  // 사거리 밖 페널티

            return score;
        }

        #endregion
    }
}
