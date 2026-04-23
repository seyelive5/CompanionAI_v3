using System;
using System.Collections.Generic;
using Kingmaker;                                                  // Game.Instance
using Kingmaker.AI;                                               // AiBrainHelper.IsThreatningArea
using Kingmaker.Controllers;                                      // AreaEffectsController.CheckInertWarpEffect
using Kingmaker.ElementsSystem;                                   // ActionList
using Kingmaker.EntitySystem.Entities;                            // BaseUnitEntity
using Kingmaker.Pathfinding;                                      // CustomGridNodeBase, GetNearestNodeXZ
using Kingmaker.UnitLogic.Abilities;                              // AbilityData, AbilityRange
using Kingmaker.UnitLogic.Abilities.Blueprints;                   // BlueprintAbility extensions
using Kingmaker.UnitLogic.Abilities.Components.AreaEffects;       // AreaEffectEntity, AbilityAreaEffectRunAction, AbilityAreaEffectBuff
using Kingmaker.UnitLogic.Mechanics.Actions;                      // ContextActionDealDamage
using Kingmaker.UnitLogic.Mechanics.Components;                   // AddFactContextActions
using UnityEngine;                                                // Vector3
using CompanionAI_v3.Data;                                        // AbilityDatabase, AbilityClassificationData

namespace CompanionAI_v3.GameInterface
{
    public static partial class CombatAPI
    {
        #region Ability Type Detection

        // ★ v3.8.61: IsMomentumGeneratingAbility 제거 — 호출부 없는 데드코드 (string 매칭만)

        /// <summary>
        /// 능력이 방어 자세인지 확인
        /// ★ v3.8.61: String 매칭 제거 → AbilityDatabase 위임 (GUID + Flag 기반)
        /// </summary>
        public static bool IsDefensiveStanceAbility(AbilityData ability)
        {
            return AbilityDatabase.IsDefensiveStance(ability);
        }

        /// <summary>
        /// 능력이 Heroic Act인지 확인
        /// </summary>
        public static bool IsHeroicActAbility(AbilityData ability)
        {
            return AbilityDatabase.IsHeroicAct(ability);
        }

        /// <summary>
        /// 능력이 Righteous Fury (Revel in Slaughter)인지 확인
        /// ★ v3.8.61: 플레이스홀더 GUID + String 매칭 제거 → AbilityDatabase 위임
        /// </summary>
        public static bool IsRighteousFuryAbility(AbilityData ability)
        {
            return AbilityDatabase.IsRighteousFury(ability);
        }

        /// <summary>
        /// ★ v3.0.58: 능력의 정확한 사거리 반환 (게임 API 사용)
        /// ★ v3.8.06: 모든 AOE 능력 사거리 정확히 계산
        /// - Pattern AOE (Cone, Circle, Ray 등): PatternSettings.Pattern.Radius
        /// - Circle AOE (AbilityTargetsAround): AoERadius
        /// - LidlessStare 등 Range="Unlimited"이지만 실제로는 Pattern.Radius가 범위
        /// </summary>
        public static float GetAbilityRange(AbilityData ability)
        {
            if (ability == null) return 0f;

            try
            {
                var bp = ability.Blueprint;
                if (bp == null) return 0f;

                // ★ v3.8.06: 모든 Pattern/AOE 능력 처리 (게임 API 통합 사용)
                // PatternSettings는 WarhammerAbilityAttackDelivery + AbilityTargetsInPattern 모두 포함
                var patternSettings = bp.PatternSettings;
                if (patternSettings != null)
                {
                    int patternRadius = patternSettings.Pattern?.Radius ?? 0;
                    if (patternRadius > 0)
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAbilityRange: {bp.name} is Pattern AOE, Radius={patternRadius}");
                        return patternRadius;
                    }
                }

                // ★ v3.8.06: Circle AOE 처리 (IAbilityAoERadiusProvider)
                int aoERadius = bp.AoERadius;
                if (aoERadius > 0)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAbilityRange: {bp.name} is Circle AOE, Radius={aoERadius}");
                    return aoERadius;
                }

