using System;
using System.Collections.Generic;
using HarmonyLib;
using Kingmaker.Code.UI.MVVM.VM.Bark;
using Kingmaker.Controllers.TurnBased;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Entities.Base;
using Kingmaker.Mechanics.Entities;
using Kingmaker.PubSubSystem;
using Kingmaker.PubSubSystem.Core;
using UnityEngine;

namespace CompanionAI_v3.MachineSpirit
{
    public enum GameEventType
    {
        Bark,
        Dialogue,
        CombatStart,
        CombatEnd,
        UnitDeath,
        TurnPlanSummary
    }

    public struct GameEvent
    {
        public GameEventType Type;
        public string Speaker;
        public string Text;
        public float Timestamp;

        public override string ToString() =>
            string.IsNullOrEmpty(Speaker)
                ? $"[{Type}] {Text}"
                : $"[{Type}] {Speaker}: {Text}";
    }

    /// <summary>
    /// ★ MachineSpirit: Collects game events (barks, combat start/end, unit death)
    /// into a ring buffer for LLM context.
    /// </summary>
    public static class GameEventCollector
    {
        private const int MAX_EVENTS = 30;
        private static readonly List<GameEvent> _events = new List<GameEvent>(MAX_EVENTS + 5);
        private static bool _subscribed;

        public static IReadOnlyList<GameEvent> RecentEvents => _events;

        public static void AddEvent(GameEventType type, string speaker, string text)
        {
            if (_events.Count >= MAX_EVENTS)
                _events.RemoveAt(0);

            _events.Add(new GameEvent
            {
                Type = type,
                Speaker = speaker ?? "",
                Text = text ?? "",
                Timestamp = Time.time
            });

            // TODO: Task 6 — Notify MachineSpirit of major events for spontaneous speech
            // if (type == GameEventType.CombatStart ||
            //     type == GameEventType.CombatEnd ||
            //     type == GameEventType.UnitDeath)
            // {
            //     MachineSpirit.OnMajorEvent(_events[_events.Count - 1]);
            // }
        }

        public static void AddTurnPlanSummary(string unitName, string summary)
        {
            AddEvent(GameEventType.TurnPlanSummary, unitName, summary);
        }

        public static void Clear() => _events.Clear();

        // ── EventBus subscriber ──
        private static CombatEventSubscriber _subscriber;

        public static void Subscribe()
        {
            if (_subscribed) return;
            _subscriber = new CombatEventSubscriber();
            EventBus.Subscribe(_subscriber);
            _subscribed = true;
        }

        public static void Unsubscribe()
        {
            if (!_subscribed) return;
            EventBus.Unsubscribe(_subscriber);
            _subscriber = null;
            _subscribed = false;
        }

        private class CombatEventSubscriber :
            IUnitDeathHandler,
            ITurnBasedModeHandler
        {
            public void HandleUnitDeath(AbstractUnitEntity unit)
            {
                if (unit == null) return;
                string name = unit.CharacterName ?? "Unknown";
                bool isEnemy = !unit.IsPlayerFaction;
                string desc = isEnemy ? $"{name} was destroyed" : $"{name} has fallen";
                AddEvent(GameEventType.UnitDeath, null, desc);
            }

            public void HandleTurnBasedModeSwitched(bool isTurnBased)
            {
                if (isTurnBased)
                    AddEvent(GameEventType.CombatStart, null, "Combat initiated");
                else
                    AddEvent(GameEventType.CombatEnd, null, "Combat concluded");
            }
        }
    }

    // ── Harmony Patch for BarkPlayer ──
    [HarmonyPatch(typeof(BarkPlayer), nameof(BarkPlayer.Bark),
        new Type[] { typeof(Entity), typeof(string), typeof(float), typeof(string),
                     typeof(BaseUnitEntity), typeof(bool), typeof(string), typeof(Color) })]
    public static class BarkPlayerPatch
    {
        [HarmonyPrefix]
        public static void Prefix(Entity entity, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            // TODO: Task 6 — gate on MachineSpirit.IsActive
            // if (!MachineSpirit.IsActive) return;

            string speaker = "Unknown";
            try
            {
                if (entity is BaseUnitEntity bue)
                    speaker = bue.CharacterName ?? "Unknown";
            }
            catch { /* safe fallback */ }

            GameEventCollector.AddEvent(GameEventType.Bark, speaker, text);
        }
    }
}
