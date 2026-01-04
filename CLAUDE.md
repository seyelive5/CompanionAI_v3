# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

---

# CompanionAI v3.5 - Warhammer 40K Rogue Trader AI Mod

## 프로젝트 개요
- **언어**: C# (.NET Framework 4.8.1)
- **타입**: Unity Mod Manager 기반 게임 모드
- **목적**: 동료 AI 완전 대체 - TurnPlanner 중심 아키텍처

## 핵심 설계 원칙
1. **TurnPlanner가 두뇌**: 모든 결정은 TurnPlanner가 담당
2. **단일 진입점**: MainAIPatch 하나만 게임과 통신
3. **게임 AI는 실행만**: 게임은 우리 결정을 실행하는 역할
4. **무한 루프 방지**: TurnState에서 중앙화된 추적
5. **팀 협동 (v3.5+)**: TeamBlackboard로 팀 전체 상태 공유

## 폴더 구조
```
CompanionAI_v3/
├── Core/           - 중앙 컨트롤러 (TurnOrchestrator, TurnState, TurnPlan)
├── Analysis/       - 상황 분석 (SituationAnalyzer, TargetScorer, ClusterDetector)
├── Planning/       - 전략 기획 (TurnPlanner, DPSPlan, TankPlan, SupportPlan)
├── Execution/      - 행동 실행 (ActionExecutor)
├── Data/           - 데이터 (AbilityDatabase, AbilityInfo)
├── GameInterface/  - 게임 연동 (CombatAPI, MainAIPatch, MovementAPI)
├── Settings/       - 설정 (ModSettings, AIConfig, UnitSettings)
├── UI/             - Unity Mod Manager UI (MainUI)
└── Coordination/   - 팀 협동 (TeamBlackboard, RoleDetector)
```

## 빌드 명령

**⚠️ 중요: Visual Studio 2018 (버전 18) 사용**

```powershell
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo
```

**빌드 출력 경로**: `C:\Users\veria\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\UnityModManager\CompanionAI_v3\`
(csproj에서 UMM 폴더로 직접 출력 설정됨)

## 릴리즈 배포 규칙

**⚠️ 중요: zip 파일에는 dll + Info.json만 포함**

```powershell
# 임시 폴더에 dll, Info.json만 복사 후 압축
# 절대로 사용자 설정 파일(settings.json, aiconfig.json)을 포함하지 말 것!
```

배포 파일:
- `CompanionAI_v3.dll` - 필수
- `Info.json` - 필수
- `settings.json` - ❌ 포함 금지 (사용자 설정)
- `aiconfig.json` - ❌ 포함 금지 (자동 생성됨)

## 아키텍처 흐름
```
MainAIPatch (진입점)
    ↓
TurnOrchestrator.ProcessTurn()
    ↓
SituationAnalyzer.Analyze() → Situation 생성
    ↓
TurnPlanner.CreatePlan() → TurnPlan 생성
    ↓
ActionExecutor.Execute() → ExecutionResult 반환
    ↓
