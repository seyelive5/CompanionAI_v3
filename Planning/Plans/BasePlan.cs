using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using UnityEngine;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;
using CompanionAI_v3.Planning.Planners;

namespace CompanionAI_v3.Planning.Plans
{
    /// <summary>
    /// ★ v3.0.47: 모든 Role Plan의 기본 클래스
    /// Planners에게 위임하는 얇은 래퍼
    /// </summary>
    public abstract class BasePlan
    {
        #region Constants

        protected const float HP_COST_THRESHOLD = 40f;
        protected const float DEFAULT_MELEE_ATTACK_COST = 2f;
        protected const float DEFAULT_RANGED_ATTACK_COST = 2f;
        protected const int MAX_ATTACKS_PER_PLAN = 3;
        protected const int MAX_POSITIONAL_BUFFS = 3;

        #endregion

        #region Confidence Helpers (v3.2.20)

        /// <summary>
        /// ★ v3.2.20: 현재 팀 신뢰도 조회
        /// </summary>
        protected float GetTeamConfidence()
            => TeamBlackboard.Instance.TeamConfidence;

        /// <summary>
        /// ★ v3.5.36: 현재 팀 신뢰도 상태 조회
        /// Heroic/Confident/Neutral/Worried/Panicked 상태 반환
        /// </summary>
        protected ConfidenceState GetConfidenceState()
            => TeamBlackboard.Instance.GetConfidenceState();

        /// <summary>
        /// ★ v3.2.20: 신뢰도 기반 공격 적극도 배율 (0.3 ~ 1.5)
        /// 높을수록 공격적 행동 선호
        /// </summary>
        protected float GetConfidenceAggression()
        {
            float confidence = TeamBlackboard.Instance.TeamConfidence;
            return CurvePresets.ConfidenceToAggression?.Evaluate(confidence) ?? 1f;
        }

        /// <summary>
        /// ★ v3.2.20: 신뢰도 기반 방어 필요도 배율 (0.3 ~ 1.5)
        /// 높을수록 방어적 행동 선호
        /// </summary>
        protected float GetConfidenceDefenseNeed()
        {
            float confidence = TeamBlackboard.Instance.TeamConfidence;
            return CurvePresets.ConfidenceToDefenseNeed?.Evaluate(confidence) ?? 1f;
        }

        #endregion

        #region Abstract Methods

        /// <summary>
        /// Role별 턴 계획 생성
        /// </summary>
        public abstract TurnPlan CreatePlan(Situation situation, TurnState turnState);

        /// <summary>
        /// Role 이름 (로깅용)
        /// </summary>
        protected abstract string RoleName { get; }

        #endregion

        #region Heal/Reload - Delegates to HealPlanner

        protected PlannedAction PlanEmergencyHeal(Situation situation, ref float remainingAP)
            => HealPlanner.PlanEmergencyHeal(situation, ref remainingAP, RoleName);

        protected PlannedAction PlanReload(Situation situation, ref float remainingAP)
            => HealPlanner.PlanReload(situation, ref remainingAP, RoleName);

        protected BaseUnitEntity FindWoundedAlly(Situation situation, float threshold)
            => HealPlanner.FindWoundedAlly(situation, threshold);

        #endregion

        #region Movement - Delegates to MovementPlanner

        protected PlannedAction PlanMoveOrGapCloser(Situation situation, ref float remainingAP)
            => MovementPlanner.PlanMoveOrGapCloser(situation, ref remainingAP, RoleName);

        // ★ v3.0.89: forceMove 파라미터 오버로드 추가
        protected PlannedAction PlanMoveOrGapCloser(Situation situation, ref float remainingAP, bool forceMove)
            => MovementPlanner.PlanMoveOrGapCloser(situation, ref remainingAP, RoleName, forceMove);

        // ★ v3.1.00: bypassCanMoveCheck 파라미터 오버로드 추가
        // MP 회복 능력 계획 후 예측 MP 기반으로 이동 가능할 때 사용
        protected PlannedAction PlanMoveOrGapCloser(Situation situation, ref float remainingAP, bool forceMove, bool bypassCanMoveCheck)
            => MovementPlanner.PlanMoveOrGapCloser(situation, ref remainingAP, RoleName, forceMove, bypassCanMoveCheck);

