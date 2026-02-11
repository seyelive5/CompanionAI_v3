using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using CompanionAI_v3.Core;
using Kingmaker.Enums;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic;
using Pathfinding;
using UnityEngine;
using CompanionAI_v3.GameInterface;

namespace CompanionAI_v3.Analysis
{
    /// <summary>
    /// ★ v3.7.00: 사역마 위치 최적화 시스템
    /// - 4타일 반경 규칙 기반 최적 위치 계산
    /// - 사역마 타입별 다른 위치 전략
    /// ★ v3.7.75: 게임 네이티브 API (GetNodesSpiralAround + CanStandHere) 사용으로 단순화
    /// ★ v3.7.76: 탐색 반경을 MAX_SEARCH_RADIUS_TILES로 제한 (무한 루프 방지)
    /// </summary>
    public static class FamiliarPositioner
    {
        #region Constants

        /// <summary>
        /// 사역마 효과 반경 (타일)
        /// </summary>
        public const float EFFECT_RADIUS_TILES = 4f;

        /// <summary>
        /// Relocate가 필요한 최소 거리 (타일)
        /// ★ v3.7.22: 3f → 2f로 변경 (2타일 이동도 유의미한 커버리지 향상 가능)
        /// </summary>
        public const float MIN_RELOCATE_DISTANCE_TILES = 2f;

        /// <summary>
        /// ★ v3.7.75: 최대 탐색 반경 (타일) - GetNodesSpiralAround 용
        /// </summary>
        private const int MAX_SEARCH_RADIUS_TILES = 10;

        #endregion

        #region ★ v3.9.10: Zero-alloc 정적 리스트 (LINQ Where().ToList() 제거)

        private static readonly List<BaseUnitEntity> _sharedValidAllies = new List<BaseUnitEntity>(16);
        private static readonly List<BaseUnitEntity> _sharedValidEnemies = new List<BaseUnitEntity>(16);
        private static readonly List<BaseUnitEntity> _sharedThreateningEnemies = new List<BaseUnitEntity>(4);

        #endregion

        #region Position Score

        /// <summary>
        /// 위치 점수 결과
        /// </summary>
        public class PositionScore
        {
            public Vector3 Position { get; set; }
            public int AlliesInRange { get; set; }
            public int EnemiesInRange { get; set; }
            public float Score { get; set; }
            public string Reason { get; set; }

            /// <summary>★ v3.8.52: Raven 턴 단위 페이즈 - true면 버프 배포 우선, false면 디버프/공격 전환</summary>
            public bool IsBuffPhase { get; set; } = true;

            public override string ToString()
            {
                return $"Pos=({Position.x:F1}, {Position.z:F1}), Allies={AlliesInRange}, Enemies={EnemiesInRange}, Score={Score:F1}";
            }
        }

        #endregion

        #region Main API

        /// <summary>
        /// 사역마 타입별 최적 위치 계산
        /// ★ v3.7.22: maxRangeMeters 파라미터 추가 - Relocate 사거리 제한
        /// </summary>
        public static PositionScore FindOptimalPosition(
            BaseUnitEntity master,
            PetType familiarType,
            List<BaseUnitEntity> allies,
            List<BaseUnitEntity> enemies,
            float maxRangeMeters = 0f)
        {
            if (master == null)
                return CreateDefaultPosition(master?.Position ?? Vector3.zero);

            try
            {
                return familiarType switch
                {
                    // 버프 확산형: 아군 중심
                    PetType.ServoskullSwarm => FindBuffCenterPosition(master, allies, enemies, maxRangeMeters),
                    // ★ v3.7.90: Raven은 페이즈 기반 위치 결정
                    PetType.Raven => FindRavenOptimalPosition(master, allies, enemies, maxRangeMeters),

                    // 적 제어형: 위협적 적 근처
                    PetType.Mastiff => FindApprehendPosition(master, allies, enemies, maxRangeMeters),
                    PetType.Eagle => FindDisruptPosition(master, allies, enemies, maxRangeMeters),

                    // 기본값
                    _ => FindBuffCenterPosition(master, allies, enemies, maxRangeMeters)
                };
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[FamiliarPositioner] FindOptimalPosition error: {ex.Message}");
                return CreateDefaultPosition(master.Position);
            }
        }

