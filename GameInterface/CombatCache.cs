using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// ★ v3.5.29: 전투 중 반복 계산 캐싱
    ///
    /// 성능 최적화 목적:
    /// - 거리 캐시: 유닛 쌍별 거리 (같은 턴 내 위치 불변) - 94% 히트율
    /// - 타겟팅 캐시: 능력-타겟 쌍별 사용 가능 여부 - 46-82% 히트율
    /// - HP% 캐시: 유닛별 HP 비율 (같은 계획 사이클 내 불변) ★ v3.8.60
    /// - 명중률 캐시: 능력-타겟 쌍별 명중률 (같은 턴 내 위치 불변) ★ v3.9.30
    ///
    /// 캐시 생명주기:
    /// - ClearAll(): 턴 시작 시 전체 캐시 클리어
    /// - InvalidateTarget(): 밀치기/이동 스킬 실행 후 해당 타겟만 무효화
    ///
    /// 성능 효과:
    /// - AnalyzeTargets(): 40+ CanUseAbilityOn 호출 → 캐시 히트
    /// - TargetScorer: 10+ GetDistance 호출 → 캐시 히트
    ///
    /// ★ v3.5.31: LOS 캐시 제거 - 0% 히트율 (같은 노드쌍이 재조회되지 않음)
    /// ★ v3.5.98: 거리 캐시를 타일 단위로 저장 (1 타일 = 1.35m)
    /// </summary>
    public static class CombatCache
    {
        #region Distance Cache

        /// <summary>
        /// ★ v3.5.98: 거리 캐시: (unitA_id, unitB_id) → distance (타일 단위)
        /// 양방향 대칭: GetDistanceInTiles(A,B) == GetDistanceInTiles(B,A)
        /// </summary>
        private static readonly Dictionary<(string, string), float> _distanceCache = new Dictionary<(string, string), float>();

        /// <summary>캐시 통계: 히트 횟수</summary>
        public static int DistanceHits { get; private set; }

        /// <summary>캐시 통계: 미스 횟수</summary>
        public static int DistanceMisses { get; private set; }

        /// <summary>
        /// ★ v3.5.98: 캐시된 거리 반환 (타일 단위)
        /// 모든 거리 비교에 이 함수 사용
        /// </summary>
        public static float GetDistanceInTiles(BaseUnitEntity a, BaseUnitEntity b)
        {
            if (a == null || b == null)
                return float.MaxValue;

            // 정규화된 키 (작은 ID가 먼저 오도록 - 양방향 대칭)
            var key = GetDistanceKey(a.UniqueId, b.UniqueId);

            if (_distanceCache.TryGetValue(key, out float dist))
            {
                DistanceHits++;
                return dist;
            }

            DistanceMisses++;
            dist = CombatAPI.GetDistanceInTiles(a, b);  // 타일 단위
            _distanceCache[key] = dist;
            return dist;
        }

        /// <summary>
        /// 캐시된 거리 반환 (미터 단위) - 하위 호환용
        /// ★ v3.5.98: 새 코드에서는 GetDistanceInTiles() 사용 권장
        /// </summary>
        public static float GetDistance(BaseUnitEntity a, BaseUnitEntity b)
        {
            // 타일 단위로 캐시된 값을 미터로 변환
            return GetDistanceInTiles(a, b) * CombatAPI.GridCellSize;
        }

        /// <summary>
        /// 거리 키 정규화 (A,B) == (B,A)
        /// </summary>
        private static (string, string) GetDistanceKey(string id1, string id2)
        {
            return string.CompareOrdinal(id1, id2) <= 0
                ? (id1, id2)
                : (id2, id1);
        }

        #endregion

        #region HP Percent Cache

        /// <summary>
        /// ★ v3.8.60: HP% 캐시: unit_id → HP percent (0-100)
        /// 같은 계획 사이클 내 동일 유닛의 HP를 반복 조회하지 않음
        /// 150-200회/턴 → 캐시 히트로 property 접근 제거
        /// </summary>
        private static readonly Dictionary<string, float> _hpPercentCache = new Dictionary<string, float>();

        /// <summary>캐시 통계</summary>
        public static int HPHits { get; private set; }
        public static int HPMisses { get; private set; }

        /// <summary>
        /// ★ v3.8.60: 캐시된 HP% 반환
        /// </summary>
        public static float GetHPPercent(BaseUnitEntity unit)
        {
            if (unit == null) return 0f;

            string key = unit.UniqueId;
            if (_hpPercentCache.TryGetValue(key, out float cached))
            {
                HPHits++;
                return cached;
            }

            HPMisses++;
            float hp = CombatAPI.GetHPPercent(unit);
            _hpPercentCache[key] = hp;
            return hp;
        }

        #endregion

        #region Hit Chance Cache

        /// <summary>
        /// ★ v3.9.30: 명중률 캐시: (abilityBlueprintName, targetId) → HitChanceInfo
        /// 같은 턴 내 동일 ability-target 쌍의 명중률은 불변
        /// TargetScorer + TacticalOptionEvaluator + UtilityScorer에서 공유
        /// </summary>
        private static readonly Dictionary<(string, string), CombatAPI.HitChanceInfo> _hitChanceCache
            = new Dictionary<(string, string), CombatAPI.HitChanceInfo>();

        /// <summary>캐시 통계</summary>
        public static int HitChanceHits { get; private set; }
        public static int HitChanceMisses { get; private set; }

        /// <summary>
        /// ★ v3.9.30: 캐시된 명중률 반환
        /// </summary>
        public static CombatAPI.HitChanceInfo GetHitChance(
            AbilityData ability, BaseUnitEntity attacker, BaseUnitEntity target)
        {
            if (ability == null || attacker == null || target == null)
                return null;

            string abilityKey = ability.Blueprint?.name ?? "unknown";
            string targetKey = target.UniqueId;
            var key = (abilityKey, targetKey);

            if (_hitChanceCache.TryGetValue(key, out var cached))
            {
                HitChanceHits++;
                return cached;
            }

            HitChanceMisses++;
            var result = CombatAPI.GetHitChance(ability, attacker, target);
            if (result != null)
                _hitChanceCache[key] = result;
            return result;
        }

        #endregion

        #region Targeting Cache

        /// <summary>
        /// 타겟팅 캐시: (ability_id, target_id) → (canUse, reason)
        /// </summary>
        private static readonly Dictionary<(string, string), (bool canUse, string reason)> _targetingCache = new Dictionary<(string, string), (bool, string)>();

        /// <summary>캐시 통계: 히트 횟수</summary>
        public static int TargetingHits { get; private set; }

        /// <summary>캐시 통계: 미스 횟수</summary>
        public static int TargetingMisses { get; private set; }

        /// <summary>
        /// 캐시된 타겟팅 체크
        /// </summary>
        public static bool CanUseAbilityOn(AbilityData ability, TargetWrapper target, out string reason)
        {
            if (ability == null || target == null)
            {
                reason = "Null parameter";
                return false;
            }

            // 키 생성: 능력 UniqueId + 타겟 Id
            // ★ v3.5.36: Point 좌표를 F1(0.1m 단위)로 반올림하여 캐시 히트율 향상
            string abilityId = ability.UniqueId ?? ability.Blueprint?.name ?? "unknown";
            string targetId = target.Entity?.UniqueId ?? $"point_{target.Point.x:F1}_{target.Point.z:F1}";
            var key = (abilityId, targetId);

            if (_targetingCache.TryGetValue(key, out var cached))
            {
                TargetingHits++;
                reason = cached.reason;
                return cached.canUse;
            }

            TargetingMisses++;
            bool canUse = CombatAPI.CanUseAbilityOn(ability, target, out reason);
            _targetingCache[key] = (canUse, reason);
            return canUse;
        }

        #endregion

        #region Cache Management

        /// <summary>
        /// 턴 시작 시 전체 캐시 클리어
        /// TurnOrchestrator.OnTurnStart()에서 호출
        /// </summary>
        public static void ClearAll()
        {
            int distCount = _distanceCache.Count;
            int targetCount = _targetingCache.Count;
            int hpCount = _hpPercentCache.Count;
            int hitChanceCount = _hitChanceCache.Count;

            _distanceCache.Clear();
            _targetingCache.Clear();
            _hpPercentCache.Clear();
            _hitChanceCache.Clear();  // ★ v3.9.30
            CombatAPI.ClearWeaponRangeCache();  // ★ v3.9.24: 무기 사거리 캐시도 클리어

            // 통계 로깅 (이전 턴의 캐시 효율)
            if (DistanceHits + DistanceMisses > 0 || TargetingHits + TargetingMisses > 0
                || HPHits + HPMisses > 0 || HitChanceHits + HitChanceMisses > 0)
            {
                float distHitRate = DistanceHits + DistanceMisses > 0
                    ? (float)DistanceHits / (DistanceHits + DistanceMisses) * 100f
                    : 0f;
                float targetHitRate = TargetingHits + TargetingMisses > 0
                    ? (float)TargetingHits / (TargetingHits + TargetingMisses) * 100f
                    : 0f;
                float hpHitRate = HPHits + HPMisses > 0
                    ? (float)HPHits / (HPHits + HPMisses) * 100f
                    : 0f;
                float hitChanceHitRate = HitChanceHits + HitChanceMisses > 0
                    ? (float)HitChanceHits / (HitChanceHits + HitChanceMisses) * 100f
                    : 0f;

                Main.LogDebug($"[CombatCache] Cleared: Distance({distCount}, {distHitRate:F0}%), " +
                             $"Targeting({targetCount}, {targetHitRate:F0}%), " +
                             $"HP({hpCount}, {hpHitRate:F0}%), " +
                             $"HitChance({hitChanceCount}, {hitChanceHitRate:F0}%)");
            }

            ResetStats();
        }

        /// <summary>
        /// 특정 타겟 관련 캐시만 무효화
        /// 밀치기/이동 스킬 실행 후 호출
        /// </summary>
        // ★ v3.8.48: 정적 리스트 재사용 (LINQ .ToList() 할당 제거)
        private static readonly List<(string, string)> _keysToRemove = new List<(string, string)>(32);

        public static void InvalidateTarget(BaseUnitEntity target)
        {
            if (target == null) return;

            var targetId = target.UniqueId;
            int invalidatedDist = 0;
            int invalidatedTarget = 0;

            // ★ v3.8.48: LINQ → 직접 순회 (0 할당)
            // 거리 캐시에서 해당 타겟 관련 항목 제거
            _keysToRemove.Clear();
            foreach (var key in _distanceCache.Keys)
            {
                if (key.Item1 == targetId || key.Item2 == targetId)
                    _keysToRemove.Add(key);
            }
            for (int i = 0; i < _keysToRemove.Count; i++)
            {
                _distanceCache.Remove(_keysToRemove[i]);
                invalidatedDist++;
            }

            // 타겟팅 캐시에서 해당 타겟 관련 항목 제거
            _keysToRemove.Clear();
            foreach (var key in _targetingCache.Keys)
            {
                if (key.Item2 == targetId || key.Item2.StartsWith("point_"))
                    _keysToRemove.Add(key);
            }
            for (int i = 0; i < _keysToRemove.Count; i++)
            {
                _targetingCache.Remove(_keysToRemove[i]);
                invalidatedTarget++;
            }

            // ★ v3.8.60: HP 캐시도 무효화 (데미지/힐 후 HP 변경)
            _hpPercentCache.Remove(targetId);

            // ★ v3.9.30: 명중률 캐시에서 해당 타겟 관련 항목 제거
            int invalidatedHitChance = 0;
            _keysToRemove.Clear();
            foreach (var key in _hitChanceCache.Keys)
            {
                if (key.Item2 == targetId)
                    _keysToRemove.Add(key);
            }
            for (int i = 0; i < _keysToRemove.Count; i++)
            {
                _hitChanceCache.Remove(_keysToRemove[i]);
                invalidatedHitChance++;
            }

            if (invalidatedDist > 0 || invalidatedTarget > 0 || invalidatedHitChance > 0)
            {
                Main.LogDebug($"[CombatCache] Invalidated for {target.CharacterName}: " +
                             $"Distance={invalidatedDist}, Targeting={invalidatedTarget}, HP=1, HitChance={invalidatedHitChance}");
            }
        }

        /// <summary>
        /// 시전자가 이동한 후 시전자 관련 캐시 무효화
        /// (시전자 위치가 바뀌면 LOS/거리가 변함)
        /// </summary>
        public static void InvalidateCaster(BaseUnitEntity caster)
        {
            if (caster == null) return;

            var casterId = caster.UniqueId;
            int invalidated = 0;

            // ★ v3.8.48: LINQ → 직접 순회 (0 할당)
            // 거리 캐시에서 시전자 관련 항목 제거
            _keysToRemove.Clear();
            foreach (var key in _distanceCache.Keys)
            {
                if (key.Item1 == casterId || key.Item2 == casterId)
                    _keysToRemove.Add(key);
            }
            for (int i = 0; i < _keysToRemove.Count; i++)
            {
                _distanceCache.Remove(_keysToRemove[i]);
                invalidated++;
            }

            // ★ v3.8.48: .Keys.ToList() → .Clear() (전부 지우는 거니까)
            // 타겟팅 캐시에서 시전자 능력 관련 항목 제거
            // 주의: 시전자 이동 후 능력의 타겟팅 결과가 달라질 수 있음
            int targetingCleared = _targetingCache.Count;
            _targetingCache.Clear();

            // ★ v3.9.30: 시전자 이동 → 명중률 변경 (거리/LOS 변화) → 전체 클리어
            int hitChanceCleared = _hitChanceCache.Count;
            _hitChanceCache.Clear();

            if (invalidated > 0 || targetingCleared > 0 || hitChanceCleared > 0)
            {
                Main.LogDebug($"[CombatCache] Caster moved {caster.CharacterName}: cleared {invalidated} distance, {targetingCleared} targeting, {hitChanceCleared} hitChance");
            }
        }

        /// <summary>
        /// 통계 초기화
        /// </summary>
        private static void ResetStats()
        {
            DistanceHits = 0;
            DistanceMisses = 0;
            TargetingHits = 0;
            TargetingMisses = 0;
            HPHits = 0;
            HPMisses = 0;
            HitChanceHits = 0;
            HitChanceMisses = 0;
        }

        #endregion

        #region Debug

        /// <summary>
        /// 현재 캐시 상태 출력 (디버그용)
        /// </summary>
        public static string GetCacheStatus()
        {
            float distHitRate = DistanceHits + DistanceMisses > 0
                ? (float)DistanceHits / (DistanceHits + DistanceMisses) * 100f
                : 0f;
            float targetHitRate = TargetingHits + TargetingMisses > 0
                ? (float)TargetingHits / (TargetingHits + TargetingMisses) * 100f
                : 0f;

            float hpHitRate = HPHits + HPMisses > 0
                ? (float)HPHits / (HPHits + HPMisses) * 100f
                : 0f;
            float hitChanceHitRate = HitChanceHits + HitChanceMisses > 0
                ? (float)HitChanceHits / (HitChanceHits + HitChanceMisses) * 100f
                : 0f;

            return $"Distance: {_distanceCache.Count} ({distHitRate:F0}%), " +
                   $"Targeting: {_targetingCache.Count} ({targetHitRate:F0}%), " +
                   $"HP: {_hpPercentCache.Count} ({hpHitRate:F0}%), " +
                   $"HitChance: {_hitChanceCache.Count} ({hitChanceHitRate:F0}%)";
        }

        #endregion
    }
}
