namespace CompanionAI_v3.Core
{
    /// <summary>
    /// ★ v3.8.44: 공격 Phase → 이동 Phase 간 컨텍스트 전달
    ///
    /// 문제: 공격 Phase 실패 시 didPlanAttack=false만 전달되어
    /// 이동 Phase가 "왜 실패했는지" 모름 → 잘못된 forceMove 판단
    ///
    /// 해결: 공격 실패 이유와 사용 가능한 능력 사거리를 구조화하여 전달
    /// - RangeWasIssue=true → 이동으로 해결 가능 → forceMove OK
    /// - HeightCheckFailed=true → 이동으로 해결 불가 → forceMove 금지
    /// - AllAbilitiesFiltered=true → 사용 가능한 공격 없음 → forceMove 금지
    /// </summary>
    public class AttackPhaseContext
    {
        /// <summary>
        /// 사용 가능한 공격 능력 중 최대 사거리 (타일 단위)
        /// -1이면 사용 가능한 능력 없음
        /// MovementPlanner가 무기 사거리 대신 이 값으로 이동 위치 계산
        /// </summary>
        public float BestAbilityRange = -1f;

        /// <summary>
        /// 사거리 부족이 공격 실패의 주요 원인인지
        /// true면 이동으로 해결 가능성 있음
        /// </summary>
        public bool RangeWasIssue;

        /// <summary>
        /// AOE 높이 차이 체크 실패 (수평 이동으로 해결 불가)
        /// </summary>
        public bool HeightCheckFailed;

        /// <summary>
        /// 모든 공격 능력이 필터링됨 (쿨다운, DangerousAoE 제한 등)
        /// </summary>
        public bool AllAbilitiesFiltered;

        /// <summary>
        /// ★ v3.8.72: Analyzer는 Hittable이라 했지만 AttackPlanner가 실패
        /// LoS, 캐시 stale, 게임 상태 변화 등 다양한 원인
        /// true면 이동으로 해결 시도 (새 위치에서 LoS 확보)
        /// </summary>
        public bool HittableMismatch;

        /// <summary>
        /// ★ v3.9.28: 이동 액션이 이미 계획됨 (MoveToAttack 전략)
        /// true면 CanUseAbilityOn 사거리 실패 시 RecalculateHittable 결과를 신뢰
        /// (유닛이 아직 이동 전이므로 현재 위치 기준 CanUseAbilityOn이 TargetTooFar 반환)
        /// </summary>
        public bool HasPendingMove;

        /// <summary>
        /// 이동으로 공격 문제를 해결할 수 있는지 판단
        /// ★ v3.8.72: HittableMismatch도 이동 후보 (새 위치에서 LoS 확보 가능)
        /// </summary>
        public bool ShouldForceMove => (RangeWasIssue || HittableMismatch) && !HeightCheckFailed && !AllAbilitiesFiltered;

        /// <summary>
        /// 유효한 능력 사거리가 있는지 (MovementPlanner용)
        /// </summary>
        public bool HasValidRange => BestAbilityRange > 0f;

        public override string ToString()
        {
            return $"AttackCtx[range={BestAbilityRange:F1}, rangeIssue={RangeWasIssue}, " +
                   $"heightFail={HeightCheckFailed}, allFiltered={AllAbilitiesFiltered}, " +
                   $"hittableMismatch={HittableMismatch}, pendingMove={HasPendingMove}, shouldMove={ShouldForceMove}]";
        }
    }
}
