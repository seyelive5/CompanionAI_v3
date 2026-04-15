// Planning/LLM/LLMWarmup.cs
// ★ v3.98.0: Ollama 모델 사전 로딩.
// 게임 실행 후 LLM 전투 AI가 켜져 있으면 백그라운드로 모델을 미리 메모리에 올림.
// keep_alive=-1이 모든 요청에 이미 설정되어 있어 한 번 올리면 유지됨.
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json.Linq;

namespace CompanionAI_v3.Planning.LLM
{
    /// <summary>
    /// Ollama 모델 백그라운드 warmup.
    ///
    /// 기존 문제: 첫 전투 첫 유닛 턴에 모델 로드 → 수초~수십초 정지 체감.
    /// 해결: 게임 시작/세팅 활성 시 더미 요청 1회 보내 메모리에 올려둠.
    ///
    /// 호출 지점: MachineSpirit.Update() 매 프레임 경량 체크
    /// 스레드: UnityWebRequest 기반 비동기 — UI 블로킹 없음
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

            // 모델 결정 (LLMScorer.ResolveModel과 동일 경로)
            string model = settings.LLMJudgeModel;
            if (string.IsNullOrEmpty(model))
                model = settings.MachineSpirit?.Model;
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
        /// </summary>
        public static IEnumerator WarmupModel(string baseUrl, string modelId)
        {
            if (_isWarming) yield break;
            if (string.IsNullOrEmpty(modelId)) yield break;
            if (_warmedModels.Contains(modelId)) yield break;

            _isWarming = true;
            Main.Log($"[LLMWarmup] Preloading model '{modelId}'...");

            string url = NormalizeBaseUrl(baseUrl) + "/api/chat";

            var body = new JObject
            {
                ["model"] = modelId,
                ["messages"] = new JArray
                {
                    new JObject { ["role"] = "user", ["content"] = "ready" }
                },
                ["stream"] = false,
                ["keep_alive"] = -1,
                ["think"] = false,
                ["options"] = new JObject
                {
                    ["num_predict"] = 1,
                    ["temperature"] = 0
                }
            };

            UnityWebRequest req = null;
            try
            {
                req = new UnityWebRequest(url, "POST");
                req.uploadHandler = new UploadHandlerRaw(
                    Encoding.UTF8.GetBytes(body.ToString(Newtonsoft.Json.Formatting.None)));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = WARMUP_TIMEOUT_SECONDS;
            }
            catch (System.Exception ex)
            {
                Main.LogDebug($"[LLMWarmup] request build failed: {ex.Message}");
                _isWarming = false;
                if (req != null) req.Dispose();
                yield break;
            }

            float startTime = Time.realtimeSinceStartup;
            yield return req.SendWebRequest();
            float elapsed = Time.realtimeSinceStartup - startTime;

            if (req.result == UnityWebRequest.Result.Success)
            {
                _warmedModels.Add(modelId);
                Main.Log($"[LLMWarmup] Model '{modelId}' warmed in {elapsed:F1}s");
            }
            else
            {
                Main.LogDebug($"[LLMWarmup] Warmup failed for '{modelId}': {req.error} (HTTP {req.responseCode})");
                // 실패 시 _warmedModels에 추가하지 않음 → 다음 체크 시 재시도
            }

            req.Dispose();
            _isWarming = false;
        }

        /// <summary>Ollama base URL 정규화 (/v1 suffix 제거).</summary>
        private static string NormalizeBaseUrl(string baseUrl)
        {
            if (string.IsNullOrEmpty(baseUrl)) return "http://localhost:11434";
            string url = baseUrl.TrimEnd('/');
            if (url.EndsWith("/v1"))
                url = url.Substring(0, url.Length - 3);
            return url;
        }
    }
}