        /// <summary>
        /// Relocate가 필요한지 판단
        /// ★ v3.7.22: 현재 위치 대비 커버리지 향상 기반으로 판단 (절대 점수 기준 제거)
        /// ★ v3.8.53: 페이즈 인식 - 디버프 모드에서는 적 커버리지 향상만으로 재배치 허용
        /// </summary>
        public static bool ShouldRelocate(
            BaseUnitEntity familiar,
            PositionScore optimalPosition,
            float currentDistanceToOptimal,
            int currentAlliesInRange = 0,
            int currentEnemiesInRange = 0)
        {
            if (familiar == null || optimalPosition == null)
                return false;

            // 1. 최소 거리 이상 떨어져 있어야 Relocate 의미 있음
            float distanceTiles = CombatAPI.MetersToTiles(currentDistanceToOptimal);
            if (distanceTiles < MIN_RELOCATE_DISTANCE_TILES)
                return false;

            // ★ v3.8.56: 디버프 모드 여부 먼저 확인
            bool isDebuffMode = !optimalPosition.IsBuffPhase;

            // 2. 최적 위치에서 효과를 받을 유닛 수 체크
            // ★ v3.8.56: 디버프 모드에서는 적 1명이라도 있으면 재배치 가치 있음
            //   사람 플레이어라면 "일단 뭐라도 해야겠다" → 적 1명이라도 찾아 이동
            int totalAffected = optimalPosition.AlliesInRange + optimalPosition.EnemiesInRange;
            int minAffected = isDebuffMode ? 1 : 2;
            if (totalAffected < minAffected)
                return false;

            // ★ v3.7.22: 커버리지 향상 기준으로 판단
            int alliesGained = optimalPosition.AlliesInRange - currentAlliesInRange;
            int enemiesGained = optimalPosition.EnemiesInRange - currentEnemiesInRange;
            int netGain = alliesGained + enemiesGained;

            // ★ v3.8.56: 디버프 모드에서는 적 1명 이상 커버 가능하면 재배치 가치 있음
            // 적이 흩어져 있어도 가장 가까운/강한 적이라도 찾아 디버프/공격
            if (isDebuffMode && enemiesGained >= 1)
            {
                Main.LogDebug($"[FamiliarPositioner] ShouldRelocate: Yes (DEBUFF MODE - enemy gain={enemiesGained}, " +
                    $"allies {currentAlliesInRange}→{optimalPosition.AlliesInRange}, " +
                    $"enemies {currentEnemiesInRange}→{optimalPosition.EnemiesInRange})");
                return true;
            }

            // 버프 모드: 최소 1명 이상 추가 커버 가능해야 함 (기존 로직)
            if (netGain < 1)
            {
                Main.LogDebug($"[FamiliarPositioner] ShouldRelocate: No (no coverage gain: " +
                    $"allies {currentAlliesInRange}→{optimalPosition.AlliesInRange}, " +
                    $"enemies {currentEnemiesInRange}→{optimalPosition.EnemiesInRange}, " +
                    $"phase={( isDebuffMode ? "DEBUFF" : "BUFF")})");
                return false;
            }

            Main.LogDebug($"[FamiliarPositioner] ShouldRelocate: Yes (distance={distanceTiles:F1} tiles, gain=+{netGain}, allies {currentAlliesInRange}→{optimalPosition.AlliesInRange}, enemies {currentEnemiesInRange}→{optimalPosition.EnemiesInRange})");
            return true;
        }

        #endregion

        #region Position Strategies

