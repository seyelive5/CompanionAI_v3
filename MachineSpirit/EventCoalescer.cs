// MachineSpirit/EventCoalescer.cs
// ★ v3.68.0: Merge simultaneous events into single LLM call
using System.Collections.Generic;
using UnityEngine;

namespace CompanionAI_v3.MachineSpirit
{
    /// <summary>
    /// Collects game events and batches those arriving within 5 seconds
    /// into a single merged event list for one LLM call.
    /// </summary>
    public static class EventCoalescer
    {
        private const float MERGE_WINDOW = 5f;

        private static readonly List<GameEvent> _pendingEvents = new List<GameEvent>();
        private static float _firstEventTime;
        private static bool _hasPending;

        /// <summary>
        /// Add an event to the pending batch. Starts 5s timer on first event.
        /// </summary>
        public static void Enqueue(GameEvent evt)
        {
            if (!_hasPending)
            {
                _firstEventTime = Time.time;
                _hasPending = true;
            }
            _pendingEvents.Add(evt);
        }

        /// <summary>
        /// Check if the merge window has elapsed and return the batch.
        /// Call this from MachineSpirit.Update() each frame.
        /// Returns null if no events are ready.
        /// </summary>
        public static List<GameEvent> TryFlush()
        {
            if (!_hasPending) return null;
            if (Time.time - _firstEventTime < MERGE_WINDOW) return null;

            var batch = new List<GameEvent>(_pendingEvents);
            _pendingEvents.Clear();
            _hasPending = false;
            return batch;
        }

        /// <summary>
        /// Check if there are pending events (for UI "thinking" indicator).
        /// </summary>
        public static bool HasPending => _hasPending;

        /// <summary>
        /// Clear all pending events (e.g., on combat end or area transition).
        /// </summary>
        public static void Clear()
        {
            _pendingEvents.Clear();
            _hasPending = false;
        }
    }
}
