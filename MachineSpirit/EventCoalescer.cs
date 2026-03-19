// MachineSpirit/EventCoalescer.cs
// ★ v3.68.0: Batches simultaneous game events into a single LLM call
using System.Collections.Generic;
using UnityEngine;

namespace CompanionAI_v3.MachineSpirit
{
    /// <summary>
    /// Batches simultaneous game events into a single LLM call.
    /// 5-second coalescing window — first event starts timer, others accumulate.
    /// After window expires, all pending events are sent to MachineSpirit.OnMergedEvents().
    /// </summary>
    public static class EventCoalescer
    {
        private const float COALESCE_WINDOW = 5f;

        private static readonly List<GameEvent> _pending = new List<GameEvent>();
        private static float _firstEventTime;
        private static bool _hasEvents;

        public static void Enqueue(GameEvent evt)
        {
            if (!_hasEvents)
            {
                _firstEventTime = Time.time;
                _hasEvents = true;
            }
            _pending.Add(evt);
        }

        /// <summary>
        /// Called from MachineSpirit.Update(). Flushes after 5s window.
        /// </summary>
        public static void Update()
        {
            if (!_hasEvents) return;
            if (Time.time - _firstEventTime < COALESCE_WINDOW) return;
            if (LLMClient.IsRequesting) return; // Wait for current request

            var batch = new List<GameEvent>(_pending);
            _pending.Clear();
            _hasEvents = false;

            if (batch.Count > 0)
                MachineSpirit.OnMergedEvents(batch);
        }

        public static void Clear()
        {
            _pending.Clear();
            _hasEvents = false;
        }
    }
}
