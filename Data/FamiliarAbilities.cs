using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.Blueprints;
using Kingmaker.Enums;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Mechanics.Actions;
using Kingmaker.Mechanics.Actions;
using CompanionAI_v3.GameInterface;

namespace CompanionAI_v3.Data
{
    /// <summary>
    /// ★ v3.7.00: 사역마 능력 데이터베이스
    /// - GUID/BlueprintName 기반 능력 식별 (키워드 매칭 금지)
    /// - 각 사역마 타입별 키스톤 능력 식별
    /// </summary>
    public static class FamiliarAbilities
    {
        #region Known GUIDs

        // ========================================
        // Servo-Skull Swarm 능력 GUID (Master에서 사용)
        // ========================================
        private static readonly HashSet<string> ServoSkullAbilityGuids = new()
        {
            "33aa1b047d084a9b8faf534767a3a534",  // ServoskullPet_PrioritySignal_Ability
            "5376c2d18af1499db985fbde6d5fe1ce",  // ServoskullPet_Redirect_Ability
            "62eeb81743734fc5b8fac71b34b14683",  // ServoskullPet_VitalitySignal_Ability
            "517b1451079843a2b88df9e52ea66a96",  // ServoskullPet_Redirect_SupportAbility (Pet에서)
        };

        // ========================================
        // Psyber-Raven 능력 GUID (Master에서 사용)
        // ========================================
        private static readonly HashSet<string> PsyberRavenAbilityGuids = new()
        {
            "7962a71f962946258a64df9cd4b7c36a",  // RavenPet_Concentrate_Ability (Warp Relay)
            "5f6ef4bf13754f7285ac540bfd2ee80f",  // RavenPet_Redirect_Ability
            "1300cf8d118b4910a4f93770ffa1b827",  // RavenPet_Redirect_Support_Ability (Pet에서)
            "319b06b3dbad4d47ae1dff5c1647f904",  // RavenPet_Hex_Ability
        };

        // ========================================
        // Cyber-Mastiff 능력 GUID (Master에서 사용)
        // ========================================
        private static readonly HashSet<string> CyberMastiffAbilityGuids = new()
        {
            "3311f1213a1e452c874ce2cde5c03f59",  // MastiffPet_Apprehend_Ability
            "05a10418b81049198fb5d792ca346f2f",  // MastiffPet_Fast_Ability
            "78e7771b7c974111887e762d5e999a11",  // MastiffPet_Protect_Ability
            "b642b445adc04242ba616c3e98125972",  // MastiffPet_Roam_Ability
            // Pet에서 사용되는 능력
            "5e3d39466761499389e5e4cbadb3d26c",  // Master_ArbitesCyberMastiff_ApprehendBite_Ability
            "baaf91661f904a1d821996d1c8704d87",  // Master_ArbitesCyberMastiff_Claws_Ability
            "7e363d7d17164736a6c4de8167ef9238",  // Master_ArbitesCyberMastiff_JumpClaws_Ability
            "0d91a5167a914e33a09ec1ec359dfa4e",  // Master_ArbitesCyberMastiff_ProtectClaws_Ability
        };

        // ========================================
        // Cyber-Eagle 능력 GUID (Master에서 사용)
        // ========================================
        private static readonly HashSet<string> CyberEagleAbilityGuids = new()
        {
            "95c502e72a6743d1ad0cbadf13051225",  // EaglePet_AerialRush_Ability
            "40bc0f9310894ee2ace0d60daafa3c19",  // EaglePet_BlindingDive_Ability
            "9cea4a0c57c448f7a90c44998745bf96",  // EaglePet_ObstructVision_Ability
            "ce847cb6b6074a528eaf4cc4479d4db0",  // EaglePet_Screen_Ability
            // Pet에서 사용되는 능력
            "92e3e63c740e4bcfac0f392246766acb",  // EaglePet_Ascend_Support_Ability
            "0836ac67f4084babbab15bc42f20a016",  // Master_CyberEagle_Claws_Ability
        };

        #endregion

        #region Known BlueprintNames (게임 로그에서 확인된 이름)

        // ========================================
        // Relocate 능력 BlueprintName
        // ========================================
        private static readonly HashSet<string> RelocateBlueprintNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "ServoskullPet_Redirect_Ability",
            "ServoskullPet_Redirect_SupportAbility",
            "RavenPet_Redirect_Ability",
            "RavenPet_Redirect_Support_Ability",
            "EaglePet_Ascend_Support_Ability",  // Eagle의 이동 능력
        };

        // ========================================
        // Relocate 능력 GUID
        // ========================================
        private static readonly HashSet<string> RelocateGuids = new()
        {
            "5376c2d18af1499db985fbde6d5fe1ce",  // ServoskullPet_Redirect_Ability
            "517b1451079843a2b88df9e52ea66a96",  // ServoskullPet_Redirect_SupportAbility
            "5f6ef4bf13754f7285ac540bfd2ee80f",  // RavenPet_Redirect_Ability
            "1300cf8d118b4910a4f93770ffa1b827",  // RavenPet_Redirect_Support_Ability
            "92e3e63c740e4bcfac0f392246766acb",  // EaglePet_Ascend_Support_Ability
        };

        // ========================================
        // ★ v3.8.02: 비피해 디버프 GUID (Warp Relay 확산 가능, Momentum 불필요)
        // 이 능력들은 피해를 주지 않는 순수 디버프
        // ========================================
        private static readonly HashSet<string> NonDamagingDebuffGuids = new()
        {
            "8c86972daac142fea33bb3bc5c84396c",  // 감각 박탈 (Sensory Deprivation) - 실명 디버프
        };

        // ========================================
        // ★ v3.8.02: 피해 능력 GUID (CanTargetFriends=True여도 피해 능력)
        // 아군/적 모두 타겟 가능하지만 실제로는 피해를 주는 능력
        // ========================================
        private static readonly HashSet<string> DamageDealingAbilityGuids = new()
        {
            "a2cca43669184eaa9f0da981f204e1c9",  // 점화 (Ignition) - 화염 피해
        };

