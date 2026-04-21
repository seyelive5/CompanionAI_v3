using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.View.Covers;
using Pathfinding;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// ★ v3.110.19: 게임의 ProtectionTileScorer 패턴 직접 이식.
    /// 수동 재구현 없이 게임 API(LosCalculations)만 사용.
    /// 참조: Kingmaker.AI.AreaScanning.TileScorers.ProtectionTileScorer
    /// </summary>
    public static class TileScorerPort
    {
        /// <summary>
        /// HideScore 5-tuple 계산. 각 값 0~1 범위.
        /// [0] FullCoverComplete — 모든 적에게 ≥Full 엄폐 완성 여부 (0 or 1)
        /// [1] AnyCoverComplete  — 모든 적에게 ≥Half 엄폐 완성 여부 (0 or 1)
        /// [2] AnyCoverRatio     — ≥Half 엄폐 비율 (0~1)
        /// [3] FullCoverRatio    — ≥Full 엄폐 비율 (0~1)
        /// [4] HideValue         — 가중 aggregate (게임 hideCoverValues 역수 기반)
        /// </summary>
        public struct HideScoreComponents
        {
            public float FullCoverComplete;
            public float AnyCoverComplete;
            public float AnyCoverRatio;
            public float FullCoverRatio;
            public float HideValue;
        }

        // 게임 상수: hideCoverValues = [None=0, Half=0.0004, Full=0.02, Invisible=1]
        private static readonly float[] hideCoverValues = { 0f, 0.0004f, 0.02f, 1f };

        public static HideScoreComponents GetHideScoreComponents(
            CustomGridNodeBase node,
            IntRect unitSizeRect,
            List<BaseUnitEntity> enemies)
        {
            var result = new HideScoreComponents();
            if (enemies == null || enemies.Count == 0 || node == null) return result;

            int validCount = 0;
            int fullOrInvisible = 0;
            int halfOrBetter = 0;
            float hideValue = 0f;

            foreach (var enemy in enemies)
            {
                if (enemy == null || enemy.LifeState.IsDead) continue;
                var enemyNode = enemy.Position.GetNearestNodeXZ() as CustomGridNodeBase;
                if (enemyNode == null) continue;

                validCount++;
                try
                {
                    var los = LosCalculations.GetWarhammerLos(
                        enemyNode, enemy.SizeRect, node, unitSizeRect);
                    var coverType = los.CoverType;
                    int idx = (int)coverType;
                    if (idx >= 0 && idx < hideCoverValues.Length)
                        hideValue += hideCoverValues[idx];

                    if (coverType == LosCalculations.CoverType.Full ||
                        coverType == LosCalculations.CoverType.Invisible)
                        fullOrInvisible++;
                    if (coverType != LosCalculations.CoverType.None)
                        halfOrBetter++;
                }
                catch { }
            }

            if (validCount == 0) return result;

            result.FullCoverComplete = (fullOrInvisible == validCount) ? 1f : 0f;
            result.AnyCoverComplete  = (halfOrBetter == validCount) ? 1f : 0f;
            result.AnyCoverRatio     = (float)halfOrBetter / validCount;
            result.FullCoverRatio    = (float)fullOrInvisible / validCount;
            result.HideValue         = hideValue;
            return result;
        }
    }
}
