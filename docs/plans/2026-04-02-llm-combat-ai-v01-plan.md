# LLM 전투 AI v0.1 구현 계획

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Ollama LLM이 턴제 전투에서 실제 전술적 의사결정을 내릴 수 있는지 실험하는 시스템을 CompanionAI 내부에 구축한다.

**Architecture:** TurnOrchestrator에서 LLM 모드 유닛을 감지하면 기존 TurnPlanner 대신 LLMDecisionEngine으로 분기. 전장 상태를 JSON으로 직렬화 → Ollama 비스트리밍 호출 → 응답 검증 → ActionExecutor로 실행. 실패 시 TurnPlanner fallback + 오버레이에 실패 이유 표시.

**Tech Stack:** C# (.NET 4.8.1), Harmony 2.2.2, Newtonsoft.Json, Unity IMGUI, Ollama REST API

**설계 문서:** [2026-04-02-llm-combat-ai-v01-design.md](2026-04-02-llm-combat-ai-v01-design.md)

---

## Task 1: 설정 인프라 — CharacterSettings에 LLM 모드 추가

**Files:**
- Modify: `Settings/ModSettings.cs` — CharacterSettings 클래스에 EnableLLMMode 속성 추가
- Create: `LLM_CombatAI/LLMCombatSettings.cs` — LLM 전역 설정 + 유닛 LLM 모드 조회 헬퍼

**Step 1: CharacterSettings에 EnableLLMMode 추가**

`Settings/ModSettings.cs`의 `CharacterSettings` 클래스 (line ~2145 부근)에:
```csharp
public bool EnableLLMMode { get; set; } = false;
```

**Step 2: LLMCombatSettings.cs 생성**

```csharp
// LLM_CombatAI/LLMCombatSettings.cs
namespace CompanionAI_v3.LLM_CombatAI
{
    public static class LLMCombatSettings
    {
        // Ollama 모델명 (기본값: gemma3:4b)
        public static string ModelName => Main.Settings?.LLMModelName ?? "gemma3:4b";

        // 시스템 프롬프트
        public static string SystemPrompt => @"You are a tactical combat AI for Warhammer 40K: Rogue Trader.
Given the battlefield state, choose the best action.

Rules:
- You can move AND use a skill in one turn (if AP/MP allows)
- Prioritize: finish wounded enemies > heal critically injured allies > buff before big attacks > deal maximum damage
- Consider weapon range - don't move unnecessarily if already in range
- NEVER target allies with attack skills
- Use healing on allies with lowest HP first
- Buffs should be used before attacking when AP allows

Respond ONLY with valid JSON:
{
  ""reasoning"": ""brief tactical explanation"",
  ""action"": ""attack"" | ""move"" | ""move_and_attack"" | ""buff"" | ""heal"" | ""defend"",
  ""target"": ""enemy_id or ally_name"",
  ""skill"": ""skill_id from the provided list"",
  ""move_to"": [x, y] (optional, only if moving)
}";

        // 유닛이 LLM 모드인지 확인
        public static bool IsLLMControlled(BaseUnitEntity unit)
        {
            if (unit == null) return false;
            var settings = Main.Settings?.GetCharacterSettings(unit);
            return settings?.EnableLLMMode ?? false;
        }
    }
}
```

**Step 3: ModSettings에 LLMModelName 전역 설정 추가**

`Settings/ModSettings.cs`의 ModSettings 클래스에:
```csharp
public string LLMModelName { get; set; } = "gemma3:4b";
```

**Step 4: 빌드 확인**

```powershell
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo
```
Expected: 빌드 성공 (에러 0)

**Step 5: 커밋**

```bash
git add LLM_CombatAI/LLMCombatSettings.cs Settings/ModSettings.cs
git commit -m "feat(llm): add LLM mode settings infrastructure"
```

---

## Task 2: 전장 직렬화 — BattlefieldSerializer

**Files:**
- Create: `LLM_CombatAI/BattlefieldSerializer.cs` — Situation → JSON 변환
- Reference: `Analysis/Situation.cs`, `GameInterface/CombatAPI.cs`

**Step 1: BattlefieldSerializer.cs 생성**

Situation 객체에서 LLM에게 보낼 JSON을 생성한다. 토큰 절약을 위해 최소한의 정보만 포함.

