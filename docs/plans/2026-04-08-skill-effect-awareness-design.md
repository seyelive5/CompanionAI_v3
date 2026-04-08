# Skill Effect Awareness — Design Document

**Date:** 2026-04-08
**Status:** Approved
**Author:** veria + Claude (brainstorming session)

## Problem Statement

The LLM combat AI currently sees abilities as bare names grouped by simple category (Atk/Buff/Heal/AoE/Dbf). It does not understand:

1. **Skill side-effects** — e.g., Run and Gun grants extra MP after attacking, but the LLM only sees "Run and Gun" as a Buff. After using it, the unit often just ends turn instead of using the new MP to reposition and attack again.
2. **Action sequencing** — e.g., buff → attack → bonus action → reposition → attack chains. Without knowing what each skill enables, the LLM cannot plan multi-step turns.
3. **Timing constraints** — e.g., Finisher abilities only effective on low-HP enemies, PreCombatBuff only worth using before engaging, TurnEnding skills must be used last.

User-observed examples:
- Run and Gun used with no follow-up movement/attack
- Tank ending turn early when more actions were available
- Skills used in wrong order (e.g., post-action buff used pre-action)

## Goals

1. Give the LLM enough information about each available skill to plan **multi-step action chains**
2. Use **existing infrastructure** where possible (RAG/KnowledgeIndex, AbilityDatabase)
3. Keep the token budget increase **modest** (~40% on SK line, ~50ms added latency)
4. **Zero regression risk** — fall back gracefully if any layer fails

## Non-Goals

