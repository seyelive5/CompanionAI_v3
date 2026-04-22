# Phase B.1 — ExtraTurn Canonical API Migration Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** v3.111.10 hybrid ExtraTurn 감지(`Harmony flag && low-resource threshold`)를 게임 canonical API(`Initiative.InterruptingOrder`)로 교체하여 false positive/negative 0% 달성 + Harmony 패치/캐시 삭제.

**Architecture:**
- 디컴파일 조사(`TurnController.cs:896,956,1002,1047,1544-1559`, `TurnOrderQueue.cs:252`, `Initiative.cs:40,105`)로 `InterruptingOrder`가 게임 내 ExtraTurn 판정의 single source of truth임을 확인.
- 게임이 턴 시작 시 `InterruptingOrder > 0` 세팅, 턴 종료 시 0으로 리셋 — 우리 감지 로직이 동일 신호를 직접 조회하면 Harmony/threshold/stale cache 전부 불필요.
- `ExtraTurnGrantedAP/MP`는 Situation에 저장만 되고 읽는 곳 없음 → dead data, 함께 제거.

**Tech Stack:** C# .NET Framework 4.8.1, Unity Mod Manager, HarmonyLib, MSBuild 18 (2025)

**검증 기준:**
- 빌드 클린 (경고 0, 오류 0)
- Pascal/Abelard/Idira ExtraTurn 시나리오: `IsExtraTurn = true` 정확 감지
- 일반 턴: `IsExtraTurn = false` (false positive 0%)
- 저자원 정상 턴(AP≤2 && MP≤5인 일반 턴): `IsExtraTurn = false` (threshold 편향 제거)

---

## Task 1: `CombatAPI.IsExtraTurn(BaseUnitEntity)` 헬퍼 추가

**Why:** 게임의 `TurnController.GetInterruptingOrder`는 `private static`이라 직접 호출 불가. `Initiative.InterruptingOrder`는 public이지만 squad 유닛 처리(companion은 squad 아니지만 defense-in-depth)를 mirroring할 필요. `CombatAPI`에 single-source helper로 집중하면 향후 호출부가 증가해도 한곳만 관리.

**Files:**
- Modify: `GameInterface/CombatAPI.cs` (기존 AP/MP 헬퍼 근처에 추가)

**Step 1: CombatAPI에서 기존 AP/MP 헬퍼 위치 확인**

```
Grep pattern="GetCurrentAP" path="GameInterface/CombatAPI.cs" output_mode="content" -n=true
```

적절한 위치(기존 turn-state helper 그룹)를 찾아 그 다음에 추가.

**Step 2: 헬퍼 메서드 추가**

```csharp
/// <summary>
/// ★ v3.111.12: 게임 canonical API 기반 ExtraTurn 감지.
/// 디컴파일 참조: TurnController.GetInterruptingOrder (private static helper).
///   - 일반 유닛: unit.Initiative.InterruptingOrder > 0
///   - Squad 유닛: squad.Initiative.InterruptingOrder > 0 (companions는 squad 아니지만 safety)
/// 게임이 TurnOrderQueue.InterruptCurrentUnit에서 셋업, TurnController.EndUnitTurn에서 0 리셋.
/// </summary>
public static bool IsExtraTurn(BaseUnitEntity unit)
{
    if (unit == null || unit.Initiative == null) return false;

    // Squad 경로 (defense-in-depth — companions는 not-in-squad지만 enemy mob이 섞일 수 있음)
    if (unit.IsInSquad)
    {
        var squadPart = unit.GetSquadOptional();
        var squad = squadPart?.Squad;
        return squad?.Initiative != null && squad.Initiative.InterruptingOrder > 0;
    }

    return unit.Initiative.InterruptingOrder > 0;
}
```

**Step 3: 필요 using 확인**

`Kingmaker.Controllers.TurnBased` (Initiative), `Kingmaker.UnitLogic.Squads` (PartSquad/GetSquadOptional)가 CombatAPI.cs에 이미 있는지 Grep, 없으면 추가.

```
Grep pattern="using Kingmaker" path="GameInterface/CombatAPI.cs" output_mode="content" -n=true head_limit=20
```

**Step 4: 중간 빌드 검증 (선택)**

이 태스크 단독 빌드는 필수 아님 — 다음 태스크에서 사용처가 생긴 뒤 한번에 빌드.

---

## Task 2: `SituationAnalyzer.AnalyzeUnitState` 하이브리드 블록 교체

