# LLMPlan v0.3 구현 계획

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** LLM이 사전 검증된 행동 메뉴에서 행동 순서를 결정하고, 기존 Planner가 스킬을 자동 선택하여 실행하는 LLMPlan 시스템 구축.

**Architecture:** ActionMenuBuilder가 Situation에서 가능한 행동 목록을 생성하고, BattlefieldMapBuilder가 ASCII 그리드 맵을 만들고, LLMPromptAssembler가 프롬프트를 조립하여 Ollama에 전송. 응답을 LLMPlanBuilder가 기존 AttackPlanner/BuffPlanner/HealPlanner/MovementPlanner를 호출하여 PlannedAction 리스트로 변환하고 TurnPlan을 생성. TurnOrchestrator의 HandleLLMTurn을 v0.3으로 교체.

**Tech Stack:** C# (.NET 4.8.1), Harmony 2.2.2, Newtonsoft.Json, Unity IMGUI, Ollama REST API

**설계 문서:** [2026-04-02-llm-plan-v03-design.md](2026-04-02-llm-plan-v03-design.md)

---

## Task 1: BattlefieldMapBuilder — ASCII 그리드 맵 생성

**Files:**
- Create: `LLM_CombatAI/BattlefieldMapBuilder.cs`
- Reference: `Analysis/BattlefieldGrid.cs`, `GameInterface/CombatAPI.cs`

**구현:**

유닛 중심 20×15 타일 영역의 ASCII 맵 생성. `BattlefieldGrid.Instance`에서 walkable 정보와 유닛 위치를 가져와 텍스트로 변환.

핵심 메서드:
```csharp
public static string Build(BaseUnitEntity unit, Situation situation)
```

로직:
1. 유닛 위치를 그리드 좌표로 변환 (`GetNode()` → `XCoordinateInGrid/ZCoordinateInGrid`)
2. 유닛 중심 ±10(X), ±7(Z) 타일 영역 계산
3. 영역 내 각 셀을 문자로 변환:
   - `★` = 자기 자신
   - `a1~a5` = 아군 (situation.Allies, 자신 제외)
   - `e1~e5` = 적 (situation.Enemies, 거리순)
   - `B` = 보스 (MaxHP가 평균 2배 이상)
   - `#` = 비이동가능 (BattlefieldGrid.IsWalkable == false이고 유닛 근처)
   - `.` = 빈 공간
4. 범위 밖 유닛은 "→ B1=Chaos Lord 18 tiles east" 형태로 범례에 추가
5. 클러스터 감지: 적 3명 이상이 3타일 내면 "CLUSTERED" 표시

**빌드 확인 후 커밋.**

---

## Task 2: ActionMenuBuilder — 사전 검증된 행동 메뉴 생성

**Files:**
- Create: `LLM_CombatAI/ActionMenuBuilder.cs`
- Reference: `GameInterface/CombatAPI.cs`, `Data/AbilityDatabase.cs`, `Analysis/Situation.cs`

**구현:**

Situation에서 현재 유닛이 할 수 있는 행동 목록을 생성. 각 행동은 사전 검증됨.

핵심 메서드:
```csharp
public static string BuildMenu(BaseUnitEntity unit, Situation situation)
public static List<ActionOption> BuildOptions(BaseUnitEntity unit, Situation situation)
```

`ActionOption` 구조:
```csharp
public class ActionOption
{
    public string Type;        // "buff", "attack", "aoe_attack", "heal", "move", "debuff", "taunt", "reload"
    public string Description; // "Self-buff (AP 0~1) — 속사, 무모한 돌진"
    public float APCost;       // 추정 AP 비용
    public bool Available;     // 사용 가능 여부
    public string UnavailableReason; // 불가 시 이유
}
```

