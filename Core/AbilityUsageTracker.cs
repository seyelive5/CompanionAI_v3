using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Kingmaker.UnitLogic.Abilities;

namespace CompanionAI_v3.Core
{
    /// <summary>
    /// 중앙화된 능력 사용 추적 시스템
    /// v2.2에서 포팅
    ///
    /// 설계 원칙:
    /// 1. 프레임 기반 자동 만료 - 전투 간 초기화 불필요
    /// 2. CombatAPI.HasActiveBuff와 함께 사용
    /// 3. 각 전략에서 개별 추적 코드 제거
    ///
    /// 사용 흐름:
    /// 1. HasActiveBuff() → 실제 버프 상태 확인 (1차)
    /// 2. WasUsedRecently() → 같은 결정 사이클 내 중복 방지 (2차)
    /// 3. MarkUsed() → 시전 시 기록
    /// </summary>
    public static class AbilityUsageTracker
    {
        // 유닛ID -> (능력ID -> 사용된 프레임)
        private static readonly Dictionary<string, Dictionary<string, int>> _usageByUnit
            = new Dictionary<string, Dictionary<string, int>>();

        // 실패한 능력 추적 (더 긴 쿨다운)
        private static readonly Dictionary<string, Dictionary<string, int>> _failedByUnit
            = new Dictionary<string, Dictionary<string, int>>();

        // 결정 사이클 내 중복 방지를 위한 프레임 윈도우
        // 60fps 기준 약 1초 (한 턴의 결정 사이클)
        private const int DEFAULT_FRAME_WINDOW = 60;

        // 실패한 능력의 쿨다운 (약 5초 - 전체 턴 동안)
        private const int FAILED_FRAME_WINDOW = 300;

        // 오래된 기록 정리 임계값 (약 15초)
        private const int CLEANUP_THRESHOLD = 900;

        /// <summary>
        /// 능력 사용 기록
        /// </summary>
        public static void MarkUsed(string unitId, string abilityId)
        {
            if (string.IsNullOrEmpty(unitId) || string.IsNullOrEmpty(abilityId))
                return;

            if (!_usageByUnit.TryGetValue(unitId, out var abilities))
            {
                abilities = new Dictionary<string, int>();
                _usageByUnit[unitId] = abilities;
            }

            abilities[abilityId] = Time.frameCount;
            Main.LogDebug($"[UsageTracker] Marked: {abilityId} for unit {unitId} at frame {Time.frameCount}");
        }

        /// <summary>
        /// 능력 사용 기록 (AbilityData 오버로드)
        /// </summary>
        public static void MarkUsed(string unitId, AbilityData ability)
        {
            if (ability == null) return;
            string abilityId = GetAbilityId(ability);
            MarkUsed(unitId, abilityId);
        }

        /// <summary>
        /// 최근에 사용했는지 확인 (프레임 윈도우 내)
        /// </summary>
        public static bool WasUsedRecently(string unitId, string abilityId, int frameWindow = DEFAULT_FRAME_WINDOW)
        {
            if (string.IsNullOrEmpty(unitId) || string.IsNullOrEmpty(abilityId))
                return false;

            if (!_usageByUnit.TryGetValue(unitId, out var abilities))
                return false;

            if (!abilities.TryGetValue(abilityId, out int usedFrame))
                return false;

            int framesSince = Time.frameCount - usedFrame;
            bool wasRecent = framesSince <= frameWindow;

            if (wasRecent)
            {
                Main.LogDebug($"[UsageTracker] {abilityId} was used {framesSince} frames ago (within {frameWindow})");
            }

            return wasRecent;
        }

        /// <summary>
        /// 최근에 사용했는지 확인 (AbilityData 오버로드)
        /// </summary>
        public static bool WasUsedRecently(string unitId, AbilityData ability, int frameWindow = DEFAULT_FRAME_WINDOW)
        {
            if (ability == null) return false;
            string abilityId = GetAbilityId(ability);
            return WasUsedRecently(unitId, abilityId, frameWindow);
        }

        /// <summary>
        /// 특정 타겟에 대해 최근 사용했는지 확인
        /// (버프를 특정 아군에게 건 경우 등)
        /// </summary>
        public static bool WasUsedOnTargetRecently(string unitId, string abilityId, string targetId, int frameWindow = DEFAULT_FRAME_WINDOW)
        {
            string pairKey = $"{abilityId}:{targetId}";
            return WasUsedRecently(unitId, pairKey, frameWindow);
        }

        /// <summary>
        /// 특정 타겟에 대해 능력 사용 기록
        /// </summary>
        public static void MarkUsedOnTarget(string unitId, string abilityId, string targetId)
        {
            string pairKey = $"{abilityId}:{targetId}";
            MarkUsed(unitId, pairKey);
        }

        /// <summary>
        /// 특정 타겟에 대해 능력 사용 기록 (AbilityData 오버로드)
        /// </summary>
        public static void MarkUsedOnTarget(string unitId, AbilityData ability, string targetId)
        {
            if (ability == null) return;
            string abilityId = GetAbilityId(ability);
            MarkUsedOnTarget(unitId, abilityId, targetId);
        }

