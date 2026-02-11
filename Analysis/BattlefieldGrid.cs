using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Pathfinding;
using UnityEngine;

namespace CompanionAI_v3.Analysis
{
    /// <summary>
    /// ★ v3.7.62: 전장 그리드 - 게임의 실제 맵 구조 활용
    /// ★ v3.9.08: Zero-alloc 정적 캐시 + Dead Code 정리
    ///
    /// 게임의 실제 그리드 시스템을 캐싱하여 빠른 Walkable/점유 조회 제공:
    /// - CustomGridGraph: 전체 맵 노드 배열
    /// - CustomGridNode.Walkable: 이동 가능 여부 (정적 캐싱)
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

        #region ★ v3.9.08: Zero-alloc 정적 캐시 (GC 할당 제거 + 1D 평탄화)

        // 250×250 = 62,500 bool (62.5KB) — 모든 RT 맵 커버
        // 1D 평탄화: CPU 캐시 효율 극대화
        private static readonly bool[] _sharedWalkableBuffer = new bool[MAX_CACHE_SIZE * MAX_CACHE_SIZE];

        // 현재 인스턴스에서 사용할 버퍼 참조
        private bool[] _walkableCache;

        #endregion

        #region Grid State

        /// <summary>게임 그리드 그래프 참조</summary>
        private CustomGridGraph _gridGraph;

        /// <summary>그리드 전체 크기</summary>
        private int _graphWidth;
        private int _graphDepth;

        /// <summary>캐시 영역 경계 (전투 영역 + 패딩)</summary>
        private int _minX, _maxX, _minZ, _maxZ;

        /// <summary>캐시 영역 크기</summary>
        private int _cacheWidth, _cacheDepth;

        /// <summary>그리드 유효 여부</summary>
        private bool _isValid;

        #endregion

        #region Initialization

        /// <summary>
        /// 전투 시작 시 그리드 초기화
        /// TurnEventHandler.HandleTurnBasedModeSwitched(true)에서 호출
        /// ★ v3.9.08: 정적 버퍼 연결 — new 할당 제거
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

                // ★ v3.9.08: 정적 버퍼 참조 (힙 할당 0)
                _walkableCache = _sharedWalkableBuffer;

                // 4. Walkable 캐싱
                RefreshWalkableData();

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
        /// ★ v3.9.08: Walkable 데이터 캐싱 (1D 평탄화, 연결 캐싱 제거)
        /// 기존 CacheGridData() 대체
        /// </summary>
        private void RefreshWalkableData()
        {
            int cachedNodes = 0;
            int walkableNodes = 0;

            for (int z = 0; z < _cacheDepth; z++)
            {
                int rowOffset = z * MAX_CACHE_SIZE;
                for (int x = 0; x < _cacheWidth; x++)
                {
                    var node = _gridGraph.GetNode(_minX + x, _minZ + z);
                    bool walkable = node != null && node.Walkable;
                    _walkableCache[rowOffset + x] = walkable;

                    if (node != null) cachedNodes++;
                    if (walkable) walkableNodes++;
                }
            }

            Main.LogDebug($"[BattlefieldGrid] Cached {cachedNodes} nodes, {walkableNodes} walkable");
        }

        /// <summary>
        /// 전투 종료 시 캐시 정리
        /// ★ v3.9.08: 참조만 끊기 (정적 버퍼는 다음 전투에 재사용)
        /// </summary>
        public void Clear()
        {
            _walkableCache = null;
            _gridGraph = null;
            _isValid = false;
            Main.LogDebug("[BattlefieldGrid] Cleared");
        }

        /// <summary>
        /// ★ v3.7.68: 유닛 위치 기반으로 캐시 영역 확장 필요 여부 확인
        /// </summary>
        private bool NeedsExpansion(List<BaseUnitEntity> allUnits)
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
        /// ★ v3.9.08: 배열 복사/재할당 완전 제거 — 범위 재계산 + 데이터 갱신만
        /// </summary>
        public void ExpandIfNeeded(List<BaseUnitEntity> allUnits)
        {
            if (!_isValid || allUnits == null) return;
            if (!NeedsExpansion(allUnits)) return;

            var startTime = DateTime.Now;

            // ★ v3.9.08: 정적 버퍼 위에 덮어쓰기 — new 할당 0, 배열 복사 0
            CalculateCombatBounds(allUnits);
            _cacheWidth = Math.Min(_maxX - _minX + 1, MAX_CACHE_SIZE);
            _cacheDepth = Math.Min(_maxZ - _minZ + 1, MAX_CACHE_SIZE);
            RefreshWalkableData();

            var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
            Main.Log($"[BattlefieldGrid] Refreshed: {_cacheWidth}x{_cacheDepth} " +
                $"(bounds: {_minX},{_minZ} to {_maxX},{_maxZ}) in {elapsed:F1}ms");
        }

        #endregion

        #region Coordinate Helpers

        /// <summary>
        /// ★ v3.9.08: 1D 캐시 인덱스 계산 (GridToCacheIndex 대체)
        /// unsigned 캐스트: 음수 → 큰 양수 → 범위 초과 = 1개 비교로 2개 조건 처리
        /// </summary>
        private int GetCacheIndex(int graphX, int graphZ)
        {
            int localX = graphX - _minX;
            int localZ = graphZ - _minZ;
            if ((uint)localX >= (uint)_cacheWidth || (uint)localZ >= (uint)_cacheDepth)
                return -1;
            return localZ * MAX_CACHE_SIZE + localX;
        }

        /// <summary>
        /// 월드 좌표로 노드 가져오기
        /// </summary>
        public CustomGridNodeBase GetNode(Vector3 worldPos)
        {
            return worldPos.GetNearestNodeXZ() as CustomGridNodeBase;
        }

        #endregion

        #region Walkability Queries (Cached — O(1))

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
        /// ★ v3.9.08: 1D 인덱싱
        /// </summary>
        public bool IsWalkable(CustomGridNodeBase node)
        {
            if (!_isValid || node == null)
                return node?.Walkable ?? false;

            int index = GetCacheIndex(node.XCoordinateInGrid, node.ZCoordinateInGrid);
            if (index >= 0)
                return _walkableCache[index];

            // 캐시 범위 밖이면 직접 조회
            return node.Walkable;
        }

        #endregion

        #region Occupancy Queries (Real-time)

        /// <summary>
        /// 노드가 다른 유닛에 의해 점유되었는지 확인
        /// </summary>
        private bool IsOccupiedByOther(CustomGridNodeBase node, BaseUnitEntity unit)
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

        #endregion

        #region Node Validation

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

        #region Properties

        /// <summary>그리드 초기화 완료 여부</summary>
        public bool IsValid => _isValid;

        #endregion

        #region Debug

        /// <summary>
        /// 디버그용 그리드 상태 출력
        /// ★ v3.9.08: O(n²) walkable 카운트 루프 제거
        /// </summary>
        public string GetDebugInfo()
        {
            if (!_isValid)
                return "[BattlefieldGrid] Not initialized";

            return $"[BattlefieldGrid] {_cacheWidth}x{_cacheDepth} " +
                   $"(graph: {_graphWidth}x{_graphDepth}), " +
                   $"bounds: ({_minX},{_minZ})-({_maxX},{_maxZ})";
        }

        #endregion
    }
}
