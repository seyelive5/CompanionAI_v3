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
    }

    /// <summary>
    /// ★ Phase 3: LLM-as-Judge 후보 플랜 생성기.
    /// 동일 전략이 아닌 서로 다른 타겟을 기반으로 후보 플랜을 생성한다.
    ///
    /// Candidate 1: BestTarget (기본 — 기존 AI 결과와 동일)
    /// Candidate 2: 2nd-best target (TargetScorer 기준 차점 적)
    /// Candidate 3: AoE 클러스터 중심 적 (ClusterDetector로 2+ 적 밀집 발견 시)
    ///
    /// 설계 원칙:
    /// - 타겟이 다르면 이동, 능력 선택, AP 분배가 자연스럽게 달라짐
    /// - 각 후보는 독립된 TurnState를 사용하여 상호 간섭 없음
    /// - 원본 situation.BestTarget을 임시 교체 후 반드시 복원
    /// - 실패한 플랜(null)은 자동 제외
    /// </summary>
    public static class CandidatePlanGenerator
    {
        /// <summary>
        /// ★ Phase 3: 후보 플랜 2~3개 생성 (타겟 변경 기반).
        /// </summary>
        /// <param name="situation">현재 전투 상황 스냅샷</param>
        /// <param name="turnState">원본 턴 상태 (읽기 전용 — 복제 후 사용)</param>
        /// <param name="planner">TurnPlanner 인스턴스</param>
        /// <param name="role">AI 역할</param>
        /// <param name="maxCandidates">최대 후보 수 (기본 3)</param>
        /// <returns>유효한 후보 플랜 리스트 (비어있을 수 있음)</returns>
        public static List<CandidatePlan> Generate(
            Situation situation, TurnState turnState,
            TurnPlanner planner, AIRole role,
            int maxCandidates = 3)
        {
            var candidates = new List<CandidatePlan>(maxCandidates);

            if (situation?.Unit == null || situation.Enemies == null || situation.Enemies.Count == 0)
            {
                Main.Log("[CandidatePlanGenerator] No unit or enemies — returning empty");
                return candidates;
            }

            var originalBestTarget = situation.BestTarget;
            var originalCanKill = situation.CanKillBestTarget;

            Main.Log($"[CandidatePlanGenerator] Generating target-varied candidates for {situation.Unit.CharacterName}" +
                $" (BestTarget={originalBestTarget?.CharacterName ?? "none"}, Hittable={situation.HittableEnemies?.Count ?? 0})");

            try
            {
                // ── Candidate 1: Default (BestTarget) ──
                // 기존 AI가 선택하는 그대로 — baseline 플랜
                var defaultCandidate = BuildCandidate(
                    situation, turnState, planner, role,
                    originalBestTarget, "Default", 0);
                if (defaultCandidate != null)
                    candidates.Add(defaultCandidate);

                if (candidates.Count >= maxCandidates)
                    return candidates;

                // ── Candidate 2: Second-best target ──
                var secondBest = FindSecondBestTarget(situation, role, originalBestTarget);
                if (secondBest != null)
                {
                    var secondCandidate = BuildCandidate(
                        situation, turnState, planner, role,
                        secondBest, "AltTarget", 1);
                    if (secondCandidate != null)
                        candidates.Add(secondCandidate);
                }

                if (candidates.Count >= maxCandidates)
                    return candidates;

                // ── Candidate 3: AoE Cluster target ──
                var clusterTarget = FindClusterTarget(situation, originalBestTarget, secondBest);
                if (clusterTarget != null)
                {
                    var clusterCandidate = BuildCandidate(
                        situation, turnState, planner, role,
                        clusterTarget, "AoECluster", 2);
                    if (clusterCandidate != null)
                        candidates.Add(clusterCandidate);
                }
            }
            finally
            {
                // ★ 반드시 원본 BestTarget 복원
                situation.BestTarget = originalBestTarget;
                situation.CanKillBestTarget = originalCanKill;
            }

            Main.Log($"[CandidatePlanGenerator] Generated {candidates.Count} valid candidate plans");
            return candidates;
        }

        /// <summary>
        /// 특정 타겟으로 BestTarget을 임시 교체하고 독립된 TurnState로 플랜 생성.
        /// 생성 후 BestTarget은 BuildCandidate 내에서 복원하지 않음 (caller가 finally에서 일괄 복원).
        /// </summary>
        private static CandidatePlan BuildCandidate(
            Situation situation, TurnState originalState,
            TurnPlanner planner, AIRole role,
            BaseUnitEntity focusTarget, string label, int index)
        {
            if (focusTarget == null)
                return null;

            // BestTarget 임시 교체
            situation.BestTarget = focusTarget;

            // CanKillBestTarget 재계산: HP 기반 간단 추정
            situation.CanKillBestTarget = EstimateCanKill(situation, focusTarget);

            // 독립된 TurnState 생성 — 게임 API에서 현재 AP/MP 직접 조회
            float currentAP = CombatAPI.GetCurrentAP(situation.Unit);
            float currentMP = CombatAPI.GetCurrentMP(situation.Unit);
            var freshState = new TurnState(situation.Unit, currentAP, currentMP);

            // 원본 turnState에서 보존해야 할 상태 복사
            CopyTurnStateFlags(originalState, freshState);

            // FocusTargetId 설정 — BasePlan.EvaluateOrReuseStrategy()가 이 타겟을 기준으로 전략 평가
            freshState.SetContext(StrategicContextKeys.FocusTargetId, focusTarget.UniqueId);

            // TurnPlan 생성
            TurnPlan plan;
            try
            {
                plan = planner.CreatePlan(situation, freshState);
            }
            catch (System.Exception ex)
            {
                Main.Log($"[CandidatePlanGenerator] {label} #{index} ({focusTarget.CharacterName}) failed: {ex.Message}");
                return null;
            }

            if (plan == null)
            {
                Main.LogDebug($"[CandidatePlanGenerator] {label} #{index} ({focusTarget.CharacterName}) produced null plan — skipping");
                return null;
            }

            // 유틸리티 점수: TargetScorer 기반 적 점수 + 플랜 액션 카운트 보너스
            float targetScore = TargetScorer.ScoreEnemy(focusTarget, situation, role);
            float utilityScore = targetScore;

            // 전략에서 킬 가능 보너스 추가
            var strategy = freshState.GetContext<TurnStrategy>(StrategicContextKeys.TurnStrategyKey, null);
            if (strategy != null)
            {
                if (strategy.ExpectedKills > 0)
                    utilityScore += strategy.ExpectedKills * 40f;
                utilityScore += strategy.ExpectedTotalDamage * 0.1f;
            }

            var candidate = new CandidatePlan
            {
                Plan = plan,
                Strategy = strategy,
                UtilityScore = utilityScore,
                Summary = PlanSummarizer.Summarize(plan, strategy, situation),
                FocusTarget = focusTarget
            };

            Main.LogDebug($"[CandidatePlanGenerator] {label} #{index}: Target={focusTarget.CharacterName} — " +
                $"actions={plan.AllActions.Count}, utility={utilityScore:F0}, priority={plan.Priority}");

            return candidate;
        }

        /// <summary>
        /// HittableEnemies에서 BestTarget 다음으로 높은 점수의 적을 찾음.
        /// BestTarget과 다른 적이어야 의미 있는 대안 플랜이 됨.
        /// </summary>
        private static BaseUnitEntity FindSecondBestTarget(Situation situation, AIRole role, BaseUnitEntity bestTarget)
        {
            // ★ HittableEnemies가 아닌 전체 Enemies에서 탐색 — 이동 후 공격 가능한 적도 포함
            if (situation.Enemies == null || situation.Enemies.Count < 2)
                return null;

            BaseUnitEntity secondBest = null;
            float secondBestScore = float.MinValue;
            string bestId = bestTarget?.UniqueId;

            for (int i = 0; i < situation.Enemies.Count; i++)
            {
                var enemy = situation.Enemies[i];
                if (enemy == null) continue;

                try { if (enemy.LifeState?.IsDead == true) continue; }
                catch { continue; }

                // BestTarget 건너뜀
                if (bestId != null && enemy.UniqueId == bestId)
                    continue;

                float score = TargetScorer.ScoreEnemy(enemy, situation, role);
                if (score > secondBestScore)
                {
                    secondBestScore = score;
                    secondBest = enemy;
                }
            }

            if (secondBest != null)
            {
                Main.LogDebug($"[CandidatePlanGenerator] Second-best target: {secondBest.CharacterName} (score={secondBestScore:F1})");
            }
            else
            {
                Main.LogDebug("[CandidatePlanGenerator] No second-best target found (all same as BestTarget or < 2 hittable)");
            }

            return secondBest;
        }

        /// <summary>
        /// ClusterDetector로 2+ 적 밀집 클러스터를 찾고, 클러스터 중심에 가장 가까운 적을 반환.
        /// BestTarget 및 secondBest와 다른 적이어야 의미 있는 대안.
        /// 중복이면 null 반환 (후보 생략).
        /// </summary>
        private static BaseUnitEntity FindClusterTarget(
            Situation situation,
            BaseUnitEntity bestTarget,
            BaseUnitEntity secondBest)
        {
            // AoE 능력이 없으면 클러스터 타겟이 무의미
            if (!situation.HasAoEAttacks)
                return null;

            if (situation.Enemies == null || situation.Enemies.Count < 3)
                return null;

            var clusters = ClusterDetector.FindClusters(situation.Enemies);
            if (clusters == null || clusters.Count == 0)
                return null;

            // 최대 QualityScore 클러스터 (FindClusters는 이미 정렬됨)
            var bestCluster = clusters[0];
            if (bestCluster.Count < 2)
                return null;

            // 클러스터 중심에 가장 가까운 적 찾기
            BaseUnitEntity closestToCenter = null;
            float closestDist = float.MaxValue;

            for (int i = 0; i < bestCluster.Enemies.Count; i++)
            {
                var enemy = bestCluster.Enemies[i];
                if (enemy == null) continue;

                try { if (enemy.LifeState?.IsDead == true) continue; }
                catch { continue; }

                float dist = Vector3.Distance(enemy.Position, bestCluster.Center);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestToCenter = enemy;
                }
            }

            if (closestToCenter == null)
                return null;

            // 이미 선택된 타겟과 중복이면 의미 없음 — 생략
            string closestId = closestToCenter.UniqueId;
            if (bestTarget != null && closestId == bestTarget.UniqueId)
            {
                Main.LogDebug("[CandidatePlanGenerator] Cluster target same as BestTarget — skipping");
                return null;
            }
            if (secondBest != null && closestId == secondBest.UniqueId)
            {
                Main.LogDebug("[CandidatePlanGenerator] Cluster target same as second-best — skipping");
                return null;
            }

            Main.LogDebug($"[CandidatePlanGenerator] Cluster target: {closestToCenter.CharacterName} " +
                $"(cluster: {bestCluster.Count} enemies, quality={bestCluster.QualityScore:F0})");

            return closestToCenter;
        }

        /// <summary>
        /// 타겟 처치 가능 여부 간이 추정.
        /// 정확한 시뮬레이션은 전략 평가기가 수행하므로 여기서는 HP% 기반 대략 추정.
        /// </summary>
        private static bool EstimateCanKill(Situation situation, BaseUnitEntity target)
        {
            if (target == null) return false;
            try
            {
                float hpPct = CombatCache.GetHPPercent(target);
                // HP 30% 미만이면 킬 가능성 높음 (대략적 추정)
                return hpPct < 30f;
            }
            catch { return false; }
        }

        /// <summary>
        /// 원본 TurnState에서 보존해야 할 플래그를 새 TurnState로 복사.
        /// 이미 실행된 행동 정보를 정확히 전달해야 Phase가 올바르게 진행.
        /// </summary>
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
    }
}
