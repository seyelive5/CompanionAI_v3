# Post-Phase 6 로드맵 — v3.111.11 ~ v3.112 구현 플랜

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** v3.110.0 ~ v3.111.10 (Phase 1-6 마스터 플랜 + v3.111.x 버그 수정) 리뷰에서 드러난 Critical/Important 이슈를 구조적으로 해결. 단계적 commit-friendly 플랜.

**Architecture:**
- Phase A (v3.111.11): 미커밋 InfluenceMap 제거 마무리 + dead code 정리 + 커밋. **상세 태스크 포함**.
- Phase B (v3.111.12): 구조적 수정 — ExtraTurn canonical API 마이그레이션, Plan 가드 push-down, FindAnyAttackAbility 보강. **개요 포함, 착수 전 별도 세션에서 상세화**.
- Phase C (v3.112.x): 튜닝 준비 — HideScore 정규화, FindMelee/FindRetreat 축 통합, threat range 캐시. **개요만**.
- Phase D (장기): 테스트 하네스, CombatAPI 분리, WORK_TRACKER 업데이트. **개요만**.

**Tech Stack:** C# .NET Framework 4.8.1, Unity Mod Manager, Harmony, MSBuild

**리뷰 근거:**
- 세션 내 3-way 병렬 code review (2026-04-22) 결과
- Reviewer 1: Phase 1-6 마스터 플랜 (Critical 2 / Important 6)
- Reviewer 2: v3.111.3~10 버그 수정 시리즈 (Critical 5 / Important 7)
- Reviewer 3: 미커밋 InfluenceMap 제거 (빌드 성공, Important 3)

---

## Phase A: v3.111.11 — InfluenceMap 제거 마무리 + Dead Code 정리

**완료 조건:**
- [ ] 미커밋 변경사항(3 파일 삭제, 2 파일 수정)이 커밋됨
- [ ] `W_SAFETY` dead constant 제거
- [ ] `retreatSafetyGain` dead variable + 로그 노이즈 제거
- [ ] `Info.json` 버전 `3.111.10` → `3.111.11`
- [ ] MSBuild 재빌드 성공
- [ ] 메모리 노트 업데이트 (Phase 5 실제 완료 이력 정정)

**예상 소요:** 20-30분

---

### Task A.1: `W_SAFETY` dead constant 제거

**Why:** 미커밋 변경으로 이 상수를 사용하던 두 지점(score 반영, retreatSafetyGain 계산)이 모두 삭제됨. 선언만 남아 있어 "미사용 코드/변수 정리" 원칙 위반.

**Files:**
- Modify: `Planning/TacticalOptionEvaluator.cs:105-106`

**Step 1: 확인**

Read `Planning/TacticalOptionEvaluator.cs` 105-106줄:
```csharp
// 안전도 가중치 (InfluenceMap 기반)
private const float W_SAFETY = 15f;
```

**Step 2: 제거**

주석 + 상수 2줄을 모두 삭제. 인접한 `W_HITTABLE_IMPROVEMENT` (line 104)와 `W_MOVE_COST` (line 108) 사이의 빈 줄 구조 유지.

**Step 3: grep 재확인**

```bash
Grep pattern="W_SAFETY" in "Planning/TacticalOptionEvaluator.cs"
```
Expected: 0 matches (다른 곳에서 사용 없음 재검증)

---

### Task A.2: `retreatSafetyGain` dead variable 제거

**Why:** 항상 0인 변수. 로그 `safetyGain=0`도 노이즈. 

**Files:**
- Modify: `Planning/TacticalOptionEvaluator.cs:494-512`

**Step 1: 변경 전**

```csharp
// 스코어 = 공격 가치 + 후퇴 안전 이득
float attackScore = currentHittable * W_HITTABLE + W_ATTACK_BASE;

// ★ v3.110.16: InfluenceMap threat/ctrl 제거 (Y축 좌표 버그로 항상 0이었음)
//   후퇴 안전도는 FindRetreatPositionSync가 PositionScore로 직접 평가하므로 여기서 추정 불필요.
float retreatSafetyGain = 0f;

option.Score = attackScore + retreatSafetyGain + mpRecoveryBonus;

// ... (Overwatch penalty 로직은 유지) ...

option.Reason = $"hittable={currentHittable}, safetyGain={retreatSafetyGain:F0}, mpRecov={hasPostActionMPRecovery}";
```

