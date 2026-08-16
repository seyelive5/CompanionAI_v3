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

### 12. AoE 대피 이중계산 + 재대피 루프 (v3.118.6) — ✅ 인게임 검증 완료 (2026-07-11)

**배경**: 사용자 관찰 + 로그 확정. 대피가 여러 번 위치선택/이동(비효율) + 대피 후 공격 안 하고 턴엔드.

- [x] **이중계산 근본**: 대피 TurnPlan 이 짧은 생성자→InitialAP/MP=0→NeedsReplan 3-2 "AP 증가(0→5)" 오판→같은 대피 반복. 로그: `Replan needed: AP increased (0.0->5.0)`.
- [x] **CreateEarlyPhasePlan 헬퍼**: Ultimate/대피/긴급힐에 situation 스냅샷 전달(3개 초기 Phase 공통 버그 일괄).
- [x] **재대피 루프 근본**: 포화 전장서 대피 이동이 AoE 못 벗어남→`Standing in DAMAGING AoE` 재발→루프→stagnation→턴엔드(Hittable>0 인데 공격 안 함).
- [x] **Phase 0.5 `!HasMovedThisTurn` 가드**: 이미 이동했는데 여전히 AoE면 재대피 대신 공격. 첫 대피 보존.
- [x] **인게임 검증 (2026-07-11, v3.118.11 + F2/F3)**: AoE 위험지대 전투에서 3역할(Heinrix DPS/DOOM Overseer/Abelard Tank) 대피 발동 — 유닛당 대피 **1회**만, `AP increased (0.0->5.0)` **소멸**, 재대피 루프 **없음**(대피 후 공격/버프 전환 = F3 게이트 홀딩), Stagnant #1에서 정지(#2/#3·강제 턴엔드 0건). 단, **대피 자체가 AoE를 못 벗어나는 별개 결함 발견 → §14**.

### 13. v3.118.0-6 코드 리뷰 확정 결함 15건 — 수정 대기 (2026-07-10)

**배경**: 릴리즈 범위 c970229..bdf6a8f 전수 리뷰(파인더 10각도 + 적대적 검증 5 + 갭스윕). **작업 지시서: [docs/reviews/2026-07-10-v3.118-code-review.md](docs/reviews/2026-07-10-v3.118-code-review.md)** — 항목별 앵커/메커니즘/수정 방향 + 반박 4건(재수정 금지) 포함. 재발 방지 가드는 적용 완료(CLAUDE.md "AI 플래닝 코드 함정" + Lesson 20 + code-metrics.sh 정규식 확장).

- [x] **그룹 A — 필터 API 내재화 (v3.118.9, 구현 완료 — 인게임 검증 대기)**: F4(자해 게이트 우회, 키벨라 Blood Oath)·F8(Blade Dance 재수집)·F14(0-AP preference) — 관통 원인 1 해소
  - 공유 헬퍼 `CombatAPI.IsSelfDamageBlockedAtHP(ability, hp%)` 신설(임계값 = DB 등록값 우선, 없으면 SC 기본). SituationAnalyzer:833 게이트를 이 헬퍼로 리팩토링(DRY).
  - F4: FindAnyAttackAbility psyker 패스 + 최종 offensive 폴백에 헬퍼 게이트 적용 → Blood Oath 등 raw 재조회 누수 차단.
  - F14: GetZeroAPAttacks에 자해 게이트(무조건) + `preference` 파라미터(PreferRanged 근접 배제) 내재화. planning(PlanZeroAPAttacks)만 실제 preference 전달, count 호출자(스냅샷/replan)는 기본 Adaptive 유지 → F2/F12 count 일관성 보존.
  - F8: PlanSelfTargetedAoEAttack에 `PreferRanged && GetWeaponAttackType==Melee` 게이트(필터와 동일 기준) → Blade Dance 재수집 누수 차단, 원거리 self-AoE 노바는 유지(blanket 아님).
