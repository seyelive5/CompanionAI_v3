# UI Redesign — Imperial Dark Tab System Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** MainUI를 임페리얼 다크 톤의 탭 기반 레이아웃으로 전면 리디자인

**Architecture:** UIStyles 정적 클래스에서 Texture2D 기반 커스텀 GUIStyle을 일괄 관리. MainUI는 탭 enum으로 콘텐츠를 전환. 기존 접기/펴기 토글들은 탭으로 대체하여 제거.

**Tech Stack:** Unity IMGUI (GUILayout), Texture2D, GUIStyle, Rich Text

---

### Task 1: UIStyles.cs — 스타일 시스템 생성

**Files:**
- Create: `UI/UIStyles.cs`

**Step 1: UIStyles.cs 작성**

```csharp
using UnityEngine;

namespace CompanionAI_v3.UI
{
    /// <summary>
    /// Imperial Dark 테마 — Texture2D 기반 커스텀 스타일
    /// </summary>
    public static class UIStyles
    {
        // ── Color Palette (Imperial Dark) ──
        private static readonly Color BgColor         = new Color32(0x1A, 0x1A, 0x1E, 0xFF);
        private static readonly Color TabActiveColor   = new Color32(0x8B, 0x73, 0x32, 0xFF);
        private static readonly Color TabInactiveColor = new Color32(0x2D, 0x2D, 0x32, 0xFF);
        private static readonly Color TabHoverColor    = new Color32(0x3A, 0x3A, 0x42, 0xFF);
        private static readonly Color SectionBgColor   = new Color32(0x22, 0x22, 0x28, 0xFF);
        private static readonly Color DividerColor     = new Color32(0x3A, 0x3A, 0x42, 0xFF);
        private static readonly Color ButtonColor      = new Color32(0x33, 0x33, 0x3A, 0xFF);
        private static readonly Color ButtonHoverColor = new Color32(0x44, 0x44, 0x4E, 0xFF);
        private static readonly Color CheckOnColor     = new Color32(0xD4, 0xA9, 0x47, 0xFF);

        // ── Rich Text Color Strings ──
        public const string Gold      = "#D4A947";
        public const string TextLight = "#C8C8C8";
        public const string TextMid   = "#888888";
        public const string TextDim   = "#666666";
        public const string Danger    = "#FF6347";
        public const string Green     = "#98FB98";
        public const string RoleBlue  = "#4169E1";
        public const string RoleRed   = "#FF6347";
        public const string RoleGold  = "#FFD700";
        public const string RoleGreen = "#98FB98";

        // ── Textures ──
        private static Texture2D _bgTex;
        private static Texture2D _tabActiveTex;
        private static Texture2D _tabInactiveTex;
        private static Texture2D _tabHoverTex;
        private static Texture2D _sectionTex;
        private static Texture2D _dividerTex;
        private static Texture2D _buttonTex;
        private static Texture2D _buttonHoverTex;
        private static Texture2D _checkOnTex;

        // ── Styles ──
        public static GUIStyle Background   { get; private set; }
        public static GUIStyle TabActive     { get; private set; }
        public static GUIStyle TabInactive   { get; private set; }
        public static GUIStyle SectionBox    { get; private set; }
        public static GUIStyle Header        { get; private set; }
        public static GUIStyle SubHeader     { get; private set; }
        public static GUIStyle Description   { get; private set; }
        public static GUIStyle Label         { get; private set; }
        public static GUIStyle BoldLabel     { get; private set; }
        public static GUIStyle Button        { get; private set; }
        public static GUIStyle Checkbox      { get; private set; }
        public static GUIStyle CharRow       { get; private set; }
        public static GUIStyle Divider       { get; private set; }
        public static GUIStyle SliderLabel   { get; private set; }

        private static bool _initialized;

        public static void InitOnce()
        {
            if (_initialized) return;
            _initialized = true;

            // Create textures
            _bgTex          = MakeTex(BgColor);
            _tabActiveTex   = MakeTex(TabActiveColor);
            _tabInactiveTex = MakeTex(TabInactiveColor);
            _tabHoverTex    = MakeTex(TabHoverColor);
            _sectionTex     = MakeTex(SectionBgColor);
            _dividerTex     = MakeTex(DividerColor);
            _buttonTex      = MakeTex(ButtonColor);
            _buttonHoverTex = MakeTex(ButtonHoverColor);
            _checkOnTex     = MakeTex(CheckOnColor);

            // Background
            Background = new GUIStyle(GUI.skin.box)
            {
                normal = { background = _bgTex },
                padding = new RectOffset(10, 10, 10, 10)
            };

            // Tabs
            TabActive = new GUIStyle(GUI.skin.button)
            {
                normal    = { background = _tabActiveTex, textColor = Color.white },
                hover     = { background = _tabActiveTex, textColor = Color.white },
                active    = { background = _tabActiveTex, textColor = Color.white },
                focused   = { background = _tabActiveTex, textColor = Color.white },
                fontSize  = 15,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                richText  = true,
                padding   = new RectOffset(12, 12, 8, 8),
                margin    = new RectOffset(1, 1, 0, 0)
            };

            TabInactive = new GUIStyle(GUI.skin.button)
            {
                normal    = { background = _tabInactiveTex, textColor = new Color32(0x99, 0x99, 0x99, 0xFF) },
                hover     = { background = _tabHoverTex, textColor = new Color32(0xCC, 0xCC, 0xCC, 0xFF) },
                active    = { background = _tabHoverTex, textColor = Color.white },
                focused   = { background = _tabInactiveTex, textColor = new Color32(0x99, 0x99, 0x99, 0xFF) },
                fontSize  = 15,
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleCenter,
                richText  = true,
                padding   = new RectOffset(12, 12, 8, 8),
                margin    = new RectOffset(1, 1, 0, 0)
            };

            // Section box
            SectionBox = new GUIStyle(GUI.skin.box)
            {
                normal  = { background = _sectionTex },
                padding = new RectOffset(15, 15, 12, 12),
                margin  = new RectOffset(0, 0, 5, 5)
            };

            // Header (title)
            Header = new GUIStyle(GUI.skin.label)
            {
                fontSize  = 20,
                fontStyle = FontStyle.Bold,
                richText  = true,
                alignment = TextAnchor.MiddleLeft,
                padding   = new RectOffset(5, 5, 5, 2)
            };

            // SubHeader (section titles)
            SubHeader = new GUIStyle(GUI.skin.label)
            {
                fontSize  = 17,
                fontStyle = FontStyle.Bold,
                richText  = true,
                padding   = new RectOffset(2, 2, 4, 4)
            };

            // Description
            Description = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                richText = true,
                wordWrap = true,
                normal   = { textColor = new Color32(0x88, 0x88, 0x88, 0xFF) },
                padding  = new RectOffset(2, 2, 2, 2)
            };

            // Normal label
            Label = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                richText = true,
                normal   = { textColor = new Color32(0xC8, 0xC8, 0xC8, 0xFF) }
            };

            // Bold label
            BoldLabel = new GUIStyle(GUI.skin.label)
            {
                fontSize  = 16,
                fontStyle = FontStyle.Bold,
                richText  = true,
                normal    = { textColor = new Color32(0xC8, 0xC8, 0xC8, 0xFF) }
            };

            // Button
            Button = new GUIStyle(GUI.skin.button)
            {
                normal   = { background = _buttonTex, textColor = new Color32(0xC8, 0xC8, 0xC8, 0xFF) },
                hover    = { background = _buttonHoverTex, textColor = Color.white },
                active   = { background = _tabActiveTex, textColor = Color.white },
                fontSize = 15,
                richText = true,
                alignment = TextAnchor.MiddleCenter,
                padding  = new RectOffset(8, 8, 6, 6)
            };

            // Checkbox
            Checkbox = new GUIStyle(GUI.skin.box)
            {
                normal    = { background = _buttonTex },
                hover     = { background = _buttonHoverTex },
                fontSize  = 20,
                fontStyle = FontStyle.Bold,
                richText  = true,
                alignment = TextAnchor.MiddleCenter,
                padding   = new RectOffset(4, 4, 4, 4)
            };

            // Character row
            CharRow = new GUIStyle(GUI.skin.box)
            {
                normal  = { background = _sectionTex },
                padding = new RectOffset(8, 8, 6, 6),
                margin  = new RectOffset(0, 0, 2, 2)
            };

            // Divider
            Divider = new GUIStyle()
            {
                normal     = { background = _dividerTex },
                fixedHeight = 1,
                margin     = new RectOffset(0, 0, 8, 8)
            };

            // Slider label
            SliderLabel = new GUIStyle(GUI.skin.label)
            {
                fontSize  = 16,
                richText  = true,
                alignment = TextAnchor.MiddleLeft
            };
        }

        private static Texture2D MakeTex(Color color)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }

        /// <summary>
        /// 가로 구분선
        /// </summary>
        public static void DrawDivider()
        {
            GUILayout.Box(GUIContent.none, Divider, GUILayout.ExpandWidth(true), GUILayout.Height(1));
        }

        /// <summary>
        /// 섹션 제목 (골드)
        /// </summary>
        public static void SectionTitle(string text)
        {
            GUILayout.Label($"<color={Gold}>{text}</color>", SubHeader);
        }
    }
}
```

