using System;
using System.Collections.Generic;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Pathfinding;
using UnityEngine;

namespace CompanionAI_v3.Analysis
{
    /// <summary>
    /// ★ v3.7.62: 전장 그리드 - 게임의 실제 맵 구조 활용
    ///
    /// 기존 InfluenceMap의 문제점:
    /// - 실제 맵 구조(벽, 구조물, 장애물) 전혀 고려 안함
    /// - 거리 기반으로만 위협도/엄폐 계산
    /// - 이동 불가능한 영역도 점수 부여 → 이동 실패 발생
    ///
    /// 해결책: 게임의 실제 그리드 시스템 활용
    /// - CustomGridGraph: 전체 맵 노드 배열
    /// - CustomGridNode.Walkable: 이동 가능 여부 (정적 캐싱)
    /// - ContainsConnection: 노드 간 연결 (정적 캐싱)
    /// - CustomGridNodeController: 유닛 점유 (실시간 조회)
    /// </summary>
    public class BattlefieldGrid
    {
        #region Singleton

        private static BattlefieldGrid _instance;
        public static BattlefieldGrid Instance => _instance ??= new BattlefieldGrid();

        #endregion

        #region Constants

        /// <summary>전투 영역 패딩 (타일 단위) - ★ v3.7.68: 40으로 증가</summary>
        private const int COMBAT_PADDING = 40;

        /// <summary>최대 캐시 크기 (한 방향) - ★ v3.7.68: 250으로 증가</summary>
        private const int MAX_CACHE_SIZE = 250;

        /// <summary>캐시 재구성 트리거 거리 (타일) - 유닛이 경계에서 이만큼 가까우면 확장</summary>
        private const int EXPAND_THRESHOLD = 10;

        #endregion

        #region Grid Data

        /// <summary>게임 그리드 그래프 참조</summary>
        private CustomGridGraph _gridGraph;

        /// <summary>그리드 전체 크기</summary>
        private int _graphWidth;
        private int _graphDepth;

        /// <summary>캐시 영역 경계 (전투 영역 + 패딩)</summary>
        private int _minX, _maxX, _minZ, _maxZ;

        /// <summary>캐시 영역 크기</summary>
        private int _cacheWidth, _cacheDepth;

        /// <summary>Walkable 캐시 (정적 - 전투 시작 시 1회 계산)</summary>
        private bool[,] _walkableCache;

        /// <summary>Connection 캐시 [x, z, direction(0-7)] (정적)</summary>
        private bool[,,] _connectionCache;

        /// <summary>그리드 유효 여부</summary>
        private bool _isValid;

        #endregion

        #region Initialization

        /// <summary>
        /// 전투 시작 시 그리드 초기화
        /// TurnEventHandler.HandleTurnBasedModeSwitched(true)에서 호출
        /// </summary>
        public void InitializeFromCombat(List<BaseUnitEntity> allUnits)
        {
            try
            {
                var startTime = DateTime.Now;

                // 1. 게임 그리드 그래프 가져오기
                if (!TryGetGridGraph(out _gridGraph))
                {
                    Main.LogError("[BattlefieldGrid] Failed to get grid graph");
                    _isValid = false;
                    return;
                }

                _graphWidth = _gridGraph.width;
                _graphDepth = _gridGraph.depth;

                // 2. 전투 영역 경계 계산 (유닛 위치 + 패딩)
                CalculateCombatBounds(allUnits);

                // 3. 캐시 크기 계산
                _cacheWidth = Math.Min(_maxX - _minX + 1, MAX_CACHE_SIZE);
                _cacheDepth = Math.Min(_maxZ - _minZ + 1, MAX_CACHE_SIZE);

                // 4. 캐시 배열 할당
                _walkableCache = new bool[_cacheWidth, _cacheDepth];
                _connectionCache = new bool[_cacheWidth, _cacheDepth, 8];

                // 5. Walkable 및 Connection 캐싱
                CacheGridData();

                _isValid = true;

                var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                Main.Log($"[BattlefieldGrid] Initialized: {_cacheWidth}x{_cacheDepth} " +
                    $"(bounds: {_minX},{_minZ} to {_maxX},{_maxZ}) in {elapsed:F1}ms");
            }
            catch (Exception ex)
            {
                Main.LogError($"[BattlefieldGrid] Initialize failed: {ex.Message}");
                _isValid = false;
            }
        }

