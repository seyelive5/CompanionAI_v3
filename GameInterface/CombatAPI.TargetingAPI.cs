// ★ v3.111.28 (Phase D.2 Session 6): CombatAPI god-file 분리
// 원본 GameInterface/CombatAPI.cs 의 4 region 을 partial class 로 추출
//   - Target Scoring System (nested: Accurate Damage Prediction)
//   - Targeting Detection (v3.1.25)
//   - Hit Chance API (v3.6.7)
//   - Flanking API (v3.28.0)
// 2× CalculateTargetScore private static overloads 포함.
// Cross-partial: Hit Chance 가 CombatAPI.UnitQueries.cs 의 CalculateEffectiveHitChance 호출
//   (2 call sites — marker 주석 추가, Session 3 precedent).
using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.Blueprints.Classes.Experience;  // UnitDifficultyType (Damage Prediction)
using Kingmaker.EntitySystem.Entities;           // BaseUnitEntity
using Kingmaker.Pathfinding;                     // CustomGraphHelper (Flanking)
using Kingmaker.RuleSystem;                      // Rulebook (Hit Chance)
using Kingmaker.RuleSystem.Rules;                // RuleCalculateHitChances (Hit Chance)
using Kingmaker.UnitLogic.Abilities;             // AbilityData
using Kingmaker.UnitLogic.Parts;                 // WarhammerCombatSide (Flanking)
using Kingmaker.Utility;                         // TargetWrapper
using Kingmaker.View.Covers;                     // LosCalculations (Hit Chance CoverType)
using UnityEngine;                               // Vector3
using CompanionAI_v3.Settings;                   // SC, RangePreference

namespace CompanionAI_v3.GameInterface
{
    public static partial class CombatAPI
    {
        #region Target Scoring System

        /// <summary>
        /// 타겟 점수 정보
        /// ★ v3.0.1: 실제 데미지/HP 기반 정보 추가
        /// </summary>
        public class TargetScore
        {
            public BaseUnitEntity Target { get; set; }
            public float Score { get; set; }
            public string Reason { get; set; }
            public bool IsHittable { get; set; }
            public float Distance { get; set; }
            public float HPPercent { get; set; }
            // ★ v3.0.1: 실제 데미지 정보
            public int ActualHP { get; set; }
            public int PredictedMinDamage { get; set; }
            public int PredictedMaxDamage { get; set; }
            public bool CanKillInOneHit { get; set; }
            public bool CanKillInTwoHits { get; set; }
        }

        #region Accurate Damage Prediction (v3.0.1)

        /// <summary>
        /// ★ v3.0.1: 유닛의 실제 현재 HP 반환
        /// </summary>
        public static int GetActualHP(BaseUnitEntity unit)
        {
            if (unit == null) return 0;
            try
            {
                return unit.Health?.HitPointsLeft ?? 0;
            }
            // ★ v3.13.0: 안전한 기본값 — 1 (0은 "사망"으로 오판될 위험, 1은 "빈사")
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetActualHP failed for {unit?.CharacterName}: {ex.Message}");
                return 1;
            }
        }

