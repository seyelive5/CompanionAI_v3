// Diagnostics/TrainingDataCollector.cs
// ★ v3.82.0: LLM 스코어링 결과 + 실행 결과를 JSONL로 수집.
// 미래 fine-tuning 데이터셋 구축용.
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using CompanionAI_v3.Core;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Planning.LLM;

namespace CompanionAI_v3.Diagnostics
{
    /// <summary>
    /// ★ v3.82.0: LLM-influenced 턴의 입력(전투 상태) + 출력(가중치, 플랜) + 결과(AP 사용, 행동 수)를
    /// JSONL 형식으로 기록. 전투 종료 시 파일로 플러시.
    ///
    /// 출력 경로: [ModPath]/training_data/combat_YYYYMMDD.jsonl
    /// 각 줄은 하나의 턴 결정을 표현하는 JSON 객체.
    /// </summary>
    public static class TrainingDataCollector
    {
        private static readonly List<string> _buffer = new List<string>();

        /// <summary>현재 버퍼에 쌓인 엔트리 수</summary>
        public static int BufferCount => _buffer.Count;

        /// <summary>★ v3.84.0: 이 세션에서 기록된 총 엔트리 수</summary>
        public static int TotalRecorded { get; private set; }

        /// <summary>
        /// LLM-influenced 턴 하나를 기록.
        /// 전투 상태(compactState), LLM 가중치, 플랜 요약, 실행 결과를 하나의 JSONL 엔트리로.
        /// </summary>
        /// <param name="unitName">유닛 이름</param>
        /// <param name="role">역할 (DPS, Tank, Support, Overseer)</param>
        /// <param name="compactState">CompactBattlefieldEncoder 출력 (전투 상태 요약)</param>
        /// <param name="weights">LLM이 출력한 스코어링 가중치</param>
        /// <param name="planSummary">PlanSummarizer 출력 (플랜 자연어 요약)</param>
        /// <param name="turnState">턴 상태 (행동 수, AP 사용량 등)</param>
        public static void RecordTurn(
            string unitName, string role,
            string compactState,
            ScorerWeights weights,
            string planSummary,
            TurnState turnState)
        {
            if (turnState?.Unit == null) return;

            try
            {
                float apUsed = turnState.StartingAP - CombatAPI.GetCurrentAP(turnState.Unit);
                if (apUsed < 0) apUsed = 0;

                var entry = new JObject
                {
                    ["timestamp"] = DateTime.UtcNow.ToString("o"),
                    ["unit"] = unitName ?? "Unknown",
                    ["role"] = role ?? "Unknown",
                    ["state"] = compactState ?? "",
                    ["weights"] = weights != null ? JObject.FromObject(new
                    {
                        aoe_weight = weights.AoEWeight,
                        focus_fire = weights.FocusFire,
                        priority_target = weights.PriorityTarget,
                        heal_priority = weights.HealPriority,
                        buff_priority = weights.BuffPriority,
                        defensive_stance = weights.DefensiveStance
                    }) : new JObject(),
                    ["plan"] = planSummary ?? "",
                    ["actions"] = turnState.ActionCount,
                    ["ap_used"] = Math.Round(apUsed, 1),
                    ["attacked"] = turnState.HasAttackedThisTurn,
                    ["moved"] = turnState.HasMovedThisTurn
                };

                _buffer.Add(entry.ToString(Newtonsoft.Json.Formatting.None));
                TotalRecorded++;

                // 즉시 파일에 추가 (턴 종료 시 바로 저장 — 전투 종료 대기 안 함)
                try
                {
                    string dir = Path.Combine(Main.ModPath, "training_data");
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, $"combat_{DateTime.Now:yyyyMMdd}.jsonl");
                    File.AppendAllText(path, entry.ToString(Newtonsoft.Json.Formatting.None) + "\n");
                }
                catch { /* 즉시 저장 실패해도 버퍼에는 남음 */ }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[TrainingData] RecordTurn failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 버퍼의 모든 엔트리를 파일로 플러시.
        /// 경로: [ModPath]/training_data/combat_YYYYMMDD.jsonl
        /// 기존 파일에 추가(Append).
        /// </summary>
        public static void FlushToFile()
        {
            if (_buffer.Count == 0) return;

            try
            {
                string dir = Path.Combine(
                    Main.ModPath, "training_data");
                Directory.CreateDirectory(dir);

                string path = Path.Combine(dir, $"combat_{DateTime.Now:yyyyMMdd}.jsonl");
                File.AppendAllLines(path, _buffer);

                Main.Log($"[TrainingData] Saved {_buffer.Count} entries to {path}");
                _buffer.Clear();
            }
            catch (Exception ex)
            {
                Main.LogWarning($"[TrainingData] FlushToFile failed: {ex.Message}");
            }
        }

        /// <summary>버퍼 클리어 (파일 저장 없이)</summary>
        public static void Clear()
        {
            _buffer.Clear();
        }
    }
}
