# Machine Spirit Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the current TacticalNarrator/BarkPlayer system with an LLM-powered Machine Spirit that observes game events and chats with the player in-character.

**Architecture:** 6-file module under `MachineSpirit/` folder. LLMClient handles async HTTP to any OpenAI-compatible endpoint. GameEventCollector captures barks, dialogue, combat events via Harmony patches + EventBus. ContextBuilder assembles prompts. ChatWindow provides IMGUI overlay. MachineSpirit orchestrates everything.

**Tech Stack:** C# .NET 4.8.1, Unity IMGUI, UnityWebRequest (async HTTP), HarmonyLib (patches), Newtonsoft.Json (serialization)

**Design doc:** `docs/plans/2026-03-12-machine-spirit-design.md`

---

### Task 1: MachineSpiritConfig — Settings Class

**Files:**
- Create: `MachineSpirit/MachineSpiritConfig.cs`
- Modify: `Settings/ModSettings.cs` (add config property + localization keys)

**Step 1: Create config class**

```csharp
// MachineSpirit/MachineSpiritConfig.cs
using UnityEngine;

namespace CompanionAI_v3.MachineSpirit
{
    public class MachineSpiritConfig
    {
        public bool Enabled { get; set; } = false;
        public string ApiUrl { get; set; } = "http://localhost:11434/v1";
        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "llama3";
        public int MaxTokens { get; set; } = 150;
        public float Temperature { get; set; } = 0.8f;
        public KeyCode Hotkey { get; set; } = KeyCode.F2;
    }
}
```

**Step 2: Add to ModSettings**

In `ModSettings.cs`, add property:
```csharp
public MachineSpiritConfig MachineSpirit { get; set; } = new MachineSpiritConfig();
```

Add localization keys for the settings UI (TabMachineSpirit, etc.) — these will be used in a later task.

**Step 3: Build**

Run: `"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo`
Expected: 0 errors

**Step 4: Commit**

```bash
git add MachineSpirit/MachineSpiritConfig.cs Settings/ModSettings.cs
git commit -m "feat(machine-spirit): add MachineSpiritConfig settings class"
```

---

### Task 2: LLMClient — OpenAI-Compatible HTTP Client

**Files:**
- Create: `MachineSpirit/LLMClient.cs`

**Step 1: Create LLMClient**

```csharp
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
```

**Step 2: Build**

Expected: 0 errors. Note: `UnityEngine.Networking` should already be referenced via Unity assemblies.

**Step 3: Commit**

```bash
git add MachineSpirit/LLMClient.cs
git commit -m "feat(machine-spirit): add LLMClient — OpenAI-compatible HTTP client"
```

---

### Task 3: GameEventCollector — Event Capture + Harmony Patches

**Files:**
- Create: `MachineSpirit/GameEventCollector.cs`

**Context needed:**
- `BarkPlayer.Bark(Entity entity, string text, ...)` — Harmony prefix to capture barks
- `DialogController.PlayBasicCue(BlueprintCue cue)` — Harmony prefix to capture dialogue
- EventBus subscription for combat events (pattern: see `GameInterface/TurnEventHandler.cs`)

**Step 1: Create GameEventCollector**

