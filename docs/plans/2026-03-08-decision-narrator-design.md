# DecisionNarrator — AI 결정 실시간 분석 시스템

**날짜:** 2026-03-08
**버전:** v3.42.0 이후 구현 예정
**목적:** 사용자가 인게임에서 AI의 결정 과정을 자연어로 실시간 확인

---

## 배경

현재 CombatReport 시스템(v3.20.0)은 JSON 파일로 턴 기록을 내보내지만:
- `LogPhase()` 호출이 코드 전체에서 3곳뿐 (Main.Log 1383곳 대비 극소)
- 인게임 UI 없음 — JSON을 직접 열어야 함
- 개발자 용어 그대로 — 사용자가 이해 불가
- AI 턴이 빠르게 지나가서 결정 과정을 놓침

## 요구사항

1. **인게임 실시간 UI** — 포트레이트 근처에 결정 요약 표시
2. **자연어 문장** — "돌연변이를 공격합니다 — 아군 2명을 위협하고 있어서"
3. **AI 턴 자동 일시정지** — on/off 토글, "계속" 버튼으로 재개
4. **턴 히스토리** — 이전 턴 결정을 되돌려 볼 수 있음
5. **사용자용** — 상세(개발자) 모드는 확장 여지만 남김
6. **기존 CombatReport 연동** — 자연어 요약을 JSON에도 기록

## 아키텍처

```
TurnPlanner.CreatePlan(situation)
    -> TurnPlan 생성
    -> DecisionNarrator.Narrate(plan, situation)
        ├─ NarrativeBuilder.Build(plan, situation)     // 자연어 문장 생성
        │     -> "돌연변이를 공격합니다 — 아군 2명을 위협하고 있어서"
        ├─ DecisionHistory.Add(narrative)               // 턴 히스토리 보관
        ├─ CombatReportCollector.LogPhase(narrative)    // 기존 JSON에도 기록
        └─ DecisionOverlayUI.Show(unit, narrative)      // 인게임 UI 표시

ActionExecutor.Execute(action)
    -> DecisionNarrator.NarrateExecution(action, result) // 실행 결과 기록
```

## 새 파일

| 파일 | 역할 |
|------|------|
| `Diagnostics/DecisionNarrator.cs` | 진입점 — Narrate() 호출 시 빌더+UI+리포트 연동 |
| `Diagnostics/NarrativeBuilder.cs` | 자연어 문장 생성 (Plan+Situation -> 사용자 문장) |
| `Diagnostics/DecisionHistory.cs` | 최근 20턴 링 버퍼 히스토리 |
| `UI/DecisionOverlayUI.cs` | IMGUI 오버레이 패널 (좌하단 포트레이트 옆) |

## 기존 파일 변경 (최소)

| 파일 | 변경 |
|------|------|
| `Planning/TurnPlanner.cs` | Plan 생성 후 `DecisionNarrator.Narrate()` 1줄 추가 |
| `Execution/ActionExecutor.cs` | 실행 후 `DecisionNarrator.NarrateExecution()` 1줄 추가 |
| `Core/TurnOrchestrator.cs` | 일시정지 로직 추가 (PauseOnAITurn) |
| `UI/MainUI.cs` | 설정 체크박스 2개 (EnableDecisionOverlay, PauseOnAITurn) |
| `Settings/ModSettings.cs` | 설정 프로퍼티 2개 추가 |

## NarrativeBuilder — 자연어 생성

### ActionType별 템플릿

**공격:**
- "{0}을(를) 공격합니다 — 아군 {1}명을 위협하고 있어서" (CountAlliesTargeting > 0)
- "{0}을(를) 공격합니다 — 처치 가능한 적이라서" (CanKillBestTarget)
- "{0}을(를) 공격합니다 — 가장 가까운 적이라서" (target == NearestEnemy)
- "{0}을(를) 공격합니다 — 가장 위험한 적이라서" (TargetScorer 최고 점수)

**이동:**
- "{0} 방향으로 이동합니다 — 공격 사거리에 들어가기 위해" (MoveToAttack)
- "후퇴합니다 — 적이 너무 가까워서" (Retreat)
- "{0}에게 접근합니다 — 치료하기 위해" (MoveToHeal)

**힐:** "{0}을(를) 치료합니다 — HP {1}%"
**버프:** "{0}에게 {1}을(를) 사용합니다"
**도발:** "적의 관심을 끌어 아군을 보호합니다"
**EndTurn:** "행동을 마칩니다 — {이유}"

### 이유 추출 방법

새 분석 로직 없이 기존 데이터를 번역:
- `PlannedAction.Reason` — 이미 존재하는 영어 사유 문자열
- `situation.CanKillBestTarget`, `NearestEnemy`, `HPPercent`
- `TeamBlackboard.CountAlliesTargeting(target)`
- `TurnPlan.Priority` — Emergency, Retreat 등

## DecisionOverlayUI

### 표시 위치
화면 좌측 하단, 캐릭터 포트레이트 열 바로 오른쪽. IMGUI 반투명 패널.

### 패널 레이아웃
```
+-------------------------------------------+
| [역할아이콘] 아벨라드 (Tank) -- HP 85%     |
|                                           |
| * 적의 관심을 끌어 아군을 보호합니다       |
| * 돌연변이를 공격합니다                    |
|   -- 아군 2명을 위협하고 있어서            |
| * 행동을 마칩니다 -- AP 부족               |
|                                           |
| < 이전 턴  [2/5]  다음 턴 >               |
+-------------------------------------------+
```

### 일시정지
- `PauseOnAITurn` 활성 시: Plan 생성 직후 `Time.timeScale = 0f`
- 패널에 "계속" 버튼 표시
- 클릭 시 `Time.timeScale = 1f` 복원

## 설정

| 설정 | 기본값 | 설명 |
|------|--------|------|
| EnableDecisionOverlay | false | AI 결정 오버레이 표시 |
| PauseOnAITurn | false | AI 턴 자동 일시정지 |

둘 다 기본 off — 기존 사용자 경험 변화 없음.

## 다국어

기존 Localization 시스템 활용. 이유 템플릿을 Localization 키로 관리:
- `narr_attack_threatening` -> EN/KO
- `narr_retreat` -> EN/KO
- 등

## 상세 모드 확장 여지

```csharp
public enum NarrativeLevel
{
    User,       // 현재 구현 -- 자연어 1~3줄
    Developer   // 나중에 -- TargetScorer 점수, AP Budget, 대안 비교 등
}
```

현재는 User만 구현. Developer는 빈 분기로 남김.

## 성능

- `IsEnabled` false면 Narrate() 즉시 반환 (오버헤드 0)
- DecisionHistory: 최근 20턴 링 버퍼 (고정 메모리)
- IMGUI 텍스트만 — 프레임 부하 무시 가능