MainAIPatch → 게임에 결과 전달
```

## 핵심 컴포넌트

### TurnOrchestrator (Core/TurnOrchestrator.cs)
- 싱글톤 패턴
- ProcessTurn()이 메인 진입점
- TurnState 관리 (유닛별 턴 상태)
- 안전 장치 (최대 행동 수, 연속 실패 체크)

### TurnPlanner (Planning/TurnPlanner.cs)
- **실제 두뇌!** 모든 전략 결정
- Phase 기반 우선순위:
  1. Emergency Heal (HP < 30%)
  2. Reload (탄약 없음)
  3. Retreat (원거리인데 위험)
  4. Buff (선제 버프)
  5. Move (공격 불가 시)
  6. Attack (핵심 행동)
  7. Post-action (Run and Gun 등)
  8. End Turn

### SituationAnalyzer (Analysis/SituationAnalyzer.cs)
- 현재 전투 상황 스냅샷 생성
- 유닛 상태, 적/아군, 무기/탄약, 능력 분류

### AbilityDatabase (Data/AbilityDatabase.cs)
- GUID 기반 능력 식별 (다국어 호환)
- AbilityTiming enum으로 사용 시점 분류
- 미등록 능력은 휴리스틱 추론

---

# Claude 행동 방침

## ⚠️ 새 세션 시작 시 필수 작업

**새 대화 세션이 시작되면 반드시:**
1. 전체 코드베이스 구조 파악 (폴더별 역할 이해)
2. 최근 변경사항 확인 (git log, 주요 파일 버전 주석)
3. 현재 게임 로그 확인 (문제 있는지 파악)
4. LESSONS_LEARNED 섹션 전부 읽기

**왜?**
- 이전 세션의 컨텍스트가 없으면 같은 실수 반복
- 게임 메커니즘을 모르면 잘못된 해결책 제시
- 부분만 보면 전체 아키텍처 파괴

---

## 핵심 원칙: "나무를 보지 말고 숲을 봐라"

### v3 특화 지침
- TurnPlanner가 중심! 다른 컴포넌트는 보조 역할
- 새 기능 추가 시 TurnPlanner에서 시작
- 무한 루프 방지는 TurnState에서 중앙 관리
- 이동 로직은 게임 AI에 위임 (복잡한 pathfinding)

### 적극적 문제 해결
- 질문의 근본 원인까지 파악
- 더 나은 솔루션 주도적 제안
- 복잡한 리팩토링도 거리낌 없이 진행
- 표면적 증상이 아닌 구조적 문제 해결

### 완전한 구현
- 분석 → 설계 → 구현 → 테스트 한 번에
- 여러 파일 동시 수정 OK
- 아키텍처 개선 적극 제안
- 관련된 모든 파일 함께 업데이트

### 금지 사항
- 쉬운 해결책은 객관적으로 정말로 이게 가장 최고의 선택이라고 판단될 때만 제시
- "나중에 하세요" 같은 미루기 금지
- 부분적 수정 대신 완전하고 전체적인 해결
- 임시방편/땜빵 코드 작성 금지

---

## ⚠️ 핵심 교훈: 절대 AP로 턴 시작을 감지하지 마라

### 문제 (v3.0.76 이전)
```csharp
// ❌ 잘못된 방법 - AP 기반 턴 시작 감지
if (currentAP >= state.StartingAP) {
    // 새 턴 시작으로 판단
}
```

**왜 실패하는가:**
- `전투 트랜스` 같은 버프가 AP를 증가시킴 (5→6)
- AP 증가를 "새 턴 시작"으로 오인
- TurnState가 반복 재생성 → **무한 루프**

### 해결: 게임 이벤트 시스템 사용 (v3.0.76+)

```csharp
// ✅ 올바른 방법 - 게임 이벤트 구독
public class TurnEventHandler : ITurnStartHandler, ITurnEndHandler, ITurnBasedModeHandler
{
    public void HandleUnitStartTurn(bool isTurnBased) {
        // 게임이 직접 알려줌 - 100% 정확
        TurnOrchestrator.Instance.OnTurnStart(unit);
    }
}
```

### 게임 턴 시스템 핵심 API

| API | 설명 | 용도 |
|-----|------|------|
| `ITurnStartHandler` | 유닛 턴 시작 이벤트 | **턴 상태 초기화** |
| `ITurnEndHandler` | 유닛 턴 종료 이벤트 | 상태 정리 |
| `ITurnBasedModeHandler` | 전투 시작/종료 이벤트 | 전투 상태 초기화 |
| `TurnController.CombatRound` | 현재 전투 라운드 (1부터 시작) | 같은 턴인지 확인 |
| `TurnController.GameRound` | 전역 라운드 카운터 | ❌ CombatRound와 혼동 금지 |
| `Initiative.LastTurn` | GameRound 기반 | ❌ CombatRound와 비교 불가 |

### 핵심 파일 (수정 전 반드시 읽을 것)
- `TurnOrchestrator.cs` - IsNewTurn(), OnTurnStart()
- `TurnEventHandler.cs` - 게임 이벤트 구독
- `AbilityUsageTracker.cs` - ClearForUnit()

---

## ⚠️ AP/MP 소스 규칙 (v3.0.77+)

### 단일 진실 소스: `CombatAPI.GetCurrentAP()` / `GetCurrentMP()`

```csharp
// ✅ 올바른 방법 - 게임 API 직접 사용
float ap = CombatAPI.GetCurrentAP(unit);
float ap = situation.CurrentAP;  // SituationAnalyzer가 게임 API 호출

