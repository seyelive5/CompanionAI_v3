using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using CompanionAI_v3.Analysis;

namespace CompanionAI_v3.Settings
{
    /// <summary>
    /// ★ v3.1.30: JSON 설정 가능한 곡선 구성
    /// </summary>
    [Serializable]
    public class CurveConfig
    {
        [JsonProperty("type")]
        public string Type { get; set; } = "Linear";

        [JsonProperty("minInput")]
        public float MinInput { get; set; } = 0f;

        [JsonProperty("maxInput")]
        public float MaxInput { get; set; } = 1f;

        [JsonProperty("minOutput")]
        public float MinOutput { get; set; } = 0f;

        [JsonProperty("maxOutput")]
        public float MaxOutput { get; set; } = 100f;

        [JsonProperty("steepness")]
        public float Steepness { get; set; } = 10f;

        [JsonProperty("midpoint")]
        public float Midpoint { get; set; } = 0.5f;

        [JsonProperty("exponent")]
        public float Exponent { get; set; } = 2f;

        /// <summary>
        /// JSON 설정을 ResponseCurve로 변환
        /// </summary>
        public ResponseCurve ToCurve()
        {
            CurveType curveType;
            if (!Enum.TryParse(Type, true, out curveType))
            {
                Main.LogError($"[AIConfig] Unknown curve type: {Type}, defaulting to Linear");
                curveType = CurveType.Linear;
            }

            return new ResponseCurve
            {
                Type = curveType,
                MinInput = MinInput,
                MaxInput = MaxInput,
                MinOutput = MinOutput,
                MaxOutput = MaxOutput,
                Steepness = Steepness,
                Midpoint = Midpoint,
                Exponent = Exponent
            };
        }
    }

    /// <summary>
    /// Role별 타겟 스코어링 가중치
    /// </summary>
    [Serializable]
    public class RoleWeights
    {
        /// <summary>HP% 가중치 (낮은 HP 우선)</summary>
        [JsonProperty("hpPercent")]
        public float HPPercent { get; set; } = 0.5f;

        /// <summary>위협도 가중치</summary>
        [JsonProperty("threat")]
        public float Threat { get; set; } = 0.3f;

        /// <summary>거리 가중치</summary>
        [JsonProperty("distance")]
        public float Distance { get; set; } = 0.2f;

        /// <summary>마무리 보너스 가중치</summary>
        [JsonProperty("finisherBonus")]
        public float FinisherBonus { get; set; } = 1.5f;

        /// <summary>1타킬 보너스 가중치</summary>
        [JsonProperty("oneHitKillBonus")]
        public float OneHitKillBonus { get; set; } = 2.0f;
    }

    /// <summary>
    /// ★ v3.5.00: 임계값 설정 (매직 넘버 외부화)
    /// PDF 방법론: 하드코딩된 임계값을 JSON 설정으로 외부화
    /// </summary>
    [Serializable]
    public class ThresholdConfig
    {
        /// <summary>긴급 힐 HP% 기준</summary>
        [JsonProperty("emergencyHealHP")]
        public float EmergencyHealHP { get; set; } = 30f;

        /// <summary>마무리 타겟 HP% 기준</summary>
        [JsonProperty("finisherTargetHP")]
        public float FinisherTargetHP { get; set; } = 30f;

        /// <summary>힐 우선순위 HP% 기준</summary>
        [JsonProperty("healPriorityHP")]
        public float HealPriorityHP { get; set; } = 50f;

        /// <summary>버프 스킵 HP% 기준 (HP가 이보다 낮으면 버프 스킵)</summary>
        [JsonProperty("skipBuffBelowHP")]
        public float SkipBuffBelowHP { get; set; } = 40f;

        /// <summary>안전 거리 (원거리 캐릭터용, 미터)</summary>
        [JsonProperty("safeDistance")]
        public float SafeDistance { get; set; } = 7f;

        /// <summary>위험 적 거리 (이보다 가까우면 위험)</summary>
        [JsonProperty("dangerDistance")]
        public float DangerDistance { get; set; } = 5f;

        /// <summary>1타킬 데미지/HP 비율 기준</summary>
        [JsonProperty("oneHitKillRatio")]
        public float OneHitKillRatio { get; set; } = 0.95f;

        /// <summary>2타킬 데미지/HP 비율 기준</summary>
        [JsonProperty("twoHitKillRatio")]
        public float TwoHitKillRatio { get; set; } = 0.5f;

