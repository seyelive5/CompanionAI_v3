using System;
using System.Collections.Generic;
using System.Diagnostics;  // ★ v3.8.48: Stopwatch 프로파일링
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.AI;  // ★ v3.9.02: AiBrainController.SecondsAiTimeout
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

        // ★ v3.8.48: 프로파일링 (Stopwatch)
        private readonly Stopwatch _profilerStopwatch = new Stopwatch();
        private long _totalAnalyzeMs;
        private long _totalPlanMs;
        private long _totalExecuteMs;
        private int _profilerTurnCount;

        // ★ v3.0.72: _allowedCoverSeekOnce 제거
        // IsFinishedTurn = true + Status.Success 방식으로 전환하여 불필요해짐

        /// <summary>
        /// ★ v3.9.02: 게임 기본 AI 타임아웃 백업 (턴 종료 시 복원)
        /// </summary>
        private static float _originalAiTimeout = -1f;

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
        /// ★ v3.5.36: 서브 메서드로 분해하여 가독성 향상
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
                // 1. 검증 및 준비
                var validateResult = ValidateAndPrepare(unit, unitId, unitName, out var turnState);
                if (validateResult != null)
                    return validateResult;

                // 2. 이전 명령 완료 대기
                var waitResult = WaitForPendingCommands(unit, unitName, turnState);
                if (waitResult != null)
                    return waitResult;

                // 3. 명령 완료 후 처리
                _executor.CheckForKills();
                NotifyRoundChangeIfNeeded();

                // 4. 계획 생성/업데이트
                var situation = CreateOrUpdatePlan(unit, unitId, unitName, turnState);
                if (situation == null)
                {
                    return ExecutionResult.EndTurn("Situation analysis failed");
                }

                // 5. 다음 행동 실행
                return ExecuteNextAction(unit, unitName, turnState, situation);
            }
            catch (Exception ex)
            {
                Main.LogError($"[Orchestrator] {unitName}: Critical error - {ex.Message}");
                Main.LogError($"[Orchestrator] Stack: {ex.StackTrace}");
                return ExecutionResult.EndTurn($"Exception: {ex.Message}");
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// ★ v3.5.23: 회복 가능한 실패인지 확인
        /// ★ v3.5.36: ExecutionErrorType Enum으로 리팩토링
        /// 이 에러들은 해당 액션만 스킵하고 다음 액션으로 계속 진행
        /// </summary>
        private bool IsRecoverableFailure(string reason)
        {
            var errorType = ExecutionErrorTypeExtensions.ParseFromReason(reason);
            return errorType.IsRecoverable();
        }

        #endregion

        #region ProcessTurn Sub-Methods (v3.5.36)

        /// <summary>
        /// ★ v3.5.36: 턴 시작 전 검증 및 준비
        /// - 새 턴 시 stale 데이터 정리
        /// - pendingEndTurn 체크
        /// - AP=0 안전장치
        /// </summary>
        /// <returns>null이면 계속 진행, ExecutionResult면 즉시 반환</returns>
        private ExecutionResult ValidateAndPrepare(BaseUnitEntity unit, string unitId, string unitName, out TurnState turnState)
        {
            turnState = null;

            // 새 턴 시작 시 이전 턴의 stale 데이터 정리
            if (IsGameTurnStart(unit))
            {
                if (_pendingEndTurn.Contains(unitId))
                {
                    Main.Log($"[Orchestrator] {unitName}: New turn started - clearing stale pendingEndTurn");
                    _pendingEndTurn.Remove(unitId);
                }
                _turnStates.Remove(unitId);
            }

            // 이미 턴 종료가 결정되었으면 반복 처리 방지
            bool isPending = _pendingEndTurn.Contains(unitId);
            Main.LogDebug($"[Orchestrator] {unitName}: pendingEndTurn check - isPending={isPending}, setCount={_pendingEndTurn.Count}");

            if (isPending)
            {
                Main.Log($"[Orchestrator] {unitName}: Already pending end turn - skipping");
                return ExecutionResult.EndTurn("Already pending end turn");
            }

            // 턴 상태 가져오기 또는 생성
            turnState = GetOrCreateTurnState(unit);

            // AP=0이고 이미 행동했으면 안전장치로 턴 종료
            float currentMP = CombatAPI.GetCurrentMP(unit);
            float gameAP = CombatAPI.GetCurrentAP(unit);
            if (gameAP <= 0 && turnState.ActionCount > 0)
            {
                // 플랜에 Move가 남아있고 MP가 있으면 계속 진행 (Move는 AP 안 씀)
                var pendingAction = turnState.Plan?.PeekNextAction();
                if (pendingAction?.Type == ActionType.Move && currentMP > 0)
                {
                    Main.Log($"[Orchestrator] {unitName}: AP=0 but Move pending with MP={currentMP:F1} - continuing");
                }
                // ★ v3.5.88: 0 AP 공격이 있으면 계속 진행 (Break Through → Slash 등)
                else if (CombatAPI.HasZeroAPAttack(unit))
                {
                    Main.Log($"[Orchestrator] {unitName}: AP=0 but 0 AP attacks available - continuing");
                }
                else
                {
                    Main.Log($"[Orchestrator] {unitName}: Game AP=0 with {turnState.ActionCount} actions done - ending turn");
                    return ExecutionResult.EndTurn("No AP remaining");
                }
            }

            // 안전 체크: 최대 행동 수
            if (turnState.HasReachedMaxActions)
            {
                Main.LogWarning($"[Orchestrator] {unitName}: Max actions reached ({TurnState.MaxActionsPerTurn})");
                return ExecutionResult.EndTurn("Max actions reached");
            }

            // 안전 체크: 연속 실패 횟수
            if (turnState.ConsecutiveFailures >= GameConstants.MAX_CONSECUTIVE_FAILURES)
            {
                // ★ v3.8.92: AP 남아있고 폴백 재계획 여유 있으면 리셋 후 재시도
                float currentAPForReset = CombatAPI.GetCurrentAP(unit);
                if (currentAPForReset > 0 && turnState.FallbackReplanCount < GameConstants.MAX_FALLBACK_REPLANS)
                {
                    turnState.FallbackReplanCount++;
                    turnState.ConsecutiveFailures = 0;
                    turnState.Plan?.Cancel($"Consecutive failure reset #{turnState.FallbackReplanCount}");
                    Main.Log($"[Orchestrator] {unitName}: Consecutive failures reset - fallback replan #{turnState.FallbackReplanCount} (AP={currentAPForReset:F1})");
                    // null 반환 = 검증 통과 → CreateOrUpdatePlan에서 IsComplete=true → 새 계획
                }
                else
                {
                    Main.LogWarning($"[Orchestrator] {unitName}: Too many consecutive failures, no recovery left (FallbackReplans={turnState.FallbackReplanCount})");
                    return ExecutionResult.EndTurn("Too many failures");
                }
            }

            return null;  // 검증 통과
        }

        /// <summary>
        /// ★ v3.5.36: 이전 명령 완료 대기
        /// </summary>
        /// <returns>null이면 계속 진행, ExecutionResult면 즉시 반환</returns>
        private ExecutionResult WaitForPendingCommands(BaseUnitEntity unit, string unitName, TurnState turnState)
        {
            if (!CombatAPI.IsReadyForNextAction(unit))
            {
                turnState.WaitCount++;
                if (turnState.WaitCount > GameConstants.COMMAND_WAIT_TIMEOUT_FRAMES)
                {
                    Main.LogWarning($"[Orchestrator] {unitName}: Wait timeout ({turnState.WaitCount} frames) - forcing end turn");
                    turnState.WaitCount = 0;
                    return ExecutionResult.EndTurn("Wait timeout");
                }
                Main.LogDebug($"[Orchestrator] {unitName}: Waiting for previous command to complete (wait={turnState.WaitCount})");
                return ExecutionResult.Waiting("Command in progress");
            }
            turnState.WaitCount = 0;  // 대기 성공 시 초기화
            return null;  // 대기 완료
        }

        /// <summary>
        /// ★ v3.5.36: 라운드 변경 감지 및 알림
        /// </summary>
        private void NotifyRoundChangeIfNeeded()
        {
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
        }

        /// <summary>
        /// ★ v3.5.36: 계획 생성 또는 업데이트
        /// </summary>
        /// <returns>분석된 Situation, null이면 분석 실패</returns>
        private Situation CreateOrUpdatePlan(BaseUnitEntity unit, string unitId, string unitName, TurnState turnState)
        {
            // ★ v3.8.48: 분석 시간 측정
            _profilerStopwatch.Restart();

            // 상황 분석
            var situation = _analyzer.Analyze(unit, turnState);

            if (situation == null)
            {
                Main.LogWarning($"[Orchestrator] {unitName}: Situation analysis returned null");
                return null;
            }

            // ★ v3.8.48: 분석 시간 기록
            _profilerStopwatch.Stop();
            _totalAnalyzeMs += _profilerStopwatch.ElapsedMilliseconds;

            // TeamBlackboard에 상황 등록
            TeamBlackboard.Instance.RegisterUnitSituation(unitId, situation);

            // ★ v3.8.48: 계획 시간 측정
            _profilerStopwatch.Restart();

            // 계획이 없거나 완료되면 새 계획 생성
            if (turnState.Plan == null || turnState.Plan.IsComplete)
            {
                Main.Log($"[Orchestrator] {unitName}: Creating new turn plan (continuation={turnState.Plan?.IsComplete ?? false})");
                turnState.Plan = _planner.CreatePlan(situation, turnState);
                TeamBlackboard.Instance.RegisterUnitPlan(unitId, turnState.Plan);
            }

            // 계획 재수립 필요 여부 확인
            if (turnState.Plan.NeedsReplan(situation))
            {
                // ★ v3.8.86: 재계획 전 전략 컨텍스트 캡처
                CaptureStrategicContextOnReplan(turnState);

                Main.Log($"[Orchestrator] {unitName}: Replanning due to situation change");
                turnState.Plan = _planner.CreatePlan(situation, turnState);
                TeamBlackboard.Instance.RegisterUnitPlan(unitId, turnState.Plan);
            }

            // ★ v3.8.48: 계획 시간 기록
            _profilerStopwatch.Stop();
            _totalPlanMs += _profilerStopwatch.ElapsedMilliseconds;

            return situation;
        }

        /// <summary>
        /// ★ v3.5.36: 다음 행동 실행 및 결과 처리
        /// </summary>
        private ExecutionResult ExecuteNextAction(BaseUnitEntity unit, string unitName, TurnState turnState, Situation situation)
        {
            // 다음 행동 가져오기
            var nextAction = turnState.Plan.GetNextAction();

            if (nextAction == null)
            {
                Main.Log($"[Orchestrator] {unitName}: No more actions in plan");
                return ExecutionResult.EndTurn("Plan complete");
            }

            // ★ v3.8.48: 실행 시간 측정
            _profilerStopwatch.Restart();

            // 행동 실행
            Main.Log($"[Orchestrator] {unitName}: Executing {nextAction}");
            var result = _executor.Execute(nextAction, situation);

            // ★ v3.8.48: 실행 시간 기록 + 10턴마다 평균 출력
            _profilerStopwatch.Stop();
            _totalExecuteMs += _profilerStopwatch.ElapsedMilliseconds;
            _profilerTurnCount++;
            if (_profilerTurnCount % 10 == 0)
            {
                Main.Log($"[Profiler] Last {_profilerTurnCount} turns avg: " +
                    $"Analyze={_totalAnalyzeMs / _profilerTurnCount}ms, " +
                    $"Plan={_totalPlanMs / _profilerTurnCount}ms, " +
                    $"Execute={_totalExecuteMs / _profilerTurnCount}ms");
            }

            // 결과 기록
            bool success = result.Type == ResultType.CastAbility || result.Type == ResultType.MoveTo;
            turnState.RecordAction(nextAction, success);

            // 능력 사용 추적
            TrackAbilityUsage(unit, nextAction, success);

            // ★ v3.8.86: 그룹 실패 처리 (기존 HandleExecutionFailure 호출 전)
            if (result.Type == ResultType.Failure && nextAction.GroupTag != null)
            {
                if (nextAction.FailurePolicy == GroupFailurePolicy.SkipRemainingInGroup)
                {
                    turnState.Plan.FailGroup(nextAction.GroupTag);
                    Main.Log($"[Orchestrator] {unitName}: Group '{nextAction.GroupTag}' failed — remaining actions purged");
                }
                // ContinueGroup은 아무것도 안 함 (그룹 내 다른 액션 계속 실행)
            }

            // ★ v3.8.86: 성공 시 전략 컨텍스트 캡처 (재계획 대비)
            if (success && nextAction.GroupTag != null)
            {
                CaptureStrategicContext(turnState, nextAction);
            }

            // 실패 처리
            if (result.Type == ResultType.Failure)
            {
                return HandleExecutionFailure(unitName, turnState, result);
            }

            return result;
        }

        /// <summary>
        /// ★ v3.5.36: 능력 사용 추적
        /// </summary>
        private void TrackAbilityUsage(BaseUnitEntity unit, PlannedAction action, bool success)
        {
            if (action.Ability == null) return;

            if (success)
            {
                AbilityUsageTracker.MarkUsed(unit.UniqueId, action.Ability);

                // 타겟이 있는 경우 타겟별 추적도 (공격 제외)
                var targetEntity = action.Target?.Entity as BaseUnitEntity;
                if (targetEntity != null && action.Type != ActionType.Attack)
                {
                    AbilityUsageTracker.MarkUsedOnTarget(unit.UniqueId, action.Ability, targetEntity.UniqueId);
                }
            }
            else
            {
                AbilityUsageTracker.MarkFailed(unit.UniqueId, action.Ability);
            }
        }

        /// <summary>
        /// ★ v3.8.86: 성공한 그룹 액션의 전략 컨텍스트 저장
        /// 재계획 시 이전 계획의 의도를 새 계획에 전달
        /// </summary>
        private void CaptureStrategicContext(TurnState turnState, PlannedAction action)
        {
            // 킬 시퀀스 진행 추적
            if (action.GroupTag.StartsWith("KillSeq_"))
            {
                string targetId = action.GroupTag.Substring("KillSeq_".Length);
                turnState.SetContext(StrategicContextKeys.KillSequenceTargetId, targetId);
            }

            // 콤보 전제 추적
            if (action.GroupTag.StartsWith("Combo_"))
            {
                turnState.SetContext(StrategicContextKeys.ComboPrereqApplied, true);
                // 콤보 후속 GUID 저장 (GroupTag에서 추출)
                string abilityGuid = action.GroupTag.Substring("Combo_".Length);
                turnState.SetContext(StrategicContextKeys.ComboFollowUpGuid, abilityGuid);
                var targetEntity = action.Target?.Entity as BaseUnitEntity;
                if (targetEntity != null)
                    turnState.SetContext(StrategicContextKeys.ComboTargetId, targetEntity.UniqueId);
            }
        }

        /// <summary>
        /// ★ v3.8.86: 재계획 전 실행 이력에서 전략 컨텍스트 추출
        /// NeedsReplan/FallbackReplan에 의한 재계획 직전에 호출
        /// </summary>
        private void CaptureStrategicContextOnReplan(TurnState turnState)
        {
            if (turnState?.ExecutedActions == null) return;

            // 공격 성공 이력이 있으면 DeferredRetreat 힌트
            foreach (var action in turnState.ExecutedActions)
            {
                if (action.WasSuccessful == true && action.Type == ActionType.Attack)
                {
                    turnState.SetContext(StrategicContextKeys.DeferredRetreat, true);
                    break;
                }
            }
        }

        /// <summary>
        /// ★ v3.8.92: 실행 실패 처리 — 3-tier 에러 분류 활성화 + 폴백 재계획
        /// 기존: Recoverable + 큐 남음 → Continue, 그 외 → EndTurn
        /// 변경: RequiresReplan 티어 활성화, 큐 비었어도 AP 남으면 재계획 시도
        /// </summary>
        private ExecutionResult HandleExecutionFailure(string unitName, TurnState turnState, ExecutionResult result)
        {
            var errorType = ExecutionErrorTypeExtensions.ParseFromReason(result.Reason);

            // Tier 3 (300+): 턴 종료 필수 (AP 없음 등)
            if (errorType.RequiresEndTurn())
            {
                Main.Log($"[Orchestrator] {unitName}: EndTurn-class failure ({errorType}: {result.Reason})");
                turnState.Plan?.Cancel("EndTurn failure");
                return ExecutionResult.EndTurn($"Execution failed: {result.Reason}");
            }

            // Tier 1 (100-199): 회복 가능 — 큐에 남은 액션 있으면 스킵
            if (errorType.IsRecoverable() && turnState.Plan?.RemainingActionCount > 0)
            {
                Main.LogWarning($"[Orchestrator] {unitName}: Recoverable failure ({errorType}: {result.Reason}) - skipping to next action");
                return ExecutionResult.Continue();
            }

            // Tier 2 (200-299) 또는 Tier 1이지만 큐 비었음: 폴백 재계획 시도
            // 조건: AP > 0 AND 재계획 횟수 제한 이내
            float currentAP = CombatAPI.GetCurrentAP(turnState.Unit);
            if (currentAP > 0 && turnState.FallbackReplanCount < GameConstants.MAX_FALLBACK_REPLANS)
            {
                turnState.FallbackReplanCount++;
                turnState.ConsecutiveFailures = 0;  // 재계획 시 실패 카운터 리셋
                // ★ v3.8.86: 재계획 전 전략 컨텍스트 캡처
                CaptureStrategicContextOnReplan(turnState);
                turnState.Plan?.Cancel($"Fallback replan #{turnState.FallbackReplanCount} ({errorType}: {result.Reason})");

                Main.Log($"[Orchestrator] {unitName}: Fallback replan #{turnState.FallbackReplanCount} triggered ({errorType}: {result.Reason}) - AP={currentAP:F1}");
                return ExecutionResult.Continue();  // → 다음 ProcessTurn에서 IsComplete=true → 새 계획 생성
            }

            // 모든 복구 경로 소진 → 턴 종료
            Main.LogWarning($"[Orchestrator] {unitName}: All recovery paths exhausted ({errorType}: {result.Reason}, FallbackReplans={turnState.FallbackReplanCount}) - ending turn");
            turnState.Plan?.Cancel("All recovery exhausted");
            return ExecutionResult.EndTurn($"Execution failed: {result.Reason}");
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

            // ★ v3.5.29: 전투 캐시 초기화 (거리/타겟팅)
            CombatCache.ClearAll();

            // ★ v3.8.15: AI 패스파인딩 캐시 초기화 (스터터링 방지)
            MovementAPI.InvalidateAiPathCache();

            // ★ v3.7.68: BattlefieldGrid 동적 확장 체크 (유닛이 경계 근처면 확장)
            try
            {
                var allUnits = Game.Instance?.TurnController?.AllUnits?
                    .OfType<BaseUnitEntity>()
                    .Where(u => u != null && u.IsInCombat)
                    .ToList();
                if (allUnits != null && allUnits.Count > 0)
                {
                    Analysis.BattlefieldGrid.Instance.ExpandIfNeeded(allUnits);
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[Orchestrator] BattlefieldGrid expand check failed: {ex.Message}");
            }

            // ★ v3.9.02: 우리 유닛 턴에서 게임 AI 타임아웃 확장
            // 기본 40초는 다수 액션(버프+공격+이동 반복) 시 부족할 수 있음
            // 모드 자체 안전장치(ConsecutiveFailures, MaxActions, FallbackReplans)로 무한루프 방지
            try
            {
                float currentTimeout = AiBrainController.SecondsAiTimeout;
                if (currentTimeout < 300f)
                {
                    _originalAiTimeout = currentTimeout;
                    AiBrainController.SecondsAiTimeout = 300f;
                    Main.LogDebug($"[Orchestrator] AI timeout extended: {currentTimeout}s → 300s");
                }
            }
            catch (Exception) { /* AiBrainController 접근 실패 시 무시 */ }

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
                // ★ v3.7.87: 턴 종료 시 행동 기록 (보너스 턴 대응)
                // 보너스 턴이 끝나면 기록 → 실제 턴에서 체크하여 턴 종료
                if (state.ActionCount > 0)
                {
                    TeamBlackboard.Instance.RecordUnitActed(unit);
                }

                Main.Log($"[Orchestrator] Turn ended for {unit.CharacterName}: {state}");
                _turnStates.Remove(unitId);
            }

            // ★ 턴 종료 상태 정리
            _pendingEndTurn.Remove(unitId);
            _pendingMoveDestinations.Remove(unitId);

            // ★ v3.9.02: 게임 AI 타임아웃 복원
            RestoreAiTimeout();
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

            // ★ v3.8.58: 아군 상태 캐시 정리
            AllyStateCache.Clear();

            // ★ v3.8.55: Raven support 사거리 캐시 정리
            GameInterface.FamiliarAPI.ClearRangeCache();

            // ★ v3.8.48: 리플렉션 캐시 정리
            GameInterface.CustomBehaviourTreePatch.ClearTreeCache();

            // ★ v3.8.48: Situation 풀 정리
            _analyzer.ClearPool();

            // ★ v3.9.02: AI 타임아웃 복원
            RestoreAiTimeout();
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
        /// ★ v3.9.02: 게임 AI 타임아웃 원래 값으로 복원
        /// </summary>
        private void RestoreAiTimeout()
        {
            if (_originalAiTimeout > 0)
            {
                try
                {
                    AiBrainController.SecondsAiTimeout = _originalAiTimeout;
                    Main.LogDebug($"[Orchestrator] AI timeout restored to {_originalAiTimeout}s");
                    _originalAiTimeout = -1f;
                }
                catch (Exception) { }
            }
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
