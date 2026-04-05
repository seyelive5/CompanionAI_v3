# LLM-as-Scorer + 성능 최적화 구현 계획

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** LLM이 유틸리티 가중치를 JSON으로 출력하여 스코어링 자체를 변경하고, 프롬프트 압축 + 시맨틱 캐싱 + 예측적 사전 호출로 응답 시간을 최소화하는 시스템 구축.

**Architecture:** LLMScorer가 전장 상황을 압축하여 Ollama에 전송 → 가중치 JSON 수신 → UtilityScorer/TargetScorer에 주입 → 가중치 적용 플랜 vs 기본 플랜을 Judge가 비교. 캐싱과 사전 호출로 실질 지연 0에 수렴.

**Tech Stack:** C# (.NET 4.8.1), Ollama REST API, Newtonsoft.Json

**설계 문서:** [2026-04-05-llm-scorer-optimization-design.md](2026-04-05-llm-scorer-optimization-design.md)

---

## Task 1: ScorerWeights 데이터 클래스 + LLMScorer 엔진

**Files:**
- Create: `Planning/LLM/ScorerWeights.cs`
- Create: `Planning/LLM/LLMScorer.cs`

**ScorerWeights** — LLM이 출력하는 가중치 JSON을 매핑:
```csharp
public class ScorerWeights
{
    public float AoEWeight = 1.0f;       // AoE 스코어 배율 (1.0=기본, 2.0=2배)
    public float FocusFire = 1.0f;       // 지정 타겟 스코어 배율
    public int PriorityTarget = -1;      // 적 인덱스 (-1=미지정)
    public float HealPriority = 0f;      // 힐 오프셋 (-0.3=30% 감소)
    public float BuffPriority = 1.0f;    // 버프 스코어 배율
    public bool DefensiveStance = false; // 방어적 포지셔닝

    public static ScorerWeights Parse(string response, int enemyCount)
    // JSON {"aoe_weight":2.0,...} 또는 plain text 파싱
    // 실패 시 기본값 반환 (모든 값 1.0)
}
```

**LLMScorer** — Ollama 호출 + 가중치 수신:
```csharp
public static class LLMScorer
{
    public static IEnumerator Score(Situation situation, string roleName, Action<ScorerWeights> onResult)
    // 1. CompactBattlefieldEncoder로 압축 상태 생성 (~100 토큰)
    // 2. Ollama /api/chat (think=false, temp=0, num_predict=50)
    // 3. ScorerWeights.Parse()
    // 4. 실패 시 기본 가중치 반환
}
```

**빌드 확인 후 커밋.**

---

## Task 2: CompactBattlefieldEncoder — 프롬프트 압축

**Files:**
- Create: `Planning/LLM/CompactBattlefieldEncoder.cs`

약축 형태로 전투 상태를 ~100 토큰으로 압축:

```
U:Argenta,DPS,HP85,AP4,MP10,Wpn:Bolter/12
A:Abelard,Tank,HP93,d5|Heinrix,Sup,HP25!,d3
E:0:Psyker,HP40,d5,HI|1:Cult,HP100,d8|2:Cult,HP100,d8,CL|3:Heavy,HP90,d15
K:0 finishable|1,2 clustered|Heinrix critical
```

기존 `BattlefieldSummarizer`를 참조하되 토큰 최소화. static StringBuilder 재사용.

시스템 프롬프트 (~80 토큰):
```
Tactical advisor. Output JSON scoring weights for this unit's turn.
Keys: aoe_weight(float,def 1), focus_fire(float,def 1), priority_target(int,def -1), heal_priority(float,def 0), buff_priority(float,def 1), defensive_stance(bool,def false).
Only output changed values. Example: {"aoe_weight":2.0,"priority_target":0}
```

**빌드 확인 후 커밋.**

---

## Task 3: UtilityScorer/TargetScorer 가중치 주입 확장

**Files:**
- Modify: `Analysis/TargetScorer.cs` — 기존 LLM_Focus 로직 확장
- Modify: `Analysis/UtilityScorer.cs` — ScoreAttack(), ScoreHeal()에 가중치 참조 추가

**TargetScorer 확장:**
- 기존: `LLM_FocusTarget` + `LLM_FocusBonus` (고정 +50)
- 변경: `LLM_ScorerWeights` 키에서 `ScorerWeights` 객체 읽기
  - `focus_fire` × target 점수 배율
  - `priority_target` → 지정 적 보너스

**UtilityScorer 확장:**
- `ScoreAttack()`: AoE 능력이면 `aoe_weight` 배율 적용
- `ScoreHeal()`: `heal_priority` 오프셋 적용
- `ScoreAttackBuff()` (BuffPlanner): `buff_priority` 배율 적용

가중치는 `TurnState.StrategicContext["LLM_ScorerWeights"]`에 저장.
`TargetScorer._activeTurnState`로 접근 (기존 패턴 재사용).

**빌드 확인 후 커밋.**

---

## Task 4: CandidatePlanGenerator 단순화 + TurnOrchestrator 통합

**Files:**
- Modify: `Planning/LLM/CandidatePlanGenerator.cs` — 아키타입 15개 → 가중치 기반 2개
- Modify: `Core/TurnOrchestrator.cs` — HandleLLMJudge에서 LLMScorer 사용

**CandidatePlanGenerator 단순화:**
```csharp
public static List<CandidatePlan> Generate(...)
{
    // Plan A: LLM 가중치 적용
    turnState.SetContext("LLM_ScorerWeights", scorerWeights);
    var llmPlan = planner.CreatePlan(situation, turnState);

    // Plan B: 기본 가중치 (LLM 없음)
    turnState.SetContext("LLM_ScorerWeights", null);
    var basePlan = planner.CreatePlan(situation, freshTurnState);

    return [llmPlan, basePlan]; // Judge가 A vs B 선택
}
```

