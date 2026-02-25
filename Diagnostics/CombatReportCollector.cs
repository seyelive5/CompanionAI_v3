using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.Diagnostics.Models;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Planning;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Diagnostics
{
    /// <summary>
    /// ★ v3.20.0: 전투 리포트 데이터 수집기 (Singleton)
    /// TurnOrchestrator의 각 단계에서 호출하여 데이터를 점진적으로 수집.
    /// IsEnabled=false 시 모든 메서드는 즉시 반환 (오버헤드 0).
    /// </summary>
    public class CombatReportCollector
    {
        #region Singleton

        private static CombatReportCollector _instance;
        public static CombatReportCollector Instance => _instance ??= new CombatReportCollector();

        #endregion

        #region State

        private CombatReport _currentReport;
        private TurnDecisionLog _currentTurn;

        public bool IsEnabled => ModSettings.Instance?.EnableCombatReport ?? false;

        #endregion

        // ─── 전투 시작/종료 ──────────────────────────────────────────────────

        public void OnCombatStart()
        {
            if (!IsEnabled) return;

            string id = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _currentReport = new CombatReport
            {
                CombatId = id,
                Timestamp = DateTime.Now
            };
            _currentTurn = null;

            // 파티 구성 수집 (전투 중 유닛 목록에서)
            try
            {
                var allUnits = Game.Instance?.TurnController?.AllUnits?
                    .OfType<BaseUnitEntity>()
                    .Where(u => u != null && u.IsPlayerFaction && !u.IsDead)
                    .ToList();

                if (allUnits != null)
                {
                    foreach (var u in allUnits)
                        _currentReport.Party.Add(u.CharacterName);
                }
            }
            catch { /* 파티 수집 실패 시 무시 */ }

            Main.Log($"[CombatReport] Combat started → ID={id}");
        }

        public void OnCombatEnd(string result)
        {
            if (!IsEnabled) return;
            if (_currentReport == null) return;

            // 마지막 턴 미완료 시 강제 마감
            FinalizeCurrentTurn(0f, 0f);

            _currentReport.Result = result;
            _currentReport.TotalRounds = GetCurrentRound();

            CombatReportExporter.Export(_currentReport);

            _currentReport = null;
            _currentTurn = null;
        }

        // ─── 턴 시작/종료 ────────────────────────────────────────────────────

        public void OnTurnStart(BaseUnitEntity unit, Situation situation)
        {
            if (!IsEnabled) return;
            if (_currentReport == null) return;
            if (unit == null || situation == null) return;

            // ★ 같은 유닛의 연속 분석(리플랜)이면 기존 턴 유지 — 새 TurnDecisionLog 생성 안 함
            // 2-Phase 구조에서 리플랜마다 AnalyzePhase가 재호출되기 때문에 필수
            if (_currentTurn != null && _currentTurn.UnitId == unit.UniqueId)
            {
                // 최신 상황으로 HittableEnemyCount만 갱신 (더 정확한 이슈 감지를 위해)
                _currentTurn.HittableEnemyCount = situation.HittableEnemies?.Count ?? 0;
                return;
            }

            // 다른 유닛으로 전환 시 이전 턴 마감
            FinalizeCurrentTurn(0f, 0f);

            _currentTurn = new TurnDecisionLog
            {
                UnitName = unit.CharacterName,
                UnitId = unit.UniqueId,
                Role = "?",   // RecordPlan에서 Reasoning 파싱으로 정확히 설정
                Round = GetCurrentRound(),
                HP = situation.HPPercent,
                StartAP = situation.CurrentAP,
                StartMP = situation.CurrentMP,
                TeamConfidence = TeamBlackboard.Instance.TeamConfidence,
                Tactic = TeamBlackboard.Instance.CurrentTactic.ToString(),
                HittableEnemyCount = situation.HittableEnemies?.Count ?? 0
            };

            // ★ v3.20.1: 전황 스냅샷 — 가장 가까운 적, 최선 타겟, Hittable 수
            try
            {
                string nearest = situation.NearestEnemy?.CharacterName ?? "none";
                string bestTarget = situation.BestTarget?.CharacterName ?? "none";
                // ★ v3.20.2: NH=NormalHittableCount 추가 — TacticalEval이 사용하는 실제 값
                // H=전체(DangerousAoE 포함), NH=일반공격 hittable (TacticalEval 기준)
                // NH=0이면 AttackFromCurrent 비활성 → MoveToAttack이 낮은 점수로 선택되는 이유 설명
                _currentTurn.Phases.Add(
                    $"Situation: Nearest={nearest}@{situation.NearestEnemyDistance:F1}t" +
                    $" Best={bestTarget} CanKill={situation.CanKillBestTarget}" +
                    $" H={situation.HittableEnemies?.Count ?? 0} NH={situation.NormalHittableCount}" +
                    $" Tactic={TeamBlackboard.Instance.CurrentTactic}");
            }
            catch { /* Situation snapshot 실패 무시 */ }
        }

        public void OnTurnEnd(float endAP, float endMP)
        {
            if (!IsEnabled) return;
            FinalizeCurrentTurn(endAP, endMP);
        }

        // ─── 결정 과정 기록 ──────────────────────────────────────────────────

        /// <summary>
        /// ★ v3.20.1: APBudget, TacticalEval 등 핵심 결정 로그를 JSON에 기록.
        /// BasePlan/TurnPlanner의 Main.Log 호출 옆에서 함께 호출.
        /// </summary>
        public void LogPhase(string message)
        {
            if (!IsEnabled || _currentTurn == null) return;
            _currentTurn.Phases.Add(message);
        }

        // ─── 계획 기록 ───────────────────────────────────────────────────────

        /// <summary>
        /// TurnPlan 생성 시 호출. Replan 시 이전 계획을 덮어씀 (최신 의도 반영).
        /// </summary>
        public void RecordPlan(TurnPlan plan)
        {
            if (!IsEnabled) return;
            if (_currentTurn == null || plan == null) return;

            // ★ v3.20.1: Role은 Reasoning 접두어에서 파싱 — 수동/Auto 설정 모두 신뢰 가능
            // Reasoning 형식: "Overseer: Support -> Buff -> ..." → 콜론 앞이 역할명
            if (!string.IsNullOrEmpty(plan.Reasoning))
            {
                int colonIdx = plan.Reasoning.IndexOf(':');
                if (colonIdx > 0)
                    _currentTurn.Role = plan.Reasoning.Substring(0, colonIdx).Trim();
            }

            // 최신 플랜 요약 업데이트 (Replan 시 덮어씀 — 최종 의도 반영)
            _currentTurn.PlanReason = plan.Reasoning;
            _currentTurn.PlanPriority = plan.Priority.ToString();

            _currentTurn.PlannedActions.Clear();

            if (plan.AllActions == null) return;

            foreach (var action in plan.AllActions)
            {
                if (action == null) continue;

                string targetName = null;
                if (action.Target?.Entity is BaseUnitEntity targetUnit)
                    targetName = targetUnit.CharacterName;
                else if (action.MoveDestination.HasValue)
                    targetName = "position";

                _currentTurn.PlannedActions.Add(new PlannedActionLog
                {
                    Type = action.Type.ToString(),
                    Ability = action.Ability?.Name,
                    Target = targetName,
                    APCost = action.APCost
                });
            }
        }

        // ─── 실행 결과 기록 ──────────────────────────────────────────────────

        public void RecordExecution(PlannedAction action, ExecutionResult result, bool success)
        {
            if (!IsEnabled) return;
            if (_currentTurn == null || action == null || result == null) return;

            // EndTurn은 리포트에서 제외 — 항상 "성공"이 아닌 결과여도 정상 완료
            if (action.Type == ActionType.EndTurn) return;

            // 행동 문자열 구성: "HeavyStrike → Cultist"
            string abilityPart = action.Ability?.Name ?? action.Type.ToString();
            string targetPart = null;
            if (action.Target?.Entity is BaseUnitEntity targetUnit)
                targetPart = targetUnit.CharacterName;
            else if (action.MoveDestination.HasValue)
                // Reason에 "Melee position near 돌연변이" 같은 설명이 있으면 사용
                targetPart = !string.IsNullOrEmpty(action.Reason) ? action.Reason : "position";

            string actionStr = targetPart != null
                ? $"{abilityPart} → {targetPart}"
                : abilityPart;

            string detail = success ? "OK" : (result.Reason ?? "FAIL");

            _currentTurn.Results.Add(new ExecutionResultLog
            {
                Action = actionStr,
                Success = success,
                Detail = detail
            });
        }

        // ─── 내부 헬퍼 ───────────────────────────────────────────────────────

        private void FinalizeCurrentTurn(float endAP, float endMP)
        {
            if (_currentTurn == null) return;

            _currentTurn.EndAP = endAP;
            _currentTurn.EndMP = endMP;

            // 이슈 감지
            var issues = IssueDetector.Detect(_currentTurn);
            foreach (var issue in issues)
            {
                _currentTurn.Issues.Add($"{issue.Type}: {issue.Detail}");
                _currentReport?.FlaggedIssues.Add(issue);
            }

            _currentReport?.Turns.Add(_currentTurn);
            _currentTurn = null;

            // ★ 실시간 기록 — current_combat.json 덮어쓰기 (전투 중 조회 가능)
            CombatReportExporter.ExportLive(_currentReport);
        }

        private int GetCurrentRound()
        {
            try
            {
                return Game.Instance?.TurnController?.CombatRound ?? 0;
            }
            catch { return 0; }
        }
    }
}
