using System;
using System.Collections.Generic;
using System.Linq;
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
                var attacks = situation.AvailableAttacks
                    .Where(a => CombatAPI.CanUseAbilityOn(a, new TargetWrapper(target), out _))
                    .ToList();

                if (attacks.Count == 0)
                    return null;

                // 1. 단일 능력 1타킬 체크 (가장 효율적)
                var singleKillSequence = TrySingleAbilityKill(attacks, target, targetHP);
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
        /// </summary>
        private static KillSequence TrySingleAbilityKill(
            List<AbilityData> attacks,
            BaseUnitEntity target,
            float targetHP)
        {
            foreach (var attack in attacks)
            {
                var (minDamage, maxDamage, _) = CombatAPI.GetDamagePrediction(attack, target);

                // 최소 데미지로도 킬 가능
                if (minDamage >= targetHP)
                {
                    float avgDamage = (minDamage + maxDamage) / 2f;
                    float apCost = attack.CalculateActionPointCost();

                    return new KillSequence
                    {
                        Target = target,
                        Abilities = new List<AbilityData> { attack },
                        TotalDamage = avgDamage,
                        TargetHP = targetHP,
                        APCost = apCost
                    };
                }
            }
            return null;
        }

        /// <summary>
        /// 버프 + 공격 조합으로 킬 가능한지 시뮬레이션
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

                        // AP가 충분한지 확인
                        if (situation.CurrentAP >= buffAPCost + attackAPCost)
                        {
                            Main.LogDebug($"[KillSimulator] Buff+Attack kill: {buff.Name} + {attack.Name} = {buffedDamage:F0} dmg >= {targetHP:F0} HP");

                            return new KillSequence
                            {
                                Target = target,
                                Abilities = new List<AbilityData> { buff, attack },
                                TotalDamage = buffedDamage,
                                TargetHP = targetHP,
                                APCost = buffAPCost + attackAPCost
                            };
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 다중 공격 조합으로 킬 가능한지 시뮬레이션
        /// </summary>
        private static KillSequence SimulateMultiAttack(
            Situation situation,
            BaseUnitEntity target,
            List<AbilityData> attacks,
            float targetHP,
            int maxAbilities)
        {
            // 그리디 방식: 높은 데미지 능력부터 누적
            var sortedAttacks = attacks
                .Select(a => {
                    var (min, max, _) = CombatAPI.GetDamagePrediction(a, target);
                    return new {
                        Attack = a,
                        Damage = (min + max) / 2f,
                        APCost = a.CalculateActionPointCost()
                    };
                })
                .Where(x => x.Damage > 0)
                .OrderByDescending(x => x.Damage / Math.Max(x.APCost, 0.1f)) // 효율 순
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
        private static float EstimateBuffMultiplier(AbilityData buff)
        {
            if (buff?.Blueprint == null)
                return 1.25f;

            try
            {
                // 1. Blueprint 컴포넌트에서 데미지 증가 효과 분석 시도
                var components = buff.Blueprint.ComponentsArray;
                if (components != null)
                {
                    foreach (var component in components)
                    {
                        if (component == null) continue;

                        string typeName = component.GetType().Name;

                        // 데미지 보너스 컴포넌트 감지
                        if (typeName.Contains("DamageBonus") || typeName.Contains("AddOutgoingDamageBonus"))
                        {
                            // 보수적으로 30% 증가로 가정
                            Main.LogDebug($"[KillSimulator] Found damage bonus component: {typeName}");
                            return 1.3f;
                        }

                        // 추가 공격 컴포넌트 감지
                        if (typeName.Contains("ExtraAttack") || typeName.Contains("AdditionalAttack"))
                        {
                            Main.LogDebug($"[KillSimulator] Found extra attack component: {typeName}");
                            return 1.5f;  // 추가 공격은 50% 증가로 추정
                        }

                        // 크리티컬 보너스 컴포넌트 감지
                        if (typeName.Contains("CriticalBonus") || typeName.Contains("CriticalChance"))
                        {
                            Main.LogDebug($"[KillSimulator] Found critical bonus component: {typeName}");
                            return 1.2f;  // 크리티컬 보너스는 20% 증가로 추정
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[KillSimulator] EstimateBuffMultiplier component analysis error: {ex.Message}");
            }

            // 2. 이름 기반 휴리스틱 (폴백)
            string buffName = buff.Name?.ToLower() ?? "";
            string bpName = buff.Blueprint.name?.ToLower() ?? "";

            // 알려진 버프들의 배율
            if (buffName.Contains("rapid fire") || buffName.Contains("속사") ||
                bpName.Contains("rapidfire"))
                return 1.5f;  // 추가 공격으로 인한 대폭 증가

            if (buffName.Contains("frenzy") || buffName.Contains("트랜스") ||
                bpName.Contains("frenzy") || bpName.Contains("combattrance"))
                return 1.3f;  // +30% 데미지

            if (buffName.Contains("fury") || buffName.Contains("분노") ||
                bpName.Contains("fury") || bpName.Contains("rage"))
                return 1.25f; // +25% 데미지

            if (buffName.Contains("aim") || buffName.Contains("조준") ||
                bpName.Contains("aim") || bpName.Contains("precise"))
                return 1.15f; // +15% 명중률 → 데미지 환산

            // 기본값: 보수적으로 1.2 (20% 증가)
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
