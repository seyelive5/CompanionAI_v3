# Skill Effect Awareness Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Enrich the LLM Combat AI's skill awareness by injecting compact English effect labels into the SK line of the battlefield encoder, so the LLM understands skill side-effects (e.g., Run and Gun grants extra MP) and can plan multi-step action chains.

**Architecture:** A pure-function `AbilityEffectExtractor` translates `AbilityFlags` + `AbilityTiming` into compact English labels. A static `AbilityEffectCache` holds a `Dictionary<guid, label>` built once at game load and persisted to `tactical_skill_cache.json`. `CompactBattlefieldEncoder.AppendSkillsLine()` looks up labels per skill and emits them inline. All layers fall back gracefully — empty label means current behavior (skill name only).

**Tech Stack:** C# .NET Framework 4.8.1, Unity Mod Manager + Harmony, Newtonsoft.Json, Owlcat Pathfinder/Rogue Trader game APIs (`BlueprintAbility`, `AbilityData`).

---

## Reference Materials

- **Design doc:** `docs/plans/2026-04-08-skill-effect-awareness-design.md`
- **Existing files to read before starting:**
  - `Data/AbilityInfo.cs` — `AbilityTiming`, `AbilityFlags`, `AbilityClassificationData`
  - `Data/AbilityDatabase.cs` lines 24-200, 919-960 — `AbilityInfo`, `GetInfo()`, `GetGuid()`
  - `Planning/LLM/CompactBattlefieldEncoder.cs` lines 320-392 — current `AppendSkillsLine()`
  - `Planning/LLM/TacticalMemory.cs` — file I/O pattern (Newtonsoft.Json, `Initialize(modPath)`)
  - `Main.cs` lines 40-60 — initialization area

## Verification Commands

**Build (run after every code change):**
```bash
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" "c:\Users\veria\Downloads\CompanionAI_v3-master - v3.5.7\CompanionAI_v3.csproj" -p:Configuration=Release -t:Rebuild -v:minimal -nologo 2>&1 | tail -10
```

Expected on success: last line is `CompanionAI_v3 -> ...CompanionAI_v3.dll`. No `error CS` lines anywhere in output.

**No unit-test framework exists in this project.** Verification is done by:
1. Build success (compile errors catch most type/syntax issues)
2. Manual log inspection after running combat in-game
3. Cache file existence check

---

## Task 1: Add `GetAllInfos()` enumerator to `AbilityDatabase`

The cache builder needs to iterate every entry in the private `Database` dictionary. Currently no public enumeration exists.

**Files:**
- Modify: `Data/AbilityDatabase.cs` (add method near line 950, after `GetInfo(AbilityData)`)

**Step 1: Add the enumeration method**

Open `Data/AbilityDatabase.cs`, find this existing method around line 945:
```csharp
public static AbilityInfo GetInfo(AbilityData ability)
{
    string guid = GetGuid(ability);
    return GetInfo(guid);
}
```

Add immediately after it:
```csharp
/// <summary>
/// 등록된 모든 AbilityInfo 항목 enumeration.
/// AbilityEffectCache 빌드용.
/// </summary>
public static System.Collections.Generic.IEnumerable<AbilityInfo> GetAllInfos()
{
    return Database.Values;
}
```

**Step 2: Build to verify**

Run the build command above. Expect `CompanionAI_v3 -> ...dll` with no errors.

**Step 3: Commit**

```bash
cd "c:\Users\veria\Downloads\CompanionAI_v3-master - v3.5.7"
git add Data/AbilityDatabase.cs
git commit -m "feat: add GetAllInfos enumerator to AbilityDatabase

Enables AbilityEffectCache to iterate all hand-curated entries
when building the GUID->label dictionary.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: Create `AbilityEffectExtractor.cs` (skeleton + AbilityInfo path)

Pure-function translator from `AbilityInfo` (DB entry) to a short English label.

**Files:**
- Create: `Planning/LLM/AbilityEffectExtractor.cs`

**Step 1: Create the file with skeleton + Timing/Flag mapping**

Create `Planning/LLM/AbilityEffectExtractor.cs` with this content:

```csharp
// Planning/LLM/AbilityEffectExtractor.cs
// ★ Skill Effect Awareness: AbilityFlags + Timing → 짧은 영어 효과 라벨
// LLM 프롬프트에 주입되어 스킬 부가효과(Run and Gun의 보너스 MP 등)를 인식시킴
using System.Text;
using CompanionAI_v3.Data;