각 행동 유형별 검증:
- **buff**: `situation.AvailableBuffs.Count > 0` 또는 0 AP 버프 존재
- **ally_buff**: CanTargetFriends인 버프 존재
- **attack**: `situation.AvailableAttacks.Count > 0 && situation.HasHittableEnemies`
- **aoe_attack**: `situation.AvailableAoEAttacks.Count > 0`
- **heal**: `situation.AvailableHeals.Count > 0` 이고 아군 중 부상자 존재
- **debuff**: `situation.AvailableDebuffs.Count > 0`
- **move**: `situation.CanMove && situation.CurrentMP > 0`
- **reload**: `situation.NeedsReload`
- **taunt**: 도발 스킬 존재 (Tank만)

타겟 목록도 함께 생성:
```
== Targets ==
Enemies:
  e1: Witch Gunslinger (HP 12%, 8 tiles) — FINISHABLE
  e2: Witch (HP 60%, 10 tiles)
Allies:
  a1: Abelard (Tank, HP 93%)
  a2: Heinrix (Support, HP 25%) — CRITICAL
```

**빌드 확인 후 커밋.**

---

## Task 3: LLMPromptAssembler — 프롬프트 조립

**Files:**
- Create: `LLM_CombatAI/LLMPromptAssembler.cs`
- Modify: `LLM_CombatAI/LLMCombatSettings.cs` — v0.3 시스템 프롬프트 추가

**구현:**

맵 + 상황 + 메뉴를 하나의 프롬프트로 조립.

```csharp
public static class LLMPromptAssembler
{
    public static string BuildUserMessage(
        BaseUnitEntity unit, Situation situation, Settings.AIRole role)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BattlefieldMapBuilder.Build(unit, situation));
        sb.AppendLine();
        sb.AppendLine(StrategyContextBuilder.Build(unit, situation, role)); // v0.2 재사용
        sb.AppendLine();
        sb.AppendLine(ActionMenuBuilder.BuildMenu(unit, situation));
        return sb.ToString();
    }
}
```

시스템 프롬프트 (LLMCombatSettings.PlanSystemPrompt):
```
You are commanding a unit in turn-based tactical combat (Warhammer 40K: Rogue Trader).
You receive: a battlefield map, situation summary, and available actions.
Plan this unit's entire turn by selecting actions in order.

Rules:
- Choose actions from the available list only (do NOT invent actions)
- Use target IDs (e1, e2, a1, a2) when specifying targets
- AP is limited — the system will cut off actions if AP runs out
- The combat system automatically selects the best skill for each action type
- If you choose "move", specify direction with "toward": "e1" or "toward": "allies"
- Fewer correct actions are better than many wrong ones

Respond with JSON:
{
  "actions": [
    {"type": "buff"},
    {"type": "move", "toward": "e1"},
    {"type": "aoe_attack", "target": "e1"},
    {"type": "attack", "target": "e1"}
  ],
  "reasoning": "brief explanation of your tactical plan"
}
```

**빌드 확인 후 커밋.**

---

## Task 4: LLMPlanBuilder — LLM 응답 → TurnPlan 변환

**Files:**
- Create: `LLM_CombatAI/LLMPlanBuilder.cs`
- Reference: `Planning/Planners/AttackPlanner.cs`, `Planning/Planners/BuffPlanner.cs`, `Planning/Planners/HealPlanner.cs`, `Planning/Planners/MovementPlanner.cs`, `Core/PlannedAction.cs`, `Core/TurnPlan.cs`

**구현:**

LLM JSON 응답을 파싱하고, 각 행동을 기존 Planner를 호출하여 PlannedAction으로 변환.

핵심 메서드:
```csharp
public static class LLMPlanBuilder
{
    /// <summary>LLM 응답 → TurnPlan 변환. AP 초과 시 자동 잘라냄.</summary>
    public static TurnPlan Build(
        string llmResponse,
        BaseUnitEntity unit,
        Situation situation,
        string roleName,
        out string reasoning,
        out string failReason)
}
```

변환 로직 (각 행동별):

