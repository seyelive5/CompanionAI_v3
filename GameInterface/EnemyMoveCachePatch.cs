using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using Kingmaker.AI;
using Kingmaker.AI.BehaviourTrees.Nodes;
using Kingmaker.EntitySystem.Entities;
using Pathfinding;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// ★ v3.111.3: 게임이 적 턴 시작 시 계산하는 AiConsideredMoveVariants를 Harmony로 캡처.
    /// 이전 v3.111.0 Phase 5는 우리가 직접 pathfinding 호출 → 150ms × 5 = 750ms 블로킹 + 0% 성공.
    /// 게임은 async Task.WhenAll로 병렬 계산 (정상 작동) → 결과만 훔쳐 쓰기.
    ///
    /// Async Postfix 패턴: __result Task를 continuation으로 wrapping.
    /// 원본 Task 완료 후 TargetInfo.AiConsideredMoveVariants snapshot을 캐시에 복사.
    /// TargetInfo 객체는 풀링되므로 list reference 보관 금지 — 복사 필수.
    /// </summary>
    public static class EnemyMoveCache
    {
        private static readonly Dictionary<BaseUnitEntity, List<GraphNode>> _cache
            = new Dictionary<BaseUnitEntity, List<GraphNode>>();
        private static readonly object _lock = new object();

        /// <summary>스냅샷 저장 (list 복사). Postfix에서 호출.</summary>
        public static void Store(BaseUnitEntity enemy, List<GraphNode> moves)
        {
            if (enemy == null || moves == null) return;
            lock (_lock)
            {
                _cache[enemy] = new List<GraphNode>(moves);
            }
        }

        /// <summary>캐시된 예상 이동 위치 조회. 없으면 null.</summary>
        public static List<GraphNode> Get(BaseUnitEntity enemy)
        {
            if (enemy == null) return null;
            lock (_lock)
            {
                return _cache.TryGetValue(enemy, out var list) ? list : null;
            }
        }

        /// <summary>전체 캐시 크기 (디버그).</summary>
        public static int Count
        {
            get { lock (_lock) return _cache.Count; }
        }

        /// <summary>전투 종료 시 정리.</summary>
        public static void Clear()
        {
            lock (_lock) _cache.Clear();
        }
    }

    [HarmonyPatch]
    public static class EnemyMoveCachePatch
    {
        /// <summary>
        /// AsyncUpdateEnemyMoveVariants는 private async Task — Harmony Postfix는
        /// Task 반환 시점에 실행됨 (데이터 아직 미설정). __result Task에 continuation 붙여
        /// 완료 후 캡처.
        /// </summary>
        [HarmonyPatch(typeof(AsyncTaskNodeInitializeDecisionContext), "AsyncUpdateEnemyMoveVariants")]
        [HarmonyPostfix]
        public static void AsyncUpdateEnemyMoveVariants_Postfix(TargetInfo enemy, ref Task __result)
        {
            if (__result == null || enemy == null) return;
            var originalTask = __result;
            __result = CaptureAfterCompletion(originalTask, enemy);
        }

        private static async Task CaptureAfterCompletion(Task original, TargetInfo enemy)
        {
            await original;
            try
            {
                if (enemy?.Entity is BaseUnitEntity bue
                    && enemy.AiConsideredMoveVariants != null
                    && enemy.AiConsideredMoveVariants.Count > 0)
                {
                    EnemyMoveCache.Store(bue, enemy.AiConsideredMoveVariants);
                    if (Main.IsDebugEnabled)
                        Main.LogDebug($"[EnemyMoveCache] Captured {enemy.AiConsideredMoveVariants.Count} nodes for {bue.CharacterName}");
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled)
                    Main.LogWarning($"[EnemyMoveCache] Postfix capture failed: {ex.Message}");
            }
        }
    }
}
