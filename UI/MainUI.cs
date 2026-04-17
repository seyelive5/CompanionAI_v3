using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using UnityEngine;
using CompanionAI_v3.Data;
using CompanionAI_v3.Diagnostics;
using CompanionAI_v3.Settings;
using MSp = CompanionAI_v3.MachineSpirit;

namespace CompanionAI_v3.UI
{
    /// <summary>
    /// v3.0: 메인 UI — Imperial Dark tab system (8 tabs)
    /// </summary>
    public static class MainUI
    {
        // ── Tab system ───────────────────────────────────────────
        private enum UITab { Party, Gameplay, Combat, Performance, Language, Debug, MachineSpirit, LLMCombatAI }
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
            (UITab.LLMCombatAI,  "TabLLMCombatAI"),
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
                case UITab.LLMCombatAI:  DrawLLMCombatAITab(); break;
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
                    case Language.Chinese:  langName = "中文";      break;
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

            // ★ v3.109.0: LLM 시각 오버레이 (위협 랭킹 + 액션 프리뷰)
            Main.Settings.EnableLLMVisualOverlay = DrawCheckbox(Main.Settings.EnableLLMVisualOverlay, L("EnableLLMVisualOverlay"));
            GUILayout.Label($"<color={UIStyles.TextMid}>{L("EnableLLMVisualOverlayDesc")}</color>", UIStyles.Description);

            // ★ v3.84.0: LLM Developer Tools section
            GUILayout.Space(15);
            GUILayout.Label($"<color={UIStyles.RoleGold}>\u2500\u2500 {L("LLMDevTools")} \u2500\u2500</color>", UIStyles.BoldLabel);
            GUILayout.Label($"<color={UIStyles.Danger}>{L("LLMDevToolsWarning")}</color>", UIStyles.Description);
            GUILayout.Space(5);

            Main.Settings.EnableTrainingDataCollection = DrawCheckbox(
                Main.Settings.EnableTrainingDataCollection, L("EnableTrainingDataCollection"));
            GUILayout.Label($"<color={UIStyles.TextMid}>{L("TrainingDataCollectionDesc")}\n" +
                $"Buffered entries: {TrainingDataCollector.BufferCount}</color>", UIStyles.Description);
        }

        // ═════════════════════════════════════════════════════════
        // LLM Combat AI Tab
        // ═════════════════════════════════════════════════════════

        private static bool _llmCombatScannedOnce;
        private static bool _llmCombatInstallingModel;
        private static string _llmCombatInstallStatus = "";
        private static string _llmCombatPendingModelSelect;
        private static bool _llmCombatCatalogExpanded;  // 다운로드 카탈로그 펼침 상태
        private static string _llmCombatDeleteConfirm;   // ★ v3.94.0: 삭제 확인 대기 중인 모델

        private const string RECOMMENDED_JUDGE_MODEL = "gemma4:e4b";

        /// <summary>
        /// 다운로드 가능 모델 카탈로그 (Gemma 4 + Qwen 3.5 시리즈).
        /// Name: Ollama pull 이름 / SizeGB: 디스크 크기 / Desc: 설명
        /// </summary>
        private static readonly (string Name, float SizeGB, string Desc)[] DOWNLOADABLE_MODELS = new[]
        {
            // Gemma 4 series
            ("gemma4:e2b",    7.2f, "Gemma 4 E2B — lightweight, 128K context"),
            ("gemma4:e4b",    9.6f, "Gemma 4 E4B — recommended, 128K context"),
            ("gemma4:26b",   18.0f, "Gemma 4 26B-A4B MoE (Q4_K_M) — only 4B active at runtime, 256K context"),
            ("VladimirGav/gemma4-26b-16GB-VRAM", 14.0f, "Gemma 4 26B community build (IQ4_XS) — fits on 16GB VRAM"),
            // Qwen 3.5 series
            ("qwen3.5:0.8b",  1.0f, "Qwen 3.5 0.8B — very light, testing only"),
            ("qwen3.5:2b",    2.7f, "Qwen 3.5 2B — fast, low VRAM"),
            ("qwen3.5:4b",    3.4f, "Qwen 3.5 4B — balanced"),
            ("qwen3.5:9b",    6.6f, "Qwen 3.5 9B — higher quality"),
            ("qwen3.5:27b",  17.0f, "Qwen 3.5 27B — max quality, needs 20GB+ RAM"),
        };

