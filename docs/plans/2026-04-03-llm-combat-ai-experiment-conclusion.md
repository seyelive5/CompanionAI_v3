# LLM 전투 AI 실험 — 결론 보고서

> 실험 기간: 2026-04-02 ~ 2026-04-03
> 모델: gemma3:4b-it-qat, qwen3:14b (Ollama 로컬)
> 결론: **로컬 LLM(4~14B)은 턴제 전투 AI 대체/보강에 비실용적**

---

## 실험 목적

CompanionAI의 TurnPlanner(Utility Scoring 기반)를 LLM으로 대체하거나 보강하여, 더 "인간적인" 전술적 의사결정이 가능한지 검증.

## 버전별 접근과 결과

### v0.1 — LLM 직접 실행
**접근:** LLM이 스킬 GUID + 타겟을 직접 선택 → ActionExecutor 실행
**결과:** 실패
- GUID 환각 (존재하지 않는 스킬 선택)
- TargetRestrictionNotPassed (게임 타겟 제한 모름)
- 턴당 1개 행동만 (AP 낭비)
**교훈:** LLM은 게임 메커니즘(GUID, 쿨다운, 사거리, 타겟 제한)을 이해하지 못함

### v0.2 — 전략 어드바이저 (TurnStrategy 힌트)
**접근:** LLM이 전략 방향("aggressive", "aoe_clear" 등) 선택 → TurnStrategy 객체에 매핑 → 기존 Plan이 힌트로 참조
**결과:** 무의미
- Plan 코드가 TurnStrategy를 "힌트"로만 취급 (강제 아님)
- `ShouldPrioritizeAoE = true`여도 AoE 조건 불충족 시 무시
- Plan의 90%가 전략과 무관하게 자율 실행
- LLM 전략 ≠ 실제 행동 (사용자 혼란)
**교훈:** 기존 Plan이 너무 자율적이라 외부 힌트가 끼어들 여지 없음

### v0.3 — 행동 메뉴 기반 TurnPlan 직접 생성
**접근:** 코드가 "사전 검증된 행동 메뉴"를 만들고, LLM이 순서를 결정, 기존 Planner가 스킬 자동 선택 → TurnPlan 직접 생성
**결과:** 부분 성공
- LLM의 첫 1~2개 행동은 실행됨 (buff → AoE 등)
- 이후 쿨다운/LOS 문제로 계획 무너짐 → TurnPlanner fallback
- 하이브리드 실행 자체는 성공 (LLM 초반 + TurnPlanner 후반)
- **긍정적 사례: DPS 캐릭터가 AoE를 적극 사용 (기존엔 단발만)**
**교훈:** LLM이 쿨다운/LOS를 모르니 복수 행동 계획은 무너짐. 단, 공간 추론(밀집 감지 → AoE)에서 가치 확인

### v0.4 — Plan 분기점 LLM 개입
**접근:** 기존 Plan 실행 중 5개 핵심 분기점에서 LLM에게 선택지 질문 (타겟 우선순위, 버프 여부, 이동, 킬 vs AoE, 리스크 평가)
**결과:** 기술적 성공, 실질적 무차별
- LLM 호출 + 답변 저장 + Plan에서 읽기 정상 작동
- 하지만 기존 스코어링이 이미 충분히 좋아서 LLM의 다른 선택이 나올 상황이 드묾
- 사용자 체감: "변화를 모르겠다"
**교훈:** 기존 시스템이 잘 되고 있으면 LLM 보강의 여지가 없음

---

## 핵심 발견사항

### LLM이 못하는 것 (로컬 4~14B)
1. **게임 메커니즘 이해** — 쿨다운, LOS, 사거리, 타겟 제한, AP 조합
2. **정확한 산수** — AP 비용 합산, 사용 횟수 계산
3. **상태 추적** — 어떤 능력이 이미 쿨다운인지, 어떤 버프가 활성 중인지
4. **일관된 계획** — 4+ 행동의 유효한 시퀀스 생성

### LLM이 잘하는 것
1. **공간 추론** — ASCII 맵에서 밀집 감지 → AoE 추천 (v0.3에서 확인)
2. **자연어 추론** — "적이 밀집해 있으니 범위 공격이 효율적" (reasoning 품질)
3. **역할 이해** — 서포트는 힐/버프 우선, DPS는 공격 우선 (프롬프트로 유도 시)

