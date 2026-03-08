using UnityEngine;
using CompanionAI_v3.Diagnostics;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.UI
{
    /// <summary>
    /// ★ v3.44.0: IMGUI OnGUI를 매 프레임 호출하기 위한 MonoBehaviour
    /// Main.Load()에서 GameObject에 부착. DontDestroyOnLoad로 영속.
    /// </summary>
    public class DecisionOverlayBehaviour : MonoBehaviour
    {
        private void OnGUI()
        {
            DecisionOverlayUI.OnGUI();
        }
    }

    /// <summary>
    /// ★ v3.44.0: AI 결정 오버레이 — IMGUI 기반 인게임 패널
    /// 화면 좌하단 포트레이트 옆에 반투명 패널로 결정 내용 표시
    /// </summary>
    public static class DecisionOverlayUI
    {
        private static GUIStyle _panelStyle;
        private static GUIStyle _headerStyle;
        private static GUIStyle _lineStyle;
        private static GUIStyle _buttonStyle;
        private static GameObject _overlayGO;
        private static float _lastScale;  // 스케일 변경 감지

        // 기본 크기 (scale=1.0 기준)
        private const float BASE_PANEL_X = 220f;
        private const float BASE_PANEL_WIDTH = 380f;
        private const float BASE_PANEL_BOTTOM_MARGIN = 20f;
        private const int BASE_HEADER_FONT = 15;
        private const int BASE_LINE_FONT = 13;
        private const int BASE_BUTTON_FONT = 12;

        public static void RequestUpdate() { /* 현재는 매 프레임 렌더링 — 향후 최적화 여지 */ }

        /// <summary>
        /// 모드 로드 시 호출 — IMGUI 렌더링용 GameObject 생성
        /// </summary>
        public static void Initialize()
        {
            if (_overlayGO != null) return;
            _overlayGO = new GameObject("CompanionAI_DecisionOverlay");
            _overlayGO.AddComponent<DecisionOverlayBehaviour>();
            Object.DontDestroyOnLoad(_overlayGO);
        }

        /// <summary>
        /// 모드 비활성화 시 호출 — GameObject 정리
        /// </summary>
        public static void Destroy()
        {
            if (_overlayGO != null)
            {
                Object.Destroy(_overlayGO);
                _overlayGO = null;
            }
            _panelStyle = null;
        }

        /// <summary>
        /// DecisionOverlayBehaviour.OnGUI()에서 매 프레임 호출
        /// 전투 중이고 EnableDecisionOverlay일 때만 그림
        /// </summary>
        public static void OnGUI()
        {
            if (!DecisionNarrator.IsEnabled) return;

            float scale = Mathf.Clamp(ModSettings.Instance?.DecisionOverlayScale ?? 1f, 0.8f, 2.0f);
            InitStyles(scale);

            var narrator = DecisionNarrator.Instance;
            var entry = narrator.History.GetCurrent();
            if (entry == null) return;

            float panelWidth = BASE_PANEL_WIDTH * scale;
            float panelX = BASE_PANEL_X * scale;

            // 패널 높이 동적 계산
            float lineHeight = 22f * scale;
            float headerHeight = 28f * scale;
            float navHeight = 30f * scale;
            float pauseButtonHeight = narrator.IsPaused ? 35f * scale : 0f;
            float contentHeight = headerHeight + (entry.Lines.Count * lineHeight) + navHeight + pauseButtonHeight + 20f * scale;
            float panelY = Screen.height - contentHeight - BASE_PANEL_BOTTOM_MARGIN;

            // 반투명 배경
            GUI.color = new Color(0f, 0f, 0f, 0.75f);
            GUI.Box(new Rect(panelX, panelY, panelWidth, contentHeight), "", _panelStyle);
            GUI.color = Color.white;

            float pad = 10f * scale;
            GUILayout.BeginArea(new Rect(panelX + pad, panelY + 5f, panelWidth - pad * 2f, contentHeight - 10f));

            // 헤더: 유닛명 (역할) — HP
            string header = string.Format(Localization.Get("narr_header"),
                entry.UnitName, entry.Role, $"{entry.HPPercent:F0}");
            GUILayout.Label(header, _headerStyle);

            // 결정 라인
            foreach (var line in entry.Lines)
            {
                GUILayout.Label($"  \u2022 {line}", _lineStyle);
            }

            GUILayout.Space(5f * scale);

            // 네비게이션
            float navBtnWidth = 70f * scale;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Localization.Get("narr_prev_turn"), _buttonStyle, GUILayout.Width(navBtnWidth)))
                narrator.History.NavigatePrev();
            GUILayout.FlexibleSpace();
            GUILayout.Label(narrator.History.GetPositionLabel(), _lineStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Localization.Get("narr_next_turn"), _buttonStyle, GUILayout.Width(navBtnWidth)))
                narrator.History.NavigateNext();
            GUILayout.EndHorizontal();

            // 일시정지 시 "계속" 버튼
            if (narrator.IsPaused)
            {
                GUILayout.Space(5f * scale);
                if (GUILayout.Button(Localization.Get("narr_continue"), _buttonStyle, GUILayout.Height(28f * scale)))
                {
                    narrator.Resume();
                }
            }

            GUILayout.EndArea();
        }

        private static void InitStyles(float scale)
        {
            // 스케일 변경 시 스타일 재생성
            if (_panelStyle != null && Mathf.Abs(_lastScale - scale) < 0.01f) return;
            _lastScale = scale;

            _panelStyle = new GUIStyle(GUI.skin.box);
            _panelStyle.normal.background = Texture2D.whiteTexture;

            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(BASE_HEADER_FONT * scale),
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1f, 0.85f, 0.4f) },  // 골드
                richText = true
            };

            _lineStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(BASE_LINE_FONT * scale),
                normal = { textColor = Color.white },
                wordWrap = true,
                richText = true
            };

            _buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = Mathf.RoundToInt(BASE_BUTTON_FONT * scale)
            };
        }
    }
}
