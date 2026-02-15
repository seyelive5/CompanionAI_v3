using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Utility;
using CompanionAI_v3.GameInterface;

namespace CompanionAI_v3.Core
{
    /// <summary>
    /// 턴 전체 계획
    /// TurnPlanner가 생성하고, TurnOrchestrator가 순차 실행
    /// </summary>
    public class TurnPlan
    {
        #region Properties

        /// <summary>계획된 행동 큐</summary>
        private readonly Queue<PlannedAction> _actionQueue;

        /// <summary>전체 계획된 행동 목록 (디버깅용)</summary>
        public IReadOnlyList<PlannedAction> AllActions { get; }

        /// <summary>턴 우선순위</summary>
        public TurnPriority Priority { get; }

        /// <summary>계획 수립 이유</summary>
        public string Reasoning { get; }

        /// <summary>남은 행동 수</summary>
        public int RemainingActionCount => _actionQueue.Count;

        /// <summary>모든 행동 완료 여부</summary>
        public bool IsComplete => _actionQueue.Count == 0;

        /// <summary>계획 수립 시점 HP%</summary>
        public float InitialHP { get; private set; }

        /// <summary>계획 수립 시점 가장 가까운 적 거리</summary>
        public float InitialNearestEnemyDistance { get; private set; }

        /// <summary>★ v3.0.6: 계획 수립 시점 공격 가능 적 수</summary>
        public int InitialHittableCount { get; private set; }

        /// <summary>★ v3.1.09: 계획 수립 시점 AP (리플랜 감지용)</summary>
        public float InitialAP { get; private set; }

        /// <summary>★ v3.1.09: 계획 수립 시점 MP (리플랜 감지용)</summary>
        public float InitialMP { get; private set; }

        /// <summary>★ v3.5.88: 계획 수립 시점 0 AP 공격 수 (Break Through → Slash 감지용)</summary>
        public int InitialZeroAPAttackCount { get; private set; }

        /// <summary>★ v3.0.6: 계획에 공격이 포함되어 있는지</summary>
        public bool HasAttackActions { get; private set; }

        /// <summary>★ v3.1.05: 계획에 이동이 포함되어 있는지</summary>
        public bool HasMoveActions { get; private set; }

        /// <summary>★ v3.8.86: 실패한 그룹 태그 (lazy init, 보통 null)</summary>
        private HashSet<string> _failedGroups;

        #endregion

        #region Constructor

