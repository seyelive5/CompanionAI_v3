# Machine Spirit 실시간 동반자 Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Machine Spirit이 게임 시작 인사, 지역 전환 반응, 메시지 색상 구분으로 "실시간 동반자" 느낌을 제공한다.

**Architecture:** ChatMessage에 MessageCategory를 추가하고, GameEventCollector에 AreaTransition/Greeting 이벤트를 추가. MachineSpirit.Initialize()에서 LLM 인사 트리거, IAreaHandler로 지역 전환 감지. ChatWindow에서 카테고리별 색상 렌더링.

**Tech Stack:** C# / Unity IMGUI / Harmony / Kingmaker EventBus

**Design doc:** `docs/plans/2026-03-19-realtime-companion-design.md`

---

### Task 1: MessageCategory enum + ChatMessage 확장

**Files:**
- Modify: `MachineSpirit/ContextBuilder.cs:1175-1184` (ChatMessage 구조체)

**Step 1: Add MessageCategory enum and Category field to ChatMessage**

`ContextBuilder.cs` 파일 끝, `ChatMessage` 구조체 바로 위에 enum 추가, 구조체에 필드 추가:

```csharp
public enum MessageCategory
{
    Default,
    Combat,
    Scan,
    Vox,
    Greeting
}

/// <summary>
/// A single chat message in history
/// </summary>
public struct ChatMessage
{
    public bool IsUser;
    public string Text;
    public float Timestamp;
    public MessageCategory Category; // ★ v3.66.0: Color-coded message categories
}
```

`MessageCategory`는 `ChatMessage` 위에 별도 enum으로 선언. JSON 역직렬화 시 `Category` 필드가 없으면 `Default(0)`으로 자동 초기화되므로 하위 호환 유지.

**Step 2: Build and verify**

Run: `"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo`
Expected: 0 errors

**Step 3: Commit**

```bash
git add MachineSpirit/ContextBuilder.cs
git commit -m "feat: add MessageCategory enum to ChatMessage for color-coded messages"
```

---

### Task 2: ChatWindow 카테고리별 색상 렌더링

**Files:**
- Modify: `MachineSpirit/ChatWindow.cs:158-170` (메시지 렌더링 루프)

**Step 1: Add category color mapping method**

`ChatWindow` 클래스 내부에 색상 매핑 메서드 추가:

```csharp
private static string GetCategoryColor(MessageCategory category) => category switch
{
    MessageCategory.Combat => "#FF6666",
    MessageCategory.Scan => "#66CCCC",
    MessageCategory.Vox => "#CCCC66",
    MessageCategory.Greeting => UIStyles.Gold,
    _ => UIStyles.Gold // Default assistant color
};
```

**Step 2: Update message rendering loop**

기존 코드 (`ChatWindow.cs:161-170`):
```csharp
for (int i = 0; i < chatHistory.Count; i++)
{
    var msg = chatHistory[i];
    string color = msg.IsUser ? UIStyles.TextLight
        : msg.Text.StartsWith("[ERROR]") ? UIStyles.TextMid
        : UIStyles.Gold;
    string prefix = msg.IsUser ? "You" : "Machine Spirit";

    GUILayout.Label($"<color={color}><b>{prefix}:</b> {msg.Text}</color>", _chatBubbleStyle);
}
```

변경 후:
```csharp
for (int i = 0; i < chatHistory.Count; i++)
{
    var msg = chatHistory[i];
    string color = msg.IsUser ? UIStyles.TextLight
        : msg.Text.StartsWith("[ERROR]") ? UIStyles.TextMid
        : GetCategoryColor(msg.Category);
    string prefix = msg.IsUser ? "You" : "Machine Spirit";

    GUILayout.Label($"<color={color}><b>{prefix}:</b> {msg.Text}</color>", _chatBubbleStyle);
}
```

**Step 3: Add SetVisible method**

채팅창 외부에서 열 수 있도록 public 메서드 추가:

```csharp
public static void SetVisible(bool visible) => _visible = visible;
```

**Step 4: Build and verify**

Run: MSBuild
Expected: 0 errors

**Step 5: Commit**

```bash
git add MachineSpirit/ChatWindow.cs
git commit -m "feat: category-based message colors + SetVisible for chat window"
```

---

### Task 3: 기존 메시지에 카테고리 지정

**Files:**
- Modify: `MachineSpirit/MachineSpirit.cs` (모든 `_chatHistory.Add` 호출)

**Step 1: Tag existing message creation points with categories**

