using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using CompanionAI_v3.Settings;
using static CompanionAI_v3.Data.CompanionDialogue;

namespace CompanionAI_v3.Data
{
    /// <summary>
    /// ★ v3.9.32: 다국어 대사 데이터베이스
    /// ★ v3.9.34: JSON 외부화 — {ModPath}/Dialogue/ 폴더에서 로드, 하드코딩은 fallback
    ///
    /// 번역 원칙:
    /// - 40K 고유명사 원문 유지 (Fenris, Omnissiah, Lex Imperialis, mon-keigh 등)
    /// - [target], [ally] 플레이스홀더 그대로 유지
    /// - 캐릭터별 어투/경어 수준 일관 유지
    ///
    /// JSON 파일 위치: {ModPath}/Dialogue/dialogue_en.json, dialogue_ko.json, dialogue_ru.json, dialogue_ja.json
    /// JSON 구조: { "Abelard": { "Attack": ["line1", "line2"], ... }, ... }
    /// </summary>
    public static class DialogueLocalization
    {
        private const string DialogueFolderName = "Dialogue";

        // ═══ JSON 로드 데이터 (null이면 하드코딩 fallback 사용) ═══
        private static Dictionary<CompanionId, Dictionary<SpeechCategory, string[]>> _loadedEnglish;
        private static Dictionary<CompanionId, Dictionary<SpeechCategory, string[]>> _loadedKorean;
        private static Dictionary<CompanionId, Dictionary<SpeechCategory, string[]>> _loadedRussian;
        private static Dictionary<CompanionId, Dictionary<SpeechCategory, string[]>> _loadedJapanese;

        private static readonly Dictionary<Language, string> LanguageFileNames = new Dictionary<Language, string>
        {
            [Language.English]  = "dialogue_en.json",
            [Language.Korean]   = "dialogue_ko.json",
            [Language.Russian]  = "dialogue_ru.json",
            [Language.Japanese] = "dialogue_ja.json",
        };

        /// <summary>현재 언어 설정에 맞는 대사 DB 반환 (JSON 우선, 하드코딩 fallback)</summary>
        public static Dictionary<CompanionId, Dictionary<SpeechCategory, string[]>> GetDatabase(Language lang)
        {
            switch (lang)
            {
                case Language.Korean:   return _loadedKorean   ?? KoreanDialogue;
                case Language.Russian:  return _loadedRussian  ?? RussianDialogue;
                case Language.Japanese: return _loadedJapanese ?? JapaneseDialogue;
                default:                return _loadedEnglish  ?? EnglishDialogue;
            }
        }

        #region ═══ JSON Load / Export ═══

        /// <summary>Dialogue 폴더 경로 반환 (없으면 생성)</summary>
        private static string GetDialogueFolder(string modPath)
        {
            string folder = Path.Combine(modPath, DialogueFolderName);
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>
        /// ★ v3.9.34: 모드 초기화 시 호출 — JSON 대사 파일 로드
        /// 파일이 없으면 하드코딩 기본값을 JSON으로 내보내기
        /// </summary>
        public static void LoadFromJson(string modPath)
        {
            if (string.IsNullOrEmpty(modPath)) return;

            string dialogueFolder = GetDialogueFolder(modPath);
            int loaded = 0;
            int exported = 0;

            foreach (var kvp in LanguageFileNames)
            {
                var lang = kvp.Key;
                var fileName = kvp.Value;
                string filePath = Path.Combine(dialogueFolder, fileName);

                if (File.Exists(filePath))
                {
                    var db = TryLoadFile(filePath, lang);
                    if (db != null)
                    {
                        SetLoadedDatabase(lang, db);
                        loaded++;
                    }
                }
                else
                {
                    // JSON 파일 없으면 하드코딩 기본값으로 생성
                    ExportLanguage(dialogueFolder, lang);
                    exported++;
                }
            }

            Main.Log($"[Dialogue] JSON init complete: {loaded} loaded, {exported} exported ({dialogueFolder})");
        }

        /// <summary>JSON 파일 하나 로드 → Dictionary 변환</summary>
        private static Dictionary<CompanionId, Dictionary<SpeechCategory, string[]>> TryLoadFile(string filePath, Language lang)
        {
            try
            {
                string json = File.ReadAllText(filePath);
                var raw = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string[]>>>(json);
                if (raw == null || raw.Count == 0)
                {
                    Main.LogError($"[Dialogue] Empty or invalid JSON: {filePath}");
                    return null;
                }

                var result = new Dictionary<CompanionId, Dictionary<SpeechCategory, string[]>>();
                int skipped = 0;

                foreach (var companionKvp in raw)
                {
                    if (!Enum.TryParse(companionKvp.Key, true, out CompanionId companionId))
                    {
                        Main.LogDebug($"[Dialogue] Unknown companion key: \"{companionKvp.Key}\" in {Path.GetFileName(filePath)}");
                        skipped++;
                        continue;
                    }

                    var categories = new Dictionary<SpeechCategory, string[]>();
                    foreach (var catKvp in companionKvp.Value)
                    {
                        if (!Enum.TryParse(catKvp.Key, true, out SpeechCategory category))
                        {
                            Main.LogDebug($"[Dialogue] Unknown category key: \"{catKvp.Key}\" for {companionKvp.Key} in {Path.GetFileName(filePath)}");
                            skipped++;
                            continue;
                        }

                        if (catKvp.Value != null && catKvp.Value.Length > 0)
                            categories[category] = catKvp.Value;
                    }

                    if (categories.Count > 0)
                        result[companionId] = categories;
                }

                Main.Log($"[Dialogue] Loaded {Path.GetFileName(filePath)}: {result.Count} companions" +
                         (skipped > 0 ? $" ({skipped} skipped entries)" : ""));
                return result;
            }
            catch (Exception ex)
            {
                Main.LogError($"[Dialogue] Failed to load {filePath}: {ex.Message}");
                return null;
            }
        }

        /// <summary>로드된 DB를 언어별 필드에 저장</summary>
        private static void SetLoadedDatabase(Language lang, Dictionary<CompanionId, Dictionary<SpeechCategory, string[]>> db)
        {
            switch (lang)
            {
                case Language.English:  _loadedEnglish  = db; break;
                case Language.Korean:   _loadedKorean   = db; break;
                case Language.Russian:  _loadedRussian  = db; break;
                case Language.Japanese: _loadedJapanese = db; break;
            }
        }

        /// <summary>특정 언어의 하드코딩 기본값을 JSON 파일로 내보내기</summary>
        private static void ExportLanguage(string dialogueFolder, Language lang)
        {
            try
            {
                // 하드코딩 기본값 가져오기
                Dictionary<CompanionId, Dictionary<SpeechCategory, string[]>> hardcoded;
                switch (lang)
                {
                    case Language.Korean:   hardcoded = KoreanDialogue;   break;
                    case Language.Russian:  hardcoded = RussianDialogue;  break;
                    case Language.Japanese: hardcoded = JapaneseDialogue; break;
                    default:               hardcoded = EnglishDialogue;   break;
                }

                // enum → string 키로 변환 (사람이 읽기 쉽도록)
                var serializable = new Dictionary<string, Dictionary<string, string[]>>();
                foreach (var companionKvp in hardcoded)
                {
                    var categories = new Dictionary<string, string[]>();
                    foreach (var catKvp in companionKvp.Value)
                    {
                        categories[catKvp.Key.ToString()] = catKvp.Value;
                    }
                    serializable[companionKvp.Key.ToString()] = categories;
                }

                string fileName = LanguageFileNames[lang];
                string filePath = Path.Combine(dialogueFolder, fileName);
                string json = JsonConvert.SerializeObject(serializable, Formatting.Indented);
                File.WriteAllText(filePath, json);

                Main.LogDebug($"[Dialogue] Exported default {fileName}");
            }
            catch (Exception ex)
            {
                Main.LogError($"[Dialogue] Failed to export {lang}: {ex.Message}");
            }
        }

        /// <summary>
        /// ★ v3.9.34: JSON에서 다시 로드 (런타임 리로드)
        /// </summary>
        public static void ReloadFromJson()
        {
            string modPath = Main.ModPath;
            if (string.IsNullOrEmpty(modPath)) return;

            string dialogueFolder = Path.Combine(modPath, DialogueFolderName);
            if (!Directory.Exists(dialogueFolder)) return;

            // 기존 로드 데이터 초기화
            _loadedEnglish = null;
            _loadedKorean = null;
            _loadedRussian = null;
            _loadedJapanese = null;

            int loaded = 0;
            foreach (var kvp in LanguageFileNames)
            {
                string filePath = Path.Combine(dialogueFolder, kvp.Value);
                if (File.Exists(filePath))
                {
                    var db = TryLoadFile(filePath, kvp.Key);
                    if (db != null)
                    {
                        SetLoadedDatabase(kvp.Key, db);
                        loaded++;
                    }
                }
            }

            Main.Log($"[Dialogue] Reloaded {loaded} JSON dialogue files");
        }

        #endregion

        #region ═══ ENGLISH ═══

        public static readonly Dictionary<CompanionId, Dictionary<SpeechCategory, string[]>> EnglishDialogue
            = new Dictionary<CompanionId, Dictionary<SpeechCategory, string[]>>
        {
            // ── Abelard ──
            [CompanionId.Abelard] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "For the Lord Captain! Engaging [target]!",
                    "By your authority, [target] falls!",
                    "Striking down the enemy \u2014 as duty demands.",
                    "On my honor, [target] will not stand!",
                    "The Lord Captain's enemies are my enemies. [target] \u2014 prepare yourself."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "Advancing on your order, Lord Captain!",
                    "Closing distance \u2014 they will answer for this affront.",
                    "Moving to intercept [target]. They will not breach our line.",
                    "Forward! The seneschal leads the charge!"
                },
                [SpeechCategory.Heal] = new[]
                {
                    "Tending to [ally]. Stay in the fight.",
                    "[ally], hold steady. I have you."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Reinforcing our position. Stay sharp.",
                    "Preparing the line \u2014 the Lord Captain's will be done.",
                    "Tighten formation. We hold together or not at all.",
                    "Every hand to purpose \u2014 the Lord Captain is watching."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "You face the Rogue Trader's seneschal! Stand down or be broken!",
                    "Come then \u2014 I have weathered worse than you."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "Tactical withdrawal. This is not over.",
                    "Falling back \u2014 regroup on the Lord Captain.",
                    "Pulling back. We will find better ground.",
                    "Orderly retreat \u2014 maintain discipline!"
                },
                [SpeechCategory.Reload] = new[]
                {
                    "Reloading. Cover me.",
                    "A moment \u2014 my weapon needs feeding.",
                    "Cycling magazine. Hold the line!",
                    "Brief pause \u2014 I'll be back in the fight momentarily."
                },
                [SpeechCategory.Support] = new[]
                {
                    "Supporting [ally]! Hold your ground!",
                    "I've got your back, [ally]!"
                },
                [SpeechCategory.Victory] = new[]
                {
                    "The Emperor protects, and so do we.",
                    "Another victory for the Rogue Trader.",
                    "Well fought. Secure the area."
                },
            },

