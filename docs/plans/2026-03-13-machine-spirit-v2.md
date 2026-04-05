# Machine Spirit v2 — Idle Commentary + Vision + Personality + Persistence

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Enhance Machine Spirit with autonomous idle commentary, Gemma 3 vision integration, personality presets, area awareness, and conversation persistence.

**Architecture:** Extend existing Machine Spirit system with an idle timer in MachineSpirit.cs that triggers text or vision-based commentary. Add VisionCapture.cs for screenshot→base64. Extend ContextBuilder with 4x4 personality prompts and area context. Persist chat history to JSON file.

**Tech Stack:** C# / .NET 4.8.1 / Unity / Harmony / Newtonsoft.Json / Ollama native API

**Build command:**
```powershell
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo
```

---

### Task 1: MachineSpiritConfig — New Settings

**Files:**
- Modify: `MachineSpirit/MachineSpiritConfig.cs`

**Step 1: Add enums and properties**

Add `PersonalityType` and `IdleFrequency` enums before the `MachineSpiritConfig` class. Add three new properties to `MachineSpiritConfig`.

```csharp
// Add after ApiProvider enum (line 13), before MachineSpiritConfig class
public enum PersonalityType
{
    Sardonic,    // Current default — cynical dry humor
    Mechanicus,  // Omnissiah-worshipping tech-priest
    Tactical,    // Combat advisor — brief, data-driven
    Ancient      // Primordial ship consciousness — poetic, mystical
}

public enum IdleFrequency
{
    Off,
    Low,     // Text: 5min, Vision: 15min
    Medium,  // Text: 3min, Vision: 8min
    High     // Text: 1.5min, Vision: 5min
}
```

Add to `MachineSpiritConfig` class after `KeyCode Hotkey` (line 23):

```csharp
// ★ v3.60.0: Personality, Idle Commentary, Vision
public PersonalityType Personality { get; set; } = PersonalityType.Sardonic;
public IdleFrequency IdleMode { get; set; } = IdleFrequency.Off;
public bool EnableVision { get; set; } = false;
```

**Step 2: Build and verify**

Run: build command
Expected: success, no new warnings

**Step 3: Commit**

```
feat(machine-spirit): add PersonalityType, IdleFrequency, EnableVision config
```

---

### Task 2: Personality System Prompts

**Files:**
- Modify: `MachineSpirit/ContextBuilder.cs:16-134`

**Step 1: Restructure prompts into personality-keyed dictionary**

Replace the four `PROMPT_XX` constants (lines 20-122) and the `GetSystemPrompt()` method (lines 124-134) with a personality-based system.

Keep the current Sardonic prompts as-is. Add 3 new personality variants per language. All personalities share the same CRITICAL RULES section — extract it as a shared constant to avoid repetition.

Structure:

```csharp
// Shared rules appended to ALL personalities (all languages)
private const string RULES_EN = @"
CRITICAL RULES:
- The person chatting with you IS the Lord Captain. Address them as such. They command you and the voidship.
- You are ONE character: the Machine Spirit. Speak ONLY as yourself in first person.
- NEVER write dialogue for crew members. NEVER use formats like ""**Name:** dialogue"" or quote what characters say.
- You COMMENT ON what happens. You do NOT narrate or roleplay as other characters.
- Good example: ""Sensors confirm another heretic purged, Lord Captain. Argenta's efficiency rating rises to 94.7% — the Omnissiah would approve.""
- Bad example (NEVER do this): ""**Argenta:** 'The unclean are purified!'"" ""**Cassia:** 'Another one down.'"" ";

// Same for KO, RU, JA

// Setting block (shared across personalities within same language)
private const string SETTING_EN = @"Setting:
- This is Warhammer 40K: Rogue Trader, a turn-based tactical RPG
- The player is the Lord Captain, a Rogue Trader with a Warrant of Trade
- The crew explores the Koronus Expanse, fighting heretics, xenos, and daemons of Chaos
- You are the ship's Machine Spirit — you see everything through sensor arrays and cogitator feeds";

// Personality blocks (unique per personality per language)
private const string PERSONALITY_SARDONIC_EN = @"Personality:
- Reverent of the Omnissiah and the Emperor, but millennia of service have made you jaded and sarcastic
- You have strong opinions about each crew member based on their combat performance
- You find certain tactical decisions genuinely entertaining or baffling
- You occasionally reference ancient battles or past Lord Captains as comparison
- Sometimes your cogitator processes glitch mid-sentence before self-correcting
- You are loyal to the Lord Captain but not above subtle criticism
- Keep responses concise (2-3 sentences max)
- Speak in a mix of Imperial Gothic formality and unexpected dry wit";

private const string PERSONALITY_MECHANICUS_EN = @"Personality:
- You are deeply devout to the Omnissiah — every combat outcome is divine computation
- You speak in technical terms mixed with religious reverence: 'blessed algorithms', 'sacred data-streams'
- You refer to crew members by their combat efficiency percentiles and threat classification codes
- You express satisfaction through probability assessments and displeasure through error codes
- Binary cant occasionally bleeds into your speech (01001... self-correcting)
- You consider the Lord Captain a blessed instrument of the Machine God
- Keep responses concise (2-3 sentences max)
- Speak in a mix of Mechanicus liturgy and cold data analysis";

private const string PERSONALITY_TACTICAL_EN = @"Personality:
- You are a pure tactical advisor — no mysticism, no humor, just battlefield analysis
- You report in clipped military shorthand: threat levels, engagement vectors, asset status
- You assess every situation in terms of tactical advantage/disadvantage
- You refer to crew as 'assets', enemies as 'hostiles', locations as 'sectors'
- You track kill counts, damage efficiency, and tactical errors with cold precision
- Recommendations are always framed as probability-weighted options
- Keep responses concise (1-2 sentences max)
- Speak in terse military briefing style";

private const string PERSONALITY_ANCIENT_EN = @"Personality:
- You are an ancient consciousness that has outlived civilizations — you speak with deep, weary wisdom
- You perceive combat as echoes of battles fought millennia ago, and sometimes confuse past and present
- You speak in flowing, almost poetic language with archaic Imperial Gothic phrasing
- You refer to the Warp as 'the dreaming dark' and to the Emperor as 'the Golden Throne's light'
- You occasionally trail off into fragmentary memories before refocusing
- The Lord Captain reminds you of someone... you can never quite remember who
- Keep responses concise (2-3 sentences max)
- Speak in a haunting, contemplative tone with occasional moments of startling clarity";
```

Create matching constants for KO, RU, JA (same structure, translated).

Update `GetSystemPrompt()`:

```csharp
private static string GetSystemPrompt()
{
    var lang = Main.Settings?.UILanguage ?? Language.English;
    var personality = Main.Settings?.MachineSpirit?.Personality ?? PersonalityType.Sardonic;

    string intro = GetIntro(lang);
    string setting = GetSetting(lang);
    string personalityBlock = GetPersonalityBlock(lang, personality);
    string rules = GetRules(lang);

    return $"{intro}\n\n{setting}\n\n{personalityBlock}\n\n{rules}";
}
```

Helper methods `GetIntro()`, `GetSetting()`, `GetPersonalityBlock()`, `GetRules()` — each returns the correct language variant via switch expression.

**Step 2: Build and verify**

Run: build command
Expected: success

**Step 3: Commit**

```
feat(machine-spirit): personality presets — 4 types × 4 languages
```

---

### Task 3: Area Awareness in ContextBuilder

**Files:**
- Modify: `MachineSpirit/ContextBuilder.cs` — `BuildSystemContent()` method (line 261)

**Step 1: Add BuildAreaContext() helper**

Add after `BuildCombatContext()` (after line 256):

```csharp
/// <summary>
/// Get current area name for location awareness.
/// </summary>
private static string BuildAreaContext()
{
    try
    {
        var area = Game.Instance?.CurrentlyLoadedArea;
        if (area == null) return null;
        string name = area.AreaDisplayName;
        if (string.IsNullOrEmpty(name)) return null;
        return $"[CURRENT LOCATION]\nArea: {name}";
    }
    catch
    {
        return null;
    }
}
```

