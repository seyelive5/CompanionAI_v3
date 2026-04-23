using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.Blueprints;
using Kingmaker.Designers.Mechanics.Facts;        // WeaponSetChangedTrigger, WarhammerModifyIncomingAttackDamage, WarhammerIncomingDamageNullifier
using Kingmaker.Designers.Mechanics.Facts.Damage; // WarhammerDamageModifier
using Kingmaker.EntitySystem;                     // EntityFact
using Kingmaker.Mechanics.Damage;                 // DamageExtension.Contains
using Kingmaker.EntitySystem.Entities;            // BaseUnitEntity
using Kingmaker.Enums;                            // WeaponFamily
using Kingmaker.Pathfinding;                      // PathfindingService
using Kingmaker.UnitLogic.Abilities;              // AbilityData
using Kingmaker.UnitLogic.Abilities.Components.CasterCheckers; // WarhammerAbilityManageResources
using Kingmaker.UnitLogic.Buffs.Components;       // WarhammerFreeUltimateBuff
using Kingmaker.UnitLogic.FactLogic;              // AddDamageTypeImmunity, ForceMoveTriggerInitiator
using Kingmaker.UnitLogic.Mechanics.Actions;      // ContextActionApplyBuff, ContextActionPush
using UnityEngine;                                // Time, Vector3
using CompanionAI_v3.Data;                        // AbilityDatabase, BlueprintCache
using CompanionAI_v3.Settings;                    // RangePreference

namespace CompanionAI_v3.GameInterface
{
    public static partial class CombatAPI
    {
        // ★ v3.8.80: GetAvailableAbilities 프레임 캐시 (v3.111.30: residual → Abilities partial 이동)
        // 같은 프레임 내 동일 유닛에 대한 반복 호출 방지 (Analyze + Plan = 4+회/프레임)
        private static string _cachedAbilitiesUnitId;
        private static int _cachedAbilitiesFrame;
        private static List<AbilityData> _cachedAbilitiesList;

        #region Abilities

        /// <summary>
        /// ★ v3.0.94: GetUnavailabilityReasons() 체크 추가
        /// 기존: data.IsAvailable만 체크 → 쿨다운 능력도 포함됨!
        /// 수정: GetUnavailabilityReasons()로 쿨다운, 탄약, 충전 등 모두 체크
        /// ★ v3.1.11: 보너스 사용(런 앤 건 등) 처리 추가
        /// </summary>
        public static List<AbilityData> GetAvailableAbilities(BaseUnitEntity unit)
        {
            if (unit == null) return new List<AbilityData>();

            // ★ v3.8.80: 프레임 캐시 - 같은 프레임/유닛이면 이전 결과 재사용
            // ProcessTurn 1회당 Analyze(2회) + Plan(2+회) = 4+회 호출되지만 결과 동일
            int currentFrame = Time.frameCount;
            string unitId = unit.UniqueId;
            if (_cachedAbilitiesList != null
                && _cachedAbilitiesFrame == currentFrame
                && _cachedAbilitiesUnitId == unitId)
            {
                return _cachedAbilitiesList;
            }

            var abilities = new List<AbilityData>();

            try
            {
                var rawAbilities = unit.Abilities?.RawFacts;
                if (rawAbilities == null) return abilities;

                foreach (var ability in rawAbilities)
                {
                    try
                    {
                        var data = ability?.Data;
                        if (data == null) continue;

                        // ★ v3.6.20: IsAbilityAvailable(out reasons)와 동일한 로직 사용
                        List<string> reasons;
                        if (!IsAbilityAvailable(data, out reasons))
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Filtered out {GetAbilityDisplayName(data)}: {string.Join(", ", reasons)}");
                            continue;
                        }

                        // ★ v3.5.32: 중복 그룹 체크 - 계획 단계에서 필터링
                        if (HasDuplicateAbilityGroups(data))
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Filtered out {GetAbilityDisplayName(data)}: duplicate ability groups (game data bug)");
                            continue;
                        }

                        abilities.Add(data);
                    }
                    catch (Exception iterEx)
                    {
                        // ★ v3.111.14: 단일 능력 처리 실패 → 다음으로 (LocalizedString 등 예외 격리)
                        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAvailableAbilities: skip ability due to {iterEx.GetType().Name}: {iterEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                // ★ v3.4.01: P1-2 예외 상세 로깅
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAvailableAbilities error: {ex.Message}");
            }

            // 캐시 저장
            _cachedAbilitiesUnitId = unitId;
            _cachedAbilitiesFrame = currentFrame;
            _cachedAbilitiesList = abilities;

            return abilities;
        }

        /// <summary>
        /// ★ v3.0.17: v2.2에서 포팅 - 완전한 공격 능력 검증
        /// - Weapon 확인
        /// - 재장전 제외
        /// - 수류탄 제외 (IsGrenadeOrExplosive)
        /// - ★ GetUnavailabilityReasons() 체크 (핵심!)
        /// - RangePreference에 맞는 무기 우선
        /// - 폴백으로 IsOffensiveAbility 확인
        /// </summary>
        public static AbilityData FindAnyAttackAbility(BaseUnitEntity unit, RangePreference preference,
            bool includeDangerousAoE = false)  // ★ v3.9.92: DangerousAoE 포함 옵션
        {
            if (unit == null) return null;

            try
            {
                var rawAbilities = unit.Abilities?.RawFacts;
                if (rawAbilities == null) return null;

                AbilityData preferredAttack = null;
                float preferredRange = 0f;
                AbilityData fallbackAttack = null;

                foreach (var ability in rawAbilities)
                {
                    try
                    {
                        var abilityData = ability?.Data;
                        if (abilityData == null) continue;

                        // 1. 무기 공격만
                        if (abilityData.Weapon == null) continue;

                        // 2. 재장전 제외
                        if (AbilityDatabase.IsReload(abilityData)) continue;

                        // 3. ★ v3.0.17: 수류탄/폭발물 제외 (v2.2 포팅)
                        if (CombatHelpers.IsGrenadeOrExplosive(abilityData))
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Skipping {GetAbilityDisplayName(abilityData)}: IsGrenadeOrExplosive");
                            continue;
                        }

                        // 4. ★ v3.0.18: CanTargetEnemies 체크 (v3.0.16에서 누락됨!)
                        // "칼날" 같은 스킬은 Weapon != null 이지만 적을 타겟할 수 없음
                        // ★ v3.9.92: DangerousAoE (화염방사기 Cone/Ray)는 포인트 타겟이지만
                        //   적 위치를 타겟할 수 있으므로 includeDangerousAoE=true 시 허용
                        var bp = abilityData.Blueprint;
                        if (bp != null && !bp.CanTargetEnemies)
                        {
                            if (includeDangerousAoE && AbilityDatabase.IsDangerousAoE(abilityData))
                            {
                                // DangerousAoE 포인트 타겟 — 위치 평가에 사용 가능
                            }
                            else
                            {
                                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Skipping {GetAbilityDisplayName(abilityData)}: CanTargetEnemies=false");
                                continue;
                            }
                        }

                        // 5. ★ v3.0.17: 핵심! GetUnavailabilityReasons() 체크 (v2.2 포팅)
                        List<string> reasons;
                        if (!IsAbilityAvailable(abilityData, out reasons))
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Skipping {GetAbilityDisplayName(abilityData)}: {string.Join(", ", reasons)}");
                            continue;
                        }

                        // 5. ★ v3.0.27: RangePreference에 맞는 무기 중 사거리가 가장 긴 것 선택
                        // 기존: 첫 번째 선호 무기에서 break → 사거리 짧은 "현상금 청구" 문제
                        if (CombatHelpers.IsPreferredWeaponType(abilityData, preference))
                        {
                            float range = GetAbilityRange(abilityData);
                            if (preferredAttack == null || range > preferredRange)
                            {
                                preferredAttack = abilityData;
                                preferredRange = range;
                                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Found preferred ({preference}) attack: {GetAbilityDisplayName(abilityData)} (range={range:F1})");
                            }
                            // ★ v3.0.27: break 제거 - 더 긴 사거리 무기를 찾기 위해 계속 검색
                        }
                        else if (fallbackAttack == null)
                        {
                            fallbackAttack = abilityData;  // 폴백용 저장
                        }
                    }
                    catch (Exception iterEx)
                    {
                        // ★ v3.111.14: per-ability 예외 격리 (LocalizedString 등) → 다음 능력으로
                        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] FindAnyAttackAbility: skip ability due to {iterEx.GetType().Name}: {iterEx.Message}");
                    }
                }

