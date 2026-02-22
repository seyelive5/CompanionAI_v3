# WORK_TRACKER.md — 미완성 작업 추적

> **Claude 필수 규칙**: 매 세션 시작 시 이 파일을 읽고, 미완성 항목을 사용자에게 보고할 것.
> "완료"라고 말하기 전에 해당 항목의 체크리스트를 전부 통과했는지 확인할 것.

---

## 미완성 기능 목록

### 1. TurnStrategy 시스템 (v3.11.0~) — ★ v3.19.6 완성

**현상**: ~~전략이 수립되지만 실제 계획에 반영되지 않는 Phase가 많음. Replan 시 전략 소실.~~

**v3.19.0에서 해결된 항목**:
- [x] TurnStrategy 클래스 정의 (Core/TurnStrategy.cs)
- [x] TurnStrategyPlanner 10-시드 평가 엔진 (Planning/TurnStrategyPlanner.cs)
- [x] DPSPlan Phase 3 킬시퀀스 전략 참조
- [x] DPSPlan Phase 4 버프 전략 참조
- [x] DPSPlan Phase 4.4 AoE 전략 참조
- [x] DPSPlan Phase 5 AP 바닥 전략 참조
- [x] **Replan 시 전략 유지**: GetContext로 이전 전략 조회 → 유효하면 재사용
- [x] **TankPlan 전략 적용**: TurnStrategyPlanner(Role=Tank) 호출 + Phase 4.8c AoE + Phase 5 AP 바닥
- [x] **SupportPlan 전략 적용**: TurnStrategyPlanner(Role=Support) 호출 + Phase 5.5 AoE + Phase 6 AP 바닥
- [x] **OverseerPlan 전략 적용**: TurnStrategyPlanner(Role=Overseer) 호출 + Phase 4.96-97 AoE + Phase 5 AP 바닥
- [x] **PlansPostAction 활성화**: Phase 6 PostAction에서 전략이 R&G 계획 시 공격 미계획 상태에서도 R&G 시도
- [x] **ShouldDebuffBeforeAttack 활성화**: Phase 4.95 디버프 우선 적용
- [x] ~~**Role별 시드 필터**: 비-DPS Role은 시드 0,2,4,6만 평가 (40% 연산량)~~ → v3.19.6에서 제거

**v3.19.2에서 해결된 항목**:
- [x] **Replan 시 타겟 유효성 검증**: FocusTargetId로 이전 전략의 BestTarget이 사망/LOS 차단 시 전략 재평가 (전 Role 적용)
- [x] **전략 → AP 예산 강제**: APBudget.CanAfford()로 공격 루프 AP 예약 강제 (수동 deduct/restore 패턴 제거)

**v3.19.6에서 해결된 항목**:
- [x] **Role별 시드 필터 제거**: 전 Role 10개 시드 전체 평가 — 비-DPS도 버프/킬/디버프 시드 사용 가능
- [x] **Role 가중치 스코어링**: NON_DPS_COMPLEX_SEED_WEIGHT=0.85 — 비-DPS 역할은 복합 시드(BuffedAttack, KillSequence 등) 점수 15% 감소로 본업 우선 + 여유 시 DPS 행동 허용

### 2. 무기 스위칭 (v3.9.72~) — ★ v3.19.0 정상화

**현상**: ~~기능이 추가되었으나 실전에서 거의 작동하지 않음.~~

**v3.19.0에서 해결된 항목**:
- [x] WeaponSetAnalyzer 무기 분석 (Data/WeaponSetAnalyzer.cs)
- [x] PlannedAction.WeaponSwitch 액션 타입 (Core/PlannedAction.cs)
- [x] ActionExecutor 무기 전환 실행 (Execution/ActionExecutor.cs)
- [x] CombatAPI 무기 API (GameInterface/CombatAPI.cs:1166-1255)
- [x] DPSPlan Phase 1.55/1.56/9.5 전환 로직
- [x] **전 Role 적용**: TankPlan, SupportPlan, OverseerPlan에 Phase 1.55 추가
- [x] **Phase 1.55 조건 완화**: `ShouldSwitchForEffectiveness()` — 적이 공격 가능해도 대체 무기가 확연히 유리하면 전환

