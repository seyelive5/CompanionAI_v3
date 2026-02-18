using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using CompanionAI_v3.Analysis;

namespace CompanionAI_v3.Core
{
    /// <summary>
    /// ★ v3.9.04: AI 계산 프레임 분산 — 계산 단계
    /// ProcessTurn()에서 Analyze → Plan+Execute를 별도 프레임으로 분리
    /// </summary>
    public enum ComputePhase
    {
        /// <summary>기본 상태 — Analyze 수행 후 WaitingForPlan으로 전환</summary>
        Ready,

        /// <summary>Analyze 완료 — 다음 프레임에 Plan+Execute 수행</summary>
        WaitingForPlan
    }

    /// <summary>
    /// 현재 턴의 상태를 추적
    /// 한 유닛의 턴 동안 유지되는 모든 상태 정보
    /// </summary>
    public class TurnState
    {
        #region Constants

        /// <summary>
        /// 턴당 최대 행동 수 (최후의 안전장치)
        /// ★ v3.5.25: 15 → 9999 (사실상 무제한)
        /// 게임의 자연스러운 제한(AP/MP/쿨다운)을 따름
        /// </summary>
        public const int MaxActionsPerTurn = 9999;

        #endregion

        #region Identity

        /// <summary>이 턴의 유닛</summary>
        public BaseUnitEntity Unit { get; }

        /// <summary>유닛 고유 ID</summary>
        public string UnitId { get; }

        /// <summary>턴 시작 프레임</summary>
        public int TurnStartFrame { get; }

        /// <summary>이 턴이 시작된 전투 라운드</summary>
        public int CombatRound { get; }

        #endregion

        #region Plan

        /// <summary>현재 턴 계획</summary>
        public TurnPlan Plan { get; set; }

        #endregion

        #region Executed Actions

        /// <summary>이번 턴에 실행된 행동들</summary>
        public List<PlannedAction> ExecutedActions { get; } = new List<PlannedAction>();

        /// <summary>이번 턴 행동 횟수</summary>
        public int ActionCount => ExecutedActions.Count;

        #endregion

        #region State Flags

        /// <summary>이동 완료 여부</summary>
        public bool HasMovedThisTurn { get; set; }

        /// <summary>공격 완료 여부 (첫 공격)</summary>
        public bool HasAttackedThisTurn { get; set; }

        /// <summary>버프 사용 여부</summary>
        public bool HasBuffedThisTurn { get; set; }

        /// <summary>재장전 완료 여부</summary>
        public bool HasReloadedThisTurn { get; set; }

        /// <summary>힐 사용 여부</summary>
        public bool HasHealedThisTurn { get; set; }

        /// <summary>첫 번째 행동(AP 소모) 완료 여부</summary>
        public bool HasPerformedFirstAction { get; set; }

        /// <summary>★ v3.0.3: 이번 턴 이동 횟수 (다중 이동 지원)</summary>
        public int MoveCount { get; set; }

        /// <summary>★ v3.0.3: 공격 후 추가 이동 허용 (이동→공격 완료 시 추가 이동 가능)</summary>
        public bool AllowPostAttackMove => HasMovedThisTurn && HasAttackedThisTurn;

        /// <summary>★ v3.0.7: 추격 이동 허용 (이동했지만 공격 못함 - 적이 너무 멀어서)</summary>
        public bool AllowChaseMove => HasMovedThisTurn && !HasAttackedThisTurn;

        /// <summary>★ v3.9.72: 이번 턴 무기 세트 전환 횟수</summary>
        public int WeaponSwitchCount { get; set; }

        #endregion

        #region Resources

        /// <summary>
        /// 남은 AP
        /// ★ v3.4.01: P2-1 레거시 필드 - 실시간 AP는 CombatAPI.GetCurrentAP() 사용
        /// RecordAction()에서 내부 추적용으로만 사용
        /// </summary>
        [System.Obsolete("v3.0.77+: Use CombatAPI.GetCurrentAP() for real-time AP")]
        public float RemainingAP { get; set; }

        /// <summary>
        /// 남은 MP
        /// ★ v3.4.01: P2-1 레거시 필드 - 실시간 MP는 CombatAPI.GetCurrentMP() 사용
        /// RecordAction()에서 내부 추적용으로만 사용
        /// </summary>
        [System.Obsolete("v3.0.77+: Use CombatAPI.GetCurrentMP() for real-time MP")]
        public float RemainingMP { get; set; }

        /// <summary>시작 AP</summary>
        public float StartingAP { get; }

        /// <summary>시작 MP</summary>
        public float StartingMP { get; }

        /// <summary>★ v3.0.76: 이 턴에서 본 최대 AP (버프로 인한 AP 증가 감지용)</summary>
        public float MaxAPSeenThisTurn { get; set; }

        /// <summary>★ v3.1.03: 마지막으로 확인한 MP (리플랜용)</summary>
        public float LastKnownMP { get; set; }

        #endregion

        #region Safety

        /// <summary>연속 실패 횟수</summary>
        public int ConsecutiveFailures { get; set; }

        /// <summary>★ v3.0.46: 대기 횟수 (무한 대기 방지)</summary>
        public int WaitCount { get; set; }

        /// <summary>★ v3.8.92: 실패 후 폴백 재계획 횟수</summary>
        public int FallbackReplanCount { get; set; }

        /// <summary>★ v3.9.06: 빈 큐 EndTurn 시 안전 재계획 횟수 (최대 1회)</summary>
        public int EmptyPlanEndCount { get; set; }

        /// <summary>★ v3.9.14: 마지막 계획 생성 시점의 게임 AP (정체 감지용)</summary>
        public float APAtLastPlanStart { get; set; } = -1f;

