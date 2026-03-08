using Kingmaker.EntitySystem.Entities;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Diagnostics
{
    /// <summary>
    /// ★ v3.44.0: TurnPlan + Situation → 사용자용 자연어 요약 생성
    /// 새 분석 로직 없이 기존 데이터를 번역/요약
    /// </summary>
    public static class NarrativeBuilder
    {
        public enum NarrativeLevel
        {
            User,       // 자연어 1~3줄
            Developer   // 나중에 — TargetScorer 점수, AP Budget 등
        }

        /// <summary>
        /// Plan + Situation → NarrativeEntry
        /// </summary>
        public static NarrativeEntry Build(TurnPlan plan, Situation situation,
            NarrativeLevel level = NarrativeLevel.User)
        {
            if (plan == null || situation == null) return null;

            var entry = new NarrativeEntry
            {
                UnitName = situation.Unit?.CharacterName ?? "?",
                Role = ExtractRole(plan),
                HPPercent = situation.HPPercent,
                Round = GetCurrentRound()
            };

            if (plan.AllActions == null || plan.AllActions.Count == 0)
            {
                entry.Lines.Add(L("narr_end_no_targets"));
                return entry;
            }

            // 긴급 상황 헤더
            if (plan.Priority == TurnPriority.Emergency || plan.Priority == TurnPriority.Critical)
            {
                if (situation.HPPercent < 30f)
                    entry.Lines.Add(string.Format(L("narr_emergency_heal"), $"{situation.HPPercent:F0}"));
            }

            foreach (var action in plan.AllActions)
            {
                string line = NarrateAction(action, situation);
                if (line != null)
                    entry.Lines.Add(line);
            }

            // 빈 결과 방지
            if (entry.Lines.Count == 0)
                entry.Lines.Add(L("narr_end_wait"));

            return entry;
        }

        private static string NarrateAction(PlannedAction action, Situation situation)
        {
            if (action == null) return null;

            var target = action.Target?.Entity as BaseUnitEntity;
            string targetName = target?.CharacterName ?? "?";

            switch (action.Type)
            {
                case ActionType.Attack:
                    return NarrateAttack(action, target, targetName, situation);

                case ActionType.Move:
                    return NarrateMove(action, situation);

                case ActionType.Heal:
                    // CombatCache.GetHPPercent는 이미 0~100 범위
                    float targetHP = target != null
                        ? CombatCache.GetHPPercent(target)
                        : 0f;
                    return string.Format(L("narr_heal"), targetName, $"{targetHP:F0}");

                case ActionType.Buff:
                case ActionType.Support:
                    // 도발 체크
                    if (action.Ability != null && AbilityDatabase.IsTaunt(action.Ability))
                        return L("narr_taunt");
                    // 패밀리어 재활성화 체크
                    if (action.IsFamiliarTarget)
                        return string.Format(L("narr_familiar_reactivate"), targetName);
                    string abilityName = action.Ability?.Name ?? "?";
                    string buffTarget = target != null ? targetName
                        : (action.Target?.Point != null ? "" : situation.Unit?.CharacterName ?? "");
                    return string.Format(L("narr_buff"), buffTarget, abilityName);

                case ActionType.Reload:
                    return L("narr_reload");

                case ActionType.EndTurn:
                    return NarrateEndTurn(situation);

                default:
                    return null;
            }
        }

        private static string NarrateAttack(PlannedAction action, BaseUnitEntity target,
            string targetName, Situation situation)
        {
            if (target == null)
                return string.Format(L("narr_attack_best"), targetName);

            // 이유 판별 우선순위: 아군 위협 > 처치 가능 > 최근접 > 기본
            int alliesTargeting = TeamBlackboard.Instance.CountAlliesTargeting(target);
            if (alliesTargeting > 0)
                return string.Format(L("narr_attack_threatening"), targetName, alliesTargeting);

            if (situation.CanKillBestTarget && target == situation.BestTarget)
                return string.Format(L("narr_attack_killable"), targetName);

            if (target == situation.NearestEnemy)
                return string.Format(L("narr_attack_nearest"), targetName);

            return string.Format(L("narr_attack_best"), targetName);
        }

        private static string NarrateMove(PlannedAction action, Situation situation)
        {
            string reason = action.Reason ?? "";

            // Reason 문자열에서 의도 추출
            if (reason.Contains("heal") || reason.Contains("Heal"))
            {
                string healTarget = ExtractNameFromReason(reason);
                return string.Format(L("narr_move_heal"), healTarget);
            }

            if (reason.Contains("Retreat") || reason.Contains("retreat") || reason.Contains("safe"))
                return L("narr_retreat");

            // 기본: 공격 접근
            string approachTarget = situation.NearestEnemy?.CharacterName
                ?? ExtractNameFromReason(reason) ?? "?";
            return string.Format(L("narr_move_approach"), approachTarget);
        }

        private static string NarrateEndTurn(Situation situation)
        {
            if (situation.CurrentAP < 1f)
                return L("narr_end_no_ap");
            if (situation.HittableEnemies == null || situation.HittableEnemies.Count == 0)
                return L("narr_end_no_targets");
            return L("narr_end_wait");
        }

        private static string ExtractRole(TurnPlan plan)
        {
            if (string.IsNullOrEmpty(plan.Reasoning)) return "?";
            int colonIdx = plan.Reasoning.IndexOf(':');
            return colonIdx > 0 ? plan.Reasoning.Substring(0, colonIdx).Trim() : "?";
        }

        private static string ExtractNameFromReason(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "?";
            // "Move to heal Alice" → "Alice", "Melee position near Mutant" → "Mutant"
            string[] words = reason.Split(' ');
            return words.Length > 0 ? words[words.Length - 1] : "?";
        }

        private static int GetCurrentRound()
        {
            try { return Kingmaker.Game.Instance?.TurnController?.CombatRound ?? 0; }
            catch { return 0; }
        }

        private static string L(string key) => Localization.Get(key);
    }
}
