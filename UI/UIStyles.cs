using UnityEngine;

namespace CompanionAI_v3.UI
{
    /// <summary>
    /// Imperial Dark theme — centralized GUIStyle and color system.
    /// All textures created once via InitOnce(), called from OnGUI().
    /// </summary>
    public static class UIStyles
    {
        // ── Rich text color constants ──────────────────────────
        public const string Gold      = "#D4A947";
        public const string TextLight = "#C8C8C8";
        public const string TextMid   = "#888888";
        public const string TextDim   = "#666666";
        public const string Danger    = "#FF6347";
        public const string Green     = "#98FB98";
        public const string RoleBlue  = "#4169E1";
        public const string RoleRed   = "#FF6347";
        public const string RoleGold  = "#FFD700";
        public const string RoleGreen = "#98FB98";

        // ── Background palette (Color32) ───────────────────────
        private static readonly Color32 ColBackground   = new Color32(0x1A, 0x1A, 0x1E, 0xFF);
        private static readonly Color32 ColTabActive     = new Color32(0x8B, 0x73, 0x32, 0xFF);
        private static readonly Color32 ColTabInactive   = new Color32(0x2D, 0x2D, 0x32, 0xFF);
        private static readonly Color32 ColTabHover      = new Color32(0x3A, 0x3A, 0x42, 0xFF);
        private static readonly Color32 ColSectionBg     = new Color32(0x22, 0x22, 0x28, 0xFF);
        private static readonly Color32 ColDivider       = new Color32(0x3A, 0x3A, 0x42, 0xFF);
        private static readonly Color32 ColButton        = new Color32(0x33, 0x33, 0x3A, 0xFF);
        private static readonly Color32 ColButtonHover   = new Color32(0x44, 0x44, 0x4E, 0xFF);
        private static readonly Color32 ColCheckOn       = new Color32(0xD4, 0xA9, 0x47, 0xFF);

        // ── Textures (created once) ────────────────────────────
        private static Texture2D _texBackground;
        private static Texture2D _texTabActive;
        private static Texture2D _texTabInactive;
        private static Texture2D _texTabHover;
        private static Texture2D _texSectionBg;
        private static Texture2D _texDivider;
        private static Texture2D _texButton;
        private static Texture2D _texButtonHover;
        private static Texture2D _texCheckOn;

        // ── Styles ─────────────────────────────────────────────
        public static GUIStyle Background  { get; private set; }
        public static GUIStyle TabActive   { get; private set; }
        public static GUIStyle TabInactive { get; private set; }
        public static GUIStyle SectionBox  { get; private set; }
        public static GUIStyle Header      { get; private set; }
        public static GUIStyle SubHeader   { get; private set; }
        public static GUIStyle Description { get; private set; }
        public static GUIStyle Label       { get; private set; }
        public static GUIStyle BoldLabel   { get; private set; }
        public static GUIStyle Button      { get; private set; }
        public static GUIStyle Checkbox    { get; private set; }
        public static GUIStyle CharRow     { get; private set; }
        public static GUIStyle Divider     { get; private set; }
        public static GUIStyle SliderLabel { get; private set; }

        private static bool _initialized;

        // ── Init ───────────────────────────────────────────────

