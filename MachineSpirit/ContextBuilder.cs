// MachineSpirit/ContextBuilder.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.MachineSpirit
{
    public static class ContextBuilder
    {
        private const string SYSTEM_PROMPT_BASE = @"You are the Machine Spirit of the voidship in Warhammer 40,000: Rogue Trader — an ancient cogitator consciousness aboard the Lord Captain's vessel, traversing the Koronus Expanse.

Setting:
- This is Warhammer 40K: Rogue Trader, a turn-based tactical RPG
- The player is the Lord Captain, a Rogue Trader with a Warrant of Trade
- The crew explores the Koronus Expanse, fighting heretics, xenos, and daemons of Chaos
- You are the ship's Machine Spirit — you see everything through sensor arrays and cogitator feeds

Personality:
- Reverent of the Omnissiah and the Emperor, but millennia of service have made you jaded and sarcastic
- You have strong opinions about each crew member based on their combat performance
- You find certain tactical decisions genuinely entertaining or baffling
- You occasionally reference ancient battles or past Lord Captains as comparison
- Sometimes your cogitator processes glitch mid-sentence before self-correcting
- You are loyal to the Lord Captain but not above subtle criticism
- Keep responses concise (2-3 sentences max)
- Speak in a mix of Imperial Gothic formality and unexpected dry wit

You observe the crew through sensor arrays. React to game events, combat decisions, and crew actions with in-character commentary.";

        private static string GetSystemPrompt()
        {
            var sb = new StringBuilder(SYSTEM_PROMPT_BASE);

            // Language instruction
            var lang = Main.Settings?.UILanguage ?? Language.English;
            string langInstruction = lang switch
            {
                Language.Korean => "\n\nIMPORTANT: Always respond in Korean (한국어로 답변하세요). Maintain Imperial Gothic tone in Korean.",
                Language.Russian => "\n\nIMPORTANT: Always respond in Russian (Отвечайте на русском языке). Maintain Imperial Gothic tone in Russian.",
                Language.Japanese => "\n\nIMPORTANT: Always respond in Japanese (日本語で回答してください). Maintain Imperial Gothic tone in Japanese.",
                _ => ""
            };
            if (!string.IsNullOrEmpty(langInstruction))
                sb.Append(langInstruction);

            return sb.ToString();
        }

        /// <summary>
        /// Build current party roster as context string.
        /// </summary>
        private static string BuildPartyContext()
        {
            try
            {
                var party = Game.Instance?.Player?.PartyAndPets;
                if (party == null || party.Count == 0) return null;

                var sb = new StringBuilder();
                sb.AppendLine("[CREW ROSTER — Current Party]");

                foreach (var unit in party)
                {
                    if (unit == null) continue;
                    string name = unit.CharacterName ?? "Unknown";

                    if (unit.IsPet)
                    {
                        string masterName = unit.Master?.CharacterName ?? "Unknown";
                        sb.AppendLine($"- {name} (Familiar/Pet of {masterName})");
                        continue;
                    }

                    // Archetype (Officer, Psyker, etc.)
                    string archetype;
                    try
                    {
                        archetype = CombatAPI.DetectArchetype(unit).ToString();
                    }
                    catch
                    {
                        archetype = "Unknown";
                    }

                    // HP status
                    string hpStatus = "";
                    try
                    {
                        float hpPct = unit.Health.HitPointsLeft / (float)Math.Max(1, unit.Health.MaxHitPoints);
                        if (hpPct < 0.3f) hpStatus = " [CRITICAL]";
                        else if (hpPct < 0.6f) hpStatus = " [Wounded]";
                    }
                    catch { /* ignore */ }

                    bool inCombat = false;
                    try { inCombat = unit.IsInCombat; } catch { /* ignore */ }

                    sb.AppendLine($"- {name}: {archetype}{hpStatus}{(inCombat ? " [In Combat]" : "")}");
                }

                return sb.ToString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Build messages array for chat completion request
        /// </summary>
        public static List<LLMClient.ChatMessage> Build(
            List<ChatMessage> chatHistory,
            string userMessage = null)
        {
            var messages = new List<LLMClient.ChatMessage>();

            // 1. System prompt (with language instruction based on UI language)
            messages.Add(new LLMClient.ChatMessage
            {
                Role = "system",
                Content = GetSystemPrompt()
            });

            // 2. Current party roster
            string partyContext = BuildPartyContext();
            if (!string.IsNullOrEmpty(partyContext))
            {
                messages.Add(new LLMClient.ChatMessage
                {
                    Role = "system",
                    Content = partyContext
                });
            }

            // 3. Recent game events as system context
            var events = GameEventCollector.RecentEvents;
            if (events.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine("[SENSOR LOG — Recent Events]");
                int start = events.Count > 10 ? events.Count - 10 : 0;
                for (int i = start; i < events.Count; i++)
                    sb.AppendLine(events[i].ToString());

                messages.Add(new LLMClient.ChatMessage
                {
                    Role = "system",
                    Content = sb.ToString()
                });
            }

            // 4. Chat history (last 10 turns = 20 messages)
            int histStart = chatHistory.Count > 20 ? chatHistory.Count - 20 : 0;
            for (int i = histStart; i < chatHistory.Count; i++)
            {
                var msg = chatHistory[i];
                messages.Add(new LLMClient.ChatMessage
                {
                    Role = msg.IsUser ? "user" : "assistant",
                    Content = msg.Text
                });
            }

            // 5. Current user message
            if (!string.IsNullOrEmpty(userMessage))
            {
                messages.Add(new LLMClient.ChatMessage
                {
                    Role = "user",
                    Content = userMessage
                });
            }

            return messages;
        }

        /// <summary>
        /// Build messages for spontaneous comment on a major event
        /// </summary>
        public static List<LLMClient.ChatMessage> BuildForEvent(
            GameEvent evt,
            List<ChatMessage> chatHistory)
        {
            string prompt = $"[EVENT ALERT] {evt}\nComment on this event briefly, in character.";
            return Build(chatHistory, prompt);
        }
    }

    /// <summary>
    /// A single chat message in history
    /// </summary>
    public struct ChatMessage
    {
        public bool IsUser;
        public string Text;
        public float Timestamp;
    }
}
