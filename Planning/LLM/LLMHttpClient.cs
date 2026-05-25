// Planning/LLM/LLMHttpClient.cs
// ★ v3.114.0 (Phase F.2): LLM HTTP 통합 클라이언트.
// LLMCommander/Scorer/Judge/Warmup 4 파일의 UnityWebRequest 중복(약 70%)을 제거.
// 본 파일은 skeleton — caller 마이그레이션은 후속 commit 에서 진행.
//
// 비공유 (caller-owned, 의도적):
//   - Latch (_isCommanding/_isScoring/_isJudging/_isWarming): caller 별로 의미가 다름
//   - Watchdog (LLMScorer C3 fix): Scorer 전용 안전망
//   - Response 파싱: 도메인별 (CommanderDirective/ScorerWeights/JudgeConfidence/없음)
//
// 공유 (이 파일에서 통합):
//   - Request body 빌딩 (BuildChatRequest)
//   - UnityWebRequest 라이프사이클 (PostChatAsync)
//   - HttpWebRequest sync POST (PostGenerateSync — Warmup unload 전용)
//   - URL 정규화 (NormalizeBaseUrl)
//   - 모델 결정 체인 (ResolveModel: LLMJudgeModel → "gemma4:e4b")
//   - message.content 추출 (ExtractContent)

