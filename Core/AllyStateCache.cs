using System;
using System.Collections.Generic;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Buffs.Blueprints;
using Kingmaker.UnitLogic.Mechanics.Actions;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;

namespace CompanionAI_v3.Core
{
    /// <summary>
    /// ★ v3.8.58: 중앙 아군 상태 캐시
    /// - SituationAnalyzer.Analyze()에서 Refresh → 모든 컴포넌트가 읽음
    /// - Replan 시에도 Refresh되므로 항상 최신 상태
    /// - Master의 Warp Relay 버프 타입별 커버리지를 정확히 추적
    /// </summary>
    public static class AllyStateCache
    {
        #region Data Structures

        public struct AllyState
        {
            public BaseUnitEntity Unit;
            public string UnitId;
            public float CurrentHP;
            public float MaxHP;
            public float HPPercent;
            public bool IsConscious;

            /// <summary>Master가 시전한 사이킨 버프의 능력 GUID 목록 (이 아군에게 활성화된 것)</summary>
            public HashSet<string> MasterPsychicBuffGuids;

            /// <summary>모든 활성 버프의 Blueprint GUID 목록</summary>
            public HashSet<string> ActiveBuffGuids;
        }

        #endregion

        #region State

        private static readonly Dictionary<string, AllyState> _states = new Dictionary<string, AllyState>();
        private static BaseUnitEntity _master;
        private static readonly HashSet<string> _masterWarpRelayBuffGuids = new HashSet<string>();
        private static int _masterWarpRelayBuffCount;
        private static bool _initialized;

        /// <summary>
        /// ★ v3.8.58: 능력 → 적용 버프 Blueprint 매핑 캐시
        /// 능력의 AbilityEffectRunAction을 파싱하여 ContextActionApplyBuff를 추출.
        /// 능력 블루프린트는 전투 중 변하지 않으므로 전투 간에도 유지 (Clear에서 초기화 안함).
        /// </summary>
        private static readonly Dictionary<string, List<BuffMapping>> _abilityToBuffs = new Dictionary<string, List<BuffMapping>>();

        private struct BuffMapping
        {
            public string BuffBlueprintGuid;
            public BlueprintBuff Blueprint;
        }

        #endregion

        #region Lifecycle

        /// <summary>
        /// SituationAnalyzer에서 호출 — 아군 상태 전체 갱신
        /// Replan 시에도 호출되므로 버프 확산 후 새로운 상태 반영
        /// </summary>
        public static void Refresh(BaseUnitEntity master, List<BaseUnitEntity> allies)
        {
            _states.Clear();
            _master = master;

            if (master == null || allies == null)
            {
                _initialized = false;
                return;
            }

            // 1. Master의 Warp Relay 대상 버프 능력 파악
            DiscoverMasterWarpRelayBuffs(master);

            // 2. 각 아군 상태 수집
            for (int i = 0; i < allies.Count; i++)
            {
                var ally = allies[i];
                if (ally == null || ally == master) continue;
                if (FamiliarAPI.IsFamiliar(ally)) continue;
                if (!ally.IsConscious) continue;

                var state = BuildAllyState(master, ally);
                _states[ally.UniqueId] = state;
            }

            _initialized = true;

            Main.LogDebug($"[AllyStateCache] Refreshed: {_states.Count} allies, " +
                $"{_masterWarpRelayBuffCount} master WR buff types, " +
                $"coverage={GetPsychicBuffCoverage():P0}");
        }

        /// <summary>전투 종료 시 캐시 완전 초기화</summary>
        public static void Clear()
        {
            _states.Clear();
            _master = null;
            _masterWarpRelayBuffGuids.Clear();
            _masterWarpRelayBuffCount = 0;
            _initialized = false;
        }

        #endregion

        #region Master Ability Discovery

