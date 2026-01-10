using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using UnityEngine;

namespace CompanionAI_v3.Data
{
    /// <summary>
    /// 특수 능력 처리 시스템 - SituationAnalyzer/TurnPlanner에서 사용
    /// v2.2에서 포팅 - DoT 강화, 연쇄 효과, 콤보 시스템
    ///
    /// 계획 시점에 특수 능력의 효과성을 판단하여 최적의 계획 수립 지원
    /// </summary>
    public static class SpecialAbilityHandler
    {
        #region DOT Types

        /// <summary>
        /// DoT 타입 (게임의 DOT enum 참고)
        /// </summary>
        public enum DOTType
        {
            Bleeding,           // 출혈
            Burning,            // 화염
            Toxic,              // 독
            PsykerBurning,      // 사이커 화염
            NavigatorBurning,   // 내비게이터 화염
            PsykerToxin,        // 사이커 독
            AssassinHaemorrhage // 암살자 출혈
        }

        #endregion

        #region Special Ability Types

        /// <summary>
        /// 특수 능력 카테고리
        /// </summary>
        public enum SpecialAbilityType
        {
            None,           // 일반 능력
            DOTIntensify,   // DoT 강화 - 타겟에 기존 DoT 있을 때 효과 증가
            ChainEffect,    // 연쇄 효과 - 타겟 간 전파
            ComboFollowup,  // 콤보 후속 - 선행 스킬 후 효과 증가
            BuffRequired,   // 버프 필요 - 특정 버프 활성화 필요
            DebuffEnhancer, // 디버프 강화 - 적 디버프 시 추가 효과
            StackBased      // 스택 기반 - 스택에 따라 효과 증가
        }

        #endregion

        #region GUID Registry

        /// <summary>
        /// DoT 강화 능력 GUID (Symphony of Flames 등)
        /// </summary>
        public static readonly HashSet<string> DOTIntensifyAbilities = new HashSet<string>
        {
            "7720d74e51f94184bb43b97ce9c9e53f",  // Pyromancy_ShapeFlames_Ability
            "24f1e49a2294434da2dc17edb6808517",  // Pyromancy_FanTheFlames_Ability
            "cb3a7a2b865d424183d290b4ff8d3f34",  // Pyromancy_FanTheFlames_EnemiesOnly
        };

        /// <summary>
        /// 연쇄 효과 능력 GUID
        /// </summary>
        public static readonly HashSet<string> ChainEffectAbilities = new HashSet<string>
        {
            "635161f3087c4294bf39c5fefe3d01af",  // ChainLightningPush_Ability
            "7b68b4aa3c024f348a20dce3ef172e40",  // ChainLightning_Ability
            "3c48374cbe244fc2bb8b6293230a6829",  // ChainLightningDesperatePush_Ability
        };

        /// <summary>
        /// Burning DoT 적용 능력 (Shape Flames와 콤보)
        /// </summary>
        public static readonly HashSet<string> BurningDOTAbilities = new HashSet<string>
        {
            "8a759cdc2b754309b1fb75397798fbf1",  // Pyromancy_Weapon_Inferno_Ability
            "c4ea2ad9fe1e4509916cb5f1787b1530",  // Pyromancy_Weapon_Inferno_Desperate
            "84ddefd28f224d5fb3f5e176375c1f05",  // Pyromancy_Weapon_Inferno_Heroic
            "321a9274e3454d69ada142f4ce540b12",  // Pyromancy_FireStorm_Ability
        };

        #endregion

        #region DOT Patterns

        private static readonly string[] BurningDOTPatterns = new[]
        {
            "burning", "burn", "flame", "fire", "inferno", "immolat", "pyro",
            "화염", "불꽃", "연소"
        };

        private static readonly string[] BleedingDOTPatterns = new[]
        {
            "bleed", "haemorrhage", "hemorrhage", "blood", "wound",
            "출혈", "피"
        };

        private static readonly string[] ToxicDOTPatterns = new[]
        {
            "toxic", "poison", "venom", "blight",
            "독", "중독"
        };

        #endregion

        #region Main API

        /// <summary>
        /// 능력이 특수 처리 필요한지 확인
        /// </summary>
        public static bool IsSpecialAbility(AbilityData ability)
        {
            return GetSpecialType(ability) != SpecialAbilityType.None;
        }

