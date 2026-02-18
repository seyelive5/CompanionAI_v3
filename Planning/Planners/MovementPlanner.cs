using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.Utility;
using Pathfinding;
using UnityEngine;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// ★ v3.0.47: 이동 관련 계획 담당
    /// - 이동, GapCloser, 후퇴, 안전 이동
    /// </summary>
    public static class MovementPlanner
    {
        /// <summary>
        /// ★ 이동 또는 GapCloser 계획 (공통화)
        /// 모든 Role에서 사용 - 근접 캐릭터가 적에게 도달 못하면 GapCloser 사용
        /// ★ v3.0.89: forceMove 파라미터 추가 - 공격 실패 시 이동 강제
        /// ★ v3.1.00: bypassCanMoveCheck 파라미터 추가 - MP 회복 예측 후 이동 허용
        /// ★ v3.1.01: predictedMP 파라미터 추가 - MovementAPI에 예측 MP 전달
        /// ★ v3.5.18: Blackboard 통합 - SharedTarget 우선 이동
        /// </summary>
        /// ★ v3.8.44: AttackPhaseContext 파라미터 추가
        public static PlannedAction PlanMoveOrGapCloser(Situation situation, ref float remainingAP, string roleName, bool forceMove = false, bool bypassCanMoveCheck = false, float predictedMP = 0f, AttackPhaseContext attackContext = null)
        {
            // ★ v3.0.89: forceMove=true면 HasHittableEnemies 체크 스킵
            // 사용 사례: 원거리 fallback으로 Hittable=True인데 PreferMelee라서 공격 못함 → 이동 필요
            // ★ v3.1.29: 원거리 캐릭터가 위험 거리 내에 있으면 후퇴 이동 허용
            if (!forceMove && situation.HasHittableEnemies)
            {
                // 원거리가 위험하면 이동 허용 (공격 가능해도 후퇴 필요)
                bool isRangedInDanger = situation.PrefersRanged && situation.IsInDanger;
                if (!isRangedInDanger)
                    return null;
                if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] Ranged in danger - allowing movement despite hittable enemies");
            }
            if (!situation.HasLivingEnemies) return null;
            if (situation.NearestEnemy == null) return null;

            // ★ v3.5.18: Blackboard에서 전술적 타겟 결정
            // 우선순위: SharedTarget > BestTarget > NearestEnemy
            var tacticalTarget = GetTacticalMoveTarget(situation);
            float tacticalTargetDistance = tacticalTarget != null
                ? CombatCache.GetDistance(situation.Unit, tacticalTarget)
                : situation.NearestEnemyDistance;

            // ★ v3.5.19: Main.Log로 변경하여 검증 가능하게
            Main.Log($"[{roleName}] TacticalTarget={tacticalTarget?.CharacterName ?? "null"}, Distance={tacticalTargetDistance:F1}m");

            // ★ 먼저 GapCloser 시도 (근접 선호이고 적이 멀 때)
            // ★ v3.5.18: tacticalTarget 사용
            // ★ v3.5.34: MP 비용 예측 추가
            if (!situation.PrefersRanged && tacticalTargetDistance > 3f)
            {
                // ★ v3.5.34: effectiveMP 계산 (predictedMP 고려)
                float effectiveMP = Math.Max(situation.CurrentMP, predictedMP);
                var gapCloserAction = PlanGapCloser(situation, tacticalTarget, ref remainingAP, ref effectiveMP, roleName);
                if (gapCloserAction != null)
                {
                    Main.Log($"[{roleName}] GapCloser instead of move: {gapCloserAction.Ability?.Name}");
                    return gapCloserAction;
                }
            }

            // GapCloser 없으면 일반 이동
            // ★ v3.1.01: bypassCanMoveCheck와 predictedMP 전달
            // ★ v3.5.18: tacticalTarget 전달
            return PlanMoveToEnemy(situation, roleName, bypassCanMoveCheck, predictedMP, tacticalTarget, attackContext);
        }

        /// <summary>
        /// ★ v3.5.18: 전술적 이동 타겟 결정
        /// Blackboard의 SharedTarget이 있으면 우선, 없으면 BestTarget 또는 NearestEnemy
        /// </summary>
        private static BaseUnitEntity GetTacticalMoveTarget(Situation situation)
        {
            // 1. Blackboard의 SharedTarget 확인
            var sharedTarget = TeamBlackboard.Instance?.SharedTarget;
            if (sharedTarget != null && !sharedTarget.LifeState.IsDead && situation.Enemies.Contains(sharedTarget))
            {
                // ★ v3.5.19: Main.Log로 변경
                Main.Log($"[MovementPlanner] ★ Using SharedTarget: {sharedTarget.CharacterName}");
                return sharedTarget;
            }

            // 2. BestTarget 확인 (Situation에서 이미 계산됨)
            if (situation.BestTarget != null && !situation.BestTarget.LifeState.IsDead)
            {
                // ★ v3.5.19: Main.Log로 변경
                Main.Log($"[MovementPlanner] Using BestTarget: {situation.BestTarget.CharacterName}");
                return situation.BestTarget;
            }

            // 3. 폴백: NearestEnemy
            return situation.NearestEnemy;
        }

        /// <summary>
        /// GapCloser 계획 (모든 Role 공통)
        /// ★ v3.0.81: PointTarget 능력 지원 (Death from Above 등)
        /// ★ v3.0.87: 디버그 로깅 추가
        /// ★ v3.1.24: 첫 타겟 실패 시 다른 적 타겟도 시도
        /// ★ v3.5.34: MP 비용 예측 추가 - 실제 타일 경로 기반 계산
        /// </summary>
        public static PlannedAction PlanGapCloser(Situation situation, BaseUnitEntity target, ref float remainingAP, ref float remainingMP, string roleName)
        {
            // ★ v3.0.87: 진입 로깅
            if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanGapCloser: target={target?.CharacterName}, AP={remainingAP:F1}, MP={remainingMP:F1}, attacks={situation.AvailableAttacks?.Count ?? 0}");

            var gapClosers = situation.AvailableAttacks
                .Where(a => AbilityDatabase.IsGapCloser(a))
                // ★ v3.7.27: MultiTarget 능력 이중 체크 (컴포넌트 + 명시적 제외)
                // ★ v3.8.62: BlueprintCache 캐시 사용 (GetComponent O(n) → O(1))
                .Where(a => !BlueprintCache.IsMultiTarget(a))
                .Where(a => !FamiliarAbilities.IsMultiTargetFamiliarAbility(a))
                .ToList();

            if (gapClosers.Count == 0)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanGapCloser: No GapClosers in AvailableAttacks");
                return null;
            }

            if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanGapCloser: Found {gapClosers.Count} GapClosers: {string.Join(", ", gapClosers.Select(g => g.Name))}");

            foreach (var gapCloser in gapClosers)
            {
                float cost = CombatAPI.GetAbilityAPCost(gapCloser);
                if (cost > remainingAP)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanGapCloser: {gapCloser.Name} skipped - AP cost {cost:F1} > remaining {remainingAP:F1}");
                    continue;
                }

                var info = AbilityDatabase.GetInfo(gapCloser);
                if (info?.HPThreshold > 0 && situation.HPPercent < info.HPThreshold)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanGapCloser: {gapCloser.Name} skipped - HP {situation.HPPercent:F0}% < threshold {info.HPThreshold}%");
                    continue;
                }

                // ★ v3.0.81: PointTarget 능력 처리 (Death from Above 등)
                bool isPointTarget = info != null && (info.Flags & AbilityFlags.PointTarget) != 0;
                if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanGapCloser: {gapCloser.Name} isPointTarget={isPointTarget}");

                // ★ v3.1.24: 첫 타겟 실패 시 다른 적들도 시도
                var targetsToTry = new List<BaseUnitEntity>();
                if (target != null) targetsToTry.Add(target);
                targetsToTry.AddRange(situation.Enemies.Where(e => e != target && e != null && e.IsConscious));

                foreach (var candidateTarget in targetsToTry)
                {
                    // ★ v3.5.34: MP 비용 체크 (실제 타일 경로 기반)
                    // ★ v3.9.22: MP=0이어도 GapCloser 시도 허용 — 게임 자체 경로 검증 + CanUseAbilityOn이 최종 판정
                    // 기존: MP 프리필터가 MP=0일 때 모든 GapCloser 차단 (돌격 불가 버그)
                    float mpCost = CombatAPI.GetAbilityExpectedMPCost(gapCloser, candidateTarget);
                    if (remainingMP > 0 && mpCost > remainingMP && mpCost < float.MaxValue)
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanGapCloser: {gapCloser.Name} -> {candidateTarget.CharacterName} skipped - MP cost {mpCost:F1} > remaining {remainingMP:F1}");
                        continue;
                    }

                    if (isPointTarget)
                    {
                        // ★ v3.1.28: 능력 정보 전달하여 범위 내 착지 위치 찾기
                        // ★ v3.4.02: P1 수정 - situation 전달하여 InfluenceMap 활용
                        var landingPosition = FindGapCloserLandingPosition(situation.Unit, candidateTarget, gapCloser, situation);
                        if (landingPosition.HasValue)
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanGapCloser: Landing position found at ({landingPosition.Value.x:F1},{landingPosition.Value.z:F1}) for {candidateTarget.CharacterName}");
                            var pointTarget = new TargetWrapper(landingPosition.Value);
                            string reason;
                            if (CombatAPI.CanUseAbilityOn(gapCloser, pointTarget, out reason))
                            {
                                remainingAP -= cost;
                                // ★ v3.5.34: MP도 차감
                                if (mpCost < float.MaxValue)
                                {
                                    remainingMP -= mpCost;
                                    if (remainingMP < 0) remainingMP = 0;
                                }
                                Main.Log($"[{roleName}] Position gap closer: {gapCloser.Name} -> near {candidateTarget.CharacterName} (AP:{cost:F1}, MP:{mpCost:F1})");
                                return PlannedAction.PositionalAttack(gapCloser, landingPosition.Value, $"Jump to {candidateTarget.CharacterName}", cost);
                            }
                            else
                            {
                                if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanGapCloser: {gapCloser.Name} -> {candidateTarget.CharacterName} failed: {reason}");
                            }
                        }
                        else
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanGapCloser: {gapCloser.Name} -> {candidateTarget.CharacterName} - no landing position");
                        }
                    }
                    else
                    {
                        // ★ v3.7.88: Unit 타겟 갭클로저 - 실제 경로 검증 (게임 패스파인딩 활용)
                        // 기존 문제: CanUseAbilityOn()이 사거리 초과를 허용하는 경우가 있음
                        // 해결: FindPathChargeTB_Blocking으로 실제 도달 가능 여부 사전 검증

                        // 1. Charge 경로 검증
                        bool hasValidPath = false;
                        try
                        {
                            var agent = situation.Unit.View?.AgentASP;
                            if (agent != null)
                            {
                                var chargePath = PathfindingService.Instance.FindPathChargeTB_Blocking(
                                    agent,
                                    situation.Unit.Position,
                                    candidateTarget.Position,
                                    false,  // ignoreBlockers
                                    candidateTarget  // targetEntity
                                );
                                hasValidPath = chargePath?.path != null && chargePath.path.Count >= 2;

                                if (!hasValidPath)
                                {
                                    if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanGapCloser: {gapCloser.Name} -> {candidateTarget.CharacterName} - NO CHARGE PATH");
                                    continue;
                                }
                            }
                            else
                            {
                                if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanGapCloser: Agent is null, skipping path validation");
                            }
                        }
                        catch (Exception ex)
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanGapCloser: Path validation error: {ex.Message}");
                        }

                        // 2. 기존 검증
                        var targetWrapper = new TargetWrapper(candidateTarget);
                        string reason;
                        if (CombatAPI.CanUseAbilityOn(gapCloser, targetWrapper, out reason))
                        {
                            remainingAP -= cost;
                            // ★ v3.5.34: MP도 차감
                            if (mpCost < float.MaxValue)
                            {
                                remainingMP -= mpCost;
                                if (remainingMP < 0) remainingMP = 0;
                            }
                            Main.Log($"[{roleName}] Gap closer: {gapCloser.Name} -> {candidateTarget.CharacterName} (AP:{cost:F1}, MP:{mpCost:F1}, pathOK={hasValidPath})");
                            return PlannedAction.Attack(gapCloser, candidateTarget, $"Gap closer on {candidateTarget.CharacterName}", cost);
                        }
                        else
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanGapCloser: {gapCloser.Name} -> {candidateTarget.CharacterName} failed: {reason}");
                        }
                    }
                }
            }

            if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanGapCloser: All GapClosers failed on all targets");
            return null;
        }

        /// <summary>
        /// ★ v3.5.34: PlanGapCloser 오버로드 (remainingMP 없는 버전 - 레거시 호환)
        /// </summary>
        public static PlannedAction PlanGapCloser(Situation situation, BaseUnitEntity target, ref float remainingAP, string roleName)
        {
            float remainingMP = situation.CurrentMP;
            return PlanGapCloser(situation, target, ref remainingAP, ref remainingMP, roleName);
        }

        /// <summary>
        /// ★ v3.0.81: 갭클로저 착지 위치 찾기
        /// ★ v3.1.28: 능력 범위 고려 - 스킬 범위 내에서만 착지 위치 선택
        /// ★ v3.6.11: 게임 로직 기반 재구현 - 적 주변 1타일에 착지
        ///
        /// 핵심: DeathFromAbove 등 Point 타겟 GapCloser는 적 위치가 아닌
        /// 적 주변 1타일 바깥의 빈 셀에 착지해야 함
        /// </summary>
        private static Vector3? FindGapCloserLandingPosition(BaseUnitEntity unit, BaseUnitEntity target, AbilityData gapCloserAbility, Situation situation = null)
        {
            // ★ v3.5.98: 능력 범위 확인 (타일 단위)
            float abilityRange = CombatAPI.GetAbilityRangeInTiles(gapCloserAbility);
            if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] FindGapCloserLanding: ability={gapCloserAbility.Name}, range={abilityRange:F1} tiles");

            // ★ v3.5.98: 타일 단위로 변환
            float targetDistance = CombatCache.GetDistanceInTiles(unit, target);

            // ★ v3.5.98: 적이 너무 멀면 갭클로저 사용 안 함
            float meleeAttackRange = 2f;  // 타일
            float maxEffectiveRange = abilityRange + meleeAttackRange;
            if (targetDistance > maxEffectiveRange)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] FindGapCloserLanding: target too far ({targetDistance:F1} > {maxEffectiveRange:F1} tiles), skipping gap closer");
                return null;
            }

            // ★ v3.5.87: 순수 이동형 GapCloser만 착지 후 검증
            if (situation != null)
            {
                float gapCloserDamage = CombatAPI.EstimateDamage(gapCloserAbility, target);
                bool isDamagingGapCloser = gapCloserDamage > 0;

                if (!isDamagingGapCloser)
                {
                    float gapCloserCost = CombatAPI.GetAbilityAPCost(gapCloserAbility);
                    float apAfterLanding = situation.CurrentAP - gapCloserCost;

                    bool hasMeleeAfter = situation.AvailableAttacks != null &&
                        situation.AvailableAttacks.Any(a => a.IsMelee &&
                            CombatAPI.GetAbilityAPCost(a) <= apAfterLanding);
                    if (!hasMeleeAfter && apAfterLanding < 1f)
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] Movement-only GapCloser {gapCloserAbility.Name} skipped - no follow-up attack possible (AP after={apAfterLanding:F1})");
                        return null;
                    }
                }
            }

            // ★ v3.6.11: 게임 로직 기반 - 적 주변 1타일에서 착지 위치 찾기
            // GridAreaHelper.GetNodesSpiralAround 사용
            try
            {
                var targetNode = target.CurrentUnwalkableNode;
                if (targetNode == null)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] FindGapCloserLanding: target has no valid node");
                    return null;
                }

                // 적 주변 1타일의 노드들을 나선형으로 탐색
                var nodesAroundTarget = GridAreaHelper.GetNodesSpiralAround(
                    targetNode,
                    target.SizeRect,
                    1  // ★ 핵심: 적 바로 옆 1타일
                );

                Vector3? bestLandingPos = null;
                float bestDistance = float.MaxValue;

                foreach (var node in nodesAroundTarget)
                {
                    if (node == null || !node.Walkable)
                        continue;

                    // 다른 유닛이 점유 중인지 확인
                    if (node.TryGetUnit(out var occupant) && occupant != null && occupant.IsConscious && occupant != unit)
                        continue;

                    // ★ v3.7.63: BattlefieldGrid 검증 추가
                    if (!BattlefieldGrid.Instance.ValidateNode(unit, node))
                        continue;

                    Vector3 nodePos = node.Vector3Position;

                    // ★ 능력 사거리 체크 (캐스터 → 착지 위치)
                    float distFromCaster = CombatAPI.MetersToTiles(Vector3.Distance(unit.Position, nodePos));
                    if (distFromCaster > abilityRange)
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] Node at ({nodePos.x:F1},{nodePos.z:F1}) out of ability range ({distFromCaster:F1} > {abilityRange:F1})");
                        continue;
                    }

                    // 캐스터에서 가장 가까운 위치 선택
                    if (distFromCaster < bestDistance)
                    {
                        bestDistance = distFromCaster;
                        bestLandingPos = nodePos;
                    }
                }

                if (bestLandingPos.HasValue)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] FindGapCloserLanding: found landing at ({bestLandingPos.Value.x:F1},{bestLandingPos.Value.z:F1}), dist={bestDistance:F1} tiles from caster");
                    return bestLandingPos;
                }

                if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] FindGapCloserLanding: no valid landing position around target");
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] FindGapCloserLanding grid search failed: {ex.Message}");
            }

            // ★ 폴백: MovementAPI 사용 (기존 로직)
            AIRole role = situation?.CharacterSettings?.Role ?? AIRole.Auto;
            // ★ v3.8.50: 근접 AOE 스플래시 보너스 전달
            var bestMeleeAoE = situation?.AvailableAttacks != null
                ? CombatHelpers.GetBestMeleeAoEAbility(situation.AvailableAttacks)
                : null;
            var meleePosition = MovementAPI.FindMeleeAttackPositionSync(
                unit, target, 2f, 0f,
                situation?.InfluenceMap,
                role,
                situation?.PredictiveThreatMap,
                bestMeleeAoE,
                situation?.Enemies);

            if (meleePosition != null)
            {
                float distToLandingTiles = CombatAPI.MetersToTiles(Vector3.Distance(unit.Position, meleePosition.Position));
                if (distToLandingTiles <= abilityRange)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] FindGapCloserLanding (fallback): melee position at dist={distToLandingTiles:F1} tiles");
                    return meleePosition.Position;
                }
            }

            if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] FindGapCloserLanding: all methods failed");
            return null;
        }

        /// <summary>
        /// 적에게 이동
        /// ★ v3.1.00: bypassCanMoveCheck 파라미터 추가
        /// ★ v3.1.01: predictedMP 파라미터 추가 - MovementAPI에 전달
        /// ★ v3.2.25: Role 추출하여 MovementAPI에 전달 - Frontline 기반 위치 점수
        /// ★ v3.5.18: tacticalTarget 파라미터 추가 - SharedTarget/BestTarget 우선 이동
        /// </summary>
        /// ★ v3.8.44: AttackPhaseContext 파라미터 추가
        public static PlannedAction PlanMoveToEnemy(Situation situation, string roleName, bool bypassCanMoveCheck = false, float predictedMP = 0f, BaseUnitEntity tacticalTarget = null, AttackPhaseContext attackContext = null)
        {
            bool isChaseMove = false;

            if (situation.HasMovedThisTurn)
            {
                if (situation.AllowPostAttackMove)
                {
                    Main.Log($"[{roleName}] PlanMoveToEnemy: Post-attack move allowed");
                    isChaseMove = true;
                }
                else if (situation.AllowChaseMove)
                {
                    Main.Log($"[{roleName}] PlanMoveToEnemy: Chase move allowed");
                    isChaseMove = true;
                }
                else
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanMoveToEnemy: Already moved this turn, skipping");
                    return null;
                }
            }

            if (isChaseMove)
            {
                // ★ v3.1.01: predictedMP가 있으면 chase move 허용
                if (situation.CurrentMP <= 0 && predictedMP <= 0)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanMoveToEnemy: Chase move blocked - no MP (predictedMP={predictedMP:F1})");
                    return null;
                }
            }
            else
            {
                // ★ v3.1.00: bypassCanMoveCheck=true면 CanMove 체크 스킵
                // MP 회복 능력(무모한 돌진 등) 계획 후 예측 MP로 이동 가능할 때 사용
                if (!bypassCanMoveCheck && !situation.CanMove)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanMoveToEnemy: CanMove=false, skipping");
                    return null;
                }
            }

            if (situation.NearestEnemy == null) return null;

            var unit = situation.Unit;
            // ★ v3.5.18: tacticalTarget이 있으면 사용, 없으면 NearestEnemy
            var target = tacticalTarget ?? situation.NearestEnemy;

            // ★ v3.1.01: 실제 MP와 예측 MP 중 큰 값 사용
            float effectiveMP = Math.Max(situation.CurrentMP, predictedMP);

            // ★ v3.2.25: Role 추출 (Frontline 점수 적용용)
            AIRole role = situation.CharacterSettings?.Role ?? AIRole.Auto;
            if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanMoveToEnemy: effectiveMP={effectiveMP:F1}, role={role}");

            if (situation.PrefersRanged)
            {
                // ★ v3.0.73: MovementAPI 기반 타일 스코어링 사용
                // 기존: 단순 벡터 계산 (적에게 3m 접근) → 위험!
                // 수정: 엄폐, 안전거리, LOS 등 종합 점수화

                // ★ v3.9.24: 능력 사거리 우선, WeaponRangeProfile 폴백
                float weaponRange = GetEffectiveRange(situation, attackContext);

                // ★ v3.1.01: predictedMP 전달
                // ★ v3.2.00: influenceMap 전달
                // ★ v3.2.25: role 전달 (Frontline 점수)
                // ★ v3.4.00: predictiveMap 전달 (적 이동 예측)
                var bestPosition = MovementAPI.FindRangedAttackPositionSync(
                    unit,
                    situation.Enemies,
                    weaponRange,
                    situation.MinSafeDistance,
                    effectiveMP,
                    situation.InfluenceMap,
                    role,
                    situation.PredictiveThreatMap
                );

                // ★ v3.8.47: HittableEnemyCount가 0이면 유효한 공격 위치가 아님
                // 넓은 맵에서 LOS-only 폴백으로 현재 위치 근처가 반환되면
                // 적에게 접근하지 못하고 계속 멈춰있는 문제 수정
                if (bestPosition != null && bestPosition.HittableEnemyCount == 0)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanMoveToEnemy: Best position has no hittable enemies (HittableEnemyCount=0) - using approach fallback");
                    bestPosition = null;
                }

                if (bestPosition == null)
                {
                    // ★ v3.8.45: 원거리 캐릭터 접근 폴백 안전 체크
                    // FindRangedAttackPositionSync가 null = 안전한 공격 위치 없음
                    // Case 1: 적이 사거리 내 → 안전 위치 없으니 이동하지 않음 (접근은 악화)
                    // Case 2: 적이 사거리 밖 → 접근하되 MinSafeDistance 이내로는 접근 금지
                    if (situation.PrefersRanged)
                    {
                        float nearestEnemyTiles = CombatCache.GetDistanceInTiles(unit, target);
                        if (nearestEnemyTiles <= weaponRange)
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanMoveToEnemy: Enemy in range ({nearestEnemyTiles:F1} <= {weaponRange:F1}) but no safe position - staying put");
                            return null;
                        }

                        // 사거리 밖 → 접근하되 안전 거리 유지
                        var safeApproach = MovementAPI.FindBestApproachPosition(unit, target, effectiveMP);
                        if (safeApproach != null)
                        {
                            float approachDistToEnemy = CombatAPI.MetersToTiles(
                                Vector3.Distance(safeApproach.Position, target.Position));
                            if (approachDistToEnemy < situation.MinSafeDistance)
                            {
                                if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanMoveToEnemy: Approach cancelled - would enter danger zone ({approachDistToEnemy:F1} < MinSafe={situation.MinSafeDistance:F1})");
                                return null;
                            }
                            Main.Log($"[{roleName}] PlanMoveToEnemy: Safe approach ({approachDistToEnemy:F1} tiles from enemy)");
                            return PlannedAction.Move(safeApproach.Position, $"Safe approach {target.CharacterName}");
                        }
                        if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanMoveToEnemy: No safe ranged position found (effectiveMP={effectiveMP:F1})");
                        return null;
                    }

                    // 근접 캐릭터: 기존 로직 유지 (안전 거리 불필요)
                    var fallbackPosition = MovementAPI.FindBestApproachPosition(
                        unit, target, effectiveMP);

                    if (fallbackPosition != null)
                    {
                        Main.Log($"[{roleName}] PlanMoveToEnemy: No attack position, fallback to approach ({fallbackPosition.Position.x:F1},{fallbackPosition.Position.z:F1})");
                        return PlannedAction.Move(fallbackPosition.Position, $"Approach {target.CharacterName}");
                    }

                    if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanMoveToEnemy: No safe ranged position found (effectiveMP={effectiveMP:F1})");
                    return null;
                }

                // 현재 위치와 거의 같으면 이동 불필요
                float moveDistance = Vector3.Distance(unit.Position, bestPosition.Position);
                if (moveDistance < 1f)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanMoveToEnemy: Already at optimal position");
                    return null;
                }

                Main.Log($"[{roleName}] Safe ranged position: ({bestPosition.Position.x:F1},{bestPosition.Position.z:F1}) " +
                    $"score={bestPosition.TotalScore:F1}, cover={bestPosition.BestCover}");
                return PlannedAction.Move(bestPosition.Position, $"Safe attack position");
            }
            else
            {
                // ★ v3.0.74: 근접 캐릭터도 MovementAPI 기반 타일 스코어링 사용
                // 기존: target.Position (적의 점유된 타일) → 도달 불가
                // 수정: 적에게 인접한 공격 가능 타일 찾기

                float meleeRange = 2f;  // 기본 근접 사거리
                try
                {
                    var primaryHand = unit.Body?.PrimaryHand;
                    if (primaryHand?.HasWeapon == true && primaryHand.Weapon.Blueprint.IsMelee)
                    {
                        int attackRange = primaryHand.Weapon.AttackRange;
                        if (attackRange > 0 && attackRange < 100)
                            meleeRange = attackRange;
                    }
                }
                catch (Exception ex) { if (Main.IsDebugEnabled) Main.LogDebug($"[MovePlanner] {ex.Message}"); }

                // ★ v3.1.01: predictedMP 전달
                // ★ v3.2.00: influenceMap 전달
                // ★ v3.2.25: role 전달 (Frontline 점수)
                // ★ v3.4.00: predictiveMap 전달 (적 이동 예측)
                // ★ v3.8.50: 근접 AOE 스플래시 보너스 전달
                var bestMeleeAoEForMove = CombatHelpers.GetBestMeleeAoEAbility(situation.AvailableAttacks);
                var bestPosition = MovementAPI.FindMeleeAttackPositionSync(
                    unit,
                    target,
                    meleeRange,
                    effectiveMP,
                    situation.InfluenceMap,
                    role,
                    situation.PredictiveThreatMap,
                    bestMeleeAoEForMove,
                    situation.Enemies
                );

                if (bestPosition == null)
                {
                    // ★ v3.9.52: FindBestApproachPosition 사용 (벽 뒤 적에게 A* 경로 기반 우회 접근)
                    // 게임 네이티브 AI처럼 도달 가능한 셀 중 타겟에 가장 가까운 위치로 이동
                    var approachPosition = MovementAPI.FindBestApproachPosition(unit, target, effectiveMP);
                    if (approachPosition != null)
                    {
                        Main.Log($"[{roleName}] PlanMoveToEnemy: No melee position, approach via pathfinding ({approachPosition.Position.x:F1},{approachPosition.Position.z:F1})");
                        return PlannedAction.Move(approachPosition.Position, $"Approach {target.CharacterName}");
                    }

                    // 최후 폴백: 적 위치 직접 사용 (FindBestApproachPosition도 실패한 경우)
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanMoveToEnemy: No approach position found, falling back to target position");
                    return PlannedAction.Move(target.Position, $"Approach {target.CharacterName}");
                }

                // 현재 위치와 거의 같으면 이동 불필요
                float moveDistance = Vector3.Distance(unit.Position, bestPosition.Position);
                if (moveDistance < 1f)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanMoveToEnemy: Already at melee position");
                    return null;
                }

                Main.Log($"[{roleName}] Melee attack position: ({bestPosition.Position.x:F1},{bestPosition.Position.z:F1}) " +
                    $"score={bestPosition.TotalScore:F1}");
                return PlannedAction.Move(bestPosition.Position, $"Melee position near {target.CharacterName}");
            }
        }

        /// <summary>
        /// 후퇴 (원거리 캐릭터가 적과 너무 가까울 때)
        /// ★ v3.0.61: 현재 위치가 이미 안전하면 이동 불필요
        /// ★ v3.2.25: role 전달 (Frontline 점수)
        /// ★ v3.7.11: 무기 사거리 기반 최대 후퇴 거리 제한 (공격 가능 거리 유지)
        /// ★ v3.8.23: SoldierDash 등 후퇴용 대시 능력 지원
        /// </summary>
        public static PlannedAction PlanRetreat(Situation situation)
        {
            if (situation.HasMovedThisTurn) return null;
            if (!situation.CanMove) return null;

            var unit = situation.Unit;
            var nearestEnemy = situation.NearestEnemy;
            if (nearestEnemy == null) return null;

            // ★ v3.0.61: 현재 위치가 이미 안전 거리 이상이면 후퇴 불필요
            if (situation.NearestEnemyDistance >= situation.MinSafeDistance)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] {unit.CharacterName}: Already safe, no retreat needed");
                return null;
            }

            // ★ v3.8.23: 후퇴용 대시 능력 먼저 확인 (SoldierDash 등)
            // 대시는 걷기보다 더 멀리, 더 안전하게 후퇴 가능
            var dashRetreatAction = PlanRetreatWithDash(situation);
            if (dashRetreatAction != null)
            {
                Main.Log($"[MovementPlanner] {unit.CharacterName}: Retreating with dash ability");
                return dashRetreatAction;
            }

            // ★ v3.2.25: Role 추출
            AIRole role = situation.CharacterSettings?.Role ?? AIRole.Auto;

            // ★ v3.9.24: 중앙집중 무기 사거리 프로필 사용
            float weaponRangeTiles = situation.WeaponRange.EffectiveRange;
            float maxSafeDistance = situation.WeaponRange.MaxRetreatDistance;
            // ★ v3.9.24: 단거리 무기 최소 후퇴 거리 하한선 (Scatter 제외)
            if (maxSafeDistance < 2f && !situation.WeaponRange.IsScatter)
                maxSafeDistance = 2f;
            Main.Log($"[MovementPlanner] {unit.CharacterName}: Retreat range check - WeaponRange={weaponRangeTiles:F1}, MinSafe={situation.MinSafeDistance:F1}, MaxSafe={maxSafeDistance:F1}");

            // ★ v3.7.04: 사역마 거리 제약 계산
            // ★ v3.7.90: 고정 15m → 동적 사역마 스킬 사거리 기반으로 변경
            // Servo-Skull/Raven은 버프 시전 거리 내에 있어야 함
            UnityEngine.Vector3? familiarPos = null;
            float maxFamiliarDist = 0f;
            if (situation.HasFamiliar && situation.Familiar != null &&
                (situation.FamiliarType == Kingmaker.Enums.PetType.ServoskullSwarm ||
                 situation.FamiliarType == Kingmaker.Enums.PetType.Raven))
            {
                familiarPos = situation.FamiliarPosition;
                // ★ v3.7.90: 마스터의 사역마 대상 능력 최대 사거리 동적 계산
                maxFamiliarDist = FamiliarAPI.GetMaxFamiliarAbilityRange(unit);
                if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] {unit.CharacterName}: Retreat with familiar constraint (max {maxFamiliarDist:F1}m from familiar)");
            }

            // ★ v3.0.60: MovementAPI 기반 실제 도달 가능한 타일 사용
            // ★ v3.2.00: influenceMap 전달
            // ★ v3.2.25: role 전달 (Frontline 점수)
            // ★ v3.4.00: predictiveMap 전달 (적 이동 예측)
            // ★ v3.7.04: familiarPos 전달 (사역마 거리 제약)
            // ★ v3.7.11: maxSafeDistance 전달 (무기 사거리 기반)
            var retreatScore = MovementAPI.FindRetreatPositionSync(
                unit,
                situation.Enemies,
                situation.MinSafeDistance,
                maxSafeDistance,
                0f,
                situation.InfluenceMap,
                role,
                situation.PredictiveThreatMap,
                familiarPos,
                maxFamiliarDist
            );

            if (retreatScore == null)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] {unit.CharacterName}: No reachable retreat position");
                return null;
            }

            return PlannedAction.Move(retreatScore.Position, $"Retreat from {nearestEnemy.CharacterName}");
        }

        /// <summary>
        /// ★ v3.8.23: 대시 능력을 사용한 후퇴 계획
        /// SoldierDash 등 IsRetreatCapable 플래그가 있는 능력 사용
        /// - 걷기보다 더 멀리 후퇴 가능
        /// - IgnoreEnemies, DisableAttacksOfOpportunity로 안전
        /// </summary>
        private static PlannedAction PlanRetreatWithDash(Situation situation)
        {
            var unit = situation.Unit;

            // 후퇴 가능한 대시 능력 찾기
            var retreatDashes = situation.AvailableAttacks?
                .Where(a => AbilityDatabase.IsRetreatCapable(a))
                .Where(a => AbilityDatabase.IsGapCloser(a))  // GapCloser여야 이동 가능
                .ToList();

            if (retreatDashes == null || retreatDashes.Count == 0)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] {unit.CharacterName}: No retreat-capable dash abilities");
                return null;
            }

            if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] {unit.CharacterName}: Found {retreatDashes.Count} retreat dash(es): {string.Join(", ", retreatDashes.Select(d => d.Name))}");

            foreach (var dashAbility in retreatDashes)
            {
                float apCost = CombatAPI.GetAbilityAPCost(dashAbility);
                if (apCost > situation.CurrentAP)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] {unit.CharacterName}: {dashAbility.Name} skipped - AP cost {apCost:F1} > current {situation.CurrentAP:F1}");
                    continue;
                }

                var info = AbilityDatabase.GetInfo(dashAbility);
                bool isPointTarget = info != null && (info.Flags & AbilityFlags.PointTarget) != 0;

                if (!isPointTarget)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] {unit.CharacterName}: {dashAbility.Name} skipped - not PointTarget");
                    continue;
                }

                // 대시 능력의 범위 내에서 안전한 후퇴 위치 찾기
                var landingPosition = FindRetreatDashLandingPosition(situation, dashAbility);
                if (landingPosition == null)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] {unit.CharacterName}: {dashAbility.Name} - no safe landing position");
                    continue;
                }

                // 후퇴 위치가 현재 위치보다 안전한지 확인
                float currentDistToEnemy = situation.NearestEnemyDistance;
                float newDistToEnemy = Vector3.Distance(landingPosition.Value, situation.NearestEnemy.Position);

                if (newDistToEnemy <= currentDistToEnemy)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] {unit.CharacterName}: {dashAbility.Name} landing not safer (current={currentDistToEnemy:F1}, new={newDistToEnemy:F1})");
                    continue;
                }

                // 대시 사용 가능 여부 최종 확인
                var pointTarget = new TargetWrapper(landingPosition.Value);
                string reason;
                if (!CombatAPI.CanUseAbilityOn(dashAbility, pointTarget, out reason))
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] {unit.CharacterName}: {dashAbility.Name} cannot use: {reason}");
                    continue;
                }

                Main.Log($"[MovementPlanner] {unit.CharacterName}: Retreat dash {dashAbility.Name} to ({landingPosition.Value.x:F1},{landingPosition.Value.z:F1}), " +
                    $"distance {currentDistToEnemy:F1}m → {newDistToEnemy:F1}m (AP:{apCost:F1})");

                return PlannedAction.PositionalAttack(dashAbility, landingPosition.Value, $"Dash retreat from {situation.NearestEnemy.CharacterName}", apCost);
            }

            return null;
        }

        /// <summary>
        /// ★ v3.8.23: 후퇴 대시의 착지 위치 찾기
        /// 적으로부터 멀어지면서 대시 범위 내의 안전한 위치 탐색
        /// </summary>
        private static Vector3? FindRetreatDashLandingPosition(Situation situation, AbilityData dashAbility)
        {
            var unit = situation.Unit;
            var nearestEnemy = situation.NearestEnemy;
            if (nearestEnemy == null) return null;

            // 대시 능력의 범위 (타일 단위)
            float dashRange = CombatAPI.GetAbilityRangeInTiles(dashAbility);
            if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] FindRetreatDashLanding: {dashAbility.Name} range={dashRange:F1} tiles");

            // ★ v3.9.24: 중앙집중 무기 사거리 프로필 사용 (후퇴 후에도 공격 가능해야 함)
            float weaponRangeTiles = situation.WeaponRange.EffectiveRange;

            // 적으로부터 반대 방향 계산
            Vector3 retreatDirection = (unit.Position - nearestEnemy.Position).normalized;

            // 그리드 기반 탐색 - 대시 범위 내에서 가장 안전한 위치 찾기
            try
            {
                var unitNode = unit.CurrentUnwalkableNode;
                if (unitNode == null)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] FindRetreatDashLanding: unit has no valid node");
                    return null;
                }

                // 대시 범위 내의 노드들을 나선형으로 탐색
                int searchRadius = Mathf.CeilToInt(dashRange);
                var nodesInRange = GridAreaHelper.GetNodesSpiralAround(
                    unitNode,
                    unit.SizeRect,
                    searchRadius
                );

                Vector3? bestPosition = null;
                float bestScore = float.MinValue;

                foreach (var node in nodesInRange)
                {
                    if (node == null || !node.Walkable)
                        continue;

                    // 다른 유닛이 점유 중인지 확인
                    if (node.TryGetUnit(out var occupant) && occupant != null && occupant.IsConscious && occupant != unit)
                        continue;

                    // BattlefieldGrid 검증
                    if (!BattlefieldGrid.Instance.ValidateNode(unit, node))
                        continue;

                    Vector3 nodePos = node.Vector3Position;

                    // 대시 범위 내인지 확인
                    float distFromUnit = CombatAPI.MetersToTiles(Vector3.Distance(unit.Position, nodePos));
                    if (distFromUnit > dashRange || distFromUnit < 0.5f)
                        continue;

                    // 적으로부터의 거리
                    float distFromEnemy = Vector3.Distance(nodePos, nearestEnemy.Position);
                    float distFromEnemyTiles = CombatAPI.MetersToTiles(distFromEnemy);

                    // 무기 사거리보다 멀면 공격 불가 → 스킵
                    if (distFromEnemyTiles > weaponRangeTiles)
                        continue;

                    // 점수 계산: 적으로부터 멀수록 + 후퇴 방향일수록 좋음
                    Vector3 toNode = (nodePos - unit.Position).normalized;
                    float directionScore = Vector3.Dot(toNode, retreatDirection);  // -1 ~ 1

                    // 최종 점수 = 적으로부터 거리 + 방향 보너스
                    float score = distFromEnemy + (directionScore * 3f);

                    // ★ v3.8.76: 공격 가능 적 수 보너스 (후퇴해도 공격 가능한 위치 선호)
                    int hittable = CombatAPI.CountHittableEnemiesFromPosition(
                        unit, node, situation.Enemies);
                    if (hittable > 0)
                        score += hittable * 5f;
                    else
                        score -= 8f;  // LOS 없는 위치 패널티

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestPosition = nodePos;
                    }
                }

                if (bestPosition.HasValue)
                {
                    float bestDistFromEnemy = Vector3.Distance(bestPosition.Value, nearestEnemy.Position);
                    if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] FindRetreatDashLanding: best position at ({bestPosition.Value.x:F1},{bestPosition.Value.z:F1}), " +
                        $"dist from enemy={bestDistFromEnemy:F1}m, score={bestScore:F1}");
                }

                return bestPosition;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] FindRetreatDashLanding grid search failed: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// ★ v3.0.60: 행동 완료 후 안전 후퇴 (MovementAPI 기반)
        /// ★ v3.0.61: 현재 위치가 이미 안전하면 이동 불필요
        /// ★ v3.2.25: role 전달 (Frontline 점수)
        /// ★ v3.7.11: 무기 사거리 기반 최대 후퇴 거리 제한 (공격 가능 거리 유지)
        /// </summary>
        public static PlannedAction PlanPostActionSafeRetreat(Situation situation)
        {
            if (!situation.CanMove) return null;
            if (situation.CurrentMP <= 0) return null;

            var unit = situation.Unit;
            var nearestEnemy = situation.NearestEnemy;
            if (nearestEnemy == null) return null;

            // ★ v3.0.61: 현재 위치가 이미 안전 거리 이상이면 이동 불필요
            if (situation.NearestEnemyDistance >= situation.MinSafeDistance)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] {unit.CharacterName}: Already safe (dist={situation.NearestEnemyDistance:F1}m >= {situation.MinSafeDistance}m), no retreat needed");
                return null;
            }

            // ★ v3.2.25: Role 추출
            AIRole role = situation.CharacterSettings?.Role ?? AIRole.Auto;

            // ★ v3.9.24: 중앙집중 무기 사거리 프로필 사용
            float weaponRangeTiles = situation.WeaponRange.EffectiveRange;
            float maxSafeDistance = situation.WeaponRange.MaxRetreatDistance;
            // ★ v3.9.24: 단거리 무기 최소 후퇴 거리 하한선 (Scatter 제외)
            if (maxSafeDistance < 2f && !situation.WeaponRange.IsScatter)
                maxSafeDistance = 2f;

            // ★ v3.7.04: 사역마 거리 제약 계산
            // ★ v3.7.90: 고정 15m → 동적 사역마 스킬 사거리 기반
            UnityEngine.Vector3? familiarPos = null;
            float maxFamiliarDist = 0f;
            if (situation.HasFamiliar && situation.Familiar != null &&
                (situation.FamiliarType == Kingmaker.Enums.PetType.ServoskullSwarm ||
                 situation.FamiliarType == Kingmaker.Enums.PetType.Raven))
            {
                familiarPos = situation.FamiliarPosition;
                maxFamiliarDist = FamiliarAPI.GetMaxFamiliarAbilityRange(unit);
            }

            // ★ v3.0.60: PathfindingService 기반 실제 도달 가능 위치
            // ★ v3.2.00: influenceMap 전달
            // ★ v3.2.25: role 전달 (Frontline 점수)
            // ★ v3.4.00: predictiveMap 전달 (적 이동 예측)
            // ★ v3.7.04: familiarPos 전달 (사역마 거리 제약)
            // ★ v3.7.11: maxSafeDistance 전달 (무기 사거리 기반)
            var retreatScore = MovementAPI.FindRetreatPositionSync(
                unit,
                situation.Enemies,
                situation.MinSafeDistance,
                maxSafeDistance,
                0f,
                situation.InfluenceMap,
                role,
                situation.PredictiveThreatMap,
                familiarPos,
                maxFamiliarDist
            );

            if (retreatScore == null)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] {unit.CharacterName}: No reachable safe retreat position");
                return null;
            }

            // ★ v3.0.61: 최적 위치가 현재 위치보다 충분히 좋은지 확인
            float currentDistToEnemy = situation.NearestEnemyDistance;
            float newDistToEnemy = Vector3.Distance(retreatScore.Position, nearestEnemy.Position);

            // 이동 후 거리가 현재보다 최소 2m 이상 멀어지지 않으면 이동 가치 없음
            if (newDistToEnemy < currentDistToEnemy + 2f)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] {unit.CharacterName}: Retreat not worth it (current={currentDistToEnemy:F1}m, after={newDistToEnemy:F1}m)");
                return null;
            }

            return PlannedAction.Move(retreatScore.Position, $"Safe retreat from {nearestEnemy.CharacterName}");
        }

        /// <summary>
        /// 후퇴 필요 여부 확인
        /// </summary>
        public static bool ShouldRetreat(Situation situation)
        {
            var rangePreference = situation.RangePreference;
            if (rangePreference != Settings.RangePreference.PreferRanged)
                return false;

            return situation.NearestEnemyDistance < situation.MinSafeDistance;
        }

        /// <summary>
        /// ★ v3.8.74: Tactical Reposition — 공격 쿨다운 시 안전 위치로 재배치
        ///
        /// PlanPostActionSafeRetreat와의 차이:
        /// - PlanPostActionSafeRetreat: "Already safe" 가드 있음 (NearestEnemyDist >= MinSafe면 거부)
        /// - PlanTacticalReposition: "Already safe" 가드 없음 (공격 불가 = 최대한 안전하게)
        ///
        /// FindRetreatPositionSync 사용 (FindRangedAttackPositionSync 아님!):
        /// - 적에게서 최대한 멀어지되, 무기 사거리(weaponRange-1) 내 유지
        /// - 후퇴 방향 보너스 + 엄폐 보너스 + 위협 점수
        /// - 적에게 절대 접근하지 않음
        /// </summary>
        public static PlannedAction PlanTacticalReposition(Situation situation, float remainingMP)
        {
            if (!situation.PrefersRanged) return null;
            if (remainingMP <= 0) return null;

            var unit = situation.Unit;
            if (situation.NearestEnemy == null) return null;

            // ★ v3.9.24: 중앙집중 무기 사거리 프로필 사용
            float weaponRange = situation.WeaponRange.EffectiveRange;
            float maxSafeDistance = situation.WeaponRange.MaxRetreatDistance;
            if (maxSafeDistance < 2f && !situation.WeaponRange.IsScatter)
                maxSafeDistance = 2f;
            AIRole role = situation.CharacterSettings?.Role ?? AIRole.Auto;

            // ★ 핵심: FindRetreatPositionSync 사용 — 안전 최대화 (공격 위치가 아님!)
            // PlanPostActionSafeRetreat의 "Already safe" 가드를 거치지 않고 직접 호출
            // 이유: 공격 불가 상태에서는 MinSafeDistance가 아닌 maxSafeDistance까지 후퇴가 이득

            // 사역마 거리 제약 계산
            UnityEngine.Vector3? familiarPos = null;
            float maxFamiliarDist = 0f;
            if (situation.HasFamiliar && situation.Familiar != null &&
                (situation.FamiliarType == Kingmaker.Enums.PetType.ServoskullSwarm ||
                 situation.FamiliarType == Kingmaker.Enums.PetType.Raven))
            {
                familiarPos = situation.FamiliarPosition;
                maxFamiliarDist = FamiliarAPI.GetMaxFamiliarAbilityRange(unit);
            }

            var bestPosition = MovementAPI.FindRetreatPositionSync(
                unit, situation.Enemies, situation.MinSafeDistance, maxSafeDistance,
                remainingMP, situation.InfluenceMap, role, situation.PredictiveThreatMap,
                familiarPos, maxFamiliarDist);

            if (bestPosition == null) return null;

            // 현재 위치와 거의 같으면 이동 불필요
            float moveDistance = Vector3.Distance(unit.Position, bestPosition.Position);
            if (moveDistance < 2f)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] TacticalReposition: Already at good position ({moveDistance:F1}m)");
                return null;
            }

            Main.Log($"[MovementPlanner] TacticalReposition: ({bestPosition.Position.x:F1},{bestPosition.Position.z:F1}), " +
                $"score={bestPosition.TotalScore:F1}, move={moveDistance:F1}m, cover={bestPosition.CoverScore:F1}");

            return PlannedAction.Move(bestPosition.Position, "Tactical reposition (cooldown)");
        }

        #region Helper Methods

        // ★ v3.9.24: GetWeaponRange() 삭제 — CombatAPI.GetWeaponRangeProfile()로 중앙집중화

        /// <summary>
        /// ★ v3.8.44: 유효 사거리 결정 (공격 Phase 컨텍스트 우선)
        /// ★ v3.9.24: 폴백을 중앙집중 WeaponRangeProfile로 변경
        /// 1순위: AttackPhaseContext의 능력 사거리 (정확)
        /// 2순위: Situation.WeaponRange.EffectiveRange (중앙집중)
        /// </summary>
        private static float GetEffectiveRange(Situation situation, AttackPhaseContext attackContext)
        {
            float range;

            if (attackContext?.HasValidRange == true)
            {
                range = attackContext.BestAbilityRange;
                if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] GetEffectiveRange: ability={range:F1} (from context)");
            }
            else
            {
                // ★ v3.9.56: BlendedAttackRange 우선 (모든 유한 사거리 스킬 고려)
                range = situation.BlendedAttackRange > 0
                    ? situation.BlendedAttackRange
                    : situation.WeaponRange.EffectiveRange;
                if (range <= 0f) range = 15f;  // 안전 폴백
            }

            // ★ v3.9.74: 무기 로테이션 활성 시 짧은 사거리 무기 기준 포지셔닝
            // 유저가 로테이션을 켰다면 양쪽 무기 모두 사용할 의도
            // → 짧은 사거리 무기 기준으로 이동 (긴 사거리 무기는 가까이서도 사용 가능)
            // ★ v3.9.78: 동일 타입(원거리+원거리, 근접+근접)에만 적용
            // 혼합 타입(원거리+근접)은 현재 무기 사거리 유지 — 원거리 캐릭이 근접 거리로 돌진 방지
            if (situation.WeaponRotationAvailable && situation.WeaponSetData != null)
            {
                int currentIdx = situation.CurrentWeaponSetIndex;
                int altIdx = currentIdx == 0 ? 1 : 0;
                if (altIdx < situation.WeaponSetData.Length && currentIdx < situation.WeaponSetData.Length)
                {
                    var currentSet = situation.WeaponSetData[currentIdx];
                    var altSet = situation.WeaponSetData[altIdx];
                    float altRange = altSet.PrimaryWeaponRange;

                    // 동일 타입일 때만 짧은 사거리 적용 (볼터+화염방사기 등)
                    bool bothRanged = currentSet.HasRangedWeapon && altSet.HasRangedWeapon;
                    bool bothMelee = currentSet.HasMeleeWeapon && altSet.HasMeleeWeapon;
                    if ((bothRanged || bothMelee) && altRange > 0 && altRange < range)
                    {
                        float original = range;
                        range = altRange;
                        if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] GetEffectiveRange: rotation → {original:F1} → {range:F1} " +
                            $"(same-type shorter weapon range={altRange:F0})");
                    }
                }
            }

            if (Main.IsDebugEnabled) Main.LogDebug($"[MovementPlanner] GetEffectiveRange: {range:F1} " +
                $"(blended={situation.BlendedAttackRange:F1}, weapon={situation.WeaponRange.EffectiveRange:F1})");
            return range;
        }

        private static Vector3 CalculateAveragePosition(IEnumerable<BaseUnitEntity> units)
        {
            var list = units.ToList();
            if (list.Count == 0) return Vector3.zero;

            Vector3 sum = Vector3.zero;
            foreach (var unit in list)
            {
                sum += unit.Position;
            }
            return sum / list.Count;
        }

        #endregion
    }
}
