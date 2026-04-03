# LLM 전략 어드바이저 v0.2 — 설계서

> v0.1 실험 결과: LLM이 합리적 판단 가능하나, 게임 메커니즘(GUID, 타겟 제한, AP)을 모름.
> v0.2 방향: LLM은 "전술적 의도"만 결정, 실행은 기존 TurnPlanner가 전담.
> 확정일: 2026-04-02

---

## 핵심 아이디어

**현재:** `TurnStrategyPlanner`가 10개 시드를 점수로 평가 → 최고 점수 선택 → Plan 실행
**개선:** LLM이 자연어 상황을 보고 전략 방향 결정 → `TurnStrategy` 객체 생성 → 기존 Plan 실행

```
현재: Situation → TurnStrategyPlanner(점수 평가) → TurnStrategy → Plan 실행
v0.2: Situation → LLM(전술적 판단)             → TurnStrategy → Plan 실행
                                                    ↑
                                              바뀌는 건 여기만
```

**Plan 코드(DPSPlan, TankPlan, SupportPlan, OverseerPlan)는 한 줄도 변경하지 않음.**

---

## 확정된 결정사항

| 항목 | 결정 | 근거 |
|------|------|------|
| LLM 역할 | 전략 어드바이저 (TurnStrategy 생성) | 게임 메커니즘은 기존 코드가 처리 |
| 기존 시스템 | 병행 운영 (LLM vs 점수 비교) | 실험 데이터 수집 목적 |
| LLM 입력 | 자연어 상황 설명 (풍부하게) | 로컬 모델이므로 토큰 비용 없음, 풍부할수록 정확 |
| LLM 출력 | 구조화된 JSON (strategy + focus_target + reasoning) | 코드 파싱 명확성 |
| 전략 목록 | 전술적 의도로 재포장 (시드 번호 아닌 의미 있는 이름) | LLM 추론 품질 향상 |
| 사용자 피드백 | 기존 대사 시스템(TacticalOverlayUI)으로 reasoning 표시 | "생각 중..." → 결과 자연스럽게 표시 |
| 비교 오버레이 | LLM 선택 vs 점수 선택 병렬 표시 | 일치율/독자 선택 통계 |

---

## 아키텍처

### 턴 처리 흐름

```
TurnOrchestrator.ProcessTurn()
    ↓
SituationAnalyzer.Analyze() → Situation 생성
    ↓
Plan.CreatePlan() → EvaluateOrReuseStrategy()
    ↓
"이 유닛 LLM 모드?" (LLMCombatSettings.IsLLMControlled)
    │
    ├─ 예 ─→ [병렬 실행]
    │        ├─ TurnStrategyPlanner.Evaluate() → 점수 기반 전략 (비교용)
    │        └─ LLMStrategyEngine.Decide()     → LLM 기반 전략 (채택)
    │        → LLM 전략 → TurnStrategy 매핑
    │        → 비교 로깅 (LLM vs 점수)
    │        → TacticalOverlayUI에 reasoning 표시
    │        → 실패 시 점수 기반 전략 fallback
    │
    └─ 아니오 → TurnStrategyPlanner.Evaluate() (기존 흐름 그대로)
    ↓
Plan이 TurnStrategy를 읽고 Phase별 실행 (변경 없음)
```

### 통합 지점

**단 하나:** `BasePlan.EvaluateOrReuseStrategy()` (BasePlan.cs line ~2333)

```csharp
// 현재
strategy = TurnStrategyPlanner.Evaluate(situation, role);

// v0.2 (LLM 모드)
var scoreStrategy = TurnStrategyPlanner.Evaluate(situation, role);  // 비교용
var llmStrategy = LLMStrategyEngine.Evaluate(unit, situation, role); // LLM 판단
strategy = llmStrategy ?? scoreStrategy;  // LLM 실패 시 점수 fallback
LLMStrategyLogger.Compare(llmStrategy, scoreStrategy);  // 비교 기록
```

---

## LLM 입력: 자연어 상황 설명

`ContextBuilder`가 Situation 객체를 자연어로 변환:

```
You are Argenta, a DPS (ranged) unit. Round 3 of combat.
Your stats: AP 4, MP 6, HP 85%. Equipped: Bolter (range 12 tiles).

== Battlefield ==
Allies (5):
- Heinrix (Support) HP 25% — 3 tiles to your left. CRITICAL.
- Abelard (Tank) HP 80% — front line, engaging 2 enemies.
- Cassia (DPS) HP 90% — rear, safe position.
- Idira (Support) HP 70% — center.
- Yrliet (DPS) HP 60% — right flank.

Enemies (4):
- Chaos Marine (HP 15%) — adjacent to Abelard. FINISHABLE.
- Cultist A (HP 100%) — 8 tiles away, clustered with Cultist B.
- Cultist B (HP 100%) — 7 tiles away, clustered with Cultist A.
- Chaos Lord [BOSS] (HP 90%) — 15 tiles, acts next turn. HIGH THREAT.

== Tactical Assessment ==
- 2 enemies clustered (Cultist A, B) — AoE effective (2+ targets).
- 1 enemy finishable (Chaos Marine, HP 15%).
- 1 ally critical (Heinrix, HP 25%).
- Boss acts next turn — high pressure.

== Your Available Strategies ==
- aggressive: Focus fire on a priority target. Best for finishing wounded enemies.
- aoe_clear: Area attack on clustered enemies. Best when 2+ enemies grouped.
- defensive: Protect/heal allies, retreat if needed. Best when allies critical.
- buff_setup: Apply buffs before attacking. Best for burst damage next action.
- debuff_first: Weaken enemy before attacking. Best vs high-defense targets.
- focus_boss: Prioritize the boss. Best when boss is the primary threat.

Choose ONE strategy and explain your reasoning.
```

