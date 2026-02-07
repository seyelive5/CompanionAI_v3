using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.View.Covers;  // ★ v3.8.31: LosCalculations.CoverType
using CompanionAI_v3.Core;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;
using Kingmaker.Blueprints.Classes.Experience;  // ★ v3.8.49: UnitDifficultyType

namespace CompanionAI_v3.Analysis
{
    /// <summary>
    /// ★ v3.1.21: 통합 타겟 스코어링 시스템
    /// Role별 가중치를 적용하여 최적 타겟 선택
    /// </summary>
    public static class TargetScorer
    {
        #region Weight Classes

        /// <summary>
        /// 적 타겟 스코어링 가중치
        /// </summary>
        public class EnemyWeights
        {
            public float HPPercent { get; set; }      // 낮은 HP 우선 (마무리)
            public float Distance { get; set; }       // 거리 패널티/보너스
            public float Threat { get; set; }         // 위협도 (데미지 딜러 등)
            public float CanKill { get; set; }        // 1타 킬 가능 보너스
            public float Hittable { get; set; }       // 현재 공격 가능 보너스
            public float DebuffState { get; set; }    // DOT 등 디버프 상태
            public float SpecialRole { get; set; }    // Healer/Caster 보너스
            public float Difficulty { get; set; }      // ★ v3.8.49: 적 등급 (Boss/Elite 등) 보너스
        }

        /// <summary>
        /// 아군 타겟 스코어링 가중치 (Support용)
        /// </summary>
        public class AllyWeights
        {
            public float HPPercent { get; set; }      // 낮은 HP 우선 (힐 필요)
            public float Distance { get; set; }       // 거리 패널티
            public float AllyRole { get; set; }       // Tank > DPS > Support
            public float InDanger { get; set; }       // 위험 지역 보너스
            public float MissingHP { get; set; }      // 손실 HP 양
        }

        #endregion

        #region Role-based Weight Presets

        // DPS: 약한 적 우선, 1타 킬 최우선
        public static readonly EnemyWeights DPSWeights = new EnemyWeights
        {
            HPPercent = 0.8f,     // 높음 - 마무리 중시
            Distance = 0.3f,      // 낮음 - 이동 OK
            Threat = 0.5f,        // 중간
            CanKill = 1.5f,       // 매우 높음 - 1타 킬 최우선
            Hittable = 0.6f,      // 중간
            DebuffState = 0.7f,   // 높음 - DOT 콤보
            SpecialRole = 0.5f,   // 중간
            Difficulty = 0.6f     // ★ v3.8.49: 중간 - 보스 공격하되 킬 가능한 졸개 우선
        };

        // Tank: 가까운 적 우선, 거리 중시
        public static readonly EnemyWeights TankWeights = new EnemyWeights
        {
            HPPercent = 0.3f,     // 낮음 - 마무리보다 접근
            Distance = 1.0f,      // 매우 높음 - 가까운 적 우선
            Threat = 0.8f,        // 높음 - 위협 제거
            CanKill = 0.4f,       // 낮음 - 킬보다 어그로
            Hittable = 0.8f,      // 높음 - 바로 공격 가능
            DebuffState = 0.2f,   // 낮음
            SpecialRole = 0.3f,   // 낮음
            Difficulty = 1.0f     // ★ v3.8.49: 매우 높음 - 보스 어그로/교전 최우선
        };

        // Support: 안전한 공격, 위협 제거
        public static readonly EnemyWeights SupportWeights = new EnemyWeights
        {
            HPPercent = 0.5f,     // 중간
            Distance = 0.2f,      // 낮음 - 원거리 공격
            Threat = 1.0f,        // 매우 높음 - 위협 제거 우선
            CanKill = 0.6f,       // 중간
            Hittable = 1.0f,      // 매우 높음 - 이동 없이 공격
            DebuffState = 0.8f,   // 높음 - 디버프 활용
            SpecialRole = 0.9f,   // 높음 - Healer/Caster 우선
            Difficulty = 0.8f     // ★ v3.8.49: 높음 - 보스에 디버프/CC 집중
        };