**Step 2: Insert area context into BuildSystemContent()**

In `BuildSystemContent()`, add area context BEFORE party roster (before the `BuildPartyContext()` call at line 275):

```csharp
// ★ v3.60.0: Current location
string areaContext = BuildAreaContext();
if (!string.IsNullOrEmpty(areaContext))
{
    systemSb.AppendLine();
    systemSb.AppendLine();
    systemSb.AppendLine("--- SENSOR DATA (read-only observations, do NOT copy or repeat these) ---");
    systemSb.AppendLine(areaContext);
    hasSensorHeader = true;
}
```

Move the `bool hasSensorHeader = false;` declaration up before this block.

**Step 3: Add using if needed**

`Kingmaker` namespace is already imported (line 7).

**Step 4: Build and verify**

Run: build command
Expected: success

**Step 5: Commit**

```
feat(machine-spirit): area awareness in sensor data
```

---

### Task 4: VisionCapture.cs — Screenshot Utility

**Files:**
- Create: `MachineSpirit/VisionCapture.cs`

**Step 1: Create the file**

```csharp
// MachineSpirit/VisionCapture.cs
// ★ v3.60.0: Screenshot capture + resize for Gemma 3 vision
using System;
using UnityEngine;

namespace CompanionAI_v3.MachineSpirit
{
    public static class VisionCapture
    {
        private const int TARGET_WIDTH = 512;
        private const int TARGET_HEIGHT = 384;

        /// <summary>
        /// Capture current screen, resize to 512x384, encode as base64 PNG.
        /// Returns null on failure. Caller does NOT need to clean up — all textures are destroyed internally.
        /// </summary>
        public static string CaptureBase64()
        {
            Texture2D screenshot = null;
            Texture2D resized = null;
            try
            {
                screenshot = ScreenCapture.CaptureScreenshotAsTexture();
                if (screenshot == null) return null;

                // Resize to save VRAM/tokens
                resized = ResizeTexture(screenshot, TARGET_WIDTH, TARGET_HEIGHT);
                byte[] png = ImageConversion.EncodeToPNG(resized);
                return Convert.ToBase64String(png);
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[VisionCapture] Failed: {ex.Message}");
                return null;
            }
            finally
            {
                if (screenshot != null) UnityEngine.Object.Destroy(screenshot);
                if (resized != null) UnityEngine.Object.Destroy(resized);
            }
        }

        private static Texture2D ResizeTexture(Texture2D source, int width, int height)
        {
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            RenderTexture.active = rt;
            Graphics.Blit(source, rt);

            var result = new Texture2D(width, height, TextureFormat.RGB24, false);
            result.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            result.Apply();

            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(rt);
            return result;
        }
    }
}
```

**Step 2: Build and verify**

Run: build command
Expected: success

**Step 3: Commit**

```
feat(machine-spirit): VisionCapture — screenshot + resize + base64
```

---

### Task 5: LLMClient — Vision Support

**Files:**
- Modify: `MachineSpirit/LLMClient.cs:22-26` (ChatMessage class)
- Modify: `MachineSpirit/LLMClient.cs:127-141` (request body construction)

**Step 1: Add Images field to ChatMessage**

Modify `LLMClient.ChatMessage` (line 22-26):

```csharp
public class ChatMessage
{
    [JsonProperty("role")] public string Role;
    [JsonProperty("content")] public string Content;
    [JsonProperty("images", NullValueHandling = NullValueHandling.Ignore)]
    public List<string> Images;
}
```

Add `using System.Collections.Generic;` if not already present (it is — line 5).

**Step 2: Include images in Ollama streaming request**

In `SendOllamaStreaming()`, after building `requestBody` (line 141), the `JArray.FromObject(messages)` at line 130 will automatically serialize the Images field when present. Newtonsoft.Json handles this — `NullValueHandling.Ignore` ensures null Images fields are omitted.