        /// <summary>
        /// 게임의 CustomGridGraph 가져오기
        /// </summary>
        private bool TryGetGridGraph(out CustomGridGraph graph)
        {
            graph = null;

            try
            {
                var astar = AstarPath.active;
                if (astar == null || astar.data == null || astar.data.graphs == null)
                    return false;

                if (astar.data.graphs.Length == 0)
                    return false;

                // 첫 번째 그래프가 CustomGridGraph인지 확인
                graph = astar.data.graphs[0] as CustomGridGraph;
                if (graph == null)
                {
                    // 다른 인덱스에서 찾기
                    foreach (var g in astar.data.graphs)
                    {
                        graph = g as CustomGridGraph;
                        if (graph != null)
                            break;
                    }
                }

                return graph != null;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[BattlefieldGrid] TryGetGridGraph error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 전투 영역 경계 계산 (모든 유닛 위치 기반)
        /// </summary>
        private void CalculateCombatBounds(List<BaseUnitEntity> allUnits)
        {
            _minX = int.MaxValue;
            _maxX = int.MinValue;
            _minZ = int.MaxValue;
            _maxZ = int.MinValue;

            if (allUnits != null && allUnits.Count > 0)
            {
                foreach (var unit in allUnits)
                {
                    if (unit == null) continue;

                    var node = unit.Position.GetNearestNodeXZ() as CustomGridNodeBase;
                    if (node == null) continue;

                    int x = node.XCoordinateInGrid;
                    int z = node.ZCoordinateInGrid;

                    _minX = Math.Min(_minX, x);
                    _maxX = Math.Max(_maxX, x);
                    _minZ = Math.Min(_minZ, z);
                    _maxZ = Math.Max(_maxZ, z);
                }
            }

            // 유효한 경계가 없으면 전체 맵 사용
            if (_minX == int.MaxValue)
            {
                _minX = 0;
                _maxX = _graphWidth - 1;
                _minZ = 0;
                _maxZ = _graphDepth - 1;
            }

            // 패딩 추가
            _minX = Math.Max(0, _minX - COMBAT_PADDING);
            _maxX = Math.Min(_graphWidth - 1, _maxX + COMBAT_PADDING);
            _minZ = Math.Max(0, _minZ - COMBAT_PADDING);
            _maxZ = Math.Min(_graphDepth - 1, _maxZ + COMBAT_PADDING);
        }

        /// <summary>
        /// Walkable 및 Connection 데이터 캐싱
        /// </summary>
        private void CacheGridData()
        {
            int cachedNodes = 0;
            int walkableNodes = 0;

            for (int x = 0; x < _cacheWidth; x++)
            {
                for (int z = 0; z < _cacheDepth; z++)
                {
                    int graphX = _minX + x;
                    int graphZ = _minZ + z;

                    var node = _gridGraph.GetNode(graphX, graphZ);
                    if (node != null)
                    {
                        cachedNodes++;

                        // Walkable 캐싱
                        _walkableCache[x, z] = node.Walkable;
                        if (node.Walkable) walkableNodes++;

                        // 8방향 Connection 캐싱
                        for (int dir = 0; dir < 8; dir++)
                        {
                            _connectionCache[x, z, dir] = node.HasConnectionInDirection(dir);
                        }
                    }
                    else
                    {
                        // 노드 없음 = 이동 불가
                        _walkableCache[x, z] = false;
                        for (int dir = 0; dir < 8; dir++)
                        {
                            _connectionCache[x, z, dir] = false;
                        }
                    }
                }
            }

            Main.LogDebug($"[BattlefieldGrid] Cached {cachedNodes} nodes, {walkableNodes} walkable");
        }

        /// <summary>
        /// 전투 종료 시 캐시 정리
        /// </summary>
        public void Clear()
        {
            _walkableCache = null;
            _connectionCache = null;
            _gridGraph = null;
            _isValid = false;
            Main.LogDebug("[BattlefieldGrid] Cleared");
        }

        /// <summary>
        /// ★ v3.7.68: 유닛 위치 기반으로 캐시 영역 확장 필요 여부 확인
        /// </summary>
        public bool NeedsExpansion(List<BaseUnitEntity> allUnits)
        {
            if (!_isValid || allUnits == null) return false;

            foreach (var unit in allUnits)
            {
                if (unit == null || !unit.IsConscious) continue;

                var node = unit.Position.GetNearestNodeXZ() as CustomGridNodeBase;
                if (node == null) continue;

                int x = node.XCoordinateInGrid;
                int z = node.ZCoordinateInGrid;

                // 경계에서 EXPAND_THRESHOLD 이내면 확장 필요
                if (x < _minX + EXPAND_THRESHOLD || x > _maxX - EXPAND_THRESHOLD ||
                    z < _minZ + EXPAND_THRESHOLD || z > _maxZ - EXPAND_THRESHOLD)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// ★ v3.7.68: 유닛 목록 기반으로 캐시 영역 동적 확장
        /// TurnOrchestrator에서 턴 시작 시 호출 권장
        /// </summary>
        public void ExpandIfNeeded(List<BaseUnitEntity> allUnits)
        {
            if (!_isValid || allUnits == null) return;

            if (!NeedsExpansion(allUnits))
                return;

            var startTime = DateTime.Now;

            // 새 경계 계산
            int newMinX = _minX, newMaxX = _maxX;
            int newMinZ = _minZ, newMaxZ = _maxZ;

            foreach (var unit in allUnits)
            {
                if (unit == null) continue;

                var node = unit.Position.GetNearestNodeXZ() as CustomGridNodeBase;
                if (node == null) continue;

                int x = node.XCoordinateInGrid;
                int z = node.ZCoordinateInGrid;

                // 패딩 포함하여 경계 확장
                newMinX = Math.Min(newMinX, x - COMBAT_PADDING);
                newMaxX = Math.Max(newMaxX, x + COMBAT_PADDING);
                newMinZ = Math.Min(newMinZ, z - COMBAT_PADDING);
                newMaxZ = Math.Max(newMaxZ, z + COMBAT_PADDING);
            }

            // 그래프 경계 제한
            newMinX = Math.Max(0, newMinX);
            newMaxX = Math.Min(_graphWidth - 1, newMaxX);
            newMinZ = Math.Max(0, newMinZ);
            newMaxZ = Math.Min(_graphDepth - 1, newMaxZ);

            // 경계가 변경되지 않았으면 리턴
            if (newMinX >= _minX && newMaxX <= _maxX && newMinZ >= _minZ && newMaxZ <= _maxZ)
                return;

            // 새 캐시 크기 계산
            int newCacheWidth = Math.Min(newMaxX - newMinX + 1, MAX_CACHE_SIZE);
            int newCacheDepth = Math.Min(newMaxZ - newMinZ + 1, MAX_CACHE_SIZE);

            // 새 캐시 배열 생성 및 데이터 복사/확장
            var newWalkableCache = new bool[newCacheWidth, newCacheDepth];
            var newConnectionCache = new bool[newCacheWidth, newCacheDepth, 8];

            int copiedNodes = 0;
            int newNodes = 0;

            for (int x = 0; x < newCacheWidth; x++)
            {
                for (int z = 0; z < newCacheDepth; z++)
                {
                    int graphX = newMinX + x;
                    int graphZ = newMinZ + z;

                    // 기존 캐시에 있으면 복사
                    if (GridToCacheIndex(graphX, graphZ, out int oldCacheX, out int oldCacheZ))
                    {
                        newWalkableCache[x, z] = _walkableCache[oldCacheX, oldCacheZ];
                        for (int dir = 0; dir < 8; dir++)
                            newConnectionCache[x, z, dir] = _connectionCache[oldCacheX, oldCacheZ, dir];
                        copiedNodes++;
                    }
                    else
                    {
                        // 새로 캐싱
                        var node = _gridGraph.GetNode(graphX, graphZ);
                        if (node != null)
                        {
                            newWalkableCache[x, z] = node.Walkable;
                            for (int dir = 0; dir < 8; dir++)
                                newConnectionCache[x, z, dir] = node.HasConnectionInDirection(dir);
                            newNodes++;
                        }
                    }
                }
            }

            // 새 캐시로 교체
            _walkableCache = newWalkableCache;
            _connectionCache = newConnectionCache;
            _minX = newMinX;
            _maxX = newMaxX;
            _minZ = newMinZ;
            _maxZ = newMaxZ;
            _cacheWidth = newCacheWidth;
            _cacheDepth = newCacheDepth;

            var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
            Main.Log($"[BattlefieldGrid] Expanded: {_cacheWidth}x{_cacheDepth} " +
                $"(bounds: {_minX},{_minZ} to {_maxX},{_maxZ}) " +
                $"copied={copiedNodes}, new={newNodes}, in {elapsed:F1}ms");
        }

        #endregion

        #region Coordinate Conversion

        /// <summary>
        /// 월드 좌표 → 그리드 좌표 변환
        /// </summary>
        public bool WorldToGrid(Vector3 worldPos, out int x, out int z)
        {
            x = 0;
            z = 0;

            if (!_isValid || _gridGraph == null)
                return false;

            var node = worldPos.GetNearestNodeXZ() as CustomGridNodeBase;
            if (node == null)
                return false;

            x = node.XCoordinateInGrid;
            z = node.ZCoordinateInGrid;
            return true;
        }

        /// <summary>
        /// 그리드 좌표가 캐시 범위 내인지 확인
        /// </summary>
        private bool IsInCacheBounds(int graphX, int graphZ)
        {
            return graphX >= _minX && graphX <= _maxX &&
                   graphZ >= _minZ && graphZ <= _maxZ;
        }

        /// <summary>
        /// 그리드 좌표 → 캐시 인덱스 변환
        /// </summary>
        private bool GridToCacheIndex(int graphX, int graphZ, out int cacheX, out int cacheZ)
        {
            cacheX = graphX - _minX;
            cacheZ = graphZ - _minZ;

            return cacheX >= 0 && cacheX < _cacheWidth &&
                   cacheZ >= 0 && cacheZ < _cacheDepth;
        }

        /// <summary>
        /// 월드 좌표로 노드 가져오기
        /// </summary>
        public CustomGridNodeBase GetNode(Vector3 worldPos)
        {
            return worldPos.GetNearestNodeXZ() as CustomGridNodeBase;
        }

        /// <summary>
        /// 그리드 좌표로 노드 가져오기
        /// </summary>
        public CustomGridNodeBase GetNode(int graphX, int graphZ)
        {
            if (!_isValid || _gridGraph == null)
                return null;

            return _gridGraph.GetNode(graphX, graphZ);
        }

        #endregion

        #region Walkability Queries (Cached - O(1))

        /// <summary>
        /// 월드 좌표가 Walkable인지 확인 (캐시 조회)
        /// </summary>
        public bool IsWalkable(Vector3 worldPos)
        {
            if (!_isValid)
                return true; // 폴백: 게임에 맡김

            var node = GetNode(worldPos);
            return node != null && IsWalkable(node);
        }

        /// <summary>
        /// 노드가 Walkable인지 확인 (캐시 조회)
        /// </summary>
        public bool IsWalkable(CustomGridNodeBase node)
        {
            if (!_isValid || node == null)
                return node?.Walkable ?? false;

            int graphX = node.XCoordinateInGrid;
            int graphZ = node.ZCoordinateInGrid;

            // 캐시 범위 내면 캐시 조회
            if (GridToCacheIndex(graphX, graphZ, out int cacheX, out int cacheZ))
            {
                return _walkableCache[cacheX, cacheZ];
            }

            // 캐시 범위 밖이면 직접 조회
            return node.Walkable;
        }

        /// <summary>
        /// 특정 방향으로 연결이 있는지 확인 (캐시 조회)
        /// Direction: 0=S, 1=E, 2=N, 3=W, 4=SE, 5=NE, 6=NW, 7=SW
        /// </summary>
        public bool HasConnection(CustomGridNodeBase node, int direction)
        {
            if (!_isValid || node == null || direction < 0 || direction >= 8)
                return false;

            int graphX = node.XCoordinateInGrid;
            int graphZ = node.ZCoordinateInGrid;

            // 캐시 범위 내면 캐시 조회
            if (GridToCacheIndex(graphX, graphZ, out int cacheX, out int cacheZ))
            {
                return _connectionCache[cacheX, cacheZ, direction];
            }

            // 캐시 범위 밖이면 직접 조회
            return node.HasConnectionInDirection(direction);
        }

        #endregion

        #region Occupancy Queries (Real-time)

        /// <summary>
        /// 노드에 유닛이 있는지 확인 (실시간 조회)
        /// </summary>
        public bool IsOccupied(CustomGridNodeBase node)
        {
            if (node == null) return true;

            try
            {
                // 게임의 실제 점유 체크 사용
                return node.ContainsUnit();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 노드가 다른 유닛에 의해 점유되었는지 확인
        /// </summary>
        public bool IsOccupiedByOther(CustomGridNodeBase node, BaseUnitEntity unit)
        {
            if (node == null) return true;

            try
            {
                if (node.TryGetUnit(out var occupant))
                {
                    return occupant != null && occupant != unit && occupant.IsConscious;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 월드 좌표가 다른 유닛에 의해 점유되었는지 확인
        /// </summary>
        public bool IsOccupiedByOther(Vector3 worldPos, BaseUnitEntity unit)
        {
            var node = GetNode(worldPos);
            return IsOccupiedByOther(node, unit);
        }

        /// <summary>
        /// 유닛이 특정 노드에 설 수 있는지 확인 (종합 검증)
        /// - Walkable 체크 (캐시)
        /// - 점유 체크 (실시간)
        /// </summary>
        public bool CanUnitStandOn(BaseUnitEntity unit, CustomGridNodeBase node)
        {
            if (unit == null || node == null)
                return false;

            // 1. Walkable 체크 (캐시)
            if (!IsWalkable(node))
                return false;

            // 2. 다른 유닛 점유 체크 (실시간)
            if (IsOccupiedByOther(node, unit))
                return false;

            return true;
        }

        /// <summary>
        /// 유닛이 특정 위치에 설 수 있는지 확인
        /// </summary>
        public bool CanUnitStandOn(BaseUnitEntity unit, Vector3 worldPos)
        {
            var node = GetNode(worldPos);
            return CanUnitStandOn(unit, node);
        }

        #endregion

        #region Position Validation

        /// <summary>
        /// 타겟 위치 검증 (이동 전 사전 검증)
        /// Returns false if position is unwalkable or occupied
        /// </summary>
        public bool ValidateTargetPosition(BaseUnitEntity unit, Vector3 targetPos)
        {
            if (!_isValid)
                return true; // 그리드 미초기화 시 폴백: 게임에 맡김

            var node = GetNode(targetPos);
            if (node == null)
            {
                Main.LogDebug($"[BattlefieldGrid] ValidateTargetPosition: Node not found at ({targetPos.x:F1},{targetPos.z:F1})");
                return false;
            }

            // 1. Walkable 체크
            if (!IsWalkable(node))
            {
                Main.LogDebug($"[BattlefieldGrid] ValidateTargetPosition: Not walkable at ({node.XCoordinateInGrid},{node.ZCoordinateInGrid})");
                return false;
            }

            // 2. 점유 체크
            if (IsOccupiedByOther(node, unit))
            {
                Main.LogDebug($"[BattlefieldGrid] ValidateTargetPosition: Occupied at ({node.XCoordinateInGrid},{node.ZCoordinateInGrid})");
                return false;
            }

            return true;
        }

        /// <summary>
        /// 노드 검증 (위치 탐색 시 필터링용)
        /// </summary>
        public bool ValidateNode(BaseUnitEntity unit, CustomGridNodeBase node)
        {
            if (!_isValid)
                return true;

            if (node == null)
                return false;

            // Walkable + 점유 체크
            return IsWalkable(node) && !IsOccupiedByOther(node, unit);
        }

        #endregion

        #region Path Validation

        /// <summary>
        /// 두 노드 간 직선 경로가 연결되어 있는지 확인
        /// ContainsConnection 체크로 벽/장애물 감지
        /// (PointTargetingHelper.IsPathClear와 유사)
        /// </summary>
        public bool IsPathClear(CustomGridNodeBase fromNode, CustomGridNodeBase toNode)
        {
            if (!_isValid || fromNode == null || toNode == null)
                return false;

            // Bresenham's line algorithm with connection check
            int x0 = fromNode.XCoordinateInGrid;
            int z0 = fromNode.ZCoordinateInGrid;
            int x1 = toNode.XCoordinateInGrid;
            int z1 = toNode.ZCoordinateInGrid;

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
                var node = GetNode(x, z);
                if (node == null)
                    return false;

                // 노드 간 연결 체크
                if (prevNode != null)
                {
                    if (!prevNode.ContainsConnection(node))
                        return false;
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

            return true;
        }

        /// <summary>
        /// 두 위치 간 직선 경로가 연결되어 있는지 확인
        /// </summary>
        public bool IsPathClear(Vector3 from, Vector3 to)
        {
            var fromNode = GetNode(from);
            var toNode = GetNode(to);
            return IsPathClear(fromNode, toNode);
        }

        #endregion

        #region Properties

        /// <summary>그리드 초기화 완료 여부</summary>
        public bool IsValid => _isValid;

        /// <summary>캐시 영역 크기</summary>
        public int CacheWidth => _cacheWidth;
        public int CacheDepth => _cacheDepth;

        /// <summary>캐시 영역 경계</summary>
        public int MinX => _minX;
        public int MaxX => _maxX;
        public int MinZ => _minZ;
        public int MaxZ => _maxZ;

        #endregion

        #region Debug

        /// <summary>
        /// 디버그용 그리드 상태 출력
        /// </summary>
        public string GetDebugInfo()
        {
            if (!_isValid)
                return "[BattlefieldGrid] Not initialized";

            int walkableCount = 0;
            for (int x = 0; x < _cacheWidth; x++)
            {
                for (int z = 0; z < _cacheDepth; z++)
                {
                    if (_walkableCache[x, z])
                        walkableCount++;
                }
            }

            return $"[BattlefieldGrid] {_cacheWidth}x{_cacheDepth} " +
                   $"(graph: {_graphWidth}x{_graphDepth}), " +
                   $"bounds: ({_minX},{_minZ})-({_maxX},{_maxZ}), " +
                   $"walkable: {walkableCount}/{_cacheWidth * _cacheDepth}";
        }

        #endregion
    }
}