| LLM 행동 | 코드 처리 |
|----------|----------|
| `buff` | `BuffPlanner.PlanBuffWithReservation(situation, ref ap, 0f, role)` 또는 0AP 버프 우선 사용 |
| `ally_buff` | 타겟 아군 찾기 → `BuffPlanner.PlanAllyBuff(situation, ref ap, role)` |
| `attack` | 타겟 적 찾기 → `AttackPlanner.PlanAttack(situation, ref ap, role, preferTarget)` |
| `aoe_attack` | AoE 능력 선택 → PlannedAction.Attack (AoE) 또는 PositionalAttack |
| `heal` | 타겟 아군 찾기 → `HealPlanner.PlanAllyHeal(situation, ally, ref ap, role)` |
| `debuff` | 타겟 적 찾기 → 디버프 능력 선택 → PlannedAction.Debuff |
| `move` | `toward` 파싱 → `MovementPlanner.PlanMoveToEnemy(situation, role, tacticalTarget: target)` |
| `reload` | `PlannedAction.Reload(reloadAbility, unit, apCost)` |
| `taunt` | 도발 스킬 → PlannedAction.Attack(tauntAbility, target, ...) |
| `end_turn` | `PlannedAction.EndTurn(reason)` |

AP 사후 검증:
```csharp
float remainingAP = situation.CurrentAP;
var actions = new List<PlannedAction>();
foreach (var llmAction in parsedActions)
{
    var planned = ConvertAction(llmAction, unit, situation, ref remainingAP, roleName);
    if (planned == null) continue; // 변환 실패 → 스킵
    actions.Add(planned);
    if (remainingAP <= 0.01f) break; // AP 소진
}
if (actions.Count == 0) return null; // 전부 실패 → fallback
actions.Add(PlannedAction.EndTurn("LLM plan complete"));
return new TurnPlan(actions, TurnPriority.DirectAttack, $"LLM: {reasoning}", ...);
```

타겟 해석:
- "e1", "e2" → situation.Enemies에서 인덱스로 찾기 (거리순 정렬 기준)
- "a1", "a2" → situation.Allies에서 인덱스로 찾기
- "allies" (이동 방향) → 아군 중심 방향으로 이동
- 이름 직접 지정 → CharacterName 매칭

**빌드 확인 후 커밋.**

---

## Task 5: TurnOrchestrator HandleLLMTurn v0.3 교체

**Files:**
- Modify: `Core/TurnOrchestrator.cs` — HandleLLMTurn을 v0.3 방식으로 교체

**구현:**

v0.2의 HandleLLMTurn(전략 힌트 → BasePlan)을 v0.3(행동 메뉴 → LLMPlanBuilder → TurnPlan)으로 교체.

새 HandleLLMTurn 흐름:
```
Phase 1: LLM 미시작
  → LLMPromptAssembler.BuildUserMessage() → 프롬프트 생성
  → LLMClient.SendOllamaNonStreaming() 코루틴 시작
  → TacticalOverlayUI.ShowAlways("전술 분석 중...")
  → return Waiting

Phase 2: LLM 처리 중
  → 타임아웃 30초 체크
  → return Waiting

Phase 3: 결과 수신
  → LLMPlanBuilder.Build(response, unit, situation, ...)
  → 성공: turnState.Plan = llmPlan → ExecuteNextAction()
         + TacticalOverlayUI.ShowAlways(reasoning)
         + LLMStatusOverlay에 행동 계획 표시
  → 실패: 기존 Plan fallback
         turnState.Plan = _planner.CreatePlan(situation, turnState)
         → ExecuteNextAction()
```

v0.2와의 차이점:
- LLMStrategyEngine 대신 직접 LLMClient 호출 (프롬프트는 LLMPromptAssembler가 생성)
- 결과를 TurnState 컨텍스트에 저장하지 않음 → 직접 TurnPlan 생성
- BasePlan.EvaluateOrReuseStrategy의 LLM 분기 불필요

**v0.2 코드 정리:**
- BasePlan.EvaluateOrReuseStrategy에서 LLM 관련 코드 제거 (isLLMStrategy, LLMStrategyResult 체크)
- 원래 코드로 복원 (previousValid = dmg > 0 체크)

**빌드 확인 후 커밋.**

---

