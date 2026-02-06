using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kingmaker;
using Kingmaker.AI;
using Kingmaker.AI.BehaviourTrees;
using Kingmaker.AI.BehaviourTrees.Nodes;
using Kingmaker.Blueprints;  // ★ v3.7.29: GetComponent<T>() 확장 메서드
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Abilities.Components;  // ★ v3.7.29: AbilityMultiTarget
using Kingmaker.UnitLogic.Commands;
using Kingmaker.Utility;
using Pathfinding;
using UnityEngine;
using CompanionAI_v3.Core;
using CompanionAI_v3.Data;  // ★ v3.7.52: FamiliarAbilities

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

        #region PartUnitBrain.Tick Patch - 매 Tick마다 트리 확인/교체

        /// <summary>
        /// ★ v3.7.17: Tick() Prefix 패치 - 핵심 수정
        ///
        /// 문제:
        /// - UpdateBehaviourTree()는 SetBrain(), SetCustomBehaviour() 등에서만 호출됨
        /// - 턴마다 호출되지 않음
        /// - 게임이 어떤 시점에 트리를 다시 설정하면 우리 커스텀 트리가 덮어쓰여짐
        /// - 로그에서 "Root (Type: Selector)" 확인 → 게임 네이티브 트리가 실행 중
        ///
        /// 해결:
        /// - Tick() 호출 시마다 트리가 우리 것인지 확인
        /// - 아니면 커스텀 트리로 교체
        /// - 이렇게 하면 100% 확실하게 커스텀 트리 사용
        /// </summary>
        [HarmonyPatch(typeof(PartUnitBrain), "Tick")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        public static void Tick_Prefix(PartUnitBrain __instance)
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

                // 현재 트리 확인
                if (_behaviourTreeField == null)
                {
                    Main.LogError("[CustomBehaviourTree] m_BehaviourTree field not found!");
                    return;
                }

                var currentTree = _behaviourTreeField.GetValue(__instance) as BehaviourTree;

                // 이미 커스텀 트리인지 확인 (루트가 Loop인지)
                if (IsCustomTree(currentTree))
                {
                    return;  // 이미 커스텀 트리 → 교체 불필요
                }

                // 커스텀 트리가 아님 → 교체 필요
                Main.LogWarning($"[CustomBehaviourTree] {unit.CharacterName}: Native tree detected, replacing with custom tree");

                // 커스텀 트리 생성
                if (!TryCreateCustomTree(unit, out var customTree))
                {
                    Main.LogError($"[CustomBehaviourTree] Failed to create custom tree for {unit.CharacterName}");
                    return;
                }

                // 새 트리 설정
                _behaviourTreeField.SetValue(__instance, customTree);

                // 트리 초기화 (중요!)
                customTree.Init();

                Main.Log($"[CustomBehaviourTree] Replaced tree for {unit.CharacterName} (during Tick)");
            }
            catch (Exception ex)
            {
                Main.LogError($"[CustomBehaviourTree] Tick_Prefix error: {ex.Message}");
            }
        }

        /// <summary>
        /// ★ v3.7.17: 커스텀 트리인지 확인
        /// 우리 트리는 Loop가 루트, 게임 트리는 Selector가 루트
        /// ★ v3.7.18: 필드 이름 수정 - m_Root → root (게임 코드 확인)
        /// </summary>
        private static readonly FieldInfo _rootNodeField = typeof(BehaviourTree)
            .GetField("root", BindingFlags.NonPublic | BindingFlags.Instance);

        private static bool IsCustomTree(BehaviourTree tree)
        {
            if (tree == null) return false;

            try
            {
                if (_rootNodeField == null)
                {
                    Main.LogError("[CustomBehaviourTree] root field not found in BehaviourTree!");
                    return false;
                }

                var rootNode = _rootNodeField.GetValue(tree) as BehaviourTreeNode;
                if (rootNode == null) return false;

                // 우리 커스텀 트리는 Loop가 루트
                // 게임 네이티브 트리는 Selector가 루트
                return rootNode is Loop;
            }
            catch (Exception ex)
            {
                Main.LogError($"[CustomBehaviourTree] IsCustomTree error: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region PartUnitBrain.UpdateBehaviourTree Patch (백업)

        /// <summary>
        /// ★ v3.5.28: UpdateBehaviourTree() 직접 패치 (Tick_Prefix의 백업)
        ///
        /// Tick_Prefix가 주된 메커니즘이지만, 첫 번째 호출 전에도
        /// 커스텀 트리가 설정되도록 UpdateBehaviourTree Postfix도 유지
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

                // ★ v3.7.16: 메인 액션 시퀀스 (루프 내부)
                // ★ 수정: SafeAsyncTaskNodeInitializeDecisionContext로 교체 - 정렬 예외 방지
                var mainSelector = new Selector(
                    new Sequence(
                        new TaskNodeWaitCommandsDone(),
                        new Condition(
                            b => b.Unit.Commands.Empty && b.Unit.State.CanActInTurnBased,
                            new Sequence(
                                new AsyncTaskNodeInitializeDecisionContext(),  // ★ v3.7.19: 래퍼 제거, 직접 사용 복원
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
                        // ★ v3.7.25: MultiTarget 능력 처리 (AerialRush 등)
                        if (result.AllTargets != null && result.AllTargets.Count > 0)
                        {
                            // ★ v3.7.48: MultiTarget 능력 실행
                            //
                            // 게임 검증 흐름 (decompile 분석 결과):
                            // 1. AbilityMultiTarget.Deliver()가 m_AbilityQueue의 능력을 Eagle 커맨드 큐에 추가
                            // 2. UnitUseAbility.OnInit()에서 CanTarget(Point2, Eagle.Position) 호출
                            // 3. AbilityCustomDirectMovement.CheckTargetRestriction()이 Eagle.Position에서 Point2까지 경로 계산
                            //
                            // ★ 핵심: Point2 검증은 Eagle의 "현재 위치"에서 수행됨!
                            // → PointTargetingHelper.FindBestAerialRushPath()에서 Point1 = Eagle.Position 보장 필요
                            // → BasePlan.PlanFamiliarAerialRush()에서 이미 Eagle 위치 기반 경로 탐색 수행
                            //
                            // MultiTarget 능력은 직접 명령 실행 (TaskNodeCastAbility 우회)
                            // ★ v3.7.26: 스킬은 Master가 시전 - LOS 검증은 PlanFamiliarAerialRush에서 수행
                            var cmd = new UnitUseAbilityParams(result.Ability, result.Target);
                            cmd.AllTargets = result.AllTargets;
                            unit.Commands.Run(cmd);
                            Main.Log($"[CompanionAIDecisionNode] {unit.CharacterName}: Cast MultiTarget {result.Ability?.Name} ({result.AllTargets.Count} targets)");
                            return Status.Running;  // 명령 완료 대기 (다음 tick에서 IsCommandQueueEmpty 체크)
                        }

                        // ★ v3.7.29: 방어적 체크 - MultiTarget 능력이 AllTargets 없이 시전되면 거부
                        // 이 상황은 SituationAnalyzer 필터가 실패했음을 의미 (예: 알 수 없는 GUID)
                        // ★ v3.7.52: AbilityMultiTarget 컴포넌트 대신 IsMultiTargetFamiliarAbility() 사용
                        // AbilityMultiTarget이 있어도 단일 Point로 시전 가능한 능력이 있음 (예: Servo-Skull Redirect)
                        if (FamiliarAbilities.IsMultiTargetFamiliarAbility(result.Ability))
                        {
                            Main.LogError($"[CompanionAIDecisionNode] BLOCKED: MultiTarget ability {result.Ability.Name} without AllTargets! This ability requires 2 Point targets.");
                            // 이 능력을 스킵하고 다음 행동으로 (리플랜 트리거)
                            return Status.Running;
                        }

                        // 일반 능력 시전 → context에 설정하고 Success
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
                                // ★ v3.8.42: 이동 중 카메라 추적
                                // 게임은 UnitMoveToProper에 대한 카메라 핸들러가 없어서
                                // 직접 Follower.Follow()를 호출하여 이동 중 카메라가 유닛을 따라감
                                // (다음 능력 시전 시 자동으로 Release됨)
                                try
                                {
                                    if (Kingmaker.Settings.SettingsRoot.Game.TurnBased.CameraScrollToCurrentUnit.GetValue())
                                    {
                                        Game.Instance.CameraController?.Follower?.Follow(unit);
                                    }
                                }
                                catch (Exception) { /* 카메라 실패가 AI를 중단시키지 않도록 */ }

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