**Step 2: 변경 후**

```csharp
// 스코어 = 공격 가치 + MP 회복 보너스
// ★ v3.111.11: InfluenceMap 제거 후 FindRetreatPositionSync가 PositionScore로 직접 평가하므로
//   여기서 safety gain 추정 불필요.
float attackScore = currentHittable * W_HITTABLE + W_ATTACK_BASE;

option.Score = attackScore + mpRecoveryBonus;

// ... (Overwatch penalty 로직은 유지) ...

option.Reason = $"hittable={currentHittable}, mpRecov={hasPostActionMPRecovery}";
```

변경 3곳:
- line 494 주석 업데이트
- line 497-499 `retreatSafetyGain = 0f` 선언 삭제
- line 501 `option.Score` 수식 단순화
- line 512 `option.Reason`에서 `safetyGain=...` 제거

---

### Task A.3: MovementPlanner 주석 정리 (선택)

**Why:** `// ★ v3.4.02: P1 수정 - situation 전달하여 InfluenceMap 활용` 주석이 이제 잘못된 설명. 유지 시 혼란.

**Files:**
- Modify: `Planning/Planners/MovementPlanner.cs:246`

**Step 1: 변경**

line 246의 주석:
```csharp
// ★ v3.4.02: P1 수정 - situation 전달하여 InfluenceMap 활용
```
→ 삭제 (바로 윗줄 `// ★ v3.1.28: 능력 정보 전달하여 범위 내 착지 위치 찾기`가 현재 목적을 이미 설명).

**Skip 조건:** 시간 없으면 v3.111.12에서 일괄 처리해도 무방.

---

### Task A.4: Info.json 버전 범프

**Files:**
- Modify: `Info.json`

**Step 1: 변경**

```json
"Version": "3.111.10",
```
→
```json
"Version": "3.111.11",
```

---

### Task A.5: 빌드 검증

**Step 1: Release 재빌드**

```powershell
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo
```

Expected: 경고 0, 오류 0. 출력 경로 `%LOCALAPPDATA%Low\Owlcat Games\Warhammer 40000 Rogue Trader\UnityModManager\CompanionAI_v3\CompanionAI_v3.dll`에 dll 갱신.

**Step 2: 실패 시 대응**

- `W_SAFETY` 참조 누락 감지 → grep으로 재확인
- `retreatSafetyGain` 변수 참조 누락 감지 → 해당 라인 Read로 확인

---

### Task A.6: 커밋

**Step 1: git status로 변경 파일 확인**

```bash
git status --short
```

Expected 변경사항 (9 lines net):
- `D Analysis/BattlefieldInfluenceMap.cs`
- `D Analysis/EnemyMobilityAnalyzer.cs`
- `D Analysis/PredictiveThreatMap.cs`
- `M Info.json`
- `M Planning/Planners/MovementPlanner.cs`
- `M Planning/TacticalOptionEvaluator.cs`

**Step 2: 커밋**

```bash
git add -A Analysis/BattlefieldInfluenceMap.cs Analysis/EnemyMobilityAnalyzer.cs Analysis/PredictiveThreatMap.cs Info.json Planning/Planners/MovementPlanner.cs Planning/TacticalOptionEvaluator.cs

git commit -m "refactor(v3.111.11): InfluenceMap 제거 마무리 — 분석 3클래스 + W_SAFETY dead code 정리

Phase D+E of luminous-wobbling-eclipse plan:
- Delete BattlefieldInfluenceMap.cs (962 lines), EnemyMobilityAnalyzer.cs (444),
  PredictiveThreatMap.cs (436). Y축 좌표 버그로 항상 0이었음.
- Remove situation.InfluenceMap/PredictiveThreatMap from MovementPlanner
  FindMeleeAttackPositionSync/FindRangedAttackPositionSync/FindRetreatPositionSync
  call sites (6 places).
- Remove TacticalOptionEvaluator dead code: W_SAFETY constant, retreatSafetyGain
  variable, safetyGain=0 log noise.

Replaced by: PositionScore 5축 (HideScore, EnemyTurnThreatSum, StayingAwayBonus,
PriorityTarget, CoverScore) via EvaluatePosition/FindRetreatPositionSync direct scoring.

Total: -1875 / +8 lines. Build: clean."
```