        /// <summary>
        /// 버프 확산형 (Servo-Skull, Psyber-Raven): 아군 중심 위치
        /// ★ v3.7.22: maxRangeMeters 파라미터 추가
        /// </summary>
        private static PositionScore FindBuffCenterPosition(
            BaseUnitEntity master,
            List<BaseUnitEntity> allies,
            List<BaseUnitEntity> enemies,
            float maxRangeMeters = 0f)
        {
            // ★ v3.9.10: LINQ → FillWhere (GC 할당 제거)
            CollectionHelper.FillWhere(allies, _sharedValidAllies, a => a != null && a.IsConscious && !FamiliarAPI.IsFamiliar(a));
            var validAllies = _sharedValidAllies;

            if (validAllies.Count == 0)
                return CreateDefaultPosition(master.Position);

            // 아군들의 중심점 계산
            Vector3 centroid = CalculateCentroid(validAllies);

            // 중심점 주변에서 최적 위치 탐색
            var bestPosition = FindBestPositionAroundPoint(
                centroid,
                master,
                validAllies,
                enemies ?? new List<BaseUnitEntity>(),
                prioritizeAllies: true,
                maxRangeMeters: maxRangeMeters);

            Main.LogDebug($"[FamiliarPositioner] BuffCenter: {bestPosition}");
            return bestPosition;
        }

        /// <summary>
        /// ★ v3.7.90: Raven 페이즈 기반 최적 위치 결정
        /// - 버프 페이즈: 아군 버프 커버리지 낮으면 아군 중심 위치
        /// - 공격/디버프 페이즈: 아군 버프 충분하면 적 밀집 지역으로 이동
        /// ★ v3.8.30: 물리적 거리 대신 실제 사이킥 버프 보유 여부로 커버리지 계산
        /// </summary>
        private static PositionScore FindRavenOptimalPosition(
            BaseUnitEntity master,
            List<BaseUnitEntity> allies,
            List<BaseUnitEntity> enemies,
            float maxRangeMeters = 0f)
        {
            // ★ v3.9.10: LINQ → FillWhere (GC 할당 제거)
            CollectionHelper.FillWhere(allies, _sharedValidAllies, a => a != null && a.IsConscious && !FamiliarAPI.IsFamiliar(a));
            var validAllies = _sharedValidAllies;
            CollectionHelper.FillWhere(enemies, _sharedValidEnemies, e => e != null && e.IsConscious);
            var validEnemies = _sharedValidEnemies;

            // ★ v3.8.58: AllyStateCache 기반 정확한 커버리지 (모든 사이킨 버프 타입별 개별 확인)
            // 기존: ANY ONE 사이킨 버프 보유 → "buffed" 판정 (조짐만 확산해도 100%)
            // 변경: 총 (아군×버프타입) 인스턴스 / 가능 최대 = 정확한 커버리지
            float buffCoverage = Core.AllyStateCache.GetPsychicBuffCoverage();
            int totalBuffTypes = Core.AllyStateCache.MasterWarpRelayBuffCount;

            // totalBuffTypes=0 → 확산할 사이킨 버프 없음 → 디버프/공격 페이즈
            // coverage < 60% → 아직 확산할 버프 남음 → 버프 페이즈 유지
            bool isBuffPhase = totalBuffTypes > 0 && buffCoverage < 0.6f;

            if (isBuffPhase)
            {
                Main.LogDebug($"[FamiliarPositioner] Raven BUFF PHASE: coverage={buffCoverage:P0} ({totalBuffTypes} WR buff types, {Core.AllyStateCache.AllyCount} allies)");
                var buffPos = FindBuffCenterPosition(master, allies, enemies, maxRangeMeters);
                buffPos.Reason = $"Buff phase (psychic coverage={buffCoverage:P0})";
                buffPos.IsBuffPhase = true;  // ★ v3.8.52: 턴 단위 페이즈 전달
                return buffPos;
            }
            else
            {
                Main.LogDebug($"[FamiliarPositioner] Raven ATTACK/DEBUFF PHASE: coverage={buffCoverage:P0} - moving to enemy cluster");

                // 적 밀집 지역으로 이동 (디버프/공격용)
                if (validEnemies.Count > 0)
                {
                    // ★ v3.8.55: Raven support ability 실제 사거리로 도달 가능 범위 제한
                    var familiar = FamiliarAPI.GetFamiliar(master);
                    float supportRangeMeters = FamiliarAPI.GetRavenSupportRangeMeters(familiar);
                    Vector3? ravenPos = familiar?.Position;

                    var enemyClusterCenter = FindEnemyClusterCenter(validEnemies);
                    var attackPos = FindBestPositionAroundPoint(
                        enemyClusterCenter,
                        master,
                        validAllies,
                        validEnemies,
                        prioritizeAllies: false,  // 적 우선
                        maxRangeMeters: maxRangeMeters,
                        familiarPos: ravenPos,
                        familiarRangeMeters: supportRangeMeters);

                    attackPos.Reason = $"Attack phase - enemy cluster ({attackPos.EnemiesInRange} enemies)";
                    attackPos.IsBuffPhase = false;  // ★ v3.8.52: 공격/디버프 페이즈

                    if (ravenPos.HasValue)
                    {
                        float distFromRaven = Vector3.Distance(ravenPos.Value, attackPos.Position);
                        Main.LogDebug($"[FamiliarPositioner] Raven Attack: {attackPos} (dist from Raven={distFromRaven:F1}m, supportRange={supportRangeMeters:F1}m)");
                    }
                    else
                    {
                        Main.LogDebug($"[FamiliarPositioner] Raven Attack: {attackPos}");
                    }
                    return attackPos;
                }

                // 적이 없으면 아군 위치 유지
                var fallbackPos = FindBuffCenterPosition(master, allies, enemies, maxRangeMeters);
                fallbackPos.IsBuffPhase = false;  // ★ v3.8.52: 커버리지 충분하지만 적 없음
                return fallbackPos;
            }
        }

