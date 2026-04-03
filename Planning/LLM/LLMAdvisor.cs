// Planning/LLM/LLMAdvisor.cs
// ★ Phase 4: Strategic Advisor — LLM이 전장을 분석하여 StrategicIntent 출력.
// LLMJudge와 동일한 HTTP 패턴 (format 파라미터 없음, think=false, temp=0).
using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json.Linq;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.MachineSpirit;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning.LLM
{
    /// <summary>
    /// ★ Phase 4: LLM Strategic Advisor — 전장 분석 → StrategicIntent 출력.
    /// LLMJudge(Phase 3)와 독립적으로 동작하며, TurnPlanner 가중치를 수정.
    /// Ollama에 1회 호출 (~0.5s), 응답 ~5 토큰.
    /// </summary>
    public static class LLMAdvisor
    {
        private static bool _isAdvising;

        /// <summary>Advisor 요청 진행 중 여부</summary>
        public static bool IsAdvising => _isAdvising;

        /// <summary>마지막 Advisor 호출 소요 시간 (ms).</summary>
        public static long LastAdvisorTimeMs { get; private set; }

        /// <summary>Advisor 타임아웃 (초).</summary>
        private const int ADVISOR_TIMEOUT_SECONDS = 30;

        // ═══════════════════════════════════════════════════════════
        // 재사용 버퍼 (GC 방지)
        // ═══════════════════════════════════════════════════════════
        private static readonly StringBuilder _sbSystem = new StringBuilder(256);
        private static readonly StringBuilder _sbUser = new StringBuilder(512);

        // ═══════════════════════════════════════════════════════════
        // 시스템 메시지 캐시
        // ═══════════════════════════════════════════════════════════
        private static string _cachedSystemRole;
        private static string _cachedSystemMsg;

        /// <summary>
        /// 진행 상태 초기화 (전투 종료 시 등).
        /// </summary>
        public static void Reset()
        {
            _isAdvising = false;
        }

        // ═══════════════════════════════════════════════════════════
        // Public API
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// LLM Advisor 코루틴 — 전장 분석 후 StrategicIntent 출력.
        /// </summary>
        /// <param name="situation">현재 전투 상황</param>
        /// <param name="roleName">유닛 역할 이름 (DPS, Tank 등)</param>
        /// <param name="onResult">결과 콜백. 실패 시 Balanced 폴백.</param>
        public static IEnumerator Advise(
            Situation situation,
            string roleName,
            Action<StrategicIntent> onResult)
        {
            if (_isAdvising)
            {
                Main.Log("[LLMAdvisor] Already advising -- fallback to Balanced");
                onResult?.Invoke(StrategicIntent.Balanced);
                yield break;
            }

            if (situation == null)
            {
                onResult?.Invoke(StrategicIntent.Balanced);
                yield break;
            }

            int enemyCount = situation.Enemies?.Count ?? 0;
            if (enemyCount == 0)
            {
                onResult?.Invoke(StrategicIntent.Balanced);
                yield break;
            }

            _isAdvising = true;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // 1. 메시지 구성
                string systemMsg, userMsg;
                try
                {
                    systemMsg = BuildSystemMessage(roleName);
                    userMsg = BuildUserMessage(situation);
                }
                catch (Exception msgEx)
                {
                    Main.LogWarning($"[LLMAdvisor] Message build failed: {msgEx.Message} — fallback to Balanced");
                    _isAdvising = false;
                    onResult?.Invoke(StrategicIntent.Balanced);
                    yield break;
                }

                // 2. 모델 결정 (LLMJudge와 동일 경로)
                string model = ResolveModel();

                // 3. Ollama 요청 구성
                // ★ CRITICAL: format 파라미터 없음 (Gemma4 빈 응답 문제)
                // ★ CRITICAL: think=false (thinking 모드 비활성화)
                var requestBody = new JObject
                {
                    ["model"] = model,
                    ["messages"] = new JArray
                    {
                        new JObject { ["role"] = "system", ["content"] = systemMsg },
                        new JObject { ["role"] = "user", ["content"] = userMsg }
                    },
                    ["stream"] = false,
                    ["keep_alive"] = -1,
                    ["options"] = new JObject
                    {
                        ["temperature"] = 0,
                        ["num_predict"] = 30  // 응답 ~5 토큰 (예: "focus_fire 2 0.75 0.5")
                    },
                    ["think"] = false
                };

                // 4. Ollama URL 결정
                string baseUrl = GetOllamaBaseUrl();
                string url = baseUrl + "/api/chat";

                Main.LogDebug($"[LLMAdvisor] -> {url}, model={model}, enemies={enemyCount}");

                // 5. HTTP 요청
                string responseText = null;
                string errorText = null;

                var request = new UnityWebRequest(url, "POST");
                request.uploadHandler = new UploadHandlerRaw(
                    Encoding.UTF8.GetBytes(requestBody.ToString(Newtonsoft.Json.Formatting.None)));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = ADVISOR_TIMEOUT_SECONDS;

                var op = request.SendWebRequest();

                // 타임아웃 대기
                float deadline = Time.realtimeSinceStartup + ADVISOR_TIMEOUT_SECONDS + 1f;
                while (!op.isDone)
                {
                    if (Time.realtimeSinceStartup > deadline)
                    {
                        errorText = "Advisor timeout exceeded";
                        request.Abort();
                        break;
                    }
                    yield return null;
                }

                if (errorText == null)
                {
                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        responseText = request.downloadHandler.text;
                    }
                    else
                    {
                        errorText = $"HTTP {request.responseCode}: {request.error}";
                    }
                }

                request.Dispose();

                // 6. 응답 파싱
                StrategicIntent intent = StrategicIntent.Balanced;

                if (responseText != null)
                {
                    Main.LogDebug($"[LLMAdvisor] Raw response ({responseText.Length} chars): {Truncate(responseText, 300)}");
                    string content = ExtractContent(responseText);
                    intent = StrategicIntent.Parse(content, enemyCount);

                    stopwatch.Stop();
                    LastAdvisorTimeMs = stopwatch.ElapsedMilliseconds;
                    intent.ElapsedMs = LastAdvisorTimeMs;

                    Main.Log($"[LLMAdvisor] Result: {intent} in {LastAdvisorTimeMs}ms");
                }
                else
                {
                    stopwatch.Stop();
                    LastAdvisorTimeMs = stopwatch.ElapsedMilliseconds;
                    Main.Log($"[LLMAdvisor] Failed: {errorText} -- fallback to Balanced ({LastAdvisorTimeMs}ms)");
                }

                _isAdvising = false;
                onResult?.Invoke(intent);
            }
            finally
            {
                _isAdvising = false;
                if (stopwatch.IsRunning) stopwatch.Stop();
            }
        }

        // ═══════════════════════════════════════════════════════════
        // 메시지 빌드
        // ═══════════════════════════════════════════════════════════

        /// <summary>시스템 메시지 — 역할별 캐싱. 짧고 정확한 형식 지시.</summary>
        private static string BuildSystemMessage(string roleName)
        {
            if (_cachedSystemMsg != null && _cachedSystemRole == roleName)
                return _cachedSystemMsg;

            _sbSystem.Clear();
            _sbSystem.Append("You are a tactical combat advisor for a ").Append(roleName).Append(" unit.\n");
            _sbSystem.Append("Given the battlefield, output your strategic intent as:\n");
            _sbSystem.Append("GOAL TARGET_INDEX AOE_PREF AGGRESSION\n\n");
            _sbSystem.Append("GOAL: focus_fire | aoe_clear | protect_ally | retreat | balanced\n");
            _sbSystem.Append("TARGET_INDEX: enemy number (0-based) to prioritize (-1 if none)\n");
            _sbSystem.Append("AOE_PREF: 0.0 (single target) to 1.0 (prefer AoE)\n");
            _sbSystem.Append("AGGRESSION: 0.0 (defensive) to 1.0 (all-out attack)\n\n");
            _sbSystem.Append("Example: focus_fire 2 0.25 0.75\n");
            _sbSystem.Append("Output ONLY the 4 values. Nothing else.");

            _cachedSystemRole = roleName;
            _cachedSystemMsg = _sbSystem.ToString();
            return _cachedSystemMsg;
        }

        /// <summary>유저 메시지 — 전장 요약 + 번호 매긴 적 목록.</summary>
        private static string BuildUserMessage(Situation situation)
        {
            _sbUser.Clear();

            // 전장 요약 (BattlefieldSummarizer 재사용)
            _sbUser.Append(BattlefieldSummarizer.Summarize(situation));

            // 적 목록에 명시적 인덱스 추가 (LLM이 TARGET_INDEX를 정확히 참조하도록)
            var enemies = situation.Enemies;
            if (enemies != null && enemies.Count > 0)
            {
                _sbUser.Append("\nEnemy Index:\n");
                for (int i = 0; i < enemies.Count; i++)
                {
                    var e = enemies[i];
                    if (e == null) continue;

                    string name = e.CharacterName ?? "Enemy";
                    float hp = GameInterface.CombatAPI.GetHPPercent(e);
                    float dist = GameInterface.CombatAPI.GetDistanceInTiles(situation.Unit, e);

                    _sbUser.Append(i).Append(": ").Append(name)
                           .Append(" (HP:").Append(hp.ToString("F0")).Append("%")
                           .Append(", ").Append(dist.ToString("F0")).Append(" tiles)");

                    // 핵심 태그
                    if (situation.BestTarget != null && e.UniqueId == situation.BestTarget.UniqueId)
                        _sbUser.Append(" [HIGH PRIORITY]");
                    if (situation.CanKillBestTarget && situation.BestTarget != null && e.UniqueId == situation.BestTarget.UniqueId)
                        _sbUser.Append(" [FINISHABLE]");
                    if (hp < 25f)
                        _sbUser.Append(" [LOW HP]");

                    _sbUser.Append('\n');
                }
            }

            return _sbUser.ToString();
        }

        // ═══════════════════════════════════════════════════════════
        // 응답 파싱
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Ollama /api/chat 응답에서 content 추출.
        /// {"message":{"content":"focus_fire 2 0.75 0.5"}}
        /// </summary>
        private static string ExtractContent(string rawResponse)
        {
            try
            {
                var outerJson = JObject.Parse(rawResponse);
                string content = outerJson["message"]?["content"]?.ToString();
                return content?.Trim() ?? "";
            }
            catch
            {
                return "";
            }
        }

        // ═══════════════════════════════════════════════════════════
        // 헬퍼 (LLMJudge와 동일 패턴)
        // ═══════════════════════════════════════════════════════════

        /// <summary>Advisor에 사용할 모델 결정. LLMJudge 전용 설정 -> MachineSpirit 폴백.</summary>
        private static string ResolveModel()
        {
            // LLM Judge 모델 설정을 Advisor도 공유
            var judgeModel = Main.Settings?.LLMJudgeModel;
            if (!string.IsNullOrEmpty(judgeModel))
                return judgeModel;

            var msConfig = Main.Settings?.MachineSpirit;
            if (msConfig != null && !string.IsNullOrEmpty(msConfig.Model))
                return msConfig.Model;

            return "gemma4:e4b";
        }

        /// <summary>Ollama base URL 결정 (v1 suffix 제거).</summary>
        private static string GetOllamaBaseUrl()
        {
            string url = Main.Settings?.MachineSpirit?.ApiUrl ?? "http://localhost:11434/v1";
            url = url.TrimEnd('/');
            if (url.EndsWith("/v1"))
                url = url.Substring(0, url.Length - 3);
            return url;
        }

        private static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "(null)";
            return s.Length <= maxLen ? s : s.Substring(0, maxLen) + "...";
        }
    }
}
