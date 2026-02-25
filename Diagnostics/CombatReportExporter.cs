using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using CompanionAI_v3.Diagnostics.Models;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Diagnostics
{
    /// <summary>
    /// ★ v3.20.0: 전투 리포트 JSON 내보내기
    ///
    /// 출력 파일:
    ///   {ModPath}/combat_reports/current_combat.json  ← 진행 중 실시간 덮어쓰기 (매 턴 갱신)
    ///   {ModPath}/combat_reports/{id}_summary.json    ← 전투 종료 시 최종본 (2~3KB)
    ///   {ModPath}/combat_reports/{id}_full.json       ← 전투 종료 시 전체 상세
    /// </summary>
    public static class CombatReportExporter
    {
        private static readonly JsonSerializerSettings _settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Include
        };

        /// <summary>
        /// ★ v3.20.0: 매 턴 종료 시 호출 — current_combat.json을 최신 상태로 덮어씀
        /// 전투 중 실시간 조회 가능. OnTurnEnd에서 호출.
        /// </summary>
        public static void ExportLive(CombatReport report)
        {
            if (report == null || report.Turns.Count == 0) return;

            try
            {
                string dir = Path.Combine(Main.ModEntry.Path, "combat_reports");
                Directory.CreateDirectory(dir);

                var summary = BuildSummary(report);
                string json = JsonConvert.SerializeObject(summary, _settings);
                File.WriteAllText(Path.Combine(dir, "current_combat.json"), json);
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[CombatReport] Live export failed: {ex.Message}");
            }
        }

        public static void Export(CombatReport report)
        {
            if (report == null || report.Turns.Count == 0) return;

            try
            {
                string dir = Path.Combine(Main.ModEntry.Path, "combat_reports");
                Directory.CreateDirectory(dir);

                string id = report.CombatId;

                // 1. Full detail (전체 상세)
                string fullJson = JsonConvert.SerializeObject(report, _settings);
                File.WriteAllText(Path.Combine(dir, $"{id}_full.json"), fullJson);

                // 2. Summary (요약, Claude Code가 먼저 읽는 파일)
                var summary = BuildSummary(report);
                string summaryJson = JsonConvert.SerializeObject(summary, _settings);
                File.WriteAllText(Path.Combine(dir, $"{id}_summary.json"), summaryJson);

                // 3. 오래된 리포트 정리
                int keepCount = ModSettings.Instance?.MaxCombatReports ?? 10;
                CleanOldReports(dir, keepCount);

                Main.Log($"[CombatReport] Exported: {id} " +
                    $"({report.Turns.Count} turns, {report.FlaggedIssues.Count} issues flagged)");
            }
            catch (Exception ex)
            {
                Main.LogError($"[CombatReport] Export failed: {ex.Message}");
            }
        }

        private static object BuildSummary(CombatReport report)
        {
            // 턴 목록을 1줄 요약으로 압축
            var turnSummaries = report.Turns.Select(t =>
            {
                // actions: "Buff → Self, HeavyStrike → Cultist(OK), HeavyStrike → Cultist(FAIL:...)"
                string actions = t.Results != null && t.Results.Count > 0
                    ? string.Join(", ", t.Results.Select(r =>
                        r.Success ? r.Action : $"{r.Action}(FAIL:{r.Detail})"))
                    : "(no actions)";

                return new
                {
                    round = t.Round,
                    unit = t.UnitName,
                    role = t.Role,
                    hp = $"{t.HP:F0}%",
                    ap = $"{t.StartAP - t.EndAP:F1}/{t.StartAP:F1} used",
                    tactic = t.Tactic,
                    confidence = $"{t.TeamConfidence:F2}",
                    plan = t.PlanReason,
                    priority = t.PlanPriority,
                    phases = t.Phases,
                    actions,
                    issues = t.Issues
                };
            }).ToList();

            // 통계
            int totalActions = report.Turns.Sum(t => t.Results?.Count ?? 0);
            int successActions = report.Turns.Sum(t => t.Results?.Count(r => r.Success) ?? 0);
            int turnsWithIssues = report.Turns.Count(t => t.Issues.Count > 0);
            string mostCommonIssue = report.FlaggedIssues
                .GroupBy(i => i.Type)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();

            return new
            {
                combat_id = report.CombatId,
                timestamp = report.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                total_rounds = report.TotalRounds,
                result = report.Result,
                party = report.Party,
                turns = turnSummaries,
                flagged_issues = report.FlaggedIssues,
                stats = new
                {
                    total_actions = totalActions,
                    successful_actions = successActions,
                    turns_with_issues = turnsWithIssues,
                    most_common_issue = mostCommonIssue ?? "none"
                }
            };
        }

        private static void CleanOldReports(string dir, int keepCount)
        {
            try
            {
                // summary와 full을 쌍으로 취급 — summary 기준으로 오래된 것 삭제
                var summaries = Directory.GetFiles(dir, "*_summary.json")
                    .OrderByDescending(f => f)  // 파일명이 날짜 기반이므로 내림차순 = 최신순
                    .ToList();

                // keepCount 초과분 삭제
                foreach (var oldSummary in summaries.Skip(keepCount))
                {
                    try
                    {
                        File.Delete(oldSummary);
                        string fullPath = oldSummary.Replace("_summary.json", "_full.json");
                        if (File.Exists(fullPath))
                            File.Delete(fullPath);
                    }
                    catch { /* 개별 파일 삭제 실패 무시 */ }
                }
            }
            catch { /* 정리 실패 시 무시 */ }
        }
    }
}
