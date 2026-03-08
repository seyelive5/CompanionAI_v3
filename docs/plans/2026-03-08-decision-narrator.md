# DecisionNarrator Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add an ingame IMGUI overlay that shows natural language explanations of AI decisions with pause-on-AI-turn and turn history.

**Architecture:** DecisionNarrator coordinates NarrativeBuilder (text generation), DecisionHistory (ring buffer), and DecisionOverlayUI (IMGUI panel). Hooks into TurnOrchestrator after plan creation. Feeds summaries back to CombatReportCollector for JSON output.

**Tech Stack:** C# / Unity IMGUI / .NET 4.8.1 / UMM mod framework. No unit tests (game mod) — build verification only.

---

## Task 1: ModSettings + Localization — 설정 인프라

**Files:**
- Modify: `Settings/ModSettings.cs:1601` (EnableShipCombatAI 아래에 추가)
- Modify: `Settings/ModSettings.cs:75-99` (Localization 딕셔너리에 키 추가)

**Step 1: Add settings properties**

`ModSettings.cs:1601` (EnableShipCombatAI 바로 아래) 에 추가:

```csharp
/// <summary>★ v3.44.0: AI 결정 오버레이 — 인게임에서 AI의 결정 근거를 자연어로 표시</summary>
public bool EnableDecisionOverlay { get; set; } = false;

/// <summary>★ v3.44.0: AI 턴 자동 일시정지 — 결정 내용을 읽고 수동으로 재개</summary>
public bool PauseOnAITurn { get; set; } = false;
```

**Step 2: Add Localization keys**

`ModSettings.cs` Localization 딕셔너리 `EnableCombatReportDesc` 항목 아래에 추가:

```csharp
["EnableDecisionOverlay"] = new() {
    { Language.English, "AI Decision Overlay" },
    { Language.Korean, "AI 결정 오버레이" },
    { Language.Russian, "Оверлей решений ИИ" },
    { Language.Japanese, "AI判断オーバーレイ" }
},
["EnableDecisionOverlayDesc"] = new() {
    { Language.English, "Show AI decision reasoning in-game during combat. Displays why each companion chose their actions." },
    { Language.Korean, "전투 중 AI의 결정 근거를 인게임으로 표시합니다. 각 동료가 왜 그 행동을 선택했는지 자연어로 설명합니다." },
    { Language.Russian, "Показывать обоснование решений ИИ в бою. Объясняет, почему каждый компаньон выбрал свои действия." },
    { Language.Japanese, "戦闘中にAIの判断根拠をゲーム内に表示。各仲間がその行動を選んだ理由を説明します。" }
},
["PauseOnAITurn"] = new() {
    { Language.English, "Pause on AI Turn" },
    { Language.Korean, "AI 턴 자동 일시정지" },
    { Language.Russian, "Пауза на ходу ИИ" },
    { Language.Japanese, "AIターンで自動一時停止" }
},
["PauseOnAITurnDesc"] = new() {
    { Language.English, "Automatically pause when an AI companion starts their turn. Press 'Continue' to resume." },
    { Language.Korean, "AI 동료의 턴 시작 시 자동으로 일시정지합니다. '계속' 버튼으로 재개하세요." },
    { Language.Russian, "Автоматическая пауза при начале хода ИИ. Нажмите «Продолжить»." },
    { Language.Japanese, "AI仲間のターン開始時に自動一時停止。「続行」ボタンで再開。" }
},
```

**Step 3: Add Narrative template keys**

같은 Localization 딕셔너리에 내러티브 템플릿 추가:

```csharp
// === Decision Narrator Templates ===
["narr_header"] = new() {
    { Language.English, "{0} ({1}) — HP {2}%" },
    { Language.Korean, "{0} ({1}) — HP {2}%" },
    { Language.Russian, "{0} ({1}) — HP {2}%" },
    { Language.Japanese, "{0} ({1}) — HP {2}%" }
},
["narr_attack_threatening"] = new() {
    { Language.English, "Attacking {0} — threatening {1} allies" },
    { Language.Korean, "{0}을(를) 공격합니다 — 아군 {1}명을 위협하고 있어서" },
    { Language.Russian, "Атакует {0} — угрожает {1} союзникам" },
    { Language.Japanese, "{0}を攻撃 — 味方{1}名を脅かしているため" }
},
["narr_attack_killable"] = new() {
    { Language.English, "Attacking {0} — can finish them off" },
    { Language.Korean, "{0}을(를) 공격합니다 — 처치 가능한 적이라서" },
    { Language.Russian, "Атакует {0} — можно добить" },
    { Language.Japanese, "{0}を攻撃 — 倒せる敵だから" }
},
["narr_attack_nearest"] = new() {
    { Language.English, "Attacking {0} — nearest enemy" },
    { Language.Korean, "{0}을(를) 공격합니다 — 가장 가까운 적이라서" },
    { Language.Russian, "Атакует {0} — ближайший враг" },
    { Language.Japanese, "{0}を攻撃 — 最も近い敵" }
},
["narr_attack_best"] = new() {
    { Language.English, "Attacking {0} — highest priority target" },
    { Language.Korean, "{0}을(를) 공격합니다 — 가장 위험한 적이라서" },
    { Language.Russian, "Атакует {0} — приоритетная цель" },
    { Language.Japanese, "{0}を攻撃 — 最優先目標" }
},
["narr_move_approach"] = new() {
    { Language.English, "Moving toward {0} — getting into attack range" },
    { Language.Korean, "{0} 방향으로 이동합니다 — 공격 사거리에 들어가기 위해" },
    { Language.Russian, "Двигается к {0} — входит в зону атаки" },
    { Language.Japanese, "{0}に接近 — 攻撃射程に入るため" }
},
["narr_retreat"] = new() {
    { Language.English, "Retreating — enemy too close" },
    { Language.Korean, "후퇴합니다 — 적이 너무 가까워서" },
    { Language.Russian, "Отступает — враг слишком близко" },
    { Language.Japanese, "後退 — 敵が近すぎる" }
},
["narr_move_heal"] = new() {
    { Language.English, "Moving toward {0} — to heal them" },
    { Language.Korean, "{0}에게 접근합니다 — 치료하기 위해" },
    { Language.Russian, "Двигается к {0} — чтобы лечить" },
    { Language.Japanese, "{0}に接近 — 治療するため" }
},
["narr_heal"] = new() {
    { Language.English, "Healing {0} — HP {1}%" },
    { Language.Korean, "{0}을(를) 치료합니다 — HP {1}%" },
    { Language.Russian, "Лечит {0} — HP {1}%" },
    { Language.Japanese, "{0}を回復 — HP {1}%" }
},
["narr_buff"] = new() {
    { Language.English, "Using {1} on {0}" },
    { Language.Korean, "{0}에게 {1}을(를) 사용합니다" },
    { Language.Russian, "Использует {1} на {0}" },
    { Language.Japanese, "{0}に{1}を使用" }
},
["narr_taunt"] = new() {
    { Language.English, "Taunting — drawing enemy attention to protect allies" },
    { Language.Korean, "적의 관심을 끌어 아군을 보호합니다" },
    { Language.Russian, "Провоцирует — отвлекает врагов от союзников" },
    { Language.Japanese, "挑発 — 味方を守るため敵の注意を引く" }
},
["narr_reload"] = new() {
    { Language.English, "Reloading weapon" },
    { Language.Korean, "무기를 재장전합니다" },
    { Language.Russian, "Перезаряжает оружие" },
    { Language.Japanese, "武器をリロード" }
},
["narr_end_no_ap"] = new() {
    { Language.English, "Turn complete — not enough AP" },
    { Language.Korean, "행동을 마칩니다 — AP가 부족합니다" },
    { Language.Russian, "Ход завершён — недостаточно AP" },
    { Language.Japanese, "ターン終了 — APが足りない" }
},
["narr_end_no_targets"] = new() {
    { Language.English, "Turn complete — no targets in range" },
    { Language.Korean, "행동을 마칩니다 — 공격 가능한 적이 없습니다" },
    { Language.Russian, "Ход завершён — нет целей в зоне" },
    { Language.Japanese, "ターン終了 — 射程内に目標なし" }
},
["narr_end_wait"] = new() {
    { Language.English, "Turn complete — waiting for next turn" },
    { Language.Korean, "행동을 마칩니다 — 다음 턴을 기다립니다" },
    { Language.Russian, "Ход завершён — ожидание следующего хода" },
    { Language.Japanese, "ターン終了 — 次のターンを待機" }
},
["narr_emergency_heal"] = new() {
    { Language.English, "Emergency heal — HP critically low ({0}%)" },
    { Language.Korean, "긴급 치료 — HP가 매우 낮습니다 ({0}%)" },
    { Language.Russian, "Экстренное лечение — HP критически низкий ({0}%)" },
    { Language.Japanese, "緊急回復 — HPが極めて低い ({0}%)" }
},
["narr_familiar_reactivate"] = new() {
    { Language.English, "Reactivating {0} — familiar is unconscious" },
    { Language.Korean, "{0}을(를) 재활성화합니다 — 사역마가 기절 상태" },
    { Language.Russian, "Реактивирует {0} — фамильяр без сознания" },
    { Language.Japanese, "{0}を再起動 — ファミリアが気絶中" }
},
["narr_continue"] = new() {
    { Language.English, "▶ Continue" },
    { Language.Korean, "▶ 계속" },
    { Language.Russian, "▶ Продолжить" },
    { Language.Japanese, "▶ 続行" }
},
["narr_prev_turn"] = new() {
    { Language.English, "◀ Prev" },
    { Language.Korean, "◀ 이전" },
    { Language.Russian, "◀ Назад" },
    { Language.Japanese, "◀ 前へ" }
},
["narr_next_turn"] = new() {
    { Language.English, "Next ▶" },
    { Language.Korean, "다음 ▶" },
    { Language.Russian, "Далее ▶" },
    { Language.Japanese, "次へ ▶" }
},
```

