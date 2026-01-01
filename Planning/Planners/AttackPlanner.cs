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
        /// ★ v3.1.23: Self-Targeted AOE (Bladedance 등) 특수 처리 추가
        /// ★ v3.1.24: moveDestination 파라미터 추가 - 이동 후 위치에서 최근접 적 재계산
        /// </summary>
        public static PlannedAction PlanPostMoveAttack(Situation situation, BaseUnitEntity target, ref float remainingAP, string roleName, UnityEngine.Vector3? moveDestination = null)
        {
            // ★ v3.1.24: 이동 목적지가 있으면 해당 위치에서 최근접 적 재계산
            var effectiveTarget = target;
            if (moveDestination.HasValue && situation.Enemies != null)
            {
                effectiveTarget = FindNearestEnemyFromPosition(moveDestination.Value, situation.Enemies);
                if (effectiveTarget == null)
                {
                    Main.LogDebug($"[{roleName}] PlanPostMoveAttack: No enemy reachable from destination");
                    return null;
                }

                if (effectiveTarget != target)
                {
                    Main.LogDebug($"[{roleName}] PlanPostMoveAttack: Target changed from {target?.CharacterName} to {effectiveTarget.CharacterName} based on move destination");
                }
            }

            if (effectiveTarget == null) return null;

            var attack = SelectBestAttack(situation, effectiveTarget);
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

            // ★ v3.1.23: Self-Targeted AOE 공격 처리 (Bladedance 등)
            // Range=Personal, CanTargetSelf인 DangerousAoE → 적을 타겟으로 할 수 없음
            if (CombatAPI.IsSelfTargetedAoEAttack(attack))
            {
                return PlanSelfTargetedAoEAttack(situation, attack, ref remainingAP, roleName);
            }

            // ★ v3.1.24: 이동 후 위치에서 공격 범위 검증
            if (moveDestination.HasValue)
            {
                float distFromDest = UnityEngine.Vector3.Distance(moveDestination.Value, effectiveTarget.Position);
                float attackRange = CombatAPI.GetAbilityRange(attack);
                if (distFromDest > attackRange)
                {
                    Main.LogDebug($"[{roleName}] PostMoveAttack: {attack.Name} out of range ({distFromDest:F1}m > {attackRange:F1}m)");
                    return null;
                }
            }

            // 일반 공격
            float cost = CombatAPI.GetAbilityAPCost(attack);
            if (cost > remainingAP) return null;

            remainingAP -= cost;
            Main.LogDebug($"[{roleName}] PostMoveAttack: {attack.Name} -> {effectiveTarget.CharacterName}");
            return PlannedAction.Attack(attack, effectiveTarget, $"Post-move attack with {attack.Name}", cost);
        }

        /// <summary>
        /// ★ v3.1.24: 특정 위치에서 최근접 적 찾기
        /// </summary>
        private static BaseUnitEntity FindNearestEnemyFromPosition(UnityEngine.Vector3 position, List<BaseUnitEntity> enemies)
        {
            if (enemies == null || enemies.Count == 0) return null;

            return enemies
                .Where(e => e != null && e.IsConscious)
                .OrderBy(e => UnityEngine.Vector3.Distance(position, e.Position))
                .FirstOrDefault();
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
        /// ★ v3.1.21: Role 기반 최적 적 타겟 선택
        /// TargetScorer를 사용하여 Role별 가중치 적용
        /// </summary>
        public static BaseUnitEntity FindWeakestEnemy(Situation situation, HashSet<string> excludeTargetIds = null)
        {
            // Role 결정 (Auto면 DPS로 처리)
            var role = situation.CharacterSettings?.Role ?? Settings.AIRole.Auto;
            var effectiveRole = role == Settings.AIRole.Auto ? Settings.AIRole.DPS : role;

            var candidates = situation.HittableEnemies
                .Where(e => e != null && !e.LifeState.IsDead)
                .Where(e => !IsExcluded(e, excludeTargetIds))
                .ToList();

            if (candidates.Count > 0)
            {
                var best = TargetScorer.SelectBestEnemy(candidates, situation, effectiveRole);
                if (best != null) return best;
            }

            // 폴백: 모든 적
            var allCandidates = situation.Enemies
                .Where(e => e != null && !e.LifeState.IsDead)
                .Where(e => !IsExcluded(e, excludeTargetIds))
                .ToList();

            return TargetScorer.SelectBestEnemy(allCandidates, situation, effectiveRole);
        }

        #endregion

        #region AOE Attack (v3.1.16)

        /// <summary>
        /// ★ v3.1.16: AOE 공격 계획 - 안전하고 효율적인 위치 선택
        /// ★ v3.1.18: 방향성 패턴(Cone/Ray/Sector) 지원 추가
        /// </summary>
        public static PlannedAction PlanAoEAttack(
            Situation situation,
            ref float remainingAP,
            string roleName)
        {
            // Point 타겟 AOE 능력 찾기
            var aoEAbilities = situation.AvailableAttacks
                .Where(a => CombatAPI.IsPointTargetAbility(a))
                .Where(a => !AbilityDatabase.IsReload(a))
                .Where(a => !AbilityDatabase.IsTurnEnding(a))
                .ToList();

            if (aoEAbilities.Count == 0) return null;

            foreach (var ability in aoEAbilities)
            {
                float cost = CombatAPI.GetAbilityAPCost(ability);
                if (cost > remainingAP) continue;

                var patternType = CombatAPI.GetPatternType(ability);
                AoESafetyChecker.AoEScore bestResult = null;

                // ★ v3.1.18: 패턴 타입에 따른 분기
                if (CombatAPI.IsDirectionalPattern(patternType))
                {
                    // 방향성 패턴 (Cone/Ray/Sector) - 타겟 기반
                    bestResult = AoESafetyChecker.FindBestDirectionalAoETarget(
                        ability,
                        situation.Unit,
                        situation.Enemies,
                        situation.Allies,
                        minEnemiesRequired: 2);

                    if (bestResult == null || !bestResult.IsSafe) continue;

                    // 방향성 패턴은 주 타겟 유닛으로 타겟팅
                    var primaryTarget = bestResult.AffectedUnits
                        .FirstOrDefault(u => situation.Unit.CombatGroup.IsEnemy(u));

                    if (primaryTarget == null) continue;

                    // 유닛 타겟 검증
                    var targetWrapper = new TargetWrapper(primaryTarget);
                    string reason;
                    if (!CombatAPI.CanUseAbilityOn(ability, targetWrapper, out reason))
                    {
                        Main.LogDebug($"[{roleName}] Directional AOE blocked: {ability.Name} - {reason}");
                        continue;
                    }

                    remainingAP -= cost;
                    Main.Log($"[{roleName}] Directional AOE ({patternType}): {ability.Name} -> {primaryTarget.CharacterName} " +
                        $"- {bestResult.EnemiesHit} enemies, {bestResult.AlliesHit} allies");

                    return PlannedAction.Attack(
                        ability,
                        primaryTarget,
                        $"Directional AOE ({patternType}) on {bestResult.EnemiesHit} enemies",
                        cost);
                }
                else
                {
                    // Circle 패턴 - 위치 기반 (기존 로직)
                    bestResult = AoESafetyChecker.FindBestAoEPosition(
                        ability,
                        situation.Unit,
                        situation.Enemies,
                        situation.Allies,
                        minEnemiesRequired: 2);

                    if (bestResult == null || !bestResult.IsSafe) continue;

                    // Point 타겟 검증
                    string reason;
                    if (!CombatAPI.CanUseAbilityOnPoint(ability, bestResult.Position, out reason))
                    {
                        Main.LogDebug($"[{roleName}] AOE blocked: {ability.Name} - {reason}");
                        continue;
                    }

                    remainingAP -= cost;
                    Main.Log($"[{roleName}] AOE (Circle): {ability.Name} at ({bestResult.Position.x:F1},{bestResult.Position.z:F1}) " +
                        $"- {bestResult.EnemiesHit} enemies, {bestResult.AlliesHit} allies");

                    return PlannedAction.PositionalAttack(
                        ability,
                        bestResult.Position,
                        $"AOE on {bestResult.EnemiesHit} enemies",
                        cost);
                }
            }

            return null;
        }

        #endregion

        #region AOE Taunt (v3.1.17)

        /// <summary>
        /// ★ v3.1.17: AOE 도발 계획 - 다수 적 도발
        /// </summary>
        public static PlannedAction PlanAoETaunt(
            Situation situation,
            ref float remainingAP,
            string roleName)
        {
            // Point 타겟 + 도발 능력 찾기
            var aoeTaunts = situation.AvailableBuffs
                .Where(a => AbilityDatabase.IsTaunt(a))
                .Where(a => CombatAPI.IsPointTargetAbility(a))
                .ToList();

            if (aoeTaunts.Count == 0) return null;

            foreach (var ability in aoeTaunts)
            {
                float cost = CombatAPI.GetAbilityAPCost(ability);
                if (cost > remainingAP) continue;

                // 이미 활성화된 버프 스킵
                if (CombatAPI.HasActiveBuff(situation.Unit, ability)) continue;

                // 최적 위치 찾기 (적 대상이므로 기존 로직 재사용)
                var bestPosition = AoESafetyChecker.FindBestAoEPosition(
                    ability,
                    situation.Unit,
                    situation.Enemies,
                    situation.Allies,
                    minEnemiesRequired: 2);

                if (bestPosition == null || !bestPosition.IsSafe) continue;

                // Point 타겟 검증
                string reason;
                if (!CombatAPI.CanUseAbilityOnPoint(ability, bestPosition.Position, out reason))
                {
                    Main.LogDebug($"[{roleName}] AOE Taunt blocked: {ability.Name} - {reason}");
                    continue;
                }

                remainingAP -= cost;
                Main.Log($"[{roleName}] AOE Taunt: {ability.Name} at ({bestPosition.Position.x:F1},{bestPosition.Position.z:F1}) " +
                    $"- {bestPosition.EnemiesHit} enemies");

                return PlannedAction.PositionalBuff(
                    ability,
                    bestPosition.Position,
                    $"AOE Taunt on {bestPosition.EnemiesHit} enemies",
                    cost);
            }

            return null;
        }

        #endregion

        #region Self-Targeted AOE (v3.1.23)

        /// <summary>
        /// ★ v3.1.23: Self-Targeted AOE 공격 계획 (Bladedance 등)
        /// Range=Personal, CanTargetSelf인 DangerousAoE 능력 처리
        /// </summary>
        public static PlannedAction PlanSelfTargetedAoEAttack(
            Situation situation,
            AbilityData attack,
            ref float remainingAP,
            string roleName)
        {
            if (attack == null) return null;
            if (!CombatAPI.IsSelfTargetedAoEAttack(attack)) return null;

            float cost = CombatAPI.GetAbilityAPCost(attack);
            if (cost > remainingAP) return null;

            var caster = situation.Unit;

            // 안전성 체크: 인접 아군이 있으면 사용 거부 (아군 피해 방지)
            int adjacentAllies = CombatAPI.CountAdjacentAllies(caster);
            if (adjacentAllies > 0)
            {
                Main.LogDebug($"[{roleName}] Self-AoE {attack.Name} skipped: {adjacentAllies} allies adjacent");
                return null;
            }

            // 효율성 체크: 인접 적이 없으면 낭비
            int adjacentEnemies = CombatAPI.CountAdjacentEnemies(caster);
            if (adjacentEnemies == 0)
            {
                Main.LogDebug($"[{roleName}] Self-AoE {attack.Name} skipped: no adjacent enemies");
                return null;
            }

            // 자신에게 사용 가능한지 확인
            var selfTarget = new TargetWrapper(caster);
            string reason;
            if (!CombatAPI.CanUseAbilityOn(attack, selfTarget, out reason))
            {
                Main.LogDebug($"[{roleName}] Self-AoE {attack.Name} unavailable: {reason}");
                return null;
            }

            remainingAP -= cost;
            Main.Log($"[{roleName}] Self-AoE: {attack.Name} (hitting {adjacentEnemies} enemies)");

            // ★ 자신을 타겟으로 하는 Buff 형태로 반환
            return PlannedAction.Buff(attack, caster,
                $"Self-AoE attack hitting {adjacentEnemies} enemies", cost);
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