namespace CompanionAI_v3.Planning.LLM
{
    /// <summary>
    /// AbilityInfo / BlueprintAbility 메타데이터를 LLM-친화적 영어 라벨로 변환.
    /// 순수 함수 — 게임 API 호출 없음, 캐시 가능.
    /// 결과는 AbilityEffectCache에 저장됨.
    /// </summary>
    public static class AbilityEffectExtractor
    {
        private const int MAX_LABEL_LENGTH = 60;

        /// <summary>
        /// AbilityInfo (hand-curated DB 항목) → 효과 라벨.
        /// Timing이 base, Flags가 modifier 역할.
        /// </summary>
        public static string ExtractFromInfo(AbilityInfo info)
        {
            if (info == null) return "";

            var sb = new StringBuilder(64);

            // 1. Timing → base label
            string timingLabel = TimingToLabel(info.Timing);
            if (!string.IsNullOrEmpty(timingLabel))
                sb.Append(timingLabel);

            // 2. Flags → modifier suffixes
            AppendFlagModifiers(sb, info.Flags);

            return Truncate(sb.ToString());
        }

        // ═══════════════════════════════════════════════════════════
        // Timing 변환
        // ═══════════════════════════════════════════════════════════

        private static string TimingToLabel(AbilityTiming timing)
        {
            switch (timing)
            {
                case AbilityTiming.PostFirstAction:
                    return "bonus action — use after attacking";
                case AbilityTiming.PreCombatBuff:
                    return "pre-combat — use before engaging";
                case AbilityTiming.PreAttackBuff:
                    return "pre-attack buff — use before shooting";
                case AbilityTiming.TurnEnding:
                    return "ends turn — use last";
                case AbilityTiming.Finisher:
                    return "finisher — use on low-HP enemies";
                case AbilityTiming.GapCloser:
                    return "gap closer — closes distance";
                case AbilityTiming.HeroicAct:
                    return "heroic act — needs Momentum 175+";
                case AbilityTiming.DesperateMeasure:
                    return "desperate measure — needs Momentum 25";
                case AbilityTiming.Reload:
                    return "reload weapon";
                case AbilityTiming.Taunt:
                    return "taunt — pulls enemy attacks";
                case AbilityTiming.Healing:
                    return "heals ally";
                case AbilityTiming.Debuff:
                    return "debuff — apply before attacking";
                case AbilityTiming.Emergency:
                    return "emergency — use when low HP";
                case AbilityTiming.SelfDamage:
                    return "self-damage — costs HP";
                case AbilityTiming.DangerousAoE:
                    return "AoE — ⚠ may hit allies";
                case AbilityTiming.RighteousFury:
                    return "righteous fury — after killing enemy";
                case AbilityTiming.DOTIntensify:
                    return "intensifies damage-over-time";
                case AbilityTiming.ChainEffect:
                    return "chain effect";
                case AbilityTiming.PositionalBuff:
                    return "positional buff — frontline/support/rear zone";
                case AbilityTiming.Stratagem:
                    return "tactic zone enhancement";
                case AbilityTiming.Marker:
                    return "marks target — no damage";
                case AbilityTiming.CrowdControl:
                    return "crowd control — stun/paralysis";
                case AbilityTiming.Grenade:
                    return "grenade — thrown explosive";
                default:
                    return ""; // Normal: no special timing label
            }
        }

        // ═══════════════════════════════════════════════════════════
        // Flag 변환 (suffix로 추가)
        // ═══════════════════════════════════════════════════════════

