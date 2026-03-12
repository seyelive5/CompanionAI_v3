// MachineSpirit/MachineSpirit.cs
using System.Collections.Generic;
using UnityEngine;

namespace CompanionAI_v3.MachineSpirit
{
    public static class MachineSpirit
    {
        private const int MAX_CHAT_HISTORY = 100;
        private const float SPONTANEOUS_COOLDOWN = 30f;
        private static readonly List<ChatMessage> _chatHistory = new List<ChatMessage>();
        private static MachineSpiritConfig Config => Main.Settings?.MachineSpirit;
        private static float _lastSpontaneousTime;

        public static bool IsActive =>
            Config != null && Config.Enabled && !string.IsNullOrEmpty(Config.ApiUrl);

        public static void Initialize()
        {
            GameEventCollector.Subscribe();
            CoroutineRunner.EnsureInstance(); // OnGUI 렌더링을 위해 즉시 생성
        }

        public static void Shutdown()
        {
            GameEventCollector.Unsubscribe();
            GameEventCollector.Clear();
            _chatHistory.Clear();
            LLMClient.Reset();
        }

        public static void OnGUI()
        {
            if (!IsActive) return;
            ChatWindow.OnGUI(Config, _chatHistory);
        }

        private static void TrimHistory()
        {
            while (_chatHistory.Count > MAX_CHAT_HISTORY)
                _chatHistory.RemoveAt(0);
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
            TrimHistory();

            var messages = ContextBuilder.Build(_chatHistory);
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
            if (Time.time - _lastSpontaneousTime < SPONTANEOUS_COOLDOWN) return;
            _lastSpontaneousTime = Time.time;

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
    /// Also handles OnGUI for ChatWindow (Main.OnGUI only fires when UMM settings are open).
    /// </summary>
    public class CoroutineRunner : MonoBehaviour
    {
        private static CoroutineRunner _instance;

        public static void Start(System.Collections.IEnumerator coroutine)
        {
            EnsureInstance();
            _instance.StartCoroutine(coroutine);
        }

        public static void EnsureInstance()
        {
            if (_instance != null) return;
            var go = new GameObject("CompanionAI_CoroutineRunner");
            go.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<CoroutineRunner>();
        }

        /// <summary>
        /// Unity calls this every frame — renders ChatWindow independently of UMM settings panel.
        /// </summary>
        private void OnGUI()
        {
            MachineSpirit.OnGUI();
        }
    }
}
