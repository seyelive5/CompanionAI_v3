# Machine Spirit World Awareness — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make Machine Spirit react to player choices, quests, soul marks, and read world state (profit, factions, quests, conviction) into every LLM call.

**Architecture:** EventCoalescer batches 5s of game events into one LLM call. GameEventCollector subscribes to 3 new global interfaces + polls for level-up/warp. ContextBuilder injects world state into every system prompt.

**Tech Stack:** C# .NET 4.8.1, Unity IMGUI, Harmony, Kingmaker.PubSubSystem EventBus

**API Note:** `ISectorMapWarpTravelHandler` and `IUnitLevelUpHandler` are entity-bound (ISubscriber<T>) — cannot use global EventBus.Subscribe. These will use polling in Update() instead.

---

### Task 1: EventCoalescer — 5-second merge queue

**Files:**
- Create: `MachineSpirit/EventCoalescer.cs`

**Step 1: Create EventCoalescer**

```csharp
// MachineSpirit/EventCoalescer.cs
using System.Collections.Generic;
using UnityEngine;

namespace CompanionAI_v3.MachineSpirit
{
    /// <summary>
    /// ★ v3.68.0: Batches simultaneous game events into a single LLM call.
    /// 5-second coalescing window — first event starts timer, others accumulate.
    /// </summary>
    public static class EventCoalescer
    {
        private const float COALESCE_WINDOW = 5f;

        private static readonly List<GameEvent> _pending = new List<GameEvent>();
        private static float _firstEventTime;
        private static bool _hasEvents;

        public static void Enqueue(GameEvent evt)
        {
            if (!_hasEvents)
            {
                _firstEventTime = Time.time;
                _hasEvents = true;
            }
            _pending.Add(evt);
        }

        /// <summary>
        /// Called from MachineSpirit.Update(). Flushes after 5s window.
        /// </summary>
        public static void Update()
        {
            if (!_hasEvents) return;
            if (Time.time - _firstEventTime < COALESCE_WINDOW) return;
            if (LLMClient.IsRequesting) return; // Wait for current request

            var batch = new List<GameEvent>(_pending);
            _pending.Clear();
            _hasEvents = false;

            if (batch.Count > 0)
                MachineSpirit.OnMergedEvents(batch);
        }

        public static void Clear()
        {
            _pending.Clear();
            _hasEvents = false;
        }
    }
}
```

**Step 2: Add to csproj**

The project uses `<Compile Include="..." />` — verify with existing entries. SDK-style csproj auto-includes, so this may not be needed. Check csproj format first.

**Step 3: Build to verify compilation**

Run: `"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo`

**Step 4: Commit**

```bash
git add MachineSpirit/EventCoalescer.cs
git commit -m "feat: EventCoalescer — 5s merge queue for simultaneous game events"
```

---

### Task 2: New GameEventTypes + 3 global EventBus interfaces

**Files:**
- Modify: `MachineSpirit/GameEventCollector.cs`

**Step 1: Add 5 new GameEventType values**

After `AreaTransition` in the enum:
```csharp
AreaTransition,
PlayerChoice,    // ★ v3.68.0: Player dialogue answer selection
SoulMarkShift,   // ★ v3.68.0: Conviction/alignment change
QuestUpdate,     // ★ v3.68.0: Quest started/completed/failed
WarpTravel,      // ★ v3.68.0: Warp travel events
LevelUp          // ★ v3.68.0: Character level up
```

**Step 2: Add ToString formats for new types**

In `GameEvent.ToString()`:
```csharp
if (Type == GameEventType.PlayerChoice)
    return $"Decision — Player chose: \"{Text}\"";
if (Type == GameEventType.SoulMarkShift)
    return $"Conviction — {Text}";
if (Type == GameEventType.QuestUpdate)
    return $"Mission — {Text}";
if (Type == GameEventType.WarpTravel)
    return $"Navigation — {Text}";
if (Type == GameEventType.LevelUp)
    return $"Advancement — {Text}";
```

**Step 3: Add 3 global interfaces to CombatEventSubscriber**

Add to interface list:
```csharp
ISelectAnswerHandler,       // ★ v3.68.0
ISoulMarkShiftHandler,      // ★ v3.68.0
IQuestHandler               // ★ v3.68.0
```

Add required usings:
```csharp
using Kingmaker.DialogSystem.Blueprints;
using Kingmaker.AreaLogic.QuestSystem;
using Kingmaker.UnitLogic.Alignments;
```

**Step 4: Implement 3 handler methods**

