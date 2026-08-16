namespace CompanionAI_v3.Core
{
    /// <summary>
    /// 게임 상수 - 매직 넘버 중앙화
    /// ★ v3.5.36: 하드코딩된 값들을 명시적 상수로 정리
    /// </summary>
    public static class GameConstants
    {
        #region Timeouts & Limits

        /// <summary>
        /// 명령 완료 대기 타임아웃 (프레임)
        /// ★ v3.6.21: 120 → 1800 (2초 → 30초)
        /// 긴 애니메이션 스킬(사이킥, 다중타격 AOE 등) 허용
        /// </summary>
        public const int COMMAND_WAIT_TIMEOUT_FRAMES = 1800;

        /// <summary>
        /// 연속 실패 허용 횟수
        /// 이 횟수 초과 시 턴 종료
        /// </summary>
        public const int MAX_CONSECUTIVE_FAILURES = 3;

        /// <summary>
        /// ★ v3.8.92: 턴당 최대 폴백 재계획 횟수
        /// 실행 실패 후 새 계획으로 복구 시도하는 최대 횟수
        /// </summary>
        public const int MAX_FALLBACK_REPLANS = 2;

        /// <summary>
        /// 턴당 이동 설정(SetupMovement) 실패가 이 횟수에 도달하면 이번 턴 나머지 동안 이동 계획 차단
        /// (TurnState.MovementBlockedThisTurn) — 같은 목적지 재선택 replan 루프를 끊고 제자리 플랜으로 전환.
        /// </summary>
        public const int MOVE_SETUP_FAILURES_BLOCK_MOVEMENT = 2;

        /// <summary>
        /// 턴당 이동 설정 실패 하드 상한 — 이동 차단 후에도 실패가 계속되면(비정상) 턴 종료.
        /// </summary>
        public const int MAX_MOVE_SETUP_FAILURES = 3;

        #endregion

        #region Thresholds

        /// <summary>
        /// HP 급감 임계값 (%)
        /// 이 값 이상 HP가 감소하면 재계획 트리거
        /// </summary>
        public const float HP_CRITICAL_DROP_THRESHOLD = 20f;

        /// <summary>
        /// AP 회복 감지 임계값
        /// 이 값 이상 AP가 증가하면 새 기회로 판단
        /// </summary>
        public const float AP_RECOVERY_EPSILON = 0.5f;

        /// <summary>
        /// 추가 Hittable 타겟 임계값
        /// 이 수 이상 새로운 타겟이 Hittable이 되면 재계획
        /// </summary>
        public const int MIN_ADDITIONAL_HITTABLE_TARGETS = 2;

        #endregion
    }
}
