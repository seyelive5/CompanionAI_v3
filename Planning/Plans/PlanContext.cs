using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning.Plans
{
    /// <summary>
    /// ★ Phase 0.1: Plan 실행 중 공유되는 상태.
    /// 기존 각 Plan의 CreatePlan() 로컬 변수들을 통합.
    /// Template Method 패턴에서 Phase 메서드 간 상태 전달에 사용.
    /// </summary>
    public class PlanContext
    {
        // ══════════════════════════════════════════════════════════════
        // 입력 (변경 안 함)
        // ══════════════════════════════════════════════════════════════
        public Situation Situation;
        public TurnState TurnState;
        public BaseUnitEntity Unit;
        public string RoleName;       // "DPS", "Tank", "Support", "Overseer"
        public AIRole Role;

        // ══════════════════════════════════════════════════════════════
        // AP/MP 추적
        // ══════════════════════════════════════════════════════════════
        public float RemainingAP;
        public float RemainingMP;
        public APBudget Budget;

        // ══════════════════════════════════════════════════════════════
        // 전략
        // ══════════════════════════════════════════════════════════════
        public TurnStrategy Strategy;

        // ══════════════════════════════════════════════════════════════
        // 행동 수집
        // ══════════════════════════════════════════════════════════════
        public List<PlannedAction> Actions;

        // ══════════════════════════════════════════════════════════════
        // 공통 Phase 간 공유 플래그 (ALL Plans)
        // ══════════════════════════════════════════════════════════════

        /// <summary>공격이 계획되었는가 (Phase 4~5에서 설정, Phase 6~9에서 참조)</summary>
        public bool DidPlanAttack;

        /// <summary>이동이 계획에 포함되었는가 (Phase 8~9에서 중복 이동 방지)</summary>
        public bool HasMoveInPlan;

        /// <summary>계획된 공격 수 (MAX_ATTACKS_PER_PLAN 제한용)</summary>
        public int AttacksPlanned;

        // ══════════════════════════════════════════════════════════════
        // 전술 평가 결과 (ALL Plans)
        // ══════════════════════════════════════════════════════════════

        /// <summary>TacticalOptionEvaluator 결과</summary>
        public TacticalEvaluation TacticalEval;

        /// <summary>공격 실패 이유 추적 (이동 Phase에 전달)</summary>
        public AttackPhaseContext AttackContext;

        // ══════════════════════════════════════════════════════════════
        // 타겟/능력 제외 목록 (Phase 5 공격 루프)
        // ══════════════════════════════════════════════════════════════

        /// <summary>이미 공격 계획된 타겟 ID (분산 공격)</summary>
        public HashSet<string> PlannedTargetIds;

        /// <summary>이미 사용 계획된 능력 GUID (이중 계획 방지)</summary>
        public HashSet<string> PlannedAbilityGuids;

        // ══════════════════════════════════════════════════════════════
        // Familiar/Keystone 관련 (DPS, Tank, Support, Overseer)
        // ══════════════════════════════════════════════════════════════

        /// <summary>사역마 키스톤 능력 사용 GUID 추적 (아군 버프 중복 방지)</summary>
        public HashSet<string> UsedKeystoneAbilityGuids;

        /// <summary>WarpRelay 사용 여부</summary>
        public bool UsedWarpRelay;

        // ══════════════════════════════════════════════════════════════
        // 위치 버프 추적 (ALL Plans)
        // ══════════════════════════════════════════════════════════════

        /// <summary>사용된 위치 버프 GUID (중복 방지)</summary>
        public HashSet<string> UsedPositionalBuffs;

        // ══════════════════════════════════════════════════════════════
        // DPS 전용
        // ══════════════════════════════════════════════════════════════

        /// <summary>콤보 전제 조건이 이전 plan에서 이미 적용됨 (replan 시)</summary>
        public bool ComboAlreadyApplied;

        /// <summary>콤보 타겟 ID (replan 시 컨텍스트에서 복원)</summary>
        public string ComboTargetId;

        /// <summary>이전 plan에서 후퇴를 우선해야 함 (replan 시)</summary>
        public bool ShouldPrioritizeRetreat;

        /// <summary>보너스 무기 전환 이미 수행됨 (Phase 1.55/1.56 탁구 방지)</summary>
        public bool BonusWeaponSwitch;

        /// <summary>후퇴 지연 (Phase 1.6 → Phase 8 전달)</summary>
        public bool DeferRetreat;

        /// <summary>팀 전략이 Retreat 모드인가</summary>
        public bool IsRetreatMode;

        /// <summary>킬 시퀀스가 계획되었는가</summary>
        public bool DidPlanKillSequence;

        /// <summary>AoE 경쟁에서 보류된 킬 시퀀스</summary>
        public KillSimulator.KillSequence PendingKillSequence;

        /// <summary>킬 시퀀스로 계획된 타겟</summary>
        public BaseUnitEntity KillSequenceTarget;

        /// <summary>콤보 선행 능력 (Phase 4.5 → Phase 5 전달)</summary>
        public AbilityData ComboPrereqAbility;

        /// <summary>콤보 후속 능력 (Phase 5 → Phase 5.5 전달)</summary>
        public AbilityData ComboFollowUpAbility;

        /// <summary>콤보 선행 능력을 사용했는가</summary>
        public bool UsedComboPrereq;

        /// <summary>마크한 적 (Phase 4.6 → Phase 5 전달)</summary>
        public BaseUnitEntity MarkedTarget;

        /// <summary>적 1명 이하일 때 공격 AP 예약 (Phase 4 → Phase 5 복원)</summary>
        public float LastEnemyAttackReserve;

        /// <summary>무기 전환이 Phase 9.5에서 계획되었는가 (TurnEnding 스킵용)</summary>
        public bool WeaponSwitchPlanned;

        // ══════════════════════════════════════════════════════════════
        // Tank 전용
        // ══════════════════════════════════════════════════════════════

        /// <summary>방어 필요도 (Confidence 기반, 0.3~1.5)</summary>
        public float DefenseNeed;

        /// <summary>이동이 필요하여 ClearMP 방어 자세를 Phase 8.9로 연기</summary>
        public bool TankNeedsMovement;

        // ══════════════════════════════════════════════════════════════
        // Support 전용
        // ══════════════════════════════════════════════════════════════

        /// <summary>힐 임계값 (사용자 설정 + Confidence 보정)</summary>
        public float HealThreshold;

        // ══════════════════════════════════════════════════════════════
        // Overseer 전용
        // ══════════════════════════════════════════════════════════════

        /// <summary>HeroicAct가 계획되었는가 (Phase 2 → Phase 3 WarpRelay 콤보)</summary>
        public bool HeroicActPlanned;

        /// <summary>Raven 턴 페이즈: 버프/디버프 모드</summary>
        public bool IsRavenBuffPhase;

        /// <summary>공격적 재배치 수행 여부 (Phase 3.5.5 → Phase 4.6 중복 방지)</summary>
        public bool DidAggressiveRelocate;

        /// <summary>Phase 3.2.5에서 키스톤 사전 전달 완료</summary>
        public bool PreRelocateKeystoneDone;

        /// <summary>Phase 4.9에서 갭클로저가 계획됨 (Phase 5.6 중복 방지)</summary>
        public bool DidPlanGapCloserPhase49;

        /// <summary>AoE가 계획됨 (Phase 4.96/4.97 → Phase 5 didPlanAttack 초기화)</summary>
        public bool DidPlanAoE;

        // ══════════════════════════════════════════════════════════════
        // Ally Buff 루프 추적 (Support, Overseer)
        // ══════════════════════════════════════════════════════════════

        /// <summary>키스톤 GUID만 (아군 버프 Phase에서 중복 방지)</summary>
        public HashSet<string> KeystoneOnlyGuids;

        /// <summary>턴 부여 대상 추적 (같은 대상에게 중복 부여 방지)</summary>
        public HashSet<string> PlannedTurnGrantTargets;

        /// <summary>(buffGuid:targetId) 쌍 추적 — 같은 버프를 다른 아군에게는 허용</summary>
        public HashSet<string> PlannedBuffTargetPairs;

        /// <summary>능력별 계획 횟수 (과다 계획 방지)</summary>
        public Dictionary<string, int> PlannedAbilityUseCounts;

        // ══════════════════════════════════════════════════════════════
        // 팩토리 메서드
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// PlanContext 생성.
        /// BasePlan.CreateAPBudget()는 외부에서 호출 후 Budget 필드에 직접 할당.
        /// </summary>
        public static PlanContext Create(
            Situation situation, TurnState turnState,
            string roleName, AIRole role)
        {
            return new PlanContext
            {
                // 입력
                Situation = situation,
                TurnState = turnState,
                Unit = situation.Unit,
                RoleName = roleName,
                Role = role,

                // AP/MP
                RemainingAP = situation.CurrentAP,
                RemainingMP = situation.CurrentMP,

                // 행동 수집
                Actions = new List<PlannedAction>(),

                // 공통 플래그
                DidPlanAttack = false,
                HasMoveInPlan = false,
                AttacksPlanned = 0,
                AttackContext = new AttackPhaseContext(),

                // 타겟/능력 추적
                PlannedTargetIds = new HashSet<string>(),
                PlannedAbilityGuids = new HashSet<string>(),

                // Familiar
                UsedKeystoneAbilityGuids = new HashSet<string>(),
                UsedWarpRelay = false,

                // 위치 버프
                UsedPositionalBuffs = new HashSet<string>(),

                // DPS
                ComboAlreadyApplied = false,
                ComboTargetId = null,
                ShouldPrioritizeRetreat = false,
                BonusWeaponSwitch = false,
                DeferRetreat = false,
                IsRetreatMode = false,
                DidPlanKillSequence = false,
                PendingKillSequence = null,
                KillSequenceTarget = null,
                ComboPrereqAbility = null,
                ComboFollowUpAbility = null,
                UsedComboPrereq = false,
                MarkedTarget = null,
                LastEnemyAttackReserve = 0f,
                WeaponSwitchPlanned = false,

                // Overseer
                HeroicActPlanned = false,
                IsRavenBuffPhase = true,
                DidAggressiveRelocate = false,
                PreRelocateKeystoneDone = false,
                DidPlanGapCloserPhase49 = false,
                DidPlanAoE = false,

                // Ally Buff 루프
                KeystoneOnlyGuids = null,        // 필요 시 lazy init
                PlannedTurnGrantTargets = null,   // 필요 시 lazy init
                PlannedBuffTargetPairs = null,    // 필요 시 lazy init
                PlannedAbilityUseCounts = null,   // 필요 시 lazy init
            };
        }
    }
}