        // ★ v3.1.01: predictedMP 파라미터 오버로드 추가
        // MovementAPI에 예측 MP를 전달하여 reachable tiles 계산에 사용
        protected PlannedAction PlanMoveOrGapCloser(Situation situation, ref float remainingAP, bool forceMove, bool bypassCanMoveCheck, float predictedMP)
            => MovementPlanner.PlanMoveOrGapCloser(situation, ref remainingAP, RoleName, forceMove, bypassCanMoveCheck, predictedMP);

        protected PlannedAction PlanGapCloser(Situation situation, BaseUnitEntity target, ref float remainingAP)
            => MovementPlanner.PlanGapCloser(situation, target, ref remainingAP, RoleName);

        // ★ v3.5.34: MP 비용 예측 버전 추가
        protected PlannedAction PlanGapCloser(Situation situation, BaseUnitEntity target, ref float remainingAP, ref float remainingMP)
            => MovementPlanner.PlanGapCloser(situation, target, ref remainingAP, ref remainingMP, RoleName);

        protected PlannedAction PlanMoveToEnemy(Situation situation)
            => MovementPlanner.PlanMoveToEnemy(situation, RoleName);

        protected PlannedAction PlanRetreat(Situation situation)
            => MovementPlanner.PlanRetreat(situation);

        protected PlannedAction PlanPostActionSafeRetreat(Situation situation)
            => MovementPlanner.PlanPostActionSafeRetreat(situation);

        protected bool ShouldRetreat(Situation situation)
            => MovementPlanner.ShouldRetreat(situation);

        #endregion

        #region Attack - Delegates to AttackPlanner

        protected PlannedAction PlanAttack(Situation situation, ref float remainingAP, BaseUnitEntity preferTarget = null,
            HashSet<string> excludeTargetIds = null, HashSet<string> excludeAbilityGuids = null)
            => AttackPlanner.PlanAttack(situation, ref remainingAP, RoleName, preferTarget, excludeTargetIds, excludeAbilityGuids);

        protected AbilityData SelectBestAttack(Situation situation, BaseUnitEntity target, HashSet<string> excludeAbilityGuids = null)
            => AttackPlanner.SelectBestAttack(situation, target, excludeAbilityGuids);

        protected PlannedAction PlanPostMoveAttack(Situation situation, BaseUnitEntity target, ref float remainingAP)
            => AttackPlanner.PlanPostMoveAttack(situation, target, ref remainingAP, RoleName);

        // ★ v3.1.24: 이동 목적지 기반 Post-move 공격
        protected PlannedAction PlanPostMoveAttack(Situation situation, BaseUnitEntity target, ref float remainingAP, Vector3? moveDestination)
            => AttackPlanner.PlanPostMoveAttack(situation, target, ref remainingAP, RoleName, moveDestination);

        protected PlannedAction PlanFinisher(Situation situation, BaseUnitEntity target, ref float remainingAP)
            => AttackPlanner.PlanFinisher(situation, target, ref remainingAP, RoleName);

        protected PlannedAction PlanSpecialAbility(Situation situation, ref float remainingAP)
            => AttackPlanner.PlanSpecialAbility(situation, ref remainingAP, RoleName);

        protected PlannedAction PlanSafeRangedAttack(Situation situation, ref float remainingAP,
            HashSet<string> excludeTargetIds = null, HashSet<string> excludeAbilityGuids = null)
            => AttackPlanner.PlanSafeRangedAttack(situation, ref remainingAP, RoleName, excludeTargetIds, excludeAbilityGuids);

        protected BaseUnitEntity FindLowHPEnemy(Situation situation, float threshold)
            => AttackPlanner.FindLowHPEnemy(situation, threshold);

        protected BaseUnitEntity FindWeakestEnemy(Situation situation, HashSet<string> excludeTargetIds = null)
            => AttackPlanner.FindWeakestEnemy(situation, excludeTargetIds);