        private static void DrawLLMCombatAITab()
        {
            // ★ v3.84.0: Experimental warning
            GUILayout.Label($"<color=#FF9900><b>\u26A0 {L("LLMCombatAIExperimental")}</b></color>", UIStyles.BoldLabel);
            GUILayout.Label($"<color=#AAAAAA>{L("LLMCombatAIExperimentalDesc")}</color>", UIStyles.Description);
            GUILayout.Space(10);

            UIStyles.SectionTitle(L("LLMCombatAITitle"));
            GUILayout.Label($"<color={UIStyles.TextMid}>{L("LLMCombatAIDesc")}</color>", UIStyles.Description);
            GUILayout.Space(5);

            // ══════════════════════════════════════════════════════
            // Section 1: Global Enable + Ollama Status
            // ══════════════════════════════════════════════════════

            Main.Settings.EnableLLMCombatAI = DrawCheckbox(Main.Settings.EnableLLMCombatAI, L("LLMCombatAIEnable"));
            GUILayout.Space(3);

            var ollamaState = MSp.OllamaSetup.State;
            bool ollamaReady = ollamaState == MSp.OllamaSetup.SetupState.Ready;

            if (!_llmCombatScannedOnce && !MSp.OllamaSetup.IsFetchingModels)
            {
                _llmCombatScannedOnce = true;
                MSp.CoroutineRunner.Start(MSp.OllamaSetup.FetchInstalledModels());
            }

            bool ollamaConnected = MSp.OllamaSetup.InstalledModels.Count > 0 || ollamaReady;
            string statusIcon = ollamaConnected ? $"<color={UIStyles.Green}>\u2713 Running</color>" : $"<color={UIStyles.Danger}>\u2717 Not Found</color>";
            GUILayout.Label($"<color={UIStyles.TextLight}>Ollama Status: </color>{statusIcon}", UIStyles.BoldLabel);

            if (!ollamaConnected)
            {
                GUILayout.Label($"<color={UIStyles.TextMid}>{L("LLMCombatAIOllamaHint")}</color>", UIStyles.Description);
            }

            if (!Main.Settings.EnableLLMCombatAI) return;

            UIStyles.DrawDivider();
            GUILayout.Space(5);

            // ══════════════════════════════════════════════════════
            // Section 2: Model Selection (installed models)
            // ══════════════════════════════════════════════════════

            GUILayout.Label($"<color={UIStyles.Gold}>{L("LLMCombatAIModel")}</color>", UIStyles.SubHeader);
            GUILayout.Space(3);

            // Auto-select after install completes
            if (_llmCombatPendingModelSelect != null && !_llmCombatInstallingModel)
            {
                Main.Settings.LLMJudgeModel = _llmCombatPendingModelSelect;
                _llmCombatPendingModelSelect = null;
            }

            string currentModel = Main.Settings.LLMJudgeModel;
            if (string.IsNullOrEmpty(currentModel)) currentModel = RECOMMENDED_JUDGE_MODEL;
            var installed = MSp.OllamaSetup.InstalledModels;
            bool isCurrentInstalled = false;
            for (int i = 0; i < installed.Count; i++)
            {
                if (installed[i].Name == currentModel) { isCurrentInstalled = true; break; }
            }
            string installedMark = isCurrentInstalled ? $"<color={UIStyles.Green}>\u2713 Installed</color>" : $"<color={UIStyles.Danger}>\u2717 Not Installed</color>";
            GUILayout.Label($"<color={UIStyles.TextLight}>{L("LLMCombatAICurrent")}: </color><color={UIStyles.Gold}>{currentModel}</color>  {installedMark}", UIStyles.BoldLabel);
            GUILayout.Space(3);

            GUILayout.Label($"<color={UIStyles.Gold}>\u2605 {L("LLMCombatAIRecommended")}</color>", UIStyles.Description);
            GUILayout.Label($"<color={UIStyles.TextMid}>{L("LLMCombatAIRecommendedDesc")}</color>", UIStyles.Description);
            GUILayout.Space(5);

            // Install progress indicator
            if (_llmCombatInstallingModel && !string.IsNullOrEmpty(_llmCombatInstallStatus))
            {
                GUILayout.Label($"<color={UIStyles.Gold}>{_llmCombatInstallStatus}</color>", UIStyles.BoldLabel);
                GUILayout.Space(3);
            }

            // Installed models list
            GUILayout.Label($"<color={UIStyles.TextLight}>{L("LLMCombatAIAvailable")}</color>", UIStyles.BoldLabel);
            GUILayout.Space(3);

            if (MSp.OllamaSetup.IsFetchingModels)
            {
                GUILayout.Label($"<color={UIStyles.TextMid}>Scanning installed models...</color>", UIStyles.Description);
            }
            else if (installed.Count == 0)
            {
                GUILayout.Label($"<color={UIStyles.TextDim}>{L("LLMCombatAINoModels")}</color>", UIStyles.Description);
            }
            else
            {
                var localFixedNames = new System.Collections.Generic.HashSet<string>();
                for (int i = 0; i < installed.Count; i++)
                {
                    string n = installed[i].Name;
                    if (!n.Contains("/")) localFixedNames.Add(n);
                }

                for (int i = 0; i < installed.Count; i++)
                {
                    var m = installed[i];

                    if (m.Name.Contains("/"))
                    {
                        string afterSlash = m.Name.Substring(m.Name.IndexOf('/') + 1);
                        if (afterSlash.Contains(":")) afterSlash = afterSlash.Substring(0, afterSlash.IndexOf(':'));
                        if (localFixedNames.Contains(afterSlash)) continue;
                    }

                    bool isActive = m.Name == Main.Settings.LLMJudgeModel || (string.IsNullOrEmpty(Main.Settings.LLMJudgeModel) && m.Name == RECOMMENDED_JUDGE_MODEL);
                    string sizeStr = m.SizeGB >= 1f ? $"{m.SizeGB:F1}GB" : $"{(int)(m.SizeGB * 1024)}MB";
                    bool isRecommended = m.Name == RECOMMENDED_JUDGE_MODEL;
                    string recTag = isRecommended ? $" \u2605 {L("LLMCombatAIRecTag")}" : "";

                    string selectLabel = isActive
                        ? $"<color={UIStyles.Gold}>\u25c9 {m.Name}  ({sizeStr}){recTag}</color>"
                        : $"<color={UIStyles.TextLight}>\u25cb {m.Name}  ({sizeStr}){recTag}</color>";

                    if (GUILayout.Button(selectLabel, UIStyles.Button, GUILayout.ExpandWidth(true), GUILayout.Height(BUTTON_HEIGHT)))
                    {
                        Main.Settings.LLMJudgeModel = m.Name;
                    }
                }
            }

            GUILayout.Space(5);

            // Refresh button
            if (GUILayout.Button($"<color={UIStyles.TextLight}>\u21bb {L("LLMCombatAIRefresh")}</color>",
                UIStyles.Button, GUILayout.Width(UIStyles.Sd(160)), GUILayout.Height(BUTTON_HEIGHT)))
            {
                MSp.CoroutineRunner.Start(MSp.OllamaSetup.FetchInstalledModels());
            }

            UIStyles.DrawDivider();
            GUILayout.Space(5);

            // ══════════════════════════════════════════════════════
            // Section 2b: Downloadable Model Catalog (Collapsible)
            // ══════════════════════════════════════════════════════

            DrawLLMCombatAIDownloadCatalog(ollamaConnected, installed);

            UIStyles.DrawDivider();
            GUILayout.Space(5);

            // ══════════════════════════════════════════════════════
            // Section 3: Character Selection
            // ══════════════════════════════════════════════════════

            GUILayout.Label($"<color={UIStyles.Gold}>{L("LLMCombatAIApplyTo")}</color>", UIStyles.SubHeader);
            GUILayout.Space(3);

            var characters = GetPartyMembers();
            if (characters.Count == 0)
            {
                GUILayout.Label($"<color={UIStyles.TextMid}><i>{L("NoCharacters")}</i></color>", UIStyles.Description);
            }
            else
            {
                bool allEnabled = true;
                for (int i = 0; i < characters.Count; i++)
                {
                    var cs = Main.Settings.GetOrCreateSettings(characters[i].Id, characters[i].Name);
                    if (!cs.EnableLLMJudge) { allEnabled = false; break; }
                }

                bool newAll = DrawCheckbox(allEnabled, L("LLMCombatAIAll"));
                if (newAll != allEnabled)
                {
                    for (int i = 0; i < characters.Count; i++)
                    {
                        var cs = Main.Settings.GetOrCreateSettings(characters[i].Id, characters[i].Name);
                        cs.EnableLLMJudge = newAll;
                    }
                    Main.Settings.SaveCharacterSettings();
                }

                for (int i = 0; i < characters.Count; i++)
                {
                    var ch = characters[i];
                    var cs = Main.Settings.GetOrCreateSettings(ch.Id, ch.Name);
                    string roleColor = GetRoleColor(cs.Role);
                    string roleName = Localization.GetRoleName(cs.Role);

                    GUILayout.BeginHorizontal();
                    GUILayout.Space(UIStyles.Sd(20));

                    bool newVal = DrawCheckbox(cs.EnableLLMJudge, $"{ch.Name}  <color={roleColor}>({roleName})</color>");
                    if (newVal != cs.EnableLLMJudge)
                    {
                        cs.EnableLLMJudge = newVal;
                        Main.Settings.SaveCharacterSettings();
                    }

                    GUILayout.EndHorizontal();
                }
            }

            UIStyles.DrawDivider();
            GUILayout.Space(5);

            // ── Display Options ──
            GUILayout.Label($"<color={UIStyles.Gold}>{L("LLMCombatAIDisplay")}</color>", UIStyles.SubHeader);
            GUILayout.Space(3);

            Main.Settings.ShowLLMOverlay = DrawCheckbox(Main.Settings.ShowLLMOverlay, L("LLMCombatAIOverlay"));
            GUILayout.Label($"<color={UIStyles.TextMid}>{L("LLMCombatAIOverlayDesc")}</color>", UIStyles.Description);

            UIStyles.DrawDivider();
            GUILayout.Space(5);

            // ── Statistics ──
            GUILayout.Label($"<color={UIStyles.Gold}>{L("LLMCombatAIStats")}</color>", UIStyles.SubHeader);
            GUILayout.Space(3);

            long lastTime = Planning.LLM.LLMJudge.LastJudgeTimeMs;
            if (lastTime > 0)
            {
                GUILayout.Label($"<color={UIStyles.TextLight}>{L("LLMCombatAILastTime")}: <color={UIStyles.Gold}>{lastTime}ms</color></color>", UIStyles.Label);
            }
            else
            {
                GUILayout.Label($"<color={UIStyles.TextDim}>{L("LLMCombatAINoStats")}</color>", UIStyles.Description);
            }
        }

