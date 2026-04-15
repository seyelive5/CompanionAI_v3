// MachineSpirit/MachineSpirit.cs
// ★ v3.58.0: Ollama streaming routing + background conversation summary
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using CompanionAI_v3.Settings;
using Newtonsoft.Json;
using UnityEngine;

namespace CompanionAI_v3.MachineSpirit
{
    public static class MachineSpirit
    {
        private const int MAX_CHAT_HISTORY = 100;
        private const float SPONTANEOUS_COOLDOWN = 15f;
        private const float DIALOGUE_COOLDOWN = 30f; // ★ v3.66.0: Separate cooldown for dialogue reactions
        private const float AREA_TRANSITION_COOLDOWN = 30f;
        private const int SUMMARY_THRESHOLD = 30; // Summarize when history exceeds this
        private const int SUMMARY_WINDOW = 20;    // Number of old messages to summarize

        private static readonly List<ChatMessage> _chatHistory = new List<ChatMessage>();
        private static MachineSpiritConfig Config => Main.Settings?.MachineSpirit;
        private static float _lastSpontaneousTime;
        private static float _lastDialogueCommentTime; // ★ v3.66.0
        private static float _lastAreaTransitionTime;
        private static bool _hasGreeted;
        private static bool _isTemplateChecking; // ★ v3.72.0: Block LLM calls during template check

        // ★ v3.60.0: Idle commentary
        private static float _lastActivityTime;
        private static float _nextIdleTextTime;
        private static float _nextIdleVisionTime;
        private static bool _idleVisionPending;

        // ★ v3.68.0: Polling for entity-bound events
        private static float _lastPollTime;
        private static int _lastKnownLevelTotal;
        private static bool _wasInWarp;

        private static readonly Dictionary<IdleFrequency, (float textInterval, float visionInterval)> IdleIntervals
            = new Dictionary<IdleFrequency, (float, float)>
        {
            { IdleFrequency.Off,    (float.MaxValue, float.MaxValue) },
            { IdleFrequency.Low,    (240f, 600f) },
            { IdleFrequency.Medium, (120f, 360f) },
            { IdleFrequency.High,   (60f,  180f) },
        };

        // ★ Conversation summary (background summarization of old messages)
        private static string _conversationSummary;
        private static bool _isSummarizing;
        private static int _summarizedUpToIndex; // Last message index that was included in summary

        public static bool IsActive =>
            Config != null && Config.Enabled && !string.IsNullOrEmpty(Config.ApiUrl);

        private static void ResetIdleTimers()
        {
            var intervals = IdleIntervals[Config?.IdleMode ?? IdleFrequency.Off];
            _nextIdleTextTime = Time.time + intervals.textInterval;
            _nextIdleVisionTime = Time.time + intervals.visionInterval;
        }

        public static void Initialize()
        {
            GameEventCollector.Subscribe();
            CoroutineRunner.EnsureInstance(); // OnGUI 렌더링을 위해 즉시 생성
            LoadChatHistory();
            _lastActivityTime = Time.time;
            ResetIdleTimers();
            _hasGreeted = false;
            _lastKnownLevelTotal = 0;
            _wasInWarp = false;
            _lastPollTime = 0f;
            EventCoalescer.Clear();

            // ★ v3.70.0: Start background knowledge indexing (if enabled)
            if (Config.EnableKnowledge)
                Knowledge.KnowledgeIndex.StartIndexing();

            // ★ v3.71.0: Auto-fix template for community models on Ollama
            if (Config.Provider == ApiProvider.Ollama && !string.IsNullOrEmpty(Config.Model))
                CoroutineRunner.Start(CheckAndApplyTemplateFix());
        }

        /// <summary>
        /// ★ v3.71.0: Check current Ollama model's template and auto-fix if missing.
        /// Runs on Initialize and when model selection changes.
        /// </summary>
        private static IEnumerator CheckAndApplyTemplateFix()
        {
            _isTemplateChecking = true;
            try
            {
                // Small delay to let Ollama server be ready
                yield return new UnityEngine.WaitForSeconds(2f);

                if (Config == null || Config.Provider != ApiProvider.Ollama)
                    yield break;

                yield return OllamaSetup.CheckAndFixTemplate(Config.Model);

                if (!string.IsNullOrEmpty(OllamaSetup.TemplateFixedModel))
                {
                    Main.LogDebug($"[MachineSpirit] Switching to template-fixed model: {OllamaSetup.TemplateFixedModel}");
                    Config.Model = OllamaSetup.TemplateFixedModel;
                    OllamaSetup.TemplateFixedModel = null;
                }
            }
            finally
            {
                _isTemplateChecking = false;
            }
        }

        public static void Shutdown()
        {
            SaveChatHistory();
            GameEventCollector.Unsubscribe();
            GameEventCollector.Clear();
            _chatHistory.Clear();
            _conversationSummary = null;
            _isSummarizing = false;
            _summarizedUpToIndex = 0;
            LLMClient.Reset();
        }

        // ════════════════════════════════════════════════════════════
        // Chat History Persistence
        // ════════════════════════════════════════════════════════════

        private static string GetChatHistoryPath()
        {
            // Save next to the mod DLL in UMM folder
            string modDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            return Path.Combine(modDir ?? ".", "chat_history.json");
        }

        [Serializable]
        private class SavedChat
        {
            public List<ChatMessage> Messages;
            public string Summary;
        }

        public static void SaveChatHistory()
        {
            if (_chatHistory.Count == 0) return;
            try
            {
                var data = new SavedChat
                {
                    Messages = new List<ChatMessage>(_chatHistory),
                    Summary = _conversationSummary
                };
                string json = JsonConvert.SerializeObject(data, Formatting.Indented);
                File.WriteAllText(GetChatHistoryPath(), json);
                Main.LogDebug($"[MachineSpirit] Chat saved: {_chatHistory.Count} messages");
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[MachineSpirit] Save failed: {ex.Message}");
            }
        }