**Step 2: 빌드 확인**

Run: `MSBuild CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo`
Expected: 성공 (UIStyles는 아직 호출되지 않으므로 컴파일만 확인)

**Step 3: Commit**

```bash
git add UI/UIStyles.cs
git commit -m "feat: add UIStyles — Imperial Dark theme system"
```

---

### Task 2: MainUI.cs — 탭 시스템 + 헤더 리팩토링

**Files:**
- Modify: `UI/MainUI.cs`

**Step 1: 탭 enum 추가 + 필드 정리**

기존 접기/펴기 bool 5개 제거, 탭 enum + 활성 탭 필드 추가:

```csharp
// 제거:
// _showAdvancedSettings, _showPerformanceSettings, _showAoESettings,
// _showWeaponRotationSettings, _showDebugSettings

// 추가:
private enum UITab { Party, Gameplay, Combat, Performance, Language, Debug }
private static UITab _activeTab = UITab.Party;

// 탭 표시명 (Localization key)
private static readonly (UITab tab, string locKey)[] TabDefs = new[]
{
    (UITab.Party,       "TabParty"),
    (UITab.Gameplay,    "TabGameplay"),
    (UITab.Combat,      "TabCombat"),
    (UITab.Performance, "TabPerformance"),
    (UITab.Language,    "TabLanguage"),
    (UITab.Debug,       "TabDebug"),
};
```

