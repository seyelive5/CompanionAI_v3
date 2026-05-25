// Planning/LLM/LLMWarmup.cs
// ★ v3.98.0: Ollama 모델 사전 로딩.
// 게임 실행 후 LLM 전투 AI가 켜져 있으면 백그라운드로 모델을 미리 메모리에 올림.
// keep_alive=-1이 모든 요청에 이미 설정되어 있어 한 번 올리면 유지됨.
// ★ v3.114.0 (Phase F.2): UnityWebRequest/HttpWebRequest → LLMHttpClient 마이그레이션.
using System.Collections;
using System.Collections.Generic;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Planning.LLM
{
    /// <summary>
    /// Ollama 모델 백그라운드 warmup.
    ///
    /// 기존 문제: 첫 전투 첫 유닛 턴에 모델 로드 → 수초~수십초 정지 체감.
    /// 해결: 게임 시작/세팅 활성 시 더미 요청 1회 보내 메모리에 올려둠.
    ///
    /// 호출 지점: MachineSpirit.Update() 매 프레임 경량 체크
    /// 스레드: LLMHttpClient.PostChatAsync 기반 비동기 — UI 블로킹 없음
    /// </summary>
    public static class LLMWarmup
    {
        private static readonly HashSet<string> _warmedModels = new HashSet<string>();
        private static bool _isWarming;
        private const int WARMUP_TIMEOUT_SECONDS = 120; // 큰 모델도 로드 가능

        public static bool IsWarming => _isWarming;

        public static bool IsWarmed(string modelId)
        {
            return !string.IsNullOrEmpty(modelId) && _warmedModels.Contains(modelId);
        }

        /// <summary>모델이 재로드되어야 할 때 호출 (설정 변경 등).</summary>
        public static void InvalidateModel(string modelId)
        {
            if (!string.IsNullOrEmpty(modelId))
                _warmedModels.Remove(modelId);
        }

        private static string _lastCheckedModel;

        /// <summary>
        /// 매 프레임 경량 체크 — warmup 필요 시 코루틴 시작.
        /// MachineSpirit.Update() 최상단에서 호출. O(1) property lookup + HashSet lookup.
        /// EnableLLMCombatAI가 켜져 있고 현재 모델이 warmed 상태 아니면 백그라운드 시작.
        /// </summary>
        public static void TryTickWarmup()
        {
            var settings = Main.Settings;
            if (settings == null || !settings.EnableLLMCombatAI) return;
            if (_isWarming) return;

            // 모델 결정 — 전투 LLM 과 동일 체인 (LLMHttpClient.ResolveModel 위임)
            string model = LLMHttpClient.ResolveModel();
            if (string.IsNullOrEmpty(model)) return;

            // 모델이 바뀌었으면 이전 것 무효화
            if (_lastCheckedModel != model && !string.IsNullOrEmpty(_lastCheckedModel))
                _warmedModels.Remove(_lastCheckedModel);
            _lastCheckedModel = model;

            if (_warmedModels.Contains(model)) return;

            string apiUrl = settings.MachineSpirit?.ApiUrl;
            if (string.IsNullOrEmpty(apiUrl)) apiUrl = "http://localhost:11434";

            CompanionAI_v3.MachineSpirit.CoroutineRunner.Start(WarmupModel(apiUrl, model));
        }

        /// <summary>
        /// 모델 warmup 코루틴. 이미 warmed이거나 warming 중이면 즉시 종료.
        /// 더미 요청: "ready" 메시지 + num_predict=1 → 거의 비용 없음.
        /// ★ v3.114.0 (Phase F.2): LLMHttpClient.PostChatAsync 위임.
        /// </summary>
        public static IEnumerator WarmupModel(string baseUrl, string modelId)
        {
            if (_isWarming) yield break;
            if (string.IsNullOrEmpty(modelId)) yield break;
            if (_warmedModels.Contains(modelId)) yield break;

            _isWarming = true;
            try
            {
                Log.Planning.Info($"[LLMWarmup] Preloading model '{modelId}'...");

                // Build request via LLMHttpClient.BuildChatRequest (Warmup pattern: user 메시지만, num_predict=1, keep_alive=-1)
                var body = LLMHttpClient.BuildChatRequest(
                    model: modelId,
                    systemMsg: null,
                    userMsg: "ready",
                    numPredict: 1,
                    temperature: 0f,
                    think: false,
                    keepAlive: -1);

                LLMHttpClient.Response response = default(LLMHttpClient.Response);
                yield return LLMHttpClient.PostChatAsync(
                    baseUrl, body, WARMUP_TIMEOUT_SECONDS,
                    r => response = r);

                if (response.Success)
                {
                    _warmedModels.Add(modelId);
                    Log.Planning.Info($"[LLMWarmup] Model '{modelId}' warmed in {response.ElapsedSeconds:F1}s");
                }
                else
                {
                    Log.Planning.Debug($"[LLMWarmup] Warmup failed for '{modelId}': {response.ErrorMessage} (HTTP {response.HttpStatusCode})");
                    // 실패 시 _warmedModels에 추가하지 않음 → 다음 체크 시 재시도
                }
            }
            finally
            {
                _isWarming = false;
            }
        }

        /// <summary>
        /// ★ v3.112.3: 게임/모드 종료 시 Ollama 에 모델 VRAM 해제 요청.
        /// keep_alive=-1 로 영구 유지 중인 모델을 즉시 언로드.
        /// Sync HTTP — 셧다운 중 코루틴 불안정하므로 blocking 방식.
        /// 호출 경로: MachineSpirit.Shutdown() (UMM 토글) + Application.quitting (게임 종료).
        /// ★ v3.114.0 (Phase F.2): LLMHttpClient.PostGenerateSync 위임.
        /// </summary>
        public static void UnloadAllModels(string baseUrl)
        {
            if (_warmedModels.Count == 0) return;

            var models = new List<string>(_warmedModels);
            _warmedModels.Clear();  // 재진입 시 웜업 다시 트리거되도록

            // ★ v3.112.4 (C1): 총 셧다운 지연 budget cap.
            //   기존: 2000ms × N — Ollama 응답 없으면 사용자 체감 hang.
            //   현재: per-request 500ms + 누적 1500ms cap.
            //   keep_alive=0 payload 는 TCP send 에 실리면 충분 — 응답 대기 가치 낮음.
            const int PerRequestTimeoutMs = 500;
            const int TotalBudgetMs = 1500;
            var totalSw = System.Diagnostics.Stopwatch.StartNew();

            foreach (var model in models)
            {
                if (totalSw.ElapsedMilliseconds > TotalBudgetMs)
                {
                    Log.Planning.Debug($"[LLMWarmup] Unload budget ({TotalBudgetMs}ms) exceeded — skipping remaining models");
                    break;
                }

                var response = LLMHttpClient.PostGenerateSync(
                    baseUrl: baseUrl,
                    model: model,
                    keepAlive: 0,
                    timeoutMs: PerRequestTimeoutMs);

                if (response.Success)
                {
                    Log.Planning.Info($"[LLMWarmup] Unloaded model '{model}' (HTTP {response.HttpStatusCode}, {totalSw.ElapsedMilliseconds}ms)");
                }
                else
                {
                    Log.Planning.Debug($"[LLMWarmup] Unload failed for '{model}' ({totalSw.ElapsedMilliseconds}ms): {response.ErrorMessage}");
                    // budget 내에서 다음 모델 시도 — keep_alive=0 payload 는 TCP 송신만 되면 됨
                }
            }
        }
    }
}
