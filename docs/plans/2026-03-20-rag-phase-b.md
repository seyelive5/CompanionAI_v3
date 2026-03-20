# RAG Phase B — Hybrid Search Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Enable Machine Spirit to answer freeform game questions by searching Blueprint/Encyclopedia data with BM25+vector hybrid search.

**Architecture:** Background coroutine indexes game data into KnowledgeEntry list. BM25 available immediately, vector embeddings computed lazily via Ollama /api/embed. User questions detected by heuristic → search → [REFERENCE DATA] injected into LLM prompt with personality preserved.

**Tech Stack:** C# .NET 4.8.1, Unity coroutines, Ollama /api/embed (nomic-embed-text), in-memory index

---

### Task 1: KnowledgeEntry + BM25Search

**Files:**
- Create: `MachineSpirit/Knowledge/KnowledgeEntry.cs`
- Create: `MachineSpirit/Knowledge/BM25Search.cs`

**Step 1: Create KnowledgeEntry struct**

```csharp
// MachineSpirit/Knowledge/KnowledgeEntry.cs
namespace CompanionAI_v3.MachineSpirit.Knowledge
{
    public class KnowledgeEntry
    {
        public string Id;           // Blueprint GUID
        public string Title;        // Display name
        public string Text;         // Full description text
        public string Category;     // weapon, ability, quest, lore, enemy, item
        public string[] Tokens;     // Pre-tokenized for BM25
        public float[] Embedding;   // null until vector computed
    }
}
```

**Step 2: Create BM25Search**

Pure C# BM25 implementation. Key methods:
- `Tokenize(string text)` → lowercase split + stopword removal
- `BuildIndex(List<KnowledgeEntry> entries)` → compute IDF for all terms
- `Search(string query, int topK)` → return scored results

```csharp
// MachineSpirit/Knowledge/BM25Search.cs
// BM25 with k1=1.2, b=0.75
// Tokenize: lowercase, split on non-alphanumeric, remove 1-char tokens
// IDF: log((N - df + 0.5) / (df + 0.5) + 1)
// TF normalization: (tf * (k1+1)) / (tf + k1*(1-b+b*docLen/avgDocLen))
```

**Step 3: Build and verify**

Run: MSBuild command
Expected: 0 errors

**Step 4: Commit**

```
feat: KnowledgeEntry struct + BM25 keyword search engine
```

---

### Task 2: KnowledgeIndex — Background Indexing

**Files:**
- Create: `MachineSpirit/Knowledge/KnowledgeIndex.cs`

**Step 1: Create KnowledgeIndex**

Static class with background coroutine indexing. Key methods:
- `StartIndexing()` → launches coroutine
- `IndexCoroutine()` → processes 5-10 blueprints per frame
- `IsReady` → true when BM25 index is built
- `Search(string query, int topK)` → delegates to BM25 (and later hybrid)

Indexing targets (in order):
1. `BlueprintItemWeapon` via `Utilities.GetBlueprintGuids<BlueprintItemWeapon>()`
2. `BlueprintAbility` via `Utilities.GetBlueprintGuids<BlueprintAbility>()`
3. `BlueprintUnit` via `Utilities.GetBlueprintGuids<BlueprintUnit>()`
4. `BlueprintQuest` via `Utilities.GetBlueprintGuids<BlueprintQuest>()`
5. Encyclopedia via `UIConfig.Instance.ChapterList` tree traversal

For each blueprint:
- Load via `ResourcesLibrary.TryGetBlueprint<T>(guid)`
- Extract title (`.name`), text (`.Description` or `.RawDescription`)
- Create KnowledgeEntry, tokenize for BM25
- Yield every 10 entries to avoid frame stutter

After all entries collected → `BM25Search.BuildIndex(entries)`

**Step 2: Wire into MachineSpirit.Initialize()**

After existing initialization, add:
```csharp
KnowledgeIndex.StartIndexing();
```

**Step 3: Build and verify**

Run: MSBuild command
Expected: 0 errors

**Step 4: Commit**

```
feat: KnowledgeIndex — background Blueprint/Encyclopedia indexing
```

---

### Task 3: VectorSearch + HybridSearch

**Files:**
- Create: `MachineSpirit/Knowledge/VectorSearch.cs`
- Create: `MachineSpirit/Knowledge/HybridSearch.cs`

**Step 1: Add GetEmbedding to LLMClient**