        // Support 아군 타겟 가중치
        public static readonly AllyWeights SupportAllyWeights = new AllyWeights
        {
            HPPercent = 1.0f,     // 매우 높음 - 낮은 HP 우선 힐
            Distance = 0.3f,      // 낮음 - 거리 무시 (힐 사거리 김)
            AllyRole = 0.8f,      // 높음 - Tank > DPS
            InDanger = 0.9f,      // 높음 - 위험 지역 우선
            MissingHP = 0.7f      // 높음 - 손실량 많을수록 우선
        };

        #endregion

        #region Enemy Scoring

        /// <summary>
        /// Role 기반 적 타겟 점수 계산
        /// </summary>
        public static float ScoreEnemy(
            BaseUnitEntity target,
            Situation situation,
            AIRole role)
        {
            if (target == null) return -1000f;

            try
            {
                if (target.LifeState?.IsDead == true) return -1000f;
            }
            catch { }

            var weights = GetEnemyWeights(role);
            var t = AIConfig.GetThresholds();  // ★ v3.5.00: ThresholdConfig 적용
            float score = 50f;  // 기본 점수

            try
            {
                // 1. HP% 점수 (낮을수록 높음)
                float hpPercent = CombatAPI.GetHPPercent(target);
                float hpScore = (100f - hpPercent) * 0.5f;  // 0~50
                score += hpScore * weights.HPPercent;

                // 2. 거리 점수 (가까울수록 좋음, but Role별 차이)
                // ★ v3.5.29: 캐시된 거리 사용
                float distance = CombatCache.GetDistance(situation.Unit, target);
                float distanceScore = -distance * 2f;  // 거리 패널티

                // Tank는 근접 보너스 (★ v3.5.00: ThresholdConfig)
                if (role == AIRole.Tank && distance <= t.ThreatProximity)
                    distanceScore += 30f;

                score += distanceScore * weights.Distance;

                // 3. 1타 킬 가능성 (최우선)
                if (situation.PrimaryAttack != null)
                {
                    if (CombatAPI.CanKillInOneHit(situation.PrimaryAttack, target))
                    {
                        score += 60f * weights.CanKill;
                    }
                }

                // 4. 위협도 평가
                float threat = EvaluateThreat(target, situation);
                score += threat * 30f * weights.Threat;

                // 5. Hittable 여부
                bool isHittable = situation.HittableEnemies?.Contains(target) ?? false;
                if (isHittable)
                    score += 25f * weights.Hittable;
                else
                    score -= 15f;

                // 6. 디버프 상태 (DOT 등)
                if (HasHarmfulDebuff(target))
                {
                    score += 20f * weights.DebuffState;
                }

                // ★ v3.8.31: 명중률 기반 스코어링 (게임 RuleCalculateHitChances 사용)
                // - 낮은 명중률 타겟 페널티 (회피 or 사거리 초과)
                // - 최적 거리 타겟 보너스
                // - 엄폐 상태 반영
                if (situation.PrimaryAttack != null)
                {
                    var hitInfo = CombatAPI.GetHitChance(situation.PrimaryAttack, situation.Unit, target);
                    if (hitInfo != null)
                    {
                        // 명중률 기반 점수 조정
                        if (hitInfo.IsVeryLowHitChance)  // < 30%
                        {
                            score -= 25f;  // 심각한 페널티
                            Main.LogDebug($"[TargetScorer] {target.CharacterName}: -25 very low hit ({hitInfo.HitChance}%)");
                        }
                        else if (hitInfo.IsLowHitChance)  // < 50%
                        {
                            score -= 15f;  // 중간 페널티
                            Main.LogDebug($"[TargetScorer] {target.CharacterName}: -15 low hit ({hitInfo.HitChance}%)");
                        }
                        else if (hitInfo.HitChance >= 80)  // 높은 명중률
                        {
                            score += 10f;  // 보너스
                        }

                        // 최적 거리 보너스 (DistanceFactor >= 1.0)
                        if (hitInfo.IsInOptimalRange)
                        {
                            score += 8f;
                            Main.LogDebug($"[TargetScorer] {target.CharacterName}: +8 optimal range");
                        }

                        // 엄폐 페널티 (Full Cover = 높은 페널티)
                        if (hitInfo.CoverType == LosCalculations.CoverType.Full)
                        {
                            score -= 12f;
                            Main.LogDebug($"[TargetScorer] {target.CharacterName}: -12 full cover");
                        }
                        else if (hitInfo.CoverType == LosCalculations.CoverType.Half)
                        {
                            score -= 6f;
                        }
                    }
                }

                // 7. 특수 역할 (Healer/Caster)
                if (IsHealer(target))
                    score += 20f * weights.SpecialRole;
                if (IsCaster(target))
                    score += 15f * weights.SpecialRole;

                // ★ v3.2.15: TeamBlackboard SharedTarget 보너스 (팀 집중 공격)
                if (TeamBlackboard.Instance.SharedTarget == target)
                {
                    score += 50f;
                    Main.LogDebug($"[TargetScorer] +50 SharedTarget: {target.CharacterName}");
                }

                // ★ v3.8.46: Target Inertia (타겟 관성)
                // 이전 턴에 공격한 타겟에 보너스 → 동일 타겟 집중 공격 유도
                // Inertia(+20) < SharedTarget(+50) → 팀 협동이 항상 우선
                var previousTarget = TeamBlackboard.Instance.GetPreviousTarget(situation.Unit?.UniqueId);
                if (previousTarget != null && previousTarget == target)
                {
                    float inertiaBonus = AIConfig.GetScoringConfig().InertiaBonus;
                    score += inertiaBonus;
                    Main.LogDebug($"[TargetScorer] +{inertiaBonus:F0} Inertia: {target.CharacterName}");
                }

                // ★ v3.2.15: 아군이 타겟팅 중인 적 보너스 (화력 집중)
                int alliesTargeting = TeamBlackboard.Instance.CountAlliesTargeting(target);
                if (alliesTargeting > 0)
                {
                    score += alliesTargeting * 15f;
                }

                // ★ v3.2.25: 전선 기반 고립 페널티
                // 전선 너머 15m 이상인 적은 추격 위험 (고립/매복 위험)
                if (situation.InfluenceMap != null && situation.InfluenceMap.IsValid)
                {
                    float frontlineDist = situation.InfluenceMap.GetFrontlineDistance(target.Position);

                    // 전선 너머 15m 이상 = 추격 위험
                    if (frontlineDist > 15f)
                    {
                        float isolationPenalty = (frontlineDist - 15f) * 2f;
                        score -= isolationPenalty;
                        Main.LogDebug($"[TargetScorer] {target.CharacterName}: -{isolationPenalty:F0} isolation (frontline+{frontlineDist:F1}m)");
                    }

                    // 전선 근처 적 우선 (도달 용이, 팀 지원 가능)
                    if (frontlineDist >= -5f && frontlineDist <= 5f)
                    {
                        score += 10f;
                    }
                }

                // ★ v3.8.49: 적 등급(DifficultyType) 기반 전략적 중요도
                // 게임 디자이너의 공식 난도 분류 (Swarm~ChapterBoss)를 활용
                // EvaluateThreat(행동 기반)와 분리 — 게임 분류 기반 독립 요소
                var difficultyType = CombatAPI.GetDifficultyType(target);
                float difficultyScore = 0f;
                switch (difficultyType)
                {
                    case UnitDifficultyType.Elite:       difficultyScore = 8f;  break;
                    case UnitDifficultyType.MiniBoss:    difficultyScore = 15f; break;
                    case UnitDifficultyType.Boss:        difficultyScore = 25f; break;
                    case UnitDifficultyType.ChapterBoss: difficultyScore = 30f; break;
                    // Swarm/Common/Hard = 0 (기본 적)
                }
                if (difficultyScore > 0f)
                {
                    score += difficultyScore * weights.Difficulty;
                    Main.LogDebug($"[TargetScorer] {target.CharacterName}: +{difficultyScore * weights.Difficulty:F0} difficulty ({difficultyType})");
                }

                // ★ v3.2.30: 킬 시뮬레이터 확정 킬 보너스 (설정으로 토글 가능)
                bool useKillSimulator = situation.CharacterSettings?.UseKillSimulator ?? true;
                if (useKillSimulator)
                {
                    var killSequence = KillSimulator.FindKillSequence(situation, target);
                    if (killSequence != null && killSequence.IsConfirmedKill)
                    {
                        // 확정 킬 가능 타겟에 높은 보너스
                        // 효율이 높을수록 (낮은 AP로 킬) 추가 보너스
                        float killBonus = 40f + Math.Min(killSequence.Efficiency * 5f, 20f);

                        // ★ v3.5.83: AOE 다중 킬 보너스
                        // AOE 1능력으로 킬 가능하면, 패턴 내 다른 적까지 동시 킬 가능성 평가
                        if (killSequence.Abilities.Count == 1 && AbilityDatabase.IsAoE(killSequence.Abilities[0]))
                        {
                            var aoeAbility = killSequence.Abilities[0];
                            int totalInPattern = CombatAPI.CountEnemiesInPattern(
                                aoeAbility,
                                target.Position,
                                situation.Unit.Position,
                                situation.Enemies);

                            int additionalTargets = Math.Max(0, totalInPattern - 1);
                            if (additionalTargets > 0)
                            {
                                // 추가 킬당 +20점 보너스
                                float multiKillBonus = additionalTargets * 20f;
                                killBonus += multiKillBonus;
                                Main.LogDebug($"[TargetScorer] {target.CharacterName}: +{multiKillBonus:F0} AOE multi-kill ({additionalTargets} additional targets)");
                            }
                        }

                        score += killBonus;
                        Main.LogDebug($"[TargetScorer] {target.CharacterName}: +{killBonus:F0} ConfirmedKill ({killSequence.Abilities.Count} abilities, {killSequence.TotalDamage:F0} dmg)");
                    }
                }

                // ★ v3.5.84: AOE 클러스터 보너스 (KillSimulator와 동일 방식)
                // 타겟 선택 시 AOE 가치 반영 - 클러스터 중심 타겟 우선
                // ★ 수정: IsAoE() 체크 대신 CountEnemiesInPattern 결과로 판단 (점사 사격 등 포함)
                float aoeClusterBonus = 0f;
                int availableAttackCount = situation.AvailableAttacks?.Count ?? 0;

                if (availableAttackCount > 0)
                {
                    foreach (var attack in situation.AvailableAttacks)
                    {
                        // ★ v3.5.84: 게임 API로 직접 패턴 체크 (IsAoE() 대신)
                        int enemiesInPattern = CombatAPI.CountEnemiesInPattern(
                            attack,
                            target.Position,
                            situation.Unit.Position,
                            situation.Enemies);

                        // 2명 이상 맞추면 AOE로 간주
                        if (enemiesInPattern < 2) continue;

                        Main.LogDebug($"[TargetScorer] AOE: {attack.Name} -> {target.CharacterName}: {enemiesInPattern} enemies in pattern");

                        int additionalEnemies = enemiesInPattern - 1;
                        float attackAoEBonus = additionalEnemies * 10f;  // 타겟 선택용 보너스 (+10/적)

                        if (attackAoEBonus > aoeClusterBonus)
                            aoeClusterBonus = attackAoEBonus;
                    }
                }
                if (aoeClusterBonus > 0f)
                {
                    score += aoeClusterBonus;
                    Main.LogDebug($"[TargetScorer] {target.CharacterName}: +{aoeClusterBonus:F0} AOE cluster bonus");
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[TargetScorer] ScoreEnemy error: {ex.Message}");
            }

            return score;
        }

        /// <summary>
        /// Role별 가중치 반환
        /// </summary>
        private static EnemyWeights GetEnemyWeights(AIRole role)
        {
            switch (role)
            {
                case AIRole.Tank: return TankWeights;
                case AIRole.Support: return SupportWeights;
                case AIRole.DPS:
                default: return DPSWeights;
            }
        }

        /// <summary>
        /// Role 기반 최적 적 타겟 선택
        /// </summary>
        public static BaseUnitEntity SelectBestEnemy(
            List<BaseUnitEntity> candidates,
            Situation situation,
            AIRole role)
        {
            if (candidates == null || candidates.Count == 0)
                return null;

            try
            {
                // ★ v3.8.48: LINQ → CollectionHelper (0 할당, O(n))
                float bestScore;
                var best = CollectionHelper.MaxByWhere(candidates,
                    t => {
                        try { return t.LifeState?.IsDead != true; }
                        catch { return true; }
                    },
                    t => ScoreEnemy(t, situation, role),
                    out bestScore);

                if (best != null)
                {
                    Main.LogDebug($"[TargetScorer] Best enemy for {role}: {best.CharacterName} (score={bestScore:F1})");
                    return best;
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[TargetScorer] SelectBestEnemy error: {ex.Message}");
            }

            return candidates.FirstOrDefault();
        }

        #endregion

        #region Ally Scoring

        /// <summary>
        /// 아군 힐 대상 점수 계산
        /// </summary>
        public static float ScoreAllyForHealing(
            BaseUnitEntity ally,
            Situation situation)
        {
            if (ally == null) return -1000f;

            try
            {
                if (ally.LifeState?.IsDead == true) return -1000f;
            }
            catch { }

            var weights = SupportAllyWeights;
            float score = 0f;

            try
            {
                var t = AIConfig.GetThresholds();  // ★ v3.5.00: ThresholdConfig 적용
                var healThresholds = t.HealPriorityThresholds;

                // 1. HP% (낮을수록 힐 우선) - ★ v3.5.00: 다단계 임계값
                float hpPercent = CombatAPI.GetHPPercent(ally);
                if (hpPercent < healThresholds[0]) score += 80f * weights.HPPercent;       // 최우선 (기본 25%)
                else if (hpPercent < healThresholds[1]) score += 50f * weights.HPPercent;  // 높음 (기본 50%)
                else if (hpPercent < healThresholds[2]) score += 20f * weights.HPPercent;  // 보통 (기본 75%)
                else score -= 30f;  // 힐 불필요

                // 2. 거리 패널티
                // ★ v3.5.29: 캐시된 거리 사용
                float distance = CombatCache.GetDistance(situation.Unit, ally);
                score -= distance * 2f * weights.Distance;

                // 3. 역할 우선순위 (Tank > DPS > Support)
                var allyRole = GetUnitRole(ally);
                switch (allyRole)
                {
                    case AIRole.Tank:
                        score += 30f * weights.AllyRole;
                        break;
                    case AIRole.DPS:
                        score += 20f * weights.AllyRole;
                        break;
                    case AIRole.Support:
                        score += 10f * weights.AllyRole;
                        break;
                }

                // 4. 위험 상태 (적과 가까움) - ★ v3.5.00: ThresholdConfig
                float allyNearestEnemyDist = GetNearestEnemyDistance(ally, situation);
                if (allyNearestEnemyDist < t.ThreatProximity)
                {
                    score += 25f * weights.InDanger;
                }

                // 5. 손실 HP 양
                float missingHP = 100f - hpPercent;
                score += missingHP * 0.3f * weights.MissingHP;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[TargetScorer] ScoreAllyForHealing error: {ex.Message}");
            }

            return score;
        }

        /// <summary>
        /// 최적 힐 대상 선택
        /// </summary>
        public static BaseUnitEntity SelectBestAllyForHealing(
            List<BaseUnitEntity> allies,
            Situation situation,
            float hpThreshold = 80f)
        {
            if (allies == null || allies.Count == 0)
                return null;

            try
            {
                // ★ v3.8.48: LINQ → CollectionHelper (0 할당, O(n))
                float bestScore;
                var best = CollectionHelper.MaxByWhere(allies,
                    a => {
                        try { return a.LifeState?.IsDead != true && CombatAPI.GetHPPercent(a) < hpThreshold; }
                        catch { return false; }
                    },
                    a => ScoreAllyForHealing(a, situation),
                    out bestScore);

                if (best != null)
                {
                    Main.LogDebug($"[TargetScorer] Best ally for healing: {best.CharacterName} (score={bestScore:F1})");
                    return best;
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[TargetScorer] SelectBestAllyForHealing error: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// 최적 버프 대상 선택 (Support)
        /// </summary>
        public static BaseUnitEntity SelectBestAllyForBuff(
            List<BaseUnitEntity> allies,
            Situation situation)
        {
            if (allies == null || allies.Count == 0)
                return null;

            try
            {
                // ★ v3.8.48: LINQ → CollectionHelper (0 할당, O(n))
                // 버프는 역할 우선순위 중시, HP는 덜 중요
                float bestScore;
                var best = CollectionHelper.MaxByWhere(allies,
                    a => {
                        try { return a.LifeState?.IsDead != true && a.IsConscious; }
                        catch { return false; }
                    },
                    a => GetBuffPriority(a, situation),
                    out bestScore);

                if (best != null)
                {
                    Main.LogDebug($"[TargetScorer] Best ally for buff: {best.CharacterName} (score={bestScore:F1})");
                    return best;
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[TargetScorer] SelectBestAllyForBuff error: {ex.Message}");
            }

            return allies.FirstOrDefault();
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// ★ v3.5.40: 위협도 평가 (0.0 ~ 1.0)
        /// 추정/추측 금지 원칙: 게임 API에서 직접 조회 가능한 값만 사용
        ///
        /// 구성요소:
        /// 1. Lethality (HP 기반) - 만피일수록 위협적
        /// 2. Proximity (거리 기반) - 가까울수록 위협적
        /// 3. RoleBonus (역할 기반) - Healer/Caster/원거리 보너스
        ///
        /// 미구현 (API 제약):
        /// - 적 데미지 예측 (GetDamagePrediction은 우리 능력 전용)
        /// </summary>
        private static float EvaluateThreat(BaseUnitEntity target, Situation situation)
        {
            float threat = 0f;

            try
            {
                var t = AIConfig.GetThresholds();

                // 1. Lethality (HP 기반) - Response Curve 적용
                // API: CombatAPI.GetHPPercent() ✅ 검증됨
                float hpPercent = CombatAPI.GetHPPercent(target);
                float hpNormalized = hpPercent / 100f;  // 0~1 (0=빈사, 1=만피)
                float lethalityScore = CurvePresets.EnemyLethality.Evaluate(hpNormalized);
                threat += lethalityScore * t.LethalityWeight;

                // 2. Proximity (거리 기반) - Response Curve 적용
                // API: CombatCache.GetDistance() ✅ 검증됨
                float distance = CombatCache.GetDistance(situation.Unit, target);
                float maxRange = t.ThreatMaxDistance;
                float proximityNormalized = 1f - Math.Min(1f, distance / maxRange);  // 0~1 (0=멀리, 1=가까이)
                float proximityScore = CurvePresets.EnemyProximity.Evaluate(proximityNormalized);
                threat += proximityScore * t.ProximityWeight;

                // 3. RoleBonus (역할 기반) - 게임 API 직접 조회
                // API: unit.Abilities.Enumerable ✅ 검증됨
                if (IsHealer(target))
                    threat += t.HealerRoleBonus;
                if (IsCaster(target))
                    threat += t.CasterRoleBonus;

                // 무기 기반 보너스 (데미지 예측 대체)
                // API: weapon.Blueprint.IsMelee ✅ 검증됨
                if (HasRangedWeapon(target))
                    threat += t.RangedWeaponBonus;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[TargetScorer] EvaluateThreat error: {ex.Message}");
                return 0.5f;  // 폴백: 중간 위협도
            }

            return Math.Max(0f, Math.Min(1f, threat));
        }

        /// <summary>
        /// ★ v3.5.40: 원거리 무기 소지 여부 확인
        /// </summary>
        private static bool HasRangedWeapon(BaseUnitEntity unit)
        {
            try
            {
                var weapon = unit.Body?.PrimaryHand?.Weapon;
                if (weapon == null) return false;
                return weapon.Blueprint?.IsMelee == false;
            }
            catch { return false; }
        }

        /// <summary>
        /// ★ v3.5.75: CombatHelpers.IsHealer()로 통합 (중복 제거)
        /// </summary>
        private static bool IsHealer(BaseUnitEntity unit)
            => CombatHelpers.IsHealer(unit);

        /// <summary>
        /// 유닛이 Caster인지 확인
        /// </summary>
        private static bool IsCaster(BaseUnitEntity unit)
        {
            try
            {
                var abilities = unit.Abilities?.Enumerable;
                if (abilities == null) return false;

                return abilities.Any(a => a?.Data != null &&
                    AbilityDatabase.IsPsychic(a.Data));
            }
            catch { return false; }
        }

        /// <summary>
        /// ★ v3.7.65: 유닛에 해로운 디버프가 있는지 확인 (게임 API 기반 - 키워드 매칭 제거)
        /// </summary>
        private static bool HasHarmfulDebuff(BaseUnitEntity unit)
        {
            try
            {
                var buffs = unit.Buffs?.Enumerable;
                if (buffs == null) return false;

                return buffs.Any(b => {
                    var bp = b.Blueprint;
                    if (bp == null) return false;

                    // 1. 적으로부터 받은 버프 = 디버프일 가능성 높음
                    var caster = b.Context?.MaybeCaster;
                    if (caster != null && unit.CombatGroup?.IsEnemy(caster) == true)
                        return true;

                    // ★ v3.7.65: 게임 API - IsHardCrowdControl 체크 (HardCrowdControlBuff 컴포넌트 보유)
                    if (bp.IsHardCrowdControl)
                        return true;

                    // ★ v3.7.65: DOT 효과는 해로운 효과
                    if (bp.IsDOTVisual)
                        return true;

                    // ★ v3.7.65: DynamicDamage 플래그가 있으면 피해 효과
                    if (bp.DynamicDamage)
                        return true;

                    return false;
                });
            }
            catch { return false; }
        }

        /// <summary>
        /// 가장 가까운 적까지의 거리
        /// </summary>
        private static float GetNearestEnemyDistance(
            BaseUnitEntity ally,
            Situation situation)
        {
            if (situation.Enemies == null || situation.Enemies.Count == 0)
                return float.MaxValue;

            try
            {
                // ★ v3.5.29: 캐시된 거리 사용
                return situation.Enemies
                    .Where(e => e != null)
                    .Where(e => {
                        try { return e.LifeState?.IsDead != true; }
                        catch { return true; }
                    })
                    .Select(e => CombatCache.GetDistance(ally, e))
                    .DefaultIfEmpty(float.MaxValue)
                    .Min();
            }
            catch { return float.MaxValue; }
        }

        /// <summary>
        /// 유닛의 설정된 Role 가져오기
        /// </summary>
        private static AIRole GetUnitRole(BaseUnitEntity unit)
        {
            try
            {
                var settings = ModSettings.Instance?.GetOrCreateSettings(
                    unit.UniqueId, unit.CharacterName);
                return settings?.Role ?? AIRole.Auto;
            }
            catch { return AIRole.Auto; }
        }

        /// <summary>
        /// 버프 우선순위 점수
        /// </summary>
        private static float GetBuffPriority(
            BaseUnitEntity ally,
            Situation situation)
        {
            float priority = 0f;

            try
            {
                var t = AIConfig.GetThresholds();  // ★ v3.5.00: ThresholdConfig 적용
                var role = GetUnitRole(ally);

                switch (role)
                {
                    case AIRole.Tank: priority += 30f; break;
                    case AIRole.DPS: priority += 20f; break;
                    case AIRole.Support: priority += 10f; break;
                }

                // 본인은 약간 낮은 우선순위
                if (ally == situation.Unit)
                    priority -= 5f;

                // 낮은 HP = 높은 우선순위 (보호 필요) - ★ v3.5.00: ThresholdConfig
                float hpPercent = CombatAPI.GetHPPercent(ally);
                if (hpPercent < t.PreAttackBuffMinHP)
                    priority += 15f;
            }
            catch { }

            return priority;
        }

        #endregion
    }
}