        private static void AppendFlagModifiers(StringBuilder sb, AbilityFlags flags)
        {
            if (flags == AbilityFlags.None) return;

            // 가장 중요한 플래그만 선택 (토큰 절약)
            if ((flags & AbilityFlags.IsRetreatCapable) != 0)
                Append(sb, "grants retreat movement");

            if ((flags & AbilityFlags.IsCautiousApproach) != 0)
                Append(sb, "defensive stance");

            if ((flags & AbilityFlags.IsConfidentApproach) != 0)
                Append(sb, "aggressive stance");

            if ((flags & AbilityFlags.IsDefensiveBuff) != 0)
                Append(sb, "+defense");

            if ((flags & AbilityFlags.IsOffensiveBuff) != 0)
                Append(sb, "+offense");

            if ((flags & AbilityFlags.HasCC) != 0)
                Append(sb, "stuns/paralyzes");

            if ((flags & AbilityFlags.HasDOT) != 0)
                Append(sb, "damage over time");

            if ((flags & AbilityFlags.IsBurst) != 0)
                Append(sb, "burst fire");

            if ((flags & AbilityFlags.IsScatter) != 0)
                Append(sb, "scatter");

            if ((flags & AbilityFlags.IsMelee) != 0)
                Append(sb, "melee");

            if ((flags & AbilityFlags.IsAoE) != 0)
                Append(sb, "AoE");

            if ((flags & AbilityFlags.Dangerous) != 0)
                Append(sb, "⚠ may hit allies");

            if ((flags & AbilityFlags.IsFreeAction) != 0)
                Append(sb, "free action");

            if ((flags & AbilityFlags.OncePerTurn) != 0)
                Append(sb, "1/turn");

            if ((flags & AbilityFlags.SingleUse) != 0)
                Append(sb, "1/combat");
        }

        private static void Append(StringBuilder sb, string text)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(text);
        }

        private static string Truncate(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length <= MAX_LABEL_LENGTH) return s;
            return s.Substring(0, MAX_LABEL_LENGTH - 3) + "...";
        }
    }
}
```

**Step 2: Build to verify**

Run the build command. Expect success.

**Step 3: Commit**

```bash
git add Planning/LLM/AbilityEffectExtractor.cs
git commit -m "feat: AbilityEffectExtractor — Timing/Flag to English label

Pure function translator. Maps AbilityTiming + AbilityFlags into
compact English effect labels (max 60 chars) for LLM consumption.
Handles all 22 timing types + 14 most-relevant flag modifiers.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: Add BlueprintAbility fallback to `AbilityEffectExtractor`

For abilities not in the hand-curated `AbilityDatabase`, extract a label directly from the game's `BlueprintAbility` properties.

**Files:**
- Modify: `Planning/LLM/AbilityEffectExtractor.cs` (add `ExtractFromBlueprint` method)

**Step 1: Add the fallback method**

Open `Planning/LLM/AbilityEffectExtractor.cs`, add this method **after** `ExtractFromInfo`:

```csharp
/// <summary>
/// BlueprintAbility (게임 데이터) → 효과 라벨.
/// AbilityDatabase에 없는 ability용 폴백.
/// 게임 API 속성 (range, AbilityTags 등)에서 직접 추출.
/// </summary>
public static string ExtractFromBlueprint(Kingmaker.UnitLogic.Abilities.Blueprints.BlueprintAbility ability)
{
    if (ability == null) return "";

    var sb = new StringBuilder(64);

    try
    {
        // AbilityTag 기반 분류 (가장 신뢰할 수 있는 게임 API)
        var tag = ability.Tag;
        if (tag != Kingmaker.UnitLogic.Abilities.AbilityTag.None)
        {
            string tagLabel = TagToLabel(tag);
            if (!string.IsNullOrEmpty(tagLabel))
                sb.Append(tagLabel);
        }

        // Range 정보 (있으면)
        if (ability.Range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Personal)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append("self");
        }
        else if (ability.Range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Touch)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append("melee range");
        }

        // ActionType (Standard/Move/Free)
        if (ability.ActionType == Kingmaker.UnitLogic.Commands.Base.UnitCommand.CommandType.Free)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append("free action");
        }
    }
    catch
    {
        // Game API 호출 실패 시 빈 라벨 반환 (안전 폴백)
        return "";
    }

    return Truncate(sb.ToString());
}

private static string TagToLabel(Kingmaker.UnitLogic.Abilities.AbilityTag tag)
{
    switch (tag)
    {
        case Kingmaker.UnitLogic.Abilities.AbilityTag.Heal:
            return "heals ally";
        case Kingmaker.UnitLogic.Abilities.AbilityTag.Damage:
            return "deals damage";
        case Kingmaker.UnitLogic.Abilities.AbilityTag.Buff:
            return "buff effect";
        case Kingmaker.UnitLogic.Abilities.AbilityTag.Debuff:
            return "debuff effect";
        case Kingmaker.UnitLogic.Abilities.AbilityTag.ThrowingGrenade:
            return "grenade — thrown explosive";
        case Kingmaker.UnitLogic.Abilities.AbilityTag.UsingCombatDrug:
            return "combat drug — temporary boost";
        case Kingmaker.UnitLogic.Abilities.AbilityTag.Trap:
            return "trap — placed device";
        default:
            return "";
    }
}
```