        // ========================================
        // Medicae Signal 능력
        // ========================================
        private static readonly HashSet<string> MedicaeBlueprintNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "ServoskullPet_VitalitySignal_Ability",
            "ServoskullPet_VitalitySignal_BuffAlly",
            "VitalitySignal",
        };

        private static readonly HashSet<string> MedicaeGuids = new()
        {
            "62eeb81743734fc5b8fac71b34b14683",  // VitalitySignal
            "b535edd9eba84b3bbb38bcc399d48718",  // ServoskullPet_VitalitySignal_BuffAlly
        };

        // ========================================
        // Extrapolation 능력 (Servo-Skull 키스톤)
        // ========================================
        private static readonly HashSet<string> ExtrapolationBlueprintNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "ServoskullPet_Expand_Ability",
            "Expand",
        };

        private static readonly HashSet<string> ExtrapolationGuids = new()
        {
            "d68b6efac32b4db7afaf7de694eab819",  // Expand (Extrapolation)
        };

        // ========================================
        // Warp Relay 능력 (Psyber-Raven 키스톤)
        // ========================================
        private static readonly HashSet<string> WarpRelayBlueprintNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "RavenPet_Concentrate_Ability",
            "RavenPet_Concentrate_Support_Ability",
        };

        private static readonly HashSet<string> WarpRelayGuids = new()
        {
            "7962a71f962946258a64df9cd4b7c36a",  // RavenPet_Concentrate_Ability
        };

        // ========================================
        // Complete the Cycle 능력 (Raven)
        // ========================================
        private static readonly HashSet<string> CycleBlueprintNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "RavenPet_Cycle_Ability",
        };

        // ========================================
        // Apprehend 능력 (Cyber-Mastiff)
        // ========================================
        private static readonly HashSet<string> ApprehendBlueprintNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "MastiffPet_Apprehend_Ability",
            "MastiffPet_Apprehend",
        };

        private static readonly HashSet<string> ApprehendGuids = new()
        {
            "3311f1213a1e452c874ce2cde5c03f59",  // MastiffPet_Apprehend_Ability
            "5e3d39466761499389e5e4cbadb3d26c",  // Master_ArbitesCyberMastiff_ApprehendBite_Ability (Pet)
        };

        // ========================================
        // Fast 능력 (Cyber-Mastiff)
        // ========================================
        private static readonly HashSet<string> FastBlueprintNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "MastiffPet_Fast_Ability",
        };

        private static readonly HashSet<string> FastGuids = new()
        {
            "05a10418b81049198fb5d792ca346f2f",  // MastiffPet_Fast_Ability
        };

        // ========================================
        // Roam 능력 (Cyber-Mastiff)
        // ========================================
        private static readonly HashSet<string> RoamBlueprintNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "MastiffPet_Roam_Ability",
        };

        private static readonly HashSet<string> RoamGuids = new()
        {
            "b642b445adc04242ba616c3e98125972",  // MastiffPet_Roam_Ability
        };

        // ========================================
        // Protect! 능력 (Cyber-Mastiff)
        // ========================================
        private static readonly HashSet<string> ProtectBlueprintNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "MastiffPet_Protect_Ability",
            "MastiffPet_Protect",
            "Master_ArbitesCyberMastiff_ProtectClaws_Ability",
        };

        private static readonly HashSet<string> ProtectGuids = new()
        {
            "78e7771b7c974111887e762d5e999a11",  // MastiffPet_Protect_Ability
            "0d91a5167a914e33a09ec1ec359dfa4e",  // Master_ArbitesCyberMastiff_ProtectClaws_Ability (Pet)
        };

        // ========================================
        // Obstruct Vision 능력 (Cyber-Eagle)
        // ========================================
        private static readonly HashSet<string> ObstructVisionBlueprintNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "EaglePet_ObstructVision_Ability",
            "EaglePet_Obstruct_Ability",
        };

        private static readonly HashSet<string> ObstructVisionGuids = new()
        {
            "9cea4a0c57c448f7a90c44998745bf96",  // EaglePet_ObstructVision_Ability
            "e985b758fa94464abfd506c773409571",  // ★ v3.7.31: 시야 방해 — 활공 (MultiTarget Glide 버전)
        };

        // ========================================
        // Aerial Rush 능력 (Cyber-Eagle)
        // ========================================
        private static readonly HashSet<string> AerialRushBlueprintNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "EaglePet_AerialRush_Ability",
            "EaglePet_AerialRush_Ascended_Ability",  // ★ v3.7.25: Ascended 버전 추가
        };

        private static readonly HashSet<string> AerialRushGuids = new()
        {
            "95c502e72a6743d1ad0cbadf13051225",  // EaglePet_AerialRush_Ability
            "d830b9fd0e7240139d3f7381fa308ab7",  // ★ v3.7.25: EaglePet_AerialRush_Ascended_Ability
        };

        // ========================================
        // ★ v3.7.30: Aerial Rush Support 능력 (Cyber-Eagle)
        // 실명 공격 — 활공 (Blinding Glide)
        // ========================================
        private static readonly HashSet<string> AerialRushSupportBlueprintNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "EaglePet_AerialRush_Support_Ability",
            "EaglePet_AerialRush_Support_Ascended_Ability",
        };

        // ★ v3.7.31: AerialRush Support GUIDs 추가
        private static readonly HashSet<string> AerialRushSupportGuids = new()
        {
            "31e321c9332449c6a8531cb652b7290b",  // 실명 공격 — 활공 (Blinding Glide)
        };

        // ========================================
        // Blinding Dive / Blinding Strike 능력 (Cyber-Eagle)
        // ========================================
        private static readonly HashSet<string> BlindingDiveBlueprintNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "EaglePet_BlindingDive_Ability",
        };

        private static readonly HashSet<string> BlindingDiveGuids = new()
        {
            "40bc0f9310894ee2ace0d60daafa3c19",  // EaglePet_BlindingDive_Ability
        };

        // Alias for backwards compatibility
        private static readonly HashSet<string> BlindingStrikeBlueprintNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "EaglePet_BlindingDive_Ability",
            "EaglePet_BlindingStrike_Ability",
            "EaglePet_Blinding_Ability",
        };

        private static readonly HashSet<string> BlindingStrikeGuids = new()
        {
            "40bc0f9310894ee2ace0d60daafa3c19",  // EaglePet_BlindingDive_Ability
        };

        // ========================================
        // Screen 능력 (Cyber-Eagle)
        // ========================================
        private static readonly HashSet<string> ScreenBlueprintNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "EaglePet_Screen_Ability",
        };

        private static readonly HashSet<string> ScreenGuids = new()
        {
            "ce847cb6b6074a528eaf4cc4479d4db0",  // EaglePet_Screen_Ability
        };

        // ========================================
        // ★ v3.7.14: Jump Claws 능력 (Cyber-Mastiff)
        // 점프/돌진 + 클로우 멀티히트 공격
        // ========================================
        private static readonly HashSet<string> JumpClawsBlueprintNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Master_ArbitesCyberMastiff_JumpClaws_Ability",
        };

        private static readonly HashSet<string> JumpClawsGuids = new()
        {
            "7e363d7d17164736a6c4de8167ef9238",  // Master_ArbitesCyberMastiff_JumpClaws_Ability
        };

        // ========================================
        // ★ v3.7.14: Claws 능력 (Cyber-Mastiff) - 순수 근접
        // ========================================
        private static readonly HashSet<string> MastiffClawsBlueprintNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Master_ArbitesCyberMastiff_Claws_Ability",
        };

        private static readonly HashSet<string> MastiffClawsGuids = new()
        {
            "baaf91661f904a1d821996d1c8704d87",  // Master_ArbitesCyberMastiff_Claws_Ability
        };

        // ========================================
        // ★ v3.7.14: Claws 능력 (Cyber-Eagle) - 순수 근접
        // ========================================
        private static readonly HashSet<string> EagleClawsBlueprintNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Master_CyberEagle_Claws_Ability",
        };

        private static readonly HashSet<string> EagleClawsGuids = new()
        {
            "0836ac67f4084babbab15bc42f20a016",  // Master_CyberEagle_Claws_Ability
        };

        // ========================================
        // Concentrate 능력 (Psyber-Raven - Warp Relay)
        // ========================================
        private static readonly HashSet<string> ConcentrateBlueprintNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "RavenPet_Concentrate_Ability",
        };

        private static readonly HashSet<string> ConcentrateGuids = new()
        {
            "7962a71f962946258a64df9cd4b7c36a",  // RavenPet_Concentrate_Ability
        };

        // ========================================
        // Hex 능력 (Psyber-Raven)
        // ========================================
        private static readonly HashSet<string> HexBlueprintNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "RavenPet_Hex_Ability",
        };

        private static readonly HashSet<string> HexGuids = new()
        {
            "319b06b3dbad4d47ae1dff5c1647f904",  // RavenPet_Hex_Ability
        };

        #endregion

        #region Common Abilities

        /// <summary>
        /// Relocate 능력인지 확인 (GUID/BlueprintName 기반)
        /// </summary>
        public static bool IsRelocateAbility(AbilityData ability)
        {
            return IsRelocateAbility(ability, null);
        }

        /// <summary>
        /// Relocate 능력인지 확인 (GUID/BlueprintName 기반)
        /// ★ v3.7.02: PetType 파라미터 추가 - Mastiff는 Relocate 없음
        /// </summary>
        public static bool IsRelocateAbility(AbilityData ability, PetType? familiarType)
        {
            if (ability == null) return false;

            // ★ v3.7.02: Mastiff는 Relocate 능력이 없음
            if (familiarType == PetType.Mastiff)
                return false;

            try
            {
                // 1. GUID 체크
                string guid = AbilityDatabase.GetGuid(ability);
                if (!string.IsNullOrEmpty(guid) && RelocateGuids.Contains(guid))
                    return true;

                // 2. BlueprintName 체크
                string blueprintName = ability.Blueprint?.name;
                if (!string.IsNullOrEmpty(blueprintName) && RelocateBlueprintNames.Contains(blueprintName))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Reactivate 능력인지 확인 (사역마 부활)
        /// ★ v3.8.59: GUID 우선 체크 + Blueprint 이름 폴백 (GUID 자동 등록)
        /// </summary>
        private static string _reactivateGuid;
        public static bool IsReactivateAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                string guid = AbilityDatabase.GetGuid(ability);

                // GUID 캐시 히트
                if (!string.IsNullOrEmpty(_reactivateGuid) && guid == _reactivateGuid)
                    return true;

                // Blueprint 이름으로 확인 후 GUID 자동 등록
                string blueprintName = ability.Blueprint?.name;
                if (string.IsNullOrEmpty(blueprintName)) return false;

                if (blueprintName.Equals("Reactivate_Ability", StringComparison.OrdinalIgnoreCase) ||
                    blueprintName.Equals("Pet_Reactivate_Ability", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(guid) && _reactivateGuid == null)
                    {
                        _reactivateGuid = guid;
                        Main.LogDebug($"[FamiliarAbilities] Reactivate GUID registered: {guid}");
                    }
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Servo-Skull Swarm Abilities

        /// <summary>
        /// Extrapolation 대상이 될 수 있는 능력인지 확인
        /// 조건: 비공격, 비사이킥, 단일 대상 버프/디버프
        /// </summary>
        public static bool IsExtrapolationTarget(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                var blueprint = ability.Blueprint;
                if (blueprint == null) return false;

                // 1. 공격 능력 제외
                if (IsAttackAbility(ability)) return false;

                // 2. 사이킥 능력 제외 (Servo-Skull은 비사이킥만)
                if (IsPsychicAbility(ability)) return false;

                // 3. 자기 자신만 타겟 가능한 능력 제외
                if (blueprint.CanTargetSelf && !blueprint.CanTargetFriends && !blueprint.CanTargetEnemies)
                    return false;

                // 4. 단일 대상 (Point 타겟 제외)
                if (blueprint.CanTargetPoint && !blueprint.CanTargetFriends && !blueprint.CanTargetEnemies)
                    return false;

                // 5. 아군 또는 적 타겟 가능해야 함
                if (!blueprint.CanTargetFriends && !blueprint.CanTargetEnemies)
                    return false;

                // 6. 추가 턴 부여 능력 제외 (가이드 명시)
                if (IsExtraTurnAbility(ability)) return false;

                Main.LogDebug($"[FamiliarAbilities] {ability.Name}: Valid Extrapolation target");
                return true;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[FamiliarAbilities] IsExtrapolationTarget error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Medicae Signal 능력인지 확인
        /// </summary>
        public static bool IsMedicaeSignal(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                // 1. GUID 체크
                string guid = AbilityDatabase.GetGuid(ability);
                if (!string.IsNullOrEmpty(guid) && MedicaeGuids.Contains(guid))
                    return true;

                // 2. BlueprintName 체크
                string blueprintName = ability.Blueprint?.name;
                if (!string.IsNullOrEmpty(blueprintName) && MedicaeBlueprintNames.Contains(blueprintName))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Psyber-Raven Abilities

        /// <summary>
        /// Warp Relay 대상이 될 수 있는 능력인지 확인
        /// 조건: 비공격, 사이킥, 단일 대상 버프/디버프
        /// ★ v3.7.09: 비피해 디버프 (감각 박탈 등)도 포함
        /// </summary>
        public static bool IsWarpRelayTarget(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                var blueprint = ability.Blueprint;
                if (blueprint == null) return false;

                // 1. 사이킥 능력이어야 함 (Psyber-Raven은 사이킥만)
                if (!IsPsychicAbility(ability)) return false;

                // 2. ★ v3.7.09: 피해를 주는 공격 능력만 제외 (디버프는 포함)
                // - 무기 공격: 제외
                // - 직접 피해 사이킥 (점화 등): 제외
                // - 비피해 디버프 (감각 박탈 등): 포함
                if (IsDamageDealingAbility(ability)) return false;

                // 3. 자기 자신만 타겟 가능한 능력 제외
                if (blueprint.CanTargetSelf && !blueprint.CanTargetFriends && !blueprint.CanTargetEnemies)
                    return false;

                // 4. 아군 또는 적 타겟 가능해야 함
                if (!blueprint.CanTargetFriends && !blueprint.CanTargetEnemies)
                    return false;

                // 5. 추가 턴 부여 능력 제외 (가이드 명시)
                if (IsExtraTurnAbility(ability)) return false;

                // 6. ★ v3.7.74: Point Target 능력 제외
                // Warp Relay는 유닛 타겟 능력만 확산 가능
                // 황제의 말씀 등 Point AOE 버프는 직접 위치에 캐스트해야 함
                if (blueprint.CanTargetPoint && !blueprint.CanTargetEnemies)
                {
                    // Point AOE 버프 (아군 타겟은 AOE 범위 효과일 뿐)
                    Main.LogDebug($"[FamiliarAbilities] {ability.Name}: Point AOE - not Warp Relay target");
                    return false;
                }

                Main.LogDebug($"[FamiliarAbilities] {ability.Name}: Valid Warp Relay target (Enemy={blueprint.CanTargetEnemies}, Friend={blueprint.CanTargetFriends})");
                return true;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[FamiliarAbilities] IsWarpRelayTarget error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Purification Discharge 능력인지 확인
        /// </summary>
        /// ★ v3.8.59: GUID 우선 체크 + Blueprint 이름 폴백 (GUID 자동 등록)
        private static string _purificationDischargeGuid;
        public static bool IsPurificationDischarge(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                string guid = AbilityDatabase.GetGuid(ability);

                if (!string.IsNullOrEmpty(_purificationDischargeGuid) && guid == _purificationDischargeGuid)
                    return true;

                string blueprintName = ability.Blueprint?.name;
                if (string.IsNullOrEmpty(blueprintName)) return false;

                if (blueprintName.Equals("RavenPet_PurificationDischarge_Ability", StringComparison.OrdinalIgnoreCase) ||
                    blueprintName.Equals("PurificationDischarge", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(guid) && _purificationDischargeGuid == null)
                    {
                        _purificationDischargeGuid = guid;
                        Main.LogDebug($"[FamiliarAbilities] PurificationDischarge GUID registered: {guid}");
                    }
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Cyber-Mastiff Abilities

        /// <summary>
        /// Apprehend 능력인지 확인
        /// ★ v3.7.01: GUID 체크 추가
        /// </summary>
        public static bool IsApprehendAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                // 1. GUID 체크
                string guid = AbilityDatabase.GetGuid(ability);
                if (!string.IsNullOrEmpty(guid) && ApprehendGuids.Contains(guid))
                    return true;

                // 2. BlueprintName 체크
                string blueprintName = ability.Blueprint?.name;
                if (!string.IsNullOrEmpty(blueprintName) && ApprehendBlueprintNames.Contains(blueprintName))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Protect! 능력인지 확인
        /// ★ v3.7.01: GUID 체크 추가
        /// </summary>
        public static bool IsProtectAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                // 1. GUID 체크
                string guid = AbilityDatabase.GetGuid(ability);
                if (!string.IsNullOrEmpty(guid) && ProtectGuids.Contains(guid))
                    return true;

                // 2. BlueprintName 체크
                string blueprintName = ability.Blueprint?.name;
                if (!string.IsNullOrEmpty(blueprintName) && ProtectBlueprintNames.Contains(blueprintName))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Fast 능력인지 확인 (Cyber-Mastiff)
        /// </summary>
        public static bool IsFastAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                // 1. GUID 체크
                string guid = AbilityDatabase.GetGuid(ability);
                if (!string.IsNullOrEmpty(guid) && FastGuids.Contains(guid))
                    return true;

                // 2. BlueprintName 체크
                string blueprintName = ability.Blueprint?.name;
                if (!string.IsNullOrEmpty(blueprintName) && FastBlueprintNames.Contains(blueprintName))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Roam 능력인지 확인 (Cyber-Mastiff)
        /// </summary>
        public static bool IsRoamAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                // 1. GUID 체크
                string guid = AbilityDatabase.GetGuid(ability);
                if (!string.IsNullOrEmpty(guid) && RoamGuids.Contains(guid))
                    return true;

                // 2. BlueprintName 체크
                string blueprintName = ability.Blueprint?.name;
                if (!string.IsNullOrEmpty(blueprintName) && RoamBlueprintNames.Contains(blueprintName))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Cyber-Eagle Abilities

        /// <summary>
        /// Obstruct Vision 능력인지 확인
        /// ★ v3.7.01: GUID 체크 추가
        /// </summary>
        public static bool IsObstructVisionAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                // 1. GUID 체크
                string guid = AbilityDatabase.GetGuid(ability);
                if (!string.IsNullOrEmpty(guid) && ObstructVisionGuids.Contains(guid))
                    return true;

                // 2. BlueprintName 체크
                string blueprintName = ability.Blueprint?.name;
                if (!string.IsNullOrEmpty(blueprintName) && ObstructVisionBlueprintNames.Contains(blueprintName))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Blinding Strike/Dive 능력인지 확인
        /// ★ v3.7.01: GUID 체크 추가
        /// </summary>
        public static bool IsBlindingStrikeAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                // 1. GUID 체크
                string guid = AbilityDatabase.GetGuid(ability);
                if (!string.IsNullOrEmpty(guid) && BlindingDiveGuids.Contains(guid))
                    return true;

                // 2. BlueprintName 체크
                string blueprintName = ability.Blueprint?.name;
                if (!string.IsNullOrEmpty(blueprintName) && BlindingStrikeBlueprintNames.Contains(blueprintName))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Aerial Rush 능력인지 확인 (Cyber-Eagle)
        /// </summary>
        public static bool IsAerialRushAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                // 1. GUID 체크
                string guid = AbilityDatabase.GetGuid(ability);
                if (!string.IsNullOrEmpty(guid) && AerialRushGuids.Contains(guid))
                    return true;

                // 2. BlueprintName 체크
                string blueprintName = ability.Blueprint?.name;
                if (!string.IsNullOrEmpty(blueprintName) && AerialRushBlueprintNames.Contains(blueprintName))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// ★ v3.7.30: Aerial Rush Support 능력인지 확인 (실명 공격 — 활공)
        /// AerialRush와 동일하게 2개 Point 타겟 필요
        /// </summary>
        public static bool IsAerialRushSupportAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                // ★ v3.7.31: GUID 체크 추가
                string guid = AbilityDatabase.GetGuid(ability);
                if (!string.IsNullOrEmpty(guid) && AerialRushSupportGuids.Contains(guid))
                    return true;

                // BlueprintName 체크
                string blueprintName = ability.Blueprint?.name;
                if (!string.IsNullOrEmpty(blueprintName) && AerialRushSupportBlueprintNames.Contains(blueprintName))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// ★ v3.7.27: MultiTarget 사역마 능력인지 확인
        /// 이 능력들은 AvailableAttacks가 아닌 FamiliarAbilities에서만 처리됨
        /// AttackPlanner/MovementPlanner에서 필터링할 때 사용
        /// </summary>
        public static bool IsMultiTargetFamiliarAbility(AbilityData ability)
        {
            if (ability == null) return false;

            // AerialRush는 2개 Point 타겟 필요 (시작점, 끝점)
            if (IsAerialRushAbility(ability)) return true;

            // ★ v3.7.30: AerialRush Support도 2개 Point 타겟 필요
            if (IsAerialRushSupportAbility(ability)) return true;

            // ★ v3.7.51: ObstructVision Glide (MultiTarget 버전)
            string guid = AbilityDatabase.GetGuid(ability);
            if (guid == "e985b758fa94464abfd506c773409571")
                return true;

            return false;
        }

        /// <summary>
        /// Screen 능력인지 확인 (Cyber-Eagle)
        /// </summary>
        public static bool IsScreenAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                // 1. GUID 체크
                string guid = AbilityDatabase.GetGuid(ability);
                if (!string.IsNullOrEmpty(guid) && ScreenGuids.Contains(guid))
                    return true;

                // 2. BlueprintName 체크
                string blueprintName = ability.Blueprint?.name;
                if (!string.IsNullOrEmpty(blueprintName) && ScreenBlueprintNames.Contains(blueprintName))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// ★ v3.7.14: Blinding Dive 능력인지 확인 (Cyber-Eagle movement+attack)
        /// IsBlindingStrikeAbility()와 같은 GUID이지만, 공격 계획용으로 명시적 분리
        /// AbilityCustomDirectMovement 패턴: StepThroughTarget=true, StopOnFirstEncounter=true
        /// </summary>
        public static bool IsBlindingDiveAbility(AbilityData ability)
        {
            // BlindingStrike와 동일 GUID - 명시적으로 분리 (계획 로직에서 용도 구분)
            return IsBlindingStrikeAbility(ability);
        }

        /// <summary>
        /// ★ v3.7.14: Jump Claws 능력인지 확인 (Cyber-Mastiff)
        /// 점프/돌진 + 클로우 멀티히트 공격
        /// </summary>
        public static bool IsJumpClawsAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                // 1. GUID 체크
                string guid = AbilityDatabase.GetGuid(ability);
                if (!string.IsNullOrEmpty(guid) && JumpClawsGuids.Contains(guid))
                    return true;

                // 2. BlueprintName 체크
                string blueprintName = ability.Blueprint?.name;
                if (!string.IsNullOrEmpty(blueprintName) && JumpClawsBlueprintNames.Contains(blueprintName))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// ★ v3.7.14: Mastiff Claws 능력인지 확인 (순수 근접 공격)
        /// </summary>
        public static bool IsMastiffClawsAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                // 1. GUID 체크
                string guid = AbilityDatabase.GetGuid(ability);
                if (!string.IsNullOrEmpty(guid) && MastiffClawsGuids.Contains(guid))
                    return true;

                // 2. BlueprintName 체크
                string blueprintName = ability.Blueprint?.name;
                if (!string.IsNullOrEmpty(blueprintName) && MastiffClawsBlueprintNames.Contains(blueprintName))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// ★ v3.7.14: Eagle Claws 능력인지 확인 (순수 근접 공격)
        /// </summary>
        public static bool IsEagleClawsAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                // 1. GUID 체크
                string guid = AbilityDatabase.GetGuid(ability);
                if (!string.IsNullOrEmpty(guid) && EagleClawsGuids.Contains(guid))
                    return true;

                // 2. BlueprintName 체크
                string blueprintName = ability.Blueprint?.name;
                if (!string.IsNullOrEmpty(blueprintName) && EagleClawsBlueprintNames.Contains(blueprintName))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// ★ v3.7.14: Claws 능력인지 통합 확인 (Eagle 또는 Mastiff)
        /// </summary>
        public static bool IsClawsAbility(AbilityData ability, PetType? familiarType = null)
        {
            if (ability == null) return false;

            if (familiarType.HasValue)
            {
                return familiarType.Value switch
                {
                    PetType.Mastiff => IsMastiffClawsAbility(ability),
                    PetType.Eagle => IsEagleClawsAbility(ability),
                    _ => false
                };
            }

            // 타입 미지정 시 둘 다 체크
            return IsMastiffClawsAbility(ability) || IsEagleClawsAbility(ability);
        }

        #endregion

        #region Servo-Skull Signal Abilities

        /// <summary>
        /// Priority Signal 능력인지 확인 (Servo-Skull)
        /// </summary>
        public static bool IsPrioritySignal(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                // ★ v3.8.59: GUID 전용 (string 폴백 제거)
                string guid = AbilityDatabase.GetGuid(ability);
                return guid == "33aa1b047d084a9b8faf534767a3a534";
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Vitality Signal 능력인지 확인 (Servo-Skull)
        /// </summary>
        public static bool IsVitalitySignal(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                // ★ v3.8.59: GUID 전용 (string 폴백 제거)
                string guid = AbilityDatabase.GetGuid(ability);
                return guid == "62eeb81743734fc5b8fac71b34b14683";
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Psyber-Raven Abilities

        /// <summary>
        /// Concentrate 능력인지 확인 (Psyber-Raven - Warp Relay)
        /// </summary>
        public static bool IsConcentrateAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                // GUID 체크
                string guid = AbilityDatabase.GetGuid(ability);
                if (!string.IsNullOrEmpty(guid) && ConcentrateGuids.Contains(guid))
                    return true;

                // BlueprintName 체크
                string bpName = ability.Blueprint?.name;
                if (!string.IsNullOrEmpty(bpName) && ConcentrateBlueprintNames.Contains(bpName))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Hex 능력인지 확인 (Psyber-Raven)
        /// </summary>
        public static bool IsHexAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                // GUID 체크
                string guid = AbilityDatabase.GetGuid(ability);
                if (!string.IsNullOrEmpty(guid) && HexGuids.Contains(guid))
                    return true;

                // BlueprintName 체크
                string bpName = ability.Blueprint?.name;
                if (!string.IsNullOrEmpty(bpName) && HexBlueprintNames.Contains(bpName))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// ★ v3.7.12: Cycle (Complete the Cycle) 능력인지 확인 (Psyber-Raven)
        /// Warp Relay로 확산된 사이킥 재시전
        /// </summary>
        public static bool IsCycleAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                // BlueprintName 체크 (GUID 미확인)
                string bpName = ability.Blueprint?.name;
                if (!string.IsNullOrEmpty(bpName) && CycleBlueprintNames.Contains(bpName))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// ★ v3.7.69: Raven 공격 능력인지 확인
        /// - PurificationDischarge 등 Overcharge 필요 능력
        /// - 이 능력들은 Overcharge 없이 사용하면 Raven에게 자해 데미지
        /// </summary>
        public static bool IsRavenAttackAbility(AbilityData ability)
        {
            if (ability == null) return false;

            // PurificationDischarge는 Raven 공격 능력
            if (IsPurificationDischarge(ability)) return true;

            // 추후 다른 Raven 공격 능력이 추가되면 여기에 추가
            // (현재 게임에서 PurificationDischarge가 유일한 Raven 공격 능력)

            return false;
        }

        #endregion

        #region Keystone Detection

        /// <summary>
        /// 사역마 타입별 키스톤 능력인지 확인
        /// </summary>
        public static bool IsKeystoneAbility(AbilityData ability, PetType type)
        {
            if (ability == null) return false;

            return type switch
            {
                PetType.ServoskullSwarm => IsExtrapolationKeystone(ability),
                PetType.Raven => IsWarpRelayKeystone(ability),
                PetType.Mastiff => IsApprehendAbility(ability),
                PetType.Eagle => IsObstructVisionAbility(ability),
                _ => false
            };
        }

        private static bool IsExtrapolationKeystone(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                // 1. GUID 체크
                string guid = AbilityDatabase.GetGuid(ability);
                if (!string.IsNullOrEmpty(guid) && ExtrapolationGuids.Contains(guid))
                    return true;

                // 2. BlueprintName 체크
                string blueprintName = ability.Blueprint?.name;
                if (!string.IsNullOrEmpty(blueprintName) && ExtrapolationBlueprintNames.Contains(blueprintName))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsWarpRelayKeystone(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                string blueprintName = ability.Blueprint?.name;
                if (!string.IsNullOrEmpty(blueprintName) && WarpRelayBlueprintNames.Contains(blueprintName))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// 공격 능력인지 확인
        /// </summary>
        private static bool IsAttackAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                var blueprint = ability.Blueprint;
                if (blueprint == null) return false;

                // 무기 공격
                if (ability.Weapon != null) return true;

                // 간단한 휴리스틱: CanTargetEnemies && !CanTargetFriends && Harmful이면 공격
                if (blueprint.CanTargetEnemies && !blueprint.CanTargetFriends &&
                    blueprint.EffectOnEnemy == AbilityEffectOnUnit.Harmful)
                {
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// ★ v3.7.65: 피해를 주는 능력인지 확인 (게임 API 기반 - 키워드 매칭 제거)
        /// - 무기 공격: true
        /// - 직접 피해 컴포넌트 보유: true
        /// - Harmful + 적만 타겟 + AoEDamage: true
        /// ★ v3.7.79: Point AOE + 적 타겟 능력 감지 강화 (사이킥 비명 등)
        /// ★ v3.8.05: 중첩된 Actions 내부 ContextActionDealDamage 검색 (JBP 분석 기반)
        /// </summary>
        private static bool IsDamageDealingAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                var blueprint = ability.Blueprint;
                if (blueprint == null) return false;

                // ★ v3.8.02: GUID 기반 명시적 분류 (최우선 - 블루프린트 분석으로 확인된 분류)
                // 게임 데이터 자체가 일관성 없으므로 (NotOffensive, EffectOnEnemy 신뢰 불가)
                // 검증된 GUID 화이트리스트가 가장 신뢰할 수 있음
                string guid = blueprint.AssetGuid?.ToString();
                if (!string.IsNullOrEmpty(guid))
                {
                    // 명시적 피해 능력 (CanTargetFriends=True여도 피해)
                    if (DamageDealingAbilityGuids.Contains(guid))
                    {
                        Main.LogDebug($"[FamiliarAbilities] {ability.Name}: Known damage-dealing (whitelisted GUID)");
                        return true;
                    }

                    // 명시적 비피해 디버프 (실제 피해 컴포넌트 없음)
                    if (NonDamagingDebuffGuids.Contains(guid))
                    {
                        Main.LogDebug($"[FamiliarAbilities] {ability.Name}: Known non-damaging debuff (whitelisted GUID)");
                        return false;
                    }
                }

                // 무기 공격은 항상 피해
                if (ability.Weapon != null) return true;

                // ★ v3.7.65: 게임 네이티브 API 사용 (AoEDamage는 피해 능력)
                if (blueprint.IsAoEDamage) return true;

                // ★ v3.8.00: AttackType이 있으면 피해 능력 (게임 API 직접 사용)
                // AttackAbilityType: Melee, Scatter, Pattern, SingleShot
                if (blueprint.AttackType.HasValue)
                {
                    Main.LogDebug($"[FamiliarAbilities] {ability.Name}: Detected as damage-dealing (AttackType={blueprint.AttackType.Value})");
                    return true;
                }

                // ★ v3.8.05: 중첩된 컴포넌트 검색 (ContextActionDealDamage, ContextActionApplyDOT)
                // AbilityEffectRunAction.Actions.Actions 내부를 재귀적으로 검색
                // ★ v3.8.62: BlueprintCache 캐시 사용 (ComponentsArray 순회 제거)
                var runAction = BlueprintCache.GetCachedRunAction(blueprint);
                if (runAction?.Actions?.Actions != null)
                {
                    if (ContainsDamageAction(runAction.Actions.Actions))
                    {
                        Main.LogDebug($"[FamiliarAbilities] {ability.Name}: Detected as damage-dealing (ContextActionDealDamage found in Actions)");
                        return true;
                    }
                }

                // ★ v3.7.79: Point AOE + 적 타겟 = 피해 능력 (사이킹 비명 등)
                // Point 타겟 + 적만 타겟 가능 + Harmful = 확실한 공격 능력
                if (blueprint.CanTargetPoint && blueprint.CanTargetEnemies &&
                    !blueprint.CanTargetFriends && blueprint.EffectOnEnemy == AbilityEffectOnUnit.Harmful)
                {
                    Main.LogDebug($"[FamiliarAbilities] {ability.Name}: Detected as damage-dealing (Point AOE enemy-only harmful)");
                    return true;
                }

                // ★ v3.8.05: 여기까지 도달하면 피해 능력이 아님
                // NotOffensive 플래그는 신뢰할 수 없음 (감각박탈: NotOffensive=false이지만 피해 없음)
                // EffectOnEnemy도 신뢰할 수 없음 (점화: EffectOnEnemy=None이지만 피해 있음)
                // 위에서 DealDamage/ApplyDOT 컴포넌트를 찾지 못했으면 비피해 능력으로 판단
                Main.LogDebug($"[FamiliarAbilities] {ability.Name}: No damage components found → non-damaging");
                return false;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[FamiliarAbilities] IsDamageDealingAbility error for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.8.05: Actions 배열 내에서 피해 액션 검색 (재귀)
        /// ContextActionDealDamage, ContextActionApplyDOT 등
        /// </summary>
        private static bool ContainsDamageAction(Kingmaker.ElementsSystem.GameAction[] actions)
        {
            if (actions == null) return false;

            foreach (var action in actions)
            {
                if (action == null) continue;

                // ★ v3.8.59: 타입 안전 체크 (string 매칭 제거)
                if (action is ContextActionDealDamage ||
                    action is ContextActionApplyDOT)
                {
                    return true;
                }

                // 중첩된 Actions 검색 (SavingThrow, Conditional 등)
                // Reflection으로 Actions 또는 Succeed/Failed 프로퍼티 검색
                try
                {
                    var actionType = action.GetType();

                    // ContextActionSavingThrow.Actions
                    var actionsField = actionType.GetField("Actions");
                    if (actionsField != null)
                    {
                        var nestedActionList = actionsField.GetValue(action);
                        if (nestedActionList != null)
                        {
                            var nestedActionsField = nestedActionList.GetType().GetField("Actions");
                            if (nestedActionsField != null)
                            {
                                var nestedActions = nestedActionsField.GetValue(nestedActionList) as Kingmaker.ElementsSystem.GameAction[];
                                if (nestedActions != null && ContainsDamageAction(nestedActions))
                                    return true;
                            }
                        }
                    }

                    // ContextActionConditionalSaved.Succeed/Failed
                    var succeedField = actionType.GetField("Succeed");
                    var failedField = actionType.GetField("Failed");

                    if (succeedField != null)
                    {
                        var succeedActionList = succeedField.GetValue(action);
                        if (succeedActionList != null)
                        {
                            var succeedActionsField = succeedActionList.GetType().GetField("Actions");
                            if (succeedActionsField != null)
                            {
                                var succeedActions = succeedActionsField.GetValue(succeedActionList) as Kingmaker.ElementsSystem.GameAction[];
                                if (succeedActions != null && ContainsDamageAction(succeedActions))
                                    return true;
                            }
                        }
                    }

                    if (failedField != null)
                    {
                        var failedActionList = failedField.GetValue(action);
                        if (failedActionList != null)
                        {
                            var failedActionsField = failedActionList.GetType().GetField("Actions");
                            if (failedActionsField != null)
                            {
                                var failedActions = failedActionsField.GetValue(failedActionList) as Kingmaker.ElementsSystem.GameAction[];
                                if (failedActions != null && ContainsDamageAction(failedActions))
                                    return true;
                            }
                        }
                    }
                }
                catch
                {
                    // Reflection 실패 무시 - 다음 액션 계속 검색
                }
            }

            return false;
        }

        /// <summary>
        /// ★ v3.7.65: 사이킥 능력인지 확인 (게임 API 기반 - 키워드 매칭 제거)
        /// 게임 네이티브 IsPsykerAbility 사용
        /// ★ v3.7.96: public으로 변경 (BasePlan에서 사용)
        /// </summary>
        public static bool IsPsychicAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                var blueprint = ability.Blueprint;
                if (blueprint == null) return false;

                // ★ v3.7.65: 게임 네이티브 API 사용 (키워드 매칭 제거)
                return blueprint.IsPsykerAbility;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// ★ v3.7.96: 피해를 주는 사이킥 공격인지 확인
        /// Raven Warp Relay + Overcharge 상태에서 적에게 피해 전달용
        /// </summary>
        public static bool IsDamagingPsychicAttack(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                // 1. 사이킥 능력이어야 함
                if (!IsPsychicAbility(ability)) return false;

                // 2. 피해를 주는 능력이어야 함
                if (!IsDamageDealingAbility(ability)) return false;

                // 3. 적 타겟 가능해야 함
                var blueprint = ability.Blueprint;
                if (blueprint == null) return false;
                if (!blueprint.CanTargetEnemies) return false;

                // 4. 추가 턴 능력 제외
                if (IsExtraTurnAbility(ability)) return false;

                Main.LogDebug($"[FamiliarAbilities] {ability.Name}: Valid damaging psychic attack for Warp Relay");
                return true;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[FamiliarAbilities] IsDamagingPsychicAttack error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 추가 턴 부여 능력인지 확인 (Extrapolation/Warp Relay 제외 대상)
        /// </summary>
        /// ★ v3.8.59: GUID 우선 체크 + Blueprint 이름 폴백 (GUID 자동 등록)
        private static string _extraTurnGuid;
        private static bool IsExtraTurnAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                string guid = AbilityDatabase.GetGuid(ability);

                if (!string.IsNullOrEmpty(_extraTurnGuid) && guid == _extraTurnGuid)
                    return true;

                string blueprintName = ability.Blueprint?.name;
                if (string.IsNullOrEmpty(blueprintName)) return false;

                if (blueprintName.Equals("BringItDown_ExtraTurn_Ability", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(guid) && _extraTurnGuid == null)
                    {
                        _extraTurnGuid = guid;
                        Main.LogDebug($"[FamiliarAbilities] ExtraTurn GUID registered: {guid}");
                    }
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 사역마 관련 능력인지 확인 (GUID 기반)
        /// </summary>
        public static bool IsFamiliarAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                string guid = AbilityDatabase.GetGuid(ability);
                if (string.IsNullOrEmpty(guid)) return false;

                return ServoSkullAbilityGuids.Contains(guid) ||
                       PsyberRavenAbilityGuids.Contains(guid) ||
                       CyberMastiffAbilityGuids.Contains(guid) ||
                       CyberEagleAbilityGuids.Contains(guid);
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Ability Collection

        /// <summary>
        /// Master의 사역마 관련 능력 목록 수집
        /// ★ v3.7.01: RawFacts 직접 접근으로 변경 (GetAvailableAbilities 필터링 문제 해결)
        /// </summary>
        public static List<AbilityData> CollectFamiliarAbilities(
            Kingmaker.EntitySystem.Entities.BaseUnitEntity master,
            PetType familiarType)
        {
            var result = new List<AbilityData>();
            if (master == null) return result;

            try
            {
                // ★ v3.7.01: RawFacts 직접 접근 - 필터링 없이 모든 능력 조회
                var rawAbilities = master?.Abilities?.RawFacts;
                if (rawAbilities == null || rawAbilities.Count == 0)
                {
                    Main.LogDebug($"[FamiliarAbilities] {master.CharacterName}: No abilities in RawFacts");
                    return result;
                }

                foreach (var fact in rawAbilities)
                {
                    var ability = fact?.Data;
                    if (ability?.Blueprint == null) continue;

                    // BlueprintName 접두사로 Pet 능력 필터링
                    string bpName = ability.Blueprint.name ?? "";
                    if (!IsPetAbilityByName(bpName))
                        continue;

                    // ★ v3.7.21: 능력 가용성 체크 추가 - CasterRestriction, 쿨다운 등 검증
                    // 계획 단계에서 사용 불가능한 능력을 미리 필터링하여 Replan 루프 방지
                    List<string> unavailableReasons;
                    if (!GameInterface.CombatAPI.IsAbilityAvailable(ability, out unavailableReasons))
                    {
                        Main.LogDebug($"[FamiliarAbilities] Filtered out {ability.Name}: {string.Join(", ", unavailableReasons)}");
                        continue;
                    }

                    // 공통 능력
                    if (IsRelocateAbility(ability) || IsReactivateAbility(ability))
                    {
                        result.Add(ability);
                        continue;
                    }

                    // 타입별 키스톤/특수 능력
                    switch (familiarType)
                    {
                        case PetType.ServoskullSwarm:
                            if (IsMedicaeSignal(ability) || IsExtrapolationKeystone(ability) ||
                                IsPrioritySignal(ability) || IsVitalitySignal(ability))
                                result.Add(ability);
                            break;

                        case PetType.Raven:
                            // ★ v3.7.69: Raven 공격 능력은 Overcharge 상태에서만 수집
                            // Overcharge 없이 공격하면 Raven 자신에게 데미지가 들어감!
                            if (IsRavenAttackAbility(ability))
                            {
                                // Overcharge 상태 체크 - FamiliarAPI 사용
                                if (GameInterface.FamiliarAPI.IsRavenOverchargeActive(master))
                                {
                                    Main.LogDebug($"[FamiliarAbilities] Raven attack {ability.Name} collected (Overcharge active)");
                                    result.Add(ability);
                                }
                                else
                                {
                                    Main.LogDebug($"[FamiliarAbilities] Raven attack {ability.Name} SKIPPED - Overcharge NOT active (self-damage risk!)");
                                }
                            }
                            // 비공격 능력 (Warp Relay, Hex, Cycle 등)은 항상 수집
                            else if (IsWarpRelayKeystone(ability) || IsConcentrateAbility(ability) ||
                                     IsHexAbility(ability) || IsCycleAbility(ability))
                            {
                                result.Add(ability);
                            }
                            break;

                        case PetType.Mastiff:
                            // ★ v3.7.14: JumpClaws, Claws 추가
                            if (IsApprehendAbility(ability) || IsProtectAbility(ability) ||
                                IsFastAbility(ability) || IsRoamAbility(ability) ||
                                IsJumpClawsAbility(ability) || IsMastiffClawsAbility(ability))
                                result.Add(ability);
                            break;

                        case PetType.Eagle:
                            // ★ v3.7.14: EagleClaws 추가
                            // ★ v3.7.30: AbilityMultiTarget 컴포넌트 동적 감지 (BlueprintName 하드코딩 불필요!)
                            bool isMultiTarget = ability.Blueprint?.GetComponent<AbilityMultiTarget>() != null;
                            if (IsObstructVisionAbility(ability) || IsBlindingStrikeAbility(ability) ||
                                IsAerialRushAbility(ability) || IsScreenAbility(ability) ||
                                IsEagleClawsAbility(ability) || IsAerialRushSupportAbility(ability) ||
                                isMultiTarget)  // ★ v3.7.30: 모든 MultiTarget Eagle 능력 자동 수집
                            {
                                if (isMultiTarget)
                                    Main.LogDebug($"[FamiliarAbilities] Auto-collected MultiTarget: {ability.Name}");
                                result.Add(ability);
                            }
                            break;
                    }
                }

                Main.LogDebug($"[FamiliarAbilities] Collected {result.Count} familiar abilities for {master.CharacterName} ({familiarType})");
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[FamiliarAbilities] CollectFamiliarAbilities error: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// BlueprintName이 Pet 능력인지 확인 (접두사 기반)
        /// </summary>
        private static bool IsPetAbilityByName(string blueprintName)
        {
            if (string.IsNullOrEmpty(blueprintName)) return false;

            return blueprintName.StartsWith("MastiffPet_", StringComparison.OrdinalIgnoreCase) ||
                   blueprintName.StartsWith("EaglePet_", StringComparison.OrdinalIgnoreCase) ||
                   blueprintName.StartsWith("RavenPet_", StringComparison.OrdinalIgnoreCase) ||
                   blueprintName.StartsWith("ServoskullPet_", StringComparison.OrdinalIgnoreCase) ||
                   blueprintName.StartsWith("Pet_", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Extrapolation/Warp Relay 대상으로 사용 가능한 버프 능력 필터링
        /// </summary>
        public static List<AbilityData> FilterAbilitiesForFamiliarSpread(
            List<AbilityData> abilities,
            PetType familiarType)
        {
            var result = new List<AbilityData>();
            if (abilities == null) return result;

            foreach (var ability in abilities)
            {
                switch (familiarType)
                {
                    case PetType.ServoskullSwarm:
                        if (IsExtrapolationTarget(ability))
                            result.Add(ability);
                        break;

                    case PetType.Raven:
                        if (IsWarpRelayTarget(ability))
                            result.Add(ability);
                        break;

                    // Mastiff/Eagle은 버프 확산 없음
                }
            }

            return result;
        }

        #endregion
    }
}
