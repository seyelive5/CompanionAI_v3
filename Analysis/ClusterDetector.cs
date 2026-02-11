using System;
using System.Collections.Generic;
// ★ v3.9.04: LINQ 제거 — 모든 LINQ를 수동 루프로 대체 (GC 0)
// using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using UnityEngine;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Analysis
{
    /// <summary>
    /// 적 클러스터 데이터
    /// ★ v3.9.04: Reset() 추가 (오브젝트 풀링 지원)
    /// </summary>
    public class EnemyCluster
    {
        /// <summary>클러스터 중심 (가중 평균 위치)</summary>
        public Vector3 Center { get; set; }

        /// <summary>클러스터 내 적 목록</summary>
        public List<BaseUnitEntity> Enemies { get; set; } = new List<BaseUnitEntity>();

        /// <summary>클러스터 반경 (중심에서 가장 먼 적까지 거리)</summary>
        public float Radius { get; set; }

        /// <summary>평균 분산도 (중심에서 평균 거리)</summary>
        public float AverageSpread { get; set; }

        /// <summary>밀도 = 적 수 / 면적</summary>
        public float Density { get; set; }

        /// <summary>AOE 타겟팅 품질 점수 (0-100+)</summary>
        public float QualityScore { get; set; }

        /// <summary>클러스터 내 적 수</summary>
        public int Count => Enemies.Count;

        /// <summary>
        /// ★ v3.5.76: 유효한 클러스터인가? (설정의 MinClusterSize 이상)
        /// 기본값 2, 설정으로 1까지 완화 가능
        /// </summary>
        public bool IsValid => Count >= ClusterDetector.MIN_CLUSTER_SIZE;

        /// <summary>
        /// ★ v3.9.04: 풀 반환 전 상태 초기화
        /// </summary>
        public void Reset()
        {
            Center = Vector3.zero;
            Enemies.Clear();
            Radius = 0f;
            AverageSpread = 0f;
            Density = 0f;
            QualityScore = 0f;
        }
    }

    /// <summary>
    /// ★ v3.3.00: 적 클러스터 탐지 알고리즘
    /// AOE 최적화를 위한 밀도 기반 공간 분석
    /// ★ v3.9.04: Zero-alloc 최적화 — LINQ 전면 제거, 오브젝트 풀링, 정적 버퍼 재사용
    /// </summary>
    public static class ClusterDetector
    {
        #region Configuration

        /// <summary>
        /// ★ v3.5.98: 기본 클러스터 탐색 반경 (타일 단위)
        /// AOE 반경과 일관성을 위해 타일 사용
        /// </summary>
        public const float DEFAULT_CLUSTER_RADIUS = 6f;  // 타일

        /// <summary>기본 클러스터 최소 크기 (컴파일 타임 상수)</summary>
        public const int DEFAULT_MIN_CLUSTER_SIZE = 2;

        /// <summary>
        /// ★ v3.5.76: 클러스터 최소 크기 (설정에서 읽음)
        /// 기본값 2, 설정으로 1까지 완화 가능
        /// </summary>
        public static int MIN_CLUSTER_SIZE =>
            AIConfig.GetAoEConfig()?.MinClusterSize ?? DEFAULT_MIN_CLUSTER_SIZE;

        /// <summary>
        /// ★ v3.5.20: 설정에서 최대 클러스터 수 읽기 (기본값 5)
        /// </summary>
        private static int MaxClusters =>
            ModSettings.Instance?.MaxClusters ?? 5;

        /// <summary>
        /// ★ v3.5.20: 설정에서 최대 평가 위치 수 읽기 (기본값 25)
        /// </summary>
        private static int MaxPositionsToEvaluate =>
            ModSettings.Instance?.MaxPositionsToEvaluate ?? 25;

        #endregion

        #region ★ v3.9.04: Zero-alloc 정적 버퍼 & 오브젝트 풀

        // 클러스터 풀 (Stack = LIFO, 임시 객체에 최적)
        private static readonly Stack<EnemyCluster> _clusterPool = new Stack<EnemyCluster>(8);

        // 활성 클러스터 리스트 (FindClusters 반환값 — 정적 재사용)
        private static readonly List<EnemyCluster> _activeClusters = new List<EnemyCluster>(8);

        // GreedyCluster 공유 HashSet
        private static readonly HashSet<BaseUnitEntity> _sharedAssignedSet = new HashSet<BaseUnitEntity>();

        // ExpandCluster 공유 컬렉션
        private static readonly Queue<BaseUnitEntity> _sharedQueue = new Queue<BaseUnitEntity>(16);
        private static readonly HashSet<BaseUnitEntity> _sharedInClusterSet = new HashSet<BaseUnitEntity>();

        // 필터링용 임시 리스트
        private static readonly List<BaseUnitEntity> _sharedValidEnemies = new List<BaseUnitEntity>(16);

        /// <summary>클러스터 풀에서 대여 (없으면 새로 생성)</summary>
        private static EnemyCluster RentCluster()
        {
            return _clusterPool.Count > 0 ? _clusterPool.Pop() : new EnemyCluster();
        }

        /// <summary>클러스터 풀에 반환</summary>
        private static void ReturnCluster(EnemyCluster cluster)
        {
            cluster.Reset();
            _clusterPool.Push(cluster);
        }

        #endregion

        #region Main API

        /// <summary>
        /// 적 클러스터 탐색
        /// ★ v3.5.76: minClusterSize가 -1이면 설정값 사용
        /// ★ v3.9.04: Zero-alloc — 정적 버퍼 재사용, LINQ 전면 제거
        /// </summary>
        /// <param name="enemies">분석할 적 목록</param>
        /// <param name="maxClusterRadius">클러스터 멤버십 최대 거리</param>
        /// <param name="minClusterSize">클러스터 최소 크기 (-1이면 설정값 사용)</param>
        /// <returns>품질 점수 순으로 정렬된 클러스터 목록 (정적 버퍼 — 다음 호출 전까지만 유효)</returns>
        public static List<EnemyCluster> FindClusters(
            List<BaseUnitEntity> enemies,
            float maxClusterRadius = DEFAULT_CLUSTER_RADIUS,
            int minClusterSize = -1)
        {
            // ★ v3.9.04: 이전 클러스터 풀 반환 + Clear
            for (int i = 0; i < _activeClusters.Count; i++)
                ReturnCluster(_activeClusters[i]);
            _activeClusters.Clear();

            // ★ v3.5.76: -1이면 설정값 사용, 아니면 파라미터 값 사용
            int effectiveMinSize = minClusterSize < 0 ? MIN_CLUSTER_SIZE : minClusterSize;

            if (enemies == null || enemies.Count < effectiveMinSize)
                return _activeClusters;

            try
            {
                // ★ v3.9.04: LINQ Where().ToList() 제거 → 수동 필터링
                _sharedValidEnemies.Clear();
                for (int i = 0; i < enemies.Count; i++)
                {
                    var e = enemies[i];
                    if (e != null && e.IsConscious)
                        _sharedValidEnemies.Add(e);
                }

                if (_sharedValidEnemies.Count < effectiveMinSize)
                    return _activeClusters;

                // Greedy 클러스터링: _activeClusters에 직접 추가
                PerformGreedyCluster(_sharedValidEnemies, maxClusterRadius, effectiveMinSize);

                // 품질 점수 계산
                for (int i = 0; i < _activeClusters.Count; i++)
                {
                    CalculateClusterMetrics(_activeClusters[i]);
                }

                // ★ v3.9.04: LINQ Where().OrderBy().Take().ToList() 제거
                // QualityScore 내림차순 정렬
                _activeClusters.Sort((a, b) => b.QualityScore.CompareTo(a.QualityScore));

                // 무효 클러스터 및 MaxClusters 초과분 제거 (뒤에서부터)
                int maxClusters = MaxClusters;
                for (int i = _activeClusters.Count - 1; i >= 0; i--)
                {
                    var c = _activeClusters[i];
                    if (!c.IsValid || i >= maxClusters)
                    {
                        ReturnCluster(c);
                        _activeClusters.RemoveAt(i);
                    }
                }

                return _activeClusters;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[ClusterDetector] Error in FindClusters: {ex.Message}");
                for (int i = 0; i < _activeClusters.Count; i++)
                    ReturnCluster(_activeClusters[i]);
                _activeClusters.Clear();
                return _activeClusters;
            }
        }

        /// <summary>
        /// 특정 AOE 능력에 최적인 클러스터 탐색
        /// ★ v3.9.04: LINQ 제거, inline 필터링 + best 추적
        /// </summary>
        /// <param name="ability">AOE 능력</param>
        /// <param name="caster">시전자</param>
        /// <param name="enemies">적 목록</param>
        /// <param name="allies">아군 목록</param>
        /// <returns>최적 클러스터 (없으면 null)</returns>
        public static EnemyCluster FindBestClusterForAbility(
            AbilityData ability,
            BaseUnitEntity caster,
            List<BaseUnitEntity> enemies,
            List<BaseUnitEntity> allies)
        {
            if (ability == null || caster == null || enemies == null)
                return null;

            try
            {
                float aoERadius = CombatAPI.GetAoERadius(ability);  // 타일
                if (aoERadius <= 0) aoERadius = 3f;

                // ★ v3.5.98: 타일 단위 사용
                float abilityRange = CombatAPI.GetAbilityRangeInTiles(ability);

                // 능력 반경에 맞는 클러스터 탐색
                var clusters = FindClusters(enemies, aoERadius);

                // ★ v3.9.04: LINQ 제거 — 사거리 필터 + 아군 페널티 + best 추적을 단일 루프에서
                EnemyCluster bestCluster = null;
                float bestScore = 0f;

                for (int i = 0; i < clusters.Count; i++)
                {
                    var c = clusters[i];

                    // ★ v3.5.98: 시전자 사거리 내 클러스터만 (타일 단위)
                    if (CombatAPI.MetersToTiles(Vector3.Distance(caster.Position, c.Center)) > abilityRange)
                        continue;

                    // 아군 페널티 적용
                    // ★ v3.6.10: ability 전달하여 패턴별 높이 체크
                    ApplyAllyPenalty(c, allies, caster, aoERadius, ability);

                    // 페널티 후 유효한 클러스터 중 최고점 추적
                    if (c.QualityScore > 0 && c.QualityScore > bestScore)
                    {
                        bestScore = c.QualityScore;
                        bestCluster = c;
                    }
                }

                return bestCluster;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[ClusterDetector] Error in FindBestClusterForAbility: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 클러스터 내 최적 AOE 위치 탐색 (그리드 탐색)
        /// ★ v3.9.04: early exit 최적화
        /// </summary>
        /// <param name="cluster">대상 클러스터</param>
        /// <param name="ability">AOE 능력</param>
        /// <param name="caster">시전자</param>
        /// <param name="allies">아군 목록</param>
        /// <param name="aoERadius">AOE 반경</param>
        /// <returns>최적 위치 (없으면 null)</returns>
        public static Vector3? FindOptimalAoEPosition(
            EnemyCluster cluster,
            AbilityData ability,
            BaseUnitEntity caster,
            List<BaseUnitEntity> allies,
            float aoERadius)
        {
            // ★ v3.5.76: 설정 기반 최소 크기 검증
            if (cluster == null || !cluster.IsValid || ability == null || caster == null)
                return null;

            try
            {
                // ★ v3.5.98: 타일 단위 사용
                float abilityRange = CombatAPI.GetAbilityRangeInTiles(ability);

                // ★ v3.5.76: 설정 기반 아군 피격 제한
                var aoeConfig = AIConfig.GetAoEConfig();
                int maxPlayerAlliesHit = aoeConfig?.MaxPlayerAlliesHit ?? 1;

                // 클러스터 중심에서 시작
                // ★ v3.6.10: ability 전달하여 패턴별 높이 체크
                Vector3 bestPosition = cluster.Center;
                int bestHits = CountEnemiesInRadius(cluster.Center, cluster.Enemies, aoERadius, ability);

                // ★ v3.5.20: 중심 주변 그리드 탐색 (설정에서 성능 제한 읽음)
                float searchStep = aoERadius / 3f;  // 반경당 3단계
                float searchRadius = cluster.Radius + searchStep;

                int positionsChecked = 0;
                int maxPositions = MaxPositionsToEvaluate;

                for (float dx = -searchRadius; dx <= searchRadius && positionsChecked < maxPositions; dx += searchStep)
                {
                    for (float dz = -searchRadius; dz <= searchRadius && positionsChecked < maxPositions; dz += searchStep)
                    {
                        positionsChecked++;

                        Vector3 testPos = cluster.Center + new Vector3(dx, 0, dz);

                        // ★ v3.5.98: 능력 사거리 체크 (타일 단위)
                        if (CombatAPI.MetersToTiles(Vector3.Distance(caster.Position, testPos)) > abilityRange)
                            continue;

                        // ★ v3.7.64: BattlefieldGrid Walkable 체크
                        if (BattlefieldGrid.Instance.IsValid && !BattlefieldGrid.Instance.IsWalkable(testPos))
                            continue;

                        // 적중 수 계산
                        // ★ v3.6.10: ability 전달하여 패턴별 높이 체크
                        int hits = CountEnemiesInRadius(testPos, cluster.Enemies, aoERadius, ability);

                        // ★ v3.9.04: early exit — bestHits보다 적으면 아군 체크 불필요
                        if (hits < bestHits)
                            continue;

                        // ★ v3.5.76: 아군 안전 체크 (설정 기반)
                        int allyHits = CountAlliesInRadius(testPos, allies, caster, aoERadius, ability);
                        if (allyHits > maxPlayerAlliesHit)
                            continue;  // 설정된 최대값 초과 시 제외

                        // 더 나은 위치?
                        if (hits > bestHits || (hits == bestHits && allyHits == 0))
                        {
                            bestHits = hits;
                            bestPosition = testPos;
                        }
                    }
                }

                Main.LogDebug($"[ClusterDetector] Optimal position: {bestHits} hits at ({bestPosition.x:F1}, {bestPosition.z:F1})");
                return bestPosition;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[ClusterDetector] Error in FindOptimalAoEPosition: {ex.Message}");
                return cluster?.Center;
            }
        }

        #endregion

        #region Clustering Algorithm

        /// <summary>
        /// Greedy 클러스터링: 가장 밀집된 시드 찾기 → 확장 → 제거 → 반복
        /// O(n²) 복잡도, 적 10명 기준 < 1ms
        /// ★ v3.9.04: void 반환, _activeClusters에 직접 추가, 정적 HashSet 재사용
        /// </summary>
        private static void PerformGreedyCluster(
            List<BaseUnitEntity> enemies,
            float maxRadius,
            int minSize)
        {
            _sharedAssignedSet.Clear();

            while (_sharedAssignedSet.Count < enemies.Count)
            {
                // 미할당 적 중 이웃이 가장 많은 적 찾기
                BaseUnitEntity bestSeed = null;
                int bestNeighborCount = 0;

                for (int i = 0; i < enemies.Count; i++)
                {
                    var enemy = enemies[i];
                    if (_sharedAssignedSet.Contains(enemy)) continue;

                    int neighborCount = CountNeighbors(enemy, enemies, maxRadius);
                    if (neighborCount > bestNeighborCount)
                    {
                        bestNeighborCount = neighborCount;
                        bestSeed = enemy;
                    }
                }

                if (bestSeed == null || bestNeighborCount < minSize - 1)
                    break;  // 더 이상 유효한 클러스터 없음

                // 시드에서 클러스터 확장 (풀에서 대여)
                var cluster = RentCluster();
                PerformExpandCluster(bestSeed, enemies, maxRadius, cluster);

                if (cluster.Count >= minSize)
                {
                    _activeClusters.Add(cluster);
                    for (int i = 0; i < cluster.Enemies.Count; i++)
                        _sharedAssignedSet.Add(cluster.Enemies[i]);
                }
                else
                {
                    // 클러스터로 인정 안 함, 풀 반환
                    ReturnCluster(cluster);
                    _sharedAssignedSet.Add(bestSeed);
                }
            }
        }

        /// <summary>
        /// ★ v3.5.98: 반경 내 미할당 이웃 수 계산 (radius는 타일 단위)
        /// ★ v3.6.10: 높이 체크 추가 (Circle 기준 1.6m - 클러스터는 일반 AOE 기준)
        /// ★ v3.9.04: LINQ Count(predicate) 제거, _sharedAssignedSet 직접 접근
        /// </summary>
        private static int CountNeighbors(
            BaseUnitEntity seed,
            List<BaseUnitEntity> enemies,
            float radius)  // 타일
        {
            int count = 0;
            for (int i = 0; i < enemies.Count; i++)
            {
                var e = enemies[i];
                if (e == seed) continue;
                if (_sharedAssignedSet.Contains(e)) continue;
                if (CombatCache.GetDistanceInTiles(seed, e) > radius) continue;
                if (Mathf.Abs(seed.Position.y - e.Position.y) > CombatAPI.AoELevelDiffCircle) continue;
                count++;
            }
            return count;
        }

        /// <summary>
        /// ★ v3.5.98: 시드에서 클러스터 확장 (maxRadius는 타일 단위)
        /// ★ v3.6.10: 높이 체크 추가
        /// ★ v3.9.04: void 반환, cluster 파라미터로 받음, 정적 Queue/HashSet 재사용
        /// </summary>
        private static void PerformExpandCluster(
            BaseUnitEntity seed,
            List<BaseUnitEntity> enemies,
            float maxRadius,  // 타일
            EnemyCluster cluster)
        {
            _sharedQueue.Clear();
            _sharedInClusterSet.Clear();

            _sharedQueue.Enqueue(seed);
            _sharedInClusterSet.Add(seed);

            while (_sharedQueue.Count > 0)
            {
                var current = _sharedQueue.Dequeue();
                cluster.Enemies.Add(current);

                // 현재 클러스터 중심 기준 반경 내 미할당 이웃 찾기
                Vector3 clusterCenter = CalculateCenter(cluster.Enemies);

                for (int i = 0; i < enemies.Count; i++)
                {
                    var enemy = enemies[i];
                    if (_sharedAssignedSet.Contains(enemy)) continue;
                    if (_sharedInClusterSet.Contains(enemy)) continue;

                    // ★ v3.5.98: 클러스터 중심에서 거리 체크 (타일 단위)
                    // ★ v3.6.10: 높이 체크 추가 (Circle 기준 1.6m)
                    float dist = CombatAPI.MetersToTiles(Vector3.Distance(clusterCenter, enemy.Position));
                    float heightDiff = Mathf.Abs(clusterCenter.y - enemy.Position.y);
                    if (dist <= maxRadius && heightDiff <= CombatAPI.AoELevelDiffCircle)
                    {
                        _sharedQueue.Enqueue(enemy);
                        _sharedInClusterSet.Add(enemy);
                    }
                }
            }

            cluster.Center = CalculateCenter(cluster.Enemies);
        }

        /// <summary>
        /// 위치들의 가중 중심 계산
        /// </summary>
        private static Vector3 CalculateCenter(List<BaseUnitEntity> enemies)
        {
            if (enemies.Count == 0)
                return Vector3.zero;

            Vector3 sum = Vector3.zero;
            for (int i = 0; i < enemies.Count; i++)
                sum += enemies[i].Position;

            return sum / enemies.Count;
        }

        #endregion

        #region Scoring

        /// <summary>
        /// 클러스터 품질 메트릭 계산
        /// </summary>
        private static void CalculateClusterMetrics(EnemyCluster cluster)
        {
            if (cluster.Enemies.Count == 0)
            {
                cluster.QualityScore = 0;
                return;
            }

            // 분산도 계산
            float maxDist = 0f;
            float totalDist = 0f;

            for (int i = 0; i < cluster.Enemies.Count; i++)
            {
                float dist = Vector3.Distance(cluster.Center, cluster.Enemies[i].Position);
                if (dist > maxDist) maxDist = dist;
                totalDist += dist;
            }

            cluster.Radius = maxDist;
            cluster.AverageSpread = totalDist / cluster.Enemies.Count;

            // 밀도 계산 (적 수 / 면적)
            float area = Mathf.PI * maxDist * maxDist;
            cluster.Density = area > 0.1f ? cluster.Enemies.Count / area : 0f;

            // 품질 점수 계산
            // 높은 적 수 = 좋음 (선형)
            // 낮은 분산도 = 좋음 (밀집 클러스터)
            // 높은 밀도 = 좋음

            float countScore = cluster.Count * 20f;  // 적당 20점
            float tightnessBonus = Mathf.Clamp01(1f - cluster.AverageSpread / 5f) * 30f;  // 최대 30점
            float densityBonus = Mathf.Clamp01(cluster.Density * 10f) * 20f;  // 최대 20점

            cluster.QualityScore = countScore + tightnessBonus + densityBonus;

            Main.LogDebug($"[ClusterDetector] Cluster: {cluster.Count} enemies, " +
                $"spread={cluster.AverageSpread:F1}m, density={cluster.Density:F2}, " +
                $"score={cluster.QualityScore:F0}");
        }

        /// <summary>
        /// ★ v3.5.76: 클러스터 점수에 아군 근접 페널티 적용 (설정 기반)
        /// ★ v3.6.10: ability 파라미터 추가하여 패턴별 높이 체크
        /// </summary>
        private static void ApplyAllyPenalty(
            EnemyCluster cluster,
            List<BaseUnitEntity> allies,
            BaseUnitEntity caster,
            float aoERadius,
            AbilityData ability = null)
        {
            if (allies == null) return;

            var aoeConfig = AIConfig.GetAoEConfig();
            int maxPlayerAlliesHit = aoeConfig?.MaxPlayerAlliesHit ?? 1;
            // ★ v3.8.94: playerAllyPenalty 제거 — 허용 범위 내 감점 없음, 초과 시만 차단
            float npcAllyPenalty = aoeConfig?.ClusterNpcAllyPenalty ?? 20f;

            int playerPartyAlliesInRange = 0;

            for (int i = 0; i < allies.Count; i++)
            {
                var ally = allies[i];
                if (ally == null || !ally.IsConscious) continue;
                if (ally == caster) continue;

                // ★ v3.6.10: 2D 거리 + 높이 체크 통합
                if (!CombatAPI.IsUnitInAoERange(ability, cluster.Center, ally, aoERadius))
                    continue;

                // 아군이 AOE 범위 내에 있음
                try
                {
                    if (!caster.IsPlayerEnemy && ally.IsInPlayerParty)
                    {
                        playerPartyAlliesInRange++;

                        // ★ v3.8.94: 초과 시만 차단, 허용 범위 내 감점 없음
                        if (playerPartyAlliesInRange > maxPlayerAlliesHit)
                        {
                            cluster.QualityScore = -1000f;
                            Main.LogDebug($"[ClusterDetector] Cluster invalidated: {playerPartyAlliesInRange} player allies > max {maxPlayerAlliesHit}");
                            return;
                        }
                        // 허용 범위 내 → 감점 없음 (기존 ClusterAllyPenalty 제거)
                    }
                    else
                    {
                        // NPC 아군 = 작은 페널티 (설정 기반)
                        cluster.QualityScore -= npcAllyPenalty;
                    }
                }
                catch (Exception ex)
                {
                    Main.LogDebug($"[ClusterDetector] ApplyAllyPenalty error for {ally?.CharacterName}: {ex.Message}");
                }
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// ★ v3.5.98: 반경 내 적 수 계산 (radius는 타일 단위)
        /// ★ v3.6.10: 높이 체크 추가 - ability가 있으면 패턴 타입별 임계값 사용
        /// ★ v3.9.04: LINQ Count(predicate) 제거
        /// </summary>
        private static int CountEnemiesInRadius(Vector3 center, List<BaseUnitEntity> enemies, float radius, AbilityData ability = null)
        {
            int count = 0;
            for (int i = 0; i < enemies.Count; i++)
            {
                var e = enemies[i];
                if (e == null || !e.IsConscious) continue;
                if (CombatAPI.IsUnitInAoERange(ability, center, e, radius))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// ★ v3.5.98: 반경 내 플레이어 파티 아군 수 계산 (radius는 타일 단위)
        /// ★ v3.6.10: 높이 체크 추가
        /// ★ v3.9.04: LINQ Count(predicate) 제거
        /// </summary>
        private static int CountAlliesInRadius(
            Vector3 center,
            List<BaseUnitEntity> allies,
            BaseUnitEntity caster,
            float radius,  // 타일
            AbilityData ability = null)
        {
            if (allies == null) return 0;

            int count = 0;
            for (int i = 0; i < allies.Count; i++)
            {
                var a = allies[i];
                if (a == null || !a.IsConscious || a == caster) continue;
                if (caster.IsPlayerEnemy || !a.IsInPlayerParty) continue;
                if (CombatAPI.IsUnitInAoERange(ability, center, a, radius))
                    count++;
            }
            return count;
        }

        #endregion
    }
}