                // 1. Blueprint.GetRange() 사용 (게임 공식 API)
                int baseRange = bp.GetRange();

                if (baseRange >= 0)
                {
                    // Personal(0), Touch(1), Unlimited(100000), Custom(CustomRange)
                    if (baseRange == 0) return 0f;  // Personal - 자신만
                    if (baseRange >= 100000) return 100f;  // Unlimited
                    return baseRange;  // 게임 단위 = 미터
                }

                // 2. Weapon 타입 (-1) - 무기 사거리 사용
                if (ability.Weapon != null)
                {
                    return ability.Weapon.AttackRange;
                }

                // 3. ★ v3.8.63: 무기 타입인데 무기 없음 — 근접 사거리 폴백
                // 기존 15f(원거리)는 비무장 능력에 대해 잘못된 값이었음
                // bp.GetRange() == -1 + Weapon == null = 비무장/근접 능력
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAbilityRange: {bp.name} has weapon-type range but no weapon — fallback to melee range");
                return GridCellSize * 2;  // 약 2.7m (근접 2셀)
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAbilityRange error: {ex.Message}");
                return GridCellSize * 2;  // ★ v3.8.63: 에러 시에도 근접 폴백 (15f보다 안전)
            }
        }

        /// <summary>
        /// 능력이 무제한 사거리인지 확인
        /// ★ v3.8.06: 모든 AOE 능력 처리 (Pattern/Circle AOE는 실제로 제한 범위)
        /// </summary>
        public static bool IsUnlimitedRange(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                var bp = ability.Blueprint;
                if (bp == null) return false;

                // ★ v3.8.06: Pattern AOE는 무제한이 아님 (실제 범위는 Pattern.Radius)
                if (bp.PatternSettings != null)
                {
                    int patternRadius = bp.PatternSettings.Pattern?.Radius ?? 0;
                    if (patternRadius > 0) return false;
                }

                // ★ v3.8.06: Circle AOE도 무제한이 아님
                if (bp.AoERadius > 0) return false;

                return bp.Range == AbilityRange.Unlimited;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsUnlimitedRange failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.7.81: 특정 위치에서 타겟에게 능력 사용 가능한지 확인
        /// 이동 후 공격 검증에 사용
        /// ★ v3.9.04: 게임 API (CanTargetFromNode) 기반으로 전환
        /// 기존 LosCalculations.HasLos()는 게임의 CanUseAbilityOn()과 결과 불일치 발생
        /// → Analyzer(hittable)와 Validator(reachable) 판정이 달라 공격 누락
        /// </summary>
        public static bool CanReachTargetFromPosition(AbilityData ability, Vector3 fromPosition, BaseUnitEntity target)
        {
            if (ability == null || target == null) return false;

            try
            {
                var fromNode = fromPosition.GetNearestNodeXZ() as Kingmaker.Pathfinding.CustomGridNodeBase;
                if (fromNode == null)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CanReachFromPos: Node lookup failed, allowing");
                    return true;
                }

                // ★ v3.9.04: 게임 API 위임 — Analyzer와 동일한 검증 기준 사용
                bool canTarget = CanTargetFromPosition(ability, fromNode, target, out string reason);

                if (Main.IsDebugEnabled)
                {
                    if (!canTarget)
                        Main.LogDebug($"[CombatAPI] CanReachFromPos: {ability.Name} -> {target.CharacterName}, BLOCKED: {reason}");
                    else
                        Main.LogDebug($"[CombatAPI] CanReachFromPos: {ability.Name} -> {target.CharacterName}, OK");
                }

                return canTarget;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CanReachTargetFromPosition error: {ex.Message}");
                return true;  // 에러 시 허용 (안전하게)
            }
        }

        #endregion

        #region Ability Type Detection API (v3.5.73)

        /// <summary>
        /// ★ v3.5.73: 능력의 공격 카테고리 정보
        /// 게임 네이티브 API만 사용 - 문자열 휴리스틱 금지
        /// </summary>
        public class AbilityTypeInfo
        {
            // 공격 방식 - 게임 네이티브 API 기반
            public bool IsBurst { get; set; }        // 점사/연사
            public bool IsScatter { get; set; }      // 산탄
            public bool IsSingleShot { get; set; }   // 단발
            public bool IsAoE { get; set; }          // 범위 공격
            public bool IsCharge { get; set; }       // 돌격
            public bool IsMelee { get; set; }        // 근접
            public bool IsRanged { get; set; }       // 원거리

            // 패턴 정보 (v3.5.39 API 재사용)
            public bool IsPattern { get; set; }
            public Kingmaker.Blueprints.PatternType? PatternType { get; set; }
            public float PatternRadius { get; set; }
            public float PatternAngle { get; set; }

            // 무기 연관
            public bool IsWeaponAbility { get; set; }

            // 계산된 분류
            public Data.AttackCategory Category => CalculateCategory();

            private Data.AttackCategory CalculateCategory()
            {
                if (IsCharge) return Data.AttackCategory.GapCloser;
                if (IsAoE || IsPattern) return Data.AttackCategory.AoE;
                if (IsScatter) return Data.AttackCategory.Scatter;
                if (IsBurst) return Data.AttackCategory.Burst;
                if (IsSingleShot) return Data.AttackCategory.SingleTarget;
                return Data.AttackCategory.Normal;
            }
        }

        // AbilityTypeInfo 캐시 (GUID별)
        private static Dictionary<string, AbilityTypeInfo> AbilityTypeCache = new Dictionary<string, AbilityTypeInfo>();

        /// <summary>
        /// ★ v3.5.73: 능력 타입 정보 조회 (게임 API 기반)
        /// 문자열 휴리스틱 없이 게임 네이티브 속성만 사용
        /// </summary>
        public static AbilityTypeInfo GetAbilityTypeInfo(AbilityData ability)
        {
            if (ability == null) return new AbilityTypeInfo();

            try
            {
                // 캐시 확인
                var guid = ability.Blueprint?.AssetGuid?.ToString();
                if (!string.IsNullOrEmpty(guid) && AbilityTypeCache.TryGetValue(guid, out var cached))
                    return cached;

                var info = new AbilityTypeInfo
                {
                    // 공격 방식 - 게임 네이티브 API 직접 호출
                    IsBurst = ability.IsBurstAttack,
                    IsScatter = ability.IsScatter,
                    IsSingleShot = ability.IsSingleShot,
                    IsAoE = ability.IsAOE,
                    IsCharge = ability.IsCharge,
                    IsMelee = ability.IsMelee,
                    IsRanged = !ability.IsMelee,
                    IsWeaponAbility = ability.Weapon != null,
                };

                // 패턴 정보 (v3.5.39 API 재사용)
                var patternInfo = GetPatternInfo(ability);
                if (patternInfo != null && patternInfo.IsValid)
                {
                    info.IsPattern = true;
                    info.PatternType = patternInfo.Type;
                    info.PatternRadius = patternInfo.Radius;
                    info.PatternAngle = patternInfo.Angle;
                }

                // 캐시 저장
                if (!string.IsNullOrEmpty(guid))
                    AbilityTypeCache[guid] = info;

                return info;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAbilityTypeInfo error: {ex.Message}");
                return new AbilityTypeInfo();
            }
        }

        /// <summary>
        /// ★ v3.5.73: Burst 공격 여부 (게임 API 직접 호출)
        /// </summary>
        public static bool IsBurstAttack(AbilityData ability)
            => ability?.IsBurstAttack ?? false;

        /// <summary>
        /// ★ v3.5.73: Scatter 공격 여부 (게임 API 직접 호출)
        /// </summary>
        public static bool IsScatterAttack(AbilityData ability)
            => ability?.IsScatter ?? false;

        /// <summary>
        /// ★ v3.5.73: Charge (돌격) 능력 여부 (게임 API 직접 호출)
        /// </summary>
        public static bool IsChargeAbility(AbilityData ability)
            => ability?.IsCharge ?? false;

        /// <summary>
        /// ★ v3.5.73: 공격 카테고리 조회 (게임 API 기반 자동 분류)
        /// </summary>
        public static Data.AttackCategory GetAttackCategory(AbilityData ability)
            => GetAbilityTypeInfo(ability).Category;

        /// <summary>
        /// ★ v3.5.73: 능력 타입 캐시 클리어 (전투 종료 시 호출)
        /// </summary>
        public static void ClearAbilityTypeCache()
        {
            AbilityTypeCache.Clear();
            Main.LogDebug("[CombatAPI] AbilityType cache cleared");
        }

        #endregion

        #region Ability Classification Data (v3.7.73)

        // ★ v3.7.73: AbilityClassificationData 캐시 (GUID별)
        private static Dictionary<string, AbilityClassificationData> ClassificationCache = new Dictionary<string, AbilityClassificationData>();

        /// <summary>
        /// ★ v3.7.73: 능력의 모든 분류 속성을 추출 (게임 API 기반)
        /// 캐싱되어 동일 GUID는 한 번만 계산
        /// </summary>
        public static AbilityClassificationData GetClassificationData(AbilityData ability)
        {
            if (ability == null) return new AbilityClassificationData();

            try
            {
                // 캐시 확인
                var guid = ability.Blueprint?.AssetGuid?.ToString();
                if (!string.IsNullOrEmpty(guid) && ClassificationCache.TryGetValue(guid, out var cached))
                    return cached;

                var bp = ability.Blueprint;
                if (bp == null) return new AbilityClassificationData();

                var data = new AbilityClassificationData
                {
                    // ═══════════════════════════════════════════════════════════════
                    // 기본 타입 (Blueprint에서 추출)
                    // ═══════════════════════════════════════════════════════════════
                    Tag = bp.AbilityTag,
                    ParamsSource = bp.AbilityParamsSource,
                    SpellDescriptor = bp.SpellDescriptor,

                    // ═══════════════════════════════════════════════════════════════
                    // 공격 특성 (AbilityData 런타임 + Blueprint 혼합)
                    // ═══════════════════════════════════════════════════════════════
                    AttackType = bp.AttackType,
                    IsMelee = ability.IsMelee,
                    IsRanged = !ability.IsMelee,
                    IsScatter = ability.IsScatter,
                    IsBurst = ability.IsBurstAttack,
                    IsSingleShot = ability.IsSingleShot,
                    IsAoE = ability.IsAOE,
                    IsCharge = ability.IsCharge,
                    IsMoveUnit = bp.IsMoveUnit,
                    BurstCount = ability.BurstAttacksCount,
                    AoERadius = bp.AoERadius,

                    // ═══════════════════════════════════════════════════════════════
                    // 타겟팅 (Blueprint에서 추출)
                    // ═══════════════════════════════════════════════════════════════
                    Range = bp.Range,
                    RangeCells = (int)GetAbilityRange(ability),
                    MinRangeCells = bp.MinRange,
                    CustomRange = bp.CustomRange,
                    CanTargetEnemies = bp.CanTargetEnemies,
                    CanTargetFriends = bp.CanTargetFriends,
                    CanTargetSelf = bp.CanTargetSelf,
                    CanTargetPoint = bp.CanTargetPoint,
                    CanTargetDead = bp.CanCastToDeadTarget,
                    AoETargets = bp.AoETargets,
                    NeedLoS = true, // 대부분의 능력은 LOS 필요

                    // ═══════════════════════════════════════════════════════════════
                    // 효과 (Blueprint에서 추출)
                    // ═══════════════════════════════════════════════════════════════
                    EffectOnAlly = bp.EffectOnAlly,
                    EffectOnEnemy = bp.EffectOnEnemy,
                    NotOffensive = bp.NotOffensive,
                    IsWeaponAbility = bp.IsWeaponAbility,
                    IsPsykerAbility = bp.IsPsykerAbility,

                    // ═══════════════════════════════════════════════════════════════
                    // 특수 (Blueprint에서 추출)
                    // ═══════════════════════════════════════════════════════════════
                    IsHeroicAct = bp.IsHeroicAct,
                    IsDesperateMeasure = bp.IsDesperateMeasure,
                    IsMomentum = bp.IsMomentum,

                    // ═══════════════════════════════════════════════════════════════
                    // 비용 (Blueprint + Runtime 혼합)
                    // ═══════════════════════════════════════════════════════════════
                    APCost = bp.ActionPointCost,
                    IsFreeAction = ability.IsFreeAction,

                    // ═══════════════════════════════════════════════════════════════
                    // ★ v3.7.89: AOO 관련 (Blueprint에서 추출)
                    // ═══════════════════════════════════════════════════════════════
                    UsingInThreateningArea = (int)bp.UsingInThreateningArea,
                };

                // 패턴 타입 추출
                var patternInfo = GetPatternInfo(ability);
                if (patternInfo != null && patternInfo.IsValid)
                {
                    data.PatternType = patternInfo.Type.ToString();
                }

                // 캐시 저장
                if (!string.IsNullOrEmpty(guid))
                    ClassificationCache[guid] = data;

                return data;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetClassificationData error: {ex.Message}");
                return new AbilityClassificationData();
            }
        }

        /// <summary>
        /// ★ v3.7.73: 분류 데이터 캐시 클리어 (전투 종료 시 호출)
        /// </summary>
        public static void ClearClassificationCache()
        {
            ClassificationCache.Clear();
            Main.LogDebug("[CombatAPI] Classification cache cleared");
        }

        /// <summary>
        /// ★ v3.7.73: 모든 능력 관련 캐시 클리어
        /// </summary>
        public static void ClearAllAbilityCaches()
        {
            ClearAbilityTypeCache();
            ClearClassificationCache();
            Main.LogDebug("[CombatAPI] All ability caches cleared");
        }

        #endregion

        #region Damaging AoE Detection (v3.9.70)

        // ★ v3.9.70: 블루프린트 기반 피해 AoE 판별 캐시 (정적 데이터이므로 전투 내 재사용)
        private static readonly Dictionary<string, bool> _damagingAoECache = new Dictionary<string, bool>();

        /// <summary>
        /// ★ v3.9.70: 유닛이 현재 피해를 주는 AoE 구역 안에 있는지 확인
        /// 1차: AiBrainHelper.IsThreatningArea() (적 AoE에 대해 정확)
        /// 2차 폴백: 블루프린트 컴포넌트 직접 검사 (환경 AoE — caster null로 IsSuitableTargetType 실패 우회)
        /// </summary>
        public static bool IsUnitInDamagingAoE(BaseUnitEntity unit)
        {
            if (unit == null) return false;

            try
            {
                foreach (var areaEffect in Game.Instance.State.AreaEffects)
                {
                    if (areaEffect == null) continue;

                    // 1차: 게임 API — 적 AoE에 대해 팩션 체크 포함
                    if (AiBrainHelper.IsThreatningArea(areaEffect, unit))
                    {
                        if (areaEffect.Contains(unit))
                            return true;
                        continue;
                    }

                    // 2차 폴백: 환경/중립 AoE — IsSuitableTargetType이 caster null로 실패하는 경우
                    // 아군이 시전한 AoE는 건너뛰기 (아군 AoE에서 도망칠 필요 없음)
                    var caster = areaEffect.Context?.MaybeCaster;
                    if (caster != null && !caster.IsEnemy(unit))
                        continue;

                    // 블루프린트에 피해 컴포넌트가 있고, 유닛이 안에 있는가?
                    if (HasDamagingComponents(areaEffect) && areaEffect.Contains(unit))
                    {
                        if (Main.IsDebugEnabled)
                            Main.LogDebug($"[CombatAPI] ★ Damaging AoE detected via fallback: {areaEffect.Blueprint?.name ?? "unknown"} (caster={(caster != null ? "enemy" : "null/environmental")})");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsUnitInDamagingAoE error: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// ★ v3.9.70: 특정 위치가 피해를 주는 AoE 구역 안에 있는지 확인
        /// 이동 후보 타일 평가에 사용 — unit은 팩션 체크용
        /// </summary>
        public static bool IsPositionInDamagingAoE(Vector3 position, BaseUnitEntity unit)
        {
            if (unit == null) return false;

            try
            {
                foreach (var areaEffect in Game.Instance.State.AreaEffects)
                {
                    if (areaEffect == null) continue;

                    // 1차: 게임 API
                    if (AiBrainHelper.IsThreatningArea(areaEffect, unit))
                    {
                        if (areaEffect.Contains(position))
                            return true;
                        continue;
                    }

                    // 2차 폴백: 아군 시전 AoE 건너뛰기
                    var caster = areaEffect.Context?.MaybeCaster;
                    if (caster != null && !caster.IsEnemy(unit))
                        continue;

                    if (HasDamagingComponents(areaEffect) && areaEffect.Contains(position))
                        return true;
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsPositionInDamagingAoE error: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// AoE 블루프린트에 피해를 주는 컴포넌트가 있는지 직접 확인
        /// 게임의 CheckDealDamage + CheckApplyBuffWithDamage 로직 재현
        /// IsSuitableTargetType 팩션 체크를 우회하여 환경 AoE도 감지
        /// </summary>
        private static bool HasDamagingComponents(AreaEffectEntity areaEffect)
        {
            var blueprint = areaEffect?.Blueprint;
            if (blueprint == null) return false;

            // 캐시 확인 (블루프린트 컴포넌트는 정적 데이터)
            string bpId = blueprint.AssetGuid?.ToString();
            if (bpId != null && _damagingAoECache.TryGetValue(bpId, out bool cached))
                return cached;

            bool isDamaging = false;

            foreach (var component in blueprint.ComponentsArray)
            {
                if (component == null) continue;

                // Check 1: AbilityAreaEffectRunAction — UnitEnter/UnitMove/Round에 ContextActionDealDamage가 있는지
                if (component is AbilityAreaEffectRunAction runAction)
                {
                    if (ContainsDamageAction(runAction.UnitEnter) ||
                        ContainsDamageAction(runAction.UnitExit) ||
                        ContainsDamageAction(runAction.UnitMove) ||
                        ContainsDamageAction(runAction.Round))
                    {
                        isDamaging = true;
                        break;
                    }
                }

                // Check 2: AbilityAreaEffectBuff — 버프에 AddFactContextActions.Activated/NewRound에 피해가 있는지
                if (component is AbilityAreaEffectBuff buffComponent)
                {
                    var buff = buffComponent.Buff;
                    if (buff != null)
                    {
                        foreach (var buffComp in buff.ComponentsArray)
                        {
                            if (buffComp is AddFactContextActions contextActions)
                            {
                                if (ContainsDamageAction(contextActions.Activated) ||
                                    ContainsDamageAction(contextActions.NewRound) ||
                                    ContainsDamageAction(contextActions.RoundEnd))
                                {
                                    isDamaging = true;
                                    break;
                                }
                            }
                        }
                        if (isDamaging) break;
                    }
                }
            }

            // 캐시 저장
            if (bpId != null)
                _damagingAoECache[bpId] = isDamaging;

            return isDamaging;
        }

        /// <summary>
        /// ActionList 내에 ContextActionDealDamage가 포함되어 있는지 확인
        /// </summary>
        private static bool ContainsDamageAction(ActionList actionList)
        {
            if (actionList?.Actions == null) return false;
            foreach (var action in actionList.Actions)
            {
                if (action is ContextActionDealDamage)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 전투 시작 시 AoE 캐시 초기화 (CombatCache.ClearAll에서 호출)
        /// </summary>
        public static void ClearDamagingAoECache()
        {
            _damagingAoECache.Clear();
            _lastHazardCheckUnit = null;
        }

        // ── ★ v3.19.8: Unified Hazard Zone Detection ──
        // DamagingAoE + PsychicNullZone(사이커 전용)을 단일 메서드로 통합
        // 모든 이동 계획에서 일관된 위험 회피 보장

        private static BaseUnitEntity _lastHazardCheckUnit;
        private static bool _lastHazardCheckIsPsychic;

        /// <summary>
        /// ★ v3.19.8: 특정 위치가 위험 구역(DamagingAoE + PsychicNullZone) 안인지 통합 판별
        /// 모든 이동 후보 타일 평가에서 IsPositionInDamagingAoE 대신 사용
        /// 사이커 여부는 유닛별 캐시 — 타일 루프에서 반복 호출 시 O(1)
        /// </summary>
        public static bool IsPositionInHazardZone(Vector3 position, BaseUnitEntity unit)
        {
            if (IsPositionInDamagingAoE(position, unit)) return true;

            // 사이커 여부 캐시 (같은 유닛이면 재계산 안 함)
            if (unit != _lastHazardCheckUnit)
            {
                _lastHazardCheckUnit = unit;
                _lastHazardCheckIsPsychic = HasPsychicAbilities(unit);
            }
            if (_lastHazardCheckIsPsychic && IsPositionInPsychicNullZone(position)) return true;

            return false;
        }

        /// <summary>
        /// ★ v3.19.8: 유닛이 현재 위험 구역 안에 있는지 통합 판별
        /// </summary>
        public static bool IsUnitInHazardZone(BaseUnitEntity unit)
        {
            if (IsUnitInDamagingAoE(unit)) return true;
            if (HasPsychicAbilities(unit) && IsUnitInPsychicNullZone(unit)) return true;
            return false;
        }

        /// <summary>
        /// ★ v3.9.70: 유닛이 사이킥 사용 불가 구역(Inert Warp Effect)에 있는지 확인
        /// 워프 데미지 존은 AreaEffectRestrictions.CannotUsePsychicPowers 플래그를 가짐
        /// </summary>
        public static bool IsUnitInPsychicNullZone(BaseUnitEntity unit)
        {
            if (unit == null) return false;
            try
            {
                var node = (CustomGridNodeBase)(Pathfinding.GraphNode)unit.CurrentNode;
                if (node == null) return false;
                return AreaEffectsController.CheckInertWarpEffect(node);
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsUnitInPsychicNullZone error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.9.70: 특정 위치가 사이킥 사용 불가 구역에 있는지 확인
        /// 이동 후보 타일 평가에 사용
        /// </summary>
        public static bool IsPositionInPsychicNullZone(Vector3 position)
        {
            try
            {
                foreach (var areaEffect in Game.Instance.State.AreaEffects)
                {
                    if (areaEffect == null) continue;
                    if (areaEffect.Blueprint.HasInertWarpEffect && areaEffect.Contains(position))
                        return true;
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsPositionInPsychicNullZone error: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// ★ v3.9.70: 유닛이 사이킥 능력을 보유하고 있는지 확인
        /// </summary>
        public static bool HasPsychicAbilities(BaseUnitEntity unit)
        {
            if (unit == null) return false;
            try
            {
                var abilities = unit.Abilities;
                if (abilities == null) return false;
                foreach (var ability in abilities.RawFacts)
                {
                    if (ability?.Blueprint?.IsPsykerAbility == true)
                        return true;
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] HasPsychicAbilities failed for {unit?.CharacterName}: {ex.Message}");
            }
            return false;
        }

        #endregion
    }
}
