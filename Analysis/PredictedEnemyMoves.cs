using System;
using System.Collections.Generic;
using Kingmaker.AI;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Pathfinding;

namespace CompanionAI_v3.Analysis
{
    /// <summary>
    /// ★ v3.111.0 Phase 5: 적별 예상 이동 위치 집합.
    /// 게임 AsyncTaskNodeInitializeDecisionContext 패턴 — 각 적이 이 턴 이동 가능한
    /// 모든 노드를 계산해 HideScore 등에서 worst-case cover 평가에 사용.
    ///
    /// 턴 시작 1회 계산 + SituationAnalyzer continuation 캐시 재사용.
    /// 성능: 적 5명, 150ms/적 타임아웃 → ~750ms/turn start, 캐시 히트 시 0ms.
    /// maxPathLen=2 (게임 기본): 적이 실질적으로 이 턴 도달 가능한 범위.
    /// </summary>
    public class PredictedEnemyMoves
    {
        private readonly Dictionary<BaseUnitEntity, List<GraphNode>> _moves
            = new Dictionary<BaseUnitEntity, List<GraphNode>>();

        private const float MAX_PATH_LEN = 2.0f;
        private const int TIMEOUT_MS = 150;  // 적당 타임아웃

        public int EnemyCount => _moves.Count;

        public static PredictedEnemyMoves Compute(List<BaseUnitEntity> enemies)
        {
            var result = new PredictedEnemyMoves();
            if (enemies == null) return result;

            int totalComputed = 0;
            foreach (var enemy in enemies)
            {
                if (enemy == null || enemy.LifeState.IsDead) continue;
                try
                {
                    var nodes = ComputeSingleEnemy(enemy);
                    if (nodes != null && nodes.Count > 0)
                    {
                        result._moves[enemy] = nodes;
                        totalComputed += nodes.Count;
                    }
                }
                catch (Exception ex)
                {
                    if (Main.IsDebugEnabled)
                        Main.LogWarning($"[PredictedMoves] {enemy?.CharacterName} computation failed: {ex.Message}");
                }
            }

            if (Main.IsDebugEnabled)
                Main.LogDebug($"[PredictedMoves] Computed for {result._moves.Count} enemies, {totalComputed} total reachable nodes");

            return result;
        }

        private static List<GraphNode> ComputeSingleEnemy(BaseUnitEntity enemy)
        {
            var agent = enemy.View?.MovementAgent;
            if (agent == null) return null;

            // 위협 데이터는 우리가 필요 없음 — 적 이동만 파악 (API 시그니처 만족용)
            var threatsDict = AiBrainHelper.GatherThreatsData(enemy);
            if (threatsDict == null)
                threatsDict = new Dictionary<GraphNode, AiBrainHelper.ThreatsInfo>();

            var task = PathfindingService.Instance.FindAllReachableTiles_Delayed_Task(
                agent, enemy.Position, (int)MAX_PATH_LEN, threatsDict);

            if (!task.Wait(TIMEOUT_MS))
            {
                if (Main.IsDebugEnabled)
                    Main.LogDebug($"[PredictedMoves] {enemy.CharacterName}: timeout, skipped");
                return null;
            }

            var tiles = task.Result;
            if (tiles == null || tiles.Count == 0) return null;

            // 게임 선별 로직 (AsyncTaskNodeInitializeDecisionContext):
            // IsCanStand=true 또는 Length=0 은 항상 포함
            // Length<2 는 전부 포함
            // Length>=2 는 격자무늬 50% 샘플링
            var result = new List<GraphNode>(tiles.Count);
            foreach (var kvp in tiles)
            {
                var cell = kvp.Value;
                var node = kvp.Key;
                if (!cell.IsCanStand && cell.Length > 0) continue;
                if (cell.Length >= 2f)
                {
                    // 격자무늬 50% 샘플링 — node hash 기반 간단 선별
                    if ((node.GetHashCode() & 1) != 0) continue;
                }
                result.Add(node);
            }
            return result;
        }

        public List<GraphNode> GetMovesFor(BaseUnitEntity enemy)
        {
            if (enemy == null) return null;
            return _moves.TryGetValue(enemy, out var list) ? list : null;
        }

        /// <summary>전체 적의 예상 이동 위치를 순회.</summary>
        public IEnumerable<KeyValuePair<BaseUnitEntity, List<GraphNode>>> All => _moves;
    }
}
