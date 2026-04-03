# LLM 전략 어드바이저 v0.2 구현 계획

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** LLM이 TurnStrategyPlanner를 대체하여 전술적 의도(TurnStrategy)를 결정하고, 기존 Plan 코드가 그대로 실행하는 시스템 구축.

**Architecture:** `EvaluateOrReuseStrategy()`에서 LLM 모드 유닛이면 LLM에게 자연어 상황 설명을 보내 전략 선택을 받고, JSON 응답을 TurnStrategy 객체로 매핑. 기존 점수 기반 평가도 병렬 수행하여 비교 통계 수집. 대사 시스템(TacticalOverlayUI)으로 reasoning 표시.

**Tech Stack:** C# (.NET 4.8.1), Harmony 2.2.2, Newtonsoft.Json, Unity IMGUI, Ollama REST API

**설계 문서:** [2026-04-02-llm-strategy-advisor-v02-design.md](2026-04-02-llm-strategy-advisor-v02-design.md)

---

## Task 1: StrategyContextBuilder — Situation을 자연어로 변환

**Files:**
- Create: `LLM_CombatAI/StrategyContextBuilder.cs`
- Reference: `Analysis/Situation.cs`, `GameInterface/CombatAPI.cs`, `Core/TurnStrategy.cs`

**Step 1: StrategyContextBuilder.cs 생성**

Situation 객체를 LLM이 이해할 자연어 문자열로 변환한다. 기존 BattlefieldSerializer(JSON)와 달리 풍부한 맥락을 포함.

```csharp
namespace CompanionAI_v3.LLM_CombatAI
{
    /// <summary>
    /// ★ v3.76.0: Situation → LLM용 자연어 전장 설명 변환
    /// </summary>
    public static class StrategyContextBuilder
    {
        /// <summary>
        /// 전장 상황을 LLM이 이해할 자연어로 변환.
        /// </summary>
        public static string Build(BaseUnitEntity unit, Situation situation, Settings.AIRole role)
```

포함할 정보 (자연어 섹션별):

**섹션 1: 유닛 소개**
```
You are {CharacterName}, a {role} ({ranged/melee}) unit. Round {round} of combat.
Your stats: AP {ap}, MP {mp}, HP {hp}%. Equipped: {weapon} (range {range} tiles).
```
- `situation.CharacterName`, `situation.CurrentAP`, `situation.CurrentMP`, `situation.HPPercent`
- `situation.HasRangedWeapon` → "ranged" / "melee"
- 역할: role 파라미터에서 직접 사용

**섹션 2: 아군 상황**
```
== Allies ==
- {name} ({role}) HP {hp}% — {distance} tiles away. {status}
```
- situation.Allies 순회
- HP < 30% → "CRITICAL", HP < 50% → "WOUNDED"
- `CombatAPI.GetDistanceInTiles(unit, ally)`

**섹션 3: 적 상황**
```
== Enemies ==
- {name} (HP {hp}%) — {distance} tiles away. {tags}
```
- situation.Enemies 순회 (거리순 정렬)
- HP < 20% → "FINISHABLE"
- situation.BestTarget → "HIGH PRIORITY"
- 보스 감지: HP가 가장 높거나 이름에 "Lord"/"Champion" 등 → "BOSS"

**섹션 4: 전술 평가**
```
== Tactical Assessment ==
- {n} enemies clustered — AoE effective.
- {target} finishable (HP {hp}%).
- {ally} critically wounded (HP {hp}%).
```
- ClusterDetector 결과 활용 (situation에서 클러스터 정보)
- HP < 20% 적 = finishable
- HP < 30% 아군 = critical

**섹션 5: 사용 가능한 전략 목록**
```
== Available Strategies ==
- aggressive: Focus fire on a priority target.
- aoe_clear: Area attack on clustered enemies. {only if cluster detected}
- defensive: Protect/heal allies. {only if ally HP < 30%}
- buff_setup: Apply buffs before attacking. {only if buffs available}
- debuff_first: Weaken enemy defenses. {only if debuffs available}
- focus_boss: Prioritize the boss. {only if boss detected}
```
- 상황에 따라 해당되지 않는 전략 제외
- 예: AoE 능력 없으면 aoe_clear 제외, 버프 없으면 buff_setup 제외

