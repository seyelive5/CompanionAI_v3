namespace CompanionAI_v3.Core
{
    /// <summary>
    /// 실행 에러 유형 - 문자열 매칭 대신 Enum 사용
    /// ★ v3.5.36: 에러 분류 강화
    /// </summary>
    public enum ExecutionErrorType
    {
        /// <summary>알 수 없는 에러</summary>
        Unknown = 0,

        // ═══════════════════════════════════════════════════════════
        // 회복 가능 (해당 액션만 스킵, 다음 액션 계속)
        // ═══════════════════════════════════════════════════════════

        /// <summary>전략가 구역 겹침 - 다른 위치에 배치하면 됨</summary>
        StrategistZonesCantOverlap = 100,

        /// <summary>이미 버프 있음 - 스킵 가능</summary>
        AlreadyHasBuff = 101,

        /// <summary>버프가 이미 활성 상태 - 중복 적용 불가</summary>
        BuffAlreadyActive = 102,

        /// <summary>타겟에 더 높은 레벨의 버프 있음 - 스킵</summary>
        TargetHasHigherBuff = 103,

        /// <summary>리소스 부족 (MP 등) - 다른 행동 시도</summary>
        NotEnoughResources = 104,

        /// <summary>시전자 이동 - 위치 기반 능력 스킵</summary>
        CasterMoved = 105,

        /// <summary>★ v3.7.86: 타겟에 시야가 없음 - 다른 타겟 시도</summary>
        HasNoLosToTarget = 106,

        /// <summary>★ v3.7.86: 타겟 도달 불가 - 다른 타겟 시도</summary>
        TargetUnreachable = 107,

        // ═══════════════════════════════════════════════════════════
        // 재계획 필요
        // ═══════════════════════════════════════════════════════════

        /// <summary>타겟 사망 - 다른 타겟 선택 필요</summary>
        TargetDead = 200,

        /// <summary>능력 사용 불가 - 다른 능력 선택 필요</summary>
        AbilityUnavailable = 201,

        // ═══════════════════════════════════════════════════════════
        // 턴 종료
        // ═══════════════════════════════════════════════════════════

        /// <summary>AP 없음 - 턴 종료</summary>
        NoAPLeft = 300,

        /// <summary>모든 행동 소진 - 턴 종료</summary>
        AllActionsExhausted = 301
    }

    /// <summary>
    /// ExecutionErrorType 확장 메서드
    /// </summary>
    public static class ExecutionErrorTypeExtensions
    {
        /// <summary>
        /// 문자열에서 에러 유형 파싱
        /// </summary>
        public static ExecutionErrorType ParseFromReason(string reason)
        {
            if (string.IsNullOrEmpty(reason))
                return ExecutionErrorType.Unknown;

            // 회복 가능 에러
            if (reason.Contains("StrategistZonesCantOverlap"))
                return ExecutionErrorType.StrategistZonesCantOverlap;
            if (reason.Contains("AlreadyHasBuff"))
                return ExecutionErrorType.AlreadyHasBuff;
            if (reason.Contains("BuffAlreadyActive"))
                return ExecutionErrorType.BuffAlreadyActive;
            if (reason.Contains("TargetHasHigherBuff"))
                return ExecutionErrorType.TargetHasHigherBuff;
            if (reason.Contains("NotEnoughResources"))
                return ExecutionErrorType.NotEnoughResources;
            if (reason.Contains("CasterMoved"))
                return ExecutionErrorType.CasterMoved;
            // ★ v3.7.86: LOS/Range 에러 추가
            if (reason.Contains("HasNoLosToTarget") || reason.Contains("No LOS"))
                return ExecutionErrorType.HasNoLosToTarget;
            if (reason.Contains("unreachable") || reason.Contains("Unreachable"))
                return ExecutionErrorType.TargetUnreachable;

            // 재계획 필요
            if (reason.Contains("TargetDead") || reason.Contains("target is dead"))
                return ExecutionErrorType.TargetDead;
            if (reason.Contains("AbilityUnavailable") || reason.Contains("not available"))
                return ExecutionErrorType.AbilityUnavailable;

            // 턴 종료
            if (reason.Contains("NoAP") || reason.Contains("no AP"))
                return ExecutionErrorType.NoAPLeft;
            if (reason.Contains("AllActionsExhausted"))
                return ExecutionErrorType.AllActionsExhausted;

            return ExecutionErrorType.Unknown;
        }

        /// <summary>
        /// 회복 가능한 에러인지 확인
        /// </summary>
        public static bool IsRecoverable(this ExecutionErrorType errorType)
        {
            // 100~199: 회복 가능 에러
            return (int)errorType >= 100 && (int)errorType < 200;
        }

        /// <summary>
        /// 재계획이 필요한 에러인지 확인
        /// </summary>
        public static bool RequiresReplan(this ExecutionErrorType errorType)
        {
            // 200~299: 재계획 필요 에러
            return (int)errorType >= 200 && (int)errorType < 300;
        }

        /// <summary>
        /// 턴 종료가 필요한 에러인지 확인
        /// </summary>
        public static bool RequiresEndTurn(this ExecutionErrorType errorType)
        {
            // 300+: 턴 종료 에러
            return (int)errorType >= 300;
        }
    }
}
