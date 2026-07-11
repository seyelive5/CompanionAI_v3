using System;
using System.Collections.Generic;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.Data;
using CompanionAI_v3.Diagnostics;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;
using CompanionAI_v3.Planning.Planners;

namespace CompanionAI_v3.Planning.Plans
{
    public abstract partial class BasePlan
    {
        #region Common Methods (not delegated)

        /// <summary>
        /// AoE phase 에서 계획된 능력의 GUID 를 공격 제외 목록에 등록 — Phase 5 공격 루프가
        /// 같은 턴에 동일 AoE 를 다시 선택하는 중복 시전 방지.
        /// 단일 hittable 적일 때는 동일 능력 재공격 허용 정책(Phase 5 루프와 동일 기준)에 따라 등록하지 않음.
        /// </summary>
        protected static void ExcludePlannedAbilityGuid(PlannedAction action, Situation situation, HashSet<string> excludeAbilityGuids)
        {
            if (action?.Ability == null || excludeAbilityGuids == null) return;
            if (situation.HittableEnemies.Count <= 1) return;
            string guid = action.Ability.Blueprint?.AssetGuid?.ToString();
            if (!string.IsNullOrEmpty(guid)) excludeAbilityGuids.Add(guid);
        }

        /// <summary>
        /// ★ v3.1.24: 최종 AP 활용 (모든 주요 행동 실패 후)
        /// Phase 9에서 사용 - 공격/이동 모두 실패했지만 AP가 남았을 때
        /// </summary>
        protected PlannedAction PlanFinalAPUtilization(Situation situation, ref float remainingAP)
        {
            // ★ v3.18.22: TurnEnding AP 예약 — Phase 10 곡예술 등을 위한 AP 보존
            float turnEndingReservedAP = CalculateTurnEndingReservedAP(situation);
            float spendableAP = remainingAP - turnEndingReservedAP;

            if (spendableAP < 1f) return null;

            string unitId = situation.Unit.UniqueId;
            float currentAP = remainingAP;  // 람다에서 사용하기 위해 로컬 변수에 복사

            // 1. 아직 사용 안 한 저우선순위 버프
            foreach (var buff in situation.AvailableBuffs)
            {
                float cost = CombatAPI.GetAbilityAPCost(buff);
                if (cost > spendableAP) continue;

                string abilityId = buff.Blueprint?.AssetGuid?.ToString();
                if (string.IsNullOrEmpty(abilityId)) continue;

                // 최근 사용된 능력 스킵
                if (AbilityUsageTracker.WasUsedRecently(unitId, abilityId, 1000)) continue;

                // ★ v3.110.7: 이 턴 이미 계획된 버프 스킵 — PlanFinalAPUtilization dedup 누락 버그 수정.
                // 로그 분석: 인내/황제의 말씀/피 흘리기가 self-target으로 2회씩 중복 등장 →
                // Phase 3/4에서 자기 버프가 _plannedBuffGuids에 등록됐는데, Phase 9가 이를 무시하고
                // 동일 버프를 "Final AP buff" 사유로 재추가. 실행 시점엔 이미 cooldown이라 두 번째는 실패.
                if (_plannedBuffGuids.Contains(abilityId))
                {
                    if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Phase 9: Skip {buff.Name} (already planned this turn)");
                    continue;
                }

                // 선제 버프 제외 (공격 없으면 무의미)
                var timing = AbilityDatabase.GetTiming(buff);
                if (timing == AbilityTiming.PreAttackBuff ||
                    timing == AbilityTiming.HeroicAct ||
                    timing == AbilityTiming.RighteousFury ||
                    timing == AbilityTiming.SelfDamage ||  // ★ v3.9.14: Phase 9에서 자해 버프 차단 (HP 낭비 방지)
                    CombatAPI.IsSelfDamagingAbility(buff))  // F10: AutoDetect 가 SelfDamage 를 안 주는 컴포넌트 감지 자해도 차단
                    continue;

                // 턴 종료 능력 제외
                if (AbilityDatabase.IsTurnEnding(buff)) continue;

                // 자신 대상 버프
                var selfTarget = new TargetWrapper(situation.Unit);
                string reason;
                if (CombatAPI.CanUseAbilityOn(buff, selfTarget, out reason))
                {
                    remainingAP -= cost;
                    _plannedBuffGuids.Add(abilityId);  // ★ v3.110.7: dedup 등록
                    Log.Planning.Info($"[{RoleName}] Phase 9: Final buff - {buff.Name}");
                    return PlannedAction.Buff(buff, situation.Unit, "Final AP buff", cost);
                }
            }

            // 2. 디버프 (적에게)
            // ★ v3.40.8: 면역 적에게 디버프 낭비 방지
            if (situation.NearestEnemy != null && situation.AvailableDebuffs != null
                && !CombatAPI.IsTargetImmuneToDamage(situation.NearestEnemy, situation.Unit))
            {
                foreach (var debuff in situation.AvailableDebuffs)
                {
                    float cost = CombatAPI.GetAbilityAPCost(debuff);
                    if (cost > spendableAP) continue;

                    string abilityId = debuff.Blueprint?.AssetGuid?.ToString();
                    if (string.IsNullOrEmpty(abilityId)) continue;

                    if (AbilityUsageTracker.WasUsedRecently(unitId, abilityId, 1000)) continue;

                    // ★ v3.110.7: 이 턴 이미 계획된 능력 스킵 — 공포 지대 같은 PointTarget 디버프가
                    // 다른 phase에서 PlanDebuff로 이미 추가됐는데 Phase 9에서 또 추가하던 버그.
                    // _plannedBuffGuids는 이제 "once-per-turn ability tracker"로 역할 확장 (buff + debuff + marker).
                    if (_plannedBuffGuids.Contains(abilityId))
                    {
                        if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Phase 9: Skip debuff {debuff.Name} (already planned this turn)");
                        continue;
                    }

                    var target = new TargetWrapper(situation.NearestEnemy);
                    string reason;
                    if (CombatAPI.CanUseAbilityOn(debuff, target, out reason))
                    {
                        // ★ v3.18.4: AoE 안전성 체크 (아군 피해 방지)
                        if (!CombatHelpers.IsAttackSafeForTarget(debuff, situation.Unit, situation.NearestEnemy, situation.Allies))
                        {
                            Log.Planning.Info($"[{RoleName}] Phase 9: Final debuff SKIPPED (AoE unsafe): {debuff.Name} -> {situation.NearestEnemy.CharacterName}");
                            continue;
                        }

                        // 포인트 AoE 착탄 사전 검증 — 다층 절벽 등에서 착탄점이 밀리면 타겟이 패턴 밖.
                        // 미검증 시 계획→게이트 차단→재계획 반복 후 Stagnant 턴엔드로 AP 낭비 (§15 DOOM Enfeeble 실증)
                        if (!CombatAPI.WillPointCastReachTarget(debuff, situation.Unit, situation.NearestEnemy))
                        {
                            Log.Planning.Info($"[{RoleName}] Phase 9: Final debuff SKIPPED (target not in pattern): {debuff.Name} -> {situation.NearestEnemy.CharacterName}");
                            continue;
                        }

                        remainingAP -= cost;
                        _plannedBuffGuids.Add(abilityId);  // ★ v3.110.7: dedup 등록
                        Log.Planning.Info($"[{RoleName}] Phase 9: Final debuff - {debuff.Name} -> {situation.NearestEnemy.CharacterName}");
                        return PlannedAction.Attack(debuff, situation.NearestEnemy, "Final AP debuff", cost);
                    }
                }
            }

            // 3. 마커 (적에게)
            // ★ v3.1.28: 이미 마킹된 타겟에 중복 적용 방지
            // ★ v3.40.8: 면역 적에게 마커 낭비 방지
            if (situation.NearestEnemy != null && situation.AvailableMarkers != null
                && situation.HasHittableEnemies  // ★ v3.9.14: 때릴 수 없으면 마킹 무의미 (SingleUse 낭비 방지)
                && !CombatAPI.IsTargetImmuneToDamage(situation.NearestEnemy, situation.Unit))
            {
                string targetId = situation.NearestEnemy.UniqueId;
                foreach (var marker in situation.AvailableMarkers)
                {
                    float cost = CombatAPI.GetAbilityAPCost(marker);
                    if (cost > spendableAP) continue;

                    string abilityId = marker.Blueprint?.AssetGuid?.ToString();
                    if (string.IsNullOrEmpty(abilityId)) continue;

                    // ★ v3.1.28: 타겟별 중복 체크 (같은 타겟에 같은 마커 적용 방지)
                    string usageKey = $"{abilityId}:{targetId}";
                    if (AbilityUsageTracker.WasUsedRecently(unitId, usageKey, 5000))
                    {
                        if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Phase 9: Skipping {marker.Name} - already used on {situation.NearestEnemy.CharacterName}");
                        continue;
                    }

                    // 능력 자체도 최근 사용 여부 확인
                    if (AbilityUsageTracker.WasUsedRecently(unitId, abilityId, 1000)) continue;

                    // ★ v3.110.7: 이 턴 이미 계획된 능력 스킵
                    if (_plannedBuffGuids.Contains(abilityId))
                    {
                        if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Phase 9: Skip marker {marker.Name} (already planned this turn)");
                        continue;
                    }

                    var target = new TargetWrapper(situation.NearestEnemy);
                    string reason;
                    if (CombatAPI.CanUseAbilityOn(marker, target, out reason))
                    {
                        // ★ v3.18.4: AoE 안전성 체크 (아군 피해 방지)
                        if (!CombatHelpers.IsAttackSafeForTarget(marker, situation.Unit, situation.NearestEnemy, situation.Allies))
                        {
                            Log.Planning.Info($"[{RoleName}] Phase 9: Final marker SKIPPED (AoE unsafe): {marker.Name} -> {situation.NearestEnemy.CharacterName}");
                            continue;
                        }

                        remainingAP -= cost;
                        _plannedBuffGuids.Add(abilityId);  // ★ v3.110.7: dedup 등록
                        Log.Planning.Info($"[{RoleName}] Phase 9: Final marker - {marker.Name} -> {situation.NearestEnemy.CharacterName}");
                        return PlannedAction.Buff(marker, situation.NearestEnemy, "Final AP marker", cost);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// ★ v3.0.56: ClearMPAfterUse 능력 사용 전 선제적 후퇴 계획
        /// 위험 상황에서 MP를 전부 소모하는 능력을 쓰기 전에 먼저 안전 확보
        /// </summary>
        protected PlannedAction PlanPreemptiveRetreatForClearMPAbility(Situation situation, ref float remainingMP)
        {
            // 이미 이동했거나 이동 불가
            if (situation.HasMovedThisTurn || !situation.CanMove || situation.CurrentMP <= 0)
                return null;

            // ClearMPAfterUse 능력이 있는지 확인
            var clearMPAbility = CollectionHelper.FirstOrDefault(situation.AvailableAttacks,
                a => CombatAPI.AbilityClearsMPAfterUse(a, situation.Unit));  // ★ v3.8.88

            if (clearMPAbility == null) return null;

            // 선제적 이동 필요 여부 확인
            if (!UtilityScorer.ShouldMoveBeforeClearMPAbility(situation, clearMPAbility))
                return null;

            // 후퇴 위치 찾기
            var retreatAction = MovementPlanner.PlanRetreat(situation);
            if (retreatAction != null)
            {
                Log.Planning.Info($"[{RoleName}] ★ Preemptive retreat before {clearMPAbility.Name} (ClearMPAfterUse)");
                remainingMP = 0f;  // 이동 후 MP 소진
            }

            return retreatAction;
        }

        /// <summary>
        /// ★ v3.0.56: ClearMPAfterUse 능력이 있고 위험 상황인지 확인
        /// </summary>
        protected bool ShouldPrioritizeSafetyForClearMPAbility(Situation situation)
        {
            // ClearMPAfterUse 능력 존재 확인
            bool hasClearMPAbility = CollectionHelper.Any(situation.AvailableAttacks,
                a => CombatAPI.AbilityClearsMPAfterUse(a, situation.Unit));  // ★ v3.8.88

            if (!hasClearMPAbility) return false;

            // 역할별 안전 가중치
            float safetyWeight = UtilityScorer.GetRoleSafetyWeight(situation);
            if (safetyWeight < 0.4f) return false;  // Tank는 무시

            // 위험 상황 또는 적이 가까우면 true
            return situation.IsInDanger || situation.NearestEnemyDistanceTiles < situation.MinSafeDistance;
        }

        /// <summary>
        /// 공격 후 추가 행동
        /// </summary>
        protected List<PlannedAction> PlanPostAttackActions(Situation situation, ref float remainingAP, bool skipMove = false)
        {
            var actions = new List<PlannedAction>();

            if (!situation.HasAttackedThisTurn)
                return actions;

            // 디버프
            // ★ v3.40.8: 면역 적에게 디버프 낭비 방지
            if (remainingAP >= 1f && situation.NearestEnemy != null && situation.AvailableDebuffs.Count > 0
                && !CombatAPI.IsTargetImmuneToDamage(situation.NearestEnemy, situation.Unit))
            {
                var debuff = PlanDebuff(situation, situation.NearestEnemy, ref remainingAP);
                if (debuff != null) actions.Add(debuff);
            }

            // 방어 버프
            if (remainingAP >= 1f && !situation.HasHittableEnemies)
            {
                var defensiveBuff = PlanDefensiveBuff(situation, ref remainingAP);
                if (defensiveBuff != null) actions.Add(defensiveBuff);
            }

            // 추가 이동
            // ★ v3.8.45: 원거리 캐릭터는 공격 후 적에게 접근하지 않음
            // 원거리가 공격 후 전진하면 다음 턴에 위험 위치에 노출됨
            if (!skipMove && !situation.HasHittableEnemies && situation.HasLivingEnemies &&
                situation.CanMove && situation.AllowPostAttackMove && !situation.PrefersRanged)
            {
                var moveAction = PlanMoveToEnemy(situation);
                if (moveAction != null) actions.Add(moveAction);
            }

            return actions;
        }

        /// <summary>
        /// ★ v3.8.72: Hittable Mismatch 사후 보정
        /// Analyzer가 Hittable이라 판정했지만 AttackPlanner가 모든 타겟에서 실패한 경우
        /// Situation 플래그를 보정하여 이동 Phase가 올바르게 작동하도록 함
        ///
        /// 보정 내용:
        /// 1. HittableEnemies 클리어 → HasHittableEnemies=false
        /// 2. NeedsReposition=true (원거리: 새 위치에서 LoS 확보)
        /// 3. AllowPostAttackMove=true (이미 이동+공격했으면 추가 이동 허용)
        /// 4. AttackPhaseContext.HittableMismatch=true (forceMove 판단용)
        /// </summary>
        protected void HandleHittableMismatch(Situation situation, bool didPlanAttack, AttackPhaseContext attackContext)
        {
            if (didPlanAttack || !situation.HasHittableEnemies) return;

            int mismatchCount = situation.HittableEnemies.Count;
            Log.Planning.Info($"[{RoleName}] ★ Hittable mismatch: {mismatchCount} marked hittable but no attack possible - correcting");

            // 1. 거짓 Hittable 클리어
            situation.HittableEnemies.Clear();

            // 2. 원거리: 재배치 필요 (새 위치에서 LoS 확보 가능)
            if (situation.PrefersRanged)
                situation.NeedsReposition = true;

            // 3. 이미 이동+공격했으면 추가 이동 허용 (AllowPostAttackMove 재계산)
            //    원래: turnState.AllowPostAttackMove && !HasHittableEnemies
            //    이제: HasHittableEnemies=false이므로, HasMovedThisTurn && HasAttackedThisTurn이면 true
            if (situation.HasMovedThisTurn && situation.HasAttackedThisTurn)
                situation.AllowPostAttackMove = true;

            // 4. AttackPhaseContext에 기록 → ShouldForceMove에 반영
            if (attackContext != null)
                attackContext.HittableMismatch = true;
        }

        /// <summary>
        /// 이동 후 공격에 필요한 AP 예약량 계산
        /// </summary>
        protected float CalculateReservedAPForPostMoveAttack(Situation situation)
        {
            if (situation.HasHittableEnemies) return 0f;
            if (!situation.CanMove) return 0f;
            if (!situation.HasLivingEnemies) return 0f;

            float distanceToNearest = situation.NearestEnemyDistance;  // 미터
            // ★ v3.6.4: MP(타일)를 미터로 변환하여 distanceToNearest(미터)와 비교
            float movementRangeMeters = CombatAPI.TilesToMeters(situation.CurrentMP);

            if (distanceToNearest > movementRangeMeters + 10f) return 0f;  // 10m 버퍼

            float defaultAttackCost = situation.PrefersRanged ? DEFAULT_RANGED_ATTACK_COST : DEFAULT_MELEE_ATTACK_COST;
            float attackCost = defaultAttackCost;

            if (situation.PrimaryAttack != null)
            {
                float primaryCost = CombatAPI.GetAbilityAPCost(situation.PrimaryAttack);
                if (primaryCost >= 1f)
                {
                    attackCost = primaryCost;
                }
            }
            else if (situation.AvailableAttacks.Count > 0)
            {
                // ★ v3.8.78: LINQ → for 루프 (0 할당)
                float maxCost = 0f;
                bool prefersRanged = situation.PrefersRanged;
                for (int i = 0; i < situation.AvailableAttacks.Count; i++)
                {
                    var a = situation.AvailableAttacks[i];
                    if (a == null) continue;
                    if (AbilityDatabase.IsReload(a) || AbilityDatabase.IsTurnEnding(a)) continue;
                    if (prefersRanged ? a.IsMelee : !a.IsMelee) continue;
                    float cost = CombatAPI.GetAbilityAPCost(a);
                    if (cost >= 1f && cost > maxCost) maxCost = cost;
                }
                if (maxCost > 0f) attackCost = maxCost;
            }

            return Math.Max(attackCost, defaultAttackCost);
        }

        /// <summary>
        /// ★ v3.18.22: TurnEnding 능력에 필요한 AP 예약량 계산
        /// 곡예술 등 턴 종료 시 사용하는 능력의 최소 AP 비용을 반환
        /// Phase 4.5 아군 버프 루프, Phase 5 공격 루프, Phase 9 잔여 AP 활용에서 이 AP를 보존해야 함
        /// ★ SpringAttack 조건은 체크하지 않음 — 계획 시점엔 미이동 상태이나 실행 시점엔 충족 가능
        /// </summary>
        protected float CalculateTurnEndingReservedAP(Situation situation)
        {
            if (situation.AvailableBuffs == null || situation.AvailableBuffs.Count == 0)
                return 0f;

            float minCost = float.MaxValue;
            for (int i = 0; i < situation.AvailableBuffs.Count; i++)
            {
                var buff = situation.AvailableBuffs[i];
                if (!AbilityDatabase.IsTurnEnding(buff)) continue;
                if (CombatAPI.IsAbilityOnCooldownWithGroups(buff)) continue;
                // ★ SpringAttack 조건 미체크: 계획 시점엔 이동 전이라 조건 미충족이지만,
                // Phase 10 실행 시점엔 이동 완료로 충족 가능. 낙관적 예약.
                // Phase 10에서 PlanTurnEndingAbility가 최종 조건 체크를 수행.

                float cost = CombatAPI.GetAbilityAPCost(buff);
                if (cost < minCost) minCost = cost;
            }

            return minCost == float.MaxValue ? 0f : minCost;
        }

        /// <summary>
        /// ★ v3.19.4: APBudget 팩토리 — 레거시 예약 계산을 단일 생성 지점으로 통합
        /// 4개 Plan에서 반복되던 10줄 budget 생성 블록 + effectiveReservedAP 수동 계산을 대체
        /// effectiveReservedAP는 budget.EffectiveReserved 자동 속성으로 대체
        /// </summary>
        protected APBudget CreateAPBudget(Situation situation, float remainingAP, float masterMinAttackAP = 0f)
        {
            var budget = new APBudget
            {
                TotalAP = remainingAP,
                PostMoveReserved = CalculateReservedAPForPostMoveAttack(situation),
                TurnEndingReserved = CalculateTurnEndingReservedAP(situation),
                MasterMinAttackReserved = masterMinAttackAP
            };
            // ★ v3.20.1: APBudget 결정 JSON 리포트에 기록 (모든 역할 공통)
            CombatReportCollector.Instance.LogPhase(budget.ToString());
            return budget;
        }

        /// <summary>
        /// ★ v3.19.4: 마스터 최소 공격 AP 계산 (Overseer 전용)
        /// Phase 4.5 아군 버프가 전체 AP를 소모하지 않도록 최소 공격 AP 예약
        /// </summary>
        protected float CalculateMasterMinAttackAP(Situation situation)
        {
            if (situation.AvailableAttacks == null || situation.AvailableAttacks.Count == 0)
                return 0f;

            float minCost = float.MaxValue;
            for (int i = 0; i < situation.AvailableAttacks.Count; i++)
            {
                float cost = CombatAPI.GetAbilityAPCost(situation.AvailableAttacks[i]);
                if (cost < minCost) minCost = cost;
            }
            return minCost == float.MaxValue ? 0f : minCost;
        }

        /// <summary>
        /// ★ v3.22.0: 이전 전략의 FocusTarget이 여전히 공격 가능한지 검증
        /// 4개 Plan에서 중복되던 FocusTarget 유효성 검증 루프를 BasePlan으로 추출
        /// </summary>
        /// <returns>FocusTarget이 유효하면 true, 무효하면 false</returns>
        protected bool ValidateFocusTarget(Situation situation, TurnState turnState, string roleTag)
        {
            string focusTargetId = turnState.GetContext<string>(StrategicContextKeys.FocusTargetId, null);
            if (focusTargetId == null) return true;

            for (int i = 0; i < situation.HittableEnemies.Count; i++)
            {
                if (situation.HittableEnemies[i].UniqueId == focusTargetId)
                    return true;
            }
            Log.Planning.Info($"[{roleTag}] Strategy: Previous FocusTarget {focusTargetId} no longer hittable — re-evaluating");
            return false;
        }

        /// <summary>
        /// ★ v3.22.0: 전략 평가 또는 이전 전략 재사용 — 전 Role 통합
        /// 4개 Plan에서 중복되던 ~50줄 전략 블록을 BasePlan으로 추출
        /// 포함: previousStrategyValid 판정, FocusTarget 검증, TurnStrategyPlanner 호출,
        ///       FocusTargetId/TacticalObjective 저장, budget.StrategyPostActionReserved 설정
        /// </summary>
        /// <param name="role">AI 역할 (DPS는 기본값 사용)</param>
        /// <returns>최적 전략 (공격 불가 시 null)</returns>
        protected TurnStrategy EvaluateOrReuseStrategy(
            Situation situation, TurnState turnState,
            ref APBudget budget, string roleTag,
            Settings.AIRole role = Settings.AIRole.DPS)
        {
            TurnStrategy strategy = turnState.GetContext<TurnStrategy>(
                StrategicContextKeys.TurnStrategyKey, default(TurnStrategy));

            bool previousValid = strategy != null
                && situation.HasHittableEnemies
                && situation.BestTarget != null
                && strategy.ExpectedTotalDamage > 0;

            if (previousValid && !ValidateFocusTarget(situation, turnState, roleTag))
                previousValid = false;

            if (previousValid)
            {
                budget.StrategyPostActionReserved = strategy.ReservedAPForPostAction;
                Log.Planning.Info($"[{roleTag}] Strategy: Reusing previous ({strategy.Sequence}, dmg={strategy.ExpectedTotalDamage:F0})");
                return strategy;
            }

            if (situation.HasHittableEnemies &&
                TeamBlackboard.Instance.CurrentTactic != TacticalSignal.Retreat)
            {
                strategy = TurnStrategyPlanner.Evaluate(situation, role);
                if (strategy != null)
                {
                    turnState.SetContext(StrategicContextKeys.TurnStrategyKey, strategy);
                    if (situation.BestTarget != null)
                        turnState.SetContext(StrategicContextKeys.FocusTargetId, situation.BestTarget.UniqueId);

                    string objective = strategy.PrioritizesKillSequence ? "Kill"
                        : strategy.ShouldPrioritizeAoE ? "AoE" : "Attack";
                    turnState.SetContext(StrategicContextKeys.TacticalObjective, objective);

                    budget.StrategyPostActionReserved = strategy.ReservedAPForPostAction;
                    Log.Planning.Info($"[{roleTag}] Strategy: {strategy.Sequence} (dmg={strategy.ExpectedTotalDamage:F0}, objective={objective})");
                }
                return strategy;
            }

            return null;
        }

        /// <summary>
        /// 주변 적 수 계산
        /// </summary>
        protected int CountNearbyEnemies(Situation situation, float range)
        {
            // ★ v3.8.48: LINQ → CollectionHelper (0 할당)
            return CollectionHelper.CountWhere(situation.Enemies, e =>
                !e.LifeState.IsDead &&
                CombatAPI.GetDistance(situation.Unit, e) <= range);
        }

        /// <summary>
        /// 턴 우선순위 결정
        /// </summary>
        protected TurnPriority DeterminePriority(List<PlannedAction> actions, Situation situation)
        {
            if (actions.Count == 0) return TurnPriority.EndTurn;

            var firstAction = actions[0];

            switch (firstAction.Type)
            {
                case ActionType.Heal:
                    return TurnPriority.Emergency;
                case ActionType.Reload:
                    return TurnPriority.Reload;
                case ActionType.Move:
                    if (situation.IsInDanger) return TurnPriority.Retreat;
                    return TurnPriority.MoveAndAttack;
                case ActionType.Buff:
                    return TurnPriority.BuffedAttack;
                case ActionType.Attack:
                    return TurnPriority.DirectAttack;
                default:
                    return TurnPriority.EndTurn;
            }
        }

        /// <summary>
        /// 턴 계획 요약
        /// </summary>
        protected string DetermineReasoning(List<PlannedAction> actions, Situation situation)
        {
            if (actions.Count == 0) return "No actions available";

            // ★ v3.8.78: LINQ → for 루프 (0 할당 - Distinct 대체)
            var sb = new System.Text.StringBuilder(64);
            ActionType lastType = (ActionType)(-1);
            for (int i = 0; i < actions.Count; i++)
            {
                if (actions[i].Type != lastType)
                {
                    if (sb.Length > 0) sb.Append(" -> ");
                    sb.Append(actions[i].Type.ToString());
                    lastType = actions[i].Type;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 턴 종료 이유 결정
        /// </summary>
        protected string GetEndTurnReason(Situation situation)
        {
            if (!situation.HasLivingEnemies) return "No enemies";
            if (situation.CurrentAP < 1f) return "No AP";
            if (situation.AvailableAttacks.Count == 0) return "No attacks available";
            if (!situation.HasHittableEnemies) return "No hittable targets";

            return "No valid actions";
        }

        #endregion
    }
}
