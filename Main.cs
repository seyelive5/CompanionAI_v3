using System;
using System.Reflection;
using HarmonyLib;
using UnityModManagerNet;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Data;
using CompanionAI_v3.Settings;
using CompanionAI_v3.UI;
using CompanionAI_v3.GameInterface;

namespace CompanionAI_v3
{
    /// <summary>
    /// CompanionAI v3.0 - 완전히 재설계된 동료 AI 시스템
    ///
    /// 핵심 원칙: TurnPlanner가 모든 결정, 게임은 실행만
    /// </summary>
    public static class Main
    {
        public static bool Enabled { get; private set; }
        public static UnityModManager.ModEntry ModEntry { get; private set; }
        public static ModSettings Settings => ModSettings.Instance;
        public static string ModPath => ModEntry?.Path ?? "";
        private static Harmony _harmony;

        /// <summary>
        /// 모드 로드 진입점
        /// </summary>
        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            ModEntry = modEntry;
            modEntry.OnToggle = OnToggle;
            modEntry.OnGUI = OnGUI;
            modEntry.OnSaveGUI = OnSaveGUI;

            // 설정 로드
            ModSettings.Load(modEntry);

            // ★ v3.9.40: 저장된 언어 설정을 Localization에 즉시 반영
            // (UI를 열기 전에도 올바른 언어로 대사가 출력되도록)
            Localization.CurrentLanguage = ModSettings.Instance.UILanguage;

            // ★ v3.5.96: PerSaveSettings에 모드 경로 설정
            PerSaveSettings.SetModPath(modEntry.Path);

            // ★ v3.1.30: Response Curves 초기화
            CurvePresets.Initialize();

            // ★ v3.9.36: 대사 JSON 로드 (없으면 기본값 내보내기)
            DialogueLocalization.LoadFromJson(modEntry.Path);

            Log("CompanionAI v3.0 loaded successfully");
            return true;
        }

        /// <summary>
        /// 모드 활성화/비활성화
        /// </summary>
        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            Enabled = value;

            if (value)
            {
                try
                {
                    _harmony = new Harmony(modEntry.Info.Id);
                    _harmony.PatchAll(Assembly.GetExecutingAssembly());

                    // ★ v3.5.95: private 메서드 수동 패치 (세이브/로드)
                    SaveLoadPatch.ApplyManualPatches(_harmony);

                    Log("Harmony patches applied");

                    // ★ v3.0.76: 게임 턴 이벤트 구독
                    TurnEventHandler.Instance.Subscribe();
                }
                catch (Exception ex)
                {
                    LogError($"Failed to apply patches: {ex.Message}");
                    return false;
                }
            }
            else
            {
                try
                {
                    // ★ v3.0.76: 게임 턴 이벤트 구독 해제
                    TurnEventHandler.Instance.Unsubscribe();

                    _harmony?.UnpatchAll(modEntry.Info.Id);
                    Log("Harmony patches removed");
                }
                catch (Exception ex)
                {
                    LogError($"Failed to remove patches: {ex.Message}");
                }
            }

            return true;
        }

        /// <summary>
        /// GUI 렌더링
        /// </summary>
        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            MainUI.OnGUI();
        }

        /// <summary>
        /// 설정 저장
        /// </summary>
        private static void OnSaveGUI(UnityModManager.ModEntry modEntry)
        {
            ModSettings.Save();
        }

        #region Logging

        /// <summary>
        /// ★ v3.8.78: 디버그 로깅 활성화 여부 (호출자 측 가드용)
        /// Main.LogDebug($"...") 호출 전에 이 프로퍼티로 가드하면
        /// 디버그 모드 OFF 시 $"..." 문자열 할당 자체를 방지
        /// </summary>
        public static bool IsDebugEnabled =>
            ModSettings.Instance?.EnableDebugLogging ?? false;

        public static void Log(string message)
        {
            ModEntry?.Logger?.Log($"[CompanionAI] {message}");
        }

        public static void LogDebug(string message)
        {
            if (ModSettings.Instance?.EnableDebugLogging ?? false)
            {
                ModEntry?.Logger?.Log($"[CompanionAI][DEBUG] {message}");
            }
        }

        public static void LogError(string message)
        {
            ModEntry?.Logger?.Error($"[CompanionAI][ERROR] {message}");
        }

        public static void LogWarning(string message)
        {
            ModEntry?.Logger?.Warning($"[CompanionAI][WARN] {message}");
        }

        #endregion
    }
}
