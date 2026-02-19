using Kingmaker.UnitLogic.Abilities;

namespace CompanionAI_v3.Core
{
    /// <summary>
    /// ★ v3.11.0: TurnStrategyPlanner의 평가 결과
    /// Phase들이 참조하는 전략 가이드 — Phase 동작을 강제하지 않고 힌트 제공
    /// </summary>
    public class TurnStrategy
    {
        /// <summary>선택된 시퀀스 유형</summary>
        public SequenceType Sequence { get; set; }

        /// <summary>Phase 4: 버프를 사용해야 하는가?</summary>
        public bool ShouldBuffBeforeAttack { get; set; }

        /// <summary>Phase 4: 사용할 버프 (null이면 Phase 4 자체 선택)</summary>
        public AbilityData RecommendedBuff { get; set; }

        /// <summary>Phase 5: 공격 루프 AP 하한 — R&G AP 예약</summary>
        public float ReservedAPForPostAction { get; set; }

        /// <summary>Phase 6: R&G 사용이 전략의 일부인가?</summary>
        public bool PlansPostAction { get; set; }

        /// <summary>평가된 총 기대 데미지 (로깅/디버그)</summary>
        public float ExpectedTotalDamage { get; set; }

        /// <summary>선택 이유 (로깅)</summary>
        public string Reason { get; set; }

        // ── ★ v3.11.0: 확장 필드 — AoE, 킬 시퀀스, 디버프 가이드 ──

        /// <summary>Phase 4.4: AoE를 우선해야 하는가?</summary>
        public bool ShouldPrioritizeAoE { get; set; }

        /// <summary>Phase 4.4: 전략이 추천하는 AoE 능력 (null이면 Phase 4.4 자체 선택)</summary>
        public AbilityData RecommendedAoE { get; set; }

        /// <summary>Phase 3: 킬 시퀀스를 우선해야 하는가?</summary>
        public bool PrioritizesKillSequence { get; set; }

        /// <summary>Phase 4.5: 디버프 후 공격이 유리한가?</summary>
        public bool ShouldDebuffBeforeAttack { get; set; }

        /// <summary>시뮬레이션 예상 킬 수 (로깅/디버그)</summary>
        public int ExpectedKills { get; set; }
    }

    /// <summary>★ v3.11.0: 전략적 행동 시퀀스 유형</summary>
    public enum SequenceType
    {
        /// <summary>Attack×N — 순수 공격</summary>
        Standard,

        /// <summary>Buff → Attack×(N-1) — 버프 후 공격</summary>
        BuffedAttack,

        /// <summary>Attack×K → R&G → Attack×M — R&G 체인</summary>
        RnGChain,

        /// <summary>Buff → Attack×K → R&G → Attack×M — 버프+R&G 체인</summary>
        BuffedRnGChain,

        // ── ★ v3.11.0: 새 시퀀스 유형 ──

        /// <summary>AoE×N — AoE 집중 (클러스터 타겟)</summary>
        AoEFocus,

        /// <summary>Buff → AoE×N — 버프 후 AoE 집중</summary>
        BuffedAoE,

        /// <summary>AoE → R&G → Attack — AoE 후 R&G 체인</summary>
        AoERnGChain,

        /// <summary>KillSimulator 최적 시퀀스 — 확정 킬 우선</summary>
        KillSequence,

        /// <summary>Debuff → Attack×(N-1) — 디버프 후 공격</summary>
        DebuffedAttack,

        /// <summary>Buff → AoE → R&G → Attack — 올인 콤보</summary>
        BuffedRnGAoE
    }
}
