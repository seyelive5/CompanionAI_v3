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
    }
}
