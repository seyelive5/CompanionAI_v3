using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// ★ v3.0.47: 공격 관련 계획 담당
    /// - 일반 공격, 마무리, 특수 능력, 타겟 선택
    /// </summary>
    public static class AttackPlanner
    {
        /// <summary>
        /// 공격 계획
        /// </summary>
        public static PlannedAction PlanAttack(Situation situation, ref float remainingAP,
            string roleName, BaseUnitEntity preferTarget = null,
            HashSet<string> excludeTargetIds = null, HashSet<string> excludeAbilityGuids = null)
        {
            var candidateTargets = new List<BaseUnitEntity>();

            if (preferTarget != null && !IsExcluded(preferTarget, excludeTargetIds))
                candidateTargets.Add(preferTarget);

            if (situation.BestTarget != null && !candidateTargets.Contains(situation.BestTarget) && !IsExcluded(situation.BestTarget, excludeTargetIds))
                candidateTargets.Add(situation.BestTarget);

            foreach (var hittable in situation.HittableEnemies)
            {
                if (hittable != null && !candidateTargets.Contains(hittable) && !IsExcluded(hittable, excludeTargetIds))
                    candidateTargets.Add(hittable);
            }

            if (situation.NearestEnemy != null && !candidateTargets.Contains(situation.NearestEnemy) && !IsExcluded(situation.NearestEnemy, excludeTargetIds))
                candidateTargets.Add(situation.NearestEnemy);

            if (candidateTargets.Count == 0) return null;

            foreach (var target in candidateTargets)
            {
                var attack = SelectBestAttack(situation, target, excludeAbilityGuids);
                if (attack == null) continue;

                float cost = CombatAPI.GetAbilityAPCost(attack);
                if (cost > remainingAP) continue;

                var targetWrapper = new TargetWrapper(target);
                string reason;
                if (CombatAPI.CanUseAbilityOn(attack, targetWrapper, out reason))
                {
                    remainingAP -= cost;
                    Main.LogDebug($"[{roleName}] Attack: {attack.Name} -> {target.CharacterName}");
                    return PlannedAction.Attack(attack, target, $"Attack with {attack.Name}", cost);
                }
            }

            return null;
        }

        /// <summary>
        /// 최적 공격 선택 (Utility 스코어링 기반)
        /// </summary>
        public static AbilityData SelectBestAttack(Situation situation, BaseUnitEntity target, HashSet<string> excludeAbilityGuids = null)
        {
            if (situation.AvailableAttacks.Count == 0) return null;

            var targetWrapper = new TargetWrapper(target);
            var rangePreference = situation.RangePreference;

            var filteredAttacks = situation.AvailableAttacks
                .Where(a => !AbilityDatabase.IsReload(a))
                .Where(a => !AbilityDatabase.IsPostFirstAction(a))
                .Where(a => !AbilityDatabase.IsTurnEnding(a))
                .Where(a => !AbilityDatabase.IsFinisher(a))
                .Where(a => !AbilityDatabase.IsDangerousAoE(a))
                .Where(a => !IsAbilityExcluded(a, excludeAbilityGuids))
                .ToList();

            if (rangePreference == RangePreference.PreferRanged)
            {
                var rangedOnly = filteredAttacks.Where(a => !a.IsMelee).ToList();
                if (rangedOnly.Count > 0)
                    filteredAttacks = rangedOnly;
            }
            else if (rangePreference == RangePreference.PreferMelee)
            {
                var meleeOnly = filteredAttacks.Where(a => a.IsMelee).ToList();
                if (meleeOnly.Count > 0)
                    filteredAttacks = meleeOnly;
            }

            var scoredAttacks = filteredAttacks
                .Select(a => new { Attack = a, Score = UtilityScorer.ScoreAttack(a, target, situation) })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ToList();

            foreach (var scored in scoredAttacks)
            {
                string reason;
                if (CombatAPI.CanUseAbilityOn(scored.Attack, targetWrapper, out reason))
                {
                    return scored.Attack;
                }
            }

            return situation.PrimaryAttack;
        }

        /// <summary>
        /// 이동 후 공격 계획
        /// </summary>
        public static PlannedAction PlanPostMoveAttack(Situation situation, BaseUnitEntity target, ref float remainingAP, string roleName)
        {
            if (target == null) return null;

            var attack = SelectBestAttack(situation, target);
            if (attack == null)
            {
                if (situation.AvailableAttacks.Count > 0)
                {
                    var rangePreference = situation.RangePreference;
                    if (rangePreference == RangePreference.PreferRanged)
                    {
                        attack = situation.AvailableAttacks.FirstOrDefault(a => !a.IsMelee);
                    }
                    else if (rangePreference == RangePreference.PreferMelee)
                    {
                        attack = situation.AvailableAttacks.FirstOrDefault(a => a.IsMelee);
                    }

                    if (attack == null)
                    {
                        attack = situation.AvailableAttacks.FirstOrDefault();
                    }
                }

                if (attack == null)
                {
                    attack = CombatAPI.FindAnyAttackAbility(situation.Unit, situation.RangePreference);
                }
            }

            if (attack == null) return null;

            float cost = CombatAPI.GetAbilityAPCost(attack);
            if (cost > remainingAP) return null;

            remainingAP -= cost;
            Main.LogDebug($"[{roleName}] PostMoveAttack: {attack.Name} -> {target.CharacterName}");
            return PlannedAction.Attack(attack, target, $"Post-move attack with {attack.Name}", cost);
        }

        /// <summary>
        /// 마무리 스킬 계획 (DPS 전용)
        /// </summary>
        public static PlannedAction PlanFinisher(Situation situation, BaseUnitEntity target, ref float remainingAP, string roleName)
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
                        Main.Log($"[{roleName}] Finisher (KILL): {finisher.Name} -> {target.CharacterName} (HP={hp})");
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
                    Main.Log($"[{roleName}] Finisher: {finisher.Name} -> {target.CharacterName}");
                    return PlannedAction.Attack(finisher, target, $"Finisher on {target.CharacterName}", cost);
                }
            }

            return null;
        }

        /// <summary>
        /// 특수 능력 계획 (DoT 강화, 연쇄 효과)
        /// </summary>
        public static PlannedAction PlanSpecialAbility(Situation situation, ref float remainingAP, string roleName)
        {
            if (situation.AvailableSpecialAbilities == null || situation.AvailableSpecialAbilities.Count == 0)
                return null;

            if (situation.BestTarget == null)
                return null;

            var target = situation.BestTarget;
            var enemies = situation.Enemies;
            var targetWrapper = new TargetWrapper(target);
            float currentAP = remainingAP;

            var scoredAbilities = situation.AvailableSpecialAbilities
                .Select(a => new
                {
                    Ability = a,
                    Score = SpecialAbilityHandler.GetSpecialAbilityEffectivenessScore(a, target, enemies),
                    Cost = CombatAPI.GetAbilityAPCost(a)
                })
                .Where(x => x.Cost <= currentAP && x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ToList();

            foreach (var entry in scoredAbilities)
            {
                var ability = entry.Ability;

                if (!SpecialAbilityHandler.CanUseSpecialAbilityEffectively(ability, target, enemies))
                    continue;

                string reason;
                if (CombatAPI.CanUseAbilityOn(ability, targetWrapper, out reason))
                {
                    remainingAP -= entry.Cost;

                    string abilityType = AbilityDatabase.IsDOTIntensify(ability) ? "DoT Intensify" :
                                        AbilityDatabase.IsChainEffect(ability) ? "Chain Effect" : "Special";

                    Main.Log($"[{roleName}] {abilityType}: {ability.Name} -> {target.CharacterName}");
                    return PlannedAction.Attack(ability, target, $"{abilityType} on {target.CharacterName}", entry.Cost);
                }
            }

            return null;
        }

        /// <summary>
        /// 안전한 원거리 공격 (Support 전용)
        /// ★ v3.0.49: Weapon != null 조건 제거 - 사이킥/수류탄 능력 허용
        /// ★ v3.0.50: AoE 아군 피해 체크 추가
        /// </summary>
        public static PlannedAction PlanSafeRangedAttack(Situation situation, ref float remainingAP,
            string roleName, HashSet<string> excludeTargetIds = null, HashSet<string> excludeAbilityGuids = null)
        {
            // ★ v3.0.49: !a.IsMelee만 체크 - 사이킥 능력(Weapon=null)도 원거리 공격으로 허용
            var rangedAttacks = situation.AvailableAttacks
                .Where(a => !a.IsMelee)
                .Where(a => !AbilityDatabase.IsDangerousAoE(a))
                .Where(a => !IsAbilityExcluded(a, excludeAbilityGuids))
                .OrderBy(a => CombatAPI.GetAbilityAPCost(a))
                .ToList();

            if (rangedAttacks.Count == 0) return null;

            var candidateTargets = new List<BaseUnitEntity>();

            if (situation.BestTarget != null && !IsExcluded(situation.BestTarget, excludeTargetIds))
                candidateTargets.Add(situation.BestTarget);

            foreach (var hittable in situation.HittableEnemies)
            {
                if (hittable != null && !candidateTargets.Contains(hittable) && !IsExcluded(hittable, excludeTargetIds))
                    candidateTargets.Add(hittable);
            }

            if (situation.NearestEnemy != null && !candidateTargets.Contains(situation.NearestEnemy) && !IsExcluded(situation.NearestEnemy, excludeTargetIds))
                candidateTargets.Add(situation.NearestEnemy);

            if (candidateTargets.Count == 0) return null;

            foreach (var target in candidateTargets)
            {
                var targetWrapper = new TargetWrapper(target);

                foreach (var attack in rangedAttacks)
                {
                    float cost = CombatAPI.GetAbilityAPCost(attack);
                    if (cost > remainingAP) continue;

                    // ★ v3.0.50: AoE 아군 피해 체크 (CanTargetFriends=true인 능력)
                    if (attack.Blueprint?.CanTargetFriends == true)
                    {
                        bool allyNearTarget = situation.Allies.Any(ally =>
                            ally != null && !ally.LifeState.IsDead &&
                            CombatAPI.GetDistance(ally, target) < 4f);  // 4m 이내 아군 있으면 스킵

                        if (allyNearTarget)
                        {
                            Main.LogDebug($"[{roleName}] Skipping {attack.Name} - ally near target {target.CharacterName}");
                            continue;
                        }
                    }

                    string reason;
                    if (CombatAPI.CanUseAbilityOn(attack, targetWrapper, out reason))
                    {
                        remainingAP -= cost;
                        Main.Log($"[{roleName}] Safe attack: {attack.Name} -> {target.CharacterName}");
                        return PlannedAction.Attack(attack, target, $"Safe attack on {target.CharacterName}", cost);
                    }
                }
            }

            return null;
        }

        #region Target Selection

        /// <summary>
        /// 낮은 HP 적 찾기 (1타 킬 우선)
        /// </summary>
        public static BaseUnitEntity FindLowHPEnemy(Situation situation, float threshold)
        {
            var primaryAttack = situation.PrimaryAttack;

            if (primaryAttack != null)
            {
                var oneHitKill = situation.HittableEnemies
                    .Where(e => e != null && !e.LifeState.IsDead)
                    .Where(e => CombatAPI.CanKillInOneHit(primaryAttack, e))
                    .OrderBy(e => CombatAPI.GetActualHP(e))
                    .FirstOrDefault();

                if (oneHitKill != null) return oneHitKill;
            }

            var hittableLowHP = situation.HittableEnemies
                .Where(e => e != null && !e.LifeState.IsDead)
                .Where(e => CombatAPI.GetHPPercent(e) <= threshold)
                .OrderBy(e => CombatAPI.GetActualHP(e))
                .FirstOrDefault();

            return hittableLowHP ?? situation.Enemies
                .Where(e => e != null && !e.LifeState.IsDead)
                .Where(e => CombatAPI.GetHPPercent(e) <= threshold)
                .OrderBy(e => CombatAPI.GetActualHP(e))
                .FirstOrDefault();
        }

        /// <summary>
        /// 가장 약한 적 찾기 (Utility 스코어링 기반)
        /// </summary>
        public static BaseUnitEntity FindWeakestEnemy(Situation situation, HashSet<string> excludeTargetIds = null)
        {
            var candidates = situation.HittableEnemies
                .Where(e => e != null && !e.LifeState.IsDead)
                .Where(e => !IsExcluded(e, excludeTargetIds))
                .ToList();

            if (candidates.Count > 0)
            {
                var best = UtilityScorer.SelectBestTarget(candidates, situation);
                if (best != null) return best;
            }

            var allCandidates = situation.Enemies
                .Where(e => e != null && !e.LifeState.IsDead)
                .Where(e => !IsExcluded(e, excludeTargetIds))
                .ToList();

            return UtilityScorer.SelectBestTarget(allCandidates, situation);
        }

        #endregion

        #region Helper Methods

        public static bool IsExcluded(BaseUnitEntity target, HashSet<string> excludeTargetIds)
        {
            if (target == null || excludeTargetIds == null) return false;
            return excludeTargetIds.Contains(target.UniqueId);
        }

        public static bool IsAbilityExcluded(AbilityData ability, HashSet<string> excludeAbilityGuids)
        {
            if (ability == null || excludeAbilityGuids == null || excludeAbilityGuids.Count == 0)
                return false;

            var guid = ability.Blueprint?.AssetGuid?.ToString();
            if (string.IsNullOrEmpty(guid)) return false;

            return excludeAbilityGuids.Contains(guid);
        }

        #endregion
    }
}
