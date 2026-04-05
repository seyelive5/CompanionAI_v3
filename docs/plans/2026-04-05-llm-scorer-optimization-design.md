# LLM-as-Scorer + 성능 최적화 — 설계서

> Phase 3+4 검증 완료 후 다음 단계.
> 연구 문서 기반: 게임 전투 AI에서 LLM을 통합하는 실전 아키텍처.md
> 확정일: 2026-04-05

---

## 핵심 변경: LLM-as-Scorer

### 현재 (Phase 4)

```
Advisor → "focus_fire 2 0.25 0.75" → +50 보너스 1개
→ 후보 생성 (아키타입 기반) → Judge 선택
문제: 아키타입 15개를 만들어도 Plan이 같은 행동 → 대부분 중복 제거
```

### 변경 후

```
LLM → 가중치 JSON 출력:
{
  "aoe_weight": 2.0,        // AoE 스코어 배율
  "focus_fire": 1.5,        // 지정 타겟 스코어 배율
  "priority_target": 2,     // 적 인덱스
  "heal_priority": -0.3,    // 힐 우선순위 오프셋
  "buff_priority": 0.8,     // 버프 스코어 배율
  "defensive_stance": false  // 방어적 포지셔닝
}

→ UtilityScorer/TargetScorer가 이 가중치로 점수 계산
→ 가중치 적용 플랜 vs 기본 플랜 → Judge 비교 선택
```

### 왜 이게 후보 중복을 해결하는가

현재: 전략 플래그(ShouldPrioritizeAoE 등) → Plan이 힌트로만 참조, 무시 가능
변경: **점수 자체를 곱셈/가산으로 수정** → UtilityScorer가 다른 능력을 1등으로 선택
→ 같은 타겟이라도 AoE 2배 가중치면 AoE 능력이 1등 → 다른 행동

### 가중치 적용 범위

| 가중치 | 적용 대상 | 효과 |
|--------|----------|------|
| `aoe_weight` | UtilityScorer.ScoreAttack() — AoE 능력 | AoE 선호도 (1.0=기본, 2.0=2배 선호) |
| `focus_fire` | TargetScorer.ScoreEnemy() — 지정 타겟 | 타겟 집중도 (1.5=50% 보너스) |
| `priority_target` | TargetScorer — 타겟 인덱스 | 집중할 적 번호 |
| `heal_priority` | UtilityScorer.ScoreHeal() | 힐 가중치 오프셋 (-0.3=30% 감소) |
| `buff_priority` | BuffPlanner.ScoreAttackBuff() | 버프 스코어 배율 |
| `defensive_stance` | PositionEvaluator | 방어적 위치 선호 |

### 후보 생성 (단순화)

```
Plan A: LLM 가중치 적용 → CreatePlan()
Plan B: 기본 가중치 (LLM 없음) → CreatePlan()
→ Judge가 A vs B 선택
→ A 선택 시 LLM이 실질적 영향
→ B 선택 시 기존 AI가 더 나았다는 의미 (데이터 수집)
```

아키타입 15개 → 2개로 단순화. 가중치 자체가 무한한 조합을 만듦.

---

## Step 2: 프롬프트 압축

### 현재 (~300 토큰)

```
**Unit:** Argenta (DPS, HP:85%, AP:4, MP:10)
**Weapon:** Bolter (ranged, range 12)
**Allies:** Abelard (Tank, HP:93%, 5 tiles), Heinrix (Support, HP:25% CRITICAL)
...
```

### 압축 후 (~100 토큰)

```
U:Argenta,DPS,HP85,AP4,MP10,Wpn:Bolter/12
A:Abelard,Tank,HP93,d5|Heinrix,Sup,HP25!,d3
E:0:Psyker,HP40,d5,HI|1:Cult,HP100,d8|2:Cult,HP100,d8,CL|3:Heavy,HP90,d15
K:0 finishable|1,2 clustered|Heinrix critical
```

약축 규칙:
- HP=퍼센트(정수), d=거리(타일), !=CRITICAL, HI=HIGH PRIORITY, CL=CLUSTERED
- 적은 인덱스 번호 부여 (priority_target 참조용)
- K=Key factors (핵심 전술 요소)

### 시스템 프롬프트도 압축

현재 ~200토큰 → ~80토큰:
```
Tactical advisor for turn-based combat. Output scoring weights as JSON.
Keys: aoe_weight(float), focus_fire(float), priority_target(int), heal_priority(float), buff_priority(float), defensive_stance(bool).
Default all 1.0/0/false. Only change what matters.
```

### 예상 효과