In `MachineSpirit/LLMClient.cs`, add method to call Ollama `/api/embed`:
```csharp
public static IEnumerator GetEmbedding(string text, Action<float[]> onResult, Action<string> onError)
{
    var payload = JsonConvert.SerializeObject(new { model = "nomic-embed-text", input = text });
    // POST to http://localhost:11434/api/embed
    // Parse response.embeddings[0] as float[]
    // Call onResult with the embedding
}
```

**Step 2: Create VectorSearch**

- `ComputeEmbeddings(List<KnowledgeEntry> entries)` → coroutine, batch 5 at a time via /api/embed
- `Search(float[] queryEmbedding, int topK)` → brute-force dot product, return top-K
- `IsReady` → true when all embeddings computed

**Step 3: Create HybridSearch**

- `Search(string query, float[] queryEmbedding, int topK)` → RRF combination
- If VectorSearch not ready → BM25 only
- If both ready → BM25 top-20 + Vector top-20 → RRF(k=60) → top-K

```csharp
// RRF: score(doc) = sum(1/(k + rank_i)) across all result lists
const int k = 60;
```

**Step 4: Build and verify**

Run: MSBuild command
Expected: 0 errors

**Step 5: Commit**

```
feat: VectorSearch + HybridSearch — embedding + RRF combination
```

---

### Task 4: Question Detection + ContextBuilder Integration

**Files:**
- Modify: `MachineSpirit/MachineSpirit.cs` (OnUserMessage method)
- Modify: `MachineSpirit/ContextBuilder.cs` (add BuildForKnowledgeQuery)

**Step 1: Add question detection in OnUserMessage**

In `MachineSpirit.OnUserMessage(string text)`, before the existing `ContextBuilder.Build()` call:

```csharp
// ★ RAG Phase B: detect game knowledge question
List<KnowledgeEntry> searchResults = null;
if (KnowledgeIndex.IsReady)
{
    searchResults = KnowledgeIndex.DetectAndSearch(text);
}
```

`DetectAndSearch` heuristic:
- Contains `?` OR question keywords (뭐/어떻게/추천/최적/what/how/best/recommend/damage/range)
- Run search → if top result score > threshold → return results
- Otherwise return null (treat as normal conversation)

**Step 2: Add BuildForKnowledgeQuery to ContextBuilder**

New method that wraps existing Build() but adds [REFERENCE DATA]:
```csharp
public static List<LLMClient.ChatMessage> BuildForKnowledgeQuery(
    string query, List<KnowledgeEntry> results,
    List<ChatMessage> history, MachineSpiritConfig config, string summary)
{
    // Same as Build() but inject [REFERENCE DATA] section
    // + "Answer based on reference data. Be accurate. Keep personality.
    //    If data doesn't contain the answer, say you don't know."
}
```

**Step 3: Route in OnUserMessage**

```csharp
var messages = (searchResults != null && searchResults.Count > 0)
    ? ContextBuilder.BuildForKnowledgeQuery(text, searchResults, _chatHistory, Config, _conversationSummary)
    : ContextBuilder.Build(_chatHistory, Config, conversationSummary: _conversationSummary);
```

**Step 4: Build and verify**

Run: MSBuild command
Expected: 0 errors

**Step 5: Commit**

```
feat: RAG question detection + knowledge-aware response generation
```

---

### Task 5: Background Embedding + Version Bump + Release

**Files:**
- Modify: `MachineSpirit/Knowledge/KnowledgeIndex.cs` (start embedding after BM25 ready)
- Modify: `Info.json` (version bump)

**Step 1: Start background embedding after BM25 index is built**

At the end of `IndexCoroutine()`, after BM25 is ready:
```csharp
// Start lazy vector embedding (only if Ollama is the provider)
if (MachineSpirit.Config?.Provider == ApiProvider.Ollama)
{
    CoroutineRunner.Start(VectorSearch.ComputeEmbeddings(_entries));
}
```

**Step 2: Wire query embedding in search flow**

In `KnowledgeIndex.DetectAndSearch()`, if VectorSearch.IsReady:
- Get query embedding via LLMClient.GetEmbedding
- Pass to HybridSearch.Search()
- Otherwise fall back to BM25 only

Note: GetEmbedding is async (coroutine). For simplicity, cache the query embedding
from a separate coroutine and use BM25 results immediately, enhancing with vector
results on next query if available.

**Step 3: Version bump**

Info.json: "3.68.0" → "3.70.0"

**Step 4: Full build and verify**

Run: MSBuild command
Expected: 0 errors

**Step 5: Commit + push + release**

```
feat: RAG Phase B — hybrid search, background indexing, knowledge queries
```
