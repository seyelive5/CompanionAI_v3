using System;
using System.Collections.Generic;
using Kingmaker.Designers.Mechanics.Facts;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Enums;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using UnityEngine;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning.Plans
{
    public abstract partial class BasePlan
    {
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
        /// <param name="overrideCheckPosition">★ v3.10.0: 아군 범위 체크 위치 오버라이드.
        /// null이면 optimalPos.Position 사용 (기존 동작).
        /// 디버프 모드에서 재배치 전 사역마 현재 위치를 전달하여 아군 근처에서 버프 전달.</param>
        protected List<PlannedAction> PlanAllFamiliarKeystoneBuffs(Situation situation, ref float remainingAP, bool heroicActPlanned = false, Vector3? overrideCheckPosition = null)
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
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Keystone Loop: No optimal position");
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
                            if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Keystone Loop: Found {attack.Name} in AvailableAttacks for Warp Relay");
                        }
                    }
                }

                if (keystoneDebuffs.Count > 0)
                {
                    if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Keystone Loop: {keystoneDebuffs.Count} debuffs eligible for Warp Relay");
                }
            }

            // 버프/디버프 모두 없으면 종료
            if (keystoneBuffs.Count == 0 && keystoneDebuffs.Count == 0)
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Keystone Loop: No keystone-eligible abilities found");
                return actions;
            }

            // 사용된 능력 추적
            var usedAbilityGuids = new HashSet<string>();
            var familiarTarget = new TargetWrapper(situation.Familiar);
            var typeName = FamiliarAPI.GetFamiliarTypeName(situation.FamiliarType);

            // ★ v3.7.22: 범위 내 실제 아군 목록 (버프 중복 체크용)
            // ★ v3.8.78: LINQ → CollectionHelper (0 할당)
            // ★ v3.10.0: overrideCheckPosition이 주어지면 해당 위치에서 아군 체크
            //   디버프 모드에서 재배치 전 사역마 현재 위치 사용 → 아군 근처에서 버프 가능
            CollectionHelper.FillWhere(situation.Allies, _tempUnits,
                a => a.IsConscious && !FamiliarAPI.IsFamiliar(a));
            Vector3 checkPosition = overrideCheckPosition ?? optimalPos.Position;
            // ★ v3.18.14: 실제 키스톤 AoE 반경 사용 (EFFECT_RADIUS_TILES=4 → 실제 능력 AoE)
            var alliesInRange = FamiliarAPI.GetAlliesInRadius(
                checkPosition,
                situation.FamiliarEffectRadius,
                _tempUnits);

            // ★ v3.10.0: 스냅샷(optimalPos.AlliesInRange) 대신 fresh 계산(alliesInRange.Count) 사용
            // optimalPos.AlliesInRange는 분석 프레임에서 계산된 스냅샷 — 재배치/이동 후 stale 가능
            if (keystoneBuffs.Count > 0 && alliesInRange.Count >= 1)
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
                        if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Keystone Loop: {buff.Name} skipped - no allies need it (all {alliesInRange.Count} already have it)");
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
                            if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Keystone Loop: {buff.Name} blocked (point target) - {reason}");
                            continue;
                        }
                    }
                    else
                    {
                        // 유닛 타겟은 사역마에게 시전 가능한지 확인
                        if (!CombatAPI.CanUseAbilityOn(buff, familiarTarget, out reason))
                        {
                            if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Keystone Loop: {buff.Name} blocked - {reason}");
                            continue;
                        }
                    }

                    remainingAP -= cost;
                    if (!string.IsNullOrEmpty(guid))
                        usedAbilityGuids.Add(guid);

                    Log.Planning.Info($"[{RoleName}] ★ Familiar Keystone Buff: {buff.Name} on {typeName} " +
                        $"({alliesNeedingBuff}/{alliesInRange.Count} allies need buff)" +
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
                        if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Keystone Point AOE: {buff.Name} at ({familiarPos.x:F1}, {familiarPos.z:F1})");
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
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Keystone: Momentum check - heroicActPlanned={heroicActPlanned}, buffActive={buffActive}, hasMomentum={hasMomentum}");
            }

            // ★ v3.8.52: 턴 단위 페이즈 기반 디버프 제어
            // 버프 페이즈: Raven이 아군 근처 → 디버프 시전해도 Warp Relay가 적에게 도달 불가 → 스킵
            // 공격 페이즈: Raven이 적 근처로 재배치됨 → 디버프 Warp Relay가 적에게 확산
            bool isRavenBuffPhase = optimalPos.IsBuffPhase;
            if (situation.FamiliarType == PetType.Raven && keystoneDebuffs.Count > 0)
            {
                Log.Planning.Info($"[{RoleName}] Raven Phase: {(isRavenBuffPhase ? "BUFF (아군 버프 우선)" : "DEBUFF (적 디버프 전환)")}");
            }

            // ★ 비피해 디버프 처리 (Momentum 불필요) - 적 2명+ 필요
            // ★ v3.8.52: 버프 페이즈에서는 디버프 완전 스킵 (Raven이 아군 근처이므로 무의미)
            // ★ v3.8.53: optimalPos.EnemiesInRange는 재배치 예정(NeedsFamiliarRelocate)일 때만 사용
            //   - 재배치가 Phase 3.3에서 먼저 실행되므로, 디버프 실행 시 Raven은 최적 위치에 있음
            //   - 재배치 없이 optimalPos만 보면 Raven이 아군 근처인데 디버프가 계획되는 버그 발생
            int actualEnemiesNearRaven = 0;
            bool hasEnoughEnemiesForDebuff = false;
            bool willRelocateForDebuff = false;     // ★ v3.18.10: 디버프 루프에서 사용 (hoisted)
            Vector3 ravenPosForDebuff = default;    // ★ v3.18.10: per-ability AoE 체크용 (hoisted)
            if (!isRavenBuffPhase && keystoneDebuffs.Count > 0 && situation.Familiar != null)
            {
                ravenPosForDebuff = situation.Familiar.Position;
                // ★ v3.8.78: LINQ → CollectionHelper (0 할당)
                CollectionHelper.FillWhere(situation.Enemies, _tempUnits,
                    e => e.IsConscious);
                // ★ v3.18.14: 실제 키스톤 AoE 반경 사용
                actualEnemiesNearRaven = FamiliarAPI.CountEnemiesInRadius(
                    ravenPosForDebuff, situation.FamiliarEffectRadius, _tempUnits);

                // ★ v3.8.53: 재배치 예정 여부에 따라 적 수 판단
                // NeedsFamiliarRelocate=true → Phase 3.3에서 최적 위치로 이동 예정 → optimalPos 기준 사용 가능
                // NeedsFamiliarRelocate=false → Raven은 현재 위치에 머물 → 현재 위치 기준만 사용
                willRelocateForDebuff = situation.NeedsFamiliarRelocate;
                int effectiveEnemyEstimate = willRelocateForDebuff
                    ? Math.Max(actualEnemiesNearRaven, optimalPos.EnemiesInRange)
                    : actualEnemiesNearRaven;

                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Keystone Debuff: Enemies near Raven current={actualEnemiesNearRaven}, " +
                    $"optimal={optimalPos.EnemiesInRange}, willRelocate={willRelocateForDebuff}, effective={effectiveEnemyEstimate}");
                // ★ v3.8.56: 적 1명이라도 있으면 디버프 허용 (사람처럼 일단 뭐라도 하기)
                hasEnoughEnemiesForDebuff = effectiveEnemyEstimate >= 1;
            }
            else if (isRavenBuffPhase && keystoneDebuffs.Count > 0)
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Keystone Debuff: Skipped (Raven in BUFF phase - prioritizing ally buff distribution)");
            }

            if (keystoneDebuffs.Count > 0 && hasEnoughEnemiesForDebuff)
            {
                // ★ v3.18.10: willRelocate 기반 정확한 적 수 (로그/설명용)
                int baseEffectiveEnemyCount = willRelocateForDebuff
                    ? Math.Max(actualEnemiesNearRaven, optimalPos.EnemiesInRange)
                    : actualEnemiesNearRaven;
                foreach (var debuff in keystoneDebuffs)
                {
                    if (remainingAP < 1f) break;

                    string guid = debuff.Blueprint?.AssetGuid?.ToString();
                    if (!string.IsNullOrEmpty(guid) && usedAbilityGuids.Contains(guid))
                        continue;

                    float cost = CombatAPI.GetAbilityAPCost(debuff);
                    if (cost > remainingAP) continue;

                    // ★ v3.18.14: Per-ability AoE 반경으로 실제 적중 가능 적 수 검증
                    // FamiliarEffectRadius로 이미 min AoE 기반 적 수 계산됨
                    // 개별 능력 AoE가 더 작으면 → 능력 범위 내 적만 유효
                    int debuffEnemyCount = baseEffectiveEnemyCount;
                    if (!willRelocateForDebuff)
                    {
                        float debuffAoE = CombatAPI.GetAoERadius(debuff);
                        if (debuffAoE > 0)
                        {
                            debuffEnemyCount = FamiliarAPI.CountEnemiesInRadius(
                                ravenPosForDebuff, debuffAoE, _tempUnits);
                            if (debuffEnemyCount < 1)
                            {
                                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Keystone Debuff: {debuff.Name} skipped - " +
                                    $"0 enemies in actual AoE ({debuffAoE:F1} tiles, effectRadius={situation.FamiliarEffectRadius:F1})");
                                continue;
                            }
                        }
                    }

                    // 디버프를 Raven에게 시전 가능한지 확인
                    string reason;
                    if (!CombatAPI.CanUseAbilityOn(debuff, familiarTarget, out reason))
                    {
                        if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Keystone Debuff: {debuff.Name} can't target Raven - {reason}");
                        continue;
                    }

                    remainingAP -= cost;
                    if (!string.IsNullOrEmpty(guid))
                        usedAbilityGuids.Add(guid);

                    Log.Planning.Info($"[{RoleName}] ★ Familiar Keystone Debuff: {debuff.Name} on {typeName} " +
                        $"({debuffEnemyCount} enemies in range) - Warp Relay spread");

                    var debuffAction = PlannedAction.Attack(
                        debuff,
                        situation.Familiar,
                        $"Warp Relay debuff: {debuff.Name} ({debuffEnemyCount} enemies)",
                        cost);
                    debuffAction.IsFamiliarTarget = true;
                    actions.Add(debuffAction);
                }
            }
            else if (keystoneDebuffs.Count > 0 && !isRavenBuffPhase)
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Keystone Debuff: Not enough enemies near Raven (current={actualEnemiesNearRaven}, optimal={optimalPos.EnemiesInRange})");
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

                    // ★ v3.18.10: Per-ability AoE 반경으로 실제 적 수 검증
                    int attackEnemyCount = willRelocateForDebuff
                        ? Math.Max(actualEnemiesNearRaven, optimalPos.EnemiesInRange)
                        : actualEnemiesNearRaven;
                    if (!willRelocateForDebuff)
                    {
                        float attackAoE = CombatAPI.GetAoERadius(attack);
                        if (attackAoE > 0)
                        {
                            attackEnemyCount = FamiliarAPI.CountEnemiesInRadius(
                                ravenPosForDebuff, attackAoE, _tempUnits);
                            if (attackEnemyCount < 1)
                            {
                                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Warp Relay Attack: {attack.Name} skipped - " +
                                    $"0 enemies in actual AoE ({attackAoE:F1} tiles)");
                                continue;
                            }
                        }
                    }

                    // Raven에게 시전 가능한지 확인
                    string reason;
                    if (!CombatAPI.CanUseAbilityOn(attack, familiarTarget, out reason))
                    {
                        if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Warp Relay Attack: {attack.Name} can't target Raven - {reason}");
                        continue;
                    }

                    remainingAP -= cost;
                    if (!string.IsNullOrEmpty(guid))
                        usedAbilityGuids.Add(guid);

                    Log.Planning.Info($"[{RoleName}] ★ Warp Relay Psychic Attack: {attack.Name} on {typeName} " +
                        $"({attackEnemyCount} enemies) - Momentum active, damage spreads!");

                    var attackAction = PlannedAction.Attack(
                        attack,
                        situation.Familiar,
                        $"Warp Relay attack: {attack.Name} ({attackEnemyCount} enemies)",
                        cost);
                    attackAction.IsFamiliarTarget = true;
                    actions.Add(attackAction);
                }
            }
            else if (hasMomentum && isRavenBuffPhase)
            {
                // ★ v3.8.57: Warp Relay 불가 → Phase 5에서 직접 적 공격으로 폴백
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Warp Relay Attack: Momentum active but in BUFF phase - psychic attacks available as direct cast in Phase 5");
            }
            else if (hasMomentum && !hasEnoughEnemiesForDebuff)
            {
                // ★ v3.8.57: Warp Relay 불가 → Phase 5에서 직접 적 공격으로 폴백
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Warp Relay Attack: Momentum active but no enemies near Raven - psychic attacks available as direct cast in Phase 5");
            }
            else if (!hasMomentum && situation.FamiliarType == PetType.Raven)
            {
                // ★ v3.8.57: Momentum 없어도 사이킹 공격은 Phase 5에서 직접 캐스팅 가능
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] No Momentum: psychic attacks available as direct cast in Phase 5 (no Warp Relay AOE spread)");
            }

            if (actions.Count > 0)
            {
                Log.Planning.Info($"[{RoleName}] Keystone Loop: {actions.Count} abilities planned for familiar");
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
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Familiar Relocate: Mastiff has no Relocate ability");
                return null;
            }

            // Relocate 능력 찾기
            var relocate = CollectionHelper.FirstOrDefault(situation.FamiliarAbilities,
                a => FamiliarAbilities.IsRelocateAbility(a));

            if (relocate == null)
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Familiar Relocate: No relocate ability found");
                return null;
            }

            // AP 비용 확인
            float apCost = CombatAPI.GetAbilityAPCost(relocate);
            if (remainingAP < apCost)
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Familiar Relocate: Not enough AP ({remainingAP:F1} < {apCost:F1})");
                return null;
            }

            // 최적 위치 확인
            var optimalPos = situation.OptimalFamiliarPosition;
            if (optimalPos == null)
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Familiar Relocate: No optimal position");
                return null;
            }

            // LOS/타겟 가능 여부 확인
            string reason;
            if (!CombatAPI.CanUseAbilityOnPoint(relocate, optimalPos.Position, out reason))
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Familiar Relocate blocked: {reason}");
                return null;
            }

            remainingAP -= apCost;

            var typeName = FamiliarAPI.GetFamiliarTypeName(situation.FamiliarType);
            Log.Planning.Info($"[{RoleName}] ★ Familiar Relocate: {typeName} to optimal position " +
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
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Familiar Keystone: Not enough allies in range ({optimalPos?.AlliesInRange ?? 0})");
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
                    if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Familiar Keystone blocked (point): {buffAbility.Name} -> {reason}");
                    return null;
                }
            }
            else
            {
                var familiarTarget = new TargetWrapper(situation.Familiar);
                if (!CombatAPI.CanUseAbilityOn(buffAbility, familiarTarget, out reason))
                {
                    if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Familiar Keystone blocked: {buffAbility.Name} -> {reason}");
                    return null;
                }
            }

            remainingAP -= apCost;

            var typeName = FamiliarAPI.GetFamiliarTypeName(situation.FamiliarType);
            Log.Planning.Info($"[{RoleName}] ★ Familiar Keystone: {buffAbility.Name} on {typeName} " +
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
        /// ★ v3.22.6: 사역마 Apprehend 계획 (Cyber-Mastiff) — 완전 재작성
        /// - TeamBlackboard 기반 대상 고정 (재발행 방지)
        /// - BestTarget 연동 (마스터 공격 대상과 동일 → 연대공격 극대화)
        /// - 도달 가능성 체크 (마스티프가 2-3턴 내 도달 불가한 원거리 적 스킵)
        /// </summary>
        protected PlannedAction PlanFamiliarApprehend(Situation situation, ref float remainingAP)
        {
            if (situation.FamiliarType != PetType.Mastiff) return null;

            var apprehend = CollectionHelper.FirstOrDefault(situation.FamiliarAbilities,
                a => FamiliarAbilities.IsApprehendAbility(a));
            if (apprehend == null) return null;

            float apCost = CombatAPI.GetAbilityAPCost(apprehend);
            if (remainingAP < apCost) return null;

            // ★ v3.22.6: 기존 Apprehend 대상 확인 — 생존 시 재발행 불필요
            string masterId = situation.Unit.UniqueId;
            string existingTargetId = TeamBlackboard.Instance.GetMastiffApprehendTarget(masterId);
            if (existingTargetId != null)
            {
                var existingTarget = CollectionHelper.FirstOrDefault(situation.Enemies,
                    e => e.IsConscious && e.UniqueId == existingTargetId);
                if (existingTarget != null)
                {
                    if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Mastiff Apprehend: Already active on {existingTarget.CharacterName}, skipping");
                    return null;  // 대상 생존 → 재발행 불필요
                }
                // 대상 사망/무효 → clear 후 새 대상 선택
                TeamBlackboard.Instance.ClearMastiffApprehendTarget(masterId);
                Log.Planning.Info($"[{RoleName}] Mastiff Apprehend: Previous target eliminated, selecting new target");
            }

            // ★ v3.22.6: BestTarget 연동 + 도달 가능성 체크
            BaseUnitEntity targetEnemy = null;

            // 1순위: BestTarget (마스터 공격 대상과 일치 → 연대공격 보너스 극대화)
            if (situation.BestTarget != null && situation.BestTarget.IsConscious
                && IsMastiffReachable(situation, situation.BestTarget))
            {
                string reason;
                if (CombatAPI.CanUseAbilityOn(apprehend, new TargetWrapper(situation.BestTarget), out reason))
                {
                    targetEnemy = situation.BestTarget;
                    Log.Planning.Info($"[{RoleName}] Mastiff Apprehend: BestTarget {targetEnemy.CharacterName} (coordinated)");
                }
            }

            // 2순위: HittableEnemies 중 도달 가능한 적 (마스티프 기준 거리순)
            if (targetEnemy == null && situation.HittableEnemies != null)
            {
                float bestDist = float.MaxValue;
                for (int i = 0; i < situation.HittableEnemies.Count; i++)
                {
                    var enemy = situation.HittableEnemies[i];
                    if (!enemy.IsConscious) continue;
                    if (!IsMastiffReachable(situation, enemy)) continue;
                    string reason;
                    if (!CombatAPI.CanUseAbilityOn(apprehend, new TargetWrapper(enemy), out reason)) continue;
                    float dist = Vector3.Distance(situation.Familiar.Position, enemy.Position);
                    if (dist < bestDist) { bestDist = dist; targetEnemy = enemy; }
                }
                if (targetEnemy != null)
                    Log.Planning.Info($"[{RoleName}] Mastiff Apprehend: Reachable hittable {targetEnemy.CharacterName} (dist={CombatAPI.MetersToTiles(bestDist):F1}tiles)");
            }

            // 3순위: NearestEnemy 폴백 (도달 불확실하지만 최선)
            if (targetEnemy == null && situation.NearestEnemy != null && situation.NearestEnemy.IsConscious)
            {
                string reason;
                if (CombatAPI.CanUseAbilityOn(apprehend, new TargetWrapper(situation.NearestEnemy), out reason))
                {
                    targetEnemy = situation.NearestEnemy;
                    Log.Planning.Info($"[{RoleName}] Mastiff Apprehend: NearestEnemy fallback {targetEnemy.CharacterName}");
                }
            }

            if (targetEnemy == null)
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Mastiff Apprehend: No valid reachable target found");
                return null;
            }

            // ★ v3.22.6: Apprehend 대상 기록 (턴 간 보존)
            TeamBlackboard.Instance.SetMastiffApprehendTarget(masterId, targetEnemy.UniqueId);
            remainingAP -= apCost;

            Log.Planning.Info($"[{RoleName}] ★ Mastiff Apprehend: {targetEnemy.CharacterName} (locked until eliminated)");
            return PlannedAction.Attack(apprehend, targetEnemy,
                $"Mastiff Apprehend on {targetEnemy.CharacterName}", apCost);
        }

        /// <summary>
        /// ★ v3.22.6: 마스티프 도달 가능성 체크
        /// 마스티프 MP가 아닌 절대 거리 기준 (pet AI가 이동 관리, 우리 API로 정확한 MP 조회 어려움)
        /// </summary>
        private bool IsMastiffReachable(Situation situation, BaseUnitEntity enemy)
        {
            if (situation.Familiar == null) return false;
            float dist = Vector3.Distance(situation.Familiar.Position, enemy.Position);
            float distTiles = CombatAPI.MetersToTiles(dist);
            return distTiles <= SC.MastiffApprehendMaxReachTiles;
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
            // ★ v3.40.8: 데미지 면역 적 제외 (구조물에 시야 방해 무의미)
            var targetEnemy = CollectionHelper.MaxByWhere(situation.Enemies,
                e => e.IsConscious && !CombatAPI.IsTargetImmuneToDamage(e, situation.Unit),
                e =>
                {
                    int nearbyCount = CollectionHelper.CountWhere(situation.Enemies,
                        other => other.IsConscious && other != e &&
                        CombatCache.GetDistanceInTiles(e, other) <= 3f);
                    return nearbyCount * 10000f + (float)(e.Health?.MaxHitPoints ?? 0);
                });

            if (targetEnemy == null)
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Eagle Obstruct: No suitable target found");
                return null;
            }

            // 타겟 가능 여부 확인
            var targetWrapper = new TargetWrapper(targetEnemy);
            string reason;
            if (!CombatAPI.CanUseAbilityOn(obstruct, targetWrapper, out reason))
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Eagle Obstruct blocked: {reason}");
                return null;
            }

            remainingAP -= apCost;

            Log.Planning.Info($"[{RoleName}] ★ Eagle Obstruct Vision: {targetEnemy.CharacterName}");

            return PlannedAction.Attack(
                obstruct,
                targetEnemy,
                $"Eagle Obstruct Vision on {targetEnemy.CharacterName}",
                apCost);
        }

        /// <summary>
        /// ★ v3.22.6: 사역마 Protect! 계획 (Cyber-Mastiff) — 조건 강화
        /// - Apprehend 활성 시 Protect 스킵 (배타적 명령)
        /// - 근접 적이 위협하는 약한 아군만 보호 (HP &lt; 50%)
        /// - 마스터 자신도 보호 대상 (HP &lt; 60% + 근접 적 존재)
        /// </summary>
        protected PlannedAction PlanFamiliarProtect(Situation situation, ref float remainingAP)
        {
            if (situation.FamiliarType != PetType.Mastiff) return null;

            var protect = CollectionHelper.FirstOrDefault(situation.FamiliarAbilities,
                a => FamiliarAbilities.IsProtectAbility(a));
            if (protect == null) return null;

            float apCost = CombatAPI.GetAbilityAPCost(protect);
            if (remainingAP < apCost) return null;

            // ★ v3.22.6: Apprehend 활성 시 Protect 스킵 (공격 명령과 보호 명령은 배타적)
            string masterId = situation.Unit.UniqueId;
            string apprehendTargetId = TeamBlackboard.Instance.GetMastiffApprehendTarget(masterId);
            if (apprehendTargetId != null)
            {
                var apprehendTarget = CollectionHelper.FirstOrDefault(situation.Enemies,
                    e => e.IsConscious && e.UniqueId == apprehendTargetId);
                if (apprehendTarget != null)
                {
                    if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Mastiff Protect: Skipped — Apprehend active on {apprehendTarget.CharacterName}");
                    return null;  // Apprehend 활성 → Protect 불필요
                }
            }

            // ★ v3.22.6: 조건 강화 — 근접 적이 위협하는 약한 아군만 보호
            // HP < MastiffProtectMaxHP(50%) + 근접 무기 적 3타일 내 존재
            var allyToProtect = CollectionHelper.MinByWhere(situation.Allies,
                a => a.IsConscious && !FamiliarAPI.IsFamiliar(a) && a != situation.Unit
                     && CombatCache.GetHPPercent(a) < SC.MastiffProtectMaxHP
                     && HasNearbyMeleeEnemy(situation, a),
                a => CombatCache.GetHPPercent(a));

            // 마스터 자신도 후보 (HP < 60% + 근접 적 존재)
            if (allyToProtect == null
                && CombatCache.GetHPPercent(situation.Unit) < 60f
                && HasNearbyMeleeEnemy(situation, situation.Unit))
            {
                allyToProtect = situation.Unit;
            }

            if (allyToProtect == null)
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Mastiff Protect: No threatened ally needs protection");
                return null;
            }

            var targetWrapper = new TargetWrapper(allyToProtect);
            string reason;
            if (!CombatAPI.CanUseAbilityOn(protect, targetWrapper, out reason))
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Mastiff Protect blocked: {reason}");
                return null;
            }

            float allyHP = CombatCache.GetHPPercent(allyToProtect);
            remainingAP -= apCost;
            Log.Planning.Info($"[{RoleName}] ★ Mastiff Protect: {allyToProtect.CharacterName} (HP={allyHP:F0}%)");

            return PlannedAction.Buff(protect, allyToProtect,
                $"Mastiff Protect {allyToProtect.CharacterName}", apCost);
        }

        /// <summary>
        /// ★ v3.22.6: 아군 근처에 근접 무기 적이 있는지 확인
        /// Protect 발동 조건 — 원거리 적에게는 마스티프 보호가 무의미
        /// </summary>
        private bool HasNearbyMeleeEnemy(Situation situation, BaseUnitEntity ally)
        {
            return CollectionHelper.Any(situation.Enemies,
                e => e.IsConscious && !CombatAPI.HasRangedWeapon(e)
                     && CombatCache.GetDistanceInTiles(ally, e) <= 3f);
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
            if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Aerial Rush: Entry - FamiliarType={situation.FamiliarType}, " +
                $"FamiliarAbilities={situation.FamiliarAbilities?.Count ?? 0}, AP={remainingAP:F1}");

            // Cyber-Eagle만 해당
            if (situation.FamiliarType != PetType.Eagle)
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Aerial Rush: Skip - Not Eagle (type={situation.FamiliarType})");
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
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Aerial Rush: No MultiTarget ability found. Available abilities:");
                if (situation.FamiliarAbilities != null)
                {
                    foreach (var ab in situation.FamiliarAbilities)
                    {
                        bool isMulti = FamiliarAbilities.IsMultiTargetFamiliarAbility(ab);
                        bool isAerial = FamiliarAbilities.IsAerialRushAbility(ab);
                        if (Main.IsDebugEnabled) Log.Planning.Debug($"  - {ab.Name} [{ab.Blueprint?.AssetGuid}] MultiTarget={isMulti}, AerialRush={isAerial}");
                    }
                }
                return null;
            }

            // AP 비용 확인
            float apCost = CombatAPI.GetAbilityAPCost(aerialRush);
            if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Aerial Rush: Found ability={aerialRush.Name}, APCost={apCost:F1}, RemainingAP={remainingAP:F1}");

            // ★ v3.7.46: 디버그 - TargetRestrictions 덤프
            try
            {
                var restrictions = aerialRush.Blueprint?.TargetRestrictions;
                if (restrictions != null && restrictions.Length > 0)
                {
                    if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Aerial Rush: TargetRestrictions ({restrictions.Length} total):");
                    foreach (var restriction in restrictions)
                    {
                        if (Main.IsDebugEnabled) Log.Planning.Debug($"  - {restriction.GetType().Name}");
                    }
                }
                else
                {
                    if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Aerial Rush: No TargetRestrictions");
                }

                // ★ v3.99.0: AbilityTargetsAround 디버그 블록 제거 — 순수 로그용이었고 해당 타입이 [Obsolete]
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Log.Planning.Error(ex, $"[{RoleName}] Aerial Rush: Error dumping restrictions");
            }

            if (remainingAP < apCost)
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Aerial Rush: Insufficient AP ({remainingAP:F1} < {apCost:F1})");
                return null;
            }

            var masterNode = situation.Unit.Position.GetNearestNodeXZ() as CustomGridNodeBase;
            if (masterNode == null)
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Aerial Rush: Master node is null");
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
                            if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Aerial Rush: Found {comp.GetType().Name} - distance calc uses Pet position");
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

            if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Aerial Rush: Point1={point1RangeTiles} tiles (maxEnemy={maxEnemyDist:F0}), " +
                $"Point2={point2RangeTiles} tiles (from Support ability RangeCells), EagleMP={familiarMP:F0}, OverrideCaster={hasOverrideCasterByPet}");

            // ★ v3.7.48: Eagle 위치 기반 경로 탐색
            // 게임 검증 분석 결과: Point2 검증은 Eagle.Position에서 수행됨
            // → Point1 = Eagle.Position으로 고정해야 TargetRestrictionNotPassed 방지
            CustomGridNodeBase eagleNode = null;
            if (familiar != null)
            {
                eagleNode = situation.FamiliarPosition.GetNearestNodeXZ() as CustomGridNodeBase;
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Aerial Rush: Using Eagle position ({situation.FamiliarPosition.x:F1},{situation.FamiliarPosition.z:F1})");
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
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Aerial Rush: No path from current position, checking Master movement...");

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
                        if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Aerial Rush: Found path after Master moves to ({movePos.x:F1},{movePos.z:F1})");
                    }
                }
            }

            if (!foundPath || bestPoint1Node == null || bestPoint2Node == null)
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Aerial Rush: No valid path found (even with movement)");
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

                // ★ v3.19.8: 이동 위치가 위험 구역이면 Aerial Rush 취소
                if (!situation.NeedsAoEEvacuation &&
                    CombatAPI.IsPositionInHazardZone(masterMovePos, situation.Unit))
                {
                    Log.Planning.Info($"[{RoleName}] Aerial Rush move position in hazard zone — cancelled");
                    return null;
                }

                Log.Planning.Info($"[{RoleName}] ★ Eagle Aerial Rush requires movement: " +
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

            Log.Planning.Info($"[{RoleName}] ★ Eagle Aerial Rush: Point1({point1.x:F1},{point1.z:F1}) -> Point2({point2.x:F1},{point2.z:F1}) through {targetName} ({estimatedPathTargets} enemies in path)");

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
            // ★ v3.40.8: 데미지 면역 적 제외 (구조물 등)
            CollectionHelper.FillWhere(situation.Enemies, _tempUnits,
                e => e.IsConscious && !CombatAPI.IsTargetImmuneToDamage(e, situation.Unit));
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

            Log.Planning.Info($"[{RoleName}] ★ Eagle Blinding Dive: {targetEnemy.CharacterName} ({(isRanged ? "Ranged" : "Melee")}, HP={CombatCache.GetHPPercent(targetEnemy):F0}%)");

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
            // ★ v3.40.8: 데미지 면역 적 제외 (구조물 등)
            CollectionHelper.FillWhere(situation.Enemies, _tempUnits,
                e => e.IsConscious && !CombatAPI.IsTargetImmuneToDamage(e, situation.Unit));
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
                            if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Jump Claws cluster target: {nearbyCount} nearby");
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

            Log.Planning.Info($"[{RoleName}] ★ Mastiff Jump Claws: {targetEnemy.CharacterName} (HP={CombatCache.GetHPPercent(targetEnemy):F0}%)");

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
            // ★ v3.40.8: 데미지 면역 적 제외 (구조물 등)
            CollectionHelper.FillWhere(situation.Enemies, _tempUnits,
                e => e.IsConscious && !CombatAPI.IsTargetImmuneToDamage(e, situation.Unit));
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

            Log.Planning.Info($"[{RoleName}] ★ {familiarName} Claws: {targetEnemy.CharacterName} (HP={CombatCache.GetHPPercent(targetEnemy):F0}%)");

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
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Eagle Screen blocked: {reason}");
                return null;
            }

            remainingAP -= apCost;

            Log.Planning.Info($"[{RoleName}] ★ Eagle Screen: {allyToScreen.CharacterName} (HP={allyHP:F0}%)");

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
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Priority Signal blocked: {reason}");
                return null;
            }

            remainingAP -= apCost;
            Log.Planning.Info($"[{RoleName}] ★ Servo-Skull Priority Signal");

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

            // ★ v3.18.14: 능력 실제 AoE 반경 사용 (EFFECT_RADIUS_TILES 하드코딩 제거)
            float signalRadius = CombatAPI.GetAoERadius(signal);
            float signalCheckRadius = signalRadius > 0 ? signalRadius : situation.FamiliarEffectRadius;
            int woundedInRange = situation.Familiar != null
                ? CollectionHelper.CountWhere(situation.Allies, a =>
                    a.IsConscious && CombatCache.GetHPPercent(a) < 70f &&
                    CombatCache.GetDistanceInTiles(situation.Familiar, a) <= signalCheckRadius)
                : 0;

            // 2명 이상 부상 아군이 범위 내 있어야 의미
            if (woundedInRange < 2)
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Vitality Signal: Only {woundedInRange} wounded in range (need 2+)");
                return null;
            }

            var selfTarget = new TargetWrapper(situation.Unit);
            string reason;
            if (!CombatAPI.CanUseAbilityOn(signal, selfTarget, out reason))
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Vitality Signal blocked: {reason}");
                return null;
            }

            remainingAP -= apCost;
            Log.Planning.Info($"[{RoleName}] ★ Servo-Skull Vitality Signal ({woundedInRange} wounded in range)");

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
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Hex: No raven available");
                return null;
            }

            // 레이븐 효과 범위 (EFFECT_RADIUS_TILES) × 2 이내 적만 후보
            float maxHexRange = CombatAPI.TilesToMeters(FamiliarPositioner.EFFECT_RADIUS_TILES * 2f);
            BaseUnitEntity target = null;
            float bestHP = 0f;
            // ★ v3.40.8: 데미지 면역 적 제외 (Hex도 면역 구조물에는 무의미)
            for (int i = 0; i < situation.Enemies.Count; i++)
            {
                var enemy = situation.Enemies[i];
                if (!enemy.IsConscious) continue;
                if (CombatAPI.IsTargetImmuneToDamage(enemy, situation.Unit)) continue;

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
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Hex: No enemies within Raven range ({maxHexRange:F1}m)");
                return null;
            }

            var targetWrapper = new TargetWrapper(target);
            string reason;
            if (!CombatAPI.CanUseAbilityOn(hex, targetWrapper, out reason))
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Hex blocked: {reason}");
                return null;
            }

            remainingAP -= apCost;
            Log.Planning.Info($"[{RoleName}] ★ Raven Hex: {target.CharacterName} (within Raven range)");

            return PlannedAction.Attack(hex, target,
                $"Hex on {target.CharacterName}", apCost);
        }

        /// <summary>
        /// ★ v3.18.0: 레이븐 정화방전 (Purification Discharge) 계획
        /// 4타일 내 가장 가까운 적 3명에게 (Psy Rating × WP bonus) 충격 데미지
        /// Overcharge 없이 사용 시 레이븐 자해 → 반드시 Overcharge 확인 필수
        /// </summary>
        protected PlannedAction PlanFamiliarPurificationDischarge(Situation situation, ref float remainingAP)
        {
            if (situation.FamiliarType != PetType.Raven) return null;
            if (situation.Familiar == null || !situation.Familiar.IsConscious) return null;

            // 안전 체크: Overcharge(HeroicAct) 활성 시에만 사용
            if (!FamiliarAPI.CanRavenUseAttackAbilities(situation.Unit)) return null;

            // 사역마 능력 중 정화방전 찾기
            var abilities = situation.FamiliarAbilities;
            if (abilities == null) return null;

            AbilityData discharge = null;
            for (int i = 0; i < abilities.Count; i++)
            {
                if (FamiliarAbilities.IsPurificationDischarge(abilities[i]))
                {
                    discharge = abilities[i];
                    break;
                }
            }
            if (discharge == null) return null;

            float apCost = CombatAPI.GetAbilityAPCost(discharge);
            if (apCost > remainingAP) return null;

            // 레이븐 주변 4타일 내 적 존재 확인
            // ★ v3.40.8: 데미지 면역 적 제외
            int enemiesNearRaven = 0;
            float ravenEffectRadius = CombatAPI.TilesToMeters(4);
            for (int i = 0; i < situation.Enemies.Count; i++)
            {
                var enemy = situation.Enemies[i];
                if (enemy == null || !enemy.IsConscious) continue;
                if (CombatAPI.IsTargetImmuneToDamage(enemy, situation.Unit)) continue;
                float dist = Vector3.Distance(situation.Familiar.Position, enemy.Position);
                if (dist <= ravenEffectRadius) enemiesNearRaven++;
            }
            if (enemiesNearRaven == 0) return null;

            // 사용 가능 여부 최종 확인
            List<string> unavailReasons;
            if (!CombatAPI.IsAbilityAvailable(discharge, out unavailReasons)) return null;
            if (discharge.IsRestricted) return null;

            remainingAP -= apCost;
            Log.Planning.Info($"[{RoleName}] ★ Purification Discharge: {enemiesNearRaven} enemies near Raven");

            return PlannedAction.Buff(discharge, situation.Familiar,
                $"Purification Discharge ({enemiesNearRaven} enemies)", apCost);
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
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Cycle blocked: {reason}");
                return null;
            }

            remainingAP -= apCost;
            Log.Planning.Info($"[{RoleName}] ★ Raven Complete the Cycle");

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
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Fast blocked: {reason}");
                return null;
            }

            remainingAP -= apCost;
            Log.Planning.Info($"[{RoleName}] ★ Mastiff Fast (mobility buff)");

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
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Roam blocked: {reason}");
                return null;
            }

            remainingAP -= apCost;
            Log.Planning.Info($"[{RoleName}] ★ Mastiff Roam (auto-attack mode)");

            return PlannedAction.Buff(roam, situation.Unit,
                "Mastiff Roam (autonomous)", apCost);
        }

        #endregion
    }
}
