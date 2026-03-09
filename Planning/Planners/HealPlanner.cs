using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using UnityEngine;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// ★ v3.0.47: 힐/재장전 관련 계획 담당
    /// </summary>
    public static class HealPlanner
    {
        // ★ v3.42.0: Zero-allocation 임시 리스트
        private static readonly List<PlannedAction> _tempHealActions = new List<PlannedAction>();
        private static readonly List<BaseUnitEntity> _tempHealTargets = new List<BaseUnitEntity>();

        /// <summary>
        /// 긴급 자기 힐
        /// </summary>
        public static PlannedAction PlanEmergencyHeal(Situation situation, ref float remainingAP, string roleName,
            float healThresholdOverride = -1f)
        {
            if (situation.AvailableHeals.Count == 0) return null;
            float threshold = healThresholdOverride > 0 ? healThresholdOverride : Settings.SC.EmergencyHealHP;
            if (situation.HPPercent >= threshold) return null;
            if (situation.HasHealedThisTurn) return null;

            // ★ v3.12.2: ScoreHeal 기반 최적 힐 선택 (기존 first-available 대체)
            var selfTarget = new TargetWrapper(situation.Unit);
            AbilityData bestHeal = null;
            float bestScore = float.MinValue;
            float bestCost = 0f;

            for (int i = 0; i < situation.AvailableHeals.Count; i++)
            {
                var heal = situation.AvailableHeals[i];
                float cost = CombatAPI.GetAbilityAPCost(heal);
                if (cost > remainingAP) continue;

                string reason;
                if (!CombatAPI.CanUseAbilityOn(heal, selfTarget, out reason)) continue;

                float score = UtilityScorer.ScoreHeal(heal, situation.Unit, situation);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestHeal = heal;
                    bestCost = cost;
                }
            }

            if (bestHeal == null) return null;

            remainingAP -= bestCost;
            Main.Log($"[{roleName}] Emergency heal: {bestHeal.Name} (score={bestScore:F1}, HP={situation.HPPercent:F0}%)");
            return PlannedAction.Heal(bestHeal, situation.Unit,
                $"Emergency heal (HP={situation.HPPercent:F0}%)", bestCost);
        }

        /// <summary>
        /// 아군 힐
        /// ★ v3.5.10: 힐 예약 시스템 통합 (중복 힐 방지)
        /// </summary>
        public static PlannedAction PlanAllyHeal(Situation situation, BaseUnitEntity ally, ref float remainingAP, string roleName)
        {
            if (situation.AvailableHeals.Count == 0) return null;
            if (ally == null) return null;

            // ★ v3.5.10: 이미 다른 Support가 힐 예약한 대상은 스킵
            if (TeamBlackboard.Instance.IsHealReserved(ally))
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] Heal skip - {ally.CharacterName} already reserved for heal");
                return null;
            }

            // ★ v3.12.2: ScoreHeal 기반 최적 힐 선택 (기존 first-available 대체)
            var targetWrapper = new TargetWrapper(ally);
            AbilityData bestHeal = null;
            float bestScore = float.MinValue;
            float bestCost = 0f;

            for (int i = 0; i < situation.AvailableHeals.Count; i++)
            {
                var heal = situation.AvailableHeals[i];
                float cost = CombatAPI.GetAbilityAPCost(heal);
                if (cost > remainingAP) continue;

                string reason;
                if (!CombatAPI.CanUseAbilityOn(heal, targetWrapper, out reason)) continue;

                float score = UtilityScorer.ScoreHeal(heal, ally, situation);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestHeal = heal;
                    bestCost = cost;
                }
            }

            if (bestHeal == null) return null;

            // ★ v3.5.10: 힐 대상 예약
            TeamBlackboard.Instance.ReserveHeal(ally);

            remainingAP -= bestCost;
            Main.Log($"[{roleName}] Heal ally: {bestHeal.Name} -> {ally.CharacterName} (score={bestScore:F1})");
            return PlannedAction.Heal(bestHeal, ally, $"Heal {ally.CharacterName}", bestCost);
        }

        /// <summary>
        /// 재장전
        /// </summary>
        public static PlannedAction PlanReload(Situation situation, ref float remainingAP, string roleName)
        {
            var reload = situation.ReloadAbility;
            if (reload == null) return null;
            if (!situation.NeedsReload) return null;
            if (situation.HasReloadedThisTurn) return null;

            float cost = CombatAPI.GetAbilityAPCost(reload);
            if (cost > remainingAP) return null;

            var target = new TargetWrapper(situation.Unit);
            string reason;
            if (CombatAPI.CanUseAbilityOn(reload, target, out reason))
            {
                remainingAP -= cost;
                return PlannedAction.Reload(reload, situation.Unit, cost);
            }

            return null;
        }

        /// <summary>
        /// ★ v3.1.21: 부상당한 아군 찾기 - TargetScorer 기반
        /// Role, 위험도 등 복합 요소 고려
        /// ★ v3.5.10: 이미 힐 예약된 아군 제외
        /// </summary>
        public static BaseUnitEntity FindWoundedAlly(Situation situation, float threshold)
        {
            // ★ v3.42.0: Zero-allocation — static 리스트 재사용
            _tempHealTargets.Clear();
            // ★ v3.18.4: CombatantAllies 사용 (사역마 제외)
            for (int i = 0; i < situation.CombatantAllies.Count; i++)
            {
                var a = situation.CombatantAllies[i];
                if (a != null && !a.LifeState.IsDead && !TeamBlackboard.Instance.IsHealReserved(a))
                    _tempHealTargets.Add(a);
            }

            // 본인도 힐 대상에 포함
            if (!_tempHealTargets.Contains(situation.Unit) && !TeamBlackboard.Instance.IsHealReserved(situation.Unit))
                _tempHealTargets.Add(situation.Unit);

            if (_tempHealTargets.Count == 0)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[HealPlanner] No heal targets available (all reserved or healthy)");
                return null;
            }

            // ★ v3.1.21: TargetScorer 사용 (Role 우선순위 + 위험도 고려)
            return TargetScorer.SelectBestAllyForHealing(_tempHealTargets, situation, threshold);
        }

        /// <summary>
        /// ★ v3.42.0: 이동 후 힐 - 힐 사거리 밖의 아군에게 접근하여 힐
        /// SupportPlan에서 공용화 — 전 역할에서 사용 가능
        /// ★ v3.9.46: 최초 구현 (SupportPlan)
        /// ★ v3.9.66: Chebyshev 거리 + LOS 검증 + 조기 반환 제거
        /// ★ v3.12.2: ScoreHeal 기반 최적 힐 선택
        /// ★ v3.18.18: DamagingAoE/HazardZone 회피
        /// </summary>
        public static List<PlannedAction> PlanMoveToHeal(Situation situation, BaseUnitEntity woundedAlly, ref float remainingAP, float remainingMP, string roleName)
        {
            if (remainingMP <= 0) return null;
            if (situation.AvailableHeals.Count == 0) return null;

            var unit = situation.Unit;
            if (unit == null || woundedAlly == null) return null;

            // 최장 사거리 파악 (탐색 범위 최대화)
            int maxHealRange = 0;
            bool hasAffordableHeal = false;

            foreach (var heal in situation.AvailableHeals)
            {
                float cost = CombatAPI.GetAbilityAPCost(heal);
                if (cost > remainingAP) continue;

                hasAffordableHeal = true;
                int range = CombatAPI.GetAbilityRangeInTiles(heal);
                if (range > maxHealRange) maxHealRange = range;
            }

            if (!hasAffordableHeal) return null;

            // 도달 가능한 타일 획득
            var tiles = MovementAPI.FindAllReachableTilesSync(unit, remainingMP);
            if (tiles == null || tiles.Count == 0) return null;

            // 타일 검색 — Chebyshev 거리 + LOS 검증 + ScoreHeal 기반 최적 힐 선택
            Vector3? bestPos = null;
            float bestDist = float.MaxValue;
            AbilityData bestHeal = null;
            float bestHealCost = 0f;
            float bestTileScore = float.MinValue;

            bool avoidHazardZonesHeal = !situation.NeedsAoEEvacuation;

            foreach (var kvp in tiles)
            {
                var cell = kvp.Value;
                if (!cell.IsCanStand) continue;

                var node = kvp.Key as CustomGridNodeBase;
                if (node == null) continue;

                var pos = node.Vector3Position;

                // HazardZone 회피
                if (avoidHazardZonesHeal && CombatAPI.IsPositionInHazardZone(pos, unit))
                    continue;

                // 게임 API 그리드 거리 (Chebyshev + SizeRect)
                float distToAlly = CombatAPI.GetDistanceInTiles(pos, woundedAlly);
                if (distToAlly > maxHealRange) continue;

                // 이 위치에서 최고 점수 힐 탐색
                AbilityData tileHeal = null;
                float tileScore = float.MinValue;
                float tileCost = 0f;

                foreach (var heal in situation.AvailableHeals)
                {
                    float cost = CombatAPI.GetAbilityAPCost(heal);
                    if (cost > remainingAP) continue;

                    int range = CombatAPI.GetAbilityRangeInTiles(heal);
                    if (distToAlly > range) continue;

                    // LOS 검증 — 해당 위치에서 힐 시전 가능 확인
                    if (!CombatAPI.CanReachTargetFromPosition(heal, pos, woundedAlly)) continue;

                    float score = UtilityScorer.ScoreHeal(heal, woundedAlly, situation);
                    if (score > tileScore)
                    {
                        tileScore = score;
                        tileHeal = heal;
                        tileCost = cost;
                    }
                }

                if (tileHeal == null) continue;

                // 가장 가까운 유효 타일 (같은 거리면 점수 최대)
                if (distToAlly < bestDist || (distToAlly == bestDist && tileScore > bestTileScore))
                {
                    bestDist = distToAlly;
                    bestPos = pos;
                    bestHeal = tileHeal;
                    bestHealCost = tileCost;
                    bestTileScore = tileScore;
                }
            }

            if (!bestPos.HasValue)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] MoveToHeal: No reachable tile with LOS within heal range ({maxHealRange}) of {woundedAlly.CharacterName}");
                return null;
            }

            // 힐 대상 예약 (중복 힐 방지)
            TeamBlackboard.Instance.ReserveHeal(woundedAlly);

            // Move + Heal 계획
            remainingAP -= bestHealCost;

            int plannedRange = CombatAPI.GetAbilityRangeInTiles(bestHeal);
            // ★ v3.42.0: Zero-allocation — static 리스트 재사용
            _tempHealActions.Clear();
            _tempHealActions.Add(PlannedAction.Move(bestPos.Value,
                $"Move to heal {woundedAlly.CharacterName} (range={plannedRange} tiles)"));
            _tempHealActions.Add(PlannedAction.Heal(bestHeal, woundedAlly,
                $"Heal after move: {woundedAlly.CharacterName}", bestHealCost));

            Main.Log($"[{roleName}] MoveToHeal: Moving {CombatAPI.MetersToTiles(Vector3.Distance(unit.Position, bestPos.Value)):F1} tiles to heal {woundedAlly.CharacterName} ({bestHeal.Name}, range={plannedRange})");

            return _tempHealActions;
        }

        /// <summary>
        /// ★ v3.42.0: 여유 AP/MP가 있을 때 부상 아군 치유 (이동 포함)
        /// 주 행동(공격 등) 이후 호출 — 낭비 방지를 위해 조건부 실행
        /// 전 역할에서 사용 가능 (DPS/Tank/Support/Overseer)
        /// ⚠️ 반환된 리스트는 static — 즉시 AddRange로 복사할 것
        /// </summary>
        public static List<PlannedAction> PlanOpportunisticAllyHeal(Situation situation, ref float remainingAP, float remainingMP, string roleName)
        {
            if (situation.AvailableHeals.Count == 0) return null;

            // 최소 치유 AP 비용 확인
            float minHealCost = float.MaxValue;
            for (int i = 0; i < situation.AvailableHeals.Count; i++)
            {
                float cost = CombatAPI.GetAbilityAPCost(situation.AvailableHeals[i]);
                if (cost < minHealCost) minHealCost = cost;
            }
            if (remainingAP < minHealCost) return null;

            // 사용자 설정 기반 임계값
            float healThreshold = situation.CharacterSettings?.HealAtHPPercent ?? SC.HealPriorityMid;

            // 부상 아군 탐색
            var woundedAlly = FindWoundedAlly(situation, healThreshold);
            if (woundedAlly == null) return null;

            // 1차: 직접 치유 시도 (사거리 내)
            var directHeal = PlanAllyHeal(situation, woundedAlly, ref remainingAP, roleName);
            if (directHeal != null)
            {
                // ★ v3.42.0: Zero-allocation — static 리스트 재사용
                _tempHealActions.Clear();
                _tempHealActions.Add(directHeal);
                return _tempHealActions;
            }

            // 2차: 이동 후 치유 시도 (사거리 밖) — PlanMoveToHeal도 _tempHealActions 사용
            if (remainingMP > 0)
            {
                return PlanMoveToHeal(situation, woundedAlly, ref remainingAP, remainingMP, roleName);
            }

            return null;
        }

        /// <summary>
        /// 아군 버프 (Support 전용)
        /// ★ v3.1.21: TargetScorer 기반 버프 대상 선택
        /// </summary>
        public static PlannedAction PlanAllyBuff(Situation situation, ref float remainingAP, string roleName)
        {
            foreach (var buff in situation.AvailableBuffs)
            {
                if (buff.Blueprint?.CanTargetFriends != true) continue;
                // ★ v3.40.4: 무기 공격이 아군 버프로 사용되는 것 방지
                if (buff.Weapon != null) continue;

                float cost = CombatAPI.GetAbilityAPCost(buff);
                if (cost > remainingAP) continue;

                // ★ v3.1.21: TargetScorer로 최적 버프 대상 선택
                // 이미 버프가 있는 아군 제외
                // ★ v3.18.4: CombatantAllies 사용 (사역마 제외)
                var candidates = situation.CombatantAllies
                    .Where(a => a != null && !a.LifeState.IsDead)
                    .Where(a => !AllyStateCache.HasBuff(a, buff))
                    .ToList();

                // 본인도 후보에 추가 (버프 없으면)
                if (!AllyStateCache.HasBuff(situation.Unit, buff) && !candidates.Contains(situation.Unit))
                    candidates.Add(situation.Unit);

                var bestTarget = TargetScorer.SelectBestAllyForBuff(candidates, situation);
                if (bestTarget == null) continue;

                var targetWrapper = new TargetWrapper(bestTarget);
                string reason;
                if (CombatAPI.CanUseAbilityOn(buff, targetWrapper, out reason))
                {
                    remainingAP -= cost;
                    Main.Log($"[{roleName}] Buff ally: {buff.Name} -> {bestTarget.CharacterName}");
                    return PlannedAction.Buff(buff, bestTarget, $"Buff {bestTarget.CharacterName}", cost);
                }
            }

            return null;
        }
    }
}
