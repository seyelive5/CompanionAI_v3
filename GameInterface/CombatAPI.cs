using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Items;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Mechanics.Actions;
using Kingmaker.UnitLogic.Parts;
using Kingmaker.Utility;
using Kingmaker.View.Covers;
using UnityEngine;
using CompanionAI_v3.Data;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// 게임 API 래퍼 - 모든 게임 상호작용을 중앙화
    /// </summary>
    public static class CombatAPI
    {
        #region Ability Checks

        /// <summary>
        /// 능력을 타겟에게 사용 가능한지 확인
        /// </summary>
        public static bool CanUseAbilityOn(AbilityData ability, TargetWrapper target, out string reason)
        {
            reason = null;

            if (ability == null || target == null)
            {
                reason = "Null ability or target";
                return false;
            }

            try
            {
                // 기본 타겟 검증
                AbilityData.UnavailabilityReasonType? unavailableReason;
                bool canTarget = ability.CanTarget(target, out unavailableReason);

                if (!canTarget && unavailableReason.HasValue)
                {
                    reason = unavailableReason.Value.ToString();
                    return false;
                }

                // 위치 기반 검증 (LOS, 사거리)
                var caster = ability.Caster as BaseUnitEntity;
                var targetEntity = target.Entity as BaseUnitEntity;

                if (caster != null && targetEntity != null)
                {
                    var casterNode = caster.CurrentUnwalkableNode;
                    var targetNode = targetEntity.CurrentUnwalkableNode;

                    if (casterNode != null && targetNode != null)
                    {
                        int distance;
                        LosCalculations.CoverType coverType;

                        bool canTargetFromNode = ability.CanTargetFromNode(
                            casterNode, targetNode, target, out distance, out coverType);

                        if (!canTargetFromNode)
                        {
                            bool hasLos = coverType != LosCalculations.CoverType.Invisible;
                            reason = hasLos ? "OutOfRange" : "NoLineOfSight";
                            return false;
                        }
                    }
                }

                return canTarget;
            }
            catch (Exception ex)
            {
                reason = $"Exception: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// 능력이 사용 가능한지 확인 (간단한 버전)
        /// </summary>
        public static bool IsAbilityAvailable(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                return ability.IsAvailable;
            }
            catch (Exception ex)
            {
                // ★ v3.4.01: P1-2 예외 상세 로깅
                Main.LogDebug($"[CombatAPI] IsAbilityAvailable error for {ability.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.5.15: 능력이 쿨다운 그룹 포함 완전 쿨다운 체크
        /// GetUnavailabilityReasons()는 그룹 쿨다운을 감지하지 못함
        /// PartAbilityCooldowns.IsOnCooldown()을 직접 사용해야 정확함
        /// ★ 주의: IsOnCooldown()은 IsIgnoredByComponent 조건이 있어서 그룹 쿨다운을 놓칠 수 있음
        /// GroupIsOnCooldown()으로 각 그룹을 직접 체크해야 함
        /// ★ v3.5.16: 중복 그룹 체크 추가 (게임 데이터 버그 대응)
        /// </summary>
        public static bool IsAbilityOnCooldownWithGroups(AbilityData ability)
        {
            if (ability == null) return true;

            try
            {
                // ★ 안전한 이름 추출 (로컬라이제이션 에러 방지)
                string abilityName = "Unknown";
                try { abilityName = ability.Blueprint?.name ?? ability.Name ?? "Unknown"; }
                catch { /* 로컬라이제이션 에러 무시 */ }

                var caster = ability.Caster as BaseUnitEntity;
                if (caster == null)
                {
                    Main.LogDebug($"[CombatAPI] CooldownCheck: {abilityName} - caster is null");
                    return false;
                }

                var cooldownPart = caster.AbilityCooldowns;
                if (cooldownPart == null)
                {
                    Main.LogDebug($"[CombatAPI] CooldownCheck: {abilityName} - cooldownPart is null");
                    return false;
                }

                // 1. 능력 자체 쿨다운 체크 (이건 IsIgnoredByComponent를 고려함)
                bool isOnCooldown = cooldownPart.IsOnCooldown(ability);
                if (isOnCooldown)
                {
                    Main.LogDebug($"[CombatAPI] CooldownCheck: {abilityName} - ability on cooldown");
                    return true;
                }

                // 2. 그룹 쿨다운 체크
                var groups = ability.AbilityGroups;
                if (groups != null && groups.Count > 0)
                {
                    // ★ v3.5.16: 중복 그룹 감지 - 게임 데이터 버그로 중복 그룹이 있으면
                    // StartGroupCooldown()에서 에러 발생. 중복 그룹이 있는 능력은 사용 차단.
                    var seenGroups = new HashSet<string>();
                    foreach (var group in groups)
                    {
                        if (group == null) continue;
                        string groupId = group.AssetGuid?.ToString() ?? group.name ?? "unknown";
                        if (seenGroups.Contains(groupId))
                        {
                            Main.Log($"[CombatAPI] ★ {abilityName}: BLOCKED - duplicate group detected (game data bug)");
                            return true; // 중복 그룹이 있으면 사용 차단
                        }
                        seenGroups.Add(groupId);

                        bool groupOnCooldown = cooldownPart.GroupIsOnCooldown(group);
                        if (groupOnCooldown)
                        {
                            Main.LogDebug($"[CombatAPI] CooldownCheck: {abilityName} - Group '{group.name}' on cooldown");
                            return true;
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Main.LogError($"[CombatAPI] IsAbilityOnCooldownWithGroups error: {ex.Message}\n{ex.StackTrace}");
                return false; // 에러 시 일단 허용
            }
        }

        /// <summary>
        /// ★ v3.0.17: 능력이 사용 가능한지 상세 확인 (v2.2에서 포팅)
        /// GetUnavailabilityReasons()로 실제 사용 불가 이유 확인
        /// ★ v3.1.11: 보너스 사용(런 앤 건 등) 처리 추가
        /// </summary>
        public static bool IsAbilityAvailable(AbilityData ability, out List<string> reasons)
        {
            reasons = new List<string>();

            if (ability == null)
            {
                reasons.Add("Null ability");
                return false;
            }

            try
            {
                // ★ 소모품 충전 횟수 체크 (charges=0이면 사용 불가)
                if (ability.SourceItem != null)
                {
                    var usableItem = ability.SourceItem as Kingmaker.Items.ItemEntityUsable;
                    if (usableItem != null && usableItem.Charges <= 0)
                    {
                        reasons.Add("No charges remaining");
                        return false;
                    }
                }

                // ★ 핵심: GetUnavailabilityReasons() 사용 - v2.2와 동일
                var unavailabilityReasons = ability.GetUnavailabilityReasons();

                if (unavailabilityReasons.Count > 0)
                {
                    // ★ v3.1.11: 쿨다운이어도 보너스 사용이 있으면 허용
                    // IsAvailable은 IsBonusUsage를 체크하므로, IsAvailable=true면 보너스 사용 가능
                    bool onlyOnCooldown = unavailabilityReasons.All(r =>
                        r == AbilityData.UnavailabilityReasonType.IsOnCooldown ||
                        r == AbilityData.UnavailabilityReasonType.IsOnCooldownUntilEndOfCombat);

                    if (onlyOnCooldown && ability.IsAvailable)
                    {
                        // 쿨다운이지만 보너스 사용 가능 (런 앤 건 등)
                        Main.LogDebug($"[CombatAPI] IsAbilityAvailable: {ability.Name} on cooldown but has bonus usage");
                        return true;
                    }

                    reasons = unavailabilityReasons.Select(r => r.ToString()).ToList();
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                reasons.Add($"Exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.0.17: 공격성 능력인지 확인 (적만 타겟 가능)
        /// </summary>
        public static bool IsOffensiveAbility(AbilityData ability)
        {
            if (ability == null) return false;
            try
            {
                var bp = ability.Blueprint;
                return bp.CanTargetEnemies && !bp.CanTargetFriends;
            }
            catch { return false; }
        }

        #endregion

        #region Unit State

        /// <summary>
        /// HP 퍼센트 반환
        /// ★ v3.0.1: GetActualHP/GetActualMaxHP 기반으로 통합
        /// </summary>
        public static float GetHPPercent(BaseUnitEntity unit)
        {
            if (unit == null) return 0f;
            try
            {
                int current = GetActualHP(unit);
                int max = GetActualMaxHP(unit);
                if (max <= 0) return 100f;
                return (float)current / max * 100f;
            }
            catch { return 100f; }
        }

        /// <summary>
        /// ★ v3.0.13 Fix: AP/MP 수정
        /// Yellow = Action Points (스킬/공격용)
        /// Blue = Movement Points (이동용)
        /// </summary>
        public static float GetCurrentAP(BaseUnitEntity unit)
        {
            if (unit == null) return 0f;
            try
            {
                // ★ Yellow Action Points = 액션 포인트 (능력/공격)
                return unit.CombatState?.ActionPointsYellow ?? 3f;
            }
            catch { return 3f; }
        }

        /// <summary>
        /// ★ v3.0.13 Fix: AP/MP 수정
        /// Blue = Movement Points (이동용)
        /// </summary>
        public static float GetCurrentMP(BaseUnitEntity unit)
        {
            if (unit == null) return 0f;
            try
            {
                // ★ Blue Action Points = 이동 포인트 (Movement Points)
                return unit.CombatState?.ActionPointsBlue ?? 0f;
            }
            catch { return 0f; }
        }

        public static bool CanMove(BaseUnitEntity unit)
        {
            if (unit == null) return false;
            try { return unit.State.CanMove; }
            catch { return false; }
        }

        public static bool CanAct(BaseUnitEntity unit)
        {
            if (unit == null) return false;
            try { return unit.State.CanActInTurnBased; }
            catch { return false; }
        }

        /// <summary>
        /// ★ v3.0.10: 명령 큐가 비어있는지 확인 (이전 명령 완료 여부)
        /// 게임의 TaskNodeWaitCommandsDone과 동일한 체크
        /// </summary>
        public static bool IsCommandQueueEmpty(BaseUnitEntity unit)
        {
            if (unit == null) return true;
            try
            {
                return unit.Commands.Empty;
            }
            catch { return true; }
        }

        /// <summary>
        /// ★ v3.0.10: 유닛이 다음 행동을 할 준비가 되었는지 확인
        /// Commands.Empty && CanActInTurnBased
        /// </summary>
        public static bool IsReadyForNextAction(BaseUnitEntity unit)
        {
            if (unit == null) return false;
            try
            {
                return unit.Commands.Empty && unit.State.CanActInTurnBased;
            }
            catch { return false; }
        }

        public static float GetDistance(BaseUnitEntity from, BaseUnitEntity to)
        {
            if (from == null || to == null) return float.MaxValue;
            try { return Vector3.Distance(from.Position, to.Position); }
            catch { return float.MaxValue; }
        }

        #endregion

        #region Weapon & Ammo

        public static bool HasRangedWeapon(BaseUnitEntity unit)
        {
            if (unit == null) return false;
            try
            {
                var primary = unit.Body?.PrimaryHand?.MaybeWeapon;
                if (primary != null && !primary.Blueprint.IsMelee) return true;

                var secondary = unit.Body?.SecondaryHand?.MaybeWeapon;
                if (secondary != null && !secondary.Blueprint.IsMelee) return true;

                return false;
            }
            catch { return false; }
        }

        public static bool HasMeleeWeapon(BaseUnitEntity unit)
        {
            if (unit == null) return false;
            try
            {
                var primary = unit.Body?.PrimaryHand?.MaybeWeapon;
                if (primary != null && primary.Blueprint.IsMelee) return true;

                var secondary = unit.Body?.SecondaryHand?.MaybeWeapon;
                if (secondary != null && secondary.Blueprint.IsMelee) return true;

                return false;
            }
            catch { return false; }
        }

        public static bool NeedsReloadAnyRanged(BaseUnitEntity unit)
        {
            if (unit == null) return false;

            try
            {
                var body = unit.Body;
                if (body == null) return false;

                // 현재 무기 체크
                if (CheckWeaponNeedsReload(body.PrimaryHand?.MaybeWeapon)) return true;
                if (CheckWeaponNeedsReload(body.SecondaryHand?.MaybeWeapon)) return true;

                // 다른 무기 세트 체크
                var handsSets = body.HandsEquipmentSets;
                if (handsSets != null)
                {
                    foreach (var set in handsSets)
                    {
                        if (CheckWeaponNeedsReload(set?.PrimaryHand?.MaybeWeapon)) return true;
                        if (CheckWeaponNeedsReload(set?.SecondaryHand?.MaybeWeapon)) return true;
                    }
                }
            }
            catch (Exception ex)
            {
                // ★ v3.4.01: P1-2 예외 상세 로깅
                Main.LogDebug($"[CombatAPI] NeedsReloadAnyRanged error: {ex.Message}");
            }

            return false;
        }

        private static bool CheckWeaponNeedsReload(ItemEntityWeapon weapon)
        {
            if (weapon == null) return false;
            if (weapon.Blueprint.IsMelee) return false;

            int maxAmmo = weapon.Blueprint?.WarhammerMaxAmmo ?? -1;
            if (maxAmmo <= 0) return false;  // 탄약 필요 없음

            return weapon.CurrentAmmo <= 0;
        }

        public static int GetCurrentAmmo(BaseUnitEntity unit)
        {
            if (unit == null) return -1;
            try
            {
                var weapon = unit.Body?.PrimaryHand?.MaybeWeapon;
                if (weapon == null) return -1;
                if (weapon.Blueprint.IsMelee) return -1;

                return weapon.CurrentAmmo;
            }
            catch { return -1; }
        }

        public static int GetMaxAmmo(BaseUnitEntity unit)
        {
            if (unit == null) return -1;
            try
            {
                var weapon = unit.Body?.PrimaryHand?.MaybeWeapon;
                if (weapon == null) return -1;
                if (weapon.Blueprint.IsMelee) return -1;

                return weapon.Blueprint?.WarhammerMaxAmmo ?? -1;
            }
            catch { return -1; }
        }

        #endregion

        #region Unit Lists

        public static List<BaseUnitEntity> GetEnemies(BaseUnitEntity unit)
        {
            var enemies = new List<BaseUnitEntity>();
            if (unit == null) return enemies;

            try
            {
                var allUnits = Game.Instance?.State?.AllBaseAwakeUnits;
                if (allUnits == null) return enemies;

                foreach (var other in allUnits)
                {
                    if (other == null || other == unit) continue;
                    if (other.LifeState.IsDead) continue;

                    // 적 판별
                    bool isEnemy = (unit.IsPlayerFaction && other.IsPlayerEnemy) ||
                                   (!unit.IsPlayerFaction && !other.IsPlayerEnemy);

                    if (isEnemy)
                    {
                        enemies.Add(other);
                    }
                }
            }
            catch (Exception ex)
            {
                // ★ v3.4.01: P1-2 예외 상세 로깅
                Main.LogDebug($"[CombatAPI] GetEnemies error: {ex.Message}");
            }

            return enemies;
        }

        public static List<BaseUnitEntity> GetAllies(BaseUnitEntity unit)
        {
            var allies = new List<BaseUnitEntity>();
            if (unit == null) return allies;

            try
            {
                var allUnits = Game.Instance?.State?.AllBaseAwakeUnits;
                if (allUnits == null) return allies;

                foreach (var other in allUnits)
                {
                    if (other == null || other == unit) continue;
                    if (other.LifeState.IsDead) continue;

                    // 아군 판별
                    bool isAlly = unit.IsPlayerFaction == other.IsPlayerFaction;

                    if (isAlly)
                    {
                        allies.Add(other);
                    }
                }
            }
            catch (Exception ex)
            {
                // ★ v3.4.01: P1-2 예외 상세 로깅
                Main.LogDebug($"[CombatAPI] GetAllies error: {ex.Message}");
            }

            return allies;
        }

        #endregion

        #region Abilities

        /// <summary>
        /// ★ v3.0.94: GetUnavailabilityReasons() 체크 추가
        /// 기존: data.IsAvailable만 체크 → 쿨다운 능력도 포함됨!
        /// 수정: GetUnavailabilityReasons()로 쿨다운, 탄약, 충전 등 모두 체크
        /// ★ v3.1.11: 보너스 사용(런 앤 건 등) 처리 추가
        /// </summary>
        public static List<AbilityData> GetAvailableAbilities(BaseUnitEntity unit)
        {
            var abilities = new List<AbilityData>();
            if (unit == null) return abilities;

            try
            {
                var rawAbilities = unit.Abilities?.RawFacts;
                if (rawAbilities == null) return abilities;

                foreach (var ability in rawAbilities)
                {
                    var data = ability?.Data;
                    if (data == null) continue;

                    // ★ v3.0.94: IsAvailable + GetUnavailabilityReasons() 체크
                    // 기존 IsAvailable만으로는 쿨다운을 필터링하지 않음!
                    // ★ v3.1.11: IsAvailable은 IsBonusUsage 체크를 포함함
                    // 쿨다운이어도 보너스 사용이 있으면 IsAvailable=true
                    if (!data.IsAvailable) continue;

                    // ★ 핵심: GetUnavailabilityReasons()로 실제 사용 가능 여부 체크
                    var unavailabilityReasons = data.GetUnavailabilityReasons();
                    if (unavailabilityReasons.Count > 0)
                    {
                        // ★ v3.1.11: 쿨다운이어도 보너스 사용이 있으면 허용
                        // GetUnavailabilityReasons()는 IsBonusUsage를 체크하지 않음
                        // 하지만 IsAvailable은 체크함 → IsAvailable=true면 보너스 사용 가능
                        bool onlyOnCooldown = unavailabilityReasons.All(r =>
                            r == AbilityData.UnavailabilityReasonType.IsOnCooldown ||
                            r == AbilityData.UnavailabilityReasonType.IsOnCooldownUntilEndOfCombat);

                        if (onlyOnCooldown)
                        {
                            // IsAvailable=true이고 쿨다운만 문제라면 → 보너스 사용 가능
                            Main.Log($"[CombatAPI] {data.Name}: On cooldown but has bonus usage - allowing");
                        }
                        else
                        {
                            // 쿨다운 이외의 이유가 있음 → 스킵
                            Main.LogDebug($"[CombatAPI] Filtered out {data.Name}: {string.Join(", ", unavailabilityReasons)}");
                            continue;
                        }
                    }

                    abilities.Add(data);
                }
            }
            catch (Exception ex)
            {
                // ★ v3.4.01: P1-2 예외 상세 로깅
                Main.LogDebug($"[CombatAPI] GetAvailableAbilities error: {ex.Message}");
            }

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
        public static AbilityData FindAnyAttackAbility(BaseUnitEntity unit, RangePreference preference)
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
                    var abilityData = ability?.Data;
                    if (abilityData == null) continue;

                    // 1. 무기 공격만
                    if (abilityData.Weapon == null) continue;

                    // 2. 재장전 제외
                    if (AbilityDatabase.IsReload(abilityData)) continue;

                    // 3. ★ v3.0.17: 수류탄/폭발물 제외 (v2.2 포팅)
                    if (CombatHelpers.IsGrenadeOrExplosive(abilityData))
                    {
                        Main.LogDebug($"[CombatAPI] Skipping {abilityData.Name}: IsGrenadeOrExplosive");
                        continue;
                    }

                    // 4. ★ v3.0.18: CanTargetEnemies 체크 (v3.0.16에서 누락됨!)
                    // "칼날" 같은 스킬은 Weapon != null 이지만 적을 타겟할 수 없음
                    var bp = abilityData.Blueprint;
                    if (bp != null && !bp.CanTargetEnemies)
                    {
                        Main.LogDebug($"[CombatAPI] Skipping {abilityData.Name}: CanTargetEnemies=false");
                        continue;
                    }

                    // 5. ★ v3.0.17: 핵심! GetUnavailabilityReasons() 체크 (v2.2 포팅)
                    List<string> reasons;
                    if (!IsAbilityAvailable(abilityData, out reasons))
                    {
                        Main.LogDebug($"[CombatAPI] Skipping {abilityData.Name}: {string.Join(", ", reasons)}");
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
                            Main.LogDebug($"[CombatAPI] Found preferred ({preference}) attack: {abilityData.Name} (range={range:F1})");
                        }
                        // ★ v3.0.27: break 제거 - 더 긴 사거리 무기를 찾기 위해 계속 검색
                    }
                    else if (fallbackAttack == null)
                    {
                        fallbackAttack = abilityData;  // 폴백용 저장
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
                            Main.LogDebug($"[CombatAPI] Found ranged offensive ability (pref={preference}): {abilityData.Name}");
                            return abilityData;
                        }
                    }
                }

                // 폴백 무기 사용
                if (fallbackAttack != null)
                {
                    Main.LogDebug($"[CombatAPI] No preferred weapon, using fallback: {fallbackAttack.Name}");
                    return fallbackAttack;
                }

                // ★ v3.0.17: 무기 공격이 없으면 공격성 능력 찾기 (v2.2 포팅)
                foreach (var ability in rawAbilities)
                {
                    var abilityData = ability?.Data;
                    if (abilityData == null) continue;

                    if (IsOffensiveAbility(abilityData))
                    {
                        List<string> reasons;
                        if (IsAbilityAvailable(abilityData, out reasons))
                        {
                            Main.LogDebug($"[CombatAPI] Found offensive ability as fallback: {abilityData.Name}");
                            return abilityData;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[CombatAPI] FindAnyAttackAbility error: {ex.Message}");
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
            catch { return 1f; }
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
            catch { return 0f; }
        }

        /// <summary>
        /// ★ v3.0.55: 능력이 MP를 전부 클리어하는지 확인
        /// </summary>
        public static bool AbilityClearsMPAfterUse(AbilityData ability)
        {
            if (ability == null) return false;
            try
            {
                return ability.ClearMPAfterUse;
            }
            catch { return false; }
        }

        public static bool HasActiveBuff(BaseUnitEntity unit, AbilityData ability)
        {
            if (unit == null || ability == null) return false;

            try
            {
                // ★ v3.4.01: P0-3 Blueprint null 체크
                if (ability.Blueprint == null) return false;

                // 능력의 버프 블루프린트 추출
                var runAction = ability.Blueprint.GetComponent<AbilityEffectRunAction>();
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
                Main.LogDebug($"[CombatAPI] HasActiveBuff error: {ex.Message}");
            }

            return false;
        }

        #endregion

        #region Veil & Psychic

        /// <summary>
        /// 현재 Veil Thickness 값
        /// ★ LESSONS_LEARNED 10.3: Veil Degradation 체크
        /// </summary>
        public static int GetVeilThickness()
        {
            try
            {
                return Game.Instance?.TurnController?.VeilThicknessCounter?.Value ?? 0;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 능력이 사이킥 파워인지 확인
        /// </summary>
        public static bool IsPsychicAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                return ability.Blueprint?.AbilityParamsSource == WarhammerAbilityParamsSource.PsychicPower;
            }
            catch { return false; }
        }

        /// <summary>
        /// Veil 상태에서 사이킥 능력 사용이 안전한지 확인
        /// ★ LESSONS_LEARNED 10.3:
        /// - 10 이상: 주의
        /// - 15 이상: 위험 (Major Psychic 차단)
        /// - 20: 최대 위험
        /// </summary>
        public static bool IsPsychicSafeToUse(AbilityData ability)
        {
            if (!IsPsychicAbility(ability)) return true;

            int veil = GetVeilThickness();

            // Veil +3 이상인 Major Psychic은 15 이상에서 차단
            // (모든 사이킥을 15 이상에서 차단하는 것이 안전)
            if (veil >= 15)
            {
                Main.LogDebug($"[CombatAPI] Veil too high ({veil}): blocking psychic {ability.Name}");
                return false;
            }

            return true;
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
            catch { return 0; }
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
            catch { return 0; }
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
                Main.LogDebug($"[CombatAPI] GetDamagePrediction error: {ex.Message}");
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
            catch { return false; }
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
            catch { return false; }
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
            catch { return 0f; }
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
                return 15f;  // 기본 추정치
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
                        Main.LogDebug($"[CombatAPI] Not hittable: {enemy.CharacterName} - {reason} (dist={score.Distance:F1}m, ability={attackAbility.Name})");
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
                Main.LogDebug($"[CombatAPI] Best target: {hittable.Target.CharacterName} (score={hittable.Score:F1})");
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
                Main.LogDebug($"[Scoring] {target.CharacterName}: +50 (1-hit kill possible, HP={scoreData.ActualHP}, MinDmg={scoreData.PredictedMinDamage})");
            }
            // 2타 킬 가능 (+25)
            else if (scoreData != null && scoreData.CanKillInTwoHits && isHittable)
            {
                score += 25f;
                Main.LogDebug($"[Scoring] {target.CharacterName}: +25 (2-hit kill possible)");
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

        #region Retreat & Cover System

        /// <summary>
        /// 엄폐 타입 (게임 API 래퍼)
        /// </summary>
        public enum CoverLevel
        {
            None,
            Half,
            Full,
            Invisible
        }

        /// <summary>
        /// 특정 위치의 엄폐 타입 (간략화 - 거리 기반 추정)
        /// </summary>
        public static CoverLevel GetCoverTypeAtPosition(Vector3 position, BaseUnitEntity fromEnemy)
        {
            if (fromEnemy == null) return CoverLevel.None;

            try
            {
                // 간단한 거리 기반 추정 (실제 LOS 체크는 게임 노드 필요)
                float distance = Vector3.Distance(fromEnemy.Position, position);

                // 거리가 멀면 Full cover 추정
                if (distance > 20f) return CoverLevel.Full;
                if (distance > 10f) return CoverLevel.Half;

                return CoverLevel.None;
            }
            catch
            {
                return CoverLevel.None;
            }
        }

        /// <summary>
        /// 후퇴 위치 찾기 - TurnPlanner.PlanRetreat에서 사용
        /// </summary>
        public static Vector3? FindRetreatPosition(
            BaseUnitEntity unit,
            BaseUnitEntity nearestEnemy,
            float minSafeDistance,
            List<BaseUnitEntity> allEnemies)
        {
            if (unit == null || nearestEnemy == null) return null;

            // 기본: 적으로부터 반대 방향
            var retreatDir = (unit.Position - nearestEnemy.Position).normalized;
            var baseRetreatPos = unit.Position + retreatDir * minSafeDistance * 1.5f;

            // 다른 적 확인
            if (allEnemies != null)
            {
                float bestScore = float.MinValue;
                Vector3 bestPos = baseRetreatPos;

                // 8방향 검색
                for (int i = 0; i < 8; i++)
                {
                    float angle = i * 45f * Mathf.Deg2Rad;
                    var dir = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
                    var testPos = unit.Position + dir * minSafeDistance * 1.5f;

                    float score = EvaluateRetreatPosition(testPos, allEnemies, minSafeDistance);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestPos = testPos;
                    }
                }

                return bestPos;
            }

            return baseRetreatPos;
        }

        private static float EvaluateRetreatPosition(Vector3 position, List<BaseUnitEntity> enemies, float minSafeDistance)
        {
            float score = 0f;

            foreach (var enemy in enemies)
            {
                if (enemy == null || enemy.LifeState.IsDead) continue;

                float dist = Vector3.Distance(position, enemy.Position);

                if (dist >= minSafeDistance)
                {
                    score += 10f;  // 안전 거리 확보
                }
                else
                {
                    score -= (minSafeDistance - dist) * 5f;  // 너무 가까우면 감점
                }
            }

            return score;
        }

        /// <summary>
        /// 후퇴가 필요한지 확인
        /// </summary>
        public static bool ShouldRetreat(
            BaseUnitEntity unit,
            RangePreference preference,
            float nearestEnemyDistance,
            float minSafeDistance)
        {
            // 원거리 선호가 아니면 후퇴 불필요
            if (preference != RangePreference.PreferRanged)
                return false;

            // 안전 거리 미만이면 후퇴 필요
            return nearestEnemyDistance < minSafeDistance;
        }

        #endregion

        #region Momentum System

        private const int MOMENTUM_START = 100;
        private const int MOMENTUM_HEROIC_THRESHOLD = 175;
        private const int MOMENTUM_DESPERATE_THRESHOLD = 50;

        /// <summary>
        /// 현재 Momentum 값
        /// </summary>
        public static int GetCurrentMomentum()
        {
            try
            {
                var momentumGroups = Game.Instance?.Player?.GetOrCreate<Kingmaker.Controllers.TurnBased.TurnDataPart>()?.MomentumGroups;
                if (momentumGroups == null) return MOMENTUM_START;

                foreach (var group in momentumGroups)
                {
                    if (group.IsParty)
                        return group.Momentum;
                }
                return MOMENTUM_START;
            }
            catch { return MOMENTUM_START; }
        }

        /// <summary>
        /// Heroic Act 사용 가능 여부 (Momentum 175+)
        /// </summary>
        public static bool IsHeroicActAvailable()
        {
            return GetCurrentMomentum() >= MOMENTUM_HEROIC_THRESHOLD;
        }

        /// <summary>
        /// Desperate Measure 활성 여부 (Momentum <= 50)
        /// </summary>
        public static bool IsDesperateMeasureActive()
        {
            return GetCurrentMomentum() <= MOMENTUM_DESPERATE_THRESHOLD;
        }

        /// <summary>
        /// Momentum 상태 문자열
        /// </summary>
        public static string GetMomentumStatusString()
        {
            int momentum = GetCurrentMomentum();
            string status;

            if (momentum >= 175)
                status = "HEROIC";
            else if (momentum >= 100)
                status = "High";
            else if (momentum >= 50)
                status = "Normal";
            else if (momentum > 25)
                status = "Low";
            else
                status = "DESPERATE";

            return $"Momentum: {momentum} ({status})";
        }

        #endregion

        #region Ability Type Detection

        /// <summary>
        /// 능력이 Momentum 생성 능력인지 확인
        /// </summary>
        public static bool IsMomentumGeneratingAbility(AbilityData ability)
        {
            if (ability == null) return false;

            string bpName = ability.Blueprint?.name?.ToLower() ?? "";
            return bpName.Contains("momentum") || bpName.Contains("inspire") ||
                   bpName.Contains("rally") || bpName.Contains("warcry");
        }

        /// <summary>
        /// 능력이 방어 자세인지 확인
        /// </summary>
        public static bool IsDefensiveStanceAbility(AbilityData ability)
        {
            if (ability == null) return false;

            string bpName = ability.Blueprint?.name?.ToLower() ?? "";
            return bpName.Contains("defensive") || bpName.Contains("stance") ||
                   bpName.Contains("guard") || bpName.Contains("bulwark");
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
        /// </summary>
        public static bool IsRighteousFuryAbility(AbilityData ability)
        {
            if (ability == null) return false;

            string bpName = ability.Blueprint?.name?.ToLower() ?? "";
            string guid = AbilityDatabase.GetGuid(ability);

            // GUID 체크
            if (guid == "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx")  // 실제 GUID로 교체 필요
                return true;

            return bpName.Contains("revelinslaughter") || bpName.Contains("righteousfury") ||
                   bpName.Contains("학살") || bpName.Contains("분노");
        }

        /// <summary>
        /// ★ v3.0.58: 능력의 정확한 사거리 반환 (게임 API 사용)
        /// </summary>
        public static float GetAbilityRange(AbilityData ability)
        {
            if (ability == null) return 0f;

            try
            {
                var bp = ability.Blueprint;
                if (bp == null) return 0f;

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

                // 3. 폴백: 원거리 기본값
                return 15f;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[CombatAPI] GetAbilityRange error: {ex.Message}");
                return 15f;
            }
        }

        /// <summary>
        /// 능력이 무제한 사거리인지 확인
        /// </summary>
        public static bool IsUnlimitedRange(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                return ability.Blueprint?.Range == AbilityRange.Unlimited;
            }
            catch { return false; }
        }

        #endregion

        #region Ability Filtering (Timing-Aware)

        /// <summary>
        /// 선제적 버프만 필터링 (전투 시작/첫 행동 전)
        /// </summary>
        public static List<AbilityData> FilterProactiveBuffs(List<AbilityData> abilities, BaseUnitEntity unit)
        {
            if (abilities == null) return new List<AbilityData>();

            return abilities.Where(a => {
                var timing = AbilityDatabase.GetTiming(a);
                bool isProactive = timing == AbilityTiming.PreCombatBuff || timing == AbilityTiming.PreAttackBuff;

                // 이미 활성화된 버프 제외
                if (isProactive && HasActiveBuff(unit, a))
                    return false;

                return isProactive;
            }).ToList();
        }

        /// <summary>
        /// PostFirstAction 능력만 필터링 (첫 행동 후)
        /// </summary>
        public static List<AbilityData> FilterPostFirstActionAbilities(List<AbilityData> abilities)
        {
            if (abilities == null) return new List<AbilityData>();

            return abilities.Where(a => AbilityDatabase.IsPostFirstAction(a)).ToList();
        }

        /// <summary>
        /// 턴 종료 능력만 필터링
        /// </summary>
        public static List<AbilityData> FilterTurnEndingAbilities(List<AbilityData> abilities)
        {
            if (abilities == null) return new List<AbilityData>();

            return abilities.Where(a => AbilityDatabase.IsTurnEnding(a)).ToList();
        }

        /// <summary>
        /// 마무리 능력만 필터링
        /// </summary>
        public static List<AbilityData> FilterFinisherAbilities(List<AbilityData> abilities)
        {
            if (abilities == null) return new List<AbilityData>();

            return abilities.Where(a => AbilityDatabase.IsFinisher(a)).ToList();
        }

        #endregion

        #region Resource Prediction

        /// <summary>
        /// ★ v3.0.98: 능력이 MP를 회복하는지 확인하고 예상 회복량 반환
        /// Blueprint의 WarhammerContextActionRestoreActionPoints 컴포넌트에서 직접 읽어옴
        /// </summary>
        public static float GetAbilityMPRecovery(AbilityData ability, BaseUnitEntity caster = null)
        {
            if (ability?.Blueprint == null) return 0f;

            try
            {
                // AbilityEffectRunAction 컴포넌트에서 Actions 확인
                var runAction = ability.Blueprint.GetComponent<AbilityEffectRunAction>();
                if (runAction?.Actions?.Actions == null) return 0f;

                foreach (var action in runAction.Actions.Actions)
                {
                    // WarhammerContextActionRestoreActionPoints 찾기
                    if (action is WarhammerContextActionRestoreActionPoints restoreAction)
                    {
                        // MovePointsToMax가 true면 최대 MP 반환 (보수적으로 10 추정)
                        if (restoreAction.MovePointsToMax)
                        {
                            return 10f;
                        }

                        // MovePoints 값 계산 (ContextValue)
                        // ContextValue는 정적 값이거나 스탯 기반일 수 있음
                        var movePoints = restoreAction.MovePoints;
                        if (movePoints != null)
                        {
                            // 정적 값이 있으면 사용
                            int staticValue = movePoints.Value;
                            if (staticValue > 0)
                            {
                                Main.LogDebug($"[CombatAPI] {ability.Name}: MP recovery = {staticValue}");
                                return staticValue;
                            }

                            // 런타임 계산 필요 (캐스터 컨텍스트 기반)
                            // 보수적으로 기본값 반환
                            return 6f;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[CombatAPI] GetAbilityMPRecovery error: {ex.Message}");
            }

            return 0f;
        }

        /// <summary>
        /// ★ v3.0.98: 능력이 AP를 회복하는지 확인하고 예상 회복량 반환
        /// </summary>
        public static float GetAbilityAPRecovery(AbilityData ability, BaseUnitEntity caster = null)
        {
            if (ability?.Blueprint == null) return 0f;

            try
            {
                var runAction = ability.Blueprint.GetComponent<AbilityEffectRunAction>();
                if (runAction?.Actions?.Actions == null) return 0f;

                foreach (var action in runAction.Actions.Actions)
                {
                    if (action is WarhammerContextActionRestoreActionPoints restoreAction)
                    {
                        if (restoreAction.ActionPointsToMax)
                        {
                            return 5f;  // 최대 AP (보수적 추정)
                        }

                        var actionPoints = restoreAction.ActionPoints;
                        if (actionPoints != null)
                        {
                            int staticValue = actionPoints.Value;
                            if (staticValue > 0)
                            {
                                Main.LogDebug($"[CombatAPI] {ability.Name}: AP recovery = {staticValue}");
                                return staticValue;
                            }

                            return 2f;  // 보수적 기본값
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[CombatAPI] GetAbilityAPRecovery error: {ex.Message}");
            }

            return 0f;
        }

        /// <summary>
        /// ★ v3.0.98: 능력이 리소스(AP/MP)를 회복하는 능력인지 확인
        /// </summary>
        public static bool IsResourceRecoveryAbility(AbilityData ability)
        {
            return GetAbilityMPRecovery(ability) > 0 || GetAbilityAPRecovery(ability) > 0;
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
            catch { return null; }
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
            catch { return 0f; }
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
            catch { return null; }
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
            catch { return Kingmaker.UnitLogic.Abilities.Components.TargetType.Enemy; }
        }

        /// <summary>
        /// ★ v3.1.19: Point 타겟 능력인지 확인 (개선)
        /// CanTargetPoint + 실제 AOE 반경 필요
        /// </summary>
        public static bool IsPointTargetAbility(AbilityData ability)
        {
            try
            {
                var bp = ability?.Blueprint;
                if (bp == null || !bp.CanTargetPoint) return false;

                // ★ v3.1.19: 패턴 설정에서 실제 반경 확인
                var pattern = ability.GetPatternSettings()?.Pattern;
                if (pattern != null)
                    return pattern.Radius > 0;  // 실제 AOE 반경 필요

                // Blueprint AOE 반경 폴백
                return bp.AoERadius > 0;
            }
            catch { return false; }
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
                Main.LogDebug($"[CombatAPI] GetPatternAngle error: {ex.Message}");
                return 90f;
            }
        }

        /// <summary>
        /// ★ v3.1.18: 패턴이 방향성 패턴인지 확인 (Cone/Ray/Sector)
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
            catch { return false; }
        }

        /// <summary>
        /// ★ v3.1.23: 인접 아군 수 계산 (Self-Targeted AOE 안전성 체크)
        /// </summary>
        public static int CountAdjacentAllies(BaseUnitEntity unit, float radius = 2.5f)
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

                    // 거리 체크
                    float dist = Vector3.Distance(unit.Position, other.Position);
                    if (dist <= radius)
                        count++;
                }

                return count;
            }
            catch { return 0; }
        }

        /// <summary>
        /// ★ v3.1.23: 인접 적 수 계산 (Self-Targeted AOE 효율성 체크)
        /// </summary>
        public static int CountAdjacentEnemies(BaseUnitEntity unit, float radius = 2.5f)
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

                    // 거리 체크
                    float dist = Vector3.Distance(unit.Position, other.Position);
                    if (dist <= radius)
                        count++;
                }

                return count;
            }
            catch { return 0; }
        }

        #endregion

        #region Pattern Info Cache (v3.1.19)

        /// <summary>
        /// ★ v3.1.19: AOE 패턴 정보 통합 클래스
        /// </summary>
        public class PatternInfo
        {
            public Kingmaker.Blueprints.PatternType? Type { get; set; }
            public float Radius { get; set; }
            public float Angle { get; set; }
            public Kingmaker.UnitLogic.Abilities.Components.TargetType TargetType { get; set; }
            public bool IsDirectional { get; set; }
            public bool IsValid => Radius > 0;
        }

        private static Dictionary<string, PatternInfo> PatternCache = new Dictionary<string, PatternInfo>();

        /// <summary>
        /// ★ v3.1.19: 패턴 정보 조회 (캐싱)
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
                var info = new PatternInfo
                {
                    Type = patternType,
                    Radius = GetAoERadius(ability),
                    Angle = GetPatternAngle(ability),
                    TargetType = GetAoETargetType(ability),
                    IsDirectional = IsDirectionalPattern(patternType)
                };

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
            catch { return false; }
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

        #region SpringAttack (v3.5.22)

        /// <summary>
        /// ★ v3.5.22: 유닛이 SpringAttack(Acrobatic Artistry)을 사용할 수 있는 조건인지 확인
        /// - 갭클로저 2회 이상 사용 → 사용 (역순 공격 2번 가치)
        /// - 단, 노렸던 적이 전부 죽었으면 사용 X (역순 공격해도 의미 없음)
        /// </summary>
        public static bool CanUseSpringAttackAbility(BaseUnitEntity unit)
        {
            if (unit == null) return false;

            try
            {
                var springAttackPart = unit.GetOptional<UnitPartSpringAttack>();
                if (springAttackPart == null) return false;

                int entryCount = springAttackPart.Entries?.Count ?? 0;

                // ★ 갭클로저 2회 미만 → 사용 안 함
                if (entryCount < 2)
                {
                    Main.LogDebug($"[CombatAPI] {unit.CharacterName}: SpringAttack skip (entries={entryCount}, need 2+)");
                    return false;
                }

                // ★ 살아있는 적이 있는지 확인 (전부 죽었으면 역순 공격 의미 없음)
                var enemies = GetEnemies(unit);
                int livingEnemies = enemies.Count;

                if (livingEnemies == 0)
                {
                    Main.Log($"[CombatAPI] {unit.CharacterName}: SpringAttack skip - all enemies dead");
                    return false;
                }

                Main.Log($"[CombatAPI] {unit.CharacterName}: SpringAttack {entryCount} entries + {livingEnemies} living enemies - use!");
                return true;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[CombatAPI] CanUseSpringAttackAbility error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.5.22: 유닛이 UnitPartSpringAttack를 가지고 있는지 확인
        /// </summary>
        public static bool HasSpringAttackPart(BaseUnitEntity unit)
        {
            if (unit == null) return false;

            try
            {
                return unit.GetOptional<UnitPartSpringAttack>() != null;
            }
            catch { return false; }
        }

        #endregion
    }
}
