using System;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.View.Covers;
using UnityEngine;

namespace CompanionAI_v3.GameInterface
{
    public static partial class MovementAPI
    {
        #region Position Scoring

        public class PositionScore
        {
            public CustomGridNodeBase Node { get; set; }
            public Vector3 Position => Node?.Vector3Position ?? Vector3.zero;

            /// <summary>
            /// ★ v3.111.1: 공격자 관점 fire efficiency (0~30). 게임 fireCoverValues 역수 체계.
            /// 적 cover 높을수록 우리 공격 효율 ↓. HideScore(방어 관점)와 쌍으로 trade-off.
            /// </summary>
            public float CoverScore { get; set; }
            public float DistanceScore { get; set; }
            public float ThreatScore { get; set; }
            public float AttackScore { get; set; }
            public float APCost { get; set; }

            // ★ v3.110.16: InfluenceThreatScore/InfluenceControlBonus 제거 (Phase C).
            //   InfluenceMap의 역제곱 거리 기반 threat/ctrl은 ThreatScore/CoverScore와 중복되며
            //   실증 로그상 유의미 기여 작음. ExposureScore(v3.110.15)가 "적 밀집 회피" 역할 대체.
            //   ApplyInfluenceScores 메서드 + 관련 influenceMap 파라미터 함께 제거됨.

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

            /// <summary>
            /// ★ v3.116.8 옵션 B: 원거리 AoE (Cone/Ray/Sector/Burst) 다중 타격 커버리지 보너스.
            /// 이 위치에서 가장 가까운 적을 향해 패턴을 시뮬했을 때 잡히는 추가 적 수 × 가중치.
            /// 단발 사격 평가에 묻혀 Cone 5명 자리가 1명 자리와 ~8점만 차이나던 문제 해결.
            /// MeleeAoESplashBonus 의 ranged 대응판 — 동일 가중치 (12) 사용.
            /// </summary>
            public float AoeHitCountBonus { get; set; }

            /// <summary>★ v3.9.02: 아군 밀집 패널티 (AoE 취약성 방지 + 아군 AoE 방해 방지)</summary>
            public float AllyClusterPenalty { get; set; }

            /// <summary>★ v3.28.0: 플랭킹 보너스 (Back=최대, Side=중간, Front=0)</summary>
            public float FlankingScore { get; set; }

            /// <summary>★ v3.74.2: 진동 방지 패널티 (이전 위치로 되돌아가면 감점)</summary>
            public float OscillationPenalty { get; set; }

            /// <summary>★ v3.110.15: 노출도 패널티 (자신을 볼 수 있는 적 수 기반).
            /// InfluenceMap의 원래 의도(적 밀집 회피)를 게임 API로 정확히 구현.
            /// hittableFromLos는 대칭 LOS라 "적→자신 LOS 수"와 동일 — 재사용.
            /// 사용자 증상 "Support/원거리가 적 밀집 한복판 포지셔닝" 직접 해결.</summary>
            public float ExposureScore { get; set; }

            /// <summary>
            /// ★ v3.110.20 Phase 2: 각 적이 이 턴에 이 위치를 공격 가능한 확률 합계.
            /// CombatAPI.GetEnemyTurnThreatScore 반환값 합산 (0 / 0.5 / 1 per enemy).
            /// 게임 학습된 threatRange + 현재 장비 무기 사거리 + 적 AP 기반 정확한 위협 평가.
            /// N명의 적이 즉시 공격 가능 = N, 이동 후 공격 가능 N명 = 0.5N, 안전 = 0.
            /// </summary>
            public float EnemyTurnThreatSum { get; set; }

            /// <summary>
            /// ★ v3.110.22 Phase 4: 적 이동능력 반영 안전거리 점수 (0=근접, 1=모두 안전).
            /// TileScorerPort.GetStayingAwayScore 원본 값. 게임 ProtectionTileScorer 패턴.
            /// </summary>
            public float StayingAwayScore { get; set; }

            /// <summary>
            /// ★ v3.110.22 Phase 4: StayingAwayScore × goal-based weight.
            /// Retreat/FindCover = 40/30 (적극적 원거리 유지), RangedAttack = 25 (중간),
            /// 근접 approach = 10 (낮음 — 근거리 공격 방해 방지).
            /// TotalScore에 직접 기여하는 가중값.
            /// </summary>
            public float StayingAwayBonus { get; set; }

