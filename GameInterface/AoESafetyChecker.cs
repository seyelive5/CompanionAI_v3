using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using UnityEngine;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// ★ v3.1.16: AOE 안전성 검증 - 게임 AI 로직 기반
    /// </summary>
    public static class AoESafetyChecker
    {
        /// <summary>
        /// AOE 위치 평가 결과
        /// </summary>
        public class AoEScore
        {
            public Vector3 Position { get; set; }
            public float Score { get; set; }
            public int EnemiesHit { get; set; }
            public int AlliesHit { get; set; }
            public bool IsSafe { get; set; }
            public List<BaseUnitEntity> AffectedUnits { get; set; } = new List<BaseUnitEntity>();
        }

        /// <summary>
        /// AOE 위치의 안전성과 효율성 평가
        /// 게임의 AOETargetSelector 스코어링 로직 기반
        /// </summary>
        public static AoEScore EvaluateAoEPosition(
            AbilityData ability,
            BaseUnitEntity caster,
            Vector3 targetPosition,
            List<BaseUnitEntity> allUnits)
        {
            var score = new AoEScore { Position = targetPosition, IsSafe = true };

            float aoERadius = CombatAPI.GetAoERadius(ability);
            if (aoERadius <= 0) aoERadius = 3f;

            // 게임 스코어링 로직 재현
            const float HIT_SCORE = 10000f;
            const float PENALTY_MULTIPLIER = 1.0f;

            float totalScore = 0f;

            foreach (var unit in allUnits)
            {
                if (unit == null || !unit.IsConscious) continue;

                float dist = Vector3.Distance(targetPosition, unit.Position);
                if (dist > aoERadius) continue;

                score.AffectedUnits.Add(unit);
                float distanceBonus = HIT_SCORE - dist * dist;

                try
                {
                    if (caster.CombatGroup.IsEnemy(unit))
                    {
                        // 적: +점수
                        score.EnemiesHit++;
                        totalScore += distanceBonus;
                    }
                    else if (caster.CombatGroup.IsAlly(unit))
                    {
                        // 아군 체크
                        score.AlliesHit++;

                        // ★ v3.1.20: 플레이어 파티 아군 - 유연성 추가
                        if (!caster.IsPlayerEnemy && unit.IsInPlayerParty)
                        {
                            // 아군 2명 이상이면 절대 거부
                            if (score.AlliesHit >= 2)
                            {
                                score.IsSafe = false;
                                score.Score = float.MinValue;
                                return score;
                            }

                            // 아군 1명: 높은 페널티 (적 3명 맞춰야 상쇄)
                            totalScore -= 3f * HIT_SCORE;  // -30000
                            CompanionAI_v3.Main.LogDebug($"[AOE] Player party ally in range: {unit.CharacterName} - heavy penalty applied");
                            continue;  // NPC 페널티 중복 적용 방지
                        }

                        // NPC 아군: 페널티
                        totalScore -= PENALTY_MULTIPLIER * HIT_SCORE;
                    }
                    else if (unit == caster)
                    {
                        // 캐스터 자신: 2배 페널티
                        totalScore -= 2f * PENALTY_MULTIPLIER * HIT_SCORE;
                        score.AlliesHit++;
                    }
                }
                catch
                {
                    // 피아 구분 실패 시 적으로 간주
                    score.EnemiesHit++;
                    totalScore += distanceBonus;
                }
            }

            score.Score = totalScore;
            // 최소 2명 이상 적 커버, 점수 양수일 때만 안전
            score.IsSafe = score.IsSafe && totalScore > 0 && score.EnemiesHit >= 2;

            return score;
        }

        /// <summary>
        /// 최적의 AOE 시전 위치 찾기
        /// </summary>
        public static AoEScore FindBestAoEPosition(
            AbilityData ability,
            BaseUnitEntity caster,
            List<BaseUnitEntity> enemies,
            List<BaseUnitEntity> allies,
            int minEnemiesRequired = 2)
        {
            if (enemies == null || enemies.Count < minEnemiesRequired)
                return null;

            var allUnits = new List<BaseUnitEntity>();
            allUnits.AddRange(enemies.Where(e => e != null));
            if (allies != null) allUnits.AddRange(allies.Where(a => a != null));
            allUnits.Add(caster);

            float aoERadius = CombatAPI.GetAoERadius(ability);
            float abilityRange = CombatAPI.GetAbilityRange(ability);

            var candidates = new List<AoEScore>();

            // 전략 1: 각 적 위치 중심
            foreach (var enemy in enemies)
            {
                if (enemy == null || !enemy.IsConscious) continue;

                float distToCaster = Vector3.Distance(caster.Position, enemy.Position);
                if (distToCaster > abilityRange) continue;

                var score = EvaluateAoEPosition(ability, caster, enemy.Position, allUnits);
                if (score.IsSafe && score.EnemiesHit >= minEnemiesRequired)
                    candidates.Add(score);
            }

            // 전략 2: 적 2명 중간점
            for (int i = 0; i < enemies.Count; i++)
            {
                for (int j = i + 1; j < enemies.Count; j++)
                {
                    var e1 = enemies[i];
                    var e2 = enemies[j];
                    if (e1 == null || e2 == null || !e1.IsConscious || !e2.IsConscious) continue;

                    Vector3 center = (e1.Position + e2.Position) / 2f;
                    float distToCaster = Vector3.Distance(caster.Position, center);
                    if (distToCaster > abilityRange) continue;

                    var score = EvaluateAoEPosition(ability, caster, center, allUnits);
                    if (score.IsSafe && score.EnemiesHit >= minEnemiesRequired)
                        candidates.Add(score);
                }
            }

            // 최고 점수 선택
            return candidates
                .OrderByDescending(c => c.Score)
                .FirstOrDefault();
        }

        /// <summary>
        /// ★ v3.1.20: 간단한 AOE 안전성 체크 (수정)
        /// 아군 1명 피격 = 허용 (페널티로 처리)
        /// 아군 2명 이상 피격 = 절대 거부
        /// </summary>
        public static bool IsAoESafe(
            AbilityData ability,
            BaseUnitEntity caster,
            Vector3 targetPosition,
            List<BaseUnitEntity> allies)
        {
            float aoERadius = CombatAPI.GetAoERadius(ability);
            if (aoERadius <= 0) return true;

            if (allies == null) return true;

            int playerPartyAlliesInRange = 0;

            foreach (var ally in allies)
            {
                if (ally == null || !ally.IsConscious) continue;

                float dist = Vector3.Distance(targetPosition, ally.Position);
                if (dist <= aoERadius)
                {
                    try
                    {
                        if (!caster.IsPlayerEnemy && ally.IsInPlayerParty)
                        {
                            playerPartyAlliesInRange++;

                            // ★ v3.1.20: 2명 이상이면 거부
                            if (playerPartyAlliesInRange >= 2)
                                return false;
                        }
                    }
                    catch { }
                }
            }

            // 0~1명은 허용 (EvaluateAoEPosition에서 페널티로 처리)
            return true;
        }

        #region Ally-Targeting AOE (v3.1.17)

        /// <summary>
        /// 아군 타겟 AOE 위치 평가 (버프/힐용)
        /// 적 타겟 AOE와 반대로 아군이 많을수록 높은 점수
        /// </summary>
        public static AoEScore EvaluateAllyAoEPosition(
            AbilityData ability,
            BaseUnitEntity caster,
            Vector3 targetPosition,
            List<BaseUnitEntity> allies,
            bool requiresWounded = false)
        {
            var score = new AoEScore { Position = targetPosition, IsSafe = true };

            float aoERadius = CombatAPI.GetAoERadius(ability);
            if (aoERadius <= 0) aoERadius = 3f;

            const float HIT_SCORE = 10000f;
            float totalScore = 0f;

            foreach (var unit in allies)
            {
                if (unit == null || !unit.IsConscious) continue;

                // 힐 AOE: 부상 아군만 카운트
                if (requiresWounded)
                {
                    float hpPercent = CombatAPI.GetHPPercent(unit);
                    if (hpPercent >= 90f) continue;  // 거의 풀피면 스킵
                }

                float dist = Vector3.Distance(targetPosition, unit.Position);
                if (dist > aoERadius) continue;

                score.AffectedUnits.Add(unit);
                score.AlliesHit++;

                // 거리가 가까울수록 높은 점수
                float distanceBonus = HIT_SCORE - dist * dist;

                // 힐 AOE: HP가 낮을수록 보너스
                if (requiresWounded)
                {
                    float hpPercent = CombatAPI.GetHPPercent(unit);
                    float hpBonus = (100f - hpPercent) * 100f;  // HP 50% = +5000
                    distanceBonus += hpBonus;
                }

                totalScore += distanceBonus;
            }

            score.Score = totalScore;
            // 최소 2명 이상 커버해야 의미 있음
            score.IsSafe = score.AlliesHit >= 2;

            return score;
        }

        /// <summary>
        /// 최적의 아군 AOE 시전 위치 찾기 (버프/힐용)
        /// </summary>
        public static AoEScore FindBestAllyAoEPosition(
            AbilityData ability,
            BaseUnitEntity caster,
            List<BaseUnitEntity> allies,
            int minAlliesRequired = 2,
            bool requiresWounded = false)
        {
            if (allies == null || allies.Count < minAlliesRequired)
                return null;

            float aoERadius = CombatAPI.GetAoERadius(ability);
            float abilityRange = CombatAPI.GetAbilityRange(ability);

            var candidates = new List<AoEScore>();

            // 전략 1: 각 아군 위치 중심
            foreach (var ally in allies)
            {
                if (ally == null || !ally.IsConscious) continue;

                // 힐: 풀피 아군은 스킵
                if (requiresWounded)
                {
                    float hpPercent = CombatAPI.GetHPPercent(ally);
                    if (hpPercent >= 90f) continue;
                }

                float distToCaster = Vector3.Distance(caster.Position, ally.Position);
                if (distToCaster > abilityRange) continue;

                var evalScore = EvaluateAllyAoEPosition(ability, caster, ally.Position, allies, requiresWounded);
                if (evalScore.IsSafe && evalScore.AlliesHit >= minAlliesRequired)
                    candidates.Add(evalScore);
            }

            // 전략 2: 아군 2명 중간점
            for (int i = 0; i < allies.Count; i++)
            {
                for (int j = i + 1; j < allies.Count; j++)
                {
                    var a1 = allies[i];
                    var a2 = allies[j];
                    if (a1 == null || a2 == null || !a1.IsConscious || !a2.IsConscious) continue;

                    Vector3 center = (a1.Position + a2.Position) / 2f;
                    float distToCaster = Vector3.Distance(caster.Position, center);
                    if (distToCaster > abilityRange) continue;

                    var evalScore = EvaluateAllyAoEPosition(ability, caster, center, allies, requiresWounded);
                    if (evalScore.IsSafe && evalScore.AlliesHit >= minAlliesRequired)
                        candidates.Add(evalScore);
                }
            }

            // 최고 점수 선택
            return candidates
                .OrderByDescending(c => c.Score)
                .FirstOrDefault();
        }

        #endregion

        #region Directional Pattern AOE (v3.1.18)

        /// <summary>
        /// ★ v3.1.18: 방향성 AOE(Cone/Ray/Sector)의 최적 타겟 찾기
        /// Circle 패턴과 달리 방향이 중요하므로 각 적을 향한 방향별로 평가
        /// </summary>
        public static AoEScore FindBestDirectionalAoETarget(
            Kingmaker.UnitLogic.Abilities.AbilityData ability,
            BaseUnitEntity caster,
            System.Collections.Generic.List<BaseUnitEntity> enemies,
            System.Collections.Generic.List<BaseUnitEntity> allies,
            int minEnemiesRequired = 2)
        {
            if (enemies == null || enemies.Count < minEnemiesRequired)
                return null;

            var patternType = CombatAPI.GetPatternType(ability);
            if (!patternType.HasValue) return null;

            float radius = CombatAPI.GetAoERadius(ability);
            float abilityRange = CombatAPI.GetAbilityRange(ability);
            float angle = CombatAPI.GetPatternAngle(ability);

            var candidates = new System.Collections.Generic.List<AoEScore>();

            // 각 적을 주 타겟으로 평가 (방향 결정)
            foreach (var primaryTarget in enemies)
            {
                if (primaryTarget == null || !primaryTarget.IsConscious) continue;

                float distToCaster = Vector3.Distance(caster.Position, primaryTarget.Position);
                if (distToCaster > abilityRange) continue;

                // 이 적을 향한 방향으로 패턴 시전 시 영향받는 유닛 계산
                Vector3 direction = (primaryTarget.Position - caster.Position).normalized;

                var score = EvaluateDirectionalAoE(
                    ability, caster, direction, primaryTarget,
                    enemies, allies, patternType.Value, radius, angle);

                if (score.IsSafe && score.EnemiesHit >= minEnemiesRequired)
                    candidates.Add(score);
            }

            return candidates.OrderByDescending(c => c.Score).FirstOrDefault();
        }

        /// <summary>
        /// ★ v3.1.18: 특정 방향의 Cone/Ray/Sector 패턴 평가
        /// </summary>
        public static AoEScore EvaluateDirectionalAoE(
            Kingmaker.UnitLogic.Abilities.AbilityData ability,
            BaseUnitEntity caster,
            Vector3 direction,
            BaseUnitEntity primaryTarget,
            System.Collections.Generic.List<BaseUnitEntity> enemies,
            System.Collections.Generic.List<BaseUnitEntity> allies,
            Kingmaker.Blueprints.PatternType patternType,
            float radius,
            float angle)
        {
            var score = new AoEScore
            {
                Position = primaryTarget.Position,  // 주 타겟 위치 저장 (타겟팅용)
                IsSafe = true
            };

            const float HIT_SCORE = 10000f;
            const float PENALTY_MULTIPLIER = 1.0f;
            float totalScore = 0f;

            // 모든 유닛 체크
            var allUnits = new System.Collections.Generic.List<BaseUnitEntity>();
            allUnits.AddRange(enemies.Where(e => e != null));
            if (allies != null) allUnits.AddRange(allies.Where(a => a != null));

            foreach (var unit in allUnits)
            {
                if (unit == null || !unit.IsConscious) continue;

                Vector3 toUnit = unit.Position - caster.Position;
                float dist = toUnit.magnitude;
                if (dist > radius) continue;

                // 패턴 내에 있는지 확인
                bool inPattern = IsInDirectionalPattern(toUnit, direction, patternType, radius, angle);
                if (!inPattern) continue;

                score.AffectedUnits.Add(unit);
                float distanceBonus = HIT_SCORE - dist * dist;

                try
                {
                    if (caster.CombatGroup.IsEnemy(unit))
                    {
                        score.EnemiesHit++;
                        totalScore += distanceBonus;
                    }
                    else if (caster.CombatGroup.IsAlly(unit))
                    {
                        score.AlliesHit++;

                        // ★ v3.1.20: 플레이어 파티 아군 - 유연성 추가
                        if (!caster.IsPlayerEnemy && unit.IsInPlayerParty)
                        {
                            // 아군 2명 이상이면 절대 거부
                            if (score.AlliesHit >= 2)
                            {
                                score.IsSafe = false;
                                score.Score = float.MinValue;
                                return score;
                            }

                            // 아군 1명: 높은 페널티 (적 3명 맞춰야 상쇄)
                            totalScore -= 3f * HIT_SCORE;  // -30000
                            CompanionAI_v3.Main.LogDebug($"[AOE] Player party ally in directional pattern: {unit.CharacterName} - heavy penalty applied");
                            continue;  // NPC 페널티 중복 적용 방지
                        }

                        totalScore -= PENALTY_MULTIPLIER * HIT_SCORE;
                    }
                }
                catch
                {
                    score.EnemiesHit++;
                    totalScore += distanceBonus;
                }
            }

            // 캐스터 자신 체크 (Cone의 경우 원점에서 시작하므로 자신은 안전)
            // Ray도 마찬가지로 캐스터 위치에서 시작

            score.Score = totalScore;
            score.IsSafe = score.IsSafe && totalScore > 0 && score.EnemiesHit >= 2;

            return score;
        }

        /// <summary>
        /// ★ v3.1.18: 유닛이 방향성 패턴 내에 있는지 확인
        /// </summary>
        public static bool IsInDirectionalPattern(
            Vector3 toUnit,
            Vector3 direction,
            Kingmaker.Blueprints.PatternType patternType,
            float radius,
            float angle)
        {
            float dist = toUnit.magnitude;
            if (dist > radius) return false;
            if (dist < 0.1f) return false;  // 캐스터 위치

            // 방향과의 각도 계산
            float unitAngle = Vector3.Angle(direction, toUnit);

            switch (patternType)
            {
                case Kingmaker.Blueprints.PatternType.Ray:
                    // Ray: 매우 좁은 직선 (약 15도 - 1타일 폭)
                    return unitAngle <= 15f;

                case Kingmaker.Blueprints.PatternType.Cone:
                case Kingmaker.Blueprints.PatternType.Sector:
                    // Cone/Sector: 지정된 각도의 절반 이내
                    return unitAngle <= angle / 2f;

                default:
                    return false;
            }
        }

        #endregion
    }
}
