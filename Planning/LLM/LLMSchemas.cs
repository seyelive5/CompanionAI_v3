// Planning/LLM/LLMSchemas.cs
// Ollama Structured Output (`format`) 용 JSON 스키마 정의.
using System;
using Newtonsoft.Json.Linq;

namespace CompanionAI_v3.Planning.LLM
{
    /// <summary>
    /// 전투 LLM 요청의 `format` 스키마 빌더.
    ///
    /// 배경 — 이전에는 `format` 을 쓰지 않고 응답을 문자열로 파싱했다.
    /// Ollama 에 `think=false` 일 때 format 제약이 조용히 무시되는 버그가 있었기 때문
    /// (issue #15260: format probability masking 이 thinking→content 전이에서만 적용돼,
    ///  think=false 면 그 전이가 없어 마스킹이 걸리지 않음).
    /// PR #15678 (2026-04-21 머지) 로 수정됨 — think 값에 따라 첫 토큰부터 format 을 적용한다.
    ///
    /// 재도입 전 실측 (Ollama 0.24.0 / 0.32.13, think=false):
    ///   gemma4:e4b  — 문자열 enum·정수 범위 스키마 3/3 준수, temperature=0 에서 완전 결정적
    ///   qwen3.5:9b  — 3/3 준수, 동일하게 결정적
    ///
    /// 기존 파서는 그대로 둔다. 스키마 준수 응답도 결국 JSON 이라 같은 경로로 파싱되며,
    /// 구버전 Ollama·미지원 모델에서는 폴백이 계속 동작한다.
    /// </summary>
    internal static class LLMSchemas
    {
        // ScorerWeights 의 이산 카테고리 (ScorerWeights._aoeWeightMap 등과 일치해야 함).
        // v3.102.0 에서 float 대신 카테고리를 우선하도록 바꾼 이유가 "소형 모델이 숫자보다
        // 범주를 안정적으로 고른다" 였으므로, 스키마도 숫자가 아니라 enum 으로 강제한다.
        private static readonly string[] AoeWeightValues = { "skip", "normal", "priority" };
        private static readonly string[] FocusFireValues = { "off", "normal", "heavy" };
        private static readonly string[] HealPriorityValues = { "suppress", "normal", "urgent" };
        private static readonly string[] BuffPriorityValues = { "skip", "normal", "heavy" };

        /// <summary>Judge 선택 스키마는 후보 수마다 달라 캐시한다.</summary>
        private static JObject _cachedChoiceSchema;
        private static int _cachedChoiceCount;

        private static JObject _cachedConfidenceSchema;
        private static int _cachedConfidenceCount;

        private static JObject StringEnum(string[] values)
        {
            var arr = new JArray();
            for (int i = 0; i < values.Length; i++) arr.Add(values[i]);
            return new JObject { ["type"] = "string", ["enum"] = arr };
        }

        private static JArray Required(params string[] names)
        {
            var arr = new JArray();
            for (int i = 0; i < names.Length; i++) arr.Add(names[i]);
            return arr;
        }

        /// <summary>
        /// LLMScorer 응답 스키마. `priority_target` 범위는 표시된 적 수에 따라 달라진다.
        /// -1 = 우선 대상 없음. 파서(ScorerWeights)가 어차피 클램프하므로 범위는 보조 힌트다.
        /// </summary>
        public static JObject ScorerWeights(int enemyCount)
        {
            int maxTarget = Math.Max(0, enemyCount - 1);

            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["aoe_weight"] = StringEnum(AoeWeightValues),
                    ["focus_fire"] = StringEnum(FocusFireValues),
                    ["priority_target"] = new JObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = -1,
                        ["maximum"] = maxTarget
                    },
                    ["heal_priority"] = StringEnum(HealPriorityValues),
                    ["buff_priority"] = StringEnum(BuffPriorityValues),
                    ["defensive_stance"] = new JObject { ["type"] = "boolean" },
                    ["reasoning"] = new JObject { ["type"] = "string" }
                },
                // reasoning 은 선택 — 강제하면 소형 모델이 토큰 예산을 설명에 써버린다.
                ["required"] = Required("aoe_weight", "focus_fire", "priority_target",
                    "heal_priority", "buff_priority", "defensive_stance")
            };
        }

        /// <summary>LLMJudge 단일 선택 스키마 — {"choice":"A"}. 라벨은 후보 수만큼만 허용.</summary>
        public static JObject JudgeChoice(int candidateCount, char[] choiceLabels)
        {
            if (_cachedChoiceSchema != null && _cachedChoiceCount == candidateCount)
                return _cachedChoiceSchema;

            int count = Math.Min(candidateCount, choiceLabels.Length);
            var labels = new JArray();
            for (int i = 0; i < count; i++) labels.Add(choiceLabels[i].ToString());

            _cachedChoiceSchema = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["choice"] = new JObject { ["type"] = "string", ["enum"] = labels }
                },
                ["required"] = Required("choice")
            };
            _cachedChoiceCount = candidateCount;
            return _cachedChoiceSchema;
        }

        /// <summary>
        /// LLMJudge 신뢰도 분포 스키마 — {"ratios":[0.7,0.3],"narration":"..."}.
        /// 기존 텍스트 형식("A:0.7,B:0.3" + 개행 + 내레이션)도 파서에 남아 있어,
        /// 스키마가 적용되지 않는 환경에서는 그대로 동작한다.
        /// </summary>
        public static JObject JudgeConfidence(int candidateCount)
        {
            if (_cachedConfidenceSchema != null && _cachedConfidenceCount == candidateCount)
                return _cachedConfidenceSchema;

            _cachedConfidenceSchema = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["ratios"] = new JObject
                    {
                        ["type"] = "array",
                        ["items"] = new JObject { ["type"] = "number" },
                        ["minItems"] = candidateCount,
                        ["maxItems"] = candidateCount
                    },
                    ["narration"] = new JObject { ["type"] = "string" }
                },
                ["required"] = Required("ratios", "narration")
            };
            _cachedConfidenceCount = candidateCount;
            return _cachedConfidenceSchema;
        }
    }
}
