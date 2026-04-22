using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem;  // ★ v3.8.66: EntityHelper.DistanceToInCells 확장 메서드
using Kingmaker.AI;  // ★ v3.9.70: AiBrainHelper.IsThreatningArea
using Kingmaker.UnitLogic.Abilities.Components.AreaEffects;  // ★ v3.9.70: AbilityAreaEffectRunAction, AbilityAreaEffectBuff
using Kingmaker.ElementsSystem;  // ★ v3.9.70: ActionList, GameAction
using Kingmaker.UnitLogic.Mechanics.Components;  // ★ v3.9.70: AddFactContextActions
using Kingmaker.Controllers;  // ★ v3.9.70: AreaEffectsController.CheckInertWarpEffect
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Items;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities;
using Pathfinding;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Abilities.Components.Base;
using Kingmaker.UnitLogic.Abilities.Components.CasterCheckers;
using Kingmaker.UnitLogic.Abilities.Components.Patterns;
using Kingmaker.UnitLogic.Mechanics.Actions;
using Kingmaker.UnitLogic.Parts;
using Kingmaker.UnitLogic.Levelup.Obsolete.Blueprints.Spells;
using Kingmaker.Utility;
using Kingmaker.GameCommands;  // ★ v3.9.72: SwitchHandEquipment 확장 메서드
using Kingmaker.View.Covers;
using Kingmaker.RuleSystem;
using Kingmaker.RuleSystem.Rules;
using UnityEngine;
using CompanionAI_v3.Data;
using CompanionAI_v3.Settings;
using Kingmaker.UnitLogic;  // ★ v3.7.89: AOO API
using Kingmaker.UnitLogic.Buffs.Components;  // ★ v3.8.36: WarhammerAbilityRestriction
using Kingmaker.Blueprints.Classes.Experience;  // ★ v3.8.49: UnitDifficultyType
using Kingmaker.Designers.Mechanics.Facts;        // ★ v3.9.88: WeaponSetChangedTrigger
using Kingmaker.Designers.Mechanics.Facts.Damage; // ★ v3.40.6: WarhammerDamageModifier (면역 감지)
using Kingmaker.UnitLogic.FactLogic;        // ★ v3.40.2: ForceMoveTriggerInitiator (Push 감지)
using Kingmaker.UnitLogic.Mechanics;        // ★ v3.40.6: ContextValueType (면역 컴포넌트 평가)
using Kingmaker.UnitLogic.Mechanics.Damage; // ★ v3.40.6: DamageTypeMask (데미지 면역 감지)
using Kingmaker.Mechanics.Damage;           // ★ v3.40.6: DamageExtension.Contains
using Kingmaker.EntitySystem.Stats.Base;    // ★ v3.26.0: StatType (적/아군 스탯 조회)
using Kingmaker.EntitySystem.Stats;          // ★ v3.26.0: ModifiableValue
using Kingmaker.Enums;                       // ★ v3.28.0: Size (플랭킹 공격 방향)
using Kingmaker.Blueprints.Root;             // ★ v3.28.0: ProgressionRoot (아키타입 감지)
using Kingmaker.UnitLogic.Progression.Paths; // ★ v3.28.0: BlueprintCareerPath
using Kingmaker.Controllers.TurnBased;        // ★ v3.111.12: Initiative.InterruptingOrder
using Kingmaker.UnitLogic.Squads;              // ★ v3.111.12: PartSquadExtension.GetSquadOptional

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// 게임 API 래퍼 - 모든 게임 상호작용을 중앙화
    /// </summary>
    public static class CombatAPI
    {
        // ★ v3.8.80: GetAvailableAbilities 프레임 캐시
        // 같은 프레임 내 동일 유닛에 대한 반복 호출 방지 (Analyze + Plan = 4+회/프레임)
        private static string _cachedAbilitiesUnitId;
        private static int _cachedAbilitiesFrame;
        private static List<AbilityData> _cachedAbilitiesList;

        // ★ v3.9.10: Pattern counting zero-alloc 풀 (new HashSet<> 제거)
        private static readonly HashSet<BaseUnitEntity> _sharedUnitSet = new HashSet<BaseUnitEntity>();
        private static readonly HashSet<BaseUnitEntity> _sharedAllySet = new HashSet<BaseUnitEntity>();

        #region Ability Checks

        /// <summary>
        /// 능력을 타겟에게 사용 가능한지 확인
        /// </summary>
        public static bool CanUseAbilityOn(AbilityData ability, TargetWrapper target, out string reason)
        {
            reason = null;

            if (ability == null || target == null)
            {
                reason = "Null ability or target";
                return false;
            }

            try
            {
                // ★ v3.8.36: IsRestricted 체크 복원 (버프 제한 존중)
                // 잠재력 초월(SoulMarkHope4) 같은 버프는 의도적으로 능력을 제한함
                // 이 제한을 무시하면 AI가 사용 불가능한 능력을 선택하게 됨
                if (ability.IsRestricted)
                {
                    reason = GetRestrictionReason(ability);
                    // 디버그 로깅 - 어떤 능력이 왜 제한되는지 파악
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CanUseAbilityOn: {ability.Name} IsRestricted=true - {reason}");
                    return false;
                }

                // 기본 타겟 검증
                AbilityData.UnavailabilityReasonType? unavailableReason;
                bool canTarget = ability.CanTarget(target, out unavailableReason);

                if (!canTarget && unavailableReason.HasValue)
                {
                    reason = unavailableReason.Value.ToString();
                    return false;
                }

                // 위치 기반 검증 (LOS, 사거리)
                var caster = ability.Caster as BaseUnitEntity;
                var targetEntity = target.Entity as BaseUnitEntity;

                if (caster != null && targetEntity != null)
                {
                    var casterNode = caster.CurrentUnwalkableNode;
                    var targetNode = targetEntity.CurrentUnwalkableNode;

                    if (casterNode != null && targetNode != null)
                    {
                        int distance;
                        LosCalculations.CoverType coverType;

                        bool canTargetFromNode = ability.CanTargetFromNode(
                            casterNode, targetNode, target, out distance, out coverType);

                        if (!canTargetFromNode)
                        {
                            bool hasLos = coverType != LosCalculations.CoverType.Invisible;
                            reason = hasLos ? "OutOfRange" : "NoLineOfSight";
                            return false;
                        }
                    }
                }

                return canTarget;
            }
            catch (Exception ex)
            {
                reason = $"Exception: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// ★ v3.8.25: AbilityCasterHasFacts 컴포넌트 검증
        /// ★ v3.8.33: 게임 API의 IsRestricted/IsAvailable 직접 사용
        /// 게임이 모든 제한 조건을 체크하도록 위임 (복잡한 로직 복제 대신)
        /// </summary>
        public static bool MeetsCasterFactRequirements(AbilityData ability, out string reason)
        {
            reason = null;
            if (ability == null) return true;

            try
            {
                // ★ v3.8.33: 게임 API 직접 사용 - 모든 제한 조건 체크
                // IsRestricted 체크 항목:
                // - CombatStateRestriction (InCombatOnly/NotInCombatOnly)
                // - InterruptionAbilityRestrictions
                // - IAbilityCasterRestriction 컴포넌트들 (HasFacts, HasNoFacts, InCombat 등)
                // - WeaponReloadLogic
                // - UsingInThreateningArea
                // - ConcussionEffect
                if (ability.IsRestricted)
                {
                    // 자세한 이유 파악 시도
                    reason = GetRestrictionReason(ability);
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsRestricted=true for {ability.Name}: {reason}");
                    return false;
                }

                // IsAvailable 추가 체크 (AP, 쿨다운, 탄약 등)
                if (!ability.IsAvailable)
                {
                    reason = GetUnavailabilityReason(ability);
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsAvailable=false for {ability.Name}: {reason}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] MeetsCasterFactRequirements error for {ability?.Name}: {ex.Message}");
                return true; // 에러 시 일단 허용
            }
        }

        /// <summary>
        /// ★ v3.9.72: 능력 제한 이유 상세 파악 — 게임 IsRestricted 16가지 체크 전부 커버
        /// </summary>
        private static string GetRestrictionReason(AbilityData ability)
        {
            var reasons = new List<string>();

            try
            {
                var bp = ability.Blueprint;
                var caster = ability.Caster;
                var unitCaster = caster as BaseUnitEntity;

                // Check 2: CombatStateRestriction
                if (bp.CombatStateRestriction == BlueprintAbility.CombatStateRestrictionType.InCombatOnly && !caster.IsInCombat)
                    reasons.Add("InCombatOnly but not in combat");
                if (bp.CombatStateRestriction == BlueprintAbility.CombatStateRestrictionType.NotInCombatOnly && caster.IsInCombat)
                    reasons.Add("NotInCombatOnly but in combat");

                // Check 3: InterruptionAbilityRestrictions (보너스/인터럽트 턴 제한)
                // PartAbilitySettings 직접 접근 불가 → Fact 기반으로 간접 확인
                // (정확한 진단은 Check 11/13/16에서 수행)

                // Check 4: CasterRestrictions
                foreach (var restriction in bp.CasterRestrictions)
                {
                    if (!restriction.IsCasterRestrictionPassed(caster))
                    {
                        var text = restriction.GetAbilityCasterRestrictionUIText(caster);
                        reasons.Add($"CasterRestriction: {restriction.GetType().Name}: {text}");
                    }
                }

                // Check 6: UsingInThreateningArea
                if (unitCaster?.CombatState != null && unitCaster.CombatState.IsEngaged)
                {
                    if (ability.UsingInThreateningArea == BlueprintAbility.UsingInThreateningAreaType.CannotUse)
                        reasons.Add("CannotUse in threatening area (engaged)");
                }

                // Check 7-9: Area Effect 제한
                if (unitCaster != null)
                {
                    try
                    {
                        var node = (CustomGridNodeBase)(Pathfinding.GraphNode)unitCaster.CurrentNode;
                        if (node != null)
                        {
                            if (!bp.IsWeaponAbility && AreaEffectsController.CheckConcussionEffect(node))
                                reasons.Add("ConcussionEffect (weapon-only zone)");
                            if (bp.IsWeaponAbility && AreaEffectsController.CheckCantAttackEffect(node))
                                reasons.Add("CantAttackEffect (no weapon zone)");
                            if (bp.IsPsykerAbility && AreaEffectsController.CheckInertWarpEffect(node))
                                reasons.Add("InertWarpEffect (psychic null zone)");
                        }
                    }
                    catch { }
                }

                // Check 11: Blueprint.Restrictions (IAbilityRestriction[])
                try
                {
                    // Restrictions 프로퍼티 직접 사용 (ComponentsArray 대신)
                    foreach (var restriction in bp.Restrictions)
                    {
                        if (!restriction.IsAbilityRestrictionPassed(ability))
                        {
                            var uiText = restriction.GetAbilityRestrictionUIText();
                            reasons.Add($"AbilityRestriction: {restriction.GetType().Name}: {uiText}");
                        }
                    }
                }
                catch { }

                // Check 13: UnitPartForbiddenAbilities (AbilityGroupLimitation, AbilitySourceLimitation 등)
                // 직접 접근 불가 (IHavePrototype 어셈블리 미참조) → 버프의 제한 컴포넌트 직접 확인
                if (unitCaster != null)
                {
                    try
                    {
                        var buffs = unitCaster.Buffs;
                        if (buffs != null)
                        {
                            foreach (var buff in buffs.RawFacts)
                            {
                                if (buff.Blueprint?.ComponentsArray == null) continue;
                                foreach (var comp in buff.Blueprint.ComponentsArray)
                                {
                                    string compName = comp.GetType().Name;
                                    if (compName == "AbilityGroupLimitation" ||
                                        compName == "AbilitySourceLimitation" ||
                                        compName == "TargetLimitation")
                                    {
                                        reasons.Add($"ForbiddenAbility: {buff.Name} ({compName})");
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }

                // Check 16: WarhammerAbilityRestriction — Facts 전체 (Buffs + Features)
                if (unitCaster != null)
                {
                    try
                    {
                        bool hasFactRestriction = unitCaster.Facts
                            .GetComponents<Kingmaker.UnitLogic.Buffs.Components.WarhammerAbilityRestriction>(
                                r => r.AbilityIsRestricted(ability)).Any();
                        if (hasFactRestriction)
                        {
                            reasons.Add("FactRestriction (WarhammerAbilityRestriction on caster fact)");
                        }
                    }
                    catch { }
                }

                // ★ Final check: HasRequiredParams / Fact.Active
                // 게임 IsRestricted 최종 return true 경로:
                //   if (HasRequiredParams) { if (Fact == null || Fact.Active) { ...checks... } } return true;
                // → HasRequiredParams=false 이거나 Fact!=null && !Fact.Active 이면 무조건 restricted
                try
                {
                    bool hasReqParams = ability.HasRequiredParams;
                    var fact = ability.Fact;
                    bool factActive = fact?.Active ?? true;  // Fact==null이면 true (통과)

                    if (!hasReqParams)
                        reasons.Add($"HasRequiredParams=false (RequireParamUnitFact)");
                    if (fact != null && !factActive)
                        reasons.Add($"Fact.Active=false (ability fact deactivated, fact={fact.Name})");
                }
                catch { }
            }
            catch { }

            return reasons.Count > 0 ? string.Join(", ", reasons) : "Unknown restriction";
        }

        /// <summary>
        /// ★ v3.8.33: 능력 사용 불가 이유 상세 파악
        /// </summary>
        private static string GetUnavailabilityReason(AbilityData ability)
        {
            var reasons = new List<string>();

            try
            {
                if (ability.GetAvailableForCastCount() == 0)
                    reasons.Add("No casts available");
                if (!ability.HasEnoughActionPoint)
                    reasons.Add("Not enough AP");
                if (!ability.HasEnoughAmmo)
                    reasons.Add("Not enough ammo");
                if (ability.IsRestricted)
                    reasons.Add("IsRestricted");
                if (ability.IsOnCooldown && !ability.IsBonusUsage)
                    reasons.Add("On cooldown");
            }
            catch { }

            return reasons.Count > 0 ? string.Join(", ", reasons) : "Unknown unavailability";
        }

        /// <summary>
        /// 능력이 사용 가능한지 확인 (간단한 버전)
        /// </summary>
        public static bool IsAbilityAvailable(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                return ability.IsAvailable;
            }
            catch (Exception ex)
            {
                // ★ v3.4.01: P1-2 예외 상세 로깅
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsAbilityAvailable error for {ability.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.6.18: 가상 위치에서 타겟 공격 가능 여부 확인
        /// 이동 계획 시 해당 위치에서 실제 공격이 가능한지 검증
        /// </summary>
        /// <param name="ability">체크할 능력</param>
        /// <param name="fromNode">가상 시전 위치</param>
        /// <param name="target">타겟 유닛</param>
        /// <param name="unavailableReason">실패 이유 (출력)</param>
        /// <returns>해당 위치에서 공격 가능 여부</returns>
        public static bool CanTargetFromPosition(
            AbilityData ability,
            CustomGridNodeBase fromNode,
            BaseUnitEntity target,
            out string unavailableReason)
        {
            unavailableReason = null;

            if (ability == null || fromNode == null || target == null)
            {
                unavailableReason = "Null parameter";
                return false;
            }

            try
            {
                var targetNode = target.CurrentUnwalkableNode;
                if (targetNode == null)
                {
                    unavailableReason = "NoTargetNode";
                    return false;
                }

                // ★ 게임의 CanTargetFromNode 사용 - 실제 LOS/거리 검증
                var targetWrapper = new TargetWrapper(target);
                int distance;
                LosCalculations.CoverType coverType;
                AbilityData.UnavailabilityReasonType? gameReason;

                bool canTarget = ability.CanTargetFromNode(
                    fromNode,
                    targetNode,
                    targetWrapper,
                    out distance,
                    out coverType,
                    out gameReason);

                if (!canTarget && gameReason.HasValue)
                {
                    unavailableReason = gameReason.Value.ToString();
                }

                return canTarget;
            }
            catch (Exception ex)
            {
                unavailableReason = $"Exception: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// ★ v3.6.18: 가상 위치에서 공격 가능한 적 수 계산
        /// ★ v3.7.66: BattlefieldGrid 검증 추가 - 위치 유효성 사전 확인
        /// </summary>
        public static int CountHittableEnemiesFromPosition(
            BaseUnitEntity unit,
            CustomGridNodeBase fromNode,
            List<BaseUnitEntity> enemies,
            AbilityData primaryAttack = null,
            List<BaseUnitEntity> allies = null,  // ★ v3.8.70: scatter safety용
            float maxRangeOverride = 0f)  // ★ v3.9.86: 무기 로테이션용 사거리 오버라이드
        {
            if (unit == null || fromNode == null || enemies == null || enemies.Count == 0)
                return 0;

            // ★ v3.7.66: 위치 유효성 사전 확인 - 설 수 없는 위치면 0
            var grid = Analysis.BattlefieldGrid.Instance;
            if (grid != null && grid.IsValid && !grid.CanUnitStandOn(unit, fromNode))
            {
                return 0;
            }

            // 공격 능력이 없으면 가장 기본 공격 찾기
            if (primaryAttack == null)
            {
                primaryAttack = FindAnyAttackAbility(unit, Settings.RangePreference.PreferRanged);
            }
            // ★ v3.9.92: 일반 공격 없으면 DangerousAoE (화염방사기 등) 시도
            if (primaryAttack == null)
            {
                primaryAttack = FindAnyAttackAbility(unit, Settings.RangePreference.PreferRanged, includeDangerousAoE: true);
            }

            if (primaryAttack == null)
                return 0;

            // ★ v3.9.92: DangerousAoE 포인트 타겟 감지
            // CanTargetEnemies=false인 DangerousAoE는 CanTargetFromPosition이 항상 실패
            // → 거리+LOS 기반 평가로 대체 (패턴 반경 내 + 시야 확인)
            bool isDangerousAoEPointTarget = AbilityDatabase.IsDangerousAoE(primaryAttack)
                && primaryAttack.Blueprint != null && !primaryAttack.Blueprint.CanTargetEnemies;
            float dangerousAoERadius = 0f;
            if (isDangerousAoEPointTarget)
            {
                var patternInfo = GetPatternInfo(primaryAttack);
                dangerousAoERadius = (patternInfo != null && patternInfo.IsValid)
                    ? patternInfo.Radius
                    : (float)GetAbilityRangeInTiles(primaryAttack);
            }

            int count = 0;
            foreach (var enemy in enemies)
            {
                if (enemy == null || enemy.LifeState.IsDead) continue;

                // ★ v3.9.92: DangerousAoE 포인트 타겟 — 거리+LOS 기반 평가
                // CanTargetFromPosition은 CanTargetEnemies=false라 항상 실패
                // 대신: 패턴 반경 내 + LOS 확보 시 hittable 판정
                if (isDangerousAoEPointTarget)
                {
                    float distTiles = GetDistanceInTiles(fromNode.Vector3Position, enemy);
                    if (distTiles > dangerousAoERadius) continue;

                    // LOS 체크
                    try
                    {
                        var enemyNode = enemy.CurrentUnwalkableNode;
                        if (enemyNode == null) continue;
                        var los = LosCalculations.GetWarhammerLos(
                            fromNode, unit.SizeRect, enemyNode, enemy.SizeRect);
                        if (los.CoverType == LosCalculations.CoverType.Invisible) continue;
                    }
                    catch { continue; }

                    // 아군 안전 체크
                    if (allies != null)
                    {
                        if (!CombatHelpers.IsAttackSafeForTargetFromPosition(
                            primaryAttack, fromNode.Vector3Position, unit, enemy, allies))
                            continue;
                    }
                    count++;
                    continue;  // 다음 적으로
                }

                string reason;
                if (CanTargetFromPosition(primaryAttack, fromNode, enemy, out reason))
                {
                    // ★ v3.9.24: 대형 유닛 거리 보정 — CanTargetFromNode vs CanUseAbilityOn 불일치 방지
                    // ★ v3.9.86: maxRangeOverride가 설정되면 능력 사거리 대신 사용
                    //   (무기 로테이션: 볼터 24 → 화염방사기 7 전환 시 짧은 사거리로 필터링)
                    if (!IsPointTargetAbility(primaryAttack))
                    {
                        float rangeTiles = maxRangeOverride > 0f
                            ? maxRangeOverride
                            : (float)GetAbilityRangeInTiles(primaryAttack);
                        float distTiles = GetDistanceInTiles(fromNode.Vector3Position, enemy);
                        if (distTiles > rangeTiles)
                            continue;
                    }

                    // ★ v3.9.24: DangerousAoE Directional 패턴 거리 검증
                    // CanTargetFromPosition은 무기 RangeCells만 체크하고 패턴 반경은 체크 안 함
                    // Cone/Ray/Sector 패턴은 patternRadius까지만 유효
                    if (AbilityDatabase.IsDangerousAoE(primaryAttack))
                    {
                        var patternInfo = GetPatternInfo(primaryAttack);
                        if (patternInfo != null && patternInfo.IsValid && patternInfo.CanBeDirectional)
                        {
                            float distTiles = GetDistanceInTiles(fromNode.Vector3Position, enemy);
                            if (distTiles > patternInfo.Radius)
                                continue;
                        }
                    }

                    // ★ v3.8.70: 후보 위치에서의 안전 체크 (scatter safety 포함)
                    if (allies != null)
                    {
                        if (!CombatHelpers.IsAttackSafeForTargetFromPosition(
                            primaryAttack, fromNode.Vector3Position, unit, enemy, allies))
                            continue;
                    }
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// ★ v3.5.15: 능력이 쿨다운 그룹 포함 완전 쿨다운 체크
        /// GetUnavailabilityReasons()는 그룹 쿨다운을 감지하지 못함
        /// PartAbilityCooldowns.IsOnCooldown()을 직접 사용해야 정확함
        /// ★ 주의: IsOnCooldown()은 IsIgnoredByComponent 조건이 있어서 그룹 쿨다운을 놓칠 수 있음
        /// GroupIsOnCooldown()으로 각 그룹을 직접 체크해야 함
        /// ★ v3.5.16: 중복 그룹 체크 추가 (게임 데이터 버그 대응)
        /// ★ v3.5.81: 보너스 사용 체크 추가 (런앤건 등)
        /// </summary>
        public static bool IsAbilityOnCooldownWithGroups(AbilityData ability)
        {
            if (ability == null) return true;

            try
            {
                // ★ 안전한 이름 추출 (로컬라이제이션 에러 방지)
                string abilityName = "Unknown";
                try { abilityName = ability.Blueprint?.name ?? ability.Name ?? "Unknown"; }
                catch { /* 로컬라이제이션 에러 무시 */ }

                // ★ v3.5.81: 보너스 사용 체크 - IsAvailable이 true면 보너스 사용 가능
                // 쿨다운이어도 런앤건 등으로 보너스 사용이 부여되면 IsAvailable=true
                if (ability.IsAvailable)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CooldownCheck: {abilityName} - IsAvailable=true (bonus usage available)");
                    return false; // 보너스 사용 가능 → 쿨다운 아닌 것으로 처리
                }

                var caster = ability.Caster as BaseUnitEntity;
                if (caster == null)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CooldownCheck: {abilityName} - caster is null");
                    return false;
                }

                var cooldownPart = caster.AbilityCooldowns;
                if (cooldownPart == null)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CooldownCheck: {abilityName} - cooldownPart is null");
                    return false;
                }

                // 1. 능력 자체 쿨다운 체크 (이건 IsIgnoredByComponent를 고려함)
                bool isOnCooldown = cooldownPart.IsOnCooldown(ability);
                if (isOnCooldown)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CooldownCheck: {abilityName} - ability on cooldown");
                    return true;
                }

                // 2. 그룹 쿨다운 체크
                var groups = ability.AbilityGroups;
                if (groups != null && groups.Count > 0)
                {
                    // ★ v3.5.16: 중복 그룹 감지 - 게임 데이터 버그로 중복 그룹이 있으면
                    // StartGroupCooldown()에서 에러 발생. 중복 그룹이 있는 능력은 사용 차단.
                    var seenGroups = new HashSet<string>();
                    foreach (var group in groups)
                    {
                        if (group == null) continue;
                        string groupId = group.AssetGuid?.ToString() ?? group.name ?? "unknown";
                        if (seenGroups.Contains(groupId))
                        {
                            Main.Log($"[CombatAPI] ★ {abilityName}: BLOCKED - duplicate group detected (game data bug)");
                            return true; // 중복 그룹이 있으면 사용 차단
                        }
                        seenGroups.Add(groupId);

                        bool groupOnCooldown = cooldownPart.GroupIsOnCooldown(group);
                        if (groupOnCooldown)
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CooldownCheck: {abilityName} - Group '{group.name}' on cooldown");
                            return true;
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Main.LogError($"[CombatAPI] IsAbilityOnCooldownWithGroups error: {ex.Message}\n{ex.StackTrace}");
                return false; // 에러 시 일단 허용
            }
        }

        /// <summary>
        /// ★ v3.5.32: 중복 그룹 체크 (쿨다운 체크 없이 그룹 중복만 확인)
        /// 게임 데이터 버그로 일부 능력이 동일 그룹에 중복 등록되어 있음
        /// </summary>
        public static bool HasDuplicateAbilityGroups(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                var groups = ability.AbilityGroups;
                if (groups == null || groups.Count <= 1) return false;

                var seenGroups = new HashSet<string>();
                foreach (var group in groups)
                {
                    if (group == null) continue;
                    string groupId = group.AssetGuid?.ToString() ?? group.name ?? "unknown";
                    if (seenGroups.Contains(groupId))
                    {
                        return true; // 중복 그룹 발견
                    }
                    seenGroups.Add(groupId);
                }
                return false;
            }
            catch
            {
                return false; // 에러 시 일단 허용
            }
        }

        /// <summary>
        /// ★ v3.0.17: 능력이 사용 가능한지 상세 확인 (v2.2에서 포팅)
        /// GetUnavailabilityReasons()로 실제 사용 불가 이유 확인
        /// ★ v3.1.11: 보너스 사용(런 앤 건 등) 처리 추가
        /// </summary>
        public static bool IsAbilityAvailable(AbilityData ability, out List<string> reasons)
        {
            reasons = new List<string>();

            if (ability == null)
            {
                reasons.Add("Null ability");
                return false;
            }

            try
            {
                // ★ 소모품 충전 횟수 체크 (charges=0이면 사용 불가)
                if (ability.SourceItem != null)
                {
                    var usableItem = ability.SourceItem as Kingmaker.Items.ItemEntityUsable;
                    if (usableItem != null && usableItem.Charges <= 0)
                    {
                        reasons.Add("No charges remaining");
                        return false;
                    }
                }

                // ★ 핵심: GetUnavailabilityReasons() 사용 - v2.2와 동일
                var unavailabilityReasons = ability.GetUnavailabilityReasons();

                if (unavailabilityReasons.Count > 0)
                {
                    // ★ v3.1.11: 쿨다운이어도 보너스 사용이 있으면 허용
                    // IsAvailable은 IsBonusUsage를 체크하므로, IsAvailable=true면 보너스 사용 가능
                    bool onlyOnCooldown = unavailabilityReasons.All(r =>
                        r == AbilityData.UnavailabilityReasonType.IsOnCooldown ||
                        r == AbilityData.UnavailabilityReasonType.IsOnCooldownUntilEndOfCombat);

                    if (onlyOnCooldown && ability.IsAvailable)
                    {
                        // 쿨다운이지만 보너스 사용 가능 (런 앤 건 등)
                        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsAbilityAvailable: {ability.Name} on cooldown but has bonus usage");
                        return true;
                    }

                    // ★ v3.8.37: WarhammerFreeUltimateBuff가 있으면 IsUltimateAbilityUsedThisRound 무시
                    // 잠재력 초월(SoulMarkHope4) 버프는 궁극기 라운드 제한을 우회해야 함
                    bool onlyUltimateRoundLimit = unavailabilityReasons.All(r =>
                        r == AbilityData.UnavailabilityReasonType.IsUltimateAbilityUsedThisRound);

                    if (onlyUltimateRoundLimit)
                    {
                        var caster = ability.Caster;
                        if (caster != null && caster.Facts.HasComponent<WarhammerFreeUltimateBuff>(null))
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsAbilityAvailable: {ability.Name} has WarhammerFreeUltimateBuff - bypassing round limit");
                            return true;
                        }
                    }

                    reasons = unavailabilityReasons.Select(r => r.ToString()).ToList();
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                reasons.Add($"Exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.0.17: 공격성 능력인지 확인 (적만 타겟 가능)
        /// </summary>
        public static bool IsOffensiveAbility(AbilityData ability)
        {
            if (ability == null) return false;
            try
            {
                var bp = ability.Blueprint;
                return bp.CanTargetEnemies && !bp.CanTargetFriends;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsOffensiveAbility failed: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Unit State

        /// <summary>
        /// HP 퍼센트 반환
        /// ★ v3.0.1: GetActualHP/GetActualMaxHP 기반으로 통합
        /// </summary>
        public static float GetHPPercent(BaseUnitEntity unit)
        {
            if (unit == null) return 0f;
            try
            {
                int current = GetActualHP(unit);
                int max = GetActualMaxHP(unit);
                if (max <= 0) return 100f;
                return (float)current / max * 100f;
            }
            // ★ v3.13.0: 안전한 기본값 — 0f (부상으로 판단 → 방어적 행동 유도)
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetHPPercent failed for {unit?.CharacterName}: {ex.Message}");
                return 0f;
            }
        }

        /// <summary>
        /// ★ v3.0.13 Fix: AP/MP 수정
        /// Yellow = Action Points (스킬/공격용)
        /// Blue = Movement Points (이동용)
        /// </summary>
        public static float GetCurrentAP(BaseUnitEntity unit)
        {
            if (unit == null) return 0f;
            try
            {
                // ★ Yellow Action Points = 액션 포인트 (능력/공격)
                // ★ v3.13.0: ?? 0f (기존 3f → AP 없으면 EndTurn이 안전)
                return unit.CombatState?.ActionPointsYellow ?? 0f;
            }
            // ★ v3.13.0: 안전한 기본값 — 0f (AP 없음 → EndTurn)
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetCurrentAP failed for {unit?.CharacterName}: {ex.Message}");
                return 0f;
            }
        }

        /// <summary>
        /// ★ v3.0.13 Fix: AP/MP 수정
        /// Blue = Movement Points (이동용)
        /// </summary>
        public static float GetCurrentMP(BaseUnitEntity unit)
        {
            if (unit == null) return 0f;
            try
            {
                // ★ Blue Action Points = 이동 포인트 (Movement Points)
                return unit.CombatState?.ActionPointsBlue ?? 0f;
            }
            // ★ v3.13.0: 로깅 추가 (기본값 0f는 이미 안전)
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetCurrentMP failed for {unit?.CharacterName}: {ex.Message}");
                return 0f;
            }
        }

        /// <summary>
        /// ★ v3.111.18 Phase C.4: 적별 threat range 턴별 캐시.
        ///   reflection 호출 (AiCollectedDataStorage + weapon blueprint)이 비싸서
        ///   EvaluatePosition이 tile × enemies 반복 호출 시 3,200회/scan 핫스팟.
        ///   threat range는 한 턴 동안 불변(무기/학습 데이터 턴 중 안 바뀜) → 캐시 안전.
        ///   무효화: CombatCache.ClearAll() (턴 시작).
        /// </summary>
        private static readonly Dictionary<BaseUnitEntity, int> _enemyThreatRangeCache
            = new Dictionary<BaseUnitEntity, int>();

        /// <summary>★ v3.111.18: 턴 시작 시 CombatCache.ClearAll()에서 호출.</summary>
        public static void ClearEnemyThreatRangeCache() => _enemyThreatRangeCache.Clear();

        /// <summary>
        /// ★ v3.110.20: 적의 위협 사거리 (타일 단위).
        /// 게임 AI 학습 데이터 (GetThreatRange) + 현재 장비 무기 사거리 중 큰 값.
        /// 게임 학습이 없는 신규 유닛은 무기 사거리로 폴백.
        /// 게임 패턴: AttackEffectivenessTileScorer.CalculateEnemyTargetThreatScore
        /// ★ v3.111.18 Phase C.4: 턴별 캐시 적용.
        /// </summary>
        public static int GetEnemyThreatRangeInTiles(BaseUnitEntity enemy)
        {
            if (enemy == null) return 0;

            // ★ v3.111.18: 턴 내 캐시 체크
            if (_enemyThreatRangeCache.TryGetValue(enemy, out int cached))
                return cached;

            int learnedRange = 0;
            try
            {
                var dataStorage = Kingmaker.Game.Instance?.Player?.AiCollectedDataStorage;
                if (dataStorage != null)
                {
                    var unitData = dataStorage[enemy];
                    if (unitData != null && unitData.AttackDataCollection != null)
                        learnedRange = unitData.AttackDataCollection.GetThreatRange();
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled)
                    Main.LogWarning($"[CombatAPI] GetEnemyThreatRange learned failed for {enemy?.CharacterName}: {ex.Message}");
            }

            int weaponRange = 0;
            try
            {
                var weapon = enemy.GetFirstWeapon();
                if (weapon != null && weapon.Blueprint != null)
                    weaponRange = weapon.Blueprint.AttackRange;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled)
                    Main.LogWarning($"[CombatAPI] GetEnemyThreatRange weapon failed for {enemy?.CharacterName}: {ex.Message}");
            }

            int result = System.Math.Max(learnedRange, weaponRange);
            _enemyThreatRangeCache[enemy] = result;
            return result;
        }

        /// <summary>
        /// ★ v3.110.20: 적이 이 턴에 특정 위치를 공격 가능한 확률 (0 / 0.5 / 1).
        /// 게임 패턴: threatRange + AP_Blue 기준.
        ///   dist ≤ threatRange        → 1.0 (즉시 공격 가능)
        ///   dist ≤ threatRange + AP   → 0.5 (이동 후 공격 가능)
        ///   dist > threatRange + AP   → 0   (이 턴 안전)
        /// 참조: AttackEffectivenessTileScorer.CalculateEnemyTargetThreatScore (decompile 195-215)
        /// </summary>
        public static float GetEnemyTurnThreatScore(BaseUnitEntity enemy, UnityEngine.Vector3 targetPos)
        {
            if (enemy == null) return 0f;

            int threatRange = GetEnemyThreatRangeInTiles(enemy);
            float apBlue = 0f;
            try
            {
                apBlue = enemy.CombatState?.ActionPointsBlue ?? 0f;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled)
                    Main.LogWarning($"[CombatAPI] GetEnemyTurnThreatScore AP read failed for {enemy?.CharacterName}: {ex.Message}");
            }

            int distCells = (int)System.Math.Ceiling(GetDistanceInTiles(targetPos, enemy));
            if (distCells <= threatRange) return 1.0f;
            if (distCells <= threatRange + apBlue) return 0.5f;
            return 0f;
        }

        // ★ v3.110.21 Phase 3: UnitPartPriorityTarget 리플렉션 캐시.
        // m_PriorityTargets가 private이라 FieldInfo 캐싱 필수.
        private static System.Reflection.FieldInfo _priorityTargetsField;
        private static bool _priorityTargetsFieldLookupAttempted;

        /// <summary>
        /// ★ v3.110.21: 이 타겟이 공격자의 "우선 공격 대상" 여부.
        /// 도발/마크/겨냥 능력으로 UnitPartPriorityTarget.AddTarget된 Buff 리스트 순회.
        /// Buff.Owner == target이면 priority target.
        ///
        /// 게임 API: UnitPartPriorityTarget.GetPriorityTarget(BlueprintBuff)은 forward 전용.
        /// 역방향 조회 ("이 타겟이 우선인가") 위해 m_PriorityTargets 리플렉션 접근.
        /// FieldInfo 캐시로 성능 부담 최소화 (조회 1회당 ~마이크로초).
        /// </summary>
        public static bool IsPriorityTargetFor(BaseUnitEntity target, BaseUnitEntity attacker)
        {
            if (target == null || attacker == null) return false;

            try
            {
                var priorityPart = attacker.GetOptional<Kingmaker.UnitLogic.Parts.UnitPartPriorityTarget>();
                if (priorityPart == null) return false;

                // FieldInfo 1회 lookup + 캐싱
                if (!_priorityTargetsFieldLookupAttempted)
                {
                    _priorityTargetsFieldLookupAttempted = true;
                    _priorityTargetsField = typeof(Kingmaker.UnitLogic.Parts.UnitPartPriorityTarget)
                        .GetField("m_PriorityTargets",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (_priorityTargetsField == null)
                    {
                        Main.LogWarning("[CombatAPI] IsPriorityTargetFor: m_PriorityTargets field not found via reflection. Priority target detection disabled.");
                    }
                }

                if (_priorityTargetsField == null) return false;

                var typedList = _priorityTargetsField.GetValue(priorityPart)
                    as System.Collections.Generic.List<Kingmaker.EntitySystem.EntityFactRef<Kingmaker.UnitLogic.Buffs.Buff>>;
                if (typedList == null) return false;

                foreach (var entityFactRef in typedList)
                {
                    var buff = entityFactRef.Fact;
                    if (buff?.Owner == target) return true;
                }
            }
            catch (System.Exception ex)
            {
                if (Main.IsDebugEnabled)
                    Main.LogWarning($"[CombatAPI] IsPriorityTargetFor reflection failed: {ex.Message}");
            }

            return false;
        }

        public static bool CanMove(BaseUnitEntity unit)
        {
            if (unit == null) return false;
            try { return unit.State.CanMove; }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CanMove failed for {unit?.CharacterName}: {ex.Message}");
                return false;
            }
        }

        public static bool CanAct(BaseUnitEntity unit)
        {
            if (unit == null) return false;
            try { return unit.State.CanActInTurnBased; }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CanAct failed for {unit?.CharacterName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.111.14: 능력 표시명 안전 조회 — LocalizedString 예외 격리.
        /// ability.Name(대문자 N)은 LocalizedString 경유 → 번역 key 누락/깨진 asset reference 시 예외.
        /// bp.name(소문자 n)은 Unity ScriptableObject 내부 이름 → 번역 비경유, 항상 안전.
        /// 로그/디버그 문자열 interpolation에서 사용 (매칭 용도 아님 — 매칭은 GUID 기반 유지).
        /// </summary>
        public static string GetAbilityDisplayName(AbilityData ability)
        {
            if (ability == null) return "null";
            try
            {
                var name = ability.Name;
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch { /* LocalizedString 예외 → fallback */ }

            try
            {
                var bp = ability.Blueprint;
                return bp?.name ?? "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }

        /// <summary>
        /// ★ v3.111.12: 게임 canonical API 기반 ExtraTurn(임시턴) 감지.
        /// 디컴파일 참조: TurnController.GetInterruptingOrder (private static helper).
        ///   - 일반 유닛: unit.Initiative.InterruptingOrder > 0
        ///   - Squad 유닛: squad.Initiative.InterruptingOrder > 0 (companions는 squad 아니지만 safety)
        /// 게임이 TurnOrderQueue.InterruptCurrentUnit에서 셋업, TurnController.EndUnitTurn에서 0 리셋.
        /// v3.111.8 ~ 10의 Harmony hybrid를 대체 — 결정적, 즉시성, 레이싱 없음.
        /// </summary>
        public static bool IsExtraTurn(BaseUnitEntity unit)
        {
            if (unit == null) return false;
            try
            {
                if (unit.Initiative == null) return false;

                // Squad 경로 (defense-in-depth — companions는 not-in-squad지만 enemy mob에 섞일 가능성)
                if (unit.IsInSquad)
                {
                    var squadPart = unit.GetSquadOptional();
                    var squad = squadPart?.Squad;
                    return squad?.Initiative != null && squad.Initiative.InterruptingOrder > 0;
                }

                return unit.Initiative.InterruptingOrder > 0;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsExtraTurn failed for {unit?.CharacterName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.0.10: 명령 큐가 비어있는지 확인 (이전 명령 완료 여부)
        /// 게임의 TaskNodeWaitCommandsDone과 동일한 체크
        /// </summary>
        public static bool IsCommandQueueEmpty(BaseUnitEntity unit)
        {
            if (unit == null) return true;
            try
            {
                return unit.Commands.Empty;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsCommandQueueEmpty failed for {unit?.CharacterName}: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// ★ v3.0.10: 유닛이 다음 행동을 할 준비가 되었는지 확인
        /// Commands.Empty && CanActInTurnBased
        /// </summary>
        public static bool IsReadyForNextAction(BaseUnitEntity unit)
        {
            if (unit == null) return false;
            try
            {
                return unit.Commands.Empty && unit.State.CanActInTurnBased;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsReadyForNextAction failed for {unit?.CharacterName}: {ex.Message}");
                return false;
            }
        }

        public static float GetDistance(BaseUnitEntity from, BaseUnitEntity to)
        {
            if (from == null || to == null) return float.MaxValue;
            try
            {
                // ★ v3.8.66: 게임 API 기반 (SizeRect 반영) — 미터 단위
                return (float)from.DistanceToInCells(to) * GridCellSize;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetDistance failed: {ex.Message}");
                return float.MaxValue;
            }
        }

        #endregion

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

        #region Weapon & Ammo

        public static bool HasRangedWeapon(BaseUnitEntity unit)
        {
            if (unit == null) return false;
            try
            {
                var primary = unit.Body?.PrimaryHand?.MaybeWeapon;
                if (primary != null && !primary.Blueprint.IsMelee) return true;

                var secondary = unit.Body?.SecondaryHand?.MaybeWeapon;
                if (secondary != null && !secondary.Blueprint.IsMelee) return true;

                return false;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] HasRangedWeapon failed for {unit?.CharacterName}: {ex.Message}");
                return false;
            }
        }

        public static bool HasMeleeWeapon(BaseUnitEntity unit)
        {
            if (unit == null) return false;
            try
            {
                var primary = unit.Body?.PrimaryHand?.MaybeWeapon;
                if (primary != null && primary.Blueprint.IsMelee) return true;

                var secondary = unit.Body?.SecondaryHand?.MaybeWeapon;
                if (secondary != null && secondary.Blueprint.IsMelee) return true;

                return false;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] HasMeleeWeapon failed for {unit?.CharacterName}: {ex.Message}");
                return false;
            }
        }

        public static bool NeedsReloadAnyRanged(BaseUnitEntity unit)
        {
            if (unit == null) return false;

            try
            {
                var body = unit.Body;
                if (body == null) return false;

                // 현재 무기 체크
                if (CheckWeaponNeedsReload(body.PrimaryHand?.MaybeWeapon)) return true;
                if (CheckWeaponNeedsReload(body.SecondaryHand?.MaybeWeapon)) return true;

                // 다른 무기 세트 체크
                var handsSets = body.HandsEquipmentSets;
                if (handsSets != null)
                {
                    foreach (var set in handsSets)
                    {
                        if (CheckWeaponNeedsReload(set?.PrimaryHand?.MaybeWeapon)) return true;
                        if (CheckWeaponNeedsReload(set?.SecondaryHand?.MaybeWeapon)) return true;
                    }
                }
            }
            catch (Exception ex)
            {
                // ★ v3.4.01: P1-2 예외 상세 로깅
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] NeedsReloadAnyRanged error: {ex.Message}");
            }

            return false;
        }

        private static bool CheckWeaponNeedsReload(ItemEntityWeapon weapon)
        {
            if (weapon == null) return false;
            if (weapon.Blueprint.IsMelee) return false;

            int maxAmmo = weapon.Blueprint?.WarhammerMaxAmmo ?? -1;
            if (maxAmmo <= 0) return false;  // 탄약 필요 없음

            return weapon.CurrentAmmo <= 0;
        }

        public static int GetCurrentAmmo(BaseUnitEntity unit)
        {
            if (unit == null) return -1;
            try
            {
                var weapon = unit.Body?.PrimaryHand?.MaybeWeapon;
                if (weapon == null) return -1;
                if (weapon.Blueprint.IsMelee) return -1;

                return weapon.CurrentAmmo;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetCurrentAmmo failed for {unit?.CharacterName}: {ex.Message}");
                return -1;
            }
        }

        public static int GetMaxAmmo(BaseUnitEntity unit)
        {
            if (unit == null) return -1;
            try
            {
                var weapon = unit.Body?.PrimaryHand?.MaybeWeapon;
                if (weapon == null) return -1;
                if (weapon.Blueprint.IsMelee) return -1;

                return weapon.Blueprint?.WarhammerMaxAmmo ?? -1;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetMaxAmmo failed for {unit?.CharacterName}: {ex.Message}");
                return -1;
            }
        }

        #endregion

        #region Weapon Set Management (v3.9.72)

        /// <summary>
        /// ★ v3.9.72: 현재 활성 무기 세트 인덱스 (0 또는 1)
        /// </summary>
        public static int GetCurrentWeaponSetIndex(BaseUnitEntity unit)
        {
            return unit?.Body?.CurrentHandEquipmentSetIndex ?? 0;
        }

        /// <summary>
        /// ★ v3.9.72: 양쪽 세트에 모두 무기가 장착되어 있는지 확인
        /// </summary>
        public static bool HasMultipleWeaponSets(BaseUnitEntity unit)
        {
            if (unit?.Body?.HandsEquipmentSets == null) return false;
            var sets = unit.Body.HandsEquipmentSets;
            if (sets.Count < 2) return false;

            try
            {
                bool set0HasWeapon = sets[0]?.PrimaryHand?.HasItem == true;
                bool set1HasWeapon = sets[1]?.PrimaryHand?.HasItem == true;
                return set0HasWeapon && set1HasWeapon;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] HasMultipleWeaponSets failed for {unit?.CharacterName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.9.72: 무기 세트 전환 실행 (0 AP)
        /// </summary>
        public static void SwitchWeaponSet(BaseUnitEntity unit, int targetSetIndex)
        {
            if (unit == null || targetSetIndex < 0 || targetSetIndex > 1) return;
            if (unit.Body.CurrentHandEquipmentSetIndex == targetSetIndex) return;

            Game.Instance.GameCommandQueue.SwitchHandEquipment(unit, targetSetIndex);
            Main.Log($"[CombatAPI] ★ Weapon switch: {unit.CharacterName} -> Set {targetSetIndex}");
        }

        /// <summary>
        /// ★ v3.9.72: 특정 무기 세트의 주 무기 이름 조회
        /// </summary>
        public static string GetWeaponSetPrimaryName(BaseUnitEntity unit, int setIndex)
        {
            if (unit?.Body?.HandsEquipmentSets == null) return null;
            var sets = unit.Body.HandsEquipmentSets;
            if (setIndex < 0 || setIndex >= sets.Count) return null;

            try
            {
                // ★ MaybeItem 사용 — MaybeWeapon은 비활성 세트에서 Active 체크로 null 반환
                var weapon = sets[setIndex]?.PrimaryHand?.MaybeItem as ItemEntityWeapon;
                return weapon?.Name;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetWeaponNameForSet failed for {unit?.CharacterName}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ★ v3.9.72: 양쪽 세트의 주 무기 Blueprint가 동일한지 확인
        /// 같은 무기면 전환 의미 없음
        /// </summary>
        public static bool AreBothWeaponSetsSame(BaseUnitEntity unit)
        {
            if (unit?.Body?.HandsEquipmentSets == null) return true;
            var sets = unit.Body.HandsEquipmentSets;
            if (sets.Count < 2) return true;

            try
            {
                // ★ MaybeItem 사용 — MaybeWeapon은 비활성 세트에서 Active 체크로 null 반환
                var weapon0 = (sets[0]?.PrimaryHand?.MaybeItem as ItemEntityWeapon)?.Blueprint;
                var weapon1 = (sets[1]?.PrimaryHand?.MaybeItem as ItemEntityWeapon)?.Blueprint;
                if (weapon0 == null || weapon1 == null) return false;
                return weapon0 == weapon1;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] AreBothWeaponSetsSame failed for {unit?.CharacterName}: {ex.Message}");
                return true;
            }
        }

        #endregion

        #region Weapon Range Profile (v3.9.24)

        /// <summary>
        /// ★ v3.9.24: 무기 사거리 중앙집중 프로필
        /// 모든 서브시스템이 일관된 무기 사거리 정보를 사용하도록 중앙에서 관리
        /// </summary>
        public struct WeaponRangeProfile
        {
            /// <summary>무기 최대 사거리 (타일 단위, RangeCells)</summary>
            public float MaxRange;
            /// <summary>무기 최적 사거리 (타일 단위, 없으면 0)</summary>
            public float OptimalRange;
            /// <summary>유효 공격 거리 (방향성 AoE면 patternRadius, 아니면 OptimalRange 또는 MaxRange)</summary>
            public float EffectiveRange;
            /// <summary>Scatter 무기 (자동 명중)</summary>
            public bool IsScatter;
            /// <summary>근접 무기</summary>
            public bool IsMelee;
            /// <summary>Cone/Ray/Sector 패턴 보유</summary>
            public bool HasDirectionalPattern;
            /// <summary>방향성 패턴 반경 (타일 단위)</summary>
            public float PatternRadius;
            /// <summary>★ v3.110.11: 자동 계산된 MinSafeDistance (타일). EffectiveRange × 0.3, 근접/Scatter=0.</summary>
            public float ClampedMinSafeDistance;
            /// <summary>max(0, EffectiveRange - 1) — 후퇴 시 최대 거리</summary>
            public float MaxRetreatDistance;
            /// <summary>EffectiveRange <= 8 인 단거리 무기</summary>
            public bool IsShortRange => EffectiveRange <= 8f;
        }

        // ★ v3.9.24: 유닛당 턴별 캐시 (턴 시작 시 ClearAll()로 클리어)
        private static readonly Dictionary<string, WeaponRangeProfile> _weaponRangeCache = new Dictionary<string, WeaponRangeProfile>();

        /// <summary>
        /// ★ v3.9.24 → v3.110.11: 무기 사거리 프로필 중앙 계산 (자동 MinSafeDistance)
        /// - 무기의 OptimalRange/AttackRange 에서 직접 조회
        /// - 방향성 AoE(Cone/Ray/Sector)면 EffectiveRange = patternRadius
        /// - ClampedMinSafeDistance는 무기 특성에서 자동 계산 (사용자 설정 제거)
        ///   근접/Scatter: 0
        ///   Cone/Ray: PatternRadius × 0.3 (근접에서 Cone burst 가능)
        ///   일반 원거리: EffectiveRange × 0.3
        /// - 모든 서브시스템이 이 하나의 소스에서 무기 사거리를 조회
        /// </summary>
        public static WeaponRangeProfile GetWeaponRangeProfile(BaseUnitEntity unit)
        {
            if (unit == null)
                return CreateDefaultProfile();

            string cacheKey = unit.UniqueId;
            if (_weaponRangeCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var profile = CalculateWeaponRangeProfile(unit);
            _weaponRangeCache[cacheKey] = profile;

            Main.Log($"[CombatAPI] WeaponRangeProfile for {unit.CharacterName}: " +
                $"MaxRange={profile.MaxRange:F1}, OptimalRange={profile.OptimalRange:F1}, " +
                $"EffectiveRange={profile.EffectiveRange:F1}, " +
                $"IsMelee={profile.IsMelee}, IsScatter={profile.IsScatter}, " +
                $"HasDirectional={profile.HasDirectionalPattern}, PatternRadius={profile.PatternRadius:F1}, " +
                $"AutoMinSafe={profile.ClampedMinSafeDistance:F1}, " +
                $"IsShortRange={profile.IsShortRange}");

            return profile;
        }

        /// <summary>★ v3.110.11: 무기 특성 기반 자동 MinSafeDistance (타일).</summary>
        private const float AUTO_MINSAFE_RATIO = 0.3f;

        private static WeaponRangeProfile CalculateWeaponRangeProfile(BaseUnitEntity unit)
        {
            var profile = new WeaponRangeProfile();

            try
            {
                var primaryHand = unit.Body?.PrimaryHand;
                if (primaryHand?.HasWeapon != true)
                    return CreateDefaultProfile();

                var weapon = primaryHand.Weapon;
                bool isMelee = weapon.Blueprint.IsMelee;
                profile.IsMelee = isMelee;

                // ★ v3.111.4: Psyker 오감지 방지 — primaryHand가 melee staff여도
                //   실제 주공격이 Directional AoE(Cone/Ray/Sector) 사이킥이면 ranged 취급.
                //   증상: 카시아(Psyker Support) staff 착용 → melee 판정 → EffectiveRange=1,
                //         MinSafe=0 → AI가 1타일 밀착이 "최적"이라 착각.
                //   수정: melee 조기 return 전에 Cone/Ray/Sector 주공격 능력 탐지.
                var directionalAoE = TryFindDirectionalAoEPrimaryAttack(unit);
                if (directionalAoE.ability != null && directionalAoE.radius > 3f)
                {
                    profile.IsMelee = false;  // ranged 포지셔닝으로 전환
                    profile.HasDirectionalPattern = true;
                    profile.PatternRadius = directionalAoE.radius;
                    profile.MaxRange = directionalAoE.radius;
                    profile.OptimalRange = 0f;
                    // Cone/Ray는 시전자 위치에서 패턴 반경까지만 닿음
                    profile.EffectiveRange = directionalAoE.radius;
                    // Scatter 여부는 패턴 특성과 별개로 체크
                    profile.IsScatter = directionalAoE.ability.IsScatter;
                    if (profile.IsScatter)
                    {
                        profile.ClampedMinSafeDistance = 0f;
                    }
                    else
                    {
                        profile.ClampedMinSafeDistance = Math.Max(1f, profile.EffectiveRange * AUTO_MINSAFE_RATIO);
                    }
                    float maxAllowedRetreatDir = profile.EffectiveRange - 1f;
                    if (maxAllowedRetreatDir < 0f) maxAllowedRetreatDir = 0f;
                    profile.MaxRetreatDistance = maxAllowedRetreatDir;

                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] {unit.CharacterName}: Directional AoE primary attack " +
                        $"detected ({directionalAoE.ability.Name}, radius={directionalAoE.radius:F1}t) — " +
                        $"overriding melee=>ranged profile.");
                    return profile;
                }

                if (isMelee)
                {
                    // 근접 무기: 사거리 = AttackRange (보통 2 타일)
                    profile.MaxRange = weapon.AttackRange > 0 ? weapon.AttackRange : 2f;
                    profile.OptimalRange = 0f;
                    profile.EffectiveRange = profile.MaxRange;
                    profile.ClampedMinSafeDistance = 0f;
                    profile.MaxRetreatDistance = 0f;
                    return profile;
                }

                // 원거리 무기
                int attackRange = weapon.AttackRange;
                int optimalRange = weapon.AttackOptimalRange;

                profile.MaxRange = (attackRange > 0 && attackRange < 10000) ? attackRange : 15f;
                profile.OptimalRange = (optimalRange > 0 && optimalRange < 10000) ? optimalRange : 0f;

                // 기본 유효 사거리: OptimalRange > MaxRange 순서
                profile.EffectiveRange = profile.OptimalRange > 0 ? profile.OptimalRange : profile.MaxRange;

                // Scatter 체크 — 주 공격 능력에서 확인
                var primaryAttack = FindAnyAttackAbility(unit, Settings.RangePreference.PreferRanged);
                if (primaryAttack != null)
                {
                    profile.IsScatter = primaryAttack.IsScatter;

                    // 방향성 AoE 패턴 체크 (Cone/Ray/Sector)
                    var patternInfo = GetPatternInfo(primaryAttack);
                    if (patternInfo != null && patternInfo.IsValid && patternInfo.CanBeDirectional)
                    {
                        profile.HasDirectionalPattern = true;
                        profile.PatternRadius = patternInfo.Radius;
                        // ★ 핵심: 방향성 패턴의 유효 사거리 = 패턴 반경
                        // Cone/Ray는 시전자 위치에서 패턴 반경까지만 닿음
                        profile.EffectiveRange = patternInfo.Radius;
                    }
                }

                // Scatter 무기는 안전 거리 불필요 (자동 명중, 아군 피격 안 함)
                if (profile.IsScatter)
                {
                    profile.ClampedMinSafeDistance = 0f;
                    profile.MaxRetreatDistance = profile.EffectiveRange - 1f;
                    if (profile.MaxRetreatDistance < 0f) profile.MaxRetreatDistance = 0f;
                    return profile;
                }

                // ★ v3.110.11: 자동 MinSafeDistance 계산 (사용자 설정 제거)
                // 근거: EffectiveRange × 0.3 = 적절히 가까운 사격 거리
                //   볼터 단발 (MaxD 15) → 4.5타일
                //   볼터 Cone (PatternRadius 7) → 2.1타일 (Cone burst 영역 진입 가능)
                //   산탄총 (MaxD 5) → 1.5타일
                // 이전: 사용자 설정 7m 고정 → 무기 특성 무시, Cone 사용 봉쇄
                profile.ClampedMinSafeDistance = Math.Max(1f, profile.EffectiveRange * AUTO_MINSAFE_RATIO);

                // MaxRetreatDistance는 여전히 EffectiveRange - 1 (후퇴 상한)
                float maxAllowedRetreat = profile.EffectiveRange - 1f;
                if (maxAllowedRetreat < 0f) maxAllowedRetreat = 0f;
                profile.MaxRetreatDistance = maxAllowedRetreat;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[CombatAPI] CalculateWeaponRangeProfile error for {unit.CharacterName}: {ex.Message}");
                return CreateDefaultProfile();
            }

            return profile;
        }

        private static WeaponRangeProfile CreateDefaultProfile()
        {
            // ★ v3.110.11: 기본 프로필도 EffectiveRange × 0.3 규칙 적용
            const float DEFAULT_EFFECTIVE_RANGE = 15f;
            return new WeaponRangeProfile
            {
                MaxRange = DEFAULT_EFFECTIVE_RANGE,
                OptimalRange = 0f,
                EffectiveRange = DEFAULT_EFFECTIVE_RANGE,
                ClampedMinSafeDistance = Math.Max(1f, DEFAULT_EFFECTIVE_RANGE * AUTO_MINSAFE_RATIO),
                MaxRetreatDistance = DEFAULT_EFFECTIVE_RANGE - 1f,
            };
        }

        /// <summary>
        /// ★ v3.111.7: Directional AoE(Cone/Ray/Sector) 주공격 능력 직접 탐지.
        ///
        /// v3.111.4는 FindAnyAttackAbility 경유 → AbilityDatabase.IsReload → GetTiming →
        /// AutoDetectTiming → IsMultiTarget → CacheAbility → AbilityData.Name getter →
        /// LocalizedString.op_Implicit 사전존재 예외로 Psyker에서 항상 실패.
        ///
        /// v3.111.7 재구현: unit.Abilities.RawFacts 직접 iteration + 개별 ability try/catch.
        /// AbilityDatabase.*/BlueprintCache.* 호출 회피 (Name getter 예외 회피).
        /// Blueprint.CanTargetEnemies / Blueprint.IsMelee 만 사용 (localization 무관).
        /// 최대 반경 Directional 능력을 선택.
        ///
        /// 반환: (ability, radius[tile]) 튜플. 해당 없으면 (null, 0).
        /// </summary>
        private static (AbilityData ability, float radius) TryFindDirectionalAoEPrimaryAttack(BaseUnitEntity unit)
        {
            if (unit == null) return (null, 0f);

            AbilityData best = null;
            float bestRadius = 0f;

            try
            {
                // ★ v3.111.7: 기존 CombatAPI 패턴 그대로 (line 1759 참고) — AbilityDatabase 미경유
                var rawAbilities = unit.Abilities?.RawFacts;
                if (rawAbilities == null) return (null, 0f);

                foreach (var ability in rawAbilities)
                {
                    try
                    {
                        var abilityData = ability?.Data;
                        if (abilityData == null) continue;

                        var bp = abilityData.Blueprint;
                        if (bp == null) continue;

                        // 적 타겟 가능한 공격 능력만 (버프/힐/자기타겟 제외)
                        // Blueprint 프로퍼티는 LocalizedString 미경유 — 안전
                        if (!bp.CanTargetEnemies) continue;

                        // 명시적 melee 스킬 제외 (체인소드 sweep 등)
                        // ★ v3.111.7: IsMelee는 AbilityData 프로퍼티 (Weapon.Blueprint.IsMelee 래핑)
                        if (abilityData.IsMelee) continue;

                        // PatternInfo는 GetAoERadius/GetPatternType/AssetGuid 만 사용 — AbilityDatabase 미경유
                        var patternInfo = GetPatternInfo(abilityData);
                        if (patternInfo == null || !patternInfo.IsValid) continue;
                        if (!patternInfo.CanBeDirectional) continue;
                        if (patternInfo.Radius <= 3f) continue;

                        // 최대 반경 능력 선택 (복수 Directional 능력이 있을 경우 주무기 선호)
                        if (patternInfo.Radius > bestRadius)
                        {
                            best = abilityData;
                            bestRadius = patternInfo.Radius;
                        }
                    }
                    catch
                    {
                        // ★ v3.111.7: 개별 ability 처리 실패 → 다음으로 (안전 스킵)
                        // LocalizedString 예외는 특정 능력에서만 발생하므로 격리 필요
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] TryFindDirectionalAoEPrimaryAttack iteration failed for {unit?.CharacterName}: {ex.Message}");
                return (null, 0f);
            }

            return (best, bestRadius);
        }

        /// <summary>
        /// ★ v3.9.24: 무기 사거리 캐시 클리어 (턴 시작 시 CombatCache.ClearAll()에서 호출)
        /// </summary>
        public static void ClearWeaponRangeCache()
        {
            _weaponRangeCache.Clear();
        }

        #endregion

        #region Unit Lists

        public static List<BaseUnitEntity> GetEnemies(BaseUnitEntity unit)
        {
            var enemies = new List<BaseUnitEntity>();
            if (unit == null) return enemies;

            try
            {
                // ★ v3.9.40: IsInCombat 필터로 현재 전투 참가자만 포함
                // 기존: AllBaseAwakeUnits 전체 → 맵 전체의 모든 적 포함 (비전투 적까지 타겟팅)
                // 수정: IsInCombat 플래그로 현재 전투에 참가 중인 유닛만 필터링
                var allUnits = Game.Instance?.State?.AllBaseAwakeUnits;
                if (allUnits == null) return enemies;

                bool inTurnBasedCombat = Game.Instance?.TurnController?.TurnBasedModeActive == true;
                int skippedNonCombat = 0;

                foreach (var other in allUnits)
                {
                    if (other == null || other == unit) continue;
                    if (other.LifeState.IsDead) continue;

                    // ★ v3.9.40: 턴제 전투 중이면 전투 참가자만 포함
                    if (inTurnBasedCombat && !other.IsInCombat)
                    {
                        skippedNonCombat++;
                        continue;
                    }

                    bool isEnemy = (unit.IsPlayerFaction && other.IsPlayerEnemy) ||
                                   (!unit.IsPlayerFaction && !other.IsPlayerEnemy);

                    if (isEnemy)
                    {
                        enemies.Add(other);
                    }
                }

                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetEnemies: {enemies.Count} enemies (filtered {skippedNonCombat} non-combat units)");
            }
            catch (Exception ex)
            {
                // ★ v3.4.01: P1-2 예외 상세 로깅
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetEnemies error: {ex.Message}");
            }

            return enemies;
        }

        public static List<BaseUnitEntity> GetAllies(BaseUnitEntity unit)
        {
            var allies = new List<BaseUnitEntity>();
            if (unit == null) return allies;

            try
            {
                var allUnits = Game.Instance?.State?.AllBaseAwakeUnits;
                if (allUnits == null) return allies;

                foreach (var other in allUnits)
                {
                    if (other == null || other == unit) continue;
                    if (other.LifeState.IsDead) continue;

                    // 아군 판별
                    bool isAlly = unit.IsPlayerFaction == other.IsPlayerFaction;

                    if (isAlly)
                    {
                        allies.Add(other);
                    }
                }
            }
            catch (Exception ex)
            {
                // ★ v3.4.01: P1-2 예외 상세 로깅
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAllies error: {ex.Message}");
            }

            return allies;
        }

        #endregion

        #region Abilities

        /// <summary>
        /// ★ v3.0.94: GetUnavailabilityReasons() 체크 추가
        /// 기존: data.IsAvailable만 체크 → 쿨다운 능력도 포함됨!
        /// 수정: GetUnavailabilityReasons()로 쿨다운, 탄약, 충전 등 모두 체크
        /// ★ v3.1.11: 보너스 사용(런 앤 건 등) 처리 추가
        /// </summary>
        public static List<AbilityData> GetAvailableAbilities(BaseUnitEntity unit)
        {
            if (unit == null) return new List<AbilityData>();

            // ★ v3.8.80: 프레임 캐시 - 같은 프레임/유닛이면 이전 결과 재사용
            // ProcessTurn 1회당 Analyze(2회) + Plan(2+회) = 4+회 호출되지만 결과 동일
            int currentFrame = Time.frameCount;
            string unitId = unit.UniqueId;
            if (_cachedAbilitiesList != null
                && _cachedAbilitiesFrame == currentFrame
                && _cachedAbilitiesUnitId == unitId)
            {
                return _cachedAbilitiesList;
            }

            var abilities = new List<AbilityData>();

            try
            {
                var rawAbilities = unit.Abilities?.RawFacts;
                if (rawAbilities == null) return abilities;

                foreach (var ability in rawAbilities)
                {
                    try
                    {
                        var data = ability?.Data;
                        if (data == null) continue;

                        // ★ v3.6.20: IsAbilityAvailable(out reasons)와 동일한 로직 사용
                        List<string> reasons;
                        if (!IsAbilityAvailable(data, out reasons))
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Filtered out {GetAbilityDisplayName(data)}: {string.Join(", ", reasons)}");
                            continue;
                        }

                        // ★ v3.5.32: 중복 그룹 체크 - 계획 단계에서 필터링
                        if (HasDuplicateAbilityGroups(data))
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Filtered out {GetAbilityDisplayName(data)}: duplicate ability groups (game data bug)");
                            continue;
                        }

                        abilities.Add(data);
                    }
                    catch (Exception iterEx)
                    {
                        // ★ v3.111.14: 단일 능력 처리 실패 → 다음으로 (LocalizedString 등 예외 격리)
                        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAvailableAbilities: skip ability due to {iterEx.GetType().Name}: {iterEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                // ★ v3.4.01: P1-2 예외 상세 로깅
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAvailableAbilities error: {ex.Message}");
            }

            // 캐시 저장
            _cachedAbilitiesUnitId = unitId;
            _cachedAbilitiesFrame = currentFrame;
            _cachedAbilitiesList = abilities;

            return abilities;
        }

        /// <summary>
        /// ★ v3.0.17: v2.2에서 포팅 - 완전한 공격 능력 검증
        /// - Weapon 확인
        /// - 재장전 제외
        /// - 수류탄 제외 (IsGrenadeOrExplosive)
        /// - ★ GetUnavailabilityReasons() 체크 (핵심!)
        /// - RangePreference에 맞는 무기 우선
        /// - 폴백으로 IsOffensiveAbility 확인
        /// </summary>
        public static AbilityData FindAnyAttackAbility(BaseUnitEntity unit, RangePreference preference,
            bool includeDangerousAoE = false)  // ★ v3.9.92: DangerousAoE 포함 옵션
        {
            if (unit == null) return null;

            try
            {
                var rawAbilities = unit.Abilities?.RawFacts;
                if (rawAbilities == null) return null;

                AbilityData preferredAttack = null;
                float preferredRange = 0f;
                AbilityData fallbackAttack = null;

                foreach (var ability in rawAbilities)
                {
                    try
                    {
                        var abilityData = ability?.Data;
                        if (abilityData == null) continue;

                        // 1. 무기 공격만
                        if (abilityData.Weapon == null) continue;

                        // 2. 재장전 제외
                        if (AbilityDatabase.IsReload(abilityData)) continue;

                        // 3. ★ v3.0.17: 수류탄/폭발물 제외 (v2.2 포팅)
                        if (CombatHelpers.IsGrenadeOrExplosive(abilityData))
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Skipping {GetAbilityDisplayName(abilityData)}: IsGrenadeOrExplosive");
                            continue;
                        }

                        // 4. ★ v3.0.18: CanTargetEnemies 체크 (v3.0.16에서 누락됨!)
                        // "칼날" 같은 스킬은 Weapon != null 이지만 적을 타겟할 수 없음
                        // ★ v3.9.92: DangerousAoE (화염방사기 Cone/Ray)는 포인트 타겟이지만
                        //   적 위치를 타겟할 수 있으므로 includeDangerousAoE=true 시 허용
                        var bp = abilityData.Blueprint;
                        if (bp != null && !bp.CanTargetEnemies)
                        {
                            if (includeDangerousAoE && AbilityDatabase.IsDangerousAoE(abilityData))
                            {
                                // DangerousAoE 포인트 타겟 — 위치 평가에 사용 가능
                            }
                            else
                            {
                                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Skipping {GetAbilityDisplayName(abilityData)}: CanTargetEnemies=false");
                                continue;
                            }
                        }

                        // 5. ★ v3.0.17: 핵심! GetUnavailabilityReasons() 체크 (v2.2 포팅)
                        List<string> reasons;
                        if (!IsAbilityAvailable(abilityData, out reasons))
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Skipping {GetAbilityDisplayName(abilityData)}: {string.Join(", ", reasons)}");
                            continue;
                        }

                        // 5. ★ v3.0.27: RangePreference에 맞는 무기 중 사거리가 가장 긴 것 선택
                        // 기존: 첫 번째 선호 무기에서 break → 사거리 짧은 "현상금 청구" 문제
                        if (CombatHelpers.IsPreferredWeaponType(abilityData, preference))
                        {
                            float range = GetAbilityRange(abilityData);
                            if (preferredAttack == null || range > preferredRange)
                            {
                                preferredAttack = abilityData;
                                preferredRange = range;
                                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Found preferred ({preference}) attack: {GetAbilityDisplayName(abilityData)} (range={range:F1})");
                            }
                            // ★ v3.0.27: break 제거 - 더 긴 사거리 무기를 찾기 위해 계속 검색
                        }
                        else if (fallbackAttack == null)
                        {
                            fallbackAttack = abilityData;  // 폴백용 저장
                        }
                    }
                    catch (Exception iterEx)
                    {
                        // ★ v3.111.14: per-ability 예외 격리 (LocalizedString 등) → 다음 능력으로
                        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] FindAnyAttackAbility: skip ability due to {iterEx.GetType().Name}: {iterEx.Message}");
                    }
                }

                // 선호 타입이 있으면 사용
                if (preferredAttack != null)
                {
                    return preferredAttack;
                }

                // ★ v3.0.21: 선호 무기가 없을 때, RangePreference에 따라 사이킥 공격 우선 검토
                // 카시아 같은 원거리 사이커는 근접 무기보다 사이킥 공격 우선
                if (preference == RangePreference.PreferRanged)
                {
                    foreach (var ability in rawAbilities)
                    {
                        try
                        {
                            var abilityData = ability?.Data;
                            if (abilityData == null) continue;

                            // 무기 아닌 공격성 능력 (사이킥 공격 등)
                            if (abilityData.Weapon != null) continue;
                            if (!IsOffensiveAbility(abilityData)) continue;

                            // 근접 스킬 제외
                            if (abilityData.IsMelee) continue;

                            List<string> reasons;
                            if (IsAbilityAvailable(abilityData, out reasons))
                            {
                                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Found ranged offensive ability (pref={preference}): {GetAbilityDisplayName(abilityData)}");
                                return abilityData;
                            }
                        }
                        catch (Exception iterEx)
                        {
                            // ★ v3.111.14: per-ability 예외 격리 (psyker LocalizedString 핫스팟)
                            if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] FindAnyAttackAbility psyker fallback: skip ability due to {iterEx.GetType().Name}: {iterEx.Message}");
                        }
                    }
                }

                // 폴백 무기 사용
                if (fallbackAttack != null)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] No preferred weapon, using fallback: {GetAbilityDisplayName(fallbackAttack)}");
                    return fallbackAttack;
                }

                // ★ v3.0.17: 무기 공격이 없으면 공격성 능력 찾기 (v2.2 포팅)
                foreach (var ability in rawAbilities)
                {
                    try
                    {
                        var abilityData = ability?.Data;
                        if (abilityData == null) continue;

                        if (IsOffensiveAbility(abilityData))
                        {
                            List<string> reasons;
                            if (IsAbilityAvailable(abilityData, out reasons))
                            {
                                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Found offensive ability as fallback: {GetAbilityDisplayName(abilityData)}");
                                return abilityData;
                            }
                        }
                    }
                    catch (Exception iterEx)
                    {
                        // ★ v3.111.14: per-ability 예외 격리
                        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] FindAnyAttackAbility offensive fallback: skip ability due to {iterEx.GetType().Name}: {iterEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] FindAnyAttackAbility error: {ex.Message}");
            }

            return null;
        }

        public static float GetAbilityAPCost(AbilityData ability)
        {
            if (ability == null) return 1f;
            try
            {
                return ability.CalculateActionPointCost();
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAbilityAPCost failed for {ability?.Name}: {ex.Message}");
                return 1f;
            }
        }

        /// <summary>
        /// ★ v3.6.14: 능력이 bonus usage 상태인지 확인
        /// 쿨다운이지만 런 앤 건 등으로 보너스 사용 가능한 경우 true
        /// </summary>
        public static bool HasBonusUsage(AbilityData ability)
        {
            if (ability == null) return false;
            try
            {
                var unavailabilityReasons = ability.GetUnavailabilityReasons();
                if (unavailabilityReasons.Count == 0) return false;

                // 쿨다운만 문제인지 확인
                bool onlyOnCooldown = unavailabilityReasons.All(r =>
                    r == AbilityData.UnavailabilityReasonType.IsOnCooldown ||
                    r == AbilityData.UnavailabilityReasonType.IsOnCooldownUntilEndOfCombat);

                // 쿨다운이지만 IsAvailable=true면 bonus usage 있음
                return onlyOnCooldown && ability.IsAvailable;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] HasBonusUsage failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.6.14: 실제 사용 시 필요한 AP 비용 (bonus usage면 0)
        /// </summary>
        public static float GetEffectiveAPCost(AbilityData ability)
        {
            if (ability == null) return 1f;
            if (HasBonusUsage(ability)) return 0f;
            return GetAbilityAPCost(ability);
        }

        /// <summary>
        /// ★ v3.5.88: 0 AP 공격이 있는지 확인
        /// Break Through → Slash 같은 보너스 능력 감지용
        /// </summary>
        public static bool HasZeroAPAttack(BaseUnitEntity unit)
        {
            if (unit == null) return false;

            try
            {
                var abilities = GetAvailableAbilities(unit);
                foreach (var ability in abilities)
                {
                    if (ability == null) continue;

                    // 공격 능력인지 확인 (무기 사용 또는 Offensive)
                    bool isAttack = ability.Weapon != null ||
                                   IsOffensiveAbility(ability);
                    if (!isAttack) continue;

                    // ★ v3.8.86: GetEffectiveAPCost 사용 - bonus usage 공격도 감지
                    float cost = GetEffectiveAPCost(ability);
                    if (cost <= 0.01f)  // 0 AP (부동소수점 오차 허용)
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Found 0 AP attack: {ability.Name} (bonus={HasBonusUsage(ability)})");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] HasZeroAPAttack error: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// ★ v3.5.88: 0 AP 공격 목록 가져오기
        /// </summary>
        public static List<AbilityData> GetZeroAPAttacks(BaseUnitEntity unit)
        {
            var result = new List<AbilityData>();
            if (unit == null) return result;

            try
            {
                var abilities = GetAvailableAbilities(unit);
                foreach (var ability in abilities)
                {
                    if (ability == null) continue;

                    // 공격 능력인지 확인
                    bool isAttack = ability.Weapon != null ||
                                   IsOffensiveAbility(ability);
                    if (!isAttack) continue;

                    // ★ v3.8.86: GetEffectiveAPCost 사용 - bonus usage 공격도 감지
                    float cost = GetEffectiveAPCost(ability);
                    if (cost <= 0.01f)
                    {
                        result.Add(ability);
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetZeroAPAttacks error: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// ★ v3.9.10: 0 AP 공격이 적에게 도달 가능한지 확인
        /// 현재 위치에서 사거리 내 적이 있거나, 이동 후 사거리 내로 진입 가능한지 확인
        /// TurnOrchestrator에서 0 AP 공격 루프 방지용
        /// </summary>
        public static bool CanAnyZeroAPAttackReachEnemy(BaseUnitEntity unit, float remainingMP)
        {
            if (unit == null) return false;

            try
            {
                var zeroAPAttacks = GetZeroAPAttacks(unit);
                if (zeroAPAttacks.Count == 0) return false;

                var enemies = GetEnemies(unit);
                if (enemies.Count == 0) return false;

                float movableTiles = remainingMP / GridCellSize;  // MP를 타일로 변환

                foreach (var attack in zeroAPAttacks)
                {
                    int rangeTiles = GetAbilityRangeInTiles(attack);

                    foreach (var enemy in enemies)
                    {
                        float distTiles = GetDistanceInTiles(unit, enemy);

                        // 현재 위치에서 사거리 내이거나, 이동하면 도달 가능
                        if (distTiles <= rangeTiles + movableTiles)
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug(
                                $"[CombatAPI] 0AP attack {attack.Name} can reach {enemy.CharacterName} " +
                                $"(dist={distTiles:F1}, range={rangeTiles}, movable={movableTiles:F1})");
                            return true;
                        }
                    }
                }

                Main.Log($"[CombatAPI] No 0AP attack can reach any enemy (MP={remainingMP:F1}, movable={movableTiles:F1} tiles)");
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CanAnyZeroAPAttackReachEnemy error: {ex.Message}");
                return true;  // 에러 시 안전하게 계속 진행 허용
            }

            return false;
        }

        /// <summary>
        /// ★ v3.0.55: 능력의 MP 코스트 계산
        /// ClearMPAfterUse가 true인 능력은 999를 반환 (전체 MP 클리어)
        /// </summary>
        public static float GetAbilityMPCost(AbilityData ability)
        {
            if (ability == null) return 0f;
            try
            {
                // ClearMPAfterUse 체크 - 이 능력 사용 후 MP가 전부 소모됨
                if (ability.ClearMPAfterUse)
                {
                    return 999f;  // 전체 MP 클리어를 의미
                }

                // 일반적인 경우: MP 코스트 없음 (대부분의 능력)
                // 일부 이동 기반 능력은 MP를 사용하지만, 현재는 ClearMPAfterUse만 고려
                return 0f;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAbilityMPCost failed for {ability?.Name}: {ex.Message}");
                return 0f;
            }
        }

        /// <summary>
        /// ★ v3.0.55: 능력이 MP를 전부 클리어하는지 확인
        /// ★ v3.8.86: BlueprintCache 우선 사용 (O(1) 조회)
        /// </summary>
        public static bool AbilityClearsMPAfterUse(AbilityData ability)
        {
            if (ability == null) return false;
            try
            {
                // ★ v3.8.86: 캐시 우선 조회
                var cached = BlueprintCache.GetOrCache(ability);
                if (cached != null) return cached.ClearMPAfterUse;
                return ability.ClearMPAfterUse;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] AbilityClearsMPAfterUse failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.8.88: 유닛의 DoNotResetMovementPointsOnAttacks 특성 고려
        /// Run&Gun 등이 활성화되면 WarhammerEndTurn.OnCast()가 MP를 실제로 안 지움
        /// </summary>
        public static bool AbilityClearsMPAfterUse(AbilityData ability, BaseUnitEntity caster)
        {
            if (!AbilityClearsMPAfterUse(ability)) return false;
            try
            {
                if (caster?.Features?.DoNotResetMovementPointsOnAttacks ?? false)
                    return false;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] AbilityClearsMPAfterUse(caster) failed: {ex.Message}");
            }
            return true;
        }

        /// <summary>
        /// ★ v3.5.34: GapCloser/Charge 능력의 MP 비용 계산
        /// 게임의 패스파인딩 API를 사용하여 실제 타일 경로 비용 계산
        /// MP 비용 = 경로 타일 수 - 1 (출발점 제외)
        /// </summary>
        public static float GetGapCloserMPCost(BaseUnitEntity unit, Vector3 targetPosition)
        {
            if (unit == null) return float.MaxValue;

            try
            {
                var agent = unit.View?.MovementAgent;
                if (agent == null)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetGapCloserMPCost: agent is null");
                    return float.MaxValue;
                }

                // 게임의 Charge 경로 계산 API 사용
                var path = PathfindingService.Instance.FindPathChargeTB_Blocking(
                    agent,
                    unit.Position,
                    targetPosition,
                    false,  // ignoreBlockers
                    null    // targetEntity
                );

                if (path == null || path.path == null || path.path.Count < 2)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetGapCloserMPCost: invalid path (count={path?.path?.Count ?? 0})");
                    return float.MaxValue;
                }

                // MP 비용 = 경로 타일 수 - 1 (출발점 제외)
                // 게임의 AbilityCustomDirectMovement.Deliver()와 동일한 계산
                float mpCost = Math.Max(0, path.path.Count - 1);
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetGapCloserMPCost: path={path.path.Count} tiles -> MP cost={mpCost}");
                return mpCost;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetGapCloserMPCost error: {ex.Message}");
                return float.MaxValue;
            }
        }

        /// <summary>
        /// ★ v3.5.34: 능력의 MP 비용 계산 (통합 API)
        /// GapCloser/Charge 능력은 실제 경로 기반, 그 외는 컴포넌트 기반
        /// </summary>
        public static float GetAbilityExpectedMPCost(AbilityData ability, BaseUnitEntity target = null)
        {
            if (ability == null) return 0f;

            try
            {
                // 1. ClearMPAfterUse 체크 - 전체 MP 소모
                if (ability.ClearMPAfterUse)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] {ability.Name}: ClearMPAfterUse -> MP cost=MAX");
                    return float.MaxValue;
                }

                // 2. WarhammerAbilityManageResources 체크 (고정 MP 비용)
                var manageResources = ability.Blueprint?.GetComponent<WarhammerAbilityManageResources>();
                if (manageResources != null)
                {
                    if (manageResources.CostsMaximumMovePoints)
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] {ability.Name}: CostsMaximumMovePoints -> MP cost=MAX");
                        return float.MaxValue;
                    }
                    if (manageResources.shouldSpendMovePoints > 0)
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] {ability.Name}: shouldSpendMovePoints={manageResources.shouldSpendMovePoints}");
                        return manageResources.shouldSpendMovePoints;
                    }
                }

                // 3. IsMoveUnit (Charge/GapCloser 등) - 패스파인딩으로 실제 비용 계산
                if (ability.Blueprint?.IsMoveUnit == true && target != null)
                {
                    var caster = ability.Caster as BaseUnitEntity;
                    if (caster != null)
                    {
                        float mpCost = GetGapCloserMPCost(caster, target.Position);
                        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] {ability.Name}: IsMoveUnit -> MP cost={mpCost:F1}");
                        return mpCost;
                    }
                }

                return 0f;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAbilityExpectedMPCost error: {ex.Message}");
                return 0f;
            }
        }

        public static bool HasActiveBuff(BaseUnitEntity unit, AbilityData ability)
        {
            if (unit == null || ability == null) return false;

            try
            {
                // ★ v3.4.01: P0-3 Blueprint null 체크
                if (ability.Blueprint == null) return false;

                // 능력의 버프 블루프린트 추출
                // ★ v3.8.62: BlueprintCache 캐시 사용 (GetComponent O(n) → O(1))
                var runAction = BlueprintCache.GetCachedRunAction(ability.Blueprint);
                if (runAction?.Actions?.Actions != null)
                {
                    foreach (var action in runAction.Actions.Actions)
                    {
                        if (action is ContextActionApplyBuff applyBuff)
                        {
                            var buffBlueprint = applyBuff.Buff;
                            if (buffBlueprint == null) continue;

                            var existingBuff = unit.Buffs.GetBuff(buffBlueprint);
                            if (existingBuff != null)
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // ★ v3.4.01: P1-2 예외 상세 로깅
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] HasActiveBuff error: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// ★ v3.7.94: 버프 남은 라운드 조회 (게임 API 활용)
        /// </summary>
        /// <param name="unit">대상 유닛</param>
        /// <param name="ability">버프 능력</param>
        /// <returns>남은 라운드 (버프 없으면 0, 영구 버프면 -1)</returns>
        public static int GetBuffRemainingRounds(BaseUnitEntity unit, AbilityData ability)
        {
            if (unit == null || ability?.Blueprint == null) return 0;

            try
            {
                // ★ v3.8.62: BlueprintCache 캐시 사용 (GetComponent O(n) → O(1))
                var runAction = BlueprintCache.GetCachedRunAction(ability.Blueprint);
                if (runAction?.Actions?.Actions != null)
                {
                    foreach (var action in runAction.Actions.Actions)
                    {
                        if (action is ContextActionApplyBuff applyBuff)
                        {
                            var buffBlueprint = applyBuff.Buff;
                            if (buffBlueprint == null) continue;

                            var existingBuff = unit.Buffs.GetBuff(buffBlueprint);
                            if (existingBuff != null)
                            {
                                // 영구 버프 (DurationInRounds == 0)
                                if (existingBuff.IsPermanent)
                                    return -1;

                                // 남은 라운드 반환
                                return existingBuff.ExpirationInRounds;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetBuffRemainingRounds error: {ex.Message}");
            }

            return 0;  // 버프 없음
        }

        /// <summary>
        /// ★ v3.7.94: 버프 갱신 필요 여부 확인
        /// 버프가 없거나 곧 만료되면 true
        /// </summary>
        /// <param name="unit">대상 유닛</param>
        /// <param name="ability">버프 능력</param>
        /// <param name="refreshThreshold">갱신 임계값 (기본 2라운드 이하면 갱신)</param>
        public static bool NeedsBuffRefresh(BaseUnitEntity unit, AbilityData ability, int refreshThreshold = 2)
        {
            int remaining = GetBuffRemainingRounds(unit, ability);

            // 영구 버프면 갱신 불필요
            if (remaining == -1)
                return false;

            // 버프 없거나 임계값 이하면 갱신 필요
            return remaining <= refreshThreshold;
        }

        /// <summary>
        /// ★ v3.7.94: 유닛의 모든 활성 버프 이름 목록 (디버그용)
        /// </summary>
        public static List<string> GetAllActiveBuffNames(BaseUnitEntity unit)
        {
            var result = new List<string>();
            if (unit?.Buffs == null) return result;

            try
            {
                foreach (var buff in unit.Buffs)
                {
                    string name = buff.Blueprint?.Name ?? buff.Name ?? "Unknown";
                    int remaining = buff.IsPermanent ? -1 : buff.ExpirationInRounds;
                    string durationStr = remaining == -1 ? "∞" : $"{remaining}R";
                    result.Add($"{name} ({durationStr})");
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAllActiveBuffNames error: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// ★ v3.7.94: 유닛이 특정 버프 카테고리를 가지고 있는지 확인
        /// </summary>
        public static bool HasBuffOfType(BaseUnitEntity unit, string buffNameContains)
        {
            if (unit?.Buffs == null || string.IsNullOrEmpty(buffNameContains)) return false;

            try
            {
                foreach (var buff in unit.Buffs)
                {
                    string name = buff.Blueprint?.Name ?? buff.Name ?? "";
                    if (name.IndexOf(buffNameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] HasBuffOfType error: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// ★ v3.32.0: 플라스마 과열 Rank 조회
        /// PlasmaOverheat_Buff (GUID: 0835dbc012334dd49f849fcc92e9f708) — Stacking: Rank
        /// 매 사격 Rank +2, 턴 시작 Rank -1, Rank 4+ = 100% 폭발 (자기+주변 AoE)
        /// </summary>
        public static int GetPlasmaOverheatRank(BaseUnitEntity unit)
        {
            if (unit?.Buffs == null) return 0;
            try
            {
                foreach (var buff in unit.Buffs)
                {
                    if (buff.Blueprint?.AssetGuid?.ToString() == "0835dbc012334dd49f849fcc92e9f708")
                        return buff.Rank;
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetPlasmaOverheatRank error: {ex.Message}");
            }
            return 0;
        }

        /// <summary>
        /// ★ v3.32.0: 능력이 플라스마 무기를 사용하는지 확인
        /// AbilityData.Weapon → BlueprintItemWeapon.Family == WeaponFamily.Plasma
        /// </summary>
        public static bool IsPlasmaWeapon(AbilityData ability)
        {
            try
            {
                return ability?.Weapon?.Blueprint.Family == WeaponFamily.Plasma;
            }
            catch { return false; }
        }

        /// <summary>
        /// ★ v3.40.0: Prey 마킹 능력 GUID 목록 (HuntDownThePrey, ChoosePrey_Noble)
        /// </summary>
        private static readonly HashSet<string> PreyAbilityGuids = new HashSet<string>
        {
            "b97c9e76f6ca46d3bb8ccd86baa9d7c9", // HuntDownThePrey (Bounty Hunter)
            "43ee13d74e824d07a0fa2a651c23df40", // ChoosePrey_Noble
        };

        /// <summary>
        /// ★ v3.40.0: 적이 Prey(먹잇감)로 마크되었는지 확인
        /// buff.Context.SourceAbility의 GUID로 역추적 — Prey 버프 GUID 불필요
        /// Piercing Shot + Prey = 보장 크리 → ScoreAttackBuff에서 가산점
        /// </summary>
        public static bool IsMarkedAsPrey(BaseUnitEntity target)
        {
            if (target?.Buffs == null) return false;
            try
            {
                foreach (var buff in target.Buffs)
                {
                    var sourceAbility = buff?.Context?.SourceAbility;
                    if (sourceAbility == null) continue;
                    var guid = sourceAbility.AssetGuid?.ToString();
                    if (guid != null && PreyAbilityGuids.Contains(guid))
                        return true;
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsMarkedAsPrey error: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// ★ v3.40.6: 타겟이 공격자의 데미지에 면역인지 확인
        /// 4가지 메커니즘 검사:
        /// 1) AddDamageTypeImmunity — 특정 데미지 타입 면역 (PctMul_Extra = 0)
        /// 2) WarhammerDamageModifier — UnmodifiablePercentDamageModifier=0 or PercentDamageModifier≤-100
        /// 3) WarhammerModifyIncomingAttackDamage — PercentDamageModifier ≤ -100
        /// 4) WarhammerIncomingDamageNullifier — NullifyChances = 0 (데미지 통과 확률 0%)
        /// 면역 타겟은 공격해도 데미지 0이므로 AI가 다른 타겟을 선택해야 함
        /// </summary>
        public static bool IsTargetImmuneToDamage(BaseUnitEntity target, BaseUnitEntity attacker)
        {
            if (target == null || attacker == null) return false;

            try
            {
                // 공격자의 주 무기 데미지 타입 조회
                var weapon = attacker.Body?.PrimaryHand?.Weapon;
                if (weapon?.Blueprint?.DamageType == null) return false;

                var attackerDmgType = weapon.Blueprint.DamageType.Type;
                bool debugEnabled = Main.IsDebugEnabled;

                foreach (var fact in target.Facts.List)
                {
                    if (fact == null) continue;

                    // 1. AddDamageTypeImmunity — 특정 데미지 타입 면역
                    foreach (var component in fact.SelectComponents<AddDamageTypeImmunity>())
                    {
                        if (component.Types.Contains(attackerDmgType))
                        {
                            if (debugEnabled)
                                Main.LogDebug($"[CombatAPI] ★ {target.CharacterName} IMMUNE via AddDamageTypeImmunity ({attackerDmgType}, fact: {fact.Name})");
                            return true;
                        }
                    }

                    // 2. WarhammerDamageModifier (WarhammerDamageModifierTarget 포함)
                    //    - UnmodifiablePercentDamageModifier = 0 → PctMul_Extra=0 = 데미지 완전 무효화
                    //    - PercentDamageModifier ≤ -100 → PctAdd -100% = 데미지 0
                    //    ★ v3.94.0: Restrictions 체크 — 조건부 면역은 판정에서 제외
                    foreach (var component in fact.SelectComponents<WarhammerDamageModifier>())
                    {
                        // 조건부 (특정 무기/공격자 타입에만 적용) → 무조건 면역 아님
                        if (!IsUnconditionalModifier(component)) continue;
                        try
                        {
                            var unmodPct = component.UnmodifiablePercentDamageModifier;
                            if (unmodPct != null && unmodPct.Enabled)
                            {
                                int unmodValue = EvaluateContextValue(unmodPct, fact);
                                if (unmodValue != int.MaxValue && unmodValue == 0)
                                {
                                    if (debugEnabled)
                                        Main.LogDebug($"[CombatAPI] ★ {target.CharacterName} IMMUNE via WarhammerDamageModifier.UnmodPctMul=0 (fact: {fact.Name})");
                                    return true;
                                }
                            }

                            var pctMod = component.PercentDamageModifier;
                            if (pctMod != null && pctMod.Enabled)
                            {
                                int pctValue = EvaluateContextValue(pctMod, fact);
                                if (pctValue != int.MaxValue && pctValue <= -100)
                                {
                                    if (debugEnabled)
                                        Main.LogDebug($"[CombatAPI] ★ {target.CharacterName} IMMUNE via WarhammerDamageModifier.PctDmgMod={pctValue} (fact: {fact.Name})");
                                    return true;
                                }
                            }
                        }
                        catch { }
                    }

                    // 3. WarhammerModifyIncomingAttackDamage — PctDmgMod ≤ -100
                    //    ★ v3.94.0: Restrictions 체크
                    foreach (var component in fact.SelectComponents<WarhammerModifyIncomingAttackDamage>())
                    {
                        if (!IsUnconditionalModifier(component)) continue;
                        try
                        {
                            var pctMod = component.PercentDamageModifier;
                            if (pctMod != null)
                            {
                                int pctValue = EvaluateContextValue(pctMod, fact);
                                if (pctValue != int.MaxValue && pctValue <= -100)
                                {
                                    if (debugEnabled)
                                        Main.LogDebug($"[CombatAPI] ★ {target.CharacterName} IMMUNE via WarhammerModifyIncomingAttackDamage (PctDmgMod={pctValue}, fact: {fact.Name})");
                                    return true;
                                }
                            }
                        }
                        catch { }
                    }

                    // 4. WarhammerIncomingDamageNullifier — DamageChance = 0% (완전 면역)
                    //    ★ v3.94.0: Restrictions 체크
                    foreach (var component in fact.SelectComponents<WarhammerIncomingDamageNullifier>())
                    {
                        if (!IsUnconditionalModifier(component)) continue;
                        try
                        {
                            var field = typeof(WarhammerIncomingDamageNullifier).GetField("m_NullifyChances",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (field != null)
                            {
                                var nullifyCV = field.GetValue(component) as Kingmaker.UnitLogic.Mechanics.ContextValue;
                                if (nullifyCV != null)
                                {
                                    int chances = EvaluateContextValue(nullifyCV, fact);
                                    if (chances != int.MaxValue)
                                    {
                                        chances = Math.Max(Math.Min(chances, 100), 0);
                                        if (chances <= 0)
                                        {
                                            if (debugEnabled)
                                                Main.LogDebug($"[CombatAPI] ★ {target.CharacterName} IMMUNE via WarhammerIncomingDamageNullifier (DmgChance=0%, fact: {fact.Name})");
                                            return true;
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }

                // 진단 로그 제거됨 — 면역 감지 확인 완료 (v3.40.6)
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsTargetImmuneToDamage error: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// ★ v3.42.0: attacker 없이 무조건적 면역만 체크 (메커니즘 2-4)
        /// 도발 타겟 선택, 위치 기반 적 탐색 등 특정 공격자의 무기 타입이 불필요한 경우 사용
        /// 메커니즘 1 (AddDamageTypeImmunity)은 무기 타입 의존이므로 생략
        /// </summary>
        public static bool IsTargetUnconditionallyImmune(BaseUnitEntity target)
        {
            if (target == null) return false;

            try
            {
                bool debugEnabled = Main.IsDebugEnabled;

                foreach (var fact in target.Facts.List)
                {
                    if (fact == null) continue;

                    // 2. WarhammerDamageModifier — 무조건적 데미지 무효화
                    //    ★ v3.94.0: Restrictions 체크 — 조건부 면역은 판정에서 제외
                    foreach (var component in fact.SelectComponents<WarhammerDamageModifier>())
                    {
                        if (!IsUnconditionalModifier(component)) continue;
                        try
                        {
                            var unmodPct = component.UnmodifiablePercentDamageModifier;
                            if (unmodPct != null && unmodPct.Enabled)
                            {
                                int unmodValue = EvaluateContextValue(unmodPct, fact);
                                if (unmodValue != int.MaxValue && unmodValue == 0)
                                {
                                    if (debugEnabled)
                                        Main.LogDebug($"[CombatAPI] ★ {target.CharacterName} UNCONDITIONALLY IMMUNE via WarhammerDamageModifier.UnmodPctMul=0 (fact: {fact.Name})");
                                    return true;
                                }
                            }

                            var pctMod = component.PercentDamageModifier;
                            if (pctMod != null && pctMod.Enabled)
                            {
                                int pctValue = EvaluateContextValue(pctMod, fact);
                                if (pctValue != int.MaxValue && pctValue <= -100)
                                {
                                    if (debugEnabled)
                                        Main.LogDebug($"[CombatAPI] ★ {target.CharacterName} UNCONDITIONALLY IMMUNE via WarhammerDamageModifier.PctDmgMod={pctValue} (fact: {fact.Name})");
                                    return true;
                                }
                            }
                        }
                        catch { }
                    }

                    // 3. WarhammerModifyIncomingAttackDamage — PctDmgMod ≤ -100
                    //    ★ v3.94.0: Restrictions 체크
                    foreach (var component in fact.SelectComponents<WarhammerModifyIncomingAttackDamage>())
                    {
                        if (!IsUnconditionalModifier(component)) continue;
                        try
                        {
                            var pctMod = component.PercentDamageModifier;
                            if (pctMod != null)
                            {
                                int pctValue = EvaluateContextValue(pctMod, fact);
                                if (pctValue != int.MaxValue && pctValue <= -100)
                                {
                                    if (debugEnabled)
                                        Main.LogDebug($"[CombatAPI] ★ {target.CharacterName} UNCONDITIONALLY IMMUNE via WarhammerModifyIncomingAttackDamage (PctDmgMod={pctValue}, fact: {fact.Name})");
                                    return true;
                                }
                            }
                        }
                        catch { }
                    }

                    // 4. WarhammerIncomingDamageNullifier — DamageChance = 0%
                    //    ★ v3.94.0: Restrictions 체크
                    foreach (var component in fact.SelectComponents<WarhammerIncomingDamageNullifier>())
                    {
                        if (!IsUnconditionalModifier(component)) continue;
                        try
                        {
                            var field = typeof(WarhammerIncomingDamageNullifier).GetField("m_NullifyChances",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (field != null)
                            {
                                var nullifyCV = field.GetValue(component) as Kingmaker.UnitLogic.Mechanics.ContextValue;
                                if (nullifyCV != null)
                                {
                                    int chances = EvaluateContextValue(nullifyCV, fact);
                                    if (chances != int.MaxValue)
                                    {
                                        chances = Math.Max(Math.Min(chances, 100), 0);
                                        if (chances <= 0)
                                        {
                                            if (debugEnabled)
                                                Main.LogDebug($"[CombatAPI] ★ {target.CharacterName} UNCONDITIONALLY IMMUNE via WarhammerIncomingDamageNullifier (DmgChance=0%, fact: {fact.Name})");
                                            return true;
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsTargetUnconditionallyImmune error: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// ContextValue를 안전하게 평가 — Simple이면 직접 읽기, 아니면 Context로 Calculate 시도
        /// 실패 시 int.MaxValue 반환
        /// </summary>
        private static int EvaluateContextValue(Kingmaker.UnitLogic.Mechanics.ContextValue cv, EntityFact fact)
        {
            if (cv == null) return int.MaxValue;
            if (cv.ValueType == Kingmaker.UnitLogic.Mechanics.ContextValueType.Simple)
                return cv.Value;
            try
            {
                var ctx = fact.MaybeContext;
                if (ctx != null) return cv.Calculate(ctx);
            }
            catch { }
            return int.MaxValue;
        }

        /// <summary>
        /// ★ v3.94.0: WarhammerDamageModifier 계열 컴포넌트가 무조건 적용되는지 확인.
        /// 게임 소스(WarhammerDamageModifier.cs:38)는 TryApply 진입 시 Restrictions.IsPassed를 체크.
        /// Restrictions.Property가 null이거나 Empty면 무조건 적용 → 진짜 면역 판정 가능.
        /// Property가 있으면 조건부 (예: "워프 생물"은 특정 무기 타입에만 감소 적용) → 면역 판정 금지.
        ///
        /// 세 컴포넌트 모두 "Restrictions" 필드 이름 공유:
        /// - WarhammerDamageModifier: public
        /// - WarhammerModifyIncomingAttackDamage: protected
        /// - WarhammerIncomingDamageNullifier: private
        /// Reflection으로 통일 접근 (base type까지 탐색).
        /// </summary>
        private static bool IsUnconditionalModifier(object component)
        {
            if (component == null) return false;
            try
            {
                // Restrictions 필드 탐색 (base type까지)
                System.Reflection.FieldInfo field = null;
                var current = component.GetType();
                while (field == null && current != null && current != typeof(object))
                {
                    field = current.GetField("Restrictions",
                        System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.DeclaredOnly);
                    current = current.BaseType;
                }

                if (field == null) return true; // Restrictions 필드 없음 → 무조건 적용

                var restrictions = field.GetValue(component)
                    as Kingmaker.Designers.Mechanics.Facts.Restrictions.RestrictionCalculator;
                if (restrictions == null) return true;

                var prop = restrictions.Property;
                // Property == null 또는 Property.Empty 이면 무조건 PASS (게임 로직과 동일)
                return prop == null || prop.Empty;
            }
            catch
            {
                // 탐색 실패 시 보수적으로 false 반환 (면역 판정 안 함 — 공격 가능으로 둠)
                return false;
            }
        }

        /// <summary>
        /// ★ v3.40.2: 유닛의 근접 공격이 적을 밀어내는지 (Push) 판별
        /// 1) 무기 Blueprint의 OnHitActions에 ContextActionPush 포함
        /// 2) 유닛 버프에 ForceMoveTriggerInitiator 컴포넌트 보유 (공격 시 밀어내기 발동)
        /// </summary>
        public static bool CanMeleeAttackCausePush(BaseUnitEntity unit)
        {
            if (unit == null) return false;

            try
            {
                // 1. 무기의 OnHitActions에서 ContextActionPush 검사
                var weapon = unit.Body?.PrimaryHand?.Weapon;
                if (weapon?.Blueprint != null)
                {
                    var onHitEffect = weapon.Blueprint.OnHitActions;
                    var actionList = onHitEffect?.OnHitActions;
                    if (actionList?.Actions != null)
                    {
                        foreach (var action in actionList.Actions)
                        {
                            if (action is ContextActionPush)
                                return true;
                        }
                    }
                }

                // 2. 유닛 버프에 ForceMoveTriggerInitiator 검사 (공격 시 밀어내기 트리거)
                if (unit.Facts.HasComponent<ForceMoveTriggerInitiator>(null))
                    return true;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CanMeleeAttackCausePush error: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// ★ v3.8.39: 유닛이 잠재력 초월(WarhammerFreeUltimateBuff)을 가지고 있는지 확인
        /// 이 버프가 있으면 궁극기 사용이 가능한 추가 턴
        /// </summary>
        public static bool HasFreeUltimateBuff(BaseUnitEntity unit)
        {
            if (unit == null) return false;

            try
            {
                return unit.Facts.HasComponent<WarhammerFreeUltimateBuff>(null);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// ★ v3.9.88: 유닛이 무기 전환 시 보너스 공격을 받는지 확인
        /// WeaponSetChangedTrigger가 있으면 무기 전환 시 ActionList 실행
        /// → ContextActionAddBonusAbilityUsage로 보너스 공격 부여 (Versatility 등)
        ///
        /// 게임 메커니즘: PrimaryHandAbilityGroup 공유 쿨다운
        /// - 무기 공격 사용 → 해당 그룹 전체 쿨다운 (같은 슬롯의 모든 무기)
        /// - 무기 세트 전환만으로는 쿨다운 우회 불가
        /// - WeaponSetChangedTrigger → ContextActionAddBonusAbilityUsage → IsBonusUsage=true → 쿨다운 우회
        /// </summary>
        public static bool HasWeaponSwitchBonusAttack(BaseUnitEntity unit)
        {
            if (unit == null) return false;

            try
            {
                return unit.Facts.HasComponent<WeaponSetChangedTrigger>(null);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// ★ v3.8.39: 능력이 궁극기(HeroicAct 또는 DesperateMeasure)인지 확인
        /// </summary>
        public static bool IsUltimateAbility(AbilityData ability)
        {
            if (ability?.Blueprint == null) return false;
            return ability.Blueprint.IsHeroicAct || ability.Blueprint.IsDesperateMeasure;
        }

        /// <summary>
        /// ★ v3.8.41: 궁극기 타겟 유형 분류 (실제 능력 데이터 기반)
        ///
        /// 실제 궁극기 분석 결과:
        /// - SelfBuff(Personal): Steady Superiority, Carnival of Misery, Overcharge,
        ///   Firearm Mastery, Unyielding Guard, Daring Breach
        /// - ImmediateAttack(적 타겟): Dispatch, Death Waltz, Wild Hunt, Dismantling Attack
        /// - AllyBuff(아군 타겟): Finest Hour!
        /// - AreaEffect(지점 타겟): Take and Hold, Orchestrated Firestorm
        /// </summary>
        public enum UltimateTargetType
        {
            Unknown,
            SelfBuff,         // Personal 타겟: 자기 강화/자원회복/방어오라 (대부분의 궁극기)
            ImmediateAttack,  // 적 타겟: 즉시 공격 (Dispatch, Death Waltz, Wild Hunt 등)
            AllyBuff,         // 아군 타겟: 아군 지원 (Finest Hour!)
            AreaEffect         // 지점 타겟: 구역 효과 (Take and Hold, Orchestrated Firestorm)
        }

        /// <summary>
        /// ★ v3.8.41: 궁극기 타겟 유형 판별 (블루프린트 플래그 기반)
        /// </summary>
        public static UltimateTargetType ClassifyUltimateTarget(AbilityData ability)
        {
            if (ability?.Blueprint == null) return UltimateTargetType.Unknown;

            var bp = ability.Blueprint;

            // 1. 적 타겟 = 즉시 공격 (Dispatch, Death Waltz, Wild Hunt, Dismantling Attack)
            if (bp.CanTargetEnemies)
                return UltimateTargetType.ImmediateAttack;

            // 2. 지점 타겟 = 구역 효과 (Take and Hold, Orchestrated Firestorm)
            if (bp.CanTargetPoint && !bp.CanTargetSelf)
                return UltimateTargetType.AreaEffect;

            // 3. 아군 타겟 (자기 제외) = 아군 버프 (Finest Hour!)
            if (bp.CanTargetFriends && !bp.CanTargetSelf)
                return UltimateTargetType.AllyBuff;

            // 4. Self 타겟 = 자기 강화 (대부분의 Personal 궁극기)
            //    Steady Superiority, Carnival, Overcharge, Firearm Mastery,
            //    Unyielding Guard, Daring Breach 등
            if (bp.CanTargetSelf)
                return UltimateTargetType.SelfBuff;

            return UltimateTargetType.Unknown;
        }

        /// <summary>
        /// ★ v3.8.41: 궁극기 상세 정보 구조체
        /// </summary>
        public struct UltimateInfo
        {
            public UltimateTargetType TargetType;
            public bool IsHeroicAct;
            public bool IsDesperateMeasure;
            public bool IsAoE;
            public float AoERadius;
            public bool CanTargetSelf;
            public bool CanTargetFriends;
            public bool CanTargetEnemies;
            public bool CanTargetPoint;
            public bool NotOffensive;
            public string EffectOnAlly;
            public string EffectOnEnemy;
        }

        /// <summary>
        /// ★ v3.8.41: 궁극기 상세 정보 조회
        /// </summary>
        public static UltimateInfo GetUltimateInfo(AbilityData ability)
        {
            var info = new UltimateInfo { TargetType = UltimateTargetType.Unknown };
            if (ability?.Blueprint == null) return info;

            var bp = ability.Blueprint;

            info.TargetType = ClassifyUltimateTarget(ability);
            info.IsHeroicAct = bp.IsHeroicAct;
            info.IsDesperateMeasure = bp.IsDesperateMeasure;
            info.IsAoE = bp.IsAoE || bp.IsAoEDamage;
            info.AoERadius = GetAoERadius(ability);
            info.CanTargetSelf = bp.CanTargetSelf;
            info.CanTargetFriends = bp.CanTargetFriends;
            info.CanTargetEnemies = bp.CanTargetEnemies;
            info.CanTargetPoint = bp.CanTargetPoint;
            info.NotOffensive = bp.NotOffensive;
            info.EffectOnAlly = bp.EffectOnAlly.ToString();
            info.EffectOnEnemy = bp.EffectOnEnemy.ToString();

            return info;
        }

        #endregion

        #region Veil & Psychic - v3.6.0 Enhanced

        // ★ v3.6.0: Veil Degradation 상수
        public const int VEIL_WARNING_THRESHOLD = 10;   // 주의 시작
        public const int VEIL_DANGER_THRESHOLD = 15;    // 위험 (Major 차단)
        public const int VEIL_MAXIMUM = 20;             // 최대치

        /// <summary>
        /// 사이킥 안전 등급
        /// </summary>
        public enum PsychicSafetyLevel
        {
            Safe,       // 안전하게 사용 가능
            Caution,    // 주의 필요 (Veil 10~14)
            Dangerous,  // 위험 (예상 Veil이 15+ 도달)
            Blocked     // 차단 (현재 Veil이 이미 위험 수준)
        }

        /// <summary>
        /// 현재 Veil Thickness 값
        /// </summary>
        public static int GetVeilThickness()
        {
            try
            {
                return Game.Instance?.TurnController?.VeilThicknessCounter?.Value ?? 0;
            }
            // ★ v3.13.0: 안전한 기본값 — VEIL_DANGER_THRESHOLD (위험 가정 → 사이킥 차단)
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetVeilThickness failed: {ex.Message}");
                return VEIL_DANGER_THRESHOLD;
            }
        }

        /// <summary>
        /// 능력이 사이킥 파워인지 확인
        /// </summary>
        public static bool IsPsychicAbility(AbilityData ability)
        {
            if (ability == null) return false;
            try
            {
                return ability.Blueprint?.AbilityParamsSource == WarhammerAbilityParamsSource.PsychicPower;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsPsychicAbility failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.6.0: Major 사이킥 (Veil +3 이상) 여부
        /// Major: Perils of Warp 발생 가능, 더 강력한 효과
        /// </summary>
        public static bool IsMajorPsychicAbility(AbilityData ability)
        {
            if (ability == null) return false;
            try
            {
                var bp = ability.Blueprint;
                return bp != null && IsPsychicAbility(ability) && bp.VeilThicknessPointsToAdd >= 3;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsMajorPsychicAbility failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.6.0: Minor 사이킥 (Veil +1~2) 여부
        /// </summary>
        public static bool IsMinorPsychicAbility(AbilityData ability)
        {
            if (ability == null) return false;
            try
            {
                var bp = ability.Blueprint;
                if (bp == null || !IsPsychicAbility(ability)) return false;
                int veilAdd = bp.VeilThicknessPointsToAdd;
                return veilAdd > 0 && veilAdd < 3;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsMinorPsychicAbility failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.6.0: 사이킥 사용 시 Veil 증가량 (음수 = Veil 감소 스킬)
        /// </summary>
        public static int GetVeilIncrease(AbilityData ability)
        {
            if (ability == null || !IsPsychicAbility(ability)) return 0;
            try
            {
                return ability.Blueprint?.VeilThicknessPointsToAdd ?? 0;
            }
            // ★ v3.13.0: 안전한 기본값 — 3 (Major psychic 수준 → 사용 억제)
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetVeilIncrease failed for {ability?.Name}: {ex.Message}");
                return 3;
            }
        }

        /// <summary>
        /// ★ v3.6.0: Veil을 낮추는 스킬인지 확인
        /// </summary>
        public static bool IsVeilReducingAbility(AbilityData ability)
        {
            return GetVeilIncrease(ability) < 0;
        }

        /// <summary>
        /// ★ v3.6.0: 사이킥 안전 등급 평가
        ///
        /// 로직:
        /// - Veil 감소 스킬: 항상 Safe (위험 상황에서 오히려 도움)
        /// - Major (Veil +3): 현재 Veil >= 15면 Blocked, 예상 >= 15면 Dangerous
        /// - Minor (Veil +1~2): 예상 Veil >= 20이면 Dangerous
        /// - Veil >= 10: Caution (경고만)
        /// </summary>
        public static PsychicSafetyLevel EvaluatePsychicSafety(AbilityData ability)
        {
            if (!IsPsychicAbility(ability))
                return PsychicSafetyLevel.Safe;

            int currentVeil = GetVeilThickness();
            int veilIncrease = GetVeilIncrease(ability);

            // Veil 감소 스킬은 항상 허용 (위험할수록 필요!)
            if (veilIncrease < 0)
                return PsychicSafetyLevel.Safe;

            int projectedVeil = currentVeil + veilIncrease;

            // Major 사이킥 (Veil +3+)
            if (IsMajorPsychicAbility(ability))
            {
                // 이미 위험 수준이면 차단 (Perils of Warp 위험)
                if (currentVeil >= VEIL_DANGER_THRESHOLD)
                    return PsychicSafetyLevel.Blocked;

                // 사용 후 위험 수준 도달 예상
                if (projectedVeil >= VEIL_DANGER_THRESHOLD)
                    return PsychicSafetyLevel.Dangerous;

                // 주의 수준
                if (currentVeil >= VEIL_WARNING_THRESHOLD)
                    return PsychicSafetyLevel.Caution;
            }
            // Minor 사이킥 (Veil +1~2)
            else
            {
                // 최대치 초과 예상시만 위험
                if (projectedVeil >= VEIL_MAXIMUM)
                    return PsychicSafetyLevel.Dangerous;

                // 위험 수준 도달 시 주의
                if (projectedVeil >= VEIL_DANGER_THRESHOLD)
                    return PsychicSafetyLevel.Caution;
            }

            return PsychicSafetyLevel.Safe;
        }

        /// <summary>
        /// ★ v3.6.0: 사이킥 사용 가능 여부 (Blocked만 차단)
        /// Caution/Dangerous는 허용하되 로그 출력
        /// </summary>
        public static bool IsPsychicSafeToUse(AbilityData ability)
        {
            if (!IsPsychicAbility(ability)) return true;

            var safety = EvaluatePsychicSafety(ability);
            int veil = GetVeilThickness();
            int veilAdd = GetVeilIncrease(ability);

            switch (safety)
            {
                case PsychicSafetyLevel.Blocked:
                    if (Main.IsDebugEnabled) Main.LogDebug($"[Veil] BLOCKED: {ability.Name} (Veil={veil}, +{veilAdd})");
                    return false;

                case PsychicSafetyLevel.Dangerous:
                    if (Main.IsDebugEnabled) Main.LogDebug($"[Veil] DANGEROUS but allowed: {ability.Name} (Veil={veil}→{veil + veilAdd})");
                    return true;  // 위험하지만 허용 (Minor는 사용 가능하도록)

                case PsychicSafetyLevel.Caution:
                    if (Main.IsDebugEnabled) Main.LogDebug($"[Veil] Caution: {ability.Name} (Veil={veil})");
                    return true;

                default:
                    return true;
            }
        }

        /// <summary>
        /// ★ v3.6.0: Veil 상태 문자열 (로그/디버그용)
        /// </summary>
        public static string GetVeilStatusString()
        {
            int veil = GetVeilThickness();
            string status = veil >= VEIL_DANGER_THRESHOLD ? "DANGER" :
                           veil >= VEIL_WARNING_THRESHOLD ? "WARNING" : "SAFE";
            return $"Veil={veil}/{VEIL_MAXIMUM} ({status})";
        }

        #endregion

        #region Target Scoring System

        /// <summary>
        /// 타겟 점수 정보
        /// ★ v3.0.1: 실제 데미지/HP 기반 정보 추가
        /// </summary>
        public class TargetScore
        {
            public BaseUnitEntity Target { get; set; }
            public float Score { get; set; }
            public string Reason { get; set; }
            public bool IsHittable { get; set; }
            public float Distance { get; set; }
            public float HPPercent { get; set; }
            // ★ v3.0.1: 실제 데미지 정보
            public int ActualHP { get; set; }
            public int PredictedMinDamage { get; set; }
            public int PredictedMaxDamage { get; set; }
            public bool CanKillInOneHit { get; set; }
            public bool CanKillInTwoHits { get; set; }
        }

        #region Accurate Damage Prediction (v3.0.1)

        /// <summary>
        /// ★ v3.0.1: 유닛의 실제 현재 HP 반환
        /// </summary>
        public static int GetActualHP(BaseUnitEntity unit)
        {
            if (unit == null) return 0;
            try
            {
                return unit.Health?.HitPointsLeft ?? 0;
            }
            // ★ v3.13.0: 안전한 기본값 — 1 (0은 "사망"으로 오판될 위험, 1은 "빈사")
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetActualHP failed for {unit?.CharacterName}: {ex.Message}");
                return 1;
            }
        }

        /// <summary>
        /// ★ v3.0.1: 유닛의 최대 HP 반환
        /// </summary>
        public static int GetActualMaxHP(BaseUnitEntity unit)
        {
            if (unit == null) return 0;
            try
            {
                return unit.Health?.MaxHitPoints ?? 0;
            }
            // ★ v3.13.0: 안전한 기본값 — 1 (0으로 나눔 방지, HP% 계산 안전)
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetActualMaxHP failed for {unit?.CharacterName}: {ex.Message}");
                return 1;
            }
        }

        /// <summary>
        /// ★ v3.8.49: 적 난도 등급 조회
        /// 게임 BlueprintUnit.DifficultyType (Swarm~ChapterBoss 7단계)
        /// </summary>
        public static UnitDifficultyType GetDifficultyType(BaseUnitEntity unit)
        {
            if (unit == null) return UnitDifficultyType.Common;
            try
            {
                return unit.Blueprint.DifficultyType;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetDifficultyType failed for {unit?.CharacterName}: {ex.Message}");
                return UnitDifficultyType.Common;
            }
        }

        /// <summary>
        /// ★ v3.0.1: 게임 API를 사용한 정확한 데미지 예측
        /// ability.GetDamagePrediction(target, casterPosition, context) 사용
        /// </summary>
        public static (int MinDamage, int MaxDamage, int Penetration) GetDamagePrediction(
            AbilityData ability,
            BaseUnitEntity target)
        {
            if (ability == null || target == null)
                return (0, 0, 0);

            try
            {
                var caster = ability.Caster as BaseUnitEntity;
                if (caster == null) return (0, 0, 0);

                // ★ 게임 API: AbilityDataHelper.GetDamagePrediction()
                var prediction = ability.GetDamagePrediction(target, caster.Position, null);
                if (prediction == null) return (0, 0, 0);

                return (prediction.MinDamage, prediction.MaxDamage, prediction.Penetration);
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetDamagePrediction error: {ex.Message}");
                return (0, 0, 0);
            }
        }

        /// <summary>
        /// ★ v3.0.1: 1타에 킬 가능 여부 (MinDamage >= CurrentHP)
        /// </summary>
        public static bool CanKillInOneHit(AbilityData ability, BaseUnitEntity target)
        {
            if (ability == null || target == null) return false;

            try
            {
                int hp = GetActualHP(target);
                if (hp <= 0) return false;

                var (minDamage, maxDamage, _) = GetDamagePrediction(ability, target);

                // 최소 데미지로도 킬 가능
                return minDamage >= hp;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CanKillInOneHit failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.0.1: 2타에 킬 가능 여부 (MaxDamage * 2 >= CurrentHP)
        /// </summary>
        public static bool CanKillInTwoHits(AbilityData ability, BaseUnitEntity target)
        {
            if (ability == null || target == null) return false;

            try
            {
                int hp = GetActualHP(target);
                if (hp <= 0) return false;

                var (minDamage, maxDamage, _) = GetDamagePrediction(ability, target);

                // 최대 데미지 2번으로 킬 가능
                return maxDamage * 2 >= hp;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CanKillInTwoHits failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.0.1: 예상 킬 확률 계산 (0.0 ~ 1.0)
        /// - 1.0 = 확실한 1타 킬 (MinDamage >= HP)
        /// - 0.5+ = 높은 확률의 1타 킬 (MaxDamage >= HP)
        /// - 낮음 = 여러 타 필요
        /// </summary>
        public static float CalculateKillProbability(AbilityData ability, BaseUnitEntity target)
        {
            if (ability == null || target == null) return 0f;

            try
            {
                int hp = GetActualHP(target);
                if (hp <= 0) return 1f;

                var (minDamage, maxDamage, _) = GetDamagePrediction(ability, target);
                if (maxDamage <= 0) return 0f;

                // 최소 데미지로도 킬 가능 → 100%
                if (minDamage >= hp) return 1.0f;

                // 최대 데미지로 킬 가능 → 확률 계산 (데미지 분포가 균일하다고 가정)
                if (maxDamage >= hp)
                {
                    // (maxDamage - hp) / (maxDamage - minDamage)
                    float range = maxDamage - minDamage;
                    if (range <= 0) return 0.5f;
                    return (float)(maxDamage - hp) / range;
                }

                // 2타 킬 가능성
                if (maxDamage * 2 >= hp)
                {
                    return 0.25f;  // 2타 필요
                }

                // 3타 이상 필요
                return 0.1f;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CalculateKillProbability failed: {ex.Message}");
                return 0f;
            }
        }

        /// <summary>
        /// ★ v3.0.44: 예상 평균 데미지 계산
        /// </summary>
        public static float EstimateDamage(AbilityData ability, BaseUnitEntity target)
        {
            if (ability == null || target == null) return 0f;

            try
            {
                var (minDamage, maxDamage, _) = GetDamagePrediction(ability, target);
                return (minDamage + maxDamage) / 2f;
            }
            catch
            {
                // 폴백: 레벨 기반 추정
                return Settings.SC.FallbackEstimateDamage;
            }
        }

        #endregion

        /// <summary>
        /// 모든 적에 대해 타겟 점수 계산 - SituationAnalyzer에서 사용
        /// ★ v3.0.1: 실제 데미지 예측 기반 스코어링
        /// </summary>
        public static List<TargetScore> ScoreAllTargets(
            BaseUnitEntity unit,
            List<BaseUnitEntity> enemies,
            AbilityData attackAbility,
            RangePreference preference)
        {
            var scores = new List<TargetScore>();
            if (unit == null || enemies == null) return scores;

            foreach (var enemy in enemies)
            {
                if (enemy == null || enemy.LifeState.IsDead) continue;

                var score = new TargetScore
                {
                    Target = enemy,
                    Distance = GetDistance(unit, enemy),
                    HPPercent = GetHPPercent(enemy),
                    ActualHP = GetActualHP(enemy),
                    IsHittable = false,
                    Score = 0f,
                    Reason = ""
                };

                // 공격 가능 여부
                if (attackAbility != null)
                {
                    var target = new TargetWrapper(enemy);
                    string reason;
                    score.IsHittable = CanUseAbilityOn(attackAbility, target, out reason);
                    if (!score.IsHittable)
                    {
                        score.Reason = reason;
                        // ★ v3.0.14: Hittable=false 원인 로깅
                        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Not hittable: {enemy.CharacterName} - {reason} (dist={score.Distance:F1}m, ability={attackAbility.Name})");
                    }

                    // ★ v3.0.1: 실제 데미지 예측
                    var (minDmg, maxDmg, _) = GetDamagePrediction(attackAbility, enemy);
                    score.PredictedMinDamage = minDmg;
                    score.PredictedMaxDamage = maxDmg;
                    score.CanKillInOneHit = minDmg >= score.ActualHP && score.ActualHP > 0;
                    score.CanKillInTwoHits = maxDmg * 2 >= score.ActualHP && score.ActualHP > 0;
                }

                // ★ v3.0.1: 실제 데미지 기반 점수 계산
                score.Score = CalculateTargetScore(unit, enemy, attackAbility, score.IsHittable, preference, score);

                scores.Add(score);
            }

            return scores.OrderByDescending(s => s.Score).ToList();
        }

        /// <summary>
        /// 최적 타겟 찾기
        /// </summary>
        public static BaseUnitEntity FindBestTarget(
            BaseUnitEntity unit,
            List<BaseUnitEntity> enemies,
            AbilityData attackAbility,
            RangePreference preference)
        {
            var scores = ScoreAllTargets(unit, enemies, attackAbility, preference);

            // 공격 가능한 타겟 중 최고 점수
            var hittable = scores.FirstOrDefault(s => s.IsHittable);
            if (hittable != null)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] Best target: {hittable.Target.CharacterName} (score={hittable.Score:F1})");
                return hittable.Target;
            }

            // 공격 불가 시 가장 가까운 적
            var nearest = scores.OrderBy(s => s.Distance).FirstOrDefault();
            return nearest?.Target;
        }

        /// <summary>
        /// ★ v3.0.1: 실제 데미지 기반 타겟 점수 계산
        /// - 1타 킬 가능: +50 보너스
        /// - 2타 킬 가능: +25 보너스
        /// - HP가 낮을수록: +점수 (1/HP 기반, 게임 AI와 동일)
        /// - 거리: 근접/원거리 선호도에 따라 보너스
        /// </summary>
        private static float CalculateTargetScore(
            BaseUnitEntity caster,
            BaseUnitEntity target,
            AbilityData attackAbility,
            bool isHittable,
            RangePreference preference,
            TargetScore scoreData = null)
        {
            float score = 0f;

            // 기본 점수: 공격 가능 여부
            if (isHittable) score += 100f;

            // ★ v3.0.1: 1타 킬 가능 최우선 (+50)
            if (scoreData != null && scoreData.CanKillInOneHit && isHittable)
            {
                score += 50f;
                if (Main.IsDebugEnabled) Main.LogDebug($"[Scoring] {target.CharacterName}: +50 (1-hit kill possible, HP={scoreData.ActualHP}, MinDmg={scoreData.PredictedMinDamage})");
            }
            // 2타 킬 가능 (+25)
            else if (scoreData != null && scoreData.CanKillInTwoHits && isHittable)
            {
                score += 25f;
                if (Main.IsDebugEnabled) Main.LogDebug($"[Scoring] {target.CharacterName}: +25 (2-hit kill possible)");
            }

            // ★ v3.0.1: HP 점수 - 게임 AI와 동일한 방식 (1/HP)
            // 낮은 HP = 높은 점수 (최대 +30)
            int actualHP = scoreData?.ActualHP ?? GetActualHP(target);
            if (actualHP > 0)
            {
                // 1000 / HP 로 정규화 (HP 100 → +10, HP 50 → +20, HP 30 → +33)
                float hpScore = Math.Min(30f, 1000f / actualHP);
                score += hpScore;
            }
            else
            {
                // 폴백: HP% 기반
                float hpPercent = GetHPPercent(target);
                score += (100f - hpPercent) * 0.3f;  // 최대 +30
            }

            // 거리 점수: 가까울수록 높은 점수
            float distance = GetDistance(caster, target);
            if (distance < 30f)
            {
                score += (30f - distance) * 0.3f;  // 최대 +9
            }

            // RangePreference 보너스
            if (preference == RangePreference.PreferMelee && distance <= 3f)
            {
                score += 15f;  // 근접 범위 내
            }
            else if (preference == RangePreference.PreferRanged && distance >= 5f && distance <= 15f)
            {
                score += 12f;  // 최적 원거리
            }

            // ★ v3.0.1: 킬 확률 보너스 (데미지 예측 기반)
            if (attackAbility != null && isHittable)
            {
                float killProb = CalculateKillProbability(attackAbility, target);
                score += killProb * 20f;  // 최대 +20 (100% 킬 확률)
            }

            return score;
        }

        /// <summary>
        /// Legacy 호환: 이전 시그니처 유지
        /// </summary>
        private static float CalculateTargetScore(
            BaseUnitEntity caster,
            BaseUnitEntity target,
            bool isHittable,
            RangePreference preference)
        {
            return CalculateTargetScore(caster, target, null, isHittable, preference, null);
        }

        /// <summary>
        /// 타겟이 실제로 공격 가능한지 확인 (Hittable check)
        /// </summary>
        public static bool CheckIfHittable(BaseUnitEntity unit, BaseUnitEntity target, AbilityData attackAbility)
        {
            if (unit == null || target == null) return false;

            if (attackAbility != null)
            {
                var targetWrapper = new TargetWrapper(target);
                string reason;
                return CanUseAbilityOn(attackAbility, targetWrapper, out reason);
            }

            // 능력 없으면 거리로만 추정
            float dist = GetDistance(unit, target);
            return dist <= 15f;
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

        #region Ability Filtering (Timing-Aware)

        /// <summary>
        /// 선제적 버프만 필터링 (전투 시작/첫 행동 전)
        /// </summary>
        public static List<AbilityData> FilterProactiveBuffs(List<AbilityData> abilities, BaseUnitEntity unit)
        {
            if (abilities == null) return new List<AbilityData>();

            return abilities.Where(a => {
                var timing = AbilityDatabase.GetTiming(a);
                bool isProactive = timing == AbilityTiming.PreCombatBuff || timing == AbilityTiming.PreAttackBuff;

                // 이미 활성화된 버프 제외
                if (isProactive && HasActiveBuff(unit, a))
                    return false;

                return isProactive;
            }).ToList();
        }

        /// <summary>
        /// PostFirstAction 능력만 필터링 (첫 행동 후)
        /// </summary>
        public static List<AbilityData> FilterPostFirstActionAbilities(List<AbilityData> abilities)
        {
            if (abilities == null) return new List<AbilityData>();

            return abilities.Where(a => AbilityDatabase.IsPostFirstAction(a)).ToList();
        }

        /// <summary>
        /// 턴 종료 능력만 필터링
        /// </summary>
        public static List<AbilityData> FilterTurnEndingAbilities(List<AbilityData> abilities)
        {
            if (abilities == null) return new List<AbilityData>();

            return abilities.Where(a => AbilityDatabase.IsTurnEnding(a)).ToList();
        }

        /// <summary>
        /// 마무리 능력만 필터링
        /// </summary>
        public static List<AbilityData> FilterFinisherAbilities(List<AbilityData> abilities)
        {
            if (abilities == null) return new List<AbilityData>();

            return abilities.Where(a => AbilityDatabase.IsFinisher(a)).ToList();
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

        #region AOE Support (v3.1.16)

        /// <summary>
        /// ★ v3.1.16: AOE 패턴 설정 조회
        /// </summary>
        public static Kingmaker.UnitLogic.Abilities.Components.Base.IAbilityAoEPatternProvider GetPatternSettings(AbilityData ability)
        {
            try
            {
                return ability?.GetPatternSettings();
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetPatternSettings failed for {ability?.Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ★ v3.1.16: AOE 반경 조회 (타일 단위)
        /// </summary>
        public static float GetAoERadius(AbilityData ability)
        {
            try
            {
                var pattern = ability?.GetPatternSettings()?.Pattern;
                if (pattern != null)
                    return pattern.Radius;

                return ability?.Blueprint?.AoERadius ?? 0f;
            }
            // ★ v3.13.0: 로깅 추가 (기본값 0f는 이미 보수적 — AoE 무시)
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetAoERadius failed for {ability?.Name}: {ex.Message}");
                return 0f;
            }
        }

        /// <summary>
        /// ★ v3.1.16: AOE 패턴 타입 조회
        /// </summary>
        public static Kingmaker.Blueprints.PatternType? GetPatternType(AbilityData ability)
        {
            try
            {
                return ability?.GetPatternSettings()?.Pattern?.Type;
            }
            // ★ v3.13.0: 로깅 추가 (기본값 null은 이미 보수적 — 패턴 불명)
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetPatternType failed for {ability?.Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ★ v3.1.16: AOE 대상 타입 조회 (Enemy/Ally/Any)
        /// </summary>
        public static Kingmaker.UnitLogic.Abilities.Components.TargetType GetAoETargetType(AbilityData ability)
        {
            try
            {
                return ability?.GetPatternSettings()?.Targets ?? Kingmaker.UnitLogic.Abilities.Components.TargetType.Enemy;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAoETargetType failed for {ability?.Name}: {ex.Message}");
                return Kingmaker.UnitLogic.Abilities.Components.TargetType.Enemy;
            }
        }

        /// <summary>
        /// ★ v3.5.74: Point 타겟 능력인지 확인 (게임 API 우선)
        /// 게임 네이티브 IsAOE 먼저 체크 + 기존 로직 폴백
        /// </summary>
        public static bool IsPointTargetAbility(AbilityData ability)
        {
            try
            {
                if (ability == null) return false;

                // ★ v3.5.74: 게임 네이티브 IsAOE 먼저 체크
                if (ability.IsAOE) return true;

                var bp = ability.Blueprint;
                if (bp == null || !bp.CanTargetPoint) return false;

                // 패턴 설정에서 실제 반경 확인
                var pattern = ability.GetPatternSettings()?.Pattern;
                if (pattern != null)
                    return pattern.Radius > 0;

                // Blueprint AOE 반경 폴백
                return bp.AoERadius > 0;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsPointTargetAbility failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.1.16: Point 타겟에 능력 사용 가능 검증
        /// </summary>
        public static bool CanUseAbilityOnPoint(AbilityData ability, Vector3 point, out string reason)
        {
            reason = null;
            if (ability == null) { reason = "Null ability"; return false; }

            try
            {
                var target = new TargetWrapper(point);
                AbilityData.UnavailabilityReasonType? unavailable;
                bool canTarget = ability.CanTarget(target, out unavailable);

                if (!canTarget && unavailable.HasValue)
                    reason = unavailable.Value.ToString();

                return canTarget;
            }
            catch (Exception ex)
            {
                reason = $"Exception: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// ★ v3.1.19: AOE 패턴 각도 조회 (Cone/Sector용, 단위: degree)
        /// Reflection 제거 - pattern.Angle 프로퍼티 직접 사용
        /// </summary>
        public static float GetPatternAngle(AbilityData ability)
        {
            try
            {
                var pattern = ability?.GetPatternSettings()?.Pattern;
                if (pattern == null) return 90f;

                // ★ v3.1.19: 게임 API 직접 사용 (AoEPattern.Angle 프로퍼티)
                // Reflection 대신 public 프로퍼티 사용 - 이미 full-angle
                return pattern.Angle;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetPatternAngle error: {ex.Message}");
                return 90f;
            }
        }

        /// <summary>
        /// ★ v3.1.18: 패턴이 방향성 패턴인지 확인 (Cone/Ray/Sector)
        /// ★ v3.8.09: 이 함수는 PatternType만 체크 - CanBeDirectional과 동일
        /// 실제 IsDirectional 판정은 GetActualIsDirectional() 사용!
        /// </summary>
        public static bool IsDirectionalPattern(Kingmaker.Blueprints.PatternType? patternType)
        {
            if (!patternType.HasValue) return false;

            switch (patternType.Value)
            {
                case Kingmaker.Blueprints.PatternType.Cone:
                case Kingmaker.Blueprints.PatternType.Ray:
                case Kingmaker.Blueprints.PatternType.Sector:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// ★ v3.8.09: 게임의 실제 IsDirectional 로직 구현
        /// - Non-Custom 패턴: AbilityAoEPatternSettings.m_Directional 필드
        /// - Custom 패턴: AoEPattern.IsDirectional → BlueprintAttackPattern.IsDirectional
        /// </summary>
        public static bool GetActualIsDirectional(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                var patternSettings = ability.GetPatternSettings();
                if (patternSettings == null) return false;

                var pattern = patternSettings.Pattern;
                if (pattern == null) return false;

                // Custom 패턴: AoEPattern.IsDirectional 프로퍼티 직접 사용
                if (pattern.IsCustom)
                {
                    try
                    {
                        return pattern.IsDirectional;  // BlueprintAttackPattern.IsDirectional
                    }
                    catch (Exception ex)
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetActualIsDirectional(custom) failed for {ability?.Name}: {ex.Message}");
                        return false;
                    }
                }

                // Non-Custom 패턴: m_Directional 필드 (Reflection)
                if (!pattern.CanBeDirectional) return false;  // Ray/Cone/Sector만 가능

                // AbilityAoEPatternSettings에서 m_Directional 필드 가져오기
                var settingsType = patternSettings.GetType();
                var directionalField = settingsType.GetField("m_Directional",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (directionalField != null)
                {
                    bool result = (bool)directionalField.GetValue(patternSettings);
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] {ability.Name}: m_Directional field = {result}");
                    return result;
                }

                // 필드를 찾지 못하면 타입 기반 폴백 (CanBeDirectional이면 true 가정)
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] {ability.Name}: m_Directional field not found, using CanBeDirectional fallback");
                return pattern.CanBeDirectional;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetActualIsDirectional error for {ability?.Name}: {ex.Message}");
                return IsDirectionalPattern(GetPatternType(ability));  // 폴백
            }
        }

        /// <summary>
        /// ★ v3.8.09: AbilityCustomRam 컴포넌트 사용 여부 (Slash 공격 등)
        /// AbilityCustomRam은 Pattern이 null이지만 동적으로 Ray 패턴 생성
        /// </summary>
        public static bool IsRamAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                var bp = ability.Blueprint;
                if (bp == null) return false;

                // AbilityCustomRam 컴포넌트 체크
                return bp.GetComponent<Kingmaker.UnitLogic.Abilities.Components.AbilityCustomRam>() != null;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsRamAbility failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.8.09: Ram 능력의 관통 여부 (m_RamThrough)
        /// true면 경로의 모든 적 타격, false면 첫 적에서 멈춤
        /// </summary>
        public static bool IsRamThroughAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                var bp = ability.Blueprint;
                if (bp == null) return false;

                var ramComponent = bp.GetComponent<Kingmaker.UnitLogic.Abilities.Components.AbilityCustomRam>();
                if (ramComponent == null) return false;

                // m_RamThrough 필드 (Reflection)
                var ramThroughField = ramComponent.GetType().GetField("m_RamThrough",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (ramThroughField != null)
                {
                    return (bool)ramThroughField.GetValue(ramComponent);
                }

                return false;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsRamThroughAbility failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Self-Targeted AOE (v3.1.23)

        /// <summary>
        /// ★ v3.1.23: 자신 타겟 AOE 공격인지 확인
        /// Bladedance 같은 능력: Range=Personal, CanTargetSelf, 인접 유닛 공격
        /// </summary>
        public static bool IsSelfTargetedAoEAttack(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                var bp = ability.Blueprint;
                if (bp == null) return false;

                // Range=Personal + CanTargetSelf 체크
                if (bp.Range != AbilityRange.Personal) return false;
                if (!bp.CanTargetSelf) return false;

                // DangerousAoE로 분류된 능력만
                return AbilityDatabase.IsDangerousAoE(ability);
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsSelfTargetedAoEAttack failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.8.50: 근접 AOE 능력 감지 (유닛 타겟형)
        /// BladeDance(Self-Target)는 제외 — 적을 직접 타겟하는 근접 AOE만 감지
        /// 게임 AbilityMeleeBurst + Pattern 기반 근접 스플래시 공격
        /// </summary>
        public static bool IsMeleeAoEAbility(AbilityData ability)
        {
            if (ability == null) return false;
            try
            {
                // Self-Target AOE는 이미 Phase 4.3에서 별도 처리
                if (IsSelfTargetedAoEAttack(ability)) return false;

                // 근접 능력이어야 함
                if (!ability.IsMelee) return false;

                // AOE 패턴이 있어야 함 (게임 네이티브 + 커스텀 감지)
                if (CombatHelpers.IsAoEAbility(ability)) return true;

                // 패턴 설정 직접 확인 (IsAoEAbility에서 놓칠 수 있는 케이스)
                if (ability.GetPatternSettings() != null) return true;

                return false;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsMeleeAoEAbility failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.6.3: 인접 아군 수 계산 (Self-Targeted AOE 안전성 체크)
        /// radius는 타일 단위 (기본 2타일 ≈ 2.7m)
        /// </summary>
        public static int CountAdjacentAllies(BaseUnitEntity unit, float radius = 2f)  // 타일
        {
            if (unit == null) return 0;

            try
            {
                int count = 0;
                var allUnits = Game.Instance?.State?.AllBaseAwakeUnits;
                if (allUnits == null) return 0;

                foreach (var other in allUnits)
                {
                    if (other == null || other == unit) continue;
                    if (other.LifeState.IsDead) continue;

                    // 아군 판별
                    bool isAlly = unit.IsPlayerFaction == other.IsPlayerFaction;
                    if (!isAlly) continue;

                    // ★ v3.6.3: 타일 단위로 변환
                    float distTiles = MetersToTiles(Vector3.Distance(unit.Position, other.Position));
                    if (distTiles <= radius)
                        count++;
                }

                return count;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CountAdjacentAllies failed for {unit?.CharacterName}: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// ★ v3.6.3: 인접 적 수 계산 (Self-Targeted AOE 효율성 체크)
        /// radius는 타일 단위 (기본 2타일 ≈ 2.7m)
        /// </summary>
        public static int CountAdjacentEnemies(BaseUnitEntity unit, float radius = 2f)  // 타일
        {
            if (unit == null) return 0;

            try
            {
                int count = 0;
                var allUnits = Game.Instance?.State?.AllBaseAwakeUnits;
                if (allUnits == null) return 0;

                foreach (var other in allUnits)
                {
                    if (other == null || other == unit) continue;
                    if (other.LifeState.IsDead) continue;

                    // 적 판별
                    bool isEnemy = (unit.IsPlayerFaction && other.IsPlayerEnemy) ||
                                   (!unit.IsPlayerFaction && !other.IsPlayerEnemy);
                    if (!isEnemy) continue;

                    // ★ v3.6.3: 타일 단위로 변환
                    float distTiles = MetersToTiles(Vector3.Distance(unit.Position, other.Position));
                    if (distTiles <= radius)
                        count++;
                }

                return count;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CountAdjacentEnemies failed for {unit?.CharacterName}: {ex.Message}");
                return 0;
            }
        }

        #endregion

        #region Pattern Info Cache (v3.1.19)

        /// <summary>
        /// ★ v3.1.19: AOE 패턴 정보 통합 클래스
        /// ★ v3.8.09: IsRamAbility, IsRamThrough 추가
        /// </summary>
        public class PatternInfo
        {
            public Kingmaker.Blueprints.PatternType? Type { get; set; }
            public float Radius { get; set; }
            public float Angle { get; set; }
            public Kingmaker.UnitLogic.Abilities.Components.TargetType TargetType { get; set; }
            public bool IsDirectional { get; set; }
            public bool CanBeDirectional { get; set; }  // ★ v3.8.09: Type만으로 판단
            public bool IsRamAbility { get; set; }      // ★ v3.8.09: AbilityCustomRam 사용
            public bool IsRamThrough { get; set; }      // ★ v3.8.09: 관통 여부
            public bool IsValid => Radius > 0 || IsRamAbility;
        }

        private static Dictionary<string, PatternInfo> PatternCache = new Dictionary<string, PatternInfo>();

        /// <summary>
        /// ★ v3.1.19: 패턴 정보 조회 (캐싱)
        /// ★ v3.8.09: GetActualIsDirectional() 사용으로 정확한 IsDirectional 판정
        /// </summary>
        public static PatternInfo GetPatternInfo(AbilityData ability)
        {
            try
            {
                var guid = ability?.Blueprint?.AssetGuid?.ToString();
                if (string.IsNullOrEmpty(guid)) return null;

                if (PatternCache.TryGetValue(guid, out var cached))
                    return cached;

                var patternType = GetPatternType(ability);
                bool canBeDirectional = IsDirectionalPattern(patternType);  // Type 기반 (Ray/Cone/Sector)
                bool actualIsDirectional = GetActualIsDirectional(ability); // 게임 실제 로직

                // ★ v3.8.09: Ram 능력 체크
                bool isRam = IsRamAbility(ability);
                bool isRamThrough = isRam && IsRamThroughAbility(ability);

                var info = new PatternInfo
                {
                    Type = patternType,
                    Radius = GetAoERadius(ability),
                    Angle = GetPatternAngle(ability),
                    TargetType = GetAoETargetType(ability),
                    CanBeDirectional = canBeDirectional,
                    IsDirectional = actualIsDirectional,
                    IsRamAbility = isRam,
                    IsRamThrough = isRamThrough
                };

                // ★ v3.8.09: 디버그 로그 (새 능력일 때만)
                if (actualIsDirectional != canBeDirectional || isRam)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] PatternInfo for {ability.Name}: Type={patternType}, " +
                        $"CanBeDirectional={canBeDirectional}, IsDirectional={actualIsDirectional}, " +
                        $"IsRam={isRam}, RamThrough={isRamThrough}, Radius={info.Radius}");
                }

                PatternCache[guid] = info;
                return info;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// ★ v3.1.19: 패턴 캐시 클리어 (전투 종료 시 호출)
        /// </summary>
        public static void ClearPatternCache()
        {
            PatternCache.Clear();
            Main.LogDebug("[CombatAPI] Pattern cache cleared");
        }

        #endregion

        #region Game Pattern API (v3.5.39)

        /// <summary>
        /// ★ v3.5.39: 게임 API를 통해 AOE 패턴의 영향받는 노드들 조회
        /// 게임과 동일한 정확한 타일 기반 계산
        /// </summary>
        public static OrientedPatternData GetAffectedNodes(
            AbilityData ability,
            Vector3 targetPosition,
            Vector3 casterPosition)
        {
            try
            {
                if (ability == null) return OrientedPatternData.Empty;

                var target = new TargetWrapper(targetPosition);
                return ability.GetPattern(target, casterPosition);
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAffectedNodes error: {ex.Message}");
                return OrientedPatternData.Empty;
            }
        }

        /// <summary>
        /// ★ v3.5.39: 게임 API를 통해 패턴 내 적 수 계산
        /// Circle, Cone, Ray 모든 패턴에서 정확하게 작동
        /// </summary>
        public static int CountEnemiesInPattern(
            AbilityData ability,
            Vector3 targetPosition,
            Vector3 casterPosition,
            List<BaseUnitEntity> enemies)
        {
            try
            {
                if (ability == null || enemies == null || enemies.Count == 0)
                    return 0;

                var pattern = GetAffectedNodes(ability, targetPosition, casterPosition);
                if (pattern.IsEmpty) return 0;

                // ★ v3.9.10: new HashSet<> 제거 → 정적 풀 재사용
                _sharedUnitSet.Clear();
                for (int i = 0; i < enemies.Count; i++)
                    _sharedUnitSet.Add(enemies[i]);

                // ★ v3.9.22: Remove로 중복 방지 — 대형 유닛(4x4)이 여러 타일 점유 시 1회만 카운트
                int count = 0;
                foreach (var node in pattern.Nodes)
                {
                    if (node.TryGetUnit(out var unit) &&
                        unit is BaseUnitEntity baseUnit &&
                        _sharedUnitSet.Remove(baseUnit))
                    {
                        count++;
                    }
                }

                return count;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CountEnemiesInPattern error: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// ★ v3.5.39: 게임 API를 통해 패턴 내 아군 수 계산 (자신 제외)
        /// </summary>
        public static int CountAlliesInPattern(
            AbilityData ability,
            Vector3 targetPosition,
            Vector3 casterPosition,
            BaseUnitEntity caster,
            List<BaseUnitEntity> allies)
        {
            try
            {
                if (ability == null || allies == null || allies.Count == 0)
                    return 0;

                var pattern = GetAffectedNodes(ability, targetPosition, casterPosition);
                if (pattern.IsEmpty) return 0;

                // ★ v3.9.10: new HashSet<> 제거 → 정적 풀 재사용
                _sharedAllySet.Clear();
                for (int i = 0; i < allies.Count; i++)
                    _sharedAllySet.Add(allies[i]);

                // ★ v3.9.22: Remove로 중복 방지 — 대형 유닛 다중 타일 점유 시 1회만 카운트
                int count = 0;
                foreach (var node in pattern.Nodes)
                {
                    if (node.TryGetUnit(out var unit) &&
                        unit is BaseUnitEntity baseUnit &&
                        baseUnit != caster &&
                        _sharedAllySet.Remove(baseUnit))
                    {
                        count++;
                    }
                }

                return count;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CountAlliesInPattern error: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// ★ v3.9.10: 패턴 1회 계산으로 적/아군 수 동시 카운트
        /// GetAffectedNodes 중복 호출 제거 — AttackPlanner 이중 호출 최적화
        /// </summary>
        public static void CountUnitsInPattern(
            AbilityData ability,
            Vector3 targetPosition,
            Vector3 casterPosition,
            BaseUnitEntity caster,
            List<BaseUnitEntity> enemies,
            List<BaseUnitEntity> allies,
            out int enemyCount,
            out int allyCount)
        {
            enemyCount = 0;
            allyCount = 0;

            try
            {
                if (ability == null) return;

                var pattern = GetAffectedNodes(ability, targetPosition, casterPosition);
                if (pattern.IsEmpty) return;

                _sharedUnitSet.Clear();
                if (enemies != null)
                    for (int i = 0; i < enemies.Count; i++)
                        _sharedUnitSet.Add(enemies[i]);

                _sharedAllySet.Clear();
                if (allies != null)
                    for (int i = 0; i < allies.Count; i++)
                        _sharedAllySet.Add(allies[i]);

                // ★ v3.9.22: Remove로 중복 방지 — 대형 유닛 다중 타일 점유 시 1회만 카운트
                foreach (var node in pattern.Nodes)
                {
                    if (node.TryGetUnit(out var unit) && unit is BaseUnitEntity baseUnit)
                    {
                        if (_sharedUnitSet.Remove(baseUnit))
                            enemyCount++;
                        if (baseUnit != caster && _sharedAllySet.Remove(baseUnit))
                            allyCount++;
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CountUnitsInPattern error: {ex.Message}");
            }
        }

        /// <summary>
        /// ★ v3.5.39: 특정 유닛이 패턴 내에 있는지 확인
        /// </summary>
        public static bool IsUnitInPattern(
            AbilityData ability,
            Vector3 targetPosition,
            Vector3 casterPosition,
            BaseUnitEntity unit)
        {
            try
            {
                if (ability == null || unit == null) return false;

                var pattern = GetAffectedNodes(ability, targetPosition, casterPosition);
                if (pattern.IsEmpty) return false;

                // 유닛이 점유한 모든 노드 확인
                foreach (var occupiedNode in unit.GetOccupiedNodes())
                {
                    if (pattern.Contains(occupiedNode))
                        return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsUnitInPattern error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.5.39: 패턴 내 모든 유닛 조회 (적/아군 구분 없이)
        /// </summary>
        public static List<BaseUnitEntity> GetUnitsInPattern(
            AbilityData ability,
            Vector3 targetPosition,
            Vector3 casterPosition)
        {
            var result = new List<BaseUnitEntity>();
            try
            {
                if (ability == null) return result;

                var pattern = GetAffectedNodes(ability, targetPosition, casterPosition);
                if (pattern.IsEmpty) return result;

                var seen = new HashSet<BaseUnitEntity>();
                foreach (var node in pattern.Nodes)
                {
                    if (node.TryGetUnit(out var unit) &&
                        unit is BaseUnitEntity baseUnit &&
                        !seen.Contains(baseUnit))
                    {
                        seen.Add(baseUnit);
                        result.Add(baseUnit);
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetUnitsInPattern error: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// ★ v3.5.39: AOE 평가 - 적 점수와 아군 피해를 함께 계산
        /// </summary>
        public static (int enemyHits, int allyHits, int playerPartyHits) EvaluateAoEPosition(
            AbilityData ability,
            Vector3 targetPosition,
            Vector3 casterPosition,
            BaseUnitEntity caster,
            List<BaseUnitEntity> enemies,
            List<BaseUnitEntity> allies)
        {
            try
            {
                if (ability == null) return (0, 0, 0);

                var pattern = GetAffectedNodes(ability, targetPosition, casterPosition);
                if (pattern.IsEmpty) return (0, 0, 0);

                int enemyHits = 0;
                int allyHits = 0;
                int playerPartyHits = 0;

                var enemySet = new HashSet<BaseUnitEntity>(enemies ?? new List<BaseUnitEntity>());
                var allySet = new HashSet<BaseUnitEntity>(allies ?? new List<BaseUnitEntity>());
                var counted = new HashSet<BaseUnitEntity>();

                foreach (var node in pattern.Nodes)
                {
                    if (!node.TryGetUnit(out var unit) || !(unit is BaseUnitEntity baseUnit))
                        continue;

                    if (counted.Contains(baseUnit)) continue;
                    counted.Add(baseUnit);

                    if (enemySet.Contains(baseUnit))
                    {
                        enemyHits++;
                    }
                    else if (baseUnit != caster && allySet.Contains(baseUnit))
                    {
                        allyHits++;
                        if (baseUnit.IsInPlayerParty)
                            playerPartyHits++;
                    }
                }

                return (enemyHits, allyHits, playerPartyHits);
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] EvaluateAoEPosition error: {ex.Message}");
                return (0, 0, 0);
            }
        }

        /// <summary>
        /// ★ v3.6.9: AOE 높이 차이 체크 - 게임 로직 참조
        /// Circle 패턴: 1.6m 이상 차이 시 효과 없음
        /// ★ v3.7.15: Directional 패턴도 1.6m로 통일
        /// 이유: 게임은 기울기(slope)를 계산하여 더 복잡한 검증을 함
        ///       우리 AI가 0.3m로 너무 엄격하게 필터링하면 공격 기회 상실
        ///       게임이 최종 검증을 하므로 사전 필터링은 관대하게
        /// </summary>
        public const float AoELevelDiffCircle = 1.6f;      // AoEPattern.SameLevelDiff
        public const float AoELevelDiffDirectional = 1.6f; // ★ v3.7.15: 0.3f → 1.6f (게임이 기울기 계산으로 검증)

        /// <summary>
        /// ★ v3.6.9: AOE 높이 차이로 인해 적에게 효과가 닿을 수 있는지 확인
        /// </summary>
        /// <param name="ability">AOE 능력</param>
        /// <param name="casterPosition">시전자 위치</param>
        /// <param name="targetPosition">타겟 위치</param>
        /// <returns>높이 차이가 허용 범위 내면 true</returns>
        public static bool IsAoEHeightInRange(AbilityData ability, Vector3 casterPosition, Vector3 targetPosition)
        {
            try
            {
                if (ability == null) return true;  // 안전 폴백

                // 패턴 타입 확인
                var patternType = GetPatternType(ability);

                // ★ v3.6.9 fix: 패턴 타입이 없으면 AOE 여부 확인 후 Circle로 처리
                // ★ v3.8.09: GetActualIsDirectional() 사용으로 정확한 판정
                bool isDirectional = false;
                if (patternType.HasValue)
                {
                    isDirectional = GetActualIsDirectional(ability);  // ★ v3.8.09: 게임 실제 로직
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] AOE height: {ability.Name} PatternType={patternType.Value}, IsDirectional={isDirectional}");
                }
                else
                {
                    // 패턴 타입이 없으면 AOE 반경으로 Circle 여부 판단
                    float aoERadius = GetAoERadius(ability);
                    if (aoERadius > 0)
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] AOE height: {ability.Name} PatternType=null but AOE r={aoERadius}, treating as Circle");
                    }
                    // isDirectional = false → Circle 임계값(1.6m) 사용
                }

                // 높이 차이 계산 (절대값)
                float heightDiff = Mathf.Abs(casterPosition.y - targetPosition.y);

                // 패턴 타입에 따른 임계값 선택
                float threshold = isDirectional ? AoELevelDiffDirectional : AoELevelDiffCircle;

                bool inRange = heightDiff <= threshold;

                if (!inRange)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] AOE height check failed: {ability.Name} " +
                        $"heightDiff={heightDiff:F2}m > threshold={threshold:F2}m ({(isDirectional ? "Directional" : "Circle")})");
                }

                return inRange;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsAoEHeightInRange error: {ex.Message}");
                return true;  // 에러 시 안전하게 허용
            }
        }

        /// <summary>
        /// ★ v3.6.9: AOE 높이 차이로 인해 적에게 효과가 닿을 수 있는지 확인 (유닛 버전)
        /// </summary>
        public static bool IsAoEHeightInRange(AbilityData ability, BaseUnitEntity caster, BaseUnitEntity target)
        {
            if (caster == null || target == null) return true;
            return IsAoEHeightInRange(ability, caster.Position, target.Position);
        }

        /// <summary>
        /// ★ v3.6.10: AOE 범위 내에 유닛이 있는지 확인 (2D 거리 + 높이 체크 통합)
        /// AoESafetyChecker, ClusterDetector에서 사용
        /// </summary>
        /// <param name="ability">AOE 능력 (null이면 Circle로 처리)</param>
        /// <param name="center">AOE 중심 (시전자 또는 타겟 위치)</param>
        /// <param name="unit">체크할 유닛</param>
        /// <param name="aoERadius">AOE 반경 (타일 단위)</param>
        /// <returns>유닛이 AOE 효과 범위 내에 있으면 true</returns>
        public static bool IsUnitInAoERange(AbilityData ability, Vector3 center, BaseUnitEntity unit, float aoERadius)
        {
            if (unit == null) return false;

            // ★ v3.8.66: 대형 유닛은 가장 가까운 경계 셀 기준 (SizeRect 반영)
            float dist2D = (float)WarhammerGeometryUtils.DistanceToInCells(
                center, new IntRect(0, 0, 0, 0),  // AoE 중심은 점
                unit.Position, unit.SizeRect);
            if (dist2D > aoERadius) return false;

            // 2. 높이 차이 체크
            float heightDiff = Mathf.Abs(center.y - unit.Position.y);

            // ★ v3.8.09: 패턴 타입에 따른 높이 임계값 - GetActualIsDirectional 사용
            bool isDirectional = false;
            if (ability != null)
            {
                isDirectional = GetActualIsDirectional(ability);  // ★ v3.8.09: 게임 실제 로직
            }

            float heightThreshold = isDirectional ? AoELevelDiffDirectional : AoELevelDiffCircle;
            return heightDiff <= heightThreshold;
        }

        /// <summary>
        /// ★ v3.6.10: 방향성 AOE(Cone/Ray/Sector) 범위 내에 유닛이 있는지 확인
        /// ★ v3.8.09: Custom/Circle 패턴 지원 추가
        /// </summary>
        public static bool IsUnitInDirectionalAoERange(
            Vector3 casterPosition,
            Vector3 direction,
            BaseUnitEntity unit,
            float radius,  // 타일
            float angle,
            Kingmaker.Blueprints.PatternType patternType)
        {
            if (unit == null) return false;

            Vector3 toUnit = unit.Position - casterPosition;

            // 1. 2D 거리 체크
            float dist2D = MetersToTiles(new Vector3(toUnit.x, 0, toUnit.z).magnitude);
            if (dist2D > radius) return false;
            if (dist2D < 0.5f) return false;  // 캐스터 위치 제외

            // 2. 높이 차이 체크 (Directional은 0.3m)
            float heightDiff = Mathf.Abs(toUnit.y);
            if (heightDiff > AoELevelDiffDirectional) return false;

            // 3. 각도 체크
            Vector3 toUnit2D = new Vector3(toUnit.x, 0, toUnit.z);
            Vector3 direction2D = new Vector3(direction.x, 0, direction.z);
            float unitAngle = Vector3.Angle(direction2D, toUnit2D);

            switch (patternType)
            {
                case Kingmaker.Blueprints.PatternType.Ray:
                    // ★ v3.8.65: 게임 검증 — Ray = Bresenham 1-cell 직선 (AoEPattern.Angle=0)
                    // 각도가 아닌 수직 거리 1타일 이내로 판정
                    {
                        Vector3 dirNorm2D = direction2D.normalized;
                        float perpMeters = Vector3.Cross(dirNorm2D, toUnit2D).magnitude;
                        float perpTiles = MetersToTiles(perpMeters);
                        return perpTiles <= 1f;
                    }

                case Kingmaker.Blueprints.PatternType.Cone:
                case Kingmaker.Blueprints.PatternType.Sector:
                    return unitAngle <= angle / 2f;

                case Kingmaker.Blueprints.PatternType.Custom:
                    // ★ v3.8.09: Custom 패턴 - 각도가 설정되어 있으면 사용
                    // 360도면 전방향 (거리만 체크)
                    if (angle >= 360f) return true;
                    return unitAngle <= angle / 2f;

                case Kingmaker.Blueprints.PatternType.Circle:
                    // ★ v3.8.09: Circle은 거리만 체크 (방향 무관)
                    return true;

                default:
                    return false;
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

        #region Targeting Detection (v3.1.25)

        /// <summary>
        /// ★ v3.1.25: 적이 특정 유닛을 타겟팅 중인지 확인
        /// </summary>
        public static bool IsTargeting(BaseUnitEntity enemy, BaseUnitEntity target)
        {
            if (enemy?.CombatState == null || target == null) return false;
            try
            {
                return enemy.CombatState.LastTarget == target;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsTargeting failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.1.25: 특정 아군을 타겟팅 중인 적 목록 조회
        /// </summary>
        public static List<BaseUnitEntity> GetEnemiesTargeting(
            BaseUnitEntity ally,
            List<BaseUnitEntity> enemies)
        {
            var targeting = new List<BaseUnitEntity>();
            if (ally == null || enemies == null) return targeting;

            foreach (var enemy in enemies)
            {
                if (enemy?.CombatState?.LastTarget == ally)
                    targeting.Add(enemy);
            }
            return targeting;
        }

        /// <summary>
        /// ★ v3.1.25: 아군(특정 유닛 제외)을 타겟팅 중인 모든 적 조회
        /// 탱커가 호출할 때: excludeUnit = 탱커 자신 (탱커 타겟팅 적은 이미 어그로 잡힌 상태)
        /// </summary>
        public static List<BaseUnitEntity> GetEnemiesTargetingAllies(
            BaseUnitEntity excludeUnit,
            List<BaseUnitEntity> allies,
            List<BaseUnitEntity> enemies)
        {
            var targeting = new List<BaseUnitEntity>();
            if (allies == null || enemies == null) return targeting;

            foreach (var enemy in enemies)
            {
                if (enemy?.CombatState == null) continue;
                var lastTarget = enemy.CombatState.LastTarget as BaseUnitEntity;
                if (lastTarget != null && lastTarget != excludeUnit && allies.Contains(lastTarget))
                {
                    targeting.Add(enemy);
                }
            }
            return targeting;
        }

        /// <summary>
        /// ★ v3.1.25: 위협받는 아군 수 (탱커 제외)
        /// </summary>
        public static int CountAlliesUnderThreat(
            BaseUnitEntity excludeUnit,
            List<BaseUnitEntity> allies,
            List<BaseUnitEntity> enemies)
        {
            if (allies == null || enemies == null) return 0;

            var threatenedAllies = new HashSet<BaseUnitEntity>();
            foreach (var enemy in enemies)
            {
                if (enemy?.CombatState == null) continue;
                var lastTarget = enemy.CombatState.LastTarget as BaseUnitEntity;
                if (lastTarget != null && lastTarget != excludeUnit && allies.Contains(lastTarget))
                {
                    threatenedAllies.Add(lastTarget);
                }
            }
            return threatenedAllies.Count;
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

        #region Hit Chance API (v3.6.7)

        /// <summary>
        /// ★ v3.6.7: 명중률 정보 구조체
        /// </summary>
        public class HitChanceInfo
        {
            /// <summary>★ v3.26.0: 실질 명중률 (BS × (1-Dodge) × (1-Parry), 1-95%)</summary>
            public int HitChance { get; set; }

            /// <summary>★ v3.26.0: BS 기반 원본 명중률 (dodge/parry 미반영)</summary>
            public int RawBSHitChance { get; set; }

            /// <summary>★ v3.26.0: 추정 회피율 (0-95%)</summary>
            public int EstimatedDodgeChance { get; set; }

            /// <summary>★ v3.26.0: 추정 패리율 (0-95%, 근접만)</summary>
            public int EstimatedParryChance { get; set; }

            /// <summary>거리 계수 (1.0=최적, 0.5=절반 이상, 0.0=사거리 초과)</summary>
            public float DistanceFactor { get; set; }

            /// <summary>엄폐 타입</summary>
            public LosCalculations.CoverType CoverType { get; set; }

            /// <summary>최적 거리 내에 있는지 (DistanceFactor >= 1.0)</summary>
            public bool IsInOptimalRange => DistanceFactor >= 1.0f;

            /// <summary>최대 사거리 내에 있는지 (DistanceFactor > 0)</summary>
            public bool IsInRange => DistanceFactor > 0f;

            /// <summary>명중률이 낮은지 (50% 미만)</summary>
            public bool IsLowHitChance => HitChance < 50;

            /// <summary>명중률이 매우 낮은지 (30% 미만)</summary>
            public bool IsVeryLowHitChance => HitChance < 30;

            public override string ToString()
            {
                return $"HitChance={HitChance}%(BS={RawBSHitChance}% dodge={EstimatedDodgeChance}% parry={EstimatedParryChance}%), DistFactor={DistanceFactor:F1}, Cover={CoverType}";
            }
        }

        /// <summary>
        /// ★ v3.6.7: 원거리 공격의 명중률 계산
        /// RuleCalculateHitChances 룰 시스템 사용
        /// </summary>
        /// <param name="ability">공격 능력</param>
        /// <param name="attacker">공격자</param>
        /// <param name="target">타겟</param>
        /// <returns>명중률 정보 (null if 계산 실패)</returns>
        public static HitChanceInfo GetHitChance(AbilityData ability, BaseUnitEntity attacker, BaseUnitEntity target)
        {
            if (ability == null || attacker == null || target == null)
                return null;

            try
            {
                int rawHitChance;
                float distanceFactor = 1.0f;
                var coverType = LosCalculations.CoverType.None;

                // ★ v3.6.8: 근접/Scatter 공격은 BS 100% (게임 로직 동일)
                if (ability.IsMelee || ability.IsScatter)
                {
                    rawHitChance = 100;
                }
                else
                {
                    // RuleCalculateHitChances 트리거
                    var hitRule = new RuleCalculateHitChances(
                        attacker, target, ability,
                        0,  // burstIndex (첫 발)
                        attacker.Position, target.Position
                    );
                    Rulebook.Trigger(hitRule);

                    rawHitChance = hitRule.ResultHitChance;
                    distanceFactor = hitRule.DistanceFactor;
                    coverType = hitRule.ResultLos;
                }

                // ★ v3.26.0: Dodge/Parry 추정 → 실질 명중률 계산
                int dodgeChance = EstimateDodgeChance(target, attacker, ability);
                int parryChance = EstimateParryChance(target, attacker, ability);
                int effectiveHitChance = CalculateEffectiveHitChance(rawHitChance, dodgeChance, parryChance);

                var result = new HitChanceInfo
                {
                    HitChance = effectiveHitChance,        // 실질 명중률
                    RawBSHitChance = rawHitChance,         // 원본 보존
                    EstimatedDodgeChance = dodgeChance,
                    EstimatedParryChance = parryChance,
                    DistanceFactor = distanceFactor,
                    CoverType = coverType
                };

                if (Main.IsDebugEnabled)
                    Main.LogDebug($"[CombatAPI] HitChance: {attacker.CharacterName} -> {target.CharacterName}: " +
                        $"BS={rawHitChance}% dodge={dodgeChance}% parry={parryChance}% → effective={effectiveHitChance}%");

                return result;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetHitChance error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ★ v3.6.7: 특정 위치에서 공격 시 명중률 계산 (이동 계획용)
        /// </summary>
        public static HitChanceInfo GetHitChanceFromPosition(
            AbilityData ability,
            BaseUnitEntity attacker,
            Vector3 attackerPosition,
            BaseUnitEntity target)
        {
            if (ability == null || attacker == null || target == null)
                return null;

            try
            {
                int rawHitChance;
                float distanceFactor = 1.0f;
                var coverType = LosCalculations.CoverType.None;

                // ★ v3.6.8: 근접/Scatter 공격은 BS 100%
                if (ability.IsMelee || ability.IsScatter)
                {
                    rawHitChance = 100;
                }
                else
                {
                    var hitRule = new RuleCalculateHitChances(
                        attacker, target, ability,
                        0,
                        attackerPosition,  // 가상 위치에서 계산
                        target.Position
                    );
                    Rulebook.Trigger(hitRule);

                    rawHitChance = hitRule.ResultHitChance;
                    distanceFactor = hitRule.DistanceFactor;
                    coverType = hitRule.ResultLos;
                }

                // ★ v3.26.0: Dodge/Parry 추정 → 실질 명중률
                int dodgeChance = EstimateDodgeChance(target, attacker, ability);
                int parryChance = EstimateParryChance(target, attacker, ability);
                int effectiveHitChance = CalculateEffectiveHitChance(rawHitChance, dodgeChance, parryChance);

                return new HitChanceInfo
                {
                    HitChance = effectiveHitChance,
                    RawBSHitChance = rawHitChance,
                    EstimatedDodgeChance = dodgeChance,
                    EstimatedParryChance = parryChance,
                    DistanceFactor = distanceFactor,
                    CoverType = coverType
                };
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetHitChanceFromPosition error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ★ v3.6.7: 거리 계수만 빠르게 계산 (이동 계획 최적화용)
        /// - 1.0 = 최대 사거리의 절반 이내 (최적)
        /// - 0.5 = 절반 초과 ~ 최대 사거리 (명중률 절반)
        /// - 0.0 = 최대 사거리 초과 (명중 불가)
        /// </summary>
        public static float GetDistanceFactor(AbilityData ability, Vector3 attackerPos, Vector3 targetPos)
        {
            if (ability == null) return 0f;

            try
            {
                // 무기 최대 사거리 (타일 단위)
                int maxRange = ability.RangeCells;
                if (maxRange <= 0 || maxRange >= 1000) return 1.0f;  // Unlimited

                // 실제 거리 (타일 단위)
                float distanceTiles = GetDistanceInTiles(attackerPos, targetPos);

                // 거리 계수 계산 (게임 로직 동일)
                float halfRange = maxRange / 2f;
                if (distanceTiles <= halfRange)
                    return 1.0f;  // 최적 거리
                else if (distanceTiles <= maxRange)
                    return 0.5f;  // 절반 거리
                else
                    return 0.0f;  // 사거리 초과
            }
            catch
            {
                return 1.0f;
            }
        }

        /// <summary>
        /// ★ v3.6.7: 최적 사거리(명중률 100% 적용) 타일 수 반환
        /// </summary>
        public static float GetOptimalRangeInTiles(AbilityData ability)
        {
            if (ability == null) return 0f;

            try
            {
                int maxRange = ability.RangeCells;
                if (maxRange <= 0 || maxRange >= 1000) return 1000f;  // Unlimited
                return maxRange / 2f;  // 최적 = 최대 사거리의 절반
            }
            catch
            {
                return 10f;  // 폴백
            }
        }

        #endregion

        #region Unit Stat Query API (v3.26.0)

        // ─── ★ v3.26.0: 유닛 스탯 조회 API ──────────────────────────────────
        // 적/아군 동일 API — BaseUnitEntity.GetStatOptional()

        /// <summary>스탯 최종값 (모든 버프/디버프 적용 후)</summary>
        public static int GetStatValue(BaseUnitEntity unit, StatType stat)
        {
            try
            {
                return unit?.GetStatOptional(stat)?.ModifiedValue ?? 0;
            }
            catch { return 0; }
        }

        /// <summary>방어구 흡수값 (장비 + 스탯 보너스)</summary>
        public static int GetArmorAbsorption(BaseUnitEntity unit)
        {
            try
            {
                int equipArmor = unit?.Body?.Armor?.MaybeArmor?.Blueprint?.DamageAbsorption ?? 0;
                int statBonus = GetStatValue(unit, StatType.DamageAbsorption);
                return equipArmor + statBonus;
            }
            catch { return 0; }
        }

        /// <summary>편향값 (장비 + 스탯)</summary>
        public static int GetDeflection(BaseUnitEntity unit)
        {
            try
            {
                int equipDeflect = unit?.Body?.Armor?.MaybeArmor?.Blueprint?.DamageDeflection ?? 0;
                int statBonus = GetStatValue(unit, StatType.DamageDeflection);
                return equipDeflect + statBonus;
            }
            catch { return 0; }
        }

        /// <summary>
        /// ★ v3.26.0: CC 저항력 추정 (0-100)
        /// 높을수록 CC에 강함. Toughness + Willpower 기반 간이 추정.
        /// </summary>
        public static float EstimateCCResistance(BaseUnitEntity target)
        {
            try
            {
                int tgh = GetStatValue(target, StatType.WarhammerToughness);
                int wp = GetStatValue(target, StatType.WarhammerWillpower);
                int dominantStat = Math.Max(tgh, wp);
                float resistance = Math.Min(95f, 30f + dominantStat);
                return resistance;
            }
            catch { return 50f; }
        }

        #endregion

        #region Dodge/Parry Estimation (v3.26.0)

        // ─── ★ v3.26.0: Dodge/Parry 추정 → Effective Hit Chance ─────────────

        /// <summary>
        /// Dodge 확률 추정 (RuleCalculateDodgeChance 트리거)
        /// 디컴파일 확인: 계산 전용 Rule (사이드이펙트 없음)
        /// </summary>
        public static int EstimateDodgeChance(BaseUnitEntity target, BaseUnitEntity attacker, AbilityData ability)
        {
            try
            {
                var targetUnit = target as UnitEntity;
                if (targetUnit == null) return 0;

                var dodgeRule = new RuleCalculateDodgeChance(
                    targetUnit,
                    attacker,    // MechanicEntity (BaseUnitEntity 상속)
                    ability,
                    LosCalculations.CoverType.None,
                    0            // burstIndex
                );
                Rulebook.Trigger(dodgeRule);
                return dodgeRule.Result;  // 0-95 (게임 자동 클램핑)
            }
            catch
            {
                return EstimateDodgeFromStats(target, attacker);
            }
        }

        /// <summary>스탯 기반 Dodge 폴백 추정</summary>
        private static int EstimateDodgeFromStats(BaseUnitEntity target, BaseUnitEntity attacker)
        {
            int targetAgi = GetStatValue(target, StatType.WarhammerAgility);
            int attackerPer = attacker != null ? GetStatValue(attacker, StatType.WarhammerPerception) : 0;
            int dodge = 30 + targetAgi - attackerPer / 2;
            return Math.Max(0, Math.Min(95, dodge));
        }

        /// <summary>
        /// Parry 확률 추정 (근접 전용, RuleCalculateParryChance 트리거)
        /// </summary>
        public static int EstimateParryChance(BaseUnitEntity target, BaseUnitEntity attacker, AbilityData ability)
        {
            try
            {
                if (ability == null || !ability.IsMelee) return 0;

                var targetUnit = target as UnitEntity;
                if (targetUnit == null) return 0;

                var parryRule = new RuleCalculateParryChance(
                    targetUnit,
                    attacker,    // MechanicEntity
                    ability,
                    0,           // resultSuperiorityNumber
                    false,       // isRangedParry
                    0            // attackerWeaponSkillOverride
                );
                Rulebook.Trigger(parryRule);
                return parryRule.Result;  // 0-95
            }
            catch
            {
                return EstimateParryFromStats(target, attacker, ability);
            }
        }

        /// <summary>스탯 기반 Parry 폴백 추정</summary>
        private static int EstimateParryFromStats(BaseUnitEntity target, BaseUnitEntity attacker, AbilityData ability)
        {
            if (ability == null || !ability.IsMelee) return 0;
            int targetWS = GetStatValue(target, StatType.WarhammerWeaponSkill);
            int attackerWS = attacker != null ? GetStatValue(attacker, StatType.WarhammerWeaponSkill) : 0;
            int parry = 20 + targetWS - attackerWS;
            return Math.Max(0, Math.Min(95, parry));
        }

        /// <summary>
        /// 실질 명중률 계산 (BS × (1-Dodge) × (1-Parry))
        /// </summary>
        private static int CalculateEffectiveHitChance(int rawHitChance, int dodgeChance, int parryChance)
        {
            float effective = rawHitChance / 100f;
            effective *= (1f - dodgeChance / 100f);
            effective *= (1f - parryChance / 100f);
            return Math.Max(1, Math.Min(95, (int)(effective * 100f)));
        }

        #endregion

        #region Flanking API (v3.28.0)

        // ─── ★ v3.28.0: 플랭킹 (공격 방향) API ─────────────────────────────
        // CustomGraphHelper.GetWarhammerAttackSide()를 래핑하여 AI 포지셔닝에 활용

        /// <summary>공격 방향의 전투 측면 판정 (Front/Left/Right/Back)</summary>
        public static WarhammerCombatSide GetAttackSide(BaseUnitEntity target, Vector3 attackerPosition)
        {
            try
            {
                Vector3 attackDir = (target.Position - attackerPosition).normalized;
                return CustomGraphHelper.GetWarhammerAttackSide(target.Forward, attackDir, target.Size);
            }
            catch
            {
                return WarhammerCombatSide.Front;
            }
        }

        /// <summary>
        /// 플랭킹 보너스 점수 (Back=1.0, Side=0.5, Front=0.0)
        /// 포지셔닝 및 타겟 스코어링에서 후방/측면 공격 보너스 부여용
        /// </summary>
        public static float GetFlankingBonus(BaseUnitEntity target, Vector3 attackerPosition)
        {
            var side = GetAttackSide(target, attackerPosition);
            switch (side)
            {
                case WarhammerCombatSide.Back: return 1.0f;
                case WarhammerCombatSide.Left:
                case WarhammerCombatSide.Right: return 0.5f;
                default: return 0f;
            }
        }

        #endregion

        #region Archetype Detection API (v3.28.0)

        // ─── ★ v3.28.0: 유닛 아키타입 감지 ─────────────────────────────────
        // ProgressionRoot.CareerPaths + GetPathRank()로 주 아키타입 감지

        /// <summary>유닛 아키타입 열거형</summary>
        public enum UnitArchetype
        {
            Unknown, Officer, Operative, ArchMilitant,
            Soldier, Assassin, Psyker, Navigator
        }

        // 아키타입 캐시 (유닛별, 전투 중 변경 없음)
        private static readonly Dictionary<string, UnitArchetype> _archetypeCache = new Dictionary<string, UnitArchetype>();

        /// <summary>아키타입 캐시 클리어 (전투 시작 시)</summary>
        public static void ClearArchetypeCache() => _archetypeCache.Clear();

        /// <summary>
        /// 유닛의 주 아키타입 감지 (캐시됨)
        /// ProgressionRoot.CareerPaths에서 가장 높은 PathRank를 가진 경로의 이름으로 판정
        /// </summary>
        public static UnitArchetype DetectArchetype(BaseUnitEntity unit)
        {
            if (unit == null) return UnitArchetype.Unknown;

            string unitId = unit.UniqueId;
            if (_archetypeCache.TryGetValue(unitId, out var cached))
                return cached;

            try
            {
                var progression = ProgressionRoot.Instance;
                if (progression == null)
                {
                    _archetypeCache[unitId] = UnitArchetype.Unknown;
                    return UnitArchetype.Unknown;
                }

                BlueprintCareerPath bestPath = null;
                int maxRank = 0;

                foreach (var cp in progression.CareerPaths)
                {
                    if (cp == null) continue;
                    int rank = unit.Progression.GetPathRank(cp);
                    if (rank > maxRank)
                    {
                        maxRank = rank;
                        bestPath = cp;
                    }
                }

                UnitArchetype result = UnitArchetype.Unknown;
                if (bestPath != null)
                {
                    string pathName = bestPath.name?.ToLowerInvariant() ?? "";
                    if (pathName.Contains("officer")) result = UnitArchetype.Officer;
                    else if (pathName.Contains("operative")) result = UnitArchetype.Operative;
                    else if (pathName.Contains("militant")) result = UnitArchetype.ArchMilitant;
                    else if (pathName.Contains("soldier")) result = UnitArchetype.Soldier;
                    else if (pathName.Contains("assassin")) result = UnitArchetype.Assassin;
                    else if (pathName.Contains("psyker")) result = UnitArchetype.Psyker;
                    else if (pathName.Contains("navigator")) result = UnitArchetype.Navigator;

                    if (Main.IsDebugEnabled && result != UnitArchetype.Unknown)
                        Main.LogDebug($"[CombatAPI] DetectArchetype({unit.CharacterName}): {result} (path={bestPath.name}, rank={maxRank})");
                }

                _archetypeCache[unitId] = result;
                return result;
            }
            catch
            {
                _archetypeCache[unitId] = UnitArchetype.Unknown;
                return UnitArchetype.Unknown;
            }
        }

        #endregion
    }
}
