// Planning/LLM/TacticalMemory.cs
// ★ Cross-Combat Tactical Memory — 전투 간 전술 기억 시스템.
// Bannerlord AI Influence의 100일 장기기억 캐싱에서 영감.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Kingmaker.EntitySystem.Entities;
using Newtonsoft.Json;

namespace CompanionAI_v3.Planning.LLM
{
    /// <summary>
    /// 전투 결과 기록 엔트리. 적 구성 → 사용 가중치 → 결과.
    /// </summary>
    public class TacticalMemoryEntry
    {
        [JsonProperty("hash")]
        public long EnemyCompositionHash;

        [JsonProperty("desc")]
        public string EnemyDescription;

        [JsonProperty("aoe")]
        public float AoEWeight;

        [JsonProperty("focus")]
        public float FocusFire;

        [JsonProperty("target")]
        public int PriorityTarget;

        [JsonProperty("heal")]
        public float HealPriority;

        [JsonProperty("buff")]
        public float BuffPriority;

        [JsonProperty("defensive")]
        public bool DefensiveStance;

        [JsonProperty("outcome")]
        public string Outcome;

        [JsonProperty("rounds")]
        public int Rounds;

        [JsonProperty("teamHP")]
        public float FinalTeamHP;

        [JsonProperty("time")]
        public string Timestamp;

        /// <summary>ScorerWeights에서 가중치 필드 설정</summary>
        public void SetFromWeights(ScorerWeights w)
        {
            if (w == null) return;
            AoEWeight = w.AoEWeight;
            FocusFire = w.FocusFire;
            PriorityTarget = w.PriorityTarget;
            HealPriority = w.HealPriority;
            BuffPriority = w.BuffPriority;
            DefensiveStance = w.DefensiveStance;
        }
    }

    /// <summary>
    /// ★ Cross-Combat Tactical Memory.
    /// 전투 종료 시 결과 기록, 전투 시작 시 유사 적 구성 회상.
    /// 파일: [ModPath]/tactical_memory.json (최대 50건, FIFO).
    /// </summary>
    public static class TacticalMemory
    {
        private static readonly List<TacticalMemoryEntry> _entries = new List<TacticalMemoryEntry>(50);
        private const int MAX_ENTRIES = 50;
        private const string FILENAME = "tactical_memory.json";
        private static string _filePath;
        private static bool _initialized;

        /// <summary>저장된 기억 수</summary>
        public static int EntryCount => _entries.Count;

        // ═══════════════════════════════════════════════════════════
        // 초기화 / I/O
        // ═══════════════════════════════════════════════════════════

