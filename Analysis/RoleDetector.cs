using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using CompanionAI_v3.Core;
using CompanionAI_v3.Data;
using CompanionAI_v3.Settings;
using CompanionAI_v3.GameInterface;

namespace CompanionAI_v3.Analysis
{
    /// <summary>
    /// ★ v3.0.92: 캐릭터 능력 기반 최적 역할 자동 감지
    /// </summary>
    public static class RoleDetector
    {
        // 역할별 점수
        private struct RoleScores
        {
            public int Tank;
            public int DPS;
            public int Support;

            public AIRole GetBestRole()
            {
                // 동점 시 우선순위: DPS > Tank > Support
                if (DPS >= Tank && DPS >= Support)
                    return AIRole.DPS;
                if (Tank >= Support)
                    return AIRole.Tank;
                return AIRole.Support;
            }

            public override string ToString()
                => $"Tank={Tank}, DPS={DPS}, Support={Support}";
        }

        /// <summary>
        /// 캐릭터의 능력을 분석하여 최적 역할 감지
        /// </summary>
        public static AIRole DetectOptimalRole(BaseUnitEntity unit)
        {
            if (unit == null) return AIRole.DPS;

            var scores = new RoleScores();
            var abilities = unit.Abilities?.Visible?.ToList();

            if (abilities == null || abilities.Count == 0)
            {
                // 능력이 없으면 무기 기반 판단
                return DetectRoleFromWeapons(unit);
            }

            // 1. 능력 분석
            foreach (var ability in abilities)
            {
                AnalyzeAbility(ability.Data, ref scores);
            }

            // 2. 무기 분석 (보조 점수)
            AnalyzeWeapons(unit, ref scores);

            // 3. 스탯 분석 (보조 점수)
            AnalyzeStats(unit, ref scores);

            var detectedRole = scores.GetBestRole();

            Main.Log($"[RoleDetector] {unit.CharacterName}: {scores} → {detectedRole}");

            return detectedRole;
        }

        /// <summary>
        /// 개별 능력 분석 (AbilityData 사용)
        /// </summary>
        private static void AnalyzeAbility(AbilityData abilityData, ref RoleScores scores)
        {
            if (abilityData == null) return;

            var timing = AbilityDatabase.GetTiming(abilityData);
            var bp = abilityData.Blueprint;

            // Tank 관련 능력
            if (AbilityDatabase.IsTaunt(abilityData))
            {
                scores.Tank += 5;
            }
            // ★ v3.0.92: 자기 버프 (방어/탱커 성향) - GUID 기반
            if (timing == AbilityTiming.PreCombatBuff && bp?.CanTargetSelf == true && bp?.CanTargetFriends != true)
            {
                // 자기만 대상인 버프 = 탱커/딜러 성향
                scores.Tank += 2;
                scores.DPS += 1;
            }
            if (AbilityDatabase.IsGapCloser(abilityData))
            {
                scores.Tank += 2;
                scores.DPS += 1;
            }

            // DPS 관련 능력
            if (AbilityDatabase.IsFinisher(abilityData))
            {
                scores.DPS += 5;
            }
            if (AbilityDatabase.IsHeroicAct(abilityData))
            {
                scores.DPS += 4;
            }
            if (timing == AbilityTiming.RighteousFury)
            {
                scores.DPS += 3;
            }
            if (AbilityDatabase.IsDOTIntensify(abilityData) || AbilityDatabase.IsChainEffect(abilityData))
            {
                scores.DPS += 2;
            }

            // Support 관련 능력
            if (timing == AbilityTiming.Healing)
            {
                // 아군 대상 힐
                if (bp?.CanTargetFriends == true)
                    scores.Support += 5;
                else
                    scores.Support += 2;  // 자가 힐
            }
            if (timing == AbilityTiming.PreCombatBuff && bp?.CanTargetFriends == true)
            {
                scores.Support += 4;
            }
            if (timing == AbilityTiming.Debuff)
            {
                scores.Support += 2;
                scores.DPS += 1;
            }

            // 근접/원거리 분석
            if (abilityData.IsMelee)
            {
                scores.Tank += 1;
                scores.DPS += 1;
            }
            else if (bp?.IsWeaponAbility == true)
            {
                // 원거리 무기 공격
                scores.DPS += 1;
                scores.Support += 1;
            }
        }

