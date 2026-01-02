using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using Kingmaker.Pathfinding;
using UnityEngine;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Data;

namespace CompanionAI_v3.Analysis
{
    /// <summary>
    /// ★ v3.1.25: 스마트 도발 스코어링 시스템
    /// - 아군 타겟팅 적 탐지
    /// - 이동 후 도발 타당성 평가
    /// - AOE 도발 범위 계산
    /// </summary>
    public static class TauntScorer
    {
        #region Taunt Option

        /// <summary>
        /// 도발 옵션 (현재 위치 또는 이동 후 도발)
        /// </summary>
        public class TauntOption
        {
            public AbilityData Ability { get; set; }
            public Vector3 Position { get; set; }              // 캐스터 이동 위치
            public Vector3 TargetPoint { get; set; }           // ★ v3.1.26: 실제 시전 타겟 위치 (적 중심점)
            public bool RequiresMove { get; set; }
            public float MoveCost { get; set; }
            public int EnemiesAffected { get; set; }
            public int EnemiesTargetingAllies { get; set; }
            public float Score { get; set; }
            public List<BaseUnitEntity> AffectedEnemies { get; set; } = new List<BaseUnitEntity>();
        }

        #endregion

        #region Scoring Weights

        // 스코어링 가중치
        private const float WEIGHT_ENEMY_TARGETING_ALLY = 100f;  // 아군 타겟팅 적 도발 (최우선)
        private const float WEIGHT_ENEMY_HIT = 30f;              // 일반 적 도발
        private const float WEIGHT_MOVE_PENALTY = -10f;          // 이동 비용 (MP당)
        private const float WEIGHT_DISTANCE_PENALTY = -2f;       // 거리 비용 (m당)

        #endregion

        #region Main API

        /// <summary>
        /// 모든 도발 옵션 평가 (현재 위치 + 이동 가능 위치)
        /// </summary>
        public static List<TauntOption> EvaluateAllTauntOptions(
            Situation situation,
            List<AbilityData> tauntAbilities,
            float availableMP)
        {
            var options = new List<TauntOption>();
            if (situation?.Unit == null || tauntAbilities == null || tauntAbilities.Count == 0)
                return options;

            var tank = situation.Unit;

            // 아군 타겟팅 중인 적 목록
            var enemiesTargetingAllies = CombatAPI.GetEnemiesTargetingAllies(
                tank, situation.Allies, situation.Enemies);

            foreach (var taunt in tauntAbilities)
            {
                if (taunt == null) continue;

                // ★ v3.1.26: 패턴 정보 완전 조회
                var patternInfo = CombatAPI.GetPatternInfo(taunt);
                bool isAoE = CombatAPI.IsPointTargetAbility(taunt);
                float tauntRange = CombatAPI.GetAbilityRange(taunt);
                float aoERadius = patternInfo?.Radius ?? (isAoE ? CombatAPI.GetAoERadius(taunt) : 0f);

                // 자기 타겟 도발인 경우 (AOE 효과가 자기 중심)
                bool isSelfTarget = taunt.Blueprint?.CanTargetSelf == true;

                // ★ v3.1.26: Range=Touch 확인 (캐스터 인접 위치만 타겟 가능)
                bool isTouchRange = taunt.Blueprint?.Range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Touch;

                // ★ v3.1.26: 패턴 타입 로깅
                Main.LogDebug($"[TauntScorer] {taunt.Name}: isAoE={isAoE}, radius={aoERadius:F1}, " +
                    $"isSelfTarget={isSelfTarget}, isTouchRange={isTouchRange}, pattern={patternInfo?.Type}");

                // 옵션 1: 현재 위치에서 도발
                var currentOption = EvaluateTauntFromPosition(
                    tank, tank.Position, taunt, isAoE, isSelfTarget, isTouchRange, aoERadius, tauntRange,
                    situation.Enemies, enemiesTargetingAllies, requiresMove: false, moveCost: 0f);
                if (currentOption != null)
                    options.Add(currentOption);

                // 옵션 2: 이동 후 도발 (MP가 있는 경우)
                if (availableMP > 0 && isAoE)  // AOE 도발만 이동 고려 (단일 타겟은 현재 위치에서)
                {
                    var moveOptions = EvaluateTauntWithMovement(
                        tank, taunt, isAoE, isSelfTarget, isTouchRange, aoERadius, tauntRange,
                        situation.Enemies, enemiesTargetingAllies, availableMP);
                    options.AddRange(moveOptions);
                }
            }

            // 점수 순으로 정렬
            return options.OrderByDescending(o => o.Score).ToList();
        }

