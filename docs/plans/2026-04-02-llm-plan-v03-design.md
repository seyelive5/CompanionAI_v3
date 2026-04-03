# LLMPlan v0.3 — 행동 메뉴 기반 LLM 전투 AI 설계서

> v0.1 교훈: LLM이 직접 GUID/타겟 선택 → 게임 메커니즘 모르니 실패
> v0.2 교훈: LLM이 전략 힌트 → Plan이 자율적이라 무시됨
> v0.3 방향: 코드가 "검증된 행동 메뉴"를 만들고, LLM이 순서를 정하고, 코드가 실행
> 확정일: 2026-04-02

---

## 핵심 아이디어

```
기존 코드: "이 유닛이 할 수 있는 행동 목록" 생성 (AP/사거리/제한 사전 검증)
LLM:       "어떤 행동을 어떤 순서로, 누구를 대상으로" 결정
기존 코드: 각 행동별 최적 스킬/위치 자동 선택 → ActionExecutor 실행
```

LLM은 게임 메커니즘을 몰라도 됨. 코드가 만든 메뉴에서 고르기만 하면 됨.

---

## 확정된 결정사항

| 항목 | 결정 | 근거 |
|------|------|------|
| Plan 구조 | LLMPlan — 별도 Plan (기존 BasePlan과 독립) | 기존 Plan 안정성 보존 |
| 행동 메뉴 | 하이브리드 (행동 유형 + 타겟 분리) | LLM이 "뭘+누구를" 결정, 코드가 "어떤 스킬로" 결정 |
| 상황 인식 | ASCII 그리드 맵 + 자연어 설명 | 공간 추론 향상, LLM 패턴 인식 활용 |
| AP/MP 관리 | 사전 표시 + 사후 검증 | LLM 산수 실수 허용, 코드가 보정 |
| Fallback | 기존 역할별 Plan (DPS/Tank/Support/Overseer) | 검증된 시스템 |
| 실행 | ActionExecutor 재사용 | 기존 코드 그대로 |
| 대사 표시 | TacticalOverlayUI로 reasoning 표시 | v0.2에서 검증됨 |

---

## 아키텍처

### 전체 흐름

```
TurnOrchestrator.ProcessTurn()
    ↓
SituationAnalyzer.Analyze() → Situation 생성
    ↓
"이 유닛 LLM 모드?" (LLMCombatSettings.IsLLMControlled)
    │
    ├─ 예 ─→ HandleLLMTurn():
    │        Phase 1: ActionMenuBuilder.Build() → 행동 메뉴 + ASCII 맵 생성
    │                 → LLM 프롬프트 조립 → Ollama 호출
    │        Phase 2: 대기 (Waiting)
    │        Phase 3: 응답 파싱 → LLMPlanBuilder.Build() → TurnPlan 생성
    │                 → AP 초과 시 자동 잘라냄
    │                 → ActionExecutor.Execute() (기존 코드)
    │        실패 시: 기존 Plan fallback
    │
    └─ 아니오 → 기존 TurnPlanner 흐름
```

### 통합 지점

`TurnOrchestrator.HandleLLMTurn()`을 v0.3 방식으로 교체:
- v0.2: LLM → TurnStrategy 힌트 → 기존 Plan이 무시
- v0.3: LLM → 행동 순서 → LLMPlanBuilder가 TurnPlan 직접 생성 → ActionExecutor 실행

---

## 행동 메뉴 (ActionMenuBuilder)

### 행동 유형

```
buff         — 자기 버프 (속사, 무모한 돌진 등)
ally_buff    — 아군 버프 (빛을 드러내라 등)
attack       — 단일 타겟 공격
aoe_attack   — 범위 공격
heal         — 아군 치유
debuff       — 적 디버프
move         — 이동 (적에게 접근 / 아군에게 접근 / 후퇴)
taunt        — 도발 (탱커)
reload       — 재장전
end_turn     — 턴 종료
```

### 타겟 목록

적과 아군을 번호로 제공:
```
== Targets ==
Enemies:
  e1: Witch Gunslinger (HP 12%, 8 tiles) — FINISHABLE
  e2: Witch (HP 60%, 10 tiles)
  e3: Witch (HP 80%, 12 tiles)
  B1: Chaos Lord (HP 90%, 18 tiles) — BOSS

Allies:
  a1: Abelard (Tank, HP 93%, 5 tiles)
  a2: Heinrix (Support, HP 25%, 3 tiles) — CRITICAL
  a3: Cassia (Support, HP 100%, 6 tiles)
```

### 메뉴 예시 (사전 검증됨)

