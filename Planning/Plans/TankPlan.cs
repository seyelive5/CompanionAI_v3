using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Enums;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Planning.Planners;

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

            // ★ v3.8.41: Phase 0 - 잠재력 초월 궁극기 (최우선)
            if (CombatAPI.HasFreeUltimateBuff(situation.Unit))
            {
                var ultimateAction = PlanUltimate(situation, ref remainingAP);
                if (ultimateAction != null)
                {
                    actions.Add(ultimateAction);
                    return new TurnPlan(actions, TurnPriority.Critical, "Tank ultimate (Transcend Potential)");
                }
                // ★ v3.8.42: 궁극기 실패 시 즉시 EndTurn (WarhammerAbilityRestriction으로 다른 능력 사용 불가)
                Main.Log("[Tank] Ultimate failed during Transcend Potential - ending turn");
                actions.Add(PlannedAction.EndTurn("Tank no ultimate available"));
                return new TurnPlan(actions, TurnPriority.EndTurn, "Tank ultimate failed (Transcend Potential)");
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

            // ★ v3.7.02: Phase 1.75 - Familiar support (키스톤 확산 포함)
            // ★ v3.7.12: 모든 사역마 능력 통합
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

                // 3. Relocate: 사역마 최적 위치로 이동 (Mastiff 제외)
                var familiarRelocate = PlanFamiliarRelocate(situation, ref remainingAP);
                if (familiarRelocate != null)
                    actions.Add(familiarRelocate);

                // ★ v3.7.02: 4. 키스톤 버프/디버프 루프 (Servo-Skull/Raven)
                var keystoneActions = PlanAllFamiliarKeystoneBuffs(situation, ref remainingAP);
                if (keystoneActions.Count > 0)
                {
                    actions.AddRange(keystoneActions);
                    Main.Log($"[Tank] Phase 1.75: {keystoneActions.Count} keystone abilities planned");
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
                var familiarApprehend = PlanFamiliarApprehend(situation, ref remainingAP);
                if (familiarApprehend != null)
                    actions.Add(familiarApprehend);
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

                // 8. Eagle Obstruct: 적 시야 방해
                var familiarObstruct = PlanFamiliarObstruct(situation, ref remainingAP);
                if (familiarObstruct != null)
                    actions.Add(familiarObstruct);

                // ★ v3.7.14: 9. Eagle Blinding Dive: 이동+실명 공격
                var blindingDive = PlanFamiliarBlindingDive(situation, ref remainingAP);
                if (blindingDive != null)
                    actions.Add(blindingDive);

                // 10. Eagle Aerial Rush: 돌진 공격 (경로상 적 타격)
                var aerialRush = PlanFamiliarAerialRush(situation, ref remainingAP);
                if (aerialRush != null)
                    actions.Add(aerialRush);

                // ★ v3.7.14: 11. Eagle Claws: 폴백 근접 공격
                if (blindingDive == null && aerialRush == null)
                {
                    var eagleClaws = PlanFamiliarClaws(situation, ref remainingAP);
                    if (eagleClaws != null)
                        actions.Add(eagleClaws);
                }
            }

            // Phase 2: 방어 자세 (Confidence 기반 결정)
            // ★ v3.2.20: 신뢰도가 낮으면(<0.5) 방어 자세 필수
            float confidence = GetTeamConfidence();
            bool needDefense = confidence < 0.5f;

            if (!situation.HasPerformedFirstAction && needDefense)
            {
                var defenseAction = PlanDefensiveStanceWithReservation(situation, ref remainingAP, reservedAP);
                if (defenseAction != null)
                {
                    actions.Add(defenseAction);
                    Main.LogDebug($"[Tank] Phase 2: Defense stance (confidence={confidence:F2} < 0.5)");
                }
            }
            else if (!needDefense && !situation.HasPerformedFirstAction)
            {
                Main.LogDebug($"[Tank] Phase 2: Skipping defense stance (confidence={confidence:F2} >= 0.5)");
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

            // ★ v3.1.25: Phase 4 - 스마트 도발 시스템
            // - 아군 타겟팅 적 탐지
            // - 이동 후 도발 타당성 스코어링
            // - AOE 도발 범위 정확 계산
            var tauntAbilities = situation.AvailableBuffs
                .Where(a => AbilityDatabase.IsTaunt(a))
                .ToList();

            if (tauntAbilities.Count > 0 && situation.HasLivingEnemies)
            {
                // 모든 도발 옵션 평가 (현재 위치 + 이동 가능 위치)
                var tauntOptions = TauntScorer.EvaluateAllTauntOptions(
                    situation, tauntAbilities, remainingMP);

                var bestOption = tauntOptions.FirstOrDefault();

                if (TauntScorer.IsTauntWorthwhile(bestOption))
                {
                    // ★ v3.5.10: 도발 대상 예약 체크 (중복 도발 방지)
                    // AOE 도발의 경우 주요 타겟(가장 가까운 적) 기준으로 예약
                    var primaryTauntTarget = situation.NearestEnemy;
                    bool canReserveTaunt = true;

                    if (primaryTauntTarget != null)
                    {
                        if (TeamBlackboard.Instance.IsTauntReserved(primaryTauntTarget))
                        {
                            Main.LogDebug($"[Tank] SmartTaunt: Skip - {primaryTauntTarget.CharacterName} already reserved for taunt");
                            canReserveTaunt = false;
                        }
                    }

                    if (canReserveTaunt)
                    {
                        Main.Log($"[Tank] SmartTaunt: {bestOption.Ability.Name} " +
                            $"(enemies={bestOption.EnemiesAffected}, targetingAllies={bestOption.EnemiesTargetingAllies}, " +
                            $"requiresMove={bestOption.RequiresMove}, score={bestOption.Score:F0})");

                        // ★ v3.5.10: 도발 대상 예약
                        if (primaryTauntTarget != null)
                        {
                            TeamBlackboard.Instance.ReserveTaunt(primaryTauntTarget);
                        }

                        // 이동이 필요하면 먼저 이동 계획
                        if (bestOption.RequiresMove)
                        {
                            var moveAction = PlannedAction.Move(bestOption.Position, "Move for smart taunt");
                            actions.Add(moveAction);
                            remainingMP -= bestOption.MoveCost;
                            Main.Log($"[Tank] SmartTaunt: Moving to taunt position (cost={bestOption.MoveCost:F1} MP)");
                        }

                        // 도발 액션 추가
                        float apCost = CombatAPI.GetAbilityAPCost(bestOption.Ability);
                        if (apCost <= remainingAP)
                        {
                            remainingAP -= apCost;

                            // ★ v3.8.20: AllyTarget 도발 (FightMe 등) - 아군 주변 적 도발
                            if (bestOption.IsAllyTargetTaunt && bestOption.TargetAlly != null)
                            {
                                actions.Add(PlannedAction.Buff(bestOption.Ability, bestOption.TargetAlly,
                                    $"AllyTaunt - protecting {bestOption.TargetAlly.CharacterName} from {bestOption.EnemiesAffected} enemies", apCost));
                                Main.Log($"[Tank] AllyTaunt: {bestOption.Ability.Name} -> {bestOption.TargetAlly.CharacterName}");
                            }
                            else if (CombatAPI.IsPointTargetAbility(bestOption.Ability))
                            {
                                // ★ v3.1.26: AOE 도발 - TargetPoint 사용 (적 중심점)
                                // Position = 캐스터 이동 위치, TargetPoint = 시전 타겟 위치
                                // CanTargetSelf=false인 스킬의 경우 적 중심점을 타겟으로 지정
                                actions.Add(PlannedAction.PositionalBuff(
                                    bestOption.Ability, bestOption.TargetPoint,
                                    $"AOE Taunt - {bestOption.EnemiesAffected} enemies ({bestOption.EnemiesTargetingAllies} targeting allies)", apCost));
                            }
                            else
                            {
                                // 단일 타겟 도발: 자신에게 시전 (Self-Target 도발)
                                actions.Add(PlannedAction.Buff(bestOption.Ability, situation.Unit,
                                    $"Taunt - protecting {bestOption.EnemiesTargetingAllies} allies from threats", apCost));
                            }
                        }
                    }
                }
                else if (situation.EnemiesTargetingAllies > 0)
                {
                    // 도발 옵션이 없지만 아군이 위협받는 상황 → 로그만 남김
                    Main.LogDebug($"[Tank] SmartTaunt: {situation.EnemiesTargetingAllies} enemies targeting allies, but no worthwhile taunt option");
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

            // ★ v3.5.37: Phase 4.8 - AOE 공격 기회
            bool didPlanAttack = false;
            // ★ v3.8.44: 공격 실패 이유 추적 (이동 Phase에 전달)
            var attackContext = new AttackPhaseContext();

            // ★ v3.8.50: Phase 4.8a: Self-Targeted AOE (BladeDance 등)
            var selfAoEAction = PlanSelfTargetedAoE(situation, ref remainingAP);
            if (selfAoEAction != null)
            {
                actions.Add(selfAoEAction);
                didPlanAttack = true;
                Main.Log($"[Tank] Phase 4.8a: Self-Targeted AOE planned");
            }

            // ★ v3.8.50: Phase 4.8b: Melee AOE (유닛 타겟 근접 스플래시)
            // Tank는 근접 역할이므로 근접 AOE 사용이 매우 적합
            if (!didPlanAttack && remainingAP >= 1f)
            {
                var meleeAoEAction = PlanMeleeAoE(situation, ref remainingAP);
                if (meleeAoEAction != null)
                {
                    actions.Add(meleeAoEAction);
                    didPlanAttack = true;
                    Main.Log($"[Tank] Phase 4.8b: Melee AOE planned");
                }
            }

            // ★ v3.5.37: Phase 4.8c: Point-AOE (Tank도 DangerousAoE 사용)
            // ★ v3.8.50: 근접 AOE도 클러스터 탐지에 포함
            var pointAoEAttacks = situation.AvailableAttacks
                .Where(a => CombatAPI.IsPointTargetAbility(a) || AbilityDatabase.IsDangerousAoE(a) ||
                            CombatAPI.IsMeleeAoEAbility(a))
                .ToList();
            if (!didPlanAttack && situation.HasLivingEnemies && pointAoEAttacks.Count > 0)
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
                        Main.Log($"[Tank] Phase 4.8c: Point-AOE attack planned");
                    }
                }
            }

            // Phase 5: 공격 - ★ v3.1.21: Tank 가중치로 최적 타겟 선택
            int attacksPlanned = 0;
            var plannedTargetIds = new HashSet<string>();
            var plannedAbilityGuids = new HashSet<string>();

            // ★ v3.6.14: AP >= 0 으로 완화 (bonus usage 공격은 0 AP로 사용 가능)
            while (remainingAP >= 0f && situation.HasHittableEnemies && attacksPlanned < MAX_ATTACKS_PER_PLAN)
            {
                // ★ v3.1.21: Tank 가중치로 최적 타겟 선택 (거리 + 위협도 중시)
                var candidates = situation.HittableEnemies
                    .Where(e => e != null && !plannedTargetIds.Contains(e.UniqueId))
                    .ToList();
                var bestTarget = TargetScorer.SelectBestEnemy(candidates, situation, Settings.AIRole.Tank)
                    ?? situation.NearestEnemy;

                // ★ v3.2.15: 아군을 위협하는 적 우선 공격 (Tank 보호 역할)
                var threateningEnemy = situation.Enemies
                    .Where(e => e != null && situation.HittableEnemies.Contains(e) &&
                                !plannedTargetIds.Contains(e.UniqueId) &&
                                TeamBlackboard.Instance.CountAlliesTargeting(e) > 0)
                    .OrderByDescending(e => TeamBlackboard.Instance.CountAlliesTargeting(e))
                    .FirstOrDefault();

                if (threateningEnemy != null)
                {
                    bestTarget = threateningEnemy;
                    Main.LogDebug($"[Tank] Phase 5: Priority target {threateningEnemy.CharacterName} (threatening allies)");
                }

                // ★ v3.8.44: attackContext 전달 - 실패 이유 기록
                var attackAction = PlanAttack(situation, ref remainingAP, attackContext,
                    preferTarget: bestTarget, excludeTargetIds: plannedTargetIds,
                    excludeAbilityGuids: plannedAbilityGuids);
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
                // ★ v3.6.22: Hittable 적이 2명 이상일 때만 타겟 제외 (다중 적 분산 공격)
                // 1명뿐이면 계속 공격할 수 있도록 제외하지 않음
                if (targetEntity != null)
                {
                    if (situation.HittableEnemies.Count > 1)
                    {
                        plannedTargetIds.Add(targetEntity.UniqueId);
                    }
                    else
                    {
                        Main.LogDebug($"[Tank] Phase 4: Allow re-attack on {targetEntity.CharacterName} (only 1 hittable enemy)");
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

            // Phase 6: PostFirstAction
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
                        Main.Log($"[Tank] Phase 6: {postAction.Ability.Name} will restore ~{expectedMP:F0} MP (predicted MP={remainingMP:F1})");
                    }
                }
            }

            // ★ v3.0.96: Phase 6.5: 공격 불가 시 남은 버프 사용
            // 이전 버그: 점화 후 Hittable=0이면 강철 팔 등 버프 사용 못함
            // HasPerformedFirstAction=true여도 남은 AP로 버프 사용
            // ★ v3.1.10: PreAttackBuff, HeroicAct, RighteousFury 제외 (공격 없으면 무의미)
            if (!didPlanAttack && remainingAP >= 1f && situation.AvailableBuffs.Count > 0)
            {
                Main.Log($"[Tank] Phase 6.5: No attack possible, using remaining buffs (AP={remainingAP:F1})");

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
                        Main.LogDebug($"[Tank] Phase 6.5: Skip {buff.Name} (timing={timing} not suitable for fallback)");
                        continue;
                    }

                    // ★ v3.5.22: SpringAttack 능력은 조건 충족 시에만 TurnEnding에서 사용
                    if (AbilityDatabase.IsSpringAttackAbility(buff))
                    {
                        Main.LogDebug($"[Tank] Phase 6.5: Skip {buff.Name} (SpringAttack - use in TurnEnding only)");
                        continue;
                    }

                    float cost = CombatAPI.GetAbilityAPCost(buff);
                    if (cost > remainingAP) continue;

                    // 이미 활성화된 버프 스킵
                    if (CombatAPI.HasActiveBuff(situation.Unit, buff)) continue;

                    // ★ Self 또는 Ally 타겟 버프 (강철 팔은 AllyTarget이므로 CanTargetFriends 체크)
                    var bp = buff.Blueprint;
                    if (bp?.CanTargetSelf != true && bp?.CanTargetFriends != true) continue;

                    var target = new TargetWrapper(situation.Unit);
                    string reason;
                    if (CombatAPI.CanUseAbilityOn(buff, target, out reason))
                    {
                        remainingAP -= cost;
                        actions.Add(PlannedAction.Buff(buff, situation.Unit, "Fallback buff - no attack available", cost));
                        Main.Log($"[Tank] Fallback buff: {buff.Name}");
                    }
                }
            }

            // ★ v3.5.35: Phase 7 (TurnEnding) → 맨 마지막으로 이동
            // TurnEnding 능력은 턴을 종료시키므로 다른 모든 행동 후에 계획해야 함

            // ★ Phase 8: 이동 또는 GapCloser (공격 가능한 적이 없을 때)
            // ★ v3.0.55: remainingMP 체크 - 계획된 능력들의 MP 코스트 반영
            // ★ v3.0.90: 공격 계획 실패 시에도 이동 허용
            // ★ v3.0.99: MP 회복 예측 후 이동 가능
            // ★ v3.1.01: predictedMP를 MovementAPI에 전달하여 reachable tiles 계산에 사용
            // ★ v3.2.25: 전선 유지 로직 - Tank가 전선 뒤에 있으면 전진 필요
            // ★ v3.5.17: Tank 적극적 접근 - 공격 후에도 근접 거리로 이동
            // ★ v3.5.36: GapCloser도 이동으로 취급 (중복 계획 방지)
            bool hasMoveInPlan = actions.Any(a => a.Type == ActionType.Move ||
                (a.Type == ActionType.Attack && a.Ability != null && AbilityDatabase.IsGapCloser(a.Ability)));
            bool canMove = situation.CanMove || remainingMP > 0;

            // ★ v3.5.17: Tank는 근접 캐릭터이므로 적에게 접근해야 함
            // 공격 후에도 적과 거리가 멀면(근접 공격 불가) 이동 필요
            float meleeEngageDistance = 3f;  // 근접 공격 거리
            bool shouldEngageMelee = !situation.PrefersRanged &&
                                      situation.HasLivingEnemies &&
                                      situation.NearestEnemyDistance > meleeEngageDistance;

            // ★ v3.2.25: Tank 전선 유지 - 전선 뒤에 있으면 전진 필요
            bool shouldAdvanceToFrontline = false;
            if (situation.InfluenceMap != null && situation.InfluenceMap.IsValid && situation.HasLivingEnemies)
            {
                float frontlineDist = situation.InfluenceMap.GetFrontlineDistance(situation.Unit.Position);
                // 전선 뒤(-5m 이하)에 있으면 전진 필요
                if (frontlineDist < -5f)
                {
                    shouldAdvanceToFrontline = true;
                    Main.LogDebug($"[Tank] Phase 8: Behind frontline ({frontlineDist:F1}m) - should advance");
                }
            }

            // ★ v3.5.17: Tank 이동 조건 확장
            // - 기존: NeedsReposition || 공격 실패 시
            // - 추가: 적과 근접 거리 밖이면 공격 후에도 이동
            // ★ v3.8.45: 원거리 + AvailableAttacks=0 → 적에게 접근 무의미
            bool noAttackNoApproach = situation.PrefersRanged && situation.AvailableAttacks.Count == 0;
            // NeedsReposition도 noAttackNoApproach 적용
            bool needsMovement = ((situation.NeedsReposition || (!didPlanAttack && situation.HasLivingEnemies)) && !noAttackNoApproach) ||
                                 shouldAdvanceToFrontline ||
                                 shouldEngageMelee;

            if (!hasMoveInPlan && needsMovement && canMove && remainingMP > 0)
            {
                Main.Log($"[Tank] Phase 8: Trying move (attack={didPlanAttack}, MP={remainingMP:F1}, " +
                    $"engageMelee={shouldEngageMelee}, advanceFrontline={shouldAdvanceToFrontline}, dist={situation.NearestEnemyDistance:F1}m)");
                // ★ v3.0.90: 공격 실패 시 forceMove=true로 이동 강제
                // ★ v3.2.25: 전선 유지 위해 전진 필요해도 forceMove
                // ★ v3.5.17: Tank 근접 접근 필요해도 forceMove
                // ★ v3.8.44: HasHittableEnemies → attackContext.ShouldForceMove (실패 이유 기반)
                bool forceMove = (!didPlanAttack && attackContext.ShouldForceMove) ||
                                 shouldAdvanceToFrontline ||
                                 shouldEngageMelee;
                Main.LogDebug($"[Tank] Phase 8: {attackContext}, forceMove={forceMove}");
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
                            Main.Log($"[Tank] Added post-move attack (from destination={moveDestination.HasValue})");
                        }
                    }
                }
            }

            // ★ v3.8.45: Phase 8.5 - 원거리 Tank 안전 후퇴
            // 원거리 설정 Tank가 공격 후 적 근처에 남아있으면 후퇴
            if (!hasMoveInPlan && remainingMP > 0 && situation.CanMove && situation.PrefersRanged)
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
                        hasMoveInPlan = true;
                        Main.Log($"[Tank] Phase 8.5: Post-action safe retreat: {retreatReason}");
                    }
                }
            }

            // Post-attack phase
            if ((situation.HasAttackedThisTurn || didPlanAttack) && remainingAP >= 1f)
            {
                var postAttackActions = PlanPostAttackActions(situation, ref remainingAP, skipMove: hasMoveInPlan);
                actions.AddRange(postAttackActions);
            }

            // ★ v3.1.24: Phase 9 - 최종 AP 활용 (모든 시도 실패 후)
            if (remainingAP >= 1f && actions.Count > 0)
            {
                var finalAction = PlanFinalAPUtilization(situation, ref remainingAP);
                if (finalAction != null)
                {
                    actions.Add(finalAction);
                    Main.Log($"[Tank] Phase 9: Final AP utilization - {finalAction.Ability?.Name}");
                }
            }

            // ★ v3.5.35: Phase 10 - 턴 종료 스킬 (항상 마지막!)
            // TurnEnding 능력은 턴을 즉시 종료하므로 반드시 마지막에 배치
            var turnEndAction = PlanTurnEndingAbility(situation, ref remainingAP);
            if (turnEndAction != null)
            {
                actions.Add(turnEndAction);
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

            // ★ v3.1.09: InitialAP/InitialMP 전달 (리플랜 감지용)
            // ★ v3.5.88: 0 AP 공격 수 전달 (Break Through → Slash 감지용)
            int zeroAPAttackCount = CombatAPI.GetZeroAPAttacks(situation.Unit).Count;
            return new TurnPlan(actions, priority, reasoning, situation.HPPercent, situation.NearestEnemyDistance,
                situation.HittableEnemies?.Count ?? 0, situation.CurrentAP, situation.CurrentMP, zeroAPAttackCount);
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

                // ★ v3.5.75: 통합 API 사용
                if (!AbilityDatabase.IsDefensiveStance(ability))
                    continue;

                float cost = CombatAPI.GetAbilityAPCost(ability);

                // 방어 자세는 필수 버프 - 예약 무시
                bool isEssential = IsEssentialBuff(ability, situation);
                if (!CanAffordBuffWithReservation(cost, remainingAP, reservedAP, isEssential))
                    continue;

                if (CombatAPI.HasActiveBuff(situation.Unit, ability)) continue;

                // ★ v3.8.25: AbilityCasterHasFacts 검증 (스택 버프 필요 여부)
                // GetUnavailabilityReasons()가 감지하지 못하는 캐스터 제한 검증
                string factReason;
                if (!CombatAPI.MeetsCasterFactRequirements(ability, out factReason))
                {
                    Main.LogDebug($"[Tank] DefensiveStance skipped - {factReason}");
                    continue;
                }

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

        // ★ v3.5.10: PlanTaunt() 삭제 - SmartTaunt 시스템(Phase 4)으로 완전 대체됨

        #endregion
    }
}