**Step 2: 빌드 확인**

```powershell
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo
```

**Step 3: 커밋**

```bash
git add LLM_CombatAI/StrategyContextBuilder.cs
git commit -m "feat(llm-v02): add natural language battlefield context builder"
```

---

## Task 2: LLMStrategyEngine — LLM 전략 결정 + TurnStrategy 매핑

**Files:**
- Create: `LLM_CombatAI/LLMStrategyEngine.cs`
- Reference: `Core/TurnStrategy.cs`, `Planning/TurnStrategyPlanner.cs`

**Step 1: LLMStrategyEngine.cs 생성**

LLM에게 상황을 보내고, JSON 응답을 TurnStrategy 객체로 매핑한다.

```csharp
namespace CompanionAI_v3.LLM_CombatAI
{
    /// <summary>
    /// ★ v3.76.0: LLM 전략 결정 엔진.
    /// StrategyContextBuilder → Ollama → TurnStrategy 매핑.
    /// </summary>
    public static class LLMStrategyEngine
    {
        // 상태 관리 (코루틴 폴링용)
        private static bool _isProcessing;
        private static string _currentUnitId;
        private static TurnStrategy _pendingStrategy;
        private static string _pendingReasoning;
        private static string _pendingStrategyName;
        private static float _responseTime;
        private static bool _failed;
        private static string _failReason;

        public static bool IsProcessing => _isProcessing;
        public static bool HasResult => !_isProcessing && (_pendingStrategy != null || _failed);
```

**핵심 메서드 1: Evaluate (코루틴 시작)**
```csharp
        /// <summary>LLM 전략 평가 시작 (코루틴).</summary>
        public static IEnumerator Evaluate(
            BaseUnitEntity unit, Situation situation, Settings.AIRole role)
```
1. `StrategyContextBuilder.Build()` → 자연어 설명
2. 시스템 프롬프트 (전략 선택 전용, v0.1과 다름)
3. `LLMClient.SendOllamaNonStreaming()` 호출
4. JSON 응답 파싱: `{"strategy": "...", "focus_target": "...", "reasoning": "..."}`
5. `MapToTurnStrategy()` 호출

**핵심 메서드 2: MapToTurnStrategy (JSON → TurnStrategy)**
```csharp
        /// <summary>LLM 전략 이름 → TurnStrategy 객체 매핑.</summary>
        private static TurnStrategy MapToTurnStrategy(
            string strategyName, string focusTarget,
            Situation situation)
```

매핑 로직:
| LLM 전략 | SequenceType | 필드 설정 |
|-----------|-------------|-----------|
| aggressive | Standard | PrioritizesKillSequence = (finishable 적 존재) |
| aoe_clear | AoEFocus | ShouldPrioritizeAoE = true |
| buff_setup | BuffedAttack | ShouldBuffBeforeAttack = true |
| debuff_first | DebuffedAttack | ShouldDebuffBeforeAttack = true |
| defensive | Standard | (공격 최소화, 기본값 유지) |
| focus_boss | Standard | BestTarget → focus_target으로 오버라이드 |

- `focus_target`이 지정되면 situation.Enemies에서 찾아 situation.BestTarget 오버라이드
- ExpectedTotalDamage는 0으로 설정 (LLM은 데미지 계산 안 함, 로깅 전용)
- Reason에 LLM reasoning 저장

**핵심 메서드 3: ConsumeResult (결과 수거)**
```csharp
        /// <summary>결과를 가져가고 상태 초기화.</summary>
        public static LLMStrategyResult ConsumeResult()
```

```csharp
    public class LLMStrategyResult
    {
        public bool Success;
        public TurnStrategy Strategy;
        public string StrategyName;    // "aggressive", "aoe_clear" 등
        public string Reasoning;       // LLM reasoning 원본
        public float ResponseTime;
        public string FailReason;
    }
```

