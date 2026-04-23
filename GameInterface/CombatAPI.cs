using System;
using Kingmaker.Blueprints;                      // ★ GetComponent<T> extension on BlueprintAbility
using Kingmaker.EntitySystem;                    // ★ v3.8.66: EntityHelper.DistanceToInCells 확장 메서드
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using Pathfinding;
using UnityEngine;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// 게임 API 래퍼 - 모든 게임 상호작용을 중앙화
    /// </summary>
    public static partial class CombatAPI
    {
        #region Unit Conversion - 타일 기준 (v3.5.98)

        /// <summary>
        /// ★ v3.5.98: 게임 그리드 셀 크기 (1 타일 = 1.35 미터)
        /// GraphParamsMechanicsCache.GridCellSize 참조
        /// </summary>
        public const float GridCellSize = 1.35f;

        /// <summary>미터 → 타일 변환</summary>
        public static float MetersToTiles(float meters) => meters / GridCellSize;

        /// <summary>타일 → 미터 변환 (필요시에만 사용)</summary>
        public static float TilesToMeters(float tiles) => tiles * GridCellSize;

        /// <summary>
        /// ★ v3.5.98: 두 유닛 간 거리를 타일 단위로 반환
        /// ★ v3.8.66: 게임 API 사용 — SizeRect 경계 간 최단 셀 거리 (대형 유닛 대응)
        /// 모든 거리 비교에 이 함수 사용
        /// </summary>
        public static float GetDistanceInTiles(BaseUnitEntity a, BaseUnitEntity b)
        {
            if (a == null || b == null) return float.MaxValue;
            try
            {
                // ★ v3.8.66: 게임 API — WarhammerGeometryUtils.DistanceToInCells (Chebyshev 변형)
                // 대형 유닛(2x2+)에서 center-to-center 대비 1~2타일 차이 보정
                return (float)a.DistanceToInCells(b);
            }
            // ★ v3.13.0: 로깅 추가 (기본값 MaxValue는 이미 보수적 — 도달 불가)
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetDistanceInTiles(unit,unit) failed: {ex.Message}");
                return float.MaxValue;
            }
        }

        /// <summary>
        /// ★ v3.5.98: 위치와 유닛 간 거리를 타일 단위로 반환
        /// ★ v3.8.66: 타겟 SizeRect 반영 (대형 유닛 대응)
        /// </summary>
        public static float GetDistanceInTiles(Vector3 position, BaseUnitEntity unit)
        {
            if (unit == null) return float.MaxValue;
            try
            {
                // ★ v3.8.66: 타겟 SizeRect 반영 — 위치는 1x1 점(IntRect(0,0,0,0))
                return (float)WarhammerGeometryUtils.DistanceToInCells(
                    position, new IntRect(0, 0, 0, 0),
                    unit.Position, unit.SizeRect);
            }
            // ★ v3.13.0: 로깅 추가
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetDistanceInTiles(pos,unit) failed: {ex.Message}");
                return float.MaxValue;
            }
        }

        /// <summary>
        /// ★ v3.5.98: 두 위치 간 거리를 타일 단위로 반환
        /// </summary>
        public static float GetDistanceInTiles(Vector3 a, Vector3 b)
        {
            float meters = Vector3.Distance(a, b);
            return meters / GridCellSize;
        }

        /// <summary>
        /// ★ v3.5.98: 능력 사거리를 타일 단위로 반환 (게임 API 사용)
        /// 기존 GetAbilityRange() 대체
        /// </summary>
        public static int GetAbilityRangeInTiles(AbilityData ability)
        {
            if (ability == null) return 0;
            try
            {
                return ability.RangeCells;  // 게임 공식 API - 타일 단위
            }
            catch
            {
                return 15;  // 폴백: 15타일
            }
        }

        /// <summary>
        /// ★ v3.7.46: MultiTarget 능력의 Point1 타겟팅 범위 반환
        ///
        /// MultiTarget 능력(예: Aerial Rush)은 각 Point마다 다른 능력 블루프린트를 사용함
        /// Point1 = TryGetNextTargetAbilityAndCaster(targetIndex=0)의 능력 범위
        /// </summary>
        public static int GetMultiTargetPoint1RangeInTiles(AbilityData rootAbility)
        {
            if (rootAbility == null) return 30;  // 폴백

            try
            {
                // IAbilityMultiTarget 컴포넌트 가져오기
                var multiTarget = rootAbility.Blueprint?.GetComponent<Kingmaker.UnitLogic.Abilities.Components.Base.IAbilityMultiTarget>();
                if (multiTarget == null)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetMultiTargetPoint1Range: No IAbilityMultiTarget component");
                    return rootAbility.RangeCells;  // MultiTarget이 아니면 기본 범위 반환
                }

                // Point1 (targetIndex=0)에 사용되는 능력 가져오기
                Kingmaker.UnitLogic.Abilities.Blueprints.BlueprintAbility point1Blueprint;
                Kingmaker.EntitySystem.Entities.MechanicEntity point1Caster;

                if (!multiTarget.TryGetNextTargetAbilityAndCaster(rootAbility, 0, out point1Blueprint, out point1Caster))
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetMultiTargetPoint1Range: TryGetNextTarget failed for index 0");
                    return 30;  // 폴백
                }

                if (point1Blueprint == null)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetMultiTargetPoint1Range: Point1 blueprint is null");
                    return 30;  // 폴백
                }

                // Point1 능력의 AbilityData 생성하여 RangeCells 가져오기
                var point1Ability = new AbilityData(point1Blueprint, point1Caster ?? rootAbility.Caster);
                int point1Range = point1Ability.RangeCells;

                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetMultiTargetPoint1Range: Point1 ability={point1Blueprint.name}, Range={point1Range} tiles");
                return point1Range;
            }
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetMultiTargetPoint1Range error: {ex.Message}");
                return 30;  // 폴백
            }
        }

        /// <summary>
        /// ★ v3.7.54: MultiTarget 능력의 Point2 타겟팅 범위 반환
        ///
        /// Aerial Rush Point2가 게임에서 거부되는 원인:
        /// - AI는 Eagle MP를 Point2 범위로 사용
        /// - 게임은 Support_Ascended_Ability.RangeCells로 검증
        /// - 이 두 값이 다르면 TargetRestrictionNotPassed 발생
        ///
        /// 해결: 게임이 실제로 사용하는 Point2 능력의 RangeCells를 반환
        /// </summary>
        public static int GetMultiTargetPoint2RangeInTiles(AbilityData rootAbility)
        {
            if (rootAbility == null) return 15;  // 폴백

            try
            {
                // IAbilityMultiTarget 컴포넌트 가져오기
                var multiTarget = rootAbility.Blueprint?.GetComponent<Kingmaker.UnitLogic.Abilities.Components.Base.IAbilityMultiTarget>();
                if (multiTarget == null)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetMultiTargetPoint2Range: No IAbilityMultiTarget component");
                    return 15;  // MultiTarget이 아니면 폴백
                }

                // Point2 (targetIndex=1)에 사용되는 능력 가져오기
                Kingmaker.UnitLogic.Abilities.Blueprints.BlueprintAbility point2Blueprint;
                Kingmaker.EntitySystem.Entities.MechanicEntity point2Caster;

                if (!multiTarget.TryGetNextTargetAbilityAndCaster(rootAbility, 1, out point2Blueprint, out point2Caster))
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetMultiTargetPoint2Range: TryGetNextTarget failed for index 1");
                    return 15;  // 폴백
                }

                if (point2Blueprint == null)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetMultiTargetPoint2Range: Point2 blueprint is null");
                    return 15;  // 폴백
                }

                // Point2 능력의 AbilityData 생성하여 RangeCells 가져오기
                // ★ caster는 Pet(Eagle) - AbilityMultiTarget.GetDelegateUnit() 참조
                var point2Ability = new AbilityData(point2Blueprint, point2Caster ?? rootAbility.Caster);
                int point2Range = point2Ability.RangeCells;

                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetMultiTargetPoint2Range: Point2 ability={point2Blueprint.name}, " +
                    $"Caster={(point2Caster as BaseUnitEntity)?.CharacterName ?? "unknown"}, Range={point2Range} tiles");
                return point2Range;
            }
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetMultiTargetPoint2Range error: {ex.Message}");
                return 15;  // 폴백
            }
        }

        #endregion
    }
}