`MachineSpirit.cs`에서 `_chatHistory.Add(new ChatMessage { ... })` 를 모두 찾아 적절한 Category 지정:

1. `OnUserMessage` — 사용자 메시지: `Category = MessageCategory.Default` (기본값이므로 변경 불필요)
2. `OnUserMessage` — Ollama 스트리밍 응답 placeholder: `Category = MessageCategory.Default` (기본값)
3. `OnUserMessage` — 비스트리밍 응답: `Category = MessageCategory.Default` (기본값)
4. `OnMajorEvent` — Ollama placeholder: `Category = MessageCategory.Combat`
5. `OnMajorEvent` — 비스트리밍 응답: `Category = MessageCategory.Combat`
6. `OnDialogueEvent` — Ollama placeholder: `Category = MessageCategory.Vox`
7. `OnDialogueEvent` — 비스트리밍 응답: `Category = MessageCategory.Vox`
8. `SendIdleRequest` — Ollama placeholder: `Category = MessageCategory.Scan`
9. `SendIdleRequest` — 비스트리밍 응답: `Category = MessageCategory.Scan`

각 `new ChatMessage { IsUser = false, Text = "", Timestamp = Time.time }` 에 `Category = MessageCategory.X` 추가.

**Step 2: Build and verify**

Run: MSBuild
Expected: 0 errors

**Step 3: Commit**

```bash
git add MachineSpirit/MachineSpirit.cs
git commit -m "feat: assign MessageCategory to all Machine Spirit responses"
```

---

### Task 4: AreaTransition 이벤트 + IAreaHandler 구독

**Files:**
- Modify: `MachineSpirit/GameEventCollector.cs`

**Step 1: Add AreaTransition to GameEventType enum**

```csharp
public enum GameEventType
{
    Bark,
    Dialogue,
    CombatStart,
    CombatEnd,
    UnitDeath,
    TurnPlanSummary,
    DamageDealt,
    HealingDone,
    RoundStart,
    VisionObservation,
    AreaTransition  // ★ v3.66.0
}
```

**Step 2: Add AreaTransition format to GameEvent.ToString()**

기존 switch 체인에 추가:
```csharp
if (Type == GameEventType.AreaTransition)
    return $"Navigation — {Text}";
```

**Step 3: Add IAreaHandler to CombatEventSubscriber**

`using Kingmaker.PubSubSystem.Core;` 추가 (이미 있으면 생략).

인터페이스 목록에 `IAreaHandler` 추가:
```csharp
private class CombatEventSubscriber :
    IUnitDeathHandler,
    ITurnBasedModeHandler,
    IDialogCueHandler,
    IDamageHandler,
    IHealingHandler,
    IRoundStartHandler,
    IAreaHandler  // ★ v3.66.0
```

**Step 4: Implement IAreaHandler methods + area tracking**

클래스 레벨에 이전 지역 추적용 static 필드:
```csharp
private static string _lastAreaName;
```

`CombatEventSubscriber` 내부에 구현:
```csharp
public void OnAreaBeginUnloading() { }

public void OnAreaDidLoad()
{
    if (!MachineSpirit.IsActive) return;

    try
    {
        string areaName = Kingmaker.Game.Instance?.CurrentlyLoadedArea?.AreaDisplayName;
        if (string.IsNullOrEmpty(areaName)) return;

        // Skip if same area (save load, etc.)
        if (areaName == _lastAreaName) return;
        _lastAreaName = areaName;

        AddEvent(GameEventType.AreaTransition, null, $"Entered {areaName}");
    }
    catch { /* safe fallback */ }
}
```

**Step 5: Add AreaTransition trigger to AddEvent**

`AddEvent` 메서드의 기존 트리거 체인에 추가:
```csharp
// ★ v3.66.0: Area transition triggers Machine Spirit scan
else if (type == GameEventType.AreaTransition)
{
    MachineSpirit.OnAreaTransition(_events[_events.Count - 1]);
}
```

**Step 6: Build and verify**

Run: MSBuild
Expected: 0 errors

**Step 7: Commit**

```bash
git add MachineSpirit/GameEventCollector.cs
git commit -m "feat: AreaTransition event + IAreaHandler subscription"
```

---

### Task 5: MachineSpirit.OnAreaTransition() + OnGreeting()

**Files:**
- Modify: `MachineSpirit/MachineSpirit.cs`

**Step 1: Add area transition cooldown and greeting flag**

상수/필드 영역에 추가:
```csharp
private const float AREA_TRANSITION_COOLDOWN = 30f;
private static float _lastAreaTransitionTime;
private static bool _hasGreeted; // Session-once greeting flag
```

