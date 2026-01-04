using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Planning;
using CompanionAI_v3.Execution;
using CompanionAI_v3.GameInterface;

namespace CompanionAI_v3.Core
{
    /// <summary>
    /// 턴 오케스트레이터 - 모든 AI 결정의 단일 제어점
    ///
    /// 핵심 원칙:
    /// 1. TurnPlanner가 턴 시작 시 전체 계획 수립
    /// 2. 계획에 따라 순차적으로 행동 실행
    /// 3. 게임 AI는 실행만, 결정은 우리가
    /// </summary>
    public class TurnOrchestrator
    {
        #region Singleton

        private static TurnOrchestrator _instance;
        public static TurnOrchestrator Instance => _instance ??= new TurnOrchestrator();

        #endregion

        #region Dependencies

        private readonly SituationAnalyzer _analyzer;
        private readonly TurnPlanner _planner;
        private readonly ActionExecutor _executor;

        #endregion

        #region State

        /// <summary>현재 턴 상태 (유닛별)</summary>
        private readonly Dictionary<string, TurnState> _turnStates = new Dictionary<string, TurnState>();

        /// <summary>현재 처리 중인 유닛 ID</summary>
        private string _currentUnitId;

        /// <summary>대기 중인 이동 목적지 (유닛별)</summary>
        private readonly Dictionary<string, UnityEngine.Vector3> _pendingMoveDestinations = new Dictionary<string, UnityEngine.Vector3>();

        /// <summary>턴 종료 결정된 유닛 (안전장치용 - v3.0.72부터 실제 사용 안 함)</summary>
        private readonly HashSet<string> _pendingEndTurn = new HashSet<string>();

        /// <summary>★ v3.5.00: 마지막으로 처리한 라운드 (TeamBlackboard.OnRoundStart 호출용)</summary>
        private int _lastProcessedRound = -1;

        // ★ v3.0.72: _allowedCoverSeekOnce 제거
        // IsFinishedTurn = true + Status.Success 방식으로 전환하여 불필요해짐

        #endregion

        #region Constructor

        public TurnOrchestrator()
        {
            _analyzer = new SituationAnalyzer();
            _planner = new TurnPlanner();
            _executor = new ActionExecutor();
        }

        #endregion

        #region Main Entry Point

