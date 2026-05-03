using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Planning.Plans
{
    public abstract partial class BasePlan
    {
        #region AOE Heal/Buff (v3.1.17)

        /// <summary>
        /// ★ v3.1.17: AOE 힐 계획 - 다수 부상 아군 힐
        /// </summary>
        protected PlannedAction PlanAoEHeal(Situation situation, ref float remainingAP)
        {
            // ★ v3.8.78: LINQ → CollectionHelper (0 할당)
            // Point 타겟 힐 능력 찾기
            CollectionHelper.FillWhere(situation.AvailableHeals, _tempAbilities,
                a => CombatAPI.IsPointTargetAbility(a));

            if (_tempAbilities.Count == 0) return null;

            // ★ v3.18.6: Allies 사용 — AoE 힐은 범위 내 모든 유닛에 영향, 사역마 포함
            CollectionHelper.FillWhere(situation.Allies, _tempUnits,
                a => a.IsConscious && CombatCache.GetHPPercent(a) < 80f);

            // 캐스터도 부상이면 추가
            if (CombatCache.GetHPPercent(situation.Unit) < 80f)
                _tempUnits.Add(situation.Unit);

            if (_tempUnits.Count < 2) return null;  // AOE 힐은 2명 이상 필요

            // ★ v3.12.2: ScoreHeal 기반 최적 AoE 힐 선택 (기존 first-available 대체)
            // 대표 타겟: 가장 부상이 심한 아군 (ScoreHeal 기준점)
            BaseUnitEntity mostWounded = _tempUnits[0];
            float lowestHP = CombatCache.GetHPPercent(mostWounded);
            for (int u = 1; u < _tempUnits.Count; u++)
            {
                float hp = CombatCache.GetHPPercent(_tempUnits[u]);
                if (hp < lowestHP) { lowestHP = hp; mostWounded = _tempUnits[u]; }
            }

            AbilityData bestAbility = null;
            float bestScore = float.MinValue;
            float bestCost = 0f;
            AoESafetyChecker.AoEScore bestAoEPosition = null;

            for (int i = 0; i < _tempAbilities.Count; i++)
            {
                var ability = _tempAbilities[i];
                float cost = CombatAPI.GetAbilityAPCost(ability);
                if (cost > remainingAP) continue;

                var candidatePosition = AoESafetyChecker.FindBestAllyAoEPosition(
                    ability,
                    situation.Unit,
                    _tempUnits,
                    minAlliesRequired: 2,
                    requiresWounded: true);

                if (candidatePosition == null || !candidatePosition.IsSafe) continue;

                string reason;
                if (!CombatAPI.CanUseAbilityOnPoint(ability, candidatePosition.Position, out reason))
                {
                    if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] AOE Heal blocked: {ability.Name} - {reason}");
                    continue;
                }

                float score = UtilityScorer.ScoreHeal(ability, mostWounded, situation);
                score += candidatePosition.AlliesHit * 15f;  // AoE 보너스: 적중 아군 수

                if (score > bestScore)
                {
                    bestScore = score;
                    bestAbility = ability;
                    bestCost = cost;
                    bestAoEPosition = candidatePosition;
                }
            }

            if (bestAbility == null) return null;

            remainingAP -= bestCost;
            Log.Planning.Info($"[{RoleName}] AOE Heal: {bestAbility.Name} at ({bestAoEPosition.Position.x:F1},{bestAoEPosition.Position.z:F1}) " +
                $"- {bestAoEPosition.AlliesHit} allies (score={bestScore:F1})");

            return PlannedAction.PositionalHeal(
                bestAbility,
                bestAoEPosition.Position,
                $"AOE Heal on {bestAoEPosition.AlliesHit} allies",
                bestCost);
        }

        /// <summary>
        /// ★ v3.1.17: AOE 버프 계획 - 다수 아군 버프
        /// </summary>
        protected PlannedAction PlanAoEBuff(Situation situation, ref float remainingAP)
        {
            // ★ v3.8.78: LINQ → CollectionHelper (0 할당)
            // Point 타겟 버프 능력 찾기 (힐, 도발 제외)
            CollectionHelper.FillWhere(situation.AvailableBuffs, _tempAbilities,
                a => CombatAPI.IsPointTargetAbility(a) && !AbilityDatabase.IsTaunt(a) && !AbilityDatabase.IsHealing(a));

            if (_tempAbilities.Count == 0) return null;

            // ★ v3.18.6: Allies 사용 — AoE 버프는 범위 내 모든 유닛에 영향, 사역마 포함
            CollectionHelper.FillWhere(situation.Allies, _tempUnits,
                a => a.IsConscious);
            _tempUnits.Add(situation.Unit);

            if (_tempUnits.Count < 2) return null;  // AOE 버프는 2명 이상 필요

            for (int i = 0; i < _tempAbilities.Count; i++)
            {
                var ability = _tempAbilities[i];
                float cost = CombatAPI.GetAbilityAPCost(ability);
                if (cost > remainingAP) continue;

                // ★ v3.8.58: 이미 활성화된 버프 스킵 (캐시된 매핑 사용)
                if (AllyStateCache.HasBuff(situation.Unit, ability)) continue;

                var bestPosition = AoESafetyChecker.FindBestAllyAoEPosition(
                    ability,
                    situation.Unit,
                    _tempUnits,
                    minAlliesRequired: 2,
                    requiresWounded: false);

                if (bestPosition == null || !bestPosition.IsSafe) continue;

                string reason;
                if (!CombatAPI.CanUseAbilityOnPoint(ability, bestPosition.Position, out reason))
                {
                    if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] AOE Buff blocked: {ability.Name} - {reason}");
                    continue;
                }

                remainingAP -= cost;
                Log.Planning.Info($"[{RoleName}] AOE Buff: {ability.Name} at ({bestPosition.Position.x:F1},{bestPosition.Position.z:F1}) " +
                    $"- {bestPosition.AlliesHit} allies");

                return PlannedAction.PositionalBuff(
                    ability,
                    bestPosition.Position,
                    $"AOE Buff on {bestPosition.AlliesHit} allies",
                    cost);
            }

            return null;
        }

        #endregion
    }
}
