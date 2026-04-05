# LLM 전투 AI 실험 — 프로젝트 설계 문서

## 목적

로컬 LLM(Ollama)이 턴제 전투에서 실제 전술적 의사결정을 내릴 수 있는지 실험.
CompanionAI와 별도 프로젝트로, 실패해도 기존 모드에 영향 없음.

## 핵심 질문 (실험으로 답할 것)

1. LLM이 전장 상태를 이해하고 합리적 전술을 선택할 수 있는가?
2. 턴 당 응답 시간이 게임 플레이에 허용 가능한 수준인가? (목표: <3초)
3. JSON 출력의 신뢰도 — 유효한 명령을 몇 % 비율로 반환하는가?
4. 어떤 모델 크기에서 실용적인가? (4B? 12B? 27B?)

## 아키텍처

```
턴 시작 (AI 유닛)
    ↓
[Harmony Prefix] PartUnitBrain.Tick 가로채기
    ↓
전장 상태 직렬화 (JSON)
  - 현재 유닛: 위치, HP, AP, MP, 장비, 사용 가능 스킬
  - 적 유닛: 위치, HP, 위협도 (최대 5명)
  - 아군 유닛: 위치, HP (간략)
  - 지형: 유닛 주변 타일 엄폐 등급 (간략)
    ↓
시스템 프롬프트 + 상태 JSON → Ollama /api/chat
    ↓
JSON 응답 파싱
  {
    "reasoning": "적 A가 HP 25%로 가장 약함...",
    "action": "attack",
    "target": "enemy_id",
    "skill": "ability_guid",
    "move_to": [x, y] (선택적)
  }
    ↓
유효성 검증
  - 스킬 GUID 존재?
  - 타겟 사거리 내?
  - AP/MP 충분?
    ↓
[통과] → 게임 명령 실행 (CastAbility / MoveTo)
[실패] → 바닐라 AI fallback (원본 BehaviourTree 실행)
```

## 최소 프로토타입 (v0.1)

### 범위
- **1명의 유닛만** LLM 제어 (UI에서 선택)
- 나머지 파티원은 게임 기본 AI
- 공격과 이동만 지원 (버프/힐/AoE는 v0.2)
- 실패 시 바닐라 AI fallback

### 파일 구조
```
LLM_CombatAI/
├── Info.json
├── LLM_CombatAI.csproj
├── Main.cs                    — UMM 진입점
├── BrainPatch.cs              — Harmony 패치 (PartUnitBrain.Tick)
├── BattlefieldSerializer.cs   — 전장 → JSON 직렬화
├── LLMDecisionEngine.cs       — Ollama 호출 + JSON 파싱
├── CommandExecutor.cs         — 파싱된 명령 → 게임 실행
└── Settings.cs                — 모델명, 활성화 유닛 선택
```

## 기술 결정

### Harmony vs MonoMod
**Harmony 사용.** 이유:
- CompanionAI에서 이미 검증됨
- PartUnitBrain.Tick 패치 경험 있음
- MonoMod 전환은 Harmony에서 문제가 발생할 때만

### 직렬화 방식
최소한의 토큰으로 최대 정보 전달:
```json
{
  "unit": {
    "name": "Argenta", "hp": "85%", "ap": 4, "mp": 6,
    "pos": [12, 8],
    "skills": [
      {"id": "guid1", "name": "Bolter Shot", "ap": 1, "range": 12, "dmg": "18-24"},
      {"id": "guid2", "name": "Burst Fire", "ap": 2, "range": 8, "dmg": "12-16", "aoe": true}
    ]
  },
  "enemies": [
    {"id": "e1", "name": "Chaos Marine", "hp": "40%", "pos": [15, 10], "threat": "high"},
    {"id": "e2", "name": "Cultist", "hp": "100%", "pos": [18, 7], "threat": "low"}
  ],
  "allies": [
    {"name": "Heinrix", "hp": "70%", "pos": [11, 9]}
  ]
}
```

### 시스템 프롬프트
```
You are a tactical combat AI for Warhammer 40K: Rogue Trader.
Given the battlefield state, choose the best action.

Rules:
- You can move AND attack in one turn (if AP/MP allows)
- Prioritize: finish wounded enemies > protect low-HP allies > deal maximum damage
- Consider weapon range — don't move unnecessarily if already in range
- NEVER target allies

Respond ONLY with valid JSON:
{
  "reasoning": "brief explanation",
  "action": "attack" | "move" | "move_and_attack" | "defend",
  "target": "enemy_id",
  "skill": "skill_id from the list",
  "move_to": [x, y] (optional, only if moving)
}
```

### Ollama 설정
- Temperature: 0.3 (낮게 — 일관성 우선)
- num_predict: 200 (짧은 응답)
- JSON mode 강제 (Ollama `format: "json"` 파라미터)

## CompanionAI에서 재사용할 코드

| 모듈 | 재사용 | 비고 |
|------|--------|------|
| Harmony 패치 패턴 | ✓ | PartUnitBrain.Tick Prefix |
| CombatAPI | ✓ | GetDistanceInTiles, GetAbilityAPCost 등 |
| Ollama 스트리밍 | △ | 비스트리밍 (JSON mode)으로 단순화 |
| AbilityDatabase | △ | GUID 조회에 일부 활용 |
| GameKnowledge | × | 전투 AI에는 불필요 |
| Machine Spirit | × | 별도 프로젝트 |

## 성공 기준

### v0.1 "작동한다"
- [ ] LLM이 유효한 JSON을 80%+ 비율로 반환
- [ ] 반환된 명령으로 유닛이 실제 이동/공격 실행
- [ ] 턴 당 평균 응답 시간 <5초 (12B 모델)
- [ ] Fallback이 정상 작동 (실패 시 바닐라 AI)
- [ ] 게임 크래시 없음

### v0.2 "쓸만하다"
- [ ] 공격, 이동, 버프, 힐 모든 행동 지원
- [ ] 파티 전체 (6명) LLM 제어
- [ ] 턴 당 평균 <3초
- [ ] 바닐라 AI 대비 전투 효율 70%+ (HP 손실 기준)

### v0.3 "인상적이다"
- [ ] 환각률 5% 미만
- [ ] 창발적 전술 관찰 (LLM이 프로그래밍하지 않은 전략 사용)
- [ ] CompanionAI 통합 가능 수준

## 리스크 & 완화

| 리스크 | 확률 | 완화 |
|--------|------|------|
| LLM 응답 너무 느림 | 높음 | 4B 모델 테스트, 프롬프트 축소 |
| JSON 파싱 실패 | 중간 | `format: "json"` 강제, 3회 재시도 |
| 없는 스킬 GUID 환각 | 높음 | 유효성 검증 + fallback |
| 게임 크래시 | 낮음 | try/catch 전체 래핑 + fallback |
| TurnPlanner 대비 품질 낮음 | 높음 | 예상됨 — 실험 목적 |

## 메모

- 이 프로젝트는 **실험**임. 실용성보다 가능성 검증이 목적.
- CompanionAI의 TurnPlanner (Utility Scoring)는 이미 매우 효과적. LLM 대체가 목표가 아님.
- 성공하면: "LLM 어드바이저" 모드로 CompanionAI에 통합 (TurnPlanner 결정 + LLM 코멘트)
- 실패하면: "LLM은 전투 AI에 부적합"이라는 검증된 결론