            /// <summary>
            /// ★ v3.110.19 Phase 1a: HideScore 5축 (게임 ProtectionTileScorer 패턴).
            /// 방어 관점 — 이 위치가 얼마나 은폐되는가. CoverScore(공격 관점)와 분리.
            /// 계산: TileScorerPort.GetHideScoreComponents 호출 결과를 Task 1.3에서 설정.
            /// </summary>
            public float HideFullComplete { get; set; }   // 0 or 1: 모든 적에게 ≥Full 엄폐 완성
            public float HideAnyComplete { get; set; }    // 0 or 1: 모든 적에게 ≥Half 엄폐 완성
            public float HideAnyRatio { get; set; }       // 0~1: ≥Half 엄폐 비율
            public float HideFullRatio { get; set; }      // 0~1: ≥Full 엄폐 비율
            public float HideValue { get; set; }          // ★ v3.111.15 Phase C.1: per-enemy 평균 엄폐 품질 [0, 1].

            /// <summary>
            /// HideScore 가중 합산 — TotalScore 기여값.
            /// FullComplete*50 (완전 은폐 특별 보너스) + AnyComplete*20 + Ratios*15 + HideValue*10.
            /// ★ v3.111.15 Phase C.1: HideValue 정규화로 max 180 → 110.
            ///   TacticalAdjustment 계수(MovementAPI:2293,2314)는 미조정 — 인게임 튜닝 시 재검토.
            /// </summary>
            public float HideScore =>
                HideFullComplete * 50f +
                HideAnyComplete * 20f +
                HideFullRatio * 15f +
                HideAnyRatio * 15f +
                HideValue * 10f;

            /// <summary>
            /// ★ v3.110.19 Phase 1a: HideScoreComponents → PositionScore Hide fields 일괄 복사.
            /// TileScorerPort.HideScoreComponents 5개 필드를 PositionScore.Hide* 5개로 매핑.
            /// 명명 불일치 (FullCoverComplete ↔ HideFullComplete 등) 타이포 리스크 방지.
            /// </summary>
            public void ApplyHideComponents(TileScorerPort.HideScoreComponents c)
            {
                HideFullComplete = c.FullCoverComplete;
                HideAnyComplete  = c.AnyCoverComplete;
                HideAnyRatio     = c.AnyCoverRatio;
                HideFullRatio    = c.FullCoverRatio;
                HideValue        = c.HideValue;
            }

            public float TotalScore => CoverScore + DistanceScore - ThreatScore + AttackScore
                                       + SharedTargetBonus + TacticalAdjustment
                                       - PathRiskScore + HitChanceBonus + MeleeAoESplashBonus
                                       - AllyClusterPenalty + FlankingScore
                                       - OscillationPenalty
                                       - ExposureScore
                                       + HideScore  // ★ v3.110.19 Phase 1a
                                       - (EnemyTurnThreatSum * 8f)  // ★ v3.110.20 Phase 2
                                       + StayingAwayBonus  // ★ v3.110.22 Phase 4
                                       + AoeHitCountBonus;  // ★ v3.116.8 옵션 B: ranged AoE coverage

            public bool CanStand { get; set; }
            public bool HasLosToEnemy { get; set; }
            public int ProvokedAttacks { get; set; }
            public LosCalculations.CoverType BestCover { get; set; }

            /// <summary>★ v3.116.10 진단: Best 위치에서 가장 좋은 splash 를 낸 AoE 능력 (디버그 로그용, score 영향 없음)</summary>
            public Kingmaker.UnitLogic.Abilities.AbilityData BestAoeAbility { get; set; }
            /// <summary>★ v3.116.10 진단: Best 위치에서 측정된 splash 카운트</summary>
            public int BestAoeSplash { get; set; }

            public override string ToString() =>
                $"Pos({Position.x:F1},{Position.z:F1}) Score={TotalScore:F1}" +
                (SharedTargetBonus > 0 ? $" [ST:{SharedTargetBonus:F1}]" : "") +
                (TacticalAdjustment != 0 ? $" [Tac:{TacticalAdjustment:F1}]" : "") +
                (PathRiskScore > 0 ? $" [Path:{PathRiskScore:F1}]" : "") +
                (HitChanceBonus != 0 ? $" [Hit:{HitChanceBonus:F1}]" : "") +
                (MeleeAoESplashBonus > 0 ? $" [Splash:{MeleeAoESplashBonus:F1}]" : "") +
                (AoeHitCountBonus > 0 ? $" [AoeCov:+{AoeHitCountBonus:F1}]" : "") +
                (AllyClusterPenalty > 0 ? $" [AllyCluster:-{AllyClusterPenalty:F1}]" : "") +
                (FlankingScore > 0 ? $" [Flank:+{FlankingScore:F1}]" : "") +
                (ExposureScore > 0 ? $" [Expo:-{ExposureScore:F1}]" : "") +
                (HideScore > 0 ? $" [Hide:+{HideScore:F1}]" : "") +
                (EnemyTurnThreatSum > 0 ? $" [TurnThreat:-{EnemyTurnThreatSum:F1}]" : "") +
                (StayingAwayBonus > 0 ? $" [StayAway:+{StayingAwayBonus:F1}]" : "");
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