        public TurnPlan(List<PlannedAction> actions, TurnPriority priority, string reasoning,
            float initialHP = 100f, float initialNearestEnemyDist = float.MaxValue, int initialHittable = 0,
            float initialAP = 0f, float initialMP = 0f, int initialZeroAPAttacks = 0)  // ★ v3.5.88: 0 AP 공격 수 추가
        {
            AllActions = actions.AsReadOnly();
            Priority = priority;
            Reasoning = reasoning;
            InitialHP = initialHP;
            InitialNearestEnemyDistance = initialNearestEnemyDist;
            InitialHittableCount = initialHittable;  // ★ v3.0.6
            InitialAP = initialAP;  // ★ v3.1.09
            InitialMP = initialMP;  // ★ v3.1.09
            InitialZeroAPAttackCount = initialZeroAPAttacks;  // ★ v3.5.88

            _actionQueue = new Queue<PlannedAction>();
            foreach (var action in actions)
            {
                _actionQueue.Enqueue(action);
            }

            // ★ v3.0.6: 공격 행동 포함 여부
            HasAttackActions = actions.Any(a => a.Type == ActionType.Attack);

            // ★ v3.1.05: 이동 행동 포함 여부
            HasMoveActions = actions.Any(a => a.Type == ActionType.Move);

            Main.Log($"[TurnPlan] Created: Priority={priority}, Actions={actions.Count}, Reason={reasoning}");
            foreach (var action in actions)
            {
                Main.LogDebug($"[TurnPlan]   - {action}");
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// 다음 실행할 행동 가져오기
        /// </summary>
        public PlannedAction GetNextAction()
        {
            if (_actionQueue.Count == 0)
                return null;

            return _actionQueue.Dequeue();
        }

        /// <summary>
        /// 다음 행동 미리보기 (제거하지 않음)
        /// </summary>
        public PlannedAction PeekNextAction()
        {
            if (_actionQueue.Count == 0)
                return null;

            return _actionQueue.Peek();
        }

        /// <summary>
        /// 계획 재수립 필요 여부 판단
        /// ★ v3.0.93: 능력 Available 및 타겟 Hittable 체크 추가
        /// ★ v3.1.09: AP/MP 증가 감지 추가, 조건 순서 정리
        ///
        /// 조건 순서:
        /// 1. 실행 불가 조건 (필수 리플랜)
        /// 2. 긴급 상황 조건 (필수 리플랜)
        /// 3. 새 기회 조건 (선택적 리플랜)
        /// </summary>
        public bool NeedsReplan(Analysis.Situation currentSituation)
        {
            if (currentSituation == null) return false;
            if (_actionQueue.Count == 0) return false;

            var nextAction = _actionQueue.Peek();
            if (nextAction == null) return false;

            // ═══════════════════════════════════════════════════════════
            // 1. 실행 불가 조건 (필수 리플랜)
            // ═══════════════════════════════════════════════════════════

            // 1-1. 다음 액션의 능력이 사용 불가능해졌는지
            if (nextAction.Ability != null && nextAction.Type != ActionType.EndTurn)
            {
                List<string> unavailableReasons;
                if (!CombatAPI.IsAbilityAvailable(nextAction.Ability, out unavailableReasons))
                {
                    // ★ v3.7.20: 사역마 타겟 액션은 NullTarget 이유 무시
                    // 실행 시점에 FamiliarAPI.GetFamiliar()로 타겟 재해석되므로
                    // stale 참조로 인한 NullTarget은 리플랜 사유가 아님
                    if (nextAction.IsFamiliarTarget)
                    {
                        var nonTargetReasons = unavailableReasons.Where(r => r != "NullTarget").ToList();
                        if (nonTargetReasons.Count > 0)
                        {
                            string reasons = string.Join(", ", nonTargetReasons);
                            Main.Log($"[TurnPlan] Replan needed: Ability {nextAction.Ability.Name} no longer available ({reasons})");
                            return true;
                        }
                        // NullTarget만 있으면 무시하고 계속 진행
                        Main.LogDebug($"[TurnPlan] Familiar target action - ignoring NullTarget, will re-resolve at execution");
                    }
                    else
                    {
                        string reasons = string.Join(", ", unavailableReasons);
                        Main.Log($"[TurnPlan] Replan needed: Ability {nextAction.Ability.Name} no longer available ({reasons})");
                        return true;
                    }
                }

                // 1-2. 공격/디버프 타겟이 공격 불가능해졌는지
                // ★ v3.7.20: 사역마 타겟 액션은 스킵 (실행 시 재해석)
                // ★ v3.7.25: MultiTarget 능력 스킵 (Point 타겟이므로 LOS 체크 불필요)
                if ((nextAction.Type == ActionType.Attack || nextAction.Type == ActionType.Debuff)
                    && nextAction.Target != null
                    && !nextAction.IsFamiliarTarget
                    && (nextAction.AllTargets == null || nextAction.AllTargets.Count == 0))
                {
                    string cantUseReason;
                    if (!CombatAPI.CanUseAbilityOn(nextAction.Ability, nextAction.Target, out cantUseReason))
                    {
                        var targetUnit = nextAction.Target.Entity as BaseUnitEntity;
                        string targetName = targetUnit?.CharacterName ?? "target";
                        Main.Log($"[TurnPlan] Replan needed: Cannot use {nextAction.Ability.Name} on {targetName} ({cantUseReason})");
                        return true;
                    }
                }
            }

            // 1-3. 계획된 타겟 사망
            if (nextAction.Type == ActionType.Attack)
            {
                var target = nextAction.Target?.Entity as BaseUnitEntity;
                if (target != null && target.LifeState.IsDead)
                {
                    Main.Log($"[TurnPlan] Replan needed: Target {target.CharacterName} is dead");
                    return true;
                }
            }

            // 1-4. 모든 적 처치됨
            if (!currentSituation.HasLivingEnemies && Priority != TurnPriority.EndTurn)
            {
                Main.Log("[TurnPlan] Replan needed: All enemies dead");
                return true;
            }

            // ★ v3.8.42: 궁극기 전용 턴은 긴급/기회 리플랜 불필요
            // 궁극기만 사용 가능한 턴이므로 HP 급감, 위협 변화, AP/MP 증가 등은 의미 없음
            // Section 1(실행 불가 감지)만으로 충분
            if (Priority == TurnPriority.Critical)
                return false;

            // ═══════════════════════════════════════════════════════════
            // 2. 긴급 상황 조건 (필수 리플랜)
            // ═══════════════════════════════════════════════════════════

            // 2-1. HP 급감 (임계값 이상)
            // ★ v3.5.36: 매직 넘버를 GameConstants로 교체
            if (Priority != TurnPriority.Emergency && currentSituation.IsHPCritical)
            {
                float hpDrop = InitialHP - currentSituation.HPPercent;
                if (hpDrop >= GameConstants.HP_CRITICAL_DROP_THRESHOLD)
                {
                    Main.Log($"[TurnPlan] Replan needed: HP dropped {hpDrop:F0}% (was {InitialHP:F0}%, now {currentSituation.HPPercent:F0}%)");
                    return true;
                }
            }

            // 2-2. 원거리 캐릭터 위협 상황 변화
            if (currentSituation.PrefersRanged && Priority != TurnPriority.Retreat)
            {
                if (InitialNearestEnemyDistance > currentSituation.MinSafeDistance &&
                    currentSituation.NearestEnemyDistance <= currentSituation.MinSafeDistance)
                {
                    Main.Log($"[TurnPlan] Replan needed: Enemy closed in (was {InitialNearestEnemyDistance:F1}m, now {currentSituation.NearestEnemyDistance:F1}m)");
                    return true;
                }
            }

            // ═══════════════════════════════════════════════════════════
            // 3. 새 기회 조건 (선택적 리플랜)
            // ═══════════════════════════════════════════════════════════

            // 3-1. ★ v3.1.09: MP 증가 감지 (런앤건, 무모한 돌진 등)
            // Move가 이미 계획되어 있으면 리플랜 불필요 (Move 실행 기회 보존)
            if (currentSituation.CurrentMP > InitialMP && !HasMoveActions)
            {
                Main.Log($"[TurnPlan] Replan needed: MP increased ({InitialMP:F1} -> {currentSituation.CurrentMP:F1}) - movement opportunity");
                return true;
            }

            // 3-2. ★ v3.1.09: AP 증가 감지 (전투 트랜스 등 버프)
            // 이미 행동을 계획했는데 AP가 늘었으면 추가 행동 가능
            // ★ v3.5.36: 매직 넘버를 GameConstants로 교체
            if (currentSituation.CurrentAP > InitialAP + GameConstants.AP_RECOVERY_EPSILON)
            {
                Main.Log($"[TurnPlan] Replan needed: AP increased ({InitialAP:F1} -> {currentSituation.CurrentAP:F1}) - additional action opportunity");
                return true;
            }

            // 3-3. 새로운 공격 기회 발생 (처음 0이었는데 지금 > 0)
            // ★ v3.9.26: NormalHittableCount 사용 — DangerousAoE 부풀림이 불필요한 replan 유발 방지
            if (!HasAttackActions && InitialHittableCount == 0 && currentSituation.NormalHittableCount > 0)
            {
                Main.Log($"[TurnPlan] Replan needed: New attack opportunity ({currentSituation.NormalHittableCount} normal targets now available)");
                return true;
            }

            // 3-4. ★ v3.5.88: 새로운 0 AP 공격 발생 (Break Through → Slash 등)
            // 계획 수립 시점에 없던 0 AP 공격이 새로 생겼으면 리플랜
            if (currentSituation.Unit != null)
            {
                var zeroAPAttacks = GameInterface.CombatAPI.GetZeroAPAttacks(currentSituation.Unit);
                if (zeroAPAttacks.Count > InitialZeroAPAttackCount)
                {
                    string attackNames = string.Join(", ", zeroAPAttacks.Select(a => a.Name));
                    Main.Log($"[TurnPlan] Replan needed: New 0 AP attack available ({zeroAPAttacks.Count} > {InitialZeroAPAttackCount}): {attackNames}");
                    return true;
                }
            }

            // 3-5. 공격 가능 적 크게 증가
            // ★ v3.9.26: NormalHittableCount 사용 — DangerousAoE 부풀림이 불필요한 replan 유발 방지
            if (InitialHittableCount > 0)
            {
                int currentHittable = currentSituation.NormalHittableCount;
                if (currentHittable >= InitialHittableCount + GameConstants.MIN_ADDITIONAL_HITTABLE_TARGETS)
                {
                    Main.Log($"[TurnPlan] Replan needed: More targets in range (was {InitialHittableCount}, now {currentHittable})");
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// ★ v3.8.86: 그룹을 실패로 표시하고 큐에서 해당 그룹 액션 즉시 제거
        /// GetNextAction/PeekNextAction 수정 불필요 — 실패 시점에 큐 정리
        /// </summary>
        public void FailGroup(string groupTag)
        {
            if (string.IsNullOrEmpty(groupTag)) return;
            if (_failedGroups == null) _failedGroups = new HashSet<string>();
            _failedGroups.Add(groupTag);

            // 큐에서 해당 그룹 액션 즉시 제거
            int removedCount = 0;
            int queueSize = _actionQueue.Count;
            for (int i = 0; i < queueSize; i++)
            {
                var action = _actionQueue.Dequeue();
                if (action.GroupTag == groupTag)
                {
                    removedCount++;
                    Main.LogDebug($"[TurnPlan] Purged: {action} (group '{groupTag}' failed)");
                }
                else
                {
                    _actionQueue.Enqueue(action);
                }
            }
            Main.Log($"[TurnPlan] Group '{groupTag}' failed — {removedCount} actions purged from queue");
        }

        /// <summary>
        /// 남은 계획 취소
        /// </summary>
        public void Cancel(string reason)
        {
            Main.Log($"[TurnPlan] Cancelled: {reason}");
            _actionQueue.Clear();
        }

        public override string ToString()
        {
            return $"[TurnPlan] Priority={Priority}, Remaining={RemainingActionCount}, {Reasoning}";
        }

        #endregion
    }

    /// <summary>
    /// 턴 우선순위 (상황에 따른 전략)
    /// </summary>
    public enum TurnPriority
    {
        /// <summary>★ v3.8.42: 궁극기 전용 턴 (잠재력 초월 등, 최고 우선순위)</summary>
        Critical = -10,

        /// <summary>긴급 (HP 위험, 즉시 힐/후퇴)</summary>
        Emergency = 0,

        /// <summary>후퇴 (원거리가 근접 위험)</summary>
        Retreat = 10,

        /// <summary>재장전 필수</summary>
        Reload = 20,

        /// <summary>버프 후 공격</summary>
        BuffedAttack = 30,

        /// <summary>직접 공격</summary>
        DirectAttack = 40,

        /// <summary>이동 후 공격</summary>
        MoveAndAttack = 50,

        /// <summary>아군 지원</summary>
        Support = 60,

        /// <summary>턴 종료 (할 게 없음)</summary>
        EndTurn = 100
    }
}
