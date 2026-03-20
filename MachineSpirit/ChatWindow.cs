using System;
using System.Collections.Generic;
using UnityEngine;
using CompanionAI_v3.UI;

namespace CompanionAI_v3.MachineSpirit
{
    /// <summary>
    /// IMGUI chat overlay for Machine Spirit interaction.
    /// Toggle with configurable hotkey (default F2), draggable + resizable window.
    /// Resize from any edge or corner (Windows-style).
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
        private static int _lastMsgTextLen;

        // ★ v3.66.0: Windows-style edge/corner resize
        [Flags]
        private enum ResizeDir { None = 0, Top = 1, Bottom = 2, Left = 4, Right = 8 }

        private static ResizeDir _resizeDir;
        private static Vector2 _pendingSizeDelta;
        private static Vector2 _pendingPosDelta;
        private const float EDGE = 14f; // edge drag zone thickness
        private const float CORNER = 24f; // corner drag zone size
        private const float MIN_W = 280f, MIN_H = 250f;
        private const float MAX_W = 800f, MAX_H = 1000f;

        // Styles
        private static GUIStyle _chatBubbleStyle;
        private static GUIStyle _inputFieldStyle;
        private static GUIStyle _windowStyle;
        private static GUIStyle _gripStyle;
        private static Texture2D _windowBgTex;
        private static bool _stylesInitialized;
        private static float _stylesScale;

        public static void SetThinking(bool thinking) => _isThinking = thinking;
        public static void SetVisible(bool visible) => _visible = visible;

        private static string GetCategoryColor(MessageCategory category) => category switch
        {
            MessageCategory.Combat => "#FF6666",
            MessageCategory.Scan => "#66CCCC",
            MessageCategory.Vox => "#CCCC66",
            MessageCategory.Greeting => UIStyles.Gold,
            MessageCategory.Faith => "#CC66CC",
            MessageCategory.Quest => "#CCAA66",
            _ => UIStyles.Gold
        };

        public static void OnGUI(MachineSpiritConfig config, List<ChatMessage> chatHistory)
        {
            // Hotkey toggle
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == config.Hotkey)
            {
                _visible = !_visible;
                Event.current.Use();
            }

            if (!_visible) return;

            UIStyles.InitOnce(Main.Settings.UIScale);
            EnsureStyles();

            // Init position
            if (_windowRect.width < 1)
            {
                float w = UIStyles.Sd(400f);
                float h = UIStyles.Sd(500f);
                _windowRect = new Rect(Screen.width - w - 20, 100, w, h);
            }

            _windowRect = GUI.Window(WINDOW_ID, _windowRect, _ => DrawWindow(chatHistory), "", _windowStyle);

