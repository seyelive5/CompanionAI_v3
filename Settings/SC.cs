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

        // ─── Plan 내부 상수 ──────────────────────────────────────────────
        // ★ v3.22.0: BasePlan에서 이관 — 중앙 튜닝
        public const float HPCostThreshold        = 40f;  // 자해 스킬 HP 비용 임계값 (%)
        public const float DefaultMeleeAttackCost = 2f;   // 근접 공격 AP 비용 폴백
        public const float DefaultRangedAttackCost = 2f;  // 원거리 공격 AP 비용 폴백
        public const int   MaxAttacksPerPlan      = 10;   // 턴당 최대 공격 수 (실질적 무제한, AP로 자연 종료)
        public const int   MaxPositionalBuffs     = 3;    // 위치 버프 최대 수

        // ─── 마스티프 사역마 ─────────────────────────────────────────────
        // ★ v3.22.6: 마스티프 Apprehend/Protect 개선
        public const float MastiffApprehendMaxReachTiles = 15f;  // Apprehend 도달 가능 최대 거리 (타일)
        public const float MastiffProtectMaxHP           = 50f;  // Protect 발동 아군 최대 HP%

        // ─── 전투 규칙 개선 ─────────────────────────────────────────────
        // ★ v3.24.0: 극저 데미지 감지 (방어구 관통 불가)
        public const float LowDamageThreshold        = 5f;   // EstimateDamage 이 이하면 방어구 관통 불가 판정
        public const float LowDamagePenalty           = 30f;  // TargetScorer 극저 데미지 타겟 페널티
        public const float LowDamageAttackPenalty     = 40f;  // UtilityScorer 극저 데미지 공격 페널티

        // ★ v3.24.0: Overwatch 포지셔닝 반영
        public const float OverwatchMovePenalty       = 15f;  // TacticalOptionEvaluator 이동 Overwatch 페널티 (적 1명당)
        public const float OverwatchEstimatedRange    = 15f;  // Overwatch 추정 사거리 (타일)

        // ★ v3.24.0: 사거리 품질 포지셔닝
        public const float PositionRangeOptimalBonus  = 55f;  // rangeFit 1.0 → +25 (55-30), 0.0 → -30 (0-30)
        public const float PositionRangeBasePenalty   = 30f;  // 기본 감산 (사거리 밖 = -30)

        // ─── v3.26.0: Tier 2 전투 규칙 ──────────────────────────────────
        // Step 4: CC 저항
        public const float CCResistanceHighThreshold = 70f;  // CC 저항률 이 이상이면 CC 스킵

        // ─── v3.28.0: Tier 3 플랭킹 + 아키타입 ──────────────────────────
        public const float FlankingPositionBonus     = 8f;    // 원거리 포지셔닝: 적 1명 Back → +8, Side → +4
        public const float FlankingMeleeBonus        = 15f;   // 근접 포지셔닝: 타겟 Back → +15, Side → +7.5
        public const float TargetFlankingBonus       = 12f;   // TargetScorer: 타겟 Back → +12, Side → +6
        public const float ExposeWeaknessMinArmor    = 15f;   // ExposeWeakness: 이 이상 방어력만 대상
        public const float VersatilityDiversityBonus = 20f;   // Arch-Militant: 다른 공격 유형 선호 보너스

        // ─── v3.30.0: 수류탄 포지셔닝 안전 ────────────────────────────────
        public const float GrenadeOutOfRangePenalty  = 60f;   // 원거리 캐릭터: 사거리 밖 수류탄 감점 (전진 방지)

        // ─── v3.32.0: 플라스마 과열 인식 ────────────────────────────────
        public const int   PlasmaOverheatDangerRank     = 2;    // 이 Rank부터 감점 시작 (50% 폭발)
        public const float PlasmaOverheatPenaltyPerRank = 40f;  // Rank당 감점 (rank2=-40, rank3=-80, rank4=-120)

        // ─── 폴백 기본값 ─────────────────────────────────────────────────
        // ★ v3.22.0: 게임 API 조회 실패 시 사용되는 안전 폴백 값 중앙화
        public const float FallbackWeaponRange   = 15f;  // 무기 사거리 폴백 (타일) — 원거리 무기 보수적 추정
        public const float FallbackEstimateDamage = 15f;  // 데미지 예측 폴백 — GetDamagePrediction 실패 시

        // ─── Phase E: 게임 내장 API 전환 feature flag ───────────────────
        // ★ v3.112.0: Phase E.1 Pilot — AoESafetyChecker.EvaluateDirectionalAoE 에서
        //             game-native OrientedPatternData 경로 활성화.
        // true: GetAffectedNodes + pattern.Contains(node) — LOS/unwalkable/level-diff 정확 반영
        // false: IsUnitInDirectionalAoERange 2D 근사 (롤백용)
        // 현재 활성 callsite: AoESafetyChecker.EvaluateDirectionalAoE (15건 중 1건 pilot)
        public const bool UseNativePattern = true;
    }
}
