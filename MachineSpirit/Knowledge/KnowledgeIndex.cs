// MachineSpirit/Knowledge/KnowledgeIndex.cs
// ★ v3.70.0: Background Blueprint/Encyclopedia indexing for RAG search
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Kingmaker;
using Kingmaker.Blueprints;

namespace CompanionAI_v3.MachineSpirit.Knowledge
{
    public static class KnowledgeIndex
    {
        private static List<KnowledgeEntry> _entries = new List<KnowledgeEntry>();
        private static BM25Search _bm25 = new BM25Search();
        private static bool _isIndexing;
        private static bool _isReady;
        private static int _indexedCount;
        private static int _totalEstimate;

        public static bool IsReady => _isReady;
        public static bool IsIndexing => _isIndexing;
        public static int IndexedCount => _indexedCount;
        public static IReadOnlyList<KnowledgeEntry> Entries => _entries;
        public static float Progress => _totalEstimate > 0 ? (float)_indexedCount / _totalEstimate : 0f;
        public static string StatusText { get; private set; } = "";

        public static void StartIndexing()
        {
            if (_isIndexing || _isReady) return;

            // Try loading from cache first
            if (TryLoadCache())
            {
                MachineSpirit.AddSystemMessage($"[Knowledge base loaded — {_entries.Count} entries]");
                return;
            }

            _isIndexing = true;
            CoroutineRunner.Start(IndexCoroutine());
        }

        private static IEnumerator IndexCoroutine()
        {
            // ★ Wait for game to fully load — BlueprintsCache needs Init() to complete
            Main.LogDebug("[KnowledgeIndex] Waiting for game to finish loading...");
            StatusText = "Waiting for game...";
            yield return new WaitForSeconds(15f);

            // Verify BlueprintsCache has data
            int retries = 0;
            while (retries < 5)
            {
                int guidCount = CountCacheEntries();
                if (guidCount > 0)
                {
                    Main.LogDebug($"[KnowledgeIndex] BlueprintsCache has {guidCount} entries, proceeding");
                    break;
                }
                retries++;
                Main.LogDebug($"[KnowledgeIndex] BlueprintsCache empty, retry {retries}/5 in 10s...");
                yield return new WaitForSeconds(10f);
            }

            Main.LogDebug("[KnowledgeIndex] Starting background indexing...");
            _entries.Clear();
            _indexedCount = 0;

            // ★ Single-pass indexing: iterate all GUIDs once, classify by type
            // 157K GUIDs × 4 type checks = too slow. Instead: load once, check type.
            var guids = CollectAllGuids();
            _totalEstimate = guids.Count;
            Main.LogDebug($"[KnowledgeIndex] Single-pass indexing {guids.Count} blueprints...");

            StatusText = "Indexing blueprints...";
            int processed = 0;
            int weapons = 0, abilities = 0, enemies = 0, quests = 0;

            foreach (string guid in guids)
            {
                try
                {
                    // Load as base type — one load per GUID, not four
                    var bp = ResourcesLibrary.TryGetBlueprint(guid);
                    if (bp == null) { processed++; continue; }

                    string category = null;
                    string text = "";
                    string title = bp.name;

                    if (string.IsNullOrEmpty(title)) { processed++; continue; }

                    // Classify by type (check most specific first)
                    if (bp is Kingmaker.Blueprints.Items.Weapons.BlueprintItemWeapon weapon)
                    {
                        category = "weapon";
                        try { text = weapon.Description; } catch { }
                        if (string.IsNullOrEmpty(text))
                        {
                            try
                            {
                                var sb = new System.Text.StringBuilder();
                                try { sb.Append($"Damage: {weapon.WarhammerDamage}-{weapon.WarhammerMaxDamage}. "); } catch { }
                                try { sb.Append($"Penetration: {weapon.WarhammerPenetration}. "); } catch { }
                                try { sb.Append($"Range: {weapon.WarhammerMaxDistance}. "); } catch { }
                                try { if (weapon.DamageType?.Type != null) sb.Append($"Type: {weapon.DamageType.Type}. "); } catch { }
                                text = sb.ToString();
                            }
                            catch { }
                        }
                        weapons++;
                    }
                    else if (bp is Kingmaker.UnitLogic.Abilities.Blueprints.BlueprintAbility ability)
                    {
                        category = "ability";
                        try { text = ability.Description; } catch { }
                        abilities++;
                    }
                    else if (bp is Kingmaker.Blueprints.BlueprintUnit unit)
                    {
                        category = "enemy";
                        try { text = unit.Description; } catch { }
                        enemies++;
                    }
                    else if (bp is Kingmaker.Blueprints.Quests.BlueprintQuest quest)
                    {
                        category = "quest";
                        try { text = quest.GetDescription(); } catch { }
                        quests++;
                    }
                    // Skip types we don't care about
                    else { processed++; continue; }

                    if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(title)) { processed++; continue; }

                    _entries.Add(new KnowledgeEntry
                    {
                        Id = bp.AssetGuid ?? "",
                        Title = title,
                        Text = text ?? "",
                        Category = category
                    });
                    _indexedCount++;
                }
                catch { }

                processed++;
                if (processed % 100 == 0)
                {
                    StatusText = $"Indexing... {processed * 100 / guids.Count}%";
                    yield return null; // yield every 100
                }
            }