**시스템 프롬프트 (v0.2 전략 선택 전용):**
```
You are a tactical advisor for a turn-based combat game.
Given the battlefield situation, choose the best strategy for this unit's turn.

Your choices will be executed by an experienced AI system that handles all mechanical details (movement, skill selection, AP management). You only decide the strategic direction.

Respond with JSON:
{
  "strategy": "one of the listed strategies",
  "focus_target": "enemy name to prioritize (optional)",
  "reasoning": "brief tactical explanation"
}
```

**Step 2: 빌드 확인**

**Step 3: 커밋**

```bash
git add LLM_CombatAI/LLMStrategyEngine.cs
git commit -m "feat(llm-v02): add LLM strategy engine with TurnStrategy mapping"
```

---

## Task 3: LLMStrategyLogger — 비교 통계

**Files:**
- Create: `LLM_CombatAI/LLMStrategyLogger.cs`

**Step 1: LLMStrategyLogger.cs 생성**

LLM 선택과 점수 기반 선택을 비교 기록.

```csharp
namespace CompanionAI_v3.LLM_CombatAI
{
    /// <summary>★ v3.76.0: LLM vs 점수 기반 전략 비교 통계.</summary>
    public static class LLMStrategyLogger
    {
        private static int _totalDecisions;
        private static int _agreements;      // LLM과 점수가 같은 전략 선택
        private static int _llmOverrides;    // LLM이 다른 전략 선택
        private static int _fallbacks;       // LLM 실패 → 점수 fallback
        private static float _totalResponseTime;

        public static int TotalDecisions => _totalDecisions;
        public static int Agreements => _agreements;
        public static int LLMOverrides => _llmOverrides;
        public static int Fallbacks => _fallbacks;
        public static float AgreementRate =>
            _totalDecisions > 0 ? (float)_agreements / _totalDecisions * 100f : 0f;
        public static float AvgResponseTime =>
            _totalDecisions > 0 ? _totalResponseTime / _totalDecisions : 0f;

        /// <summary>LLM과 점수 기반 전략 비교 기록.</summary>
        public static void RecordComparison(
            string llmStrategy, string scoreStrategy,
            float responseTime, bool llmSucceeded)

        public static void RecordFallback(float responseTime, string reason)

        public static void Reset()

        /// <summary>전투 종료 시 요약 로그.</summary>
        public static void LogSummary()
    }
}
```

- `RecordComparison`: LLM 전략명과 점수 전략의 SequenceType을 비교하여 일치/불일치 기록
- 전략 비교 시 aggressive ↔ Standard/KillSequence, aoe_clear ↔ AoEFocus 등 매핑 필요

**Step 2: 빌드 확인**

**Step 3: 커밋**

```bash
git add LLM_CombatAI/LLMStrategyLogger.cs
git commit -m "feat(llm-v02): add LLM vs score strategy comparison logger"
```

---

## Task 4: BasePlan 통합 — EvaluateOrReuseStrategy에 LLM 분기

**Files:**
- Modify: `Planning/Plans/BasePlan.cs:2307-2351` — EvaluateOrReuseStrategy()
- Reference: `LLM_CombatAI/LLMStrategyEngine.cs`, `LLM_CombatAI/LLMCombatSettings.cs`

**Step 1: EvaluateOrReuseStrategy 수정**

핵심 통합 지점. 기존 코드의 `TurnStrategyPlanner.Evaluate()` 호출 전에 LLM 분기를 삽입.

현재 코드 (BasePlan.cs line 2330-2348):
```csharp
if (situation.HasHittableEnemies &&
    TeamBlackboard.Instance.CurrentTactic != TacticalSignal.Retreat)
{
    strategy = TurnStrategyPlanner.Evaluate(situation, role);
    // ... context 저장, 로깅
    return strategy;
}
```

