using System;
using System.Collections.Generic;
using System.IO;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CompanionAI_v3.Diagnostics
{
    /// <summary>
    /// ★ v3.48.0: Tactical Narrator 대사 데이터베이스
    /// JSON 파일 로드 + 3-Layer 대사 선택 (no-repeat) + placeholder 치환
    /// </summary>
    public static class TacticalDialogueDB
    {
        #region Data Structure

        // situation[category] = string[]
        private static Dictionary<string, string[]> _situation = new Dictionary<string, string[]>();
        // plan[category] = string[]
        private static Dictionary<string, string[]> _plan = new Dictionary<string, string[]>();
        // personality[companionKey][category] = string[]
        private static Dictionary<string, Dictionary<string, string[]>> _personality
            = new Dictionary<string, Dictionary<string, string[]>>();

        private static bool _isLoaded;
        private static string _modPath;

        #endregion

        #region No-Repeat History

        // 최근 사용 인덱스 추적 (반복 방지)
        private static readonly Dictionary<string, int> _lastUsedIndex = new Dictionary<string, int>();
        private static readonly System.Random _rng = new System.Random();

        #endregion

        #region Loading

        /// <summary>언어 변경 시 재로드 (modPath 캐시 사용)</summary>
        public static void ReloadFromJson()
        {
            if (!string.IsNullOrEmpty(_modPath))
                LoadFromJson(_modPath);
        }

        /// <summary>JSON 파일에서 대사 데이터 로드</summary>
        public static void LoadFromJson(string modPath)
        {
            _modPath = modPath;
            _isLoaded = false;
            _situation.Clear();
            _plan.Clear();
            _personality.Clear();

            var lang = ModSettings.Instance?.UILanguage ?? Language.English;
            string langCode = lang == Language.Korean ? "ko" :
                              lang == Language.Japanese ? "ja" :
                              lang == Language.Russian ? "ru" :
                              lang == Language.Chinese ? "zh" : "en";

            string filePath = Path.Combine(modPath, "Dialogue", $"tactical_{langCode}.json");

            // 해당 언어 파일 없으면 영어 폴백
            if (!File.Exists(filePath))
            {
                filePath = Path.Combine(modPath, "Dialogue", "tactical_en.json");
            }

            if (!File.Exists(filePath))
            {
                Main.Log("[TacticalDialogueDB] No dialogue files found, using hardcoded fallback");
                LoadHardcodedFallback();
                return;
            }

            try
            {
                string json = File.ReadAllText(filePath);
                var root = JObject.Parse(json);

                // situation
                if (root["situation"] is JObject sitObj)
                {
                    foreach (var prop in sitObj.Properties())
                    {
                        _situation[prop.Name] = prop.Value.ToObject<string[]>();
                    }
                }

                // plan
                if (root["plan"] is JObject planObj)
                {
                    foreach (var prop in planObj.Properties())
                    {
                        _plan[prop.Name] = prop.Value.ToObject<string[]>();
                    }
                }

                // personality
                if (root["personality"] is JObject persObj)
                {
                    foreach (var compProp in persObj.Properties())
                    {
                        var catDict = new Dictionary<string, string[]>();
                        if (compProp.Value is JObject catObj)
                        {
                            foreach (var catProp in catObj.Properties())
                            {
                                catDict[catProp.Name] = catProp.Value.ToObject<string[]>();
                            }
                        }
                        _personality[compProp.Name] = catDict;
                    }
                }

                _isLoaded = true;
                Main.Log($"[TacticalDialogueDB] Loaded from {filePath}: " +
                    $"sit={_situation.Count} plan={_plan.Count} personality={_personality.Count} companions");
            }
            catch (Exception ex)
            {
                Main.LogError($"[TacticalDialogueDB] Failed to load {filePath}: {ex.Message}");
                LoadHardcodedFallback();
            }
        }

        #endregion

        #region Line Selection

        /// <summary>
        /// 카테고리 + 동료에 맞는 2~3줄 대사 조합
        /// 조합 패턴: [Situation + Plan + Personality], [Situation + Personality], [Plan + Personality]
        /// </summary>
        public static string[] GetLines(TacticalNarrator.SpeechCategory category, string companionKey, Situation situation)
        {
            if (!_isLoaded) LoadHardcodedFallback();

            string catKey = category.ToString();

            string sitLine = PickRandom("sit_" + catKey, GetArray(_situation, catKey));
            string planLine = PickRandom("plan_" + catKey, GetArray(_plan, catKey));
            string persLine = PickRandom("pers_" + companionKey + "_" + catKey,
                GetPersonalityArray(companionKey, catKey));

            // placeholder 치환
            sitLine = ReplacePlaceholders(sitLine, situation);
            planLine = ReplacePlaceholders(planLine, situation);
            persLine = ReplacePlaceholders(persLine, situation);

            // 조합 패턴 랜덤 (다양성)
            int pattern = _rng.Next(3);
            switch (pattern)
            {
                case 0: // 3줄: Situation + Plan + Personality
                    if (!string.IsNullOrEmpty(sitLine) && !string.IsNullOrEmpty(planLine) && !string.IsNullOrEmpty(persLine))
                        return new[] { sitLine, planLine, persLine };
                    break;
                case 1: // 2줄: Situation + Personality
                    if (!string.IsNullOrEmpty(sitLine) && !string.IsNullOrEmpty(persLine))
                        return new[] { sitLine, persLine };
                    break;
                case 2: // 2줄: Plan + Personality
                    if (!string.IsNullOrEmpty(planLine) && !string.IsNullOrEmpty(persLine))
                        return new[] { planLine, persLine };
                    break;
            }

            // 폴백: 있는 것들 모아서
            var result = new List<string>(3);
            if (!string.IsNullOrEmpty(sitLine)) result.Add(sitLine);
            if (!string.IsNullOrEmpty(planLine)) result.Add(planLine);
            if (!string.IsNullOrEmpty(persLine)) result.Add(persLine);

            return result.Count > 0 ? result.ToArray() : null;
        }

        /// <summary>no-repeat 랜덤 선택</summary>
        private static string PickRandom(string historyKey, string[] options)
        {
            if (options == null || options.Length == 0) return null;
            if (options.Length == 1) return options[0];

            int lastIdx = -1;
            _lastUsedIndex.TryGetValue(historyKey, out lastIdx);

            int idx;
            int attempts = 0;
            do
            {
                idx = _rng.Next(options.Length);
                attempts++;
            } while (idx == lastIdx && attempts < 5);

            _lastUsedIndex[historyKey] = idx;
            return options[idx];
        }

        /// <summary>반복 히스토리 초기화 (전투 종료 시)</summary>
        public static void ResetHistory()
        {
            _lastUsedIndex.Clear();
        }

        #endregion

        #region Placeholder Substitution

        private static string ReplacePlaceholders(string text, Situation situation)
        {
            if (string.IsNullOrEmpty(text) || situation == null) return text;

            // {target} — 최적 타겟 이름
            if (text.Contains("{target}"))
            {
                string targetName = situation.BestTarget?.CharacterName ?? "???";
                text = text.Replace("{target}", targetName);
            }

            // {enemyCount} — 적 수
            if (text.Contains("{enemyCount}"))
            {
                text = text.Replace("{enemyCount}", situation.Enemies.Count.ToString());
            }

            // {hp} — 본인 HP%
            if (text.Contains("{hp}"))
            {
                text = text.Replace("{hp}", $"{situation.HPPercent:F0}");
            }

            // {ally} — HP가 가장 낮은 아군
            if (text.Contains("{ally}"))
            {
                string allyName = FindLowestHPAlly(situation);
                text = text.Replace("{ally}", allyName);
            }

            // {hittable} — 공격 가능 적 수
            if (text.Contains("{hittable}"))
            {
                text = text.Replace("{hittable}", situation.HittableEnemies.Count.ToString());
            }

            return text;
        }

        private static string FindLowestHPAlly(Situation situation)
        {
            if (situation.Allies == null || situation.Allies.Count == 0) return "???";

            float lowestHP = float.MaxValue;
            string lowestName = "???";

            foreach (var ally in situation.Allies)
            {
                if (ally == null || ally == situation.Unit) continue;
                float hp = GameInterface.CombatAPI.GetHPPercent(ally);
                if (hp < lowestHP)
                {
                    lowestHP = hp;
                    lowestName = ally.CharacterName;
                }
            }
            return lowestName;
        }

        #endregion

        #region Helpers

        private static string[] GetArray(Dictionary<string, string[]> dict, string key)
        {
            return dict.TryGetValue(key, out var arr) ? arr : null;
        }

        private static string[] GetPersonalityArray(string companionKey, string catKey)
        {
            if (_personality.TryGetValue(companionKey, out var catDict))
            {
                if (catDict.TryGetValue(catKey, out var arr))
                    return arr;
            }

            // Unknown 폴백
            if (companionKey != "Unknown" && _personality.TryGetValue("Unknown", out var unknownDict))
            {
                if (unknownDict.TryGetValue(catKey, out var arr))
                    return arr;
            }

            return null;
        }

        #endregion

        #region Hardcoded Fallback

        /// <summary>JSON 파일 없을 때 최소 영어 기본 대사</summary>
        private static void LoadHardcodedFallback()
        {
            _situation = new Dictionary<string, string[]>
            {
                ["Emergency"] = new[] { "Critical situation — wounded allies detected.", "This is dire. Medical attention needed." },
                ["Retreat"] = new[] { "Enemy closing in — tactical withdrawal advised.", "Too close for comfort." },
                ["KillTarget"] = new[] { "{target} is weakened — finishing blow possible.", "Enemy {target} is vulnerable." },
                ["Attack"] = new[] { "{enemyCount} hostiles in sight.", "Engaging {hittable} targets." },
                ["AoE"] = new[] { "Enemy cluster detected — area attack opportunity.", "Multiple targets grouped together." },
                ["Support"] = new[] { "Allies need support.", "Providing tactical assistance." },
                ["EndTurn"] = new[] { "No viable actions remaining.", "Holding position." }
            };

            _plan = new Dictionary<string, string[]>
            {
                ["Emergency"] = new[] { "Prioritizing emergency healing.", "Treating wounds first." },
                ["Retreat"] = new[] { "Falling back to safe distance.", "Repositioning for safety." },
                ["KillTarget"] = new[] { "Moving to eliminate {target}.", "Finishing off {target} this turn." },
                ["Attack"] = new[] { "Engaging the enemy.", "Pressing the attack." },
                ["AoE"] = new[] { "Deploying area attack.", "Targeting the cluster." },
                ["Support"] = new[] { "Supporting the team.", "Buffing allies for combat." },
                ["EndTurn"] = new[] { "Nothing more to do.", "Standing by." }
            };

            _personality = new Dictionary<string, Dictionary<string, string[]>>
            {
                ["Unknown"] = new Dictionary<string, string[]>
                {
                    ["Emergency"] = new[] { "We must endure.", "Stay focused." },
                    ["Retreat"] = new[] { "A tactical retreat, nothing more.", "Living to fight another day." },
                    ["KillTarget"] = new[] { "This one falls now.", "No mercy." },
                    ["Attack"] = new[] { "For the Emperor!", "Into the fray!" },
                    ["AoE"] = new[] { "They won't know what hit them.", "Cleansing fire!" },
                    ["Support"] = new[] { "We stand together.", "I've got your back." },
                    ["EndTurn"] = new[] { "Patience...", "Waiting for the right moment." }
                }
            };

            _isLoaded = true;
        }

        #endregion
    }
}