```csharp
// LLM_CombatAI/BattlefieldSerializer.cs
using Newtonsoft.Json.Linq;

namespace CompanionAI_v3.LLM_CombatAI
{
    public static class BattlefieldSerializer
    {
        public static string Serialize(BaseUnitEntity unit, Situation situation)
        {
            var root = new JObject();

            // 현재 유닛 정보
            var unitObj = new JObject
            {
                ["name"] = situation.CharacterName,
                ["hp"] = $"{situation.HPPercent:F0}%",
                ["ap"] = situation.CurrentAP,
                ["mp"] = situation.CurrentMP,
                ["pos"] = new JArray(
                    (int)(unit.Position.x / 1.35f),
                    (int)(unit.Position.z / 1.35f))
            };

            // 사용 가능한 스킬 목록
            var skills = new JArray();
            var abilities = CombatAPI.GetAvailableAbilities(unit);
            foreach (var ability in abilities)
            {
                var info = AbilityDatabase.GetInfo(ability);
                var skillObj = new JObject
                {
                    ["id"] = AbilityDatabase.GetGuid(ability),
                    ["name"] = ability.Name,
                    ["ap"] = CombatAPI.GetAbilityAPCost(ability),
                    ["range"] = CombatAPI.GetAbilityRangeInTiles(ability)
                };
                // 타입 분류
                if (info != null)
                {
                    var timing = info.Timing;
                    skillObj["type"] = ClassifyAbilityType(timing);
                }
                else
                {
                    skillObj["type"] = "attack"; // 미등록은 공격으로 추정
                }
                skills.Add(skillObj);
            }
            unitObj["skills"] = skills;
            root["unit"] = unitObj;

            // 적 유닛 (최대 8명, 거리순)
            var enemies = new JArray();
            foreach (var enemy in situation.Enemies)
            {
                if (enemies.Count >= 8) break;
                var hp = CombatAPI.GetHPPercent(enemy);
                var dist = CombatAPI.GetDistanceInTiles(unit, enemy);
                enemies.Add(new JObject
                {
                    ["id"] = enemy.UniqueId,
                    ["name"] = enemy.CharacterName,
                    ["hp"] = $"{hp:F0}%",
                    ["pos"] = new JArray(
                        (int)(enemy.Position.x / 1.35f),
                        (int)(enemy.Position.z / 1.35f)),
                    ["dist"] = $"{dist:F1}",
                    ["threat"] = ClassifyThreat(enemy, situation)
                });
            }
            root["enemies"] = enemies;

            // 아군 유닛
            var allies = new JArray();
            foreach (var ally in situation.Allies)
            {
                if (ally == unit) continue; // 자기 자신 제외
                var hp = CombatAPI.GetHPPercent(ally);
                allies.Add(new JObject
                {
                    ["name"] = ally.CharacterName,
                    ["hp"] = $"{hp:F0}%",
                    ["pos"] = new JArray(
                        (int)(ally.Position.x / 1.35f),
                        (int)(ally.Position.z / 1.35f))
                });
            }
            root["allies"] = allies;

            return root.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static string ClassifyAbilityType(AbilityTiming timing)
        {
            switch (timing)
            {
                case AbilityTiming.PreAttackBuff:
                case AbilityTiming.PreCombatBuff:
                case AbilityTiming.PostFirstAction:
                    return "buff";
                case AbilityTiming.Heal:
                    return "heal";
                case AbilityTiming.Debuff:
                    return "debuff";
                default:
                    return "attack";
            }
        }

        private static string ClassifyThreat(BaseUnitEntity enemy, Situation situation)
        {
            if (enemy == situation.BestTarget) return "high";
            var hp = CombatAPI.GetHPPercent(enemy);
            if (hp < 30f) return "low"; // 빈사
            return "medium";
        }
    }
}
```

**Step 2: 빌드 확인**

Expected: 빌드 성공. Situation/CombatAPI 참조 정상.

**Step 3: 커밋**

```bash
git add LLM_CombatAI/BattlefieldSerializer.cs
git commit -m "feat(llm): add battlefield state JSON serializer"
```

---

## Task 3: LLM 클라이언트 — 비스트리밍 호출

**Files:**
- Modify: `MachineSpirit/LLMClient.cs` — SendOllamaNonStreaming() 메서드 추가

**Step 1: LLMClient에 비스트리밍 메서드 추가**

기존 `SendOllamaStreaming()` (line 221) 패턴을 기반으로, `stream: false` + `format: "json"` 버전 추가.

