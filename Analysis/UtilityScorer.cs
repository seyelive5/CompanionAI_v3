using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using CompanionAI_v3.Core;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;
using UnityEngine;

namespace CompanionAI_v3.Analysis
{
    /// <summary>
    /// ★ v3.0.44: Utility AI 스코어링 시스템
    /// 각 행동에 점수를 부여하여 최적의 행동 선택
    /// </summary>
    public static class UtilityScorer
    {
        #region Combat Phase Detection

        /// <summary>
        /// 전투 페이즈 (초반/중반/정리/위기)
        /// </summary>
        public enum CombatPhase
        {
            Opening,    // 초반 - 버프 중시
            Midgame,    // 중반 - 균형
            Cleanup,    // 정리 - 마무리 중시
            Desperate   // 위기 - 생존 중시
        }

        /// <summary>
        /// 현재 전투 페이즈 감지
        /// ★ v3.5.00: ThresholdConfig 적용
        /// </summary>
        public static CombatPhase DetectPhase(Situation situation)
        {
            var t = AIConfig.GetThresholds();

            // ★ v3.0.46: 아군 평균 HP 계산 (LifeState null 체크 추가)
            float allyAvgHP = 100f;
            if (situation.Allies != null && situation.Allies.Count > 0)
            {
                allyAvgHP = situation.Allies
                    .Where(a => a != null && a.LifeState != null && !a.LifeState.IsDead)
                    .Select(a => CombatAPI.GetHPPercent(a))
                    .DefaultIfEmpty(100f)
                    .Average();
            }

            // ★ v3.5.00: ThresholdConfig에서 임계값 읽기
            // 위기 상황: 아군 평균 HP가 낮거나 본인 HP가 위험
            if (allyAvgHP < t.DesperatePhaseHP || situation.HPPercent < t.DesperateSelfHP)
                return CombatPhase.Desperate;

            // 정리 단계: 적 수가 기준 이하
            if ((situation.Enemies?.Count ?? 0) <= t.CleanupEnemyCount)
                return CombatPhase.Cleanup;

            // 초반: 첫 턴 또는 버프 전
            if (!situation.HasBuffedThisTurn && !situation.HasAttackedThisTurn && situation.CurrentAP >= t.OpeningPhaseMinAP)
                return CombatPhase.Opening;

            return CombatPhase.Midgame;
        }

        #endregion

        #region Buff Scoring

