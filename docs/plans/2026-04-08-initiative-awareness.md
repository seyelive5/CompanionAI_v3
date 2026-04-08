# Initiative Awareness Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Inject per-enemy initiative timing (T1/T2/.../T+R relative to current unit) and weapon type (melee/ranged) into the LLM combat AI's E line, so the LLM can decide whether to engage, ignore, or flee based on turn order and threat type.

**Architecture:** A small pure helper `InitiativeTracker` wraps the existing `Game.Instance.TurnController.UnitsAndSquadsByInitiativeForCurrentTurn` API to compute a `Dictionary<enemy, T-number>` per Encode call. `CompactBattlefieldEncoder.AppendEnemiesLine()` looks up each enemy and emits inline labels. Falls back gracefully to existing behavior on any failure.

**Tech Stack:** C# .NET Framework 4.8.1, Unity Mod Manager + Harmony, Owlcat Pathfinder/Rogue Trader game APIs (`TurnController`, `BaseUnitEntity`).

---

## Reference Materials

- **Design doc:** `docs/plans/2026-04-08-initiative-awareness-design.md`
- **Existing files to read before starting:**
  - `Planning/LLM/CompactBattlefieldEncoder.cs` lines 158-229 — current `AppendEnemiesLine()`
  - `GameInterface/CombatAPI.cs` lines 1054-1092 — `HasMeleeWeapon`/`HasRangedWeapon`
  - Decompile: `roguetrader_decompile/project/Code/Kingmaker/Controllers/TurnBased/TurnController.cs` line 151 — `UnitsAndSquadsByInitiativeForCurrentTurn` (returns `IEnumerable<MechanicEntity>`)
  - `Planning/LLM/AbilityEffectExtractor.cs` — example pure function with try/catch fallback

## Verification Commands

**Build (run after every code change):**
```bash
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" "c:\Users\veria\Downloads\CompanionAI_v3-master - v3.5.7\CompanionAI_v3.csproj" -p:Configuration=Release -t:Rebuild -v:minimal -nologo 2>&1 | tail -10
```

Expected on success: last line is `CompanionAI_v3 -> ...CompanionAI_v3.dll`. No `error CS` lines.

**No unit-test framework exists.** Verification is via:
1. Build success
2. In-game log inspection (look for `T1,melee`/`T+R,ranged` style suffixes in LLM prompt)
3. Manual play: ranged unit should no longer flee from `T+R,melee` enemies

---

## Task 1: Create `InitiativeTracker.cs`

Pure helper that computes enemy-to-T-number mapping.

**Files:**
- Create: `c:\Users\veria\Downloads\CompanionAI_v3-master - v3.5.7\Planning\LLM\InitiativeTracker.cs`

**Step 1: Create the file with this EXACT content**

```csharp
// Planning/LLM/InitiativeTracker.cs
// ★ Initiative Awareness: 적의 행동 순서를 현재 유닛 기준 상대 인덱스로 변환.
// LLM이 "이 적이 내 다음 행동 전에 나를 공격하는가?" 판단할 수 있도록 함.
using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;

namespace CompanionAI_v3.Planning.LLM
{
    /// <summary>
    /// 게임의 TurnController.UnitsAndSquadsByInitiativeForCurrentTurn 큐를
    /// 현재 유닛 기준 적-T번호 매핑으로 변환.
    ///
    /// T번호 의미:
    ///   T1, T2, T3... = 자신의 다음 차례 전에 행동하는 적 (1=가장 먼저)
    ///   미포함 (호출자가 T+R로 처리) = 자신의 다음 차례 이후에 행동
    ///
    /// 라운드 경계: 자신 다음 차례까지의 거리는 (현재 라운드 잔여) + (다음 라운드 시작~자신 직전).
    /// 큐는 현재 라운드만 있으므로 wrap-around 처리.
    /// </summary>
    public static class InitiativeTracker
    {
        private static readonly Dictionary<BaseUnitEntity, int> EmptyResult
            = new Dictionary<BaseUnitEntity, int>();

        /// <summary>
        /// 현재 유닛 다음 차례 전에 행동하는 적들의 T번호 매핑.
        /// 큐가 비어있거나 self를 못 찾거나 예외 발생 시 빈 Dictionary 반환.
        /// </summary>
        public static Dictionary<BaseUnitEntity, int> GetEnemiesBeforeNextTurn(BaseUnitEntity self)
        {
            if (self == null) return EmptyResult;

            try
            {
                var turnController = Game.Instance?.TurnController;
                if (turnController == null) return EmptyResult;

                var queueEnumerable = turnController.UnitsAndSquadsByInitiativeForCurrentTurn;
                if (queueEnumerable == null) return EmptyResult;

                // BaseUnitEntity로 필터링 (UnitSquad 등 비유닛 제외)
                var queue = queueEnumerable
                    .OfType<BaseUnitEntity>()
                    .ToList();

                if (queue.Count == 0) return EmptyResult;

                int selfIdx = queue.IndexOf(self);
                if (selfIdx < 0) return EmptyResult;

                var result = new Dictionary<BaseUnitEntity, int>();
                int counter = 1;

                // 1단계: 자신 이후 ~ 큐 끝
                for (int i = selfIdx + 1; i < queue.Count; i++)
                {
                    if (IsEnemy(queue[i], self))
                    {
                        result[queue[i]] = counter++;
                    }
                }

                // 2단계: 큐 시작 ~ 자신 직전 (다음 라운드 wrap-around)
                for (int i = 0; i < selfIdx; i++)
                {
                    if (IsEnemy(queue[i], self))
                    {
                        result[queue[i]] = counter++;
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[InitiativeTracker] Failed: {ex.Message}");
                return EmptyResult;
            }
        }

        /// <summary>적 판별 — 현재 유닛 기준 적대 관계인지</summary>
        private static bool IsEnemy(BaseUnitEntity candidate, BaseUnitEntity self)
        {
            if (candidate == null || self == null) return false;
            if (candidate == self) return false;
            try
            {
                return candidate.IsEnemy(self);
            }
            catch
            {
                return false;
            }
        }
    }
}
```