```csharp
/// <summary>
/// Ollama 비스트리밍 호출 — 전투 AI용.
/// stream: false, format: "json" 강제.
/// </summary>
public static IEnumerator SendOllamaNonStreaming(
    string model,
    string systemPrompt,
    string userMessage,
    float temperature,
    int maxTokens,
    Action<string> onResponse,
    Action<string> onError)
{
    var url = GetOllamaBaseUrl() + "/api/chat";

    var messages = new JArray
    {
        new JObject { ["role"] = "system", ["content"] = systemPrompt },
        new JObject { ["role"] = "user", ["content"] = userMessage }
    };

    var body = new JObject
    {
        ["model"] = model,
        ["messages"] = messages,
        ["stream"] = false,
        ["format"] = "json",
        ["options"] = new JObject
        {
            ["temperature"] = temperature,
            ["num_predict"] = maxTokens
        },
        ["keep_alive"] = -1
    };

    var request = new UnityWebRequest(url, "POST");
    var bodyBytes = System.Text.Encoding.UTF8.GetBytes(body.ToString());
    request.uploadHandler = new UploadHandlerRaw(bodyBytes);
    request.downloadHandler = new DownloadHandlerBuffer();
    request.SetRequestHeader("Content-Type", "application/json");

    yield return request.SendWebRequest();

    if (request.isNetworkError || request.isHttpError)
    {
        onError?.Invoke($"Ollama error: {request.error}");
        request.Dispose();
        yield break;
    }

    try
    {
        var responseJson = JObject.Parse(request.downloadHandler.text);
        var content = responseJson["message"]?["content"]?.ToString();
        if (string.IsNullOrEmpty(content))
        {
            onError?.Invoke("Empty response from Ollama");
        }
        else
        {
            onResponse?.Invoke(content);
        }
    }
    catch (Exception ex)
    {
        onError?.Invoke($"Response parse error: {ex.Message}");
    }
    finally
    {
        request.Dispose();
    }
}

// Ollama base URL 헬퍼 (기존 코드에서 추출 또는 재사용)
private static string GetOllamaBaseUrl()
{
    // MachineSpirit config에서 URL 가져오기, 기본값 localhost:11434
    return "http://localhost:11434";
}
```

**Step 2: 빌드 확인**

**Step 3: 커밋**

```bash
git add MachineSpirit/LLMClient.cs
git commit -m "feat(llm): add non-streaming Ollama method for combat AI"
```

---

## Task 4: 명령 검증 — LLMCommandValidator

**Files:**
- Create: `LLM_CombatAI/LLMCommandValidator.cs` — JSON 파싱 + 유효성 검증

**Step 1: LLMCommandValidator.cs 생성**

LLM 응답 JSON을 파싱하고, 스킬 GUID 존재 여부, 타겟 유효성, AP 충분 여부를 검증한다.