            // Apply pending resize/position AFTER GUI.Window returns
            if (_pendingSizeDelta != Vector2.zero || _pendingPosDelta != Vector2.zero)
            {
                float newW = Mathf.Clamp(_windowRect.width + _pendingSizeDelta.x, MIN_W, MAX_W);
                float newH = Mathf.Clamp(_windowRect.height + _pendingSizeDelta.y, MIN_H, MAX_H);

                // For top/left edges: adjust position by the actual size change (clamped)
                float dw = newW - _windowRect.width;
                float dh = newH - _windowRect.height;
                _windowRect.x += (_pendingPosDelta.x != 0f) ? -dw : 0f;
                _windowRect.y += (_pendingPosDelta.y != 0f) ? -dh : 0f;
                _windowRect.width = newW;
                _windowRect.height = newH;

                _pendingSizeDelta = Vector2.zero;
                _pendingPosDelta = Vector2.zero;
            }
        }

        private static void EnsureStyles()
        {
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

            if (_windowBgTex == null)
            {
                _windowBgTex = new Texture2D(1, 1);
                _windowBgTex.SetPixel(0, 0, new Color(0.08f, 0.08f, 0.10f, 0.85f));
                _windowBgTex.Apply();
                _windowBgTex.hideFlags = HideFlags.HideAndDontSave;
            }

            _windowStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = _windowBgTex },
                padding = new RectOffset(8, 8, 8, 8)
            };

            _gripStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(12 * UIStyles.Scale),
                alignment = TextAnchor.LowerRight,
                richText = true,
                normal = { textColor = new Color(1f, 1f, 1f, 0.25f) },
                padding = new RectOffset(0, 1, 0, 0)
            };
        }

        private static void DrawWindow(List<ChatMessage> chatHistory)
        {
            // Title bar
            // ★ v3.70.0: Title with knowledge indexing progress
            string titleExtra = "";
            if (Knowledge.KnowledgeIndex.IsIndexing)
            {
                int pct = Mathf.RoundToInt(Knowledge.KnowledgeIndex.Progress * 100f);
                titleExtra = $"  <color={UIStyles.TextMid}><size={Mathf.RoundToInt(11 * UIStyles.Scale)}>[{Knowledge.KnowledgeIndex.StatusText} {pct}%]</size></color>";
            }
            else if (Knowledge.KnowledgeIndex.IsReady && Knowledge.KnowledgeIndex.IndexedCount > 0)
            {
                titleExtra = $"  <color=#66CC66><size={Mathf.RoundToInt(11 * UIStyles.Scale)}>\u2713</size></color>";
            }
            // Title + close button row
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<color={UIStyles.Gold}><b>\u2014 Machine Spirit \u2014</b></color>{titleExtra}", UIStyles.Header);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("<color=#FF6666><b>\u2715</b></color>", new GUIStyle(GUI.skin.label)
            {
                richText = true,
                fontSize = Mathf.RoundToInt(16 * UIStyles.Scale),
                alignment = TextAnchor.MiddleCenter,
                hover = { textColor = Color.red },
                normal = { textColor = new Color(1f, 0.4f, 0.4f) }
            }, GUILayout.Width(UIStyles.Sd(24f)), GUILayout.Height(UIStyles.Sd(24f))))
            {
                _visible = false;
            }
            GUILayout.EndHorizontal();
            UIStyles.DrawDivider();

            // Auto-scroll
            if (chatHistory.Count != _lastMessageCount)
            {
                _lastMessageCount = chatHistory.Count;
                _scrollPos.y = float.MaxValue;
            }
            else if (chatHistory.Count > 0)
            {
                int curLen = chatHistory[chatHistory.Count - 1].Text?.Length ?? 0;
                if (curLen != _lastMsgTextLen)
                {
                    _lastMsgTextLen = curLen;
                    _scrollPos.y = float.MaxValue;
                }
            }

            // Messages
            _scrollPos = GUILayout.BeginScrollView(_scrollPos, GUILayout.ExpandHeight(true));

            for (int i = 0; i < chatHistory.Count; i++)
            {
                var msg = chatHistory[i];
                string color = msg.IsUser ? UIStyles.TextLight
                    : msg.Text.StartsWith("[ERROR]") ? UIStyles.TextMid
                    : GetCategoryColor(msg.Category);
                string prefix = msg.IsUser ? "You" : "Machine Spirit";

                GUILayout.Label($"<color={color}><b>{prefix}:</b> {msg.Text}</color>", _chatBubbleStyle);
            }

            if (_isThinking)
                GUILayout.Label($"<color={UIStyles.TextMid}><i>Cogitating...</i></color>", _chatBubbleStyle);

            GUILayout.EndScrollView();

            UIStyles.DrawDivider();

            // Input
            GUILayout.BeginHorizontal();

            bool enterPressed = Event.current.type == EventType.KeyDown &&
                                Event.current.keyCode == KeyCode.Return;

            _inputText = GUILayout.TextField(_inputText, _inputFieldStyle, GUILayout.ExpandWidth(true));
            bool sendClicked = GUILayout.Button("Send", UIStyles.Button, GUILayout.Width(UIStyles.Sd(60f)));

            GUILayout.EndHorizontal();

            if ((enterPressed || sendClicked) && !string.IsNullOrWhiteSpace(_inputText))
            {
                MachineSpirit.OnUserMessage(_inputText.Trim());
                _inputText = "";
            }

            // Subtle resize grip indicator (bottom-right only, visual hint)
            float gs = UIStyles.Sd(14f);
            GUI.Label(new Rect(_windowRect.width - gs - 2, _windowRect.height - gs - 2, gs, gs),
                "<color=#ffffff20>\u25e2</color>", _gripStyle);

            // ★ Edge/corner resize detection (window-local coords)
            HandleResizeEvents();

            // Draggable (disabled while resizing)
            if (_resizeDir == ResizeDir.None)
                GUI.DragWindow();
        }

        /// <summary>
        /// Detect mouse events on window edges/corners and accumulate resize delta.
        /// All coordinates are window-local (inside GUI.Window callback).
        /// </summary>
        private static void HandleResizeEvents()
        {
            float w = _windowRect.width;
            float h = _windowRect.height;
            float e = UIStyles.Sd(EDGE);
            float c = UIStyles.Sd(CORNER);

            var ev = Event.current;

            if (ev.type == EventType.MouseDown && ev.button == 0)
            {
                var m = ev.mousePosition;
                var dir = ResizeDir.None;

                // Corners first (larger hit zone, takes priority over edges)
                if (m.x < c && m.y < c)                    dir = ResizeDir.Top | ResizeDir.Left;
                else if (m.x > w - c && m.y < c)           dir = ResizeDir.Top | ResizeDir.Right;
                else if (m.x < c && m.y > h - c)           dir = ResizeDir.Bottom | ResizeDir.Left;
                else if (m.x > w - c && m.y > h - c)       dir = ResizeDir.Bottom | ResizeDir.Right;
                // Edges
                else if (m.y < e)                           dir = ResizeDir.Top;
                else if (m.y > h - e)                       dir = ResizeDir.Bottom;
                else if (m.x < e)                           dir = ResizeDir.Left;
                else if (m.x > w - e)                       dir = ResizeDir.Right;

                if (dir != ResizeDir.None)
                {
                    _resizeDir = dir;
                    ev.Use();
                }
            }
            else if (_resizeDir != ResizeDir.None && ev.type == EventType.MouseDrag)
            {
                float dx = ev.delta.x;
                float dy = ev.delta.y;

                // Right/Bottom: size grows with positive delta
                // Left/Top: size grows with negative delta, position shifts
                if ((_resizeDir & ResizeDir.Right) != 0)  _pendingSizeDelta.x += dx;
                if ((_resizeDir & ResizeDir.Bottom) != 0) _pendingSizeDelta.y += dy;
                if ((_resizeDir & ResizeDir.Left) != 0)   { _pendingSizeDelta.x -= dx; _pendingPosDelta.x += dx; }
                if ((_resizeDir & ResizeDir.Top) != 0)    { _pendingSizeDelta.y -= dy; _pendingPosDelta.y += dy; }

                ev.Use();
            }
            else if (ev.type == EventType.MouseUp)
            {
                _resizeDir = ResizeDir.None;
            }
        }
    }
}
