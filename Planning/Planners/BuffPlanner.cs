using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
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
                Main.LogDebug($"[{roleName}] Skip buff {buff.Name}: cost={cost:F1}, remaining={remainingAP:F1}, reserved={reservedAP:F1}");
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

                string bpName = ability.Blueprint?.name?.ToLower() ?? "";
                if (!bpName.Contains("defensive") && !bpName.Contains("stance") &&
                    !bpName.Contains("bulwark") && !bpName.Contains("guard"))
                    continue;

                float cost = CombatAPI.GetAbilityAPCost(ability);

                bool isEssential = IsEssentialBuff(ability, situation);
                if (!CanAffordBuffWithReservation(cost, remainingAP, reservedAP, isEssential))
                    continue;

                if (CombatAPI.HasActiveBuff(situation.Unit, ability)) continue;

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
                Main.LogDebug($"[{roleName}] PlanAttackBuff skipped: No available attacks");
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

                if (CombatAPI.HasActiveBuff(situation.Unit, buff)) continue;

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

                if (CombatAPI.HasActiveBuff(situation.Unit, taunt)) continue;

                TargetWrapper target;
                if (taunt.Blueprint?.CanTargetSelf == true)
                {
                    target = new TargetWrapper(situation.Unit);
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

                if (CombatAPI.HasActiveBuff(situation.Unit, heroic)) continue;

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

                if (CombatAPI.HasActiveBuff(target, debuff)) continue;

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

                if (CombatAPI.HasActiveBuff(target, marker)) continue;

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

            var defensiveBuffs = situation.AvailableBuffs
                .Where(a => !CombatAPI.HasActiveBuff(situation.Unit, a))
                .Where(a => {
                    string bpName = a.Blueprint?.name?.ToLower() ?? "";
                    return bpName.Contains("defensive") || bpName.Contains("stance") ||
                           bpName.Contains("guard") || bpName.Contains("bulwark");
                })
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
        /// ★ v3.5.23: 역할 기반 구역 배치
        /// - Frontline → Tank or Melee DPS
        /// - Backline → Support or Ranged DPS
        /// - Rear → Ranged DPS or Support
        /// </summary>
        public static PlannedAction PlanPositionalBuff(Situation situation, ref float remainingAP, HashSet<string> usedBuffGuids, string roleName)
        {
            var positionalBuffs = situation.AvailablePositionalBuffs;
            if (positionalBuffs == null || positionalBuffs.Count == 0) return null;

            var allies = situation.Allies.Where(a => a != null && !a.LifeState.IsDead).ToList();
            allies.Add(situation.Unit);

            if (allies.Count == 0) return null;

            // ★ v3.5.23: 역할 기반 구역 위치 계산
            var zonePositions = CalculateRoleBasedZonePositions(allies, situation);
            Vector3 frontlinePos = zonePositions.frontline;
            Vector3 backlinePos = zonePositions.backline;
            Vector3 rearPos = zonePositions.rear;

            Main.LogDebug($"[{roleName}] Zone positions - Frontline: {frontlinePos}, Backline: {backlinePos}, Rear: {rearPos}");

            foreach (var buff in positionalBuffs)
            {
                string buffGuid = buff.Blueprint?.AssetGuid?.ToString() ?? buff.Name;
                if (usedBuffGuids != null && usedBuffGuids.Contains(buffGuid))
                    continue;

                float cost = CombatAPI.GetAbilityAPCost(buff);
                if (cost > remainingAP) continue;

                string bpName = buff.Blueprint?.name?.ToLower() ?? "";
                Vector3 preferredPosition;
                string zoneType;

                if (bpName.Contains("frontline") || bpName.Contains("front"))
                {
                    preferredPosition = frontlinePos;
                    zoneType = "Frontline";
                }
                else if (bpName.Contains("rear"))
                {
                    preferredPosition = rearPos;
                    zoneType = "Rear";
                }
                else if (bpName.Contains("backline") || bpName.Contains("back"))
                {
                    preferredPosition = backlinePos;
                    zoneType = "Backline";
                }
                else
                {
                    preferredPosition = CalculateAveragePosition(allies);
                    zoneType = "Zone";
                }

                // ★ v3.5.23: 전략가 구역 겹침 체크 및 대체 위치 찾기
                Vector3 targetPosition = preferredPosition;
                if (CombatAPI.IsStrategistZoneAbility(buff))
                {
                    // 겹침 체크
                    if (CombatAPI.IsPositionTooCloseToExistingZones(preferredPosition, 5f))
                    {
                        var nonOverlappingPos = CombatAPI.FindNonOverlappingZonePosition(buff, preferredPosition, 10f);
                        if (nonOverlappingPos.HasValue)
                        {
                            targetPosition = nonOverlappingPos.Value;
                            Main.Log($"[{roleName}] PositionalBuff: {buff.Name} adjusted position to avoid overlap");
                        }
                        else
                        {
                            Main.LogDebug($"[{roleName}] PositionalBuff: {buff.Name} skipped - no non-overlapping position found");
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
                    Main.Log($"[{roleName}] PositionalBuff: {buff.Name} ({zoneType}) at ({targetPosition.x:F1}, {targetPosition.z:F1})");
                    return PlannedAction.PositionalBuff(buff, targetPosition, $"{zoneType} zone", cost);
                }
            }

            return null;
        }

        /// <summary>
        /// ★ v3.5.23: 역할 기반 구역 위치 계산
        /// - Frontline → Tank or Melee DPS (최전방)
        /// - Backline → Support or Ranged DPS
        /// - Rear → Ranged DPS or Support (후방)
        /// </summary>
        private static (Vector3 frontline, Vector3 backline, Vector3 rear) CalculateRoleBasedZonePositions(
            List<BaseUnitEntity> allies, Situation situation)
        {
            var tankOrMelee = new List<BaseUnitEntity>();
            var supportOrRanged = new List<BaseUnitEntity>();

            foreach (var ally in allies)
            {
                var settings = ModSettings.Instance?.GetOrCreateSettings(ally.UniqueId, ally.CharacterName);
                var role = settings?.Role ?? AIRole.Auto;
                var rangePreference = settings?.RangePreference ?? RangePreference.Adaptive;

                // Auto 역할은 RoleDetector로 감지
                if (role == AIRole.Auto)
                {
                    role = RoleDetector.DetectOptimalRole(ally);
                }

                // Tank or Melee DPS → Frontline 후보
                if (role == AIRole.Tank ||
                    (role == AIRole.DPS && rangePreference == RangePreference.PreferMelee))
                {
                    tankOrMelee.Add(ally);
                }
                // Support or Ranged DPS → Backline/Rear 후보
                else if (role == AIRole.Support ||
                         (role == AIRole.DPS && rangePreference == RangePreference.PreferRanged))
                {
                    supportOrRanged.Add(ally);
                }
                // Adaptive DPS → 현재 위치 기준 (적에게 가까우면 전방, 멀면 후방)
                else
                {
                    var enemyCenter = CalculateEnemyCenter(situation);
                    var allyCenter = CalculateAveragePosition(allies);
                    float distToEnemy = Vector3.Distance(ally.Position, enemyCenter);
                    float avgDist = Vector3.Distance(allyCenter, enemyCenter);

                    if (distToEnemy < avgDist)
                        tankOrMelee.Add(ally);
                    else
                        supportOrRanged.Add(ally);
                }
            }

            // Fallback: 빈 리스트면 전체 아군 사용
            if (tankOrMelee.Count == 0)
            {
                // 적에게 가장 가까운 아군을 전방으로
                var enemyCenter = CalculateEnemyCenter(situation);
                var nearest = allies.OrderBy(a => Vector3.Distance(a.Position, enemyCenter)).FirstOrDefault();
                if (nearest != null) tankOrMelee.Add(nearest);
            }
            if (supportOrRanged.Count == 0)
            {
                // 적에게 가장 먼 아군을 후방으로
                var enemyCenter = CalculateEnemyCenter(situation);
                var farthest = allies.OrderByDescending(a => Vector3.Distance(a.Position, enemyCenter)).FirstOrDefault();
                if (farthest != null) supportOrRanged.Add(farthest);
            }

            // 위치 계산
            Vector3 frontlinePos = CalculateAveragePosition(tankOrMelee);
            Vector3 rearPos = CalculateAveragePosition(supportOrRanged);

            // Backline = Frontline과 Rear 중간 (약간 Rear 쪽)
            // Support/Ranged DPS가 있으면 그들 위치, 없으면 중간점
            Vector3 backlinePos;
            if (supportOrRanged.Count > 0)
            {
                // 후방 캐릭터들 중 적에게 가장 가까운 쪽을 Backline으로
                var enemyCenter = CalculateEnemyCenter(situation);
                var closerSupport = supportOrRanged.OrderBy(a => Vector3.Distance(a.Position, enemyCenter)).FirstOrDefault();
                backlinePos = closerSupport != null ? closerSupport.Position : (frontlinePos + rearPos) / 2f;
            }
            else
            {
                backlinePos = (frontlinePos + rearPos) / 2f;
            }

            // 로깅
            string tankNames = string.Join(", ", tankOrMelee.Select(a => a.CharacterName));
            string supportNames = string.Join(", ", supportOrRanged.Select(a => a.CharacterName));
            Main.LogDebug($"[BuffPlanner] Role-based zones: Frontline=[{tankNames}], Backline/Rear=[{supportNames}]");

            return (frontlinePos, backlinePos, rearPos);
        }

        /// <summary>
        /// Stratagem 계획
        /// </summary>
        public static PlannedAction PlanStratagem(Situation situation, ref float remainingAP, string roleName)
        {
            var stratagems = situation.AvailableStratagems;
            if (stratagems == null || stratagems.Count == 0) return null;

            var strategistZonePositions = FindStrategistZonePositions(situation.Unit);
            if (strategistZonePositions.Count == 0) return null;

            var sortedStratagems = stratagems
                .OrderBy(s => GetStratagemPriority(s, situation))
                .ToList();

            foreach (var stratagem in sortedStratagems)
            {
                float cost = CombatAPI.GetAbilityAPCost(stratagem);
                if (cost > remainingAP) continue;

                foreach (var zonePos in strategistZonePositions)
                {
                    var target = new TargetWrapper(zonePos);
                    string reason;
                    if (CombatAPI.CanUseAbilityOn(stratagem, target, out reason))
                    {
                        remainingAP -= cost;
                        string stratagemType = GetStratagemType(stratagem);
                        Main.Log($"[{roleName}] Stratagem: {stratagem.Name} ({stratagemType})");
                        return PlannedAction.PositionalBuff(stratagem, zonePos, $"Stratagem: {stratagemType}", cost);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// PostAction (Run and Gun)
        /// </summary>
        public static PlannedAction PlanPostAction(Situation situation, ref float remainingAP, string roleName)
        {
            var runAndGun = situation.RunAndGunAbility;
            if (runAndGun == null) return null;

            if (!situation.HasPerformedFirstAction) return null;

            float cost = CombatAPI.GetAbilityAPCost(runAndGun);
            if (cost > remainingAP) return null;

            var target = new TargetWrapper(situation.Unit);
            string reason;
            if (CombatAPI.CanUseAbilityOn(runAndGun, target, out reason))
            {
                remainingAP -= cost;
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
            Main.LogDebug($"[{roleName}] PlanTurnEnding: TurnEndingAbilities={turnEndingAbilities.Count}, AP={remainingAP:F1}");

            if (turnEndingAbilities.Count == 0)
            {
                Main.LogDebug($"[{roleName}] PlanTurnEnding: No TurnEnding abilities in AvailableBuffs");
                return null;
            }

            Main.LogDebug($"[{roleName}] PlanTurnEnding: Found: {string.Join(", ", turnEndingAbilities.Select(a => a.Name))}");

            // ★ v3.5.15: 그룹 쿨다운으로 인해 사용 불가능한 능력 필터링
            turnEndingAbilities = turnEndingAbilities
                .Where(a => !CombatAPI.IsAbilityOnCooldownWithGroups(a))
                .ToList();

            if (turnEndingAbilities.Count == 0)
            {
                Main.LogDebug($"[{roleName}] PlanTurnEnding: All TurnEnding abilities on cooldown (including group cooldowns)");
                return null;
            }

            foreach (var ability in turnEndingAbilities)
            {
                float cost = CombatAPI.GetAbilityAPCost(ability);
                if (cost > remainingAP)
                {
                    Main.LogDebug($"[{roleName}] PlanTurnEnding: {ability.Name} skipped - AP cost {cost:F1} > remaining {remainingAP:F1}");
                    continue;
                }

                // ★ v3.5.22: SpringAttack 능력(Acrobatic Artistry) 조건 체크
                // 갭클로저 사용 이력이 있거나 시작 위치에서 이동한 경우에만 사용
                if (AbilityDatabase.IsSpringAttackAbility(ability))
                {
                    if (!CombatAPI.CanUseSpringAttackAbility(situation.Unit))
                    {
                        Main.LogDebug($"[{roleName}] PlanTurnEnding: {ability.Name} skipped - no gap closer used and at start position");
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
                    Main.LogDebug($"[{roleName}] PlanTurnEnding: {ability.Name} using offset point ({targetPoint.x:F1},{targetPoint.z:F1})");
                }

                TargetWrapper target = isPointTarget ? new TargetWrapper(targetPoint) : new TargetWrapper(situation.Unit);
                Main.LogDebug($"[{roleName}] PlanTurnEnding: {ability.Name} isPointTarget={isPointTarget}, canTargetSelf={canTargetSelf}");

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
                    Main.LogDebug($"[{roleName}] PlanTurnEnding: {ability.Name} CanUseAbilityOn=false, reason={reason}");
                }
            }

            Main.LogDebug($"[{roleName}] PlanTurnEnding: All abilities failed");
            return null;
        }

        #region Helper Methods

        public static bool IsEssentialBuff(AbilityData ability, Situation situation)
        {
            if (ability == null) return false;

            string bpName = ability.Blueprint?.name?.ToLower() ?? "";

            if (situation.IsHPCritical)
            {
                if (bpName.Contains("heal") || bpName.Contains("defensive") ||
                    bpName.Contains("endure") || bpName.Contains("stance"))
                    return true;
            }

            var role = situation.CharacterSettings?.Role ?? AIRole.Auto;
            if (role == AIRole.Tank)
            {
                if (bpName.Contains("defensive") || bpName.Contains("stance"))
                    return true;
            }

            return false;
        }

        public static bool CanAffordBuffWithReservation(float buffCost, float remainingAP, float reservedAP, bool isEssential)
        {
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

        private static List<Vector3> FindStrategistZonePositions(BaseUnitEntity caster)
        {
            var positions = new List<Vector3>();
            try
            {
                var areaEffects = Game.Instance?.State?.AreaEffects;
                if (areaEffects == null) return positions;

                foreach (var areaEffect in areaEffects)
                {
                    var bp = areaEffect.Blueprint;
                    if (bp == null || !bp.IsStrategistAbility) continue;

                    var context = areaEffect.Context;
                    if (context?.MaybeCaster != caster) continue;

                    positions.Add(areaEffect.View?.ViewTransform?.position ?? areaEffect.Position);
                }
            }
            catch (Exception e)
            {
                Main.LogDebug($"[BuffPlanner] Error finding strategist zones: {e.Message}");
            }

            return positions;
        }

        private static int GetStratagemPriority(AbilityData stratagem, Situation situation)
        {
            string bpName = stratagem.Blueprint?.name?.ToLower() ?? "";

            if (situation.HPPercent < 50f)
            {
                if (bpName.Contains("stronghold") || bpName.Contains("trenchline"))
                    return 1;
            }

            if (bpName.Contains("killzone")) return 2;
            if (bpName.Contains("overwhelming")) return 3;
            if (bpName.Contains("combatlocus") || bpName.Contains("combat_locus")) return 4;
            if (bpName.Contains("stronghold")) return 5;
            if (bpName.Contains("trenchline")) return 6;
            if (bpName.Contains("blitz")) return 7;

            return 10;
        }

        private static string GetStratagemType(AbilityData stratagem)
        {
            string bpName = stratagem.Blueprint?.name?.ToLower() ?? "";

            if (bpName.Contains("killzone")) return "Killzone";
            if (bpName.Contains("overwhelming")) return "Overwhelming";
            if (bpName.Contains("combatlocus") || bpName.Contains("combat_locus")) return "CombatLocus";
            if (bpName.Contains("stronghold")) return "Stronghold";
            if (bpName.Contains("trenchline")) return "Trenchline";
            if (bpName.Contains("blitz")) return "Blitz";

            return "Stratagem";
        }

        #endregion
    }
}
