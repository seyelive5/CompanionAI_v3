using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Mechanics.Actions;
using Kingmaker.Utility;
using UnityEngine;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// ★ v3.0.47: 버프/디버프 관련 계획 담당
    /// - 자기 버프, 아군 버프, 디버프, 마커, 위치 버프, Stratagem
    /// </summary>
    public static class BuffPlanner
    {
        /// <summary>
        /// ★ v3.8.41: 통합 궁극기 계획 (모든 역할 공통)
        ///
        /// 모든 타겟 유형(Self, 적, 아군, 지점)의 궁극기를 올바르게 처리
        /// HeroicAct + DesperateMeasure 모두 탐색
        ///
        /// 호출 시점: 각 플랜의 최초 페이즈 (FreeUltimateBuff 감지 시)
        /// </summary>
        public static PlannedAction PlanUltimate(Situation situation, ref float remainingAP, string roleName)
        {
            var unit = situation.Unit;

            // 모든 궁극기 수집 (HeroicAct + DesperateMeasure)
            var ultimates = situation.AvailableBuffs
                .Where(a => CombatAPI.IsUltimateAbility(a))
                .ToList();

            // AvailableAttacks에도 궁극기가 있을 수 있음 (적 타겟 궁극기)
            if (situation.AvailableAttacks != null)
            {
                var attackUltimates = situation.AvailableAttacks
                    .Where(a => CombatAPI.IsUltimateAbility(a))
                    .ToList();
                ultimates.AddRange(attackUltimates);
            }

            // 중복 제거 (GUID 기반)
            ultimates = ultimates
                .GroupBy(a => a.Blueprint?.AssetGuid?.ToString() ?? a.Name)
                .Select(g => g.First())
                .ToList();

            if (ultimates.Count == 0)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanUltimate: No ultimate abilities available");
                return null;
            }

            Main.Log($"[{roleName}] PlanUltimate: Found {ultimates.Count} ultimates: {string.Join(", ", ultimates.Select(a => a.Name))}");

            // ★ v3.8.48: anonymous type → ValueTuple (GC 압박 감소)
            // 점수 기반 정렬 (ScoreBuff 사용 → FreeUltimateBuff 보너스 포함)
            var scored = new List<(AbilityData Ability, float Score)>();
            for (int i = 0; i < ultimates.Count; i++)
            {
                float s = UtilityScorer.ScoreBuff(ultimates[i], situation);
                if (s > 0) scored.Add((ultimates[i], s));
            }
            scored.Sort((x, y) => y.Score.CompareTo(x.Score));

            if (scored.Count == 0)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanUltimate: All ultimates scored <= 0");
                return null;
            }

            for (int idx = 0; idx < scored.Count; idx++)
            {
                var ability = scored[idx].Ability;
                float cost = CombatAPI.GetAbilityAPCost(ability);

                // 0 코스트가 아닌 경우 AP 체크
                if (cost > 0 && cost > remainingAP)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanUltimate: {ability.Name} skipped (cost={cost:F1} > AP={remainingAP:F1})");
                    continue;
                }

                // 타겟 유형에 따라 적절한 타겟 결정
                var targetType = CombatAPI.ClassifyUltimateTarget(ability);
                TargetWrapper target = null;
                string targetDesc = "";

                switch (targetType)
                {
                    case CombatAPI.UltimateTargetType.SelfBuff:
                        // Personal 궁극기 → 자기 자신
                        target = new TargetWrapper(unit);
                        targetDesc = "self";
                        break;

                    case CombatAPI.UltimateTargetType.ImmediateAttack:
                        // 적 타겟 궁극기 → 최적 적 선택
                        var bestEnemy = situation.BestTarget ?? situation.NearestEnemy;
                        if (bestEnemy != null)
                        {
                            target = new TargetWrapper(bestEnemy);
                            targetDesc = bestEnemy.CharacterName;
                        }
                        break;

                    case CombatAPI.UltimateTargetType.AllyBuff:
                        // 아군 타겟 궁극기 → 최적 아군 선택 (가장 강한 딜러 우선)
                        var bestAlly = SelectBestAllyForUltimate(situation);
                        if (bestAlly != null)
                        {
                            target = new TargetWrapper(bestAlly);
                            targetDesc = bestAlly.CharacterName;
                        }
                        break;

                    case CombatAPI.UltimateTargetType.AreaEffect:
                        // 지점 타겟 궁극기 → 최적 위치 계산
                        var bestPos = FindBestUltimatePosition(ability, situation);
                        if (bestPos.HasValue)
                        {
                            target = new TargetWrapper(bestPos.Value);
                            targetDesc = $"({bestPos.Value.x:F1},{bestPos.Value.z:F1})";
                        }
                        break;

                    default:
                        // 알 수 없는 타겟 → self 시도
                        target = new TargetWrapper(unit);
                        targetDesc = "self(fallback)";
                        break;
                }

                if (target == null)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanUltimate: {ability.Name} skipped - no valid target for {targetType}");
                    continue;
                }

                string reason;
                if (CombatAPI.CanUseAbilityOn(ability, target, out reason))
                {
                    remainingAP -= cost;
                    Main.Log($"[{roleName}] ★ ULTIMATE: {ability.Name} -> {targetDesc} " +
                        $"(type={targetType}, score={scored[idx].Score:F0}, heroic={ability.Blueprint?.IsHeroicAct})");

                    // 타겟 유형에 따른 PlannedAction 생성
                    switch (targetType)
                    {
                        case CombatAPI.UltimateTargetType.ImmediateAttack:
                            var targetEntity = target.Entity as BaseUnitEntity;
                            return PlannedAction.Attack(ability, targetEntity,
                                $"Ultimate attack: {ability.Name}", cost);

                        case CombatAPI.UltimateTargetType.AreaEffect:
                            return PlannedAction.PositionalBuff(ability, target.Point,
                                $"Ultimate area: {ability.Name}", cost);

                        default:
                            var buffTarget = (target.Entity as BaseUnitEntity) ?? unit;
                            return PlannedAction.Buff(ability, buffTarget,
                                $"Ultimate: {ability.Name}", cost);
                    }
                }
                else
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanUltimate: {ability.Name} -> {targetDesc} failed: {reason}");
                }
            }

            if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanUltimate: All candidates failed");
            return null;
        }

        /// <summary>
        /// ★ v3.8.41: 궁극기 사용 대상으로 최적 아군 선택 (Finest Hour! 등)
        /// 우선순위: 풀AP 공격 가능한 강한 딜러 > HP 높은 아군 > 가장 가까운 아군
        /// </summary>
        private static BaseUnitEntity SelectBestAllyForUltimate(Situation situation)
        {
            if (situation.Allies == null || situation.Allies.Count == 0)
                return null;

            BaseUnitEntity bestAlly = null;
            float bestScore = float.MinValue;

            foreach (var ally in situation.Allies)
            {
                if (ally == null || !ally.IsConscious) continue;
                if (ally == situation.Unit) continue;  // 자기 자신 제외

                float score = 0f;

                // HP가 높은 아군 우선 (생존 가능성)
                float hpPercent = CombatCache.GetHPPercent(ally);
                score += hpPercent;

                // DPS 역할 우선 (추가 턴의 가치 극대화)
                var settings = ModSettings.Instance?.GetOrCreateSettings(ally.UniqueId, ally.CharacterName);
                var role = settings?.Role ?? AIRole.Auto;
                if (role == AIRole.Auto)
                    role = RoleDetector.DetectOptimalRole(ally);

                if (role == AIRole.DPS) score += 50f;
                else if (role == AIRole.Tank) score += 20f;
                else if (role == AIRole.Support) score += 10f;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestAlly = ally;
                }
            }

            if (bestAlly != null)
                if (Main.IsDebugEnabled) Main.LogDebug($"[BuffPlanner] Best ally for ultimate: {bestAlly.CharacterName} (score={bestScore:F0})");

            return bestAlly;
        }

        /// <summary>
        /// ★ v3.8.41: 구역 궁극기 최적 위치 계산 (Take and Hold, Orchestrated Firestorm 등)
        /// </summary>
        private static Vector3? FindBestUltimatePosition(AbilityData ability, Situation situation)
        {
            bool isOffensive = ability.Blueprint?.NotOffensive != true;
            float radius = CombatAPI.GetAoERadius(ability);
            if (radius <= 0) radius = 3f;

            if (isOffensive)
            {
                // 공격형 구역: 적이 가장 많이 모인 위치
                var enemies = situation.Enemies?.Where(e => e != null && e.IsConscious).ToList();
                if (enemies == null || enemies.Count == 0) return null;

                var clusters = ClusterDetector.FindClusters(enemies, radius);
                if (clusters.Any())
                {
                    var bestCluster = clusters.OrderByDescending(c => c.Count).First();
                    return bestCluster.Center;
                }

                // 폴백: 가장 가까운 적 위치
                return situation.NearestEnemy?.Position;
            }
            else
            {
                // 지원형 구역: 아군이 가장 많이 모인 위치
                var allies = situation.Allies?.Where(a => a != null && a.IsConscious).ToList();
                if (allies == null || allies.Count == 0) return null;

                return FindBestCoveragePosition(allies, radius,
                    CalculateAveragePosition(allies));
            }
        }

        /// <summary>
        /// 버프 계획 (AP 예약 고려)
        /// </summary>
        public static PlannedAction PlanBuffWithReservation(Situation situation, ref float remainingAP, float reservedAP, string roleName)
        {
            if (situation.BestBuff == null) return null;

            var buff = situation.BestBuff;
            float cost = CombatAPI.GetAbilityAPCost(buff);

            bool isEssential = IsEssentialBuff(buff, situation);
            if (!CanAffordBuffWithReservation(cost, remainingAP, reservedAP, isEssential))
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] Skip buff {buff.Name}: cost={cost:F1}, remaining={remainingAP:F1}, reserved={reservedAP:F1}");
                return null;
            }

            var target = new TargetWrapper(situation.Unit);
            string reason;
            if (CombatAPI.CanUseAbilityOn(buff, target, out reason))
            {
                remainingAP -= cost;
                Main.Log($"[{roleName}] Buff: {buff.Name} (cost={cost:F1})");
                return PlannedAction.Buff(buff, situation.Unit, $"Proactive buff: {buff.Name}", cost);
            }

            return null;
        }

        /// <summary>
        /// 방어 자세 계획 (Tank 전용)
        /// </summary>
        public static PlannedAction PlanDefensiveStanceWithReservation(Situation situation, ref float remainingAP, float reservedAP, string roleName)
        {
            var target = new TargetWrapper(situation.Unit);

            foreach (var ability in situation.AvailableBuffs)
            {
                var info = AbilityDatabase.GetInfo(ability);
                if (info == null) continue;
                if (info.Timing != AbilityTiming.PreCombatBuff) continue;

                // ★ v3.5.75: 통합 API 사용
                if (!AbilityDatabase.IsDefensiveStance(ability))
                    continue;

                float cost = CombatAPI.GetAbilityAPCost(ability);

                bool isEssential = IsEssentialBuff(ability, situation);
                if (!CanAffordBuffWithReservation(cost, remainingAP, reservedAP, isEssential))
                    continue;

                if (AllyStateCache.HasBuff(situation.Unit, ability)) continue;

                // ★ v3.8.25: AbilityCasterHasFacts 검증 (스택 버프 필요 여부)
                // GetUnavailabilityReasons()가 감지하지 못하는 캐스터 제한 검증
                string factReason;
                if (!CombatAPI.MeetsCasterFactRequirements(ability, out factReason))
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] DefensiveStance skipped - {factReason}");
                    continue;
                }

                string reason;
                if (CombatAPI.CanUseAbilityOn(ability, target, out reason))
                {
                    remainingAP -= cost;
                    Main.Log($"[{roleName}] Defensive stance: {ability.Name}");
                    return PlannedAction.Buff(ability, situation.Unit, "Defensive stance priority", cost);
                }
            }

            return null;
        }

        /// <summary>
        /// 공격 버프 계획 (DPS 전용)
        /// ★ v3.1.10: 사용 가능한 공격이 없으면 스킵
        /// </summary>
        public static PlannedAction PlanAttackBuffWithReservation(Situation situation, ref float remainingAP, float reservedAP, string roleName)
        {
            // ★ v3.1.10: 사용 가능한 공격이 없으면 공격 전 버프 사용 금지
            if (situation.AvailableAttacks == null || situation.AvailableAttacks.Count == 0)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanAttackBuff skipped: No available attacks");
                return null;
            }

            // ★ v3.8.68: 실제 공격 가능한 적이 없으면 공격 버프 사용 금지
            // AvailableAttacks는 능력 존재만 체크, HasHittableEnemies는 LOS+사거리 체크
            // AoE 안전 체크까지는 여기서 못 하지만, 기본적인 실행 가능 여부는 검증
            if (!situation.HasHittableEnemies)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanAttackBuff skipped: No hittable enemies (attacks available but no valid targets)");
                return null;
            }

            var attackBuffs = situation.AvailableBuffs
                .Where(a => {
                    var timing = AbilityDatabase.GetTiming(a);
                    return timing == AbilityTiming.PreAttackBuff || timing == AbilityTiming.RighteousFury;
                })
                .ToList();

            if (attackBuffs.Count == 0) return null;

            float effectiveReservedAP = situation.HasHittableEnemies
                ? (situation.PrimaryAttack != null ? CombatAPI.GetAbilityAPCost(situation.PrimaryAttack) : 1f)
                : reservedAP;

            var target = new TargetWrapper(situation.Unit);

            foreach (var buff in attackBuffs)
            {
                if (AbilityDatabase.IsRunAndGun(buff)) continue;
                if (AbilityDatabase.IsPostFirstAction(buff)) continue;

                float cost = CombatAPI.GetAbilityAPCost(buff);

                bool isEssential = IsEssentialBuff(buff, situation);
                if (!CanAffordBuffWithReservation(cost, remainingAP, effectiveReservedAP, isEssential))
                    continue;

                if (AllyStateCache.HasBuff(situation.Unit, buff)) continue;

                string reason;
                if (CombatAPI.CanUseAbilityOn(buff, target, out reason))
                {
                    remainingAP -= cost;
                    Main.Log($"[{roleName}] Attack buff: {buff.Name}");
                    return PlannedAction.Buff(buff, situation.Unit, "Attack buff before strike", cost);
                }
            }

            return null;
        }

        /// <summary>
        /// 도발 계획 (Tank 전용)
        /// ★ v3.8.19: AllyTarget 도발 (FightMe 등) 처리 추가
        /// </summary>
        public static PlannedAction PlanTaunt(Situation situation, ref float remainingAP, string roleName)
        {
            var taunts = situation.AvailableBuffs
                .Where(a => AbilityDatabase.IsTaunt(a))
                .ToList();

            if (taunts.Count == 0) return null;

            foreach (var taunt in taunts)
            {
                float cost = CombatAPI.GetAbilityAPCost(taunt);
                if (cost > remainingAP) continue;

                if (AllyStateCache.HasBuff(situation.Unit, taunt)) continue;

                TargetWrapper target;
                if (taunt.Blueprint?.CanTargetSelf == true)
                {
                    target = new TargetWrapper(situation.Unit);
                }
                // ★ v3.8.19: 아군 타겟 도발 (FightMe 등) - 위협받는 아군 보호
                else if (taunt.Blueprint?.CanTargetFriends == true && taunt.Blueprint?.CanTargetEnemies == false)
                {
                    // 적에게 타겟팅되고 있거나 적에게 둘러싸인 아군 찾기
                    var allyToProtect = FindAllyNeedingProtection(situation);
                    if (allyToProtect != null)
                    {
                        target = new TargetWrapper(allyToProtect);
                        Main.Log($"[{roleName}] AllyTaunt: {taunt.Name} -> protecting {allyToProtect.CharacterName}");
                    }
                    else
                    {
                        continue;
                    }
                }
                else if (situation.NearestEnemy != null)
                {
                    target = new TargetWrapper(situation.NearestEnemy);
                }
                else
                {
                    continue;
                }

                string reason;
                if (CombatAPI.CanUseAbilityOn(taunt, target, out reason))
                {
                    remainingAP -= cost;
                    Main.Log($"[{roleName}] Taunt: {taunt.Name}");
                    return PlannedAction.Buff(taunt, situation.Unit, "Taunt - enemies nearby", cost);
                }
            }

            return null;
        }

        /// <summary>
        /// ★ v3.8.19: 보호가 필요한 아군 찾기 (FightMe 등 아군 타겟 도발용)
        /// 우선순위: 적에게 둘러싸인 아군 > HP 낮은 아군 > 가장 가까운 아군
        /// </summary>
        private static BaseUnitEntity FindAllyNeedingProtection(Situation situation)
        {
            if (situation.Allies == null || situation.Allies.Count == 0)
                return null;

            BaseUnitEntity bestAlly = null;
            float bestScore = float.MinValue;

            foreach (var ally in situation.Allies)
            {
                if (ally == situation.Unit) continue;  // 자기 자신 제외
                if (!ally.IsConscious) continue;

                float score = 0f;

                // 주변 적 수 계산 (반경 3m 내)
                int nearbyEnemies = situation.Enemies?.Count(e =>
                    e.IsConscious && CombatCache.GetDistance(ally, e) <= 4.5f) ?? 0;

                score += nearbyEnemies * 50f;  // 적 1명당 50점

                // HP 비율 낮을수록 높은 점수
                float hpPercent = CombatCache.GetHPPercent(ally);
                score += (1f - hpPercent) * 100f;  // HP 0%면 100점 추가

                // 탱크가 아닌 캐릭터 우선 (딜러/서포터 보호)
                // 간단히 HP가 낮은 캐릭터를 우선시

                if (score > bestScore && nearbyEnemies > 0)  // 적이 근처에 있어야 함
                {
                    bestScore = score;
                    bestAlly = ally;
                }
            }

            return bestAlly;
        }

        /// <summary>
        /// Heroic Act 계획 (DPS 전용)
        /// </summary>
        public static PlannedAction PlanHeroicAct(Situation situation, ref float remainingAP, string roleName)
        {
            var heroicAbilities = situation.AvailableBuffs
                .Where(a => AbilityDatabase.IsHeroicAct(a))
                .ToList();

            if (heroicAbilities.Count == 0) return null;

            var target = new TargetWrapper(situation.Unit);
            string unitId = situation.Unit.UniqueId;

            foreach (var heroic in heroicAbilities)
            {
                float cost = CombatAPI.GetAbilityAPCost(heroic);
                if (cost > remainingAP) continue;

                if (AbilityDatabase.IsSingleUse(heroic) &&
                    AbilityUsageTracker.WasUsedRecently(unitId, heroic, 6000))
                {
                    continue;
                }

                if (AllyStateCache.HasBuff(situation.Unit, heroic)) continue;

                string reason;
                if (CombatAPI.CanUseAbilityOn(heroic, target, out reason))
                {
                    AbilityUsageTracker.MarkUsed(unitId, heroic);
                    remainingAP -= cost;
                    Main.Log($"[{roleName}] Heroic Act: {heroic.Name}");
                    return PlannedAction.Buff(heroic, situation.Unit, "Heroic Act - high momentum", cost);
                }
            }

            return null;
        }

        /// <summary>
        /// 디버프 계획
        /// </summary>
        public static PlannedAction PlanDebuff(Situation situation, BaseUnitEntity target, ref float remainingAP, string roleName)
        {
            var debuffs = situation.AvailableDebuffs;
            if (debuffs.Count == 0) return null;

            var targetWrapper = new TargetWrapper(target);

            foreach (var debuff in debuffs)
            {
                float cost = CombatAPI.GetAbilityAPCost(debuff);
                if (cost > remainingAP) continue;

                if (AllyStateCache.HasBuff(target, debuff)) continue;

                string reason;
                if (CombatAPI.CanUseAbilityOn(debuff, targetWrapper, out reason))
                {
                    remainingAP -= cost;
                    Main.Log($"[{roleName}] Debuff: {debuff.Name} -> {target.CharacterName}");
                    return PlannedAction.Debuff(debuff, target, $"Debuff {target.CharacterName}", cost);
                }
            }

            return null;
        }

        /// <summary>
        /// 마킹 스킬 계획
        /// </summary>
        public static PlannedAction PlanMarker(Situation situation, BaseUnitEntity target, ref float remainingAP, string roleName)
        {
            var markers = situation.AvailableMarkers;
            if (markers.Count == 0) return null;
            if (target == null) return null;

            var targetWrapper = new TargetWrapper(target);

            foreach (var marker in markers)
            {
                float cost = CombatAPI.GetAbilityAPCost(marker);
                if (cost > remainingAP) continue;

                if (AllyStateCache.HasBuff(target, marker)) continue;

                string reason;
                if (CombatAPI.CanUseAbilityOn(marker, targetWrapper, out reason))
                {
                    remainingAP -= cost;
                    Main.Log($"[{roleName}] Marker: {marker.Name} -> {target.CharacterName}");
                    return PlannedAction.Debuff(marker, target, $"Mark {target.CharacterName}", cost);
                }
            }

            return null;
        }

        /// <summary>
        /// 방어 버프 계획 (Post-attack용)
        /// </summary>
        public static PlannedAction PlanDefensiveBuff(Situation situation, ref float remainingAP, string roleName)
        {
            var target = new TargetWrapper(situation.Unit);

            // ★ v3.5.75: 통합 API 사용
            var defensiveBuffs = situation.AvailableBuffs
                .Where(a => !AllyStateCache.HasBuff(situation.Unit, a))
                .Where(a => AbilityDatabase.IsDefensiveStance(a))
                .ToList();

            foreach (var buff in defensiveBuffs)
            {
                float cost = CombatAPI.GetAbilityAPCost(buff);
                if (cost > remainingAP) continue;

                string reason;
                if (CombatAPI.CanUseAbilityOn(buff, target, out reason))
                {
                    remainingAP -= cost;
                    return PlannedAction.Buff(buff, situation.Unit, "Defensive buff", cost);
                }
            }

            return null;
        }

        /// <summary>
        /// 위치 버프 계획 (Grand Strategist 등)
        /// ★ v3.5.93: AoESafetyChecker.FindBestAllyAoEPosition 패턴 적용
        /// - 각 능력의 실제 AOE 반경 사용
        /// - 역할 그룹별 최적 커버리지 위치 계산
        /// </summary>
        public static PlannedAction PlanPositionalBuff(Situation situation, ref float remainingAP, HashSet<string> usedBuffGuids, string roleName)
        {
            var positionalBuffs = situation.AvailablePositionalBuffs;
            if (positionalBuffs == null || positionalBuffs.Count == 0) return null;

            var allies = situation.Allies.Where(a => a != null && !a.LifeState.IsDead).ToList();
            allies.Add(situation.Unit);

            if (allies.Count == 0) return null;

            // ★ v3.5.93: 역할별 아군 분류 (미리 한 번만 수행)
            var roleGroups = ClassifyAlliesByRole(allies);

            foreach (var buff in positionalBuffs)
            {
                string buffGuid = buff.Blueprint?.AssetGuid?.ToString() ?? buff.Name;
                if (usedBuffGuids != null && usedBuffGuids.Contains(buffGuid))
                    continue;

                float cost = CombatAPI.GetAbilityAPCost(buff);
                if (cost > remainingAP) continue;

                // ★ v3.5.98: 능력의 실제 AOE 반경 조회 (타일 단위)
                float aoERadius = CombatAPI.GetAoERadius(buff);  // 타일
                if (aoERadius <= 0)
                {
                    // 폴백: Pattern에서 직접 조회 (타일 단위)
                    try
                    {
                        var spawnAction = buff.Blueprint?.ElementsArray?
                            .OfType<ContextActionSpawnAreaEffect>()
                            .FirstOrDefault();
                        aoERadius = spawnAction?.AreaEffect?.Pattern?.Radius ?? 3;  // 이미 타일 단위
                    }
                    catch
                    {
                        aoERadius = 3f;  // 기본값 (타일)
                    }
                }

                // ★ v3.5.91: enum으로 존 타입 결정
                var zoneTypeEnum = GetZoneTypeFromAbility(buff);
                List<BaseUnitEntity> targetGroup;
                string zoneType;

                if (zoneTypeEnum.HasValue)
                {
                    switch (zoneTypeEnum.Value)
                    {
                        case StrategistTacticsAreaEffectType.Frontline:
                            targetGroup = roleGroups.tankOrMelee;
                            zoneType = "Frontline";
                            break;
                        case StrategistTacticsAreaEffectType.Backline:
                            targetGroup = roleGroups.supports;
                            zoneType = "Backline";
                            break;
                        case StrategistTacticsAreaEffectType.Rear:
                            targetGroup = roleGroups.rangedDPS;
                            zoneType = "Rear";
                            break;
                        default:
                            targetGroup = allies;
                            zoneType = "Zone";
                            break;
                    }
                }
                else
                {
                    targetGroup = allies;
                    zoneType = "Zone";
                }

                // ★ v3.5.93: 능력의 실제 반경으로 최적 위치 계산
                Vector3 preferredPosition = FindBestCoveragePosition(
                    targetGroup.Count > 0 ? targetGroup : allies,
                    aoERadius,
                    CalculateAveragePosition(allies));

                if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] {buff.Name} ({zoneType}): radius={aoERadius:F1}m, targetGroup={targetGroup.Count} units");

                // 겹침 체크 및 대체 위치 찾기
                Vector3 targetPosition = preferredPosition;
                if (CombatAPI.IsStrategistZoneAbility(buff))
                {
                    if (CombatAPI.IsPositionTooCloseToExistingZones(preferredPosition, aoERadius))
                    {
                        var nonOverlappingPos = CombatAPI.FindNonOverlappingZonePosition(buff, preferredPosition, aoERadius * 2f);
                        if (nonOverlappingPos.HasValue)
                        {
                            targetPosition = nonOverlappingPos.Value;
                            Main.Log($"[{roleName}] PositionalBuff: {buff.Name} adjusted position to avoid overlap");
                        }
                        else
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PositionalBuff: {buff.Name} skipped - no non-overlapping position found");
                            usedBuffGuids?.Add(buffGuid);
                            continue;
                        }
                    }
                }

                var target = new TargetWrapper(targetPosition);
                string reason;
                if (CombatAPI.CanUseAbilityOn(buff, target, out reason))
                {
                    remainingAP -= cost;
                    usedBuffGuids?.Add(buffGuid);
                    Main.Log($"[{roleName}] PositionalBuff: {buff.Name} ({zoneType}, r={aoERadius:F1}m) at ({targetPosition.x:F1}, {targetPosition.z:F1})");
                    return PlannedAction.PositionalBuff(buff, targetPosition, $"{zoneType} zone", cost);
                }
            }

            return null;
        }

        /// <summary>
        /// ★ v3.5.93: 아군을 역할별로 분류
        /// </summary>
        private static (List<BaseUnitEntity> tankOrMelee, List<BaseUnitEntity> supports, List<BaseUnitEntity> rangedDPS)
            ClassifyAlliesByRole(List<BaseUnitEntity> allies)
        {
            var tankOrMelee = new List<BaseUnitEntity>();
            var supports = new List<BaseUnitEntity>();
            var rangedDPS = new List<BaseUnitEntity>();

            foreach (var ally in allies)
            {
                var settings = ModSettings.Instance?.GetOrCreateSettings(ally.UniqueId, ally.CharacterName);
                var role = settings?.Role ?? AIRole.Auto;
                var rangePreference = settings?.RangePreference ?? RangePreference.Adaptive;

                if (role == AIRole.Auto)
                    role = RoleDetector.DetectOptimalRole(ally);

                if (role == AIRole.Tank || (role == AIRole.DPS && rangePreference == RangePreference.PreferMelee))
                    tankOrMelee.Add(ally);
                else if (role == AIRole.Support)
                    supports.Add(ally);
                else if (role == AIRole.DPS && rangePreference == RangePreference.PreferRanged)
                    rangedDPS.Add(ally);
                else
                    supports.Add(ally);  // Adaptive → Support 그룹
            }

            // 로깅
            string tankNames = string.Join(", ", tankOrMelee.Select(a => a.CharacterName));
            string supportNames = string.Join(", ", supports.Select(a => a.CharacterName));
            string rangedNames = string.Join(", ", rangedDPS.Select(a => a.CharacterName));
            if (Main.IsDebugEnabled) Main.LogDebug($"[BuffPlanner] Role groups: Tank/Melee=[{tankNames}], Support=[{supportNames}], Ranged=[{rangedNames}]");

            return (tankOrMelee, supports, rangedDPS);
        }

        /// <summary>
        /// ★ v3.5.93: 타겟 그룹을 최대한 커버하는 AOE 위치 찾기
        /// AoESafetyChecker.FindBestAllyAoEPosition 로직 기반
        /// </summary>
        private static Vector3 FindBestCoveragePosition(List<BaseUnitEntity> targets, float radius, Vector3 fallback)
        {
            if (targets == null || targets.Count == 0)
                return fallback;

            if (targets.Count == 1)
                return targets[0].Position;

            var candidates = new List<(Vector3 pos, int count, float score)>();

            // 전략 1: 각 타겟 위치를 중심으로 평가
            foreach (var target in targets)
            {
                if (target == null || !target.IsConscious) continue;

                var (count, score) = EvaluateCoverageAt(target.Position, targets, radius);
                candidates.Add((target.Position, count, score));
            }

            // 전략 2: 타겟 쌍의 중간점 평가
            for (int i = 0; i < targets.Count; i++)
            {
                for (int j = i + 1; j < targets.Count; j++)
                {
                    var t1 = targets[i];
                    var t2 = targets[j];
                    if (t1 == null || t2 == null || !t1.IsConscious || !t2.IsConscious) continue;

                    // ★ v3.5.98: 두 타겟이 너무 멀면 중간점 스킵 (타일 단위)
                    if (CombatCache.GetDistanceInTiles(t1, t2) > radius * 2) continue;

                    Vector3 midpoint = (t1.Position + t2.Position) / 2f;
                    var (count, score) = EvaluateCoverageAt(midpoint, targets, radius);
                    candidates.Add((midpoint, count, score));
                }
            }

            // ★ v3.8.48: LINQ → 수동 루프 (0 할당)
            // 최적 위치 선택: 커버 수 > 스코어 순
            var best = (pos: Vector3.zero, count: 0, score: 0f);
            float bestComposite = float.MinValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                float composite = c.count * 100000f + c.score;
                if (composite > bestComposite)
                {
                    bestComposite = composite;
                    best = c;
                }
            }

            if (best.count > 0)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[BuffPlanner] Best coverage: {best.count} units at ({best.pos.x:F1}, {best.pos.z:F1}), score={best.score:F0}");
                return best.pos;
            }

            return fallback;
        }

        /// <summary>
        /// ★ v3.5.98: 특정 위치에서 타겟 커버리지 평가 (radius는 타일 단위)
        /// </summary>
        private static (int count, float score) EvaluateCoverageAt(Vector3 position, List<BaseUnitEntity> targets, float radius)
        {
            int count = 0;
            float score = 0f;
            const float HIT_SCORE = 10000f;

            foreach (var target in targets)
            {
                if (target == null || !target.IsConscious) continue;

                // ★ v3.5.98: 타일 단위로 변환
                float dist = CombatAPI.MetersToTiles(Vector3.Distance(position, target.Position));
                if (dist <= radius)
                {
                    count++;
                    // 거리가 가까울수록 높은 점수
                    score += HIT_SCORE - dist * dist;
                }
            }

            return (count, score);
        }

        /// <summary>
        /// ★ v3.5.91: Stratagem 계획 - 스마트 존 선택 (GUID 기반)
        /// </summary>
        public static PlannedAction PlanStratagem(Situation situation, ref float remainingAP, string roleName)
        {
            var stratagems = situation.AvailableStratagems;
            if (stratagems == null || stratagems.Count == 0) return null;

            // ★ v3.5.90: 존 정보와 함께 조회
            var zoneInfos = GetStrategistZonesWithInfo(situation.Unit, situation);
            if (zoneInfos.Count == 0) return null;

            var sortedStratagems = stratagems
                .OrderBy(s => GetStratagemPriority(s, situation))
                .ToList();

            foreach (var stratagem in sortedStratagems)
            {
                float cost = CombatAPI.GetAbilityAPCost(stratagem);
                if (cost > remainingAP) continue;

                // ★ v3.5.90: Stratagem 유형에 따라 최적 존 선택
                var bestZone = SelectBestZoneForStratagem(stratagem, zoneInfos, situation);
                if (bestZone == null) continue;

                var target = new TargetWrapper(bestZone.Position);
                string reason;
                if (CombatAPI.CanUseAbilityOn(stratagem, target, out reason))
                {
                    remainingAP -= cost;
                    string stratagemType = GetStratagemType(stratagem);
                    Main.Log($"[{roleName}] Stratagem: {stratagem.Name} ({stratagemType}) -> {bestZone.ZoneType} (allies={bestZone.AllyCount}, enemies={bestZone.EnemyCount})");
                    return PlannedAction.PositionalBuff(stratagem, bestZone.Position,
                        $"Stratagem: {stratagemType} on {bestZone.ZoneType}", cost);
                }
            }

            return null;
        }

        /// <summary>
        /// ★ v3.5.90: Stratagem 유형에 따라 최적 존 선택
        /// </summary>
        private static ZoneInfo SelectBestZoneForStratagem(AbilityData stratagem, List<ZoneInfo> zones, Situation situation)
        {
            if (zones == null || zones.Count == 0) return null;

            string type = GetStratagemType(stratagem);

            switch (type)
            {
                case "Killzone":
                    // 적이 가장 많은 존 (적에게 재굴림 강제, 즉사 효과)
                    return zones.OrderByDescending(z => z.EnemyCount).FirstOrDefault();

                case "Overwhelming":
                    // 적+아군 모두 있는 존 (covering 효과로 아군 공격 강화)
                    return zones.Where(z => z.EnemyCount > 0 && z.AllyCount > 0)
                               .OrderByDescending(z => z.AllyCount)
                               .FirstOrDefault() ?? zones.FirstOrDefault();

                case "Stronghold":
                    // HP 낮은 아군이 있는 존 (아머 보너스, 방어)
                    return zones.OrderByDescending(z => z.LowHPAllyCount)
                               .ThenByDescending(z => z.AllyCount)
                               .FirstOrDefault();

                case "Trenchline":
                    // Frontline 우선 (근접 적 방어용)
                    return zones.FirstOrDefault(z => z.ZoneType == "Frontline")
                        ?? zones.OrderByDescending(z => z.EnemyCount).FirstOrDefault();

                case "CombatLocus":
                    // 아군이 가장 많은 존 (보너스 2배)
                    return zones.OrderByDescending(z => z.AllyCount).FirstOrDefault();

                case "Blitz":
                    // Frontline 우선 (이동 보너스로 진입 지원)
                    return zones.FirstOrDefault(z => z.ZoneType == "Frontline")
                        ?? zones.FirstOrDefault();

                default:
                    return zones.FirstOrDefault();
            }
        }

        /// <summary>
        /// ★ v3.5.91: Strategist 존 정보 조회 (enum 기반 타입 식별)
        /// </summary>
        private static List<ZoneInfo> GetStrategistZonesWithInfo(BaseUnitEntity caster, Situation situation)
        {
            var zones = new List<ZoneInfo>();

            try
            {
                var areaEffects = Game.Instance?.State?.AreaEffects;
                if (areaEffects == null) return zones;

                foreach (var areaEffect in areaEffects)
                {
                    var bp = areaEffect.Blueprint;
                    if (bp == null || !bp.IsStrategistAbility) continue;
                    if (areaEffect.Context?.MaybeCaster != caster) continue;

                    var zonePos = areaEffect.View?.ViewTransform?.position ?? areaEffect.Position;

                    // ★ v3.6.2: 실제 AOE 반경 사용 (Pattern.Radius는 타일 단위)
                    float zoneRadiusTiles = bp.Pattern?.Radius ?? 3;  // 타일 단위

                    // ★ v3.5.91: enum으로 존 타입 식별 (텍스트 매칭 제거)
                    string zoneType = bp.TacticsAreaEffectType.ToString();  // "Frontline", "Backline", "Rear"

                    // ★ v3.6.2: 존 내 유닛 수 계산 - 타일 단위로 통일
                    int allyCount = 0, enemyCount = 0, lowHPAllyCount = 0;

                    if (situation.Allies != null)
                    {
                        foreach (var ally in situation.Allies)
                        {
                            if (ally == null || ally.LifeState.IsDead) continue;
                            float distTiles = CombatAPI.MetersToTiles(Vector3.Distance(ally.Position, zonePos));
                            if (distTiles <= zoneRadiusTiles)
                            {
                                allyCount++;
                                if (CombatCache.GetHPPercent(ally) < 50f)
                                    lowHPAllyCount++;
                            }
                        }
                    }

                    if (situation.Enemies != null)
                    {
                        foreach (var enemy in situation.Enemies)
                        {
                            if (enemy == null || enemy.LifeState.IsDead) continue;
                            float distTiles = CombatAPI.MetersToTiles(Vector3.Distance(enemy.Position, zonePos));
                            if (distTiles <= zoneRadiusTiles)
                                enemyCount++;
                        }
                    }

                    zones.Add(new ZoneInfo
                    {
                        Position = zonePos,
                        ZoneType = zoneType,
                        Radius = zoneRadiusTiles,  // 타일 단위
                        AllyCount = allyCount,
                        EnemyCount = enemyCount,
                        LowHPAllyCount = lowHPAllyCount
                    });

                    if (Main.IsDebugEnabled) Main.LogDebug($"[BuffPlanner] Zone {zoneType} (radius={zoneRadiusTiles:F1} tiles): allies={allyCount}, enemies={enemyCount}, lowHP={lowHPAllyCount}");
                }
            }
            catch (Exception e)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[BuffPlanner] Error getting zone info: {e.Message}");
            }

            return zones;
        }

        /// <summary>
        /// ★ v3.6.2: 존 정보 클래스 (타일 단위로 통일)
        /// </summary>
        private class ZoneInfo
        {
            public Vector3 Position { get; set; }
            public string ZoneType { get; set; }
            public float Radius { get; set; }  // 타일 단위
            public int AllyCount { get; set; }
            public int EnemyCount { get; set; }
            public int LowHPAllyCount { get; set; }
        }

        /// <summary>
        /// PostAction (Run and Gun)
        /// ★ v3.5.80: attackPlanned 파라미터 추가 - 공격이 계획됨도 허용
        /// </summary>
        public static PlannedAction PlanPostAction(Situation situation, ref float remainingAP, string roleName, bool attackPlanned = false)
        {
            var runAndGun = situation.RunAndGunAbility;
            if (runAndGun == null) return null;

            // ★ v3.5.80: 공격이 이미 실행됨 OR 공격이 계획됨
            if (!situation.HasPerformedFirstAction && !attackPlanned) return null;

            float cost = CombatAPI.GetAbilityAPCost(runAndGun);
            if (cost > remainingAP) return null;

            var target = new TargetWrapper(situation.Unit);
            string reason;
            if (CombatAPI.CanUseAbilityOn(runAndGun, target, out reason))
            {
                remainingAP -= cost;
                if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] Phase 6: Planning {runAndGun.Name} (attackPlanned={attackPlanned})");
                return PlannedAction.Buff(runAndGun, situation.Unit, "Run and Gun", cost);
            }

            return null;
        }

        /// <summary>
        /// 턴 종료 능력
        /// ★ v3.0.88: 디버그 로깅 추가
        /// ★ v3.0.89: PointTarget 능력 지원 (VeilOfBlades 등)
        /// ★ v3.5.15: 그룹 쿨다운 체크 추가 (WeaponAttackAbilityGroup 등)
        /// </summary>
        public static PlannedAction PlanTurnEndingAbility(Situation situation, ref float remainingAP, string roleName)
        {
            var turnEndingAbilities = situation.AvailableBuffs
                .Where(a => AbilityDatabase.IsTurnEnding(a))
                .ToList();

            // ★ v3.0.88: 디버그 로깅
            if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanTurnEnding: TurnEndingAbilities={turnEndingAbilities.Count}, AP={remainingAP:F1}");

            if (turnEndingAbilities.Count == 0)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanTurnEnding: No TurnEnding abilities in AvailableBuffs");
                return null;
            }

            if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanTurnEnding: Found: {string.Join(", ", turnEndingAbilities.Select(a => a.Name))}");

            // ★ v3.5.15: 그룹 쿨다운으로 인해 사용 불가능한 능력 필터링
            turnEndingAbilities = turnEndingAbilities
                .Where(a => !CombatAPI.IsAbilityOnCooldownWithGroups(a))
                .ToList();

            if (turnEndingAbilities.Count == 0)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanTurnEnding: All TurnEnding abilities on cooldown (including group cooldowns)");
                return null;
            }

            foreach (var ability in turnEndingAbilities)
            {
                float cost = CombatAPI.GetAbilityAPCost(ability);
                if (cost > remainingAP)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanTurnEnding: {ability.Name} skipped - AP cost {cost:F1} > remaining {remainingAP:F1}");
                    continue;
                }

                // ★ v3.5.22: SpringAttack 능력(Acrobatic Artistry) 조건 체크
                // 갭클로저 사용 이력이 있거나 시작 위치에서 이동한 경우에만 사용
                if (AbilityDatabase.IsSpringAttackAbility(ability))
                {
                    if (!CombatAPI.CanUseSpringAttackAbility(situation.Unit))
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanTurnEnding: {ability.Name} skipped - no gap closer used and at start position");
                        continue;
                    }
                    Main.Log($"[{roleName}] SpringAttack condition met - can use {ability.Name}");
                }

                // ★ v3.0.89: PointTarget vs SelfTarget 분기
                // VeilOfBlades 등: CanTargetPoint=True, CanTargetSelf=False → 위치 타겟
                // ★ v3.1.28: CanTargetSelf=False인 경우 자기 위치 대신 오프셋 위치 사용
                bool isPointTarget = ability.Blueprint?.CanTargetPoint == true && ability.Blueprint?.CanTargetSelf != true;
                bool canTargetSelf = ability.Blueprint?.CanTargetSelf ?? true;

                Vector3 targetPoint = situation.Unit.Position;
                if (isPointTarget && !canTargetSelf)
                {
                    // ★ v3.1.28: 적 방향으로 1.5m 오프셋 (CannotTargetSelf 회피)
                    var nearestEnemy = situation.NearestEnemy;
                    if (nearestEnemy != null)
                    {
                        var direction = (nearestEnemy.Position - situation.Unit.Position).normalized;
                        if (direction.sqrMagnitude > 0.01f)
                        {
                            targetPoint = situation.Unit.Position + direction * 1.5f;
                        }
                        else
                        {
                            targetPoint = situation.Unit.Position + Vector3.forward * 1.5f;
                        }
                    }
                    else
                    {
                        targetPoint = situation.Unit.Position + Vector3.forward * 1.5f;
                    }
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanTurnEnding: {ability.Name} using offset point ({targetPoint.x:F1},{targetPoint.z:F1})");
                }

                TargetWrapper target = isPointTarget ? new TargetWrapper(targetPoint) : new TargetWrapper(situation.Unit);
                if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanTurnEnding: {ability.Name} isPointTarget={isPointTarget}, canTargetSelf={canTargetSelf}");

                string reason;
                if (CombatAPI.CanUseAbilityOn(ability, target, out reason))
                {
                    remainingAP -= cost;
                    Main.Log($"[{roleName}] Turn ending: {ability.Name}");

                    // ★ v3.0.89: PointTarget이면 PositionalAction 반환
                    // ★ v3.1.28: targetPoint 사용 (오프셋 적용된 위치)
                    if (isPointTarget)
                    {
                        return PlannedAction.PositionalAttack(ability, targetPoint, "Turn ending ability (point)", cost);
                    }
                    return PlannedAction.Buff(ability, situation.Unit, "Turn ending ability", cost);
                }
                else
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanTurnEnding: {ability.Name} CanUseAbilityOn=false, reason={reason}");
                }
            }

            if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanTurnEnding: All abilities failed");
            return null;
        }

        #region Helper Methods

        public static bool IsEssentialBuff(AbilityData ability, Situation situation)
        {
            if (ability == null) return false;

            // ★ v3.8.61: String 매칭 제거 → AbilityDatabase API 전용
            // "heal" → IsHealing(), "endure" → IsDefensiveStance (Endure에 플래그 추가됨)
            if (situation.IsHPCritical)
            {
                if (AbilityDatabase.IsHealing(ability) ||
                    AbilityDatabase.IsDefensiveStance(ability))
                    return true;
            }

            var role = situation.CharacterSettings?.Role ?? AIRole.Auto;
            if (role == AIRole.Tank)
            {
                if (AbilityDatabase.IsDefensiveStance(ability))
                    return true;
            }

            return false;
        }

        public static bool CanAffordBuffWithReservation(float buffCost, float remainingAP, float reservedAP, bool isEssential)
        {
            // ★ v3.8.38: 0 코스트 능력은 항상 허용 (WarhammerFreeUltimateBuff 궁극기 등)
            if (buffCost <= 0f)
                return true;

            if (isEssential)
                return buffCost <= remainingAP;

            return buffCost <= (remainingAP - reservedAP);
        }

        private static Vector3 CalculateEnemyCenter(Situation situation)
        {
            var livingEnemies = situation.Enemies.Where(e => e != null && !e.LifeState.IsDead).ToList();
            if (livingEnemies.Count > 0)
            {
                return CalculateAveragePosition(livingEnemies);
            }
            else
            {
                var forward = situation.Unit.Forward;
                if (forward == Vector3.zero) forward = Vector3.forward;
                return situation.Unit.Position + forward * 20f;
            }
        }

        private static Vector3 CalculateAveragePosition(IEnumerable<BaseUnitEntity> units)
        {
            var list = units.ToList();
            if (list.Count == 0) return Vector3.zero;

            Vector3 sum = Vector3.zero;
            foreach (var unit in list)
            {
                sum += unit.Position;
            }
            return sum / list.Count;
        }

        /// <summary>
        /// ★ v3.5.91: AbilityData에서 존 타입 enum 조회 (텍스트 매칭 제거)
        /// </summary>
        private static StrategistTacticsAreaEffectType? GetZoneTypeFromAbility(AbilityData ability)
        {
            if (ability?.Blueprint == null) return null;

            try
            {
                var spawnAction = ability.Blueprint.ElementsArray?
                    .OfType<ContextActionSpawnAreaEffect>()
                    .FirstOrDefault();

                if (spawnAction?.AreaEffect == null) return null;

                return spawnAction.AreaEffect.TacticsAreaEffectType;
            }
            catch
            {
                return null;
            }
        }

        // ★ v3.5.91: Stratagem GUID 기반 타입 매핑 (텍스트 매칭 제거)
        private static readonly Dictionary<string, string> StratagemGuidToType = new Dictionary<string, string>
        {
            { "7005fbf810a64264893cd18fc0187b39", "Blitz" },
            { "b6fa6a9130a64255933ca0144f28dd03", "CombatLocus" },
            { "ab86bcee2036424c90dd12c2ad3fab39", "Killzone" },
            { "7a5637714948456686eeaafa37f51813", "Overwhelming" },
            { "111f6e8111ae4d30a9d5d6d06027281d", "Stronghold" },
            { "0e89f6eda1ae4960aeebfed0737289a3", "Trenchline" }
        };

        // ★ v3.5.91: Stratagem GUID 기반 우선순위 매핑
        private static readonly Dictionary<string, int> StratagemGuidToPriority = new Dictionary<string, int>
        {
            { "ab86bcee2036424c90dd12c2ad3fab39", 2 },  // Killzone
            { "7a5637714948456686eeaafa37f51813", 3 },  // Overwhelming
            { "b6fa6a9130a64255933ca0144f28dd03", 4 },  // CombatLocus
            { "111f6e8111ae4d30a9d5d6d06027281d", 5 },  // Stronghold
            { "0e89f6eda1ae4960aeebfed0737289a3", 6 },  // Trenchline
            { "7005fbf810a64264893cd18fc0187b39", 7 }   // Blitz
        };

        // ★ v3.5.91: HP가 낮을 때 우선순위가 높아지는 Stratagem GUIDs
        private static readonly HashSet<string> DefensiveStratagemGuids = new HashSet<string>
        {
            "111f6e8111ae4d30a9d5d6d06027281d",  // Stronghold
            "0e89f6eda1ae4960aeebfed0737289a3"   // Trenchline
        };

        private static int GetStratagemPriority(AbilityData stratagem, Situation situation)
        {
            string guid = stratagem.Blueprint?.AssetGuid?.ToString();
            if (guid == null) return 10;

            // HP가 낮을 때 방어 Stratagem 우선
            if (situation.HPPercent < 50f && DefensiveStratagemGuids.Contains(guid))
                return 1;

            if (StratagemGuidToPriority.TryGetValue(guid, out int priority))
                return priority;

            return 10;
        }

        private static string GetStratagemType(AbilityData stratagem)
        {
            string guid = stratagem.Blueprint?.AssetGuid?.ToString();
            if (guid != null && StratagemGuidToType.TryGetValue(guid, out var type))
                return type;

            return "Stratagem";
        }

        #endregion
    }
}