**Step 2: OnGUI 리팩토링**

```csharp
public static void OnGUI()
{
    Localization.CurrentLanguage = Main.Settings.UILanguage;
    UIStyles.InitOnce();

    try
    {
        _scrollPosition = GUILayout.BeginScrollView(_scrollPosition, GUILayout.Height(700));
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
```

**Step 3: DrawHeader 수정**

```csharp
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
```

**Step 4: DrawTabBar 작성**

```csharp
private static void DrawTabBar()
{
    GUILayout.BeginHorizontal();
    foreach (var (tab, locKey) in TabDefs)
    {
        var style = (_activeTab == tab) ? UIStyles.TabActive : UIStyles.TabInactive;
        if (GUILayout.Button(L(locKey), style, GUILayout.Height(36)))
            _activeTab = tab;
    }
    GUILayout.FlexibleSpace();
    GUILayout.EndHorizontal();
}
```

**Step 5: DrawTabContent 작성**

```csharp
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
    }
    GUILayout.EndVertical();
}
```

**Step 6: Commit**

```bash
git add UI/MainUI.cs
git commit -m "feat: MainUI tab system + header redesign"
```

---

### Task 3: 파티 탭 구현

**Files:**
- Modify: `UI/MainUI.cs`

**Step 1: DrawPartyTab 작성**