        /// <summary>
        /// 버프의 유용성 점수 계산
        /// ★ v3.1.30: Response Curves 적용
        /// </summary>
        public static float ScoreBuff(AbilityData buff, Situation situation)
        {
            if (buff == null) return -1000f;

            float score = 50f;  // 기본 점수

            // ★ v3.8.40: 잠재력 초월(FreeUltimateBuff) 활성 시 궁극기 상세 점수 시스템
            bool hasFreeUltimateBuff = CombatAPI.HasFreeUltimateBuff(situation.Unit);
            bool isUltimate = CombatAPI.IsUltimateAbility(buff);

            if (hasFreeUltimateBuff)
            {
                if (isUltimate)
                {
                    // 궁극기 기본 보너스 (추가 턴은 궁극기 사용을 위한 것)
                    score += 500f;

                    // ★ v3.8.40: 상세 분류 기반 점수
                    score += ScoreUltimateByType(buff, situation);
                }
                else
                {
                    // 궁극기가 아님 = 큰 감점 (WarhammerAbilityRestriction으로 제한될 것)
                    score -= 1000f;
                    Main.LogDebug($"[UtilityScorer] {buff.Name}: Non-ultimate during FreeUltimate turn - skipped");
                }
            }

            // ★ v3.1.30: AP 효율 Response Curve 적용
            float cost = CombatAPI.GetAbilityAPCost(buff);
            score += CurvePresets.BuffAPCost.Evaluate(cost);

            // ★ 타이밍 적합성
            var timing = AbilityDatabase.GetTiming(buff);
            var phase = DetectPhase(situation);

            switch (timing)
            {
                case AbilityTiming.PreCombatBuff:
                    // 초반에 선제 버프 높은 점수
                    if (phase == CombatPhase.Opening) score += 30f;
                    else if (phase == CombatPhase.Cleanup) score -= 20f;  // 정리 단계에선 불필요
                    break;

                case AbilityTiming.PreAttackBuff:
                    // ★ v3.1.10: 공격 가능할 때만 높은 점수
                    // 사용 가능한 공격이 없으면 (쿨다운 등) 사용 불가
                    bool hasAvailableAttacks = situation.AvailableAttacks != null && situation.AvailableAttacks.Count > 0;
                    if (!hasAvailableAttacks)
                    {
                        score = -1000f;  // 사용 불가
                    }
                    else if (situation.HasHittableEnemies)
                    {
                        score += 25f;
                    }
                    else
                    {
                        score -= 10f;  // 적이 범위 밖
                    }
                    break;

                case AbilityTiming.HeroicAct:
                    // ★ v3.1.10: 사용 가능한 공격이 없으면 사용 불가
                    bool hasAttacksForHeroic = situation.AvailableAttacks != null && situation.AvailableAttacks.Count > 0;
                    if (!hasAttacksForHeroic)
                    {
                        score = -1000f;
                    }
                    else
                    {
                        // 강력한 능력 - 많은 적이 있을 때 유리
                        int enemyCount = situation.Enemies?.Count ?? 0;
                        if (enemyCount >= 4) score += 30f;
                        else if (enemyCount <= 2) score -= 10f;
                    }
                    break;

                case AbilityTiming.RighteousFury:
                    // ★ v3.1.10: PreAttackBuff와 동일하게 처리
                    bool hasAttacksForFury = situation.AvailableAttacks != null && situation.AvailableAttacks.Count > 0;
                    if (!hasAttacksForFury)
                    {
                        score = -1000f;
                    }
                    else if (situation.HasHittableEnemies)
                    {
                        score += 20f;
                    }
                    break;

                case AbilityTiming.SelfDamage:
                    // ★ v3.5.00: ThresholdConfig 적용
                    var selfDmgThresholds = AIConfig.GetThresholds();
                    // HP가 충분할 때만
                    if (situation.HPPercent >= selfDmgThresholds.SelfDamageMinHP) score += 10f;
                    else if (situation.HPPercent < selfDmgThresholds.PreAttackBuffMinHP) score -= 30f;
                    break;

                case AbilityTiming.Emergency:
                    // 위기 상황에서 높은 점수
                    if (phase == CombatPhase.Desperate) score += 40f;
                    else score -= 20f;
                    break;

                case AbilityTiming.Taunt:
                    // ★ v3.5.00: ThresholdConfig 적용
                    var tauntThresholds = AIConfig.GetThresholds();
                    // 근접 적 다수일 때
                    int nearbyEnemies = situation.Enemies?.Count(e =>
                        e != null && CombatAPI.GetDistance(situation.Unit, e) <= tauntThresholds.ThreatProximity) ?? 0;
                    if (nearbyEnemies >= 2) score += 25f;
                    else score -= 15f;
                    break;

                case AbilityTiming.PostFirstAction:
                    // ★ v3.6.2: Break Through 등 - 첫 공격 후 0 AP 공격 활성화
                    // 근접 적이 있으면 좋음 (Slash 사용 가능), 없으면 나쁨 (1.5타일 ≈ 2m)
                    bool hasNearbyEnemyForPost = situation.Enemies?.Any(e =>
                        e != null && CombatCache.GetDistanceInTiles(situation.Unit, e) <= 1.5f) ?? false;

                    if (hasNearbyEnemyForPost)
                        score += 40f;  // Charge와 비슷한 수준으로 보너스
                    else
                        score -= 50f;  // 적에게 도달 못하면 쓸모없음
                    break;
            }

            // ★ 공격 시너지: 공격 가능한 적이 있고 공격적 버프면 보너스
            if (situation.HasHittableEnemies && IsOffensiveBuff(buff))
                score += 20f;

            // ★ 방어 시너지: 위험 상황에서 방어적 버프면 보너스
            if (situation.IsInDanger && IsDefensiveBuff(buff))
                score += 25f;

            // ★ 중복 패널티: 이미 활성화된 버프
            if (CombatAPI.HasActiveBuff(situation.Unit, buff))
                score -= 100f;

            // ★ 이미 버프한 턴에는 추가 버프 약간 감점 (한 턴에 너무 많은 버프 방지)
            if (situation.HasBuffedThisTurn)
                score -= 10f;

            // ★ 페이즈별 가중치
            switch (phase)
            {
                case CombatPhase.Opening:
                    score *= 1.3f;  // 초반엔 버프 가중치 UP
                    break;
                case CombatPhase.Cleanup:
                    score *= 0.7f;  // 정리 단계엔 버프 가중치 DOWN
                    break;
                case CombatPhase.Desperate:
                    if (!IsDefensiveBuff(buff)) score *= 0.5f;  // 위기엔 방어 버프만
                    break;
            }

            return score;
        }

        /// <summary>
        /// 공격적 버프인가? (데미지 증가, 명중률 증가 등)
        /// </summary>
        private static bool IsOffensiveBuff(AbilityData buff)
        {
            if (buff == null) return false;
            var timing = AbilityDatabase.GetTiming(buff);
            return timing == AbilityTiming.PreAttackBuff || timing == AbilityTiming.HeroicAct;
        }

        /// <summary>
        /// 방어적 버프인가? (방어력 증가, 회피 등)
        /// </summary>
        private static bool IsDefensiveBuff(AbilityData buff)
        {
            if (buff == null) return false;
            var timing = AbilityDatabase.GetTiming(buff);
            return timing == AbilityTiming.PreCombatBuff || timing == AbilityTiming.Emergency;
        }

