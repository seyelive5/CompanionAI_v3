using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityModManagerNet;

namespace CompanionAI_v3.Settings
{
    /// <summary>
    /// Language option
    /// </summary>
    public enum Language
    {
        English,
        Korean
    }

    /// <summary>
    /// Localization system
    /// </summary>
    public static class Localization
    {
        public static Language CurrentLanguage { get; set; } = Language.English;

        private static readonly Dictionary<string, Dictionary<Language, string>> Strings = new()
        {
            // Header
            ["Title"] = new() { { Language.English, "Companion AI v3.0 - TurnPlanner System" }, { Language.Korean, "동료 AI v3.0 - TurnPlanner 시스템" } },
            ["Subtitle"] = new() { { Language.English, "Complete AI replacement with TurnPlanner architecture" }, { Language.Korean, "TurnPlanner 아키텍처 기반 완전한 AI 대체" } },

            // Global Settings
            ["GlobalSettings"] = new() { { Language.English, "Global Settings" }, { Language.Korean, "전역 설정" } },
            ["EnableDebugLogging"] = new() { { Language.English, "Enable Debug Logging" }, { Language.Korean, "디버그 로깅 활성화" } },
            ["ShowAIDecisionLog"] = new() { { Language.English, "Show AI Decision Log" }, { Language.Korean, "AI 결정 로그 표시" } },
            ["Language"] = new() { { Language.English, "Language" }, { Language.Korean, "언어" } },

            // Party Members
            ["PartyMembers"] = new() { { Language.English, "Party Members" }, { Language.Korean, "파티원" } },
            ["AI"] = new() { { Language.English, "AI" }, { Language.Korean, "AI" } },
            ["Character"] = new() { { Language.English, "Character" }, { Language.Korean, "캐릭터" } },
            ["Role"] = new() { { Language.English, "Role" }, { Language.Korean, "역할" } },
            ["Range"] = new() { { Language.English, "Range" }, { Language.Korean, "거리" } },
            ["NoCharacters"] = new() { { Language.English, "No characters available. Load a save game first." }, { Language.Korean, "사용 가능한 캐릭터가 없습니다. 먼저 저장 파일을 불러오세요." } },

            // Combat Role
            ["CombatRole"] = new() { { Language.English, "Combat Role" }, { Language.Korean, "전투 역할" } },
            ["CombatRoleDesc"] = new() { { Language.English, "How should this character behave in combat?" }, { Language.Korean, "이 캐릭터가 전투에서 어떻게 행동할까요?" } },

            // Role names
            ["Role_Balanced"] = new() { { Language.English, "Balanced" }, { Language.Korean, "균형" } },
            ["Role_Tank"] = new() { { Language.English, "Tank" }, { Language.Korean, "탱커" } },
            ["Role_DPS"] = new() { { Language.English, "DPS" }, { Language.Korean, "딜러" } },
            ["Role_Support"] = new() { { Language.English, "Support" }, { Language.Korean, "지원" } },

            // Role descriptions
            ["RoleDesc_Balanced"] = new() {
                { Language.English, "Jack of all trades. Uses the best available action based on the situation." },
                { Language.Korean, "만능형. 상황에 맞는 최선의 행동을 선택합니다." }
            },
            ["RoleDesc_Tank"] = new() {
                { Language.English, "Frontline fighter. Draws enemy attention, uses defensive skills, protects allies." },
                { Language.Korean, "최전방 전사. 적의 주의를 끌고, 방어 스킬 사용, 아군을 보호합니다." }
            },
            ["RoleDesc_DPS"] = new() {
                { Language.English, "Damage dealer. Focuses on killing enemies quickly, prioritizes low HP targets." },
                { Language.Korean, "딜러. 적을 빠르게 처치하는 데 집중, 체력 낮은 적 우선 공격." }
            },
            ["RoleDesc_Support"] = new() {
                { Language.English, "Team supporter. Prioritizes buffs/debuffs, heals allies, avoids front line." },
                { Language.Korean, "팀 서포터. 버프/디버프 우선, 아군 치유, 최전방 회피." }
            },

            // Range Preference
            ["RangePreference"] = new() { { Language.English, "Range Preference" }, { Language.Korean, "거리 선호도" } },
            ["RangePreferenceDesc"] = new() { { Language.English, "How does this character prefer to engage enemies?" }, { Language.Korean, "이 캐릭터가 적과 어떻게 교전할까요?" } },

            // Range preference names
            ["Range_Adaptive"] = new() { { Language.English, "Adaptive" }, { Language.Korean, "적응형" } },
            ["Range_PreferMelee"] = new() { { Language.English, "Melee" }, { Language.Korean, "근접" } },
            ["Range_PreferRanged"] = new() { { Language.English, "Ranged" }, { Language.Korean, "원거리" } },
            ["Range_MaintainRange"] = new() { { Language.English, "Keep Dist." }, { Language.Korean, "거리 유지" } },

            // Range preference descriptions
            ["RangeDesc_Adaptive"] = new() {
                { Language.English, "Uses whatever weapon/skill is already in range. Minimizes unnecessary movement." },
                { Language.Korean, "이미 사거리 내에 있는 무기/스킬 사용. 불필요한 이동 최소화." }
            },
            ["RangeDesc_PreferMelee"] = new() {
                { Language.English, "Actively moves toward enemies for close combat. Best for melee fighters." },
                { Language.Korean, "적에게 적극적으로 접근. 근접 전투원에게 적합." }
            },
            ["RangeDesc_PreferRanged"] = new() {
                { Language.English, "Keeps safe distance from enemies. Prioritizes ranged attacks over melee." },
                { Language.Korean, "적과 안전 거리 유지. 근접보다 원거리 공격 우선." }
            },
            ["RangeDesc_MaintainRange"] = new() {
                { Language.English, "Tries to stay at optimal weapon range. Moves back if enemies get too close." },
                { Language.Korean, "최적 무기 사거리 유지 시도. 적이 너무 가까우면 후퇴." }
            },
        };

