using Kingmaker.Controllers.TurnBased;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Interfaces;
using Kingmaker.PubSubSystem;
using Kingmaker.PubSubSystem.Core;
using Kingmaker.PubSubSystem.Core.Interfaces;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;

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

            // 우리가 제어하는 유닛인지 확인
            if (!TurnOrchestrator.Instance.ShouldControl(unit)) return;

            Main.LogDebug($"[TurnEventHandler] Turn started for {unit.CharacterName}");

            // TurnOrchestrator에 새 턴 시작 알림
            TurnOrchestrator.Instance.OnTurnStart(unit);

            // ★ v3.5.26: 턴 시작 시간 기록 (IsActingEnabled 구현용)
            CustomBehaviourTreePatch.RecordTurnStart(unit.UniqueId);
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

            // TurnOrchestrator에 턴 종료 알림
            TurnOrchestrator.Instance.OnTurnEnd(unit);

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
                TurnOrchestrator.Instance.OnCombatEnd();

                // ★ v3.1.19: 패턴 캐시 클리어
                CombatAPI.ClearPatternCache();
            }
            else
            {
                Main.Log("[TurnEventHandler] Combat started");

                // ★ v3.2.10: TeamBlackboard 초기화
                TeamBlackboard.Instance.InitializeCombat();
            }
        }

    }
}
