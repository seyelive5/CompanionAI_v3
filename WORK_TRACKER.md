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
6. **런타임 로그 증거 확인** (★ v3.111.19 Phase D.4 추가): 기대 동작이 `GameLogFull.txt`에 증거로 찍히는가?
   - 예: `"[Analyzer] Extra turn CONFIRMED"`, `"Hide=33.6(F0.93/A0.93)"`, `"StayAway=0.70(17.6)"`
   - **빌드 클린 ≠ 실행 증명**. v3.111.0 Phase 5는 빌드 클린이었지만 `task.Wait` 데드락으로 0% 효과 — "완료 선언" 후 배포한 뒤 발견.
   - 기능 검증은 항상 로그 관찰까지 포함. 로그에 증거 없으면 완료 아님.
7. **메트릭 회귀 없음** (Phase 6 이후): `bash scripts/code-metrics.sh` 결과가 `docs/metrics/baseline.md` 가 가리키는 활성 베이스라인 대비 모든 항목 동등 또는 개선. 의도적 악화 (예: 신규 모듈 추가로 LOC 증가)는 commit 메시지에 명시.

### 7. 코드 감사 기술 부채 정리 — ★ v3.22.0 완성

**배경**: 전체 코드베이스 감사 결과 A등급 1건(전략 중복) + B등급 5건 발견

**v3.22.0에서 해결된 항목**:
- [x] **전략 검증 중복 제거**: 4개 Plan의 ~200줄 중복 → `BasePlan.EvaluateOrReuseStrategy()` + `ValidateFocusTarget()` 추출
- [x] **TacticalObjective 누락 수정**: DPSPlan에만 있던 TacticalObjective 설정을 전 Role 통합
- [x] **FocusTarget 로그 ID 누락 수정**: Tank/Support/Overseer에서 focusTargetId 미출력 → 통합 메서드에서 전 Role 출력
- [x] **폴백 상수 SC.cs 중앙화**: CombatAPI/MovementAPI/MainAIPatch/TacticalOptionEvaluator의 하드코딩 `15f` → `SC.FallbackWeaponRange`/`SC.FallbackEstimateDamage`
- [x] **catch 블록 디버그 로깅**: CustomBehaviourTree(2), TurnOrchestrator(2), AoESafetyChecker(1) — 5곳에 `Main.LogDebug()` 추가
- [x] **TurnState Obsolete 필드 제거**: `RemainingAP`/`RemainingMP` 완전 제거 (v3.0.77 이후 미사용)
- [x] **CombatAPI.cs.bak 삭제**: 129KB 백업 파일 정리
- [x] **BasePlan 매직 넘버 SC.cs 이관**: HP_COST_THRESHOLD, DEFAULT_*_ATTACK_COST, MAX_ATTACKS_PER_PLAN, MAX_POSITIONAL_BUFFS

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
- [x] v3.22.0: 코드 감사 기술 부채 정리 (전략 중복 제거, TacticalObjective 누락 수정, 폴백 상수 중앙화, catch 로깅, Obsolete 제거)
- [x] v3.22.2: AI 로직 감사 (33건 검증 → 25건+ False Positive 확인, SupportPlan AoE 힐 임계값 70f→healThreshold 수정)
- [x] v3.22.4: Turn Order Awareness 확장 (PositionEvaluator 턴 순서 기반 위협 가중, BasePlan.PlanAllyBuff 행동 예정 아군 버프 우선)
- [x] v3.22.6: 마스티프 사역마 Apprehend/Protect 개선 (TeamBlackboard 상태 추적 → 대상 고정/재발행 방지, BestTarget 연동 → 연대공격 극대화, 도달 가능성 체크, Protect 조건 강화 → 근접 적 위협+HP<50%만, OverseerPlan Phase 3.7 재구성 → Apprehend 활성시 전부 스킵으로 AP 절약, Protect Phase 9.5 이동)
- [x] v3.24.0: 전투 규칙 기반 스코어링 개선 Tier 1 (EV 스코어링: hitChance×damage 확률적 기대값 도입 → 이산적 hit threshold 대체, 극저 데미지 감지: EstimateDamage<5 타겟/공격 페널티 → 방어구 관통 불가 감지, Overwatch 포지셔닝: TacticalOptionEvaluator 이동 페널티 + PositionEvaluator 구역 회피, 사거리 품질: PositionEvaluator 이진 LOS → 최적사거리 연속 스코어링 + ExpectedDamageRatio 커브)

### 8. 스킬 사용 로직 체계적 개선 — ★ v3.40.0 완성

**배경**: 스킬 사용 감사 결과 6가지 구조적 문제 발견 ([SKILL_USAGE_AUDIT.md](SKILL_USAGE_AUDIT.md) 참조)

