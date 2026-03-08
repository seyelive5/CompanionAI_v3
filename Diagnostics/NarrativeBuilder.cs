using System.Collections.Generic;
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
    /// 행동을 그룹화하여 전략적 의도를 압축 표현 (최대 4~5줄)
    /// </summary>
    public static class NarrativeBuilder
    {
        public enum NarrativeLevel
        {
            User,       // 자연어 1~3줄
            Developer   // 나중에 — TargetScorer 점수, AP Budget 등
        }

        // 임시 리스트 재사용 (zero-allocation)
        private static readonly List<string> _tempTargets = new List<string>();
        private static readonly List<string> _tempLines = new List<string>();

        /// <summary>
        /// Plan + Situation → NarrativeEntry (그룹화된 자연어 요약)
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

            // ★ 전략 의도 요약 (Plan Priority 기반, 1줄)
            string strategy = NarrateStrategy(plan, situation);
            if (strategy != null)
                entry.Lines.Add(strategy);

            // ★ 행동 그룹화 — 같은 타입+같은 능력을 묶어서 표현
            BuildGroupedLines(plan, situation, _tempLines);
            for (int i = 0; i < _tempLines.Count; i++)
                entry.Lines.Add(_tempLines[i]);

            // 빈 결과 방지
            if (entry.Lines.Count == 0)
                entry.Lines.Add(L("narr_end_wait"));

            return entry;
        }

        /// <summary>
        /// 전략 의도 1줄 요약 (Plan Priority + 상황 기반)
        /// </summary>
        private static string NarrateStrategy(TurnPlan plan, Situation situation)
        {
            switch (plan.Priority)
            {
                case TurnPriority.Critical:
                    return L("narr_strat_critical");
                case TurnPriority.Emergency:
                    return string.Format(L("narr_strat_emergency"), $"{situation.HPPercent:F0}");
                case TurnPriority.Retreat:
                    return L("narr_strat_retreat");
                case TurnPriority.Reload:
                    return L("narr_strat_reload");
                case TurnPriority.Support:
                    return L("narr_strat_support");
                case TurnPriority.EndTurn:
                    return NarrateEndTurn(situation);
                default:
                    // BuffedAttack, DirectAttack, MoveAndAttack — 공격 턴
                    // 전략 라인 없이 행동 그룹으로 충분
                    return null;
            }
        }

        /// <summary>
        /// 행동 목록을 그룹화하여 압축된 라인 생성
        /// 같은 능력+같은 ActionType → 타겟 묶음
        /// 예: "아군 5명에게 지휘의 목소리 사용" (10줄 → 1줄)
        /// </summary>
        private static void BuildGroupedLines(TurnPlan plan, Situation situation, List<string> lines)
        {
            lines.Clear();

            string currentAbilityName = null;
            ActionType currentType = (ActionType)(-1);
            _tempTargets.Clear();
            bool currentIsTaunt = false;
            bool currentIsFamiliar = false;

            for (int i = 0; i < plan.AllActions.Count; i++)
            {
                var action = plan.AllActions[i];
                if (action == null) continue;

                string abilityName = action.Ability?.Name;
                bool isSameGroup = (action.Type == currentType && abilityName == currentAbilityName
                    && (action.Type == ActionType.Buff || action.Type == ActionType.Support
                        || action.Type == ActionType.Attack));

                if (!isSameGroup && _tempTargets.Count > 0)
                {
                    // 이전 그룹 플러시
                    FlushGroup(currentType, currentAbilityName, _tempTargets, situation, lines,
                        currentIsTaunt, currentIsFamiliar);
                    _tempTargets.Clear();
                }

                // 새 그룹 시작 또는 기존 그룹에 추가
                currentType = action.Type;
                currentAbilityName = abilityName;
                currentIsTaunt = action.Ability != null && AbilityDatabase.IsTaunt(action.Ability);
                currentIsFamiliar = action.IsFamiliarTarget;

                // 그룹화 불가능한 타입은 즉시 출력
                if (action.Type == ActionType.Move || action.Type == ActionType.Reload
                    || action.Type == ActionType.EndTurn)
                {
                    if (_tempTargets.Count > 0)
                    {
                        FlushGroup(currentType, currentAbilityName, _tempTargets, situation, lines,
                            currentIsTaunt, currentIsFamiliar);
                        _tempTargets.Clear();
                    }
                    string singleLine = NarrateSingleAction(action, situation);
                    if (singleLine != null) lines.Add(singleLine);
                    currentType = (ActionType)(-1);
                    currentAbilityName = null;
                    continue;
                }

                // 타겟 이름 수집
                var target = action.Target?.Entity as BaseUnitEntity;
                string targetName = target?.CharacterName ?? "?";
                if (!_tempTargets.Contains(targetName))
                    _tempTargets.Add(targetName);
            }

            // 마지막 그룹 플러시
            if (_tempTargets.Count > 0)
                FlushGroup(currentType, currentAbilityName, _tempTargets, situation, lines,
                    currentIsTaunt, currentIsFamiliar);
            _tempTargets.Clear();
        }

        /// <summary>
        /// 누적된 그룹을 1줄로 출력
        /// </summary>
        private static void FlushGroup(ActionType type, string abilityName,
            List<string> targets, Situation situation, List<string> lines,
            bool isTaunt, bool isFamiliar)
        {
            if (targets.Count == 0) return;

            // 도발은 특수 문장
            if (isTaunt)
            {
                lines.Add(L("narr_taunt"));
                return;
            }

            // 패밀리어 재활성화
            if (isFamiliar && targets.Count == 1)
            {
                lines.Add(string.Format(L("narr_familiar_reactivate"), targets[0]));
                return;
            }

            string name = abilityName ?? type.ToString();

            if (type == ActionType.Attack)
            {
                if (targets.Count == 1)
                {
                    // 단일 타겟 공격 — 이유 포함
                    lines.Add(NarrateAttackTarget(targets[0], situation));
                }
                else
                {
                    // 다중 타겟 공격
                    string targetList = JoinTargets(targets);
                    lines.Add(string.Format(L("narr_attack_multi"), targetList, targets.Count));
                }
                return;
            }

            if (type == ActionType.Buff || type == ActionType.Support)
            {
                if (targets.Count == 1)
                {
                    lines.Add(string.Format(L("narr_buff"), targets[0], name));
                }
                else
                {
                    // "아군 N명에게 X 사용"
                    lines.Add(string.Format(L("narr_buff_multi"), name, targets.Count));
                }
                return;
            }

            if (type == ActionType.Heal)
            {
                if (targets.Count == 1)
                {
                    lines.Add(string.Format(L("narr_heal"), targets[0], "?"));
                }
                else
                {
                    lines.Add(string.Format(L("narr_heal_multi"), targets.Count));
                }
                return;
            }
        }

        /// <summary>단일 공격 타겟 — 이유 포함</summary>
        private static string NarrateAttackTarget(string targetName, Situation situation)
        {
            // BestTarget/NearestEnemy와 이름 비교로 이유 판별
            string bestName = situation.BestTarget?.CharacterName;
            string nearestName = situation.NearestEnemy?.CharacterName;

            if (situation.CanKillBestTarget && targetName == bestName)
                return string.Format(L("narr_attack_killable"), targetName);

            if (targetName == nearestName)
                return string.Format(L("narr_attack_nearest"), targetName);

            if (targetName == bestName)
                return string.Format(L("narr_attack_best"), targetName);

            return string.Format(L("narr_attack_best"), targetName);
        }

        /// <summary>그룹화 불가능한 행동 (Move, Reload, EndTurn)</summary>
        private static string NarrateSingleAction(PlannedAction action, Situation situation)
        {
            switch (action.Type)
            {
                case ActionType.Move:
                    return NarrateMove(action, situation);
                case ActionType.Reload:
                    return L("narr_reload");
                case ActionType.EndTurn:
                    return NarrateEndTurn(situation);
                default:
                    return null;
            }
        }

        private static string NarrateMove(PlannedAction action, Situation situation)
        {
            string reason = action.Reason ?? "";

            if (reason.Contains("heal") || reason.Contains("Heal"))
            {
                string healTarget = ExtractNameFromReason(reason);
                return string.Format(L("narr_move_heal"), healTarget);
            }

            if (reason.Contains("Retreat") || reason.Contains("retreat") || reason.Contains("safe"))
                return L("narr_retreat");

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
            string[] words = reason.Split(' ');
            return words.Length > 0 ? words[words.Length - 1] : "?";
        }

        private static string JoinTargets(List<string> targets)
        {
            if (targets.Count <= 3)
                return string.Join(", ", targets);
            // 4+ 타겟이면 처음 2개 + "외 N명"
            return $"{targets[0]}, {targets[1]} +{targets.Count - 2}";
        }

        private static int GetCurrentRound()
        {
            try { return Kingmaker.Game.Instance?.TurnController?.CombatRound ?? 0; }
            catch { return 0; }
        }

        private static string L(string key) => Localization.Get(key);
    }
}