변경 후:
```csharp
if (situation.HasHittableEnemies &&
    TeamBlackboard.Instance.CurrentTactic != TacticalSignal.Retreat)
{
    // ★ v3.76.0: LLM 전략 어드바이저 — 점수 기반 평가는 항상 수행 (비교용)
    var scoreStrategy = TurnStrategyPlanner.Evaluate(situation, role);

    // LLM 모드: LLM 전략 결정 시도
    if (LLMCombatSettings.IsLLMControlled(unit))
    {
        strategy = HandleLLMStrategy(unit, situation, role, scoreStrategy, turnState, roleTag);
    }
    else
    {
        strategy = scoreStrategy;
    }

    if (strategy != null)
    {
        turnState.SetContext(StrategicContextKeys.TurnStrategyKey, strategy);
        if (situation.BestTarget != null)
            turnState.SetContext(StrategicContextKeys.FocusTargetId, situation.BestTarget.UniqueId);

        string objective = strategy.PrioritizesKillSequence ? "Kill"
            : strategy.ShouldPrioritizeAoE ? "AoE" : "Attack";
        turnState.SetContext(StrategicContextKeys.TacticalObjective, objective);

        budget.StrategyPostActionReserved = strategy.ReservedAPForPostAction;
        Main.Log($"[{roleTag}] Strategy: {strategy.Sequence} (dmg={strategy.ExpectedTotalDamage:F0}, objective={objective})");
    }
    return strategy;
}
```

**Step 2: HandleLLMStrategy 헬퍼 메서드 추가**

BasePlan에 protected 메서드 추가:

```csharp
/// <summary>★ v3.76.0: LLM 전략 결정 처리 (폴링 기반).</summary>
protected TurnStrategy HandleLLMStrategy(
    BaseUnitEntity unit, Situation situation, Settings.AIRole role,
    TurnStrategy scoreStrategy, TurnState turnState, string roleTag)
{
    // Phase 1: LLM 시작
    if (!LLMStrategyEngine.IsProcessing && !LLMStrategyEngine.HasResult)
    {
        // 대사 시스템으로 "생각 중" 표시
        TacticalOverlayUI.Show(unit.CharacterName,
            new[] { "전술 분석 중..." },
            GetCompanionColor(unit), 30f);

        MachineSpirit.CoroutineRunner.Start(
            LLMStrategyEngine.Evaluate(unit, situation, role));

        // 이번 프레임은 null 반환 → 다음 프레임에 다시 호출됨
        // (EvaluateOrReuseStrategy가 null 반환하면 BasePlan이 전략 없이 진행)
        // 대신 scoreStrategy를 임시로 사용
        return scoreStrategy;
    }

    // Phase 2: 대기 중 — 이미 시작됐으면 결과가 올 때까지 scoreStrategy 사용
    if (LLMStrategyEngine.IsProcessing)
    {
        return scoreStrategy;
    }

    // Phase 3: 결과 처리
    var result = LLMStrategyEngine.ConsumeResult();

    if (result.Success)
    {
        Main.Log($"[{roleTag}] LLM Strategy: {result.StrategyName} ({result.ResponseTime:F1}초)");
        Main.Log($"[{roleTag}] LLM Reasoning: {result.Reasoning}");

        // 비교 로깅
        string scoreSequence = scoreStrategy?.Sequence.ToString() ?? "None";
        LLMStrategyLogger.RecordComparison(result.StrategyName, scoreSequence, result.ResponseTime, true);

        // 대사 시스템으로 reasoning 표시
        TacticalOverlayUI.Show(unit.CharacterName,
            new[] { result.Reasoning },
            GetCompanionColor(unit), 5f);

        // 오버레이에 비교 표시
        LLMStatusOverlay.ShowStrategyComparison(
            unit.CharacterName, result.StrategyName, result.Reasoning,
            scoreSequence, result.ResponseTime);

        return result.Strategy;
    }
    else
    {
        // LLM 실패 → 점수 기반 fallback
        Main.Log($"[{roleTag}] LLM Strategy failed: {result.FailReason} → score fallback");
        LLMStrategyLogger.RecordFallback(result.ResponseTime, result.FailReason);
        return scoreStrategy;
    }
}
```

**주의사항:** 
- `EvaluateOrReuseStrategy`는 프레임 기반이 아닌 동기 호출. LLM 코루틴이 완료될 때까지 기다릴 수 없음.
- **해결:** LLM 코루틴을 시작하되, 이번 턴은 scoreStrategy로 진행. LLM 결과는 다음 replan 시 또는 StrategicContext를 통해 적용.
- **대안:** TurnOrchestrator에서 LLM 대기 Phase를 추가 (v0.1의 HandleLLMTurn 패턴 재활용).

