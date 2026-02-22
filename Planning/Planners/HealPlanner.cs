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

            // ★ v3.12.2: ScoreHeal 기반 최적 힐 선택 (기존 first-available 대체)
            var selfTarget = new TargetWrapper(situation.Unit);
            AbilityData bestHeal = null;
            float bestScore = float.MinValue;
            float bestCost = 0f;

            for (int i = 0; i < situation.AvailableHeals.Count; i++)
            {
                var heal = situation.AvailableHeals[i];
                float cost = CombatAPI.GetAbilityAPCost(heal);
                if (cost > remainingAP) continue;

                string reason;
                if (!CombatAPI.CanUseAbilityOn(heal, selfTarget, out reason)) continue;

                float score = UtilityScorer.ScoreHeal(heal, situation.Unit, situation);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestHeal = heal;
                    bestCost = cost;
                }
            }

            if (bestHeal == null) return null;

            remainingAP -= bestCost;
            Main.Log($"[{roleName}] Emergency heal: {bestHeal.Name} (score={bestScore:F1}, HP={situation.HPPercent:F0}%)");
            return PlannedAction.Heal(bestHeal, situation.Unit,
                $"Emergency heal (HP={situation.HPPercent:F0}%)", bestCost);
        }

        /// <summary>
        /// 아군 힐
        /// ★ v3.5.10: 힐 예약 시스템 통합 (중복 힐 방지)
        /// </summary>
        public static PlannedAction PlanAllyHeal(Situation situation, BaseUnitEntity ally, ref float remainingAP, string roleName)
        {
            if (situation.AvailableHeals.Count == 0) return null;
            if (ally == null) return null;

            // ★ v3.5.10: 이미 다른 Support가 힐 예약한 대상은 스킵
            if (TeamBlackboard.Instance.IsHealReserved(ally))
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] Heal skip - {ally.CharacterName} already reserved for heal");
                return null;
            }

            // ★ v3.12.2: ScoreHeal 기반 최적 힐 선택 (기존 first-available 대체)
            var targetWrapper = new TargetWrapper(ally);
            AbilityData bestHeal = null;
            float bestScore = float.MinValue;
            float bestCost = 0f;

            for (int i = 0; i < situation.AvailableHeals.Count; i++)
            {
                var heal = situation.AvailableHeals[i];
                float cost = CombatAPI.GetAbilityAPCost(heal);
                if (cost > remainingAP) continue;

                string reason;
                if (!CombatAPI.CanUseAbilityOn(heal, targetWrapper, out reason)) continue;

                float score = UtilityScorer.ScoreHeal(heal, ally, situation);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestHeal = heal;
                    bestCost = cost;
                }
            }

            if (bestHeal == null) return null;

            // ★ v3.5.10: 힐 대상 예약
            TeamBlackboard.Instance.ReserveHeal(ally);

            remainingAP -= bestCost;
            Main.Log($"[{roleName}] Heal ally: {bestHeal.Name} -> {ally.CharacterName} (score={bestScore:F1})");
            return PlannedAction.Heal(bestHeal, ally, $"Heal {ally.CharacterName}", bestCost);
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
        /// ★ v3.5.10: 이미 힐 예약된 아군 제외
        /// </summary>
        public static BaseUnitEntity FindWoundedAlly(Situation situation, float threshold)
        {
            var allTargets = new List<BaseUnitEntity>();
            // ★ v3.18.4: CombatantAllies 사용 (사역마 제외)
            allTargets.AddRange(situation.CombatantAllies.Where(a => a != null && !a.LifeState.IsDead));

            // 본인도 힐 대상에 포함
            if (!allTargets.Contains(situation.Unit))
                allTargets.Add(situation.Unit);

            // ★ v3.5.10: 이미 힐 예약된 아군 제외 (중복 힐 방지)
            allTargets = allTargets
                .Where(a => !TeamBlackboard.Instance.IsHealReserved(a))
                .ToList();

            if (allTargets.Count == 0)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[HealPlanner] No heal targets available (all reserved or healthy)");
                return null;
            }

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
                // ★ v3.18.4: CombatantAllies 사용 (사역마 제외)
                var candidates = situation.CombatantAllies
                    .Where(a => a != null && !a.LifeState.IsDead)
                    .Where(a => !AllyStateCache.HasBuff(a, buff))
                    .ToList();

                // 본인도 후보에 추가 (버프 없으면)
                if (!AllyStateCache.HasBuff(situation.Unit, buff) && !candidates.Contains(situation.Unit))
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
