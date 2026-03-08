using Kingmaker.EntitySystem.Entities;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Diagnostics
{
    /// <summary>
    /// ★ v3.44.0: AI 결정 내러티브 진입점
    /// Plan 생성 후 호출 → 자연어 생성 → UI 표시 → CombatReport 기록
    /// IsEnabled=false면 모든 메서드 즉시 반환 (오버헤드 0)
    /// </summary>
    public class DecisionNarrator
    {
        private static DecisionNarrator _instance;
        public static DecisionNarrator Instance => _instance ??= new DecisionNarrator();

        private readonly DecisionHistory _history = new DecisionHistory();
        public DecisionHistory History => _history;

        public static bool IsEnabled => ModSettings.Instance?.EnableDecisionOverlay ?? false;

        /// <summary>현재 일시정지 상태 (DecisionOverlayUI가 참조)</summary>
        public bool IsPaused { get; set; }

        /// <summary>
        /// Plan 생성 직후 호출 — 내러티브 생성 + UI + CombatReport
        /// </summary>
        public void Narrate(TurnPlan plan, Situation situation, BaseUnitEntity unit)
        {
            if (!IsEnabled) return;
            if (plan == null || situation == null) return;

            var entry = NarrativeBuilder.Build(plan, situation);
            if (entry == null) return;

            _history.Add(entry);

            // CombatReport에도 기록 (JSON 리포트 풍부화)
            if (entry.Lines.Count > 0)
            {
                string summary = string.Join(" | ", entry.Lines);
                CombatReportCollector.Instance.LogPhase($"[Narrator] {summary}");
            }

            // UI 갱신 요청
            UI.DecisionOverlayUI.RequestUpdate();
        }

        /// <summary>전투 종료 시 히스토리 초기화</summary>
        public void OnCombatEnd()
        {
            _history.Clear();
            IsPaused = false;
        }

        /// <summary>일시정지 해제</summary>
        public void Resume()
        {
            IsPaused = false;
            UnityEngine.Time.timeScale = 1f;
        }
    }
}