**v3.34.0에서 해결된 항목**:
- [x] **BuffPlanner 스마트화**: ScoreAttackBuff() 점수 시스템 — 0 AP +100, Wildfire AP 부족 +80, KillSimulator 데미지 배율, CC 보너스
- [x] **PostFirstAction 일반화**: PlanPostAction()이 RunAndGun 외 DaringBreach, BringItDown, HitAndRun 등 전체 처리
- [x] **OverseerPlan 마스터 버프**: Phase 4.955에 PlanAttackBuffWithReservation() 삽입
- [x] **이동 전 MP 버프**: Situation.MPBuffAbility + TacticalOptionEvaluator 확장 MP + BasePlan.PlanMPBuffBeforeMove() + 4개 Plan Phase 7.8/8.8

**v3.36.0에서 해결된 항목**:
- [x] **AbilityDatabase 누락 스킬 22개 등록**: Executioner(2), Bounty Hunter(5), Biomancer(1), Pyromancer(2), Telepathy(3), Soldier(1), Navigator(3), Overseer(7)

**v3.38.0에서 해결된 항목**:
- [x] **AutoDetectTiming MP 회복 감지**: Phase 2.5 — 미등록 MP 회복 능력 PostFirstAction 자동 분류
- [x] **0 AP 버프 일괄 사용**: PlanFreeAttackBuffs() — 모든 0 AP PreAttackBuff 전부 계획 (전 Plan Phase 4.05/3.05/4.955b/4.75)
- [x] **0 AP 공격 소진**: PlanZeroAPAttacks() — AP 예산 무관 무료 공격 계획 (전 Plan Phase 5.8/6.5, 최대 3개)

**v3.40.0에서 해결된 항목**:
- [x] **Piercing Shot Prey 인식**: CombatAPI.IsMarkedAsPrey() + ScoreAttackBuff Prey 대상 +60점 (HuntDownThePrey/ChoosePrey_Noble 지원)
- [x] **Cautious/Confident Approach 자동 전환**: PlanApproachStance() — HP/위협/역할 기반 스탠스 선택 (전 Plan Phase 1.8, DPS/Overseer=Confident, Tank/Support=Cautious)
- [x] **Voice of Command 등록 확인**: 기존 등록 (`9c78e44bf8ff44a9afff8370c673c9ad`, PreCombatBuff, AllyTarget)
- [ ] **공격제한 미포함 공격 구분**: 보류 — 게임 API에 명시적 구분 없음, 0 AP는 PlanZeroAPAttacks()에서 이미 처리

### 9. 게시판 피드백 Fix-α (v3.118.0-2) — 구현 완료, ⚠️ 인게임 검증 대기

**배경**: 유저 피드백 4건 트리아지(2026-07-07) 중 확정 갭 3건 수정. 상세 진단은 세션 메모리 `nexus_feedback_triage_2026_07_07.md`.

**v3.118.0 — 빈자리 AoE (포인트 타겟 점유 재검증)**:
- [x] TurnPlan 1-3b: 포인트-공격 패턴 내 의식 있는 적 0 → 리플랜
- [x] ActionExecutor 백스톱: 점유 + 포인트 타겟 아군안전(IsAoESafe) 재검증
- [x] CombatAPI.TryCountUnitsInPattern (패턴 계산 실패 fail-open 신호)
- [x] **✅ 인게임 검증 완료 (2026-07-07 로그)**: `Replan needed: point AoE Machine Spirit Communion ... no longer hits any enemy` + `Plasma Overcharge ...` 2건 실포착. 예외 0.

**v3.118.1→.3 — 자해 능력 HP 게이트 (블루프린트 덤프 실측으로 전제 교정)**:
- [x] ~~v3.118.1: DealDamage 스캔~~ → **死코드였음** (게임 자해는 DealDamage 안 씀)
- [x] v3.118.3: CombatAPI.IsSelfDamagingAbility 를 실제 메커니즘으로 재작성 — `AbilityResourceWounds`(HP 코스트, 주) + self-scoped DealDamage/ApplyDOT(보조), HealInsteadOfDamageFact 회복 변환 제외
- [x] **블루프린트 실측 확정**: 키벨라 자해 3종(BloodOath `590c990c`/VeilOfBlades=BladeShroud `8b7bcaa0`/OathOfVengeance `3774147440`) 전부 AbilityResourceWounds. Bloodletting=Ensanguinate=ApplyDOT
- [x] **⚠️ 근본 수정 아님**: 이 3종은 v3.9.64(2026-02-17)부터 hpThreshold 70f 등록 + Marker/PreCombatBuff/TurnEnding 분기가 이미 HP<70% 차단 중 → v3.118.3은 死코드 교정 + 메커니즘 기반 미등록 방어(defense-in-depth)
- [x] **✅ 인게임 검증 완료 (2026-07-07 로그)**: `[Analyzer] Blocked self-damage Blood Oath: HP 65% < threshold 70%` 12건 — 키벨라 Blood Oath(AbilityResourceWounds) 70% 게이트 작동, 자살 스팸 불가 확정. 예외 0.
- [ ] **제보 원인 재확인 (미해결)**: 최신 버전에서 자해 게이트됨 확인 → 키벨라 사망은 근접 노출(Blade Dance/Spring Attack) 쪽. **제보자 버전 + GameLogFull 필요**