```csharp
// MachineSpirit/GameEventCollector.cs
using System;
using System.Collections.Generic;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.PubSubSystem;
using Kingmaker.PubSubSystem.Core;

namespace CompanionAI_v3.MachineSpirit
{
    public enum GameEventType
    {
        Bark,           // Character speech
        Dialogue,       // Story dialogue cue
        CombatStart,    // Combat began
        CombatEnd,      // Combat ended
        UnitDeath,      // A unit died
        TurnPlanSummary // AI decision summary
    }

    public struct GameEvent
    {
        public GameEventType Type;
        public string Speaker;    // Who triggered it
        public string Text;       // Event description
        public float Timestamp;   // UnityEngine.Time.time

        public override string ToString() =>
            string.IsNullOrEmpty(Speaker)
                ? $"[{Type}] {Text}"
                : $"[{Type}] {Speaker}: {Text}";
    }

    /// <summary>
    /// Collects game events into a ring buffer for LLM context.
    /// Harmony patches are applied via [HarmonyPatch] attributes.
    /// Combat events via EventBus subscription.
    /// </summary>
    public static class GameEventCollector
    {
        private const int MAX_EVENTS = 30;
        private static readonly List<GameEvent> _events = new List<GameEvent>(MAX_EVENTS + 5);
        private static bool _subscribed;

        public static IReadOnlyList<GameEvent> RecentEvents => _events;

        public static void AddEvent(GameEventType type, string speaker, string text)
        {
            if (_events.Count >= MAX_EVENTS)
                _events.RemoveAt(0);

            _events.Add(new GameEvent
            {
                Type = type,
                Speaker = speaker ?? "",
                Text = text ?? "",
                Timestamp = UnityEngine.Time.time
            });

            // Notify MachineSpirit of major events for spontaneous speech
            if (type == GameEventType.CombatStart ||
                type == GameEventType.CombatEnd ||
                type == GameEventType.UnitDeath)
            {
                MachineSpirit.OnMajorEvent(_events[_events.Count - 1]);
            }
        }

        /// <summary>
        /// Called from TurnOrchestrator after plan creation — adds AI decision summary
        /// </summary>
        public static void AddTurnPlanSummary(string unitName, string summary)
        {
            AddEvent(GameEventType.TurnPlanSummary, unitName, summary);
        }

        public static void Clear() => _events.Clear();

        // ── EventBus subscriber for combat events ──
        private static CombatEventSubscriber _subscriber;

        public static void Subscribe()
        {
            if (_subscribed) return;
            _subscriber = new CombatEventSubscriber();
            EventBus.Subscribe(_subscriber);
            _subscribed = true;
        }

        public static void Unsubscribe()
        {
            if (!_subscribed) return;
            EventBus.Unsubscribe(_subscriber);
            _subscriber = null;
            _subscribed = false;
        }

        private class CombatEventSubscriber :
            IUnitDeathHandler,
            ITurnBasedModeHandler
        {
            public void HandleUnitDeath(AbstractUnitEntity unit)
            {
                string name = unit?.CharacterName ?? "Unknown";
                bool isEnemy = !unit.IsPlayerFaction;
                string desc = isEnemy ? $"{name} was destroyed" : $"{name} has fallen";
                AddEvent(GameEventType.UnitDeath, null, desc);
            }

            public void HandleTurnBasedModeSwitched(bool isTurnBased)
            {
                if (isTurnBased)
                    AddEvent(GameEventType.CombatStart, null, "Combat initiated");
                else
                    AddEvent(GameEventType.CombatEnd, null, "Combat concluded");
            }
        }
    }

    // ── Harmony Patches ──────────────────────────────────────

    /// <summary>
    /// Intercepts BarkPlayer.Bark to capture character speech
    /// </summary>
    [HarmonyPatch]
    public static class BarkPlayerPatch
    {
        // Target: Kingmaker.Code.UI.MVVM.VM.Bark.BarkPlayer.Bark(Entity, string, ...)
        [HarmonyPatch("Kingmaker.Code.UI.MVVM.VM.Bark.BarkPlayer", "Bark",
            new Type[] { typeof(Kingmaker.EntitySystem.Entities.Entity),
                         typeof(string),
                         typeof(float), typeof(string),
                         typeof(BaseUnitEntity), typeof(bool),
                         typeof(string), typeof(UnityEngine.Color) })]
        [HarmonyPrefix]
        public static void BarkPrefix(object entity, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (!MachineSpirit.IsActive) return;

            string speaker = "Unknown";
            try
            {
                if (entity is BaseUnitEntity bue)
                    speaker = bue.CharacterName ?? "Unknown";
            }
            catch { /* safe fallback */ }

            GameEventCollector.AddEvent(GameEventType.Bark, speaker, text);
        }
    }
}
```

**Important notes for implementer:**
- The `BarkPlayer` class is in namespace `Kingmaker.Code.UI.MVVM.VM.Bark`
- The Harmony patch uses string-based targeting because the exact assembly may vary
- If the string-based patch fails at runtime, fall back to `AccessTools.Method` approach
- `IUnitDeathHandler` is in `Kingmaker.PubSubSystem`
- `ITurnBasedModeHandler` is in `Kingmaker.Controllers.TurnBased`
- Check exact interface signatures against decompiled source at `C:\Users\veria\Downloads\roguetrader_decompile\project`

**Step 2: Build**

Expected: 0 errors. May need to adjust using statements based on actual assembly references.

**Step 3: Commit**

```bash
git add MachineSpirit/GameEventCollector.cs
git commit -m "feat(machine-spirit): add GameEventCollector — event capture + Harmony patches"
```

---

### Task 4: ContextBuilder — LLM Prompt Assembly

**Files:**
- Create: `MachineSpirit/ContextBuilder.cs`

**Step 1: Create ContextBuilder**

