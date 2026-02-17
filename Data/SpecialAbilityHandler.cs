using System;
using System.Collections.Generic;
using System.Linq;
using Code.Enums;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Buffs.Components;
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
        /// ★ v3.8.60: String 폴백 제거 — GUID 전용
        public static SpecialAbilityType GetSpecialType(AbilityData ability)
        {
            if (ability == null) return SpecialAbilityType.None;

            string guid = AbilityDatabase.GetGuid(ability);
            if (string.IsNullOrEmpty(guid)) return SpecialAbilityType.None;

            if (DOTIntensifyAbilities.Contains(guid)) return SpecialAbilityType.DOTIntensify;
            if (ChainEffectAbilities.Contains(guid)) return SpecialAbilityType.ChainEffect;

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
        /// ★ v3.8.60: 게임 PartDOTDirector API 사용 (string 매칭 제거)
        /// </summary>
        public static bool HasDoT(BaseUnitEntity target, DOTType? specificType = null)
        {
            if (target == null) return false;

            try
            {
                if (specificType.HasValue)
                {
                    return DOTLogic.GetDamageOfTypeInstancesCount(target, MapToGameDOT(specificType.Value)) > 0;
                }

                // Any DOT — PartDOTDirector는 첫 DOT 등록 시 생성, 모두 만료 시 제거
                return target.GetOptional<DOTLogic.PartDOTDirector>() != null;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[SpecialAbility] HasDoT error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// DoT 스택 수 계산
        /// ★ v3.8.60: 게임 PartDOTDirector API 사용 (string 매칭 제거)
        /// </summary>
        public static int CountDOTStacks(BaseUnitEntity target, DOTType? specificType = null)
        {
            if (target == null) return 0;

            try
            {
                if (specificType.HasValue)
                {
                    return DOTLogic.GetDamageOfTypeInstancesCount(target, MapToGameDOT(specificType.Value));
                }

                // 모든 DOT 타입 합산
                int count = 0;
                foreach (DOT dotValue in Enum.GetValues(typeof(DOT)))
                {
                    count += DOTLogic.GetDamageOfTypeInstancesCount(target, dotValue);
                }
                return count;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[SpecialAbility] CountDOTStacks error: {ex.Message}");
                return 0;
            }
        }

        // ★ v3.8.61: IsDOTBuff/IsAnyDOTBuff 제거 — v3.8.60 PartDOTDirector API + v3.8.61 IsEnemy 체크로 대체

        /// ★ v3.8.60: GUID 우선 확인 추가
        private static DOTType? InferDOTTypeFromAbility(AbilityData ability)
        {
            if (ability == null) return null;

            // GUID 기반 우선 확인
            string guid = AbilityDatabase.GetGuid(ability);
            if (!string.IsNullOrEmpty(guid))
            {
                if (DOTIntensifyAbilities.Contains(guid) || BurningDOTAbilities.Contains(guid))
                    return DOTType.Burning;
            }

            // 블루프린트 이름 폴백 (GUID 미등록 능력의 DOT 타입 추론)
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
        /// ★ v3.9.68: 게임 AbilityDataHelper.SelectNextTarget 알고리즘 정밀 복제
        /// 기존 자체 시뮬레이션의 3가지 버그 수정:
        ///   1. Vector3.Distance (유클리드 미터) → DistanceToInCells (Chebyshev 셀)
        ///   2. Radius * GridCellSize (셀→미터 변환) → Radius 직접 사용 (이미 셀)
        ///   3. enemies만 순회 → Game.Instance.State.AllBaseUnits (TargetType.Any 시 아군 포함)
        /// 추가: IsValidTargetForAttack, CheckTarget, IsInCombat 검증 (게임과 동일)
        /// </summary>
        public static List<BaseUnitEntity> PredictChainTargets(AbilityData ability, BaseUnitEntity initialTarget)
        {
            var result = new List<BaseUnitEntity>();
            if (ability == null || initialTarget == null) return result;

            result.Add(initialTarget);

            try
            {
                var deliverChain = ability.Blueprint?.GetComponent<AbilityDeliverChain>();
                if (deliverChain == null) return result;

                int radiusCells = deliverChain.Radius;
                if (radiusCells <= 0) return result;

                int maxTargets = 5;
                try
                {
                    int targets = deliverChain.TargetsCount.Value;
                    if (targets > 0) maxTargets = targets;
                }
                catch { }

                var usedTargets = new HashSet<BaseUnitEntity> { initialTarget };
                Vector3 currentPoint = initialTarget.Position;

                for (int i = 1; i < maxTargets; i++)
                {
                    BaseUnitEntity nextTarget = null;
                    float minDist = float.MaxValue;

                    // 게임: Game.Instance.State.AllBaseUnits 전체 순회
                    foreach (var unit in Game.Instance.State.AllBaseUnits)
                    {
                        // 게임: IsValidTargetForAttack (HP, 의식, 펫 제한 등)
                        if (!ability.IsValidTargetForAttack(unit)) continue;
                        // 게임: IsInCombat (전투 참여 유닛만)
                        if (!unit.IsInCombat) continue;

                        // 게임: DistanceToInCells (Chebyshev + SizeRect)
                        float dist = (float)unit.DistanceToInCells(currentPoint);

                        // 게임: CheckTarget (TargetDead, TargetType)
                        if (!CheckChainTarget(ability, deliverChain, unit)) continue;

                        if (dist <= radiusCells && !usedTargets.Contains(unit) && dist < minDist)
                        {
                            minDist = dist;
                            nextTarget = unit;
                        }
                    }

                    if (nextTarget == null) break;

                    result.Add(nextTarget);
                    usedTargets.Add(nextTarget);
                    currentPoint = nextTarget.Position;
                }

                if (Main.IsDebugEnabled)
                    Main.LogDebug($"[SpecialAbility] Chain prediction: {ability.Name} -> {result.Count} targets (radius={radiusCells} cells, max={maxTargets})");
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[SpecialAbility] Chain prediction error: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// ★ v3.9.68: 게임 AbilityDeliverChain.CheckTarget 복제
        /// </summary>
        private static bool CheckChainTarget(AbilityData ability, AbilityDeliverChain chain, BaseUnitEntity unit)
        {
            if (ability.Caster == null) return false;
            if (unit.LifeState.IsDead && !chain.TargetDead) return false;
            if (chain.TargetType == TargetType.Enemy && !ability.Caster.IsEnemy(unit)) return false;
            if (chain.TargetType == TargetType.Ally && ability.Caster.IsEnemy(unit)) return false;
            return true;
        }

        /// <summary>
        /// 연쇄 효과용 유효 타겟 수 계산
        /// ★ v3.9.68: PredictChainTargets 기반으로 교체
        /// </summary>
        public static int CountChainTargets(AbilityData ability, BaseUnitEntity initialTarget, List<BaseUnitEntity> enemies)
        {
            return PredictChainTargets(ability, initialTarget).Count;
        }

        #endregion

        #region Debuff Detection

        /// <summary>
        /// 타겟에 디버프가 있는지 확인
        /// ★ v3.8.61: String 키워드 매칭 → 게임 API (적 시전 버프 = 디버프)
        /// </summary>
        public static bool HasDebuff(BaseUnitEntity target)
        {
            if (target == null) return false;

            try
            {
                // DOT 존재 = 디버프
                if (target.GetOptional<DOTLogic.PartDOTDirector>() != null)
                    return true;

                // 적이 부여한 버프 = 디버프 (게임의 BuffUIGroup.Enemy 분류 기준)
                foreach (var buff in target.Buffs.Enumerable)
                {
                    var caster = buff.Context?.MaybeCaster as BaseUnitEntity;
                    if (caster != null && target.CombatGroup?.IsEnemy(caster) == true)
                        return true;
                    if (buff.Blueprint != null && buff.Blueprint.IsDOTVisual)
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
        /// ★ v3.8.61: String 키워드 매칭 → 게임 API
        /// </summary>
        public static int CountDebuffs(BaseUnitEntity target)
        {
            if (target == null) return 0;

            int count = 0;

            try
            {
                foreach (var buff in target.Buffs.Enumerable)
                {
                    var caster = buff.Context?.MaybeCaster as BaseUnitEntity;
                    if (caster != null && target.CombatGroup?.IsEnemy(caster) == true)
                    {
                        count++;
                        continue;
                    }
                    if (buff.Blueprint != null && buff.Blueprint.IsDOTVisual)
                        count++;
                }
            }
            catch (Exception ex) { Main.LogDebug($"[SpecialAbility] CountDebuffs error: {ex.Message}"); }

            return count;
        }

        #endregion

        #region Combo Detection

        /// <summary>
        /// 능력이 Burning DoT를 적용하는지 확인
        /// </summary>
        /// ★ v3.8.60: String 폴백 제거 — GUID 전용
        public static bool AppliesBurningDOT(AbilityData ability)
        {
            if (ability == null) return false;

            string guid = AbilityDatabase.GetGuid(ability);
            return !string.IsNullOrEmpty(guid) && BurningDOTAbilities.Contains(guid);
        }

        /// <summary>
        /// DoT 강화 능력인지 확인
        /// </summary>
        /// ★ v3.8.60: String 폴백 제거 — GUID 전용
        public static bool IsDOTIntensifyAbility(AbilityData ability)
        {
            if (ability == null) return false;

            string guid = AbilityDatabase.GetGuid(ability);
            return !string.IsNullOrEmpty(guid) && DOTIntensifyAbilities.Contains(guid);
        }

        /// <summary>
        /// 연쇄 효과 능력인지 확인
        /// </summary>
        /// ★ v3.8.60: String 폴백 제거 — GUID 전용
        public static bool IsChainEffectAbility(AbilityData ability)
        {
            if (ability == null) return false;

            string guid = AbilityDatabase.GetGuid(ability);
            return !string.IsNullOrEmpty(guid) && ChainEffectAbilities.Contains(guid);
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

        /// <summary>
        /// 내부 DOTType → 게임 DOT enum 변환
        /// 두 enum의 값이 동일 (Bleeding=0, Burning=1, ... AssassinHaemorrhage=6)
        /// </summary>
        private static DOT MapToGameDOT(DOTType dotType)
        {
            return (DOT)(int)dotType;
        }

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
