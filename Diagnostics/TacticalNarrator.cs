using System;
using UnityEngine;
using Kingmaker.EntitySystem.Entities;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Data;
using CompanionAI_v3.Settings;
using CompanionAI_v3.UI;

namespace CompanionAI_v3.Diagnostics
{
    /// <summary>
    /// ★ v3.48.0: Tactical Narrator — 턴 시작 시 동료 대사 생성 + 표시
    /// TurnOrchestrator에서 초기 TurnPlan 생성 직후 1회 호출
    /// IsEnabled=false면 모든 메서드 즉시 반환 (오버헤드 0)
    /// </summary>
    public static class TacticalNarrator
    {
        public static bool IsEnabled => ModSettings.Instance?.EnableDecisionOverlay ?? false;

        /// <summary>
        /// 7가지 전술 대사 카테고리
        /// </summary>
        public enum SpeechCategory
        {
            Emergency,
            Retreat,
            KillTarget,
            Attack,
            AoE,
            Support,
            EndTurn
        }

        /// <summary>
        /// Plan 생성 직후 호출 — 카테고리 결정 → 대사 조합 → UI 표시
        /// </summary>
        public static void Narrate(BaseUnitEntity unit, TurnPlan plan, Situation situation, TurnStrategy strategy)
        {
            if (!IsEnabled) return;
            if (unit == null || plan == null || situation == null) return;

            try
            {
                // 1. 카테고리 결정
                var category = DetermineCategory(plan, situation, strategy);

                // 2. 동료 식별
                var companionId = CompanionDialogue.IdentifyCompanion(unit);
                string companionKey = companionId.ToString();

                // 3. 대사 조합 (2~3줄)
                var lines = TacticalDialogueDB.GetLines(category, companionKey, situation);
                if (lines == null || lines.Length == 0) return;

                // 4. 이름 색상
                Color nameColor = GetCompanionColor(companionId);

                // 5. UI 표시
                TacticalOverlayUI.Show(unit.CharacterName, lines, nameColor, 5f);

                // 6. CombatReport 기록
                string summary = string.Join(" | ", lines);
                CombatReportCollector.Instance.LogPhase(
                    $"[Narrator] {unit.CharacterName} ({category}): {summary}");

                Main.LogDebug($"[TacticalNarrator] {unit.CharacterName}: category={category}, lines={lines.Length}");
            }
            catch (Exception ex)
            {
                Main.LogError($"[TacticalNarrator] Error: {ex.Message}");
            }
        }

        /// <summary>턴 종료 시 오버레이 숨김</summary>
        public static void OnTurnEnd()
        {
            TacticalOverlayUI.Hide();
        }

        /// <summary>전투 종료 시 정리</summary>
        public static void OnCombatEnd()
        {
            TacticalOverlayUI.Hide();
            TacticalDialogueDB.ResetHistory();
        }

        /// <summary>
        /// TurnPlan + Situation + Strategy → 7개 카테고리 중 1개 선택
        /// Priority order: Emergency > Retreat > KillTarget > AoE > Support > Attack > EndTurn
        /// </summary>
        private static SpeechCategory DetermineCategory(TurnPlan plan, Situation situation, TurnStrategy strategy)
        {
            var priority = plan.Priority;

            // Emergency: HP 위험
            if (priority == TurnPriority.Emergency || priority == TurnPriority.Critical)
                return SpeechCategory.Emergency;

            // Retreat
            if (priority == TurnPriority.Retreat)
                return SpeechCategory.Retreat;

            // KillTarget: 처치 가능 타겟
            if (situation.CanKillBestTarget)
                return SpeechCategory.KillTarget;

            // AoE: 전략 시퀀스가 AoE 계열
            if (strategy != null)
            {
                var seqType = strategy.Sequence;
                if (seqType == SequenceType.AoEFocus ||
                    seqType == SequenceType.BuffedAoE ||
                    seqType == SequenceType.AoERnGChain ||
                    seqType == SequenceType.BuffedRnGAoE)
                    return SpeechCategory.AoE;
            }

            // Support: 지원/버프 우선
            if (priority == TurnPriority.Support || priority == TurnPriority.BuffedAttack)
            {
                // BuffedAttack은 자기 버프 → 공격이므로 Attack으로 분류할 수도 있지만,
                // 힐이 포함되어 있으면 Support
                if (plan.AllActions != null)
                {
                    foreach (var action in plan.AllActions)
                    {
                        if (action.Type == ActionType.Heal || action.Type == ActionType.Support)
                            return SpeechCategory.Support;
                    }
                }
                // 순수 버프+공격이면 Attack
                if (priority == TurnPriority.BuffedAttack)
                    return SpeechCategory.Attack;
                return SpeechCategory.Support;
            }

            // Attack: 직접 공격/이동 공격
            if (priority == TurnPriority.DirectAttack || priority == TurnPriority.MoveAndAttack ||
                priority == TurnPriority.Reload)
                return SpeechCategory.Attack;

            // EndTurn
            if (priority == TurnPriority.EndTurn)
                return SpeechCategory.EndTurn;

            // 기본값: Attack
            return SpeechCategory.Attack;
        }

        /// <summary>CompanionId → Unity Color 변환</summary>
        private static Color GetCompanionColor(CompanionDialogue.CompanionId id)
        {
            switch (id)
            {
                case CompanionDialogue.CompanionId.Abelard:   return HexColor("FFD700");
                case CompanionDialogue.CompanionId.Heinrix:   return HexColor("B0C4DE");
                case CompanionDialogue.CompanionId.Argenta:   return HexColor("FF8C42");
                case CompanionDialogue.CompanionId.Pasqal:    return HexColor("90EE90");
                case CompanionDialogue.CompanionId.Idira:     return HexColor("DDA0DD");
                case CompanionDialogue.CompanionId.Cassia:    return HexColor("FFB6C1");
                case CompanionDialogue.CompanionId.Yrliet:    return HexColor("E0FFFF");
                case CompanionDialogue.CompanionId.Jae:       return HexColor("FFEC8B");
                case CompanionDialogue.CompanionId.Marazhai:  return HexColor("FF6B6B");
                case CompanionDialogue.CompanionId.Ulfar:     return HexColor("87CEEB");
                case CompanionDialogue.CompanionId.Kibellah:  return HexColor("C9A0DC");
                case CompanionDialogue.CompanionId.Solomorne: return HexColor("C0C0C0");
                default: return Color.white;
            }
        }

        private static Color HexColor(string hex)
        {
            if (ColorUtility.TryParseHtmlString("#" + hex, out Color c))
                return c;
            return Color.white;
        }
    }
}