        /// <summary>
        /// 다운로드 가능 모델 카탈로그 (Collapsible).
        /// 헤더를 클릭하면 접고/펼침. 펼친 상태에서는 표 형태로 모델 표시.
        /// </summary>
        private static void DrawLLMCombatAIDownloadCatalog(bool ollamaConnected, System.Collections.Generic.IReadOnlyList<MSp.OllamaSetup.InstalledModel> installed)
        {
            // ── Collapsible Header (clickable) ──
            string arrow = _llmCombatCatalogExpanded ? "\u25bc" : "\u25b6"; // ▼ : ▶
            string headerLabel = $"<color={UIStyles.Gold}>{arrow} \u2b07 Available to Download</color>  <color={UIStyles.TextDim}>(Gemma 4 / Qwen 3.5 — click to {(_llmCombatCatalogExpanded ? "collapse" : "expand")})</color>";

            if (GUILayout.Button(headerLabel, UIStyles.Button, GUILayout.ExpandWidth(true), GUILayout.Height(BUTTON_HEIGHT)))
            {
                _llmCombatCatalogExpanded = !_llmCombatCatalogExpanded;
            }

            if (!_llmCombatCatalogExpanded) return;

            GUILayout.Space(5);

            if (!ollamaConnected)
            {
                GUILayout.Label($"<color={UIStyles.Danger}>Ollama not running. Start Ollama first.</color>", UIStyles.Description);
                return;
            }

            // ── Table Header Row ── (gold for visibility)
            GUILayout.BeginHorizontal(UIStyles.SectionBox);
            GUILayout.Label($"<color={UIStyles.Gold}><b>Status</b></color>", UIStyles.Label, GUILayout.Width(UIStyles.Sd(50)));
            GUILayout.Label($"<color={UIStyles.Gold}><b>Model</b></color>", UIStyles.Label, GUILayout.Width(UIStyles.Sd(180)));
            GUILayout.Label($"<color={UIStyles.Gold}><b>Size</b></color>", UIStyles.Label, GUILayout.Width(UIStyles.Sd(60)));
            GUILayout.Label($"<color={UIStyles.Gold}><b>Description</b></color>", UIStyles.Label, GUILayout.ExpandWidth(true));
            GUILayout.Label($"<color={UIStyles.Gold}><b>Action</b></color>", UIStyles.Label, GUILayout.Width(UIStyles.Sd(110)));
            GUILayout.EndHorizontal();

            GUILayout.Space(2);

            // ── Table Rows ──
            for (int i = 0; i < DOWNLOADABLE_MODELS.Length; i++)
            {
                var entry = DOWNLOADABLE_MODELS[i];

                // Check if already installed
                bool alreadyInstalled = false;
                for (int j = 0; j < installed.Count; j++)
                {
                    if (installed[j].Name == entry.Name) { alreadyInstalled = true; break; }
                }

                bool isRec = entry.Name == RECOMMENDED_JUDGE_MODEL;
                bool isDeletePending = _llmCombatDeleteConfirm == entry.Name;

                // Cell row (alternate row tinting via SectionBox or plain)
                GUILayout.BeginHorizontal();

                // Status cell — 흰색에 가깝게
                string statusCell = alreadyInstalled
                    ? $"<color={UIStyles.Green}>\u2713</color>"
                    : $"<color=#FFFFFF>\u25a1</color>";
                GUILayout.Label(statusCell, UIStyles.Label, GUILayout.Width(UIStyles.Sd(50)));

                // Model name cell (short name + recommended star)
                string shortName = entry.Name.Contains("/")
                    ? entry.Name.Substring(entry.Name.IndexOf('/') + 1)
                    : entry.Name;
                string nameCell = isRec
                    ? $"<color={UIStyles.Gold}>{shortName} \u2605</color>"
                    : $"<color=#FFFFFF>{shortName}</color>";
                GUILayout.Label(nameCell, UIStyles.Label, GUILayout.Width(UIStyles.Sd(180)));

                // Size cell — 밝게
                GUILayout.Label($"<color=#E0E0E0>{entry.SizeGB:F1}GB</color>", UIStyles.Label, GUILayout.Width(UIStyles.Sd(60)));

                // Description cell — 흰색
                GUILayout.Label($"<color=#FFFFFF>{entry.Desc}</color>", UIStyles.Label, GUILayout.ExpandWidth(true));

                // Action cell — ★ v3.94.0: Download / Delete 분기
                if (alreadyInstalled)
                {
                    if (isDeletePending)
                    {
                        // 삭제 확인 모드: Yes / No
                        GUI.enabled = !MSp.OllamaSetup.IsDeletingModel && !_llmCombatInstallingModel;
                        if (GUILayout.Button($"<color={UIStyles.Danger}>Yes</color>", UIStyles.Button, GUILayout.Width(UIStyles.Sd(52)), GUILayout.Height(BUTTON_HEIGHT)))
                        {
                            _llmCombatDeleteConfirm = null;
                            MSp.CoroutineRunner.Start(MSp.OllamaSetup.DeleteModel(entry.Name));
                        }
                        if (GUILayout.Button("No", UIStyles.Button, GUILayout.Width(UIStyles.Sd(52)), GUILayout.Height(BUTTON_HEIGHT)))
                        {
                            _llmCombatDeleteConfirm = null;
                        }
                        GUI.enabled = true;
                    }
                    else
                    {
                        // 삭제 버튼
                        GUI.enabled = !MSp.OllamaSetup.IsDeletingModel && !_llmCombatInstallingModel;
                        if (GUILayout.Button($"<color={UIStyles.Danger}>\u2716 Delete</color>", UIStyles.Button, GUILayout.Width(UIStyles.Sd(110)), GUILayout.Height(BUTTON_HEIGHT)))
                        {
                            _llmCombatDeleteConfirm = entry.Name;
                        }
                        GUI.enabled = true;
                    }
                }
                else
                {
                    // 다운로드 버튼
                    GUI.enabled = !_llmCombatInstallingModel;
                    if (GUILayout.Button($"<color={UIStyles.Gold}>\u2b07 Download</color>", UIStyles.Button, GUILayout.Width(UIStyles.Sd(110)), GUILayout.Height(BUTTON_HEIGHT)))
                    {
                        _llmCombatInstallingModel = true;
                        _llmCombatPendingModelSelect = entry.Name;
                        MSp.CoroutineRunner.Start(LLMCombatInstallModel(entry.Name));
                    }
                    GUI.enabled = true;
                }

                GUILayout.EndHorizontal();
                GUILayout.Space(1);
            }
        }

