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
        /// 부상당한 아군 찾기
        /// </summary>
        public static BaseUnitEntity FindWoundedAlly(Situation situation, float threshold)
        {
            var allTargets = new List<BaseUnitEntity> { situation.Unit };
            allTargets.AddRange(situation.Allies.Where(a => a != null && !a.LifeState.IsDead));

            return allTargets
                .Where(a => CombatAPI.GetHPPercent(a) < threshold)
                .OrderBy(a => CombatAPI.GetHPPercent(a))
                .FirstOrDefault();
        }

        /// <summary>
        /// 아군 버프 (Support 전용)
        /// </summary>
        public static PlannedAction PlanAllyBuff(Situation situation, ref float remainingAP, string roleName)
        {
            // 버프 대상 우선순위: Tank > DPS > 본인 > 기타
            var prioritizedTargets = new List<BaseUnitEntity>();

            foreach (var ally in situation.Allies.Where(a => a != null && !a.LifeState.IsDead))
            {
                var settings = Settings.ModSettings.Instance?.GetOrCreateSettings(ally.UniqueId, ally.CharacterName);
                if (settings?.Role == Settings.AIRole.Tank)
                    prioritizedTargets.Add(ally);
            }

            foreach (var ally in situation.Allies.Where(a => a != null && !a.LifeState.IsDead))
            {
                var settings = Settings.ModSettings.Instance?.GetOrCreateSettings(ally.UniqueId, ally.CharacterName);
                if (settings?.Role == Settings.AIRole.DPS && !prioritizedTargets.Contains(ally))
                    prioritizedTargets.Add(ally);
            }

            prioritizedTargets.Add(situation.Unit);

            foreach (var ally in situation.Allies.Where(a => a != null && !a.LifeState.IsDead))
            {
                if (!prioritizedTargets.Contains(ally))
                    prioritizedTargets.Add(ally);
            }

            foreach (var buff in situation.AvailableBuffs)
            {
                if (buff.Blueprint?.CanTargetFriends != true) continue;

                float cost = CombatAPI.GetAbilityAPCost(buff);
                if (cost > remainingAP) continue;

                foreach (var target in prioritizedTargets)
                {
                    if (CombatAPI.HasActiveBuff(target, buff)) continue;

                    var targetWrapper = new TargetWrapper(target);
                    string reason;
                    if (CombatAPI.CanUseAbilityOn(buff, targetWrapper, out reason))
                    {
                        remainingAP -= cost;
                        Main.Log($"[{roleName}] Buff ally: {buff.Name} -> {target.CharacterName}");
                        return PlannedAction.Buff(buff, target, $"Buff {target.CharacterName}", cost);
                    }
                }
            }

            return null;
        }
    }
}