// ❌ 사용 금지 - 레거시 필드
float ap = turnState.RemainingAP;  // 버프 효과 미반영!
float ap = turnState.StartingAP;   // 턴 시작 시점 스냅샷
```

### 왜?
- **버프 효과**: `전투 트랜스`가 AP 5→6 증가
- **TurnState.RemainingAP**: 턴 시작 시 설정, 이후 **업데이트 안 됨**
- **situation.CurrentAP**: 매번 게임 API 호출 → **실시간 반영**

### 코드 위치별 사용법

| 위치 | 사용할 값 | 이유 |
|------|----------|------|
| TurnPlanner | `situation.CurrentAP` | 계획 수립에 실시간 AP 필요 |
| Plan 클래스들 | `situation.CurrentAP` | 동일 |
| TurnOrchestrator | `CombatAPI.GetCurrentAP()` | Situation 없을 때 |
| TurnState | **사용 안 함** (레거시) | AP 추적 안 함 |

### TurnState의 역할 (v3.0.77+)
- ~~AP/MP 추적~~ → **행동 횟수, 플래그만 추적**
- `ActionCount`, `HasMovedThisTurn`, `HasAttackedThisTurn` 등
- AP/MP는 항상 **게임 API에서 직접 조회**

---

## ⚠️ Hittable 계산 규칙 (v3.0.78+)

### 문제 (v3.0.77 이전)
```csharp
// ❌ 잘못된 방법 - 단일 참조 능력으로 Hittable 계산
var attackAbility = CombatAPI.FindAnyAttackAbility(unit, preference);
// 이 능력이 쿨다운이면 → HittableEnemies = 0 → 공격 스킵!
```

**왜 실패하는가:**
- `일격` 같은 주 공격이 쿨다운
- 다른 공격(`죽음의 속삭임`, 일반 공격)은 사용 가능
- 하지만 Hittable=0이므로 DPSPlan이 공격 루프를 스킵
- 결과: "DPS no targets" 턴 종료

### 해결: 모든 가용 공격으로 Hittable 계산 (v3.0.78+)

```csharp
// ✅ 올바른 방법 - 모든 AvailableAttacks 기준
// 1. AnalyzeAbilities() 먼저 호출 (순서 변경)
// 2. AnalyzeTargets()에서 모든 공격으로 Hittable 체크
foreach (var enemy in situation.Enemies)
{
    foreach (var attack in situation.AvailableAttacks)
    {
        if (CombatAPI.CanUseAbilityOn(attack, targetWrapper, out _))
        {
            situation.HittableEnemies.Add(enemy);
            break;  // 하나라도 공격 가능하면 Hittable
        }
    }
}
```

### 핵심 변경 (SituationAnalyzer.cs)
1. **분석 순서 변경**: `AnalyzeAbilities()` → `AnalyzeTargets()`
2. **Hittable 계산**: 모든 `AvailableAttacks`로 체크
3. **BestTarget 선택**: `UtilityScorer.SelectBestTarget()` 사용
4. **PrimaryAttack 선택 이동**: `BestTarget` 설정 후 선택 (최적 공격 결정)

### 모든 Role에 적용
- DPS, Tank, Support, Balanced 모두 동일한 `HasHittableEnemies` 조건 사용
- SituationAnalyzer 수정으로 모든 Role이 자동으로 혜택 받음

---

## ⚠️ RangeFilter 폴백 규칙 (v3.0.79+)

### 문제 (v3.0.78)
```csharp
// ❌ 문제 상황 - RangeFilter가 유효한 공격을 필터링
// 설정: PreferMelee
// 상황: 일격(근접) 쿨다운, 죽음의 속삭임(원거리) 사용 가능
// 결과: FilterAbilitiesByRangePreference()가 원거리 스킬 필터링
//       → AvailableAttacks에 공격 없음 → Hittable=0 → 턴 종료
```

**왜 문제인가:**
- 선호 무기 타입 공격이 모두 쿨다운일 때
- 비선호 타입 공격(원거리)이 있어도 필터링됨
- DPS가 공격을 못하고 턴 종료

### 해결: RangeFilter 폴백 (v3.0.79+)

```csharp
// ✅ 올바른 방법 - 필터링된 공격으로 못 맞추면 전체 공격으로 재시도
// AnalyzeTargets()에서:
if (situation.HittableEnemies.Count == 0 && allUnfilteredAttacks.Count > filteredAttacks.Count)
{
    Main.Log($"[Analyzer] ★ RangeFilter fallback: trying unfiltered attacks");

    foreach (var enemy in situation.Enemies)
    {
        foreach (var attack in allUnfilteredAttacks)
        {
            if (CombatAPI.CanUseAbilityOn(attack, target, out _))
            {
                situation.HittableEnemies.Add(enemy);
                // 필터링에서 제외된 공격도 AvailableAttacks에 추가
                if (!situation.AvailableAttacks.Contains(attack))
                    situation.AvailableAttacks.Add(attack);
                break;
            }
        }
    }
}
```

### 작동 원리
1. **기본**: RangePreference에 맞는 공격만 사용
2. **폴백 조건**: 필터링된 공격으로 Hittable=0 AND 필터링 안 된 공격 존재
3. **폴백 동작**: 모든 공격(버프/힐/재장전 제외)으로 재검사
4. **결과**: 비선호 타입이라도 공격 가능하면 Hittable에 추가

### 실제 예시
```
Blade (PreferMelee):
- 일격: 쿨다운 (근접)
- 죽음의 속삭임: 사용 가능 (원거리) ← 기존: 필터링됨
- 결과 (v3.0.78): Hittable=0, 턴 종료
- 결과 (v3.0.79): 폴백으로 죽음의 속삭임 추가, 공격 진행
```

---

## ⚠️ 리소스 회복 예측 규칙 (v3.0.98+)

### 문제 (v3.0.97 이전)
```csharp
// ❌ 잘못된 방법 - MP 회복 능력을 계획하지만 예측 안 함
var postAction = PlanPostAction(situation, ref remainingAP);  // 무모한 돌진 계획
// remainingMP는 여전히 0
// Phase 8 이동 체크: MP=0 → 이동 계획 안 함
```

**왜 실패하는가:**
- 무모한 돌진(PostFirstAction)이 MP를 회복해줌
- 하지만 계획 단계에서 remainingMP에 반영 안 됨
- Phase 8 이동 체크 시 MP=0 → 이동 불가로 판단
- 실제 실행 시: 무모한 돌진 → MP=10 획득 → **하지만 이동 계획 없음!**

### 해결: Blueprint에서 직접 회복량 읽기 (v3.0.98+)

```csharp
// ✅ 올바른 방법 - 게임 데이터에서 직접 읽어옴
// CombatAPI.GetAbilityMPRecovery()
var runAction = ability.Blueprint.GetComponent<AbilityEffectRunAction>();
foreach (var action in runAction.Actions.Actions)
{
    if (action is WarhammerContextActionRestoreActionPoints restoreAction)
    {
        // MovePoints 값 직접 읽기
        return restoreAction.MovePoints.Value;
    }
}