**v3.118.2 — PreferRanged 근접 누수 3경로 차단**:
- [x] FilterAbilitiesByRangePreference 폴백 제거 (하드 제약)
- [x] PlanMeleeAoEAttack 조기 반환 + PlanPostMoveAttack/FindAnyAttackAbility 근접 폴백 배제
- [ ] **인게임 로그 증거**: `RangeFilter: PreferRanged - no ranged available this turn` 발생 턴에 근접 공격 없음 (버프/재배치로 대체되는지 확인)

### 10. 블루프린트 덤프 감사 교정 (v3.118.4-5) — ⚠️ 인게임 검증 대기

**배경**: 블루프린트 원본 덤프 3중 감사(펫/Warp Relay/분류). 상세 `blueprint_audit_findings_2026_07_07.md`.

**v3.118.4 — 능력 분류 3건 교정**:
- [x] Assassin Extermination Mark(`0fab919b`) 미등록 → Marker/EnemyTarget (0뎀 공격 오분류 해소)
- [x] Ensanguinate(`858e84`) SelfDamage/hp60 → PreAttackBuff/hp40 (HP코스트 오인 교정, +3MP 템포)
- [x] RavenCycle(`78e54abc`) PreCombatBuff → FamiliarOnly (제네릭 버프 경쟁 제거)
- [x] **검증 후 제외**: Stabilize(SelfTargetOnly 실은 정확 — 파티효과=릴레이), Growl(저가치)
- [x] **✅ Ensanguinate 검증 (2026-07-07 로그)**: 키벨라 자기시전 1회 — 이전 60%↓ 사장 해소, 자살 아님. 예외 0.
- [ ] **미검증(시나리오 미발생)**: Extermination Mark(Assassin 미출전) / Cycle(Raven Cycle 미발생) — 회귀 없음

**v3.118.5 — Warp Relay 진단 RedirectTargetType 정밀화**:
- [x] CombatAPI.GetRelayRedirectTargetType/IsRelayRedirectEnemyOnly/GetRelayRedirectTypeName
- [x] FamiliarSupport: Enemy-리다이렉트 오탐 Warn 소거 + Any/Ally 로그에 redirect 종류 기록. 하드 veto 미도입(증거 우선)
- [ ] **미검증(시나리오 미발생)**: 2026-07-07 로그에 Warp Relay Psychic Attack 0건(Overseer+Raven+Momentum+피해사이킥 조합 미발생) — 회귀 없음, 다음 기회에 관찰

### 11. Fix γ 대기 — 로그로 확정된 미해결 (2026-07-07)
- **파스칼 DPS 공격불가 (확정)**: `[Analyzer] Pasqal abilities: Debuffs=6, Attacks=0` → Hittable=0 → "DPS no targets" 턴종료 6회 (전투 통틀어 공격19/디버프51/no-target종료6). 공격 kit 이 전부 Debuff 분류 → 제보 "DPS인데 디버프만" 실증. **다음 작업 최우선 후보**.
- **카시아 (부분)**: Support/Hittable=0(Lidless Stare 아군차단) + Frontline 키스톤 시전. 단 HP 100% 유지 → 이번 세션엔 "bullet sponge" 미재현. 포지셔닝 Phase 2 territory.

### 12. AoE 대피 이중계산 + 재대피 루프 (v3.118.6) — ⚠️ 인게임 검증 대기

**배경**: 사용자 관찰 + 로그 확정. 대피가 여러 번 위치선택/이동(비효율) + 대피 후 공격 안 하고 턴엔드.