**Step 4: Build and verify**

Run: `MSBuild CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo`
Expected: Build succeeded, no errors.

**Step 5: Commit**

```bash
git add Settings/ModSettings.cs
git commit -m "feat: Add DecisionNarrator settings and localization keys (v3.44.0)"
```

---

## Task 2: DecisionHistory — 턴 히스토리 링 버퍼

**Files:**
- Create: `Diagnostics/DecisionHistory.cs`

**Step 1: Create DecisionHistory**

```csharp
using System.Collections.Generic;

namespace CompanionAI_v3.Diagnostics
{
    /// <summary>
    /// ★ v3.44.0: 턴별 결정 내러티브 히스토리 (최근 N턴 링 버퍼)
    /// DecisionOverlayUI에서 이전/다음 턴 탐색에 사용
    /// </summary>
    public class DecisionHistory
    {
        private const int MAX_ENTRIES = 20;
        private readonly List<NarrativeEntry> _entries = new List<NarrativeEntry>();
        private int _viewIndex = -1;  // -1 = 최신 (라이브)

        public int Count => _entries.Count;
        public int ViewIndex => _viewIndex;
        public bool IsViewingLive => _viewIndex < 0 || _viewIndex >= _entries.Count - 1;

        public void Add(NarrativeEntry entry)
        {
            if (entry == null) return;
            _entries.Add(entry);
            if (_entries.Count > MAX_ENTRIES)
                _entries.RemoveAt(0);
            _viewIndex = _entries.Count - 1;  // 새 항목 추가 시 최신으로 이동
        }

        public NarrativeEntry GetCurrent()
        {
            if (_entries.Count == 0) return null;
            int idx = _viewIndex >= 0 && _viewIndex < _entries.Count
                ? _viewIndex : _entries.Count - 1;
            return _entries[idx];
        }

        public void NavigatePrev()
        {
            if (_viewIndex > 0) _viewIndex--;
        }

        public void NavigateNext()
        {
            if (_viewIndex < _entries.Count - 1) _viewIndex++;
        }

        public void Clear()
        {
            _entries.Clear();
            _viewIndex = -1;
        }

        /// <summary>현재 보고 있는 위치 표시 문자열 (예: "3/10")</summary>
        public string GetPositionLabel()
        {
            if (_entries.Count == 0) return "";
            int display = (_viewIndex >= 0 ? _viewIndex : _entries.Count - 1) + 1;
            return $"{display}/{_entries.Count}";
        }
    }

    /// <summary>
    /// 한 턴의 내러티브 요약
    /// </summary>
    public class NarrativeEntry
    {
        public string UnitName { get; set; }
        public string Role { get; set; }
        public float HPPercent { get; set; }
        public List<string> Lines { get; set; } = new List<string>();
        public int Round { get; set; }
    }
}
```

