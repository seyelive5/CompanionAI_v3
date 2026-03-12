// MachineSpirit/LLMClient.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CompanionAI_v3.MachineSpirit
{
    public static class LLMClient
    {
        private static bool _isRequesting;
        public static bool IsRequesting => _isRequesting;

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

            var requestBody = new JObject
            {
                ["model"] = config.Model,
                ["messages"] = JArray.FromObject(messages),
                ["max_tokens"] = config.MaxTokens,
                ["temperature"] = config.Temperature
            };

            string url = config.ApiUrl.TrimEnd('/') + "/chat/completions";
            string json = requestBody.ToString(Formatting.None);

            var request = new UnityWebRequest(url, "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            if (!string.IsNullOrEmpty(config.ApiKey))
                request.SetRequestHeader("Authorization", $"Bearer {config.ApiKey}");

            request.timeout = 30;

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
                var content = response["choices"]?[0]?["message"]?["content"]?.ToString();
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