**Step 2: Build to verify**

Run the build command. If `AbilityTag` enum values are different in the game version, the build will fail with `CS0117` — note which value is missing, comment out that case, and rebuild. Document any removed cases in the commit message.

**Step 3: Commit**

```bash
git add Planning/LLM/AbilityEffectExtractor.cs
git commit -m "feat: BlueprintAbility fallback for AbilityEffectExtractor

For abilities not in AbilityDatabase, extracts label from game API
properties (AbilityTag, Range, ActionType). Used as 2nd-tier fallback
in the lookup cascade.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: Create `AbilityEffectCache.cs` (skeleton + Initialize stub)

Skeleton with the public API. Actual cache build comes in Task 5.

**Files:**
- Create: `Planning/LLM/AbilityEffectCache.cs`

**Step 1: Create the file**

```csharp
// Planning/LLM/AbilityEffectCache.cs
// ★ Skill Effect Awareness: GUID → effect label 빠른 조회 캐시
// 게임 로드 시 1회 빌드, tactical_skill_cache.json에 저장.
// CompactBattlefieldEncoder가 매 LLM 호출마다 O(1) 조회로 사용.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using CompanionAI_v3.Data;

namespace CompanionAI_v3.Planning.LLM
{
    /// <summary>
    /// GUID → 효과 라벨 매핑 캐시.
    /// 게임 로드 시 1회 빌드되며 디스크에 저장됨 (재시작 시 즉시 로드).
    /// 캐시 미스 시 빈 문자열 반환 (안전 폴백).
    /// </summary>
    public static class AbilityEffectCache
    {
        private const string FILENAME = "tactical_skill_cache.json";
        private const int SCHEMA_VERSION = 1;

        private static readonly Dictionary<string, string> _labels = new Dictionary<string, string>();
        private static bool _initialized;
        private static string _filePath;

        /// <summary>저장된 라벨 수 (진단용)</summary>
        public static int LabelCount => _labels.Count;

        /// <summary>초기화 완료 여부</summary>
        public static bool IsReady => _initialized;

        /// <summary>
        /// 게임 로드 시 1회 호출. 비동기 코루틴.
        /// 1. tactical_skill_cache.json 존재 시 → 즉시 로드
        /// 2. 없으면 → AbilityDatabase 순회 + 저장
        /// </summary>
        public static IEnumerator Initialize(string modPath)
        {
            if (_initialized) yield break;

            _filePath = Path.Combine(modPath, FILENAME);

            // 1. 디스크 캐시 로드 시도
            if (TryLoadFromDisk())
            {
                _initialized = true;
                Main.Log($"[AbilityEffectCache] Loaded {_labels.Count} labels from disk");
                yield break;
            }

            // 2. 캐시 빌드 (Task 5에서 구현)
            Main.LogDebug("[AbilityEffectCache] No cache file — building from AbilityDatabase");
            BuildFromDatabase();

            // 3. 디스크 저장
            SaveToDisk();

            _initialized = true;
            Main.Log($"[AbilityEffectCache] Built {_labels.Count} labels");
            yield return null;
        }

        /// <summary>
        /// O(1) 조회. 캐시 미스 시 빈 문자열 반환.
        /// </summary>
        public static string GetLabel(string abilityGuid)
        {
            if (string.IsNullOrEmpty(abilityGuid)) return "";
            return _labels.TryGetValue(abilityGuid, out string label) ? label : "";
        }

        /// <summary>전체 캐시 클리어 (디버그용)</summary>
        public static void Clear()
        {
            _labels.Clear();
            _initialized = false;
        }

        // ═══════════════════════════════════════════════════════════
        // Internal: build / load / save
        // ═══════════════════════════════════════════════════════════

        private static void BuildFromDatabase()
        {
            // Task 5에서 구현
        }

        private static bool TryLoadFromDisk()
        {
            if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath))
                return false;

