using System.Linq;
using Kingmaker;
using Kingmaker.Controllers.TurnBased;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Interfaces;
using Kingmaker.PubSubSystem;
using Kingmaker.PubSubSystem.Core;
using Kingmaker.PubSubSystem.Core.Interfaces;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Data;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// ★ v3.0.76: 게임의 턴 이벤트를 직접 구독
    /// AP 기반 추측 대신 게임 이벤트 활용
    /// </summary>
    public class TurnEventHandler : ITurnStartHandler, ITurnEndHandler, ITurnBasedModeHandler
    {
        private static TurnEventHandler _instance;
        private bool _isSubscribed = false;

        public static TurnEventHandler Instance => _instance ??= new TurnEventHandler();

        /// <summary>
        /// 이벤트 구독 시작
        /// </summary>
        public void Subscribe()
        {
            if (_isSubscribed) return;

            EventBus.Subscribe(this);
            _isSubscribed = true;
            Main.Log("[TurnEventHandler] Subscribed to turn events");
        }

        /// <summary>
        /// 이벤트 구독 해제
        /// </summary>
        public void Unsubscribe()
        {
            if (!_isSubscribed) return;

            EventBus.Unsubscribe(this);
            _isSubscribed = false;
            Main.Log("[TurnEventHandler] Unsubscribed from turn events");
        }

        /// <summary>
        /// ITurnStartHandler - 유닛 턴 시작 시 호출
        /// </summary>
        public void HandleUnitStartTurn(bool isTurnBased)
        {
            if (!isTurnBased) return;
            if (!Main.Enabled) return;

            var unit = EventInvokerExtensions.MechanicEntity as BaseUnitEntity;
            if (unit == null) return;

            // ★ v3.21.6: 함선 AI 위임 — ForceAIControl로 게임 네이티브 AI 활성화
            if (TurnOrchestrator.IsShipAIDelegated(unit))
            {
                TurnOrchestrator.ApplyShipForceAI(unit);
                return;  // CompanionAI는 개입하지 않음, 게임 네이티브 AI가 제어
            }

            // 우리가 제어하는 유닛인지 확인
            if (!TurnOrchestrator.Instance.ShouldControl(unit))
            {
                // ★ v3.82.0: 적/비제어 유닛 턴 → LLM Scorer pre-compute 시도
                // 다음 아군 턴을 위해 LLM 스코어링(가중치)을 미리 계산
                try { Planning.LLM.LLMPreCompute.TryStartPreCompute(); }
                catch (System.Exception ex) { Main.LogDebug($"[TurnEventHandler] PreCompute failed: {ex.Message}"); }
                return;
            }

            Main.LogDebug($"[TurnEventHandler] Turn started for {unit.CharacterName}");

            // TurnOrchestrator에 새 턴 시작 알림
            TurnOrchestrator.Instance.OnTurnStart(unit);

            // ★ v3.5.26: 턴 시작 시간 기록 (IsActingEnabled 구현용)
            CustomBehaviourTreePatch.RecordTurnStart(unit.UniqueId);

            // ★ v3.10.0: 디시전 노드 도달 추적 초기화
            CustomBehaviourTreePatch.ClearDecisionNodeTracking(unit.UniqueId);
        }

        /// <summary>
        /// ITurnEndHandler - 유닛 턴 종료 시 호출
        /// </summary>
        public void HandleUnitEndTurn(bool isTurnBased)
        {
            if (!isTurnBased) return;
            if (!Main.Enabled) return;

            var unit = EventInvokerExtensions.MechanicEntity as BaseUnitEntity;
            if (unit == null) return;

            Main.LogDebug($"[TurnEventHandler] Turn ended for {unit.CharacterName}");

            // ★ v3.21.6: 함선 ForceAIControl 해제
            TurnOrchestrator.RemoveShipForceAI(unit);

            // ★ v3.10.0: 디시전 노드 도달 여부 진단
            if (TurnOrchestrator.Instance.ShouldControl(unit) &&
                !CustomBehaviourTreePatch.WasDecisionNodeReached(unit.UniqueId))
            {
                Main.LogWarning($"[TurnEventHandler] ★ TURN SKIPPED: {unit.CharacterName} — " +
                    $"CompanionAIDecisionNode was NEVER reached this turn! " +
                    $"Possible cause: CanActInTurnBased=false, stun, or tree failure. " +
                    $"CanAct={unit.State?.CanActInTurnBased}, Commands.Empty={unit.Commands?.Empty}");
            }

            // TurnOrchestrator에 턴 종료 알림
            TurnOrchestrator.Instance.OnTurnEnd(unit);

            // ★ v3.48.0: Tactical Narrator 오버레이 숨김
            Diagnostics.TacticalNarrator.OnTurnEnd();

            // ★ v3.76.0: LLM Combat Panel 숨김
            UI.LLMCombatPanel.Hide();

            // ★ v3.109.0: LLM 시각 오버레이 클리어
            UI.LLMVisualOverlay.Clear();

            // ★ v3.5.26: 턴 시작 시간 정리
            CustomBehaviourTreePatch.ClearTurnStart(unit.UniqueId);
        }

        /// <summary>
        /// ITurnBasedModeHandler - 턴제 모드 전환 시 호출
        /// </summary>
        public void HandleTurnBasedModeSwitched(bool isTurnBased)
        {
            if (!Main.Enabled) return;

            if (!isTurnBased)
            {
                Main.Log("[TurnEventHandler] Combat ended");

                // ★ v3.9.80: 승리 환호 — OnCombatEnd() 전에 호출 (ClearAll 이전에 캐릭터 식별 필요)
                try
                {
                    var party = Game.Instance?.Player?.PartyAndPets;
                    if (party != null)
                    {
                        var conscious = new System.Collections.Generic.List<BaseUnitEntity>();
                        foreach (var u in party)
                            if (u?.LifeState?.IsConscious == true) conscious.Add(u);

                        if (conscious.Count > 0)
                            CompanionDialogue.AnnounceVictory(conscious);
                    }
                }
                catch (System.Exception ex)
                {
                    Main.LogDebug($"[TurnEventHandler] Victory bark error: {ex.Message}");
                }

                TurnOrchestrator.Instance.OnCombatEnd();

                // ★ v3.48.0: Tactical Narrator 전투 종료 정리
                Diagnostics.TacticalNarrator.OnCombatEnd();

                // ★ v3.76.0: LLM Combat Panel 정리
                UI.LLMCombatPanel.Reset();

                // ★ v3.1.19: 패턴 캐시 클리어
                CombatAPI.ClearPatternCache();

                // ★ v3.7.62: BattlefieldGrid 정리
                BattlefieldGrid.Instance.Clear();

                // ★ v3.9.42: 접근 경로 캐시 클리어
                MovementAPI.ClearApproachPathCache();
            }
            else
            {
                Main.Log("[TurnEventHandler] Combat started");

                // ★ v3.111.19 Phase D.5: Defense-in-depth — combat end cleanup이 누락되는 경로
                //   (게임 crash, save/load, 비정상 종료 등)에서 stale cache 방지.
                //   정상 흐름에서는 OnCombatEnd → ClearPool에서 이미 Clear됨.
                EnemyMoveCache.Clear();

                // ★ v3.82.0: LLM Scorer Cache + Pre-compute 초기화
                Planning.LLM.LLMScorerCache.Clear();
                Planning.LLM.LLMPreCompute.Clear();

                // ★ v3.2.10: TeamBlackboard 초기화
                TeamBlackboard.Instance.InitializeCombat();

                // ★ v3.7.62: BattlefieldGrid 초기화 - 전장 맵 구조 캐싱
                try
                {
                    var allUnits = Game.Instance?.TurnController?.AllUnits?
                        .OfType<BaseUnitEntity>()
                        .ToList() ?? new System.Collections.Generic.List<BaseUnitEntity>();
                    BattlefieldGrid.Instance.InitializeFromCombat(allUnits);
                }
                catch (System.Exception ex)
                {
                    Main.LogError($"[TurnEventHandler] BattlefieldGrid init failed: {ex.Message}");
                }
            }
        }

    }
}
