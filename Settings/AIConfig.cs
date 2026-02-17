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
        public float SelfDamageMinHP { get; set; } = 80f;

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

        #region ★ v3.5.40: 위협 평가 가중치 (추정/추측 금지 원칙)

        /// <summary>
        /// Lethality 가중치: 적 HP 기반 위협도
        /// 높을수록 만피 적이 더 위협적
        /// </summary>
        [JsonProperty("lethalityWeight")]
        public float LethalityWeight { get; set; } = 0.3f;

        /// <summary>
        /// Proximity 가중치: 거리 기반 위협도
        /// 높을수록 가까운 적이 더 위협적
        /// </summary>
        [JsonProperty("proximityWeight")]
        public float ProximityWeight { get; set; } = 0.4f;

        /// <summary>
        /// Healer 역할 보너스: 힐러 적 추가 위협도
        /// </summary>
        [JsonProperty("healerRoleBonus")]
        public float HealerRoleBonus { get; set; } = 0.15f;

        /// <summary>
        /// Caster 역할 보너스: 캐스터 적 추가 위협도
        /// </summary>
        [JsonProperty("casterRoleBonus")]
        public float CasterRoleBonus { get; set; } = 0.1f;

        /// <summary>
        /// 원거리 무기 보너스: 원거리 무기 적 추가 위협도
        /// </summary>
        [JsonProperty("rangedWeaponBonus")]
        public float RangedWeaponBonus { get; set; } = 0.05f;

        /// <summary>
        /// 위협 평가 최대 거리 (정규화 기준)
        /// </summary>
        [JsonProperty("threatMaxDistance")]
        public float ThreatMaxDistance { get; set; } = 30f;

        #endregion

        #region ★ v3.5.76: AOE 설정

        /// <summary>AOE 스코어링 및 제한 설정</summary>
        [JsonProperty("aoe")]
        public AoEConfig AoE { get; set; } = new AoEConfig();

        #endregion
    }

    /// <summary>
    /// ★ v3.5.76: AOE 스코어링 및 제한 설정
    /// 하드코딩된 AOE 제한을 외부화하여 사용자 조정 가능
    /// </summary>
    [Serializable]
    public class AoEConfig
    {
        /// <summary>적 1명 타격 시 기본 점수</summary>
        [JsonProperty("enemyHitScore")]
        public float EnemyHitScore { get; set; } = 10000f;

        /// <summary>
        /// 플레이어 파티 아군 피격 페널티 배수
        /// 기존 3.0 → 2.0으로 완화 (적 2명이면 아군 1명 피격 허용)
        /// </summary>
        [JsonProperty("playerAllyPenaltyMultiplier")]
        public float PlayerAllyPenaltyMultiplier { get; set; } = 2.0f;

        /// <summary>NPC 아군 피격 페널티 배수</summary>
        [JsonProperty("npcAllyPenaltyMultiplier")]
        public float NpcAllyPenaltyMultiplier { get; set; } = 1.0f;

        /// <summary>캐스터 자신 피격 페널티 배수</summary>
        [JsonProperty("casterSelfPenaltyMultiplier")]
        public float CasterSelfPenaltyMultiplier { get; set; } = 2.0f;

        /// <summary>
        /// 플레이어 파티 아군 최대 피격 허용 수
        /// 이 수를 초과하면 AOE 사용 거부
        /// </summary>
        [JsonProperty("maxPlayerAlliesHit")]
        public int MaxPlayerAlliesHit { get; set; } = 1;

        /// <summary>
        /// ★ v3.8.94: MaxPlayerAlliesHit로 통합 — 별도 Self-AoE 제한 불필요
        /// JSON 하위 호환용으로만 유지
        /// </summary>
        [JsonProperty("selfAoeMaxAdjacentAllies")]
        [Obsolete("v3.8.94: MaxPlayerAlliesHit로 통합")]
        public int SelfAoeMaxAdjacentAllies { get; set; } = 1;

        /// <summary>Self-AoE 사용에 필요한 최소 인접 적 수</summary>
        [JsonProperty("selfAoeMinAdjacentEnemies")]
        public int SelfAoeMinAdjacentEnemies { get; set; } = 1;

        /// <summary>
        /// ★ v3.8.94: MaxPlayerAlliesHit로 통합 — 별도 Melee AoE 제한 불필요
        /// JSON 하위 호환용으로만 유지
        /// </summary>
        [JsonProperty("meleeAoeMaxAdjacentAllies")]
        [Obsolete("v3.8.94: MaxPlayerAlliesHit로 통합")]
        public int MeleeAoeMaxAdjacentAllies { get; set; } = 1;

        /// <summary>
        /// ★ v3.8.94: DangerousAoE 필터 제거 — 모든 AoE는 MaxPlayerAlliesHit로 통합 관리
        /// JSON 하위 호환용으로만 유지
        /// </summary>
        [JsonProperty("allowDangerousAoEAutoSelect")]
        [Obsolete("v3.8.94: DangerousAoE 필터 제거, MaxPlayerAlliesHit로 통합")]
        public bool AllowDangerousAoEAutoSelect { get; set; } = false;

        /// <summary>
        /// ★ v3.8.94: MinEnemiesForAoE(CharacterSettings)로 통합
        /// JSON 하위 호환용으로만 유지
        /// </summary>
        [JsonProperty("dangerousAoEMinEnemies")]
        [Obsolete("v3.8.94: CharacterSettings.MinEnemiesForAoE로 통합")]
        public int DangerousAoEMinEnemies { get; set; } = 3;

        /// <summary>
        /// ★ v3.5.76: 클러스터 최소 크기
        /// AOE 타겟팅 시 유효한 클러스터로 인정되는 최소 적 수
        /// 1로 설정 시 단일 적도 클러스터로 취급 (AOE 완화)
        /// </summary>
        [JsonProperty("minClusterSize")]
        public int MinClusterSize { get; set; } = 2;

        /// <summary>
        /// ★ v3.8.94: 허용 범위 내 감점 제거 — MaxPlayerAlliesHit 초과 시만 차단
        /// JSON 하위 호환용으로만 유지
        /// </summary>
        [JsonProperty("clusterAllyPenalty")]
        [Obsolete("v3.8.94: 허용 범위 내 감점 제거, MaxPlayerAlliesHit 초과 시만 차단")]
        public float ClusterAllyPenalty { get; set; } = 40f;

        /// <summary>
        /// 클러스터 내 NPC 아군 페널티 점수
        /// </summary>
        [JsonProperty("clusterNpcAllyPenalty")]
        public float ClusterNpcAllyPenalty { get; set; } = 20f;

        // ★ v3.6.15: DangerousAoEAllyPenalty, AoEAllyCheckRadius 삭제
        // 이제 실제 AOE 반경을 사용하고 아군 있으면 무조건 차단
    }

    /// <summary>
    /// ★ v3.8.46: 스코어링 가중치 외부화
    /// UtilityScorer, TargetScorer 등의 하드코딩 가중치를 JSON 설정 가능하게 함
    /// </summary>
    [Serializable]
    public class ScoringConfig
    {
        // ========================================
        // Phase 배율 (ScoreBuff)
        // ========================================

        /// <summary>초반 페이즈 버프 배율</summary>
        [JsonProperty("openingPhaseBuffMult")] public float OpeningPhaseBuffMult { get; set; } = 1.3f;

        /// <summary>정리 페이즈 버프 배율</summary>
        [JsonProperty("cleanupPhaseBuffMult")] public float CleanupPhaseBuffMult { get; set; } = 0.7f;

        /// <summary>위기 페이즈 비방어 버프 배율</summary>
        [JsonProperty("desperateNonDefMult")] public float DesperateNonDefMult { get; set; } = 0.5f;

        // ========================================
        // 타이밍 보너스 (ScoreBuff)
        // ========================================

        /// <summary>선제 버프 - 초반 보너스</summary>
        [JsonProperty("preCombatOpeningBonus")] public float PreCombatOpeningBonus { get; set; } = 30f;

        /// <summary>선제 버프 - 정리 페널티</summary>
        [JsonProperty("preCombatCleanupPenalty")] public float PreCombatCleanupPenalty { get; set; } = 20f;

        /// <summary>공격 전 버프 - 적 타격 가능 보너스</summary>
        [JsonProperty("preAttackHittableBonus")] public float PreAttackHittableBonus { get; set; } = 25f;

        /// <summary>공격 전 버프 - 적 부재 페널티</summary>
        [JsonProperty("preAttackNoEnemyPenalty")] public float PreAttackNoEnemyPenalty { get; set; } = 10f;

        /// <summary>긴급 버프 - 위기 상황 보너스</summary>
        [JsonProperty("emergencyDesperateBonus")] public float EmergencyDesperateBonus { get; set; } = 40f;

        /// <summary>긴급 버프 - 비위기 페널티</summary>
        [JsonProperty("emergencyNonDesperatePenalty")] public float EmergencyNonDesperatePenalty { get; set; } = 20f;

        /// <summary>도발 - 근접 적 다수 보너스</summary>
        [JsonProperty("tauntNearEnemiesBonus")] public float TauntNearEnemiesBonus { get; set; } = 25f;

        /// <summary>도발 - 적 부족 페널티</summary>
        [JsonProperty("tauntFewEnemiesPenalty")] public float TauntFewEnemiesPenalty { get; set; } = 15f;

        // ========================================
        // 시너지 (CalculateSynergyBonus)
        // ========================================

        /// <summary>공격 버프 + 공격 시너지</summary>
        [JsonProperty("buffAttackSynergy")] public float BuffAttackSynergy { get; set; } = 25f;

        /// <summary>이동 + 공격 시너지 (갭클로저)</summary>
        [JsonProperty("moveAttackSynergy")] public float MoveAttackSynergy { get; set; } = 10f;

        /// <summary>연속 공격 시너지 (공격당)</summary>
        [JsonProperty("multiAttackPerAttack")] public float MultiAttackPerAttack { get; set; } = 10f;

        /// <summary>★ v3.8.46: 방어 버프 + 이동 시너지</summary>
        [JsonProperty("defenseRetreatSynergy")] public float DefenseRetreatSynergy { get; set; } = 15f;

        /// <summary>★ v3.8.46: 킬 확정 시너지 (플랜 데미지 ≥ 타겟 HP)</summary>
        [JsonProperty("killConfirmSynergy")] public float KillConfirmSynergy { get; set; } = 30f;

        /// <summary>★ v3.8.46: 거의 킬 시너지 (플랜 데미지 ≥ 90% HP)</summary>
        [JsonProperty("almostKillSynergy")] public float AlmostKillSynergy { get; set; } = 15f;

        // ========================================
        // 공격 점수 (ScoreAttack)
        // ========================================

        /// <summary>ClearMP + 위험 상황 기본 감점</summary>
        [JsonProperty("clearMPDangerBase")] public float ClearMPDangerBase { get; set; } = 60f;

        /// <summary>AOE 추가 적당 보너스</summary>
        [JsonProperty("aoeBonusPerEnemy")] public float AoEBonusPerEnemy { get; set; } = 15f;

        // ========================================
        // 타겟 관성 (P1)
        // ========================================

        /// <summary>이전 턴 동일 타겟 공격 보너스 (SharedTarget +50보다 낮게 유지)</summary>
        [JsonProperty("inertiaBonus")] public float InertiaBonus { get; set; } = 20f;

        // ========================================
        // 디버프 활용 (P3)
        // ========================================

        /// <summary>Hard CC (기절/고정) 적 공격 보너스</summary>
        [JsonProperty("hardCCExploitBonus")] public float HardCCExploitBonus { get; set; } = 15f;

        /// <summary>DOT (출혈/독/화상) 적 공격 보너스</summary>
        [JsonProperty("dotFollowUpBonus")] public float DOTFollowUpBonus { get; set; } = 8f;
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

        /// <summary>★ v3.8.46: 스코어링 가중치 설정</summary>
        [JsonProperty("scoring")]
        public ScoringConfig Scoring { get; set; }

        /// <summary>
        /// 싱글톤 인스턴스
        /// </summary>
        public static AIConfig Instance { get; private set; }

        /// <summary>
        /// ★ v3.8.12: 모드 경로 (Save용)
        /// </summary>
        private static string _modPath = null;

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
                Performance = new PerformanceConfig(),
                Scoring = new ScoringConfig()
            };
        }

        /// <summary>
        /// JSON 파일에서 설정 로드
        /// </summary>
        public static void Load(string modPath)
        {
            _modPath = modPath;  // ★ v3.8.12: 경로 저장
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
        /// ★ v3.8.12: 현재 설정을 aiconfig.json으로 저장 (UI에서 호출)
        /// </summary>
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
                if (Instance == null)
                    Instance = CreateDefault();

                string json = JsonConvert.SerializeObject(Instance, Formatting.Indented);
                File.WriteAllText(configPath, json);
                Main.LogDebug("[AIConfig] Settings saved to aiconfig.json");
            }
            catch (Exception ex)
            {
                Main.LogError($"[AIConfig] Failed to save: {ex.Message}");
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

        /// <summary>★ v3.5.76: AOE 설정 (null-safe)</summary>
        public static AoEConfig GetAoEConfig()
        {
            return Instance?.Thresholds?.AoE ?? new AoEConfig();
        }

        /// <summary>성능 설정 (null-safe)</summary>
        public static PerformanceConfig GetPerformance()
        {
            return Instance?.Performance ?? new PerformanceConfig();
        }

        /// <summary>★ v3.8.46: 스코어링 가중치 (null-safe)</summary>
        public static ScoringConfig GetScoringConfig()
        {
            return Instance?.Scoring ?? new ScoringConfig();
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
