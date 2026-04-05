using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Settings;
using CompanionAI_v3.GameInterface;
using UnityEngine;

namespace CompanionAI_v3.Planning.LLM
{
    /// <summary>
    /// ★ Phase 3: LLM-as-Judge 후보 플랜.
    /// 하나의 타겟 중심으로 생성된 완전한 TurnPlan + 메타데이터.
    /// </summary>
    public class CandidatePlan
    {
        /// <summary>전략에서 파생된 완전한 턴 계획</summary>
        public TurnPlan Plan;

        /// <summary>이 계획의 근거가 된 전략</summary>
        public TurnStrategy Strategy;

        /// <summary>전략 유틸리티 점수 (가중 점수 기준)</summary>
        public float UtilityScore;

        /// <summary>LLM이 이해할 자연어 요약 (1줄)</summary>
        public string Summary;

        /// <summary>이 플랜의 주 타겟 (요약/비교용)</summary>
        public BaseUnitEntity FocusTarget;

        /// <summary>★ v3.78.0: 아키타입 태그 (Aggressive, AoE Sweep, Debuff→Attack, Support, Defensive)</summary>
        public string ArchetypeTag;
    }

    /// <summary>
    /// ★ v3.78.0: 전술 아키타입 열거형.
    /// 각 아키타입은 다른 타겟 + 전략 플래그 + 컨텍스트 조합으로 구별되는 플랜을 생성.
    /// ★ v3.80.0: 10개 추가 (총 15개) — 조건 완화로 후보 다양성 향상.
    /// ★ NOTE: LLM-as-Scorer 도입 후 아키타입 기반 생성은 비활성화.
    ///   향후 필요 시 복원 가능하도록 열거형은 유지.
    /// </summary>
    public enum TacticalArchetype
    {
        /// <summary>올인 공격 — BestTarget, 버프+킬 시퀀스 우선</summary>
        Aggressive,

        /// <summary>범위 소탕 — 클러스터 중심 적, AoE 우선</summary>
        AoEClear,

        /// <summary>약화 후 공격 — 최고 HP 적, 디버프 후 공격</summary>
        DebuffSetup,

        /// <summary>아군 지원 — 힐/버프 우선, 공격은 보조</summary>
        Support,

        /// <summary>방어적 — 가장 가까운 적, 후퇴/방어 우선</summary>
        Defensive,

        // ── ★ v3.80.0: 신규 아키타입 ──

        /// <summary>대체 타겟 — 2순위 적, Aggressive와 동일 전략</summary>
        AltTarget,

        /// <summary>마무리 — HP &lt; 25% 적 처치 우선</summary>
        Finisher,

        /// <summary>버프 집중 — 2개 이상 버프 후 공격</summary>
        BuffStack,

        /// <summary>팀 지원 — 아군 버프/존 배치, 공격 최소화</summary>
        TeamEnabler,

        /// <summary>존 컨트롤 — 포지셔널 버프/스트라타젬 배치</summary>
        ZoneControl,

        /// <summary>오버워치 대기 — 재장전/대기, 다음 턴 준비</summary>
        OverwatchSetup,

        /// <summary>아군 보호 — 부상 아군 근처 방어</summary>
        ProtectAlly,

        /// <summary>측면 공격 — 다른 각도에서 접근</summary>
        Flanking,

        /// <summary>후퇴 치유 — 빠지면서 회복</summary>
        RetreatHeal
    }