```csharp
// LLM_CombatAI/LLMCommandValidator.cs
namespace CompanionAI_v3.LLM_CombatAI
{
    public class LLMCommand
    {
        public string Reasoning { get; set; }
        public string Action { get; set; }      // attack, move, buff, heal, etc.
        public string TargetId { get; set; }
        public string SkillId { get; set; }     // ability GUID
        public float? MoveX { get; set; }
        public float? MoveY { get; set; }
    }

    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public string FailReason { get; set; }
        public LLMCommand Command { get; set; }
        public PlannedAction PlannedAction { get; set; }  // 변환된 게임 명령
    }

    public static class LLMCommandValidator
    {
        public static ValidationResult Validate(
            string llmResponse,
            BaseUnitEntity unit,
            Situation situation)
        {
            // Step 1: JSON 파싱
            JObject json;
            try
            {
                json = JObject.Parse(llmResponse);
            }
            catch (Exception ex)
            {
                return Fail($"JSON 파싱 실패: {ex.Message}", llmResponse);
            }

            var command = new LLMCommand
            {
                Reasoning = json["reasoning"]?.ToString() ?? "",
                Action = json["action"]?.ToString()?.ToLower() ?? "",
                TargetId = json["target"]?.ToString() ?? "",
                SkillId = json["skill"]?.ToString() ?? ""
            };

            var moveTo = json["move_to"] as JArray;
            if (moveTo != null && moveTo.Count >= 2)
            {
                command.MoveX = moveTo[0].Value<float>() * 1.35f; // 타일 → 미터
                command.MoveY = moveTo[1].Value<float>() * 1.35f;
            }

            // Step 2: 이동 전용 액션
            if (command.Action == "move" || command.Action == "defend")
            {
                return ValidateMoveOnly(command, unit, situation);
            }

            // Step 3: 스킬 GUID 검증
            if (string.IsNullOrEmpty(command.SkillId))
            {
                return Fail("스킬 ID 누락", command);
            }

            var abilities = CombatAPI.GetAvailableAbilities(unit);
            AbilityData matchedAbility = null;
            foreach (var ability in abilities)
            {
                if (AbilityDatabase.GetGuid(ability) == command.SkillId)
                {
                    matchedAbility = ability;
                    break;
                }
            }

            if (matchedAbility == null)
            {
                // GUID 환각 — 이름으로 폴백 시도
                var skillName = json["skill_name"]?.ToString();
                if (!string.IsNullOrEmpty(skillName))
                {
                    foreach (var ability in abilities)
                    {
                        if (ability.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase))
                        {
                            matchedAbility = ability;
                            break;
                        }
                    }
                }
                if (matchedAbility == null)
                {
                    return Fail($"존재하지 않는 스킬 GUID: {command.SkillId}", command);
                }
            }

            // Step 4: AP 충분 여부
            float apCost = CombatAPI.GetAbilityAPCost(matchedAbility);
            if (situation.CurrentAP < apCost)
            {
                return Fail($"AP 부족: 필요 {apCost}, 보유 {situation.CurrentAP}", command);
            }

            // Step 5: 타겟 검증
            BaseUnitEntity targetUnit = null;
            if (!string.IsNullOrEmpty(command.TargetId))
            {
                targetUnit = FindUnitById(command.TargetId, situation);
                if (targetUnit == null)
                {
                    return Fail($"타겟을 찾을 수 없음: {command.TargetId}", command);
                }
            }

            // Step 6: PlannedAction 변환
            var plannedAction = new PlannedAction
            {
                Type = ClassifyActionType(command.Action),
                Ability = matchedAbility,
                Target = targetUnit != null ? new TargetWrapper(targetUnit) : null,
                APCost = apCost
            };

            // 이동 + 공격인 경우 이동 목적지 설정
            if (command.MoveX.HasValue && command.MoveY.HasValue)
            {
                plannedAction.MoveDestination = new Vector3(
                    command.MoveX.Value, unit.Position.y, command.MoveY.Value);
            }

            return new ValidationResult
            {
                IsValid = true,
                Command = command,
                PlannedAction = plannedAction
            };
        }

        private static ValidationResult ValidateMoveOnly(
            LLMCommand command, BaseUnitEntity unit, Situation situation)
        {
            if (!command.MoveX.HasValue || !command.MoveY.HasValue)
            {
                return Fail("이동 좌표 누락", command);
            }

            var dest = new Vector3(command.MoveX.Value, unit.Position.y, command.MoveY.Value);
            var plannedAction = new PlannedAction
            {
                Type = ActionType.Move,
                MoveDestination = dest
            };

            return new ValidationResult
            {
                IsValid = true,
                Command = command,
                PlannedAction = plannedAction
            };
        }

        private static BaseUnitEntity FindUnitById(string id, Situation situation)
        {
            foreach (var enemy in situation.Enemies)
                if (enemy.UniqueId == id) return enemy;
            foreach (var ally in situation.Allies)
                if (ally.UniqueId == id || ally.CharacterName == id) return ally;
            return null;
        }

        private static ActionType ClassifyActionType(string action)
        {
            switch (action)
            {
                case "attack":
                case "move_and_attack": return ActionType.Attack;
                case "buff": return ActionType.Buff;
                case "heal": return ActionType.Heal;
                case "move": return ActionType.Move;
                case "defend": return ActionType.EndTurn;
                default: return ActionType.Attack;
            }
        }

        private static ValidationResult Fail(string reason, LLMCommand command)
        {
            return new ValidationResult
            {
                IsValid = false,
                FailReason = reason,
                Command = command
            };
        }

        private static ValidationResult Fail(string reason, string rawResponse)
        {
            return new ValidationResult
            {
                IsValid = false,
                FailReason = reason,
                Command = new LLMCommand { Reasoning = rawResponse }
            };
        }
    }
}
```

**Step 2: 빌드 확인**

**Step 3: 커밋**

```bash
git add LLM_CombatAI/LLMCommandValidator.cs
git commit -m "feat(llm): add command validator with GUID/target/AP checks"
```

---

## Task 5: 의사결정 엔진 — LLMDecisionEngine

**Files:**
- Create: `LLM_CombatAI/LLMDecisionEngine.cs` — 직렬화 → LLM 호출 → 검증 → 결과 반환

**Step 1: LLMDecisionEngine.cs 생성**

코루틴 기반으로 Ollama 호출 → 검증 → PlannedAction 또는 실패 이유 반환.