**Why:** 현재 코드는 Harmony 캐시 + AP/MP threshold 이중 체크. `InterruptingOrder` 직접 조회는 결정적 + 즉시성 + 레이싱 없음.

**Files:**
- Modify: `Analysis/SituationAnalyzer.cs:139-161`

**Step 1: 변경 전 블록 재확인**

```
Read Analysis/SituationAnalyzer.cs offset=139 limit=25
```

Expected: v3.111.10 하이브리드 로직 (harmonyFlag && actualLowResource).

**Step 2: 교체**

```csharp
// ★ v3.111.12: Canonical API 기반 ExtraTurn 감지.
//   디컴파일 TurnController.GetInterruptingOrder 로직을 CombatAPI.IsExtraTurn으로 감쌈.
//   v3.111.10 하이브리드(Harmony+threshold) 대비 장점:
//     - 결정적: threshold 편향(저자원 정상턴 오탐) 제거.
//     - 즉시성: 턴 시작 직후 게임 상태 그대로 반영.
//     - 레이싱 없음: StartUnitTurnInternal 이후 SetYellowPoint 재할당과 무관.
situation.IsExtraTurn = CombatAPI.IsExtraTurn(unit);

if (Main.IsDebugEnabled && situation.IsExtraTurn)
{
    Main.LogDebug($"[Analyzer] {unit.CharacterName}: Extra turn CONFIRMED (InterruptingOrder>0) — AP={situation.CurrentAP:F1}, MP={situation.CurrentMP:F1}");
}
```

- 제거: `extraTurnInfo` 조회 2줄
- 제거: `harmonyFlag`/`actualLowResource` 지역 변수
- 제거: `situation.ExtraTurnGrantedAP/MP` 대입 2줄
- 제거: FALSE POSITIVE 브랜치 로그 (이제 개념상 불가능)

**Step 3: import 확인**

`CompanionAI_v3.GameInterface` namespace는 이미 CombatAPI 사용 중이라 추가 using 불필요.

---

## Task 3: `Situation.cs`에서 `ExtraTurnGrantedAP/MP` dead 필드 제거

**Why:** 전체 코드베이스에서 set만 있고 read 없음 (Grep 확인 완료: Situation.cs 정의·Reset 3곳 + SituationAnalyzer 할당 2곳뿐). 게임 API의 `GetCurrentAP/MP`가 ExtraTurn 시점에 이미 granted 값을 반환.

**Files:**
- Modify: `Analysis/Situation.cs:43-47` (필드 정의 + XML 주석)
- Modify: `Analysis/Situation.cs:424-425` (Reset 초기화)

**Step 1: 필드 정의 제거**

Read line 42-48 먼저:

```
Read Analysis/Situation.cs offset=42 limit=8
```

다음 블록 전체 삭제:
```csharp
/// <summary>★ v3.111.8: 임시턴 시 부여된 AP (보통 1-2).</summary>
public int ExtraTurnGrantedAP { get; set; }

/// <summary>★ v3.111.8: 임시턴 시 부여된 MP (보통 0).</summary>
public int ExtraTurnGrantedMP { get; set; }
```

`IsExtraTurn` property와 `#endregion` 사이 빈 줄 1개 유지.

**Step 2: Reset 초기화 제거**

line 424-425 2줄 삭제:
```csharp
ExtraTurnGrantedAP = 0;        // ★ v3.111.8
ExtraTurnGrantedMP = 0;        // ★ v3.111.8
```

`IsExtraTurn = false;` 줄은 유지.

**Step 3: 잔여 참조 없음 재확인**

```
Grep pattern="ExtraTurnGrantedAP|ExtraTurnGrantedMP" output_mode="files_with_matches"
```

Expected: 0 matches.

---

## Task 4: `ExtraTurnPatch.cs` 삭제

**Why:** `ExtraTurnCache` + `ExtraTurnPatch` 두 타입 모두 이 파일 전용. Task 2 이후 Get/Store 호출부 0이 되므로 완전 삭제 가능. Harmony `[HarmonyPatch]` attribute 제거로 `_harmony.PatchAll`이 자동 스킵.

**Files:**
- Delete: `GameInterface/ExtraTurnPatch.cs`

**Step 1: 외부 참조 최종 확인**

```
Grep pattern="ExtraTurnCache|ExtraTurnPatch" output_mode="content" -n=true
```

Expected: 본인 파일 + SituationAnalyzer Clear 호출만. Clear 호출은 Task 5에서 제거.

**Step 2: 파일 삭제**

