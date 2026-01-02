using System;
using System.Collections.Generic;
using UnityEngine;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Analysis
{
    /// <summary>
    /// ★ v3.1.30: Response Curve 시스템
    /// 매직 넘버를 부드러운 곡선으로 대체하여 자연스러운 AI 의사결정
    /// 참조: Dave Mark의 IAUS (Infinite Axis Utility System)
    /// </summary>
    public enum CurveType
    {
        /// <summary>선형: y = mx + b</summary>
        Linear,

        /// <summary>2차: y = ax^2 + bx + c</summary>
        Quadratic,

        /// <summary>시그모이드: y = 1 / (1 + e^(-k*(x-mid)))</summary>
        Logistic,

        /// <summary>다항식: y = x^n</summary>
        Polynomial,

        /// <summary>지수: y = a * e^(bx)</summary>
        Exponential,

        /// <summary>반전 시그모이드: 1 - Logistic</summary>
        InverseLogistic
    }

    /// <summary>
    /// Response Curve 정의
    /// 입력값을 정규화하고 곡선을 적용하여 출력값 생성
    /// </summary>
    [Serializable]
    public class ResponseCurve
    {
        public CurveType Type { get; set; } = CurveType.Linear;

        /// <summary>입력 최소값 (정규화 기준)</summary>
        public float MinInput { get; set; } = 0f;

        /// <summary>입력 최대값 (정규화 기준)</summary>
        public float MaxInput { get; set; } = 1f;

        /// <summary>출력 최소값</summary>
        public float MinOutput { get; set; } = 0f;

        /// <summary>출력 최대값</summary>
        public float MaxOutput { get; set; } = 100f;

        /// <summary>Logistic 곡선의 가파름 (높을수록 급격한 전환)</summary>
        public float Steepness { get; set; } = 10f;

        /// <summary>Logistic 곡선의 중심점 (전환이 일어나는 지점)</summary>
        public float Midpoint { get; set; } = 0.5f;

        /// <summary>Polynomial 곡선의 지수</summary>
        public float Exponent { get; set; } = 2f;

        /// <summary>Linear 곡선의 기울기 (자동 계산 가능)</summary>
        public float Slope { get; set; } = 1f;

        /// <summary>Linear 곡선의 y절편 (자동 계산 가능)</summary>
        public float Intercept { get; set; } = 0f;

        public ResponseCurve() { }

        public ResponseCurve(CurveType type, float minInput, float maxInput, float minOutput, float maxOutput)
        {
            Type = type;
            MinInput = minInput;
            MaxInput = maxInput;
            MinOutput = minOutput;
            MaxOutput = maxOutput;
        }

        /// <summary>
        /// 입력값을 곡선에 따라 출력값으로 변환
        /// </summary>
        public float Evaluate(float input)
        {
            // 1. 입력 정규화 [0, 1]
            float range = MaxInput - MinInput;
            float normalized = range > 0.001f
                ? Mathf.Clamp01((input - MinInput) / range)
                : 0f;

            // 2. 곡선 적용
            float curved = ApplyCurve(normalized);

            // 3. 출력 범위로 매핑
            return Mathf.Lerp(MinOutput, MaxOutput, curved);
        }

        private float ApplyCurve(float t)
        {
            switch (Type)
            {
                case CurveType.Linear:
                    return t;

                case CurveType.Quadratic:
                    return t * t;

                case CurveType.Logistic:
                    // Sigmoid: 1 / (1 + e^(-k*(t-mid)))
                    // t는 이미 [0,1]로 정규화됨
                    float x = (t - Midpoint) * Steepness;
                    return 1f / (1f + Mathf.Exp(-x));

                case CurveType.InverseLogistic:
                    // 반전 Sigmoid: 높은 값에서 낮은 출력
                    float x2 = (t - Midpoint) * Steepness;
                    return 1f - (1f / (1f + Mathf.Exp(-x2)));

                case CurveType.Polynomial:
                    return Mathf.Pow(t, Exponent);

                case CurveType.Exponential:
                    // e^(t*steepness) - 1 정규화
                    if (Steepness <= 0.001f) return t;
                    float expVal = Mathf.Exp(t * Steepness) - 1f;
                    float expMax = Mathf.Exp(Steepness) - 1f;
                    return expMax > 0.001f ? expVal / expMax : t;

                default:
                    return t;
            }
        }

        /// <summary>
        /// 디버그용: 곡선 샘플 출력
        /// </summary>
        public string DebugSample(int samples = 10)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[{Type}] Input({MinInput}-{MaxInput}) -> Output({MinOutput}-{MaxOutput})");

            for (int i = 0; i <= samples; i++)
            {
                float t = (float)i / samples;
                float input = Mathf.Lerp(MinInput, MaxInput, t);
                float output = Evaluate(input);
                sb.AppendLine($"  {input:F2} -> {output:F2}");
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// 미리 정의된 곡선 프리셋
    /// 게임 AI에서 자주 사용되는 패턴들
    /// </summary>
    public static class CurvePresets
    {
        private static bool _initialized = false;
        private static readonly object _lock = new object();

        #region Attack Scoring Curves

        /// <summary>
        /// 데미지/HP 비율 -> 공격 점수
        /// 1타킬 가능(ratio >= 1.0)하면 높은 점수, 20% 데미지면 낮은 점수
        /// </summary>
        public static ResponseCurve DamageRatio { get; private set; }

        /// <summary>
        /// 1타킬 확률 보너스
        /// ratio 0.8 이상에서 급격히 상승
        /// </summary>
        public static ResponseCurve OneHitKillBonus { get; private set; }

        /// <summary>
        /// AP 효율: 데미지/AP 비율 -> 보너스
        /// </summary>
        public static ResponseCurve APEfficiency { get; private set; }

        #endregion

        #region Heal Scoring Curves

        /// <summary>
        /// HP% -> 힐 긴급도
        /// 25% 이하에서 급격히 상승 (Sigmoid)
        /// </summary>
        public static ResponseCurve HealUrgency { get; private set; }

        /// <summary>
        /// 자기 힐 보너스: HP가 낮을수록 자기 힐 우선
        /// </summary>
        public static ResponseCurve SelfHealBonus { get; private set; }

        #endregion

        #region Target Scoring Curves

        /// <summary>
        /// HP% -> 마무리 우선순위
        /// 낮은 HP에서 높은 우선순위 (InverseLogistic)
        /// </summary>
        public static ResponseCurve HPPriority { get; private set; }

        /// <summary>
        /// 거리 -> 위협도
        /// 가까울수록 높은 위협
        /// </summary>
        public static ResponseCurve ThreatByDistance { get; private set; }

        /// <summary>
        /// 거리 -> 타겟 접근성 패널티
        /// 멀수록 낮은 점수
        /// </summary>
        public static ResponseCurve DistancePenalty { get; private set; }

        #endregion

        #region Position Scoring Curves

        /// <summary>
        /// 적과의 거리 -> 안전 점수
        /// 원거리 캐릭터용: 가까우면 위험
        /// </summary>
        public static ResponseCurve SafetyByDistance { get; private set; }

        /// <summary>
        /// 엄폐물 가치
        /// </summary>
        public static ResponseCurve CoverValue { get; private set; }

        /// <summary>
        /// 위협 개수 -> 위치 패널티
        /// </summary>
        public static ResponseCurve ThreatCountPenalty { get; private set; }

        #endregion

        #region Buff Scoring Curves

        /// <summary>
        /// AP 비용 -> 버프 효율
        /// 저비용 버프가 높은 점수
        /// </summary>
        public static ResponseCurve BuffAPCost { get; private set; }

        #endregion

        #region Confidence Curves (v3.2.20)

        /// <summary>
        /// ★ v3.2.20: 팀 신뢰도 -> 공격 적극도
        /// confidence 높을수록 더 공격적 (0.3 ~ 1.5)
        /// </summary>
        public static ResponseCurve ConfidenceToAggression { get; private set; }

        /// <summary>
        /// ★ v3.2.20: 팀 신뢰도 -> 방어 필요도
        /// confidence 낮을수록 방어 필요 (역방향, 0.3 ~ 1.5)
        /// </summary>
        public static ResponseCurve ConfidenceToDefenseNeed { get; private set; }

        #endregion

        /// <summary>
        /// 프리셋 초기화 (모드 로드 시 호출)
        /// </summary>
        public static void Initialize()
        {
            lock (_lock)
            {
                if (_initialized) return;

                // Attack Scoring
                DamageRatio = new ResponseCurve
                {
                    Type = CurveType.Logistic,
                    MinInput = 0f,
                    MaxInput = 1.5f,
                    MinOutput = 0f,
                    MaxOutput = 100f,
                    Steepness = 8f,
                    Midpoint = 0.5f  // 50% 데미지에서 중간 점수
                };

                OneHitKillBonus = new ResponseCurve
                {
                    Type = CurveType.Exponential,
                    MinInput = 0.8f,
                    MaxInput = 1.2f,
                    MinOutput = 0f,
                    MaxOutput = 80f,
                    Steepness = 5f
                };

                APEfficiency = new ResponseCurve
                {
                    Type = CurveType.Polynomial,
                    MinInput = 0f,
                    MaxInput = 50f,  // 데미지/AP
                    MinOutput = 0f,
                    MaxOutput = 30f,
                    Exponent = 0.5f  // 감소하는 수익률
                };

                // Heal Scoring
                HealUrgency = new ResponseCurve
                {
                    Type = CurveType.InverseLogistic,
                    MinInput = 0f,
                    MaxInput = 100f,
                    MinOutput = -30f,  // 풀피면 감점
                    MaxOutput = 80f,   // 위급하면 80점
                    Steepness = 0.15f,
                    Midpoint = 0.4f    // HP 40%에서 전환
                };

                SelfHealBonus = new ResponseCurve
                {
                    Type = CurveType.InverseLogistic,
                    MinInput = 0f,
                    MaxInput = 100f,
                    MinOutput = 0f,
                    MaxOutput = 40f,
                    Steepness = 0.12f,
                    Midpoint = 0.35f
                };

                // Target Scoring
                HPPriority = new ResponseCurve
                {
                    Type = CurveType.InverseLogistic,
                    MinInput = 0f,
                    MaxInput = 100f,
                    MinOutput = 0f,
                    MaxOutput = 50f,
                    Steepness = 0.12f,
                    Midpoint = 0.35f  // HP 35%에서 전환
                };

                ThreatByDistance = new ResponseCurve
                {
                    Type = CurveType.InverseLogistic,
                    MinInput = 0f,
                    MaxInput = 20f,  // 미터
                    MinOutput = 0f,
                    MaxOutput = 30f,
                    Steepness = 0.5f,
                    Midpoint = 0.4f
                };

                DistancePenalty = new ResponseCurve
                {
                    Type = CurveType.Linear,
                    MinInput = 0f,
                    MaxInput = 30f,
                    MinOutput = 0f,
                    MaxOutput = -60f  // 멀수록 감점
                };

                // Position Scoring
                SafetyByDistance = new ResponseCurve
                {
                    Type = CurveType.Quadratic,
                    MinInput = 0f,
                    MaxInput = 15f,
                    MinOutput = -50f,  // 너무 가까우면 위험
                    MaxOutput = 30f
                };

                CoverValue = new ResponseCurve
                {
                    Type = CurveType.Linear,
                    MinInput = 0f,
                    MaxInput = 1f,
                    MinOutput = 0f,
                    MaxOutput = 40f
                };

                ThreatCountPenalty = new ResponseCurve
                {
                    Type = CurveType.Linear,
                    MinInput = 0f,
                    MaxInput = 5f,
                    MinOutput = 0f,
                    MaxOutput = -100f
                };

                // Buff Scoring
                BuffAPCost = new ResponseCurve
                {
                    Type = CurveType.InverseLogistic,
                    MinInput = 0f,
                    MaxInput = 5f,
                    MinOutput = -15f,  // 고비용은 감점
                    MaxOutput = 15f,   // 0 AP는 보너스
                    Steepness = 2f,
                    Midpoint = 0.4f
                };

                // ★ v3.2.20: Confidence Curves
                ConfidenceToAggression = new ResponseCurve
                {
                    Type = CurveType.Logistic,
                    MinInput = 0f,
                    MaxInput = 1f,
                    MinOutput = 0.3f,   // 최소 공격성 (절망 상태)
                    MaxOutput = 1.5f,   // 최대 공격성 (압도 상태)
                    Steepness = 6f,
                    Midpoint = 0.5f     // 50% confidence에서 1.0 배율
                };

                ConfidenceToDefenseNeed = new ResponseCurve
                {
                    Type = CurveType.InverseLogistic,
                    MinInput = 0f,
                    MaxInput = 1f,
                    MinOutput = 0.3f,   // 낮은 방어 필요 (압도 상태)
                    MaxOutput = 1.5f,   // 높은 방어 필요 (절망 상태)
                    Steepness = 6f,
                    Midpoint = 0.5f
                };

                _initialized = true;
                Main.Log("[ResponseCurves] Presets initialized (v3.2.20 with Confidence curves)");
            }
        }

        /// <summary>
        /// JSON 설정에서 곡선 로드 (있으면 덮어쓰기)
        /// </summary>
        public static void LoadFromConfig(Dictionary<string, CurveConfig> curves)
        {
            if (curves == null) return;

            foreach (var kvp in curves)
            {
                var curve = kvp.Value.ToCurve();
                if (curve == null) continue;

                switch (kvp.Key.ToLower())
                {
                    case "damageratio": DamageRatio = curve; break;
                    case "onehitkillbonus": OneHitKillBonus = curve; break;
                    case "apefficiency": APEfficiency = curve; break;
                    case "healurgency": HealUrgency = curve; break;
                    case "selfhealbonus": SelfHealBonus = curve; break;
                    case "hppriority": HPPriority = curve; break;
                    case "threatbydistance": ThreatByDistance = curve; break;
                    case "distancepenalty": DistancePenalty = curve; break;
                    case "safetybydistance": SafetyByDistance = curve; break;
                    case "covervalue": CoverValue = curve; break;
                    case "threatcountpenalty": ThreatCountPenalty = curve; break;
                    case "buffapcost": BuffAPCost = curve; break;
                }
            }

            Main.Log($"[ResponseCurves] Loaded {curves.Count} curves from config");
        }

        /// <summary>
        /// 초기화 상태 확인
        /// </summary>
        public static bool IsInitialized => _initialized;
    }
}