        protected bool IsExcluded(BaseUnitEntity target, HashSet<string> excludeTargetIds)
            => AttackPlanner.IsExcluded(target, excludeTargetIds);

        protected bool IsAbilityExcluded(AbilityData ability, HashSet<string> excludeAbilityGuids)
            => AttackPlanner.IsAbilityExcluded(ability, excludeAbilityGuids);

        // ★ v3.1.16: AOE 공격 계획 (모든 Role에서 사용 가능)
        protected PlannedAction PlanAoEAttack(Situation situation, ref float remainingAP)
            => AttackPlanner.PlanAoEAttack(situation, ref remainingAP, RoleName);

        // ★ v3.1.29: Self-Targeted AOE 계획 (BladeDance 등)
        protected PlannedAction PlanSelfTargetedAoE(Situation situation, ref float remainingAP)
        {
            // DangerousAoE 중 Self-Target 능력 찾기
            var selfAoEAbilities = situation.AvailableAttacks
                .Where(a => CombatAPI.IsSelfTargetedAoEAttack(a))
                .ToList();

            // AvailableAttacks에서 필터링되었을 수 있으니 전체에서 다시 찾기
            if (selfAoEAbilities.Count == 0)
            {
                selfAoEAbilities = CombatAPI.GetAvailableAbilities(situation.Unit)
                    .Where(a => CombatAPI.IsSelfTargetedAoEAttack(a))
                    .ToList();
            }

            if (selfAoEAbilities.Count == 0) return null;

            foreach (var ability in selfAoEAbilities)
            {
                var result = AttackPlanner.PlanSelfTargetedAoEAttack(situation, ability, ref remainingAP, RoleName);
                if (result != null) return result;
            }

            return null;
        }

        #endregion

        #region AOE Heal/Buff (v3.1.17)

        /// <summary>
        /// ★ v3.1.17: AOE 힐 계획 - 다수 부상 아군 힐
        /// </summary>
        protected PlannedAction PlanAoEHeal(Situation situation, ref float remainingAP)
        {
            // Point 타겟 힐 능력 찾기
            var aoeHeals = situation.AvailableHeals
                .Where(a => CombatAPI.IsPointTargetAbility(a))
                .ToList();

            if (aoeHeals.Count == 0) return null;

            // 부상 아군 필터링 (HP < 80%)
            var woundedAllies = situation.Allies
                .Where(a => a != null && a.IsConscious)
                .Where(a => CombatAPI.GetHPPercent(a) < 80f)
                .ToList();

            // 캐스터도 부상이면 추가
            if (CombatAPI.GetHPPercent(situation.Unit) < 80f)
                woundedAllies.Add(situation.Unit);

            if (woundedAllies.Count < 2) return null;  // AOE 힐은 2명 이상 필요

            foreach (var ability in aoeHeals)
            {
                float cost = CombatAPI.GetAbilityAPCost(ability);
                if (cost > remainingAP) continue;

                var bestPosition = AoESafetyChecker.FindBestAllyAoEPosition(
                    ability,
                    situation.Unit,
                    woundedAllies,
                    minAlliesRequired: 2,
                    requiresWounded: true);

                if (bestPosition == null || !bestPosition.IsSafe) continue;

                string reason;
                if (!CombatAPI.CanUseAbilityOnPoint(ability, bestPosition.Position, out reason))
                {
                    Main.LogDebug($"[{RoleName}] AOE Heal blocked: {ability.Name} - {reason}");
                    continue;
                }

                remainingAP -= cost;
                Main.Log($"[{RoleName}] AOE Heal: {ability.Name} at ({bestPosition.Position.x:F1},{bestPosition.Position.z:F1}) " +
                    $"- {bestPosition.AlliesHit} allies");

                return PlannedAction.PositionalHeal(
                    ability,
                    bestPosition.Position,
                    $"AOE Heal on {bestPosition.AlliesHit} allies",
                    cost);
            }

            return null;
        }

