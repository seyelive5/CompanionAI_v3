// Planning/LLM/StrategicIntent.cs
// ★ Phase 4: Strategic Advisor — LLM이 출력한 전략 의도 데이터.
// Parse()는 다양한 형태의 LLM 응답을 안전하게 파싱.
using System;
using System.Globalization;

namespace CompanionAI_v3.Planning.LLM
{
    /// <summary>
    /// ★ Phase 4: LLM Strategic Advisor가 출력하는 전략 목표 유형.
    /// </summary>
    public enum IntentType
    {
        /// <summary>특정 적에게 화력 집중</summary>
        FocusFire,
        /// <summary>AoE로 다수 적 동시 타격</summary>
        AoEClear,
        /// <summary>아군 보호 우선</summary>
        ProtectAlly,
        /// <summary>후퇴/방어적 플레이</summary>
        Retreat,
        /// <summary>기본 균형 전략 (LLM 응답 실패 시 폴백)</summary>
        Balanced
    }

    /// <summary>
    /// ★ Phase 4: LLM Strategic Advisor의 출력 — 전략적 의도.
    /// TurnPlanner의 가중치 수정에 사용됨.
    /// </summary>
    public class StrategicIntent
    {
        /// <summary>주요 전략 목표</summary>
        public IntentType PrimaryGoal;

        /// <summary>집중 공격 대상 적 인덱스 (0-based, Situation.Enemies 기준)</summary>
        public int FocusTargetIndex;

        /// <summary>AoE 선호도 (0.0=단일 타겟 선호, 1.0=AoE 최대 선호)</summary>
        public float AoEPreference;

        /// <summary>공격성 수준 (0.0=방어적, 0.5=균형, 1.0=전면 공격)</summary>
        public float AggressionLevel;

        /// <summary>마지막 LLM Advisor 호출 소요 시간 (ms). 디버그용.</summary>
        public long ElapsedMs;

        /// <summary>기본 균형 전략 인스턴스 (폴백용)</summary>
        public static readonly StrategicIntent Balanced = new StrategicIntent
        {
            PrimaryGoal = IntentType.Balanced,
            FocusTargetIndex = -1,
            AoEPreference = 0.5f,
            AggressionLevel = 0.5f
        };

        /// <summary>
        /// LLM 응답 텍스트를 StrategicIntent로 파싱.
        ///
        /// 지원 형식:
        /// 1. "focus_fire 2 0.75 0.5" (공백 구분 plain text)
        /// 2. {"goal":"focus_fire","target":2,"aoe":0.75,"aggression":0.5} (JSON)
        /// 3. "focus_fire" (부분 — 나머지 기본값)
        /// 4. 파싱 불가 → Balanced 폴백
        /// </summary>
        /// <param name="response">LLM 원본 응답 (Ollama content)</param>
        /// <param name="enemyCount">유효 적 수 (인덱스 범위 검증용)</param>
        public static StrategicIntent Parse(string response, int enemyCount)
        {
            if (string.IsNullOrEmpty(response))
                return Balanced;

            response = response.Trim();

            // JSON 형태 시도
            if (response.IndexOf('{') >= 0)
            {
                var fromJson = TryParseJson(response, enemyCount);
                if (fromJson != null) return fromJson;
            }

            // Plain text 형태: "focus_fire 2 0.75 0.5"
            return TryParsePlainText(response, enemyCount);
        }

        /// <summary>
        /// JSON 형태 파싱: {"goal":"focus_fire","target":2,"aoe":0.75,"aggression":0.5}
        /// </summary>
        private static StrategicIntent TryParseJson(string text, int enemyCount)
        {
            try
            {
                // 최소한의 JSON 파싱 — Newtonsoft.Json 의존
                var json = Newtonsoft.Json.Linq.JObject.Parse(text);

                string goalStr = json["goal"]?.ToString()
                    ?? json["GOAL"]?.ToString()
                    ?? json["primary_goal"]?.ToString();

                IntentType goal = ParseGoal(goalStr);

                int targetIdx = ParseInt(
                    json["target"]?.ToString()
                    ?? json["TARGET_INDEX"]?.ToString()
                    ?? json["target_index"]?.ToString(),
                    -1);

                float aoe = ParseFloat(
                    json["aoe"]?.ToString()
                    ?? json["AOE_PREF"]?.ToString()
                    ?? json["aoe_pref"]?.ToString(),
                    0.5f);

                float aggression = ParseFloat(
                    json["aggression"]?.ToString()
                    ?? json["AGGRESSION"]?.ToString(),
                    0.5f);

                return CreateValidated(goal, targetIdx, aoe, aggression, enemyCount);
            }
            catch
            {
                return null; // JSON 파싱 실패 → plain text로 폴백
            }
        }