**Step 2: Build to verify**

Run the build command from Reference Materials. Expect `CompanionAI_v3 -> ...dll` with no errors.

**Note on `BaseUnitEntity.IsEnemy`**: This is a standard Owlcat API method. If the build fails with `error CS1061: 'BaseUnitEntity' does not contain a definition for 'IsEnemy'`, search the codebase:
```
Use Grep tool: pattern "\.IsEnemy\(" path "GameInterface" or path "Analysis"
```
Existing code in this project uses `IsEnemy()` extensively (e.g., `CombatAPI.GetEnemies` filtering). If the method has a different signature, adapt the call.

**Step 3: Commit**

```bash
cd "c:\Users\veria\Downloads\CompanionAI_v3-master - v3.5.7"
git add Planning/LLM/InitiativeTracker.cs
git commit -m "feat: InitiativeTracker — enemy turn order relative to current unit

Pure helper that wraps TurnController.UnitsAndSquadsByInitiativeForCurrentTurn
into a Dictionary<enemy, T-number> mapping. T1=acts first before next turn,
T2=second, etc. Wraps around to next round to handle round boundary correctly.
Returns empty Dictionary on any game API failure.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: Modify `CompactBattlefieldEncoder.AppendEnemiesLine()` to inject T-numbers

**Files:**
- Modify: `c:\Users\veria\Downloads\CompanionAI_v3-master - v3.5.7\Planning\LLM\CompactBattlefieldEncoder.cs` lines 158-229

**Step 1: Read the current method**

Use the Read tool to read lines 155-230 of `CompactBattlefieldEncoder.cs` to see the current `AppendEnemiesLine` method and understand its structure.

**Step 2: Locate the insertion points**

Two insertions needed:

A) **Top of method** (after the cluster detection, before `_sb.Append("E:")` at line 193):
```csharp
            // ★ 이니셔티브 매핑 — 한 번만 빌드 (적별 lookup용)
            var initMap = InitiativeTracker.GetEnemiesBeforeNextTurn(unit);
```

B) **End of per-enemy block** (after the `if (eHP < 20f) _sb.Append(",FIN");` line, before `displayed++;`):
```csharp
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
```

**Step 3: Apply both edits**

Use the Edit tool with `replace_all=false` to apply each insertion. Be careful to use enough surrounding context to make each `old_string` unique within the file.

**Step 4: Update the example comment at top of the section**

Find this line (around line 155):
```csharp
        // E:0:Psyker,HP40,d5,HI|1:Cult,HP100,d8|2:Cult,HP100,d8,CL|3:Heavy,HP90,d15
```

Replace with:
```csharp
        // E:0:Psyker,HP40,d5,HI,T1,melee|1:Cult,HP100,d8,T2,melee|2:Cult,HP100,d8,CL,T+R,melee|3:Heavy,HP90,d15,T3,ranged
```

**Step 5: Build to verify**

Run the build command. Expect clean build.

**If build fails because `InitiativeTracker` not found:**
The class is in the same namespace (`CompanionAI_v3.Planning.LLM`) so no `using` is needed. Verify the file exists at `Planning/LLM/InitiativeTracker.cs` from Task 1.

**Step 6: Commit**

```bash
cd "c:\Users\veria\Downloads\CompanionAI_v3-master - v3.5.7"
git add Planning/LLM/CompactBattlefieldEncoder.cs
git commit -m "feat: inject initiative + weapon type labels into E line

E line per-enemy now includes:
  T1/T2/.../T+R  = relative turn order before unit's next action
  melee/ranged   = weapon type (or omitted if both false)

Allows LLM to ignore distant enemies that won't act soon, prioritize
imminent threats, and decide flee/engage based on weapon type.