        /// <summary>
        /// Master의 Warp Relay로 확산 가능한 버프 능력 GUID 수집
        /// - FamiliarAbilities.IsWarpRelayTarget() + CanTargetFriends 필터
        /// - 디버프(적 대상만)는 제외: coverage는 아군 버프만 추적
        /// </summary>
        private static void DiscoverMasterWarpRelayBuffs(BaseUnitEntity master)
        {
            _masterWarpRelayBuffGuids.Clear();
            _masterWarpRelayBuffCount = 0;

            var rawFacts = master.Abilities?.RawFacts;
            if (rawFacts == null) return;

            for (int i = 0; i < rawFacts.Count; i++)
            {
                try
                {
                    var abilityData = rawFacts[i].Data;
                    if (abilityData?.Blueprint == null) continue;

                    // 버프만 (CanTargetFriends) — 디버프(감각 박탈 등)는 coverage에서 제외
                    if (!abilityData.Blueprint.CanTargetFriends) continue;

                    // Warp Relay 대상인지 확인 (사이킨 + 비피해 + 유닛 타겟)
                    if (!FamiliarAbilities.IsWarpRelayTarget(abilityData)) continue;

                    string guid = abilityData.Blueprint.AssetGuid?.ToString();
                    if (!string.IsNullOrEmpty(guid) && _masterWarpRelayBuffGuids.Add(guid))
                    {
                        _masterWarpRelayBuffCount++;
                        Main.LogDebug($"[AllyStateCache] WR buff discovered: {abilityData.Name} ({guid})");
                    }
                }
                catch (Exception ex)
                {
                    Main.LogDebug($"[AllyStateCache] DiscoverWRBuffs error: {ex.Message}");
                }
            }

            Main.LogDebug($"[AllyStateCache] Master {master.CharacterName}: {_masterWarpRelayBuffCount} Warp Relay buff types");
        }

        #endregion

        #region Per-Ally State Building

        private static AllyState BuildAllyState(BaseUnitEntity master, BaseUnitEntity ally)
        {
            var state = new AllyState
            {
                Unit = ally,
                UnitId = ally.UniqueId,
                CurrentHP = ally.Health?.HitPointsLeft ?? 0,
                MaxHP = ally.Health?.MaxHitPoints ?? 1,
                IsConscious = ally.IsConscious,
                MasterPsychicBuffGuids = new HashSet<string>(),
                ActiveBuffGuids = new HashSet<string>()
            };
            state.HPPercent = state.MaxHP > 0 ? state.CurrentHP / state.MaxHP : 0f;

            if (ally.Buffs == null) return state;

            foreach (var buff in ally.Buffs)
            {
                if (buff?.Blueprint == null) continue;

                // 모든 활성 버프 Blueprint GUID 수집
                string buffBpGuid = buff.Blueprint.AssetGuid?.ToString();
                if (!string.IsNullOrEmpty(buffBpGuid))
                    state.ActiveBuffGuids.Add(buffBpGuid);

                // Master가 시전한 사이킨 버프 추적 (능력 GUID 기준)
                if (buff.Context?.MaybeCaster == master)
                {
                    var sourceAbility = buff.Context.SourceAbility;
                    if (sourceAbility != null && sourceAbility.IsPsykerAbility && sourceAbility.CanTargetFriends)
                    {
                        string abilityGuid = sourceAbility.AssetGuid?.ToString();
                        if (!string.IsNullOrEmpty(abilityGuid))
                            state.MasterPsychicBuffGuids.Add(abilityGuid);
                    }
                }
            }

            return state;
        }

        #endregion

        #region Public API — Psychic Buff Coverage

        /// <summary>
        /// 사이킨 버프 전체 커버리지 (0.0 ~ 1.0)
        /// = 총 (아군, 버프) 인스턴스 / (의식있는 아군 수 × 마스터 WR 버프 종류 수)
        ///
        /// 예: 마스터 WR 버프 2종 (조짐 + 선견지명), 아군 5명, 조짐만 확산
        /// → totalInstances=5, maxPossible=10, coverage=50% → BUFF PHASE 유지
        ///
        /// 예: 모두 확산 → totalInstances=10, maxPossible=10, coverage=100% → DEBUFF PHASE
        /// </summary>
        public static float GetPsychicBuffCoverage()
        {
            if (!_initialized || _masterWarpRelayBuffCount == 0 || _states.Count == 0)
                return 0f;

            int totalInstances = 0;
            int consciousAllies = 0;

            foreach (var kvp in _states)
            {
                var state = kvp.Value;
                if (!state.IsConscious) continue;
                consciousAllies++;

                foreach (var wrGuid in _masterWarpRelayBuffGuids)
                {
                    if (state.MasterPsychicBuffGuids.Contains(wrGuid))
                        totalInstances++;
                }
            }

            int maxPossible = consciousAllies * _masterWarpRelayBuffCount;
            return maxPossible > 0 ? (float)totalInstances / maxPossible : 0f;
        }

        /// <summary>Master의 Warp Relay 대상 버프 종류 수</summary>
        public static int MasterWarpRelayBuffCount => _masterWarpRelayBuffCount;

        /// <summary>사이킨 버프 커버리지가 충분한지 (BUFF 페이즈 필요 여부)</summary>
        public static bool IsBuffPhaseNeeded()
        {
            return _initialized && _masterWarpRelayBuffCount > 0 && GetPsychicBuffCoverage() < 0.6f;
        }