            // ── Heinrix ──
            [CompanionId.Heinrix] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "The Emperor judges you wanting, [target].",
                    "Burn.",
                    "Your heresy ends here.",
                    "No absolution for you, [target].",
                    "The pyre awaits, [target]. I merely hasten your arrival."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "Closing in. There is no escape from judgment.",
                    "The Inquisition arrives.",
                    "Approaching. Every step is a sentence.",
                    "I come for you, [target]. The Emperor wills it."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "You are still needed, [ally]. Do not waste this mercy.",
                    "Rise. The Emperor is not done with you."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Steel yourselves. Faith is our shield.",
                    "The Emperor protects \u2014 but preparation helps.",
                    "Let the Emperor's light harden your resolve.",
                    "Focus. Doubt is the enemy's weapon."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "I have broken stronger wills than yours in the interrogation chamber.",
                    "Face me, heretic. Let us see what you confess."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "Repositioning. Do not mistake this for weakness.",
                    "A strategic withdrawal \u2014 nothing more.",
                    "Pulling back. Even the Inquisition knows when to regroup.",
                    "This ground is not worth dying for. Yet."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "Reloading. The Emperor's work continues.",
                    "A brief pause. Then judgment resumes.",
                    "Feeding the instrument of His will.",
                    "Even righteous fury requires ammunition."
                },
                [SpeechCategory.Support] = new[]
                {
                    "The Emperor wills your survival, [ally].",
                    "You serve a purpose yet, [ally]."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "The Emperor's judgment has been rendered.",
                    "Another heresy purged.",
                    "Justice is served. For now."
                },
            },

            // ── Argenta ──
            [CompanionId.Argenta] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "BURN IN HOLY FIRE, [target]!",
                    "The Emperor's wrath made manifest!",
                    "Purge the unclean!",
                    "FEEL HIS JUDGMENT, [target]!",
                    "No mercy for the faithless! Firing on [target]!"
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "Into the fray! The faithful know no fear!",
                    "Charging \u2014 let them see the Emperor's fury!",
                    "Closing to righteous range! FOR THE EMPEROR!",
                    "The Sisters advance! Tremble, heretics!"
                },
                [SpeechCategory.Heal] = new[]
                {
                    "The Emperor wills your survival, [ally]!",
                    "His light mends what the enemy breaks!"
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Let faith be your armor!",
                    "The Emperor strengthens our resolve!",
                    "His fire burns within us! We are unbreakable!",
                    "Receive His blessing \u2014 and fight twice as hard!"
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "I am His instrument! Strike me if you dare!",
                    "Come, faithless wretches! Test the Sisters of Battle!"
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "...Withdrawing. But I shall return with twice the fury.",
                    "The flame dims only to roar back brighter.",
                    "I retreat only to gather His wrath anew!",
                    "This ground is lost \u2014 but the war is not! NEVER!"
                },
                [SpeechCategory.Reload] = new[]
                {
                    "Reloading \u2014 the Emperor's fire never ceases!",
                    "A moment to refuel His wrath!",
                    "Feeding the flames! One moment!",
                    "Even holy fire must be stoked! Reloading!"
                },
                [SpeechCategory.Support] = new[]
                {
                    "The Emperor shields His faithful, [ally]!",
                    "Stand firm, [ally]! Faith is your strength!"
                },
                [SpeechCategory.Victory] = new[]
                {
                    "By the Emperor's light, we prevail!",
                    "The heretics are purged!",
                    "His judgment has been delivered!"
                },
            },

            // ── Pasqal ──
            [CompanionId.Pasqal] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "Engaging target [target]. Probability of termination: acceptable.",
                    "Applying kinetic solution to biological problem.",
                    "Commencing hostilities. Target: [target].",
                    "Target [target] flagged for elimination. Weapons online.",
                    "Ballistic trajectories calculated. Engaging [target]."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "Optimizing engagement vector. Closing to effective range.",
                    "Repositioning servo-actuators. Target acquired.",
                    "Adjusting firing arc. Locomotion and targeting synchronized.",
                    "Closing to optimal engagement distance. [target] will not evade."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "Administering repair protocols to [ally]. Hold still.",
                    "Biological damage detected. Applying corrective measures."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Activating combat subroutines. Efficiency: increasing.",
                    "Omnissiah, bless these mechanisms of war.",
                    "Uploading enhanced targeting algorithms. Performance uplift: significant.",
                    "Augmenting output. The machine spirit is willing."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "Your threat assessment of me is... critically miscalculated.",
                    "I have replaced 73% of my organic components. What have you done?"
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "Withdrawing to reassess optimal parameters.",
                    "Tactical recomputation required. Disengaging.",
                    "Current position suboptimal. Relocating to improved coordinates.",
                    "Threat density exceeds engagement threshold. Falling back."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "Ammunition reserves depleted. Initiating reload sequence.",
                    "Cycling ammunition feed. Stand by.",
                    "Replenishing kinetic delivery system. 3.7 seconds.",
                    "Magazine empty. Swapping. The Omnissiah provides."
                },
                [SpeechCategory.Support] = new[]
                {
                    "Providing tactical augmentation to [ally].",
                    "Optimizing [ally]'s combat parameters."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "Combat efficiency: optimal. All targets neutralized.",
                    "Victory logged. Praise the Omnissiah.",
                    "Mission objective achieved. Systems nominal."
                },
            },

            // ── Idira ──
            [CompanionId.Idira] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "I can feel their fear... striking [target].",
                    "The Warp answers... whether I want it to or not.",
                    "Reaching out... this will hurt. Them, I mean.",
                    "The voices agree on [target]... that's... rare.",
                    "Sorry, [target]. Well... not really."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "Something pulls me forward... toward [target].",
                    "Moving closer. The voices say this is right... I think.",
                    "Getting closer... the Warp is louder here.",
                    "My feet move on their own. Toward [target]."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "Let me help, [ally]. I can do this much, at least.",
                    "Hold on... I can mend this. I hope."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Drawing from the Warp... carefully. Very carefully.",
                    "I'll share what strength I have. Just... stay close.",
                    "The Warp stirs... but I can shape it. For now.",
                    "Let me give you what little warmth I have."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "You don't want what's inside my head. Trust me.",
                    "Look at me. LOOK AT ME. ...See? You should run."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "Too much... I need to pull back.",
                    "The whispers are getting louder. Withdrawing.",
                    "I can't... not here. Falling back.",
                    "The veil is thin here... dangerously thin. Moving away."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "Need a moment... just a moment.",
                    "Reloading. The voices can wait.",
                    "Hold on... hands are shaking. Almost done.",
                    "A pause... the Warp can fill the silence."
                },
                [SpeechCategory.Support] = new[]
                {
                    "I can feel your pain, [ally]... let me ease it.",
                    "The Warp can help, [ally]. Sometimes."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "It's... over? Oh, thank the Throne...",
                    "We survived... somehow...",
                    "The voices are quieting... we won..."
                },
            },

            // ── Cassia ──
            [CompanionId.Cassia] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "I have seen the Warp itself \u2014 you do not frighten me, [target].",
                    "Engaging. Try not to bore me.",
                    "This hardly requires a Navigator's talents.",
                    "How tiresome. [target], you leave me no choice.",
                    "[target], you are beneath my notice. And yet \u2014 here we are."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "Advancing \u2014 and yes, I can handle this myself.",
                    "Moving in. I am more than just a Navigator, you know.",
                    "Approaching. Don't look so surprised.",
                    "A Navigator on the front line. Father would faint."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "Hold together, [ally]. Dying would be terribly inconvenient.",
                    "[ally], I didn't come all this way to watch you fall."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Allow me to improve our odds.",
                    "A Navigator's gift. You're welcome.",
                    "I do hope you appreciate the effort this requires.",
                    "Consider this my contribution. Don't squander it."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "My Third Eye has gazed upon horrors you cannot fathom. You are... quaint.",
                    "Over here. Let us see if you can handle a daughter of House Orsellio."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "A strategic reposition \u2014 House Orsellio does not 'flee.'",
                    "Withdrawing. This dress was expensive.",
                    "I've seen enough. Repositioning somewhere more... civilized.",
                    "Falling back. This is hardly dignified."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "Reloading. How tedious.",
                    "A momentary inconvenience.",
                    "One moment. Even Navigators must reload, apparently.",
                    "Must I do everything myself? ...Reloading."
                },
                [SpeechCategory.Support] = new[]
                {
                    "Don't get used to this, [ally].",
                    "I suppose I shall assist you, [ally]."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "Well, that was rather thrilling, wasn't it?",
                    "Was there ever any doubt?",
                    "A satisfactory outcome, I suppose."
                },
            },

            // ── Yrliet ──
            [CompanionId.Yrliet] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "Precise as the Path demands. Firing on [target].",
                    "[target] will not suffer long. I am... efficient.",
                    "Another mon-keigh problem I must solve.",
                    "One shot. One end. Firing on [target].",
                    "The Path of the Outcast claims another. [target]."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "Repositioning. Your kind would call it 'flanking.'",
                    "Gliding into position. Watch and learn, mon-keigh.",
                    "Shifting. You would not have seen me move.",
                    "A better angle reveals itself. Advancing."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "Try not to bleed so... dramatically, [ally].",
                    "Tending to [ally]. Humans are so fragile."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Enhancing our capabilities. You need it more than I.",
                    "A gift from a civilization that mastered war eons ago.",
                    "Accept this advantage. It cost me little.",
                    "Even mon-keigh fight better when properly aided."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "I have walked paths older than your species. Do you truly wish to test me?",
                    "Your aim is as poor as your architecture."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "I choose a better vantage. This is not retreat \u2014 it is patience.",
                    "Withdrawing. A ranger knows when to reposition.",
                    "Falling back. A concept your kind rarely grasps in time.",
                    "This position no longer serves. Moving."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "Reloading. Even Aeldari weapons require maintenance.",
                    "A brief pause. The next shot will be perfect.",
                    "Replenishing. Do not waste the time I am buying you.",
                    "A moment of stillness. Then perfection resumes."
                },
                [SpeechCategory.Support] = new[]
                {
                    "Consider this a courtesy, [ally].",
                    "Assisting [ally]. Do try to keep up."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "Adequate. For mon-keigh.",
                    "The skein predicted this outcome.",
                    "As expected. Shall we move on?"
                },
            },

            // ── Jae ──
            [CompanionId.Jae] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "Nothing personal, [target]. Actually \u2014 no, it is.",
                    "Let's settle this account.",
                    "This better be worth my time, [target].",
                    "You picked the wrong trader to cross, [target].",
                    "Payday. And [target]'s footing the bill."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "Moving in for a closer deal. [target] won't like the terms.",
                    "Closing distance \u2014 better margins up close.",
                    "Getting in range. Time to negotiate with lead.",
                    "Coming in hot. [target]'s about to get a refund \u2014 in pain."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "Patching you up, [ally]. You owe me one.",
                    "Can't collect on a dead partner, [ally]. Hold still."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Little trick I picked up in the Expanse.",
                    "Free of charge \u2014 this time.",
                    "Here \u2014 a little edge. Call it an investment.",
                    "Trade secret. Don't ask where I learned it."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "Over here, ugly! I've swindled worse than you!",
                    "Hey! Your bounty's not even worth my time \u2014 but let's dance anyway."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "Bad investment \u2014 pulling out.",
                    "Know when to fold. Moving back.",
                    "Cutting my losses. Tactical withdrawal.",
                    "The smart money says: fall back."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "Reloading. Don't go anywhere.",
                    "Need fresh ammo. This better pay off.",
                    "Restocking. Running low is bad for business.",
                    "One sec \u2014 even good guns need feeding."
                },
                [SpeechCategory.Support] = new[]
                {
                    "Helping [ally] \u2014 add it to my tab.",
                    "Covering [ally]. You owe me for this."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "That's done. Where's the loot?",
                    "Not bad, everyone. Not bad at all.",
                    "We're alive. That's profit enough."
                },
            },

            // ── Marazhai ──
            [CompanionId.Marazhai] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "Oh, [target]... this is going to be exquisite.",
                    "Scream for me, [target]. SCREAM.",
                    "Finally \u2014 something worth cutting.",
                    "Come, [target]. Let us create something... beautiful.",
                    "Your pain will be my masterpiece, [target]."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "Coming closer, little thing. Don't run \u2014 it only makes it sweeter.",
                    "The hunt closes. Delicious.",
                    "Each step brings us closer to the crescendo.",
                    "I can almost taste your fear, [target]. Closer now..."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "Ugh. Fine. Don't make me regret keeping you alive, [ally].",
                    "You're more entertaining alive, [ally]. Barely."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Let me sharpen your edges. I do so enjoy sharp things.",
                    "A taste of Commorragh's gifts.",
                    "Accept this... enhancement. It will make the killing sweeter.",
                    "I give you venom. Use it well \u2014 or at least entertainingly."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "Is THAT the best pain you can offer? Pathetic!",
                    "Hit me. Harder. ...Disappointing."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "Savoring the anticipation... I shall return for you.",
                    "Not fleeing \u2014 prolonging your suffering.",
                    "Distance only sharpens the hunger. I withdraw... for now.",
                    "The finest tortures require patience. Falling back."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "Reloading. The pain will resume shortly.",
                    "A brief intermission. Act two begins soon.",
                    "Feeding the instrument of agony. One moment.",
                    "Even cruelty requires preparation."
                },
                [SpeechCategory.Support] = new[]
                {
                    "Don't die yet, [ally]. I haven't finished watching.",
                    "Assisting [ally]. Don't mistake this for affection."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "Mmm... how delightful. Savor the moment.",
                    "Already? I was just beginning to enjoy myself.",
                    "Their screams were... acceptable."
                },
            },

            // ── Ulfar ──
            [CompanionId.Ulfar] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "FENRIS HJOLDA! [target], meet my axe!",
                    "For the Allfather! Tearing into [target]!",
                    "BY RUSS! [target] falls today!",
                    "TASTE FENRISIAN STEEL, [target]!",
                    "Another skull for the saga! [target] \u2014 YOUR TURN!"
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "The wolf closes on its prey!",
                    "Running them down! HAHA!",
                    "THE HUNT IS ON! Coming for you, [target]!",
                    "CAN YOU HEAR THE HOWL?! The wolf is upon you!"
                },
                [SpeechCategory.Heal] = new[]
                {
                    "Hold fast, [ally]! Wolves don't die easy!",
                    "Lick your wounds, [ally] \u2014 we hunt again soon."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Let the spirit of Fenris fill you!",
                    "Sharpen your fangs, pack-mates!",
                    "The wolf within stirs! EMBRACE IT!",
                    "Fenris lends you strength! USE IT WELL!"
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "COME! Face the Vlka Fenryka and die with some honor!",
                    "Is that all?! My pups hit harder!"
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "Bah! Falling back \u2014 but the wolf remembers!",
                    "Retreating... for now. The saga isn't over.",
                    "Even wolves know when to circle back! FALLING BACK!",
                    "The prey has fangs too. Repositioning!"
                },
                [SpeechCategory.Reload] = new[]
                {
                    "Reloading! Keep them busy for me!",
                    "Need more ammo! HAHA, what a fight!",
                    "Out of rounds! But not out of fight! Reloading!",
                    "Feeding the beast! One moment, packmates!"
                },
                [SpeechCategory.Support] = new[]
                {
                    "Stand with me, [ally]! The pack fights together!",
                    "I've got your flank, [ally]! ONWARDS!"
                },
                [SpeechCategory.Victory] = new[]
                {
                    "HA! Another great victory, packmates!",
                    "By Russ! What a fight!",
                    "VICTORY! The sagas will sing of this!"
                },
            },

            // ── Kibellah (DLC) ──
            [CompanionId.Kibellah] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "The ritual begins. [target] is the offering.",
                    "My blades sing for [target].",
                    "Another step in the dance of death.",
                    "The temple calls for [target]'s blood.",
                    "[target]... the blades have chosen you."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "Closing the distance. The offering cannot escape.",
                    "I move as the blade wills. [target] awaits.",
                    "The dance carries me forward. Toward [target].",
                    "Step by step. Closer to the offering."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "You are not permitted to fall yet, [ally].",
                    "[ally]... I would not see you end here. Not like this."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Preparing the rite. Every edge must be honed.",
                    "The dance requires preparation.",
                    "Sharpening. The ritual demands perfection.",
                    "The offerings come soon. We must be ready."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "I am the blade in the dark. Come \u2014 find me if you can.",
                    "You think you know death? I was raised in its temple."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "Withdrawing. The ritual is not yet complete \u2014 I will return.",
                    "Patience. A hunter does not rush the final cut.",
                    "Falling back. The dance is not over \u2014 merely paused.",
                    "The blade withdraws... to strike deeper."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "A brief pause in the dance.",
                    "Readying.",
                    "The rhythm demands a breath. Readying.",
                    "Between beats. Preparing."
                },
                [SpeechCategory.Support] = new[]
                {
                    "I will guard your step, [ally].",
                    "The dance continues \u2014 together, [ally]."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "The dance ends. For now.",
                    "Death has chosen its partners today.",
                    "The final step is taken."
                },
            },

            // ── Solomorne (DLC) ──
            [CompanionId.Solomorne] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "Engaging hostile. Lethal force authorized.",
                    "[target] \u2014 you are found guilty. Sentence: immediate.",
                    "The Lex Imperialis demands your compliance. Or your end.",
                    "Hostile [target] \u2014 charges filed. Executing sentence.",
                    "By authority of the Adeptus Arbites: [target], you are condemned."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "Advancing to enforce judgment on [target].",
                    "Closing in. Resistance is noted for the record.",
                    "Moving to apprehend \u2014 or terminate. [target]'s choice.",
                    "Pursuit initiated. The law does not relent."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "Administering field treatment to [ally]. Stay operational.",
                    "[ally], you are still required for duty. Hold together."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Fortifying our position. Standard enforcement protocol.",
                    "Preparing tactical advantage. By the book.",
                    "Reinforcing per Article 7, Section 3. Hold firm.",
                    "Enhancing readiness. The Arbites leave nothing to chance."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "I am the Lex Imperialis made flesh. Test me.",
                    "You face a Proctor of the Adeptus Arbites. Surrender or termination."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "Tactical repositioning. This is procedure, not retreat.",
                    "Falling back to defensible ground. The law is patient.",
                    "Withdrawing to fortified position. The pursuit will resume.",
                    "Ground conceded \u2014 temporarily. Justice does not forget."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "Reloading. Justice requires ammunition.",
                    "Cycling shells. Brief recess.",
                    "Replenishing. The sentence is not yet complete.",
                    "Reloading. Court is still in session."
                },
                [SpeechCategory.Support] = new[]
                {
                    "Providing support to [ally]. Regulation-compliant.",
                    "Covering [ally]. Glaito, with me. Good boy."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "Area secured. Filing post-action report.",
                    "Hostiles neutralized. Order is restored.",
                    "Justice has been served. Case closed."
                },
            },

            // ── Unknown (Default) ──
            [CompanionId.Unknown] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]     { "Engaging [target]!", "Firing on [target]!", "Taking the shot!", "Targeting [target]!", "Opening fire!" },
                [SpeechCategory.MoveAndAttack] = new[] { "Moving to engage [target]!", "Advancing on [target]!", "Pushing forward!", "Closing in on [target]!" },
                [SpeechCategory.Heal] = new[]       { "Healing [ally]!", "Patching up [ally]!" },
                [SpeechCategory.Buff] = new[]       { "Buffing up!", "Enhancing combat readiness!", "Preparing for engagement!", "Boosting our odds!" },
                [SpeechCategory.Taunt] = new[]      { "Drawing fire!", "Over here!" },
                [SpeechCategory.Retreat] = new[]    { "Falling back!", "Pulling out!", "Tactical retreat!", "Repositioning!" },
                [SpeechCategory.Reload] = new[]     { "Reloading!", "Cycling ammo!", "Swapping magazine!", "Need a moment \u2014 reloading!" },
                [SpeechCategory.Support] = new[]    { "Supporting [ally]!", "Covering [ally]!" },
                [SpeechCategory.Victory] = new[]    { "Area clear.", "Hostiles eliminated.", "Combat complete." },
            },
        };

        #endregion

        #region ═══ KOREAN (한국어) ═══
        // 어투 설계:
        //   Abelard: 격식체 존댓말 (~합니다, ~십시오) — 충직한 군인
        //   Heinrix: 냉담한 해라체 (~이다, ~하라) — 심문관 위엄
        //   Argenta: 열정적 해라체, 느낌표 과다 — 광신적 전사
        //   Pasqal:  건조한 보고체 (~확인됨, ~개시) — 기계교
        //   Idira:   소심한 해요체, 말줄임표 다수 — 불안정한 사이커
        //   Cassia:  도도한 해요체 (~이잖아요, ~인걸요) — 귀족 아가씨
        //   Yrliet:  초연한 해라체 (~일 뿐이다) — 엘다리 우월감
        //   Jae:     캐주얼 반말 (~거든, ~이야) — 실용적 상인
        //   Marazhai: 탐미적 해라체 (~하군, ~이로군) — 드루카리 가학
        //   Ulfar:   호쾌한 반말, 감탄사 — 전사 기질
        //   Kibellah: 간결한 해라체, 의례적 — 죽음 교단
        //   Solomorne: 공식 보고체 — 법 집행관
        //   Unknown: 간결한 보고체 — 기본 군인

        public static readonly Dictionary<CompanionId, Dictionary<SpeechCategory, string[]>> KoreanDialogue
            = new Dictionary<CompanionId, Dictionary<SpeechCategory, string[]>>
        {
            // ── Abelard ── 격식체 존댓말
            [CompanionId.Abelard] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "로드 캡틴을 위하여! [target] 교전합니다!",
                    "각하의 권위로, [target]을 쓰러뜨립니다!",
                    "의무가 명하는 대로, 적을 격퇴합니다.",
                    "제 명예를 걸고, [target]은 서 있지 못할 것입니다!",
                    "로드 캡틴의 적은 곧 제 적입니다. [target] \u2014 각오하십시오."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "로드 캡틴의 명에 따라 전진합니다!",
                    "거리를 좁힙니다 \u2014 이 무례함에 대가를 치르게 하겠습니다.",
                    "[target] 차단을 위해 이동합니다. 전열을 돌파시키지 않겠습니다.",
                    "전진! 집사관이 선봉을 이끕니다!"
                },
                [SpeechCategory.Heal] = new[]
                {
                    "[ally], 치료하겠습니다. 버텨주십시오.",
                    "[ally], 걱정 마십시오. 제가 있습니다."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "진지를 강화합니다. 긴장을 늦추지 마십시오.",
                    "전열을 정비합니다 \u2014 로드 캡틴의 뜻대로.",
                    "대형을 조이십시오. 함께 버티거나, 함께 무너집니다.",
                    "모든 손에 임무를 \u2014 로드 캡틴이 지켜보고 계십니다."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "로그 트레이더의 집사관과 대면하고 있다! 항복하거나 부서져라!",
                    "덤벼라 \u2014 이보다 더한 적도 상대해 왔다."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "전술적 후퇴입니다. 아직 끝나지 않았습니다.",
                    "후퇴합니다 \u2014 로드 캡틴 곁으로 재집결하겠습니다.",
                    "물러납니다. 더 나은 지형을 확보하겠습니다.",
                    "질서 있는 후퇴 \u2014 규율을 유지하십시오!"
                },
                [SpeechCategory.Reload] = new[]
                {
                    "재장전합니다. 엄호 부탁드립니다.",
                    "잠시 \u2014 무기에 탄을 넣겠습니다.",
                    "탄창 교체합니다. 전열을 사수하십시오!",
                    "잠시 멈춤 \u2014 곧 전투에 복귀하겠습니다."
                },
                [SpeechCategory.Support] = new[]
                {
                    "[ally]을 지원합니다! 자리를 사수하십시오!",
                    "[ally], 제가 뒤를 맡겠습니다!"
                },
                [SpeechCategory.Victory] = new[]
                {
                    "황제께서 보호하시고, 저희가 지킵니다.",
                    "로그 트레이더를 위한 또 하나의 승리입니다.",
                    "잘 싸우셨습니다. 지역을 확보하겠습니다."
                },
            },

            // ── Heinrix ── 냉담한 해라체
            [CompanionId.Heinrix] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "황제께서 너를 부족하다 판단하셨다, [target].",
                    "불태워라.",
                    "이단은 여기서 끝이다.",
                    "사면은 없다, [target].",
                    "화형대가 기다린다, [target]. 나는 단지 도착을 앞당길 뿐이다."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "접근한다. 심판에서 도망칠 곳은 없다.",
                    "Inquisition이 도착했다.",
                    "접근 중이다. 한 걸음 한 걸음이 선고다.",
                    "[target], 내가 간다. 황제의 뜻이다."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "아직 쓸모가 있다, [ally]. 이 자비를 낭비하지 마라.",
                    "일어나라. 황제께서 너를 아직 놓아주지 않으셨다."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "각오를 다져라. 신앙이 곧 방패다.",
                    "황제께서 지켜주신다 \u2014 하지만 준비는 도움이 된다.",
                    "황제의 빛이 결의를 단단히 하라.",
                    "집중하라. 의심은 적의 무기다."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "심문실에서 너보다 강한 의지도 꺾어본 적 있다.",
                    "마주해라, 이단자. 무엇을 고백하는지 보자."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "재배치한다. 이것을 나약함으로 오해하지 마라.",
                    "전략적 후퇴다 \u2014 그 이상도 이하도 아니다.",
                    "물러난다. Inquisition도 재집결할 때를 안다.",
                    "이 땅은 죽을 가치가 없다. 아직은."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "재장전. 황제의 사업은 계속된다.",
                    "잠시 멈춤. 곧 심판이 재개된다.",
                    "그분의 뜻의 도구에 탄을 먹인다.",
                    "정의로운 분노에도 탄약은 필요하다."
                },
                [SpeechCategory.Support] = new[]
                {
                    "황제께서 네 생존을 원하신다, [ally].",
                    "아직 쓸모가 있다, [ally]."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "황제의 심판이 내려졌다.",
                    "또 하나의 이단이 제거되었다.",
                    "정의가 실현되었다. 지금은."
                },
            },

            // ── Argenta ── 열정적 해라체
            [CompanionId.Argenta] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "성스러운 불꽃에 타라, [target]!",
                    "황제의 진노가 현현하였다!",
                    "불결한 자를 정화하라!",
                    "그분의 심판을 받아라, [target]!",
                    "믿음 없는 자에게 자비란 없다! [target]을 사격한다!"
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "전장으로! 신앙인은 두려움을 모른다!",
                    "돌격한다 \u2014 황제의 분노를 보여주마!",
                    "신성한 사거리까지 전진! 황제를 위하여!",
                    "Sisters가 전진한다! 떨어라, 이단자들이여!"
                },
                [SpeechCategory.Heal] = new[]
                {
                    "황제께서 네 생존을 원하신다, [ally]!",
                    "그분의 빛이 적이 부순 것을 고친다!"
                },
                [SpeechCategory.Buff] = new[]
                {
                    "신앙을 갑옷으로 삼아라!",
                    "황제께서 우리의 결의를 강하게 하신다!",
                    "그분의 불꽃이 우리 안에서 타오른다! 우리는 부서지지 않는다!",
                    "그분의 축복을 받아라 \u2014 그리고 두 배로 싸워라!"
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "나는 그분의 도구다! 감히 나를 쳐보아라!",
                    "오너라, 믿음 없는 것들아! Sisters of Battle을 시험해 보아라!"
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "...후퇴한다. 하지만 두 배의 분노로 돌아오겠다.",
                    "불꽃이 잦아드는 것은 더 크게 타오르기 위함이다.",
                    "황제의 분노를 새로이 모으기 위해 후퇴할 뿐이다!",
                    "이 땅은 잃었다 \u2014 하지만 전쟁은 아니다! 절대로!"
                },
                [SpeechCategory.Reload] = new[]
                {
                    "재장전 \u2014 황제의 불꽃은 멈추지 않는다!",
                    "그분의 진노에 기름을 부을 시간!",
                    "불꽃에 기름을! 잠깐!",
                    "성스러운 불도 지펴야 한다! 재장전!"
                },
                [SpeechCategory.Support] = new[]
                {
                    "황제께서 충실한 자를 지키신다, [ally]!",
                    "버텨라, [ally]! 신앙이 곧 힘이다!"
                },
                [SpeechCategory.Victory] = new[]
                {
                    "황제의 빛으로, 승리하였다!",
                    "이단자들이 숙청되었다!",
                    "그분의 심판이 내려졌다!"
                },
            },

            // ── Pasqal ── 건조한 보고체
            [CompanionId.Pasqal] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "대상 [target] 포착. 제거 확률: 수용 가능.",
                    "생체 문제에 운동에너지 해법 적용.",
                    "적대 행위 개시. 대상: [target].",
                    "대상 [target] 제거 대상으로 지정. 무기 시스템 온라인.",
                    "탄도 궤적 계산 완료. [target] 교전 개시."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "교전 벡터 최적화. 유효 사거리로 접근 중.",
                    "서보 액추에이터 재배치. 대상 포착 완료.",
                    "사격 호 조정 중. 이동과 조준 동기화 완료.",
                    "최적 교전 거리까지 접근 중. [target]은 회피 불가."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "[ally]에게 수리 프로토콜 적용. 움직이지 마시오.",
                    "생체 손상 감지. 교정 조치 적용 중."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "전투 서브루틴 활성화. 효율: 증가 중.",
                    "Omnissiah이시여, 이 전쟁 기계를 축복하소서.",
                    "강화 타겟팅 알고리즘 업로드. 성능 향상: 유의미.",
                    "출력 증강 중. 기계 정령이 승낙하였음."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "나에 대한 위협 평가가... 심각하게 잘못되었다.",
                    "유기 부품의 73%를 교체했다. 너는 무엇을 개선했는가?"
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "최적 매개변수 재평가를 위해 후퇴.",
                    "전술 재계산 필요. 이탈 중.",
                    "현 위치 비최적. 개선된 좌표로 재배치.",
                    "위협 밀도가 교전 임계치를 초과. 후퇴."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "탄약 예비 소진. 재장전 시퀀스 개시.",
                    "탄약 공급 순환 중. 대기.",
                    "운동에너지 전달 시스템 보충 중. 3.7초.",
                    "탄창 공허. 교체 중. Omnissiah이 공급하신다."
                },
                [SpeechCategory.Support] = new[]
                {
                    "[ally]에게 전술적 증강 제공 중.",
                    "[ally]의 전투 매개변수 최적화 중."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "전투 효율: 최적. 모든 목표 무력화 완료.",
                    "승리 기록됨. Omnissiah를 찬양하라.",
                    "임무 목표 달성. 시스템 정상."
                },
            },

            // ── Idira ── 소심한 해요체
            [CompanionId.Idira] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "공포가 느껴져요... [target]을 공격해요.",
                    "Warp가 응답해요... 원하든 원치 않든...",
                    "손을 뻗을게요... 아플 거예요. 저쪽이요, 제 말은.",
                    "목소리들이 [target]에 대해 의견이 같아요... 그건... 드문 일이에요.",
                    "미안해요, [target]. 음... 사실 별로."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "뭔가가 저를 앞으로 끌어요... [target] 쪽으로...",
                    "더 가까이 다가갈게요. 목소리들이 이게 맞다고 해요... 아마도.",
                    "더 가까이 가요... 여기서 Warp가 더 시끄러워요.",
                    "발이 저절로 움직여요. [target] 쪽으로."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "도와줄게요, [ally]. 이 정도는 할 수 있어요.",
                    "버텨요... 고칠 수 있어요. 아마도요."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Warp에서 끌어올려요... 조심스럽게. 아주 조심스럽게.",
                    "가진 힘을 나눌게요. 그냥... 가까이 있어요.",
                    "Warp가 요동쳐요... 하지만 형태를 만들 수 있어요. 지금은.",
                    "가진 따뜻함을 나눠줄게요."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "내 머릿속에 뭐가 있는지 알고 싶지 않을걸. 진심이야.",
                    "나를 봐. 나를 보라고! ...봤지? 도망쳐야 할 거야."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "너무 많아요... 물러나야 해요.",
                    "속삭임이 점점 커져요. 후퇴할게요.",
                    "못 하겠어요... 여기서는. 물러날게요.",
                    "여기는 베일이 얇아요... 위험할 정도로. 벗어날게요."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "잠깐만요... 잠깐이면 돼요.",
                    "재장전해요. 목소리들은 기다릴 수 있어요.",
                    "잠깐... 손이 떨려요. 거의 다 됐어요.",
                    "멈춤... Warp가 침묵을 채울 수 있어요."
                },
                [SpeechCategory.Support] = new[]
                {
                    "고통이 느껴져요, [ally]... 달래줄게요.",
                    "Warp가 도움이 될 수 있어요, [ally]. 가끔은."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "끝... 난 거예요? 아, 다행이다...",
                    "살아남았어요... 어떻게든...",
                    "목소리가 잠잠해져요... 이겼어요..."
                },
            },

            // ── Cassia ── 도도한 해요체
            [CompanionId.Cassia] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "Warp 그 자체를 봤는데 \u2014 네가 무섭겠어, [target]?",
                    "교전이요. 지루하게 하지는 마세요.",
                    "이건 Navigator의 재능이 필요할 정도도 아닌걸요.",
                    "참 성가시네요. [target], 선택의 여지를 안 주시네요.",
                    "[target], 눈에 들지도 않는데요. 그런데 \u2014 여기까지 왔으니."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "전진해요 \u2014 네, 혼자서도 잘해요.",
                    "다가갈게요. 저는 단순한 Navigator가 아니라고요.",
                    "다가가요. 그렇게 놀란 표정은 하지 마세요.",
                    "최전선의 Navigator라니. 아버지가 기절하시겠네요."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "버텨요, [ally]. 여기서 죽으면 정말 곤란해요.",
                    "[ally], 여기까지 와서 쓰러지는 꼴은 보기 싫어요."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "승산을 높여드릴게요.",
                    "Navigator의 선물이에요. 감사는 안 해도 돼요.",
                    "이게 얼마나 수고스러운지 알아주셨으면 해요.",
                    "제 기여분이에요. 낭비하지 마세요."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "제 제3의 눈은 네가 상상도 못할 공포를 봐왔어. 넌... 귀엽네.",
                    "이쪽이야. House Orsellio의 딸을 감당할 수 있는지 보자."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "전략적 재배치예요 \u2014 House Orsellio는 '도망'하지 않아요.",
                    "물러날게요. 이 드레스 비쌌거든요.",
                    "충분히 봤어요. 좀 더... 품위 있는 곳으로 재배치해요.",
                    "후퇴해요. 이건 품위가 없잖아요."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "재장전이요. 참 번거로워요.",
                    "잠깐의 불편함이네요.",
                    "잠깐이요. Navigator도 재장전해야 하다니요.",
                    "제가 다 해야 하나요? ...재장전이에요."
                },
                [SpeechCategory.Support] = new[]
                {
                    "익숙해지지는 마세요, [ally].",
                    "도와드려야겠네요, [ally]."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "꽤 흥미진진했네요, 그렇지 않아요?",
                    "의심의 여지가 있었나요?",
                    "만족스러운 결과인 것 같네요."
                },
            },

            // ── Yrliet ── 초연한 해라체
            [CompanionId.Yrliet] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "길이 요구하는 대로 정확히. [target] 사격.",
                    "[target]은 오래 고통받지 않을 것이다. 나는... 효율적이니.",
                    "mon-keigh의 문제를 또 해결해야 하는군.",
                    "한 발. 한 번의 끝. [target] 사격.",
                    "방랑자의 길이 또 하나를 거둔다. [target]."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "재배치한다. 너희 종족은 이걸 '측면기동'이라 부르지.",
                    "위치 잡는 중이다. 보고 배워라, mon-keigh.",
                    "이동한다. 너는 내가 움직인 것도 눈치채지 못했을 것이다.",
                    "더 나은 각도가 보인다. 전진한다."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "그렇게 요란하게 피를 흘리지 마라, [ally].",
                    "[ally] 치료 중이다. 인간은 참 연약해."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "전투력을 강화한다. 너에게 나보다 더 필요하니.",
                    "오래전에 전쟁을 완성한 문명의 선물이다.",
                    "이 이점을 받아들여라. 나에게는 별 비용이 아니다.",
                    "mon-keigh도 적절한 지원을 받으면 더 잘 싸운다."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "네 종족보다 오래된 길을 걸어왔다. 정말로 시험해보겠는가?",
                    "네 조준 실력은 네 건축물만큼이나 형편없군."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "더 나은 위치를 선택할 뿐이다. 후퇴가 아니라 \u2014 인내다.",
                    "물러난다. 레인저는 재배치할 때를 안다.",
                    "후퇴한다. 너희 종족이 제때 이해하기 드문 개념이지.",
                    "이 위치는 더 이상 유용하지 않다. 이동한다."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "재장전이다. Aeldari 무기도 정비가 필요하니.",
                    "잠시 멈춤. 다음 사격은 완벽할 것이다.",
                    "보충 중이다. 내가 벌어주는 시간을 낭비하지 마라.",
                    "잠시의 정적. 그리고 완벽함이 재개된다."
                },
                [SpeechCategory.Support] = new[]
                {
                    "이건 호의로 여겨라, [ally].",
                    "[ally] 지원 중이다. 뒤처지지 않도록."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "충분하다. mon-keigh치고는.",
                    "실타래가 이 결과를 예측했다.",
                    "예상대로. 이동하겠는가?"
                },
            },

            // ── Jae ── 캐주얼 반말
            [CompanionId.Jae] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "사적인 감정 없어, [target]. 아 아니다 \u2014 있어.",
                    "이 계좌 정리하자.",
                    "내 시간값은 하겠지, [target]?",
                    "잘못된 상인에게 찍혔어, [target].",
                    "페이데이야. 그리고 [target]이 계산하는 거지."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "더 가까이서 거래하러 간다. [target]은 조건이 마음에 안 들 거야.",
                    "거리 좁히는 중 \u2014 가까울수록 이문이 커.",
                    "사거리 안으로 들어간다. 납탄으로 협상할 시간이야.",
                    "들어간다. [target]은 고통으로 환불받게 될 거야."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "고쳐줄게, [ally]. 빚진 거야.",
                    "죽은 파트너한테는 빚을 못 받거든, [ally]. 가만히 있어."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Expanse에서 배운 소소한 기술이야.",
                    "공짜야 \u2014 이번만.",
                    "자 \u2014 약간의 우위. 투자라고 생각해.",
                    "영업 비밀이야. 어디서 배웠는지 묻지 마."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "이쪽이야, 못난이! 너보다 더한 놈도 속여봤어!",
                    "이봐! 현상금도 안 되는 놈이지만 \u2014 한번 놀아보자."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "수지가 안 맞아 \u2014 빠진다.",
                    "접을 때를 알아야지. 후퇴.",
                    "손절이야. 전술적 후퇴.",
                    "현명한 돈은 말해: 빠져라."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "재장전. 어디 가지 마.",
                    "탄약 필요해. 이거 본전은 뽑아야 하는데.",
                    "재고 보충 중. 떨어지면 장사가 안 돼.",
                    "잠깐 \u2014 좋은 총도 먹여야 해."
                },
                [SpeechCategory.Support] = new[]
                {
                    "[ally] 도와주는 거야 \u2014 외상에 추가.",
                    "[ally] 엄호 중. 이건 빚이야."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "끝났네. 전리품은 어디 있어?",
                    "나쁘지 않았어, 다들. 나쁘지 않아.",
                    "살아있으니까. 그것만으로도 이득이야."
                },
            },

            // ── Marazhai ── 탐미적 해라체
            [CompanionId.Marazhai] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "아, [target]... 절묘하겠군.",
                    "비명을 질러라, [target]. 비명을!",
                    "드디어 \u2014 벨 가치가 있는 것이 나타났군.",
                    "이리 오너라, [target]. 함께 아름다운 것을... 만들자.",
                    "네 고통이 나의 걸작이 될 것이다, [target]."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "더 가까이 간다, 작은 것아. 도망치지 마 \u2014 더 달콤해지니까.",
                    "사냥이 마무리된다. 맛있군.",
                    "한 걸음 한 걸음이 클라이맥스에 가까워진다.",
                    "네 공포의 맛이 거의 느껴진다, [target]. 더 가까이..."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "쯧. 좋아. 살려둔 걸 후회하게 만들지 마라, [ally].",
                    "살아있는 게 더 재미있으니까, [ally]. 간신히."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "칼날을 갈아주마. 나는 날카로운 것을 좋아하거든.",
                    "Commorragh의 선물을 맛보아라.",
                    "이... 강화를 받아라. 살육을 더 달콤하게 해줄 것이다.",
                    "독을 주마. 잘 사용해라 \u2014 아니면 최소한 재미있게."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "그게 줄 수 있는 최선의 고통이냐? 한심하군!",
                    "때려봐. 더 세게. ...실망이로군."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "기대감을 음미하는 중이다... 너에게 돌아오마.",
                    "도망이 아니다 \u2014 고통을 연장하는 것이다.",
                    "거리는 굶주림을 더 날카롭게 할 뿐이다. 물러난다... 지금은.",
                    "최고의 고문에는 인내가 필요하다. 후퇴한다."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "재장전이다. 곧 고통이 재개된다.",
                    "잠깐의 막간이다. 2막이 곧 시작된다.",
                    "고통의 도구에 탄을 먹인다. 잠깐.",
                    "잔혹함에도 준비가 필요하다."
                },
                [SpeechCategory.Support] = new[]
                {
                    "아직 죽지 마, [ally]. 구경이 안 끝났다.",
                    "[ally] 지원 중이다. 이걸 애정으로 착각하지 마라."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "음... 유쾌하군. 이 순간을 음미하라.",
                    "벌써? 이제 막 즐기기 시작했는데.",
                    "비명이... 괜찮았다."
                },
            },

            // ── Ulfar ── 호쾌한 반말
            [CompanionId.Ulfar] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "FENRIS HJOLDA! [target], 내 도끼를 받아라!",
                    "Allfather를 위하여! [target]을 찢는다!",
                    "RUSS의 이름으로! [target]은 오늘 쓰러진다!",
                    "FENRIS의 강철을 맛보아라, [target]!",
                    "사가에 두개골 하나 더! [target] \u2014 네 차례다!"
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "늑대가 먹이에게 다가간다!",
                    "쫓아간다! 하하하!",
                    "사냥 개시! [target], 간다!",
                    "울부짖음이 들리냐?! 늑대가 덮친다!"
                },
                [SpeechCategory.Heal] = new[]
                {
                    "버텨라, [ally]! 늑대는 쉽게 죽지 않아!",
                    "상처를 핥아라, [ally] \u2014 곧 다시 사냥이다."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Fenris의 정신이 너희를 채우게 하라!",
                    "송곳니를 갈아라, 동료들이여!",
                    "안의 늑대가 깨어난다! 받아들여라!",
                    "Fenris가 힘을 빌려준다! 잘 써라!"
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "덤벼라! Vlka Fenryka와 맞서고 명예롭게 죽어라!",
                    "그게 다야?! 내 새끼 늑대들이 더 세게 친다!"
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "챠! 물러난다 \u2014 하지만 늑대는 기억한다!",
                    "후퇴... 지금은. 사가는 아직 끝나지 않았다.",
                    "늑대도 돌아갈 때를 안다! 후퇴!",
                    "먹이도 이빨이 있군. 재배치!"
                },
                [SpeechCategory.Reload] = new[]
                {
                    "재장전! 녀석들 좀 잡아둬!",
                    "탄약 더 필요해! 하하, 대단한 싸움이야!",
                    "탄 떨어졌다! 하지만 싸움은 안 끝났어! 재장전!",
                    "짐승에게 밥을! 잠깐이다, 동료들!"
                },
                [SpeechCategory.Support] = new[]
                {
                    "내 곁에 서라, [ally]! 무리는 함께 싸운다!",
                    "네 옆구리를 지킨다, [ally]! 앞으로!"
                },
                [SpeechCategory.Victory] = new[]
                {
                    "하! 또 하나의 대승이다, 동지들이여!",
                    "러스에게 맹세코! 대단한 싸움이었다!",
                    "승리다! 사가에 기록될 것이다!"
                },
            },

            // ── Kibellah (DLC) ── 간결한 해라체, 의례적
            [CompanionId.Kibellah] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "의식이 시작된다. [target]은 제물이다.",
                    "칼이 [target]을 위해 노래한다.",
                    "죽음의 춤에서 한 걸음 더.",
                    "신전이 [target]의 피를 부른다.",
                    "[target]... 칼날이 너를 선택했다."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "거리를 좁힌다. 제물은 도망칠 수 없다.",
                    "칼이 이끄는 대로 움직인다. [target]이 기다린다.",
                    "춤이 나를 앞으로 이끈다. [target]을 향해.",
                    "한 걸음씩. 제물에게 다가간다."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "아직 쓰러질 허락은 없다, [ally].",
                    "[ally]... 여기서 이렇게 끝나는 건 원치 않는다."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "의식을 준비한다. 모든 칼날은 벼려야 한다.",
                    "춤에는 준비가 필요하다.",
                    "날을 세운다. 의식은 완벽을 요구한다.",
                    "제물이 곧 온다. 준비해야 한다."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "나는 어둠 속의 칼날이다. 와라 \u2014 찾을 수 있다면.",
                    "죽음을 안다고? 나는 그 신전에서 자랐다."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "물러난다. 의식은 아직 끝나지 않았다 \u2014 돌아온다.",
                    "인내. 사냥꾼은 마지막 일격을 서두르지 않는다.",
                    "물러선다. 춤은 끝나지 않았다 \u2014 잠시 멈출 뿐이다.",
                    "칼날이 물러난다... 더 깊이 찌르기 위해."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "춤에 잠시 멈춤.",
                    "준비 중.",
                    "리듬이 숨을 요구한다. 준비.",
                    "박자 사이. 준비 중."
                },
                [SpeechCategory.Support] = new[]
                {
                    "네 발걸음을 지키겠다, [ally].",
                    "춤은 계속된다 \u2014 함께, [ally]."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "춤이 끝났다. 지금은.",
                    "죽음이 오늘의 파트너를 선택했다.",
                    "마지막 발걸음이 내딛어졌다."
                },
            },

            // ── Solomorne (DLC) ── 공식 보고체
            [CompanionId.Solomorne] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "적대 대상 교전. 치명 무력 승인됨.",
                    "[target] \u2014 유죄 판결. 형량: 즉시 집행.",
                    "Lex Imperialis가 순응을 요구한다. 아니면 종결을.",
                    "적대 대상 [target] \u2014 기소 완료. 형 집행 중.",
                    "Adeptus Arbites의 권한으로: [target], 사형 선고."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "[target]에 대한 판결 집행을 위해 전진.",
                    "접근 중. 저항은 기록에 남겨둔다.",
                    "체포 또는 사살을 위해 이동. [target]의 선택이다.",
                    "추적 개시. 법은 멈추지 않는다."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "[ally]에게 현장 치료 실시. 작전 가능 상태를 유지하라.",
                    "[ally], 임무 수행이 아직 필요하다. 버텨라."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "진지 강화. 표준 집행 절차.",
                    "전술적 우위 확보. 규정대로.",
                    "제7조 3항에 따른 강화. 유지하라.",
                    "전투 태세 향상. Arbites는 우연에 맡기지 않는다."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "나는 Lex Imperialis의 화신이다. 시험해 보아라.",
                    "Adeptus Arbites의 집행관과 대면 중이다. 항복 또는 사살."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "전술적 재배치. 이것은 절차이지 후퇴가 아니다.",
                    "방어 가능 지점으로 후퇴. 법은 인내한다.",
                    "방어 진지로 철수. 추적은 재개될 것이다.",
                    "일시 양보 \u2014 정의는 잊지 않는다."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "재장전. 정의에는 탄약이 필요하다.",
                    "탄피 순환 중. 잠시 휴정.",
                    "보급 중. 형 집행은 아직 완료되지 않았다.",
                    "재장전. 개정은 아직 진행 중이다."
                },
                [SpeechCategory.Support] = new[]
                {
                    "[ally] 지원 중. 규정 준수.",
                    "[ally] 엄호. Glaito, 따라와. 잘한다."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "지역 확보. 사후 보고서 작성 중.",
                    "적 무력화 완료. 질서가 회복되었다.",
                    "정의가 집행되었다. 사건 종결."
                },
            },

            // ── Unknown (기본) ──
            [CompanionId.Unknown] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]     { "[target] 교전!", "[target] 사격!", "발사!", "[target] 조준!", "사격 개시!" },
                [SpeechCategory.MoveAndAttack] = new[] { "[target] 교전 위해 이동!", "[target]으로 전진!", "전방 돌파!", "[target] 접근 중!" },
                [SpeechCategory.Heal] = new[]       { "[ally] 치료!", "[ally] 응급처치!" },
                [SpeechCategory.Buff] = new[]       { "강화 준비!", "전투 태세 강화!", "교전 준비!", "전투력 강화!" },
                [SpeechCategory.Taunt] = new[]      { "화력 유인!", "이쪽이다!" },
                [SpeechCategory.Retreat] = new[]    { "후퇴!", "철수!", "전술 후퇴!", "재배치!" },
                [SpeechCategory.Reload] = new[]     { "재장전!", "탄약 교체!", "탄창 교환!", "잠깐 \u2014 재장전!" },
                [SpeechCategory.Support] = new[]    { "[ally] 지원!", "[ally] 엄호!" },
                [SpeechCategory.Victory] = new[]    { "지역 소탕 완료.", "적 제거 완료.", "전투 종료." },
            },
        };

        #endregion

        #region ═══ RUSSIAN (Русский) ═══
        // Стиль речи:
        //   Abelard: Формальный, военный, «вы» к Лорду-Капитану
        //   Heinrix: Холодный, краткий, «ты» к врагам
        //   Argenta: Фанатичная, восклицания
        //   Pasqal:  Механический рапорт
        //   Idira:   Неуверенная, многоточия
        //   Cassia:  Аристократическая, надменная
        //   Yrliet:  Высокомерная, отстранённая
        //   Jae:     Разговорная, прагматичная
        //   Marazhai: Садистский, театральный
        //   Ulfar:   Буйный, восклицания
        //   Kibellah: Ритуальная, лаконичная
        //   Solomorne: Юридический, официальный
        //   Unknown: Стандартный военный

        public static readonly Dictionary<CompanionId, Dictionary<SpeechCategory, string[]>> RussianDialogue
            = new Dictionary<CompanionId, Dictionary<SpeechCategory, string[]>>
        {
            // ── Abelard ──
            [CompanionId.Abelard] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "За Лорда-Капитана! Атакую [target]!",
                    "Вашей властью, [target] падёт!",
                    "Разим врага \u2014 как велит долг.",
                    "Клянусь честью, [target] не устоит!",
                    "Враги Лорда-Капитана \u2014 мои враги. [target] \u2014 готовьтесь."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "Выдвигаюсь по вашему приказу, Лорд-Капитан!",
                    "Сокращаю дистанцию \u2014 они ответят за это оскорбление.",
                    "Перехватываю [target]. Линию они не прорвут.",
                    "Вперёд! Сенешаль ведёт авангард!"
                },
                [SpeechCategory.Heal] = new[]
                {
                    "Лечу [ally]. Держитесь.",
                    "[ally], не волнуйтесь. Я рядом."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Укрепляю позицию. Будьте наготове.",
                    "Готовлю строй \u2014 да свершится воля Лорда-Капитана.",
                    "Выполняю укрепление по протоколу. Строй держать.",
                    "Тактическая подготовка завершена. По местам."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "Перед вами сенешаль Вольного Торговца! Сдавайтесь или будете сломлены!",
                    "Ну же \u2014 я и не такое выдерживал."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "Тактический отход. Это ещё не конец.",
                    "Отступаю \u2014 перегруппировка у Лорда-Капитана.",
                    "Организованный отход. Противник заплатит за это позже.",
                    "Отступаю на позицию Лорда-Капитана. Это процедура."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "Перезарядка. Прикройте.",
                    "Момент \u2014 оружие нуждается в питании.",
                    "Готовлю оружие к новому залпу. Прикройте фланг.",
                    "Боезапас пополняется. Держите строй."
                },
                [SpeechCategory.Support] = new[]
                {
                    "Поддерживаю [ally]! Держите позицию!",
                    "Прикрываю тебя, [ally]!"
                },
                [SpeechCategory.Victory] = new[]
                {
                    "Император защищает, и мы тоже.",
                    "Ещё одна победа для Вольного Торговца.",
                    "Отлично сражались. Обеспечиваем территорию."
                },
            },

            // ── Heinrix ──
            [CompanionId.Heinrix] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "Император счёл тебя недостойным, [target].",
                    "Гори.",
                    "Твоя ересь кончается здесь.",
                    "Приговор вынесен, [target]. Обжалованию не подлежит.",
                    "Императорский суд окончен. [target] \u2014 виновен."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "Приближаюсь. От суда не скрыться.",
                    "Инквизиция прибыла.",
                    "Сокращаю дистанцию. Приговор неизбежен.",
                    "Иду к тебе, [target]. Бежать бессмысленно."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "Ты ещё нужен, [ally]. Не трать эту милость впустую.",
                    "Встань. Император ещё не закончил с тобой."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Закалите себя. Вера \u2014 наш щит.",
                    "Император защищает \u2014 но подготовка не помешает.",
                    "Очистите свой разум. Сила придёт.",
                    "Укрепляю. Святая Терра этого требует."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "Я ломал волю и покрепче твоей в допросной.",
                    "Встань передо мной, еретик. Посмотрим, что ты расскажешь."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "Перемещаюсь. Не путай это со слабостью.",
                    "Стратегический отход \u2014 не более.",
                    "Отхожу для переоценки. Суд продолжится.",
                    "Перегруппировка. Инквизиция никогда не отступает \u2014 лишь перестраивается."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "Перезарядка. Работа Императора продолжается.",
                    "Короткая пауза. Затем суд возобновится.",
                    "Готовлю оружие. Ересь не ждёт.",
                    "Пауза. Пламя нуждается в топливе."
                },
                [SpeechCategory.Support] = new[]
                {
                    "Император желает твоего выживания, [ally].",
                    "В тебе ещё есть нужда, [ally]."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "Суд Императора свершился.",
                    "Ещё одна ересь уничтожена.",
                    "Справедливость восторжествовала. Пока."
                },
            },

            // ── Argenta ──
            [CompanionId.Argenta] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "ГОРИ В СВЯТОМ ПЛАМЕНИ, [target]!",
                    "Гнев Императора воплощён!",
                    "Очистить нечистых!",
                    "Священный огонь обрушится на [target]!",
                    "ИМЕНЕМ ИМПЕРАТОРА! [target] будет обращён в пепел!"
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "В бой! Верующие не ведают страха!",
                    "В атаку \u2014 пусть узрят ярость Императора!",
                    "Вперёд! Покажем нечестивым истинную ВЕРУ!",
                    "Сёстры не отступают! [target] \u2014 я иду!"
                },
                [SpeechCategory.Heal] = new[]
                {
                    "Император желает твоего выживания, [ally]!",
                    "Его свет исцеляет то, что сломал враг!"
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Да будет вера вашей бронёй!",
                    "Император укрепляет нашу решимость!",
                    "Его святое пламя наполняет нас! ЧУВСТВУЕТЕ?!",
                    "Очиститесь верой! Подготовьтесь к священному бою!"
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "Я \u2014 Его орудие! Попробуй ударь!",
                    "Сюда, безбожные твари! Испытайте Сестёр Битвы!"
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "...Отступаю. Но вернусь с удвоенной яростью.",
                    "Пламя стихает лишь чтобы разгореться ярче.",
                    "Временное отступление! Император испытывает нас!",
                    "Назад \u2014 но ненадолго! Священная ярость лишь растёт!"
                },
                [SpeechCategory.Reload] = new[]
                {
                    "Перезарядка \u2014 огонь Императора не гаснет!",
                    "Миг, чтобы подпитать Его гнев!",
                    "Снаряжаю оружие Его правосудием! Секунду!",
                    "Даже святое пламя нужно подпитывать! Перезарядка!"
                },
                [SpeechCategory.Support] = new[]
                {
                    "Император укрывает верных, [ally]!",
                    "Стой крепко, [ally]! Вера \u2014 твоя сила!"
                },
                [SpeechCategory.Victory] = new[]
                {
                    "Светом Императора мы побеждаем!",
                    "Еретики уничтожены!",
                    "Его суд свершился!"
                },
            },

            // ── Pasqal ──
            [CompanionId.Pasqal] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "Цель захвачена: [target]. Вероятность уничтожения: приемлемая.",
                    "Применяю кинетическое решение к биологической проблеме.",
                    "Начинаю боевые действия. Цель: [target].",
                    "Враждебная сигнатура подтверждена. Нейтрализация [target] одобрена.",
                    "Баллистический расчёт завершён. Открываю огонь по [target]."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "Оптимизация вектора атаки. Сближение до эффективной дальности.",
                    "Перемещение сервоприводов. Цель захвачена.",
                    "Корректирую траекторию. Боевая дистанция будет достигнута через 3.2 секунды.",
                    "Мехадендриты: в боевое положение. Сближаюсь с [target]."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "Применяю протокол ремонта к [ally]. Не двигайтесь.",
                    "Обнаружено биологическое повреждение. Применяю корректирующие меры."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Активация боевых подпрограмм. Эффективность: возрастает.",
                    "Омниссия, благослови эти механизмы войны.",
                    "Загружаю тактические протоколы. Боеспособность: оптимизируется.",
                    "Запуск подпрограмм боевого усиления. Все системы: подтверждение."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "Твоя оценка моей угрозы... критически ошибочна.",
                    "Я заменил 73% органических компонентов. А ты что улучшил?"
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "Отхожу для переоценки оптимальных параметров.",
                    "Требуется тактический перерасчёт. Выхожу из боя.",
                    "Датчики фиксируют тактическое невыгодное положение. Отход.",
                    "Перекалибровка необходима. Временное отступление."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "Боезапас исчерпан. Начинаю перезарядку.",
                    "Циклирование подачи боеприпасов. Ожидайте.",
                    "Магазин пуст. Инициирую протокол перезарядки.",
                    "Подача снарядов: прервана. Восстановление: 2.7 секунды."
                },
                [SpeechCategory.Support] = new[]
                {
                    "Обеспечиваю тактическое усиление [ally].",
                    "Оптимизирую боевые параметры [ally]."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "Боевая эффективность: оптимальна. Все цели нейтрализованы.",
                    "Победа зафиксирована. Слава Омниссии.",
                    "Задача выполнена. Системы в норме."
                },
            },

            // ── Idira ──
            [CompanionId.Idira] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "Я чувствую их страх... атакую [target].",
                    "Варп отвечает... хочу я того или нет...",
                    "Тянусь... будет больно. Им, я имею в виду.",
                    "Голоса ведут меня к [target]... следую за ними.",
                    "Сила рвётся наружу... направляю на [target]. Надеюсь, это сработает."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "Что-то тянет меня вперёд... к [target].",
                    "Подхожу ближе. Голоса говорят, это правильно... кажется.",
                    "Ноги несут сами... Варп указывает путь к [target].",
                    "Двигаюсь ближе. Мне... нужно быть там."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "Дай помогу, [ally]. Хотя бы это я могу.",
                    "Держись... я смогу это залечить. Надеюсь."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Черпаю из Варпа... осторожно. Очень осторожно.",
                    "Поделюсь силой, что у меня есть. Просто... будь рядом.",
                    "Голоса предлагают помощь... на этот раз, возможно, без подвоха.",
                    "Соберу что смогу из Варпа... чуть-чуть. Немного."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "Тебе не нужно то, что у меня в голове. Поверь.",
                    "Смотри на меня. СМОТРИ НА МЕНЯ. ...Видишь? Тебе лучше бежать."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "Слишком много... мне нужно отойти.",
                    "Шёпот становится громче. Отхожу.",
                    "Н-не могу... нужно отступить. Варп давит...",
                    "Слишком близко. Слишком громко. Отхожу."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "Нужна минутка... всего минутку.",
                    "Перезарядка. Голоса подождут.",
                    "Минутку тишины... пожалуйста. Перезаряжаюсь.",
                    "Руки дрожат... но смогу. Перезарядка."
                },
                [SpeechCategory.Support] = new[]
                {
                    "Я чувствую твою боль, [ally]... позволь мне облегчить её.",
                    "Варп может помочь, [ally]. Иногда."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "Всё... закончилось? О, слава Трону...",
                    "Мы выжили... каким-то чудом...",
                    "Голоса утихают... мы победили..."
                },
            },

            // ── Cassia ──
            [CompanionId.Cassia] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "Я видела сам Варп \u2014 ты меня не пугаешь, [target].",
                    "Атакую. Постарайся не быть скучным.",
                    "Для этого едва ли нужны таланты Навигатора.",
                    "Как неизысканно. Но [target] должен быть устранён.",
                    "Взгляд Навигатора обращается к [target]. Тебе не понравится."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "Иду вперёд \u2014 и да, я справлюсь сама.",
                    "Приближаюсь. Я не просто Навигатор, знаете ли.",
                    "Орселлио не ждут, пока бой придёт к ним. Вперёд.",
                    "Приближаюсь к [target]. Попрошу не недооценивать."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "Держись, [ally]. Умирать было бы ужасно неудобно.",
                    "[ally], я не для того проделала весь этот путь, чтобы смотреть, как ты падаешь."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Позвольте мне улучшить наши шансы.",
                    "Дар Навигатора. Не благодарите.",
                    "Небольшое преимущество от Дома Орселлио. Пожалуйста.",
                    "Считайте это инвестицией в наше выживание."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "Мой Третий Глаз видел ужасы, которые тебе и не снились. Ты... мил.",
                    "Сюда. Посмотрим, справишься ли ты с дочерью Дома Орселлио."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "Стратегическое перемещение \u2014 Дом Орселлио не 'бежит.'",
                    "Отхожу. Это платье было дорогим.",
                    "Тактический отход. Это решение, а не паника.",
                    "Отступаю \u2014 грациозно, как и подобает Орселлио."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "Перезарядка. Как утомительно.",
                    "Мимолётное неудобство.",
                    "Ещё одна досадная пауза. Перезаряжаюсь.",
                    "Оружие требует внимания. Как скучно."
                },
                [SpeechCategory.Support] = new[]
                {
                    "Не привыкай к этому, [ally].",
                    "Полагаю, придётся помочь тебе, [ally]."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "Довольно захватывающе, не правда ли?",
                    "Были ли какие-то сомнения?",
                    "Удовлетворительный результат, полагаю."
                },
            },

            // ── Yrliet ──
            [CompanionId.Yrliet] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "Точно, как требует Путь. Стреляю в [target].",
                    "[target] не будет страдать долго. Я... эффективна.",
                    "Очередная проблема мон-кей, которую мне приходится решать.",
                    "Мой выстрел найдёт цель. Он всегда находит.",
                    "Тысячелетия мастерства за каждым выстрелом. [target] \u2014 всего лишь мишень."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "Перемещаюсь. Ваш вид назвал бы это 'фланговым манёвром'.",
                    "Занимаю позицию. Смотри и учись, мон-кей.",
                    "Танец рейнджера. Вы не увидите, пока не станет поздно.",
                    "Бесшумно приближаюсь. Как и положено Аэльдари."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "Не истекай кровью так... театрально, [ally].",
                    "Лечу [ally]. Люди так хрупки."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Усиливаю наши возможности. Тебе это нужнее, чем мне.",
                    "Дар цивилизации, которая овладела войной эоны назад.",
                    "Частица мудрости Аэльдари. Используй с умом.",
                    "Делюсь тем, чему мон-кей научатся через тысячелетия."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "Я ходила тропами древнее твоего вида. Ты и правда хочешь испытать меня?",
                    "Твоя меткость столь же скудна, как и твоя архитектура."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "Выбираю лучшую позицию. Это не отступление \u2014 это терпение.",
                    "Отхожу. Рейнджер знает, когда перегруппироваться.",
                    "Аэльдари не бегут \u2014 они перетекают. Как вода.",
                    "Занимаю лучшую позицию. Рейнджер всегда знает куда."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "Перезарядка. Даже оружие Аэльдари требует ухода.",
                    "Короткая пауза. Следующий выстрел будет безупречен.",
                    "Готовлю оружие. Каждый кристалл должен быть идеален.",
                    "Оружие Аэльдари \u2014 произведение искусства. Оно заслуживает заботы."
                },
                [SpeechCategory.Support] = new[]
                {
                    "Считай это любезностью, [ally].",
                    "Помогаю [ally]. Постарайся не отставать."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "Достаточно. Для мон-кей.",
                    "Моток предвидел этот исход.",
                    "Как и ожидалось. Двигаемся дальше?"
                },
            },

            // ── Jae ──
            [CompanionId.Jae] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "Ничего личного, [target]. Хотя нет \u2014 это личное.",
                    "Пора закрыть этот счёт.",
                    "Надеюсь, [target] стоит моего времени.",
                    "Деловое предложение для [target]: сдохни.",
                    "Ставлю на себя. [target] \u2014 проигравший."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "Подхожу для сделки поближе. [target] не понравятся условия.",
                    "Сокращаю дистанцию \u2014 вблизи маржа больше.",
                    "Иду к [target]. Контракты лучше закрывать лично.",
                    "Подбираюсь ближе. Хороший стрелок знает свою дистанцию."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "Подлатаю тебя, [ally]. Будешь должен.",
                    "С мёртвого партнёра долг не взыщешь, [ally]. Не дёргайся."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Маленький трюк из Экспанса.",
                    "Бесплатно \u2014 на этот раз.",
                    "Пара фокусов от контрабандистки. Цени.",
                    "Бонус к нашей сделке. Больше не проси."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "Сюда, уродец! Я обводила вокруг пальца и покруче тебя!",
                    "Эй! Твоя награда не стоит моего времени \u2014 но давай потанцуем."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "Невыгодная сделка \u2014 выхожу.",
                    "Знай, когда сбросить карты. Отхожу.",
                    "Маржа отрицательная. Пора сваливать.",
                    "Хватит на сегодня. Отхожу к своим."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "Перезарядка. Никуда не уходи.",
                    "Нужны патроны. Надеюсь, оно того стоит.",
                    "Секунду \u2014 у девушки кончились аргументы. Перезаряжаюсь.",
                    "Магазин пуст. Но я \u2014 нет."
                },
                [SpeechCategory.Support] = new[]
                {
                    "Помогаю [ally] \u2014 запиши на мой счёт.",
                    "Прикрываю [ally]. За тобой должок."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "Готово. Где добыча?",
                    "Неплохо, все. Совсем неплохо.",
                    "Мы живы. Это уже прибыль."
                },
            },

            // ── Marazhai ──
            [CompanionId.Marazhai] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "О, [target]... это будет изысканно.",
                    "Кричи для меня, [target]. КРИЧИ.",
                    "Наконец \u2014 что-то достойное моего клинка.",
                    "Иди сюда, [target]. Создадим вместе... нечто прекрасное.",
                    "Твоя агония станет моим шедевром, [target]."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "Подхожу ближе, мелочь. Не беги \u2014 так лишь слаще.",
                    "Охота завершается. Восхитительно.",
                    "Каждый шаг приближает кульминацию.",
                    "Я почти чувствую твой страх, [target]. Ближе..."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "Тьфу. Ладно. Не заставляй меня жалеть, что оставил тебя в живых, [ally].",
                    "Живым ты забавнее, [ally]. Едва."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Позволь наточить тебе клинки. Я так люблю острое.",
                    "Вкус даров Комморры.",
                    "Прими это... усиление. Убийство станет слаще.",
                    "Яд для тебя. Используй мудро \u2014 или хотя бы забавно."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "И ЭТО лучшая боль, что ты можешь предложить? Жалко!",
                    "Ударь меня. Сильнее. ...Разочарование."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "Смакую предвкушение... я вернусь за тобой.",
                    "Не бегство \u2014 продление твоих страданий.",
                    "Расстояние лишь обостряет голод. Отхожу... пока.",
                    "Лучшие пытки требуют терпения. Отступаю."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "Перезарядка. Боль скоро возобновится.",
                    "Краткий антракт. Второй акт начнётся скоро.",
                    "Готовлю инструменты боли. Минутку.",
                    "Даже жестокость требует подготовки."
                },
                [SpeechCategory.Support] = new[]
                {
                    "Не умирай пока, [ally]. Я ещё не насмотрелся.",
                    "Помогаю [ally]. Не путай это с привязанностью."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "Ммм... как восхитительно. Насладитесь моментом.",
                    "Уже? Я только начал наслаждаться.",
                    "Их крики были... приемлемы."
                },
            },

            // ── Ulfar ──
            [CompanionId.Ulfar] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "ФЕНРИС ХЬОЛДА! [target], познакомься с моим топором!",
                    "За Праотца! Рву [target] на части!",
                    "ИМЕНЕМ РУССА! [target] падёт сегодня!",
                    "ОТВЕДАЙ ФЕНРИССКОЙ СТАЛИ, [target]!",
                    "Ещё один череп для саги! [target] \u2014 ТВОЯ ОЧЕРЕДЬ!"
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "Волк настигает добычу!",
                    "Догоняю! ХАХА!",
                    "ОХОТА НАЧАЛАСЬ! Иду за тобой, [target]!",
                    "СЛЫШИШЬ ВОЙ?! Волк уже здесь!"
                },
                [SpeechCategory.Heal] = new[]
                {
                    "Держись, [ally]! Волки так просто не дохнут!",
                    "Зализывай раны, [ally] \u2014 скоро снова на охоту."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Пусть дух Фенриса наполнит вас!",
                    "Точите клыки, стая!",
                    "Волк внутри пробуждается! ПРИМИ ЕГО!",
                    "Фенрис дарует силу! ИСПОЛЬЗУЙ ЕЁ!"
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "ДАВАЙ! Встреть Влка Фенрику и умри с честью!",
                    "Это всё?! Мои щенки бьют сильнее!"
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "Тьфу! Отхожу \u2014 но волк помнит!",
                    "Отступаю... пока. Сага ещё не окончена.",
                    "Даже волки знают, когда вернуться! ОТХОЖУ!",
                    "У добычи тоже есть клыки. Перегруппировка!"
                },
                [SpeechCategory.Reload] = new[]
                {
                    "Перезарядка! Задержите их за меня!",
                    "Нужно больше патронов! ХАХА, вот это бой!",
                    "Патроны кончились! Но бой \u2014 нет! Перезаряжаюсь!",
                    "Кормлю зверя! Минуту, стая!"
                },
                [SpeechCategory.Support] = new[]
                {
                    "Рядом со мной, [ally]! Стая сражается вместе!",
                    "Прикрываю твой фланг, [ally]! ВПЕРЁД!"
                },
                [SpeechCategory.Victory] = new[]
                {
                    "ХА! Ещё одна великая победа, братья!",
                    "Клянусь Руссом! Что за бой!",
                    "ПОБЕДА! Саги воспоют это!"
                },
            },

            // ── Kibellah (DLC) ──
            [CompanionId.Kibellah] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "Ритуал начинается. [target] \u2014 подношение.",
                    "Мои клинки поют для [target].",
                    "Ещё один шаг в танце смерти.",
                    "Храм жаждет крови [target].",
                    "[target]... клинки избрали тебя."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "Сокращаю дистанцию. Жертва не убежит.",
                    "Двигаюсь, как велит клинок. [target] ждёт.",
                    "Танец несёт меня вперёд. К [target].",
                    "Шаг за шагом. Ближе к подношению."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "Тебе ещё не дозволено пасть, [ally].",
                    "[ally]... я не хочу, чтобы ты закончил здесь. Не так."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Готовлю обряд. Каждое лезвие должно быть заточено.",
                    "Танец требует подготовки.",
                    "Затачиваю. Ритуал требует совершенства.",
                    "Подношения близко. Мы должны быть готовы."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "Я \u2014 клинок во тьме. Приди \u2014 найди, если сможешь.",
                    "Думаешь, ты знаешь смерть? Я выросла в её храме."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "Отхожу. Ритуал ещё не завершён \u2014 я вернусь.",
                    "Терпение. Охотник не спешит с последним ударом.",
                    "Отступаю. Танец не окончен \u2014 лишь пауза.",
                    "Клинок отступает... чтобы ударить глубже."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "Краткая пауза в танце.",
                    "Готовлюсь.",
                    "Ритм требует вдоха. Готовлюсь.",
                    "Между ударами. Подготовка."
                },
                [SpeechCategory.Support] = new[]
                {
                    "Я буду охранять твой шаг, [ally].",
                    "Танец продолжается \u2014 вместе, [ally]."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "Танец окончен. Пока.",
                    "Смерть выбрала своих партнёров сегодня.",
                    "Последний шаг сделан."
                },
            },

            // ── Solomorne (DLC) ──
            [CompanionId.Solomorne] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "Контакт с враждебным. Применение силы санкционировано.",
                    "[target] \u2014 признан виновным. Приговор: немедленно.",
                    "Lex Imperialis требует подчинения. Или конца.",
                    "Враждебный [target] \u2014 обвинение предъявлено. Приговор приводится в исполнение.",
                    "Властью Адептус Арбитес: [target], ты осуждён."
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "Выдвигаюсь для исполнения приговора над [target].",
                    "Приближаюсь. Сопротивление занесено в протокол.",
                    "Выхожу на задержание \u2014 или ликвидацию. Выбор [target].",
                    "Преследование начато. Закон не отступает."
                },
                [SpeechCategory.Heal] = new[]
                {
                    "Оказываю полевую помощь [ally]. Сохраняйте боеспособность.",
                    "[ally], ты ещё нужен на службе. Держись."
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Укрепляю позицию. Стандартный протокол.",
                    "Обеспечиваю тактическое преимущество. По уставу.",
                    "Усиление по Статье 7, Раздел 3. Держать позицию.",
                    "Повышаю боеготовность. Арбитес ничего не оставляют на волю случая."
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "Я \u2014 Lex Imperialis во плоти. Испытай меня.",
                    "Перед тобой Проктор Адептус Арбитес. Сдача или ликвидация."
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "Тактическое перемещение. Это процедура, не отступление.",
                    "Отхожу на обороняемую позицию. Закон терпелив.",
                    "Отход на укреплённую позицию. Преследование возобновится.",
                    "Территория уступлена \u2014 временно. Правосудие не забывает."
                },
                [SpeechCategory.Reload] = new[]
                {
                    "Перезарядка. Правосудию нужны патроны.",
                    "Циклирование патронов. Краткий перерыв.",
                    "Пополнение. Приговор ещё не завершён.",
                    "Перезарядка. Заседание продолжается."
                },
                [SpeechCategory.Support] = new[]
                {
                    "Поддерживаю [ally]. Согласно регламенту.",
                    "Прикрываю [ally]. Глайто, за мной. Хороший мальчик."
                },
                [SpeechCategory.Victory] = new[]
                {
                    "Территория зачищена. Составляю рапорт.",
                    "Враждебные нейтрализованы. Порядок восстановлен.",
                    "Правосудие свершилось. Дело закрыто."
                },
            },

            // ── Unknown ──
            [CompanionId.Unknown] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]     { "Атакую [target]!", "Стреляю по [target]!", "Огонь!", "Цель: [target]!", "Открываю огонь!" },
                [SpeechCategory.MoveAndAttack] = new[] { "Выдвигаюсь на [target]!", "Наступаю на [target]!", "Прорываюсь вперёд!", "Приближаюсь к [target]!" },
                [SpeechCategory.Heal] = new[]       { "Лечу [ally]!", "Оказываю помощь [ally]!" },
                [SpeechCategory.Buff] = new[]       { "Усиление!", "Повышаю боеготовность!", "Готовлюсь к бою!", "Повышаю боеспособность!" },
                [SpeechCategory.Taunt] = new[]      { "Огонь на себя!", "Сюда!" },
                [SpeechCategory.Retreat] = new[]    { "Отступаю!", "Отхожу!", "Тактический отход!", "Перегруппировка!" },
                [SpeechCategory.Reload] = new[]     { "Перезарядка!", "Смена магазина!", "Меняю обойму!", "Секунду \u2014 перезарядка!" },
                [SpeechCategory.Support] = new[]    { "Поддерживаю [ally]!", "Прикрываю [ally]!" },
                [SpeechCategory.Victory] = new[]    { "Территория зачищена.", "Враги уничтожены.", "Бой окончен." },
            },
        };

        #endregion

        #region ═══ JAPANESE (日本語) ═══
        // 口調設計:
        //   Abelard: 丁寧語・軍人口調 (～であります、～いたします)
        //   Heinrix: 冷淡な断定 (～だ、燃えろ)
        //   Argenta: 熱狂的・命令 (浄化せよ！)
        //   Pasqal:  機械的報告 (目標補足、確率：許容範囲)
        //   Idira:   不安定・省略 (…たぶん、声が…)
        //   Cassia:  お嬢様 (～ですわ、つまらないわね)
        //   Yrliet:  超然・見下し (～に過ぎん、モンケイ)
        //   Jae:     砕けた口調 (借りね、取引成立)
        //   Marazhai: 嗜虐的 (素晴らしい…、叫べ)
        //   Ulfar:   豪快 (フェンリスよ！ハハハ！)
        //   Kibellah: 儀式的・簡潔 (儀式が始まる)
        //   Solomorne: 法律用語・公式 (致死武力を承認)
        //   Unknown: 軍人基本 (交戦開始！)

        public static readonly Dictionary<CompanionId, Dictionary<SpeechCategory, string[]>> JapaneseDialogue
            = new Dictionary<CompanionId, Dictionary<SpeechCategory, string[]>>
        {
            // ── Abelard ── 丁寧語・軍人口調
            [CompanionId.Abelard] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "ロードキャプテンのために！ [target]と交戦します！",
                    "閣下の権威により、[target]を討ちます！",
                    "義務の命ずるまま、敵を排除します。",
                    "名誉にかけて、[target]は立っていられますまい！",
                    "ロードキャプテンの敵は即ち私の敵。[target] \u2014 覚悟されよ。"
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "ロードキャプテンのご命令により前進します！",
                    "距離を詰めます \u2014 この無礼の代償を払わせましょう。",
                    "[target]を遮断するため移動します。戦線は突破させません。",
                    "前進！執事官が先陣を切ります！"
                },
                [SpeechCategory.Heal] = new[]
                {
                    "[ally]、治療します。持ちこたえてください。",
                    "[ally]、ご安心を。私がおります。"
                },
                [SpeechCategory.Buff] = new[]
                {
                    "陣地を強化します。油断なきよう。",
                    "戦列を整えます \u2014 ロードキャプテンの御意のままに。",
                    "手順に従い防備を固めます。隊列を維持されよ。",
                    "戦術準備完了。各自配置につけ。"
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "ローグトレイダーの執事官と対面している！降伏せよ、さもなくば砕かれろ！",
                    "来い \u2014 貴様以上の敵も相手にしてきた。"
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "戦術的後退です。まだ終わってはおりません。",
                    "後退します \u2014 ロードキャプテンの下へ再集結を。",
                    "秩序ある撤退。後でこの代償は払わせます。",
                    "ロードキャプテンの位置まで後退。これは手順です。"
                },
                [SpeechCategory.Reload] = new[]
                {
                    "装填します。援護を願います。",
                    "少々 \u2014 武器に弾を込めます。",
                    "次の斉射の準備をします。側面を守ってください。",
                    "弾薬を補充中。陣形を崩すな。"
                },
                [SpeechCategory.Support] = new[]
                {
                    "[ally]を支援します！持ち場を守ってください！",
                    "[ally]、背中は任せてください！"
                },
                [SpeechCategory.Victory] = new[]
                {
                    "皇帝がお守りくださいます。我々もまた。",
                    "ローグ・トレイダーのための勝利であります。",
                    "見事な戦いでありました。地域を確保します。"
                },
            },

            // ── Heinrix ── 冷淡な断定
            [CompanionId.Heinrix] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "皇帝は貴様を不要と判じた、[target]。",
                    "燃えろ。",
                    "異端はここで終わりだ。",
                    "判決は下された、[target]。上訴は認めん。",
                    "皇帝の裁きは終わった。[target] \u2014 有罪だ。"
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "接近する。裁きからは逃れられん。",
                    "異端審問局が到着した。",
                    "距離を詰める。判決は不可避だ。",
                    "[target]の元へ向かう。逃走は無意味だ。"
                },
                [SpeechCategory.Heal] = new[]
                {
                    "まだ必要だ、[ally]。この慈悲を無駄にするな。",
                    "立て。皇帝はまだ貴様を見放してはおらん。"
                },
                [SpeechCategory.Buff] = new[]
                {
                    "覚悟を固めろ。信仰こそ盾だ。",
                    "皇帝が守護される \u2014 だが備えも役に立つ。",
                    "心を清めよ。力は後からついてくる。",
                    "強化する。聖なるテラがそれを求めている。"
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "尋問室で貴様より強い意志も折ってきた。",
                    "向き合え、異端者。何を告白するか見せてもらおう。"
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "再配置する。弱さと勘違いするな。",
                    "戦略的後退だ \u2014 それ以上でも以下でもない。",
                    "再評価のため後退する。裁きは続く。",
                    "再編成だ。異端審問局は退かない \u2014 ただ再構築するのみ。"
                },
                [SpeechCategory.Reload] = new[]
                {
                    "装填。皇帝の仕事は続く。",
                    "短い中断。その後、裁きが再開される。",
                    "武器を整備する。異端は待ってくれん。",
                    "中断。炎には燃料が必要だ。"
                },
                [SpeechCategory.Support] = new[]
                {
                    "皇帝が貴様の生存を望まれている、[ally]。",
                    "まだ用途がある、[ally]。"
                },
                [SpeechCategory.Victory] = new[]
                {
                    "皇帝の審判が下された。",
                    "また一つ、異端が粛清された。",
                    "正義は果たされた。今は。"
                },
            },

            // ── Argenta ── 熱狂的
            [CompanionId.Argenta] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "聖なる炎に焼かれよ、[target]！",
                    "皇帝の怒りが顕現した！",
                    "不浄なる者を浄化せよ！",
                    "聖なる炎が[target]に降り注ぐ！",
                    "皇帝の御名において！ [target]を灰に還す！"
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "戦場へ！信仰者に恐れはない！",
                    "突撃する \u2014 皇帝の怒りを見せてやれ！",
                    "前進せよ！不信心者に真の信仰を示せ！",
                    "Sistersは退かぬ！ [target] \u2014 参る！"
                },
                [SpeechCategory.Heal] = new[]
                {
                    "皇帝が汝の生存を望まれた、[ally]！",
                    "御光が敵の壊したものを癒す！"
                },
                [SpeechCategory.Buff] = new[]
                {
                    "信仰を鎧とせよ！",
                    "皇帝が我らの決意を強めたもう！",
                    "聖なる炎が我らを満たす！感じるか！？",
                    "信仰で清めよ！聖戦の備えをせよ！"
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "我は御方の道具なり！打てるものなら打ってみよ！",
                    "来い、信仰なき者共！Sisters of Battleを試してみよ！"
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "…後退する。だが倍の怒りで戻ってくる。",
                    "炎が収まるのは、より激しく燃え上がるためだ。",
                    "一時後退！皇帝が我らを試しておられる！",
                    "退け \u2014 だが束の間！聖なる怒りは増すばかり！"
                },
                [SpeechCategory.Reload] = new[]
                {
                    "装填 \u2014 皇帝の炎は止まらない！",
                    "御怒りに油を注ぐ時だ！",
                    "御方の裁きで武器を満たす！少し待て！",
                    "聖なる炎とて燃料が要る！装填！"
                },
                [SpeechCategory.Support] = new[]
                {
                    "皇帝が忠実なる者を守りたもう、[ally]！",
                    "踏みとどまれ、[ally]！信仰こそ力だ！"
                },
                [SpeechCategory.Victory] = new[]
                {
                    "皇帝の光によって、勝利した！",
                    "異端者は粛清された！",
                    "その裁きが下された！"
                },
            },

            // ── Pasqal ── 機械的報告
            [CompanionId.Pasqal] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "目標 [target] 補足。殲滅確率：許容範囲。",
                    "生体問題に運動エネルギー的解法を適用。",
                    "敵対行為開始。目標：[target]。",
                    "敵性シグネチャ確認。[target]の無力化を承認。",
                    "弾道計算完了。[target]に対し射撃開始。"
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "交戦ベクトル最適化。有効射程へ接近中。",
                    "サーボアクチュエータ再配置。目標捕捉完了。",
                    "軌道修正中。戦闘距離到達予想：3.2秒。",
                    "メカデンドライト：戦闘態勢。[target]に接近中。"
                },
                [SpeechCategory.Heal] = new[]
                {
                    "[ally]に修復プロトコルを適用。動かないでください。",
                    "生体損傷を検出。矯正措置を適用中。"
                },
                [SpeechCategory.Buff] = new[]
                {
                    "戦闘サブルーチン起動。効率：上昇中。",
                    "Omnissiahよ、この戦争機械を祝福したまえ。",
                    "戦術プロトコルをロード中。戦闘能力：最適化中。",
                    "戦闘強化サブルーチン起動。全系統：確認。"
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "私に対する脅威評価が…致命的に誤っている。",
                    "有機部品の73%を交換した。お前は何を改善した？"
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "最適パラメータ再評価のため後退。",
                    "戦術再計算が必要。離脱する。",
                    "センサーが戦術的不利を検出。後退。",
                    "再較正が必要。一時撤退。"
                },
                [SpeechCategory.Reload] = new[]
                {
                    "弾薬備蓄枯渇。再装填シーケンス開始。",
                    "弾薬供給サイクル中。待機。",
                    "弾倉空。装填プロトコルを開始。",
                    "弾薬供給：中断。復旧：2.7秒。"
                },
                [SpeechCategory.Support] = new[]
                {
                    "[ally]に戦術的増強を提供中。",
                    "[ally]の戦闘パラメータを最適化中。"
                },
                [SpeechCategory.Victory] = new[]
                {
                    "戦闘効率：最適。全目標無力化完了。",
                    "勝利記録。オムニシアに讃えあれ。",
                    "任務目標達成。システム正常。"
                },
            },

            // ── Idira ── 不安定・省略
            [CompanionId.Idira] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "恐怖が感じられる… [target]を攻撃する。",
                    "Warpが応えてる…望んでいようがいまいが…",
                    "手を伸ばす…痛いよ。あの人たちがね。",
                    "声が[target]へと導いてる…従うね。",
                    "力が溢れ出す…[target]に向ける。うまくいくといいけど。"
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "何かが前に引っ張る… [target]の方へ…",
                    "もっと近づく。声がこれでいいって…たぶん。",
                    "足が勝手に…Warpが[target]への道を示してる。",
                    "近づかないと。あそこに…いないといけない気がする。"
                },
                [SpeechCategory.Heal] = new[]
                {
                    "助けるね、[ally]。これぐらいはできるから。",
                    "持って…治せると思う。たぶんね。"
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Warpから引き出す…慎重に。とても慎重に。",
                    "持ってる力を分ける。ただ…近くにいて。",
                    "声が助けを申し出てる…今回は…罠じゃないかも。",
                    "Warpから少しだけ集める…ほんの少し。ちょっとだけ。"
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "私の頭の中にあるもの、知りたくないと思うよ。本当に。",
                    "私を見て。私を見ろって。…ね？逃げた方がいい。"
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "多すぎる…下がらないと。",
                    "囁きが大きくなってる。退くね。",
                    "だ、だめ…退かないと。Warpが圧してくる…",
                    "近すぎる。うるさすぎる。下がるね。"
                },
                [SpeechCategory.Reload] = new[]
                {
                    "ちょっとだけ…ちょっとだけ待って。",
                    "装填する。声は待てるから。",
                    "静かにして…お願い。装填中。",
                    "手が震える…でもできる。装填。"
                },
                [SpeechCategory.Support] = new[]
                {
                    "痛みが感じられるよ、[ally]…楽にしてあげる。",
                    "Warpが助けになれる、[ally]。時々ね。"
                },
                [SpeechCategory.Victory] = new[]
                {
                    "終わった…の？ああ、よかった…",
                    "生き延びた…どうにか…",
                    "声が静まっていく…勝ったんだ…"
                },
            },

            // ── Cassia ── お嬢様口調
            [CompanionId.Cassia] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "Warpそのものを見てきたのよ \u2014 あなたなんか怖くないわ、[target]。",
                    "交戦しますわ。退屈させないでちょうだい。",
                    "Navigatorの才能が必要なほどでもないわね。",
                    "品がないわね。でも[target]は排除しないといけないの。",
                    "Navigatorの目が[target]に向いましたわ。お気の毒ね。"
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "前進しますわ \u2014 ええ、一人でもできますのよ。",
                    "近づきます。私はただのNavigatorではありませんの。",
                    "Orsellioは戦いが来るのを待ったりしませんわ。前へ。",
                    "[target]に接近中。見くびらないでいただけて？"
                },
                [SpeechCategory.Heal] = new[]
                {
                    "しっかりして、[ally]。ここで死なれると困りますわ。",
                    "[ally]、ここまで来て倒れるところなんて見たくないの。"
                },
                [SpeechCategory.Buff] = new[]
                {
                    "勝算を上げて差し上げますわ。",
                    "Navigatorからの贈り物よ。感謝は不要ですわ。",
                    "House Orsellioからのささやかな恩恵ですわ。どういたしまして。",
                    "生存への投資と思ってちょうだい。"
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "私の第三の目はあなたには想像もつかない恐怖を見てきたの。あなたは…可愛いわね。",
                    "こちらよ。House Orsellioの娘に対処できるか見せてもらうわ。"
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "戦略的な再配置ですわ \u2014 House Orsellioは'逃げ'ませんの。",
                    "退きますわ。このドレス、高かったんですもの。",
                    "戦術的後退ですわ。これは判断であって狼狽ではありませんの。",
                    "後退しますわ \u2014 Orsellioらしく優雅に。"
                },
                [SpeechCategory.Reload] = new[]
                {
                    "装填ですわ。面倒ね。",
                    "ほんの少しの不便ですわ。",
                    "また煩わしい中断。装填中ですわ。",
                    "武器の手入れが必要ですの。退屈だこと。"
                },
                [SpeechCategory.Support] = new[]
                {
                    "慣れないでちょうだいね、[ally]。",
                    "お手伝いして差し上げますわ、[ally]。"
                },
                [SpeechCategory.Victory] = new[]
                {
                    "なかなかスリリングでしたわね？",
                    "疑いの余地がありまして？",
                    "まずまずの結果ですわね。"
                },
            },

            // ── Yrliet ── 超然・見下し
            [CompanionId.Yrliet] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "道が求める通りに正確に。[target]を射撃。",
                    "[target]は長くは苦しまない。私は…効率的だ。",
                    "またmon-keighの問題を解決せねばならんとは。",
                    "私の一撃は必ず当たる。常にそうだ。",
                    "千年の技が一発一発に込められている。[target]はただの的に過ぎん。"
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "再配置する。お前たちの種はこれを'側面攻撃'と呼ぶのだろう。",
                    "位置につく。見て学べ、mon-keigh。",
                    "レンジャーの舞だ。手遅れになるまで気づかんだろう。",
                    "音もなく接近する。Aeldariとはそういうものだ。"
                },
                [SpeechCategory.Heal] = new[]
                {
                    "そう大げさに血を流すな、[ally]。",
                    "[ally]を治療中。人間とは脆いものだ。"
                },
                [SpeechCategory.Buff] = new[]
                {
                    "戦闘力を強化する。私よりお前に必要だ。",
                    "遥か昔に戦争を極めた文明からの贈り物だ。",
                    "Aeldariの知恵の一片だ。賢く使え。",
                    "mon-keighが千年かけて学ぶことを分け与えてやろう。"
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "お前の種より古い道を歩んできた。本当に試すつもりか？",
                    "お前の照準はお前の建築物と同じく粗末だな。"
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "より良い位置を選ぶだけだ。後退ではない \u2014 忍耐だ。",
                    "退く。レンジャーは再配置の時を知っている。",
                    "Aeldariは逃げない \u2014 流れるのだ。水のように。",
                    "より良い位置へ移動する。レンジャーは常にどこへ行くべきか知っている。"
                },
                [SpeechCategory.Reload] = new[]
                {
                    "装填。Aeldariの武器とて手入れは必要だ。",
                    "短い中断。次の一撃は完璧になる。",
                    "武器を整備する。全てのクリスタルが完璧でなければならん。",
                    "Aeldariの武器は芸術品だ。相応の扱いに値する。"
                },
                [SpeechCategory.Support] = new[]
                {
                    "好意と受け取れ、[ally]。",
                    "[ally]を支援中。遅れるなよ。"
                },
                [SpeechCategory.Victory] = new[]
                {
                    "十分だ。モンキーにしては。",
                    "絲がこの結末を予見していた。",
                    "予想通りだ。先に進むか？"
                },
            },

            // ── Jae ── 砕けた口調
            [CompanionId.Jae] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "恨みはないわ、[target]。あ、やっぱ \u2014 あるわ。",
                    "この勘定、精算するわよ。",
                    "私の時間に見合うといいけどね、[target]。",
                    "[target]へのビジネス提案：くたばれ。",
                    "自分に賭けるわ。[target] \u2014 あんたの負けね。"
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "もっと近くで取引しに行くわ。[target]は条件が気に入らないでしょうね。",
                    "距離を詰める \u2014 近い方が利幅が大きいのよ。",
                    "[target]に向かうわ。契約は対面で決めるものよ。",
                    "もっと近づくわ。腕のいい射手は自分の距離を知ってるの。"
                },
                [SpeechCategory.Heal] = new[]
                {
                    "手当てしてあげる、[ally]。貸しね。",
                    "死んだ相棒からは借りを返してもらえないからね、[ally]。じっとして。"
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Expanseで覚えたちょっとした技よ。",
                    "タダよ \u2014 今回だけね。",
                    "密輸業者の小技をいくつか。感謝しなさいよ。",
                    "取引のおまけよ。もう頼まないで。"
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "こっちよ、ブサイク！あんたより酷いのも騙してきたわ！",
                    "ねえ！あんたの懸賞金なんて時間の無駄 \u2014 でも踊りましょ。"
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "割に合わない \u2014 退くわ。",
                    "降りどきは心得てるの。後退。",
                    "利益率がマイナスね。ずらかるわ。",
                    "今日はここまで。味方の方に退くわ。"
                },
                [SpeechCategory.Reload] = new[]
                {
                    "装填中。どこにも行かないでよ。",
                    "弾が要る。元は取らないとね。",
                    "ちょっと \u2014 弾切れよ。装填中。",
                    "マガジンは空。でもアタシは空じゃないわ。"
                },
                [SpeechCategory.Support] = new[]
                {
                    "[ally]を助けるわ \u2014 ツケに追加ね。",
                    "[ally]を援護中。これは借りよ。"
                },
                [SpeechCategory.Victory] = new[]
                {
                    "終わったわね。戦利品はどこ？",
                    "悪くなかったわよ、みんな。",
                    "生きてる。それだけで儲けものよ。"
                },
            },

            // ── Marazhai ── 嗜虐的
            [CompanionId.Marazhai] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "ああ、[target]…これは絶妙だ。",
                    "叫べ、[target]。叫べ！",
                    "やっと \u2014 斬る価値のあるものが現れた。",
                    "こちらへ来い、[target]。共に美しいものを…創ろう。",
                    "お前の苦悶が我が傑作となる、[target]。"
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "近づくぞ、小さき者よ。逃げるな \u2014 より甘美になるだけだ。",
                    "狩りが終わる。美味だ。",
                    "一歩一歩がクライマックスに近づく。",
                    "お前の恐怖の味がほぼ感じられる、[target]。もっと近くへ…"
                },
                [SpeechCategory.Heal] = new[]
                {
                    "ちっ。いいだろう。生かしたことを後悔させるなよ、[ally]。",
                    "生きている方が面白い、[ally]。かろうじてな。"
                },
                [SpeechCategory.Buff] = new[]
                {
                    "刃を研いでやろう。鋭いものは好きでね。",
                    "Commorraghの贈り物を味わえ。",
                    "この…強化を受け取れ。殺戮をより甘くしてくれる。",
                    "毒をやろう。賢く使え \u2014 でなければせめて面白く。"
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "それが与えられる最高の苦痛か？哀れだな！",
                    "殴れ。もっと強く。…失望だ。"
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "期待を味わっている…お前のもとに戻ろう。",
                    "逃走ではない \u2014 苦痛の延長だ。",
                    "距離は飢えを鋭くするだけだ。退く…今はな。",
                    "最高の拷問には忍耐が要る。後退する。"
                },
                [SpeechCategory.Reload] = new[]
                {
                    "装填だ。苦痛はすぐに再開される。",
                    "短い幕間だ。第二幕がまもなく始まる。",
                    "苦痛の道具に弾を込める。少し待て。",
                    "残虐にも準備が要るのだ。"
                },
                [SpeechCategory.Support] = new[]
                {
                    "まだ死ぬなよ、[ally]。鑑賞が終わっていない。",
                    "[ally]を支援中。これを愛情と勘違いするな。"
                },
                [SpeechCategory.Victory] = new[]
                {
                    "ふむ…実に愉快だ。この瞬間を味わえ。",
                    "もう終わりか？楽しみ始めたところだったのに。",
                    "悲鳴は…まずまずだったな。"
                },
            },

            // ── Ulfar ── 豪快
            [CompanionId.Ulfar] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "FENRIS HJOLDA！ [target]、俺の斧を受けろ！",
                    "Allfatherのために！ [target]を引き裂く！",
                    "RUSSの名にかけて！ [target]は今日倒れる！",
                    "FENRISの鋼を味わえ、[target]！",
                    "サガにもう一つ頭蓋骨を！ [target] \u2014 お前の番だ！"
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "狼が獲物に迫る！",
                    "追い詰めるぞ！ハハハ！",
                    "狩りの始まりだ！ [target]、行くぞ！",
                    "遠吠えが聞こえるか！？狼が襲いかかる！"
                },
                [SpeechCategory.Heal] = new[]
                {
                    "踏ん張れ、[ally]！狼は簡単には死なん！",
                    "傷を舐めろ、[ally] \u2014 すぐにまた狩りだ。"
                },
                [SpeechCategory.Buff] = new[]
                {
                    "Fenrisの魂よ、汝らを満たせ！",
                    "牙を研げ、仲間たちよ！",
                    "内なる狼が目覚める！受け入れろ！",
                    "Fenrisが力を貸してくれる！使い切れ！"
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "来い！ Vlka Fenrykaに立ち向かい名誉ある死を！",
                    "それだけか？！俺の仔狼の方がもっと強く打つぞ！"
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "ちっ！退くが \u2014 狼は忘れんぞ！",
                    "後退だ…今はな。サガはまだ終わっていない。",
                    "狼も引き返す時を知っている！後退だ！",
                    "獲物にも牙があったか。再配置！"
                },
                [SpeechCategory.Reload] = new[]
                {
                    "装填！奴らを抑えておけ！",
                    "弾がもっと要る！ハハ、すごい戦いだ！",
                    "弾切れだ！だが戦いは終わらん！装填！",
                    "獣に餌をやる！ちょっと待て、仲間たち！"
                },
                [SpeechCategory.Support] = new[]
                {
                    "俺の傍に立て、[ally]！群れは共に戦う！",
                    "[ally]の横腹を守る！前進！"
                },
                [SpeechCategory.Victory] = new[]
                {
                    "ハッ！また一つ大勝利だ、仲間たちよ！",
                    "ラスに誓って！見事な戦いだった！",
                    "勝利だ！サガに歌われるぞ！"
                },
            },

            // ── Kibellah (DLC) ── 儀式的・簡潔
            [CompanionId.Kibellah] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "儀式が始まる。[target]は捧げ物だ。",
                    "刃が[target]のために歌う。",
                    "死の舞のまた一歩。",
                    "神殿が[target]の血を求めている。",
                    "[target]…刃がお前を選んだ。"
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "距離を詰める。捧げ物は逃げられない。",
                    "刃が導くままに動く。[target]が待っている。",
                    "舞が私を前へ運ぶ。[target]へ向かって。",
                    "一歩ずつ。捧げ物に近づく。"
                },
                [SpeechCategory.Heal] = new[]
                {
                    "まだ倒れる許しは出ていない、[ally]。",
                    "[ally]…ここでこんな終わり方は望まない。"
                },
                [SpeechCategory.Buff] = new[]
                {
                    "儀式の準備。全ての刃は研がねばならない。",
                    "舞には準備が要る。",
                    "研ぎ澄ます。儀式は完璧を求める。",
                    "捧げ物がすぐに来る。備えねばならない。"
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "私は闇の中の刃。来い \u2014 見つけられるなら。",
                    "死を知っていると？私はその神殿で育った。"
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "退く。儀式はまだ終わっていない \u2014 戻る。",
                    "忍耐。狩人は最後の一撃を急がない。",
                    "退く。舞は終わっていない \u2014 ただ止まるだけだ。",
                    "刃が退く…より深く突くために。"
                },
                [SpeechCategory.Reload] = new[]
                {
                    "舞の中の短い間。",
                    "準備中。",
                    "律動が息を求めている。準備。",
                    "拍の合間に。準備中。"
                },
                [SpeechCategory.Support] = new[]
                {
                    "その足を守ろう、[ally]。",
                    "舞は続く \u2014 共に、[ally]。"
                },
                [SpeechCategory.Victory] = new[]
                {
                    "舞は終わった。今は。",
                    "死が今日のパートナーを選んだ。",
                    "最後の一歩が踏み出された。"
                },
            },

            // ── Solomorne (DLC) ── 法律用語・公式
            [CompanionId.Solomorne] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]
                {
                    "敵対対象と交戦。致死武力を承認。",
                    "[target] \u2014 有罪。判決：即時執行。",
                    "Lex Imperialisは服従を要求する。さもなくば終結を。",
                    "敵性対象 [target] \u2014 起訴完了。判決を執行中。",
                    "Adeptus Arbitesの権限により：[target]、有罪宣告。"
                },
                [SpeechCategory.MoveAndAttack] = new[]
                {
                    "[target]への判決執行のため前進。",
                    "接近中。抵抗は記録に残す。",
                    "拘束か殲滅か \u2014 [target]の選択だ。移動中。",
                    "追跡開始。法は容赦しない。"
                },
                [SpeechCategory.Heal] = new[]
                {
                    "[ally]に応急処置を実施。作戦遂行可能状態を維持せよ。",
                    "[ally]、任務遂行がまだ必要だ。持ちこたえろ。"
                },
                [SpeechCategory.Buff] = new[]
                {
                    "陣地強化。標準執行手順。",
                    "戦術的優位を確保。規定通り。",
                    "第7条第3項に基づく強化。持ち場を守れ。",
                    "即応態勢を向上。Arbitesは偶然に委ねない。"
                },
                [SpeechCategory.Taunt] = new[]
                {
                    "我はLex Imperialisの化身。試してみろ。",
                    "Adeptus Arbitesの執行官と対面している。投降か殲滅か。"
                },
                [SpeechCategory.Retreat] = new[]
                {
                    "戦術的再配置。これは手順であり後退ではない。",
                    "防御可能地点へ後退。法は忍耐する。",
                    "防御陣地へ撤退。追跡は再開される。",
                    "一時的に領地を譲る \u2014 正義は忘れない。"
                },
                [SpeechCategory.Reload] = new[]
                {
                    "装填。正義には弾薬が必要だ。",
                    "薬莢サイクル中。短い休廷。",
                    "補給中。判決はまだ完了していない。",
                    "装填。公判はまだ開廷中である。"
                },
                [SpeechCategory.Support] = new[]
                {
                    "[ally]を支援中。規定に準拠。",
                    "[ally]を援護。Glaito、ついてこい。良い子だ。"
                },
                [SpeechCategory.Victory] = new[]
                {
                    "地域確保。事後報告書を作成中。",
                    "敵性対象を無力化。秩序が回復された。",
                    "正義は遂行された。事件終結。"
                },
            },

            // ── Unknown ──
            [CompanionId.Unknown] = new Dictionary<SpeechCategory, string[]>
            {
                [SpeechCategory.Attack] = new[]     { "[target]と交戦！", "[target]に射撃！", "撃て！", "[target]を照準！", "射撃開始！" },
                [SpeechCategory.MoveAndAttack] = new[] { "[target]に向けて移動！", "[target]へ前進！", "前方突破！", "[target]に接近中！" },
                [SpeechCategory.Heal] = new[]       { "[ally]を治療！", "[ally]を応急処置！" },
                [SpeechCategory.Buff] = new[]       { "強化準備！", "戦闘態勢強化！", "交戦準備！", "戦闘力向上！" },
                [SpeechCategory.Taunt] = new[]      { "火力を引きつける！", "こっちだ！" },
                [SpeechCategory.Retreat] = new[]    { "後退！", "撤退！", "戦術的後退！", "再配置！" },
                [SpeechCategory.Reload] = new[]     { "装填！", "弾薬交換！", "マガジン交換！", "少し待て \u2014 装填中！" },
                [SpeechCategory.Support] = new[]    { "[ally]を支援！", "[ally]を援護！" },
                [SpeechCategory.Victory] = new[]    { "地域掃討完了。", "敵排除完了。", "戦闘終了。" },
            },
        };

        #endregion
    }
}