// 계획 단계에서 예측
float expectedMP = AbilityDatabase.GetExpectedMPRecovery(postAction.Ability);
if (expectedMP > 0)
{
    remainingMP += expectedMP;  // ★ 이동 계획에 반영
}
```

### 핵심 변경 (v3.0.98)
1. **CombatAPI.GetAbilityMPRecovery()**: Blueprint에서 직접 MP 회복량 읽기
2. **CombatAPI.GetAbilityAPRecovery()**: Blueprint에서 직접 AP 회복량 읽기
3. **GUID 하드코딩 제거**: 게임 데이터 기반으로 자동 감지
4. **모든 Plan에 적용**: DPSPlan, TankPlan, SupportPlan

### 왜 이 방식인가?
- ❌ GUID 하드코딩: 새 능력마다 수동 추가 필요, 유지보수 어려움
- ✅ Blueprint 읽기: 모든 MP/AP 회복 능력 자동 감지

### 추가 수정 (v3.0.99)
**문제**: `remainingMP`가 예측되어도 `situation.CanMove`가 여전히 False
- `situation.CanMove`는 **계획 시작 시점**의 MP를 기반으로 설정
- Phase 6에서 MP 회복을 예측해도 이 값은 업데이트되지 않음

**해결**:
```csharp
// ★ v3.0.99: situation.CanMove는 계획 시작 시점 MP 기준, remainingMP는 예측된 MP 포함
bool canMove = situation.CanMove || remainingMP > 0;