using System;
using System.Collections;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Planning.LLM
{
    /// <summary>
    /// ★ v3.114.0 (Phase F.2): Ollama HTTP 통합 클라이언트.
    ///
    /// 목적: 4 caller (LLMCommander/Scorer/Judge/Warmup) 의 HTTP 중복 제거.
    /// 호출자는 latch/watchdog/응답 파싱만 책임짐 — 그 외 HTTP 플러밍은 모두 이 클래스 위임.
    ///
    /// 사용 패턴 (코루틴):
    /// <code>
    /// var body = LLMHttpClient.BuildChatRequest(model, systemMsg, userMsg, numPredict: 50);
    /// LLMHttpClient.Response result = default;
    /// yield return LLMHttpClient.PostChatAsync(baseUrl, body, 30, r =&gt; result = r);
    /// if (result.Success) {
    ///     string content = LLMHttpClient.ExtractContent(result.RawJson);
    ///     // 도메인별 파싱…
    /// }
    /// </code>
    /// </summary>
    public static class LLMHttpClient
    {
        // ═══════════════════════════════════════════════════════════
        // Response struct
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// HTTP 요청 결과 — 성공/실패, 응답 본문, 에러, HTTP 상태 코드,
        /// 경과 시간, 타임아웃 여부.
        /// </summary>
        public struct Response
        {
            /// <summary>HTTP 200 + 네트워크 정상 시 true.</summary>
            public bool Success;
            /// <summary>응답 본문 (JSON 원문). 실패 시 null/empty 가능.</summary>
            public string RawJson;
            /// <summary>실패 시 에러 메시지. 성공 시 null.</summary>
            public string ErrorMessage;
            /// <summary>HTTP 상태 코드 (responseCode). 0 이면 미수신.</summary>
            public int HttpStatusCode;
            /// <summary>요청 시작 ~ 응답 도착까지 경과 시간 (초).</summary>
            public float ElapsedSeconds;
            /// <summary>타임아웃으로 실패한 경우 true.</summary>
            public bool WasTimeout;
        }

        // ═══════════════════════════════════════════════════════════
        // 공유 헬퍼
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 모델 결정 체인: LLMJudgeModel (사용자 명시) → "gemma4:e4b" 폴백.
        /// MachineSpirit.Model 은 narrative/dialogue 전용 — 전투 LLM 과 분리.
        /// (UI 의 RECOMMENDED_JUDGE_MODEL 과 동일 폴백, 사용자 미설정 시 표시/동작 일치.)
        /// </summary>
        public static string ResolveModel()
        {
            var judgeModel = Main.Settings?.LLMJudgeModel;
            if (!string.IsNullOrEmpty(judgeModel))
                return judgeModel;

            return "gemma4:e4b";
        }

        /// <summary>
        /// Ollama base URL 정규화: 후행 '/' 제거 + '/v1' suffix 제거.
        /// 입력이 null/empty 면 "http://localhost:11434" 폴백.
        /// </summary>
        /// <param name="baseUrl">사용자 설정 ApiUrl (예: "http://localhost:11434/v1").</param>
        public static string NormalizeBaseUrl(string baseUrl)
        {
            if (string.IsNullOrEmpty(baseUrl))
                return "http://localhost:11434";

            string url = baseUrl.TrimEnd('/');
            if (url.EndsWith("/v1"))
                url = url.Substring(0, url.Length - 3);
            return url;
        }

        /// <summary>
        /// Ollama /api/chat request body 빌더.
        /// systemMsg 가 null/empty 면 user 메시지만 포함 (Warmup 패턴).
        /// </summary>
        /// <param name="model">모델 ID (예: "gemma4:e4b").</param>
        /// <param name="systemMsg">system role 컨텐트 (null/empty 시 생략).</param>
        /// <param name="userMsg">user role 컨텐트 (필수).</param>
        /// <param name="numPredict">max output tokens (Warmup=1, Judge=50, Scorer=120).</param>
        /// <param name="temperature">샘플링 온도 (default 0 — deterministic).</param>
        /// <param name="think">thinking 모드 활성화 여부 (Gemma4 는 false 권장).</param>
        /// <param name="keepAlive">모델 메모리 유지 시간(초). -1 = 무한, 0 = 즉시 언로드.</param>
        public static JObject BuildChatRequest(
            string model,
            string systemMsg,
            string userMsg,
            int numPredict,
            float temperature = 0f,
            bool think = false,
            int keepAlive = -1)
        {
            var messages = new JArray();
            if (!string.IsNullOrEmpty(systemMsg))
                messages.Add(new JObject { ["role"] = "system", ["content"] = systemMsg });
            messages.Add(new JObject { ["role"] = "user", ["content"] = userMsg ?? "" });

            // ★ v3.114.0 (Phase F.2 review): legacy 4 callers 의 JObject 삽입 순서 보존 —
            //   options 가 think 보다 먼저. JSON wire-level byte-identity 유지 (Ollama 는 순서 무관하지만
            //   네트워크 캡쳐 diff 시 마이그레이션 전후 동일).
            return new JObject
            {
                ["model"] = model,
                ["messages"] = messages,
                ["stream"] = false,
                ["keep_alive"] = keepAlive,
                ["options"] = new JObject
                {
                    ["num_predict"] = numPredict,
                    ["temperature"] = temperature
                },
                ["think"] = think
            };
        }

        /// <summary>
        /// Ollama 응답 본문에서 message.content 추출.
        /// 파싱 실패 시 원문 반환 (legacy fallback — 4 caller 동일 동작).
        /// </summary>
        public static string ExtractContent(string rawResponse)
        {
            if (string.IsNullOrEmpty(rawResponse)) return "";

            try
            {
                var outerJson = JObject.Parse(rawResponse);
                return outerJson["message"]?["content"]?.ToString()?.Trim() ?? "";
            }
            catch
            {
                return rawResponse.Trim();
            }
        }

        // ═══════════════════════════════════════════════════════════
        // Async POST (UnityWebRequest, 코루틴 — 4 caller async 경로)
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// UnityWebRequest 비동기 POST → /api/chat.
        /// 4 caller (Commander/Scorer/Judge/Warmup) 의 공통 async 경로.
        ///
        /// 결과는 onComplete 콜백으로 전달.
        /// 실패 케이스: 요청 빌드 실패 / 네트워크 오류 / 타임아웃 / 비-200 status.
        /// </summary>
        /// <param name="baseUrl">사용자 설정 ApiUrl (정규화는 내부에서 수행).</param>
        /// <param name="requestBody">BuildChatRequest 로 만든 JObject.</param>
        /// <param name="timeoutSeconds">UnityWebRequest.timeout (초).</param>
        /// <param name="onComplete">완료 시 호출되는 콜백 (Response 1개 인자). null 이면 결과 무시.</param>
        public static IEnumerator PostChatAsync(
            string baseUrl,
            JObject requestBody,
            int timeoutSeconds,
            Action<Response> onComplete)
        {
            string url = NormalizeBaseUrl(baseUrl) + "/api/chat";

            UnityWebRequest req = null;
            bool buildFailed = false;
            string buildError = null;
            try
            {
                req = new UnityWebRequest(url, "POST");
                string bodyJson = requestBody?.ToString(Newtonsoft.Json.Formatting.None) ?? "{}";
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(bodyJson));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = timeoutSeconds;
            }
            catch (Exception ex)
            {
                buildFailed = true;
                buildError = ex.Message;
                if (req != null)
                {
                    req.Dispose();
                    req = null;
                }
            }

            if (buildFailed)
            {
                onComplete?.Invoke(new Response
                {
                    Success = false,
                    ErrorMessage = $"request build failed: {buildError}",
                    HttpStatusCode = 0,
                    ElapsedSeconds = 0f,
                    WasTimeout = false,
                    RawJson = null
                });
                yield break;
            }

            // ★ v3.114.0 (Phase F.2 review): try/finally guarantees Dispose + callback exactly once
            // even if SendWebRequest throws. Caller latch invariant (_isXing) depends on this.
            float startTime = Time.realtimeSinceStartup;
            Response response = default(Response);
            bool callbackFired = false;
            try
            {
                yield return req.SendWebRequest();
                float elapsed = Time.realtimeSinceStartup - startTime;

                if (req.result == UnityWebRequest.Result.Success)
                {
                    response = new Response
                    {
                        Success = true,
                        RawJson = req.downloadHandler?.text,
                        ErrorMessage = null,
                        HttpStatusCode = (int)req.responseCode,
                        ElapsedSeconds = elapsed,
                        WasTimeout = false
                    };
                }
                else
                {
                    bool wasTimeout =
                        req.result == UnityWebRequest.Result.ConnectionError &&
                        !string.IsNullOrEmpty(req.error) &&
                        req.error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0;

                    response = new Response
                    {
                        Success = false,
                        RawJson = req.downloadHandler?.text,
                        ErrorMessage = req.error,
                        HttpStatusCode = (int)req.responseCode,
                        ElapsedSeconds = elapsed,
                        WasTimeout = wasTimeout
                    };
                }
            }
            finally
            {
                // Always dispose, always invoke callback exactly once (caller latch invariant)
                if (req != null) req.Dispose();
                if (!callbackFired)
                {
                    callbackFired = true;
                    try { onComplete?.Invoke(response); }
                    catch (Exception cbEx) { Log.Planning.Debug($"[LLMHttpClient] onComplete threw: {cbEx.Message}"); }
                }
            }
        }

        // ═══════════════════════════════════════════════════════════
        // Sync POST (HttpWebRequest — Warmup unload 전용)
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 동기 POST → /api/generate. **셧다운 시 모델 언로드 전용**.
        /// 코루틴은 셧다운 중 불안정하므로 blocking HttpWebRequest 사용.
        ///
        /// Body: {"model":"&lt;model&gt;","keep_alive":&lt;keepAlive&gt;}
        /// 일반적으로 keepAlive=0 (즉시 언로드) 로 사용.
        /// </summary>
        /// <param name="baseUrl">사용자 설정 ApiUrl.</param>
        /// <param name="model">언로드할 모델 ID.</param>
        /// <param name="keepAlive">keep_alive 값 (0 = 즉시 해제).</param>
        /// <param name="timeoutMs">Timeout + ReadWriteTimeout 동일값 (ms).</param>
        public static Response PostGenerateSync(
            string baseUrl,
            string model,
            int keepAlive,
            int timeoutMs)
        {
            string url = NormalizeBaseUrl(baseUrl) + "/api/generate";
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                string body = "{\"model\":\"" + model + "\",\"keep_alive\":" + keepAlive + "}";
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;

                var data = Encoding.UTF8.GetBytes(body);
                request.ContentLength = data.Length;
                using (var stream = request.GetRequestStream())
                {
                    stream.Write(data, 0, data.Length);
                }

                using (var resp = (HttpWebResponse)request.GetResponse())
                {
                    sw.Stop();
                    return new Response
                    {
                        Success = true,
                        RawJson = null, // 본문 읽지 않음 — 셧다운 중 latency 최소화
                        ErrorMessage = null,
                        HttpStatusCode = (int)resp.StatusCode,
                        ElapsedSeconds = sw.ElapsedMilliseconds / 1000f,
                        WasTimeout = false
                    };
                }
            }
            catch (WebException wex)
            {
                sw.Stop();
                bool wasTimeout = wex.Status == WebExceptionStatus.Timeout;
                int statusCode = 0;
                if (wex.Response is HttpWebResponse httpResp)
                    statusCode = (int)httpResp.StatusCode;

                return new Response
                {
                    Success = false,
                    RawJson = null,
                    ErrorMessage = wex.Message,
                    HttpStatusCode = statusCode,
                    ElapsedSeconds = sw.ElapsedMilliseconds / 1000f,
                    WasTimeout = wasTimeout
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new Response
                {
                    Success = false,
                    RawJson = null,
                    ErrorMessage = ex.Message,
                    HttpStatusCode = 0,
                    ElapsedSeconds = sw.ElapsedMilliseconds / 1000f,
                    WasTimeout = false
                };
            }
        }
    }
}