**구조적 제한 (게임 메커니즘)**:
- Phase 1.56/9.5: `HasWeaponSwitchBonus` (WeaponSetChangedTrigger/Versatility 피트) 없으면 작동 안 함
- 전환 후 같은 턴 공격 불가: async 재분석이 다음 프레임에서 발생 (2-Phase Frame Spreading)

### 3. Phase 간 교차 인식 — ★ v3.19.2 완성

**현상**: ~~각 Phase가 독립적으로 결정하여 서로를 방해함.~~

**v3.19.0에서 해결된 항목**:
- [x] 전략 기반 Phase 간 가이드 (AoE 우선, R&G 계획, 디버프 우선 등)
- [x] APBudget으로 buff/attack/turnEnding AP 경쟁 완화

**v3.19.2에서 해결된 항목**:
- [x] **GapCloser → Self-AoE 폴백 경로**: MovementAPI 폴백 착지 위치에도 Self-AoE 아군 안전성 적용
- [x] **능력 프로파일 추가**: Situation에 HasGapCloser, HasSelfAoE, HasTurnEndingAbility, HasRunAndGun, HasGapCloserCombo 추가 + SituationAnalyzer에서 자동 계산

### 4. AP 예약 시스템 — ★ v3.19.4 APBudget 팩토리 통합

**v3.19.0에서 해결된 항목**:
- [x] APBudget 구조체 추가 (Core/APBudget.cs)
- [x] 전 Plan에 APBudget 적용 (통합 로깅 + effectiveReservedAP)
- [x] 버프가 TurnEnding AP를 잠식하던 버그 수정 (effectiveReservedAP = PostMove + TurnEnding)
- [x] 전략 R&G AP를 예약에 포함

**v3.19.2에서 해결된 항목**:
- [x] **APBudget.CanAfford() 강제**: 전 Plan 공격 루프에서 수동 deduct/restore 패턴 → `budget.CanAfford(0, remainingAP)` 단일 체크로 교체
- [x] APBudget이 로깅뿐 아니라 실제 Phase 행동을 제한하는 enforcement 역할 수행

**v3.19.4에서 해결된 항목**:
- [x] **CreateAPBudget() 팩토리**: BasePlan에 통합 생성 메서드 추가 — 4개 Plan의 10줄 중복 생성 블록 제거
- [x] **EffectiveReserved 자동 속성**: `float effectiveReservedAP` 로컬 변수 제거 → `budget.EffectiveReserved` 자동 계산 (PostMove + TurnEnding + Strategy)
- [x] **CalculateMasterMinAttackAP() 추출**: OverseerPlan 인라인 계산 → BasePlan protected 메서드
- [x] **effectiveReservedAP 완전 제거**: 4개 Plan에서 수동 동기화 변수 제거 (budget 속성으로 대체)
- [x] **reservedAP/turnEndingReservedAP 로컬 변수 제거**: budget.PostMoveReserved/TurnEndingReserved로 대체

**구조적 한계 (의도적 유지)**:
- **ref remainingAP 패턴**: BasePlan의 63개 helper 메서드가 `ref float remainingAP`를 사용. APBudget으로 완전 이관하면 63개 시그니처 + 수백 호출 지점 변경 필요. 동작 변경 없이 리그레션 위험만 증가하므로 현행 유지.
- **레거시 메서드**: CalculateReservedAPForPostMoveAttack, CalculateTurnEndingReservedAP는 CreateAPBudget()이 내부적으로 호출. 외부 노출은 PlanFinalAPUtilization() 1곳뿐 — 독립 제거 불필요.

### 5. StrategicContext — ★ v3.19.2 완성

**v3.19.0에서 해결된 항목**:
- [x] TankPlan: TurnStrategy 컨텍스트 읽기/쓰기
- [x] SupportPlan: TurnStrategy 컨텍스트 읽기/쓰기
- [x] OverseerPlan: TurnStrategy 컨텍스트 읽기/쓰기

**v3.19.2에서 해결된 항목**:
- [x] **FocusTargetId 키 추가**: 전략 평가 시 기준 타겟 UniqueId 저장 → Replan 시 타겟 유효성 검증에 사용 (전 Role)
- [x] **TacticalObjective 키 추가**: 전술적 의도("Kill", "AoE", "Attack") 저장 → 턴 의도 보존