이 부분은 구현 시 `TurnOrchestrator`의 2-Phase Frame Spreading에 LLM Phase를 추가하는 방식으로 처리. 즉:
- Phase 1 (Ready): Analyze
- **Phase 1.5 (NEW - WaitingForLLMStrategy)**: LLM 코루틴 시작 → 대기 → 결과 수신
- Phase 2 (WaitingForPlan): CreatePlan (LLM 결과가 이미 TurnState에 저장됨)

**Step 3: unit 파라미터 전달**

현재 `EvaluateOrReuseStrategy`에는 `unit` 파라미터가 없음. BasePlan의 `_unit` 필드 또는 호출자에서 전달 필요.
- BasePlan에 `protected BaseUnitEntity _unit` 필드가 있는지 확인
- 없으면 `EvaluateOrReuseStrategy` 시그니처에 `BaseUnitEntity unit` 추가 + 4개 Plan 호출부 수정

**Step 4: 빌드 확인**

**Step 5: 커밋**

```bash
git add Planning/Plans/BasePlan.cs
git commit -m "feat(llm-v02): integrate LLM strategy branch into EvaluateOrReuseStrategy"
```

---

## Task 5: TurnOrchestrator LLM Strategy Phase 추가

**Files:**
- Modify: `Core/TurnOrchestrator.cs` — LLM 전략 대기 Phase 추가
- Modify: `Core/TurnState.cs` — ComputePhase에 WaitingForLLMStrategy 추가 (필요 시)

**Step 1: v0.1의 HandleLLMTurn 패턴을 전략 대기로 변경**

v0.1에서는 TurnOrchestrator가 LLM 응답을 기다렸음. v0.2도 같은 패턴:

```
ProcessTurn():
  Phase Ready → Analyze → PendingSituation 저장
  ★ Phase WaitingForLLMStrategy (NEW):
    → LLM 코루틴 시작
    → 대기 (return Waiting)
    → 결과 수신 → TurnState에 저장
  Phase WaitingForPlan → CreatePlan (LLM 전략이 이미 TurnState에 있음)
```

v0.1의 `HandleLLMTurn` 코드를 `HandleLLMStrategy`로 리팩토링:
- LLMDecisionEngine → LLMStrategyEngine
- PlannedAction 직접 실행 → TurnState에 전략 저장 후 Plan으로 넘김
- 대사 시스템으로 "전술 분석 중..." / reasoning 표시

**Step 2: v0.1 코드 정리**

v0.1의 `HandleLLMTurn` (직접 실행 경로)를 제거하고 전략 어드바이저 경로로 교체.

**Step 3: 빌드 확인**

**Step 4: 커밋**

```bash
git add Core/TurnOrchestrator.cs Core/TurnState.cs
git commit -m "feat(llm-v02): add LLM strategy waiting phase to TurnOrchestrator"
```

---

## Task 6: LLMStatusOverlay 확장 — 비교 표시

**Files:**
- Modify: `LLM_CombatAI/LLMStatusOverlay.cs` — 전략 비교 오버레이 추가

**Step 1: ShowStrategyComparison 메서드 추가**

```csharp
/// <summary>LLM vs 점수 전략 비교 표시.</summary>
public static void ShowStrategyComparison(
    string unitName, string llmStrategy, string reasoning,
    string scoreStrategy, float responseTime)
```

표시 내용:
```
┌─ LLM 전략 어드바이저 ─────────────────┐
│ 유닛: Argenta (DPS)                    │
│                                        │
│ LLM 전략: aoe_clear                    │
│ LLM 이유: "적 2명 밀집, AoE로 정리"    │
│ 점수 전략: Standard (DirectAttack)      │
│                                        │
│ ★ 채택: LLM                            │
│ 응답시간: 2.1초                         │
│                                        │
│ [통계] 일치: 12/20 (60%)               │
│ LLM 독자 선택: 8회                      │
└────────────────────────────────────────┘
```

**Step 2: 빌드 확인**

**Step 3: 커밋**

```bash
git add LLM_CombatAI/LLMStatusOverlay.cs
git commit -m "feat(llm-v02): add strategy comparison overlay display"
```

---

## Task 7: LLMCombatSettings 시스템 프롬프트 업데이트