        /// <summary>
        /// Apprehend 타겟 (Cyber-Mastiff): 위협적 원거리 적 근처
        /// ★ v3.7.22: maxRangeMeters 파라미터 추가
        /// </summary>
        private static PositionScore FindApprehendPosition(
            BaseUnitEntity master,
            List<BaseUnitEntity> allies,
            List<BaseUnitEntity> enemies,
            float maxRangeMeters = 0f)
        {
            // ★ v3.9.10: LINQ → FillWhere + 수동 top-3 (GC 할당 제거)
            CollectionHelper.FillWhere(enemies, _sharedValidEnemies, e => e != null && e.IsConscious);
            var validEnemies = _sharedValidEnemies;

            if (validEnemies.Count == 0)
                return CreateDefaultPosition(master.Position);

            // 위협적인 원거리 적/시전자 찾기 (top-3 by threat)
            // ★ v3.9.10: LINQ OrderByDescending().Take(3) → 수동 top-3 선별
            _sharedThreateningEnemies.Clear();
            float t1 = float.MinValue, t2 = float.MinValue, t3 = float.MinValue;
            BaseUnitEntity e1 = null, e2 = null, e3 = null;
            for (int i = 0; i < validEnemies.Count; i++)
            {
                var e = validEnemies[i];
                if (!IsRangedOrCasterEnemy(e)) continue;
                float t = EstimateThreat(e);
                if (t > t1) { t3 = t2; e3 = e2; t2 = t1; e2 = e1; t1 = t; e1 = e; }
                else if (t > t2) { t3 = t2; e3 = e2; t2 = t; e2 = e; }
                else if (t > t3) { t3 = t; e3 = e; }
            }
            if (e1 != null) _sharedThreateningEnemies.Add(e1);
            if (e2 != null) _sharedThreateningEnemies.Add(e2);
            if (e3 != null) _sharedThreateningEnemies.Add(e3);

            if (_sharedThreateningEnemies.Count == 0)
            {
                int takeCount = Math.Min(3, validEnemies.Count);
                for (int i = 0; i < takeCount; i++)
                    _sharedThreateningEnemies.Add(validEnemies[i]);
            }
            var threateningEnemies = _sharedThreateningEnemies;

            // 위협적 적의 중심점
            Vector3 targetCenter = CalculateCentroid(threateningEnemies);

            // 해당 위치 근처에서 최적 위치 탐색
            var bestPosition = FindBestPositionAroundPoint(
                targetCenter,
                master,
                allies ?? new List<BaseUnitEntity>(),
                validEnemies,
                prioritizeAllies: false,
                maxRangeMeters: maxRangeMeters);

            bestPosition.Reason = $"Near threatening enemies ({threateningEnemies.Count})";
            Main.LogDebug($"[FamiliarPositioner] Apprehend: {bestPosition}");
            return bestPosition;
        }

