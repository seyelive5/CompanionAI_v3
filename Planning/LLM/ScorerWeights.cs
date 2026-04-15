// Planning/LLM/ScorerWeights.cs
// ★ LLM-as-Scorer: LLM이 출력하는 유틸리티 가중치.
// Parse()는 JSON과 plain text 모두 안전하게 처리.
using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace CompanionAI_v3.Planning.LLM
{
    /// <summary>
    /// ★ LLM-as-Scorer: LLM이 출력하는 유틸리티 스코어링 가중치.
    /// UtilityScorer/TargetScorer의 개별 컴포넌트를 배율/오프셋으로 조정.
    /// 모든 값은 안전 범위로 클램프됨.
    /// </summary>
    public class ScorerWeights
    {
        /// <summary>AoE 스코어 배율 (1.0=기본, 2.0=2배 선호). 범위: 0.1~5.0</summary>
        public float AoEWeight = 1.0f;

        /// <summary>지정 타겟 스코어 배율 (1.0=기본). 범위: 0.1~5.0</summary>
        public float FocusFire = 1.0f;

        /// <summary>우선 공격 대상 적 인덱스 (-1=미지정). 범위: -1 ~ enemyCount-1</summary>
        public int PriorityTarget = -1;

        /// <summary>힐 스코어 오프셋 (0=기본, 양수=힐 강조, 음수=힐 억제). 범위: -1.0~2.0</summary>
        public float HealPriority = 0f;

        /// <summary>버프 스코어 배율 (1.0=기본). 범위: 0.1~3.0</summary>
        public float BuffPriority = 1.0f;

        /// <summary>방어적 포지셔닝 활성화 여부</summary>
        public bool DefensiveStance = false;

        /// <summary>★ LLM이 가중치 결정의 근거를 한 문장으로 설명 (1-2 문장).
        /// default 값을 출력한 경우에도 "왜 baseline이 적절한지" 설명할 수 있음.</summary>
        public string Reasoning = "";

        /// <summary>모든 값이 기본값인지 확인 (LLM 호출 실패/무의미한 경우)</summary>
        public bool IsDefault => AoEWeight == 1f && FocusFire == 1f
            && PriorityTarget == -1 && HealPriority == 0f
            && BuffPriority == 1f && !DefensiveStance;

        /// <summary>
        /// LLM 응답에서 ScorerWeights 파싱.
        ///
        /// 지원 형식:
        /// 1. {"aoe_weight":2.0,"priority_target":0} (JSON — 변경된 값만)
        /// 2. 혼합 텍스트에서 JSON 추출 ("Here is my analysis: {...}")
        /// 3. 파싱 실패 → 기본값 반환 (모든 값 1.0)
        ///
        /// 모든 값은 안전 범위로 클램프.
        /// </summary>
        /// <param name="response">LLM 원본 응답 (Ollama content)</param>
        /// <param name="enemyCount">유효 적 수 (PriorityTarget 범위 검증용)</param>
        /// <param name="displayToOriginalMap">★ v3.101.0: E 라인 display idx → situation.Enemies 원본 idx 매핑.
        /// 제공 시 LLM의 priority_target(display idx)을 원본 idx로 역매핑. null이면 원래 semantics.</param>
        public static ScorerWeights Parse(string response, int enemyCount, int[] displayToOriginalMap = null)
        {
            if (string.IsNullOrEmpty(response))
                return new ScorerWeights();

            response = response.Trim();

            // 1. JSON 형태 시도 (전체 문자열이 JSON인 경우)
            if (response.IndexOf('{') >= 0)
            {
                var result = TryParseJson(response, enemyCount, displayToOriginalMap);
                if (result != null) return result;

                // 2. 혼합 텍스트에서 JSON 추출
                result = TryExtractAndParseJson(response, enemyCount, displayToOriginalMap);
                if (result != null) return result;
            }

            // 3. 파싱 실패 → 기본값
            return new ScorerWeights();
        }

        /// <summary>
        /// 전체 문자열을 JSON으로 파싱 시도.
        /// {"aoe_weight":2.0, "focus_fire":1.5, "priority_target":0}
        /// </summary>
        private static ScorerWeights TryParseJson(string text, int enemyCount, int[] displayToOriginalMap)
        {
            try
            {
                var json = JObject.Parse(text);
                return FromJObject(json, enemyCount, displayToOriginalMap);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 혼합 텍스트에서 {...} 부분만 추출하여 JSON 파싱.
        /// LLM이 "Based on analysis: {"aoe_weight":2.0}" 형태로 응답할 수 있음.
        /// </summary>
        private static ScorerWeights TryExtractAndParseJson(string text, int enemyCount, int[] displayToOriginalMap)
        {
            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            if (start < 0 || end <= start) return null;

            string jsonPart = text.Substring(start, end - start + 1);
            try
            {
                var json = JObject.Parse(jsonPart);
                return FromJObject(json, enemyCount, displayToOriginalMap);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// JObject에서 ScorerWeights 생성. 누락된 키는 기본값 유지.
        /// 모든 값은 안전 범위로 클램프.
        /// </summary>
        private static ScorerWeights FromJObject(JObject json, int enemyCount, int[] displayToOriginalMap)
        {
            var w = new ScorerWeights();

            // aoe_weight (float, 0.1~5.0)
            w.AoEWeight = ClampF(
                ReadFloat(json, "aoe_weight", 1.0f),
                0.1f, 5.0f);

            // focus_fire (float, 0.1~5.0)
            w.FocusFire = ClampF(
                ReadFloat(json, "focus_fire", 1.0f),
                0.1f, 5.0f);

            // priority_target (int, -1 ~ enemyCount-1)
            // ★ v3.101.0: displayToOriginalMap 제공 시 display idx → original idx 역매핑
            int rawTarget = ReadInt(json, "priority_target", -1);
            if (displayToOriginalMap != null)
            {
                // LLM은 display idx를 반환함. 매핑 범위 초과 시 -1 (invalid).
                if (rawTarget >= 0 && rawTarget < displayToOriginalMap.Length)
                    rawTarget = displayToOriginalMap[rawTarget];
                else if (rawTarget >= 0)
                    rawTarget = -1;  // out of displayed range = invalid
            }
            w.PriorityTarget = (rawTarget < 0 || rawTarget >= enemyCount) ? -1 : rawTarget;

            // heal_priority (float, -1.0~2.0)
            w.HealPriority = ClampF(
                ReadFloat(json, "heal_priority", 0f),
                -1.0f, 2.0f);

            // buff_priority (float, 0.1~3.0)
            w.BuffPriority = ClampF(
                ReadFloat(json, "buff_priority", 1.0f),
                0.1f, 3.0f);

            // defensive_stance (bool)
            w.DefensiveStance = ReadBool(json, "defensive_stance", false);

            // ★ reasoning (string, optional, max 200 chars)
            var reasoningToken = json["reasoning"];
            if (reasoningToken != null)
            {
                string reasoning = reasoningToken.ToString().Trim();
                if (reasoning.Length > 200) reasoning = reasoning.Substring(0, 200);
                w.Reasoning = reasoning;
            }

            return w;
        }

        #region JSON Read Helpers

        private static float ReadFloat(JObject json, string key, float fallback)
        {
            var token = json[key];
            if (token == null) return fallback;
            try
            {
                return token.Value<float>();
            }
            catch
            {
                // 문자열로 들어올 수도 있음 ("2.0")
                string s = token.ToString();
                return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
            }
        }

        private static int ReadInt(JObject json, string key, int fallback)
        {
            var token = json[key];
            if (token == null) return fallback;
            try
            {
                return token.Value<int>();
            }
            catch
            {
                string s = token.ToString();
                return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
            }
        }

        private static bool ReadBool(JObject json, string key, bool fallback)
        {
            var token = json[key];
            if (token == null) return fallback;
            try
            {
                return token.Value<bool>();
            }
            catch
            {
                string s = token.ToString().Trim().ToLowerInvariant();
                if (s == "true" || s == "1") return true;
                if (s == "false" || s == "0") return false;
                return fallback;
            }
        }

        private static float ClampF(float value, float min, float max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        #endregion

        /// <summary>
        /// ★ 두 ScorerWeights를 비율로 블렌딩.
        /// 연속값(AoE, FocusFire, Heal, Buff): 가중 평균 + 클램핑.
        /// 이산값(PriorityTarget, DefensiveStance): 우세 비율(>= 0.5) 따름.
        /// </summary>
        public static ScorerWeights Blend(ScorerWeights a, ScorerWeights b, float ratioA, float ratioB)
        {
            if (a == null) a = new ScorerWeights();
            if (b == null) b = new ScorerWeights();

            // 비율 정규화 안전장치
            float sum = ratioA + ratioB;
            if (sum < 0.01f) { ratioA = 0.5f; ratioB = 0.5f; sum = 1f; }
            ratioA /= sum;
            ratioB /= sum;

            return new ScorerWeights
            {
                AoEWeight = ClampF(a.AoEWeight * ratioA + b.AoEWeight * ratioB, 0.1f, 5.0f),
                FocusFire = ClampF(a.FocusFire * ratioA + b.FocusFire * ratioB, 0.1f, 5.0f),
                PriorityTarget = ratioA >= 0.5f ? a.PriorityTarget : b.PriorityTarget,
                HealPriority = ClampF(a.HealPriority * ratioA + b.HealPriority * ratioB, -1.0f, 2.0f),
                BuffPriority = ClampF(a.BuffPriority * ratioA + b.BuffPriority * ratioB, 0.1f, 3.0f),
                DefensiveStance = ratioA >= 0.5f ? a.DefensiveStance : b.DefensiveStance,
                Reasoning = ratioA >= 0.5f ? a.Reasoning : b.Reasoning
            };
        }

        public override string ToString()
        {
            if (IsDefault) return "ScorerWeights(default)";
            return $"ScorerWeights(aoe={AoEWeight:F1}, focus={FocusFire:F1}, target={PriorityTarget}, " +
                   $"heal={HealPriority:F1}, buff={BuffPriority:F1}, defensive={DefensiveStance})";
        }
    }
}
