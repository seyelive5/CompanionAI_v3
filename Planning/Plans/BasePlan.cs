using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Enums;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using Kingmaker.View.Covers;
using Kingmaker.Designers.Mechanics.Facts;
using Pathfinding;
using UnityEngine;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Data;
using CompanionAI_v3.Diagnostics;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;
using CompanionAI_v3.Planning.Planners;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Planning.Plans
{
    /// <summary>
    /// ★ v3.0.47: 모든 Role Plan의 기본 클래스
    /// Planners에게 위임하는 얇은 래퍼
    /// </summary>
    public abstract partial class BasePlan
    {
        #region ★ v3.8.78: Zero-alloc temp lists (static 재사용, GC 0)

        /// <summary>LINQ .Where().ToList() 대체용 - 능력 필터링</summary>
        private static readonly List<AbilityData> _tempAbilities = new List<AbilityData>(16);
        /// <summary>LINQ .Where().ToList() 대체용 - 유닛 필터링</summary>
        private static readonly List<BaseUnitEntity> _tempUnits = new List<BaseUnitEntity>(8);
        /// <summary>LINQ .Where().ToList() 대체용 - 액션 필터링</summary>
        private static readonly List<PlannedAction> _tempActions = new List<PlannedAction>(8);

        /// <summary>★ v3.9.10: RecalculateHittable 이전 목록 백업용</summary>
        private static readonly List<BaseUnitEntity> _sharedOldHittable = new List<BaseUnitEntity>(16);
        /// <summary>★ v3.9.10: RecalculateHittable 새 목록용</summary>
        private static readonly List<BaseUnitEntity> _sharedNewHittable = new List<BaseUnitEntity>(16);

        /// <summary>★ v3.104.0: 현재 CreatePlan 호출 동안 이미 선택된 버프 GUID 추적.
        /// PlanAttackBuff/PlanBuff/PlanHeroicAct/PlanPositionalBuff가 공유.
        /// Role Plan CreatePlan 진입 시 ResetPlannedBuffTracking() 호출 필수.
        /// 같은 버프를 턴 내 여러 번 계획하는 문제(once-per-turn 룰 위반) 방지.</summary>
        protected static readonly HashSet<string> _plannedBuffGuids = new HashSet<string>(8);

        /// <summary>★ v3.104.0: CreatePlan 진입 시 버프 중복 추적 초기화.</summary>
        protected static void ResetPlannedBuffTracking() => _plannedBuffGuids.Clear();

        #endregion

        #region Constants — ★ v3.22.0: SC.cs 참조

        protected const float HP_COST_THRESHOLD = SC.HPCostThreshold;
        protected const float DEFAULT_MELEE_ATTACK_COST = SC.DefaultMeleeAttackCost;
        protected const float DEFAULT_RANGED_ATTACK_COST = SC.DefaultRangedAttackCost;
        protected const int MAX_ATTACKS_PER_PLAN = SC.MaxAttacksPerPlan;
        protected const int MAX_POSITIONAL_BUFFS = SC.MaxPositionalBuffs;

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

        protected PlannedAction PlanEmergencyHeal(Situation situation, ref float remainingAP,
            float healThresholdOverride = -1f)
            => HealPlanner.PlanEmergencyHeal(situation, ref remainingAP, RoleName, healThresholdOverride);

        protected PlannedAction PlanReload(Situation situation, ref float remainingAP)
            => HealPlanner.PlanReload(situation, ref remainingAP, RoleName);

        protected BaseUnitEntity FindWoundedAlly(Situation situation, float threshold)
            => HealPlanner.FindWoundedAlly(situation, threshold);

        /// <summary>
        /// ★ v3.42.0: 여유 AP/MP 아군 치유 (이동 포함) — 전 역할 공용
        /// </summary>
        protected List<PlannedAction> PlanOpportunisticAllyHeal(Situation situation, ref float remainingAP, float remainingMP)
            => HealPlanner.PlanOpportunisticAllyHeal(situation, ref remainingAP, remainingMP, RoleName);

        #endregion
    }
}
