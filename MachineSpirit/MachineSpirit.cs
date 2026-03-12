// MachineSpirit/MachineSpirit.cs
using System.Collections.Generic;
using UnityEngine;

namespace CompanionAI_v3.MachineSpirit
{
    public static class MachineSpirit
    {
        private static readonly List<ChatMessage> _chatHistory = new List<ChatMessage>();
        private static MachineSpiritConfig Config => Main.Settings?.MachineSpirit;

        public static bool IsActive =>
            Config != null && Config.Enabled && !string.IsNullOrEmpty(Config.ApiUrl);

        public static void Initialize()
        {
            GameEventCollector.Subscribe();
        }

        public static void Shutdown()
        {
            GameEventCollector.Unsubscribe();
            GameEventCollector.Clear();
            _chatHistory.Clear();
        }

        public static void OnGUI()
        {
            if (!IsActive) return;
            ChatWindow.OnGUI(Config, _chatHistory);
        }

        public static void OnUserMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            _chatHistory.Add(new ChatMessage
            {
                IsUser = true,
                Text = text,
                Timestamp = Time.time
            });

            var messages = ContextBuilder.Build(_chatHistory, text);
            ChatWindow.SetThinking(true);

            CoroutineRunner.Start(LLMClient.SendChatRequest(
                Config, messages,
                onResponse: response =>
                {
                    _chatHistory.Add(new ChatMessage
                    {
                        IsUser = false,
                        Text = response,
                        Timestamp = Time.time
                    });
                    ChatWindow.SetThinking(false);
                },
                onError: error =>
                {
                    _chatHistory.Add(new ChatMessage
                    {
                        IsUser = false,
                        Text = $"[ERROR] {error}",
                        Timestamp = Time.time
                    });
                    ChatWindow.SetThinking(false);
                }
            ));
        }

        public static void OnMajorEvent(GameEvent evt)
        {
            if (!IsActive) return;
            if (LLMClient.IsRequesting) return;

            var messages = ContextBuilder.BuildForEvent(evt, _chatHistory);
            ChatWindow.SetThinking(true);

            CoroutineRunner.Start(LLMClient.SendChatRequest(
                Config, messages,
                onResponse: response =>
                {
                    _chatHistory.Add(new ChatMessage
                    {
                        IsUser = false,
                        Text = response,
                        Timestamp = Time.time
                    });
                    ChatWindow.SetThinking(false);
                },
                onError: _ => ChatWindow.SetThinking(false)
            ));
        }
    }

    /// <summary>
    /// MonoBehaviour wrapper to run coroutines from static context.
    /// </summary>
    public class CoroutineRunner : MonoBehaviour
    {
        private static CoroutineRunner _instance;

        public static void Start(System.Collections.IEnumerator coroutine)
        {
            EnsureInstance();
            _instance.StartCoroutine(coroutine);
        }

        private static void EnsureInstance()
        {
            if (_instance != null) return;
            var go = new GameObject("CompanionAI_CoroutineRunner");
            go.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<CoroutineRunner>();
        }
    }
}