            Main.LogDebug($"[KnowledgeIndex] Blueprint pass: {weapons} weapons, {abilities} abilities, {enemies} enemies, {quests} quests");

            // Phase 5: Encyclopedia
            StatusText = "Indexing lore...";
            yield return IndexEncyclopedia();

            // Build BM25 index
            Main.LogDebug($"[KnowledgeIndex] Tokenizing {_entries.Count} entries...");
            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                string combined = (entry.Title ?? "") + " " + (entry.Text ?? "");
                entry.Tokens = BM25Search.Tokenize(combined);

                if (i % 50 == 0) yield return null; // yield every 50
            }

            _bm25.BuildIndex(_entries);
            _isReady = true;
            _isIndexing = false;
            StatusText = $"Ready ({_entries.Count} entries)";
            Main.LogDebug($"[KnowledgeIndex] Indexing complete: {_entries.Count} entries, BM25 ready");
            SaveCache();

            // ★ v3.70.0: Notify user that knowledge base is ready
            try
            {
                MachineSpirit.AddSystemMessage($"[Knowledge base ready — {_entries.Count} entries indexed]");
            }
            catch { }
        }

        // Cached GUID list — collected once, shared across all IndexBlueprints calls
        private static List<string> _allGuids;

        private static int CountCacheEntries()
        {
            try
            {
                var dict = GetBlueprintsDictionary();
                return dict?.Count ?? 0;
            }
            catch { return 0; }
        }

        private static System.Collections.IDictionary GetBlueprintsDictionary()
        {
            try
            {
                // ResourcesLibrary.BlueprintsCache is public static readonly
                var cache = ResourcesLibrary.BlueprintsCache;
                if (cache == null) return null;

                var dictField = cache.GetType().GetField("m_LoadedBlueprints",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                return dictField?.GetValue(cache) as System.Collections.IDictionary;
            }
            catch { return null; }
        }

        private static List<string> CollectAllGuids()
        {
            if (_allGuids != null && _allGuids.Count > 0) return _allGuids;

            _allGuids = new List<string>();
            try
            {
                var dict = GetBlueprintsDictionary();
                if (dict == null)
                {
                    Main.LogDebug("[KnowledgeIndex] m_LoadedBlueprints dictionary is null");
                    return _allGuids;
                }

                foreach (var key in dict.Keys)
                    _allGuids.Add(key.ToString());

                Main.LogDebug($"[KnowledgeIndex] Collected {_allGuids.Count} blueprint GUIDs from cache");
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[KnowledgeIndex] CollectAllGuids failed: {ex.Message}");
            }
            return _allGuids;
        }

        /// <summary>
        /// Index blueprints by iterating all cached GUIDs and filtering by type T.
        /// </summary>
        private static IEnumerator IndexBlueprints<T>(string category, Func<T, string> textExtractor)
            where T : BlueprintScriptableObject
        {
            var guids = CollectAllGuids();
            if (guids.Count == 0)
            {
                Main.LogDebug($"[KnowledgeIndex] No GUIDs available for {category}");
                yield break;
            }
            _totalEstimate = guids.Count;

            int batch = 0;
            int typeMatches = 0;
            foreach (string guid in guids)
            {
                try
                {
                    var bp = ResourcesLibrary.TryGetBlueprint<T>(guid);
                    if (bp == null) continue; // Not this type — skip

                    typeMatches++;
                    string internalName = bp.name;
                    if (string.IsNullOrEmpty(internalName)) continue;

                    string text = "";
                    try { text = textExtractor(bp); } catch { }

                    if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(internalName)) continue;

                    _entries.Add(new KnowledgeEntry
                    {
                        Id = bp.AssetGuid ?? "",
                        Title = internalName,
                        Text = text ?? "",
                        Category = category
                    });
                    _indexedCount++;
                }
                catch { }

                if (++batch % 10 == 0) yield return null;
            }

            Main.LogDebug($"[KnowledgeIndex] Indexed {_indexedCount} entries (after {category}, {typeMatches} type matches)");
        }

        private static IEnumerator IndexEncyclopedia()
        {
            // Collect all pages via non-iterator recursive method (C# iterators can't yield inside try/catch)
            var pages = new List<Kingmaker.Blueprints.Encyclopedia.BlueprintEncyclopediaPage>();

            try
            {
                var chapterList = Game.Instance?.BlueprintRoot?.UIConfig?.ChapterList;
                if (chapterList == null)
                {
                    Main.LogDebug("[KnowledgeIndex] Encyclopedia ChapterList not available");
                    yield break;
                }

                foreach (var chapter in chapterList)
                {
                    if (chapter != null)
                        CollectEncyclopediaPages(chapter, pages);
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[KnowledgeIndex] Encyclopedia collection failed: {ex.Message}");
            }

            // Index collected pages with yielding (outside try/catch)
            int encyclopediaCount = 0;
            for (int i = 0; i < pages.Count; i++)
            {
                IndexSingleEncyclopediaPage(pages[i], ref encyclopediaCount);
                if (i % 10 == 0) yield return null;
            }

            Main.LogDebug($"[KnowledgeIndex] Indexed {encyclopediaCount} encyclopedia entries");
        }

        private static void CollectEncyclopediaPages(
            Kingmaker.Blueprints.Encyclopedia.BlueprintEncyclopediaPage page,
            List<Kingmaker.Blueprints.Encyclopedia.BlueprintEncyclopediaPage> result)
        {
            if (page == null) return;
            result.Add(page);
            try
            {
                var children = page.ChildPages;
                if (children == null) return;
                foreach (var childRef in children)
                {
                    try
                    {
                        var child = childRef?.Get();
                        if (child != null) CollectEncyclopediaPages(child, result);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void IndexSingleEncyclopediaPage(
            Kingmaker.Blueprints.Encyclopedia.BlueprintEncyclopediaPage page,
            ref int count)
        {
            try
            {
                string title = null;
                try { title = page.Title?.Text; } catch { }
                if (string.IsNullOrEmpty(title)) title = page.name;

                var sb = new System.Text.StringBuilder();
                try
                {
                    var blocks = page.Blocks;
                    if (blocks != null)
                    {
                        foreach (var block in blocks)
                        {
                            var textBlock = block as Kingmaker.Blueprints.Encyclopedia.Blocks.BlueprintEncyclopediaBlockText;
                            if (textBlock?.Text != null)
                            {
                                try
                                {
                                    string blockText = textBlock.Text.Text;
                                    if (!string.IsNullOrEmpty(blockText))
                                    {
                                        if (sb.Length > 0) sb.Append(" ");
                                        sb.Append(blockText);
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { }

                string text = sb.ToString();
                if (text.Length > 500) text = text.Substring(0, 500);

                if (!string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(text))
                {
                    _entries.Add(new KnowledgeEntry
                    {
                        Id = page.AssetGuid ?? "",
                        Title = title ?? "",
                        Text = text,
                        Category = "lore"
                    });
                    _indexedCount++;
                    count++;
                }
            }
            catch { }
        }

        private static string GetCachePath()
        {
            return System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                "knowledge_cache.json");
        }

        public static bool TryLoadCache()
        {
            try
            {
                string path = GetCachePath();
                if (!System.IO.File.Exists(path)) return false;

                string json = System.IO.File.ReadAllText(path);
                var cached = Newtonsoft.Json.JsonConvert.DeserializeObject<List<KnowledgeEntry>>(json);
                if (cached == null || cached.Count == 0) return false;

                _entries = cached;
                _indexedCount = _entries.Count;

                // Rebuild BM25 tokens (not saved in cache to reduce file size)
                foreach (var entry in _entries)
                {
                    string combined = (entry.Title ?? "") + " " + (entry.Text ?? "");
                    entry.Tokens = BM25Search.Tokenize(combined);
                }

                _bm25.BuildIndex(_entries);
                _isReady = true;
                StatusText = $"Loaded from cache ({_entries.Count} entries)";
                Main.LogDebug($"[KnowledgeIndex] Loaded {_entries.Count} entries from cache");
                return true;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[KnowledgeIndex] Cache load failed: {ex.Message}");
                return false;
            }
        }

        private static void SaveCache()
        {
            try
            {
                // Clear tokens and embeddings before saving (rebuilt on load)
                var saveEntries = new List<KnowledgeEntry>();
                foreach (var entry in _entries)
                {
                    saveEntries.Add(new KnowledgeEntry
                    {
                        Id = entry.Id,
                        Title = entry.Title,
                        Text = entry.Text,
                        Category = entry.Category
                        // Tokens and Embedding are NOT saved
                    });
                }

                string json = Newtonsoft.Json.JsonConvert.SerializeObject(saveEntries, Newtonsoft.Json.Formatting.None);
                System.IO.File.WriteAllText(GetCachePath(), json);
                Main.LogDebug($"[KnowledgeIndex] Saved {_entries.Count} entries to cache");
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[KnowledgeIndex] Cache save failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Search for query. Returns top-K results from BM25 (hybrid when vector ready).
        /// </summary>
        public static List<SearchResult> Search(string query, int topK = 5)
        {
            if (!_isReady) return new List<SearchResult>();
            return _bm25.Search(query, topK);
        }

        /// <summary>
        /// Detect if message is a game knowledge question and search if so.
        /// Returns null if not a question or no relevant results.
        /// </summary>
        public static List<SearchResult> DetectAndSearch(string message)
        {
            if (!_isReady || string.IsNullOrEmpty(message)) return null;

            // Heuristic: is this a game knowledge question?
            bool isQuestion = message.Contains("?")
                || ContainsAny(message, "뭐", "어떻게", "추천", "최적", "비교", "차이", "알려", "설명")
                || ContainsAny(message, "what", "how", "best", "recommend", "damage", "range",
                    "weapon", "ability", "quest", "enemy", "where", "which", "tell me");

            if (!isQuestion) return null;

            var results = Search(message, 5);

            // Only return if we have meaningful results (score threshold)
            if (results.Count == 0 || results[0].Score < 0.5f) return null;

            return results;
        }

        private static bool ContainsAny(string text, params string[] keywords)
        {
            foreach (var kw in keywords)
            {
                if (text.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }
    }
}
