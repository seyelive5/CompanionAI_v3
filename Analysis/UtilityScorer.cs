using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.RuleSystem;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;  // ★ v3.8.90: UsingInOverwatchAreaType
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Mechanics.Actions;
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
                    .Select(a => CombatCache.GetHPPercent(a))
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

            var sc = AIConfig.GetScoringConfig();

            switch (timing)
            {
                case AbilityTiming.PreCombatBuff:
                    // 초반에 선제 버프 높은 점수
                    if (phase == CombatPhase.Opening) score += sc.PreCombatOpeningBonus;
                    else if (phase == CombatPhase.Cleanup) score -= sc.PreCombatCleanupPenalty;  // 정리 단계에선 불필요
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
                        score += sc.PreAttackHittableBonus;
                    }
                    else
                    {
                        score -= sc.PreAttackNoEnemyPenalty;  // 적이 범위 밖
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
                    if (phase == CombatPhase.Desperate) score += sc.EmergencyDesperateBonus;
                    else score -= sc.EmergencyNonDesperatePenalty;
                    break;

                case AbilityTiming.Taunt:
                    // ★ v3.5.00: ThresholdConfig 적용
                    var tauntThresholds = AIConfig.GetThresholds();
                    // 근접 적 다수일 때
                    int nearbyEnemies = situation.Enemies?.Count(e =>
                        e != null && CombatAPI.GetDistance(situation.Unit, e) <= tauntThresholds.ThreatProximity) ?? 0;
                    if (nearbyEnemies >= 2) score += sc.TauntNearEnemiesBonus;
                    else score -= sc.TauntFewEnemiesPenalty;
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

            // ★ v3.10.0: 공격 시너지 — 고정 +20에서 동적 데미지 이득 기반으로 개선
            // EstimateBuffMultiplier로 실제 데미지 증폭을 추정하여 버프 간 차별화
            // 예: +30% 버프 → gainScore=30, +20% 버프 → gainScore=20 (기존: 둘 다 +20)
            if (situation.HasHittableEnemies && IsOffensiveBuff(buff))
            {
                float gainScore = 10f; // 폴백: 타겟/공격 정보 없을 때 보수적 점수

                if (situation.BestTarget != null && situation.PrimaryAttack != null)
                {
                    float buffMultiplier = KillSimulator.EstimateBuffMultiplier(buff);
                    if (buffMultiplier > 1f)
                    {
                        var (minDmg, maxDmg, _) = CombatAPI.GetDamagePrediction(situation.PrimaryAttack, situation.BestTarget);
                        float baseDamage = (minDmg + maxDmg) / 2f;
                        float damageGain = baseDamage * (buffMultiplier - 1f);

                        // 데미지 이득을 점수로 변환 (5~40 범위)
                        gainScore = Mathf.Clamp(damageGain / 3f, 5f, 40f);

                        if (Main.IsDebugEnabled)
                            Main.LogDebug($"[UtilityScorer] BuffCoupling: {buff.Name} mult={buffMultiplier:F2}, " +
                                $"baseDmg={baseDamage:F0}, gain={damageGain:F0}, score={gainScore:F1} (was fixed +20)");
                    }
                }

                score += gainScore;
            }

            // ★ 방어 시너지: 위험 상황에서 방어적 버프면 보너스
            if (situation.IsInDanger && IsDefensiveBuff(buff))
                score += 25f;

            // ★ 중복 패널티: 이미 활성화된 버프
            if (AllyStateCache.HasBuff(situation.Unit, buff))
                score -= 100f;

            // ★ 이미 버프한 턴에는 추가 버프 약간 감점 (한 턴에 너무 많은 버프 방지)
            if (situation.HasBuffedThisTurn)
                score -= 10f;

            // ★ 페이즈별 가중치
            switch (phase)
            {
                case CombatPhase.Opening:
                    score *= sc.OpeningPhaseBuffMult;  // 초반엔 버프 가중치 UP
                    break;
                case CombatPhase.Cleanup:
                    score *= sc.CleanupPhaseBuffMult;  // 정리 단계엔 버프 가중치 DOWN
                    break;
                case CombatPhase.Desperate:
                    if (!IsDefensiveBuff(buff)) score *= sc.DesperateNonDefMult;  // 위기엔 방어 버프만
                    break;
            }

            return score;
        }

        /// <summary>
        /// ★ v3.9.20: 공격적 버프인가? (데미지 증가, 명중률 증가 등)
        /// PreAttackBuff/HeroicAct은 항상 공격적, PreCombatBuff는 블루프린트 분석
        /// </summary>
        private static bool IsOffensiveBuff(AbilityData buff)
        {
            if (buff == null) return false;
            var timing = AbilityDatabase.GetTiming(buff);
            if (timing == AbilityTiming.PreAttackBuff || timing == AbilityTiming.HeroicAct)
                return true;
            // ★ v3.9.20: PreCombatBuff 중 공격 스탯 부여 버프 감지
            if (timing == AbilityTiming.PreCombatBuff)
                return AbilityDatabase.IsOffensiveBuff(buff);
            return false;
        }

        /// <summary>
        /// ★ v3.9.20: 방어적 버프인가? (방어력 증가, 회피 등)
        /// Emergency는 항상 방어적, PreCombatBuff는 블루프린트 분석
        /// 분석 실패 시 PreCombatBuff는 방어 버프로 기본 분류 (기존 동작 유지)
        /// </summary>
        private static bool IsDefensiveBuff(AbilityData buff)
        {
            if (buff == null) return false;
            var timing = AbilityDatabase.GetTiming(buff);
            if (timing == AbilityTiming.Emergency)
                return true;
            if (timing == AbilityTiming.PreCombatBuff)
            {
                // 블루프린트 분석으로 정확한 분류
                bool isDef = AbilityDatabase.IsDefensiveBuff(buff);
                bool isOff = AbilityDatabase.IsOffensiveBuff(buff);
                if (isDef || isOff) return isDef;  // 분석 성공
                return true;  // 분석 불가: PreCombatBuff 기본값 = 방어
            }
            return false;
        }

        /// <summary>
        /// ★ v3.8.41: 궁극기 상세 점수 계산 (실제 능력 데이터 기반)
        ///
        /// 전투당 한 번만 사용 가능 → 상황에 맞는 최적의 궁극기 선택 필요
        /// 잠재력 초월 추가 턴에서는 궁극기가 유일한 행동이므로 반드시 사용해야 함
        ///
        /// 타겟 유형별 분류:
        /// - SelfBuff(Personal): 항상 사용 가능, 영구 강화 효과 (대부분의 궁극기)
        ///   Steady Superiority, Carnival, Overcharge, Firearm Mastery, Unyielding Guard, Daring Breach
        /// - ImmediateAttack(적 타겟): 즉시 공격, 적 필요
        ///   Dispatch, Death Waltz, Wild Hunt, Dismantling Attack
        /// - AllyBuff(아군 타겟): 아군 추가 턴 부여
        ///   Finest Hour!
        /// - AreaEffect(지점 타겟): 구역 기반 팀 효과
        ///   Take and Hold, Orchestrated Firestorm
        /// </summary>
        private static float ScoreUltimateByType(AbilityData ultimate, Situation situation)
        {
            float score = 0f;
            var info = CombatAPI.GetUltimateInfo(ultimate);

            // ========================================
            // 상황 변수 수집
            // ========================================
            int livingEnemies = situation.Enemies?.Count(e => e != null && e.IsConscious) ?? 0;
            int livingAllies = situation.Allies?.Count(a => a != null && a.IsConscious) ?? 0;
            float hpPercent = situation.HPPercent;
            bool isInDanger = situation.IsInDanger;
            int hittableEnemies = situation.HittableEnemies?.Count ?? 0;

            // HP 낮은 아군 수
            int lowHPAllies = situation.Allies?.Count(a =>
                a != null && a.IsConscious && a != situation.Unit &&
                CombatCache.GetHPPercent(a) < 50f) ?? 0;

            Main.LogDebug($"[UtilityScorer] Ultimate {ultimate.Name}: TargetType={info.TargetType}, " +
                $"HeroicAct={info.IsHeroicAct}, HP={hpPercent:F0}%, Danger={isInDanger}, " +
                $"Enemies={livingEnemies}, Hittable={hittableEnemies}, LowHPAllies={lowHPAllies}");

            switch (info.TargetType)
            {
                case CombatAPI.UltimateTargetType.SelfBuff:
                    // ========================================
                    // Personal 타겟 궁극기 (대부분의 궁극기)
                    // ========================================
                    // 항상 사용 가능 (타겟 불필요) → 높은 기본 점수
                    // 효과가 전투 끝까지 지속 → 적이 많을수록 가치 상승
                    //
                    // 실제 예시:
                    // - Steady Superiority: +1 공격/턴 영구 → 적 많을수록 좋음
                    // - Firearm Mastery: 연사력만큼 즉시 추가공격 → 적 있으면 즉시 효과
                    // - Daring Breach: AP/MP 전량 복구 → 잔여 전투량과 비례
                    // - Unyielding Guard: 3셀 아군 보호 반격 → 아군이 위협받을 때
                    // - Carnival of Misery: DoT +100% → DoT 기반 빌드에 강력
                    // - Overcharge: 사역마 풀힐 + AP 할인 → 사역마 있을 때

                    score += 100f;  // 기본: 항상 사용 가능

                    // 남은 적 수 = 영구 강화의 가치 (적이 많을수록 더 많은 턴 활용)
                    score += livingEnemies * 15f;

                    // 위험 상황이면 방어적 효과도 가치 있음
                    if (isInDanger)
                        score += 30f;

                    Main.LogDebug($"[UtilityScorer] {ultimate.Name}: SELF_BUFF score={score:F0}");
                    break;

                case CombatAPI.UltimateTargetType.ImmediateAttack:
                    // ========================================
                    // 적 타겟 즉시 공격 궁극기
                    // ========================================
                    // 공격 가능 적이 있어야 효과 발휘
                    //
                    // 실제 예시:
                    // - Dispatch: 필중 단일 타겟 고데미지 (실종 HP 25% 추가)
                    // - Death Waltz: 다중 근접 공격 (2+AGI/3회)
                    // - Wild Hunt: 먹잇감 전체 동시공격 필중+필크리
                    // - Dismantling Attack: 전체 적 exploit + 필중 공격 + 디버프

                    if (hittableEnemies > 0)
                    {
                        score += 120f;  // 공격 가능 = 즉시 효과

                        // 적 수에 따른 보너스 (Multi-hit 궁극기는 적 많을수록 유리)
                        score += Math.Min(livingEnemies * 20f, 100f);
                    }
                    else
                    {
                        // 공격 불가 = 사용 자체가 불가능하거나 무의미
                        score -= 200f;
                        Main.LogDebug($"[UtilityScorer] {ultimate.Name}: No hittable enemies - heavily penalized");
                    }

                    Main.LogDebug($"[UtilityScorer] {ultimate.Name}: IMMEDIATE_ATTACK score={score:F0}");
                    break;

                case CombatAPI.UltimateTargetType.AllyBuff:
                    // ========================================
                    // 아군 타겟 궁극기 (Finest Hour! 등)
                    // ========================================
                    // 아군 1명에게 추가 턴(풀AP+풀MP) 부여
                    // 강력한 딜러에게 사용하면 극대효과

                    if (livingAllies > 0)
                    {
                        score += 80f;  // 아군 존재 = 사용 가능

                        // 아군이 많을수록 좋은 타겟 선택지
                        score += livingAllies * 10f;

                        // HP 낮은 아군이 있으면 추가 턴으로 자힐 가능
                        score += lowHPAllies * 20f;

                        // 적이 많으면 추가 턴의 가치 상승
                        score += Math.Min(livingEnemies * 15f, 75f);
                    }
                    else
                    {
                        // 아군 없음 = 사용 불가
                        score -= 200f;
                    }

                    Main.LogDebug($"[UtilityScorer] {ultimate.Name}: ALLY_BUFF score={score:F0} (allies={livingAllies})");
                    break;

                case CombatAPI.UltimateTargetType.AreaEffect:
                    // ========================================
                    // 지점 타겟 구역 효과 궁극기
                    // ========================================
                    // Take and Hold: 구역 내 아군 추가 턴 (킬 시 AP 증가)
                    // Orchestrated Firestorm: 구역 내 적에게 아군 전원 공격
                    //
                    // 아군 + 적 배치에 따라 효과 크게 변동

                    // 기본 점수
                    score += 80f;

                    if (info.NotOffensive)
                    {
                        // 아군 지원형 구역 (Take and Hold)
                        // 구역 내 아군이 많을수록 효과적
                        score += livingAllies * 20f;

                        // 적이 많으면 킬 보너스로 AP 증가 기대
                        score += livingEnemies * 10f;
                    }
                    else
                    {
                        // 공격형 구역 (Orchestrated Firestorm)
                        // 적이 많을수록 + 아군(공격 참여자)이 많을수록 효과적
                        score += livingEnemies * 20f;
                        score += livingAllies * 15f;

                        // 공격 가능한 적이 없으면 무의미
                        if (livingEnemies == 0)
                            score -= 150f;
                    }

                    Main.LogDebug($"[UtilityScorer] {ultimate.Name}: AREA_EFFECT score={score:F0} " +
                        $"(offensive={!info.NotOffensive}, allies={livingAllies}, enemies={livingEnemies})");
                    break;

                default:
                    // 분류 불가 = 기본 점수 (사용은 시도)
                    score += 50f;
                    Main.LogDebug($"[UtilityScorer] {ultimate.Name}: UNKNOWN type, default score");
                    break;
            }

            // ========================================
            // HeroicAct vs DesperateMeasures: 항상 HeroicAct 우선
            // ========================================
            // 동일한 효과에 DesperateMeasures는 추가 페널티(출혈, 스탯감소 등)가 있음
            // → HeroicAct가 엄격히 상위호환
            if (info.IsHeroicAct)
                score += 100f;  // HeroicAct 선호
            // DesperateMeasures는 보너스 없음 (HeroicAct 불가 시 자동 선택)

            return score;
        }

        /// <summary>
        /// 버프 리스트에서 최적 버프 선택
        /// </summary>
        public static AbilityData SelectBestBuff(List<AbilityData> buffs, Situation situation)
        {
            if (buffs == null || buffs.Count == 0) return null;

            // ★ v3.8.48: LINQ → CollectionHelper (0 할당, O(n))
            return CollectionHelper.MaxByWithThreshold(buffs, b => ScoreBuff(b, situation), 0f);
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
            var scAtk = AIConfig.GetScoringConfig();

            bool clearsMPAfterUse = CombatAPI.AbilityClearsMPAfterUse(attack, situation.Unit);  // ★ v3.8.88: 유닛 특성 고려
            if (clearsMPAfterUse)
            {
                // 역할별 안전 가중치 적용
                float safetyWeight = GetRoleSafetyWeight(situation);

                // 위험 상황 (적이 가까움) + MP 클리어 능력 = 감점
                if (situation.IsInDanger)
                {
                    float dangerPenalty = scAtk.ClearMPDangerBase * safetyWeight;  // Support는 -48점, Tank는 -12점
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

            // ★ v3.8.90: 적 오버워치 구역 내 WillCauseAttack 능력 사용 페널티
            // 게임: OverwatchController.IsTriggersOverwatch()가 WillCauseAttack 체크 → 적 오버워치 공격 유발
            if (situation.IsInEnemyOverwatchZone)
            {
                try
                {
                    bool willTriggerOverwatch = attack.Blueprint?.UsingInOverwatchArea
                        != BlueprintAbility.UsingInOverwatchAreaType.WillNotCauseAttack;
                    if (willTriggerOverwatch)
                    {
                        // 오버워치 1건당 -20점 페널티 (적 무료 공격 1회 = 상당한 위험)
                        float overwatchPenalty = situation.EnemyOverwatchCount * 20f;
                        score -= overwatchPenalty;
                        if (Main.IsDebugEnabled)
                            Main.LogDebug($"[UtilityScorer] {attack.Name}: Overwatch trigger penalty={overwatchPenalty:F0} ({situation.EnemyOverwatchCount} overwatchers)");
                    }
                    else
                    {
                        // WillNotCauseAttack 능력 = 오버워치 안전 → 보너스
                        score += 15f;
                        if (Main.IsDebugEnabled)
                            Main.LogDebug($"[UtilityScorer] {attack.Name}: Overwatch-safe ability bonus=+15");
                    }
                }
                catch { }
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

            // ★ v3.9.30: 명중률 기반 점수 조정
            // 근접/Scatter → GetHitChance에서 100% 반환 → 영향 없음
            var hitInfo = CombatCache.GetHitChance(attack, situation.Unit, target);
            if (hitInfo != null)
            {
                if (hitInfo.HitChance < 30)
                    score -= 20f;       // 매우 낮은 명중률 → 강한 감점
                else if (hitInfo.HitChance < 50)
                    score -= 10f;       // 낮은 명중률 → 중간 감점
                else if (hitInfo.HitChance >= 80)
                    score += 5f;        // 높은 명중률 → 약간의 보너스
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

            // ★ v3.8.46: Debuff Exploitation (디버프 활용)
            // Hard CC (기절/고정) → 회피 불가, 명중 보장 → 공격 보너스
            // DOT (출혈/독/화상) → 타겟 약화 중 → 약간의 추가 보너스
            try
            {
                var targetBuffs = target.Buffs?.Enumerable;
                if (targetBuffs != null)
                {
                    bool hasHardCC = targetBuffs.Any(b => b.Blueprint?.IsHardCrowdControl == true);
                    bool hasDOT = targetBuffs.Any(b =>
                        b.Blueprint?.IsDOTVisual == true || b.Blueprint?.DynamicDamage == true);

                    if (hasHardCC)
                    {
                        score += scAtk.HardCCExploitBonus;
                        Main.LogDebug($"[UtilityScorer] +{scAtk.HardCCExploitBonus:F0} HardCC exploit: {target.CharacterName}");
                    }
                    if (hasDOT)
                    {
                        score += scAtk.DOTFollowUpBonus;
                        Main.LogDebug($"[UtilityScorer] +{scAtk.DOTFollowUpBonus:F0} DOT follow-up: {target.CharacterName}");
                    }
                }
            }
            catch (Exception ex) { Main.LogDebug($"[UtilityScorer] {ex.Message}"); }

            // ★ 특수 타이밍 고려
            // ★ v3.5.00: ThresholdConfig 적용
            var attackThresholds = AIConfig.GetThresholds();
            var timing = AbilityDatabase.GetTiming(attack);
            float targetHPPercent = CombatCache.GetHPPercent(target);

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
                    score += additionalEnemies * scAtk.AoEBonusPerEnemy;       // 추가 적당 보너스
                    Main.LogDebug($"[UtilityScorer] AOE {attack.Name} -> {target.CharacterName}: " +
                        $"hits {enemiesInPattern} enemies (+{additionalEnemies} additional) = +{additionalEnemies * scAtk.AoEBonusPerEnemy:F0}");
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
                        if (ally == situation.Unit) continue;

                        if (CombatAPI.IsUnitInDirectionalAoERange(
                            situation.Unit.Position, direction, ally, checkRadius, angle, patternType.Value))
                        {
                            alliesInDanger++;
                        }
                    }
                }
                else
                {
                    alliesInDanger = CountAlliesNear(target, situation, checkRadius);
                }

                // ★ v3.8.94: "허용이면 진짜 허용" — 초과 시만 차단, 허용 범위 내 감점 없음
                if (alliesInDanger > maxAlliesAllowed)
                {
                    score -= 1000f;
                    if (Main.IsDebugEnabled)
                        Main.LogDebug($"[Scorer] AOE {attack.Name}: {alliesInDanger} allies > max {maxAlliesAllowed} - BLOCKED");
                }
                else if (alliesInDanger > 0 && Main.IsDebugEnabled)
                {
                    Main.LogDebug($"[Scorer] AOE {attack.Name}: {alliesInDanger} allies ≤ max {maxAlliesAllowed} - ALLOWED (no penalty)");
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
            float hpPercent = CombatCache.GetHPPercent(target);
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
                if (situation.MostWoundedAlly != null && CombatCache.GetHPPercent(situation.MostWoundedAlly) < 50f)
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
            float hpPercent = CombatCache.GetHPPercent(target);
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

            // ★ v3.8.48: LINQ → CollectionHelper (0 할당, O(n))
            return CollectionHelper.MaxByWhere(targets,
                t => t != null && !t.LifeState.IsDead,
                t => ScoreTarget(t, situation));
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
            float targetHP = CombatCache.GetHPPercent(target);
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
            float expectedHeal = EstimateHealAmount(heal, target);
            float efficiency = Math.Min(expectedHeal / Math.Max(missingHP, 1f), 1f);
            score += efficiency * 20f;

            return score;
        }

        /// <summary>
        /// ★ v3.8.59: 예상 힐량 추정 (Blueprint 데이터 기반)
        /// ContextActionHealTarget의 DiceFormula 또는 MinMax 값에서 평균 힐량 계산
        /// 타겟 MaxHP 대비 비율로 반환 (missingHP와 동일한 0-100 스케일)
        /// </summary>
        private static float EstimateHealAmount(AbilityData heal, BaseUnitEntity target)
        {
            if (heal?.Blueprint == null || target == null) return 30f;

            try
            {
                // ★ v3.8.62: BlueprintCache 캐시 사용 (GetComponent O(n) → O(1))
                var runAction = BlueprintCache.GetCachedRunAction(heal.Blueprint);
                if (runAction?.Actions?.Actions == null) return 30f;

                float rawHeal = FindHealAmountInActions(runAction.Actions.Actions);
                if (rawHeal > 0f)
                {
                    int maxHP = CombatAPI.GetActualMaxHP(target);
                    if (maxHP > 0)
                    {
                        float healPercent = (rawHeal / maxHP) * 100f;
                        Main.LogDebug($"[UtilityScorer] EstimateHealAmount: {heal.Name} → avg {rawHeal:F0} HP ({healPercent:F1}% of {maxHP} maxHP)");
                        return healPercent;
                    }
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[UtilityScorer] EstimateHealAmount error: {ex.Message}");
            }

            return 30f;  // 폴백: MaxHP의 30% 추정
        }

        /// <summary>
        /// ★ v3.8.59: Actions 배열에서 ContextActionHealTarget을 찾아 평균 힐량 반환 (재귀)
        /// SavingThrow, Conditional 등 중첩 액션도 검색
        /// </summary>
        private static float FindHealAmountInActions(Kingmaker.ElementsSystem.GameAction[] actions)
        {
            if (actions == null) return 0f;

            foreach (var action in actions)
            {
                if (action == null) continue;

                if (action is ContextActionHealTarget healAction)
                {
                    return CalculateAverageHeal(healAction);
                }

                // 중첩된 Actions 검색 (SavingThrow, Conditional 등)
                try
                {
                    var actionType = action.GetType();

                    // Actions 필드 (ContextActionSavingThrow 등)
                    var actionsField = actionType.GetField("Actions");
                    if (actionsField != null)
                    {
                        var nestedActionList = actionsField.GetValue(action);
                        if (nestedActionList != null)
                        {
                            var nestedActionsField = nestedActionList.GetType().GetField("Actions");
                            if (nestedActionsField != null)
                            {
                                var nestedActions = nestedActionsField.GetValue(nestedActionList) as Kingmaker.ElementsSystem.GameAction[];
                                float nested = FindHealAmountInActions(nestedActions);
                                if (nested > 0f) return nested;
                            }
                        }
                    }

                    // Succeed/Failed 필드 (ContextActionConditionalSaved 등)
                    foreach (var fieldName in new[] { "Succeed", "Failed" })
                    {
                        var field = actionType.GetField(fieldName);
                        if (field == null) continue;

                        var actionList = field.GetValue(action);
                        if (actionList == null) continue;

                        var listActionsField = actionList.GetType().GetField("Actions");
                        if (listActionsField == null) continue;

                        var subActions = listActionsField.GetValue(actionList) as Kingmaker.ElementsSystem.GameAction[];
                        float nested = FindHealAmountInActions(subActions);
                        if (nested > 0f) return nested;
                    }
                }
                catch { /* Reflection 실패 무시 */ }
            }

            return 0f;
        }

        /// <summary>
        /// ★ v3.8.59: ContextActionHealTarget에서 평균 힐량 계산 (raw HP)
        /// Dice 기반: (MinValue + MaxValue) / 2, MinMax 기반: (Min + Max) / 2 + Bonus
        /// </summary>
        private static float CalculateAverageHeal(ContextActionHealTarget healAction)
        {
            try
            {
                if (!healAction.UseMinMaxValues)
                {
                    // Dice 기반: NdM + bonus
                    var diceValue = healAction.Value;
                    if (diceValue == null) return 0f;

                    // ContextValue.Value: Simple이면 정적 값, 비Simple이면 0 또는 배수
                    int diceCount = diceValue.DiceCountValue?.Value ?? 0;
                    int bonus = diceValue.BonusValue?.Value ?? 0;
                    var diceType = diceValue.DiceType;

                    if (diceCount <= 0 && bonus <= 0)
                        return 0f;  // 런타임 의존 값 — 파싱 불가

                    // DiceFormula: min = rolls + bonus, max = diceType × rolls + bonus
                    var formula = new DiceFormula(diceCount, diceType);
                    float avg = (formula.MinValue(bonus) + formula.MaxValue(bonus)) / 2f;
                    return Math.Max(avg, 1f);
                }
                else
                {
                    // MinMax 기반
                    int min = healAction.MinHealing?.Value ?? 0;
                    int max = healAction.MaxHealing?.Value ?? 0;
                    int bonus = healAction.Bonus?.Value ?? 0;

                    if (min <= 0 && max <= 0 && bonus <= 0)
                        return 0f;  // 런타임 의존 값

                    return (min + max) / 2f + bonus;
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[UtilityScorer] CalculateAverageHeal error: {ex.Message}");
                return 0f;
            }
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
            var sc = AIConfig.GetScoringConfig();

            bool hasBuff = plan.Any(a => a.Type == ActionType.Buff);
            bool hasAttack = plan.Any(a => a.Type == ActionType.Attack);
            bool hasMove = plan.Any(a => a.Type == ActionType.Move);

            // 버프 + 공격 시너지
            if (hasBuff && hasAttack)
            {
                var buff = plan.First(a => a.Type == ActionType.Buff);
                if (IsOffensiveBuff(buff.Ability))
                    bonus += sc.BuffAttackSynergy;  // 공격 버프 → 공격 시너지
            }

            // 이동 + 공격 시너지 (갭클로저)
            if (hasMove && hasAttack)
                bonus += sc.MoveAttackSynergy;

            // 여러 공격 시너지 (연속 공격)
            int attackCount = plan.Count(a => a.Type == ActionType.Attack);
            if (attackCount >= 2)
                bonus += attackCount * sc.MultiAttackPerAttack;

            // ★ v3.8.46: 방어 버프 + 이동 시너지 (후퇴 콤보)
            if (hasBuff && hasMove)
            {
                var buff = plan.First(a => a.Type == ActionType.Buff);
                if (IsDefensiveBuff(buff.Ability))
                    bonus += sc.DefenseRetreatSynergy;
            }

            // ★ v3.8.46: 킬 확정 / 거의 킬 시너지
            if (hasAttack && situation.BestTarget != null)
            {
                float totalPlanDamage = 0f;
                BaseUnitEntity planTarget = null;

                foreach (var action in plan.Where(a => a.Type == ActionType.Attack))
                {
                    var targetUnit = action.Target?.Entity as BaseUnitEntity;
                    if (targetUnit == null) continue;
                    if (planTarget == null) planTarget = targetUnit;
                    if (targetUnit != planTarget) continue; // 같은 타겟만 집계
                    if (action.Ability != null)
                        totalPlanDamage += CombatAPI.EstimateDamage(action.Ability, targetUnit);
                }

                if (planTarget != null && totalPlanDamage > 0f)
                {
                    float targetHP = CombatAPI.GetActualHP(planTarget);
                    float ratio = totalPlanDamage / Mathf.Max(targetHP, 1f);

                    if (ratio >= 1.0f)
                    {
                        bonus += sc.KillConfirmSynergy;
                        Main.LogDebug($"[UtilityScorer] Kill confirm synergy: +{sc.KillConfirmSynergy:F0} ({planTarget.CharacterName}, ratio={ratio:F2})");
                    }
                    else if (ratio >= 0.9f)
                    {
                        bonus += sc.AlmostKillSynergy;
                        Main.LogDebug($"[UtilityScorer] Almost-kill synergy: +{sc.AlmostKillSynergy:F0} ({planTarget.CharacterName}, ratio={ratio:F2})");
                    }
                }
            }

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

        // ★ v3.9.28: EvaluateCurrentPositionSafety() 삭제 — 호출자 없는 데드 코드
        // TacticalOptionEvaluator.EvaluateCoverQualityAtPosition()이 이 역할을 더 정확하게 수행

        /// <summary>
        /// ★ v3.0.56: ClearMPAfterUse 능력 사용 시 선제적 이동 필요 여부
        /// </summary>
        public static bool ShouldMoveBeforeClearMPAbility(Situation situation, AbilityData ability)
        {
            if (ability == null) return false;
            if (!CombatAPI.AbilityClearsMPAfterUse(ability, situation.Unit)) return false;  // ★ v3.8.88

            // 이동 불가면 false
            if (!situation.CanMove || situation.CurrentMP <= 0) return false;

            // ★ v3.9.06: 근접 캐릭터는 ClearMP 전 후퇴 금지
            // 근접은 적에게 붙어있어야 공격 가능 — 후퇴하면 공격 불가 → GapCloser로 재접근 → MP 낭비
            if (!situation.PrefersRanged) return false;

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
