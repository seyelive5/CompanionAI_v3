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

        protected PlannedAction PlanPostAction(Situation situation, ref float remainingAP)
            => BuffPlanner.PlanPostAction(situation, ref remainingAP, RoleName);

        protected PlannedAction PlanTurnEndingAbility(Situation situation, ref float remainingAP)
            => BuffPlanner.PlanTurnEndingAbility(situation, ref remainingAP, RoleName);

        protected bool IsEssentialBuff(AbilityData ability, Situation situation)
            => BuffPlanner.IsEssentialBuff(ability, situation);

        protected bool CanAffordBuffWithReservation(float buffCost, float remainingAP, float reservedAP, bool isEssential)
            => BuffPlanner.CanAffordBuffWithReservation(buffCost, remainingAP, reservedAP, isEssential);

        #endregion

        #region Common Methods (not delegated)

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

            float distanceToNearest = situation.NearestEnemyDistance;
            float movementRange = situation.CurrentMP * 1.5f;

            if (distanceToNearest > movementRange + 10f) return 0f;

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
