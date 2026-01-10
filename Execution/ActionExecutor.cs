using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.GameInterface;

namespace CompanionAI_v3.Execution
{
    /// <summary>
    /// 행동 실행기 - 계획된 행동을 실행
    /// </summary>
    public class ActionExecutor
    {
        /// <summary>
        /// ★ v3.5.00: 킬 확인용 타겟 HP 캐시
        /// 공격 전 타겟 HP를 저장하여 공격 후 비교
        /// </summary>
        private readonly Dictionary<string, TargetSnapshot> _targetSnapshots = new Dictionary<string, TargetSnapshot>();

        private class TargetSnapshot
        {
            public BaseUnitEntity Target { get; set; }
            public float HPBefore { get; set; }
            public bool WasAlive { get; set; }
        }

        /// <summary>
        /// 계획된 행동 실행
        /// </summary>
        public ExecutionResult Execute(PlannedAction action, Situation situation)
        {
            if (action == null)
            {
                return ExecutionResult.EndTurn("No action");
            }

            Main.LogDebug($"[Executor] Executing: {action}");

            try
            {
                switch (action.Type)
                {
                    case ActionType.Buff:
                    case ActionType.Attack:
                    case ActionType.Heal:
                    case ActionType.Debuff:
                    case ActionType.Support:
                    case ActionType.Special:
                    case ActionType.Reload:
                        return ExecuteAbility(action);

                    case ActionType.Move:
                        return ExecuteMove(action);

                    case ActionType.EndTurn:
                        return ExecutionResult.EndTurn(action.Reason);

                    default:
                        return ExecutionResult.Failure($"Unknown action type: {action.Type}");
                }
            }
            catch (Exception ex)
            {
                Main.LogError($"[Executor] Error executing {action.Type}: {ex.Message}");
                return ExecutionResult.Failure($"Execution error: {ex.Message}");
            }
        }

        /// <summary>
        /// 능력 실행
        /// </summary>
        private ExecutionResult ExecuteAbility(PlannedAction action)
        {
            var ability = action.Ability;
            var target = action.Target;

            if (ability == null)
            {
                return ExecutionResult.Failure("Ability is null");
            }

            if (target == null)
            {
                return ExecutionResult.Failure("Target is null");
            }

            // ★ v3.5.15: 그룹 쿨다운 체크 (계획과 실행 사이에 쿨다운될 수 있음)
            // 계획 시점에서는 사용 가능했지만, 이전 액션 실행으로 그룹이 쿨다운될 수 있음
            if (CombatAPI.IsAbilityOnCooldownWithGroups(ability))
            {
                Main.LogWarning($"[Executor] Ability skipped (group cooldown): {ability.Name}");
                return ExecutionResult.Failure($"Group cooldown: {ability.Name}");
            }

            // ★ v3.0.93: 능력 자체가 사용 가능한지 먼저 체크 (쿨다운, 탄약, 충전 등)
            List<string> unavailableReasons;
            if (!CombatAPI.IsAbilityAvailable(ability, out unavailableReasons))
            {
                string reasons = string.Join(", ", unavailableReasons);
                Main.LogWarning($"[Executor] Ability unavailable: {ability.Name} - {reasons}");
                return ExecutionResult.Failure($"Ability unavailable: {reasons}");
            }

            // 최종 검증 - 타겟에게 사용 가능한지
            string reason;
            if (!CombatAPI.CanUseAbilityOn(ability, target, out reason))
            {
                Main.LogWarning($"[Executor] Ability blocked: {ability.Name} - {reason}");
                return ExecutionResult.Failure(reason);
            }

            // ★ v3.5.00: 공격 시 타겟 스냅샷 저장 + 예상 피해량 기록
            var targetEntity = target.Entity as BaseUnitEntity;
            if (action.Type == ActionType.Attack && targetEntity != null)
            {
                // 공격 전 타겟 상태 저장
                string targetId = targetEntity.UniqueId;
                _targetSnapshots[targetId] = new TargetSnapshot
                {
                    Target = targetEntity,
                    HPBefore = CombatAPI.GetHPPercent(targetEntity),
                    WasAlive = !targetEntity.LifeState.IsDead
                };

                // 예상 피해량 기록 (Response Curve 기반 추정치)
                float estimatedDamage = CombatAPI.EstimateDamage(ability, targetEntity);
                if (estimatedDamage > 0)
                {
                    TeamBlackboard.Instance.RecordDamageDealt(estimatedDamage);
                    Main.LogDebug($"[Executor] Attack: {ability.Name} -> {targetEntity.CharacterName}, EstDmg={estimatedDamage:F0}");
                }
            }

            // ★ v3.5.29: 캐시 무효화 - 타겟 위치가 변할 수 있는 능력
            // Attack, Debuff 등 적에게 사용하는 능력은 밀치기/이동 효과가 있을 수 있음
            if (action.Type == ActionType.Attack || action.Type == ActionType.Debuff)
            {
                var cacheTarget = target.Entity as BaseUnitEntity;
                if (cacheTarget != null)
                {
                    CombatCache.InvalidateTarget(cacheTarget);
                }
            }

            // 실행 명령 반환
            Main.Log($"[Executor] Cast: {ability.Name} -> {GetTargetName(target)}");
            return ExecutionResult.CastAbility(ability, target);
        }

        /// <summary>
        /// ★ v3.5.00: 이전 공격 타겟의 킬 여부 확인
        /// TurnOrchestrator에서 명령 완료 후 호출
        /// </summary>
        public void CheckForKills()
        {
            foreach (var kvp in _targetSnapshots)
            {
                var snapshot = kvp.Value;
                if (snapshot.Target == null) continue;

                // 공격 전에 살아있었는데 지금 죽었으면 킬
                if (snapshot.WasAlive && snapshot.Target.LifeState.IsDead)
                {
                    TeamBlackboard.Instance.RecordKill(snapshot.Target);
                    Main.Log($"[Executor] ★ Kill confirmed: {snapshot.Target.CharacterName}");
                }
            }

            _targetSnapshots.Clear();
        }

        /// <summary>
        /// ★ v3.5.00: 스냅샷 초기화 (턴 시작 시)
        /// </summary>
        public void ClearSnapshots()
        {
            _targetSnapshots.Clear();
        }

        /// <summary>
        /// 이동 실행
        /// </summary>
        private ExecutionResult ExecuteMove(PlannedAction action)
        {
            if (!action.MoveDestination.HasValue)
            {
                return ExecutionResult.Failure("Move destination is null");
            }

            var destination = action.MoveDestination.Value;

            Main.Log($"[Executor] Move to: {destination}");
            return ExecutionResult.MoveTo(destination);
        }

        /// <summary>
        /// 타겟 이름 추출 (로깅용)
        /// </summary>
        private string GetTargetName(TargetWrapper target)
        {
            if (target == null) return "null";

            if (target.Entity is Kingmaker.EntitySystem.Entities.BaseUnitEntity unit)
            {
                return unit.CharacterName;
            }

            // ★ v3.0.46: 부동소수점 비교 개선
            if (target.Point.sqrMagnitude > 0.001f)
            {
                return $"Point({target.Point})";
            }

            return "unknown";
        }
    }
}
