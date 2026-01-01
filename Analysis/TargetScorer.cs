using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using CompanionAI_v3.Core;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;

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
            SpecialRole = 0.5f    // 중간
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
            SpecialRole = 0.3f    // 낮음
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
            SpecialRole = 0.9f    // 높음 - Healer/Caster 우선
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
            float score = 50f;  // 기본 점수

            try
            {
                // 1. HP% 점수 (낮을수록 높음)
                float hpPercent = CombatAPI.GetHPPercent(target);
                float hpScore = (100f - hpPercent) * 0.5f;  // 0~50
                score += hpScore * weights.HPPercent;

                // 2. 거리 점수 (가까울수록 좋음, but Role별 차이)
                float distance = CombatAPI.GetDistance(situation.Unit, target);
                float distanceScore = -distance * 2f;  // 거리 패널티

                // Tank는 근접 보너스
                if (role == AIRole.Tank && distance <= 5f)
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

                // 7. 특수 역할 (Healer/Caster)
                if (IsHealer(target))
                    score += 20f * weights.SpecialRole;
                if (IsCaster(target))
                    score += 15f * weights.SpecialRole;
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
                var scored = candidates
                    .Where(t => t != null)
                    .Where(t => {
                        try { return t.LifeState?.IsDead != true; }
                        catch { return true; }
                    })
                    .Select(t => new { Target = t, Score = ScoreEnemy(t, situation, role) })
                    .OrderByDescending(x => x.Score)
                    .FirstOrDefault();

                if (scored != null)
                {
                    Main.LogDebug($"[TargetScorer] Best enemy for {role}: {scored.Target.CharacterName} (score={scored.Score:F1})");
                    return scored.Target;
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
                // 1. HP% (낮을수록 힐 우선)
                float hpPercent = CombatAPI.GetHPPercent(ally);
                if (hpPercent < 25f) score += 80f * weights.HPPercent;
                else if (hpPercent < 50f) score += 50f * weights.HPPercent;
                else if (hpPercent < 75f) score += 20f * weights.HPPercent;
                else score -= 30f;  // 힐 불필요

                // 2. 거리 패널티
                float distance = CombatAPI.GetDistance(situation.Unit, ally);
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

                // 4. 위험 상태 (적과 가까움)
                float allyNearestEnemyDist = GetNearestEnemyDistance(ally, situation);
                if (allyNearestEnemyDist < 5f)
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
                var scored = allies
                    .Where(a => a != null)
                    .Where(a => {
                        try { return a.LifeState?.IsDead != true; }
                        catch { return true; }
                    })
                    .Where(a => CombatAPI.GetHPPercent(a) < hpThreshold)
                    .Select(a => new { Ally = a, Score = ScoreAllyForHealing(a, situation) })
                    .OrderByDescending(x => x.Score)
                    .FirstOrDefault();

                if (scored != null)
                {
                    Main.LogDebug($"[TargetScorer] Best ally for healing: {scored.Ally.CharacterName} (score={scored.Score:F1})");
                    return scored.Ally;
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
                // 버프는 역할 우선순위 중시, HP는 덜 중요
                var scored = allies
                    .Where(a => a != null)
                    .Where(a => {
                        try { return a.LifeState?.IsDead != true && a.IsConscious; }
                        catch { return true; }
                    })
                    .Select(a => new { Ally = a, Score = GetBuffPriority(a, situation) })
                    .OrderByDescending(x => x.Score)
                    .FirstOrDefault();

                if (scored != null)
                {
                    Main.LogDebug($"[TargetScorer] Best ally for buff: {scored.Ally.CharacterName} (score={scored.Score:F1})");
                    return scored.Ally;
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
        /// 위협도 평가 (0.0 ~ 1.0)
        /// </summary>
        private static float EvaluateThreat(BaseUnitEntity target, Situation situation)
        {
            float threat = 0.5f;

            try
            {
                // 무기 체크
                var weapon = target.Body?.PrimaryHand?.Weapon;
                if (weapon != null)
                {
                    // 원거리 무기 = 더 위협적
                    if (!weapon.Blueprint.IsMelee) threat += 0.2f;

                    // 긴 사거리 = 더 위협적
                    int range = weapon.AttackRange;
                    if (range > 10) threat += 0.2f;
                }

                // 거리 - 가까우면 더 위협적
                float distance = CombatAPI.GetDistance(situation.Unit, target);
                if (distance <= 5f) threat += 0.2f;

                // 저HP = 덜 위협적
                float hpPercent = CombatAPI.GetHPPercent(target);
                if (hpPercent < 30f) threat -= 0.2f;
            }
            catch { }

            return Math.Max(0f, Math.Min(1f, threat));
        }

        /// <summary>
        /// 유닛이 Healer인지 확인
        /// </summary>
        private static bool IsHealer(BaseUnitEntity unit)
        {
            try
            {
                var abilities = unit.Abilities?.Enumerable;
                if (abilities == null) return false;

                return abilities.Any(a => a?.Data != null &&
                    AbilityDatabase.IsHealing(a.Data));
            }
            catch { return false; }
        }

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
        /// 유닛에 해로운 디버프가 있는지 확인
        /// </summary>
        private static bool HasHarmfulDebuff(BaseUnitEntity unit)
        {
            try
            {
                var buffs = unit.Buffs?.Enumerable;
                if (buffs == null) return false;

                // ★ v3.1.21: IsHarmful 대신 SpellDescriptor 또는 적 캐스터 여부로 판단
                return buffs.Any(b => {
                    var bp = b.Blueprint;
                    if (bp == null) return false;

                    // 적으로부터 받은 버프 = 디버프일 가능성 높음
                    var caster = b.Context?.MaybeCaster;
                    if (caster != null && unit.CombatGroup?.IsEnemy(caster) == true)
                        return true;

                    // Harmful 키워드 체크 (이름 기반 폴백)
                    var name = bp.name?.ToLower() ?? "";
                    return name.Contains("debuff") || name.Contains("stun") ||
                           name.Contains("blind") || name.Contains("bleed") ||
                           name.Contains("poison") || name.Contains("slow");
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
                return situation.Enemies
                    .Where(e => e != null)
                    .Where(e => {
                        try { return e.LifeState?.IsDead != true; }
                        catch { return true; }
                    })
                    .Select(e => CombatAPI.GetDistance(ally, e))
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

                // 낮은 HP = 높은 우선순위 (보호 필요)
                float hpPercent = CombatAPI.GetHPPercent(ally);
                if (hpPercent < 50f)
                    priority += 15f;
            }
            catch { }

            return priority;
        }

        #endregion
    }
}