        /// <summary>
        /// Plain text 형태 파싱: "focus_fire 2 0.75 0.5"
        /// 토큰 수에 따라 부분 파싱 지원.
        /// </summary>
        private static StrategicIntent TryParsePlainText(string text, int enemyCount)
        {
            // 여러 줄인 경우 첫 유효 줄만 사용
            string line = text;
            int newlineIdx = text.IndexOfAny(new[] { '\n', '\r' });
            if (newlineIdx >= 0)
                line = text.Substring(0, newlineIdx).Trim();

            // 공백/탭으로 분리
            var tokens = line.Split(new[] { ' ', '\t', ',', ':', '=' },
                StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length == 0)
                return Balanced;

            // 토큰 0: 목표
            IntentType goal = ParseGoal(tokens[0]);

            // 토큰 1: 타겟 인덱스 (선택)
            int targetIdx = tokens.Length > 1 ? ParseInt(tokens[1], -1) : -1;

            // 토큰 2: AoE 선호도 (선택)
            float aoe = tokens.Length > 2 ? ParseFloat(tokens[2], GetDefaultAoE(goal)) : GetDefaultAoE(goal);

            // 토큰 3: 공격성 (선택)
            float aggression = tokens.Length > 3 ? ParseFloat(tokens[3], GetDefaultAggression(goal)) : GetDefaultAggression(goal);

            return CreateValidated(goal, targetIdx, aoe, aggression, enemyCount);
        }

        /// <summary>목표 문자열 → IntentType 매핑</summary>
        private static IntentType ParseGoal(string s)
        {
            if (string.IsNullOrEmpty(s)) return IntentType.Balanced;

            s = s.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");

            // 정확 매칭
            switch (s)
            {
                case "focus_fire":
                case "focusfire":
                case "focus":
                    return IntentType.FocusFire;

                case "aoe_clear":
                case "aoeclear":
                case "aoe":
                    return IntentType.AoEClear;

                case "protect_ally":
                case "protectally":
                case "protect":
                case "defend":
                    return IntentType.ProtectAlly;

                case "retreat":
                case "defensive":
                case "fallback":
                    return IntentType.Retreat;

                case "balanced":
                case "default":
                case "normal":
                    return IntentType.Balanced;
            }

            // 부분 매칭 (LLM이 "focus fire on enemy 2" 같은 문장을 출력할 수 있음)
            if (s.Contains("focus") || s.Contains("fire"))
                return IntentType.FocusFire;
            if (s.Contains("aoe") || s.Contains("area") || s.Contains("clear"))
                return IntentType.AoEClear;
            if (s.Contains("protect") || s.Contains("defend") || s.Contains("ally"))
                return IntentType.ProtectAlly;
            if (s.Contains("retreat") || s.Contains("fall") || s.Contains("back"))
                return IntentType.Retreat;

            return IntentType.Balanced;
        }

        /// <summary>검증된 StrategicIntent 생성 (범위 클램프)</summary>
        private static StrategicIntent CreateValidated(
            IntentType goal, int targetIdx, float aoe, float aggression, int enemyCount)
        {
            // 타겟 인덱스 범위 검증
            if (targetIdx < 0 || targetIdx >= enemyCount)
                targetIdx = -1;

            // 값 클램프
            aoe = Clamp01(aoe);
            aggression = Clamp01(aggression);

            return new StrategicIntent
            {
                PrimaryGoal = goal,
                FocusTargetIndex = targetIdx,
                AoEPreference = aoe,
                AggressionLevel = aggression
            };
        }

        #region Parse Helpers

        private static int ParseInt(string s, int fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            return int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
        }

        private static float ParseFloat(string s, float fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            return float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        /// <summary>목표별 AoE 기본값</summary>
        private static float GetDefaultAoE(IntentType goal)
        {
            switch (goal)
            {
                case IntentType.AoEClear: return 0.9f;
                case IntentType.FocusFire: return 0.2f;
                case IntentType.ProtectAlly: return 0.3f;
                case IntentType.Retreat: return 0.1f;
                default: return 0.5f;
            }
        }

        /// <summary>목표별 공격성 기본값</summary>
        private static float GetDefaultAggression(IntentType goal)
        {
            switch (goal)
            {
                case IntentType.FocusFire: return 0.75f;
                case IntentType.AoEClear: return 0.7f;
                case IntentType.ProtectAlly: return 0.3f;
                case IntentType.Retreat: return 0.1f;
                default: return 0.5f;
            }
        }

        #endregion

        public override string ToString()
        {
            return $"Intent({PrimaryGoal}, target={FocusTargetIndex}, aoe={AoEPreference:F2}, aggr={AggressionLevel:F2})";
        }
    }
}
