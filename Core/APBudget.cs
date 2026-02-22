namespace CompanionAI_v3.Core
{
    /// <summary>
    /// ★ v3.19.0: 통합 AP 예산 시스템
    /// 5개 독립 AP 예약 시스템(PostMoveAttack, TurnEnding, BuffReservation,
    /// DefensiveStance, AttackBuff, MasterMinAttack)을 단일 구조체로 통합
    ///
    /// 사용법:
    ///   var budget = APBudget.Create(situation, strategy, masterMinAttackAP);
    ///   float spendable = budget.SpendableForActions;
    ///   if (budget.CanAfford(cost)) { ... }
    ///
    /// 참고: ref float remainingAP 패턴은 유지 (BasePlan 90+ helper 시그니처 보존)
    /// APBudget은 예약 계산과 affordability 판단만 중앙화
    /// </summary>
    public struct APBudget
    {
        /// <summary>턴 시작 시 총 AP</summary>
        public float TotalAP;

        /// <summary>이동 후 공격 예약 AP</summary>
        public float PostMoveReserved;

        /// <summary>턴 종료 능력(곡예술 등) 예약 AP</summary>
        public float TurnEndingReserved;

        /// <summary>전략 R&G 예약 AP (strategyAPFloor)</summary>
        public float StrategyPostActionReserved;

        /// <summary>마스터 최소 공격 AP (Overseer only)</summary>
        public float MasterMinAttackReserved;

        /// <summary>공격 행동에 사용 가능한 AP (TurnEnding + Strategy 예약 제외)</summary>
        public float SpendableForActions => TotalAP - TurnEndingReserved - StrategyPostActionReserved;

        /// <summary>아군 버프에 사용 가능한 AP (MasterMinAttack + TurnEnding 예약 제외)</summary>
        public float SpendableForAllyBuffs => TotalAP - MasterMinAttackReserved - TurnEndingReserved;

        /// <summary>공격 루프 AP 하한 (전략 R&G 비용)</summary>
        public float AttackLoopFloor => StrategyPostActionReserved;

        /// <summary>★ v3.19.4: 버프 Phase에 전달할 통합 예약 AP (PostMove + TurnEnding + Strategy)
        /// effectiveReservedAP 로컬 변수를 대체 — 자동 계산으로 수동 동기화 오류 방지</summary>
        public float EffectiveReserved => PostMoveReserved + TurnEndingReserved + StrategyPostActionReserved;

        /// <summary>총 예약 AP</summary>
        public float TotalReserved => PostMoveReserved + TurnEndingReserved + StrategyPostActionReserved + MasterMinAttackReserved;

        /// <summary>비용을 감당할 수 있는지 판단 (모든 예약 고려)</summary>
        public bool CanAfford(float cost, float currentRemainingAP)
        {
            return currentRemainingAP - cost >= TurnEndingReserved + StrategyPostActionReserved;
        }

        /// <summary>버프 비용을 감당할 수 있는지 판단 (마스터 최소 공격 AP + TurnEnding 고려)</summary>
        public bool CanAffordBuff(float cost, float currentRemainingAP)
        {
            return currentRemainingAP - cost >= MasterMinAttackReserved + TurnEndingReserved;
        }

        public override string ToString()
        {
            return $"APBudget(total={TotalAP:F1}, postMove={PostMoveReserved:F1}, " +
                $"turnEnd={TurnEndingReserved:F1}, strategyR&G={StrategyPostActionReserved:F1}, " +
                $"masterAtk={MasterMinAttackReserved:F1})";
        }
    }
}
