// MachineSpirit/Knowledge/HybridSearch.cs
// ★ v3.70.0: Reciprocal Rank Fusion combining BM25 + Vector search
using System.Collections.Generic;
using System.Linq;

namespace CompanionAI_v3.MachineSpirit.Knowledge
{
    /// <summary>
    /// Combines BM25 keyword search and vector similarity search
    /// using Reciprocal Rank Fusion (RRF).
    /// Falls back to BM25-only when vector embeddings are not ready.
    /// </summary>
    public static class HybridSearch
    {
        private const int RRF_K = 60; // Standard RRF constant

        /// <summary>
        /// Hybrid search: BM25 + Vector results combined via RRF.
        /// If vectorResults is null/empty, returns BM25 results only.
        /// </summary>
        public static List<SearchResult> Search(
            List<SearchResult> bm25Results,
            List<SearchResult> vectorResults,
            int topK = 5)
        {
            var rrfScores = new Dictionary<int, float>(); // Index → score
            var entryMap = new Dictionary<int, KnowledgeEntry>(); // Index → entry

            // Add BM25 results
            if (bm25Results != null)
            {
                for (int rank = 0; rank < bm25Results.Count; rank++)
                {
                    int idx = bm25Results[rank].Index;
                    rrfScores[idx] = 1f / (RRF_K + rank + 1);
                    entryMap[idx] = bm25Results[rank].Entry;
                }
            }

            // Add vector results (if available)
            if (vectorResults != null)
            {
                for (int rank = 0; rank < vectorResults.Count; rank++)
                {
                    int idx = vectorResults[rank].Index;
                    float rrfScore = 1f / (RRF_K + rank + 1);

                    if (rrfScores.ContainsKey(idx))
                        rrfScores[idx] += rrfScore; // Both systems found it → boost
                    else
                        rrfScores[idx] = rrfScore;

                    if (!entryMap.ContainsKey(idx))
                        entryMap[idx] = vectorResults[rank].Entry;
                }
            }

            // Sort by combined score
            var sorted = rrfScores.OrderByDescending(kvp => kvp.Value).Take(topK);

            var results = new List<SearchResult>();
            foreach (var kvp in sorted)
            {
                results.Add(new SearchResult
                {
                    Index = kvp.Key,
                    Score = kvp.Value,
                    Entry = entryMap[kvp.Key]
                });
            }

            return results;
        }
    }
}