**TurnOrchestrator HandleLLMJudge 흐름:**
```
Phase 0: 캐시 체크 (Task 5)
Phase 1: LLMScorer.Score() 시작
Phase 2: 대기
Phase 3: 가중치 수신 → CandidatePlanGenerator → Judge → 실행
```

기존 LLMAdvisor 호출을 LLMScorer로 교체.

**빌드 확인 후 커밋.**

---

## Task 5: 시맨틱 캐싱

**Files:**
- Create: `Planning/LLM/LLMScorerCache.cs`
- Modify: `Core/TurnOrchestrator.cs` — 캐시 체크 추가

**LLMScorerCache:**
```csharp
public static class LLMScorerCache
{
    private static Dictionary<long, ScorerWeights> _cache;

    public static long ComputeHash(Situation situation)
    {
        // 해시 구성: 유닛역할 + HP구간(10%단위) + 적수 + 적HP합구간 + hittable수 + 아군위기
        long h = (long)role * 1000000;
        h += (long)(hpPercent / 10) * 100000;
        h += enemyCount * 10000;
        h += (long)(enemyHpSum / 100) * 100;
        h += hittableCount * 10;
        h += allyCritical ? 1 : 0;
        return h;
    }

    public static bool TryGet(long hash, out ScorerWeights weights)
    public static void Store(long hash, ScorerWeights weights)
    public static void Clear() // 전투 시작 시
    public static void InvalidateOnDeath() // 적 사망 시
}
```

TurnOrchestrator에서 LLMScorer 호출 전 캐시 체크:
```csharp
var hash = LLMScorerCache.ComputeHash(situation);
if (LLMScorerCache.TryGet(hash, out var cached))
{
    // 캐시 히트 → LLM 호출 스킵 → 즉시 가중치 사용
    weights = cached;
}
else
{
    // 캐시 미스 → LLMScorer.Score() 호출
    yield return LLMScorer.Score(...);
    LLMScorerCache.Store(hash, weights);
}
```

**빌드 확인 후 커밋.**

---

## Task 6: 예측적 사전 호출

**Files:**
- Modify: `GameInterface/TurnEventHandler.cs` — 적 턴 중 아군 사전 분석
- Create: `Planning/LLM/LLMPreCompute.cs`

**LLMPreCompute:**
```csharp
public static class LLMPreCompute
{
    private static Dictionary<string, ScorerWeights> _preComputed; // unitId → weights

    public static void StartPreCompute(BaseUnitEntity nextAlly, Situation situation)
    // 비동기로 LLMScorer.Score() 시작, 결과를 _preComputed에 저장

    public static bool TryGetPreComputed(string unitId, out ScorerWeights weights)
    // 사전 계산 결과 있으면 반환

    public static void Clear()
}
```

**TurnEventHandler 통합:**
- 적 턴 시작 시: 다음 아군 유닛 예측 → `LLMPreCompute.StartPreCompute()`
- 아군 턴 시작 시: `LLMPreCompute.TryGetPreComputed()` → 히트면 즉시 사용

**빌드 확인 후 커밋.**

---

## Task 7: 훈련 데이터 수집

**Files:**
- Create: `Diagnostics/TrainingDataCollector.cs`
- Modify: `Core/TurnOrchestrator.cs` — 턴 종료 시 수집 호출

**TrainingDataCollector:**
```csharp
public static class TrainingDataCollector
{
    public static void RecordTurn(
        string unitName, string role,
        string compactState,        // CompactBattlefieldEncoder 출력
        ScorerWeights weights,      // LLM 가중치
        string planSummary,         // PlanSummarizer 출력
        int kills, float damageDealt, float damageTaken, float apUsed, float apTotal)

    public static void FlushToFile()  // 전투 종료 시 JSONL 저장
}
```

저장 경로: `%AppData%/CompanionAI/training_data/combat_YYYYMMDD.jsonl`

**빌드 확인 후 커밋.**

---

## Task 8: 정리 + 통합 빌드

**Files:**
- Modify: `Info.json` — 버전 업데이트
- 불필요한 코드 정리 (기존 아키타입 관련 사용되지 않는 코드)

**체크리스트:**
- [ ] LLMScorer가 가중치 JSON 출력
- [ ] UtilityScorer/TargetScorer에 가중치 반영
- [ ] 가중치 플랜 vs 기본 플랜 → Judge 비교
- [ ] 프롬프트 ~100 토큰으로 압축
- [ ] 캐시 히트 시 LLM 호출 스킵
- [ ] 적 턴 중 사전 계산
- [ ] JSONL 훈련 데이터 저장
- [ ] LLM 비활성 시 기존과 100% 동일
- [ ] 빌드 성공

---

## 실행 순서

| Task | 내용 | 의존성 |
|------|------|--------|
| 1 | ScorerWeights + LLMScorer | 없음 |
| 2 | CompactBattlefieldEncoder | 없음 |
| 3 | UtilityScorer/TargetScorer 가중치 | Task 1 |
| 4 | CandidatePlanGenerator + Orchestrator | Task 1, 2, 3 |
| 5 | 시맨틱 캐싱 | Task 1 |
| 6 | 예측적 사전 호출 | Task 1, 5 |
| 7 | 훈련 데이터 수집 | Task 1, 2 |
| 8 | 정리 + 빌드 | 전체 |

**병렬 가능**: Task 1+2, Task 5+7
