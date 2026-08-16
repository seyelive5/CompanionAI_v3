// MachineSpirit/MachineSpirit.cs
// ★ v3.58.0: Ollama streaming routing + background conversation summary
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using CompanionAI_v3.Settings;
using Newtonsoft.Json;
using UnityEngine;
using CompanionAI_v3.Logging;

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

        // ★ 채팅 영속화 토글 — false: 매 세션 새 대화(저장/로드 안 함). 세션 종속 플레이버라 fresh 가 기본.
        //   true 로 바꾸면 chat_history.json 에 저장/로드 (단 모델 JSON 래핑 오염이 재유입될 위험 있음).
        private static readonly bool PersistChatHistory = false;

        private static readonly List<ChatMessage> _chatHistory = new List<ChatMessage>();
        private static MachineSpiritConfig Config => Main.Settings?.MachineSpirit;
        private static float _lastSpontaneousTime;
        private static float _lastDialogueCommentTime; // ★ v3.66.0
        private static float _lastAreaTransitionTime;
        private static bool _hasGreeted;
        private static bool _isTemplateChecking; // ★ v3.72.0: Block LLM calls during template check

        // ★ v3.112.4 (C2): Shutdown 이중 호출 방어.
        //   UMM OnToggle(false) + Application.quitting 양쪽 모두 발화 가능.
        //   Initialize 에서 reset, Shutdown 에서 set.
        private static bool _hasShutdown;

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
            _hasShutdown = false;
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
                    Log.MachineSpirit.Debug($"[MachineSpirit] Switching to template-fixed model: {OllamaSetup.TemplateFixedModel}");
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
            // ★ v3.112.4 (C2): 이중 호출 방지 (UMM 토글 + Application.quitting 양쪽 fire).
            if (_hasShutdown)
            {
                Log.MachineSpirit.Debug("[MachineSpirit] Shutdown already executed — skipping");
                return;
            }
            _hasShutdown = true;

            SaveChatHistory();
            GameEventCollector.Unsubscribe();
            GameEventCollector.Clear();
            _chatHistory.Clear();
            _conversationSummary = null;
            _isSummarizing = false;
            _summarizedUpToIndex = 0;
            LLMClient.Reset();

            // ★ v3.112.3: Ollama 모델 VRAM 해제 (keep_alive=-1 로 영구 유지 중인 모델 언로드).
            try
            {
                string apiUrl = Main.Settings?.MachineSpirit?.ApiUrl;
                if (string.IsNullOrEmpty(apiUrl)) apiUrl = "http://localhost:11434";
                Planning.LLM.LLMWarmup.UnloadAllModels(apiUrl);
            }
            catch (System.Exception ex)
            {
                Log.MachineSpirit.Error(ex, $"[MachineSpirit] Ollama unload request failed");
            }
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
            if (!PersistChatHistory || _chatHistory.Count == 0) return;
            try
            {
                var data = new SavedChat
                {
                    Messages = new List<ChatMessage>(_chatHistory),
                    Summary = _conversationSummary
                };
                string json = JsonConvert.SerializeObject(data, Formatting.Indented);
                File.WriteAllText(GetChatHistoryPath(), json);
                Log.MachineSpirit.Debug($"[MachineSpirit] Chat saved: {_chatHistory.Count} messages");
            }
            catch (Exception ex)
            {
                Log.MachineSpirit.Error(ex, $"[MachineSpirit] Save failed");
            }
        }

        public static void LoadChatHistory()
        {
            if (!PersistChatHistory) return;
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

                    Log.MachineSpirit.Debug($"[MachineSpirit] Chat loaded: {_chatHistory.Count} messages");
                }
            }
            catch (Exception ex)
            {
                Log.MachineSpirit.Error(ex, $"[MachineSpirit] Load failed");
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
            Log.MachineSpirit.Debug("[MachineSpirit] Chat history cleared (personality change)");
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

            Log.MachineSpirit.Debug($"[MachineSpirit] Model changed: {oldModel} → {newModel}");

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

            Log.MachineSpirit.Debug($"[MachineSpirit] Personality changed to: {Config.Personality}");

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

        // ════════════════════════════════════════════════════════════
        // ★ Id 기반 스트리밍 추적 — 고정 인덱스+ts 의 시프트/충돌 취약성 해결.
        //   placeholder 를 고유 Id 로 추적 → 히스토리 변형(trim/clear/add)·ts 충돌 무관하게 정확한
        //   메시지에 append, 사라졌으면 안전 중단. 빈/[SKIP] placeholder 는 정리(고아 방지).
        // ════════════════════════════════════════════════════════════

        private static int _nextMsgId = 1;

        private static int AddAssistantPlaceholder(MessageCategory category)
        {
            int id = _nextMsgId++;
            _chatHistory.Add(new ChatMessage { Id = id, IsUser = false, Text = "", Timestamp = UnityEngine.Time.time, Category = category });
            return id;
        }

        private static int FindMsgIndexById(int id)
        {
            if (id <= 0) return -1;
            for (int i = _chatHistory.Count - 1; i >= 0; i--)  // 최근부터 — placeholder 는 보통 끝쪽
                if (_chatHistory[i].Id == id) return i;
            return -1;
        }

        private static void AppendToMsgById(int id, string tokens)
        {
            int idx = FindMsgIndexById(id);
            if (idx < 0) return;
            var msg = _chatHistory[idx];   // struct → read-modify-writeback
            msg.Text += tokens;
            _chatHistory[idx] = msg;
        }

        /// <summary>완료 처리 — StripNarration 후 빈/[SKIP]이면 제거(고아 방지). 살아남은 최종 텍스트 반환(제거 시 null).</summary>
        private static string FinalizeStreamedMsg(int id)
        {
            int idx = FindMsgIndexById(id);
            if (idx < 0) return null;
            var msg = _chatHistory[idx];
            msg.Text = LLMClient.StripNarration(msg.Text);
            _chatHistory[idx] = msg;
            if (string.IsNullOrWhiteSpace(msg.Text) || msg.Text.Trim().Contains("[SKIP]"))
            {
                _chatHistory.RemoveAt(idx);   // 빈/[SKIP] 고아 제거
                return null;
            }
            return msg.Text;
        }

        #region Connection Diagnostic

        /// <summary>진단 결과 한 줄 (설정 UI 상태줄). null = 아직 실행 안 함.</summary>
        public static string DiagnosticStatus { get; private set; }

        /// <summary>마지막 진단이 통과했는지 (UI 색상 결정).</summary>
        public static bool DiagnosticOk { get; private set; }

        /// <summary>진단 진행 중 (UI 색상 결정).</summary>
        public static bool DiagnosticRunning { get; private set; }

        /// <summary>
        /// "연결 테스트" 실제 진단 — 기존엔 인사 메시지 한 번 보내는 게 전부라
        /// 서버가 꺼져 있으면 아무 표시 없이 침묵했다(커뮤니티 제보의 직접 원인).
        /// 이제 단계별로 확인해 실패 지점을 특정한다:
        ///   1) 설정 유효성 → 2) (Ollama) 서버 도달 + 모델 설치 여부 → 3) 실제 응답 1회
        /// </summary>
        public static void RunConnectionDiagnostic()
        {
            if (DiagnosticRunning) return;
            CoroutineRunner.EnsureInstance();
            CoroutineRunner.Start(ConnectionDiagnosticRoutine());
        }

        private static IEnumerator ConnectionDiagnosticRoutine()
        {
            DiagnosticRunning = true;
            DiagnosticOk = false;
            ClearLastError();

            try
            {
                if (Config == null || !Config.Enabled)
                {
                    DiagnosticStatus = "Machine Spirit is disabled — enable it first.";
                    yield break;
                }
                if (string.IsNullOrWhiteSpace(Config.ApiUrl))
                {
                    DiagnosticStatus = "API URL is empty — set it first.";
                    yield break;
                }
                if (string.IsNullOrWhiteSpace(Config.Model))
                {
                    DiagnosticStatus = "No model selected — pick a model first.";
                    yield break;
                }

                // Ollama 는 로컬 서버라 "서버 미실행 / 모델 미설치"가 압도적으로 흔한 실패 —
                // 실제 대화를 시도하기 전에 이 둘을 분리해서 알려준다.
                if (Config.Provider == ApiProvider.Ollama)
                {
                    DiagnosticStatus = "Checking Ollama server...";
                    yield return OllamaSetup.FetchInstalledModels();

                    var installed = OllamaSetup.InstalledModels;
                    if (installed == null || installed.Count == 0)
                    {
                        DiagnosticStatus = "Ollama server not responding (or no models installed). "
                            + "Start the Ollama server, then test again.";
                        yield break;
                    }

                    bool hasModel = false;
                    var names = new List<string>();
                    for (int i = 0; i < installed.Count; i++)
                    {
                        string m = installed[i].FullName;
                        if (string.IsNullOrEmpty(m)) m = installed[i].Name;
                        if (string.IsNullOrEmpty(m)) continue;
                        names.Add(m);
                        // 태그 생략 표기(예: "gemma3:4b" vs "gemma3:4b-it-qat") 허용
                        if (m.Equals(Config.Model, StringComparison.OrdinalIgnoreCase)
                            || m.StartsWith(Config.Model, StringComparison.OrdinalIgnoreCase)
                            || Config.Model.StartsWith(m, StringComparison.OrdinalIgnoreCase))
                        {
                            hasModel = true;
                        }
                    }

                    if (!hasModel)
                    {
                        DiagnosticStatus = $"Server OK, but model '{Config.Model}' is not installed. "
                            + $"Run: ollama pull {Config.Model}  (installed: {string.Join(", ", names.ToArray())})";
                        yield break;
                    }

                    DiagnosticStatus = "Server OK, model found — sending a test message...";
                }
                else
                {
                    DiagnosticStatus = "Sending a test message...";
                }

                // 실제 왕복 1회 — 성공/실패는 ReportLLMFailure/응답 경로가 채팅에도 남긴다.
                _diagnosticPending = true;
                OnUserMessage("Hello, Machine Spirit. Respond with a brief greeting.");

                float deadline = Time.time + 60f;
                while (_diagnosticPending && Time.time < deadline)
                    yield return null;

                if (_diagnosticPending)
                {
                    _diagnosticPending = false;
                    DiagnosticStatus = "No response within 60s — the model may be too large for this machine.";
                }
                else if (!string.IsNullOrEmpty(LastError))
                {
                    DiagnosticStatus = LastError;
                }
                else
                {
                    DiagnosticOk = true;
                    DiagnosticStatus = $"Connection OK — {Config.Model} responded.";
                }
            }
            finally
            {
                DiagnosticRunning = false;
            }
        }

        // 진단이 실제 응답/실패를 기다리는 중인지 — 응답·실패 경로에서 내려간다.
        private static bool _diagnosticPending;

        #endregion

        #region LLM Failure Reporting

        /// <summary>마지막 LLM 실패 사유(사용자 표시용) — 설정 UI 상태줄이 읽는다. 성공 시 null.</summary>
        public static string LastError { get; private set; }

        /// <summary>마지막 LLM 실패 시각(Time.time). 0 = 실패 이력 없음.</summary>
        public static float LastErrorTime { get; private set; }

        /// <summary>마지막 요청이 성공했음을 기록 — 상태줄이 "정상"으로 돌아간다.</summary>
        public static void ClearLastError()
        {
            LastError = null;
            LastErrorTime = 0f;
        }

        /// <summary>
        /// LLM 요청 실패를 사용자에게 보이게 처리 — 기존엔 Debug 로그로만 남아(=침묵) 사용자가
        /// "설정은 다 했는데 반응이 없다"로 겪던 문제(커뮤니티 제보)를 해소.
        /// 채팅에 `[ERROR]` 접두 메시지로 표기(이 접두사는 ContextBuilder·요약에서 제외되므로 대화 오염 없음)
        /// + LastError 에 보관해 설정 패널 상태줄에서도 확인 가능.
        /// </summary>
        private static void ReportLLMFailure(string rawError)
        {
            LastError = DescribeLLMError(rawError);
            LastErrorTime = Time.time;
            _diagnosticPending = false;  // 진단 대기 해제 (실패로 확정)

            // "이미 요청 진행 중"은 정상적인 중복 방지라 사용자에게 알릴 실패가 아님 (로그만)
            bool userVisible = !string.IsNullOrEmpty(rawError)
                && rawError.IndexOf("already in progress", StringComparison.OrdinalIgnoreCase) < 0;

            if (userVisible)
            {
                _chatHistory.Add(new ChatMessage
                {
                    IsUser = false,
                    Text = $"[ERROR] {LastError}",
                    Timestamp = Time.time
                });
                TrimHistory();
            }

            Log.MachineSpirit.Warn($"[MachineSpirit] LLM request failed: {rawError} (provider={Config?.Provider}, model={Config?.Model})");
        }

        /// <summary>
        /// 원시 오류 문자열(HTTP 코드/예외 메시지)을 사용자가 조치할 수 있는 한 문장으로 변환.
        /// 원문은 로그에 남으므로 여기서는 "무엇을 해야 하는가"에 집중한다.
        /// </summary>
        private static string DescribeLLMError(string rawError)
        {
            string e = rawError ?? "unknown";
            bool isOllama = Config?.Provider == ApiProvider.Ollama;

            // 연결 자체 실패 — 서버 미실행이 압도적으로 흔한 원인
            if (e.IndexOf("Cannot connect", StringComparison.OrdinalIgnoreCase) >= 0
                || e.IndexOf("Connection refused", StringComparison.OrdinalIgnoreCase) >= 0
                || e.IndexOf("Failed to connect", StringComparison.OrdinalIgnoreCase) >= 0
                || e.StartsWith("HTTP 0", StringComparison.OrdinalIgnoreCase))
            {
                return isOllama
                    ? $"Cannot reach Ollama at {Config?.ApiUrl}. Is the Ollama server running? (start it, then press Test Connection)"
                    : $"Cannot reach the API server at {Config?.ApiUrl}. Check the URL and your internet connection.";
            }

            if (e.StartsWith("HTTP 404", StringComparison.OrdinalIgnoreCase))
            {
                return isOllama
                    ? $"Model '{Config?.Model}' not found on the Ollama server. Pull it first (ollama pull {Config?.Model}) or pick an installed model."
                    : "Endpoint or model not found (HTTP 404). Check the API URL and model name.";
            }

            if (e.StartsWith("HTTP 401", StringComparison.OrdinalIgnoreCase)
                || e.StartsWith("HTTP 403", StringComparison.OrdinalIgnoreCase))
                return "API key rejected (HTTP 401/403). Check the API key for this provider.";

            if (e.StartsWith("HTTP 429", StringComparison.OrdinalIgnoreCase))
                return "Rate limited by the provider (HTTP 429). Wait a moment and try again.";

            if (e.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0
                || e.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0)
                return isOllama
                    ? "The model did not respond in time. A smaller model may be needed for this machine."
                    : "The request timed out. Check your connection or try again.";

            if (e.IndexOf("Empty response", StringComparison.OrdinalIgnoreCase) >= 0)
                return $"The model '{Config?.Model}' returned an empty response. Try another model.";

            return $"Request failed: {e}";
        }

        #endregion

        private static void RemoveMsgByIdIfEmpty(int id)
        {
            int idx = FindMsgIndexById(id);
            if (idx < 0) return;
            if (string.IsNullOrEmpty(_chatHistory[idx].Text?.Trim()))
                _chatHistory.RemoveAt(idx);
        }

        /// <summary>
        /// ★ Ollama 스트리밍 응답 공통 헬퍼 — placeholder(Id) 추가 + onToken/onComplete/onError 표준화.
        /// 9개 응답 경로의 중복(~40줄×9) 제거 + Id 추적으로 잘림/고아 버그 해결. summarize: 채팅 경로만 true.
        /// </summary>
        private static void StartOllamaStream(List<LLMClient.ChatMessage> messages, MessageCategory category, bool summarize,
            Action<string> onFinalText = null, Action onAlways = null)
        {
            int id = AddAssistantPlaceholder(category);
            CoroutineRunner.Start(LLMClient.SendOllamaStreaming(
                Config, messages,
                onToken: tokens =>
                {
                    AppendToMsgById(id, tokens);
                    ChatWindow.SetThinking(false);
                },
                onComplete: () =>
                {
                    string finalText = FinalizeStreamedMsg(id);   // 빈/[SKIP] 이면 null
                    ChatWindow.SetThinking(false);
                    if (!string.IsNullOrEmpty(finalText))
                    {
                        _diagnosticPending = false;  // 진단 대기 해제 (성공)
                        onFinalText?.Invoke(finalText);
                    }
                    if (summarize) MaybeSummarize();
                    onAlways?.Invoke();
                },
                onError: error =>
                {
                    RemoveMsgByIdIfEmpty(id);
                    ChatWindow.SetThinking(false);
                    ReportLLMFailure(error);
                    onAlways?.Invoke();
                }
            ));
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
                // ★ Streaming (Id 추적): placeholder 추가 + 토큰 append. 잘림/고아 버그 해결 + 중복 제거.
                StartOllamaStream(messages, MessageCategory.Default, summarize: true);
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
                            _diagnosticPending = false;  // 진단 대기 해제 (성공)
                        }
                        ChatWindow.SetThinking(false);
                        MaybeSummarize();
                    },
                    onError: error =>
                    {
                        ChatWindow.SetThinking(false);
                        ReportLLMFailure(error);
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
                StartOllamaStream(messages, MessageCategory.Combat, summarize: false);
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
                        ReportLLMFailure(error);
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
                StartOllamaStream(messages, MessageCategory.Vox, summarize: false);
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
                        ReportLLMFailure(error);
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
                StartOllamaStream(messages, MessageCategory.Vox, summarize: false);
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
                        ReportLLMFailure(error);
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
                StartOllamaStream(messages, MessageCategory.Vox, summarize: false);
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
                        ReportLLMFailure(error);
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
                StartOllamaStream(messages, MessageCategory.Scan, summarize: false);
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
                        ReportLLMFailure(error);
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
                StartOllamaStream(messages, category, summarize: true);
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
                        ReportLLMFailure(error);
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
                StartOllamaStream(messages, MessageCategory.Greeting, summarize: false);
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
                        ReportLLMFailure(error);
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
                // ★ Id 추적 + idle/vision 부수효과 복원(onFinalText: 활동시간·타이머·비전관찰, onAlways: pending 해제).
                StartOllamaStream(messages, MessageCategory.Scan, summarize: false,
                    onFinalText: text =>
                    {
                        _lastActivityTime = Time.time;
                        ResetIdleTimers();
                        if (isVision)
                        {
                            string summary = text.Length > 80 ? text.Substring(0, 80) + "..." : text;
                            GameEventCollector.AddEvent(GameEventType.VisionObservation, null, summary);
                        }
                    },
                    onAlways: () => _idleVisionPending = false);
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
                        ReportLLMFailure(error);
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

            Log.MachineSpirit.Debug($"[MachineSpirit] Summarizing {toSummarize.Count} old messages...");

            var summaryMessages = ContextBuilder.BuildSummaryPrompt(toSummarize);

            yield return LLMClient.SendBackgroundRequest(
                Config,
                summaryMessages,
                onResponse: summary =>
                {
                    // 요약 요청(10-30s) 도중 성격/모델 전환·수동 클리어로 _chatHistory 가 비워졌을 수 있다.
                    // captured endIdx 가 stale → 범위 밖 값으로 _summarizedUpToIndex 를 고정하면 이후 요약이
                    // 영구 중단된다(MaybeSummarize 의 unsummarizedCount 가 음수 고정). 현재 히스토리와 정합할 때만 커밋.
                    if (endIdx <= _chatHistory.Count && endIdx > _summarizedUpToIndex)
                    {
                        _conversationSummary = summary;
                        _summarizedUpToIndex = endIdx;
                        Log.MachineSpirit.Debug($"[MachineSpirit] Summary updated: {summary.Length} chars");
                    }
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