**주의:** `.claude/` 디렉토리나 수많은 `.md` 노트 파일, `.zip` 아카이브는 스테이징하지 말 것. 명시적 파일만 add.

**Step 3: 확인**

```bash
git log --oneline -1
git status
```
Expected: 새 커밋 HEAD, working tree에는 `.md`/`.zip` 등 untracked만 남음.

---

### Task A.7: 메모리 노트 업데이트

**Why:** 리뷰어 1 Critical #1 — 메모리의 "Phase 전부 구현 완료" 표기가 v3.111.0 Phase 5 실패를 은폐. 정직한 이력으로 정정.

**Files:**
- Modify: `C:\Users\veria\.claude\projects\c--Users-veria-Downloads-CompanionAI-v3-master---v3-5-7\memory\MEMORY.md`
- Modify: `influence_map_removal_progress.md` (해당 메모리 파일 내용)

**Step 1: MEMORY.md 버전/상태 갱신**

기존:
```
- 현재 버전: **3.111.10** (...) — Phase 1-6 + 이슈 1-4 + Hybrid ExtraTurn 감지
- **✅ 완료된 마스터 플랜**: [게임 API 전면 채택](...) — 6 Phase 전부 구현 완료 (v3.110.19 ~ v3.111.2)
```

변경:
```
- 현재 버전: **3.111.11** (...) — Phase 1-6 + 이슈 1-4 + Hybrid ExtraTurn 감지 + InfluenceMap 제거 마무리
- **⚠️ 마스터 플랜 재평가**: [게임 API 전면 채택](...) — Phase 1,2,3,4,6 완료. **Phase 5는 v3.111.0에서 failed (데드락, 0% 효과), v3.111.3에서 EnemyMoveCache Harmony 방식으로 재구현하여 실제 완료**. 상세: docs/plans/2026-04-22-post-phase6-roadmap.md
```

**Step 2: `influence_map_removal_progress.md` 완료 처리**

v3.110.17 Phase D+E가 v3.111.11로 완료됨을 기록.

---

## Phase B: v3.111.12 — 구조적 수정 (상세 플랜은 별도 세션에서)

**완료 조건:**
- [ ] ExtraTurn 감지를 `Initiative.InterruptingOrder` 기반으로 마이그레이션
- [ ] `ExtraTurnPatch.cs` + hybrid threshold 제거
- [ ] Plan별 IsExtraTurn 가드를 MovementPlanner 내부로 push-down
- [ ] `FindAnyAttackAbility`의 `ability.Name` 접근 7곳을 try/catch로 보강

**예상 소요:** 4-6시간 (별도 세션)

### Task B.1: ExtraTurn canonical API 마이그레이션

**근거:** 디컴파일 `TurnController.cs:896,956,1002,1047`이 `unit.Initiative.InterruptingOrder > 0`을 ExtraTurn 판정에 사용. `TurnOrderQueue.cs:252`에서 `StartUnitTurnInternal` 이전에 설정. **Harmony 불필요**.

**접근:**
1. `Analysis/SituationAnalyzer.cs:144` (Reset → AnalyzeUnitState 라인): 
   ```csharp
   situation.IsExtraTurn = unit.Initiative?.InterruptingOrder > 0;
   ```
