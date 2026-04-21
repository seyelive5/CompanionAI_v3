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

        // ★ v3.9.10: Zero-alloc 정적 리스트 (LINQ 제거)
        private static readonly List<AbilityData> _sharedTauntAbilities = new List<AbilityData>(8);
        private static readonly List<BaseUnitEntity> _sharedCandidates = new List<BaseUnitEntity>(16);

        public override TurnPlan CreatePlan(Situation situation, TurnState turnState)
        {
            // ★ v3.104.0: CreatePlan 진입 시 버프 중복 추적 초기화
            ResetPlannedBuffTracking();

            var actions = new List<PlannedAction>();
            // ★ v3.0.68: 게임 AP 직접 사용
            float remainingAP = situation.CurrentAP;
            // ★ v3.0.55: MP 추적 - AP처럼 계획 단계에서 MP도 추적
            float remainingMP = situation.CurrentMP;

            // ★ v3.19.4: 통합 AP 예산 — CreateAPBudget 팩토리 + EffectiveReserved 자동 속성
            var budget = CreateAPBudget(situation, remainingAP);
            if (budget.PostMoveReserved > 0 || budget.TurnEndingReserved > 0)
            {
                Main.Log($"[Tank] {budget}");
            }

            // ★ v3.22.0: 전략 평가/재사용 — BasePlan.EvaluateOrReuseStrategy()로 통합
            TurnStrategy strategy = EvaluateOrReuseStrategy(situation, turnState, ref budget, "Tank", Settings.AIRole.Tank);

            // ★ v3.12.0: Phase 0~1.5 공통 처리 (Ultimate, AoE대피, 긴급힐, 재장전)
            var earlyReturn = ExecuteCommonEarlyPhases(actions, situation, ref remainingAP);
            if (earlyReturn != null) return earlyReturn;

            // ★ v3.19.0: Phase 1.55 — 무기 전환 (현재 무기 무용/비효율 시)
            if (situation.WeaponRotationAvailable
                && (!situation.HasHittableEnemies || ShouldSwitchForEffectiveness(situation))
                && ShouldSwitchFirst(situation))
            {
                var switchActions = PlanWeaponSetRotationAttack(situation, ref remainingAP);
                if (switchActions.Count > 0)
                {
                    actions.AddRange(switchActions);
                    Main.Log($"[Tank] Phase 1.55: Switch-First — switching weapon for better effectiveness");
                    return new TurnPlan(actions, TurnPriority.DirectAttack, "Tank weapon switch-first");
                }
            }

            // ★ v3.12.0: Phase 1.75 공통 Familiar 처리
            HashSet<string> _; // Tank는 키스톤 GUID 추적 불필요
            bool usedWarpRelay = ExecuteFamiliarSupportPhase(
                actions, situation, ref remainingAP,
                supportMode: false, out _);

            // ★ v3.40.0: Phase 1.8 — Cautious/Confident Approach 스탠스 선택 (Tank = 방어 우선)
            var approachStance = PlanApproachStance(situation, preferOffensive: false);
            if (approachStance != null) actions.Add(approachStance);

            // Phase 2: 방어 자세 (Confidence 기반 결정)
            // ★ v3.11.2: Curve 기반 연속 판단 (기존 confidence < 0.5f 이진 임계값 대체)
            // defenseNeed > 1.0 → 방어 필요 (confidence ~0.5 이하에서 부드럽게 전환)
            float defenseNeed = GetConfidenceDefenseNeed();  // 0.3 ~ 1.5
            bool needDefense = defenseNeed > 1.0f;

            // ★ v3.8.86: ClearMP 방어 자세는 이동 필요 시 Phase 8.9로 연기
            bool tankNeedsMovement = situation.NeedsReposition ||
                (!situation.HasHittableEnemies && situation.HasLivingEnemies && situation.CanMove);

            if (!situation.HasPerformedFirstAction && needDefense)
            {
                var defenseAction = PlanDefensiveStanceWithReservation(situation, ref remainingAP, budget.EffectiveReserved, tankNeedsMovement);
                if (defenseAction != null)
                {
                    actions.Add(defenseAction);
                    if (Main.IsDebugEnabled) Main.LogDebug($"[Tank] Phase 2: Defense stance (defenseNeed={defenseNeed:F2} > 1.0)");
                }
            }
            else if (!needDefense && !situation.HasPerformedFirstAction)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[Tank] Phase 2: Skipping defense stance (defenseNeed={defenseNeed:F2} <= 1.0)");
            }

            // Phase 3: 기타 선제적 버프
            if (!situation.HasBuffedThisTurn && !situation.HasPerformedFirstAction)
            {
                var buffAction = PlanBuffWithReservation(situation, ref remainingAP, budget.EffectiveReserved);
                if (buffAction != null)
                {
                    actions.Add(buffAction);
                }
            }

            // ★ v3.36.0: Phase 3.05 — 나머지 0 AP 공격 버프 전부 사용
            PlanFreeAttackBuffs(actions, situation);

            // ★ v3.9.44: Phase 3.5 - 아군 버프 (CanTargetFriends=true 버프를 아군에게 사용)
            if (remainingAP >= 1f)
            {
                var allyBuffAction = PlanAllyBuff(situation, ref remainingAP);
                if (allyBuffAction != null)
                {
                    actions.Add(allyBuffAction);
                    Main.Log($"[Tank] Phase 3.5: Ally buff planned - {allyBuffAction.Ability?.Name} -> {(allyBuffAction.Target?.Entity as BaseUnitEntity)?.CharacterName ?? "unknown"}");
                }
            }

            // ★ v3.1.25: Phase 4 - 스마트 도발 시스템
            // - 아군 타겟팅 적 탐지
            // - 이동 후 도발 타당성 스코어링
            // - AOE 도발 범위 정확 계산
            // ★ v3.9.10: LINQ → for loop (GC 할당 제거)
            _sharedTauntAbilities.Clear();
            for (int i = 0; i < situation.AvailableBuffs.Count; i++)
            {
                var a = situation.AvailableBuffs[i];
                if (AbilityDatabase.IsTaunt(a))
                    _sharedTauntAbilities.Add(a);
            }
            var tauntAbilities = _sharedTauntAbilities;

            if (tauntAbilities.Count > 0 && situation.HasLivingEnemies)
            {
                // 모든 도발 옵션 평가 (현재 위치 + 이동 가능 위치)
                var tauntOptions = TauntScorer.EvaluateAllTauntOptions(
                    situation, tauntAbilities, remainingMP);

                var bestOption = tauntOptions.Count > 0 ? tauntOptions[0] : null;

                // ★ v3.111.8: 임시턴에 이동 필요한 도발 스킵 (MP=0이라 이동 실패 → 사거리 밖 시전 버그)
                //   이동 없이 가능한 다음 옵션이 있으면 채택, 없으면 도발 자체 스킵.
                if (situation.IsExtraTurn && bestOption != null && bestOption.RequiresMove)
                {
                    Main.Log($"[Tank] SmartTaunt: Skip best option - extra turn + requires move (AP={situation.CurrentAP:F1}, MP={situation.CurrentMP:F1})");
                    bestOption = null;
                    for (int i = 0; i < tauntOptions.Count; i++)
                    {
                        var alt = tauntOptions[i];
                        if (alt != null && !alt.RequiresMove)
                        {
                            bestOption = alt;
                            Main.Log($"[Tank] SmartTaunt: Falling back to no-move option {alt.Ability.Name}");
                            break;
                        }
                    }
                }

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
                            if (Main.IsDebugEnabled) Main.LogDebug($"[Tank] SmartTaunt: Skip - {primaryTauntTarget.CharacterName} already reserved for taunt");
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
                            // ★ v3.19.8: 도발 위치가 위험 구역이면 이동 취소 (현재 안전할 때만)
                            if (!situation.NeedsAoEEvacuation &&
                                CombatAPI.IsPositionInHazardZone(bestOption.Position, situation.Unit))
                            {
                                Main.Log($"[Tank] SmartTaunt: Taunt position REJECTED — in hazard zone");
                            }
                            else
                            {
                                var moveAction = PlannedAction.Move(bestOption.Position, "Move for smart taunt");
                                actions.Add(moveAction);
                                remainingMP -= bestOption.MoveCost;
                                Main.Log($"[Tank] SmartTaunt: Moving to taunt position (cost={bestOption.MoveCost:F1} MP)");
                            }
                        }

                        // 도발 액션 추가
                        float apCost = CombatAPI.GetAbilityAPCost(bestOption.Ability);
                        if (apCost <= remainingAP)
                        {
                            remainingAP -= apCost;

                            // ★ v3.8.20: AllyTarget 도발 (FightMe 등) - 아군 주변 적 도발
                            PlannedAction tauntAction;
                            if (bestOption.IsAllyTargetTaunt && bestOption.TargetAlly != null)
                            {
                                tauntAction = PlannedAction.Buff(bestOption.Ability, bestOption.TargetAlly,
                                    $"AllyTaunt - protecting {bestOption.TargetAlly.CharacterName} from {bestOption.EnemiesAffected} enemies", apCost);
                                Main.Log($"[Tank] AllyTaunt: {bestOption.Ability.Name} -> {bestOption.TargetAlly.CharacterName}");
                            }
                            else if (CombatAPI.IsPointTargetAbility(bestOption.Ability))
                            {
                                // ★ v3.1.26: AOE 도발 - TargetPoint 사용 (적 중심점)
                                // Position = 캐스터 이동 위치, TargetPoint = 시전 타겟 위치
                                // CanTargetSelf=false인 스킬의 경우 적 중심점을 타겟으로 지정
                                tauntAction = PlannedAction.PositionalBuff(
                                    bestOption.Ability, bestOption.TargetPoint,
                                    $"AOE Taunt - {bestOption.EnemiesAffected} enemies ({bestOption.EnemiesTargetingAllies} targeting allies)", apCost);
                            }
                            else
                            {
                                // ★ v3.111.5: 단일 타겟 도발 — 실제 적 타겟으로 시전
                                // 이전: situation.Unit(자기자신)로 시전하여 게임 내장 LOS/Range 검증을 우회
                                //       → 벽 너머/사거리 밖 적에게도 도발이 실행되는 버그
                                // 수정: bestOption.AffectedEnemies[0]을 타겟으로 지정하여
                                //       CanUseAbilityOn 경로에서 Enemy-target 검증이 정상 동작
                                var singleTauntTarget = (bestOption.AffectedEnemies != null && bestOption.AffectedEnemies.Count > 0)
                                    ? bestOption.AffectedEnemies[0]
                                    : primaryTauntTarget;

                                if (singleTauntTarget != null)
                                {
                                    tauntAction = PlannedAction.Buff(bestOption.Ability, singleTauntTarget,
                                        $"Taunt -> {singleTauntTarget.CharacterName} (protecting {bestOption.EnemiesTargetingAllies} allies)", apCost);
                                }
                                else
                                {
                                    // 폴백: 적 타겟을 찾지 못하면 도발 시도 자체를 스킵
                                    Main.LogWarning($"[Tank] SmartTaunt: single-target taunt {bestOption.Ability.Name} — no enemy target found, skipping");
                                    tauntAction = null;
                                }
                            }

                            // ★ v3.11.2: 예약 타겟 추적 (실패 시 ReleaseTaunt에 사용)
                            // ★ v3.111.5: tauntAction이 null일 수 있음 (단일 타겟 폴백 실패)
                            if (tauntAction != null)
                            {
                                tauntAction.ReservedTarget = primaryTauntTarget;
                                actions.Add(tauntAction);
                            }
                        }
                    }
                }
                else if (situation.EnemiesTargetingAllies > 0)
                {
                    // 도발 옵션이 없지만 아군이 위협받는 상황 → 로그만 남김
                    if (Main.IsDebugEnabled) Main.LogDebug($"[Tank] SmartTaunt: {situation.EnemiesTargetingAllies} enemies targeting allies, but no worthwhile taunt option");
                }
            }

            // Phase 4.5: 마킹 (공격 전 적 지정)
            // ★ v3.9.50: Phase 5와 동일한 타겟 선택 (NearestEnemy 대신 TargetScorer + 위협 적 우선)
            if (situation.AvailableMarkers.Count > 0 && situation.HasHittableEnemies)
            {
                var markerTarget = TargetScorer.SelectBestEnemy(situation.HittableEnemies, situation, Settings.AIRole.Tank)
                    ?? situation.NearestEnemy;

                // 아군 위협 적 우선 (Phase 5 동일)
                var threateningEnemy = CollectionHelper.MaxByWhere(
                    situation.Enemies,
                    e => e != null && situation.HittableEnemies.Contains(e) &&
                         TeamBlackboard.Instance.CountAlliesTargeting(e) > 0,
                    e => (float)TeamBlackboard.Instance.CountAlliesTargeting(e));
                if (threateningEnemy != null)
                    markerTarget = threateningEnemy;

                if (markerTarget != null)
                {
                    var markerAction = PlanMarker(situation, markerTarget, ref remainingAP);
                    if (markerAction != null)
                    {
                        actions.Add(markerAction);
                    }
                }
            }

            // ★ v3.14.0: Phase 4.6 — 공통 위치 버프
            var usedPositionalBuffs = new HashSet<string>();
            ExecutePositionalBuffPhase(actions, situation, ref remainingAP, usedPositionalBuffs);

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
            // ★ v3.9.28: 이동이 이미 계획됨 → AttackPlanner에 pending move 알림
            if (CollectionHelper.Any(actions, a => a.Type == ActionType.Move))
                attackContext.HasPendingMove = true;

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

            // ★ v3.5.37: Phase 4.8c: AOE 공격 (모든 AoE 타입)
            // ★ v3.8.96: AvailableAoEAttacks 캐시 사용 + Unit-targeted AoE 추가
            // ★ v3.19.0: 전략이 AoE 추천 시 클러스터 검증 바이패스
            if (!didPlanAttack && situation.HasLivingEnemies && situation.HasAoEAttacks)
            {
                bool useAoEOptimization = situation.CharacterSettings?.UseAoEOptimization ?? true;
                int minEnemies = ClusterDetector.MIN_CLUSTER_SIZE;
                bool hasAoEOpportunity = false;

                // ★ v3.19.0: 전략이 AoE를 추천하면 클러스터 검증 스킵
                if (strategy?.ShouldPrioritizeAoE == true)
                {
                    hasAoEOpportunity = true;
                    Main.Log($"[Tank] Phase 4.8c: Strategy recommends AoE — bypassing cluster check");
                }
                else if (useAoEOptimization)
                {
                    // ★ v3.8.96: 캐시된 AvailableAoEAttacks 사용 (인라인 LINQ 제거)
                    foreach (var aoEAbility in situation.AvailableAoEAttacks)
                    {
                        float aoERadius = CombatAPI.GetAoERadius(aoEAbility);
                        if (aoERadius <= 0) aoERadius = 5f;
                        var clusters = ClusterDetector.FindClusters(situation.Enemies, aoERadius);
                        if (CollectionHelper.Any(clusters, c => c.Count >= minEnemies))
                        {
                            hasAoEOpportunity = true;
                            if (Main.IsDebugEnabled) Main.LogDebug($"[Tank] Phase 4.8c: Cluster found for {aoEAbility.Name} (category={CombatAPI.GetAttackCategory(aoEAbility)})");
                            break;
                        }
                    }
                }
                else
                {
                    // ★ v3.6.2: 레거시 경로도 타일 단위로 통일 (6타일 ≈ 8m)
                    int nearbyEnemies = CollectionHelper.CountWhere(situation.Enemies, e =>
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
                        Main.Log($"[Tank] Phase 4.8c: Point-target AOE planned");
                    }

                    // ★ v3.8.96: Unit-targeted AoE 시도 (Burst, Scatter, 기타 모든 유닛 타겟 AoE)
                    if (!didPlanAttack)
                    {
                        var unitAoE = PlanUnitTargetedAoE(situation, ref remainingAP);
                        if (unitAoE != null)
                        {
                            actions.Add(unitAoE);
                            didPlanAttack = true;
                            Main.Log($"[Tank] Phase 4.8c: Unit-targeted AOE planned");
                        }
                    }
                }
            }

            // ★ v3.9.08: Phase 4.8c.5: AoE 재배치 (Phase 4.8c 실패 시)
            if (!didPlanAttack && remainingAP >= 1f && remainingMP > 0 && situation.HasAoEAttacks
                && !CollectionHelper.Any(actions, a => a.Type == ActionType.Move))
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

                    Main.Log($"[Tank] Phase 4.8c.5: AoE reposition planned");
                }
            }

            // ══════════════════════════════════════════════════════════════
            // Phase 4.9: 전략 옵션 평가 (공격 전 이동 필요 여부 결정)
            // ★ v3.8.76: TacticalOptionEvaluator
            // ══════════════════════════════════════════════════════════════
            TacticalEvaluation tacticalEval = EvaluateTacticalOptions(situation);
            if (tacticalEval != null && tacticalEval.WasEvaluated)
            {
                bool shouldMoveBeforeAttack;
                bool shouldDeferRetreat;
                var tacticalMoveAction = ApplyTacticalStrategy(tacticalEval, situation,
                    out shouldMoveBeforeAttack, out shouldDeferRetreat);

                if (tacticalMoveAction != null)
                {
                    // ★ v3.16.0: MoveToAttack이면 갭클로저와 비교
                    if (tacticalEval.ChosenStrategy == TacticalStrategy.MoveToAttack)
                    {
                        PlannedAction gcPreMove;
                        var gcAction = EvaluateGapCloserAsAttack(
                            situation, ref remainingAP, ref remainingMP, out gcPreMove);

                        if (gcAction != null)
                        {
                            // ★ v3.16.6: Walk+Jump 콤보 시 사전 이동 추가
                            if (gcPreMove != null) actions.Add(gcPreMove);
                            actions.Add(gcAction);
                            didPlanAttack = true;  // ★ v3.18.16: Phase 4.9 갭클로저 추적
                            Main.Log($"[Tank] Phase 4.9: GapCloser replaces MoveToAttack{(gcPreMove != null ? " (walk+jump)" : "")}");
                            var landingPos = gcAction.MoveDestination ?? gcAction.Target?.Point;
                            if (landingPos.HasValue)
                                RecalculateHittableFromDestination(situation, landingPos.Value);
                        }
                        else
                        {
                            actions.Add(tacticalMoveAction);
                            Main.Log($"[Tank] Phase 4.9: Tactical pre-attack move");
                        }
                    }
                    else
                    {
                        actions.Add(tacticalMoveAction);
                        Main.Log($"[Tank] Phase 4.9: Tactical pre-attack move");
                    }
                }
                else if (!situation.HasHittableEnemies)
                {
                    // ★ v3.16.4: 모든 전략 불가능 → 갭클로저로 돌파
                    PlannedAction gcPreMove;
                    var gcAction = EvaluateGapCloserAsAttack(
                        situation, ref remainingAP, ref remainingMP, out gcPreMove);

                    if (gcAction != null)
                    {
                        if (gcPreMove != null) actions.Add(gcPreMove);
                        actions.Add(gcAction);
                        didPlanAttack = true;  // ★ v3.18.16: Phase 4.9 갭클로저 추적
                        Main.Log($"[Tank] Phase 4.9: GapCloser as last resort{(gcPreMove != null ? " (walk+jump)" : "")}");
                        var landingPos = gcAction.MoveDestination ?? gcAction.Target?.Point;
                        if (landingPos.HasValue)
                            RecalculateHittableFromDestination(situation, landingPos.Value);
                    }
                }
            }

            // Phase 5: 공격 - ★ v3.1.21: Tank 가중치로 최적 타겟 선택
            int attacksPlanned = 0;
            var plannedTargetIds = new HashSet<string>();
            var plannedAbilityGuids = new HashSet<string>();

            // ★ v3.19.2: APBudget.CanAfford()로 강제 — TurnEnding + Strategy 예약을 중앙 검증
            while (budget.CanAfford(0, remainingAP) && situation.HasHittableEnemies && attacksPlanned < MAX_ATTACKS_PER_PLAN)
            {
                // ★ v3.1.21: Tank 가중치로 최적 타겟 선택 (거리 + 위협도 중시)
                // ★ v3.9.10: LINQ → for loop (GC 할당 제거)
                _sharedCandidates.Clear();
                for (int i = 0; i < situation.HittableEnemies.Count; i++)
                {
                    var e = situation.HittableEnemies[i];
                    if (e != null && !plannedTargetIds.Contains(e.UniqueId))
                        _sharedCandidates.Add(e);
                }
                var candidates = _sharedCandidates;
                var bestTarget = TargetScorer.SelectBestEnemy(candidates, situation, Settings.AIRole.Tank)
                    ?? situation.NearestEnemy;

                // ★ v3.2.15: 아군을 위협하는 적 우선 공격 (Tank 보호 역할)
                // ★ v3.9.10: LINQ → MaxByWhere (O(n log n) → O(n), GC 할당 제거)
                var threateningEnemy = CollectionHelper.MaxByWhere(
                    situation.Enemies,
                    e => e != null && situation.HittableEnemies.Contains(e) &&
                         !plannedTargetIds.Contains(e.UniqueId) &&
                         TeamBlackboard.Instance.CountAlliesTargeting(e) > 0,
                    e => (float)TeamBlackboard.Instance.CountAlliesTargeting(e));

                if (threateningEnemy != null)
                {
                    bestTarget = threateningEnemy;
                    if (Main.IsDebugEnabled) Main.LogDebug($"[Tank] Phase 5: Priority target {threateningEnemy.CharacterName} (threatening allies)");
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

                // ★ v3.40.2: Push recovery — 밀어내기 공격 후 갭클로저 삽입
                var pushRecovery = TryPlanPushRecoveryGapCloser(situation, attackAction, ref remainingAP, ref remainingMP, budget);
                if (pushRecovery != null)
                    actions.Add(pushRecovery);

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
                        if (Main.IsDebugEnabled) Main.LogDebug($"[Tank] Phase 4: Allow re-attack on {targetEntity.CharacterName} (only 1 hittable enemy)");
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

            // ★ v3.18.22: TurnEnding AP 복원
            // ★ v3.19.2: TurnEnding AP 복원 불필요 — budget.CanAfford()가 예약을 내부 처리

            // ★ v3.8.72: Hittable mismatch 사후 보정
            HandleHittableMismatch(situation, didPlanAttack, attackContext);

            // ★ v3.36.0: Phase 5.8 — 0 AP 공격 소진
            PlanZeroAPAttacks(actions, situation, plannedAbilityGuids);

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
            // ★ v3.14.0: Phase 6.5 — 공통 Fallback Buffs (공격 불가 시 남은 버프 소진)
            ExecuteFallbackBuffsPhase(actions, situation, ref remainingAP, didPlanAttack, tacticalEval,
                tryAllyBuffFirst: true, includeFallbackDebuff: false);

            // ★ v3.5.35: Phase 7 (TurnEnding) → 맨 마지막으로 이동
            // TurnEnding 능력은 턴을 종료시키므로 다른 모든 행동 후에 계획해야 함

            // ★ v3.34.0: Phase 7.8 — 이동 전 MP 버프 (적이 사거리 밖이고 MP 부족 시)
            if (!didPlanAttack && situation.NeedsReposition && situation.MPBuffAbility != null)
            {
                var mpBuff = PlanMPBuffBeforeMove(situation, ref remainingAP, ref remainingMP);
                if (mpBuff != null)
                    actions.Add(mpBuff);
            }

            // ★ Phase 8: 이동 또는 GapCloser (공격 가능한 적이 없을 때)
            // ★ v3.0.55: remainingMP 체크 - 계획된 능력들의 MP 코스트 반영
            // ★ v3.0.90: 공격 계획 실패 시에도 이동 허용
            // ★ v3.0.99: MP 회복 예측 후 이동 가능
            // ★ v3.1.01: predictedMP를 MovementAPI에 전달하여 reachable tiles 계산에 사용
            // ★ v3.2.25: 전선 유지 로직 - Tank가 전선 뒤에 있으면 전진 필요
            // ★ v3.5.17: Tank 적극적 접근 - 공격 후에도 근접 거리로 이동
            // ★ v3.5.36: GapCloser도 이동으로 취급 (중복 계획 방지)
            bool hasMoveInPlan = CollectionHelper.Any(actions, a => a.Type == ActionType.Move ||
                (a.Type == ActionType.Attack && a.Ability != null && AbilityDatabase.IsGapCloser(a.Ability)));
            bool canMove = situation.CanMove || remainingMP > 0;
            // ★ v3.9.22: GapCloser(돌격 등)는 AP 기반 — MP 없어도 사용 가능
            bool hasGapClosers = !situation.PrefersRanged &&
                situation.AvailableAttacks.Any(a => AbilityDatabase.IsGapCloser(a));

            // ★ v3.5.17: Tank는 근접 캐릭터이므로 적에게 접근해야 함
            // 공격 후에도 적과 거리가 멀면(근접 공격 불가) 이동 필요
            float meleeEngageDistance = 3f;  // 근접 공격 거리
            bool shouldEngageMelee = !situation.PrefersRanged &&
                                      situation.HasLivingEnemies &&
                                      situation.NearestEnemyDistance > meleeEngageDistance;

            // ★ v3.2.25: Tank 전선 유지 - 아군 평균보다 뒤처지면 전진 필요
            // ★ v3.110.18: Frontline centroid 제거 — 아군 평균 적 거리 대비 자신의 적 거리로 판정
            //   offset = avgAllyDistToEnemy - myDistToEnemy
            //   offset < -5 = 내가 아군 평균보다 적으로부터 5m+ 더 멀다 = 뒤처짐
            bool shouldAdvanceToFrontline = false;
            if (situation.HasLivingEnemies && situation.AvgAllyDistanceToNearestEnemy > 0f)
            {
                float forwardOffset = situation.GetForwardOffsetFromAllies(situation.Unit.Position);
                if (forwardOffset < -5f)
                {
                    shouldAdvanceToFrontline = true;
                    if (Main.IsDebugEnabled) Main.LogDebug($"[Tank] Phase 8: Behind party avg ({forwardOffset:F1}m offset) - should advance");
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

            // ★ v3.9.22: GapCloser는 MP 없이도 진입 허용 (AP 기반 이동)
            if (!hasMoveInPlan && needsMovement && ((canMove && remainingMP > 0) || hasGapClosers))
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
                if (Main.IsDebugEnabled) Main.LogDebug($"[Tank] Phase 8: {attackContext}, forceMove={forceMove}");
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
                    // ★ v3.40.8: 면역 적에게 PostMoveAttack 방지
                    if (budget.PostMoveReserved > 0 && situation.NearestEnemy != null
                        && !CombatAPI.IsTargetImmuneToDamage(situation.NearestEnemy, situation.Unit))
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

                // ★ v3.110.18: Frontline 제거 — 아군 평균보다 전진한 상태면 후퇴
                if (situation.AvgAllyDistanceToNearestEnemy > 0f)
                {
                    float forwardOffset = situation.GetForwardOffsetFromAllies(situation.Unit.Position);
                    if (forwardOffset > 3f)
                    {
                        needsSafeRetreat = true;
                        retreatReason = $"ahead of party ({forwardOffset:F1}m forward)";
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

            // ★ v3.8.74: Phase 8.7 - Tactical Reposition (공격 쿨다운 시 다음 턴 최적 위치)
            if (!hasMoveInPlan && noAttackNoApproach && remainingMP > 0 && situation.HasLivingEnemies)
            {
                var tacticalRepos = PlanTacticalReposition(situation, remainingMP);
                if (tacticalRepos != null)
                {
                    actions.Add(tacticalRepos);
                    hasMoveInPlan = true;
                    Main.Log($"[Tank] Phase 8.7: Tactical reposition (all attacks on cooldown, MP={remainingMP:F1})");
                }
            }

            // Post-attack phase
            if ((situation.HasAttackedThisTurn || didPlanAttack) && remainingAP >= 1f)
            {
                var postAttackActions = PlanPostAttackActions(situation, ref remainingAP, skipMove: hasMoveInPlan);
                actions.AddRange(postAttackActions);
            }

            // ★ v3.42.0: Phase 7.0 — 여유 아군 치유 (메디킷 등)
            var oppHealActions = PlanOpportunisticAllyHeal(situation, ref remainingAP, remainingMP);
            if (oppHealActions != null)
            {
                actions.AddRange(oppHealActions);
                remainingMP = 0;
                Main.Log($"[Tank] Phase 7.0: Opportunistic ally heal");
            }

            // ★ v3.8.86: Phase 8.9 - 이동 완료 후 ClearMP 방어 자세
            // Phase 2에서 ClearMPAfterUse로 연기된 방어 능력을 이동 완료 후 사용
            if (tankNeedsMovement && !situation.HasPerformedFirstAction && remainingAP >= 1f)
            {
                var deferredDefense = PlanDefensiveStanceWithReservation(situation, ref remainingAP, budget.EffectiveReserved, movementStillNeeded: false);
                if (deferredDefense != null)
                {
                    actions.Add(deferredDefense);
                    Main.Log($"[Tank] Phase 8.9: Deferred ClearMP defensive stance - {deferredDefense.Ability?.Name}");
                }
            }

            // ★ v3.1.24: Phase 9 - 최종 AP 활용 (모든 시도 실패 후)
            // ★ v3.9.06: actions.Count > 0 제한 제거 - DPSPlan v3.8.84와 통일
            // 디버프/마커는 다른 행동 없이도 팀에 기여
            if (remainingAP >= 1f)
            {
                var finalAction = PlanFinalAPUtilization(situation, ref remainingAP);
                if (finalAction != null)
                {
                    actions.Add(finalAction);
                    Main.Log($"[Tank] Phase 9: Final AP utilization - {finalAction.Ability?.Name}");
                }
            }

            // ★ v3.8.68: Post-plan 공격 검증 + 복구 (TurnEnding 전에 실행)
            int removedAttacks = ValidateAndRemoveUnreachableAttacks(actions, situation, ref didPlanAttack, ref remainingAP);

            if (removedAttacks > 0 && !didPlanAttack)
            {
                // 모든 공격이 제거됨 → 복구 이동 시도
                bool hasRecoveryMove = CollectionHelper.Any(actions, a => a.Type == ActionType.Move);
                if (!hasRecoveryMove && situation.HasLivingEnemies && remainingMP > 0)
                {
                    Main.Log($"[Tank] ★ Post-validation recovery: attempting movement (AP={remainingAP:F1}, MP={remainingMP:F1})");
                    var recoveryCtx = new AttackPhaseContext { RangeWasIssue = true };
                    bool bypassCanMoveCheck = !situation.CanMove && remainingMP > 0;
                    var recoveryMove = PlanMoveOrGapCloser(situation, ref remainingAP, true, bypassCanMoveCheck, remainingMP, recoveryCtx);
                    if (recoveryMove != null)
                    {
                        actions.Add(recoveryMove);
                        Main.Log($"[Tank] ★ Post-validation recovery: movement planned");
                    }
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
            if (Main.IsDebugEnabled) Main.LogDebug($"[Tank] Plan complete: AP={remainingAP:F1}, MP={remainingMP:F1} (started with {situation.CurrentMP:F1})");

            // ★ v3.1.09: InitialAP/InitialMP 전달 (리플랜 감지용)
            // ★ v3.5.88: 0 AP 공격 수 전달 (Break Through → Slash 감지용)
            int zeroAPAttackCount = CombatAPI.GetZeroAPAttacks(situation.Unit).Count;
            // ★ v3.9.26: NormalHittableCount 사용 — DangerousAoE 부풀림이 replan을 불필요하게 유발 방지
            return new TurnPlan(actions, priority, reasoning, situation.HPPercent, situation.NearestEnemyDistance,
                situation.NormalHittableCount, situation.CurrentAP, situation.CurrentMP, zeroAPAttackCount);
        }

        #region Tank-Specific Methods

        /// <summary>
        /// ★ v3.8.86: movementStillNeeded 파라미터 추가
        /// ClearMPAfterUse 방어 자세는 이동 필요 시 연기 (Phase 8.9에서 재시도)
        /// </summary>
        private PlannedAction PlanDefensiveStanceWithReservation(Situation situation, ref float remainingAP, float reservedAP, bool movementStillNeeded)
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

                // ★ v3.104.0: 이미 이 플랜에서 선택된 버프면 스킵 (Tank Phase 2/8.9 인내 중복 방지)
                string abilityGuid = ability?.Blueprint?.AssetGuid?.ToString() ?? ability?.Name ?? "";
                if (_plannedBuffGuids.Contains(abilityGuid)) continue;

                // ★ v3.8.86: ClearMP + 이동 필요 시 연기 (Phase 8.9에서 재시도)
                if (movementStillNeeded && CombatAPI.AbilityClearsMPAfterUse(ability, situation.Unit))  // ★ v3.8.88
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[Tank] Phase 2: Deferred {ability.Name} (ClearMP, movement needed)");
                    continue;
                }

                float cost = CombatAPI.GetAbilityAPCost(ability);

                // 방어 자세는 필수 버프 - 예약 무시
                bool isEssential = IsEssentialBuff(ability, situation);
                if (!CanAffordBuffWithReservation(cost, remainingAP, reservedAP, isEssential))
                    continue;

                if (AllyStateCache.HasBuff(situation.Unit, ability)) continue;

                // ★ v3.8.25: AbilityCasterHasFacts 검증 (스택 버프 필요 여부)
                // GetUnavailabilityReasons()가 감지하지 못하는 캐스터 제한 검증
                string factReason;
                if (!CombatAPI.MeetsCasterFactRequirements(ability, out factReason))
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[Tank] DefensiveStance skipped - {factReason}");
                    continue;
                }

                string reason;
                if (CombatAPI.CanUseAbilityOn(ability, target, out reason))
                {
                    remainingAP -= cost;
                    _plannedBuffGuids.Add(abilityGuid);  // ★ v3.104.0: dedup 등록
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
