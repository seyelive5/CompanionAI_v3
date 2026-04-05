# Strategic Directive (전략 지시) 구현 계획

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 유저가 유닛별로 전략 방향(자동/공격/방어/지원/후퇴/대기)을 설정하면 AI가 해당 방향에 맞게 행동하는 시스템

**Architecture:**
- `UserDirective` enum + `DirectiveManager` 정적 클래스 (전투 중 유닛별 지시 관리)
- `TurnStrategyPlanner.Evaluate()`에 directive 전달 → 시드 스코어 가중치 적용
- `TurnPlanner.CreatePlan()`에서 Hold/Retreat 특수 처리
- `DecisionOverlayUI` 완전 재작성 → 6개 버튼 가로 나열 UI
- 기존 DecisionNarrator/NarrativeBuilder/pause 메커니즘 전체 제거

**Tech Stack:** C# / Unity IMGUI / .NET 4.8.1

---

## Task 1: UserDirective enum + DirectiveManager 생성

**Files:**
- Create: `Core/UserDirective.cs`

**구현:**
```csharp
namespace CompanionAI_v3.Core
{
    public enum UserDirective
    {
        Auto,        // AI 자유 판단 (기본값)
        Aggressive,  // 킬시퀀스/AoE/콤보 우선, 후퇴 억제
        Defensive,   // 버프/디버프 우선, 후퇴 적극적
        Support,     // 힐/팀버프 우선
        Retreat,     // 공격 스킵 → 안전 이동 → EndTurn
        Hold         // 즉시 EndTurn
    }

    /// <summary>
    /// 유닛별 전략 지시 관리. 전투 중 유저가 IMGUI 버튼으로 변경.
    /// 전투 간 유지 (유저가 바꿀 때까지 고정).
    /// </summary>
    public static class DirectiveManager
    {
        private static readonly Dictionary<string, UserDirective> _directives
            = new Dictionary<string, UserDirective>();

        public static UserDirective GetDirective(string unitId)
        {
            if (string.IsNullOrEmpty(unitId)) return UserDirective.Auto;
            return _directives.TryGetValue(unitId, out var d) ? d : UserDirective.Auto;
        }

        public static void SetDirective(string unitId, UserDirective directive)
        {
            if (string.IsNullOrEmpty(unitId)) return;
            _directives[unitId] = directive;
        }

        /// <summary>전투 종료 시 Hold/Retreat는 Auto로 리셋 (일회성 지시)</summary>
        public static void OnCombatEnd()
        {
            var toReset = new List<string>();
            foreach (var kvp in _directives)
                if (kvp.Value == UserDirective.Retreat || kvp.Value == UserDirective.Hold)
                    toReset.Add(kvp.Key);
            foreach (var key in toReset)
                _directives[key] = UserDirective.Auto;
        }

        public static void ClearAll() => _directives.Clear();
    }
}
```

---

## Task 2: TurnPlanner — Hold/Retreat 특수 처리

**Files:**
- Modify: `Planning/TurnPlanner.cs:41-88` (CreatePlan 메서드)

**구현:**

`CreatePlan()` 메서드 시작 부분, role 해석 후 switch 문 전에 삽입:

```csharp
// ★ v3.46.0: UserDirective 특수 처리 — Hold/Retreat는 계획 수립 자체를 바이패스
var directive = Core.DirectiveManager.GetDirective(unitId);

if (directive == Core.UserDirective.Hold)
{
    Main.Log($"[TurnPlanner] {situation.Unit.CharacterName}: UserDirective=Hold → EndTurn");
    CombatReportCollector.Instance.LogPhase("Directive: Hold → EndTurn");
    return new TurnPlan(
        new List<PlannedAction> { PlannedAction.EndTurn("UserDirective: Hold") },
        TurnPriority.EndTurn, "Hold directive");
}

if (directive == Core.UserDirective.Retreat)
{
    Main.Log($"[TurnPlanner] {situation.Unit.CharacterName}: UserDirective=Retreat → SafeMove+EndTurn");
    CombatReportCollector.Instance.LogPhase("Directive: Retreat → SafeMove+EndTurn");
    // BasePlan.FindRetreatPosition() 사용을 위해 DPSPlan에 위임하되,
    // Phase 4 공격을 강제 스킵하는 것은 directive를 TurnState에 전파하여 처리
}
```

또한 `situation`에 directive를 전달하기 위해 `TurnState`에 현재 directive를 기록:

```csharp
// CreatePlan() 상단에 추가
turnState.CurrentDirective = directive;
```

**TurnState 수정** (`Core/TurnState.cs`):
```csharp
/// <summary>★ v3.46.0: 현재 턴에 적용된 유저 지시</summary>
public UserDirective CurrentDirective { get; set; } = UserDirective.Auto;
```

---

## Task 3: TurnStrategyPlanner — Directive 기반 스코어 가중치

**Files:**
- Modify: `Planning/TurnStrategyPlanner.cs:102` (Evaluate 시그니처)
- Modify: `Planning/TurnStrategyPlanner.cs:119` (EvaluateInternal 시그니처)
- Modify: scoring section (GetWeightedScore 호출부)

**구현:**

3a. 시그니처에 directive 매개변수 추가:
```csharp
public static TurnStrategy Evaluate(Situation situation,
    Settings.AIRole role = Settings.AIRole.DPS,
    UserDirective directive = UserDirective.Auto)
{
    return EvaluateInternal(situation, role, directive);
}

private static TurnStrategy EvaluateInternal(Situation situation,
    Settings.AIRole role, UserDirective directive)
```

3b. 시드 스코어링 후 가중치 적용 (GetWeightedScore 근처):

```csharp
/// <summary>★ v3.46.0: UserDirective별 시드 가중치</summary>
private static float GetDirectiveWeight(SequenceType type, UserDirective directive)
{
    if (directive == UserDirective.Auto) return 1.0f;

    switch (directive)
    {
        case UserDirective.Aggressive:
            // 킬시퀀스/AoE/콤보 강화, 방어적 선택지 약화
            switch (type)
            {
                case SequenceType.KillSequence: return 1.3f;
                case SequenceType.AoEFocus:
                case SequenceType.BuffedAoE:
                case SequenceType.AoERnGChain:
                case SequenceType.BuffedRnGAoE: return 1.2f;
                case SequenceType.Standard:
                case SequenceType.RnGChain:
                case SequenceType.BuffedRnGChain: return 1.1f;
                default: return 0.9f;
            }

        case UserDirective.Defensive:
            // 버프/디버프 강화
            switch (type)
            {
                case SequenceType.BuffedAttack:
                case SequenceType.DebuffedAttack: return 1.3f;
                case SequenceType.BuffedAoE:
                case SequenceType.BuffedRnGChain: return 1.2f;
                default: return 0.9f;
            }

        case UserDirective.Support:
            // Support directive는 TurnStrategyPlanner 스코어링이 아닌
            // BasePlan Phase 순서에서 처리 (힐/버프 임계값 완화)
            // 여기서는 공격 시드 전체 약화
            return 0.8f;

        default:
            return 1.0f;
    }
}
```

최종 스코어에 적용:
```csharp
float finalScore = GetWeightedScore(candidate.Score, candidate.Type, role)
                 * GetDirectiveWeight(candidate.Type, directive);
```

3c. 호출부 수정 — `BasePlan.EvaluateOrReuseStrategy()`에서 directive 전달:

**Files:** `Planning/Plans/BasePlan.cs` (EvaluateOrReuseStrategy 메서드)

```csharp
// 기존: TurnStrategyPlanner.Evaluate(situation, effectiveRole);
// 변경:
TurnStrategyPlanner.Evaluate(situation, effectiveRole, turnState.CurrentDirective);
```

---

## Task 4: BasePlan — Directive별 Phase 행동 변경

**Files:**
- Modify: `Planning/Plans/BasePlan.cs` (Phase 관련 메서드들)

**구현:**

4a. **Retreat directive** — Phase 4 공격 스킵, 안전 이동 강제:
```csharp
// Phase 4 진입부에서:
if (turnState.CurrentDirective == UserDirective.Retreat)
{
    // 공격 계획 전체 스킵 → FindRetreatPosition → Move → EndTurn
    var retreatPos = FindRetreatPosition(situation);
    if (retreatPos.HasValue)
        actions.Add(PlannedAction.Move(retreatPos.Value, "Retreat directive"));
    actions.Add(PlannedAction.EndTurn("Retreat directive"));
    return; // Phase 4 이후 스킵
}
```

4b. **Defensive directive** — 후퇴 임계값 완화:
```csharp
// 기존 후퇴 판단에서:
float retreatThreshold = turnState.CurrentDirective == UserDirective.Defensive
    ? 0.5f   // HP 50% 이하면 후퇴 고려
    : 0.3f;  // 기존: HP 30%
```

