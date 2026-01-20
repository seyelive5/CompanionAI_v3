using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.Enums;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
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
        };

        // ========================================
        // Aerial Rush 능력 (Cyber-Eagle)
        // ========================================
        private static readonly HashSet<string> AerialRushBlueprintNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "EaglePet_AerialRush_Ability",
        };

        private static readonly HashSet<string> AerialRushGuids = new()
        {
            "95c502e72a6743d1ad0cbadf13051225",  // EaglePet_AerialRush_Ability
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
        /// TODO: GUID 확인 후 추가
        /// </summary>
        public static bool IsReactivateAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                string blueprintName = ability.Blueprint?.name;
                if (string.IsNullOrEmpty(blueprintName)) return false;

                // 정확한 BlueprintName 매칭
                return blueprintName.Equals("Reactivate_Ability", StringComparison.OrdinalIgnoreCase) ||
                       blueprintName.Equals("Pet_Reactivate_Ability", StringComparison.OrdinalIgnoreCase);
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
        public static bool IsPurificationDischarge(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                string blueprintName = ability.Blueprint?.name;
                if (string.IsNullOrEmpty(blueprintName)) return false;

                // 정확한 BlueprintName 매칭
                return blueprintName.Equals("RavenPet_PurificationDischarge_Ability", StringComparison.OrdinalIgnoreCase) ||
                       blueprintName.Equals("PurificationDischarge", StringComparison.OrdinalIgnoreCase);
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
                // GUID 체크
                string guid = AbilityDatabase.GetGuid(ability);
                if (guid == "33aa1b047d084a9b8faf534767a3a534")
                    return true;

                // BlueprintName 체크
                string bpName = ability.Blueprint?.name;
                return bpName?.Equals("ServoskullPet_PrioritySignal_Ability", StringComparison.OrdinalIgnoreCase) ?? false;
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
                // GUID 체크
                string guid = AbilityDatabase.GetGuid(ability);
                if (guid == "62eeb81743734fc5b8fac71b34b14683")
                    return true;

                // BlueprintName 체크
                string bpName = ability.Blueprint?.name;
                return bpName?.Equals("ServoskullPet_VitalitySignal_Ability", StringComparison.OrdinalIgnoreCase) ?? false;
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
        /// ★ v3.7.09: 피해를 주는 능력인지 확인 (Warp Relay 디버프 구분용)
        /// - 무기 공격: true
        /// - 피해 사이킥 (Pyromancy 공격, Telekinesis 공격): true
        /// - 비피해 디버프 (Telepathy 디버프, Divination 디버프): false
        /// </summary>
        private static bool IsDamageDealingAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                // 무기 공격은 항상 피해
                if (ability.Weapon != null) return true;

                var blueprint = ability.Blueprint;
                if (blueprint == null) return false;

                string bpName = blueprint.name ?? "";

                // ★ Pyromancy는 대부분 직접 피해 능력
                // 점화(Ignite), 화염 폭발(Flame Burst), 불기둥(Pillar of Fire) 등
                if (bpName.Contains("Pyromancy") || bpName.Contains("Pyro") ||
                    bpName.Contains("Fire") || bpName.Contains("Flame") ||
                    bpName.Contains("Burn") || bpName.Contains("Ignite"))
                {
                    Main.LogDebug($"[FamiliarAbilities] {ability.Name}: Damage-dealing (Pyromancy pattern)");
                    return true;
                }

                // ★ Telekinesis 공격 능력
                // 염동력 일격(Telekinetic Strike), 염동력 밀치기(Telekinetic Push) 등
                if (bpName.Contains("TelekineticStrike") || bpName.Contains("TelekineticPush") ||
                    bpName.Contains("TelekineticRam") || bpName.Contains("TelekineticLash"))
                {
                    Main.LogDebug($"[FamiliarAbilities] {ability.Name}: Damage-dealing (Telekinesis attack)");
                    return true;
                }

                // ★ Biomancy 공격 능력 (피해를 주는 것들)
                if (bpName.Contains("LifeDrain") || bpName.Contains("Smite") ||
                    bpName.Contains("Wrath") || bpName.Contains("Bolt"))
                {
                    Main.LogDebug($"[FamiliarAbilities] {ability.Name}: Damage-dealing (Biomancy/attack pattern)");
                    return true;
                }

                // ★ Telepathy 능력은 대부분 디버프 (비피해)
                // 감각 박탈(Sensory Deprivation), 정신 공격 등은 피해가 아닌 상태이상
                if (bpName.Contains("Telepathy") || bpName.Contains("SensoryDeprivation") ||
                    bpName.Contains("MindControl") || bpName.Contains("Dominate") ||
                    bpName.Contains("Terrify") || bpName.Contains("Hallucination"))
                {
                    Main.LogDebug($"[FamiliarAbilities] {ability.Name}: Non-damaging debuff (Telepathy pattern)");
                    return false;
                }

                // ★ Divination 능력은 대부분 버프/디버프 (비피해)
                if (bpName.Contains("Divination") || bpName.Contains("Prescience") ||
                    bpName.Contains("Forewarning") || bpName.Contains("Scry"))
                {
                    Main.LogDebug($"[FamiliarAbilities] {ability.Name}: Non-damaging (Divination pattern)");
                    return false;
                }

                // 기본값: CanTargetEnemies && Harmful이면 피해 취급 (보수적)
                if (blueprint.CanTargetEnemies && !blueprint.CanTargetFriends &&
                    blueprint.EffectOnEnemy == AbilityEffectOnUnit.Harmful)
                {
                    Main.LogDebug($"[FamiliarAbilities] {ability.Name}: Assumed damage-dealing (Harmful enemy-only)");
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
        /// ★ v3.7.02 Fix: 사이킥 능력인지 확인 (더 정확한 판별)
        /// Officer 버프는 AbilityType.Spell이지만 사이킥이 아님
        /// </summary>
        private static bool IsPsychicAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                var blueprint = ability.Blueprint;
                if (blueprint == null) return false;

                // BlueprintName으로 먼저 확인 (더 정확)
                string bpName = blueprint.name ?? "";

                // Officer/Nobility 능력은 사이킥이 아님 (명령 능력)
                if (bpName.Contains("Officer_") || bpName.Contains("Nobility_") ||
                    bpName.Contains("VoiceOfCommand") || bpName.Contains("BringItDown") ||
                    bpName.Contains("StrategicAdaptation") || bpName.Contains("ServeMe"))
                    return false;

                // 명시적인 사이킥 능력 패턴
                if (bpName.Contains("Psychic") || bpName.Contains("PsyPower") ||
                    bpName.Contains("Psyker") || bpName.Contains("Biomancy") ||
                    bpName.Contains("Divination") || bpName.Contains("Pyromancy") ||
                    bpName.Contains("Telekinesis") || bpName.Contains("Telepathy"))
                    return true;

                // Raven 전용 능력은 사이킥 취급
                if (bpName.Contains("RavenPet_Concentrate") || bpName.Contains("RavenPet_Hex"))
                    return true;

                // Type.Spell은 사이킥 가능성이 있지만 위에서 제외되지 않은 경우만
                // (이 경우 보수적으로 false 반환 - Officer 버프 보호)
                // 실제 사이킥은 위 패턴으로 이미 잡힘
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 추가 턴 부여 능력인지 확인 (Extrapolation/Warp Relay 제외 대상)
        /// </summary>
        private static bool IsExtraTurnAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                // BlueprintName으로 확인 (정확한 이름만)
                string blueprintName = ability.Blueprint?.name;
                if (string.IsNullOrEmpty(blueprintName)) return false;

                // 알려진 추가 턴 능력 (정확한 이름)
                return blueprintName.Equals("BringItDown_ExtraTurn_Ability", StringComparison.OrdinalIgnoreCase);
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
                            // ★ v3.7.12: IsCycleAbility 추가
                            if (IsPurificationDischarge(ability) || IsWarpRelayKeystone(ability) ||
                                IsConcentrateAbility(ability) || IsHexAbility(ability) ||
                                IsCycleAbility(ability))
                                result.Add(ability);
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
                            if (IsObstructVisionAbility(ability) || IsBlindingStrikeAbility(ability) ||
                                IsAerialRushAbility(ability) || IsScreenAbility(ability) ||
                                IsEagleClawsAbility(ability))
                                result.Add(ability);
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
