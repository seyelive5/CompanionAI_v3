using System;
using System.Reflection;
using HarmonyLib;
using Kingmaker;
using Kingmaker.AI;
using Kingmaker.AI.BehaviourTrees;
using Kingmaker.AI.BehaviourTrees.Nodes;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic;
using Pathfinding;
using UnityEngine;
using CompanionAI_v3.Core;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// ★ v3.5.28: BehaviourTree 완전 대체 - UpdateBehaviourTree 직접 패치
    ///
    /// v3.5.26~27의 문제: BehaviourTreeBuilder.Create/CreateForUnit 패치로는 불충분
    /// - 게임이 내부적으로 캐싱하거나 다른 경로로 트리를 설정할 수 있음
    /// - 결과: 커스텀 트리가 생성되지만 게임 트리가 실제로 실행됨
    ///
    /// v3.5.28 해결: PartUnitBrain.UpdateBehaviourTree() 직접 패치
    /// - m_BehaviourTree 필드를 리플렉션으로 직접 설정
    /// - 100% 확실하게 커스텀 트리가 사용되도록 보장
    ///
    /// 커스텀 트리 구조:
    /// Selector
    /// ├── Sequence
    /// │   ├── TaskNodeWaitCommandsDone
    /// │   └── Condition (Commands.Empty && CanAct)
    /// │       └── Sequence
    /// │           ├── AsyncTaskNodeInitializeDecisionContext
    /// │           ├── CompanionAIDecisionNode  ← 모든 결정 처리
    /// │           └── Selector
    /// │               ├── Condition(Ability != null) → TaskNodeCastAbility
    /// │               └── Condition(FoundBetterPlace) → Movement nodes
    /// └── TaskNodeTryFinishTurn
    /// </summary>
    [HarmonyPatch]
    public static class CustomBehaviourTreePatch
    {
        /// <summary>
        /// ★ v3.5.28: m_BehaviourTree 필드 리플렉션 캐시
        /// </summary>
        private static readonly FieldInfo _behaviourTreeField = typeof(PartUnitBrain)
            .GetField("m_BehaviourTree", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// ★ 턴 시작 시간 추적 (IsActingEnabled 구현용)
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<string, TimeSpan> _turnStartTimes
            = new System.Collections.Generic.Dictionary<string, TimeSpan>();

        /// <summary>
        /// ★ 턴 시작 후 대기 시간 (초) - 애니메이션 겹침 방지
        /// </summary>
        private const float SecondsToWaitAtStart = 0.5f;

        #region PartUnitBrain.UpdateBehaviourTree Patch

        /// <summary>
        /// ★ v3.5.28: UpdateBehaviourTree() 직접 패치
        ///
        /// 이전 방식 (Create/CreateForUnit 패치)이 불완전했던 이유:
        /// - 게임이 트리를 다른 경로로 설정할 수 있음
        /// - Harmony __result 설정이 제대로 적용되지 않을 수 있음
        ///
        /// 새 방식:
        /// - Postfix에서 m_BehaviourTree 필드를 직접 확인
        /// - CompanionAI 유닛이면 커스텀 트리로 교체
        /// </summary>
        [HarmonyPatch(typeof(PartUnitBrain), "UpdateBehaviourTree")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.High)]
        public static void UpdateBehaviourTree_Postfix(PartUnitBrain __instance)
        {
            try
            {
                // Owner 확인
                var unit = __instance.Owner as BaseUnitEntity;
                if (unit == null) return;

                // CompanionAI 대상 유닛인지 확인
                if (!TurnOrchestrator.Instance.ShouldControl(unit))
                {
                    return;
                }

                // 커스텀 트리 생성
                if (!TryCreateCustomTree(unit, out var customTree))
                {
                    Main.LogWarning($"[CustomBehaviourTree] Failed to create custom tree for {unit.CharacterName}");
                    return;
                }

                // m_BehaviourTree 필드 직접 설정
                if (_behaviourTreeField == null)
                {
                    Main.LogError("[CustomBehaviourTree] m_BehaviourTree field not found!");
                    return;
                }

                // 새 트리 설정 (게임이 이미 트리를 생성했지만, 우리 트리로 교체)
                _behaviourTreeField.SetValue(__instance, customTree);

                Main.Log($"[CustomBehaviourTree] Replaced tree for {unit.CharacterName}");
            }
            catch (Exception ex)
            {
                Main.LogError($"[CustomBehaviourTree] UpdateBehaviourTree_Postfix error: {ex.Message}");
            }
        }

        /// <summary>
        /// ★ v3.5.28: 커스텀 트리 생성 로직
        /// ★ v3.6.17: Loop 노드 추가 - 턴 종료까지 여러 액션 실행
        /// </summary>
        private static bool TryCreateCustomTree(BaseUnitEntity unit, out BehaviourTree result)
        {
            result = null;
            try
            {
                Main.LogDebug($"[CustomBehaviourTree] Creating custom tree for {unit.CharacterName}");

                // ★ CompanionAI 전용 심플 트리 생성
                // 핵심: 모든 결정이 CompanionAIDecisionNode를 통과

                // ★ v3.6.17: 메인 액션 시퀀스 (루프 내부)
                var mainSelector = new Selector(
                    new Sequence(
                        new TaskNodeWaitCommandsDone(),
                        new Condition(
                            b => b.Unit.Commands.Empty && b.Unit.State.CanActInTurnBased,
                            new Sequence(
                                new AsyncTaskNodeInitializeDecisionContext(),
                                new AsyncTaskNodeCreateMoveVariants(),  // ★ v3.5.28: 이동 가능 타일 계산 (이동 시 필요)
                                new CompanionAIDecisionNode(),  // ★ Ability 또는 FoundBetterPlace 설정
                                new Selector(
                                    // Path 1: 능력 시전
                                    new Condition(
                                        b => b.DecisionContext?.Ability != null,
                                        new TaskNodeCastAbility()
                                    ),
                                    // Path 2: 이동 실행
                                    // ★ v3.5.33: CombatAPI.CanMove() 체크 추가 - 이동 불가 시 이동 브랜치 스킵
                                    new Condition(
                                        b => b.DecisionContext != null &&
                                             !b.DecisionContext.FoundBetterPlace.PathData.IsZero &&
                                             CombatAPI.CanMove(b.DecisionContext.Unit),
                                        new Sequence(
                                            TaskNodeSetupMoveCommand.ToBetterPosition(),
                                            new TaskNodeExecuteMoveCommand()
                                        )
                                    )
                                    // Path 3: 둘 다 없으면 Selector 실패 → 턴 종료로 이동
                                )
                            )
                        )
                    ),
                    new TaskNodeTryFinishTurn()
                );

                // ★ v3.6.17: Loop로 감싸기 - IsFinishedTurn이 true가 될 때까지 반복
                // 이전 문제: Loop 없이 첫 액션 후 Success 반환 → 다음 액션 실행 안 됨
                var rootNode = new Loop(
                    b => { },                    // 초기화 (불필요)
                    b => !b.IsFinishedTurn,      // 턴 종료까지 계속 반복
                    mainSelector,
                    Loop.ExitCondition.NoCondition
                );

                result = new BehaviourTree(unit, rootNode, new DecisionContext());
                return true;  // 커스텀 트리 생성 성공
            }
            catch (Exception ex)
            {
                Main.LogError($"[CustomBehaviourTree] Error creating custom tree: {ex.Message}");
                return false;  // 실패 시 게임 기본 트리 사용
            }
        }

        #endregion

        #region Turn Start Time Tracking

        /// <summary>
        /// 턴 시작 시간 기록 (TurnEventHandler에서 호출)
        /// </summary>
        public static void RecordTurnStart(string unitId)
        {
            _turnStartTimes[unitId] = Game.Instance.TimeController.RealTime;
        }

        /// <summary>
        /// 턴 종료 시 정리
        /// </summary>
        public static void ClearTurnStart(string unitId)
        {
            _turnStartTimes.Remove(unitId);
        }

        /// <summary>
        /// ★ IsActingEnabled - 턴 시작 후 0.5초 대기 (애니메이션 겹침 방지)
        /// 게임의 PartUnitBrain.IsActingEnabled와 동일한 로직
        /// </summary>
        public static bool IsActingEnabled(BaseUnitEntity unit)
        {
            if (unit == null) return false;

            // Squad 유닛은 즉시 행동 가능
            if (unit.IsInSquad) return true;

            // 턴 시작 시간 확인
            if (!_turnStartTimes.TryGetValue(unit.UniqueId, out var turnStartTime))
            {
                // 기록 없으면 즉시 행동 허용 (안전)
                return true;
            }

            var currentTime = Game.Instance.TimeController.RealTime;
            var waitTime = TimeSpan.FromSeconds(SecondsToWaitAtStart);

            return currentTime >= turnStartTime + waitTime;
        }

        #endregion
    }

    /// <summary>
    /// ★ v3.5.26: 모든 AI 결정을 처리하는 단일 노드
    ///
    /// 역할:
    /// 1. TurnOrchestrator.ProcessTurn() 호출
    /// 2. 결과에 따라 context.Ability 또는 context.FoundBetterPlace 설정
    /// 3. Status.Success 반환 → 다음 노드에서 실행
    /// </summary>
    public class CompanionAIDecisionNode : TaskNode
    {
        public CompanionAIDecisionNode() : base("CompanionAIDecisionNode")
        {
        }

        protected override Status TickInternal(Blackboard blackboard)
        {
            var context = blackboard.DecisionContext;
            if (context == null)
            {
                Main.LogWarning("[CompanionAIDecisionNode] DecisionContext is null");
                return Status.Failure;
            }

            var unit = context.Unit;
            if (unit == null)
            {
                Main.LogWarning("[CompanionAIDecisionNode] Unit is null");
                return Status.Failure;
            }

            try
            {
                // ★ v3.7.00: 사역마 유닛은 턴 스킵 (Master가 제어)
                if (FamiliarAPI.IsFamiliar(unit))
                {
                    var master = FamiliarAPI.GetMaster(unit);
                    Main.LogDebug($"[CompanionAIDecisionNode] {unit.CharacterName}: Familiar unit - skipping (controlled by {master?.CharacterName ?? "master"})");
                    blackboard.IsFinishedTurn = true;
                    return Status.Success;  // 즉시 턴 종료
                }

                // ★ IsActingEnabled 체크 - 턴 시작 후 0.5초 대기
                if (!CustomBehaviourTreePatch.IsActingEnabled(unit))
                {
                    return Status.Running;  // 대기
                }

                // ★ 명령 큐가 비어있지 않으면 대기 (이동/능력 애니메이션 중)
                if (!CombatAPI.IsCommandQueueEmpty(unit))
                {
                    return Status.Running;
                }

                // ★ 이미 턴 종료 결정되었으면 즉시 반환
                if (blackboard.IsFinishedTurn)
                {
                    return Status.Success;
                }

                // ★ TurnOrchestrator에서 결정 가져오기
                var result = TurnOrchestrator.Instance.ProcessTurn(unit);

                switch (result.Type)
                {
                    case ResultType.CastAbility:
                        // 능력 시전 → context에 설정하고 Success
                        context.Ability = result.Ability;
                        context.AbilityTarget = result.Target;
                        // ★ v3.5.33: Stale 이동 데이터 클리어 - Charge 후 이동 버그 방지
                        context.FoundBetterPlace = default;
                        Main.Log($"[CompanionAIDecisionNode] {unit.CharacterName}: Cast {result.Ability?.Name} -> {result.Target?.Entity}");
                        return Status.Success;  // → Selector가 TaskNodeCastAbility 실행

                    case ResultType.MoveTo:
                        // ★ 이동 → FoundBetterPlace 설정
                        if (result.Destination.HasValue)
                        {
                            if (SetupMovement(unit, result.Destination.Value, context))
                            {
                                Main.Log($"[CompanionAIDecisionNode] {unit.CharacterName}: Move to {result.Destination.Value}");
                                return Status.Success;  // → Selector가 Movement 노드 실행
                            }
                            else
                            {
                                Main.LogWarning($"[CompanionAIDecisionNode] {unit.CharacterName}: Failed to setup movement");
                            }
                        }
                        // 이동 설정 실패 시 턴 종료
                        blackboard.IsFinishedTurn = true;
                        return Status.Success;

                    case ResultType.EndTurn:
                        // 턴 종료
                        blackboard.IsFinishedTurn = true;
                        context.Ability = null;
                        context.AbilityTarget = null;
                        Main.Log($"[CompanionAIDecisionNode] {unit.CharacterName}: End turn - {result.Reason}");
                        return Status.Success;  // → Selector가 TaskNodeTryFinishTurn으로

                    case ResultType.Continue:
                        // 다음 행동 계속
                        return Status.Running;

                    case ResultType.Waiting:
                        // 대기 상태 유지
                        return Status.Running;

                    case ResultType.Failure:
                    default:
                        Main.LogWarning($"[CompanionAIDecisionNode] {unit.CharacterName}: Failure - {result.Reason}");
                        blackboard.IsFinishedTurn = true;
                        return Status.Success;  // 실패 시 턴 종료
                }
            }
            catch (Exception ex)
            {
                Main.LogError($"[CompanionAIDecisionNode] {unit?.CharacterName}: Error - {ex.Message}");
                blackboard.IsFinishedTurn = true;
                return Status.Success;  // 예외 시 턴 종료
            }
        }

        /// <summary>
        /// ★ 이동 설정 - context.FoundBetterPlace 설정
        /// TaskNodeSetupMoveCommand.ToBetterPosition()이 이 값을 읽어서 경로 생성
        /// </summary>
        private bool SetupMovement(BaseUnitEntity unit, Vector3 destination, DecisionContext context)
        {
            // ★ v3.5.33: 기존 stale 데이터 클리어
            context.FoundBetterPlace = default;

            try
            {
                // UnitMoveVariants 확인
                if (context.UnitMoveVariants.IsZero)
                {
                    Main.LogWarning($"[CompanionAIDecisionNode] No move variants available");
                    return false;
                }

                var cells = context.UnitMoveVariants.cells;
                if (cells == null || cells.Count == 0)
                {
                    Main.LogWarning($"[CompanionAIDecisionNode] Move variants cells is empty");
                    return false;
                }

                // 목적지 노드 찾기
                var targetNode = destination.GetNearestNodeXZ() as CustomGridNodeBase;
                if (targetNode == null)
                {
                    Main.LogWarning($"[CompanionAIDecisionNode] Cannot find node for destination {destination}");
                    return false;
                }

                WarhammerPathAiCell bestCell = default;
                bool foundCell = false;
                float bestDistance = float.MaxValue;

                // 목적지에 정확히 있으면 사용, 아니면 가장 가까운 cell 찾기
                if (cells.TryGetValue(targetNode, out var exactCell))
                {
                    bestCell = exactCell;
                    foundCell = true;
                }
                else
                {
                    // 가장 가까운 이동 가능 노드 찾기
                    foreach (var kvp in cells)
                    {
                        var node = kvp.Key as CustomGridNodeBase;
                        if (node == null) continue;

                        float dist = Vector3.Distance(node.Vector3Position, destination);
                        if (dist < bestDistance)
                        {
                            bestDistance = dist;
                            bestCell = kvp.Value;
                            foundCell = true;
                        }
                    }
                }

                if (!foundCell)
                {
                    Main.LogWarning($"[CompanionAIDecisionNode] No reachable cell found for destination");
                    return false;
                }

                // MP 체크 및 경로 축소
                // ★ v3.5.36: 경로 길이가 실제로 줄어드는지 확인하는 안전장치 추가
                float availableMP = unit.CombatState?.ActionPointsBlue ?? 0f;
                if (bestCell.Length > availableMP)
                {
                    Main.LogDebug($"[CompanionAIDecisionNode] Trimming path (need {bestCell.Length:F1}, have {availableMP:F1})");

                    var trimmedCell = bestCell;
                    while (trimmedCell.Length > availableMP && trimmedCell.ParentNode != null)
                    {
                        if (cells.TryGetValue(trimmedCell.ParentNode, out var parentCell))
                        {
                            // 경로 길이가 실제로 줄어드는지 확인
                            if (parentCell.Length < trimmedCell.Length)
                            {
                                trimmedCell = parentCell;
                            }
                            else
                            {
                                Main.LogDebug($"[CompanionAIDecisionNode] Parent path not shorter, stopping trim");
                                break;
                            }
                        }
                        else break;
                    }
                    bestCell = trimmedCell;
                }

                // 현재 위치와 같으면 이동 불필요
                var currentNode = unit.Position.GetNearestNodeXZ();
                if (bestCell.Node == currentNode)
                {
                    Main.LogDebug($"[CompanionAIDecisionNode] Already at destination");
                    return false;
                }

                // ★ FoundBetterPlace 설정 - TaskNodeSetupMoveCommand가 이 값을 읽음
                context.FoundBetterPlace = new DecisionContext.BetterPlace
                {
                    PathData = context.UnitMoveVariants,
                    BestCell = bestCell
                };

                Main.Log($"[CompanionAIDecisionNode] Movement setup complete - BestCell.Node={bestCell.Node}");
                return true;
            }
            catch (Exception ex)
            {
                Main.LogError($"[CompanionAIDecisionNode] SetupMovement error: {ex.Message}");
                return false;
            }
        }
    }
}