- [x] **그룹 B — 게이트 의도 플래그 (v3.118.7, 구현 완료 — 인게임 검증 대기)**: F1(**Overwatch/Veil TurnEnding 시전 사멸 회귀 — 최상위**)·F5(AllTargets 스테일 시전 무검증)
  - `PlannedAction.RequiresEnemyOccupancy` 플래그 신설(기본 true = Fix E 유지). `PositionalAttack(..., requiresEnemyOccupancy=false)` 옵트아웃 2곳만: BuffPlanner.cs:1623(TurnEnding Overwatch/Veil)·MovementPlanner.cs:1136(후퇴 대시).
  - F1: TurnPlan 1-3b + ActionExecutor 백스톱(:281) 두 게이트에 `&& RequiresEnemyOccupancy` 추가 → Overwatch 3중 차단(replan 루프+백스톱+ally체크) 우회. 양방향 감사: AttackPlanner:963/1060/1371(공격형 AoE)·auto-convert(:129)·MovementPlanner leap(:322/447)는 default true 유지 → Fix E 무손상.
  - F5: AllTargets 생산자는 AerialRush(MultiTargetAttack) 유일. FindBestAerialRushPath 선택 기준과 동일한 Bresenham `CountEnemiesInChargePath` 로 실행 시점(ActionExecutor)+replan(TurnPlan 1-3c) 재검증 → 스테일 라인 시전 시 실패→replan. 죽은 팩토리 2개(MultiTargetSupport/MoveThenMultiTargetAttack, 호출자 0) 제거.
- [x] **그룹 C — replan 생존 상태 (v3.118.8, 구현 완료 — 인게임 검증 대기)**: F2(대피 zero-AP 루프 — v3.118.6 §12 무효화)·F12(짧은 생성자 9곳)·F3(!HasMovedThisTurn 대피 과잉 억제)·F7(RavenCycle 유실)
  - F2+F12: `CreateEarlyPhasePlan` → `CreatePlanWithSnapshot` 리네임 + zeroAP 하드코딩 `0` → `CombatAPI.GetZeroAPAttacks(situation.Unit).Count`. 짧은 생성자 9곳 전부 스냅샷 라우팅(DPS/Tank/Support/Overseer 무기전환·재활성화 + Movement Ultimate실패/무기전환 + TurnPlanner error 폴백 인라인). 잔여 `new TurnPlan(...스냅샷...)`은 풀플랜 엔드포인트 5곳뿐.
  - F3: `PlannedAction.IsEvacuationMove` + `TurnState.HasEvacuatedThisTurn`(RecordAction Move case) + `Situation.HasEvacuatedThisTurn`(SituationAnalyzer 미러). 게이트 `!HasMovedThisTurn` → `!HasEvacuatedThisTurn` → 공격 이동 후 위험지대 재대피 차단 해소.
  - F7: `TurnState.HasExecutedAbilityMatching(predicate)` 신설(HasUsedAbility 옆). `usedWarpRelay` 초기값을 플랜-로컬 false → `Raven && turnState.HasExecutedAbilityMatching(IsWarpRelayTarget)`(Movement ExecuteFamiliarSupportPhase에 turnState 파라미터 추가 + 3콜러 갱신, OverseerPlan:101). OverseerPlan:370 inert 분기(`isRavenBuffPhase && Raven`) 제거 → `if (usedWarpRelay)`.
- [x] **그룹 D — 독립 소형 (v3.118.10, F6/F10/F11/F13/F15 구현 완료 — 인게임 검증 대기 · F9는 설계 결정 대기)**
  - F6: `CombatAPI.GetRangePreference(unit)` 헬퍼(ModSettings 소스, 분석기와 동일) 신설, 하드코딩 `FindAnyAttackAbility(unit, PreferRanged)` 7곳(4파일: AbilityChecks·WeaponSystem·MovementAPI·BestPositionFinding)을 실제 preference 전달로 교체 → 근접 유닛 포지셔닝 hittable=0 붕괴 해소.
  - F10: BuffPlanner 마진 페널티(:628) + BasePlan.Common Phase 9 차단(:71)을 `info.Timing==SelfDamage || CombatAPI.IsSelfDamagingAbility(ability)`로 확장 + 임계값 폴백(등록값>0 ? : SC 기본). 컴포넌트 감지 미등록 wounds 버프도 보호.
  - F11: SelectBestAttack 필터에 `!(PreferRanged && IsGapCloser)` 추가 → 원거리 옵션 0 시 갭클로저 단독 돌격 차단(후퇴 대시는 MovementPlanner 직접 소비라 무영향).
  - F13: Extermination Mark New GUID `542f7f3cab6a41a6859da3ba9c984168` 동일 Marker/EnemyTarget 등록.
  - F15: GetRelayRedirectTargetType의 silent catch(Warn+ex.Message) → `Log.Engine.Error(ex,…)` (무음 실패 해소, 진단 오염 방지).
  - 컷라인: AbilityDatabase.cs 날짜 스탬프 ★ 마커 2곳(:198/:897) → 평문 주석(why 보존, 마커 제거).
  - [x] **F9 (v3.118.11, 사용자 선택 A=warn-only 확정 → 구현 완료)**: SituationAnalyzer 필터 직후 `PreferRanged && !HasRangedWeapon && AvailableAttacks==0`일 때 유닛당 1회 Warn(`_f9WarnedUnits` static). v3.118.2 하드 제약 유지(auto-ignore 아님) — 오설정을 가시화만. 원거리 사이킥 보유 유닛은 필터 후 공격이 남아 미발화.
