# Phase C.3 — FindRetreatPositionSync StayingAwayBonus 연결

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** `FindRetreatPositionSync`에 `StayingAwayScore` 계산 + `StayingAwayBonus` 적용 — Phase 4의 "Retreat goal → 40f 가중치"가 dead weight였던 문제 해결.

**문제:**
- `EvaluatePosition`(1055-1065)은 MovementGoal에 따라 StayingAwayBonus 계산
  - `Retreat → 40f`, FindCover → 30f, RangedAttackPosition → 25f, default → 10f
- `FindRetreatPositionSync`(1718-1757)는 자체 점수 계산 — `StayingAwayBonus` 설정 안 함 → **항상 0**
- 결과: "적 이동능력 반영 안전거리"라는 Phase 4 개선이 가장 중요한 use case(실제 후퇴)에서 미작동

**Architecture:**
- `TileScorerPort.GetStayingAwayScore(node, unit, enemies)` 호출 후 결과를 `score.StayingAwayBonus = * 40f`로 설정
- HideComponents 적용 지점 근처(`posScore` 생성 후)에 삽입
- 40f 가중치: EvaluatePosition의 `MovementGoal.Retreat` 케이스와 동일 (일관성)

**Tech Stack:** C# .NET Framework 4.8.1

---

## Task 1: StayingAwayScore 계산 + StayingAwayBonus 설정

**Files:** `GameInterface/MovementAPI.cs:1772-1780` (HideComponents 적용 블록 직후)

HideComponents try/catch 블록 다음에 추가:

```csharp
// ★ v3.111.17 Phase C.3: StayingAwayBonus — 적 이동능력 반영 안전거리 점수.
//   Phase 4 가중치: Retreat goal → 40f (EvaluatePosition 일관).
//   기존에 설정 누락으로 Phase 4가 후퇴에서 dead weight였음.
try
{
    float stayingAway = TileScorerPort.GetStayingAwayScore(node, unit, enemies);
    score.StayingAwayScore = stayingAway;
    score.StayingAwayBonus = stayingAway * 40f;
}
catch (System.Exception ex)
{
    if (Main.IsDebugEnabled) Main.LogDebug($"[MovementAPI] retreat staying-away silent: {ex.Message}");
}
```

---

## Task 2: Version bump + build + commit + memory

- Info.json: 3.111.16 → 3.111.17
- MSBuild Rebuild
- Commit
- MEMORY.md update

---

## 범위 외

- `EnemyTurnThreatSum`: 이미 `destThreatScore = CalculateThreatScore(unit, node)`로 유사 개념 반영 중, double-count 위험 → 미추가
- `CoverScore`: 공격자 관점이라 후퇴 맥락 부적합

## 검증 기준

- 빌드 클린
- 인게임: 원거리 유닛 후퇴 시 "적 이동 가능 거리 밖 타일" 선호 (기존은 단순 거리 기반)
- 로그 관찰: `score.StayingAwayBonus > 0` 확인 (기존 항상 0)

## 참조

- 패턴 원본: `MovementAPI.EvaluatePosition:1055-1065` (MovementGoal weight)
- `TileScorerPort.GetStayingAwayScore:100-137` (적 blueprint AP/이동속도 기반)
- 로드맵: `docs/plans/2026-04-22-post-phase6-roadmap.md` Task C.3
