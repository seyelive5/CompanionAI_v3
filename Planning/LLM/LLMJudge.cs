// Planning/LLM/LLMJudge.cs
// ★ Phase 3: LLM-as-Judge — 후보 플랜 중 최선을 Ollama Structured Output으로 선택.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.MachineSpirit;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning.LLM
{
    /// <summary>
    /// ★ Phase 3: LLM-as-Judge — 후보 플랜 중 최선 선택.
    /// Ollama Structured Output으로 {"choice":"A"} 형태 응답 강제.
    /// 독립적 HTTP 요청 (MachineSpirit LLMClient와 별도 — _isRequesting 미공유).
    /// </summary>
    public static class LLMJudge
    {
        /// <summary>
        /// ★ LLM Judge 신뢰도 결과. JudgeWithConfidence에서 반환.
        /// 예: "A:0.7,B:0.3" → Ratios=[0.7, 0.3], PreferredIndex=0, IsValid=true
        /// </summary>
        public struct JudgeConfidence
        {
            /// <summary>각 후보의 신뢰도 비율 (합계 ≈ 1.0)</summary>
            public float[] Ratios;
            /// <summary>최대 비율 후보 인덱스 (argmax)</summary>
            public int PreferredIndex;
            /// <summary>파싱 성공 여부 (false면 PreferredIndex로 이진 선택 폴백)</summary>
            public bool IsValid;
        }

        private static bool _isJudging;

        /// <summary>Judge 요청 진행 중 여부</summary>
        public static bool IsJudging => _isJudging;

        /// <summary>마지막 Judge 호출의 소요 시간 (ms). 디버그/성능 모니터링용.</summary>
        public static long LastJudgeTimeMs { get; private set; }

        // ═══════════════════════════════════════════════════════════
        // 재사용 버퍼 (GC 방지)
        // ═══════════════════════════════════════════════════════════
        private static readonly StringBuilder _sbSystem = new StringBuilder(256);
        private static readonly StringBuilder _sbUser = new StringBuilder(1024);

        // 선택지 라벨 (A, B, C, ...)
        private static readonly char[] ChoiceLabels = { 'A', 'B', 'C', 'D', 'E' };

        // ═══════════════════════════════════════════════════════════
        // 시스템 메시지 캐시 (역할별)
        // ═══════════════════════════════════════════════════════════
        private static string _cachedSystemRole;
        private static string _cachedSystemMsg;

        /// <summary>기본 Judge 모델 (ModSettings에 JudgeModel이 없으면 MachineSpirit 모델 사용)</summary>
        private const string DEFAULT_JUDGE_MODEL = "gemma4:e4b";

        /// <summary>Judge 타임아웃 (초). 응답이 ~5 토큰이므로 매우 빠름.</summary>
        private const int JUDGE_TIMEOUT_SECONDS = 30; // 첫 모델 로드 시 시간 필요

        /// <summary>
        /// 진행 상태 초기화 (전투 종료 시 등).
        /// </summary>
        public static void Reset()
        {
            _isJudging = false;
        }

        // ═══════════════════════════════════════════════════════════
        // Public API
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// LLM Judge 코루틴 — 후보 플랜 중 최선을 선택.
        /// </summary>
        /// <param name="candidates">후보 플랜 리스트 (2~5개)</param>
        /// <param name="situation">현재 전투 상황</param>
        /// <param name="roleName">유닛 역할 이름 (DPS, Tank 등)</param>
        /// <param name="onResult">선택된 후보 인덱스 콜백 (0-based). 실패 시 0.</param>
        public static IEnumerator Judge(
            List<CandidatePlan> candidates,
            Situation situation,
            string roleName,
            Action<int> onResult)
        {
            if (_isJudging)
            {
                Main.Log("[LLMJudge] Already judging — fallback to index 0");
                onResult?.Invoke(0);
                yield break;
            }

            if (candidates == null || candidates.Count == 0)
            {
                onResult?.Invoke(0);
                yield break;
            }

            // 후보가 1개면 Judge 불필요
            if (candidates.Count == 1)
            {
                onResult?.Invoke(0);
                yield break;
            }

            _isJudging = true;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // 1. 메시지 구성
                string systemMsg, userMsg;
                try
                {
                    systemMsg = BuildSystemMessage(roleName, candidates.Count);
                    userMsg = BuildUserMessage(candidates, situation);
                }
                catch (Exception msgEx)
                {
                    Main.LogWarning($"[LLMJudge] Message build failed: {msgEx.Message} — fallback to index 0");
                    _isJudging = false;
                    onResult?.Invoke(0);
                    yield break;
                }

                // 2. 모델 결정
                string model = ResolveModel();

                // 3. Ollama 요청 구성 (Structured Output)
                int candidateCount = System.Math.Min(candidates.Count, ChoiceLabels.Length);
                var enumArray = new JArray();
                for (int i = 0; i < candidateCount; i++)
                    enumArray.Add(ChoiceLabels[i].ToString());

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
                    // ★ format 파라미터 제거 — gemma4:e4b에서 빈 응답 반환 문제
                    // 시스템 프롬프트 + ParseResponse 폴백으로 충분히 처리
                    ["options"] = new JObject
                    {
                        ["temperature"] = 0,
                        ["num_predict"] = 50    // thinking 토큰 소진 방지 여유
                    },
                    ["think"] = false  // ★ Gemma4/Qwen3 thinking 모드 비활성화 — Judge는 즉답만 필요
                };

                // 4. Ollama URL 결정
                string baseUrl = GetOllamaBaseUrl();
                string url = baseUrl + "/api/chat";

                Main.LogDebug($"[LLMJudge] → {url}, model={model}, candidates={candidateCount}");

                // 5. HTTP 요청
                string responseText = null;
                string errorText = null;

                var request = new UnityWebRequest(url, "POST");
                request.uploadHandler = new UploadHandlerRaw(
                    Encoding.UTF8.GetBytes(requestBody.ToString(Formatting.None)));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = JUDGE_TIMEOUT_SECONDS;

                var op = request.SendWebRequest();

                // 타임아웃 대기 (UnityWebRequest.timeout이 있지만 추가 안전장치)
                float deadline = Time.realtimeSinceStartup + JUDGE_TIMEOUT_SECONDS + 1f;
                while (!op.isDone)
                {
                    if (Time.realtimeSinceStartup > deadline)
                    {
                        errorText = "Judge timeout exceeded";
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
                int selectedIndex = 0;

                if (responseText != null)
                {
                    Main.LogDebug($"[LLMJudge] Raw response ({responseText.Length} chars): {Truncate(responseText, 300)}");
                    selectedIndex = ParseResponse(responseText, candidateCount);
                    stopwatch.Stop();
                    LastJudgeTimeMs = stopwatch.ElapsedMilliseconds;
                    string strategyLabel = candidates[selectedIndex].Strategy != null
                        ? candidates[selectedIndex].Strategy.Sequence.ToString()
                        : candidates[selectedIndex].Plan?.Priority.ToString() ?? "Unknown";
                    Main.Log($"[LLMJudge] Selected: {ChoiceLabels[selectedIndex]} " +
                        $"({strategyLabel}) in {LastJudgeTimeMs}ms");
                }
                else
                {
                    stopwatch.Stop();
                    LastJudgeTimeMs = stopwatch.ElapsedMilliseconds;
                    Main.Log($"[LLMJudge] Failed: {errorText} — fallback to index 0 ({LastJudgeTimeMs}ms)");
                }

                _isJudging = false;
                onResult?.Invoke(selectedIndex);
            }
            finally
            {
                _isJudging = false;
                if (stopwatch.IsRunning) stopwatch.Stop();
            }
        }

        // ═══════════════════════════════════════════════════════════
        // ★ Confidence-based Judge (Fuzzy Blending)
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// ★ LLM Judge 코루틴 (신뢰도 분포 버전) — "A:0.7,B:0.3" 형태 응답.
        /// 파싱 실패 시 기존 이진 선택으로 폴백.
        /// </summary>
        public static IEnumerator JudgeWithConfidence(
            List<CandidatePlan> candidates,
            Situation situation,
            string roleName,
            Action<JudgeConfidence> onResult)
        {
            if (_isJudging)
            {
                Main.Log("[LLMJudge] Already judging — fallback confidence");
                onResult?.Invoke(new JudgeConfidence { PreferredIndex = 0, IsValid = false });
                yield break;
            }

            if (candidates == null || candidates.Count == 0)
            {
                onResult?.Invoke(new JudgeConfidence { PreferredIndex = 0, IsValid = false });
                yield break;
            }

            if (candidates.Count == 1)
            {
                onResult?.Invoke(new JudgeConfidence
                {
                    Ratios = new[] { 1f },
                    PreferredIndex = 0,
                    IsValid = true
                });
                yield break;
            }

            _isJudging = true;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                string systemMsg, userMsg;
                try
                {
                    systemMsg = BuildSystemMessageConfidence(roleName, candidates.Count);
                    userMsg = BuildUserMessage(candidates, situation);
                }
                catch (Exception msgEx)
                {
                    Main.LogWarning($"[LLMJudge] Confidence message build failed: {msgEx.Message}");
                    _isJudging = false;
                    onResult?.Invoke(new JudgeConfidence { PreferredIndex = 0, IsValid = false });
                    yield break;
                }

                string model = ResolveModel();
                int candidateCount = System.Math.Min(candidates.Count, ChoiceLabels.Length);

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
                        ["num_predict"] = 50
                    },
                    ["think"] = false
                };

                string baseUrl = GetOllamaBaseUrl();
                string url = baseUrl + "/api/chat";

                Main.LogDebug($"[LLMJudge] Confidence → {url}, model={model}, candidates={candidateCount}");

                string responseText = null;
                string errorText = null;

                var request = new UnityWebRequest(url, "POST");
                request.uploadHandler = new UploadHandlerRaw(
                    Encoding.UTF8.GetBytes(requestBody.ToString(Formatting.None)));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = JUDGE_TIMEOUT_SECONDS;

                var op = request.SendWebRequest();

                float deadline = Time.realtimeSinceStartup + JUDGE_TIMEOUT_SECONDS + 1f;
                while (!op.isDone)
                {
                    if (Time.realtimeSinceStartup > deadline)
                    {
                        errorText = "Judge confidence timeout exceeded";
                        request.Abort();
                        break;
                    }
                    yield return null;
                }

                if (errorText == null)
                {
                    if (request.result == UnityWebRequest.Result.Success)
                        responseText = request.downloadHandler.text;
                    else
                        errorText = $"HTTP {request.responseCode}: {request.error}";
                }

                request.Dispose();

                JudgeConfidence confidence;

                if (responseText != null)
                {
                    Main.LogDebug($"[LLMJudge] Confidence raw ({responseText.Length} chars): {Truncate(responseText, 300)}");
                    confidence = ParseConfidenceResponse(responseText, candidateCount);
                    stopwatch.Stop();
                    LastJudgeTimeMs = stopwatch.ElapsedMilliseconds;

                    if (confidence.IsValid)
                    {
                        var ratioStr = new StringBuilder(32);
                        for (int i = 0; i < confidence.Ratios.Length; i++)
                        {
                            if (i > 0) ratioStr.Append(',');
                            ratioStr.Append(ChoiceLabels[i]).Append(':').Append(confidence.Ratios[i].ToString("F2"));
                        }
                        Main.Log($"[LLMJudge] Confidence: [{ratioStr}] preferred={ChoiceLabels[confidence.PreferredIndex]} ({LastJudgeTimeMs}ms)");
                    }
                    else
                    {
                        Main.Log($"[LLMJudge] Confidence parse failed, preferred={ChoiceLabels[confidence.PreferredIndex]} ({LastJudgeTimeMs}ms)");
                    }
                }
                else
                {
                    stopwatch.Stop();
                    LastJudgeTimeMs = stopwatch.ElapsedMilliseconds;
                    confidence = new JudgeConfidence { PreferredIndex = 0, IsValid = false };
                    Main.Log($"[LLMJudge] Confidence failed: {errorText} ({LastJudgeTimeMs}ms)");
                }

                _isJudging = false;
                onResult?.Invoke(confidence);
            }
            finally
            {
                _isJudging = false;
                if (stopwatch.IsRunning) stopwatch.Stop();
            }
        }

        // ═══════════════════════════════════════════════════════════
        // 메시지 빌드
        // ═══════════════════════════════════════════════════════════

        /// <summary>시스템 메시지 — 역할+후보수별 캐싱</summary>
        private static int _cachedCandidateCount;

        private static string BuildSystemMessage(string roleName, int candidateCount)
        {
            // 역할 + 후보 수가 같으면 캐시 재사용
            if (_cachedSystemMsg != null && _cachedSystemRole == roleName && _cachedCandidateCount == candidateCount)
                return _cachedSystemMsg;

            // ★ v3.78.0: 후보 수에 맞는 동적 선택지 문자열 생성
            int count = System.Math.Min(candidateCount, ChoiceLabels.Length);
            string choiceStr;
            if (count <= 1)
                choiceStr = "A";
            else if (count == 2)
                choiceStr = "A or B";
            else
            {
                var sb = new StringBuilder(count * 3);
                for (int i = 0; i < count - 1; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(ChoiceLabels[i]);
                }
                sb.Append(", or ").Append(ChoiceLabels[count - 1]);
                choiceStr = sb.ToString();
            }

            _sbSystem.Clear();
            _sbSystem.Append("You are a tactical combat advisor for a ").Append(roleName).Append(" unit.\n");
            _sbSystem.Append("Given the battlefield and candidate action plans, choose the single best plan.\n");
            _sbSystem.Append("Each plan represents a distinct tactical approach (archetype). ");
            _sbSystem.Append("Evaluate: threat elimination, ally survival, damage efficiency, positioning.\n");
            _sbSystem.Append("Respond with ONLY the letter of your choice (").Append(choiceStr).Append("). Nothing else.");

            _cachedSystemRole = roleName;
            _cachedCandidateCount = candidateCount;
            _cachedSystemMsg = _sbSystem.ToString();
            return _cachedSystemMsg;
        }

        /// <summary>유저 메시지 — 전장 요약 + 후보 플랜</summary>
        private static string BuildUserMessage(List<CandidatePlan> candidates, Situation situation)
        {
            _sbUser.Clear();

            // 전장 요약
            _sbUser.Append(BattlefieldSummarizer.Summarize(situation));
            _sbUser.Append("\nCandidate Plans:\n");

            // 각 후보 (A, B, C...)
            int count = System.Math.Min(candidates.Count, ChoiceLabels.Length);
            for (int i = 0; i < count; i++)
            {
                _sbUser.Append(ChoiceLabels[i]).Append(". ");
                _sbUser.Append(candidates[i].Summary ?? "(no summary)");
                // 유틸리티 점수 추가 — LLM에게 수치적 참고치 제공
                _sbUser.Append(" [score:").Append(candidates[i].UtilityScore.ToString("F0")).Append(']');
                _sbUser.Append('\n');
            }

            return _sbUser.ToString();
        }

        // ═══════════════════════════════════════════════════════════
        // 응답 파싱
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Ollama Structured Output 응답 파싱.
        /// 응답 형태: {"message":{"content":"{\"choice\":\"A\"}"}} (Ollama /api/chat)
        /// format 사용 시 content 안에 JSON 문자열이 들어옴.
        /// </summary>
        private static int ParseResponse(string rawResponse, int candidateCount)
        {
            try
            {
                // Ollama /api/chat 응답: {"message":{"content":"..."}, ...}
                var outerJson = JObject.Parse(rawResponse);
                string content = outerJson["message"]?["content"]?.ToString();

                if (string.IsNullOrEmpty(content))
                {
                    Main.LogDebug("[LLMJudge] Empty content in response");
                    return 0;
                }

                // content 파싱: JSON {"choice":"A"} 또는 plain text "A" 모두 처리
                string choice = null;
                content = content.Trim();

                if (content.StartsWith("{"))
                {
                    // JSON 형태: {"choice":"A"}
                    try
                    {
                        var innerJson = JObject.Parse(content);
                        choice = innerJson["choice"]?.ToString();
                    }
                    catch
                    {
                        // JSON 파싱 실패 → 아래 plain text 처리로 폴백
                    }
                }

                // JSON 파싱 실패 또는 plain text 응답: "A", "B", "C" 직접 추출
                if (string.IsNullOrEmpty(choice))
                {
                    // content에서 독립 A/B/C 문자 찾기 (단어 내부 매치 방지)
                    for (int i = 0; i < content.Length; i++)
                    {
                        char c = char.ToUpperInvariant(content[i]);
                        if (c >= 'A' && c < 'A' + candidateCount)
                        {
                            bool prevIsLetter = i > 0 && char.IsLetter(content[i - 1]);
                            bool nextIsLetter = i + 1 < content.Length && char.IsLetter(content[i + 1]);
                            if (prevIsLetter || nextIsLetter) continue;

                            choice = c.ToString();
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(choice))
                {
                    Main.LogDebug($"[LLMJudge] No valid choice in content: {Truncate(content, 50)}");
                    return 0;
                }

                // "A" → 0, "B" → 1, "C" → 2
                int index = char.ToUpperInvariant(choice[0]) - 'A';

                if (index < 0 || index >= candidateCount)
                {
                    Main.LogDebug($"[LLMJudge] Choice '{choice}' out of range (0-{candidateCount - 1})");
                    return 0;
                }

                return index;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[LLMJudge] Parse error: {ex.Message}, raw={Truncate(rawResponse, 200)}");
                return 0;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // Confidence 메시지 빌드 + 파싱
        // ═══════════════════════════════════════════════════════════

        /// <summary>Confidence 시스템 메시지 캐시</summary>
        private static string _cachedConfidenceRole;
        private static int _cachedConfidenceCount;
        private static string _cachedConfidenceMsg;

        /// <summary>신뢰도 분포 출력용 시스템 메시지. "A:0.7,B:0.3" 형식 요청.</summary>
        private static string BuildSystemMessageConfidence(string roleName, int candidateCount)
        {
            if (_cachedConfidenceMsg != null && _cachedConfidenceRole == roleName && _cachedConfidenceCount == candidateCount)
                return _cachedConfidenceMsg;

            int count = System.Math.Min(candidateCount, ChoiceLabels.Length);

            // 예시 생성: "A:0.7,B:0.3" 또는 "A:0.5,B:0.3,C:0.2"
            var exSb = new StringBuilder(count * 5);
            for (int i = 0; i < count; i++)
            {
                if (i > 0) exSb.Append(',');
                exSb.Append(ChoiceLabels[i]).Append(':');
                if (i == 0) exSb.Append("0.7");
                else if (i == 1) exSb.Append("0.3");
                else exSb.Append("0.0");
            }
            string example = exSb.ToString();

            _sbSystem.Clear();
            _sbSystem.Append("You are a tactical combat advisor for a ").Append(roleName).Append(" unit.\n");
            _sbSystem.Append("Given the battlefield and candidate action plans, rate each plan's confidence.\n");
            _sbSystem.Append("Evaluate: threat elimination, ally survival, damage efficiency, positioning.\n");
            _sbSystem.Append("Respond with ONLY a confidence distribution like: ").Append(example).Append('\n');
            _sbSystem.Append("Values must sum to 1.0. Nothing else.");

            _cachedConfidenceRole = roleName;
            _cachedConfidenceCount = candidateCount;
            _cachedConfidenceMsg = _sbSystem.ToString();
            return _cachedConfidenceMsg;
        }

        /// <summary>
        /// 신뢰도 분포 응답 파싱.
        /// 지원 형식:
        /// 1. "A:0.7,B:0.3"
        /// 2. "A:0.7 B:0.3" (공백 구분)
        /// 3. "A: 0.7, B: 0.3" (공백 포함)
        /// 4. 단일 문자 "A" → [1.0, 0.0] (이진 폴백)
        /// </summary>
        private static JudgeConfidence ParseConfidenceResponse(string rawResponse, int candidateCount)
        {
            var fallback = new JudgeConfidence { PreferredIndex = 0, IsValid = false };

            try
            {
                // Ollama 래퍼에서 content 추출
                var outerJson = JObject.Parse(rawResponse);
                string content = outerJson["message"]?["content"]?.ToString();

                if (string.IsNullOrEmpty(content))
                    return fallback;

                content = content.Trim();

                // "A:0.7,B:0.3" 또는 "A: 0.7, B: 0.3" 패턴 파싱
                float[] ratios = new float[candidateCount];
                bool anyFound = false;

                for (int i = 0; i < candidateCount && i < ChoiceLabels.Length; i++)
                {
                    char label = ChoiceLabels[i];
                    // 패턴: "A:0.7" or "A: 0.7" — 대소문자 무관
                    int pos = -1;
                    for (int j = 0; j < content.Length; j++)
                    {
                        if (char.ToUpperInvariant(content[j]) == label)
                        {
                            // 다음에 ':' 또는 '=' 있는지 확인
                            int k = j + 1;
                            while (k < content.Length && content[k] == ' ') k++;
                            if (k < content.Length && (content[k] == ':' || content[k] == '='))
                            {
                                pos = k + 1;
                                break;
                            }
                        }
                    }

                    if (pos >= 0)
                    {
                        // 숫자 추출
                        while (pos < content.Length && content[pos] == ' ') pos++;
                        int numStart = pos;
                        while (pos < content.Length && (char.IsDigit(content[pos]) || content[pos] == '.'))
                            pos++;

                        if (pos > numStart)
                        {
                            string numStr = content.Substring(numStart, pos - numStart);
                            if (float.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                            {
                                ratios[i] = val < 0f ? 0f : (val > 1f ? 1f : val);
                                anyFound = true;
                            }
                        }
                    }
                }

                // "A:0.7,B:0.3" 형태 파싱 성공
                if (anyFound)
                {
                    // 합계 정규화
                    float sum = 0f;
                    for (int i = 0; i < ratios.Length; i++) sum += ratios[i];

                    if (sum > 0.01f)
                    {
                        for (int i = 0; i < ratios.Length; i++) ratios[i] /= sum;
                    }
                    else
                    {
                        // 모두 0 → 균등 분배
                        float equal = 1f / candidateCount;
                        for (int i = 0; i < ratios.Length; i++) ratios[i] = equal;
                    }

                    // argmax
                    int best = 0;
                    for (int i = 1; i < ratios.Length; i++)
                    {
                        if (ratios[i] > ratios[best]) best = i;
                    }

                    return new JudgeConfidence { Ratios = ratios, PreferredIndex = best, IsValid = true };
                }

                // 폴백: 단일 문자 ("A", "B") → 이진 변환
                // 단어 내부의 문자 매치 방지 (예: "Analyzing"의 'A')
                for (int i = 0; i < content.Length; i++)
                {
                    char c = char.ToUpperInvariant(content[i]);
                    if (c >= 'A' && c < 'A' + candidateCount)
                    {
                        // 독립 문자인지 확인: 앞뒤가 알파벳이면 단어 내부 → 스킵
                        bool prevIsLetter = i > 0 && char.IsLetter(content[i - 1]);
                        bool nextIsLetter = i + 1 < content.Length && char.IsLetter(content[i + 1]);
                        if (prevIsLetter || nextIsLetter) continue;

                        int idx = c - 'A';
                        ratios = new float[candidateCount];
                        ratios[idx] = 1f;
                        return new JudgeConfidence
                        {
                            Ratios = ratios,
                            PreferredIndex = idx,
                            IsValid = true  // 단일 선택도 유효한 결과
                        };
                    }
                }

                return fallback;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[LLMJudge] Confidence parse error: {ex.Message}");
                return fallback;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // 헬퍼
        // ═══════════════════════════════════════════════════════════

        /// <summary>Judge에 사용할 모델 결정. 전용 설정 → MachineSpirit 폴백.</summary>
        private static string ResolveModel()
        {
            // 1. LLM Judge 전용 모델 설정 확인
            var judgeModel = Main.Settings?.LLMJudgeModel;
            if (!string.IsNullOrEmpty(judgeModel))
                return judgeModel;

            // 2. MachineSpirit 폴백
            var msConfig = Main.Settings?.MachineSpirit;
            if (msConfig != null && !string.IsNullOrEmpty(msConfig.Model))
                return msConfig.Model;

            return DEFAULT_JUDGE_MODEL;
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
