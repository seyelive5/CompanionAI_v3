using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.View.Covers;
using Pathfinding;
using UnityEngine;

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
        /// HideScore 5-tuple 계산. 모든 필드 [0, 1] 범위로 정규화됨 (★ v3.111.15 Phase C.1).
        /// [0] FullCoverComplete — 모든 적에게 ≥Full 엄폐 완성 여부 (0 or 1)
        /// [1] AnyCoverComplete  — 모든 적에게 ≥Half 엄폐 완성 여부 (0 or 1)
        /// [2] AnyCoverRatio     — ≥Half 엄폐 비율 (0~1)
        /// [3] FullCoverRatio    — ≥Full 엄폐 비율 (0~1)
        /// [4] HideValue         — per-enemy 평균 엄폐 품질 (0~1). hideCoverValues=[None=0, Half=0.0004, Full=0.02, Invisible=1]의 평균.
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

                    // ★ v3.110.19 fix (I2): validCount 증분은 LOS 평가가 완료된 후에만.
                    // 예외 발생 시 분모 부풀림 방지 (예외 적은 0 기여분으로 은폐도를 낮춰 보이게 함).
                    validCount++;
                }
                catch (System.Exception ex)
                {
                    if (Main.IsDebugEnabled)
                        Main.LogWarning($"[TileScorerPort] GetWarhammerLos failed for {enemy?.CharacterName}: {ex.Message}");
                }
            }

            if (validCount == 0) return result;

            result.FullCoverComplete = (fullOrInvisible == validCount) ? 1f : 0f;
            result.AnyCoverComplete  = (halfOrBetter == validCount) ? 1f : 0f;
            result.AnyCoverRatio     = (float)halfOrBetter / validCount;
            result.FullCoverRatio    = (float)fullOrInvisible / validCount;
            // ★ v3.111.15 Phase C.1: 적 수 비례 unbounded 합 → per-enemy 평균 [0, 1]로 정규화.
            //   Ratio 필드들이 이미 /validCount 패턴 사용 → 일관성. HideScore max 180 → 110.
            result.HideValue         = hideValue / validCount;
            return result;
        }

        /// <summary>
        /// ★ v3.110.22 Phase 4: 적별 이동능력 반영 안전거리 점수 (0=근접, 1=모두 안전).
        /// 게임 ProtectionTileScorer.CalculateStayingAwayScore 패턴.
        /// 적의 blueprint AP/이동속도 + Forward 방향 고려.
        /// threatMoveCells = (2 × AP_Blue_initial) / AP_per_cell — 이 적이 실질적으로
        /// 이동 가능한 최대 거리. 우리 거리가 이 범위 안이면 비례 점수, 초과면 1.0.
        /// Forward 반영: 적의 사각지대에서 더 유리한 거리 평가.
        /// </summary>
        public static float GetStayingAwayScore(
            CustomGridNodeBase node,
            BaseUnitEntity self,
            List<BaseUnitEntity> enemies)
        {
            if (node == null || self == null || enemies == null || enemies.Count == 0)
                return 0f;

            float sum = 0f;
            int count = 0;
            foreach (var enemy in enemies)
            {
                if (enemy == null || enemy.LifeState.IsDead) continue;
                var blueprint = enemy.Blueprint;
                if (blueprint == null) continue;

                try
                {
                    float initialAP = blueprint.WarhammerInitialAPBlue;
                    float apPerCell = Mathf.Max(1f, blueprint.WarhammerMovementApPerCell);
                    float threatMoveCells = (2f * initialAP) / apPerCell;

                    int distCells = Kingmaker.Utility.WarhammerGeometryUtils.DistanceToInCells(
                        node.Vector3Position, self.SizeRect, self.Forward,
                        enemy.Position, enemy.SizeRect, enemy.Forward);

                    // 적이 도달 가능 거리 이상이면 1.0, 안이면 비례
                    sum += Mathf.Min(distCells, threatMoveCells) / Mathf.Max(1f, threatMoveCells);
                    count++;
                }
                catch (System.Exception ex)
                {
                    if (Main.IsDebugEnabled)
                        Main.LogWarning($"[TileScorerPort] GetStayingAwayScore failed for {enemy?.CharacterName}: {ex.Message}");
                }
            }

            return count > 0 ? sum / count : 0f;
        }

        /// <summary>
        /// ★ v3.111.0 Phase 5: 적 예상 이동 위치까지 고려한 worst-case 엄폐 완성도.
        /// 게임 ProtectionTileScorer.GetEnsuredCovers 패턴.
        /// 각 적의 예상 이동 위치 중 "가장 나쁜 cover" (가장 노출된 각도)를 해당 적의
        /// ensured cover로 사용. 적이 어디로 움직여도 최소 이 정도 엄폐는 보장됨.
        /// predictedMoves가 null/빈이면 GetHideScoreComponents와 동일 (현재 위치만 체크).
        /// </summary>
        public static HideScoreComponents GetEnsuredCoverComponents(
            CustomGridNodeBase node,
            IntRect unitSizeRect,
            List<BaseUnitEntity> enemies,
            CompanionAI_v3.Analysis.PredictedEnemyMoves predictedMoves)
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

                try
                {
                    // 예상 이동 위치 목록 (없으면 현재 위치만)
                    var moveVariants = predictedMoves?.GetMovesFor(enemy);

                    // 각 예상 위치에서 node로의 LOS 계산 → worst cover 취함
                    LosCalculations.CoverType worstCover = LosCalculations.CoverType.Invisible;
                    bool anyEvaluated = false;

                    if (moveVariants != null && moveVariants.Count > 0)
                    {
                        foreach (var variantNode in moveVariants)
                        {
                            var customNode = variantNode as CustomGridNodeBase;
                            if (customNode == null) continue;
                            var los = LosCalculations.GetWarhammerLos(
                                customNode, enemy.SizeRect, node, unitSizeRect);
                            var coverType = los.CoverType;
                            // "worst" = smallest enum value (None < Half < Full < Invisible)
                            if (!anyEvaluated || coverType < worstCover)
                            {
                                worstCover = coverType;
                                anyEvaluated = true;
                            }
                        }
                    }

                    // Variants 없거나 모두 실패 → 현재 위치 기반 fallback
                    if (!anyEvaluated)
                    {
                        var enemyNode = enemy.Position.GetNearestNodeXZ() as CustomGridNodeBase;
                        if (enemyNode == null) continue;
                        var los = LosCalculations.GetWarhammerLos(
                            enemyNode, enemy.SizeRect, node, unitSizeRect);
                        worstCover = los.CoverType;
                    }

                    int idx = (int)worstCover;
                    if (idx >= 0 && idx < 4)
                    {
                        // hideCoverValues = [None=0, Half=0.0004, Full=0.02, Invisible=1]
                        hideValue += idx == 0 ? 0f : (idx == 1 ? 0.0004f : (idx == 2 ? 0.02f : 1f));
                    }

                    if (worstCover == LosCalculations.CoverType.Full ||
                        worstCover == LosCalculations.CoverType.Invisible)
                        fullOrInvisible++;
                    if (worstCover != LosCalculations.CoverType.None)
                        halfOrBetter++;

                    validCount++;
                }
                catch (System.Exception ex)
                {
                    if (Main.IsDebugEnabled)
                        Main.LogWarning($"[TileScorerPort] GetEnsuredCoverComponents failed for {enemy?.CharacterName}: {ex.Message}");
                }
            }

            if (validCount == 0) return result;

            result.FullCoverComplete = (fullOrInvisible == validCount) ? 1f : 0f;
            result.AnyCoverComplete  = (halfOrBetter == validCount) ? 1f : 0f;
            result.AnyCoverRatio     = (float)halfOrBetter / validCount;
            result.FullCoverRatio    = (float)fullOrInvisible / validCount;
            // ★ v3.111.15 Phase C.1: GetHideScoreComponents와 동일 정규화.
            result.HideValue         = hideValue / validCount;
            return result;
        }
    }
}
