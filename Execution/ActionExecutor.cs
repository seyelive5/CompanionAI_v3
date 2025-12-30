using System;
using System.Collections.Generic;
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

            // 실행 명령 반환
            Main.Log($"[Executor] Cast: {ability.Name} -> {GetTargetName(target)}");
            return ExecutionResult.CastAbility(ability, target);
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
