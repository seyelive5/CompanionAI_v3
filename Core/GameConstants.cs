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
        /// 약 2초 @ 60fps
        /// </summary>
        public const int COMMAND_WAIT_TIMEOUT_FRAMES = 120;

        /// <summary>
        /// 연속 실패 허용 횟수
        /// 이 횟수 초과 시 턴 종료
        /// </summary>
        public const int MAX_CONSECUTIVE_FAILURES = 3;

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