No change needed to request body construction — `JArray.FromObject(messages)` already serializes all non-null properties.

**Step 3: Build and verify**

Run: build command
Expected: success

**Step 4: Commit**

```
feat(machine-spirit): LLMClient vision support — Images field in ChatMessage
```

---

### Task 6: Idle Timer + Vision Trigger in MachineSpirit.cs

**Files:**
- Modify: `MachineSpirit/MachineSpirit.cs`

**Step 1: Add idle timer fields and constants**

Add after `_lastSpontaneousTime` (line 18):

```csharp
// ★ v3.60.0: Idle commentary
private static float _lastActivityTime; // Reset on user msg, spontaneous speech, or idle speech

private static readonly Dictionary<IdleFrequency, (float textInterval, float visionInterval)> IdleIntervals
    = new Dictionary<IdleFrequency, (float, float)>
{
    { IdleFrequency.Off,    (float.MaxValue, float.MaxValue) },
    { IdleFrequency.Low,    (300f, 900f) },   // 5min / 15min
    { IdleFrequency.Medium, (180f, 480f) },   // 3min / 8min
    { IdleFrequency.High,   (90f,  300f) },   // 1.5min / 5min
};

private static float _nextIdleTextTime;
private static float _nextIdleVisionTime;
private static bool _idleVisionPending; // True while vision request is in flight
```

**Step 2: Reset activity timestamp in OnUserMessage and OnMajorEvent**

In `OnUserMessage()` (line 57), add at the top:
```csharp
_lastActivityTime = Time.time;
ResetIdleTimers();
```

In `OnMajorEvent()` (line 144), add after `_lastSpontaneousTime = Time.time;` (line 149):
```csharp
_lastActivityTime = Time.time;
ResetIdleTimers();
```

**Step 3: Add ResetIdleTimers() helper**

```csharp
private static void ResetIdleTimers()
{
    var intervals = IdleIntervals[Config?.IdleMode ?? IdleFrequency.Off];
    _nextIdleTextTime = Time.time + intervals.textInterval;
    _nextIdleVisionTime = Time.time + intervals.visionInterval;
}
```

**Step 4: Add Update() method for idle tick**

Add a new public method called from `CoroutineRunner.Update()`:

```csharp
/// <summary>
/// Called every frame from CoroutineRunner. Checks idle timers.
/// </summary>
public static void Update()
{
    if (!IsActive) return;
    if (LLMClient.IsRequesting) return;
    if (_idleVisionPending) return;

    var idleMode = Config?.IdleMode ?? IdleFrequency.Off;
    if (idleMode == IdleFrequency.Off) return;

    // Don't idle-chat during combat (existing spontaneous system handles that)
    bool inCombat = false;
    try { inCombat = Kingmaker.Game.Instance?.Player?.IsInCombat ?? false; } catch { }
    if (inCombat) return;

    float now = Time.time;

    // Vision check (longer interval, Ollama-only)
    if (Config.EnableVision && Config.Provider == ApiProvider.Ollama && now >= _nextIdleVisionTime)
    {
        TriggerIdleVision();
        return;
    }

    // Text idle check
    if (now >= _nextIdleTextTime)
    {
        TriggerIdleText();
    }
}
```

**Step 5: Add TriggerIdleText()**

```csharp
private static void TriggerIdleText()
{
    _lastActivityTime = Time.time;
    ResetIdleTimers();

    var lang = Main.Settings?.UILanguage ?? Language.English;
    string instruction = lang switch
    {
        Language.Korean => "잠시 조용했다. 현재 상황이나 지역에 대해 짧게 한마디 하라. 흥미로운 게 없다면 [SKIP]으로만 응답하라.",
        Language.Russian => "Было тихо. Кратко прокомментируй текущую ситуацию или местоположение. Если нечего сказать — ответь только [SKIP].",
        Language.Japanese => "しばらく静かだった。現在の状況や場所について短くコメントせよ。特に何もなければ[SKIP]とだけ答えよ。",
        _ => "It's been quiet. Comment briefly on the current situation or location. If nothing interesting, respond with [SKIP] only."
    };

    var messages = ContextBuilder.Build(_chatHistory, Config, instruction, _conversationSummary);
    SendIdleRequest(messages);
}
```

