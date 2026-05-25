// Planning/LLM/LLMScorer.cs
// ★ LLM-as-Scorer: Ollama에 전투 상태를 보내고 가중치 JSON을 받음.
// LLMJudge와 동일한 HTTP 패턴 (format 없음, think=false, temp=0).
// ★ v3.114.0 (Phase F.2): HTTP 플러밍은 LLMHttpClient 로 통합. caller-specific
//   요소(latch, watchdog, ScorerWeights 파싱, 로깅) 만 본 파일에 잔존.
using System;
using System.Collections;
using System.Text;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Settings;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Planning.LLM
{
    /// <summary>
    /// ★ LLM-as-Scorer: Ollama에 전투 상태(CompactBattlefieldEncoder)를 보내고
    /// 가중치 JSON(ScorerWeights)을 받아 UtilityScorer/TargetScorer에 적용.
    /// 독립적 HTTP 요청 (LLMJudge와 별도).
    /// </summary>
    public static class LLMScorer
    {
        private static bool _isScoring;
        private static ScorerWeights _pendingWeights;

        // ★ v3.112.4 (C3): 코루틴 비정상 dispose 시 _isScoring latch 영구 유지 방지.
        //   finally 블록이 실행 안 되면 다음 Score() 호출이 모두 fallback 됨.
        //   timeout + 5초 초과한 _isScoring=true 는 강제 reset.
        private static float _scoringStartTime;
        private const float WATCHDOG_GRACE_SECONDS = 5f;

        /// <summary>Scorer 요청 진행 중 여부</summary>
        public static bool IsScoring => _isScoring;

        /// <summary>결과가 준비되었는지 (요청 완료 + 결과 미소비)</summary>
        public static bool HasResult => !_isScoring && _pendingWeights != null;

        /// <summary>마지막 Scorer 호출 소요 시간 (ms). 디버그/성능 모니터링용.</summary>
        public static long LastScorerTimeMs { get; private set; }

        /// <summary>Scorer 타임아웃 (초).</summary>
        private const int SCORER_TIMEOUT_SECONDS = 30;

        // ═══════════════════════════════════════════════════════════
        // 재사용 버퍼 (GC 방지)
        // ═══════════════════════════════════════════════════════════
        private static readonly StringBuilder _sbSystem = new StringBuilder(256);

        // ═══════════════════════════════════════════════════════════
        // 시스템 메시지 캐시 (역할별)
        // ═══════════════════════════════════════════════════════════
        private static string _cachedSystemRole;
        private static string _cachedSystemMsg;

        /// <summary>
        /// 진행 상태 초기화 (전투 종료 시 등).
        /// </summary>
        public static void Reset()
        {
            _isScoring = false;
            _pendingWeights = null;
            _scoringStartTime = 0f;  // ★ v3.112.4 (C3): watchdog 기준 시각도 reset
        }

        /// <summary>
        /// ★ v3.112.4 (C3): 워치독 — _isScoring latch 가 SCORER_TIMEOUT + grace 초과 시 강제 reset.
        /// Score() 진입 시 호출. 정상 코루틴은 SCORER_TIMEOUT 내 finally 블록이 reset 함.
        /// </summary>
        private static void TickWatchdog()
        {
            if (!_isScoring) return;
            float elapsed = UnityEngine.Time.realtimeSinceStartup - _scoringStartTime;
            float threshold = SCORER_TIMEOUT_SECONDS + WATCHDOG_GRACE_SECONDS;
            if (elapsed > threshold)
            {
                Log.Planning.Info($"[LLMScorer] Watchdog: stale _isScoring latch ({elapsed:F1}s > {threshold:F1}s) — force reset");
                _isScoring = false;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // Public API
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// LLM Scorer 코루틴 — 전투 상태를 인코딩하여 Ollama에 전송, 가중치 JSON 수신.
        /// </summary>
        /// <param name="situation">현재 전투 상황</param>
        /// <param name="roleName">유닛 역할 이름 (DPS, Tank 등)</param>
        /// <param name="enemyCount">적 수 (PriorityTarget 범위 검증용)</param>
        /// <param name="onResult">결과 콜백. 실패 시 기본 ScorerWeights.</param>
        public static IEnumerator Score(
            Situation situation,
            string roleName,
            int enemyCount,
            Action<ScorerWeights> onResult)
        {
            TickWatchdog();  // ★ v3.112.4 (C3): stale latch 검증 후
            if (_isScoring)
            {
                Log.Planning.Info("[LLMScorer] Already scoring -- fallback to defaults");
                onResult?.Invoke(new ScorerWeights());
                yield break;
            }

            if (situation == null || situation.Unit == null)
            {
                onResult?.Invoke(new ScorerWeights());
                yield break;
            }

            if (enemyCount == 0)
            {
                onResult?.Invoke(new ScorerWeights());
                yield break;
            }

            _scoringStartTime = UnityEngine.Time.realtimeSinceStartup;  // ★ v3.112.4 (C3): watchdog 기준 시각
            _isScoring = true;
            _pendingWeights = null;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // 1. 메시지 구성
                string systemMsg, userMsg;
                int[] displayMap = null;  // ★ v3.101.0: E 라인 display idx → 원본 idx 역매핑
                try
                {
                    systemMsg = BuildSystemMessage(roleName);
                    string encoded = CompactBattlefieldEncoder.Encode(situation.Unit, situation, roleName);
                    displayMap = CompactBattlefieldEncoder.GetDisplayToOriginalMap();

                    // ★ v3.110.0: Commander narration을 팀 레벨 맥락으로 프리펜드 (A안 — Pre-Scorer Brief)
                    var directive = Core.TeamBlackboard.Instance?.CommanderDirective;
                    if (directive != null && !string.IsNullOrEmpty(directive.Narration))
                    {
                        userMsg = "TEAM BRIEF: " + directive.Narration.Trim() + "\n\n" + encoded;
                    }
                    else
                    {
                        userMsg = encoded;
                    }
                }
                catch (Exception msgEx)
                {
                    Log.Planning.Warn($"[LLMScorer] Message build failed: {msgEx.Message} -- fallback to defaults");
                    _isScoring = false;
                    onResult?.Invoke(new ScorerWeights());
                    yield break;
                }

                // 2. 모델 결정 (LLMHttpClient 통합 — LLMJudgeModel → "gemma4:e4b" 폴백)
                string model = LLMHttpClient.ResolveModel();

                // 3. Ollama 요청 구성 (LLMHttpClient.BuildChatRequest 위임)
                // ★ CRITICAL: format 파라미터 없음 (Gemma4 빈 응답 문제)
                // ★ CRITICAL: think=false (thinking 모드 비활성화)
                // num_predict=120: JSON 가중치 + reasoning 1문장 (~50 토큰)
                var requestBody = LLMHttpClient.BuildChatRequest(
                    model: model,
                    systemMsg: systemMsg,
                    userMsg: userMsg,
                    numPredict: 120,
                    temperature: 0f,
                    think: false,
                    keepAlive: -1);

                // 4. Ollama URL 결정 (LLMHttpClient 정규화)
                string baseUrl = Main.Settings?.MachineSpirit?.ApiUrl;
                string normalizedUrl = LLMHttpClient.NormalizeBaseUrl(baseUrl);

                // ★ v3.110.4: 토큰 회귀 감지용 — user msg 길이 + 대략 토큰 (chars/4).
                // 컴포넌트 누적(TEAM BRIEF, CMD, PAST, KB, E line)으로 예산 초과 시 응답 지연 증가.
                int userChars = userMsg?.Length ?? 0;
                Log.Planning.Debug($"[LLMScorer] -> {normalizedUrl}/api/chat, model={model}, enemies={enemyCount}, userMsg={userChars}ch (~{userChars / 4}tok)");

                // 5. HTTP 요청 (LLMHttpClient.PostChatAsync 위임)
                LLMHttpClient.Response response = default(LLMHttpClient.Response);
                yield return LLMHttpClient.PostChatAsync(
                    baseUrl,
                    requestBody,
                    SCORER_TIMEOUT_SECONDS,
                    r => response = r);

                // 6. 응답 파싱
                ScorerWeights weights = new ScorerWeights();

                if (response.Success && !string.IsNullOrEmpty(response.RawJson))
                {
                    string responseText = response.RawJson;
                    Log.Planning.Debug($"[LLMScorer] Raw response ({responseText.Length} chars): {Truncate(responseText, 300)}");
                    string content = LLMHttpClient.ExtractContent(responseText);
                    weights = ScorerWeights.Parse(content, enemyCount, displayMap);  // ★ v3.101.0: display → original 역매핑

                    stopwatch.Stop();
                    LastScorerTimeMs = stopwatch.ElapsedMilliseconds;

                    // ★ v3.110.2: priority_target 디버깅 — 실제 적 이름 로깅
                    string targetInfo = "";
                    if (weights.PriorityTarget >= 0 && situation.Enemies != null
                        && weights.PriorityTarget < situation.Enemies.Count)
                    {
                        var targetEnemy = situation.Enemies[weights.PriorityTarget];
                        if (targetEnemy != null)
                        {
                            float tHp = GameInterface.CombatAPI.GetHPPercent(targetEnemy);
                            float tDist = situation.Unit != null
                                ? GameInterface.CombatAPI.GetDistanceInTiles(situation.Unit, targetEnemy)
                                : 0f;
                            targetInfo = $" [target={targetEnemy.CharacterName ?? "?"} HP={tHp:F0}% d={tDist:F1}]";
                        }
                    }
                    Log.Planning.Info($"[LLMScorer] Result: {weights}{targetInfo} in {LastScorerTimeMs}ms");
                    if (!string.IsNullOrEmpty(weights.Reasoning))
                        Log.Planning.Info($"[LLMScorer] Reasoning: {weights.Reasoning}");
                }
                else
                {
                    stopwatch.Stop();
                    LastScorerTimeMs = stopwatch.ElapsedMilliseconds;
                    string errorText = response.WasTimeout
                        ? "Scorer timeout exceeded"
                        : $"HTTP {response.HttpStatusCode}: {response.ErrorMessage}";
                    Log.Planning.Info($"[LLMScorer] Failed: {errorText} -- fallback to defaults ({LastScorerTimeMs}ms)");
                }

                _pendingWeights = weights;
                _isScoring = false;
                onResult?.Invoke(weights);
            }
            finally
            {
                _isScoring = false;
                if (stopwatch.IsRunning) stopwatch.Stop();
            }
        }

        /// <summary>
        /// 대기 중인 결과 소비. 소비 후 null로 초기화.
        /// </summary>
        public static ScorerWeights ConsumeResult()
        {
            var result = _pendingWeights;
            _pendingWeights = null;
            return result ?? new ScorerWeights();
        }

        // ═══════════════════════════════════════════════════════════
        // 메시지 빌드
        // ═══════════════════════════════════════════════════════════

        /// <summary>시스템 메시지 — 역할별 캐싱. ~80 토큰의 간결한 형식 지시.</summary>
        private static string BuildSystemMessage(string roleName)
        {
            if (_cachedSystemMsg != null && _cachedSystemRole == roleName)
                return _cachedSystemMsg;

            _sbSystem.Clear();
            _sbSystem.Append("Tactical scoring advisor for ").Append(roleName).Append(" unit in turn-based combat.\n");
            _sbSystem.Append("Output JSON weights. Only include changed values.\n");
            // ★ v3.102.0: 이산 카테고리 스키마 (소형 LLM 친화적)
            _sbSystem.Append("Categorical keys (use short labels):\n");
            _sbSystem.Append("  aoe_weight: \"skip\"|\"normal\"|\"priority\"\n");
            _sbSystem.Append("  focus_fire: \"off\"|\"normal\"|\"heavy\"\n");
            _sbSystem.Append("  heal_priority: \"suppress\"|\"normal\"|\"urgent\"\n");
            _sbSystem.Append("  buff_priority: \"skip\"|\"normal\"|\"heavy\"\n");
            _sbSystem.Append("Discrete keys: priority_target(int, 0..N-1 from E line), defensive_stance(bool).\n");
            _sbSystem.Append("Defaults: all \"normal\", target -1, defensive false.\n");
            // ★ v3.101.0: E 라인 정렬 정책 고지 (primacy bias 활용)
            _sbSystem.Append("Note: E: entries are pre-sorted by threat (index 0 = top threat). priority_target refers to these indices. Override only when another index has a clearly stronger tactical reason.\n");
            _sbSystem.Append("ALWAYS include 'reasoning': 1 short sentence explaining why these weights (or why baseline is fine).\n");
            _sbSystem.Append("Example: {\"aoe_weight\":\"priority\",\"focus_fire\":\"heavy\",\"priority_target\":0,\"reasoning\":\"Cluster of weak enemies — AoE will maximize damage\"}");

            _cachedSystemRole = roleName;
            _cachedSystemMsg = _sbSystem.ToString();
            return _cachedSystemMsg;
        }

        // ═══════════════════════════════════════════════════════════
        // 헬퍼
        // ═══════════════════════════════════════════════════════════
        // ★ v3.114.0 (Phase F.2): ExtractContent / ResolveModel / GetOllamaBaseUrl
        //   은 LLMHttpClient 로 이관 (4 caller 공통). 본 파일은 caller-specific
        //   로깅 전용 Truncate 만 잔존.

        private static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "(null)";
            return s.Length <= maxLen ? s : s.Substring(0, maxLen) + "...";
        }
    }
}
