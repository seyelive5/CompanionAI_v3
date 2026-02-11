using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using UnityEngine;
using CompanionAI_v3.Data;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// ★ v3.1.16: AOE 안전성 검증 - 게임 AI 로직 기반
    /// </summary>
    public static class AoESafetyChecker
    {
        /// <summary>
        /// AOE 위치 평가 결과
        /// </summary>
        public class AoEScore
        {
            public Vector3 Position { get; set; }
            public float Score { get; set; }
            public int EnemiesHit { get; set; }
            public int AlliesHit { get; set; }
            public bool IsSafe { get; set; }
            public List<BaseUnitEntity> AffectedUnits { get; set; } = new List<BaseUnitEntity>();
        }

        /// <summary>
        /// AOE 위치의 안전성과 효율성 평가
        /// 게임의 AOETargetSelector 스코어링 로직 기반
        /// ★ v3.5.76: 설정 기반 페널티 적용
        /// </summary>
        public static AoEScore EvaluateAoEPosition(
            AbilityData ability,
            BaseUnitEntity caster,
            Vector3 targetPosition,
            List<BaseUnitEntity> allUnits,
            int minEnemiesRequired = 0)
        {
            var score = new AoEScore { Position = targetPosition, IsSafe = true };

            float aoERadius = CombatAPI.GetAoERadius(ability);
            if (aoERadius <= 0) aoERadius = 3f;

            // ★ v3.5.76: 설정에서 스코어링 파라미터 로드
            var aoeConfig = AIConfig.GetAoEConfig();
            float HIT_SCORE = aoeConfig.EnemyHitScore;

            float totalScore = 0f;
            int playerPartyAlliesHit = 0;

            foreach (var unit in allUnits)
            {
                if (unit == null || !unit.IsConscious) continue;

                // ★ v3.6.10: 2D 거리 + 높이 체크 통합 (Circle/Directional 패턴별 높이 임계값 적용)
                if (!CombatAPI.IsUnitInAoERange(ability, targetPosition, unit, aoERadius)) continue;

                // 거리 보너스 계산용 2D 거리
                float dist = CombatAPI.MetersToTiles(Vector3.Distance(targetPosition, unit.Position));

                score.AffectedUnits.Add(unit);
                float distanceBonus = HIT_SCORE - dist * dist;

                try
                {
                    if (caster.CombatGroup.IsEnemy(unit))
                    {
                        // 적: +점수
                        score.EnemiesHit++;
                        totalScore += distanceBonus;
                    }
                    else if (caster.CombatGroup.IsAlly(unit))
                    {
                        // 아군 체크
                        score.AlliesHit++;

                        // ★ v3.5.76: 플레이어 파티 아군 - 설정 기반 제어
                        if (!caster.IsPlayerEnemy && unit.IsInPlayerParty)
                        {
                            playerPartyAlliesHit++;

                            // 설정된 최대 허용 수 초과 시 거부
                            if (playerPartyAlliesHit > aoeConfig.MaxPlayerAlliesHit)
                            {
                                score.IsSafe = false;
                                score.Score = float.MinValue;
                                CompanionAI_v3.Main.LogDebug($"[AOE] Too many player allies ({playerPartyAlliesHit} > {aoeConfig.MaxPlayerAlliesHit}) - rejected");
                                return score;
                            }

                            // 설정된 배수로 페널티 적용 (기존 3.0 → 기본 2.0)
                            totalScore -= aoeConfig.PlayerAllyPenaltyMultiplier * HIT_SCORE;
                            CompanionAI_v3.Main.LogDebug($"[AOE] Player party ally in range: {unit.CharacterName} - penalty {aoeConfig.PlayerAllyPenaltyMultiplier}x applied");
                            continue;  // NPC 페널티 중복 적용 방지
                        }

                        // NPC 아군: 설정된 배수 페널티
                        totalScore -= aoeConfig.NpcAllyPenaltyMultiplier * HIT_SCORE;
                    }
                    else if (unit == caster)
                    {
                        // 캐스터 자신: 설정된 배수 페널티
                        totalScore -= aoeConfig.CasterSelfPenaltyMultiplier * HIT_SCORE;
                        score.AlliesHit++;
                    }
                }
                catch
                {
                    // 피아 구분 실패 시 적으로 간주
                    score.EnemiesHit++;
                    totalScore += distanceBonus;
                }
            }

            score.Score = totalScore;

            // ★ v3.5.76: minEnemiesRequired 파라미터 활용 (0이면 기본값 2)
            int effectiveMinEnemies = minEnemiesRequired > 0 ? minEnemiesRequired : 2;
            score.IsSafe = score.IsSafe && totalScore > 0 && score.EnemiesHit >= effectiveMinEnemies;

            return score;
        }

        /// <summary>
        /// 최적의 AOE 시전 위치 찾기
        /// </summary>
        public static AoEScore FindBestAoEPosition(
            AbilityData ability,
            BaseUnitEntity caster,
            List<BaseUnitEntity> enemies,
            List<BaseUnitEntity> allies,
            int minEnemiesRequired = 2)
        {
            if (enemies == null || enemies.Count < minEnemiesRequired)
                return null;

            var allUnits = new List<BaseUnitEntity>();
            allUnits.AddRange(enemies.Where(e => e != null));
            if (allies != null) allUnits.AddRange(allies.Where(a => a != null));
            allUnits.Add(caster);

            float aoERadius = CombatAPI.GetAoERadius(ability);
            // ★ v3.5.98: 타일 단위 사용
            float abilityRange = CombatAPI.GetAbilityRangeInTiles(ability);

            var candidates = new List<AoEScore>();

            // 전략 1: 각 적 위치 중심
            foreach (var enemy in enemies)
            {
                if (enemy == null || !enemy.IsConscious) continue;

                // ★ v3.5.98: 타일 단위로 변환
                float distToCaster = CombatAPI.MetersToTiles(Vector3.Distance(caster.Position, enemy.Position));
                if (distToCaster > abilityRange) continue;

                var score = EvaluateAoEPosition(ability, caster, enemy.Position, allUnits);
                if (score.IsSafe && score.EnemiesHit >= minEnemiesRequired)
                    candidates.Add(score);
            }

            // 전략 2: 적 2명 중간점
            for (int i = 0; i < enemies.Count; i++)
            {
                for (int j = i + 1; j < enemies.Count; j++)
                {
                    var e1 = enemies[i];
                    var e2 = enemies[j];
                    if (e1 == null || e2 == null || !e1.IsConscious || !e2.IsConscious) continue;

                    Vector3 center = (e1.Position + e2.Position) / 2f;
                    // ★ v3.5.98: 타일 단위로 변환
                    float distToCaster = CombatAPI.MetersToTiles(Vector3.Distance(caster.Position, center));
                    if (distToCaster > abilityRange) continue;

                    // ★ v3.7.64: BattlefieldGrid Walkable 체크 (중간점이 장애물 안인지)
                    if (Analysis.BattlefieldGrid.Instance.IsValid && !Analysis.BattlefieldGrid.Instance.IsWalkable(center))
                        continue;

                    var score = EvaluateAoEPosition(ability, caster, center, allUnits);
                    if (score.IsSafe && score.EnemiesHit >= minEnemiesRequired)
                        candidates.Add(score);
                }
            }

            // 최고 점수 선택
            return candidates
                .OrderByDescending(c => c.Score)
                .FirstOrDefault();
        }

        /// <summary>
        /// ★ v3.9.08: 가상 위치에서 최적 Circle AoE 타겟 위치 탐색
        /// AoE 재배치용: 시전자가 fromPosition에 있다고 가정하고 사거리 체크
        /// 기존 FindBestAoEPosition()과 동일하되 caster.Position → fromPosition
        /// </summary>
        public static AoEScore FindBestAoEPositionFromPosition(
            AbilityData ability,
            BaseUnitEntity caster,
            Vector3 fromPosition,
            List<BaseUnitEntity> enemies,
            List<BaseUnitEntity> allies,
            int minEnemiesRequired = 2)
        {
            if (enemies == null || enemies.Count < minEnemiesRequired)
                return null;

            var allUnits = new List<BaseUnitEntity>();
            allUnits.AddRange(enemies.Where(e => e != null));
            if (allies != null) allUnits.AddRange(allies.Where(a => a != null));
            allUnits.Add(caster);

            float aoERadius = CombatAPI.GetAoERadius(ability);
            float abilityRange = CombatAPI.GetAbilityRangeInTiles(ability);

            var candidates = new List<AoEScore>();

            // 전략 1: 각 적 위치 중심
            foreach (var enemy in enemies)
            {
                if (enemy == null || !enemy.IsConscious) continue;

                // ★ v3.9.08: fromPosition에서 사거리 체크 (caster.Position 대신)
                float distFromPos = CombatAPI.MetersToTiles(Vector3.Distance(fromPosition, enemy.Position));
                if (distFromPos > abilityRange) continue;

                var score = EvaluateAoEPosition(ability, caster, enemy.Position, allUnits);
                if (score.IsSafe && score.EnemiesHit >= minEnemiesRequired)
                    candidates.Add(score);
            }

            // 전략 2: 적 2명 중간점
            for (int i = 0; i < enemies.Count; i++)
            {
                for (int j = i + 1; j < enemies.Count; j++)
                {
                    var e1 = enemies[i];
                    var e2 = enemies[j];
                    if (e1 == null || e2 == null || !e1.IsConscious || !e2.IsConscious) continue;

                    Vector3 center = (e1.Position + e2.Position) / 2f;
                    // ★ v3.9.08: fromPosition에서 사거리 체크
                    float distFromPos = CombatAPI.MetersToTiles(Vector3.Distance(fromPosition, center));
                    if (distFromPos > abilityRange) continue;

                    if (Analysis.BattlefieldGrid.Instance.IsValid && !Analysis.BattlefieldGrid.Instance.IsWalkable(center))
                        continue;

                    var score = EvaluateAoEPosition(ability, caster, center, allUnits);
                    if (score.IsSafe && score.EnemiesHit >= minEnemiesRequired)
                        candidates.Add(score);
                }
            }

            return candidates
                .OrderByDescending(c => c.Score)
                .FirstOrDefault();
        }

        /// <summary>
        /// ★ v3.5.76: 간단한 AOE 안전성 체크 - 설정 기반
        /// 아군 피격 허용 수는 설정으로 제어
        /// </summary>
        public static bool IsAoESafe(
            AbilityData ability,
            BaseUnitEntity caster,
            Vector3 targetPosition,
            List<BaseUnitEntity> allies)
        {
            // ★ v3.8.64: AvoidFriendlyFire 설정 반영
            try
            {
                var settings = ModSettings.Instance?.GetOrCreateSettings(caster.UniqueId);
                if (settings != null && !settings.AvoidFriendlyFire)
                    return true;  // 사용자가 아군 피격 방지 비활성화
            }
            catch { }

            float aoERadius = CombatAPI.GetAoERadius(ability);
            if (aoERadius <= 0) return true;

            if (allies == null) return true;

            // ★ v3.5.76: 설정에서 최대 허용 수 로드
            var aoeConfig = AIConfig.GetAoEConfig();
            int playerPartyAlliesInRange = 0;

            foreach (var ally in allies)
            {
                if (ally == null || !ally.IsConscious) continue;

                // ★ v3.6.10: 2D 거리 + 높이 체크 통합
                if (!CombatAPI.IsUnitInAoERange(ability, targetPosition, ally, aoERadius)) continue;

                try
                {
                    if (!caster.IsPlayerEnemy && ally.IsInPlayerParty)
                    {
                        playerPartyAlliesInRange++;

                        // ★ v3.5.76: 설정된 최대 수 초과 시 거부
                        if (playerPartyAlliesInRange > aoeConfig.MaxPlayerAlliesHit)
                            return false;
                    }
                }
                catch (Exception ex) { Main.LogDebug($"[AoESafety] {ex.Message}"); }
            }

            // 설정된 수 이하는 허용 (EvaluateAoEPosition에서 페널티로 처리)
            return true;
        }

        /// <summary>
        /// ★ v3.8.45: 유닛 타겟 능력의 아군 안전 체크
        /// ★ v3.8.70: 위치 기반 오버로드로 위임
        /// </summary>
        public static bool IsAoESafeForUnitTarget(
            AbilityData ability,
            BaseUnitEntity caster,
            BaseUnitEntity target,
            List<BaseUnitEntity> allies)
        {
            // ★ v3.8.70: 위치 기반 오버로드로 위임
            return IsAoESafeForUnitTargetFromPosition(ability, caster.Position, caster, target, allies);
        }

        /// <summary>
        /// ★ v3.8.70: 지정된 위치에서 타겟 공격 시 아군 scatter/AOE 안전 체크
        /// 이동 후보 위치 평가에 사용 (CountHittableEnemiesFromPosition 등)
        /// ★ v3.8.64: 게임 검증 — GridPatterns.CalcScatterShot 기반
        /// </summary>
        public static bool IsAoESafeForUnitTargetFromPosition(
            AbilityData ability,
            Vector3 fromPosition,
            BaseUnitEntity casterEntity,
            BaseUnitEntity target,
            List<BaseUnitEntity> allies)
        {
            // ★ v3.8.64: AvoidFriendlyFire 설정 반영
            try
            {
                var settings = ModSettings.Instance?.GetOrCreateSettings(casterEntity.UniqueId);
                if (settings != null && !settings.AvoidFriendlyFire)
                    return true;  // 사용자가 아군 피격 방지 비활성화
            }
            catch { }

            // AOE 반경 결정: GetAoERadius → GetPatternInfo 폴백
            float aoERadius = CombatAPI.GetAoERadius(ability);
            if (aoERadius <= 0)
            {
                var patternInfo = CombatAPI.GetPatternInfo(ability);
                if (patternInfo != null && patternInfo.IsValid)
                    aoERadius = patternInfo.Radius;
            }

            // ★ v3.8.82: BlueprintCache에서 캐시된 속성 사용
            // IsScatter를 직접 확인 — CanTargetFriends 프록시 불필요
            var bpInfo = BlueprintCache.GetOrCache(ability);
            bool hasScatterDanger = bpInfo?.IsScatter ?? ability?.IsScatter ?? false;
            // ★ v3.8.88: ControlledScatter는 아군 자동 회피 보장 (게임 엔진: 아군 만나면 해당 레이 전체 AutoMiss)
            if (hasScatterDanger && (bpInfo?.ControlledScatter ?? false))
                hasScatterDanger = false;

            // AOE 효과도 없고 scatter 위험도 없으면 안전
            if (aoERadius <= 0 && !hasScatterDanger) return true;

            // ★ v3.8.65: 게임 검증 — 스캐터 레이 사거리 제한
            int scatterRange = hasScatterDanger ? CombatAPI.GetAbilityRangeInTiles(ability) : 0;
            if (allies == null) return true;

            var aoeConfig = AIConfig.GetAoEConfig();
            int playerPartyAlliesInRange = 0;

            foreach (var ally in allies)
            {
                if (ally == null || !ally.IsConscious) continue;
                if (ally == target) continue;        // 타겟 자체는 제외
                if (ally == casterEntity) continue;  // 캐스터 자신은 제외

                bool isInDanger = false;

                if (aoERadius > 0)
                {
                    // AOE 반경 기반 체크
                    if (CombatAPI.IsUnitInAoERange(ability, target.Position, ally, aoERadius))
                        isInDanger = true;
                }
                else if (hasScatterDanger)
                {
                    // ★ v3.8.64~65: 5-레이 스캐터 패턴 기반 체크 (원거리 산탄 무기만)
                    // ★ v3.8.70: caster.Position → fromPosition (이동 후보 위치 지원)
                    Vector3 casterToTarget = target.Position - fromPosition;
                    Vector3 casterToAlly = ally.Position - fromPosition;
                    float casterToTargetMag = casterToTarget.magnitude;

                    if (casterToTargetMag > 0.1f)
                    {
                        Vector3 dirNorm = casterToTarget / casterToTargetMag;

                        // 캐스터 뒤에 있으면 안전 (스캐터 레이는 전방으로만)
                        float projection = Vector3.Dot(casterToAlly, dirNorm);
                        if (projection > 0)
                        {
                            // ★ v3.8.65: 사거리 제한
                            float projectionTiles = CombatAPI.MetersToTiles(projection);
                            if (projectionTiles > scatterRange) continue;

                            // 사선으로부터의 수직 거리 (타일 단위)
                            float perpDistMeters = Vector3.Cross(dirNorm, casterToAlly).magnitude;
                            float perpDistTiles = CombatAPI.MetersToTiles(perpDistMeters);

                            // 게임: 5-레이 패턴, 중심선에서 수직 2셀 이내
                            if (perpDistTiles <= 2f)
                            {
                                isInDanger = true;
                            }
                        }
                    }
                }

                if (!isInDanger) continue;

                try
                {
                    if (!casterEntity.IsPlayerEnemy && ally.IsInPlayerParty)
                    {
                        playerPartyAlliesInRange++;

                        // ★ v3.8.54: scatter 직격은 0 허용
                        int effectiveMaxAllies = (aoERadius <= 0 && hasScatterDanger) ? 0 : aoeConfig.MaxPlayerAlliesHit;
                        if (playerPartyAlliesInRange > effectiveMaxAllies)
                        {
                            string checkType = aoERadius > 0 ? $"radius={aoERadius:F1}" : "scatter";
                            if (Main.IsDebugEnabled) Main.LogDebug($"[AOE] Unit-target safety: {ability.Name} -> {target.CharacterName} blocked ({checkType}, allies={playerPartyAlliesInRange} > max={effectiveMaxAllies})");
                            return false;
                        }
                    }
                }
                catch (Exception ex) { Main.LogDebug($"[AoESafety] {ex.Message}"); }
            }

            return true;
        }

        #region Ally-Targeting AOE (v3.1.17)

        /// <summary>
        /// 아군 타겟 AOE 위치 평가 (버프/힐용)
        /// 적 타겟 AOE와 반대로 아군이 많을수록 높은 점수
        /// </summary>
        public static AoEScore EvaluateAllyAoEPosition(
            AbilityData ability,
            BaseUnitEntity caster,
            Vector3 targetPosition,
            List<BaseUnitEntity> allies,
            bool requiresWounded = false)
        {
            var score = new AoEScore { Position = targetPosition, IsSafe = true };

            float aoERadius = CombatAPI.GetAoERadius(ability);
            if (aoERadius <= 0) aoERadius = 3f;

            const float HIT_SCORE = 10000f;
            float totalScore = 0f;

            foreach (var unit in allies)
            {
                if (unit == null || !unit.IsConscious) continue;

                // 힐 AOE: 부상 아군만 카운트
                if (requiresWounded)
                {
                    float hpPercent = CombatCache.GetHPPercent(unit);
                    if (hpPercent >= 90f) continue;  // 거의 풀피면 스킵
                }

                // ★ v3.6.10: 2D 거리 + 높이 체크 통합
                if (!CombatAPI.IsUnitInAoERange(ability, targetPosition, unit, aoERadius)) continue;

                // 거리 보너스 계산용 2D 거리
                float dist = CombatAPI.MetersToTiles(Vector3.Distance(targetPosition, unit.Position));

                score.AffectedUnits.Add(unit);
                score.AlliesHit++;

                // 거리가 가까울수록 높은 점수
                float distanceBonus = HIT_SCORE - dist * dist;

                // 힐 AOE: HP가 낮을수록 보너스
                if (requiresWounded)
                {
                    float hpPercent = CombatCache.GetHPPercent(unit);
                    float hpBonus = (100f - hpPercent) * 100f;  // HP 50% = +5000
                    distanceBonus += hpBonus;
                }

                totalScore += distanceBonus;
            }

            score.Score = totalScore;
            // 최소 2명 이상 커버해야 의미 있음
            score.IsSafe = score.AlliesHit >= 2;

            return score;
        }

        /// <summary>
        /// 최적의 아군 AOE 시전 위치 찾기 (버프/힐용)
        /// </summary>
        public static AoEScore FindBestAllyAoEPosition(
            AbilityData ability,
            BaseUnitEntity caster,
            List<BaseUnitEntity> allies,
            int minAlliesRequired = 2,
            bool requiresWounded = false)
        {
            if (allies == null || allies.Count < minAlliesRequired)
                return null;

            float aoERadius = CombatAPI.GetAoERadius(ability);
            // ★ v3.5.98: 타일 단위 사용
            float abilityRange = CombatAPI.GetAbilityRangeInTiles(ability);

            var candidates = new List<AoEScore>();

            // 전략 1: 각 아군 위치 중심
            foreach (var ally in allies)
            {
                if (ally == null || !ally.IsConscious) continue;

                // 힐: 풀피 아군은 스킵
                if (requiresWounded)
                {
                    float hpPercent = CombatCache.GetHPPercent(ally);
                    if (hpPercent >= 90f) continue;
                }

                // ★ v3.5.98: 타일 단위로 변환
                float distToCaster = CombatAPI.MetersToTiles(Vector3.Distance(caster.Position, ally.Position));
                if (distToCaster > abilityRange) continue;

                var evalScore = EvaluateAllyAoEPosition(ability, caster, ally.Position, allies, requiresWounded);
                if (evalScore.IsSafe && evalScore.AlliesHit >= minAlliesRequired)
                    candidates.Add(evalScore);
            }

            // 전략 2: 아군 2명 중간점
            for (int i = 0; i < allies.Count; i++)
            {
                for (int j = i + 1; j < allies.Count; j++)
                {
                    var a1 = allies[i];
                    var a2 = allies[j];
                    if (a1 == null || a2 == null || !a1.IsConscious || !a2.IsConscious) continue;

                    Vector3 center = (a1.Position + a2.Position) / 2f;
                    // ★ v3.5.98: 타일 단위로 변환
                    float distToCaster = CombatAPI.MetersToTiles(Vector3.Distance(caster.Position, center));
                    if (distToCaster > abilityRange) continue;

                    // ★ v3.7.64: BattlefieldGrid Walkable 체크 (중간점이 장애물 안인지)
                    if (Analysis.BattlefieldGrid.Instance.IsValid && !Analysis.BattlefieldGrid.Instance.IsWalkable(center))
                        continue;

                    var evalScore = EvaluateAllyAoEPosition(ability, caster, center, allies, requiresWounded);
                    if (evalScore.IsSafe && evalScore.AlliesHit >= minAlliesRequired)
                        candidates.Add(evalScore);
                }
            }

            // 최고 점수 선택
            return candidates
                .OrderByDescending(c => c.Score)
                .FirstOrDefault();
        }

        #endregion

        #region Directional Pattern AOE (v3.1.18)

        /// <summary>
        /// ★ v3.1.18: 방향성 AOE(Cone/Ray/Sector)의 최적 타겟 찾기
        /// Circle 패턴과 달리 방향이 중요하므로 각 적을 향한 방향별로 평가
        /// </summary>
        public static AoEScore FindBestDirectionalAoETarget(
            Kingmaker.UnitLogic.Abilities.AbilityData ability,
            BaseUnitEntity caster,
            System.Collections.Generic.List<BaseUnitEntity> enemies,
            System.Collections.Generic.List<BaseUnitEntity> allies,
            int minEnemiesRequired = 2)
        {
            if (enemies == null || enemies.Count < minEnemiesRequired)
                return null;

            var patternType = CombatAPI.GetPatternType(ability);
            if (!patternType.HasValue) return null;

            float radius = CombatAPI.GetAoERadius(ability);
            // ★ v3.5.98: 타일 단위 사용
            float abilityRange = CombatAPI.GetAbilityRangeInTiles(ability);
            float angle = CombatAPI.GetPatternAngle(ability);

            // ★ v3.8.33: 방향성 패턴(Ray/Cone/Sector)은 caster에서 시작하여 radius만큼만 뻗어나감
            // abilityRange(무기 사거리)가 아닌 radius(패턴 반경)가 실제 유효 사거리!
            // 예: 무기 사거리 15, 패턴 반경 6 → Ray는 caster에서 6타일만 이동
            float effectiveRange = radius;  // 방향성 패턴은 항상 patternRadius 사용

            var candidates = new System.Collections.Generic.List<AoEScore>();

            // 각 적을 주 타겟으로 평가 (방향 결정)
            foreach (var primaryTarget in enemies)
            {
                if (primaryTarget == null || !primaryTarget.IsConscious) continue;

                // ★ v3.5.98: 타일 단위로 변환
                // ★ v3.8.33: abilityRange 대신 effectiveRange(패턴 반경) 사용
                float distToCaster = CombatAPI.MetersToTiles(Vector3.Distance(caster.Position, primaryTarget.Position));
                if (distToCaster > effectiveRange) continue;

                // 이 적을 향한 방향으로 패턴 시전 시 영향받는 유닛 계산
                Vector3 direction = (primaryTarget.Position - caster.Position).normalized;

                var score = EvaluateDirectionalAoE(
                    ability, caster, direction, primaryTarget,
                    enemies, allies, patternType.Value, radius, angle);

                if (score.IsSafe && score.EnemiesHit >= minEnemiesRequired)
                    candidates.Add(score);
            }

            return candidates.OrderByDescending(c => c.Score).FirstOrDefault();
        }

        /// <summary>
        /// ★ v3.9.08: 가상 위치에서 최적 방향성 AoE 타겟 탐색
        /// AoE 재배치용: 시전자가 fromPosition에 있다고 가정
        /// </summary>
        public static AoEScore FindBestDirectionalAoETargetFromPosition(
            Kingmaker.UnitLogic.Abilities.AbilityData ability,
            BaseUnitEntity caster,
            Vector3 fromPosition,
            System.Collections.Generic.List<BaseUnitEntity> enemies,
            System.Collections.Generic.List<BaseUnitEntity> allies,
            int minEnemiesRequired = 2)
        {
            if (enemies == null || enemies.Count < minEnemiesRequired)
                return null;

            var patternType = CombatAPI.GetPatternType(ability);
            if (!patternType.HasValue) return null;

            float radius = CombatAPI.GetAoERadius(ability);
            float angle = CombatAPI.GetPatternAngle(ability);

            // ★ v3.8.33: 방향성 패턴은 패턴 반경이 실제 유효 사거리
            float effectiveRange = radius;

            var candidates = new System.Collections.Generic.List<AoEScore>();

            foreach (var primaryTarget in enemies)
            {
                if (primaryTarget == null || !primaryTarget.IsConscious) continue;

                // ★ v3.9.08: fromPosition에서 사거리 체크
                float distFromPos = CombatAPI.MetersToTiles(Vector3.Distance(fromPosition, primaryTarget.Position));
                if (distFromPos > effectiveRange) continue;

                // ★ v3.9.08: fromPosition에서 방향 벡터 계산
                Vector3 direction = (primaryTarget.Position - fromPosition).normalized;

                var score = EvaluateDirectionalAoEFromPosition(
                    ability, caster, fromPosition, direction, primaryTarget,
                    enemies, allies, patternType.Value, radius, angle);

                if (score.IsSafe && score.EnemiesHit >= minEnemiesRequired)
                    candidates.Add(score);
            }

            return candidates.OrderByDescending(c => c.Score).FirstOrDefault();
        }

        /// <summary>
        /// ★ v3.5.76: 특정 방향의 Cone/Ray/Sector 패턴 평가 - 설정 기반
        /// </summary>
        public static AoEScore EvaluateDirectionalAoE(
            Kingmaker.UnitLogic.Abilities.AbilityData ability,
            BaseUnitEntity caster,
            Vector3 direction,
            BaseUnitEntity primaryTarget,
            System.Collections.Generic.List<BaseUnitEntity> enemies,
            System.Collections.Generic.List<BaseUnitEntity> allies,
            Kingmaker.Blueprints.PatternType patternType,
            float radius,
            float angle,
            int minEnemiesRequired = 0)
        {
            var score = new AoEScore
            {
                Position = primaryTarget.Position,  // 주 타겟 위치 저장 (타겟팅용)
                IsSafe = true
            };

            // ★ v3.5.76: 설정에서 스코어링 파라미터 로드
            var aoeConfig = AIConfig.GetAoEConfig();
            float HIT_SCORE = aoeConfig.EnemyHitScore;
            float totalScore = 0f;
            int playerPartyAlliesHit = 0;

            // 모든 유닛 체크
            var allUnits = new System.Collections.Generic.List<BaseUnitEntity>();
            allUnits.AddRange(enemies.Where(e => e != null));
            if (allies != null) allUnits.AddRange(allies.Where(a => a != null));

            foreach (var unit in allUnits)
            {
                if (unit == null || !unit.IsConscious) continue;

                // ★ v3.6.10: 2D 거리 + 높이 + 각도 체크 통합 (Directional은 0.3m 높이 제한)
                if (!CombatAPI.IsUnitInDirectionalAoERange(caster.Position, direction, unit, radius, angle, patternType))
                    continue;

                // 거리 보너스 계산용 2D 거리
                Vector3 toUnit = unit.Position - caster.Position;
                float dist = CombatAPI.MetersToTiles(toUnit.magnitude);

                score.AffectedUnits.Add(unit);
                float distanceBonus = HIT_SCORE - dist * dist;

                try
                {
                    if (caster.CombatGroup.IsEnemy(unit))
                    {
                        score.EnemiesHit++;
                        totalScore += distanceBonus;
                    }
                    else if (caster.CombatGroup.IsAlly(unit))
                    {
                        score.AlliesHit++;

                        // ★ v3.5.76: 플레이어 파티 아군 - 설정 기반 제어
                        if (!caster.IsPlayerEnemy && unit.IsInPlayerParty)
                        {
                            playerPartyAlliesHit++;

                            // 설정된 최대 허용 수 초과 시 거부
                            if (playerPartyAlliesHit > aoeConfig.MaxPlayerAlliesHit)
                            {
                                score.IsSafe = false;
                                score.Score = float.MinValue;
                                return score;
                            }

                            // 설정된 배수로 페널티 적용
                            totalScore -= aoeConfig.PlayerAllyPenaltyMultiplier * HIT_SCORE;
                            CompanionAI_v3.Main.LogDebug($"[AOE] Player party ally in directional pattern: {unit.CharacterName} - penalty {aoeConfig.PlayerAllyPenaltyMultiplier}x applied");
                            continue;  // NPC 페널티 중복 적용 방지
                        }

                        totalScore -= aoeConfig.NpcAllyPenaltyMultiplier * HIT_SCORE;
                    }
                }
                catch
                {
                    score.EnemiesHit++;
                    totalScore += distanceBonus;
                }
            }

            // 캐스터 자신 체크 (Cone의 경우 원점에서 시작하므로 자신은 안전)
            // Ray도 마찬가지로 캐스터 위치에서 시작

            score.Score = totalScore;
            // ★ v3.5.76: minEnemiesRequired 활용
            int effectiveMinEnemies = minEnemiesRequired > 0 ? minEnemiesRequired : 2;
            score.IsSafe = score.IsSafe && totalScore > 0 && score.EnemiesHit >= effectiveMinEnemies;

            return score;
        }

        /// <summary>
        /// ★ v3.9.08: 가상 위치에서 방향성 AoE 패턴 평가
        /// 기존 EvaluateDirectionalAoE와 동일하되 caster.Position → fromPosition
        /// </summary>
        public static AoEScore EvaluateDirectionalAoEFromPosition(
            Kingmaker.UnitLogic.Abilities.AbilityData ability,
            BaseUnitEntity caster,
            Vector3 fromPosition,
            Vector3 direction,
            BaseUnitEntity primaryTarget,
            System.Collections.Generic.List<BaseUnitEntity> enemies,
            System.Collections.Generic.List<BaseUnitEntity> allies,
            Kingmaker.Blueprints.PatternType patternType,
            float radius,
            float angle,
            int minEnemiesRequired = 0)
        {
            var score = new AoEScore
            {
                Position = primaryTarget.Position,
                IsSafe = true
            };

            var aoeConfig = AIConfig.GetAoEConfig();
            float HIT_SCORE = aoeConfig.EnemyHitScore;
            float totalScore = 0f;
            int playerPartyAlliesHit = 0;

            var allUnits = new System.Collections.Generic.List<BaseUnitEntity>();
            allUnits.AddRange(enemies.Where(e => e != null));
            if (allies != null) allUnits.AddRange(allies.Where(a => a != null));

            foreach (var unit in allUnits)
            {
                if (unit == null || !unit.IsConscious) continue;

                // ★ v3.9.08: fromPosition에서 패턴 범위 체크
                if (!CombatAPI.IsUnitInDirectionalAoERange(fromPosition, direction, unit, radius, angle, patternType))
                    continue;

                // ★ v3.9.08: fromPosition에서 거리 보너스 계산
                Vector3 toUnit = unit.Position - fromPosition;
                float dist = CombatAPI.MetersToTiles(toUnit.magnitude);

                score.AffectedUnits.Add(unit);
                float distanceBonus = HIT_SCORE - dist * dist;

                try
                {
                    if (caster.CombatGroup.IsEnemy(unit))
                    {
                        score.EnemiesHit++;
                        totalScore += distanceBonus;
                    }
                    else if (caster.CombatGroup.IsAlly(unit))
                    {
                        score.AlliesHit++;

                        if (!caster.IsPlayerEnemy && unit.IsInPlayerParty)
                        {
                            playerPartyAlliesHit++;

                            if (playerPartyAlliesHit > aoeConfig.MaxPlayerAlliesHit)
                            {
                                score.IsSafe = false;
                                score.Score = float.MinValue;
                                return score;
                            }

                            totalScore -= aoeConfig.PlayerAllyPenaltyMultiplier * HIT_SCORE;
                            continue;
                        }

                        totalScore -= aoeConfig.NpcAllyPenaltyMultiplier * HIT_SCORE;
                    }
                }
                catch
                {
                    score.EnemiesHit++;
                    totalScore += distanceBonus;
                }
            }

            score.Score = totalScore;
            int effectiveMinEnemies = minEnemiesRequired > 0 ? minEnemiesRequired : 2;
            score.IsSafe = score.IsSafe && totalScore > 0 && score.EnemiesHit >= effectiveMinEnemies;

            return score;
        }

        // ★ v3.8.65: IsInDirectionalPattern 삭제 — 데드코드였음
        // 실제 사용 코드: CombatAPI.IsUnitInDirectionalAoERange (Ray 수정 반영 완료)

        #endregion

        #region Cluster-Based AOE (v3.3.00)

        /// <summary>
        /// ★ v3.3.00: 클러스터 탐지 기반 최적 AOE 위치 탐색
        /// 클러스터 없으면 기존 방식으로 폴백
        /// </summary>
        /// <param name="ability">AOE 능력</param>
        /// <param name="caster">시전자</param>
        /// <param name="enemies">적 목록</param>
        /// <param name="allies">아군 목록</param>
        /// <param name="minEnemiesRequired">최소 적중 적 수</param>
        /// <returns>최적 AOE 위치 평가 결과</returns>
        public static AoEScore FindBestAoEPositionWithClusters(
            AbilityData ability,
            BaseUnitEntity caster,
            List<BaseUnitEntity> enemies,
            List<BaseUnitEntity> allies,
            int minEnemiesRequired = 2)
        {
            if (enemies == null || enemies.Count < minEnemiesRequired)
                return null;

            try
            {
                // 클러스터 탐색
                var bestCluster = Analysis.ClusterDetector.FindBestClusterForAbility(
                    ability, caster, enemies, allies);

                if (bestCluster != null && bestCluster.IsValid)
                {
                    float aoERadius = CombatAPI.GetAoERadius(ability);
                    if (aoERadius <= 0) aoERadius = 3f;

                    // 클러스터 내 최적 위치 탐색
                    Vector3? optimalPos = Analysis.ClusterDetector.FindOptimalAoEPosition(
                        bestCluster, ability, caster, allies, aoERadius);

                    if (optimalPos.HasValue)
                    {
                        // 모든 유닛 목록 생성
                        var allUnits = new List<BaseUnitEntity>();
                        allUnits.AddRange(enemies.Where(e => e != null));
                        if (allies != null) allUnits.AddRange(allies.Where(a => a != null));
                        allUnits.Add(caster);

                        var score = EvaluateAoEPosition(ability, caster, optimalPos.Value, allUnits);

                        if (score.IsSafe && score.EnemiesHit >= minEnemiesRequired)
                        {
                            // 클러스터 밀도 보너스 추가
                            score.Score += bestCluster.Density * 5000f;

                            CompanionAI_v3.Main.Log($"[AOE] Cluster-based position: " +
                                $"{score.EnemiesHit} hits, density bonus={bestCluster.Density * 5000f:F0}, " +
                                $"total={score.Score:F0}");

                            return score;
                        }
                    }
                }

                // 폴백: 기존 방식
                CompanionAI_v3.Main.LogDebug("[AOE] No valid cluster found, falling back to legacy method");
                return FindBestAoEPosition(ability, caster, enemies, allies, minEnemiesRequired);
            }
            catch (Exception ex)
            {
                CompanionAI_v3.Main.LogDebug($"[AOE] Cluster-based search failed: {ex.Message}, using legacy");
                return FindBestAoEPosition(ability, caster, enemies, allies, minEnemiesRequired);
            }
        }

        #endregion
    }
}