        /// <summary>★ v3.9.14: AP 정체 연속 횟수 (AP 변화 없고 공격 안 했으면 +1, 3회 시 EndTurn)</summary>
        public int StagnantPlanCount { get; set; }

        /// <summary>최대 액션 도달 여부</summary>
        public bool HasReachedMaxActions => ActionCount >= MaxActionsPerTurn;

        #endregion

        #region Frame Spreading (★ v3.9.04)

        /// <summary>
        /// ★ v3.9.04: 현재 계산 단계 — Analyze와 Plan+Execute를 별도 프레임으로 분리
        /// 스터터링 방지: 한 프레임에 50~150ms 계산 → 2프레임에 분산
        /// </summary>
        public ComputePhase CurrentComputePhase { get; set; } = ComputePhase.Ready;

        /// <summary>
        /// ★ v3.9.04: Analyze 결과를 다음 프레임의 Plan+Execute로 전달
        /// Ready→WaitingForPlan 전환 시 설정, Plan 완료 후 null로 클리어
        /// </summary>
        public Situation PendingSituation { get; set; }

        #endregion

        #region Strategic Context (★ v3.8.86: 재계획 시 보존)

        /// <summary>
        /// 재계획 시에도 유지되는 전략적 맥락 정보
        /// Plan.Cancel() → 새 CreatePlan()에서 이전 계획의 의도를 파악 가능
        /// </summary>
        private Dictionary<string, object> _strategicContext;

        /// <summary>전략 컨텍스트 값 설정</summary>
        public void SetContext(string key, object value)
        {
            if (_strategicContext == null)
                _strategicContext = new Dictionary<string, object>(4);
            _strategicContext[key] = value;
        }

        /// <summary>전략 컨텍스트 값 조회 (없으면 기본값 반환)</summary>
        public T GetContext<T>(string key, T defaultValue = default(T))
        {
            if (_strategicContext != null && _strategicContext.TryGetValue(key, out var value) && value is T typed)
                return typed;
            return defaultValue;
        }

        /// <summary>전략 컨텍스트 키 존재 여부</summary>
        public bool HasContext(string key)
            => _strategicContext != null && _strategicContext.ContainsKey(key);

        #endregion

        #region Constructor

        public TurnState(BaseUnitEntity unit, float currentAP, float currentMP)
        {
            Unit = unit;
            UnitId = unit?.UniqueId ?? "unknown";
            TurnStartFrame = UnityEngine.Time.frameCount;

            // ★ 게임의 현재 전투 라운드 저장
            CombatRound = Kingmaker.Game.Instance?.TurnController?.CombatRound ?? 0;

            StartingAP = currentAP;
            StartingMP = currentMP;
            RemainingAP = currentAP;
            RemainingMP = currentMP;
            MaxAPSeenThisTurn = currentAP;  // ★ v3.0.76: 초기값 설정
            LastKnownMP = currentMP;  // ★ v3.1.03: MP 변화 감지용
        }

        #endregion

        #region Methods

        /// <summary>
        /// 행동 실행 기록
        /// </summary>
        public void RecordAction(PlannedAction action, bool success)
        {
            action.IsExecuted = true;
            action.WasSuccessful = success;
            ExecutedActions.Add(action);

            if (success)
            {
                ConsecutiveFailures = 0;

                // 상태 플래그 업데이트
                switch (action.Type)
                {
                    case ActionType.Move:
                        HasMovedThisTurn = true;
                        MoveCount++;  // ★ v3.0.3: 이동 횟수 추적
                        // ★ v3.0.10: RemainingMP=0 해킹 제거 - Commands.Empty 체크로 중복 이동 방지
                        break;
                    case ActionType.Attack:
                        HasAttackedThisTurn = true;
                        HasPerformedFirstAction = true;
                        RemainingAP -= action.APCost;
                        break;
                    case ActionType.Buff:
                        HasBuffedThisTurn = true;
                        RemainingAP -= action.APCost;
                        break;
                    case ActionType.Reload:
                        HasReloadedThisTurn = true;
                        HasPerformedFirstAction = true;
                        RemainingAP -= action.APCost;
                        break;
                    case ActionType.Heal:
                        HasHealedThisTurn = true;
                        RemainingAP -= action.APCost;
                        break;
                    case ActionType.Debuff:
                    case ActionType.Support:
                    case ActionType.Special:
                        HasPerformedFirstAction = true;
                        RemainingAP -= action.APCost;
                        break;
                    case ActionType.WeaponSwitch:  // ★ v3.9.72: 무기 전환 (0 AP)
                        WeaponSwitchCount++;
                        break;
                }
            }
            else
            {
                ConsecutiveFailures++;
            }

            Main.LogDebug($"[TurnState] Action #{ActionCount}: {action} -> {(success ? "SUCCESS" : "FAILED")}");
        }

        /// <summary>
        /// 특정 능력을 이번 턴에 사용했는지 확인
        /// </summary>
        public bool HasUsedAbility(string abilityGuid)
        {
            foreach (var action in ExecutedActions)
            {
                if (action.WasSuccessful == true &&
                    action.Ability?.Blueprint?.AssetGuid?.ToString() == abilityGuid)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 디버그 정보 출력
        /// </summary>
        public override string ToString()
        {
            return $"[TurnState] {Unit?.CharacterName}: AP={RemainingAP:F1}/{StartingAP:F1}, " +
                   $"Actions={ActionCount}, Moved={HasMovedThisTurn}, Attacked={HasAttackedThisTurn}";
        }

        #endregion
    }
}
