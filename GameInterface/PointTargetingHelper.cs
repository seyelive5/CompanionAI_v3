using System;
using System.Collections.Generic;
using Kingmaker;
using Kingmaker.Controllers;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.View;
using Kingmaker.View.Covers;
using Pathfinding;
using UnityEngine;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// ★ v3.7.61: Point-Targeting 능력을 위한 전장 상태 인식 헬퍼
    ///
    /// 게임의 기존 시스템을 활용하여 유효한 착륙 셀을 선제적으로 탐색:
    /// - CustomGridNodeController: 점유 캐시 (O(1) Dictionary)
    /// - LosCalculations: 시야선 체크 (Quadtree 캐싱)
    /// - CustomGridGraph: 그리드 노드 접근 (O(1) 배열)
    /// - ContainsConnection: 노드 간 장애물 체크 (벽/엄폐물 감지)
    /// </summary>
    public static class PointTargetingHelper
    {
        /// <summary>
        /// ★ v3.7.37: Eagle MP 범위 내 유효한 착륙 셀 목록 반환
        /// 조건: Walkable + Unoccupied + LOS from Eagle + 높이 제한
        /// </summary>
        /// <param name="originNode">시작 노드 (Eagle 위치)</param>
        /// <param name="maxRangeTiles">최대 탐색 거리 (타일 단위)</param>
        /// <param name="minRangeTiles">최소 거리 (기본 2타일)</param>
        /// <param name="maxHeightDiff">최대 높이 차이 (미터, 기본 1.5m)</param>
        /// <returns>유효한 착륙 셀 목록</returns>
        public static List<CustomGridNodeBase> FindValidLandingCells(
            CustomGridNodeBase originNode,
            int maxRangeTiles,
            int minRangeTiles = 2,
            float maxHeightDiff = 1.5f)
        {
            var result = new List<CustomGridNodeBase>();

            if (originNode == null)
            {
                Main.LogDebug("[PointTargetingHelper] originNode is null");
                return result;
            }

            // ★ Game.Instance.CustomGridNodeController 직접 접근
            var controller = Game.Instance?.CustomGridNodeController;
            if (controller == null)
            {
                Main.LogDebug("[PointTargetingHelper] CustomGridNodeController is null");
                return result;
            }

            // 그리드 접근
            var graph = CustomGridNode.GetGridGraph(originNode.GraphIndex);
            if (graph == null)
            {
                Main.LogDebug("[PointTargetingHelper] Graph is null");
                return result;
            }

            // Origin 그리드 좌표 및 높이
            int cx = originNode.XCoordinateInGrid;
            int cz = originNode.ZCoordinateInGrid;
            float originY = ((Vector3)originNode.Vector3Position).y;

            // 반경 내 모든 셀 순회
            int scannedCells = 0;
            int validCells = 0;
            int heightFiltered = 0;

            for (int dx = -maxRangeTiles; dx <= maxRangeTiles; dx++)
            {
                for (int dz = -maxRangeTiles; dz <= maxRangeTiles; dz++)
                {
                    // ★ Warhammer 거리: max(|dx|,|dz|) + min(|dx|,|dz|)/2
                    int absDx = Math.Abs(dx);
                    int absDz = Math.Abs(dz);
                    int dist = Math.Max(absDx, absDz) + Math.Min(absDx, absDz) / 2;

                    // 거리 범위 체크
                    if (dist > maxRangeTiles || dist < minRangeTiles)
                        continue;

                    var node = graph.GetNode(cx + dx, cz + dz);
                    if (node == null)
                        continue;

                    scannedCells++;

                    // ★ 유효성 검증 (모두 O(1))
                    // 1. Walkable 체크 (★ v3.7.63: BattlefieldGrid 캐시 우선)
                    if (Analysis.BattlefieldGrid.Instance.IsValid)
                    {
                        if (!Analysis.BattlefieldGrid.Instance.IsWalkable(node))
                            continue;
                    }
                    else if (!node.Walkable)
                    {
                        continue;
                    }

                    // 2. Unoccupied 체크 (CustomGridNodeController 사용)
                    if (controller.ContainsUnit(node))
                        continue;

                    // 3. ★ v3.7.37: 높이 차이 체크 (Directional 패턴 제한)
                    float nodeY = ((Vector3)node.Vector3Position).y;
                    float heightDiff = Math.Abs(nodeY - originY);
                    if (heightDiff > maxHeightDiff)
                    {
                        heightFiltered++;
                        continue;
                    }

                    // 4. LOS 체크 - Node-to-Node 오버로드 사용
                    if (!LosCalculations.HasLos(
                            originNode, new IntRect(0, 0, 0, 0),
                            node, new IntRect(0, 0, 0, 0)))
                        continue;

                    result.Add(node);
                    validCells++;
                }
            }

            Main.LogDebug($"[PointTargetingHelper] Scanned {scannedCells} cells, found {validCells} valid (heightFiltered={heightFiltered})");
            return result;
        }

        /// <summary>
        /// ★ v3.7.39: 유효한 착륙 셀 중 경로상 적을 가장 많이 타격하는 셀 선택
        /// 적이 없는 경로는 null 반환 (능력 낭비 방지)
        /// </summary>
        /// <param name="originNode">시작 노드 (Eagle 위치)</param>
        /// <param name="validCells">유효한 착륙 셀 목록</param>
        /// <param name="enemies">적 유닛 목록</param>
        /// <param name="requireEnemy">true면 적이 없을 때 null 반환 (기본값: true)</param>
        /// <returns>최적의 착륙 셀 (또는 null)</returns>
        public static CustomGridNodeBase GetBestLandingCell(
            CustomGridNodeBase originNode,
            List<CustomGridNodeBase> validCells,
            List<BaseUnitEntity> enemies,
            bool requireEnemy = true)
        {
            if (validCells == null || validCells.Count == 0)
                return null;

            if (enemies == null || enemies.Count == 0)
            {
                // ★ v3.7.39: 적이 없으면 null 반환 (능력 낭비 방지)
                Main.LogDebug("[PointTargetingHelper] No enemies provided - skipping Aerial Rush");
                return null;
            }

            CustomGridNodeBase best = null;
            int maxEnemies = -1;
            float maxDistance = 0f;

            Vector3 originPos = (Vector3)originNode.Vector3Position;

            // ★ v3.7.39: 적 위치 디버그 로그
            Main.LogDebug($"[PointTargetingHelper] Eagle at ({originPos.x:F1}, {originPos.z:F1}), checking {enemies.Count} enemies:");
            foreach (var e in enemies)
            {
                if (e != null && e.IsConscious)
                {
                    float dist = UnityEngine.Vector2.Distance(
                        new UnityEngine.Vector2(originPos.x, originPos.z),
                        new UnityEngine.Vector2(e.Position.x, e.Position.z));
                    Main.LogDebug($"  - {e.CharacterName} at ({e.Position.x:F1}, {e.Position.z:F1}), 거리={dist:F1}m");
                }
            }

            foreach (var cell in validCells)
            {
                Vector3 cellPos = (Vector3)cell.Vector3Position;
                int enemiesInPath = CountEnemiesInPath(originPos, cellPos, enemies);
                float distance = Vector3.Distance(originPos, cellPos);

                // 경로상 적이 더 많거나, 같으면 더 먼 셀 선택
                if (enemiesInPath > maxEnemies ||
                    (enemiesInPath == maxEnemies && distance > maxDistance))
                {
                    maxEnemies = enemiesInPath;
                    maxDistance = distance;
                    best = cell;
                }
            }

            // ★ v3.7.39: 경로에 적이 없으면 null 반환 (능력 낭비 방지)
            if (requireEnemy && maxEnemies <= 0)
            {
                Main.LogDebug($"[PointTargetingHelper] No enemies in any path - skipping Aerial Rush");
                return null;
            }

            if (best != null)
            {
                Vector3 bestPos = (Vector3)best.Vector3Position;
                Main.LogDebug($"[PointTargetingHelper] Best cell at ({bestPos.x:F1}, {bestPos.z:F1}) " +
                    $"with {maxEnemies} enemies in path, distance {maxDistance:F1}m");
            }

            return best;
        }

        /// <summary>
        /// ★ v3.7.50: 게임 방식으로 재작성 - 노드 점유 기반 적 감지
        ///
        /// 게임 실제 로직 (AbilityCustomDirectMovement.GetAllTargetUnits):
        /// - 경로상 노드 목록 순회
        /// - 각 노드에서 GetAllUnits()로 유닛 확인
        ///
        /// 간소화 버전: 경로 너비(2타일)를 사용하여 히트 판정
        /// </summary>
        public static int CountEnemiesInPath(Vector3 start, Vector3 end, List<BaseUnitEntity> enemies)
        {
            if (enemies == null || enemies.Count == 0)
                return 0;

            int count = 0;

            // 2D 계산 (높이 무시)
            Vector2 start2D = new Vector2(start.x, start.z);
            Vector2 end2D = new Vector2(end.x, end.z);
            Vector2 direction2D = (end2D - start2D).normalized;
            float pathLength = Vector2.Distance(start2D, end2D);

            // ★ v3.7.50: 게임처럼 경로 너비 2타일 (이동 + 양옆 1타일)
            // 실제 게임은 노드 점유로 판정하지만, 간소화를 위해 넓은 히트박스 사용
            const float TILE_SIZE = 1.35f;
            const float PATH_WIDTH = TILE_SIZE * 2f;  // 경로 너비 = 2타일

            foreach (var enemy in enemies)
            {
                if (enemy == null || !enemy.IsConscious)
                    continue;

                // 적 SizeRect 고려 (2x2 적 등)
                IntRect sizeRect = enemy.SizeRect;
                int maxSize = Math.Max(sizeRect.Width, sizeRect.Height);
                float enemyRadius = maxSize * TILE_SIZE * 0.5f;

                // 2D 적 위치
                Vector2 enemyPos2D = new Vector2(enemy.Position.x, enemy.Position.z);
                Vector2 toEnemy = enemyPos2D - start2D;
                float projectionLength = Vector2.Dot(toEnemy, direction2D);

                // 경로 범위 체크 (적 크기 고려)
                if (projectionLength < -enemyRadius || projectionLength > pathLength + enemyRadius)
                    continue;

                // 경로로부터의 수직 거리
                Vector2 closestPointOnPath = start2D + direction2D * Mathf.Clamp(projectionLength, 0, pathLength);
                float perpendicularDistance = Vector2.Distance(enemyPos2D, closestPointOnPath);

                // ★ v3.7.50: 경로 너비 + 적 반경 = 히트 판정
                float hitThreshold = PATH_WIDTH + enemyRadius;

                if (perpendicularDistance <= hitThreshold)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// ★ v3.7.59: 노드 기반 경로 적 감지 (게임과 동일한 방식)
        ///
        /// 게임의 실제 로직 (AbilityCustomDirectMovement.GetAllTargetUnits):
        /// 1. P1→P2 경로를 따라 모든 노드 수집
        /// 2. 각 경로 노드에서 caster가 점유할 노드들 확인
        /// 3. 해당 노드에 서있는 적 유닛 감지
        ///
        /// 노드 기반이므로 기하학적 거리가 아닌 타일 점유로 판정
        /// </summary>
        public static List<BaseUnitEntity> GetEnemiesInChargePath(
            Vector3 p1Pos, Vector3 p2Pos,
            List<BaseUnitEntity> allEnemies,
            int casterSize = 1)  // Eagle = 1x1
        {
            var hitEnemies = new List<BaseUnitEntity>();

            if (allEnemies == null || allEnemies.Count == 0)
                return hitEnemies;

            // P1, P2 노드 구하기
            var p1Node = p1Pos.GetNearestNodeXZ() as CustomGridNodeBase;
            var p2Node = p2Pos.GetNearestNodeXZ() as CustomGridNodeBase;

            if (p1Node == null || p2Node == null)
            {
                Main.LogDebug("[PointTargetingHelper] GetEnemiesInChargePath: P1 or P2 node is null");
                return hitEnemies;
            }

            // ★ 경로 노드 수집 (Bresenham 알고리즘으로 모든 노드 순회)
            var pathNodes = GetNodesAlongPath(p1Node, p2Node, casterSize);

            if (pathNodes.Count == 0)
            {
                Main.LogDebug("[PointTargetingHelper] GetEnemiesInChargePath: No path nodes found");
                return hitEnemies;
            }

            Main.LogDebug($"[PointTargetingHelper] Path from ({p1Node.XCoordinateInGrid},{p1Node.ZCoordinateInGrid}) " +
                $"to ({p2Node.XCoordinateInGrid},{p2Node.ZCoordinateInGrid}): {pathNodes.Count} nodes");

            // ★ 경로 노드 좌표 세트 (빠른 검색용)
            var pathNodeCoords = new HashSet<(int x, int z)>();
            foreach (var node in pathNodes)
            {
                pathNodeCoords.Add((node.XCoordinateInGrid, node.ZCoordinateInGrid));
            }

            // ★ 각 적 유닛 체크: 점유 노드가 경로와 교차하는지
            foreach (var enemy in allEnemies)
            {
                if (enemy == null || !enemy.IsConscious)
                    continue;

                // 적이 점유하는 모든 노드 구하기 (2x2 적은 4개 노드)
                var enemyNodes = GetEnemyOccupiedNodeCoords(enemy);

                // 경로 노드와 적 점유 노드 교차 검사
                bool isHit = false;
                foreach (var enemyCoord in enemyNodes)
                {
                    if (pathNodeCoords.Contains(enemyCoord))
                    {
                        isHit = true;
                        break;
                    }
                }

                if (isHit)
                {
                    hitEnemies.Add(enemy);
                    Main.LogDebug($"[PointTargetingHelper]   ✓ HIT: {enemy.CharacterName} at ({enemy.Position.x:F1},{enemy.Position.z:F1})");
                }
            }

            return hitEnemies;
        }

        /// <summary>
        /// ★ v3.8.07: 게임 패스파인딩을 사용한 실제 Charge 경로의 적 목록
        /// Bresenham 직선이 아닌 게임의 실제 타일 기반 패스파인딩 사용
        /// </summary>
        public static List<BaseUnitEntity> GetEnemiesInChargePath(
            Vector3 p1Pos, Vector3 p2Pos,
            List<BaseUnitEntity> allEnemies,
            UnitMovementAgentBase agent,  // ★ 실제 패스파인딩용 Agent
            int casterSize = 1)
        {
            var hitEnemies = new List<BaseUnitEntity>();

            if (allEnemies == null || allEnemies.Count == 0)
                return hitEnemies;

            if (agent == null)
            {
                // Agent가 없으면 기존 Bresenham 방식 폴백
                Main.LogDebug("[PointTargetingHelper] GetEnemiesInChargePath: No agent, fallback to Bresenham");
                return GetEnemiesInChargePath(p1Pos, p2Pos, allEnemies, casterSize);
            }

            try
            {
                // ★ 게임의 실제 Charge 패스파인딩 사용
                var chargePath = PathfindingService.Instance
                    .FindPathChargeTB_Blocking(agent, p1Pos, p2Pos, true, null);

                if (chargePath?.path == null || chargePath.path.Count == 0)
                {
                    Main.LogDebug("[PointTargetingHelper] GetEnemiesInChargePath: Charge path failed, fallback to Bresenham");
                    return GetEnemiesInChargePath(p1Pos, p2Pos, allEnemies, casterSize);
                }

                // ★ 실제 경로 노드에서 좌표 추출
                var pathNodeCoords = new HashSet<(int x, int z)>();
                foreach (var graphNode in chargePath.path)
                {
                    var gridNode = graphNode as CustomGridNodeBase;
                    if (gridNode != null)
                    {
                        pathNodeCoords.Add((gridNode.XCoordinateInGrid, gridNode.ZCoordinateInGrid));

                        // casterSize > 1인 경우 추가 노드도 포함
                        if (casterSize > 1)
                        {
                            for (int ox = 0; ox < casterSize; ox++)
                            {
                                for (int oz = 0; oz < casterSize; oz++)
                                {
                                    if (ox == 0 && oz == 0) continue;
                                    pathNodeCoords.Add((gridNode.XCoordinateInGrid + ox, gridNode.ZCoordinateInGrid + oz));
                                }
                            }
                        }
                    }
                }

                var p1Node = p1Pos.GetNearestNodeXZ() as CustomGridNodeBase;
                var p2Node = p2Pos.GetNearestNodeXZ() as CustomGridNodeBase;
                Main.LogDebug($"[PointTargetingHelper] ActualChargePath from " +
                    $"({p1Node?.XCoordinateInGrid},{p1Node?.ZCoordinateInGrid}) to " +
                    $"({p2Node?.XCoordinateInGrid},{p2Node?.ZCoordinateInGrid}): " +
                    $"{chargePath.path.Count} nodes (actual pathfinding)");

                // ★ 각 적 유닛 체크
                foreach (var enemy in allEnemies)
                {
                    if (enemy == null || !enemy.IsConscious)
                        continue;

                    var enemyNodes = GetEnemyOccupiedNodeCoords(enemy);

                    bool isHit = false;
                    foreach (var enemyCoord in enemyNodes)
                    {
                        if (pathNodeCoords.Contains(enemyCoord))
                        {
                            isHit = true;
                            break;
                        }
                    }

                    if (isHit)
                    {
                        hitEnemies.Add(enemy);
                        Main.LogDebug($"[PointTargetingHelper]   ✓ HIT (actual path): {enemy.CharacterName}");
                    }
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[PointTargetingHelper] GetEnemiesInChargePath ERROR: {ex.Message}, fallback to Bresenham");
                return GetEnemiesInChargePath(p1Pos, p2Pos, allEnemies, casterSize);
            }

            return hitEnemies;
        }

        /// <summary>
        /// ★ v3.8.07: 게임 패스파인딩으로 경로상 적 수 카운트 (Agent 버전)
        /// </summary>
        public static int CountEnemiesInChargePath(
            Vector3 p1Pos, Vector3 p2Pos,
            List<BaseUnitEntity> allEnemies,
            UnitMovementAgentBase agent,
            int casterSize = 1)
        {
            return GetEnemiesInChargePath(p1Pos, p2Pos, allEnemies, agent, casterSize).Count;
        }

        /// <summary>
        /// ★ v3.7.61: Bresenham 알고리즘으로 P1→P2 경로의 모든 노드 수집
        /// 게임의 Linecast.Ray2NodeOffsets와 유사한 방식 + ContainsConnection 검증
        ///
        /// 핵심 변경: 노드 간 연결(ContainsConnection) 체크 추가
        /// - 벽/엄폐물이 있으면 연결이 끊어져 있음
        /// - 경로 중간에 장애물이 있으면 거기서 경로 종료
        /// </summary>
        private static List<CustomGridNodeBase> GetNodesAlongPath(
            CustomGridNodeBase startNode,
            CustomGridNodeBase endNode,
            int casterSize = 1)
        {
            var nodes = new List<CustomGridNodeBase>();

            if (startNode == null || endNode == null)
                return nodes;

            var graph = CustomGridNode.GetGridGraph(startNode.GraphIndex);
            if (graph == null)
                return nodes;

            int x0 = startNode.XCoordinateInGrid;
            int z0 = startNode.ZCoordinateInGrid;
            int x1 = endNode.XCoordinateInGrid;
            int z1 = endNode.ZCoordinateInGrid;

            // Bresenham's line algorithm
            int dx = Math.Abs(x1 - x0);
            int dz = Math.Abs(z1 - z0);
            int sx = x0 < x1 ? 1 : -1;
            int sz = z0 < z1 ? 1 : -1;
            int err = dx - dz;

            int x = x0;
            int z = z0;
            CustomGridNodeBase prevNode = null;

            while (true)
            {
                var node = graph.GetNode(x, z);
                if (node != null)
                {
                    // ★ v3.7.61: 이전 노드와 현재 노드 간 연결 체크
                    // ContainsConnection = 벽/엄폐물 없이 이동 가능한지
                    if (prevNode != null)
                    {
                        if (!prevNode.ContainsConnection(node))
                        {
                            // 장애물 발견! 경로 차단됨
                            Main.LogDebug($"[PointTargetingHelper] GetNodesAlongPath: Connection BLOCKED at ({prevNode.XCoordinateInGrid},{prevNode.ZCoordinateInGrid}) -> ({node.XCoordinateInGrid},{node.ZCoordinateInGrid})");
                            break;  // 여기서 경로 종료
                        }
                    }

                    nodes.Add(node);
                    prevNode = node;

                    // ★ casterSize > 1인 경우, 추가 노드도 경로에 포함
                    // (2x2 caster라면 인접 노드들도 추가)
                    if (casterSize > 1)
                    {
                        for (int ox = 0; ox < casterSize; ox++)
                        {
                            for (int oz = 0; oz < casterSize; oz++)
                            {
                                if (ox == 0 && oz == 0) continue;
                                var additionalNode = graph.GetNode(x + ox, z + oz);
                                if (additionalNode != null && !nodes.Contains(additionalNode))
                                {
                                    nodes.Add(additionalNode);
                                }
                            }
                        }
                    }
                }
                else
                {
                    // 노드 없음 (맵 밖) - 경로 중단
                    break;
                }

                if (x == x1 && z == z1)
                    break;

                int e2 = 2 * err;
                if (e2 > -dz)
                {
                    err -= dz;
                    x += sx;
                }
                if (e2 < dx)
                {
                    err += dx;
                    z += sz;
                }
            }

            return nodes;
        }

        /// <summary>
        /// ★ v3.7.59: 적 유닛이 점유하는 모든 노드 좌표 반환
        /// 2x2 적은 4개 노드, 1x1 적은 1개 노드
        /// </summary>
        private static List<(int x, int z)> GetEnemyOccupiedNodeCoords(BaseUnitEntity enemy)
        {
            var coords = new List<(int x, int z)>();

            if (enemy == null)
                return coords;

            var enemyNode = enemy.Position.GetNearestNodeXZ() as CustomGridNodeBase;
            if (enemyNode == null)
                return coords;

            IntRect sizeRect = enemy.SizeRect;
            int baseX = enemyNode.XCoordinateInGrid;
            int baseZ = enemyNode.ZCoordinateInGrid;

            // SizeRect 기반으로 점유 노드 계산
            // SizeRect는 (x, y, width-1, height-1) 형태
            for (int dx = sizeRect.xmin; dx <= sizeRect.xmax; dx++)
            {
                for (int dz = sizeRect.ymin; dz <= sizeRect.ymax; dz++)
                {
                    coords.Add((baseX + dx, baseZ + dz));
                }
            }

            return coords;
        }

        /// <summary>
        /// ★ v3.7.59: P1 → P2 경로의 적 수 카운트
        /// </summary>
        public static int CountEnemiesInChargePath(
            Vector3 p1Pos, Vector3 p2Pos,
            List<BaseUnitEntity> allEnemies,
            int casterSize = 1)
        {
            return GetEnemiesInChargePath(p1Pos, p2Pos, allEnemies, casterSize).Count;
        }

        /// <summary>
        /// ★ v3.7.61: 두 지점 간 직선 경로가 장애물 없이 통과 가능한지 확인
        /// ContainsConnection 체크로 벽/엄폐물 감지
        /// </summary>
        public static bool IsPathClear(Vector3 from, Vector3 to)
        {
            var fromNode = from.GetNearestNodeXZ() as CustomGridNodeBase;
            var toNode = to.GetNearestNodeXZ() as CustomGridNodeBase;

            if (fromNode == null || toNode == null)
                return false;

            return IsPathClear(fromNode, toNode);
        }

        /// <summary>
        /// ★ v3.7.61: 두 노드 간 직선 경로가 장애물 없이 통과 가능한지 확인
        /// </summary>
        public static bool IsPathClear(CustomGridNodeBase fromNode, CustomGridNodeBase toNode)
        {
            if (fromNode == null || toNode == null)
                return false;

            var graph = CustomGridNode.GetGridGraph(fromNode.GraphIndex);
            if (graph == null)
                return false;

            int x0 = fromNode.XCoordinateInGrid;
            int z0 = fromNode.ZCoordinateInGrid;
            int x1 = toNode.XCoordinateInGrid;
            int z1 = toNode.ZCoordinateInGrid;

            // Bresenham's line algorithm with connection check
            int dx = Math.Abs(x1 - x0);
            int dz = Math.Abs(z1 - z0);
            int sx = x0 < x1 ? 1 : -1;
            int sz = z0 < z1 ? 1 : -1;
            int err = dx - dz;

            int x = x0;
            int z = z0;
            CustomGridNodeBase prevNode = null;

            while (true)
            {
                var node = graph.GetNode(x, z);
                if (node == null)
                    return false;  // 맵 밖 - 경로 불가

                // ★ 노드 간 연결 체크
                if (prevNode != null)
                {
                    if (!prevNode.ContainsConnection(node))
                    {
                        Main.LogDebug($"[PointTargetingHelper] IsPathClear: BLOCKED at ({prevNode.XCoordinateInGrid},{prevNode.ZCoordinateInGrid}) -> ({node.XCoordinateInGrid},{node.ZCoordinateInGrid})");
                        return false;  // 장애물!
                    }
                }

                prevNode = node;

                if (x == x1 && z == z1)
                    break;

                int e2 = 2 * err;
                if (e2 > -dz)
                {
                    err -= dz;
                    x += sx;
                }
                if (e2 < dx)
                {
                    err += dx;
                    z += sz;
                }
            }

            return true;  // 장애물 없음
        }

        /// <summary>
        /// 단일 셀 유효성 검증 (개별 체크용)
        /// ★ v3.7.66: BattlefieldGrid 캐시 사용
        /// </summary>
        public static bool IsValidLandingCell(
            CustomGridNodeBase originNode,
            CustomGridNodeBase targetNode)
        {
            if (originNode == null || targetNode == null)
                return false;

            var controller = Game.Instance?.CustomGridNodeController;
            if (controller == null)
                return false;

            // ★ v3.7.66: 1. Walkable (BattlefieldGrid 캐시 우선)
            var grid = Analysis.BattlefieldGrid.Instance;
            if (grid != null && grid.IsValid)
            {
                if (!grid.IsWalkable(targetNode))
                    return false;
            }
            else if (!targetNode.Walkable)
            {
                return false;
            }

            // 2. Unoccupied
            if (controller.ContainsUnit(targetNode))
                return false;

            // 3. LOS
            if (!LosCalculations.HasLos(
                    originNode, new IntRect(0, 0, 0, 0),
                    targetNode, new IntRect(0, 0, 0, 0)))
                return false;

            return true;
        }

        /// <summary>
        /// 유효한 셀 중 가장 먼 셀 선택
        /// </summary>
        private static CustomGridNodeBase GetFarthestCell(
            CustomGridNodeBase originNode,
            List<CustomGridNodeBase> validCells)
        {
            if (validCells == null || validCells.Count == 0)
                return null;

            CustomGridNodeBase farthest = null;
            float maxDist = 0f;

            Vector3 originPos = (Vector3)originNode.Vector3Position;

            foreach (var cell in validCells)
            {
                float dist = Vector3.Distance(originPos, (Vector3)cell.Vector3Position);
                if (dist > maxDist)
                {
                    maxDist = dist;
                    farthest = cell;
                }
            }

            return farthest;
        }

        /// <summary>
        /// Warhammer 거리 계산 (타일 단위)
        /// max(|dx|, |dz|) + min(|dx|, |dz|) / 2
        /// </summary>
        public static int GetWarhammerDistance(int dx, int dz)
        {
            int absDx = Math.Abs(dx);
            int absDz = Math.Abs(dz);
            return Math.Max(absDx, absDz) + Math.Min(absDx, absDz) / 2;
        }

        /// <summary>
        /// 두 노드 간 Warhammer 거리 계산
        /// </summary>
        public static int GetWarhammerDistance(CustomGridNodeBase from, CustomGridNodeBase to)
        {
            if (from == null || to == null)
                return int.MaxValue;

            int dx = to.XCoordinateInGrid - from.XCoordinateInGrid;
            int dz = to.ZCoordinateInGrid - from.ZCoordinateInGrid;
            return GetWarhammerDistance(dx, dz);
        }

        /// <summary>
        /// ★ v3.7.55: Aerial Rush 경로 완전 재작성
        ///
        /// 실제 게임 동작:
        /// - Point1 = Eagle이 돌진하며 적을 타격하는 지점 (적 위치 또는 근처)
        /// - Point2 = Eagle이 최종 착륙하는 지점
        /// - 경로: Eagle.Position → Point1(적 타격) → Point2(착륙)
        ///
        /// 검증 조건:
        /// 1. Master → Point1: LOS 필요
        /// 2. Eagle → Point1: Charge 경로 유효
        /// 3. Point1 → Point2: Support 능력 RangeCells 내
        /// </summary>
        public static bool FindBestAerialRushPath(
            CustomGridNodeBase masterNode,
            IntRect masterSizeRect,
            int point1RangeTiles,  // Master → Point1 거리 체크용 (LOS 검증)
            int point2RangeTiles,  // Point1 → Point2 거리 (Support 능력 RangeCells)
            List<BaseUnitEntity> enemies,
            out CustomGridNodeBase bestPoint1,
            out CustomGridNodeBase bestPoint2,
            CustomGridNodeBase eagleNode = null,  // Eagle 현재 위치
            BaseUnitEntity eagle = null)  // Charge 경로 검증용
        {
            bestPoint1 = null;
            bestPoint2 = null;

            if (masterNode == null || enemies == null || enemies.Count == 0)
            {
                Main.LogDebug("[PointTargetingHelper] FindBestPath: Invalid input");
                return false;
            }

            var controller = Game.Instance?.CustomGridNodeController;
            if (controller == null)
                return false;

            var graph = CustomGridNode.GetGridGraph(masterNode.GraphIndex);
            if (graph == null)
                return false;

            // Eagle 현재 위치 (없으면 Master 기준 폴백)
            CustomGridNodeBase eagleBaseNode = eagleNode ?? masterNode;
            Vector3 eaglePos = (Vector3)eagleBaseNode.Vector3Position;
            Vector3 masterPos = (Vector3)masterNode.Vector3Position;
            float baseY = eaglePos.y;

            const float TILE_SIZE = 1.35f;
            const float MAX_HEIGHT_DIFF = 2.5f;

            Main.LogDebug($"[PointTargetingHelper] FindBestPath: Master at ({masterPos.x:F1},{masterPos.z:F1}), " +
                $"Eagle at ({eaglePos.x:F1},{eaglePos.z:F1}), " +
                $"P1Range={point1RangeTiles}, P2Range={point2RangeTiles} tiles, Enemies={enemies.Count}");

            // 후보: (Point1, Point2, 적수, 경로길이, 타겟이름)
            var candidates = new List<(CustomGridNodeBase p1, CustomGridNodeBase p2, int enemies, float totalLen, string enemyName)>();
            int candidatesChecked = 0;

            // ★ v3.7.55: 각 적에 대해 Point1 = 적 근처 빈 타일, Point2 = 적 뒤 착륙 지점
            foreach (var enemy in enemies)
            {
                if (enemy == null || !enemy.IsConscious)
                    continue;

                Vector3 enemyPos = enemy.Position;

                // 높이 차이 체크
                if (Math.Abs(enemyPos.y - baseY) > MAX_HEIGHT_DIFF * 2)
                    continue;

                // Eagle → 적 방향
                Vector2 eagleToEnemy2D = new Vector2(enemyPos.x - eaglePos.x, enemyPos.z - eaglePos.z);
                float distEagleToEnemy = eagleToEnemy2D.magnitude;
                if (distEagleToEnemy < TILE_SIZE * 2)  // 너무 가까우면 스킵
                    continue;

                Vector2 direction2D = eagleToEnemy2D.normalized;

                // ★ Point1 = 적 바로 앞 빈 타일 (Eagle이 여기까지 돌진하며 적 타격)
                // 적보다 1~2 타일 앞 위치 탐색
                float[] p1Offsets = { -1f, -2f, 0f };  // 적 기준 Eagle 방향으로 1~2타일 앞, 또는 적 위치

                foreach (float p1Offset in p1Offsets)
                {
                    float p1DistFromEnemy = p1Offset * TILE_SIZE;
                    Vector3 idealP1 = new Vector3(
                        enemyPos.x - direction2D.x * (-p1DistFromEnemy),  // 적에서 Eagle 방향으로 offset
                        baseY,
                        enemyPos.z - direction2D.y * (-p1DistFromEnemy));

                    var point1Node = idealP1.GetNearestNodeXZ() as CustomGridNodeBase;
                    if (point1Node == null)
                        continue;

                    Vector3 p1Pos = (Vector3)point1Node.Vector3Position;

                    // Point1은 빈 타일이어야 함 (적이 점유하지 않음)
                    if (controller.ContainsUnit(point1Node))
                        continue;

                    if (!point1Node.Walkable)
                        continue;

                    // ★ v3.7.63: BattlefieldGrid 검증 (캐시 기반 추가 체크)
                    if (Analysis.BattlefieldGrid.Instance.IsValid &&
                        !Analysis.BattlefieldGrid.Instance.IsWalkable(point1Node))
                        continue;

                    // Master → Point1 거리 체크
                    float masterToP1Dist = Vector3.Distance(masterPos, p1Pos) / TILE_SIZE;
                    if (masterToP1Dist > point1RangeTiles)
                        continue;

                    // Master → Point1 LOS 체크
                    if (!LosCalculations.HasLos(masterNode, masterSizeRect, point1Node, new IntRect(0, 0, 0, 0)))
                        continue;

                    // ★ v3.7.60: Point2 = 적을 지나친 후 착륙 지점 (적 뒤로 2~11 타일)
                    // 사용자 피드백: Point2 범위는 약 11-12타일
                    float[] landingDistances = { 2f, 4f, 6f, 8f, 10f, 11f };

                    foreach (float landingDist in landingDistances)
                    {
                        // Point2 = 적 위치에서 direction 방향으로 landingDist 타일
                        float landingMeters = landingDist * TILE_SIZE;

                        Vector3 idealP2 = new Vector3(
                            enemyPos.x + direction2D.x * landingMeters,
                            baseY,
                            enemyPos.z + direction2D.y * landingMeters);

                        var point2Node = idealP2.GetNearestNodeXZ() as CustomGridNodeBase;
                        if (point2Node == null)
                            continue;

                        candidatesChecked++;

                        // Point2 유효성 검사
                        if (!point2Node.Walkable)
                            continue;

                        if (controller.ContainsUnit(point2Node))
                            continue;

                        // ★ v3.7.63: BattlefieldGrid 검증 (캐시 기반 추가 체크)
                        if (Analysis.BattlefieldGrid.Instance.IsValid &&
                            !Analysis.BattlefieldGrid.Instance.IsWalkable(point2Node))
                            continue;

                        float p2Y = ((Vector3)point2Node.Vector3Position).y;
                        if (Math.Abs(p2Y - baseY) > MAX_HEIGHT_DIFF)
                            continue;

                        // Point1 → Point2 LOS 체크
                        if (!LosCalculations.HasLos(point1Node, new IntRect(0, 0, 0, 0),
                                                    point2Node, new IntRect(0, 0, 0, 0)))
                            continue;

                        // ★ v3.7.61: Point1 → Point2 노드 연결 체크 (장애물/엄폐물 확인)
                        if (!IsPathClear(point1Node, point2Node))
                        {
                            Main.LogDebug($"[PointTargetingHelper] P1→P2 path BLOCKED by obstacle: ({point1Node.XCoordinateInGrid},{point1Node.ZCoordinateInGrid}) -> ({point2Node.XCoordinateInGrid},{point2Node.ZCoordinateInGrid})");
                            continue;
                        }

                        // Point1 → Point2 실제 거리 확인 (Support RangeCells 이내)
                        Vector3 p2Pos = (Vector3)point2Node.Vector3Position;
                        float p1ToP2Dist = Vector3.Distance(p1Pos, p2Pos) / TILE_SIZE;
                        if (p1ToP2Dist > point2RangeTiles)
                            continue;

                        // ★ v3.7.58: P1 → P2 경로에서 적 타격
                        // Eagle이 P1에서 소환되어 P2로 이동하면서 경로상 적 공격
                        int enemyCount = CountEnemiesInChargePath(p1Pos, p2Pos, enemies);
                        if (enemyCount <= 0)
                            continue;  // 경로에 적이 없으면 스킵

                        float totalLen = Vector3.Distance(p1Pos, p2Pos);  // P1→P2 거리만

                        candidates.Add((point1Node, point2Node, enemyCount, totalLen, enemy.CharacterName));
                    }
                }
            }

            Main.LogDebug($"[PointTargetingHelper] FindBestPath: Checked {candidatesChecked} candidates, Valid={candidates.Count}");

            // 적 수 > 경로 길이 순 정렬
            candidates.Sort((a, b) =>
            {
                if (a.enemies != b.enemies) return b.enemies.CompareTo(a.enemies);
                return b.totalLen.CompareTo(a.totalLen);
            });

            // ★ Eagle → Point1 Charge 경로 검증
            var eagleAgent = eagle?.MaybeMovementAgent;
            Main.LogDebug($"[PointTargetingHelper] Charge validation: eagleAgent={(eagleAgent != null ? "Valid" : "NULL")}, candidates={candidates.Count}");

            foreach (var candidate in candidates)
            {
                Vector3 p1 = (Vector3)candidate.p1.Vector3Position;
                Vector3 p2 = (Vector3)candidate.p2.Vector3Position;

                // ★ Eagle.Position → Point1 Charge 경로 검증
                if (eagleAgent != null)
                {
                    try
                    {
                        // Eagle 현재 위치에서 Point1(적 위치)로 돌진 경로
                        var chargePath = PathfindingService.Instance
                            .FindPathChargeTB_Blocking(eagleAgent, eaglePos, p1, true, null);
                        bool hasValidPath = chargePath?.path != null && chargePath.path.Count > 0;

                        if (!hasValidPath)
                        {
                            Main.LogDebug($"[PointTargetingHelper] Eagle→P1 Charge FAILED: " +
                                $"Eagle({eaglePos.x:F1},{eaglePos.z:F1}) -> P1({p1.x:F1},{p1.z:F1}) toward {candidate.enemyName}");
                            continue;
                        }

                        // ★ v3.7.60: Point1 → Point2 착륙 경로 검증 (적을 통과함!)
                        // Eagle은 P1→P2 이동 중 적을 타격하므로 적 통과 필요
                        var landingPath = PathfindingService.Instance
                            .FindPathChargeTB_Blocking(eagleAgent, p1, p2, true, null);  // true = 적 통과 가능
                        bool hasLandingPath = landingPath?.path != null && landingPath.path.Count > 0;

                        if (!hasLandingPath)
                        {
                            Main.LogDebug($"[PointTargetingHelper] P1→P2 Landing FAILED: " +
                                $"P1({p1.x:F1},{p1.z:F1}) -> P2({p2.x:F1},{p2.z:F1})");
                            continue;
                        }

                        // ★ v3.8.07: 실제 경로 기반 적 수 재계산
                        var actualHitEnemies = GetEnemiesInChargePath(p1, p2, enemies, eagleAgent);
                        int actualEnemyCount = actualHitEnemies.Count;

                        if (actualEnemyCount <= 0)
                        {
                            Main.LogDebug($"[PointTargetingHelper] P1→P2 ActualPath has NO ENEMIES: " +
                                $"P1({p1.x:F1},{p1.z:F1}) -> P2({p2.x:F1},{p2.z:F1}) (Bresenham={candidate.enemies})");
                            continue;
                        }

                        Main.LogDebug($"[PointTargetingHelper] Path VALID: " +
                            $"Eagle({eaglePos.x:F1},{eaglePos.z:F1}) -> P1({p1.x:F1},{p1.z:F1}) -> P2({p2.x:F1},{p2.z:F1}), " +
                            $"ActualEnemies={actualEnemyCount} (Bresenham={candidate.enemies})");
                    }
                    catch (Exception ex)
                    {
                        Main.LogDebug($"[PointTargetingHelper] Charge path ERROR: {ex.Message}");
                        continue;
                    }
                }

                bestPoint1 = candidate.p1;
                bestPoint2 = candidate.p2;

                // ★ v3.8.07: P1 → P2 경로의 타격 대상 (실제 패스파인딩 사용)
                var hitEnemies = eagleAgent != null
                    ? GetEnemiesInChargePath(p1, p2, enemies, eagleAgent)
                    : GetEnemiesInChargePath(p1, p2, enemies);
                var hitNames = string.Join(", ", hitEnemies.ConvertAll(e => e.CharacterName));

                Main.LogDebug($"[PointTargetingHelper] FindBestPath: SUCCESS - " +
                    $"P1({p1.x:F1},{p1.y:F2},{p1.z:F1}) -> P2({p2.x:F1},{p2.y:F2},{p2.z:F1}), " +
                    $"Enemies={candidate.enemies}, Target={candidate.enemyName}");
                Main.LogDebug($"[PointTargetingHelper] ★ 타격 대상 목록: [{hitNames}]");
                return true;
            }

            Main.LogDebug("[PointTargetingHelper] FindBestPath: No valid path found");
            return false;
        }

        /// <summary>
        /// ★ v3.7.45: Master가 이동할 수 있는 최적의 위치 찾기 (Aerial Rush 사용 가능 위치)
        ///
        /// Overseer 아키타입의 핵심: 사역마 활용이 메인이므로 이동해서라도 사용해야 함
        /// </summary>
        /// <param name="masterNode">현재 Master 위치</param>
        /// <param name="masterSizeRect">Master 크기</param>
        /// <param name="masterMPTiles">Master 이동 가능 타일 수</param>
        /// <param name="point1RangeTiles">능력 Point1 사거리</param>
        /// <param name="point2RangeTiles">Eagle 이동 범위</param>
        /// <param name="enemies">적 목록</param>
        /// <param name="bestMasterPos">출력: Master가 이동할 위치</param>
        /// <param name="bestPoint1">출력: 최적 Point1</param>
        /// <param name="bestPoint2">출력: 최적 Point2</param>
        /// <param name="eagleNode">★ v3.7.48: Eagle 현재 위치 (null이면 Master 기준)</param>
        /// <returns>성공 여부</returns>
        public static bool FindBestMasterPositionForAerialRush(
            CustomGridNodeBase masterNode,
            IntRect masterSizeRect,
            int masterMPTiles,
            int point1RangeTiles,
            int point2RangeTiles,
            List<BaseUnitEntity> enemies,
            out CustomGridNodeBase bestMasterPos,
            out CustomGridNodeBase bestPoint1,
            out CustomGridNodeBase bestPoint2,
            CustomGridNodeBase eagleNode = null,  // ★ v3.7.48: Eagle 위치
            BaseUnitEntity eagle = null)  // ★ v3.7.50: Charge 경로 검증용
        {
            bestMasterPos = null;
            bestPoint1 = null;
            bestPoint2 = null;

            if (masterNode == null || enemies == null || enemies.Count == 0)
                return false;

            var controller = Game.Instance?.CustomGridNodeController;
            if (controller == null)
                return false;

            var graph = CustomGridNode.GetGridGraph(masterNode.GraphIndex);
            if (graph == null)
                return false;

            int masterCX = masterNode.XCoordinateInGrid;
            int masterCZ = masterNode.ZCoordinateInGrid;
            float masterY = ((Vector3)masterNode.Vector3Position).y;

            int bestEnemyCount = 0;
            float bestPathLength = 0f;
            int bestMasterDistance = int.MaxValue;  // 가까운 이동 위치 선호

            int masterPosCandidates = 0;

            Main.LogDebug($"[PointTargetingHelper] FindBestMasterPos: Master MP={masterMPTiles} tiles, " +
                $"P1 range={point1RangeTiles}, P2 range={point2RangeTiles}");

            // Master 이동 가능한 모든 위치 탐색
            for (int mdx = -masterMPTiles; mdx <= masterMPTiles; mdx++)
            {
                for (int mdz = -masterMPTiles; mdz <= masterMPTiles; mdz++)
                {
                    int masterDist = GetWarhammerDistance(mdx, mdz);
                    if (masterDist > masterMPTiles)
                        continue;

                    var newMasterNode = graph.GetNode(masterCX + mdx, masterCZ + mdz);
                    if (newMasterNode == null || !newMasterNode.Walkable)
                        continue;

                    // Master 높이 체크
                    float newMasterY = ((Vector3)newMasterNode.Vector3Position).y;
                    if (Math.Abs(newMasterY - masterY) > 3.0f)
                        continue;

                    // Master 이동 위치 점유 체크 (자신 위치 제외)
                    if (masterDist > 0 && controller.ContainsUnit(newMasterNode))
                        continue;

                    masterPosCandidates++;

                    // ★ v3.7.48: 이 위치에서 Aerial Rush 경로 찾기 (Eagle 위치 전달)
                    CustomGridNodeBase tempP1, tempP2;
                    if (FindBestAerialRushPath(newMasterNode, masterSizeRect, point1RangeTiles, point2RangeTiles,
                        enemies, out tempP1, out tempP2, eagleNode, eagle))  // ★ v3.7.50: eagle 전달
                    {
                        Vector3 p1Pos = (Vector3)tempP1.Vector3Position;
                        Vector3 p2Pos = (Vector3)tempP2.Vector3Position;
                        // ★ v3.8.07: P1 → P2 경로에서 적 타격 (실제 패스파인딩 사용)
                        var eagleAgent = eagle?.MaybeMovementAgent;
                        int enemyCount = eagleAgent != null
                            ? CountEnemiesInChargePath(p1Pos, p2Pos, enemies, eagleAgent)
                            : CountEnemiesInChargePath(p1Pos, p2Pos, enemies);
                        float pathLength = Vector3.Distance(p1Pos, p2Pos);

                        // 최적 선택: 1) 적 수 > 2) 경로 길이 > 3) Master 이동 거리 짧은 것
                        bool isBetter = false;
                        if (enemyCount > bestEnemyCount)
                            isBetter = true;
                        else if (enemyCount == bestEnemyCount && pathLength > bestPathLength)
                            isBetter = true;
                        else if (enemyCount == bestEnemyCount && Math.Abs(pathLength - bestPathLength) < 0.5f && masterDist < bestMasterDistance)
                            isBetter = true;

                        if (isBetter)
                        {
                            bestEnemyCount = enemyCount;
                            bestPathLength = pathLength;
                            bestMasterDistance = masterDist;
                            bestMasterPos = newMasterNode;
                            bestPoint1 = tempP1;
                            bestPoint2 = tempP2;
                        }
                    }
                }
            }

            Main.LogDebug($"[PointTargetingHelper] FindBestMasterPos: Checked {masterPosCandidates} positions, " +
                $"BestEnemies={bestEnemyCount}, BestMasterDist={bestMasterDistance}");

            if (bestMasterPos != null && bestPoint1 != null && bestPoint2 != null && bestEnemyCount > 0)
            {
                Vector3 mPos = (Vector3)bestMasterPos.Vector3Position;
                Vector3 p1 = (Vector3)bestPoint1.Vector3Position;
                Vector3 p2 = (Vector3)bestPoint2.Vector3Position;
                Main.LogDebug($"[PointTargetingHelper] FindBestMasterPos: SUCCESS - " +
                    $"Master moves to ({mPos.x:F1},{mPos.z:F1}) dist={bestMasterDistance} tiles, " +
                    $"then Point1({p1.x:F1},{p1.z:F1}) -> Point2({p2.x:F1},{p2.z:F1}), Enemies={bestEnemyCount}");
                return true;
            }

            return false;
        }
    }
}
