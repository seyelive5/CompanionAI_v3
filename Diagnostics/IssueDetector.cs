using System.Collections.Generic;
using System.Linq;
using CompanionAI_v3.Diagnostics.Models;

namespace CompanionAI_v3.Diagnostics
{
    /// <summary>
    /// ★ v3.20.0: 턴 결정 로그에서 이상 행동을 자동 감지
    /// CombatReportCollector.OnTurnEnd()에서 호출
    /// </summary>
    public static class IssueDetector
    {
        public static List<DetectedIssue> Detect(TurnDecisionLog log)
        {
            var issues = new List<DetectedIssue>();
            if (log == null) return issues;

            // === AP 낭비 ===
            // EndAP >= 1.5이면 사용 가능한 AP를 남기고 턴 종료
            // ★ v3.20.2: 두 가지 면제 조건 (오탐 방지)
            // 1) Hittable=0 AND MP=0 → 전술적 막다른 상황 (이동도 공격도 불가) — 이동하거나 공격 선택지 자체가 없음
            // 2) Hittable=0 AND Plan=EndTurn → 능력 고갈 후 EndTurn 선택 (지휘관/Support 클래스 설계 한계)
            //    예: 카시아가 AP 소모 버프 능력을 모두 사용한 후 2AP 잔여 → 진짜 할 것이 없는 상태
            if (log.EndAP >= 1.5f)
            {
                bool noMovePossible = log.HittableEnemyCount == 0 && log.EndMP <= 0f;
                bool noTargetsAndEndedTurn = log.HittableEnemyCount == 0 && log.PlanPriority == "EndTurn";
                if (!noMovePossible && !noTargetsAndEndedTurn)
                {
                    issues.Add(new DetectedIssue
                    {
                        Round = log.Round,
                        Unit = log.UnitName,
                        Type = "AP_WASTE",
                        Detail = $"{log.EndAP:F1} AP unused (started with {log.StartAP:F1})"
                    });
                }
            }

            // === 실행 실패 ===
            // ★ v3.20.1: "Continue to next action"은 GroupFailurePolicy.ContinueGroup 정상 동작 — 오탐 제외
            // Move 실패 후 공격을 계속하는 패턴 (KillSeq, 이동 후 공격 콤보)이 여기에 해당
            if (log.Results != null)
            {
                var failures = log.Results
                    .Where(r => !r.Success && r.Detail != "Continue to next action")
                    .ToList();
                if (failures.Count > 0)
                {
                    var failDetails = string.Join(", ", failures.Select(r => r.Action));
                    issues.Add(new DetectedIssue
                    {
                        Round = log.Round,
                        Unit = log.UnitName,
                        Type = "EXECUTION_FAILURE",
                        Detail = $"{failures.Count} failed: {failDetails}"
                    });
                }
            }

            // === 저HP인데 힐 미계획 ===
            if (log.HP < 40f && log.PlannedActions != null)
            {
                bool healPlanned = log.PlannedActions.Any(a => a.Type == "Heal");
                bool healReceived = log.Results?.Any(r =>
                    r.Action != null && r.Action.Contains("Heal") && r.Success) ?? false;

                if (!healPlanned && !healReceived)
                {
                    issues.Add(new DetectedIssue
                    {
                        Round = log.Round,
                        Unit = log.UnitName,
                        Type = "LOW_HP_NO_HEAL",
                        Detail = $"HP {log.HP:F0}% - no heal planned or received"
                    });
                }
            }

            // === AP 있는데 Hittable 타겟 없음 (아무 유용한 행동도 못한 경우만) ===
            // Support/Overseer가 버프·힐을 성공했으면 정상 동작 — 오탐 방지
            if (log.HittableEnemyCount == 0 && log.StartAP >= 2f)
            {
                bool hasAttack = log.PlannedActions?.Any(a => a.Type == "Attack") ?? false;
                bool hasSuccessfulAction = log.Results?.Any(r => r.Success) ?? false;
                if (!hasAttack && !hasSuccessfulAction)
                {
                    issues.Add(new DetectedIssue
                    {
                        Round = log.Round,
                        Unit = log.UnitName,
                        Type = "NO_HITTABLE_TARGETS",
                        Detail = $"Had {log.StartAP:F1} AP but 0 hittable enemies and no useful actions taken"
                    });
                }
            }

            // === 이동 후 공격 없이 AP 잔여 ===
            // ★ v3.20.2: H=0이면 이동만 하는 것은 정상 (적 접근, 다음 턴 대비) — 오탐 제외
            // H=0 상황에서 MoveOnly 전략 선택 → 다음 턴 포지셔닝이 목적이므로 공격이 없는 것은 의도된 행동
            if (log.PlannedActions != null && log.EndAP >= 1f && log.HittableEnemyCount > 0)
            {
                int moveIdx = log.PlannedActions.FindIndex(a => a.Type == "Move");
                if (moveIdx >= 0)
                {
                    bool hasAttackAfterMove = log.PlannedActions
                        .Skip(moveIdx + 1)
                        .Any(a => a.Type == "Attack");

                    if (!hasAttackAfterMove)
                    {
                        issues.Add(new DetectedIssue
                        {
                            Round = log.Round,
                            Unit = log.UnitName,
                            Type = "MOVE_NO_ATTACK",
                            Detail = $"Moved but no attack after ({log.EndAP:F1} AP remaining)"
                        });
                    }
                }
            }

            // === 팀 신뢰도 극단값 ===
            if (log.TeamConfidence < 0.2f)
            {
                issues.Add(new DetectedIssue
                {
                    Round = log.Round,
                    Unit = log.UnitName,
                    Type = "CRITICAL_CONFIDENCE",
                    Detail = $"Team confidence {log.TeamConfidence:F2} (critical)"
                });
            }

            return issues;
        }
    }
}