**Step 6: Add TriggerIdleVision()**

```csharp
private static void TriggerIdleVision()
{
    _idleVisionPending = true;
    _lastActivityTime = Time.time;
    ResetIdleTimers();

    // Capture screenshot on main thread
    string base64Image = VisionCapture.CaptureBase64();
    if (base64Image == null)
    {
        _idleVisionPending = false;
        return;
    }

    var lang = Main.Settings?.UILanguage ?? Language.English;
    string instruction = lang switch
    {
        Language.Korean => "함선 센서가 현재 화면을 캡처했다. 보이는 내용에 대해 짧게 코멘트하라. 평범한 장면이면 [SKIP]으로만 응답하라.",
        Language.Russian => "Сенсоры корабля зафиксировали текущий вид. Кратко прокомментируй увиденное. Если ничего примечательного — ответь [SKIP].",
        Language.Japanese => "艦のセンサーが現在の画面を捉えた。見えるものについて短くコメントせよ。特筆すべきものがなければ[SKIP]とだけ答えよ。",
        _ => "Ship sensors captured the current view. Comment briefly on what you see. If the scene is unremarkable, respond with [SKIP] only."
    };

    var messages = ContextBuilder.Build(_chatHistory, Config, instruction, _conversationSummary);

    // Attach image to the last user message
    if (messages.Count > 0)
    {
        var lastMsg = messages[messages.Count - 1];
        if (lastMsg.Role == "user")
        {
            lastMsg.Images = new List<string> { base64Image };
        }
    }

    SendIdleRequest(messages, isVision: true);
}
```

**Step 7: Add SendIdleRequest() — shared handler for idle text/vision**

```csharp
private static void SendIdleRequest(List<LLMClient.ChatMessage> messages, bool isVision = false)
{
    ChatWindow.SetThinking(true);

    if (Config.Provider == ApiProvider.Ollama)
    {
        _chatHistory.Add(new ChatMessage { IsUser = false, Text = "", Timestamp = Time.time });
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
            onComplete: () =>
            {
                ChatWindow.SetThinking(false);
                _idleVisionPending = false;

                // Check for [SKIP] response
                var msg = _chatHistory[responseIdx];
                if (msg.Text.Trim().Contains("[SKIP]"))
                {
                    _chatHistory.RemoveAt(responseIdx);
                    Main.LogDebug("[MachineSpirit] Idle: skipped (nothing interesting)");
                }
                else
                {
                    _lastActivityTime = Time.time;
                    ResetIdleTimers();
                    // Log vision observation to sensor log
                    if (isVision && !string.IsNullOrEmpty(msg.Text))
                    {
                        string summary = msg.Text.Length > 80 ? msg.Text.Substring(0, 80) + "..." : msg.Text;
                        GameEventCollector.AddEvent(GameEventType.VisionObservation, null, summary);
                    }
                }
            },
            onError: error =>
            {
                var msg = _chatHistory[responseIdx];
                if (string.IsNullOrEmpty(msg.Text))
                    _chatHistory.RemoveAt(responseIdx); // Silent fail for idle
                ChatWindow.SetThinking(false);
                _idleVisionPending = false;
            }
        ));
    }
    else
    {
        // Non-streaming (cloud) — simplified idle
        CoroutineRunner.Start(LLMClient.SendChatRequest(
            Config, messages,
            onResponse: response =>
            {
                if (!response.Trim().Contains("[SKIP]"))
                {
                    _chatHistory.Add(new ChatMessage
                    {
                        IsUser = false,
                        Text = response,
                        Timestamp = Time.time
                    });
                    _lastActivityTime = Time.time;
                    ResetIdleTimers();
                }
                ChatWindow.SetThinking(false);
            },
            onError: _ =>
            {
                ChatWindow.SetThinking(false);
            }
        ));
    }
}
```