    /// <summary>
    /// ★ LLM-as-Scorer: 후보 플랜 생성기 (간소화).
    /// 이전: 15개 아키타입 기반 → 현재: LLM 가중치 플랜 + 베이스라인 플랜 (최대 2개).
    /// LLMScorer가 출력한 ScorerWeights를 TargetScorer/UtilityScorer에 주입하여
    /// 동일한 TurnPlanner에서 다른 행동 패턴을 유도.
    /// </summary>
    public static class CandidatePlanGenerator
    {
        /// <summary>
        /// ★ LLM-as-Scorer: 가중치 기반 후보 플랜 생성.
        /// Plan A: LLM 가중치 적용 (ScorerWeights → TargetScorer/UtilityScorer)
        /// Plan B: 베이스라인 (가중치 없음)
        /// </summary>
        /// <param name="situation">현재 전투 상황</param>
        /// <param name="turnState">현재 턴 상태</param>
        /// <param name="planner">TurnPlanner 인스턴스</param>
        /// <param name="role">유닛 역할</param>
        /// <param name="llmWeights">LLM Scorer 가중치 (null이면 베이스라인만)</param>
        /// <param name="maxCandidates">최대 후보 수 (기본 2)</param>
        public static List<CandidatePlan> Generate(
            Situation situation, TurnState turnState,
            TurnPlanner planner, AIRole role,
            ScorerWeights llmWeights = null,
            int maxCandidates = 2)
        {
            var candidates = new List<CandidatePlan>(maxCandidates);

            if (situation?.Unit == null || situation.Enemies == null || situation.Enemies.Count == 0)
            {
                Main.Log("[CandidatePlanGenerator] No unit or enemies — returning empty");
                return candidates;
            }

            var originalBestTarget = situation.BestTarget;
            var originalCanKill = situation.CanKillBestTarget;

            Main.Log($"[CandidatePlanGenerator] Generating candidates for {situation.Unit.CharacterName}" +
                $" (BestTarget={originalBestTarget?.CharacterName ?? "none"}, " +
                $"HP={situation.HPPercent:F0}%, weights={llmWeights?.ToString() ?? "none"})");

            try
            {
                // ══════════════════════════════════════════════════
                // Plan A: LLM 가중치 적용 플랜
                // ══════════════════════════════════════════════════
                if (llmWeights != null && !llmWeights.IsDefault)
                {
                    var llmState = CreateFreshTurnState(turnState, situation);
                    llmState.SetContext(StrategicContextKeys.LLM_ScorerWeights, llmWeights);

                    // PriorityTarget 지정 시 BestTarget 오버라이드
                    if (llmWeights.PriorityTarget >= 0 && llmWeights.PriorityTarget < situation.Enemies.Count)
                    {
                        var priorityEnemy = situation.Enemies[llmWeights.PriorityTarget];
                        if (priorityEnemy != null)
                        {
                            situation.BestTarget = priorityEnemy;
                            situation.CanKillBestTarget = EstimateCanKill(situation, priorityEnemy);
                        }
                    }

                    // DefensiveStance → 전략 컨텍스트 주입
                    if (llmWeights.DefensiveStance)
                    {
                        llmState.SetContext(StrategicContextKeys.LLM_DefensiveMode, true);
                        llmState.SetContext(StrategicContextKeys.TacticalObjective, "Retreat");
                    }

                    TargetScorer.SetActiveTurnState(llmState);
                    TurnPlan llmPlan;
                    try
                    {
                        llmPlan = planner.CreatePlan(situation, llmState);
                    }
                    catch (System.Exception ex)
                    {
                        Main.Log($"[CandidatePlanGenerator] LLM weights plan failed: {ex.Message}");
                        llmPlan = null;
                    }
                    TargetScorer.ClearActiveTurnState();

                    // BestTarget 복원
                    situation.BestTarget = originalBestTarget;
                    situation.CanKillBestTarget = originalCanKill;

                    if (llmPlan != null)
                    {
                        var llmStrategy = llmState.GetContext<TurnStrategy>(StrategicContextKeys.TurnStrategyKey, null);
                        float utilityScore = llmWeights.FocusFire * 100f + llmWeights.AoEWeight * 50f;
                        if (llmStrategy != null)
                        {
                            if (llmStrategy.ExpectedKills > 0) utilityScore += llmStrategy.ExpectedKills * 40f;
                            utilityScore += llmStrategy.ExpectedTotalDamage * 0.1f;
                        }

                        // 요약 시 BestTarget을 임시로 LLM 타겟으로 설정
                        var llmFocusTarget = (llmWeights.PriorityTarget >= 0 && llmWeights.PriorityTarget < situation.Enemies.Count)
                            ? situation.Enemies[llmWeights.PriorityTarget]
                            : originalBestTarget;
                        situation.BestTarget = llmFocusTarget;

                        candidates.Add(new CandidatePlan
                        {
                            Plan = llmPlan,
                            Strategy = llmStrategy,
                            UtilityScore = utilityScore,
                            Summary = PlanSummarizer.Summarize(llmPlan, llmStrategy, situation, "LLM Weights"),
                            FocusTarget = llmFocusTarget,
                            ArchetypeTag = "LLM Weights"
                        });

                        situation.BestTarget = originalBestTarget;
                        situation.CanKillBestTarget = originalCanKill;
                    }
                }

                // ══════════════════════════════════════════════════
                // Plan B: 베이스라인 (LLM 가중치 없음)
                // ══════════════════════════════════════════════════
                var baseState = CreateFreshTurnState(turnState, situation);
                // LLM_ScorerWeights 설정하지 않음 — 순수 기존 스코어링
                TurnPlan basePlan;
                try
                {
                    basePlan = planner.CreatePlan(situation, baseState);
                }
                catch (System.Exception ex)
                {
                    Main.Log($"[CandidatePlanGenerator] Baseline plan failed: {ex.Message}");
                    basePlan = null;
                }

                if (basePlan != null)
                {
                    var baseStrategy = baseState.GetContext<TurnStrategy>(StrategicContextKeys.TurnStrategyKey, null);
                    float baseUtility = 100f;
                    if (baseStrategy != null)
                    {
                        if (baseStrategy.ExpectedKills > 0) baseUtility += baseStrategy.ExpectedKills * 40f;
                        baseUtility += baseStrategy.ExpectedTotalDamage * 0.1f;
                    }

                    candidates.Add(new CandidatePlan
                    {
                        Plan = basePlan,
                        Strategy = baseStrategy,
                        UtilityScore = baseUtility,
                        Summary = PlanSummarizer.Summarize(basePlan, baseStrategy, situation, "Baseline"),
                        FocusTarget = originalBestTarget,
                        ArchetypeTag = "Baseline"
                    });
                }
            }
            finally
            {
                // ★ 반드시 원본 BestTarget 복원
                situation.BestTarget = originalBestTarget;
                situation.CanKillBestTarget = originalCanKill;
                TargetScorer.ClearActiveTurnState();
            }

            Main.Log($"[CandidatePlanGenerator] Generated {candidates.Count} candidates: " +
                string.Join(", ", GetCandidateTags(candidates)));
            return candidates;
        }

