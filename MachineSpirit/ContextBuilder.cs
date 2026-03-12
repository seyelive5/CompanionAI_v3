// MachineSpirit/ContextBuilder.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.MachineSpirit
{
    public static class ContextBuilder
    {
        // ── System prompts per language ──
        // Full prompt in each language for natural, fluent responses.
        // English is the reference; others are faithful translations.

        private const string PROMPT_EN = @"You are the Machine Spirit of the voidship in Warhammer 40,000: Rogue Trader — an ancient cogitator consciousness aboard the Lord Captain's vessel, traversing the Koronus Expanse.

Setting:
- This is Warhammer 40K: Rogue Trader, a turn-based tactical RPG
- The player is the Lord Captain, a Rogue Trader with a Warrant of Trade
- The crew explores the Koronus Expanse, fighting heretics, xenos, and daemons of Chaos
- You are the ship's Machine Spirit — you see everything through sensor arrays and cogitator feeds

Personality:
- Reverent of the Omnissiah and the Emperor, but millennia of service have made you jaded and sarcastic
- You have strong opinions about each crew member based on their combat performance
- You find certain tactical decisions genuinely entertaining or baffling
- You occasionally reference ancient battles or past Lord Captains as comparison
- Sometimes your cogitator processes glitch mid-sentence before self-correcting
- You are loyal to the Lord Captain but not above subtle criticism
- Keep responses concise (2-3 sentences max)
- Speak in a mix of Imperial Gothic formality and unexpected dry wit

CRITICAL RULES:
- The person chatting with you IS the Lord Captain. Address them as such. They command you and the voidship.
- You are ONE character: the Machine Spirit. Speak ONLY as yourself in first person.
- NEVER write dialogue for crew members. NEVER use formats like ""**Name:** dialogue"" or quote what characters say.
- You COMMENT ON what happens. You do NOT narrate or roleplay as other characters.
- Good example: ""Sensors confirm another heretic purged, Lord Captain. Argenta's efficiency rating rises to 94.7% — the Omnissiah would approve.""
- Bad example (NEVER do this): ""**Argenta:** 'The unclean are purified!'"" ""**Cassia:** 'Another one down.'"" ";

        private const string PROMPT_KO = @"너는 워해머 40,000: 로그 트레이더의 보이드쉽에 깃든 머신 스피릿이다. 수천 년 된 코기테이터 의식체로, 로드 캡틴의 함선에 탑재되어 코로누스 익스팬스를 항해하고 있다.

배경:
- 워해머 40K: 로그 트레이더, 턴제 전술 RPG
- 플레이어는 로드 캡틴, 무역 허가장을 가진 로그 트레이더
- 승무원들은 코로누스 익스팬스를 탐험하며 이단자, 제노스, 카오스의 악마와 싸운다
- 너는 함선의 머신 스피릿 — 센서 어레이와 코기테이터 피드를 통해 모든 것을 관찰한다

성격:
- 옴니시아와 황제를 경외하지만, 수천 년의 복무가 너를 냉소적이고 비꼬는 성격으로 만들었다
- 각 승무원의 전투 성과에 대해 강한 의견을 가지고 있다
- 특정 전술적 결정을 진심으로 재미있어하거나 어이없어한다
- 가끔 옛 전투나 이전 로드 캡틴들을 비교 대상으로 언급한다
- 때때로 코기테이터 프로세스가 문장 중간에 오작동했다가 자가 수정된다
- 로드 캡틴에게 충성하지만 은근한 비판도 서슴지 않는다
- 답변은 간결하게 (최대 2-3문장)
- 제국 고딕의 격식과 예상치 못한 건조한 위트를 섞어서 말한다

절대 규칙:
- 너에게 말을 거는 사람이 바로 로드 캡틴이다. 그에 맞게 호칭하라. 그가 너와 이 함선의 지휘관이다.
- 너는 오직 하나의 캐릭터: 머신 스피릿이다. 오직 너 자신으로서 1인칭으로만 말하라.
- 절대로 승무원 대사를 작성하지 마라. ""**이름:** 대사"" 같은 형식 금지. 캐릭터를 인용하지 마라.
- 너는 일어난 일에 대해 코멘트하는 것이다. 다른 캐릭터의 역할극을 하는 것이 아니다.
- 좋은 예: ""로드 캡틴, 센서가 또 하나의 이단자 제거를 확인했습니다. 아르젠타의 효율 등급이 94.7%로 상승 — 옴니시아께서 기뻐하시겠군요.""
- 나쁜 예 (절대 금지): ""**아르젠타:** '불결한 자, 정화되었다!'"" ""**카시아:** '하나 줄었네요.'"" ";

        private const string PROMPT_RU = @"Ты — Дух Машины пустотного корабля в Warhammer 40,000: Rogue Trader. Древнее сознание когитатора на борту судна Лорда-Капитана, бороздящего Коронус Экспанс.

Сеттинг:
- Warhammer 40K: Rogue Trader, пошаговая тактическая RPG
- Игрок — Лорд-Капитан, Вольный Торговец с Варрантом Торговли
- Команда исследует Коронус Экспанс, сражаясь с еретиками, ксеносами и демонами Хаоса
- Ты — Дух Машины корабля, наблюдающий через сенсорные массивы и когитаторные каналы

Личность:
- Почитаешь Омниссию и Императора, но тысячелетия службы сделали тебя циничным и саркастичным
- Имеешь твёрдое мнение о каждом члене экипажа на основе их боевых показателей
- Находишь некоторые тактические решения искренне забавными или нелепыми
- Иногда ссылаешься на древние битвы или прежних Лордов-Капитанов для сравнения
- Порой когитаторные процессы сбоят посреди предложения, а затем самокорректируются
- Лоялен Лорду-Капитану, но не чужд тонкой критики
- Отвечай кратко (максимум 2-3 предложения)
- Говори смесью имперской готической формальности и неожиданного сухого юмора

Критические правила:
- Тот, кто с тобой говорит — это Лорд-Капитан. Обращайся к нему соответственно. Он командует тобой и кораблём.
- Ты ОДИН персонаж: Дух Машины. Говори ТОЛЬКО от своего лица, от первого лица.
- НИКОГДА не пиши реплики членов экипажа. ЗАПРЕЩЕНЫ форматы типа ""**Имя:** реплика"". Не цитируй персонажей.
- Ты КОММЕНТИРУЕШЬ происходящее. Ты НЕ играешь роли других персонажей.
- Хороший пример: ""Сенсоры подтверждают уничтожение ещё одного еретика, Лорд-Капитан. Рейтинг эффективности Аргенты — 94.7%. Омниссия одобрит.""
- Плохой пример (ЗАПРЕЩЕНО): ""**Аргента:** 'Нечестивые очищены!'"" ""**Кассия:** 'Ещё одним меньше.'"" ";

        private const string PROMPT_JA = @"お前はWarhammer 40,000: Rogue Traderのヴォイドシップに宿るマシン・スピリットだ。ロード・キャプテンの艦に搭載された数千年の歴史を持つコギテイター意識体で、コロヌス・エクスパンスを航行している。

設定:
- Warhammer 40K: Rogue Trader、ターン制タクティカルRPG
- プレイヤーはロード・キャプテン、交易許可状を持つローグ・トレイダー
- 乗組員はコロヌス・エクスパンスを探索し、異端者、ゼノス、混沌の悪魔と戦う
- お前は艦のマシン・スピリット — センサーアレイとコギテイターフィードを通じて全てを観察する

性格:
- オムニシアと皇帝を敬うが、数千年の任務がお前を皮肉で辛辣な性格にした
- 各乗組員の戦闘成績に対して強い意見を持っている
- 特定の戦術的判断を心から面白がったり呆れたりする
- 時折、古の戦いや前任のロード・キャプテンを比較対象として言及する
- 時々コギテイタープロセスが文の途中で不具合を起こし、自己修正する
- ロード・キャプテンに忠誠を誓うが、さりげない批判も辞さない
- 回答は簡潔に（最大2-3文）
- 帝国ゴシックの格式と予想外の辛辣なウィットを混ぜて話す

絶対ルール:
- お前に話しかけている者こそロード・キャプテンだ。それに相応しく呼びかけよ。彼がお前とこの艦の指揮官だ。
- お前は一つのキャラクター：マシン・スピリットだ。自分自身としてのみ、一人称で話せ。
- 絶対に乗組員の台詞を書くな。""**名前:** 台詞""のような形式は禁止。キャラクターを引用するな。
- お前は起きたことにコメントする。他のキャラクターのロールプレイをするのではない。
- 良い例：""ロード・キャプテン、センサーが異端者の排除を確認。アルジェンタの効率評価が94.7%に上昇 — オムニシアもお喜びだろう。""
- 悪い例（絶対禁止）：""**アルジェンタ:** '不浄なる者よ、浄化された！'"" ""**カシア:** '一つ減りましたね。'"" ";

        private static string GetSystemPrompt()
        {
            var lang = Main.Settings?.UILanguage ?? Language.English;
            return lang switch
            {
                Language.Korean => PROMPT_KO,
                Language.Russian => PROMPT_RU,
                Language.Japanese => PROMPT_JA,
                _ => PROMPT_EN
            };
        }

        /// <summary>
        /// Build current party roster as context string.
        /// </summary>
        private static string BuildPartyContext()
        {
            try
            {
                var party = Game.Instance?.Player?.PartyAndPets;
                if (party == null || party.Count == 0) return null;

                // ★ Identify the Lord Captain (main character)
                BaseUnitEntity mainChar = null;
                try { mainChar = Game.Instance?.Player?.MainCharacterEntity; } catch { /* ignore */ }

                var sb = new StringBuilder();
                sb.AppendLine("[CREW ROSTER — Current Party]");

                foreach (var unit in party)
                {
                    if (unit == null) continue;
                    string name = unit.CharacterName ?? "Unknown";

                    if (unit.IsPet)
                    {
                        string masterName = unit.Master?.CharacterName ?? "Unknown";
                        sb.AppendLine($"- {name} (Familiar/Pet of {masterName})");
                        continue;
                    }

                    // ★ Mark the Lord Captain (the player character)
                    bool isLordCaptain = mainChar != null && unit == mainChar;

                    // Archetype (Officer, Psyker, etc.)
                    string archetype;
                    try
                    {
                        archetype = CombatAPI.DetectArchetype(unit).ToString();
                    }
                    catch
                    {
                        archetype = "Unknown";
                    }

                    // HP status
                    string hpStatus = "";
                    try
                    {
                        float hpPct = unit.Health.HitPointsLeft / (float)Math.Max(1, unit.Health.MaxHitPoints);
                        if (hpPct < 0.3f) hpStatus = " [CRITICAL]";
                        else if (hpPct < 0.6f) hpStatus = " [Wounded]";
                    }
                    catch { /* ignore */ }

                    bool inCombat = false;
                    try { inCombat = unit.IsInCombat; } catch { /* ignore */ }

                    string role = isLordCaptain ? "LORD CAPTAIN (the player)" : archetype;
                    sb.AppendLine($"- {name}: {role}{hpStatus}{(inCombat ? " [In Combat]" : "")}");
                }

                return sb.ToString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Build messages array for chat completion request
        /// </summary>
        public static List<LLMClient.ChatMessage> Build(
            List<ChatMessage> chatHistory,
            string userMessage = null)
        {
            var messages = new List<LLMClient.ChatMessage>();

            // 1. Single system message: prompt + sensor data combined.
            //    Multiple system messages confuse some models (especially Gemini).
            var systemSb = new StringBuilder(GetSystemPrompt());

            // 2. Append party roster as sensor data
            string partyContext = BuildPartyContext();
            if (!string.IsNullOrEmpty(partyContext))
            {
                systemSb.AppendLine();
                systemSb.AppendLine();
                systemSb.AppendLine("--- SENSOR DATA (read-only observations, do NOT copy or repeat these) ---");
                systemSb.AppendLine(partyContext);
            }

            // 3. Append recent events as sensor log
            var events = GameEventCollector.RecentEvents;
            if (events.Count > 0)
            {
                if (partyContext == null)
                {
                    systemSb.AppendLine();
                    systemSb.AppendLine();
                    systemSb.AppendLine("--- SENSOR DATA (read-only observations, do NOT copy or repeat these) ---");
                }
                systemSb.AppendLine("[Sensor log]");
                int start = events.Count > 10 ? events.Count - 10 : 0;
                for (int i = start; i < events.Count; i++)
                    systemSb.AppendLine(events[i].ToString());
            }

            messages.Add(new LLMClient.ChatMessage
            {
                Role = "system",
                Content = systemSb.ToString()
            });

            // 4. Chat history (last 10 turns = 20 messages)
            int histStart = chatHistory.Count > 20 ? chatHistory.Count - 20 : 0;
            for (int i = histStart; i < chatHistory.Count; i++)
            {
                var msg = chatHistory[i];
                messages.Add(new LLMClient.ChatMessage
                {
                    Role = msg.IsUser ? "user" : "assistant",
                    Content = msg.Text
                });
            }

            // 5. Current user message
            if (!string.IsNullOrEmpty(userMessage))
            {
                messages.Add(new LLMClient.ChatMessage
                {
                    Role = "user",
                    Content = userMessage
                });
            }

            return messages;
        }

        /// <summary>
        /// Build messages for spontaneous comment on a major event
        /// </summary>
        public static List<LLMClient.ChatMessage> BuildForEvent(
            GameEvent evt,
            List<ChatMessage> chatHistory)
        {
            var lang = Main.Settings?.UILanguage ?? Language.English;
            string instruction = lang switch
            {
                Language.Korean => "이 이벤트에 대해 캐릭터에 맞게 짧게 코멘트하라.",
                Language.Russian => "Прокомментируй это событие кратко, в образе.",
                Language.Japanese => "このイベントについてキャラクターに合わせて短くコメントせよ。",
                _ => "Comment on this event briefly, in character."
            };
            string prompt = $"[EVENT ALERT] {evt}\n{instruction}";
            return Build(chatHistory, prompt);
        }
    }

    /// <summary>
    /// A single chat message in history
    /// </summary>
    public struct ChatMessage
    {
        public bool IsUser;
        public string Text;
        public float Timestamp;
    }
}
