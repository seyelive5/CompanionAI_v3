namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// ★ v3.110.12: 이동 계획이 null을 반환한 이유 분류.
    /// MovementPlanner의 각 null 반환 지점에서 설정하여 "왜 이동 안 했나"를 단일 로그로 파악.
    /// 이전: null 경로가 3단계 누적(PlanMoveOrGapCloser → PlanGapCloser/PlanMoveToEnemy → 실패) —
    ///       역추적 어려움. Hittable=0 Best + staying put 같은 상황에서 원인 규명 복잡.
    /// </summary>
    public enum MoveDecisionReason
    {
        /// <summary>아직 결정 미완료 (초기값)</summary>
        None,

        /// <summary>이동 계획 성공 — null 아님</summary>
        Planned,

        /// <summary>이동 필요 없음 — 이미 공격 가능 위치 (HasHittableEnemies=true + 후퇴 불필요)</summary>
        NoMoveNeeded_Hittable,

        /// <summary>적 없음 (HasLivingEnemies=false)</summary>
        NoLivingEnemies,

        /// <summary>NearestEnemy 없음 (전투 초기 상태 등)</summary>
        NoNearestEnemy,

        /// <summary>이미 최적 위치 (moveDistance &lt; 1f)</summary>
        AlreadyAtOptimal,

        /// <summary>적이 사거리 내 + 현재 위치에서 실제 공격 가능 → staying put 정당</summary>
        StayingPut_Hittable,

        /// <summary>안전한 공격 위치 탐색 실패 (FindRangedAttackPositionSync returns null)
        /// AND 사거리 밖 → 접근 시도도 실패 (FindBestApproachPosition도 null)</summary>
        NoSafePositionFound,

        /// <summary>접근하려니 MinSafeDistance 이내 진입 → 취소</summary>
        ApproachCancelledBySafety,

        /// <summary>모든 공격 능력 필터링 (쿨다운 등) + 위험 상황 아님 → 이동 의미 없음</summary>
        AllAbilitiesFiltered,

        /// <summary>이동 불가 (CanMove=false, MP 0 등)</summary>
        CannotMove,

        /// <summary>GapCloser 계획 실패 (CanUseAbilityOn 모두 실패)</summary>
        GapCloserFailed,
    }

    /// <summary>
    /// ★ v3.110.12: 마지막 이동 결정 이유 추적 — 진단용.
    /// MovementPlanner 호출자가 "왜 이동 안 했나"를 이 값으로 확인 가능.
    /// 전역 static이므로 동시 호출 없는 환경(턴제 AI)에서만 유효.
    /// </summary>
    public static class MoveDecisionTracker
    {
        public static MoveDecisionReason LastReason { get; private set; } = MoveDecisionReason.None;
        public static string LastReasonDetail { get; private set; } = "";

        public static void Set(MoveDecisionReason reason, string detail = null)
        {
            LastReason = reason;
            LastReasonDetail = detail ?? "";
        }

        public static void Reset()
        {
            LastReason = MoveDecisionReason.None;
            LastReasonDetail = "";
        }
    }
}