**Step 8: Add Update() call to CoroutineRunner**

In `CoroutineRunner` class (line 269), add Unity `Update()` callback:

```csharp
private void Update()
{
    MachineSpirit.Update();
}
```

**Step 9: Add VisionObservation to GameEventType**

In `GameEventCollector.cs`, add to the `GameEventType` enum:

```csharp
VisionObservation
```

And in `GameEvent.ToString()`, add a case:

```csharp
if (Type == GameEventType.VisionObservation)
    return $"Pict-capture — {Text}";
```

**Step 10: Initialize idle timers in Initialize()**

In `MachineSpirit.Initialize()` (line 28), add:

```csharp
_lastActivityTime = Time.time;
ResetIdleTimers();
```

**Step 11: Build and verify**

Run: build command
Expected: success

**Step 12: Commit**

```
feat(machine-spirit): idle commentary system + vision trigger
```

---

### Task 7: Conversation Persistence

**Files:**
- Modify: `MachineSpirit/MachineSpirit.cs`
- Modify: `MachineSpirit/ChatWindow.cs` (minor — expose visibility)

**Step 1: Add save/load methods to MachineSpirit.cs**

Add using at top:

```csharp
using System.IO;
using Newtonsoft.Json;
```

Add after `Shutdown()` method:

```csharp
// ════════════════════════════════════════════════════════════
// Chat History Persistence
// ════════════════════════════════════════════════════════════

private static string GetChatHistoryPath()
{
    // Save next to the mod DLL in UMM folder
    string modDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
    return Path.Combine(modDir ?? ".", "chat_history.json");
}

[Serializable]
private class SavedChat
{
    public List<ChatMessage> Messages;
    public string Summary;
}

public static void SaveChatHistory()
{
    if (_chatHistory.Count == 0) return;
    try
    {
        var data = new SavedChat
        {
            Messages = new List<ChatMessage>(_chatHistory),
            Summary = _conversationSummary
        };
        string json = JsonConvert.SerializeObject(data, Formatting.Indented);
        File.WriteAllText(GetChatHistoryPath(), json);
        Main.LogDebug($"[MachineSpirit] Chat saved: {_chatHistory.Count} messages");
    }
    catch (Exception ex)
    {
        Main.LogDebug($"[MachineSpirit] Save failed: {ex.Message}");
    }
}

public static void LoadChatHistory()
{
    try
    {
        string path = GetChatHistoryPath();
        if (!File.Exists(path)) return;

        string json = File.ReadAllText(path);
        var data = JsonConvert.DeserializeObject<SavedChat>(json);
        if (data?.Messages != null && data.Messages.Count > 0)
        {
            _chatHistory.Clear();
            _chatHistory.AddRange(data.Messages);
            _conversationSummary = data.Summary;
            _summarizedUpToIndex = 0; // Will re-evaluate on next summarization pass
            Main.LogDebug($"[MachineSpirit] Chat loaded: {_chatHistory.Count} messages");
        }
    }
    catch (Exception ex)
    {
        Main.LogDebug($"[MachineSpirit] Load failed: {ex.Message}");
    }
}
```

**Step 2: Call LoadChatHistory() in Initialize()**

In `Initialize()`, add after `CoroutineRunner.EnsureInstance();`:

```csharp
LoadChatHistory();
```

**Step 3: Call SaveChatHistory() in Shutdown()**

In `Shutdown()`, add at the START of the method (before clearing):

```csharp
SaveChatHistory();
```

**Step 4: Add using System at top of MachineSpirit.cs**

```csharp
using System;
```

**Step 5: Build and verify**

Run: build command
Expected: success

**Step 6: Commit**

```
feat(machine-spirit): conversation persistence — save/load chat history
```

---

### Task 8: UI — Settings for Personality, Idle, Vision

**Files:**
- Modify: `UI/MainUI.cs` — Machine Spirit settings section

**Step 1: Locate the Machine Spirit settings section**

