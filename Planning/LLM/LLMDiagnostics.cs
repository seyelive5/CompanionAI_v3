// Planning/LLM/LLMDiagnostics.cs
// LLM 실패의 공용 진단 계층 — 두 LLM 스택(MachineSpirit 대화 / Planning.LLM 전투)이 공유한다.
//
// 왜 공용인가:
//   커뮤니티 제보 "Machine Spirit 이 반응하지 않는다"의 실제 원인은 Ollama 환경 문제가 아니라
//   **실패를 아무데도 보여주지 않은 것**이었다(v3.119.0 에서 대화 쪽 해소). 전투 LLM 은 실패해도
//   휴리스틱으로 정상 폴백하므로 사용자가 "LLM 이 실제로 동작 중인지" 판단할 방법이 전혀 없다 —
//   같은 클래스의 문제가 그대로 남아 있었다. 오류 해석을 한 곳에 두어 두 스택이 같은 문구를 쓴다.

using System;
using CompanionAI_v3.Logging;
using UnityEngine;

namespace CompanionAI_v3.Planning.LLM
{
    public static class LLMDiagnostics
    {
        // ════════════════════════════════════════════════════════════
        // 공용: 실패 → 조치 가능한 한 문장
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 원시 실패 정보를 사용자가 조치할 수 있는 한 문장으로 변환.
        /// 원문(코드/예외 메시지)은 로그에 남으므로 여기서는 "무엇을 해야 하는가"에 집중한다.
        /// </summary>
        /// <param name="rawError">전송 계층 오류 문자열 (없으면 null).</param>
        /// <param name="httpStatusCode">HTTP 상태 코드. 0 = 미수신(연결 실패 등).</param>
        /// <param name="wasTimeout">타임아웃으로 실패했는지.</param>
        /// <param name="isLocalOllama">로컬 Ollama 대상인지 (조치 문구가 달라진다).</param>
        /// <param name="apiUrl">대상 URL (문구에 포함).</param>
        /// <param name="model">대상 모델 (문구에 포함).</param>
        public static string DescribeFailure(
            string rawError, int httpStatusCode, bool wasTimeout,
            bool isLocalOllama, string apiUrl, string model)
        {
            string e = rawError ?? "";

            if (wasTimeout || Contains(e, "timeout") || Contains(e, "timed out"))
            {
                return isLocalOllama
                    ? $"The model '{model}' did not respond in time. A smaller model may be needed for this machine."
                    : "The request timed out. Check your connection or try again.";
            }

            // 연결 자체 실패 — 서버 미실행이 압도적으로 흔한 원인
            if (httpStatusCode == 0
                || Contains(e, "Cannot connect") || Contains(e, "Connection refused")
                || Contains(e, "Failed to connect") || Contains(e, "Unable to connect")
                || StartsWith(e, "HTTP 0"))
            {
                return isLocalOllama
                    ? $"Cannot reach Ollama at {apiUrl}. Is the Ollama server running?"
                    : $"Cannot reach the API server at {apiUrl}. Check the URL and your internet connection.";
            }

            if (httpStatusCode == 404 || StartsWith(e, "HTTP 404"))
            {
                return isLocalOllama
                    ? $"Model '{model}' not found on the Ollama server. Pull it first (ollama pull {model}) or pick an installed model."
                    : "Endpoint or model not found (HTTP 404). Check the API URL and model name.";
            }

            if (httpStatusCode == 401 || httpStatusCode == 403
                || StartsWith(e, "HTTP 401") || StartsWith(e, "HTTP 403"))
                return "API key rejected (HTTP 401/403). Check the API key for this provider.";

            if (httpStatusCode == 429 || StartsWith(e, "HTTP 429"))
                return "Rate limited by the provider (HTTP 429). Wait a moment and try again.";

            if (httpStatusCode >= 500 || StartsWith(e, "HTTP 5"))
                return $"The server returned an error (HTTP {httpStatusCode}). Try again shortly.";

            if (Contains(e, "Empty response"))
                return $"The model '{model}' returned an empty response. Try another model.";

            return string.IsNullOrEmpty(e)
                ? "Request failed (no detail available)."
                : $"Request failed: {e}";
        }

        private static bool Contains(string s, string sub)
            => !string.IsNullOrEmpty(s) && s.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool StartsWith(string s, string prefix)
            => !string.IsNullOrEmpty(s) && s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        // ════════════════════════════════════════════════════════════
        // 전투 LLM 상태 (Judge / Scorer / Commander 공용)
        // ════════════════════════════════════════════════════════════

        /// <summary>전투 LLM 마지막 실패 사유(사용자 표시용). 성공 시 null.</summary>
        public static string CombatLastError { get; private set; }

        /// <summary>전투 LLM 마지막 실패 시각(Time.time). 0 = 실패 이력 없음.</summary>
        public static float CombatLastErrorTime { get; private set; }

        /// <summary>전투 LLM 마지막 성공 시각(Time.time). 0 = 성공 이력 없음.</summary>
        public static float CombatLastSuccessTime { get; private set; }

        /// <summary>이번 세션 전투 LLM 실패 누적 횟수 (연속 아님 — 총계).</summary>
        public static int CombatFailureCount { get; private set; }

        /// <summary>
        /// 전투 LLM 요청 실패 기록 — 로그는 Warn(조치 문구 포함), 상태는 UI 표시용으로 보관.
        /// 전투 LLM 은 실패해도 휴리스틱으로 폴백하므로 게임은 계속되지만,
        /// 사용자가 "LLM 이 실제로 동작 중인지" 알 수 있어야 한다.
        /// </summary>
        /// <param name="source">호출자 라벨 (Judge/Scorer/Commander).</param>
        public static void RecordCombatFailure(
            string source, string rawError, int httpStatusCode, bool wasTimeout, string apiUrl, string model)
        {
            // 전투 LLM 은 로컬 Ollama 전용 스택 (LLMHttpClient 는 /api/chat 만 사용)
            CombatLastError = DescribeFailure(rawError, httpStatusCode, wasTimeout,
                isLocalOllama: true, apiUrl: apiUrl, model: model);
            CombatLastErrorTime = Time.time;
            CombatFailureCount++;

            Log.Planning.Warn($"[{source}] LLM unavailable — {CombatLastError} " +
                $"(raw='{rawError}', http={httpStatusCode}, timeout={wasTimeout}, model={model}) — falling back to heuristics");
        }

        /// <summary>전투 LLM 요청 성공 기록 — 상태줄이 "정상"으로 복귀한다.</summary>
        public static void RecordCombatSuccess()
        {
            CombatLastError = null;
            CombatLastSuccessTime = Time.time;
        }

        /// <summary>전투 종료/설정 변경 시 상태 초기화.</summary>
        public static void ResetCombatState()
        {
            CombatLastError = null;
            CombatLastErrorTime = 0f;
            CombatLastSuccessTime = 0f;
            CombatFailureCount = 0;
        }
    }
}