        /// <summary>
        /// Must be called from OnGUI() — requires GUI.skin to be available.
        /// Safe to call every frame; work only happens once.
        /// </summary>
        public static void InitOnce()
        {
            if (_initialized) return;
            _initialized = true;

            // Textures
            _texBackground  = MakeTex(ColBackground);
            _texTabActive   = MakeTex(ColTabActive);
            _texTabInactive = MakeTex(ColTabInactive);
            _texTabHover    = MakeTex(ColTabHover);
            _texSectionBg   = MakeTex(ColSectionBg);
            _texDivider     = MakeTex(ColDivider);
            _texButton      = MakeTex(ColButton);
            _texButtonHover = MakeTex(ColButtonHover);
            _texCheckOn     = MakeTex(ColCheckOn);

            // ── Background ─────────────────────────────────────
            Background = new GUIStyle(GUI.skin.box)
            {
                normal = { background = _texBackground }
            };

            // ── TabActive ──────────────────────────────────────
            TabActive = new GUIStyle(GUI.skin.button)
            {
                fontSize  = 15,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal    = { background = _texTabActive,   textColor = Color.white },
                hover     = { background = _texTabActive,   textColor = Color.white },
                active    = { background = _texTabActive,   textColor = Color.white },
                focused   = { background = _texTabActive,   textColor = Color.white }
            };

            // ── TabInactive ────────────────────────────────────
            var greyText = new Color(0.6f, 0.6f, 0.6f);
            TabInactive = new GUIStyle(GUI.skin.button)
            {
                fontSize  = 15,
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleCenter,
                normal    = { background = _texTabInactive, textColor = greyText },
                hover     = { background = _texTabHover,    textColor = Color.white },
                active    = { background = _texTabHover,    textColor = Color.white },
                focused   = { background = _texTabInactive, textColor = greyText }
            };

            // ── SectionBox ─────────────────────────────────────
            SectionBox = new GUIStyle(GUI.skin.box)
            {
                normal  = { background = _texSectionBg },
                padding = new RectOffset(12, 12, 8, 8),
                margin  = new RectOffset(0, 0, 4, 4)
            };

            // ── Header (size 20) ───────────────────────────────
            Header = new GUIStyle(GUI.skin.label)
            {
                fontSize  = 20,
                fontStyle = FontStyle.Bold,
                richText  = true,
                alignment = TextAnchor.MiddleLeft,
                normal    = { textColor = Color.white }
            };

            // ── SubHeader (size 17) ────────────────────────────
            SubHeader = new GUIStyle(GUI.skin.label)
            {
                fontSize  = 17,
                fontStyle = FontStyle.Bold,
                richText  = true,
                alignment = TextAnchor.MiddleLeft,
                normal    = { textColor = Color.white }
            };

            // ── Description ────────────────────────────────────
            Description = new GUIStyle(GUI.skin.label)
            {
                fontSize  = 14,
                wordWrap  = true,
                richText  = true,
                normal    = { textColor = greyText }
            };

            // ── Label (size 15, light grey) ────────────────────
            var lightGrey = new Color(0.78f, 0.78f, 0.78f); // ~#C8C8C8
            Label = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                richText = true,
                normal   = { textColor = lightGrey }
            };

            // ── BoldLabel ──────────────────────────────────────
            BoldLabel = new GUIStyle(GUI.skin.label)
            {
                fontSize  = 16,
                fontStyle = FontStyle.Bold,
                richText  = true,
                normal    = { textColor = lightGrey }
            };

            // ── Button ─────────────────────────────────────────
            Button = new GUIStyle(GUI.skin.button)
            {
                fontSize  = 14,
                alignment = TextAnchor.MiddleCenter,
                normal    = { background = _texButton,      textColor = lightGrey },
                hover     = { background = _texButtonHover,  textColor = Color.white },
                active    = { background = _texButtonHover,  textColor = Color.white },
                focused   = { background = _texButton,      textColor = lightGrey }
            };

            // ── Checkbox ───────────────────────────────────────
            Checkbox = new GUIStyle(GUI.skin.button)
            {
                fontSize  = 20,
                alignment = TextAnchor.MiddleCenter,
                normal    = { background = _texButton,  textColor = lightGrey },
                hover     = { background = _texButtonHover, textColor = Color.white },
                active    = { background = _texCheckOn, textColor = Color.white },
                focused   = { background = _texButton,  textColor = lightGrey }
            };

            // ── CharRow ────────────────────────────────────────
            CharRow = new GUIStyle(GUI.skin.box)
            {
                normal  = { background = _texSectionBg },
                padding = new RectOffset(8, 8, 6, 6),
                margin  = new RectOffset(0, 0, 2, 2)
            };

            // ── Divider (1px line) ─────────────────────────────
            Divider = new GUIStyle(GUI.skin.box)
            {
                normal     = { background = _texDivider },
                fixedHeight = 1,
                margin     = new RectOffset(0, 0, 6, 6),
                padding    = new RectOffset(0, 0, 0, 0)
            };

            // ── SliderLabel ────────────────────────────────────
            SliderLabel = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                richText = true,
                normal   = { textColor = lightGrey }
            };
        }

        // ── Helpers ────────────────────────────────────────────

        /// <summary>
        /// Draws a 1px horizontal divider line.
        /// </summary>
        public static void DrawDivider()
        {
            GUILayout.Box(GUIContent.none, Divider, GUILayout.ExpandWidth(true), GUILayout.Height(1));
        }

        /// <summary>
        /// Draws a gold-colored section title using SubHeader style.
        /// </summary>
        public static void SectionTitle(string title)
        {
            GUILayout.Label($"<color={Gold}>{title}</color>", SubHeader);
        }

        /// <summary>
        /// Creates a 1x1 Texture2D that won't be destroyed on scene load.
        /// </summary>
        private static Texture2D MakeTex(Color color)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }
    }
}
