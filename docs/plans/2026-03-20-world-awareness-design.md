# Machine Spirit World Awareness — Design Document

**Date:** 2026-03-20
**Version Target:** v3.68.0

## Goal

Make Machine Spirit a truly aware companion by subscribing to game world events and reading game state. The LLM should react to player choices, quest changes, moral shifts, warp travel, and level-ups — while always knowing the current quest objectives, profit factor, faction standings, and conviction path.

## Architecture: 4 Modules

### 1. EventCoalescer (NEW FILE)

Static class that batches simultaneous game events into a single LLM call.

- Events enqueued via `EventCoalescer.Enqueue(GameEvent)`
- 5-second coalescing window: first event starts timer, additional events accumulate
- After 5 seconds, merged event list passed to `MachineSpirit.OnMergedEvents()`
- If LLM is already requesting, queued events wait until current request completes
- Timer managed via `Update()` called from MachineSpirit's Update loop

### 2. GameEventCollector Extension

5 new EventBus interfaces added to CombatEventSubscriber:

| Interface | Handler Method | GameEventType | Sensor Log Format |
|-----------|---------------|---------------|-------------------|
| `ISelectAnswerHandler` | `HandleSelectAnswer(BlueprintAnswer)` | `PlayerChoice` | `Decision — Player chose: "{text}"` |
| `ISoulMarkShiftHandler` | `HandleSoulMarkShift(ISoulMarkShiftProvider)` | `SoulMarkShift` | `Conviction — Shifted toward {direction}` |
| `IQuestHandler` | `HandleQuestStarted/Completed/Failed(Quest)` | `QuestUpdate` | `Mission — Quest {state}: "{name}"` |
| `ISectorMapWarpTravelHandler` | `HandleWarpTravelStarted/Stopped(...)` | `WarpTravel` | `Navigation — Warp travel {started/ended}` |
| `IUnitLevelUpHandler` | `HandleUnitAfterLevelUp(LevelUpController)` | `LevelUp` | `Advancement — {name} leveled up` |

All handlers: record event in sensor log + enqueue to EventCoalescer.
PlayerChoice events also added to `_dialogueBuffer` for transcript context.

### 3. BuildWorldContext() in ContextBuilder

New method injected into every LLM call's system prompt. Reads game state:

```
[WORLD STATE]
Date: {CalendarRoot date}
Active Quests: {count} ({top 5 quest names, NeedToAttention first})
Profit Factor: {total}
Faction Standing: {faction}: {rep} ({label}) | ...
Cargo: {filled}% full
Conviction: {SoulMark direction}
```

Access paths:
- `Game.Instance.Player.QuestBook.Quests`
- `Game.Instance.Player.ProfitFactor.Total`
- `Game.Instance.Player.FractionsReputation`
- `Game.Instance.Player.CargoState`
- SoulMark direction via character facts

All access wrapped in try/catch — partial output on failure.

### 4. Merged Events Prompt + Message Colors

`ContextBuilder.BuildForMergedEvents(List<GameEvent>, ...)` builds a single LLM prompt containing world state + sensor data + all pending events. Instruction: "React to all these events naturally in one response."

New MessageCategory values:

| Category | Color | Used For |
|----------|-------|----------|
| Faith | `#CC66CC` (purple) | Soul mark / conviction shifts |
| Quest | `#CCAA66` (orange) | Quest updates, level-ups |

## Files Changed

| File | Change |
|------|--------|
| `MachineSpirit/EventCoalescer.cs` | **NEW** — 5s merge queue |
| `MachineSpirit/GameEventCollector.cs` | 5 interfaces + 5 event types |
| `MachineSpirit/ContextBuilder.cs` | BuildWorldContext() + BuildForMergedEvents() |
| `MachineSpirit/MachineSpirit.cs` | OnMergedEvents() + EventCoalescer.Update() |
| `MachineSpirit/ChatWindow.cs` | Faith/Quest color mapping |
| `CompanionAI_v3.csproj` | EventCoalescer.cs reference |

## Out of Scope (YAGNI)

- Companion affection/romance tracking
- Colony detail stats
- Ship detailed stats (PartStarship)
- Cross-session statistics persistence