        /// <summary>
        /// ★ v3.8.40: 궁극기 유형별 상세 점수 계산
        /// 전투당 한 번만 사용 가능하므로 상황에 맞는 최적의 궁극기 선택 필요
        /// </summary>
        private static float ScoreUltimateByType(AbilityData ultimate, Situation situation)
        {
            float score = 0f;
            var info = CombatAPI.GetUltimateInfo(ultimate);

            // 상황 변수 수집
            int livingEnemies = situation.Enemies?.Count(e => e != null && e.IsConscious) ?? 0;
            int livingAllies = situation.Allies?.Count(a => a != null && a.IsConscious) ?? 0;
            float hpPercent = situation.HPPercent;
            bool isInDanger = situation.IsInDanger;
            float nearestEnemyDist = situation.NearestEnemyDistance;
            int hittableEnemies = situation.HittableEnemies?.Count ?? 0;

            // 근접 적 수 (3m 이내)
            int nearbyEnemies = situation.Enemies?.Count(e =>
                e != null && e.IsConscious &&
                CombatAPI.GetDistance(situation.Unit, e) <= 4.5f) ?? 0;

            // HP 낮은 아군 수
            int lowHPAllies = situation.Allies?.Count(a =>
                a != null && a.IsConscious && a != situation.Unit &&
                CombatAPI.GetHPPercent(a) < 50f) ?? 0;

            // AOE 범위 내 적 수 계산 (AOE 궁극기용)
            int enemiesInAoERange = 0;
            if (info.IsAoE && info.AoERadius > 0)
            {
                var clusters = ClusterDetector.FindClusters(
                    situation.Enemies.Where(e => e != null && e.IsConscious).ToList(),
                    info.AoERadius);
                enemiesInAoERange = clusters.Any() ? clusters.Max(c => c.Count) : 0;
            }

            Main.LogDebug($"[UtilityScorer] Ultimate {ultimate.Name}: Type={info.Type}, " +
                $"HP={hpPercent:F0}%, Danger={isInDanger}, Enemies={livingEnemies}, " +
                $"Nearby={nearbyEnemies}, LowHPAllies={lowHPAllies}, AoETargets={enemiesInAoERange}");

            switch (info.Type)
            {
                case CombatAPI.UltimateType.Defensive:
                    // ========================================
                    // 방어적 궁극기: HP 낮거나 위험할 때 우선
                    // ========================================

                    // HP 기반 점수 (HP 낮을수록 높음)
                    if (hpPercent < 30f)
                        score += 150f;  // 위급
                    else if (hpPercent < 50f)
                        score += 100f;  // 위험
                    else if (hpPercent < 70f)
                        score += 50f;   // 주의
                    else
                        score -= 50f;   // HP 충분하면 감점

                    // 위험 상황 보너스
                    if (isInDanger)
                        score += 80f;

                    // 근접 적 수 기반 (둘러싸임)
                    score += nearbyEnemies * 30f;

                    // DesperateMeasure는 위기 상황에서 더 높은 점수
                    if (info.IsDesperateMeasure && hpPercent < 40f)
                        score += 100f;

                    Main.LogDebug($"[UtilityScorer] {ultimate.Name}: DEFENSIVE score={score:F0}");
                    break;

                case CombatAPI.UltimateType.OffensiveSingle:
                    // ========================================
                    // 단일 타겟 공격: 고가치 타겟 존재 시 우선
                    // ========================================

                    // 공격 가능 적 있으면 기본 점수
                    if (hittableEnemies > 0)
                        score += 80f;
                    else
                        score -= 100f;  // 공격 불가면 큰 감점

                    // 적 수 적을 때 (보스전 등) 더 효율적
                    if (livingEnemies <= 2)
                        score += 50f;   // 소수 정예전

                    // HP가 너무 낮으면 공격보다 생존 우선
                    if (hpPercent < 30f)
                        score -= 80f;

                    Main.LogDebug($"[UtilityScorer] {ultimate.Name}: SINGLE OFFENSIVE score={score:F0}");
                    break;

                case CombatAPI.UltimateType.OffensiveAoE:
                    // ========================================
                    // AOE 공격: 다수 적 밀집 시 우선
                    // ========================================

                    // AOE 범위 내 적 수 기반 (핵심 요소)
                    if (enemiesInAoERange >= 4)
                        score += 200f;  // 대규모 집단
                    else if (enemiesInAoERange >= 3)
                        score += 150f;  // 좋은 기회
                    else if (enemiesInAoERange >= 2)
                        score += 80f;   // 괜찮음
                    else
                        score -= 50f;   // 단일 타겟에 AOE는 낭비

                    // 전체 적 수 보너스
                    score += livingEnemies * 15f;

                    // HP가 너무 낮으면 감점
                    if (hpPercent < 30f)
                        score -= 60f;

                    Main.LogDebug($"[UtilityScorer] {ultimate.Name}: AOE OFFENSIVE score={score:F0} (AoETargets={enemiesInAoERange})");
                    break;

                case CombatAPI.UltimateType.Support:
                    // ========================================
                    // 지원 궁극기: 아군이 위험하거나 다수일 때 우선
                    // ========================================

                    // HP 낮은 아군 수 기반
                    score += lowHPAllies * 60f;

                    // 아군 수 기반
                    score += livingAllies * 20f;

                    // 아군이 없으면 의미 없음
                    if (livingAllies == 0)
                        score -= 200f;

                    // 자신의 HP가 위급하면 지원보다 생존
                    if (hpPercent < 25f)
                        score -= 100f;

                    Main.LogDebug($"[UtilityScorer] {ultimate.Name}: SUPPORT score={score:F0} (lowHPAllies={lowHPAllies})");
                    break;

                case CombatAPI.UltimateType.Mobility:
                    // ========================================
                    // 이동 궁극기: 적이 멀거나 위치 조정 필요 시 우선
                    // ========================================

                    // 적이 멀면 이동 필요
                    if (nearestEnemyDist > 10f)
                        score += 100f;
                    else if (nearestEnemyDist > 5f)
                        score += 50f;
                    else
                        score -= 30f;  // 이미 가까우면 불필요

                    // 공격 불가 상태면 이동 필요
                    if (hittableEnemies == 0 && livingEnemies > 0)
                        score += 80f;

                    // 위험 상황에서 탈출용으로도 가치
                    if (isInDanger && nearbyEnemies >= 2)
                        score += 60f;

                    Main.LogDebug($"[UtilityScorer] {ultimate.Name}: MOBILITY score={score:F0} (dist={nearestEnemyDist:F1})");
                    break;

                default:
                    // 알 수 없는 유형 = 기본 점수
                    score += 30f;
                    Main.LogDebug($"[UtilityScorer] {ultimate.Name}: UNKNOWN type, default score");
                    break;
            }

            // HeroicAct vs DesperateMeasure 추가 조정
            // HeroicAct: 공격적 상황에서 보너스
            // DesperateMeasure: 방어적 상황에서 보너스
            if (info.IsHeroicAct && livingEnemies >= 3)
                score += 30f;
            if (info.IsDesperateMeasure && (hpPercent < 50f || isInDanger))
                score += 40f;

            return score;
        }