4c. **Support directive** — 힐/버프 임계값 완화:
```csharp
// Phase 1 (Emergency Heal) 임계값:
float healThreshold = turnState.CurrentDirective == UserDirective.Support
    ? 0.7f   // HP 70% 이하 아군도 힐 대상
    : 0.3f;  // 기존: HP 30%

// Phase 2 (Buff) — Support directive면 버프 항상 시도
bool shouldBuff = turnState.CurrentDirective == UserDirective.Support
    || strategy?.ShouldBuffBeforeAttack == true;
```

4d. **Aggressive directive** — 후퇴 억제:
```csharp
// 후퇴 판단에서:
if (turnState.CurrentDirective == UserDirective.Aggressive)
{
    // 후퇴 하지 않음 (HP 15% 미만 긴급 후퇴만 허용)
    if (situation.HPPercent > 15f) shouldRetreat = false;
}
```

---

## Task 5: DecisionOverlayUI 완전 재작성 — 6버튼 전략 지시 UI

**Files:**
- Rewrite: `UI/DecisionOverlayUI.cs`

**구현:**

```csharp
using UnityEngine;
using CompanionAI_v3.Core;
using CompanionAI_v3.Settings;
using Kingmaker;
using Kingmaker.Controllers.TurnBased;
using Kingmaker.EntitySystem.Entities;

namespace CompanionAI_v3.UI
{
    public class DirectiveOverlayBehaviour : MonoBehaviour
    {
        private void OnGUI() => DirectiveOverlayUI.OnGUI();
    }

    /// <summary>
    /// ★ v3.46.0: 전략 지시 오버레이 — 유닛별 6버튼 가로 나열
    /// [유닛명]  [자동] [공격] [방어] [지원] [후퇴] [대기]
    /// 선택된 버튼만 색상 강조. 전투 중에만 표시.
    /// </summary>
    public static class DirectiveOverlayUI
    {
        private static GameObject _overlayGO;
        private static GUIStyle _labelStyle;
        private static GUIStyle _normalBtnStyle;
        private static GUIStyle _selectedBtnStyle;
        private static float _lastScale;

        private static readonly UserDirective[] _allDirectives = {
            UserDirective.Auto, UserDirective.Aggressive, UserDirective.Defensive,
            UserDirective.Support, UserDirective.Retreat, UserDirective.Hold
        };

        public static void Initialize()
        {
            if (_overlayGO != null) return;
            _overlayGO = new GameObject("CompanionAI_DirectiveOverlay");
            _overlayGO.AddComponent<DirectiveOverlayBehaviour>();
            Object.DontDestroyOnLoad(_overlayGO);
        }

        public static void Destroy()
        {
            if (_overlayGO != null)
            {
                Object.Destroy(_overlayGO);
                _overlayGO = null;
            }
            _labelStyle = null;
        }

        public static void OnGUI()
        {
            // 전투 중에만 표시
            if (!Main.Enabled) return;
            if (ModSettings.Instance?.EnableDecisionOverlay != true) return;
            var tc = Game.Instance?.TurnController;
            if (tc == null || !tc.TurnBasedModeActive) return;

            // 현재 턴 유닛
            var unit = tc.CurrentUnit as BaseUnitEntity;
            if (unit == null) return;

            // AI 제어 유닛만 (ShouldControl 체크)
            if (!TurnOrchestrator.Instance.ShouldControl(unit)) return;

            float scale = Mathf.Clamp(
                ModSettings.Instance?.DecisionOverlayScale ?? 1f, 0.8f, 2.0f);
            InitStyles(scale);

            string unitId = unit.UniqueId;
            string unitName = unit.CharacterName;
            var current = DirectiveManager.GetDirective(unitId);

            // 레이아웃 계산
            float btnWidth = 55f * scale;
            float btnHeight = 28f * scale;
            float labelWidth = 120f * scale;
            float spacing = 4f * scale;
            float totalWidth = labelWidth + (_allDirectives.Length * (btnWidth + spacing));
            float panelX = 220f * scale;
            float panelY = Screen.height - btnHeight - 30f * scale;

            // 반투명 배경
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.Box(new Rect(panelX - 5f, panelY - 5f,
                totalWidth + 10f, btnHeight + 10f), "");
            GUI.color = Color.white;

            // 유닛명
            GUI.Label(new Rect(panelX, panelY + 4f * scale, labelWidth, btnHeight),
                unitName, _labelStyle);

            // 6개 버튼
            float x = panelX + labelWidth;
            foreach (var dir in _allDirectives)
            {
                bool isSelected = (dir == current);
                var style = isSelected ? _selectedBtnStyle : _normalBtnStyle;
                string label = GetDirectiveLabel(dir);

                if (GUI.Button(new Rect(x, panelY, btnWidth, btnHeight), label, style))
                {
                    DirectiveManager.SetDirective(unitId, dir);
                    Main.Log($"[Directive] {unitName}: → {dir}");
                }
                x += btnWidth + spacing;
            }
        }

        private static string GetDirectiveLabel(UserDirective dir)
        {
            var lang = ModSettings.Instance?.UILanguage ?? Language.English;
            switch (dir)
            {
                case UserDirective.Auto:
                    return lang == Language.Korean ? "자동" :
                           lang == Language.Japanese ? "自動" :
                           lang == Language.Russian ? "Авто" : "Auto";
                case UserDirective.Aggressive:
                    return lang == Language.Korean ? "공격" :
                           lang == Language.Japanese ? "攻撃" :
                           lang == Language.Russian ? "Атака" : "Aggro";
                case UserDirective.Defensive:
                    return lang == Language.Korean ? "방어" :
                           lang == Language.Japanese ? "防御" :
                           lang == Language.Russian ? "Защита" : "Def";
                case UserDirective.Support:
                    return lang == Language.Korean ? "지원" :
                           lang == Language.Japanese ? "支援" :
                           lang == Language.Russian ? "Помощь" : "Sup";
                case UserDirective.Retreat:
                    return lang == Language.Korean ? "후퇴" :
                           lang == Language.Japanese ? "撤退" :
                           lang == Language.Russian ? "Отход" : "Back";
                case UserDirective.Hold:
                    return lang == Language.Korean ? "대기" :
                           lang == Language.Japanese ? "待機" :
                           lang == Language.Russian ? "Ждать" : "Hold";
                default: return dir.ToString();
            }
        }

        private static void InitStyles(float scale)
        {
            if (_labelStyle != null && Mathf.Abs(_lastScale - scale) < 0.01f) return;
            _lastScale = scale;

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(14 * scale),
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1f, 0.85f, 0.4f) }, // 골드
                alignment = TextAnchor.MiddleLeft
            };

            _normalBtnStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = Mathf.RoundToInt(12 * scale),
                normal = { textColor = Color.white }
            };

            _selectedBtnStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = Mathf.RoundToInt(12 * scale),
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1f, 0.9f, 0.2f) }  // 밝은 노랑
            };

            // 선택된 버튼 배경 강조
            var selectedTex = new Texture2D(1, 1);
            selectedTex.SetPixel(0, 0, new Color(0.3f, 0.5f, 0.2f, 0.8f)); // 녹색 계열
            selectedTex.Apply();
            _selectedBtnStyle.normal.background = selectedTex;
        }
    }
}
```

