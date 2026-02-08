using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.AI;
using Kingmaker.AI.AreaScanning;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.View.Covers;
using Pathfinding;
using UnityEngine;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// 이동 API - 위치 평가 및 최적 위치 찾기
    /// </summary>
    public static class MovementAPI
    {
        #region Path Threat Weights

        /// <summary>★ v3.8.59: 경로 위협 가중치 — 중복 제거, 단일 정의</summary>
        private const float WEIGHT_AOO = 20f;           // 기회공격 유발
        private const float WEIGHT_AOE_ENTRY = 15f;     // AoE 진입
        private const float WEIGHT_DAMAGING_AOE_STEP = 10f;  // 데미지 AoE 내 이동

        #endregion

        #region ★ v3.8.15: AI Pathfinding Cache (스터터링 방지)

        /// <summary>
        /// AI 패스파인딩 캐시 - 한 턴에 한 번만 계산
        /// 문제: FindAllReachableTilesWithThreatsSync가 한 턴에 4번까지 호출됨
        /// 해결: 첫 호출에서 캐싱하고 이후 호출에서 재사용
        /// </summary>
        private static string _cachedUnitId;
        private static float _cachedAP;
        private static Dictionary<GraphNode, WarhammerPathAiCell> _cachedAiTiles;
        private static int _cachedTurnNumber = -1;

        /// <summary>AI 패스파인딩 캐시 무효화</summary>
        public static void InvalidateAiPathCache()
        {
            _cachedUnitId = null;
            _cachedAP = 0;
            _cachedAiTiles = null;
            Main.LogDebug("[MovementAPI] AI pathfinding cache invalidated");
        }

        #endregion

        #region Position Scoring

        public class PositionScore
        {
            public CustomGridNodeBase Node { get; set; }
            public Vector3 Position => Node?.Vector3Position ?? Vector3.zero;

            public float CoverScore { get; set; }
            public float DistanceScore { get; set; }
            public float ThreatScore { get; set; }
            public float AttackScore { get; set; }
            public float APCost { get; set; }

            /// <summary>★ v3.2.00: 영향력 맵 기반 위협 점수</summary>
            public float InfluenceThreatScore { get; set; }

            /// <summary>★ v3.2.00: 아군 통제 구역 보너스</summary>
            public float InfluenceControlBonus { get; set; }

            /// <summary>★ v3.5.18: Blackboard 통합 - SharedTarget 접근 보너스</summary>
            public float SharedTargetBonus { get; set; }

            /// <summary>★ v3.5.18: Blackboard 통합 - 팀 전술 기반 조정</summary>
            public float TacticalAdjustment { get; set; }

            /// <summary>★ v3.5.41: Larian 방법론 - 경로 위험도 점수</summary>
            public float PathRiskScore { get; set; }

            /// <summary>★ v3.6.7: 명중률 보너스 (원거리 공격 시 최적 거리 보너스)</summary>
            public float HitChanceBonus { get; set; }

            /// <summary>★ v3.6.18: 실제 공격 가능 적 수 (CanTargetFromNode 검증)</summary>
            public int HittableEnemyCount { get; set; }

            /// <summary>★ v3.8.50: 근접 AOE 스플래시 보너스 (패턴 내 추가 적 수 기반)</summary>
            public float MeleeAoESplashBonus { get; set; }

            public float TotalScore => CoverScore + DistanceScore - ThreatScore + AttackScore
                                       - InfluenceThreatScore + InfluenceControlBonus
                                       + SharedTargetBonus + TacticalAdjustment
                                       - PathRiskScore + HitChanceBonus + MeleeAoESplashBonus;

            public bool CanStand { get; set; }
            public bool HasLosToEnemy { get; set; }
            public int ProvokedAttacks { get; set; }
            public LosCalculations.CoverType BestCover { get; set; }

            public override string ToString() =>
                $"Pos({Position.x:F1},{Position.z:F1}) Score={TotalScore:F1}" +
                (InfluenceThreatScore > 0 || InfluenceControlBonus > 0
                    ? $" [Inf:T{InfluenceThreatScore:F1}/C{InfluenceControlBonus:F1}]" : "") +
                (SharedTargetBonus > 0 ? $" [ST:{SharedTargetBonus:F1}]" : "") +
                (TacticalAdjustment != 0 ? $" [Tac:{TacticalAdjustment:F1}]" : "") +
                (PathRiskScore > 0 ? $" [Path:{PathRiskScore:F1}]" : "") +
                (HitChanceBonus != 0 ? $" [Hit:{HitChanceBonus:F1}]" : "") +
                (MeleeAoESplashBonus > 0 ? $" [Splash:{MeleeAoESplashBonus:F1}]" : "");
        }

        public enum MovementGoal
        {
            FindCover,
            MaintainDistance,
            ApproachEnemy,
            AttackPosition,
            Retreat,
            RangedAttackPosition
        }

        #endregion

        #region Tile Discovery

        public static Dictionary<GraphNode, WarhammerPathPlayerCell> FindAllReachableTilesSync(
            BaseUnitEntity unit,
            float? maxAP = null)
        {
            if (unit == null) return new Dictionary<GraphNode, WarhammerPathPlayerCell>();

            try
            {
                float ap = maxAP ?? unit.CombatState?.ActionPointsBlue ?? 0f;
                if (ap <= 0) return new Dictionary<GraphNode, WarhammerPathPlayerCell>();

                var agent = unit.View?.MovementAgent;
                if (agent == null) return new Dictionary<GraphNode, WarhammerPathPlayerCell>();

                var tiles = PathfindingService.Instance.FindAllReachableTiles_Blocking(
                    agent,
                    unit.Position,
                    ap,
                    ignoreThreateningAreaCost: false
                );

                Main.LogDebug($"[MovementAPI] {unit.CharacterName}: Found {tiles?.Count ?? 0} reachable tiles");
                return tiles ?? new Dictionary<GraphNode, WarhammerPathPlayerCell>();
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[MovementAPI] FindAllReachableTilesSync error: {ex.Message}");
                return new Dictionary<GraphNode, WarhammerPathPlayerCell>();
            }
        }

        /// <summary>
        /// ★ v3.8.13: AI용 패스파인딩 - 경로 위협 데이터 포함 (ProvokedAttacks, EnteredAoE, StepsInsideDamagingAoE)
        /// ★ v3.8.15: 캐싱 추가 - 한 턴에 한 번만 계산하여 스터터링 방지
        ///
        /// 게임의 AI와 동일한 메서드 사용 (AiAreaScanner.FindAllReachableNodesAsync 참조)
        ///
        /// 핵심 차이점:
        /// - FindAllReachableTilesSync: WarhammerPathPlayerCell 반환 (위협 데이터 없음)
        /// - FindAllReachableTilesWithThreatsSync: WarhammerPathAiCell 반환 (위협 데이터 포함)
        /// </summary>
        public static Dictionary<GraphNode, WarhammerPathAiCell> FindAllReachableTilesWithThreatsSync(
            BaseUnitEntity unit,
            float? maxAP = null)
        {
            if (unit == null) return new Dictionary<GraphNode, WarhammerPathAiCell>();

            try
            {
                float ap = maxAP ?? unit.CombatState?.ActionPointsBlue ?? 0f;
                if (ap <= 0) return new Dictionary<GraphNode, WarhammerPathAiCell>();

                string unitId = unit.UniqueId;
                int currentTurn = Game.Instance?.TurnController?.CombatRound ?? -1;

                // ★ v3.8.15: 캐시 체크 - 같은 유닛, 같은 턴, 같은 AP면 캐시된 결과 반환
                if (_cachedAiTiles != null &&
                    _cachedUnitId == unitId &&
                    _cachedTurnNumber == currentTurn &&
                    Math.Abs(_cachedAP - ap) < 0.1f)
                {
                    Main.LogDebug($"[MovementAPI] {unit.CharacterName}: Using cached AI pathfinding ({_cachedAiTiles.Count} tiles)");
                    return _cachedAiTiles;
                }

                var agent = unit.View?.MovementAgent;
                if (agent == null) return new Dictionary<GraphNode, WarhammerPathAiCell>();

                // ★ 게임 AI와 동일: 먼저 위협 데이터 수집
                var threatsDict = AiBrainHelper.GatherThreatsData(unit);

                // ★ AI용 패스파인딩 호출 (비동기 → 동기 변환)
                // 참고: 이 호출은 AI 턴 처리 중이므로 블로킹해도 안전
                var task = PathfindingService.Instance.FindAllReachableTiles_Delayed_Task(
                    agent,
                    unit.Position,
                    (int)ap,
                    threatsDict
                );

                // ★ v3.8.15: 타임아웃 200ms로 축소 (기존 1000ms → 끊김 원인)
                if (!task.Wait(200))
                {
                    Main.LogDebug($"[MovementAPI] {unit.CharacterName}: AI pathfinding timeout, falling back to player version");
                    var fallback = ConvertToAiCells(FindAllReachableTilesSync(unit, maxAP));
                    // 폴백 결과도 캐싱
                    _cachedUnitId = unitId;
                    _cachedAP = ap;
                    _cachedAiTiles = fallback;
                    _cachedTurnNumber = currentTurn;
                    return fallback;
                }

                var tiles = task.Result;
                if (tiles == null || tiles.Count == 0)
                {
                    Main.LogDebug($"[MovementAPI] {unit.CharacterName}: AI pathfinding returned null/empty, falling back");
                    var fallback = ConvertToAiCells(FindAllReachableTilesSync(unit, maxAP));
                    _cachedUnitId = unitId;
                    _cachedAP = ap;
                    _cachedAiTiles = fallback;
                    _cachedTurnNumber = currentTurn;
                    return fallback;
                }

                // ★ v3.8.15: 결과 캐싱
                _cachedUnitId = unitId;
                _cachedAP = ap;
                _cachedAiTiles = tiles;
                _cachedTurnNumber = currentTurn;

                // ★ 디버그: 경로 위협 데이터 샘플 출력 (첫 호출에서만)
                int threatsFound = 0;
                foreach (var kvp in tiles)
                {
                    var cell = kvp.Value;
                    if (cell.ProvokedAttacks > 0 || cell.EnteredAoE > 0 || cell.StepsInsideDamagingAoE > 0)
                    {
                        threatsFound++;
                        if (threatsFound <= 3)  // 최대 3개만 로깅
                        {
                            Main.LogDebug($"[MovementAPI] ThreatData: Node({cell.Position.x:F1},{cell.Position.z:F1}) AoO={cell.ProvokedAttacks}, AoE={cell.EnteredAoE}, DmgAoE={cell.StepsInsideDamagingAoE}");
                        }
                    }
                }

                Main.Log($"[MovementAPI] {unit.CharacterName}: AI pathfinding found {tiles.Count} tiles, {threatsFound} with threats");
                return tiles;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[MovementAPI] FindAllReachableTilesWithThreatsSync error: {ex.Message}");
                return ConvertToAiCells(FindAllReachableTilesSync(unit, maxAP));
            }
        }

        /// <summary>
        /// ★ v3.8.13: WarhammerPathPlayerCell → WarhammerPathAiCell 변환 (폴백용)
        /// 위협 데이터는 0으로 설정 (플레이어 셀에는 없음)
        /// </summary>
        private static Dictionary<GraphNode, WarhammerPathAiCell> ConvertToAiCells(
            Dictionary<GraphNode, WarhammerPathPlayerCell> playerCells)
        {
            var result = new Dictionary<GraphNode, WarhammerPathAiCell>();
            if (playerCells == null) return result;

            foreach (var kvp in playerCells)
            {
                var pc = kvp.Value;
                var node = pc.Node as CustomGridNodeBase;
                if (node == null) continue;

                result[kvp.Key] = new WarhammerPathAiCell(
                    pc.Position,
                    pc.DiagonalsCount,
                    pc.Length,
                    pc.Node,
                    pc.ParentNode,
                    pc.IsCanStand,
                    0,  // EnteredAoE - 플레이어 셀에는 없음
                    0,  // StepsInsideDamagingAoE
                    0   // ProvokedAttacks
                );
            }
            return result;
        }

        #endregion

        #region Threat Detection

        /// <summary>
        /// ★ v3.0.62: AoE/함정 위협 점수 계산
        /// AiBrainHelper.TryFindThreats를 사용하여 해당 노드의 위협 평가
        /// </summary>
        public static float CalculateThreatScore(BaseUnitEntity unit, CustomGridNodeBase node)
        {
            if (unit == null || node == null) return 0f;

            float threatScore = 0f;

            try
            {
                var threats = AiBrainHelper.TryFindThreats(unit, node);
                if (threats == null) return 0f;

                // AoO 위협 (기습공격 유발)
                if (threats.aooUnits != null && threats.aooUnits.Count > 0)
                {
                    threatScore += threats.aooUnits.Count * 20f;
                    Main.LogDebug($"[MovementAPI] Node has {threats.aooUnits.Count} AoO threats");
                }

                // Overwatch 위협 (경계사격)
                if (threats.overwatchUnits != null && threats.overwatchUnits.Count > 0)
                {
                    threatScore += threats.overwatchUnits.Count * 25f;
                    Main.LogDebug($"[MovementAPI] Node has {threats.overwatchUnits.Count} Overwatch threats");
                }

                // AoE 위협 (화염, 독가스 등)
                if (threats.aes != null && threats.aes.Count > 0)
                {
                    threatScore += threats.aes.Count * 30f;
                    Main.LogDebug($"[MovementAPI] Node has {threats.aes.Count} AoE threats");
                }

                // 이동 시 데미지 AoE (화염 지대 등)
                if (threats.dmgOnMoveAes != null && threats.dmgOnMoveAes.Count > 0)
                {
                    threatScore += threats.dmgOnMoveAes.Count * 50f;
                    Main.LogDebug($"[MovementAPI] Node has {threats.dmgOnMoveAes.Count} damage-on-move AoE");
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[MovementAPI] CalculateThreatScore error: {ex.Message}");
            }

            return threatScore;
        }

        /// <summary>
        /// ★ v3.5.41: Larian Combat AI 방법론 - 경로 위험도 평가
        /// 시작점에서 끝점까지 경로 상의 모든 타일에 대해 위협 점수를 합산
        ///
        /// Larian AI 참조: MovementScore = A→B 경로상 PositionScore 합산
        /// 목적지만이 아닌 경로 전체의 위험도를 평가하여
        /// 안전한 경로를 선택하도록 유도
        /// </summary>
        /// <param name="unit">이동하는 유닛</param>
        /// <param name="startPos">시작 위치</param>
        /// <param name="endNode">목표 노드</param>
        /// <param name="pathCell">경로 셀 (경로 정보 포함)</param>
        /// <param name="influenceMap">영향력 맵 (위협 조회용)</param>
        /// <returns>경로 평균 위험도 (0 = 안전, 높을수록 위험)</returns>
        public static float EvaluatePathRisk(
            BaseUnitEntity unit,
            Vector3 startPos,
            CustomGridNodeBase endNode,
            WarhammerPathPlayerCell pathCell,
            BattlefieldInfluenceMap influenceMap)
        {
            // ★ v3.8.13: AI 셀로 변환하여 통합 메서드 호출
            var aiCell = new WarhammerPathAiCell(
                pathCell.Position,
                pathCell.DiagonalsCount,
                pathCell.Length,
                pathCell.Node,
                pathCell.ParentNode,
                pathCell.IsCanStand,
                0, 0, 0  // 플레이어 셀에는 위협 데이터 없음
            );
            return EvaluatePathRiskAi(unit, startPos, endNode, aiCell, influenceMap);
        }

        /// <summary>
        /// ★ v3.8.13: AI 셀용 경로 위험도 평가 (실제 경로 위협 데이터 활용)
        /// AI 패스파인더가 계산한 ProvokedAttacks, EnteredAoE, StepsInsideDamagingAoE를 직접 사용
        ///
        /// 핵심: 게임의 AI 패스파인더는 경로 전체의 위협을 누적 계산하여 셀에 저장
        /// - ProvokedAttacks: 해당 경로로 이동 시 유발되는 총 AoO 수
        /// - EnteredAoE: 경로에서 진입하는 AoE 구역 수
        /// - StepsInsideDamagingAoE: 피해 AoE 내에서 이동하는 총 칸 수
        /// </summary>
        public static float EvaluatePathRiskAi(
            BaseUnitEntity unit,
            Vector3 startPos,
            CustomGridNodeBase endNode,
            WarhammerPathAiCell pathCell,
            BattlefieldInfluenceMap influenceMap)
        {
            if (unit == null || endNode == null || pathCell.Node == null)
                return 0f;

            float totalRisk = 0f;

            try
            {
                // ★ v3.8.13: AI 셀의 경로 위협 데이터 직접 활용 (게임 패스파인더가 이미 계산함)
                // 이 값들은 경로 전체의 누적 위협이므로 별도 계산 불필요
                float pathProvokedAttacks = pathCell.ProvokedAttacks;
                float pathEnteredAoE = pathCell.EnteredAoE;
                float pathDamagingAoESteps = pathCell.StepsInsideDamagingAoE;

                // 경로 위협 점수 계산 (게임의 TileScorer와 유사한 가중치)
                // - AoO: 20점 (매우 위험 - 즉시 피해 + 행동 방해)
                // - AoE 진입: 15점 (위험 - 지속 피해 가능성)
                // - 피해 AoE 내 이동: 10점 (중간 - 매 칸 피해)
                totalRisk += pathProvokedAttacks * 20f;
                totalRisk += pathEnteredAoE * 15f;
                totalRisk += pathDamagingAoESteps * 10f;

                // ★ 디버그: 실제 경로 위협 데이터 로깅
                if (totalRisk > 0)
                {
                    Main.LogDebug($"[MovementAPI] PathRiskAi: AoO={pathProvokedAttacks}, AoE={pathEnteredAoE}, DmgAoE={pathDamagingAoESteps} -> Risk={totalRisk:F1}");
                }

                // 추가: 영향력 맵 기반 위협 (적 밀집도) - 목적지에서만 평가
                if (influenceMap != null && influenceMap.IsValid)
                {
                    float influenceThreat = influenceMap.GetThreatAt(endNode.Vector3Position);
                    totalRisk += influenceThreat * 3f;
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[MovementAPI] EvaluatePathRiskAi error: {ex.Message}");
                return 0f;
            }

            return totalRisk;
        }

        /// <summary>
        /// ★ v3.5.41: 단순 거리 기반 경로 위험도 평가 (ParentNode 없을 때 사용)
        /// 경로 정보 없이 시작점과 끝점 사이를 샘플링하여 위협 평가
        /// </summary>
        public static float EvaluatePathRiskSimple(
            BaseUnitEntity unit,
            Vector3 startPos,
            Vector3 endPos,
            BattlefieldInfluenceMap influenceMap)
        {
            if (unit == null || influenceMap == null || !influenceMap.IsValid)
                return 0f;

            float totalRisk = 0f;
            int sampleCount = 0;

            try
            {
                float distance = Vector3.Distance(startPos, endPos);
                int samples = Math.Max(3, (int)(distance / 3f));  // 3m 간격으로 샘플링

                for (int i = 0; i <= samples; i++)
                {
                    float t = samples > 0 ? (float)i / samples : 0f;
                    var samplePos = Vector3.Lerp(startPos, endPos, t);

                    float threatAtSample = influenceMap.GetThreatAt(samplePos);
                    totalRisk += threatAtSample;
                    sampleCount++;
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[MovementAPI] EvaluatePathRiskSimple error: {ex.Message}");
                return 0f;
            }

            return sampleCount > 0 ? totalRisk / sampleCount * 3f : 0f;
        }

        /// <summary>
        /// ★ v3.6.7: 명중률 기반 위치 보너스 계산
        /// ★ v3.6.8: Scatter/근접 공격 예외 처리 추가
        /// 최적 사거리(무기 사거리의 절반 이내) 위치에 보너스 부여
        ///
        /// 거리 계수(Distance Factor):
        /// - 1.0 = 최적 거리 (사거리 절반 이내) → +15 보너스
        /// - 0.5 = 중간 거리 (절반~최대) → +5 보너스
        /// - 0.0 = 사거리 초과 → -10 패널티
        /// - Scatter/근접 → 항상 0 (100% 명중, 거리 무관)
        /// </summary>
        /// <param name="position">평가할 위치</param>
        /// <param name="enemies">적 목록</param>
        /// <param name="weaponRange">무기 사거리 (타일 단위)</param>
        /// <param name="isScatter">★ v3.6.8: Scatter 공격 여부 (100% 명중)</param>
        /// <param name="isMelee">★ v3.6.8: 근접 공격 여부 (100% 명중)</param>
        /// <returns>명중률 보너스 점수</returns>
        public static float CalculateHitChanceBonus(
            Vector3 position,
            List<BaseUnitEntity> enemies,
            float weaponRange,
            bool isScatter = false,
            bool isMelee = false)
        {
            // ★ v3.6.8: Scatter/근접은 거리와 무관하게 100% 명중 → 보너스 불필요
            if (isScatter || isMelee)
                return 0f;

            if (enemies == null || enemies.Count == 0 || weaponRange <= 0)
                return 0f;

            float bestBonus = -10f;  // 기본값: 사거리 초과 패널티

            try
            {
                float optimalRange = weaponRange / 2f;  // 최적 거리 = 최대 사거리의 절반

                foreach (var enemy in enemies)
                {
                    if (enemy == null || enemy.LifeState.IsDead) continue;

                    // 타일 단위 거리 계산
                    float distTiles = CombatAPI.MetersToTiles(Vector3.Distance(position, enemy.Position));

                    float bonus;
                    if (distTiles <= optimalRange)
                    {
                        // 최적 거리 내 → +15 보너스
                        bonus = 15f;
                    }
                    else if (distTiles <= weaponRange)
                    {
                        // 중간 거리 (절반~최대) → +5 보너스
                        bonus = 5f;
                    }
                    else
                    {
                        // 사거리 초과 → -10 패널티
                        bonus = -10f;
                    }

                    // 최고 보너스 채택 (가장 유리한 적 기준)
                    if (bonus > bestBonus)
                        bestBonus = bonus;
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[MovementAPI] CalculateHitChanceBonus error: {ex.Message}");
                return 0f;
            }

            return bestBonus;
        }

        #endregion

        #region Position Evaluation

        public static List<PositionScore> EvaluateAllPositions(
            BaseUnitEntity unit,
            Dictionary<GraphNode, WarhammerPathAiCell> reachableTiles,
            List<BaseUnitEntity> enemies,
            MovementGoal goal,
            float targetDistance = 10f,
            float minSafeDistance = 5f)
        {
            var scores = new List<PositionScore>();
            if (unit == null || reachableTiles == null || reachableTiles.Count == 0)
                return scores;

            foreach (var kvp in reachableTiles)
            {
                var node = kvp.Key as CustomGridNodeBase;
                var cell = kvp.Value;

                if (node == null || !cell.IsCanStand)
                    continue;

                var score = EvaluatePosition(unit, node, cell, enemies, goal, targetDistance, minSafeDistance);
                scores.Add(score);
            }

            // ★ v3.8.48: 정렬 제거 (호출자가 필요 시 직접 정렬)
            return scores;
        }

        public static PositionScore EvaluatePosition(
            BaseUnitEntity unit,
            CustomGridNodeBase node,
            WarhammerPathAiCell cell,
            List<BaseUnitEntity> enemies,
            MovementGoal goal,
            float targetDistance = 10f,
            float minSafeDistance = 5f)
        {
            var score = new PositionScore
            {
                Node = node,
                CanStand = cell.IsCanStand,
                APCost = cell.Length,
                ProvokedAttacks = cell.ProvokedAttacks,
                BestCover = LosCalculations.CoverType.None
            };

            if (enemies == null || enemies.Count == 0)
                return score;

            float totalCoverScore = 0f;
            float nearestEnemyDist = float.MaxValue;
            bool hasAnyLos = false;

            foreach (var enemy in enemies)
            {
                if (enemy == null || enemy.LifeState.IsDead) continue;

                var enemyNode = enemy.Position.GetNearestNodeXZ() as CustomGridNodeBase;
                if (enemyNode == null) continue;

                // ★ v3.6.1: 타일 단위로 변환 (minSafeDistance가 타일 단위)
                float dist = CombatAPI.MetersToTiles(Vector3.Distance(node.Vector3Position, enemy.Position));
                if (dist < nearestEnemyDist) nearestEnemyDist = dist;

                try
                {
                    var los = LosCalculations.GetWarhammerLos(enemyNode, enemy.SizeRect, node, unit.SizeRect);
                    var coverType = los.CoverType;

                    if (coverType != LosCalculations.CoverType.Invisible) hasAnyLos = true;

                    switch (coverType)
                    {
                        case LosCalculations.CoverType.Invisible:
                            totalCoverScore += 40f;
                            break;
                        case LosCalculations.CoverType.Full:
                            totalCoverScore += 30f;
                            break;
                        case LosCalculations.CoverType.Half:
                            totalCoverScore += 15f;
                            break;
                    }

                    if (coverType > score.BestCover)
                        score.BestCover = coverType;
                }
                catch { }
            }

            score.CoverScore = totalCoverScore / Math.Max(1, enemies.Count);
            score.HasLosToEnemy = hasAnyLos;

            switch (goal)
            {
                case MovementGoal.FindCover:
                case MovementGoal.Retreat:
                    score.DistanceScore = Math.Min(30f, nearestEnemyDist * 2f);
                    break;

                case MovementGoal.MaintainDistance:
                    float distDiff = Math.Abs(nearestEnemyDist - targetDistance);
                    score.DistanceScore = Math.Max(0f, 20f - distDiff * 2f);
                    break;

                case MovementGoal.ApproachEnemy:
                    score.DistanceScore = Math.Max(0f, 30f - nearestEnemyDist * 2f);
                    break;

                case MovementGoal.AttackPosition:
                    if (nearestEnemyDist <= targetDistance && nearestEnemyDist >= 3f)
                        score.DistanceScore = 25f;
                    else if (nearestEnemyDist <= targetDistance)
                        score.DistanceScore = 15f;
                    else
                        score.DistanceScore = 0f;
                    break;

                case MovementGoal.RangedAttackPosition:
                    float weaponRange = targetDistance;

                    if (nearestEnemyDist < minSafeDistance)
                    {
                        score.DistanceScore = -50f + nearestEnemyDist * 5f;
                    }
                    else if (nearestEnemyDist <= weaponRange)
                    {
                        // ★ v3.0.46: Division by Zero 방지
                        float denominator = weaponRange - minSafeDistance;
                        float distRatio = denominator > 0.1f
                            ? (nearestEnemyDist - minSafeDistance) / denominator
                            : 0.5f;
                        score.DistanceScore = 20f + distRatio * 10f;
                    }
                    else
                    {
                        score.DistanceScore = Math.Max(0f, 10f - (nearestEnemyDist - weaponRange) * 2f);
                    }
                    break;
            }

            score.ThreatScore = cell.ProvokedAttacks * WEIGHT_AOO + cell.EnteredAoE * WEIGHT_AOE_ENTRY;

            if (hasAnyLos && nearestEnemyDist <= targetDistance)
                score.AttackScore = 20f;

            return score;
        }

        #endregion

        #region Best Position Finding

        /// <summary>
        /// ★ v3.0.62: AoE/위협 점수 통합
        /// ★ v3.1.01: predictedMP 파라미터 추가 - MP 회복 예측 후 이동 계획 지원
        /// ★ v3.2.00: influenceMap 파라미터 추가 - 영향력 맵 기반 위협/통제 점수
        /// ★ v3.2.25: role 파라미터 추가 - Role별 전선 위치 점수
        /// ★ v3.4.00: predictiveMap 파라미터 추가 - 적 이동 예측 기반 위협 점수
        /// </summary>
        public static PositionScore FindRangedAttackPositionSync(
            BaseUnitEntity unit,
            List<BaseUnitEntity> enemies,
            float weaponRange = 15f,
            float minSafeDistance = 5f,
            float predictedMP = 0f,
            BattlefieldInfluenceMap influenceMap = null,
            AIRole role = AIRole.Auto,
            Analysis.PredictiveThreatMap predictiveMap = null)
        {
            // ★ v3.8.13: AI용 패스파인딩 사용 - 경로 위협 데이터(ProvokedAttacks, EnteredAoE) 포함
            var tiles = predictedMP > 0
                ? FindAllReachableTilesWithThreatsSync(unit, predictedMP)
                : FindAllReachableTilesWithThreatsSync(unit);
            if (tiles == null || tiles.Count == 0)
            {
                Main.LogDebug($"[MovementAPI] {unit.CharacterName}: No reachable tiles (predictedMP={predictedMP:F1})");
                return null;
            }

            // ★ v3.8.13: 이제 AI 셀에 실제 경로 위협 데이터가 포함됨
            // BattlefieldGrid 검증만 추가로 수행
            var aiCells = new Dictionary<GraphNode, WarhammerPathAiCell>();
            foreach (var kvp in tiles)
            {
                var aiCell = kvp.Value;
                var node = aiCell.Node as CustomGridNodeBase;
                if (node == null || !aiCell.IsCanStand) continue;

                // ★ v3.7.62: BattlefieldGrid 검증 - Walkable/점유 체크
                if (!BattlefieldGrid.Instance.ValidateNode(unit, node))
                    continue;

                aiCells[kvp.Key] = aiCell;
            }

            var scores = EvaluateAllPositions(unit, aiCells, enemies, MovementGoal.RangedAttackPosition, weaponRange, minSafeDistance);

            // ★ v3.6.8: Scatter/근접 공격 여부 감지 (위치 보너스 계산에 사용)
            // Scatter/근접은 거리와 무관하게 100% 명중 → 거리 보너스 불필요
            var primaryAttack = CombatAPI.FindAnyAttackAbility(unit, Settings.RangePreference.PreferRanged);
            bool isScatter = CombatAPI.IsScatterAttack(primaryAttack);
            bool isMelee = primaryAttack?.IsMelee ?? false;

            // ★ v3.0.62: 위협 점수 추가 (AoE, AoO, Overwatch)
            // ★ v3.2.00: 영향력 맵 기반 위협/통제 점수 추가
            // ★ v3.5.41: 경로 위험도 점수 추가 (Larian 방법론)
            // ★ v3.6.7: 명중률 기반 위치 보너스 추가
            // ★ v3.6.8: Scatter/근접 예외 처리 추가
            foreach (var score in scores)
            {
                score.ThreatScore += CalculateThreatScore(unit, score.Node);

                // ★ v3.2.25: 영향력 맵 + Role별 Frontline 점수 적용
                // ★ v3.4.00: 예측 위협 맵 점수 추가
                if (influenceMap != null && influenceMap.IsValid)
                {
                    ApplyInfluenceScores(score, influenceMap, role, predictiveMap);

                    // ★ v3.8.13: AI 셀에서 직접 경로 위험도 평가 (실제 위협 데이터 활용)
                    // AI 패스파인더가 이미 경로 상의 AoO, AoE 진입 횟수를 계산해둠
                    var originalTile = tiles.Values.FirstOrDefault(t =>
                        t.Node == score.Node);

                    if (originalTile.Node != null)
                    {
                        // AI 셀의 경로 위협 데이터 직접 활용
                        score.PathRiskScore = EvaluatePathRiskAi(
                            unit, unit.Position, score.Node, originalTile, influenceMap);
                    }
                    else
                    {
                        // 폴백: 단순 샘플링 방식
                        score.PathRiskScore = EvaluatePathRiskSimple(
                            unit, unit.Position, score.Position, influenceMap);
                    }
                }

                // ★ v3.6.7/v3.6.8: 명중률 기반 위치 보너스 (Scatter/근접 예외)
                // 최적 사거리 내 위치에 높은 보너스, 멀수록 패널티
                score.HitChanceBonus = CalculateHitChanceBonus(score.Position, enemies, weaponRange, isScatter, isMelee);

                // ★ v3.6.18: 실제 공격 가능 적 수 계산 (CanTargetFromNode 검증)
                // 기본 LOS 체크만으로는 불충분 - 실제 능력 사용 가능 여부 확인 필요
                score.HittableEnemyCount = CombatAPI.CountHittableEnemiesFromPosition(
                    unit, score.Node, enemies, primaryAttack);
            }

            // ★ v3.8.48: O(n log n) 정렬 제거 → O(n) MaxByWhere 사용 (100+ 요소 최적화)
            // ★ v3.6.18: 실제 공격 가능한 위치 우선 선택 (HittableEnemyCount > 0)
            var best = CollectionHelper.MaxByWhere(scores,
                s => s.CanStand && s.HittableEnemyCount > 0 && s.DistanceScore >= 20f,
                s => s.TotalScore);

            if (best == null)
            {
                best = CollectionHelper.MaxByWhere(scores,
                    s => s.CanStand && s.HittableEnemyCount > 0 && s.DistanceScore > 0f,
                    s => s.TotalScore);
            }

            // ★ v3.8.45: 3차 폴백에 MinSafeDistance 체크 추가
            // 기존: HittableEnemyCount > 0만 체크 → 적 1.4타일 위치도 선택됨
            // 수정: DistanceScore >= 0 = MinSafeDistance 이상 위치만 허용
            // (DistanceScore < 0 = nearestEnemyDist < minSafeDistance → 위험 지역)
            if (best == null)
            {
                best = CollectionHelper.MaxByWhere(scores,
                    s => s.CanStand && s.HittableEnemyCount > 0 && s.DistanceScore >= 0f,
                    s => s.TotalScore);
            }

            // ★ v3.6.18: 공격 가능 위치 없으면 기존 LOS 기반 폴백 (접근 이동용)
            if (best == null)
            {
                Main.LogDebug($"[MovementAPI] {unit.CharacterName}: No hittable position found, fallback to LOS-based");
                best = CollectionHelper.MaxByWhere(scores,
                    s => s.CanStand && s.HasLosToEnemy && s.DistanceScore >= 20f,
                    s => s.TotalScore);
            }

            // ★ v3.8.45: LOS 폴백도 MinSafeDistance 준수
            if (best == null)
            {
                best = CollectionHelper.MaxByWhere(scores,
                    s => s.CanStand && s.HasLosToEnemy && s.DistanceScore >= 0f,
                    s => s.TotalScore);
            }

            if (best != null)
            {
                Main.Log($"[MovementAPI] FindRangedAttackPosition: Best=({best.Position.x:F1},{best.Position.z:F1}), score={best.TotalScore:F1}, dist={CombatAPI.MetersToTiles(Vector3.Distance(best.Position, enemies.FirstOrDefault()?.Position ?? best.Position)):F1}m, hittable={best.HittableEnemyCount}, cover={best.BestCover}, enemyLoS={(best.HasLosToEnemy ? 1 : 0)}");
            }
            else
            {
                Main.LogDebug($"[MovementAPI] {unit.CharacterName}: No better position found for ranged character with MP={predictedMP:F1}");
            }

            return best;
        }

        /// <summary>
        /// ★ v3.0.74: 근접 공격 위치 찾기 (실제 도달 가능한 타일 기반)
        /// ★ v3.1.01: predictedMP 파라미터 추가
        /// ★ v3.2.00: influenceMap 파라미터 추가
        /// ★ v3.2.25: role 파라미터 추가 - Role별 전선 위치 점수
        /// ★ v3.4.00: predictiveMap 파라미터 추가 - 적 이동 예측 기반 위협 점수
        /// 적의 타일이 아닌, 적에게 인접한 공격 가능 위치 반환
        /// </summary>
        public static PositionScore FindMeleeAttackPositionSync(
            BaseUnitEntity unit,
            BaseUnitEntity target,
            float meleeRange = 2f,
            float predictedMP = 0f,
            BattlefieldInfluenceMap influenceMap = null,
            AIRole role = AIRole.Auto,
            Analysis.PredictiveThreatMap predictiveMap = null,
            AbilityData meleeAoEAbility = null,
            List<BaseUnitEntity> enemies = null)
        {
            if (unit == null || target == null) return null;

            // ★ v3.8.13: AI용 패스파인딩 사용 - 경로 위협 데이터 포함
            var tiles = predictedMP > 0
                ? FindAllReachableTilesWithThreatsSync(unit, predictedMP)
                : FindAllReachableTilesWithThreatsSync(unit);
            if (tiles == null || tiles.Count == 0)
            {
                Main.LogDebug($"[MovementAPI] {unit.CharacterName}: No reachable tiles for melee approach (predictedMP={predictedMP:F1})");
                return null;
            }

            var targetPos = target.Position;
            var unitPos = unit.Position;

            // 적 뒤쪽 방향 (플랭킹 보너스용)
            var flankDir = (targetPos - unitPos).normalized;

            var candidates = new List<PositionScore>();

            foreach (var kvp in tiles)
            {
                var aiCell = kvp.Value;
                var node = aiCell.Node as CustomGridNodeBase;
                if (node == null || !aiCell.IsCanStand) continue;

                // ★ v3.7.62: BattlefieldGrid 검증 - Walkable/점유 체크
                if (!BattlefieldGrid.Instance.ValidateNode(unit, node))
                    continue;

                var pos = node.Vector3Position;

                // ★ v3.6.4: 적과의 거리 계산 - 타일 단위로 통일
                // meleeRange와 SizeRect.Width는 타일 단위이므로 미터→타일 변환 필요
                float distToTargetTiles = CombatAPI.MetersToTiles(Vector3.Distance(pos, targetPos));

                // 근접 공격 사거리 내 타일만 선택 (타일 단위)
                // 적의 크기 고려 (대형 적은 더 넓은 공격 범위)
                float effectiveRange = meleeRange + (target.SizeRect.Width * 0.5f);
                if (distToTargetTiles > effectiveRange) continue;

                // 적 위치와 거의 동일하면 스킵 (점유 타일) - 0.5타일 ≈ 0.67m
                if (distToTargetTiles < 0.5f) continue;

                // 점수 계산
                float score = 100f;  // 기본 점수

                // 1. 이동 거리 점수 (가까울수록 좋음 - MP 절약)
                float distFromUnit = Vector3.Distance(pos, unitPos);
                score -= distFromUnit * 2f;

                // 2. 플랭킹 보너스 (적 뒤쪽에서 공격)
                var attackDir = (targetPos - pos).normalized;
                float flankDot = Vector3.Dot(attackDir, flankDir);
                if (flankDot > 0.5f)  // 뒤쪽에서 공격
                    score += 15f;

                // 3. ★ v3.8.13: AI 셀의 경로 위협 데이터 활용 (목적지 위협 + 경로 위협)
                // CalculateThreatScore(목적지)에 추가로 경로 위협도 반영
                float destThreatScore = CalculateThreatScore(unit, node);
                float pathThreatScore = aiCell.ProvokedAttacks * WEIGHT_AOO + aiCell.EnteredAoE * WEIGHT_AOE_ENTRY + aiCell.StepsInsideDamagingAoE * WEIGHT_DAMAGING_AOE_STEP;
                float threatScore = destThreatScore + pathThreatScore;
                score -= threatScore;

                var posScore = new PositionScore
                {
                    Node = node,
                    CanStand = true,
                    APCost = aiCell.Length,
                    DistanceScore = score,
                    ThreatScore = threatScore
                };

                // ★ v3.2.25: 영향력 맵 + Role별 Frontline 점수 적용
                // ★ v3.4.00: 예측 위협 맵 점수 추가
                // ★ v3.8.13: AI 셀로 경로 위험도 평가
                if (influenceMap != null && influenceMap.IsValid)
                {
                    ApplyInfluenceScores(posScore, influenceMap, role, predictiveMap);

                    // ★ v3.8.13: AI 셀의 경로 위협 데이터 직접 활용
                    if (aiCell.Node != null)
                    {
                        posScore.PathRiskScore = EvaluatePathRiskAi(
                            unit, unitPos, node, aiCell, influenceMap);
                    }
                    else
                    {
                        posScore.PathRiskScore = EvaluatePathRiskSimple(
                            unit, unitPos, pos, influenceMap);
                    }
                }

                // ★ v3.8.50: 근접 AOE 스플래시 보너스
                // 이 위치에서 근접 AOE를 사용할 때 패턴 내 추가 적 수 계산
                // 원형: 위치 무관, 원뿔/부채꼴: 방향에 따라 다른 적 포함
                if (meleeAoEAbility != null && enemies != null)
                {
                    int splashCount = CombatAPI.CountEnemiesInPattern(
                        meleeAoEAbility, targetPos, pos, enemies);
                    if (splashCount >= 2)
                    {
                        posScore.MeleeAoESplashBonus = (splashCount - 1) * 12f;
                    }
                }

                candidates.Add(posScore);
            }

            if (candidates.Count == 0)
            {
                Main.LogDebug($"[MovementAPI] {unit.CharacterName}: No melee attack positions within range");
                return null;
            }

            // ★ v3.8.48: LINQ → CollectionHelper (0 할당, O(n))
            var best = CollectionHelper.MaxBy(candidates, c => c.TotalScore);
            float finalDist = Vector3.Distance(best.Position, targetPos);
            Main.Log($"[MovementAPI] {unit.CharacterName}: Melee position at ({best.Position.x:F1},{best.Position.z:F1}) " +
                $"dist={finalDist:F1}m, score={best.TotalScore:F1}");

            return best;
        }

        /// <summary>
        /// ★ v3.0.60: 후퇴 위치 찾기 (실제 도달 가능한 타일 기반)
        /// ★ v3.0.62: AoE/위협 점수 통합
        /// ★ v3.1.01: predictedMP 파라미터 추가
        /// ★ v3.2.00: influenceMap 파라미터 추가
        /// ★ v3.2.25: role 파라미터 추가 - Role별 전선 위치 점수
        /// ★ v3.4.00: predictiveMap 파라미터 추가 - 적 이동 예측 기반 위협 점수
        ///
        /// PositionEvaluator의 단순 Vector3 계산 대신 PathfindingService 사용
        /// </summary>
        public static PositionScore FindRetreatPositionSync(
            BaseUnitEntity unit,
            List<BaseUnitEntity> enemies,
            float minSafeDistance = 8f,
            float predictedMP = 0f,
            BattlefieldInfluenceMap influenceMap = null,
            AIRole role = AIRole.Auto,
            Analysis.PredictiveThreatMap predictiveMap = null)
        {
            // 기본 호출 - maxSafeDistance는 무제한 (0)
            return FindRetreatPositionSync(unit, enemies, minSafeDistance, 0f, predictedMP,
                influenceMap, role, predictiveMap, null, 0f);
        }

        /// <summary>
        /// ★ v3.7.04: 사역마 거리 제약을 고려한 후퇴 위치 찾기
        /// ★ v3.7.11: maxSafeDistance 파라미터 추가 - 무기 사거리 기반 최대 후퇴 거리
        /// familiarPosition이 지정되면 해당 위치에서 maxFamiliarDistance 이내로 제한
        /// maxSafeDistance > 0이면 해당 거리 초과 시 큰 패널티 적용
        /// </summary>
        public static PositionScore FindRetreatPositionSync(
            BaseUnitEntity unit,
            List<BaseUnitEntity> enemies,
            float minSafeDistance,
            float maxSafeDistance,
            float predictedMP,
            BattlefieldInfluenceMap influenceMap,
            AIRole role,
            Analysis.PredictiveThreatMap predictiveMap,
            Vector3? familiarPosition,
            float maxFamiliarDistanceMeters)
        {
            if (unit == null || enemies == null || enemies.Count == 0)
                return null;

            // ★ v3.8.13: AI용 패스파인딩 사용 - 경로 위협 데이터 포함
            var tiles = predictedMP > 0
                ? FindAllReachableTilesWithThreatsSync(unit, predictedMP)
                : FindAllReachableTilesWithThreatsSync(unit);
            if (tiles == null || tiles.Count == 0)
            {
                Main.LogDebug($"[MovementAPI] {unit.CharacterName}: No reachable tiles for retreat (predictedMP={predictedMP:F1})");
                return null;
            }

            // 적들의 중심점 계산
            Vector3 enemyCenter = Vector3.zero;
            int count = 0;
            foreach (var enemy in enemies)
            {
                if (enemy == null || enemy.LifeState.IsDead) continue;
                enemyCenter += enemy.Position;
                count++;
            }
            if (count == 0) return null;
            enemyCenter /= count;

            // 후퇴 방향 (적 반대)
            var retreatDir = (unit.Position - enemyCenter).normalized;

            var candidates = new List<PositionScore>();

            foreach (var kvp in tiles)
            {
                var aiCell = kvp.Value;
                var node = aiCell.Node as CustomGridNodeBase;
                if (node == null || !aiCell.IsCanStand) continue;

                // ★ v3.7.62: BattlefieldGrid 검증 - Walkable/점유 체크
                if (!BattlefieldGrid.Instance.ValidateNode(unit, node))
                    continue;

                var pos = node.Vector3Position;

                // ★ v3.6.1: 모든 적과의 최소 거리 계산 (타일 단위)
                float nearestEnemyDist = float.MaxValue;
                foreach (var enemy in enemies)
                {
                    if (enemy == null || enemy.LifeState.IsDead) continue;
                    float d = CombatAPI.MetersToTiles(Vector3.Distance(pos, enemy.Position));
                    if (d < nearestEnemyDist) nearestEnemyDist = d;
                }

                // 안전 거리 미달이면 스킵 (minSafeDistance는 타일 단위)
                if (nearestEnemyDist < minSafeDistance) continue;

                // ★ v3.7.04: 사역마 거리 제약 체크
                float familiarDistPenalty = 0f;
                if (familiarPosition.HasValue && maxFamiliarDistanceMeters > 0)
                {
                    float distToFamiliar = Vector3.Distance(pos, familiarPosition.Value);
                    if (distToFamiliar > maxFamiliarDistanceMeters)
                    {
                        // 사역마와 너무 멀면 큰 패널티 (하지만 완전히 제외하진 않음)
                        familiarDistPenalty = (distToFamiliar - maxFamiliarDistanceMeters) * 5f;
                        Main.LogDebug($"[MovementAPI] Retreat pos ({pos.x:F1},{pos.z:F1}) too far from familiar: {distToFamiliar:F1}m > {maxFamiliarDistanceMeters:F1}m, penalty={familiarDistPenalty:F1}");
                    }
                }

                // ★ v3.7.11: 무기 사거리 초과 패널티 (너무 멀리 후퇴하면 공격 불가)
                float weaponRangePenalty = 0f;
                if (maxSafeDistance > 0 && nearestEnemyDist > maxSafeDistance)
                {
                    // 무기 사거리를 초과하면 큰 패널티 적용
                    // 초과한 거리의 제곱에 비례하여 패널티 (급격히 증가)
                    float excess = nearestEnemyDist - maxSafeDistance;
                    weaponRangePenalty = excess * excess * 10f;
                    Main.LogDebug($"[MovementAPI] Retreat pos ({pos.x:F1},{pos.z:F1}) exceeds weapon range: {nearestEnemyDist:F1} > {maxSafeDistance:F1}, penalty={weaponRangePenalty:F1}");
                }

                // 점수 계산: 적에게서 멀수록 + 후퇴 방향 보너스
                // ★ v3.7.11: 하지만 무기 사거리를 넘으면 더 이상 보너스 없음
                float effectiveDistForScore = maxSafeDistance > 0
                    ? Math.Min(nearestEnemyDist, maxSafeDistance)
                    : nearestEnemyDist;
                float distScore = effectiveDistForScore * 2f;

                // 후퇴 방향과의 일치도
                var moveDir = (pos - unit.Position).normalized;
                float directionBonus = Vector3.Dot(moveDir, retreatDir) * 10f;

                // 이동 거리 패널티 (너무 멀면 MP 낭비)
                float moveDist = Vector3.Distance(unit.Position, pos);
                float moveDistPenalty = moveDist * 0.5f;

                // ★ v3.8.13: AI 셀의 경로 위협 데이터 활용 (목적지 위협 + 경로 위협)
                float destThreatScore = CalculateThreatScore(unit, node);
                float pathThreatScore = aiCell.ProvokedAttacks * WEIGHT_AOO + aiCell.EnteredAoE * WEIGHT_AOE_ENTRY + aiCell.StepsInsideDamagingAoE * WEIGHT_DAMAGING_AOE_STEP;

                var score = new PositionScore
                {
                    Node = node,
                    CanStand = true,
                    APCost = aiCell.Length,
                    // ★ v3.7.04: 사역마 거리 패널티 적용
                    // ★ v3.7.11: 무기 사거리 초과 패널티 적용
                    DistanceScore = distScore + directionBonus - moveDistPenalty - familiarDistPenalty - weaponRangePenalty,
                    // ★ v3.8.13: 경로 위협도 반영
                    ThreatScore = destThreatScore + pathThreatScore
                };

                // ★ v3.2.25: 영향력 맵 + Role별 Frontline 점수 적용
                // ★ v3.4.00: 예측 위협 맵 점수 추가
                // ★ v3.8.13: AI 셀로 경로 위험도 평가
                if (influenceMap != null && influenceMap.IsValid)
                {
                    ApplyInfluenceScores(score, influenceMap, role, predictiveMap);

                    // ★ v3.8.13: AI 셀의 경로 위협 데이터 직접 활용
                    if (aiCell.Node != null)
                    {
                        score.PathRiskScore = EvaluatePathRiskAi(
                            unit, unit.Position, node, aiCell, influenceMap);
                    }
                    else
                    {
                        score.PathRiskScore = EvaluatePathRiskSimple(
                            unit, unit.Position, pos, influenceMap);
                    }
                }

                // 엄폐 점수 추가
                try
                {
                    var coverType = LosCalculations.GetCoverType(pos);
                    switch (coverType)
                    {
                        case LosCalculations.CoverType.Full:
                            score.CoverScore = 30f;
                            break;
                        case LosCalculations.CoverType.Half:
                            score.CoverScore = 15f;
                            break;
                        case LosCalculations.CoverType.Invisible:
                            score.CoverScore = 40f;
                            break;
                    }
                }
                catch { }

                candidates.Add(score);
            }

            if (candidates.Count == 0)
            {
                // ★ v3.7.23: 안전 거리 달성 불가 시 폴백 - 도달 가능한 최대 거리 위치
                // 사역마 Relocate와 같은 로직 - 최대한 멀리 갈 수 있는 위치로 이동
                Main.Log($"[MovementAPI] {unit.CharacterName}: No safe retreat positions at {minSafeDistance:F1} tiles, fallback to farthest reachable");

                PositionScore farthestCandidate = null;
                float farthestDist = 0f;

                foreach (var kvp in tiles)
                {
                    var aiCell = kvp.Value;
                    var node = aiCell.Node as CustomGridNodeBase;
                    if (node == null || !aiCell.IsCanStand) continue;

                    var pos = node.Vector3Position;

                    // 적들로부터의 최소 거리
                    float nearestEnemyDist = float.MaxValue;
                    foreach (var enemy in enemies)
                    {
                        if (enemy == null || enemy.LifeState.IsDead) continue;
                        float d = CombatAPI.MetersToTiles(Vector3.Distance(pos, enemy.Position));
                        if (d < nearestEnemyDist) nearestEnemyDist = d;
                    }

                    // 현재 위치보다 멀고, 지금까지 찾은 것보다 멀면 갱신
                    float currentDist = CombatAPI.MetersToTiles(Vector3.Distance(unit.Position, enemies[0].Position));
                    if (nearestEnemyDist > currentDist && nearestEnemyDist > farthestDist)
                    {
                        farthestDist = nearestEnemyDist;
                        farthestCandidate = new PositionScore
                        {
                            Node = node,
                            CanStand = true,
                            DistanceScore = nearestEnemyDist
                        };
                    }
                }

                if (farthestCandidate != null)
                {
                    Main.Log($"[MovementAPI] {unit.CharacterName}: Fallback retreat to ({farthestCandidate.Position.x:F1},{farthestCandidate.Position.z:F1}) dist={farthestDist:F1} tiles");
                    return farthestCandidate;
                }

                Main.LogDebug($"[MovementAPI] {unit.CharacterName}: No retreat positions at all");
                return null;
            }

            // ★ v3.8.48: LINQ → CollectionHelper (0 할당, O(n))
            var best = CollectionHelper.MaxBy(candidates, c => c.TotalScore);
            Main.LogDebug($"[MovementAPI] {unit.CharacterName}: Retreat to ({best.Position.x:F1},{best.Position.z:F1}) score={best.TotalScore:F1}");

            return best;
        }

        /// <summary>
        /// ★ v3.7.23: 적 방향으로 도달 가능한 최대 위치 찾기 (접근 폴백용)
        /// 공격 가능한 위치가 없을 때 최대한 적에게 가까이 이동
        /// </summary>
        public static PositionScore FindBestApproachPosition(
            BaseUnitEntity unit,
            BaseUnitEntity target,
            float predictedMP = 0f)
        {
            if (unit == null || target == null) return null;

            // ★ v3.8.13: AI용 패스파인딩 사용 - 경로 위협 데이터 포함
            var tiles = predictedMP > 0
                ? FindAllReachableTilesWithThreatsSync(unit, predictedMP)
                : FindAllReachableTilesWithThreatsSync(unit);
            if (tiles == null || tiles.Count == 0)
            {
                Main.LogDebug($"[MovementAPI] {unit.CharacterName}: No reachable tiles for approach");
                return null;
            }

            var targetPos = target.Position;
            PositionScore closestCandidate = null;
            float closestDist = float.MaxValue;
            float lowestPathRisk = float.MaxValue;

            foreach (var kvp in tiles)
            {
                var aiCell = kvp.Value;
                var node = aiCell.Node as CustomGridNodeBase;
                if (node == null || !aiCell.IsCanStand) continue;

                var pos = node.Vector3Position;
                float distToTarget = Vector3.Distance(pos, targetPos);

                // ★ v3.8.13: 경로 위협도 계산 (접근 시에도 안전한 경로 선호)
                float pathRisk = aiCell.ProvokedAttacks * WEIGHT_AOO + aiCell.EnteredAoE * WEIGHT_AOE_ENTRY + aiCell.StepsInsideDamagingAoE * WEIGHT_DAMAGING_AOE_STEP;

                // 현재 위치보다 가깝고, 지금까지 찾은 것보다 가까우면 갱신
                // 같은 거리면 더 안전한 경로 선택
                float currentDist = Vector3.Distance(unit.Position, targetPos);
                if (distToTarget < currentDist &&
                    (distToTarget < closestDist || (distToTarget == closestDist && pathRisk < lowestPathRisk)))
                {
                    closestDist = distToTarget;
                    lowestPathRisk = pathRisk;
                    closestCandidate = new PositionScore
                    {
                        Node = node,
                        CanStand = true,
                        DistanceScore = 100f - distToTarget,  // 가까울수록 높은 점수
                        PathRiskScore = pathRisk  // ★ v3.8.13: 경로 위험도 기록
                    };
                }
            }

            if (closestCandidate != null)
            {
                Main.Log($"[MovementAPI] {unit.CharacterName}: Approach position at ({closestCandidate.Position.x:F1},{closestCandidate.Position.z:F1}) dist={closestDist:F1}m, pathRisk={lowestPathRisk:F1} to {target.CharacterName}");
            }

            return closestCandidate;
        }

        #endregion

        #region Influence Map Integration (v3.2.00)

        /// <summary>
        /// ★ v3.2.25: 영향력 맵 기반 위협/통제 점수 적용 (Role별 Frontline 점수 포함)
        /// ★ v3.4.00: 예측 위협 맵 지원 추가
        /// ★ v3.5.18: Response Curves + Blackboard 통합
        ///
        /// - 적 밀집 지역 회피 (InfluenceThreatScore) - ThreatCountPenalty 커브 적용
        /// - 아군 통제 구역 선호 (InfluenceControlBonus) - SafetyByDistance 커브 적용
        /// - Role별 전선 위치 선호/회피
        /// - 예측 위협 반영 (다음 턴 적 이동 고려)
        /// - SharedTarget 접근 보너스 (Blackboard)
        /// - TeamConfidence 기반 전술 조정 (Blackboard)
        /// </summary>
        private static void ApplyInfluenceScores(
            PositionScore score,
            BattlefieldInfluenceMap influenceMap,
            AIRole role = AIRole.Auto,
            Analysis.PredictiveThreatMap predictiveMap = null)
        {
            if (score == null || influenceMap == null || !influenceMap.IsValid)
                return;

            var pos = score.Position;

            // ★ v3.5.18: Response Curves 적용
            // 적 밀집도 기반 위협 - ThreatCountPenalty 커브 사용
            float threatDensity = influenceMap.GetThreatAt(pos);
            float threatMultiplier = CurvePresets.ThreatCountPenalty?.Evaluate(threatDensity) ?? (threatDensity * 8f);
            score.InfluenceThreatScore = threatMultiplier;

            // 아군 통제 구역 보너스 - SafetyByDistance 커브 개념 적용
            float allyControl = influenceMap.GetControlAt(pos);
            float safetyMultiplier = CurvePresets.SafetyByDistance?.Evaluate(allyControl * 10f) ?? (allyControl * 4f);
            score.InfluenceControlBonus = safetyMultiplier;

            // ★ v3.5.00: CoverMap 보너스 - CoverValue 커브 적용
            float coverQuality = influenceMap.GetCoverAt(pos);
            float coverBonus = CurvePresets.CoverValue?.Evaluate(coverQuality) ?? (coverQuality * 12f);
            score.InfluenceControlBonus += coverBonus;

            // ★ v3.5.00: PDF 방법론 통합 점수
            float combinedTacticalScore = influenceMap.GetCombinedScore(pos);
            score.InfluenceControlBonus += combinedTacticalScore * 8f;

            // ★ v3.2.25: Frontline 거리 기반 Role별 점수
            float frontlineDist = influenceMap.GetFrontlineDistance(pos);
            ApplyFrontlineScore(score, frontlineDist, role);

            // 안전 구역 추가 보너스
            if (influenceMap.IsSafeZone(pos))
            {
                score.InfluenceControlBonus += 5f;
            }

            // ★ v3.4.00: 예측 위협 점수 (다음 턴 적 이동 고려)
            if (predictiveMap != null && predictiveMap.IsValid)
            {
                float predictedThreat = predictiveMap.GetPredictedThreatAt(pos);
                float turnSafety = predictiveMap.GetTurnSafetyScore(pos);

                score.InfluenceThreatScore += predictedThreat * 6f;

                if (turnSafety > 0.7f)
                {
                    score.InfluenceControlBonus += turnSafety * 10f;
                }
            }

            // ★ v3.5.18: Blackboard 통합 - SharedTarget 접근 보너스
            ApplyBlackboardScores(score, pos, role);
        }

        /// <summary>
        /// ★ v3.5.18: Blackboard 기반 점수 적용
        /// - SharedTarget에 가까운 위치 보너스
        /// - TeamConfidence에 따른 전술 조정 (공격/방어 성향)
        /// - CurrentTactic에 따른 위치 선호
        /// </summary>
        private static void ApplyBlackboardScores(PositionScore score, Vector3 pos, AIRole role)
        {
            var blackboard = TeamBlackboard.Instance;
            if (blackboard == null) return;

            // 1. SharedTarget 접근 보너스
            var sharedTarget = blackboard.SharedTarget;
            if (sharedTarget != null && !sharedTarget.LifeState.IsDead)
            {
                // ★ v3.6.1: 타일 단위로 변환
                float distToSharedTarget = CombatAPI.MetersToTiles(Vector3.Distance(pos, sharedTarget.Position));

                // 근접 역할(Tank, DPS)은 SharedTarget에 가까울수록 보너스
                // Support는 SharedTarget 근처에서 힐/버프 가능하도록 적당한 거리 선호
                switch (role)
                {
                    case AIRole.Tank:
                    case AIRole.DPS:
                        // ★ v3.6.1: 타일 단위 (2타일 ≈ 2.7m, 7타일 ≈ 9.5m)
                        if (distToSharedTarget <= 2f)
                            score.SharedTargetBonus = 20f;
                        else if (distToSharedTarget <= 7f)
                            score.SharedTargetBonus = 20f - (distToSharedTarget - 2f) * 3f;
                        break;

                    case AIRole.Support:
                        // ★ v3.6.1: 타일 단위 (4-8타일 ≈ 5.4-10.8m)
                        if (distToSharedTarget >= 4f && distToSharedTarget <= 8f)
                            score.SharedTargetBonus = 10f;
                        else if (distToSharedTarget < 4f)
                            score.SharedTargetBonus = distToSharedTarget * 2.5f;
                        break;
                }
            }

            // 2. TeamConfidence 기반 전술 조정
            float confidence = blackboard.TeamConfidence;
            // ConfidenceToAggression: 신뢰도 높으면 공격적 (전진 보너스)
            // ConfidenceToDefenseNeed: 신뢰도 낮으면 방어적 (후방/엄폐 보너스)
            float aggressionMod = CurvePresets.ConfidenceToAggression?.Evaluate(confidence) ?? 1f;
            float defenseMod = CurvePresets.ConfidenceToDefenseNeed?.Evaluate(confidence) ?? 1f;

            // 공격 성향이 높으면(>1) 전진 보너스, 방어 필요도 높으면(>1) 엄폐 보너스 증폭
            if (aggressionMod > 1f)
            {
                // 공격적 상황: 적에게 가까운 위치에 약간 보너스
                score.TacticalAdjustment += (aggressionMod - 1f) * 5f;
            }
            if (defenseMod > 1f)
            {
                // 방어적 상황: 엄폐/안전 점수에 가중치
                score.TacticalAdjustment += score.CoverScore * (defenseMod - 1f) * 0.5f;
            }

            // 3. CurrentTactic에 따른 조정
            var tactic = blackboard.CurrentTactic;
            switch (tactic)
            {
                case TacticalSignal.Retreat:
                    // 후퇴 모드: 적에게서 먼 위치 추가 보너스
                    score.TacticalAdjustment -= score.AttackScore * 0.5f;  // 공격 위치 가치 감소
                    break;

                case TacticalSignal.Attack:
                    // 공격 모드: SharedTarget 보너스 증폭, 전진 선호
                    score.SharedTargetBonus *= 1.5f;
                    break;

                case TacticalSignal.Defend:
                    // 방어 모드: 엄폐 보너스 증폭
                    score.TacticalAdjustment += score.CoverScore * 0.3f;
                    break;
            }

            // ★ v3.5.19: Blackboard 적용 결과 로깅 (비-0 값만)
            if (score.SharedTargetBonus != 0 || score.TacticalAdjustment != 0)
            {
                Main.LogDebug($"[MovementAPI] Blackboard: ST={score.SharedTargetBonus:F1}, Tac={score.TacticalAdjustment:F1}, Tactic={tactic}");
            }
        }

        /// <summary>
        /// ★ v3.2.25: Role별 전선 위치 점수
        /// Tank: 전선 앞(0~5m) 선호, 후방 페널티
        /// DPS: 전선 너머 10m 이상 고립 페널티
        /// Support: 전선 뒤(-5m 이하) 선호, 전선 앞 페널티
        /// </summary>
        private static void ApplyFrontlineScore(PositionScore score, float frontlineDist, AIRole role)
        {
            switch (role)
            {
                case AIRole.Tank:
                    // Tank: 전선 앞(0~5m)에서 보너스, 후방(-5m 이하) 페널티
                    if (frontlineDist >= 0f && frontlineDist <= 5f)
                    {
                        score.InfluenceControlBonus += 15f;  // 전선 앞 적극 위치
                    }
                    else if (frontlineDist < -5f)
                    {
                        score.InfluenceThreatScore += 10f;  // 전선 뒤 = 역할 수행 불가
                    }
                    break;

                case AIRole.DPS:
                    // DPS: 전선 너머 10m 이상 침투 시 고립 페널티
                    if (frontlineDist > 10f)
                    {
                        float isolationPenalty = (frontlineDist - 10f) * 3f;
                        score.InfluenceThreatScore += isolationPenalty;
                    }
                    break;

                case AIRole.Support:
                    // Support: 전선 뒤(-5m 이하) 보너스, 전선 앞 페널티
                    if (frontlineDist < -5f)
                    {
                        score.InfluenceControlBonus += 10f;  // 후방 안전 위치
                    }
                    else if (frontlineDist > 0f)
                    {
                        score.InfluenceThreatScore += frontlineDist * 4f;  // 전선 앞 = 위험
                    }
                    break;

                // Auto/기타: 기본 동작 (추가 점수 없음)
            }
        }

        /// <summary>
        /// ★ v3.2.00: 영향력 맵에서 가장 안전한 위치 반환
        /// </summary>
        public static Vector3? GetSafestPosition(BattlefieldInfluenceMap influenceMap, Vector3 currentPos)
        {
            if (influenceMap == null || !influenceMap.IsValid)
                return null;

            return influenceMap.GetNearestSafeZone(currentPos);
        }

        #endregion
    }
}
