using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using UnityEngine;
using CompanionAI_v3.GameInterface;

namespace CompanionAI_v3.Analysis
{
    /// <summary>
    /// ★ v3.4.00: 예측적 위협 맵
    ///
    /// 적의 이동 가능 범위를 기반으로 다음 턴 위협 구역을 예측.
    /// ★ v3.6.3: GridCellSize와 통일 (1.35m = 1타일)
    /// 현재 위협 + 예측 위협을 결합하여 안전 위치 결정에 활용.
    /// </summary>
    public class PredictiveThreatMap
    {
        #region Constants

        /// <summary>★ v3.6.3: 그리드 셀 크기 - GridCellSize와 통일 (1.35m = 1타일)</summary>
        private const float CELL_SIZE = 1.35f;  // CombatAPI.GridCellSize

        /// <summary>예측 위협 최대 거리</summary>
        private const float MAX_PREDICTION_DISTANCE = 25f;

        /// <summary>안전 구역 위협 임계값</summary>
        private const float SAFE_ZONE_THRESHOLD = 0.3f;

        /// <summary>위험 구역 위협 임계값</summary>
        private const float DANGER_ZONE_THRESHOLD = 0.7f;

        /// <summary>거리 감쇠 계수 (먼 타일 = 이동 확률 낮음)</summary>
        private const float DISTANCE_DECAY_FACTOR = 0.1f;

        #endregion

        #region Fields

        // 예측 위협 그리드
        private float[,] _predictedThreatGrid;

        // 그리드 메타데이터
        private Vector3 _gridOrigin;
        private int _gridWidth;
        private int _gridHeight;
        private bool _isValid;

        // 분석된 적 데이터
        private List<EnemyMobility> _enemyMobilities;

        #endregion

        #region Properties

        /// <summary>예측된 안전 구역 (다음 턴에도 안전할 가능성 높음)</summary>
        public List<Vector3> PredictedSafeZones { get; private set; } = new List<Vector3>();

        /// <summary>위험 구역 (다음 턴에 적이 도달 가능)</summary>
        public List<Vector3> DangerZones { get; private set; } = new List<Vector3>();

        /// <summary>맵 유효성</summary>
        public bool IsValid => _isValid;

        /// <summary>분석된 적 수</summary>
        public int EnemyCount => _enemyMobilities?.Count ?? 0;

        #endregion

        #region Factory

        /// <summary>
        /// 예측적 위협 맵 계산
        /// </summary>
        /// <param name="enemies">적 유닛 목록</param>
        /// <param name="mobilities">적 이동력 분석 결과</param>
        /// <param name="currentInfluence">현재 영향력 맵 (경계 참조용)</param>
        /// <returns>예측 위협 맵</returns>
        public static PredictiveThreatMap Compute(
            List<BaseUnitEntity> enemies,
            List<EnemyMobility> mobilities,
            BattlefieldInfluenceMap currentInfluence)
        {
            var map = new PredictiveThreatMap();
            map.ComputeInternal(enemies, mobilities, currentInfluence);
            return map;
        }

        #endregion

        #region Core Computation

        private void ComputeInternal(
            List<BaseUnitEntity> enemies,
            List<EnemyMobility> mobilities,
            BattlefieldInfluenceMap currentInfluence)
        {
            _enemyMobilities = mobilities ?? new List<EnemyMobility>();

            // 적이 2명 미만이면 스킵 (성능 최적화)
            if (enemies == null || enemies.Count < 2 || _enemyMobilities.Count == 0)
            {
                _isValid = false;
                return;
            }

            try
            {
                // 1. 그리드 초기화 (현재 영향력 맵 경계 활용)
                InitializeGrid(enemies, currentInfluence);

                // 2. 예측 위협 계산
                ComputePredictedThreat();

                // 3. 안전/위험 구역 탐색
                FindZones();

                _isValid = true;

                Main.LogDebug($"[PredictiveThreat] Computed: {_gridWidth}x{_gridHeight} grid, " +
                              $"{PredictedSafeZones.Count} safe zones, {DangerZones.Count} danger zones");
            }
            catch (Exception ex)
            {
                Main.LogError($"[PredictiveThreat] Compute failed: {ex.Message}");
                _isValid = false;
            }
        }

