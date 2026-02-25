using System;
using System.Collections.Generic;

namespace CompanionAI_v3.Diagnostics.Models
{
    /// <summary>
    /// ★ v3.20.0: 전투 1회 전체 리포트
    /// CombatReportExporter가 전투 종료 시 JSON으로 직렬화
    /// </summary>
    public class CombatReport
    {
        public string CombatId { get; set; }           // "20260225_143000"
        public DateTime Timestamp { get; set; }
        public int TotalRounds { get; set; }
        public string Result { get; set; } = "Ongoing"; // "Victory", "Defeat", "Ongoing"

        public List<string> Party { get; set; } = new List<string>();
        public List<string> InitialEnemies { get; set; } = new List<string>();

        public List<TurnDecisionLog> Turns { get; set; } = new List<TurnDecisionLog>();

        public List<DetectedIssue> FlaggedIssues { get; set; } = new List<DetectedIssue>();
    }

    /// <summary>자동 감지된 이상 행동 항목</summary>
    public class DetectedIssue
    {
        public int Round { get; set; }
        public string Unit { get; set; }
        public string Type { get; set; }               // "AP_WASTE", "LOW_HP_NO_HEAL" 등
        public string Detail { get; set; }
    }
}
