using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using UnityEngine;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Analysis
{
    /// <summary>
    /// ★ v3.2.00: 전장 영향력 맵
    ///
    /// 적/아군의 위치 기반 영향력을 2D 그리드로 계산하여 O(1) 조회 제공.
    /// - ThreatAt(pos) = Σ (EnemyThreat[i] / Distance[i]²)
    /// - ControlAt(pos) = Σ (AllyStrength[i] / Distance[i]²)
    ///
    /// 전선(Frontline), 안전 구역, 팀 위협도 등 전술적 정보 제공.
    /// </summary>
    public class BattlefieldInfluenceMap
    {
        #region Constants

        /// <summary>그리드 셀 크기 (미터)</summary>
        private const float CELL_SIZE = 2.0f;

        /// <summary>영향력 최대 거리 (미터)</summary>
        private const float MAX_INFLUENCE_DISTANCE = 20.0f;

        /// <summary>최소 거리 (0 나눗셈 방지)</summary>
        private const float MIN_DISTANCE = 1.0f;

        /// <summary>안전 구역 위협 임계값</summary>
        private const float SAFE_ZONE_THRESHOLD = 0.5f;

        #endregion

        #region Fields

        // 그리드 데이터
        private float[,] _threatGrid;      // 적 위협 밀도
        private float[,] _controlGrid;     // 아군 통제 영역
        private float[,] _coverGrid;       // ★ v3.5.00: 엄폐 품질 (0=None, 0.5=Half, 1.0=Full)

        // 그리드 메타데이터
        private Vector3 _gridOrigin;       // 그리드 원점 (최소 좌표)
        private int _gridWidth;            // X 방향 셀 수
        private int _gridHeight;           // Z 방향 셀 수
        private bool _isValid;             // 유효한 맵 여부

        // 유닛 캐시
        private List<BaseUnitEntity> _enemies;
        private List<BaseUnitEntity> _allies;

        // ★ v3.5.10: PDF Stamp Cache
        private CoverStampCache _stampCache;

        #endregion

        #region ★ v3.5.10: PDF Stamp Caching (Template Kernel)

        /// <summary>
        /// PDF 방법론 기반 Stamp 캐싱 시스템
        ///
        /// 기존: 100×100 그리드 × 적 수 × raycast = O(W×H×E) = 최대 80,000 raycasts
        /// 변경: 적 수 × 21×21 stamp × raycast = O(E×441) = 최대 3,528 raycasts
        ///
        /// 성능 향상: 22배 (400-1600ms → 35-50ms)
        /// </summary>
        private class CoverStampCache
        {
            /// <summary>스탬프 크기 (21×21 = 441 cells per enemy)</summary>
            private const int STAMP_SIZE = 21;
            private const int STAMP_RADIUS = 10;  // STAMP_SIZE / 2

            /// <summary>적 ID → 스탬프 맵핑</summary>
            private Dictionary<string, float[,]> _stamps = new Dictionary<string, float[,]>();

            /// <summary>
            /// 모든 적에 대한 스탬프 사전 계산
            /// </summary>
            public void PrecomputeStamps(
                List<BaseUnitEntity> enemies,
                Vector3 gridOrigin,
                float cellSize)
            {
                _stamps.Clear();

                foreach (var enemy in enemies)
                {
                    if (enemy == null) continue;

                    string enemyId = enemy.UniqueId ?? enemy.CharacterName ?? "unknown";
                    float[,] stamp = new float[STAMP_SIZE, STAMP_SIZE];

                    // 21×21 스탬프 내 각 셀의 엄폐 계산
                    for (int dx = -STAMP_RADIUS; dx <= STAMP_RADIUS; dx++)
                    {
                        for (int dz = -STAMP_RADIUS; dz <= STAMP_RADIUS; dz++)
                        {
                            // 스탬프 셀의 월드 좌표 계산
                            Vector3 checkPos = enemy.Position + new Vector3(
                                dx * cellSize,
                                0,
                                dz * cellSize
                            );

                            // 이 위치에서 해당 적에 대한 엄폐 품질
                            float coverValue = CalculateCoverValue(checkPos, enemy);
                            stamp[dx + STAMP_RADIUS, dz + STAMP_RADIUS] = coverValue;
                        }
                    }

                    _stamps[enemyId] = stamp;
                }

                Main.LogDebug($"[CoverStamp] Precomputed {_stamps.Count} stamps ({STAMP_SIZE}x{STAMP_SIZE} each)");
            }

            /// <summary>
            /// 스탬프를 그리드에 적용 (Max 블렌딩)
            /// 각 셀에서 가장 좋은 엄폐값 사용
            /// </summary>
            public void ApplyStampsToGrid(
                float[,] coverGrid,
                List<BaseUnitEntity> enemies,
                Vector3 gridOrigin,
                int gridWidth,
                int gridHeight,
                float cellSize)
            {
                // 먼저 모든 셀을 0으로 초기화
                for (int x = 0; x < gridWidth; x++)
                    for (int z = 0; z < gridHeight; z++)
                        coverGrid[x, z] = 0f;

                // 각 적의 스탬프 적용
                foreach (var enemy in enemies)
                {
                    if (enemy == null) continue;

                    string enemyId = enemy.UniqueId ?? enemy.CharacterName ?? "unknown";
                    if (!_stamps.TryGetValue(enemyId, out var stamp))
                        continue;

                    // 적의 그리드 좌표
                    int centerX = Mathf.FloorToInt((enemy.Position.x - gridOrigin.x) / cellSize);
                    int centerZ = Mathf.FloorToInt((enemy.Position.z - gridOrigin.z) / cellSize);

                    // 스탬프를 그리드에 블렌딩
                    for (int dx = -STAMP_RADIUS; dx <= STAMP_RADIUS; dx++)
                    {
                        for (int dz = -STAMP_RADIUS; dz <= STAMP_RADIUS; dz++)
                        {
                            int gx = centerX + dx;
                            int gz = centerZ + dz;

                            // 그리드 경계 체크
                            if (gx < 0 || gx >= gridWidth || gz < 0 || gz >= gridHeight)
                                continue;

                            float stampValue = stamp[dx + STAMP_RADIUS, dz + STAMP_RADIUS];

                            // ★ 핵심: Max 블렌딩 (가장 좋은 엄폐값 사용)
                            // 여러 적 중 하나라도 엄폐가 안 되면 0, 모든 적에게 엄폐되면 1
                            // → 실제로는 Min 블렌딩이 더 적합 (가장 노출된 값 사용)
                            // PDF 원문은 "overlay" 방식을 사용하나, 전술적으로 Min이 더 현실적
                            if (coverGrid[gx, gz] == 0f)
                            {
                                coverGrid[gx, gz] = stampValue;
                            }
                            else
                            {
                                // Min 블렌딩: 가장 취약한 방향 기준
                                coverGrid[gx, gz] = Mathf.Min(coverGrid[gx, gz], stampValue);
                            }
                        }
                    }
                }

                // 스탬프 범위 밖 셀은 0 유지 (엄폐 정보 없음 = 안전하지 않음으로 간주)
            }

            /// <summary>
            /// 위치에서 특정 적에 대한 엄폐 품질 계산
            /// </summary>
            private float CalculateCoverValue(Vector3 position, BaseUnitEntity enemy)
            {
                try
                {
                    var coverLevel = CombatAPI.GetCoverTypeAtPosition(position, enemy);
                    switch (coverLevel)
                    {
                        case CombatAPI.CoverLevel.Full:
                            return 1.0f;
                        case CombatAPI.CoverLevel.Half:
                            return 0.5f;
                        default:
                            return 0.0f;
                    }
                }
                catch
                {
                    return 0.0f;
                }
            }
        }

        #endregion

        #region Properties

        /// <summary>전선 위치 (적/아군 경계)</summary>
        public Vector3 Frontline { get; private set; }

        /// <summary>안전 구역 목록 (위협이 낮은 위치)</summary>
        public List<Vector3> SafeZones { get; private set; } = new List<Vector3>();

        /// <summary>전체 팀 위협도 (0-1)</summary>
        public float TeamThreatLevel { get; private set; }

        /// <summary>적 중심점</summary>
        public Vector3 EnemyCentroid { get; private set; }

        /// <summary>아군 중심점</summary>
        public Vector3 AllyCentroid { get; private set; }

        /// <summary>★ v3.2.25: 전선 방향 (아군→적)</summary>
        public Vector3 FrontlineDirection { get; private set; }

        /// <summary>맵이 유효한지 (계산 완료)</summary>
        public bool IsValid => _isValid;

        #endregion

        #region Factory

        /// <summary>
        /// 영향력 맵 계산
        /// </summary>
        public static BattlefieldInfluenceMap Compute(
            List<BaseUnitEntity> enemies,
            List<BaseUnitEntity> allies)
        {
            var map = new BattlefieldInfluenceMap();
            map.ComputeInternal(enemies, allies);
            return map;
        }

        #endregion

        #region Core Computation

        private void ComputeInternal(List<BaseUnitEntity> enemies, List<BaseUnitEntity> allies)
        {
            _enemies = enemies ?? new List<BaseUnitEntity>();
            _allies = allies ?? new List<BaseUnitEntity>();

            if (_enemies.Count == 0 && _allies.Count == 0)
            {
                _isValid = false;
                return;
            }

            try
            {
                // 1. 전장 경계 계산
                CalculateBounds(out Vector3 min, out Vector3 max);

                // 2. 그리드 초기화
                InitializeGrid(min, max);

                // 3. 영향력 계산
                ComputeInfluence();

                // 4. 전술 정보 계산
                ComputeTacticalInfo();

                _isValid = true;
            }
            catch (Exception ex)
            {
                Main.LogError($"[InfluenceMap] Compute failed: {ex.Message}");
                _isValid = false;
            }
        }

        private void CalculateBounds(out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue, 0, float.MaxValue);
            max = new Vector3(float.MinValue, 0, float.MinValue);

            // 모든 유닛 위치에서 경계 계산
            foreach (var unit in _enemies)
            {
                if (unit == null) continue;
                var pos = unit.Position;
                min.x = Mathf.Min(min.x, pos.x);
                min.z = Mathf.Min(min.z, pos.z);
                max.x = Mathf.Max(max.x, pos.x);
                max.z = Mathf.Max(max.z, pos.z);
            }

            foreach (var unit in _allies)
            {
                if (unit == null) continue;
                var pos = unit.Position;
                min.x = Mathf.Min(min.x, pos.x);
                min.z = Mathf.Min(min.z, pos.z);
                max.x = Mathf.Max(max.x, pos.x);
                max.z = Mathf.Max(max.z, pos.z);
            }

            // 여유 공간 추가 (영향력 범위 고려)
            min.x -= MAX_INFLUENCE_DISTANCE;
            min.z -= MAX_INFLUENCE_DISTANCE;
            max.x += MAX_INFLUENCE_DISTANCE;
            max.z += MAX_INFLUENCE_DISTANCE;
        }

        private void InitializeGrid(Vector3 min, Vector3 max)
        {
            _gridOrigin = min;
            _gridWidth = Mathf.CeilToInt((max.x - min.x) / CELL_SIZE) + 1;
            _gridHeight = Mathf.CeilToInt((max.z - min.z) / CELL_SIZE) + 1;

            // 성능을 위해 최대 크기 제한
            _gridWidth = Mathf.Min(_gridWidth, 100);
            _gridHeight = Mathf.Min(_gridHeight, 100);

            _threatGrid = new float[_gridWidth, _gridHeight];
            _controlGrid = new float[_gridWidth, _gridHeight];
            _coverGrid = new float[_gridWidth, _gridHeight];  // ★ v3.5.00: CoverMap
        }

        private void ComputeInfluence()
        {
            // 각 셀에 대해 영향력 계산
            for (int x = 0; x < _gridWidth; x++)
            {
                for (int z = 0; z < _gridHeight; z++)
                {
                    Vector3 cellPos = GetCellWorldPosition(x, z);

                    // 적 위협 계산
                    float threat = 0f;
                    foreach (var enemy in _enemies)
                    {
                        if (enemy == null) continue;
                        float dist = Vector3.Distance(cellPos, enemy.Position);
                        if (dist < MAX_INFLUENCE_DISTANCE)
                        {
                            float enemyThreat = GetUnitThreatValue(enemy);
                            // 역제곱 감소 (거리가 2배 → 영향력 1/4)
                            float effectiveDist = Mathf.Max(dist, MIN_DISTANCE);
                            threat += enemyThreat / (effectiveDist * effectiveDist);
                        }
                    }
                    _threatGrid[x, z] = threat;

                    // 아군 통제력 계산
                    float control = 0f;
                    foreach (var ally in _allies)
                    {
                        if (ally == null) continue;
                        float dist = Vector3.Distance(cellPos, ally.Position);
                        if (dist < MAX_INFLUENCE_DISTANCE)
                        {
                            float allyStrength = GetUnitStrengthValue(ally);
                            float effectiveDist = Mathf.Max(dist, MIN_DISTANCE);
                            control += allyStrength / (effectiveDist * effectiveDist);
                        }
                    }
                    _controlGrid[x, z] = control;
                }
            }
        }

        private void ComputeTacticalInfo()
        {
            // 1. 중심점 계산
            ComputeCentroids();

            // 2. 전선 계산 (적/아군 중심 사이의 중간점)
            ComputeFrontline();

            // 3. ★ v3.5.00: CoverMap 계산 (PDF 방법론)
            ComputeCoverMap();

            // 4. 안전 구역 탐색
            FindSafeZones();

            // 5. 팀 위협도 계산
            ComputeTeamThreatLevel();
        }

        /// <summary>
        /// ★ v3.5.10: CoverMap 계산 (PDF Stamp 방식)
        ///
        /// 기존 O(W×H×E) → O(E×441) 최적화
        /// - 스탬프 사전 계산: 적 수 × 21×21 = 최대 3,528 raycasts
        /// - 그리드 적용: 순수 메모리 연산 (raycast 없음)
        /// </summary>
        private void ComputeCoverMap()
        {
            if (_enemies.Count == 0)
            {
                // 적이 없으면 모든 위치가 안전 (Full cover)
                for (int x = 0; x < _gridWidth; x++)
                    for (int z = 0; z < _gridHeight; z++)
                        _coverGrid[x, z] = 1.0f;
                return;
            }

            // ★ v3.5.10: Stamp 캐시 초기화 및 사용
            if (_stampCache == null)
                _stampCache = new CoverStampCache();

            // Step 1: 스탬프 사전 계산 (E × 441 raycasts)
            _stampCache.PrecomputeStamps(_enemies, _gridOrigin, CELL_SIZE);

            // Step 2: 스탬프를 그리드에 적용 (순수 메모리 연산)
            _stampCache.ApplyStampsToGrid(
                _coverGrid,
                _enemies,
                _gridOrigin,
                _gridWidth,
                _gridHeight,
                CELL_SIZE
            );

            Main.LogDebug($"[InfluenceMap] CoverMap computed (Stamp): {_gridWidth}x{_gridHeight} grid, {_enemies.Count} enemies");
        }

        private void ComputeCentroids()
        {
            Vector3 enemySum = Vector3.zero;
            int enemyCount = 0;
            foreach (var enemy in _enemies)
            {
                if (enemy == null) continue;
                enemySum += enemy.Position;
                enemyCount++;
            }
            EnemyCentroid = enemyCount > 0 ? enemySum / enemyCount : Vector3.zero;

            Vector3 allySum = Vector3.zero;
            int allyCount = 0;
            foreach (var ally in _allies)
            {
                if (ally == null) continue;
                allySum += ally.Position;
                allyCount++;
            }
            AllyCentroid = allyCount > 0 ? allySum / allyCount : Vector3.zero;
        }

        /// <summary>
        /// ★ v3.2.25: Contact Line 기반 전선 계산
        /// 각 아군의 최근접 적과의 중간점들을 평균하여 실제 교전 위치 기반 전선 산출
        /// </summary>
        private void ComputeFrontline()
        {
            var contactPoints = new List<Vector3>();

            // 1. 각 아군-최근접 적 쌍의 접촉점 계산
            foreach (var ally in _allies)
            {
                if (ally == null) continue;

                BaseUnitEntity nearestEnemy = null;
                float nearestDist = float.MaxValue;

                foreach (var enemy in _enemies)
                {
                    if (enemy == null) continue;
                    float dist = Vector3.Distance(ally.Position, enemy.Position);
                    if (dist < nearestDist)
                    {
                        nearestDist = dist;
                        nearestEnemy = enemy;
                    }
                }

                // 접촉 거리 20m 이내면 접촉 지점으로 인정
                if (nearestEnemy != null && nearestDist < 20f)
                {
                    Vector3 contactPoint = (ally.Position + nearestEnemy.Position) / 2f;
                    contactPoints.Add(contactPoint);
                }
            }

            // 2. 접촉 지점들의 평균 = 전선
            if (contactPoints.Count > 0)
            {
                Vector3 sum = Vector3.zero;
                foreach (var point in contactPoints)
                    sum += point;
                Frontline = sum / contactPoints.Count;
            }
            else
            {
                // 폴백: 기존 중간점 방식 (접촉이 없으면)
                Frontline = (EnemyCentroid + AllyCentroid) / 2f;
            }

            // 3. 전선 방향 계산 (아군→적)
            Vector3 direction = EnemyCentroid - AllyCentroid;
            FrontlineDirection = direction.magnitude > 0.01f ? direction.normalized : Vector3.forward;

            Main.LogDebug($"[InfluenceMap] Frontline computed: {contactPoints.Count} contact points, " +
                $"Pos={Frontline}, Dir={FrontlineDirection}");
        }

        private void FindSafeZones()
        {
            SafeZones.Clear();

            // 아군 중심점 근처에서 위협이 낮은 셀 탐색
            for (int x = 0; x < _gridWidth; x++)
            {
                for (int z = 0; z < _gridHeight; z++)
                {
                    if (_threatGrid[x, z] < SAFE_ZONE_THRESHOLD && _controlGrid[x, z] > 0)
                    {
                        Vector3 cellPos = GetCellWorldPosition(x, z);
                        // 아군 중심에서 너무 멀지 않은 위치만
                        if (Vector3.Distance(cellPos, AllyCentroid) < MAX_INFLUENCE_DISTANCE)
                        {
                            SafeZones.Add(cellPos);
                        }
                    }
                }
            }

            // 가장 안전한 10개 위치로 제한
            if (SafeZones.Count > 10)
            {
                SafeZones.Sort((a, b) =>
                {
                    float threatA = GetThreatAt(a);
                    float threatB = GetThreatAt(b);
                    return threatA.CompareTo(threatB);
                });
                SafeZones = SafeZones.GetRange(0, 10);
            }
        }

        private void ComputeTeamThreatLevel()
        {
            if (_allies.Count == 0)
            {
                TeamThreatLevel = 1.0f;
                return;
            }

            // 각 아군 위치의 평균 위협도
            float totalThreat = 0f;
            int count = 0;

            foreach (var ally in _allies)
            {
                if (ally == null) continue;
                totalThreat += GetThreatAt(ally.Position);
                count++;
            }

            // 정규화 (0-1 범위)
            float avgThreat = count > 0 ? totalThreat / count : 0f;
            TeamThreatLevel = Mathf.Clamp01(avgThreat / 10f);  // 10 = 높은 위협 기준값
        }

        #endregion

        #region Unit Value Calculation

        private float GetUnitThreatValue(BaseUnitEntity enemy)
        {
            if (enemy == null) return 0f;

            try
            {
                float baseThreat = 1.0f;

                // HP 비율에 따른 위협도 (죽어가는 적은 덜 위협적)
                float hpPercent = CombatAPI.GetHPPercent(enemy);
                float hpFactor = hpPercent / 100f;
                baseThreat *= (0.5f + 0.5f * hpFactor);

                // 무기 타입에 따른 위협도
                bool hasRanged = CombatAPI.HasRangedWeapon(enemy);
                if (hasRanged)
                {
                    baseThreat *= 1.3f;  // 원거리 적은 더 넓은 영역에 위협
                }

                return baseThreat;
            }
            catch
            {
                return 1.0f;
            }
        }

        private float GetUnitStrengthValue(BaseUnitEntity ally)
        {
            if (ally == null) return 0f;

            try
            {
                float baseStrength = 1.0f;

                // HP 비율에 따른 통제력
                float hpPercent = CombatAPI.GetHPPercent(ally);
                float hpFactor = hpPercent / 100f;
                baseStrength *= (0.3f + 0.7f * hpFactor);

                return baseStrength;
            }
            catch
            {
                return 1.0f;
            }
        }

        #endregion

        #region Grid Helpers

        private Vector3 GetCellWorldPosition(int x, int z)
        {
            return new Vector3(
                _gridOrigin.x + x * CELL_SIZE + CELL_SIZE / 2f,
                0,
                _gridOrigin.z + z * CELL_SIZE + CELL_SIZE / 2f
            );
        }

        private bool WorldToGrid(Vector3 worldPos, out int x, out int z)
        {
            x = Mathf.FloorToInt((worldPos.x - _gridOrigin.x) / CELL_SIZE);
            z = Mathf.FloorToInt((worldPos.z - _gridOrigin.z) / CELL_SIZE);

            return x >= 0 && x < _gridWidth && z >= 0 && z < _gridHeight;
        }

        #endregion

        #region Public Query Methods

        /// <summary>
        /// 특정 위치의 적 위협도 조회 (O(1))
        /// </summary>
        public float GetThreatAt(Vector3 position)
        {
            if (!_isValid) return 0f;

            if (WorldToGrid(position, out int x, out int z))
            {
                return _threatGrid[x, z];
            }

            // 그리드 외부: 실시간 계산
            return CalculateThreatAtPosition(position);
        }

        /// <summary>
        /// 특정 위치의 아군 통제력 조회 (O(1))
        /// </summary>
        public float GetControlAt(Vector3 position)
        {
            if (!_isValid) return 0f;

            if (WorldToGrid(position, out int x, out int z))
            {
                return _controlGrid[x, z];
            }

            // 그리드 외부: 실시간 계산
            return CalculateControlAtPosition(position);
        }

        /// <summary>
        /// ★ v3.2.25: 전선까지의 거리
        /// 양수 = 적 방향 (전선 너머), 음수 = 아군 방향 (전선 뒤)
        /// </summary>
        public float GetFrontlineDistance(Vector3 position)
        {
            if (!_isValid) return 0f;
            if (FrontlineDirection.magnitude < 0.01f) return 0f;

            // 전선에서 위치까지의 투영 거리 (FrontlineDirection 사용)
            Vector3 toPos = position - Frontline;
            float dist = Vector3.Dot(toPos, FrontlineDirection);

            return dist;  // 양수=적 방향, 음수=아군 방향
        }

        /// <summary>
        /// 위치가 안전 구역인지 확인
        /// </summary>
        public bool IsSafeZone(Vector3 position, float tolerance = 3.0f)
        {
            foreach (var safe in SafeZones)
            {
                if (Vector3.Distance(position, safe) < tolerance)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 가장 가까운 안전 구역
        /// </summary>
        public Vector3? GetNearestSafeZone(Vector3 position)
        {
            if (SafeZones.Count == 0) return null;

            Vector3 nearest = SafeZones[0];
            float nearestDist = Vector3.Distance(position, nearest);

            for (int i = 1; i < SafeZones.Count; i++)
            {
                float dist = Vector3.Distance(position, SafeZones[i]);
                if (dist < nearestDist)
                {
                    nearest = SafeZones[i];
                    nearestDist = dist;
                }
            }

            return nearest;
        }

        /// <summary>
        /// 위치의 영향력 균형 (Control - Threat, 양수=아군 우세)
        /// </summary>
        public float GetInfluenceBalance(Vector3 position)
        {
            return GetControlAt(position) - GetThreatAt(position);
        }

        /// <summary>
        /// ★ v3.5.00: 특정 위치의 엄폐 품질 조회 (O(1))
        /// 0.0 = 엄폐 없음, 0.5 = 절반 엄폐, 1.0 = 완전 엄폐
        /// </summary>
        public float GetCoverAt(Vector3 position)
        {
            if (!_isValid) return 0f;

            if (WorldToGrid(position, out int x, out int z))
            {
                return _coverGrid[x, z];
            }

            // 그리드 외부: 실시간 계산
            return CalculateCoverAtPosition(position);
        }

        /// <summary>
        /// ★ v3.5.00: PDF 방법론 기반 전술 점수 (O(1))
        /// TacticalScore = CoverMap × 0.4 + (1 - ThreatMap_normalized) × 0.6
        /// 값이 높을수록 좋은 위치
        /// </summary>
        public float GetCombinedScore(Vector3 position)
        {
            if (!_isValid) return 0.5f;

            float cover = GetCoverAt(position);
            float threat = GetThreatAt(position);

            // 위협도 정규화 (0-1 범위로, 10 = 높은 위협 기준)
            float normalizedThreat = Mathf.Clamp01(threat / 10f);

            // PDF 공식: CoverMap × 0.4 + (1 - ThreatMap) × 0.6
            float tacticalScore = cover * 0.4f + (1f - normalizedThreat) * 0.6f;

            return tacticalScore;
        }

        #endregion

        #region Fallback Calculations (그리드 외부용)

        private float CalculateThreatAtPosition(Vector3 position)
        {
            float threat = 0f;
            foreach (var enemy in _enemies)
            {
                if (enemy == null) continue;
                float dist = Vector3.Distance(position, enemy.Position);
                if (dist < MAX_INFLUENCE_DISTANCE)
                {
                    float enemyThreat = GetUnitThreatValue(enemy);
                    float effectiveDist = Mathf.Max(dist, MIN_DISTANCE);
                    threat += enemyThreat / (effectiveDist * effectiveDist);
                }
            }
            return threat;
        }

        private float CalculateControlAtPosition(Vector3 position)
        {
            float control = 0f;
            foreach (var ally in _allies)
            {
                if (ally == null) continue;
                float dist = Vector3.Distance(position, ally.Position);
                if (dist < MAX_INFLUENCE_DISTANCE)
                {
                    float allyStrength = GetUnitStrengthValue(ally);
                    float effectiveDist = Mathf.Max(dist, MIN_DISTANCE);
                    control += allyStrength / (effectiveDist * effectiveDist);
                }
            }
            return control;
        }

        /// <summary>
        /// ★ v3.5.00: 그리드 외부용 엄폐 계산
        /// </summary>
        private float CalculateCoverAtPosition(Vector3 position)
        {
            if (_enemies.Count == 0) return 1.0f;

            // 가장 가까운 적 찾기
            BaseUnitEntity nearestEnemy = null;
            float nearestDist = float.MaxValue;
            foreach (var enemy in _enemies)
            {
                if (enemy == null) continue;
                float dist = Vector3.Distance(position, enemy.Position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestEnemy = enemy;
                }
            }

            if (nearestEnemy == null) return 1.0f;

            var coverLevel = CombatAPI.GetCoverTypeAtPosition(position, nearestEnemy);
            switch (coverLevel)
            {
                case CombatAPI.CoverLevel.Full: return 1.0f;
                case CombatAPI.CoverLevel.Half: return 0.5f;
                default: return 0.0f;
            }
        }

        #endregion

        #region Debug

        public override string ToString()
        {
            if (!_isValid) return "[InfluenceMap] Invalid";

            return $"[InfluenceMap] Grid={_gridWidth}x{_gridHeight}, " +
                   $"Enemies={_enemies?.Count ?? 0}, Allies={_allies?.Count ?? 0}, " +
                   $"ThreatLevel={TeamThreatLevel:F2}, SafeZones={SafeZones.Count}";
        }

        #endregion
    }
}