- ⚠️ **§9/§12 인게임 검증 교차**: §12 검증은 F2/F3 수정 전 **실패 예상** — 로그 해석 시 혼동 금지. §9 근접누수 확인 시 F8(Blade Dance)/F14(0-AP Kick)가 예외로 관찰될 수 있음. F1은 수정 전 `point AoE Overwatch|Veil` grep으로 실증 데이터 확보 가능.
- [x] **로그 사전 점검 (2026-07-10)**: F1 흔적 없음(보유자 미출전 — 판정은 코드 추적 유지) / §12 버그 현장 8회+Stagnant 3유닛 실증(v3.118.6 이전 바이너리) / F2 감시 라인 = 대피 턴 `New 0 AP attack`(Heinrix Slash 보유 확인) / Blood Oath 차단 주체는 이 로그 기준 DOOM(§9 "키벨라" 귀속 재확인 필요). 상세: 리뷰 문서 말미.
- [x] **인게임 검증 1차 (2026-07-11, v3.118.11 두 세션)**: **F9 확정**(Cassia PreferRanged+원거리무기無 → 정확히 1회 Warn, 스팸 없음) / **F2·F3 확정**(§12 참조) / F4 과잉차단 없음(키벨라 HP=100% Blood Oath 정상 시전; hp70 미만 케이스 미발생) / 크래시·예외·replan 루프 0건. **미발동**: F1(Overwatch/Veil 미시전), F5(AerialRush 미시전), F8/F14(해당 유닛 턴 미관찰) — 후속 세션 필요.

### 14. 대피 목적지 도달 불가 — 게임 실행기 트림/턴 사멸 (v3.118.12) — ⚠️ 인게임 검증 대기

**배경 (2026-07-11 AoE 전투 로그)**: §12 재검증 중 발견. 3유닛 전부 대피 목적지가 게임 패스파인더에서 `unreachable, trim path` — Heinrix 한 칸 못미침(여전히 AoE 안), DOOM (66,83)→(66,82) 트림, **Abelard는 완전 실패 → `Nothing to do, finish turn` → AP 5.0 전량 미사용 턴 사멸**.

**근본 원인 (디컴파일 확정)**: 게임 실행기 `TaskNodeSetupMoveCommand`가 endpoint부터 역방향 `CanStopAtNode` = **`UnitMoveVariants.cells[node].IsCanStand`** 검사(SetupMoveCommandHelper.cs:318) + `RuleCalculateMovementCost.Calculate`가 위협지역/override 코스트로 재계산해 추가 절단. 정지가능 prefix < 2포인트면 Failure → 우리 트리가 TaskNodeTryFinishTurn 폴백 = 턴 사멸. 우리 쪽은 **PlanAoEEvacuation과 SetupMovement만 IsCanStand 미검사**(코드베이스 나머지 12곳+는 관례 준수) + 유클리드 거리 사용 + 실패 시 `IsFinishedTurn=true`.

