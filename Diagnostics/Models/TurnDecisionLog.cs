using System.Collections.Generic;
using Newtonsoft.Json;

namespace CompanionAI_v3.Diagnostics.Models
{
    /// <summary>
    /// ★ v3.20.0: 턴 1회의 결정 및 실행 기록
    /// CombatReportCollector가 턴 진행 중 점진적으로 채움
    /// </summary>
    public class TurnDecisionLog
    {
        // === 기본 정보 ===
        public string UnitName { get; set; }
        public string UnitId { get; set; }
        public string Role { get; set; }       // "Tank", "DPS", "Support", "Overseer"
        public int Round { get; set; }

        // === 턴 시작 상태 ===
        public float HP { get; set; }          // 퍼센트 (0~100)
        public float StartAP { get; set; }
        public float StartMP { get; set; }
        public float TeamConfidence { get; set; }
        public string Tactic { get; set; }     // "Attack", "Defend", "Retreat"

        // === 계획 요약 (가장 최근 플랜, Replan 시 덮어씀) ===
        public string PlanReason { get; set; }     // "Support -> Buff -> Move -> Attack"
        public string PlanPriority { get; set; }   // "DirectAttack", "EndTurn" 등
        public List<PlannedActionLog> PlannedActions { get; set; } = new List<PlannedActionLog>();

        // === 결정 과정 로그 (APBudget, TacticalEval 등 핵심 선택지) ===
        // ★ v3.20.1: 게임 로그에서 노이즈 없이 AI 결정 근거만 추출
        public List<string> Phases { get; set; } = new List<string>();

        // === 실행 결과 ===
        public List<ExecutionResultLog> Results { get; set; } = new List<ExecutionResultLog>();

        // === 턴 종료 상태 ===
        public float EndAP { get; set; }
        public float EndMP { get; set; }

        // === 자동 감지된 이슈 ===
        public List<string> Issues { get; set; } = new List<string>();

        // === 내부 상태 (이슈 감지용, JSON 직렬화 제외) ===
        [JsonIgnore]
        public int HittableEnemyCount { get; set; }
    }

    public class PlannedActionLog
    {
        public string Type { get; set; }       // "Attack", "Buff", "Heal", "Move", "EndTurn" 등
        public string Ability { get; set; }    // 능력 이름 (이동/EndTurn은 null)
        public string Target { get; set; }     // 대상 이름, "Self", "point" 등
        public float APCost { get; set; }
    }

    public class ExecutionResultLog
    {
        public string Action { get; set; }     // "HeavyStrike → Cultist"
        public bool Success { get; set; }
        public string Detail { get; set; }     // "OK" 또는 실패 이유
    }
}
