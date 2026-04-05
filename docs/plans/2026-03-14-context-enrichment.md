# Machine Spirit 컨텍스트 강화 Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Machine Spirit이 전투/탐험 상태를 더 풍부하게 인지하고, 프롬프트 압축으로 소형 모델에서도 안정적으로 동작하게 한다.

**Architecture:** ContextBuilder.cs의 Build메서드들을 확장하여 버프/장비/전투흐름/스탯 데이터를 상황별 선택적으로 주입. GameEventCollector에 킬 트래커 추가. MachineSpirit.cs에서 클라우드 요약 활성화 + 동적 대화 윈도우 크기 적용.

**Tech Stack:** C# / .NET 4.8.1 / Unity Mod Manager / Harmony / Kingmaker API

---

## Task 1: GameEventCollector — 킬 트래커 추가

**Files:**
- Modify: `MachineSpirit/GameEventCollector.cs`

### Step 1: 킬 트래커 데이터 구조 + API 추가

`GameEventCollector` 클래스에 킬 카운트 딕셔너리와 접근 메서드를 추가한다.

`GameEventCollector.cs`의 `_events` 필드 아래 (line ~70)에 추가:

```csharp
// ★ v3.64.0: Kill tracker per combat encounter
private static readonly Dictionary<string, int> _killCounts = new Dictionary<string, int>();

public static IReadOnlyDictionary<string, int> KillCounts => _killCounts;
```

`using System.Collections.Generic;` 는 이미 있으므로 추가 불필요.

### Step 2: HandleUnitDeath에서 킬 카운트 증가

`CombatEventSubscriber.HandleUnitDeath()` (line ~132)를 수정하여, 적이 죽었을 때 마지막으로 데미지를 준 아군의 킬 카운트를 증가시킨다.

기존 코드:
```csharp
public void HandleUnitDeath(AbstractUnitEntity unit)
{
    if (unit == null) return;
    string name = unit.CharacterName ?? "Unknown";
    bool isEnemy = !unit.IsPlayerFaction;
    string desc = isEnemy ? $"{name} was destroyed" : $"{name} has fallen";
    AddEvent(GameEventType.UnitDeath, null, desc);
}
```

변경 후:
```csharp
public void HandleUnitDeath(AbstractUnitEntity unit)
{
    if (unit == null) return;
    string name = unit.CharacterName ?? "Unknown";
    bool isEnemy = !unit.IsPlayerFaction;
    string desc = isEnemy ? $"{name} was destroyed" : $"{name} has fallen";
    AddEvent(GameEventType.UnitDeath, null, desc);

    // ★ v3.64.0: Track kills by party members
    if (isEnemy)
    {
        // Find last damage event targeting this unit from a party member
        for (int i = _events.Count - 1; i >= Math.Max(0, _events.Count - 10); i--)
        {
            var evt = _events[i];
            if (evt.Type == GameEventType.DamageDealt && evt.Text.Contains(name))
            {
                string killer = evt.Speaker;
                if (!string.IsNullOrEmpty(killer) && killer != "Unknown")
                {
                    _killCounts.TryGetValue(killer, out int count);
                    _killCounts[killer] = count + 1;
                }
                break;
            }
        }
    }
}
```

### Step 3: 전투 시작 시 킬 카운트 리셋

`HandleTurnBasedModeSwitched(true)` (line ~141)에서 전투 시작 시 킬 카운트를 초기화한다.

기존:
```csharp
if (isTurnBased)
{
    _combatRound = 0;
    AddEvent(GameEventType.CombatStart, null, "Combat initiated");
}
```

변경:
```csharp
if (isTurnBased)
{
    _combatRound = 0;
    _killCounts.Clear();
    AddEvent(GameEventType.CombatStart, null, "Combat initiated");
}
```

### Step 4: using 추가

파일 상단에 `using System;` 이 없으면 추가 (Math.Max 사용을 위해). 이미 있으면 생략.

### Step 5: 빌드 확인

```powershell
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo
```

Expected: 빌드 성공 (0 errors)

### Step 6: 커밋

```bash
git add MachineSpirit/GameEventCollector.cs
git commit -m "feat: kill tracker in GameEventCollector for Machine Spirit context"
```

---

## Task 2: ContextBuilder — 전투 컨텍스트 강화