**Step 2: Build and verify**

**Step 3: Commit**

```bash
git add Diagnostics/DecisionHistory.cs
git commit -m "feat: Add DecisionHistory ring buffer for turn narrative browsing"
```

---

## Task 3: NarrativeBuilder — 자연어 문장 생성 엔진

**Files:**
- Create: `Diagnostics/NarrativeBuilder.cs`

**Step 1: Create NarrativeBuilder**

```csharp
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Data;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Diagnostics
{
    /// <summary>
    /// ★ v3.44.0: TurnPlan + Situation → 사용자용 자연어 요약 생성
    /// 새 분석 로직 없이 기존 데이터를 번역/요약
    /// </summary>
    public static class NarrativeBuilder
    {
        public enum NarrativeLevel
        {
            User,       // 자연어 1~3줄
            Developer   // 나중에 — TargetScorer 점수, AP Budget 등
        }

        /// <summary>
        /// Plan + Situation → NarrativeEntry
        /// </summary>
        public static NarrativeEntry Build(TurnPlan plan, Situation situation,
            NarrativeLevel level = NarrativeLevel.User)
        {
            if (plan == null || situation == null) return null;

            var entry = new NarrativeEntry
            {
                UnitName = situation.Unit?.CharacterName ?? "?",
                Role = ExtractRole(plan),
                HPPercent = situation.HPPercent,
                Round = GetCurrentRound()
            };

            if (plan.AllActions == null || plan.AllActions.Count == 0)
            {
                entry.Lines.Add(L("narr_end_no_targets"));
                return entry;
            }

            // 긴급 상황 헤더
            if (plan.Priority == TurnPriority.Emergency || plan.Priority == TurnPriority.Critical)
            {
                if (situation.HPPercent < 30f)
                    entry.Lines.Add(string.Format(L("narr_emergency_heal"), $"{situation.HPPercent:F0}"));
            }

            foreach (var action in plan.AllActions)
            {
                string line = NarrateAction(action, situation);
                if (line != null)
                    entry.Lines.Add(line);
            }

            // 빈 결과 방지
            if (entry.Lines.Count == 0)
                entry.Lines.Add(L("narr_end_wait"));

            return entry;
        }

        private static string NarrateAction(PlannedAction action, Situation situation)
        {
            if (action == null) return null;

            var target = action.Target?.Entity as BaseUnitEntity;
            string targetName = target?.CharacterName ?? "?";

            switch (action.Type)
            {
                case ActionType.Attack:
                    return NarrateAttack(action, target, targetName, situation);

                case ActionType.Move:
                    return NarrateMove(action, situation);

                case ActionType.Heal:
                    float targetHP = target != null
                        ? GameInterface.CombatCache.GetHPPercent(target) * 100f
                        : 0f;
                    return string.Format(L("narr_heal"), targetName, $"{targetHP:F0}");

                case ActionType.Buff:
                case ActionType.Support:
                    // 도발 체크
                    if (action.Ability != null && AbilityDatabase.IsTaunt(action.Ability))
                        return L("narr_taunt");
                    // 패밀리어 재활성화 체크
                    if (action.IsFamiliarTarget)
                        return string.Format(L("narr_familiar_reactivate"), targetName);
                    string abilityName = action.Ability?.Name ?? "?";
                    string buffTarget = target != null ? targetName
                        : (action.Target?.Point != null ? "" : situation.Unit?.CharacterName ?? "");
                    return string.Format(L("narr_buff"), buffTarget, abilityName);

                case ActionType.Reload:
                    return L("narr_reload");

                case ActionType.EndTurn:
                    return NarrateEndTurn(situation);

                default:
                    return null;
            }
        }

        private static string NarrateAttack(PlannedAction action, BaseUnitEntity target,
            string targetName, Situation situation)
        {
            if (target == null)
                return string.Format(L("narr_attack_best"), targetName);

            // 이유 판별 우선순위: 아군 위협 > 처치 가능 > 최근접 > 기본
            int alliesTargeting = TeamBlackboard.Instance.CountAlliesTargeting(target);
            if (alliesTargeting > 0)
                return string.Format(L("narr_attack_threatening"), targetName, alliesTargeting);

            if (situation.CanKillBestTarget && target == situation.BestTarget)
                return string.Format(L("narr_attack_killable"), targetName);

            if (target == situation.NearestEnemy)
                return string.Format(L("narr_attack_nearest"), targetName);

            return string.Format(L("narr_attack_best"), targetName);
        }

        private static string NarrateMove(PlannedAction action, Situation situation)
        {
            string reason = action.Reason ?? "";

            // Reason 문자열에서 의도 추출
            if (reason.Contains("heal") || reason.Contains("Heal"))
            {
                // "Move to heal X" → X 이름 추출
                string healTarget = ExtractNameFromReason(reason);
                return string.Format(L("narr_move_heal"), healTarget);
            }

            if (reason.Contains("Retreat") || reason.Contains("retreat") || reason.Contains("safe"))
                return L("narr_retreat");

            // 기본: 공격 접근
            string approachTarget = situation.NearestEnemy?.CharacterName
                ?? ExtractNameFromReason(reason) ?? "?";
            return string.Format(L("narr_move_approach"), approachTarget);
        }

        private static string NarrateEndTurn(Situation situation)
        {
            if (situation.CurrentAP < 1f)
                return L("narr_end_no_ap");
            if (situation.HittableEnemies == null || situation.HittableEnemies.Count == 0)
                return L("narr_end_no_targets");
            return L("narr_end_wait");
        }

        private static string ExtractRole(TurnPlan plan)
        {
            if (string.IsNullOrEmpty(plan.Reasoning)) return "?";
            int colonIdx = plan.Reasoning.IndexOf(':');
            return colonIdx > 0 ? plan.Reasoning.Substring(0, colonIdx).Trim() : "?";
        }

        private static string ExtractNameFromReason(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "?";
            // "Move to heal Alice" → "Alice", "Melee position near Mutant" → "Mutant"
            string[] words = reason.Split(' ');
            return words.Length > 0 ? words[words.Length - 1] : "?";
        }

        private static int GetCurrentRound()
        {
            try { return Kingmaker.Game.Instance?.TurnController?.CombatRound ?? 0; }
            catch { return 0; }
        }

        private static string L(string key) => Localization.Get(key);
    }
}
```