        private void InitializeGrid(List<BaseUnitEntity> enemies, BattlefieldInfluenceMap currentInfluence)
        {
            Vector3 min, max;

            if (currentInfluence != null && currentInfluence.IsValid)
            {
                // 현재 영향력 맵에서 경계 추출 (EnemyCentroid, AllyCentroid 활용)
                Vector3 center = (currentInfluence.EnemyCentroid + currentInfluence.AllyCentroid) / 2f;
                min = center - Vector3.one * MAX_PREDICTION_DISTANCE;
                max = center + Vector3.one * MAX_PREDICTION_DISTANCE;
            }
            else
            {
                // 폴백: 적 위치에서 경계 계산
                min = new Vector3(float.MaxValue, 0, float.MaxValue);
                max = new Vector3(float.MinValue, 0, float.MinValue);

                foreach (var enemy in enemies)
                {
                    if (enemy == null) continue;
                    var pos = enemy.Position;
                    min.x = Mathf.Min(min.x, pos.x - MAX_PREDICTION_DISTANCE);
                    min.z = Mathf.Min(min.z, pos.z - MAX_PREDICTION_DISTANCE);
                    max.x = Mathf.Max(max.x, pos.x + MAX_PREDICTION_DISTANCE);
                    max.z = Mathf.Max(max.z, pos.z + MAX_PREDICTION_DISTANCE);
                }
            }

            _gridOrigin = min;
            _gridWidth = Mathf.CeilToInt((max.x - min.x) / CELL_SIZE) + 1;
            _gridHeight = Mathf.CeilToInt((max.z - min.z) / CELL_SIZE) + 1;

            // 성능을 위해 최대 크기 제한
            _gridWidth = Mathf.Clamp(_gridWidth, 1, 80);
            _gridHeight = Mathf.Clamp(_gridHeight, 1, 80);

            _predictedThreatGrid = new float[_gridWidth, _gridHeight];
        }

        private void ComputePredictedThreat()
        {
            // 각 셀에 대해 예측 위협 계산
            for (int x = 0; x < _gridWidth; x++)
            {
                for (int z = 0; z < _gridHeight; z++)
                {
                    Vector3 cellPos = GetCellWorldPosition(x, z);
                    float threat = CalculatePredictedThreatAt(cellPos);
                    _predictedThreatGrid[x, z] = threat;
                }
            }
        }

        /// <summary>
        /// 특정 위치의 예측 위협 계산
        /// </summary>
        private float CalculatePredictedThreatAt(Vector3 position)
        {
            float totalThreat = 0f;

            foreach (var mobility in _enemyMobilities)
            {
                if (mobility == null || mobility.Enemy == null) continue;

                // ★ v3.6.2: 1. 현재 위치에서의 거리 (타일 단위로 통일)
                float distFromEnemyTiles = CombatAPI.MetersToTiles(Vector3.Distance(position, mobility.Enemy.Position));

                // 2. 도달 가능 타일 기반 위협
                float reachThreat = CalculateReachThreat(position, mobility);

                // ★ v3.6.2: 3. GapCloser 기반 위협 - 모두 타일 단위로 통일
                float gapCloserThreat = 0f;
                // GapCloserRange는 미터로 저장되어 있으므로 타일로 변환
                float gapCloserRangeTiles = CombatAPI.MetersToTiles(mobility.GapCloserRange);
                float totalReachTiles = gapCloserRangeTiles + mobility.MovementPoints;  // 이제 둘 다 타일
                if (mobility.HasGapCloser && distFromEnemyTiles <= totalReachTiles)
                {
                    // GapCloser 범위 내면 추가 위협
                    gapCloserThreat = 0.5f * (1f - distFromEnemyTiles / totalReachTiles);
                    gapCloserThreat = Mathf.Clamp01(gapCloserThreat);
                }

                // 적 위협 계수 (HP, 공격력 등 고려)
                float enemyThreatFactor = GetEnemyThreatFactor(mobility.Enemy);

                totalThreat += (reachThreat + gapCloserThreat) * enemyThreatFactor;
            }

            // 정규화 (0-1 범위)
            return Mathf.Clamp01(totalThreat);
        }

        /// <summary>
        /// 도달 가능 타일 기반 위협 계산
        /// </summary>
        private float CalculateReachThreat(Vector3 position, EnemyMobility mobility)
        {
            if (mobility.TileCount == 0) return 0f;

            float minDistToReachable = float.MaxValue;

            // 가장 가까운 도달 가능 타일까지의 거리
            foreach (var tile in mobility.ReachableTiles)
            {
                float dist = Vector3.Distance(position, tile);
                if (dist < minDistToReachable)
                    minDistToReachable = dist;
            }

            // 거리 기반 위협 계산
            // 타일 위 = 1.0, 멀수록 감소
            if (minDistToReachable < CELL_SIZE)
            {
                return 1.0f;  // 도달 가능 타일 위
            }
            else if (minDistToReachable < mobility.MaxReach)
            {
                // 거리에 따른 감쇠
                float decay = 1f - (minDistToReachable / mobility.MaxReach);
                return Mathf.Clamp01(decay * decay);  // 제곱 감쇠
            }

            return 0f;
        }