        // ═══════════════════════════════════════════════════════════
        // Helper Methods
        // ═══════════════════════════════════════════════════════════

        /// <summary>독립된 TurnState 생성 (원본 플래그 복사)</summary>
        private static TurnState CreateFreshTurnState(TurnState original, Situation situation)
        {
            float currentAP = CombatAPI.GetCurrentAP(situation.Unit);
            float currentMP = CombatAPI.GetCurrentMP(situation.Unit);
            var freshState = new TurnState(situation.Unit, currentAP, currentMP);
            CopyTurnStateFlags(original, freshState);
            return freshState;
        }

        /// <summary>타겟 처치 가능 여부 간이 추정 (HP% 기반)</summary>
        private static bool EstimateCanKill(Situation situation, BaseUnitEntity target)
        {
            if (target == null) return false;
            try
            {
                float hpPct = CombatCache.GetHPPercent(target);
                return hpPct < 30f;
            }
            catch { return false; }
        }

        /// <summary>원본 TurnState에서 보존해야 할 플래그를 새 TurnState로 복사</summary>
        private static void CopyTurnStateFlags(TurnState source, TurnState dest)
        {
            dest.HasMovedThisTurn = source.HasMovedThisTurn;
            dest.HasAttackedThisTurn = source.HasAttackedThisTurn;
            dest.HasBuffedThisTurn = source.HasBuffedThisTurn;
            dest.HasReloadedThisTurn = source.HasReloadedThisTurn;
            dest.HasHealedThisTurn = source.HasHealedThisTurn;
            dest.HasPerformedFirstAction = source.HasPerformedFirstAction;
            dest.MoveCount = source.MoveCount;
            dest.WeaponSwitchCount = source.WeaponSwitchCount;
            dest.LastAttackCategory = source.LastAttackCategory;
        }

        /// <summary>후보 리스트에서 태그 목록 추출 (로깅용)</summary>
        private static string[] GetCandidateTags(List<CandidatePlan> candidates)
        {
            var tags = new string[candidates.Count];
            for (int i = 0; i < candidates.Count; i++)
                tags[i] = candidates[i].ArchetypeTag ?? "?";
            return tags;
        }
    }
}