                // 선호 타입이 있으면 사용
                if (preferredAttack != null)
                {
                    return preferredAttack;
                }

                // ★ v3.0.21: 선호 무기가 없을 때, RangePreference에 따라 사이킥 공격 우선 검토
                // 카시아 같은 원거리 사이커는 근접 무기보다 사이킥 공격 우선
                if (preference == RangePreference.PreferRanged)
                {
                    foreach (var ability in rawAbilities)
                    {
                        try
                        {
                            var abilityData = ability?.Data;
                            if (abilityData == null) continue;

                            // 무기 아닌 공격성 능력 (사이킥 공격 등)
                            if (abilityData.Weapon != null) continue;
                            if (!IsOffensiveAbility(abilityData)) continue;

                            // 근접 스킬 제외
                            if (abilityData.IsMelee) continue;

                            List<string> reasons;
                            if (IsAbilityAvailable(abilityData, out reasons))
                            {
                                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Found ranged offensive ability (pref={preference}): {GetAbilityDisplayName(abilityData)}");
                                return abilityData;
                            }
                        }
                        catch (Exception iterEx)
                        {
                            // ★ v3.111.14: per-ability 예외 격리 (psyker LocalizedString 핫스팟)
                            if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] FindAnyAttackAbility psyker fallback: skip ability due to {iterEx.GetType().Name}: {iterEx.Message}");
                        }
                    }
                }

