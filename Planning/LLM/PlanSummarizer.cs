using System.Collections.Generic;
using System.Text;
using Kingmaker.EntitySystem.Entities;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;

namespace CompanionAI_v3.Planning.LLM
{
    /// <summary>
    /// ★ Phase 3: TurnPlan을 LLM이 이해할 자연어 1줄 요약으로 변환.
    /// 각 PlannedAction을 간결한 영어 문구로 변환하고, 화살표(→)로 연결.
    ///
    /// 형식: "Plan [SequenceType]: Action1 → Action2 → Action3"
    /// 예시: "Plan [BuffedAttack]: Buff self with Heroic Act → Attack Psyker (expected kill) → Move to cover"
    /// </summary>
    public static class PlanSummarizer
    {
        // 재사용 StringBuilder (GC 방지)
        private static readonly StringBuilder _sb = new StringBuilder(256);

        // ★ v3.103.0: Once-per-turn 중복 능력 감지용 (GC 방지 static 재사용)
        // KillSequence seed가 아닌 경우 같은 능력 이름 2회 이상 등장 시 [dup] 표시 → Judge에게 룰 위반 힌트
        private static readonly HashSet<string> _tempUsedAbilities = new HashSet<string>(8);

        /// <summary>
        /// TurnPlan + TurnStrategy → 자연어 1줄 요약.
        /// </summary>
        /// <param name="plan">요약할 턴 계획</param>
        /// <param name="strategy">계획의 근거 전략 (null 허용)</param>
        /// <param name="situation">현재 상황 (킬 판정용)</param>
        /// <param name="archetypeTag">★ v3.78.0: 아키타입 태그 (null이면 생략)</param>
        /// <returns>자연어 요약 문자열</returns>
        public static string Summarize(TurnPlan plan, TurnStrategy strategy, Situation situation, string archetypeTag = null)
        {
            if (plan == null) return "(no plan)";

            _sb.Clear();

            // ★ v3.78.0: 아키타입 태그 접두어 "[Aggressive] Plan [...]"
            if (!string.IsNullOrEmpty(archetypeTag))
                _sb.Append('[').Append(archetypeTag).Append("] ");

            // 헤더: "Plan [SequenceType] Focus TargetName:"
            string seqName = strategy != null ? strategy.Sequence.ToString() : plan.Priority.ToString();
            string focusName = situation?.BestTarget?.CharacterName;
            _sb.Append("Plan [").Append(seqName).Append(']');
            if (!string.IsNullOrEmpty(focusName))
                _sb.Append(" Focus ").Append(focusName);
            _sb.Append(": ");

            // ★ v3.103.0: Once-per-turn 룰 위반 감지 준비
            // KillSequence는 kill bonus action으로 정당한 중복 허용 → dup 체크 스킵
            // RnGChain 계열도 이동 후 추가 공격 룰이므로 스킵
            bool allowDuplicates = strategy != null && (
                strategy.PrioritizesKillSequence
                || strategy.Sequence == SequenceType.RnGChain
                || strategy.Sequence == SequenceType.BuffedRnGChain
                || strategy.Sequence == SequenceType.AoERnGChain
                || strategy.Sequence == SequenceType.BuffedRnGAoE);
            _tempUsedAbilities.Clear();

            // 각 액션을 자연어로 변환
            IReadOnlyList<PlannedAction> actions = plan.AllActions;
            bool first = true;

            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];

                // EndTurn은 요약에서 제외 (마지막 행동이므로 의미 없음)
                if (action.Type == ActionType.EndTurn)
                    continue;

                string phrase = DescribeAction(action, strategy, situation);
                if (string.IsNullOrEmpty(phrase))
                    continue;

                // ★ v3.103.0: 같은 능력 중복 사용 감지 — Judge에게 룰 위반 힌트
                // 대부분의 능력은 once-per-turn. 같은 능력 2회 이상 등장 시 [dup] 표시
                if (!allowDuplicates && action.Ability != null)
                {
                    string abilityName = action.Ability.Name;
                    if (!string.IsNullOrEmpty(abilityName) && !_tempUsedAbilities.Add(abilityName))
                    {
                        phrase += " [dup:once-per-turn]";
                    }
                }

                if (!first)
                    _sb.Append(" → ");
                _sb.Append(phrase);
                first = false;
            }

