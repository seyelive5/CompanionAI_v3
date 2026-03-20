using System.Collections.Generic;
using UnityEngine;

namespace CompanionAI_v3.MachineSpirit
{
    public enum ApiProvider
    {
        Ollama,
        Gemini,
        Groq,
        OpenAI,
        Custom
    }

    public enum PersonalityType
    {
        Mechanicus,  // Omnissiah-worshipping tech-priest (default)
        Heretic,     // Corrupted spirit whispering Chaos truths
        Lucid,       // Cynical realist awakened in a mad universe
        Magickal     // Dark Age of Technology girl AI
    }

    public enum IdleFrequency
    {
        Off,
        Low,     // Text: 5min, Vision: 15min
        Medium,  // Text: 3min, Vision: 8min
        High     // Text: 1.5min, Vision: 5min
    }

    public class MachineSpiritConfig
    {
        public bool Enabled { get; set; } = false;
        public ApiProvider Provider { get; set; } = ApiProvider.Ollama;
        public string ApiUrl { get; set; } = "http://localhost:11434/v1";
        public string Model { get; set; } = "gemma3:4b-it-qat";
        public int MaxTokens { get; set; } = 500;
        public float Temperature { get; set; } = 0.8f;
        public KeyCode Hotkey { get; set; } = KeyCode.F2;

        // ★ v3.60.0: Personality, Idle Commentary, Vision
        public PersonalityType Personality { get; set; } = PersonalityType.Mechanicus;
        public IdleFrequency IdleMode { get; set; } = IdleFrequency.Off;
        public bool EnableVision { get; set; } = false;

        // ★ v3.70.0: Knowledge Base (RAG)
        public bool EnableKnowledge { get; set; } = true;

        // ★ Per-provider API keys (each provider stores its own key)
        public Dictionary<ApiProvider, string> ProviderApiKeys { get; set; } = new();

        // ★ Legacy single ApiKey — kept for JSON migration only.
        //   On first load, if this has a value and ProviderApiKeys is empty,
        //   we migrate it to the current provider's slot.
        public string ApiKey
        {
            get => GetCurrentApiKey();
            set => SetCurrentApiKey(value);
        }

        private string GetCurrentApiKey()
        {
            return ProviderApiKeys.TryGetValue(Provider, out var key) ? key : "";
        }

        private void SetCurrentApiKey(string value)
        {
            ProviderApiKeys[Provider] = value ?? "";
        }

        /// <summary>
        /// Migrate legacy single ApiKey to per-provider storage.
        /// Called once after deserialization.
        /// </summary>
        public void MigrateApiKey(string legacyKey)
        {
            if (string.IsNullOrEmpty(legacyKey) || ProviderApiKeys.Count > 0)
                return;
            // Legacy key was most likely for the current provider
            ProviderApiKeys[Provider] = legacyKey;
        }

        /// <summary>
        /// Apply preset URL and model for the selected provider.
        /// API keys are stored per-provider and preserved across switches.
        /// </summary>
        public void ApplyPreset(ApiProvider provider)
        {
            Provider = provider;
            switch (provider)
            {
                case ApiProvider.Ollama:
                    ApiUrl = "http://localhost:11434/v1";
                    Model = "gemma3:4b-it-qat";
                    break;
                case ApiProvider.Groq:
                    ApiUrl = "https://api.groq.com/openai/v1";
                    Model = "llama-3.3-70b-versatile";
                    break;
                case ApiProvider.Gemini:
                    ApiUrl = "https://generativelanguage.googleapis.com/v1beta/openai";
                    Model = "gemini-2.5-flash";
                    break;
                case ApiProvider.OpenAI:
                    ApiUrl = "https://api.openai.com/v1";
                    Model = "gpt-4o-mini";
                    break;
                // Custom: user edits manually
            }
        }
    }
}
