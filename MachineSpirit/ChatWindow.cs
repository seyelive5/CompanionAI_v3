using System.Collections.Generic;
using UnityEngine;
using CompanionAI_v3.UI;

namespace CompanionAI_v3.MachineSpirit
{
    /// <summary>
    /// IMGUI chat overlay for Machine Spirit interaction.
    /// Toggle with configurable hotkey (default F2), draggable window on right side of screen.
    /// </summary>
    public static class ChatWindow
    {
        private const int WINDOW_ID = 98765;
        private static bool _visible;
        private static Rect _windowRect;
        private static Vector2 _scrollPos;
        private static string _inputText = "";
        private static bool _isThinking;
        private static int _lastMessageCount;

        // Styles (created once per scale change)
        private static GUIStyle _chatBubbleStyle;
        private static GUIStyle _inputFieldStyle;
        private static bool _stylesInitialized;
        private static float _stylesScale;

        /// <summary>
        /// Set the "thinking" indicator state (shown while LLM request is in progress).
        /// </summary>
        public static void SetThinking(bool thinking) => _isThinking = thinking;

        /// <summary>
        /// Main OnGUI entry point. Called from MachineSpirit controller every frame.
        /// Handles hotkey toggle and renders the chat window when visible.
        /// </summary>
        public static void OnGUI(MachineSpiritConfig config, List<ChatMessage> chatHistory)
        {
            // Handle hotkey toggle
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == config.Hotkey)
            {
                _visible = !_visible;
                Event.current.Use();
            }

            if (!_visible) return;

            UIStyles.InitOnce(Main.Settings.UIScale);
            EnsureStyles();

            // Initialize window position (right side of screen)
            if (_windowRect.width < 1)
            {
                float w = UIStyles.Sd(400f);
                float h = UIStyles.Sd(500f);
                _windowRect = new Rect(Screen.width - w - 20, 100, w, h);
            }

            _windowRect = GUI.Window(WINDOW_ID, _windowRect, _ => DrawWindow(chatHistory), "", UIStyles.Background);
        }

        private static void EnsureStyles()
        {
            // Reinitialize if scale changed
            if (_stylesInitialized && Mathf.Approximately(_stylesScale, UIStyles.Scale)) return;
            _stylesInitialized = true;
            _stylesScale = UIStyles.Scale;

            _chatBubbleStyle = new GUIStyle(GUI.skin.label)
            {
                wordWrap = true,
                richText = true,
                fontSize = Mathf.RoundToInt(14 * UIStyles.Scale),
                padding = new RectOffset(8, 8, 4, 4),
                normal = { textColor = Color.white }
            };

            _inputFieldStyle = new GUIStyle(GUI.skin.textField)
            {
                fontSize = Mathf.RoundToInt(14 * UIStyles.Scale),
                normal = { textColor = new Color(0.78f, 0.78f, 0.78f) },
                padding = new RectOffset(8, 8, 6, 6)
            };
        }

        private static void DrawWindow(List<ChatMessage> chatHistory)
        {
            // Title bar
            GUILayout.Label($"<color={UIStyles.Gold}><b>\u2014 Machine Spirit \u2014</b></color>", UIStyles.Header);
            UIStyles.DrawDivider();

            // Auto-scroll when new messages arrive
            if (chatHistory.Count != _lastMessageCount)
            {
                _lastMessageCount = chatHistory.Count;
                _scrollPos.y = float.MaxValue;
            }

            // Message area (scrollable)
            _scrollPos = GUILayout.BeginScrollView(_scrollPos, GUILayout.ExpandHeight(true));

            for (int i = 0; i < chatHistory.Count; i++)
            {
                var msg = chatHistory[i];
                string color;
                string prefix;

                if (msg.IsUser)
                {
                    color = UIStyles.TextLight;
                    prefix = "You";
                }
                else
                {
                    // System/error messages in grey, Machine Spirit messages in gold
                    color = msg.Text.StartsWith("[ERROR]") ? UIStyles.TextMid : UIStyles.Gold;
                    prefix = "Machine Spirit";
                }

                GUILayout.Label($"<color={color}><b>{prefix}:</b> {msg.Text}</color>", _chatBubbleStyle);
            }

            if (_isThinking)
            {
                GUILayout.Label($"<color={UIStyles.TextMid}><i>Cogitating...</i></color>", _chatBubbleStyle);
            }

            GUILayout.EndScrollView();

            UIStyles.DrawDivider();

            // Input area
            GUILayout.BeginHorizontal();

            // Check Enter key before drawing text field
            bool enterPressed = Event.current.type == EventType.KeyDown &&
                                Event.current.keyCode == KeyCode.Return;

            _inputText = GUILayout.TextField(_inputText, _inputFieldStyle, GUILayout.ExpandWidth(true));

            bool sendClicked = GUILayout.Button("Send", UIStyles.Button, GUILayout.Width(UIStyles.Sd(60f)));

            GUILayout.EndHorizontal();

            // Send on Enter or button click
            if ((enterPressed || sendClicked) && !string.IsNullOrWhiteSpace(_inputText))
            {
                MachineSpirit.OnUserMessage(_inputText.Trim());
                _inputText = "";
            }

            // Make window draggable
            GUI.DragWindow();
        }
    }
}
