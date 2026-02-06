using System;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Levelup.Obsolete.Blueprints.Spells;

namespace CompanionAI_v3.Data
{
    // ═══════════════════════════════════════════════════════════════════════════
    // ★ v3.7.73: 효과 분류 마스크 (SpellDescriptor 기반)
    // 게임의 SpellDescriptor를 우리 분류 목적에 맞게 그룹화
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ★ v3.7.73: 효과 분류 마스크
    /// SpellDescriptor 플래그를 그룹화하여 빠른 분류에 사용
    /// </summary>
    public static class EffectMasks
    {
        // DoT 효과 (지속 피해)
        public const SpellDescriptor DOT = SpellDescriptor.Fire | SpellDescriptor.Acid |
            SpellDescriptor.Poison | SpellDescriptor.Bleed;

        // CC 효과 - 강력 (행동 완전 차단)
        public const SpellDescriptor HardCC = SpellDescriptor.Stun | SpellDescriptor.Paralysis |
            SpellDescriptor.Sleep | SpellDescriptor.Petrified;

        // CC 효과 - 약함 (행동 제한)
        public const SpellDescriptor SoftCC = SpellDescriptor.Fear | SpellDescriptor.Confusion |
            SpellDescriptor.Blindness | SpellDescriptor.Daze | SpellDescriptor.Charm |
            SpellDescriptor.MovementImpairing;

        // 모든 CC 효과
        public const SpellDescriptor AllCC = HardCC | SoftCC;

        // 디버프 효과 (상태이상)
        public const SpellDescriptor Debuffs = SpellDescriptor.Sickened | SpellDescriptor.Shaken |
            SpellDescriptor.Fatigue | SpellDescriptor.Exhausted | SpellDescriptor.Nauseated |
            SpellDescriptor.Frightened | SpellDescriptor.Staggered | SpellDescriptor.Curse |
            SpellDescriptor.StatDebuff;

        // 위험 효과 (사이킥 현상 등)
        public const SpellDescriptor Dangerous = SpellDescriptor.PsychicPhenomena | SpellDescriptor.Death;

        // 원소 피해
        public const SpellDescriptor Elemental = SpellDescriptor.Fire | SpellDescriptor.Cold |
            SpellDescriptor.Electricity | SpellDescriptor.Acid | SpellDescriptor.Sonic;
    }

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

        /// <summary>★ v3.7.73: CC 스킬 - 군중 제어 (Stun, Paralysis 등)</summary>
        CrowdControl,