```csharp
// ★ v3.68.0: Player dialogue choice
public void HandleSelectAnswer(BlueprintAnswer answer)
{
    if (!MachineSpirit.IsActive) return;
    try
    {
        string text = answer?.DisplayText;
        if (string.IsNullOrEmpty(text)) return;
        if (text.Length > 200) text = text.Substring(0, 200) + "...";

        var evt = new GameEvent
        {
            Type = GameEventType.PlayerChoice,
            Speaker = "Lord Captain",
            Text = text,
            Timestamp = Time.time
        };

        // Add to main events
        AddEvent(GameEventType.PlayerChoice, "Lord Captain", text);

        // Also add to dialogue buffer for transcript
        if (_dialogueBuffer.Count >= MAX_DIALOGUE)
            _dialogueBuffer.RemoveAt(0);
        _dialogueBuffer.Add(new GameEvent
        {
            Type = GameEventType.PlayerChoice,
            Speaker = "Lord Captain",
            Text = text,
            Timestamp = Time.time
        });

        EventCoalescer.Enqueue(_events[_events.Count - 1]);
    }
    catch { }
}

// ★ v3.68.0: Soul mark / conviction shift
public void HandleSoulMarkShift(ISoulMarkShiftProvider provider)
{
    if (!MachineSpirit.IsActive) return;
    try
    {
        var shift = provider?.SoulMarkShift;
        if (shift == null || shift.Empty) return;

        string direction = shift.Direction.ToString();
        string desc = $"Conviction shifted toward {direction}";

        AddEvent(GameEventType.SoulMarkShift, null, desc);
        EventCoalescer.Enqueue(_events[_events.Count - 1]);
    }
    catch { }
}

// ★ v3.68.0: Quest lifecycle
public void HandleQuestStarted(Quest quest)
{
    if (!MachineSpirit.IsActive) return;
    try
    {
        string name = quest?.Blueprint?.name ?? "Unknown";
        AddEvent(GameEventType.QuestUpdate, null, $"New quest: {name}");
        EventCoalescer.Enqueue(_events[_events.Count - 1]);
    }
    catch { }
}

public void HandleQuestCompleted(Quest quest)
{
    if (!MachineSpirit.IsActive) return;
    try
    {
        string name = quest?.Blueprint?.name ?? "Unknown";
        AddEvent(GameEventType.QuestUpdate, null, $"Quest completed: {name}");
        EventCoalescer.Enqueue(_events[_events.Count - 1]);
    }
    catch { }
}

public void HandleQuestFailed(Quest quest)
{
    if (!MachineSpirit.IsActive) return;
    try
    {
        string name = quest?.Blueprint?.name ?? "Unknown";
        AddEvent(GameEventType.QuestUpdate, null, $"Quest FAILED: {name}");
        EventCoalescer.Enqueue(_events[_events.Count - 1]);
    }
    catch { }
}

public void HandleQuestUpdated(Quest quest)
{
    // Silent — too spammy, just log in sensor without LLM call
    if (!MachineSpirit.IsActive) return;
    try
    {
        string name = quest?.Blueprint?.name ?? "Unknown";
        AddEvent(GameEventType.QuestUpdate, null, $"Quest updated: {name}");
        // No EventCoalescer enqueue — updates are too frequent
    }
    catch { }
}
```

**Step 5: Add polling for entity-bound events in AddEvent**

For LevelUp and WarpTravel, since their interfaces are entity-bound (ISubscriber<T>), we will handle them via polling in Task 5 (MachineSpirit.Update). No handler methods needed here.

**Step 6: Build and commit**

---

### Task 3: BuildWorldContext() — game state in every prompt

**Files:**
- Modify: `MachineSpirit/ContextBuilder.cs`

**Step 1: Add required usings**

```csharp
using Kingmaker.AreaLogic.QuestSystem;
using Kingmaker.Enums;
```

**Step 2: Add BuildWorldContext() method**

Place after `BuildAreaContext()`:

