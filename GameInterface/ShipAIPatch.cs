using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Kingmaker.AI;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using UnityEngine;
using Warhammer.SpaceCombat.AI;
using Warhammer.SpaceCombat.AI.BehaviourTrees;
using Kingmaker.Pathfinding;
using CompanionAI_v3.Core;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// ★ v3.21.6: 함선 AI 패치 — 플레이어 함선에 기본 AI 스코어링 제공
    ///
    /// 문제: 플레이어 함선은 BlueprintStarshipBrain이 없음 (수동 조작 전제)
    ///       → 게임 네이티브 AI의 스코어링 함수들이 모두 0을 반환 → 아무 행동도 안 함
    ///
    /// 해결: 3개 메서드를 패치하여 brain이 없을 때 기본값 제공:
    /// 1. CalculateDestinationScore → 기본 거리 점수 (desiredDistance=3타일)
    /// 2. CalculateTrajectoryScore  → 목적지 점수 + 능력 가치 합산 (위협 계산 제외)
    /// 3. GetAbilityValue           → 기본 능력 가치 1 (스코어링용)
    ///
    /// 조건: StarshipEntity + IsPlayerFaction + EnableShipCombatAI=true
    ///       + brain이 BlueprintStarshipBrain이 아닐 때만 활성화
    ///       → 적 함선, 지상전투 유닛에는 절대 영향 없음
    /// </summary>
    [HarmonyPatch]
    public static class ShipAIPatch
    {
        #region Patch 1: CalculateDestinationScore

        /// <summary>
        /// 목적지 점수 계산 — brain이 없으면 기본 거리 기반 점수 제공
        /// 원본: brain == null → return 0f
        /// 패치: 적과의 최소 거리 기반 스코어링 (desiredDistance=3)
        /// 공식: 0.95 / (1 + |minEnemyDist - 3|) — 원본과 동일
        /// </summary>
        [HarmonyPatch(typeof(TaskNodeFindBestTrajectory), "CalculateDestinationScore")]
        [HarmonyPrefix]
        public static bool CalculateDestinationScore_Prefix(
            DecisionContext context, Vector3 destination, ref float __result)
        {
            if (context?.Unit?.Brain?.Blueprint is BlueprintStarshipBrain)
                return true; // brain 있음 → 원본 실행

            if (!TurnOrchestrator.IsShipAIDelegated(context?.Unit as BaseUnitEntity))
                return true; // AI 위임 대상 아님 → 원본 실행

            __result = CalculateDefaultDestinationScore(context, destination);
            return false;
        }

        #endregion

        #region Patch 2: CalculateTrajectoryScore

        /// <summary>
        /// 궤적 점수 계산 — brain이 없으면 기본 스코어링 제공
        /// 원본: DestinationScore 계산 후 brain == null → return 0f
        /// 패치: DestinationScore + 경로 상 능력 가치 합산 (ExtraMeasures 필터/위협 계산 제외)
        /// </summary>
        [HarmonyPatch(typeof(TaskNodeFindBestTrajectory), "CalculateTrajectoryScore")]
        [HarmonyPrefix]
        public static bool CalculateTrajectoryScore_Prefix(
            SpaceCombatDecisionContext context,
            HashSet<ShipPath.DirectionalPathNode> reachableTiles,
            ShipPath.DirectionalPathNode targetNode,
            ref float __result)
        {
            if (context?.Unit?.Brain?.Blueprint is BlueprintStarshipBrain)
                return true;

            if (!TurnOrchestrator.IsShipAIDelegated(context?.Unit as BaseUnitEntity))
                return true;

            // 1. 목적지 점수 (기본 desiredDistance=3)
            float destScore = CalculateDefaultDestinationScore(
                context, targetNode.node.Vector3Position);

            // 2. 경로 상 능력별 최대 가치 합산
            //    brain이 없으므로 ExtraMeasures 필터 없음 (모든 능력 포함)
            var maxAbilityValues = new Dictionary<BlueprintAbility, int>();
            for (var node = targetNode; node != null; node = node.parent)
            {
                foreach (var ability in context.Unit.Abilities.RawFacts)
                {
                    int value = context.AbilityValueCache.GetValue(node, ability);
                    int existing;
                    if (!maxAbilityValues.TryGetValue(ability.Blueprint, out existing)
                        || existing < value)
                    {
                        maxAbilityValues[ability.Blueprint] = value;
                    }
                }
            }
            int totalAbilityValue = maxAbilityValues.Values.Sum();

            // 3. 위협 점수 = 0 (brain 없으므로 FearOfMeteors/TryToStayBehind 미적용)
            __result = destScore + (float)totalAbilityValue;
            return false;
        }

        #endregion

        #region Patch 3: GetAbilityValue

        /// <summary>
        /// 능력 가치 평가 — 플레이어 함선의 기본 능력 가치 제공
        /// 원본: BlueprintBrainBase.GetAbilityValue → 0 (BlueprintStarshipBrain이 아닐 때)
        /// 패치: 결과가 0이면 기본값 1 반환 → 모든 능력을 동등하게 취급
        ///       (실제 사용 가치는 CanTargetFromNode + FiringArc 체크로 결정됨)
        /// </summary>
        [HarmonyPatch(typeof(PartUnitBrain), nameof(PartUnitBrain.GetAbilityValue))]
        [HarmonyPostfix]
        public static void GetAbilityValue_Postfix(PartUnitBrain __instance, ref int __result)
        {
            if (__result != 0) return;
            if (!TurnOrchestrator.IsShipAIDelegated(__instance.Owner as BaseUnitEntity)) return;

            // 기본 능력 가치 = 1
            // AbilityValueCalculator.Calculate가 CanTargetFromNode + FiringArc를 이미 검증함
            // → 여기서는 "타겟 가능한 능력은 모두 가치 1" 로 처리
            __result = 1;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// 기본 목적지 점수: 적과의 최소 거리를 기준으로 스코어링
        /// desiredDistance=3 (BlueprintStarshipBrain 기본값과 동일)
        /// 공식: 0.95 / (1 + |minEnemyDist - desiredDistance|)
        /// → 적으로부터 3타일 거리일 때 최대 점수
        /// </summary>
        private static float CalculateDefaultDestinationScore(
            DecisionContext context, Vector3 destination)
        {
            if (context?.Enemies == null || context.Enemies.Count == 0)
                return 0f;

            int minDist = int.MaxValue;
            foreach (var enemy in context.Enemies)
            {
                var starship = enemy.Entity as StarshipEntity;
                if (starship != null && !starship.Blueprint.IsSoftUnit)
                {
                    int dist = starship.DistanceToInCells(destination);
                    if (dist < minDist) minDist = dist;
                }
            }

            if (minDist == int.MaxValue) return 0f;

            const float desiredDistance = 3f;
            return 0.95f / (1f + Math.Abs((float)minDist - desiredDistance));
        }

        #endregion
    }
}
