// MachineSpirit/GameKnowledge.cs
// ★ v3.68.0: Blueprint-based game knowledge for Machine Spirit context
using System;
using System.Collections.Generic;
using System.Text;
using Kingmaker;
using Kingmaker.AreaLogic.QuestSystem;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Items.Weapons;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities.Blueprints;

namespace CompanionAI_v3.MachineSpirit
{
    /// <summary>
    /// Static utility that reads game Blueprint data at runtime to provide
    /// knowledge context (enemy info, weapon stats, quest state) for the
    /// Machine Spirit LLM prompt.  Every public method is wrapped in
    /// try/catch — this runs during gameplay and must never crash.
    /// </summary>
    public static class GameKnowledge
    {
        // -----------------------------------------------------------------
        //  Unit info
        // -----------------------------------------------------------------

        /// <summary>
        /// Get info about a unit from its blueprint.
        /// Returns formatted string like "Sslyth Warrior: [description]"
        /// </summary>
        public static string GetUnitInfo(BaseUnitEntity unit)
        {
            if (unit == null) return null;
            try
            {
                var bp = unit.Blueprint;
                if (bp == null) return null;

                var sb = new StringBuilder();
                string name = unit.CharacterName;
                sb.Append(name);

                // BlueprintUnit → BlueprintUnitFact → BlueprintMechanicEntityFact.Description
                try
                {
                    string desc = bp.Description;
                    if (!string.IsNullOrEmpty(desc))
                    {
                        if (desc.Length > 200) desc = desc.Substring(0, 200) + "...";
                        sb.Append(": ").Append(desc);
                    }
                }
                catch { /* description may be unset */ }

                return sb.ToString();
            }
            catch (System.Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[GameKnowledge] GetUnitInfo silent: {ex.Message}");
                return null;
            }
        }

        // -----------------------------------------------------------------
        //  Weapon info
        // -----------------------------------------------------------------

        /// <summary>
        /// Get weapon stats from its blueprint.
        /// Returns formatted string like "Bolter: ranged, 18-24 dmg (Piercing), pen 4, range 12"
        /// </summary>
        public static string GetWeaponInfo(BlueprintItemWeapon bp)
        {
            if (bp == null) return null;
            try
            {
                var sb = new StringBuilder();
                sb.Append(bp.name ?? "weapon");

                // Melee / Ranged
                try
                {
                    sb.Append(bp.IsMelee ? ": melee" : ": ranged");
                }
                catch { }

                // Damage: WarhammerDamage–WarhammerMaxDamage
                try
                {
                    int min = bp.WarhammerDamage;
                    int max = bp.WarhammerMaxDamage;
                    if (max > 0)
                        sb.Append($", {min}-{max} dmg");
                    else if (min > 0)
                        sb.Append($", {min} dmg");
                }
                catch { }

                // Damage type (DamageTypeDescription.Type → DamageType enum)
                try
                {
                    var dt = bp.DamageType;
                    if (dt != null)
                        sb.Append($" ({dt.Type})");
                }
                catch { }

                // Penetration
                try
                {
                    int pen = bp.WarhammerPenetration;
                    if (pen > 0)
                        sb.Append($", pen {pen}");
                }
                catch { }

                // Range (WarhammerMaxDistance via AttackRange property)
                try
                {
                    int range = bp.AttackRange;
                    if (range > 0)
                        sb.Append($", range {range}");
                }
                catch { }

                // Rate of Fire
                try
                {
                    int rof = bp.RateOfFire;
                    if (rof > 1)
                        sb.Append($", RoF {rof}");
                }
                catch { }

                return sb.ToString();
            }
            catch { return null; }
        }

        // -----------------------------------------------------------------
        //  Ability info
        // -----------------------------------------------------------------

        /// <summary>
        /// Get ability info from blueprint.
        /// Returns formatted string like "Melta Shot: [description] (range: 8)"
        /// </summary>
        public static string GetAbilityInfo(BlueprintAbility ability)
        {
            if (ability == null) return null;
            try
            {
                var sb = new StringBuilder();
                sb.Append(ability.name ?? "ability");

                // Description — BlueprintAbility.RawDescription → base.Description
                try
                {
                    string desc = ability.Description;
                    if (!string.IsNullOrEmpty(desc))
                    {
                        if (desc.Length > 150) desc = desc.Substring(0, 150) + "...";
                        sb.Append(": ").Append(desc);
                    }
                }
                catch { }

                // Range
                try
                {
                    if (ability.Range != AbilityRange.Personal)
                        sb.Append($" (range: {ability.Range})");
                }
                catch { }

                return sb.ToString();
            }
            catch { return null; }
        }

        // -----------------------------------------------------------------
        //  Tactical intel (combat)
        // -----------------------------------------------------------------