```csharp
/// <summary>
/// ★ v3.68.0: World state context — quests, economy, factions, conviction.
/// Injected into every LLM call for persistent world awareness.
/// </summary>
private static string BuildWorldContext()
{
    try
    {
        var player = Game.Instance?.Player;
        if (player == null) return null;

        var sb = new StringBuilder();
        sb.AppendLine("[WORLD STATE]");

        // Active quests (top 5, attention-needed first)
        try
        {
            var questBook = player.QuestBook;
            if (questBook != null)
            {
                var quests = new List<string>();
                // Attention-needed first
                foreach (var q in questBook.Quests)
                {
                    if (q.State == QuestState.Started && q.NeedToAttention)
                    {
                        string name = q.Blueprint?.name ?? "Unknown";
                        quests.Insert(0, $"⚠ {name}");
                        if (quests.Count >= 5) break;
                    }
                }
                // Then other active quests
                if (quests.Count < 5)
                {
                    foreach (var q in questBook.Quests)
                    {
                        if (q.State == QuestState.Started && !q.NeedToAttention)
                        {
                            string name = q.Blueprint?.name ?? "Unknown";
                            quests.Add(name);
                            if (quests.Count >= 5) break;
                        }
                    }
                }
                if (quests.Count > 0)
                    sb.AppendLine($"Active Quests ({quests.Count}): {string.Join(", ", quests)}");
            }
        }
        catch { }

        // Profit Factor
        try
        {
            float pf = player.ProfitFactor.Total;
            sb.AppendLine($"Profit Factor: {pf:F0}");
        }
        catch { }

        // Faction standings
        try
        {
            var factions = player.FractionsReputation;
            if (factions != null && factions.Count > 0)
            {
                var standings = new List<string>();
                foreach (var kv in factions)
                {
                    if (kv.Key == FactionType.None) continue;
                    string label;
                    if (kv.Value >= 30) label = "Friendly";
                    else if (kv.Value >= 0) label = "Neutral";
                    else label = "Hostile";
                    standings.Add($"{kv.Key}: {kv.Value} ({label})");
                }
                if (standings.Count > 0)
                    sb.AppendLine($"Factions: {string.Join(" | ", standings)}");
            }
        }
        catch { }

        // Cargo
        try
        {
            var cargo = player.CargoState;
            if (cargo != null)
            {
                int totalEntities = 0;
                foreach (var c in cargo.CargoEntities)
                    totalEntities++;
                if (totalEntities > 0)
                    sb.AppendLine($"Cargo holds: {totalEntities}");
            }
        }
        catch { }

        // Money
        try
        {
            long money = player.Money;
            if (money > 0)
                sb.AppendLine($"Credits: {money:N0}");
        }
        catch { }

        string result = sb.ToString();
        return result.Contains("\n") ? result : null;
    }
    catch
    {
        return null;
    }
}
```

**Step 3: Inject into BuildSystemContent()**

After the party context section and before combat context:

```csharp
// ★ v3.68.0: World state — quests, economy, factions
string worldContext = BuildWorldContext();
if (!string.IsNullOrEmpty(worldContext))
{
    if (!hasSensorHeader)
    {
        systemSb.AppendLine();
        systemSb.AppendLine();
        systemSb.AppendLine("--- SENSOR DATA (read-only observations, do NOT copy or repeat these) ---");
        hasSensorHeader = true;
    }
    systemSb.AppendLine(worldContext);
}
```

**Step 4: Build and commit**

---

### Task 4: BuildForMergedEvents() + MessageCategory expansion

**Files:**
- Modify: `MachineSpirit/ContextBuilder.cs`
- Modify: `MachineSpirit/ChatWindow.cs`
- Modify: (MessageCategory enum location — in ChatWindow.cs or a shared file)

**Step 1: Add Faith and Quest to MessageCategory enum**

```csharp
public enum MessageCategory
{
    Default,
    Combat,
    Scan,
    Vox,
    Greeting,
    Faith,    // ★ v3.68.0: Soul mark / conviction
    Quest     // ★ v3.68.0: Quest updates, level-ups
}
```

**Step 2: Add colors in ChatWindow.GetCategoryColor()**

```csharp
MessageCategory.Faith => "#CC66CC",
MessageCategory.Quest => "#CCAA66",
```

**Step 3: Add BuildForMergedEvents() in ContextBuilder**

```csharp
/// <summary>
/// ★ v3.68.0: Build prompt for batched events from EventCoalescer.
/// </summary>
public static List<LLMClient.ChatMessage> BuildForMergedEvents(
    List<GameEvent> events,
    List<ChatMessage> chatHistory,
    MachineSpiritConfig config = null,
    string conversationSummary = null)
{
    var lang = Main.Settings?.UILanguage ?? Language.English;

    // Build event list description
    var eventDesc = new StringBuilder();
    eventDesc.AppendLine("The following events just occurred simultaneously:");
    foreach (var evt in events)
        eventDesc.AppendLine($"- {evt}");

    string instruction = lang switch
    {
        Language.Korean => $"다음 이벤트들이 방금 동시에 발생했다. 한 번에 자연스럽게 반응하라.\n{eventDesc}",
        Language.Russian => $"Следующие события только что произошли одновременно. Отреагируй на все естественно в одном ответе.\n{eventDesc}",
        Language.Japanese => $"以下のイベントが同時に発生した。一度に自然に反応せよ。\n{eventDesc}",
        Language.Chinese => $"以下事件刚刚同时发生。用一个回复自然地回应所有事件。\n{eventDesc}",
        _ => $"React naturally to all these events in one response.\n{eventDesc}"
    };

    return Build(chatHistory, config, instruction, conversationSummary);
}
```