            try
            {
                string json = File.ReadAllText(_filePath);
                var wrapper = JsonConvert.DeserializeObject<CacheFile>(json);

                if (wrapper == null || wrapper.SchemaVersion != SCHEMA_VERSION)
                {
                    Main.LogDebug("[AbilityEffectCache] Cache schema mismatch — rebuilding");
                    return false;
                }

                if (wrapper.Labels != null)
                {
                    _labels.Clear();
                    foreach (var kvp in wrapper.Labels)
                        _labels[kvp.Key] = kvp.Value;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[AbilityEffectCache] Load failed: {ex.Message}");
            }

            return false;
        }

        private static void SaveToDisk()
        {
            if (string.IsNullOrEmpty(_filePath)) return;

            try
            {
                var wrapper = new CacheFile
                {
                    SchemaVersion = SCHEMA_VERSION,
                    Labels = new Dictionary<string, string>(_labels)
                };
                string json = JsonConvert.SerializeObject(wrapper, Formatting.Indented);
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[AbilityEffectCache] Save failed: {ex.Message}");
            }
        }

        /// <summary>JSON 직렬화용 wrapper</summary>
        private class CacheFile
        {
            [JsonProperty("schemaVersion")]
            public int SchemaVersion { get; set; }

            [JsonProperty("labels")]
            public Dictionary<string, string> Labels { get; set; }
        }
    }
}
```

**Step 2: Build to verify**

Run the build command. Expect success.

**Step 3: Commit**

```bash
git add Planning/LLM/AbilityEffectCache.cs
git commit -m "feat: AbilityEffectCache skeleton with disk persistence

GUID -> effect label O(1) lookup. Loads from
tactical_skill_cache.json on startup, falls back to building from
AbilityDatabase (stub for now). Schema versioned for future upgrades.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: Implement `BuildFromDatabase()` in `AbilityEffectCache`

Fill in the build logic that iterates `AbilityDatabase.GetAllInfos()` (added in Task 1).

**Files:**
- Modify: `Planning/LLM/AbilityEffectCache.cs` (replace stub `BuildFromDatabase` method)

**Step 1: Replace the stub**

Find this in `Planning/LLM/AbilityEffectCache.cs`:
```csharp
private static void BuildFromDatabase()
{
    // Task 5에서 구현
}
```

Replace with:
```csharp
private static void BuildFromDatabase()
{
    int extracted = 0;
    int skipped = 0;

    foreach (var info in AbilityDatabase.GetAllInfos())
    {
        if (info == null || string.IsNullOrEmpty(info.Guid)) { skipped++; continue; }

        string label = AbilityEffectExtractor.ExtractFromInfo(info);
        if (!string.IsNullOrEmpty(label))
        {
            _labels[info.Guid] = label;
            extracted++;
        }
        else
        {
            skipped++;
        }
    }

    Main.LogDebug($"[AbilityEffectCache] BuildFromDatabase: extracted={extracted}, skipped={skipped}");
}
```

**Step 2: Build to verify**

Run the build command. Expect success.

**Step 3: Commit**

```bash
git add Planning/LLM/AbilityEffectCache.cs
git commit -m "feat: implement BuildFromDatabase for AbilityEffectCache

Iterates AbilityDatabase.GetAllInfos(), runs each through
AbilityEffectExtractor.ExtractFromInfo, populates the lookup
dictionary. Logs extraction stats.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: Wire `AbilityEffectCache.Initialize()` into `Main.Load()`

Trigger the cache build when the mod loads.

**Files:**
- Modify: `Main.cs` (add 1 line near existing `TacticalMemory.Initialize()`)

**Step 1: Find the existing initialization**

Open `Main.cs`. Search for the line:
```csharp
Planning.LLM.TacticalMemory.Initialize(modEntry.Path);
```

**Step 2: Add the new initialization right after it**

Replace:
```csharp
// ★ Tactical Memory 초기화 (전투 간 전술 기억)
Planning.LLM.TacticalMemory.Initialize(modEntry.Path);
```

With:
```csharp
// ★ Tactical Memory 초기화 (전투 간 전술 기억)
Planning.LLM.TacticalMemory.Initialize(modEntry.Path);

// ★ Skill Effect Cache 초기화 (LLM 스킬 효과 인식)
MachineSpirit.CoroutineRunner.Start(
    Planning.LLM.AbilityEffectCache.Initialize(modEntry.Path));
```

**Step 3: Build to verify**

Run the build command. Expect success.

If the build fails with `CoroutineRunner` not found, check the `using` directives in `Main.cs` and add `using CompanionAI_v3.MachineSpirit;` if missing, then use `CoroutineRunner.Start(...)` without the `MachineSpirit.` prefix.

**Step 4: Commit**

```bash
git add Main.cs
git commit -m "feat: initialize AbilityEffectCache on mod load