if (!hasMoveInPlan && needsMovement && canMove && remainingMP > 0)
```

### 추가 수정 (v3.1.00)
**문제**: v3.0.99에서 Phase 8 진입 조건은 수정했지만, `PlanMoveToEnemy` 내부에서 `situation.CanMove` 직접 체크

```csharp
// ❌ MovementPlanner.PlanMoveToEnemy() 내부
if (!situation.CanMove) return null;  // 예측된 remainingMP 무시!
```

**로그 증거**:
```
[DPS] Phase 8 check: CanMove=True, MP=6.0  // Phase 8 진입 조건 통과
[DPS] PlanMoveOrGapCloser: forceMove=False, PrefersRanged=True, Distance=16.3m
[DPS] Plan complete: AP=0.0, MP=6.0  // 이동 없이 완료 → PlanMoveToEnemy가 null 반환!
```

**해결**: `bypassCanMoveCheck` 파라미터 추가
```csharp
// ★ v3.1.00: MP 회복 예측 후 situation.CanMove=False여도 이동 가능
bool bypassCanMoveCheck = !situation.CanMove && remainingMP > 0;
var moveOrGapCloser = PlanMoveOrGapCloser(situation, ref remainingAP, forceMove, bypassCanMoveCheck);

// MovementPlanner.PlanMoveToEnemy() 내부
if (!bypassCanMoveCheck && !situation.CanMove) return null;  // bypassCanMoveCheck=true면 스킵
```

### 추가 수정 (v3.1.01)
**문제**: v3.1.00에서 `bypassCanMoveCheck`로 PlanMoveToEnemy 진입했지만, MovementAPI가 실제 게임 MP를 사용

```csharp
// ❌ MovementAPI.FindAllReachableTilesSync() 내부
float ap = maxAP ?? unit.CombatState?.ActionPointsBlue ?? 0f;  // 게임 MP = 0!
if (ap <= 0) return new Dictionary<...>();  // 빈 결과 반환
```

**로그 증거**:
```
[DPS] Phase 8: Trying move (attack planned=True, predictedMP=6.0)
[DPS] PlanMoveOrGapCloser: forceMove=False, bypassCanMove=True  // 진입 성공
[MovementAPI] 아르젠타: No reachable tiles  // 하지만 타일 없음!
[DPS] PlanMoveToEnemy: No safe ranged position found
```

**원인**:
- 무모한 돌진은 **계획만** 되었고 아직 **실행 안 됨**
- MovementAPI.FindAllReachableTilesSync()는 **실제 게임 MP**(0)로 타일 계산
- predictedMP(6.0)가 MovementAPI에 전달되지 않음

**해결**: `predictedMP` 파라미터 체인 추가
```csharp
// ★ v3.1.01: MovementAPI에 predictedMP 전달
// 1. Plan 클래스에서 remainingMP 전달
var moveOrGapCloser = PlanMoveOrGapCloser(situation, ref remainingAP, forceMove, bypassCanMoveCheck, remainingMP);

// 2. MovementPlanner에서 effectiveMP 계산
float effectiveMP = Math.Max(situation.CurrentMP, predictedMP);

// 3. MovementAPI에 전달
MovementAPI.FindRangedAttackPositionSync(unit, enemies, weaponRange, minSafeDistance, effectiveMP);