        #region ★ v3.5.00: 추가 임계값 (PDF 방법론)

        /// <summary>절박한 상황 판정 - 팀 평균 HP% 기준</summary>
        [JsonProperty("desperatePhaseHP")]
        public float DesperatePhaseHP { get; set; } = 35f;

        /// <summary>절박한 상황 판정 - 자신 HP% 기준</summary>
        [JsonProperty("desperateSelfHP")]
        public float DesperateSelfHP { get; set; } = 25f;

        /// <summary>정리 단계 판정 - 남은 적 수 기준</summary>
        [JsonProperty("cleanupEnemyCount")]
        public int CleanupEnemyCount { get; set; } = 2;

        /// <summary>자해 스킬 사용 최소 HP% (이 이상이어야 사용)</summary>
        [JsonProperty("selfDamageMinHP")]
        public float SelfDamageMinHP { get; set; } = 70f;

        /// <summary>위협 근접 거리 (미터)</summary>
        [JsonProperty("threatProximity")]
        public float ThreatProximity { get; set; } = 5f;

        /// <summary>
        /// 힐 우선순위 다단계 HP% 기준
        /// [0]: 최우선 (기본 25%)
        /// [1]: 높음 (기본 50%)
        /// [2]: 보통 (기본 75%)
        /// </summary>
        [JsonProperty("healPriorityThresholds")]
        public float[] HealPriorityThresholds { get; set; } = { 25f, 50f, 75f };

        /// <summary>위협 HP% 기준 (이 이하면 위협도 감소)</summary>
        [JsonProperty("lowThreatHP")]
        public float LowThreatHP { get; set; } = 30f;

        /// <summary>개막 단계 최소 AP</summary>
        [JsonProperty("openingPhaseMinAP")]
        public float OpeningPhaseMinAP { get; set; } = 3f;

        /// <summary>PreAttackBuff 사용 가능 최소 HP%</summary>
        [JsonProperty("preAttackBuffMinHP")]
        public float PreAttackBuffMinHP { get; set; } = 50f;

        #endregion
    }

    /// <summary>
    /// 성능 관련 설정
    /// </summary>
    [Serializable]
    public class PerformanceConfig
    {
        /// <summary>최대 평가 타겟 수</summary>
        [JsonProperty("maxTargetsToEvaluate")]
        public int MaxTargetsToEvaluate { get; set; } = 10;

        /// <summary>최대 평가 위치 수</summary>
        [JsonProperty("maxPositionsToEvaluate")]
        public int MaxPositionsToEvaluate { get; set; } = 20;

        /// <summary>조기 종료 활성화</summary>
        [JsonProperty("enableEarlyExit")]
        public bool EnableEarlyExit { get; set; } = true;

        /// <summary>캐싱 활성화</summary>
        [JsonProperty("enableCaching")]
        public bool EnableCaching { get; set; } = true;
    }

    /// <summary>
    /// ★ v3.1.30: AI 설정 메인 클래스
    /// aiconfig.json에서 로드
    /// </summary>
    [Serializable]
    public class AIConfig
    {
        /// <summary>설정 버전 (호환성 체크)</summary>
        [JsonProperty("version")]
        public string Version { get; set; } = "1.0";

        /// <summary>Response Curve 설정</summary>
        [JsonProperty("curves")]
        public Dictionary<string, CurveConfig> Curves { get; set; }

        /// <summary>DPS Role 가중치</summary>
        [JsonProperty("dps")]
        public RoleWeights DPS { get; set; }

        /// <summary>Tank Role 가중치</summary>
        [JsonProperty("tank")]
        public RoleWeights Tank { get; set; }

        /// <summary>Support Role 가중치</summary>
        [JsonProperty("support")]
        public RoleWeights Support { get; set; }

        /// <summary>임계값 설정</summary>
        [JsonProperty("thresholds")]
        public ThresholdConfig Thresholds { get; set; }

        /// <summary>성능 설정</summary>
        [JsonProperty("performance")]
        public PerformanceConfig Performance { get; set; }

        /// <summary>
        /// 싱글톤 인스턴스
        /// </summary>
        public static AIConfig Instance { get; private set; }