        /// <summary>
        /// 버프 리스트에서 최적 버프 선택
        /// </summary>
        public static AbilityData SelectBestBuff(List<AbilityData> buffs, Situation situation)
        {
            if (buffs == null || buffs.Count == 0) return null;

            return buffs
                .Select(b => new { Buff = b, Score = ScoreBuff(b, situation) })
                .Where(x => x.Score > 0)  // 양수 점수만
                .OrderByDescending(x => x.Score)
                .Select(x => x.Buff)
                .FirstOrDefault();
        }

        #endregion

        #region Attack Scoring

        /// <summary>
        /// 공격 능력의 유용성 점수 계산
        /// ★ v3.0.56: ClearMPAfterUse + 위험 상황 패널티 추가
        /// </summary>
        public static float ScoreAttack(AbilityData attack, BaseUnitEntity target, Situation situation)
        {
            if (attack == null || target == null) return -1000f;

            float score = 50f;  // 기본 점수

            // ★ v3.0.56: ClearMPAfterUse + 위험 상황 = 대폭 감점
            // 이 능력 사용 후 이동 불가 → 위험 상황에서 사용하면 위험
            bool clearsMPAfterUse = CombatAPI.AbilityClearsMPAfterUse(attack);
            if (clearsMPAfterUse)
            {
                // 역할별 안전 가중치 적용
                float safetyWeight = GetRoleSafetyWeight(situation);

                // 위험 상황 (적이 가까움) + MP 클리어 능력 = 감점
                if (situation.IsInDanger)
                {
                    float dangerPenalty = 60f * safetyWeight;  // Support는 -48점, Tank는 -12점
                    score -= dangerPenalty;
                    Main.LogDebug($"[UtilityScorer] {attack.Name}: ClearMP + InDanger penalty={dangerPenalty:F0} (safetyWeight={safetyWeight:F1})");
                }

                // 근접 적 거리 기반 추가 감점
                if (situation.NearestEnemyDistance < situation.MinSafeDistance)
                {
                    float proximityPenalty = (situation.MinSafeDistance - situation.NearestEnemyDistance) * 5f * safetyWeight;
                    score -= proximityPenalty;
                }
            }

            // ★ v3.1.30: Response Curves 기반 데미지 점수
            float estimatedDamage = CombatAPI.EstimateDamage(attack, target);
            float targetHP = CombatAPI.GetActualHP(target);
            float damageRatio = estimatedDamage / Mathf.Max(targetHP, 1f);

            // 데미지 비율 → 점수 (Logistic 곡선)
            score += CurvePresets.DamageRatio.Evaluate(damageRatio);

            // 1타킬 보너스 (Exponential 곡선, ratio 0.8 이상에서 급격히 상승)
            if (damageRatio >= 0.8f)
            {
                score += CurvePresets.OneHitKillBonus.Evaluate(damageRatio);
            }

            // ★ v3.1.30: AP 효율 Response Curve
            float cost = CombatAPI.GetAbilityAPCost(attack);
            float damagePerAP = estimatedDamage / Mathf.Max(cost, 0.5f);
            score += CurvePresets.APEfficiency.Evaluate(damagePerAP);

            // ★ v3.6.2: 거리 적합성 - 타일 단위로 통일
            float distanceTiles = CombatCache.GetDistanceInTiles(situation.Unit, target);
            if (attack.IsMelee)
            {
                if (distanceTiles <= 1.5f)  // 1.5타일 ≈ 2m
                    score += 15f;   // 근접 범위 = 좋음
                else if (distanceTiles <= 4f)  // 4타일 ≈ 5.4m (일반 이동 거리)
                    score -= (distanceTiles - 1.5f) * 2f;  // 최대 -5점 (완화)
                else if (distanceTiles <= 11f)  // 11타일 ≈ 15m (GapCloser 범위)
                    score -= 10f + (distanceTiles - 4f) * 1.5f;  // 최대 -20.5점
                else
                    score -= distanceTiles * 2f;  // 먼 거리 (완화)
            }
            else
            {
                // 원거리: 적정 거리에서 보너스 (4~11타일 ≈ 5~15m)
                if (distanceTiles >= 4f && distanceTiles <= 11f) score += 10f;
                else if (distanceTiles < 2f) score -= 10f;  // 너무 가까우면 감점 (2타일 ≈ 2.7m)
            }

            // ★ RangePreference 적합성
            var preference = situation.RangePreference;
            if (preference == RangePreference.PreferRanged)
            {
                if (!attack.IsMelee) score += 15f;
                else score -= 20f;  // 원거리 선호인데 근접 공격
            }
            else if (preference == RangePreference.PreferMelee)
            {
                if (attack.IsMelee) score += 15f;
                else score -= 10f;
            }

            // ★ v3.7.89: AOO (기회공격) 회피 점수
            // 위협 범위 내에서 AOO를 유발하는 능력은 감점
            if (CombatAPI.IsInThreateningArea(situation.Unit))
            {
                var aooStatus = CombatAPI.CheckAOOStatus(attack, situation.Unit);
                if (aooStatus.WillTriggerAOO)
                {
                    // AOO 유발 시 감점 (위협하는 적 수에 비례)
                    float aooPenalty = 15f + (aooStatus.ThreateningEnemyCount * 10f);
                    score -= aooPenalty;
                    Main.LogDebug($"[UtilityScorer] {attack.Name}: AOO penalty={aooPenalty:F0} " +
                        $"({aooStatus.ThreateningEnemyCount} threatening enemies)");
                }
                else if (aooStatus.IsSafe)
                {
                    // AOO 회피 가능 = 약간의 보너스
                    score += 5f;
                }
            }

            // ★ 특수 타이밍 고려
            // ★ v3.5.00: ThresholdConfig 적용
            var attackThresholds = AIConfig.GetThresholds();
            var timing = AbilityDatabase.GetTiming(attack);
            float targetHPPercent = CombatAPI.GetHPPercent(target);

            if (timing == AbilityTiming.Finisher)
            {
                if (targetHPPercent <= attackThresholds.FinisherTargetHP) score += 40f;  // 마무리 대상
                else score -= 30f;  // 마무리 아니면 비효율
            }

            // ★ v3.5.85: AOE 보너스 (IsAoE() 대신 패턴 기반 판단)
            // 점사 사격처럼 IsAoE()=false이지만 다수 타격 가능한 능력 지원
            // ★ v3.5.87: Self-Targeted AOE는 캐스터 위치 기준
            bool useAoEOptimization = situation.CharacterSettings?.UseAoEOptimization ?? true;
            if (useAoEOptimization && situation.Enemies != null && situation.Enemies.Count >= 2)
            {
                // Self-Targeted AOE (BladeDance 등)는 캐스터 위치 기준으로 적 수 계산
                Vector3 patternCenter = CombatAPI.IsSelfTargetedAoEAttack(attack)
                    ? situation.Unit.Position
                    : target.Position;

                int enemiesInPattern = CombatAPI.CountEnemiesInPattern(
                    attack,
                    patternCenter,
                    situation.Unit.Position,
                    situation.Enemies);

                // 2명 이상 맞추면 AOE 보너스 적용
                if (enemiesInPattern >= 2)
                {
                    int additionalEnemies = enemiesInPattern - 1;
                    score += additionalEnemies * 15f;       // 추가 적당 +15
                    Main.LogDebug($"[UtilityScorer] AOE {attack.Name} -> {target.CharacterName}: " +
                        $"hits {enemiesInPattern} enemies (+{additionalEnemies} additional) = +{additionalEnemies * 15f:F0}");
                }
            }

            // ★ v3.6.16: 모든 Point AOE 능력에 아군 체크 적용
            // DangerousAoE뿐만 아니라 Point 타겟 AOE (플라스마 과충전 등)도 포함
            // ★ v3.8.11: Directional 패턴은 실제 방향 기준 아군 체크
            // ★ v3.8.12: AIConfig.MaxPlayerAlliesHit 설정 반영
            float aoeRadius = CombatAPI.GetAoERadius(attack);
            bool isPointAoE = CombatAPI.IsPointTargetAbility(attack) && aoeRadius > 0f;
            bool isDangerousAoE = AbilityDatabase.IsDangerousAoE(attack);

            if (isPointAoE || isDangerousAoE)
            {
                float checkRadius = aoeRadius > 0f ? aoeRadius : 3f;
                int alliesInDanger = 0;

                // ★ v3.8.12: 설정에서 최대 허용 아군 수 가져오기
                var aoeConfig = AIConfig.GetAoEConfig();
                int maxAlliesAllowed = aoeConfig?.MaxPlayerAlliesHit ?? 1;

                // ★ v3.8.11: Directional 패턴은 방향 기반 체크
                var patternInfo = CombatAPI.GetPatternInfo(attack);
                bool isActuallyDirectional = patternInfo?.IsDirectional ?? false;
                var patternType = patternInfo?.Type;

                if (isActuallyDirectional && patternType.HasValue)
                {
                    // Directional: caster → target 방향의 cone/ray 내 아군만 체크
                    Vector3 direction = (target.Position - situation.Unit.Position).normalized;
                    float angle = patternInfo.Angle;

                    foreach (var ally in situation.Allies)
                    {
                        if (ally == null || ally.LifeState.IsDead) continue;
                        if (ally == situation.Unit) continue;  // 자기 자신 제외

                        // 실제 패턴 내에 있는지 체크
                        if (CombatAPI.IsUnitInDirectionalAoERange(
                            situation.Unit.Position, direction, ally, checkRadius, angle, patternType.Value))
                        {
                            alliesInDanger++;
                        }
                    }

                    // ★ v3.8.12: 설정된 최대치 초과 시에만 차단
                    if (alliesInDanger > maxAlliesAllowed)
                    {
                        score -= 1000f;
                        Main.LogDebug($"[Scorer] Directional AOE ally check {attack.Name}: {alliesInDanger} allies > max {maxAlliesAllowed} - BLOCKED");
                    }
                    else if (alliesInDanger > 0)
                    {
                        // 허용 범위 내 아군 - 페널티만 적용
                        float penalty = (aoeConfig?.ClusterAllyPenalty ?? 40f) * alliesInDanger;
                        score -= penalty;
                        Main.LogDebug($"[Scorer] Directional AOE {attack.Name}: {alliesInDanger} allies (≤{maxAlliesAllowed}) - penalty {penalty:F0}");
                    }
                    else
                    {
                        Main.LogDebug($"[Scorer] Directional AOE {attack.Name}: No allies in {patternType} pattern direction - OK");
                    }
                }
                else
                {
                    // Non-directional (Circle): 기존 반경 체크
                    alliesInDanger = CountAlliesNear(target, situation, checkRadius);

                    // ★ v3.8.12: 설정된 최대치 초과 시에만 차단
                    if (alliesInDanger > maxAlliesAllowed)
                    {
                        score -= 1000f;
                        Main.LogDebug($"[Scorer] AOE ally check {attack.Name}: {alliesInDanger} allies > max {maxAlliesAllowed} - BLOCKED");
                    }
                    else if (alliesInDanger > 0)
                    {
                        // 허용 범위 내 아군 - 페널티만 적용
                        float penalty = (aoeConfig?.ClusterAllyPenalty ?? 40f) * alliesInDanger;
                        score -= penalty;
                        Main.LogDebug($"[Scorer] AOE {attack.Name}: {alliesInDanger} allies (≤{maxAlliesAllowed}) - penalty {penalty:F0}");
                    }
                }
            }

            return score;
        }

