// MachineSpirit/Knowledge/VectorSearch.cs
// ★ v3.70.0: Vector embedding search via Ollama nomic-embed-text
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json.Linq;

namespace CompanionAI_v3.MachineSpirit.Knowledge
{
    public static class VectorSearch
    {
        private static bool _isReady;
        private static bool _isComputing;
        private static int _computedCount;

        public static bool IsReady => _isReady;
        public static bool IsComputing => _isComputing;
        public static int ComputedCount => _computedCount;

        /// <summary>
        /// Compute embeddings for all entries via Ollama /api/embed.
        /// Runs as background coroutine, 5 entries per batch.
        /// </summary>
        public static IEnumerator ComputeEmbeddings(List<KnowledgeEntry> entries)
        {
            if (_isComputing) yield break;
            _isComputing = true;
            _computedCount = 0;

            Main.LogDebug($"[VectorSearch] Starting embedding computation for {entries.Count} entries...");

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.Embedding != null) { _computedCount++; continue; } // already computed

                string textToEmbed = (entry.Title ?? "") + " " + (entry.Text ?? "");
                if (textToEmbed.Length > 2000) textToEmbed = textToEmbed.Substring(0, 2000);

                float[] embedding = null;
                bool done = false;

                CoroutineRunner.Start(GetEmbedding(textToEmbed,
                    result => { embedding = result; done = true; },
                    error => { done = true; } // Skip on error
                ));

                // Wait for embedding to complete
                while (!done) yield return null;

                if (embedding != null)
                {
                    entry.Embedding = embedding;
                    _computedCount++;
                }

                // Yield every 5 to avoid blocking
                if (i % 5 == 0) yield return null;
            }

            _isReady = true;
            _isComputing = false;
            Main.LogDebug($"[VectorSearch] Embedding complete: {_computedCount}/{entries.Count} entries");
        }

        /// <summary>
        /// Get embedding for a single text via Ollama /api/embed.
        /// </summary>
        public static IEnumerator GetEmbedding(string text, Action<float[]> onResult, Action<string> onError)
        {
            string payload = $"{{\"model\":\"nomic-embed-text\",\"input\":\"{EscapeJson(text)}\"}}";
            var request = new UnityWebRequest("http://localhost:11434/api/embed", "POST");
            request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(payload));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = 30;

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    var json = JObject.Parse(request.downloadHandler.text);
                    var embeddingsArray = json["embeddings"]?[0];
                    if (embeddingsArray != null)
                    {
                        var values = embeddingsArray.ToObject<float[]>();
                        onResult?.Invoke(values);
                    }
                    else
                    {
                        onError?.Invoke("No embeddings in response");
                    }
                }
                catch (Exception ex)
                {
                    onError?.Invoke(ex.Message);
                }
            }
            else
            {
                onError?.Invoke(request.error);
            }

            request.Dispose();
        }

        /// <summary>
        /// Search entries by cosine similarity (dot product for L2-normalized vectors).
        /// </summary>
        public static List<SearchResult> Search(List<KnowledgeEntry> entries, float[] queryEmbedding, int topK = 20)
        {
            if (queryEmbedding == null || entries == null) return new List<SearchResult>();

            var results = new List<SearchResult>();

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.Embedding == null) continue;

                float score = DotProduct(queryEmbedding, entry.Embedding);
                if (score > 0.3f) // minimum threshold
                {
                    results.Add(new SearchResult { Index = i, Score = score, Entry = entry });
                }
            }

            results.Sort((a, b) => b.Score.CompareTo(a.Score));
            if (results.Count > topK) results.RemoveRange(topK, results.Count - topK);
            return results;
        }

        private static float DotProduct(float[] a, float[] b)
        {
            if (a.Length != b.Length) return 0f;
            float sum = 0f;
            for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
            return sum;
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }
}
