using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Enums;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Parts;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// v3.7.00: Blueprint/GUID 추출 유틸리티
    /// Pet 능력 BlueprintName과 GUID를 파일로 덤프
    /// </summary>
    public static class AbilityDumper
    {
        private static string DumpFilePath => Path.Combine(Main.ModPath, "PetAbilities_Dump.txt");

        /// <summary>
        /// Pet 유닛의 모든 능력 덤프 (핵심 기능)
        /// BlueprintName과 GUID만 추출
        /// </summary>
        public static void DumpPetAbilities()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("============================================================");
                sb.AppendLine("CompanionAI v3 - PET ABILITIES DUMP");
                sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine("============================================================");
                sb.AppendLine();
                sb.AppendLine("FORMAT: BlueprintName | GUID");
                sb.AppendLine();

                var party = Game.Instance?.Player?.PartyAndPets;
                if (party == null || party.Count == 0)
                {
                    sb.AppendLine("ERROR: No party members found. Start combat first!");
                    File.WriteAllText(DumpFilePath, sb.ToString());
                    Main.Log($"[AbilityDumper] No party found");
                    return;
                }

                int petCount = 0;

                // Pet 유닛만 찾기
                foreach (var unit in party)
                {
                    if (unit == null || !unit.IsPet) continue;

                    petCount++;
                    var masterPetOwner = unit.Master?.GetOptional<UnitPartPetOwner>();
                    var petType = masterPetOwner?.PetType;

                    sb.AppendLine("------------------------------------------------------------");
                    sb.AppendLine($"PET: {unit.CharacterName}");
                    sb.AppendLine($"  Type: {petType?.ToString() ?? "Unknown"}");
                    sb.AppendLine($"  Master: {unit.Master?.CharacterName ?? "None"}");
                    sb.AppendLine();

                    // Pet의 모든 능력 덤프
                    DumpUnitAbilitiesSimple(sb, unit, "  ");
                }

                // Master의 사역마 관련 능력 덤프
                sb.AppendLine();
                sb.AppendLine("============================================================");
                sb.AppendLine("MASTER -> PET ABILITIES (Relocate, Keystone 등)");
                sb.AppendLine("============================================================");
                sb.AppendLine();

                foreach (var unit in party)
                {
                    if (unit == null || !unit.IsMaster) continue;

                    var petOwner = unit.GetOptional<UnitPartPetOwner>();
                    if (petOwner?.PetUnit == null) continue;

                    sb.AppendLine("------------------------------------------------------------");
                    sb.AppendLine($"MASTER: {unit.CharacterName}");
                    sb.AppendLine($"  Pet: {petOwner.PetUnit.CharacterName} ({petOwner.PetType})");
                    sb.AppendLine();

                    // Master의 Pet 관련 능력만 필터
                    DumpMasterPetAbilities(sb, unit, "  ");
                }

                if (petCount == 0)
                {
                    sb.AppendLine("WARNING: No pets found in party!");
                    sb.AppendLine("Make sure you have a familiar-using character in your party.");
                }

                File.WriteAllText(DumpFilePath, sb.ToString());
                Main.Log($"[AbilityDumper] Pet ability dump saved: {DumpFilePath}");
            }
            catch (Exception ex)
            {
                Main.Log($"[AbilityDumper] Error: {ex.Message}");
            }
        }

        /// <summary>
        /// 유닛의 능력을 간단한 형식으로 덤프
        /// 필터링 없이 모든 능력 표시 (RawFacts 직접 접근)
        /// </summary>
        private static void DumpUnitAbilitiesSimple(StringBuilder sb, BaseUnitEntity unit, string indent)
        {
            // 필터링 없이 모든 능력을 직접 접근
            var rawAbilities = unit?.Abilities?.RawFacts;
            if (rawAbilities == null || rawAbilities.Count == 0)
            {
                sb.AppendLine($"{indent}(No abilities in RawFacts)");
                return;
            }

            var sortedAbilities = rawAbilities
                .Where(a => a?.Data?.Blueprint != null)
                .Select(a => a.Data)
                .OrderBy(a => a.Blueprint.name)
                .ToList();

            sb.AppendLine($"{indent}Total abilities: {sortedAbilities.Count}");
            sb.AppendLine();

            foreach (var ability in sortedAbilities)
            {
                var bp = ability.Blueprint;
                string guid = bp.AssetGuid?.ToString() ?? "NO_GUID";
                string blueprintName = bp.name ?? "NO_NAME";

                sb.AppendLine($"{indent}{blueprintName}");
                sb.AppendLine($"{indent}  GUID: {guid}");
            }
        }

        /// <summary>
        /// Master의 Pet 관련 능력 덤프
        /// BlueprintName 접두사로 필터 (MastiffPet_, EaglePet_, RavenPet_, ServoskullPet_)
        /// RawFacts 직접 접근 (필터링 없음)
        /// </summary>
        private static void DumpMasterPetAbilities(StringBuilder sb, BaseUnitEntity master, string indent)
        {
            // 필터링 없이 모든 능력을 직접 접근
            var rawAbilities = master?.Abilities?.RawFacts;
            if (rawAbilities == null || rawAbilities.Count == 0)
            {
                sb.AppendLine($"{indent}(No abilities in RawFacts)");
                return;
            }

            // Pet 관련 접두사 필터 (BlueprintName이 이것으로 시작하면)
            var petPrefixes = new[] {
                "MastiffPet_",
                "EaglePet_",
                "RavenPet_",
                "ServoskullPet_",
                "Pet_",
                "Familiar_",
            };

            var petAbilities = rawAbilities
                .Where(a => a?.Data?.Blueprint != null)
                .Select(a => a.Data)
                .Where(a => {
                    string name = a.Blueprint.name ?? "";
                    return petPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                })
                .OrderBy(a => a.Blueprint.name)
                .ToList();

            if (petAbilities.Count == 0)
            {
                sb.AppendLine($"{indent}(No pet-related abilities found with Pet_ prefixes)");
                sb.AppendLine($"{indent}Total abilities on master: {rawAbilities.Count}");
                return;
            }

            sb.AppendLine($"{indent}Found {petAbilities.Count} pet abilities:");
            sb.AppendLine();

            foreach (var ability in petAbilities)
            {
                var bp = ability.Blueprint;
                string guid = bp.AssetGuid?.ToString() ?? "NO_GUID";
                string blueprintName = bp.name ?? "NO_NAME";

                sb.AppendLine($"{indent}{blueprintName}");
                sb.AppendLine($"{indent}  GUID: {guid}");
                sb.AppendLine($"{indent}  CanTargetPoint: {bp.CanTargetPoint}");
                sb.AppendLine($"{indent}  CanTargetFriends: {bp.CanTargetFriends}");
                sb.AppendLine($"{indent}  CanTargetEnemies: {bp.CanTargetEnemies}");
            }
        }

        /// <summary>
        /// 전체 파티의 모든 능력 덤프 (상세 버전)
        /// </summary>
        public static void DumpAllPartyAbilities()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("============================================================");
                sb.AppendLine("CompanionAI v3 - ALL PARTY ABILITIES (DETAILED)");
                sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine("============================================================");
                sb.AppendLine();

                var party = Game.Instance?.Player?.PartyAndPets;
                if (party == null || party.Count == 0)
                {
                    sb.AppendLine("ERROR: No party members found.");
                    File.WriteAllText(DumpFilePath, sb.ToString());
                    return;
                }

                foreach (var unit in party)
                {
                    if (unit == null) continue;

                    sb.AppendLine("------------------------------------------------------------");

                    if (unit.IsPet)
                    {
                        var masterPetOwner = unit.Master?.GetOptional<UnitPartPetOwner>();
                        var petType = masterPetOwner?.PetType;
                        sb.AppendLine($"[PET] {unit.CharacterName} ({petType})");
                        sb.AppendLine($"  Master: {unit.Master?.CharacterName ?? "None"}");
                    }
                    else
                    {
                        sb.AppendLine($"[UNIT] {unit.CharacterName}");
                        if (unit.IsMaster)
                        {
                            var petOwner = unit.GetOptional<UnitPartPetOwner>();
                            if (petOwner?.PetUnit != null)
                                sb.AppendLine($"  Has Familiar: {petOwner.PetUnit.CharacterName} ({petOwner.PetType})");
                        }
                    }
                    sb.AppendLine();

                    var rawAbilities = unit?.Abilities?.RawFacts;
                    if (rawAbilities == null || rawAbilities.Count == 0)
                    {
                        sb.AppendLine("  (No abilities in RawFacts)");
                        continue;
                    }

                    var abilities = rawAbilities
                        .Where(a => a?.Data?.Blueprint != null)
                        .Select(a => a.Data)
                        .OrderBy(a => a.Blueprint.name)
                        .ToList();

                    int index = 1;
                    foreach (var ability in abilities)
                    {
                        if (ability?.Blueprint == null) continue;

                        var bp = ability.Blueprint;
                        string guid = bp.AssetGuid?.ToString() ?? "NO_GUID";

                        sb.AppendLine($"  [{index++}] {bp.name}");
                        sb.AppendLine($"      GUID: {guid}");
                        sb.AppendLine($"      DisplayName: {ability.Name}");
                        sb.AppendLine($"      CanTargetSelf: {bp.CanTargetSelf}");
                        sb.AppendLine($"      CanTargetFriends: {bp.CanTargetFriends}");
                        sb.AppendLine($"      CanTargetEnemies: {bp.CanTargetEnemies}");
                        sb.AppendLine($"      CanTargetPoint: {bp.CanTargetPoint}");
                        sb.AppendLine($"      IsMelee: {ability.IsMelee}");
                        sb.AppendLine($"      APCost: {CombatAPI.GetAbilityAPCost(ability):F1}");
                        sb.AppendLine();
                    }
                }

                string allDumpPath = Path.Combine(Main.ModPath, "AllAbilities_Dump.txt");
                File.WriteAllText(allDumpPath, sb.ToString());
                Main.Log($"[AbilityDumper] All abilities dump saved: {allDumpPath}");
            }
            catch (Exception ex)
            {
                Main.Log($"[AbilityDumper] Error: {ex.Message}");
            }
        }

        /// <summary>
        /// BlueprintName으로 검색하여 GUID 찾기
        /// </summary>
        public static void SearchAbilityByName(string searchTerm)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Search results for: '{searchTerm}'");
                sb.AppendLine();

                var party = Game.Instance?.Player?.PartyAndPets;
                if (party == null) return;

                foreach (var unit in party)
                {
                    if (unit == null) continue;

                    var rawAbilities = unit?.Abilities?.RawFacts;
                    if (rawAbilities == null) continue;

                    foreach (var fact in rawAbilities)
                    {
                        var ability = fact?.Data;
                        if (ability?.Blueprint == null) continue;

                        string bpName = ability.Blueprint.name ?? "";
                        if (bpName.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            string guid = ability.Blueprint.AssetGuid?.ToString() ?? "NO_GUID";
                            sb.AppendLine($"  {bpName}");
                            sb.AppendLine($"    GUID: {guid}");
                            sb.AppendLine($"    Unit: {unit.CharacterName}");
                        }
                    }
                }

                Main.Log(sb.ToString());
            }
            catch (Exception ex)
            {
                Main.Log($"[AbilityDumper] Search error: {ex.Message}");
            }
        }
    }
}