        #endregion

        #region Target Scoring

        /// <summary>
        /// 타겟의 우선순위 점수 계산
        /// ★ v3.1.30: Response Curves 적용
        /// </summary>
        public static float ScoreTarget(BaseUnitEntity target, Situation situation)
        {
            if (target == null || target.LifeState.IsDead) return -1000f;

            float score = 50f;  // 기본 점수

            // ★ v3.1.30: HP → 마무리 우선순위 Response Curve (낮은 HP 우선)
            float hpPercent = CombatAPI.GetHPPercent(target);
            score += CurvePresets.HPPriority.Evaluate(hpPercent);

            // ★ v3.1.30: 1타 킬 가능성 → OneHitKillBonus Curve
            if (situation.PrimaryAttack != null)
            {
                float estimatedDamage = CombatAPI.EstimateDamage(situation.PrimaryAttack, target);
                float targetHP = CombatAPI.GetActualHP(target);
                float damageRatio = estimatedDamage / Mathf.Max(targetHP, 1f);

                if (damageRatio >= 0.8f)
                {
                    score += CurvePresets.OneHitKillBonus.Evaluate(damageRatio);
                }
            }

            // ★ v3.1.30: 위협도 평가 → ThreatByDistance Curve
            float threat = EvaluateThreat(target, situation);
            score += threat * 30f;  // 기본 위협도 점수 유지

            // ★ v3.1.30: 거리 → DistancePenalty Response Curve
            float distance = CombatAPI.GetDistance(situation.Unit, target);
            score += CurvePresets.DistancePenalty.Evaluate(distance);

            // ★ 공격 가능 여부
            bool isHittable = situation.HittableEnemies?.Contains(target) ?? false;
            if (isHittable) score += 25f;
            else score -= 15f;  // 이동 필요

            // ★ 특수 역할 보너스
            if (IsHealer(target))
            {
                // 힐러 우선 제거 (아군 치료 방지)
                score += 20f;
                // 부상 아군이 있으면 더 급함
                if (situation.MostWoundedAlly != null && CombatAPI.GetHPPercent(situation.MostWoundedAlly) < 50f)
                    score += 15f;
            }

            if (IsCaster(target))
            {
                // 캐스터 우선 (고데미지 + 약한 방어)
                score += 15f;
            }

            return score;
        }