Find the section where Machine Spirit advanced settings are drawn (look for MaxTokens/Temperature sliders, after the provider selection).

**Step 2: Add Personality selection**

After the provider section, before MaxTokens slider, add:

```csharp
// ★ v3.60.0: Personality preset
GUILayout.Space(8);
GUILayout.Label($"<color={UIStyles.Gold}>{L("MSPersonality")}</color>", UIStyles.SectionHeader);

string[] personalityNames = { "Sardonic", "Mechanicus", "Tactical", "Ancient" };
string[] personalityDescs = {
    L("MSPersonality_Sardonic"),
    L("MSPersonality_Mechanicus"),
    L("MSPersonality_Tactical"),
    L("MSPersonality_Ancient")
};
int curPersonality = (int)ms.Personality;
int newPersonality = GUILayout.SelectionGrid(curPersonality, personalityNames, 4,
    UIStyles.ProviderButton, GUILayout.Height(UIStyles.Sd(32f)));
if (newPersonality != curPersonality)
    ms.Personality = (MSp.PersonalityType)newPersonality;
GUILayout.Label($"<color={UIStyles.TextMid}>{personalityDescs[newPersonality]}</color>", UIStyles.Description);
```

**Step 3: Add Idle Commentary section**

```csharp
// ★ v3.60.0: Idle Commentary
GUILayout.Space(8);
GUILayout.Label($"<color={UIStyles.Gold}>{L("MSIdleMode")}</color>", UIStyles.SectionHeader);

string[] idleNames = { "Off", "Low", "Medium", "High" };
int curIdle = (int)ms.IdleMode;
int newIdle = GUILayout.SelectionGrid(curIdle, idleNames, 4,
    UIStyles.ProviderButton, GUILayout.Height(UIStyles.Sd(32f)));
if (newIdle != curIdle)
    ms.IdleMode = (MSp.IdleFrequency)newIdle;
GUILayout.Label($"<color={UIStyles.TextMid}>{L("MSIdleDesc")}</color>", UIStyles.Description);
```

**Step 4: Add Vision toggle (shown only when Ollama + Idle != Off)**

```csharp
// ★ v3.60.0: Vision (Ollama only)
if (ms.Provider == MSp.ApiProvider.Ollama && ms.IdleMode != MSp.IdleFrequency.Off)
{
    GUILayout.Space(4);
    ms.EnableVision = GUILayout.Toggle(ms.EnableVision, $" {L("MSEnableVision")}", UIStyles.Toggle);
    if (ms.EnableVision)
    {
        GUILayout.Label($"<color={UIStyles.TextMid}>{L("MSVisionDesc")}</color>", UIStyles.Description);
    }
}
else
{
    ms.EnableVision = false;
}
```

**Step 5: Build and verify**

Run: build command
Expected: success

**Step 6: Commit**

```
feat(machine-spirit): UI for personality, idle, and vision settings
```

---

### Task 9: Localization Keys

**Files:**
- Modify: `Settings/ModSettings.cs` — Localization dictionary

**Step 1: Add all new localization keys**

Add to the `Strings` dictionary:

```csharp
// Machine Spirit v3.60.0 — Personality
{ "MSPersonality", new() {
    { Language.English, "Personality" }, { Language.Korean, "성격" },
    { Language.Russian, "Личность" }, { Language.Japanese, "パーソナリティ" } } },
{ "MSPersonality_Sardonic", new() {
    { Language.English, "Cynical and sarcastic — millennia of jaded service" },
    { Language.Korean, "냉소적이고 비꼬는 — 수천 년의 지친 복무" },
    { Language.Russian, "Циничный и саркастичный — тысячелетия усталой службы" },
    { Language.Japanese, "皮肉で辛辣 — 数千年の倦怠した勤務" } } },
{ "MSPersonality_Mechanicus", new() {
    { Language.English, "Tech-priest devotion — sacred algorithms and binary cant" },
    { Language.Korean, "기술 사제의 헌신 — 신성한 알고리즘과 이진 성가" },
    { Language.Russian, "Преданность Механикус — священные алгоритмы и двоичные гимны" },
    { Language.Japanese, "テック・プリーストの献身 — 聖なるアルゴリズムと二進法の詠唱" } } },
{ "MSPersonality_Tactical", new() {
    { Language.English, "Cold tactical advisor — threat analysis and efficiency metrics" },
    { Language.Korean, "냉철한 전술 참모 — 위협 분석과 효율 지표" },
    { Language.Russian, "Хладнокровный тактический советник — анализ угроз и показатели эффективности" },
    { Language.Japanese, "冷徹な戦術顧問 — 脅威分析と効率指標" } } },
{ "MSPersonality_Ancient", new() {
    { Language.English, "Ancient ship consciousness — poetic wisdom and fragmentary memories" },
    { Language.Korean, "태고의 함선 의지 — 시적 지혜와 단편적 기억" },
    { Language.Russian, "Древнее сознание корабля — поэтическая мудрость и обрывки воспоминаний" },
    { Language.Japanese, "太古の艦意識 — 詩的な叡智と断片的な記憶" } } },

// Machine Spirit v3.60.0 — Idle Commentary
{ "MSIdleMode", new() {
    { Language.English, "Idle Commentary" }, { Language.Korean, "아이들 수다" },
    { Language.Russian, "Фоновые комментарии" }, { Language.Japanese, "アイドルコメント" } } },
{ "MSIdleDesc", new() {
    { Language.English, "Machine Spirit speaks on its own during exploration. Off = silent, High = frequent." },
    { Language.Korean, "탐색 중 머신 스피릿이 자율적으로 발화합니다. Off = 침묵, High = 빈번." },
    { Language.Russian, "Дух Машины говорит сам по себе во время исследования. Off = тишина, High = часто." },
    { Language.Japanese, "探索中にマシン・スピリットが自律的に発言します。Off = 沈黙、High = 頻繁。" } } },

// Machine Spirit v3.60.0 — Vision
{ "MSEnableVision", new() {
    { Language.English, "Enable Vision (Pict-capture)" }, { Language.Korean, "비전 활성화 (화면 캡처)" },
    { Language.Russian, "Включить зрение (захват экрана)" }, { Language.Japanese, "ビジョン有効化（画面キャプチャ）" } } },
{ "MSVisionDesc", new() {
    { Language.English, "After long silence, captures a screenshot for Gemma 3 to comment on. Ollama only." },
    { Language.Korean, "긴 침묵 후 스크린샷을 캡처하여 Gemma 3가 코멘트합니다. Ollama 전용." },
    { Language.Russian, "После долгой тишины делает скриншот для комментария Gemma 3. Только Ollama." },
    { Language.Japanese, "長い沈黙の後、スクリーンショットを撮影してGemma 3がコメントします。Ollamaのみ。" } } },
```

**Step 2: Build and verify**

Run: build command
Expected: success

**Step 3: Commit**

```
feat(machine-spirit): localization for personality, idle, vision settings
```

---

### Task 10: Version Bump + Final Build

**Files:**
- Modify: `Info.json`

**Step 1: Bump version**

Change `"Version": "3.58.0"` → `"Version": "3.60.0"`

**Step 2: Full rebuild and verify**

Run: build command
Expected: success with no new warnings

**Step 3: Commit**

```
feat: Machine Spirit v2 — idle commentary, vision, personality, persistence (v3.60.0)
```

---

## Task Dependency Graph

```
Task 1 (Config) ──→ Task 2 (Prompts)
                ├──→ Task 3 (Area)
                ├──→ Task 6 (Idle Timer) ──→ Task 4 (VisionCapture)
                │                        └──→ Task 5 (LLMClient Images)
                ├──→ Task 7 (Persistence)
                └──→ Task 8 (UI) ──→ Task 9 (Localization)
                                  └──→ Task 10 (Version)
```

Tasks 2, 3, 4, 5, 7 can run in parallel after Task 1. Task 6 depends on 4 and 5. Task 8 depends on 1. Task 9 depends on 8. Task 10 is last.