## Task 6: LLMStatusOverlay v0.3 업데이트

**Files:**
- Modify: `LLM_CombatAI/LLMStatusOverlay.cs` — 행동 계획 표시

**구현:**

v0.3에서는 전략 비교 대신 **행동 계획** 표시:

```
┌─ LLM Combat AI ────────────────────────┐
│ 유닛: Argenta (DPS)                     │
│                                         │
│ LLM 계획:                               │
│  1. [buff] 속사                          │
│  2. [move] → 적 클러스터 방향            │
│  3. [aoe_attack] → e1 (Witch Gunslinger)│
│  4. [attack] → e2 (Witch)               │
│                                         │
│ "적 밀집 지역으로 접근 후 AoE 정리"      │
│ 응답: 5.2초                              │
│                                         │
│ ─── 히스토리 ───                         │
│ [2] Argenta → buff,move,aoe,attack 5.2초│
│ [1] Cassia → buff,debuff,heal 3.8초     │
└─────────────────────────────────────────┘
```

`ShowPlan()` 메서드 추가:
```csharp
public static void ShowPlan(
    string unitName,
    List<string> actionDescriptions,  // ["[buff] 속사", "[move] → e1", ...]
    string reasoning,
    float responseTime)
```

**빌드 확인 후 커밋.**

---

## Task 7: v0.2 코드 정리 + 전투 이벤트 업데이트

**Files:**
- Modify: `Planning/Plans/BasePlan.cs` — LLM 분기 코드 제거, 원래 로직 복원
- Modify: `GameInterface/TurnEventHandler.cs` — v0.3 리셋/통계 연결
- Modify: `Core/TurnOrchestrator.cs` — OnTurnStart에서 v0.3 상태 초기화

**구현:**

BasePlan.EvaluateOrReuseStrategy 복원:
```csharp
// v0.2에서 추가한 isLLMStrategy, LLMStrategyResult 체크 제거
// 원래 코드로:
bool previousValid = strategy != null
    && situation.HasHittableEnemies
    && situation.BestTarget != null
    && strategy.ExpectedTotalDamage > 0;
```

전투 이벤트:
- 전투 시작: LLM 관련 상태 리셋
- 전투 종료: 통계 로그 (성공률, 평균 응답시간, 행동 분포)

**빌드 확인 후 커밋.**

---

## Task 8: 통합 빌드 + 버전 업데이트

**Files:**
- Modify: `Info.json` — 버전 업데이트 (3.78.0 → 3.80.0)

**빌드:**
```powershell
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo
```

**체크리스트:**
- [ ] BattlefieldMapBuilder가 ASCII 맵 생성
- [ ] ActionMenuBuilder가 검증된 행동 목록 생성
- [ ] LLMPromptAssembler가 맵+상황+메뉴 조립
- [ ] LLMPlanBuilder가 LLM 응답 → TurnPlan 변환
- [ ] HandleLLMTurn v0.3이 전체 파이프라인 실행
- [ ] AP 초과 시 자동 잘라냄
- [ ] 실패 시 기존 Plan fallback
- [ ] v0.2 BasePlan LLM 코드 정리됨
- [ ] 오버레이에 행동 계획 표시
- [ ] 빌드 성공

---

## 실행 순서

| Task | 내용 | 의존성 |
|------|------|--------|
| 1 | BattlefieldMapBuilder (ASCII 맵) | 없음 |
| 2 | ActionMenuBuilder (행동 메뉴) | 없음 |
| 3 | LLMPromptAssembler (프롬프트 조립) | Task 1, 2 |
| 4 | LLMPlanBuilder (응답 → TurnPlan) | Task 2 |
| 5 | TurnOrchestrator v0.3 통합 | Task 3, 4 |
| 6 | LLMStatusOverlay 업데이트 | Task 4 |
| 7 | v0.2 코드 정리 + 이벤트 | Task 5 |
| 8 | 통합 빌드 + 검증 | 전체 |

**병렬 가능**: Task 1+2 (의존성 없음), Task 4+6 (독립 가능)