**Files:**
- Modify: `MachineSpirit/ContextBuilder.cs`

### Step 1: BuildPartyContext() 확장 — 장비, 버프 추가

`BuildPartyContext()` (line ~519) 내부에서, 각 유닛 항목에 장비와 버프 정보를 추가한다.

현재 각 유닛 출력:
```csharp
sb.AppendLine($"- {name}: {role}{hpStatus}{(inCombat ? " [In Combat]" : "")}");
```

이 라인을 다음으로 교체:
```csharp
// ★ v3.64.0: Equipment + buffs
string equipment = GetUnitEquipment(unit);
string buffs = GetUnitBuffs(unit);

sb.Append($"- {name}: {role}{hpStatus}{(inCombat ? " [In Combat]" : "")}");
if (!string.IsNullOrEmpty(equipment))
    sb.Append($" | {equipment}");
sb.AppendLine();
if (!string.IsNullOrEmpty(buffs))
    sb.AppendLine($"  Buffs: {buffs}");
```

### Step 2: 헬퍼 메서드 추가 — GetUnitEquipment, GetUnitBuffs

`ContextBuilder` 클래스 내부, `BuildAreaContext()` 메서드 위에 추가:

```csharp
/// <summary>
/// ★ v3.64.0: Get equipped weapon names for context.
/// </summary>
private static string GetUnitEquipment(BaseUnitEntity unit)
{
    try
    {
        var primary = unit.Body?.PrimaryHand?.MaybeWeapon;
        var secondary = unit.Body?.SecondaryHand?.MaybeWeapon;
        if (primary == null && secondary == null) return null;

        string pName = primary?.Blueprint?.Name;
        string sName = secondary?.Blueprint?.Name;

        if (!string.IsNullOrEmpty(pName) && !string.IsNullOrEmpty(sName) && pName != sName)
            return $"wielding {pName} + {sName}";
        if (!string.IsNullOrEmpty(pName))
            return $"wielding {pName}";
        if (!string.IsNullOrEmpty(sName))
            return $"wielding {sName}";
        return null;
    }
    catch { return null; }
}

/// <summary>
/// ★ v3.64.0: Get active buff names (max 4 to save tokens).
/// </summary>
private static string GetUnitBuffs(BaseUnitEntity unit)
{
    try
    {
        var buffs = unit.Buffs?.Enumerable;
        if (buffs == null) return null;

        var names = new List<string>();
        foreach (var buff in buffs)
        {
            if (buff == null || buff.Blueprint == null) continue;
            // Skip hidden/internal buffs
            try { if (buff.Blueprint.IsHiddenInUI) continue; } catch { }
            string bName = buff.Blueprint.Name;
            if (string.IsNullOrEmpty(bName)) continue;
            if (bName.StartsWith("Feature_") || bName.StartsWith("Etude")) continue; // Internal names
            names.Add(bName);
            if (names.Count >= 4) break; // Token budget
        }
        return names.Count > 0 ? string.Join(", ", names) : null;
    }
    catch { return null; }
}
```

**필요한 using 추가** (파일 상단):
- `using Kingmaker.EntitySystem.Stats.Base;` (이미 없으면 추가)

### Step 3: BuildCombatContext() 확장 — 라운드, 모멘텀, 교전 상태, 킬 로그

`BuildCombatContext()` (line ~587)를 완전히 재작성한다.

