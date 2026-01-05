# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

---

# CompanionAI v3.5 - Warhammer 40K Rogue Trader AI Mod

## 프로젝트 개요
- **언어**: C# (.NET Framework 4.8.1)
- **타입**: Unity Mod Manager 기반 게임 모드
- **목적**: 동료 AI 완전 대체 - TurnPlanner 중심 아키텍처

## 빌드 명령

```powershell
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo
```

**빌드 출력**: UMM 폴더로 직접 출력 (`UnityModManager/CompanionAI_v3/`)

## 릴리즈 배포

**zip 파일에는 dll + Info.json만 포함** (settings.json, aiconfig.json 포함 금지)

```powershell
# 예시
Compress-Archive -Path "...\CompanionAI_v3.dll", "Info.json" -DestinationPath "CompanionAI_v3_X.X.X.zip"
gh release create vX.X.X "CompanionAI_v3_X.X.X.zip" --title "..." --notes "..."
```

---

## 핵심 설계 원칙

1. **TurnPlanner가 두뇌**: 모든 결정은 TurnPlanner가 담당
2. **단일 진입점**: CustomBehaviourTree → TurnOrchestrator
3. **게임 AI는 실행만**: 게임은 우리 결정을 실행하는 역할
4. **팀 협동**: TeamBlackboard로 팀 전체 상태 공유

## 아키텍처 흐름

```
CustomBehaviourTree.CompanionAIDecisionNode (진입점)
    ↓
TurnOrchestrator.ProcessTurn()
    ↓
SituationAnalyzer.Analyze() → Situation 생성
    ↓
TurnPlanner.CreatePlan() → TurnPlan 생성
    ↓
ActionExecutor.Execute() → ExecutionResult 반환
    ↓
게임에 결과 전달 (CastAbility / MoveTo / EndTurn)
```

## 폴더 구조

| 폴더 | 역할 | 핵심 파일 |
|------|------|----------|
| Core/ | 중앙 컨트롤러 | TurnOrchestrator, TurnState, TurnPlan |
| Analysis/ | 상황 분석 | SituationAnalyzer, TargetScorer |
| Planning/ | 전략 기획 | TurnPlanner, DPSPlan, TankPlan, SupportPlan |
| Execution/ | 행동 실행 | ActionExecutor |
| GameInterface/ | 게임 연동 | CombatAPI, CustomBehaviourTree, MovementAPI, CombatCache |
| Coordination/ | 팀 협동 | TeamBlackboard, RoleDetector |
| Data/ | 데이터 | AbilityDatabase |
| Settings/ | 설정 | ModSettings, AIConfig |

## 핵심 컴포넌트

### TurnPlanner - Phase 기반 우선순위
1. Emergency Heal (HP < 30%)
2. Reload (탄약 없음)
3. Retreat (원거리인데 위험)
4. Buff (선제 버프)
5. Move (공격 불가 시)
6. Attack (핵심 행동)
7. Post-action (Run and Gun 등)
8. End Turn

### CombatCache (v3.5.31+)
- 거리 캐시: 94% 히트율 (유닛 쌍별)
- 타겟팅 캐시: 46-82% 히트율 (능력-타겟 쌍별)
- `ClearAll()`: 턴 시작 시 (TurnOrchestrator.OnTurnStart)
- `InvalidateTarget()`: 밀치기/이동 스킬 후 (ActionExecutor)

### TeamBlackboard (v3.5+)
- SharedTarget: 팀 집중 타겟 (가장 많이 타겟된 적)
- TacticalSignal: Attack/Defend/Retreat
- 각 유닛의 Situation 집계

---

## Claude 행동 방침

### 핵심 원칙
- TurnPlanner가 중심! 새 기능은 TurnPlanner에서 시작
- 게임 메커니즘을 신뢰하라 (인위적 제한 금지)
- 부분 수정보다 완전한 해결

### 금지 사항
- 임시방편/땜빵 코드
- 인위적인 숫자 제한 (MaxActions 등)
- "나중에 하세요" 미루기

---

## 게임 API 핵심

### 필수 사용 패턴
```csharp
// AP/MP는 항상 게임 API에서 직접 조회
float ap = CombatAPI.GetCurrentAP(unit);  // ✅
float ap = situation.CurrentAP;            // ✅
float ap = turnState.RemainingAP;          // ❌ 레거시

// 능력 사용 가능 여부
var reasons = ability.GetUnavailabilityReasons();  // ✅ 쿨다운/탄약 포함
bool available = ability.IsAvailable;              // ❌ 불완전
```

### 턴 이벤트 (절대 AP로 턴 감지 금지)
```csharp
// ✅ 게임 이벤트 구독
ITurnStartHandler, ITurnEndHandler, ITurnBasedModeHandler
```

### Harmony 패치 포인트
- `PartUnitBrain.UpdateBehaviourTree`: 커스텀 트리 주입
- `TurnController.IsAiTurn/IsPlayerTurn`: AI 턴 판정
- `PartUnitBrain.IsAIEnabled`: AI 활성화 상태

---

## 참조 리소스

- **게임 디컴파일**: `C:\Users\veria\Downloads\EnhancedCompanionAI (2)\RogueTraderDecompiled-master`
- **게임 로그**: `C:\Users\veria\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\GameLogFull.txt`

---

## 과거 교훈 (LESSONS_LEARNED.md 참조)

버그 수정 과정에서 얻은 교훈들은 [LESSONS_LEARNED.md](LESSONS_LEARNED.md) 파일에 정리되어 있습니다.
주요 주제:
- AP 기반 턴 감지 금지 (v3.0.76)
- Hittable 계산 규칙 (v3.0.78)
- 리소스 회복 예측 (v3.0.98~v3.1.02)
- 능력 Available 체크 (v3.0.94)