```
== Available Actions (AP=5, MP=10) ==
1. [buff] Self-buff (AP 0~1) — 속사, 무모한 돌진 available
2. [ally_buff] Buff ally (AP 1) — 빛을 드러내라 available
3. [attack] Single attack (AP 1) — 단발 사격 (range 12, dmg ~30)
4. [aoe_attack] AoE attack (AP 2) — 연사 (range 8, radius 3, dmg ~20×targets)
5. [heal] Heal ally (AP 2) — 치유 (restore ~40 HP)
6. [move] Move toward target (MP cost varies)
7. [debuff] — NOT AVAILABLE (no debuff abilities)
8. [reload] — NOT AVAILABLE (ammo full)

Total AP: 5 | Total MP: 10
Note: You can use multiple actions. Actions cost AP. Code will select the best skill for each action type.
```

사용 불가한 행동은 "NOT AVAILABLE + 이유"로 표시 → LLM이 선택하지 않도록.

---

## ASCII 그리드 맵

### 생성 방식

`BattlefieldGrid`와 유닛 위치에서 ASCII 맵 생성:
- 전장을 타일 단위로 축소 (실제 그리드 or 상대 좌표)
- 유닛: ★(자기), a1~a5(아군), e1~e5(적), B(보스)
- 엄폐물: #
- 빈 공간: .

### 맵 크기

유닛 주변 중심으로 잘라서 표시 (전체 맵은 너무 큼):
- 자기 유닛 중심 20×15 타일 영역
- 맵 밖의 유닛은 화살표로 방향 표시 (← B1 18 tiles)

### 예시

```
== Battlefield Map (★=YOU, a=ally, e=enemy, B=boss, #=cover) ==

    0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9
 0  . . . . . . . . . . . . . . . . . . . .
 1  . . . . . . . . . . . . . . . . . . . .
 2  . . . . . . . . . . . e2. . . . . . . .
 3  . . . . . . . . . . e1. e3. . . . . . .
 4  . . . . . . . # . . . . . . . . . . . .
 5  . . . . . ★ . . . . . . . . . . . . . .
 6  . . . . a2. . . . . . . . . . . . . . .
 7  . . . a1. . . a3. . . . . . . . . . . .
 8  . . . . . . . . . . . . . . . . . . . .

Legend: ★=Argenta(YOU) a1=Abelard a2=Heinrix a3=Cassia
        e1=Witch Gunslinger(12%) e2=Witch(60%) e3=Witch(80%)
        → B1=Chaos Lord(90%) 18 tiles northeast
        e1,e2,e3 CLUSTERED (within 3 tiles)
```

---

## LLM 프롬프트 구조

```
[시스템 프롬프트]
You are commanding a unit in turn-based tactical combat.
You receive: battlefield map, situation summary, and available actions.
Plan this unit's turn by selecting actions in order.

Rules:
- Choose actions from the available list only
- Specify target when needed (use enemy/ally IDs like e1, a2)
- AP is limited — prioritize high-impact actions
- Code handles skill selection and positioning automatically
- If unsure, fewer actions are better than wrong actions

Respond with JSON:
{
  "actions": [
    {"type": "buff"},
    {"type": "move", "toward": "e1"},
    {"type": "aoe_attack", "target": "e1"},
    {"type": "attack", "target": "e1"}
  ],
  "reasoning": "brief explanation"
}

[유저 메시지]
{ASCII 그리드 맵}
{자연어 상황 요약}
{행동 메뉴}
```

---

## LLMPlanBuilder — LLM 응답 → TurnPlan 변환

### 변환 로직

LLM의 각 행동을 PlannedAction으로 변환:

| LLM 행동 | PlannedAction 변환 | 스킬 선택 |
|----------|-------------------|-----------|
| buff | PlannedAction.Buff | BuffPlanner에서 최적 버프 선택 (0 AP 우선) |
| ally_buff | PlannedAction.Buff(target=ally) | 지정 아군에게 사용 가능한 버프 선택 |
| attack | PlannedAction.Attack | AttackPlanner에서 최적 공격 스킬 + 타겟 선택 |
| aoe_attack | PlannedAction.Attack(AoE) | AoE 능력 중 최적 선택 + 캐스트 위치 계산 |
| heal | PlannedAction.Heal | 힐 능력 선택 + 타겟 (지정 or 최저 HP) |
| debuff | PlannedAction.Debuff | 디버프 능력 선택 + 타겟 |
| move | PlannedAction.Move | MovementPlanner에서 최적 위치 계산 |
| taunt | PlannedAction.Attack(taunt) | 도발 스킬 선택 |
| reload | PlannedAction.Reload | 현재 무기 리로드 |
| end_turn | PlannedAction.EndTurn | 턴 종료 |