                // 폴백 무기 사용
                if (fallbackAttack != null)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] No preferred weapon, using fallback: {GetAbilityDisplayName(fallbackAttack)}");
                    return fallbackAttack;
                }

                // ★ v3.0.17: 무기 공격이 없으면 공격성 능력 찾기 (v2.2 포팅)
                foreach (var ability in rawAbilities)
                {
                    try
                    {
                        var abilityData = ability?.Data;
                        if (abilityData == null) continue;

                        if (IsOffensiveAbility(abilityData))
                        {
                            List<string> reasons;
                            if (IsAbilityAvailable(abilityData, out reasons))
                            {
                                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Found offensive ability as fallback: {GetAbilityDisplayName(abilityData)}");
                                return abilityData;
                            }
                        }
                    }
                    catch (Exception iterEx)
                    {
                        // ★ v3.111.14: per-ability 예외 격리
                        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] FindAnyAttackAbility offensive fallback: skip ability due to {iterEx.GetType().Name}: {iterEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] FindAnyAttackAbility error: {ex.Message}");
            }

            return null;
        }

        public static float GetAbilityAPCost(AbilityData ability)
        {
            if (ability == null) return 1f;
            try
            {
                return ability.CalculateActionPointCost();
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAbilityAPCost failed for {ability?.Name}: {ex.Message}");
                return 1f;
            }
        }

        /// <summary>
        /// ★ v3.6.14: 능력이 bonus usage 상태인지 확인
        /// 쿨다운이지만 런 앤 건 등으로 보너스 사용 가능한 경우 true
        /// </summary>
        public static bool HasBonusUsage(AbilityData ability)
        {
            if (ability == null) return false;
            try
            {
                var unavailabilityReasons = ability.GetUnavailabilityReasons();
                if (unavailabilityReasons.Count == 0) return false;

                // 쿨다운만 문제인지 확인
                bool onlyOnCooldown = unavailabilityReasons.All(r =>
                    r == AbilityData.UnavailabilityReasonType.IsOnCooldown ||
                    r == AbilityData.UnavailabilityReasonType.IsOnCooldownUntilEndOfCombat);

                // 쿨다운이지만 IsAvailable=true면 bonus usage 있음
                return onlyOnCooldown && ability.IsAvailable;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] HasBonusUsage failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.6.14: 실제 사용 시 필요한 AP 비용 (bonus usage면 0)
        /// </summary>
        public static float GetEffectiveAPCost(AbilityData ability)
        {
            if (ability == null) return 1f;
            if (HasBonusUsage(ability)) return 0f;
            return GetAbilityAPCost(ability);
        }

        /// <summary>
        /// ★ v3.5.88: 0 AP 공격이 있는지 확인
        /// Break Through → Slash 같은 보너스 능력 감지용
        /// </summary>
        public static bool HasZeroAPAttack(BaseUnitEntity unit)
        {
            if (unit == null) return false;

            try
            {
                var abilities = GetAvailableAbilities(unit);
                foreach (var ability in abilities)
                {
                    if (ability == null) continue;

                    // 공격 능력인지 확인 (무기 사용 또는 Offensive)
                    bool isAttack = ability.Weapon != null ||
                                   IsOffensiveAbility(ability);
                    if (!isAttack) continue;

                    // ★ v3.8.86: GetEffectiveAPCost 사용 - bonus usage 공격도 감지
                    float cost = GetEffectiveAPCost(ability);
                    if (cost <= 0.01f)  // 0 AP (부동소수점 오차 허용)
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Found 0 AP attack: {ability.Name} (bonus={HasBonusUsage(ability)})");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] HasZeroAPAttack error: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// ★ v3.5.88: 0 AP 공격 목록 가져오기
        /// </summary>
        public static List<AbilityData> GetZeroAPAttacks(BaseUnitEntity unit)
        {
            var result = new List<AbilityData>();
            if (unit == null) return result;

            try
            {
                var abilities = GetAvailableAbilities(unit);
                foreach (var ability in abilities)
                {
                    if (ability == null) continue;

                    // 공격 능력인지 확인
                    bool isAttack = ability.Weapon != null ||
                                   IsOffensiveAbility(ability);
                    if (!isAttack) continue;

                    // ★ v3.8.86: GetEffectiveAPCost 사용 - bonus usage 공격도 감지
                    float cost = GetEffectiveAPCost(ability);
                    if (cost <= 0.01f)
                    {
                        result.Add(ability);
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetZeroAPAttacks error: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// ★ v3.9.10: 0 AP 공격이 적에게 도달 가능한지 확인
        /// 현재 위치에서 사거리 내 적이 있거나, 이동 후 사거리 내로 진입 가능한지 확인
        /// TurnOrchestrator에서 0 AP 공격 루프 방지용
        /// </summary>
        public static bool CanAnyZeroAPAttackReachEnemy(BaseUnitEntity unit, float remainingMP)
        {
            if (unit == null) return false;

            try
            {
                var zeroAPAttacks = GetZeroAPAttacks(unit);
                if (zeroAPAttacks.Count == 0) return false;

                var enemies = GetEnemies(unit);
                if (enemies.Count == 0) return false;

                float movableTiles = remainingMP / GridCellSize;  // MP를 타일로 변환

                foreach (var attack in zeroAPAttacks)
                {
                    int rangeTiles = GetAbilityRangeInTiles(attack);

                    foreach (var enemy in enemies)
                    {
                        float distTiles = GetDistanceInTiles(unit, enemy);

                        // 현재 위치에서 사거리 내이거나, 이동하면 도달 가능
                        if (distTiles <= rangeTiles + movableTiles)
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug(
                                $"[CombatAPI] 0AP attack {attack.Name} can reach {enemy.CharacterName} " +
                                $"(dist={distTiles:F1}, range={rangeTiles}, movable={movableTiles:F1})");
                            return true;
                        }
                    }
                }

                Main.Log($"[CombatAPI] No 0AP attack can reach any enemy (MP={remainingMP:F1}, movable={movableTiles:F1} tiles)");
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CanAnyZeroAPAttackReachEnemy error: {ex.Message}");
                return true;  // 에러 시 안전하게 계속 진행 허용
            }

            return false;
        }

        /// <summary>
        /// ★ v3.0.55: 능력의 MP 코스트 계산
        /// ClearMPAfterUse가 true인 능력은 999를 반환 (전체 MP 클리어)
        /// </summary>
        public static float GetAbilityMPCost(AbilityData ability)
        {
            if (ability == null) return 0f;
            try
            {
                // ClearMPAfterUse 체크 - 이 능력 사용 후 MP가 전부 소모됨
                if (ability.ClearMPAfterUse)
                {
                    return 999f;  // 전체 MP 클리어를 의미
                }

                // 일반적인 경우: MP 코스트 없음 (대부분의 능력)
                // 일부 이동 기반 능력은 MP를 사용하지만, 현재는 ClearMPAfterUse만 고려
                return 0f;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAbilityMPCost failed for {ability?.Name}: {ex.Message}");
                return 0f;
            }
        }

        /// <summary>
        /// ★ v3.0.55: 능력이 MP를 전부 클리어하는지 확인
        /// ★ v3.8.86: BlueprintCache 우선 사용 (O(1) 조회)
        /// </summary>
        public static bool AbilityClearsMPAfterUse(AbilityData ability)
        {
            if (ability == null) return false;
            try
            {
                // ★ v3.8.86: 캐시 우선 조회
                var cached = BlueprintCache.GetOrCache(ability);
                if (cached != null) return cached.ClearMPAfterUse;
                return ability.ClearMPAfterUse;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] AbilityClearsMPAfterUse failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.8.88: 유닛의 DoNotResetMovementPointsOnAttacks 특성 고려
        /// Run&Gun 등이 활성화되면 WarhammerEndTurn.OnCast()가 MP를 실제로 안 지움
        /// </summary>
        public static bool AbilityClearsMPAfterUse(AbilityData ability, BaseUnitEntity caster)
        {
            if (!AbilityClearsMPAfterUse(ability)) return false;
            try
            {
                if (caster?.Features?.DoNotResetMovementPointsOnAttacks ?? false)
                    return false;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] AbilityClearsMPAfterUse(caster) failed: {ex.Message}");
            }
            return true;
        }

        /// <summary>
        /// ★ v3.5.34: GapCloser/Charge 능력의 MP 비용 계산
        /// 게임의 패스파인딩 API를 사용하여 실제 타일 경로 비용 계산
        /// MP 비용 = 경로 타일 수 - 1 (출발점 제외)
        /// </summary>
        public static float GetGapCloserMPCost(BaseUnitEntity unit, Vector3 targetPosition)
        {
            if (unit == null) return float.MaxValue;

            try
            {
                var agent = unit.View?.MovementAgent;
                if (agent == null)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetGapCloserMPCost: agent is null");
                    return float.MaxValue;
                }

                // 게임의 Charge 경로 계산 API 사용
                var path = PathfindingService.Instance.FindPathChargeTB_Blocking(
                    agent,
                    unit.Position,
                    targetPosition,
                    false,  // ignoreBlockers
                    null    // targetEntity
                );

                if (path == null || path.path == null || path.path.Count < 2)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetGapCloserMPCost: invalid path (count={path?.path?.Count ?? 0})");
                    return float.MaxValue;
                }

                // MP 비용 = 경로 타일 수 - 1 (출발점 제외)
                // 게임의 AbilityCustomDirectMovement.Deliver()와 동일한 계산
                float mpCost = Math.Max(0, path.path.Count - 1);
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetGapCloserMPCost: path={path.path.Count} tiles -> MP cost={mpCost}");
                return mpCost;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetGapCloserMPCost error: {ex.Message}");
                return float.MaxValue;
            }
        }

        /// <summary>
        /// ★ v3.5.34: 능력의 MP 비용 계산 (통합 API)
        /// GapCloser/Charge 능력은 실제 경로 기반, 그 외는 컴포넌트 기반
        /// </summary>
        public static float GetAbilityExpectedMPCost(AbilityData ability, BaseUnitEntity target = null)
        {
            if (ability == null) return 0f;

            try
            {
                // 1. ClearMPAfterUse 체크 - 전체 MP 소모
                if (ability.ClearMPAfterUse)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] {ability.Name}: ClearMPAfterUse -> MP cost=MAX");
                    return float.MaxValue;
                }

                // 2. WarhammerAbilityManageResources 체크 (고정 MP 비용)
                var manageResources = ability.Blueprint?.GetComponent<WarhammerAbilityManageResources>();
                if (manageResources != null)
                {
                    if (manageResources.CostsMaximumMovePoints)
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] {ability.Name}: CostsMaximumMovePoints -> MP cost=MAX");
                        return float.MaxValue;
                    }
                    if (manageResources.shouldSpendMovePoints > 0)
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] {ability.Name}: shouldSpendMovePoints={manageResources.shouldSpendMovePoints}");
                        return manageResources.shouldSpendMovePoints;
                    }
                }

                // 3. IsMoveUnit (Charge/GapCloser 등) - 패스파인딩으로 실제 비용 계산
                if (ability.Blueprint?.IsMoveUnit == true && target != null)
                {
                    var caster = ability.Caster as BaseUnitEntity;
                    if (caster != null)
                    {
                        float mpCost = GetGapCloserMPCost(caster, target.Position);
                        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] {ability.Name}: IsMoveUnit -> MP cost={mpCost:F1}");
                        return mpCost;
                    }
                }

                return 0f;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAbilityExpectedMPCost error: {ex.Message}");
                return 0f;
            }
        }

        public static bool HasActiveBuff(BaseUnitEntity unit, AbilityData ability)
        {
            if (unit == null || ability == null) return false;

            try
            {
                // ★ v3.4.01: P0-3 Blueprint null 체크
                if (ability.Blueprint == null) return false;

                // 능력의 버프 블루프린트 추출
                // ★ v3.8.62: BlueprintCache 캐시 사용 (GetComponent O(n) → O(1))
                var runAction = BlueprintCache.GetCachedRunAction(ability.Blueprint);
                if (runAction?.Actions?.Actions != null)
                {
                    foreach (var action in runAction.Actions.Actions)
                    {
                        if (action is ContextActionApplyBuff applyBuff)
                        {
                            var buffBlueprint = applyBuff.Buff;
                            if (buffBlueprint == null) continue;

                            var existingBuff = unit.Buffs.GetBuff(buffBlueprint);
                            if (existingBuff != null)
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // ★ v3.4.01: P1-2 예외 상세 로깅
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] HasActiveBuff error: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// ★ v3.7.94: 버프 남은 라운드 조회 (게임 API 활용)
        /// </summary>
        /// <param name="unit">대상 유닛</param>
        /// <param name="ability">버프 능력</param>
        /// <returns>남은 라운드 (버프 없으면 0, 영구 버프면 -1)</returns>
        public static int GetBuffRemainingRounds(BaseUnitEntity unit, AbilityData ability)
        {
            if (unit == null || ability?.Blueprint == null) return 0;

            try
            {
                // ★ v3.8.62: BlueprintCache 캐시 사용 (GetComponent O(n) → O(1))
                var runAction = BlueprintCache.GetCachedRunAction(ability.Blueprint);
                if (runAction?.Actions?.Actions != null)
                {
                    foreach (var action in runAction.Actions.Actions)
                    {
                        if (action is ContextActionApplyBuff applyBuff)
                        {
                            var buffBlueprint = applyBuff.Buff;
                            if (buffBlueprint == null) continue;

                            var existingBuff = unit.Buffs.GetBuff(buffBlueprint);
                            if (existingBuff != null)
                            {
                                // 영구 버프 (DurationInRounds == 0)
                                if (existingBuff.IsPermanent)
                                    return -1;

                                // 남은 라운드 반환
                                return existingBuff.ExpirationInRounds;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetBuffRemainingRounds error: {ex.Message}");
            }

            return 0;  // 버프 없음
        }

        /// <summary>
        /// ★ v3.7.94: 버프 갱신 필요 여부 확인
        /// 버프가 없거나 곧 만료되면 true
        /// </summary>
        /// <param name="unit">대상 유닛</param>
        /// <param name="ability">버프 능력</param>
        /// <param name="refreshThreshold">갱신 임계값 (기본 2라운드 이하면 갱신)</param>
        public static bool NeedsBuffRefresh(BaseUnitEntity unit, AbilityData ability, int refreshThreshold = 2)
        {
            int remaining = GetBuffRemainingRounds(unit, ability);

            // 영구 버프면 갱신 불필요
            if (remaining == -1)
                return false;

            // 버프 없거나 임계값 이하면 갱신 필요
            return remaining <= refreshThreshold;
        }

        /// <summary>
        /// ★ v3.7.94: 유닛의 모든 활성 버프 이름 목록 (디버그용)
        /// </summary>
        public static List<string> GetAllActiveBuffNames(BaseUnitEntity unit)
        {
            var result = new List<string>();
            if (unit?.Buffs == null) return result;

            try
            {
                foreach (var buff in unit.Buffs)
                {
                    string name = buff.Blueprint?.Name ?? buff.Name ?? "Unknown";
                    int remaining = buff.IsPermanent ? -1 : buff.ExpirationInRounds;
                    string durationStr = remaining == -1 ? "∞" : $"{remaining}R";
                    result.Add($"{name} ({durationStr})");
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAllActiveBuffNames error: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// ★ v3.7.94: 유닛이 특정 버프 카테고리를 가지고 있는지 확인
        /// </summary>
        public static bool HasBuffOfType(BaseUnitEntity unit, string buffNameContains)
        {
            if (unit?.Buffs == null || string.IsNullOrEmpty(buffNameContains)) return false;

            try
            {
                foreach (var buff in unit.Buffs)
                {
                    string name = buff.Blueprint?.Name ?? buff.Name ?? "";
                    if (name.IndexOf(buffNameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] HasBuffOfType error: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// ★ v3.32.0: 플라스마 과열 Rank 조회
        /// PlasmaOverheat_Buff (GUID: 0835dbc012334dd49f849fcc92e9f708) — Stacking: Rank
        /// 매 사격 Rank +2, 턴 시작 Rank -1, Rank 4+ = 100% 폭발 (자기+주변 AoE)
        /// </summary>
        public static int GetPlasmaOverheatRank(BaseUnitEntity unit)
        {
            if (unit?.Buffs == null) return 0;
            try
            {
                foreach (var buff in unit.Buffs)
                {
                    if (buff.Blueprint?.AssetGuid?.ToString() == "0835dbc012334dd49f849fcc92e9f708")
                        return buff.Rank;
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetPlasmaOverheatRank error: {ex.Message}");
            }
            return 0;
        }

        /// <summary>
        /// ★ v3.32.0: 능력이 플라스마 무기를 사용하는지 확인
        /// AbilityData.Weapon → BlueprintItemWeapon.Family == WeaponFamily.Plasma
        /// </summary>
        public static bool IsPlasmaWeapon(AbilityData ability)
        {
            try
            {
                return ability?.Weapon?.Blueprint.Family == WeaponFamily.Plasma;
            }
            catch { return false; }
        }

        /// <summary>
        /// ★ v3.40.0: Prey 마킹 능력 GUID 목록 (HuntDownThePrey, ChoosePrey_Noble)
        /// </summary>
        private static readonly HashSet<string> PreyAbilityGuids = new HashSet<string>
        {
            "b97c9e76f6ca46d3bb8ccd86baa9d7c9", // HuntDownThePrey (Bounty Hunter)
            "43ee13d74e824d07a0fa2a651c23df40", // ChoosePrey_Noble
        };

        /// <summary>
        /// ★ v3.40.0: 적이 Prey(먹잇감)로 마크되었는지 확인
        /// buff.Context.SourceAbility의 GUID로 역추적 — Prey 버프 GUID 불필요
        /// Piercing Shot + Prey = 보장 크리 → ScoreAttackBuff에서 가산점
        /// </summary>
        public static bool IsMarkedAsPrey(BaseUnitEntity target)
        {
            if (target?.Buffs == null) return false;
            try
            {
                foreach (var buff in target.Buffs)
                {
                    var sourceAbility = buff?.Context?.SourceAbility;
                    if (sourceAbility == null) continue;
                    var guid = sourceAbility.AssetGuid?.ToString();
                    if (guid != null && PreyAbilityGuids.Contains(guid))
                        return true;
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsMarkedAsPrey error: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// ★ v3.40.6: 타겟이 공격자의 데미지에 면역인지 확인
        /// 4가지 메커니즘 검사:
        /// 1) AddDamageTypeImmunity — 특정 데미지 타입 면역 (PctMul_Extra = 0)
        /// 2) WarhammerDamageModifier — UnmodifiablePercentDamageModifier=0 or PercentDamageModifier≤-100
        /// 3) WarhammerModifyIncomingAttackDamage — PercentDamageModifier ≤ -100
        /// 4) WarhammerIncomingDamageNullifier — NullifyChances = 0 (데미지 통과 확률 0%)
        /// 면역 타겟은 공격해도 데미지 0이므로 AI가 다른 타겟을 선택해야 함
        /// </summary>
        public static bool IsTargetImmuneToDamage(BaseUnitEntity target, BaseUnitEntity attacker)
        {
            if (target == null || attacker == null) return false;

            try
            {
                // 공격자의 주 무기 데미지 타입 조회
                var weapon = attacker.Body?.PrimaryHand?.Weapon;
                if (weapon?.Blueprint?.DamageType == null) return false;

                var attackerDmgType = weapon.Blueprint.DamageType.Type;
                bool debugEnabled = Main.IsDebugEnabled;

                foreach (var fact in target.Facts.List)
                {
                    if (fact == null) continue;

                    // 1. AddDamageTypeImmunity — 특정 데미지 타입 면역
                    foreach (var component in fact.SelectComponents<AddDamageTypeImmunity>())
                    {
                        if (component.Types.Contains(attackerDmgType))
                        {
                            if (debugEnabled)
                                Main.LogDebug($"[CombatAPI] ★ {target.CharacterName} IMMUNE via AddDamageTypeImmunity ({attackerDmgType}, fact: {fact.Name})");
                            return true;
                        }
                    }

                    // 2. WarhammerDamageModifier (WarhammerDamageModifierTarget 포함)
                    //    - UnmodifiablePercentDamageModifier = 0 → PctMul_Extra=0 = 데미지 완전 무효화
                    //    - PercentDamageModifier ≤ -100 → PctAdd -100% = 데미지 0
                    //    ★ v3.94.0: Restrictions 체크 — 조건부 면역은 판정에서 제외
                    foreach (var component in fact.SelectComponents<WarhammerDamageModifier>())
                    {
                        // 조건부 (특정 무기/공격자 타입에만 적용) → 무조건 면역 아님
                        if (!IsUnconditionalModifier(component)) continue;
                        try
                        {
                            var unmodPct = component.UnmodifiablePercentDamageModifier;
                            if (unmodPct != null && unmodPct.Enabled)
                            {
                                int unmodValue = EvaluateContextValue(unmodPct, fact);
                                if (unmodValue != int.MaxValue && unmodValue == 0)
                                {
                                    if (debugEnabled)
                                        Main.LogDebug($"[CombatAPI] ★ {target.CharacterName} IMMUNE via WarhammerDamageModifier.UnmodPctMul=0 (fact: {fact.Name})");
                                    return true;
                                }
                            }

                            var pctMod = component.PercentDamageModifier;
                            if (pctMod != null && pctMod.Enabled)
                            {
                                int pctValue = EvaluateContextValue(pctMod, fact);
                                if (pctValue != int.MaxValue && pctValue <= -100)
                                {
                                    if (debugEnabled)
                                        Main.LogDebug($"[CombatAPI] ★ {target.CharacterName} IMMUNE via WarhammerDamageModifier.PctDmgMod={pctValue} (fact: {fact.Name})");
                                    return true;
                                }
                            }
                        }
                        catch { }
                    }

                    // 3. WarhammerModifyIncomingAttackDamage — PctDmgMod ≤ -100
                    //    ★ v3.94.0: Restrictions 체크
                    foreach (var component in fact.SelectComponents<WarhammerModifyIncomingAttackDamage>())
                    {
                        if (!IsUnconditionalModifier(component)) continue;
                        try
                        {
                            var pctMod = component.PercentDamageModifier;
                            if (pctMod != null)
                            {
                                int pctValue = EvaluateContextValue(pctMod, fact);
                                if (pctValue != int.MaxValue && pctValue <= -100)
                                {
                                    if (debugEnabled)
                                        Main.LogDebug($"[CombatAPI] ★ {target.CharacterName} IMMUNE via WarhammerModifyIncomingAttackDamage (PctDmgMod={pctValue}, fact: {fact.Name})");
                                    return true;
                                }
                            }
                        }
                        catch { }
                    }

                    // 4. WarhammerIncomingDamageNullifier — DamageChance = 0% (완전 면역)
                    //    ★ v3.94.0: Restrictions 체크
                    foreach (var component in fact.SelectComponents<WarhammerIncomingDamageNullifier>())
                    {
                        if (!IsUnconditionalModifier(component)) continue;
                        try
                        {
                            var field = typeof(WarhammerIncomingDamageNullifier).GetField("m_NullifyChances",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (field != null)
                            {
                                var nullifyCV = field.GetValue(component) as Kingmaker.UnitLogic.Mechanics.ContextValue;
                                if (nullifyCV != null)
                                {
                                    int chances = EvaluateContextValue(nullifyCV, fact);
                                    if (chances != int.MaxValue)
                                    {
                                        chances = Math.Max(Math.Min(chances, 100), 0);
                                        if (chances <= 0)
                                        {
                                            if (debugEnabled)
                                                Main.LogDebug($"[CombatAPI] ★ {target.CharacterName} IMMUNE via WarhammerIncomingDamageNullifier (DmgChance=0%, fact: {fact.Name})");
                                            return true;
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }

                // 진단 로그 제거됨 — 면역 감지 확인 완료 (v3.40.6)
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsTargetImmuneToDamage error: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// ★ v3.42.0: attacker 없이 무조건적 면역만 체크 (메커니즘 2-4)
        /// 도발 타겟 선택, 위치 기반 적 탐색 등 특정 공격자의 무기 타입이 불필요한 경우 사용
        /// 메커니즘 1 (AddDamageTypeImmunity)은 무기 타입 의존이므로 생략
        /// </summary>
        public static bool IsTargetUnconditionallyImmune(BaseUnitEntity target)
        {
            if (target == null) return false;

            try
            {
                bool debugEnabled = Main.IsDebugEnabled;

                foreach (var fact in target.Facts.List)
                {
                    if (fact == null) continue;

                    // 2. WarhammerDamageModifier — 무조건적 데미지 무효화
                    //    ★ v3.94.0: Restrictions 체크 — 조건부 면역은 판정에서 제외
                    foreach (var component in fact.SelectComponents<WarhammerDamageModifier>())
                    {
                        if (!IsUnconditionalModifier(component)) continue;
                        try
                        {
                            var unmodPct = component.UnmodifiablePercentDamageModifier;
                            if (unmodPct != null && unmodPct.Enabled)
                            {
                                int unmodValue = EvaluateContextValue(unmodPct, fact);
                                if (unmodValue != int.MaxValue && unmodValue == 0)
                                {
                                    if (debugEnabled)
                                        Main.LogDebug($"[CombatAPI] ★ {target.CharacterName} UNCONDITIONALLY IMMUNE via WarhammerDamageModifier.UnmodPctMul=0 (fact: {fact.Name})");
                                    return true;
                                }
                            }

                            var pctMod = component.PercentDamageModifier;
                            if (pctMod != null && pctMod.Enabled)
                            {
                                int pctValue = EvaluateContextValue(pctMod, fact);
                                if (pctValue != int.MaxValue && pctValue <= -100)
                                {
                                    if (debugEnabled)
                                        Main.LogDebug($"[CombatAPI] ★ {target.CharacterName} UNCONDITIONALLY IMMUNE via WarhammerDamageModifier.PctDmgMod={pctValue} (fact: {fact.Name})");
                                    return true;
                                }
                            }
                        }
                        catch { }
                    }

                    // 3. WarhammerModifyIncomingAttackDamage — PctDmgMod ≤ -100
                    //    ★ v3.94.0: Restrictions 체크
                    foreach (var component in fact.SelectComponents<WarhammerModifyIncomingAttackDamage>())
                    {
                        if (!IsUnconditionalModifier(component)) continue;
                        try
                        {
                            var pctMod = component.PercentDamageModifier;
                            if (pctMod != null)
                            {
                                int pctValue = EvaluateContextValue(pctMod, fact);
                                if (pctValue != int.MaxValue && pctValue <= -100)
                                {
                                    if (debugEnabled)
                                        Main.LogDebug($"[CombatAPI] ★ {target.CharacterName} UNCONDITIONALLY IMMUNE via WarhammerModifyIncomingAttackDamage (PctDmgMod={pctValue}, fact: {fact.Name})");
                                    return true;
                                }
                            }
                        }
                        catch { }
                    }

                    // 4. WarhammerIncomingDamageNullifier — DamageChance = 0%
                    //    ★ v3.94.0: Restrictions 체크
                    foreach (var component in fact.SelectComponents<WarhammerIncomingDamageNullifier>())
                    {
                        if (!IsUnconditionalModifier(component)) continue;
                        try
                        {
                            var field = typeof(WarhammerIncomingDamageNullifier).GetField("m_NullifyChances",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (field != null)
                            {
                                var nullifyCV = field.GetValue(component) as Kingmaker.UnitLogic.Mechanics.ContextValue;
                                if (nullifyCV != null)
                                {
                                    int chances = EvaluateContextValue(nullifyCV, fact);
                                    if (chances != int.MaxValue)
                                    {
                                        chances = Math.Max(Math.Min(chances, 100), 0);
                                        if (chances <= 0)
                                        {
                                            if (debugEnabled)
                                                Main.LogDebug($"[CombatAPI] ★ {target.CharacterName} UNCONDITIONALLY IMMUNE via WarhammerIncomingDamageNullifier (DmgChance=0%, fact: {fact.Name})");
                                            return true;
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsTargetUnconditionallyImmune error: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// ContextValue를 안전하게 평가 — Simple이면 직접 읽기, 아니면 Context로 Calculate 시도
        /// 실패 시 int.MaxValue 반환
        /// </summary>
        private static int EvaluateContextValue(Kingmaker.UnitLogic.Mechanics.ContextValue cv, EntityFact fact)
        {
            if (cv == null) return int.MaxValue;
            if (cv.ValueType == Kingmaker.UnitLogic.Mechanics.ContextValueType.Simple)
                return cv.Value;
            try
            {
                var ctx = fact.MaybeContext;
                if (ctx != null) return cv.Calculate(ctx);
            }
            catch { }
            return int.MaxValue;
        }

        /// <summary>
        /// ★ v3.94.0: WarhammerDamageModifier 계열 컴포넌트가 무조건 적용되는지 확인.
        /// 게임 소스(WarhammerDamageModifier.cs:38)는 TryApply 진입 시 Restrictions.IsPassed를 체크.
        /// Restrictions.Property가 null이거나 Empty면 무조건 적용 → 진짜 면역 판정 가능.
        /// Property가 있으면 조건부 (예: "워프 생물"은 특정 무기 타입에만 감소 적용) → 면역 판정 금지.
        ///
        /// 세 컴포넌트 모두 "Restrictions" 필드 이름 공유:
        /// - WarhammerDamageModifier: public
        /// - WarhammerModifyIncomingAttackDamage: protected
        /// - WarhammerIncomingDamageNullifier: private
        /// Reflection으로 통일 접근 (base type까지 탐색).
        /// </summary>
        private static bool IsUnconditionalModifier(object component)
        {
            if (component == null) return false;
            try
            {
                // Restrictions 필드 탐색 (base type까지)
                System.Reflection.FieldInfo field = null;
                var current = component.GetType();
                while (field == null && current != null && current != typeof(object))
                {
                    field = current.GetField("Restrictions",
                        System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.DeclaredOnly);
                    current = current.BaseType;
                }

                if (field == null) return true; // Restrictions 필드 없음 → 무조건 적용

                var restrictions = field.GetValue(component)
                    as Kingmaker.Designers.Mechanics.Facts.Restrictions.RestrictionCalculator;
                if (restrictions == null) return true;

                var prop = restrictions.Property;
                // Property == null 또는 Property.Empty 이면 무조건 PASS (게임 로직과 동일)
                return prop == null || prop.Empty;
            }
            catch
            {
                // 탐색 실패 시 보수적으로 false 반환 (면역 판정 안 함 — 공격 가능으로 둠)
                return false;
            }
        }

        /// <summary>
        /// ★ v3.40.2: 유닛의 근접 공격이 적을 밀어내는지 (Push) 판별
        /// 1) 무기 Blueprint의 OnHitActions에 ContextActionPush 포함
        /// 2) 유닛 버프에 ForceMoveTriggerInitiator 컴포넌트 보유 (공격 시 밀어내기 발동)
        /// </summary>
        public static bool CanMeleeAttackCausePush(BaseUnitEntity unit)
        {
            if (unit == null) return false;

            try
            {
                // 1. 무기의 OnHitActions에서 ContextActionPush 검사
                var weapon = unit.Body?.PrimaryHand?.Weapon;
                if (weapon?.Blueprint != null)
                {
                    var onHitEffect = weapon.Blueprint.OnHitActions;
                    var actionList = onHitEffect?.OnHitActions;
                    if (actionList?.Actions != null)
                    {
                        foreach (var action in actionList.Actions)
                        {
                            if (action is ContextActionPush)
                                return true;
                        }
                    }
                }

                // 2. 유닛 버프에 ForceMoveTriggerInitiator 검사 (공격 시 밀어내기 트리거)
                if (unit.Facts.HasComponent<ForceMoveTriggerInitiator>(null))
                    return true;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CanMeleeAttackCausePush error: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// ★ v3.8.39: 유닛이 잠재력 초월(WarhammerFreeUltimateBuff)을 가지고 있는지 확인
        /// 이 버프가 있으면 궁극기 사용이 가능한 추가 턴
        /// </summary>
        public static bool HasFreeUltimateBuff(BaseUnitEntity unit)
        {
            if (unit == null) return false;

            try
            {
                return unit.Facts.HasComponent<WarhammerFreeUltimateBuff>(null);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// ★ v3.9.88: 유닛이 무기 전환 시 보너스 공격을 받는지 확인
        /// WeaponSetChangedTrigger가 있으면 무기 전환 시 ActionList 실행
        /// → ContextActionAddBonusAbilityUsage로 보너스 공격 부여 (Versatility 등)
        ///
        /// 게임 메커니즘: PrimaryHandAbilityGroup 공유 쿨다운
        /// - 무기 공격 사용 → 해당 그룹 전체 쿨다운 (같은 슬롯의 모든 무기)
        /// - 무기 세트 전환만으로는 쿨다운 우회 불가
        /// - WeaponSetChangedTrigger → ContextActionAddBonusAbilityUsage → IsBonusUsage=true → 쿨다운 우회
        /// </summary>
        public static bool HasWeaponSwitchBonusAttack(BaseUnitEntity unit)
        {
            if (unit == null) return false;

            try
            {
                return unit.Facts.HasComponent<WeaponSetChangedTrigger>(null);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// ★ v3.8.39: 능력이 궁극기(HeroicAct 또는 DesperateMeasure)인지 확인
        /// </summary>
        public static bool IsUltimateAbility(AbilityData ability)
        {
            if (ability?.Blueprint == null) return false;
            return ability.Blueprint.IsHeroicAct || ability.Blueprint.IsDesperateMeasure;
        }

        /// <summary>
        /// ★ v3.8.41: 궁극기 타겟 유형 분류 (실제 능력 데이터 기반)
        ///
        /// 실제 궁극기 분석 결과:
        /// - SelfBuff(Personal): Steady Superiority, Carnival of Misery, Overcharge,
        ///   Firearm Mastery, Unyielding Guard, Daring Breach
        /// - ImmediateAttack(적 타겟): Dispatch, Death Waltz, Wild Hunt, Dismantling Attack
        /// - AllyBuff(아군 타겟): Finest Hour!
        /// - AreaEffect(지점 타겟): Take and Hold, Orchestrated Firestorm
        /// </summary>
        public enum UltimateTargetType
        {
            Unknown,
            SelfBuff,         // Personal 타겟: 자기 강화/자원회복/방어오라 (대부분의 궁극기)
            ImmediateAttack,  // 적 타겟: 즉시 공격 (Dispatch, Death Waltz, Wild Hunt 등)
            AllyBuff,         // 아군 타겟: 아군 지원 (Finest Hour!)
            AreaEffect         // 지점 타겟: 구역 효과 (Take and Hold, Orchestrated Firestorm)
        }

        /// <summary>
        /// ★ v3.8.41: 궁극기 타겟 유형 판별 (블루프린트 플래그 기반)
        /// </summary>
        public static UltimateTargetType ClassifyUltimateTarget(AbilityData ability)
        {
            if (ability?.Blueprint == null) return UltimateTargetType.Unknown;

            var bp = ability.Blueprint;

            // 1. 적 타겟 = 즉시 공격 (Dispatch, Death Waltz, Wild Hunt, Dismantling Attack)
            if (bp.CanTargetEnemies)
                return UltimateTargetType.ImmediateAttack;

            // 2. 지점 타겟 = 구역 효과 (Take and Hold, Orchestrated Firestorm)
            if (bp.CanTargetPoint && !bp.CanTargetSelf)
                return UltimateTargetType.AreaEffect;

            // 3. 아군 타겟 (자기 제외) = 아군 버프 (Finest Hour!)
            if (bp.CanTargetFriends && !bp.CanTargetSelf)
                return UltimateTargetType.AllyBuff;

            // 4. Self 타겟 = 자기 강화 (대부분의 Personal 궁극기)
            //    Steady Superiority, Carnival, Overcharge, Firearm Mastery,
            //    Unyielding Guard, Daring Breach 등
            if (bp.CanTargetSelf)
                return UltimateTargetType.SelfBuff;

            return UltimateTargetType.Unknown;
        }

        /// <summary>
        /// ★ v3.8.41: 궁극기 상세 정보 구조체
        /// </summary>
        public struct UltimateInfo
        {
            public UltimateTargetType TargetType;
            public bool IsHeroicAct;
            public bool IsDesperateMeasure;
            public bool IsAoE;
            public float AoERadius;
            public bool CanTargetSelf;
            public bool CanTargetFriends;
            public bool CanTargetEnemies;
            public bool CanTargetPoint;
            public bool NotOffensive;
            public string EffectOnAlly;
            public string EffectOnEnemy;
        }

        /// <summary>
        /// ★ v3.8.41: 궁극기 상세 정보 조회
        /// </summary>
        public static UltimateInfo GetUltimateInfo(AbilityData ability)
        {
            var info = new UltimateInfo { TargetType = UltimateTargetType.Unknown };
            if (ability?.Blueprint == null) return info;

            var bp = ability.Blueprint;

            info.TargetType = ClassifyUltimateTarget(ability);
            info.IsHeroicAct = bp.IsHeroicAct;
            info.IsDesperateMeasure = bp.IsDesperateMeasure;
            info.IsAoE = bp.IsAoE || bp.IsAoEDamage;
            info.AoERadius = GetAoERadius(ability);
            info.CanTargetSelf = bp.CanTargetSelf;
            info.CanTargetFriends = bp.CanTargetFriends;
            info.CanTargetEnemies = bp.CanTargetEnemies;
            info.CanTargetPoint = bp.CanTargetPoint;
            info.NotOffensive = bp.NotOffensive;
            info.EffectOnAlly = bp.EffectOnAlly.ToString();
            info.EffectOnEnemy = bp.EffectOnEnemy.ToString();

            return info;
        }

        #endregion
    }
}