Triggers async cache build via CoroutineRunner. First load takes
1-2 seconds; subsequent loads restore from disk in ~10ms.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: Modify `CompactBattlefieldEncoder.AppendSkillsLine()` to inject effect labels

Rewrite the SK line to multi-line format with `[effect]` tags.

**Files:**
- Modify: `Planning/LLM/CompactBattlefieldEncoder.cs` lines 320-392

**Step 1: Read the current method**

Open `Planning/LLM/CompactBattlefieldEncoder.cs` and locate `AppendSkillsLine` (around line 326).

**Step 2: Replace the entire method**

Replace lines 326-392 with:

```csharp
private static void AppendSkillsLine(Situation situation)
{
    _sb.Append("SK:\n");

    AppendSkillCategory(situation.AvailableAttacks, "Atk", 3);
    AppendSkillCategory(situation.AvailableAoEAttacks, "AoE", 2);
    AppendSkillCategory(situation.AvailableBuffs, "Buff", 3);
    AppendSkillCategory(situation.AvailableHeals, "Heal", 2);
    AppendSkillCategory(situation.AvailableDebuffs, "Dbf", 2);
}

/// <summary>
/// 카테고리별 스킬 출력. 각 스킬에 효과 라벨 부착.
/// 형식:
///   Atk:
///   - 단발 사격 [single shot]
///   - 점사 사격 [burst, +offense]
/// </summary>
private static void AppendSkillCategory(
    System.Collections.Generic.List<Kingmaker.UnitLogic.Abilities.AbilityData> abilities,
    string label, int maxItems)
{
    if (abilities == null || abilities.Count == 0) return;

    _sb.Append(label).Append(":\n");

    int count = System.Math.Min(abilities.Count, maxItems);
    for (int i = 0; i < count; i++)
    {
        var ab = abilities[i];
        if (ab == null) continue;

        _sb.Append("- ");
        _sb.Append(ab.Name ?? "?");

        // 효과 라벨 조회 — 캐시 히트 시 [...] 추가
        string guid = AbilityDatabase.GetGuid(ab);
        string effectLabel = AbilityEffectCache.GetLabel(guid);
        if (!string.IsNullOrEmpty(effectLabel))
        {
            _sb.Append(" [").Append(effectLabel).Append(']');
        }

        _sb.Append('\n');
    }
}
```

**Step 3: Verify the `using` directives at the top of the file**

The file already uses `CompanionAI_v3.Data` for other purposes, so `AbilityDatabase` should resolve. Check the top of the file (around line 1-15) for `using CompanionAI_v3.Data;`. If missing, add it.

**Step 4: Build to verify**

Run the build command. Expect success.

If the build fails because `AvailableAttacks` etc. are not `List<AbilityData>`, look at `Analysis/Situation.cs` line 243 for the actual type and update the parameter type.

**Step 5: Commit**