        public static string Get(string key)
        {
            if (Strings.TryGetValue(key, out var translations))
            {
                if (translations.TryGetValue(CurrentLanguage, out var text))
                    return text;
                if (translations.TryGetValue(Language.English, out var fallback))
                    return fallback;
            }
            return key;
        }

        public static string GetRoleName(AIRole role) => Get($"Role_{role}");
        public static string GetRoleDescription(AIRole role) => Get($"RoleDesc_{role}");
        public static string GetRangeName(RangePreference pref) => Get($"Range_{pref}");
        public static string GetRangeDescription(RangePreference pref) => Get($"RangeDesc_{pref}");
    }

    /// <summary>
    /// Role-based AI behavior profiles
    /// </summary>
    public enum AIRole
    {
        Balanced,   // Default balanced behavior
        Tank,       // Prioritize defense, draw enemy attention
        DPS,        // Prioritize damage output
        Support     // Prioritize buffs and debuffs
    }

    /// <summary>
    /// Range preference for combat
    /// </summary>
    public enum RangePreference
    {
        Adaptive,       // Use whatever is equipped
        PreferMelee,    // Stay close to enemies
        PreferRanged,   // Keep distance from enemies
        MaintainRange   // Try to stay at optimal weapon range
    }

    /// <summary>
    /// Settings for individual character
    /// </summary>
    public class CharacterSettings
    {
        public string CharacterId { get; set; } = "";
        public string CharacterName { get; set; } = "";
        public bool EnableCustomAI { get; set; } = false;
        public AIRole Role { get; set; } = AIRole.Balanced;
        public RangePreference RangePreference { get; set; } = RangePreference.Adaptive;

        // Combat behavior
        public bool UseBuffsBeforeAttack { get; set; } = true;
        public bool FinishLowHPEnemies { get; set; } = true;
        public bool AvoidFriendlyFire { get; set; } = true;
        public int MinEnemiesForAoE { get; set; } = 2;

        // Movement behavior
        public bool AllowRetreat { get; set; } = true;
        public bool SeekCover { get; set; } = true;
        public float MinSafeDistance { get; set; } = 5.0f;

        // Resource management
        public bool ConserveAmmo { get; set; } = false;
        public int HealAtHPPercent { get; set; } = 50;
    }

    /// <summary>
    /// Global mod settings
    /// </summary>
    public class ModSettings
    {
        public static ModSettings Instance { get; private set; }
        private static UnityModManager.ModEntry _modEntry;

        public bool EnableDebugLogging { get; set; } = false;
        public bool ShowAIThoughts { get; set; } = false;
        public Language UILanguage { get; set; } = Language.English;

        /// <summary>
        /// ★ v3.0.15: 주인공도 AI 제어 여부
        /// </summary>
        public bool ControlMainCharacter { get; set; } = true;
        public CharacterSettings DefaultSettings { get; set; } = new CharacterSettings();
        public Dictionary<string, CharacterSettings> CharacterSettings { get; set; }
            = new Dictionary<string, CharacterSettings>();

        public CharacterSettings GetOrCreateSettings(string characterId, string characterName = null)
        {
            if (string.IsNullOrEmpty(characterId))
                return DefaultSettings;

            if (!CharacterSettings.TryGetValue(characterId, out var settings))
            {
                settings = new CharacterSettings
                {
                    CharacterId = characterId,
                    CharacterName = characterName ?? characterId,
                    EnableCustomAI = DefaultSettings.EnableCustomAI,
                    Role = DefaultSettings.Role,
                    RangePreference = DefaultSettings.RangePreference,
                    UseBuffsBeforeAttack = DefaultSettings.UseBuffsBeforeAttack,
                    FinishLowHPEnemies = DefaultSettings.FinishLowHPEnemies,
                    AvoidFriendlyFire = DefaultSettings.AvoidFriendlyFire,
                    MinEnemiesForAoE = DefaultSettings.MinEnemiesForAoE,
                    AllowRetreat = DefaultSettings.AllowRetreat,
                    SeekCover = DefaultSettings.SeekCover,
                    MinSafeDistance = DefaultSettings.MinSafeDistance,
                    ConserveAmmo = DefaultSettings.ConserveAmmo,
                    HealAtHPPercent = DefaultSettings.HealAtHPPercent
                };
                CharacterSettings[characterId] = settings;
            }

            if (!string.IsNullOrEmpty(characterName))
                settings.CharacterName = characterName;

            return settings;
        }

        #region Save/Load

        private static string GetSettingsPath()
        {
            return Path.Combine(_modEntry.Path, "settings.json");
        }

        public static void Load(UnityModManager.ModEntry modEntry)
        {
            _modEntry = modEntry;
            try
            {
                string path = GetSettingsPath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var settings = JsonConvert.DeserializeObject<ModSettings>(json);
                    if (settings != null)
                    {
                        Instance = settings;
                        Main.Log("Settings loaded successfully");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Main.LogError($"Failed to load settings: {ex.Message}");
            }

            Main.Log("Using default settings");
            Instance = new ModSettings();
        }

        public static void Save()
        {
            if (Instance == null || _modEntry == null) return;

            try
            {
                string path = GetSettingsPath();
                string json = JsonConvert.SerializeObject(Instance, Formatting.Indented);
                File.WriteAllText(path, json);
                Main.LogDebug("Settings saved");
            }
            catch (Exception ex)
            {
                Main.LogError($"Failed to save settings: {ex.Message}");
            }
        }

        #endregion
    }
}