        /// <summary>Install an Ollama model for LLM Combat AI (reuses same pattern as Machine Spirit)</summary>
        private static System.Collections.IEnumerator LLMCombatInstallModel(string modelId)
        {
            _llmCombatInstallStatus = $"Starting download: {modelId}...";

            string ollamaPath = "ollama";
            string localAppData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
            string localOllama = System.IO.Path.Combine(localAppData, "Programs", "Ollama", "ollama.exe");
            if (System.IO.File.Exists(localOllama)) ollamaPath = localOllama;

            System.Diagnostics.Process proc = null;
            bool startFailed = false;
            try
            {
                proc = new System.Diagnostics.Process();
                proc.StartInfo.FileName = ollamaPath;
                proc.StartInfo.Arguments = $"pull {modelId}";
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.RedirectStandardOutput = true;
                proc.StartInfo.RedirectStandardError = true;
                proc.StartInfo.CreateNoWindow = true;
                proc.Start();

                proc.BeginErrorReadLine();
                proc.ErrorDataReceived += (sender, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    string line = e.Data;
                    var match = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)%");
                    if (match.Success)
                        _llmCombatInstallStatus = $"Downloading {modelId}... {match.Groups[1].Value}%";
                    else if (line.Contains("success"))
                        _llmCombatInstallStatus = $"\u2713 {modelId} installed!";
                };

                proc.BeginOutputReadLine();
                proc.OutputDataReceived += (sender, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    var match = System.Text.RegularExpressions.Regex.Match(e.Data, @"(\d+)%");
                    if (match.Success)
                        _llmCombatInstallStatus = $"Downloading {modelId}... {match.Groups[1].Value}%";
                    else if (e.Data.Contains("success"))
                        _llmCombatInstallStatus = $"\u2713 {modelId} installed!";
                };
            }
            catch
            {
                startFailed = true;
                _llmCombatInstallStatus = "Failed to start Ollama. Is it installed?";
            }

            if (!startFailed && proc != null)
            {
                float deadline = Time.realtimeSinceStartup + 1800f;
                while (!proc.HasExited && Time.realtimeSinceStartup < deadline)
                    yield return new WaitForSeconds(0.5f);

                if (proc.HasExited && proc.ExitCode == 0)
                {
                    _llmCombatInstallStatus = $"\u2713 {modelId} installed!";
                    yield return new WaitForSeconds(2f);
                    MSp.CoroutineRunner.Start(MSp.OllamaSetup.FetchInstalledModels());
                }
                else
                {
                    _llmCombatInstallStatus = "Install failed. Check Ollama is running.";
                    yield return new WaitForSeconds(5f);
                }

                proc.Dispose();
            }
            else
            {
                yield return new WaitForSeconds(5f);
            }