        /// <summary>
        /// 적 교란형 (Cyber-Eagle): 적 밀집 지역
        /// ★ v3.7.22: maxRangeMeters 파라미터 추가
        /// </summary>
        private static PositionScore FindDisruptPosition(
            BaseUnitEntity master,
            List<BaseUnitEntity> allies,
            List<BaseUnitEntity> enemies,
            float maxRangeMeters = 0f)
        {
            // ★ v3.9.10: LINQ → FillWhere (GC 할당 제거)
            CollectionHelper.FillWhere(enemies, _sharedValidEnemies, e => e != null && e.IsConscious);
            var validEnemies = _sharedValidEnemies;

            if (validEnemies.Count == 0)
                return CreateDefaultPosition(master.Position);

            // 적 밀집 지역 찾기
            var bestClusterCenter = FindEnemyClusterCenter(validEnemies);

            // 해당 위치 근처에서 최적 위치 탐색
            var bestPosition = FindBestPositionAroundPoint(
                bestClusterCenter,
                master,
                allies ?? new List<BaseUnitEntity>(),
                validEnemies,
                prioritizeAllies: false,
                maxRangeMeters: maxRangeMeters);

            bestPosition.Reason = "Enemy cluster center";
            Main.LogDebug($"[FamiliarPositioner] Disrupt: {bestPosition}");
            return bestPosition;
        }

        #endregion

        #region Position Calculation Helpers

        /// <summary>
        /// ★ v3.7.75: 게임 네이티브 API를 사용하여 유효한 노드들 수집
        /// UnitPartPetOwner.GetFreeNodeAround() 패턴 사용
        /// ★ v3.7.77: 디버그 로깅 추가 - centerNode의 Y값 확인
        /// </summary>
        internal static IEnumerable<CustomGridNodeBase> GetValidNodesAround(
            BaseUnitEntity familiar,
            Vector3 center,
            int radius)
        {
            var centerNode = center.GetNearestNodeXZ() as CustomGridNodeBase;
            if (centerNode == null)
            {
                Main.LogDebug($"[FamiliarPositioner] GetValidNodesAround: centerNode is null for center={center}");
                yield break;
            }

            // ★ v3.7.77: centerNode의 Y 값 로깅
            Main.LogDebug($"[FamiliarPositioner] GetValidNodesAround: center=({center.x:F1},{center.y:F1},{center.z:F1}) -> " +
                $"centerNode.Vector3Position=({centerNode.Vector3Position.x:F1},{centerNode.Vector3Position.y:F1},{centerNode.Vector3Position.z:F1})");

            int validCount = 0;
            // ★ 게임 API 사용: GridAreaHelper.GetNodesSpiralAround
            // 중심점에서 나선형으로 노드를 반환
            foreach (var node in GridAreaHelper.GetNodesSpiralAround(
                centerNode,
                familiar?.SizeRect ?? new IntRect(0, 0, 0, 0),
                radius,
                ignoreHeightDiff: true))
            {
                // ★ 게임 API 사용: CanStandHere (Walkable + 크기 + 블로킹 종합 검증)
                if (familiar != null && familiar.CanStandHere(node, null))
                {
                    validCount++;
                    // ★ v3.7.77: 첫 5개 유효 노드의 Y값 로깅
                    if (validCount <= 5)
                    {
                        Main.LogDebug($"[FamiliarPositioner] ValidNode #{validCount}: ({node.Vector3Position.x:F1},{node.Vector3Position.y:F1},{node.Vector3Position.z:F1})");
                    }
                    yield return node;
                }
                else if (familiar == null && node.Walkable)
                {
                    // 사역마가 없으면 단순 Walkable 체크
                    yield return node;
                }
            }

            Main.LogDebug($"[FamiliarPositioner] GetValidNodesAround: Found {validCount} valid nodes");
        }