        /// <summary>
        /// 무기 분석 - Blueprint 프로퍼티 기반 (이름 기반 체크 없음)
        /// </summary>
        private static void AnalyzeWeapons(BaseUnitEntity unit, ref RoleScores scores)
        {
            bool hasMelee = false;
            bool hasRanged = false;

            var body = unit.Body;
            if (body != null)
            {
                // 주무기 확인
                var primaryWeapon = body.PrimaryHand?.MaybeWeapon;
                if (primaryWeapon != null)
                {
                    if (primaryWeapon.Blueprint?.IsMelee == true)
                        hasMelee = true;
                    if (primaryWeapon.Blueprint?.IsRanged == true)
                        hasRanged = true;
                }

                // 보조무기 확인
                var secondaryWeapon = body.SecondaryHand?.MaybeWeapon;
                if (secondaryWeapon != null)
                {
                    if (secondaryWeapon.Blueprint?.IsMelee == true)
                        hasMelee = true;
                    if (secondaryWeapon.Blueprint?.IsRanged == true)
                        hasRanged = true;
                }
            }

            // 점수 반영 - 무기 타입만으로 판단
            if (hasMelee && !hasRanged)
            {
                scores.Tank += 2;
                scores.DPS += 1;
            }
            else if (hasRanged && !hasMelee)
            {
                scores.DPS += 1;
                scores.Support += 1;
            }
            // 근접+원거리 혼합 보유 시 DPS 성향
            else if (hasMelee && hasRanged)
            {
                scores.DPS += 2;
            }
        }

        /// <summary>
        /// 스탯 분석
        /// </summary>
        private static void AnalyzeStats(BaseUnitEntity unit, ref RoleScores scores)
        {
            // HP 기반 분석
            int maxHP = CombatAPI.GetActualMaxHP(unit);

            // 높은 HP → Tank 성향
            if (maxHP >= 100)
            {
                scores.Tank += 2;
            }
            else if (maxHP <= 50)
            {
                scores.Support += 1;  // 낮은 HP는 후방 역할
            }

            // 방어구 분석 (있다면)
            // TODO: 방어구 종류에 따른 추가 분석
        }

        /// <summary>
        /// 능력이 없을 때 무기만으로 역할 결정
        /// </summary>
        private static AIRole DetectRoleFromWeapons(BaseUnitEntity unit)
        {
            var body = unit.Body;
            if (body == null) return AIRole.DPS;

            var primaryWeapon = body.PrimaryHand?.MaybeWeapon;
            if (primaryWeapon == null) return AIRole.DPS;

            if (primaryWeapon.Blueprint?.IsMelee == true)
                return AIRole.Tank;  // 근접 무기 → Tank

            return AIRole.DPS;  // 원거리 무기 → DPS
        }

        /// <summary>
        /// 감지된 역할과 함께 설명 문자열 반환 (UI용)
        /// </summary>
        public static string GetDetectionSummary(BaseUnitEntity unit)
        {
            if (unit == null) return "Unknown";

            var role = DetectOptimalRole(unit);
            var abilities = unit.Abilities?.Visible?.ToList() ?? new List<Ability>();

            // 핵심 능력 찾기
            var keyAbilities = new List<string>();

            foreach (var ability in abilities.Take(10))  // 처음 10개만 확인
            {
                var abilityData = ability.Data;
                var timing = AbilityDatabase.GetTiming(abilityData);

                if (AbilityDatabase.IsTaunt(abilityData))
                    keyAbilities.Add($"Taunt: {ability.Name}");
                else if (AbilityDatabase.IsFinisher(abilityData))
                    keyAbilities.Add($"Finisher: {ability.Name}");
                else if (AbilityDatabase.IsHeroicAct(abilityData))
                    keyAbilities.Add($"Heroic: {ability.Name}");
                else if (timing == AbilityTiming.Healing && ability.Blueprint?.CanTargetFriends == true)
                    keyAbilities.Add($"Heal: {ability.Name}");
            }

            string keyAbilityStr = keyAbilities.Count > 0
                ? $"\nKey: {string.Join(", ", keyAbilities.Take(3))}"
                : "";

            return $"Detected: {role}{keyAbilityStr}";
        }
    }
}
