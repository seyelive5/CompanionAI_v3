# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

---

# CompanionAI v3 - Warhammer 40K Rogue Trader AI Mod

## 프로젝트 개요
- **언어**: C# (.NET Framework 4.8.1, SDK-style csproj)
- **타입**: Unity Mod Manager 기반 게임 모드 (Harmony 패치)
- **목적**: 동료 AI 완전 대체 - TurnPlanner 중심 아키텍처
- **현재 버전**: Info.json의 `Version` 필드 (코드에 버전 상수 없음)
- **진입점**: `CompanionAI_v3.Main.Load` → Harmony 패치 → `CustomBehaviourTree` → `TurnOrchestrator`

## 빌드 명령

```powershell
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo
```

**빌드 출력**: UMM 폴더로 직접 출력 (`%LOCALAPPDATA%Low\Owlcat Games\Warhammer 40000 Rogue Trader\UnityModManager\CompanionAI_v3\`)

## 릴리즈 배포

**zip 파일에는 dll + Info.json만 포함** (settings.json, aiconfig.json 포함 금지)

```powershell
$version = "X.X.X"
$dllPath = "$env:LOCALAPPDATA\Low\Owlcat Games\Warhammer 40000 Rogue Trader\UnityModManager\CompanionAI_v3\CompanionAI_v3.dll"
$zipPath = "C:\Users\veria\Downloads\CompanionAI_v3_$version.zip"

Compress-Archive -Path $dllPath, "Info.json" -DestinationPath $zipPath -Force
gh release create "v$version" $zipPath --title "v$version" --notes "..."
```

---

## 핵심 설계 원칙

1. **TurnPlanner가 두뇌**: 모든 결정은 TurnPlanner가 담당. 새 기능은 TurnPlanner에서 시작
2. **단일 진입점**: CustomBehaviourTree → TurnOrchestrator → 게임은 실행만
3. **팀 협동**: TeamBlackboard로 팀 전체 상태 공유
4. **게임 메커니즘 신뢰**: 인위적 제한 금지 (MaxActions=9999, 게임이 AP/MP로 자연 제한)

## 아키텍처 흐름

```
Main.Load() → Harmony 패치 등록, TurnEventHandler 구독
    ↓
CustomBehaviourTree.Tick_Prefix() → 게임 BehaviourTree를 커스텀으로 교체
    ↓
CompanionAIDecisionNode 실행
    ↓
TurnOrchestrator.ProcessTurn() [2-Phase Frame Spreading]
    ├─ Frame N (Ready):     SituationAnalyzer.Analyze() → PendingSituation 저장, return Waiting
    └─ Frame N+1 (WaitingForPlan): TurnPlanner.CreatePlan() → ActionExecutor.Execute()
    ↓
게임에 결과 전달 (CastAbility / MoveTo / EndTurn)
```

**2-Phase Frame Spreading** (v3.9.04): Analyze와 Plan+Execute를 별도 프레임에 분산하여 프레임 스터터링 방지

## 폴더 구조

| 폴더 | 역할 | 핵심 파일 |
|------|------|----------|
| Core/ | 중앙 컨트롤러, 데이터 구조 | TurnOrchestrator, TurnState, TurnPlan, PlannedAction |
| Analysis/ | 상황 분석, 전장 평가 | SituationAnalyzer, TargetScorer, PositionEvaluator, ClusterDetector |
| Planning/ | 전략 기획 | TurnPlanner, TacticalOptionEvaluator |
| Planning/Plans/ | 역할별 플랜 | BasePlan(추상), DPSPlan, TankPlan, SupportPlan, OverseerPlan |
| Planning/Planners/ | 행동별 플래너 | AttackPlanner, BuffPlanner, HealPlanner, MovementPlanner |
| Execution/ | 행동 실행 | ActionExecutor |
| GameInterface/ | 게임 연동 | CombatAPI, CustomBehaviourTree, MovementAPI, CombatCache |
| Coordination/ | 팀 협동 (현재 Core에 통합) | TeamBlackboard |
| Data/ | 능력 DB | AbilityDatabase, AbilityInfo, SpecialAbilityHandler |
| Settings/ | 설정 | ModSettings, AIConfig |

---

## 핵심 데이터 구조

### PlannedAction - 단일 행동 단위
```csharp
ActionType: Buff, Move, Attack, Reload, Heal, Support, Debuff, Special, EndTurn
Priority: Heal(1) < Buff(10) < Support(15) < Move(20) < Attack(50) < EndTurn(100)

// v3.8.86: ActionGroup - 연관 행동 묶음 (예: "KillSeq_enemyID")
GroupTag: string          // null이면 독립 행동
FailurePolicy: SkipRemainingInGroup | ContinueGroup

// v3.7.25: 멀티타겟 (AerialRush 등)
AllTargets: List<TargetWrapper>  // 2+ 포인트 타겟

// v3.7.20: 패밀리어 대상은 실행 시점에 재조회 (stale reference 방지)
IsFamiliarTarget: bool
```

### TurnPlan - 턴 전체 전략
- Queue 기반 순차 실행 (`_actionQueue`)
- `TurnPriority`: Critical(-10) > Emergency(0) > Retreat(10) > Reload(20) > BuffedAttack(30) > DirectAttack(40) > MoveAndAttack(50) > Support(60) > EndTurn(100)
- 생성 시점 Situation 스냅샷 저장 (HP, 거리, HittableCount 등)

### TurnState - 턴 동안 유닛별 영속 상태
- `ComputePhase`: Ready → WaitingForPlan (2-Phase 제어)
- 행동 플래그: HasMovedThisTurn, HasAttackedThisTurn 등
- 안전장치: ConsecutiveFailures, StagnantPlanCount, FallbackReplanCount
- `StrategicContext`: Dictionary - replan 간에도 보존되는 전략 컨텍스트 (v3.8.86)

---

## 역할(Role) 시스템

### RoleDetector - 능력 기반 자동 역할 감지
- **Tank**: 도발, 방어 태세, 갭클로저, 근접 무기
- **DPS**: 피니셔, Heroic Act, 피해 강화, 연쇄 효과
- **Support**: 치유, 팀 버프, 디버프
- **Overseer**: 패밀리어/펫 보유 시 최우선 (점수 ≥10이면 다른 역할 오버라이드)
- 폴백: DPS
- Auto 모드에서 전투 중 역할 고정 (oscillation 방지)

### 플랜 상속 구조
```
BasePlan (추상, ~3000줄, 90+ protected helper 메서드)
├── DPSPlan    → _dpsPlan (싱글턴, 재사용)
├── TankPlan   → _tankPlan
├── SupportPlan → _supportPlan
└── OverseerPlan → _overseerPlan
```

### Phase 실행 순서 (모든 플랜 공통)
```
Phase 0:   Transcend Potential Ultimate (무료 사용 가능 시)
Phase 1:   Emergency Heal (HP < 30%)
Phase 1.5: Reload (탄약 없음)
Phase 1.75: Familiar Support (Overseer/Tank/Support)
Phase 2:   Heroic Act / Buffs
Phase 3:   Taunt (Tank)
Phase 4:   Tactical Evaluation → Move/Attack/Retreat combo
Phase 4.3: Self-targeted AoE (Blade Dance)
Phase 4.4: Point-target AoE
Phase 5:   Post-action abilities (Run and Gun)
Phase 6:   End Turn
```

---

## Replan 메커니즘 (TurnPlan.NeedsReplan)

**Section 1 - 실행 차단 (반드시 replan)**:
- 능력 사용 불가 (쿨다운, 탄약 등)
- 타겟 공격 불가 (LOS, 사거리, 면역)
- 타겟 사망 / 전체 적 사망

**Section 2 - 긴급 상황 (Priority ≠ Emergency일 때 replan)**:
- HP 20% 이상 하락
- 원거리 캐릭터: 적이 안전 거리 내 진입

**Section 3 - 새 기회 (선택적 replan, Critical 턴 제외)**:
- MP/AP 증가 (Run and Gun, Combat Trance)
- 새 공격 가능 타겟 출현
- 0-AP 공격 사용 가능 (Break Through → Slash 체인)
- Hittable 타겟 2+ 증가

**안전장치**:
- `StagnantPlanCount` (v3.9.14): AP 소모 없이 3회 반복 시 강제 EndTurn
- `FallbackReplanCount`: 실패 후 replan 횟수 제한
- `EmptyPlanEndCount` (v3.9.06): 큐가 예상 외로 비면 안전 EndTurn

---

## 게임 API 핵심 패턴

### 필수 사용 패턴
```csharp
// AP/MP: 항상 게임 API에서 직접 조회
float ap = CombatAPI.GetCurrentAP(unit);  // ✅
float ap = situation.CurrentAP;            // ✅
float ap = turnState.RemainingAP;          // ❌ [Obsolete] 레거시

// 능력 사용 가능 여부: IsAvailable + IsRestricted 모두 체크
var reasons = ability.GetUnavailabilityReasons();  // 쿨다운/탄약
bool restricted = ability.IsRestricted;            // WarhammerAbilityRestriction 등
// ⚠️ GetUnavailabilityReasons()만으로는 IsRestricted를 잡지 못함 (Lesson #12)

// 턴 감지: 절대 AP 기반 금지
ITurnStartHandler, ITurnEndHandler  // ✅ 게임 이벤트 구독
```

### 거리/범위 단위: 타일 기준 통일 (Lesson #10)
```csharp
// 1 타일 = 1.35 미터 (GridCellSize)
CombatAPI.GetDistanceInTiles(a, b)        // ✅ 타일
CombatAPI.GetAbilityRangeInTiles(ability) // ✅ 타일 (= ability.RangeCells)
CombatAPI.GetAoERadius(ability)           // ✅ 타일
Vector3.Distance(a, b)                    // ⚠️ 미터 → MetersToTiles() 변환 필요
```

### AOE 패턴 검증 (Lesson #11)
```csharp
// 패턴 타입별 높이 제한이 다름 - "AOE니까 Circle" 추측 금지
PatternType.Circle → 1.6m | PatternType.Ray/Cone/Sector → 0.3m (Directional)
// 반드시 CombatAPI.GetPatternType(ability)로 확인
```

### Harmony 패치 포인트
- `PartUnitBrain.Tick`: 커스텀 BehaviourTree 주입 (Reflection 캐싱으로 최적화)
- `TurnController.IsAiTurn/IsPlayerTurn`: AI 턴 판정
- `PartUnitBrain.IsAIEnabled`: AI 활성화 상태

---

## 성능 최적화 패턴

### CombatCache
- 거리 캐시: 94% 히트율 (유닛 쌍별)
- 타겟팅 캐시: 46-82% 히트율 (능력-타겟 쌍별)
- `ClearAll()`: 턴 시작 시
- `InvalidateCaster()`: 이동 후 / `InvalidateTarget()`: 밀치기 후

### Zero-Allocation 설계
- BasePlan: 정적 `_tempAbilities`, `_tempUnits`, `_tempActions` 리스트 재사용
- LINQ `.ToList()` 할당 방지

### CustomBehaviourTree Reflection 캐싱 (v3.8.48)
- `_lastKnownTree` 딕셔너리로 확인된 커스텀 트리 캐시
- ~300 reflection/sec → ~0

---

## 금지 사항

- 임시방편/땜빵 코드
- 인위적인 숫자 제한 (MaxActions 등) - 게임 메커니즘 신뢰
- "나중에 하세요" 미루기
- 스킬 처리시 String 텍스트 기반 매칭 절대 금지 - GUID 기반 우선
- 추측과 추정이 아닌 정확한 계산
- 다 끝나지 않았는데 다 된 것처럼 잘난척 금지
- 거짓말 금지
- 부분 수정 대신 완전하고 전체적인 해결

## 행동 원칙

- **"나무를 보지 말고 숲을 봐라"**: 표면적 증상이 아닌 구조적 문제 해결
- **완전한 구현**: 분석 → 설계 → 구현 한 번에. 여러 파일 동시 수정 OK
- **추측보다 조사 우선**: 게임 디컴파일 소스 적극 활용
- **사이드이펙트 철저히 검토**: 전체 코드베이스 맥락에서 문제 파악
- **중복 코드 발견 시 즉시 리팩토링**, 미사용 코드 정리

---

## 참조 리소스

- **게임 디컴파일**: `C:\Users\veria\Downloads\roguetrader_decompile\project`
- **게임 로그**: `C:\Users\veria\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\GameLogFull.txt`
- **과거 교훈**: [LESSONS_LEARNED.md](LESSONS_LEARNED.md) - AP 턴 감지, Hittable 계산, 능력 Available 체크, 거리 단위, AOE 패턴 등
