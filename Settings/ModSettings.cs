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
            ["Role_Auto"] = new() { { Language.English, "Auto" }, { Language.Korean, "자동" } },
            ["Role_Tank"] = new() { { Language.English, "Tank" }, { Language.Korean, "탱커" } },
            ["Role_DPS"] = new() { { Language.English, "DPS" }, { Language.Korean, "딜러" } },
            ["Role_Support"] = new() { { Language.English, "Support" }, { Language.Korean, "지원" } },

            // Role descriptions
            ["RoleDesc_Auto"] = new() {
                { Language.English, "Automatically detects optimal role based on character abilities.\n• Has Taunt/Defense → Tank\n• Has Finisher/Heroic Act → DPS\n• Has Ally Heal/Buff → Support" },
                { Language.Korean, "캐릭터 능력을 분석하여 최적 역할을 자동 감지합니다.\n• 도발/방어 스킬 보유 → 탱커\n• 마무리/영웅적 행동 보유 → 딜러\n• 아군 힐/버프 보유 → 지원" }
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

            // ★ v3.2.30: Kill Simulator
            ["UseKillSimulator"] = new() {
                { Language.English, "Use Kill Simulator" },
                { Language.Korean, "킬 시뮬레이터 사용" }
            },
            ["UseKillSimulatorDesc"] = new() {
                { Language.English, "Simulates multi-ability combinations to find confirmed kills.\nSlightly increases processing time but improves kill efficiency." },
                { Language.Korean, "다중 능력 조합을 시뮬레이션하여 확정 킬을 찾습니다.\n처리 시간이 약간 증가하지만 킬 효율이 향상됩니다." }
            },

            // ★ v3.3.00: AOE Optimization
            ["UseAoEOptimization"] = new() {
                { Language.English, "Use AOE Optimization" },
                { Language.Korean, "AOE 최적화 사용" }
            },
            ["UseAoEOptimizationDesc"] = new() {
                { Language.English, "Detect enemy clusters for optimal AOE targeting.\nSlightly increases processing time but improves AOE efficiency." },
                { Language.Korean, "적 클러스터를 탐지하여 최적의 AOE 위치를 찾습니다.\n처리 시간이 약간 증가하지만 AOE 효율이 향상됩니다." }
            },

            // ★ v3.4.00: Predictive Movement
            ["UsePredictiveMovement"] = new() {
                { Language.English, "Use Predictive Movement" },
                { Language.Korean, "예측적 이동 사용" }
            },
            ["UsePredictiveMovementDesc"] = new() {
                { Language.English, "Predict enemy movement to select safer positions.\nConsiders where enemies can move next turn." },
                { Language.Korean, "적 이동을 예측하여 더 안전한 위치를 선택합니다.\n다음 턴에 적이 이동할 수 있는 위치를 고려합니다." }
            },

            // ★ v3.5.13: Advanced Settings UI
            ["AdvancedSettings"] = new() {
                { Language.English, "Advanced Settings" },
                { Language.Korean, "고급 설정" }
            },
            ["AdvancedWarning"] = new() {
                { Language.English, "⚠️ Changing these values may negatively affect AI behavior. Use with caution." },
                { Language.Korean, "⚠️ 이 값들을 변경하면 AI 동작에 부정적인 영향을 줄 수 있습니다. 주의하세요." }
            },
            ["ResetToDefault"] = new() {
                { Language.English, "Reset to Default" },
                { Language.Korean, "기본값으로 리셋" }
            },
            ["MinSafeDistance"] = new() {
                { Language.English, "Min Safe Distance" },
                { Language.Korean, "최소 안전 거리" }
            },
            ["MinSafeDistanceDesc"] = new() {
                { Language.English, "Minimum distance ranged characters try to keep from enemies (meters)" },
                { Language.Korean, "원거리 캐릭터가 적과 유지하려는 최소 거리 (미터)" }
            },
            ["HealAtHPPercent"] = new() {
                { Language.English, "Heal at HP%" },
                { Language.Korean, "힐 시작 HP%" }
            },
            ["HealAtHPPercentDesc"] = new() {
                { Language.English, "Start healing allies when their HP falls below this percentage" },
                { Language.Korean, "아군 HP가 이 퍼센트 이하로 떨어지면 힐 시작" }
            },
            ["MinEnemiesForAoE"] = new() {
                { Language.English, "Min Enemies for AOE" },
                { Language.Korean, "AOE 최소 적 수" }
            },
            ["MinEnemiesForAoEDesc"] = new() {
                { Language.English, "Minimum number of enemies to use AOE abilities" },
                { Language.Korean, "AOE 능력 사용에 필요한 최소 적 수" }
            },

            // ★ v3.5.20: Performance Settings
            ["PerformanceSettings"] = new() {
                { Language.English, "Performance Settings" },
                { Language.Korean, "성능 설정" }
            },
            ["PerformanceWarning"] = new() {
                { Language.English, "⚠️ Lower values = faster but less accurate AI. Higher values = smarter but slower." },
                { Language.Korean, "⚠️ 낮은 값 = 빠르지만 부정확한 AI. 높은 값 = 똑똑하지만 느림." }
            },
            ["MaxEnemiesToAnalyze"] = new() {
                { Language.English, "Max Enemies to Analyze" },
                { Language.Korean, "최대 분석 적 수" }
            },
            ["MaxEnemiesToAnalyzeDesc"] = new() {
                { Language.English, "How many enemies to evaluate when predicting threats.\nMore = accurate threat prediction, but slower.\n(Affects: Movement safety, retreat decisions)" },
                { Language.Korean, "위협 예측 시 분석할 최대 적 수.\n많을수록 위협 예측이 정확하지만 느려집니다.\n(영향: 이동 안전성, 후퇴 결정)" }
            },
            ["MaxPositionsToEvaluate"] = new() {
                { Language.English, "Max Positions to Evaluate" },
                { Language.Korean, "최대 평가 위치 수" }
            },
            ["MaxPositionsToEvaluateDesc"] = new() {
                { Language.English, "How many positions to check for optimal AOE placement.\nMore = better AOE targeting, but slower.\n(Affects: AOE ability targeting)" },
                { Language.Korean, "AOE 최적 위치 탐색 시 체크할 최대 위치 수.\n많을수록 AOE 타겟팅이 정확하지만 느려집니다.\n(영향: AOE 능력 타겟팅)" }
            },
            ["MaxClusters"] = new() {
                { Language.English, "Max Enemy Clusters" },
                { Language.Korean, "최대 클러스터 수" }
            },
            ["MaxClustersDesc"] = new() {
                { Language.English, "How many enemy groups to track for AOE opportunities.\nMore = finds more AOE chances, but slower.\n(Affects: AOE ability decisions)" },
                { Language.Korean, "AOE 기회 탐색을 위해 추적할 적 그룹 수.\n많을수록 AOE 기회를 더 많이 찾지만 느려집니다.\n(영향: AOE 능력 결정)" }
            },
            ["MaxTilesPerEnemy"] = new() {
                { Language.English, "Max Tiles per Enemy" },
                { Language.Korean, "적당 최대 타일 수" }
            },
            ["MaxTilesPerEnemyDesc"] = new() {
                { Language.English, "Movement tiles to analyze per enemy for threat prediction.\nMore = precise threat zones, but slower.\n(Affects: Predictive movement, safe positioning)" },
                { Language.Korean, "적 위협 예측을 위해 분석할 이동 타일 수.\n많을수록 위협 구역 예측이 정밀하지만 느려집니다.\n(영향: 예측적 이동, 안전 위치 선정)" }
            },
            ["ResetPerformanceToDefault"] = new() {
                { Language.English, "Reset Performance to Default" },
                { Language.Korean, "성능 설정 기본값으로" }
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
        Auto,       // ★ v3.0.92: Automatically detect optimal role based on abilities
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
        PreferRanged    // Keep distance from enemies
    }

    /// <summary>
    /// Settings for individual character
    /// </summary>
    public class CharacterSettings
    {
        public string CharacterId { get; set; } = "";
        public string CharacterName { get; set; } = "";
        public bool EnableCustomAI { get; set; } = false;
        public AIRole Role { get; set; } = AIRole.Auto;
        public RangePreference RangePreference { get; set; } = RangePreference.Adaptive;

        // Combat behavior
        public bool UseBuffsBeforeAttack { get; set; } = true;
        public bool FinishLowHPEnemies { get; set; } = true;
        public bool AvoidFriendlyFire { get; set; } = true;
        public int MinEnemiesForAoE { get; set; } = 2;

        // Movement behavior
        public bool AllowRetreat { get; set; } = true;
        public bool SeekCover { get; set; } = true;
        // ★ v3.1.29: 기본값 7m로 증가 (근접 무기 사거리 3-5m 고려, 여유 확보)
        public float MinSafeDistance { get; set; } = 7.0f;

        // Resource management
        public bool ConserveAmmo { get; set; } = false;
        public int HealAtHPPercent { get; set; } = 50;

        // ★ v3.2.30: 킬 시뮬레이터 토글 (다중 능력 조합으로 확정 킬 탐색)
        public bool UseKillSimulator { get; set; } = true;

        // ★ v3.3.00: AOE 클러스터 최적화 토글
        public bool UseAoEOptimization { get; set; } = true;

        // ★ v3.4.00: 예측적 이동 토글 (적 이동 예측하여 안전 위치 선택)
        public bool UsePredictiveMovement { get; set; } = true;
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

        #region ★ v3.5.20: Performance Settings (Global)

        /// <summary>
        /// 위협 예측 시 분석할 최대 적 수
        /// 높을수록 정확하지만 느림
        /// </summary>
        public int MaxEnemiesToAnalyze { get; set; } = 8;

        /// <summary>
        /// AOE 최적 위치 탐색 시 체크할 최대 위치 수
        /// 높을수록 AOE 타겟팅 정확, 느림
        /// </summary>
        public int MaxPositionsToEvaluate { get; set; } = 25;

        /// <summary>
        /// AOE 기회 탐색을 위해 추적할 최대 클러스터 수
        /// 높을수록 AOE 기회 많이 찾음, 느림
        /// </summary>
        public int MaxClusters { get; set; } = 5;

        /// <summary>
        /// 적 위협 예측을 위해 분석할 이동 타일 수
        /// 높을수록 위협 구역 정밀, 느림
        /// </summary>
        public int MaxTilesPerEnemy { get; set; } = 100;

        #endregion
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
                    HealAtHPPercent = DefaultSettings.HealAtHPPercent,
                    UseKillSimulator = DefaultSettings.UseKillSimulator,  // ★ v3.2.30
                    UseAoEOptimization = DefaultSettings.UseAoEOptimization,  // ★ v3.3.00
                    UsePredictiveMovement = DefaultSettings.UsePredictiveMovement  // ★ v3.4.00
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
                    }
                    else
                    {
                        Main.Log("Using default settings");
                        Instance = new ModSettings();
                    }
                }
                else
                {
                    // ★ v3.5.21: 설정 파일이 없으면 기본값으로 자동 생성
                    Main.Log("Settings file not found, creating default settings.json");
                    Instance = new ModSettings();
                    Save();  // 기본 설정 파일 생성
                }
            }
            catch (Exception ex)
            {
                Main.LogError($"Failed to load settings: {ex.Message}");
                Instance = new ModSettings();
            }

            // ★ v3.1.30: AI 설정 로드 (Response Curves, Role 가중치 등)
            AIConfig.Load(modEntry.Path);
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
