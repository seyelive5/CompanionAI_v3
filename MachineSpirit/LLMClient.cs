// MachineSpirit/LLMClient.cs
// ★ v3.58.0: Ollama native API streaming + sampling parameters
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CompanionAI_v3.Settings;

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
        /// <summary>
        /// Classify model into size tier: 0=small(1-4B), 1=mid(7-14B), 2=large(27B+).
        /// Handles both parameter-count naming (gemma3:4b) and known model names (mistral-nemo).
        /// </summary>
        public static int GetModelSizeClass(string model)
        {
            if (string.IsNullOrEmpty(model)) return 1;
            string m = model.ToLowerInvariant();

            // Known model name → size mapping (models without size in name)
            if (m.Contains("mistral-nemo") || m.Contains("nemomix")) return 1;  // 12B

            // Parameter count in name
            if (m.Contains("27b") || m.Contains("32b") || m.Contains("70b") || m.Contains("big-tiger")) return 2;
            if (m.Contains("4b") || m.Contains("3b") || m.Contains("1b") || m.Contains("0.6b")) return 0;
            if (m.Contains("12b") || m.Contains("14b") || m.Contains("7b") || m.Contains("8b")) return 1;

            return 1; // Default: mid-range
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
            if (m.Contains("gemma") || m.Contains("big-tiger")) return ModelFamily.Gemma;
            if (m.Contains("qwen3") || m.Contains("qwq")) return ModelFamily.Qwen3;
            if (m.Contains("qwen")) return ModelFamily.Qwen2;
            if (m.Contains("mistral") || m.Contains("nemo") || m.Contains("nemomix") || m.Contains("mixtral")) return ModelFamily.Mistral;
            return ModelFamily.Other;
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
                        ["num_predict"] = config.MaxTokens
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
                        ["num_predict"] = config.MaxTokens
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

            // Qwen3: disable thinking mode (outputs <think> blocks otherwise)
            if (DetectFamily(config.Model) == ModelFamily.Qwen3)
                requestBody["think"] = false;

            // Convert OpenAI-compatible URL (/v1) to native Ollama endpoint (/api/chat)
            string baseUrl = config.ApiUrl.TrimEnd('/');
            if (baseUrl.EndsWith("/v1"))
                baseUrl = baseUrl.Substring(0, baseUrl.Length - 3);
            string url = baseUrl + "/api/chat";

            Main.LogDebug($"[MachineSpirit] Ollama streaming → {url}, model={config.Model}");

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

            // Inside a think block — look for closing tag
            if (inBlock)
            {
                int closeIdx = buffer.IndexOf("</think>", StringComparison.Ordinal);
                if (closeIdx >= 0)
                {
                    inBlock = false;
                    buffer = buffer.Substring(closeIdx + 8); // after </think>
                    return buffer.Length > 0 ? buffer : null;
                }
                // Still inside — might have partial </think> at end, keep buffering
                if (buffer.Length > 200) buffer = buffer.Substring(buffer.Length - 20); // prevent unbounded growth
                return null;
            }

            // Not in block — look for opening tag
            int openIdx = buffer.IndexOf("<think>", StringComparison.Ordinal);
            if (openIdx >= 0)
            {
                string before = buffer.Substring(0, openIdx);
                string after = buffer.Substring(openIdx + 7);

                // Check if closing tag is in same chunk
                int closeInSame = after.IndexOf("</think>", StringComparison.Ordinal);
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

            // No tags — check for partial "<think" at end
            if (buffer.Length > 0 && buffer[buffer.Length - 1] == '<')
            {
                string safe = buffer.Substring(0, buffer.Length - 1);
                buffer = "<";
                return string.IsNullOrEmpty(safe) ? null : safe;
            }

            // Check for partial "<thin", "<thi", etc.
            for (int i = System.Math.Min(6, buffer.Length - 1); i >= 1; i--)
            {
                if ("<think>".StartsWith(buffer.Substring(buffer.Length - i)))
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
                    Main.Log($"[MachineSpirit] Response truncated (finish_reason=length, max_tokens={tokensInfo}).");
                Main.LogDebug($"[MachineSpirit] finish_reason={finishReason}, max_tokens={tokensInfo}, response_len={content?.Length ?? 0}");

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

                // Qwen3: disable thinking mode
                if (DetectFamily(config.Model) == ModelFamily.Qwen3)
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
                    Main.LogDebug($"[MachineSpirit] Summary parse error: {ex.Message}");
                }
            }

            request.Dispose();
        }
    }
}