        /// <summary>
        /// 게임에서 호출되는 메인 진입점
        /// SelectAbilityTargetPatch에서 호출됨
        /// </summary>
        public ExecutionResult ProcessTurn(BaseUnitEntity unit)
        {
            if (unit == null)
            {
                return ExecutionResult.Failure("Unit is null");
            }

            string unitId = unit.UniqueId;
            string unitName = unit.CharacterName;

            try
            {
                // ★ v3.0.70: 새 턴 시작 시 이전 턴의 pendingEndTurn 클리어
                // (쳐부숴라 등 즉시 턴 부여 스킬 사용 후 실제 턴에서 문제 방지)
                if (IsGameTurnStart(unit))
                {
                    if (_pendingEndTurn.Contains(unitId))
                    {
                        Main.Log($"[Orchestrator] {unitName}: New turn started - clearing stale pendingEndTurn");
                        _pendingEndTurn.Remove(unitId);
                    }
                    // 새 턴이면 TurnState도 리셋
                    _turnStates.Remove(unitId);
                }

                // ★ v3.0.67: 이미 턴 종료가 결정되었으면 반복 처리 방지
                bool isPending = _pendingEndTurn.Contains(unitId);
                Main.LogDebug($"[Orchestrator] {unitName}: pendingEndTurn check - isPending={isPending}, setCount={_pendingEndTurn.Count}");

                if (isPending)
                {
                    Main.Log($"[Orchestrator] {unitName}: Already pending end turn - skipping");
                    return ExecutionResult.EndTurn("Already pending end turn");
                }

                // 1. 턴 상태 가져오기 또는 생성
                var turnState = GetOrCreateTurnState(unit);

                // ★ v3.1.09: MP/AP 증가 감지는 TurnPlan.NeedsReplan()으로 통합
                // 더 이상 여기서 별도로 체크하지 않음

                // ★ v3.0.69: 게임 AP=0이고 이미 행동했으면 즉시 턴 종료 (안전장치)
                float currentMP = CombatAPI.GetCurrentMP(unit);
                // ★ v3.1.06: Move가 남아있고 MP가 있으면 계속 진행 (Move는 AP 안 씀)
                float gameAP = CombatAPI.GetCurrentAP(unit);
                if (gameAP <= 0 && turnState.ActionCount > 0)
                {
                    // ★ v3.1.06: 플랜에 Move가 남아있으면 계속 진행
                    var pendingAction = turnState.Plan?.PeekNextAction();
                    if (pendingAction?.Type == ActionType.Move && currentMP > 0)
                    {
                        Main.Log($"[Orchestrator] {unitName}: AP=0 but Move pending with MP={currentMP:F1} - continuing");
                    }
                    else
                    {
                        Main.Log($"[Orchestrator] {unitName}: Game AP=0 with {turnState.ActionCount} actions done - ending turn");
                        return ExecutionResult.EndTurn("No AP remaining");
                    }
                }

                // 2. 안전 체크
                if (turnState.HasReachedMaxActions)
                {
                    Main.LogWarning($"[Orchestrator] {unitName}: Max actions reached ({TurnState.MaxActionsPerTurn})");
                    return ExecutionResult.EndTurn("Max actions reached");
                }

                if (turnState.ConsecutiveFailures >= 3)
                {
                    Main.LogWarning($"[Orchestrator] {unitName}: Too many consecutive failures");
                    return ExecutionResult.EndTurn("Too many failures");
                }

                // ★ v3.0.10: 이전 명령이 완료될 때까지 대기
                // 게임의 TaskNodeWaitCommandsDone과 동일한 접근법
                // ★ v3.0.46: 무한 대기 방지 타임아웃 추가
                if (!CombatAPI.IsReadyForNextAction(unit))
                {
                    turnState.WaitCount++;
                    if (turnState.WaitCount > 120)  // 약 2초 대기 후 강제 진행
                    {
                        Main.LogWarning($"[Orchestrator] {unitName}: Wait timeout ({turnState.WaitCount} frames) - forcing end turn");
                        turnState.WaitCount = 0;
                        return ExecutionResult.EndTurn("Wait timeout");
                    }
                    Main.LogDebug($"[Orchestrator] {unitName}: Waiting for previous command to complete (wait={turnState.WaitCount})");
                    return ExecutionResult.Waiting("Command in progress");
                }
                turnState.WaitCount = 0;  // 대기 성공 시 초기화

                // ★ v3.5.00: 이전 공격의 킬 확인 (명령 완료 후)
                _executor.CheckForKills();

                // ★ v3.5.00: 라운드 변경 감지 및 TeamBlackboard 알림
                var turnController = Kingmaker.Game.Instance?.TurnController;
                if (turnController != null)
                {
                    int currentRound = turnController.CombatRound;
                    if (_lastProcessedRound != currentRound)
                    {
                        Main.Log($"[Orchestrator] Round changed: {_lastProcessedRound} → {currentRound}");
                        TeamBlackboard.Instance.OnRoundStart(currentRound);
                        _lastProcessedRound = currentRound;
                    }
                }

                // 3. 상황 분석
                var situation = _analyzer.Analyze(unit, turnState);

                // ★ v3.2.10: TeamBlackboard에 상황 등록
                TeamBlackboard.Instance.RegisterUnitSituation(unitId, situation);

                // 4. 계획이 없거나 완료되면 새 계획 생성
                // ★ v3.0.63: 연속 계획 생성 허용 (SituationAnalyzer에서 맥락 반영)
                if (turnState.Plan == null || turnState.Plan.IsComplete)
                {
                    Main.Log($"[Orchestrator] {unitName}: Creating new turn plan (continuation={turnState.Plan?.IsComplete ?? false})");
                    turnState.Plan = _planner.CreatePlan(situation, turnState);

                    // ★ v3.2.10: TeamBlackboard에 계획 등록
                    TeamBlackboard.Instance.RegisterUnitPlan(unitId, turnState.Plan);
                }

                // 5. 계획 재수립 필요 여부 확인
                if (turnState.Plan.NeedsReplan(situation))
                {
                    Main.Log($"[Orchestrator] {unitName}: Replanning due to situation change");
                    turnState.Plan = _planner.CreatePlan(situation, turnState);

                    // ★ v3.2.10: 재계획 시에도 Blackboard 업데이트
                    TeamBlackboard.Instance.RegisterUnitPlan(unitId, turnState.Plan);
                }

                // 6. 다음 행동 가져오기
                var nextAction = turnState.Plan.GetNextAction();

                if (nextAction == null)
                {
                    Main.Log($"[Orchestrator] {unitName}: No more actions in plan");
                    return ExecutionResult.EndTurn("Plan complete");
                }

                // 7. 행동 실행
                Main.Log($"[Orchestrator] {unitName}: Executing {nextAction}");
                var result = _executor.Execute(nextAction, situation);

                // 8. 결과 기록
                bool success = result.Type == ResultType.CastAbility || result.Type == ResultType.MoveTo;
                turnState.RecordAction(nextAction, success);

                // ★ v3.1.08: pendingEndTurn 설정 제거 - MainAIPatch에서 Commands 체크로 대체
                // 이동 애니메이션 중에는 Commands가 비어있지 않으므로 MainAIPatch에서 Running 반환

                // ★ v3.0.4: 능력 사용 추적 - 중앙화
                if (success && nextAction.Ability != null)
                {
                    AbilityUsageTracker.MarkUsed(unit.UniqueId, nextAction.Ability);

                    // 타겟이 있는 경우 타겟별 추적도
                    var targetEntity = nextAction.Target?.Entity as BaseUnitEntity;
                    if (targetEntity != null && nextAction.Type != ActionType.Attack)
                    {
                        AbilityUsageTracker.MarkUsedOnTarget(unit.UniqueId, nextAction.Ability, targetEntity.UniqueId);
                    }
                }
                else if (!success && nextAction.Ability != null)
                {
                    // 실패 시 실패 추적
                    AbilityUsageTracker.MarkFailed(unit.UniqueId, nextAction.Ability);
                }

                // ★ v3.0.93: 실패 시 게임 AI로 위임하지 않고 EndTurn 반환
                // 게임 AI가 제어권을 가져가면 메디킷 오사용 등 이상 행동 발생
                // ★ v3.5.23: 회복 가능한 실패는 해당 액션만 스킵하고 계속 진행
                if (result.Type == ResultType.Failure)
                {
                    // 회복 가능한 에러 목록 (해당 액션만 스킵, 다음 액션 계속)
                    bool isRecoverable = IsRecoverableFailure(result.Reason);

                    if (isRecoverable && turnState.Plan?.RemainingActionCount > 0)
                    {
                        Main.LogWarning($"[Orchestrator] {unitName}: Recoverable failure ({result.Reason}) - skipping action and continuing");
                        // 다음 액션으로 계속 진행하도록 Continue 반환
                        return ExecutionResult.Continue();
                    }

                    Main.LogWarning($"[Orchestrator] {unitName}: Execution failed ({result.Reason}) - ending turn instead of delegating to game AI");
                    turnState.Plan?.Cancel("Execution failed");
                    return ExecutionResult.EndTurn($"Execution failed: {result.Reason}");
                }

                return result;
            }
            catch (Exception ex)
            {
                Main.LogError($"[Orchestrator] {unitName}: Critical error - {ex.Message}");
                Main.LogError($"[Orchestrator] Stack: {ex.StackTrace}");
                // ★ v3.0.93: 예외 시에도 EndTurn 반환 (게임 AI 위임 방지)
                return ExecutionResult.EndTurn($"Exception: {ex.Message}");
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// ★ v3.5.23: 회복 가능한 실패인지 확인
        /// 이 에러들은 해당 액션만 스킵하고 다음 액션으로 계속 진행
        /// </summary>
        private bool IsRecoverableFailure(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return false;

            // 회복 가능한 에러 목록
            var recoverableErrors = new[]
            {
                "StrategistZonesCantOverlap",   // 전략가 구역 겹침 - 다른 위치에 배치하면 됨
                "AlreadyHasBuff",               // 이미 버프 있음 - 스킵 가능
                "BuffAlreadyActive",            // 버프 이미 활성 - 스킵 가능
                "TargetHasHigherBuff",          // 더 좋은 버프 있음 - 스킵 가능
                "NotEnoughResources",           // 리소스 부족 - 스킵하고 다른 액션 시도
                "CasterMoved"                   // 시전자 이동 - 위치 기반 능력 스킵
            };

            foreach (var error in recoverableErrors)
            {
                if (reason.Contains(error))
                    return true;
            }

            return false;
        }

        #endregion

        #region Turn State Management

        /// <summary>
        /// 유닛의 턴 상태 가져오기 또는 생성
        /// </summary>
        private TurnState GetOrCreateTurnState(BaseUnitEntity unit)
        {
            string unitId = unit.UniqueId;

            // 기존 상태 확인
            if (_turnStates.TryGetValue(unitId, out var state))
            {
                // ★ 게임 턴 시스템 기반으로 새 턴인지 확인
                if (IsNewTurn(state, unit))
                {
                    // 새 턴이면 새로 생성
                    float currentAP = CombatAPI.GetCurrentAP(unit);
                    float currentMP = CombatAPI.GetCurrentMP(unit);

                    state = new TurnState(unit, currentAP, currentMP);
                    _turnStates[unitId] = state;

                    Main.Log($"[Orchestrator] New turn state for {unit.CharacterName}: AP={currentAP:F1}, MP={currentMP:F1}");
                }
                else
                {
                    // ★ v3.0.77: 게임 AP 표시 (TurnState.RemainingAP는 레거시)
                    float gameAP = CombatAPI.GetCurrentAP(unit);
                    Main.LogDebug($"[Orchestrator] Continuing turn for {unit.CharacterName}: AP={gameAP:F1} (game)");
                }
            }
            else
            {
                // 처음 보는 유닛
                float currentAP = CombatAPI.GetCurrentAP(unit);
                float currentMP = CombatAPI.GetCurrentMP(unit);

                state = new TurnState(unit, currentAP, currentMP);
                _turnStates[unitId] = state;

                Main.Log($"[Orchestrator] New turn state for {unit.CharacterName}: AP={currentAP:F1}, MP={currentMP:F1}");
            }

            _currentUnitId = unitId;
            return state;
        }

        /// <summary>
        /// 새 턴인지 확인 (게임 턴 시스템 기반)
        /// ★ v3.0: 프레임 기반에서 게임 턴 시스템 기반으로 변경
        /// ★ v3.0.76: AP 기반 감지 제거, 게임의 Initiative 시스템 활용
        /// </summary>
        private bool IsNewTurn(TurnState state, BaseUnitEntity unit)
        {
            // 1. 게임의 현재 턴 유닛 확인
            var turnController = Kingmaker.Game.Instance?.TurnController;
            if (turnController == null)
            {
                // 턴 컨트롤러가 없으면 폴백: 프레임 기반 (10초 타임아웃)
                int framesSince = UnityEngine.Time.frameCount - state.TurnStartFrame;
                return framesSince > 600;
            }

            // 2. 현재 턴 유닛이 다른 유닛이면 새 턴이 아님 (아직 이 유닛 턴 안 옴)
            var currentTurnUnit = turnController.CurrentUnit as BaseUnitEntity;
            if (currentTurnUnit == null || currentTurnUnit.UniqueId != unit.UniqueId)
            {
                // 다른 유닛의 턴인데 왜 여기로 왔지? → 새 턴 처리
                Main.LogDebug($"[Orchestrator] CurrentTurnUnit mismatch: {currentTurnUnit?.CharacterName ?? "null"} vs {unit.CharacterName}");
                return true;
            }

            // 3. 라운드가 바뀌었으면 새 턴
            int currentRound = turnController.CombatRound;
            if (state.CombatRound > 0 && state.CombatRound != currentRound)
            {
                Main.LogDebug($"[Orchestrator] Combat round changed: {state.CombatRound} → {currentRound}");
                return true;
            }

            // ★ v3.0.76: AP 기반 감지 완전 제거
            // CombatRound가 같으면 같은 턴 (버프로 인한 AP 증가와 무관)
            // Note: LastTurn은 GameRound 기반이라 CombatRound와 비교 불가
            return false;
        }

        /// <summary>
        /// ★ v3.0.70: 게임의 새 턴 시작인지 확인 (pendingEndTurn 클리어용)
        /// ★ v3.0.76: AP 기반 감지 제거, CombatRound 기반으로 변경
        /// TurnState 없이도 판단 가능해야 함
        /// </summary>
        private bool IsGameTurnStart(BaseUnitEntity unit)
        {
            // 게임의 현재 턴 유닛 확인
            var turnController = Kingmaker.Game.Instance?.TurnController;
            if (turnController == null) return false;

            var currentTurnUnit = turnController.CurrentUnit as BaseUnitEntity;
            if (currentTurnUnit == null || currentTurnUnit.UniqueId != unit.UniqueId)
            {
                return false;  // 이 유닛의 턴이 아님
            }

            // 이 유닛의 턴인데, TurnState가 없으면 새 턴 시작
            if (!_turnStates.TryGetValue(unit.UniqueId, out var state))
            {
                return true;  // TurnState 없음 = 새 턴
            }

            // ★ v3.0.76: CombatRound가 바뀌었으면 새 턴
            int currentRound = turnController.CombatRound;
            if (state.CombatRound > 0 && state.CombatRound != currentRound)
            {
                return true;  // 새 라운드 = 새 턴
            }

            // AP 기반 감지 제거 - 버프로 인한 AP 증가 오탐 방지
            // CombatRound가 같으면 같은 턴
            return false;
        }

        /// <summary>
        /// ★ v3.0.76: 턴 시작 시 호출 (TurnEventHandler에서)
        /// 게임의 ITurnStartHandler 이벤트로 호출됨
        /// </summary>
        public void OnTurnStart(BaseUnitEntity unit)
        {
            if (unit == null) return;

            string unitId = unit.UniqueId;

            // 이전 턴 상태 정리
            _turnStates.Remove(unitId);
            _pendingEndTurn.Remove(unitId);
            _pendingMoveDestinations.Remove(unitId);

            // 능력 사용 추적 초기화
            AbilityUsageTracker.ClearForUnit(unitId);

            // ★ v3.5.00: 킬 스냅샷 초기화
            _executor.ClearSnapshots();

            Main.Log($"[Orchestrator] Turn started for {unit.CharacterName} (via event)");
        }

        /// <summary>
        /// 턴 종료 시 호출 (TurnEventHandler에서)
        /// 게임의 ITurnEndHandler 이벤트로 호출됨
        /// </summary>
        public void OnTurnEnd(BaseUnitEntity unit)
        {
            if (unit == null) return;

            string unitId = unit.UniqueId;
            if (_turnStates.TryGetValue(unitId, out var state))
            {
                Main.Log($"[Orchestrator] Turn ended for {unit.CharacterName}: {state}");
                _turnStates.Remove(unitId);
            }

            // ★ 턴 종료 상태 정리
            _pendingEndTurn.Remove(unitId);
            _pendingMoveDestinations.Remove(unitId);
        }

        /// <summary>
        /// 전투 종료 시 호출
        /// </summary>
        public void OnCombatEnd()
        {
            Main.Log("[Orchestrator] Combat ended - clearing all turn states");
            _turnStates.Clear();
            _currentUnitId = null;
            _pendingEndTurn.Clear();
            _pendingMoveDestinations.Clear();
            _lastProcessedRound = -1;  // ★ v3.5.00: 라운드 추적 초기화
            Planning.TurnPlanner.ClearDetectedRolesCache();  // ★ v3.1.15: 역할 감지 캐시 정리

            // ★ v3.2.10: TeamBlackboard 정리
            TeamBlackboard.Instance.Clear();
        }

        #endregion

        #region Utility

        /// <summary>
        /// 유닛이 우리 모드의 제어 대상인지 확인
        /// </summary>
        public bool ShouldControl(BaseUnitEntity unit)
        {
            if (unit == null) return false;
            if (!Main.Enabled) return false;

            // 플레이어 동료만 제어
            if (!unit.IsPlayerFaction) return false;

            // ★ v3.0.15: 주인공 AI 제어 옵션
            if (unit.IsMainCharacter)
            {
                var globalSettings = Settings.ModSettings.Instance;
                if (globalSettings == null || !globalSettings.ControlMainCharacter)
                {
                    return false;  // 주인공 AI 제어 비활성화
                }
                // 주인공 AI 제어 활성화됨 - 계속 진행
            }

            // 설정에서 비활성화된 유닛 제외
            var settings = Settings.ModSettings.Instance;
            if (settings != null)
            {
                var charSettings = settings.GetOrCreateSettings(unit.UniqueId, unit.CharacterName);
                if (charSettings != null && !charSettings.EnableCustomAI)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 현재 턴 상태 가져오기 (디버깅용)
        /// </summary>
        public TurnState GetCurrentTurnState()
        {
            if (_currentUnitId != null && _turnStates.TryGetValue(_currentUnitId, out var state))
            {
                return state;
            }
            return null;
        }

        #endregion

        #region Pending Move Destination

        /// <summary>
        /// 이동 목적지 저장 (FindBetterPlace에서 사용)
        /// </summary>
        public void SetPendingMoveDestination(string unitId, UnityEngine.Vector3 destination)
        {
            _pendingMoveDestinations[unitId] = destination;
            Main.LogDebug($"[Orchestrator] Pending move set for {unitId}: {destination}");
        }

        /// <summary>
        /// 저장된 이동 목적지 가져오기 (사용 후 제거됨)
        /// </summary>
        public UnityEngine.Vector3? GetAndClearPendingMoveDestination(string unitId)
        {
            if (_pendingMoveDestinations.TryGetValue(unitId, out var destination))
            {
                _pendingMoveDestinations.Remove(unitId);
                Main.LogDebug($"[Orchestrator] Pending move consumed for {unitId}: {destination}");
                return destination;
            }
            return null;
        }

        /// <summary>
        /// 저장된 이동 목적지가 있는지 확인
        /// </summary>
        public bool HasPendingMoveDestination(string unitId)
        {
            return _pendingMoveDestinations.ContainsKey(unitId);
        }

        #endregion

        #region Pending End Turn

        /// <summary>
        /// 턴 종료 상태 설정 (FindBetterPlace에서 체크)
        /// </summary>
        public void SetPendingEndTurn(string unitId)
        {
            _pendingEndTurn.Add(unitId);
            Main.LogDebug($"[Orchestrator] Pending end turn set for {unitId}");
        }

        /// <summary>
        /// 턴 종료가 예정되어 있는지 확인
        /// </summary>
        public bool IsPendingEndTurn(string unitId)
        {
            return _pendingEndTurn.Contains(unitId);
        }

        /// <summary>
        /// 턴 종료 상태 해제
        /// </summary>
        public void ClearPendingEndTurn(string unitId)
        {
            _pendingEndTurn.Remove(unitId);
        }

        #endregion

        // ★ v3.0.72: Cover Seek Once region 제거
        // IsFinishedTurn = true + Status.Success 방식으로 전환하여 불필요해짐
    }
}
