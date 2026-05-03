using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Parts;
using Kingmaker.View.Covers;
using Pathfinding;
using UnityEngine;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.Data;
using CompanionAI_v3.Logging;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.GameInterface
{
    public static partial class MovementAPI
    {
        #region Best Position Finding

        /// <summary>
        /// ★ v3.0.62: AoE/위협 점수 통합
        /// ★ v3.1.01: predictedMP 파라미터 추가 - MP 회복 예측 후 이동 계획 지원
        /// ★ v3.2.00: influenceMap 파라미터 추가 - 영향력 맵 기반 위협/통제 점수
        /// ★ v3.2.25: role 파라미터 추가 - Role별 전선 위치 점수
        /// ★ v3.4.00: predictiveMap 파라미터 추가 - 적 이동 예측 기반 위협 점수
        /// </summary>
        public static PositionScore FindRangedAttackPositionSync(
            BaseUnitEntity unit,
            List<BaseUnitEntity> enemies,
            float weaponRange = Settings.SC.FallbackWeaponRange,
            float minSafeDistance = 5f,
            float predictedMP = 0f,
            AIRole role = AIRole.Auto,
            Vector3? lastMoveOrigin = null)
        {
            // ★ v3.8.13: AI용 패스파인딩 사용 - 경로 위협 데이터(ProvokedAttacks, EnteredAoE) 포함
            var tiles = predictedMP > 0
                ? FindAllReachableTilesWithThreatsSync(unit, predictedMP)
                : FindAllReachableTilesWithThreatsSync(unit);
            if (tiles == null || tiles.Count == 0)
            {
                if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] {unit.CharacterName}: No reachable tiles (predictedMP={predictedMP:F1})");
                return null;
            }

            // ★ v3.8.13: 이제 AI 셀에 실제 경로 위협 데이터가 포함됨
            // BattlefieldGrid 검증만 추가로 수행
            // ★ v3.18.18: DamagingAoE 회피 — 안전한 유닛이 AoE 안으로 이동하지 않도록
            bool avoidHazardZones = !CombatAPI.IsUnitInHazardZone(unit);
            var aiCells = new Dictionary<GraphNode, WarhammerPathAiCell>();
            foreach (var kvp in tiles)
            {
                var aiCell = kvp.Value;
                var node = aiCell.Node as CustomGridNodeBase;
                if (node == null || !aiCell.IsCanStand) continue;

                // ★ v3.7.62: BattlefieldGrid 검증 - Walkable/점유 체크
                if (!BattlefieldGrid.Instance.ValidateNode(unit, node))
                    continue;

                // ★ v3.18.18: DamagingAoE 위치 필터링
                if (avoidHazardZones && CombatAPI.IsPositionInHazardZone(node.Vector3Position, unit))
                    continue;

                aiCells[kvp.Key] = aiCell;
            }

            var scores = EvaluateAllPositions(unit, aiCells, enemies, MovementGoal.RangedAttackPosition, weaponRange, minSafeDistance);

            // ★ v3.6.8: Scatter/근접 공격 여부 감지 (위치 보너스 계산에 사용)
            // Scatter/근접은 거리와 무관하게 100% 명중 → 거리 보너스 불필요
            var primaryAttack = CombatAPI.FindAnyAttackAbility(unit, Settings.RangePreference.PreferRanged);
            // ★ v3.9.92: 일반 공격 없으면 DangerousAoE (화염방사기 Cone/Ray 등) 시도
            // DangerousAoE는 CanTargetEnemies=false라 기본 탐색에서 누락
            // 위치 평가에는 패턴 반경+LOS로 hittable 판정 가능
            if (primaryAttack == null)
            {
                primaryAttack = CombatAPI.FindAnyAttackAbility(unit, Settings.RangePreference.PreferRanged, includeDangerousAoE: true);
                if (primaryAttack != null && Main.IsDebugEnabled)
                    Log.Engine.Debug($"[MovementAPI] {unit.CharacterName}: Using DangerousAoE for position eval: {primaryAttack.Name}");
            }
            bool isScatter = CombatAPI.IsScatterAttack(primaryAttack);
            bool isMelee = primaryAttack?.IsMelee ?? false;

            // ★ v3.116.9 옵션 B 수정: primaryAttack 이 단일 타겟이어도 unit 이 Cone/Burst/Ray 능력을
            //   별도로 보유하면 그 능력으로 위치별 AoE 커버리지 평가.
            //   v3.116.10: PatternInfo 기반 (CanBeDirectional + IsValid Radius>0) — WeaponRangeProfile 와 동일 기준.
            //   진단 로그 강화: 패턴 타입/반경/각도 + RawFacts 비교 (필터링 케이스 가시화).
            List<AbilityData> rangedAoEAbilities = null;
            List<CombatAPI.PatternInfo> rangedAoEPatternInfos = null;
            {
                var availableAbilities = CombatAPI.GetAvailableAbilities(unit);
                if (availableAbilities != null)
                {
                    foreach (var ab in availableAbilities)
                    {
                        if (ab == null || ab.IsMelee) continue;
                        var patternInfo = CombatAPI.GetPatternInfo(ab);
                        if (patternInfo != null && patternInfo.IsValid && patternInfo.CanBeDirectional)
                        {
                            if (rangedAoEAbilities == null)
                            {
                                rangedAoEAbilities = new List<AbilityData>();
                                rangedAoEPatternInfos = new List<CombatAPI.PatternInfo>();
                            }
                            rangedAoEAbilities.Add(ab);
                            rangedAoEPatternInfos.Add(patternInfo);
                        }
                    }
                }
            }
            if (Main.IsDebugEnabled)
            {
                if (rangedAoEAbilities != null)
                {
                    var details = new System.Text.StringBuilder();
                    for (int i = 0; i < rangedAoEAbilities.Count; i++)
                    {
                        var pi = rangedAoEPatternInfos[i];
                        if (i > 0) details.Append(", ");
                        details.Append($"{rangedAoEAbilities[i].Name}(Type={pi.Type},R={pi.Radius:F1},A={pi.Angle:F0})");
                    }
                    Log.Engine.Debug($"[MovementAPI] {unit.CharacterName}: Ranged AoE abilities for coverage scoring ({rangedAoEAbilities.Count}): {details}");
                }
                else
                {
                    // ★ v3.116.10 진단: WeaponRangeProfile 은 directional 인데 GetAvailableAbilities 에선 누락된 케이스 catch
                    var weaponProfile = CombatAPI.GetWeaponRangeProfile(unit);
                    if (weaponProfile.HasDirectionalPattern)
                    {
                        Log.Engine.Debug($"[MovementAPI] {unit.CharacterName}: WeaponRangeProfile.HasDirectional=True (radius={weaponProfile.PatternRadius:F1}) " +
                            "but no AoE in GetAvailableAbilities — likely cooldown/restricted at eval time");
                    }
                }
            }

            // ★ v3.8.70: 안전 체크용 아군 목록
            // ★ v3.9.24: DangerousAoE도 아군 안전 체크 필요 (Cone/Ray가 아군을 타격)
            //   CanTargetFriends=false라도 DangerousAoE는 AoE 범위 내 아군에 피해
            List<BaseUnitEntity> allies = null;
            if (primaryAttack?.Blueprint?.CanTargetFriends == true || AbilityDatabase.IsDangerousAoE(primaryAttack))
                allies = CombatAPI.GetAllies(unit);

            // ★ v3.0.62: 위협 점수 추가 (AoE, AoO, Overwatch)
            // ★ v3.2.00: 영향력 맵 기반 위협/통제 점수 추가
            // ★ v3.5.41: 경로 위험도 점수 추가 (Larian 방법론)
            // ★ v3.6.7: 명중률 기반 위치 보너스 추가
            // ★ v3.6.8: Scatter/근접 예외 처리 추가
            foreach (var score in scores)
            {
                score.ThreatScore += CalculateThreatScore(unit, score.Node);

                // ★ v3.10.0: 아군 밀집 패널티 (이동 위치 분산)
                score.AllyClusterPenalty = CalculateAllyClusterPenalty(score.Position, unit);

                // ★ v3.110.16: ApplyInfluenceScores 제거 (Phase C). Blackboard 기반 점수는 직접 호출.
                ApplyBlackboardScores(score, score.Position, role);

                // ★ v3.8.13: AI 셀에서 직접 경로 위험도 평가 (실제 위협 데이터 활용)
                // AI 패스파인더가 이미 경로 상의 AoO, AoE 진입 횟수를 계산해둠
                var originalTile = tiles.Values.FirstOrDefault(t =>
                    t.Node == score.Node);

                if (originalTile.Node != null)
                {
                    // AI 셀의 경로 위협 데이터 직접 활용
                    score.PathRiskScore = EvaluatePathRiskAi(
                        unit, unit.Position, score.Node, originalTile);
                }
                else
                {
                    // 폴백: 단순 샘플링 방식
                    score.PathRiskScore = EvaluatePathRiskSimple(
                        unit, unit.Position, score.Position);
                }

                // ★ v3.9.26: 게임 실제 명중률 기반 위치 보너스 (Scatter/근접 예외)
                // primaryAttack 있으면 GetHitChanceFromPosition 사용, 없으면 거리 밴드 폴백
                score.HitChanceBonus = CalculateHitChanceBonus(score.Position, enemies, weaponRange, isScatter, isMelee, unit, primaryAttack);

                // ★ v3.28.0: 플랭킹 스코어 (원거리 — 적들의 후방/측면에서 사격 가능한 위치 선호)
                {
                    float flankSum = 0f;
                    foreach (var enemy in enemies)
                    {
                        if (enemy == null || enemy.LifeState.IsDead) continue;
                        flankSum += CombatAPI.GetFlankingBonus(enemy, score.Position);
                    }
                    score.FlankingScore = flankSum * SC.FlankingPositionBonus;
                }

                // ★ v3.8.78: LOS 기반 hittable count가 0이면 정밀 체크 생략
                // EvaluatePosition에서 LOS로 사전 계산한 HittableEnemyCount 활용
                // LOS 0이면 CanTargetFromNode도 실패 → 500+ 불필요 호출 제거
                if (score.HittableEnemyCount > 0)
                {
                    // allies가 있으면 scatter safety 반영한 정밀 카운트로 덮어쓰기
                    int initialLosCount = score.HittableEnemyCount;
                    int realCount = CombatAPI.CountHittableEnemiesFromPosition(
                        unit, score.Node, enemies, primaryAttack, allies);
                    score.HittableEnemyCount = realCount;

                    // ★ v3.110.10: AttackScore도 실제 hittable count 기준으로 재계산.
                    //   이전 (v3.110.9 포함): EvaluatePosition에서 LOS 기반 hittableFromLos로 AttackScore 계산 후
                    //   여기서 HittableEnemyCount만 실제값으로 덮어쓰고 AttackScore는 유지 → 불일치 버그.
                    //   증상: "LOS로 5명 보이지만 실제 공격 불가" 위치가 Attack=50 유지하여 LOS 폴백에서 Best로 선택.
                    //   실증 로그(v3.110.9): Best 34건 중 25건(74%)이 hittable=0이었으나 Attack 보너스 부당 수령.
                    //   수정: realCount 기준으로 AttackScore 재구성. LOS만 있고 실제 공격 불가면 기본 +20만.
                    if (realCount != initialLosCount)
                    {
                        // HasLosToEnemy 여부로 기본 +20 부여 (EvaluatePosition과 동일 기준).
                        // 사거리 제약은 이미 LOS 계산 및 상위 필터에서 반영되므로 중복 체크 생략.
                        float attackBase = score.HasLosToEnemy ? 20f : 0f;
                        if (realCount > 0)
                        {
                            attackBase += Math.Min(realCount, 3) * 10f;
                            if (realCount > 3) attackBase += (float)Math.Sqrt(realCount - 3) * 8f;
                        }
                        score.AttackScore = attackBase;
                    }
                }

                // ★ v3.116.8 옵션 B / v3.116.9 fix: 원거리 AoE Coverage Score (Cone/Ray/Sector).
                //   단발 사격 평가에 묻히던 "Cone 5명 vs 1명" 차이를 위치 점수에 명시 반영.
                //   가장 가까운 살아있는 적을 패턴 조준점으로 잡고, 그 패턴 안에 추가로 잡히는 적 수에 보너스.
                //   MeleeAoESplashBonus (v3.8.50, FindMeleeAttackPositionSync) 의 ranged 대응판 — 동일 가중치 12.
                //   단일 적만 잡히는 위치(splash<2)는 보너스 0 — AttackScore 가 이미 처리.
                //   v3.116.9: rangedAoEAbilities (primary 와 별개 탐색, 무기+스킬 모두 포함) 사용.
                //   다중 AoE 능력 보유 시 각각 평가 후 MAX coverage 사용 — 캐릭터가 그 위치에서 사용 가능한
                //   최적 AoE 능력 기준으로 점수화.
                if (rangedAoEAbilities != null && enemies != null && score.HittableEnemyCount > 0)
                {
                    BaseUnitEntity coneTarget = null;
                    float minDist = float.MaxValue;
                    foreach (var enemy in enemies)
                    {
                        if (enemy == null || enemy.LifeState.IsDead) continue;
                        float d = Vector3.Distance(score.Position, enemy.Position);
                        if (d < minDist) { minDist = d; coneTarget = enemy; }
                    }
                    if (coneTarget != null)
                    {
                        int maxAoeCount = 0;
                        AbilityData bestAoeAbility = null;
                        for (int ai = 0; ai < rangedAoEAbilities.Count; ai++)
                        {
                            int aoeCount = CombatAPI.CountEnemiesInPattern(
                                rangedAoEAbilities[ai], coneTarget.Position, score.Position, enemies);
                            if (aoeCount > maxAoeCount)
                            {
                                maxAoeCount = aoeCount;
                                bestAoeAbility = rangedAoEAbilities[ai];
                            }
                        }
                        if (maxAoeCount >= 2)
                            score.AoeHitCountBonus = (maxAoeCount - 1) * 12f;
                        // ★ v3.116.10 진단: best ability + splash 카운트 score 에 임시 저장 (best 위치 로그용)
                        score.BestAoeAbility = bestAoeAbility;
                        score.BestAoeSplash = maxAoeCount;
                    }
                }

                // ★ v3.74.2: 진동 방지 — 이전 위치 근처로 되돌아가면 패널티
                if (lastMoveOrigin.HasValue)
                {
                    float distToLastOrigin = CombatAPI.MetersToTiles(
                        Vector3.Distance(score.Position, lastMoveOrigin.Value));
                    if (distToLastOrigin < 2f)
                    {
                        score.OscillationPenalty = 15f;
                    }
                }
            }

            // ★ v3.8.48: O(n log n) 정렬 제거 → O(n) MaxByWhere 사용 (100+ 요소 최적화)
            // ★ v3.6.18: 실제 공격 가능한 위치 우선 선택 (HittableEnemyCount > 0)
            //
            // ★ v3.110.12: 폴백 체인 단계 라벨링 — 어느 단계가 최종 선택했는지 추적 가능.
            // 5단계 우선순위: hittable-optimal > hittable-close > hittable-risky > los-optimal > los-risky
            string selectedBy = "none";
            // ★ Tier 1: hittable-optimal (DistanceScore >= 20) OR hittable-safe (HideFullRatio >= 0.8 + DistanceScore > 0).
            // Hide-safe 분기는 Argenta 케이스 같은 "Tier 2 노출 폴백" 차단용 — 안전한 hittable 자리가
            // 있는데 DistanceScore 만 부족해서 Tier 2 로 떨어져 노출 타일이 선택되는 결함 방지.
            const float HIDE_SAFE_RATIO = 0.8f;  // 80% 적이 ≥Full LOS 차단되는 자리 = 안전
            var best = CollectionHelper.MaxByWhere(scores,
                s => s.CanStand && s.HittableEnemyCount > 0
                     && (s.DistanceScore >= 20f
                         || (s.HideFullRatio >= HIDE_SAFE_RATIO && s.DistanceScore > 0f)),
                s => s.TotalScore);
            if (best != null)
                selectedBy = best.DistanceScore >= 20f ? "hittable-optimal" : "hittable-safe";

            if (best == null)
            {
                best = CollectionHelper.MaxByWhere(scores,
                    s => s.CanStand && s.HittableEnemyCount > 0 && s.DistanceScore > 0f,
                    s => s.TotalScore);
                if (best != null) selectedBy = "hittable-close";
            }

            // ★ v3.8.45: 3차 폴백에 MinSafeDistance 체크 추가
            // 기존: HittableEnemyCount > 0만 체크 → 적 1.4타일 위치도 선택됨
            // 수정: DistanceScore >= 0 = MinSafeDistance 이상 위치만 허용
            // (DistanceScore < 0 = nearestEnemyDist < minSafeDistance → 위험 지역)
            if (best == null)
            {
                best = CollectionHelper.MaxByWhere(scores,
                    s => s.CanStand && s.HittableEnemyCount > 0 && s.DistanceScore >= 0f,
                    s => s.TotalScore);
                if (best != null) selectedBy = "hittable-risky";
            }

            // ★ v3.6.18: 공격 가능 위치 없으면 기존 LOS 기반 폴백 (접근 이동용)
            if (best == null)
            {
                if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] {unit.CharacterName}: No hittable position found, fallback to LOS-based");
                best = CollectionHelper.MaxByWhere(scores,
                    s => s.CanStand && s.HasLosToEnemy && s.DistanceScore >= 20f,
                    s => s.TotalScore);
                if (best != null) selectedBy = "los-optimal";
            }

            // ★ v3.8.45: LOS 폴백도 MinSafeDistance 준수
            if (best == null)
            {
                best = CollectionHelper.MaxByWhere(scores,
                    s => s.CanStand && s.HasLosToEnemy && s.DistanceScore >= 0f,
                    s => s.TotalScore);
                if (best != null) selectedBy = "los-risky";
            }

            if (best != null)
            {
                // ★ v3.110.9: NearestEnemy 거리로 수정 (이전: enemies[0] — 단순히 리스트 첫 항목이라 오해 유발)
                BaseUnitEntity nearestEnemy = null;
                float nearestDist = float.MaxValue;
                for (int i = 0; i < enemies.Count; i++)
                {
                    var e = enemies[i];
                    if (e == null || e.LifeState.IsDead) continue;
                    float d = Vector3.Distance(best.Position, e.Position);
                    if (d < nearestDist) { nearestDist = d; nearestEnemy = e; }
                }
                float nearestTiles = nearestEnemy != null ? CombatAPI.MetersToTiles(nearestDist) : 0f;
                Log.Engine.Info($"[MovementAPI] FindRangedAttackPosition: Best=({best.Position.x:F1},{best.Position.z:F1}), score={best.TotalScore:F1}, nearestDist={nearestTiles:F1}t ({nearestEnemy?.CharacterName ?? "?"}), hittable={best.HittableEnemyCount}, cover={best.BestCover}, enemyLoS={(best.HasLosToEnemy ? 1 : 0)}, selectedBy={selectedBy}");

                // ★ v3.110.8: 점수 컴포넌트 브레이크다운 — 어느 축이 최종 선택을 좌우했는지 진단용
                // 목적: DistanceScore 수정(0.6→0.5)의 효과가 다른 축에 상쇄되는지 확인
                if (Main.IsDebugEnabled)
                {
                    Log.Engine.Debug($"[MovementAPI] Best breakdown: Cover={best.CoverScore:F1}, " +
                        $"Hide={best.HideScore:F1}(F{best.HideFullRatio:F2}/A{best.HideAnyRatio:F2}), " +
                        $"Distance={best.DistanceScore:F1}, " +
                        $"Threat=-{best.ThreatScore:F1}, TurnThreat=-{best.EnemyTurnThreatSum:F1}, " +
                        $"StayAway={best.StayingAwayScore:F2}({best.StayingAwayBonus:F1}), " +
                        $"Attack={best.AttackScore:F1}, AoeCov={best.AoeHitCountBonus:F1}, " +
                        $"Hit={best.HitChanceBonus:F1}, Path=-{best.PathRiskScore:F1}, " +
                        $"AllyC=-{best.AllyClusterPenalty:F1}, Flank={best.FlankingScore:F1}, " +
                        $"Osc=-{best.OscillationPenalty:F1}, " +
                        $"Exposure=-{best.ExposureScore:F1}");

                    // ★ v3.116.10 진단: AoE 측정 결과 — splash<2 인 케이스 가시화
                    if (best.BestAoeAbility != null)
                    {
                        Log.Engine.Debug($"[MovementAPI] Best AoE diagnostic: ability={best.BestAoeAbility.Name}, " +
                            $"splash={best.BestAoeSplash}, bonus={best.AoeHitCountBonus:F1} " +
                            $"(splash<2 = no bonus by design — narrow pattern or single target alone in cone)");
                    }

                    // ★ v3.110.16: InfluenceMap@Best 진단 로그 제거 — InfT/InfC 축 자체가 사라짐.
                }
            }
            else
            {
                if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] {unit.CharacterName}: No better position found for ranged character with MP={predictedMP:F1}");
            }

            return best;
        }

        /// <summary>
        /// ★ v3.0.74: 근접 공격 위치 찾기 (실제 도달 가능한 타일 기반)
        /// ★ v3.1.01: predictedMP 파라미터 추가
        /// ★ v3.2.00: influenceMap 파라미터 추가
        /// ★ v3.2.25: role 파라미터 추가 - Role별 전선 위치 점수
        /// ★ v3.4.00: predictiveMap 파라미터 추가 - 적 이동 예측 기반 위협 점수
        /// 적의 타일이 아닌, 적에게 인접한 공격 가능 위치 반환
        /// </summary>
        public static PositionScore FindMeleeAttackPositionSync(
            BaseUnitEntity unit,
            BaseUnitEntity target,
            float meleeRange = 2f,
            float predictedMP = 0f,
            AIRole role = AIRole.Auto,
            AbilityData meleeAoEAbility = null,
            List<BaseUnitEntity> enemies = null,
            Vector3? lastMoveOrigin = null)
        {
            if (unit == null || target == null) return null;

            // ★ v3.8.13: AI용 패스파인딩 사용 - 경로 위협 데이터 포함
            var tiles = predictedMP > 0
                ? FindAllReachableTilesWithThreatsSync(unit, predictedMP)
                : FindAllReachableTilesWithThreatsSync(unit);
            if (tiles == null || tiles.Count == 0)
            {
                if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] {unit.CharacterName}: No reachable tiles for melee approach (predictedMP={predictedMP:F1})");
                return null;
            }

            var targetPos = target.Position;
            var unitPos = unit.Position;

            // ★ v3.18.18: DamagingAoE 회피 — 안전한 유닛이 AoE 안으로 이동하지 않도록
            bool avoidHazardZones = !CombatAPI.IsUnitInHazardZone(unit);

            var candidates = new List<PositionScore>();

            foreach (var kvp in tiles)
            {
                var aiCell = kvp.Value;
                var node = aiCell.Node as CustomGridNodeBase;
                if (node == null || !aiCell.IsCanStand) continue;

                // ★ v3.7.62: BattlefieldGrid 검증 - Walkable/점유 체크
                if (!BattlefieldGrid.Instance.ValidateNode(unit, node))
                    continue;

                var pos = node.Vector3Position;

                // ★ v3.18.18: DamagingAoE 위치 필터링
                if (avoidHazardZones && CombatAPI.IsPositionInHazardZone(pos, unit))
                    continue;

                // ★ v3.9.24: 게임과 동일한 거리 계산 (Chebyshev 기반 edge-to-edge + SizeRect)
                // 이전: Vector3.Distance (중심-중심 유클리드) + SizeRect.Width*0.5 보정
                //   → 대형 유닛에서 실제 게임 CanUseAbilityOn과 불일치 (2.0 tiles vs range=1)
                // 수정: WarhammerGeometryUtils.DistanceToInCells로 대형 유닛 크기 정확 반영
                float distToTargetTiles = CombatAPI.GetDistanceInTiles(pos, target);

                // 근접 공격 사거리 내 타일만 선택 (SizeRect 이미 반영됨)
                if (distToTargetTiles > meleeRange) continue;

                // 적 위치와 거의 동일하면 스킵 (적 유닛이 점유하는 타일)
                if (distToTargetTiles < 0.5f) continue;

                // 점수 계산
                float score = 100f;  // 기본 점수

                // 1. 이동 거리 점수 (가까울수록 좋음 - MP 절약)
                float distFromUnit = Vector3.Distance(pos, unitPos);
                score -= distFromUnit * 2f;

                // 2. ★ v3.28.0: 플랭킹 보너스 (게임 네이티브 공격 방향 판정)
                // 이전: dot product 근사 → GetWarhammerAttackSide() 기반 정밀 판정
                float flankBonus = CombatAPI.GetFlankingBonus(target, pos);
                score += flankBonus * SC.FlankingMeleeBonus;

                // 3. ★ v3.8.13: AI 셀의 경로 위협 데이터 활용 (목적지 위협 + 경로 위협)
                // CalculateThreatScore(목적지)에 추가로 경로 위협도 반영
                float destThreatScore = CalculateThreatScore(unit, node);
                float pathThreatScore = aiCell.ProvokedAttacks * WEIGHT_AOO + aiCell.EnteredAoE * WEIGHT_AOE_ENTRY + aiCell.StepsInsideDamagingAoE * WEIGHT_DAMAGING_AOE_STEP;
                float threatScore = destThreatScore + pathThreatScore;
                score -= threatScore;

                var posScore = new PositionScore
                {
                    Node = node,
                    CanStand = true,
                    APCost = aiCell.Length,
                    DistanceScore = score,
                    ThreatScore = threatScore
                };

                // ★ v3.111.16 Phase C.2: Phase 1-6 방어 축 통합 — HideScore + EnemyTurnThreatSum.
                //   근접 유닛도 "엄폐된 위치", "다음 턴 위협 회피" 혜택.
                //   StayingAwayBonus는 근접 approach와 반의어이므로 0 유지.
                //   CoverScore(공격자 관점)는 근접 영향 낮아 제외.
                if (enemies != null && enemies.Count > 0)
                {
                    var pm = _currentPredictedMoves;
                    var hideComponents = pm != null
                        ? TileScorerPort.GetEnsuredCoverComponents(node, unit.SizeRect, enemies, pm)
                        : TileScorerPort.GetHideScoreComponents(node, unit.SizeRect, enemies);
                    posScore.ApplyHideComponents(hideComponents);

                    float turnThreatSum = 0f;
                    foreach (var e in enemies)
                    {
                        if (e == null || e.LifeState.IsDead) continue;
                        turnThreatSum += CombatAPI.GetEnemyTurnThreatScore(e, node.Vector3Position);
                    }
                    posScore.EnemyTurnThreatSum = turnThreatSum;
                }

                // ★ v3.110.16: ApplyInfluenceScores 제거. Blackboard + PathRisk 직접 호출.
                ApplyBlackboardScores(posScore, pos, role);

                if (aiCell.Node != null)
                {
                    posScore.PathRiskScore = EvaluatePathRiskAi(
                        unit, unitPos, node, aiCell);
                }
                else
                {
                    posScore.PathRiskScore = EvaluatePathRiskSimple(
                        unit, unitPos, pos);
                }

                // ★ v3.8.50: 근접 AOE 스플래시 보너스
                // 이 위치에서 근접 AOE를 사용할 때 패턴 내 추가 적 수 계산
                // 원형: 위치 무관, 원뿔/부채꼴: 방향에 따라 다른 적 포함
                if (meleeAoEAbility != null && enemies != null)
                {
                    int splashCount = CombatAPI.CountEnemiesInPattern(
                        meleeAoEAbility, targetPos, pos, enemies);
                    if (splashCount >= 2)
                    {
                        posScore.MeleeAoESplashBonus = (splashCount - 1) * 12f;
                    }
                }

                // ★ v3.74.2: 진동 방지 — 이전 위치 근처로 되돌아가면 패널티
                if (lastMoveOrigin.HasValue)
                {
                    float distToLastOrigin = CombatAPI.MetersToTiles(
                        Vector3.Distance(pos, lastMoveOrigin.Value));
                    if (distToLastOrigin < 2f)
                    {
                        posScore.OscillationPenalty = 15f;
                    }
                }

                candidates.Add(posScore);
            }

            if (candidates.Count == 0)
            {
                if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] {unit.CharacterName}: No melee attack positions within range");
                return null;
            }

            // ★ v3.8.48: LINQ → CollectionHelper (0 할당, O(n))
            var best = CollectionHelper.MaxBy(candidates, c => c.TotalScore);
            float finalDistTiles = CombatAPI.GetDistanceInTiles(best.Position, target);
            Log.Engine.Info($"[MovementAPI] {unit.CharacterName}: Melee position at ({best.Position.x:F1},{best.Position.z:F1}) " +
                $"dist={finalDistTiles:F1} tiles (range={meleeRange:F0}), score={best.TotalScore:F1}");

            return best;
        }

        /// <summary>
        /// ★ v3.0.60: 후퇴 위치 찾기 (실제 도달 가능한 타일 기반)
        /// ★ v3.0.62: AoE/위협 점수 통합
        /// ★ v3.1.01: predictedMP 파라미터 추가
        /// ★ v3.2.00: influenceMap 파라미터 추가
        /// ★ v3.2.25: role 파라미터 추가 - Role별 전선 위치 점수
        /// ★ v3.4.00: predictiveMap 파라미터 추가 - 적 이동 예측 기반 위협 점수
        ///
        /// PathfindingService 기반 (위협/경로 품질 반영)
        /// </summary>
        public static PositionScore FindRetreatPositionSync(
            BaseUnitEntity unit,
            List<BaseUnitEntity> enemies,
            float minSafeDistance = 8f,
            float predictedMP = 0f,
            AIRole role = AIRole.Auto)
        {
            // 기본 호출 - maxSafeDistance는 무제한 (0)
            return FindRetreatPositionSync(unit, enemies, minSafeDistance, 0f, predictedMP,
                role, null, 0f);
        }

        /// <summary>
        /// ★ v3.7.04: 사역마 거리 제약을 고려한 후퇴 위치 찾기
        /// ★ v3.7.11: maxSafeDistance 파라미터 추가 - 무기 사거리 기반 최대 후퇴 거리
        /// familiarPosition이 지정되면 해당 위치에서 maxFamiliarDistance 이내로 제한
        /// maxSafeDistance > 0이면 해당 거리 초과 시 큰 패널티 적용
        /// </summary>
        public static PositionScore FindRetreatPositionSync(
            BaseUnitEntity unit,
            List<BaseUnitEntity> enemies,
            float minSafeDistance,
            float maxSafeDistance,
            float predictedMP,
            AIRole role,
            Vector3? familiarPosition,
            float maxFamiliarDistanceMeters)
        {
            if (unit == null || enemies == null || enemies.Count == 0)
                return null;

            // ★ v3.8.13: AI용 패스파인딩 사용 - 경로 위협 데이터 포함
            var tiles = predictedMP > 0
                ? FindAllReachableTilesWithThreatsSync(unit, predictedMP)
                : FindAllReachableTilesWithThreatsSync(unit);
            if (tiles == null || tiles.Count == 0)
            {
                if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] {unit.CharacterName}: No reachable tiles for retreat (predictedMP={predictedMP:F1})");
                return null;
            }

            // 적들의 중심점 계산
            Vector3 enemyCenter = Vector3.zero;
            int count = 0;
            foreach (var enemy in enemies)
            {
                if (enemy == null || enemy.LifeState.IsDead) continue;
                enemyCenter += enemy.Position;
                count++;
            }
            if (count == 0) return null;
            enemyCenter /= count;

            // 후퇴 방향 (적 반대)
            var retreatDir = (unit.Position - enemyCenter).normalized;

            // ★ v3.8.70: 현재 위치 제외 — "Already at destination" 루프 방지
            var currentNode = unit.Position.GetNearestNodeXZ();

            // ★ v3.18.18: DamagingAoE 회피 — 안전한 유닛이 AoE 안으로 후퇴하지 않도록
            bool avoidHazardZones = !CombatAPI.IsUnitInHazardZone(unit);

            var candidates = new List<PositionScore>();

            foreach (var kvp in tiles)
            {
                var aiCell = kvp.Value;
                var node = aiCell.Node as CustomGridNodeBase;
                if (node == null || !aiCell.IsCanStand) continue;
                if (node == currentNode) continue;  // ★ v3.8.70

                // ★ v3.7.62: BattlefieldGrid 검증 - Walkable/점유 체크
                if (!BattlefieldGrid.Instance.ValidateNode(unit, node))
                    continue;

                var pos = node.Vector3Position;

                // ★ v3.18.18: DamagingAoE 위치 필터링
                if (avoidHazardZones && CombatAPI.IsPositionInHazardZone(pos, unit))
                    continue;

                // ★ v3.6.1: 모든 적과의 최소 거리 계산 (타일 단위)
                // ★ v3.8.78: LOS 기반 hittable count 동시 계산 (CountHittableEnemiesFromPosition 제거)
                float nearestEnemyDist = float.MaxValue;
                int hittableFromLos = 0;
                foreach (var enemy in enemies)
                {
                    if (enemy == null || enemy.LifeState.IsDead) continue;
                    float d = CombatAPI.MetersToTiles(Vector3.Distance(pos, enemy.Position));
                    if (d < nearestEnemyDist) nearestEnemyDist = d;

                    // LOS 체크: 적이 이 위치를 볼 수 있으면 공격도 가능
                    try
                    {
                        var enemyNode = enemy.Position.GetNearestNodeXZ() as CustomGridNodeBase;
                        if (enemyNode != null)
                        {
                            var los = LosCalculations.GetWarhammerLos(enemyNode, enemy.SizeRect, node, unit.SizeRect);
                            if (los.CoverType != LosCalculations.CoverType.Invisible)
                                hittableFromLos++;
                        }
                    }
                    catch { }
                }

                // 안전 거리 미달이면 스킵 (minSafeDistance는 타일 단위)
                if (nearestEnemyDist < minSafeDistance) continue;

                // ★ v3.7.04: 사역마 거리 제약 체크
                float familiarDistPenalty = 0f;
                if (familiarPosition.HasValue && maxFamiliarDistanceMeters > 0)
                {
                    float distToFamiliar = Vector3.Distance(pos, familiarPosition.Value);
                    if (distToFamiliar > maxFamiliarDistanceMeters)
                    {
                        // 사역마와 너무 멀면 큰 패널티 (하지만 완전히 제외하진 않음)
                        familiarDistPenalty = (distToFamiliar - maxFamiliarDistanceMeters) * 5f;
                        if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] Retreat pos ({pos.x:F1},{pos.z:F1}) too far from familiar: {distToFamiliar:F1}m > {maxFamiliarDistanceMeters:F1}m, penalty={familiarDistPenalty:F1}");
                    }
                }

                // ★ v3.7.11: 무기 사거리 초과 패널티 (너무 멀리 후퇴하면 공격 불가)
                float weaponRangePenalty = 0f;
                if (maxSafeDistance > 0 && nearestEnemyDist > maxSafeDistance)
                {
                    // 무기 사거리를 초과하면 큰 패널티 적용
                    // 초과한 거리의 제곱에 비례하여 패널티 (급격히 증가)
                    float excess = nearestEnemyDist - maxSafeDistance;
                    weaponRangePenalty = excess * excess * 10f;
                    if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] Retreat pos ({pos.x:F1},{pos.z:F1}) exceeds weapon range: {nearestEnemyDist:F1} > {maxSafeDistance:F1}, penalty={weaponRangePenalty:F1}");
                }

                // 점수 계산: 적에게서 멀수록 + 후퇴 방향 보너스
                // ★ v3.7.11: 하지만 무기 사거리를 넘으면 더 이상 보너스 없음
                float effectiveDistForScore = maxSafeDistance > 0
                    ? Math.Min(nearestEnemyDist, maxSafeDistance)
                    : nearestEnemyDist;
                float distScore = effectiveDistForScore * 2f;

                // 후퇴 방향과의 일치도
                var moveDir = (pos - unit.Position).normalized;
                float directionBonus = Vector3.Dot(moveDir, retreatDir) * 10f;

                // 이동 거리 패널티 (너무 멀면 MP 낭비)
                float moveDist = Vector3.Distance(unit.Position, pos);
                float moveDistPenalty = moveDist * 0.5f;

                // ★ v3.8.13: AI 셀의 경로 위협 데이터 활용 (목적지 위협 + 경로 위협)
                float destThreatScore = CalculateThreatScore(unit, node);
                float pathThreatScore = aiCell.ProvokedAttacks * WEIGHT_AOO + aiCell.EnteredAoE * WEIGHT_AOE_ENTRY + aiCell.StepsInsideDamagingAoE * WEIGHT_DAMAGING_AOE_STEP;

                // ★ v3.9.02: 아군 밀집 패널티 — 적 AoE 분산 + 아군 AoE 공간 확보
                float allyClusterPenalty = CalculateAllyClusterPenalty(pos, unit);

                var score = new PositionScore
                {
                    Node = node,
                    CanStand = true,
                    APCost = aiCell.Length,
                    // ★ v3.7.04: 사역마 거리 패널티 적용
                    // ★ v3.7.11: 무기 사거리 초과 패널티 적용
                    DistanceScore = distScore + directionBonus - moveDistPenalty - familiarDistPenalty - weaponRangePenalty,
                    // ★ v3.8.13: 경로 위협도 반영
                    ThreatScore = destThreatScore + pathThreatScore,
                    // ★ v3.9.02: 아군 분산 유도
                    AllyClusterPenalty = allyClusterPenalty
                };

                // ★ v3.110.16: ApplyInfluenceScores 제거. Blackboard + PathRisk 직접 호출.
                ApplyBlackboardScores(score, pos, role);

                if (aiCell.Node != null)
                {
                    score.PathRiskScore = EvaluatePathRiskAi(
                        unit, unit.Position, node, aiCell);
                }
                else
                {
                    score.PathRiskScore = EvaluatePathRiskSimple(
                        unit, unit.Position, pos);
                }

                // ★ v3.111.2 Phase 6 follow-up: 레거시 CoverScore [15/30/40] 방어 semantics → HideScore로 이관.
                // Phase 6에서 CoverScore가 공격자 관점 [0~30]로 재정의되어 legacy 대입 불가.
                // 후퇴 경로의 엄폐도 평가는 HideScore 사용 (EvaluatePosition과 semantics 일관).
                try
                {
                    var hideComp = TileScorerPort.GetHideScoreComponents(node, unit.SizeRect, enemies);
                    score.ApplyHideComponents(hideComp);
                }
                catch (System.Exception ex)
                {
                    if (Main.IsDebugEnabled) Log.Engine.Error(ex, $"[MovementAPI] hide score silent");
                }

                // ★ v3.111.17 Phase C.3: StayingAwayBonus — 적 이동능력 반영 안전거리 점수.
                //   Phase 4 가중치: Retreat goal → 40f (EvaluatePosition MovementGoal.Retreat 일관).
                //   기존 설정 누락으로 Phase 4가 실제 후퇴에서 dead weight였음.
                try
                {
                    float stayingAway = TileScorerPort.GetStayingAwayScore(node, unit, enemies);
                    score.StayingAwayScore = stayingAway;
                    score.StayingAwayBonus = stayingAway * 40f;
                }
                catch (System.Exception ex)
                {
                    if (Main.IsDebugEnabled) Log.Engine.Error(ex, $"[MovementAPI] retreat staying-away silent");
                }

                // ★ v3.8.78: LOS 기반 hittable count (기존 CountHittableEnemiesFromPosition 호출 제거)
                // 위 enemy 루프에서 GetWarhammerLos로 동시 계산 → CanTargetFromNode 호출 제거
                score.HittableEnemyCount = hittableFromLos;
                if (score.HittableEnemyCount > 0)
                {
                    // 공격 가능한 위치에 큰 보너스 (적 수 비례)
                    score.AttackScore = score.HittableEnemyCount * 15f;
                }
                else if (nearestEnemyDist <= (maxSafeDistance > 0 ? maxSafeDistance : float.MaxValue))
                {
                    // 무기 사거리 내인데 LOS 없음 → 실질적으로 무가치한 위치
                    score.AttackScore = -25f;
                }

                candidates.Add(score);
            }

            if (candidates.Count == 0)
            {
                // ★ v3.7.23: 안전 거리 달성 불가 시 폴백 - 도달 가능한 최대 거리 위치
                // 사역마 Relocate와 같은 로직 - 최대한 멀리 갈 수 있는 위치로 이동
                Log.Engine.Info($"[MovementAPI] {unit.CharacterName}: No safe retreat positions at {minSafeDistance:F1} tiles, fallback to farthest reachable");

                PositionScore farthestCandidate = null;
                float farthestDist = 0f;

                foreach (var kvp in tiles)
                {
                    var aiCell = kvp.Value;
                    var node = aiCell.Node as CustomGridNodeBase;
                    if (node == null || !aiCell.IsCanStand) continue;
                    if (node == currentNode) continue;  // ★ v3.8.70

                    var pos = node.Vector3Position;

                    // ★ v3.18.18: 폴백에서도 DamagingAoE 회피
                    if (avoidHazardZones && CombatAPI.IsPositionInHazardZone(pos, unit))
                        continue;

                    // 적들로부터의 최소 거리
                    float nearestEnemyDist = float.MaxValue;
                    foreach (var enemy in enemies)
                    {
                        if (enemy == null || enemy.LifeState.IsDead) continue;
                        float d = CombatAPI.MetersToTiles(Vector3.Distance(pos, enemy.Position));
                        if (d < nearestEnemyDist) nearestEnemyDist = d;
                    }

                    // 현재 위치보다 멀고, 지금까지 찾은 것보다 멀면 갱신
                    float currentDist = CombatAPI.MetersToTiles(Vector3.Distance(unit.Position, enemies[0].Position));
                    if (nearestEnemyDist > currentDist && nearestEnemyDist > farthestDist)
                    {
                        farthestDist = nearestEnemyDist;
                        farthestCandidate = new PositionScore
                        {
                            Node = node,
                            CanStand = true,
                            DistanceScore = nearestEnemyDist
                        };
                    }
                }

                if (farthestCandidate != null)
                {
                    Log.Engine.Info($"[MovementAPI] {unit.CharacterName}: Fallback retreat to ({farthestCandidate.Position.x:F1},{farthestCandidate.Position.z:F1}) dist={farthestDist:F1} tiles");
                    return farthestCandidate;
                }

                if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] {unit.CharacterName}: No retreat positions at all");
                return null;
            }

            // ★ v3.8.76: 공격 가능 위치 우선 선택 (hittable > 0)
            // 1차: 공격 가능한 위치 중 최고 점수
            var best = CollectionHelper.MaxByWhere(candidates,
                c => c.HittableEnemyCount > 0,
                c => c.TotalScore);

            // 2차: 공격 가능 위치 없으면 전체 중 최고 점수 (안전 최우선)
            if (best == null)
            {
                best = CollectionHelper.MaxBy(candidates, c => c.TotalScore);
                if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] {unit.CharacterName}: No hittable retreat positions, using best overall");
            }

            if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] {unit.CharacterName}: Retreat to ({best.Position.x:F1},{best.Position.z:F1}) score={best.TotalScore:F1}, hittable={best.HittableEnemyCount}");

            return best;
        }

        /// <summary>
        /// ★ v3.7.23: 적 방향으로 도달 가능한 최대 위치 찾기 (접근 폴백용)
        /// 공격 가능한 위치가 없을 때 최대한 적에게 가까이 이동
        /// </summary>
        public static PositionScore FindBestApproachPosition(
            BaseUnitEntity unit,
            BaseUnitEntity target,
            float predictedMP = 0f)
        {
            if (unit == null || target == null) return null;

            // ★ v3.8.13: AI용 패스파인딩 사용 - 경로 위협 데이터 포함
            var tiles = predictedMP > 0
                ? FindAllReachableTilesWithThreatsSync(unit, predictedMP)
                : FindAllReachableTilesWithThreatsSync(unit);
            if (tiles == null || tiles.Count == 0)
            {
                if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] {unit.CharacterName}: No reachable tiles for approach");
                return null;
            }

            var targetPos = target.Position;

            // ★ v3.18.16: 현재 안전한데 DamagingAoE 안으로 접근하는 것 방지
            // 게임 PathAiCell.StepsInsideDamagingAoE가 감지 못하는 AoE도 CombatAPI로 체크
            bool avoidHazardZones = !CombatAPI.IsUnitInHazardZone(unit);

            // ★ v3.110.13: 타겟 공격 가능성 체크용 — 단일 타겟 리스트 재사용 (GC 절감)
            var singleTargetList = new List<BaseUnitEntity>(1) { target };

            // ★ v3.9.38: A* 경로 기반 접근 위치 선택
            // 유클리드 거리가 아닌 실제 A* 경로를 따라 가장 먼 도달 가능 지점 선택
            // 벽/장애물을 올바르게 돌아가는 다중 턴 이동이 가능
            var pathResult = FindApproachAlongPath(unit, targetPos, tiles);
            if (pathResult != null)
            {
                // ★ v3.18.16: A* 접근 목적지가 DamagingAoE 안이면 거부
                if (avoidHazardZones && CombatAPI.IsPositionInHazardZone(pathResult.Position, unit))
                {
                    Log.Engine.Info($"[MovementAPI] {unit.CharacterName}: A* approach REJECTED — destination in damaging AoE ({pathResult.Position.x:F1},{pathResult.Position.z:F1})");
                }
                else
                {
                    // ★ v3.110.13: A* 결과 hittable 검증 — 공격 가능하면 즉시 채택.
                    // hittable=0이면 Euclidean 폴백의 hittable-first 탐색에 기회 부여.
                    int pathHittable = pathResult.Node != null
                        ? CombatAPI.CountHittableEnemiesFromPosition(unit, pathResult.Node, singleTargetList, null, null)
                        : 0;
                    if (pathHittable > 0)
                    {
                        pathResult.HittableEnemyCount = pathHittable;
                        pathResult.HasLosToEnemy = true;
                        Log.Engine.Info($"[MovementAPI] {unit.CharacterName}: A* approach to ({pathResult.Position.x:F1},{pathResult.Position.z:F1}) dist={Vector3.Distance(pathResult.Position, targetPos):F1}m, pathRisk={pathResult.PathRiskScore:F1}, hittable={pathHittable} to {target.CharacterName}");
                        return pathResult;
                    }
                    if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] {unit.CharacterName}: A* approach hittable=0 ({pathResult.Position.x:F1},{pathResult.Position.z:F1}), trying Euclidean hittable-first");
                }
            }
            else
            {
                if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] {unit.CharacterName}: A* path failed, falling back to Euclidean approach");
            }

            // ★ v3.110.13: 2-tier 선택 — hittable > 0 우선, 없으면 기존 거리 최소 폴백.
            // 이전(~v3.110.12): 거리+경로위험만 평가. 반환 위치가 hittable=0이어도 승인 →
            // v3.110.12의 "staying put → approach 우회" 수정이 또 다른 hittable=0 위치로 수렴하는
            // 증상의 근본 원인. 접근의 목표는 "공격 가능해지는 위치"여야 함.
            PositionScore hittableCandidate = null;
            float hittableClosestDist = float.MaxValue;
            float hittableLowestRisk = float.MaxValue;
            int hittableBestCount = 0;

            PositionScore closestCandidate = null;
            float closestDist = float.MaxValue;
            float lowestPathRisk = float.MaxValue;

            foreach (var kvp in tiles)
            {
                var aiCell = kvp.Value;
                var node = aiCell.Node as CustomGridNodeBase;
                if (node == null || !aiCell.IsCanStand) continue;

                var pos = node.Vector3Position;

                // ★ v3.18.16: DamagingAoE 타일 필터링 (현재 안전한 경우만)
                if (avoidHazardZones && CombatAPI.IsPositionInHazardZone(pos, unit)) continue;

                float distToTarget = Vector3.Distance(pos, targetPos);

                float pathRisk = aiCell.ProvokedAttacks * WEIGHT_AOO + aiCell.EnteredAoE * WEIGHT_AOE_ENTRY + aiCell.StepsInsideDamagingAoE * WEIGHT_DAMAGING_AOE_STEP;

                // ★ v3.110.13: hittable 분류 — 이 위치에서 target 공격 가능 여부
                int hittable = CombatAPI.CountHittableEnemiesFromPosition(unit, node, singleTargetList, null, null);

                if (hittable > 0)
                {
                    // hittable 후보: 최단 거리 + 최저 위험 우선
                    if (distToTarget < hittableClosestDist || (distToTarget == hittableClosestDist && pathRisk < hittableLowestRisk))
                    {
                        hittableClosestDist = distToTarget;
                        hittableLowestRisk = pathRisk;
                        hittableBestCount = hittable;
                        hittableCandidate = new PositionScore
                        {
                            Node = node,
                            CanStand = true,
                            DistanceScore = 100f - distToTarget,
                            PathRiskScore = pathRisk,
                            HittableEnemyCount = hittable,
                            HasLosToEnemy = true
                        };
                    }
                }
                else
                {
                    // 일반 후보: 기존 로직 (hittable 없을 때 폴백)
                    if (distToTarget < closestDist || (distToTarget == closestDist && pathRisk < lowestPathRisk))
                    {
                        closestDist = distToTarget;
                        lowestPathRisk = pathRisk;
                        closestCandidate = new PositionScore
                        {
                            Node = node,
                            CanStand = true,
                            DistanceScore = 100f - distToTarget,
                            PathRiskScore = pathRisk,
                        };
                    }
                }
            }

            // hittable 후보 우선 선택
            if (hittableCandidate != null)
            {
                Log.Engine.Info($"[MovementAPI] {unit.CharacterName}: Euclidean approach (hittable) to ({hittableCandidate.Position.x:F1},{hittableCandidate.Position.z:F1}) dist={hittableClosestDist:F1}m, pathRisk={hittableLowestRisk:F1}, hittable={hittableBestCount} to {target.CharacterName}");
                return hittableCandidate;
            }

            if (closestCandidate != null)
            {
                Log.Engine.Info($"[MovementAPI] {unit.CharacterName}: Euclidean approach (closest, hittable=0) to ({closestCandidate.Position.x:F1},{closestCandidate.Position.z:F1}) dist={closestDist:F1}m, pathRisk={lowestPathRisk:F1} to {target.CharacterName}");
            }

            return closestCandidate;
        }

        /// <summary>
        /// ★ v3.9.42: A* 경로를 따라 접근 가능한 최적 위치 찾기
        /// PathfindingService의 A* 패스파인딩으로 타겟까지의 전체 경로를 계산하고,
        /// 그 경로 위에서 현재 MP로 도달 가능한 가장 먼 지점을 선택.
        /// ★ 접근 경로 캐시: 같은 적에게 다중 턴 접근 시 이전 경로를 유지하여 진동 방지
        /// </summary>
        // ★ v3.9.64: A* 경로 접근 — Phase 1 (거리 가드) + 조건부 A* 우회
        // Phase 1: A* 경로 중 적에게 더 가까운 노드 선택 (거리 가드)
        // Phase 1 실패 시: Euclidean 진행도 측정 (최선 도달 타일이 현재보다 ≥1m 가까운지)
        //   - 진행 ≥ 1m: 개활지 → null 반환 → Euclidean 폴백 사용
        //   - 진행 < 1m: 벽 막힘 → A* 경로 따라 우회 (거리 가드 없이)
        // 흐름: 캐시 Phase 1 → 신선한 Phase 1 → Euclidean 진행도 체크 → 분기.
        private static PositionScore FindApproachAlongPath(
            BaseUnitEntity unit,
            Vector3 targetPos,
            Dictionary<GraphNode, WarhammerPathAiCell> reachableTiles)
        {
            try
            {
                var agent = unit.View?.MovementAgent;
                if (agent == null) return null;

                string unitId = unit.UniqueId;
                float currentDistToTarget = Vector3.Distance(unit.Position, targetPos);

                // ── Step 1: 캐시된 경로로 Phase 1 시도 ──
                if (_approachPathCache.TryGetValue(unitId, out var cached))
                {
                    float targetMoved = Vector3.Distance(cached.TargetPosition, targetPos);
                    if (targetMoved < APPROACH_CACHE_INVALIDATION_DIST && cached.Path != null && cached.Path.Count >= 2)
                    {
                        int foundIdx = FindNearestNodeOnPath(unit.Position, cached.Path);
                        if (foundIdx >= 0 && foundIdx < cached.Path.Count - 1)
                        {
                            if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] {unit.CharacterName}: Using cached approach path ({cached.Path.Count} nodes, current at step {foundIdx})");

                            // ★ v3.9.60: currentIdx 이후만 탐색 (현재 위치 선택 방지)
                            int cachedMinIdx = Math.Max(foundIdx + 1, 1);
                            var cachedResult = SearchApproachPhase1(unit, cached.Path, cachedMinIdx,
                                reachableTiles, targetPos, currentDistToTarget);
                            if (cachedResult != null) return cachedResult;
                        }
                    }

                    // 캐시 경로 Phase 1 실패 → 캐시 무효화 (오래되었거나 방향 불일치)
                    _approachPathCache.Remove(unitId);
                    if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] {unit.CharacterName}: Cached path stale — invalidating, computing fresh path");
                }

                // ── Step 2: 신선한 A* 경로 계산 ──
                // ignoreThreateningAreaCost=true → 근접 위협/AoE 비용 무시 (순수 최단 경로)
                var fullPath = PathfindingService.Instance.FindPathTB_Blocking(
                    agent, targetPos, limitRangeByActionPoints: false,
                    ignoreThreateningAreaCost: true);

                if (fullPath == null || fullPath.error || fullPath.path == null || fullPath.path.Count < 2)
                {
                    if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] {unit.CharacterName}: A* path to target failed or too short");
                    return null;
                }

                var pathNodes = fullPath.path;

                // 신선한 경로 캐시 저장 (5노드 이상의 장거리 경로만)
                if (pathNodes.Count >= 5)
                {
                    _approachPathCache[unitId] = new CachedApproachPath
                    {
                        TargetId = null,
                        TargetPosition = targetPos,
                        Path = new List<GraphNode>(pathNodes)
                    };
                }

                if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] {unit.CharacterName}: Fresh A* path has {pathNodes.Count} nodes, currentDist={currentDistToTarget:F1}");

                // ── Step 3: Phase 1 — 거리 가드 (적에게 더 가까운 노드 선택) ──
                var freshResult = SearchApproachPhase1(unit, pathNodes, 1, reachableTiles, targetPos, currentDistToTarget);
                if (freshResult != null) return freshResult;

                // ★ v3.9.64: Euclidean 진행도 기반 분기
                // Euclidean 최선 타일이 현재보다 충분히 가까우면(≥1m) → null → Euclidean 사용.
                // Euclidean이 막힘(벽 인접, 진행<1m)일 때만 → A* 경로 따라 벽 우회.
                float bestEucDist = float.MaxValue;
                foreach (var kvp in reachableTiles)
                {
                    var tileNode = kvp.Value.Node as CustomGridNodeBase;
                    if (tileNode == null || !kvp.Value.IsCanStand) continue;
                    float d = Vector3.Distance(tileNode.Vector3Position, targetPos);
                    if (d < bestEucDist) bestEucDist = d;
                }

                float eucProgress = currentDistToTarget - bestEucDist;
                if (eucProgress >= 1.0f)
                {
                    if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] {unit.CharacterName}: Euclidean progress={eucProgress:F1}m sufficient — deferring to Euclidean");
                    return null;
                }

                // Euclidean 막힘 → A* 경로를 따라 벽 우회 (proximity matching)
                if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] {unit.CharacterName}: Euclidean stuck (progress={eucProgress:F1}m) — following A* path detour");
                for (int i = pathNodes.Count - 1; i >= 1; i--)
                {
                    var pathNode = pathNodes[i];
                    if (!reachableTiles.TryGetValue(pathNode, out var aiCell)) continue;

                    var node = aiCell.Node as CustomGridNodeBase;
                    if (node == null || !aiCell.IsCanStand) continue;

                    if (Vector3.Distance(node.Vector3Position, unit.Position) < 1.5f) continue;

                    float distToTarget = Vector3.Distance(node.Vector3Position, targetPos);
                    float pathRisk = aiCell.ProvokedAttacks * WEIGHT_AOO
                        + aiCell.EnteredAoE * WEIGHT_AOE_ENTRY
                        + aiCell.StepsInsideDamagingAoE * WEIGHT_DAMAGING_AOE_STEP;

                    if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] {unit.CharacterName}: A* detour step {i}/{pathNodes.Count - 1}, dist={distToTarget:F1}m, pathRisk={pathRisk:F1}");

                    return new PositionScore
                    {
                        Node = node,
                        CanStand = true,
                        DistanceScore = 100f - distToTarget,
                        PathRiskScore = pathRisk
                    };
                }

                if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] {unit.CharacterName}: No A* detour node found ({reachableTiles.Count} reachable tiles)");
                return null;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Log.Engine.Error(ex, $"[MovementAPI] FindApproachAlongPath error");
                return null;
            }
        }

        /// <summary>
        /// ★ v3.9.60: Phase 1 — A* 경로에서 적에게 더 가까운 도달 가능 노드 탐색
        /// 경로 끝(타겟 쪽)부터 역방향 검색. 거리 가드 적용 (현재보다 가까운 노드만).
        /// 현재 위치와 동일한 노드(1.5m 이내)는 제외.
        /// </summary>
        private static PositionScore SearchApproachPhase1(
            BaseUnitEntity unit,
            List<GraphNode> pathNodes,
            int minIdx,
            Dictionary<GraphNode, WarhammerPathAiCell> reachableTiles,
            Vector3 targetPos,
            float currentDistToTarget)
        {
            for (int i = pathNodes.Count - 1; i >= minIdx; i--)
            {
                var pathNode = pathNodes[i];
                if (!reachableTiles.TryGetValue(pathNode, out var aiCell)) continue;

                var node = aiCell.Node as CustomGridNodeBase;
                if (node == null || !aiCell.IsCanStand) continue;

                float distToTarget = Vector3.Distance(node.Vector3Position, targetPos);

                // 거리 가드: 현재보다 적에게 더 가까운 노드만
                if (distToTarget >= currentDistToTarget)
                {
                    if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] {unit.CharacterName}: A* step {i} skipped — not closer (dist={distToTarget:F1} >= current={currentDistToTarget:F1})");
                    continue;
                }

                // 현재 위치와 동일한 노드 제외 (부동소수점 오차 방지)
                if (Vector3.Distance(node.Vector3Position, unit.Position) < 1.5f)
                {
                    if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] {unit.CharacterName}: A* step {i} skipped — same as current position");
                    continue;
                }

                float pathRisk = aiCell.ProvokedAttacks * WEIGHT_AOO
                    + aiCell.EnteredAoE * WEIGHT_AOE_ENTRY
                    + aiCell.StepsInsideDamagingAoE * WEIGHT_DAMAGING_AOE_STEP;

                if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] {unit.CharacterName}: A* approach node at step {i}/{pathNodes.Count - 1}, dist={distToTarget:F1}, pathRisk={pathRisk:F1}");

                return new PositionScore
                {
                    Node = node,
                    CanStand = true,
                    DistanceScore = 100f - distToTarget,
                    PathRiskScore = pathRisk
                };
            }

            return null;
        }

        /// <summary>
        /// ★ v3.9.42: 경로 위에서 주어진 위치에 가장 가까운 노드 인덱스 찾기
        /// </summary>
        private static int FindNearestNodeOnPath(Vector3 position, List<GraphNode> path)
        {
            int bestIdx = -1;
            float bestDist = float.MaxValue;

            // 경로 전반부만 탐색 (현재 위치는 경로 시작 부분에 있을 것)
            int searchLimit = Math.Min(path.Count, path.Count / 2 + 5);
            for (int i = 0; i < searchLimit; i++)
            {
                var node = path[i] as CustomGridNodeBase;
                if (node == null) continue;

                float dist = Vector3.Distance(position, node.Vector3Position);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = i;
                }
            }

            // 2타일(2.7m) 이내여야 유효한 매칭
            return bestDist < 2.7f ? bestIdx : -1;
        }

        #endregion
    }
}