```bash
git add Planning/LLM/CompactBattlefieldEncoder.cs
git commit -m "feat: inject effect labels into SK line of battlefield encoder

Multi-line format with [effect] tags after each skill name.
Empty labels (cache miss) gracefully omit the brackets — current
behavior preserved for unrecognized skills.

Token budget: ~30 -> ~80 tokens for SK section (40% total increase).

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

## Task 8: Manual smoke test (no code changes)

Verify everything works in-game.

**Step 1: Launch the game**

Start Rogue Trader. Wait for the main menu. The mod should load automatically.

**Step 2: Check the log for cache build**

Open `C:\Users\veria\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\GameLogFull.txt` (use Read tool with offset to the end).

Expected log lines (within first ~30 seconds of game launch):
```
[AbilityEffectCache] No cache file — building from AbilityDatabase
[AbilityEffectCache] BuildFromDatabase: extracted=N, skipped=M
[AbilityEffectCache] Built N labels
```

Where N should be > 100 (hundreds of curated abilities in DB).

**Step 3: Verify cache file exists**

Check that `<ModPath>/tactical_skill_cache.json` exists. The exact ModPath is shown in mod manager but typically:
```
%LOCALAPPDATA%\Low\Owlcat Games\Warhammer 40000 Rogue Trader\UnityModManager\CompanionAI_v3\tactical_skill_cache.json
```

It should contain JSON with `"schemaVersion": 1` and a `"labels"` dictionary with at least 100 entries.

**Step 4: Start a combat encounter**

Load a save with active combat or trigger one. Wait for an LLM-enabled character's turn.

**Step 5: Check the combat log for SK line**

In `GameLogFull.txt`, search for the most recent `[LLMScorer]` or `[LLM Judge]` request. Look for `SK:` in the encoded battlefield state. It should be multi-line:

```
SK:
Atk:
- 단발 사격 [...]
Buff:
- Run and Gun [bonus action — use after attacking]
...
```

**Success criteria:** At least one skill has a `[...]` effect label. Run and Gun (if available on a character) shows `bonus action — use after attacking`.

**Step 6: Restart the game once and verify cache is loaded (not rebuilt)**

Quit and relaunch. The log should now show:
```
[AbilityEffectCache] Loaded N labels from disk
```
(No "Built" or "BuildFromDatabase" lines.)

**No commit for this task** — verification only. If anything fails, debug and fix in a follow-up task.

---

## Task 9: Bump version + build release zip + commit

**Files:**
- Modify: `Info.json` (version bump)

**Step 1: Read current version**

Read `Info.json`. Note the current `"Version"` value (likely `"3.88.0"`).

**Step 2: Bump version**

Edit `Info.json`, change `"Version"` to `"3.90.0"`.

**Step 3: Build to verify**

Run the build command one final time. Expect success.

**Step 4: Create release zip**

```bash
powershell -Command '$dllPath = $env:LOCALAPPDATA + "Low\Owlcat Games\Warhammer 40000 Rogue Trader\UnityModManager\CompanionAI_v3\CompanionAI_v3.dll"; $infoPath = "C:\Users\veria\Downloads\CompanionAI_v3-master - v3.5.7\Info.json"; $zipPath = "C:\Users\veria\Downloads\CompanionAI_v3_3.90.0.zip"; if (Test-Path $zipPath) { Remove-Item $zipPath }; Compress-Archive -Path $dllPath, $infoPath -DestinationPath $zipPath -Force; $size = [math]::Round((Get-Item $zipPath).Length / 1KB); Write-Output "Created: $zipPath ($size KB)"'
```

Expected: `Created: ... (~544 KB)`.

**Step 5: Commit version bump**

```bash
git add Info.json
git commit -m "chore: bump version to 3.90.0 — skill effect awareness

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

**Step 6: Push to remote (ASK USER FIRST)**

Do NOT push without explicit user permission. Instead, ask:

> All tasks complete. Ready to push 9 commits to origin/master and create v3.90.0 GitHub release. Proceed?

If user confirms, then run:
```bash
cd "c:\Users\veria\Downloads\CompanionAI_v3-master - v3.5.7"
git push origin master
gh release create "v3.90.0" "C:\Users\veria\Downloads\CompanionAI_v3_3.90.0.zip" --title "v3.90.0" --notes "..."
```

(Notes content can be drafted based on the design doc.)

---

## Task Summary

| # | Task | Files | Risk |
|---|------|-------|------|
| 1 | `GetAllInfos()` enumerator | `Data/AbilityDatabase.cs` | Low |
| 2 | `AbilityEffectExtractor` skeleton + AbilityInfo path | NEW | Low |
| 3 | Blueprint fallback in extractor | Modify NEW file | Medium (game API dependency) |
| 4 | `AbilityEffectCache` skeleton + I/O | NEW | Low |
| 5 | Implement `BuildFromDatabase()` | Modify NEW file | Low |
| 6 | Wire `Initialize()` into `Main.Load()` | `Main.cs` | Low |
| 7 | Encoder integration (SK line rewrite) | `CompactBattlefieldEncoder.cs` | Medium (output format change) |
| 8 | Manual smoke test | None | — |
| 9 | Version bump + release | `Info.json` + zip | Low |

**Critical path:** 1 → 2 → 4 → 5 → 6 → 7 → 8 → 9 (Task 3 can be done in parallel with 4-5 if desired)

**Dependencies between tasks:**
- Task 5 depends on Tasks 1, 2, 4
- Task 6 depends on Task 4
- Task 7 depends on Task 4
- Task 8 depends on Tasks 6, 7

**Estimated total time:** 30-60 minutes of focused work.