2. `GameInterface/ExtraTurnPatch.cs` 완전 삭제 또는 `GrantedAP/MP`만 capture (현재 미사용이면 삭제).
3. `Analysis/SituationAnalyzer.cs:146`의 하이브리드 threshold (`CurrentAP <= 2f && CurrentMP <= 5f`) 삭제.
4. `ExtraTurnCache.Clear()` 호출부 유지하되 cache 자체 제거 가능 여부 확인.

**검증:**
- Pascal/Abelard/Idira ExtraTurn 시나리오에서 `IsExtraTurn = true` 정확 감지
- 일반 턴(ExtraTurn 아님)에서 `IsExtraTurn = false` 확정 (false positive 0%)

### Task B.2: Plan 가드 push-down

**근거:** 현재 8곳 sprinkle (TankPlan, SupportPlan, OverseerPlan, DPSPlan 전체).

**접근:**
- `Planning/Planners/MovementPlanner.cs:922` (`PlanRetreat`), `1201` (`PlanPostActionSafeRetreat`), `1302` (`PlanTacticalReposition`) **함수 상단**에 `if (situation.IsExtraTurn) return null;` 가드 추가.
- Plan별 sprinkle된 8곳 guard 제거:
  - `TankPlan.cs:744`
  - `SupportPlan.cs:329`
  - `OverseerPlan.cs:742,1088,1192,1331`
  - `DPSPlan.cs:194,211,709,1071`

**검증:**
- v3.111.8/9가 해결한 Pascal/Abelard/Idira 저자원 턴에서 동일 정상 동작 유지 (regression 없음)

### Task B.3: `FindAnyAttackAbility` 보강

**근거:** `CombatAPI.cs:1847-1985` — Psyker LocalizedString 예외가 `ability.Name` 접근 시 전체 scan abort. v3.111.7은 새 헬퍼(`TryFindDirectionalAoEPrimaryAttack`)만 우회. 기존 함수 + 다른 호출부 (`MovementAPI.cs:1202,1208`, `CombatAPI.cs:452,457,1539`, `AttackPlanner.cs:477`)는 여전히 취약.

**접근:**
- `FindAnyAttackAbility` 루프 내부를 per-ability try/catch로 감싸기 (v3.111.7 패턴)
- `ability.Name` 접근 지점 전체를 `BlueprintCache.cs:144-153` 패턴으로 guard (`TryGetLocalizedName` 헬퍼 추출 고려)

**검증:**
- debug 모드 ON + Psyker 포함 전투에서 전체 scan 정상 완료
- 기존 비-Psyker 시나리오 regression 없음

---

## Phase C: v3.112.x — 튜닝 준비

**완료 조건:** 사용자 인게임 튜닝 세션 전에 스코어 축이 설계대로 동작.

**예상 소요:** 4-8시간 (여러 세션 분산)

### Task C.1: HideScore 정규화
- **문제:** `MovementAPI.cs:220-225`의 `HideValue × 10f`가 unbounded. Invisible 적 10명 시 100점 → TotalScore 지배.
- **수정:** `HideValue / validCount * 10f`로 평균화 or `Min(HideValue, enemyCount) * 10f`로 상한.
- **검증:** 엄폐 많은 맵 + 적 다수 시나리오에서 HideScore가 TotalScore의 15~25% 범위.

### Task C.2: `FindMeleeAttackPositionSync`에 Phase 1-6 축 통합
- **문제:** `MovementAPI.cs:1489-1496` — HideScore/EnemyTurnThreatSum/StayingAwayBonus/CoverScore 전부 0. 근접 유닛이 Phase 1-6 개선을 못 받음.
- **수정:** 최소한 HideScore + EnemyTurnThreatSum (방어 축)은 근접에서도 계산. StayingAwayBonus는 근접 특성상 0 유지 OK, 주석 명시.

### Task C.3: `FindRetreatPositionSync`에 `StayingAwayBonus` 연결
- **문제:** `MovementAPI.cs:1718-1757` — StayingAwayBonus=0이라 Phase 4의 "Retreat → 40f 가중치"가 dead weight.
- **수정:** Retreat 경로의 각 candidate tile에 대해 `GetStayingAwayScore` 호출 + score.StayingAwayBonus 설정.