### 구조적 한계
- **기존 TurnPlanner가 이미 매우 우수** — 3000줄, 90+ 메서드, 15+ Phase로 대부분의 전술적 판단을 커버
- **LLM이 좋았던 순간** (AoE 적극 사용)은 기존 AoE 스코어링 가중치 조정으로도 달성 가능
- LLM 호출 시간 (3~17초)이 게임 플레이에 부담

---

## 기술적 산출물

### 생성된 파일 (제거 대상)
```
LLM_CombatAI/
├── LLMCombatSettings.cs       — v0.1~v0.4 설정 + 프롬프트
├── BattlefieldSerializer.cs   — v0.1 전장 JSON 직렬화
├── LLMCommandValidator.cs     — v0.1 명령 검증
├── LLMDecisionEngine.cs       — v0.1 의사결정 엔진
├── LLMCombatLogger.cs         — v0.1 통계
├── LLMStatusOverlay.cs        — v0.1~v0.4 오버레이
├── StrategyContextBuilder.cs  — v0.2 자연어 상황 설명
├── LLMStrategyEngine.cs       — v0.2 전략 매핑
├── LLMStrategyLogger.cs       — v0.2 비교 통계
├── BattlefieldMapBuilder.cs   — v0.3 ASCII 그리드 맵
├── ActionMenuBuilder.cs       — v0.3 행동 메뉴
├── LLMPromptAssembler.cs      — v0.3 프롬프트 조립
├── LLMPlanBuilder.cs          — v0.3 응답→TurnPlan 변환
├── DecisionPointCollector.cs  — v0.4 분기점 수집
└── LLMDecisionAnswers.cs      — v0.4 답변 저장
```

### 수정된 파일 (롤백 대상)
- `Core/TurnOrchestrator.cs` — HandleLLMTurn 분기
- `Planning/Plans/BasePlan.cs` — EvaluateOrReuseStrategy LLM 코드
- `Planning/Plans/DPSPlan.cs` — LLM 답변 읽기
- `Planning/Plans/TankPlan.cs` — LLM 타겟 오버라이드
- `Planning/Plans/SupportPlan.cs` — LLM 타겟 오버라이드
- `MachineSpirit/LLMClient.cs` — SendOllamaNonStreaming
- `UI/MainUI.cs` — LLM 모드 토글 + 모델 선택
- `UI/DecisionOverlayUI.cs` — LLM 오버레이 연결
- `Settings/ModSettings.cs` — EnableLLMMode, LLMModelName
- `GameInterface/TurnEventHandler.cs` — LLM 이벤트 훅
- `Core/StrategicContextKeys.cs` — LLM 키
- `Info.json` — 버전

### 재사용 가능한 코드/아이디어
- `BattlefieldMapBuilder.cs` — ASCII 그리드 맵은 디버그/시각화 용도로 유용
- `StrategyContextBuilder.cs` — 자연어 상황 설명은 Machine Spirit 대화에 활용 가능
- `ActionMenuBuilder.cs` — 행동 메뉴는 향후 AI 디버그 UI로 재활용 가능
- `SendOllamaNonStreaming()` — 비스트리밍 Ollama 호출은 다른 기능에서 사용 가능

---

## 향후 방향 제안

### LLM이 적합한 영역
1. **Machine Spirit 대화 강화** — 전투 중 코멘터리, 상황 해설 (이미 작동 중)
2. **전투 후 분석** — 전투 로그를 읽고 "이번 전투에서 아르젠타가 MVP" 같은 요약
3. **전략 코멘터리** — AI 결정을 자연어로 설명 (학습/엔터테인먼트)

### 기존 코드 개선 포인트 (LLM 없이)
- **AoE 스코어링 가중치 상향** — v0.3에서 LLM이 AoE를 더 잘 골랐던 현상은 가중치 조정으로 해결 가능
- `TurnStrategyPlanner`의 AoEFocus 시드 점수 보정

---

## 최종 결론

> "로컬 LLM(4~14B)은 턴제 전투 AI의 대체나 보강에 적합하지 않다."
> 
> 근거: 게임 메커니즘의 복잡한 제약 조건(쿨다운, LOS, 사거리, AP)을 이해하지 못하며,
> 기존 Utility Scoring 시스템이 이미 충분히 우수하여 LLM의 추가 가치가 미미하다.
> 
> 예외: 공간 추론(적 밀집 감지 → AoE 추천)에서 부분적 가치가 확인되었으나,
> 이는 기존 스코어링 가중치 조정으로도 달성 가능하다.
> 
> 원래 설계 문서의 예측 "TurnPlanner 대비 품질 낮음 — 예상됨"이 정확했다.