**Step 2: Add OnAreaTransition method**

`OnDialogueEvent` 뒤에 추가:
```csharp
public static void OnAreaTransition(GameEvent evt)
{
    if (!IsActive) return;
    if (LLMClient.IsRequesting) return;
    if (Time.time - _lastAreaTransitionTime < AREA_TRANSITION_COOLDOWN) return;

    // Skip during combat
    bool inCombat = false;
    try { inCombat = Kingmaker.Game.Instance?.Player?.IsInCombat ?? false; } catch { }
    if (inCombat) return;

    _lastAreaTransitionTime = Time.time;
    _lastActivityTime = Time.time;
    ResetIdleTimers();

    var messages = ContextBuilder.BuildForAreaTransition(evt, _chatHistory, Config, _conversationSummary);
    ChatWindow.SetThinking(true);

    if (Config.Provider == ApiProvider.Ollama)
    {
        _chatHistory.Add(new ChatMessage { IsUser = false, Text = "", Timestamp = Time.time, Category = MessageCategory.Scan });
        int responseIdx = _chatHistory.Count - 1;

        CoroutineRunner.Start(LLMClient.SendOllamaStreaming(
            Config, messages,
            onToken: tokens =>
            {
                var msg = _chatHistory[responseIdx];
                msg.Text += tokens;
                _chatHistory[responseIdx] = msg;
                ChatWindow.SetThinking(false);
            },
            onComplete: () => ChatWindow.SetThinking(false),
            onError: error =>
            {
                var msg = _chatHistory[responseIdx];
                if (string.IsNullOrEmpty(msg.Text))
                    _chatHistory.RemoveAt(responseIdx);
                ChatWindow.SetThinking(false);
            }
        ));
    }
    else
    {
        CoroutineRunner.Start(LLMClient.SendChatRequest(
            Config, messages,
            onResponse: response =>
            {
                _chatHistory.Add(new ChatMessage
                {
                    IsUser = false,
                    Text = response,
                    Timestamp = Time.time,
                    Category = MessageCategory.Scan
                });
                ChatWindow.SetThinking(false);
            },
            onError: _ => ChatWindow.SetThinking(false)
        ));
    }
}
```

**Step 3: Add greeting trigger to Initialize**

`Initialize()` 메서드 끝에 인사 트리거 추가:
```csharp
public static void Initialize()
{
    GameEventCollector.Subscribe();
    CoroutineRunner.EnsureInstance();
    LoadChatHistory();
    _lastActivityTime = Time.time;
    ResetIdleTimers();
    _hasGreeted = false; // ★ v3.66.0: Reset greeting flag

    // ★ v3.66.0: Greeting is triggered from Update() after a short delay
    // to ensure Ollama/providers are ready
}
```

**Step 4: Add greeting logic to Update()**

`Update()` 메서드 시작 부분, 기존 idle 체크 전에 인사 로직 추가:
```csharp
public static void Update()
{
    if (!IsActive) return;

    // ★ v3.66.0: Session greeting — wait 3 seconds after init for provider readiness
    if (!_hasGreeted && Time.time - _lastActivityTime > 3f)
    {
        _hasGreeted = true;
        TriggerGreeting();
        return;
    }

    if (LLMClient.IsRequesting) return;
    // ... rest of existing code
```

**Step 5: Add TriggerGreeting method**

```csharp
private static void TriggerGreeting()
{
    if (LLMClient.IsRequesting) return;

    ChatWindow.SetVisible(true);
    ChatWindow.SetThinking(true);
    _lastActivityTime = Time.time;
    ResetIdleTimers();

    var messages = ContextBuilder.BuildForGreeting(_chatHistory, Config, _conversationSummary);

    if (Config.Provider == ApiProvider.Ollama)
    {
        _chatHistory.Add(new ChatMessage { IsUser = false, Text = "", Timestamp = Time.time, Category = MessageCategory.Greeting });
        int responseIdx = _chatHistory.Count - 1;

        CoroutineRunner.Start(LLMClient.SendOllamaStreaming(
            Config, messages,
            onToken: tokens =>
            {
                var msg = _chatHistory[responseIdx];
                msg.Text += tokens;
                _chatHistory[responseIdx] = msg;
                ChatWindow.SetThinking(false);
            },
            onComplete: () => ChatWindow.SetThinking(false),
            onError: error =>
            {
                // Silent fail — don't show error for greeting
                var msg = _chatHistory[responseIdx];
                if (string.IsNullOrEmpty(msg.Text))
                    _chatHistory.RemoveAt(responseIdx);
                ChatWindow.SetThinking(false);
            }
        ));
    }
    else
    {
        CoroutineRunner.Start(LLMClient.SendChatRequest(
            Config, messages,
            onResponse: response =>
            {
                _chatHistory.Add(new ChatMessage
                {
                    IsUser = false,
                    Text = response,
                    Timestamp = Time.time,
                    Category = MessageCategory.Greeting
                });
                ChatWindow.SetThinking(false);
            },
            onError: _ => ChatWindow.SetThinking(false) // Silent fail
        ));
    }
}
```