```csharp
// LLM_CombatAI/LLMDecisionEngine.cs
namespace CompanionAI_v3.LLM_CombatAI
{
    public class LLMDecisionResult
    {
        public bool Success { get; set; }
        public PlannedAction Action { get; set; }
        public LLMCommand Command { get; set; }     // LLM 원본 명령
        public string FailReason { get; set; }
        public string RawResponse { get; set; }
        public float ResponseTime { get; set; }      // 초
    }

    public static class LLMDecisionEngine
    {
        private static LLMDecisionResult _pendingResult;
        private static bool _isProcessing;

        public static bool IsProcessing => _isProcessing;
        public static LLMDecisionResult PendingResult => _pendingResult;

        /// <summary>
        /// LLM 의사결정 시작. 코루틴으로 실행되며, 완료 시 PendingResult에 결과 저장.
        /// </summary>
        public static IEnumerator Decide(BaseUnitEntity unit, Situation situation)
        {
            _isProcessing = true;
            _pendingResult = null;
            var startTime = Time.realtimeSinceStartup;

            // 1. 전장 직렬화
            string battlefieldJson;
            try
            {
                battlefieldJson = BattlefieldSerializer.Serialize(unit, situation);
            }
            catch (Exception ex)
            {
                _pendingResult = new LLMDecisionResult
                {
                    Success = false,
                    FailReason = $"직렬화 실패: {ex.Message}",
                    ResponseTime = Time.realtimeSinceStartup - startTime
                };
                _isProcessing = false;
                yield break;
            }

            Main.LogDebug($"[LLM] {unit.CharacterName} 전장 직렬화 완료 ({battlefieldJson.Length}자)");

            // 2. Ollama 호출
            string llmResponse = null;
            string llmError = null;

            yield return LLMClient.SendOllamaNonStreaming(
                model: LLMCombatSettings.ModelName,
                systemPrompt: LLMCombatSettings.SystemPrompt,
                userMessage: battlefieldJson,
                temperature: 0.3f,
                maxTokens: 200,
                onResponse: response => llmResponse = response,
                onError: error => llmError = error);

            float responseTime = Time.realtimeSinceStartup - startTime;

            // 3. 에러 처리
            if (llmError != null)
            {
                _pendingResult = new LLMDecisionResult
                {
                    Success = false,
                    FailReason = llmError,
                    ResponseTime = responseTime
                };
                _isProcessing = false;
                yield break;
            }

            Main.LogDebug($"[LLM] {unit.CharacterName} 응답 수신 ({responseTime:F1}초): {llmResponse}");

            // 4. 명령 검증
            var validation = LLMCommandValidator.Validate(llmResponse, unit, situation);

            _pendingResult = new LLMDecisionResult
            {
                Success = validation.IsValid,
                Action = validation.PlannedAction,
                Command = validation.Command,
                FailReason = validation.FailReason,
                RawResponse = llmResponse,
                ResponseTime = responseTime
            };
            _isProcessing = false;
        }

        public static void Reset()
        {
            _pendingResult = null;
            _isProcessing = false;
        }
    }
}
```

**Step 2: 빌드 확인**

**Step 3: 커밋**

```bash
git add LLM_CombatAI/LLMDecisionEngine.cs
git commit -m "feat(llm): add decision engine (serialize → call → validate)"
```

---

## Task 6: 상태 오버레이 — LLMStatusOverlay

**Files:**
- Create: `LLM_CombatAI/LLMStatusOverlay.cs` — IMGUI 기반 실시간 상태 표시
- Create: `LLM_CombatAI/LLMCombatLogger.cs` — 누적 통계

**Step 1: LLMCombatLogger.cs 생성**

```csharp
// LLM_CombatAI/LLMCombatLogger.cs
namespace CompanionAI_v3.LLM_CombatAI
{
    public static class LLMCombatLogger
    {
        private static int _totalTurns;
        private static int _successCount;
        private static int _fallbackCount;
        private static float _totalResponseTime;
        private static readonly Dictionary<string, int> _failReasons = new Dictionary<string, int>();

        public static int TotalTurns => _totalTurns;
        public static int SuccessCount => _successCount;
        public static int FallbackCount => _fallbackCount;
        public static float SuccessRate => _totalTurns > 0 ? (float)_successCount / _totalTurns * 100f : 0f;
        public static float AvgResponseTime => _totalTurns > 0 ? _totalResponseTime / _totalTurns : 0f;
        public static IReadOnlyDictionary<string, int> FailReasons => _failReasons;

        public static void RecordSuccess(float responseTime)
        {
            _totalTurns++;
            _successCount++;
            _totalResponseTime += responseTime;
        }

        public static void RecordFallback(float responseTime, string reason)
        {
            _totalTurns++;
            _fallbackCount++;
            _totalResponseTime += responseTime;

            // 실패 이유 카테고리화
            string category = CategorizeReason(reason);
            if (_failReasons.ContainsKey(category))
                _failReasons[category]++;
            else
                _failReasons[category] = 1;
        }

        public static void Reset()
        {
            _totalTurns = 0;
            _successCount = 0;
            _fallbackCount = 0;
            _totalResponseTime = 0f;
            _failReasons.Clear();
        }

        private static string CategorizeReason(string reason)
        {
            if (reason.Contains("GUID")) return "GUID 환각";
            if (reason.Contains("JSON")) return "JSON 파싱";
            if (reason.Contains("AP")) return "AP 부족";
            if (reason.Contains("타겟")) return "타겟 무효";
            if (reason.Contains("Ollama")) return "Ollama 에러";
            if (reason.Contains("타임아웃") || reason.Contains("timeout")) return "타임아웃";
            return "기타";
        }
    }
}
```