        /// <summary>파일 경로 설정 + 디스크에서 로드</summary>
        public static void Initialize(string modPath)
        {
            if (_initialized) return;

            _filePath = Path.Combine(modPath, FILENAME);
            _initialized = true;

            try
            {
                if (File.Exists(_filePath))
                {
                    string json = File.ReadAllText(_filePath);
                    var loaded = JsonConvert.DeserializeObject<List<TacticalMemoryEntry>>(json);
                    if (loaded != null)
                    {
                        _entries.Clear();
                        _entries.AddRange(loaded);
                        Main.Log($"[TacticalMemory] Loaded {_entries.Count} entries from disk");
                    }
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[TacticalMemory] Load failed: {ex.Message}");
            }
        }

        /// <summary>디스크에 저장</summary>
        private static void SaveToFile()
        {
            if (string.IsNullOrEmpty(_filePath)) return;
            try
            {
                string json = JsonConvert.SerializeObject(_entries, Formatting.Indented);
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[TacticalMemory] Save failed: {ex.Message}");
            }
        }

        /// <summary>전체 기억 초기화</summary>
        public static void Clear()
        {
            _entries.Clear();
        }

        // ═══════════════════════════════════════════════════════════
        // 적 구성 해싱
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 적 이름별 수량 집계 (정렬됨). ComputeEnemyHash + BuildEnemyDescription 공용.
        /// includeDead=true: 전투 종료 시 메모리 기록용 (사망한 적도 포함)
        /// includeDead=false: 전투 중 회상/비교용 (살아있는 적만)
        /// </summary>
        private static SortedDictionary<string, int> CountEnemies(List<BaseUnitEntity> enemies, bool includeDead = false)
        {
            var counts = new SortedDictionary<string, int>();
            if (enemies == null) return counts;
            for (int i = 0; i < enemies.Count; i++)
            {
                if (enemies[i] == null) continue;
                if (!includeDead && enemies[i].LifeState.IsDead) continue;
                string name = enemies[i].CharacterName ?? "Unknown";
                if (counts.ContainsKey(name))
                    counts[name]++;
                else
                    counts[name] = 1;
            }
            return counts;
        }

        /// <summary>세션 간 결정론적 문자열 해시 (DJB2 변형)</summary>
        private static long StableStringHash(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            long hash = 5381;
            for (int i = 0; i < s.Length; i++)
                hash = ((hash << 5) + hash) ^ s[i];
            return hash;
        }

        /// <summary>
        /// 적 이름+수량 기반 안정 해시. 정렬 후 해싱.
        /// 예: {Cultist:2, Heavy:1, Psyker:1} → 고유 long
        /// </summary>
        public static long ComputeEnemyHash(List<BaseUnitEntity> enemies, bool includeDead = false)
        {
            if (enemies == null || enemies.Count == 0) return 0;
            var counts = CountEnemies(enemies, includeDead);

            long hash = 17;
            foreach (var kvp in counts)
            {
                hash = hash * 31 + StableStringHash(kvp.Key);
                hash = hash * 31 + kvp.Value;
            }
            return hash;
        }

        /// <summary>적 구성 설명 문자열. 예: "2xCultist,1xPsyker,1xHeavy"</summary>
        public static string BuildEnemyDescription(List<BaseUnitEntity> enemies, bool includeDead = false)
        {
            if (enemies == null || enemies.Count == 0) return "none";
            var counts = CountEnemies(enemies, includeDead);

            var sb = new StringBuilder(64);
            bool first = true;
            foreach (var kvp in counts)
            {
                if (!first) sb.Append(',');
                sb.Append(kvp.Value).Append('x').Append(kvp.Key);
                first = false;
            }
            return sb.ToString();
        }

        // ═══════════════════════════════════════════════════════════
        // 기록 (전투 종료 시)
        // ═══════════════════════════════════════════════════════════

        /// <summary>전투 결과 기록</summary>
        public static void RecordCombatEnd(
            List<BaseUnitEntity> enemies,
            ScorerWeights dominantWeights,
            bool isVictory,
            int rounds,
            float finalTeamHP)
        {
            if (!_initialized) return;
            if (enemies == null || enemies.Count == 0) return;
            if (dominantWeights == null || dominantWeights.IsDefault) return;

            var entry = new TacticalMemoryEntry
            {
                EnemyCompositionHash = ComputeEnemyHash(enemies, includeDead: true),
                EnemyDescription = BuildEnemyDescription(enemies, includeDead: true),
                Outcome = isVictory ? "win" : "loss",
                Rounds = rounds,
                FinalTeamHP = finalTeamHP,
                Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
            };
            entry.SetFromWeights(dominantWeights);

            // FIFO: 최대 50건 초과 시 오래된 것 제거
            while (_entries.Count >= MAX_ENTRIES)
                _entries.RemoveAt(0);

            _entries.Add(entry);

            Main.Log($"[TacticalMemory] Recorded: {entry.EnemyDescription}, {entry.Outcome}, " +
                $"rounds={rounds}, teamHP={finalTeamHP:F0}%, entries={_entries.Count}");

            SaveToFile();
        }

        // ═══════════════════════════════════════════════════════════
        // 회상 (전투 시작 시)
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 유사 적 구성에 대한 기억 회상.
        /// 1순위: 정확 해시 매치.
        /// 2순위: 동일 적 유형 부분 매치.
        /// </summary>
        public static List<TacticalMemoryEntry> Recall(List<BaseUnitEntity> enemies, int maxResults = 2)
        {
            var results = new List<TacticalMemoryEntry>(maxResults);
            if (!_initialized || _entries.Count == 0 || enemies == null) return results;

            // includeDead=true: 기록 시 사망 포함 해시와 매칭하기 위해
            long targetHash = ComputeEnemyHash(enemies, includeDead: true);

            // 1. 정확 해시 매치 (최신 우선)
            for (int i = _entries.Count - 1; i >= 0 && results.Count < maxResults; i--)
            {
                if (_entries[i].EnemyCompositionHash == targetHash)
                    results.Add(_entries[i]);
            }

            if (results.Count >= maxResults) return results;

            // 2. 부분 매치: 적 이름이 겹치는 기록
            string currentDesc = BuildEnemyDescription(enemies);
            var currentNames = new HashSet<string>();
            for (int i = 0; i < enemies.Count; i++)
            {
                if (enemies[i] != null && !enemies[i].LifeState.IsDead)
                    currentNames.Add(enemies[i].CharacterName ?? "Unknown");
            }

            for (int i = _entries.Count - 1; i >= 0 && results.Count < maxResults; i--)
            {
                if (_entries[i].EnemyCompositionHash == targetHash) continue; // 이미 추가됨

                // 설명에 현재 적 이름이 하나라도 포함되면 부분 매치
                string desc = _entries[i].EnemyDescription;
                foreach (var name in currentNames)
                {
                    if (desc.Contains(name))
                    {
                        results.Add(_entries[i]);
                        break;
                    }
                }
            }

            return results;
        }

        // ═══════════════════════════════════════════════════════════
        // 프롬프트 포맷팅
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 기억을 프롬프트 주입용 문자열로 변환.
        /// 예: "PAST: vs 2xCultist,1xPsyker, focus_fire=2.0 effective (3 rounds, won 75%HP)"
        /// </summary>
        public static string FormatForPrompt(List<TacticalMemoryEntry> memories)
        {
            if (memories == null || memories.Count == 0) return null;

            var sb = new StringBuilder(128);
            for (int i = 0; i < memories.Count; i++)
            {
                var m = memories[i];
                sb.Append("PAST: vs ").Append(m.EnemyDescription);

                // 가장 변경된 가중치만 표시
                bool hasWeight = false;
                if (m.FocusFire != 1f) { sb.Append(", focus=").Append(m.FocusFire.ToString("F1")); hasWeight = true; }
                if (m.AoEWeight != 1f) { sb.Append(", aoe=").Append(m.AoEWeight.ToString("F1")); hasWeight = true; }
                if (m.PriorityTarget >= 0) { sb.Append(", target=").Append(m.PriorityTarget); hasWeight = true; }
                if (m.DefensiveStance) { sb.Append(", defensive"); hasWeight = true; }

                if (!hasWeight) sb.Append(", default weights");

                sb.Append(" (").Append(m.Rounds).Append(" rounds, ");
                sb.Append(m.Outcome).Append(' ').Append((int)m.FinalTeamHP).Append("%HP)");

                if (i < memories.Count - 1) sb.Append('\n');
            }

            return sb.ToString();
        }
    }
}