        /// <summary>
        /// 위협도 평가 (0~1)
        /// ★ v3.5.00: ThresholdConfig 적용
        /// </summary>
        private static float EvaluateThreat(BaseUnitEntity target, Situation situation)
        {
            var t = AIConfig.GetThresholds();
            float threat = 0.5f;  // 기본

            // 데미지 딜러 판단 (대략적)
            try
            {
                var weapon = target.Body?.PrimaryHand?.Weapon;
                if (weapon != null)
                {
                    // 원거리 무기 = 위협
                    if (!weapon.Blueprint.IsMelee) threat += 0.2f;
                    // 데미지 높으면 위협 (AttackRange로 대략 추정)
                    int range = weapon.AttackRange;
                    if (range > 10) threat += 0.2f;  // 장거리 무기
                }
            }
            catch { }

            // 가까이 있으면 더 위협적
            float distance = CombatAPI.GetDistance(situation.Unit, target);
            if (distance <= t.ThreatProximity) threat += 0.2f;

            // HP 낮은 적은 덜 위협적 (곧 죽음)
            float hpPercent = CombatAPI.GetHPPercent(target);
            if (hpPercent < t.LowThreatHP) threat -= 0.2f;

            return Math.Max(0f, Math.Min(1f, threat));
        }

        /// <summary>
        /// ★ v3.5.75: CombatHelpers.IsHealer()로 통합 (중복 제거)
        /// </summary>
        private static bool IsHealer(BaseUnitEntity unit)
            => CombatHelpers.IsHealer(unit);

