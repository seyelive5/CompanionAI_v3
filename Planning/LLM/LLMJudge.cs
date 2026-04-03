// Planning/LLM/LLMJudge.cs
// ★ Phase 3: LLM-as-Judge — 후보 플랜 중 최선을 Ollama Structured Output으로 선택.
using System;
using System.Collections;
using System.Collections.Generic;
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
                    systemMsg = BuildSystemMessage(roleName);
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
        // 메시지 빌드
        // ═══════════════════════════════════════════════════════════

        /// <summary>시스템 메시지 — 역할별 캐싱</summary>
        private static string BuildSystemMessage(string roleName)
        {
            // 역할이 같으면 캐시 재사용
            if (_cachedSystemMsg != null && _cachedSystemRole == roleName)
                return _cachedSystemMsg;

            _sbSystem.Clear();
            _sbSystem.Append("You are a tactical combat advisor for a ").Append(roleName).Append(" unit.\n");
            _sbSystem.Append("Given the battlefield and candidate action plans, choose the single best plan.\n");
            _sbSystem.Append("Evaluate: threat elimination, ally survival, damage efficiency, positioning.\n");
            _sbSystem.Append("Respond with ONLY the letter of your choice (A, B, or C). Nothing else.");

            _cachedSystemRole = roleName;
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
                    // content에서 A/B/C 문자 찾기
                    for (int i = 0; i < content.Length; i++)
                    {
                        char c = char.ToUpperInvariant(content[i]);
                        if (c >= 'A' && c < 'A' + candidateCount)
                        {
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