        /// <summary>
        /// Build tactical intel for current combat — enemies + party weapons.
        /// Returns null if not in combat or no data available.
        /// </summary>
        public static string BuildTacticalIntel()
        {
            try
            {
                bool inCombat = false;
                try { inCombat = Game.Instance?.Player?.IsInCombat ?? false; } catch { }
                if (!inCombat) return null;

                var sb = new StringBuilder();
                sb.AppendLine("[TACTICAL INTEL]");

                // --- Enemies in combat ---
                try
                {
                    sb.AppendLine("Enemies:");
                    // TurnController.AllUnits → IEnumerable<MechanicEntity>
                    var allUnits = Game.Instance?.TurnController?.AllUnits;
                    if (allUnits != null)
                    {
                        int enemyCount = 0;
                        foreach (var entity in allUnits)
                        {
                            var unit = entity as BaseUnitEntity;
                            if (unit == null) continue;
                            try
                            {
                                // MechanicEntity.IsPlayerFaction (PartFaction.IsPlayer)
                                if (unit.IsPlayerFaction) continue;
                                // PartLifeState.IsDead
                                if (unit.LifeState.IsDead) continue;
                            }
                            catch { continue; }

                            string info = GetUnitInfo(unit);
                            if (!string.IsNullOrEmpty(info))
                            {
                                sb.Append("- ").AppendLine(info);
                                if (++enemyCount >= 5) break; // top 5 to keep prompt concise
                            }
                        }
                    }
                }
                catch { }

                // --- Party weapons ---
                try
                {
                    sb.AppendLine("Party Equipment:");
                    var party = Game.Instance?.Player?.PartyAndPets;
                    if (party != null)
                    {
                        foreach (var unit in party)
                        {
                            if (unit == null) continue;
                            try
                            {
                                if (unit.LifeState.IsDead) continue;
                                if (unit.IsPet) continue;
                            }
                            catch { continue; }

                            try
                            {
                                string name = unit.CharacterName;
                                // Body.PrimaryHand.MaybeWeapon → ItemEntityWeapon
                                var primary = unit.Body?.PrimaryHand?.MaybeWeapon;
                                if (primary != null)
                                {
                                    var weaponBp = primary.Blueprint as BlueprintItemWeapon;
                                    string weaponInfo = GetWeaponInfo(weaponBp);
                                    if (!string.IsNullOrEmpty(weaponInfo))
                                        sb.Append("- ").Append(name).Append(": ").AppendLine(weaponInfo);
                                    else
                                        sb.Append("- ").Append(name).Append(": ").AppendLine(primary.Name ?? "unknown");
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                string result = sb.ToString();
                return result.Length > 40 ? result : null;
            }
            catch { return null; }
        }

        // -----------------------------------------------------------------
        //  Location intel (exploration)
        // -----------------------------------------------------------------

        /// <summary>
        /// Build location intel for exploration — area name + active quest objectives.
        /// Returns null if in combat or no meaningful data.
        /// </summary>
        public static string BuildLocationIntel()
        {
            try
            {
                bool inCombat = false;
                try { inCombat = Game.Instance?.Player?.IsInCombat ?? false; } catch { }
                if (inCombat) return null; // combat has its own intel

                var sb = new StringBuilder();
                sb.AppendLine("[LOCATION INTEL]");

                // Current area — Game.Instance.CurrentlyLoadedArea.AreaDisplayName
                try
                {
                    var area = Game.Instance?.CurrentlyLoadedArea;
                    if (area != null)
                    {
                        string areaName = area.AreaDisplayName;
                        if (!string.IsNullOrEmpty(areaName))
                            sb.Append("Area: ").AppendLine(areaName);
                    }
                }
                catch { }

                // Active quests — Player.QuestBook.Quests (IEnumerable<Quest>)
                try
                {
                    var questBook = Game.Instance?.Player?.QuestBook;
                    if (questBook != null)
                    {
                        int count = 0;
                        foreach (var quest in questBook.Quests)
                        {
                            if (quest == null) continue;
                            try
                            {
                                // Quest.State: Started (also catches Updated which maps to Started)
                                if (quest.State != QuestState.Started) continue;

                                // Quest.Blueprint → BlueprintQuest.Title (LocalizedString)
                                var bpQuest = quest.Blueprint;
                                if (bpQuest == null) continue;

                                string title = null;
                                try { title = bpQuest.Title; } catch { }
                                if (string.IsNullOrEmpty(title))
                                    try { title = bpQuest.name; } catch { }

                                if (!string.IsNullOrEmpty(title))
                                {
                                    sb.Append("Active Quest: ").AppendLine(title);
                                    if (++count >= 3) break;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                string result = sb.ToString();
                // Only return if we have more than just the header
                return result.Length > 30 ? result : null;
            }
            catch { return null; }
        }
    }
}
