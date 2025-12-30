using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using UnityEngine;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.UI
{
    /// <summary>
    /// v3.0: 메인 UI
    /// </summary>
    public static class MainUI
    {
        private static string _selectedCharacterId = "";
        private static CharacterSettings _editingSettings = null;
        private static Vector2 _scrollPosition = Vector2.zero;

        private static GUIStyle _headerStyle;
        private static GUIStyle _boldLabelStyle;
        private static GUIStyle _boxStyle;
        private static GUIStyle _descriptionStyle;

        private const float CHECKBOX_SIZE = 50f;
        private const float BUTTON_HEIGHT = 50f;
        private const float ROLE_BUTTON_WIDTH = 120f;
        private const float RANGE_BUTTON_WIDTH = 150f;
        private const float CHAR_NAME_WIDTH = 180f;
        private const float ROLE_LABEL_WIDTH = 120f;
        private const float RANGE_LABEL_WIDTH = 160f;
        private const float LANG_BUTTON_WIDTH = 100f;

        private static string L(string key) => Localization.Get(key);

        public static void OnGUI()
        {
            Localization.CurrentLanguage = Main.Settings.UILanguage;
            InitStyles();

            try
            {
                _scrollPosition = GUILayout.BeginScrollView(_scrollPosition, GUILayout.Height(700));
                GUILayout.BeginVertical("box");

                DrawHeader();
                DrawDivider();
                DrawGlobalSettings();
                DrawDivider();
                DrawCharacterSelection();

                GUILayout.EndVertical();
                GUILayout.EndScrollView();
            }
            catch (Exception ex)
            {
                GUILayout.Label($"<color=#FF0000>UI Error: {ex.Message}</color>");
            }
        }

        private static void InitStyles()
        {
            if (_headerStyle == null)
            {
                _headerStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, richText = true };
            }
            if (_boldLabelStyle == null)
            {
                _boldLabelStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, richText = true };
            }
            if (_descriptionStyle == null)
            {
                _descriptionStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true, wordWrap = true };
            }
            if (_boxStyle == null)
            {
                _boxStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(15, 15, 15, 15) };
            }
        }

        private static void DrawDivider() => GUILayout.Space(15);

        private static void DrawHeader()
        {
            GUILayout.Label($"<color=#00FFFF><b>{L("Title")}</b></color>", _headerStyle);
            GUILayout.Label($"<color=#D8D8D8>{L("Subtitle")}</color>", _descriptionStyle);
        }

        private static void DrawGlobalSettings()
        {
            GUILayout.BeginVertical(_boxStyle);
            GUILayout.Label($"<b>{L("GlobalSettings")}</b>", _boldLabelStyle);
            GUILayout.Space(10);

            // Language
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>{L("Language")}:</b>", _boldLabelStyle, GUILayout.Width(100));
            GUILayout.Space(10);

            foreach (Language lang in Enum.GetValues(typeof(Language)))
            {
                bool isSelected = Main.Settings.UILanguage == lang;
                string langName = lang == Language.English ? "English" : "한국어";
                string buttonText = isSelected ? $"<color=#00FF00><b>{langName}</b></color>" : $"<color=#D8D8D8>{langName}</color>";

                if (GUILayout.Button(buttonText, GUI.skin.button, GUILayout.Width(LANG_BUTTON_WIDTH), GUILayout.Height(40)))
                {
                    Main.Settings.UILanguage = lang;
                    Localization.CurrentLanguage = lang;
                }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(10);

            Main.Settings.EnableDebugLogging = DrawCheckbox(Main.Settings.EnableDebugLogging, L("EnableDebugLogging"));
            Main.Settings.ShowAIThoughts = DrawCheckbox(Main.Settings.ShowAIThoughts, L("ShowAIDecisionLog"));

            GUILayout.EndVertical();
        }

        private static bool DrawCheckbox(bool value, string label)
        {
            GUILayout.BeginHorizontal();
            string checkIcon = value ? "<size=16><b><color=green>[X]</color></b></size>" : "<size=16><b>[ ]</b></size>";

            if (GUILayout.Button(checkIcon, GUI.skin.box, GUILayout.Width(CHECKBOX_SIZE), GUILayout.Height(CHECKBOX_SIZE)))
                value = !value;

            GUILayout.Space(10);
            if (GUILayout.Button($"<size=14>{label}</size>", GUI.skin.label, GUILayout.Height(CHECKBOX_SIZE)))
                value = !value;

            GUILayout.EndHorizontal();
            return value;
        }

        private static void DrawCharacterSelection()
        {
            GUILayout.BeginVertical(_boxStyle);
            GUILayout.Label($"<b>{L("PartyMembers")}</b>", _boldLabelStyle);
            GUILayout.Space(10);

            var characters = GetPartyMembers();
            if (characters.Count == 0)
            {
                GUILayout.Label($"<color=#D8D8D8><i>{L("NoCharacters")}</i></color>", _descriptionStyle);
                GUILayout.EndVertical();
                return;
            }

            // Header
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>{L("AI")}</b>", GUILayout.Width(60));
            GUILayout.Label($"<b>{L("Character")}</b>", GUILayout.Width(CHAR_NAME_WIDTH));
            GUILayout.Label($"<b>{L("Role")}</b>", GUILayout.Width(ROLE_LABEL_WIDTH));
            GUILayout.Label($"<b>{L("Range")}</b>", GUILayout.Width(RANGE_LABEL_WIDTH));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(10);

            foreach (var character in characters)
                DrawCharacterRow(character);

            GUILayout.EndVertical();
        }

        private static void DrawCharacterRow(CharacterInfo character)
        {
            var settings = Main.Settings.GetOrCreateSettings(character.Id, character.Name);

            GUILayout.BeginHorizontal("box");

            // AI Toggle
            string checkIcon = settings.EnableCustomAI ? "<size=14><b><color=green>[X]</color></b></size>" : "<size=14><b>[ ]</b></size>";
            if (GUILayout.Button(checkIcon, GUI.skin.box, GUILayout.Width(CHECKBOX_SIZE), GUILayout.Height(CHECKBOX_SIZE)))
                settings.EnableCustomAI = !settings.EnableCustomAI;

            // Character name
            bool isSelected = _selectedCharacterId == character.Id;
            string buttonText = isSelected ? $"<b>▼ {character.Name}</b>" : $"▶ {character.Name}";
            if (GUILayout.Button(buttonText, GUI.skin.button, GUILayout.Width(CHAR_NAME_WIDTH), GUILayout.Height(CHECKBOX_SIZE)))
            {
                if (isSelected) { _selectedCharacterId = ""; _editingSettings = null; }
                else { _selectedCharacterId = character.Id; _editingSettings = settings; }
            }

            // Role
            string roleColor = GetRoleColor(settings.Role);
            GUILayout.Label($"<color={roleColor}><b>{Localization.GetRoleName(settings.Role)}</b></color>", GUILayout.Width(ROLE_LABEL_WIDTH), GUILayout.Height(CHECKBOX_SIZE));

            // Range
            GUILayout.Label(Localization.GetRangeName(settings.RangePreference), GUILayout.Width(RANGE_LABEL_WIDTH), GUILayout.Height(CHECKBOX_SIZE));

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (isSelected && _editingSettings != null)
            {
                GUILayout.BeginVertical("box");
                DrawCharacterAISettings();
                GUILayout.EndVertical();
            }
        }

        private static string GetRoleColor(AIRole role) => role switch
        {
            AIRole.Tank => "#4169E1",
            AIRole.DPS => "#FF6347",
            AIRole.Support => "#FFD700",
            AIRole.Auto => "#98FB98",  // ★ v3.0.92: Auto - Light Green
            _ => "#FFFFFF"
        };

        private static void DrawCharacterAISettings()
        {
            if (_editingSettings == null) return;

            GUILayout.Space(10);
            DrawRoleSelection();
            GUILayout.Space(15);
            DrawRangePreferenceSelection();
            GUILayout.Space(10);
        }

        private static void DrawRoleSelection()
        {
            GUILayout.Label($"<b>{L("CombatRole")}</b>", _boldLabelStyle);
            GUILayout.Label($"<color=#D8D8D8><i>{L("CombatRoleDesc")}</i></color>", _descriptionStyle);
            GUILayout.Space(8);

            GUILayout.BeginHorizontal();
            foreach (AIRole role in Enum.GetValues(typeof(AIRole)))
            {
                string roleColor = GetRoleColor(role);
                bool isSelected = _editingSettings.Role == role;
                string roleName = Localization.GetRoleName(role);
                string buttonText = isSelected ? $"<color={roleColor}><b>{roleName}</b></color>" : $"<color=#D8D8D8>{roleName}</color>";

                if (GUILayout.Toggle(isSelected, buttonText, GUI.skin.button, GUILayout.Width(ROLE_BUTTON_WIDTH), GUILayout.Height(BUTTON_HEIGHT)))
                    _editingSettings.Role = role;
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(8);
            GUILayout.Label($"<color=#D8D8D8><i>{Localization.GetRoleDescription(_editingSettings.Role)}</i></color>", _descriptionStyle);
        }

        private static void DrawRangePreferenceSelection()
        {
            GUILayout.Label($"<b>{L("RangePreference")}</b>", _boldLabelStyle);
            GUILayout.Label($"<color=#D8D8D8><i>{L("RangePreferenceDesc")}</i></color>", _descriptionStyle);
            GUILayout.Space(8);

            GUILayout.BeginHorizontal();
            foreach (RangePreference pref in Enum.GetValues(typeof(RangePreference)))
            {
                bool isSelected = _editingSettings.RangePreference == pref;
                string prefName = Localization.GetRangeName(pref);
                string buttonText = isSelected ? $"<b>{prefName}</b>" : $"<color=#D8D8D8>{prefName}</color>";

                if (GUILayout.Toggle(isSelected, buttonText, GUI.skin.button, GUILayout.Width(RANGE_BUTTON_WIDTH), GUILayout.Height(BUTTON_HEIGHT)))
                    _editingSettings.RangePreference = pref;
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(8);
            GUILayout.Label($"<color=#D8D8D8><i>{Localization.GetRangeDescription(_editingSettings.RangePreference)}</i></color>", _descriptionStyle);
        }

        private static List<CharacterInfo> GetPartyMembers()
        {
            try
            {
                if (Game.Instance?.Player == null) return new List<CharacterInfo>();

                var partyMembers = Game.Instance.Player.PartyAndPets;
                if (partyMembers == null || partyMembers.Count == 0) return new List<CharacterInfo>();

                return partyMembers
                    .Where(unit => unit != null)
                    .Select(unit => new CharacterInfo { Id = unit.UniqueId ?? "unknown", Name = unit.CharacterName ?? "Unnamed", Unit = unit })
                    .ToList();
            }
            catch { return new List<CharacterInfo>(); }
        }

        private class CharacterInfo
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "Unknown";  // ★ v3.0.46: 기본값 추가
            public BaseUnitEntity Unit { get; set; }
        }
    }
}
