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

            // ★ v3.8.20: AllyTarget 도발 (FightMe 등) 지원
            public BaseUnitEntity TargetAlly { get; set; }     // 아군 타겟 도발 시 보호 대상 아군
            public bool IsAllyTargetTaunt { get; set; }        // 아군 타겟 도발 여부
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

                // ★ v3.8.20: AllyTarget 도발 감지 (FightMe 등)
                // CanTargetFriends=true, CanTargetEnemies=false, CanTargetSelf=false
                bool isAllyTargetTaunt = taunt.Blueprint?.CanTargetFriends == true &&
                                         taunt.Blueprint?.CanTargetEnemies == false &&
                                         taunt.Blueprint?.CanTargetSelf == false;

                if (isAllyTargetTaunt)
                {
                    // ★ v3.8.20: AllyTarget 도발은 별도 평가 (아군 보호 기반)
                    var allyTauntOption = EvaluateAllyTargetTaunt(situation, taunt);
                    if (allyTauntOption != null)
                    {
                        options.Add(allyTauntOption);
                        Main.LogDebug($"[TauntScorer] AllyTarget taunt {taunt.Name}: " +
                            $"protectAlly={allyTauntOption.TargetAlly?.CharacterName}, " +
                            $"enemies={allyTauntOption.EnemiesAffected}, score={allyTauntOption.Score:F0}");
                    }
                    continue;  // AllyTarget은 위치 이동 평가 스킵
                }

                // ★ v3.1.26: 패턴 정보 완전 조회
                var patternInfo = CombatAPI.GetPatternInfo(taunt);
                bool isAoE = CombatAPI.IsPointTargetAbility(taunt);
                // ★ v3.5.98: 타일 단위 사용
                float tauntRange = CombatAPI.GetAbilityRangeInTiles(taunt);
                float aoERadius = patternInfo?.Radius ?? (isAoE ? CombatAPI.GetAoERadius(taunt) : 0f);  // 타일

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
                // ★ v3.6.2: AOE/Self 도발 - 타일 단위로 통일 (기본 4타일 ≈ 5.4m)
                float effectiveRadiusTiles = aoERadius > 0 ? aoERadius : 4f;

                // ★ v3.6.24: AOE 중심점 계산 - TargetPoint 기준으로 거리 계산해야 함!
                // isTouchRange인 경우 AOE 중심은 캐스터에서 1.5m 오프셋된 위치
                // isSelfTarget인 경우 AOE 중심은 캐스터 위치
                Vector3 aoeCenterForPrediction = position;
                if (!isSelfTarget && isTouchRange && enemies.Count > 0)
                {
                    // 적들의 중심점 방향으로 1.5m 오프셋 (실제 TargetPoint 계산 로직과 동일)
                    Vector3 sum = Vector3.zero;
                    int validCount = 0;
                    foreach (var e in enemies)
                    {
                        if (e != null && e.IsConscious)
                        {
                            sum += e.Position;
                            validCount++;
                        }
                    }
                    if (validCount > 0)
                    {
                        Vector3 centroid = sum / validCount;
                        Vector3 toEnemies = centroid - position;
                        if (toEnemies.magnitude > 0.1f)
                        {
                            aoeCenterForPrediction = position + toEnemies.normalized * 1.5f;
                        }
                    }
                }

                Main.LogDebug($"[TauntScorer] AOE center for prediction: ({aoeCenterForPrediction.x:F1}, {aoeCenterForPrediction.z:F1}), radius={effectiveRadiusTiles:F1} tiles");

                foreach (var enemy in enemies)
                {
                    if (enemy == null || !enemy.IsConscious) continue;
                    // ★ v3.6.24: AOE 중심점에서 거리 계산 (캐스터 위치가 아님!)
                    float distTiles = CombatAPI.MetersToTiles(Vector3.Distance(aoeCenterForPrediction, enemy.Position));
                    if (distTiles <= effectiveRadiusTiles)
                    {
                        affectedEnemies.Add(enemy);
                        if (enemiesTargetingAllies.Contains(enemy))
                            targetingAlliesCount++;
                        Main.LogDebug($"[TauntScorer] Enemy in range: {enemy.CharacterName} at {distTiles:F1} tiles");
                    }
                }
            }
            else
            {
                // 단일 타겟 도발: 범위 내 아군 타겟팅 적 우선
                BaseUnitEntity target = null;

                // ★ v3.5.98: 1순위: 아군 타겟팅 중인 적 (타일 단위)
                target = enemiesTargetingAllies
                    .Where(e => e != null && e.IsConscious)
                    .Where(e => CombatAPI.MetersToTiles(Vector3.Distance(position, e.Position)) <= tauntRange)
                    .OrderBy(e => Vector3.Distance(position, e.Position))
                    .FirstOrDefault();

                // ★ v3.5.98: 2순위: 가장 가까운 적 (타일 단위)
                if (target == null)
                {
                    target = enemies
                        .Where(e => e != null && e.IsConscious)
                        .Where(e => CombatAPI.MetersToTiles(Vector3.Distance(position, e.Position)) <= tauntRange)
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

            // ★ v3.6.12: TargetPoint 계산 수정
            // - isSelfTarget=true: 캐스터 위치를 타겟으로
            // - !isSelfTarget: CannotTargetSelf 회피를 위해 적절한 오프셋 적용
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

                // ★ v3.6.12: 정규화 전에 방향 유효성 체크
                Vector3 toEnemies = centroid - position;
                float distToCentroid = toEnemies.magnitude;

                // ★ v3.6.12: CannotTargetSelf 방지 - 최소 1.5m 오프셋 보장
                const float MIN_OFFSET = 1.5f;  // CannotTargetSelf 회피를 위한 최소 거리

                if (distToCentroid > 0.1f)  // 유효한 방향이 있음
                {
                    Vector3 direction = toEnemies / distToCentroid;  // 정규화

                    if (isTouchRange || distToCentroid < MIN_OFFSET)
                    {
                        // ★ v3.6.12: Touch 범위이거나 centroid가 너무 가까우면 오프셋 적용
                        targetPoint = position + direction * MIN_OFFSET;
                        Main.LogDebug($"[TauntScorer] TargetPoint: offset ({targetPoint.x:F1}, {targetPoint.z:F1}) - {MIN_OFFSET}m towards enemies");
                    }
                    else
                    {
                        // centroid가 충분히 멀면 그대로 사용
                        targetPoint = centroid;
                        Main.LogDebug($"[TauntScorer] TargetPoint: enemy centroid ({targetPoint.x:F1}, {targetPoint.z:F1})");
                    }
                }
                else
                {
                    // ★ v3.6.12: 방향이 없으면 (캐스터가 적 중심에 있음) 가장 가까운 적 방향으로 오프셋
                    var nearestEnemy = affectedEnemies.OrderBy(e => Vector3.Distance(position, e.Position)).First();
                    Vector3 toNearest = nearestEnemy.Position - position;
                    if (toNearest.sqrMagnitude > 0.01f)
                    {
                        targetPoint = position + toNearest.normalized * MIN_OFFSET;
                    }
                    else
                    {
                        // 완전히 겹쳐있으면 임의 방향
                        targetPoint = position + Vector3.forward * MIN_OFFSET;
                    }
                    Main.LogDebug($"[TauntScorer] TargetPoint: fallback offset ({targetPoint.x:F1}, {targetPoint.z:F1})");
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

        #region AllyTarget Taunt Evaluation

        /// <summary>
        /// ★ v3.8.20: AllyTarget 도발 평가 (FightMe 등)
        /// 아군 주변 적을 도발하는 능력 - 보호가 필요한 아군을 찾아 타겟팅
        /// </summary>
        private static TauntOption EvaluateAllyTargetTaunt(Situation situation, AbilityData taunt)
        {
            if (situation.Allies == null || situation.Allies.Count == 0)
                return null;

            var tank = situation.Unit;
            float tauntRange = CombatAPI.GetAbilityRangeInTiles(taunt);

            // 패턴 정보에서 AOE 반경 추출
            var patternInfo = CombatAPI.GetPatternInfo(taunt);
            float aoERadius = patternInfo?.Radius ?? 3f;  // 기본 3타일

            BaseUnitEntity bestAlly = null;
            float bestScore = 0f;
            int bestEnemyCount = 0;
            int bestTargetingAlliesCount = 0;
            var bestAffectedEnemies = new List<BaseUnitEntity>();

            foreach (var ally in situation.Allies)
            {
                // 자기 자신 제외 (CanTargetSelf=false)
                if (ally == tank) continue;
                if (ally == null || !ally.IsConscious) continue;

                // 범위 체크 (Tank에서 아군까지 거리)
                float distToAllyTiles = CombatAPI.MetersToTiles(Vector3.Distance(tank.Position, ally.Position));
                if (distToAllyTiles > tauntRange)
                {
                    Main.LogDebug($"[TauntScorer] AllyTarget: {ally.CharacterName} out of range ({distToAllyTiles:F1} > {tauntRange:F1} tiles)");
                    continue;
                }

                // 아군 주변 적 계산 (AOE 반경 내)
                var nearbyEnemies = new List<BaseUnitEntity>();
                int targetingAlliesCount = 0;

                foreach (var enemy in situation.Enemies)
                {
                    if (enemy == null || !enemy.IsConscious) continue;

                    float distToAlly = CombatAPI.MetersToTiles(Vector3.Distance(ally.Position, enemy.Position));
                    if (distToAlly <= aoERadius)
                    {
                        nearbyEnemies.Add(enemy);

                        // 이 적이 아군(ally 포함)을 타겟팅 중인지 확인
                        if (situation.Allies.Any(a => a != null && CombatAPI.IsTargeting(enemy, a)))
                            targetingAlliesCount++;
                    }
                }

                if (nearbyEnemies.Count == 0)
                {
                    Main.LogDebug($"[TauntScorer] AllyTarget: {ally.CharacterName} has no nearby enemies");
                    continue;
                }

                // 점수 계산
                // - 아군 타겟팅 적 수 × 100
                // - 일반 적 수 × 30
                // - HP 낮은 아군 보너스 (최대 50점)
                float score = targetingAlliesCount * WEIGHT_ENEMY_TARGETING_ALLY;
                score += (nearbyEnemies.Count - targetingAlliesCount) * WEIGHT_ENEMY_HIT;
                score += (1f - CombatAPI.GetHPPercent(ally)) * 50f;  // HP 0%면 50점 추가

                Main.LogDebug($"[TauntScorer] AllyTarget: {ally.CharacterName} - " +
                    $"enemies={nearbyEnemies.Count}, targetingAllies={targetingAlliesCount}, " +
                    $"HP={CombatAPI.GetHPPercent(ally):P0}, score={score:F0}");

                if (score > bestScore)
                {
                    bestScore = score;
                    bestAlly = ally;
                    bestEnemyCount = nearbyEnemies.Count;
                    bestTargetingAlliesCount = targetingAlliesCount;
                    bestAffectedEnemies = nearbyEnemies;
                }
            }

            if (bestAlly == null)
                return null;

            // AP 체크
            float apCost = CombatAPI.GetAbilityAPCost(taunt);
            if (apCost > situation.CurrentAP)
                return null;

            // 사용 가능 여부 확인
            var target = new TargetWrapper(bestAlly);
            string reason;
            if (!CombatAPI.CanUseAbilityOn(taunt, target, out reason))
            {
                Main.LogDebug($"[TauntScorer] AllyTarget: Cannot use {taunt.Name} on {bestAlly.CharacterName} - {reason}");
                return null;
            }

            return new TauntOption
            {
                Ability = taunt,
                Position = tank.Position,
                TargetPoint = bestAlly.Position,  // 아군 위치
                RequiresMove = false,
                MoveCost = 0f,
                EnemiesAffected = bestEnemyCount,
                EnemiesTargetingAllies = bestTargetingAlliesCount,
                Score = bestScore,
                AffectedEnemies = bestAffectedEnemies,
                TargetAlly = bestAlly,
                IsAllyTargetTaunt = true
            };
        }

        #endregion
    }
}