        private float GetEnemyThreatFactor(BaseUnitEntity enemy)
        {
            try
            {
                // HP 비율 기반 위협 (살아있을수록 위험)
                float hpPercent = CombatAPI.GetHPPercent(enemy) / 100f;

                // 기본 위협 1.0, HP에 따라 0.5-1.5 범위
                return 0.5f + hpPercent;
            }
            catch
            {
                return 1.0f;
            }
        }

        private void FindZones()
        {
            PredictedSafeZones.Clear();
            DangerZones.Clear();

            for (int x = 0; x < _gridWidth; x++)
            {
                for (int z = 0; z < _gridHeight; z++)
                {
                    float threat = _predictedThreatGrid[x, z];
                    Vector3 pos = GetCellWorldPosition(x, z);

                    if (threat < SAFE_ZONE_THRESHOLD)
                    {
                        PredictedSafeZones.Add(pos);
                    }
                    else if (threat > DANGER_ZONE_THRESHOLD)
                    {
                        DangerZones.Add(pos);
                    }
                }
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// 특정 위치의 예측 위협도 조회
        /// </summary>
        /// <returns>0.0 (안전) ~ 1.0 (위험)</returns>
        public float GetPredictedThreatAt(Vector3 position)
        {
            if (!_isValid) return 0f;

            int x, z;
            if (!WorldToGrid(position, out x, out z))
                return 0f;

            return _predictedThreatGrid[x, z];
        }

        /// <summary>
        /// 특정 위치가 다음 턴에도 안전할지 확인
        /// </summary>
        public bool IsPositionSafeNextTurn(Vector3 position)
        {
            if (!_isValid) return true;  // 맵 없으면 안전 가정

            float threat = GetPredictedThreatAt(position);
            return threat < SAFE_ZONE_THRESHOLD;
        }

        /// <summary>
        /// 턴 안전도 점수 (위치 평가용)
        /// </summary>
        /// <returns>0.0 (위험) ~ 1.0 (안전)</returns>
        public float GetTurnSafetyScore(Vector3 position)
        {
            if (!_isValid) return 0.5f;  // 맵 없으면 중립

            float threat = GetPredictedThreatAt(position);
            return 1f - threat;  // 위협 반전 = 안전도
        }

        /// <summary>
        /// 가장 가까운 안전 구역 찾기
        /// </summary>
        public Vector3? FindNearestSafeZone(Vector3 fromPosition)
        {
            if (!_isValid || PredictedSafeZones.Count == 0)
                return null;

            Vector3 nearest = PredictedSafeZones[0];
            float minDist = Vector3.Distance(fromPosition, nearest);

            foreach (var zone in PredictedSafeZones)
            {
                float dist = Vector3.Distance(fromPosition, zone);
                if (dist < minDist)
                {
                    minDist = dist;
                    nearest = zone;
                }
            }

            return nearest;
        }

        /// <summary>
        /// 위치 주변의 평균 위협도
        /// </summary>
        public float GetAverageThreatInRadius(Vector3 center, float radius)
        {
            if (!_isValid) return 0f;

            float totalThreat = 0f;
            int count = 0;

            int minX = Mathf.Max(0, WorldToGridX(center.x - radius));
            int maxX = Mathf.Min(_gridWidth - 1, WorldToGridX(center.x + radius));
            int minZ = Mathf.Max(0, WorldToGridZ(center.z - radius));
            int maxZ = Mathf.Min(_gridHeight - 1, WorldToGridZ(center.z + radius));

            for (int x = minX; x <= maxX; x++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    Vector3 cellPos = GetCellWorldPosition(x, z);
                    if (Vector3.Distance(center, cellPos) <= radius)
                    {
                        totalThreat += _predictedThreatGrid[x, z];
                        count++;
                    }
                }
            }

            return count > 0 ? totalThreat / count : 0f;
        }

        #endregion

        #region Grid Helpers

        private Vector3 GetCellWorldPosition(int x, int z)
        {
            return new Vector3(
                _gridOrigin.x + x * CELL_SIZE + CELL_SIZE / 2f,
                _gridOrigin.y,
                _gridOrigin.z + z * CELL_SIZE + CELL_SIZE / 2f
            );
        }

        private bool WorldToGrid(Vector3 worldPos, out int x, out int z)
        {
            x = WorldToGridX(worldPos.x);
            z = WorldToGridZ(worldPos.z);

            return x >= 0 && x < _gridWidth && z >= 0 && z < _gridHeight;
        }

        private int WorldToGridX(float worldX)
        {
            return Mathf.FloorToInt((worldX - _gridOrigin.x) / CELL_SIZE);
        }

        private int WorldToGridZ(float worldZ)
        {
            return Mathf.FloorToInt((worldZ - _gridOrigin.z) / CELL_SIZE);
        }

        #endregion
    }
}