**Step 2: Build and verify**

**Step 3: Commit**

```bash
git add Diagnostics/NarrativeBuilder.cs
git commit -m "feat: Add NarrativeBuilder — natural language AI decision summaries"
```

---

## Task 4: DecisionNarrator — 진입점 + CombatReport 연동

**Files:**
- Create: `Diagnostics/DecisionNarrator.cs`

**Step 1: Create DecisionNarrator**

```csharp
using Kingmaker.EntitySystem.Entities;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Diagnostics
{
    /// <summary>
    /// ★ v3.44.0: AI 결정 내러티브 진입점
    /// Plan 생성 후 호출 → 자연어 생성 → UI 표시 → CombatReport 기록
    /// IsEnabled=false면 모든 메서드 즉시 반환 (오버헤드 0)
    /// </summary>
    public class DecisionNarrator
    {
        private static DecisionNarrator _instance;
        public static DecisionNarrator Instance => _instance ??= new DecisionNarrator();

        private readonly DecisionHistory _history = new DecisionHistory();
        public DecisionHistory History => _history;

        public static bool IsEnabled => ModSettings.Instance?.EnableDecisionOverlay ?? false;

        /// <summary>현재 일시정지 상태 (DecisionOverlayUI가 참조)</summary>
        public bool IsPaused { get; set; }

        /// <summary>
        /// Plan 생성 직후 호출 — 내러티브 생성 + UI + CombatReport
        /// </summary>
        public void Narrate(TurnPlan plan, Situation situation, BaseUnitEntity unit)
        {
            if (!IsEnabled) return;
            if (plan == null || situation == null) return;

            var entry = NarrativeBuilder.Build(plan, situation);
            if (entry == null) return;

            _history.Add(entry);

            // CombatReport에도 기록 (JSON 리포트 풍부화)
            if (entry.Lines.Count > 0)
            {
                string summary = string.Join(" | ", entry.Lines);
                CombatReportCollector.Instance.LogPhase($"[Narrator] {summary}");
            }

            // UI 갱신 요청
            DecisionOverlayUI.RequestUpdate();
        }

        /// <summary>전투 종료 시 히스토리 초기화</summary>
        public void OnCombatEnd()
        {
            _history.Clear();
            IsPaused = false;
        }

        /// <summary>일시정지 해제</summary>
        public void Resume()
        {
            IsPaused = false;
            UnityEngine.Time.timeScale = 1f;
        }
    }
}
```

