# Phase C.1 — HideScore Normalization Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** `HideValue`를 unbounded 적 수 비례 합산 → **적별 평균([0-1])** 으로 정규화하여 TotalScore에서 HideScore의 scale을 다른 축과 일치시킴.

**배경:**
- 게임 상수 `hideCoverValues = [None=0, Half=0.0004, Full=0.02, Invisible=1]` (Invisible이 Half의 2500배)
- 현재 `HideValue = sum of all enemy cover values` → 적 10명 Invisible → HideValue=10 → HideScore 기여 100 (TotalScore 지배)
- 다른 HideScore 축(`FullCoverComplete` × 50, `AnyCoverComplete` × 20, ratios × 15)은 bounded [0, 50]
- FullComplete/AnyComplete가 이미 "모든 적 보호" 보너스를 담당 → HideValue의 "적 수 비례" 역할 중복

**정규화 근거:**
- 다른 필드들(Ratios)이 이미 `/ validCount` 평균화 패턴 사용 → 일관성
- HideValue → "per-enemy 평균 cover 품질 [0, 1]" semantics가 자연스러움
- 정규화 후 HideScore max = 110 (기존 unbounded → ~180 가정보다 작음)

**Architecture:**
- `TileScorerPort.GetHideScoreComponents:88` + `GetEnsuredCoverComponents:230` 2곳 수정
- XML doc + 관련 주석 업데이트
- 계수 조정(`MovementAPI:2293, 2314`의 `* 0.05f`, `* 0.03f`)은 튜닝 영역이라 **C.1 범위 외**, 주석에 "향후 튜닝 필요" 명시

**Tech Stack:** C# .NET Framework 4.8.1

---

## Task 1: `GetHideScoreComponents` HideValue 평균화

**Files:** `GameInterface/TileScorerPort.cs:88`

변경:
```csharp
result.HideValue = hideValue;
```
→
```csharp
// ★ v3.111.15 Phase C.1: 적 수 비례 unbounded 합 → 평균 [0, 1].
//   hideCoverValues 배열([0, 0.0004, 0.02, 1]) 기반이므로 평균은 per-enemy 평균 엄폐 품질.
result.HideValue = hideValue / validCount;
```

---

## Task 2: `GetEnsuredCoverComponents` HideValue 평균화

**Files:** `GameInterface/TileScorerPort.cs:230`

동일 변경 (동일 주석).

---

## Task 3: `HideScoreComponents` 구조체 주석 업데이트

**Files:** `GameInterface/TileScorerPort.cs:18-32`

"[4] HideValue — 가중 aggregate (게임 hideCoverValues 역수 기반, 적 수에 비례한 unbounded 합)"
→
"[4] HideValue — per-enemy 평균 엄폐 품질 [0, 1]. hideCoverValues=[None=0, Half=0.0004, Full=0.02, Invisible=1]의 평균."

---

## Task 4: `PositionScore.HideValue` XML doc + `HideScore` 수식 주석 갱신

**Files:** `GameInterface/MovementAPI.cs:214-225`

기존:
```csharp
public float HideValue { get; set; }          // 가중 합계 (적 수 비례, unbounded)

/// <summary>
/// HideScore 가중 합산 — TotalScore 기여값.
/// FullComplete=50 (완전 은폐 특별 보너스), AnyComplete=20, Ratios*15, HideValue*10.
/// </summary>
```

변경:
```csharp
public float HideValue { get; set; }          // ★ v3.111.15: per-enemy 평균 엄폐 품질 [0, 1].

/// <summary>
/// HideScore 가중 합산 — TotalScore 기여값.
/// FullComplete*50 (완전 은폐 특별 보너스) + AnyComplete*20 + Ratios*15 + HideValue*10.
/// ★ v3.111.15 Phase C.1: HideValue 정규화로 max 180 → 110.
///   TacticalAdjustment 계수(MovementAPI:2293,2314)는 미조정 — 인게임 튜닝 시 재검토.
/// </summary>
```

---

## Task 5: `MovementAPI:2292-2294, 2312-2315` TacticalAdjustment 주석 갱신

기존: `// 계수 0.5 → 0.05 (HideScore max가 Cover max의 ~4.5배이므로 총량 유지).`

변경: `// ★ v3.111.15: HideValue 정규화로 HideScore max 180 → 110. 계수 미조정(튜닝 대상).`

---

## Task 6: Version bump + build + commit + memory

- Info.json: 3.111.14 → 3.111.15
- MSBuild Rebuild
- Commit
- MEMORY.md update

---

## 검증 기준

- 빌드 클린
- 인게임: 엄폐 많은 맵에서 포지셔닝 변화 관찰. HideScore가 TotalScore를 압도하지 않아야 함.
- 튜닝: 만약 "너무 약해짐"이면 `MovementAPI:2293,2314`의 계수를 0.05→0.08, 0.03→0.05로 조정 (별도 작업).

## 참조

- 게임 원본: `Code\Kingmaker\AI\AreaScanning\TileScorers\ProtectionTileScorer.cs`
- 이전 phase B 커밋: B.1 `69748a7`, B.2 `7123412`, B.3 `d37a7c9`
- 로드맵: `docs/plans/2026-04-22-post-phase6-roadmap.md` Task C.1