**Files:**
- Modify: `LLM_CombatAI/LLMCombatSettings.cs` — v0.2 전략 선택 전용 프롬프트

**Step 1: SystemPrompt 교체**

v0.1의 "스킬 GUID 선택" 프롬프트를 v0.2의 "전략 방향 선택" 프롬프트로 교체:

```csharp
public static string StrategySystemPrompt => @"You are a tactical advisor for Warhammer 40K: Rogue Trader.
Given the battlefield situation, choose the best strategy for this unit's turn.

Your choice will be executed by an experienced combat AI that handles all details (movement, skill selection, AP management, target validation). You only decide the strategic direction.

Important:
- Consider the overall battle state, not just this unit
- Think about what other allies can do — don't duplicate their strengths
- Finishing wounded enemies reduces incoming damage
- Protecting critically wounded allies prevents snowballing
- AoE is efficient when enemies are clustered but can be risky

Respond ONLY with valid JSON:
{
  ""strategy"": ""one of the listed strategies"",
  ""focus_target"": ""enemy name to prioritize (optional, omit if not applicable)"",
  ""reasoning"": ""brief tactical explanation in 1-2 sentences""
}";
```

v0.1의 SystemPrompt는 유지 (하위 호환).

**Step 2: 빌드 확인**

**Step 3: 커밋**

```bash
git add LLM_CombatAI/LLMCombatSettings.cs
git commit -m "feat(llm-v02): add strategy advisor system prompt"
```

---

## Task 8: 전투 이벤트 통합 — 통계 리셋/요약

**Files:**
- Modify: `GameInterface/TurnEventHandler.cs` — v0.2 통계 연결

**Step 1: 전투 시작 시 리셋**

```csharp
LLMStrategyLogger.Reset();
LLMStrategyEngine.Reset();
```

**Step 2: 전투 종료 시 요약**

```csharp
if (LLMStrategyLogger.TotalDecisions > 0)
{
    LLMStrategyLogger.LogSummary();
}
```

**Step 3: 빌드 확인**

**Step 4: 커밋**

```bash
git add GameInterface/TurnEventHandler.cs
git commit -m "feat(llm-v02): connect strategy logger to combat lifecycle events"
```

---

## Task 9: 통합 빌드 + 최종 검증

**Files:**
- Modify: `Info.json` — 버전 업데이트 (3.76.0 → 3.78.0)

**Step 1: 전체 리빌드**

```powershell
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo
```

**Step 2: 최종 체크리스트**

- [ ] StrategyContextBuilder가 자연어 설명 생성
- [ ] LLMStrategyEngine이 LLM 호출 → TurnStrategy 매핑
- [ ] EvaluateOrReuseStrategy에서 LLM 분기 동작
- [ ] TurnOrchestrator에서 LLM 전략 대기 Phase 동작
- [ ] 점수 기반 전략도 병렬 수행 (비교용)
- [ ] 대사 시스템으로 "전술 분석 중..." → reasoning 표시
- [ ] 비교 오버레이 표시
- [ ] LLM 실패 시 점수 기반 fallback
- [ ] 전투 종료 시 비교 통계 로그
- [ ] 빌드 성공

**Step 3: 커밋**

```bash
git add Info.json
git commit -m "feat(llm-v02): LLM Strategy Advisor v0.2 — complete integration"
```

---

## 실행 순서 요약

| Task | 내용 | 의존성 |
|------|------|--------|
| 1 | StrategyContextBuilder (자연어 변환) | 없음 |
| 2 | LLMStrategyEngine (LLM 호출 + 매핑) | Task 1 |
| 3 | LLMStrategyLogger (비교 통계) | 없음 |
| 4 | BasePlan 통합 (EvaluateOrReuseStrategy) | Task 2, 3 |
| 5 | TurnOrchestrator Phase 추가 | Task 2, 4 |
| 6 | LLMStatusOverlay 확장 | Task 3 |
| 7 | LLMCombatSettings 프롬프트 | 없음 |
| 8 | 전투 이벤트 통합 | Task 3 |
| 9 | 통합 빌드 + 검증 | 전체 |

**병렬 가능**: Task 1+3+7 (의존성 없음), Task 6+8 (독립)