        /// <summary>
        /// 캐스터인가? (사이커/마법사)
        /// </summary>
        private static bool IsCaster(BaseUnitEntity unit)
        {
            try
            {
                var abilities = unit.Abilities?.Enumerable;
                if (abilities == null) return false;
                return abilities.Any(a => a?.Data != null && AbilityDatabase.IsPsychic(a.Data));
            }
            catch { return false; }
        }

        /// <summary>
        /// 타겟 리스트에서 최적 타겟 선택
        /// </summary>
        public static BaseUnitEntity SelectBestTarget(List<BaseUnitEntity> targets, Situation situation)
        {
            if (targets == null || targets.Count == 0) return null;

            return targets
                .Where(t => t != null && !t.LifeState.IsDead)
                .Select(t => new { Target = t, Score = ScoreTarget(t, situation) })
                .OrderByDescending(x => x.Score)
                .Select(x => x.Target)
                .FirstOrDefault();
        }

        #endregion

        #region Heal Scoring

        /// <summary>
        /// 힐 능력의 유용성 점수 계산
        /// ★ v3.1.30: Response Curves 적용
        /// </summary>
        public static float ScoreHeal(AbilityData heal, BaseUnitEntity target, Situation situation)
        {
            if (heal == null || target == null) return -1000f;

            float score = 0f;

            // ★ v3.1.30: HP → 힐 긴급도 Response Curve (Sigmoid)
            float targetHP = CombatAPI.GetHPPercent(target);
            score += CurvePresets.HealUrgency.Evaluate(targetHP);

            // ★ v3.1.30: 자기 힐 보너스 Response Curve
            bool isSelfHeal = target == situation.Unit;
            if (isSelfHeal)
            {
                score += CurvePresets.SelfHealBonus.Evaluate(situation.HPPercent);
            }

            // ★ 위기 상황 보너스
            var phase = DetectPhase(situation);
            if (phase == CombatPhase.Desperate)
                score += 30f;

            // ★ AP 비용 고려
            float cost = CombatAPI.GetAbilityAPCost(heal);
            if (cost <= 1f) score += 10f;
            else if (cost >= 3f) score -= 15f;

            // ★ 힐 효율: 현재 손실 HP 대비
            float missingHP = 100f - targetHP;
            float expectedHeal = EstimateHealAmount(heal);
            float efficiency = Math.Min(expectedHeal / Math.Max(missingHP, 1f), 1f);
            score += efficiency * 20f;

            return score;
        }

        /// <summary>
        /// 예상 힐량 추정
        /// </summary>
        private static float EstimateHealAmount(AbilityData heal)
        {
            // 힐량 추정 (게임 데이터 없으면 기본값)
            // TODO: 실제 힐량 계산 가능하면 개선
            return 30f;  // 기본 추정치
        }

        #endregion

        #region Action Combination Scoring

        /// <summary>
        /// 턴 플랜 전체의 유용성 점수 계산
        /// </summary>
        public static float ScoreTurnPlan(List<PlannedAction> plan, Situation situation)
        {
            if (plan == null || plan.Count == 0) return 0f;

            float totalScore = 0f;

            // 각 행동의 점수 합산
            foreach (var action in plan)
            {
                totalScore += ScoreAction(action, situation);
            }

            // ★ 시너지 보너스
            totalScore += CalculateSynergyBonus(plan, situation);

            // ★ AP 효율 보너스
            float totalCost = plan.Sum(a => a.APCost);
            if (totalCost > 0 && totalCost <= situation.CurrentAP)
            {
                float apEfficiency = situation.CurrentAP / totalCost;
                totalScore += Math.Min(apEfficiency * 10f, 20f);
            }

            return totalScore;
        }

