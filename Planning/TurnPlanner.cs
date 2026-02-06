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
    /// ★ v3.0.92: Auto 역할 - RoleDetector로 최적 역할 자동 감지
    ///
    /// 리팩토링: 각 Role별 로직을 별도 클래스로 분리
    /// - TankPlan: 방어 자세 우선, 도발, 전선 유지
    /// - DPSPlan: Heroic Act, 마무리 스킬, 약한 적 우선
    /// - SupportPlan: 아군 힐/버프 우선, 안전 거리 유지
    /// - Auto: 캐릭터 능력 분석 → Tank/DPS/Support 중 최적 선택
    ///
    /// 모든 Role에서 GapCloser 지원 (v3.0.47 핵심 수정)
    /// </summary>
    public class TurnPlanner
    {
        #region Singleton Plans

        private static readonly TankPlan _tankPlan = new TankPlan();
        private static readonly DPSPlan _dpsPlan = new DPSPlan();
        private static readonly SupportPlan _supportPlan = new SupportPlan();
        private static readonly OverseerPlan _overseerPlan = new OverseerPlan();  // ★ v3.7.91

        // ★ v3.0.92: Auto 모드에서 감지된 역할 캐싱 (전투 중 일관성 유지)
        private static readonly Dictionary<string, AIRole> _detectedRoles = new Dictionary<string, AIRole>();

        #endregion

        /// <summary>
        /// 턴 계획 생성 - Role에 따라 분기
        /// </summary>
        public TurnPlan CreatePlan(Situation situation, TurnState turnState)
        {
            var configuredRole = situation.CharacterSettings?.Role ?? AIRole.Auto;
            var effectiveRole = configuredRole;

            // ★ v3.0.92: Auto 모드 - 캐릭터 능력 기반 역할 감지
            if (configuredRole == AIRole.Auto)
            {
                effectiveRole = GetOrDetectRole(situation.Unit);
            }

            // ★ v3.0.68: 게임 AP 직접 사용 (추적 AP 제거 - 동기화 문제 원천 차단)
            float effectiveAP = situation.CurrentAP;

            string roleDisplay = configuredRole == AIRole.Auto
                ? $"Auto→{effectiveRole}"
                : effectiveRole.ToString();

            Main.Log($"[TurnPlanner] Planning for {situation.Unit.CharacterName} (Role={roleDisplay}): " +
                    $"HP={situation.HPPercent:F0}%, AP={effectiveAP:F1}, MP={situation.CurrentMP:F1}, " +
                    $"Enemies={situation.Enemies.Count}, Hittable={situation.HittableEnemies.Count}");

            try
            {
                switch (effectiveRole)
                {
                    case AIRole.Tank:
                        return _tankPlan.CreatePlan(situation, turnState);
                    case AIRole.DPS:
                        return _dpsPlan.CreatePlan(situation, turnState);
                    case AIRole.Support:
                        return _supportPlan.CreatePlan(situation, turnState);
                    case AIRole.Overseer:  // ★ v3.7.91: 사역마 중심 전략
                        return _overseerPlan.CreatePlan(situation, turnState);
                    default:
                        // Auto가 아닌 경우 여기 도달하지 않음, 폴백으로 DPS
                        return _dpsPlan.CreatePlan(situation, turnState);
                }
            }
            catch (Exception ex)
            {
                Main.LogError($"[TurnPlanner] Error: {ex.Message}");
                var fallbackActions = new List<PlannedAction> { PlannedAction.EndTurn($"Error: {ex.Message}") };
                return new TurnPlan(fallbackActions, TurnPriority.EndTurn, "Error fallback");
            }
        }

        /// <summary>
        /// ★ v3.0.92: 캐싱된 역할 반환 또는 새로 감지
        /// </summary>
        private AIRole GetOrDetectRole(Kingmaker.EntitySystem.Entities.BaseUnitEntity unit)
        {
            if (unit == null) return AIRole.DPS;

            string unitId = unit.UniqueId;

            // 이미 감지된 역할이 있으면 재사용 (전투 중 일관성)
            if (_detectedRoles.TryGetValue(unitId, out var cachedRole))
            {
                return cachedRole;
            }

            // 새로 감지
            var detectedRole = RoleDetector.DetectOptimalRole(unit);
            _detectedRoles[unitId] = detectedRole;

            return detectedRole;
        }

        /// <summary>
        /// ★ v3.0.92: 캐시 초기화 (전투 시작 시 호출)
        /// </summary>
        public static void ClearDetectedRolesCache()
        {
            _detectedRoles.Clear();
            Main.Log("[TurnPlanner] Cleared detected roles cache");
        }
    }
}
