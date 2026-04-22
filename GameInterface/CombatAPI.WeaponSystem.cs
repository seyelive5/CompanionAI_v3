using System;
using System.Collections.Generic;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.GameCommands;  // ★ v3.9.72: SwitchHandEquipment 확장 메서드
using Kingmaker.Items;
using Kingmaker.UnitLogic.Abilities;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.GameInterface
{
    public static partial class CombatAPI
    {
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
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] HasRangedWeapon failed for {unit?.CharacterName}: {ex.Message}");
                return false;
            }
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
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] HasMeleeWeapon failed for {unit?.CharacterName}: {ex.Message}");
                return false;
            }
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
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] NeedsReloadAnyRanged error: {ex.Message}");
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
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetCurrentAmmo failed for {unit?.CharacterName}: {ex.Message}");
                return -1;
            }
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
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetMaxAmmo failed for {unit?.CharacterName}: {ex.Message}");
                return -1;
            }
        }

        #endregion

        #region Weapon Set Management (v3.9.72)

        /// <summary>
        /// ★ v3.9.72: 현재 활성 무기 세트 인덱스 (0 또는 1)
        /// </summary>
        public static int GetCurrentWeaponSetIndex(BaseUnitEntity unit)
        {
            return unit?.Body?.CurrentHandEquipmentSetIndex ?? 0;
        }

        /// <summary>
        /// ★ v3.9.72: 양쪽 세트에 모두 무기가 장착되어 있는지 확인
        /// </summary>
        public static bool HasMultipleWeaponSets(BaseUnitEntity unit)
        {
            if (unit?.Body?.HandsEquipmentSets == null) return false;
            var sets = unit.Body.HandsEquipmentSets;
            if (sets.Count < 2) return false;

            try
            {
                bool set0HasWeapon = sets[0]?.PrimaryHand?.HasItem == true;
                bool set1HasWeapon = sets[1]?.PrimaryHand?.HasItem == true;
                return set0HasWeapon && set1HasWeapon;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] HasMultipleWeaponSets failed for {unit?.CharacterName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.9.72: 무기 세트 전환 실행 (0 AP)
        /// </summary>
        public static void SwitchWeaponSet(BaseUnitEntity unit, int targetSetIndex)
        {
            if (unit == null || targetSetIndex < 0 || targetSetIndex > 1) return;
            if (unit.Body.CurrentHandEquipmentSetIndex == targetSetIndex) return;

            Game.Instance.GameCommandQueue.SwitchHandEquipment(unit, targetSetIndex);
            Main.Log($"[CombatAPI] ★ Weapon switch: {unit.CharacterName} -> Set {targetSetIndex}");
        }

        /// <summary>
        /// ★ v3.9.72: 특정 무기 세트의 주 무기 이름 조회
        /// </summary>
        public static string GetWeaponSetPrimaryName(BaseUnitEntity unit, int setIndex)
        {
            if (unit?.Body?.HandsEquipmentSets == null) return null;
            var sets = unit.Body.HandsEquipmentSets;
            if (setIndex < 0 || setIndex >= sets.Count) return null;

            try
            {
                // ★ MaybeItem 사용 — MaybeWeapon은 비활성 세트에서 Active 체크로 null 반환
                var weapon = sets[setIndex]?.PrimaryHand?.MaybeItem as ItemEntityWeapon;
                return weapon?.Name;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetWeaponNameForSet failed for {unit?.CharacterName}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ★ v3.9.72: 양쪽 세트의 주 무기 Blueprint가 동일한지 확인
        /// 같은 무기면 전환 의미 없음
        /// </summary>
        public static bool AreBothWeaponSetsSame(BaseUnitEntity unit)
        {
            if (unit?.Body?.HandsEquipmentSets == null) return true;
            var sets = unit.Body.HandsEquipmentSets;
            if (sets.Count < 2) return true;

            try
            {
                // ★ MaybeItem 사용 — MaybeWeapon은 비활성 세트에서 Active 체크로 null 반환
                var weapon0 = (sets[0]?.PrimaryHand?.MaybeItem as ItemEntityWeapon)?.Blueprint;
                var weapon1 = (sets[1]?.PrimaryHand?.MaybeItem as ItemEntityWeapon)?.Blueprint;
                if (weapon0 == null || weapon1 == null) return false;
                return weapon0 == weapon1;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] AreBothWeaponSetsSame failed for {unit?.CharacterName}: {ex.Message}");
                return true;
            }
        }

        #endregion

        #region Weapon Range Profile (v3.9.24)

        /// <summary>
        /// ★ v3.9.24: 무기 사거리 중앙집중 프로필
        /// 모든 서브시스템이 일관된 무기 사거리 정보를 사용하도록 중앙에서 관리
        /// </summary>
        public struct WeaponRangeProfile
        {
            /// <summary>무기 최대 사거리 (타일 단위, RangeCells)</summary>
            public float MaxRange;
            /// <summary>무기 최적 사거리 (타일 단위, 없으면 0)</summary>
            public float OptimalRange;
            /// <summary>유효 공격 거리 (방향성 AoE면 patternRadius, 아니면 OptimalRange 또는 MaxRange)</summary>
            public float EffectiveRange;
            /// <summary>Scatter 무기 (자동 명중)</summary>
            public bool IsScatter;
            /// <summary>근접 무기</summary>
            public bool IsMelee;
            /// <summary>Cone/Ray/Sector 패턴 보유</summary>
            public bool HasDirectionalPattern;
            /// <summary>방향성 패턴 반경 (타일 단위)</summary>
            public float PatternRadius;
            /// <summary>★ v3.110.11: 자동 계산된 MinSafeDistance (타일). EffectiveRange × 0.3, 근접/Scatter=0.</summary>
            public float ClampedMinSafeDistance;
            /// <summary>max(0, EffectiveRange - 1) — 후퇴 시 최대 거리</summary>
            public float MaxRetreatDistance;
            /// <summary>EffectiveRange <= 8 인 단거리 무기</summary>
            public bool IsShortRange => EffectiveRange <= 8f;
        }

        // ★ v3.9.24: 유닛당 턴별 캐시 (턴 시작 시 ClearAll()로 클리어)
        private static readonly Dictionary<string, WeaponRangeProfile> _weaponRangeCache = new Dictionary<string, WeaponRangeProfile>();

        /// <summary>
        /// ★ v3.9.24 → v3.110.11: 무기 사거리 프로필 중앙 계산 (자동 MinSafeDistance)
        /// - 무기의 OptimalRange/AttackRange 에서 직접 조회
        /// - 방향성 AoE(Cone/Ray/Sector)면 EffectiveRange = patternRadius
        /// - ClampedMinSafeDistance는 무기 특성에서 자동 계산 (사용자 설정 제거)
        ///   근접/Scatter: 0
        ///   Cone/Ray: PatternRadius × 0.3 (근접에서 Cone burst 가능)
        ///   일반 원거리: EffectiveRange × 0.3
        /// - 모든 서브시스템이 이 하나의 소스에서 무기 사거리를 조회
        /// </summary>
        public static WeaponRangeProfile GetWeaponRangeProfile(BaseUnitEntity unit)
        {
            if (unit == null)
                return CreateDefaultProfile();

            string cacheKey = unit.UniqueId;
            if (_weaponRangeCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var profile = CalculateWeaponRangeProfile(unit);
            _weaponRangeCache[cacheKey] = profile;

            Main.Log($"[CombatAPI] WeaponRangeProfile for {unit.CharacterName}: " +
                $"MaxRange={profile.MaxRange:F1}, OptimalRange={profile.OptimalRange:F1}, " +
                $"EffectiveRange={profile.EffectiveRange:F1}, " +
                $"IsMelee={profile.IsMelee}, IsScatter={profile.IsScatter}, " +
                $"HasDirectional={profile.HasDirectionalPattern}, PatternRadius={profile.PatternRadius:F1}, " +
                $"AutoMinSafe={profile.ClampedMinSafeDistance:F1}, " +
                $"IsShortRange={profile.IsShortRange}");

            return profile;
        }

        /// <summary>★ v3.110.11: 무기 특성 기반 자동 MinSafeDistance (타일).</summary>
        private const float AUTO_MINSAFE_RATIO = 0.3f;

        private static WeaponRangeProfile CalculateWeaponRangeProfile(BaseUnitEntity unit)
        {
            var profile = new WeaponRangeProfile();

            try
            {
                var primaryHand = unit.Body?.PrimaryHand;
                if (primaryHand?.HasWeapon != true)
                    return CreateDefaultProfile();

                var weapon = primaryHand.Weapon;
                bool isMelee = weapon.Blueprint.IsMelee;
                profile.IsMelee = isMelee;

                // ★ v3.111.4: Psyker 오감지 방지 — primaryHand가 melee staff여도
                //   실제 주공격이 Directional AoE(Cone/Ray/Sector) 사이킥이면 ranged 취급.
                //   증상: 카시아(Psyker Support) staff 착용 → melee 판정 → EffectiveRange=1,
                //         MinSafe=0 → AI가 1타일 밀착이 "최적"이라 착각.
                //   수정: melee 조기 return 전에 Cone/Ray/Sector 주공격 능력 탐지.
                var directionalAoE = TryFindDirectionalAoEPrimaryAttack(unit);
                if (directionalAoE.ability != null && directionalAoE.radius > 3f)
                {
                    profile.IsMelee = false;  // ranged 포지셔닝으로 전환
                    profile.HasDirectionalPattern = true;
                    profile.PatternRadius = directionalAoE.radius;
                    profile.MaxRange = directionalAoE.radius;
                    profile.OptimalRange = 0f;
                    // Cone/Ray는 시전자 위치에서 패턴 반경까지만 닿음
                    profile.EffectiveRange = directionalAoE.radius;
                    // Scatter 여부는 패턴 특성과 별개로 체크
                    profile.IsScatter = directionalAoE.ability.IsScatter;
                    if (profile.IsScatter)
                    {
                        profile.ClampedMinSafeDistance = 0f;
                    }
                    else
                    {
                        profile.ClampedMinSafeDistance = Math.Max(1f, profile.EffectiveRange * AUTO_MINSAFE_RATIO);
                    }
                    float maxAllowedRetreatDir = profile.EffectiveRange - 1f;
                    if (maxAllowedRetreatDir < 0f) maxAllowedRetreatDir = 0f;
                    profile.MaxRetreatDistance = maxAllowedRetreatDir;

                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] {unit.CharacterName}: Directional AoE primary attack " +
                        $"detected ({directionalAoE.ability.Name}, radius={directionalAoE.radius:F1}t) — " +
                        $"overriding melee=>ranged profile.");
                    return profile;
                }

                if (isMelee)
                {
                    // 근접 무기: 사거리 = AttackRange (보통 2 타일)
                    profile.MaxRange = weapon.AttackRange > 0 ? weapon.AttackRange : 2f;
                    profile.OptimalRange = 0f;
                    profile.EffectiveRange = profile.MaxRange;
                    profile.ClampedMinSafeDistance = 0f;
                    profile.MaxRetreatDistance = 0f;
                    return profile;
                }

                // 원거리 무기
                int attackRange = weapon.AttackRange;
                int optimalRange = weapon.AttackOptimalRange;

                profile.MaxRange = (attackRange > 0 && attackRange < 10000) ? attackRange : 15f;
                profile.OptimalRange = (optimalRange > 0 && optimalRange < 10000) ? optimalRange : 0f;

                // 기본 유효 사거리: OptimalRange > MaxRange 순서
                profile.EffectiveRange = profile.OptimalRange > 0 ? profile.OptimalRange : profile.MaxRange;

                // Scatter 체크 — 주 공격 능력에서 확인
                var primaryAttack = FindAnyAttackAbility(unit, Settings.RangePreference.PreferRanged);
                if (primaryAttack != null)
                {
                    profile.IsScatter = primaryAttack.IsScatter;

                    // 방향성 AoE 패턴 체크 (Cone/Ray/Sector)
                    var patternInfo = GetPatternInfo(primaryAttack);
                    if (patternInfo != null && patternInfo.IsValid && patternInfo.CanBeDirectional)
                    {
                        profile.HasDirectionalPattern = true;
                        profile.PatternRadius = patternInfo.Radius;
                        // ★ 핵심: 방향성 패턴의 유효 사거리 = 패턴 반경
                        // Cone/Ray는 시전자 위치에서 패턴 반경까지만 닿음
                        profile.EffectiveRange = patternInfo.Radius;
                    }
                }

                // Scatter 무기는 안전 거리 불필요 (자동 명중, 아군 피격 안 함)
                if (profile.IsScatter)
                {
                    profile.ClampedMinSafeDistance = 0f;
                    profile.MaxRetreatDistance = profile.EffectiveRange - 1f;
                    if (profile.MaxRetreatDistance < 0f) profile.MaxRetreatDistance = 0f;
                    return profile;
                }

                // ★ v3.110.11: 자동 MinSafeDistance 계산 (사용자 설정 제거)
                // 근거: EffectiveRange × 0.3 = 적절히 가까운 사격 거리
                //   볼터 단발 (MaxD 15) → 4.5타일
                //   볼터 Cone (PatternRadius 7) → 2.1타일 (Cone burst 영역 진입 가능)
                //   산탄총 (MaxD 5) → 1.5타일
                // 이전: 사용자 설정 7m 고정 → 무기 특성 무시, Cone 사용 봉쇄
                profile.ClampedMinSafeDistance = Math.Max(1f, profile.EffectiveRange * AUTO_MINSAFE_RATIO);

                // MaxRetreatDistance는 여전히 EffectiveRange - 1 (후퇴 상한)
                float maxAllowedRetreat = profile.EffectiveRange - 1f;
                if (maxAllowedRetreat < 0f) maxAllowedRetreat = 0f;
                profile.MaxRetreatDistance = maxAllowedRetreat;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[CombatAPI] CalculateWeaponRangeProfile error for {unit.CharacterName}: {ex.Message}");
                return CreateDefaultProfile();
            }

            return profile;
        }

        private static WeaponRangeProfile CreateDefaultProfile()
        {
            // ★ v3.110.11: 기본 프로필도 EffectiveRange × 0.3 규칙 적용
            const float DEFAULT_EFFECTIVE_RANGE = 15f;
            return new WeaponRangeProfile
            {
                MaxRange = DEFAULT_EFFECTIVE_RANGE,
                OptimalRange = 0f,
                EffectiveRange = DEFAULT_EFFECTIVE_RANGE,
                ClampedMinSafeDistance = Math.Max(1f, DEFAULT_EFFECTIVE_RANGE * AUTO_MINSAFE_RATIO),
                MaxRetreatDistance = DEFAULT_EFFECTIVE_RANGE - 1f,
            };
        }

        /// <summary>
        /// ★ v3.111.7: Directional AoE(Cone/Ray/Sector) 주공격 능력 직접 탐지.
        ///
        /// v3.111.4는 FindAnyAttackAbility 경유 → AbilityDatabase.IsReload → GetTiming →
        /// AutoDetectTiming → IsMultiTarget → CacheAbility → AbilityData.Name getter →
        /// LocalizedString.op_Implicit 사전존재 예외로 Psyker에서 항상 실패.
        ///
        /// v3.111.7 재구현: unit.Abilities.RawFacts 직접 iteration + 개별 ability try/catch.
        /// AbilityDatabase.*/BlueprintCache.* 호출 회피 (Name getter 예외 회피).
        /// Blueprint.CanTargetEnemies / Blueprint.IsMelee 만 사용 (localization 무관).
        /// 최대 반경 Directional 능력을 선택.
        ///
        /// 반환: (ability, radius[tile]) 튜플. 해당 없으면 (null, 0).
        /// </summary>
        private static (AbilityData ability, float radius) TryFindDirectionalAoEPrimaryAttack(BaseUnitEntity unit)
        {
            if (unit == null) return (null, 0f);

            AbilityData best = null;
            float bestRadius = 0f;

            try
            {
                // ★ v3.111.7: 기존 CombatAPI 패턴 그대로 (line 1759 참고) — AbilityDatabase 미경유
                var rawAbilities = unit.Abilities?.RawFacts;
                if (rawAbilities == null) return (null, 0f);

                foreach (var ability in rawAbilities)
                {
                    try
                    {
                        var abilityData = ability?.Data;
                        if (abilityData == null) continue;

                        var bp = abilityData.Blueprint;
                        if (bp == null) continue;

                        // 적 타겟 가능한 공격 능력만 (버프/힐/자기타겟 제외)
                        // Blueprint 프로퍼티는 LocalizedString 미경유 — 안전
                        if (!bp.CanTargetEnemies) continue;

                        // 명시적 melee 스킬 제외 (체인소드 sweep 등)
                        // ★ v3.111.7: IsMelee는 AbilityData 프로퍼티 (Weapon.Blueprint.IsMelee 래핑)
                        if (abilityData.IsMelee) continue;

                        // PatternInfo는 GetAoERadius/GetPatternType/AssetGuid 만 사용 — AbilityDatabase 미경유
                        var patternInfo = GetPatternInfo(abilityData);
                        if (patternInfo == null || !patternInfo.IsValid) continue;
                        if (!patternInfo.CanBeDirectional) continue;
                        if (patternInfo.Radius <= 3f) continue;

                        // 최대 반경 능력 선택 (복수 Directional 능력이 있을 경우 주무기 선호)
                        if (patternInfo.Radius > bestRadius)
                        {
                            best = abilityData;
                            bestRadius = patternInfo.Radius;
                        }
                    }
                    catch
                    {
                        // ★ v3.111.7: 개별 ability 처리 실패 → 다음으로 (안전 스킵)
                        // LocalizedString 예외는 특정 능력에서만 발생하므로 격리 필요
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] TryFindDirectionalAoEPrimaryAttack iteration failed for {unit?.CharacterName}: {ex.Message}");
                return (null, 0f);
            }

            return (best, bestRadius);
        }

        /// <summary>
        /// ★ v3.9.24: 무기 사거리 캐시 클리어 (턴 시작 시 CombatCache.ClearAll()에서 호출)
        /// </summary>
        public static void ClearWeaponRangeCache()
        {
            _weaponRangeCache.Clear();
        }

        #endregion
    }
}