기존 메서드를 아래로 교체:
```csharp
private static string BuildCombatContext()
{
    try
    {
        var allUnits = Game.Instance?.State?.AllBaseAwakeUnits;
        if (allUnits == null) return null;

        var sb = new StringBuilder();
        sb.AppendLine("[HOSTILE FORCES — Active Combat]");

        // ★ v3.64.0: Combat round from GameEventCollector
        int round = 0;
        foreach (var evt in GameEventCollector.RecentEvents)
        {
            if (evt.Type == GameEventType.RoundStart)
            {
                // Parse "Combat round N" → N
                var parts = evt.Text.Split(' ');
                if (parts.Length >= 3 && int.TryParse(parts[parts.Length - 1], out int r))
                    round = r;
            }
        }
        if (round > 0)
            sb.AppendLine($"[ROUND {round}]");

        int enemyCount = 0;
        int listed = 0;
        float partyHpTotal = 0f, partyHpMax = 0f;
        float enemyHpTotal = 0f, enemyHpMax = 0f;

        // ★ v3.64.0: Track engagement status for party members
        var engagedParty = new List<string>();

        foreach (var unit in allUnits)
        {
            if (unit == null || unit.IsDead) continue;

            bool inCombat = false;
            try { inCombat = unit.IsInCombat; } catch { }
            if (!inCombat) continue;

            // HP tracking for momentum
            try
            {
                float hp = unit.Health.HitPointsLeft;
                float maxHp = Math.Max(1, unit.Health.MaxHitPoints);
                if (unit.IsPlayerFaction)
                {
                    partyHpTotal += hp;
                    partyHpMax += maxHp;

                    // Check engagement
                    bool engaged = false;
                    try { engaged = unit.CombatState?.IsEngaged ?? false; } catch { }
                    if (engaged)
                    {
                        int threatCount = 0;
                        try
                        {
                            var engagedBy = unit.CombatState?.EngagedBy;
                            if (engagedBy != null) threatCount = engagedBy.Count;
                        }
                        catch { }
                        string charName = unit.CharacterName ?? "Unknown";
                        engagedParty.Add(threatCount > 0
                            ? $"{charName} ENGAGED (threatened by {threatCount})"
                            : $"{charName} ENGAGED");
                    }
                }
                else
                {
                    enemyHpTotal += hp;
                    enemyHpMax += maxHp;
                }
            }
            catch { }

            // Enemy listing
            if (!unit.IsPlayerFaction)
            {
                enemyCount++;
                if (listed < 10)
                {
                    string name = unit.CharacterName ?? "Unknown";
                    string hpStatus = "";
                    try
                    {
                        float hpPct = unit.Health.HitPointsLeft / (float)Math.Max(1, unit.Health.MaxHitPoints);
                        if (hpPct < 0.25f) hpStatus = " [CRITICAL]";
                        else if (hpPct < 0.5f) hpStatus = " [Wounded]";
                    }
                    catch { }

                    sb.AppendLine($"- {name}{hpStatus}");
                    listed++;
                }
            }
        }

        if (enemyCount == 0) return null;

        if (listed < enemyCount)
            sb.AppendLine($"  ...and {enemyCount - listed} more");
        sb.AppendLine($"Total hostiles: {enemyCount}");

        // ★ v3.64.0: Battle momentum
        if (partyHpMax > 0 && enemyHpMax > 0)
        {
            float partyPct = partyHpTotal / partyHpMax;
            float enemyPct = enemyHpTotal / enemyHpMax;
            string momentum;
            if (partyPct > 0.7f && enemyPct < 0.4f)
                momentum = "Dominant";
            else if (partyPct > enemyPct + 0.15f)
                momentum = "Favorable";
            else if (enemyPct > partyPct + 0.15f)
                momentum = "Unfavorable";
            else
                momentum = "Contested";
            sb.AppendLine($"[BATTLE MOMENTUM] {momentum} — Party {partyPct:P0} / Hostiles {enemyPct:P0}");
        }

        // ★ v3.64.0: Engagement alerts
        foreach (var e in engagedParty)
            sb.AppendLine($"⚠ {e}");

        // ★ v3.64.0: Kill log
        var kills = GameEventCollector.KillCounts;
        if (kills.Count > 0)
        {
            sb.Append("[KILL LOG] ");
            bool first = true;
            foreach (var kv in kills)
            {
                if (!first) sb.Append(", ");
                sb.Append($"{kv.Key}: {kv.Value}");
                first = false;
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
    catch
    {
        return null;
    }
}
```

### Step 4: 빌드 확인

```powershell
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo
```

### Step 5: 커밋

```bash
git add MachineSpirit/ContextBuilder.cs
git commit -m "feat: enriched combat context — buffs, equipment, momentum, engagement, kill log"
```

---

## Task 3: ContextBuilder — 탐험 컨텍스트 강화

**Files:**
- Modify: `MachineSpirit/ContextBuilder.cs`

### Step 1: BuildPartyContext()에 스탯 요약 추가 (비전투 시)

`BuildPartyContext()` 내부에서 전투 중이 아닌 유닛의 핵심 스탯을 표시한다.

현재 `sb.Append($"- {name}: {role}...` 부분을 수정한다.
전투 중이 아닐 때만 스탯을 표시하도록 조건을 추가:

```csharp
// ★ v3.64.0: Stats summary (exploration only, saves tokens in combat)
if (!inCombat && !unit.IsPet)
{
    string stats = GetUnitStats(unit);
    if (!string.IsNullOrEmpty(stats))
        sb.AppendLine($"  Stats: {stats}");
}
```

이 코드는 Task 2에서 추가한 buffs 출력 라인 아래에 배치한다.

### Step 2: GetUnitStats 헬퍼 추가

`GetUnitBuffs()` 메서드 아래에 추가:

```csharp
/// <summary>
/// ★ v3.64.0: Core Warhammer stats for exploration context.
/// </summary>
private static string GetUnitStats(BaseUnitEntity unit)
{
    try
    {
        int bs = CombatAPI.GetStatValue(unit, StatType.WarhammerBallisticSkill);
        int ws = CombatAPI.GetStatValue(unit, StatType.WarhammerWeaponSkill);
        int t = CombatAPI.GetStatValue(unit, StatType.WarhammerToughness);
        if (bs == 0 && ws == 0 && t == 0) return null;
        return $"BS:{bs} WS:{ws} T:{t}";
    }
    catch { return null; }
}
```

### Step 3: BuildPartyContext() — 전체 건강 요약 (전원 건강하면 축약)

`BuildPartyContext()` 최상단, `sb.AppendLine("[CREW ROSTER — Current Party]")` 아래에 건강 요약 로직을 추가:

```csharp
// ★ v3.64.0: Party health summary
float totalHpPct = 0f;
int memberCount = 0;
bool anyWounded = false;
foreach (var u in party)
{
    if (u == null || u.IsPet) continue;
    memberCount++;
    try
    {
        float pct = u.Health.HitPointsLeft / (float)Math.Max(1, u.Health.MaxHitPoints);
        totalHpPct += pct;
        if (pct < 0.9f) anyWounded = true;
    }
    catch { }
}
if (memberCount > 0 && !anyWounded)
{
    sb.AppendLine($"All crew operational (avg {totalHpPct / memberCount:P0} HP)");
}
```

이 경우, 전원 건강하면 개별 유닛에서 HP 상태 표시를 생략하는 것이 아니라 (기존 [CRITICAL]/[Wounded] 태그는 조건부라 이미 표시 안 됨), 추가 요약 한 줄만 붙인다.

### Step 4: 빌드 확인

```powershell
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo
```

### Step 5: 커밋

```bash
git add MachineSpirit/ContextBuilder.cs
git commit -m "feat: enriched exploration context — stats, health summary"
```

---

## Task 4: 프롬프트 압축 개선

**Files:**
- Modify: `MachineSpirit/MachineSpirit.cs`
- Modify: `MachineSpirit/ContextBuilder.cs`
- Modify: `MachineSpirit/LLMClient.cs`

### Step 1: 클라우드 프로바이더도 요약 허용

`MachineSpirit.cs`의 `MaybeSummarize()` (line ~485):

기존:
```csharp
if (Config.Provider != ApiProvider.Ollama) return; // Only for local (free) models
```

변경:
```csharp
// ★ v3.64.0: Enable summarization for all providers (context quality > cost savings)
// Cloud providers benefit from summarization too — prevents context overflow
```

즉, 이 `return` 라인을 삭제한다.

### Step 2: 대화 윈도우 크기를 모델 크기에 따라 동적 조절

`ContextBuilder.cs`의 `Build()` 메서드 (line ~748)에서:

기존:
```csharp
// Chat history (last 10 turns = 20 messages)
int histStart = chatHistory.Count > 20 ? chatHistory.Count - 20 : 0;
```

변경:
```csharp
// ★ v3.64.0: Dynamic history window based on model context size
int maxHistory = GetHistoryWindow(config);
int histStart = chatHistory.Count > maxHistory ? chatHistory.Count - maxHistory : 0;
```

### Step 3: GetHistoryWindow 헬퍼 추가

`ContextBuilder` 클래스에 추가 (IsGemmaModel 근처):

