using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using UnityEngine;
using CompanionAI_v3.Data;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.UI
{
    /// <summary>
    /// v3.0: 메인 UI — Imperial Dark tab system (6 tabs)
    /// </summary>
    public static class MainUI
    {
        // ── Tab system ───────────────────────────────────────────
        private enum UITab { Party, Gameplay, Combat, Performance, Language, Debug, MachineSpirit }
        private static UITab _activeTab = UITab.Party;

        private static readonly (UITab tab, string locKey)[] TabDefs = new[]
        {
            (UITab.Party,       "TabParty"),
            (UITab.Gameplay,    "TabGameplay"),
            (UITab.Combat,      "TabCombat"),
            (UITab.Performance, "TabPerformance"),
            (UITab.Language,    "TabLanguage"),
            (UITab.Debug,       "TabDebug"),
            (UITab.MachineSpirit, "TabMachineSpirit"),
        };

        // ── State ────────────────────────────────────────────────
        private static string _selectedCharacterId = "";
        private static CharacterSettings _editingSettings = null;
        private static Vector2 _scrollPosition = Vector2.zero;
        private static bool _showAdvancedSettings = false;  // ★ v3.5.13: 고급 설정 접기/펴기

        // ── Scaled dimensions ────────────────────────────────────
        private static float CHECKBOX_SIZE     => UIStyles.Sd(34f);
        private static float BUTTON_HEIGHT     => UIStyles.Sd(34f);
        private static float ROLE_BUTTON_WIDTH => UIStyles.Sd(80f);
        private static float RANGE_BUTTON_WIDTH => UIStyles.Sd(100f);
        private static float CHAR_NAME_WIDTH   => UIStyles.Sd(120f);
        private static float ROLE_LABEL_WIDTH  => UIStyles.Sd(80f);
        private static float RANGE_LABEL_WIDTH => UIStyles.Sd(107f);
        private static float LANG_BUTTON_WIDTH => UIStyles.Sd(100f);
        private static float CHECKBOX_DRAW_SIZE => UIStyles.Sd(28f);

        private static string L(string key) => Localization.Get(key);

        // ═════════════════════════════════════════════════════════
        // OnGUI
        // ═════════════════════════════════════════════════════════

        public static void OnGUI()
        {
            Localization.CurrentLanguage = Main.Settings.UILanguage;
            UIStyles.InitOnce(Main.Settings.UIScale);
            try
            {
                _scrollPosition = GUILayout.BeginScrollView(_scrollPosition, GUILayout.Height(UIStyles.Sd(467)));
                GUILayout.BeginVertical(UIStyles.Background);
                DrawHeader();
                DrawTabBar();
                GUILayout.Space(5);
                DrawTabContent();
                GUILayout.EndVertical();
                GUILayout.EndScrollView();
            }
            catch (Exception ex)
            {
                GUILayout.Label($"<color={UIStyles.Danger}>UI Error: {ex.Message}</color>");
            }
        }

        // ═════════════════════════════════════════════════════════
        // Header + Tabs
        // ═════════════════════════════════════════════════════════

        private static void DrawHeader()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<color={UIStyles.Gold}>COMPANION AI</color>", UIStyles.Header);
            GUILayout.FlexibleSpace();
            GUILayout.Label($"<color={UIStyles.TextDim}>v{GetVersion()}</color>", UIStyles.Label);
            GUILayout.EndHorizontal();
            GUILayout.Label($"<color={UIStyles.TextMid}>TurnPlanner-based Tactical AI System</color>", UIStyles.Description);
            GUILayout.Space(5);
        }

        private static string GetVersion()
        {
            try { return UnityModManagerNet.UnityModManager.FindMod("CompanionAI_v3")?.Info?.Version ?? "?"; }
            catch { return "?"; }
        }

        private static void DrawTabBar()
        {
            GUILayout.BeginHorizontal();
            foreach (var (tab, locKey) in TabDefs)
            {
                var style = (_activeTab == tab) ? UIStyles.TabActive : UIStyles.TabInactive;
                if (GUILayout.Button(L(locKey), style, GUILayout.Height(UIStyles.Sd(24))))
                    _activeTab = tab;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private static void DrawTabContent()
        {
            GUILayout.BeginVertical(UIStyles.SectionBox);
            switch (_activeTab)
            {
                case UITab.Party:       DrawPartyTab(); break;
                case UITab.Gameplay:    DrawGameplayTab(); break;
                case UITab.Combat:      DrawCombatTab(); break;
                case UITab.Performance: DrawPerformanceTab(); break;
                case UITab.Language:    DrawLanguageTab(); break;
                case UITab.Debug:       DrawDebugTab(); break;
                case UITab.MachineSpirit: DrawMachineSpiritTab(); break;
            }
            GUILayout.EndVertical();
        }

        // ═════════════════════════════════════════════════════════
        // Party Tab
        // ═════════════════════════════════════════════════════════

        private static void DrawPartyTab()
        {
            UIStyles.SectionTitle(L("PartyMembers"));
            UIStyles.DrawDivider();
            GUILayout.Space(5);

            var characters = GetPartyMembers();
            if (characters.Count == 0)
            {
                GUILayout.Label($"<color={UIStyles.TextMid}><i>{L("NoCharacters")}</i></color>", UIStyles.Description);
                return;
            }

            // Header row
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<color={UIStyles.TextLight}><b>{L("AI")}</b></color>", UIStyles.Label, GUILayout.Width(UIStyles.Sd(40)));
            GUILayout.Label($"<color={UIStyles.TextLight}><b>{L("Character")}</b></color>", UIStyles.Label, GUILayout.Width(CHAR_NAME_WIDTH));
            GUILayout.Label($"<color={UIStyles.TextLight}><b>{L("Role")}</b></color>", UIStyles.Label, GUILayout.Width(ROLE_LABEL_WIDTH));
            GUILayout.Label($"<color={UIStyles.TextLight}><b>{L("Range")}</b></color>", UIStyles.Label, GUILayout.Width(RANGE_LABEL_WIDTH));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(10);

            foreach (var character in characters)
                DrawCharacterRow(character);
        }

        // ═════════════════════════════════════════════════════════
        // Gameplay Tab
        // ═════════════════════════════════════════════════════════

        private static void DrawGameplayTab()
        {
            UIStyles.SectionTitle(L("GameplaySettings"));
            UIStyles.DrawDivider();
            GUILayout.Space(5);

            // UI Scale
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<color={UIStyles.TextLight}>{L("UIScale")}</color>", UIStyles.BoldLabel, GUILayout.Width(UIStyles.Sd(120)));
            Main.Settings.UIScale = GUILayout.HorizontalSlider(Main.Settings.UIScale, 0.8f, 2.5f, GUILayout.Width(UIStyles.Sd(200)), GUILayout.Height(UIStyles.Sd(15)));
            Main.Settings.UIScale = Mathf.Round(Main.Settings.UIScale * 10f) / 10f; // snap to 0.1
            GUILayout.Label($"<color={UIStyles.Gold}>{Main.Settings.UIScale:F1}x</color>", UIStyles.Label, GUILayout.Width(UIStyles.Sd(40)));
            GUILayout.EndHorizontal();
            GUILayout.Space(5);
            UIStyles.DrawDivider();
            GUILayout.Space(5);

            // AI Speech
            Main.Settings.EnableAISpeech = DrawCheckbox(Main.Settings.EnableAISpeech, L("EnableAISpeech"));

            if (Main.Settings.EnableAISpeech)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(UIStyles.Sd(37));
                if (GUILayout.Button($"<color={UIStyles.TextLight}>{L("ReloadDialogue")}</color>", UIStyles.Button, GUILayout.Width(UIStyles.Sd(167)), GUILayout.Height(UIStyles.Sd(20))))
                {
                    DialogueLocalization.ReloadFromJson();
                    Diagnostics.TacticalDialogueDB.ReloadFromJson();
                }
                GUILayout.EndHorizontal();
            }

            // Victory Bark
            Main.Settings.EnableVictoryBark = DrawCheckbox(Main.Settings.EnableVictoryBark, L("EnableVictoryBark"));

            GUILayout.Space(10);
            UIStyles.DrawDivider();
            GUILayout.Space(5);

            // Allied NPC AI
            Main.Settings.EnableAlliedNPCAI = DrawCheckbox(Main.Settings.EnableAlliedNPCAI, L("EnableAlliedNPCAI"));
            GUILayout.Label($"<color={UIStyles.Danger}>{L("EnableAlliedNPCAIDesc")}</color>", UIStyles.Description);

            // Ship Combat AI
            Main.Settings.EnableShipCombatAI = DrawCheckbox(Main.Settings.EnableShipCombatAI, L("EnableShipCombatAI"));
            GUILayout.Label($"<color={UIStyles.Danger}>{L("EnableShipCombatAIDesc")}</color>", UIStyles.Description);
        }

        // ═════════════════════════════════════════════════════════
        // Combat Tab
        // ═════════════════════════════════════════════════════════

        private static void DrawCombatTab()
        {
            UIStyles.SectionTitle(L("CombatSettings"));
            UIStyles.DrawDivider();
            GUILayout.Space(5);

            // ── AoE Section ──────────────────────────────────────
            GUILayout.Label($"<color={UIStyles.Gold}>{L("AoESettings")}</color>", UIStyles.BoldLabel);
            GUILayout.Space(5);

            var aoeConfig = AIConfig.GetAoEConfig();
            if (aoeConfig != null)
            {
                if (GUILayout.Button($"<color={UIStyles.Gold}>{L("ResetAoEToDefault")}</color>", UIStyles.Button, GUILayout.Width(UIStyles.Sd(234)), GUILayout.Height(CHECKBOX_DRAW_SIZE)))
                {
                    AIConfig.Instance.AoE = new AoEConfig();
                    AIConfig.Save();
                }
                GUILayout.Space(10);

                bool aoeChanged = false;
                int iv;

                iv = DrawSliderSettingIntLarge(L("MaxPlayerAlliesHit"), L("MaxPlayerAlliesHitDesc"), aoeConfig.MaxPlayerAlliesHit, 0, 3);
                if (iv != aoeConfig.MaxPlayerAlliesHit) { aoeConfig.MaxPlayerAlliesHit = iv; aoeChanged = true; }

                iv = DrawSliderSettingIntLarge(L("CfgMinClusterSize"), L("CfgMinClusterSizeDesc"), aoeConfig.MinClusterSize, 1, 5);
                if (iv != aoeConfig.MinClusterSize) { aoeConfig.MinClusterSize = iv; aoeChanged = true; }

                if (aoeChanged) AIConfig.Save();
            }

            GUILayout.Space(10);
            UIStyles.DrawDivider();
            GUILayout.Space(5);

            // ── Weapon Rotation Section ──────────────────────────
            GUILayout.Label($"<color={UIStyles.Gold}>{L("WeaponRotationSettings")}</color>", UIStyles.BoldLabel);
            GUILayout.Space(5);

            var wrConfig = AIConfig.GetWeaponRotationConfig();
            if (wrConfig != null)
            {
                GUILayout.Label($"<color={UIStyles.TextMid}>{L("WeaponRotationWarning")}</color>", UIStyles.Description);
                GUILayout.Space(5);

                if (GUILayout.Button($"<color={UIStyles.Gold}>{L("ResetWeaponRotationToDefault")}</color>", UIStyles.Button, GUILayout.Width(UIStyles.Sd(234)), GUILayout.Height(CHECKBOX_DRAW_SIZE)))
                {
                    wrConfig.MaxSwitchesPerTurn = new WeaponRotationConfig().MaxSwitchesPerTurn;
                    AIConfig.Save();
                }
                GUILayout.Space(10);

                int iv = DrawSliderSettingIntLarge(L("MaxSwitchesPerTurn"), L("MaxSwitchesPerTurnDesc"), wrConfig.MaxSwitchesPerTurn, 1, 4);
                if (iv != wrConfig.MaxSwitchesPerTurn)
                {
                    wrConfig.MaxSwitchesPerTurn = iv;
                    AIConfig.Save();
                }
            }
        }

        // ═════════════════════════════════════════════════════════
        // Performance Tab
        // ═════════════════════════════════════════════════════════

        private static void DrawPerformanceTab()
        {
            UIStyles.SectionTitle(L("PerformanceSettings"));
            UIStyles.DrawDivider();
            GUILayout.Space(5);

            GUILayout.Label($"<color={UIStyles.Danger}>{L("PerformanceWarning")}</color>", UIStyles.Description);
            GUILayout.Space(10);

            if (GUILayout.Button($"<color={UIStyles.Gold}>{L("ResetPerformanceToDefault")}</color>", UIStyles.Button, GUILayout.Width(UIStyles.Sd(234)), GUILayout.Height(CHECKBOX_DRAW_SIZE)))
            {
                Main.Settings.MaxEnemiesToAnalyze = 8;
                Main.Settings.MaxPositionsToEvaluate = 25;
                Main.Settings.MaxClusters = 5;
                Main.Settings.MaxTilesPerEnemy = 100;
            }
            GUILayout.Space(15);

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
        }

        // ═════════════════════════════════════════════════════════
        // Language Tab
        // ═════════════════════════════════════════════════════════

        private static void DrawLanguageTab()
        {
            UIStyles.SectionTitle(L("Language"));
            UIStyles.DrawDivider();
            GUILayout.Space(5);

            foreach (Language lang in Enum.GetValues(typeof(Language)))
            {
                string langName;
                switch (lang)
                {
                    case Language.English:  langName = "English";  break;
                    case Language.Korean:   langName = "한국어";    break;
                    case Language.Russian:  langName = "Русский";  break;
                    case Language.Japanese: langName = "日本語";    break;
                    default:               langName = lang.ToString(); break;
                }

                bool isSelected = Main.Settings.UILanguage == lang;
                var style = isSelected ? UIStyles.TabActive : UIStyles.Button;

                if (GUILayout.Button(langName, style, GUILayout.Width(UIStyles.Sd(200)), GUILayout.Height(UIStyles.Sd(34))))
                {
                    Main.Settings.UILanguage = lang;
                    Localization.CurrentLanguage = lang;
                    Diagnostics.TacticalDialogueDB.ReloadFromJson();
                }
                GUILayout.Space(5);
            }
        }

        // ═════════════════════════════════════════════════════════
        // Debug Tab
        // ═════════════════════════════════════════════════════════

        private static void DrawDebugTab()
        {
            UIStyles.SectionTitle(L("DebugDiagnostics"));
            UIStyles.DrawDivider();
            GUILayout.Space(5);

            GUILayout.Label($"<color={UIStyles.TextMid}>{L("DebugDiagnosticsDesc")}</color>", UIStyles.Description);
            GUILayout.Space(10);

            Main.Settings.EnableDebugLogging = DrawCheckbox(Main.Settings.EnableDebugLogging, L("EnableDebugLogging"));
            GUILayout.Space(5);
            Main.Settings.ShowAIThoughts = DrawCheckbox(Main.Settings.ShowAIThoughts, L("ShowAIDecisionLog"));

            GUILayout.Space(10);
            UIStyles.DrawDivider();
            GUILayout.Space(5);

            // Combat Report
            Main.Settings.EnableCombatReport = DrawCheckbox(Main.Settings.EnableCombatReport, L("EnableCombatReport"));
            GUILayout.Label($"<color={UIStyles.TextMid}>{L("EnableCombatReportDesc")}</color>", UIStyles.Description);

            GUILayout.Space(10);

            // Tactical Narrator / Decision Overlay
            Main.Settings.EnableDecisionOverlay = DrawCheckbox(Main.Settings.EnableDecisionOverlay, L("EnableDecisionOverlay"));
            GUILayout.Label($"<color={UIStyles.TextMid}>{L("EnableDecisionOverlayDesc")}</color>", UIStyles.Description);
            if (Main.Settings.EnableDecisionOverlay)
            {
                GUILayout.Space(5);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"<color={UIStyles.TextLight}>    {L("OverlayScale")}: {Main.Settings.DecisionOverlayScale:F1}x</color>", UIStyles.SliderLabel, GUILayout.Width(UIStyles.Sd(134)));
                Main.Settings.DecisionOverlayScale = GUILayout.HorizontalSlider(
                    Main.Settings.DecisionOverlayScale, 0.8f, 2.0f, GUILayout.Width(UIStyles.Sd(134)));
                GUILayout.EndHorizontal();
            }
        }

        // ═════════════════════════════════════════════════════════
        // Machine Spirit Tab
        // ═════════════════════════════════════════════════════════

        private static void DrawMachineSpiritTab()
        {
            var ms = Main.Settings.MachineSpirit;

            // Description
            UIStyles.SectionTitle(L("TabMachineSpirit"));
            GUILayout.Label($"<color={UIStyles.TextMid}>{L("MSDescription")}</color>", UIStyles.Description);
            UIStyles.DrawDivider();
            GUILayout.Space(5);

            // Enable toggle
            ms.Enabled = DrawCheckbox(ms.Enabled, L("MSEnabled"));
            GUILayout.Space(10);
            UIStyles.DrawDivider();
            GUILayout.Space(5);

            // API Settings (only show if enabled)
            if (ms.Enabled)
            {
                // API URL
                GUILayout.BeginHorizontal();
                GUILayout.Label($"<color={UIStyles.TextLight}>{L("MSApiUrl")}</color>", UIStyles.BoldLabel, GUILayout.Width(UIStyles.Sd(120)));
                ms.ApiUrl = GUILayout.TextField(ms.ApiUrl, GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();
                GUILayout.Space(5);

                // API Key (masked)
                GUILayout.BeginHorizontal();
                GUILayout.Label($"<color={UIStyles.TextLight}>{L("MSApiKey")}</color>", UIStyles.BoldLabel, GUILayout.Width(UIStyles.Sd(120)));
                ms.ApiKey = GUILayout.PasswordField(ms.ApiKey, '*', GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();
                GUILayout.Space(5);

                // Model
                GUILayout.BeginHorizontal();
                GUILayout.Label($"<color={UIStyles.TextLight}>{L("MSModel")}</color>", UIStyles.BoldLabel, GUILayout.Width(UIStyles.Sd(120)));
                ms.Model = GUILayout.TextField(ms.Model, GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();
                GUILayout.Space(10);
                UIStyles.DrawDivider();
                GUILayout.Space(5);

                // Max Tokens slider (50-500)
                GUILayout.BeginHorizontal();
                GUILayout.Label($"<color={UIStyles.TextLight}>{L("MSMaxTokens")}</color>", UIStyles.BoldLabel, GUILayout.Width(UIStyles.Sd(120)));
                ms.MaxTokens = Mathf.RoundToInt(GUILayout.HorizontalSlider(ms.MaxTokens, 50, 500, GUILayout.Width(UIStyles.Sd(200)), GUILayout.Height(UIStyles.Sd(15))));
                GUILayout.Label($"<color={UIStyles.Gold}>{ms.MaxTokens}</color>", UIStyles.Label, GUILayout.Width(UIStyles.Sd(50)));
                GUILayout.EndHorizontal();
                GUILayout.Space(5);

                // Temperature slider (0.0-2.0)
                GUILayout.BeginHorizontal();
                GUILayout.Label($"<color={UIStyles.TextLight}>{L("MSTemperature")}</color>", UIStyles.BoldLabel, GUILayout.Width(UIStyles.Sd(120)));
                ms.Temperature = GUILayout.HorizontalSlider(ms.Temperature, 0f, 2f, GUILayout.Width(UIStyles.Sd(200)), GUILayout.Height(UIStyles.Sd(15)));
                ms.Temperature = Mathf.Round(ms.Temperature * 10f) / 10f;
                GUILayout.Label($"<color={UIStyles.Gold}>{ms.Temperature:F1}</color>", UIStyles.Label, GUILayout.Width(UIStyles.Sd(50)));
                GUILayout.EndHorizontal();
                GUILayout.Space(5);

                // Hotkey display
                GUILayout.BeginHorizontal();
                GUILayout.Label($"<color={UIStyles.TextLight}>{L("MSHotkey")}</color>", UIStyles.BoldLabel, GUILayout.Width(UIStyles.Sd(120)));
                GUILayout.Label($"<color={UIStyles.Gold}>{ms.Hotkey}</color>", UIStyles.Label);
                GUILayout.EndHorizontal();
                GUILayout.Space(10);
                UIStyles.DrawDivider();
                GUILayout.Space(5);

                // Test Connection button
                if (GUILayout.Button($"<color={UIStyles.TextLight}>{L("MSTestConnection")}</color>", UIStyles.Button, GUILayout.Width(UIStyles.Sd(180)), GUILayout.Height(BUTTON_HEIGHT)))
                {
                    CompanionAI_v3.MachineSpirit.MachineSpirit.OnUserMessage("Hello, Machine Spirit. Respond with a brief greeting.");
                }
            }
        }

        // ═════════════════════════════════════════════════════════
        // Shared Controls
        // ═════════════════════════════════════════════════════════

        private static bool DrawCheckbox(bool value, string label)
        {
            GUILayout.BeginHorizontal();
            string checkIcon = value
                ? $"<color={UIStyles.Gold}>\u2611</color>"
                : $"<color={UIStyles.TextDim}>\u2610</color>";
            if (GUILayout.Button(checkIcon, UIStyles.Checkbox, GUILayout.Width(CHECKBOX_DRAW_SIZE), GUILayout.Height(CHECKBOX_DRAW_SIZE)))
                value = !value;
            GUILayout.Space(UIStyles.Sd(6));
            if (GUILayout.Button($"<color={UIStyles.TextLight}>{label}</color>", UIStyles.Label, GUILayout.Height(CHECKBOX_DRAW_SIZE)))
                value = !value;
            GUILayout.EndHorizontal();
            return value;
        }

        // ═════════════════════════════════════════════════════════
        // Character Row + Settings (Party Tab)
        // ═════════════════════════════════════════════════════════

        private static void DrawCharacterRow(CharacterInfo character)
        {
            var settings = Main.Settings.GetOrCreateSettings(character.Id, character.Name);

            GUILayout.BeginHorizontal(UIStyles.CharRow);

            // AI Toggle
            string checkIcon = settings.EnableCustomAI
                ? $"<color={UIStyles.Gold}>\u2611</color>"
                : $"<color={UIStyles.TextDim}>\u2610</color>";
            if (GUILayout.Button(checkIcon, UIStyles.Checkbox, GUILayout.Width(CHECKBOX_SIZE), GUILayout.Height(CHECKBOX_SIZE)))
            {
                settings.EnableCustomAI = !settings.EnableCustomAI;
                Main.Settings.SaveCharacterSettings();
            }

            // Character name
            bool isSelected = _selectedCharacterId == character.Id;
            string buttonText = isSelected
                ? $"<color={UIStyles.Gold}><b>\u25BC {character.Name}</b></color>"
                : $"<color={UIStyles.TextLight}>\u25B6 {character.Name}</color>";
            if (GUILayout.Button(buttonText, UIStyles.Button, GUILayout.Width(CHAR_NAME_WIDTH), GUILayout.Height(CHECKBOX_SIZE)))
            {
                if (isSelected) { _selectedCharacterId = ""; _editingSettings = null; }
                else { _selectedCharacterId = character.Id; _editingSettings = settings; }
            }

            // Role
            string roleColor = GetRoleColor(settings.Role);
            GUILayout.Label($"<color={roleColor}><b>{Localization.GetRoleName(settings.Role)}</b></color>", UIStyles.Label, GUILayout.Width(ROLE_LABEL_WIDTH), GUILayout.Height(CHECKBOX_SIZE));

            // Range
            GUILayout.Label($"<color={UIStyles.TextLight}>{Localization.GetRangeName(settings.RangePreference)}</color>", UIStyles.Label, GUILayout.Width(RANGE_LABEL_WIDTH), GUILayout.Height(CHECKBOX_SIZE));

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (isSelected && _editingSettings != null)
            {
                GUILayout.BeginVertical(UIStyles.SectionBox);
                DrawCharacterAISettings();
                GUILayout.EndVertical();
            }
        }

        private static string GetRoleColor(AIRole role) => role switch
        {
            AIRole.Tank => UIStyles.RoleBlue,
            AIRole.DPS => UIStyles.RoleRed,
            AIRole.Support => UIStyles.RoleGold,
            AIRole.Auto => UIStyles.RoleGreen,
            _ => UIStyles.TextLight
        };

        private static void DrawCharacterAISettings()
        {
            if (_editingSettings == null) return;

            GUILayout.Space(10);
            DrawRoleSelection();
            GUILayout.Space(15);
            DrawRangePreferenceSelection();
            GUILayout.Space(15);

            // Weapon Set Rotation — per-character toggle
            _editingSettings.EnableWeaponSetRotation = DrawCheckbox(_editingSettings.EnableWeaponSetRotation, L("EnableWeaponSetRotation"));
            GUILayout.Label($"<color={UIStyles.TextMid}>{L("EnableWeaponSetRotationDesc")}</color>", UIStyles.Description);
            GUILayout.Space(15);

            DrawAdvancedSettings();
            GUILayout.Space(10);
        }

        private static void DrawAdvancedSettings()
        {
            if (_editingSettings == null) return;

            string toggleText = _showAdvancedSettings
                ? $"<color={UIStyles.Gold}>\u25BC {L("AdvancedSettings")}</color>"
                : $"<color={UIStyles.TextMid}>\u25B6 {L("AdvancedSettings")}</color>";

            if (GUILayout.Button(toggleText, UIStyles.Label, GUILayout.Height(UIStyles.Sd(20))))
                _showAdvancedSettings = !_showAdvancedSettings;

            if (!_showAdvancedSettings) return;

            GUILayout.BeginVertical(UIStyles.SectionBox);

            // Warning
            GUILayout.Label($"<color={UIStyles.Danger}>{L("AdvancedWarning")}</color>", UIStyles.Description);
            GUILayout.Space(10);

            // Reset button
            if (GUILayout.Button($"<color={UIStyles.Gold}>{L("ResetToDefault")}</color>", UIStyles.Button, GUILayout.Width(UIStyles.Sd(134)), GUILayout.Height(UIStyles.Sd(24))))
            {
                _editingSettings.MinSafeDistance = 7.0f;
                _editingSettings.HealAtHPPercent = 50;
                _editingSettings.MinEnemiesForAoE = 2;
                _editingSettings.UseKillSimulator = true;
                _editingSettings.UseAoEOptimization = true;
                _editingSettings.UsePredictiveMovement = true;
                _editingSettings.EnableWeaponSetRotation = false;
            }
            GUILayout.Space(15);

            // Toggle options
            _editingSettings.UseKillSimulator = DrawCheckbox(_editingSettings.UseKillSimulator, L("UseKillSimulator"));
            GUILayout.Label($"<color={UIStyles.TextMid}>{L("UseKillSimulatorDesc")}</color>", UIStyles.Description);
            GUILayout.Space(8);

            _editingSettings.UseAoEOptimization = DrawCheckbox(_editingSettings.UseAoEOptimization, L("UseAoEOptimization"));
            GUILayout.Label($"<color={UIStyles.TextMid}>{L("UseAoEOptimizationDesc")}</color>", UIStyles.Description);
            GUILayout.Space(8);

            _editingSettings.UsePredictiveMovement = DrawCheckbox(_editingSettings.UsePredictiveMovement, L("UsePredictiveMovement"));
            GUILayout.Label($"<color={UIStyles.TextMid}>{L("UsePredictiveMovementDesc")}</color>", UIStyles.Description);
            GUILayout.Space(15);

            // Sliders
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

        // ═════════════════════════════════════════════════════════
        // Role / Range Selection
        // ═════════════════════════════════════════════════════════

        private static void DrawRoleSelection()
        {
            GUILayout.Label($"<color={UIStyles.TextLight}><b>{L("CombatRole")}</b></color>", UIStyles.BoldLabel);
            GUILayout.Label($"<color={UIStyles.TextMid}><i>{L("CombatRoleDesc")}</i></color>", UIStyles.Description);
            GUILayout.Space(8);

            GUILayout.BeginHorizontal();
            foreach (AIRole role in Enum.GetValues(typeof(AIRole)))
            {
                string roleColor = GetRoleColor(role);
                bool isSelected = _editingSettings.Role == role;
                string roleName = Localization.GetRoleName(role);
                string buttonText = isSelected
                    ? $"<color={roleColor}><b>{roleName}</b></color>"
                    : $"<color={UIStyles.TextMid}>{roleName}</color>";

                if (GUILayout.Toggle(isSelected, buttonText, UIStyles.Button, GUILayout.Width(ROLE_BUTTON_WIDTH), GUILayout.Height(BUTTON_HEIGHT)) && !isSelected)
                {
                    _editingSettings.Role = role;
                    Main.Settings.SaveCharacterSettings();
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(8);
            GUILayout.Label($"<color={UIStyles.TextMid}><i>{Localization.GetRoleDescription(_editingSettings.Role)}</i></color>", UIStyles.Description);
        }

        private static void DrawRangePreferenceSelection()
        {
            GUILayout.Label($"<color={UIStyles.TextLight}><b>{L("RangePreference")}</b></color>", UIStyles.BoldLabel);
            GUILayout.Label($"<color={UIStyles.TextMid}><i>{L("RangePreferenceDesc")}</i></color>", UIStyles.Description);
            GUILayout.Space(8);

            GUILayout.BeginHorizontal();
            foreach (RangePreference pref in Enum.GetValues(typeof(RangePreference)))
            {
                bool isSelected = _editingSettings.RangePreference == pref;
                string prefName = Localization.GetRangeName(pref);
                string buttonText = isSelected
                    ? $"<color={UIStyles.Gold}><b>{prefName}</b></color>"
                    : $"<color={UIStyles.TextMid}>{prefName}</color>";

                if (GUILayout.Toggle(isSelected, buttonText, UIStyles.Button, GUILayout.Width(RANGE_BUTTON_WIDTH), GUILayout.Height(BUTTON_HEIGHT)) && !isSelected)
                {
                    _editingSettings.RangePreference = pref;
                    Main.Settings.SaveCharacterSettings();
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(8);
            GUILayout.Label($"<color={UIStyles.TextMid}><i>{Localization.GetRangeDescription(_editingSettings.RangePreference)}</i></color>", UIStyles.Description);
        }

        // ═════════════════════════════════════════════════════════
        // Slider Helpers
        // ═════════════════════════════════════════════════════════

        private static float DrawSliderSetting(string label, string desc, float value, float min, float max, string format, string suffix)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>{label}:</b>", UIStyles.BoldLabel, GUILayout.Width(UIStyles.Sd(120)));
            float newValue = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(UIStyles.Sd(140)));
            GUILayout.Label($"<color={UIStyles.Gold}>{newValue.ToString(format)}{suffix}</color>", UIStyles.SliderLabel, GUILayout.Width(UIStyles.Sd(54)));
            GUILayout.EndHorizontal();
            GUILayout.Label($"<color={UIStyles.TextMid}>{desc}</color>", UIStyles.Description);
            GUILayout.Space(UIStyles.Sd(7));
            return newValue;
        }

        private static int DrawSliderSettingInt(string label, string desc, int value, int min, int max, string suffix)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>{label}:</b>", UIStyles.BoldLabel, GUILayout.Width(UIStyles.Sd(120)));
            int newValue = (int)GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(UIStyles.Sd(140)));
            GUILayout.Label($"<color={UIStyles.Gold}>{newValue}{suffix}</color>", UIStyles.SliderLabel, GUILayout.Width(UIStyles.Sd(54)));
            GUILayout.EndHorizontal();
            GUILayout.Label($"<color={UIStyles.TextMid}>{desc}</color>", UIStyles.Description);
            GUILayout.Space(UIStyles.Sd(7));
            return newValue;
        }

        private static int DrawSliderSettingIntLarge(string label, string description, int value, int min, int max)
        {
            GUILayout.BeginVertical();

            GUILayout.Label($"<b>{label}</b>: <color={UIStyles.Gold}>{value}</color>", UIStyles.BoldLabel);
            GUILayout.Label($"<color={UIStyles.TextMid}>{description}</color>", UIStyles.Description);
            GUILayout.Space(UIStyles.Sd(3));

            GUILayout.BeginHorizontal();
            GUILayout.Label($"<color={UIStyles.TextMid}>{min}</color>", UIStyles.SliderLabel, GUILayout.Width(UIStyles.Sd(27)));
            value = (int)GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(UIStyles.Sd(334)), GUILayout.Height(UIStyles.Sd(17)));
            GUILayout.Label($"<color={UIStyles.TextMid}>{max}</color>", UIStyles.SliderLabel, GUILayout.Width(UIStyles.Sd(34)));
            GUILayout.EndHorizontal();

            GUILayout.Space(UIStyles.Sd(10));
            GUILayout.EndVertical();

            return value;
        }

        private static float DrawSliderSettingFloatLarge(string label, string description, float value, float min, float max)
        {
            GUILayout.BeginVertical();

            GUILayout.Label($"<b>{label}</b>: <color={UIStyles.Gold}>{value:F0}</color>", UIStyles.BoldLabel);
            GUILayout.Label($"<color={UIStyles.TextMid}>{description}</color>", UIStyles.Description);
            GUILayout.Space(UIStyles.Sd(3));

            GUILayout.BeginHorizontal();
            GUILayout.Label($"<color={UIStyles.TextMid}>{min:F0}</color>", UIStyles.SliderLabel, GUILayout.Width(UIStyles.Sd(27)));
            value = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(UIStyles.Sd(334)), GUILayout.Height(UIStyles.Sd(17)));
            GUILayout.Label($"<color={UIStyles.TextMid}>{max:F0}</color>", UIStyles.SliderLabel, GUILayout.Width(UIStyles.Sd(34)));
            GUILayout.EndHorizontal();

            GUILayout.Space(UIStyles.Sd(10));
            GUILayout.EndVertical();

            return value;
        }

        // ═════════════════════════════════════════════════════════
        // Helpers
        // ═════════════════════════════════════════════════════════

        private static List<CharacterInfo> GetPartyMembers()
        {
            try
            {
                if (Game.Instance?.Player == null) return new List<CharacterInfo>();

                var partyMembers = Game.Instance.Player.PartyAndPets;
                if (partyMembers == null || partyMembers.Count == 0) return new List<CharacterInfo>();

                // ★ v3.7.15: 사역마(Familiar/Pet) 제외 - IsPet 체크
                return partyMembers
                    .Where(unit => unit != null)
                    .Where(unit => !unit.IsPet)
                    .Select(unit => new CharacterInfo { Id = unit.UniqueId ?? "unknown", Name = unit.CharacterName ?? "Unnamed", Unit = unit })
                    .ToList();
            }
            catch (Exception ex) { Main.LogDebug($"[MainUI] {ex.Message}"); return new List<CharacterInfo>(); }
        }

        private class CharacterInfo
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "Unknown";
            public BaseUnitEntity Unit { get; set; }
        }
    }
}
