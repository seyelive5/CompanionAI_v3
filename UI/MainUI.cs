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
        private static bool _showAdvancedSettings = false;  // ★ v3.5.13: 고급 설정 접기/펴기
        private static bool _showPerformanceSettings = false;  // ★ v3.5.20: 성능 설정 접기/펴기

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
        private const float LANG_BUTTON_WIDTH = 150f;

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
                _boldLabelStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, richText = true };
            }
            if (_descriptionStyle == null)
            {
                _descriptionStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, richText = true, wordWrap = true };
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

            GUILayout.Space(15);
            DrawPerformanceSettings();

            GUILayout.EndVertical();
        }

        /// <summary>
        /// ★ v3.5.20: 성능 설정 섹션 (전역)
        /// </summary>
        private static void DrawPerformanceSettings()
        {
            // ★ v3.5.21: 접기/펴기 버튼 - 크게, 눈에 띄게
            GUILayout.BeginHorizontal();
            string toggleText = _showPerformanceSettings
                ? $"<size=20><b><color=#00BFFF>▼ {L("PerformanceSettings")}</color></b></size>"
                : $"<size=20><b><color=#AAAAAA>▶ {L("PerformanceSettings")}</color></b></size>";

            if (GUILayout.Button(toggleText, _boldLabelStyle, GUILayout.Height(40), GUILayout.Width(400)))
                _showPerformanceSettings = !_showPerformanceSettings;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (!_showPerformanceSettings) return;

            // ★ v3.5.21: 박스 넓이 확대
            GUILayout.BeginVertical("box", GUILayout.MinWidth(700));

            // 경고 메시지 - 크게
            GUILayout.Label($"<size=18><color=#00BFFF>{L("PerformanceWarning")}</color></size>", _descriptionStyle);
            GUILayout.Space(15);

            // 리셋 버튼 - 크게
            if (GUILayout.Button($"<size=18><color=#FFFF00>{L("ResetPerformanceToDefault")}</color></size>", GUILayout.Width(350), GUILayout.Height(45)))
            {
                Main.Settings.MaxEnemiesToAnalyze = 8;
                Main.Settings.MaxPositionsToEvaluate = 25;
                Main.Settings.MaxClusters = 5;
                Main.Settings.MaxTilesPerEnemy = 100;
            }
            GUILayout.Space(20);

            // 슬라이더 옵션들 - 크게
            Main.Settings.MaxEnemiesToAnalyze = DrawSliderSettingIntLarge(
                L("MaxEnemiesToAnalyze"),
                L("MaxEnemiesToAnalyzeDesc"),
                Main.Settings.MaxEnemiesToAnalyze,
                3, 20);

            Main.Settings.MaxPositionsToEvaluate = DrawSliderSettingIntLarge(
                L("MaxPositionsToEvaluate"),
                L("MaxPositionsToEvaluateDesc"),
                Main.Settings.MaxPositionsToEvaluate,
                10, 50);

            Main.Settings.MaxClusters = DrawSliderSettingIntLarge(
                L("MaxClusters"),
                L("MaxClustersDesc"),
                Main.Settings.MaxClusters,
                2, 10);

            Main.Settings.MaxTilesPerEnemy = DrawSliderSettingIntLarge(
                L("MaxTilesPerEnemy"),
                L("MaxTilesPerEnemyDesc"),
                Main.Settings.MaxTilesPerEnemy,
                30, 200);

            GUILayout.EndVertical();
        }

        /// <summary>
        /// ★ v3.5.21: 큰 폰트 슬라이더 (성능 설정용)
        /// </summary>
        private static int DrawSliderSettingIntLarge(string label, string description, int value, int min, int max)
        {
            GUILayout.BeginVertical();

            // 라벨 - 큰 폰트
            GUILayout.Label($"<size=18><b>{label}</b>: <color=#00FF00>{value}</color></size>", _boldLabelStyle);

            // 설명 - 중간 폰트
            GUILayout.Label($"<size=16><color=#888888>{description}</color></size>", _descriptionStyle);
            GUILayout.Space(5);

            // 슬라이더 - 넓게
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<size=16>{min}</size>", GUILayout.Width(40));
            value = (int)GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(500), GUILayout.Height(25));
            GUILayout.Label($"<size=16>{max}</size>", GUILayout.Width(50));
            GUILayout.EndHorizontal();

            GUILayout.Space(15);
            GUILayout.EndVertical();

            return value;
        }

        private static bool DrawCheckbox(bool value, string label)
        {
            GUILayout.BeginHorizontal();
            string checkIcon = value ? "<size=22><b><color=green>☑</color></b></size>" : "<size=22><b>☐</b></size>";

            if (GUILayout.Button(checkIcon, GUI.skin.box, GUILayout.Width(CHECKBOX_SIZE), GUILayout.Height(CHECKBOX_SIZE)))
                value = !value;

            GUILayout.Space(10);
            if (GUILayout.Button($"<size=16>{label}</size>", GUI.skin.label, GUILayout.Height(CHECKBOX_SIZE)))
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
            string checkIcon = settings.EnableCustomAI ? "<size=20><b><color=green>☑</color></b></size>" : "<size=20><b>☐</b></size>";
            if (GUILayout.Button(checkIcon, GUI.skin.box, GUILayout.Width(CHECKBOX_SIZE), GUILayout.Height(CHECKBOX_SIZE)))
            {
                settings.EnableCustomAI = !settings.EnableCustomAI;
                Main.Settings.SaveCharacterSettings();  // ★ v3.5.89: 세이브에 저장
            }

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
            GUILayout.Space(15);
            DrawAdvancedSettings();
            GUILayout.Space(10);
        }

        /// <summary>
        /// ★ v3.5.13: 고급 설정 섹션 (경고 + 리셋 버튼 포함)
        /// </summary>
        private static void DrawAdvancedSettings()
        {
            if (_editingSettings == null) return;

            // 접기/펴기 버튼
            string toggleText = _showAdvancedSettings
                ? $"<color=#FFA500>▼ {L("AdvancedSettings")}</color>"
                : $"<color=#888888>▶ {L("AdvancedSettings")}</color>";

            if (GUILayout.Button(toggleText, GUI.skin.label, GUILayout.Height(30)))
                _showAdvancedSettings = !_showAdvancedSettings;

            if (!_showAdvancedSettings) return;

            GUILayout.BeginVertical("box");

            // 경고 메시지
            GUILayout.Label($"<color=#FF6600>{L("AdvancedWarning")}</color>", _descriptionStyle);
            GUILayout.Space(10);

            // 리셋 버튼
            if (GUILayout.Button($"<color=#FFFF00>{L("ResetToDefault")}</color>", GUILayout.Width(200), GUILayout.Height(35)))
            {
                _editingSettings.MinSafeDistance = 7.0f;
                _editingSettings.HealAtHPPercent = 50;
                _editingSettings.MinEnemiesForAoE = 2;
                _editingSettings.UseKillSimulator = true;
                _editingSettings.UseAoEOptimization = true;
                _editingSettings.UsePredictiveMovement = true;
            }
            GUILayout.Space(15);

            // 토글 옵션들
            _editingSettings.UseKillSimulator = DrawCheckbox(_editingSettings.UseKillSimulator, L("UseKillSimulator"));
            GUILayout.Label($"<color=#888888><size=14>{L("UseKillSimulatorDesc")}</size></color>", _descriptionStyle);
            GUILayout.Space(8);

            _editingSettings.UseAoEOptimization = DrawCheckbox(_editingSettings.UseAoEOptimization, L("UseAoEOptimization"));
            GUILayout.Label($"<color=#888888><size=14>{L("UseAoEOptimizationDesc")}</size></color>", _descriptionStyle);
            GUILayout.Space(8);

            _editingSettings.UsePredictiveMovement = DrawCheckbox(_editingSettings.UsePredictiveMovement, L("UsePredictiveMovement"));
            GUILayout.Label($"<color=#888888><size=14>{L("UsePredictiveMovementDesc")}</size></color>", _descriptionStyle);
            GUILayout.Space(15);

            // 슬라이더 옵션들
            _editingSettings.MinSafeDistance = DrawSliderSetting(
                L("MinSafeDistance"),
                L("MinSafeDistanceDesc"),
                _editingSettings.MinSafeDistance,
                3f, 15f, "0.0", "m");

            _editingSettings.HealAtHPPercent = DrawSliderSettingInt(
                L("HealAtHPPercent"),
                L("HealAtHPPercentDesc"),
                _editingSettings.HealAtHPPercent,
                20, 80, "%");

            _editingSettings.MinEnemiesForAoE = DrawSliderSettingInt(
                L("MinEnemiesForAoE"),
                L("MinEnemiesForAoEDesc"),
                _editingSettings.MinEnemiesForAoE,
                1, 5, "");

            GUILayout.EndVertical();
        }

        private static float DrawSliderSetting(string label, string desc, float value, float min, float max, string format, string suffix)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>{label}:</b>", _boldLabelStyle, GUILayout.Width(180));
            float newValue = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(200));
            GUILayout.Label($"<color=#00FF00>{newValue.ToString(format)}{suffix}</color>", GUILayout.Width(80));
            GUILayout.EndHorizontal();
            GUILayout.Label($"<color=#888888><size=14>{desc}</size></color>", _descriptionStyle);
            GUILayout.Space(10);
            return newValue;
        }

        private static int DrawSliderSettingInt(string label, string desc, int value, int min, int max, string suffix)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>{label}:</b>", _boldLabelStyle, GUILayout.Width(180));
            int newValue = (int)GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(200));
            GUILayout.Label($"<color=#00FF00>{newValue}{suffix}</color>", GUILayout.Width(80));
            GUILayout.EndHorizontal();
            GUILayout.Label($"<color=#888888><size=14>{desc}</size></color>", _descriptionStyle);
            GUILayout.Space(10);
            return newValue;
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

                if (GUILayout.Toggle(isSelected, buttonText, GUI.skin.button, GUILayout.Width(ROLE_BUTTON_WIDTH), GUILayout.Height(BUTTON_HEIGHT)) && !isSelected)
                {
                    _editingSettings.Role = role;
                    Main.Settings.SaveCharacterSettings();  // ★ v3.5.89: 세이브에 저장
                }
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

                if (GUILayout.Toggle(isSelected, buttonText, GUI.skin.button, GUILayout.Width(RANGE_BUTTON_WIDTH), GUILayout.Height(BUTTON_HEIGHT)) && !isSelected)
                {
                    _editingSettings.RangePreference = pref;
                    Main.Settings.SaveCharacterSettings();  // ★ v3.5.89: 세이브에 저장
                }
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