**Step 2: LLMStatusOverlay.cs 생성**

```csharp
// LLM_CombatAI/LLMStatusOverlay.cs
using UnityEngine;

namespace CompanionAI_v3.LLM_CombatAI
{
    public static class LLMStatusOverlay
    {
        private static LLMDecisionResult _lastResult;
        private static string _lastUnitName;
        private static float _displayUntil;  // Time.realtimeSinceStartup 기준

        private static readonly Color SuccessColor = new Color(0.2f, 0.8f, 0.2f);
        private static readonly Color FailColor = new Color(0.9f, 0.2f, 0.2f);
        private static readonly Color BgColor = new Color(0f, 0f, 0f, 0.85f);

        private const float DisplayDuration = 8f;  // 8초간 표시

        public static void ShowResult(string unitName, LLMDecisionResult result)
        {
            _lastResult = result;
            _lastUnitName = unitName;
            _displayUntil = Time.realtimeSinceStartup + DisplayDuration;
        }

        public static void DrawGUI()
        {
            if (_lastResult == null) return;
            if (Time.realtimeSinceStartup > _displayUntil) return;

            float width = 420f;
            float x = Screen.width - width - 20f;
            float y = 20f;

            // 배경
            var bgStyle = new GUIStyle(GUI.skin.box);
            bgStyle.normal.background = MakeTex(2, 2, BgColor);

            GUILayout.BeginArea(new Rect(x, y, width, 300f), bgStyle);
            GUILayout.Space(8);

            // 제목
            var titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };
            GUILayout.Label("LLM Combat AI", titleStyle);
            GUILayout.Space(4);

            var labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                normal = { textColor = Color.white },
                wordWrap = true
            };

            GUILayout.Label($"유닛: {_lastUnitName}", labelStyle);

            // 상태
            var statusStyle = new GUIStyle(labelStyle)
            {
                fontStyle = FontStyle.Bold,
                normal = { textColor = _lastResult.Success ? SuccessColor : FailColor }
            };

            if (_lastResult.Success)
            {
                GUILayout.Label("상태: ✓ LLM 성공", statusStyle);
                GUILayout.Label($"응답시간: {_lastResult.ResponseTime:F1}초", labelStyle);
                if (_lastResult.Command != null)
                {
                    GUILayout.Label($"LLM 판단: \"{_lastResult.Command.Reasoning}\"", labelStyle);
                    GUILayout.Label($"명령: {_lastResult.Command.Action} → {_lastResult.Command.TargetId} ({_lastResult.Command.SkillId})", labelStyle);
                }
            }
            else
            {
                GUILayout.Label("상태: ✗ FALLBACK (TurnPlanner)", statusStyle);
                GUILayout.Label($"응답시간: {_lastResult.ResponseTime:F1}초", labelStyle);
                GUILayout.Label($"실패 이유: {_lastResult.FailReason}", labelStyle);
                if (_lastResult.RawResponse != null)
                {
                    var truncated = _lastResult.RawResponse.Length > 100
                        ? _lastResult.RawResponse.Substring(0, 100) + "..."
                        : _lastResult.RawResponse;
                    GUILayout.Label($"LLM 원본: {truncated}", labelStyle);
                }
                GUILayout.Label("→ TurnPlanner가 대신 처리", labelStyle);
            }

            // 누적 통계
            GUILayout.Space(8);
            var statStyle = new GUIStyle(labelStyle)
            {
                fontSize = 11,
                normal = { textColor = new Color(0.7f, 0.7f, 0.7f) }
            };
            GUILayout.Label($"[통계] 성공: {LLMCombatLogger.SuccessCount}/{LLMCombatLogger.TotalTurns} ({LLMCombatLogger.SuccessRate:F0}%) | 평균: {LLMCombatLogger.AvgResponseTime:F1}초", statStyle);

            GUILayout.EndArea();
        }

        // 단색 텍스처 생성 헬퍼
        private static Texture2D MakeTex(int w, int h, Color col)
        {
            var pix = new Color[w * h];
            for (int i = 0; i < pix.Length; i++) pix[i] = col;
            var tex = new Texture2D(w, h);
            tex.SetPixels(pix);
            tex.Apply();
            return tex;
        }
    }
}
```

