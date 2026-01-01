using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.GameInterface;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// ★ v3.0.47: 힐/재장전 관련 계획 담당
    /// </summary>
    public static class HealPlanner
    {
        /// <summary>
        /// 긴급 자기 힐
        /// </summary>
        public static PlannedAction PlanEmergencyHeal(Situation situation, ref float remainingAP, string roleName)
        {
            if (situation.AvailableHeals.Count == 0) return null;
            if (situation.HPPercent >= 30f) return null;
            if (situation.HasHealedThisTurn) return null;

            foreach (var heal in situation.AvailableHeals)
            {
                float cost = CombatAPI.GetAbilityAPCost(heal);
                if (cost > remainingAP) continue;

                var target = new TargetWrapper(situation.Unit);
                string reason;
                if (CombatAPI.CanUseAbilityOn(heal, target, out reason))
                {
                    remainingAP -= cost;
                    return PlannedAction.Heal(heal, situation.Unit,
                        $"Emergency heal (HP={situation.HPPercent:F0}%)", cost);
                }
            }

            return null;
        }

        /// <summary>
        /// 아군 힐
        /// </summary>
        public static PlannedAction PlanAllyHeal(Situation situation, BaseUnitEntity ally, ref float remainingAP, string roleName)
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
                    Main.Log($"[{roleName}] Heal ally: {heal.Name} -> {ally.CharacterName}");
                    return PlannedAction.Heal(heal, ally, $"Heal {ally.CharacterName}", cost);
                }
            }

            return null;
        }

        /// <summary>
        /// 재장전
        /// </summary>
        public static PlannedAction PlanReload(Situation situation, ref float remainingAP, string roleName)
        {
            var reload = situation.ReloadAbility;
            if (reload == null) return null;
            if (!situation.NeedsReload) return null;
            if (situation.HasReloadedThisTurn) return null;

            float cost = CombatAPI.GetAbilityAPCost(reload);
            if (cost > remainingAP) return null;

            var target = new TargetWrapper(situation.Unit);
            string reason;
            if (CombatAPI.CanUseAbilityOn(reload, target, out reason))
            {
                remainingAP -= cost;
                return PlannedAction.Reload(reload, situation.Unit, cost);
            }

            return null;
        }

        /// <summary>
        /// ★ v3.1.21: 부상당한 아군 찾기 - TargetScorer 기반
        /// Role, 위험도 등 복합 요소 고려
        /// </summary>
        public static BaseUnitEntity FindWoundedAlly(Situation situation, float threshold)
        {
            var allTargets = new List<BaseUnitEntity>();
            allTargets.AddRange(situation.Allies.Where(a => a != null && !a.LifeState.IsDead));

            // 본인도 힐 대상에 포함
            if (!allTargets.Contains(situation.Unit))
                allTargets.Add(situation.Unit);

            // ★ v3.1.21: TargetScorer 사용 (Role 우선순위 + 위험도 고려)
            return TargetScorer.SelectBestAllyForHealing(allTargets, situation, threshold);
        }

        /// <summary>
        /// 아군 버프 (Support 전용)
        /// ★ v3.1.21: TargetScorer 기반 버프 대상 선택
        /// </summary>
        public static PlannedAction PlanAllyBuff(Situation situation, ref float remainingAP, string roleName)
        {
            foreach (var buff in situation.AvailableBuffs)
            {
                if (buff.Blueprint?.CanTargetFriends != true) continue;

                float cost = CombatAPI.GetAbilityAPCost(buff);
                if (cost > remainingAP) continue;

                // ★ v3.1.21: TargetScorer로 최적 버프 대상 선택
                // 이미 버프가 있는 아군 제외
                var candidates = situation.Allies
                    .Where(a => a != null && !a.LifeState.IsDead)
                    .Where(a => !CombatAPI.HasActiveBuff(a, buff))
                    .ToList();

                // 본인도 후보에 추가 (버프 없으면)
                if (!CombatAPI.HasActiveBuff(situation.Unit, buff) && !candidates.Contains(situation.Unit))
                    candidates.Add(situation.Unit);

                var bestTarget = TargetScorer.SelectBestAllyForBuff(candidates, situation);
                if (bestTarget == null) continue;

                var targetWrapper = new TargetWrapper(bestTarget);
                string reason;
                if (CombatAPI.CanUseAbilityOn(buff, targetWrapper, out reason))
                {
                    remainingAP -= cost;
                    Main.Log($"[{roleName}] Buff ally: {buff.Name} -> {bestTarget.CharacterName}");
                    return PlannedAction.Buff(buff, bestTarget, $"Buff {bestTarget.CharacterName}", cost);
                }
            }

            return null;
        }
    }
}
