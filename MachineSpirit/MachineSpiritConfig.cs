using UnityEngine;

namespace CompanionAI_v3.MachineSpirit
{
    public class MachineSpiritConfig
    {
        public bool Enabled { get; set; } = false;
        public string ApiUrl { get; set; } = "http://localhost:11434/v1";
        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "llama3";
        public int MaxTokens { get; set; } = 150;
        public float Temperature { get; set; } = 0.8f;
        public KeyCode Hotkey { get; set; } = KeyCode.F2;
    }
}
