using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem;  // ★ v3.8.66: EntityHelper.DistanceToInCells 확장 메서드
using Kingmaker.AI;  // ★ v3.9.70: AiBrainHelper.IsThreatningArea
using Kingmaker.UnitLogic.Abilities.Components.AreaEffects;  // ★ v3.9.70: AbilityAreaEffectRunAction, AbilityAreaEffectBuff
using Kingmaker.ElementsSystem;  // ★ v3.9.70: ActionList, GameAction
using Kingmaker.UnitLogic.Mechanics.Components;  // ★ v3.9.70: AddFactContextActions
using Kingmaker.Controllers;  // ★ v3.9.70: AreaEffectsController.CheckInertWarpEffect
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Items;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities;
using Pathfinding;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Abilities.Components.Base;
using Kingmaker.UnitLogic.Abilities.Components.CasterCheckers;
using Kingmaker.UnitLogic.Abilities.Components.Patterns;
using Kingmaker.UnitLogic.Mechanics.Actions;
using Kingmaker.UnitLogic.Parts;
using Kingmaker.Utility;
using Kingmaker.View.Covers;
using Kingmaker.RuleSystem;
using Kingmaker.RuleSystem.Rules;
using UnityEngine;
using CompanionAI_v3.Data;
using CompanionAI_v3.Settings;
using Kingmaker.UnitLogic;  // ★ v3.7.89: AOO API
using Kingmaker.UnitLogic.Buffs.Components;  // ★ v3.8.36: WarhammerAbilityRestriction
using Kingmaker.Blueprints.Classes.Experience;  // ★ v3.8.49: UnitDifficultyType
using Kingmaker.Designers.Mechanics.Facts;        // ★ v3.9.88: WeaponSetChangedTrigger
using Kingmaker.Designers.Mechanics.Facts.Damage; // ★ v3.40.6: WarhammerDamageModifier (면역 감지)
using Kingmaker.UnitLogic.FactLogic;        // ★ v3.40.2: ForceMoveTriggerInitiator (Push 감지)
using Kingmaker.UnitLogic.Mechanics;        // ★ v3.40.6: ContextValueType (면역 컴포넌트 평가)
using Kingmaker.UnitLogic.Mechanics.Damage; // ★ v3.40.6: DamageTypeMask (데미지 면역 감지)
using Kingmaker.Mechanics.Damage;           // ★ v3.40.6: DamageExtension.Contains
using Kingmaker.Enums;                       // ★ v3.28.0: Size (플랭킹 공격 방향)

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// 게임 API 래퍼 - 모든 게임 상호작용을 중앙화
    /// </summary>
    public static partial class CombatAPI
    {
        // ★ v3.8.80: GetAvailableAbilities 프레임 캐시
        // 같은 프레임 내 동일 유닛에 대한 반복 호출 방지 (Analyze + Plan = 4+회/프레임)
        private static string _cachedAbilitiesUnitId;
        private static int _cachedAbilitiesFrame;
        private static List<AbilityData> _cachedAbilitiesList;

        // ★ v3.9.10: Pattern counting zero-alloc 풀 (new HashSet<> 제거)
        private static readonly HashSet<BaseUnitEntity> _sharedUnitSet = new HashSet<BaseUnitEntity>();
        private static readonly HashSet<BaseUnitEntity> _sharedAllySet = new HashSet<BaseUnitEntity>();

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

        #region Target Scoring System

        /// <summary>
        /// 타겟 점수 정보
        /// ★ v3.0.1: 실제 데미지/HP 기반 정보 추가
        /// </summary>
        public class TargetScore
        {
            public BaseUnitEntity Target { get; set; }
            public float Score { get; set; }
            public string Reason { get; set; }
            public bool IsHittable { get; set; }
            public float Distance { get; set; }
            public float HPPercent { get; set; }
            // ★ v3.0.1: 실제 데미지 정보
            public int ActualHP { get; set; }
            public int PredictedMinDamage { get; set; }
            public int PredictedMaxDamage { get; set; }
            public bool CanKillInOneHit { get; set; }
            public bool CanKillInTwoHits { get; set; }
        }

        #region Accurate Damage Prediction (v3.0.1)

        /// <summary>
        /// ★ v3.0.1: 유닛의 실제 현재 HP 반환
        /// </summary>
        public static int GetActualHP(BaseUnitEntity unit)
        {
            if (unit == null) return 0;
            try
            {
                return unit.Health?.HitPointsLeft ?? 0;
            }
            // ★ v3.13.0: 안전한 기본값 — 1 (0은 "사망"으로 오판될 위험, 1은 "빈사")
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetActualHP failed for {unit?.CharacterName}: {ex.Message}");
                return 1;
            }
        }

        /// <summary>
        /// ★ v3.0.1: 유닛의 최대 HP 반환
        /// </summary>
        public static int GetActualMaxHP(BaseUnitEntity unit)
        {
            if (unit == null) return 0;
            try
            {
                return unit.Health?.MaxHitPoints ?? 0;
            }
            // ★ v3.13.0: 안전한 기본값 — 1 (0으로 나눔 방지, HP% 계산 안전)
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetActualMaxHP failed for {unit?.CharacterName}: {ex.Message}");
                return 1;
            }
        }

        /// <summary>
        /// ★ v3.8.49: 적 난도 등급 조회
        /// 게임 BlueprintUnit.DifficultyType (Swarm~ChapterBoss 7단계)
        /// </summary>
        public static UnitDifficultyType GetDifficultyType(BaseUnitEntity unit)
        {
            if (unit == null) return UnitDifficultyType.Common;
            try
            {
                return unit.Blueprint.DifficultyType;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetDifficultyType failed for {unit?.CharacterName}: {ex.Message}");
                return UnitDifficultyType.Common;
            }
        }

        /// <summary>
        /// ★ v3.0.1: 게임 API를 사용한 정확한 데미지 예측
        /// ability.GetDamagePrediction(target, casterPosition, context) 사용
        /// </summary>
        public static (int MinDamage, int MaxDamage, int Penetration) GetDamagePrediction(
            AbilityData ability,
            BaseUnitEntity target)
        {
            if (ability == null || target == null)
                return (0, 0, 0);

            try
            {
                var caster = ability.Caster as BaseUnitEntity;
                if (caster == null) return (0, 0, 0);

                // ★ 게임 API: AbilityDataHelper.GetDamagePrediction()
                var prediction = ability.GetDamagePrediction(target, caster.Position, null);
                if (prediction == null) return (0, 0, 0);

                return (prediction.MinDamage, prediction.MaxDamage, prediction.Penetration);
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetDamagePrediction error: {ex.Message}");
                return (0, 0, 0);
            }
        }

        /// <summary>
        /// ★ v3.0.1: 1타에 킬 가능 여부 (MinDamage >= CurrentHP)
        /// </summary>
        public static bool CanKillInOneHit(AbilityData ability, BaseUnitEntity target)
        {
            if (ability == null || target == null) return false;

            try
            {
                int hp = GetActualHP(target);
                if (hp <= 0) return false;

                var (minDamage, maxDamage, _) = GetDamagePrediction(ability, target);

                // 최소 데미지로도 킬 가능
                return minDamage >= hp;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CanKillInOneHit failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.0.1: 2타에 킬 가능 여부 (MaxDamage * 2 >= CurrentHP)
        /// </summary>
        public static bool CanKillInTwoHits(AbilityData ability, BaseUnitEntity target)
        {
            if (ability == null || target == null) return false;

            try
            {
                int hp = GetActualHP(target);
                if (hp <= 0) return false;

                var (minDamage, maxDamage, _) = GetDamagePrediction(ability, target);

                // 최대 데미지 2번으로 킬 가능
                return maxDamage * 2 >= hp;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CanKillInTwoHits failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.0.1: 예상 킬 확률 계산 (0.0 ~ 1.0)
        /// - 1.0 = 확실한 1타 킬 (MinDamage >= HP)
        /// - 0.5+ = 높은 확률의 1타 킬 (MaxDamage >= HP)
        /// - 낮음 = 여러 타 필요
        /// </summary>
        public static float CalculateKillProbability(AbilityData ability, BaseUnitEntity target)
        {
            if (ability == null || target == null) return 0f;

            try
            {
                int hp = GetActualHP(target);
                if (hp <= 0) return 1f;

                var (minDamage, maxDamage, _) = GetDamagePrediction(ability, target);
                if (maxDamage <= 0) return 0f;

                // 최소 데미지로도 킬 가능 → 100%
                if (minDamage >= hp) return 1.0f;

                // 최대 데미지로 킬 가능 → 확률 계산 (데미지 분포가 균일하다고 가정)
                if (maxDamage >= hp)
                {
                    // (maxDamage - hp) / (maxDamage - minDamage)
                    float range = maxDamage - minDamage;
                    if (range <= 0) return 0.5f;
                    return (float)(maxDamage - hp) / range;
                }

                // 2타 킬 가능성
                if (maxDamage * 2 >= hp)
                {
                    return 0.25f;  // 2타 필요
                }

                // 3타 이상 필요
                return 0.1f;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CalculateKillProbability failed: {ex.Message}");
                return 0f;
            }
        }

        /// <summary>
        /// ★ v3.0.44: 예상 평균 데미지 계산
        /// </summary>
        public static float EstimateDamage(AbilityData ability, BaseUnitEntity target)
        {
            if (ability == null || target == null) return 0f;

            try
            {
                var (minDamage, maxDamage, _) = GetDamagePrediction(ability, target);
                return (minDamage + maxDamage) / 2f;
            }
            catch
            {
                // 폴백: 레벨 기반 추정
                return Settings.SC.FallbackEstimateDamage;
            }
        }

        #endregion

        /// <summary>
        /// 모든 적에 대해 타겟 점수 계산 - SituationAnalyzer에서 사용
        /// ★ v3.0.1: 실제 데미지 예측 기반 스코어링
        /// </summary>
        public static List<TargetScore> ScoreAllTargets(
            BaseUnitEntity unit,
            List<BaseUnitEntity> enemies,
            AbilityData attackAbility,
            RangePreference preference)
        {
            var scores = new List<TargetScore>();
            if (unit == null || enemies == null) return scores;

            foreach (var enemy in enemies)
            {
                if (enemy == null || enemy.LifeState.IsDead) continue;

                var score = new TargetScore
                {
                    Target = enemy,
                    Distance = GetDistance(unit, enemy),
                    HPPercent = GetHPPercent(enemy),
                    ActualHP = GetActualHP(enemy),
                    IsHittable = false,
                    Score = 0f,
                    Reason = ""
                };

                // 공격 가능 여부
                if (attackAbility != null)
                {
                    var target = new TargetWrapper(enemy);
                    string reason;
                    score.IsHittable = CanUseAbilityOn(attackAbility, target, out reason);
                    if (!score.IsHittable)
                    {
                        score.Reason = reason;
                        // ★ v3.0.14: Hittable=false 원인 로깅
                        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Not hittable: {enemy.CharacterName} - {reason} (dist={score.Distance:F1}m, ability={attackAbility.Name})");
                    }

                    // ★ v3.0.1: 실제 데미지 예측
                    var (minDmg, maxDmg, _) = GetDamagePrediction(attackAbility, enemy);
                    score.PredictedMinDamage = minDmg;
                    score.PredictedMaxDamage = maxDmg;
                    score.CanKillInOneHit = minDmg >= score.ActualHP && score.ActualHP > 0;
                    score.CanKillInTwoHits = maxDmg * 2 >= score.ActualHP && score.ActualHP > 0;
                }

                // ★ v3.0.1: 실제 데미지 기반 점수 계산
                score.Score = CalculateTargetScore(unit, enemy, attackAbility, score.IsHittable, preference, score);

                scores.Add(score);
            }

            return scores.OrderByDescending(s => s.Score).ToList();
        }

        /// <summary>
        /// 최적 타겟 찾기
        /// </summary>
        public static BaseUnitEntity FindBestTarget(
            BaseUnitEntity unit,
            List<BaseUnitEntity> enemies,
            AbilityData attackAbility,
            RangePreference preference)
        {
            var scores = ScoreAllTargets(unit, enemies, attackAbility, preference);

            // 공격 가능한 타겟 중 최고 점수
            var hittable = scores.FirstOrDefault(s => s.IsHittable);
            if (hittable != null)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Best target: {hittable.Target.CharacterName} (score={hittable.Score:F1})");
                return hittable.Target;
            }

            // 공격 불가 시 가장 가까운 적
            var nearest = scores.OrderBy(s => s.Distance).FirstOrDefault();
            return nearest?.Target;
        }

        /// <summary>
        /// ★ v3.0.1: 실제 데미지 기반 타겟 점수 계산
        /// - 1타 킬 가능: +50 보너스
        /// - 2타 킬 가능: +25 보너스
        /// - HP가 낮을수록: +점수 (1/HP 기반, 게임 AI와 동일)
        /// - 거리: 근접/원거리 선호도에 따라 보너스
        /// </summary>
        private static float CalculateTargetScore(
            BaseUnitEntity caster,
            BaseUnitEntity target,
            AbilityData attackAbility,
            bool isHittable,
            RangePreference preference,
            TargetScore scoreData = null)
        {
            float score = 0f;

            // 기본 점수: 공격 가능 여부
            if (isHittable) score += 100f;

            // ★ v3.0.1: 1타 킬 가능 최우선 (+50)
            if (scoreData != null && scoreData.CanKillInOneHit && isHittable)
            {
                score += 50f;
                if (Main.IsDebugEnabled) Main.LogDebug($"[Scoring] {target.CharacterName}: +50 (1-hit kill possible, HP={scoreData.ActualHP}, MinDmg={scoreData.PredictedMinDamage})");
            }
            // 2타 킬 가능 (+25)
            else if (scoreData != null && scoreData.CanKillInTwoHits && isHittable)
            {
                score += 25f;
                if (Main.IsDebugEnabled) Main.LogDebug($"[Scoring] {target.CharacterName}: +25 (2-hit kill possible)");
            }

            // ★ v3.0.1: HP 점수 - 게임 AI와 동일한 방식 (1/HP)
            // 낮은 HP = 높은 점수 (최대 +30)
            int actualHP = scoreData?.ActualHP ?? GetActualHP(target);
            if (actualHP > 0)
            {
                // 1000 / HP 로 정규화 (HP 100 → +10, HP 50 → +20, HP 30 → +33)
                float hpScore = Math.Min(30f, 1000f / actualHP);
                score += hpScore;
            }
            else
            {
                // 폴백: HP% 기반
                float hpPercent = GetHPPercent(target);
                score += (100f - hpPercent) * 0.3f;  // 최대 +30
            }

            // 거리 점수: 가까울수록 높은 점수
            float distance = GetDistance(caster, target);
            if (distance < 30f)
            {
                score += (30f - distance) * 0.3f;  // 최대 +9
            }

            // RangePreference 보너스
            if (preference == RangePreference.PreferMelee && distance <= 3f)
            {
                score += 15f;  // 근접 범위 내
            }
            else if (preference == RangePreference.PreferRanged && distance >= 5f && distance <= 15f)
            {
                score += 12f;  // 최적 원거리
            }

            // ★ v3.0.1: 킬 확률 보너스 (데미지 예측 기반)
            if (attackAbility != null && isHittable)
            {
                float killProb = CalculateKillProbability(attackAbility, target);
                score += killProb * 20f;  // 최대 +20 (100% 킬 확률)
            }

            return score;
        }

        /// <summary>
        /// Legacy 호환: 이전 시그니처 유지
        /// </summary>
        private static float CalculateTargetScore(
            BaseUnitEntity caster,
            BaseUnitEntity target,
            bool isHittable,
            RangePreference preference)
        {
            return CalculateTargetScore(caster, target, null, isHittable, preference, null);
        }

        /// <summary>
        /// 타겟이 실제로 공격 가능한지 확인 (Hittable check)
        /// </summary>
        public static bool CheckIfHittable(BaseUnitEntity unit, BaseUnitEntity target, AbilityData attackAbility)
        {
            if (unit == null || target == null) return false;

            if (attackAbility != null)
            {
                var targetWrapper = new TargetWrapper(target);
                string reason;
                return CanUseAbilityOn(attackAbility, targetWrapper, out reason);
            }

            // 능력 없으면 거리로만 추정
            float dist = GetDistance(unit, target);
            return dist <= 15f;
        }

        #endregion

        #region Ability Type Detection

        // ★ v3.8.61: IsMomentumGeneratingAbility 제거 — 호출부 없는 데드코드 (string 매칭만)

        /// <summary>
        /// 능력이 방어 자세인지 확인
        /// ★ v3.8.61: String 매칭 제거 → AbilityDatabase 위임 (GUID + Flag 기반)
        /// </summary>
        public static bool IsDefensiveStanceAbility(AbilityData ability)
        {
            return AbilityDatabase.IsDefensiveStance(ability);
        }

        /// <summary>
        /// 능력이 Heroic Act인지 확인
        /// </summary>
        public static bool IsHeroicActAbility(AbilityData ability)
        {
            return AbilityDatabase.IsHeroicAct(ability);
        }

        /// <summary>
        /// 능력이 Righteous Fury (Revel in Slaughter)인지 확인
        /// ★ v3.8.61: 플레이스홀더 GUID + String 매칭 제거 → AbilityDatabase 위임
        /// </summary>
        public static bool IsRighteousFuryAbility(AbilityData ability)
        {
            return AbilityDatabase.IsRighteousFury(ability);
        }

        /// <summary>
        /// ★ v3.0.58: 능력의 정확한 사거리 반환 (게임 API 사용)
        /// ★ v3.8.06: 모든 AOE 능력 사거리 정확히 계산
        /// - Pattern AOE (Cone, Circle, Ray 등): PatternSettings.Pattern.Radius
        /// - Circle AOE (AbilityTargetsAround): AoERadius
        /// - LidlessStare 등 Range="Unlimited"이지만 실제로는 Pattern.Radius가 범위
        /// </summary>
        public static float GetAbilityRange(AbilityData ability)
        {
            if (ability == null) return 0f;

            try
            {
                var bp = ability.Blueprint;
                if (bp == null) return 0f;

                // ★ v3.8.06: 모든 Pattern/AOE 능력 처리 (게임 API 통합 사용)
                // PatternSettings는 WarhammerAbilityAttackDelivery + AbilityTargetsInPattern 모두 포함
                var patternSettings = bp.PatternSettings;
                if (patternSettings != null)
                {
                    int patternRadius = patternSettings.Pattern?.Radius ?? 0;
                    if (patternRadius > 0)
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAbilityRange: {bp.name} is Pattern AOE, Radius={patternRadius}");
                        return patternRadius;
                    }
                }

                // ★ v3.8.06: Circle AOE 처리 (IAbilityAoERadiusProvider)
                int aoERadius = bp.AoERadius;
                if (aoERadius > 0)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAbilityRange: {bp.name} is Circle AOE, Radius={aoERadius}");
                    return aoERadius;
                }

                // 1. Blueprint.GetRange() 사용 (게임 공식 API)
                int baseRange = bp.GetRange();

                if (baseRange >= 0)
                {
                    // Personal(0), Touch(1), Unlimited(100000), Custom(CustomRange)
                    if (baseRange == 0) return 0f;  // Personal - 자신만
                    if (baseRange >= 100000) return 100f;  // Unlimited
                    return baseRange;  // 게임 단위 = 미터
                }

                // 2. Weapon 타입 (-1) - 무기 사거리 사용
                if (ability.Weapon != null)
                {
                    return ability.Weapon.AttackRange;
                }

                // 3. ★ v3.8.63: 무기 타입인데 무기 없음 — 근접 사거리 폴백
                // 기존 15f(원거리)는 비무장 능력에 대해 잘못된 값이었음
                // bp.GetRange() == -1 + Weapon == null = 비무장/근접 능력
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAbilityRange: {bp.name} has weapon-type range but no weapon — fallback to melee range");
                return GridCellSize * 2;  // 약 2.7m (근접 2셀)
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAbilityRange error: {ex.Message}");
                return GridCellSize * 2;  // ★ v3.8.63: 에러 시에도 근접 폴백 (15f보다 안전)
            }
        }

        /// <summary>
        /// 능력이 무제한 사거리인지 확인
        /// ★ v3.8.06: 모든 AOE 능력 처리 (Pattern/Circle AOE는 실제로 제한 범위)
        /// </summary>
        public static bool IsUnlimitedRange(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                var bp = ability.Blueprint;
                if (bp == null) return false;

                // ★ v3.8.06: Pattern AOE는 무제한이 아님 (실제 범위는 Pattern.Radius)
                if (bp.PatternSettings != null)
                {
                    int patternRadius = bp.PatternSettings.Pattern?.Radius ?? 0;
                    if (patternRadius > 0) return false;
                }

                // ★ v3.8.06: Circle AOE도 무제한이 아님
                if (bp.AoERadius > 0) return false;

                return bp.Range == AbilityRange.Unlimited;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsUnlimitedRange failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.7.81: 특정 위치에서 타겟에게 능력 사용 가능한지 확인
        /// 이동 후 공격 검증에 사용
        /// ★ v3.9.04: 게임 API (CanTargetFromNode) 기반으로 전환
        /// 기존 LosCalculations.HasLos()는 게임의 CanUseAbilityOn()과 결과 불일치 발생
        /// → Analyzer(hittable)와 Validator(reachable) 판정이 달라 공격 누락
        /// </summary>
        public static bool CanReachTargetFromPosition(AbilityData ability, Vector3 fromPosition, BaseUnitEntity target)
        {
            if (ability == null || target == null) return false;

            try
            {
                var fromNode = fromPosition.GetNearestNodeXZ() as Kingmaker.Pathfinding.CustomGridNodeBase;
                if (fromNode == null)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CanReachFromPos: Node lookup failed, allowing");
                    return true;
                }

                // ★ v3.9.04: 게임 API 위임 — Analyzer와 동일한 검증 기준 사용
                bool canTarget = CanTargetFromPosition(ability, fromNode, target, out string reason);

                if (Main.IsDebugEnabled)
                {
                    if (!canTarget)
                        Main.LogDebug($"[CombatAPI] CanReachFromPos: {ability.Name} -> {target.CharacterName}, BLOCKED: {reason}");
                    else
                        Main.LogDebug($"[CombatAPI] CanReachFromPos: {ability.Name} -> {target.CharacterName}, OK");
                }

                return canTarget;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CanReachTargetFromPosition error: {ex.Message}");
                return true;  // 에러 시 허용 (안전하게)
            }
        }

        #endregion

        #region AOE Support (v3.1.16)

        /// <summary>
        /// ★ v3.1.16: AOE 패턴 설정 조회
        /// </summary>
        public static Kingmaker.UnitLogic.Abilities.Components.Base.IAbilityAoEPatternProvider GetPatternSettings(AbilityData ability)
        {
            try
            {
                return ability?.GetPatternSettings();
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetPatternSettings failed for {ability?.Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ★ v3.1.16: AOE 반경 조회 (타일 단위)
        /// </summary>
        public static float GetAoERadius(AbilityData ability)
        {
            try
            {
                var pattern = ability?.GetPatternSettings()?.Pattern;
                if (pattern != null)
                    return pattern.Radius;

                return ability?.Blueprint?.AoERadius ?? 0f;
            }
            // ★ v3.13.0: 로깅 추가 (기본값 0f는 이미 보수적 — AoE 무시)
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetAoERadius failed for {ability?.Name}: {ex.Message}");
                return 0f;
            }
        }

        /// <summary>
        /// ★ v3.1.16: AOE 패턴 타입 조회
        /// </summary>
        public static Kingmaker.Blueprints.PatternType? GetPatternType(AbilityData ability)
        {
            try
            {
                return ability?.GetPatternSettings()?.Pattern?.Type;
            }
            // ★ v3.13.0: 로깅 추가 (기본값 null은 이미 보수적 — 패턴 불명)
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetPatternType failed for {ability?.Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ★ v3.1.16: AOE 대상 타입 조회 (Enemy/Ally/Any)
        /// </summary>
        public static Kingmaker.UnitLogic.Abilities.Components.TargetType GetAoETargetType(AbilityData ability)
        {
            try
            {
                return ability?.GetPatternSettings()?.Targets ?? Kingmaker.UnitLogic.Abilities.Components.TargetType.Enemy;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAoETargetType failed for {ability?.Name}: {ex.Message}");
                return Kingmaker.UnitLogic.Abilities.Components.TargetType.Enemy;
            }
        }

        /// <summary>
        /// ★ v3.5.74: Point 타겟 능력인지 확인 (게임 API 우선)
        /// 게임 네이티브 IsAOE 먼저 체크 + 기존 로직 폴백
        /// </summary>
        public static bool IsPointTargetAbility(AbilityData ability)
        {
            try
            {
                if (ability == null) return false;

                // ★ v3.5.74: 게임 네이티브 IsAOE 먼저 체크
                if (ability.IsAOE) return true;

                var bp = ability.Blueprint;
                if (bp == null || !bp.CanTargetPoint) return false;

                // 패턴 설정에서 실제 반경 확인
                var pattern = ability.GetPatternSettings()?.Pattern;
                if (pattern != null)
                    return pattern.Radius > 0;

                // Blueprint AOE 반경 폴백
                return bp.AoERadius > 0;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsPointTargetAbility failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.1.16: Point 타겟에 능력 사용 가능 검증
        /// </summary>
        public static bool CanUseAbilityOnPoint(AbilityData ability, Vector3 point, out string reason)
        {
            reason = null;
            if (ability == null) { reason = "Null ability"; return false; }

            try
            {
                var target = new TargetWrapper(point);
                AbilityData.UnavailabilityReasonType? unavailable;
                bool canTarget = ability.CanTarget(target, out unavailable);

                if (!canTarget && unavailable.HasValue)
                    reason = unavailable.Value.ToString();

                return canTarget;
            }
            catch (Exception ex)
            {
                reason = $"Exception: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// ★ v3.1.19: AOE 패턴 각도 조회 (Cone/Sector용, 단위: degree)
        /// Reflection 제거 - pattern.Angle 프로퍼티 직접 사용
        /// </summary>
        public static float GetPatternAngle(AbilityData ability)
        {
            try
            {
                var pattern = ability?.GetPatternSettings()?.Pattern;
                if (pattern == null) return 90f;

                // ★ v3.1.19: 게임 API 직접 사용 (AoEPattern.Angle 프로퍼티)
                // Reflection 대신 public 프로퍼티 사용 - 이미 full-angle
                return pattern.Angle;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetPatternAngle error: {ex.Message}");
                return 90f;
            }
        }

        /// <summary>
        /// ★ v3.1.18: 패턴이 방향성 패턴인지 확인 (Cone/Ray/Sector)
        /// ★ v3.8.09: 이 함수는 PatternType만 체크 - CanBeDirectional과 동일
        /// 실제 IsDirectional 판정은 GetActualIsDirectional() 사용!
        /// </summary>
        public static bool IsDirectionalPattern(Kingmaker.Blueprints.PatternType? patternType)
        {
            if (!patternType.HasValue) return false;

            switch (patternType.Value)
            {
                case Kingmaker.Blueprints.PatternType.Cone:
                case Kingmaker.Blueprints.PatternType.Ray:
                case Kingmaker.Blueprints.PatternType.Sector:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// ★ v3.8.09: 게임의 실제 IsDirectional 로직 구현
        /// - Non-Custom 패턴: AbilityAoEPatternSettings.m_Directional 필드
        /// - Custom 패턴: AoEPattern.IsDirectional → BlueprintAttackPattern.IsDirectional
        /// </summary>
        public static bool GetActualIsDirectional(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                var patternSettings = ability.GetPatternSettings();
                if (patternSettings == null) return false;

                var pattern = patternSettings.Pattern;
                if (pattern == null) return false;

                // Custom 패턴: AoEPattern.IsDirectional 프로퍼티 직접 사용
                if (pattern.IsCustom)
                {
                    try
                    {
                        return pattern.IsDirectional;  // BlueprintAttackPattern.IsDirectional
                    }
                    catch (Exception ex)
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetActualIsDirectional(custom) failed for {ability?.Name}: {ex.Message}");
                        return false;
                    }
                }

                // Non-Custom 패턴: m_Directional 필드 (Reflection)
                if (!pattern.CanBeDirectional) return false;  // Ray/Cone/Sector만 가능

                // AbilityAoEPatternSettings에서 m_Directional 필드 가져오기
                var settingsType = patternSettings.GetType();
                var directionalField = settingsType.GetField("m_Directional",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (directionalField != null)
                {
                    bool result = (bool)directionalField.GetValue(patternSettings);
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] {ability.Name}: m_Directional field = {result}");
                    return result;
                }

                // 필드를 찾지 못하면 타입 기반 폴백 (CanBeDirectional이면 true 가정)
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] {ability.Name}: m_Directional field not found, using CanBeDirectional fallback");
                return pattern.CanBeDirectional;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetActualIsDirectional error for {ability?.Name}: {ex.Message}");
                return IsDirectionalPattern(GetPatternType(ability));  // 폴백
            }
        }

        /// <summary>
        /// ★ v3.8.09: AbilityCustomRam 컴포넌트 사용 여부 (Slash 공격 등)
        /// AbilityCustomRam은 Pattern이 null이지만 동적으로 Ray 패턴 생성
        /// </summary>
        public static bool IsRamAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                var bp = ability.Blueprint;
                if (bp == null) return false;

                // AbilityCustomRam 컴포넌트 체크
                return bp.GetComponent<Kingmaker.UnitLogic.Abilities.Components.AbilityCustomRam>() != null;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsRamAbility failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.8.09: Ram 능력의 관통 여부 (m_RamThrough)
        /// true면 경로의 모든 적 타격, false면 첫 적에서 멈춤
        /// </summary>
        public static bool IsRamThroughAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                var bp = ability.Blueprint;
                if (bp == null) return false;

                var ramComponent = bp.GetComponent<Kingmaker.UnitLogic.Abilities.Components.AbilityCustomRam>();
                if (ramComponent == null) return false;

                // m_RamThrough 필드 (Reflection)
                var ramThroughField = ramComponent.GetType().GetField("m_RamThrough",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (ramThroughField != null)
                {
                    return (bool)ramThroughField.GetValue(ramComponent);
                }

                return false;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsRamThroughAbility failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Self-Targeted AOE (v3.1.23)

        /// <summary>
        /// ★ v3.1.23: 자신 타겟 AOE 공격인지 확인
        /// Bladedance 같은 능력: Range=Personal, CanTargetSelf, 인접 유닛 공격
        /// </summary>
        public static bool IsSelfTargetedAoEAttack(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                var bp = ability.Blueprint;
                if (bp == null) return false;

                // Range=Personal + CanTargetSelf 체크
                if (bp.Range != AbilityRange.Personal) return false;
                if (!bp.CanTargetSelf) return false;

                // DangerousAoE로 분류된 능력만
                return AbilityDatabase.IsDangerousAoE(ability);
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsSelfTargetedAoEAttack failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.8.50: 근접 AOE 능력 감지 (유닛 타겟형)
        /// BladeDance(Self-Target)는 제외 — 적을 직접 타겟하는 근접 AOE만 감지
        /// 게임 AbilityMeleeBurst + Pattern 기반 근접 스플래시 공격
        /// </summary>
        public static bool IsMeleeAoEAbility(AbilityData ability)
        {
            if (ability == null) return false;
            try
            {
                // Self-Target AOE는 이미 Phase 4.3에서 별도 처리
                if (IsSelfTargetedAoEAttack(ability)) return false;

                // 근접 능력이어야 함
                if (!ability.IsMelee) return false;

                // AOE 패턴이 있어야 함 (게임 네이티브 + 커스텀 감지)
                if (CombatHelpers.IsAoEAbility(ability)) return true;

                // 패턴 설정 직접 확인 (IsAoEAbility에서 놓칠 수 있는 케이스)
                if (ability.GetPatternSettings() != null) return true;

                return false;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsMeleeAoEAbility failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.6.3: 인접 아군 수 계산 (Self-Targeted AOE 안전성 체크)
        /// radius는 타일 단위 (기본 2타일 ≈ 2.7m)
        /// </summary>
        public static int CountAdjacentAllies(BaseUnitEntity unit, float radius = 2f)  // 타일
        {
            if (unit == null) return 0;

            try
            {
                int count = 0;
                var allUnits = Game.Instance?.State?.AllBaseAwakeUnits;
                if (allUnits == null) return 0;

                foreach (var other in allUnits)
                {
                    if (other == null || other == unit) continue;
                    if (other.LifeState.IsDead) continue;

                    // 아군 판별
                    bool isAlly = unit.IsPlayerFaction == other.IsPlayerFaction;
                    if (!isAlly) continue;

                    // ★ v3.6.3: 타일 단위로 변환
                    float distTiles = MetersToTiles(Vector3.Distance(unit.Position, other.Position));
                    if (distTiles <= radius)
                        count++;
                }

                return count;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CountAdjacentAllies failed for {unit?.CharacterName}: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// ★ v3.6.3: 인접 적 수 계산 (Self-Targeted AOE 효율성 체크)
        /// radius는 타일 단위 (기본 2타일 ≈ 2.7m)
        /// </summary>
        public static int CountAdjacentEnemies(BaseUnitEntity unit, float radius = 2f)  // 타일
        {
            if (unit == null) return 0;

            try
            {
                int count = 0;
                var allUnits = Game.Instance?.State?.AllBaseAwakeUnits;
                if (allUnits == null) return 0;

                foreach (var other in allUnits)
                {
                    if (other == null || other == unit) continue;
                    if (other.LifeState.IsDead) continue;

                    // 적 판별
                    bool isEnemy = (unit.IsPlayerFaction && other.IsPlayerEnemy) ||
                                   (!unit.IsPlayerFaction && !other.IsPlayerEnemy);
                    if (!isEnemy) continue;

                    // ★ v3.6.3: 타일 단위로 변환
                    float distTiles = MetersToTiles(Vector3.Distance(unit.Position, other.Position));
                    if (distTiles <= radius)
                        count++;
                }

                return count;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CountAdjacentEnemies failed for {unit?.CharacterName}: {ex.Message}");
                return 0;
            }
        }

        #endregion

        #region Pattern Info Cache (v3.1.19)

        /// <summary>
        /// ★ v3.1.19: AOE 패턴 정보 통합 클래스
        /// ★ v3.8.09: IsRamAbility, IsRamThrough 추가
        /// </summary>
        public class PatternInfo
        {
            public Kingmaker.Blueprints.PatternType? Type { get; set; }
            public float Radius { get; set; }
            public float Angle { get; set; }
            public Kingmaker.UnitLogic.Abilities.Components.TargetType TargetType { get; set; }
            public bool IsDirectional { get; set; }
            public bool CanBeDirectional { get; set; }  // ★ v3.8.09: Type만으로 판단
            public bool IsRamAbility { get; set; }      // ★ v3.8.09: AbilityCustomRam 사용
            public bool IsRamThrough { get; set; }      // ★ v3.8.09: 관통 여부
            public bool IsValid => Radius > 0 || IsRamAbility;
        }

        private static Dictionary<string, PatternInfo> PatternCache = new Dictionary<string, PatternInfo>();

        /// <summary>
        /// ★ v3.1.19: 패턴 정보 조회 (캐싱)
        /// ★ v3.8.09: GetActualIsDirectional() 사용으로 정확한 IsDirectional 판정
        /// </summary>
        public static PatternInfo GetPatternInfo(AbilityData ability)
        {
            try
            {
                var guid = ability?.Blueprint?.AssetGuid?.ToString();
                if (string.IsNullOrEmpty(guid)) return null;

                if (PatternCache.TryGetValue(guid, out var cached))
                    return cached;

                var patternType = GetPatternType(ability);
                bool canBeDirectional = IsDirectionalPattern(patternType);  // Type 기반 (Ray/Cone/Sector)
                bool actualIsDirectional = GetActualIsDirectional(ability); // 게임 실제 로직

                // ★ v3.8.09: Ram 능력 체크
                bool isRam = IsRamAbility(ability);
                bool isRamThrough = isRam && IsRamThroughAbility(ability);

                var info = new PatternInfo
                {
                    Type = patternType,
                    Radius = GetAoERadius(ability),
                    Angle = GetPatternAngle(ability),
                    TargetType = GetAoETargetType(ability),
                    CanBeDirectional = canBeDirectional,
                    IsDirectional = actualIsDirectional,
                    IsRamAbility = isRam,
                    IsRamThrough = isRamThrough
                };

                // ★ v3.8.09: 디버그 로그 (새 능력일 때만)
                if (actualIsDirectional != canBeDirectional || isRam)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] PatternInfo for {ability.Name}: Type={patternType}, " +
                        $"CanBeDirectional={canBeDirectional}, IsDirectional={actualIsDirectional}, " +
                        $"IsRam={isRam}, RamThrough={isRamThrough}, Radius={info.Radius}");
                }

                PatternCache[guid] = info;
                return info;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// ★ v3.1.19: 패턴 캐시 클리어 (전투 종료 시 호출)
        /// </summary>
        public static void ClearPatternCache()
        {
            PatternCache.Clear();
            Main.LogDebug("[CombatAPI] Pattern cache cleared");
        }

        #endregion

        #region Game Pattern API (v3.5.39)

        /// <summary>
        /// ★ v3.5.39: 게임 API를 통해 AOE 패턴의 영향받는 노드들 조회
        /// 게임과 동일한 정확한 타일 기반 계산
        /// </summary>
        public static OrientedPatternData GetAffectedNodes(
            AbilityData ability,
            Vector3 targetPosition,
            Vector3 casterPosition)
        {
            try
            {
                if (ability == null) return OrientedPatternData.Empty;

                var target = new TargetWrapper(targetPosition);
                return ability.GetPattern(target, casterPosition);
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAffectedNodes error: {ex.Message}");
                return OrientedPatternData.Empty;
            }
        }

        /// <summary>
        /// ★ v3.5.39: 게임 API를 통해 패턴 내 적 수 계산
        /// Circle, Cone, Ray 모든 패턴에서 정확하게 작동
        /// </summary>
        public static int CountEnemiesInPattern(
            AbilityData ability,
            Vector3 targetPosition,
            Vector3 casterPosition,
            List<BaseUnitEntity> enemies)
        {
            try
            {
                if (ability == null || enemies == null || enemies.Count == 0)
                    return 0;

                var pattern = GetAffectedNodes(ability, targetPosition, casterPosition);
                if (pattern.IsEmpty) return 0;

                // ★ v3.9.10: new HashSet<> 제거 → 정적 풀 재사용
                _sharedUnitSet.Clear();
                for (int i = 0; i < enemies.Count; i++)
                    _sharedUnitSet.Add(enemies[i]);

                // ★ v3.9.22: Remove로 중복 방지 — 대형 유닛(4x4)이 여러 타일 점유 시 1회만 카운트
                int count = 0;
                foreach (var node in pattern.Nodes)
                {
                    if (node.TryGetUnit(out var unit) &&
                        unit is BaseUnitEntity baseUnit &&
                        _sharedUnitSet.Remove(baseUnit))
                    {
                        count++;
                    }
                }

                return count;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CountEnemiesInPattern error: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// ★ v3.5.39: 게임 API를 통해 패턴 내 아군 수 계산 (자신 제외)
        /// </summary>
        public static int CountAlliesInPattern(
            AbilityData ability,
            Vector3 targetPosition,
            Vector3 casterPosition,
            BaseUnitEntity caster,
            List<BaseUnitEntity> allies)
        {
            try
            {
                if (ability == null || allies == null || allies.Count == 0)
                    return 0;

                var pattern = GetAffectedNodes(ability, targetPosition, casterPosition);
                if (pattern.IsEmpty) return 0;

                // ★ v3.9.10: new HashSet<> 제거 → 정적 풀 재사용
                _sharedAllySet.Clear();
                for (int i = 0; i < allies.Count; i++)
                    _sharedAllySet.Add(allies[i]);

                // ★ v3.9.22: Remove로 중복 방지 — 대형 유닛 다중 타일 점유 시 1회만 카운트
                int count = 0;
                foreach (var node in pattern.Nodes)
                {
                    if (node.TryGetUnit(out var unit) &&
                        unit is BaseUnitEntity baseUnit &&
                        baseUnit != caster &&
                        _sharedAllySet.Remove(baseUnit))
                    {
                        count++;
                    }
                }

                return count;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CountAlliesInPattern error: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// ★ v3.9.10: 패턴 1회 계산으로 적/아군 수 동시 카운트
        /// GetAffectedNodes 중복 호출 제거 — AttackPlanner 이중 호출 최적화
        /// </summary>
        public static void CountUnitsInPattern(
            AbilityData ability,
            Vector3 targetPosition,
            Vector3 casterPosition,
            BaseUnitEntity caster,
            List<BaseUnitEntity> enemies,
            List<BaseUnitEntity> allies,
            out int enemyCount,
            out int allyCount)
        {
            enemyCount = 0;
            allyCount = 0;

            try
            {
                if (ability == null) return;

                var pattern = GetAffectedNodes(ability, targetPosition, casterPosition);
                if (pattern.IsEmpty) return;

                _sharedUnitSet.Clear();
                if (enemies != null)
                    for (int i = 0; i < enemies.Count; i++)
                        _sharedUnitSet.Add(enemies[i]);

                _sharedAllySet.Clear();
                if (allies != null)
                    for (int i = 0; i < allies.Count; i++)
                        _sharedAllySet.Add(allies[i]);

                // ★ v3.9.22: Remove로 중복 방지 — 대형 유닛 다중 타일 점유 시 1회만 카운트
                foreach (var node in pattern.Nodes)
                {
                    if (node.TryGetUnit(out var unit) && unit is BaseUnitEntity baseUnit)
                    {
                        if (_sharedUnitSet.Remove(baseUnit))
                            enemyCount++;
                        if (baseUnit != caster && _sharedAllySet.Remove(baseUnit))
                            allyCount++;
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CountUnitsInPattern error: {ex.Message}");
            }
        }

        /// <summary>
        /// ★ v3.5.39: 특정 유닛이 패턴 내에 있는지 확인
        /// </summary>
        public static bool IsUnitInPattern(
            AbilityData ability,
            Vector3 targetPosition,
            Vector3 casterPosition,
            BaseUnitEntity unit)
        {
            try
            {
                if (ability == null || unit == null) return false;

                var pattern = GetAffectedNodes(ability, targetPosition, casterPosition);
                if (pattern.IsEmpty) return false;

                // 유닛이 점유한 모든 노드 확인
                foreach (var occupiedNode in unit.GetOccupiedNodes())
                {
                    if (pattern.Contains(occupiedNode))
                        return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsUnitInPattern error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.5.39: 패턴 내 모든 유닛 조회 (적/아군 구분 없이)
        /// </summary>
        public static List<BaseUnitEntity> GetUnitsInPattern(
            AbilityData ability,
            Vector3 targetPosition,
            Vector3 casterPosition)
        {
            var result = new List<BaseUnitEntity>();
            try
            {
                if (ability == null) return result;

                var pattern = GetAffectedNodes(ability, targetPosition, casterPosition);
                if (pattern.IsEmpty) return result;

                var seen = new HashSet<BaseUnitEntity>();
                foreach (var node in pattern.Nodes)
                {
                    if (node.TryGetUnit(out var unit) &&
                        unit is BaseUnitEntity baseUnit &&
                        !seen.Contains(baseUnit))
                    {
                        seen.Add(baseUnit);
                        result.Add(baseUnit);
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetUnitsInPattern error: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// ★ v3.5.39: AOE 평가 - 적 점수와 아군 피해를 함께 계산
        /// </summary>
        public static (int enemyHits, int allyHits, int playerPartyHits) EvaluateAoEPosition(
            AbilityData ability,
            Vector3 targetPosition,
            Vector3 casterPosition,
            BaseUnitEntity caster,
            List<BaseUnitEntity> enemies,
            List<BaseUnitEntity> allies)
        {
            try
            {
                if (ability == null) return (0, 0, 0);

                var pattern = GetAffectedNodes(ability, targetPosition, casterPosition);
                if (pattern.IsEmpty) return (0, 0, 0);

                int enemyHits = 0;
                int allyHits = 0;
                int playerPartyHits = 0;

                var enemySet = new HashSet<BaseUnitEntity>(enemies ?? new List<BaseUnitEntity>());
                var allySet = new HashSet<BaseUnitEntity>(allies ?? new List<BaseUnitEntity>());
                var counted = new HashSet<BaseUnitEntity>();

                foreach (var node in pattern.Nodes)
                {
                    if (!node.TryGetUnit(out var unit) || !(unit is BaseUnitEntity baseUnit))
                        continue;

                    if (counted.Contains(baseUnit)) continue;
                    counted.Add(baseUnit);

                    if (enemySet.Contains(baseUnit))
                    {
                        enemyHits++;
                    }
                    else if (baseUnit != caster && allySet.Contains(baseUnit))
                    {
                        allyHits++;
                        if (baseUnit.IsInPlayerParty)
                            playerPartyHits++;
                    }
                }

                return (enemyHits, allyHits, playerPartyHits);
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] EvaluateAoEPosition error: {ex.Message}");
                return (0, 0, 0);
            }
        }

        /// <summary>
        /// ★ v3.6.9: AOE 높이 차이 체크 - 게임 로직 참조
        /// Circle 패턴: 1.6m 이상 차이 시 효과 없음
        /// ★ v3.7.15: Directional 패턴도 1.6m로 통일
        /// 이유: 게임은 기울기(slope)를 계산하여 더 복잡한 검증을 함
        ///       우리 AI가 0.3m로 너무 엄격하게 필터링하면 공격 기회 상실
        ///       게임이 최종 검증을 하므로 사전 필터링은 관대하게
        /// </summary>
        public const float AoELevelDiffCircle = 1.6f;      // AoEPattern.SameLevelDiff
        public const float AoELevelDiffDirectional = 1.6f; // ★ v3.7.15: 0.3f → 1.6f (게임이 기울기 계산으로 검증)

        /// <summary>
        /// ★ v3.6.9: AOE 높이 차이로 인해 적에게 효과가 닿을 수 있는지 확인
        /// </summary>
        /// <param name="ability">AOE 능력</param>
        /// <param name="casterPosition">시전자 위치</param>
        /// <param name="targetPosition">타겟 위치</param>
        /// <returns>높이 차이가 허용 범위 내면 true</returns>
        public static bool IsAoEHeightInRange(AbilityData ability, Vector3 casterPosition, Vector3 targetPosition)
        {
            try
            {
                if (ability == null) return true;  // 안전 폴백

                // 패턴 타입 확인
                var patternType = GetPatternType(ability);

                // ★ v3.6.9 fix: 패턴 타입이 없으면 AOE 여부 확인 후 Circle로 처리
                // ★ v3.8.09: GetActualIsDirectional() 사용으로 정확한 판정
                bool isDirectional = false;
                if (patternType.HasValue)
                {
                    isDirectional = GetActualIsDirectional(ability);  // ★ v3.8.09: 게임 실제 로직
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] AOE height: {ability.Name} PatternType={patternType.Value}, IsDirectional={isDirectional}");
                }
                else
                {
                    // 패턴 타입이 없으면 AOE 반경으로 Circle 여부 판단
                    float aoERadius = GetAoERadius(ability);
                    if (aoERadius > 0)
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] AOE height: {ability.Name} PatternType=null but AOE r={aoERadius}, treating as Circle");
                    }
                    // isDirectional = false → Circle 임계값(1.6m) 사용
                }

                // 높이 차이 계산 (절대값)
                float heightDiff = Mathf.Abs(casterPosition.y - targetPosition.y);

                // 패턴 타입에 따른 임계값 선택
                float threshold = isDirectional ? AoELevelDiffDirectional : AoELevelDiffCircle;

                bool inRange = heightDiff <= threshold;

                if (!inRange)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] AOE height check failed: {ability.Name} " +
                        $"heightDiff={heightDiff:F2}m > threshold={threshold:F2}m ({(isDirectional ? "Directional" : "Circle")})");
                }

                return inRange;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsAoEHeightInRange error: {ex.Message}");
                return true;  // 에러 시 안전하게 허용
            }
        }

        /// <summary>
        /// ★ v3.6.9: AOE 높이 차이로 인해 적에게 효과가 닿을 수 있는지 확인 (유닛 버전)
        /// </summary>
        public static bool IsAoEHeightInRange(AbilityData ability, BaseUnitEntity caster, BaseUnitEntity target)
        {
            if (caster == null || target == null) return true;
            return IsAoEHeightInRange(ability, caster.Position, target.Position);
        }

        /// <summary>
        /// ★ v3.6.10: AOE 범위 내에 유닛이 있는지 확인 (2D 거리 + 높이 체크 통합)
        /// AoESafetyChecker, ClusterDetector에서 사용
        /// </summary>
        /// <param name="ability">AOE 능력 (null이면 Circle로 처리)</param>
        /// <param name="center">AOE 중심 (시전자 또는 타겟 위치)</param>
        /// <param name="unit">체크할 유닛</param>
        /// <param name="aoERadius">AOE 반경 (타일 단위)</param>
        /// <returns>유닛이 AOE 효과 범위 내에 있으면 true</returns>
        public static bool IsUnitInAoERange(AbilityData ability, Vector3 center, BaseUnitEntity unit, float aoERadius)
        {
            if (unit == null) return false;

            // ★ v3.8.66: 대형 유닛은 가장 가까운 경계 셀 기준 (SizeRect 반영)
            float dist2D = (float)WarhammerGeometryUtils.DistanceToInCells(
                center, new IntRect(0, 0, 0, 0),  // AoE 중심은 점
                unit.Position, unit.SizeRect);
            if (dist2D > aoERadius) return false;

            // 2. 높이 차이 체크
            float heightDiff = Mathf.Abs(center.y - unit.Position.y);

            // ★ v3.8.09: 패턴 타입에 따른 높이 임계값 - GetActualIsDirectional 사용
            bool isDirectional = false;
            if (ability != null)
            {
                isDirectional = GetActualIsDirectional(ability);  // ★ v3.8.09: 게임 실제 로직
            }

            float heightThreshold = isDirectional ? AoELevelDiffDirectional : AoELevelDiffCircle;
            return heightDiff <= heightThreshold;
        }

        /// <summary>
        /// ★ v3.6.10: 방향성 AOE(Cone/Ray/Sector) 범위 내에 유닛이 있는지 확인
        /// ★ v3.8.09: Custom/Circle 패턴 지원 추가
        /// </summary>
        public static bool IsUnitInDirectionalAoERange(
            Vector3 casterPosition,
            Vector3 direction,
            BaseUnitEntity unit,
            float radius,  // 타일
            float angle,
            Kingmaker.Blueprints.PatternType patternType)
        {
            if (unit == null) return false;

            Vector3 toUnit = unit.Position - casterPosition;

            // 1. 2D 거리 체크
            float dist2D = MetersToTiles(new Vector3(toUnit.x, 0, toUnit.z).magnitude);
            if (dist2D > radius) return false;
            if (dist2D < 0.5f) return false;  // 캐스터 위치 제외

            // 2. 높이 차이 체크 (Directional은 0.3m)
            float heightDiff = Mathf.Abs(toUnit.y);
            if (heightDiff > AoELevelDiffDirectional) return false;

            // 3. 각도 체크
            Vector3 toUnit2D = new Vector3(toUnit.x, 0, toUnit.z);
            Vector3 direction2D = new Vector3(direction.x, 0, direction.z);
            float unitAngle = Vector3.Angle(direction2D, toUnit2D);

            switch (patternType)
            {
                case Kingmaker.Blueprints.PatternType.Ray:
                    // ★ v3.8.65: 게임 검증 — Ray = Bresenham 1-cell 직선 (AoEPattern.Angle=0)
                    // 각도가 아닌 수직 거리 1타일 이내로 판정
                    {
                        Vector3 dirNorm2D = direction2D.normalized;
                        float perpMeters = Vector3.Cross(dirNorm2D, toUnit2D).magnitude;
                        float perpTiles = MetersToTiles(perpMeters);
                        return perpTiles <= 1f;
                    }

                case Kingmaker.Blueprints.PatternType.Cone:
                case Kingmaker.Blueprints.PatternType.Sector:
                    return unitAngle <= angle / 2f;

                case Kingmaker.Blueprints.PatternType.Custom:
                    // ★ v3.8.09: Custom 패턴 - 각도가 설정되어 있으면 사용
                    // 360도면 전방향 (거리만 체크)
                    if (angle >= 360f) return true;
                    return unitAngle <= angle / 2f;

                case Kingmaker.Blueprints.PatternType.Circle:
                    // ★ v3.8.09: Circle은 거리만 체크 (방향 무관)
                    return true;

                default:
                    return false;
            }
        }

        #endregion

        #region Ability Type Detection API (v3.5.73)

        /// <summary>
        /// ★ v3.5.73: 능력의 공격 카테고리 정보
        /// 게임 네이티브 API만 사용 - 문자열 휴리스틱 금지
        /// </summary>
        public class AbilityTypeInfo
        {
            // 공격 방식 - 게임 네이티브 API 기반
            public bool IsBurst { get; set; }        // 점사/연사
            public bool IsScatter { get; set; }      // 산탄
            public bool IsSingleShot { get; set; }   // 단발
            public bool IsAoE { get; set; }          // 범위 공격
            public bool IsCharge { get; set; }       // 돌격
            public bool IsMelee { get; set; }        // 근접
            public bool IsRanged { get; set; }       // 원거리

            // 패턴 정보 (v3.5.39 API 재사용)
            public bool IsPattern { get; set; }
            public Kingmaker.Blueprints.PatternType? PatternType { get; set; }
            public float PatternRadius { get; set; }
            public float PatternAngle { get; set; }

            // 무기 연관
            public bool IsWeaponAbility { get; set; }

            // 계산된 분류
            public Data.AttackCategory Category => CalculateCategory();

            private Data.AttackCategory CalculateCategory()
            {
                if (IsCharge) return Data.AttackCategory.GapCloser;
                if (IsAoE || IsPattern) return Data.AttackCategory.AoE;
                if (IsScatter) return Data.AttackCategory.Scatter;
                if (IsBurst) return Data.AttackCategory.Burst;
                if (IsSingleShot) return Data.AttackCategory.SingleTarget;
                return Data.AttackCategory.Normal;
            }
        }

        // AbilityTypeInfo 캐시 (GUID별)
        private static Dictionary<string, AbilityTypeInfo> AbilityTypeCache = new Dictionary<string, AbilityTypeInfo>();

        /// <summary>
        /// ★ v3.5.73: 능력 타입 정보 조회 (게임 API 기반)
        /// 문자열 휴리스틱 없이 게임 네이티브 속성만 사용
        /// </summary>
        public static AbilityTypeInfo GetAbilityTypeInfo(AbilityData ability)
        {
            if (ability == null) return new AbilityTypeInfo();

            try
            {
                // 캐시 확인
                var guid = ability.Blueprint?.AssetGuid?.ToString();
                if (!string.IsNullOrEmpty(guid) && AbilityTypeCache.TryGetValue(guid, out var cached))
                    return cached;

                var info = new AbilityTypeInfo
                {
                    // 공격 방식 - 게임 네이티브 API 직접 호출
                    IsBurst = ability.IsBurstAttack,
                    IsScatter = ability.IsScatter,
                    IsSingleShot = ability.IsSingleShot,
                    IsAoE = ability.IsAOE,
                    IsCharge = ability.IsCharge,
                    IsMelee = ability.IsMelee,
                    IsRanged = !ability.IsMelee,
                    IsWeaponAbility = ability.Weapon != null,
                };

                // 패턴 정보 (v3.5.39 API 재사용)
                var patternInfo = GetPatternInfo(ability);
                if (patternInfo != null && patternInfo.IsValid)
                {
                    info.IsPattern = true;
                    info.PatternType = patternInfo.Type;
                    info.PatternRadius = patternInfo.Radius;
                    info.PatternAngle = patternInfo.Angle;
                }

                // 캐시 저장
                if (!string.IsNullOrEmpty(guid))
                    AbilityTypeCache[guid] = info;

                return info;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAbilityTypeInfo error: {ex.Message}");
                return new AbilityTypeInfo();
            }
        }

        /// <summary>
        /// ★ v3.5.73: Burst 공격 여부 (게임 API 직접 호출)
        /// </summary>
        public static bool IsBurstAttack(AbilityData ability)
            => ability?.IsBurstAttack ?? false;

        /// <summary>
        /// ★ v3.5.73: Scatter 공격 여부 (게임 API 직접 호출)
        /// </summary>
        public static bool IsScatterAttack(AbilityData ability)
            => ability?.IsScatter ?? false;

        /// <summary>
        /// ★ v3.5.73: Charge (돌격) 능력 여부 (게임 API 직접 호출)
        /// </summary>
        public static bool IsChargeAbility(AbilityData ability)
            => ability?.IsCharge ?? false;

        /// <summary>
        /// ★ v3.5.73: 공격 카테고리 조회 (게임 API 기반 자동 분류)
        /// </summary>
        public static Data.AttackCategory GetAttackCategory(AbilityData ability)
            => GetAbilityTypeInfo(ability).Category;

        /// <summary>
        /// ★ v3.5.73: 능력 타입 캐시 클리어 (전투 종료 시 호출)
        /// </summary>
        public static void ClearAbilityTypeCache()
        {
            AbilityTypeCache.Clear();
            Main.LogDebug("[CombatAPI] AbilityType cache cleared");
        }

        #endregion

        #region Ability Classification Data (v3.7.73)

        // ★ v3.7.73: AbilityClassificationData 캐시 (GUID별)
        private static Dictionary<string, AbilityClassificationData> ClassificationCache = new Dictionary<string, AbilityClassificationData>();

        /// <summary>
        /// ★ v3.7.73: 능력의 모든 분류 속성을 추출 (게임 API 기반)
        /// 캐싱되어 동일 GUID는 한 번만 계산
        /// </summary>
        public static AbilityClassificationData GetClassificationData(AbilityData ability)
        {
            if (ability == null) return new AbilityClassificationData();

            try
            {
                // 캐시 확인
                var guid = ability.Blueprint?.AssetGuid?.ToString();
                if (!string.IsNullOrEmpty(guid) && ClassificationCache.TryGetValue(guid, out var cached))
                    return cached;

                var bp = ability.Blueprint;
                if (bp == null) return new AbilityClassificationData();

                var data = new AbilityClassificationData
                {
                    // ═══════════════════════════════════════════════════════════════
                    // 기본 타입 (Blueprint에서 추출)
                    // ═══════════════════════════════════════════════════════════════
                    Tag = bp.AbilityTag,
                    ParamsSource = bp.AbilityParamsSource,
                    SpellDescriptor = bp.SpellDescriptor,

                    // ═══════════════════════════════════════════════════════════════
                    // 공격 특성 (AbilityData 런타임 + Blueprint 혼합)
                    // ═══════════════════════════════════════════════════════════════
                    AttackType = bp.AttackType,
                    IsMelee = ability.IsMelee,
                    IsRanged = !ability.IsMelee,
                    IsScatter = ability.IsScatter,
                    IsBurst = ability.IsBurstAttack,
                    IsSingleShot = ability.IsSingleShot,
                    IsAoE = ability.IsAOE,
                    IsCharge = ability.IsCharge,
                    IsMoveUnit = bp.IsMoveUnit,
                    BurstCount = ability.BurstAttacksCount,
                    AoERadius = bp.AoERadius,

                    // ═══════════════════════════════════════════════════════════════
                    // 타겟팅 (Blueprint에서 추출)
                    // ═══════════════════════════════════════════════════════════════
                    Range = bp.Range,
                    RangeCells = (int)GetAbilityRange(ability),
                    MinRangeCells = bp.MinRange,
                    CustomRange = bp.CustomRange,
                    CanTargetEnemies = bp.CanTargetEnemies,
                    CanTargetFriends = bp.CanTargetFriends,
                    CanTargetSelf = bp.CanTargetSelf,
                    CanTargetPoint = bp.CanTargetPoint,
                    CanTargetDead = bp.CanCastToDeadTarget,
                    AoETargets = bp.AoETargets,
                    NeedLoS = true, // 대부분의 능력은 LOS 필요

                    // ═══════════════════════════════════════════════════════════════
                    // 효과 (Blueprint에서 추출)
                    // ═══════════════════════════════════════════════════════════════
                    EffectOnAlly = bp.EffectOnAlly,
                    EffectOnEnemy = bp.EffectOnEnemy,
                    NotOffensive = bp.NotOffensive,
                    IsWeaponAbility = bp.IsWeaponAbility,
                    IsPsykerAbility = bp.IsPsykerAbility,

                    // ═══════════════════════════════════════════════════════════════
                    // 특수 (Blueprint에서 추출)
                    // ═══════════════════════════════════════════════════════════════
                    IsHeroicAct = bp.IsHeroicAct,
                    IsDesperateMeasure = bp.IsDesperateMeasure,
                    IsMomentum = bp.IsMomentum,

                    // ═══════════════════════════════════════════════════════════════
                    // 비용 (Blueprint + Runtime 혼합)
                    // ═══════════════════════════════════════════════════════════════
                    APCost = bp.ActionPointCost,
                    IsFreeAction = ability.IsFreeAction,

                    // ═══════════════════════════════════════════════════════════════
                    // ★ v3.7.89: AOO 관련 (Blueprint에서 추출)
                    // ═══════════════════════════════════════════════════════════════
                    UsingInThreateningArea = (int)bp.UsingInThreateningArea,
                };

                // 패턴 타입 추출
                var patternInfo = GetPatternInfo(ability);
                if (patternInfo != null && patternInfo.IsValid)
                {
                    data.PatternType = patternInfo.Type.ToString();
                }

                // 캐시 저장
                if (!string.IsNullOrEmpty(guid))
                    ClassificationCache[guid] = data;

                return data;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetClassificationData error: {ex.Message}");
                return new AbilityClassificationData();
            }
        }

        /// <summary>
        /// ★ v3.7.73: 분류 데이터 캐시 클리어 (전투 종료 시 호출)
        /// </summary>
        public static void ClearClassificationCache()
        {
            ClassificationCache.Clear();
            Main.LogDebug("[CombatAPI] Classification cache cleared");
        }

        /// <summary>
        /// ★ v3.7.73: 모든 능력 관련 캐시 클리어
        /// </summary>
        public static void ClearAllAbilityCaches()
        {
            ClearAbilityTypeCache();
            ClearClassificationCache();
            Main.LogDebug("[CombatAPI] All ability caches cleared");
        }

        #endregion

        #region Targeting Detection (v3.1.25)

        /// <summary>
        /// ★ v3.1.25: 적이 특정 유닛을 타겟팅 중인지 확인
        /// </summary>
        public static bool IsTargeting(BaseUnitEntity enemy, BaseUnitEntity target)
        {
            if (enemy?.CombatState == null || target == null) return false;
            try
            {
                return enemy.CombatState.LastTarget == target;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsTargeting failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.1.25: 특정 아군을 타겟팅 중인 적 목록 조회
        /// </summary>
        public static List<BaseUnitEntity> GetEnemiesTargeting(
            BaseUnitEntity ally,
            List<BaseUnitEntity> enemies)
        {
            var targeting = new List<BaseUnitEntity>();
            if (ally == null || enemies == null) return targeting;

            foreach (var enemy in enemies)
            {
                if (enemy?.CombatState?.LastTarget == ally)
                    targeting.Add(enemy);
            }
            return targeting;
        }

        /// <summary>
        /// ★ v3.1.25: 아군(특정 유닛 제외)을 타겟팅 중인 모든 적 조회
        /// 탱커가 호출할 때: excludeUnit = 탱커 자신 (탱커 타겟팅 적은 이미 어그로 잡힌 상태)
        /// </summary>
        public static List<BaseUnitEntity> GetEnemiesTargetingAllies(
            BaseUnitEntity excludeUnit,
            List<BaseUnitEntity> allies,
            List<BaseUnitEntity> enemies)
        {
            var targeting = new List<BaseUnitEntity>();
            if (allies == null || enemies == null) return targeting;

            foreach (var enemy in enemies)
            {
                if (enemy?.CombatState == null) continue;
                var lastTarget = enemy.CombatState.LastTarget as BaseUnitEntity;
                if (lastTarget != null && lastTarget != excludeUnit && allies.Contains(lastTarget))
                {
                    targeting.Add(enemy);
                }
            }
            return targeting;
        }

        /// <summary>
        /// ★ v3.1.25: 위협받는 아군 수 (탱커 제외)
        /// </summary>
        public static int CountAlliesUnderThreat(
            BaseUnitEntity excludeUnit,
            List<BaseUnitEntity> allies,
            List<BaseUnitEntity> enemies)
        {
            if (allies == null || enemies == null) return 0;

            var threatenedAllies = new HashSet<BaseUnitEntity>();
            foreach (var enemy in enemies)
            {
                if (enemy?.CombatState == null) continue;
                var lastTarget = enemy.CombatState.LastTarget as BaseUnitEntity;
                if (lastTarget != null && lastTarget != excludeUnit && allies.Contains(lastTarget))
                {
                    threatenedAllies.Add(lastTarget);
                }
            }
            return threatenedAllies.Count;
        }

        #endregion

        #region Damaging AoE Detection (v3.9.70)

        // ★ v3.9.70: 블루프린트 기반 피해 AoE 판별 캐시 (정적 데이터이므로 전투 내 재사용)
        private static readonly Dictionary<string, bool> _damagingAoECache = new Dictionary<string, bool>();

        /// <summary>
        /// ★ v3.9.70: 유닛이 현재 피해를 주는 AoE 구역 안에 있는지 확인
        /// 1차: AiBrainHelper.IsThreatningArea() (적 AoE에 대해 정확)
        /// 2차 폴백: 블루프린트 컴포넌트 직접 검사 (환경 AoE — caster null로 IsSuitableTargetType 실패 우회)
        /// </summary>
        public static bool IsUnitInDamagingAoE(BaseUnitEntity unit)
        {
            if (unit == null) return false;

            try
            {
                foreach (var areaEffect in Game.Instance.State.AreaEffects)
                {
                    if (areaEffect == null) continue;

                    // 1차: 게임 API — 적 AoE에 대해 팩션 체크 포함
                    if (AiBrainHelper.IsThreatningArea(areaEffect, unit))
                    {
                        if (areaEffect.Contains(unit))
                            return true;
                        continue;
                    }

                    // 2차 폴백: 환경/중립 AoE — IsSuitableTargetType이 caster null로 실패하는 경우
                    // 아군이 시전한 AoE는 건너뛰기 (아군 AoE에서 도망칠 필요 없음)
                    var caster = areaEffect.Context?.MaybeCaster;
                    if (caster != null && !caster.IsEnemy(unit))
                        continue;

                    // 블루프린트에 피해 컴포넌트가 있고, 유닛이 안에 있는가?
                    if (HasDamagingComponents(areaEffect) && areaEffect.Contains(unit))
                    {
                        if (Main.IsDebugEnabled)
                            Main.LogDebug($"[CombatAPI] ★ Damaging AoE detected via fallback: {areaEffect.Blueprint?.name ?? "unknown"} (caster={(caster != null ? "enemy" : "null/environmental")})");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsUnitInDamagingAoE error: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// ★ v3.9.70: 특정 위치가 피해를 주는 AoE 구역 안에 있는지 확인
        /// 이동 후보 타일 평가에 사용 — unit은 팩션 체크용
        /// </summary>
        public static bool IsPositionInDamagingAoE(Vector3 position, BaseUnitEntity unit)
        {
            if (unit == null) return false;

            try
            {
                foreach (var areaEffect in Game.Instance.State.AreaEffects)
                {
                    if (areaEffect == null) continue;

                    // 1차: 게임 API
                    if (AiBrainHelper.IsThreatningArea(areaEffect, unit))
                    {
                        if (areaEffect.Contains(position))
                            return true;
                        continue;
                    }

                    // 2차 폴백: 아군 시전 AoE 건너뛰기
                    var caster = areaEffect.Context?.MaybeCaster;
                    if (caster != null && !caster.IsEnemy(unit))
                        continue;

                    if (HasDamagingComponents(areaEffect) && areaEffect.Contains(position))
                        return true;
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsPositionInDamagingAoE error: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// AoE 블루프린트에 피해를 주는 컴포넌트가 있는지 직접 확인
        /// 게임의 CheckDealDamage + CheckApplyBuffWithDamage 로직 재현
        /// IsSuitableTargetType 팩션 체크를 우회하여 환경 AoE도 감지
        /// </summary>
        private static bool HasDamagingComponents(AreaEffectEntity areaEffect)
        {
            var blueprint = areaEffect?.Blueprint;
            if (blueprint == null) return false;

            // 캐시 확인 (블루프린트 컴포넌트는 정적 데이터)
            string bpId = blueprint.AssetGuid?.ToString();
            if (bpId != null && _damagingAoECache.TryGetValue(bpId, out bool cached))
                return cached;

            bool isDamaging = false;

            foreach (var component in blueprint.ComponentsArray)
            {
                if (component == null) continue;

                // Check 1: AbilityAreaEffectRunAction — UnitEnter/UnitMove/Round에 ContextActionDealDamage가 있는지
                if (component is AbilityAreaEffectRunAction runAction)
                {
                    if (ContainsDamageAction(runAction.UnitEnter) ||
                        ContainsDamageAction(runAction.UnitExit) ||
                        ContainsDamageAction(runAction.UnitMove) ||
                        ContainsDamageAction(runAction.Round))
                    {
                        isDamaging = true;
                        break;
                    }
                }

                // Check 2: AbilityAreaEffectBuff — 버프에 AddFactContextActions.Activated/NewRound에 피해가 있는지
                if (component is AbilityAreaEffectBuff buffComponent)
                {
                    var buff = buffComponent.Buff;
                    if (buff != null)
                    {
                        foreach (var buffComp in buff.ComponentsArray)
                        {
                            if (buffComp is AddFactContextActions contextActions)
                            {
                                if (ContainsDamageAction(contextActions.Activated) ||
                                    ContainsDamageAction(contextActions.NewRound) ||
                                    ContainsDamageAction(contextActions.RoundEnd))
                                {
                                    isDamaging = true;
                                    break;
                                }
                            }
                        }
                        if (isDamaging) break;
                    }
                }
            }

            // 캐시 저장
            if (bpId != null)
                _damagingAoECache[bpId] = isDamaging;

            return isDamaging;
        }

        /// <summary>
        /// ActionList 내에 ContextActionDealDamage가 포함되어 있는지 확인
        /// </summary>
        private static bool ContainsDamageAction(ActionList actionList)
        {
            if (actionList?.Actions == null) return false;
            foreach (var action in actionList.Actions)
            {
                if (action is ContextActionDealDamage)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 전투 시작 시 AoE 캐시 초기화 (CombatCache.ClearAll에서 호출)
        /// </summary>
        public static void ClearDamagingAoECache()
        {
            _damagingAoECache.Clear();
            _lastHazardCheckUnit = null;
        }

        // ── ★ v3.19.8: Unified Hazard Zone Detection ──
        // DamagingAoE + PsychicNullZone(사이커 전용)을 단일 메서드로 통합
        // 모든 이동 계획에서 일관된 위험 회피 보장

        private static BaseUnitEntity _lastHazardCheckUnit;
        private static bool _lastHazardCheckIsPsychic;

        /// <summary>
        /// ★ v3.19.8: 특정 위치가 위험 구역(DamagingAoE + PsychicNullZone) 안인지 통합 판별
        /// 모든 이동 후보 타일 평가에서 IsPositionInDamagingAoE 대신 사용
        /// 사이커 여부는 유닛별 캐시 — 타일 루프에서 반복 호출 시 O(1)
        /// </summary>
        public static bool IsPositionInHazardZone(Vector3 position, BaseUnitEntity unit)
        {
            if (IsPositionInDamagingAoE(position, unit)) return true;

            // 사이커 여부 캐시 (같은 유닛이면 재계산 안 함)
            if (unit != _lastHazardCheckUnit)
            {
                _lastHazardCheckUnit = unit;
                _lastHazardCheckIsPsychic = HasPsychicAbilities(unit);
            }
            if (_lastHazardCheckIsPsychic && IsPositionInPsychicNullZone(position)) return true;

            return false;
        }

        /// <summary>
        /// ★ v3.19.8: 유닛이 현재 위험 구역 안에 있는지 통합 판별
        /// </summary>
        public static bool IsUnitInHazardZone(BaseUnitEntity unit)
        {
            if (IsUnitInDamagingAoE(unit)) return true;
            if (HasPsychicAbilities(unit) && IsUnitInPsychicNullZone(unit)) return true;
            return false;
        }

        /// <summary>
        /// ★ v3.9.70: 유닛이 사이킥 사용 불가 구역(Inert Warp Effect)에 있는지 확인
        /// 워프 데미지 존은 AreaEffectRestrictions.CannotUsePsychicPowers 플래그를 가짐
        /// </summary>
        public static bool IsUnitInPsychicNullZone(BaseUnitEntity unit)
        {
            if (unit == null) return false;
            try
            {
                var node = (CustomGridNodeBase)(Pathfinding.GraphNode)unit.CurrentNode;
                if (node == null) return false;
                return AreaEffectsController.CheckInertWarpEffect(node);
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsUnitInPsychicNullZone error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.9.70: 특정 위치가 사이킥 사용 불가 구역에 있는지 확인
        /// 이동 후보 타일 평가에 사용
        /// </summary>
        public static bool IsPositionInPsychicNullZone(Vector3 position)
        {
            try
            {
                foreach (var areaEffect in Game.Instance.State.AreaEffects)
                {
                    if (areaEffect == null) continue;
                    if (areaEffect.Blueprint.HasInertWarpEffect && areaEffect.Contains(position))
                        return true;
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsPositionInPsychicNullZone error: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// ★ v3.9.70: 유닛이 사이킥 능력을 보유하고 있는지 확인
        /// </summary>
        public static bool HasPsychicAbilities(BaseUnitEntity unit)
        {
            if (unit == null) return false;
            try
            {
                var abilities = unit.Abilities;
                if (abilities == null) return false;
                foreach (var ability in abilities.RawFacts)
                {
                    if (ability?.Blueprint?.IsPsykerAbility == true)
                        return true;
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] HasPsychicAbilities failed for {unit?.CharacterName}: {ex.Message}");
            }
            return false;
        }

        #endregion

        #region Unit Conversion - 타일 기준 (v3.5.98)

        /// <summary>
        /// ★ v3.5.98: 게임 그리드 셀 크기 (1 타일 = 1.35 미터)
        /// GraphParamsMechanicsCache.GridCellSize 참조
        /// </summary>
        public const float GridCellSize = 1.35f;

        /// <summary>미터 → 타일 변환</summary>
        public static float MetersToTiles(float meters) => meters / GridCellSize;

        /// <summary>타일 → 미터 변환 (필요시에만 사용)</summary>
        public static float TilesToMeters(float tiles) => tiles * GridCellSize;

        /// <summary>
        /// ★ v3.5.98: 두 유닛 간 거리를 타일 단위로 반환
        /// ★ v3.8.66: 게임 API 사용 — SizeRect 경계 간 최단 셀 거리 (대형 유닛 대응)
        /// 모든 거리 비교에 이 함수 사용
        /// </summary>
        public static float GetDistanceInTiles(BaseUnitEntity a, BaseUnitEntity b)
        {
            if (a == null || b == null) return float.MaxValue;
            try
            {
                // ★ v3.8.66: 게임 API — WarhammerGeometryUtils.DistanceToInCells (Chebyshev 변형)
                // 대형 유닛(2x2+)에서 center-to-center 대비 1~2타일 차이 보정
                return (float)a.DistanceToInCells(b);
            }
            // ★ v3.13.0: 로깅 추가 (기본값 MaxValue는 이미 보수적 — 도달 불가)
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetDistanceInTiles(unit,unit) failed: {ex.Message}");
                return float.MaxValue;
            }
        }

        /// <summary>
        /// ★ v3.5.98: 위치와 유닛 간 거리를 타일 단위로 반환
        /// ★ v3.8.66: 타겟 SizeRect 반영 (대형 유닛 대응)
        /// </summary>
        public static float GetDistanceInTiles(Vector3 position, BaseUnitEntity unit)
        {
            if (unit == null) return float.MaxValue;
            try
            {
                // ★ v3.8.66: 타겟 SizeRect 반영 — 위치는 1x1 점(IntRect(0,0,0,0))
                return (float)WarhammerGeometryUtils.DistanceToInCells(
                    position, new IntRect(0, 0, 0, 0),
                    unit.Position, unit.SizeRect);
            }
            // ★ v3.13.0: 로깅 추가
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetDistanceInTiles(pos,unit) failed: {ex.Message}");
                return float.MaxValue;
            }
        }

        /// <summary>
        /// ★ v3.5.98: 두 위치 간 거리를 타일 단위로 반환
        /// </summary>
        public static float GetDistanceInTiles(Vector3 a, Vector3 b)
        {
            float meters = Vector3.Distance(a, b);
            return meters / GridCellSize;
        }

        /// <summary>
        /// ★ v3.5.98: 능력 사거리를 타일 단위로 반환 (게임 API 사용)
        /// 기존 GetAbilityRange() 대체
        /// </summary>
        public static int GetAbilityRangeInTiles(AbilityData ability)
        {
            if (ability == null) return 0;
            try
            {
                return ability.RangeCells;  // 게임 공식 API - 타일 단위
            }
            catch
            {
                return 15;  // 폴백: 15타일
            }
        }

        /// <summary>
        /// ★ v3.7.46: MultiTarget 능력의 Point1 타겟팅 범위 반환
        ///
        /// MultiTarget 능력(예: Aerial Rush)은 각 Point마다 다른 능력 블루프린트를 사용함
        /// Point1 = TryGetNextTargetAbilityAndCaster(targetIndex=0)의 능력 범위
        /// </summary>
        public static int GetMultiTargetPoint1RangeInTiles(AbilityData rootAbility)
        {
            if (rootAbility == null) return 30;  // 폴백

            try
            {
                // IAbilityMultiTarget 컴포넌트 가져오기
                var multiTarget = rootAbility.Blueprint?.GetComponent<Kingmaker.UnitLogic.Abilities.Components.Base.IAbilityMultiTarget>();
                if (multiTarget == null)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetMultiTargetPoint1Range: No IAbilityMultiTarget component");
                    return rootAbility.RangeCells;  // MultiTarget이 아니면 기본 범위 반환
                }

                // Point1 (targetIndex=0)에 사용되는 능력 가져오기
                Kingmaker.UnitLogic.Abilities.Blueprints.BlueprintAbility point1Blueprint;
                Kingmaker.EntitySystem.Entities.MechanicEntity point1Caster;

                if (!multiTarget.TryGetNextTargetAbilityAndCaster(rootAbility, 0, out point1Blueprint, out point1Caster))
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetMultiTargetPoint1Range: TryGetNextTarget failed for index 0");
                    return 30;  // 폴백
                }

                if (point1Blueprint == null)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetMultiTargetPoint1Range: Point1 blueprint is null");
                    return 30;  // 폴백
                }

                // Point1 능력의 AbilityData 생성하여 RangeCells 가져오기
                var point1Ability = new AbilityData(point1Blueprint, point1Caster ?? rootAbility.Caster);
                int point1Range = point1Ability.RangeCells;

                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetMultiTargetPoint1Range: Point1 ability={point1Blueprint.name}, Range={point1Range} tiles");
                return point1Range;
            }
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetMultiTargetPoint1Range error: {ex.Message}");
                return 30;  // 폴백
            }
        }

        /// <summary>
        /// ★ v3.7.54: MultiTarget 능력의 Point2 타겟팅 범위 반환
        ///
        /// Aerial Rush Point2가 게임에서 거부되는 원인:
        /// - AI는 Eagle MP를 Point2 범위로 사용
        /// - 게임은 Support_Ascended_Ability.RangeCells로 검증
        /// - 이 두 값이 다르면 TargetRestrictionNotPassed 발생
        ///
        /// 해결: 게임이 실제로 사용하는 Point2 능력의 RangeCells를 반환
        /// </summary>
        public static int GetMultiTargetPoint2RangeInTiles(AbilityData rootAbility)
        {
            if (rootAbility == null) return 15;  // 폴백

            try
            {
                // IAbilityMultiTarget 컴포넌트 가져오기
                var multiTarget = rootAbility.Blueprint?.GetComponent<Kingmaker.UnitLogic.Abilities.Components.Base.IAbilityMultiTarget>();
                if (multiTarget == null)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetMultiTargetPoint2Range: No IAbilityMultiTarget component");
                    return 15;  // MultiTarget이 아니면 폴백
                }

                // Point2 (targetIndex=1)에 사용되는 능력 가져오기
                Kingmaker.UnitLogic.Abilities.Blueprints.BlueprintAbility point2Blueprint;
                Kingmaker.EntitySystem.Entities.MechanicEntity point2Caster;

                if (!multiTarget.TryGetNextTargetAbilityAndCaster(rootAbility, 1, out point2Blueprint, out point2Caster))
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetMultiTargetPoint2Range: TryGetNextTarget failed for index 1");
                    return 15;  // 폴백
                }

                if (point2Blueprint == null)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetMultiTargetPoint2Range: Point2 blueprint is null");
                    return 15;  // 폴백
                }

                // Point2 능력의 AbilityData 생성하여 RangeCells 가져오기
                // ★ caster는 Pet(Eagle) - AbilityMultiTarget.GetDelegateUnit() 참조
                var point2Ability = new AbilityData(point2Blueprint, point2Caster ?? rootAbility.Caster);
                int point2Range = point2Ability.RangeCells;

                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetMultiTargetPoint2Range: Point2 ability={point2Blueprint.name}, " +
                    $"Caster={(point2Caster as BaseUnitEntity)?.CharacterName ?? "unknown"}, Range={point2Range} tiles");
                return point2Range;
            }
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetMultiTargetPoint2Range error: {ex.Message}");
                return 15;  // 폴백
            }
        }

        #endregion

        #region Hit Chance API (v3.6.7)

        /// <summary>
        /// ★ v3.6.7: 명중률 정보 구조체
        /// </summary>
        public class HitChanceInfo
        {
            /// <summary>★ v3.26.0: 실질 명중률 (BS × (1-Dodge) × (1-Parry), 1-95%)</summary>
            public int HitChance { get; set; }

            /// <summary>★ v3.26.0: BS 기반 원본 명중률 (dodge/parry 미반영)</summary>
            public int RawBSHitChance { get; set; }

            /// <summary>★ v3.26.0: 추정 회피율 (0-95%)</summary>
            public int EstimatedDodgeChance { get; set; }

            /// <summary>★ v3.26.0: 추정 패리율 (0-95%, 근접만)</summary>
            public int EstimatedParryChance { get; set; }

            /// <summary>거리 계수 (1.0=최적, 0.5=절반 이상, 0.0=사거리 초과)</summary>
            public float DistanceFactor { get; set; }

            /// <summary>엄폐 타입</summary>
            public LosCalculations.CoverType CoverType { get; set; }

            /// <summary>최적 거리 내에 있는지 (DistanceFactor >= 1.0)</summary>
            public bool IsInOptimalRange => DistanceFactor >= 1.0f;

            /// <summary>최대 사거리 내에 있는지 (DistanceFactor > 0)</summary>
            public bool IsInRange => DistanceFactor > 0f;

            /// <summary>명중률이 낮은지 (50% 미만)</summary>
            public bool IsLowHitChance => HitChance < 50;

            /// <summary>명중률이 매우 낮은지 (30% 미만)</summary>
            public bool IsVeryLowHitChance => HitChance < 30;

            public override string ToString()
            {
                return $"HitChance={HitChance}%(BS={RawBSHitChance}% dodge={EstimatedDodgeChance}% parry={EstimatedParryChance}%), DistFactor={DistanceFactor:F1}, Cover={CoverType}";
            }
        }

        /// <summary>
        /// ★ v3.6.7: 원거리 공격의 명중률 계산
        /// RuleCalculateHitChances 룰 시스템 사용
        /// </summary>
        /// <param name="ability">공격 능력</param>
        /// <param name="attacker">공격자</param>
        /// <param name="target">타겟</param>
        /// <returns>명중률 정보 (null if 계산 실패)</returns>
        public static HitChanceInfo GetHitChance(AbilityData ability, BaseUnitEntity attacker, BaseUnitEntity target)
        {
            if (ability == null || attacker == null || target == null)
                return null;

            try
            {
                int rawHitChance;
                float distanceFactor = 1.0f;
                var coverType = LosCalculations.CoverType.None;

                // ★ v3.6.8: 근접/Scatter 공격은 BS 100% (게임 로직 동일)
                if (ability.IsMelee || ability.IsScatter)
                {
                    rawHitChance = 100;
                }
                else
                {
                    // RuleCalculateHitChances 트리거
                    var hitRule = new RuleCalculateHitChances(
                        attacker, target, ability,
                        0,  // burstIndex (첫 발)
                        attacker.Position, target.Position
                    );
                    Rulebook.Trigger(hitRule);

                    rawHitChance = hitRule.ResultHitChance;
                    distanceFactor = hitRule.DistanceFactor;
                    coverType = hitRule.ResultLos;
                }

                // ★ v3.26.0: Dodge/Parry 추정 → 실질 명중률 계산
                int dodgeChance = EstimateDodgeChance(target, attacker, ability);
                int parryChance = EstimateParryChance(target, attacker, ability);
                int effectiveHitChance = CalculateEffectiveHitChance(rawHitChance, dodgeChance, parryChance);

                var result = new HitChanceInfo
                {
                    HitChance = effectiveHitChance,        // 실질 명중률
                    RawBSHitChance = rawHitChance,         // 원본 보존
                    EstimatedDodgeChance = dodgeChance,
                    EstimatedParryChance = parryChance,
                    DistanceFactor = distanceFactor,
                    CoverType = coverType
                };

                if (Main.IsDebugEnabled)
                    Main.LogDebug($"[CombatAPI] HitChance: {attacker.CharacterName} -> {target.CharacterName}: " +
                        $"BS={rawHitChance}% dodge={dodgeChance}% parry={parryChance}% → effective={effectiveHitChance}%");

                return result;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetHitChance error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ★ v3.6.7: 특정 위치에서 공격 시 명중률 계산 (이동 계획용)
        /// </summary>
        public static HitChanceInfo GetHitChanceFromPosition(
            AbilityData ability,
            BaseUnitEntity attacker,
            Vector3 attackerPosition,
            BaseUnitEntity target)
        {
            if (ability == null || attacker == null || target == null)
                return null;

            try
            {
                int rawHitChance;
                float distanceFactor = 1.0f;
                var coverType = LosCalculations.CoverType.None;

                // ★ v3.6.8: 근접/Scatter 공격은 BS 100%
                if (ability.IsMelee || ability.IsScatter)
                {
                    rawHitChance = 100;
                }
                else
                {
                    var hitRule = new RuleCalculateHitChances(
                        attacker, target, ability,
                        0,
                        attackerPosition,  // 가상 위치에서 계산
                        target.Position
                    );
                    Rulebook.Trigger(hitRule);

                    rawHitChance = hitRule.ResultHitChance;
                    distanceFactor = hitRule.DistanceFactor;
                    coverType = hitRule.ResultLos;
                }

                // ★ v3.26.0: Dodge/Parry 추정 → 실질 명중률
                int dodgeChance = EstimateDodgeChance(target, attacker, ability);
                int parryChance = EstimateParryChance(target, attacker, ability);
                int effectiveHitChance = CalculateEffectiveHitChance(rawHitChance, dodgeChance, parryChance);

                return new HitChanceInfo
                {
                    HitChance = effectiveHitChance,
                    RawBSHitChance = rawHitChance,
                    EstimatedDodgeChance = dodgeChance,
                    EstimatedParryChance = parryChance,
                    DistanceFactor = distanceFactor,
                    CoverType = coverType
                };
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetHitChanceFromPosition error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ★ v3.6.7: 거리 계수만 빠르게 계산 (이동 계획 최적화용)
        /// - 1.0 = 최대 사거리의 절반 이내 (최적)
        /// - 0.5 = 절반 초과 ~ 최대 사거리 (명중률 절반)
        /// - 0.0 = 최대 사거리 초과 (명중 불가)
        /// </summary>
        public static float GetDistanceFactor(AbilityData ability, Vector3 attackerPos, Vector3 targetPos)
        {
            if (ability == null) return 0f;

            try
            {
                // 무기 최대 사거리 (타일 단위)
                int maxRange = ability.RangeCells;
                if (maxRange <= 0 || maxRange >= 1000) return 1.0f;  // Unlimited

                // 실제 거리 (타일 단위)
                float distanceTiles = GetDistanceInTiles(attackerPos, targetPos);

                // 거리 계수 계산 (게임 로직 동일)
                float halfRange = maxRange / 2f;
                if (distanceTiles <= halfRange)
                    return 1.0f;  // 최적 거리
                else if (distanceTiles <= maxRange)
                    return 0.5f;  // 절반 거리
                else
                    return 0.0f;  // 사거리 초과
            }
            catch
            {
                return 1.0f;
            }
        }

        /// <summary>
        /// ★ v3.6.7: 최적 사거리(명중률 100% 적용) 타일 수 반환
        /// </summary>
        public static float GetOptimalRangeInTiles(AbilityData ability)
        {
            if (ability == null) return 0f;

            try
            {
                int maxRange = ability.RangeCells;
                if (maxRange <= 0 || maxRange >= 1000) return 1000f;  // Unlimited
                return maxRange / 2f;  // 최적 = 최대 사거리의 절반
            }
            catch
            {
                return 10f;  // 폴백
            }
        }

        #endregion

        #region Flanking API (v3.28.0)

        // ─── ★ v3.28.0: 플랭킹 (공격 방향) API ─────────────────────────────
        // CustomGraphHelper.GetWarhammerAttackSide()를 래핑하여 AI 포지셔닝에 활용

        /// <summary>공격 방향의 전투 측면 판정 (Front/Left/Right/Back)</summary>
        public static WarhammerCombatSide GetAttackSide(BaseUnitEntity target, Vector3 attackerPosition)
        {
            try
            {
                Vector3 attackDir = (target.Position - attackerPosition).normalized;
                return CustomGraphHelper.GetWarhammerAttackSide(target.Forward, attackDir, target.Size);
            }
            catch
            {
                return WarhammerCombatSide.Front;
            }
        }

        /// <summary>
        /// 플랭킹 보너스 점수 (Back=1.0, Side=0.5, Front=0.0)
        /// 포지셔닝 및 타겟 스코어링에서 후방/측면 공격 보너스 부여용
        /// </summary>
        public static float GetFlankingBonus(BaseUnitEntity target, Vector3 attackerPosition)
        {
            var side = GetAttackSide(target, attackerPosition);
            switch (side)
            {
                case WarhammerCombatSide.Back: return 1.0f;
                case WarhammerCombatSide.Left:
                case WarhammerCombatSide.Right: return 0.5f;
                default: return 0f;
            }
        }

        #endregion
    }
}