        /// <summary>
        /// 개별 행동 점수
        /// </summary>
        private static float ScoreAction(PlannedAction action, Situation situation)
        {
            var targetUnit = action.Target?.Entity as BaseUnitEntity;

            switch (action.Type)
            {
                case ActionType.Buff:
                    return ScoreBuff(action.Ability, situation);
                case ActionType.Attack:
                    return targetUnit != null ? ScoreAttack(action.Ability, targetUnit, situation) : 10f;
                case ActionType.Heal:
                    return targetUnit != null ? ScoreHeal(action.Ability, targetUnit, situation) : 10f;
                case ActionType.Move:
                    return 20f;  // 이동은 고정 점수
                default:
                    return 10f;
            }
        }

        /// <summary>
        /// 행동 조합 시너지 보너스
        /// </summary>
        private static float CalculateSynergyBonus(List<PlannedAction> plan, Situation situation)
        {
            float bonus = 0f;

            bool hasBuff = plan.Any(a => a.Type == ActionType.Buff);
            bool hasAttack = plan.Any(a => a.Type == ActionType.Attack);
            bool hasMove = plan.Any(a => a.Type == ActionType.Move);

            // 버프 + 공격 시너지
            if (hasBuff && hasAttack)
            {
                var buff = plan.First(a => a.Type == ActionType.Buff);
                if (IsOffensiveBuff(buff.Ability))
                    bonus += 25f;  // 공격 버프 → 공격 시너지
            }

            // 이동 + 공격 시너지 (갭클로저)
            if (hasMove && hasAttack)
                bonus += 10f;

            // 여러 공격 시너지 (연속 공격)
            int attackCount = plan.Count(a => a.Type == ActionType.Attack);
            if (attackCount >= 2)
                bonus += attackCount * 10f;

            return bonus;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// 특정 위치 근처의 적 수
        /// </summary>
        private static int CountEnemiesNear(BaseUnitEntity center, Situation situation, float radius)
        {
            if (center == null || situation.Enemies == null) return 0;
            return situation.Enemies.Count(e =>
                e != null && !e.LifeState.IsDead &&
                CombatAPI.GetDistance(center, e) <= radius);
        }

        /// <summary>
        /// 특정 위치 근처의 아군 수
        /// </summary>
        private static int CountAlliesNear(BaseUnitEntity center, Situation situation, float radius)
        {
            if (center == null || situation.Allies == null) return 0;
            return situation.Allies.Count(a =>
                a != null && !a.LifeState.IsDead &&
                CombatAPI.GetDistance(center, a) <= radius);
        }

        /// <summary>
        /// ★ v3.0.56: 역할별 안전 가중치
        /// Support는 안전 최우선, Tank는 안전 무시
        /// </summary>
        public static float GetRoleSafetyWeight(Situation situation)
        {
            var role = situation.CharacterSettings?.Role ?? Settings.AIRole.Auto;
            switch (role)
            {
                case Settings.AIRole.Support:
                    return 0.8f;   // 안전 최우선 - 높은 가중치
                case Settings.AIRole.DPS:
                    return 0.5f;   // 균형
                case Settings.AIRole.Tank:
                    return 0.2f;   // 안전 무시 - 낮은 가중치
                case Settings.AIRole.Auto:
                default:
                    // ★ v3.0.92: Auto는 RangePreference에 따라 조정
                    if (situation.PrefersRanged)
                        return 0.7f;  // 원거리 선호 = 안전 중시
                    else
                        return 0.4f;  // 근접 선호 = 안전 덜 중시
            }
        }

        /// <summary>
        /// ★ v3.0.56: 현재 위치의 안전도 점수 (0~100)
        /// </summary>
        public static float EvaluateCurrentPositionSafety(Situation situation)
        {
            float safety = 50f;  // 기본 점수

            // 적과의 거리
            if (situation.NearestEnemyDistance >= situation.MinSafeDistance * 1.5f)
                safety += 30f;
            else if (situation.NearestEnemyDistance >= situation.MinSafeDistance)
                safety += 10f;
            else if (situation.NearestEnemyDistance < situation.MinSafeDistance * 0.5f)
                safety -= 30f;
            else
                safety -= 10f;

            // 엄폐 여부
            if (situation.HasCover)
                safety += 20f;

            // HP 상태
            if (situation.HPPercent < 30f)
                safety -= 20f;

            return Math.Max(0f, Math.Min(100f, safety));
        }

        /// <summary>
        /// ★ v3.0.56: ClearMPAfterUse 능력 사용 시 선제적 이동 필요 여부
        /// </summary>
        public static bool ShouldMoveBeforeClearMPAbility(Situation situation, AbilityData ability)
        {
            if (ability == null) return false;
            if (!CombatAPI.AbilityClearsMPAfterUse(ability)) return false;

            // 이동 불가면 false
            if (!situation.CanMove || situation.CurrentMP <= 0) return false;

            // 역할별 안전 가중치
            float safetyWeight = GetRoleSafetyWeight(situation);
            if (safetyWeight < 0.4f) return false;  // Tank는 이동 안 함

            // 위험 상황이면 이동 필요
            if (situation.IsInDanger) return true;

            // 적이 안전 거리보다 가까우면 이동 필요
            if (situation.NearestEnemyDistance < situation.MinSafeDistance) return true;

            return false;
        }

        #endregion
    }
}