        /// <summary>
        /// 능력의 특수 타입 가져오기
        /// </summary>
        public static SpecialAbilityType GetSpecialType(AbilityData ability)
        {
            if (ability == null) return SpecialAbilityType.None;

            string guid = AbilityDatabase.GetGuid(ability);

            // GUID 기반 우선 확인
            if (!string.IsNullOrEmpty(guid))
            {
                if (DOTIntensifyAbilities.Contains(guid)) return SpecialAbilityType.DOTIntensify;
                if (ChainEffectAbilities.Contains(guid)) return SpecialAbilityType.ChainEffect;
            }

            // 블루프린트 이름 기반 폴백 감지
            string bpName = ability.Blueprint?.name?.ToLower() ?? "";

            if (bpName.Contains("symphony") || bpName.Contains("intensify") ||
                bpName.Contains("교향곡") || bpName.Contains("강화"))
            {
                if (ContainsAny(bpName, BurningDOTPatterns))
                    return SpecialAbilityType.DOTIntensify;
            }

            if (bpName.Contains("chain") || bpName.Contains("연쇄") || bpName.Contains("arc"))
                return SpecialAbilityType.ChainEffect;

            if (bpName.Contains("followup") || bpName.Contains("combo") ||
                bpName.Contains("후속") || bpName.Contains("연계"))
                return SpecialAbilityType.ComboFollowup;

            return SpecialAbilityType.None;
        }

        /// <summary>
        /// 특수 능력을 현재 상황에서 효과적으로 사용할 수 있는지 확인
        /// TurnPlanner에서 계획 수립 시 호출
        /// </summary>
        public static bool CanUseSpecialAbilityEffectively(
            AbilityData ability,
            BaseUnitEntity target,
            List<BaseUnitEntity> enemies)
        {
            if (ability == null || target == null) return false;

            var specialType = GetSpecialType(ability);

            switch (specialType)
            {
                case SpecialAbilityType.None:
                    return true; // 일반 능력은 항상 OK

                case SpecialAbilityType.DOTIntensify:
                    var dotType = InferDOTTypeFromAbility(ability);
                    bool hasDoT = HasDoT(target, dotType);
                    if (!hasDoT)
                    {
                        Main.LogDebug($"[SpecialAbility] {ability.Name} skipped - target has no {dotType} DoT");
                        return false;
                    }
                    Main.Log($"[SpecialAbility] {ability.Name} effective - target has {dotType} DoT!");
                    return true;

                case SpecialAbilityType.ChainEffect:
                    int chainTargets = CountChainTargets(ability, target, enemies);
                    if (chainTargets < 2)
                    {
                        Main.LogDebug($"[SpecialAbility] {ability.Name} skipped - only {chainTargets} chain target(s)");
                        return false;
                    }
                    Main.Log($"[SpecialAbility] {ability.Name} effective - {chainTargets} chain targets!");
                    return true;

                case SpecialAbilityType.DebuffEnhancer:
                    bool hasDebuff = HasDebuff(target);
                    if (!hasDebuff)
                    {
                        Main.LogDebug($"[SpecialAbility] {ability.Name} skipped - target has no debuff");
                        return false;
                    }
                    return true;

                default:
                    return true;
            }
        }

        /// <summary>
        /// 특수 능력의 효과 점수 계산 (0-100)
        /// 높을수록 사용 권장 - TurnPlanner에서 우선순위 결정에 사용
        /// </summary>
        public static int GetSpecialAbilityEffectivenessScore(
            AbilityData ability,
            BaseUnitEntity target,
            List<BaseUnitEntity> enemies)
        {
            if (ability == null || target == null) return 0;

            var specialType = GetSpecialType(ability);

            switch (specialType)
            {
                case SpecialAbilityType.DOTIntensify:
                    var dotType = InferDOTTypeFromAbility(ability);
                    int dotStacks = CountDOTStacks(target, dotType);
                    if (dotStacks == 0) return 0;
                    return Math.Min(100, 50 + dotStacks * 10);

                case SpecialAbilityType.ChainEffect:
                    int chainCount = CountChainTargets(ability, target, enemies);
                    if (chainCount < 2) return 20;
                    return Math.Min(100, chainCount * 25);

                case SpecialAbilityType.DebuffEnhancer:
                    int debuffCount = CountDebuffs(target);
                    return Math.Min(100, 40 + debuffCount * 15);

                default:
                    return 50;
            }
        }

        #endregion

        #region DOT Detection

