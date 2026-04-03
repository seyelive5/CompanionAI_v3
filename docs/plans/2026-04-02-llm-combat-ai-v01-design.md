# LLM 전투 AI v0.1 — 구현 설계서

> 원본 실험 설계: [2026-03-26-llm-combat-ai-experiment.md](2026-03-26-llm-combat-ai-experiment.md)
> 확정일: 2026-04-02

---

## 확정된 결정사항

| 항목 | 결정 | 근거 |
|------|------|------|
| 프로젝트 구조 | CompanionAI 내부 `LLM_CombatAI/` 하위 폴더 | 기존 코드 직접 재사용, 실패 시 폴더 삭제로 정리 |
| 후킹 | Harmony (기존 유지) | 검증됨, 패치 포인트 소수, MonoDetour 전환 불필요 |
| LLM 호출 | 비스트리밍 (`stream: false`, `format: "json"`) | 전술 결정은 완성된 JSON 하나만 필요 |
| 유닛 선택 | UI에서 유닛별 자유 토글 (제한 없음) | 기술적으로 1명이든 6명이든 같은 코드 |
| 턴 분기 | TurnOrchestrator에서 LLM/TurnPlanner 분기 | 기존 코드 최소 수정, ActionExecutor 공유 |
| Fallback | TurnPlanner + 실시간 상태 오버레이 | 실패 이유 가시성 확보 (실험 관찰 목적) |
| 행동 범위 | 전체 (공격, 이동, 버프, 힐, AoE 등) | LLM에게 스킬은 전부 "목록에서 고르기", 제한 불필요 |
| 응답 시간 | 제한 없음 | 실험이므로 감내 |

---

## 파일 구조

```
LLM_CombatAI/
├── LLMCombatSettings.cs       — 유닛별 LLM 모드 토글, 모델명
├── BattlefieldSerializer.cs   — 전장 상태 → JSON 직렬화
├── LLMDecisionEngine.cs       — Ollama 비스트리밍 호출 + JSON 파싱
├── LLMCommandValidator.cs     — 파싱된 명령 유효성 검증
├── LLMStatusOverlay.cs        — 실시간 상태/reasoning 화면 표시
└── LLMCombatLogger.cs         — 누적 통계 (성공률, 응답시간)
```

## 기존 코드 재사용

| CompanionAI 모듈 | 재사용 방식 |
|------------------|-------------|
| TurnOrchestrator | 분기점 추가 (LLM 유닛 감지) |
| CombatAPI | 직접 호출 (거리, AP, 능력 조회) |
| ActionExecutor | 그대로 사용 (LLM 명령 → 게임 실행) |
| AbilityDatabase | GUID 조회에 활용 |
| LLMClient | `SendOllamaSync()` 메서드 추가 |
| MainUI | 유닛별 LLM 토글 추가 |
| DirectiveOverlay | LLMStatusOverlay 기반 |

---

## 턴 처리 흐름

```
TurnOrchestrator.ProcessTurn()
    ↓
"이 유닛 LLM 모드?" (LLMCombatSettings.IsLLMControlled(unit))
    │
    ├─ 예 ─→ BattlefieldSerializer.Serialize(unit, situation)
    │        → LLMDecisionEngine.Decide(serializedState)
    │        → LLMCommandValidator.Validate(command, unit)
    │            ├─ 성공 → ActionExecutor.Execute(plannedAction)
    │            │         + LLMStatusOverlay: 초록 (reasoning 표시)
    │            └─ 실패 → TurnPlanner.CreatePlan() (기존 AI fallback)
    │                      + LLMStatusOverlay: 빨강 (실패 이유 표시)
    │
    └─ 아니오 → 기존 TurnPlanner 흐름 (변경 없음)
```

---

## 직렬화 형식

