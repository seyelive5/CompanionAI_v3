# Phase C.4 — GetEnemyThreatRangeInTiles Turn Cache

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** `GetEnemyThreatRangeInTiles`를 턴별 캐시화 — reflection(`AiCollectedDataStorage`, `GetFirstWeapon`)이 scan당 수천 회 호출되던 성능 핫스팟 제거.

**문제:**
- `CombatAPI.GetEnemyThreatRangeInTiles`는 2개 reflection 호출 필요:
  - `Game.Instance.Player.AiCollectedDataStorage[enemy].AttackDataCollection.GetThreatRange()`
  - `enemy.GetFirstWeapon().Blueprint.AttackRange`
- `GetEnemyTurnThreatScore`(뿐만 아니라 `EvaluatePosition`이 각 tile × enemies 호출)에서 매번 실행
- 스캔 1회 기준: 400 tiles × 8 enemies = **3,200 호출/scan**
- 적별 threat range는 한 턴 동안 불변(무기/학습 데이터 턴 중 바뀌지 않음)

**Architecture:**
- `CombatAPI` 내 static `Dictionary<BaseUnitEntity, int> _enemyThreatRangeCache`
- `GetEnemyThreatRangeInTiles`에 캐시 체크 → 계산 → 저장 패턴
- `ClearEnemyThreatRangeCache()` public 메서드 추가
- `CombatCache.ClearAll()`에서 호출 (기존 `ClearWeaponRangeCache`, `ClearDamagingAoECache` 옆에)

**Tech Stack:** C# .NET Framework 4.8.1

---

## Task 1: `CombatAPI`에 캐시 필드 + Clear 메서드 추가

**Files:** `GameInterface/CombatAPI.cs` (기존 WeaponRangeCache/DamagingAoECache 필드 근처)

먼저 현재 위치 파악 (기존 캐시 근처):

```
Grep pattern="ClearWeaponRangeCache|ClearDamagingAoECache|_weaponRangeCache" path="GameInterface/CombatAPI.cs" output_mode="content" -n=true
```

추가:
```csharp
// ★ v3.111.18 Phase C.4: 적별 threat range 턴별 캐시.
//   GetEnemyThreatRangeInTiles는 2개 reflection 필요 (AiCollectedDataStorage + weapon blueprint).
//   EvaluatePosition에서 tile × enemies 반복 호출 → 3,200회/scan.
//   적 threat range는 한 턴 동안 불변이라 캐시 안전.
private static readonly Dictionary<BaseUnitEntity, int> _enemyThreatRangeCache
    = new Dictionary<BaseUnitEntity, int>();

public static void ClearEnemyThreatRangeCache() => _enemyThreatRangeCache.Clear();
```

---

## Task 2: `GetEnemyThreatRangeInTiles`에 캐시 로직 추가

**Files:** `GameInterface/CombatAPI.cs:842-877`

변경 (함수 시작 직후 캐시 체크, 리턴 직전 저장):

```csharp
public static int GetEnemyThreatRangeInTiles(BaseUnitEntity enemy)
{
    if (enemy == null) return 0;

    // ★ v3.111.18 Phase C.4: 턴별 캐시 체크
    if (_enemyThreatRangeCache.TryGetValue(enemy, out int cached))
        return cached;

    int learnedRange = 0;
    try { ... } catch { ... }

    int weaponRange = 0;
    try { ... } catch { ... }

    int result = System.Math.Max(learnedRange, weaponRange);
    _enemyThreatRangeCache[enemy] = result;
    return result;
}
```

---

## Task 3: `CombatCache.ClearAll`에 캐시 클리어 호출 추가

**Files:** `GameInterface/CombatCache.cs:284-285`

기존:
```csharp
CombatAPI.ClearWeaponRangeCache();
CombatAPI.ClearDamagingAoECache();
```

변경:
```csharp
CombatAPI.ClearWeaponRangeCache();
CombatAPI.ClearDamagingAoECache();
CombatAPI.ClearEnemyThreatRangeCache();  // ★ v3.111.18 Phase C.4
```

---

## Task 4: Version bump + build + commit + memory

- Info.json: 3.111.17 → 3.111.18
- MSBuild Rebuild
- Commit
- MEMORY.md update

---

## 검증 기준

- 빌드 클린
- 인게임: 성능 개선 (체감 가능한 지연 감소 — 특히 적 多數 전투에서)
- 정확성: 캐시 값이 턴 내 불변이므로 결과 동일

## 범위 외

- `GetEnemyTurnThreatScore` 자체 캐시: AP_Blue가 턴 중 변할 수 있어(적 이동 등 hypothetical) 캐시 시 정확성 위험. 근본 호출인 `GetEnemyThreatRangeInTiles` 캐시만으로 대부분 비용 절감.

## 참조

- 패턴: `ClearWeaponRangeCache`, `ClearDamagingAoECache` (CombatAPI 기존 static 캐시)
- 로드맵: `docs/plans/2026-04-22-post-phase6-roadmap.md` Task C.4
