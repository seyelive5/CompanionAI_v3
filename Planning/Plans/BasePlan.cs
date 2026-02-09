using System;
using System.Collections.Generic;
using System.Linq;
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
        #region ★ v3.8.78: Zero-alloc temp lists (static 재사용, GC 0)

        /// <summary>LINQ .Where().ToList() 대체용 - 능력 필터링</summary>
        private static readonly List<AbilityData> _tempAbilities = new List<AbilityData>(16);
        /// <summary>LINQ .Where().ToList() 대체용 - 유닛 필터링</summary>
        private static readonly List<BaseUnitEntity> _tempUnits = new List<BaseUnitEntity>(8);
        /// <summary>LINQ .Where().ToList() 대체용 - 액션 필터링</summary>
        private static readonly List<PlannedAction> _tempActions = new List<PlannedAction>(8);

        #endregion

        #region Constants

        protected const float HP_COST_THRESHOLD = 40f;
        protected const float DEFAULT_MELEE_ATTACK_COST = 2f;
        protected const float DEFAULT_RANGED_ATTACK_COST = 2f;
        // ★ v3.8.16: 3 → 10으로 증가 (실질적 제한 해제)
        // AP 부족/PlanAttack null 반환으로 자연스럽게 종료되므로 인위적 제한 불필요
        protected const int MAX_ATTACKS_PER_PLAN = 10;
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

        // ★ v3.8.44: AttackPhaseContext 전달 - 능력 사거리 기반 이동 위치 계산
        protected PlannedAction PlanMoveOrGapCloser(Situation situation, ref float remainingAP, bool forceMove, bool bypassCanMoveCheck, float predictedMP, AttackPhaseContext attackContext)
            => MovementPlanner.PlanMoveOrGapCloser(situation, ref remainingAP, RoleName, forceMove, bypassCanMoveCheck, predictedMP, attackContext);

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

        // ★ v3.8.74: Tactical Reposition delegate
        protected PlannedAction PlanTacticalReposition(Situation situation, float remainingMP)
            => MovementPlanner.PlanTacticalReposition(situation, remainingMP);

        protected bool ShouldRetreat(Situation situation)
            => MovementPlanner.ShouldRetreat(situation);

        #endregion

        #region Attack - Delegates to AttackPlanner

        protected PlannedAction PlanAttack(Situation situation, ref float remainingAP, BaseUnitEntity preferTarget = null,
            HashSet<string> excludeTargetIds = null, HashSet<string> excludeAbilityGuids = null)
            => AttackPlanner.PlanAttack(situation, ref remainingAP, RoleName, preferTarget, excludeTargetIds, excludeAbilityGuids);

        // ★ v3.8.44: AttackPhaseContext 전달 - 공격 실패 이유 기록
        protected PlannedAction PlanAttack(Situation situation, ref float remainingAP, AttackPhaseContext context,
            BaseUnitEntity preferTarget = null, HashSet<string> excludeTargetIds = null, HashSet<string> excludeAbilityGuids = null)
            => AttackPlanner.PlanAttack(situation, ref remainingAP, RoleName, preferTarget, excludeTargetIds, excludeAbilityGuids, context);

        protected AbilityData SelectBestAttack(Situation situation, BaseUnitEntity target, HashSet<string> excludeAbilityGuids = null)
            => AttackPlanner.SelectBestAttack(situation, target, excludeAbilityGuids);

        // ★ v3.8.44: AttackPhaseContext 전달 오버로드
        protected AbilityData SelectBestAttack(Situation situation, BaseUnitEntity target, HashSet<string> excludeAbilityGuids, AttackPhaseContext context)
            => AttackPlanner.SelectBestAttack(situation, target, excludeAbilityGuids, context);

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
            // ★ v3.8.78: LINQ → CollectionHelper (0 할당)
            // DangerousAoE 중 Self-Target 능력 찾기
            CollectionHelper.FillWhere(situation.AvailableAttacks, _tempAbilities,
                a => CombatAPI.IsSelfTargetedAoEAttack(a));

            // AvailableAttacks에서 필터링되었을 수 있으니 전체에서 다시 찾기
            if (_tempAbilities.Count == 0)
            {
                CollectionHelper.FillWhere(CombatAPI.GetAvailableAbilities(situation.Unit), _tempAbilities,
                    a => CombatAPI.IsSelfTargetedAoEAttack(a));
            }

            if (_tempAbilities.Count == 0) return null;

            for (int i = 0; i < _tempAbilities.Count; i++)
            {
                var result = AttackPlanner.PlanSelfTargetedAoEAttack(situation, _tempAbilities[i], ref remainingAP, RoleName);
                if (result != null) return result;
            }

            return null;
        }

        /// ★ v3.8.50: 근접 AOE 계획 (유닛 타겟 근접 스플래시)
        protected PlannedAction PlanMeleeAoE(Situation situation, ref float remainingAP)
        {
            // ★ v3.8.78: LINQ → CollectionHelper (0 할당)
            // AvailableAttacks에서 근접 AOE 검색
            CollectionHelper.FillWhere(situation.AvailableAttacks, _tempAbilities,
                a => CombatAPI.IsMeleeAoEAbility(a));

            // DangerousAoE 필터로 제외되었을 수 있으니 전체에서 다시 찾기
            if (_tempAbilities.Count == 0)
            {
                CollectionHelper.FillWhere(CombatAPI.GetAvailableAbilities(situation.Unit), _tempAbilities,
                    a => CombatAPI.IsMeleeAoEAbility(a));
            }

            if (_tempAbilities.Count == 0) return null;

            return AttackPlanner.PlanMeleeAoEAttack(situation, ref remainingAP, RoleName);
        }

        #endregion

        #region AOE Heal/Buff (v3.1.17)

        /// <summary>
        /// ★ v3.1.17: AOE 힐 계획 - 다수 부상 아군 힐
        /// </summary>
        protected PlannedAction PlanAoEHeal(Situation situation, ref float remainingAP)
        {
            // ★ v3.8.78: LINQ → CollectionHelper (0 할당)
            // Point 타겟 힐 능력 찾기
            CollectionHelper.FillWhere(situation.AvailableHeals, _tempAbilities,
                a => CombatAPI.IsPointTargetAbility(a));

            if (_tempAbilities.Count == 0) return null;

            // 부상 아군 필터링 (HP < 80%)
            CollectionHelper.FillWhere(situation.Allies, _tempUnits,
                a => a.IsConscious && CombatCache.GetHPPercent(a) < 80f);

            // 캐스터도 부상이면 추가
            if (CombatCache.GetHPPercent(situation.Unit) < 80f)
                _tempUnits.Add(situation.Unit);

            if (_tempUnits.Count < 2) return null;  // AOE 힐은 2명 이상 필요

            for (int i = 0; i < _tempAbilities.Count; i++)
            {
                var ability = _tempAbilities[i];
                float cost = CombatAPI.GetAbilityAPCost(ability);
                if (cost > remainingAP) continue;

                var bestPosition = AoESafetyChecker.FindBestAllyAoEPosition(
                    ability,
                    situation.Unit,
                    _tempUnits,
                    minAlliesRequired: 2,
                    requiresWounded: true);

                if (bestPosition == null || !bestPosition.IsSafe) continue;

                string reason;
                if (!CombatAPI.CanUseAbilityOnPoint(ability, bestPosition.Position, out reason))
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] AOE Heal blocked: {ability.Name} - {reason}");
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
            // ★ v3.8.78: LINQ → CollectionHelper (0 할당)
            // Point 타겟 버프 능력 찾기 (힐, 도발 제외)
            CollectionHelper.FillWhere(situation.AvailableBuffs, _tempAbilities,
                a => CombatAPI.IsPointTargetAbility(a) && !AbilityDatabase.IsTaunt(a) && !AbilityDatabase.IsHealing(a));

            if (_tempAbilities.Count == 0) return null;

            // 모든 살아있는 아군 (자신 포함)
            CollectionHelper.FillWhere(situation.Allies, _tempUnits,
                a => a.IsConscious);
            _tempUnits.Add(situation.Unit);

            if (_tempUnits.Count < 2) return null;  // AOE 버프는 2명 이상 필요

            for (int i = 0; i < _tempAbilities.Count; i++)
            {
                var ability = _tempAbilities[i];
                float cost = CombatAPI.GetAbilityAPCost(ability);
                if (cost > remainingAP) continue;

                // ★ v3.8.58: 이미 활성화된 버프 스킵 (캐시된 매핑 사용)
                if (AllyStateCache.HasBuff(situation.Unit, ability)) continue;

                var bestPosition = AoESafetyChecker.FindBestAllyAoEPosition(
                    ability,
                    situation.Unit,
                    _tempUnits,
                    minAlliesRequired: 2,
                    requiresWounded: false);

                if (bestPosition == null || !bestPosition.IsSafe) continue;

                string reason;
                if (!CombatAPI.CanUseAbilityOnPoint(ability, bestPosition.Position, out reason))
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] AOE Buff blocked: {ability.Name} - {reason}");
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

        // ★ v3.8.41: 통합 궁극기 계획 (모든 타겟 유형 처리)
        protected PlannedAction PlanUltimate(Situation situation, ref float remainingAP)
            => BuffPlanner.PlanUltimate(situation, ref remainingAP, RoleName);

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

        /// <summary>
        /// ★ v3.7.93: 아군 버프 계획 (BasePlan으로 이동)
        /// Support/Overseer 모두 사용 가능
        /// ★ v3.8.16: 턴 부여 능력 중복 방지 파라미터 추가 (쳐부숴라 등)
        /// </summary>
        /// <param name="situation">현재 상황</param>
        /// <param name="remainingAP">남은 AP</param>
        /// <param name="usedKeystoneGuids">키스톤 루프에서 이미 사용된 버프 GUID (중복 방지)</param>
        /// <param name="plannedTurnGrantTargetIds">★ v3.8.16: 이미 턴 부여가 계획된 대상 ID (중복 방지)</param>
        /// <param name="plannedBuffTargetPairs">★ v3.8.51: 이미 계획된 (버프GUID:타겟ID) 쌍 (같은 버프를 다른 아군에게 사용 허용)</param>
        /// <returns>아군 버프 행동 또는 null</returns>
        protected PlannedAction PlanAllyBuff(Situation situation, ref float remainingAP, HashSet<string> usedKeystoneGuids = null, HashSet<string> plannedTurnGrantTargetIds = null, HashSet<string> plannedBuffTargetPairs = null)
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
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] AllyBuff: Retreat tactic - buff wounded ally {mostWounded.CharacterName}");
                }
            }
            else if (tactic == TacticalSignal.Attack)
            {
                // ★ v3.8.78: .Where() LINQ 제거 → inline 가드
                // 공격: DPS 우선 버프 (HP 50% 이상인 DPS)
                foreach (var ally in situation.Allies)
                {
                    if (ally == null || ally.LifeState.IsDead) continue;
                    var settings = ModSettings.Instance?.GetOrCreateSettings(ally.UniqueId, ally.CharacterName);
                    if (settings?.Role == AIRole.DPS && CombatCache.GetHPPercent(ally) > 50f)
                    {
                        prioritizedTargets.Add(ally);
                    }
                }
                if (prioritizedTargets.Count > 0)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] AllyBuff: Attack tactic - buff DPS first");
                }
            }

            // 기본 우선순위 (Defend 또는 위에서 대상 없을 때): Tank > DPS > 본인 > 기타
            // 1. Tank 역할 먼저
            foreach (var ally in situation.Allies)
            {
                if (ally == null || ally.LifeState.IsDead) continue;
                var settings = ModSettings.Instance?.GetOrCreateSettings(ally.UniqueId, ally.CharacterName);
                if (settings?.Role == AIRole.Tank && !prioritizedTargets.Contains(ally))
                    prioritizedTargets.Add(ally);
            }

            // 2. DPS 역할
            foreach (var ally in situation.Allies)
            {
                if (ally == null || ally.LifeState.IsDead) continue;
                var settings = ModSettings.Instance?.GetOrCreateSettings(ally.UniqueId, ally.CharacterName);
                if (settings?.Role == AIRole.DPS && !prioritizedTargets.Contains(ally))
                    prioritizedTargets.Add(ally);
            }

            // 3. 본인
            if (!prioritizedTargets.Contains(situation.Unit))
                prioritizedTargets.Add(situation.Unit);

            // 4. 나머지 아군
            foreach (var ally in situation.Allies)
            {
                if (ally == null || ally.LifeState.IsDead) continue;
                if (!prioritizedTargets.Contains(ally))
                    prioritizedTargets.Add(ally);
            }

            foreach (var buff in situation.AvailableBuffs)
            {
                if (buff.Blueprint?.CanTargetFriends != true) continue;

                // ★ v3.7.07 Fix: 실제 사역마에게 성공한 버프만 스킵
                string buffGuid = buff.Blueprint?.AssetGuid?.ToString();
                if (!string.IsNullOrEmpty(buffGuid) && usedKeystoneGuids != null && usedKeystoneGuids.Contains(buffGuid))
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Skip {buff.Name} - successfully used on familiar in Keystone phase");
                    continue;
                }

                float cost = CombatAPI.GetAbilityAPCost(buff);
                if (cost > remainingAP) continue;

                // ★ v3.7.87: 턴 전달 능력인지 확인 (쳐부숴라 등)
                bool isTurnGrant = AbilityDatabase.IsTurnGrantAbility(buff);

                foreach (var target in prioritizedTargets)
                {
                    // ★ v3.8.51: 이미 계획된 (버프, 타겟) 쌍 스킵
                    // 같은 버프를 다른 아군에게는 사용 가능하지만, 동일 조합은 중복 방지
                    string targetId = target.UniqueId ?? target.CharacterName ?? "unknown";
                    if (plannedBuffTargetPairs != null && !string.IsNullOrEmpty(buffGuid))
                    {
                        string pairKey = $"{buffGuid}:{targetId}";
                        if (plannedBuffTargetPairs.Contains(pairKey))
                            continue;
                    }

                    // ★ v3.7.95: 스마트 버프 체크 - 버프 지속시간 확인해서 갱신 필요 여부 판단
                    // NeedsBuffRefresh: 버프 없거나 2라운드 이하 남으면 true (갱신 필요)
                    if (!CombatAPI.NeedsBuffRefresh(target, buff))
                    {
                        int remaining = CombatAPI.GetBuffRemainingRounds(target, buff);
                        string durStr = remaining == -1 ? "영구" : $"{remaining}R";
                        if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Skip {buff.Name} -> {target.CharacterName}: buff active ({durStr} remaining)");
                        continue;
                    }

                    // ★ v3.7.87: 턴 전달 능력은 이미 행동한 유닛에게 쓰면 낭비
                    if (isTurnGrant && TeamBlackboard.Instance.HasActedThisRound(target))
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Skip {buff.Name} -> {target.CharacterName}: already acted this round");
                        continue;
                    }

                    // ★ v3.8.16: 턴 전달 능력이 이미 이 턴에 계획된 대상에게 중복 사용 방지
                    // 같은 계획 단계에서 같은 대상에게 여러 번 쳐부숴라 계획 방지
                    if (isTurnGrant && plannedTurnGrantTargetIds != null && plannedTurnGrantTargetIds.Contains(targetId))
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Skip {buff.Name} -> {target.CharacterName}: turn grant already planned for this target");
                        continue;
                    }

                    var targetWrapper = new TargetWrapper(target);
                    string reason;
                    if (CombatAPI.CanUseAbilityOn(buff, targetWrapper, out reason))
                    {
                        // ★ v3.8.16: 턴 전달 능력 성공 시 대상 ID 기록
                        if (isTurnGrant && plannedTurnGrantTargetIds != null)
                        {
                            plannedTurnGrantTargetIds.Add(targetId);
                            Main.Log($"[{RoleName}] Turn grant planned: {buff.Name} -> {target.CharacterName} (tracked for duplicate prevention)");
                        }

                        remainingAP -= cost;
                        Main.Log($"[{RoleName}] Buff ally: {buff.Name} -> {target.CharacterName}");
                        return PlannedAction.Buff(buff, target, $"Buff {target.CharacterName}", cost);
                    }
                }
            }

            return null;
        }

        #endregion

        #region Post-Plan Validation (v3.8.68)

        /// <summary>
        /// ★ v3.8.68: Post-plan 공격 도달 가능 여부 검증 + 복구
        /// 1. 이동 목적지(또는 현재 위치)에서 모든 공격의 도달 가능 여부 검증
        /// 2. 도달 불가 공격 제거 (AP 복구)
        /// 3. 모든 공격 제거 시 공격 전 버프도 제거 (AP 복구)
        /// 4. didPlanAttack 업데이트
        /// </summary>
        /// <returns>제거된 공격 수</returns>
        protected int ValidateAndRemoveUnreachableAttacks(
            List<PlannedAction> actions,
            Situation situation,
            ref bool didPlanAttack,
            ref float remainingAP)
        {
            var firstMoveAction = CollectionHelper.FirstOrDefault(actions, a => a.Type == ActionType.Move);
            UnityEngine.Vector3 validationPosition;
            bool hasMoveForValidation = firstMoveAction?.MoveDestination != null;

            if (hasMoveForValidation)
                validationPosition = firstMoveAction.MoveDestination.Value;
            else
                validationPosition = situation.Unit.Position;

            // 도달 불가 공격 탐지
            var invalidAttacks = new List<PlannedAction>();
            foreach (var action in actions)
            {
                if (action.Type != ActionType.Attack && action.Type != ActionType.Debuff) continue;
                if (action.Ability == null) continue;

                var targetEntity = action.Target?.Entity as BaseUnitEntity;
                if (targetEntity == null) continue;  // Point 타겟은 스킵

                if (!CombatAPI.CanReachTargetFromPosition(action.Ability, validationPosition, targetEntity))
                {
                    invalidAttacks.Add(action);
                    Main.LogWarning($"[{RoleName}] Attack validation FAILED: {action.Ability.Name} -> {targetEntity.CharacterName} " +
                        $"(unreachable from {(hasMoveForValidation ? "move destination" : "current position")})");
                }
            }

            if (invalidAttacks.Count == 0) return 0;

            // 도달 불가 공격 제거 + AP 복구
            foreach (var invalid in invalidAttacks)
            {
                actions.Remove(invalid);
                remainingAP += invalid.APCost;
                Main.Log($"[{RoleName}] ★ Removed unreachable attack: {invalid.Ability?.Name} -> {invalid.Target?.Entity?.ToString() ?? "?"}");
            }

            // didPlanAttack 업데이트
            didPlanAttack = CollectionHelper.Any(actions, a => a.Type == ActionType.Attack);

            // 모든 공격이 제거됐으면 공격 전 버프도 제거 (낭비 방지)
            if (!didPlanAttack)
            {
                // ★ v3.8.78: LINQ → CollectionHelper (0 할당)
                CollectionHelper.FillWhere(actions, _tempActions,
                    a => a.Type == ActionType.Buff && a.Ability != null && IsPreAttackBuff(a.Ability));

                for (int bi = 0; bi < _tempActions.Count; bi++)
                {
                    var buff = _tempActions[bi];
                    actions.Remove(buff);
                    remainingAP += buff.APCost;
                    Main.Log($"[{RoleName}] ★ Removed orphaned pre-attack buff: {buff.Ability?.Name} (no attacks remaining)");
                }
            }

            return invalidAttacks.Count;
        }

        /// <summary>
        /// ★ v3.8.68: 공격 전 버프 여부 판별
        /// </summary>
        private static bool IsPreAttackBuff(AbilityData ability)
        {
            var timing = AbilityDatabase.GetTiming(ability);
            return timing == AbilityTiming.PreAttackBuff || timing == AbilityTiming.RighteousFury;
        }

        /// <summary>
        /// ★ v3.8.76: 전략 옵션 평가 (공격-이동 조합 선택)
        /// Emergency/Reload Phase 이후, 공격/이동 Phase 전에 호출
        /// </summary>
        protected TacticalEvaluation EvaluateTacticalOptions(Situation situation)
        {
            if (!TacticalOptionEvaluator.ShouldEvaluate(situation))
                return null;

            bool needsRetreat = ShouldRetreat(situation);
            return TacticalOptionEvaluator.Evaluate(situation, needsRetreat, RoleName);
        }

        /// <summary>
        /// ★ v3.8.76: 전략 평가 결과를 Plan 실행에 적용
        /// MoveToAttack → 이동 액션 생성 + HittableEnemies 재계산
        /// AttackThenRetreat → deferRetreat=true
        /// </summary>
        protected PlannedAction ApplyTacticalStrategy(
            TacticalEvaluation eval,
            Situation situation,
            out bool shouldMoveBeforeAttack,
            out bool shouldDeferRetreat)
        {
            shouldMoveBeforeAttack = false;
            shouldDeferRetreat = false;

            if (eval == null || !eval.WasEvaluated)
                return null;

            switch (eval.ChosenStrategy)
            {
                case TacticalStrategy.AttackFromCurrent:
                    // 이동 불필요 - 현재 위치에서 공격 진행
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] TacticalStrategy: AttackFromCurrent (hittable={eval.ExpectedHittableCount})");
                    break;

                case TacticalStrategy.MoveToAttack:
                    // 공격 전 이동 필요
                    shouldMoveBeforeAttack = true;
                    if (eval.MoveDestination.HasValue)
                    {
                        // HittableEnemies 재계산
                        RecalculateHittableFromDestination(situation, eval.MoveDestination.Value);
                        Main.Log($"[{RoleName}] TacticalStrategy: MoveToAttack → ({eval.MoveDestination.Value.x:F1},{eval.MoveDestination.Value.z:F1}), hittable={eval.ExpectedHittableCount}");
                        return PlannedAction.Move(eval.MoveDestination.Value,
                            $"Tactical pre-attack move (hittable: {eval.ExpectedHittableCount})");
                    }
                    break;

                case TacticalStrategy.AttackThenRetreat:
                    // 공격 먼저, 후퇴는 나중에
                    shouldDeferRetreat = true;
                    Main.Log($"[{RoleName}] TacticalStrategy: AttackThenRetreat (attack first, retreat after)");
                    break;

                case TacticalStrategy.MoveOnly:
                    // 공격 불가 - Phase 8에서 이동 처리
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] TacticalStrategy: MoveOnly (no attack possible)");
                    break;
            }

            return null;
        }

        /// <summary>
        /// ★ v3.8.76: 이동 후 위치에서 HittableEnemies 재계산
        ///
        /// 핵심 문제: SituationAnalyzer는 턴 시작 위치에서 HittableEnemies를 판정하지만,
        /// Phase 1.6 후퇴/Phase 8 이동 후에는 새 위치에서 LOS/사거리가 달라짐.
        /// 이동이 계획된 후 공격 Phase 전에 호출하여 정확한 공격 대상 목록 유지.
        ///
        /// 이것이 없으면: 이동 후 도달 불가 공격 계획 → ValidateAndRemoveUnreachableAttacks에서 사후 제거 → AP 낭비
        /// 이것이 있으면: 이동 목적지에서 실제 공격 가능한 적만 대상으로 공격 계획
        /// </summary>
        protected void RecalculateHittableFromDestination(Situation situation, Vector3 destination)
        {
            var destNode = destination.GetNearestNodeXZ() as CustomGridNodeBase;
            if (destNode == null)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] RecalculateHittable: destination node not found");
                return;
            }

            var unit = situation.Unit;
            int oldCount = situation.HittableEnemies.Count;
            var oldHittable = new List<BaseUnitEntity>(situation.HittableEnemies);

            // 이동 후 위치에서 각 적의 도달 가능성 재검사
            // AvailableAttacks 중 하나라도 해당 적을 타겟 가능하면 Hittable
            var newHittable = new List<BaseUnitEntity>();

            foreach (var enemy in situation.Enemies)
            {
                if (enemy == null || enemy.LifeState.IsDead) continue;

                bool canHit = false;
                foreach (var attack in situation.AvailableAttacks)
                {
                    if (CombatHelpers.ShouldExcludeFromAttack(attack, false)) continue;

                    string reason;
                    if (CombatAPI.CanTargetFromPosition(attack, destNode, enemy, out reason))
                    {
                        canHit = true;
                        break;
                    }
                }

                if (canHit)
                    newHittable.Add(enemy);
            }

            // Situation 업데이트
            situation.HittableEnemies.Clear();
            for (int i = 0; i < newHittable.Count; i++)
                situation.HittableEnemies.Add(newHittable[i]);

            // BestTarget이 더 이상 Hittable이 아니면 새 BestTarget 선택
            if (situation.BestTarget != null && !newHittable.Contains(situation.BestTarget))
            {
                var oldBest = situation.BestTarget;
                situation.BestTarget = newHittable.Count > 0 ? newHittable[0] : null;
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] BestTarget changed after move: {oldBest.CharacterName} → {situation.BestTarget?.CharacterName ?? "null"}");
            }

            Main.Log($"[{RoleName}] ★ RecalculateHittable from ({destination.x:F1},{destination.z:F1}): {oldCount} → {newHittable.Count} hittable");

            // 소실된 타겟 로깅 (디버그)
            if (newHittable.Count < oldCount)
            {
                int logged = 0;
                foreach (var enemy in oldHittable)
                {
                    if (!newHittable.Contains(enemy))
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Lost target after move: {enemy.CharacterName}");
                        if (++logged >= 3) break;
                    }
                }
            }
        }

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
                        if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Phase 9: Skipping {marker.Name} - already used on {situation.NearestEnemy.CharacterName}");
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
            var clearMPAbility = CollectionHelper.FirstOrDefault(situation.AvailableAttacks,
                a => CombatAPI.AbilityClearsMPAfterUse(a));

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
            bool hasClearMPAbility = CollectionHelper.Any(situation.AvailableAttacks,
                a => CombatAPI.AbilityClearsMPAfterUse(a));

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
            Main.Log($"[{RoleName}] ★ Hittable mismatch: {mismatchCount} marked hittable but no attack possible - correcting");

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

        #region Familiar Support (v3.7.00)

        /// <summary>
        /// ★ v3.7.02: 모든 키스톤 버프를 사역마에게 시전 (루프)
        /// ★ v3.7.09: Raven의 경우 디버프도 포함 (적에게 확산)
        /// AP가 남아있는 동안 적용 가능한 모든 버프/디버프를 사역마에게 시전
        /// </summary>
        /// <summary>
        /// ★ v3.8.01: heroicActPlanned 파라미터 추가
        /// 계획 단계에서 HeroicAct를 계획했으면 Momentum이 있는 것으로 간주
        /// (버프는 실행 시에만 적용되므로, 계획 단계에서는 "계획됨" 상태로 판단)
        /// </summary>
        protected List<PlannedAction> PlanAllFamiliarKeystoneBuffs(Situation situation, ref float remainingAP, bool heroicActPlanned = false)
        {
            var actions = new List<PlannedAction>();

            // Servo-Skull/Raven만 해당 (Mastiff/Eagle은 버프 확산 없음)
            if (!situation.HasFamiliar || situation.Familiar == null)
                return actions;
            if (situation.FamiliarType != PetType.ServoskullSwarm &&
                situation.FamiliarType != PetType.Raven)
                return actions;

            var optimalPos = situation.OptimalFamiliarPosition;
            if (optimalPos == null)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Keystone Loop: No optimal position");
                return actions;
            }

            // ★ v3.8.78: .ToList() 불필요 복사 제거 (AvailableBuffs는 이미 List<AbilityData>)
            var keystoneBuffs = FamiliarAbilities.FilterAbilitiesForFamiliarSpread(
                situation.AvailableBuffs,
                situation.FamiliarType.Value);

            // ★ v3.7.09: Raven의 경우 디버프도 추가 (Warp Relay로 적에게 확산)
            // ★ v3.7.10: AvailableDebuffs + AvailableAttacks 모두 검사
            // 감각 박탈 등이 Timing=Normal로 분류되어 AvailableAttacks에 있을 수 있음
            var keystoneDebuffs = new List<AbilityData>();
            if (situation.FamiliarType == PetType.Raven)
            {
                // 1. AvailableDebuffs에서 검색
                if (situation.AvailableDebuffs != null)
                {
                    // ★ v3.8.78: .ToList() 불필요 복사 제거
                    var debuffCandidates = FamiliarAbilities.FilterAbilitiesForFamiliarSpread(
                        situation.AvailableDebuffs,
                        PetType.Raven);
                    keystoneDebuffs.AddRange(debuffCandidates);
                }

                // 2. ★ v3.7.10: AvailableAttacks에서도 검색 (Timing=Normal인 디버프)
                // 비피해 사이킥 + 적 타겟 가능 = Warp Relay 디버프 후보
                if (situation.AvailableAttacks != null)
                {
                    foreach (var attack in situation.AvailableAttacks)
                    {
                        // 이미 추가된 건 스킵
                        string guid = attack.Blueprint?.AssetGuid?.ToString();
                        if (CollectionHelper.Any(keystoneDebuffs, d => d.Blueprint?.AssetGuid?.ToString() == guid))
                            continue;

                        // Warp Relay 대상인지 확인 (비피해 사이킹, 적 타겟)
                        if (FamiliarAbilities.IsWarpRelayTarget(attack) &&
                            attack.Blueprint?.CanTargetEnemies == true)
                        {
                            keystoneDebuffs.Add(attack);
                            if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Keystone Loop: Found {attack.Name} in AvailableAttacks for Warp Relay");
                        }
                    }
                }

                if (keystoneDebuffs.Count > 0)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Keystone Loop: {keystoneDebuffs.Count} debuffs eligible for Warp Relay");
                }
            }

            // 버프/디버프 모두 없으면 종료
            if (keystoneBuffs.Count == 0 && keystoneDebuffs.Count == 0)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Keystone Loop: No keystone-eligible abilities found");
                return actions;
            }

            // 사용된 능력 추적
            var usedAbilityGuids = new HashSet<string>();
            var familiarTarget = new TargetWrapper(situation.Familiar);
            var typeName = FamiliarAPI.GetFamiliarTypeName(situation.FamiliarType);

            // ★ v3.7.22: 범위 내 실제 아군 목록 (버프 중복 체크용)
            // ★ v3.8.78: LINQ → CollectionHelper (0 할당)
            CollectionHelper.FillWhere(situation.Allies, _tempUnits,
                a => a.IsConscious && !FamiliarAPI.IsFamiliar(a));
            var alliesInRange = FamiliarAPI.GetAlliesInRadius(
                optimalPos.Position,
                FamiliarPositioner.EFFECT_RADIUS_TILES,
                _tempUnits);

            // ★ v3.8.57: 아군 1명이라도 있으면 Raven Warp Relay 경유 (직접 시전과 동일 AP + 추가 확산 가능성)
            if (keystoneBuffs.Count > 0 && optimalPos.AlliesInRange >= 1)
            {
                foreach (var buff in keystoneBuffs)
                {
                    if (remainingAP < 1f) break;

                    string guid = buff.Blueprint?.AssetGuid?.ToString();
                    if (!string.IsNullOrEmpty(guid) && usedAbilityGuids.Contains(guid))
                        continue;

                    float cost = CombatAPI.GetAbilityAPCost(buff);
                    if (cost > remainingAP) continue;

                    // ★ v3.8.58: 이미 활성화된 버프 스킵 (사역마 체크, 캐시된 매핑 사용)
                    if (AllyStateCache.HasBuff(situation.Familiar, buff)) continue;

                    // ★ v3.8.58: AllyStateCache 기반 버프 보유 체크 (캐시된 아군은 게임 API 호출 없음)
                    int alliesNeedingBuff = 0;
                    foreach (var ally in alliesInRange)
                    {
                        if (!AllyStateCache.HasBuff(ally, buff))
                            alliesNeedingBuff++;
                    }

                    // ★ v3.8.57: 1명이라도 필요하면 Raven 경유 (직접 시전 대비 손해 없고 추가 확산 가능)
                    if (alliesNeedingBuff < 1)
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Keystone Loop: {buff.Name} skipped - no allies need it (all {alliesInRange.Count} already have it)");
                        continue;
                    }

                    // ★ v3.7.71: Point AOE 능력은 위치 타겟, 그 외는 유닛 타겟
                    bool isPointTarget = CombatAPI.IsPointTargetAbility(buff);
                    Vector3 familiarPos = situation.Familiar.Position;

                    string reason;
                    if (isPointTarget)
                    {
                        // Point AOE는 위치로 시전 가능한지 확인
                        if (!CombatAPI.CanUseAbilityOnPoint(buff, familiarPos, out reason))
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Keystone Loop: {buff.Name} blocked (point target) - {reason}");
                            continue;
                        }
                    }
                    else
                    {
                        // 유닛 타겟은 사역마에게 시전 가능한지 확인
                        if (!CombatAPI.CanUseAbilityOn(buff, familiarTarget, out reason))
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Keystone Loop: {buff.Name} blocked - {reason}");
                            continue;
                        }
                    }

                    remainingAP -= cost;
                    if (!string.IsNullOrEmpty(guid))
                        usedAbilityGuids.Add(guid);

                    Main.Log($"[{RoleName}] ★ Familiar Keystone Buff: {buff.Name} on {typeName} " +
                        $"({alliesNeedingBuff}/{optimalPos.AlliesInRange} allies need buff)" +
                        (isPointTarget ? " [Point AOE]" : ""));

                    // ★ v3.7.71: Point AOE는 위치 타겟, 유닛 타겟은 사역마 직접 타겟
                    PlannedAction buffAction;
                    if (isPointTarget)
                    {
                        // Point AOE 능력 - 사역마 위치로 시전 (펫 타겟팅 제한 우회)
                        buffAction = PlannedAction.PositionalBuff(
                            buff,
                            familiarPos,
                            $"Keystone spread: {buff.Name} ({alliesNeedingBuff} allies need it)",
                            cost);
                        // IsFamiliarTarget = false (PositionalBuff는 기본값 false)
                        if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Keystone Point AOE: {buff.Name} at ({familiarPos.x:F1}, {familiarPos.z:F1})");
                    }
                    else
                    {
                        // 유닛 타겟 능력 - 사역마 직접 타겟
                        buffAction = PlannedAction.Buff(
                            buff,
                            situation.Familiar,
                            $"Keystone spread: {buff.Name} ({alliesNeedingBuff} allies need it)",
                            cost);
                        buffAction.IsFamiliarTarget = true;  // 실행 시 사역마 재해석
                    }
                    actions.Add(buffAction);
                }
            }

            // ★ v3.7.96: Raven Warp Relay 재정의
            // 1. 비피해 디버프: Momentum 없이도 Warp Relay로 적에게 전달 가능
            // 2. 피해 사이킹 공격: Momentum(과충전) 있을 때만 Warp Relay로 적에게 전달 가능
            // ★ v3.8.01: heroicActPlanned가 true면 Momentum이 있는 것으로 간주
            // (계획 단계에서는 버프가 아직 적용 안 됨, 실행 시 적용되므로 "계획됨"으로 판단)
            bool buffActive = FamiliarAPI.IsRavenOverchargeActive(situation.Unit);
            bool hasMomentum = situation.FamiliarType == PetType.Raven &&
                               (heroicActPlanned || buffActive);

            if (situation.FamiliarType == PetType.Raven)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Keystone: Momentum check - heroicActPlanned={heroicActPlanned}, buffActive={buffActive}, hasMomentum={hasMomentum}");
            }

            // ★ v3.8.52: 턴 단위 페이즈 기반 디버프 제어
            // 버프 페이즈: Raven이 아군 근처 → 디버프 시전해도 Warp Relay가 적에게 도달 불가 → 스킵
            // 공격 페이즈: Raven이 적 근처로 재배치됨 → 디버프 Warp Relay가 적에게 확산
            bool isRavenBuffPhase = optimalPos.IsBuffPhase;
            if (situation.FamiliarType == PetType.Raven && keystoneDebuffs.Count > 0)
            {
                Main.Log($"[{RoleName}] Raven Phase: {(isRavenBuffPhase ? "BUFF (아군 버프 우선)" : "DEBUFF (적 디버프 전환)")}");
            }

            // ★ 비피해 디버프 처리 (Momentum 불필요) - 적 2명+ 필요
            // ★ v3.8.52: 버프 페이즈에서는 디버프 완전 스킵 (Raven이 아군 근처이므로 무의미)
            // ★ v3.8.53: optimalPos.EnemiesInRange는 재배치 예정(NeedsFamiliarRelocate)일 때만 사용
            //   - 재배치가 Phase 3.3에서 먼저 실행되므로, 디버프 실행 시 Raven은 최적 위치에 있음
            //   - 재배치 없이 optimalPos만 보면 Raven이 아군 근처인데 디버프가 계획되는 버그 발생
            int actualEnemiesNearRaven = 0;
            bool hasEnoughEnemiesForDebuff = false;
            if (!isRavenBuffPhase && keystoneDebuffs.Count > 0 && situation.Familiar != null)
            {
                var ravenCurrentPos = situation.Familiar.Position;
                // ★ v3.8.78: LINQ → CollectionHelper (0 할당)
                CollectionHelper.FillWhere(situation.Enemies, _tempUnits,
                    e => e.IsConscious);
                actualEnemiesNearRaven = FamiliarAPI.CountEnemiesInRadius(
                    ravenCurrentPos, FamiliarPositioner.EFFECT_RADIUS_TILES, _tempUnits);

                // ★ v3.8.53: 재배치 예정 여부에 따라 적 수 판단
                // NeedsFamiliarRelocate=true → Phase 3.3에서 최적 위치로 이동 예정 → optimalPos 기준 사용 가능
                // NeedsFamiliarRelocate=false → Raven은 현재 위치에 머물 → 현재 위치 기준만 사용
                bool willRelocate = situation.NeedsFamiliarRelocate;
                int effectiveEnemyEstimate = willRelocate
                    ? Math.Max(actualEnemiesNearRaven, optimalPos.EnemiesInRange)
                    : actualEnemiesNearRaven;

                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Keystone Debuff: Enemies near Raven current={actualEnemiesNearRaven}, " +
                    $"optimal={optimalPos.EnemiesInRange}, willRelocate={willRelocate}, effective={effectiveEnemyEstimate}");
                // ★ v3.8.56: 적 1명이라도 있으면 디버프 허용 (사람처럼 일단 뭐라도 하기)
                hasEnoughEnemiesForDebuff = effectiveEnemyEstimate >= 1;
            }
            else if (isRavenBuffPhase && keystoneDebuffs.Count > 0)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Keystone Debuff: Skipped (Raven in BUFF phase - prioritizing ally buff distribution)");
            }

            if (keystoneDebuffs.Count > 0 && hasEnoughEnemiesForDebuff)
            {
                int effectiveEnemyCount = Math.Max(actualEnemiesNearRaven, optimalPos.EnemiesInRange);
                foreach (var debuff in keystoneDebuffs)
                {
                    if (remainingAP < 1f) break;

                    string guid = debuff.Blueprint?.AssetGuid?.ToString();
                    if (!string.IsNullOrEmpty(guid) && usedAbilityGuids.Contains(guid))
                        continue;

                    float cost = CombatAPI.GetAbilityAPCost(debuff);
                    if (cost > remainingAP) continue;

                    // 디버프를 Raven에게 시전 가능한지 확인
                    string reason;
                    if (!CombatAPI.CanUseAbilityOn(debuff, familiarTarget, out reason))
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Keystone Debuff: {debuff.Name} can't target Raven - {reason}");
                        continue;
                    }

                    remainingAP -= cost;
                    if (!string.IsNullOrEmpty(guid))
                        usedAbilityGuids.Add(guid);

                    Main.Log($"[{RoleName}] ★ Familiar Keystone Debuff: {debuff.Name} on {typeName} " +
                        $"({effectiveEnemyCount} enemies in range) - Warp Relay spread");

                    var debuffAction = PlannedAction.Attack(
                        debuff,
                        situation.Familiar,
                        $"Warp Relay debuff: {debuff.Name} ({effectiveEnemyCount} enemies)",
                        cost);
                    debuffAction.IsFamiliarTarget = true;
                    actions.Add(debuffAction);
                }
            }
            else if (keystoneDebuffs.Count > 0 && !isRavenBuffPhase)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Keystone Debuff: Not enough enemies near Raven (current={actualEnemiesNearRaven}, optimal={optimalPos.EnemiesInRange})");
            }

            // ★ v3.7.96: 피해 사이킥 공격 처리 (Momentum 필요!) ★ v3.8.56: 적 1명+ 허용
            // Overcharge(과충전) 상태에서만 사이킹 데미지를 Raven에게 사용해 적에게 전달 가능
            // ★ v3.8.52: 버프 페이즈에서는 피해 사이킹 공격도 스킵 (Raven이 아군 근처)
            if (hasMomentum && hasEnoughEnemiesForDebuff && !isRavenBuffPhase && situation.AvailableAttacks != null)
            {
                foreach (var attack in situation.AvailableAttacks)
                {
                    if (remainingAP < 1f) break;

                    // 이미 사용된 능력 스킵
                    string guid = attack.Blueprint?.AssetGuid?.ToString();
                    if (!string.IsNullOrEmpty(guid) && usedAbilityGuids.Contains(guid))
                        continue;

                    // 사이킹 능력이어야 함
                    if (!FamiliarAbilities.IsPsychicAbility(attack))
                        continue;

                    // 피해를 주는 공격이어야 함 (비피해 디버프는 위에서 처리됨)
                    if (!FamiliarAbilities.IsDamagingPsychicAttack(attack))
                        continue;

                    // Point Target 능력 제외 (유닛 타겟만)
                    if (attack.Blueprint?.CanTargetPoint == true && !attack.Blueprint.CanTargetEnemies)
                        continue;

                    float cost = CombatAPI.GetAbilityAPCost(attack);
                    if (cost > remainingAP) continue;

                    // Raven에게 시전 가능한지 확인
                    string reason;
                    if (!CombatAPI.CanUseAbilityOn(attack, familiarTarget, out reason))
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Warp Relay Attack: {attack.Name} can't target Raven - {reason}");
                        continue;
                    }

                    remainingAP -= cost;
                    if (!string.IsNullOrEmpty(guid))
                        usedAbilityGuids.Add(guid);

                    Main.Log($"[{RoleName}] ★ Warp Relay Psychic Attack: {attack.Name} on {typeName} " +
                        $"({optimalPos.EnemiesInRange} enemies) - Momentum active, damage spreads!");

                    var attackAction = PlannedAction.Attack(
                        attack,
                        situation.Familiar,
                        $"Warp Relay attack: {attack.Name} ({optimalPos.EnemiesInRange} enemies)",
                        cost);
                    attackAction.IsFamiliarTarget = true;
                    actions.Add(attackAction);
                }
            }
            else if (hasMomentum && isRavenBuffPhase)
            {
                // ★ v3.8.57: Warp Relay 불가 → Phase 5에서 직접 적 공격으로 폴백
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Warp Relay Attack: Momentum active but in BUFF phase - psychic attacks available as direct cast in Phase 5");
            }
            else if (hasMomentum && !hasEnoughEnemiesForDebuff)
            {
                // ★ v3.8.57: Warp Relay 불가 → Phase 5에서 직접 적 공격으로 폴백
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Warp Relay Attack: Momentum active but no enemies near Raven - psychic attacks available as direct cast in Phase 5");
            }
            else if (!hasMomentum && situation.FamiliarType == PetType.Raven)
            {
                // ★ v3.8.57: Momentum 없어도 사이킹 공격은 Phase 5에서 직접 캐스팅 가능
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] No Momentum: psychic attacks available as direct cast in Phase 5 (no Warp Relay AOE spread)");
            }

            if (actions.Count > 0)
            {
                Main.Log($"[{RoleName}] Keystone Loop: {actions.Count} abilities planned for familiar");
            }

            return actions;
        }

        /// <summary>
        /// ★ v3.7.00: 사역마 Relocate 계획 (턴 초반에 최적 위치로 이동)
        /// ★ v3.7.02: Mastiff는 Relocate 없음
        /// </summary>
        protected PlannedAction PlanFamiliarRelocate(Situation situation, ref float remainingAP)
        {
            // 사역마 없거나 Relocate 불필요
            if (!situation.HasFamiliar || !situation.NeedsFamiliarRelocate)
                return null;

            // ★ v3.7.02: Mastiff는 Relocate 능력이 없음
            if (situation.FamiliarType == PetType.Mastiff)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Familiar Relocate: Mastiff has no Relocate ability");
                return null;
            }

            // Relocate 능력 찾기
            var relocate = CollectionHelper.FirstOrDefault(situation.FamiliarAbilities,
                a => FamiliarAbilities.IsRelocateAbility(a));

            if (relocate == null)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Familiar Relocate: No relocate ability found");
                return null;
            }

            // AP 비용 확인
            float apCost = CombatAPI.GetAbilityAPCost(relocate);
            if (remainingAP < apCost)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Familiar Relocate: Not enough AP ({remainingAP:F1} < {apCost:F1})");
                return null;
            }

            // 최적 위치 확인
            var optimalPos = situation.OptimalFamiliarPosition;
            if (optimalPos == null)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Familiar Relocate: No optimal position");
                return null;
            }

            // LOS/타겟 가능 여부 확인
            string reason;
            if (!CombatAPI.CanUseAbilityOnPoint(relocate, optimalPos.Position, out reason))
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Familiar Relocate blocked: {reason}");
                return null;
            }

            remainingAP -= apCost;

            var typeName = FamiliarAPI.GetFamiliarTypeName(situation.FamiliarType);
            Main.Log($"[{RoleName}] ★ Familiar Relocate: {typeName} to optimal position " +
                $"({optimalPos.AlliesInRange} allies, {optimalPos.EnemiesInRange} enemies in range)");

            // ★ v3.8.30: PositionalBuff 경로 사용 (MultiTarget 경로 문제 해결)
            // - PropertyCalculatorComponent.SaveToContext="ForMainTarget"는 게임의 TaskNodeCastAbility를 통해야 제대로 동작
            // - MultiTarget 경로(UnitUseAbilityParams 직접 실행)는 컨텍스트 설정이 불완전하여 "unit is null" 오류 발생
            // - Point 타겟 능력은 BehaviourTree 컨텍스트 설정 후 TaskNodeCastAbility로 실행해야 함
            return PlannedAction.PositionalBuff(
                relocate,
                optimalPos.Position,
                $"Relocate {typeName} to optimal position",
                apCost);
        }

        /// <summary>
        /// ★ v3.7.00: 사역마 키스톤 능력 계획 (Extrapolation/Warp Relay)
        /// 단일 버프/사이킥을 사역마에 시전 → 4타일 내 모든 아군에게 확산
        /// </summary>
        protected PlannedAction PlanFamiliarKeystone(
            Situation situation,
            AbilityData buffAbility,
            ref float remainingAP)
        {
            // 사역마 없음
            if (!situation.HasFamiliar || situation.Familiar == null)
                return null;

            // 사역마 타입별 키스톤 조건 확인
            bool canUseKeystone = situation.FamiliarType switch
            {
                PetType.ServoskullSwarm => FamiliarAbilities.IsExtrapolationTarget(buffAbility),
                PetType.Raven => FamiliarAbilities.IsWarpRelayTarget(buffAbility),
                _ => false
            };

            if (!canUseKeystone)
                return null;

            // 4타일 내 아군이 2명 이상이어야 의미 있음
            var optimalPos = situation.OptimalFamiliarPosition;
            if (optimalPos == null || optimalPos.AlliesInRange < 2)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Familiar Keystone: Not enough allies in range ({optimalPos?.AlliesInRange ?? 0})");
                return null;
            }

            // AP 비용 확인
            float apCost = CombatAPI.GetAbilityAPCost(buffAbility);
            if (remainingAP < apCost)
                return null;

            // ★ v3.7.71: Point AOE 능력은 위치 타겟, 그 외는 유닛 타겟
            bool isPointTarget = CombatAPI.IsPointTargetAbility(buffAbility);
            Vector3 familiarPos = situation.Familiar.Position;

            // 타겟팅 검증
            string reason;
            if (isPointTarget)
            {
                if (!CombatAPI.CanUseAbilityOnPoint(buffAbility, familiarPos, out reason))
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Familiar Keystone blocked (point): {buffAbility.Name} -> {reason}");
                    return null;
                }
            }
            else
            {
                var familiarTarget = new TargetWrapper(situation.Familiar);
                if (!CombatAPI.CanUseAbilityOn(buffAbility, familiarTarget, out reason))
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Familiar Keystone blocked: {buffAbility.Name} -> {reason}");
                    return null;
                }
            }

            remainingAP -= apCost;

            var typeName = FamiliarAPI.GetFamiliarTypeName(situation.FamiliarType);
            Main.Log($"[{RoleName}] ★ Familiar Keystone: {buffAbility.Name} on {typeName} " +
                $"for AoE spread ({optimalPos.AlliesInRange} allies)" +
                (isPointTarget ? " [Point AOE]" : ""));

            // ★ v3.7.71: Point AOE는 위치 타겟, 유닛 타겟은 사역마 직접 타겟
            PlannedAction action;
            if (isPointTarget)
            {
                action = PlannedAction.PositionalBuff(
                    buffAbility,
                    familiarPos,
                    $"Cast on {typeName} for AoE spread ({optimalPos.AlliesInRange} allies)",
                    apCost);
            }
            else
            {
                action = PlannedAction.Buff(
                    buffAbility,
                    situation.Familiar,
                    $"Cast on {typeName} for AoE spread ({optimalPos.AlliesInRange} allies)",
                    apCost);
                action.IsFamiliarTarget = true;
            }
            return action;
        }

        /// <summary>
        /// ★ v3.7.00: 사역마 Apprehend 계획 (Cyber-Mastiff)
        /// ★ v3.7.04: 연대공격을 위해 Master가 공격할 타겟과 동일한 적 우선
        /// Mastiff Apprehend → Master Attack 순서로 같은 적 공격 시 연대공격 발동
        /// </summary>
        protected PlannedAction PlanFamiliarApprehend(Situation situation, ref float remainingAP)
        {
            // Cyber-Mastiff만 해당
            if (situation.FamiliarType != PetType.Mastiff)
                return null;

            // Apprehend 능력 찾기
            var apprehend = CollectionHelper.FirstOrDefault(situation.FamiliarAbilities,
                a => FamiliarAbilities.IsApprehendAbility(a));

            if (apprehend == null)
                return null;

            // AP 비용 확인
            float apCost = CombatAPI.GetAbilityAPCost(apprehend);
            if (remainingAP < apCost)
                return null;

            // ★ v3.7.04: 연대공격을 위해 Master가 공격할 타겟을 최우선 타겟으로
            // Mastiff Apprehend → Master Attack 같은 적 → 연대공격 보너스
            BaseUnitEntity targetEnemy = null;
            bool isCoordinated = false;

            // 1순위: Master가 공격 가능한 적 중 가장 좋은 타겟 (HittableEnemies)
            // HP가 낮은 적을 우선 (Master가 마무리하기 좋음)
            if (situation.HittableEnemies != null && situation.HittableEnemies.Count > 0)
            {
                // ★ v3.8.48: LINQ → CollectionHelper (0 할당, O(n))
                // HP%가 낮은 순으로 정렬 (마무리 타겟)
                var bestHittable = CollectionHelper.MinByWhere(situation.HittableEnemies,
                    e => e.IsConscious,
                    e => CombatCache.GetHPPercent(e));

                if (bestHittable != null)
                {
                    var bestTarget = new TargetWrapper(bestHittable);
                    string bestReason;
                    if (CombatAPI.CanUseAbilityOn(apprehend, bestTarget, out bestReason))
                    {
                        targetEnemy = bestHittable;
                        isCoordinated = true;
                        Main.Log($"[{RoleName}] Mastiff Apprehend: Targeting hittable enemy for coordinated attack");
                    }
                    else
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Mastiff Apprehend: Hittable enemy blocked - {bestReason}");
                    }
                }
            }

            // 2순위: NearestEnemy (이동 후 공격 가능)
            if (targetEnemy == null && situation.NearestEnemy != null && situation.NearestEnemy.IsConscious)
            {
                var nearTarget = new TargetWrapper(situation.NearestEnemy);
                string nearReason;
                if (CombatAPI.CanUseAbilityOn(apprehend, nearTarget, out nearReason))
                {
                    targetEnemy = situation.NearestEnemy;
                    isCoordinated = true;  // NearestEnemy도 Master가 공격할 가능성 높음
                    Main.Log($"[{RoleName}] Mastiff Apprehend: Targeting NearestEnemy for coordinated attack");
                }
            }

            // 3순위: 원거리 위협 적
            if (targetEnemy == null)
            {
                // ★ v3.8.48: LINQ → CollectionHelper (0 할당, O(n))
                targetEnemy = CollectionHelper.MaxByWhere(situation.Enemies,
                    e => e.IsConscious && CombatAPI.HasRangedWeapon(e),
                    e => (float)(e.Health?.MaxHitPoints ?? 0));
            }

            // 4순위: 아무 적이라도
            if (targetEnemy == null)
            {
                // ★ v3.8.48: LINQ → CollectionHelper (0 할당, O(n))
                targetEnemy = CollectionHelper.MaxByWhere(situation.Enemies,
                    e => e.IsConscious,
                    e => (float)(e.Health?.MaxHitPoints ?? 0));
            }

            if (targetEnemy == null)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Mastiff Apprehend: No valid target found");
                return null;
            }

            // 타겟 가능 여부 확인 (3순위/4순위용)
            if (!isCoordinated)
            {
                var targetWrapper = new TargetWrapper(targetEnemy);
                string reason;
                if (!CombatAPI.CanUseAbilityOn(apprehend, targetWrapper, out reason))
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Mastiff Apprehend blocked: {reason}");
                    return null;
                }
            }

            remainingAP -= apCost;

            string coordMsg = isCoordinated ? " (Coordinated Attack)" : "";
            Main.Log($"[{RoleName}] ★ Mastiff Apprehend: {targetEnemy.CharacterName}{coordMsg}");

            return PlannedAction.Attack(
                apprehend,
                targetEnemy,
                $"Mastiff Apprehend on {targetEnemy.CharacterName}{coordMsg}",
                apCost);
        }

        /// <summary>
        /// ★ v3.7.00: 사역마 Obstruct Vision 계획 (Cyber-Eagle)
        /// 적 밀집 지역에 시야 방해 → 아군 오사 유발 / 적 명중률 감소
        /// </summary>
        protected PlannedAction PlanFamiliarObstruct(Situation situation, ref float remainingAP)
        {
            // Cyber-Eagle만 해당
            if (situation.FamiliarType != PetType.Eagle)
                return null;

            // ★ v3.7.31: 단일 타겟 능력만 처리 (MultiTarget 활공 버전은 PlanFamiliarAerialRush에서 처리)
            // Obstruct Vision 능력 찾기
            var obstruct = CollectionHelper.FirstOrDefault(situation.FamiliarAbilities,
                a => FamiliarAbilities.IsObstructVisionAbility(a) &&
                                     !FamiliarAbilities.IsMultiTargetFamiliarAbility(a));

            if (obstruct == null)
            {
                // Blinding Strike 폴백 (단일 타겟만)
                obstruct = CollectionHelper.FirstOrDefault(situation.FamiliarAbilities,
                    a => FamiliarAbilities.IsBlindingStrikeAbility(a) &&
                                        !FamiliarAbilities.IsMultiTargetFamiliarAbility(a));
            }

            if (obstruct == null)
                return null;

            // AP 비용 확인
            float apCost = CombatAPI.GetAbilityAPCost(obstruct);
            if (remainingAP < apCost)
                return null;

            // ★ v3.8.48: LINQ → CollectionHelper (O(n²) 클러스터링 but 0 할당)
            // 적 밀집 지역 (2명 이상) 또는 위협적 적 찾기
            // 합산 scorer: nearbyCount * 10000 + maxHP로 ThenByDescending 시뮬레이션
            var targetEnemy = CollectionHelper.MaxByWhere(situation.Enemies,
                e => e.IsConscious,
                e =>
                {
                    int nearbyCount = CollectionHelper.CountWhere(situation.Enemies,
                        other => other.IsConscious && other != e &&
                        CombatCache.GetDistanceInTiles(e, other) <= 3f);
                    return nearbyCount * 10000f + (float)(e.Health?.MaxHitPoints ?? 0);
                });

            if (targetEnemy == null)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Eagle Obstruct: No suitable target found");
                return null;
            }

            // 타겟 가능 여부 확인
            var targetWrapper = new TargetWrapper(targetEnemy);
            string reason;
            if (!CombatAPI.CanUseAbilityOn(obstruct, targetWrapper, out reason))
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Eagle Obstruct blocked: {reason}");
                return null;
            }

            remainingAP -= apCost;

            Main.Log($"[{RoleName}] ★ Eagle Obstruct Vision: {targetEnemy.CharacterName}");

            return PlannedAction.Attack(
                obstruct,
                targetEnemy,
                $"Eagle Obstruct Vision on {targetEnemy.CharacterName}",
                apCost);
        }

        /// <summary>
        /// ★ v3.7.01: 사역마 Protect! 계획 (Cyber-Mastiff)
        /// 위협받거나 HP가 낮은 아군 호위
        /// </summary>
        protected PlannedAction PlanFamiliarProtect(Situation situation, ref float remainingAP)
        {
            // Cyber-Mastiff만 해당
            if (situation.FamiliarType != PetType.Mastiff)
                return null;

            // Protect! 능력 찾기
            var protect = CollectionHelper.FirstOrDefault(situation.FamiliarAbilities,
                a => FamiliarAbilities.IsProtectAbility(a));

            if (protect == null)
                return null;

            // AP 비용 확인
            float apCost = CombatAPI.GetAbilityAPCost(protect);
            if (remainingAP < apCost)
                return null;

            // ★ v3.8.48: LINQ → CollectionHelper (0 할당, O(n))
            // 보호할 아군 찾기 (HP 낮거나 주변 적이 많은 아군)
            // HP 낮을수록 우선 (MinBy), 같은 HP면 주변 적이 많은 순 (ThenByDescending 시뮬)
            // scorer: HP% * 100 - nearbyEnemies (낮을수록 우선)
            var allyToProtect = CollectionHelper.MinByWhere(situation.Allies,
                a => a.IsConscious && !FamiliarAPI.IsFamiliar(a) && a != situation.Unit,
                a =>
                {
                    int nearbyEnemyCount = CollectionHelper.CountWhere(situation.Enemies,
                        e => e.IsConscious && CombatCache.GetDistanceInTiles(a, e) <= 3f);
                    return CombatCache.GetHPPercent(a) * 100f - nearbyEnemyCount;
                });

            if (allyToProtect == null)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Mastiff Protect: No ally to protect");
                return null;
            }

            // HP가 70% 이상이고 주변 적이 없으면 스킵
            float allyHP = CombatCache.GetHPPercent(allyToProtect);
            // ★ v3.8.48: LINQ → CollectionHelper (0 할당)
            int nearbyEnemies = CollectionHelper.CountWhere(situation.Enemies, e =>
                e.IsConscious &&
                CombatCache.GetDistanceInTiles(allyToProtect, e) <= 3f);

            if (allyHP > 70f && nearbyEnemies == 0)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Mastiff Protect: {allyToProtect.CharacterName} doesn't need protection (HP={allyHP:F0}%, enemies nearby=0)");
                return null;
            }

            // 타겟 가능 여부 확인
            var targetWrapper = new TargetWrapper(allyToProtect);
            string reason;
            if (!CombatAPI.CanUseAbilityOn(protect, targetWrapper, out reason))
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Mastiff Protect blocked: {reason}");
                return null;
            }

            remainingAP -= apCost;

            Main.Log($"[{RoleName}] ★ Mastiff Protect: {allyToProtect.CharacterName} (HP={allyHP:F0}%, nearby enemies={nearbyEnemies})");

            return PlannedAction.Buff(
                protect,
                allyToProtect,
                $"Mastiff Protect {allyToProtect.CharacterName}",
                apCost);
        }

        /// <summary>
        /// ★ v3.7.45: 사역마 Aerial Rush 계획 (Cyber-Eagle)
        /// 이동 + 공격 능력 - 타겟까지 돌진하며 경로상 적에게 피해
        ///
        /// ★ 핵심 메커니즘:
        /// - Eagle은 턴 시작 시 필드에 없음 → 첫 클릭 시 하늘에서 내려옴
        /// - Point1: Master가 능력 사거리(ability.RangeCells) 내에서 클릭
        /// - Point2: Point1에서 Eagle 이동 범위(Familiar MP) 내 착륙 위치
        ///
        /// ★ Overseer 아키타입: 사역마 활용이 메인 → 이동해서라도 사용
        /// </summary>
        protected PlannedAction PlanFamiliarAerialRush(Situation situation, ref float remainingAP)
        {
            // ★ v3.7.43: 디버그 로그 추가
            if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Aerial Rush: Entry - FamiliarType={situation.FamiliarType}, " +
                $"FamiliarAbilities={situation.FamiliarAbilities?.Count ?? 0}, AP={remainingAP:F1}");

            // Cyber-Eagle만 해당
            if (situation.FamiliarType != PetType.Eagle)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Aerial Rush: Skip - Not Eagle (type={situation.FamiliarType})");
                return null;
            }

            // ★ v3.7.31: 모든 Eagle MultiTarget 능력 처리 (우선순위 기반)
            // 우선순위: AerialRush > AerialRushSupport > ObstructVision(Glide) > 기타 MultiTarget
            AbilityData aerialRush = null;

            // 1. AerialRush (데미지 우선)
            aerialRush = CollectionHelper.FirstOrDefault(situation.FamiliarAbilities,
                a => FamiliarAbilities.IsAerialRushAbility(a));

            // 2. AerialRush Support (실명 공격 — 활공)
            if (aerialRush == null)
            {
                aerialRush = CollectionHelper.FirstOrDefault(situation.FamiliarAbilities,
                    a => FamiliarAbilities.IsAerialRushSupportAbility(a));
            }

            // 3. ObstructVision Glide 버전 (시야 방해 — 활공)
            if (aerialRush == null)
            {
                aerialRush = CollectionHelper.FirstOrDefault(situation.FamiliarAbilities,
                    a => FamiliarAbilities.IsObstructVisionAbility(a) &&
                                         FamiliarAbilities.IsMultiTargetFamiliarAbility(a));
            }

            // 4. 기타 모든 Eagle MultiTarget 능력 (폴백)
            if (aerialRush == null)
            {
                aerialRush = CollectionHelper.FirstOrDefault(situation.FamiliarAbilities,
                    a => FamiliarAbilities.IsMultiTargetFamiliarAbility(a));
            }

            if (aerialRush == null)
            {
                // ★ v3.7.43: 모든 능력 GUID 로그
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Aerial Rush: No MultiTarget ability found. Available abilities:");
                if (situation.FamiliarAbilities != null)
                {
                    foreach (var ab in situation.FamiliarAbilities)
                    {
                        bool isMulti = FamiliarAbilities.IsMultiTargetFamiliarAbility(ab);
                        bool isAerial = FamiliarAbilities.IsAerialRushAbility(ab);
                        if (Main.IsDebugEnabled) Main.LogDebug($"  - {ab.Name} [{ab.Blueprint?.AssetGuid}] MultiTarget={isMulti}, AerialRush={isAerial}");
                    }
                }
                return null;
            }

            // AP 비용 확인
            float apCost = CombatAPI.GetAbilityAPCost(aerialRush);
            if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Aerial Rush: Found ability={aerialRush.Name}, APCost={apCost:F1}, RemainingAP={remainingAP:F1}");

            // ★ v3.7.46: 디버그 - TargetRestrictions 덤프
            try
            {
                var restrictions = aerialRush.Blueprint?.TargetRestrictions;
                if (restrictions != null && restrictions.Length > 0)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Aerial Rush: TargetRestrictions ({restrictions.Length} total):");
                    foreach (var restriction in restrictions)
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"  - {restriction.GetType().Name}");
                    }
                }
                else
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Aerial Rush: No TargetRestrictions");
                }

                // MultiTarget 컴포넌트 정보
                var components = aerialRush.Blueprint?.ComponentsArray;
                if (components != null)
                {
                    foreach (var comp in components)
                    {
                        // ★ v3.8.61: string 매칭 → is 연산자
                        if (comp is Kingmaker.UnitLogic.Abilities.Components.AbilityTargetsAround)
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Aerial Rush: Has AbilityTargetsAround component");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Aerial Rush: Error dumping restrictions: {ex.Message}");
            }

            if (remainingAP < apCost)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Aerial Rush: Insufficient AP ({remainingAP:F1} < {apCost:F1})");
                return null;
            }

            var masterNode = situation.Unit.Position.GetNearestNodeXZ() as CustomGridNodeBase;
            if (masterNode == null)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Aerial Rush: Master node is null");
                return null;
            }

            // ★ v3.7.46: Point1, Point2 범위 결정
            //
            // 게임 메커니즘 분석 결과:
            // - Eagle 활공 능력은 AbilityRange.Unlimited (100000 타일) 반환
            // - 하지만 WarhammerOverrideAbilityCasterPositionByPet 컴포넌트가 있으면
            //   거리 계산 시 Pet(Eagle) 위치가 사용됨
            // - 실제 제한은 LOS(시야선)로 걸림 (NeedLoS=true면 HasLos 체크)
            // - Point1: Master가 클릭 → Eagle이 "나타날" 위치
            // - Point2: Eagle이 Point1에서 이동할 착륙 위치
            //
            var familiar = FamiliarAPI.GetFamiliar(situation.Unit);
            float familiarMP = familiar != null ? CombatAPI.GetCurrentMP(familiar) : 0f;

            // ★ v3.7.46: 컴포넌트 분석 (디버깅용)
            bool hasOverrideCasterByPet = false;
            try
            {
                var components = aerialRush.Blueprint?.ComponentsArray;
                if (components != null)
                {
                    foreach (var comp in components)
                    {
                        // ★ v3.8.59: 타입 안전 체크 (string 매칭 제거)
                        if (comp is WarhammerOverrideAbilityCasterPositionByPet ||
                            comp is WarhammerOverrideAbilityCasterPositionContextual)
                        {
                            hasOverrideCasterByPet = true;
                            if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Aerial Rush: Found {comp.GetType().Name} - distance calc uses Pet position");
                            break;
                        }
                    }
                }
            }
            catch { }

            // Point1 범위: RangeCells가 Unlimited(100000)면 LOS 기반 계산으로 대체
            // (AI가 100000 타일 탐색하면 게임 멈춤)
            int point1RangeTiles;

            // 적 위치 기반으로 실용적인 탐색 범위 계산
            float maxEnemyDist = 0f;
            foreach (var enemy in situation.Enemies)
            {
                if (enemy == null || !enemy.IsConscious) continue;
                float dist = CombatCache.GetDistanceInTiles(situation.Unit, enemy);  // 캐시 기반 타일 거리
                if (dist > maxEnemyDist) maxEnemyDist = dist;
            }

            // Point1 범위 = 적까지 거리 + Eagle 이동력 + 여유분 (최대 60타일)
            // 이렇게 하면 적을 타격할 수 있는 모든 Point1 후보를 포함함
            point1RangeTiles = Math.Max(10, Math.Min((int)(maxEnemyDist + familiarMP + 5), 60));

            // ★ v3.7.54: 게임이 실제 사용하는 Support 능력의 RangeCells 사용
            // 원인: AI가 Eagle MP 기준으로 Point2 계산 → 게임은 Support_Ascended_Ability.RangeCells로 검증
            // → 두 값이 다르면 TargetRestrictionNotPassed 발생
            int point2RangeTiles = CombatAPI.GetMultiTargetPoint2RangeInTiles(aerialRush);

            if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Aerial Rush: Point1={point1RangeTiles} tiles (maxEnemy={maxEnemyDist:F0}), " +
                $"Point2={point2RangeTiles} tiles (from Support ability RangeCells), EagleMP={familiarMP:F0}, OverrideCaster={hasOverrideCasterByPet}");

            // ★ v3.7.48: Eagle 위치 기반 경로 탐색
            // 게임 검증 분석 결과: Point2 검증은 Eagle.Position에서 수행됨
            // → Point1 = Eagle.Position으로 고정해야 TargetRestrictionNotPassed 방지
            CustomGridNodeBase eagleNode = null;
            if (familiar != null)
            {
                eagleNode = situation.FamiliarPosition.GetNearestNodeXZ() as CustomGridNodeBase;
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Aerial Rush: Using Eagle position ({situation.FamiliarPosition.x:F1},{situation.FamiliarPosition.z:F1})");
            }

            CustomGridNodeBase bestPoint1Node, bestPoint2Node;
            bool foundPath = PointTargetingHelper.FindBestAerialRushPath(
                masterNode,
                situation.Unit.SizeRect,
                point1RangeTiles,
                point2RangeTiles,
                situation.Enemies,
                out bestPoint1Node,
                out bestPoint2Node,
                eagleNode,  // ★ v3.7.48: Eagle 위치 전달
                familiar);  // ★ v3.7.50: Charge 경로 검증용

            // ★ v3.7.45: 현재 위치에서 안 되면 Master 이동 고려 (Overseer 핵심 기능!)
            CustomGridNodeBase masterMoveNode = null;
            if (!foundPath)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Aerial Rush: No path from current position, checking Master movement...");

                float masterMP = CombatAPI.GetCurrentMP(situation.Unit);
                int masterMPTiles = (int)masterMP;

                if (masterMPTiles >= 2)  // 최소 2타일 이동 가능해야 의미 있음
                {
                    foundPath = PointTargetingHelper.FindBestMasterPositionForAerialRush(
                        masterNode,
                        situation.Unit.SizeRect,
                        masterMPTiles,
                        point1RangeTiles,
                        point2RangeTiles,
                        situation.Enemies,
                        out masterMoveNode,
                        out bestPoint1Node,
                        out bestPoint2Node,
                        eagleNode,  // ★ v3.7.48: Eagle 위치 전달
                        familiar);  // ★ v3.7.50: Charge 경로 검증용

                    if (foundPath && masterMoveNode != null)
                    {
                        Vector3 movePos = (Vector3)masterMoveNode.Vector3Position;
                        if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Aerial Rush: Found path after Master moves to ({movePos.x:F1},{movePos.z:F1})");
                    }
                }
            }

            if (!foundPath || bestPoint1Node == null || bestPoint2Node == null)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Aerial Rush: No valid path found (even with movement)");
                return null;
            }

            // Point1, Point2 확정
            UnityEngine.Vector3 point1 = (UnityEngine.Vector3)bestPoint1Node.Vector3Position;
            UnityEngine.Vector3 point2 = (UnityEngine.Vector3)bestPoint2Node.Vector3Position;

            // ★ v3.8.07: P1 → P2 경로에서 적 타격 (실제 패스파인딩 사용)
            var familiarAgent = familiar?.MaybeMovementAgent;
            int estimatedPathTargets = familiarAgent != null
                ? PointTargetingHelper.CountEnemiesInChargePath(point1, point2, situation.Enemies, familiarAgent)
                : PointTargetingHelper.CountEnemiesInChargePath(point1, point2, situation.Enemies);

            // 경로 상에 있는 첫 번째 적 이름 찾기 (로깅용)
            string targetName = "path";
            UnityEngine.Vector3 direction = (point2 - point1).normalized;
            float pathLength = UnityEngine.Vector3.Distance(point1, point2);
            foreach (var enemy in situation.Enemies)
            {
                if (enemy == null || !enemy.IsConscious) continue;
                UnityEngine.Vector3 toEnemy = enemy.Position - point1;
                float proj = UnityEngine.Vector3.Dot(toEnemy, direction);
                if (proj >= 0 && proj <= pathLength)
                {
                    UnityEngine.Vector3 closestPoint = point1 + direction * proj;
                    float perpDist = UnityEngine.Vector3.Distance(enemy.Position, closestPoint);
                    if (perpDist <= 2.7f)
                    {
                        targetName = enemy.CharacterName ?? "enemy";
                        break;
                    }
                }
            }

            // ★ v3.7.45: Master 이동이 필요한 경우
            // 이동 행동만 먼저 반환 → 다음 사이클에서 능력 사용
            // (게임은 이동 완료 후 다시 AI 업데이트를 호출함)
            if (masterMoveNode != null)
            {
                Vector3 masterMovePos = (Vector3)masterMoveNode.Vector3Position;
                Main.Log($"[{RoleName}] ★ Eagle Aerial Rush requires movement: " +
                    $"Master moves to ({masterMovePos.x:F1},{masterMovePos.z:F1}) first, " +
                    $"then will use Point1({point1.x:F1},{point1.z:F1}) -> Point2({point2.x:F1},{point2.z:F1}) " +
                    $"through {targetName} ({estimatedPathTargets} enemies in path)");

                // 이동만 먼저 반환 - 다음 AI 사이클에서 Aerial Rush 재계획됨
                return PlannedAction.Move(masterMovePos, $"Move for Aerial Rush ({estimatedPathTargets} enemies)");
            }

            remainingAP -= apCost;

            // MultiTarget 리스트 생성
            var targets = new System.Collections.Generic.List<TargetWrapper>
            {
                new TargetWrapper(point1),
                new TargetWrapper(point2)
            };

            Main.Log($"[{RoleName}] ★ Eagle Aerial Rush: Point1({point1.x:F1},{point1.z:F1}) -> Point2({point2.x:F1},{point2.z:F1}) through {targetName} ({estimatedPathTargets} enemies in path)");

            return PlannedAction.MultiTargetAttack(
                aerialRush,
                targets,
                $"Aerial Rush through {targetName} ({estimatedPathTargets} in path)",
                apCost);
        }

        // ★ v3.7.36: CountEnemiesInPath, FindNearestUnoccupiedCell 제거
        // → PointTargetingHelper로 통합

        /// <summary>
        /// ★ v3.7.14: 사역마 Blinding Dive 계획 (Cyber-Eagle)
        /// 이동+공격+실명 디버프 - 원거리 적 우선 (실명 효과 극대화)
        /// </summary>
        protected PlannedAction PlanFamiliarBlindingDive(Situation situation, ref float remainingAP)
        {
            // Cyber-Eagle만 해당
            if (situation.FamiliarType != PetType.Eagle)
                return null;

            // Blinding Dive 능력 찾기 (BlindingStrike와 동일 GUID)
            var blindingDive = CollectionHelper.FirstOrDefault(situation.FamiliarAbilities,
                a => FamiliarAbilities.IsBlindingDiveAbility(a));

            if (blindingDive == null)
                return null;

            // AP 비용 확인
            float apCost = CombatAPI.GetAbilityAPCost(blindingDive);
            if (remainingAP < apCost)
                return null;

            // 타겟 선정: 원거리 적 > 고HP 적 > 아무 적
            // ★ v3.8.78: LINQ → CollectionHelper (0 할당)
            CollectionHelper.FillWhere(situation.Enemies, _tempUnits,
                e => e.IsConscious);
            var enemies = _tempUnits;

            if (enemies.Count == 0)
                return null;

            BaseUnitEntity targetEnemy = null;

            // ★ v3.8.48: LINQ → for 루프 (GC 압박 감소)
            // 1순위: 원거리 적 (실명 효과 극대화) - HP 높은 순
            {
                float bestHP = float.MinValue;
                for (int i = 0; i < enemies.Count; i++)
                {
                    var e = enemies[i];
                    if (!CombatAPI.HasRangedWeapon(e)) continue;
                    float hp = CombatCache.GetHPPercent(e);
                    if (hp > bestHP)
                    {
                        var tw = new TargetWrapper(e);
                        if (CombatAPI.CanUseAbilityOn(blindingDive, tw, out _))
                        {
                            targetEnemy = e;
                            bestHP = hp;
                        }
                    }
                }
            }

            // 2순위: 고HP 적 (실명 지속 효과)
            if (targetEnemy == null)
            {
                float bestHP = float.MinValue;
                for (int i = 0; i < enemies.Count; i++)
                {
                    var e = enemies[i];
                    float hp = CombatCache.GetHPPercent(e);
                    if (hp > bestHP)
                    {
                        var tw = new TargetWrapper(e);
                        if (CombatAPI.CanUseAbilityOn(blindingDive, tw, out _))
                        {
                            targetEnemy = e;
                            bestHP = hp;
                        }
                    }
                }
            }

            if (targetEnemy == null)
                return null;

            remainingAP -= apCost;
            bool isRanged = CombatAPI.HasRangedWeapon(targetEnemy);

            Main.Log($"[{RoleName}] ★ Eagle Blinding Dive: {targetEnemy.CharacterName} ({(isRanged ? "Ranged" : "Melee")}, HP={CombatCache.GetHPPercent(targetEnemy):F0}%)");

            return PlannedAction.Attack(
                blindingDive,
                targetEnemy,
                $"Blinding Dive to {targetEnemy.CharacterName} (Blind debuff)",
                apCost);
        }

        /// <summary>
        /// ★ v3.7.14: 사역마 Jump Claws 계획 (Cyber-Mastiff)
        /// 점프+클로우 공격 - 클러스터 중심 타겟 우선
        /// </summary>
        protected PlannedAction PlanFamiliarJumpClaws(Situation situation, ref float remainingAP)
        {
            // Cyber-Mastiff만 해당
            if (situation.FamiliarType != PetType.Mastiff)
                return null;

            // Jump Claws 능력 찾기
            var jumpClaws = CollectionHelper.FirstOrDefault(situation.FamiliarAbilities,
                a => FamiliarAbilities.IsJumpClawsAbility(a));

            if (jumpClaws == null)
                return null;

            // AP 비용 확인
            float apCost = CombatAPI.GetAbilityAPCost(jumpClaws);
            if (remainingAP < apCost)
                return null;

            // 타겟 선정: 클러스터 중심 > 저HP 적 > 가장 가까운 적
            // ★ v3.8.78: LINQ → CollectionHelper (0 할당)
            CollectionHelper.FillWhere(situation.Enemies, _tempUnits,
                e => e.IsConscious);
            var enemies = _tempUnits;

            if (enemies.Count == 0)
                return null;

            BaseUnitEntity targetEnemy = null;

            // ★ v3.8.48: LINQ → for 루프 (anonymous type 제거, O(n²) → 최적화된 O(n²))
            // 1순위: 적 클러스터 중심 (주변 적이 많은 적)
            {
                float bestClusterScore = float.MinValue;
                for (int i = 0; i < enemies.Count; i++)
                {
                    var e = enemies[i];
                    int nearbyCount = 0;
                    for (int j = 0; j < enemies.Count; j++)
                    {
                        var other = enemies[j];
                        if (other != e && CombatCache.GetDistance(e, other) <= 4f)
                            nearbyCount++;
                    }
                    if (nearbyCount < 1) continue;
                    // nearbyCount * 10000 - HP% (클러스터 우선, 같으면 저HP 우선)
                    float score = nearbyCount * 10000f - CombatCache.GetHPPercent(e);
                    if (score > bestClusterScore)
                    {
                        var tw = new TargetWrapper(e);
                        if (CombatAPI.CanUseAbilityOn(jumpClaws, tw, out _))
                        {
                            targetEnemy = e;
                            bestClusterScore = score;
                            if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Jump Claws cluster target: {nearbyCount} nearby");
                        }
                    }
                }
            }

            // 2순위: 저HP 적 (마무리)
            if (targetEnemy == null)
            {
                float bestHP = float.MaxValue;
                for (int i = 0; i < enemies.Count; i++)
                {
                    var e = enemies[i];
                    float hp = CombatCache.GetHPPercent(e);
                    if (hp < bestHP)
                    {
                        var tw = new TargetWrapper(e);
                        if (CombatAPI.CanUseAbilityOn(jumpClaws, tw, out _))
                        {
                            targetEnemy = e;
                            bestHP = hp;
                        }
                    }
                }
            }

            // 3순위: 가장 가까운 적
            if (targetEnemy == null)
            {
                float bestDist = float.MaxValue;
                for (int i = 0; i < enemies.Count; i++)
                {
                    var e = enemies[i];
                    float dist = CombatCache.GetDistance(situation.Unit, e);
                    if (dist < bestDist)
                    {
                        var tw = new TargetWrapper(e);
                        if (CombatAPI.CanUseAbilityOn(jumpClaws, tw, out _))
                        {
                            targetEnemy = e;
                            bestDist = dist;
                        }
                    }
                }
            }

            if (targetEnemy == null)
                return null;

            remainingAP -= apCost;

            Main.Log($"[{RoleName}] ★ Mastiff Jump Claws: {targetEnemy.CharacterName} (HP={CombatCache.GetHPPercent(targetEnemy):F0}%)");

            return PlannedAction.Attack(
                jumpClaws,
                targetEnemy,
                $"Jump Claws to {targetEnemy.CharacterName}",
                apCost);
        }

        /// <summary>
        /// ★ v3.7.14: 사역마 Claws 계획 (Cyber-Eagle/Cyber-Mastiff 공통)
        /// 순수 근접 공격 - 폴백용 기본 공격
        /// </summary>
        protected PlannedAction PlanFamiliarClaws(Situation situation, ref float remainingAP)
        {
            // Eagle 또는 Mastiff만 해당
            if (situation.FamiliarType != PetType.Eagle && situation.FamiliarType != PetType.Mastiff)
                return null;

            // Claws 능력 찾기 (타입별로 다른 GUID)
            var claws = CollectionHelper.FirstOrDefault(situation.FamiliarAbilities,
                a => FamiliarAbilities.IsClawsAbility(a, situation.FamiliarType));

            if (claws == null)
                return null;

            // AP 비용 확인
            float apCost = CombatAPI.GetAbilityAPCost(claws);
            if (remainingAP < apCost)
                return null;

            // 타겟 선정: 저HP 적 > 가장 가까운 적
            // ★ v3.8.78: LINQ → CollectionHelper (0 할당)
            CollectionHelper.FillWhere(situation.Enemies, _tempUnits,
                e => e.IsConscious);
            var enemies = _tempUnits;

            if (enemies.Count == 0)
                return null;

            BaseUnitEntity targetEnemy = null;

            // ★ v3.8.48: LINQ → for 루프 (0 할당)
            // 1순위: 저HP 적 (마무리)
            {
                float bestHP = float.MaxValue;
                for (int i = 0; i < enemies.Count; i++)
                {
                    var e = enemies[i];
                    float hp = CombatCache.GetHPPercent(e);
                    if (hp < bestHP)
                    {
                        var tw = new TargetWrapper(e);
                        if (CombatAPI.CanUseAbilityOn(claws, tw, out _))
                        {
                            targetEnemy = e;
                            bestHP = hp;
                        }
                    }
                }
            }

            // 2순위: 가장 가까운 적
            if (targetEnemy == null)
            {
                float bestDist = float.MaxValue;
                for (int i = 0; i < enemies.Count; i++)
                {
                    var e = enemies[i];
                    float dist = CombatCache.GetDistance(situation.Unit, e);
                    if (dist < bestDist)
                    {
                        var tw = new TargetWrapper(e);
                        if (CombatAPI.CanUseAbilityOn(claws, tw, out _))
                        {
                            targetEnemy = e;
                            bestDist = dist;
                        }
                    }
                }
            }

            if (targetEnemy == null)
                return null;

            remainingAP -= apCost;
            string familiarName = situation.FamiliarType == PetType.Eagle ? "Eagle" : "Mastiff";

            Main.Log($"[{RoleName}] ★ {familiarName} Claws: {targetEnemy.CharacterName} (HP={CombatCache.GetHPPercent(targetEnemy):F0}%)");

            return PlannedAction.Attack(
                claws,
                targetEnemy,
                $"{familiarName} Claws to {targetEnemy.CharacterName}",
                apCost);
        }

        /// <summary>
        /// ★ v3.7.01: 사역마 Screen 계획 (Cyber-Eagle)
        /// 아군 보호/지원
        /// </summary>
        protected PlannedAction PlanFamiliarScreen(Situation situation, ref float remainingAP)
        {
            // Cyber-Eagle만 해당
            if (situation.FamiliarType != PetType.Eagle)
                return null;

            // Screen 능력 찾기
            var screen = CollectionHelper.FirstOrDefault(situation.FamiliarAbilities,
                a => FamiliarAbilities.IsScreenAbility(a));

            if (screen == null)
                return null;

            // AP 비용 확인
            float apCost = CombatAPI.GetAbilityAPCost(screen);
            if (remainingAP < apCost)
                return null;

            // ★ v3.8.48: LINQ → CollectionHelper (0 할당, O(n))
            // 보호할 아군 찾기 (HP 낮거나 위협받는 아군)
            var allyToScreen = CollectionHelper.MinByWhere(situation.Allies,
                a => a.IsConscious && !FamiliarAPI.IsFamiliar(a) && a != situation.Unit,
                a => CombatCache.GetHPPercent(a));

            if (allyToScreen == null)
                return null;

            // HP가 60% 이상이면 스킵
            float allyHP = CombatCache.GetHPPercent(allyToScreen);
            if (allyHP > 60f)
                return null;

            // 타겟 가능 여부 확인
            var targetWrapper = new TargetWrapper(allyToScreen);
            string reason;
            if (!CombatAPI.CanUseAbilityOn(screen, targetWrapper, out reason))
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Eagle Screen blocked: {reason}");
                return null;
            }

            remainingAP -= apCost;

            Main.Log($"[{RoleName}] ★ Eagle Screen: {allyToScreen.CharacterName} (HP={allyHP:F0}%)");

            return PlannedAction.Buff(
                screen,
                allyToScreen,
                $"Eagle Screen {allyToScreen.CharacterName}",
                apCost);
        }

        /// <summary>
        /// ★ v3.7.00: 버프 시전 시 사역마 Keystone 우선 검토
        /// 직접 아군에게 버프하는 대신 사역마에게 버프 → 확산
        /// </summary>
        protected PlannedAction PlanBuffWithFamiliarCheck(
            Situation situation,
            AbilityData buff,
            BaseUnitEntity normalTarget,
            ref float remainingAP)
        {
            // 사역마 Keystone 가능하면 그쪽 우선
            if (situation.HasFamiliar)
            {
                var keystoneAction = PlanFamiliarKeystone(situation, buff, ref remainingAP);
                if (keystoneAction != null)
                    return keystoneAction;
            }

            // 일반 버프 폴백
            float apCost = CombatAPI.GetAbilityAPCost(buff);
            if (remainingAP < apCost)
                return null;

            var target = new TargetWrapper(normalTarget);
            string reason;
            if (!CombatAPI.CanUseAbilityOn(buff, target, out reason))
                return null;

            remainingAP -= apCost;
            return PlannedAction.Buff(buff, normalTarget, $"Buff on {normalTarget.CharacterName}", apCost);
        }

        /// <summary>
        /// ★ v3.7.12: Priority Signal 계획 (Servo-Skull)
        /// Servo-Skull 방어력 상승 + 적 주의 분산
        /// </summary>
        protected PlannedAction PlanFamiliarPrioritySignal(Situation situation, ref float remainingAP)
        {
            if (situation.FamiliarType != PetType.ServoskullSwarm)
                return null;

            var signal = CollectionHelper.FirstOrDefault(situation.FamiliarAbilities,
                a => FamiliarAbilities.IsPrioritySignal(a));

            if (signal == null) return null;

            float apCost = CombatAPI.GetAbilityAPCost(signal);
            if (remainingAP < apCost) return null;

            // 이미 버프 활성화 확인
            if (AllyStateCache.HasBuff(situation.Familiar, signal)) return null;

            // Self-target이므로 Unit에게 시전
            var selfTarget = new TargetWrapper(situation.Unit);
            string reason;
            if (!CombatAPI.CanUseAbilityOn(signal, selfTarget, out reason))
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Priority Signal blocked: {reason}");
                return null;
            }

            remainingAP -= apCost;
            Main.Log($"[{RoleName}] ★ Servo-Skull Priority Signal");

            return PlannedAction.Buff(signal, situation.Unit,
                "Priority Signal (Servo-Skull defense)", apCost);
        }

        /// <summary>
        /// ★ v3.7.12: Vitality Signal 계획 (Servo-Skull)
        /// 4타일 범위 AoE 힐 - 개별 힐보다 효율적
        /// </summary>
        protected PlannedAction PlanFamiliarVitalitySignal(Situation situation, ref float remainingAP)
        {
            if (situation.FamiliarType != PetType.ServoskullSwarm)
                return null;

            var signal = CollectionHelper.FirstOrDefault(situation.FamiliarAbilities,
                a => FamiliarAbilities.IsVitalitySignal(a));

            if (signal == null) return null;

            float apCost = CombatAPI.GetAbilityAPCost(signal);
            if (remainingAP < apCost) return null;

            // ★ v3.8.48: LINQ → CollectionHelper (0 할당)
            // 범위 내 부상 아군 수 계산 (4타일 = 약 5.4m)
            int woundedInRange = situation.Familiar != null
                ? CollectionHelper.CountWhere(situation.Allies, a =>
                    a.IsConscious && CombatCache.GetHPPercent(a) < 70f &&
                    CombatCache.GetDistanceInTiles(situation.Familiar, a) <= 4f)
                : 0;

            // 2명 이상 부상 아군이 범위 내 있어야 의미
            if (woundedInRange < 2)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Vitality Signal: Only {woundedInRange} wounded in range (need 2+)");
                return null;
            }

            var selfTarget = new TargetWrapper(situation.Unit);
            string reason;
            if (!CombatAPI.CanUseAbilityOn(signal, selfTarget, out reason))
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Vitality Signal blocked: {reason}");
                return null;
            }

            remainingAP -= apCost;
            Main.Log($"[{RoleName}] ★ Servo-Skull Vitality Signal ({woundedInRange} wounded in range)");

            return PlannedAction.Buff(signal, situation.Unit,
                $"Vitality Signal (AoE heal, {woundedInRange} wounded)", apCost);
        }

        /// <summary>
        /// ★ v3.7.12: Hex 계획 (Psyber-Raven)
        /// 적 디버프 - Warp Relay 확산 가능
        /// </summary>
        protected PlannedAction PlanFamiliarHex(Situation situation, ref float remainingAP)
        {
            if (situation.FamiliarType != PetType.Raven)
                return null;

            var hex = CollectionHelper.FirstOrDefault(situation.FamiliarAbilities,
                a => FamiliarAbilities.IsHexAbility(a));

            if (hex == null) return null;

            float apCost = CombatAPI.GetAbilityAPCost(hex);
            if (remainingAP < apCost) return null;

            // ★ v3.8.51: 레이븐 범위 내 적만 타겟 가능
            // Hex는 레이븐 능력이므로 레이븐 근처 적에게만 효과적
            var raven = situation.Familiar;
            if (raven == null)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Hex: No raven available");
                return null;
            }

            // 레이븐 효과 범위 (EFFECT_RADIUS_TILES) × 2 이내 적만 후보
            float maxHexRange = CombatAPI.TilesToMeters(FamiliarPositioner.EFFECT_RADIUS_TILES * 2f);
            BaseUnitEntity target = null;
            float bestHP = 0f;
            for (int i = 0; i < situation.Enemies.Count; i++)
            {
                var enemy = situation.Enemies[i];
                if (!enemy.IsConscious) continue;

                float distToRaven = CombatCache.GetDistance(raven, enemy);
                if (distToRaven > maxHexRange) continue;

                float hp = (float)(enemy.Health?.MaxHitPoints ?? 0);
                if (hp > bestHP)
                {
                    bestHP = hp;
                    target = enemy;
                }
            }

            if (target == null)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Hex: No enemies within Raven range ({maxHexRange:F1}m)");
                return null;
            }

            var targetWrapper = new TargetWrapper(target);
            string reason;
            if (!CombatAPI.CanUseAbilityOn(hex, targetWrapper, out reason))
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Hex blocked: {reason}");
                return null;
            }

            remainingAP -= apCost;
            Main.Log($"[{RoleName}] ★ Raven Hex: {target.CharacterName} (within Raven range)");

            return PlannedAction.Attack(hex, target,
                $"Hex on {target.CharacterName}", apCost);
        }

        /// <summary>
        /// ★ v3.7.12: Cycle 계획 (Psyber-Raven)
        /// Warp Relay로 확산된 사이킹 재시전
        /// </summary>
        protected PlannedAction PlanFamiliarCycle(Situation situation, ref float remainingAP,
            bool hasUsedWarpRelayThisTurn)
        {
            // Warp Relay를 이번 턴에 사용하지 않았으면 무의미
            if (!hasUsedWarpRelayThisTurn)
                return null;

            if (situation.FamiliarType != PetType.Raven)
                return null;

            var cycle = CollectionHelper.FirstOrDefault(situation.FamiliarAbilities,
                a => FamiliarAbilities.IsCycleAbility(a));

            if (cycle == null) return null;

            float apCost = CombatAPI.GetAbilityAPCost(cycle);
            if (remainingAP < apCost) return null;

            var selfTarget = new TargetWrapper(situation.Unit);
            string reason;
            if (!CombatAPI.CanUseAbilityOn(cycle, selfTarget, out reason))
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Cycle blocked: {reason}");
                return null;
            }

            remainingAP -= apCost;
            Main.Log($"[{RoleName}] ★ Raven Complete the Cycle");

            return PlannedAction.Buff(cycle, situation.Unit,
                "Complete the Cycle (re-cast relay)", apCost);
        }

        /// <summary>
        /// ★ v3.7.12: Fast 계획 (Cyber-Mastiff)
        /// 이동/속도 버프 - Apprehend 전 사용
        /// </summary>
        protected PlannedAction PlanFamiliarFast(Situation situation, ref float remainingAP)
        {
            if (situation.FamiliarType != PetType.Mastiff)
                return null;

            var fast = CollectionHelper.FirstOrDefault(situation.FamiliarAbilities,
                a => FamiliarAbilities.IsFastAbility(a));

            if (fast == null) return null;

            float apCost = CombatAPI.GetAbilityAPCost(fast);
            if (remainingAP < apCost) return null;

            // 이미 버프 활성화 확인
            if (AllyStateCache.HasBuff(situation.Familiar, fast)) return null;

            var selfTarget = new TargetWrapper(situation.Unit);
            string reason;
            if (!CombatAPI.CanUseAbilityOn(fast, selfTarget, out reason))
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Fast blocked: {reason}");
                return null;
            }

            remainingAP -= apCost;
            Main.Log($"[{RoleName}] ★ Mastiff Fast (mobility buff)");

            return PlannedAction.Buff(fast, situation.Unit,
                "Mastiff Fast (mobility)", apCost);
        }

        /// <summary>
        /// ★ v3.7.12: Roam 계획 (Cyber-Mastiff)
        /// 자동 공격 모드 - Apprehend 대상 없을 때
        /// </summary>
        protected PlannedAction PlanFamiliarRoam(Situation situation, ref float remainingAP)
        {
            if (situation.FamiliarType != PetType.Mastiff)
                return null;

            var roam = CollectionHelper.FirstOrDefault(situation.FamiliarAbilities,
                a => FamiliarAbilities.IsRoamAbility(a));

            if (roam == null) return null;

            float apCost = CombatAPI.GetAbilityAPCost(roam);
            if (remainingAP < apCost) return null;

            var selfTarget = new TargetWrapper(situation.Unit);
            string reason;
            if (!CombatAPI.CanUseAbilityOn(roam, selfTarget, out reason))
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{RoleName}] Roam blocked: {reason}");
                return null;
            }

            remainingAP -= apCost;
            Main.Log($"[{RoleName}] ★ Mastiff Roam (auto-attack mode)");

            return PlannedAction.Buff(roam, situation.Unit,
                "Mastiff Roam (autonomous)", apCost);
        }

        #endregion
    }
}