**Step 2: Build and verify**

**Step 3: Commit**

```bash
git add Diagnostics/DecisionNarrator.cs
git commit -m "feat: Add DecisionNarrator — coordinator for narrative, UI, and report"
```

---

## Task 5: DecisionOverlayUI — IMGUI 패널

**Files:**
- Create: `UI/DecisionOverlayUI.cs`

**Step 1: Create DecisionOverlayUI**

```csharp
using UnityEngine;
using CompanionAI_v3.Diagnostics;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.UI
{
    /// <summary>
    /// ★ v3.44.0: AI 결정 오버레이 — IMGUI 기반 인게임 패널
    /// 화면 좌하단 포트레이트 옆에 반투명 패널로 결정 내용 표시
    /// </summary>
    public static class DecisionOverlayUI
    {
        private static bool _needsUpdate;
        private static GUIStyle _panelStyle;
        private static GUIStyle _headerStyle;
        private static GUIStyle _lineStyle;
        private static GUIStyle _buttonStyle;

        private const float PANEL_X = 220f;    // 포트레이트 열 너비 이후
        private const float PANEL_WIDTH = 380f;
        private const float PANEL_BOTTOM_MARGIN = 20f;

        public static void RequestUpdate() => _needsUpdate = true;

        /// <summary>
        /// MainAIPatch 또는 Main.OnGUI에서 호출
        /// 전투 중이고 EnableDecisionOverlay일 때만 그림
        /// </summary>
        public static void OnGUI()
        {
            if (!DecisionNarrator.IsEnabled) return;

            InitStyles();

            var narrator = DecisionNarrator.Instance;
            var entry = narrator.History.GetCurrent();
            if (entry == null) return;

            // 패널 높이 동적 계산
            float lineHeight = 22f;
            float headerHeight = 28f;
            float navHeight = 30f;
            float pauseButtonHeight = narrator.IsPaused ? 35f : 0f;
            float contentHeight = headerHeight + (entry.Lines.Count * lineHeight) + navHeight + pauseButtonHeight + 20f;
            float panelY = Screen.height - contentHeight - PANEL_BOTTOM_MARGIN;

            // 반투명 배경
            GUI.color = new Color(0f, 0f, 0f, 0.75f);
            GUI.Box(new Rect(PANEL_X, panelY, PANEL_WIDTH, contentHeight), "", _panelStyle);
            GUI.color = Color.white;

            GUILayout.BeginArea(new Rect(PANEL_X + 10f, panelY + 5f, PANEL_WIDTH - 20f, contentHeight - 10f));

            // 헤더: 유닛명 (역할) — HP
            string header = string.Format(Localization.Get("narr_header"),
                entry.UnitName, entry.Role, $"{entry.HPPercent:F0}");
            GUILayout.Label(header, _headerStyle);

            // 결정 라인
            foreach (var line in entry.Lines)
            {
                GUILayout.Label($"  • {line}", _lineStyle);
            }

            GUILayout.Space(5f);

            // 네비게이션
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Localization.Get("narr_prev_turn"), _buttonStyle, GUILayout.Width(70f)))
                narrator.History.NavigatePrev();
            GUILayout.FlexibleSpace();
            GUILayout.Label(narrator.History.GetPositionLabel(), _lineStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Localization.Get("narr_next_turn"), _buttonStyle, GUILayout.Width(70f)))
                narrator.History.NavigateNext();
            GUILayout.EndHorizontal();

            // 일시정지 시 "계속" 버튼
            if (narrator.IsPaused)
            {
                GUILayout.Space(5f);
                if (GUILayout.Button(Localization.Get("narr_continue"), _buttonStyle, GUILayout.Height(28f)))
                {
                    narrator.Resume();
                }
            }

            GUILayout.EndArea();
        }

        private static void InitStyles()
        {
            if (_panelStyle != null) return;

            _panelStyle = new GUIStyle(GUI.skin.box);
            _panelStyle.normal.background = Texture2D.whiteTexture;

            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1f, 0.85f, 0.4f) },  // 골드
                richText = true
            };

            _lineStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                normal = { textColor = Color.white },
                wordWrap = true,
                richText = true
            };

            _buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 12
            };
        }
    }
}
```