- [x] **이중계산 근본**: 대피 TurnPlan 이 짧은 생성자→InitialAP/MP=0→NeedsReplan 3-2 "AP 증가(0→5)" 오판→같은 대피 반복. 로그: `Replan needed: AP increased (0.0->5.0)`.
- [x] **CreateEarlyPhasePlan 헬퍼**: Ultimate/대피/긴급힐에 situation 스냅샷 전달(3개 초기 Phase 공통 버그 일괄).
- [x] **재대피 루프 근본**: 포화 전장서 대피 이동이 AoE 못 벗어남→`Standing in DAMAGING AoE` 재발→루프→stagnation→턴엔드(Hittable>0 인데 공격 안 함).
- [x] **Phase 0.5 `!HasMovedThisTurn` 가드**: 이미 이동했는데 여전히 AoE면 재대피 대신 공격. 첫 대피 보존.
- [ ] **인게임 검증**: `Replan needed: AP increased (0.0->5.0)` 소멸 + 대피 후 공격 실행(Hittable>0 시) + stagnation 턴엔드 감소

### 13. v3.118.0-6 코드 리뷰 확정 결함 15건 — 수정 대기 (2026-07-10)

**배경**: 릴리즈 범위 c970229..bdf6a8f 전수 리뷰(파인더 10각도 + 적대적 검증 5 + 갭스윕). **작업 지시서: [docs/reviews/2026-07-10-v3.118-code-review.md](docs/reviews/2026-07-10-v3.118-code-review.md)** — 항목별 앵커/메커니즘/수정 방향 + 반박 4건(재수정 금지) 포함. 재발 방지 가드는 적용 완료(CLAUDE.md "AI 플래닝 코드 함정" + Lesson 20 + code-metrics.sh 정규식 확장).

- [ ] **그룹 A — 필터 API 내재화**: F4(자해 게이트 우회, 키벨라 Blood Oath)·F8(Blade Dance 재수집)·F14(0-AP preference) — FindAnyAttackAbility/GetZeroAPAttacks 내부에 게이트 내장
- [x] **그룹 B — 게이트 의도 플래그 (v3.118.7, 구현 완료 — 인게임 검증 대기)**: F1(**Overwatch/Veil TurnEnding 시전 사멸 회귀 — 최상위**)·F5(AllTargets 스테일 시전 무검증)
  - `PlannedAction.RequiresEnemyOccupancy` 플래그 신설(기본 true = Fix E 유지). `PositionalAttack(..., requiresEnemyOccupancy=false)` 옵트아웃 2곳만: BuffPlanner.cs:1623(TurnEnding Overwatch/Veil)·MovementPlanner.cs:1136(후퇴 대시).
  - F1: TurnPlan 1-3b + ActionExecutor 백스톱(:281) 두 게이트에 `&& RequiresEnemyOccupancy` 추가 → Overwatch 3중 차단(replan 루프+백스톱+ally체크) 우회. 양방향 감사: AttackPlanner:963/1060/1371(공격형 AoE)·auto-convert(:129)·MovementPlanner leap(:322/447)는 default true 유지 → Fix E 무손상.
  - F5: AllTargets 생산자는 AerialRush(MultiTargetAttack) 유일. FindBestAerialRushPath 선택 기준과 동일한 Bresenham `CountEnemiesInChargePath` 로 실행 시점(ActionExecutor)+replan(TurnPlan 1-3c) 재검증 → 스테일 라인 시전 시 실패→replan. 죽은 팩토리 2개(MultiTargetSupport/MoveThenMultiTargetAttack, 호출자 0) 제거.
- [ ] **그룹 C — replan 생존 상태**: F2(대피 zero-AP 루프 — v3.118.6 §12 무효화)·F12(짧은 생성자 9곳)·F3(!HasMovedThisTurn 대피 과잉 억제)·F7(RavenCycle 유실)
- [ ] **그룹 D — 독립 소형**: F6(하드코딩 PreferRanged 4곳 → 근접 유닛 이동 회귀)·F10(자해 2차 보호 등록-키)·F11(갭클로저 단독후보)·F13(Extermination Mark New GUID — PLAUSIBLE, 검증 우선)·F15(catch 교정)·F9(근접전용+PreferRanged 무경고 — **설계 결정 필요**)
- ⚠️ **§9/§12 인게임 검증 교차**: §12 검증은 F2/F3 수정 전 **실패 예상** — 로그 해석 시 혼동 금지. §9 근접누수 확인 시 F8(Blade Dance)/F14(0-AP Kick)가 예외로 관찰될 수 있음. F1은 수정 전 `point AoE Overwatch|Veil` grep으로 실증 데이터 확보 가능.
- [x] **로그 사전 점검 (2026-07-10)**: F1 흔적 없음(보유자 미출전 — 판정은 코드 추적 유지) / §12 버그 현장 8회+Stagnant 3유닛 실증(v3.118.6 이전 바이너리) / F2 감시 라인 = 대피 턴 `New 0 AP attack`(Heinrix Slash 보유 확인) / Blood Oath 차단 주체는 이 로그 기준 DOOM(§9 "키벨라" 귀속 재확인 필요). 상세: 리뷰 문서 말미.
