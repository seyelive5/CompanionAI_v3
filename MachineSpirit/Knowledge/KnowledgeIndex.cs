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

        public static bool IsReady => _isReady;
        public static bool IsIndexing => _isIndexing;
        public static int IndexedCount => _indexedCount;
        public static IReadOnlyList<KnowledgeEntry> Entries => _entries;

        public static void StartIndexing()
        {
            if (_isIndexing || _isReady) return;
            _isIndexing = true;
            CoroutineRunner.Start(IndexCoroutine());
        }

        private static IEnumerator IndexCoroutine()
        {
            Main.LogDebug("[KnowledgeIndex] Starting background indexing...");
            _entries.Clear();
            _indexedCount = 0;

            // Phase 1: Weapons
            yield return IndexBlueprints<Kingmaker.Blueprints.Items.Weapons.BlueprintItemWeapon>("weapon", bp =>
            {
                string text = "";
                try { text = bp.Description; } catch { }
                if (string.IsNullOrEmpty(text))
                {
                    try
                    {
                        var sb = new System.Text.StringBuilder();
                        try { sb.Append($"Damage: {bp.WarhammerDamage}-{bp.WarhammerMaxDamage}. "); } catch { }
                        try { sb.Append($"Penetration: {bp.WarhammerPenetration}. "); } catch { }
                        try { sb.Append($"Range: {bp.WarhammerMaxDistance}. "); } catch { }
                        try { if (bp.DamageType?.Type != null) sb.Append($"Type: {bp.DamageType.Type}. "); } catch { }
                        text = sb.ToString();
                    }
                    catch { }
                }
                return text;
            });

            // Phase 2: Abilities
            yield return IndexBlueprints<Kingmaker.UnitLogic.Abilities.Blueprints.BlueprintAbility>("ability", bp =>
            {
                string text = "";
                try { text = bp.Description; } catch { }
                return text;
            });

            // Phase 3: Units (enemies/NPCs)
            yield return IndexBlueprints<Kingmaker.Blueprints.BlueprintUnit>("enemy", bp =>
            {
                string text = "";
                try { text = bp.Description; } catch { }
                return text;
            });

            // Phase 4: Quests
            yield return IndexBlueprints<Kingmaker.Blueprints.Quests.BlueprintQuest>("quest", bp =>
            {
                string text = "";
                try { text = bp.GetDescription(); } catch { }
                return text;
            });

            // Phase 5: Encyclopedia
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
            Main.LogDebug($"[KnowledgeIndex] Indexing complete: {_entries.Count} entries, BM25 ready");
        }

        /// <summary>
        /// Index all blueprints of type T. Constraint is BlueprintScriptableObject
        /// because Kingmaker.Cheats.Utilities.GetBlueprintGuids requires it.
        /// </summary>
        private static IEnumerator IndexBlueprints<T>(string category, Func<T, string> textExtractor)
            where T : BlueprintScriptableObject
        {
            IEnumerable<string> guids = null;
            try
            {
                guids = Kingmaker.Cheats.Utilities.GetBlueprintGuids<T>();
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[KnowledgeIndex] Failed to get GUIDs for {typeof(T).Name}: {ex.Message}");
                yield break;
            }

            if (guids == null) yield break;

            int batch = 0;
            foreach (string guid in guids)
            {
                try
                {
                    var bp = ResourcesLibrary.TryGetBlueprint<T>(guid);
                    if (bp == null) continue;

                    string title = bp.name;
                    if (string.IsNullOrEmpty(title)) continue;

                    string text = "";
                    try { text = textExtractor(bp); } catch { }

                    // Skip entries with no meaningful text
                    if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(title)) continue;

                    _entries.Add(new KnowledgeEntry
                    {
                        Id = guid,
                        Title = title,
                        Text = text ?? "",
                        Category = category
                    });
                    _indexedCount++;
                }
                catch { }

                if (++batch % 10 == 0) yield return null; // yield every 10
            }

            Main.LogDebug($"[KnowledgeIndex] Indexed {_indexedCount} entries (after {category})");
        }

        private static IEnumerator IndexEncyclopedia()
        {
            // Collect all pages via non-iterator recursive method (C# iterators can't yield inside try/catch)
            var pages = new List<Kingmaker.Blueprints.Encyclopedia.BlueprintEncyclopediaPage>();

            try
            {
                var chapterList = Kingmaker.Blueprints.Root.UIConfig.Instance?.ChapterList;
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
