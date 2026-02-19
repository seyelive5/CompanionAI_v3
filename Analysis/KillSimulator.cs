using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.Designers.Mechanics.Facts;
using Kingmaker.Designers.Mechanics.Facts.Damage;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;

namespace CompanionAI_v3.Analysis
{
    /// <summary>
    /// ★ v3.2.30: 다중 능력 조합 킬 시뮬레이션
    /// 개별 능력으로는 1타킬 불가능하지만 조합으로 확정 킬 가능한 시퀀스 탐색
    /// </summary>
    public static class KillSimulator
    {
        /// <summary>
        /// 킬 시퀀스 결과
        /// </summary>
        public class KillSequence
        {
            public BaseUnitEntity Target { get; set; }
            public List<AbilityData> Abilities { get; set; } = new List<AbilityData>();
            public float TotalDamage { get; set; }
            public float TargetHP { get; set; }
            public bool IsConfirmedKill => TotalDamage >= TargetHP;
            public float APCost { get; set; }
            public float Efficiency => APCost > 0 && IsConfirmedKill ? (TotalDamage / APCost) : 0f;
        }

        /// <summary>
        /// 타겟에 대해 킬 확정 시퀀스 탐색
        /// </summary>
        /// <param name="situation">현재 전투 상황</param>
        /// <param name="target">타겟 유닛</param>
        /// <param name="maxAbilities">최대 능력 조합 수 (기본 3)</param>
        /// <returns>킬 시퀀스 (IsConfirmedKill로 확정 킬 여부 확인)</returns>
        public static KillSequence FindKillSequence(
            Situation situation,
            BaseUnitEntity target,
            int maxAbilities = 3)
        {
            if (situation == null || target == null)
                return null;

            try
            {
                // ★ v3.4.01: P0-1 null 체크 추가
                if (situation.AvailableAttacks == null || situation.AvailableAttacks.Count == 0)
                    return null;

                // 타겟 현재 HP 계산
                // ★ v3.5.00: GetHP → GetActualHP
                float targetHP = CombatAPI.GetActualHP(target);
                if (targetHP <= 0)
                    return null;

                // 타겟에게 사용 가능한 공격 능력들
                // ★ v3.6.10: Point 타겟 AOE 높이 체크 추가
                var attacks = situation.AvailableAttacks
                    .Where(a => {
                        // AOE 높이 체크
                        if (CombatAPI.IsPointTargetAbility(a))
                        {
                            if (!CombatAPI.IsAoEHeightInRange(a, situation.Unit, target))
                            {
                                Main.LogDebug($"[KillSimulator] AOE height failed: {a.Name} -> {target.CharacterName}");
                                return false;
                            }
                        }
                        return CombatAPI.CanUseAbilityOn(a, new TargetWrapper(target), out _);
                    })
                    .ToList();

                if (attacks.Count == 0)
                    return null;

                // 1. 단일 능력 1타킬 체크 (가장 효율적)
                // ★ v3.5.78: situation 전달하여 AOE 보너스 고려
                var singleKillSequence = TrySingleAbilityKill(situation, attacks, target, targetHP);
                if (singleKillSequence != null)
                    return singleKillSequence;

                // 2. 버프 + 공격 조합 시뮬레이션
                var buffSequence = SimulateWithBuffs(situation, target, attacks, targetHP);
                if (buffSequence != null && buffSequence.IsConfirmedKill)
                    return buffSequence;

                // 3. 다중 공격 조합 시뮬레이션
                var multiAttackSequence = SimulateMultiAttack(situation, target, attacks, targetHP, maxAbilities);
                return multiAttackSequence;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[KillSimulator] Error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 단일 능력으로 1타킬 가능한지 체크
        /// ★ v3.5.78: AOE 보너스를 고려하여 최적 킬 능력 선택 (첫 번째가 아닌 최고 점수)
        /// </summary>
        private static KillSequence TrySingleAbilityKill(
            Situation situation,
            List<AbilityData> attacks,
            BaseUnitEntity target,
            float targetHP)
        {
            KillSequence bestSequence = null;
            float bestScore = float.MinValue;

            foreach (var attack in attacks)
            {
                var (minDamage, maxDamage, _) = CombatAPI.GetDamagePrediction(attack, target);

                // 최소 데미지로도 킬 가능한가?
                if (minDamage >= targetHP)
                {
                    float avgDamage = (minDamage + maxDamage) / 2f;
                    float apCost = attack.CalculateActionPointCost();
                    float efficiency = avgDamage / Math.Max(apCost, 0.1f);

                    // ★ v3.5.78: AOE 보너스 계산 - 게임 API로 정확한 타일 감지
                    float aoeBonus = 0f;
                    if (situation != null)
                    {
                        int aoeEnemyCount = CombatAPI.CountEnemiesInPattern(
                            attack,
                            target.Position,
                            situation.Unit.Position,
                            situation.Enemies);

                        int additionalEnemies = Math.Max(0, aoeEnemyCount - 1);
                        aoeBonus = additionalEnemies * 15f;

                        if (additionalEnemies > 0)
                        {
                            Main.LogDebug($"[KillSimulator] Single-kill AOE: {attack.Name} " +
                                $"hits {aoeEnemyCount} enemies → +{aoeBonus:F0} bonus");
                        }
                    }

                    float totalScore = efficiency + aoeBonus;

                    // 더 좋은 킬 능력?
                    if (totalScore > bestScore)
                    {
                        bestScore = totalScore;
                        bestSequence = new KillSequence
                        {
                            Target = target,
                            Abilities = new List<AbilityData> { attack },
                            TotalDamage = avgDamage,
                            TargetHP = targetHP,
                            APCost = apCost
                        };
                    }
                }
            }

            return bestSequence;
        }

        /// <summary>
        /// 버프 + 공격 조합으로 킬 가능한지 시뮬레이션
        /// ★ v3.5.82: AOE 보너스를 고려하여 최적 조합 선택
        /// </summary>
        private static KillSequence SimulateWithBuffs(
            Situation situation,
            BaseUnitEntity target,
            List<AbilityData> attacks,
            float targetHP)
        {
            // 공격 버프 능력 찾기 (PreAttackBuff)
            // ★ v3.5.00: AvailableAbilities → AvailableBuffs
            var attackBuffs = situation.AvailableBuffs
                .Where(a => AbilityDatabase.GetTiming(a) == AbilityTiming.PreAttackBuff)
                .ToList();

            if (attackBuffs.Count == 0)
                return null;

            // ★ v3.5.82: 모든 킬 가능 조합을 비교하여 최적 선택
            KillSequence bestSequence = null;
            float bestScore = float.MinValue;

            foreach (var buff in attackBuffs)
            {
                float buffMultiplier = EstimateBuffMultiplier(buff);
                float buffAPCost = buff.CalculateActionPointCost();

                foreach (var attack in attacks)
                {
                    var (minDamage, maxDamage, _) = CombatAPI.GetDamagePrediction(attack, target);
                    float avgDamage = (minDamage + maxDamage) / 2f;
                    float buffedDamage = avgDamage * buffMultiplier;

                    // 버프 적용 후 킬 가능
                    if (buffedDamage >= targetHP)
                    {
                        float attackAPCost = attack.CalculateActionPointCost();
                        float totalAPCost = buffAPCost + attackAPCost;

                        // AP가 충분한지 확인
                        if (situation.CurrentAP >= totalAPCost)
                        {
                            float efficiency = buffedDamage / Math.Max(totalAPCost, 0.1f);

                            // ★ v3.5.82: AOE 보너스 계산
                            float aoeBonus = 0f;
                            int aoeEnemyCount = CombatAPI.CountEnemiesInPattern(
                                attack,
                                target.Position,
                                situation.Unit.Position,
                                situation.Enemies);

                            int additionalEnemies = Math.Max(0, aoeEnemyCount - 1);
                            aoeBonus = additionalEnemies * 15f;

                            float totalScore = efficiency + aoeBonus;

                            // 더 좋은 조합?
                            if (totalScore > bestScore)
                            {
                                bestScore = totalScore;
                                bestSequence = new KillSequence
                                {
                                    Target = target,
                                    Abilities = new List<AbilityData> { buff, attack },
                                    TotalDamage = buffedDamage,
                                    TargetHP = targetHP,
                                    APCost = totalAPCost
                                };

                                if (additionalEnemies > 0)
                                {
                                    Main.LogDebug($"[KillSimulator] Buff+Attack AOE: {buff.Name} + {attack.Name} " +
                                        $"hits {aoeEnemyCount} enemies → +{aoeBonus:F0} bonus");
                                }
                            }
                        }
                    }
                }
            }

            if (bestSequence != null)
            {
                Main.LogDebug($"[KillSimulator] Buff+Attack kill: {bestSequence.Abilities[0].Name} + {bestSequence.Abilities[1].Name} " +
                    $"= {bestSequence.TotalDamage:F0} dmg >= {targetHP:F0} HP (score={bestScore:F0})");
            }

            return bestSequence;
        }

        /// <summary>
        /// 다중 공격 조합으로 킬 가능한지 시뮬레이션
        /// ★ v3.5.77: AOE 클러스터 보너스 추가 - 게임 API 기반 정확한 타일 감지
        /// </summary>
        private static KillSequence SimulateMultiAttack(
            Situation situation,
            BaseUnitEntity target,
            List<AbilityData> attacks,
            float targetHP,
            int maxAbilities)
        {
            // 그리디 방식: 높은 데미지 능력부터 누적
            // ★ v3.5.77: AOE 보너스를 효율 계산에 반영
            var sortedAttacks = attacks
                .Select(a => {
                    var (min, max, _) = CombatAPI.GetDamagePrediction(a, target);
                    float damage = (min + max) / 2f;
                    float apCost = a.CalculateActionPointCost();

                    // ★ AOE 클러스터 보너스: 게임 API로 실제 영향 받는 적 수 계산
                    int aoeEnemyCount = CombatAPI.CountEnemiesInPattern(
                        a,
                        target.Position,
                        situation.Unit.Position,
                        situation.Enemies);

                    // 타겟 본인 제외한 추가 적 수
                    int additionalEnemies = Math.Max(0, aoeEnemyCount - 1);

                    // 추가 적당 15점 보너스 (효율 점수에 가산)
                    float aoeBonus = additionalEnemies * 15f;

                    if (additionalEnemies > 0)
                    {
                        Main.LogDebug($"[KillSimulator] AOE bonus: {a.Name} hits {aoeEnemyCount} enemies (+{additionalEnemies} additional) → +{aoeBonus:F0} efficiency");
                    }

                    return new {
                        Attack = a,
                        Damage = damage,
                        APCost = apCost,
                        AoEBonus = aoeBonus,
                        AdditionalEnemies = additionalEnemies
                    };
                })
                .Where(x => x.Damage > 0)
                .OrderByDescending(x => (x.Damage / Math.Max(x.APCost, 0.1f)) + x.AoEBonus) // 효율 + AOE 보너스 순
                .ToList();

            if (sortedAttacks.Count == 0)
                return null;

            var sequence = new KillSequence { Target = target, TargetHP = targetHP };
            float remainingAP = situation.CurrentAP;
            float totalDamage = 0f;
            var usedAbilities = new HashSet<string>();

            foreach (var item in sortedAttacks)
            {
                // 최대 능력 수 제한
                if (sequence.Abilities.Count >= maxAbilities)
                    break;

                // AP 부족
                if (remainingAP < item.APCost)
                    continue;

                // 같은 능력 중복 사용 방지 (쿨다운 고려)
                string abilityId = item.Attack.Blueprint?.AssetGuid?.ToString() ?? item.Attack.Name;
                if (usedAbilities.Contains(abilityId))
                    continue;

                usedAbilities.Add(abilityId);
                sequence.Abilities.Add(item.Attack);
                sequence.APCost += item.APCost;
                totalDamage += item.Damage;
                remainingAP -= item.APCost;

                // 킬 확정
                if (totalDamage >= targetHP)
                {
                    sequence.TotalDamage = totalDamage;
                    Main.LogDebug($"[KillSimulator] Multi-attack kill: {sequence.Abilities.Count} abilities = {totalDamage:F0} dmg >= {targetHP:F0} HP");
                    return sequence;
                }
            }

            // 킬 불가능해도 최선의 시도 반환
            sequence.TotalDamage = totalDamage;
            return sequence;
        }

        /// <summary>
        /// 버프 능력의 데미지 증가 배율 추정
        /// ★ v3.4.01: P2-2 Blueprint 기반 분석 + 이름 기반 휴리스틱
        /// </summary>
        // ★ v3.10.0: private → internal (TurnStrategyPlanner에서 재사용)
        internal static float EstimateBuffMultiplier(AbilityData buff)
        {
            if (buff?.Blueprint == null)
                return 1.25f;

            try
            {
                // ★ v3.8.60: 타입 안전 체크 (string 매칭 제거)
                // 게임 디컴파일에서 확인된 실제 타입으로 직접 비교
                var components = buff.Blueprint.ComponentsArray;
                if (components != null)
                {
                    foreach (var component in components)
                    {
                        if (component == null) continue;

                        // 데미지 수정/보너스 컴포넌트 (WarhammerDamageModifier 추상 베이스 → 3개 구현체 모두 매칭)
                        if (component is WarhammerDamageModifier ||
                            component is WarhammerDamageBonusAgainstSize ||
                            component is WarhammerModifyOutgoingAttackDamage)
                        {
                            Main.LogDebug($"[KillSimulator] Found damage bonus component: {component.GetType().Name}");
                            return 1.3f;
                        }

                        // 크리티컬 데미지 수정 컴포넌트 (추상 베이스 → Initiator/Target/Global 모두 매칭)
                        if (component is WarhammerCriticalDamageModifier)
                        {
                            Main.LogDebug($"[KillSimulator] Found critical bonus component: {component.GetType().Name}");
                            return 1.2f;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[KillSimulator] EstimateBuffMultiplier component analysis error: {ex.Message}");
            }

            // ★ v3.7.65: 이름 기반 휴리스틱 제거 - 컴포넌트 기반 감지만 사용
            // 컴포넌트에서 감지 못한 경우 보수적인 기본값 반환
            return 1.2f;
        }

        /// <summary>
        /// 특정 타겟에 대해 확정 킬이 가능한지 빠르게 체크
        /// (전체 시퀀스 계산 없이 가능성만 확인)
        /// </summary>
        public static bool CanConfirmKill(Situation situation, BaseUnitEntity target)
        {
            var sequence = FindKillSequence(situation, target);
            return sequence != null && sequence.IsConfirmedKill;
        }
    }
}
