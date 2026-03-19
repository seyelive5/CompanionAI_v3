using System;
using System.Collections.Generic;
using HarmonyLib;
using Kingmaker.Code.UI.MVVM.VM.Bark;
using Kingmaker.Controllers.Dialog;
using Kingmaker.Controllers.TurnBased;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Entities.Base;
using Kingmaker.Mechanics.Entities;
using Kingmaker.PubSubSystem;
using Kingmaker.PubSubSystem.Core;
using Kingmaker.RuleSystem.Rules.Damage;
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
        TurnPlanSummary,
        DamageDealt,
        HealingDone,
        RoundStart,
        VisionObservation,
        AreaTransition  // ★ v3.66.0
    }

    public struct GameEvent
    {
        public GameEventType Type;
        public string Speaker;
        public string Text;
        public float Timestamp;

        public override string ToString()
        {
            // Format events as sensor observations, NOT as dialogue transcripts.
            // Prevents LLMs from copying the format and generating character dialogue.
            if (Type == GameEventType.Dialogue && !string.IsNullOrEmpty(Speaker))
                return $"Cogitator log — {Speaker} said: \"{Text}\"";
            if (Type == GameEventType.Bark && !string.IsNullOrEmpty(Speaker))
                return $"Vox intercept — {Speaker} spoke: \"{Text}\"";
            if (Type == GameEventType.TurnPlanSummary && !string.IsNullOrEmpty(Speaker))
                return $"Tactical cogitator — {Speaker}: {Text}";
            if (Type == GameEventType.DamageDealt)
                return $"Weapon array — {Text}";
            if (Type == GameEventType.HealingDone)
                return $"Medicae bay — {Text}";
            if (Type == GameEventType.RoundStart)
                return $"Chrono — {Text}";
            if (Type == GameEventType.VisionObservation)
                return $"Pict-capture — {Text}";
            if (Type == GameEventType.AreaTransition)
                return $"Navigation — {Text}";
            if (string.IsNullOrEmpty(Speaker))
                return $"Sensor: {Text}";
            return $"Sensor — {Speaker}: {Text}";
        }
    }

    /// <summary>
    /// ★ MachineSpirit: Collects game events (barks, combat start/end, unit death)
    /// into a ring buffer for LLM context.
    /// </summary>
    public static class GameEventCollector
    {
        private const int MAX_EVENTS = 50;
        private static readonly List<GameEvent> _events = new List<GameEvent>(MAX_EVENTS + 5);

        // ★ v3.64.0: Kill tracker per combat encounter
        private static readonly Dictionary<string, int> _killCounts = new Dictionary<string, int>();
        public static IReadOnlyDictionary<string, int> KillCounts => _killCounts;

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

            // ★ v3.52.0: Notify MachineSpirit of major events for spontaneous speech
            if (type == GameEventType.CombatStart ||
                type == GameEventType.CombatEnd ||
                type == GameEventType.UnitDeath)
            {
                MachineSpirit.OnMajorEvent(_events[_events.Count - 1]);
            }
            // ★ v3.66.0: Dialogue triggers Machine Spirit reaction (separate cooldown)
            else if (type == GameEventType.Dialogue)
            {
                MachineSpirit.OnDialogueEvent(_events[_events.Count - 1]);
            }
            // ★ v3.66.0: Area transition triggers Machine Spirit scan
            else if (type == GameEventType.AreaTransition)
            {
                MachineSpirit.OnAreaTransition(_events[_events.Count - 1]);
            }
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

        private static int _combatRound;
        private static string _lastAreaName;

        private class CombatEventSubscriber :
            IUnitDeathHandler,
            ITurnBasedModeHandler,
            IDialogCueHandler,
            IDamageHandler,
            IHealingHandler,
            IRoundStartHandler,
            IAreaHandler  // ★ v3.66.0
        {
            public void HandleUnitDeath(AbstractUnitEntity unit)
            {
                if (unit == null) return;
                string name = unit.CharacterName ?? "Unknown";
                bool isEnemy = !unit.IsPlayerFaction;
                string desc = isEnemy ? $"{name} was destroyed" : $"{name} has fallen";
                AddEvent(GameEventType.UnitDeath, null, desc);

                // ★ v3.64.0: Track kills by party members
                if (isEnemy)
                {
                    for (int i = _events.Count - 1; i >= Math.Max(0, _events.Count - 10); i--)
                    {
                        var evt = _events[i];
                        if (evt.Type == GameEventType.DamageDealt && evt.Text.Contains(name))
                        {
                            string killer = evt.Speaker;
                            if (!string.IsNullOrEmpty(killer) && killer != "Unknown")
                            {
                                _killCounts.TryGetValue(killer, out int count);
                                _killCounts[killer] = count + 1;
                            }
                            break;
                        }
                    }
                }
            }

            public void HandleTurnBasedModeSwitched(bool isTurnBased)
            {
                if (isTurnBased)
                {
                    _combatRound = 0;
                    _killCounts.Clear();  // ★ v3.64.0
                    AddEvent(GameEventType.CombatStart, null, "Combat initiated");
                }
                else
                {
                    AddEvent(GameEventType.CombatEnd, null, "Combat concluded");
                }
            }

            public void HandleOnCueShow(CueShowData cueShowData)
            {
                if (!MachineSpirit.IsActive) return;
                if (cueShowData?.Cue == null) return;

                try
                {
                    string text = cueShowData.Cue.DisplayText;
                    if (string.IsNullOrEmpty(text)) return;

                    string speaker = "Unknown";
                    try
                    {
                        speaker = cueShowData.Cue.Speaker?.Blueprint?.CharacterName ?? "Unknown";
                    }
                    catch { /* safe fallback */ }

                    // Truncate long dialogue to save tokens (keep first ~120 chars)
                    if (text.Length > 120)
                        text = text.Substring(0, 120) + "...";

                    AddEvent(GameEventType.Dialogue, speaker, text);
                }
                catch { /* safe fallback */ }
            }

            // ★ v3.58.0: Combat detail events for Machine Spirit context

            public void HandleDamageDealt(RuleDealDamage dealDamage)
            {
                if (!MachineSpirit.IsActive) return;
                if (dealDamage == null) return;
                if (dealDamage.IsDot || dealDamage.IsCollisionDamage) return;

                try
                {
                    var attacker = dealDamage.Initiator as BaseUnitEntity;
                    var target = dealDamage.Target as BaseUnitEntity;
                    if (attacker == null || target == null) return;

                    int damage = dealDamage.Result;
                    if (damage <= 0) return;

                    string attackerName = attacker.CharacterName ?? "Unknown";
                    string targetName = target.CharacterName ?? "Unknown";

                    // Only track significant damage (>15% of target max HP) or killing blows
                    int maxHp = 1;
                    try { maxHp = System.Math.Max(1, target.Health.MaxHitPoints); } catch { }
                    float damagePercent = damage / (float)maxHp;
                    bool isKill = dealDamage.HPBeforeDamage > 0 && dealDamage.HPBeforeDamage <= damage;

                    if (damagePercent < 0.15f && !isKill) return;

                    string desc;
                    if (isKill)
                        desc = $"{attackerName} destroyed {targetName} ({damage} damage, killing blow)";
                    else
                        desc = $"{attackerName} dealt {damage} damage to {targetName} ({damagePercent:P0} HP)";

                    AddEvent(GameEventType.DamageDealt, attackerName, desc);

                    // Spontaneous trigger when party member takes massive damage (>30% HP)
                    if (target.IsPlayerFaction && damagePercent >= 0.3f)
                    {
                        MachineSpirit.OnMajorEvent(_events[_events.Count - 1]);
                    }
                }
                catch { /* safe fallback */ }
            }

            public void HandleHealing(RuleHealDamage healDamage)
            {
                if (!MachineSpirit.IsActive) return;
                if (healDamage == null) return;

                try
                {
                    var healer = healDamage.Initiator as BaseUnitEntity;
                    var target = healDamage.Target as BaseUnitEntity;
                    if (healer == null || target == null) return;

                    int value = healDamage.Value;
                    if (value <= 0) return;
                    if (!target.IsPlayerFaction) return; // Only track party healing

                    string healerName = healer.CharacterName ?? "Unknown";
                    string targetName = target.CharacterName ?? "Unknown";

                    AddEvent(GameEventType.HealingDone, healerName, $"{healerName} healed {targetName} for {value} HP");
                }
                catch { /* safe fallback */ }
            }

            public void HandleRoundStart(bool isTurnBased)
            {
                if (!MachineSpirit.IsActive) return;
                if (!isTurnBased) return;

                _combatRound++;
                AddEvent(GameEventType.RoundStart, null, $"Combat round {_combatRound}");
            }

            // ★ v3.66.0: IAreaHandler — detect area transitions
            public void OnAreaBeginUnloading() { }

            public void OnAreaDidLoad()
            {
                if (!MachineSpirit.IsActive) return;

                try
                {
                    string areaName = Kingmaker.Game.Instance?.CurrentlyLoadedArea?.AreaDisplayName;
                    if (string.IsNullOrEmpty(areaName)) return;

                    // Skip if same area (save load, etc.)
                    if (areaName == _lastAreaName) return;
                    _lastAreaName = areaName;

                    AddEvent(GameEventType.AreaTransition, null, $"Entered {areaName}");
                }
                catch { /* safe fallback */ }
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
            // ★ v3.52.0: Gate bark collection on MachineSpirit being active
            if (!MachineSpirit.IsActive) return;

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
