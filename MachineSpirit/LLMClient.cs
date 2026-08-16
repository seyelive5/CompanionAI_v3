// MachineSpirit/LLMClient.cs
// ★ v3.58.0: Ollama native API streaming + sampling parameters
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CompanionAI_v3.Settings;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.MachineSpirit
{
    public static class LLMClient
    {
        private static bool _isRequesting;
        public static bool IsRequesting => _isRequesting;

        public static void Reset() => _isRequesting = false;

        public class ChatMessage
        {
            [JsonProperty("role")] public string Role;
            [JsonProperty("content")] public string Content;
            [JsonProperty("images", NullValueHandling = NullValueHandling.Ignore)]
            public List<string> Images;
        }

        // ════════════════════════════════════════════════════════════
        // Ollama Native API — Streaming + Full Sampling Control
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// Custom DownloadHandler that parses Ollama's NDJSON streaming format in real-time.
        /// ReceiveData is called from Unity's worker thread → uses lock for thread safety.
        /// </summary>
        private class StreamHandler : DownloadHandlerScript
        {
            private readonly object _lock = new object();
            private readonly List<string> _pendingTokens = new List<string>();
            private string _partial = "";

            public StreamHandler() : base(new byte[4096]) { }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                _partial += Encoding.UTF8.GetString(data, 0, dataLength);

                int idx;
                while ((idx = _partial.IndexOf('\n')) >= 0)
                {
                    string line = _partial.Substring(0, idx).Trim();
                    _partial = _partial.Substring(idx + 1);

                    if (string.IsNullOrEmpty(line)) continue;

                    try
                    {
                        // Ollama NDJSON: {"message":{"content":"token"},"done":false}
                        var json = JObject.Parse(line);
                        var content = json["message"]?["content"]?.ToString();
                        if (!string.IsNullOrEmpty(content))
                        {
                            lock (_lock) { _pendingTokens.Add(content); }
                        }
                    }
                    catch { /* partial JSON line — will be completed on next chunk */ }
                }
                return true;
            }

            /// <summary>
            /// Flush all pending tokens into a single string (call from main thread each frame).
            /// Returns null if no tokens are pending.
            /// </summary>
            public string FlushTokens()
            {
                lock (_lock)
                {
                    if (_pendingTokens.Count == 0) return null;
                    var sb = new StringBuilder();
                    foreach (var t in _pendingTokens) sb.Append(t);
                    _pendingTokens.Clear();
                    return sb.ToString();
                }
            }
        }

        /// <summary>
        /// Model-tier context size: larger models get more context for richer conversations.
        /// 4B models: 4096 (fast, fits in small VRAM)
        /// 12B models: 8192 (balanced)
        /// 27B+ models: 16384 (maximum context for deep reasoning)
        /// </summary>
        /// <summary>소형/중형 경계 (십억 파라미터). 미만이면 0(소형).</summary>
        private const float SIZE_CLASS_SMALL_MAX_B = 6f;
        /// <summary>중형/대형 경계 (십억 파라미터). 이상이면 2(대형).</summary>
        private const float SIZE_CLASS_LARGE_MIN_B = 20f;

        /// <summary>
        /// Classify model into size tier: 0=small(~1-4B), 1=mid(~7-14B), 2=large(~20B+).
        /// 태그에서 파라미터 수를 파싱해 판정한다 (<see cref="ParseParamBillions"/>).
        ///
        /// 이전 구현은 부분 문자열 포함(`m.Contains("4b")` 등)이라 신형 태그에서 깨졌다:
        ///   gemma4:31b   → "1b" 에 걸려 소형 판정 → 31B 모델에 4096 컨텍스트
        ///   qwen3.5:0.8b → "8b" 에 걸려 중형 판정
        ///   gemma4:26b · gemma4:e2b · qwen3.5:2b · qwen3.5:35b · qwen3.5:122b → 어디에도 안 걸려 중형 폴백
        /// </summary>
        public static int GetModelSizeClass(string model)
        {
            if (string.IsNullOrEmpty(model)) return 1;
            string m = model.ToLowerInvariant();

            float billions = ParseParamBillions(m);
            if (billions > 0f)
            {
                if (billions < SIZE_CLASS_SMALL_MAX_B) return 0;
                if (billions >= SIZE_CLASS_LARGE_MIN_B) return 2;
                return 1;
            }

            // 이름에 크기가 없는 모델 — 알려진 것만 수동 매핑
            if (m.Contains("mistral-nemo") || m.Contains("nemomix")) return 1;  // 12B

            return 1; // Default: mid-range
        }

        /// <summary>
        /// 모델 태그에서 파라미터 수(십억 단위)를 뽑는다. 없으면 0.
        ///
        /// `숫자+b`(십억) 또는 `숫자+m`(백만) 중 **가장 앞의 것**을 취한다.
        /// 앞의 것인 이유: MoE 표기 `gemma4:26b-a4b` 는 총 26B / 활성 4B 이고, 메모리를 결정하는 건 총량이다.
        /// 양자화·포맷 접미사(`-it-q4_K_M`, `-mlx-bf16`, `:q4-k-m`)의 숫자는 크기 표기 뒤에 오므로 영향이 없고,
        /// 계열 번호(`qwen3.5:`, `gemma4:`, `llama-3.3-`)는 뒤에 b/m 이 붙지 않아 자연히 걸러진다.
        /// </summary>
        private static float ParseParamBillions(string m)
        {
            for (int i = 0; i < m.Length; i++)
            {
                if (!char.IsDigit(m[i])) continue;

                int start = i;
                int j = i;
                while (j < m.Length && (char.IsDigit(m[j]) || m[j] == '.')) j++;
                if (j >= m.Length) break;  // 숫자로 끝남 = 단위 없음

                char unit = m[j];
                if (unit != 'b' && unit != 'm') { i = j; continue; }

                // 단위 뒤에 숫자가 이어지면 크기 표기가 아니다 (방어적)
                if (j + 1 < m.Length && char.IsDigit(m[j + 1])) { i = j; continue; }

                if (!float.TryParse(m.Substring(start, j - start), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float value) || value <= 0f)
                {
                    i = j;
                    continue;
                }

                return unit == 'm' ? value / 1000f : value;
            }
            return 0f;
        }

        private static int GetOllamaContextSize(string model)
        {
            switch (GetModelSizeClass(model))
            {
                case 0: return 4096;   // Small (1-4B)
                case 2: return 16384;  // Large (27B+)
                default: return 8192;  // Mid (7-14B)
            }
        }

        // ★ v3.71.0: Model family detection for per-family sampling profiles
        internal enum ModelFamily { Gemma, Mistral, Qwen3, Qwen2, Other }

        internal static ModelFamily DetectFamily(string model)
        {
            if (string.IsNullOrEmpty(model)) return ModelFamily.Other;
            string m = model.ToLowerInvariant();
            if (m.Contains("gemma") || m.Contains("big-tiger") || m.Contains("daichi") || m.Contains("pascal")) return ModelFamily.Gemma;
            if (m.Contains("qwen3") || m.Contains("qwq")) return ModelFamily.Qwen3;
            if (m.Contains("qwen")) return ModelFamily.Qwen2;
            if (m.Contains("mistral") || m.Contains("nemo") || m.Contains("nemomix") || m.Contains("mixtral")) return ModelFamily.Mistral;
            return ModelFamily.Other;
        }

        /// <summary>
        /// ★ thinking 지원 모델 감지 — think:false 로 끄지 않으면 reasoning 에 토큰을 다 써서 content 가
        /// 비거나 지연됨(gemma4-e4b-rp 무응답 사례 — 8초 thinking, content 빔). Qwen3 + gemma4(thinking 변형) 포함.
        /// </summary>
        internal static bool IsThinkingModel(string model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            string m = model.ToLowerInvariant();
            return m.Contains("qwen3") || m.Contains("qwq")
                || m.Contains("gemma4") || m.Contains("gemma-4")
                || m.Contains("thinking") || m.Contains("reasoning") || m.Contains("-think");
        }

        /// <summary>
        /// Per-family sampling profile. Each model family has different optimal parameters.
        /// </summary>
        private static JObject BuildSamplingOptions(MachineSpiritConfig config, string model)
        {
            int numCtx = GetOllamaContextSize(model);
            var family = DetectFamily(model);

            // ★ v3.71.0: Research-backed optimal parameters per family
            // Sources: Google official (Gemma), community RP benchmarks (Mistral/NemoMix),
            //          Qwen official docs, Ollama GitHub issues (#9871, #14493, #10159)
            switch (family)
            {
                case ModelFamily.Gemma:
                    return new JObject
                    {
                        ["temperature"] = 1.0,    // Google official — Ollama may default to 0.1 (bug #9871)
                        ["top_k"] = 64,            // Google official — Ollama default 40 is too low
                        ["top_p"] = 0.95,
                        ["min_p"] = 0.01,
                        ["repeat_penalty"] = 1.0,  // Must be 1.0 — Gemma degrades with penalty
                        ["repeat_last_n"] = 256,
                        ["num_ctx"] = numCtx,
                        ["num_predict"] = config.MaxTokens
                    };
                case ModelFamily.Mistral:
                    return new JObject
                    {
                        ["temperature"] = 1.0,     // Mistral/NeMo: high temp for creative RP
                        ["top_k"] = 40,
                        ["top_p"] = 0.95,
                        ["min_p"] = 0.1,            // Critical — below 0.1 causes logic breakdown
                        ["repeat_penalty"] = 1.1,   // NeMo has inherent repetition tendency
                        ["repeat_last_n"] = 128,
                        ["num_ctx"] = numCtx,
                        ["num_predict"] = config.MaxTokens,
                        // ★ v3.74.0: Stop sequences to prevent RP-tuned models from narrating
                        ["stop"] = new JArray(
                            "\n**",            // Bold character name format
                            "\nLord Captain:", // Writing as player
                            "\n*"              // Asterisk action narration
                        )
                    };
                case ModelFamily.Qwen3:
                    return new JObject
                    {
                        ["temperature"] = 0.7,     // Qwen official — >0.8 causes repetition loops
                        ["top_k"] = 20,             // Conservative for stability
                        ["top_p"] = 0.8,
                        ["min_p"] = 0.05,
                        ["repeat_penalty"] = 1.05,
                        ["repeat_last_n"] = 64,
                        ["num_ctx"] = numCtx,
                        ["num_predict"] = config.MaxTokens
                    };
                default: // Qwen2, Other
                    return new JObject
                    {
                        ["temperature"] = 0.7,     // Qwen2.5 official
                        ["top_k"] = 20,
                        ["top_p"] = 0.8,
                        ["min_p"] = 0.05,
                        ["repeat_penalty"] = 1.05,
                        ["repeat_last_n"] = 128,
                        ["num_ctx"] = numCtx,
                        ["num_predict"] = config.MaxTokens,
                        // ★ v3.74.0: Stop sequences to prevent RP-tuned models from narrating
                        ["stop"] = new JArray(
                            "\n**",            // Bold character name format
                            "\nLord Captain:", // Writing as player
                            "\n*"              // Asterisk action narration
                        )
                    };
            }
        }

        /// <summary>
        /// Send streaming request to Ollama's native /api/chat endpoint.
        /// Provides real-time token display + full sampling parameter control.
        /// onToken is called per-frame with accumulated new tokens.
        /// </summary>
        public static IEnumerator SendOllamaStreaming(
            MachineSpiritConfig config,
            List<ChatMessage> messages,
            Action<string> onToken,
            Action onComplete,
            Action<string> onError)
        {
            if (_isRequesting)
            {
                onError?.Invoke("Request already in progress");
                yield break;
            }
            _isRequesting = true;

            // ★ v3.71.0: Per-family sampling profiles
            var requestBody = new JObject
            {
                ["model"] = config.Model,
                ["messages"] = JArray.FromObject(messages),
                ["stream"] = true,
                ["keep_alive"] = -1,
                ["options"] = BuildSamplingOptions(config, config.Model)
            };

            // thinking 모델은 think:false 필수 — 안 끄면 reasoning 에 토큰 소진→content 빔(무응답). gemma4 포함.
            if (IsThinkingModel(config.Model))
                requestBody["think"] = false;

            // Convert OpenAI-compatible URL (/v1) to native Ollama endpoint (/api/chat)
            string baseUrl = config.ApiUrl.TrimEnd('/');
            if (baseUrl.EndsWith("/v1"))
                baseUrl = baseUrl.Substring(0, baseUrl.Length - 3);
            string url = baseUrl + "/api/chat";

            Log.MachineSpirit.Debug($"[MachineSpirit] Ollama streaming → {url}, model={config.Model}");

            var handler = new StreamHandler();
            var request = new UnityWebRequest(url, "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(requestBody.ToString(Formatting.None)));
            request.downloadHandler = handler;
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = 120; // Ollama may need time for model loading

            var op = request.SendWebRequest();

            // ★ v3.71.0: Qwen3 <think> block filtering (defensive — even with think:false)
            bool filterThink = DetectFamily(config.Model) == ModelFamily.Qwen3;
            bool inThinkBlock = false;
            string thinkBuffer = "";

            // Poll for streaming tokens each frame
            while (!op.isDone)
            {
                string tokens = handler.FlushTokens();
                if (tokens != null)
                {
                    if (filterThink)
                        tokens = FilterThinkTokens(tokens, ref inThinkBlock, ref thinkBuffer);
                    if (!string.IsNullOrEmpty(tokens))
                        onToken?.Invoke(tokens);
                }
                yield return null;
            }

            // Flush any remaining tokens
            string remaining = handler.FlushTokens();
            if (remaining != null)
            {
                if (filterThink)
                    remaining = FilterThinkTokens(remaining, ref inThinkBlock, ref thinkBuffer);
                if (!string.IsNullOrEmpty(remaining))
                    onToken?.Invoke(remaining);
            }

            _isRequesting = false;

            if (request.result != UnityWebRequest.Result.Success)
            {
                string errorDetail = request.error;
                onError?.Invoke($"HTTP {request.responseCode}: {errorDetail}");
            }
            else
            {
                onComplete?.Invoke();
            }

            request.Dispose();
        }

        /// <summary>
        /// ★ v3.71.0: Filter out Qwen3 &lt;think&gt;...&lt;/think&gt; blocks from streaming tokens.
        /// Handles partial tags across chunk boundaries.
        /// </summary>
        private static string FilterThinkTokens(string text, ref bool inBlock, ref string buffer)
        {
            buffer += text;

            // Inside a think block — look for closing tag (case-insensitive: <THINK>, <Think>, etc.)
            if (inBlock)
            {
                int closeIdx = buffer.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                if (closeIdx >= 0)
                {
                    inBlock = false;
                    string result = buffer.Substring(closeIdx + 8); // after </think>
                    buffer = "";  // 버퍼를 비워야 다음 청크에서 이 잔여분이 재방출(중복)되지 않음 (same-chunk 분기와 동일)
                    return result.Length > 0 ? result : null;
                }
                // Still inside — might have partial </think> at end, keep buffering
                if (buffer.Length > 200) buffer = buffer.Substring(buffer.Length - 20); // prevent unbounded growth
                return null;
            }

            // Not in block — look for opening tag (case-insensitive)
            int openIdx = buffer.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (openIdx >= 0)
            {
                string before = buffer.Substring(0, openIdx);
                string after = buffer.Substring(openIdx + 7);

                // Check if closing tag is in same chunk (case-insensitive)
                int closeInSame = after.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                if (closeInSame >= 0)
                {
                    buffer = after.Substring(closeInSame + 8);
                    string result = before + buffer;
                    buffer = "";
                    return string.IsNullOrEmpty(result) ? null : result;
                }

                // Opening tag found, no close yet
                inBlock = true;
                buffer = "";
                return string.IsNullOrEmpty(before) ? null : before;
            }

            // No tags — check for partial "<think" at end (case-insensitive)
            if (buffer.Length > 0 && buffer[buffer.Length - 1] == '<')
            {
                string safe = buffer.Substring(0, buffer.Length - 1);
                buffer = "<";
                return string.IsNullOrEmpty(safe) ? null : safe;
            }

            // Check for partial "<thin", "<thi", etc. (case-insensitive)
            for (int i = System.Math.Min(6, buffer.Length - 1); i >= 1; i--)
            {
                string tail = buffer.Substring(buffer.Length - i).ToLowerInvariant();
                if ("<think>".StartsWith(tail, StringComparison.Ordinal))
                {
                    string safe = buffer.Substring(0, buffer.Length - i);
                    buffer = buffer.Substring(buffer.Length - i);
                    return string.IsNullOrEmpty(safe) ? null : safe;
                }
            }

            // All clean
            string output = buffer;
            buffer = "";
            return output;
        }

        /// <summary>
        /// ★ v3.74.0: Strip narration patterns from RP-tuned model output.
        /// Called on accumulated response text, not individual tokens.
        /// </summary>
        internal static string StripNarration(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // ★ 모델이 응답을 {"response":"..."} JSON 으로 감싸는 습성 방어 — 안쪽 텍스트만 추출 (잘린 JSON 포함).
            text = UnwrapJsonResponse(text);
            if (string.IsNullOrEmpty(text)) return text;

            // Remove *action descriptions* (asterisk-wrapped text)
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\*[^*]+\*", "");

            // Remove **Bold Name:** dialogue format
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\*\*\w+:\*\*\s*", "");

            // Remove lines starting with third-person narration
            var lines = text.Split('\n');
            var sb = new StringBuilder();
            foreach (var line in lines)
            {
                string trimmed = line.TrimStart();
                // Skip lines that start with third-person narration patterns
                if (System.Text.RegularExpressions.Regex.IsMatch(trimmed,
                    @"^(She |He |They |The \w+ |[A-Z]\w+ (said|walked|looked|raised|drew|turned|smiled|nodded|shook|whispered|muttered|sighed|laughed|frowned|grinned|glanced))"))
                    continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(line);
            }

            return sb.ToString().Trim();
        }

        /// <summary>
        /// 모델이 응답을 {"response":"..."} (또는 content/message/text/reply) JSON 으로 감쌀 때 안쪽 텍스트만 추출.
        /// 프롬프트 규칙으로 1차 차단하되, 모델이 무시할 경우의 안전망. 스트리밍 중 잘린 JSON 도 관대 처리.
        /// </summary>
        internal static string UnwrapJsonResponse(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string t = text.TrimStart();
            if (t.Length == 0 || t[0] != '{') return text;  // JSON 봉투 아님 — 그대로

            // 1) 정상 JSON 파싱 시도
            try
            {
                var obj = Newtonsoft.Json.Linq.JObject.Parse(t);
                var field = obj["response"] ?? obj["content"] ?? obj["message"] ?? obj["text"] ?? obj["reply"];
                if (field != null && field.Type == Newtonsoft.Json.Linq.JTokenType.String)
                    return field.ToString();
            }
            catch { /* 잘리거나 malformed — 아래 lenient 추출로 폴백 */ }

            // 2) lenient: "response":"..." 안쪽만 추출 (스트리밍 중간 끊김 대응)
            var m = System.Text.RegularExpressions.Regex.Match(t, "\"(?:response|content|message|text|reply)\"\\s*:\\s*\"");
            if (!m.Success) return text;
            string inner = t.Substring(m.Index + m.Length).TrimEnd();
            if (inner.EndsWith("}")) inner = inner.Substring(0, inner.Length - 1).TrimEnd();
            if (inner.EndsWith("\"")) inner = inner.Substring(0, inner.Length - 1);
            return inner.Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\"", "\"");
        }

        // ════════════════════════════════════════════════════════════
        // OpenAI-Compatible API — Non-streaming (Gemini, Groq, OpenAI, Custom)
        // ════════════════════════════════════════════════════════════

        public static IEnumerator SendChatRequest(
            MachineSpiritConfig config,
            List<ChatMessage> messages,
            Action<string> onResponse,
            Action<string> onError)
        {
            if (_isRequesting)
            {
                onError?.Invoke("Request already in progress");
                yield break;
            }

            _isRequesting = true;

            bool isThinkingModel = config.Provider == ApiProvider.Gemini;

            var requestBody = new JObject
            {
                ["model"] = config.Model,
                ["messages"] = JArray.FromObject(messages),
                ["temperature"] = config.Temperature
            };

            if (!isThinkingModel)
                requestBody["max_tokens"] = config.MaxTokens;

            string url = config.ApiUrl.TrimEnd('/') + "/chat/completions";
            string json = requestBody.ToString(Formatting.None);

            var request = new UnityWebRequest(url, "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            if (!string.IsNullOrEmpty(config.ApiKey))
                request.SetRequestHeader("Authorization", $"Bearer {config.ApiKey}");

            request.timeout = config.Provider == ApiProvider.Ollama ? 120 : 30;

            yield return request.SendWebRequest();

            _isRequesting = false;

            if (request.result != UnityWebRequest.Result.Success)
            {
                onError?.Invoke($"HTTP {request.responseCode}: {request.error}");
                request.Dispose();
                yield break;
            }

            try
            {
                var response = JObject.Parse(request.downloadHandler.text);
                var choice = response["choices"]?[0];
                var content = choice?["message"]?["content"]?.ToString();
                var finishReason = choice?["finish_reason"]?.ToString();

                string tokensInfo = isThinkingModel ? "unlimited (thinking model)" : config.MaxTokens.ToString();
                if (finishReason == "length")
                    Log.MachineSpirit.Info($"[MachineSpirit] Response truncated (finish_reason=length, max_tokens={tokensInfo}).");
                Log.MachineSpirit.Debug($"[MachineSpirit] finish_reason={finishReason}, max_tokens={tokensInfo}, response_len={content?.Length ?? 0}");

                if (string.IsNullOrEmpty(content))
                    onError?.Invoke("Empty response from LLM");
                else
                    onResponse?.Invoke(content.Trim());
            }
            catch (Exception ex)
            {
                onError?.Invoke($"Parse error: {ex.Message}");
            }

            request.Dispose();
        }

        // ════════════════════════════════════════════════════════════
        // Background Request — For conversation summary (independent of _isRequesting)
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// Lightweight background request for conversation summarization.
        /// Does not block user-facing requests (_isRequesting is not set).
        /// Uses Ollama native API (non-streaming) or OpenAI-compatible depending on provider.
        /// </summary>
        public static IEnumerator SendBackgroundRequest(
            MachineSpiritConfig config,
            List<ChatMessage> messages,
            Action<string> onResponse)
        {
            string url;
            JObject requestBody;

            if (config.Provider == ApiProvider.Ollama)
            {
                // Ollama native API (non-streaming) for best compatibility
                string baseUrl = config.ApiUrl.TrimEnd('/');
                if (baseUrl.EndsWith("/v1"))
                    baseUrl = baseUrl.Substring(0, baseUrl.Length - 3);
                url = baseUrl + "/api/chat";

                requestBody = new JObject
                {
                    ["model"] = config.Model,
                    ["messages"] = JArray.FromObject(messages),
                    ["stream"] = false,
                    ["keep_alive"] = -1,
                    ["options"] = new JObject
                    {
                        ["temperature"] = 0.3, // Low temperature for factual summary
                        ["repeat_penalty"] = DetectFamily(config.Model) == ModelFamily.Mistral ? 1.05 : 1.0,
                        ["num_predict"] = 200,
                        ["num_ctx"] = 4096
                    }
                };

                // thinking 모델 think:false (gemma4 등 포함 — content 빔 방지)
                if (IsThinkingModel(config.Model))
                    requestBody["think"] = false;
            }
            else
            {
                url = config.ApiUrl.TrimEnd('/') + "/chat/completions";
                requestBody = new JObject
                {
                    ["model"] = config.Model,
                    ["messages"] = JArray.FromObject(messages),
                    ["temperature"] = 0.3,
                    ["max_tokens"] = 200
                };
            }

            var request = new UnityWebRequest(url, "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(requestBody.ToString(Formatting.None)));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(config.ApiKey))
                request.SetRequestHeader("Authorization", $"Bearer {config.ApiKey}");
            request.timeout = 30;

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    string content;
                    if (config.Provider == ApiProvider.Ollama)
                    {
                        content = JObject.Parse(request.downloadHandler.text)["message"]?["content"]?.ToString();
                    }
                    else
                    {
                        content = JObject.Parse(request.downloadHandler.text)["choices"]?[0]?["message"]?["content"]?.ToString();
                    }
                    if (!string.IsNullOrEmpty(content))
                        onResponse?.Invoke(content.Trim());
                }
                catch (Exception ex)
                {
                    Log.MachineSpirit.Error(ex, $"[MachineSpirit] Summary parse error");
                }
            }

            request.Dispose();
        }
    }
}