**Step 3: 빌드 확인**

**Step 4: 커밋**

```bash
git add LLM_CombatAI/LLMCombatLogger.cs LLM_CombatAI/LLMStatusOverlay.cs
git commit -m "feat(llm): add status overlay and combat statistics logger"
```

---

## Task 7: TurnOrchestrator 통합 — LLM 분기 삽입

**Files:**
- Modify: `Core/TurnOrchestrator.cs` — ProcessTurn에서 LLM 유닛 분기

**Step 1: TurnOrchestrator.cs 수정**

`PlanAndExecutePhase()` 메서드 (line 209 부근)에서 `_planner.CreatePlan()` 호출 전에 LLM 분기를 삽입한다.

핵심 로직:
1. `LLMCombatSettings.IsLLMControlled(unit)` 체크
2. 예 → 코루틴으로 `LLMDecisionEngine.Decide()` 시작
3. 코루틴 완료 대기 (ComputePhase 활용)
4. 성공 → ActionExecutor.Execute() + 오버레이 초록
5. 실패 → TurnPlanner.CreatePlan() fallback + 오버레이 빨강

**주의사항:**
- TurnOrchestrator는 프레임 기반 (코루틴이 아닌 Tick 호출). LLM 응답 대기를 위해 새 ComputePhase 추가 필요.
- `WaitingForLLM` 상태 추가: LLM 응답 도착 전까지 `ExecutionResult.Waiting` 반환.

TurnOrchestrator에 추가할 코드 (PlanAndExecutePhase 진입 시):
```csharp
// LLM 분기 — TurnPlanner.CreatePlan() 호출 전
if (LLMCombatSettings.IsLLMControlled(unit))
{
    return HandleLLMTurn(unit, unitName, turnState, situation);
}
```

새 메서드:
```csharp
private ExecutionResult HandleLLMTurn(
    BaseUnitEntity unit, string unitName,
    TurnState turnState, Situation situation)
{
    // Phase 1: LLM 호출 시작
    if (!LLMDecisionEngine.IsProcessing && LLMDecisionEngine.PendingResult == null)
    {
        Main.LogDebug($"[LLM] {unitName} LLM 의사결정 시작");
        Main.StartCoroutine(LLMDecisionEngine.Decide(unit, situation));
        return ExecutionResult.Waiting();
    }

    // Phase 2: 대기 중
    if (LLMDecisionEngine.IsProcessing)
    {
        return ExecutionResult.Waiting();
    }

    // Phase 3: 결과 처리
    var result = LLMDecisionEngine.PendingResult;
    LLMDecisionEngine.Reset();

    if (result.Success)
    {
        // LLM 성공 → 직접 실행
        Main.LogDebug($"[LLM] {unitName} LLM 성공: {result.Command.Action} → {result.Command.TargetId}");
        LLMCombatLogger.RecordSuccess(result.ResponseTime);
        LLMStatusOverlay.ShowResult(unitName, result);
        return _executor.Execute(result.Action, situation);
    }
    else
    {
        // LLM 실패 → TurnPlanner fallback
        Main.LogDebug($"[LLM] {unitName} LLM 실패: {result.FailReason} → TurnPlanner fallback");
        LLMCombatLogger.RecordFallback(result.ResponseTime, result.FailReason);
        LLMStatusOverlay.ShowResult(unitName, result);

        // 기존 TurnPlanner 경로
        turnState.Plan = _planner.CreatePlan(situation, turnState);
        return ExecuteNextAction(unit, unitName, turnState, situation);
    }
}
```

**Step 2: Main.cs에 코루틴 실행 헬퍼 확인**

`Main.StartCoroutine()` 가능 여부 확인. UMM 환경에서는 MonoBehaviour가 필요하므로, 기존 패턴 확인 필요.
- 이미 MachineSpirit에서 코루틴을 실행하는 패턴이 있으면 동일하게 사용.
- 없으면 `CoroutineRunner` MonoBehaviour 추가.

**Step 3: LLMStatusOverlay.DrawGUI()를 Main.OnGUI에 연결**

`Main.cs`의 `OnGUI` 콜백에:
```csharp
LLMStatusOverlay.DrawGUI();
```

