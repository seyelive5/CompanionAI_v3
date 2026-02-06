using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Enums;
using Kingmaker.UnitLogic.Parts;
using UnityEngine;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// ★ v3.7.00: Overseer 아키타입 사역마(Familiar) API
    /// - 사역마 감지 및 정보 조회
    /// - 4타일 반경 규칙 지원
    /// </summary>
    public static class FamiliarAPI
    {
        #region Familiar Detection

        /// <summary>
        /// 유닛이 사역마를 소유하고 있는지 확인
        /// </summary>
        public static bool HasFamiliar(BaseUnitEntity unit)
        {
            if (unit == null) return false;

            try
            {
                // IsMaster 속성 확인 (UnitPartPetOwner 존재 여부)
                return unit.IsMaster;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[FamiliarAPI] HasFamiliar error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 유닛의 사역마 가져오기
        /// </summary>
        public static BaseUnitEntity GetFamiliar(BaseUnitEntity unit)
        {
            if (unit == null) return null;

            try
            {
                var petOwner = unit.GetOptional<UnitPartPetOwner>();
                return petOwner?.PetUnit;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[FamiliarAPI] GetFamiliar error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 사역마 타입 가져오기
        /// </summary>
        public static PetType? GetFamiliarType(BaseUnitEntity unit)
        {
            if (unit == null) return null;

            try
            {
                var petOwner = unit.GetOptional<UnitPartPetOwner>();
                if (petOwner == null || petOwner.PetUnit == null)
                    return null;

                return petOwner.PetType;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[FamiliarAPI] GetFamiliarType error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 이 유닛이 사역마인지 확인 (Master가 있으면 사역마)
        /// </summary>
        public static bool IsFamiliar(BaseUnitEntity unit)
        {
            if (unit == null) return false;

            try
            {
                return unit.IsPet;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[FamiliarAPI] IsFamiliar error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 사역마의 주인(Master) 가져오기
        /// </summary>
        public static BaseUnitEntity GetMaster(BaseUnitEntity familiar)
        {
            if (familiar == null) return null;

            try
            {
                return familiar.Master;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[FamiliarAPI] GetMaster error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 사역마의 현재 위치 가져오기
        /// </summary>
        public static Vector3 GetFamiliarPosition(BaseUnitEntity master)
        {
            var familiar = GetFamiliar(master);
            if (familiar == null)
                return master?.Position ?? Vector3.zero;

            return familiar.Position;
        }

        /// <summary>
        /// 사역마가 의식이 있는지 확인
        /// </summary>
        public static bool IsFamiliarConscious(BaseUnitEntity master)
        {
            var familiar = GetFamiliar(master);
            if (familiar == null) return false;

            try
            {
                return familiar.IsConscious;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Radius Calculations (4-Tile Rule)

        /// <summary>
        /// 4타일 반경을 미터로 변환
        /// </summary>
        public const float FAMILIAR_EFFECT_RADIUS_TILES = 4f;
        public const float FAMILIAR_EFFECT_RADIUS_METERS = 4f * 1.35f;  // 약 5.4m

        /// <summary>
        /// 지정 위치에서 반경 내 아군 수 계산
        /// </summary>
        public static int CountAlliesInRadius(Vector3 position, float radiusTiles, List<BaseUnitEntity> allies)
        {
            if (allies == null) return 0;

            float radiusMeters = radiusTiles * 1.35f;  // 타일 → 미터 변환
            int count = 0;

            foreach (var ally in allies)
            {
                if (ally == null || !ally.IsConscious) continue;
                if (IsFamiliar(ally)) continue;  // 사역마 제외

                float distance = Vector3.Distance(position, ally.Position);
                if (distance <= radiusMeters)
                    count++;
            }

            return count;
        }

        /// <summary>
        /// 지정 위치에서 반경 내 적 수 계산
        /// </summary>
        public static int CountEnemiesInRadius(Vector3 position, float radiusTiles, List<BaseUnitEntity> enemies)
        {
            if (enemies == null) return 0;

            float radiusMeters = radiusTiles * 1.35f;
            int count = 0;

            foreach (var enemy in enemies)
            {
                if (enemy == null || !enemy.IsConscious) continue;

                float distance = Vector3.Distance(position, enemy.Position);
                if (distance <= radiusMeters)
                    count++;
            }

            return count;
        }

        /// <summary>
        /// 반경 내 아군 목록 가져오기
        /// </summary>
        public static List<BaseUnitEntity> GetAlliesInRadius(Vector3 position, float radiusTiles, List<BaseUnitEntity> allies)
        {
            var result = new List<BaseUnitEntity>();
            if (allies == null) return result;

            float radiusMeters = radiusTiles * 1.35f;

            foreach (var ally in allies)
            {
                if (ally == null || !ally.IsConscious) continue;
                if (IsFamiliar(ally)) continue;

                float distance = Vector3.Distance(position, ally.Position);
                if (distance <= radiusMeters)
                    result.Add(ally);
            }

            return result;
        }

        /// <summary>
        /// 반경 내 적 목록 가져오기
        /// </summary>
        public static List<BaseUnitEntity> GetEnemiesInRadius(Vector3 position, float radiusTiles, List<BaseUnitEntity> enemies)
        {
            var result = new List<BaseUnitEntity>();
            if (enemies == null) return result;

            float radiusMeters = radiusTiles * 1.35f;

            foreach (var enemy in enemies)
            {
                if (enemy == null || !enemy.IsConscious) continue;

                float distance = Vector3.Distance(position, enemy.Position);
                if (distance <= radiusMeters)
                    result.Add(enemy);
            }

            return result;
        }

        #endregion

        #region Familiar Type Helpers

        /// <summary>
        /// 버프 확산형 사역마인지 (Servo-Skull, Psyber-Raven)
        /// </summary>
        public static bool IsBuffSpreadFamiliar(PetType? type)
        {
            return type == PetType.ServoskullSwarm || type == PetType.Raven;
        }

        /// <summary>
        /// 적 제어형 사역마인지 (Cyber-Mastiff, Cyber-Eagle)
        /// </summary>
        public static bool IsEnemyControlFamiliar(PetType? type)
        {
            return type == PetType.Mastiff || type == PetType.Eagle;
        }

        /// <summary>
        /// 사역마 타입 이름 (디버그용)
        /// </summary>
        public static string GetFamiliarTypeName(PetType? type)
        {
            if (!type.HasValue) return "None";

            return type.Value switch
            {
                PetType.ServoskullSwarm => "Servo-Skull Swarm",
                PetType.Raven => "Psyber-Raven",
                PetType.Mastiff => "Cyber-Mastiff",
                PetType.Eagle => "Cyber-Eagle",
                PetType.Servitor => "Servitor",
                _ => type.Value.ToString()
            };
        }

        #endregion

        #region Overcharge (HeroicAct) Detection

        /// <summary>
        /// ★ v3.7.69: Raven Master가 Overcharge(HeroicAct) 상태인지 확인
        /// Overcharge가 활성화되어야만 공격형 사이킥도 Warp Relay 가능
        /// Overcharge 없이 PurificationDischarge 등 공격 사용 시 Raven에게 자해 데미지
        /// </summary>
        public static bool IsRavenOverchargeActive(BaseUnitEntity master)
        {
            if (master == null) return false;

            try
            {
                // MomentumRoot에서 HeroicActBuff 가져오기
                var momentumRoot = Kingmaker.Blueprints.Root.BlueprintRoot.Instance?.WarhammerRoot?.MomentumRoot;
                if (momentumRoot == null)
                {
                    Main.LogDebug("[FamiliarAPI] IsRavenOverchargeActive: MomentumRoot is null");
                    return false;
                }

                var heroicActBuff = momentumRoot.HeroicActBuff;
                if (heroicActBuff == null)
                {
                    Main.LogDebug("[FamiliarAPI] IsRavenOverchargeActive: HeroicActBuff is null");
                    return false;
                }

                // Master의 Facts에 HeroicActBuff가 있는지 확인
                bool hasOvercharge = master.Facts?.Contains(heroicActBuff) ?? false;

                if (hasOvercharge)
                {
                    Main.LogDebug($"[FamiliarAPI] {master.CharacterName}: Overcharge (HeroicAct) ACTIVE - attack abilities safe");
                }

                return hasOvercharge;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[FamiliarAPI] IsRavenOverchargeActive error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.7.69: Raven 공격 능력을 안전하게 사용할 수 있는지 확인
        /// - Overcharge 상태에서만 true
        /// - Overcharge 없으면 PurificationDischarge 등 사용 시 Raven 자해
        /// </summary>
        public static bool CanRavenUseAttackAbilities(BaseUnitEntity master)
        {
            if (master == null) return false;

            // Raven을 가진 Overseer인지 확인
            var familiarType = GetFamiliarType(master);
            if (familiarType != PetType.Raven) return false;

            // Overcharge 상태 확인
            return IsRavenOverchargeActive(master);
        }

        #endregion

        #region Diagnostics

        /// <summary>
        /// 사역마 상태 로깅 (디버그용)
        /// </summary>
        public static void LogFamiliarStatus(BaseUnitEntity unit)
        {
            if (unit == null) return;

            if (IsFamiliar(unit))
            {
                var master = GetMaster(unit);
                Main.LogDebug($"[FamiliarAPI] {unit.CharacterName}: Is Familiar, Master={master?.CharacterName ?? "None"}");
            }
            else if (HasFamiliar(unit))
            {
                var familiar = GetFamiliar(unit);
                var type = GetFamiliarType(unit);
                var conscious = IsFamiliarConscious(unit);
                Main.LogDebug($"[FamiliarAPI] {unit.CharacterName}: Has Familiar={GetFamiliarTypeName(type)}, " +
                    $"Conscious={conscious}, Position={familiar?.Position}");
            }
            else
            {
                Main.LogDebug($"[FamiliarAPI] {unit.CharacterName}: No Familiar");
            }
        }

        #endregion

        #region ★ v3.7.90: Familiar Ability Range

        /// <summary>
        /// ★ v3.7.90: 마스터의 사역마 대상 능력 중 최대 사거리 반환 (미터 단위)
        /// 후퇴 시 이 거리 이내로 제한하여 사역마 스킬 시전 가능 유지
        /// </summary>
        /// <param name="master">오버시어 마스터</param>
        /// <returns>최대 사역마 능력 사거리 (미터), 없으면 15f 기본값</returns>
        public static float GetMaxFamiliarAbilityRange(BaseUnitEntity master)
        {
            const float DEFAULT_RANGE = 15f;  // 기본값 (기존 하드코딩 값)
            const float MAX_RANGE_CAP = 20f;  // ★ v3.7.92: 최대 사거리 상한선 (너무 멀리 후퇴 방지)
            const int UNLIMITED_THRESHOLD_TILES = 50;  // ★ v3.7.92: 이 값 이상은 Unlimited로 간주

            if (master == null) return DEFAULT_RANGE;

            try
            {
                var familiar = GetFamiliar(master);
                if (familiar == null) return DEFAULT_RANGE;

                var familiarType = GetFamiliarType(master);
                if (familiarType == null) return DEFAULT_RANGE;

                float maxRange = 0f;
                int validAbilityCount = 0;

                // 마스터의 모든 능력 조회
                var abilities = master.Abilities?.RawFacts;
                if (abilities == null || abilities.Count == 0) return DEFAULT_RANGE;

                foreach (var fact in abilities)
                {
                    var ability = fact?.Data;
                    if (ability?.Blueprint == null) continue;

                    // 사역마 관련 능력만 체크
                    if (!Data.FamiliarAbilities.IsFamiliarAbility(ability) &&
                        !IsPetAbilityByName(ability.Blueprint.name))
                        continue;

                    // 사거리 추출 (타일 단위)
                    int rangeTiles = CombatAPI.GetAbilityRangeInTiles(ability);

                    // ★ v3.7.92: Unlimited 사거리 제외 (50타일 이상 또는 0 이하)
                    if (rangeTiles >= UNLIMITED_THRESHOLD_TILES || rangeTiles <= 0)
                    {
                        Main.LogDebug($"[FamiliarAPI] Skipping unlimited range ability: {ability.Name} ({rangeTiles} tiles)");
                        continue;
                    }

                    float rangeMeters = CombatAPI.TilesToMeters(rangeTiles);

                    if (rangeMeters > maxRange)
                    {
                        maxRange = rangeMeters;
                        validAbilityCount++;
                        Main.LogDebug($"[FamiliarAPI] Familiar ability range: {ability.Name} = {rangeMeters:F1}m ({rangeTiles} tiles)");
                    }
                }

                // 유효한 능력이 없으면 기본값
                if (validAbilityCount == 0 || maxRange < 5f)
                {
                    Main.LogDebug($"[FamiliarAPI] No valid range abilities found, using default {DEFAULT_RANGE}m");
                    return DEFAULT_RANGE;
                }

                // ★ v3.7.92: 최대 사거리 상한선 적용
                if (maxRange > MAX_RANGE_CAP)
                {
                    Main.LogDebug($"[FamiliarAPI] Capping range from {maxRange:F1}m to {MAX_RANGE_CAP}m");
                    maxRange = MAX_RANGE_CAP;
                }

                Main.LogDebug($"[FamiliarAPI] GetMaxFamiliarAbilityRange: {maxRange:F1}m for {master.CharacterName}");
                return maxRange;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[FamiliarAPI] GetMaxFamiliarAbilityRange error: {ex.Message}");
                return DEFAULT_RANGE;
            }
        }

        /// <summary>
        /// BlueprintName이 Pet 능력인지 확인 (접두사 기반)
        /// </summary>
        private static bool IsPetAbilityByName(string blueprintName)
        {
            if (string.IsNullOrEmpty(blueprintName)) return false;

            return blueprintName.StartsWith("MastiffPet_", StringComparison.OrdinalIgnoreCase) ||
                   blueprintName.StartsWith("EaglePet_", StringComparison.OrdinalIgnoreCase) ||
                   blueprintName.StartsWith("RavenPet_", StringComparison.OrdinalIgnoreCase) ||
                   blueprintName.StartsWith("ServoskullPet_", StringComparison.OrdinalIgnoreCase) ||
                   blueprintName.StartsWith("Pet_", StringComparison.OrdinalIgnoreCase);
        }

        #endregion
    }
}