- Per-character skill personality (handled by role detection)
- Predicting exact damage numbers (LLM doesn't need precision)
- LLM-distilled descriptions (deferred — user can opt-in later)
- Translating skill effects to other languages (English is most token-efficient)

## Architecture Overview

```
[Game Load]
  AbilityEffectCache.Initialize(modPath)
    ├─ Try load tactical_skill_cache.json
    │   └─ HIT  → restore Dictionary in ~10ms
    │   └─ MISS → build from AbilityDatabase + BlueprintAbility scan (5-10s, async)
    │              → save JSON
    └─ Ready

[Combat Turn]
  CompactBattlefieldEncoder.Encode()
    ├─ U/A/E/K (unchanged)
    └─ AppendSkillsLine()
        └─ For each ability:
             label = AbilityEffectCache.GetLabel(ability.Guid)  // O(1)
             output: "- {name} [{label}]"
  → LLM receives enriched SK line
```

### Key Insight

**90% of the infrastructure already exists:**
- `KnowledgeIndex` already indexes all `BlueprintAbility` with descriptions (cached to disk)
- `AbilityDatabase` already maps GUID → `AbilityFlags` + `AbilityTiming` (hand-curated)
- `AbilityClassificationData` already extracts `IsBurst`/`IsScatter`/`AoERadius`/`IsMelee` from game API

**Missing piece:** A translator from `AbilityFlags` + `AbilityTiming` → short, LLM-friendly English label, exposed as a fast lookup table.

## Components

### New: `Planning/LLM/AbilityEffectExtractor.cs` (~150 lines)

Pure function that converts metadata into a compact English label.

```csharp
public static class AbilityEffectExtractor
{
    /// <summary>
    /// AbilityInfo (DB entry) → label.
    /// Uses Timing as base + Flags as modifiers.
    /// </summary>
    public static string ExtractFromInfo(AbilityInfo info);

    /// <summary>
    /// BlueprintAbility (game data) → label.
    /// Fallback for abilities not in AbilityDatabase.
    /// Uses AbilityClassificationData.
    /// </summary>
    public static string ExtractFromBlueprint(BlueprintAbility ability);
}
```

**Examples:**
- `RunAndGun` (PostFirstAction) → `bonus action — use after attacking, grants extra MP`
- `Taunt` (Taunt timing, IsTauntAbility flag) → `taunt — pulls enemy attacks`
- `EmperorsWord` (PreAttackBuff, SelfTargetOnly) → `pre-attack buff — use before shooting`
- `Reload` (Reload timing, IsReloadAbility) → `reload weapon`
- `DeathSentence` (Finisher, targetHP=30) → `finisher — use on low-HP enemies`
- `FlameStorm` (Normal, IsAoE, AoERadius=3, Dangerous) → `AoE radius 3, ⚠ may hit allies`

**Constraints:**
- Max label length: 60 characters
- English only (most token-efficient for LLM)
- Pure function, no game API calls at runtime

### New: `Planning/LLM/AbilityEffectCache.cs` (~120 lines)

Fast O(1) lookup table, persisted to disk.

```csharp
public static class AbilityEffectCache
{
    private static readonly Dictionary<string, string> _labels = new();
    private static bool _initialized;

    /// <summary>
    /// Called once at game load.
    /// Builds Dictionary from AbilityDatabase + BlueprintAbility scan.
    /// Saves to tactical_skill_cache.json.
    /// </summary>
    public static IEnumerator Initialize(string modPath);

    /// <summary>O(1) lookup. Returns "" if guid unknown.</summary>
    public static string GetLabel(string abilityGuid);

    /// <summary>Stats for diagnostics.</summary>
    public static int LabelCount => _labels.Count;
}
```

**Cache file format (`tactical_skill_cache.json`):**
```json
{
  "gameVersion": "1.0.0",
  "schemaVersion": 1,
  "labels": {
    "22a25a3e418246ccbe95f2cc81c17473": "bonus action — use after attacking",
    "98f4a31b68e446ad9c63411c7b349146": "reload weapon",
    "...": "..."
  }
}
```

Cache invalidation: gameVersion or schemaVersion mismatch → rebuild.

### Modified: `Planning/LLM/CompactBattlefieldEncoder.cs`

`AppendSkillsLine()` rewritten to multi-line format with effect labels:

```
SK:
Atk:
- 단발 사격 [single shot]
- 점사 사격 [burst]
Buff:
- 황제의 말씀 [pre-attack buff — use before shooting]
- Run and Gun [bonus action — use after attacking]
AoE:
- 화염 폭풍 [AoE radius 3, ⚠ may hit allies]
```

If `AbilityEffectCache.GetLabel(guid)` returns empty string, the `[...]` is omitted (skill name only — current behavior preserved).

### Modified: `Main.cs`

Add one line to `Load()`:
```csharp
// existing
Planning.LLM.TacticalMemory.Initialize(modEntry.Path);
// new
MachineSpirit.CoroutineRunner.Start(
    Planning.LLM.AbilityEffectCache.Initialize(modEntry.Path));
```

## Data Flow

### Token Budget

| Component | Current | New | Delta |
|-----------|---------|-----|-------|
| U (self) | ~15 | ~15 | 0 |
| A (allies) | ~15 | ~15 | 0 |
| E (enemies) | ~25 | ~25 | 0 |
| K (key factors) | ~10 | ~10 | 0 |
| **SK (skills)** | **~30** | **~80** | **+50** |
| KB (knowledge) | ~10-30 | ~10-30 | 0 |
| CMD/PAST | ~10 | ~10 | 0 |
| **Total** | **~120** | **~170** | **+50 (~40%)** |

Latency impact: ~50ms additional TTFT, output tokens unchanged.

### Cache Build Flow

```
1st game load:
  AbilityEffectCache.Initialize() (async coroutine)
    ├─ Phase 1: Iterate AbilityDatabase (~hundreds of entries)
    │   └─ For each: ExtractFromInfo() → label → Dictionary
    ├─ Phase 2: Optional Blueprint scan (for fallback labels)
    │   └─ For each BlueprintAbility not in DB:
    │       ExtractFromBlueprint() → label → Dictionary
    └─ Phase 3: Save tactical_skill_cache.json

Subsequent game loads:
  AbilityEffectCache.Initialize()
    └─ Load JSON → restore Dictionary (~10ms)

Combat turn:
  Encoder.AppendSkillsLine()
    └─ AbilityEffectCache.GetLabel(guid) → O(1)
```

## Fallback Cascade

```
1. AbilityEffectCache.GetLabel(guid)
   ├─ DB hit (hand-curated)        → highest quality label
   ├─ DB miss + Blueprint hit       → auto-extracted label (good)
   └─ Both miss (rare ability)      → empty string (safe)

2. Encoder behavior:
   ├─ Non-empty label → "- name [label]"
   └─ Empty label    → "- name"  (current behavior)

3. System failure:
   ├─ Cache file corrupted   → rebuild on next load
   ├─ Cache build failed     → empty Dictionary, all empty labels
   └─ Initialize coroutine never finished → empty labels until done
```

**No regression risk** — every failure path degrades to current behavior.

## Edge Cases

| Case | Handling |
|------|----------|
| Ability not in DB | Blueprint fallback or empty label |
| Very long ability name | Effect label capped at 60 chars; name not truncated (search/match) |
| Cache file missing | Async rebuild (5-10s); first combat may use empty labels |
| Cache file from old game version | gameVersion mismatch → rebuild |
| KnowledgeIndex not ready yet | Independent — cache uses its own file |
| Game patches change ability | gameVersion bump → cache invalidated |

## Verification

1. **Build**: MSBuild Release succeeds, zero new warnings
2. **Cache**:
   - Log: `[AbilityEffectCache] Built N labels`
   - File exists: `<ModPath>/tactical_skill_cache.json`
3. **Encoder output** (debug log):
   - SK line uses new multi-line format
   - Run and Gun shows `[bonus action — use after attacking]`
4. **LLM behavior** (combat log):
   - Run and Gun followed by movement/attack chain (not bare end-turn)
   - Judge narration mentions skill effects (e.g., "use Run and Gun's bonus MP to close in")
5. **Performance**:
   - LLM Judge time within +100ms of baseline (~1500 → ~1600ms acceptable)
6. **Fallback**:
   - Delete cache file → rebuilds on next load
   - DB-missing ability → empty label, no crash

## Out of Scope (Future Work)

- **LLM-distilled descriptions** (Approach C in brainstorm): User-triggered "AI refine" button in mod UI that sends each ability through the LLM for a 1-2 sentence tactical summary. Higher quality but requires hundreds of LLM calls (~5 minutes one-time).
- **Per-character archetype awareness** in labels (e.g., "great for assassins"): too narrow.
- **Context-sensitive label selection** (different label depending on current situation): adds complexity for marginal gain.

## File Summary

| File | Change | LOC |
|------|--------|-----|
| `Planning/LLM/AbilityEffectExtractor.cs` | NEW | ~150 |
| `Planning/LLM/AbilityEffectCache.cs` | NEW | ~120 |
| `Planning/LLM/CompactBattlefieldEncoder.cs` | Modify `AppendSkillsLine()` | +30/-15 |
| `Main.cs` | Add `Initialize()` call | +2 |
| `docs/plans/2026-04-08-skill-effect-awareness-design.md` | NEW (this doc) | — |