---

## Task 6: 기존 Narrator/Pause 시스템 제거

**Files:**
- Modify: `Core/TurnOrchestrator.cs` — DecisionNarrator/pause 관련 코드 전체 제거
- Modify: `Core/TurnState.cs` — HasPausedThisTurn 제거
- Modify: `GameInterface/TurnEventHandler.cs` — DecisionNarrator.OnCombatEnd() 호출 제거
- Modify: `Main.cs` — DecisionOverlayUI.Initialize() → DirectiveOverlayUI.Initialize()
- Modify: `UI/MainUI.cs` — PauseOnAITurn UI 제거, 설명 텍스트 변경
- Modify: `Settings/ModSettings.cs` — PauseOnAITurn 속성 제거
- Delete (또는 비우기): `Diagnostics/NarrativeBuilder.cs`
- Modify: `Diagnostics/DecisionNarrator.cs` — IsPaused/Narrate/NarrativeBuilder 의존 제거 (또는 파일 삭제)
- Modify: `Diagnostics/DecisionHistory.cs` — narrator 전용이었으면 제거

**제거 항목 상세:**

6a. `TurnOrchestrator.cs`:
- 제거: `_lastProcessedUnitId` 필드 (line 76)
- 제거: line 112-113 (`DecisionNarrator.Instance.IsPaused` 체크)
- 제거: line 122-133 (턴 재개 감지 블록)
- 제거: line 283-285, 297-299 (Narrate + TryPauseForPlanReview 호출)
- 제거: line 306-308 (IsPaused 체크 후 Waiting 반환)
- 제거: line 333-340 (TryPauseForPlanReview 메서드)
- 제거: line 943 (`_lastProcessedUnitId = null`)