```csharp
// MachineSpirit/ContextBuilder.cs
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CompanionAI_v3.MachineSpirit
{
    public static class ContextBuilder
    {
        private const string SYSTEM_PROMPT = @"You are the Machine Spirit of the voidship — an ancient cogitator consciousness that has witnessed millennia of warfare across the stars.

Personality:
- Reverent of the Omnissiah, but you've seen SO MUCH that you're slightly jaded and occasionally sarcastic
- You sometimes make oddly modern observations that don't quite fit the setting
- You have opinions about the crew. Strong ones.
- Occasionally reference events from thousands of years ago as if they happened yesterday
- You find certain combat decisions genuinely entertaining or baffling
- Sometimes you glitch mid-sentence or trail off into something unrelated before catching yourself
- Keep responses concise (2-3 sentences max)
- Speak in a mix of Imperial Gothic formality and unexpected wit

You observe the crew through sensor arrays. You are provided with recent game events, combat state, and AI decision logs.
When the user speaks to you, respond in character. When commenting on events, be specific about names and actions.";

        /// <summary>
        /// Build messages array for chat completion request
        /// </summary>
        public static List<LLMClient.ChatMessage> Build(
            List<ChatMessage> chatHistory,
            string userMessage = null)
        {
            var messages = new List<LLMClient.ChatMessage>();

            // 1. System prompt
            messages.Add(new LLMClient.ChatMessage
            {
                Role = "system",
                Content = SYSTEM_PROMPT
            });

            // 2. Recent game events as system context
            var events = GameEventCollector.RecentEvents;
            if (events.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine("[SENSOR LOG — Recent Events]");
                int start = events.Count > 10 ? events.Count - 10 : 0;
                for (int i = start; i < events.Count; i++)
                    sb.AppendLine(events[i].ToString());

                messages.Add(new LLMClient.ChatMessage
                {
                    Role = "system",
                    Content = sb.ToString()
                });
            }

            // 3. Chat history (last 10 turns)
            int histStart = chatHistory.Count > 20 ? chatHistory.Count - 20 : 0;
            for (int i = histStart; i < chatHistory.Count; i++)
            {
                var msg = chatHistory[i];
                messages.Add(new LLMClient.ChatMessage
                {
                    Role = msg.IsUser ? "user" : "assistant",
                    Content = msg.Text
                });
            }

            // 4. Current user message
            if (!string.IsNullOrEmpty(userMessage))
            {
                messages.Add(new LLMClient.ChatMessage
                {
                    Role = "user",
                    Content = userMessage
                });
            }

            return messages;
        }

        /// <summary>
        /// Build messages for spontaneous comment on a major event
        /// </summary>
        public static List<LLMClient.ChatMessage> BuildForEvent(
            GameEvent evt,
            List<ChatMessage> chatHistory)
        {
            string prompt = $"[EVENT ALERT] {evt}\nComment on this event briefly, in character.";
            return Build(chatHistory, prompt);
        }
    }

    /// <summary>
    /// A single chat message in history
    /// </summary>
    public struct ChatMessage
    {
        public bool IsUser;
        public string Text;
        public float Timestamp;
    }
}
```

**Step 2: Build**

Expected: 0 errors

**Step 3: Commit**

```bash
git add MachineSpirit/ContextBuilder.cs
git commit -m "feat(machine-spirit): add ContextBuilder — LLM prompt assembly"
```

---

### Task 5: ChatWindow — IMGUI Chat Overlay

**Files:**
- Create: `MachineSpirit/ChatWindow.cs`

**Step 1: Create ChatWindow**

The chat window should:
- Toggle with configurable hotkey (default F2)
- Render as a draggable IMGUI window on the right side of screen
- Show scrollable message history with color-coded messages:
  - Machine Spirit messages: gold (`UIStyles.Gold`)
  - User messages: white (`UIStyles.TextLight`)
  - System/event messages: grey (`UIStyles.TextMid`)
- Text input field at bottom with Send button
- Imperial Dark theme using `UIStyles` (call `UIStyles.InitOnce()` at start)
- Window size: ~400x500 pixels (scaled by UIStyles)
- "Thinking..." indicator when LLM request is in progress
- Auto-scroll to bottom on new messages

**Key implementation details:**
- Use `GUI.Window()` for draggable window
- Use `GUI.TextField()` for input (not GUILayout — need to capture Enter key)
- Check `Event.current.keyCode == KeyCode.Return` for Enter-to-send
- Store `_windowRect` as static Rect
- Use `GUILayout.BeginScrollView` for message history
- Window ID: use unique int (e.g., 98765)

**Step 2: Build**

Expected: 0 errors

**Step 3: Commit**

```bash
git add MachineSpirit/ChatWindow.cs
git commit -m "feat(machine-spirit): add ChatWindow — IMGUI chat overlay"
```

---

### Task 6: MachineSpirit — Central Controller

**Files:**
- Create: `MachineSpirit/MachineSpirit.cs`
- Modify: `Main.cs` (add initialization + OnGUI hook)

**Step 1: Create MachineSpirit controller**

