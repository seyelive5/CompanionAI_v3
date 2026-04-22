# Phase B.3 — LocalizedString Exception Isolation Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 능력 순회 루프 내부에서 `ability.Name`(LocalizedString 경유) 예외 → 루프 전체 abort 버그를 per-ability try/catch + 안전 헬퍼로 격리.

**원칙 확인:**
- 매칭/식별: GUID 기반 유지 (190건 grep 결과 → 능력 매칭에 Name 사용 0건, 원칙 준수됨)
- 로그 표시: `ability.Name` 사용 불가피. 하지만 **루프 내부**에서 예외 시 전체 abort → 격리 필요

**Architecture:**
- `BlueprintCache.cs:144-153`의 `try { ability.Name } catch { bp.name }` 패턴을 공용 헬퍼로 추출
- Risk-ranked 순회 루프에만 적용 (전체 190건 치환 금지 — 과도 변경 risk)
- `FindAnyAttackAbility`(v3.111.7가 `TryFindDirectionalAoEPrimaryAttack`에서 시행한 패턴) 확장

**Tech Stack:** C# .NET Framework 4.8.1, MSBuild 18

**Risk ranking (루프 + rawAbilities/allAbilities 직접 스캔):**

| Priority | 위치 | 영향 |
|---|---|---|
| 🔴 High | `CombatAPI.GetAvailableAbilities:1835` | 예외 시 유닛 전체 능력 빈 리스트 → 모든 Plan 실패 |
| 🔴 High | `CombatAPI.FindAnyAttackAbility:1895` (main) | 공격 능력 선택 실패 → 턴 낭비 |
| 🔴 High | `CombatAPI.FindAnyAttackAbility:1968` (psyker fallback) | 사이커 전용, 원거리 사이커 공격 실패 |
| 🔴 High | `CombatAPI.FindAnyAttackAbility:1997` (offensive fallback) | 무기 없는 유닛 fallback 실패 |
| 🟡 Medium | `SituationAnalyzer.AnalyzeAbilities:779` | GetAvailableAbilities가 먼저 필터하므로 2차 방어 |
| ✅ Done | `CombatAPI.TryFindDirectionalAoEPrimaryAttack:1662` | v3.111.7에서 처리됨 |

**범위 외 (이번 작업 안 함):**
- ActionExecutor, FamiliarAbilities, AttackPlanner 등 루프 밖 로그 — 개별 예외는 해당 호출만 실패하고 시스템 영향 없음
- Plan 내부 필터된 리스트 순회(AvailableAttacks 등) — 이미 GetAvailableAbilities가 걸러둠

---

## Task 1: `CombatAPI.GetAbilityDisplayName` 헬퍼 신설

**Why:** `BlueprintCache.cs:144-153` 패턴을 재사용 가능한 공용 헬퍼로 추출. 해당 위치의 내부 코드는 그대로 유지(지역 최적화된 캐시 경로라 영향 없음).

**Files:** `GameInterface/CombatAPI.cs` (IsExtraTurn 헬퍼 근처에 추가)

```csharp
/// <summary>
/// ★ v3.111.14: 능력 표시명 안전 조회 — LocalizedString 예외 격리.
/// ability.Name(대문자 N)은 LocalizedString 경유 → 번역 key 누락/깨진 asset reference 시 예외.
/// bp.name(소문자 n)은 Unity ScriptableObject 내부 이름 → 번역 비경유, 항상 안전.
/// 로그/디버그 문자열 interpolation에서 사용.
/// </summary>
public static string GetAbilityDisplayName(AbilityData ability)
{
    if (ability == null) return "null";
    try
    {
        var name = ability.Name;
        if (!string.IsNullOrEmpty(name)) return name;
    }
    catch { /* LocalizedString 예외 → fallback */ }

    try
    {
        var bp = ability.Blueprint;
        return bp?.name ?? "Unknown";
    }
    catch
    {
        return "Unknown";
    }
}
```

---

## Task 2: `GetAvailableAbilities` 루프 보강

**Files:** `GameInterface/CombatAPI.cs:1835-1856`

기존:
```csharp
foreach (var ability in rawAbilities)
{
    var data = ability?.Data;
    if (data == null) continue;

    List<string> reasons;
    if (!IsAbilityAvailable(data, out reasons))
    {
        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Filtered out {data.Name}: ...");
        continue;
    }
    if (HasDuplicateAbilityGroups(data)) { ... }
    abilities.Add(data);
}
```

변경:
```csharp
foreach (var ability in rawAbilities)
{
    try
    {
        var data = ability?.Data;
        if (data == null) continue;

        List<string> reasons;
        if (!IsAbilityAvailable(data, out reasons))
        {
            if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Filtered out {GetAbilityDisplayName(data)}: ...");
            continue;
        }
        if (HasDuplicateAbilityGroups(data))
        {
            if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Filtered out {GetAbilityDisplayName(data)}: duplicate groups");
            continue;
        }
        abilities.Add(data);
    }
    catch (Exception ex)
    {
        // ★ v3.111.14: 단일 능력 처리 실패 → 다음으로 (LocalizedString 등 예외 격리)
        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAvailableAbilities: skip ability due to {ex.GetType().Name}: {ex.Message}");
    }
}
```

---

## Task 3: `FindAnyAttackAbility` 3개 루프 보강

**Files:** `GameInterface/CombatAPI.cs:1895-1956, 1968-1986, 1997-2011`

각 루프에 동일 패턴 적용:
- 루프 바디 전체를 `try { ... } catch { continue; }` 로 감싸기
- 로그 문자열 `{abilityData.Name}` / `{fallbackAttack.Name}` → `{GetAbilityDisplayName(...)}` 로 치환

**주의:** 각 루프에서 `continue`는 try 안에서 쓰므로 catch에서도 빈 `continue` (암묵적 다음 iteration)로 OK.

---

## Task 4: `SituationAnalyzer.AnalyzeAbilities` 루프 보강 (defense-in-depth)

**Files:** `Analysis/SituationAnalyzer.cs:779-1047`

**Why:** Task 2가 1차 방어라 보통은 안전하지만, 2차 방어로 루프 바디를 try/catch. Name 로그 22건 → `CombatAPI.GetAbilityDisplayName(ability)` 치환.

**범위:** 루프 내부 Name interpolation만 치환. 루프 바깥(예: 779 이전/1047 이후) 로그는 건드리지 않음.

---

## Task 5: Version bump + build + commit + memory

- Info.json: 3.111.13 → 3.111.14
- MSBuild Rebuild
- Commit: B.3 완료
- MEMORY.md: v3.111.14 + B.3 완료 기록

---

## 검증 기준

- 빌드 클린
- 기존 비-Psyker 시나리오: 영향 없음 (정상 Name 반환 경로 우선)
- 디버그 모드 ON + 사이커(Idira 등) 포함 전투: 이전에 공격 스킵되던 케이스가 정상 공격 능력 선택
- 로그 출력: 예외 발생 능력만 "Unknown" 또는 blueprint 내부 이름(예: "RapidFire_SoundBarrier") 표시

## 참조

- B.1 커밋: `69748a7`, B.2 커밋: `7123412`
- 로드맵: `docs/plans/2026-04-22-post-phase6-roadmap.md` Task B.3
- 안전 패턴 원본: `Data/BlueprintCache.cs:144-153`
- v3.111.7 선행: `CombatAPI.TryFindDirectionalAoEPrimaryAttack:1693-1698`
