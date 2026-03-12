// MachineSpirit/LLMClient.cs
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

        /// <summary>Reset state on mod shutdown (clears stuck request flag).</summary>
        public static void Reset() => _isRequesting = false;

        // Message format for OpenAI chat completions
        public class ChatMessage
        {
            [JsonProperty("role")] public string Role;
            [JsonProperty("content")] public string Content;
        }

        /// <summary>
        /// Send chat completion request. Calls onResponse with the reply text,
        /// or onError with error message. Non-blocking via coroutine.
        /// </summary>
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

            // Gemini 2.5 "thinking" models count internal reasoning tokens against max_tokens,
            // so a low cap (e.g. 750) leaves almost nothing for the visible response.
            // Solution: don't send max_tokens at all — let the API use its default.
            // The system prompt already instructs "2-3 sentences max" to keep responses short.
            // Only include max_tokens for non-thinking providers (Ollama, Groq, OpenAI)
            // where it acts as a reasonable safety cap.
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

            // Ollama needs 60-120s on first request (model loading into VRAM)
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

                // Log truncation diagnostics
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
    }
}
