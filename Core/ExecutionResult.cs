using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using UnityEngine;

namespace CompanionAI_v3.Core
{
    /// <summary>
    /// 행동 실행 결과
    /// TurnOrchestrator → 게임 AI로 전달되는 명령
    /// </summary>
    public class ExecutionResult
    {
        public ResultType Type { get; private set; }
        public AbilityData Ability { get; private set; }
        public TargetWrapper Target { get; private set; }
        public Vector3? Destination { get; private set; }
        public string Reason { get; private set; }

        /// <summary>
        /// ★ v3.7.25: MultiTarget 능력용 타겟 리스트
        /// AerialRush 등 2개 이상의 타겟이 필요한 능력에 사용
        /// </summary>
        public List<TargetWrapper> AllTargets { get; private set; }

        private ExecutionResult() { }

        #region Factory Methods

        /// <summary>
        /// 능력 시전 명령
        /// </summary>
        public static ExecutionResult CastAbility(AbilityData ability, TargetWrapper target)
        {
            return new ExecutionResult
            {
                Type = ResultType.CastAbility,
                Ability = ability,
                Target = target,
                Reason = $"Cast {ability?.Name} on {target?.Entity?.ToString() ?? "point"}"
            };
        }

        /// <summary>
        /// ★ v3.7.25: MultiTarget 능력 시전 명령
        /// AerialRush 등 2개 이상의 타겟이 필요한 능력에 사용
        /// </summary>
        public static ExecutionResult CastAbilityMultiTarget(AbilityData ability, List<TargetWrapper> allTargets)
        {
            return new ExecutionResult
            {
                Type = ResultType.CastAbility,
                Ability = ability,
                Target = allTargets?.Count > 0 ? allTargets[0] : null,
                AllTargets = allTargets,
                Reason = $"Cast {ability?.Name} (MultiTarget: {allTargets?.Count ?? 0} targets)"
            };
        }

        /// <summary>
        /// 이동 명령
        /// </summary>
        public static ExecutionResult MoveTo(Vector3 destination)
        {
            return new ExecutionResult
            {
                Type = ResultType.MoveTo,
                Destination = destination,
                Reason = $"Move to {destination}"
            };
        }

        /// <summary>
        /// 턴 종료
        /// </summary>
        public static ExecutionResult EndTurn(string reason = "No more actions")
        {
            return new ExecutionResult
            {
                Type = ResultType.EndTurn,
                Reason = reason
            };
        }

        /// <summary>
        /// 실패 (게임 AI에 위임)
        /// </summary>
        public static ExecutionResult Failure(string reason)
        {
            return new ExecutionResult
            {
                Type = ResultType.Failure,
                Reason = reason
            };
        }

        /// <summary>
        /// 계속 진행 (다음 행동 처리 필요)
        /// </summary>
        public static ExecutionResult Continue()
        {
            return new ExecutionResult
            {
                Type = ResultType.Continue,
                Reason = "Continue to next action"
            };
        }

        /// <summary>
        /// ★ v3.0.10: 대기 (이전 명령 실행 중)
        /// </summary>
        public static ExecutionResult Waiting(string reason = "Waiting for command completion")
        {
            return new ExecutionResult
            {
                Type = ResultType.Waiting,
                Reason = reason
            };
        }

        #endregion

        public override string ToString()
        {
            return $"[{Type}] {Reason}";
        }
    }

    /// <summary>
    /// 실행 결과 타입
    /// </summary>
    public enum ResultType
    {
        /// <summary>능력 시전</summary>
        CastAbility,

        /// <summary>위치로 이동</summary>
        MoveTo,

        /// <summary>턴 종료</summary>
        EndTurn,

        /// <summary>실패 - 게임 AI에 위임</summary>
        Failure,

        /// <summary>계속 진행</summary>
        Continue,

        /// <summary>★ v3.0.10: 대기 - 이전 명령 실행 중</summary>
        Waiting
    }
}