기존 `DrawCharacterSelection()` + `DrawCharacterRow()` + `DrawCharacterAISettings()` + `DrawAdvancedSettings()` 내용을 통합. 스타일만 UIStyles로 교체.

```csharp
private static void DrawPartyTab()
{
    UIStyles.SectionTitle(L("PartyMembers"));
    GUILayout.Space(8);

    var characters = GetPartyMembers();
    if (characters.Count == 0)
    {
        GUILayout.Label($"<color={UIStyles.TextMid}><i>{L("NoCharacters")}</i></color>", UIStyles.Description);
        return;
    }

    // Header row
    GUILayout.BeginHorizontal();
    GUILayout.Label($"<color={UIStyles.Gold}><b>{L("AI")}</b></color>", UIStyles.Label, GUILayout.Width(55));
    GUILayout.Label($"<color={UIStyles.Gold}><b>{L("Character")}</b></color>", UIStyles.Label, GUILayout.Width(CHAR_NAME_WIDTH));
    GUILayout.Label($"<color={UIStyles.Gold}><b>{L("Role")}</b></color>", UIStyles.Label, GUILayout.Width(ROLE_LABEL_WIDTH));
    GUILayout.Label($"<color={UIStyles.Gold}><b>{L("Range")}</b></color>", UIStyles.Label, GUILayout.Width(RANGE_LABEL_WIDTH));
    GUILayout.FlexibleSpace();
    GUILayout.EndHorizontal();
    UIStyles.DrawDivider();

    foreach (var character in characters)
        DrawCharacterRow(character);
}
```

**Step 2: DrawCharacterRow UIStyles 적용**

기존 DrawCharacterRow에서 `GUI.skin.box` → `UIStyles.CharRow`, `GUI.skin.button` → `UIStyles.Button`, 체크박스 → `UIStyles.Checkbox` 적용. 색상 하드코딩 → `UIStyles.Gold` 등으로 교체.

**Step 3: DrawCharacterAISettings + DrawRoleSelection + DrawRangePreferenceSelection UIStyles 적용**

역할/거리 선택 버튼, 고급 설정 내 체크박스/슬라이더에 UIStyles 적용.

**Step 4: DrawAdvancedSettings 유지**

캐릭터별 고급 설정은 접기/펴기 유지 (`_showAdvancedSettings`). 이것만 탭이 아닌 인라인 접기.

**Step 5: 빌드 확인**

Run: `MSBuild ...`

**Step 6: Commit**

```bash
git add UI/MainUI.cs
git commit -m "feat: Party tab with Imperial Dark styling"
```

---

### Task 4: 게임플레이 탭 구현

**Files:**
- Modify: `UI/MainUI.cs`

**Step 1: DrawGameplayTab 작성**

기존 `DrawGlobalSettings()`에서 게임플레이 관련 항목만 추출:

```csharp
private static void DrawGameplayTab()
{
    UIStyles.SectionTitle(L("GameplaySettings"));
    GUILayout.Space(10);

    // AI Speech
    Main.Settings.EnableAISpeech = DrawCheckbox(Main.Settings.EnableAISpeech, L("EnableAISpeech"));
    if (Main.Settings.EnableAISpeech)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Space(55);
        if (GUILayout.Button(L("ReloadDialogue"), UIStyles.Button, GUILayout.Width(250), GUILayout.Height(30)))
        {
            DialogueLocalization.ReloadFromJson();
            Diagnostics.TacticalDialogueDB.ReloadFromJson();
        }
        GUILayout.EndHorizontal();
    }
    GUILayout.Space(5);

    // Victory Bark
    Main.Settings.EnableVictoryBark = DrawCheckbox(Main.Settings.EnableVictoryBark, L("EnableVictoryBark"));
    GUILayout.Space(10);
    UIStyles.DrawDivider();
    GUILayout.Space(10);

    // Allied NPC AI
    Main.Settings.EnableAlliedNPCAI = DrawCheckbox(Main.Settings.EnableAlliedNPCAI, L("EnableAlliedNPCAI"));
    GUILayout.Label($"<color={UIStyles.Danger}><size=14>{L("EnableAlliedNPCAIDesc")}</size></color>", UIStyles.Description);
    GUILayout.Space(5);

    // Ship Combat AI
    Main.Settings.EnableShipCombatAI = DrawCheckbox(Main.Settings.EnableShipCombatAI, L("EnableShipCombatAI"));
    GUILayout.Label($"<color={UIStyles.Danger}><size=14>{L("EnableShipCombatAIDesc")}</size></color>", UIStyles.Description);
}
```

