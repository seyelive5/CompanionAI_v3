using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Pathfinding;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;

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

            int total = enemies?.Count ?? 0;
            int hits = 0;
            if (enemies != null)
            {
                foreach (var e in enemies)
                {
                    if (EnemyMoveCache.Get(e) != null) hits++;
                }
            }

            // 계측: 캐시가 없는 적은 엄폐/노출이 "적의 현재 위치" 기준으로만 평가된다
            //   (TileScorerPort.GetEnsuredCoverComponents 의 fallback 경로). 즉 아직 행동하지 않은 적은
            //   "지금 자리에 계속 있을 것"으로 가정되어, 그 적에게 노출될 타일이 안전해 보일 수 있다.
            //   실제 전투에서 미보유 비율이 얼마인지 알아야 보정 필요성을 판단할 수 있으므로,
            //   불완전할 때만 기본 로그 레벨로 남긴다 (완전하면 Debug — 정상 상태는 조용히).
            if (total > 0 && hits < total)
            {
                int round = -1;
                try { round = Kingmaker.Game.Instance?.TurnController?.CombatRound ?? -1; }
                catch { /* 라운드 번호는 계측 부가정보 — 실패해도 본 로그는 남긴다 */ }

                Log.Analysis.Info($"[PredictedMoves] R{round}: {hits}/{total} enemies have predicted moves — " +
                    $"{total - hits} uncached (their cover/exposure judged from current position only)");
            }
            else if (Main.IsDebugEnabled)
            {
                Log.Analysis.Debug($"[PredictedMoves] Cache query: {hits}/{total} enemies have cached moves (total cache size: {EnemyMoveCache.Count})");
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
