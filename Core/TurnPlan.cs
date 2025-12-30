using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;

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

        /// <summary>★ v3.0.6: 계획에 공격이 포함되어 있는지</summary>
        public bool HasAttackActions { get; private set; }

        #endregion

        #region Constructor

        public TurnPlan(List<PlannedAction> actions, TurnPriority priority, string reasoning,
            float initialHP = 100f, float initialNearestEnemyDist = float.MaxValue, int initialHittable = 0)
        {
            AllActions = actions.AsReadOnly();
            Priority = priority;
            Reasoning = reasoning;
            InitialHP = initialHP;
            InitialNearestEnemyDistance = initialNearestEnemyDist;
            InitialHittableCount = initialHittable;  // ★ v3.0.6

            _actionQueue = new Queue<PlannedAction>();
            foreach (var action in actions)
            {
                _actionQueue.Enqueue(action);
            }

            // ★ v3.0.6: 공격 행동 포함 여부
            HasAttackActions = actions.Any(a => a.Type == ActionType.Attack);

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
        /// </summary>
        public bool NeedsReplan(Analysis.Situation currentSituation)
        {
            if (currentSituation == null) return false;
            if (_actionQueue.Count == 0) return false;

            // 1. HP 급감 체크 - Emergency가 아니었는데 지금 위험하면 재계획
            if (Priority != TurnPriority.Emergency && currentSituation.IsHPCritical)
            {
                float hpDrop = InitialHP - currentSituation.HPPercent;
                if (hpDrop >= 20f)  // 20% 이상 감소
                {
                    Main.Log($"[TurnPlan] Replan needed: HP dropped {hpDrop:F0}% (was {InitialHP:F0}%, now {currentSituation.HPPercent:F0}%)");
                    return true;
                }
            }

            // 2. 계획된 타겟 사망 체크
            var nextAction = _actionQueue.Peek();
            if (nextAction != null && nextAction.Type == ActionType.Attack)
            {
                var target = nextAction.Target?.Entity as BaseUnitEntity;
                if (target != null && target.LifeState.IsDead)
                {
                    Main.Log($"[TurnPlan] Replan needed: Target {target.CharacterName} is dead");
                    return true;
                }
            }

            // 3. 원거리 캐릭터 위협 상황 변화
            if (currentSituation.PrefersRanged && Priority != TurnPriority.Retreat)
            {
                // 원래 안전했는데 지금 위험해짐
                if (InitialNearestEnemyDistance > currentSituation.MinSafeDistance &&
                    currentSituation.NearestEnemyDistance <= currentSituation.MinSafeDistance)
                {
                    Main.Log($"[TurnPlan] Replan needed: Enemy closed in (was {InitialNearestEnemyDistance:F1}m, now {currentSituation.NearestEnemyDistance:F1}m)");
                    return true;
                }
            }

            // 4. 모든 적 처치됨
            if (!currentSituation.HasLivingEnemies && Priority != TurnPriority.EndTurn)
            {
                Main.Log("[TurnPlan] Replan needed: All enemies dead");
                return true;
            }

            // ★ v3.0.6: 5. 새로운 공격 기회 발생
            // ★ v3.0.61: 조건 수정 - "처음에 공격 불가였는데 지금 가능해진 경우"만
            // "Skip attack" 결정 시 HasAttackActions=false지만 InitialHittableCount>0
            // 이 경우는 의도적 스킵이므로 Replan 금지
            if (!HasAttackActions && InitialHittableCount == 0 && currentSituation.HasHittableEnemies)
            {
                Main.Log($"[TurnPlan] Replan needed: New attack opportunity ({currentSituation.HittableEnemies?.Count ?? 0} targets now available)");
                return true;
            }

            // ★ v3.0.6: 6. 공격 가능 적 크게 증가 (이동 후 더 많은 적 사정권)
            if (InitialHittableCount > 0 && currentSituation.HittableEnemies != null)
            {
                int currentHittable = currentSituation.HittableEnemies.Count;
                if (currentHittable >= InitialHittableCount + 2)  // 2명 이상 추가
                {
                    Main.Log($"[TurnPlan] Replan needed: More targets in range (was {InitialHittableCount}, now {currentHittable})");
                    return true;
                }
            }

            return false;
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
