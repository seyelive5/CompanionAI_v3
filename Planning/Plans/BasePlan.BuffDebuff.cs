using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;
using CompanionAI_v3.Planning.Planners;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning.Plans
{
    public abstract partial class BasePlan
    {
        #region Buff/Debuff - Delegates to BuffPlanner

        protected PlannedAction PlanBuffWithReservation(Situation situation, ref float remainingAP, float reservedAP)
            => BuffPlanner.PlanBuffWithReservation(situation, ref remainingAP, reservedAP, RoleName, _plannedBuffGuids);

        protected PlannedAction PlanDefensiveStanceWithReservation(Situation situation, ref float remainingAP, float reservedAP)
            => BuffPlanner.PlanDefensiveStanceWithReservation(situation, ref remainingAP, reservedAP, RoleName, _plannedBuffGuids);

        protected PlannedAction PlanAttackBuffWithReservation(Situation situation, ref float remainingAP, float reservedAP)
            => BuffPlanner.PlanAttackBuffWithReservation(situation, ref remainingAP, reservedAP, RoleName, _plannedBuffGuids);

        /// <summary>
        /// ★ v3.10.0: 전략이 추천한 특정 버프를 사용하는 헬퍼
        /// TurnStrategyPlanner가 선택한 최적 버프를 Phase 4에서 직접 적용
        /// </summary>
        protected PlannedAction PlanSpecificBuff(Situation situation, AbilityData buff, ref float remainingAP, float reservedAP)
        {
            if (buff == null) return null;

            // ★ v3.104.0: 이미 이 플랜에서 선택된 버프면 스킵 (strategy.RecommendedBuff 중복 방지)
            string buffGuid = buff.Blueprint?.AssetGuid?.ToString() ?? buff.Name ?? "";
            if (_plannedBuffGuids.Contains(buffGuid))
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] PlanSpecificBuff skip {buff.Name}: already planned this turn");
                return null;
            }

            float cost = CombatAPI.GetAbilityAPCost(buff);
            if (cost > remainingAP - reservedAP) return null;

            var target = new TargetWrapper(situation.Unit);
            string reason;
            if (!CombatAPI.CanUseAbilityOn(buff, target, out reason))
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] PlanSpecificBuff: {buff.Name} unavailable — {reason}");
                return null;
            }

            remainingAP -= cost;
            _plannedBuffGuids.Add(buffGuid);  // ★ v3.104.0: dedup 등록
            return PlannedAction.Buff(buff, situation.Unit, "Strategy-recommended buff", cost);
        }

        /// <summary>★ v3.40.0: Cautious/Confident Approach 스탠스 자동 선택</summary>
        protected PlannedAction PlanApproachStance(Situation situation, bool preferOffensive)
            => BuffPlanner.PlanApproachStance(situation, preferOffensive, RoleName);

        protected PlannedAction PlanTaunt(Situation situation, ref float remainingAP)
            => BuffPlanner.PlanTaunt(situation, ref remainingAP, RoleName);

        protected PlannedAction PlanHeroicAct(Situation situation, ref float remainingAP)
            => BuffPlanner.PlanHeroicAct(situation, ref remainingAP, RoleName, _plannedBuffGuids);

        // ★ v3.8.41: 통합 궁극기 계획 (모든 타겟 유형 처리)
        protected PlannedAction PlanUltimate(Situation situation, ref float remainingAP)
            => BuffPlanner.PlanUltimate(situation, ref remainingAP, RoleName);

        protected PlannedAction PlanDebuff(Situation situation, BaseUnitEntity target, ref float remainingAP)
            => BuffPlanner.PlanDebuff(situation, target, ref remainingAP, RoleName, _plannedBuffGuids);

        protected PlannedAction PlanMarker(Situation situation, BaseUnitEntity target, ref float remainingAP)
            => BuffPlanner.PlanMarker(situation, target, ref remainingAP, RoleName);

        protected PlannedAction PlanDefensiveBuff(Situation situation, ref float remainingAP)
            => BuffPlanner.PlanDefensiveBuff(situation, ref remainingAP, RoleName, _plannedBuffGuids);

        protected PlannedAction PlanPositionalBuff(Situation situation, ref float remainingAP, HashSet<string> usedBuffGuids = null)
            => BuffPlanner.PlanPositionalBuff(situation, ref remainingAP, usedBuffGuids ?? _plannedBuffGuids, RoleName);

        protected PlannedAction PlanStratagem(Situation situation, ref float remainingAP)
            => BuffPlanner.PlanStratagem(situation, ref remainingAP, RoleName);

        // ★ v3.5.80: attackPlanned 파라미터 추가
        protected PlannedAction PlanPostAction(Situation situation, ref float remainingAP, bool attackPlanned = false)
            => BuffPlanner.PlanPostAction(situation, ref remainingAP, RoleName, attackPlanned);

        protected PlannedAction PlanTurnEndingAbility(Situation situation, ref float remainingAP)
            => BuffPlanner.PlanTurnEndingAbility(situation, ref remainingAP, RoleName);

        protected bool IsEssentialBuff(AbilityData ability, Situation situation)
            => BuffPlanner.IsEssentialBuff(ability, situation);

        protected bool CanAffordBuffWithReservation(float buffCost, float remainingAP, float reservedAP, bool isEssential)
            => BuffPlanner.CanAffordBuffWithReservation(buffCost, remainingAP, reservedAP, isEssential);

        /// <summary>
        /// ★ v3.7.93: 아군 버프 계획 (BasePlan으로 이동)
        /// Support/Overseer 모두 사용 가능
        /// ★ v3.8.16: 턴 부여 능력 중복 방지 파라미터 추가 (쳐부숴라 등)
        /// </summary>
        /// <param name="situation">현재 상황</param>
        /// <param name="remainingAP">남은 AP</param>
        /// <param name="usedKeystoneGuids">키스톤 루프에서 이미 사용된 버프 GUID (중복 방지)</param>
        /// <param name="plannedTurnGrantTargetIds">★ v3.8.16: 이미 턴 부여가 계획된 대상 ID (중복 방지)</param>
        /// <param name="plannedBuffTargetPairs">★ v3.8.51: 이미 계획된 (버프GUID:타겟ID) 쌍 (같은 버프를 다른 아군에게 사용 허용)</param>
        /// <param name="plannedAbilityUseCounts">★ v3.14.2: 능력별 계획 횟수 추적 (GetAvailableForCastCount 초과 방지)</param>
        /// <returns>아군 버프 행동 또는 null</returns>
        protected PlannedAction PlanAllyBuff(Situation situation, ref float remainingAP, HashSet<string> usedKeystoneGuids = null, HashSet<string> plannedTurnGrantTargetIds = null, HashSet<string> plannedBuffTargetPairs = null, Dictionary<string, int> plannedAbilityUseCounts = null)
        {
            // ★ v3.2.15: 팀 전술에 따라 버프 대상 우선순위 조정
            var tactic = TeamBlackboard.Instance.CurrentTactic;
            var prioritizedTargets = new List<BaseUnitEntity>();

            if (tactic == TacticalSignal.Retreat)
            {
                // 후퇴: 가장 위험한 아군 우선 (생존 버프)
                var mostWounded = TeamBlackboard.Instance.GetMostWoundedAlly();
                if (mostWounded != null)
                {
                    prioritizedTargets.Add(mostWounded);
                    if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] AllyBuff: Retreat tactic - buff wounded ally {mostWounded.CharacterName}");
                }
            }
            else if (tactic == TacticalSignal.Attack)
            {
                // ★ v3.8.78: .Where() LINQ 제거 → inline 가드
                // ★ v3.18.4: CombatantAllies 사용 (사역마 제외)
                // 공격: DPS 우선 버프 (HP 50% 이상인 DPS)
                foreach (var ally in situation.CombatantAllies)
                {
                    if (ally == null || ally.LifeState.IsDead) continue;
                    var settings = ModSettings.Instance?.GetOrCreateSettings(ally.UniqueId, ally.CharacterName);
                    if (settings?.Role == AIRole.DPS && CombatCache.GetHPPercent(ally) > 50f)
                    {
                        prioritizedTargets.Add(ally);
                    }
                }
                if (prioritizedTargets.Count > 0)
                {
                    if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] AllyBuff: Attack tactic - buff DPS first");
                }
            }

            // ★ v3.18.4: CombatantAllies 사용 (사역마에게 버프 낭비 방지)
            // 기본 우선순위 (Defend 또는 위에서 대상 없을 때): Tank > DPS > 본인 > 기타
            // 1. Tank 역할 먼저
            foreach (var ally in situation.CombatantAllies)
            {
                if (ally == null || ally.LifeState.IsDead) continue;
                var settings = ModSettings.Instance?.GetOrCreateSettings(ally.UniqueId, ally.CharacterName);
                if (settings?.Role == AIRole.Tank && !prioritizedTargets.Contains(ally))
                    prioritizedTargets.Add(ally);
            }

            // 2. DPS 역할
            foreach (var ally in situation.CombatantAllies)
            {
                if (ally == null || ally.LifeState.IsDead) continue;
                var settings = ModSettings.Instance?.GetOrCreateSettings(ally.UniqueId, ally.CharacterName);
                if (settings?.Role == AIRole.DPS && !prioritizedTargets.Contains(ally))
                    prioritizedTargets.Add(ally);
            }

            // 3. 본인
            if (!prioritizedTargets.Contains(situation.Unit))
                prioritizedTargets.Add(situation.Unit);

            // 4. 나머지 아군
            foreach (var ally in situation.CombatantAllies)
            {
                if (ally == null || ally.LifeState.IsDead) continue;
                if (!prioritizedTargets.Contains(ally))
                    prioritizedTargets.Add(ally);
            }

            // ★ v3.22.4: 턴 순서 인지 — 이미 행동한 아군을 뒤로 이동
            // 역할 우선순위(Tactic→Tank→DPS→Self→Rest) 유지하면서, 행동 예정 아군이 버프 우선 수령
            TargetScorer.RefreshTurnOrderCache(situation.Unit);
            {
                var notActed = new List<BaseUnitEntity>();
                var acted = new List<BaseUnitEntity>();
                foreach (var t in prioritizedTargets)
                {
                    if (t.Initiative?.ActedThisRound == true)
                        acted.Add(t);
                    else
                        notActed.Add(t);
                }
                prioritizedTargets.Clear();
                prioritizedTargets.AddRange(notActed);
                prioritizedTargets.AddRange(acted);
            }

            foreach (var buff in situation.AvailableBuffs)
            {
                if (buff.Blueprint?.CanTargetFriends != true) continue;

                // ★ v3.40.4: 공격 능력이 아군 버프로 잘못 사용되는 것 방지
                // 무기 능력(Weapon != null)은 공격 → 아군에게 사용하면 피해를 입힘
                // 예: 죽음의 환영 (영웅적 행위) = CanTargetFriends이지만 무기 공격 → 아군 즉사
                if (buff.Weapon != null) continue;

                // ★ v3.7.07 Fix: 실제 사역마에게 성공한 버프만 스킵
                string buffGuid = buff.Blueprint?.AssetGuid?.ToString();
                if (!string.IsNullOrEmpty(buffGuid) && usedKeystoneGuids != null && usedKeystoneGuids.Contains(buffGuid))
                {
                    if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Skip {buff.Name} - successfully used on familiar in Keystone phase");
                    continue;
                }

                float cost = CombatAPI.GetAbilityAPCost(buff);
                if (cost > remainingAP) continue;

                // ★ v3.14.2: 이미 계획된 횟수가 사용 가능 횟수 이상이면 스킵
                // 게임 API GetAvailableForCastCount()로 실제 사용 가능 횟수 확인
                if (plannedAbilityUseCounts != null && !string.IsNullOrEmpty(buffGuid))
                {
                    plannedAbilityUseCounts.TryGetValue(buffGuid, out int plannedUses);
                    if (plannedUses > 0)
                    {
                        try
                        {
                            int availableCasts = buff.GetAvailableForCastCount();
                            if (availableCasts > 0 && plannedUses >= availableCasts)
                            {
                                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Skip {buff.Name}: already planned {plannedUses}/{availableCasts} casts");
                                continue;
                            }
                        }
                        catch { }
                    }
                }

                // ★ v3.7.87: 턴 전달 능력인지 확인 (쳐부숴라 등)
                bool isTurnGrant = AbilityDatabase.IsTurnGrantAbility(buff);

                // ★ v3.28.0: Officer BringItDown → 캐리 유닛 최우선
                // BringItDown은 아군에게 사용하는 공격 강화 버프 → DPS 캐리에게 집중
                var targetList = prioritizedTargets;
                if (AbilityDatabase.IsBringItDown(buff))
                {
                    string carryId = TeamBlackboard.Instance.HeroicActPriorityUnitId;
                    if (carryId != null)
                    {
                        BaseUnitEntity carryUnit = null;
                        foreach (var t in prioritizedTargets)
                        {
                            if (t.UniqueId == carryId) { carryUnit = t; break; }
                        }
                        if (carryUnit != null)
                        {
                            // 캐리 유닛을 맨 앞으로 (나머지 순서 유지)
                            targetList = new List<BaseUnitEntity> { carryUnit };
                            foreach (var t in prioritizedTargets)
                            {
                                if (t != carryUnit) targetList.Add(t);
                            }
                            if (Main.IsDebugEnabled)
                                Log.Planning.Debug($"[{RoleName}] BringItDown: carry unit {carryUnit.CharacterName} prioritized");
                        }
                    }
                }

                foreach (var target in targetList)
                {
                    // ★ v3.8.51: 이미 계획된 (버프, 타겟) 쌍 스킵
                    // 같은 버프를 다른 아군에게는 사용 가능하지만, 동일 조합은 중복 방지
                    string targetId = target.UniqueId ?? target.CharacterName ?? "unknown";
                    if (plannedBuffTargetPairs != null && !string.IsNullOrEmpty(buffGuid))
                    {
                        string pairKey = $"{buffGuid}:{targetId}";
                        if (plannedBuffTargetPairs.Contains(pairKey))
                            continue;
                    }

                    // ★ v3.7.95: 스마트 버프 체크 - 버프 지속시간 확인해서 갱신 필요 여부 판단
                    // NeedsBuffRefresh: 버프 없거나 2라운드 이하 남으면 true (갱신 필요)
                    if (!CombatAPI.NeedsBuffRefresh(target, buff))
                    {
                        int remaining = CombatAPI.GetBuffRemainingRounds(target, buff);
                        string durStr = remaining == -1 ? "영구" : $"{remaining}R";
                        if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Skip {buff.Name} -> {target.CharacterName}: buff active ({durStr} remaining)");
                        continue;
                    }

                    // ★ v3.7.87: 턴 전달 능력은 이미 행동한 유닛에게 쓰면 낭비
                    if (isTurnGrant && TeamBlackboard.Instance.HasActedThisRound(target))
                    {
                        if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Skip {buff.Name} -> {target.CharacterName}: already acted this round");
                        continue;
                    }

                    // ★ v3.8.16: 턴 전달 능력이 이미 이 턴에 계획된 대상에게 중복 사용 방지
                    // 같은 계획 단계에서 같은 대상에게 여러 번 쳐부숴라 계획 방지
                    if (isTurnGrant && plannedTurnGrantTargetIds != null && plannedTurnGrantTargetIds.Contains(targetId))
                    {
                        if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Skip {buff.Name} -> {target.CharacterName}: turn grant already planned for this target");
                        continue;
                    }

                    var targetWrapper = new TargetWrapper(target);
                    string reason;
                    if (CombatAPI.CanUseAbilityOn(buff, targetWrapper, out reason))
                    {
                        // ★ v3.8.16: 턴 전달 능력 성공 시 대상 ID 기록
                        if (isTurnGrant && plannedTurnGrantTargetIds != null)
                        {
                            plannedTurnGrantTargetIds.Add(targetId);
                            Log.Planning.Info($"[{RoleName}] Turn grant planned: {buff.Name} -> {target.CharacterName} (tracked for duplicate prevention)");
                        }

                        remainingAP -= cost;
                        Log.Planning.Info($"[{RoleName}] Buff ally: {buff.Name} -> {target.CharacterName}");
                        return PlannedAction.Buff(buff, target, $"Buff {target.CharacterName}", cost);
                    }
                }
            }

            return null;
        }

        #endregion
    }
}