### Task C.4: `GetEnemyThreatRangeInTiles` 턴별 캐시
- **문제:** `CombatAPI.cs:840-875` — 400타일 × 8적 = 3,200회/scan reflection.
- **수정:** `Dictionary<BaseUnitEntity, int>` 턴별 캐시. `CombatCache.ClearAll()`의 턴 시작 hook에 무효화 등록.

### Task C.5 (선택): HideValue rename
- `PositionScore.HideValue` → `HideCoverSum` (unbounded 특성 명시).

---

## Phase D: 장기 — 프로세스 & 구조

**완료 조건:** 재발 방지 + 유지보수성 향상.

### Task D.1: 최소 테스트 하네스
- `Tests/` 디렉토리 신설.
- 우선순위: ExtraTurn 감지 (4 role × 각 시나리오), TurnPlan.NeedsReplan 불변식, CombatAPI 거리/패턴 유틸.
- 목적: v3.111.0-class "배포 후 broken" 방지.

### Task D.2: `CombatAPI.cs` 분리
- 6000+줄 god-file → `AbilityDetection.cs` / `TargetingValidation.cs` / `RangeProfile.cs` / `PositioningHelpers.cs` 등으로 책임별 분리.
- Psyker Cone 감지 중복이 자연스럽게 드러나 재발 방지.

### Task D.3: `ActionExecutor` 재검증 확장
- `ActionExecutor.cs:236-239`의 Attack/Debuff/Buff-적대 커버를 Heal/Support/Special까지 확장.
- ActionType 대신 target-shape (`targetUnit != null && targetUnit != caster`)로 판별.

### Task D.4: WORK_TRACKER.md 완료 판정 기준 업데이트
- "빌드 클린 + 저자 훑어보기"를 "런타임 로그 트레이스 확인"으로 격상.
- v3.111.0 Phase 5 failure 같은 "선언-현실 gap" 방지.

### Task D.5: `EnemyMoveCache`/`ExtraTurnCache` 전투 시작 훅
- `GameInterface/TurnEventHandler.cs:181-202`에 combat start Clear 명시 추가 (현재는 end만).
- defense-in-depth.

### Task D.6: LESSONS_LEARNED.md 업데이트
- Lesson #14: "canonical game API 먼저 찾기 — 디컴파일 5분 grep이 Harmony 하이브리드 3회 반복보다 가치."
- Lesson #15: "Phase X 완료 선언 전 실제 런타임 로그에서 효과 확인."

---

## 실행 권장 순서

1. **즉시**: Phase A (Task A.1 ~ A.7) — 이번 세션 또는 다음 짧은 세션.
2. **다음 세션 (집중)**: Phase B.1 (ExtraTurn API) — 제일 큰 구조적 승리, 5시간 투자 대비 향후 false positive 0.
3. **그 후**: Phase B.2 → Phase B.3 → Phase C (C.1, C.2, C.3, C.4 순서).
4. **장기 백로그**: Phase D.

## 참조

- 리뷰 결과 요약: 세션 내 3-way 병렬 agent report (2026-04-22)
- 원 플랜: `docs/plans/2026-04-21-game-api-adoption-master-plan.md`
- 디컴파일: `C:\Users\veria\Downloads\roguetrader_decompile\project`
  - `Code\Kingmaker\Controllers\TurnBased\TurnController.cs:896,956,1002,1047,1544-1559`
  - `Code\Kingmaker\Controllers\TurnBased\IInterruptTurnStartHandler.cs`
  - `Code\Kingmaker\Controllers\TurnBased\TurnOrderQueue.cs:223-254`
  - `Code\Kingmaker\AI\AreaScanning\TileScorers\ProtectionTileScorer.cs`
  - `Code\Kingmaker\AI\AreaScanning\TileScorers\AttackEffectivenessTileScorer.cs`
  - `Code\Kingmaker\UnitLogic\Parts\UnitPartPriorityTarget.cs`
