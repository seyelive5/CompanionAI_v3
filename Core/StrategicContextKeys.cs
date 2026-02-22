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

        // ── DPS: 보너스 무기 전환 ──

        /// <summary>★ v3.9.92: bool - Phase 1.56이 보너스 공격을 위해 무기 전환함
        /// Phase 1.55가 즉시 되돌리지 않도록 방지 — 이동 후 재분석에서 공격 가능</summary>
        public const string BonusWeaponSwitch = "BonusWeaponSwitch";

        // ── 전략 평가 ──

        /// <summary>★ v3.10.0: TurnStrategy 전략 가이드 (replan 시 보존)</summary>
        public const string TurnStrategyKey = "TurnStrategy";

        // ── 공통: 킬 시퀀스 ──

        /// <summary>string - 킬 시퀀스 타겟 UniqueId (재계획 시 동일 타겟 우선)</summary>
        public const string KillSequenceTargetId = "KillSeqTargetId";

        // ── ★ v3.19.2: 전략 집중 타겟 / 턴 의도 ──

        /// <summary>★ v3.19.2: string - 전략 평가 시 기준이 된 타겟 UniqueId
        /// Replan 시 동일 타겟이 유효하면 전략 재사용, 무효하면 재평가</summary>
        public const string FocusTargetId = "FocusTargetId";

        /// <summary>★ v3.19.2: string - 현재 턴의 전술적 의도 ("Kill", "AoE", "Support", "Retreat")
        /// Replan 시 의도 연속성 보존 — 의도가 유효하면 전략 힌트로 활용</summary>
        public const string TacticalObjective = "TacticalObjective";
    }
}