Token budget: ~25 -> ~50 tokens for E section (+15% total).

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: Manual smoke test (no code changes)

Verify everything works in-game.

**Step 1: Launch the game**

Start Rogue Trader. Wait for the main menu, then load a save with active turn-based combat (or trigger one).

**Step 2: Wait for an LLM-enabled character's turn**

The LLM Combat AI must be enabled (`EnableLLMCombatAI` = true and per-character `EnableLLMJudge` = true).

**Step 3: Check the game log for the new E line format**

Open `C:\Users\veria\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\GameLogFull.txt` and search for the most recent `[LLMScorer]` or `[LLM Judge]` block.

Look for E line in the encoded battlefield state (logged at debug level via `[LLMScorer] -> ...` and the request body). It should be:
```
E:0:<name>,HP<n>,d<n>,...,T<n>,<type>|1:<name>,...
```

Examples:
- `E:0:Psyker,HP40,d5,HI,T1,melee|1:Cult,HP100,d8,T+R,ranged`
- The first enemy listed (idx 0) is not necessarily T1 — T number depends on initiative, not list order

**Step 4: Verify turn order accuracy**

Cross-reference with the in-game initiative panel (shows turn order at the top of screen). The T numbers in the E line should match: enemies acting before your next turn should have T1, T2, T3...; enemies acting after should have T+R.

**Step 5: Verify weapon type accuracy**

Hover over enemies in-game to see their weapons. Confirm:
- Enemies with melee weapons → `,melee`
- Enemies with ranged weapons → `,ranged`
- Enemies with both → `,melee` (priority)
- Enemies with neither (rare, no weapon equipped) → label omitted

**Step 6: Behavioral test**

Set up a scenario where a ranged ally has a melee enemy that:
- Is far away (d > 10)
- Is `T+R` (acts after the ally's next turn)

Expected: The LLM should NOT prioritize fleeing from this enemy. It should focus on closer/more immediate threats. Look at the Judge narration to see if it mentions "delayed threat" or similar.

**Step 7: Edge case test (optional)**

If you can find a save with a familiar / pet that has no standard turn, verify no crashes.

**No commit for this task** — verification only. If anything fails, debug and fix in a follow-up task.

---

## Task 4: Bump version + release zip

**Files:**
- Modify: `Info.json` (version bump)

**Step 1: Read current version**

Read `Info.json`. Current value should be `"3.90.0"`.

**Step 2: Bump to 3.92.0**

Edit `Info.json`, change `"Version"` to `"3.92.0"`.

**Step 3: Build to verify**

Run the build command one final time.

**Step 4: Create release zip**

```bash
powershell -Command '$dllPath = $env:LOCALAPPDATA + "Low\Owlcat Games\Warhammer 40000 Rogue Trader\UnityModManager\CompanionAI_v3\CompanionAI_v3.dll"; $infoPath = "C:\Users\veria\Downloads\CompanionAI_v3-master - v3.5.7\Info.json"; $zipPath = "C:\Users\veria\Downloads\CompanionAI_v3_3.92.0.zip"; if (Test-Path $zipPath) { Remove-Item $zipPath }; Compress-Archive -Path $dllPath, $infoPath -DestinationPath $zipPath -Force; $size = [math]::Round((Get-Item $zipPath).Length / 1KB); Write-Output "Created: $zipPath ($size KB)"'
```

Expected: `Created: ... (~547 KB)`.

**Step 5: Commit version bump**

```bash
cd "c:\Users\veria\Downloads\CompanionAI_v3-master - v3.5.7"
git add Info.json
git commit -m "chore: bump version to 3.92.0 — initiative awareness

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

**Step 6: Push to remote (ASK USER FIRST)**

Do NOT push without explicit user permission. Ask:
> All tasks complete. Ready to push commits and create v3.92.0 GitHub release. Proceed?

If user confirms:
```bash
cd "c:\Users\veria\Downloads\CompanionAI_v3-master - v3.5.7"
git push origin master
gh release create "v3.92.0" "C:\Users\veria\Downloads\CompanionAI_v3_3.92.0.zip" --title "v3.92.0" --notes "..."
```

(Notes content drafted from design doc.)

---

## Task Summary

| # | Task | Files | Risk |
|---|------|-------|------|
| 1 | `InitiativeTracker.cs` (new helper) | NEW (~80 lines) | Low (pure function, try/catch wrapped) |
| 2 | Encoder integration (E line) | `CompactBattlefieldEncoder.cs` (+15 lines) | Medium (output format change) |
| 3 | Manual smoke test | None | — |
| 4 | Version bump + release | `Info.json` + zip | Low |

**Critical path:** 1 → 2 → 3 → 4

**Dependencies:**
- Task 2 depends on Task 1 (uses `InitiativeTracker`)
- Task 3 depends on Tasks 1+2
- Task 4 depends on Task 3 (smoke test must pass)

**Estimated total time:** 20-30 minutes of focused work.
