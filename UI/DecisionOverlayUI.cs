using UnityEngine;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.UI
{
    /// <summary>
    /// ★ v3.48.0: IMGUI OnGUI를 매 프레임 호출하기 위한 MonoBehaviour
    /// Main.Load()에서 GameObject에 부착. DontDestroyOnLoad로 영속.
    /// </summary>
    public class DecisionOverlayBehaviour : MonoBehaviour
    {
        private void OnGUI()
        {
            TacticalOverlayUI.OnGUI();
            LLMCombatPanel.DrawGUI();
        }
    }

    /// <summary>
    /// ★ v3.48.0: Tactical Narrator 오버레이 — 턴 시작 시 동료 대사 표시
    /// 5초 자동 페이드아웃, 턴 종료 시 즉시 소멸
    /// </summary>
    public static class TacticalOverlayUI
    {
        private static GameObject _overlayGO;

        // 현재 표시 중인 데이터
        private static string _unitName;
        private static string[] _lines;
        private static Color _nameColor = new Color(1f, 0.85f, 0.4f);

        // 타이머
        private static float _showStartTime;
        private static float _duration;
        private static bool _isVisible;

        // 스타일 캐시
        private static GUIStyle _nameStyle;
        private static GUIStyle _lineStyle;
        private static float _lastScale;

        /// <summary>오버레이 GameObject 초기화 (Main.Load에서 호출)</summary>
        public static void Initialize()
        {
            if (_overlayGO != null) return;
            _overlayGO = new GameObject("CompanionAI_TacticalOverlay");
            _overlayGO.AddComponent<DecisionOverlayBehaviour>();
            Object.DontDestroyOnLoad(_overlayGO);
        }

        /// <summary>오버레이 GameObject 파괴 (Main.Unload에서 호출)</summary>
        public static void Destroy()
        {
            if (_overlayGO != null)
            {
                Object.Destroy(_overlayGO);
                _overlayGO = null;
            }
            _nameStyle = null;
            _lineStyle = null;
            _isVisible = false;
        }

        /// <summary>
        /// 대사 표시 시작
        /// </summary>
        /// <param name="unitName">캐릭터 이름</param>
        /// <param name="lines">2~3줄 대사</param>
        /// <param name="nameColor">캐릭터 이름 색상</param>
        /// <param name="duration">표시 시간 (초)</param>
        // ★ Phase 3: EnableDecisionOverlay 설정 무시 플래그
        private static bool _bypassOverlayCheck;

        public static void Show(string unitName, string[] lines, Color nameColor, float duration = 5f)
        {
            if (lines == null || lines.Length == 0) return;

            _unitName = unitName;
            _lines = lines;
            _nameColor = nameColor;
            _duration = duration;
            _showStartTime = Time.unscaledTime;
            _isVisible = true;
            _bypassOverlayCheck = false;
        }

        /// <summary>★ Phase 3: EnableDecisionOverlay 설정과 무관하게 표시 (LLM Judge용).</summary>
        public static void ShowAlways(string unitName, string[] lines, Color nameColor, float duration = 5f)
        {
            Show(unitName, lines, nameColor, duration);
            _bypassOverlayCheck = true;
        }

        /// <summary>즉시 숨기기 (턴 종료 시)</summary>
        public static void Hide()
        {
            _isVisible = false;
            _lines = null;
        }

        /// <summary>현재 표시 중인지</summary>
        public static bool IsVisible => _isVisible;

        /// <summary>매 프레임 IMGUI 렌더링</summary>
        public static void OnGUI()
        {
            if (!_isVisible) return;
            if (_lines == null || _lines.Length == 0) return;
            if (!Main.Enabled) return;
            if (!_bypassOverlayCheck && ModSettings.Instance?.EnableDecisionOverlay != true) return;

            // 타이머 체크
            float elapsed = Time.unscaledTime - _showStartTime;
            if (elapsed >= _duration)
            {
                _isVisible = false;
                return;
            }

            // 페이드아웃: 마지막 1초
            float alpha = 1f;
            float fadeStart = _duration - 1f;
            if (elapsed > fadeStart)
            {
                alpha = 1f - ((elapsed - fadeStart) / 1f);
            }

            float scale = Mathf.Clamp(
                ModSettings.Instance?.DecisionOverlayScale ?? 1f, 0.8f, 2.0f);
            InitStyles(scale);

            // 레이아웃 계산
            float padding = 12f * scale;
            float lineHeight = 22f * scale;
            float nameHeight = 26f * scale;
            float maxWidth = 500f * scale;

            float panelHeight = nameHeight + (_lines.Length * lineHeight) + padding * 2;
            float panelWidth = maxWidth + padding * 2;

            // ★ v3.48.0: 초상화 바로 오른쪽 위 — 말풍선 느낌
            float panelX = (Screen.width / 2f) + 80f * scale;
            float panelY = Screen.height - panelHeight - 180f * scale;

            // 반투명 배경
            Color bgColor = new Color(0f, 0f, 0f, 0.7f * alpha);
            GUI.color = bgColor;
            GUI.Box(new Rect(panelX, panelY, panelWidth, panelHeight), "");
            GUI.color = Color.white;

            float y = panelY + padding;

            // 캐릭터 이름
            Color fadedNameColor = _nameColor;
            fadedNameColor.a = alpha;
            _nameStyle.normal.textColor = fadedNameColor;
            GUI.Label(new Rect(panelX + padding, y, maxWidth, nameHeight), _unitName, _nameStyle);
            y += nameHeight;

            // 대사 라인들
            Color lineColor = new Color(0.9f, 0.9f, 0.85f, alpha);
            _lineStyle.normal.textColor = lineColor;
            foreach (var line in _lines)
            {
                if (string.IsNullOrEmpty(line)) continue;
                GUI.Label(new Rect(panelX + padding, y, maxWidth, lineHeight), line, _lineStyle);
                y += lineHeight;
            }
        }

        private static void InitStyles(float scale)
        {
            if (_nameStyle != null && Mathf.Abs(_lastScale - scale) < 0.01f) return;
            _lastScale = scale;

            _nameStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(16 * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft
            };

            _lineStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(14 * scale),
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleLeft,
                wordWrap = true
            };
        }
    }

    /// <summary>
    /// ★ v3.48.0: 호환성 스텁 — Main.cs에서 DirectiveOverlayUI.Initialize() 호출 유지
    /// </summary>
    public static class DirectiveOverlayUI
    {
        public static void Initialize() => TacticalOverlayUI.Initialize();
        public static void Destroy() => TacticalOverlayUI.Destroy();
    }
}
