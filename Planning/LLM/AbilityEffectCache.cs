// Planning/LLM/AbilityEffectCache.cs
// ★ Skill Effect Awareness: GUID → effect label 빠른 조회 캐시
// 게임 로드 시 1회 빌드, tactical_skill_cache.json에 저장.
// CompactBattlefieldEncoder가 매 LLM 호출마다 O(1) 조회로 사용.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using CompanionAI_v3.Data;

namespace CompanionAI_v3.Planning.LLM
{
    /// <summary>
    /// GUID → 효과 라벨 매핑 캐시.
    /// 게임 로드 시 1회 빌드되며 디스크에 저장됨 (재시작 시 즉시 로드).
    /// 캐시 미스 시 빈 문자열 반환 (안전 폴백).
    /// </summary>
    public static class AbilityEffectCache
    {
        private const string FILENAME = "tactical_skill_cache.json";
        private const int SCHEMA_VERSION = 1;

        private static readonly Dictionary<string, string> _labels = new Dictionary<string, string>();
        private static bool _initialized;
        private static string _filePath;

        /// <summary>저장된 라벨 수 (진단용)</summary>
        public static int LabelCount => _labels.Count;

        /// <summary>초기화 완료 여부</summary>
        public static bool IsReady => _initialized;

        /// <summary>
        /// 게임 로드 시 1회 호출. 비동기 코루틴.
        /// 1. tactical_skill_cache.json 존재 시 → 즉시 로드
        /// 2. 없으면 → AbilityDatabase 순회 + 저장
        /// </summary>
        public static IEnumerator Initialize(string modPath)
        {
            if (_initialized) yield break;

            _filePath = Path.Combine(modPath, FILENAME);

            // 1. 디스크 캐시 로드 시도
            if (TryLoadFromDisk())
            {
                _initialized = true;
                Main.Log($"[AbilityEffectCache] Loaded {_labels.Count} labels from disk");
                yield break;
            }

            // 2. AbilityDatabase에서 캐시 빌드
            Main.LogDebug("[AbilityEffectCache] No cache file — building from AbilityDatabase");
            BuildFromDatabase();

            // 3. 디스크 저장
            SaveToDisk();

            _initialized = true;
            Main.Log($"[AbilityEffectCache] Built {_labels.Count} labels");
        }

        /// <summary>
        /// O(1) 조회. 캐시 미스 시 빈 문자열 반환.
        /// </summary>
        public static string GetLabel(string abilityGuid)
        {
            if (string.IsNullOrEmpty(abilityGuid)) return "";
            return _labels.TryGetValue(abilityGuid, out string label) ? label : "";
        }

        /// <summary>전체 캐시 클리어 (디버그용)</summary>
        public static void Clear()
        {
            _labels.Clear();
            _initialized = false;
        }

        // ═══════════════════════════════════════════════════════════
        // Internal: build / load / save
        // ═══════════════════════════════════════════════════════════

        private static void BuildFromDatabase()
        {
            int extracted = 0;
            int skipped = 0;

            foreach (var info in AbilityDatabase.GetAllInfos())
            {
                if (info == null || string.IsNullOrEmpty(info.Guid)) { skipped++; continue; }

                string label = AbilityEffectExtractor.ExtractFromInfo(info);
                if (!string.IsNullOrEmpty(label))
                {
                    _labels[info.Guid] = label;
                    extracted++;
                }
                else
                {
                    skipped++;
                }
            }

            Main.LogDebug($"[AbilityEffectCache] BuildFromDatabase: extracted={extracted}, skipped={skipped}");
        }

        private static bool TryLoadFromDisk()
        {
            if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath))
                return false;

            try
            {
                string json = File.ReadAllText(_filePath);
                var wrapper = JsonConvert.DeserializeObject<CacheFile>(json);

                if (wrapper == null || wrapper.SchemaVersion != SCHEMA_VERSION)
                {
                    Main.LogDebug("[AbilityEffectCache] Cache schema mismatch — rebuilding");
                    return false;
                }

                if (wrapper.Labels != null)
                {
                    _labels.Clear();
                    foreach (var kvp in wrapper.Labels)
                        _labels[kvp.Key] = kvp.Value;
                    return true;
                }

                Main.LogDebug("[AbilityEffectCache] Cache has null labels — rebuilding");
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[AbilityEffectCache] Load failed: {ex.Message}");
            }

            return false;
        }

        private static void SaveToDisk()
        {
            if (string.IsNullOrEmpty(_filePath)) return;

            try
            {
                var wrapper = new CacheFile
                {
                    SchemaVersion = SCHEMA_VERSION,
                    Labels = new Dictionary<string, string>(_labels)
                };
                string json = JsonConvert.SerializeObject(wrapper, Formatting.Indented);
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[AbilityEffectCache] Save failed: {ex.Message}");
            }
        }

        /// <summary>JSON 직렬화용 wrapper</summary>
        private class CacheFile
        {
            [JsonProperty("schemaVersion")]
            public int SchemaVersion { get; set; }

            [JsonProperty("labels")]
            public Dictionary<string, string> Labels { get; set; }
        }
    }
}