### AP 사후 검증

```csharp
float remainingAP = situation.CurrentAP;
foreach (var action in llmActions)
{
    float cost = EstimateAPCost(action, situation);
    if (remainingAP < cost)
    {
        // AP 부족 → 나머지 행동 잘라냄
        Log($"AP 부족 ({remainingAP} < {cost}) — 이후 행동 {remaining}개 스킵");
        break;
    }
    plannedActions.Add(ConvertToPlannedAction(action, situation));
    remainingAP -= cost;
}
```

### 스킬 자동 선택

각 행동 유형별로 기존 Planner의 로직을 재사용:
- `attack` → `AttackPlanner.SelectBestAttack()` 호출
- `aoe_attack` → `AttackPlanner.SelectBestAoE()` + `ClusterDetector` 활용
- `buff` → `BuffPlanner.ScoreAttackBuff()` 로 최적 버프 선택
- `heal` → `HealPlanner` 로직 재사용
- `move` → `MovementPlanner.FindBestPosition()` 호출

---

## 파일 구조

### 새 파일
```
LLM_CombatAI/
├── ActionMenuBuilder.cs    — 사전 검증된 행동 메뉴 생성
├── BattlefieldMapBuilder.cs — ASCII 그리드 맵 생성
├── LLMPlanBuilder.cs       — LLM 응답 → TurnPlan 변환 (스킬 자동 선택)
└── LLMPromptAssembler.cs   — 맵 + 상황 + 메뉴 → 최종 프롬프트 조립
```

### 수정 파일
```
Core/TurnOrchestrator.cs    — HandleLLMTurn v0.3 방식으로 교체
LLM_CombatAI/LLMCombatSettings.cs — v0.3 시스템 프롬프트
```

### 재사용 (수정 없음)
```
Analysis/SituationAnalyzer.cs   — 상황 분석 그대로
Analysis/BattlefieldGrid.cs     — 그리드 데이터 소스
Analysis/ClusterDetector.cs     — AoE 클러스터 감지
Planning/Planners/AttackPlanner.cs — 공격 스킬 선택
Planning/Planners/BuffPlanner.cs   — 버프 스킬 선택
Planning/Planners/HealPlanner.cs   — 힐 스킬 선택
Planning/Planners/MovementPlanner.cs — 이동 위치 계산
Execution/ActionExecutor.cs     — 실행 엔진
GameInterface/CombatAPI.cs      — 게임 API
MachineSpirit/LLMClient.cs      — Ollama 호출
```

---

## 대사 표시

v0.2와 동일:
- LLM 처리 중: TacticalOverlayUI `"전술 분석 중..."`
- LLM 완료 시: TacticalOverlayUI에 reasoning 표시
- 비교 오버레이: LLMStatusOverlay에 행동 계획 + 통계

---

## v0.2와의 관계

v0.2의 TurnStrategy 경로를 **완전히 교체**:
- v0.2: HandleLLMTurn → LLMStrategyEngine → TurnState 저장 → BasePlan.EvaluateOrReuseStrategy
- v0.3: HandleLLMTurn → ActionMenuBuilder + BattlefieldMapBuilder → LLM → LLMPlanBuilder → TurnPlan → ActionExecutor

BasePlan의 EvaluateOrReuseStrategy에 추가했던 LLM 분기 코드를 제거하고, TurnOrchestrator의 HandleLLMTurn에서 모든 것을 처리.

---

## 성공 기준

- [ ] LLM이 행동 메뉴에서 유효한 행동을 선택
- [ ] 선택한 행동이 PlannedAction으로 정상 변환
- [ ] AP 초과 시 자동 잘라냄
- [ ] ASCII 맵에서 공간 관계 정확히 표현
- [ ] 기존 ActionExecutor로 정상 실행
- [ ] 실패 시 기존 Plan fallback
- [ ] LLM 유닛의 행동이 비-LLM 유닛과 눈에 띄게 다름
- [ ] 게임 크래시 없음

## 리스크

| 리스크 | 완화 |
|--------|------|
| LLM이 메뉴 밖 행동 선택 | 파싱 시 무시, 유효한 것만 실행 |
| AP 계산 틀림 | 사후 검증으로 잘라냄 |
| ASCII 맵 너무 큼 | 유닛 중심 20×15 영역으로 제한 |
| 스킬 자동 선택 품질 | 기존 Planner 로직 재사용 (이미 검증됨) |
| 응답 시간 | 로컬 모델이므로 감내, 타임아웃 30초 |
