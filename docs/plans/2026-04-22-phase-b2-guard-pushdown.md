# Phase B.2 — ExtraTurn Guard Push-Down Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 9곳 sprinkle된 `if (situation.IsExtraTurn)` 가드를 호출 대상 함수 내부로 push-down하여 DRY화 + 재발 방지. 로드맵의 "MovementPlanner 3 함수" 가정을 call-site 분석 결과로 정정.

**Architecture:**
- 조사 결과 sprinkle 9곳은 4개 push-down 가능 함수 + 2개 context-specific 패턴으로 분해됨.
- `PlanRetreat`는 push-down 금지 — emergency HP retreat은 MP=0에서도 시도해야 함 (sprinkle된 PlanRetreat 호출은 역시 이 이유로 가드 없음).
- 4개 target 함수: 상단 guard-clause 패턴 확인(`if (!CanMove) return null` 등) → `if (IsExtraTurn) return null` 추가는 기존 스타일과 일치.

**Tech Stack:** C# .NET Framework 4.8.1, MSBuild 18

**Call-site analysis (v3.111.12 기준):**

| # | Sprinkle 위치 | 호출 대상 | Action |
|---|---|---|---|
| 1 | DPSPlan.cs:1057 | Phase 8 approach 분기 전체 | ⚠️ **유지** (context-specific, gap-closer 경로 포함) |
| 2 | DPSPlan.cs:1121 | `PlanPostActionSafeRetreat` | 제거 (push to MovementPlanner) |
| 3 | DPSPlan.cs:1149 | `PlanTacticalReposition` | 제거 (push to MovementPlanner) |
| 4 | SupportPlan.cs:485 | `PlanPostActionSafeRetreat` | 제거 (push to MovementPlanner) |
| 5 | SupportPlan.cs:568 | `PlanMoveTowardAllies` (internal) | 제거 (push to SupportPlan.internal) |
| 6 | SupportPlan.cs:619 | `PlanTacticalReposition` | 제거 (push to MovementPlanner) |
| 7 | OverseerPlan.cs:401 | `PlanRavenAggressiveRelocate` (internal) | 제거 (push to OverseerPlan.internal) |
| 8 | OverseerPlan.cs:655 | `PlanRavenAggressiveRelocate` (internal) | 제거 (push to OverseerPlan.internal) |
| 9 | TankPlan.cs:149 | SmartTaunt alt-option selection | ⚠️ **유지** (return-null 패턴 아닌 선택 로직) |

**결과:** 4 함수 push-down + 7 sprinkle 제거 + 2 sprinkle 문서화 유지.

---

## Task 1: `MovementPlanner.PlanPostActionSafeRetreat` 가드 추가

**Files:** `Planning/Planners/MovementPlanner.cs:1200-1204`

상단 guard clause 바로 다음에 추가:

```csharp
public static PlannedAction PlanPostActionSafeRetreat(Situation situation)
{
    if (!situation.CanMove) return null;
    if (situation.CurrentMP <= 0) return null;
    // ★ v3.111.13: 임시턴 스킵 — AP/MP 부족으로 fallback이 엉뚱한 위치 반환 가능.
    //   이전 v3.111.8/9 sprinkle(DPS:1121, Support:485) push-down.
    if (situation.IsExtraTurn) return null;
    ...
}
```

---

## Task 2: `MovementPlanner.PlanTacticalReposition` 가드 추가

**Files:** `Planning/Planners/MovementPlanner.cs:1301-1304`

```csharp
public static PlannedAction PlanTacticalReposition(Situation situation, float remainingMP)
{
    if (!situation.PrefersRanged) return null;
    if (remainingMP <= 0) return null;
    // ★ v3.111.13: 임시턴 스킵 — MP=0 → 잘못된 위치로 이동하는 버그 방지.
    //   이전 v3.111.9 sprinkle(DPS:1149, Support:619) push-down.
    if (situation.IsExtraTurn) return null;
    ...
}
```

---

## Task 3: `OverseerPlan.PlanRavenAggressiveRelocate` 가드 추가

**Files:** `Planning/Plans/OverseerPlan.cs:1512-1514`

함수 시작 직후 빈 줄 다음에 추가:

```csharp
private PlannedAction PlanRavenAggressiveRelocate(Situation situation, ref float remainingAP, bool skipCoverageCheck = false)
{
    // ★ v3.111.13: 임시턴 스킵 — AP/MP 부족으로 Raven 사거리 밖 재배치 실패.
    //   이전 v3.111.8 sprinkle(Phase 3.5.5, Phase 4.6) push-down.
    if (situation.IsExtraTurn) return null;

    // Raven Relocate 능력 찾기
    ...
}
```

---

## Task 4: `SupportPlan.PlanMoveTowardAllies` 가드 추가

**Files:** `Planning/Plans/SupportPlan.cs:781-784`

```csharp
private PlannedAction PlanMoveTowardAllies(Situation situation, float remainingMP)
{
    if (remainingMP <= 0) return null;
    // ★ v3.111.13: 임시턴 스킵 — AP/MP 부족으로 이동 실패 → fallback 버그.
    //   이전 v3.111.9 sprinkle(Phase 9) push-down.
    if (situation.IsExtraTurn) return null;
    ...
}
```

---

## Task 5-11: Sprinkle 가드 7곳 제거

각 Plan의 sprinkle guard block을 `if/else` 구조에서 단순화. 구체 edit은 실행 중 Read로 확인 후 진행 (정확한 blocks이 변경될 수 있어 선행 snapshot 불신뢰).

**제거 대상:**
- DPSPlan.cs:1120-1146 (PostActionSafeRetreat 가드 블록, `if (!IsExtraTurn && ...)` 조건에서 IsExtraTurn 빼기)
- DPSPlan.cs:1148-1162 (PlanTacticalReposition 가드 블록, 동일)
- SupportPlan.cs:484-528 (PostActionSafeRetreat 가드 블록)
- SupportPlan.cs:567-600 (PlanMoveTowardAllies 가드 블록)
- SupportPlan.cs:618-630 (PlanTacticalReposition 가드 블록)
- OverseerPlan.cs:400-417 (PlanRavenAggressiveRelocate Phase 3.5.5 가드)
- OverseerPlan.cs:654-670 (PlanRavenAggressiveRelocate Phase 4.6 가드)

**핵심 원칙:** push-down으로 함수가 null 반환 시 기존 if/else의 "null 체크"가 그대로 동작. 즉, guard 제거 = `if (!IsExtraTurn) { ... var result = Plan...(); if (result != null) actions.Add(result); }` → `{ ... var result = Plan...(); if (result != null) actions.Add(result); }`. 로깅은 유지 가치 낮음(함수 내부에서 찍는 로그로 충분).

---

## Task 12: 유지하는 2곳 문서화

### DPSPlan.cs:1057 (Phase 8 approach branch)

주석 보강: 이 가드는 PlanMoveOrGapCloser + PlanRetreat + gap-closer 경로 전체를 포함. PlanMoveOrGapCloser는 push-down 금지(widely used, 일반 턴에서 필요). 따라서 sprinkle 유지.

### TankPlan.cs:149 (SmartTaunt alt-option)

주석 보강: 이 가드는 "return null" 패턴 아님. "best option이 RequiresMove면 대체 옵션 찾기"라는 선택 로직이라 push-down 불가.

---

## Task 13: Version bump + build + commit + memory

- Info.json: 3.111.12 → 3.111.13
- MSBuild Rebuild
- Commit: B.2 완료
- MEMORY.md: v3.111.13 + B.2 완료 기록

---

## 검증 기준

- 빌드 클린 (경고/오류 0)
- 인게임 런타임: Pascal/Abelard/Idira ExtraTurn 시 이전과 동일한 "skip" 행동 (로그 메시지는 함수 내부에서 출력하도록 shift됨)
- 정상 턴: 영향 없음 (가드가 false이므로 기존과 동일 경로)

## 참조

- B.1 커밋: `69748a7` (Canonical API 마이그레이션)
- 로드맵: `docs/plans/2026-04-22-post-phase6-roadmap.md` Task B.2
- 이전 sprinkle 이력: v3.111.8 (초기 TankPlan/Overseer), v3.111.9 (DPS/Support 확장)
