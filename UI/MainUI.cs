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
    /// v3.0: 메인 UI
    /// </summary>
    public static class MainUI
    {
        private static string _selectedCharacterId = "";
        private static CharacterSettings _editingSettings = null;
        private static Vector2 _scrollPosition = Vector2.zero;
        private static bool _showAdvancedSettings = false;  // ★ v3.5.13: 고급 설정 접기/펴기
        private static bool _showPerformanceSettings = false;  // ★ v3.5.20: 성능 설정 접기/펴기
        private static bool _showAoESettings = false;  // ★ v3.8.12: AOE 설정 접기/펴기
        private static bool _showLogicSettings = false;  // ★ v3.16.2: AI 로직 설정 상위 그룹
        private static bool _showThresholdSettings = false;  // ★ v3.16.2: 전투 임계값
        private static bool _showThreatSettings = false;  // ★ v3.16.2: 위협 평가
        private static bool _showScoringSettings = false;  // ★ v3.16.2: 스코어링 가중치
        private static bool _showRoleWeightSettings = false;  // ★ v3.16.2: 역할별 가중치
        private static bool _showWeaponRotationSettings = false;  // ★ v3.16.2: 무기 로테이션

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
                string langName;
                switch (lang)
                {
                    case Language.English:  langName = "English";  break;
                    case Language.Korean:   langName = "한국어";    break;
                    case Language.Russian:  langName = "Русский";  break;
                    case Language.Japanese: langName = "日本語";    break;
                    default:               langName = lang.ToString(); break;
                }
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
            Main.Settings.EnableAISpeech = DrawCheckbox(Main.Settings.EnableAISpeech, L("EnableAISpeech"));  // ★ v3.9.32

            // ★ v3.9.34: 대사 JSON 리로드 버튼
            if (Main.Settings.EnableAISpeech)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(55);  // 체크박스 들여쓰기와 정렬
                if (GUILayout.Button($"<color=#D8D8D8>{L("ReloadDialogue")}</color>", GUILayout.Width(250), GUILayout.Height(30)))
                {
                    DialogueLocalization.ReloadFromJson();
                }
                GUILayout.EndHorizontal();
            }
            Main.Settings.EnableVictoryBark = DrawCheckbox(Main.Settings.EnableVictoryBark, L("EnableVictoryBark"));  // ★ v3.9.80

            GUILayout.Space(15);
            DrawPerformanceSettings();

            GUILayout.Space(15);
            DrawAoESettings();  // ★ v3.8.12: AOE 설정

            // ★ v3.16.2: aiconfig.json 전체 설정 노출
            GUILayout.Space(15);
            DrawLogicSettings();

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

        /// <summary>
        /// ★ v3.8.12: AOE 설정 섹션 (전역)
        /// </summary>
        private static void DrawAoESettings()
        {
            // 접기/펴기 버튼
            GUILayout.BeginHorizontal();
            string toggleText = _showAoESettings
                ? $"<size=20><b><color=#FF6347>▼ {L("AoESettings")}</color></b></size>"
                : $"<size=20><b><color=#AAAAAA>▶ {L("AoESettings")}</color></b></size>";

            if (GUILayout.Button(toggleText, _boldLabelStyle, GUILayout.Height(40), GUILayout.Width(400)))
                _showAoESettings = !_showAoESettings;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (!_showAoESettings) return;

            var aoeConfig = AIConfig.GetAoEConfig();
            if (aoeConfig == null) return;

            GUILayout.BeginVertical("box", GUILayout.MinWidth(700));

            // 경고 메시지
            GUILayout.Label($"<size=18><color=#FF6347>{L("AoEWarning")}</color></size>", _descriptionStyle);
            GUILayout.Space(15);

            // 리셋 버튼
            if (GUILayout.Button($"<size=18><color=#FFFF00>{L("ResetAoEToDefault")}</color></size>", GUILayout.Width(350), GUILayout.Height(45)))
            {
                aoeConfig.MaxPlayerAlliesHit = 1;
                AIConfig.Save();
            }
            GUILayout.Space(20);

            // ★ v3.8.94: MaxPlayerAlliesHit 슬라이더만 유지 — 모든 AoE 타입에 통합 적용
            int newMaxAllies = DrawSliderSettingIntLarge(
                L("MaxPlayerAlliesHit"),
                L("MaxPlayerAlliesHitDesc"),
                aoeConfig.MaxPlayerAlliesHit,
                0, 3);
            if (newMaxAllies != aoeConfig.MaxPlayerAlliesHit)
            {
                aoeConfig.MaxPlayerAlliesHit = newMaxAllies;
                AIConfig.Save();
            }

            GUILayout.EndVertical();
        }

        /// <summary>
        /// ★ v3.16.2: AI 로직 설정 상위 그룹 — 모든 aiconfig.json 세부 설정을 감싸는 접이식 섹션
        /// </summary>
        private static void DrawLogicSettings()
        {
            // 상위 접기/펴기 버튼 (경고 아이콘 포함)
            GUILayout.BeginHorizontal();
            string toggleText = _showLogicSettings
                ? $"<size=22><b><color=#FF4500>▼ {L("LogicSettings")}</color></b></size>"
                : $"<size=22><b><color=#AAAAAA>▶ {L("LogicSettings")}</color></b></size>";

            if (GUILayout.Button(toggleText, _boldLabelStyle, GUILayout.Height(45), GUILayout.Width(450)))
                _showLogicSettings = !_showLogicSettings;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (!_showLogicSettings) return;

            GUILayout.BeginVertical("box", GUILayout.MinWidth(720));

            // 상위 경고 메시지
            GUILayout.Label($"<size=16><color=#FF6600>{L("LogicSettingsWarning")}</color></size>", _descriptionStyle);
            GUILayout.Space(10);

            // ★ v3.18.8: 통합 리셋 버튼 (모든 하위 설정 일괄 초기화)
            if (GUILayout.Button($"<size=20><b><color=#FF4444>{L("ResetAllLogicToDefault")}</color></b></size>", GUILayout.Width(500), GUILayout.Height(50)))
            {
                AIConfig.ResetToDefault();
            }
            GUILayout.Space(15);

            // 하위 섹션들
            DrawThresholdSettings();
            GUILayout.Space(10);
            DrawThreatSettings();
            GUILayout.Space(10);
            DrawAoEAdvancedSettings();
            GUILayout.Space(10);
            DrawScoringSettings();
            GUILayout.Space(10);
            DrawRoleWeightSettings();
            GUILayout.Space(10);
            DrawWeaponRotationSettings();

            GUILayout.EndVertical();
        }

        /// <summary>
        /// ★ v3.16.2: 전투 임계값 설정 섹션
        /// </summary>
        private static void DrawThresholdSettings()
        {
            GUILayout.BeginHorizontal();
            string toggleText = _showThresholdSettings
                ? $"<size=20><b><color=#E8A317>▼ {L("ThresholdSettings")}</color></b></size>"
                : $"<size=20><b><color=#AAAAAA>▶ {L("ThresholdSettings")}</color></b></size>";

            if (GUILayout.Button(toggleText, _boldLabelStyle, GUILayout.Height(40), GUILayout.Width(400)))
                _showThresholdSettings = !_showThresholdSettings;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (!_showThresholdSettings) return;

            var thresholds = AIConfig.GetThresholds();
            if (thresholds == null) return;

            GUILayout.BeginVertical("box", GUILayout.MinWidth(680));
            GUILayout.Label($"<size=16><color=#E8A317>{L("ThresholdWarning")}</color></size>", _descriptionStyle);
            GUILayout.Space(10);

            if (GUILayout.Button($"<size=18><color=#FFFF00>{L("ResetThresholdToDefault")}</color></size>", GUILayout.Width(350), GUILayout.Height(45)))
            {
                var def = new ThresholdConfig();
                thresholds.EmergencyHealHP = def.EmergencyHealHP;
                thresholds.HealPriorityHP = def.HealPriorityHP;
                thresholds.FinisherTargetHP = def.FinisherTargetHP;
                thresholds.SkipBuffBelowHP = def.SkipBuffBelowHP;
                thresholds.PreAttackBuffMinHP = def.PreAttackBuffMinHP;
                thresholds.SelfDamageMinHP = def.SelfDamageMinHP;
                thresholds.DesperatePhaseHP = def.DesperatePhaseHP;
                thresholds.DesperateSelfHP = def.DesperateSelfHP;
                thresholds.SafeDistance = def.SafeDistance;
                thresholds.DangerDistance = def.DangerDistance;
                thresholds.OneHitKillRatio = def.OneHitKillRatio;
                thresholds.TwoHitKillRatio = def.TwoHitKillRatio;
                thresholds.CleanupEnemyCount = def.CleanupEnemyCount;
                thresholds.OpeningPhaseMinAP = def.OpeningPhaseMinAP;
                thresholds.LowThreatHP = def.LowThreatHP;
                AIConfig.Save();
            }
            GUILayout.Space(15);

            bool changed = false;

            float v;
            v = DrawSliderSettingFloatLarge(L("EmergencyHealHP"), L("EmergencyHealHPDesc"), thresholds.EmergencyHealHP, 10f, 60f);
            if (v != thresholds.EmergencyHealHP) { thresholds.EmergencyHealHP = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("HealPriorityHP"), L("HealPriorityHPDesc"), thresholds.HealPriorityHP, 20f, 80f);
            if (v != thresholds.HealPriorityHP) { thresholds.HealPriorityHP = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("FinisherTargetHP"), L("FinisherTargetHPDesc"), thresholds.FinisherTargetHP, 10f, 50f);
            if (v != thresholds.FinisherTargetHP) { thresholds.FinisherTargetHP = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("SkipBuffBelowHP"), L("SkipBuffBelowHPDesc"), thresholds.SkipBuffBelowHP, 10f, 70f);
            if (v != thresholds.SkipBuffBelowHP) { thresholds.SkipBuffBelowHP = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("PreAttackBuffMinHP"), L("PreAttackBuffMinHPDesc"), thresholds.PreAttackBuffMinHP, 20f, 80f);
            if (v != thresholds.PreAttackBuffMinHP) { thresholds.PreAttackBuffMinHP = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("SelfDamageMinHP"), L("SelfDamageMinHPDesc"), thresholds.SelfDamageMinHP, 40f, 100f);
            if (v != thresholds.SelfDamageMinHP) { thresholds.SelfDamageMinHP = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("DesperatePhaseHP"), L("DesperatePhaseHPDesc"), thresholds.DesperatePhaseHP, 15f, 60f);
            if (v != thresholds.DesperatePhaseHP) { thresholds.DesperatePhaseHP = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("DesperateSelfHP"), L("DesperateSelfHPDesc"), thresholds.DesperateSelfHP, 10f, 50f);
            if (v != thresholds.DesperateSelfHP) { thresholds.DesperateSelfHP = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("CfgSafeDistance"), L("CfgSafeDistanceDesc"), thresholds.SafeDistance, 3f, 15f);
            if (v != thresholds.SafeDistance) { thresholds.SafeDistance = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("DangerDistance"), L("DangerDistanceDesc"), thresholds.DangerDistance, 2f, 10f);
            if (v != thresholds.DangerDistance) { thresholds.DangerDistance = v; changed = true; }

            v = DrawSliderSettingFloat2Decimal(L("OneHitKillRatio"), L("OneHitKillRatioDesc"), thresholds.OneHitKillRatio, 0.7f, 1.0f);
            if (v != thresholds.OneHitKillRatio) { thresholds.OneHitKillRatio = v; changed = true; }

            v = DrawSliderSettingFloat2Decimal(L("TwoHitKillRatio"), L("TwoHitKillRatioDesc"), thresholds.TwoHitKillRatio, 0.3f, 0.8f);
            if (v != thresholds.TwoHitKillRatio) { thresholds.TwoHitKillRatio = v; changed = true; }

            int iv;
            iv = DrawSliderSettingIntLarge(L("CleanupEnemyCount"), L("CleanupEnemyCountDesc"), thresholds.CleanupEnemyCount, 1, 5);
            if (iv != thresholds.CleanupEnemyCount) { thresholds.CleanupEnemyCount = iv; changed = true; }

            v = DrawSliderSettingFloatLarge(L("OpeningPhaseMinAP"), L("OpeningPhaseMinAPDesc"), thresholds.OpeningPhaseMinAP, 1f, 6f);
            if (v != thresholds.OpeningPhaseMinAP) { thresholds.OpeningPhaseMinAP = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("LowThreatHP"), L("LowThreatHPDesc"), thresholds.LowThreatHP, 10f, 60f);
            if (v != thresholds.LowThreatHP) { thresholds.LowThreatHP = v; changed = true; }

            if (changed) AIConfig.Save();

            GUILayout.EndVertical();
        }

        /// <summary>
        /// ★ v3.16.2: 위협 평가 가중치 섹션
        /// </summary>
        private static void DrawThreatSettings()
        {
            GUILayout.BeginHorizontal();
            string toggleText = _showThreatSettings
                ? $"<size=20><b><color=#DC143C>▼ {L("ThreatSettings")}</color></b></size>"
                : $"<size=20><b><color=#AAAAAA>▶ {L("ThreatSettings")}</color></b></size>";

            if (GUILayout.Button(toggleText, _boldLabelStyle, GUILayout.Height(40), GUILayout.Width(400)))
                _showThreatSettings = !_showThreatSettings;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (!_showThreatSettings) return;

            var thresholds = AIConfig.GetThresholds();
            if (thresholds == null) return;

            GUILayout.BeginVertical("box", GUILayout.MinWidth(680));
            GUILayout.Label($"<size=16><color=#DC143C>{L("ThreatWarning")}</color></size>", _descriptionStyle);
            GUILayout.Space(10);

            if (GUILayout.Button($"<size=18><color=#FFFF00>{L("ResetThreatToDefault")}</color></size>", GUILayout.Width(350), GUILayout.Height(45)))
            {
                thresholds.LethalityWeight = 0.3f;
                thresholds.ProximityWeight = 0.4f;
                thresholds.HealerRoleBonus = 0.15f;
                thresholds.CasterRoleBonus = 0.1f;
                thresholds.RangedWeaponBonus = 0.05f;
                thresholds.ThreatMaxDistance = 30f;
                AIConfig.Save();
            }
            GUILayout.Space(15);

            bool changed = false;
            float v;

            v = DrawSliderSettingFloat2Decimal(L("LethalityWeight"), L("LethalityWeightDesc"), thresholds.LethalityWeight, 0f, 1.0f);
            if (v != thresholds.LethalityWeight) { thresholds.LethalityWeight = v; changed = true; }

            v = DrawSliderSettingFloat2Decimal(L("ProximityWeight"), L("ProximityWeightDesc"), thresholds.ProximityWeight, 0f, 1.0f);
            if (v != thresholds.ProximityWeight) { thresholds.ProximityWeight = v; changed = true; }

            v = DrawSliderSettingFloat2Decimal(L("HealerRoleBonus"), L("HealerRoleBonusDesc"), thresholds.HealerRoleBonus, 0f, 0.5f);
            if (v != thresholds.HealerRoleBonus) { thresholds.HealerRoleBonus = v; changed = true; }

            v = DrawSliderSettingFloat2Decimal(L("CasterRoleBonus"), L("CasterRoleBonusDesc"), thresholds.CasterRoleBonus, 0f, 0.5f);
            if (v != thresholds.CasterRoleBonus) { thresholds.CasterRoleBonus = v; changed = true; }

            v = DrawSliderSettingFloat2Decimal(L("RangedWeaponBonus"), L("RangedWeaponBonusDesc"), thresholds.RangedWeaponBonus, 0f, 0.3f);
            if (v != thresholds.RangedWeaponBonus) { thresholds.RangedWeaponBonus = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("ThreatMaxDistance"), L("ThreatMaxDistanceDesc"), thresholds.ThreatMaxDistance, 10f, 50f);
            if (v != thresholds.ThreatMaxDistance) { thresholds.ThreatMaxDistance = v; changed = true; }

            if (changed) AIConfig.Save();

            GUILayout.EndVertical();
        }

        /// <summary>
        /// ★ v3.16.2: AoE 세부 설정 (기존 DrawAoESettings의 MaxPlayerAlliesHit 외 추가 항목)
        /// </summary>
        private static void DrawAoEAdvancedSettings()
        {
            var aoeConfig = AIConfig.GetAoEConfig();
            if (aoeConfig == null) return;

            // AoE 고급 설정은 별도 토글 없이 기존 _showAoESettings 재활용하지 않음 —
            // 상위 LogicSettings 내부이므로 항상 표시
            GUILayout.Label($"<size=20><b><color=#FF6347>— {L("AoESettings")} ({L("ScoringGroup_Other").Replace("— ", "").Replace(" —", "").Trim()}) —</color></b></size>", _boldLabelStyle);
            GUILayout.Space(5);

            GUILayout.BeginVertical("box", GUILayout.MinWidth(680));

            bool changed = false;
            float v;
            int iv;

            v = DrawSliderSettingFloatLarge(L("EnemyHitScore"), L("EnemyHitScoreDesc"), aoeConfig.EnemyHitScore, 1000f, 50000f);
            if (v != aoeConfig.EnemyHitScore) { aoeConfig.EnemyHitScore = v; changed = true; }

            v = DrawSliderSettingFloat2Decimal(L("PlayerAllyPenaltyMult"), L("PlayerAllyPenaltyMultDesc"), aoeConfig.PlayerAllyPenaltyMultiplier, 0.5f, 5.0f);
            if (v != aoeConfig.PlayerAllyPenaltyMultiplier) { aoeConfig.PlayerAllyPenaltyMultiplier = v; changed = true; }

            v = DrawSliderSettingFloat2Decimal(L("NpcAllyPenaltyMult"), L("NpcAllyPenaltyMultDesc"), aoeConfig.NpcAllyPenaltyMultiplier, 0.5f, 5.0f);
            if (v != aoeConfig.NpcAllyPenaltyMultiplier) { aoeConfig.NpcAllyPenaltyMultiplier = v; changed = true; }

            v = DrawSliderSettingFloat2Decimal(L("CasterSelfPenaltyMult"), L("CasterSelfPenaltyMultDesc"), aoeConfig.CasterSelfPenaltyMultiplier, 0.5f, 5.0f);
            if (v != aoeConfig.CasterSelfPenaltyMultiplier) { aoeConfig.CasterSelfPenaltyMultiplier = v; changed = true; }

            iv = DrawSliderSettingIntLarge(L("CfgMinClusterSize"), L("CfgMinClusterSizeDesc"), aoeConfig.MinClusterSize, 1, 5);
            if (iv != aoeConfig.MinClusterSize) { aoeConfig.MinClusterSize = iv; changed = true; }

            v = DrawSliderSettingFloatLarge(L("ClusterNpcAllyPenalty"), L("ClusterNpcAllyPenaltyDesc"), aoeConfig.ClusterNpcAllyPenalty, 0f, 100f);
            if (v != aoeConfig.ClusterNpcAllyPenalty) { aoeConfig.ClusterNpcAllyPenalty = v; changed = true; }

            if (changed) AIConfig.Save();

            GUILayout.EndVertical();
        }

        /// <summary>
        /// ★ v3.16.2: 스코어링 가중치 섹션
        /// </summary>
        private static void DrawScoringSettings()
        {
            GUILayout.BeginHorizontal();
            string toggleText = _showScoringSettings
                ? $"<size=20><b><color=#9370DB>▼ {L("ScoringSettings")}</color></b></size>"
                : $"<size=20><b><color=#AAAAAA>▶ {L("ScoringSettings")}</color></b></size>";

            if (GUILayout.Button(toggleText, _boldLabelStyle, GUILayout.Height(40), GUILayout.Width(400)))
                _showScoringSettings = !_showScoringSettings;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (!_showScoringSettings) return;

            var scoring = AIConfig.GetScoringConfig();
            if (scoring == null) return;

            GUILayout.BeginVertical("box", GUILayout.MinWidth(680));
            GUILayout.Label($"<size=16><color=#9370DB>{L("ScoringWarning")}</color></size>", _descriptionStyle);
            GUILayout.Space(10);

            if (GUILayout.Button($"<size=18><color=#FFFF00>{L("ResetScoringToDefault")}</color></size>", GUILayout.Width(350), GUILayout.Height(45)))
            {
                var def = new ScoringConfig();
                scoring.OpeningPhaseBuffMult = def.OpeningPhaseBuffMult;
                scoring.CleanupPhaseBuffMult = def.CleanupPhaseBuffMult;
                scoring.DesperateNonDefMult = def.DesperateNonDefMult;
                scoring.PreCombatOpeningBonus = def.PreCombatOpeningBonus;
                scoring.PreCombatCleanupPenalty = def.PreCombatCleanupPenalty;
                scoring.PreAttackHittableBonus = def.PreAttackHittableBonus;
                scoring.PreAttackNoEnemyPenalty = def.PreAttackNoEnemyPenalty;
                scoring.EmergencyDesperateBonus = def.EmergencyDesperateBonus;
                scoring.EmergencyNonDesperatePenalty = def.EmergencyNonDesperatePenalty;
                scoring.TauntNearEnemiesBonus = def.TauntNearEnemiesBonus;
                scoring.TauntFewEnemiesPenalty = def.TauntFewEnemiesPenalty;
                scoring.BuffAttackSynergy = def.BuffAttackSynergy;
                scoring.MoveAttackSynergy = def.MoveAttackSynergy;
                scoring.MultiAttackPerAttack = def.MultiAttackPerAttack;
                scoring.DefenseRetreatSynergy = def.DefenseRetreatSynergy;
                scoring.KillConfirmSynergy = def.KillConfirmSynergy;
                scoring.AlmostKillSynergy = def.AlmostKillSynergy;
                scoring.ClearMPDangerBase = def.ClearMPDangerBase;
                scoring.AoEBonusPerEnemy = def.AoEBonusPerEnemy;
                scoring.InertiaBonus = def.InertiaBonus;
                scoring.HardCCExploitBonus = def.HardCCExploitBonus;
                scoring.DOTFollowUpBonus = def.DOTFollowUpBonus;
                AIConfig.Save();
            }
            GUILayout.Space(15);

            bool changed = false;
            float v;

            // ── 버프 배율 ──
            GUILayout.Label($"<size=18><color=#9370DB>{L("ScoringGroup_BuffMult")}</color></size>", _boldLabelStyle);
            GUILayout.Space(5);

            v = DrawSliderSettingFloat2Decimal(L("OpeningPhaseBuffMult"), L("OpeningPhaseBuffMultDesc"), scoring.OpeningPhaseBuffMult, 0.5f, 3.0f);
            if (v != scoring.OpeningPhaseBuffMult) { scoring.OpeningPhaseBuffMult = v; changed = true; }

            v = DrawSliderSettingFloat2Decimal(L("CleanupPhaseBuffMult"), L("CleanupPhaseBuffMultDesc"), scoring.CleanupPhaseBuffMult, 0.3f, 2.0f);
            if (v != scoring.CleanupPhaseBuffMult) { scoring.CleanupPhaseBuffMult = v; changed = true; }

            v = DrawSliderSettingFloat2Decimal(L("DesperateNonDefMult"), L("DesperateNonDefMultDesc"), scoring.DesperateNonDefMult, 0.1f, 1.5f);
            if (v != scoring.DesperateNonDefMult) { scoring.DesperateNonDefMult = v; changed = true; }

            // ── 타이밍 보너스 ──
            GUILayout.Space(10);
            GUILayout.Label($"<size=18><color=#9370DB>{L("ScoringGroup_Timing")}</color></size>", _boldLabelStyle);
            GUILayout.Space(5);

            v = DrawSliderSettingFloatLarge(L("PreCombatOpeningBonus"), L("PreCombatOpeningBonusDesc"), scoring.PreCombatOpeningBonus, 0f, 100f);
            if (v != scoring.PreCombatOpeningBonus) { scoring.PreCombatOpeningBonus = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("PreCombatCleanupPenalty"), L("PreCombatCleanupPenaltyDesc"), scoring.PreCombatCleanupPenalty, 0f, 60f);
            if (v != scoring.PreCombatCleanupPenalty) { scoring.PreCombatCleanupPenalty = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("PreAttackHittableBonus"), L("PreAttackHittableBonusDesc"), scoring.PreAttackHittableBonus, 0f, 80f);
            if (v != scoring.PreAttackHittableBonus) { scoring.PreAttackHittableBonus = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("PreAttackNoEnemyPenalty"), L("PreAttackNoEnemyPenaltyDesc"), scoring.PreAttackNoEnemyPenalty, 0f, 40f);
            if (v != scoring.PreAttackNoEnemyPenalty) { scoring.PreAttackNoEnemyPenalty = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("EmergencyDesperateBonus"), L("EmergencyDesperateBonusDesc"), scoring.EmergencyDesperateBonus, 0f, 100f);
            if (v != scoring.EmergencyDesperateBonus) { scoring.EmergencyDesperateBonus = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("EmergencyNonDesperatePenalty"), L("EmergencyNonDesperatePenaltyDesc"), scoring.EmergencyNonDesperatePenalty, 0f, 60f);
            if (v != scoring.EmergencyNonDesperatePenalty) { scoring.EmergencyNonDesperatePenalty = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("TauntNearEnemiesBonus"), L("TauntNearEnemiesBonusDesc"), scoring.TauntNearEnemiesBonus, 0f, 80f);
            if (v != scoring.TauntNearEnemiesBonus) { scoring.TauntNearEnemiesBonus = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("TauntFewEnemiesPenalty"), L("TauntFewEnemiesPenaltyDesc"), scoring.TauntFewEnemiesPenalty, 0f, 50f);
            if (v != scoring.TauntFewEnemiesPenalty) { scoring.TauntFewEnemiesPenalty = v; changed = true; }

            // ── 시너지 ──
            GUILayout.Space(10);
            GUILayout.Label($"<size=18><color=#9370DB>{L("ScoringGroup_Synergy")}</color></size>", _boldLabelStyle);
            GUILayout.Space(5);

            v = DrawSliderSettingFloatLarge(L("BuffAttackSynergy"), L("BuffAttackSynergyDesc"), scoring.BuffAttackSynergy, 0f, 80f);
            if (v != scoring.BuffAttackSynergy) { scoring.BuffAttackSynergy = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("MoveAttackSynergy"), L("MoveAttackSynergyDesc"), scoring.MoveAttackSynergy, 0f, 40f);
            if (v != scoring.MoveAttackSynergy) { scoring.MoveAttackSynergy = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("MultiAttackPerAttack"), L("MultiAttackPerAttackDesc"), scoring.MultiAttackPerAttack, 0f, 40f);
            if (v != scoring.MultiAttackPerAttack) { scoring.MultiAttackPerAttack = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("DefenseRetreatSynergy"), L("DefenseRetreatSynergyDesc"), scoring.DefenseRetreatSynergy, 0f, 50f);
            if (v != scoring.DefenseRetreatSynergy) { scoring.DefenseRetreatSynergy = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("KillConfirmSynergy"), L("KillConfirmSynergyDesc"), scoring.KillConfirmSynergy, 0f, 80f);
            if (v != scoring.KillConfirmSynergy) { scoring.KillConfirmSynergy = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("AlmostKillSynergy"), L("AlmostKillSynergyDesc"), scoring.AlmostKillSynergy, 0f, 50f);
            if (v != scoring.AlmostKillSynergy) { scoring.AlmostKillSynergy = v; changed = true; }

            // ── 기타 ──
            GUILayout.Space(10);
            GUILayout.Label($"<size=18><color=#9370DB>{L("ScoringGroup_Other")}</color></size>", _boldLabelStyle);
            GUILayout.Space(5);

            v = DrawSliderSettingFloatLarge(L("ClearMPDangerBase"), L("ClearMPDangerBaseDesc"), scoring.ClearMPDangerBase, 20f, 150f);
            if (v != scoring.ClearMPDangerBase) { scoring.ClearMPDangerBase = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("AoEBonusPerEnemy"), L("AoEBonusPerEnemyDesc"), scoring.AoEBonusPerEnemy, 5f, 50f);
            if (v != scoring.AoEBonusPerEnemy) { scoring.AoEBonusPerEnemy = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("InertiaBonus"), L("InertiaBonusDesc"), scoring.InertiaBonus, 0f, 50f);
            if (v != scoring.InertiaBonus) { scoring.InertiaBonus = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("HardCCExploitBonus"), L("HardCCExploitBonusDesc"), scoring.HardCCExploitBonus, 0f, 50f);
            if (v != scoring.HardCCExploitBonus) { scoring.HardCCExploitBonus = v; changed = true; }

            v = DrawSliderSettingFloatLarge(L("DOTFollowUpBonus"), L("DOTFollowUpBonusDesc"), scoring.DOTFollowUpBonus, 0f, 30f);
            if (v != scoring.DOTFollowUpBonus) { scoring.DOTFollowUpBonus = v; changed = true; }

            if (changed) AIConfig.Save();

            GUILayout.EndVertical();
        }

        /// <summary>
        /// ★ v3.16.2: 역할별 타겟 가중치 섹션
        /// </summary>
        private static void DrawRoleWeightSettings()
        {
            GUILayout.BeginHorizontal();
            string toggleText = _showRoleWeightSettings
                ? $"<size=20><b><color=#20B2AA>▼ {L("RoleWeightSettings")}</color></b></size>"
                : $"<size=20><b><color=#AAAAAA>▶ {L("RoleWeightSettings")}</color></b></size>";

            if (GUILayout.Button(toggleText, _boldLabelStyle, GUILayout.Height(40), GUILayout.Width(400)))
                _showRoleWeightSettings = !_showRoleWeightSettings;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (!_showRoleWeightSettings) return;

            var config = AIConfig.Instance;
            if (config == null) return;

            GUILayout.BeginVertical("box", GUILayout.MinWidth(680));
            GUILayout.Label($"<size=16><color=#20B2AA>{L("RoleWeightWarning")}</color></size>", _descriptionStyle);
            GUILayout.Space(10);

            if (GUILayout.Button($"<size=18><color=#FFFF00>{L("ResetRoleWeightToDefault")}</color></size>", GUILayout.Width(350), GUILayout.Height(45)))
            {
                var def = AIConfig.CreateDefault();
                config.DPS = def.DPS;
                config.Tank = def.Tank;
                config.Support = def.Support;
                AIConfig.Save();
            }
            GUILayout.Space(15);

            bool changed = false;

            // DPS
            GUILayout.Label($"<size=18><b><color=#FF6347>— DPS —</color></b></size>", _boldLabelStyle);
            GUILayout.Space(5);
            changed |= DrawRoleWeightSliders(config.DPS);

            // Tank
            GUILayout.Space(10);
            GUILayout.Label($"<size=18><b><color=#4169E1>— Tank —</color></b></size>", _boldLabelStyle);
            GUILayout.Space(5);
            changed |= DrawRoleWeightSliders(config.Tank);

            // Support
            GUILayout.Space(10);
            GUILayout.Label($"<size=18><b><color=#FFD700>— Support —</color></b></size>", _boldLabelStyle);
            GUILayout.Space(5);
            changed |= DrawRoleWeightSliders(config.Support);

            if (changed) AIConfig.Save();

            GUILayout.EndVertical();
        }

        /// <summary>
        /// ★ v3.16.2: RoleWeights 5개 슬라이더 (DPS/Tank/Support 공통)
        /// </summary>
        private static bool DrawRoleWeightSliders(RoleWeights weights)
        {
            if (weights == null) return false;
            bool changed = false;
            float v;

            v = DrawSliderSettingFloat2Decimal(L("RW_HPPercent"), L("RW_HPPercentDesc"), weights.HPPercent, 0f, 1.0f);
            if (v != weights.HPPercent) { weights.HPPercent = v; changed = true; }

            v = DrawSliderSettingFloat2Decimal(L("RW_Threat"), L("RW_ThreatDesc"), weights.Threat, 0f, 1.0f);
            if (v != weights.Threat) { weights.Threat = v; changed = true; }

            v = DrawSliderSettingFloat2Decimal(L("RW_Distance"), L("RW_DistanceDesc"), weights.Distance, 0f, 1.0f);
            if (v != weights.Distance) { weights.Distance = v; changed = true; }

            v = DrawSliderSettingFloat2Decimal(L("RW_FinisherBonus"), L("RW_FinisherBonusDesc"), weights.FinisherBonus, 0.5f, 3.0f);
            if (v != weights.FinisherBonus) { weights.FinisherBonus = v; changed = true; }

            v = DrawSliderSettingFloat2Decimal(L("RW_OneHitKillBonus"), L("RW_OneHitKillBonusDesc"), weights.OneHitKillBonus, 0.5f, 4.0f);
            if (v != weights.OneHitKillBonus) { weights.OneHitKillBonus = v; changed = true; }

            return changed;
        }

        /// <summary>
        /// ★ v3.16.2: 무기 로테이션 설정 섹션
        /// </summary>
        private static void DrawWeaponRotationSettings()
        {
            GUILayout.BeginHorizontal();
            string toggleText = _showWeaponRotationSettings
                ? $"<size=20><b><color=#DAA520>▼ {L("WeaponRotationSettings")}</color></b></size>"
                : $"<size=20><b><color=#AAAAAA>▶ {L("WeaponRotationSettings")}</color></b></size>";

            if (GUILayout.Button(toggleText, _boldLabelStyle, GUILayout.Height(40), GUILayout.Width(400)))
                _showWeaponRotationSettings = !_showWeaponRotationSettings;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (!_showWeaponRotationSettings) return;

            var wrConfig = AIConfig.GetWeaponRotationConfig();
            if (wrConfig == null) return;

            GUILayout.BeginVertical("box", GUILayout.MinWidth(680));
            GUILayout.Label($"<size=16><color=#DAA520>{L("WeaponRotationWarning")}</color></size>", _descriptionStyle);
            GUILayout.Space(10);

            if (GUILayout.Button($"<size=18><color=#FFFF00>{L("ResetWeaponRotationToDefault")}</color></size>", GUILayout.Width(350), GUILayout.Height(45)))
            {
                wrConfig.MaxSwitchesPerTurn = 2;
                wrConfig.MinEnemiesForAlternateAoE = 2;
                AIConfig.Save();
            }
            GUILayout.Space(15);

            bool changed = false;
            int iv;

            iv = DrawSliderSettingIntLarge(L("MaxSwitchesPerTurn"), L("MaxSwitchesPerTurnDesc"), wrConfig.MaxSwitchesPerTurn, 1, 4);
            if (iv != wrConfig.MaxSwitchesPerTurn) { wrConfig.MaxSwitchesPerTurn = iv; changed = true; }

            iv = DrawSliderSettingIntLarge(L("MinEnemiesForAlternateAoE"), L("MinEnemiesForAlternateAoEDesc"), wrConfig.MinEnemiesForAlternateAoE, 1, 5);
            if (iv != wrConfig.MinEnemiesForAlternateAoE) { wrConfig.MinEnemiesForAlternateAoE = iv; changed = true; }

            if (changed) AIConfig.Save();

            GUILayout.EndVertical();
        }

        /// <summary>
        /// ★ v3.16.2: 소수점 2자리 float 슬라이더 (0-1.0 범위 값용)
        /// </summary>
        private static float DrawSliderSettingFloat2Decimal(string label, string description, float value, float min, float max)
        {
            GUILayout.BeginVertical();

            GUILayout.Label($"<size=18><b>{label}</b>: <color=#00FF00>{value:F2}</color></size>", _boldLabelStyle);
            GUILayout.Label($"<size=16><color=#888888>{description}</color></size>", _descriptionStyle);
            GUILayout.Space(5);

            GUILayout.BeginHorizontal();
            GUILayout.Label($"<size=16>{min:F2}</size>", GUILayout.Width(50));
            float newValue = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(500), GUILayout.Height(25));
            GUILayout.Label($"<size=16>{max:F2}</size>", GUILayout.Width(50));
            GUILayout.EndHorizontal();

            // 소수점 2자리로 반올림
            newValue = (float)Math.Round(newValue, 2);

            GUILayout.Space(15);
            GUILayout.EndVertical();

            return newValue;
        }

        /// <summary>
        /// ★ v3.8.12: 큰 폰트 float 슬라이더 (AOE 설정용)
        /// </summary>
        private static float DrawSliderSettingFloatLarge(string label, string description, float value, float min, float max)
        {
            GUILayout.BeginVertical();

            GUILayout.Label($"<size=18><b>{label}</b>: <color=#00FF00>{value:F0}</color></size>", _boldLabelStyle);
            GUILayout.Label($"<size=16><color=#888888>{description}</color></size>", _descriptionStyle);
            GUILayout.Space(5);

            GUILayout.BeginHorizontal();
            GUILayout.Label($"<size=16>{min:F0}</size>", GUILayout.Width(40));
            value = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(500), GUILayout.Height(25));
            GUILayout.Label($"<size=16>{max:F0}</size>", GUILayout.Width(50));
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

            // ★ v3.9.72: Weapon Set Rotation — 캐릭터별 토글
            _editingSettings.EnableWeaponSetRotation = DrawCheckbox(_editingSettings.EnableWeaponSetRotation, L("EnableWeaponSetRotation"));
            GUILayout.Label($"<color=#888888><size=14>{L("EnableWeaponSetRotationDesc")}</size></color>", _descriptionStyle);
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
                _editingSettings.EnableWeaponSetRotation = false;  // ★ 기본값 OFF
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

                // ★ v3.7.15: 사역마(Familiar/Pet) 제외 - IsPet 체크
                return partyMembers
                    .Where(unit => unit != null)
                    .Where(unit => !unit.IsPet)  // 사역마 제외
                    .Select(unit => new CharacterInfo { Id = unit.UniqueId ?? "unknown", Name = unit.CharacterName ?? "Unnamed", Unit = unit })
                    .ToList();
            }
            catch (Exception ex) { Main.LogDebug($"[MainUI] {ex.Message}"); return new List<CharacterInfo>(); }
        }

        private class CharacterInfo
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "Unknown";  // ★ v3.0.46: 기본값 추가
            public BaseUnitEntity Unit { get; set; }
        }
    }
}