**Step 2: Commit**

```bash
git add UI/MainUI.cs
git commit -m "feat: Gameplay tab"
```

---

### Task 5: 전투 + 성능 탭 구현

**Files:**
- Modify: `UI/MainUI.cs`

**Step 1: DrawCombatTab 작성**

기존 `DrawAoESettings()` + `DrawWeaponRotationSettings()` 내용을 접기/펴기 없이 바로 표시. 리셋 버튼 + 슬라이더 포함.

**Step 2: DrawPerformanceTab 작성**

기존 `DrawPerformanceSettings()` 내용을 접기/펴기 없이 바로 표시. 경고 + 리셋 + 4개 슬라이더.

**Step 3: 슬라이더 헬퍼 UIStyles 적용**

`DrawSliderSettingIntLarge`, `DrawSliderSettingFloatLarge` → UIStyles 색상/스타일 적용.

**Step 4: 빌드 확인**

**Step 5: Commit**

```bash
git add UI/MainUI.cs
git commit -m "feat: Combat + Performance tabs"
```

---

### Task 6: 언어 + 디버그 탭 구현

**Files:**
- Modify: `UI/MainUI.cs`

**Step 1: DrawLanguageTab 작성**

기존 언어 선택 버튼 4개. 간결하게 큰 버튼으로 표시.

```csharp
private static void DrawLanguageTab()
{
    UIStyles.SectionTitle(L("Language"));
    GUILayout.Space(15);

    foreach (Language lang in Enum.GetValues(typeof(Language)))
    {
        bool isSelected = Main.Settings.UILanguage == lang;
        string langName = lang switch
        {
            Language.English  => "English",
            Language.Korean   => "한국어",
            Language.Russian  => "Русский",
            Language.Japanese => "日本語",
            _                 => lang.ToString()
        };

        var style = isSelected ? UIStyles.TabActive : UIStyles.Button;
        if (GUILayout.Button(langName, style, GUILayout.Width(200), GUILayout.Height(45)))
        {
            Main.Settings.UILanguage = lang;
            Localization.CurrentLanguage = lang;
            Diagnostics.TacticalDialogueDB.ReloadFromJson();
        }
        GUILayout.Space(5);
    }
}
```

**Step 2: DrawDebugTab 작성**

기존 `DrawDebugSettings()` 내용. 접기/펴기 제거, 바로 표시.

**Step 3: 빌드 확인**

**Step 4: Commit**

```bash
git add UI/MainUI.cs
git commit -m "feat: Language + Debug tabs"
```

---

### Task 7: Localization 키 추가 + 레거시 제거 + 최종 정리

**Files:**
- Modify: `Settings/ModSettings.cs` (Localization 딕셔너리에 탭 이름 키 추가)
- Modify: `UI/MainUI.cs` (레거시 메서드 제거)

**Step 1: Localization에 탭 키 추가**