```json
{
  "unit": {
    "name": "Argenta",
    "hp": "85%",
    "ap": 4,
    "mp": 6,
    "pos": [12, 8],
    "skills": [
      {"id": "guid1", "name": "Bolter Shot", "type": "attack", "ap": 1, "range": 12, "dmg": "18-24"},
      {"id": "guid2", "name": "Burst Fire", "type": "attack", "ap": 2, "range": 8, "dmg": "12-16", "aoe": true},
      {"id": "guid3", "name": "Heal", "type": "heal", "ap": 2, "range": 6},
      {"id": "guid4", "name": "War Cry", "type": "buff", "ap": 1}
    ]
  },
  "enemies": [
    {"id": "e1", "name": "Chaos Marine", "hp": "40%", "pos": [15, 10], "threat": "high"},
    {"id": "e2", "name": "Cultist", "hp": "100%", "pos": [18, 7], "threat": "low"}
  ],
  "allies": [
    {"name": "Heinrix", "hp": "70%", "pos": [11, 9], "role": "support"}
  ]
}
```

## 시스템 프롬프트

```
You are a tactical combat AI for Warhammer 40K: Rogue Trader.
Given the battlefield state, choose the best action.

Rules:
- You can move AND use a skill in one turn (if AP/MP allows)
- Prioritize: finish wounded enemies > heal critically injured allies > buff before big attacks > deal maximum damage
- Consider weapon range — don't move unnecessarily if already in range
- NEVER target allies with attack skills
- Use healing on allies with lowest HP first
- Buffs should be used before attacking when AP allows

Respond ONLY with valid JSON:
{
  "reasoning": "brief tactical explanation",
  "action": "attack" | "move" | "move_and_attack" | "buff" | "heal" | "defend",
  "target": "enemy_id or ally_id",
  "skill": "skill_id from the provided list",
  "move_to": [x, y] (optional, only if moving)
}
```

## Ollama 설정

- Temperature: 0.3
- num_predict: 200
- `format: "json"` 강제
- 모델: 사용자 선택 (LLMCombatSettings)

---

## 상태 오버레이 (LLMStatusOverlay)

### 성공 시
```
┌─ LLM Combat AI ──────────────────┐
│ 유닛: Argenta                     │
│ 상태: ✓ LLM 성공                  │
│ 응답시간: 1.8초                    │
│ LLM 판단: "적 Chaos Marine HP 40% │
│   — Bolter Shot으로 마무리"        │
│ 명령: Attack → e1 (Bolter Shot)   │
└───────────────────────────────────┘
```

### 실패 시
```
┌─ LLM Combat AI ──────────────────┐
│ 유닛: Argenta                     │
│ 상태: ✗ FALLBACK (TurnPlanner)    │
│ 실패 이유: 존재하지 않는 스킬 GUID │
│ LLM 원본: {"skill":"fake_guid"}   │
│ → TurnPlanner가 대신 처리         │
└───────────────────────────────────┘
```

### 전투 종료 시 통계
```
┌─ LLM 전투 통계 ──────────────────┐
│ 총 턴: 24                         │
│ LLM 성공: 19 (79.2%)             │
│ Fallback: 5 (20.8%)              │
│ 평균 응답시간: 2.3초               │
│ 실패 원인: GUID 환각 3, 파싱 2    │
└───────────────────────────────────┘
```

---

## 성공 기준

- [ ] LLM이 유효한 JSON을 80%+ 반환
- [ ] 공격/이동/버프/힐 모든 행동 실행 가능
- [ ] Fallback 시 TurnPlanner 정상 작동 + 실패 이유 오버레이 표시
- [ ] 누적 통계 전투 종료 시 표시
- [ ] 게임 크래시 없음

## 리스크 & 완화

| 리스크 | 완화 |
|--------|------|
| LLM 응답 느림 | 실험이므로 감내, 통계로 기록 |
| JSON 파싱 실패 | `format: "json"` 강제, TurnPlanner fallback |
| 스킬 GUID 환각 | LLMCommandValidator 검증 + fallback |
| 다수 유닛 대기 시간 | 순차 처리, 오버레이로 진행 상황 표시 |
