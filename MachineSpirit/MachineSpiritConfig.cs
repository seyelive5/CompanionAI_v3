using UnityEngine;

namespace CompanionAI_v3.MachineSpirit
{
    public enum ApiProvider
    {
        Ollama,
        Groq,
        Gemini,
        OpenAI,
        Custom
    }

    public class MachineSpiritConfig
    {
        public bool Enabled { get; set; } = false;
        public ApiProvider Provider { get; set; } = ApiProvider.Ollama;
        public string ApiUrl { get; set; } = "http://localhost:11434/v1";
        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "llama3.2";
        public int MaxTokens { get; set; } = 150;
        public float Temperature { get; set; } = 0.8f;
        public KeyCode Hotkey { get; set; } = KeyCode.F2;

        /// <summary>
        /// Apply preset URL and model for the selected provider.
        /// Does not overwrite ApiKey.
        /// </summary>
        public void ApplyPreset(ApiProvider provider)
        {
            Provider = provider;
            switch (provider)
            {
                case ApiProvider.Ollama:
                    ApiUrl = "http://localhost:11434/v1";
                    Model = "llama3.2";
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