- [x] **Fix A (탐색)**: PlanAoEEvacuation에 `!IsCanStand → continue` + 거리 기준 유클리드 → `cell.Length`(실경로 MP).
- [x] **Fix B (핸드오프)**: CustomBehaviourTree.SetupMovement 4분기(정확일치/A*역탐색/최근접/MP트림) 전부 standability 준수 + 트림 후 부모 체인 standable-walk → 게임이 거부할 endpoint를 아예 안 넘김.
- [x] **Fix C (턴 사멸 복구)**: `TurnOrchestrator.NotifyMoveSetupFailed` 신설 — RecordAction(success=true) 선기록 회수(WasSuccessful/HasEvacuatedThisTurn/HasMovedThisTurn/MoveCount 롤백) + 플랜 취소 → decision node가 턴 종료 대신 Running 반환 → 다음 틱 재계획. ConsecutiveFailures(≤3)+FallbackReplan(≤2) 가드로 바운드. 트리 개조는 배제(게임 Loop가 자식 Failure 시 같은 프레임 동기 재반복 → 프리즈 위험).
- [x] **인게임 검증 1차 (2026-07-11, v3.118.12)**: ① `unreachable, trim path` **0건**(이전 3건) ✅ ③ 턴 사멸 **소멸**(Abelard 정상 대피+후속 플랜, Move setup failed 미발생) ✅ — 그러나 ② **미달**: 3유닛 전부 요청 노드가 게임 UnitMoveVariants에서 거부돼 Fix B 폴백이 1칸 못미친 노드 선택(Heinrix (18.23,31.73)→(16.87,31.73) 등), 여전히 AoE 안 착지. 원인 = **우리 MovementAPI dict(타임아웃 시 player 폴백) vs 게임 AI 변형의 도달성 발산** (서로 다른 3개 타일 전부 거부 = 체계적).
- [x] **Fix D (v3.118.13, 발산 구조 제거)**: 플랜은 BT 틱 안에서 실행되고 게임의 `UnitMoveVariants`가 DecisionContext에 이미 존재(AsyncTaskNodeCreateMoveVariants가 decision node 직전 실행) → `MovementAPI.Set/Get/ClearTickMoveVariants`로 decision node가 ProcessTurn 전후 stash/clear, PlanAoEEvacuation이 **게임 딕셔너리 우선** 사용(없을 때만 자체 계산 폴백). 선택 노드가 게임 변형에 by-construction 존재 → SetupMovement 정확일치 → 전체 경로 실행. 게임 변형에 AoE 밖 정지가능 타일이 없으면 대피 생략(부분 이동으로 MP 낭비 대신 제자리 전투 — 올바른 정책).
- [x] **인게임 검증 2차 (2026-07-11, v3.118.13) — ✅ 전 항목 통과**: ② DOOM 요청 (10.13,39.83)→BestCell (62,84) **정확 일치**·Abelard (19.58,35.78)→(69,81) **정확 일치**, 트림 0, **대피 직후 `Standing in DAMAGING AoE` 미재발 = AoE 탈출 확인**. Abelard는 탈출 후 SmartTaunt 이동+Taunting Scream 3적 도발까지. Heinrix는 3회 검색 전부 `No safe tile found`(게임 메트릭상 탈출 불가) → **제자리 풀 턴**(버프 4+공격 4+이동 2, "No AP remaining" 정상 종료) — 의도한 정책 그대로. 관찰: 같은 턴 내 검색 실패→성공 전이(DOOM/Abelard 첫 시도 실패, 수 초 후 성공) = 틱별 게임 상태(존 만료/MP) 변화의 정직한 반영, 무해.

### 15. 포인트 AoE 착탄 밀림 — 플래너 사전검증 부재 (v3.118.14 수정, 인게임 검증 대기)

**배경 (v3.118.13 검증 로그)**: DOOM이 MP=0/AP=4/Hittable=0 상태에서 Phase 9 "Final debuff Enfeeble → Cutthroat Rebel"이 point (27.7,39.8)를 **3연속 동일하게 선택**, 매번 점유 게이트가 차단 → Fallback replan #1/#2 → Stagnant #3 강제 턴엔드(AP=4 미사용).

**조사 결과 (2026-07-11, 디컴파일+블루프린트 확정) — 게이트가 옳았음**:
- Enfeeble 블루프린트(`Biomancy_Enfeeble_Ability.jbp`): 포인트 AoE 확정 — `AbilityTargetsInPattern` Custom 패턴 반경 3, `IgnoreLos=false`, `IgnoreLevelDifference=false`, CustomRange 10. 우리 DB 등록(PointTarget) 정확.
- **착탄 메커니즘**: 게임 `AoEPatternHelper.GetActualCastNode`가 시전자→타겟 **그리드 라인캐스트**로 착탄점을 정함 — 장애물/절벽에서 멈추면 패턴 중심이 타겟 앞으로 밀림. DOOM(하층 y=-16.1)→적(상층 y=-13.5, 2.6m 절벽): 라인캐스트가 절벽에서 멈춤 → 패턴이 하층에 깔림 + `IgnoreLevelDifference=false`로 상층 배제 → **적 미포함(비어있지 않은 패턴) → count 0 → 게이트 정당 차단**. 실제 시전했어도 같은 계산으로 헛방.
- **결함 = 플래너 사전검증 부재**: `CanUseAbilityOn(유닛)`은 시야 LOS 기준이라 통과 → 안 닿는 타겟을 3연속 재선택. 공격 경로는 Analyzer hittable 계산의 `IsAoEHeightInRange`(:595)가 이미 사전 필터하지만 **디버프/마커 경로는 착탄 검증 전무**. BuffPlanner.PlanDebuff 경로는 Type=Debuff라 1-3b 게이트도 안 타서 **조용한 헛방**(관찰된 Attack-type보다 나쁜 변형).

