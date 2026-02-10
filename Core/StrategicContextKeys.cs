namespace CompanionAI_v3.Core
{
    /// <summary>
    /// ★ v3.8.86: TurnState.StrategicContext 키 상수
    /// 재계획(Replan) 시 이전 계획의 전략적 의도를 새 계획에 전달
    /// </summary>
    public static class StrategicContextKeys
    {
        // ── DPS: 콤보 체인 ──

        /// <summary>bool - 콤보 전제 능력이 이미 적용됨 (재계획 시 후속만 계획)</summary>
        public const string ComboPrereqApplied = "ComboPrereqApplied";

        /// <summary>string - 콤보 후속 능력 GUID (재계획 시 해당 능력 우선)</summary>
        public const string ComboFollowUpGuid = "ComboFollowUpGuid";

        /// <summary>string - 콤보 전제가 적용된 타겟 UniqueId</summary>
        public const string ComboTargetId = "ComboTargetId";

        // ── DPS: 공격 후 후퇴 전략 ──

        /// <summary>bool - 공격 후 후퇴 전략이었음 (재계획 시 후퇴 우선)</summary>
        public const string DeferredRetreat = "DeferRetreat";

        // ── 공통: 킬 시퀀스 ──

        /// <summary>string - 킬 시퀀스 타겟 UniqueId (재계획 시 동일 타겟 우선)</summary>
        public const string KillSequenceTargetId = "KillSeqTargetId";
    }
}