            if (first)
            {
                // 모든 액션이 EndTurn이었으면
                _sb.Append("End turn (no actions)");
            }

            return _sb.ToString();
        }

        /// <summary>
        /// 단일 PlannedAction → 자연어 문구.
        /// </summary>
        private static string DescribeAction(PlannedAction action, TurnStrategy strategy, Situation situation)
        {
            string targetName = GetTargetName(action);
            string abilityName = action.Ability?.Name ?? "?";

            switch (action.Type)
            {
                case ActionType.Attack:
                    return DescribeAttack(action, abilityName, targetName, strategy, situation);

                case ActionType.Buff:
                    return $"Buff self with {abilityName}";

                case ActionType.Heal:
                    return DescribeHeal(action, abilityName, targetName);

                case ActionType.Move:
                    return DescribeMove(action);

                case ActionType.Debuff:
                    return $"Debuff {targetName} with {abilityName}";

                case ActionType.Support:
                    return $"Support {targetName} with {abilityName}";

                case ActionType.Reload:
                    return "Reload weapon";

                case ActionType.WeaponSwitch:
                    return $"Switch to weapon set {action.WeaponSetIndex}";

                case ActionType.Special:
                    return $"Use {abilityName} on {targetName}";

                default:
                    return null;
            }
        }

        /// <summary>공격 액션 설명 — 킬 가능성 등 추가 정보 포함</summary>
        private static string DescribeAttack(PlannedAction action, string abilityName, string targetName,
            TurnStrategy strategy, Situation situation)
        {
            // 킬 판정: 전략이 KillSequence이고 타겟이 BestTarget이면 "expected kill"
            bool isExpectedKill = false;
            if (strategy != null && strategy.PrioritizesKillSequence && strategy.ExpectedKills > 0)
            {
                var targetUnit = action.Target?.Entity as BaseUnitEntity;
                if (targetUnit != null && situation?.BestTarget != null
                    && targetUnit.UniqueId == situation.BestTarget.UniqueId)
                {
                    isExpectedKill = true;
                }
            }

            // AoE 여부
            bool isAoE = action.AllTargets != null && action.AllTargets.Count > 1;
            if (!isAoE && action.Ability != null)
            {
                // Point 타겟 공격도 AoE로 간주
                var target = action.Target;
                if (target != null && target.Entity == null && target.Point != default)
                    isAoE = true;
            }

            string suffix = isExpectedKill ? " (expected kill)"
                          : isAoE ? " (AoE)"
                          : "";

            return $"Attack {targetName} with {abilityName}{suffix}";
        }

        /// <summary>힐 액션 설명 — HP% 포함</summary>
        private static string DescribeHeal(PlannedAction action, string abilityName, string targetName)
        {
            var targetUnit = action.Target?.Entity as BaseUnitEntity;
            if (targetUnit != null)
            {
                float hpPct = targetUnit.Health.HitPointsLeft * 100f / targetUnit.Health.MaxHitPoints;
                return $"Heal {targetName} with {abilityName} ({hpPct:F0}% HP)";
            }
            return $"Heal {targetName} with {abilityName}";
        }

        /// <summary>이동 액션 설명 — 이유 기반으로 구분</summary>
        private static string DescribeMove(PlannedAction action)
        {
            string reason = action.Reason ?? "";
            string lowerReason = reason.ToLowerInvariant();

            if (lowerReason.Contains("retreat") || lowerReason.Contains("safety") || lowerReason.Contains("safe"))
                return "Retreat to safety";
            if (lowerReason.Contains("engage") || lowerReason.Contains("gap") || lowerReason.Contains("close"))
                return "Move to engage";
            if (lowerReason.Contains("cover"))
                return "Move to cover";
            if (lowerReason.Contains("flank"))
                return "Flank enemy";

            // 기본: 이유 그대로 사용 (짧으면)
            if (reason.Length > 0 && reason.Length <= 40)
                return $"Move ({reason})";

            return "Move to position";
        }

        /// <summary>타겟 이름 추출 헬퍼</summary>
        private static string GetTargetName(PlannedAction action)
        {
            if (action.Target == null) return "target";

            var unit = action.Target.Entity as BaseUnitEntity;
            if (unit != null)
                return unit.CharacterName ?? "unit";

            // Point 타겟
            return "area";
        }
    }
}