        /// <summary>
        /// ★ v3.0.1: 유닛의 최대 HP 반환
        /// </summary>
        public static int GetActualMaxHP(BaseUnitEntity unit)
        {
            if (unit == null) return 0;
            try
            {
                return unit.Health?.MaxHitPoints ?? 0;
            }
            // ★ v3.13.0: 안전한 기본값 — 1 (0으로 나눔 방지, HP% 계산 안전)
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetActualMaxHP failed for {unit?.CharacterName}: {ex.Message}");
                return 1;
            }
        }

        /// <summary>
        /// ★ v3.8.49: 적 난도 등급 조회
        /// 게임 BlueprintUnit.DifficultyType (Swarm~ChapterBoss 7단계)
        /// </summary>
        public static UnitDifficultyType GetDifficultyType(BaseUnitEntity unit)
        {
            if (unit == null) return UnitDifficultyType.Common;
            try
            {
                return unit.Blueprint.DifficultyType;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetDifficultyType failed for {unit?.CharacterName}: {ex.Message}");
                return UnitDifficultyType.Common;
            }
        }

        /// <summary>
        /// ★ v3.0.1: 게임 API를 사용한 정확한 데미지 예측
        /// ability.GetDamagePrediction(target, casterPosition, context) 사용
        /// </summary>
        public static (int MinDamage, int MaxDamage, int Penetration) GetDamagePrediction(
            AbilityData ability,
            BaseUnitEntity target)
        {
            if (ability == null || target == null)
                return (0, 0, 0);

            try
            {
                var caster = ability.Caster as BaseUnitEntity;
                if (caster == null) return (0, 0, 0);

                // ★ 게임 API: AbilityDataHelper.GetDamagePrediction()
                var prediction = ability.GetDamagePrediction(target, caster.Position, null);
                if (prediction == null) return (0, 0, 0);

                return (prediction.MinDamage, prediction.MaxDamage, prediction.Penetration);
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetDamagePrediction error: {ex.Message}");
                return (0, 0, 0);
            }
        }

        /// <summary>
        /// ★ v3.0.1: 1타에 킬 가능 여부 (MinDamage >= CurrentHP)
        /// </summary>
        public static bool CanKillInOneHit(AbilityData ability, BaseUnitEntity target)
        {
            if (ability == null || target == null) return false;

            try
            {
                int hp = GetActualHP(target);
                if (hp <= 0) return false;

                var (minDamage, maxDamage, _) = GetDamagePrediction(ability, target);

                // 최소 데미지로도 킬 가능
                return minDamage >= hp;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CanKillInOneHit failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.0.1: 2타에 킬 가능 여부 (MaxDamage * 2 >= CurrentHP)
        /// </summary>
        public static bool CanKillInTwoHits(AbilityData ability, BaseUnitEntity target)
        {
            if (ability == null || target == null) return false;

            try
            {
                int hp = GetActualHP(target);
                if (hp <= 0) return false;

                var (minDamage, maxDamage, _) = GetDamagePrediction(ability, target);

                // 최대 데미지 2번으로 킬 가능
                return maxDamage * 2 >= hp;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CanKillInTwoHits failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.0.1: 예상 킬 확률 계산 (0.0 ~ 1.0)
        /// - 1.0 = 확실한 1타 킬 (MinDamage >= HP)
        /// - 0.5+ = 높은 확률의 1타 킬 (MaxDamage >= HP)
        /// - 낮음 = 여러 타 필요
        /// </summary>
        public static float CalculateKillProbability(AbilityData ability, BaseUnitEntity target)
        {
            if (ability == null || target == null) return 0f;

            try
            {
                int hp = GetActualHP(target);
                if (hp <= 0) return 1f;

                var (minDamage, maxDamage, _) = GetDamagePrediction(ability, target);
                if (maxDamage <= 0) return 0f;

                // 최소 데미지로도 킬 가능 → 100%
                if (minDamage >= hp) return 1.0f;

                // 최대 데미지로 킬 가능 → 확률 계산 (데미지 분포가 균일하다고 가정)
                if (maxDamage >= hp)
                {
                    // (maxDamage - hp) / (maxDamage - minDamage)
                    float range = maxDamage - minDamage;
                    if (range <= 0) return 0.5f;
                    return (float)(maxDamage - hp) / range;
                }

                // 2타 킬 가능성
                if (maxDamage * 2 >= hp)
                {
                    return 0.25f;  // 2타 필요
                }

                // 3타 이상 필요
                return 0.1f;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CalculateKillProbability failed: {ex.Message}");
                return 0f;
            }
        }

        /// <summary>
        /// ★ v3.0.44: 예상 평균 데미지 계산
        /// </summary>
        public static float EstimateDamage(AbilityData ability, BaseUnitEntity target)
        {
            if (ability == null || target == null) return 0f;

            try
            {
                var (minDamage, maxDamage, _) = GetDamagePrediction(ability, target);
                return (minDamage + maxDamage) / 2f;
            }
            catch
            {
                // 폴백: 레벨 기반 추정
                return Settings.SC.FallbackEstimateDamage;
            }
        }

        #endregion

        /// <summary>
        /// 모든 적에 대해 타겟 점수 계산 - SituationAnalyzer에서 사용
        /// ★ v3.0.1: 실제 데미지 예측 기반 스코어링
        /// </summary>
        public static List<TargetScore> ScoreAllTargets(
            BaseUnitEntity unit,
            List<BaseUnitEntity> enemies,
            AbilityData attackAbility,
            RangePreference preference)
        {
            var scores = new List<TargetScore>();
            if (unit == null || enemies == null) return scores;

            foreach (var enemy in enemies)
            {
                if (enemy == null || enemy.LifeState.IsDead) continue;

                var score = new TargetScore
                {
                    Target = enemy,
                    Distance = GetDistance(unit, enemy),
                    HPPercent = GetHPPercent(enemy),
                    ActualHP = GetActualHP(enemy),
                    IsHittable = false,
                    Score = 0f,
                    Reason = ""
                };

                // 공격 가능 여부
                if (attackAbility != null)
                {
                    var target = new TargetWrapper(enemy);
                    string reason;
                    score.IsHittable = CanUseAbilityOn(attackAbility, target, out reason);
                    if (!score.IsHittable)
                    {
                        score.Reason = reason;
                        // ★ v3.0.14: Hittable=false 원인 로깅
                        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Not hittable: {enemy.CharacterName} - {reason} (dist={score.Distance:F1}m, ability={attackAbility.Name})");
                    }

                    // ★ v3.0.1: 실제 데미지 예측
                    var (minDmg, maxDmg, _) = GetDamagePrediction(attackAbility, enemy);
                    score.PredictedMinDamage = minDmg;
                    score.PredictedMaxDamage = maxDmg;
                    score.CanKillInOneHit = minDmg >= score.ActualHP && score.ActualHP > 0;
                    score.CanKillInTwoHits = maxDmg * 2 >= score.ActualHP && score.ActualHP > 0;
                }

                // ★ v3.0.1: 실제 데미지 기반 점수 계산
                score.Score = CalculateTargetScore(unit, enemy, attackAbility, score.IsHittable, preference, score);

                scores.Add(score);
            }

            return scores.OrderByDescending(s => s.Score).ToList();
        }

        /// <summary>
        /// 최적 타겟 찾기
        /// </summary>
        public static BaseUnitEntity FindBestTarget(
            BaseUnitEntity unit,
            List<BaseUnitEntity> enemies,
            AbilityData attackAbility,
            RangePreference preference)
        {
            var scores = ScoreAllTargets(unit, enemies, attackAbility, preference);

            // 공격 가능한 타겟 중 최고 점수
            var hittable = scores.FirstOrDefault(s => s.IsHittable);
            if (hittable != null)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Best target: {hittable.Target.CharacterName} (score={hittable.Score:F1})");
                return hittable.Target;
            }

            // 공격 불가 시 가장 가까운 적
            var nearest = scores.OrderBy(s => s.Distance).FirstOrDefault();
            return nearest?.Target;
        }

        /// <summary>
        /// ★ v3.0.1: 실제 데미지 기반 타겟 점수 계산
        /// - 1타 킬 가능: +50 보너스
        /// - 2타 킬 가능: +25 보너스
        /// - HP가 낮을수록: +점수 (1/HP 기반, 게임 AI와 동일)
        /// - 거리: 근접/원거리 선호도에 따라 보너스
        /// </summary>
        private static float CalculateTargetScore(
            BaseUnitEntity caster,
            BaseUnitEntity target,
            AbilityData attackAbility,
            bool isHittable,
            RangePreference preference,
            TargetScore scoreData = null)
        {
            float score = 0f;

            // 기본 점수: 공격 가능 여부
            if (isHittable) score += 100f;

            // ★ v3.0.1: 1타 킬 가능 최우선 (+50)
            if (scoreData != null && scoreData.CanKillInOneHit && isHittable)
            {
                score += 50f;
                if (Main.IsDebugEnabled) Main.LogDebug($"[Scoring] {target.CharacterName}: +50 (1-hit kill possible, HP={scoreData.ActualHP}, MinDmg={scoreData.PredictedMinDamage})");
            }
            // 2타 킬 가능 (+25)
            else if (scoreData != null && scoreData.CanKillInTwoHits && isHittable)
            {
                score += 25f;
                if (Main.IsDebugEnabled) Main.LogDebug($"[Scoring] {target.CharacterName}: +25 (2-hit kill possible)");
            }

            // ★ v3.0.1: HP 점수 - 게임 AI와 동일한 방식 (1/HP)
            // 낮은 HP = 높은 점수 (최대 +30)
            int actualHP = scoreData?.ActualHP ?? GetActualHP(target);
            if (actualHP > 0)
            {
                // 1000 / HP 로 정규화 (HP 100 → +10, HP 50 → +20, HP 30 → +33)
                float hpScore = Math.Min(30f, 1000f / actualHP);
                score += hpScore;
            }
            else
            {
                // 폴백: HP% 기반
                float hpPercent = GetHPPercent(target);
                score += (100f - hpPercent) * 0.3f;  // 최대 +30
            }

            // 거리 점수: 가까울수록 높은 점수
            float distance = GetDistance(caster, target);
            if (distance < 30f)
            {
                score += (30f - distance) * 0.3f;  // 최대 +9
            }

            // RangePreference 보너스
            if (preference == RangePreference.PreferMelee && distance <= 3f)
            {
                score += 15f;  // 근접 범위 내
            }
            else if (preference == RangePreference.PreferRanged && distance >= 5f && distance <= 15f)
            {
                score += 12f;  // 최적 원거리
            }

            // ★ v3.0.1: 킬 확률 보너스 (데미지 예측 기반)
            if (attackAbility != null && isHittable)
            {
                float killProb = CalculateKillProbability(attackAbility, target);
                score += killProb * 20f;  // 최대 +20 (100% 킬 확률)
            }

            return score;
        }

        /// <summary>
        /// Legacy 호환: 이전 시그니처 유지
        /// </summary>
        private static float CalculateTargetScore(
            BaseUnitEntity caster,
            BaseUnitEntity target,
            bool isHittable,
            RangePreference preference)
        {
            return CalculateTargetScore(caster, target, null, isHittable, preference, null);
        }

        /// <summary>
        /// 타겟이 실제로 공격 가능한지 확인 (Hittable check)
        /// </summary>
        public static bool CheckIfHittable(BaseUnitEntity unit, BaseUnitEntity target, AbilityData attackAbility)
        {
            if (unit == null || target == null) return false;

            if (attackAbility != null)
            {
                var targetWrapper = new TargetWrapper(target);
                string reason;
                return CanUseAbilityOn(attackAbility, targetWrapper, out reason);
            }

            // 능력 없으면 거리로만 추정
            float dist = GetDistance(unit, target);
            return dist <= 15f;
        }

        #endregion

        #region Targeting Detection (v3.1.25)

        /// <summary>
        /// ★ v3.1.25: 적이 특정 유닛을 타겟팅 중인지 확인
        /// </summary>
        public static bool IsTargeting(BaseUnitEntity enemy, BaseUnitEntity target)
        {
            if (enemy?.CombatState == null || target == null) return false;
            try
            {
                return enemy.CombatState.LastTarget == target;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsTargeting failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.1.25: 특정 아군을 타겟팅 중인 적 목록 조회
        /// </summary>
        public static List<BaseUnitEntity> GetEnemiesTargeting(
            BaseUnitEntity ally,
            List<BaseUnitEntity> enemies)
        {
            var targeting = new List<BaseUnitEntity>();
            if (ally == null || enemies == null) return targeting;

            foreach (var enemy in enemies)
            {
                if (enemy?.CombatState?.LastTarget == ally)
                    targeting.Add(enemy);
            }
            return targeting;
        }

        /// <summary>
        /// ★ v3.1.25: 아군(특정 유닛 제외)을 타겟팅 중인 모든 적 조회
        /// 탱커가 호출할 때: excludeUnit = 탱커 자신 (탱커 타겟팅 적은 이미 어그로 잡힌 상태)
        /// </summary>
        public static List<BaseUnitEntity> GetEnemiesTargetingAllies(
            BaseUnitEntity excludeUnit,
            List<BaseUnitEntity> allies,
            List<BaseUnitEntity> enemies)
        {
            var targeting = new List<BaseUnitEntity>();
            if (allies == null || enemies == null) return targeting;

            foreach (var enemy in enemies)
            {
                if (enemy?.CombatState == null) continue;
                var lastTarget = enemy.CombatState.LastTarget as BaseUnitEntity;
                if (lastTarget != null && lastTarget != excludeUnit && allies.Contains(lastTarget))
                {
                    targeting.Add(enemy);
                }
            }
            return targeting;
        }

        /// <summary>
        /// ★ v3.1.25: 위협받는 아군 수 (탱커 제외)
        /// </summary>
        public static int CountAlliesUnderThreat(
            BaseUnitEntity excludeUnit,
            List<BaseUnitEntity> allies,
            List<BaseUnitEntity> enemies)
        {
            if (allies == null || enemies == null) return 0;

            var threatenedAllies = new HashSet<BaseUnitEntity>();
            foreach (var enemy in enemies)
            {
                if (enemy?.CombatState == null) continue;
                var lastTarget = enemy.CombatState.LastTarget as BaseUnitEntity;
                if (lastTarget != null && lastTarget != excludeUnit && allies.Contains(lastTarget))
                {
                    threatenedAllies.Add(lastTarget);
                }
            }
            return threatenedAllies.Count;
        }

        #endregion

        #region Hit Chance API (v3.6.7)

        /// <summary>
        /// ★ v3.6.7: 명중률 정보 구조체
        /// </summary>
        public class HitChanceInfo
        {
            /// <summary>★ v3.26.0: 실질 명중률 (BS × (1-Dodge) × (1-Parry), 1-95%)</summary>
            public int HitChance { get; set; }

            /// <summary>★ v3.26.0: BS 기반 원본 명중률 (dodge/parry 미반영)</summary>
            public int RawBSHitChance { get; set; }

            /// <summary>★ v3.26.0: 추정 회피율 (0-95%)</summary>
            public int EstimatedDodgeChance { get; set; }

            /// <summary>★ v3.26.0: 추정 패리율 (0-95%, 근접만)</summary>
            public int EstimatedParryChance { get; set; }

            /// <summary>거리 계수 (1.0=최적, 0.5=절반 이상, 0.0=사거리 초과)</summary>
            public float DistanceFactor { get; set; }

            /// <summary>엄폐 타입</summary>
            public LosCalculations.CoverType CoverType { get; set; }

            /// <summary>최적 거리 내에 있는지 (DistanceFactor >= 1.0)</summary>
            public bool IsInOptimalRange => DistanceFactor >= 1.0f;

            /// <summary>최대 사거리 내에 있는지 (DistanceFactor > 0)</summary>
            public bool IsInRange => DistanceFactor > 0f;

            /// <summary>명중률이 낮은지 (50% 미만)</summary>
            public bool IsLowHitChance => HitChance < 50;

            /// <summary>명중률이 매우 낮은지 (30% 미만)</summary>
            public bool IsVeryLowHitChance => HitChance < 30;

            public override string ToString()
            {
                return $"HitChance={HitChance}%(BS={RawBSHitChance}% dodge={EstimatedDodgeChance}% parry={EstimatedParryChance}%), DistFactor={DistanceFactor:F1}, Cover={CoverType}";
            }
        }

        /// <summary>
        /// ★ v3.6.7: 원거리 공격의 명중률 계산
        /// RuleCalculateHitChances 룰 시스템 사용
        /// </summary>
        /// <param name="ability">공격 능력</param>
        /// <param name="attacker">공격자</param>
        /// <param name="target">타겟</param>
        /// <returns>명중률 정보 (null if 계산 실패)</returns>
        public static HitChanceInfo GetHitChance(AbilityData ability, BaseUnitEntity attacker, BaseUnitEntity target)
        {
            if (ability == null || attacker == null || target == null)
                return null;

            try
            {
                int rawHitChance;
                float distanceFactor = 1.0f;
                var coverType = LosCalculations.CoverType.None;

                // ★ v3.6.8: 근접/Scatter 공격은 BS 100% (게임 로직 동일)
                if (ability.IsMelee || ability.IsScatter)
                {
                    rawHitChance = 100;
                }
                else
                {
                    // RuleCalculateHitChances 트리거
                    var hitRule = new RuleCalculateHitChances(
                        attacker, target, ability,
                        0,  // burstIndex (첫 발)
                        attacker.Position, target.Position
                    );
                    Rulebook.Trigger(hitRule);

                    rawHitChance = hitRule.ResultHitChance;
                    distanceFactor = hitRule.DistanceFactor;
                    coverType = hitRule.ResultLos;
                }

                // ★ v3.26.0: Dodge/Parry 추정 → 실질 명중률 계산
                int dodgeChance = EstimateDodgeChance(target, attacker, ability);
                int parryChance = EstimateParryChance(target, attacker, ability);
                // Helper: CombatAPI.UnitQueries.cs
                int effectiveHitChance = CalculateEffectiveHitChance(rawHitChance, dodgeChance, parryChance);

                var result = new HitChanceInfo
                {
                    HitChance = effectiveHitChance,        // 실질 명중률
                    RawBSHitChance = rawHitChance,         // 원본 보존
                    EstimatedDodgeChance = dodgeChance,
                    EstimatedParryChance = parryChance,
                    DistanceFactor = distanceFactor,
                    CoverType = coverType
                };

                if (Main.IsDebugEnabled)
                    Main.LogDebug($"[CombatAPI] HitChance: {attacker.CharacterName} -> {target.CharacterName}: " +
                        $"BS={rawHitChance}% dodge={dodgeChance}% parry={parryChance}% → effective={effectiveHitChance}%");

                return result;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetHitChance error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ★ v3.6.7: 특정 위치에서 공격 시 명중률 계산 (이동 계획용)
        /// </summary>
        public static HitChanceInfo GetHitChanceFromPosition(
            AbilityData ability,
            BaseUnitEntity attacker,
            Vector3 attackerPosition,
            BaseUnitEntity target)
        {
            if (ability == null || attacker == null || target == null)
                return null;

            try
            {
                int rawHitChance;
                float distanceFactor = 1.0f;
                var coverType = LosCalculations.CoverType.None;

                // ★ v3.6.8: 근접/Scatter 공격은 BS 100%
                if (ability.IsMelee || ability.IsScatter)
                {
                    rawHitChance = 100;
                }
                else
                {
                    var hitRule = new RuleCalculateHitChances(
                        attacker, target, ability,
                        0,
                        attackerPosition,  // 가상 위치에서 계산
                        target.Position
                    );
                    Rulebook.Trigger(hitRule);

                    rawHitChance = hitRule.ResultHitChance;
                    distanceFactor = hitRule.DistanceFactor;
                    coverType = hitRule.ResultLos;
                }

                // ★ v3.26.0: Dodge/Parry 추정 → 실질 명중률
                int dodgeChance = EstimateDodgeChance(target, attacker, ability);
                int parryChance = EstimateParryChance(target, attacker, ability);
                // Helper: CombatAPI.UnitQueries.cs
                int effectiveHitChance = CalculateEffectiveHitChance(rawHitChance, dodgeChance, parryChance);

                return new HitChanceInfo
                {
                    HitChance = effectiveHitChance,
                    RawBSHitChance = rawHitChance,
                    EstimatedDodgeChance = dodgeChance,
                    EstimatedParryChance = parryChance,
                    DistanceFactor = distanceFactor,
                    CoverType = coverType
                };
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetHitChanceFromPosition error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ★ v3.6.7: 거리 계수만 빠르게 계산 (이동 계획 최적화용)
        /// - 1.0 = 최대 사거리의 절반 이내 (최적)
        /// - 0.5 = 절반 초과 ~ 최대 사거리 (명중률 절반)
        /// - 0.0 = 최대 사거리 초과 (명중 불가)
        /// </summary>
        public static float GetDistanceFactor(AbilityData ability, Vector3 attackerPos, Vector3 targetPos)
        {
            if (ability == null) return 0f;

            try
            {
                // 무기 최대 사거리 (타일 단위)
                int maxRange = ability.RangeCells;
                if (maxRange <= 0 || maxRange >= 1000) return 1.0f;  // Unlimited

                // 실제 거리 (타일 단위)
                float distanceTiles = GetDistanceInTiles(attackerPos, targetPos);

                // 거리 계수 계산 (게임 로직 동일)
                float halfRange = maxRange / 2f;
                if (distanceTiles <= halfRange)
                    return 1.0f;  // 최적 거리
                else if (distanceTiles <= maxRange)
                    return 0.5f;  // 절반 거리
                else
                    return 0.0f;  // 사거리 초과
            }
            catch
            {
                return 1.0f;
            }
        }

        /// <summary>
        /// ★ v3.6.7: 최적 사거리(명중률 100% 적용) 타일 수 반환
        /// </summary>
        public static float GetOptimalRangeInTiles(AbilityData ability)
        {
            if (ability == null) return 0f;

            try
            {
                int maxRange = ability.RangeCells;
                if (maxRange <= 0 || maxRange >= 1000) return 1000f;  // Unlimited
                return maxRange / 2f;  // 최적 = 최대 사거리의 절반
            }
            catch
            {
                return 10f;  // 폴백
            }
        }

        #endregion

        #region Flanking API (v3.28.0)

        // ─── ★ v3.28.0: 플랭킹 (공격 방향) API ─────────────────────────────
        // CustomGraphHelper.GetWarhammerAttackSide()를 래핑하여 AI 포지셔닝에 활용

        /// <summary>공격 방향의 전투 측면 판정 (Front/Left/Right/Back)</summary>
        public static WarhammerCombatSide GetAttackSide(BaseUnitEntity target, Vector3 attackerPosition)
        {
            try
            {
                Vector3 attackDir = (target.Position - attackerPosition).normalized;
                return CustomGraphHelper.GetWarhammerAttackSide(target.Forward, attackDir, target.Size);
            }
            catch
            {
                return WarhammerCombatSide.Front;
            }
        }

        /// <summary>
        /// 플랭킹 보너스 점수 (Back=1.0, Side=0.5, Front=0.0)
        /// 포지셔닝 및 타겟 스코어링에서 후방/측면 공격 보너스 부여용
        /// </summary>
        public static float GetFlankingBonus(BaseUnitEntity target, Vector3 attackerPosition)
        {
            var side = GetAttackSide(target, attackerPosition);
            switch (side)
            {
                case WarhammerCombatSide.Back: return 1.0f;
                case WarhammerCombatSide.Left:
                case WarhammerCombatSide.Right: return 0.5f;
                default: return 0f;
            }
        }

        #endregion
    }
}