```bash
rm "GameInterface/ExtraTurnPatch.cs"
```

Windows 환경이면:
```bash
git rm "GameInterface/ExtraTurnPatch.cs"
```
(csproj는 SDK-style라 include 자동 — 수동 편집 불필요)

---

## Task 5: `SituationAnalyzer.Reset`에서 `ExtraTurnCache.Clear()` 호출 제거

**Why:** Task 4에서 타입 자체가 삭제됨. 호출 안 지우면 빌드 실패.

**Files:**
- Modify: `Analysis/SituationAnalyzer.cs:53-56`

**Step 1: 변경 전 확인**

```
Read Analysis/SituationAnalyzer.cs offset=52 limit=6
```

**Step 2: 제거**

```csharp
// ★ v3.111.8: 임시턴 캐시도 전투 종료 시 정리 (전투 간 유출 방지).
CompanionAI_v3.GameInterface.ExtraTurnCache.Clear();
```

위 2줄(주석 + Clear 호출) 삭제. 인접한 `EnemyMoveCache.Clear()`는 유지.

---

## Task 6: `Info.json` 버전 범프

**Files:**
- Modify: `Info.json`

**Step 1: 변경**

```json
"Version": "3.111.11",
```
→
```json
"Version": "3.111.12",
```

---

## Task 7: 빌드 검증

**Step 1: Release Rebuild**

```powershell
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo
```

Expected: 경고 0, 오류 0. Output dll 갱신:
`%LOCALAPPDATA%Low\Owlcat Games\Warhammer 40000 Rogue Trader\UnityModManager\CompanionAI_v3\CompanionAI_v3.dll`

**Step 2: 실패 시 대응 매트릭스**

| 증상 | 원인 | 조치 |
|------|------|------|
| `ExtraTurnCache not found` | Task 5 누락 | SituationAnalyzer.cs line 55-56 재확인 |
| `ExtraTurnGranted* not found` | 외부 사용처 잔존 | Grep으로 재스캔 |
| `IsInSquad not a member` | using 누락 | CombatAPI에 `using Kingmaker.UnitLogic.Squads;` 필요 |
| `GetSquadOptional not found` | 게임 버전 차이 | 디컴파일 최신 확인, 없으면 `IsInSquad`만 체크 후 false 반환 |

---

## Task 8: 커밋

**Step 1: 변경 파일 확인**

```bash
git status --short
```

Expected (5 entries):
- `M Analysis/Situation.cs`
- `M Analysis/SituationAnalyzer.cs`
- `D GameInterface/ExtraTurnPatch.cs`
- `M GameInterface/CombatAPI.cs`
- `M Info.json`

**Step 2: 커밋**

```bash
git add Analysis/Situation.cs Analysis/SituationAnalyzer.cs GameInterface/ExtraTurnPatch.cs GameInterface/CombatAPI.cs Info.json
```

```bash
git commit -m "$(cat <<'EOF'
refactor(v3.111.12): ExtraTurn canonical API 마이그레이션 — Initiative.InterruptingOrder 직접 조회

Phase B.1 of post-phase6-roadmap:
- Add CombatAPI.IsExtraTurn helper — mirrors TurnController.GetInterruptingOrder (squad-safe).
- Replace SituationAnalyzer hybrid (Harmony flag + AP/MP threshold) with single canonical check.
- Remove Situation.ExtraTurnGrantedAP/MP dead fields (never read anywhere).
- Delete GameInterface/ExtraTurnPatch.cs (106 lines: ExtraTurnCache + ExtraTurnPatch Harmony patch).
- Remove ExtraTurnCache.Clear() call from SituationAnalyzer.Reset.

Canonical source of truth (decompile):
- Initiative.InterruptingOrder set by TurnOrderQueue.InterruptCurrentUnit:252
- Reset by TurnController.EndUnitTurn:1055 (unit.Initiative.InterruptingOrder = 0)
- Game-wide ExtraTurn predicate: TurnController.GetInterruptingOrder (squad-aware)

Advantages over v3.111.10 hybrid:
- Deterministic: AP/MP threshold bias removed (저자원 정상턴 false positive 제거).
- Immediate: StartUnitTurnInternal 이후 SetYellowPoint 재할당과 independent.
- Zero-cost: Harmony patch + static cache 삭제.

Verification: build clean. Pascal/Abelard/Idira ExtraTurn scenarios to be verified
in-game (IsExtraTurn=true on interrupt, false on normal turn).
EOF
)"
```

**Step 3: 확인**

```bash
git log --oneline -1
git status
```

