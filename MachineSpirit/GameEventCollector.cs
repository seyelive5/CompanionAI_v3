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
using Kingmaker.DialogSystem.Blueprints;
using Kingmaker.AreaLogic.QuestSystem;
using Kingmaker.UnitLogic.Alignments;
using Kingmaker.UnitLogic.Levelup.Obsolete;
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
        AreaTransition,
        PlayerChoice,    // ★ v3.68.0: Player dialogue answer selection
        SoulMarkShift,   // ★ v3.68.0: Conviction/alignment change
        QuestUpdate,     // ★ v3.68.0: Quest started/completed/failed
        WarpTravel,      // ★ v3.68.0: Warp travel events
        LevelUp,         // ★ v3.68.0: Character level up
        DialogueStart,   // ★ v3.70.0: Dialogue scene started
        DialogueEnd      // ★ v3.70.0: Dialogue scene ended
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
            if (Type == GameEventType.PlayerChoice)
                return $"Decision — Player chose: \"{Text}\"";
            if (Type == GameEventType.SoulMarkShift)
                return $"Conviction — {Text}";
            if (Type == GameEventType.QuestUpdate)
                return $"Mission — {Text}";
            if (Type == GameEventType.WarpTravel)
                return $"Navigation — {Text}";
            if (Type == GameEventType.LevelUp)
                return $"Advancement — {Text}";
            if (Type == GameEventType.DialogueStart)
                return $"Dialogue — {Text}";
            if (Type == GameEventType.DialogueEnd)
                return "Dialogue — Scene concluded";
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
        private const int MAX_EVENTS = 128; // ★ v3.68.0: expanded from 50
        private static readonly List<GameEvent> _events = new List<GameEvent>(MAX_EVENTS + 5);

        // ★ v3.68.0: Separate dialogue buffer — keeps full NPC conversation context
        private const int MAX_DIALOGUE = 30;
        private static readonly List<GameEvent> _dialogueBuffer = new List<GameEvent>(MAX_DIALOGUE + 5);
        public static IReadOnlyList<GameEvent> DialogueBuffer => _dialogueBuffer;

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
            // ★ v3.70.0: Dialogue lines only go to buffer (smart timing triggers LLM at scene start/end)
            else if (type == GameEventType.Dialogue)
            {
                if (_dialogueBuffer.Count >= MAX_DIALOGUE)
                    _dialogueBuffer.RemoveAt(0);
                _dialogueBuffer.Add(_events[_events.Count - 1]);
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
            IAreaHandler,
            ISelectAnswerHandler,       // ★ v3.68.0
            ISoulMarkShiftHandler,      // ★ v3.68.0
            IQuestHandler,              // ★ v3.68.0
            ILevelUpInitiateUIHandler,  // ★ v3.68.0
            IDialogStartHandler,        // ★ v3.70.0
            IDialogFinishHandler        // ★ v3.70.0
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

                    if (text.Length > 200)
                        text = text.Substring(0, 200) + "...";

                    // ★ v3.70.0: Only log + buffer, don't trigger LLM reaction (smart timing handles this)
                    AddEvent(GameEventType.Dialogue, speaker, text);
                    // Removed: MachineSpirit.OnDialogueEvent() — reactions now happen at scene start/end only
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

                    _dialogueBuffer.Clear(); // ★ v3.68.0: Reset dialogue context on area change
                    AddEvent(GameEventType.AreaTransition, null, $"Entered {areaName}");
                }
                catch { /* safe fallback */ }
            }

            // ★ v3.68.0: Player dialogue choice
            public void HandleSelectAnswer(BlueprintAnswer answer)
            {
                if (!MachineSpirit.IsActive) return;
                try
                {
                    string text = answer?.DisplayText;
                    if (string.IsNullOrEmpty(text)) return;
                    if (text.Length > 200) text = text.Substring(0, 200) + "...";

                    AddEvent(GameEventType.PlayerChoice, "Lord Captain", text);

                    // Also add to dialogue buffer for transcript context
                    if (_dialogueBuffer.Count >= MAX_DIALOGUE)
                        _dialogueBuffer.RemoveAt(0);
                    _dialogueBuffer.Add(new GameEvent
                    {
                        Type = GameEventType.PlayerChoice,
                        Speaker = "Lord Captain",
                        Text = text,
                        Timestamp = UnityEngine.Time.time
                    });

                    EventCoalescer.Enqueue(_events[_events.Count - 1]);
                }
                catch { }
            }

            // ★ v3.68.0: Soul mark / conviction shift
            public void HandleSoulMarkShift(ISoulMarkShiftProvider provider)
            {
                if (!MachineSpirit.IsActive) return;
                try
                {
                    var shift = provider?.SoulMarkShift;
                    if (shift.Direction == SoulMarkDirection.None) return;

                    string direction = shift.Direction.ToString();
                    string desc = $"Conviction shifted toward {direction}";

                    AddEvent(GameEventType.SoulMarkShift, null, desc);
                    EventCoalescer.Enqueue(_events[_events.Count - 1]);
                }
                catch { }
            }

            // ★ v3.68.0: Quest lifecycle
            public void HandleQuestStarted(Quest quest)
            {
                if (!MachineSpirit.IsActive) return;
                try
                {
                    string name = quest?.Blueprint?.name ?? "Unknown";
                    AddEvent(GameEventType.QuestUpdate, null, $"New quest: {name}");
                    EventCoalescer.Enqueue(_events[_events.Count - 1]);
                }
                catch { }
            }

            public void HandleQuestCompleted(Quest quest)
            {
                if (!MachineSpirit.IsActive) return;
                try
                {
                    string name = quest?.Blueprint?.name ?? "Unknown";
                    AddEvent(GameEventType.QuestUpdate, null, $"Quest completed: {name}");
                    EventCoalescer.Enqueue(_events[_events.Count - 1]);
                }
                catch { }
            }

            public void HandleQuestFailed(Quest quest)
            {
                if (!MachineSpirit.IsActive) return;
                try
                {
                    string name = quest?.Blueprint?.name ?? "Unknown";
                    AddEvent(GameEventType.QuestUpdate, null, $"Quest FAILED: {name}");
                    EventCoalescer.Enqueue(_events[_events.Count - 1]);
                }
                catch { }
            }

            public void HandleQuestUpdated(Quest quest)
            {
                // Silent — too frequent for LLM calls, just log in sensor
                if (!MachineSpirit.IsActive) return;
                try
                {
                    string name = quest?.Blueprint?.name ?? "Unknown";
                    AddEvent(GameEventType.QuestUpdate, null, $"Quest updated: {name}");
                }
                catch { }
            }

            // ★ v3.68.0: Level up detection (ILevelUpInitiateUIHandler — global subscriber)
            public void HandleLevelUpStart(BaseUnitEntity unit, Action onCommit = null, Action onStop = null, LevelUpState.CharBuildMode mode = LevelUpState.CharBuildMode.LevelUp)
            {
                if (!MachineSpirit.IsActive) return;
                if (mode != LevelUpState.CharBuildMode.LevelUp) return; // Skip chargen
                try
                {
                    string unitName = unit?.CharacterName ?? "Unknown";
                    AddEvent(GameEventType.LevelUp, null, $"{unitName} reached a new level");
                    EventCoalescer.Enqueue(_events[_events.Count - 1]);
                }
                catch { }
            }

            // ★ v3.70.0: Dialogue scene start/end — smart timing for LLM reactions
            public void HandleDialogStarted(BlueprintDialog dialog)
            {
                if (!MachineSpirit.IsActive) return;
                try
                {
                    string dialogName = dialog?.name ?? "Unknown";
                    AddEvent(GameEventType.DialogueStart, null, $"Dialogue started: {dialogName}");
                    MachineSpirit.OnDialogueStarted();
                }
                catch { }
            }

            public void HandleDialogFinished(BlueprintDialog dialog, bool success)
            {
                if (!MachineSpirit.IsActive) return;
                try
                {
                    AddEvent(GameEventType.DialogueEnd, null, "Dialogue ended");
                    MachineSpirit.OnDialogueEnded();
                }
                catch { }
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