```csharp
/// <summary>
/// ★ v3.64.0: Dynamic history window — smaller models get fewer messages to stay within context.
/// </summary>
private static int GetHistoryWindow(MachineSpiritConfig config)
{
    if (config == null) return 20;

    // Cloud providers: generous window (large context)
    if (config.Provider != ApiProvider.Ollama) return 20;

    // Ollama: model size determines window
    string model = config.Model?.ToLowerInvariant() ?? "";
    if (model.Contains("1b") || model.Contains("3b") || model.Contains("4b"))
        return 12; // Small models: 6 turns
    if (model.Contains("27b") || model.Contains("70b"))
        return 20; // Large models: 10 turns
    return 16; // Mid-range (7B-12B): 8 turns
}
```

### Step 4: 센서 로그도 동적 크기

`ContextBuilder.cs`의 `BuildSystemContent()` (line ~712) 센서 로그 부분:

기존:
```csharp
int start = events.Count > 20 ? events.Count - 20 : 0;
```

변경:
```csharp
// ★ v3.64.0: Dynamic sensor log size — fewer events for small models
int maxEvents = (Config?.Provider == ApiProvider.Ollama && IsSmallModel(Config)) ? 10 : 20;
int start = events.Count > maxEvents ? events.Count - maxEvents : 0;
```

**주의**: `BuildSystemContent()`은 현재 `config` 파라미터를 받지 않는다. 이를 해결하기 위해:

1. `BuildSystemContent(string conversationSummary)` → `BuildSystemContent(string conversationSummary, MachineSpiritConfig config = null)` 로 시그니처 변경
2. `Build()` 메서드 내 호출도 수정: `BuildSystemContent(conversationSummary)` → `BuildSystemContent(conversationSummary, config)`

### Step 5: IsSmallModel 헬퍼 추가

```csharp
private static bool IsSmallModel(MachineSpiritConfig config)
{
    if (config?.Model == null) return false;
    string m = config.Model.ToLowerInvariant();
    return m.Contains("1b") || m.Contains("3b") || m.Contains("4b");
}
```

### Step 6: MaybeSummarize의 SUMMARY_WINDOW 동적 조정

`MachineSpirit.cs`의 `SummarizeCoroutine()` (line ~499):

기존:
```csharp
int endIdx = _chatHistory.Count - 20;
```

이 20도 동적으로 변경해야 한다. 하지만 `ContextBuilder.GetHistoryWindow()`는 내부 메서드이므로, 여기서는 간단하게:

```csharp
// ★ v3.64.0: Match summarization window to history window
int historyWindow = Config.Provider == ApiProvider.Ollama ? 12 : 20;
string model = Config.Model?.ToLowerInvariant() ?? "";
if (Config.Provider == ApiProvider.Ollama)
{
    if (model.Contains("27b") || model.Contains("70b")) historyWindow = 20;
    else if (!model.Contains("1b") && !model.Contains("3b") && !model.Contains("4b")) historyWindow = 16;
}
int endIdx = _chatHistory.Count - historyWindow;
```

### Step 7: 빌드 확인

```powershell
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo
```

### Step 8: 커밋

```bash
git add MachineSpirit/MachineSpirit.cs MachineSpirit/ContextBuilder.cs
git commit -m "feat: prompt compression — cloud summarization, dynamic window sizing"
```

---

## Task 5: 버전 업데이트 + 최종 빌드

**Files:**
- Modify: `Info.json`

### Step 1: 버전 업데이트

`Info.json`의 `"Version"` 필드를 현재 값에서 `"3.64.0"`으로 변경한다.

### Step 2: 최종 빌드

```powershell
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo
```

Expected: 빌드 성공 (0 errors, 0 warnings 이상적)

### Step 3: 커밋

```bash
git add Info.json
git commit -m "chore: bump version to 3.64.0 — context enrichment + prompt compression"
```

---

## 검증 체크리스트

- [ ] 빌드 성공 (0 errors)
- [ ] 킬 트래커: 전투 시작 시 리셋, 유닛 사망 시 킬 카운트 증가
- [ ] 전투 컨텍스트: 라운드, 모멘텀, 교전 상태, 킬 로그 표시
- [ ] 탐험 컨텍스트: 스탯, 장비, 버프, 건강 요약 표시
- [ ] 소형 모델(4b): 히스토리 12개, 센서 로그 10개로 제한
- [ ] 클라우드 프로바이더: 요약 활성화
- [ ] 기존 기능 회귀 없음 (채팅, 아이들, 자발적 이벤트)
