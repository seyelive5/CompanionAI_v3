using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.Blueprints;  // BlueprintExtenstions.GetComponent<T>
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic;  // ★ v3.7.89: AOO API (GetEngagedByUnits 확장 메서드)
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;  // BlueprintAbility.UsingInThreateningAreaType
using Kingmaker.UnitLogic.Mechanics.Actions;  // ★ v3.0.98: WarhammerContextActionRestoreActionPoints
using Kingmaker.UnitLogic.Parts;               // ★ v3.5.22: UnitPartSpringAttack
using UnityEngine;
using CompanionAI_v3.Data;      // BlueprintCache
using CompanionAI_v3.Settings;  // RangePreference

namespace CompanionAI_v3.GameInterface
{
    public static partial class CombatAPI
    {
        #region ★ v3.7.89: Attack of Opportunity (AOO) API

        /// <summary>
        /// 유닛이 현재 적의 위협 범위 내에 있는지 확인
        /// (적이 기회공격을 할 수 있는 상태인지)
        /// </summary>
        public static bool IsInThreateningArea(BaseUnitEntity unit)
        {
            if (unit == null) return false;
            try
            {
                var engagedBy = unit.GetEngagedByUnits(true);
                return engagedBy != null && engagedBy.Any();
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsInThreateningArea error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 유닛을 위협하는 적 목록 반환
        /// </summary>
        public static List<BaseUnitEntity> GetThreateningEnemies(BaseUnitEntity unit)
        {
            if (unit == null) return new List<BaseUnitEntity>();
            try
            {
                var engagedBy = unit.GetEngagedByUnits(true);
                return engagedBy?.ToList() ?? new List<BaseUnitEntity>();
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetThreateningEnemies error: {ex.Message}");
                return new List<BaseUnitEntity>();
            }
        }

        /// <summary>
        /// 능력 사용 시 기회공격이 발생하는지 확인
        /// </summary>
        /// <returns>
        /// true: AOO 발생
        /// false: AOO 없음 (안전하게 사용 가능)
        /// </returns>
        public static bool WillCauseAOO(AbilityData ability, BaseUnitEntity caster)
        {
            if (ability == null || caster == null) return false;
            try
            {
                // 1. 능력의 AOO 유형 확인
                var aooType = ability.UsingInThreateningArea;

                // AOO를 유발하지 않는 능력이면 false
                if (aooType != BlueprintAbility.UsingInThreateningAreaType.WillCauseAOO)
                    return false;

                // 2. 유닛이 위협 범위 내에 있는지 확인
                if (!IsInThreateningArea(caster))
                    return false;

                // 3. 사이커 예외: 사이커 능력은 AOO 면제 특성이 있을 수 있음
                // (게임 내 MechanicsFeatureType.PsychicPowersDoNotProvokeAoO)
                // 이 부분은 게임이 자체적으로 처리하므로 여기선 단순화

                return true;  // AOO 발생 예상
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] WillCauseAOO error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 능력이 위협 범위 내에서 사용 불가능한지 확인
        /// </summary>
        public static bool CannotUseInThreateningArea(AbilityData ability)
        {
            if (ability == null) return false;
            try
            {
                return ability.UsingInThreateningArea == BlueprintAbility.UsingInThreateningAreaType.CannotUse;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// AOO 정보를 포함한 능력 평가 결과
        /// </summary>
        public struct AOOCheckResult
        {
            public bool IsInThreatArea;      // 유닛이 위협 범위 내
            public bool WillTriggerAOO;       // 능력 사용 시 AOO 발생
            public bool CannotUseHere;        // 위협 범위 내 사용 불가
            public int ThreateningEnemyCount; // 위협하는 적 수
            public List<BaseUnitEntity> ThreateningEnemies;

            public bool IsSafe => !WillTriggerAOO && !CannotUseHere;
        }

        /// <summary>
        /// 능력 사용 시 AOO 상태 종합 체크
        /// </summary>
        public static AOOCheckResult CheckAOOStatus(AbilityData ability, BaseUnitEntity caster)
        {
            var result = new AOOCheckResult
            {
                ThreateningEnemies = new List<BaseUnitEntity>()
            };

            if (ability == null || caster == null)
                return result;

            try
            {
                result.ThreateningEnemies = GetThreateningEnemies(caster);
                result.ThreateningEnemyCount = result.ThreateningEnemies.Count;
                result.IsInThreatArea = result.ThreateningEnemyCount > 0;

                var aooType = ability.UsingInThreateningArea;

                result.CannotUseHere = result.IsInThreatArea &&
                    aooType == BlueprintAbility.UsingInThreateningAreaType.CannotUse;

                result.WillTriggerAOO = result.IsInThreatArea &&
                    aooType == BlueprintAbility.UsingInThreateningAreaType.WillCauseAOO;

                return result;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CheckAOOStatus error: {ex.Message}");
                return result;
            }
        }

        #endregion

        #region Retreat & Cover System

        // ★ v3.9.28: CoverLevel enum + GetCoverTypeAtPosition() 삭제
        // 거리 기반 가짜 추정이었음 (distance > 20m = Full, > 10m = Half)
        // BattlefieldInfluenceMap → GetCellCoverStatus (타일 펜스 기반)
        // SituationAnalyzer → GetWarhammerLos (실제 LOS 기반)으로 교체됨

        /// <summary>
        /// 후퇴 위치 찾기 - TurnPlanner.PlanRetreat에서 사용
        /// </summary>
        public static Vector3? FindRetreatPosition(
            BaseUnitEntity unit,
            BaseUnitEntity nearestEnemy,
            float minSafeDistance,
            List<BaseUnitEntity> allEnemies)
        {
            if (unit == null || nearestEnemy == null) return null;

            // 기본: 적으로부터 반대 방향
            var retreatDir = (unit.Position - nearestEnemy.Position).normalized;
            var baseRetreatPos = unit.Position + retreatDir * minSafeDistance * 1.5f;

            // 다른 적 확인
            if (allEnemies != null)
            {
                float bestScore = float.MinValue;
                Vector3 bestPos = baseRetreatPos;

                // 8방향 검색
                for (int i = 0; i < 8; i++)
                {
                    float angle = i * 45f * Mathf.Deg2Rad;
                    var dir = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
                    var testPos = unit.Position + dir * minSafeDistance * 1.5f;

                    float score = EvaluateRetreatPosition(testPos, allEnemies, minSafeDistance);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestPos = testPos;
                    }
                }

                return bestPos;
            }

            return baseRetreatPos;
        }

        private static float EvaluateRetreatPosition(Vector3 position, List<BaseUnitEntity> enemies, float minSafeDistance)
        {
            float score = 0f;

            foreach (var enemy in enemies)
            {
                if (enemy == null || enemy.LifeState.IsDead) continue;

                float dist = Vector3.Distance(position, enemy.Position);

                if (dist >= minSafeDistance)
                {
                    score += 10f;  // 안전 거리 확보
                }
                else
                {
                    score -= (minSafeDistance - dist) * 5f;  // 너무 가까우면 감점
                }
            }

            return score;
        }

        /// <summary>
        /// 후퇴가 필요한지 확인
        /// </summary>
        public static bool ShouldRetreat(
            BaseUnitEntity unit,
            RangePreference preference,
            float nearestEnemyDistance,
            float minSafeDistance)
        {
            // 원거리 선호가 아니면 후퇴 불필요
            if (preference != RangePreference.PreferRanged)
                return false;

            // 안전 거리 미만이면 후퇴 필요
            return nearestEnemyDistance < minSafeDistance;
        }

        #endregion

        #region Momentum System

        private const int MOMENTUM_START = 100;
        private const int MOMENTUM_HEROIC_THRESHOLD = 175;
        private const int MOMENTUM_DESPERATE_THRESHOLD = 50;

        /// <summary>
        /// 현재 Momentum 값
        /// </summary>
        public static int GetCurrentMomentum()
        {
            try
            {
                var momentumGroups = Game.Instance?.Player?.GetOrCreate<Kingmaker.Controllers.TurnBased.TurnDataPart>()?.MomentumGroups;
                if (momentumGroups == null) return MOMENTUM_START;

                foreach (var group in momentumGroups)
                {
                    if (group.IsParty)
                        return group.Momentum;
                }
                return MOMENTUM_START;
            }
            // ★ v3.13.0: 안전한 기본값 — 0 (모멘텀 불확실 시 Heroic Act 사용 안 함)
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetCurrentMomentum failed: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Heroic Act 사용 가능 여부 (Momentum 175+)
        /// </summary>
        public static bool IsHeroicActAvailable()
        {
            return GetCurrentMomentum() >= MOMENTUM_HEROIC_THRESHOLD;
        }

        /// <summary>
        /// Desperate Measure 활성 여부 (Momentum <= 50)
        /// </summary>
        public static bool IsDesperateMeasureActive()
        {
            return GetCurrentMomentum() <= MOMENTUM_DESPERATE_THRESHOLD;
        }

        /// <summary>
        /// Momentum 상태 문자열
        /// </summary>
        public static string GetMomentumStatusString()
        {
            int momentum = GetCurrentMomentum();
            string status;

            if (momentum >= 175)
                status = "HEROIC";
            else if (momentum >= 100)
                status = "High";
            else if (momentum >= 50)
                status = "Normal";
            else if (momentum > 25)
                status = "Low";
            else
                status = "DESPERATE";

            return $"Momentum: {momentum} ({status})";
        }

        #endregion

        #region Resource Prediction

        /// <summary>
        /// ★ v3.0.98: 능력이 MP를 회복하는지 확인하고 예상 회복량 반환
        /// Blueprint의 WarhammerContextActionRestoreActionPoints 컴포넌트에서 직접 읽어옴
        /// </summary>
        public static float GetAbilityMPRecovery(AbilityData ability, BaseUnitEntity caster = null)
        {
            if (ability?.Blueprint == null) return 0f;

            try
            {
                // AbilityEffectRunAction 컴포넌트에서 Actions 확인
                // ★ v3.8.62: BlueprintCache 캐시 사용 (GetComponent O(n) → O(1))
                var runAction = BlueprintCache.GetCachedRunAction(ability.Blueprint);
                if (runAction?.Actions?.Actions == null) return 0f;

                foreach (var action in runAction.Actions.Actions)
                {
                    // WarhammerContextActionRestoreActionPoints 찾기
                    if (action is WarhammerContextActionRestoreActionPoints restoreAction)
                    {
                        // MovePointsToMax가 true면 최대 MP 반환 (보수적으로 10 추정)
                        if (restoreAction.MovePointsToMax)
                        {
                            return 10f;
                        }

                        // MovePoints 값 계산 (ContextValue)
                        // ContextValue는 정적 값이거나 스탯 기반일 수 있음
                        var movePoints = restoreAction.MovePoints;
                        if (movePoints != null)
                        {
                            // 정적 값이 있으면 사용
                            int staticValue = movePoints.Value;
                            if (staticValue > 0)
                            {
                                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] {ability.Name}: MP recovery = {staticValue}");
                                return staticValue;
                            }

                            // 런타임 계산 필요 (캐스터 컨텍스트 기반)
                            // 보수적으로 기본값 반환
                            return 6f;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAbilityMPRecovery error: {ex.Message}");
            }

            return 0f;
        }

        /// <summary>
        /// ★ v3.0.98: 능력이 AP를 회복하는지 확인하고 예상 회복량 반환
        /// </summary>
        public static float GetAbilityAPRecovery(AbilityData ability, BaseUnitEntity caster = null)
        {
            if (ability?.Blueprint == null) return 0f;

            try
            {
                // ★ v3.8.62: BlueprintCache 캐시 사용 (GetComponent O(n) → O(1))
                var runAction = BlueprintCache.GetCachedRunAction(ability.Blueprint);
                if (runAction?.Actions?.Actions == null) return 0f;

                foreach (var action in runAction.Actions.Actions)
                {
                    if (action is WarhammerContextActionRestoreActionPoints restoreAction)
                    {
                        if (restoreAction.ActionPointsToMax)
                        {
                            return 5f;  // 최대 AP (보수적 추정)
                        }

                        var actionPoints = restoreAction.ActionPoints;
                        if (actionPoints != null)
                        {
                            int staticValue = actionPoints.Value;
                            if (staticValue > 0)
                            {
                                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] {ability.Name}: AP recovery = {staticValue}");
                                return staticValue;
                            }

                            return 2f;  // 보수적 기본값
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAbilityAPRecovery error: {ex.Message}");
            }

            return 0f;
        }

        /// <summary>
        /// ★ v3.0.98: 능력이 리소스(AP/MP)를 회복하는 능력인지 확인
        /// </summary>
        public static bool IsResourceRecoveryAbility(AbilityData ability)
        {
            return GetAbilityMPRecovery(ability) > 0 || GetAbilityAPRecovery(ability) > 0;
        }

        #endregion

        #region SpringAttack (v3.5.22)

        /// <summary>
        /// ★ v3.5.22: 유닛이 SpringAttack(Acrobatic Artistry)을 사용할 수 있는 조건인지 확인
        /// - 갭클로저 2회 이상 사용 → 사용 (역순 공격 2번 가치)
        /// - 단, 노렸던 적이 전부 죽었으면 사용 X (역순 공격해도 의미 없음)
        /// </summary>
        public static bool CanUseSpringAttackAbility(BaseUnitEntity unit)
        {
            if (unit == null) return false;

            try
            {
                var springAttackPart = unit.GetOptional<UnitPartSpringAttack>();
                if (springAttackPart == null) return false;

                int entryCount = springAttackPart.Entries?.Count ?? 0;

                // ★ 갭클로저 2회 미만 → 사용 안 함
                if (entryCount < 2)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] {unit.CharacterName}: SpringAttack skip (entries={entryCount}, need 2+)");
                    return false;
                }

                // ★ 살아있는 적이 있는지 확인 (전부 죽었으면 역순 공격 의미 없음)
                var enemies = GetEnemies(unit);
                int livingEnemies = enemies.Count;

                if (livingEnemies == 0)
                {
                    Main.Log($"[CombatAPI] {unit.CharacterName}: SpringAttack skip - all enemies dead");
                    return false;
                }

                Main.Log($"[CombatAPI] {unit.CharacterName}: SpringAttack {entryCount} entries + {livingEnemies} living enemies - use!");
                return true;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CanUseSpringAttackAbility error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.5.22: 유닛이 UnitPartSpringAttack를 가지고 있는지 확인
        /// </summary>
        public static bool HasSpringAttackPart(BaseUnitEntity unit)
        {
            if (unit == null) return false;

            try
            {
                return unit.GetOptional<UnitPartSpringAttack>() != null;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] HasSpringAttackPart failed for {unit?.CharacterName}: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Strategist Zones (v3.5.23)

        /// <summary>
        /// ★ v3.5.23: 기존 전략가 구역들의 위치 가져오기
        /// </summary>
        public static List<(Vector3 position, string type)> GetExistingStrategistZones()
        {
            var zones = new List<(Vector3, string)>();

            try
            {
                foreach (var areaEffect in Game.Instance.State.AreaEffects)
                {
                    if (areaEffect?.Blueprint?.IsStrategistAbility != true) continue;

                    var zoneType = areaEffect.Blueprint.TacticsAreaEffectType.ToString();
                    zones.Add((areaEffect.Position, zoneType));
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetExistingStrategistZones error: {ex.Message}");
            }

            return zones;
        }

        /// <summary>
        /// ★ v3.5.23: 전략가 구역 능력인지 확인
        /// </summary>
        public static bool IsStrategistZoneAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                return ability.Blueprint?.GetComponent<Kingmaker.UnitLogic.ActivatableAbilities.Restrictions.AbilityRestrictionStrategist>() != null;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsStrategistZoneAbility failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.5.23: 기존 전략가 구역과 너무 가까운지 확인 (거리 기반)
        /// 정확한 패턴 겹침 체크는 게임이 하므로, 여기서는 거리 기반으로 대략적 체크
        /// </summary>
        public static bool IsPositionTooCloseToExistingZones(Vector3 position, float minDistance = 6f)
        {
            try
            {
                var existingZones = GetExistingStrategistZones();
                foreach (var zone in existingZones)
                {
                    if (Vector3.Distance(position, zone.position) < minDistance)
                    {
                        return true;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsPositionTooCloseToExistingZones error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.5.23: 전략가 구역의 겹치지 않는 위치 찾기 (거리 기반)
        /// </summary>
        public static Vector3? FindNonOverlappingZonePosition(AbilityData ability, Vector3 preferredPosition, float searchRadius = 10f)
        {
            if (ability == null) return null;

            try
            {
                // 전략가 구역이 아니면 그냥 선호 위치 반환
                if (!IsStrategistZoneAbility(ability)) return preferredPosition;

                // 기존 구역과 거리 체크
                if (!IsPositionTooCloseToExistingZones(preferredPosition, 6f))
                {
                    return preferredPosition;
                }

                // 겹치면 주변에서 겹치지 않는 위치 탐색
                var existingZones = GetExistingStrategistZones();
                if (existingZones.Count == 0) return preferredPosition;

                // 기존 구역들의 평균 중심에서 멀어지는 방향으로 오프셋
                Vector3 avgCenter = Vector3.zero;
                foreach (var zone in existingZones)
                {
                    avgCenter += zone.position;
                }
                avgCenter /= existingZones.Count;

                Vector3 offsetDir = (preferredPosition - avgCenter).normalized;
                if (offsetDir.magnitude < 0.1f)
                {
                    offsetDir = Vector3.forward;
                }

                // 점진적으로 거리를 늘려가며 겹치지 않는 위치 찾기
                for (float offset = 3f; offset <= searchRadius; offset += 1.5f)
                {
                    Vector3 testPos = preferredPosition + offsetDir * offset;
                    if (!IsPositionTooCloseToExistingZones(testPos, 6f))
                    {
                        Main.Log($"[CombatAPI] Found non-overlapping position at offset {offset:F1}m");
                        return testPos;
                    }
                }

                // 찾지 못하면 null
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Could not find non-overlapping position for {ability.Name}");
                return null;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] FindNonOverlappingZonePosition error: {ex.Message}");
                return null;
            }
        }

        #endregion
    }
}
