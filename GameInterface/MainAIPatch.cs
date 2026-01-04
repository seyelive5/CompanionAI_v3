using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Kingmaker;
using Kingmaker.AI;
using Kingmaker.AI.BehaviourTrees;
using Kingmaker.AI.BehaviourTrees.Nodes;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Pathfinding;
using CompanionAI_v3.Core;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// 메인 AI 패치 - 폴백 및 게임 상태 제어
    ///
    /// ★ v3.5.28: BehaviourTree 완전 대체 후 역할
    ///
    /// 패치 체인:
    /// 1. CustomBehaviourTreePatch.UpdateBehaviourTree_Postfix (CustomBehaviourTree.cs)
    ///    └─ CompanionAI 유닛에 커스텀 트리 적용 (정상 경로)
    ///    └─ CompanionAIDecisionNode가 모든 AI 결정 처리
    ///
    /// 2. MainAIPatch (이 파일) - 폴백 및 게임 상태 제어
    ///    ├─ SelectAbilityTarget_Prefix: 커스텀 트리 미적용 시 폴백
    ///    ├─ FindBetterPlace_Prefix: 이동 로직 폴백 + 원거리 포지셔닝
    ///    ├─ IsAiTurn_Postfix: 게임에 "AI 턴" 보고
    ///    ├─ IsPlayerTurn_Postfix: 게임에 "플레이어 턴 아님" 보고
    ///    ├─ IsAIEnabled_Postfix: Brain 활성화 상태 제어
    ///    └─ IsUsualMeleeUnit_Postfix: 원거리 유닛 돌진 방지
    /// </summary>
    [HarmonyPatch]
    public static class MainAIPatch
    {
        #region Patch Target - TaskNodeSelectAbilityTarget (폴백)

        /// <summary>
        /// ★ v3.5.28: 폴백 패치
        /// - 정상 경로: CustomBehaviourTree의 CompanionAIDecisionNode가 처리
        /// - 폴백 경로: 커스텀 트리 미적용 유닛 (모드 로드 전 생성된 트리, 호환 문제 등)
        /// </summary>
        [HarmonyPatch(typeof(TaskNodeSelectAbilityTarget), "TickInternal")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        public static bool SelectAbilityTarget_Prefix(
            TaskNodeSelectAbilityTarget __instance,
            Blackboard blackboard,  // ★ Blackboard 파라미터 추가
            ref Status __result)
        {
            // ★ v3.0.75: 이미 턴이 종료되었으면 즉시 반환
            // IsFinishedTurn = true 설정 후에도 Root가 반복되면서 다시 호출될 수 있음
            if (blackboard?.IsFinishedTurn == true)
            {
                __result = Status.Success;
                return false;
            }

            // ★ v2.2 방식: Blackboard에서 DecisionContext 가져오기
            var context = blackboard?.DecisionContext;
            if (context == null)
            {
                Main.LogDebug("[MainAIPatch] Context is null");
                return true;
            }

            var unit = context.Unit;
            if (unit == null)
            {
                return true;
            }

            // ★ v3.1.08: 이동/능력 애니메이션 중에는 대기 (LESSONS_LEARNED 12.2 적용)
            // 문제: 이동 중 게임이 AI를 계속 호출 → EndTurn 반복 반환 → 렉
            // 해결: Commands가 비어있지 않으면 Running 반환하여 게임이 대기하게 함
            if (!CombatAPI.IsCommandQueueEmpty(unit))
            {
                __result = Status.Running;
                return false;
            }

            if (!TurnOrchestrator.Instance.ShouldControl(unit))
            {
                return true;
            }

            try
            {
                var result = TurnOrchestrator.Instance.ProcessTurn(unit);

                switch (result.Type)
                {
                    case ResultType.CastAbility:
                        context.Ability = result.Ability;
                        context.AbilityTarget = result.Target;
                        __result = Status.Success;
                        Main.Log($"[MainAIPatch] {unit.CharacterName}: Cast {result.Ability?.Name} -> {result.Target?.Entity}");
                        return false;

                    case ResultType.MoveTo:
                        // ★ 완전 제어: 게임 AI에 위임하지 않음 (LESSONS_LEARNED 12.2)
                        // ★ v3.1.08: ClearPendingEndTurn 제거 - Commands 체크로 대체
                        // 이동 목적지 저장 - FindBetterPlace에서 사용
                        if (result.Destination.HasValue)
                        {
                            TurnOrchestrator.Instance.SetPendingMoveDestination(unit.UniqueId, result.Destination.Value);
                            Main.Log($"[MainAIPatch] {unit.CharacterName}: Move to {result.Destination.Value}");
                        }
                        // 능력 없음 → 이동 결정 로직으로 넘어감
                        context.Ability = null;
                        context.AbilityTarget = null;
                        __result = Status.Failure;  // ★ 능력 선택 실패 → 이동 로직 트리거
                        return false;  // ★ 원본 실행 안 함

                    case ResultType.EndTurn:
                        // ★ v3.0.72: 게임의 방식대로 턴 종료
                        // IsFinishedTurn = true 설정하면 행동 트리가 종료되고 턴이 넘어감
                        // Status.Success 반환하면 Selector가 다른 브랜치(FindBetterPlace 등) 시도 안 함
                        blackboard.IsFinishedTurn = true;
                        context.Ability = null;
                        context.AbilityTarget = null;
                        __result = Status.Success;
                        Main.Log($"[MainAIPatch] {unit.CharacterName}: End turn - {result.Reason}");
                        return false;

                    case ResultType.Continue:
                        __result = Status.Running;
                        return false;

                    // ★ v3.0.65: Waiting 상태 처리 - 게임 AI로 위임하지 않음
                    case ResultType.Waiting:
                        __result = Status.Running;  // 대기 상태 유지
                        return false;  // 게임 AI 실행 안 함

                    case ResultType.Failure:
                    default:
                        Main.LogWarning($"[MainAIPatch] {unit.CharacterName}: Delegating to game AI - {result.Reason}");
                        return true;
                }
            }
            catch (Exception ex)
            {
                Main.LogError($"[MainAIPatch] {unit.CharacterName}: Critical error - {ex.Message}");
                return true;
            }
        }

        #endregion

        #region Patch - TaskNodeFindBetterPlace (이동 위치 결정)

        [HarmonyPatch(typeof(TaskNodeFindBetterPlace), "TickInternal")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        public static bool FindBetterPlace_Prefix(
            TaskNodeFindBetterPlace __instance,
            Blackboard blackboard,
            ref Status __result)
        {
            // ★ v3.0.75: 이미 턴이 종료되었으면 즉시 반환
            if (blackboard?.IsFinishedTurn == true)
            {
                __result = Status.Success;
                return false;
            }

            try
            {
                var context = blackboard?.DecisionContext;
                if (context == null) return true;

                var unit = context.Unit;
                if (unit == null || !unit.IsDirectlyControllable) return true;

                if (!TurnOrchestrator.Instance.ShouldControl(unit))
                {
                    return true;
                }

                // ★ v3.0.72: pendingEndTurn 체크 제거
                // EndTurn 시 Status.Success 반환하므로 FindBetterPlace가 호출되지 않음
                // (Selector는 Success 받으면 다음 브랜치를 시도하지 않음)

                var settings = Main.Settings?.GetOrCreateSettings(unit.UniqueId, unit.CharacterName);
                if (settings == null || !settings.EnableCustomAI) return true;

                // ★ TurnPlanner가 지정한 이동 목적지 확인
                var pendingDest = TurnOrchestrator.Instance.GetAndClearPendingMoveDestination(unit.UniqueId);
                if (pendingDest.HasValue)
                {
                    // ★ v3.0.53: MP 디버깅 - 왜 게임과 다른 값인지 확인
                    float actualMP = unit.CombatState?.ActionPointsBlue ?? -1f;
                    int moveVariantCount = context.UnitMoveVariants.cells?.Count ?? 0;
                    Main.Log($"[FindBetterPlace] {unit.CharacterName}: Pending dest={pendingDest.Value}, ActualMP={actualMP:F1}, MoveVariants={moveVariantCount}");

                    __result = FindBetterPlaceByDestination(context, unit, pendingDest.Value);
                    return false;
                }

                // 원거리 선호가 아니면 게임 AI
                var rangePreference = settings.RangePreference;
                if (rangePreference != RangePreference.PreferRanged)
                {
                    return true;
                }

                // ★ v3.0.65: 원거리 선호 캐릭터는 무기 종류와 관계없이 커스텀 로직 사용
                // (카시아처럼 근접무기를 들고 있어도 돌진하면 안 됨)
                bool hasRangedWeapon = HasRangedWeapon(unit);
                Main.Log($"[FindBetterPlace] {unit.CharacterName}: Using custom ranged positioning (hasRanged={hasRangedWeapon})");

                __result = FindBetterPlaceForRanged(context, unit, settings);
                return false;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[FindBetterPlace] Error: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// TurnPlanner가 지정한 목적지로 이동
        /// </summary>
        private static Status FindBetterPlaceByDestination(DecisionContext context, BaseUnitEntity unit, UnityEngine.Vector3 destination)
        {
            context.IsMoveCommand = true;

            // 목적지에서 가장 가까운 노드 찾기
            var targetNode = destination.GetNearestNodeXZ() as CustomGridNodeBase;
            if (targetNode == null)
            {
                Main.LogWarning($"[FindBetterPlace] {unit.CharacterName}: Cannot find node for destination");
                context.IsMoveCommand = false;
                return Status.Failure;
            }

            // UnitMoveVariants에서 해당 노드 또는 가장 가까운 이동 가능 노드 찾기
            var cells = context.UnitMoveVariants.cells;
            if (cells == null || cells.Count == 0)
            {
                Main.LogWarning($"[FindBetterPlace] {unit.CharacterName}: No move variants available");
                context.IsMoveCommand = false;
                return Status.Failure;
            }

            WarhammerPathAiCell bestCell = default;
            bool foundCell = false;
            float bestDistance = float.MaxValue;

            // 목적지에 정확히 있으면 사용, 아니면 가장 가까운 cell 찾기
            if (cells.TryGetValue(targetNode, out var exactCell))
            {
                bestCell = exactCell;
                foundCell = true;
                Main.Log($"[FindBetterPlace] {unit.CharacterName}: Found exact destination node");
            }
            else
            {
                // 가장 가까운 이동 가능 노드 찾기
                foreach (var kvp in cells)
                {
                    var node = kvp.Key as CustomGridNodeBase;
                    if (node == null) continue;

                    float dist = UnityEngine.Vector3.Distance(node.Vector3Position, destination);
                    if (dist < bestDistance)
                    {
                        bestDistance = dist;
                        bestCell = kvp.Value;
                        foundCell = true;
                    }
                }

                if (foundCell)
                {
                    Main.Log($"[FindBetterPlace] {unit.CharacterName}: Using closest reachable node (dist={bestDistance:F1})");
                }
            }

            if (!foundCell)
            {
                Main.LogWarning($"[FindBetterPlace] {unit.CharacterName}: No reachable cell found");
                context.IsMoveCommand = false;
                return Status.Failure;
            }

            // AP 체크 및 경로 축소
            float availableAP = unit.CombatState?.ActionPointsBlue ?? 0f;
            if (bestCell.Length > availableAP)
            {
                Main.Log($"[FindBetterPlace] {unit.CharacterName}: Trimming path (need {bestCell.Length:F1}, have {availableAP:F1})");

                var trimmedCell = bestCell;
                while (trimmedCell.Length > availableAP && trimmedCell.ParentNode != null)
                {
                    if (cells.TryGetValue(trimmedCell.ParentNode, out var parentCell))
                    {
                        trimmedCell = parentCell;
                    }
                    else break;
                }
                bestCell = trimmedCell;
            }

            // 현재 위치와 같으면 이동 불필요
            var currentNode = unit.Position.GetNearestNodeXZ();
            if (bestCell.Node == currentNode)
            {
                Main.Log($"[FindBetterPlace] {unit.CharacterName}: Already at destination");
                context.IsMoveCommand = false;
                return Status.Failure;  // 이동 불필요
            }

            context.FoundBetterPlace = new DecisionContext.BetterPlace
            {
                PathData = context.UnitMoveVariants,
                BestCell = bestCell
            };

            Main.Log($"[FindBetterPlace] {unit.CharacterName}: Moving to planned destination");
            context.IsMoveCommand = false;
            return Status.Success;
        }

        private static Status FindBetterPlaceForRanged(DecisionContext context, BaseUnitEntity unit, CharacterSettings settings)
        {
            context.IsMoveCommand = true;

            var enemies = CombatAPI.GetEnemies(unit);
            if (enemies == null || enemies.Count == 0)
            {
                Main.LogDebug($"[FindBetterPlace] {unit.CharacterName}: No enemies");
                context.IsMoveCommand = false;
                return Status.Failure;
            }

            // 무기 사거리 결정
            float weaponRange = 15f;
            try
            {
                var primaryHand = unit.Body?.PrimaryHand;
                if (primaryHand?.HasWeapon == true && !primaryHand.Weapon.Blueprint.IsMelee)
                {
                    int optRange = primaryHand.Weapon.AttackOptimalRange;
                    if (optRange > 0 && optRange < 10000)
                        weaponRange = optRange;
                    else
                    {
                        int attackRange = primaryHand.Weapon.AttackRange;
                        if (attackRange > 0 && attackRange < 10000)
                            weaponRange = attackRange;
                    }
                }
            }
            catch { }

            float minSafeDistance = settings?.MinSafeDistance ?? 5f;

            Main.Log($"[FindBetterPlace] {unit.CharacterName}: Finding position (range={weaponRange}, safe={minSafeDistance})");

            // MovementAPI로 최적 위치 찾기
            var bestPosition = MovementAPI.FindRangedAttackPositionSync(unit, enemies, weaponRange, minSafeDistance);

            if (bestPosition == null || bestPosition.Node == null)
            {
                Main.Log($"[FindBetterPlace] {unit.CharacterName}: No position found - staying");

                // 현재 위치 유지
                var currentNode = unit.Position.GetNearestNodeXZ() as CustomGridNodeBase;
                if (currentNode != null && context.UnitMoveVariants.cells != null &&
                    context.UnitMoveVariants.cells.TryGetValue(currentNode, out var currentCell))
                {
                    context.FoundBetterPlace = new DecisionContext.BetterPlace
                    {
                        PathData = context.UnitMoveVariants,
                        BestCell = currentCell
                    };
                    context.IsMoveCommand = false;
                    return Status.Success;
                }

                context.IsMoveCommand = false;
                return Status.Failure;
            }

            // UnitMoveVariants에서 해당 노드의 cell 가져오기
            if (context.UnitMoveVariants.cells != null &&
                context.UnitMoveVariants.cells.TryGetValue(bestPosition.Node, out var bestCell))
            {
                float availableAP = unit.CombatState?.ActionPointsBlue ?? 0f;

                // AP 부족하면 경로 축소
                if (bestCell.Length > availableAP)
                {
                    Main.Log($"[FindBetterPlace] {unit.CharacterName}: Trimming path (need {bestCell.Length:F1}, have {availableAP:F1})");

                    var trimmedCell = bestCell;
                    while (trimmedCell.Length > availableAP && trimmedCell.ParentNode != null)
                    {
                        if (context.UnitMoveVariants.cells.TryGetValue(trimmedCell.ParentNode, out var parentCell))
                        {
                            trimmedCell = parentCell;
                        }
                        else break;
                    }
                    bestCell = trimmedCell;
                }

                context.FoundBetterPlace = new DecisionContext.BetterPlace
                {
                    PathData = context.UnitMoveVariants,
                    BestCell = bestCell
                };

                var currentNode = unit.Position.GetNearestNodeXZ();
                if (bestCell.Node == currentNode)
                {
                    Main.Log($"[FindBetterPlace] {unit.CharacterName}: Already at optimal position");
                }
                else
                {
                    Main.Log($"[FindBetterPlace] {unit.CharacterName}: Moving to ({bestPosition.Position.x:F1},{bestPosition.Position.z:F1})");
                }

                context.IsMoveCommand = false;
                return Status.Success;
            }

            Main.Log($"[FindBetterPlace] {unit.CharacterName}: Node not in UnitMoveVariants");
            context.IsMoveCommand = false;
            return Status.Failure;
        }

        private static bool HasRangedWeapon(BaseUnitEntity unit)
        {
            try
            {
                var primaryHand = unit.Body?.PrimaryHand;
                if (primaryHand?.HasWeapon == true)
                {
                    var weapon = primaryHand.Weapon;
                    if (weapon?.Blueprint != null && !weapon.Blueprint.IsMelee)
                        return true;
                }

                var secondaryHand = unit.Body?.SecondaryHand;
                if (secondaryHand?.HasWeapon == true)
                {
                    var weapon = secondaryHand.Weapon;
                    if (weapon?.Blueprint != null && !weapon.Blueprint.IsMelee)
                        return true;
                }

                var altPrimaryHand = unit.Body?.HandsEquipmentSets?.LastOrDefault()?.PrimaryHand;
                if (altPrimaryHand?.HasWeapon == true)
                {
                    var weapon = altPrimaryHand.Weapon;
                    if (weapon?.Blueprint != null && !weapon.Blueprint.IsMelee)
                        return true;
                }
            }
            catch { }

            return false;
        }

        #endregion

        #region Turn Controller Patches

        [HarmonyPatch(typeof(Kingmaker.Controllers.TurnBased.TurnController), "IsAiTurn", MethodType.Getter)]
        [HarmonyPostfix]
        public static void IsAiTurn_Postfix(ref bool __result)
        {
            if (!Main.Enabled) return;

            var currentUnit = Game.Instance?.TurnController?.CurrentUnit as BaseUnitEntity;
            if (currentUnit != null && TurnOrchestrator.Instance.ShouldControl(currentUnit))
            {
                __result = true;
            }
        }

        [HarmonyPatch(typeof(Kingmaker.Controllers.TurnBased.TurnController), "IsPlayerTurn", MethodType.Getter)]
        [HarmonyPostfix]
        public static void IsPlayerTurn_Postfix(ref bool __result)
        {
            if (!Main.Enabled) return;

            var currentUnit = Game.Instance?.TurnController?.CurrentUnit as BaseUnitEntity;
            if (currentUnit != null && TurnOrchestrator.Instance.ShouldControl(currentUnit))
            {
                __result = false;
            }
        }

        #endregion

        #region Brain Patches

        [HarmonyPatch(typeof(Kingmaker.UnitLogic.PartUnitBrain), "IsAIEnabled", MethodType.Getter)]
        [HarmonyPostfix]
        public static void IsAIEnabled_Postfix(Kingmaker.UnitLogic.PartUnitBrain __instance, ref bool __result)
        {
            if (!Main.Enabled) return;

            var unit = __instance.Owner as BaseUnitEntity;
            if (unit != null && TurnOrchestrator.Instance.ShouldControl(unit))
            {
                __result = true;
            }
        }

        /// <summary>
        /// ★ IsUsualMeleeUnit 패치 - 원거리 선호 캐릭터가 적에게 돌진하지 않도록
        /// (Cassia 워프 가이드 스태프 같은 IsMelee=true 무기 보유 시 문제 방지)
        /// </summary>
        [HarmonyPatch(typeof(Kingmaker.UnitLogic.PartUnitBrain), "IsUsualMeleeUnit", MethodType.Getter)]
        [HarmonyPostfix]
        public static void IsUsualMeleeUnit_Postfix(Kingmaker.UnitLogic.PartUnitBrain __instance, ref bool __result)
        {
            if (!Main.Enabled) return;
            if (!__result) return;  // 이미 false면 패스

            var unit = __instance.Owner as BaseUnitEntity;
            if (unit == null) return;

            if (!TurnOrchestrator.Instance.ShouldControl(unit)) return;

            var settings = Main.Settings?.GetOrCreateSettings(unit.UniqueId, unit.CharacterName);
            if (settings == null) return;

            // 원거리 선호면 근접 유닛 취급 안 함 → 적에게 돌진하지 않음
            if (settings.RangePreference == RangePreference.PreferRanged)
            {
                __result = false;
                Main.LogDebug($"[MainAIPatch] {unit.CharacterName}: IsUsualMeleeUnit = false (PreferRanged)");
            }
        }

        #endregion
    }
}