        /// <summary>
        /// 능력 실패 기록
        /// 실패한 능력은 더 긴 쿨다운 적용
        /// </summary>
        public static void MarkFailed(string unitId, string abilityId)
        {
            if (string.IsNullOrEmpty(unitId) || string.IsNullOrEmpty(abilityId))
                return;

            if (!_failedByUnit.TryGetValue(unitId, out var abilities))
            {
                abilities = new Dictionary<string, int>();
                _failedByUnit[unitId] = abilities;
            }

            abilities[abilityId] = Time.frameCount;
            Main.Log($"[UsageTracker] ★ FAILED: {abilityId} for unit {unitId} at frame {Time.frameCount}");
        }

        /// <summary>
        /// 능력 실패 기록 (AbilityData 오버로드)
        /// </summary>
        public static void MarkFailed(string unitId, AbilityData ability)
        {
            if (ability == null) return;
            string abilityId = GetAbilityId(ability);
            MarkFailed(unitId, abilityId);
        }

        /// <summary>
        /// 최근에 실패했는지 확인
        /// </summary>
        public static bool HasFailedRecently(string unitId, string abilityId, int frameWindow = FAILED_FRAME_WINDOW)
        {
            if (string.IsNullOrEmpty(unitId) || string.IsNullOrEmpty(abilityId))
                return false;

            if (!_failedByUnit.TryGetValue(unitId, out var abilities))
                return false;

            if (!abilities.TryGetValue(abilityId, out int failedFrame))
                return false;

            int framesSince = Time.frameCount - failedFrame;
            bool failedRecently = framesSince <= frameWindow;

            if (failedRecently)
            {
                Main.LogDebug($"[UsageTracker] {abilityId} FAILED {framesSince} frames ago (cooldown {frameWindow})");
            }

            return failedRecently;
        }

        /// <summary>
        /// 최근에 실패했는지 확인 (AbilityData 오버로드)
        /// </summary>
        public static bool HasFailedRecently(string unitId, AbilityData ability)
        {
            if (ability == null) return false;
            string abilityId = GetAbilityId(ability);
            return HasFailedRecently(unitId, abilityId);
        }

        /// <summary>
        /// AbilityData에서 고유 ID 추출
        /// GUID 우선, 없으면 이름 사용
        /// </summary>
        public static string GetAbilityId(AbilityData ability)
        {
            if (ability == null) return "";
            return ability.Blueprint?.AssetGuid?.ToString() ?? ability.Name ?? "unknown";
        }

        /// <summary>
        /// 오래된 기록 정리 (메모리 관리)
        /// 선택적으로 호출 - 전투 종료 시 등
        /// </summary>
        public static void CleanupOldEntries()
        {
            int currentFrame = Time.frameCount;
            int cleanedCount = 0;

            foreach (var unitAbilities in _usageByUnit.Values)
            {
                var keysToRemove = unitAbilities
                    .Where(kvp => currentFrame - kvp.Value > CLEANUP_THRESHOLD)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    unitAbilities.Remove(key);
                    cleanedCount++;
                }
            }

            // 빈 유닛 항목 제거
            var emptyUnits = _usageByUnit
                .Where(kvp => kvp.Value.Count == 0)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var unitId in emptyUnits)
            {
                _usageByUnit.Remove(unitId);
            }

            // 실패 기록도 정리
            foreach (var unitAbilities in _failedByUnit.Values)
            {
                var keysToRemove = unitAbilities
                    .Where(kvp => currentFrame - kvp.Value > CLEANUP_THRESHOLD)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    unitAbilities.Remove(key);
                    cleanedCount++;
                }
            }

            var emptyFailedUnits = _failedByUnit
                .Where(kvp => kvp.Value.Count == 0)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var unitId in emptyFailedUnits)
            {
                _failedByUnit.Remove(unitId);
            }

            if (cleanedCount > 0)
            {
                Main.LogDebug($"[UsageTracker] Cleaned up {cleanedCount} old entries");
            }
        }

        /// <summary>
        /// 전체 초기화 (필요 시)
        /// 프레임 기반이므로 보통 필요 없음
        /// </summary>
        public static void ClearAll()
        {
            _usageByUnit.Clear();
            _failedByUnit.Clear();
            Main.LogDebug("[UsageTracker] All tracking cleared");
        }

        /// <summary>
        /// ★ v3.0.76: 특정 유닛의 추적 기록 초기화
        /// 턴 시작 시 호출하여 이전 턴 기록 정리
        /// </summary>
        public static void ClearForUnit(string unitId)
        {
            if (string.IsNullOrEmpty(unitId)) return;

            _usageByUnit.Remove(unitId);
            _failedByUnit.Remove(unitId);
            Main.LogDebug($"[UsageTracker] Cleared tracking for unit {unitId}");
        }

        /// <summary>
        /// 디버그: 현재 추적 상태 출력
        /// </summary>
        public static string GetDebugStatus()
        {
            int totalUnits = _usageByUnit.Count;
            int totalAbilities = _usageByUnit.Values.Sum(d => d.Count);
            int failedUnits = _failedByUnit.Count;
            int failedAbilities = _failedByUnit.Values.Sum(d => d.Count);
            return $"[UsageTracker] Units: {totalUnits}, Abilities: {totalAbilities}, Failed: {failedAbilities}, Frame: {Time.frameCount}";
        }
    }
}
