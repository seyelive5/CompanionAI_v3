// MachineSpirit/Knowledge/BM25Search.cs
// ★ v3.70.0: BM25 keyword search engine for game knowledge
using System;
using System.Collections.Generic;
using System.Linq;

namespace CompanionAI_v3.MachineSpirit.Knowledge
{
    /// <summary>
    /// Pure C# BM25 implementation for keyword search over KnowledgeEntry list.
    /// </summary>
    public class BM25Search
    {
        private const float K1 = 1.2f;
        private const float B = 0.75f;

        private List<KnowledgeEntry> _entries;
        private Dictionary<string, float> _idf;  // term → IDF score
        private float _avgDocLen;
        private bool _isReady;

        public bool IsReady => _isReady;

        private static readonly HashSet<string> _stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
            "have", "has", "had", "do", "does", "did", "will", "would", "could",
            "should", "may", "might", "shall", "can", "to", "of", "in", "for",
            "on", "with", "at", "by", "from", "as", "into", "through", "during",
            "before", "after", "above", "below", "between", "out", "off", "over",
            "under", "again", "further", "then", "once", "and", "but", "or",
            "nor", "not", "so", "very", "just", "about", "up", "it", "its",
            "this", "that", "these", "those", "i", "me", "my", "we", "our",
            "you", "your", "he", "him", "his", "she", "her", "they", "them",
            "what", "which", "who", "whom", "when", "where", "why", "how"
        };

        /// <summary>
        /// Tokenize text: lowercase, split on non-alphanumeric, remove stopwords and 1-char tokens.
        /// </summary>
        public static string[] Tokenize(string text)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<string>();

            var tokens = new List<string>();
            var sb = new System.Text.StringBuilder();

            foreach (char c in text.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(c);
                }
                else if (sb.Length > 0)
                {
                    string token = sb.ToString();
                    sb.Clear();
                    if (token.Length > 1 && !_stopWords.Contains(token))
                        tokens.Add(token);
                }
            }
            if (sb.Length > 1)
            {
                string token = sb.ToString();
                if (!_stopWords.Contains(token))
                    tokens.Add(token);
            }

            return tokens.ToArray();
        }

        /// <summary>
        /// Build BM25 index from knowledge entries. Entries must have Tokens pre-populated.
        /// </summary>
        public void BuildIndex(List<KnowledgeEntry> entries)
        {
            _entries = entries;
            int N = entries.Count;
            if (N == 0) { _isReady = true; return; }

            // Compute average document length
            long totalLen = 0;
            foreach (var e in entries)
                totalLen += e.Tokens?.Length ?? 0;
            _avgDocLen = (float)totalLen / N;

            // Compute IDF for all terms
            var df = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                if (e.Tokens == null) continue;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var token in e.Tokens)
                {
                    if (seen.Add(token))
                    {
                        if (df.ContainsKey(token)) df[token]++;
                        else df[token] = 1;
                    }
                }
            }

            _idf = new Dictionary<string, float>(df.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in df)
            {
                _idf[kvp.Key] = (float)Math.Log((N - kvp.Value + 0.5f) / (kvp.Value + 0.5f) + 1f);
            }

            _isReady = true;
        }

        /// <summary>
        /// Search for query, return top-K results sorted by BM25 score.
        /// </summary>
        public List<SearchResult> Search(string query, int topK = 20)
        {
            if (!_isReady || _entries == null || _entries.Count == 0)
                return new List<SearchResult>();

            var queryTokens = Tokenize(query);
            if (queryTokens.Length == 0) return new List<SearchResult>();

            var results = new List<SearchResult>();

            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.Tokens == null || entry.Tokens.Length == 0) continue;

                float score = 0f;
                int docLen = entry.Tokens.Length;

                // Count term frequencies in this document
                var tf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var token in entry.Tokens)
                {
                    if (tf.ContainsKey(token)) tf[token]++;
                    else tf[token] = 1;
                }

                // Also boost exact title match
                bool titleMatch = entry.Title != null &&
                    entry.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

                foreach (var qToken in queryTokens)
                {
                    if (!_idf.TryGetValue(qToken, out float idf)) continue;
                    if (!tf.TryGetValue(qToken, out int termFreq)) continue;

                    float tfNorm = (termFreq * (K1 + 1f)) /
                        (termFreq + K1 * (1f - B + B * docLen / _avgDocLen));
                    score += idf * tfNorm;
                }

                // Title match bonus (significant boost for exact name matches)
                if (titleMatch) score *= 2.0f;

                if (score > 0.01f)
                {
                    results.Add(new SearchResult { Index = i, Score = score, Entry = entry });
                }
            }

            results.Sort((a, b) => b.Score.CompareTo(a.Score));
            if (results.Count > topK) results.RemoveRange(topK, results.Count - topK);
            return results;
        }
    }

    public class SearchResult
    {
        public int Index;
        public float Score;
        public KnowledgeEntry Entry;
    }
}