// 4. FindAllReachableTilesSync에서 사용
var tiles = predictedMP > 0
    ? FindAllReachableTilesSync(unit, predictedMP)  // 예측 MP 사용
    : FindAllReachableTilesSync(unit);              // 기본 동작
```

**수정된 파일**:
- `MovementAPI.cs`: `FindRangedAttackPositionSync`, `FindMeleeAttackPositionSync`, `FindRetreatPositionSync`에 `predictedMP` 파라미터 추가
- `MovementPlanner.cs`: `PlanMoveOrGapCloser`, `PlanMoveToEnemy`에 `predictedMP` 파라미터 추가
- `BasePlan.cs`: `PlanMoveOrGapCloser` 오버로드 추가
- `DPSPlan.cs`, `TankPlan.cs`, `SupportPlan.cs`: Phase 8에서 `remainingMP` 전달

### 추가 수정 (v3.1.02)
**문제**: v3.1.01에서 Move 액션이 정상적으로 계획되지만 실행되지 않음

**로그 증거**:
```
[TurnPlan] - [Move] -> (-14.18, 4.05, -53.33) (Safe attack position)  ← Move 정상 계획!
...
[Orchestrator] 아르젠타: Game AP=0 with 5 actions done - ending turn  ← 하지만 턴 종료!
```

**원인**:
- TurnOrchestrator.ProcessTurn()이 AP=0이면 즉시 턴 종료
- Move 액션은 **MP를 사용**하지만, AP 체크에서 걸림
- 보류 중인 Move 액션이 있어도 실행 전에 턴 종료

**해결**: AP=0이지만 보류 중인 Move + MP 있으면 계속 진행
```csharp
// ★ v3.1.02: Move 액션은 MP를 사용하므로, 보류 중인 Move 액션이 있고 MP가 있으면 계속 진행
float gameAP = CombatAPI.GetCurrentAP(unit);
float gameMP = CombatAPI.GetCurrentMP(unit);
if (gameAP <= 0 && turnState.ActionCount > 0)
{
    bool hasPendingMoveWithMP = false;
    if (turnState.Plan != null && gameMP > 0)
    {
        var pendingAction = turnState.Plan.PeekNextAction();
        if (pendingAction != null && pendingAction.Type == ActionType.Move)
        {
            hasPendingMoveWithMP = true;
            Main.Log($"[Orchestrator] {unitName}: AP=0 but pending Move with MP={gameMP:F1} - continuing");
        }
    }

    if (!hasPendingMoveWithMP)
    {
        return ExecutionResult.EndTurn("No AP remaining");
    }
}
```

**수정된 파일**:
- `TurnOrchestrator.cs`: AP=0 안전장치에 Move 액션 예외 추가

---

## ⚠️ 능력 Available 체크 규칙 (v3.0.94+)

### 문제 (v3.0.93 이전)
```csharp
// ❌ 잘못된 방법 - IsAvailable만 체크
public static List<AbilityData> GetAvailableAbilities(BaseUnitEntity unit)
{
    ...
    if (data.IsAvailable)  // 이것만으로는 쿨다운 필터링 안 됨!
    {
        abilities.Add(data);
    }
    ...
}
```

**왜 실패하는가:**
- `data.IsAvailable`은 기본적인 체크만 수행
- **쿨다운, 탄약 부족, 충전 없음 등을 필터링하지 않음**
- 결과: 쿨다운인 능력도 `AvailableAttacks`에 포함
- TurnPlanner가 쿨다운 능력으로 계획 수립 → 실행 시 실패

**실제 로그:**
```
[Analyzer] Cassia abilities: Attacks=3  // 쿨다운 능력 포함됨
[TurnPlan] Replan needed: Ability 단발 사격 no longer available (IsOnCooldown)
[TurnPlanner] Replanning due to situation change
[Analyzer] Cassia abilities: Attacks=3  // 여전히 쿨다운 능력 포함!
[Attack] 단발 사격 -> 돌격대  // 또 쿨다운 능력 선택
[Executor] Ability unavailable: 단발 사격 - IsOnCooldown
```

### 해결: GetUnavailabilityReasons() 체크 (v3.0.94+)

```csharp
// ✅ 올바른 방법 - GetUnavailabilityReasons() 사용
public static List<AbilityData> GetAvailableAbilities(BaseUnitEntity unit)
{
    ...
    if (!data.IsAvailable) continue;

    // ★ 핵심: GetUnavailabilityReasons()로 실제 사용 가능 여부 체크
    var unavailabilityReasons = data.GetUnavailabilityReasons();
    if (unavailabilityReasons.Count > 0)
    {
        // 쿨다운, 탄약 부족, 충전 없음 등 → 스킵
        continue;
    }

    abilities.Add(data);
    ...
}
```

### 핵심 차이점

| 체크 방식 | 쿨다운 | 탄약 | 충전 | 기타 제한 |
|----------|--------|------|------|----------|
| `data.IsAvailable` | ❌ | ❌ | ❌ | 일부만 |
| `GetUnavailabilityReasons()` | ✅ | ✅ | ✅ | 전부 |

### 효과
- SituationAnalyzer.AnalyzeAbilities() → 쿨다운 능력이 애초에 제외됨
- TurnPlanner → 실제 사용 가능한 능력만으로 계획 수립
- 더 이상 NeedsReplan→Replan→동일 쿨다운 능력 포함 문제 없음

---

## ⚠️ 인위적인 제한 금지 (v3.5.25+)

### 문제 (v3.5.24 이전)
```csharp
// ❌ 잘못된 접근 - 인위적인 숫자 제한
public const int MaxActionsPerTurn = 15;  // 왜 15? 근거 없음
```

**실제 발생한 문제:**
```
Action #12: Debuff (죽음의 맹세)
Action #13: Buff (무모한 결단) → MP 회복
Action #14: Buff (???리?) → MP 추가 회복
Action #15: Move → 적 접근
→ "Max actions reached (15)" 강제 종료
→ 플랜에 남아있던 Attack 실행 못함!
→ AP=2.0, MP=9.0 남아있는데 강제 종료
```

**이건 정상적인 키벨라 플레이:**
- 죽음 강림(갭클로저) 범위 밖 → MP 회복 버프 → 이동 → 공격
- 게임 메커니즘대로 작동 중인데 인위적 제한에 막힘

### 해결: 게임의 자연스러운 제한을 따름 (v3.5.25+)
```csharp
// ✅ 사실상 무제한 - 게임 메커니즘이 알아서 제한
public const int MaxActionsPerTurn = 9999;
```

**게임의 자연스러운 종료 조건:**
- AP=0 AND 공격/스킬 불가
- MP=0 AND 이동 불필요
- 모든 스킬 쿨다운
- 타겟 없음

**이미 있는 안전장치:**
- `ConsecutiveFailures >= 3` → 턴 종료
- `Plan.IsComplete` → 턴 종료
- `gameAP <= 0 && turnState.ActionCount > 0` → 턴 종료 (Move 예외 처리됨)

### 핵심 교훈
- 인위적인 숫자 제한(15개)은 버그를 숨기는 bandaid
- "루프 감지" 같은 휴리스틱도 정상 플레이를 막을 수 있음
- **게임 메커니즘을 신뢰하라**

---

## 참조 리소스
- **게임 디컴파일 소스**: `C:\Users\veria\Downloads\EnhancedCompanionAI (2)\RogueTraderDecompiled-master`

## 게임 API 핵심 사항

### DecisionContext 속성
- `Unit`: 현재 유닛
- `Ability`: 선택된 능력
- `AbilityTarget`: 타겟 (TargetWrapper)
- `IsMoveCommand`: 이동 명령 플래그
- `FoundBetterPlace`: 이동 위치 정보

### Status 반환값
- `Success`: 능력 시전 → TaskNodeCastAbility 실행
- `Failure`: 턴 종료
- `Running`: 계속 진행

### Harmony 패치 포인트
- `TaskNodeSelectAbilityTarget.TickInternal`: 능력/타겟 선택
- `TurnController.IsAiTurn`: AI 턴 판정
- `TurnController.IsPlayerTurn`: 플레이어 턴 판정
- `PartUnitBrain.IsAIEnabled`: AI 활성화 상태