        /// <summary>
        /// ★ v3.1.17: AOE 버프 계획 - 다수 아군 버프
        /// </summary>
        protected PlannedAction PlanAoEBuff(Situation situation, ref float remainingAP)
        {
            // Point 타겟 버프 능력 찾기 (힐, 도발 제외)
            var aoeBuffs = situation.AvailableBuffs
                .Where(a => CombatAPI.IsPointTargetAbility(a))
                .Where(a => !AbilityDatabase.IsTaunt(a))
                .Where(a => !AbilityDatabase.IsHealing(a))
                .ToList();

            if (aoeBuffs.Count == 0) return null;

            // 모든 살아있는 아군 (자신 포함)
            var allAllies = situation.Allies
                .Where(a => a != null && a.IsConscious)
                .ToList();
            allAllies.Add(situation.Unit);

            if (allAllies.Count < 2) return null;  // AOE 버프는 2명 이상 필요

            foreach (var ability in aoeBuffs)
            {
                float cost = CombatAPI.GetAbilityAPCost(ability);
                if (cost > remainingAP) continue;

                // 이미 활성화된 버프 스킵
                if (CombatAPI.HasActiveBuff(situation.Unit, ability)) continue;

                var bestPosition = AoESafetyChecker.FindBestAllyAoEPosition(
                    ability,
                    situation.Unit,
                    allAllies,
                    minAlliesRequired: 2,
                    requiresWounded: false);

                if (bestPosition == null || !bestPosition.IsSafe) continue;

                string reason;
                if (!CombatAPI.CanUseAbilityOnPoint(ability, bestPosition.Position, out reason))
                {
                    Main.LogDebug($"[{RoleName}] AOE Buff blocked: {ability.Name} - {reason}");
                    continue;
                }

                remainingAP -= cost;
                Main.Log($"[{RoleName}] AOE Buff: {ability.Name} at ({bestPosition.Position.x:F1},{bestPosition.Position.z:F1}) " +
                    $"- {bestPosition.AlliesHit} allies");

                return PlannedAction.PositionalBuff(
                    ability,
                    bestPosition.Position,
                    $"AOE Buff on {bestPosition.AlliesHit} allies",
                    cost);
            }

            return null;
        }

        #endregion

        #region Buff/Debuff - Delegates to BuffPlanner

        protected PlannedAction PlanBuffWithReservation(Situation situation, ref float remainingAP, float reservedAP)
            => BuffPlanner.PlanBuffWithReservation(situation, ref remainingAP, reservedAP, RoleName);

        protected PlannedAction PlanDefensiveStanceWithReservation(Situation situation, ref float remainingAP, float reservedAP)
            => BuffPlanner.PlanDefensiveStanceWithReservation(situation, ref remainingAP, reservedAP, RoleName);

        protected PlannedAction PlanAttackBuffWithReservation(Situation situation, ref float remainingAP, float reservedAP)
            => BuffPlanner.PlanAttackBuffWithReservation(situation, ref remainingAP, reservedAP, RoleName);

        protected PlannedAction PlanTaunt(Situation situation, ref float remainingAP)
            => BuffPlanner.PlanTaunt(situation, ref remainingAP, RoleName);

        protected PlannedAction PlanHeroicAct(Situation situation, ref float remainingAP)
            => BuffPlanner.PlanHeroicAct(situation, ref remainingAP, RoleName);

        protected PlannedAction PlanDebuff(Situation situation, BaseUnitEntity target, ref float remainingAP)
            => BuffPlanner.PlanDebuff(situation, target, ref remainingAP, RoleName);

        protected PlannedAction PlanMarker(Situation situation, BaseUnitEntity target, ref float remainingAP)
            => BuffPlanner.PlanMarker(situation, target, ref remainingAP, RoleName);

        protected PlannedAction PlanDefensiveBuff(Situation situation, ref float remainingAP)
            => BuffPlanner.PlanDefensiveBuff(situation, ref remainingAP, RoleName);

        protected PlannedAction PlanPositionalBuff(Situation situation, ref float remainingAP, HashSet<string> usedBuffGuids = null)
            => BuffPlanner.PlanPositionalBuff(situation, ref remainingAP, usedBuffGuids, RoleName);

