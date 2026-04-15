// Planning/LLM/LLMScorer.cs
// ★ LLM-as-Scorer: Ollama에 전투 상태를 보내고 가중치 JSON을 받음.
// LLMJudge와 동일한 HTTP 패턴 (format 없음, think=false, temp=0).
using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json.Linq;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning.LLM
{
    /// <summary>
    /// ★ LLM-as-Scorer: Ollama에 전투 상태(CompactBattlefieldEncoder)를 보내고
    /// 가중치 JSON(ScorerWeights)을 받아 UtilityScorer/TargetScorer에 적용.
    /// 독립적 HTTP 요청 (LLMJudge와 별도).
    /// </summary>
    public static class LLMScorer
    {
        private static bool _isScoring;
        private static ScorerWeights _pendingWeights;

        /// <summary>Scorer 요청 진행 중 여부</summary>
        public static bool IsScoring => _isScoring;

        /// <summary>결과가 준비되었는지 (요청 완료 + 결과 미소비)</summary>
        public static bool HasResult => !_isScoring && _pendingWeights != null;

        /// <summary>마지막 Scorer 호출 소요 시간 (ms). 디버그/성능 모니터링용.</summary>
        public static long LastScorerTimeMs { get; private set; }

        /// <summary>Scorer 타임아웃 (초).</summary>
        private const int SCORER_TIMEOUT_SECONDS = 30;

        // ═══════════════════════════════════════════════════════════
        // 재사용 버퍼 (GC 방지)
        // ═══════════════════════════════════════════════════════════
        private static readonly StringBuilder _sbSystem = new StringBuilder(256);

        // ═══════════════════════════════════════════════════════════
        // 시스템 메시지 캐시 (역할별)
        // ═══════════════════════════════════════════════════════════
        private static string _cachedSystemRole;
        private static string _cachedSystemMsg;

        /// <summary>
        /// 진행 상태 초기화 (전투 종료 시 등).
        /// </summary>
        public static void Reset()
        {
            _isScoring = false;
            _pendingWeights = null;
        }

        // ═══════════════════════════════════════════════════════════
        // Public API
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// LLM Scorer 코루틴 — 전투 상태를 인코딩하여 Ollama에 전송, 가중치 JSON 수신.
        /// </summary>
        /// <param name="situation">현재 전투 상황</param>
        /// <param name="roleName">유닛 역할 이름 (DPS, Tank 등)</param>
        /// <param name="enemyCount">적 수 (PriorityTarget 범위 검증용)</param>
        /// <param name="onResult">결과 콜백. 실패 시 기본 ScorerWeights.</param>
        public static IEnumerator Score(
            Situation situation,
            string roleName,
            int enemyCount,
            Action<ScorerWeights> onResult)
        {
            if (_isScoring)
            {
                Main.Log("[LLMScorer] Already scoring -- fallback to defaults");
                onResult?.Invoke(new ScorerWeights());
                yield break;
            }

            if (situation == null || situation.Unit == null)
            {
                onResult?.Invoke(new ScorerWeights());
                yield break;
            }

            if (enemyCount == 0)
            {
                onResult?.Invoke(new ScorerWeights());
                yield break;
            }

            _isScoring = true;
            _pendingWeights = null;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // 1. 메시지 구성
                string systemMsg, userMsg;
                int[] displayMap = null;  // ★ v3.101.0: E 라인 display idx → 원본 idx 역매핑
                try
                {
                    systemMsg = BuildSystemMessage(roleName);
                    userMsg = CompactBattlefieldEncoder.Encode(situation.Unit, situation, roleName);
                    displayMap = CompactBattlefieldEncoder.GetDisplayToOriginalMap();
                }
                catch (Exception msgEx)
                {
                    Main.LogWarning($"[LLMScorer] Message build failed: {msgEx.Message} -- fallback to defaults");
                    _isScoring = false;
                    onResult?.Invoke(new ScorerWeights());
                    yield break;
                }

                // 2. 모델 결정 (LLMJudge와 동일 경로)
                string model = ResolveModel();

                // 3. Ollama 요청 구성
                // ★ CRITICAL: format 파라미터 없음 (Gemma4 빈 응답 문제)
                // ★ CRITICAL: think=false (thinking 모드 비활성화)
                var requestBody = new JObject
                {
                    ["model"] = model,
                    ["messages"] = new JArray
                    {
                        new JObject { ["role"] = "system", ["content"] = systemMsg },
                        new JObject { ["role"] = "user", ["content"] = userMsg }
                    },
                    ["stream"] = false,
                    ["keep_alive"] = -1,
                    ["options"] = new JObject
                    {
                        ["temperature"] = 0,
                        ["num_predict"] = 120  // JSON 가중치 + reasoning 1문장 (~50 토큰)
                    },
                    ["think"] = false
                };

                // 4. Ollama URL 결정
                string baseUrl = GetOllamaBaseUrl();
                string url = baseUrl + "/api/chat";

                Main.LogDebug($"[LLMScorer] -> {url}, model={model}, enemies={enemyCount}");

                // 5. HTTP 요청
                string responseText = null;
                string errorText = null;

                var request = new UnityWebRequest(url, "POST");
                request.uploadHandler = new UploadHandlerRaw(
                    Encoding.UTF8.GetBytes(requestBody.ToString(Newtonsoft.Json.Formatting.None)));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = SCORER_TIMEOUT_SECONDS;

                var op = request.SendWebRequest();

                // 타임아웃 대기
                float deadline = Time.realtimeSinceStartup + SCORER_TIMEOUT_SECONDS + 1f;
                while (!op.isDone)
                {
                    if (Time.realtimeSinceStartup > deadline)
                    {
                        errorText = "Scorer timeout exceeded";
                        request.Abort();
                        break;
                    }
                    yield return null;
                }

                if (errorText == null)
                {
                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        responseText = request.downloadHandler.text;
                    }
                    else
                    {
                        errorText = $"HTTP {request.responseCode}: {request.error}";
                    }
                }

                request.Dispose();

                // 6. 응답 파싱
                ScorerWeights weights = new ScorerWeights();

                if (responseText != null)
                {
                    Main.LogDebug($"[LLMScorer] Raw response ({responseText.Length} chars): {Truncate(responseText, 300)}");
                    string content = ExtractContent(responseText);
                    weights = ScorerWeights.Parse(content, enemyCount, displayMap);  // ★ v3.101.0: display → original 역매핑

                    stopwatch.Stop();
                    LastScorerTimeMs = stopwatch.ElapsedMilliseconds;

                    Main.Log($"[LLMScorer] Result: {weights} in {LastScorerTimeMs}ms");
                    if (!string.IsNullOrEmpty(weights.Reasoning))
                        Main.Log($"[LLMScorer] Reasoning: {weights.Reasoning}");
                }
                else
                {
                    stopwatch.Stop();
                    LastScorerTimeMs = stopwatch.ElapsedMilliseconds;
                    Main.Log($"[LLMScorer] Failed: {errorText} -- fallback to defaults ({LastScorerTimeMs}ms)");
                }

                _pendingWeights = weights;
                _isScoring = false;
                onResult?.Invoke(weights);
            }
            finally
            {
                _isScoring = false;
                if (stopwatch.IsRunning) stopwatch.Stop();
            }
        }

        /// <summary>
        /// 대기 중인 결과 소비. 소비 후 null로 초기화.
        /// </summary>
        public static ScorerWeights ConsumeResult()
        {
            var result = _pendingWeights;
            _pendingWeights = null;
            return result ?? new ScorerWeights();
        }

        // ═══════════════════════════════════════════════════════════
        // 메시지 빌드
        // ═══════════════════════════════════════════════════════════

        /// <summary>시스템 메시지 — 역할별 캐싱. ~80 토큰의 간결한 형식 지시.</summary>
        private static string BuildSystemMessage(string roleName)
        {
            if (_cachedSystemMsg != null && _cachedSystemRole == roleName)
                return _cachedSystemMsg;

            _sbSystem.Clear();
            _sbSystem.Append("Tactical scoring advisor for ").Append(roleName).Append(" unit in turn-based combat.\n");
            _sbSystem.Append("Output JSON weights to adjust scoring. Only include changed values.\n");
            _sbSystem.Append("Keys: aoe_weight(float), focus_fire(float), priority_target(int), heal_priority(float), buff_priority(float), defensive_stance(bool), reasoning(string).\n");
            _sbSystem.Append("Defaults: all 1.0, target -1, heal 0, defensive false.\n");
            // ★ v3.101.0: E 라인 정렬 정책 고지 (primacy bias 활용)
            _sbSystem.Append("Note: E: entries are pre-sorted by threat (index 0 = top threat). priority_target refers to these indices. Override only when another index has a clearly stronger tactical reason.\n");
            _sbSystem.Append("ALWAYS include 'reasoning': 1 short sentence explaining why these weights (or why baseline is fine).\n");
            _sbSystem.Append("Example: {\"aoe_weight\":2.0,\"priority_target\":0,\"reasoning\":\"Cluster of weak enemies — AoE will maximize damage\"}");

            _cachedSystemRole = roleName;
            _cachedSystemMsg = _sbSystem.ToString();
            return _cachedSystemMsg;
        }

        // ═══════════════════════════════════════════════════════════
        // 응답 파싱
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Ollama /api/chat 응답에서 content 추출.
        /// {"message":{"content":"{\"aoe_weight\":2.0}"}}
        /// </summary>
        private static string ExtractContent(string rawResponse)
        {
            try
            {
                var outerJson = JObject.Parse(rawResponse);
                string content = outerJson["message"]?["content"]?.ToString();
                return content?.Trim() ?? "";
            }
            catch
            {
                return "";
            }
        }

        // ═══════════════════════════════════════════════════════════
        // 헬퍼 (LLMJudge와 동일 패턴)
        // ═══════════════════════════════════════════════════════════

        /// <summary>Scorer에 사용할 모델 결정. LLM Judge 전용 설정 -> MachineSpirit 폴백.</summary>
        private static string ResolveModel()
        {
            var judgeModel = Main.Settings?.LLMJudgeModel;
            if (!string.IsNullOrEmpty(judgeModel))
                return judgeModel;

            var msConfig = Main.Settings?.MachineSpirit;
            if (msConfig != null && !string.IsNullOrEmpty(msConfig.Model))
                return msConfig.Model;

            return "gemma4:e4b";
        }

        /// <summary>Ollama base URL 결정 (v1 suffix 제거).</summary>
        private static string GetOllamaBaseUrl()
        {
            string url = Main.Settings?.MachineSpirit?.ApiUrl ?? "http://localhost:11434/v1";
            url = url.TrimEnd('/');
            if (url.EndsWith("/v1"))
                url = url.Substring(0, url.Length - 3);
            return url;
        }

        private static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "(null)";
            return s.Length <= maxLen ? s : s.Substring(0, maxLen) + "...";
        }
    }
}