Expected: HEAD 새 커밋, working tree clean (tracked 기준).

---

## Task 9: 런타임 검증 가이드 (사용자 액션)

**Why:** 빌드 클린만으로 완료 판정 불가. 로드맵 "완료 판정 기준"에 런타임 로그 트레이스 확인 포함.

**사용자가 인게임에서 확인할 항목:**

1. **일반 턴** (ExtraTurn 아님):
   - 로그에 `[Analyzer] ... Extra turn CONFIRMED` 출력 **없어야 함**
   - Plan 실행이 정상(이동/공격 가드 발동 없음)

2. **임시턴 시나리오 (쳐부숴라 등):**
   - Pascal/Abelard/Idira 대상:
     - 로그: `[Analyzer] ... Extra turn CONFIRMED (InterruptingOrder>0) — AP=... MP=...`
     - Plan: v3.111.8/9 가드가 여전히 작동 (이동 스킵 등)

3. **저자원 정상 턴** (디버프로 AP=2, MP=5 케이스):
   - `Extra turn CONFIRMED` **출력 안 됨** (하이브리드 때 false positive였던 케이스 해결 확인)

**로그 위치:** `C:\Users\veria\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\GameLogFull.txt`

**문제 발생 시:**
- False positive 재등장 → `Initiative.InterruptingOrder` 게임 버전별 의미 차이 조사.
- False negative (실제 ExtraTurn인데 false) → squad 경로 분기 재검토, IsInSquad=true인데 enemy 판정인 가능성 확인.

---

## Task 10: 메모리 노트 업데이트

**Files:**
- Modify: `C:\Users\veria\.claude\projects\c--Users-veria-Downloads-CompanionAI-v3-master---v3-5-7\memory\MEMORY.md`

**Step 1: 버전/상태 갱신**

기존:
```
- 현재 버전: **3.111.11** (Info.json, 2026-04-22 기준) — Phase 1-6 + 이슈 1-4 + Hybrid ExtraTurn 감지 + InfluenceMap 제거 마무리.
```

변경:
```
- 현재 버전: **3.111.12** (Info.json, 2026-04-22 기준) — Phase 1-6 + 이슈 1-4 + Canonical ExtraTurn API + InfluenceMap 제거 마무리.
- **Phase B.1 완료**: ExtraTurn 감지를 Harmony hybrid → Initiative.InterruptingOrder canonical API로 마이그레이션. ExtraTurnPatch.cs 삭제, GrantedAP/MP dead 필드 제거.
```

---

## 실행 권장 순서

| 태스크 | 소요 | 의존성 | 검증 |
|--------|------|--------|------|
| 1: CombatAPI helper | 10분 | - | 없음 (사용처 Task 2에서) |
| 2: SituationAnalyzer 교체 | 10분 | 1 | Task 7 빌드에서 |
| 3: Situation dead 필드 제거 | 5분 | 2 | Task 7 빌드에서 |
| 4: ExtraTurnPatch.cs 삭제 | 2분 | 2 | Task 7 빌드에서 |
| 5: Reset Clear 호출 제거 | 2분 | 4 | Task 7 빌드에서 |
| 6: Info.json | 1분 | - | - |
| 7: 빌드 | 2분 | 1-6 | MSBuild 출력 |
| 8: 커밋 | 3분 | 7 PASS | git log |
| 9: 런타임 (사용자) | 인게임 테스트 | 8 | GameLogFull.txt |
| 10: 메모리 갱신 | 2분 | 8 | - |

**총 예상 시간:** 30-40분 (사용자 런타임 테스트 제외)

## 참조

- 디컴파일 (조사 완료):
  - `Code/Kingmaker/Controllers/TurnBased/Initiative.cs:40,105` (public InterruptingOrder, Clear)
  - `Code/Kingmaker/Controllers/TurnBased/TurnController.cs:896,956,1002,1047,1055,1544-1559` (GetInterruptingOrder + 사용처 + 리셋)
  - `Code/Kingmaker/Controllers/TurnBased/TurnOrderQueue.cs:252` (InterruptCurrentUnit 셋업)
  - `Code/Kingmaker/Controllers/TurnBased/InterruptionData.cs` (AsExtraTurn 구조체)
- 원 로드맵: `docs/plans/2026-04-22-post-phase6-roadmap.md` Task B.1
- 이전 버전 이력: v3.111.8 (초기 Harmony), v3.111.9 (DPS Phase 8 가드), v3.111.10 (hybrid threshold)