        /// <summary>★ v3.7.73: 수류탄 - AbilityTag.ThrowingGrenade 기반</summary>
        Grenade,
    }

    /// <summary>
    /// 스킬 카테고리 (빠른 분류용)
    /// </summary>
    [Flags]
    public enum AbilityFlags : long
    {
        None = 0,
        SingleUse = 1L << 0,      // 전투당 1회
        RequiresLowHP = 1L << 1,  // 적 HP 낮을 때
        Dangerous = 1L << 2,      // 아군 피해 가능
        SelfTargetOnly = 1L << 3, // 자기 타겟만
        EnemyTarget = 1L << 4,    // 적 타겟
        AllyTarget = 1L << 5,     // 아군 타겟
        PointTarget = 1L << 6,    // 위치 타겟
        IsAoE = 1L << 7,          // AoE 능력
        IsPsychic = 1L << 8,      // 사이킥 능력
        IsWeaponAttack = 1L << 9, // 무기 공격
        IsConsumable = 1L << 10,  // 소모품
        OncePerTurn = 1L << 11,   // 한 턴에 한 번만
        RequiresBurstAttack = 1L << 12, // ★ v3.5.73: Burst 공격 전용 버프 (속사 등)
        IsDefensiveStance = 1L << 13,   // ★ v3.7.65: 방어 태세
        IsReloadAbility = 1L << 14,     // ★ v3.7.65: 재장전 능력
        IsTauntAbility = 1L << 15,      // ★ v3.7.65: 도발 능력
        IsFinisherAbility = 1L << 16,   // ★ v3.7.65: 마무리 능력

        // ★ v3.7.73: 새로운 플래그 (게임 API 기반)
        IsNavigator = 1L << 17,         // 항법사 능력
        IsGrenade = 1L << 18,           // 수류탄
        IsTrap = 1L << 19,              // 함정
        IsDrug = 1L << 20,              // 전투 약물
        HasDOT = 1L << 21,              // DoT 효과 있음
        HasCC = 1L << 22,               // CC 효과 있음
        HasDebuff = 1L << 23,           // 디버프 효과 있음
        IsMelee = 1L << 24,             // 근접 공격
        IsRanged = 1L << 25,            // 원거리 공격
        IsBurst = 1L << 26,             // 연사 공격
        IsScatter = 1L << 27,           // 산탄 공격
        IsSingleShot = 1L << 28,        // 단발 공격
        HasMinRange = 1L << 29,         // 최소 사거리 있음
        IsFreeAction = 1L << 30,        // 무료 행동
        HasPsychicPhenomena = 1L << 31, // 사이킥 현상 위험
        IsRetreatCapable = 1L << 32,    // ★ v3.8.23: 후퇴용 이동 스킬 (SoldierDash 등)
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

        /// <summary>★ v3.7.73: 근접 공격</summary>
        Melee,
    }

    /// <summary>
    /// ★ v3.7.73: 능력의 모든 분류 속성을 캐싱하는 구조체
    /// 게임 API에서 추출한 모든 속성을 한 곳에 저장
    /// </summary>
    public class AbilityClassificationData
    {
        // ═══════════════════════════════════════════════════════════════
        // 기본 타입 (게임 API 직접)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>AbilityTag - Heal, ThrowingGrenade, UsingCombatDrug, Trap 등</summary>
        public AbilityTag Tag { get; set; }

        /// <summary>능력 소스 - Weapon, PsychicPower, NavigatorPower, SkillCheck 등</summary>
        public WarhammerAbilityParamsSource ParamsSource { get; set; }

        /// <summary>SpellDescriptor - 효과 플래그 (Fire, Poison, Stun 등)</summary>
        public SpellDescriptor SpellDescriptor { get; set; }

        // ═══════════════════════════════════════════════════════════════
        // 공격 특성 (게임 API 직접)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>공격 타입 enum - SingleShot, Scatter, Pattern, Melee</summary>
        public AttackAbilityType? AttackType { get; set; }

        public bool IsMelee { get; set; }
        public bool IsRanged { get; set; }
        public bool IsScatter { get; set; }
        public bool IsBurst { get; set; }
        public bool IsSingleShot { get; set; }
        public bool IsAoE { get; set; }
        public bool IsCharge { get; set; }
        public bool IsMoveUnit { get; set; }
        public int BurstCount { get; set; }
        public int AoERadius { get; set; }
        public string PatternType { get; set; }

        // ═══════════════════════════════════════════════════════════════
        // 타겟팅 (게임 API 직접)
        // ═══════════════════════════════════════════════════════════════

        public AbilityRange Range { get; set; }
        public int RangeCells { get; set; }
        public int MinRangeCells { get; set; }
        public int CustomRange { get; set; }
        public bool CanTargetEnemies { get; set; }
        public bool CanTargetFriends { get; set; }
        public bool CanTargetSelf { get; set; }
        public bool CanTargetPoint { get; set; }
        public bool CanTargetDead { get; set; }
        public TargetType AoETargets { get; set; }
        public bool NeedLoS { get; set; }

        // ═══════════════════════════════════════════════════════════════
        // 효과 (게임 API 직접)
        // ═══════════════════════════════════════════════════════════════

        public AbilityEffectOnUnit EffectOnAlly { get; set; }
        public AbilityEffectOnUnit EffectOnEnemy { get; set; }
        public bool NotOffensive { get; set; }
        public bool IsWeaponAbility { get; set; }
        public bool IsPsykerAbility { get; set; }

        // ═══════════════════════════════════════════════════════════════
        // 특수 (게임 API 직접)
        // ═══════════════════════════════════════════════════════════════

        public bool IsHeroicAct { get; set; }
        public bool IsDesperateMeasure { get; set; }
        public bool IsStratagem { get; set; }
        public bool IsMomentum { get; set; }

        // ═══════════════════════════════════════════════════════════════
        // 비용 (게임 API 직접)
        // ═══════════════════════════════════════════════════════════════

        public int APCost { get; set; }
        public bool IsFreeAction { get; set; }
        public int CooldownRounds { get; set; }

        // ═══════════════════════════════════════════════════════════════
        // ★ v3.7.89: AOO (기회공격) 관련 - 게임 API 직접
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 위협 범위 내 사용 시 AOO 유형
        /// WillCauseAOO = 0: AOO 유발
        /// CanUseWithoutAOO = 1: AOO 없이 사용 가능
        /// CannotUse = 2: 위협 범위 내 사용 불가
        /// </summary>
        public int UsingInThreateningArea { get; set; }

        // ═══════════════════════════════════════════════════════════════
        // 분석된 분류 (우리 로직으로 계산)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>DoT 효과 있음</summary>
        public bool HasDOT => (SpellDescriptor & EffectMasks.DOT) != 0;

        /// <summary>CC 효과 있음</summary>
        public bool HasCC => (SpellDescriptor & EffectMasks.AllCC) != 0;

        /// <summary>강력 CC 효과 있음 (Stun, Paralysis, Sleep)</summary>
        public bool HasHardCC => (SpellDescriptor & EffectMasks.HardCC) != 0;

        /// <summary>디버프 효과 있음</summary>
        public bool HasDebuff => (SpellDescriptor & EffectMasks.Debuffs) != 0;

        /// <summary>위험 효과 있음 (사이킥 현상 등)</summary>
        public bool HasDangerousEffect => (SpellDescriptor & EffectMasks.Dangerous) != 0;

        /// <summary>사이킥 능력 (AbilityParamsSource 플래그 또는 Blueprint 속성)</summary>
        public bool IsPsychic => IsPsykerAbility ||
            (ParamsSource & WarhammerAbilityParamsSource.PsychicPower) != 0;

        /// <summary>항법사 능력</summary>
        public bool IsNavigator => (ParamsSource & WarhammerAbilityParamsSource.NavigatorPower) != 0;

        /// <summary>수류탄</summary>
        public bool IsGrenade => Tag == AbilityTag.ThrowingGrenade;

        /// <summary>전투 약물</summary>
        public bool IsDrug => Tag == AbilityTag.UsingCombatDrug;

        /// <summary>함정</summary>
        public bool IsTrap => Tag == AbilityTag.Trap;

        // ★ v3.7.89: AOO 관련 계산 속성

        /// <summary>위협 범위 내 사용 시 AOO 유발</summary>
        public bool WillCauseAOO => UsingInThreateningArea == 0;  // BlueprintAbility.UsingInThreateningAreaType.WillCauseAOO

        /// <summary>위협 범위 내에서 AOO 없이 사용 가능</summary>
        public bool CanUseWithoutAOO => UsingInThreateningArea == 1;  // BlueprintAbility.UsingInThreateningAreaType.CanUseWithoutAOO

        /// <summary>위협 범위 내 사용 불가</summary>
        public bool CannotUseInThreatArea => UsingInThreateningArea == 2;  // BlueprintAbility.UsingInThreateningAreaType.CannotUse

        /// <summary>위협 범위 내에서 안전하게 사용 가능 (AOO 없음 + 사용 가능)</summary>
        public bool IsSafeInThreatArea => UsingInThreateningArea == 1;

        /// <summary>공격 카테고리 계산</summary>
        public AttackCategory Category
        {
            get
            {
                if (IsCharge || IsMoveUnit) return AttackCategory.GapCloser;
                if (IsMelee && !IsAoE) return AttackCategory.Melee;
                if (IsAoE || AoERadius > 0 || !string.IsNullOrEmpty(PatternType)) return AttackCategory.AoE;
                if (IsScatter) return AttackCategory.Scatter;
                if (IsBurst) return AttackCategory.Burst;
                if (IsSingleShot) return AttackCategory.SingleTarget;
                return AttackCategory.Normal;
            }
        }

        /// <summary>AbilityFlags 플래그 계산</summary>
        public AbilityFlags ComputedFlags
        {
            get
            {
                var flags = AbilityFlags.None;

                // 타겟팅
                if (CanTargetSelf && !CanTargetEnemies && !CanTargetFriends) flags |= AbilityFlags.SelfTargetOnly;
                if (CanTargetEnemies) flags |= AbilityFlags.EnemyTarget;
                if (CanTargetFriends) flags |= AbilityFlags.AllyTarget;
                if (CanTargetPoint) flags |= AbilityFlags.PointTarget;

                // 공격 타입
                if (IsAoE || AoERadius > 0) flags |= AbilityFlags.IsAoE;
                if (IsPsychic) flags |= AbilityFlags.IsPsychic;
                if (IsWeaponAbility) flags |= AbilityFlags.IsWeaponAttack;
                if (IsNavigator) flags |= AbilityFlags.IsNavigator;
                if (IsGrenade) flags |= AbilityFlags.IsGrenade;
                if (IsTrap) flags |= AbilityFlags.IsTrap;
                if (IsDrug) flags |= AbilityFlags.IsDrug;

                // 효과
                if (HasDOT) flags |= AbilityFlags.HasDOT;
                if (HasCC) flags |= AbilityFlags.HasCC;
                if (HasDebuff) flags |= AbilityFlags.HasDebuff;
                if (HasDangerousEffect) flags |= AbilityFlags.HasPsychicPhenomena;
                if (EffectOnAlly == AbilityEffectOnUnit.Harmful) flags |= AbilityFlags.Dangerous;

                // 공격 방식
                if (IsMelee) flags |= AbilityFlags.IsMelee;
                if (IsRanged) flags |= AbilityFlags.IsRanged;
                if (IsBurst) flags |= AbilityFlags.IsBurst;
                if (IsScatter) flags |= AbilityFlags.IsScatter;
                if (IsSingleShot) flags |= AbilityFlags.IsSingleShot;

                // 기타
                if (MinRangeCells > 0) flags |= AbilityFlags.HasMinRange;
                if (IsFreeAction) flags |= AbilityFlags.IsFreeAction;

                return flags;
            }
        }
    }
}