**Step 4: Build and commit**

---

### Task 5: MachineSpirit.OnMergedEvents() + EventCoalescer.Update() + Polling

**Files:**
- Modify: `MachineSpirit/MachineSpirit.cs`

**Step 1: Add EventCoalescer.Update() call**

In MachineSpirit.Update(), after the greeting check:
```csharp
// ★ v3.68.0: Process coalesced events
EventCoalescer.Update();
```

**Step 2: Add level-up and warp polling**

In MachineSpirit.Update(), add polling for entity-bound events:

```csharp
// ★ v3.68.0: Poll for entity-bound events (every 2 seconds)
PollEntityEvents();
```

Add method:
```csharp
private static float _lastPollTime;
private static int _lastKnownLevelTotal;
private static bool _wasInWarp;

private static void PollEntityEvents()
{
    if (Time.time - _lastPollTime < 2f) return;
    _lastPollTime = Time.time;

    try
    {
        var player = Kingmaker.Game.Instance?.Player;
        if (player == null) return;

        // Level-up detection: sum all party levels, detect increase
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
            // Someone leveled up — find who
            foreach (var unit in player.PartyAndPets)
            {
                if (unit == null || unit.IsPet) continue;
                string name = unit.CharacterName ?? "Unknown";
                leveledChar = name;
                break; // Just grab the first name for now
            }
            if (leveledChar != null)
            {
                GameEventCollector.AddEvent(GameEventType.LevelUp, leveledChar, $"{leveledChar} leveled up");
                EventCoalescer.Enqueue(GameEventCollector.RecentEvents[GameEventCollector.RecentEvents.Count - 1]);
            }
        }
        _lastKnownLevelTotal = levelTotal;

        // Warp travel detection
        bool inWarp = false;
        try { inWarp = player.IsInWarpTravel; } catch { }
        if (inWarp && !_wasInWarp)
        {
            GameEventCollector.AddEvent(GameEventType.WarpTravel, null, "Warp travel initiated — Gellar field engaged");
            EventCoalescer.Enqueue(GameEventCollector.RecentEvents[GameEventCollector.RecentEvents.Count - 1]);
        }
        else if (!inWarp && _wasInWarp)
        {
            GameEventCollector.AddEvent(GameEventType.WarpTravel, null, "Warp travel concluded — Translation to realspace");
            EventCoalescer.Enqueue(GameEventCollector.RecentEvents[GameEventCollector.RecentEvents.Count - 1]);
        }
        _wasInWarp = inWarp;
    }
    catch { }
}
```

**Step 3: Add OnMergedEvents() handler**

```csharp
// ════════════════════════════════════════════════════════════
// ★ v3.68.0: Merged event handler — batched response
// ════════════════════════════════════════════════════════════

public static void OnMergedEvents(List<GameEvent> events)
{
    if (!IsActive) return;
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
                ChatWindow.SetThinking(false);
                MaybeSummarize();
            },
            onError: error =>
            {
                var msg = _chatHistory[responseIdx];
                if (string.IsNullOrEmpty(msg.Text))
                    _chatHistory.RemoveAt(responseIdx);
                ChatWindow.SetThinking(false);
            }
        ));
    }
    else
    {
        CoroutineRunner.Start(LLMClient.SendChatRequest(
            Config, messages,
            onResponse: response =>
            {
                _chatHistory.Add(new ChatMessage
                {
                    IsUser = false,
                    Text = response,
                    Timestamp = Time.time,
                    Category = category
                });
                ChatWindow.SetThinking(false);
                MaybeSummarize();
            },
            onError: _ => ChatWindow.SetThinking(false)
        ));
    }
}
```

**Step 4: Initialize polling state in Initialize()**

```csharp
_lastKnownLevelTotal = 0;
_wasInWarp = false;
_lastPollTime = 0f;
EventCoalescer.Clear();
```

**Step 5: Build and commit**

---

### Task 6: Version bump + final build + push

**Files:**
- Modify: `Info.json` — version to `3.68.0`

**Step 1: Update version**

**Step 2: Final rebuild**

**Step 3: Commit all, push, create release**