        /// <summary>
        /// ★ v3.10.0: 아군 밀집 패널티 공개 API (OverseerPlan 등 외부 위치 스코어링용)
        /// </summary>
        public static float GetAllyClusterPenalty(Vector3 position, BaseUnitEntity self)
        {
            return CalculateAllyClusterPenalty(position, self);
        }

        /// <summary>
        /// ★ v3.110.9: 아군 밀집 패널티 재조정 — 실전 로그에서 -376까지 폭주하는 케이스 확인.
        ///
        /// 이전 (v3.10.0):
        ///   반경 4타일 선형 감쇠 (0타일=80, 1타일=60, 2타일=40, 3타일=20)
        ///   예약: 0타일=120, 1타일=90, 2타일=60, 3타일=30
        ///   파티 5명 + 예약 3~4개가 근처 → -300 이상 흔함.
        ///   4타일(팀 대형 유지 적정 거리)까지 감점하여 AI가 외톨이 위치 선호 → "너무 멀리" 증상.
        ///
        /// 현재: 2단 곡선 — 1타일 이내(실제 겹침)만 강한 페널티, 1~2.5타일은 약한 페널티, 이상은 0.
        ///   물리적 아군: 0타일=60, 0.5타일=30, 1타일=12, 2타일=4, 2.5+타일=0
        ///   예약 위치:  0타일=80, 0.5타일=40, 1타일=20, 2타일=5, 2+타일=0
        /// </summary>
        private static float CalculateAllyClusterPenalty(Vector3 position, BaseUnitEntity self)
        {
            const float NEAR_RADIUS_TILES = 1.0f;   // 이 거리 이내는 실제 겹침 영역 → 강한 페널티
            const float WIDE_RADIUS_ALLY = 2.5f;    // 물리적 아군 영향 최대 거리
            const float WIDE_RADIUS_RESERVED = 2.0f; // 예약 위치 영향 최대 거리

            const float NEAR_WEIGHT_ALLY = 60f;     // 0타일 기준 최대 페널티
            const float WIDE_WEIGHT_ALLY = 8f;      // 1~2.5타일 구간 선형 약한 패널티
            const float NEAR_WEIGHT_RESERVED = 80f;
            const float WIDE_WEIGHT_RESERVED = 10f;

            float penalty = 0f;
            try
            {
                // 1. 물리적 아군 위치 패널티
                var allUnits = Game.Instance?.TurnController?.AllUnits;
                if (allUnits != null)
                {
                    foreach (var entity in allUnits)
                    {
                        var ally = entity as BaseUnitEntity;
                        if (ally == null || ally == self) continue;
                        if (!ally.IsPlayerFaction || ally.LifeState.IsDead) continue;

                        float distTiles = CombatAPI.MetersToTiles(Vector3.Distance(position, ally.Position));
                        if (distTiles < NEAR_RADIUS_TILES)
                        {
                            // 실제 겹침 영역: 강한 페널티
                            penalty += (NEAR_RADIUS_TILES - distTiles) * NEAR_WEIGHT_ALLY;
                        }
                        else if (distTiles < WIDE_RADIUS_ALLY)
                        {
                            // 인근: 약한 페널티 (대형 유지와 양립)
                            penalty += (WIDE_RADIUS_ALLY - distTiles) * WIDE_WEIGHT_ALLY;
                        }
                    }
                }

                // 2. 예약된 이동 목적지 패널티 (물리 위치보다 약간 강함)
                var reservedPositions = Core.TeamBlackboard.Instance?.GetReservedMovePositions();
                if (reservedPositions != null)
                {
                    for (int i = 0; i < reservedPositions.Count; i++)
                    {
                        float distTiles = CombatAPI.MetersToTiles(Vector3.Distance(position, reservedPositions[i]));
                        if (distTiles < NEAR_RADIUS_TILES)
                        {
                            penalty += (NEAR_RADIUS_TILES - distTiles) * NEAR_WEIGHT_RESERVED;
                        }
                        else if (distTiles < WIDE_RADIUS_RESERVED)
                        {
                            penalty += (WIDE_RADIUS_RESERVED - distTiles) * WIDE_WEIGHT_RESERVED;
                        }
                    }
                }
            }
            catch (Exception) { }

            return penalty;
        }

        #endregion
    }
}