**Step 2: Build and verify**

**Step 3: Commit**

```bash
git add UI/DecisionOverlayUI.cs
git commit -m "feat: Add DecisionOverlayUI — IMGUI overlay panel for AI decisions"
```

---

## Task 6: 기존 코드 연결 — TurnOrchestrator + MainUI

**Files:**
- Modify: `Core/TurnOrchestrator.cs:256-271` (Narrate 호출 + 일시정지)
- Modify: `UI/MainUI.cs` (설정 체크박스 추가)
- Modify: `GameInterface/MainAIPatch.cs` (OnGUI 호출)
- Modify: `GameInterface/TurnEventHandler.cs` (전투 종료 시 히스토리 초기화)

**Step 1: Hook DecisionNarrator into TurnOrchestrator**

`TurnOrchestrator.cs` 상단 using 추가:
```csharp
using CompanionAI_v3.UI;  // ★ v3.44.0: DecisionOverlayUI
```

`TurnOrchestrator.cs:256-260` — Plan 생성 직후 Narrate + 일시정지:

기존 코드:
```csharp
turnState.Plan = _planner.CreatePlan(situation, turnState);
Data.CompanionDialogue.AnnouncePlan(unit, turnState.Plan);
TeamBlackboard.Instance.RegisterUnitPlan(unitId, turnState.Plan);
CombatReportCollector.Instance.RecordPlan(turnState.Plan);
```

변경 후:
```csharp
turnState.Plan = _planner.CreatePlan(situation, turnState);
Data.CompanionDialogue.AnnouncePlan(unit, turnState.Plan);
TeamBlackboard.Instance.RegisterUnitPlan(unitId, turnState.Plan);
CombatReportCollector.Instance.RecordPlan(turnState.Plan);
// ★ v3.44.0: DecisionNarrator — 자연어 결정 요약 + UI + 일시정지
DecisionNarrator.Instance.Narrate(turnState.Plan, situation, unit);
if (DecisionNarrator.IsEnabled && Main.Settings.PauseOnAITurn)
{
    DecisionNarrator.Instance.IsPaused = true;
    UnityEngine.Time.timeScale = 0f;
}
```

`TurnOrchestrator.cs:267-271` — Replan에도 동일하게 추가:

기존 코드:
```csharp
turnState.Plan = _planner.CreatePlan(situation, turnState);
Data.CompanionDialogue.AnnouncePlan(unit, turnState.Plan);
TeamBlackboard.Instance.RegisterUnitPlan(unitId, turnState.Plan);
CombatReportCollector.Instance.RecordPlan(turnState.Plan);
```

