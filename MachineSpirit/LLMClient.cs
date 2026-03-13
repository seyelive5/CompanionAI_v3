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
        private static int GetOllamaContextSize(string model)
        {
            if (string.IsNullOrEmpty(model)) return 8192;
            string m = model.ToLowerInvariant();
            if (m.Contains("27b") || m.Contains("70b")) return 16384;
            if (m.Contains("4b") || m.Contains("3b") || m.Contains("1b")) return 4096;
            return 8192; // Default for 7B-12B range
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

            // Build native Ollama API request with full parameter control
            // Sampling params based on Gemma 3 research + community best practices:
            //   top_p=0.95, min_p=0.01 → suppress hallucination without killing creativity
            //   repeat_penalty=1.0 → Gemma models produce more natural text with penalty disabled
            //   num_ctx: model-specific (4B→4096, 12B→8192, 27B→16384)
            var requestBody = new JObject
            {
                ["model"] = config.Model,
                ["messages"] = JArray.FromObject(messages),
                ["stream"] = true,
                ["options"] = new JObject
                {
                    ["temperature"] = config.Temperature,
                    ["top_p"] = 0.95,
                    ["min_p"] = 0.01,
                    ["repeat_penalty"] = 1.0,
                    ["num_ctx"] = GetOllamaContextSize(config.Model),
                    ["num_predict"] = config.MaxTokens
                }
            };

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

            // Poll for streaming tokens each frame
            while (!op.isDone)
            {
                string tokens = handler.FlushTokens();
                if (tokens != null)
                    onToken?.Invoke(tokens);
                yield return null;
            }

            // Flush any remaining tokens
            string remaining = handler.FlushTokens();
            if (remaining != null)
                onToken?.Invoke(remaining);

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
                    ["options"] = new JObject
                    {
                        ["temperature"] = 0.3, // Low temperature for factual summary
                        ["num_predict"] = 200,
                        ["num_ctx"] = 4096
                    }
                };
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