**Step 4: 빌드 확인**

**Step 5: 커밋**

```bash
git add Core/TurnOrchestrator.cs Main.cs
git commit -m "feat(llm): integrate LLM decision branch into TurnOrchestrator"
```

---

## Task 8: UI 토글 — MainUI에 LLM 모드 추가

**Files:**
- Modify: `UI/MainUI.cs` — DrawCharacterAISettings에 LLM 토글 추가

**Step 1: MainUI.cs 수정**

`DrawCharacterAISettings()` (line 1182 부근)에서 기존 토글 다음에:
```csharp
// LLM 모드 토글
_editingSettings.EnableLLMMode = DrawCheckbox(
    _editingSettings.EnableLLMMode,
    "LLM Combat AI");

if (_editingSettings.EnableLLMMode)
{
    GUILayout.Label($"  모델: {LLMCombatSettings.ModelName}",
        new GUIStyle(GUI.skin.label) { fontSize = 11 });
}
```

**Step 2: 빌드 확인**

**Step 3: 커밋**

```bash
git add UI/MainUI.cs
git commit -m "feat(llm): add per-unit LLM mode toggle in MainUI"
```

---

## Task 9: 전투 초기화/종료 — 통계 리셋 + 요약

**Files:**
- Modify: `Core/TurnOrchestrator.cs` 또는 `GameInterface/TurnEventHandler.cs` — 전투 시작/종료 이벤트에 LLM 통계 연결

**Step 1: 전투 시작 시 통계 리셋**

TurnEventHandler 또는 TurnOrchestrator의 전투 시작 지점에:
```csharp
LLMCombatLogger.Reset();
LLMDecisionEngine.Reset();
```

**Step 2: 전투 종료 시 요약 로그**

전투 종료 이벤트에:
```csharp
if (LLMCombatLogger.TotalTurns > 0)
{
    Main.Log($"[LLM 전투 통계] 총 턴: {LLMCombatLogger.TotalTurns}, " +
        $"성공: {LLMCombatLogger.SuccessCount} ({LLMCombatLogger.SuccessRate:F0}%), " +
        $"평균 응답: {LLMCombatLogger.AvgResponseTime:F1}초");

    foreach (var reason in LLMCombatLogger.FailReasons)
        Main.Log($"  실패 원인: {reason.Key} × {reason.Value}회");
}
```

**Step 3: 빌드 확인**

**Step 4: 커밋**

```bash
git add Core/TurnOrchestrator.cs  # 또는 TurnEventHandler.cs
git commit -m "feat(llm): add combat start/end hooks for LLM statistics"
```

---

## Task 10: 통합 빌드 + 최종 검증

**Files:**
- Modify: `Info.json` — 버전 업데이트

**Step 1: Info.json 버전 업데이트**

현재 버전에서 +2 (LLM 실험 기능 추가):
```json
"Version": "3.76.0"
```

**Step 2: 전체 리빌드**

```powershell
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo
```
Expected: 빌드 성공, 경고 0, 에러 0

**Step 3: 최종 체크리스트**

- [ ] `LLM_CombatAI/` 폴더에 6개 파일 생성 확인
- [ ] TurnOrchestrator에 LLM 분기 삽입 확인
- [ ] MainUI에 LLM 토글 표시 확인
- [ ] ModSettings에 EnableLLMMode + LLMModelName 추가 확인
- [ ] LLMClient에 SendOllamaNonStreaming 추가 확인
- [ ] 전투 시작/종료 이벤트에 통계 연결 확인
- [ ] 빌드 성공

**Step 4: 최종 커밋**

```bash
git add Info.json
git commit -m "feat(llm): LLM Combat AI v0.1 experiment — complete integration"
```

---

## 실행 순서 요약

| Task | 내용 | 의존성 |
|------|------|--------|
| 1 | 설정 인프라 | 없음 |
| 2 | 전장 직렬화 | Task 1 |
| 3 | LLM 비스트리밍 클라이언트 | 없음 |
| 4 | 명령 검증 | Task 1 |
| 5 | 의사결정 엔진 | Task 2, 3, 4 |
| 6 | 상태 오버레이 + 통계 | Task 5 |
| 7 | TurnOrchestrator 통합 | Task 5, 6 |
| 8 | UI 토글 | Task 1 |
| 9 | 전투 초기화/종료 | Task 6, 7 |
| 10 | 통합 빌드 + 검증 | 전체 |

**병렬 가능**: Task 1+3 (의존성 없음), Task 2+4 (Task 1 완료 후), Task 6+8 (독립)