변경 후:
```csharp
turnState.Plan = _planner.CreatePlan(situation, turnState);
Data.CompanionDialogue.AnnouncePlan(unit, turnState.Plan);
TeamBlackboard.Instance.RegisterUnitPlan(unitId, turnState.Plan);
CombatReportCollector.Instance.RecordPlan(turnState.Plan);
// ★ v3.44.0: DecisionNarrator — replan 시에도 내러티브 갱신
DecisionNarrator.Instance.Narrate(turnState.Plan, situation, unit);
```

**Step 2: Hook OnGUI**

`GameInterface/MainAIPatch.cs`에서 OnGUI 호출 경로를 확인하고 `DecisionOverlayUI.OnGUI()` 호출 추가.
또는 `Main.cs`의 UMM OnGUI에서 호출 — 정확한 위치는 기존 패턴에 따름.

참조: UMM은 `Main.OnGUI(UnityModManager.ModEntry modEntry)` 패턴을 사용.
기존 `MainUI.OnGUI()`가 여기서 호출됨 → 같은 위치에 `DecisionOverlayUI.OnGUI()` 추가.

**Step 3: Hook combat end**

`GameInterface/TurnEventHandler.cs`의 전투 종료 이벤트에서:
```csharp
DecisionNarrator.Instance.OnCombatEnd();
```

**Step 4: Add MainUI toggles**

`UI/MainUI.cs`의 Debug & Diagnostics 섹션 (DrawDebugSettings)에:
```csharp
// ★ v3.44.0: Decision Overlay
Main.Settings.EnableDecisionOverlay = GUILayout.Toggle(
    Main.Settings.EnableDecisionOverlay, $"  {L("EnableDecisionOverlay")}", GUILayout.Height(CHECKBOX_SIZE));
if (Main.Settings.EnableDecisionOverlay)
{
    GUILayout.Label($"    {L("EnableDecisionOverlayDesc")}", _descriptionStyle);
    Main.Settings.PauseOnAITurn = GUILayout.Toggle(
        Main.Settings.PauseOnAITurn, $"    {L("PauseOnAITurn")}", GUILayout.Height(CHECKBOX_SIZE));
    GUILayout.Label($"      {L("PauseOnAITurnDesc")}", _descriptionStyle);
}
```

**Step 5: Build and verify**

**Step 6: Commit**

```bash
git add Core/TurnOrchestrator.cs UI/MainUI.cs GameInterface/MainAIPatch.cs GameInterface/TurnEventHandler.cs
git commit -m "feat: Wire DecisionNarrator into TurnOrchestrator, MainUI, and combat lifecycle"
```

---

## Task 7: csproj 업데이트 + 최종 빌드 + 버전 업데이트

**Files:**
- Modify: `CompanionAI_v3.csproj` (새 파일 포함 확인 — SDK-style이므로 자동 포함)
- Modify: `Info.json` (버전 업데이트)

**Step 1: Verify all new files are included**

SDK-style csproj는 자동 포함이지만 확인:
```bash
grep -c "DecisionNarrator\|NarrativeBuilder\|DecisionHistory\|DecisionOverlayUI" CompanionAI_v3.csproj
```
0이면 정상 (SDK-style 자동 포함).

**Step 2: Update version**

`Info.json`:
```json
"Version": "3.44.0"
```

**Step 3: Full rebuild and verify**

Run: `MSBuild CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo`
Expected: Build succeeded, 0 errors.

**Step 4: Commit**

```bash
git add Info.json
git commit -m "v3.44.0: DecisionNarrator — AI decision overlay system"
```

---

## Task Summary

| Task | 내용 | 새 파일 | 변경 파일 |
|------|------|---------|----------|
| 1 | 설정 + 다국어 | — | ModSettings.cs |
| 2 | 히스토리 링 버퍼 | DecisionHistory.cs | — |
| 3 | 자연어 생성 엔진 | NarrativeBuilder.cs | — |
| 4 | 진입점 + 리포트 연동 | DecisionNarrator.cs | — |
| 5 | IMGUI 오버레이 패널 | DecisionOverlayUI.cs | — |
| 6 | 기존 코드 연결 | — | TurnOrchestrator.cs, MainUI.cs, MainAIPatch.cs, TurnEventHandler.cs |
| 7 | 빌드 + 버전 | — | Info.json |
