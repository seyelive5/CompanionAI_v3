using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Pathfinding;
using CompanionAI_v3.GameInterface;

namespace CompanionAI_v3.Analysis
{
    /// <summary>
    /// ★ v3.111.3: 적별 예상 이동 위치 조회 (EnemyMoveCache 기반).
    /// 이전 v3.111.0은 직접 pathfinding → 750ms 블로킹 + 0% 성공 → 폐기.
    /// 신: Harmony로 게임의 AsyncUpdateEnemyMoveVariants를 후킹해 캐시 → 조회만 O(1).
    /// 비용 0ms/턴. 단, 첫 적 턴이 돌기 전엔 캐시 비어있어 Phase 1a로 fallback됨.
    /// </summary>
    public class PredictedEnemyMoves
    {
        private readonly List<BaseUnitEntity> _trackedEnemies;

        private PredictedEnemyMoves(List<BaseUnitEntity> enemies)
        {
            _trackedEnemies = enemies;
        }

        public static PredictedEnemyMoves Compute(List<BaseUnitEntity> enemies)
        {
            var result = new PredictedEnemyMoves(enemies);
            if (Main.IsDebugEnabled)
            {
                int hits = 0;
                if (enemies != null)
                {
                    foreach (var e in enemies)
                    {
                        if (EnemyMoveCache.Get(e) != null) hits++;
                    }
                }
                Main.LogDebug($"[PredictedMoves] Cache query: {hits}/{enemies?.Count ?? 0} enemies have cached moves (total cache size: {EnemyMoveCache.Count})");
            }
            return result;
        }

        public List<GraphNode> GetMovesFor(BaseUnitEntity enemy)
        {
            return EnemyMoveCache.Get(enemy);
        }

        public int EnemyCount
        {
            get
            {
                if (_trackedEnemies == null) return 0;
                int n = 0;
                foreach (var e in _trackedEnemies)
                {
                    if (EnemyMoveCache.Get(e) != null) n++;
                }
                return n;
            }
        }
    }
}
