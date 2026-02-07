using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using UnityEngine;
using CompanionAI_v3.Data;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// 전투 유틸리티 - SituationAnalyzer와 TurnPlanner에서 사용
    /// v2.2에서 포팅 - 계획 수립 시점에 사용되는 순수 함수들
    /// </summary>
    public static class CombatHelpers
    {
        #region AOE Detection

        /// <summary>
        /// ★ v3.5.74: 능력이 AoE인지 확인 (게임 API만 사용 - 문자열 폴백 제거)
        /// </summary>
        public static bool IsAoEAbility(AbilityData ability)
        {
            if (ability == null) return false;

            // ★ v3.5.74: v3.5.73 AttackCategory API 활용
            if (CombatAPI.GetAttackCategory(ability) == AttackCategory.AoE)
                return true;

            // 게임 네이티브 API
            if (ability.IsAOE) return true;
            if (ability.GetPatternSettings() != null) return true;

            var bp = ability.Blueprint;
            if (bp != null && bp.CanTargetPoint && bp.AoERadius > 0) return true;

            // ★ v3.5.74: 문자열 기반 폴백 제거 - 게임 API만 신뢰
            return false;
        }

        /// <summary>
        /// ★ v3.8.50: 공격 목록에 근접 AOE 능력이 있는지 확인
        /// </summary>
        public static bool HasMeleeAoEAbility(IList<AbilityData> availableAttacks)
        {
            if (availableAttacks == null) return false;
            for (int i = 0; i < availableAttacks.Count; i++)
            {
                if (CombatAPI.IsMeleeAoEAbility(availableAttacks[i]))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// ★ v3.8.50: 가장 좋은 근접 AOE 능력 반환 (가장 넓은 패턴)
        /// </summary>
        public static AbilityData GetBestMeleeAoEAbility(IList<AbilityData> availableAttacks)
        {
            if (availableAttacks == null) return null;
            AbilityData best = null;
            float bestRadius = 0f;
            for (int i = 0; i < availableAttacks.Count; i++)
            {
                var a = availableAttacks[i];
                if (!CombatAPI.IsMeleeAoEAbility(a)) continue;
                float r = CombatAPI.GetAoERadius(a);
                if (r <= 0) r = 2f; // 근접 기본 반경
                if (best == null || r > bestRadius)
                {
                    best = a;
                    bestRadius = r;
                }
            }
            return best;
        }

        #endregion

        #region Grenade Detection

        /// <summary>
        /// ★ v3.7.65: 수류탄/폭발물인지 확인 (게임 API 기반 - 키워드 매칭 제거)
        /// </summary>
        public static bool IsGrenadeOrExplosive(AbilityData ability)
        {
            if (ability == null) return false;

            // ★ v3.7.65: 게임 네이티브 API 사용 (키워드 매칭 제거)
            var bp = ability.Blueprint;
            if (bp != null && bp.IsGrenade)
                return true;

            return false;
        }

        #endregion

        #region Unit Counting

        /// <summary>
        /// ★ v3.5.98: 적 위치 근처의 아군 수 (radius는 타일 단위)
        /// 기본값 5타일 ≈ 6.75m
        /// </summary>
        public static int CountAlliesNearEnemy(
            BaseUnitEntity caster,
            BaseUnitEntity enemy,
            List<BaseUnitEntity> allies,
            float radius = 5f)  // 타일 (기존 7m ≈ 5타일)
        {
            if (enemy == null) return 0;
            return CountAlliesNearPosition(caster, enemy.Position, allies, radius);
        }

        /// <summary>
        /// ★ v3.5.98: 특정 위치 근처의 아군 수 (radius는 타일 단위)
        /// </summary>
        public static int CountAlliesNearPosition(
            BaseUnitEntity caster,
            Vector3 position,
            List<BaseUnitEntity> allies,
            float radius = 5f)  // 타일
        {
            int count = 0;

            if (allies != null)
            {
                foreach (var ally in allies)
                {
                    if (ally == null || ally.LifeState.IsDead) continue;

                    // ★ v3.5.98: 타일 단위로 변환
                    float distance = CombatAPI.MetersToTiles(Vector3.Distance(ally.Position, position));
                    if (distance <= radius)
                    {
                        count++;
                    }
                }
            }

            if (caster != null)
            {
                // ★ v3.5.98: 타일 단위로 변환
                float selfDistance = CombatAPI.MetersToTiles(Vector3.Distance(caster.Position, position));
                if (selfDistance <= radius && selfDistance > 0.4f)  // 0.4타일 ≈ 0.5m
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// ★ v3.5.98: 특정 위치 근처의 적 수 (radius는 타일 단위)
        /// 기본값 4타일 ≈ 5.4m
        /// </summary>
        public static int CountEnemiesNearPosition(
            Vector3 position,
            List<BaseUnitEntity> enemies,
            float radius = 4f)  // 타일 (기존 5m ≈ 3.7타일 → 4타일로 반올림)
        {
            int count = 0;

            if (enemies != null)
            {
                foreach (var enemy in enemies)
                {
                    if (enemy == null || enemy.LifeState.IsDead) continue;

                    // ★ v3.5.98: 타일 단위로 변환
                    float distance = CombatAPI.MetersToTiles(Vector3.Distance(enemy.Position, position));
                    if (distance <= radius)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        #endregion

        #region AOE Safety

        /// <summary>
        /// ★ v3.5.98: AoE 능력 사용이 안전한지 확인
        /// 반경 2타일 (≈2.7m), 아군 2명까지 허용
        /// </summary>
        public static bool IsAoESafe(
            AbilityData ability,
            BaseUnitEntity caster,
            BaseUnitEntity enemy,
            List<BaseUnitEntity> allies)
        {
            if (!IsAoEAbility(ability)) return true;

            int alliesNear = CountAlliesNearEnemy(caster, enemy, allies, 2f);  // 타일 (기존 2.5m ≈ 2타일)
            return alliesNear <= 2;
        }

        /// <summary>
        /// ★ v3.5.98: 수류탄 사용이 효율적인지 확인 (적 2명 이상)
        /// </summary>
        public static bool IsGrenadeEfficient(
            AbilityData ability,
            BaseUnitEntity target,
            List<BaseUnitEntity> allEnemies)
        {
            if (!IsGrenadeOrExplosive(ability)) return true;
            if (target == null) return false;

            int enemiesNear = CountEnemiesNearPosition(target.Position, allEnemies, 4f);  // 타일 (기존 5m ≈ 4타일)
            return enemiesNear >= 2;
        }

        /// <summary>
        /// ★ v3.5.98: AoE 공격이 효율적인지 확인 (적 2명 이상 + 아군 안전)
        /// </summary>
        public static bool ShouldUseAoE(
            AbilityData ability,
            BaseUnitEntity caster,
            BaseUnitEntity target,
            List<BaseUnitEntity> allEnemies,
            List<BaseUnitEntity> allies)
        {
            if (!IsAoEAbility(ability)) return false;
            if (target == null) return false;

            // 아군 안전성 (반경 2타일, 2명까지 허용)
            int alliesNear = CountAlliesNearEnemy(caster, target, allies, 2f);  // 타일
            if (alliesNear > 2) return false;

            // 적 수 확인 (4타일 반경) - 2명 이상이면 효율적
            int enemiesNear = CountEnemiesNearPosition(target.Position, allEnemies, 4f);  // 타일
            return enemiesNear >= 2;
        }

        #endregion

        #region Weapon Attack Type

        /// <summary>
        /// 무기 공격 유형
        /// </summary>
        public enum WeaponAttackType
        {
            Unknown,
            Single,     // 단발
            Burst,      // 점사
            Scatter,    // 산탄/확산 AoE
            Grenade,    // 수류탄/폭발물
            Melee       // 근접
        }

        /// <summary>
        /// ★ v3.5.74: 무기 공격 유형 분류 (게임 API 기반 - 문자열 폴백 제거)
        /// </summary>
        public static WeaponAttackType GetWeaponAttackType(AbilityData ability)
        {
            if (ability == null) return WeaponAttackType.Unknown;

            // ★ v3.5.74: v3.5.73 AttackCategory API 활용
            var category = CombatAPI.GetAttackCategory(ability);

            // 수류탄/폭발물 (IsGrenadeOrExplosive는 유지 - 특수 케이스)
            if (IsGrenadeOrExplosive(ability))
                return WeaponAttackType.Grenade;

            // AttackCategory → WeaponAttackType 매핑
            switch (category)
            {
                case AttackCategory.Scatter:
                    return WeaponAttackType.Scatter;

                case AttackCategory.Burst:
                    return WeaponAttackType.Burst;

                case AttackCategory.SingleTarget:
                    return WeaponAttackType.Single;

                case AttackCategory.AoE:
                    return WeaponAttackType.Scatter;  // AoE는 Scatter로 처리

                case AttackCategory.GapCloser:
                    return WeaponAttackType.Melee;  // GapCloser는 근접으로 처리
            }

            // 게임 네이티브 API로 근접 체크
            if (ability.IsMelee)
                return WeaponAttackType.Melee;

            // 기본값
            return WeaponAttackType.Single;
        }

        #endregion

        #region Attack Priority Scoring

        /// <summary>
        /// ★ v3.5.98: 상황에 맞는 최적 공격 우선순위 (낮을수록 우선)
        /// TurnPlanner.SelectBestAttack에서 사용
        /// </summary>
        public static int GetAttackPriority(
            AbilityData ability,
            BaseUnitEntity caster,
            BaseUnitEntity target,
            List<BaseUnitEntity> allEnemies,
            List<BaseUnitEntity> allies)
        {
            var attackType = GetWeaponAttackType(ability);
            // ★ v3.5.98: 타일 단위 사용 (기존 5m → 4타일, 3m → 2타일)
            int enemiesNear = target != null ? CountEnemiesNearPosition(target.Position, allEnemies, 4f) : 1;
            int alliesNear = target != null ? CountAlliesNearEnemy(caster, target, allies, 2f) : 0;

            // 무기 공격 여부 확인
            bool isWeaponAttack = ability.Weapon != null;

            // 기본 우선순위 (낮을수록 우선)
            int priority = isWeaponAttack ? 50 : 150;

            switch (attackType)
            {
                case WeaponAttackType.Single:
                    priority = isWeaponAttack
                        ? (enemiesNear == 1 ? 5 : 15)
                        : (enemiesNear == 1 ? 50 : 80);
                    break;

                case WeaponAttackType.Burst:
                    priority = isWeaponAttack
                        ? (enemiesNear <= 2 ? 8 : 20)
                        : (enemiesNear <= 2 ? 60 : 90);
                    break;

                case WeaponAttackType.Scatter:
                    if (enemiesNear >= 2 && alliesNear <= 2)
                        priority = isWeaponAttack ? 3 : 40;
                    else if (alliesNear > 2)
                        priority = 200; // 아군 3명 이상 - 사용 안함
                    else
                        priority = isWeaponAttack ? 25 : 70;
                    break;

                case WeaponAttackType.Grenade:
                    if (enemiesNear >= 2 && alliesNear <= 1)
                        priority = 2; // 최우선
                    else if (alliesNear > 1)
                        priority = 300; // 아군 2명 이상 - 사용 안함
                    else
                        priority = 150; // 적 1명만 - 비효율
                    break;

                case WeaponAttackType.Melee:
                    priority = isWeaponAttack ? 10 : 55;
                    break;

                default:
                    priority = isWeaponAttack ? 50 : 150;
                    break;
            }

            return priority;
        }

        #endregion

        #region RangePreference Helpers

        /// <summary>
        /// 원거리 선호 여부 (Single Source of Truth)
        /// </summary>
        public static bool ShouldPreferRanged(CharacterSettings settings)
        {
            if (settings == null) return false;
            return settings.RangePreference == RangePreference.PreferRanged;
        }

        /// <summary>
        /// 근접 선호 여부 (Single Source of Truth)
        /// </summary>
        public static bool ShouldPreferMelee(CharacterSettings settings)
        {
            if (settings == null) return false;
            return settings.RangePreference == RangePreference.PreferMelee;
        }

        /// <summary>
        /// 능력이 선호하는 무기 타입인지 확인
        /// ★ v3.0.51: Grenade도 원거리에 포함
        /// </summary>
        public static bool IsPreferredWeaponType(AbilityData ability, RangePreference preference)
        {
            if (ability == null) return false;

            var weaponType = GetWeaponAttackType(ability);
            // ★ v3.0.51: Grenade도 원거리 공격에 포함
            bool isRanged = weaponType == WeaponAttackType.Single ||
                           weaponType == WeaponAttackType.Burst ||
                           weaponType == WeaponAttackType.Scatter ||
                           weaponType == WeaponAttackType.Grenade;
            bool isMelee = weaponType == WeaponAttackType.Melee;

            switch (preference)
            {
                case RangePreference.PreferRanged:
                    return isRanged;
                case RangePreference.PreferMelee:
                    return isMelee;
                default: // Adaptive
                    return true;
            }
        }

        /// <summary>
        /// RangePreference에 따라 능력 리스트 필터링
        /// ★ v3.0.51: Grenade도 원거리에 포함
        /// </summary>
        public static List<AbilityData> FilterAbilitiesByRangePreference(
            List<AbilityData> abilities,
            RangePreference preference)
        {
            if (abilities == null || abilities.Count == 0)
                return abilities;

            if (preference == RangePreference.PreferRanged)
            {
                // ★ v3.0.51: Grenade도 원거리 공격에 포함 (수류탄은 투척 원거리 무기)
                var rangedOnly = abilities.Where(a => {
                    var weaponType = GetWeaponAttackType(a);
                    return weaponType == WeaponAttackType.Single ||
                           weaponType == WeaponAttackType.Burst ||
                           weaponType == WeaponAttackType.Scatter ||
                           weaponType == WeaponAttackType.Grenade;  // ★ 수류탄 추가
                }).ToList();

                if (rangedOnly.Count > 0)
                {
                    Main.LogDebug($"[CombatHelpers] RangeFilter: {preference} - {rangedOnly.Count} ranged (filtered {abilities.Count - rangedOnly.Count} melee)");
                    return rangedOnly;
                }
                Main.LogDebug($"[CombatHelpers] No ranged abilities - fallback to all");
            }
            else if (preference == RangePreference.PreferMelee)
            {
                var meleeOnly = abilities.Where(a => {
                    var weaponType = GetWeaponAttackType(a);
                    return weaponType == WeaponAttackType.Melee;
                }).ToList();

                if (meleeOnly.Count > 0)
                {
                    Main.LogDebug($"[CombatHelpers] RangeFilter: PreferMelee - {meleeOnly.Count} melee (filtered {abilities.Count - meleeOnly.Count} ranged)");
                    return meleeOnly;
                }
                Main.LogDebug($"[CombatHelpers] No melee abilities - fallback to all");
            }

            return abilities;  // Adaptive: 필터 없음
        }

        #endregion

        #region Unit Helpers

        /// <summary>
        /// ★ v3.5.75: 유닛이 힐러인지 확인 (통합 - UtilityScorer, TargetScorer에서 중복 제거)
        /// </summary>
        public static bool IsHealer(BaseUnitEntity unit)
        {
            try
            {
                var abilities = unit?.Abilities?.Enumerable;
                if (abilities == null) return false;
                return abilities.Any(a => a?.Data != null && AbilityDatabase.IsHealing(a.Data));
            }
            catch { return false; }
        }

        #endregion
    }
}
