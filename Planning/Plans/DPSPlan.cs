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
using CompanionAI_v3.Settings;

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
            // ★ v3.5.80: 공격 가능 + MP 회복 PostAction 있으면 후퇴를 Phase 8로 미룸
            // 공격 → 런앤건(MP회복) → 후퇴 순서가 더 효율적
            bool deferRetreat = false;
            if (ShouldRetreat(situation))
            {
                // 공격 가능하고 MP 회복 PostAction이 있으면 후퇴를 뒤로 미룸
                bool hasPostActionMPRecovery = situation.AvailableBuffs
                    .Any(a => AbilityDatabase.GetTiming(a) == AbilityTiming.PostFirstAction &&
                              AbilityDatabase.GetExpectedMPRecovery(a) > 0);

                if (situation.HasHittableEnemies && hasPostActionMPRecovery)
                {
                    deferRetreat = true;
                    Main.LogDebug($"[DPS] Phase 1.6: Deferring retreat - will attack + use MP recovery first");
                }
                else
                {
                    var retreatAction = PlanRetreat(situation);
                    if (retreatAction != null)
                    {
                        actions.Add(retreatAction);
                    }
                }
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

                // 3. Relocate: 사역마를 최적 위치로 이동 (Mastiff 제외)
                var familiarRelocate = PlanFamiliarRelocate(situation, ref remainingAP);
                if (familiarRelocate != null)
                    actions.Add(familiarRelocate);

                // ★ v3.7.02: 4. 키스톤 버프/디버프 루프 (Servo-Skull/Raven)
                var keystoneActions = PlanAllFamiliarKeystoneBuffs(situation, ref remainingAP);
                if (keystoneActions.Count > 0)
                {
                    actions.AddRange(keystoneActions);
                    Main.Log($"[DPS] Phase 1.75: {keystoneActions.Count} keystone abilities planned");
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

                // 8. Eagle: 시야 방해 (적 클러스터 교란)
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

            // Phase 2: Heroic Act (Momentum 175+)
            var heroicAction = PlanHeroicAct(situation, ref remainingAP);
            if (heroicAction != null)
            {
                actions.Add(heroicAction);
            }

            // Phase 3: 마무리 스킬 우선 (적 HP 30% 미만)
            // ★ v3.2.30: KillSimulator로 확정 킬 시퀀스 탐색 (설정으로 토글 가능)
            bool useKillSimulator = situation.CharacterSettings?.UseKillSimulator ?? true;
            bool didPlanKillSequence = false;

            // ★ v3.5.79: Phase 3에서 킬 시퀀스 타겟을 Phase 5와 공유하기 위해 미리 초기화
            var plannedTargetIds = new HashSet<string>();
            var plannedAbilityGuids = new HashSet<string>();
            BaseUnitEntity killSequenceTarget = null;  // 킬 시퀀스로 계획된 타겟

            if (useKillSimulator && situation.BestTarget != null)
            {
                var killSequence = KillSimulator.FindKillSequence(situation, situation.BestTarget);
                if (killSequence != null && killSequence.IsConfirmedKill && killSequence.APCost <= remainingAP)
                {
                    Main.Log($"[DPS] Phase 3: Kill sequence found for {situation.BestTarget.CharacterName} ({killSequence.Abilities.Count} abilities, {killSequence.TotalDamage:F0} dmg)");

                    foreach (var ability in killSequence.Abilities)
                    {
                        // ★ v3.4.01: P1-1 능력 사용 가능 여부 재확인
                        List<string> unavailReasons;
                        if (!CombatAPI.IsAbilityAvailable(ability, out unavailReasons))
                        {
                            Main.LogDebug($"[DPS] Kill sequence ability no longer available: {ability.Name} ({string.Join(", ", unavailReasons)})");
                            break;  // 시퀀스 중단
                        }

                        float apCost = ability.CalculateActionPointCost();
                        if (remainingAP >= apCost)
                        {
                            var timing = AbilityDatabase.GetTiming(ability);
                            // ★ v3.5.00: SelfBuff → PreCombatBuff (SelfBuff enum 없음)
                            if (timing == AbilityTiming.PreAttackBuff || timing == AbilityTiming.PreCombatBuff)
                            {
                                // ★ v3.4.02: P0 수정 - reason, apCost 파라미터 추가
                                actions.Add(PlannedAction.Buff(ability, situation.Unit, "Kill sequence buff", apCost));
                            }
                            else
                            {
                                // ★ v3.4.02: P0 수정 - reason, apCost 파라미터 추가
                                actions.Add(PlannedAction.Attack(ability, killSequence.Target, "Kill sequence attack", apCost));
                            }
                            remainingAP -= apCost;
                        }
                    }

                    if (actions.Count > 0)
                    {
                        didPlanKillSequence = true;
                        // ★ v3.5.79: 킬 시퀀스 타겟을 Phase 5에서 SharedTarget으로 덮어쓰지 않도록 등록
                        killSequenceTarget = killSequence.Target;
                        if (killSequenceTarget != null)
                        {
                            plannedTargetIds.Add(killSequenceTarget.UniqueId);
                            Main.LogDebug($"[DPS] Phase 3: Kill sequence target {killSequenceTarget.CharacterName} added to plannedTargetIds");
                        }
                    }
                }
            }

            // 킬 시퀀스로 계획하지 않았으면 기존 Finisher 로직 사용
            if (!didPlanKillSequence)
            {
                var lowHPEnemy = FindLowHPEnemy(situation, 30f);
                if (lowHPEnemy != null)
                {
                    var finisherAction = PlanFinisher(situation, lowHPEnemy, ref remainingAP);
                    if (finisherAction != null)
                    {
                        actions.Add(finisherAction);
                    }
                }
            }

            // Phase 4: 공격 버프 (첫 행동 전)
            // ★ v3.2.15: Retreat 전술이면 버프 스킵 (생존 우선)
            // ★ v3.2.20: 신뢰도가 높으면(>0.75) 버프 스킵하고 즉시 공격
            float confidence = GetTeamConfidence();
            bool isRetreatMode = TeamBlackboard.Instance.CurrentTactic == TacticalSignal.Retreat;
            bool veryConfident = confidence > 0.75f;

            if (!situation.HasPerformedFirstAction && !situation.HasBuffedThisTurn &&
                !isRetreatMode && !veryConfident)
            {
                var buffAction = PlanAttackBuffWithReservation(situation, ref remainingAP, reservedAP);
                if (buffAction != null)
                {
                    actions.Add(buffAction);
                }
            }
            else if (veryConfident && !situation.HasPerformedFirstAction)
            {
                Main.LogDebug($"[DPS] Phase 4: Skipping buff (confidence={confidence:F2} > 0.75)");
            }

            // ★ v3.1.16: didPlanAttack 변수를 여기서 미리 선언 (Phase 4.4 AOE용)
            bool didPlanAttack = false;

            // ★ v3.1.29: Phase 4.3: Self-Targeted AOE (BladeDance 등)
            // 아군이 인접하지 않고 적이 인접해 있을 때만 사용
            var selfAoEAction = PlanSelfTargetedAoE(situation, ref remainingAP);
            if (selfAoEAction != null)
            {
                actions.Add(selfAoEAction);
                didPlanAttack = true;
                Main.Log($"[DPS] Phase 4.3: Self-Targeted AOE planned");
            }

            // ★ v3.1.16: Phase 4.4: AOE 공격 (적 2명 이상 근처일 때)
            // ★ v3.3.00: 클러스터 기반 AOE 기회 탐색
            // ★ v3.5.37: MinEnemiesForAoE 설정 적용
            // ★ v3.5.74: AttackCategory.AoE 추가 체크 (v3.5.73 API 활용)
            int minEnemies = situation.CharacterSettings?.MinEnemiesForAoE ?? 2;
            if (remainingAP >= 1f && situation.Enemies.Count >= minEnemies)
            {
                bool hasAoEOpportunity = false;
                bool useAoEOptimization = situation.CharacterSettings?.UseAoEOptimization ?? true;

                if (useAoEOptimization)
                {
                    // ★ v3.5.74: 클러스터 기반 AOE 기회 탐색 (게임 API + AttackCategory)
                    foreach (var aoeAbility in situation.AvailableAttacks
                        .Where(a => CombatAPI.IsPointTargetAbility(a) ||
                                    CombatAPI.GetAttackCategory(a) == AttackCategory.AoE))
                    {
                        float aoERadius = CombatAPI.GetAoERadius(aoeAbility);  // 타일 반환
                        // ★ v3.6.4: 기본 5타일 ≈ 6.75m
                        if (aoERadius <= 0) aoERadius = 5f;

                        var clusters = Analysis.ClusterDetector.FindClusters(situation.Enemies, aoERadius);
                        if (clusters.Any(c => c.Count >= minEnemies))
                        {
                            hasAoEOpportunity = true;
                            Main.LogDebug($"[DPS] Phase 4.4: Cluster found for {aoeAbility.Name} (radius={aoERadius:F1}m)");
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
                        Main.Log($"[DPS] Phase 4.4: AOE attack planned ({(useAoEOptimization ? "cluster-based" : "legacy")})");
                    }
                }
            }

            // ★ v3.1.22: Phase 4.5: 특수 능력 + 콤보 연계 감지
            // GetComboPrerequisite()를 호출하여 DOT 강화 전 DOT 적용 필요 여부 확인
            AbilityData comboPrereqAbility = null;
            AbilityData comboFollowUpAbility = null;

            var specialAction = PlanSpecialAbilityWithCombo(situation, ref remainingAP,
                out comboPrereqAbility, out comboFollowUpAbility);
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
            // ★ v3.1.16: didPlanAttack은 Phase 4.4에서 이미 선언됨
            // ★ v3.1.22: 콤보 선행 능력(comboPrereqAbility) 우선 계획
            // ★ v3.5.79: plannedTargetIds, plannedAbilityGuids는 Phase 3에서 이미 초기화됨
            int attacksPlanned = 0;
            bool usedComboPrereq = false;

            // ★ v3.0.87: Phase 5 진입 상태 로깅
            Main.LogDebug($"[DPS] Phase 5 entry: AP={remainingAP:F1}, HasHittable={situation.HasHittableEnemies}, " +
                $"HittableCount={situation.HittableEnemies?.Count ?? 0}, AvailableAttacks={situation.AvailableAttacks?.Count ?? 0}");

            // ★ v3.1.22: 콤보 선행 능력 로깅
            if (comboPrereqAbility != null)
            {
                Main.Log($"[DPS] Phase 5: Combo prerequisite detected - will prioritize {comboPrereqAbility.Name}");
            }

            // ★ v3.6.14: AP >= 0 으로 완화 (bonus usage 공격은 0 AP로 사용 가능)
            // AttackPlanner.PlanAttack()이 GetEffectiveAPCost()로 AP 체크하므로 안전
            while (remainingAP >= 0f && situation.HasHittableEnemies && attacksPlanned < MAX_ATTACKS_PER_PLAN)
            {
                var weakestEnemy = FindWeakestEnemy(situation, plannedTargetIds);
                var preferTarget = weakestEnemy ?? situation.BestTarget;

                // ★ v3.5.84: SharedTarget vs BestTarget 점수 비교 (무조건 덮어쓰기 제거)
                // SharedTarget이 10% 이내 열세이면 팀 협력 우선, 아니면 BestTarget 유지
                var sharedTarget = TeamBlackboard.Instance.SharedTarget;
                if (sharedTarget != null && situation.HittableEnemies.Contains(sharedTarget) &&
                    !plannedTargetIds.Contains(sharedTarget.UniqueId))
                {
                    float bestScore = preferTarget != null ?
                        TargetScorer.ScoreEnemy(preferTarget, situation, AIRole.DPS) : 0f;
                    float sharedScore = TargetScorer.ScoreEnemy(sharedTarget, situation, AIRole.DPS);

                    // SharedTarget이 90% 이상 점수면 팀 협력 우선
                    if (sharedScore >= bestScore * 0.9f)
                    {
                        preferTarget = sharedTarget;
                        Main.LogDebug($"[DPS] Phase 5: Using SharedTarget {sharedTarget.CharacterName} (score={sharedScore:F0} vs best={bestScore:F0})");
                    }
                    else
                    {
                        Main.LogDebug($"[DPS] Phase 5: Keeping BestTarget {preferTarget?.CharacterName} (SharedTarget {sharedTarget.CharacterName} score={sharedScore:F0} < {bestScore * 0.9f:F0})");
                    }
                }

                PlannedAction attackAction = null;

                // ★ v3.1.22: 첫 공격에서 콤보 선행 능력 우선 사용
                if (comboPrereqAbility != null && !usedComboPrereq)
                {
                    attackAction = PlanAttackWithPreferredAbility(situation, ref remainingAP,
                        preferTarget, comboPrereqAbility, plannedTargetIds);
                    if (attackAction != null)
                    {
                        usedComboPrereq = true;
                        Main.Log($"[DPS] Phase 5: Used combo prerequisite {comboPrereqAbility.Name}");
                    }
                }

                // 일반 공격 폴백
                if (attackAction == null)
                {
                    attackAction = PlanAttack(situation, ref remainingAP, preferTarget: preferTarget,
                        excludeTargetIds: plannedTargetIds, excludeAbilityGuids: plannedAbilityGuids);
                }

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
                        Main.LogDebug($"[DPS] Phase 5: Allow re-attack on {targetEntity.CharacterName} (only 1 hittable enemy)");
                    }
                }

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

            // ★ v3.1.22: Phase 5.5: 콤보 후속 능력 (DOT 적용 후 DOT 강화)
            // Phase 5에서 콤보 선행 능력(예: Inferno)을 사용했으면, 이제 후속 능력(예: Shape Flames) 사용
            if (comboFollowUpAbility != null && usedComboPrereq && remainingAP >= 1f)
            {
                float followUpCost = CombatAPI.GetAbilityAPCost(comboFollowUpAbility);
                if (followUpCost <= remainingAP)
                {
                    // 콤보 선행 능력을 맞은 적에게 후속 능력 사용
                    foreach (var enemy in situation.Enemies)
                    {
                        if (enemy == null || enemy.LifeState.IsDead) continue;

                        // DOT가 있는 적에게만 DOT 강화 사용
                        if (!SpecialAbilityHandler.CanUseSpecialAbilityEffectively(
                            comboFollowUpAbility, enemy, situation.Enemies))
                            continue;

                        var targetWrapper = new TargetWrapper(enemy);
                        string reason;
                        if (CombatAPI.CanUseAbilityOn(comboFollowUpAbility, targetWrapper, out reason))
                        {
                            remainingAP -= followUpCost;
                            actions.Add(PlannedAction.Attack(comboFollowUpAbility, enemy,
                                $"Combo followup: {comboFollowUpAbility.Name}", followUpCost));
                            Main.Log($"[DPS] Phase 5.5: Combo followup {comboFollowUpAbility.Name} -> {enemy.CharacterName}");
                            break;
                        }
                    }
                }
            }

            // ★ Phase 5.6: GapCloser (공격 계획 실패 시) - 기존 Phase 5.5
            // ★ v3.0.86: 거리 조건 제거 - 적이 4m에 있어도 근접 사거리(2m)에 못 들어올 수 있음
            // 기존: NearestEnemyDistance > 5f → 적이 5m 이내면 스킵 (버그!)
            // 수정: 공격 계획 실패 시 무조건 GapCloser 시도 (GapCloser 자체가 유효성 검사)

            // ★ v3.1.22: Phase 5.6 진입 전 상태 로깅 (기존 Phase 5.5)
            Main.LogDebug($"[DPS] Phase 5.6 check: didPlanAttack={didPlanAttack}, HasHittableEnemies={situation.HasHittableEnemies}, " +
                $"NearestEnemy={situation.NearestEnemy?.CharacterName ?? "null"}, Distance={situation.NearestEnemyDistance:F1}m, AP={remainingAP:F1}");

            if (!didPlanAttack && situation.NearestEnemy != null)
            {
                Main.Log($"[DPS] Phase 5.6: Trying GapCloser as fallback (attack failed)");
                // ★ v3.5.34: MP 비용 예측 버전 사용
                var gapCloserAction = PlanGapCloser(situation, situation.NearestEnemy, ref remainingAP, ref remainingMP);
                if (gapCloserAction != null)
                {
                    actions.Add(gapCloserAction);
                    didPlanAttack = true;  // GapCloser도 공격으로 취급
                    Main.Log($"[DPS] GapCloser fallback: {gapCloserAction.Ability?.Name}");
                }
                else
                {
                    Main.LogDebug($"[DPS] Phase 5.6: GapCloser returned null");
                }
            }
            else if (didPlanAttack)
            {
                Main.LogDebug($"[DPS] Phase 5.6: Skipped - already planned attack");
            }
            else
            {
                Main.LogDebug($"[DPS] Phase 5.6: Skipped - NearestEnemy is null");
            }

            // Phase 6: PostFirstAction
            // ★ v3.5.80: didPlanAttack 전달하여 공격이 계획됨도 런앤건 허용
            if (situation.HasPerformedFirstAction || didPlanAttack)
            {
                var postAction = PlanPostAction(situation, ref remainingAP, didPlanAttack);
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
                    // ★ v3.5.22: TurnEnding, SpringAttack 능력도 폴백에서 제외
                    var timing = AbilityDatabase.GetTiming(buff);
                    if (timing == AbilityTiming.PreAttackBuff ||
                        timing == AbilityTiming.HeroicAct ||
                        timing == AbilityTiming.RighteousFury ||
                        timing == AbilityTiming.TurnEnding)
                    {
                        Main.LogDebug($"[DPS] Phase 6.5: Skip {buff.Name} (timing={timing} not suitable for fallback)");
                        continue;
                    }

                    // ★ v3.5.22: SpringAttack 능력은 조건 충족 시에만 TurnEnding에서 사용
                    if (AbilityDatabase.IsSpringAttackAbility(buff))
                    {
                        Main.LogDebug($"[DPS] Phase 6.5: Skip {buff.Name} (SpringAttack - use in TurnEnding only)");
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

            // ★ v3.5.35: Phase 7 (TurnEnding) → 맨 마지막으로 이동
            // TurnEnding 능력은 턴을 종료시키므로 다른 모든 행동 후에 계획해야 함

            // ★ Phase 8: 이동 또는 GapCloser (공격 불가 시)
            // ★ v3.0.55: remainingMP 체크 - 계획된 능력들의 MP 코스트 반영
            // ★ v3.0.89: 공격 계획 실패 시에도 이동 허용
            // ★ v3.0.99: MP 회복 예측 후 이동 가능 - situation.CanMove는 계획 시작 시점 기준
            //            Phase 6에서 MP 회복을 예측했으면 remainingMP > 0으로 이동 가능
            // ★ v3.1.01: predictedMP를 MovementAPI에 전달하여 reachable tiles 계산에 사용
            // ★ v3.1.29: 원거리가 위험하면 공격 후에도 후퇴 이동 허용
            // ★ v3.5.36: GapCloser도 이동으로 취급 (Phase 5.6에서 GapCloser 계획 시 Phase 8 스킵)
            // ★ v3.5.80: deferRetreat - Phase 1.6에서 미뤄진 후퇴 처리
            bool hasMoveInPlan = actions.Any(a => a.Type == ActionType.Move ||
                (a.Type == ActionType.Attack && a.Ability != null && AbilityDatabase.IsGapCloser(a.Ability)));
            // ★ v3.1.29: 원거리가 위험하면 이동 필요
            bool isRangedInDanger = situation.PrefersRanged && situation.IsInDanger;
            // ★ v3.5.80: deferRetreat 포함 - 공격+런앤건 후 후퇴 필요
            bool needsMovement = situation.NeedsReposition || (!didPlanAttack && situation.HasLivingEnemies) || isRangedInDanger || deferRetreat;
            // ★ v3.0.99: situation.CanMove는 계획 시작 시점 MP 기준, remainingMP는 예측된 MP 포함
            bool canMove = situation.CanMove || remainingMP > 0;

            Main.LogDebug($"[DPS] Phase 8 check: hasMoveInPlan={hasMoveInPlan}, NeedsReposition={situation.NeedsReposition}, " +
                $"didPlanAttack={didPlanAttack}, needsMovement={needsMovement}, CanMove={canMove}, MP={remainingMP:F1}, IsInDanger={situation.IsInDanger}");

            if (!hasMoveInPlan && needsMovement && canMove && remainingMP > 0)
            {
                Main.Log($"[DPS] Phase 8: Trying move (attack planned={didPlanAttack}, predictedMP={remainingMP:F1}, isRangedInDanger={isRangedInDanger})");
                // ★ v3.0.89: 공격 실패 시 forceMove=true로 이동 강제
                // ★ v3.1.29: 원거리가 위험하면 공격 가능해도 후퇴 이동 강제
                bool forceMove = (!didPlanAttack && situation.HasHittableEnemies) || isRangedInDanger;
                // ★ v3.1.00: MP 회복 예측 후 situation.CanMove=False여도 이동 가능
                // PlanMoveToEnemy 내부의 CanMove 체크를 우회
                bool bypassCanMoveCheck = !situation.CanMove && remainingMP > 0;
                // ★ v3.1.01: remainingMP를 MovementAPI에 전달하여 실제로 이동 가능한 타일 계산
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
                            Main.Log($"[DPS] Added post-move attack (from destination={moveDestination.HasValue})");
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

            // ★ v3.1.24: Phase 9 - 최종 AP 활용 (모든 시도 실패 후)
            // 공격/이동 모두 실패했지만 AP가 남았을 때 저우선순위 버프/디버프/마커 사용
            if (remainingAP >= 1f && actions.Count > 0)
            {
                var finalAction = PlanFinalAPUtilization(situation, ref remainingAP);
                if (finalAction != null)
                {
                    actions.Add(finalAction);
                    Main.Log($"[DPS] Phase 9: Final AP utilization - {finalAction.Ability?.Name}");
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
                actions.Add(PlannedAction.EndTurn("DPS no targets"));
            }

            var priority = DeterminePriority(actions, situation);
            var reasoning = $"DPS: {DetermineReasoning(actions, situation)}";

            // ★ v3.0.55: MP 추적 로깅
            Main.LogDebug($"[DPS] Plan complete: AP={remainingAP:F1}, MP={remainingMP:F1} (started with {situation.CurrentMP:F1})");

            // ★ v3.1.09: InitialAP/InitialMP 전달 (리플랜 감지용)
            // ★ v3.5.88: 0 AP 공격 수 전달 (Break Through → Slash 감지용)
            int zeroAPAttackCount = CombatAPI.GetZeroAPAttacks(situation.Unit).Count;
            return new TurnPlan(actions, priority, reasoning, situation.HPPercent, situation.NearestEnemyDistance,
                situation.HittableEnemies?.Count ?? 0, situation.CurrentAP, situation.CurrentMP, zeroAPAttackCount);
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

        /// <summary>
        /// ★ v3.1.22: 특수 능력 계획 + 콤보 연계 감지
        /// DOT 강화 같은 능력이 콤보 선행 능력(Inferno 등)을 필요로 하면 감지하여 반환
        /// </summary>
        private PlannedAction PlanSpecialAbilityWithCombo(Situation situation, ref float remainingAP,
            out AbilityData comboPrereqAbility, out AbilityData comboFollowUpAbility)
        {
            comboPrereqAbility = null;
            comboFollowUpAbility = null;

            if (situation.AvailableSpecialAbilities == null || situation.AvailableSpecialAbilities.Count == 0)
                return null;

            var enemies = situation.Enemies;
            if (enemies == null || enemies.Count == 0)
                return null;

            float currentAP = remainingAP;

            // 콤보 능력 후보 목록 (공격 + 특수)
            var allAttackAbilities = new List<AbilityData>();
            if (situation.AvailableAttacks != null)
                allAttackAbilities.AddRange(situation.AvailableAttacks);
            allAttackAbilities.AddRange(situation.AvailableSpecialAbilities);

            foreach (var ability in situation.AvailableSpecialAbilities)
            {
                float cost = CombatAPI.GetAbilityAPCost(ability);
                if (cost > currentAP) continue;

                // 모든 적에 대해 이 능력 사용 가능 여부 확인
                foreach (var enemy in enemies)
                {
                    if (enemy == null || enemy.LifeState.IsDead) continue;

                    // ★ v3.1.22: 콤보 선행 능력 필요 여부 확인
                    // 예: Shape Flames가 DOT 없는 적에게 사용 불가 → Inferno 먼저 필요
                    if (!SpecialAbilityHandler.CanUseSpecialAbilityEffectively(ability, enemy, enemies))
                    {
                        // 콤보 선행 능력 찾기
                        var prereq = SpecialAbilityHandler.GetComboPrerequisite(ability, enemy, allAttackAbilities);
                        if (prereq != null)
                        {
                            // 콤보 선행 능력을 Phase 5에서 우선 사용하도록 설정
                            comboPrereqAbility = prereq;
                            comboFollowUpAbility = ability;
                            Main.Log($"[DPS] Phase 4.5: Combo detected - {prereq.Name} → {ability.Name}");
                            // 특수 능력은 여기서 사용하지 않고, Phase 5.5에서 사용
                            continue;
                        }
                        continue;
                    }

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

        /// <summary>
        /// ★ v3.1.22: 특정 능력을 우선 사용하는 공격 계획
        /// 콤보 선행 능력(Inferno 등)을 강제로 사용
        /// </summary>
        private PlannedAction PlanAttackWithPreferredAbility(Situation situation, ref float remainingAP,
            BaseUnitEntity preferTarget, AbilityData preferredAbility, HashSet<string> excludeTargetIds)
        {
            if (preferredAbility == null || preferTarget == null) return null;

            float cost = CombatAPI.GetAbilityAPCost(preferredAbility);
            if (cost > remainingAP) return null;

            // 타겟 제외 목록 체크
            if (excludeTargetIds != null && excludeTargetIds.Contains(preferTarget.UniqueId))
            {
                // 선호 타겟이 제외되어 있으면 다른 적 찾기
                foreach (var enemy in situation.Enemies)
                {
                    if (enemy == null || enemy.LifeState.IsDead) continue;
                    if (excludeTargetIds.Contains(enemy.UniqueId)) continue;

                    var targetWrapper = new TargetWrapper(enemy);
                    string reason;
                    if (CombatAPI.CanUseAbilityOn(preferredAbility, targetWrapper, out reason))
                    {
                        remainingAP -= cost;
                        Main.Log($"[DPS] Preferred ability: {preferredAbility.Name} -> {enemy.CharacterName}");
                        return PlannedAction.Attack(preferredAbility, enemy, $"Combo prereq on {enemy.CharacterName}", cost);
                    }
                }
                return null;
            }

            // 선호 타겟에게 능력 사용
            var target = new TargetWrapper(preferTarget);
            string unavailReason;
            if (CombatAPI.CanUseAbilityOn(preferredAbility, target, out unavailReason))
            {
                remainingAP -= cost;
                Main.Log($"[DPS] Preferred ability: {preferredAbility.Name} -> {preferTarget.CharacterName}");
                return PlannedAction.Attack(preferredAbility, preferTarget, $"Combo prereq on {preferTarget.CharacterName}", cost);
            }

            return null;
        }

        #endregion
    }
}
