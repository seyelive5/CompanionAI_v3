// Planning/LLM/StrategyWeightModifier.cs
// ★ Phase 4: Strategic Advisor — StrategicIntent를 TurnState 컨텍스트에 적용.
// TargetScorer.ScoreEnemy()가 이 값을 읽어 가중치 수정.
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;

namespace CompanionAI_v3.Planning.LLM
{
    /// <summary>
    /// ★ Phase 4: LLM StrategicIntent를 TurnState.StrategicContext에 주입.
    /// TargetScorer, BasePlan 등이 이 키를 조회하여 점수 수정.
    /// </summary>
    public static class StrategyWeightModifier
    {
        // ═══════════════════════════════════════════════════════════
        // Context Keys — TurnState.GetContext<T>()로 조회
        // ═══════════════════════════════════════════════════════════

        /// <summary>집중 공격 대상 UniqueId (string). null이면 지정 없음.</summary>
        public const string KEY_FOCUS_TARGET = "LLM_FocusTarget";

        /// <summary>집중 공격 보너스 점수 (float). TargetScorer에서 해당 적에 가산.</summary>
        public const string KEY_FOCUS_BONUS = "LLM_FocusBonus";

        /// <summary>AoE 선호도 (float, 0~1). 높을수록 AoE 공격 우선.</summary>
        public const string KEY_AOE_PREF = "LLM_AoEPreference";

        /// <summary>공격성 수정치 (float, -0.5~+0.5). 양수=공격적, 음수=방어적.</summary>
        public const string KEY_AGGRESSION = "LLM_AggressionMod";

        /// <summary>주요 전략 목표 (IntentType as int). 플랜에서 참조 가능.</summary>
        public const string KEY_PRIMARY_GOAL = "LLM_PrimaryGoal";

        /// <summary>
        /// 기본 집중 공격 보너스 점수.
        /// TargetScorer의 기존 최대 보너스 (SharedTarget=50) 수준.
        /// </summary>
        private const float DEFAULT_FOCUS_BONUS = 50f;

        /// <summary>
        /// StrategicIntent를 TurnState 컨텍스트에 적용.
        /// TargetScorer.ScoreEnemy()가 다음 CreatePlan()에서 이 값을 읽음.
        /// </summary>
        /// <param name="intent">LLM Advisor 결과 (null이면 무시)</param>
        /// <param name="turnState">현재 턴 상태 (컨텍스트 저장 대상)</param>
        /// <param name="situation">전투 상황 (적 목록에서 UniqueId 조회)</param>
        public static void ApplyIntent(StrategicIntent intent, TurnState turnState, Situation situation)
        {
            if (intent == null || turnState == null) return;

            // 1. 주요 목표 저장
            turnState.SetContext(KEY_PRIMARY_GOAL, (int)intent.PrimaryGoal);

            // 2. 집중 공격 대상 지정
            if (intent.PrimaryGoal == IntentType.FocusFire && intent.FocusTargetIndex >= 0)
            {
                var enemies = situation?.Enemies;
                if (enemies != null && intent.FocusTargetIndex < enemies.Count)
                {
                    var target = enemies[intent.FocusTargetIndex];
                    if (target != null)
                    {
                        turnState.SetContext(KEY_FOCUS_TARGET, target.UniqueId);
                        turnState.SetContext(KEY_FOCUS_BONUS, DEFAULT_FOCUS_BONUS);

                        Main.LogDebug($"[StrategyWeightModifier] Focus target: {target.CharacterName} " +
                            $"(idx={intent.FocusTargetIndex}, bonus={DEFAULT_FOCUS_BONUS})");
                    }
                }
            }
            else if (intent.PrimaryGoal == IntentType.AoEClear && intent.FocusTargetIndex >= 0)
            {
                // AoE Clear에서도 타겟 지정 가능 (클러스터 중심 적)
                var enemies = situation?.Enemies;
                if (enemies != null && intent.FocusTargetIndex < enemies.Count)
                {
                    var target = enemies[intent.FocusTargetIndex];
                    if (target != null)
                    {
                        turnState.SetContext(KEY_FOCUS_TARGET, target.UniqueId);
                        // AoE 모드에서는 집중 보너스가 낮음 (AoE 선호가 주력)
                        turnState.SetContext(KEY_FOCUS_BONUS, DEFAULT_FOCUS_BONUS * 0.5f);
                    }
                }
            }

            // 3. AoE 선호도
            turnState.SetContext(KEY_AOE_PREF, intent.AoEPreference);

            // 4. 공격성 수정치 (0.5 중심 → -0.5 ~ +0.5 범위로 변환)
            float aggressionMod = intent.AggressionLevel - 0.5f;
            turnState.SetContext(KEY_AGGRESSION, aggressionMod);

            Main.LogDebug($"[StrategyWeightModifier] Applied: goal={intent.PrimaryGoal}, " +
                $"aoe={intent.AoEPreference:F2}, aggression={aggressionMod:+0.00;-0.00}");
        }

        /// <summary>
        /// TurnState에서 LLM Advisor 컨텍스트를 모두 제거.
        /// 새 턴 시작 시 등에 사용.
        /// </summary>
        public static void ClearContext(TurnState turnState)
        {
            if (turnState == null) return;

            // SetContext에 null을 넣으면 키는 존재하지만 GetContext<string>에서 null 반환
            // HasContext는 여전히 true — 문제 없음 (GetContext가 default 반환)
            turnState.SetContext(KEY_FOCUS_TARGET, null);
            turnState.SetContext(KEY_FOCUS_BONUS, 0f);
            turnState.SetContext(KEY_AOE_PREF, 0.5f);
            turnState.SetContext(KEY_AGGRESSION, 0f);
            turnState.SetContext(KEY_PRIMARY_GOAL, (int)IntentType.Balanced);
        }
    }
}
