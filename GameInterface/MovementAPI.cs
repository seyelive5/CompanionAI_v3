using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.AI;
using Kingmaker.AI.AreaScanning;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Parts;
using Kingmaker.View.Covers;
using Pathfinding;
using UnityEngine;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.Data;
using CompanionAI_v3.Settings;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// 이동 API - 위치 평가 및 최적 위치 찾기
    /// </summary>
    public static partial class MovementAPI
    {
        #region Path Threat Weights

        /// <summary>★ v3.9.70: 경로 위협 가중치 — 게임 수준으로 대폭 상향
        /// 게임 원본: AoO=+1000, AoE진입=+300, DamagingAoE스텝=+100
        /// v3.8.59: 20/15/10 → 위험 구역 통과 패널티 미미하여 무시됨
        /// v3.9.70: 100/80/60 → 위험 경로를 확실히 기피하되 필수 이동은 허용</summary>
        private const float WEIGHT_AOO = 100f;           // 기회공격 유발
        private const float WEIGHT_AOE_ENTRY = 80f;      // AoE 진입
        private const float WEIGHT_DAMAGING_AOE_STEP = 60f;  // 데미지 AoE 내 이동

        #endregion

        #region ★ v3.8.15: AI Pathfinding Cache (스터터링 방지)

        /// <summary>
        /// ★ v3.8.78: 2-슬롯 LRU AI 패스파인딩 캐시
        /// 문제: FindAllReachableTilesWithThreatsSync가 한 턴에 4번까지 호출됨
        ///   같은 유닛이 다른 AP(full AP vs predictedMP)로 호출 → 단일 캐시에서 미스 발생
        /// 해결: 2-슬롯 LRU 캐시로 2개 AP 값 동시 보관
        /// </summary>
        private static string _cachedUnitId1, _cachedUnitId2;
        private static float _cachedAP1, _cachedAP2;
        private static Dictionary<GraphNode, WarhammerPathAiCell> _cachedAiTiles1, _cachedAiTiles2;
        private static int _cachedTurnNumber1 = -1, _cachedTurnNumber2 = -1;
        private static int _lastUsedSlot;  // LRU: 마지막 사용 슬롯 (1 or 2)

        /// <summary>AI 패스파인딩 캐시 무효화</summary>
        public static void InvalidateAiPathCache()
        {
            _cachedUnitId1 = null; _cachedAP1 = 0; _cachedAiTiles1 = null; _cachedTurnNumber1 = -1;
            _cachedUnitId2 = null; _cachedAP2 = 0; _cachedAiTiles2 = null; _cachedTurnNumber2 = -1;
            _lastUsedSlot = 0;
            if (Main.IsDebugEnabled) Log.Engine.Debug("[MovementAPI] AI pathfinding cache invalidated (2-slot)");
        }

        // ★ v3.111.0 Phase 5: 턴 시작 시 SituationAnalyzer가 설정. EvaluatePosition이 조회.
        // 적 예상 이동 위치를 HideScore worst-case 계산에 전달하기 위한 정적 스크래치패드.
        private static CompanionAI_v3.Analysis.PredictedEnemyMoves _currentPredictedMoves;

        /// <summary>
        /// ★ v3.111.0 Phase 5: 현재 턴의 적 예상 이동 위치 설정 (SituationAnalyzer 호출).
        /// EvaluatePosition이 이 필드를 읽어 GetEnsuredCoverComponents vs GetHideScoreComponents 선택.
        /// </summary>
        public static void SetPredictedMoves(CompanionAI_v3.Analysis.PredictedEnemyMoves pm)
        {
            _currentPredictedMoves = pm;
        }

        /// <summary>★ v3.8.78: 2-슬롯 LRU 캐시 저장</summary>
        private static void CacheAiTiles(string unitId, float ap, int turnNumber, Dictionary<GraphNode, WarhammerPathAiCell> tiles)
        {
            // LRU: 마지막 사용 슬롯이 아닌 슬롯에 저장 (가장 최근 히트한 슬롯 보존)
            if (_lastUsedSlot != 1)
            {
                _cachedUnitId1 = unitId; _cachedAP1 = ap;
                _cachedAiTiles1 = tiles; _cachedTurnNumber1 = turnNumber;
                _lastUsedSlot = 1;
            }
            else
            {
                _cachedUnitId2 = unitId; _cachedAP2 = ap;
                _cachedAiTiles2 = tiles; _cachedTurnNumber2 = turnNumber;
                _lastUsedSlot = 2;
            }
        }

        #endregion

        #region ★ v3.9.42: Approach Path Cache (다중 턴 접근 방향 일관성)

        /// <summary>
        /// 유닛별 A* 접근 경로 캐시
        /// 문제: 좁은 통로에서 매 턴 A* 재계산 → 다른 경로 선택 → 왔다갔다 진동
        /// 해결: 접근 경로를 캐시하여 같은 적에게 계속 접근할 때 동일 경로 유지
        /// </summary>
        private struct CachedApproachPath
        {
            public string TargetId;          // 접근 대상 적 UniqueId
            public Vector3 TargetPosition;   // 경로 계산 시 적 위치
            public List<GraphNode> Path;     // 전체 A* 경로 노드
        }

        private static readonly Dictionary<string, CachedApproachPath> _approachPathCache = new();

        /// <summary>접근 경로 캐시 전체 클리어 (전투 종료 시)</summary>
        public static void ClearApproachPathCache()
        {
            _approachPathCache.Clear();
            if (Main.IsDebugEnabled) Log.Engine.Debug("[MovementAPI] Approach path cache cleared");
        }

        /// <summary>특정 유닛의 접근 경로 캐시 클리어</summary>
        public static void ClearApproachPathCache(string unitId)
        {
            _approachPathCache.Remove(unitId);
        }

        /// <summary>적 위치가 크게 변했는지 확인 (3타일 이상 이동 시 경로 재계산)</summary>
        private const float APPROACH_CACHE_INVALIDATION_DIST = 3f * 1.35f; // 3 tiles in meters

        #endregion

        #region Current Position Evaluation

        /// <summary>
        /// 현재 위치를 (이동 후보들과 동일한 13축 기준으로) 평가.
        /// EvaluateAttackFromCurrent 등 "이동 안 함" 옵션이 이동 옵션과 *대칭적으로*
        /// 위치 품질을 비교할 수 있도록 동일 점수 시스템 사용.
        ///
        /// 경로 관련 축 (PathRiskScore, OscillationPenalty 등) 은 이동 없으므로 0.
        /// 그 외 13축 (Cover, Hide, Distance, Threat, TurnThreat, StayAway, Attack, Hit,
        /// AllyC, Flank, Exposure 등) 은 EvaluatePosition + 후처리 enrichment 그대로 적용.
        /// </summary>
        public static PositionScore EvaluateCurrentPosition(
            BaseUnitEntity unit,
            List<BaseUnitEntity> enemies,
            float weaponRange = Settings.SC.FallbackWeaponRange,
            float minSafeDistance = 5f,
            AIRole role = AIRole.Auto)
        {
            if (unit == null || enemies == null) return null;

            var node = unit.Position.GetNearestNodeXZ() as Kingmaker.Pathfinding.CustomGridNodeBase;
            if (node == null) return null;

            // 이동 안 함 → 경로 비용 0 인 stub cell.
            var stubCell = new WarhammerPathAiCell(
                node.Vector3Position,
                diagonalsCount: 0,
                length: 0f,
                node: node,
                parentNode: null,
                isCanStand: true,
                enteredAoE: 0,
                stepsInsideDamagingAoE: 0,
                provokedAttacks: 0);

            float targetDistance = weaponRange;
            var score = EvaluatePosition(unit, node, stubCell, enemies, MovementGoal.RangedAttackPosition, targetDistance, minSafeDistance);
            if (score == null) return null;

            // 후처리 enrichment — FindRangedAttackPositionSync 의 score loop 와 동일.
            // 경로 관련 (PathRiskScore, OscillationPenalty) 은 이동 없으므로 0 유지.
            score.ThreatScore += CalculateThreatScore(unit, score.Node);
            score.AllyClusterPenalty = CalculateAllyClusterPenalty(score.Position, unit);
            ApplyBlackboardScores(score, score.Position, role);

            // 명중률 보너스 — 같은 함수 사용 (현재 위치에서 적들 향한 hit chance).
            var primaryAttack = CombatAPI.FindAnyAttackAbility(unit, Settings.RangePreference.PreferRanged);
            if (primaryAttack == null)
                primaryAttack = CombatAPI.FindAnyAttackAbility(unit, Settings.RangePreference.PreferRanged, includeDangerousAoE: true);
            bool isScatter = CombatAPI.IsScatterAttack(primaryAttack);
            bool isMelee = primaryAttack?.IsMelee ?? false;
            score.HitChanceBonus = CalculateHitChanceBonus(score.Position, enemies, weaponRange, isScatter, isMelee, unit, primaryAttack);

            // 플랭킹 스코어 (현재 위치에서 적 후방/측면 가능성).
            float flankSum = 0f;
            for (int i = 0; i < enemies.Count; i++)
            {
                var enemy = enemies[i];
                if (enemy == null || enemy.LifeState.IsDead) continue;
                flankSum += CombatAPI.GetFlankingBonus(enemy, score.Position);
            }
            score.FlankingScore = flankSum * SC.FlankingPositionBonus;

            return score;
        }

        #endregion
    }
}