        /// <summary>
        /// 도발이 가치 있는지 판단 (최소 임계값)
        /// </summary>
        public static bool IsTauntWorthwhile(TauntOption option)
        {
            if (option == null) return false;

            // 아군 타겟팅 적이 있으면 무조건 가치 있음
            if (option.EnemiesTargetingAllies > 0) return true;

            // 이동 필요 없이 2명 이상 도발 가능하면 가치 있음
            if (!option.RequiresMove && option.EnemiesAffected >= 2) return true;

            // 이동해서 3명 이상 도발 가능하면 가치 있음
            if (option.RequiresMove && option.EnemiesAffected >= 3) return true;

            // 이동 없이 1명이라도 도발 가능하고 점수 양수면 가치 있음
            if (!option.RequiresMove && option.EnemiesAffected >= 1 && option.Score > 0) return true;

            return false;
        }

        #endregion

        #region Position Evaluation

        /// <summary>
        /// 특정 위치에서 도발 평가
        /// </summary>
        private static TauntOption EvaluateTauntFromPosition(
            BaseUnitEntity tank,
            Vector3 position,
            AbilityData taunt,
            bool isAoE,
            bool isSelfTarget,
            bool isTouchRange,  // ★ v3.1.27: Touch 범위 여부
            float aoERadius,
            float tauntRange,
            List<BaseUnitEntity> enemies,
            List<BaseUnitEntity> enemiesTargetingAllies,
            bool requiresMove,
            float moveCost)
        {
            var affectedEnemies = new List<BaseUnitEntity>();
            int targetingAlliesCount = 0;

            if (isAoE || isSelfTarget)
            {
                // AOE/Self 도발: 반경 내 모든 적
                float effectiveRadius = aoERadius > 0 ? aoERadius : 5f;  // 기본 5m

                foreach (var enemy in enemies)
                {
                    if (enemy == null || !enemy.IsConscious) continue;
                    float dist = Vector3.Distance(position, enemy.Position);
                    if (dist <= effectiveRadius)
                    {
                        affectedEnemies.Add(enemy);
                        if (enemiesTargetingAllies.Contains(enemy))
                            targetingAlliesCount++;
                    }
                }
            }
            else
            {
                // 단일 타겟 도발: 범위 내 아군 타겟팅 적 우선
                BaseUnitEntity target = null;

                // 1순위: 아군 타겟팅 중인 적
                target = enemiesTargetingAllies
                    .Where(e => e != null && e.IsConscious)
                    .Where(e => Vector3.Distance(position, e.Position) <= tauntRange)
                    .OrderBy(e => Vector3.Distance(position, e.Position))
                    .FirstOrDefault();

                // 2순위: 가장 가까운 적
                if (target == null)
                {
                    target = enemies
                        .Where(e => e != null && e.IsConscious)
                        .Where(e => Vector3.Distance(position, e.Position) <= tauntRange)
                        .OrderBy(e => Vector3.Distance(position, e.Position))
                        .FirstOrDefault();
                }

                if (target != null)
                {
                    affectedEnemies.Add(target);
                    if (enemiesTargetingAllies.Contains(target))
                        targetingAlliesCount = 1;
                }
            }

            if (affectedEnemies.Count == 0)
                return null;

            // 점수 계산
            float score = 0f;
            score += targetingAlliesCount * WEIGHT_ENEMY_TARGETING_ALLY;
            score += (affectedEnemies.Count - targetingAlliesCount) * WEIGHT_ENEMY_HIT;
            score += moveCost * WEIGHT_MOVE_PENALTY;

            if (requiresMove)
            {
                float moveDistance = Vector3.Distance(tank.Position, position);
                score += moveDistance * WEIGHT_DISTANCE_PENALTY;
            }

            // ★ v3.1.27: TargetPoint 계산
            // - isSelfTarget=true: 캐스터 위치를 타겟으로 (기존 동작)
            // - isTouchRange + !isSelfTarget: 적 방향 1.5m 오프셋 (터치 범위 내, CannotTargetSelf 회피)
            // - !isTouchRange + !isSelfTarget: 적 중심점을 타겟으로
            Vector3 targetPoint = position;  // 기본값: 캐스터 위치
            if (!isSelfTarget && affectedEnemies.Count > 0)
            {
                // 영향받는 적들의 중심점 계산
                Vector3 sum = Vector3.zero;
                foreach (var enemy in affectedEnemies)
                {
                    sum += enemy.Position;
                }
                Vector3 centroid = sum / affectedEnemies.Count;

                if (isTouchRange)
                {
                    // ★ v3.1.27: Range=Touch + CanTargetSelf=False
                    // 적 중심점 방향으로 1.5m 오프셋 (터치 범위 ~2.3m 내)
                    Vector3 direction = (centroid - position).normalized;
                    if (direction.sqrMagnitude > 0.01f)  // 방향 유효성 체크
                    {
                        targetPoint = position + direction * 1.5f;
                    }
                    else
                    {
                        // 방향이 없으면 임의 방향으로 오프셋
                        targetPoint = position + Vector3.forward * 1.5f;
                    }
                    Main.LogDebug($"[TauntScorer] TargetPoint: touch offset ({targetPoint.x:F1}, {targetPoint.z:F1}) - 1.5m towards enemies");
                }
                else
                {
                    // 기존: 적 중심점 사용
                    targetPoint = centroid;
                    Main.LogDebug($"[TauntScorer] TargetPoint: enemy centroid ({targetPoint.x:F1}, {targetPoint.z:F1})");
                }
            }

            return new TauntOption
            {
                Ability = taunt,
                Position = position,
                TargetPoint = targetPoint,  // ★ v3.1.26: 실제 시전 위치
                RequiresMove = requiresMove,
                MoveCost = moveCost,
                EnemiesAffected = affectedEnemies.Count,
                EnemiesTargetingAllies = targetingAlliesCount,
                Score = score,
                AffectedEnemies = affectedEnemies
            };
        }