- 프롬프트: 300→100 토큰 (3배 감소)
- 출력: 변경 없음 (~30 토큰 JSON)
- 프리픽스 캐싱: 시스템 프롬프트 80토큰 캐시 → 실질 처리 ~100토큰
- 예상 응답: 현재 0.9초 → **~0.3초**

---

## Step 3: 시맨틱 캐싱

### 설계

전투 상태를 해시로 변환하여, 유사한 상황에서 이전 LLM 결정 재사용.

```csharp
public static class LLMScorerCache
{
    // 캐시: 상태 해시 → LLM 가중치 JSON
    private static Dictionary<long, ScorerWeights> _cache;

    public static long ComputeHash(Situation situation)
    {
        // 핵심 요소만 해시:
        // - 유닛 역할 + HP 구간 (10% 단위)
        // - 적 수 + 적 HP 구간 합
        // - 아군 위기 여부 (HP < 30%)
        // - Hittable 적 수
        // → 같은 "전술적 상황"이면 같은 해시
    }
}
```

해시가 같으면 → 캐시 히트 → LLM 호출 안 함 → **0ms**
해시가 다르면 → LLM 호출 → 결과 캐시 저장

### 캐시 무효화

- 전투 시작: 캐시 초기화
- 적 사망: 해당 해시 무효화
- 3라운드 경과: 전체 캐시 클리어 (상황 변화 반영)

### 예상 히트율

같은 턴에 여러 아군이 비슷한 상황 → 높은 히트율 예상.
다만 첫 턴은 캐시 미스 → 점진적 효과.

---

## Step 4: 예측적 사전 호출

### 설계

적 턴 중에 아군 캐릭터의 LLM 분석을 미리 시작.

```
아군 턴 종료 → 적 턴 시작
  → 백그라운드: 다음 아군 순서 캐릭터의 Situation 사전 분석
  → 백그라운드: LLM-as-Scorer 사전 호출 (비동기)
적 턴 중... (2~5초 소요)
적 턴 종료 → 아군 턴 시작
  → 사전 계산된 가중치 즉시 사용 → **0ms LLM 대기**
```

### 구현 위치

`TurnEventHandler.HandleTurnStarted(unit)`:
- 적 턴이 시작되면 → 다음 아군 유닛 예측
- `CoroutineRunner.Start(LLMScorer.PreComputeAsync(nextAlly, situation))`

### 제한사항

- 적 턴 중 상황이 바뀔 수 있음 (적이 아군을 공격) → 사전 계산 결과가 stale
- 해결: 아군 턴 시작 시 상황 변화 감지 → 변화 크면 재계산, 작으면 사전 결과 사용

---

## Step 5: 훈련 데이터 수집

### JSONL 형식

```jsonl
{"timestamp":"2026-04-05T12:30:00","unit":"Argenta","role":"DPS","state":"U:Argenta,DPS,HP85...","weights":{"aoe_weight":2.0,"focus_fire":1.5,...},"plan_summary":"Buff→AoE→Attack","actions":["Buff:속사","Attack:점사→Psyker","Attack:단발→Cultist"],"outcome":{"kills":2,"damage_dealt":180,"damage_taken":0,"ap_used":5,"ap_total":5}}
```

### 수집 시점

매 턴 종료 시:
1. 전투 상태 (압축 형태)
2. LLM 출력 가중치
3. 생성된 플랜 요약
4. 실행 결과 (킬, 데미지, AP 소진)

### 저장 위치

`%AppData%/CompanionAI/training_data/combat_YYYYMMDD.jsonl`

### 활용

- 1,000개+ 쌍이면 QLoRA 파인튜닝 가능
- Claude API로 CoT 추론 증류 → 고품질 훈련 데이터
- 파인튜닝은 별도 Python 작업 (이 설계 범위 밖)

---

## 구현 순서 및 의존성

```
Step 1: LLM-as-Scorer (가중치 JSON)
  → LLMScorer.cs 신규
  → UtilityScorer/TargetScorer 가중치 참조 확장
  → CandidatePlanGenerator 단순화 (아키타입 → 가중치 2개)
  → Advisor/Judge 교체

Step 2: 프롬프트 압축
  → CompactBattlefieldEncoder.cs 신규
  → 시스템 프롬프트 축소

Step 3: 시맨틱 캐싱
  → LLMScorerCache.cs 신규
  → HandleLLMJudge에 캐시 체크 추가

Step 4: 예측적 사전 호출
  → TurnEventHandler에 사전 호출 추가
  → HandleLLMJudge에 사전 결과 참조

Step 5: 훈련 데이터 수집
  → TrainingDataCollector.cs 신규
  → 턴 종료 시 JSONL 저장
```
