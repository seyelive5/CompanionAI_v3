using UnityEngine;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.UI
{
    /// <summary>
    /// ★ v3.76.0: LLM Combat AI 전용 패널 — 전투 중 화면 오른쪽 고정 표시.
    /// Advisor 분석 → Judge 평가 → 결과 표시의 3단계 페이즈를 시각화.
    /// DecisionOverlayBehaviour.OnGUI()에서 매 프레임 호출.
    /// </summary>
    public static class LLMCombatPanel
    {
        // ═══════════════════════════════════════════════════════════
        // State
        // ═══════════════════════════════════════════════════════════
        private static string _unitName;
        private static string _roleName;
        private static string _phase;          // "Analyzing", "Evaluating", "Complete"
        private static string _strategyType;   // "Aggressive", "Support Allies", etc.
        private static string _planLabel;      // "Plan A", "Plan B"
        private static string[] _planActions;  // individual action lines split by →
        private static float _responseTime;
        private static float _showTime;        // Time.unscaledTime when shown
        private static bool _visible;
        private static int _candidateCount;    // Judge 평가 중인 후보 수

        // ═══════════════════════════════════════════════════════════
        // Cached styles + background texture
        // ═══════════════════════════════════════════════════════════
        private static GUIStyle _titleStyle;
        private static GUIStyle _headerStyle;
        private static GUIStyle _bodyStyle;
        private static GUIStyle _dimStyle;
        private static GUIStyle _boxStyle;
        private static Texture2D _bgTexture;
        private static float _lastScale;
        private static bool _stylesInit;

        /// <summary>Auto-hide duration (seconds) after result shown.</summary>
        private const float AUTO_HIDE_SECONDS = 8f;

        /// <summary>Fade-out duration at the end.</summary>
        private const float FADE_DURATION = 1.5f;

        // ═══════════════════════════════════════════════════════════
        // Public API — TurnOrchestrator에서 호출
        // ═══════════════════════════════════════════════════════════

        /// <summary>Advisor 시작 시 호출 — "Analyzing..." 표시.</summary>
        public static void ShowAnalyzing(string unitName, string roleName)
        {
            _unitName = unitName ?? "Unit";
            _roleName = roleName ?? "?";
            _phase = "Analyzing";
            _strategyType = null;
            _planLabel = null;
            _planActions = null;
            _responseTime = 0f;
            _candidateCount = 0;
            _showTime = Time.unscaledTime;
            _visible = true;
        }

        /// <summary>Judge 시작 시 호출 — "Evaluating N plans..." 표시.</summary>
        public static void ShowEvaluating(string unitName, int candidateCount)
        {
            _unitName = unitName ?? _unitName ?? "Unit";
            _phase = "Evaluating";
            _candidateCount = candidateCount;
            _planLabel = null;
            _planActions = null;
            _showTime = Time.unscaledTime;
            _visible = true;
        }

        /// <summary>
        /// Judge 완료 시 호출 — 최종 결과 표시.
        /// </summary>
        /// <param name="unitName">캐릭터 이름</param>
        /// <param name="roleName">역할 이름 (Tank, DPS 등)</param>
        /// <param name="strategyType">아키타입 태그 (Aggressive, Support Allies 등)</param>
        /// <param name="planLabel">선택된 플랜 라벨 (예: "Plan B")</param>
        /// <param name="planSummary">PlanSummarizer 출력 (→ 로 연결된 액션)</param>
        /// <param name="responseTime">총 응답 시간 (초)</param>
        public static void ShowResult(string unitName, string roleName,
            string strategyType, string planLabel, string planSummary, float responseTime)
        {
            _unitName = unitName ?? "Unit";
            _roleName = roleName ?? "?";
            _phase = "Complete";
            _strategyType = strategyType ?? "Balanced";
            _planLabel = planLabel ?? "Plan A";
            _responseTime = responseTime;
            _showTime = Time.unscaledTime;
            _visible = true;

            // Split plan summary into individual action lines
            // Input format: "[Aggressive] Plan [BuffedAttack] Focus Worker: Buff self with X → Attack Y with Z"
            // We want just the action part after the colon
            _planActions = SplitPlanActions(planSummary);
        }

        /// <summary>패널 숨기기 (턴 종료 시 등).</summary>
        public static void Hide()
        {
            _visible = false;
        }

        /// <summary>전투 종료 시 완전 정리.</summary>
        public static void Reset()
        {
            _visible = false;
            _unitName = null;
            _roleName = null;
            _phase = null;
            _planActions = null;
        }

        // ═══════════════════════════════════════════════════════════
        // IMGUI Rendering — DecisionOverlayBehaviour.OnGUI()에서 호출
        // ═══════════════════════════════════════════════════════════

        public static void DrawGUI()
        {
            if (!_visible) return;
            if (!Main.Enabled) return;
            if (Main.Settings?.ShowLLMOverlay != true) return;

            // Auto-hide timer (Complete 상태에서만)
            float elapsed = Time.unscaledTime - _showTime;
            if (_phase == "Complete" && elapsed >= AUTO_HIDE_SECONDS)
            {
                _visible = false;
                return;
            }

            // Fade-out alpha (Complete 상태에서 마지막 FADE_DURATION 초)
            float alpha = 1f;
            if (_phase == "Complete")
            {
                float fadeStart = AUTO_HIDE_SECONDS - FADE_DURATION;
                if (elapsed > fadeStart)
                {
                    alpha = 1f - ((elapsed - fadeStart) / FADE_DURATION);
                    alpha = Mathf.Clamp01(alpha);
                }
            }

            float scale = Mathf.Clamp(Main.Settings?.UIScale ?? 1.5f, 0.8f, 2.5f);
            InitStyles(scale);

            float width = 340f * scale;
            float padding = 12f * scale;
            float lineHeight = 20f * scale;
            float spacing = 4f * scale;

            // Calculate dynamic height
            int contentLines = 3; // title + unit + blank divider
            if (_phase == "Analyzing" || _phase == "Evaluating")
            {
                contentLines += 1; // phase status line
            }
            else if (_phase == "Complete")
            {
                contentLines += 1; // strategy line
                contentLines += 1; // plan label line
                if (_planActions != null)
                    contentLines += _planActions.Length;
                contentLines += 1; // spacing
                contentLines += 1; // response time
            }

            float height = contentLines * lineHeight + padding * 3 + spacing * 3;

            // Position: right side, vertically centered
            float x = Screen.width - width - 15f;
            float y = (Screen.height - height) / 2f;

            // ── Background ──
            Color prevColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, alpha);

            if (_boxStyle != null)
            {
                GUI.Box(new Rect(x, y, width, height), GUIContent.none, _boxStyle);
            }
            else
            {
                // Fallback if style init failed
                GUI.color = new Color(0.05f, 0.05f, 0.1f, 0.85f * alpha);
                GUI.Box(new Rect(x, y, width, height), "");
            }

            GUI.color = prevColor;

            // ── Content Area ──
            GUILayout.BeginArea(new Rect(x + padding, y + padding, width - padding * 2, height - padding * 2));

            // Title: "LLM Combat AI"
            Color fadedGold = new Color(1f, 0.843f, 0f, alpha); // #FFD700
            _titleStyle.normal.textColor = fadedGold;
            GUILayout.Label("LLM Combat AI", _titleStyle);

            GUILayout.Space(spacing);

            // Unit name + role
            Color nameColor = new Color(1f, 1f, 1f, alpha);
            Color roleColor = new Color(0.53f, 0.53f, 0.53f, alpha); // #888
            _headerStyle.normal.textColor = nameColor;
            _headerStyle.richText = true;
            GUILayout.Label($"<b>{EscapeRichText(_unitName)}</b> <color=#{ColorToHex(roleColor)}>({EscapeRichText(_roleName)})</color>", _headerStyle);

            GUILayout.Space(spacing);

            // Phase-dependent content
            if (_phase == "Analyzing")
            {
                Color phaseColor = new Color(0.6f, 0.8f, 1f, alpha);
                _bodyStyle.normal.textColor = phaseColor;
                DrawAnimatedDots("Strategic analysis", alpha);
            }
            else if (_phase == "Evaluating")
            {
                Color phaseColor = new Color(0.6f, 0.8f, 1f, alpha);
                _bodyStyle.normal.textColor = phaseColor;
                string countText = _candidateCount > 0 ? $" ({_candidateCount} plans)" : "";
                DrawAnimatedDots($"Evaluating{countText}", alpha);
            }
            else if (_phase == "Complete")
            {
                // Strategy type with color
                Color stratColor = GetStrategyColor(_strategyType, alpha);
                _headerStyle.normal.textColor = stratColor;
                GUILayout.Label($"Strategy: {EscapeRichText(_strategyType)}", _headerStyle);

                GUILayout.Space(2f * scale);

                // Plan label
                Color labelColor = new Color(1f, 1f, 1f, alpha);
                _bodyStyle.normal.textColor = labelColor;
                GUILayout.Label($"{EscapeRichText(_planLabel)}:", _bodyStyle);

                // Plan actions
                if (_planActions != null)
                {
                    Color actionColor = new Color(0.8f, 0.8f, 0.8f, alpha); // #CCC
                    _bodyStyle.normal.textColor = actionColor;
                    for (int i = 0; i < _planActions.Length; i++)
                    {
                        string prefix = i < _planActions.Length - 1 ? "  \u2192 " : "  \u2192 "; // → arrow
                        if (i == 0) prefix = "  "; // first line no arrow
                        GUILayout.Label(prefix + EscapeRichText(_planActions[i]), _bodyStyle);
                    }
                }

                GUILayout.Space(spacing);

                // Response time
                Color dimColor = new Color(0.4f, 0.4f, 0.4f, alpha);
                _dimStyle.normal.textColor = dimColor;
                GUILayout.Label($"Response: {_responseTime:F1}s", _dimStyle);
            }

            GUILayout.EndArea();
        }

        // ═══════════════════════════════════════════════════════════
        // Internal helpers
        // ═══════════════════════════════════════════════════════════

        /// <summary>Animated dots for "Analyzing...", "Evaluating..." states.</summary>
        private static void DrawAnimatedDots(string text, float alpha)
        {
            // 0.5초마다 점 1~3개 반복
            int dotCount = (int)(Time.unscaledTime * 2f) % 3 + 1;
            string dots = new string('.', dotCount);
            GUILayout.Label(text + dots, _bodyStyle);
        }

        /// <summary>
        /// PlanSummarizer 출력에서 액션 부분만 추출하여 분리.
        /// 입력: "[Aggressive] Plan [BuffedAttack] Focus Worker: Buff self → Attack X → Move"
        /// 출력: ["Buff self", "Attack X", "Move"]
        /// </summary>
        private static string[] SplitPlanActions(string planSummary)
        {
            if (string.IsNullOrEmpty(planSummary))
                return null;

            // Find the colon after "Plan [xxx]:" to extract action part
            string actionPart = planSummary;
            int colonIdx = planSummary.IndexOf(": ");
            if (colonIdx >= 0 && colonIdx + 2 < planSummary.Length)
            {
                actionPart = planSummary.Substring(colonIdx + 2);
            }

            // Split by " → "
            string[] parts = actionPart.Split(new[] { " \u2192 " }, System.StringSplitOptions.RemoveEmptyEntries);

            // Trim each part
            for (int i = 0; i < parts.Length; i++)
                parts[i] = parts[i].Trim();

            return parts.Length > 0 ? parts : null;
        }

        /// <summary>Strategy type → color mapping.</summary>
        private static Color GetStrategyColor(string strategyType, float alpha)
        {
            if (string.IsNullOrEmpty(strategyType))
                return new Color(0.8f, 0.8f, 0.8f, alpha);

            // Normalize for matching
            string lower = strategyType.ToLowerInvariant();

            if (lower.Contains("aggressive") || lower.Contains("attack"))
                return new Color(1f, 0.4f, 0.4f, alpha);       // #FF6666 Red

            if (lower.Contains("aoe") || lower.Contains("sweep") || lower.Contains("clear"))
                return new Color(0.4f, 0.8f, 1f, alpha);       // #66CCFF Cyan

            if (lower.Contains("debuff"))
                return new Color(1f, 0.8f, 0f, alpha);         // #FFCC00 Yellow

            if (lower.Contains("support") || lower.Contains("allies") || lower.Contains("heal"))
                return new Color(0.4f, 1f, 0.4f, alpha);       // #66FF66 Green

            if (lower.Contains("defensive") || lower.Contains("retreat") || lower.Contains("protect"))
                return new Color(0.4f, 0.4f, 1f, alpha);       // #6666FF Blue

            return new Color(0.8f, 0.8f, 0.8f, alpha);         // #CCC Gray
        }

        /// <summary>Style 초기화 (스케일 변경 시 재생성).</summary>
        private static void InitStyles(float scale)
        {
            if (_stylesInit && _titleStyle != null && Mathf.Abs(_lastScale - scale) < 0.01f)
                return;

            _lastScale = scale;
            _stylesInit = true;

            // Background texture (dark blue-black)
            if (_bgTexture == null)
            {
                _bgTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                _bgTexture.SetPixel(0, 0, new Color(0.05f, 0.05f, 0.12f, 0.88f));
                _bgTexture.Apply();
            }

            // Box style with background texture
            _boxStyle = new GUIStyle
            {
                normal = { background = _bgTexture }
            };

            // Title: bold, larger
            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(16 * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                richText = false
            };

            // Header: unit name, strategy type
            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(14 * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                richText = true,
                wordWrap = true
            };

            // Body: plan actions, status
            _bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(13 * scale),
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleLeft,
                richText = false,
                wordWrap = true
            };

            // Dim: response time, secondary info
            _dimStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(12 * scale),
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleLeft,
                richText = false
            };
        }

        /// <summary>Escape rich text tags to prevent injection.</summary>
        private static string EscapeRichText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("<", "\u200B<"); // zero-width space breaks tag parsing
        }

        /// <summary>Color to hex string (without alpha).</summary>
        private static string ColorToHex(Color c)
        {
            return ColorUtility.ToHtmlStringRGBA(c);
        }
    }
}
