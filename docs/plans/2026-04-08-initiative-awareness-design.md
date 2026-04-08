# Initiative & Enemy Type Awareness — Design Document

**Date:** 2026-04-08
**Status:** Approved
**Author:** veria + Claude (brainstorming session)

## Problem Statement

The LLM combat AI sees enemies in the prompt as bare data: name, HP%, distance, and a few tactical tags (`HI` for high-priority, `CL` for clustered, `FIN` for finishable). It does **not** know:

1. **Turn order** — when each enemy will act relative to the current unit
2. **Weapon type** — whether each enemy is melee or ranged

This causes suboptimal decisions:
- A ranged ally might flee from a melee enemy that won't act for several turns (wasted movement)
- A unit might engage a distant ranged enemy when a closer melee enemy is about to attack
- Tank prioritization fails when an "imminent" threat is actually delayed by initiative

User-stated example from brainstorming:
> "원거리 캐릭이라면 적이 가까이 다가와도 해당적이 한참 나중에 행동하고 자신의 턴 다음에 행동하게 된다면 무시해도 되겠죠"

## Goals

1. Inject **per-enemy initiative timing** into the LLM prompt (relative to current unit)
2. Inject **per-enemy weapon type** (melee/ranged)
3. Use **existing game APIs** — no new tracking infrastructure
4. Keep token budget increase **modest** (~15% on E line)
5. **Zero regression risk** — graceful fallback if APIs unavailable

## Non-Goals

- Predicting exact enemy abilities or attack damage (too complex, low value)
- Tracking enemy buffs/debuffs (separate concern)
- Advanced enemy classification (psyker, hybrid, caster) — YAGNI for now
- Caching turn order across turns (game state changes too frequently)

## Architecture Overview

```
[LLM call at unit's turn]
  CompactBattlefieldEncoder.Encode(unit, situation)
    └─ AppendEnemiesLine()
        ├─ initMap = InitiativeTracker.GetEnemiesBeforeNextTurn(unit)  // O(n) once
        └─ for each enemy:
             output: "{idx}:{name},HP{hp},d{dist},...tags...,T{n},{type}"
             where:
               T{n}    = enemy acts n-th before unit's next turn (n >= 1)
               T+R     = enemy acts after unit's next turn (next round or later)
               type    = "melee" | "ranged" | (omitted if both false)
  → LLM receives enriched E line
```

### Key Insight

**All needed APIs already exist:**
- `Game.Instance.TurnController.UnitsAndSquadsByInitiativeForCurrentTurn` — turn order enumerable
- `CombatAPI.HasMeleeWeapon(BaseUnitEntity)` — melee detection
- `CombatAPI.HasRangedWeapon(BaseUnitEntity)` — ranged detection

**Missing piece:** A small helper that converts the turn order enumerable into a per-enemy "T number" relative to the current unit.

## Components

### New: `Planning/LLM/InitiativeTracker.cs` (~80 lines)

Pure helper that wraps the game's turn order API.

```csharp
public static class InitiativeTracker
{
    /// <summary>
    /// 현재 유닛 다음 차례 전에 행동하는 적들의 순서 매핑.
    /// Key: 적 entity, Value: T 번호 (1=가장 먼저)
    /// 다음 차례 이후의 적 또는 큐에 없는 적은 미포함 (호출자가 T+R로 처리)
    /// </summary>
    public static Dictionary<BaseUnitEntity, int> GetEnemiesBeforeNextTurn(BaseUnitEntity self);
}
```

**Algorithm:**
1. Get `UnitsAndSquadsByInitiativeForCurrentTurn` as a list
2. Find current unit's index
3. Walk from `selfIdx+1` to end, counting enemies → T1, T2, T3...
4. Wrap around: walk from 0 to `selfIdx-1`, continuing the count (next round same order)
5. Stop at unit's next turn (anything beyond is implicitly T+R)

**Constraints:**
- Pure function: no caching, no side effects
- try/catch wraps the entire body — game API failures return empty Dictionary
- Allies and non-combat units (familiars without standard turn) are skipped automatically by the game's queue filter

### Modified: `Planning/LLM/CompactBattlefieldEncoder.cs`

`AppendEnemiesLine()` rewrite:

```csharp
private static void AppendEnemiesLine(BaseUnitEntity unit, Situation situation)
{
    // ... existing cluster detection ...

    // ★ Initiative map — single call per encode
    var initMap = InitiativeTracker.GetEnemiesBeforeNextTurn(unit);

    _sb.Append("E:");
    int displayed = 0;

    for (int i = 0; i < enemies.Count && displayed < MAX_ENEMIES; i++)
    {
        var e = enemies[i];
        if (e == null || !e.IsConscious) continue;

        // ... existing name, HP, distance, HI/CL/FIN tags ...

        // ★ 이니셔티브 라벨
        if (initMap.TryGetValue(e, out int tNum))
            _sb.Append(",T").Append(tNum);
        else
            _sb.Append(",T+R");

        // ★ 무기 유형
        if (CombatAPI.HasMeleeWeapon(e))
            _sb.Append(",melee");
        else if (CombatAPI.HasRangedWeapon(e))
            _sb.Append(",ranged");
        // 둘 다 false면 라벨 생략 (안전 폴백)

        displayed++;
    }
    // ...
}
```

## Output Format Comparison

**Before:**
```
E:0:Psyker,HP40,d5,HI|1:Cult,HP100,d8|2:Cult,HP100,d8,CL|3:Heavy,HP90,d15
```

**After:**
```
E:0:Psyker,HP40,d5,HI,T1,melee|1:Cult,HP100,d8,T2,melee|2:Cult,HP100,d8,CL,T+R,melee|3:Heavy,HP90,d15,T3,ranged
```

LLM interpretation:
- `Psyker T1 melee` → first to act before me, melee → **immediate threat**
- `Cult T2 melee` → second to act → **secondary threat**
- `Cult T+R melee` → next round or later → **safe to ignore for now**
- `Heavy T3 ranged` → third, ranged → **distance-independent threat**

## Token Budget

| Component | Current | New | Delta |
|-----------|---------|-----|-------|
| Per-enemy text | `0:Psyker,HP40,d5,HI` (~10t) | + `,T1,melee` (~3-4t) | +3-4 |
| E line (8 enemies) | ~25 tokens | ~50 tokens | +25 |
| Total Encoder | ~150-180 | ~175-205 | +25 (~15%) |

Modest increase for high decision-making value.

## Edge Cases

| Case | Handling |
|------|----------|
| Not in turn-based combat | Empty queue → empty Dictionary → all enemies get `T+R` (harmless) |
| Self not in queue (rare) | IndexOf returns -1 → empty Dictionary → all `T+R` |
| Enemy died mid-turn | Not in queue → `T+R` (already irrelevant) |
| Familiar / no-standard-turn unit | Game API filters out via `UnitsOrderWithStandardTurn` |
| Round boundary (self acts last in round) | Wrap-around algorithm handles continuation into next round |
| Weapon slot empty | Both `HasMelee` and `HasRanged` false → label omitted |
| Game API exception | try/catch returns empty Dictionary → graceful fallback |

## Fallback Cascade

```
1. InitiativeTracker.GetEnemiesBeforeNextTurn(self)
   ├─ Queue valid + self found → Dictionary populated
   └─ Any failure → empty Dictionary (no exception)

2. Per-enemy T label
   ├─ Dictionary hit → "T{N}"
   └─ Dictionary miss → "T+R"

3. Per-enemy weapon type
   ├─ HasMelee → "melee"
   ├─ HasRanged → "ranged"
   └─ Both false → label omitted

4. Total system failure → existing E line behavior preserved (no regression)
```

## Performance

- `GetEnemiesBeforeNextTurn` called once per Encode (not per enemy)
- Queue enumeration: O(n) where n ≈ 10-20 units
- Dictionary build: ~100 bytes, GC'd immediately
- Per-call cost: ~0.1-0.2ms (negligible vs ~hundreds of ms for LLM call itself)

## Verification

1. **Build**: MSBuild Release succeeds
2. **In-game log inspection**:
   - E line shows `,T1,melee` style suffixes
   - Turn numbers match the game's initiative panel
   - Weapon types match enemy character sheets
3. **LLM behavior**:
   - Ranged units no longer flee from `T+R,melee` enemies
   - Threat prioritization weighted toward `T1`/`T2` enemies
   - Judge narration mentions "acts before me" or "delayed threat"
4. **Edge case stress**:
   - Combat start (queue may be empty briefly)
   - After multiple unit deaths
   - Bonus turn / Heroic Act usage

## Out of Scope (Future Work)

- **Enemy ability classification**: psyker, caster, hybrid (would need ability scanning, complex)
- **Per-enemy weapon range numeric** (e.g., `r12`): low value, hard to extract from arbitrary enemy
- **Predicting enemy AoE / debuff usage**: too complex, separate feature
- **Caching initiative across encoder calls**: not needed at current performance

## File Summary

| File | Change | LOC |
|------|--------|-----|
| `Planning/LLM/InitiativeTracker.cs` | NEW | ~80 |
| `Planning/LLM/CompactBattlefieldEncoder.cs` | Modify `AppendEnemiesLine()` | +15/-0 |
| `docs/plans/2026-04-08-initiative-awareness-design.md` | NEW (this doc) | — |