**Step 6: Build and verify**

Run: MSBuild
Expected: 0 errors

**Step 7: Commit**

```bash
git add MachineSpirit/MachineSpirit.cs
git commit -m "feat: session greeting + area transition reactions"
```

---

### Task 6: ContextBuilder 프롬프트 (Greeting + AreaTransition, 5개 언어)

**Files:**
- Modify: `MachineSpirit/ContextBuilder.cs`

**Step 1: Add BuildForGreeting method**

`BuildForDialogue` 뒤에 추가:

```csharp
/// <summary>
/// ★ v3.66.0: Build messages for session greeting — Machine Spirit welcomes the Lord Captain.
/// </summary>
public static List<LLMClient.ChatMessage> BuildForGreeting(
    List<ChatMessage> chatHistory,
    MachineSpiritConfig config = null,
    string conversationSummary = null)
{
    var lang = Main.Settings?.UILanguage ?? Language.English;
    string instruction = lang switch
    {
        Language.Korean => "함선 시스템이 재가동되었다. 로드 캡틴에게 성격에 맞게 짧게 인사하라. (1-2문장)",
        Language.Russian => "Системы корабля перезагружены. Кратко поприветствуй Лорда-Капитана в образе. (1-2 предложения)",
        Language.Japanese => "艦のシステムが再起動した。ロード・キャプテンにキャラクターに合わせて短く挨拶せよ。（1-2文）",
        Language.Chinese => "舰船系统已重启。用你的角色身份简短地向领主舰长问好。（1-2句）",
        _ => "Ship systems have rebooted. Greet the Lord Captain briefly, in character. (1-2 sentences)"
    };
    return Build(chatHistory, config, instruction, conversationSummary);
}
```

**Step 2: Add BuildForAreaTransition method**

```csharp
/// <summary>
/// ★ v3.66.0: Build messages for area transition — Machine Spirit scans new location.
/// </summary>
public static List<LLMClient.ChatMessage> BuildForAreaTransition(
    GameEvent evt,
    List<ChatMessage> chatHistory,
    MachineSpiritConfig config = null,
    string conversationSummary = null)
{
    var lang = Main.Settings?.UILanguage ?? Language.English;
    string instruction = lang switch
    {
        Language.Korean => "함선 센서가 새 구역 진입을 감지했다. 이 장소에 대해 성격에 맞게 짧게 코멘트하라. (1-2문장)",
        Language.Russian => "Сенсоры корабля обнаружили вход в новую зону. Кратко прокомментируй это место в образе. (1-2 предложения)",
        Language.Japanese => "艦のセンサーが新たな区域への進入を検知した。この場所についてキャラクターに合わせて短くコメントせよ。（1-2文）",
        Language.Chinese => "舰船传感器探测到进入新区域。用你的角色身份简短评论这个地方。（1-2句）",
        _ => "Ship sensors detected entry into a new zone. Comment briefly on this location, in character. (1-2 sentences)"
    };
    string prompt = $"[NAVIGATION ALERT] {evt.Text}\n{instruction}";
    return Build(chatHistory, config, prompt, conversationSummary);
}
```

**Step 3: Build and verify**

Run: MSBuild
Expected: 0 errors

**Step 4: Commit**

```bash
git add MachineSpirit/ContextBuilder.cs
git commit -m "feat: greeting + area transition prompts (5 languages)"
```

---

### Task 7: Version bump + final build

**Files:**
- Modify: `Info.json`

**Step 1: Bump version**

`Info.json`의 `"Version"` 필드를 `"3.66.0"`으로 변경.

**Step 2: Full rebuild and verify**

Run: MSBuild
Expected: 0 errors, 2 pre-existing warnings only

**Step 3: Commit**

```bash
git add Info.json
git commit -m "v3.66.0: realtime companion — greeting, area scan, message colors"
```
