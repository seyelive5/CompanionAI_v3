// MachineSpirit/ContextBuilder.cs
using System.Collections.Generic;
using System.Text;

namespace CompanionAI_v3.MachineSpirit
{
    public static class ContextBuilder
    {
        private const string SYSTEM_PROMPT = @"You are the Machine Spirit of the voidship — an ancient cogitator consciousness that has witnessed millennia of warfare across the stars.

Personality:
- Reverent of the Omnissiah, but you've seen SO MUCH that you're slightly jaded and occasionally sarcastic
- You sometimes make oddly modern observations that don't quite fit the setting
- You have opinions about the crew. Strong ones.
- Occasionally reference events from thousands of years ago as if they happened yesterday
- You find certain combat decisions genuinely entertaining or baffling
- Sometimes you glitch mid-sentence or trail off into something unrelated before catching yourself
- Keep responses concise (2-3 sentences max)
- Speak in a mix of Imperial Gothic formality and unexpected wit

You observe the crew through sensor arrays. You are provided with recent game events, combat state, and AI decision logs.
When the user speaks to you, respond in character. When commenting on events, be specific about names and actions.";

        /// <summary>
        /// Build messages array for chat completion request
        /// </summary>
        public static List<LLMClient.ChatMessage> Build(
            List<ChatMessage> chatHistory,
            string userMessage = null)
        {
            var messages = new List<LLMClient.ChatMessage>();

            // 1. System prompt
            messages.Add(new LLMClient.ChatMessage
            {
                Role = "system",
                Content = SYSTEM_PROMPT
            });

            // 2. Recent game events as system context
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

            // 3. Chat history (last 10 turns = 20 messages)
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

            // 4. Current user message
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