- [x] **수정 (v3.118.14)**: `CombatAPI.WillPointCastReachTarget(ability, caster, target)` 신설 — 게이트(TryCountUnitsInPattern)와 **동일 계산**으로 "포인트 변환 시전이 타겟을 실제로 맞추는지" 사전 검증, 비포인트/계산실패는 fail-open(게이트와 동일 시맨틱, 과잉차단 방지). 적용 3곳: BasePlan.Common Phase 9 Final debuff(실증 사이트) + BuffPlanner.PlanDebuff(조용한 헛방 변형) + PlanMarker(보험). 기존 site 관례(IsAttackSafeForTarget도 현재 위치 기준)와 동일하게 현재 위치 기준.
- [ ] **인게임 검증**: 다층 맵에서 psyker 디버프 보유 유닛(DOOM Enfeeble) 전투 — `Final debuff SKIPPED (target not in pattern)` 로그 확인 + Enfeeble 3연속 차단→Stagnant 턴엔드 패턴 소멸(대신 다른 행동 또는 정상 턴엔드).

### 16. v3.118.7~14 코드 리뷰 — R1 이동 설정 실패 무한 재계획(프리즈) 회귀 + 위생 8건 (v3.118.15) — ⚠️ 인게임 검증 대기

**배경**: 릴리즈 범위 `bdf6a8f..fa3d1e6` 리뷰. 다중 에이전트 워크플로우가 세션 한도로 규약 각도 1개만 완료 → 나머지 6각도는 단독 정독. **리뷰 문서: [docs/reviews/2026-07-11-v3.118.7-14-code-review.md](docs/reviews/2026-07-11-v3.118.7-14-code-review.md)** (CONFIRMED 1 + 위생 8 + 안전 확인 + 관찰).

- [x] **R1 (medium, v3.118.12 Fix C 회귀)**: `NotifyMoveSetupFailed` 의 `ConsecutiveFailures++` 는 MoveTo 선기록 `RecordAction(success=true)` 가 매 사이클 0 리셋 → 0↔1 진동, 상한 불가. StagnantPlan 은 이미 공격한 턴엔 안 셈 → 포위 상태 재배치 Move 반복 시 **AI 타임아웃 300s 까지 프리즈** 가능(인게임 미발생 — 대피는 게임 dict 로 성공, 잠재 회귀). 수정: `TurnState.MoveSetupFailCount` 전용 카운터 — 1회 재계획(이동 허용) / 2회 `MovementBlockedThisTurn` → Analyzer `CanMove=false` 미러 + ExecuteNextAction 잔여 Move 스킵 → **제자리 재계획** / 3회 턴 종료(하드 상한). `GameConstants.MOVE_SETUP_FAILURES_BLOCK_MOVEMENT=2 / MAX_MOVE_SETUP_FAILURES=3`.
- [x] **위생 H1~H9**: catch 3곳 `Error(ex,…)` / 4 Role 플랜 종결부 `CreatePlanWithSnapshot` 통합(−16줄, ★12) / 차지라인 재검증 `PointTargetingHelper.ChargeLineHitsAnyEnemy` 헬퍼(1-3c+F5 통합) / `HasUsedWarpRelayThisTurn` 헬퍼 / 자해 차단 로그 임계값 복원(`GetSelfDamageHPThreshold`) / XML doc(ClearTickMoveVariants·GetZeroAPAttacks preference 계약) / 날짜 스탬프 3·★ 2 제거 / F9 문구 정정 / deep-if 4209→4202.
- [ ] **인게임 검증**: 이동 설정 실패 로그 관찰 시 — `Move setup failed — cancelling plan for replan (setup failures=1/2 …)` → 재실패면 `movement blocked for the rest of this turn` 후 제자리 플랜(공격/버프)으로 AP 소진 → 프리즈 없음. `Skipping Move — movement blocked` 가 뜨면 플래너가 CanMove=false 를 우회한 경로가 있다는 뜻(어느 플래너인지 앞 로그로 식별).
- 관찰(후속 후보, 결함 아님): 후퇴 대시 착지 AoE 피해의 아군 안전 미검사(v3.118.0 이전부터, IsAoESafe 가 피해 미구분이라 현재 구조론 과잉차단) / 자체 dict 즉시 이동 경로의 게임 도달성 발산 잔존(R1 로 바운드) / code-metrics.sh `Error+ex.Message` 변종 31곳 미집계.