```csharp
["TabParty"] = new() {
    { Language.English, "Party" }, { Language.Korean, "파티" },
    { Language.Russian, "Отряд" }, { Language.Japanese, "パーティ" }
},
["TabGameplay"] = new() {
    { Language.English, "Gameplay" }, { Language.Korean, "게임플레이" },
    { Language.Russian, "Геймплей" }, { Language.Japanese, "ゲームプレイ" }
},
["TabCombat"] = new() {
    { Language.English, "Combat" }, { Language.Korean, "전투" },
    { Language.Russian, "Бой" }, { Language.Japanese, "戦闘" }
},
["TabPerformance"] = new() {
    { Language.English, "Performance" }, { Language.Korean, "성능" },
    { Language.Russian, "Производительность" }, { Language.Japanese, "パフォーマンス" }
},
["TabLanguage"] = new() {
    { Language.English, "Language" }, { Language.Korean, "언어" },
    { Language.Russian, "Язык" }, { Language.Japanese, "言語" }
},
["TabDebug"] = new() {
    { Language.English, "Debug" }, { Language.Korean, "디버그" },
    { Language.Russian, "Отладка" }, { Language.Japanese, "デバッグ" }
},
["GameplaySettings"] = new() {
    { Language.English, "Gameplay Settings" }, { Language.Korean, "게임플레이 설정" },
    { Language.Russian, "Настройки геймплея" }, { Language.Japanese, "ゲームプレイ設定" }
},
```

**Step 2: 레거시 메서드/필드 제거**

제거 대상:
- `_showPerformanceSettings`, `_showAoESettings`, `_showWeaponRotationSettings`, `_showDebugSettings` 필드
- `_headerStyle`, `_boldLabelStyle`, `_boxStyle`, `_descriptionStyle` 필드
- `InitStyles()` 메서드 전체
- `DrawGlobalSettings()` 메서드 전체
- `DrawPerformanceSettings()` 메서드 → DrawPerformanceTab으로 대체
- `DrawAoESettings()` 메서드 → DrawCombatTab에 통합
- `DrawWeaponRotationSettings()` 메서드 → DrawCombatTab에 통합
- `DrawDebugSettings()` 메서드 → DrawDebugTab으로 대체
- `DrawCharacterSelection()` 메서드 → DrawPartyTab으로 대체

유지:
- `_showAdvancedSettings` (캐릭터별 고급 설정 접기 — 탭 내 인라인)
- `DrawCheckbox()`, `DrawSliderSettingIntLarge()`, `DrawSliderSettingFloatLarge()`, `DrawSliderSetting()`, `DrawSliderSettingInt()`
- `DrawCharacterRow()`, `DrawCharacterAISettings()`, `DrawRoleSelection()`, `DrawRangePreferenceSelection()`, `DrawAdvancedSettings()`
- `GetPartyMembers()`, `CharacterInfo`

**Step 3: DrawCheckbox UIStyles 적용**

```csharp
private static bool DrawCheckbox(bool value, string label)
{
    GUILayout.BeginHorizontal();
    string checkIcon = value
        ? $"<color={UIStyles.Gold}>☑</color>"
        : $"<color={UIStyles.TextDim}>☐</color>";

    if (GUILayout.Button(checkIcon, UIStyles.Checkbox, GUILayout.Width(40), GUILayout.Height(40)))
        value = !value;

    GUILayout.Space(8);
    if (GUILayout.Button($"<color={UIStyles.TextLight}>{label}</color>", UIStyles.Label, GUILayout.Height(40)))
        value = !value;

    GUILayout.EndHorizontal();
    return value;
}
```

**Step 4: 빌드 확인**

Run: `MSBuild ...`

**Step 5: Commit**

```bash
git add UI/MainUI.cs Settings/ModSettings.cs
git commit -m "feat: add tab localization keys + remove legacy UI code"
```

---

### Task 8: 버전 업데이트 + 최종 빌드

**Files:**
- Modify: `Info.json`

**Step 1: 버전 업데이트**

`3.48.0` → `3.50.0`

**Step 2: 최종 빌드**

Run: `MSBuild CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo`
Expected: 0 errors, 0 warnings (또는 기존 warnings만)

**Step 3: Commit**

```bash
git add Info.json
git commit -m "v3.50.0: UI Redesign — Imperial Dark tab system"
```