        /// <summary>
        /// 기본값으로 초기화된 새 인스턴스 생성
        /// </summary>
        public static AIConfig CreateDefault()
        {
            return new AIConfig
            {
                Version = "1.0",
                Curves = new Dictionary<string, CurveConfig>(),
                DPS = new RoleWeights
                {
                    HPPercent = 0.8f,
                    Threat = 0.4f,
                    Distance = 0.2f,
                    FinisherBonus = 1.5f,
                    OneHitKillBonus = 2.0f
                },
                Tank = new RoleWeights
                {
                    HPPercent = 0.3f,
                    Threat = 0.6f,
                    Distance = 0.4f,
                    FinisherBonus = 1.0f,
                    OneHitKillBonus = 1.2f
                },
                Support = new RoleWeights
                {
                    HPPercent = 0.5f,
                    Threat = 0.3f,
                    Distance = 0.3f,
                    FinisherBonus = 1.2f,
                    OneHitKillBonus = 1.5f
                },
                Thresholds = new ThresholdConfig(),
                Performance = new PerformanceConfig()
            };
        }

        /// <summary>
        /// JSON 파일에서 설정 로드
        /// </summary>
        public static void Load(string modPath)
        {
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

                        // Response Curves에 커스텀 설정 적용
                        if (config.Curves != null && config.Curves.Count > 0)
                        {
                            CurvePresets.LoadFromConfig(config.Curves);
                        }

                        Main.Log($"[AIConfig] Loaded from {configPath} (v{config.Version})");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Main.LogError($"[AIConfig] Failed to load: {ex.Message}");
            }

            // ★ v3.5.21: 기본 설정 사용 및 파일 자동 생성
            Instance = CreateDefault();
            SaveDefault(modPath);
            Main.Log("[AIConfig] Created default aiconfig.json");
        }

        /// <summary>
        /// ★ v3.5.21: 기본 설정을 aiconfig.json으로 저장
        /// </summary>
        public static void SaveDefault(string modPath)
        {
            string configPath = Path.Combine(modPath, "aiconfig.json");

            try
            {
                if (Instance == null)
                    Instance = CreateDefault();

                string json = JsonConvert.SerializeObject(Instance, Formatting.Indented);
                File.WriteAllText(configPath, json);
            }
            catch (Exception ex)
            {
                Main.LogError($"[AIConfig] Failed to save default: {ex.Message}");
            }
        }

        /// <summary>
        /// 설정 파일 저장 (기본 템플릿 생성용)
        /// </summary>
        public static void SaveTemplate(string modPath)
        {
            string configPath = Path.Combine(modPath, "aiconfig.template.json");

            try
            {
                var template = CreateDefault();

                // 예시 커브 설정 추가
                template.Curves = new Dictionary<string, CurveConfig>
                {
                    ["damageRatio"] = new CurveConfig
                    {
                        Type = "Logistic",
                        MinInput = 0f,
                        MaxInput = 1.5f,
                        MinOutput = 0f,
                        MaxOutput = 100f,
                        Steepness = 8f,
                        Midpoint = 0.5f
                    },
                    ["healUrgency"] = new CurveConfig
                    {
                        Type = "InverseLogistic",
                        MinInput = 0f,
                        MaxInput = 100f,
                        MinOutput = -30f,
                        MaxOutput = 80f,
                        Steepness = 0.15f,
                        Midpoint = 0.4f
                    }
                };

                string json = JsonConvert.SerializeObject(template, Formatting.Indented);
                File.WriteAllText(configPath, json);
                Main.Log($"[AIConfig] Template saved to {configPath}");
            }
            catch (Exception ex)
            {
                Main.LogError($"[AIConfig] Failed to save template: {ex.Message}");
            }
        }

        #region Convenience Accessors

        /// <summary>임계값 설정 (null-safe)</summary>
        public static ThresholdConfig GetThresholds()
        {
            return Instance?.Thresholds ?? new ThresholdConfig();
        }

        /// <summary>성능 설정 (null-safe)</summary>
        public static PerformanceConfig GetPerformance()
        {
            return Instance?.Performance ?? new PerformanceConfig();
        }

        /// <summary>Role별 가중치 (null-safe)</summary>
        public static RoleWeights GetRoleWeights(AIRole role)
        {
            if (Instance == null)
                return new RoleWeights();

            switch (role)
            {
                case AIRole.Tank:
                    return Instance.Tank ?? new RoleWeights();
                case AIRole.Support:
                    return Instance.Support ?? new RoleWeights();
                case AIRole.DPS:
                default:
                    return Instance.DPS ?? new RoleWeights();
            }
        }

        #endregion
    }
}
