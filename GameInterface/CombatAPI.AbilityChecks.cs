using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.Controllers;                          // AreaEffectsController
using Kingmaker.EntitySystem.Entities;                // BaseUnitEntity
using Kingmaker.Pathfinding;                          // CustomGridNodeBase
using Kingmaker.UnitLogic.Abilities;                  // AbilityData
using Kingmaker.UnitLogic.Abilities.Blueprints;       // BlueprintAbility
using Kingmaker.UnitLogic.Buffs.Components;           // WarhammerAbilityRestriction, WarhammerFreeUltimateBuff
using Kingmaker.Utility;                              // TargetWrapper
using Kingmaker.View.Covers;                          // LosCalculations
using CompanionAI_v3.Data;                            // AbilityDatabase, AbilityTiming

namespace CompanionAI_v3.GameInterface
{
    public static partial class CombatAPI
    {
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
    }
}