            _llmCombatInstallingModel = false;
            _llmCombatInstallStatus = "";
            _llmCombatPendingModelSelect = null;
        }

        // ═════════════════════════════════════════════════════════
        // Machine Spirit Tab
        // ═════════════════════════════════════════════════════════

        private static int _mainProviderChoice = -1; // 0=Ollama, 1=Cloud
        private static int _selectedCloudIdx = -1;   // 0=Gemini, 1=Groq, 2=OpenAI, 3=Custom
        // _showAddModel removed — always visible now
        private static bool _showCustomize = true;    // foldout: "Customize" (default open)

        private static void DrawMachineSpiritTab()
        {
            var ms = Main.Settings.MachineSpirit;

            // Sync state on first draw
            if (_mainProviderChoice < 0)
            {
                if (ms.Provider == MSp.ApiProvider.Ollama)
                {
                    _mainProviderChoice = 0;
                    _selectedCloudIdx = 0;
                }
                else
                {
                    _mainProviderChoice = 1;
                    _selectedCloudIdx = (int)ms.Provider - 1;
                }
            }

            // ══════════════════════════════════════════════════════
            // Zone 1: Always visible — Enable + Provider + Model
            // ══════════════════════════════════════════════════════

            // Enable toggle
            ms.Enabled = DrawCheckbox(ms.Enabled, L("MSEnabled"));
            if (!ms.Enabled) return; // early out — nothing else to show

            UIStyles.DrawDivider();
            GUILayout.Space(3);

            // Provider selection: Ollama vs Cloud
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<color={UIStyles.TextLight}>{L("MSProvider")}</color>", UIStyles.BoldLabel, GUILayout.Width(UIStyles.Sd(120)));
            int newChoice = GUILayout.SelectionGrid(_mainProviderChoice,
                new[] { "\u2605 Ollama (Local, Free)", "Cloud / Custom" }, 2,
                UIStyles.Button, GUILayout.Height(BUTTON_HEIGHT));
            GUILayout.EndHorizontal();

            if (newChoice != _mainProviderChoice)
            {
                _mainProviderChoice = newChoice;
                if (newChoice == 0)
                {
                    ms.ApplyPreset(MSp.ApiProvider.Ollama);
                    MSp.OllamaSetup.Reset();
                }
                else
                {
                    ms.ApplyPreset((MSp.ApiProvider)(_selectedCloudIdx + 1));
                }
            }

            UIStyles.DrawDivider();
            GUILayout.Space(3);

            // Provider-specific section (model selection + cloud config)
            if (_mainProviderChoice == 0)
                DrawOllamaSection(ms);
            else
                DrawCloudSection(ms);

            // ══════════════════════════════════════════════════════
            // Zone 2: Foldout "Customize" (default open)
            // ══════════════════════════════════════════════════════

            GUILayout.Space(5);
            UIStyles.DrawDivider();
            GUILayout.Space(3);

            string customizeArrow = _showCustomize ? "\u25be" : "\u25b8";
            if (GUILayout.Button($"<color={UIStyles.TextLight}>{customizeArrow} {L("MSCustomize")}</color>",
                UIStyles.BoldLabel))
                _showCustomize = !_showCustomize;

            if (_showCustomize)
            {
                GUILayout.Space(3);

                // ── Personality ──
                GUILayout.Label($"<color={UIStyles.Gold}>{L("MSPersonality")}</color>", UIStyles.SubHeader);

                string[] personalityNames = { "Mechanicus", "Heretic", "Lucid", "Magickal" };
                string[] personalityDescs = {
                    L("MSPersonality_Mechanicus"),
                    L("MSPersonality_Heretic"),
                    L("MSPersonality_Lucid"),
                    L("MSPersonality_Magickal")
                };
                int curPersonality = (int)ms.Personality;
                int newPersonality = GUILayout.SelectionGrid(curPersonality, personalityNames, 4,
                    UIStyles.Button, GUILayout.Height(UIStyles.Sd(32f)));
                if (newPersonality != curPersonality)
                {
                    ms.Personality = (MSp.PersonalityType)newPersonality;
                    MachineSpirit.MachineSpirit.OnPersonalityChanged();
                }
                GUILayout.Label($"<color={UIStyles.TextMid}>{personalityDescs[newPersonality]}</color>", UIStyles.Description);

                GUILayout.Space(5);

                // ── Idle Commentary ──
                GUILayout.Label($"<color={UIStyles.Gold}>{L("MSIdleMode")}</color>", UIStyles.SubHeader);

                string[] idleNames = { "Off", "Low", "Medium", "High" };
                int curIdle = (int)ms.IdleMode;
                int newIdle = GUILayout.SelectionGrid(curIdle, idleNames, 4,
                    UIStyles.Button, GUILayout.Height(UIStyles.Sd(32f)));
                if (newIdle != curIdle)
                    ms.IdleMode = (MSp.IdleFrequency)newIdle;
                GUILayout.Label($"<color={UIStyles.TextMid}>{L("MSIdleDesc")}</color>", UIStyles.Description);

                GUILayout.Space(3);

                // ── Knowledge Base ──
                ms.EnableKnowledge = DrawCheckbox(ms.EnableKnowledge, L("MSKnowledgeEnable"));
                GUILayout.Label($"<color=#CC6666>{L("MSKnowledgeWarn")}</color>", UIStyles.Description);

                // ── Vision (Ollama only) ──
                if (ms.Provider == MSp.ApiProvider.Ollama && ms.IdleMode != MSp.IdleFrequency.Off)
                {
                    GUILayout.Space(3);
                    ms.EnableVision = DrawCheckbox(ms.EnableVision, L("MSEnableVision"));
                    if (ms.EnableVision)
                        GUILayout.Label($"<color={UIStyles.TextMid}>{L("MSVisionDesc")}</color>", UIStyles.Description);
                }
                else
                {
                    ms.EnableVision = false;
                }

                // ── Advanced Settings (Cloud providers only) ──
                if (ms.Provider != MSp.ApiProvider.Ollama)
                {
                    GUILayout.Space(5);
                    GUILayout.Label($"<color={UIStyles.TextLight}>{L("MSAdvanced")}</color>", UIStyles.BoldLabel);
                    GUILayout.Label($"<color={UIStyles.TextDim}>{L("MSAdvancedHint")}</color>", UIStyles.Description);
                    GUILayout.Space(3);

                    // Max Tokens slider (50-500)
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"<color={UIStyles.TextLight}>{L("MSMaxTokens")}</color>", UIStyles.BoldLabel, GUILayout.Width(UIStyles.Sd(120)));
                    ms.MaxTokens = Mathf.RoundToInt(GUILayout.HorizontalSlider(ms.MaxTokens, 50, 500, GUILayout.Width(UIStyles.Sd(200)), GUILayout.Height(UIStyles.Sd(15))));
                    GUILayout.Label($"<color={UIStyles.Gold}>{ms.MaxTokens}</color>", UIStyles.Label, GUILayout.Width(UIStyles.Sd(50)));
                    GUILayout.EndHorizontal();
                    GUILayout.Space(3);

                    // Temperature slider (0.0-2.0)
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"<color={UIStyles.TextLight}>{L("MSTemperature")}</color>", UIStyles.BoldLabel, GUILayout.Width(UIStyles.Sd(120)));
                    ms.Temperature = GUILayout.HorizontalSlider(ms.Temperature, 0f, 2f, GUILayout.Width(UIStyles.Sd(200)), GUILayout.Height(UIStyles.Sd(15)));
                    ms.Temperature = Mathf.Round(ms.Temperature * 10f) / 10f;
                    GUILayout.Label($"<color={UIStyles.Gold}>{ms.Temperature:F1}</color>", UIStyles.Label, GUILayout.Width(UIStyles.Sd(50)));
                    GUILayout.EndHorizontal();
                }
            }

            // ══════════════════════════════════════════════════════
            // Zone 3: Footer (always visible, compact)
            // ══════════════════════════════════════════════════════

            GUILayout.Space(5);
            UIStyles.DrawDivider();
            GUILayout.Space(3);

            GUILayout.BeginHorizontal();
            GUILayout.Label($"<color={UIStyles.TextLight}>{L("MSHotkey")}: </color><color={UIStyles.Gold}>{ms.Hotkey}</color>", UIStyles.BoldLabel, GUILayout.Width(UIStyles.Sd(120)));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button($"<color={UIStyles.TextLight}>{L("MSTestConnection")}</color>", UIStyles.Button, GUILayout.Width(UIStyles.Sd(160)), GUILayout.Height(BUTTON_HEIGHT)))
            {
                MSp.MachineSpirit.OnUserMessage("Hello, Machine Spirit. Respond with a brief greeting.");
            }
            GUILayout.EndHorizontal();
        }

        // ═════════════════════════════════════════════════════════
        // Ollama / Cloud Sections
        // ═════════════════════════════════════════════════════════

        private static void DrawOllamaSection(MSp.MachineSpiritConfig ms)
        {
            // Status text (only when actively checking/pulling)
            if (MSp.OllamaSetup.State != MSp.OllamaSetup.SetupState.Idle)
            {
                string statusColor = MSp.OllamaSetup.State == MSp.OllamaSetup.SetupState.Error
                    ? UIStyles.RoleRed : UIStyles.Gold;
                GUILayout.Label($"<color={statusColor}>{MSp.OllamaSetup.StatusText}</color>", UIStyles.Label);
                GUILayout.Space(3);
            }

            // Model selection (Ollama presets + scan installed)
            DrawModelSelection(ms);
        }

        private static void DrawCloudSection(MSp.MachineSpiritConfig ms)
        {
            // Cloud provider sub-selection
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<color={UIStyles.TextLight}>{L("MSProvider")}</color>", UIStyles.BoldLabel, GUILayout.Width(UIStyles.Sd(120)));
            int newCloudIdx = GUILayout.SelectionGrid(_selectedCloudIdx,
                new[] { "Gemini", "Groq", "OpenAI", "Custom" }, 4,
                UIStyles.Button, GUILayout.Height(BUTTON_HEIGHT));
            GUILayout.EndHorizontal();

            if (newCloudIdx != _selectedCloudIdx)
            {
                _selectedCloudIdx = newCloudIdx;
                ms.ApplyPreset((MSp.ApiProvider)(newCloudIdx + 1));
            }

            // Provider guide
            GUILayout.Label($"<color={UIStyles.TextMid}>{L("MSGuide_" + ms.Provider)}</color>", UIStyles.Description);
            GUILayout.Space(5);

            // Setup steps (Groq/Gemini/OpenAI)
            if (ms.Provider == MSp.ApiProvider.Groq || ms.Provider == MSp.ApiProvider.Gemini || ms.Provider == MSp.ApiProvider.OpenAI)
            {
                GUILayout.Label($"<color={UIStyles.Gold}>{L("MSSteps_" + ms.Provider)}</color>", UIStyles.Description);
                GUILayout.Space(5);
            }

            // API URL (Custom only)
            if (ms.Provider == MSp.ApiProvider.Custom)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"<color={UIStyles.TextLight}>{L("MSApiUrl")}</color>", UIStyles.BoldLabel, GUILayout.Width(UIStyles.Sd(120)));
                ms.ApiUrl = GUILayout.TextField(ms.ApiUrl, GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();
                GUILayout.Space(5);
            }

            // API Key (all cloud providers)
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<color={UIStyles.TextLight}>{L("MSApiKey")}</color>", UIStyles.BoldLabel, GUILayout.Width(UIStyles.Sd(120)));
            ms.ApiKey = GUILayout.PasswordField(ms.ApiKey, '*', GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
            GUILayout.Space(5);

            // Model selection (cloud presets)
            DrawModelSelection(ms);
        }

        // ═════════════════════════════════════════════════════════
        // Model Selection
        // ═════════════════════════════════════════════════════════

        private struct ModelPreset
        {
            public string Id;       // model ID sent to API
            public string Label;    // button display name
            public string DescKey;  // localization key for description
        }

        private static readonly Dictionary<MSp.ApiProvider, ModelPreset[]> _modelPresets = new()
        {
            [MSp.ApiProvider.Ollama] = null, // ★ v3.70.0: Ollama uses tier-based selection instead
        };

        // ★ v3.70.0: Ollama models organized by GPU tier
        private struct OllamaTier
        {
            public string Label;      // "4B", "12B", "27B+"
            public string DescKey;    // tier description localization key
            public bool IsHighEnd;    // show red warning
            public ModelPreset[] Models;
        }

        private static readonly OllamaTier[] _ollamaTiers = new[]
        {
            new OllamaTier
            {
                Label = "4B  (6GB GPU)", DescKey = "MSTier_4b", IsHighEnd = false,
                Models = new[]
                {
                    new ModelPreset { Id = "gemma4:e4b", Label = "★ Gemma 4 E4B (Recommended)", DescKey = "MSModel_gemma4_e4b" },
                    new ModelPreset { Id = "gemma3:4b-it-qat", Label = "Gemma 3 4B QAT", DescKey = "MSModel_gemma3_4b" },
                }
            },
            new OllamaTier
            {
                Label = "12B  (12GB GPU)", DescKey = "MSTier_12b", IsHighEnd = false,
                Models = new[]
                {
                    new ModelPreset { Id = "gemma3:12b",                                Label = "★ Gemma 3 12B",           DescKey = "MSModel_gemma3_12b" },
                    new ModelPreset { Id = "elated_hamilton_557/daichi-12b",               Label = "Daichi 12B",              DescKey = "MSModel_daichi" },
                    new ModelPreset { Id = "qwen3:14b",                                  Label = "Qwen 3 14B",              DescKey = "MSModel_qwen3_14b" },
                    new ModelPreset { Id = "michaelbui/nemomix-unleashed-12b:q4-k-m",     Label = "NemoMix Unleashed 12B",   DescKey = "MSModel_nemomix" },
                }
            },
            new OllamaTier
            {
                Label = "27B+  (24GB GPU)", DescKey = "MSTier_27b", IsHighEnd = true,
                Models = new[]
                {
                    new ModelPreset { Id = "gemma4:27b", Label = "★ Gemma 4 27B", DescKey = "MSModel_gemma4_27b" },
                    new ModelPreset { Id = "gemma3:27b",                                             Label = "Gemma 3 27B",             DescKey = "MSModel_gemma3_27b" },
                    new ModelPreset { Id = "jean-luc/big-tiger-gemma:27b-v1c-Q3_K_M",                Label = "Big Tiger Gemma 27B",     DescKey = "MSModel_bigtiger" },
                    new ModelPreset { Id = "qwen3:32b",                                              Label = "Qwen 3 32B",              DescKey = "MSModel_qwen3_32b" },
                }
            },
        };

        private static int _selectedTierIdx = 0;
        private static bool _isInstallingModel;

        private static readonly Dictionary<MSp.ApiProvider, ModelPreset[]> _cloudPresets = new()
        {
            [MSp.ApiProvider.Gemini] = new[]
            {
                new ModelPreset { Id = "gemini-2.5-flash",      Label = "Gemini 2.5 Flash",      DescKey = "MSModel_gemini25flash" },
                new ModelPreset { Id = "gemini-2.5-flash-lite", Label = "Gemini 2.5 Flash-Lite",  DescKey = "MSModel_gemini25lite" },
                new ModelPreset { Id = "gemini-2.5-pro",        Label = "Gemini 2.5 Pro",         DescKey = "MSModel_gemini25pro" },
            },
            [MSp.ApiProvider.Groq] = new[]
            {
                new ModelPreset { Id = "llama-3.3-70b-versatile",                    Label = "Llama 3.3 70B",    DescKey = "MSModel_llama33" },
                new ModelPreset { Id = "meta-llama/llama-4-scout-17b-16e-instruct",  Label = "Llama 4 Scout",    DescKey = "MSModel_llama4scout" },
                new ModelPreset { Id = "qwen/qwen3-32b",                             Label = "Qwen 3 32B",       DescKey = "MSModel_qwen3" },
            },
            [MSp.ApiProvider.OpenAI] = new[]
            {
                new ModelPreset { Id = "gpt-4o-mini",  Label = "GPT-4o Mini",   DescKey = "MSModel_gpt4omini" },
                new ModelPreset { Id = "gpt-4o",       Label = "GPT-4o",        DescKey = "MSModel_gpt4o" },
            },
        };

        private static int _selectedModelIdx = -1;

        private static void DrawModelSelection(MSp.MachineSpiritConfig ms)
        {
            GUILayout.Label($"<color={UIStyles.TextLight}>{L("MSModel")}</color>", UIStyles.BoldLabel);

            // ★ v3.70.0: Ollama uses tier-based selection
            if (ms.Provider == MSp.ApiProvider.Ollama)
            {
                DrawOllamaModelSelection(ms);
                return;
            }

            if (_cloudPresets.TryGetValue(ms.Provider, out var presets))
            {
                // Sync selection index
                if (_selectedModelIdx < 0 || _selectedModelIdx >= presets.Length || presets[_selectedModelIdx].Id != ms.Model)
                {
                    _selectedModelIdx = -1;
                    for (int i = 0; i < presets.Length; i++)
                    {
                        if (presets[i].Id == ms.Model) { _selectedModelIdx = i; break; }
                    }
                }

                var labels = new string[presets.Length];
                for (int i = 0; i < presets.Length; i++) labels[i] = presets[i].Label;

                int newIdx = GUILayout.SelectionGrid(
                    _selectedModelIdx >= 0 ? _selectedModelIdx : 0,
                    labels, presets.Length,
                    UIStyles.Button, GUILayout.Height(BUTTON_HEIGHT));

                if (newIdx != _selectedModelIdx)
                {
                    _selectedModelIdx = newIdx;
                    ms.Model = presets[newIdx].Id;
                }

                if (_selectedModelIdx >= 0 && _selectedModelIdx < presets.Length)
                    GUILayout.Label($"<color={UIStyles.TextMid}>{L(presets[_selectedModelIdx].DescKey)}</color>", UIStyles.Description);
            }
            else
            {
                ms.Model = GUILayout.TextField(ms.Model, GUILayout.ExpandWidth(true));
                GUILayout.Label($"<color={UIStyles.TextDim}>{L("MSModelHint")}</color>", UIStyles.Description);
            }
        }

        // ★ v3.72.0: Clean Ollama model selection UI
        private static bool _ollamaScannedOnce;

        private static void DrawOllamaModelSelection(MSp.MachineSpiritConfig ms)
        {
            // Auto-select after install
            if (_pendingModelSelect != null && !_isInstallingModel)
            {
                MSp.MachineSpirit.OnModelChanged(_pendingModelSelect);
                _pendingModelSelect = null;
                _selectedModelIdx = -1;
            }

            // Auto-scan installed models on first draw
            if (!_ollamaScannedOnce && !MSp.OllamaSetup.IsFetchingModels)
            {
                _ollamaScannedOnce = true;
                MSp.CoroutineRunner.Start(MSp.OllamaSetup.FetchInstalledModels());
            }

            // ── Active Model ──
            GUILayout.Label($"<color={UIStyles.Gold}>\u25b6 Active: {(string.IsNullOrEmpty(ms.Model) ? "None" : ms.Model)}</color>", UIStyles.SubHeader);
            GUILayout.Space(3);

            // ── Installed Models ──
            var installed = MSp.OllamaSetup.InstalledModels;
            if (installed.Count > 0)
            {
                // Build set of local names to hide their community originals
                var localFixedNames = new System.Collections.Generic.HashSet<string>();
                for (int i = 0; i < installed.Count; i++)
                {
                    string n = installed[i].Name;
                    if (!n.Contains("/")) localFixedNames.Add(n); // local model
                }

                for (int i = 0; i < installed.Count; i++)
                {
                    var m = installed[i];

                    // Hide community original if template-fixed local version exists
                    if (m.Name.Contains("/"))
                    {
                        string afterSlash = m.Name.Substring(m.Name.IndexOf('/') + 1);
                        if (afterSlash.Contains(":")) afterSlash = afterSlash.Substring(0, afterSlash.IndexOf(':'));
                        if (localFixedNames.Contains(afterSlash)) continue;
                    }

                    bool isActive = m.Name == ms.Model;
                    string sizeStr = m.SizeGB >= 1f ? $"{m.SizeGB:F1}GB" : $"{(int)(m.SizeGB * 1024)}MB";

                    GUILayout.BeginHorizontal();

                    // Select button (radio style)
                    string selectLabel = isActive
                        ? $"<color={UIStyles.Gold}>\u25c9 {m.Name}  ({sizeStr})</color>"
                        : $"<color={UIStyles.TextLight}>\u25cb {m.Name}  ({sizeStr})</color>";
                    if (GUILayout.Button(selectLabel, UIStyles.Button, GUILayout.ExpandWidth(true), GUILayout.Height(BUTTON_HEIGHT)))
                    {
                        if (m.Name != ms.Model)
                            MSp.MachineSpirit.OnModelChanged(m.Name);
                        _selectedModelIdx = -1;
                    }

                    // Delete button
                    GUI.enabled = !MSp.OllamaSetup.IsDeletingModel && !isActive;
                    if (GUILayout.Button($"<color=#FF6666>Delete</color>", UIStyles.Button,
                        GUILayout.Width(UIStyles.Sd(60)), GUILayout.Height(BUTTON_HEIGHT)))
                    {
                        MSp.OllamaSetup.DeleteConfirmModel = m.Name;
                    }
                    GUI.enabled = true;

                    GUILayout.EndHorizontal();
                }

                // Delete confirmation
                if (!string.IsNullOrEmpty(MSp.OllamaSetup.DeleteConfirmModel))
                {
                    GUILayout.Space(3);
                    GUILayout.Label($"<color=#FF6666>\u26a0 {L("MSDeleteConfirm")} '{MSp.OllamaSetup.DeleteConfirmModel}'?</color>", UIStyles.Description);
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button($"<color=#FF6666>{L("MSDeleteYes")}</color>", UIStyles.Button,
                        GUILayout.Width(UIStyles.Sd(120)), GUILayout.Height(BUTTON_HEIGHT)))
                    {
                        MSp.CoroutineRunner.Start(MSp.OllamaSetup.DeleteModel(MSp.OllamaSetup.DeleteConfirmModel));
                    }
                    if (GUILayout.Button($"<color={UIStyles.TextLight}>{L("MSDeleteNo")}</color>", UIStyles.Button,
                        GUILayout.Width(UIStyles.Sd(120)), GUILayout.Height(BUTTON_HEIGHT)))
                    {
                        MSp.OllamaSetup.DeleteConfirmModel = null;
                    }
                    GUILayout.EndHorizontal();
                }

                if (MSp.OllamaSetup.IsDeletingModel)
                    GUILayout.Label($"<color={UIStyles.TextMid}>Deleting...</color>", UIStyles.Description);
            }
            else if (MSp.OllamaSetup.IsFetchingModels)
            {
                GUILayout.Label($"<color={UIStyles.TextMid}>Scanning installed models...</color>", UIStyles.Description);
            }

            GUILayout.Space(5);

            // ── Add New Model (always visible) ──
            UIStyles.DrawDivider();
            GUILayout.Label($"<color={UIStyles.TextLight}>{L("MSAddModel")}</color>", UIStyles.BoldLabel);
            GUILayout.Space(3);
            {

                // Tier tabs
                var tierLabels = new string[_ollamaTiers.Length];
                for (int i = 0; i < _ollamaTiers.Length; i++) tierLabels[i] = _ollamaTiers[i].Label;

                int newTier = GUILayout.SelectionGrid(_selectedTierIdx, tierLabels, _ollamaTiers.Length,
                    UIStyles.Button, GUILayout.Height(BUTTON_HEIGHT));
                if (newTier != _selectedTierIdx) { _selectedTierIdx = newTier; _selectedModelIdx = -1; }

                var tier = _ollamaTiers[_selectedTierIdx];

                if (tier.IsHighEnd)
                    GUILayout.Label($"<color=#FF6666>\u26a0 {L("MSTierWarning")}</color>", UIStyles.Description);

                GUILayout.Space(3);

                // Models in selected tier — each with install status
                var models = tier.Models;
                var installedNames = new System.Collections.Generic.HashSet<string>();
                for (int i = 0; i < installed.Count; i++) installedNames.Add(installed[i].Name);

                for (int i = 0; i < models.Length; i++)
                {
                    bool isInstalled = installedNames.Contains(models[i].Id);
                    // Also check template-fixed local name
                    string localName = models[i].Id;
                    if (localName.Contains("/"))
                    {
                        string afterSlash = localName.Substring(localName.IndexOf('/') + 1);
                        if (afterSlash.Contains(":")) afterSlash = afterSlash.Substring(0, afterSlash.IndexOf(':'));
                        if (installedNames.Contains(afterSlash)) isInstalled = true;
                    }

                    GUILayout.BeginHorizontal();

                    // Model name
                    string nameColor = models[i].Label.StartsWith("\u2605") ? UIStyles.Gold : UIStyles.TextLight;
                    GUILayout.Label($"<color={nameColor}>{models[i].Label}</color>", UIStyles.BoldLabel, GUILayout.ExpandWidth(true));

                    // Install / Installed status
                    if (isInstalled)
                    {
                        GUILayout.Label($"<color={UIStyles.Gold}>\u2713 Installed</color>", UIStyles.Label, GUILayout.Width(UIStyles.Sd(80)));
                    }
                    else
                    {
                        GUI.enabled = !_isInstallingModel;
                        if (GUILayout.Button($"<color={UIStyles.TextLight}>Install</color>", UIStyles.Button,
                            GUILayout.Width(UIStyles.Sd(80)), GUILayout.Height(BUTTON_HEIGHT)))
                        {
                            _isInstallingModel = true;
                            MSp.CoroutineRunner.Start(InstallOllamaModel(models[i].Id));
                        }
                        GUI.enabled = true;
                    }

                    GUILayout.EndHorizontal();

                    // Description below (brighter text)
                    GUILayout.Label($"<color={UIStyles.TextMid}>{L(models[i].DescKey)}</color>", UIStyles.Description);
                    GUILayout.Space(2);
                }
            }

            // Install progress (always visible during install, even if foldout closed)
            if (_isInstallingModel && !string.IsNullOrEmpty(_installStatus))
            {
                GUILayout.Space(3);
                GUILayout.Label($"<color={UIStyles.Gold}>{_installStatus}</color>", UIStyles.Label);
            }
        }

        private static string _installStatus = "";

        private static string _pendingModelSelect; // auto-select after install

        private static System.Collections.IEnumerator InstallOllamaModel(string modelId)
        {
            _installStatus = $"Starting download: {modelId}...";
            _pendingModelSelect = modelId;

            // Find ollama executable
            string ollamaPath = "ollama";
            string localAppData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
            string localOllama = System.IO.Path.Combine(localAppData, "Programs", "Ollama", "ollama.exe");
            if (System.IO.File.Exists(localOllama)) ollamaPath = localOllama;

            System.Diagnostics.Process proc = null;
            bool startFailed = false;
            try
            {
                proc = new System.Diagnostics.Process();
                proc.StartInfo.FileName = ollamaPath;
                proc.StartInfo.Arguments = $"pull {modelId}";
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.RedirectStandardOutput = true;
                proc.StartInfo.RedirectStandardError = true;
                proc.StartInfo.CreateNoWindow = true;
                proc.Start();

                // Read stderr async (ollama outputs progress to stderr)
                proc.BeginErrorReadLine();
                proc.ErrorDataReceived += (sender, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    // Parse progress: "pulling abc123... 45% ▕████▏ 1.2 GB/3.1 GB"
                    string line = e.Data;
                    int pctIdx = line.IndexOf('%');
                    if (pctIdx > 0)
                    {
                        // Find the number before %
                        int start = pctIdx - 1;
                        while (start > 0 && (char.IsDigit(line[start - 1]) || line[start - 1] == ' ')) start--;
                        string pctStr = line.Substring(start, pctIdx - start).Trim();
                        // Find GB info after %
                        int gbIdx = line.LastIndexOf("GB");
                        string sizeInfo = "";
                        if (gbIdx > pctIdx)
                        {
                            // Find the size range like "1.2 GB/3.1 GB"
                            int rangeStart = pctIdx + 1;
                            while (rangeStart < line.Length && !char.IsDigit(line[rangeStart])) rangeStart++;
                            if (rangeStart < gbIdx + 2)
                                sizeInfo = " — " + line.Substring(rangeStart, gbIdx + 2 - rangeStart).Trim();
                        }
                        _installStatus = $"Downloading: {pctStr}%{sizeInfo}";
                    }
                    else if (line.Contains("pulling"))
                    {
                        _installStatus = "Pulling manifest...";
                    }
                    else if (line.Contains("verifying"))
                    {
                        _installStatus = "Verifying...";
                    }
                    else if (line.Contains("writing"))
                    {
                        _installStatus = "Finalizing...";
                    }
                };
            }
            catch (System.Exception ex)
            {
                Main.LogDebug($"[MachineSpirit] Failed to start ollama pull: {ex.Message}");
                _installStatus = $"Error: {ex.Message}";
                startFailed = true;
            }

            if (startFailed)
            {
                yield return new UnityEngine.WaitForSeconds(3f);
                _isInstallingModel = false;
                _installStatus = "";
                yield break;
            }

            // Poll process until done
            float startTime = UnityEngine.Time.time;
            while (!proc.HasExited)
            {
                yield return new UnityEngine.WaitForSeconds(0.5f);

                // Safety timeout: 60 minutes
                if (UnityEngine.Time.time - startTime > 3600f)
                {
                    try { proc.Kill(); } catch { }
                    _installStatus = "Timeout — download took too long";
                    yield return new UnityEngine.WaitForSeconds(3f);
                    _isInstallingModel = false;
                    _installStatus = "";
                    yield break;
                }
            }

            if (proc.ExitCode == 0)
            {
                Main.LogDebug($"[MachineSpirit] Model '{modelId}' installed successfully");
                proc.Dispose();

                // ★ v3.71.0: Check and fix template for community models
                _installStatus = "Verifying template...";
                yield return MSp.OllamaSetup.CheckAndFixTemplate(modelId);

                // If a template-fixed local model was created, use that instead
                if (!string.IsNullOrEmpty(MSp.OllamaSetup.TemplateFixedModel))
                {
                    _pendingModelSelect = MSp.OllamaSetup.TemplateFixedModel;
                    _installStatus = $"\u2713 {MSp.OllamaSetup.TemplateFixedModel} ready!";
                    MSp.OllamaSetup.TemplateFixedModel = null;
                }
                else
                {
                    _installStatus = $"\u2713 {modelId} installed!";
                }

                _selectedModelIdx = -1;
                yield return new UnityEngine.WaitForSeconds(2f);
                MSp.CoroutineRunner.Start(MSp.OllamaSetup.FetchInstalledModels());
            }
            else
            {
                string err = "";
                try { err = proc.StandardError.ReadToEnd(); } catch { }
                Main.LogDebug($"[MachineSpirit] Model install failed (exit {proc.ExitCode}): {err}");
                _installStatus = $"Install failed. Check Ollama is running.";
                proc.Dispose();
                yield return new UnityEngine.WaitForSeconds(5f);
            }

            _isInstallingModel = false;
            _installStatus = "";
            _pendingModelSelect = null;
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
