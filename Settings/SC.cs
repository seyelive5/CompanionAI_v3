namespace CompanionAI_v3.Settings
{
    /// <summary>
    /// ★ v3.20.0: 내부 AI 튜닝 상수 — 외부 JSON/UI 노출 없음
    /// 구 AIConfig의 ThresholdConfig + ScoringConfig + 내부 AoE 가중치 통합
    ///
    /// 설계 원칙:
    ///   - 사용자 설정 (영구 보존 필요)  → AIConfig.cs (AoEConfig, WeaponRotationConfig)
    ///   - 개발자 튜닝 상수 (업데이트 즉시 반영) → 이 파일
    /// </summary>
    internal static class SC
    {
        // ─── 전투 임계값 ─────────────────────────────────────────────────────
        public const float EmergencyHealHP        = 30f;   // 긴급 힐 HP% 기준
        public const float FinisherTargetHP       = 30f;   // 마무리 타겟 HP% 기준
        public const float HealPriorityHP         = 50f;   // 힐 우선순위 HP% 기준
        public const float SkipBuffBelowHP        = 40f;   // 이 HP 이하면 버프 스킵
        public const float SafeDistance           = 7f;    // 원거리 캐릭터 안전 거리 (미터)
        public const float DangerDistance         = 5f;    // 위험 적 거리 (미터)
        public const float OneHitKillRatio        = 0.95f; // 1타킬 데미지/HP 비율
        public const float TwoHitKillRatio        = 0.5f;  // 2타킬 데미지/HP 비율
        public const float DesperatePhaseHP       = 35f;   // 절박 상황: 팀 평균 HP%
        public const float DesperateSelfHP        = 25f;   // 절박 상황: 자신 HP%
        public const int   CleanupEnemyCount      = 2;     // 정리 단계: 남은 적 수 이하
        public const float SelfDamageMinHP        = 80f;   // 자해 스킬 사용 최소 HP%
        public const float ThreatProximity        = 5f;    // 위협 근접 거리 (미터)
        public const float HealPriorityLow        = 25f;   // 힐 최우선 HP% [구 HealPriorityThresholds[0]]
        public const float HealPriorityMid        = 50f;   // 힐 높음 HP%   [구 HealPriorityThresholds[1]]
        public const float HealPriorityHigh       = 75f;   // 힐 보통 HP%   [구 HealPriorityThresholds[2]]
        public const float LowThreatHP            = 30f;   // 위협도 감소 HP% (이하면 위협 낮음)
        public const float OpeningPhaseMinAP      = 3f;    // 개막 단계 최소 AP
        public const float PreAttackBuffMinHP     = 50f;   // PreAttackBuff 사용 가능 최소 HP%

        // ─── 위협 평가 가중치 ──────────────────────────────────────────────
        public const float LethalityWeight    = 0.3f;   // Lethality (HP 기반 위협도) 가중치
        public const float ProximityWeight    = 0.4f;   // Proximity (거리 기반 위협도) 가중치
        public const float HealerRoleBonus    = 0.15f;  // 힐러 역할 추가 위협도
        public const float CasterRoleBonus    = 0.1f;   // 캐스터 역할 추가 위협도
        public const float RangedWeaponBonus  = 0.05f;  // 원거리 무기 추가 위협도
        public const float ThreatMaxDistance  = 30f;    // 위협 평가 최대 거리 (정규화 기준)

        // ─── 버프 스코어링 배율 ──────────────────────────────────────────
        public const float OpeningPhaseBuffMult          = 1.3f;  // 초반 버프 배율
        public const float CleanupPhaseBuffMult          = 0.7f;  // 정리 단계 버프 배율
        public const float DesperateNonDefMult           = 0.5f;  // 위기 시 비방어 버프 배율
        public const float PreCombatOpeningBonus         = 30f;   // 선제 버프 초반 보너스
        public const float PreCombatCleanupPenalty       = 20f;   // 선제 버프 정리 페널티
        public const float PreAttackHittableBonus        = 25f;   // 공격 전 버프 + 적 타격 가능 보너스
        public const float PreAttackNoEnemyPenalty       = 10f;   // 공격 전 버프 + 적 부재 페널티
        public const float EmergencyDesperateBonus       = 40f;   // 긴급 버프 위기 상황 보너스
        public const float EmergencyNonDesperatePenalty  = 20f;   // 긴급 버프 비위기 페널티
        public const float TauntNearEnemiesBonus         = 25f;   // 도발 + 근접 다수 적 보너스
        public const float TauntFewEnemiesPenalty        = 15f;   // 도발 + 적 부족 페널티

        // ─── 시너지 보너스 ────────────────────────────────────────────────
        public const float BuffAttackSynergy      = 25f;  // 공격 버프 + 공격 시너지
        public const float MoveAttackSynergy      = 10f;  // 이동 + 공격 시너지
        public const float MultiAttackPerAttack   = 10f;  // 연속 공격 시너지 (공격당)
        public const float DefenseRetreatSynergy  = 15f;  // 방어 버프 + 이동 시너지
        public const float KillConfirmSynergy     = 30f;  // 킬 확정 시너지
        public const float AlmostKillSynergy      = 15f;  // 거의 킬 시너지

        // ─── 공격 스코어링 ────────────────────────────────────────────────
        public const float ClearMPDangerBase   = 60f;  // ClearMP + 위험 상황 기본 감점
        public const float AoEBonusPerEnemy    = 15f;  // AoE 추가 적당 보너스
        public const float InertiaBonus        = 20f;  // 이전 턴 동일 타겟 보너스
        public const float HardCCExploitBonus  = 15f;  // Hard CC 상태 적 공격 보너스
        public const float DOTFollowUpBonus    = 8f;   // DOT 상태 적 공격 보너스

        // ─── AoE 내부 가중치 ─────────────────────────────────────────────
        public const float AoEEnemyHitScore         = 10000f; // 적 1명 타격 기본 점수
        public const float AoEPlayerAllyPenaltyMult = 2.0f;   // 플레이어 아군 피격 페널티 배수
        public const float AoENpcAllyPenaltyMult    = 1.0f;   // NPC 아군 피격 페널티 배수
        public const float AoECasterSelfPenaltyMult = 2.0f;   // 캐스터 자신 피격 페널티 배수
        public const float AoEClusterNpcAllyPenalty = 20f;    // 클러스터 NPC 아군 페널티 점수

        // ─── 무기 로테이션 내부 상수 ──────────────────────────────────────
        public const int AoEMinEnemiesForAlternateAoE = 2;  // 대체 세트 AoE 최소 적 수
    }
}