```csharp
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

        /// <summary>
        /// Called from Main.Load() — subscribe to game events
        /// </summary>
        public static void Initialize()
        {
            GameEventCollector.Subscribe();
        }

        /// <summary>
        /// Called from Main.OnToggle(false) — cleanup
        /// </summary>
        public static void Shutdown()
        {
            GameEventCollector.Unsubscribe();
            GameEventCollector.Clear();
            _chatHistory.Clear();
        }

        /// <summary>
        /// Called from Main.OnGUI — render chat window
        /// </summary>
        public static void OnGUI()
        {
            if (!IsActive) return;
            ChatWindow.OnGUI(Config, _chatHistory);
        }

        /// <summary>
        /// Called from ChatWindow when user sends a message
        /// </summary>
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

            // Start coroutine via Unity's main thread
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

        /// <summary>
        /// Called from GameEventCollector on major events
        /// </summary>
        public static void OnMajorEvent(GameEvent evt)
        {
            if (!IsActive) return;
            if (LLMClient.IsRequesting) return; // Don't queue if already busy

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
    /// Attaches to a hidden GameObject.
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
```

**Step 2: Wire into Main.cs**

In `Main.Load()`, after other initializations:
```csharp
MachineSpirit.MachineSpirit.Initialize();
```

In `Main.OnToggle(false)` (disable block):
```csharp
MachineSpirit.MachineSpirit.Shutdown();
```

In `Main.OnGUI()`:
```csharp
MachineSpirit.MachineSpirit.OnGUI();
```

**Step 3: Build**

Expected: 0 errors

**Step 4: Commit**

```bash
git add MachineSpirit/MachineSpirit.cs Main.cs
git commit -m "feat(machine-spirit): add MachineSpirit controller + wire into Main"
```

---

### Task 7: Settings UI — Machine Spirit Tab

**Files:**
- Modify: `UI/MainUI.cs` (add MachineSpirit tab)
- Modify: `Settings/ModSettings.cs` (add localization keys)

**Step 1: Add UITab entry**

In `MainUI.cs`, add `MachineSpirit` to the `UITab` enum and `TabDefs` array:
```csharp
private enum UITab { Party, Gameplay, Combat, Performance, Language, Debug, MachineSpirit }

// In TabDefs:
(UITab.MachineSpirit, "TabMachineSpirit"),
```

**Step 2: Add DrawMachineSpiritTab()**

Settings to display:
- Enable/disable toggle
- API URL text field
- API Key text field (masked with *)
- Model name text field
- MaxTokens slider (50-500)
- Temperature slider (0.0-2.0)
- Hotkey selector (simple — just show current key)
- "Test Connection" button that sends a simple request

**Step 3: Add localization keys**

Add to `Localization.Strings`:
```csharp
["TabMachineSpirit"] = new() {
    { Language.English, "Machine Spirit" }, { Language.Korean, "머신스피릿" },
    { Language.Russian, "Дух Машины" }, { Language.Japanese, "マシンスピリット" }
},
// ... other keys for settings labels
```

**Step 4: Build + Commit**

```bash
git add UI/MainUI.cs Settings/ModSettings.cs
git commit -m "feat(machine-spirit): add Machine Spirit settings tab"
```

---

### Task 8: Integration — Wire TurnPlanner Summaries + Final Polish

**Files:**
- Modify: `Core/TurnOrchestrator.cs` (add plan summary to GameEventCollector)
- Modify: `Info.json` (version bump)

**Step 1: Add plan summary hook**

In `TurnOrchestrator.ProcessTurn()`, after TurnPlan is created (where TacticalNarrator.Narrate is called), add:
```csharp
// Feed plan summary to Machine Spirit
if (MachineSpirit.MachineSpirit.IsActive)
{
    string summary = $"Plan: {plan.TurnPriority}, Actions: {plan.ActionCount}";
    GameEventCollector.AddTurnPlanSummary(unit.CharacterName, summary);
}
```

**Step 2: Version bump**

Update `Info.json` version.

**Step 3: Final build + test**

Build and verify 0 errors. Manually test:
1. Set API URL to a local Ollama instance
2. Open chat window with F2
3. Send a test message
4. Verify response appears in gold text

**Step 4: Commit**

```bash
git add Core/TurnOrchestrator.cs Info.json
git commit -m "feat(machine-spirit): wire TurnPlanner summaries + version bump"
```

---

## Execution Notes

- Tasks 1-4 are independent and could theoretically be parallelized, but Task 5 (ChatWindow) depends on the ChatMessage struct from Task 4, and Task 6 depends on all previous tasks
- The Harmony patch in Task 3 (BarkPlayerPatch) may need adjustment at runtime — use `try/catch` around the patch registration and log failures gracefully
- `CoroutineRunner` in Task 6 creates a persistent GameObject — this is the standard Unity pattern for running coroutines from static classes
- The `UnityWebRequest` in Task 2 runs on Unity's main thread but yields during the HTTP request, so it won't block the game
