using System;
using System.IO;
using Newtonsoft.Json;

namespace CompanionAI_v3.Settings
{
    /// <summary>
    /// ★ v3.20.0: 사용자 노출 AOE 설정
    /// - MaxPlayerAlliesHit : 아군 최대 피격 허용 수 (0=아군 피격 완전 차단)
    /// - MinClusterSize     : AoE 클러스터 최소 적 수 (1=단일 적도 AoE 허용)
    /// - SelfAoeMinAdjacentEnemies : Self-AoE 최소 인접 적 수
    ///
    /// 내부 AoE 가중치(EnemyHitScore, PenaltyMultiplier 등)는 SC.cs 참조
    /// </summary>
    [Serializable]
    public class AoEConfig
    {
        [JsonProperty("maxPlayerAlliesHit")]
        public int MaxPlayerAlliesHit { get; set; } = 0;

        [JsonProperty("minClusterSize")]
        public int MinClusterSize { get; set; } = 2;

        [JsonProperty("selfAoeMinAdjacentEnemies")]
        public int SelfAoeMinAdjacentEnemies { get; set; } = 1;
    }

    /// <summary>
    /// ★ v3.20.0: 사용자 노출 무기 로테이션 설정
    /// - MaxSwitchesPerTurn : 턴당 최대 무기 전환 횟수
    ///
    /// 내부 상수(MinEnemiesForAlternateAoE 등)는 SC.cs 참조
    /// </summary>
    [Serializable]
    public class WeaponRotationConfig
    {
        [JsonProperty("maxSwitchesPerTurn")]
        public int MaxSwitchesPerTurn { get; set; } = 2;
    }

    /// <summary>
    /// ★ v3.20.0: 사용자 영구 설정 (aiconfig.json)
    ///
    /// 설계 원칙:
    ///   - 여기 있는 값은 사용자가 의도적으로 바꾸는 것 → 업데이트 후에도 유지되어야 함
    ///   - 개발자 튜닝 상수(스코어링 가중치, 임계값 등)는 SC.cs로 이동 → 항상 최신값 적용
    /// </summary>
    [Serializable]
    public class AIConfig
    {
        [JsonProperty("aoe")]
        public AoEConfig AoE { get; set; } = new AoEConfig();

        [JsonProperty("weaponRotation")]
        public WeaponRotationConfig WeaponRotation { get; set; } = new WeaponRotationConfig();

        public static AIConfig Instance { get; private set; }

        private static string _modPath;

        public static AIConfig CreateDefault()
        {
            return new AIConfig
            {
                AoE = new AoEConfig(),
                WeaponRotation = new WeaponRotationConfig()
            };
        }

        public static void Load(string modPath)
        {
            _modPath = modPath;
            string configPath = Path.Combine(modPath, "aiconfig.json");

            try
            {
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    var config = JsonConvert.DeserializeObject<AIConfig>(json);

                    if (config != null)
                    {
                        Instance = config;

                        // null 보호 (구버전 JSON 호환 또는 필드 누락 시)
                        if (Instance.AoE == null) Instance.AoE = new AoEConfig();
                        if (Instance.WeaponRotation == null) Instance.WeaponRotation = new WeaponRotationConfig();

                        Main.Log($"[AIConfig] Loaded from {configPath}");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Main.LogError($"[AIConfig] Failed to load: {ex.Message}");
            }

            Instance = CreateDefault();
            Save();
            Main.Log("[AIConfig] Created default aiconfig.json");
        }

        public static void Save()
        {
            if (string.IsNullOrEmpty(_modPath))
            {
                Main.LogError("[AIConfig] Cannot save - modPath not set");
                return;
            }

            string configPath = Path.Combine(_modPath, "aiconfig.json");

            try
            {
                if (Instance == null) Instance = CreateDefault();
                string json = JsonConvert.SerializeObject(Instance, Formatting.Indented);
                File.WriteAllText(configPath, json);
                Main.LogDebug("[AIConfig] Settings saved to aiconfig.json");
            }
            catch (Exception ex)
            {
                Main.LogError($"[AIConfig] Failed to save: {ex.Message}");
            }
        }

        /// <summary>AOE 설정 (null-safe)</summary>
        public static AoEConfig GetAoEConfig()
        {
            return Instance?.AoE ?? new AoEConfig();
        }

        /// <summary>무기 로테이션 설정 (null-safe)</summary>
        public static WeaponRotationConfig GetWeaponRotationConfig()
        {
            return Instance?.WeaponRotation ?? new WeaponRotationConfig();
        }
    }
}