        protected PlannedAction PlanStratagem(Situation situation, ref float remainingAP)
            => BuffPlanner.PlanStratagem(situation, ref remainingAP, RoleName);

        // ★ v3.5.80: attackPlanned 파라미터 추가
        protected PlannedAction PlanPostAction(Situation situation, ref float remainingAP, bool attackPlanned = false)
            => BuffPlanner.PlanPostAction(situation, ref remainingAP, RoleName, attackPlanned);

        protected PlannedAction PlanTurnEndingAbility(Situation situation, ref float remainingAP)
            => BuffPlanner.PlanTurnEndingAbility(situation, ref remainingAP, RoleName);

        protected bool IsEssentialBuff(AbilityData ability, Situation situation)
            => BuffPlanner.IsEssentialBuff(ability, situation);

        protected bool CanAffordBuffWithReservation(float buffCost, float remainingAP, float reservedAP, bool isEssential)
            => BuffPlanner.CanAffordBuffWithReservation(buffCost, remainingAP, reservedAP, isEssential);

        #endregion

        #region Common Methods (not delegated)

        /// <summary>
        /// ★ v3.1.24: 최종 AP 활용 (모든 주요 행동 실패 후)
        /// Phase 9에서 사용 - 공격/이동 모두 실패했지만 AP가 남았을 때
        /// </summary>
        protected PlannedAction PlanFinalAPUtilization(Situation situation, ref float remainingAP)
        {
            if (remainingAP < 1f) return null;

            string unitId = situation.Unit.UniqueId;
            float currentAP = remainingAP;  // 람다에서 사용하기 위해 로컬 변수에 복사

            // 1. 아직 사용 안 한 저우선순위 버프
            foreach (var buff in situation.AvailableBuffs)
            {
                float cost = CombatAPI.GetAbilityAPCost(buff);
                if (cost > remainingAP) continue;

                string abilityId = buff.Blueprint?.AssetGuid?.ToString();
                if (string.IsNullOrEmpty(abilityId)) continue;

                // 최근 사용된 능력 스킵
                if (AbilityUsageTracker.WasUsedRecently(unitId, abilityId, 1000)) continue;

                // 선제 버프 제외 (공격 없으면 무의미)
                var timing = AbilityDatabase.GetTiming(buff);
                if (timing == AbilityTiming.PreAttackBuff ||
                    timing == AbilityTiming.HeroicAct ||
                    timing == AbilityTiming.RighteousFury)
                    continue;

                // 턴 종료 능력 제외
                if (AbilityDatabase.IsTurnEnding(buff)) continue;

                // 자신 대상 버프
                var selfTarget = new TargetWrapper(situation.Unit);
                string reason;
                if (CombatAPI.CanUseAbilityOn(buff, selfTarget, out reason))
                {
                    remainingAP -= cost;
                    Main.Log($"[{RoleName}] Phase 9: Final buff - {buff.Name}");
                    return PlannedAction.Buff(buff, situation.Unit, "Final AP buff", cost);
                }
            }

            // 2. 디버프 (적에게)
            if (situation.NearestEnemy != null && situation.AvailableDebuffs != null)
            {
                foreach (var debuff in situation.AvailableDebuffs)
                {
                    float cost = CombatAPI.GetAbilityAPCost(debuff);
                    if (cost > remainingAP) continue;

                    string abilityId = debuff.Blueprint?.AssetGuid?.ToString();
                    if (string.IsNullOrEmpty(abilityId)) continue;

                    if (AbilityUsageTracker.WasUsedRecently(unitId, abilityId, 1000)) continue;

                    var target = new TargetWrapper(situation.NearestEnemy);
                    string reason;
                    if (CombatAPI.CanUseAbilityOn(debuff, target, out reason))
                    {
                        remainingAP -= cost;
                        Main.Log($"[{RoleName}] Phase 9: Final debuff - {debuff.Name} -> {situation.NearestEnemy.CharacterName}");
                        return PlannedAction.Attack(debuff, situation.NearestEnemy, "Final AP debuff", cost);
                    }
                }
            }

            // 3. 마커 (적에게)
            // ★ v3.1.28: 이미 마킹된 타겟에 중복 적용 방지
            if (situation.NearestEnemy != null && situation.AvailableMarkers != null)
            {
                string targetId = situation.NearestEnemy.UniqueId;
                foreach (var marker in situation.AvailableMarkers)
                {
                    float cost = CombatAPI.GetAbilityAPCost(marker);
                    if (cost > remainingAP) continue;

                    string abilityId = marker.Blueprint?.AssetGuid?.ToString();
                    if (string.IsNullOrEmpty(abilityId)) continue;

                    // ★ v3.1.28: 타겟별 중복 체크 (같은 타겟에 같은 마커 적용 방지)
                    string usageKey = $"{abilityId}:{targetId}";
                    if (AbilityUsageTracker.WasUsedRecently(unitId, usageKey, 5000))
                    {
                        Main.LogDebug($"[{RoleName}] Phase 9: Skipping {marker.Name} - already used on {situation.NearestEnemy.CharacterName}");
                        continue;
                    }

                    // 능력 자체도 최근 사용 여부 확인
                    if (AbilityUsageTracker.WasUsedRecently(unitId, abilityId, 1000)) continue;

                    var target = new TargetWrapper(situation.NearestEnemy);
                    string reason;
                    if (CombatAPI.CanUseAbilityOn(marker, target, out reason))
                    {
                        remainingAP -= cost;
                        Main.Log($"[{RoleName}] Phase 9: Final marker - {marker.Name} -> {situation.NearestEnemy.CharacterName}");
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
            var clearMPAbility = situation.AvailableAttacks
                .FirstOrDefault(a => CombatAPI.AbilityClearsMPAfterUse(a));

            if (clearMPAbility == null) return null;

            // 선제적 이동 필요 여부 확인
            if (!UtilityScorer.ShouldMoveBeforeClearMPAbility(situation, clearMPAbility))
                return null;

            // 후퇴 위치 찾기
            var retreatAction = MovementPlanner.PlanRetreat(situation);
            if (retreatAction != null)
            {
                Main.Log($"[{RoleName}] ★ Preemptive retreat before {clearMPAbility.Name} (ClearMPAfterUse)");
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
            bool hasClearMPAbility = situation.AvailableAttacks
                .Any(a => CombatAPI.AbilityClearsMPAfterUse(a));

            if (!hasClearMPAbility) return false;

            // 역할별 안전 가중치
            float safetyWeight = UtilityScorer.GetRoleSafetyWeight(situation);
            if (safetyWeight < 0.4f) return false;  // Tank는 무시

            // 위험 상황 또는 적이 가까우면 true
            return situation.IsInDanger || situation.NearestEnemyDistance < situation.MinSafeDistance;
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
            if (remainingAP >= 1f && situation.NearestEnemy != null && situation.AvailableDebuffs.Count > 0)
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
            if (!skipMove && !situation.HasHittableEnemies && situation.HasLivingEnemies &&
                situation.CanMove && situation.AllowPostAttackMove)
            {
                var moveAction = PlanMoveToEnemy(situation);
                if (moveAction != null) actions.Add(moveAction);
            }

            return actions;
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
                var preferredAttacks = situation.AvailableAttacks
                    .Where(a => !AbilityDatabase.IsReload(a) && !AbilityDatabase.IsTurnEnding(a))
                    .Where(a => situation.PrefersRanged ? !a.IsMelee : a.IsMelee)
                    .Select(a => CombatAPI.GetAbilityAPCost(a))
                    .Where(cost => cost >= 1f)
                    .ToList();

                if (preferredAttacks.Count > 0)
                {
                    attackCost = preferredAttacks.Max();
                }
            }

            return Math.Max(attackCost, defaultAttackCost);
        }

        /// <summary>
        /// 주변 적 수 계산
        /// </summary>
        protected int CountNearbyEnemies(Situation situation, float range)
        {
            return situation.Enemies.Count(e =>
                e != null && !e.LifeState.IsDead &&
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

            var types = actions.Select(a => a.Type.ToString()).Distinct();
            return string.Join(" -> ", types);
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