### 6. 위험지역 회피 통합 — ★ v3.19.8 완성

**현상**: ~~DamagingAoE 회피는 대부분 적용되었으나, PsychicNullZone은 대피(Phase 0.5)에서만 체크. AoE 리포지션/SmartTaunt/Aerial Rush 이동은 위험지역 미검증.~~

**v3.19.8에서 해결된 항목**:
- [x] **통합 HazardZone API**: `CombatAPI.IsPositionInHazardZone()` / `IsUnitInHazardZone()` — DamagingAoE + PsychicNullZone(사이커 전용) 단일 메서드, 사이커 여부 유닛별 캐시
- [x] **MovementAPI 전체 전환**: 6개 이동 함수의 `IsPositionInDamagingAoE` → `IsPositionInHazardZone` (FindRanged/FindMelee/FindRetreat/FindApproach 모두)
- [x] **MovementPlanner 전체 전환**: GapCloser 착지, 근접 이동, 접근 이동, 후퇴 대시 — 모두 HazardZone 통합 체크
- [x] **SupportPlan/OverseerPlan 전환**: 힐 이동, 포지셔닝, 후퇴 — HazardZone 통합
- [x] **AttackPlanner AoE 리포지션 누락 수정**: `GetAoERepositionCandidates()`에 HazardZone 필터 추가
- [x] **TankPlan SmartTaunt 누락 수정**: 도발 이동 위치 HazardZone 체크 추가
- [x] **BasePlan Aerial Rush 누락 수정**: 사전 이동 위치 HazardZone 체크 추가

---

## 작업 완료 판정 기준

어떤 기능이든 "완료"라고 말하기 전에:

1. **전 Role 적용 확인**: DPS/Tank/Support/Overseer 4개 모두에 해당되는가?
2. **Replan 경로 확인**: Replan 시에도 정상 작동하는가?
3. **폴백 경로 확인**: 주 경로 + 폴백 경로 모두 처리했는가?
4. **설정 기본값 확인**: 새 기능의 기본값이 합리적인가? (OFF로 두면 사실상 미구현)
5. **실제 동작 시나리오 확인**: 빌드 성공 ≠ 작동. 실전 시나리오에서 트리거되는 조건이 현실적인가?

---

## 최근 완료 항목 (검증됨)

- [x] v3.18.20: PreCombatBuff HP 임계값 체크 (SituationAnalyzer.cs)
- [x] v3.18.22: TurnEnding AP 예약 시스템 (BasePlan + 4개 Plan 모두)
- [x] v3.18.24: GapCloser 착지 위치 Self-AoE 안전성 (MovementPlanner.cs, 폴백 경로 미적용)
- [x] v3.19.0: TurnStrategy 전 Role 완성 (Replan 유지, Role별 시드 필터, 미사용 필드 활성화)
- [x] v3.19.0: 무기 스위칭 정상화 (조건 완화, 전 Role Phase 1.55)
- [x] v3.19.0: APBudget 통합 (구조체 추가, 전 Plan effectiveReservedAP, 버프-TurnEnding 잠식 수정)
- [x] v3.19.2: Replan 타겟 유효성 검증 (FocusTargetId — 전 Role 적용)
- [x] v3.19.2: APBudget 강제 (CanAfford()로 공격 루프 AP 예약 중앙 검증 — 전 Plan)
- [x] v3.19.2: 능력 프로파일 (Situation.HasGapCloser/HasSelfAoE/HasGapCloserCombo — SituationAnalyzer 자동 계산)
- [x] v3.19.2: FocusTargetId + TacticalObjective StrategicContext 키 (전 Role)
- [x] v3.19.2: GapCloser 폴백 경로 Self-AoE 안전성 (MovementPlanner — 폴백도 아군 근접 경고)
- [x] v3.19.4: APBudget 팩토리 통합 (CreateAPBudget + EffectiveReserved + effectiveReservedAP 완전 제거)
- [x] v3.19.6: TurnStrategy Role별 시드 필터 제거 + 가중치 스코어링 (전 Role 10시드 평가, 비-DPS 복합 시드 0.85 가중치)
- [x] v3.19.8: 위험지역 회피 통합 (HazardZone API, PsychicNullZone 이동 회피, AoE리포지션/SmartTaunt/AerialRush 누락 수정)