        /// <summary>
        /// 이동 후 도발 옵션 평가 (이동 가능한 모든 타일)
        /// </summary>
        private static List<TauntOption> EvaluateTauntWithMovement(
            BaseUnitEntity tank,
            AbilityData taunt,
            bool isAoE,
            bool isSelfTarget,
            bool isTouchRange,  // ★ v3.1.27: Touch 범위 여부
            float aoERadius,
            float tauntRange,
            List<BaseUnitEntity> enemies,
            List<BaseUnitEntity> enemiesTargetingAllies,
            float availableMP)
        {
            var options = new List<TauntOption>();

            try
            {
                // 이동 가능한 모든 타일 조회
                var reachableTiles = MovementAPI.FindAllReachableTilesSync(tank, availableMP);
                if (reachableTiles == null || reachableTiles.Count == 0)
                    return options;

                // 샘플링: 너무 많으면 간격으로 샘플링 (성능 최적화)
                var tileList = reachableTiles.ToList();
                int sampleInterval = tileList.Count > 50 ? Math.Max(1, tileList.Count / 30) : 1;

                for (int i = 0; i < tileList.Count; i += sampleInterval)
                {
                    var kvp = tileList[i];
                    var node = kvp.Key as CustomGridNodeBase;
                    var cell = kvp.Value;
                    if (node == null || !cell.IsCanStand) continue;

                    Vector3 tilePosition = node.Vector3Position;
                    float moveCost = cell.Length;  // 이 타일까지 이동 비용

                    var option = EvaluateTauntFromPosition(
                        tank, tilePosition, taunt, isAoE, isSelfTarget, isTouchRange, aoERadius, tauntRange,
                        enemies, enemiesTargetingAllies, requiresMove: true, moveCost: moveCost);

                    if (option != null && option.Score > 0)
                        options.Add(option);
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[TauntScorer] Error evaluating movement options: {ex.Message}");
            }

            return options;
        }

        #endregion
    }
}