        /// <summary>
        /// 타겟에 DoT가 있는지 확인
        /// </summary>
        public static bool HasDoT(BaseUnitEntity target, DOTType? specificType = null)
        {
            if (target == null) return false;

            try
            {
                foreach (var buff in target.Buffs.Enumerable)
                {
                    string buffName = buff.Blueprint?.name?.ToLower() ?? "";

                    if (specificType.HasValue)
                    {
                        if (IsDOTBuff(buffName, specificType.Value))
                            return true;
                    }
                    else
                    {
                        if (IsAnyDOTBuff(buffName))
                            return true;
                    }
                }

                // PartDOTDirector 접근 시도
                try
                {
                    var dotDirector = target.GetOptional<Kingmaker.UnitLogic.Buffs.Components.DOTLogic.PartDOTDirector>();
                    if (dotDirector != null)
                        return true;
                }
                catch { }

                return false;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[SpecialAbility] HasDoT error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// DoT 스택 수 계산
        /// </summary>
        public static int CountDOTStacks(BaseUnitEntity target, DOTType? specificType = null)
        {
            if (target == null) return 0;

            int count = 0;

            try
            {
                foreach (var buff in target.Buffs.Enumerable)
                {
                    string buffName = buff.Blueprint?.name?.ToLower() ?? "";

                    if (specificType.HasValue)
                    {
                        if (IsDOTBuff(buffName, specificType.Value))
                            count++;
                    }
                    else
                    {
                        if (IsAnyDOTBuff(buffName))
                            count++;
                    }
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[SpecialAbility] CountDOTStacks error: {ex.Message}");
            }

            return count;
        }

        private static bool IsDOTBuff(string buffName, DOTType dotType)
        {
            switch (dotType)
            {
                case DOTType.Burning:
                case DOTType.PsykerBurning:
                case DOTType.NavigatorBurning:
                    return ContainsAny(buffName, BurningDOTPatterns);

                case DOTType.Bleeding:
                case DOTType.AssassinHaemorrhage:
                    return ContainsAny(buffName, BleedingDOTPatterns);

                case DOTType.Toxic:
                case DOTType.PsykerToxin:
                    return ContainsAny(buffName, ToxicDOTPatterns);

                default:
                    return false;
            }
        }

        private static bool IsAnyDOTBuff(string buffName)
        {
            return ContainsAny(buffName, BurningDOTPatterns) ||
                   ContainsAny(buffName, BleedingDOTPatterns) ||
                   ContainsAny(buffName, ToxicDOTPatterns);
        }

        private static DOTType? InferDOTTypeFromAbility(AbilityData ability)
        {
            if (ability == null) return null;

            string bpName = ability.Blueprint?.name?.ToLower() ?? "";

            if (ContainsAny(bpName, BurningDOTPatterns))
                return DOTType.Burning;

            if (ContainsAny(bpName, BleedingDOTPatterns))
                return DOTType.Bleeding;

            if (ContainsAny(bpName, ToxicDOTPatterns))
                return DOTType.Toxic;

            return null;
        }

        #endregion

        #region Chain Effect Detection

        /// <summary>
        /// 연쇄 효과용 유효 타겟 수 계산
        /// </summary>
        public static int CountChainTargets(AbilityData ability, BaseUnitEntity initialTarget, List<BaseUnitEntity> enemies)
        {
            if (ability == null || initialTarget == null || enemies == null) return 0;

            float chainRadius = 7f;  // 기본 연쇄 범위
            int maxChainTargets = 5;  // 기본 최대 연쇄

            int count = 1;  // 초기 타겟 포함
            var usedTargets = new HashSet<BaseUnitEntity> { initialTarget };
            Vector3 currentPosition = initialTarget.Position;

            for (int i = 0; i < maxChainTargets - 1; i++)
            {
                BaseUnitEntity nextTarget = null;
                float closestDistance = float.MaxValue;

                foreach (var enemy in enemies)
                {
                    if (enemy == null || enemy.LifeState.IsDead) continue;
                    if (usedTargets.Contains(enemy)) continue;

                    float distance = Vector3.Distance(currentPosition, enemy.Position);
                    if (distance <= chainRadius && distance < closestDistance)
                    {
                        closestDistance = distance;
                        nextTarget = enemy;
                    }
                }

                if (nextTarget == null) break;

                count++;
                usedTargets.Add(nextTarget);
                currentPosition = nextTarget.Position;
            }

            return count;
        }

        #endregion

        #region Debuff Detection

        /// <summary>
        /// 타겟에 디버프가 있는지 확인
        /// </summary>
        public static bool HasDebuff(BaseUnitEntity target)
        {
            if (target == null) return false;

            try
            {
                foreach (var buff in target.Buffs.Enumerable)
                {
                    string buffName = buff.Blueprint?.name?.ToLower() ?? "";
                    if (IsDebuffByName(buffName))
                        return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 타겟의 디버프 수 계산
        /// </summary>
        public static int CountDebuffs(BaseUnitEntity target)
        {
            if (target == null) return 0;

            int count = 0;

            try
            {
                foreach (var buff in target.Buffs.Enumerable)
                {
                    string buffName = buff.Blueprint?.name?.ToLower() ?? "";
                    if (IsDebuffByName(buffName))
                        count++;
                }
            }
            catch { }

            return count;
        }

        private static bool IsDebuffByName(string buffName)
        {
            return buffName.Contains("weaken") || buffName.Contains("slow") ||
                   buffName.Contains("stun") || buffName.Contains("blind") ||
                   buffName.Contains("fear") || buffName.Contains("vulnerability") ||
                   buffName.Contains("expose") || buffName.Contains("mark") ||
                   IsAnyDOTBuff(buffName);
        }

        #endregion

        #region Combo Detection

        /// <summary>
        /// 능력이 Burning DoT를 적용하는지 확인
        /// </summary>
        public static bool AppliesBurningDOT(AbilityData ability)
        {
            if (ability == null) return false;

            string guid = AbilityDatabase.GetGuid(ability);
            if (!string.IsNullOrEmpty(guid) && BurningDOTAbilities.Contains(guid))
                return true;

            string bpName = ability.Blueprint?.name?.ToLower() ?? "";
            return bpName.Contains("inferno") || bpName.Contains("firestorm") ||
                   bpName.Contains("인페르노") || bpName.Contains("화염폭풍");
        }

        /// <summary>
        /// DoT 강화 능력인지 확인
        /// </summary>
        public static bool IsDOTIntensifyAbility(AbilityData ability)
        {
            if (ability == null) return false;

            string guid = AbilityDatabase.GetGuid(ability);
            if (!string.IsNullOrEmpty(guid) && DOTIntensifyAbilities.Contains(guid))
                return true;

            string bpName = ability.Blueprint?.name?.ToLower() ?? "";
            return bpName.Contains("shapeflames") || bpName.Contains("fantheflames") ||
                   bpName.Contains("symphony") || bpName.Contains("교향곡");
        }

        /// <summary>
        /// 연쇄 효과 능력인지 확인
        /// </summary>
        public static bool IsChainEffectAbility(AbilityData ability)
        {
            if (ability == null) return false;

            string guid = AbilityDatabase.GetGuid(ability);
            if (!string.IsNullOrEmpty(guid) && ChainEffectAbilities.Contains(guid))
                return true;

            string bpName = ability.Blueprint?.name?.ToLower() ?? "";
            return bpName.Contains("chainlightning") || bpName.Contains("chain") ||
                   bpName.Contains("연쇄");
        }

        /// <summary>
        /// 콤보 권장 여부 확인 - TurnPlanner에서 스킬 순서 결정에 사용
        /// 예: 적에게 Burning DoT가 없으면 먼저 Inferno 사용 권장
        /// </summary>
        public static AbilityData GetComboPrerequisite(
            AbilityData ability,
            BaseUnitEntity target,
            List<AbilityData> availableAbilities)
        {
            if (ability == null || target == null) return null;

            var specialType = GetSpecialType(ability);

            if (specialType == SpecialAbilityType.DOTIntensify)
            {
                var dotType = InferDOTTypeFromAbility(ability);
                if (!HasDoT(target, dotType))
                {
                    // Burning DoT가 없으면 먼저 적용할 스킬 찾기
                    if (dotType == DOTType.Burning || dotType == DOTType.PsykerBurning)
                    {
                        foreach (var avail in availableAbilities)
                        {
                            if (AppliesBurningDOT(avail))
                            {
                                Main.Log($"[SpecialAbility] Combo: Use {avail.Name} first before {ability.Name}");
                                return avail;
                            }
                        }
                    }
                }
            }

            return null;
        }

        #endregion

        #region Helper Methods

        private static bool ContainsAny(string text, string[] patterns)
        {
            if (string.IsNullOrEmpty(text)) return false;

            foreach (var pattern in patterns)
            {
                if (text.Contains(pattern))
                    return true;
            }

            return false;
        }

        #endregion
    }
}
