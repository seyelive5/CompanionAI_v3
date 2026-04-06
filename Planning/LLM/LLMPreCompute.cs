// Planning/LLM/LLMPreCompute.cs
// ★ v3.82.0: Predictive pre-computation — 적 턴 동안 다음 아군의 LLM 스코어링을 미리 계산.
// ★ Scorer weights만 사전 계산 (상황 독립적, 히트율 높음).
//    풀 파이프라인(Plan 사전 생성)은 턴제 게임 특성상 비효율적이므로 제거됨.
using System;
using System.Collections;
using System.Collections.Generic;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning.LLM
{
    /// <summary>
    /// ★ v3.82.0: 적 턴 동안 LLM-enabled 아군의 스코어링을 미리 계산.
    /// 아군 턴이 오면 캐시된 결과를 즉시 사용하여 지연 없는 플래닝.
    ///
    /// 전략:
    /// 1. TurnEventHandler에서 적 턴 시작 감지
    /// 2. LLM-enabled 아군 중 아직 pre-compute 안 된 유닛 선택
    /// 3. SituationAnalyzer로 상황 스냅샷 → LLMScorer 코루틴 시작
    /// 4. 아군 턴 시작 시 TurnOrchestrator가 결과 조회
    ///
    /// 제한:
    /// - 한 번에 하나의 코루틴만 (네트워크 부하 방지)
    /// - 적 턴 동안 전장이 변해도 대략적 가중치는 유효 (가중치는 전략 방향만 조정)
    /// - 풀 파이프라인(Plan 생성)은 턴제 특성상 15% 미만 히트율 → Scorer-only로 한정
    /// </summary>
    public static class LLMPreCompute
    {
        private static readonly Dictionary<string, ScorerWeights> _preComputed
            = new Dictionary<string, ScorerWeights>();

        private static bool _isPreComputing;

        /// <summary>현재 pre-compute 진행 중 여부</summary>
        public static bool IsPreComputing => _isPreComputing;

        /// <summary>pre-computed 결과 수</summary>
        public static int PreComputedCount => _preComputed.Count;

        /// <summary>
        /// 적 턴 동안 호출 — LLM-enabled 아군 중 하나를 선택하여 스코어링 시작.
        /// 이미 진행 중이면 무시. 이미 결과가 있는 유닛은 스킵.
        /// </summary>
        public static void TryStartPreCompute()
        {
            if (_isPreComputing) return;

            // LLM 전역 설정 확인
            if (!(Main.Settings?.EnableLLMCombatAI ?? false)) return;

            var party = Game.Instance?.Player?.PartyAndPets;
            if (party == null) return;

            foreach (var unit in party)
            {
                if (unit == null) continue;
                if (!unit.IsInCombat) continue;
                if (unit.LifeState?.IsConscious != true) continue;

                // LLM-enabled 확인
                var charSettings = Main.Settings?.GetOrCreateSettings(unit.UniqueId, unit.CharacterName);
                if (charSettings == null || !charSettings.EnableLLMJudge) continue;

                // 이미 pre-compute 완료
                if (_preComputed.ContainsKey(unit.UniqueId)) continue;

                // 이 유닛에 대해 pre-compute 시작
                _isPreComputing = true;

                // 역할 결정
                var configuredRole = charSettings.Role;
                if (configuredRole == AIRole.Auto)
                    configuredRole = Analysis.RoleDetector.DetectOptimalRole(unit);

                // 간이 Situation 생성을 위한 SituationAnalyzer 사용
                Situation situation = null;
                try
                {
                    var analyzer = new SituationAnalyzer();
                    situation = analyzer.Analyze(unit, null);
                }
                catch (Exception ex)
                {
                    Main.LogDebug($"[LLMPreCompute] Situation analysis failed for {unit.CharacterName}: {ex.Message}");
                    _isPreComputing = false;
                    return;
                }

                if (situation == null)
                {
                    _isPreComputing = false;
                    return;
                }

                string unitId = unit.UniqueId;
                string unitName = unit.CharacterName;
                int enemyCount = situation.Enemies?.Count ?? 0;

                // 캐시도 먼저 체크 — 캐시 히트면 pre-compute 불필요
                var hash = LLMScorerCache.ComputeHash(situation, configuredRole);
                if (LLMScorerCache.TryGet(hash, out var cachedWeights))
                {
                    _preComputed[unitId] = cachedWeights;
                    _isPreComputing = false;
                    Main.LogDebug($"[LLMPreCompute] {unitName}: Cache hit during pre-compute (hash={hash})");
                    // 다음 유닛 시도를 위해 루프 계속
                    continue;
                }

                Main.LogDebug($"[LLMPreCompute] Starting pre-compute for {unitName} ({configuredRole}, enemies={enemyCount})");

                MachineSpirit.CoroutineRunner.Start(PreComputeCoroutine(
                    situation, configuredRole.ToString(), enemyCount, unitId, unitName, hash));
                return; // 한 번에 하나만
            }
        }

        private static IEnumerator PreComputeCoroutine(
            Situation situation, string roleName, int enemyCount,
            string unitId, string unitName, long cacheHash)
        {
            ScorerWeights result = null;

            yield return LLMScorer.Score(
                situation, roleName, enemyCount,
                w => result = w);

            if (result != null)
            {
                _preComputed[unitId] = result;
                // 캐시에도 저장 (다른 유닛 동일 상황 시 재사용)
                LLMScorerCache.Store(cacheHash, result);
                Main.LogDebug($"[LLMPreCompute] {unitName}: Pre-compute done ({result})");
            }
            else
            {
                Main.LogDebug($"[LLMPreCompute] {unitName}: Pre-compute returned null");
            }

            _isPreComputing = false;

            // 완료 후 다음 유닛 시도
            TryStartPreCompute();
        }

        /// <summary>
        /// 해당 유닛에 대한 pre-computed 결과 조회.
        /// 결과를 소비하면 딕셔너리에서 제거 (1회성).
        /// </summary>
        public static bool TryGetPreComputed(string unitId, out ScorerWeights weights)
        {
            if (_preComputed.TryGetValue(unitId, out weights))
            {
                _preComputed.Remove(unitId);
                return true;
            }
            weights = null;
            return false;
        }

        /// <summary>전체 상태 클리어 (전투 종료 시)</summary>
        public static void Clear()
        {
            _preComputed.Clear();
            _isPreComputing = false;
        }
    }
}
