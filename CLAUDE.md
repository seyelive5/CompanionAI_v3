# CompanionAI v3.0 - Warhammer 40K Rogue Trader AI Mod

## 프로젝트 개요
- **언어**: C# (.NET Framework 4.8.1)
- **타입**: Unity Mod Manager 기반 게임 모드
- **목적**: 동료 AI 완전 대체 - TurnPlanner 중심 아키텍처

## v3.0 핵심 설계 원칙
1. **TurnPlanner가 두뇌**: 모든 결정은 TurnPlanner가 담당
2. **단일 진입점**: MainAIPatch 하나만 게임과 통신
3. **게임 AI는 실행만**: 게임은 우리 결정을 실행하는 역할
4. **무한 루프 방지**: TurnState에서 중앙화된 추적

## 폴더 구조
```
CompanionAI_v3/
├── Core/           - 중앙 컨트롤러 (TurnOrchestrator, TurnState, TurnPlan)
├── Analysis/       - 상황 분석 (SituationAnalyzer, TargetScorer, PositionEvaluator)
├── Planning/       - 전략 기획 (TurnPlanner - 핵심!)
├── Execution/      - 행동 실행 (ActionExecutor)
├── Data/           - 데이터 (AbilityDatabase, AbilityInfo)
├── GameInterface/  - 게임 연동 (CombatAPI, MainAIPatch)
└── Settings/       - 설정 (ModSettings, UnitSettings)
```

## 빌드 명령
```powershell
cd c:\Users\veria\Downloads\CompanionAI_v3
dotnet build -c Release
```

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

## 참조 리소스
- **게임 디컴파일 소스**: `C:\Users\veria\Downloads\EnhancedCompanionAI (2)\RogueTraderDecompiled-master`
- **v2.2 코드베이스**: `C:\Users\veria\Downloads\CompanionAI_v2.2`
- **스킬 데이터**: `C:\Users\veria\Downloads\CompanionAI_v2.2\Rogue_Trader_Skills.csv`
- **게임 로그**: `C:\Users\veria\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\GameLogFull.txt`

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
