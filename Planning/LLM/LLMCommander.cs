// Planning/LLM/LLMCommander.cs
// ★ Team Commander — 라운드 시작 시 1회 팀 전체 전략 지시 (LLM 호출).
// Convai NPC2NPC / Bannerlord AI Influence에서 영감.
// ★ v3.114.0 (Phase F.2): UnityWebRequest → LLMHttpClient 마이그레이션.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Planning.LLM
{
    /// <summary>
    /// 팀 전략 지시 데이터. TeamBlackboard에 저장, CompactBattlefieldEncoder에서 참조.
    /// </summary>
    public class CommanderDirective
    {
        /// <summary>팀 집중 타겟 적 인덱스 (-1 = 개별 판단)</summary>
        public int FocusTarget = -1;

        /// <summary>포메이션 방향: aggressive / balanced / defensive</summary>
        public string Formation = "balanced";

        /// <summary>협동 힌트 (예: "tank_first", "focus_fire", "protect_healer")</summary>
        public string Synergy = "";

        /// <summary>전술 내레이션 — 유저에게 보여줄 자연어 설명 (1-2문장).
        /// ★ v3.110.0: Scorer 프롬프트에도 프리펜드되어 팀 레벨 맥락 제공.</summary>
        public string Narration = "";

        /// <summary>★ v3.110.0: 전투 유형 분류 (normal|cleanup|horde|elite|boss|ambush).
        /// TurnStrategyPlanner가 시드 선호도 바이어스로 소비.</summary>
        public string EncounterType = "normal";

        /// <summary>기본값인지 확인 (LLM 미호출/실패 시)</summary>
        public bool IsDefault => FocusTarget == -1
            && (Formation == "balanced" || string.IsNullOrEmpty(Formation))
            && string.IsNullOrEmpty(Synergy)
            && EncounterType == "normal";

        /// <summary>
        /// LLM 응답에서 CommanderDirective 파싱.
        /// 형식: {"focus_target":0,"formation":"aggressive","synergy":"tank_first"}
        /// </summary>
        public static CommanderDirective Parse(string response, int enemyCount)
        {
            var d = new CommanderDirective();
            if (string.IsNullOrEmpty(response)) return d;

            response = response.Trim();

            // JSON 추출
            int start = response.IndexOf('{');
            int end = response.LastIndexOf('}');
            if (start < 0 || end <= start) return d;

            string jsonPart = response.Substring(start, end - start + 1);
            try
            {
                var json = JObject.Parse(jsonPart);

                // focus_target — ★ v3.110.3: TryParse 반환값으로 실패 판정 (이전 로직 가독성 개선)
                var ftToken = json["focus_target"];
                if (ftToken != null)
                {
                    int ft;
                    try
                    {
                        ft = ftToken.Value<int>();
                    }
                    catch
                    {
                        if (!int.TryParse(ftToken.ToString(), out ft)) ft = -1;
                    }
                    d.FocusTarget = (ft >= 0 && ft < enemyCount) ? ft : -1;
                }

                // formation
                var fmToken = json["formation"];
                if (fmToken != null)
                {
                    string fm = fmToken.ToString().Trim().ToLowerInvariant();
                    if (fm == "aggressive" || fm == "defensive" || fm == "balanced")
                        d.Formation = fm;
                }

                // synergy
                var synToken = json["synergy"];
                if (synToken != null)
                {
                    string syn = synToken.ToString().Trim();
                    if (syn.Length <= 40) // 과도한 텍스트 방지
                        d.Synergy = syn;
                }

                // ★ v3.110.0: encounter_type
                var etToken = json["encounter_type"];
                if (etToken != null)
                {
                    string et = etToken.ToString().Trim().ToLowerInvariant();
                    if (et == "normal" || et == "cleanup" || et == "horde"
                        || et == "elite" || et == "boss" || et == "ambush")
                        d.EncounterType = et;
                }

                // narration
                var narToken = json["narration"];
                if (narToken != null)
                {
                    string nar = narToken.ToString().Trim();
                    if (nar.Length <= 200) // 과도한 텍스트 방지
                        d.Narration = nar;
                }
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, $"[LLMCommander] Parse failed");
            }

            return d;
        }

        public override string ToString()
        {
            if (IsDefault) return "CommanderDirective(default)";
            // ★ v3.110.0: EncounterType 로그 표시 추가
            string s = $"CommanderDirective(enc={EncounterType}, focus={FocusTarget}, form={Formation}, syn={Synergy})";
            if (!string.IsNullOrEmpty(Narration))
                s += $" \"{Narration}\"";
            return s;
        }
    }

    /// <summary>
    /// ★ Team Commander — 라운드 시작 시 1회 팀 전체 전략 LLM 호출.
    /// 결과는 TeamBlackboard.CommanderDirective에 저장.
    /// 개별 유닛의 CompactBattlefieldEncoder가 CMD: 라인으로 참조.
    /// </summary>
    public static class LLMCommander
    {
        private static bool _isCommanding;

        /// <summary>Commander 요청 진행 중 여부</summary>
        public static bool IsCommanding => _isCommanding;

        /// <summary>마지막 Commander 호출 소요 시간 (ms)</summary>
        public static long LastCommanderTimeMs { get; private set; }

        private const int COMMANDER_TIMEOUT_SECONDS = 30;

        private static readonly StringBuilder _sbSystem = new StringBuilder(256);
        private static readonly StringBuilder _sbUser = new StringBuilder(1024);

        // 시스템 메시지 캐시
        private static string _cachedSystemMsg;

        public static void Reset()
        {
            _isCommanding = false;
        }

        // ═══════════════════════════════════════════════════════════
        // Public API
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Team Commander LLM 코루틴 — 팀 전체 전략 지시.
        /// 라운드당 1회 호출 (첫 아군 턴 시작 시).
        /// </summary>
        public static IEnumerator Command(
            List<Situation> allySituations,
            TeamBlackboard blackboard,
            int enemyCount,
            Action<CommanderDirective> onResult)
        {
            if (_isCommanding)
            {
                Log.Planning.Info("[LLMCommander] Already commanding — fallback to default");
                onResult?.Invoke(new CommanderDirective());
                yield break;
            }

            if (allySituations == null || allySituations.Count == 0 || enemyCount == 0)
            {
                onResult?.Invoke(new CommanderDirective());
                yield break;
            }

            _isCommanding = true;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // 1. 메시지 구성
                string systemMsg, userMsg;
                try
                {
                    systemMsg = BuildSystemMessage();
                    userMsg = BuildUserMessage(allySituations, blackboard);
                }
                catch (Exception msgEx)
                {
                    Log.Planning.Warn($"[LLMCommander] Message build failed: {msgEx.Message}");
                    _isCommanding = false;
                    onResult?.Invoke(new CommanderDirective());
                    yield break;
                }

                // 2. 요청 구성 (LLMHttpClient.BuildChatRequest 위임)
                string model = LLMHttpClient.ResolveModel();
                var requestBody = LLMHttpClient.BuildChatRequest(
                    model: model,
                    systemMsg: systemMsg,
                    userMsg: userMsg,
                    numPredict: 150,  // narration 포함 (~30 토큰 추가)
                    temperature: 0f,
                    think: false,
                    keepAlive: -1);

                // 3. baseUrl + 로그
                string baseUrl = Main.Settings?.MachineSpirit?.ApiUrl;
                if (string.IsNullOrEmpty(baseUrl)) baseUrl = "http://localhost:11434";

                // ★ v3.110.4: 토큰 회귀 감지용 — user msg 길이 + 대략 토큰 (chars/4)
                int userChars = userMsg?.Length ?? 0;
                Log.Planning.Debug($"[LLMCommander] → {LLMHttpClient.NormalizeBaseUrl(baseUrl)}/api/chat, model={model}, allies={allySituations.Count}, enemies={enemyCount}, userMsg={userChars}ch (~{userChars / 4}tok)");

                // 4. HTTP 요청 (LLMHttpClient.PostChatAsync 위임)
                LLMHttpClient.Response response = default(LLMHttpClient.Response);
                yield return LLMHttpClient.PostChatAsync(
                    baseUrl, requestBody, COMMANDER_TIMEOUT_SECONDS,
                    r => response = r);

                // 5. 응답 파싱
                CommanderDirective directive;
                if (response.Success)
                {
                    LLMDiagnostics.RecordCombatSuccess();
                    string raw = response.RawJson ?? "";
                    Log.Planning.Debug($"[LLMCommander] Raw ({raw.Length} chars): {Truncate(raw, 300)}");
                    string content = LLMHttpClient.ExtractContent(raw);
                    directive = CommanderDirective.Parse(content, enemyCount);
                    stopwatch.Stop();
                    LastCommanderTimeMs = stopwatch.ElapsedMilliseconds;
                    Log.Planning.Info($"[LLMCommander] {directive} ({LastCommanderTimeMs}ms)");
                }
                else
                {
                    stopwatch.Stop();
                    LastCommanderTimeMs = stopwatch.ElapsedMilliseconds;
                    directive = new CommanderDirective();
                    string errorText = response.WasTimeout
                        ? "Commander timeout exceeded"
                        : $"HTTP {response.HttpStatusCode}: {response.ErrorMessage}";
                    Log.Planning.Info($"[LLMCommander] Failed: {errorText} — default directive ({LastCommanderTimeMs}ms)");
                    LLMDiagnostics.RecordCombatFailure("LLMCommander", response.ErrorMessage,
                        response.HttpStatusCode, response.WasTimeout, baseUrl, model);
                }

                onResult?.Invoke(directive);
            }
            finally
            {
                _isCommanding = false;
                if (stopwatch.IsRunning) stopwatch.Stop();
            }
        }

        // ═══════════════════════════════════════════════════════════
        // 메시지 빌드
        // ═══════════════════════════════════════════════════════════

        private static string BuildSystemMessage()
        {
            if (_cachedSystemMsg != null) return _cachedSystemMsg;

            _sbSystem.Clear();
            _sbSystem.Append("You are a team tactical commander for a squad in turn-based combat.\n");
            _sbSystem.Append("Given the full battlefield summary, output a team directive as JSON.\n");
            // ★ v3.110.0: encounter_type + narration 중심 재포지셔닝
            _sbSystem.Append("Keys:\n");
            _sbSystem.Append("  encounter_type: \"normal\"|\"cleanup\"|\"horde\"|\"elite\"|\"boss\"|\"ambush\"\n");
            _sbSystem.Append("    cleanup=weak scattered, horde=many weaker, elite=few strong, boss=single big threat, ambush=disadvantaged\n");
            _sbSystem.Append("  narration: 1-2 sentence tactical brief — shared with each unit's scorer to align team strategy\n");
            _sbSystem.Append("  focus_target: int (-1=no override, enemy index if team should converge)\n");
            _sbSystem.Append("Example: {\"encounter_type\":\"boss\",\"narration\":\"Boss shield active this turn — debuff first, save AoE for phase 2. Tank holds aggro.\",\"focus_target\":0}\n");
            _sbSystem.Append("Output ONLY the JSON. Nothing else.");

            _cachedSystemMsg = _sbSystem.ToString();
            return _cachedSystemMsg;
        }

        /// <summary>
        /// 전체 팀+적 요약 + 팀 통계. ~200-300 토큰.
        /// </summary>
        private static string BuildUserMessage(List<Situation> allySituations, TeamBlackboard blackboard)
        {
            _sbUser.Clear();

            // 아군 요약
            _sbUser.Append("TEAM:\n");
            for (int i = 0; i < allySituations.Count; i++)
            {
                var sit = allySituations[i];
                if (sit?.Unit == null) continue;

                string name = sit.Unit.CharacterName;
                var role = sit.CharacterSettings?.Role ?? AIRole.Auto;
                if (role == AIRole.Auto)
                    role = (AIRole)Analysis.RoleDetector.DetectOptimalRole(sit.Unit);

                string roleAbbr;
                switch (role)
                {
                    case AIRole.Tank: roleAbbr = "Tank"; break;
                    case AIRole.Support: roleAbbr = "Sup"; break;
                    case AIRole.Overseer: roleAbbr = "Ovr"; break;
                    default: roleAbbr = "DPS"; break;
                }

                _sbUser.Append(name).Append(',').Append(roleAbbr);
                _sbUser.Append(",HP").Append((int)sit.HPPercent);
                if (sit.HPPercent < 30f) _sbUser.Append('!');
                _sbUser.Append(",AP").Append((int)sit.CurrentAP);
                _sbUser.Append('\n');
            }

            // 적 요약 (첫 Situation에서 Enemies 리스트 추출)
            Situation refSit = null;
            for (int i = 0; i < allySituations.Count; i++)
            {
                if (allySituations[i]?.Enemies != null && allySituations[i].Enemies.Count > 0)
                { refSit = allySituations[i]; break; }
            }

            if (refSit != null)
            {
                _sbUser.Append("ENEMIES:\n");
                int count = System.Math.Min(refSit.Enemies.Count, 8);
                for (int i = 0; i < count; i++)
                {
                    var enemy = refSit.Enemies[i];
                    if (enemy == null || enemy.LifeState.IsDead) continue;
                    _sbUser.Append(i).Append(':').Append(enemy.CharacterName);
                    float hp = CombatCache.GetHPPercent(enemy);
                    _sbUser.Append(",HP").Append((int)hp);
                    if (hp < 20f) _sbUser.Append(",FIN");
                    _sbUser.Append('\n');
                }
            }

            // 팀 통계
            if (blackboard != null)
            {
                int round = Kingmaker.Game.Instance?.TurnController?.CombatRound ?? 1;
                _sbUser.Append("STATS:");
                _sbUser.Append(" round=").Append(round);
                _sbUser.Append(" confidence=").Append(blackboard.TeamConfidence.ToString("F1"));
                _sbUser.Append(" tactic=").Append(blackboard.CurrentTactic);
                _sbUser.Append(" momentum=").Append(blackboard.KillMomentum.ToString("F1"));
                _sbUser.Append(" dmgRatio=").Append(blackboard.DamageRatio.ToString("F1"));
                _sbUser.Append('\n');
            }

            // Tactical Memory (있으면 포함)
            var memory = blackboard?.TacticalMemoryContext;
            if (!string.IsNullOrEmpty(memory))
                _sbUser.Append(memory).Append('\n');

            return _sbUser.ToString();
        }

        // ═══════════════════════════════════════════════════════════
        // 헬퍼 (caller-specific only)
        // ═══════════════════════════════════════════════════════════
        // ResolveModel / GetOllamaBaseUrl / ExtractContent → LLMHttpClient 통합 (v3.114.0 Phase F.2)

        private static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "(null)";
            return s.Length <= maxLen ? s : s.Substring(0, maxLen) + "...";
        }
    }
}
