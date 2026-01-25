using System;

namespace CompanionAI_v3.Data
{
    /// <summary>
    /// 스킬 사용 타이밍 분류
    /// </summary>
    public enum AbilityTiming
    {
        /// <summary>일반 스킬 - 언제든 사용 가능</summary>
        Normal,

        /// <summary>선제적 자기 버프 - 턴 시작 시 우선 사용</summary>
        PreCombatBuff,

        /// <summary>공격 직전 버프 - 공격 전에 사용하면 효과적</summary>
        PreAttackBuff,

        /// <summary>첫 행동 후 사용 - 추가 행동 활성화 (Run and Gun 등)</summary>
        PostFirstAction,

        /// <summary>턴 종료 스킬 - 턴 마지막에만 사용</summary>
        TurnEnding,

        /// <summary>마무리 스킬 - 적 HP 낮을 때만 효과적</summary>
        Finisher,

        /// <summary>자해 스킬 - HP를 소모하므로 HP 체크 필요</summary>
        SelfDamage,

        /// <summary>위험한 AoE - 아군 위치 확인 필수</summary>
        DangerousAoE,

        /// <summary>디버프 스킬 - 공격 전에 사용하면 효과적</summary>
        Debuff,

        /// <summary>Heroic Act - Momentum 175+ 필요</summary>
        HeroicAct,

        /// <summary>★ v3.1.12: Desperate Measure - Momentum 25 필요</summary>
        DesperateMeasure,

        /// <summary>도발 스킬 - 근접 적 2명 이상일 때</summary>
        Taunt,

        /// <summary>재장전</summary>
        Reload,

        /// <summary>힐링/치료</summary>
        Healing,

        /// <summary>돌격/갭클로저</summary>
        GapCloser,

        /// <summary>긴급 상황 스킬 - HP 낮을 때</summary>
        Emergency,

        /// <summary>분노 스킬 - 적 처치 후 활성화</summary>
        RighteousFury,

        /// <summary>스택 버프</summary>
        StackingBuff,

        /// <summary>DoT 강화</summary>
        DOTIntensify,

        /// <summary>연쇄 효과</summary>
        ChainEffect,

        /// <summary>★ v3.0.21: 위치 타겟 버프 - 전방/보조/후방 구역 등 지역 배치 스킬</summary>
        PositionalBuff,

        /// <summary>★ v3.0.23: 구역 강화 스킬 - Combat Tactics 구역을 강화하는 전략가 스킬</summary>
        Stratagem,

        /// <summary>★ v3.0.28: 마킹 스킬 - 적을 표시만 하고 데미지 없음 (Hunt Down the Prey 등)</summary>
        Marker,

        /// <summary>★ v3.7.27: 사역마 전용 능력 - FamiliarAbilities에서만 처리 (MultiTarget 등)</summary>
        FamiliarOnly,
    }

    /// <summary>
    /// 스킬 카테고리 (빠른 분류용)
    /// </summary>
    [Flags]
    public enum AbilityFlags
    {
        None = 0,
        SingleUse = 1 << 0,      // 전투당 1회
        RequiresLowHP = 1 << 1,  // 적 HP 낮을 때
        Dangerous = 1 << 2,      // 아군 피해 가능
        SelfTargetOnly = 1 << 3, // 자기 타겟만
        EnemyTarget = 1 << 4,    // 적 타겟
        AllyTarget = 1 << 5,     // 아군 타겟
        PointTarget = 1 << 6,    // 위치 타겟
        IsAoE = 1 << 7,          // AoE 능력
        IsPsychic = 1 << 8,      // 사이킥 능력
        IsWeaponAttack = 1 << 9, // 무기 공격
        IsConsumable = 1 << 10,  // 소모품
        OncePerTurn = 1 << 11,   // 한 턴에 한 번만
        // ★ v3.0.98: MPRecovery 플래그 deprecated - CombatAPI.GetAbilityMPRecovery()가 Blueprint에서 자동 감지
        // MPRecovery = 1 << 12,
        RequiresBurstAttack = 1 << 12, // ★ v3.5.73: Burst 공격 전용 버프 (속사 등)
        IsDefensiveStance = 1 << 13,   // ★ v3.7.65: 방어 태세 (StalwartDefense, DefensiveStance, Bulwark, Overwatch 등)
        IsReloadAbility = 1 << 14,     // ★ v3.7.65: 재장전 능력
        IsTauntAbility = 1 << 15,      // ★ v3.7.65: 도발 능력
        IsFinisherAbility = 1 << 16,   // ★ v3.7.65: 마무리 능력 (처형 계열)
    }

    /// <summary>
    /// ★ v3.5.73: 공격 카테고리 (게임 API 기반 자동 분류)
    /// 게임 네이티브 속성 (IsBurstAttack, IsScatter, IsCharge 등) 기반
    /// </summary>
    public enum AttackCategory
    {
        /// <summary>분류 불가 / 일반</summary>
        Normal,

        /// <summary>단일 타겟 (단발 사격)</summary>
        SingleTarget,

        /// <summary>점사/연사 (Burst)</summary>
        Burst,

        /// <summary>산탄 (Scatter)</summary>
        Scatter,

        /// <summary>범위 공격 (Circle, Cone, Ray, Pattern)</summary>
        AoE,

        /// <summary>돌격/이동기 (Charge)</summary>
        GapCloser,
    }
}