### LLM 출력

```json
{
  "strategy": "aoe_clear",
  "focus_target": "Cultist A",
  "reasoning": "Cultist A and B are clustered — AoE clears both. Chaos Marine is low HP, Abelard can finish it. Boss is far, deal with it next turn."
}
```

---

## 전략 → TurnStrategy 매핑

| LLM 전략 | SequenceType | TurnStrategy 필드 |
|-----------|-------------|-------------------|
| aggressive | DirectAttack / KillSequence | PrioritizesKillSequence=true (if finishable), BestTarget=focus_target |
| aoe_clear | AoEFocus / BuffedAoE | ShouldPrioritizeAoE=true, RecommendedAoE=best AoE ability |
| buff_setup | BuffedAttack / BuffedRnGChain | ShouldBuffBeforeAttack=true, RecommendedBuff=best buff |
| debuff_first | DebuffedAttack | ShouldDebuffBeforeAttack=true |
| defensive | Standard | (힐/후퇴는 Phase 1에서 자동 처리, 전략은 공격 최소화) |
| focus_boss | DirectAttack | BestTarget=boss entity |

매핑 시 기존 TurnStrategyPlanner의 시드 평가 데이터(예상 데미지, 사용 가능 버프 등)를 참조하여 TurnStrategy 필드를 채움. LLM이 "buff_setup"을 골랐지만 버프가 없으면 "aggressive"로 자동 보정.

---

## 대사 시스템 통합 (TacticalOverlayUI)

### LLM 처리 중 (코루틴 대기)

```
TacticalOverlayUI.Show(
    unitName: "Argenta",
    lines: new[] { "전술 분석 중..." },
    nameColor: companionColor,
    duration: 30f  // LLM 응답까지 유지
);
```

### LLM 결과 수신 후

```
TacticalOverlayUI.Show(
    unitName: "Argenta",
    lines: new[] { reasoning },  // LLM의 reasoning 그대로
    nameColor: companionColor,
    duration: 5f
);
```

기존 TacticalNarrator의 대사와 자연스럽게 교체됨 (같은 UI 사용).

---

## 비교 오버레이 (LLMStatusOverlay 확장)

```
┌─ LLM 전략 어드바이저 ─────────────────┐
│ 유닛: Argenta (DPS)                    │
│                                        │
│ LLM 전략: aoe_clear                    │
│ LLM 이유: "적 2명 밀집, AoE로 정리"    │
│ 점수 전략: aggressive (85점)            │
│                                        │
│ ★ 채택: LLM (aoe_clear)               │
│ 응답시간: 2.1초                         │
│                                        │
│ [통계] 일치: 12/20 (60%)               │
│ LLM 독자 선택: 8회                      │
└────────────────────────────────────────┘
```

---

## 파일 구조 (v0.1 확장)

### 새 파일
```
LLM_CombatAI/
├── LLMStrategyEngine.cs     — 상황 설명 생성 + LLM 호출 + 전략 매핑
├── StrategyContextBuilder.cs — Situation → 자연어 변환
└── LLMStrategyLogger.cs     — LLM vs 점수 비교 통계
```

### 수정 파일
```
Planning/Plans/BasePlan.cs         — EvaluateOrReuseStrategy()에 LLM 분기 추가
LLM_CombatAI/LLMCombatSettings.cs  — v0.2 시스템 프롬프트 업데이트
LLM_CombatAI/LLMStatusOverlay.cs   — 비교 오버레이 확장
Core/TurnOrchestrator.cs           — v0.1 직접 실행 분기 → v0.2 전략 분기로 교체
```

### 제거/비활성화 (v0.1 코드)
```
LLM_CombatAI/BattlefieldSerializer.cs  — StrategyContextBuilder로 대체
LLM_CombatAI/LLMCommandValidator.cs    — 더 이상 필요 없음 (GUID 검증 불필요)
LLM_CombatAI/LLMDecisionEngine.cs      — LLMStrategyEngine으로 대체
TurnOrchestrator의 HandleLLMTurn()     — v0.2에서는 Plan 레벨에서 분기
```

---

## 성공 기준

- [ ] LLM이 유효한 전략 JSON을 90%+ 반환 (선택지가 제한적이므로 높아야 함)
- [ ] LLM 전략으로 Plan이 정상 실행 (Phase 0~6 전체 수행, AP 완전 소진)
- [ ] LLM vs 점수 비교 통계 오버레이 표시
- [ ] 대사 시스템으로 reasoning 표시 ("전술 분석 중..." → 결과)
- [ ] LLM 실패 시 기존 점수 기반 전략 fallback 정상 작동
- [ ] 게임 크래시 없음

## 알려진 제한사항 (v0.2)

- 턴당 1회 전략 결정 (replan 시 캐시된 전략 재사용)
- LLM 응답 대기 중 게임 일시정지 (기존 2-Phase Frame Spreading 활용)
- 팀 전체 전략 조율 없음 (유닛별 독립 판단)
