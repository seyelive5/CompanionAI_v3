using System;
using UnityEngine;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.Logging;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.GameInterface
{
    public static partial class MovementAPI
    {
        #region Influence Map Integration (v3.2.00)

        // ★ v3.110.16: ApplyInfluenceScores 메서드 제거 (Phase C).
        //   InfluenceMap 기반 InfT/InfC 축은 역제곱 거리 추정으로 정보 가치 낮고 ThreatScore/CoverScore와 중복.
        //   ExposureScore(v3.110.15, sqrt(hittable) × 10)가 "적 밀집 회피" 역할 대체.
        //   Frontline 기반 Role 페널티(ApplyFrontlineScore)도 함께 제거.
        //
        //   Blackboard 기반 점수(SharedTargetBonus, TacticalAdjustment)는 의미 있으므로 유지.
        //   EvaluatePosition에서 직접 ApplyBlackboardScores를 호출하도록 이동.

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

            // ★ v3.9.50: 공격 기회 기반 전진 보너스 (무조건 적용)
            // 이전: aggressionMod > 1일 때만 보너스 → 팀 신뢰도 낮으면 보너스 0
            // 수정: 공격 가능 위치는 항상 보너스, 신뢰도에 따라 증폭
            if (score.HittableEnemyCount > 0)
            {
                float attackOpportunityBonus = score.HittableEnemyCount * 8f;
                attackOpportunityBonus *= Math.Max(0.6f, aggressionMod);
                score.TacticalAdjustment += attackOpportunityBonus;
            }

            // 공격 성향이 높으면 추가 전진 보너스
            if (aggressionMod > 1f)
            {
                score.TacticalAdjustment += (aggressionMod - 1f) * 12f;
            }
            if (defenseMod > 1f)
            {
                // ★ v3.111.2 Phase 6 follow-up: CoverScore 스케일 변경 (15-40 → 0.01-30, 공격자 semantics).
                // 방어적 상황의 "엄폐 중시"는 HideScore (방어자 관점)로 재타겟팅.
                // ★ v3.111.15 Phase C.1: HideValue 정규화로 HideScore max 180 → 110 (×1.636 감소).
                // ★ v3.113.0 (I3): 정규화 보정 0.05 → 0.082 (= 0.05×1.636). pre-v3.111.15 effective weight 와 동등 유지.
                score.TacticalAdjustment += score.HideScore * (defenseMod - 1f) * 0.082f;
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
                    // ★ v3.111.2 Phase 6 follow-up: CoverScore → HideScore로 재타겟팅 (Phase 6 semantics 변경 대응).
                    // 방어 모드의 "엄폐 중시"는 방어자 관점 HideScore가 적합.
                    // ★ v3.111.15 Phase C.1: HideValue 정규화로 HideScore max 180 → 110 (×1.636 감소).
                    // ★ v3.113.0 (I3): 정규화 보정 0.03 → 0.049 (= 0.03×1.636). pre-v3.111.15 effective weight 와 동등 유지.
                    score.TacticalAdjustment += score.HideScore * 0.049f;
                    break;
            }

            // ★ v3.8.80: Blackboard 적용 결과 로깅 (SharedTarget 보너스가 실제 적용된 경우만)
            // 기존: || 조건 → TacticalAdjustment가 Attack 전술에서 항상 비-0 → 모든 타일 로깅 (1,400+줄)
            // 수정: && 조건 → SharedTarget 보너스가 있는 의미 있는 타일만 로깅 (~50줄)
            if (score.SharedTargetBonus != 0 && score.TacticalAdjustment != 0)
            {
                if (Main.IsDebugEnabled) Log.Engine.Debug($"[MovementAPI] Blackboard: ST={score.SharedTargetBonus:F1}, Tac={score.TacticalAdjustment:F1}, Tactic={tactic}");
            }
        }

        // ★ v3.110.16: ApplyFrontlineScore 제거 (Phase C). InfT/InfC 필드가 제거됐으므로 이 함수도 무효.
        // ★ v3.110.16: GetSafestPosition 제거 (InfluenceMap.SafeZones 의존).

        #endregion
    }
}