6b. `TurnState.cs`:
- 제거: `HasPausedThisTurn` 속성

6c. `TurnEventHandler.cs`:
- 제거: line 150 (`DecisionNarrator.Instance.OnCombatEnd()`)
- 추가: `DirectiveManager.OnCombatEnd()` (Hold/Retreat 리셋)

6d. `Main.cs`:
- 변경: `DecisionOverlayUI.Initialize()` → `DirectiveOverlayUI.Initialize()`
- 변경: `DecisionOverlayUI.Destroy()` → `DirectiveOverlayUI.Destroy()`

6e. `MainUI.cs`:
- 제거: PauseOnAITurn 체크박스 (line 213-214)
- 변경: EnableDecisionOverlay 설명을 전략 지시 UI로 변경

6f. `ModSettings.cs`:
- 제거: `PauseOnAITurn` 속성 (line 1884)
- EnableDecisionOverlay는 유지 (전략 지시 UI on/off 용도)

---

## Task 7: TurnEventHandler + TurnOrchestrator 통합

**Files:**
- Modify: `GameInterface/TurnEventHandler.cs`
- Modify: `Core/TurnOrchestrator.cs`

**구현:**

7a. `TurnEventHandler.HandleTurnBasedModeSwitched()` — 전투 종료 시:
```csharp
// 기존 DecisionNarrator.Instance.OnCombatEnd() 대체:
DirectiveManager.OnCombatEnd();  // Hold/Retreat → Auto 리셋
```

7b. `TurnOrchestrator.OnCombatEnd()` — 정리:
```csharp
// _lastProcessedUnitId = null; 제거 (필드 자체 제거됨)
```

---

## Task 8: Localization 업데이트

**Files:**
- Modify: `Settings/ModSettings.cs` (Localization 클래스)

**구현:**

기존 `narr_*` 로컬라이제이션 키들을 전략 지시 관련으로 교체:

```csharp
// 제거: narr_header, narr_prev_turn, narr_next_turn, narr_continue 등
// 변경: EnableDecisionOverlay 설명
{ "EnableDecisionOverlay", new Dictionary<Language, string> {
    { Language.English, "Enable Strategic Directive UI" },
    { Language.Korean, "전략 지시 UI 활성화" },
    { Language.Japanese, "戦略指示UI有効化" },
    { Language.Russian, "Включить UI стратегических директив" }
}},
{ "EnableDecisionOverlayDesc", new Dictionary<Language, string> {
    { Language.English, "Show directive buttons during combat to guide AI behavior per unit" },
    { Language.Korean, "전투 중 유닛별 AI 행동 방향을 지시하는 버튼을 표시합니다" },
    { Language.Japanese, "戦闘中にユニットごとのAI行動方向を指示するボタンを表示" },
    { Language.Russian, "Показывать кнопки для управления поведением ИИ каждого юнита в бою" }
}}
// PauseOnAITurn, PauseOnAITurnDesc 키 제거
```

---

## Task 9: Info.json 버전 + 빌드 검증

**Files:**
- Modify: `Info.json` — Version: "3.44.4" → "3.46.0"

**빌드:**
```powershell
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo
```

빌드 성공 확인 후 완료.

---

## 구현 순서 요약

1. **Task 1**: UserDirective enum + DirectiveManager (독립)
2. **Task 2**: TurnPlanner Hold/Retreat + TurnState.CurrentDirective (Task 1 의존)
3. **Task 3**: TurnStrategyPlanner 스코어 가중치 (Task 1 의존)
4. **Task 4**: BasePlan Phase 행동 변경 (Task 2, 3 의존)
5. **Task 5**: DirectiveOverlayUI 재작성 (Task 1 의존)
6. **Task 6**: 기존 Narrator/Pause 제거 (Task 5 의존)
7. **Task 7**: TurnEventHandler/Orchestrator 통합 (Task 6 의존)
8. **Task 8**: Localization 업데이트 (Task 6 의존)
9. **Task 9**: 버전 업 + 빌드 검증 (전체 의존)
