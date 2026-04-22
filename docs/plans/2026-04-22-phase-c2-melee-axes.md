# Phase C.2 — FindMeleeAttackPositionSync Phase 1-6 Axis Integration

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 근접 공격 위치 탐색이 Phase 1-6 개선(HideScore, EnemyTurnThreatSum, StayingAwayBonus)을 못 받던 누락을 메움. 근접 유닛도 방어 축을 고려한 위치 선택.

**문제:**
- `FindRangedAttackPositionSync`(내부적으로 `EvaluatePosition`)는 Phase 1-6 전체 축 설정
- `FindMeleeAttackPositionSync`(1414)는 자체 점수 계산 — HideScore/EnemyTurnThreatSum/StayingAwayBonus 전부 0
- 결과: 근접 유닛(Abelard, Argenta-melee 등)이 "엄폐된 위치 선호", "다음 턴 위협 회피" 혜택을 못 받음

**Architecture:**
- `EvaluatePosition:1018-1036` 패턴을 근접 함수 내부 posScore 생성 직후 이식
- `StayingAwayBonus`는 근접 특성상 0 유지 (적에게 **접근**이 목적, 거리 유지 반의어) — 주석으로 명시
- 호출부 4곳 모두 이미 `situation.Enemies` 전달 — signature 변경 불요

**Tech Stack:** C# .NET Framework 4.8.1

---

## Task 1: HideScore 5축 추가

**Files:** `GameInterface/MovementAPI.cs:1498` (posScore 생성 직후)

`ApplyBlackboardScores` 호출 직전에 추가:

```csharp
// ★ v3.111.16 Phase C.2: Phase 1-6 방어 축 통합 — HideScore + EnemyTurnThreatSum.
//   근접 유닛도 "엄폐된 위치", "다음 턴 위협 회피" 선호.
//   StayingAwayBonus는 근접 approach와 반의어이므로 0 유지.
if (enemies != null && enemies.Count > 0)
{
    var pm = _currentPredictedMoves;
    var hideComponents = pm != null
        ? TileScorerPort.GetEnsuredCoverComponents(node, unit.SizeRect, enemies, pm)
        : TileScorerPort.GetHideScoreComponents(node, unit.SizeRect, enemies);
    posScore.ApplyHideComponents(hideComponents);

    float turnThreatSum = 0f;
    foreach (var e in enemies)
    {
        if (e == null || e.LifeState.IsDead) continue;
        turnThreatSum += CombatAPI.GetEnemyTurnThreatScore(e, node.Vector3Position);
    }
    posScore.EnemyTurnThreatSum = turnThreatSum;
}
```

---

## Task 2: Version bump + build + commit + memory

- Info.json: 3.111.15 → 3.111.16
- MSBuild Rebuild
- Commit
- MEMORY.md update

---

## 검증 기준

- 빌드 클린
- 인게임: Abelard 등 근접 유닛 이동 시 엄폐된 타일 선호 (기존과 다른 위치 선택 관찰)
- 로그: `posScore.HideScore > 0` 확인 (기존 항상 0이었음)

## 범위 외

- `CoverScore` (공격자 관점 적 커버): 근접은 cover 영향 적음 (대부분 melee는 cover bypass 또는 minor)
- `HitChanceBonus`: 근접은 hit chance 변별력 낮음

## 참조

- 패턴 원본: `MovementAPI.EvaluatePosition:1018-1036`
- 로드맵: `docs/plans/2026-04-22-post-phase6-roadmap.md` Task C.2
