using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Settings;
using CompanionAI_v3.Planning.Plans;

namespace CompanionAI_v3.Planning
{
    /// <summary>
    /// ★ v3.0.47: 턴 플래너 - Role에 따라 적절한 Plan 클래스로 위임
    ///
    /// 리팩토링: 각 Role별 로직을 별도 클래스로 분리
    /// - TankPlan: 방어 자세 우선, 도발, 전선 유지
    /// - DPSPlan: Heroic Act, 마무리 스킬, 약한 적 우선
    /// - SupportPlan: 아군 힐/버프 우선, 안전 거리 유지
    /// - BalancedPlan: 상황 적응형
    ///
    /// 모든 Role에서 GapCloser 지원 (v3.0.47 핵심 수정)
    /// </summary>
    public class TurnPlanner
    {
        #region Singleton Plans

        private static readonly TankPlan _tankPlan = new TankPlan();
        private static readonly DPSPlan _dpsPlan = new DPSPlan();
        private static readonly SupportPlan _supportPlan = new SupportPlan();
        private static readonly BalancedPlan _balancedPlan = new BalancedPlan();

        #endregion

        /// <summary>
        /// 턴 계획 생성 - Role에 따라 분기
        /// </summary>
        public TurnPlan CreatePlan(Situation situation, TurnState turnState)
        {
            var role = situation.CharacterSettings?.Role ?? AIRole.Balanced;

            // ★ v3.0.68: 게임 AP 직접 사용 (추적 AP 제거 - 동기화 문제 원천 차단)
            // 게임 AP가 진실의 소스 - 매 행동마다 SituationAnalyzer가 최신 값 가져옴
            float effectiveAP = situation.CurrentAP;

            Main.Log($"[TurnPlanner] Planning for {situation.Unit.CharacterName} (Role={role}): " +
                    $"HP={situation.HPPercent:F0}%, AP={effectiveAP:F1}, MP={situation.CurrentMP:F1}, " +
                    $"Enemies={situation.Enemies.Count}, Hittable={situation.HittableEnemies.Count}");

            try
            {
                switch (role)
                {
                    case AIRole.Tank:
                        return _tankPlan.CreatePlan(situation, turnState);
                    case AIRole.DPS:
                        return _dpsPlan.CreatePlan(situation, turnState);
                    case AIRole.Support:
                        return _supportPlan.CreatePlan(situation, turnState);
                    case AIRole.Balanced:
                    default:
                        return _balancedPlan.CreatePlan(situation, turnState);
                }
            }
            catch (Exception ex)
            {
                Main.LogError($"[TurnPlanner] Error: {ex.Message}");
                var fallbackActions = new List<PlannedAction> { PlannedAction.EndTurn($"Error: {ex.Message}") };
                return new TurnPlan(fallbackActions, TurnPriority.EndTurn, "Error fallback");
            }
        }
    }
}