        /// <summary>
        /// 주어진 점 주변에서 최적 위치 탐색
        /// ★ v3.7.75: 게임 네이티브 API (GetNodesSpiralAround + CanStandHere + node.Vector3Position) 사용
        /// - 복잡한 수동 샘플링 제거
        /// - 정확한 Y 좌표 보장 (node.Vector3Position)
        /// ★ v3.8.55: familiarPos/familiarRangeMeters - Raven support ability 사거리 제한
        /// </summary>
        private static PositionScore FindBestPositionAroundPoint(
            Vector3 center,
            BaseUnitEntity master,
            List<BaseUnitEntity> allies,
            List<BaseUnitEntity> enemies,
            bool prioritizeAllies,
            float maxRangeMeters = 0f,
            Vector3? familiarPos = null,
            float familiarRangeMeters = 0f)
        {
            PositionScore best = null;
            float bestScore = float.MinValue;
            Vector3 masterPos = master?.Position ?? center;

            // ★ v3.7.75: maxRange 타일 단위로 변환
            // ★ v3.7.76: 탐색 반경을 MAX_SEARCH_RADIUS_TILES로 제한 (무한 루프 방지)
            bool hasRangeLimit = maxRangeMeters > 0f;
            int rawTiles = hasRangeLimit
                ? Math.Max(1, (int)CombatAPI.MetersToTiles(maxRangeMeters))
                : MAX_SEARCH_RADIUS_TILES;
            int maxRangeTiles = Math.Min(rawTiles, MAX_SEARCH_RADIUS_TILES);

            // ★ v3.7.75: 사역마 찾기 (CanStandHere 검증용)
            BaseUnitEntity familiar = FamiliarAPI.GetFamiliar(master);

            // ★ v3.7.75: 게임 네이티브 API로 유효한 노드 반복
            foreach (var node in GetValidNodesAround(familiar, center, maxRangeTiles))
            {
                // ★ 핵심: node.Vector3Position은 올바른 Y 좌표 포함
                Vector3 pos = node.Vector3Position;

                // 범위 체크 (미터 단위)
                if (hasRangeLimit && Vector3.Distance(masterPos, pos) > maxRangeMeters)
                    continue;

                // ★ v3.8.55: Raven 도달 가능 거리 체크
                // Raven support ability 사거리를 초과하면 재배치 시 TargetTooFar 에러
                if (familiarPos.HasValue && familiarRangeMeters > 0f)
                {
                    if (Vector3.Distance(familiarPos.Value, pos) > familiarRangeMeters)
                        continue;
                }

                var score = EvaluatePosition(pos, allies, enemies, prioritizeAllies);
                if (score.Score > bestScore)
                {
                    bestScore = score.Score;
                    best = score;
                }
            }

            // 유효한 위치를 찾지 못하면 폴백
            if (best == null)
            {
                Main.LogDebug($"[FamiliarPositioner] No valid position found around ({center.x:F1}, {center.z:F1})");
                return CreateDefaultPosition(center);
            }

            Main.LogDebug($"[FamiliarPositioner] Found best position: ({best.Position.x:F1}, {best.Position.y:F1}, {best.Position.z:F1}) Score={best.Score:F1}");
            return best;
        }