        /// <summary>특정 WR 버프가 필요한 아군 수 (해당 능력의 버프 미보유)</summary>
        public static int CountAlliesNeedingWarpRelayBuff(string abilityGuid)
        {
            if (!_initialized || string.IsNullOrEmpty(abilityGuid)) return 0;

            int count = 0;
            foreach (var kvp in _states)
            {
                var state = kvp.Value;
                if (!state.IsConscious) continue;
                if (!state.MasterPsychicBuffGuids.Contains(abilityGuid))
                    count++;
            }
            return count;
        }

        #endregion

        #region Public API — Buff Query (CombatAPI.HasActiveBuff 대체)

        /// <summary>
        /// 능력이 적용하는 버프 Blueprint 매핑을 캐시에서 조회/생성
        /// AbilityEffectRunAction 파싱은 능력당 1회만 수행
        /// </summary>
        private static List<BuffMapping> GetBuffMappings(AbilityData ability)
        {
            string abilityGuid = ability.Blueprint?.AssetGuid?.ToString();
            if (string.IsNullOrEmpty(abilityGuid)) return null;

            if (_abilityToBuffs.TryGetValue(abilityGuid, out var cached))
                return cached;

            // 최초 호출: 능력의 적용 버프 Blueprint 파싱
            var mappings = new List<BuffMapping>();
            try
            {
                var runAction = ability.Blueprint.GetComponent<AbilityEffectRunAction>();
                if (runAction?.Actions?.Actions != null)
                {
                    for (int i = 0; i < runAction.Actions.Actions.Length; i++)
                    {
                        if (runAction.Actions.Actions[i] is ContextActionApplyBuff applyBuff && applyBuff.Buff != null)
                        {
                            string bpGuid = applyBuff.Buff.AssetGuid?.ToString();
                            if (!string.IsNullOrEmpty(bpGuid))
                            {
                                mappings.Add(new BuffMapping
                                {
                                    BuffBlueprintGuid = bpGuid,
                                    Blueprint = applyBuff.Buff
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[AllyStateCache] GetBuffMappings error for {ability.Name}: {ex.Message}");
            }

            _abilityToBuffs[abilityGuid] = mappings;
            return mappings;
        }

        /// <summary>
        /// 특정 유닛이 특정 능력의 버프를 보유 중인지 확인
        /// - 캐시된 아군: ActiveBuffGuids에서 직접 조회 (게임 API 호출 0회)
        /// - 기타 유닛: 캐시된 BlueprintBuff로 unit.Buffs.GetBuff() 1회 (기존 대비 파싱 생략)
        ///
        /// CombatAPI.HasActiveBuff 대체용. 능력 블루프린트 파싱이 캐시되므로 반복 호출 시 빠름.
        /// </summary>
        public static bool HasBuff(BaseUnitEntity unit, AbilityData ability)
        {
            if (unit == null || ability?.Blueprint == null) return false;

            var mappings = GetBuffMappings(ability);
            if (mappings == null || mappings.Count == 0) return false;

            // 경로 1: 캐시된 아군 → GUID 비교만으로 판정 (게임 API 호출 없음)
            if (_initialized && _states.TryGetValue(unit.UniqueId, out var state))
            {
                for (int i = 0; i < mappings.Count; i++)
                {
                    if (state.ActiveBuffGuids.Contains(mappings[i].BuffBlueprintGuid))
                        return true;
                }
                return false;
            }

            // 경로 2: 캐시 외 유닛 → 캐시된 BlueprintBuff 참조로 직접 조회
            try
            {
                for (int i = 0; i < mappings.Count; i++)
                {
                    if (mappings[i].Blueprint != null && unit.Buffs.GetBuff(mappings[i].Blueprint) != null)
                        return true;
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[AllyStateCache] HasBuff error: {ex.Message}");
            }

            return false;
        }

        #endregion

        #region Public API — General Queries

        /// <summary>캐시 초기화 여부</summary>
        public static bool IsInitialized => _initialized;

        /// <summary>캐시된 아군 수</summary>
        public static int AllyCount => _states.Count;

        /// <summary>특정 아군의 상태 조회 (캐시에 없으면 null)</summary>
        public static AllyState? GetAllyState(BaseUnitEntity unit)
        {
            if (!_initialized || unit == null) return null;
            return _states.TryGetValue(unit.UniqueId, out var state) ? state : (AllyState?)null;
        }

        #endregion
    }
}