        public static void LoadChatHistory()
        {
            try
            {
                string path = GetChatHistoryPath();
                if (!File.Exists(path)) return;

                string json = File.ReadAllText(path);
                var data = JsonConvert.DeserializeObject<SavedChat>(json);
                if (data?.Messages != null && data.Messages.Count > 0)
                {
                    _chatHistory.Clear();
                    _chatHistory.AddRange(data.Messages);
                    _conversationSummary = data.Summary;
                    _summarizedUpToIndex = 0; // Will re-evaluate on next summarization pass

                    // ★ v3.72.0: Clear stale event/dialogue buffers on history restore
                    GameEventCollector.ClearDialogueBuffer();
                    GameEventCollector.ClearEvents();

                    Main.LogDebug($"[MachineSpirit] Chat loaded: {_chatHistory.Count} messages");
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[MachineSpirit] Load failed: {ex.Message}");
            }
        }

        // ★ v3.62.0: Clear history on personality change to prevent style bleed
        public static void ClearChatHistory()
        {
            // ★ v3.70.0: Cancel any in-flight LLM request before clearing
            LLMClient.Reset();
            ChatWindow.SetThinking(false);

            _chatHistory.Clear();
            _conversationSummary = null;
            _summarizedUpToIndex = 0;
            try { File.Delete(GetChatHistoryPath()); }
            catch { /* ignore */ }
            Main.LogDebug("[MachineSpirit] Chat history cleared (personality change)");
        }

        /// <summary>
        /// ★ v3.72.0: Handle model switch — reset state for clean transition.
        /// Called from UI when user selects a different model.
        /// </summary>
        public static void OnModelChanged(string newModel)
        {
            if (Config == null) return;
            string oldModel = Config.Model;
            if (oldModel == newModel) return;

            Main.LogDebug($"[MachineSpirit] Model changed: {oldModel} → {newModel}");

            // 1. Cancel any in-flight request
            LLMClient.Reset();
            ChatWindow.SetThinking(false);

            // 2. Clear chat history (old model's responses will confuse new model)
            _chatHistory.Clear();
            _conversationSummary = null;
            _summarizedUpToIndex = 0;

            // 3. Set new model
            Config.Model = newModel;

            // 4. Re-check template for community models
            if (Config.Provider == ApiProvider.Ollama)
                CoroutineRunner.Start(CheckAndApplyTemplateFix());

            // 5. Trigger fresh greeting
            _hasGreeted = false;
            _lastActivityTime = Time.time;
            ResetIdleTimers();
        }

        /// <summary>★ v3.72.0: Reset and re-greet when personality changes.</summary>
        public static void OnPersonalityChanged()
        {
            if (Config == null) return;

            Main.LogDebug($"[MachineSpirit] Personality changed to: {Config.Personality}");

            // 1. Cancel any in-flight request
            LLMClient.Reset();
            ChatWindow.SetThinking(false);

            // 2. Clear chat history (old personality responses will confuse new one)
            _chatHistory.Clear();
            _conversationSummary = null;
            _summarizedUpToIndex = 0;

            // 3. Trigger fresh greeting with new personality
            _hasGreeted = false;
            _lastActivityTime = Time.time;
            ResetIdleTimers();
        }

        /// <summary>Add a system notification to chat (not from LLM, just info text).</summary>
        public static void AddSystemMessage(string text)
        {
            _chatHistory.Add(new ChatMessage
            {
                IsUser = false,
                Text = text,
                Timestamp = UnityEngine.Time.time,
                Category = MessageCategory.Scan
            });
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
            _lastActivityTime = Time.time;
            ResetIdleTimers();

            if (string.IsNullOrWhiteSpace(text)) return;

            _chatHistory.Add(new ChatMessage
            {
                IsUser = true,
                Text = text,
                Timestamp = Time.time
            });
            TrimHistory();

            // ★ v3.70.0: RAG — detect game knowledge questions and inject search results
            List<Knowledge.SearchResult> searchResults = null;
            if (Config.EnableKnowledge && Knowledge.KnowledgeIndex.IsReady)
            {
                searchResults = Knowledge.KnowledgeIndex.DetectAndSearch(text);
            }

            var messages = (searchResults != null && searchResults.Count > 0)
                ? ContextBuilder.BuildForKnowledgeQuery(text, searchResults, _chatHistory, Config, _conversationSummary)
                : ContextBuilder.Build(_chatHistory, Config, conversationSummary: _conversationSummary);
            ChatWindow.SetThinking(true);

            if (Config.Provider == ApiProvider.Ollama)
            {
                // ★ Streaming: add empty placeholder, update it token by token
                _chatHistory.Add(new ChatMessage { IsUser = false, Text = "", Timestamp = Time.time });
                int responseIdx = _chatHistory.Count - 1;

                CoroutineRunner.Start(LLMClient.SendOllamaStreaming(
                    Config, messages,
                    onToken: tokens =>
                    {
                        var msg = _chatHistory[responseIdx];
                        msg.Text += tokens;
                        _chatHistory[responseIdx] = msg;
                        ChatWindow.SetThinking(false); // Clear "Cogitating..." on first token
                    },
                    onComplete: () =>
                    {
                        if (responseIdx < _chatHistory.Count)
                        {
                            var msg = _chatHistory[responseIdx];
                            // ★ v3.74.0: Strip narration patterns from RP models
                            msg.Text = LLMClient.StripNarration(msg.Text);
                            _chatHistory[responseIdx] = msg;

                            if (string.IsNullOrWhiteSpace(msg.Text))
                                _chatHistory.RemoveAt(responseIdx);
                        }
                        ChatWindow.SetThinking(false);
                        MaybeSummarize();
                    },
                    onError: error =>
                    {
                        if (responseIdx < _chatHistory.Count)
                        {
                            var msg = _chatHistory[responseIdx];
                            if (string.IsNullOrEmpty(msg.Text?.Trim()))
                                _chatHistory.RemoveAt(responseIdx);
                        }
                        ChatWindow.SetThinking(false);
                        Main.LogDebug($"[MachineSpirit] LLM error (silent): {error}");
                    }
                ));
            }
            else
            {
                // ★ Non-streaming: wait for complete response (Gemini, Groq, OpenAI, Custom)
                CoroutineRunner.Start(LLMClient.SendChatRequest(
                    Config, messages,
                    onResponse: response =>
                    {
                        if (!string.IsNullOrEmpty(response?.Trim()))
                        {
                            _chatHistory.Add(new ChatMessage
                            {
                                IsUser = false,
                                Text = response,
                                Timestamp = Time.time
                            });
                        }
                        ChatWindow.SetThinking(false);
                        MaybeSummarize();
                    },
                    onError: error =>
                    {
                        ChatWindow.SetThinking(false);
                        Main.LogDebug($"[MachineSpirit] LLM error (silent): {error}");
                    }
                ));
            }
        }

        // ★ v3.94.0: 전투 중 자동 대화 비활성화 — 전투 몰입 방해 피드백 반영
        // 전투 이벤트(CombatStart/End, UnitDeath, 대미지)에 대한 LLM 반응을 중단.
        // 이벤트 수집(GameEventCollector)은 그대로 유지하여 컨텍스트 빌드에는 활용 가능.
        public static void OnMajorEvent(GameEvent evt)
        {
            // ★ v3.94.0: Combat dialogue disabled — events still collected for context
            return;

            /*
            if (!IsActive) return;
            if (LLMClient.IsRequesting) return;
            if (Time.time - _lastSpontaneousTime < SPONTANEOUS_COOLDOWN) return;
            _lastSpontaneousTime = Time.time;
            _lastActivityTime = Time.time;
            ResetIdleTimers();

            var messages = ContextBuilder.BuildForEvent(evt, _chatHistory, Config, _conversationSummary);
            ChatWindow.SetThinking(true);

            if (Config.Provider == ApiProvider.Ollama)
            {
                _chatHistory.Add(new ChatMessage { IsUser = false, Text = "", Timestamp = Time.time, Category = MessageCategory.Combat });
                int responseIdx = _chatHistory.Count - 1;

                CoroutineRunner.Start(LLMClient.SendOllamaStreaming(
                    Config, messages,
                    onToken: tokens =>
                    {
                        var msg = _chatHistory[responseIdx];
                        msg.Text += tokens;
                        _chatHistory[responseIdx] = msg;
                        ChatWindow.SetThinking(false);
                    },
                    onComplete: () =>
                    {
                        if (responseIdx < _chatHistory.Count)
                        {
                            var msg = _chatHistory[responseIdx];
                            // ★ v3.74.0: Strip narration patterns from RP models
                            msg.Text = LLMClient.StripNarration(msg.Text);
                            _chatHistory[responseIdx] = msg;

                            if (string.IsNullOrWhiteSpace(msg.Text))
                                _chatHistory.RemoveAt(responseIdx);
                        }
                        ChatWindow.SetThinking(false);
                    },
                    onError: error =>
                    {
                        if (responseIdx < _chatHistory.Count)
                        {
                            var msg = _chatHistory[responseIdx];
                            if (string.IsNullOrEmpty(msg.Text?.Trim()))
                                _chatHistory.RemoveAt(responseIdx);
                        }
                        ChatWindow.SetThinking(false);
                        Main.LogDebug($"[MachineSpirit] LLM error (silent): {error}");
                    }
                ));
            }
            else
            {
                CoroutineRunner.Start(LLMClient.SendChatRequest(
                    Config, messages,
                    onResponse: response =>
                    {
                        if (!string.IsNullOrEmpty(response?.Trim()))
                        {
                            _chatHistory.Add(new ChatMessage
                            {
                                IsUser = false,
                                Text = response,
                                Timestamp = Time.time,
                                Category = MessageCategory.Combat
                            });
                        }
                        ChatWindow.SetThinking(false);
                    },
                    onError: error =>
                    {
                        ChatWindow.SetThinking(false);
                        Main.LogDebug($"[MachineSpirit] LLM error (silent): {error}");
                    }
                ));
            }
            */
        }

        // ════════════════════════════════════════════════════════════
        // ★ v3.66.0: Dialogue Reaction — comment on NPC conversations
        // ════════════════════════════════════════════════════════════

        public static void OnDialogueEvent(GameEvent evt)
        {
            if (!IsActive) return;
            if (_isTemplateChecking) return;
            if (LLMClient.IsRequesting) return;
            if (Config?.IdleMode == IdleFrequency.Off) return; // Respect idle setting
            if (Time.time - _lastDialogueCommentTime < DIALOGUE_COOLDOWN) return;
            if (Time.time - _lastSpontaneousTime < SPONTANEOUS_COOLDOWN) return;
            _lastDialogueCommentTime = Time.time;
            _lastSpontaneousTime = Time.time;
            _lastActivityTime = Time.time;
            ResetIdleTimers();

            var messages = ContextBuilder.BuildForDialogue(evt, _chatHistory, Config, _conversationSummary);
            ChatWindow.SetThinking(true);

            if (Config.Provider == ApiProvider.Ollama)
            {
                _chatHistory.Add(new ChatMessage { IsUser = false, Text = "", Timestamp = Time.time, Category = MessageCategory.Vox });
                int responseIdx = _chatHistory.Count - 1;

                CoroutineRunner.Start(LLMClient.SendOllamaStreaming(
                    Config, messages,
                    onToken: tokens =>
                    {
                        var msg = _chatHistory[responseIdx];
                        msg.Text += tokens;
                        _chatHistory[responseIdx] = msg;
                        ChatWindow.SetThinking(false);
                    },
                    onComplete: () =>
                    {
                        if (responseIdx < _chatHistory.Count)
                        {
                            var msg = _chatHistory[responseIdx];
                            // ★ v3.74.0: Strip narration patterns from RP models
                            msg.Text = LLMClient.StripNarration(msg.Text);
                            _chatHistory[responseIdx] = msg;

                            if (string.IsNullOrWhiteSpace(msg.Text) || msg.Text.Trim().Contains("[SKIP]"))
                            {
                                _chatHistory.RemoveAt(responseIdx);
                                Main.LogDebug("[MachineSpirit] Dialogue: skipped (uninteresting)");
                            }
                        }
                        ChatWindow.SetThinking(false);
                    },
                    onError: error =>
                    {
                        if (responseIdx < _chatHistory.Count)
                        {
                            var msg = _chatHistory[responseIdx];
                            if (string.IsNullOrEmpty(msg.Text?.Trim()))
                                _chatHistory.RemoveAt(responseIdx);
                        }
                        ChatWindow.SetThinking(false);
                        Main.LogDebug($"[MachineSpirit] LLM error (silent): {error}");
                    }
                ));
            }
            else
            {
                CoroutineRunner.Start(LLMClient.SendChatRequest(
                    Config, messages,
                    onResponse: response =>
                    {
                        if (!string.IsNullOrEmpty(response?.Trim()) && !response.Trim().Contains("[SKIP]"))
                        {
                            _chatHistory.Add(new ChatMessage
                            {
                                IsUser = false,
                                Text = response,
                                Timestamp = Time.time,
                                Category = MessageCategory.Vox
                            });
                        }
                        ChatWindow.SetThinking(false);
                    },
                    onError: error =>
                    {
                        ChatWindow.SetThinking(false);
                        Main.LogDebug($"[MachineSpirit] LLM error (silent): {error}");
                    }
                ));
            }
        }

        // ════════════════════════════════════════════════════════════
        // ★ v3.70.0: Smart Dialogue Timing — react at scene start/end
        // ════════════════════════════════════════════════════════════

        // ★ v3.94.0: 대화씬 시작 반응 비활성화 — NPC 대화 반응 축소 (종료 시 1회만 반응)
        public static void OnDialogueStarted()
        {
            // ★ v3.94.0: Dialogue start reaction disabled — only react at scene end
            // 대화 버퍼는 계속 수집되므로 OnDialogueEnded에서 전체 맥락 파악 가능
            _lastActivityTime = Time.time;
            ResetIdleTimers();
            return;

            /*
            if (!IsActive) return;
            if (LLMClient.IsRequesting) return;
            if (Config?.IdleMode == IdleFrequency.Off) return;

            _lastDialogueCommentTime = Time.time;
            _lastActivityTime = Time.time;
            ResetIdleTimers();

            var messages = ContextBuilder.BuildForDialogueStart(_chatHistory, Config, _conversationSummary);
            ChatWindow.SetThinking(true);

            if (Config.Provider == ApiProvider.Ollama)
            {
                _chatHistory.Add(new ChatMessage { IsUser = false, Text = "", Timestamp = Time.time, Category = MessageCategory.Vox });
                int responseIdx = _chatHistory.Count - 1;
                CoroutineRunner.Start(LLMClient.SendOllamaStreaming(
                    Config, messages,
                    onToken: tokens => { var msg = _chatHistory[responseIdx]; msg.Text += tokens; _chatHistory[responseIdx] = msg; ChatWindow.SetThinking(false); },
                    onComplete: () =>
                    {
                        if (responseIdx < _chatHistory.Count)
                        {
                            var msg = _chatHistory[responseIdx];
                            // ★ v3.74.0: Strip narration patterns from RP models
                            msg.Text = LLMClient.StripNarration(msg.Text);
                            _chatHistory[responseIdx] = msg;

                            if (string.IsNullOrWhiteSpace(msg.Text) || msg.Text.Trim().Contains("[SKIP]"))
                                _chatHistory.RemoveAt(responseIdx);
                        }
                        ChatWindow.SetThinking(false);
                    },
                    onError: error =>
                    {
                        if (responseIdx < _chatHistory.Count)
                        {
                            var msg = _chatHistory[responseIdx];
                            if (string.IsNullOrEmpty(msg.Text?.Trim()))
                                _chatHistory.RemoveAt(responseIdx);
                        }
                        ChatWindow.SetThinking(false);
                        Main.LogDebug($"[MachineSpirit] LLM error (silent): {error}");
                    }
                ));
            }
            else
            {
                CoroutineRunner.Start(LLMClient.SendChatRequest(Config, messages,
                    onResponse: response =>
                    {
                        if (!string.IsNullOrEmpty(response?.Trim()) && !response.Contains("[SKIP]"))
                            _chatHistory.Add(new ChatMessage { IsUser = false, Text = response, Timestamp = Time.time, Category = MessageCategory.Vox });
                        ChatWindow.SetThinking(false);
                    },
                    onError: error =>
                    {
                        ChatWindow.SetThinking(false);
                        Main.LogDebug($"[MachineSpirit] LLM error (silent): {error}");
                    }
                ));
            }
            */
        }

        public static void OnDialogueEnded()
        {
            if (!IsActive) return;
            if (LLMClient.IsRequesting) return;
            if (Config?.IdleMode == IdleFrequency.Off) return;

            _lastDialogueCommentTime = Time.time;
            _lastActivityTime = Time.time;
            ResetIdleTimers();

            var messages = ContextBuilder.BuildForDialogueEnd(_chatHistory, Config, _conversationSummary);
            ChatWindow.SetThinking(true);

            if (Config.Provider == ApiProvider.Ollama)
            {
                _chatHistory.Add(new ChatMessage { IsUser = false, Text = "", Timestamp = Time.time, Category = MessageCategory.Vox });
                int responseIdx = _chatHistory.Count - 1;
                CoroutineRunner.Start(LLMClient.SendOllamaStreaming(
                    Config, messages,
                    onToken: tokens => { var msg = _chatHistory[responseIdx]; msg.Text += tokens; _chatHistory[responseIdx] = msg; ChatWindow.SetThinking(false); },
                    onComplete: () =>
                    {
                        if (responseIdx < _chatHistory.Count)
                        {
                            var msg = _chatHistory[responseIdx];
                            // ★ v3.74.0: Strip narration patterns from RP models
                            msg.Text = LLMClient.StripNarration(msg.Text);
                            _chatHistory[responseIdx] = msg;

                            if (string.IsNullOrWhiteSpace(msg.Text) || msg.Text.Trim().Contains("[SKIP]"))
                                _chatHistory.RemoveAt(responseIdx);
                        }
                        ChatWindow.SetThinking(false);
                    },
                    onError: error =>
                    {
                        if (responseIdx < _chatHistory.Count)
                        {
                            var msg = _chatHistory[responseIdx];
                            if (string.IsNullOrEmpty(msg.Text?.Trim()))
                                _chatHistory.RemoveAt(responseIdx);
                        }
                        ChatWindow.SetThinking(false);
                        Main.LogDebug($"[MachineSpirit] LLM error (silent): {error}");
                    }
                ));
            }
            else
            {
                CoroutineRunner.Start(LLMClient.SendChatRequest(Config, messages,
                    onResponse: response =>
                    {
                        if (!string.IsNullOrEmpty(response?.Trim()) && !response.Contains("[SKIP]"))
                            _chatHistory.Add(new ChatMessage { IsUser = false, Text = response, Timestamp = Time.time, Category = MessageCategory.Vox });
                        ChatWindow.SetThinking(false);
                    },
                    onError: error =>
                    {
                        ChatWindow.SetThinking(false);
                        Main.LogDebug($"[MachineSpirit] LLM error (silent): {error}");
                    }
                ));
            }
        }

        // ════════════════════════════════════════════════════════════
        // ★ v3.66.0: Area Transition — scan new locations
        // ════════════════════════════════════════════════════════════

        public static void OnAreaTransition(GameEvent evt)
        {
            if (!IsActive) return;
            if (_isTemplateChecking) return;
            if (LLMClient.IsRequesting) return;
            if (Time.time - _lastAreaTransitionTime < AREA_TRANSITION_COOLDOWN) return;

            // Skip during combat
            bool inCombat = false;
            try { inCombat = Kingmaker.Game.Instance?.Player?.IsInCombat ?? false; } catch { }
            if (inCombat) return;

            _lastAreaTransitionTime = Time.time;
            _lastActivityTime = Time.time;
            ResetIdleTimers();

            var messages = ContextBuilder.BuildForAreaTransition(evt, _chatHistory, Config, _conversationSummary);
            ChatWindow.SetThinking(true);

            if (Config.Provider == ApiProvider.Ollama)
            {
                _chatHistory.Add(new ChatMessage { IsUser = false, Text = "", Timestamp = Time.time, Category = MessageCategory.Scan });
                int responseIdx = _chatHistory.Count - 1;

                CoroutineRunner.Start(LLMClient.SendOllamaStreaming(
                    Config, messages,
                    onToken: tokens =>
                    {
                        var msg = _chatHistory[responseIdx];
                        msg.Text += tokens;
                        _chatHistory[responseIdx] = msg;
                        ChatWindow.SetThinking(false);
                    },
                    onComplete: () =>
                    {
                        if (responseIdx < _chatHistory.Count)
                        {
                            var msg = _chatHistory[responseIdx];
                            // ★ v3.74.0: Strip narration patterns from RP models
                            msg.Text = LLMClient.StripNarration(msg.Text);
                            _chatHistory[responseIdx] = msg;

                            if (string.IsNullOrWhiteSpace(msg.Text))
                                _chatHistory.RemoveAt(responseIdx);
                        }
                        ChatWindow.SetThinking(false);
                    },
                    onError: error =>
                    {
                        if (responseIdx < _chatHistory.Count)
                        {
                            var msg = _chatHistory[responseIdx];
                            if (string.IsNullOrEmpty(msg.Text?.Trim()))
                                _chatHistory.RemoveAt(responseIdx);
                        }
                        ChatWindow.SetThinking(false);
                        Main.LogDebug($"[MachineSpirit] LLM error (silent): {error}");
                    }
                ));
            }
            else
            {
                CoroutineRunner.Start(LLMClient.SendChatRequest(
                    Config, messages,
                    onResponse: response =>
                    {
                        if (!string.IsNullOrEmpty(response?.Trim()))
                        {
                            _chatHistory.Add(new ChatMessage
                            {
                                IsUser = false,
                                Text = response,
                                Timestamp = Time.time,
                                Category = MessageCategory.Scan
                            });
                        }
                        ChatWindow.SetThinking(false);
                    },
                    onError: error =>
                    {
                        ChatWindow.SetThinking(false);
                        Main.LogDebug($"[MachineSpirit] LLM error (silent): {error}");
                    }
                ));
            }
        }

        // ════════════════════════════════════════════════════════════
        // ★ v3.68.0: Polling for entity-bound events
        // ════════════════════════════════════════════════════════════

        private static void PollEntityEvents()
        {
            if (Time.time - _lastPollTime < 2f) return;
            _lastPollTime = Time.time;

            try
            {
                var player = Kingmaker.Game.Instance?.Player;
                if (player == null) return;

                // Level-up detection: track total party levels
                int levelTotal = 0;
                string leveledChar = null;
                foreach (var unit in player.PartyAndPets)
                {
                    if (unit == null || unit.IsPet) continue;
                    int lvl = 0;
                    try { lvl = unit.Progression?.CharacterLevel ?? 0; } catch { }
                    levelTotal += lvl;
                }
                if (_lastKnownLevelTotal > 0 && levelTotal > _lastKnownLevelTotal)
                {
                    // Find who leveled up (check each unit's level vs expected)
                    foreach (var unit in player.PartyAndPets)
                    {
                        if (unit == null || unit.IsPet) continue;
                        try
                        {
                            if (Kingmaker.UnitLogic.Levelup.Obsolete.LevelUpController.CanLevelUp(unit))
                                continue;
                            leveledChar = unit.CharacterName ?? "Unknown";
                        }
                        catch { }
                    }
                    if (leveledChar == null) leveledChar = "A crew member";

                    GameEventCollector.AddEvent(GameEventType.LevelUp, leveledChar, $"{leveledChar} has advanced in rank");
                    EventCoalescer.Enqueue(GameEventCollector.RecentEvents[GameEventCollector.RecentEvents.Count - 1]);
                }
                _lastKnownLevelTotal = levelTotal;

                // Warp travel detection
                bool inWarp = false;
                try { inWarp = player.WarpTravelState?.IsInWarpTravel ?? false; } catch { }
                if (inWarp && !_wasInWarp)
                {
                    GameEventCollector.AddEvent(GameEventType.WarpTravel, null, "Warp travel initiated — Gellar field engaged");
                    EventCoalescer.Enqueue(GameEventCollector.RecentEvents[GameEventCollector.RecentEvents.Count - 1]);
                }
                else if (!inWarp && _wasInWarp)
                {
                    GameEventCollector.AddEvent(GameEventType.WarpTravel, null, "Warp travel concluded — Translation to realspace complete");
                    EventCoalescer.Enqueue(GameEventCollector.RecentEvents[GameEventCollector.RecentEvents.Count - 1]);
                }
                _wasInWarp = inWarp;
            }
            catch { }
        }

        // ════════════════════════════════════════════════════════════
        // ★ v3.68.0: Merged event handler — batched response
        // ════════════════════════════════════════════════════════════

        public static void OnMergedEvents(List<GameEvent> events)
        {
            if (!IsActive) return;
            if (_isTemplateChecking) return;
            if (LLMClient.IsRequesting) return;

            _lastActivityTime = Time.time;
            ResetIdleTimers();

            // Determine best category from events
            MessageCategory category = MessageCategory.Default;
            foreach (var evt in events)
            {
                if (evt.Type == GameEventType.SoulMarkShift) { category = MessageCategory.Faith; break; }
                if (evt.Type == GameEventType.QuestUpdate || evt.Type == GameEventType.LevelUp) category = MessageCategory.Quest;
                if (evt.Type == GameEventType.WarpTravel && category == MessageCategory.Default) category = MessageCategory.Scan;
                if (evt.Type == GameEventType.PlayerChoice && category == MessageCategory.Default) category = MessageCategory.Vox;
            }

            var messages = ContextBuilder.BuildForMergedEvents(events, _chatHistory, Config, _conversationSummary);
            ChatWindow.SetThinking(true);

            if (Config.Provider == ApiProvider.Ollama)
            {
                _chatHistory.Add(new ChatMessage { IsUser = false, Text = "", Timestamp = Time.time, Category = category });
                int responseIdx = _chatHistory.Count - 1;

                CoroutineRunner.Start(LLMClient.SendOllamaStreaming(
                    Config, messages,
                    onToken: tokens =>
                    {
                        var msg = _chatHistory[responseIdx];
                        msg.Text += tokens;
                        _chatHistory[responseIdx] = msg;
                        ChatWindow.SetThinking(false);
                    },
                    onComplete: () =>
                    {
                        if (responseIdx < _chatHistory.Count)
                        {
                            var msg = _chatHistory[responseIdx];
                            // ★ v3.74.0: Strip narration patterns from RP models
                            msg.Text = LLMClient.StripNarration(msg.Text);
                            _chatHistory[responseIdx] = msg;

                            if (string.IsNullOrWhiteSpace(msg.Text))
                                _chatHistory.RemoveAt(responseIdx);
                        }
                        ChatWindow.SetThinking(false);
                        MaybeSummarize();
                    },
                    onError: error =>
                    {
                        if (responseIdx < _chatHistory.Count)
                        {
                            var msg = _chatHistory[responseIdx];
                            if (string.IsNullOrEmpty(msg.Text?.Trim()))
                                _chatHistory.RemoveAt(responseIdx);
                        }
                        ChatWindow.SetThinking(false);
                        Main.LogDebug($"[MachineSpirit] LLM error (silent): {error}");
                    }
                ));
            }
            else
            {
                CoroutineRunner.Start(LLMClient.SendChatRequest(
                    Config, messages,
                    onResponse: response =>
                    {
                        if (!string.IsNullOrEmpty(response?.Trim()))
                        {
                            _chatHistory.Add(new ChatMessage
                            {
                                IsUser = false,
                                Text = response,
                                Timestamp = Time.time,
                                Category = category
                            });
                        }
                        ChatWindow.SetThinking(false);
                        MaybeSummarize();
                    },
                    onError: error =>
                    {
                        ChatWindow.SetThinking(false);
                        Main.LogDebug($"[MachineSpirit] LLM error (silent): {error}");
                    }
                ));
            }
        }

        // ════════════════════════════════════════════════════════════
        // Idle Commentary (v3.60.0)
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// ★ v3.60.0: Called every frame. Checks idle timers for autonomous commentary.
        /// ★ v3.98.0: LLM 전투 AI 모델 warmup 체크 (IsActive와 무관하게 동작)
        /// </summary>
        public static void Update()
        {
            // ★ v3.98.0: LLM 전투 AI 모델 사전 로딩 — IsActive 체크 앞에 위치
            // MachineSpirit OFF 상태여도 LLM 전투 AI만 켜져 있으면 warmup 진행
            CompanionAI_v3.Planning.LLM.LLMWarmup.TryTickWarmup();

            if (!IsActive) return;

            // ★ v3.66.0: Session greeting — wait 3 seconds after init for provider readiness
            if (!_hasGreeted && !_isTemplateChecking && Time.time - _lastActivityTime > 3f)
            {
                _hasGreeted = true;
                TriggerGreeting();
                return;
            }

            // ★ v3.68.0: Process coalesced events
            var mergedEvents = EventCoalescer.TryFlush();
            if (mergedEvents != null && mergedEvents.Count > 0)
            {
                OnMergedEvents(mergedEvents);
            }

            // ★ v3.68.0: Poll for entity-bound events (level-up, warp travel)
            PollEntityEvents();

            if (LLMClient.IsRequesting) return;
            if (_idleVisionPending) return;

            var idleMode = Config?.IdleMode ?? IdleFrequency.Off;
            if (idleMode == IdleFrequency.Off) return;

            // Don't idle-chat during combat (existing spontaneous system handles that)
            bool inCombat = false;
            try { inCombat = Kingmaker.Game.Instance?.Player?.IsInCombat ?? false; } catch { }
            if (inCombat) return;

            float now = Time.time;

            // Vision check (longer interval, Ollama-only)
            if (Config.EnableVision && Config.Provider == ApiProvider.Ollama && now >= _nextIdleVisionTime)
            {
                TriggerIdleVision();
                return;
            }

            // Text idle check
            if (now >= _nextIdleTextTime)
            {
                TriggerIdleText();
            }
        }

        private static void TriggerGreeting()
        {
            if (LLMClient.IsRequesting) return;

            ChatWindow.SetVisible(true);
            ChatWindow.SetThinking(true);
            _lastActivityTime = Time.time;
            ResetIdleTimers();

            var messages = ContextBuilder.BuildForGreeting(_chatHistory, Config, _conversationSummary);

            if (Config.Provider == ApiProvider.Ollama)
            {
                _chatHistory.Add(new ChatMessage { IsUser = false, Text = "", Timestamp = Time.time, Category = MessageCategory.Greeting });
                int responseIdx = _chatHistory.Count - 1;

                CoroutineRunner.Start(LLMClient.SendOllamaStreaming(
                    Config, messages,
                    onToken: tokens =>
                    {
                        var msg = _chatHistory[responseIdx];
                        msg.Text += tokens;
                        _chatHistory[responseIdx] = msg;
                        ChatWindow.SetThinking(false);
                    },
                    onComplete: () =>
                    {
                        if (responseIdx < _chatHistory.Count)
                        {
                            var msg = _chatHistory[responseIdx];
                            // ★ v3.74.0: Strip narration patterns from RP models
                            msg.Text = LLMClient.StripNarration(msg.Text);
                            _chatHistory[responseIdx] = msg;

                            if (string.IsNullOrWhiteSpace(msg.Text))
                                _chatHistory.RemoveAt(responseIdx);
                        }
                        ChatWindow.SetThinking(false);
                    },
                    onError: error =>
                    {
                        if (responseIdx < _chatHistory.Count)
                        {
                            var msg = _chatHistory[responseIdx];
                            if (string.IsNullOrEmpty(msg.Text?.Trim()))
                                _chatHistory.RemoveAt(responseIdx);
                        }
                        ChatWindow.SetThinking(false);
                        Main.LogDebug($"[MachineSpirit] LLM error (silent): {error}");
                    }
                ));
            }
            else
            {
                CoroutineRunner.Start(LLMClient.SendChatRequest(
                    Config, messages,
                    onResponse: response =>
                    {
                        if (!string.IsNullOrEmpty(response?.Trim()))
                        {
                            _chatHistory.Add(new ChatMessage
                            {
                                IsUser = false,
                                Text = response,
                                Timestamp = Time.time,
                                Category = MessageCategory.Greeting
                            });
                        }
                        ChatWindow.SetThinking(false);
                    },
                    onError: error =>
                    {
                        ChatWindow.SetThinking(false);
                        Main.LogDebug($"[MachineSpirit] LLM error (silent): {error}");
                    }
                ));
            }
        }

        private static void TriggerIdleText()
        {
            _lastActivityTime = Time.time;
            ResetIdleTimers();

            // ★ v3.66.0: Context-aware idle prompt — check if recent sensor log has dialogue
            bool hasRecentDialogue = false;
            var events = GameEventCollector.RecentEvents;
            for (int i = events.Count - 1; i >= Math.Max(0, events.Count - 5); i--)
            {
                if (events[i].Type == GameEventType.Dialogue || events[i].Type == GameEventType.Bark)
                {
                    hasRecentDialogue = true;
                    break;
                }
            }

            var lang = Main.Settings?.UILanguage ?? Language.English;
            string instruction;
            if (hasRecentDialogue)
            {
                instruction = lang switch
                {
                    Language.Korean => "센서 로그에 최근 대화가 기록되었다. 이 대화나 현재 상황에 대해 네 성격에 맞게 짧게 코멘트하라. 특별히 할 말이 없다면 [SKIP]으로만 응답하라.",
                    Language.Russian => "В сенсорном журнале есть недавний разговор. Кратко прокомментируй его или текущую ситуацию в образе. Если нечего сказать — ответь только [SKIP].",
                    Language.Japanese => "センサーログに最近の会話が記録された。この会話や現在の状況についてキャラクターに合わせて短くコメントせよ。特に何もなければ[SKIP]とだけ答えよ。",
                    Language.Chinese => "传感器日志记录了近期的对话。简短评论这段对话或当前情况。如果没什么可说的，只回复[SKIP]。",
                    _ => "Sensor log recorded recent dialogue. Comment briefly on the conversation or current situation, in character. If nothing to add, respond with [SKIP] only."
                };
            }
            else
            {
                instruction = lang switch
                {
                    Language.Korean => "잠시 조용했다. 현재 상황이나 지역에 대해 짧게 한마디 하라. 흥미로운 게 없다면 [SKIP]으로만 응답하라.",
                    Language.Russian => "Было тихо. Кратко прокомментируй текущую ситуацию или местоположение. Если нечего сказать — ответь только [SKIP].",
                    Language.Japanese => "しばらく静かだった。現在の状況や場所について短くコメントせよ。特に何もなければ[SKIP]とだけ答えよ。",
                    Language.Chinese => "沉寂了一段时间。对当前情况或所在区域简短评论一句。如果没什么有趣的，只回复[SKIP]。",
                    _ => "It's been quiet. Comment briefly on the current situation or location. If nothing interesting, respond with [SKIP] only."
                };
            }

            var messages = ContextBuilder.Build(_chatHistory, Config, instruction, _conversationSummary);
            SendIdleRequest(messages);
        }

        private static void TriggerIdleVision()
        {
            _idleVisionPending = true;
            _lastActivityTime = Time.time;
            ResetIdleTimers();

            string base64Image = VisionCapture.CaptureBase64();
            if (base64Image == null)
            {
                _idleVisionPending = false;
                return;
            }

            var lang = Main.Settings?.UILanguage ?? Language.English;
            string instruction = lang switch
            {
                Language.Korean => "함선 센서가 현재 화면을 캡처했다. 보이는 내용에 대해 짧게 코멘트하라. 평범한 장면이면 [SKIP]으로만 응답하라.",
                Language.Russian => "Сенсоры корабля зафиксировали текущий вид. Кратко прокомментируй увиденное. Если ничего примечательного — ответь [SKIP].",
                Language.Japanese => "艦のセンサーが現在の画面を捉えた。見えるものについて短くコメントせよ。特筆すべきものがなければ[SKIP]とだけ答えよ。",
                Language.Chinese => "舰船传感器捕获了当前画面。简短评论你所看到的内容。如果场景平淡无奇，只回复[SKIP]。",
                _ => "Ship sensors captured the current view. Comment briefly on what you see. If the scene is unremarkable, respond with [SKIP] only."
            };

            var messages = ContextBuilder.Build(_chatHistory, Config, instruction, _conversationSummary);

            // Attach image to the last user message
            if (messages.Count > 0)
            {
                var lastMsg = messages[messages.Count - 1];
                if (lastMsg.Role == "user")
                {
                    lastMsg.Images = new System.Collections.Generic.List<string> { base64Image };
                }
            }

            SendIdleRequest(messages, isVision: true);
        }

        private static void SendIdleRequest(System.Collections.Generic.List<LLMClient.ChatMessage> messages, bool isVision = false)
        {
            ChatWindow.SetThinking(true);

            if (Config.Provider == ApiProvider.Ollama)
            {
                _chatHistory.Add(new ChatMessage { IsUser = false, Text = "", Timestamp = Time.time, Category = MessageCategory.Scan });
                int responseIdx = _chatHistory.Count - 1;

                CoroutineRunner.Start(LLMClient.SendOllamaStreaming(
                    Config, messages,
                    onToken: tokens =>
                    {
                        var msg = _chatHistory[responseIdx];
                        msg.Text += tokens;
                        _chatHistory[responseIdx] = msg;
                        ChatWindow.SetThinking(false);
                    },
                    onComplete: () =>
                    {
                        ChatWindow.SetThinking(false);
                        _idleVisionPending = false;

                        if (responseIdx < _chatHistory.Count)
                        {
                            var msg = _chatHistory[responseIdx];
                            // ★ v3.74.0: Strip narration patterns from RP models
                            msg.Text = LLMClient.StripNarration(msg.Text);
                            _chatHistory[responseIdx] = msg;

                            if (string.IsNullOrWhiteSpace(msg.Text) || msg.Text.Trim().Contains("[SKIP]"))
                            {
                                _chatHistory.RemoveAt(responseIdx);
                                Main.LogDebug("[MachineSpirit] Idle: skipped (nothing interesting)");
                            }
                            else
                            {
                                _lastActivityTime = Time.time;
                                ResetIdleTimers();
                                if (isVision && !string.IsNullOrEmpty(msg.Text))
                                {
                                    string summary = msg.Text.Length > 80 ? msg.Text.Substring(0, 80) + "..." : msg.Text;
                                    GameEventCollector.AddEvent(GameEventType.VisionObservation, null, summary);
                                }
                            }
                        }
                    },
                    onError: error =>
                    {
                        if (responseIdx < _chatHistory.Count)
                        {
                            var msg = _chatHistory[responseIdx];
                            if (string.IsNullOrEmpty(msg.Text?.Trim()))
                                _chatHistory.RemoveAt(responseIdx);
                        }
                        ChatWindow.SetThinking(false);
                        _idleVisionPending = false;
                        Main.LogDebug($"[MachineSpirit] LLM error (silent): {error}");
                    }
                ));
            }
            else
            {
                CoroutineRunner.Start(LLMClient.SendChatRequest(
                    Config, messages,
                    onResponse: response =>
                    {
                        if (!string.IsNullOrEmpty(response?.Trim()) && !response.Trim().Contains("[SKIP]"))
                        {
                            _chatHistory.Add(new ChatMessage
                            {
                                IsUser = false,
                                Text = response,
                                Timestamp = Time.time,
                                Category = MessageCategory.Scan
                            });
                            _lastActivityTime = Time.time;
                            ResetIdleTimers();
                        }
                        ChatWindow.SetThinking(false);
                    },
                    onError: error =>
                    {
                        ChatWindow.SetThinking(false);
                        Main.LogDebug($"[MachineSpirit] LLM error (silent): {error}");
                    }
                ));
            }
        }

        // ════════════════════════════════════════════════════════════
        // Background Conversation Summary
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// Trigger background summarization if chat history has grown beyond the context window.
        /// Only runs for Ollama (free/unlimited) to avoid burning API quotas.
        /// </summary>
        private static void MaybeSummarize()
        {
            if (_isSummarizing) return;
            if (_chatHistory.Count <= SUMMARY_THRESHOLD) return;

            // Check if there are unsummarized messages outside the 20-message context window
            int unsummarizedCount = _chatHistory.Count - 20 - _summarizedUpToIndex;
            if (unsummarizedCount < 10) return; // Not enough new messages to warrant re-summarization

            _isSummarizing = true;
            CoroutineRunner.Start(SummarizeCoroutine());
        }

        private static IEnumerator SummarizeCoroutine()
        {
            // Collect messages that won't fit in the 20-message context window
            // ★ v3.64.0: Match summarization window to history window
            int historyWindow = 20;
            if (Config.Provider == ApiProvider.Ollama)
            {
                string model = Config.Model?.ToLowerInvariant() ?? "";
                if (model.Contains("1b") || model.Contains("3b") || model.Contains("4b"))
                    historyWindow = 12;
                else if (!model.Contains("27b") && !model.Contains("70b"))
                    historyWindow = 16;
            }
            int endIdx = _chatHistory.Count - historyWindow;
            if (endIdx <= 0)
            {
                _isSummarizing = false;
                yield break;
            }

            var toSummarize = new List<ChatMessage>();
            for (int i = 0; i < endIdx && i < _chatHistory.Count; i++)
            {
                var msg = _chatHistory[i];
                if (!msg.Text.StartsWith("[ERROR]"))
                    toSummarize.Add(msg);
            }

            if (toSummarize.Count < 4)
            {
                _isSummarizing = false;
                yield break;
            }

            Main.LogDebug($"[MachineSpirit] Summarizing {toSummarize.Count} old messages...");

            var summaryMessages = ContextBuilder.BuildSummaryPrompt(toSummarize);

            yield return LLMClient.SendBackgroundRequest(
                Config,
                summaryMessages,
                onResponse: summary =>
                {
                    _conversationSummary = summary;
                    _summarizedUpToIndex = endIdx;
                    Main.LogDebug($"[MachineSpirit] Summary updated: {summary.Length} chars");
                }
            );

            _isSummarizing = false;
        }
    }

    /// <summary>
    /// MonoBehaviour wrapper to run coroutines from static context.
    /// Also handles OnGUI for ChatWindow (Main.OnGUI only fires when UMM settings are open).
    /// </summary>
    public class CoroutineRunner : MonoBehaviour
    {
        private static CoroutineRunner _instance;

        public static void Start(IEnumerator coroutine)
        {
            EnsureInstance();
            _instance.StartCoroutine(coroutine);
        }

        public static void EnsureInstance()
        {
            if (_instance != null) return;
            var go = new GameObject("CompanionAI_CoroutineRunner");
            go.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<CoroutineRunner>();
        }

        private void Update()
        {
            MachineSpirit.Update();
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