        /// <summary>
        /// 위치 점수 평가
        /// </summary>
        private static PositionScore EvaluatePosition(
            Vector3 position,
            List<BaseUnitEntity> allies,
            List<BaseUnitEntity> enemies,
            bool prioritizeAllies)
        {
            int alliesInRange = FamiliarAPI.CountAlliesInRadius(position, EFFECT_RADIUS_TILES, allies);
            int enemiesInRange = FamiliarAPI.CountEnemiesInRadius(position, EFFECT_RADIUS_TILES, enemies);

            float score;
            if (prioritizeAllies)
            {
                // 버프 확산: 아군 우선
                score = alliesInRange * 30f + enemiesInRange * 5f;

                // 3명 이상 아군이면 보너스
                if (alliesInRange >= 3) score += 50f;
            }
            else
            {
                // 적 제어: 적 우선
                score = enemiesInRange * 30f + alliesInRange * 5f;

                // 2명 이상 적이면 보너스
                if (enemiesInRange >= 2) score += 40f;
            }

            return new PositionScore
            {
                Position = position,
                AlliesInRange = alliesInRange,
                EnemiesInRange = enemiesInRange,
                Score = score,
                Reason = prioritizeAllies ? "Ally coverage" : "Enemy coverage"
            };
        }

        /// <summary>
        /// 유닛 목록의 중심점 계산
        /// </summary>
        private static Vector3 CalculateCentroid(List<BaseUnitEntity> units)
        {
            if (units == null || units.Count == 0)
                return Vector3.zero;

            Vector3 sum = Vector3.zero;
            int count = 0;

            foreach (var unit in units)
            {
                if (unit != null)
                {
                    sum += unit.Position;
                    count++;
                }
            }

            return count > 0 ? sum / count : Vector3.zero;
        }

        /// <summary>
        /// 적 클러스터 중심 찾기
        /// </summary>
        private static Vector3 FindEnemyClusterCenter(List<BaseUnitEntity> enemies)
        {
            if (enemies == null || enemies.Count == 0)
                return Vector3.zero;

            // 가장 많은 적이 모인 위치 찾기
            Vector3 bestCenter = enemies[0].Position;
            int bestCount = 0;

            foreach (var enemy in enemies)
            {
                int count = FamiliarAPI.CountEnemiesInRadius(enemy.Position, EFFECT_RADIUS_TILES, enemies);
                if (count > bestCount)
                {
                    bestCount = count;
                    bestCenter = enemy.Position;
                }
            }

            // 해당 적 주변 적들의 중심점 반환
            var nearbyEnemies = FamiliarAPI.GetEnemiesInRadius(bestCenter, EFFECT_RADIUS_TILES, enemies);
            return CalculateCentroid(nearbyEnemies);
        }

        /// <summary>
        /// 원거리/시전자 적인지 확인
        /// </summary>
        private static bool IsRangedOrCasterEnemy(BaseUnitEntity enemy)
        {
            if (enemy == null) return false;

            try
            {
                // 무기 확인 - 원거리 무기 보유 여부
                var weapon = enemy.Body?.PrimaryHand?.MaybeWeapon;
                if (weapon != null && weapon.Blueprint?.IsMelee == false)
                    return true;

                // 보조 무기도 확인
                var secondaryWeapon = enemy.Body?.SecondaryHand?.MaybeWeapon;
                if (secondaryWeapon != null && secondaryWeapon.Blueprint?.IsMelee == false)
                    return true;
            }
            catch
            {
                // 에러 시 false 반환
            }

            return false;
        }

        /// <summary>
        /// 적 위협도 추정
        /// </summary>
        private static float EstimateThreat(BaseUnitEntity enemy)
        {
            if (enemy == null) return 0f;

            float threat = 0f;

            try
            {
                // 기본 위협 (HP 기반)
                float hpRatio = enemy.Health.HitPointsLeft / (float)enemy.Health.MaxHitPoints;
                threat += (1f - hpRatio) * 10f;  // 체력 많이 남은 적이 더 위협적

                // 원거리면 추가 위협
                if (IsRangedOrCasterEnemy(enemy))
                    threat += 30f;
            }
            catch
            {
                threat = 10f;
            }

            return threat;
        }

        /// <summary>
        /// 기본 위치 생성 (마스터 근처)
        /// </summary>
        private static PositionScore CreateDefaultPosition(Vector3 position)
        {
            return new PositionScore
            {
                Position = position,
                AlliesInRange = 0,
                EnemiesInRange = 0,
                Score = 0f,
                Reason = "Default (no valid targets)"
            };
        }

        #endregion
    }
}
