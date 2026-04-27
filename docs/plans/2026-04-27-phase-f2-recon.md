# Phase F.2 Recon — LLM HTTP Plumbing Audit + API Design Proposal

Date: 2026-04-27  
Audited: LLMCommander (456), LLMScorer (371), LLMJudge (857), LLMWarmup (212) = 1896 lines total

## Executive Summary

All 4 LLM callers use identical Ollama /api/chat endpoint with UnityWebRequest async coroutines (except Warmup's sync unload). Request bodies are structurally identical (model + messages + stream=false + keep_alive=-1 + options). Response parsing and latching are completely divergent.

**Verdict: HIGHLY CONSOLIDATABLE** — 70% boilerplate overlap. Estimated net change: **-80 to -140 lines** after creating LLMHttpClient.

---

## Per-File Comparison

| Item | LLMCommander | LLMScorer | LLMJudge | LLMWarmup |
|---|---|---|---|---|
| Endpoint | /api/chat | /api/chat | /api/chat | /api/chat (warmup) + /api/generate (unload) |
| Library | UnityWebRequest async | UnityWebRequest async | UnityWebRequest async | UnityWebRequest async + HttpWebRequest sync (unload) |
| Timeout | 30s | 30s | 30s | 120s (warmup); 0.5s + 1.5s cap (unload) |
| Latch | _isCommanding | _isScoring + _scoringStartTime | _isJudging | _isWarming + _warmedModels |
| Watchdog | none | TickWatchdog (C3) | none | none |
| Request Body | model, messages, stream, keep_alive, options (temp, num_predict), think | Same (num_predict=120) | Same (num_predict=50) | Same (num_predict=1); unload: model+keep_alive only |
| Response Parse | message.content JSON: focus_target, formation, synergy, encounter_type, narration | message.content JSON: aoe_weight, focus_fire, heal_priority, priority_target, reasoning | message.content: letter A/B/C or ratios "A:0.7,B:0.3" | None |

---

## Common HTTP Patterns (70% — CONSOLIDATABLE)

### 1. Request Building
All async: model + messages (system/user) + stream=false + keep_alive=-1 + options {temp: 0, num_predict: N} + think: false
**→ Consolidate to**: LLMHttpClient.BuildChatRequest(model, systemMsg, userMsg, numPredict)

### 2. UnityWebRequest Lifecycle  
All async: Create POST → uploadHandler (UTF8) → downloadHandler (buffer) → header "Content-Type: application/json" → timeout → SendWebRequest() → poll with deadline
**→ Consolidate to**: LLMHttpClient.PostChatAsync(baseUrl, requestBody, timeoutSeconds, onResponse) → IEnumerator

### 3. Response Extraction
All parse: JObject.Parse(response)["message"]["content"]?.ToString()
**→ Consolidate to**: LLMHttpClient.ExtractContent(rawResponse) → string

### 4. URL Normalization
All: TrimEnd('/') + if EndsWith("/v1") remove 3 chars. Duplicated in 4 files.
**→ Consolidate to**: LLMHttpClient.NormalizeBaseUrl(baseUrl) → string

### 5. Model Resolution
All: LLMJudgeModel → MachineSpirit.Model → "gemma4:e4b"
**→ Consolidate to**: LLMHttpClient.ResolveModel() → string

---

## Non-Consolidatable (30% — CALLER-OWNED)

### 1. Latch Semantics
- Commander: _isCommanding (simple set/clear)
- Scorer: _isScoring + _scoringStartTime (watchdog needs timestamp)
- Judge: _isJudging (simple)
- Warmup: _isWarming + _warmedModels HashSet (tracks preloaded models)

Each unique. Cannot centralize.

### 2. Response Parsing
- Commander → CommanderDirective: focus_target (int), formation (enum), synergy, encounter_type, narration
- Scorer → ScorerWeights: aoe_weight, focus_fire, heal_priority, priority_target, reasoning
- Judge → int index or JudgeConfidence: letter or "A:0.7,B:0.3" distribution
- Warmup: no parsing

Completely domain-specific. Must stay.

### 3. Judge's Candidate Shuffling
Fisher-Yates shuffle + primacy bias diagnosis. Unique to Judge.

### 4. Scorer's Watchdog (C3 Fix)
Force-resets _isScoring latch if elapsed > TIMEOUT + 5s grace. Scorer-specific safety net (commits 19a5365 + 18417c8).

---

## Special Cases

### LLMWarmup: Dual Paths
**Warmup (async)**: UnityWebRequest, /api/chat, content="ready", num_predict=1, success tracked in _warmedModels  
**Unload (sync)**: HttpWebRequest blocking, /api/generate, model+keep_alive=0, 1500ms total budget cap (avoid shutdown hang)

Unload must be sync — coroutines unsafe during shutdown. **Cannot use UnityWebRequest.**

### LLMJudge: Two Variants
**Judge()**: Returns int index  
**JudgeWithConfidence()**: Returns JudgeConfidence struct with float[] ratios

Share HTTP layer, differ in response parsing.

---

## LLMHttpClient API Proposal

`csharp
namespace CompanionAI_v3.Planning.LLM
{
    public static class LLMHttpClient
    {
        public struct Response
        {
            public bool Success;
            public string RawJson;              // Response body
            public string ErrorMessage;
            public int HttpStatusCode;
            public float ElapsedSeconds;
            public bool WasTimeout;
        }

        // Shared helpers
        public static string ResolveModel();
        public static string NormalizeBaseUrl(string baseUrl);
        public static JObject BuildChatRequest(
            string model, string systemMsg, string userMsg,
            int numPredict, float temperature = 0f,
            bool think = false, int keepAlive = -1);

        // Main async (all 4 callers use from coroutine)
        public static IEnumerator PostChatAsync(
            string baseUrl, JObject requestBody, int timeoutSeconds,
            Action<Response> onResponse);

        // Extract message.content
        public static string ExtractContent(string rawResponse);

        // Sync POST (Warmup unload only)
        public static Response PostGenerateSync(
            string baseUrl, string model,
            int timeoutMs, int readWriteTimeoutMs);

        // Watchdog helper (optional)
        public static bool CheckWatchdog(
            ref bool latch, float startTime,
            float timeoutSeconds, float graceSeconds);
    }
}
`

---

## Migration Impact

### Lines Removed
- Commander: 35 lines
- Scorer: 40 lines  
- Judge: 35 lines
- Warmup: 25 lines

**Total removed: 135 lines**

### Lines Added
- LLMHttpClient: ~158 lines (ResolveModel, NormalizeBaseUrl, BuildChatRequest, PostChatAsync, ExtractContent, PostGenerateSync, CheckWatchdog, struct, comments)

**Net: +23 lines** but eliminates 75 lines of duplication across files.

---

## Consolidation Readiness

**YES — RECOMMENDED**

- Similarity: 70% identical HTTP plumbing
- Risk: LOW (callers retain all domain logic)
- Effort: 4–6 hours
- Benefits: Single source of truth, easier to extend/maintain

**Cannot consolidate**: Latches (per-caller), parsing (domain), sync unload (needs HttpWebRequest), Scorer's watchdog (C3).

