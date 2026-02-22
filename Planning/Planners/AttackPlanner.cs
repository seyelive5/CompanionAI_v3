using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.Utility;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// ★ v3.0.47: 공격 관련 계획 담당
    /// - 일반 공격, 마무리, 특수 능력, 타겟 선택
    /// </summary>
    public static class AttackPlanner
    {
        // ★ v3.9.10: Zero-alloc 정적 공유 리스트 (new List<> 제거)
        private static readonly List<BaseUnitEntity> _sharedCandidateTargets = new List<BaseUnitEntity>(16);
        private static readonly List<AbilityData> _sharedAbilityList = new List<AbilityData>(8);

        /// <summary>
        /// 공격 계획
        /// ★ v3.5.11: 상세 로깅 추가 (공격 실패 원인 진단용)
        /// </summary>
        /// ★ v3.8.44: AttackPhaseContext 지원 오버로드
        public static PlannedAction PlanAttack(Situation situation, ref float remainingAP,
            string roleName, BaseUnitEntity preferTarget = null,
            HashSet<string> excludeTargetIds = null, HashSet<string> excludeAbilityGuids = null,
            AttackPhaseContext context = null)
        {
            // ★ v3.9.10: new List<> 제거 → 정적 리스트 재사용
            _sharedCandidateTargets.Clear();
            var candidateTargets = _sharedCandidateTargets;

            if (preferTarget != null && !IsExcluded(preferTarget, excludeTargetIds))
                candidateTargets.Add(preferTarget);

            if (situation.BestTarget != null && !candidateTargets.Contains(situation.BestTarget) && !IsExcluded(situation.BestTarget, excludeTargetIds))
                candidateTargets.Add(situation.BestTarget);

            foreach (var hittable in situation.HittableEnemies)
            {
                if (hittable != null && !candidateTargets.Contains(hittable) && !IsExcluded(hittable, excludeTargetIds))
                    candidateTargets.Add(hittable);
            }

            if (situation.NearestEnemy != null && !candidateTargets.Contains(situation.NearestEnemy) && !IsExcluded(situation.NearestEnemy, excludeTargetIds))
                candidateTargets.Add(situation.NearestEnemy);

            if (candidateTargets.Count == 0)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanAttack: No candidate targets");
                return null;
            }

            // ★ v3.5.11: 각 타겟별 실패 원인 추적
            int attackNullCount = 0;
            int apInsufficientCount = 0;
            int canUseFailedCount = 0;

            foreach (var target in candidateTargets)
            {
                var attack = SelectBestAttack(situation, target, excludeAbilityGuids, context);
                if (attack == null)
                {
                    attackNullCount++;
                    continue;
                }

                // ★ v3.6.14: bonus usage면 0 AP로 처리
                float cost = CombatAPI.GetEffectiveAPCost(attack);
                if (cost > remainingAP)
                {
                    apInsufficientCount++;
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanAttack: {attack.Name} too expensive ({cost:F1} > {remainingAP:F1} AP)");
                    continue;
                }

                var targetWrapper = new TargetWrapper(target);
                string reason;
                if (CombatAPI.CanUseAbilityOn(attack, targetWrapper, out reason))
                {
                    remainingAP -= cost;
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] Attack: {attack.Name} -> {target.CharacterName}");
                    return PlannedAction.Attack(attack, target, $"Attack with {attack.Name}", cost);
                }
                else
                {
                    // ★ v3.9.28: 이동이 계획된 상태에서 CanUseAbilityOn 실패 시
                    // RecalculateHittableFromDestination이 목적지 기준으로 이미 검증한 타겟이면
                    // 현재 위치 기준 사거리 실패를 우회하여 공격 계획
                    if (context != null && context.HasPendingMove && situation.HittableEnemies.Contains(target))
                    {
                        remainingAP -= cost;
                        if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] Attack (pending move bypass): {attack.Name} -> {target.CharacterName} (was: {reason})");
                        return PlannedAction.Attack(attack, target, $"Attack with {attack.Name}", cost);
                    }

                    canUseFailedCount++;
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanAttack: CanUseAbility failed for {attack.Name} -> {target.CharacterName} ({reason})");
                }
            }

            // ★ v3.5.11: 전체 실패 요약
            if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanAttack failed: {candidateTargets.Count} targets checked - " +
                $"SelectBestAttack null={attackNullCount}, AP insufficient={apInsufficientCount}, CanUse failed={canUseFailedCount}");

            return null;
        }

        /// <summary>
        /// 최적 공격 선택 (Utility 스코어링 기반)
        /// ★ v3.5.11: 상세 로깅 추가 (공격 실패 원인 진단용)
        /// ★ v3.7.89: AOO (기회공격) 회피 로직 추가
        /// ★ v3.8.44: AttackPhaseContext 지원 - 실패 이유를 이동 Phase에 전달
        /// </summary>
        public static AbilityData SelectBestAttack(Situation situation, BaseUnitEntity target, HashSet<string> excludeAbilityGuids = null)
            => SelectBestAttack(situation, target, excludeAbilityGuids, null);

        /// <summary>
        /// ★ v3.8.44: AttackPhaseContext를 받는 오버로드
        /// context != null이면 실패 이유/능력 사거리를 기록하여 MovementPlanner가 활용
        /// </summary>
        public static AbilityData SelectBestAttack(Situation situation, BaseUnitEntity target, HashSet<string> excludeAbilityGuids, AttackPhaseContext context)
        {
            if (situation.AvailableAttacks.Count == 0)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[AttackPlanner] SelectBestAttack: No attacks available");
                if (context != null) context.AllAbilitiesFiltered = true;
                return null;
            }

            var targetWrapper = new TargetWrapper(target);
            var rangePreference = situation.RangePreference;

            // ★ v3.7.89: AOO 상태 체크 (위협 범위 내인지)
            bool isInThreatArea = CombatAPI.IsInThreateningArea(situation.Unit);
            if (isInThreatArea)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[AttackPlanner] Unit is in threatening area - applying AOO filters");
            }

            // ★ v3.8.94: DangerousAoE 필터 제거 — 모든 AoE는 아군 안전 체크(UtilityScorer + AoESafetyChecker)에서 통합 관리
            var filteredAttacks = situation.AvailableAttacks
                .Where(a => !CombatHelpers.ShouldExcludeFromAttack(a, isInThreatArea))
                .Where(a => !IsAbilityExcluded(a, excludeAbilityGuids))
                .ToList();

            // ★ v3.5.11: 필터링 결과 로깅
            if (filteredAttacks.Count == 0 && situation.AvailableAttacks.Count > 0)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[AttackPlanner] SelectBestAttack: All {situation.AvailableAttacks.Count} attacks filtered out");
                if (context != null) context.AllAbilitiesFiltered = true;
            }

            // ★ v3.8.44: 필터링된 공격 중 최대 사거리 기록 (MovementPlanner용)
            if (context != null && filteredAttacks.Count > 0)
            {
                foreach (var atk in filteredAttacks)
                {
                    float range = CombatAPI.GetAbilityRangeInTiles(atk);
                    if (range > context.BestAbilityRange)
                        context.BestAbilityRange = range;
                }
            }

            // ★ v3.9.30: RangePreference 하드 필터 제거 (이중 필터 버그 수정)
            // SituationAnalyzer가 이미 FilterAbilitiesByRangePreference 적용 (line 843)
            // UtilityScorer.ScoreAttack이 RangePreference 소프트 스코어링 (±35점)
            // 하드 필터는 Fallback으로 추가된 공격(수류탄 등)을 다시 제거하여
            // 근접 도달 불가 시 사용 가능한 원거리 공격마저 차단하는 버그 발생

            // ★ v3.8.48: anonymous type → ValueTuple (GC 압박 감소)
            var scoredAttacks = new List<(AbilityData Attack, float Score)>();
            for (int i = 0; i < filteredAttacks.Count; i++)
            {
                var a = filteredAttacks[i];
                float s = UtilityScorer.ScoreAttack(a, target, situation);
                if (s > 0) scoredAttacks.Add((a, s));
            }
            scoredAttacks.Sort((x, y) => y.Score.CompareTo(x.Score));

            // ★ v3.5.11: 스코어링 결과 로깅
            if (scoredAttacks.Count == 0 && filteredAttacks.Count > 0)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[AttackPlanner] SelectBestAttack: {filteredAttacks.Count} filtered attacks, but all scored 0 or less");
            }

            for (int i = 0; i < scoredAttacks.Count; i++)
            {
                var attack = scoredAttacks[i].Attack;
                var score = scoredAttacks[i].Score;

                // ★ v3.6.9: Point 타겟 AOE의 높이 차이 체크
                // Hittable 체크에서 필터링되어도 AvailableAttacks에는 남아있을 수 있음
                if (CombatAPI.IsPointTargetAbility(attack))
                {
                    if (!CombatAPI.IsAoEHeightInRange(attack, situation.Unit, target))
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[AttackPlanner] AOE height failed: {attack.Name} -> {target.CharacterName}");
                        if (context != null) context.HeightCheckFailed = true;
                        continue;
                    }
                }

                // ★ v3.8.33: DangerousAoE Ray/Cone/Sector 패턴 거리 검증
                // 게임은 RangeCells(무기 사거리)만 체크하지만, Ray 패턴은 patternRadius만큼만 뻗어나감
                // 무기 사거리 15 + 패턴 반경 6 → 15타일 떨어진 적 타겟 가능하지만 실제 Ray는 6타일만 이동
                if (AbilityDatabase.IsDangerousAoE(attack))
                {
                    var patternInfo = CombatAPI.GetPatternInfo(attack);
                    if (patternInfo != null && patternInfo.IsValid && patternInfo.CanBeDirectional)
                    {
                        // Ray/Cone/Sector 패턴: caster에서 patternRadius만큼만 뻗어나감
                        float patternRadius = patternInfo.Radius;
                        float distanceToTarget = CombatCache.GetDistanceInTiles(situation.Unit, target);

                        if (distanceToTarget > patternRadius)
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[AttackPlanner] DangerousAoE pattern range failed: {attack.Name} -> {target.CharacterName} " +
                                $"(dist={distanceToTarget:F1} > patternRadius={patternRadius:F0} tiles)");
                            continue;
                        }
                    }
                }

                string reason;
                if (CombatAPI.CanUseAbilityOn(attack, targetWrapper, out reason))
                {
                    // ★ v3.8.70: 공통 안전 체크 (CombatHelpers 중앙집중)
                    if (!CombatHelpers.IsAttackSafeForTarget(attack, situation.Unit, target, situation.Allies))
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[AttackPlanner] Ally safety blocked: {attack.Name} -> {target.CharacterName}");
                        continue;
                    }

                    return attack;
                }
                else
                {
                    // ★ v3.5.11: CanUseAbilityOn 실패 원인 로깅
                    if (Main.IsDebugEnabled) Main.LogDebug($"[AttackPlanner] CanUseAbilityOn failed: {attack.Name} -> {target.CharacterName} ({reason})");

                    // ★ v3.8.44: 사거리 부족 감지 (문자열 매칭 없이 거리 비교)
                    if (context != null)
                    {
                        float abilityRange = CombatAPI.GetAbilityRangeInTiles(attack);
                        float distToTarget = CombatCache.GetDistanceInTiles(situation.Unit, target);
                        if (distToTarget > abilityRange)
                            context.RangeWasIssue = true;
                    }
                }
            }

            // ★ v3.5.11: 폴백 로깅
            // ★ v3.6.10: PrimaryAttack 폴백에도 높이 체크 적용
            if (situation.PrimaryAttack != null)
            {
                // AOE 높이 체크
                if (CombatAPI.IsPointTargetAbility(situation.PrimaryAttack))
                {
                    if (!CombatAPI.IsAoEHeightInRange(situation.PrimaryAttack, situation.Unit, target))
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[AttackPlanner] PrimaryAttack AOE height failed: {situation.PrimaryAttack.Name} -> {target.CharacterName}");
                        if (context != null) context.HeightCheckFailed = true;
                        return null;  // ★ v3.6.10: 폴백도 실패
                    }
                }

                // ★ v3.8.33: DangerousAoE Ray/Cone/Sector 패턴 거리 검증 (폴백에도 적용)
                if (AbilityDatabase.IsDangerousAoE(situation.PrimaryAttack))
                {
                    var patternInfo = CombatAPI.GetPatternInfo(situation.PrimaryAttack);
                    if (patternInfo != null && patternInfo.IsValid && patternInfo.CanBeDirectional)
                    {
                        float patternRadius = patternInfo.Radius;
                        float distanceToTarget = CombatCache.GetDistanceInTiles(situation.Unit, target);

                        if (distanceToTarget > patternRadius)
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[AttackPlanner] PrimaryAttack DangerousAoE pattern range failed: {situation.PrimaryAttack.Name} -> {target.CharacterName} " +
                                $"(dist={distanceToTarget:F1} > patternRadius={patternRadius:F0} tiles)");
                            return null;
                        }
                    }
                }

                string fallbackReason;
                bool canUsePrimary = CombatAPI.CanUseAbilityOn(situation.PrimaryAttack, targetWrapper, out fallbackReason);
                if (!canUsePrimary)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[AttackPlanner] PrimaryAttack fallback also failed: {situation.PrimaryAttack.Name} -> {target.CharacterName} ({fallbackReason})");

                    // ★ v3.8.44: PrimaryAttack 폴백 사거리 부족 감지
                    if (context != null)
                    {
                        float abilityRange = CombatAPI.GetAbilityRangeInTiles(situation.PrimaryAttack);
                        float distToTarget = CombatCache.GetDistanceInTiles(situation.Unit, target);
                        if (distToTarget > abilityRange)
                            context.RangeWasIssue = true;
                    }

                    return null;  // ★ v3.6.10: 명시적 null 반환
                }

                // ★ v3.8.45: PrimaryAttack 폴백도 아군 안전 체크 (AOE + CanTargetFriends)
                if (CombatAPI.IsPointTargetAbility(situation.PrimaryAttack) || situation.PrimaryAttack.Blueprint?.CanTargetFriends == true)
                {
                    if (!AoESafetyChecker.IsAoESafeForUnitTarget(situation.PrimaryAttack, situation.Unit, target, situation.Allies))
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[AttackPlanner] PrimaryAttack ally safety blocked: {situation.PrimaryAttack.Name} -> {target.CharacterName}");
                        return null;
                    }
                }

                return situation.PrimaryAttack;
            }

            // ★ v3.8.44: context 요약 로그
            if (context != null)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[AttackPlanner] {context}");
            }

            return null;
        }

        /// <summary>
        /// 이동 후 공격 계획
        /// ★ v3.1.23: Self-Targeted AOE (Bladedance 등) 특수 처리 추가
        /// ★ v3.1.24: moveDestination 파라미터 추가 - 이동 후 위치에서 최근접 적 재계산
        /// </summary>
        public static PlannedAction PlanPostMoveAttack(Situation situation, BaseUnitEntity target, ref float remainingAP, string roleName, UnityEngine.Vector3? moveDestination = null)
        {
            // ★ v3.1.24: 이동 목적지가 있으면 해당 위치에서 최근접 적 재계산
            var effectiveTarget = target;
            if (moveDestination.HasValue && situation.Enemies != null)
            {
                effectiveTarget = FindNearestEnemyFromPosition(moveDestination.Value, situation.Enemies);
                if (effectiveTarget == null)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanPostMoveAttack: No enemy reachable from destination");
                    return null;
                }

                if (effectiveTarget != target)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PlanPostMoveAttack: Target changed from {target?.CharacterName} to {effectiveTarget.CharacterName} based on move destination");
                }
            }

            if (effectiveTarget == null) return null;

            var attack = SelectBestAttack(situation, effectiveTarget);
            if (attack == null)
            {
                if (situation.AvailableAttacks.Count > 0)
                {
                    // ★ v3.6.16: AOE 아군 안전 체크 (타겟 기준)
                    var safeAttacks = situation.AvailableAttacks
                        .Where(a => IsAoESafeForTarget(a, effectiveTarget, situation))
                        .ToList();

                    var rangePreference = situation.RangePreference;
                    if (rangePreference == RangePreference.PreferRanged)
                    {
                        attack = safeAttacks.FirstOrDefault(a => !a.IsMelee);
                    }
                    else if (rangePreference == RangePreference.PreferMelee)
                    {
                        attack = safeAttacks.FirstOrDefault(a => a.IsMelee);
                    }

                    if (attack == null)
                    {
                        attack = safeAttacks.FirstOrDefault();
                    }
                }

                if (attack == null)
                {
                    attack = CombatAPI.FindAnyAttackAbility(situation.Unit, situation.RangePreference);

                    // ★ v3.8.45: FindAnyAttackAbility 폴백도 아군 안전 체크
                    // CanTargetFriends 능력(점사 사격 등)이 반환될 수 있음
                    if (attack != null && effectiveTarget != null &&
                        (CombatAPI.IsPointTargetAbility(attack) || attack.Blueprint?.CanTargetFriends == true))
                    {
                        if (!AoESafetyChecker.IsAoESafeForUnitTarget(attack, situation.Unit, effectiveTarget, situation.Allies))
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PostMoveAttack FindAny ally safety blocked: {attack.Name} -> {effectiveTarget.CharacterName}");
                            attack = null;
                        }
                    }
                }
            }

            if (attack == null) return null;

            // ★ v3.1.23: Self-Targeted AOE 공격 처리 (Bladedance 등)
            // Range=Personal, CanTargetSelf인 DangerousAoE → 적을 타겟으로 할 수 없음
            if (CombatAPI.IsSelfTargetedAoEAttack(attack))
            {
                return PlanSelfTargetedAoEAttack(situation, attack, ref remainingAP, roleName);
            }

            // ★ v3.5.98: 이동 후 위치에서 공격 범위 검증 (타일 단위)
            if (moveDestination.HasValue)
            {
                float distFromDest = CombatAPI.MetersToTiles(UnityEngine.Vector3.Distance(moveDestination.Value, effectiveTarget.Position));
                float attackRange = CombatAPI.GetAbilityRangeInTiles(attack);
                if (distFromDest > attackRange)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PostMoveAttack: {attack.Name} out of range ({distFromDest:F1} > {attackRange:F1} tiles)");
                    return null;
                }
            }

            // 일반 공격
            float cost = CombatAPI.GetAbilityAPCost(attack);
            if (cost > remainingAP) return null;

            remainingAP -= cost;
            if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] PostMoveAttack: {attack.Name} -> {effectiveTarget.CharacterName}");
            return PlannedAction.Attack(attack, effectiveTarget, $"Post-move attack with {attack.Name}", cost);
        }

        /// <summary>
        /// ★ v3.1.24: 특정 위치에서 최근접 적 찾기
        /// </summary>
        private static BaseUnitEntity FindNearestEnemyFromPosition(UnityEngine.Vector3 position, List<BaseUnitEntity> enemies)
        {
            if (enemies == null || enemies.Count == 0) return null;

            // ★ v3.8.48: LINQ → CollectionHelper (0 할당, O(n))
            return CollectionHelper.MinByWhere(enemies,
                e => e.IsConscious,
                e => UnityEngine.Vector3.Distance(position, e.Position));
        }

        /// <summary>
        /// ★ v3.6.16: AOE 능력이 타겟에 대해 안전한지 확인
        /// - 비 AOE 능력: 항상 안전
        /// - AOE 능력: 타겟 주변 아군 수가 MaxPlayerAlliesHit 이하면 안전
        /// ★ v3.8.12: AIConfig.MaxPlayerAlliesHit 설정 반영
        /// </summary>
        private static bool IsAoESafeForTarget(AbilityData ability, BaseUnitEntity target, Situation situation)
        {
            if (ability == null || target == null) return false;

            // ★ v3.8.12: 설정에서 최대 허용 아군 수 가져오기
            var aoeConfig = AIConfig.GetAoEConfig();
            int maxAlliesAllowed = aoeConfig?.MaxPlayerAlliesHit ?? 1;

            // DangerousAoE 체크
            if (AbilityDatabase.IsDangerousAoE(ability))
            {
                float radius = CombatAPI.GetAoERadius(ability);
                if (radius <= 0f) radius = 3f;
                return CountAlliesNearTarget(target, situation, radius) <= maxAlliesAllowed;
            }

            // Point AOE 체크
            if (CombatAPI.IsPointTargetAbility(ability))
            {
                float radius = CombatAPI.GetAoERadius(ability);
                if (radius > 0f)
                {
                    return CountAlliesNearTarget(target, situation, radius) <= maxAlliesAllowed;
                }
            }

            // ★ v3.9.24: 체인 능력 안전 체크 — AoESafetyChecker로 위임
            // 체인 능력은 aoERadius=0이지만 AbilityDeliverChain으로 아군 전파 가능
            return AoESafetyChecker.IsAoESafeForUnitTarget(ability, situation.Unit, target, situation.Allies);
        }

        /// <summary>
        /// ★ v3.6.16: 타겟 주변 아군 수 계산
        /// </summary>
        private static int CountAlliesNearTarget(BaseUnitEntity target, Situation situation, float radius)
        {
            if (target == null || situation.Allies == null) return 0;

            return situation.Allies.Count(ally =>
                ally != null &&
                !ally.LifeState.IsDead &&
                CombatAPI.GetDistance(target, ally) <= radius);
        }

        /// <summary>
        /// 마무리 스킬 계획 (DPS 전용)
        /// ★ v3.5.83: AOE 보너스를 포함한 스코어 기반 선택
        /// </summary>
        public static PlannedAction PlanFinisher(Situation situation, BaseUnitEntity target, ref float remainingAP, string roleName)
        {
            var finishers = situation.AvailableAttacks
                .Where(a => AbilityDatabase.IsFinisher(a))
                .ToList();

            if (finishers.Count == 0) return null;

            var targetWrapper = new TargetWrapper(target);
            float currentAP = remainingAP;  // ★ v3.5.83: 람다용 로컬 복사

            // ★ v3.8.48: anonymous type → ValueTuple (GC 압박 감소)
            // ★ v3.5.83: 스코어 기반 finisher 선택 (AOE 보너스 포함)
            var scoredFinishers = new List<(AbilityData Finisher, float Cost, bool CanKill, float Score)>();
            for (int i = 0; i < finishers.Count; i++)
            {
                var f = finishers[i];
                float cost = CombatAPI.GetAbilityAPCost(f);
                string r;
                bool canUse = cost <= currentAP && CombatAPI.CanUseAbilityOn(f, targetWrapper, out r);
                if (!canUse) continue;
                bool canKill = CombatAPI.CanKillInOneHit(f, target);
                float score = UtilityScorer.ScoreAttack(f, target, situation);
                scoredFinishers.Add((f, cost, canKill, score));
            }
            // 킬 가능 우선, 그 다음 스코어 순
            scoredFinishers.Sort((x, y) => {
                int killCmp = y.CanKill.CompareTo(x.CanKill);
                return killCmp != 0 ? killCmp : y.Score.CompareTo(x.Score);
            });

            if (scoredFinishers.Count > 0)
            {
                var best = scoredFinishers[0];
                remainingAP -= best.Cost;

                if (best.CanKill)
                {
                    int hp = CombatAPI.GetActualHP(target);
                    Main.Log($"[{roleName}] Finisher (KILL): {best.Finisher.Name} -> {target.CharacterName} (HP={hp}, Score={best.Score:F0})");
                    return PlannedAction.Attack(best.Finisher, target, $"Finisher KILL on {target.CharacterName}", best.Cost);
                }
                else
                {
                    Main.Log($"[{roleName}] Finisher: {best.Finisher.Name} -> {target.CharacterName} (Score={best.Score:F0})");
                    return PlannedAction.Attack(best.Finisher, target, $"Finisher on {target.CharacterName}", best.Cost);
                }
            }

            return null;
        }

        /// <summary>
        /// 특수 능력 계획 (DoT 강화, 연쇄 효과)
        /// </summary>
        public static PlannedAction PlanSpecialAbility(Situation situation, ref float remainingAP, string roleName)
        {
            if (situation.AvailableSpecialAbilities == null || situation.AvailableSpecialAbilities.Count == 0)
                return null;

            if (situation.BestTarget == null)
                return null;

            var target = situation.BestTarget;
            var enemies = situation.Enemies;
            var targetWrapper = new TargetWrapper(target);
            float currentAP = remainingAP;

            // ★ v3.8.48: anonymous type → ValueTuple (GC 압박 감소)
            var scoredAbilities = new List<(AbilityData Ability, float Score, float Cost)>();
            for (int i = 0; i < situation.AvailableSpecialAbilities.Count; i++)
            {
                var a = situation.AvailableSpecialAbilities[i];
                float cost = CombatAPI.GetAbilityAPCost(a);
                if (cost > currentAP) continue;
                float score = SpecialAbilityHandler.GetSpecialAbilityEffectivenessScore(a, target, enemies);
                if (score > 0) scoredAbilities.Add((a, score, cost));
            }
            scoredAbilities.Sort((x, y) => y.Score.CompareTo(x.Score));

            for (int i = 0; i < scoredAbilities.Count; i++)
            {
                var entry = scoredAbilities[i];
                var ability = entry.Ability;

                if (!SpecialAbilityHandler.CanUseSpecialAbilityEffectively(ability, target, enemies))
                    continue;

                string reason;
                if (CombatAPI.CanUseAbilityOn(ability, targetWrapper, out reason))
                {
                    // ★ v3.9.82: 체인/AoE 아군 안전 체크 (기존 누락 — 일반 공격 경로에서만 체크되고 있었음)
                    if (!CombatHelpers.IsAttackSafeForTarget(ability, situation.Unit, target, situation.Allies))
                    {
                        Main.LogDebug($"[{roleName}] Special ability ally safety blocked: {ability.Name} -> {target.CharacterName}");
                        continue;
                    }

                    remainingAP -= entry.Cost;

                    string abilityType = AbilityDatabase.IsDOTIntensify(ability) ? "DoT Intensify" :
                                        AbilityDatabase.IsChainEffect(ability) ? "Chain Effect" : "Special";

                    Main.Log($"[{roleName}] {abilityType}: {ability.Name} -> {target.CharacterName}");
                    return PlannedAction.Attack(ability, target, $"{abilityType} on {target.CharacterName}", entry.Cost);
                }
            }

            return null;
        }

        /// <summary>
        /// 안전한 원거리 공격 (Support 전용)
        /// ★ v3.0.49: Weapon != null 조건 제거 - 사이킥/수류탄 능력 허용
        /// ★ v3.0.50: AoE 아군 피해 체크 추가
        /// </summary>
        public static PlannedAction PlanSafeRangedAttack(Situation situation, ref float remainingAP,
            string roleName, HashSet<string> excludeTargetIds = null, HashSet<string> excludeAbilityGuids = null)
        {
            // ★ v3.8.48: LINQ → for 루프 (GC 압박 감소)
            // ★ v3.0.49: !a.IsMelee만 체크 - 사이킥 능력(Weapon=null)도 원거리 공격으로 허용
            var rangedAttacks = new List<AbilityData>();
            for (int i = 0; i < situation.AvailableAttacks.Count; i++)
            {
                var a = situation.AvailableAttacks[i];
                if (!a.IsMelee && !AbilityDatabase.IsDangerousAoE(a) && !IsAbilityExcluded(a, excludeAbilityGuids))
                    rangedAttacks.Add(a);
            }
            rangedAttacks.Sort((x, y) => CombatAPI.GetAbilityAPCost(x).CompareTo(CombatAPI.GetAbilityAPCost(y)));

            if (rangedAttacks.Count == 0) return null;

            var candidateTargets = new List<BaseUnitEntity>();

            if (situation.BestTarget != null && !IsExcluded(situation.BestTarget, excludeTargetIds))
                candidateTargets.Add(situation.BestTarget);

            foreach (var hittable in situation.HittableEnemies)
            {
                if (hittable != null && !candidateTargets.Contains(hittable) && !IsExcluded(hittable, excludeTargetIds))
                    candidateTargets.Add(hittable);
            }

            if (situation.NearestEnemy != null && !candidateTargets.Contains(situation.NearestEnemy) && !IsExcluded(situation.NearestEnemy, excludeTargetIds))
                candidateTargets.Add(situation.NearestEnemy);

            if (candidateTargets.Count == 0) return null;

            foreach (var target in candidateTargets)
            {
                var targetWrapper = new TargetWrapper(target);

                foreach (var attack in rangedAttacks)
                {
                    float cost = CombatAPI.GetAbilityAPCost(attack);
                    if (cost > remainingAP) continue;

                    // ★ v3.8.64: AoESafetyChecker 통합 (간이 3타일 체크 → 게임 기반 스캐터 패턴)
                    if (attack.Blueprint?.CanTargetFriends == true)
                    {
                        if (!AoESafetyChecker.IsAoESafeForUnitTarget(attack, situation.Unit, target, situation.Allies))
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] Skipping {attack.Name} - ally in scatter zone of {target.CharacterName}");
                            continue;
                        }
                    }

                    string reason;
                    if (CombatAPI.CanUseAbilityOn(attack, targetWrapper, out reason))
                    {
                        remainingAP -= cost;
                        Main.Log($"[{roleName}] Safe attack: {attack.Name} -> {target.CharacterName}");
                        return PlannedAction.Attack(attack, target, $"Safe attack on {target.CharacterName}", cost);
                    }
                }
            }

            return null;
        }

        #region Target Selection

        /// <summary>
        /// 낮은 HP 적 찾기 (1타 킬 우선)
        /// </summary>
        public static BaseUnitEntity FindLowHPEnemy(Situation situation, float threshold)
        {
            var primaryAttack = situation.PrimaryAttack;

            // ★ v3.8.48: LINQ → CollectionHelper (0 할당, O(n))
            if (primaryAttack != null)
            {
                var oneHitKill = CollectionHelper.MinByWhere(situation.HittableEnemies,
                    e => !e.LifeState.IsDead && CombatAPI.CanKillInOneHit(primaryAttack, e),
                    e => (float)CombatAPI.GetActualHP(e));

                if (oneHitKill != null) return oneHitKill;
            }

            var hittableLowHP = CollectionHelper.MinByWhere(situation.HittableEnemies,
                e => !e.LifeState.IsDead && CombatCache.GetHPPercent(e) <= threshold,
                e => (float)CombatAPI.GetActualHP(e));

            return hittableLowHP ?? CollectionHelper.MinByWhere(situation.Enemies,
                e => !e.LifeState.IsDead && CombatCache.GetHPPercent(e) <= threshold,
                e => (float)CombatAPI.GetActualHP(e));
        }

        /// <summary>
        /// ★ v3.1.21: Role 기반 최적 적 타겟 선택
        /// TargetScorer를 사용하여 Role별 가중치 적용
        /// </summary>
        public static BaseUnitEntity FindWeakestEnemy(Situation situation, HashSet<string> excludeTargetIds = null)
        {
            // Role 결정 (Auto면 DPS로 처리)
            var role = situation.CharacterSettings?.Role ?? Settings.AIRole.Auto;
            var effectiveRole = role == Settings.AIRole.Auto ? Settings.AIRole.DPS : role;

            var candidates = situation.HittableEnemies
                .Where(e => e != null && !e.LifeState.IsDead)
                .Where(e => !IsExcluded(e, excludeTargetIds))
                .ToList();

            if (candidates.Count > 0)
            {
                var best = TargetScorer.SelectBestEnemy(candidates, situation, effectiveRole);
                if (best != null) return best;
            }

            // 폴백: 모든 적
            var allCandidates = situation.Enemies
                .Where(e => e != null && !e.LifeState.IsDead)
                .Where(e => !IsExcluded(e, excludeTargetIds))
                .ToList();

            return TargetScorer.SelectBestEnemy(allCandidates, situation, effectiveRole);
        }

        #endregion

        #region AOE Attack (v3.1.16)

        /// <summary>
        /// ★ v3.1.16: AOE 공격 계획 - 안전하고 효율적인 위치 선택
        /// ★ v3.1.18: 방향성 패턴(Cone/Ray/Sector) 지원 추가
        /// </summary>
        public static PlannedAction PlanAoEAttack(
            Situation situation,
            ref float remainingAP,
            string roleName)
        {
            // Point 타겟 AOE 능력 찾기
            var aoEAbilities = situation.AvailableAttacks
                .Where(a => CombatAPI.IsPointTargetAbility(a))
                .Where(a => !AbilityDatabase.IsReload(a))
                .Where(a => !AbilityDatabase.IsTurnEnding(a))
                .ToList();

            if (aoEAbilities.Count == 0) return null;

            foreach (var ability in aoEAbilities)
            {
                float cost = CombatAPI.GetAbilityAPCost(ability);
                if (cost > remainingAP) continue;

                var patternType = CombatAPI.GetPatternType(ability);
                AoESafetyChecker.AoEScore bestResult = null;

                // ★ v3.1.18: 패턴 타입에 따른 분기
                // ★ v3.1.29: MinEnemiesForAoE 설정값 적용
                // ★ v3.8.09: GetActualIsDirectional() 사용으로 정확한 판정
                int minEnemiesForAoE = situation.CharacterSettings?.MinEnemiesForAoE ?? 2;
                bool isActuallyDirectional = CombatAPI.GetActualIsDirectional(ability);

                if (isActuallyDirectional)
                {
                    // 방향성 패턴 (Cone/Ray/Sector) - 타겟 기반
                    bestResult = AoESafetyChecker.FindBestDirectionalAoETarget(
                        ability,
                        situation.Unit,
                        situation.Enemies,
                        situation.Allies,
                        minEnemiesRequired: minEnemiesForAoE);

                    if (bestResult == null || !bestResult.IsSafe) continue;

                    // 방향성 패턴은 주 타겟 유닛으로 타겟팅
                    var primaryTarget = bestResult.AffectedUnits
                        .FirstOrDefault(u => situation.Unit.CombatGroup.IsEnemy(u));

                    if (primaryTarget == null) continue;

                    // ★ v3.9.92: DangerousAoE CanTargetEnemies=false — 포인트 타겟으로 전환
                    // Cone/Ray 패턴은 방향만 지정하면 됨 → 클릭 사거리 내 방향 포인트 사용
                    bool isDangerousAoENoUnitTarget = AbilityDatabase.IsDangerousAoE(ability)
                        && ability.Blueprint != null && !ability.Blueprint.CanTargetEnemies;

                    if (isDangerousAoENoUnitTarget)
                    {
                        var casterPos = situation.Unit.Position;
                        var toTarget = primaryTarget.Position - casterPos;
                        float distMeters = toTarget.magnitude;
                        if (distMeters < 0.01f) continue;
                        var direction = toTarget / distMeters;

                        float clickRangeMeters = CombatAPI.TilesToMeters(CombatAPI.GetAbilityRangeInTiles(ability));
                        float targetDist = Math.Min(clickRangeMeters * 0.95f, distMeters);
                        var directionPoint = casterPos + direction * targetDist;

                        string dirReason;
                        if (!CombatAPI.CanUseAbilityOnPoint(ability, directionPoint, out dirReason))
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] DangerousAoE direction blocked: {ability.Name} - {dirReason}");
                            continue;
                        }

                        remainingAP -= cost;
                        Main.Log($"[{roleName}] Directional DangerousAoE ({patternType}): {ability.Name} -> direction of {primaryTarget.CharacterName} " +
                            $"- {bestResult.EnemiesHit} enemies, {bestResult.AlliesHit} allies");

                        return PlannedAction.PositionalAttack(
                            ability,
                            directionPoint,
                            $"Directional DangerousAoE ({patternType}) on {bestResult.EnemiesHit} enemies",
                            cost);
                    }

                    // 유닛 타겟 검증 (기존 로직)
                    var targetWrapper = new TargetWrapper(primaryTarget);
                    string reason;
                    if (!CombatAPI.CanUseAbilityOn(ability, targetWrapper, out reason))
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] Directional AOE blocked: {ability.Name} - {reason}");
                        continue;
                    }

                    remainingAP -= cost;
                    Main.Log($"[{roleName}] Directional AOE ({patternType}): {ability.Name} -> {primaryTarget.CharacterName} " +
                        $"- {bestResult.EnemiesHit} enemies, {bestResult.AlliesHit} allies");

                    return PlannedAction.Attack(
                        ability,
                        primaryTarget,
                        $"Directional AOE ({patternType}) on {bestResult.EnemiesHit} enemies",
                        cost);
                }
                else
                {
                    // Circle 패턴 - 위치 기반
                    // ★ v3.3.00: 클러스터 탐지 사용 여부 체크
                    bool useAoEOptimization = situation.CharacterSettings?.UseAoEOptimization ?? true;

                    if (useAoEOptimization)
                    {
                        // 클러스터 기반 최적 위치 탐색
                        bestResult = AoESafetyChecker.FindBestAoEPositionWithClusters(
                            ability,
                            situation.Unit,
                            situation.Enemies,
                            situation.Allies,
                            minEnemiesRequired: minEnemiesForAoE);
                    }
                    else
                    {
                        // 레거시 방식
                        bestResult = AoESafetyChecker.FindBestAoEPosition(
                            ability,
                            situation.Unit,
                            situation.Enemies,
                            situation.Allies,
                            minEnemiesRequired: minEnemiesForAoE);
                    }

                    if (bestResult == null || !bestResult.IsSafe) continue;

                    // Point 타겟 검증
                    string reason;
                    if (!CombatAPI.CanUseAbilityOnPoint(ability, bestResult.Position, out reason))
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] AOE blocked: {ability.Name} - {reason}");
                        continue;
                    }

                    remainingAP -= cost;
                    string aoEMethod = useAoEOptimization ? "Cluster" : "Legacy";
                    Main.Log($"[{roleName}] AOE ({aoEMethod}): {ability.Name} at ({bestResult.Position.x:F1},{bestResult.Position.z:F1}) " +
                        $"- {bestResult.EnemiesHit} enemies, {bestResult.AlliesHit} allies");

                    return PlannedAction.PositionalAttack(
                        ability,
                        bestResult.Position,
                        $"AOE on {bestResult.EnemiesHit} enemies",
                        cost);
                }
            }

            return null;
        }

        /// <summary>
        /// ★ v3.8.96: 유닛 타겟 AoE 공격 계획
        /// Phase 4.3 (Self-AoE), 4.3b (Melee-AoE), 4.4 (Point-AoE)에서 처리하지 않는
        /// 나머지 모든 AoE 타입을 처리 (Burst, Scatter, 기타 유닛 타겟 AoE)
        /// 예: 점사 사격(Burst Fire), 산탄(Scatter), 유닛 타겟 패턴 공격
        /// </summary>
        public static PlannedAction PlanUnitTargetedAoEAttack(
            Situation situation,
            ref float remainingAP,
            string roleName)
        {
            if (situation.AvailableAoEAttacks == null || situation.AvailableAoEAttacks.Count == 0)
                return null;

            // AvailableAoEAttacks에서 다른 Phase에서 이미 처리하는 타입 제외
            // ★ v3.9.10: new List<> 제거 → 정적 리스트 재사용
            _sharedAbilityList.Clear();
            var unitTargetedAoE = _sharedAbilityList;
            foreach (var attack in situation.AvailableAoEAttacks)
            {
                // Phase 4.4에서 처리: Point-target AoE (위치 지정형)
                if (CombatAPI.IsPointTargetAbility(attack)) continue;
                // Phase 4.3에서 처리: Self-Targeted AoE (BladeDance 등)
                if (CombatAPI.IsSelfTargetedAoEAttack(attack)) continue;
                // Phase 4.3b에서 처리: Melee AoE (근접 스플래시)
                if (CombatAPI.IsMeleeAoEAbility(attack)) continue;

                // 나머지 = 유닛 타겟 AoE (Burst, Scatter, 기타)
                unitTargetedAoE.Add(attack);
            }

            if (unitTargetedAoE.Count == 0) return null;

            int minEnemies = situation.CharacterSettings?.MinEnemiesForAoE ?? 2;

            // 각 유닛 타겟 AoE 능력에 대해 최적 타겟 탐색
            PlannedAction bestAction = null;
            int bestEnemyCount = 0;

            foreach (var ability in unitTargetedAoE)
            {
                float cost = CombatAPI.GetEffectiveAPCost(ability);
                if (cost > remainingAP) continue;

                // 각 Hittable 적에 대해 패턴 내 적 수 계산
                foreach (var enemy in situation.HittableEnemies)
                {
                    if (enemy == null || !enemy.IsConscious) continue;

                    // CanUseAbilityOn 체크
                    string reason;
                    if (!CombatAPI.CanUseAbilityOn(ability, new TargetWrapper(enemy), out reason))
                        continue;

                    // ★ v3.9.10: 패턴 1회 계산으로 적+아군 동시 카운트 (GetAffectedNodes 중복 제거)
                    CombatAPI.CountUnitsInPattern(
                        ability, enemy.Position, situation.Unit.Position,
                        situation.Unit, situation.Enemies, situation.Allies,
                        out int enemiesHit, out int alliesHit);

                    if (enemiesHit >= minEnemies && enemiesHit > bestEnemyCount)
                    {
                        // 아군 안전 체크 (alliesHit 이미 계산됨)
                        var aoeConfig = Settings.AIConfig.GetAoEConfig();
                        int maxAlliesAllowed = aoeConfig?.MaxPlayerAlliesHit ?? 1;

                        if (alliesHit > maxAlliesAllowed)
                        {
                            if (Main.IsDebugEnabled)
                                Main.LogDebug($"[{roleName}] Unit AoE {ability.Name} -> {enemy.CharacterName}: " +
                                    $"{alliesHit} allies > max {maxAlliesAllowed} - BLOCKED");
                            continue;
                        }

                        bestEnemyCount = enemiesHit;
                        bestAction = PlannedAction.Attack(
                            ability,
                            enemy,
                            $"Unit-targeted AoE ({CombatAPI.GetAttackCategory(ability)}) on {enemiesHit} enemies",
                            cost);

                        if (Main.IsDebugEnabled)
                            Main.LogDebug($"[{roleName}] Unit AoE candidate: {ability.Name} -> {enemy.CharacterName} " +
                                $"({enemiesHit} enemies, {alliesHit} allies)");
                    }
                }
            }

            if (bestAction != null)
            {
                remainingAP -= bestAction.APCost;
                Main.Log($"[{roleName}] Unit-targeted AoE: {bestAction.Ability.Name} -> {(bestAction.Target.Entity as BaseUnitEntity)?.CharacterName} " +
                    $"({bestEnemyCount} enemies)");
            }

            return bestAction;
        }

        #endregion

        #region AoE Reposition (v3.9.08)

        /// <summary>
        /// ★ v3.9.08: AoE 재배치 후보 타일 정보 (struct — GC 없음)
        /// </summary>
        private struct AoERepositionCandidate
        {
            public Kingmaker.Pathfinding.CustomGridNodeBase Node;
            public UnityEngine.Vector3 Position;
            public float Score;
        }

        // ★ v3.9.08: 정적 버퍼 (GC 할당 제거)
        private const int MAX_AOE_REPOSITION_CANDIDATES = 15;
        private static readonly AoERepositionCandidate[] _repositionBuffer = new AoERepositionCandidate[MAX_AOE_REPOSITION_CANDIDATES];

        /// <summary>
        /// ★ v3.9.08: AoE 재배치 — Phase 4.4 실패 시, 이동하면 AoE가 가능한 위치 탐색
        /// 아군 피격으로 AoE가 차단될 때, 다른 위치에서 안전한 AoE를 시전할 수 있는지 확인
        /// 이동은 MP 소모, AoE는 AP 소모 → MP+AP 예산 모두 필요
        /// </summary>
        public static (PlannedAction moveAction, PlannedAction aoEAction) PlanAoEWithReposition(
            Situation situation,
            ref float remainingAP,
            ref float remainingMP,
            string roleName)
        {
            // Guard
            if (remainingMP <= 0 || remainingAP < 1f)
                return (null, null);
            if (!situation.HasAoEAttacks || situation.AvailableAoEAttacks == null || situation.AvailableAoEAttacks.Count == 0)
                return (null, null);
            if (!situation.CanMove)
                return (null, null);

            var unit = situation.Unit;
            int minEnemies = situation.CharacterSettings?.MinEnemiesForAoE ?? 2;
            if (situation.Enemies.Count < minEnemies)
                return (null, null);

            // Reachable tiles 조회 (LRU 캐시 히트 기대)
            var reachableTiles = MovementAPI.FindAllReachableTilesWithThreatsSync(unit);
            if (reachableTiles == null || reachableTiles.Count == 0)
                return (null, null);

            // 최고 결과 추적
            PlannedAction bestMoveAction = null;
            PlannedAction bestAoEAction = null;
            float bestScore = float.MinValue;
            int bestEnemiesHit = 0;

            foreach (var aoeAbility in situation.AvailableAoEAttacks)
            {
                float abilityCost = CombatAPI.GetEffectiveAPCost(aoeAbility);
                if (abilityCost > remainingAP) continue;

                // Self-target, Melee AoE는 재배치 의미 없음
                if (CombatAPI.IsSelfTargetedAoEAttack(aoeAbility)) continue;
                if (CombatAPI.IsMeleeAoEAbility(aoeAbility)) continue;

                float aoERadius = CombatAPI.GetAoERadius(aoeAbility);
                if (aoERadius <= 0) aoERadius = 5f;

                // 클러스터 탐색
                var clusters = ClusterDetector.FindClusters(situation.Enemies, aoERadius);
                if (clusters == null || clusters.Count == 0) continue;

                // 최고 클러스터 선택
                EnemyCluster bestCluster = null;
                foreach (var cluster in clusters)
                {
                    if (cluster.Count >= minEnemies)
                    {
                        if (bestCluster == null || cluster.QualityScore > bestCluster.QualityScore)
                            bestCluster = cluster;
                    }
                }
                if (bestCluster == null) continue;

                bool isPointTarget = CombatAPI.IsPointTargetAbility(aoeAbility);
                bool isDirectional = CombatAPI.GetActualIsDirectional(aoeAbility);
                float abilityRange = isDirectional
                    ? aoERadius  // 방향성: 패턴 반경이 유효 사거리
                    : CombatAPI.GetAbilityRangeInTiles(aoeAbility);

                // 후보 타일 필터링
                int candidateCount = GetAoERepositionCandidates(
                    unit, bestCluster.Center, abilityRange,
                    reachableTiles, situation.PrefersRanged, situation.MinSafeDistance,
                    situation.Enemies, _repositionBuffer, MAX_AOE_REPOSITION_CANDIDATES);

                if (candidateCount == 0) continue;

                // 각 후보 타일에서 AoE 평가
                for (int i = 0; i < candidateCount; i++)
                {
                    var candidate = _repositionBuffer[i];
                    AoESafetyChecker.AoEScore aoEResult = null;

                    if (isPointTarget)
                    {
                        if (isDirectional)
                        {
                            aoEResult = AoESafetyChecker.FindBestDirectionalAoETargetFromPosition(
                                aoeAbility, unit, candidate.Position,
                                situation.Enemies, situation.Allies, minEnemies);
                        }
                        else
                        {
                            aoEResult = AoESafetyChecker.FindBestAoEPositionFromPosition(
                                aoeAbility, unit, candidate.Position,
                                situation.Enemies, situation.Allies, minEnemies);
                        }
                    }
                    else
                    {
                        // Unit-targeted AoE: 각 적에 대해 안전 체크
                        int bestUnitTargetHits = 0;
                        BaseUnitEntity bestUnitTarget = null;

                        foreach (var enemy in situation.Enemies)
                        {
                            if (enemy == null || !enemy.IsConscious) continue;

                            // fromPosition에서 사거리 체크
                            float distToEnemy = CombatAPI.MetersToTiles(
                                UnityEngine.Vector3.Distance(candidate.Position, enemy.Position));
                            if (distToEnemy > abilityRange) continue;

                            // 기존 IsAoESafeForUnitTargetFromPosition 활용
                            if (!AoESafetyChecker.IsAoESafeForUnitTargetFromPosition(
                                aoeAbility, candidate.Position, unit, enemy, situation.Allies))
                                continue;

                            // 패턴 내 적 수 계산
                            CombatAPI.CountUnitsInPattern(
                                aoeAbility, enemy.Position, candidate.Position,
                                unit, situation.Enemies, situation.Allies,
                                out int enemiesHit, out int alliesHit);

                            if (enemiesHit >= minEnemies && enemiesHit > bestUnitTargetHits)
                            {
                                var aoeConfig = Settings.AIConfig.GetAoEConfig();
                                int maxAlliesAllowed = aoeConfig?.MaxPlayerAlliesHit ?? 1;
                                if (alliesHit > maxAlliesAllowed) continue;

                                bestUnitTargetHits = enemiesHit;
                                bestUnitTarget = enemy;
                            }
                        }

                        if (bestUnitTarget != null && bestUnitTargetHits >= minEnemies)
                        {
                            aoEResult = new AoESafetyChecker.AoEScore
                            {
                                Position = bestUnitTarget.Position,
                                EnemiesHit = bestUnitTargetHits,
                                IsSafe = true,
                                Score = bestUnitTargetHits * 100f
                            };
                        }
                    }

                    if (aoEResult == null || !aoEResult.IsSafe || aoEResult.EnemiesHit < minEnemies)
                        continue;

                    // 점수 산정: AoE 적중 가치 + 후보 타일 점수
                    float totalScore = aoEResult.EnemiesHit * 40f + candidate.Score;

                    if (totalScore > bestScore)
                    {
                        bestScore = totalScore;
                        bestEnemiesHit = aoEResult.EnemiesHit;
                        bestMoveAction = PlannedAction.Move(candidate.Position,
                            $"AoE reposition for {aoeAbility.Name}");

                        if (isPointTarget && !isDirectional)
                        {
                            bestAoEAction = PlannedAction.PositionalAttack(
                                aoeAbility, aoEResult.Position,
                                $"AoE after reposition on {aoEResult.EnemiesHit} enemies",
                                abilityCost);
                        }
                        else if (isDirectional)
                        {
                            // 방향성: AffectedUnits에서 적 타겟 선택
                            var primaryTarget = aoEResult.AffectedUnits?.Find(
                                u => u != null && unit.CombatGroup.IsEnemy(u));
                            if (primaryTarget != null)
                            {
                                bestAoEAction = PlannedAction.Attack(
                                    aoeAbility, primaryTarget,
                                    $"Directional AoE after reposition on {aoEResult.EnemiesHit} enemies",
                                    abilityCost);
                            }
                            else
                            {
                                bestAoEAction = null; // 타겟 찾지 못함
                            }
                        }
                        else
                        {
                            // Unit-targeted AoE
                            var targetEntity = aoEResult.AffectedUnits?.Find(
                                u => u != null && unit.CombatGroup.IsEnemy(u));
                            if (targetEntity == null)
                            {
                                // AoEScore.Position에 저장된 적 위치로 가장 가까운 적 찾기
                                foreach (var enemy in situation.Enemies)
                                {
                                    if (enemy != null && enemy.IsConscious &&
                                        UnityEngine.Vector3.Distance(enemy.Position, aoEResult.Position) < 0.5f)
                                    {
                                        targetEntity = enemy;
                                        break;
                                    }
                                }
                            }

                            if (targetEntity != null)
                            {
                                bestAoEAction = PlannedAction.Attack(
                                    aoeAbility, targetEntity,
                                    $"Unit AoE after reposition on {aoEResult.EnemiesHit} enemies",
                                    abilityCost);
                            }
                            else
                            {
                                bestAoEAction = null;
                            }
                        }
                    }
                }
            }

            // 단일타겟 대비 가치 비교: AoE 적중이 현재 Hittable × 1.2보다 가치 있어야 함
            int currentHittable = situation.HittableEnemies?.Count ?? 0;
            float singleTargetValue = currentHittable * 30f * 1.2f;
            float aoEValue = bestEnemiesHit * 40f;

            if (bestMoveAction != null && bestAoEAction != null && aoEValue > singleTargetValue)
            {
                remainingAP -= bestAoEAction.APCost;
                // 이동은 MP 소모 (AP 비용 없음)

                Main.Log($"[{roleName}] AoE reposition: {bestAoEAction.Ability?.Name} " +
                    $"({bestEnemiesHit} enemies hit, score={bestScore:F0}, " +
                    $"vs singleTarget={singleTargetValue:F0})");

                return (bestMoveAction, bestAoEAction);
            }

            if (bestMoveAction != null && Main.IsDebugEnabled)
            {
                Main.LogDebug($"[{roleName}] AoE reposition rejected: " +
                    $"aoEValue={aoEValue:F0} vs singleTarget={singleTargetValue:F0}");
            }

            return (null, null);
        }

        /// <summary>
        /// ★ v3.9.08: AoE 재배치 후보 타일 필터링
        /// 모든 reachable 타일이 아닌, 클러스터-사거리 관계를 만족하는 소수만 선택
        /// 원거리: 안전 거리 유지, 근거리: 클러스터 근접
        /// </summary>
        private static int GetAoERepositionCandidates(
            BaseUnitEntity unit,
            UnityEngine.Vector3 clusterCenter,
            float abilityRange,
            Dictionary<Pathfinding.GraphNode, Kingmaker.Pathfinding.WarhammerPathAiCell> reachableTiles,
            bool prefersRanged,
            float minSafeDistance,
            List<BaseUnitEntity> enemies,
            AoERepositionCandidate[] buffer,
            int maxCandidates)
        {
            int count = 0;
            float worstScore = float.MaxValue;
            int worstIndex = 0;

            // ★ v3.19.8: 현재 안전한 유닛이 위험 구역으로 리포지션하지 않도록
            bool avoidHazardZones = !CombatAPI.IsUnitInHazardZone(unit);

            foreach (var kvp in reachableTiles)
            {
                var node = kvp.Key as Kingmaker.Pathfinding.CustomGridNodeBase;
                if (node == null) continue;

                var pos = node.Vector3Position;

                // 클러스터 중심까지 거리 체크 (사거리 내여야 AoE 가능)
                float distToCluster = CombatAPI.MetersToTiles(
                    UnityEngine.Vector3.Distance(pos, clusterCenter));
                if (distToCluster > abilityRange) continue;

                // Walkable + 점유 가능 확인
                if (!Analysis.BattlefieldGrid.Instance.IsValid ||
                    !Analysis.BattlefieldGrid.Instance.CanUnitStandOn(unit, node))
                    continue;

                // ★ v3.19.8: 위험 구역 필터링 (DamagingAoE + PsychicNullZone)
                if (avoidHazardZones && CombatAPI.IsPositionInHazardZone(pos, unit))
                    continue;

                // 현재 위치와 같으면 스킵 (이미 Phase 4.4에서 시도했음)
                if (UnityEngine.Vector3.Distance(pos, unit.Position) < 0.5f)
                    continue;

                float tileScore;
                if (prefersRanged)
                {
                    // 원거리: 안전 거리 유지 + 사거리 여유
                    float nearestEnemyDist = float.MaxValue;
                    foreach (var enemy in enemies)
                    {
                        if (enemy == null || !enemy.IsConscious) continue;
                        float d = CombatAPI.MetersToTiles(
                            UnityEngine.Vector3.Distance(pos, enemy.Position));
                        if (d < nearestEnemyDist) nearestEnemyDist = d;
                    }
                    float safetyScore = nearestEnemyDist >= minSafeDistance ? 20f : -30f;
                    float rangeScore = abilityRange - distToCluster;
                    tileScore = safetyScore + rangeScore;
                }
                else
                {
                    // 근거리: 클러스터에 가까울수록 높은 점수
                    float proximityBonus = UnityEngine.Mathf.Max(0f, 10f - distToCluster) * 5f;
                    tileScore = proximityBonus;
                }

                // 경로 위험도 차감
                var cell = kvp.Value;
                tileScore -= cell.ProvokedAttacks * 15f;

                // 상위 maxCandidates개만 유지 (삽입 정렬)
                if (count < maxCandidates)
                {
                    buffer[count] = new AoERepositionCandidate
                    {
                        Node = node,
                        Position = pos,
                        Score = tileScore
                    };
                    if (tileScore < worstScore)
                    {
                        worstScore = tileScore;
                        worstIndex = count;
                    }
                    count++;
                }
                else if (tileScore > worstScore)
                {
                    // 최하위 교체
                    buffer[worstIndex] = new AoERepositionCandidate
                    {
                        Node = node,
                        Position = pos,
                        Score = tileScore
                    };
                    // 새 최하위 찾기
                    worstScore = float.MaxValue;
                    for (int i = 0; i < maxCandidates; i++)
                    {
                        if (buffer[i].Score < worstScore)
                        {
                            worstScore = buffer[i].Score;
                            worstIndex = i;
                        }
                    }
                }
            }

            return count;
        }

        #endregion

        #region AOE Taunt (v3.1.17)

        /// <summary>
        /// ★ v3.1.17: AOE 도발 계획 - 다수 적 도발
        /// </summary>
        public static PlannedAction PlanAoETaunt(
            Situation situation,
            ref float remainingAP,
            string roleName)
        {
            // Point 타겟 + 도발 능력 찾기
            // ★ v3.9.10: LINQ 제거 → for 루프 + 정적 리스트
            _sharedAbilityList.Clear();
            for (int i = 0; i < situation.AvailableBuffs.Count; i++)
            {
                var a = situation.AvailableBuffs[i];
                if (AbilityDatabase.IsTaunt(a) && CombatAPI.IsPointTargetAbility(a))
                    _sharedAbilityList.Add(a);
            }
            var aoeTaunts = _sharedAbilityList;

            if (aoeTaunts.Count == 0) return null;

            foreach (var ability in aoeTaunts)
            {
                float cost = CombatAPI.GetAbilityAPCost(ability);
                if (cost > remainingAP) continue;

                // 이미 활성화된 버프 스킵
                if (AllyStateCache.HasBuff(situation.Unit, ability)) continue;

                // ★ v3.1.29: MinEnemiesForAoE 설정값 적용
                int minEnemiesForAoE = situation.CharacterSettings?.MinEnemiesForAoE ?? 2;

                // 최적 위치 찾기 (적 대상이므로 기존 로직 재사용)
                var bestPosition = AoESafetyChecker.FindBestAoEPosition(
                    ability,
                    situation.Unit,
                    situation.Enemies,
                    situation.Allies,
                    minEnemiesRequired: minEnemiesForAoE);

                if (bestPosition == null || !bestPosition.IsSafe) continue;

                // Point 타겟 검증
                string reason;
                if (!CombatAPI.CanUseAbilityOnPoint(ability, bestPosition.Position, out reason))
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] AOE Taunt blocked: {ability.Name} - {reason}");
                    continue;
                }

                remainingAP -= cost;
                Main.Log($"[{roleName}] AOE Taunt: {ability.Name} at ({bestPosition.Position.x:F1},{bestPosition.Position.z:F1}) " +
                    $"- {bestPosition.EnemiesHit} enemies");

                return PlannedAction.PositionalBuff(
                    ability,
                    bestPosition.Position,
                    $"AOE Taunt on {bestPosition.EnemiesHit} enemies",
                    cost);
            }

            return null;
        }

        #endregion

        #region Self-Targeted AOE (v3.1.23)

        /// <summary>
        /// ★ v3.5.76: Self-Targeted AOE 공격 계획 (Bladedance 등) - 설정 기반
        /// Range=Personal, CanTargetSelf인 DangerousAoE 능력 처리
        /// </summary>
        public static PlannedAction PlanSelfTargetedAoEAttack(
            Situation situation,
            AbilityData attack,
            ref float remainingAP,
            string roleName)
        {
            if (attack == null) return null;
            if (!CombatAPI.IsSelfTargetedAoEAttack(attack)) return null;

            float cost = CombatAPI.GetAbilityAPCost(attack);
            if (cost > remainingAP) return null;

            var caster = situation.Unit;

            // ★ v3.5.76: 설정에서 허용 수 로드
            var aoeConfig = AIConfig.GetAoEConfig();

            // ★ v3.5.87: 게임 API 기반 패턴 내 적/아군 수 계산
            int adjacentEnemies = CombatAPI.CountEnemiesInPattern(
                attack,
                caster.Position,  // Self-AOE이므로 캐스터 위치 기준
                caster.Position,
                situation.Enemies);

            int adjacentAllies = CombatAPI.CountAlliesInPattern(
                attack,
                caster.Position,
                caster.Position,
                caster,
                situation.Allies);

            // ★ v3.9.22: 커스텀 능력(BladeDance 등)은 GetPattern()이 빈 결과 반환
            // BladeDance는 표준 AoE 패턴이 아닌 AbilityCustomBladeDance.CheckEntityTargetable()로
            // InRangeInCells(caster, 1) 범위의 적을 직접 타격 — 거리 기반으로 재계산
            if (adjacentEnemies == 0 && adjacentAllies == 0)
            {
                for (int i = 0; i < situation.Enemies.Count; i++)
                {
                    var enemy = situation.Enemies[i];
                    if (enemy == null || enemy.LifeState.IsDead) continue;
                    if (CombatAPI.GetDistanceInTiles(caster, enemy) <= 1f)
                        adjacentEnemies++;
                }
                for (int i = 0; i < situation.Allies.Count; i++)
                {
                    var ally = situation.Allies[i];
                    if (ally == null || ally == caster) continue;
                    if (CombatAPI.GetDistanceInTiles(caster, ally) <= 1f)
                        adjacentAllies++;
                }
                if (Main.IsDebugEnabled && adjacentEnemies > 0)
                    Main.LogDebug($"[{roleName}] Self-AoE {attack.Name}: pattern empty, distance fallback: {adjacentEnemies} enemies, {adjacentAllies} allies within 1 cell");
            }

            // ★ v3.8.94: MaxPlayerAlliesHit로 통합 — 모든 AoE 동일 기준
            if (adjacentAllies > aoeConfig.MaxPlayerAlliesHit)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] Self-AoE {attack.Name} skipped: {adjacentAllies} > {aoeConfig.MaxPlayerAlliesHit} allies in pattern");
                return null;
            }

            // 효율성 체크: 설정된 최소 적 수 미만이면 낭비
            if (adjacentEnemies < aoeConfig.SelfAoeMinAdjacentEnemies)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] Self-AoE {attack.Name} skipped: {adjacentEnemies} < {aoeConfig.SelfAoeMinAdjacentEnemies} enemies in pattern");
                return null;
            }

            // 자신에게 사용 가능한지 확인
            var selfTarget = new TargetWrapper(caster);
            string reason;
            if (!CombatAPI.CanUseAbilityOn(attack, selfTarget, out reason))
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] Self-AoE {attack.Name} unavailable: {reason}");
                return null;
            }

            remainingAP -= cost;
            Main.Log($"[{roleName}] Self-AoE: {attack.Name} (hitting {adjacentEnemies} enemies, {adjacentAllies} allies nearby)");

            // ★ 자신을 타겟으로 하는 Buff 형태로 반환
            return PlannedAction.Buff(attack, caster,
                $"Self-AoE attack hitting {adjacentEnemies} enemies", cost);
        }

        /// <summary>
        /// ★ v3.8.50: 근접 AOE 공격 계획
        /// 적을 직접 타겟하되, 패턴 내 추가 적을 최대화하는 타겟 선택
        /// BladeDance(Self-Target)와 Point-Target AOE 사이의 데드존 해결
        /// </summary>
        public static PlannedAction PlanMeleeAoEAttack(
            Situation situation,
            ref float remainingAP,
            string roleName)
        {
            if (situation?.AvailableAttacks == null || situation.Enemies == null)
                return null;

            var aoeConfig = AIConfig.GetAoEConfig();
            int minEnemiesForAoE = situation.CharacterSettings?.MinEnemiesForAoE ?? 2;
            var caster = situation.Unit;

            // 1. 근접 AOE 능력 찾기 (AvailableAttacks에서)
            // ★ v3.9.10: new List<> 제거 → 정적 리스트 재사용
            _sharedAbilityList.Clear();
            var meleeAoEAbilities = _sharedAbilityList;
            for (int i = 0; i < situation.AvailableAttacks.Count; i++)
            {
                if (CombatAPI.IsMeleeAoEAbility(situation.AvailableAttacks[i]))
                    meleeAoEAbilities.Add(situation.AvailableAttacks[i]);
            }

            // AvailableAttacks에서 없으면 전체 능력에서 재검색 (DangerousAoE 필터 우회)
            if (meleeAoEAbilities.Count == 0)
            {
                var allAbilities = CombatAPI.GetAvailableAbilities(caster);
                if (allAbilities != null)
                {
                    for (int i = 0; i < allAbilities.Count; i++)
                    {
                        if (CombatAPI.IsMeleeAoEAbility(allAbilities[i]))
                            meleeAoEAbilities.Add(allAbilities[i]);
                    }
                }
            }

            if (meleeAoEAbilities.Count == 0) return null;

            // 2. 각 능력에 대해 최적 타겟 평가
            for (int abilityIdx = 0; abilityIdx < meleeAoEAbilities.Count; abilityIdx++)
            {
                var ability = meleeAoEAbilities[abilityIdx];
                float cost = CombatAPI.GetAbilityAPCost(ability);
                if (cost > remainingAP) continue;

                // 3. Hittable 적 중 패턴 내 적 수가 최대인 타겟 선택
                BaseUnitEntity bestTarget = null;
                int bestEnemyCount = 0;

                var hittableEnemies = situation.HittableEnemies ?? situation.Enemies;
                for (int enemyIdx = 0; enemyIdx < hittableEnemies.Count; enemyIdx++)
                {
                    var enemy = hittableEnemies[enemyIdx];
                    if (enemy == null) continue;
                    try { if (enemy.LifeState?.IsDead == true) continue; } catch { }

                    // ★ v3.9.10: 패턴 1회 계산으로 적+아군 동시 카운트
                    CombatAPI.CountUnitsInPattern(
                        ability, enemy.Position, caster.Position,
                        caster, situation.Enemies, situation.Allies,
                        out int enemies, out int allies);

                    // ★ v3.8.94: MaxPlayerAlliesHit로 통합
                    if (allies > aoeConfig.MaxPlayerAlliesHit) continue;

                    if (enemies > bestEnemyCount)
                    {
                        bestEnemyCount = enemies;
                        bestTarget = enemy;
                    }
                }

                // 4. 최소 적 수 충족 시 실행
                if (bestTarget != null && bestEnemyCount >= minEnemiesForAoE)
                {
                    var targetWrapper = new TargetWrapper(bestTarget);
                    string reason;
                    if (!CombatAPI.CanUseAbilityOn(ability, targetWrapper, out reason))
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}] Melee AOE {ability.Name} blocked on {bestTarget.CharacterName}: {reason}");
                        continue;
                    }

                    remainingAP -= cost;
                    Main.Log($"[{roleName}] Melee AOE: {ability.Name} -> {bestTarget.CharacterName} " +
                        $"(hitting {bestEnemyCount} enemies)");

                    return PlannedAction.Attack(ability, bestTarget,
                        $"Melee AOE hitting {bestEnemyCount} enemies", cost);
                }
            }

            return null;
        }

        #endregion

        #region Helper Methods

        public static bool IsExcluded(BaseUnitEntity target, HashSet<string> excludeTargetIds)
        {
            if (target == null || excludeTargetIds == null) return false;
            return excludeTargetIds.Contains(target.UniqueId);
        }

        public static bool IsAbilityExcluded(AbilityData ability, HashSet<string> excludeAbilityGuids)
        {
            if (ability == null || excludeAbilityGuids == null || excludeAbilityGuids.Count == 0)
                return false;

            var guid = ability.Blueprint?.AssetGuid?.ToString();
            if (string.IsNullOrEmpty(guid)) return false;

            return excludeAbilityGuids.Contains(guid);
        }

        #endregion

        #region Kill Sequence (v3.2.30)

        /// <summary>
        /// ★ v3.2.30: 킬 확정 시퀀스 계획
        /// KillSimulator를 사용하여 다중 능력 조합으로 확정 킬 계획
        /// </summary>
        /// <param name="situation">현재 전투 상황</param>
        /// <param name="target">타겟 유닛</param>
        /// <returns>PlannedAction 리스트 (버프 + 공격)</returns>
        public static List<PlannedAction> PlanKillSequence(Situation situation, BaseUnitEntity target)
        {
            var actions = new List<PlannedAction>();

            if (situation == null || target == null)
                return actions;

            // 설정 체크
            bool useKillSimulator = situation.CharacterSettings?.UseKillSimulator ?? true;
            if (!useKillSimulator)
                return actions;

            var sequence = KillSimulator.FindKillSequence(situation, target);

            if (sequence == null || !sequence.IsConfirmedKill)
                return actions;

            // AP 체크
            if (sequence.APCost > situation.CurrentAP)
                return actions;

            foreach (var ability in sequence.Abilities)
            {
                var timing = AbilityDatabase.GetTiming(ability);
                float apCost = CombatAPI.GetAbilityAPCost(ability);

                // ★ v3.5.00: SelfBuff → PreCombatBuff (SelfBuff enum 없음)
                if (timing == AbilityTiming.PreAttackBuff || timing == AbilityTiming.PreCombatBuff)
                {
                    // ★ v3.5.00: 누락된 reason, apCost 파라미터 추가
                    actions.Add(PlannedAction.Buff(ability, situation.Unit, "Kill sequence buff", apCost));
                }
                else
                {
                    // ★ v3.8.54: Kill Sequence 공격의 아군 안전 체크 (CanTargetFriends/사선)
                    if (CombatAPI.IsPointTargetAbility(ability) || ability.Blueprint?.CanTargetFriends == true)
                    {
                        if (!AoESafetyChecker.IsAoESafeForUnitTarget(ability, situation.Unit, target, situation.Allies))
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[AttackPlanner] Kill sequence BLOCKED by ally safety: {ability.Name} -> {target.CharacterName}");
                            actions.Clear();
                            return actions;
                        }
                    }
                    actions.Add(PlannedAction.Attack(ability, target, "Kill sequence attack", apCost));
                }
            }

            if (actions.Count > 0)
            {
                Main.Log($"[AttackPlanner] Kill sequence: {string.Join(" → ", sequence.Abilities.Select(a => a.Name))} = {sequence.TotalDamage:F0} dmg");
            }

            return actions;
        }

        /// <summary>
        /// ★ v3.2.30: 모든 Hittable 적 중 확정 킬 가능한 최적 타겟 찾기
        /// </summary>
        public static BaseUnitEntity FindBestKillTarget(Situation situation)
        {
            if (situation == null || situation.HittableEnemies == null)
                return null;

            // 설정 체크
            bool useKillSimulator = situation.CharacterSettings?.UseKillSimulator ?? true;
            if (!useKillSimulator)
                return null;

            BaseUnitEntity bestTarget = null;
            float bestEfficiency = 0f;

            foreach (var enemy in situation.HittableEnemies)
            {
                if (enemy == null || enemy.LifeState.IsDead)
                    continue;

                var sequence = KillSimulator.FindKillSequence(situation, enemy);
                if (sequence != null && sequence.IsConfirmedKill && sequence.APCost <= situation.CurrentAP)
                {
                    // 효율 = 데미지 / AP 비용 (높을수록 좋음)
                    if (sequence.Efficiency > bestEfficiency)
                    {
                        bestEfficiency = sequence.Efficiency;
                        bestTarget = enemy;
                    }
                }
            }

            if (bestTarget != null)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[AttackPlanner] Best kill target: {bestTarget.CharacterName} (efficiency={bestEfficiency:F1})");
            }

            return bestTarget;
        }

        #endregion
    }
}
