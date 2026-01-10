using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using UnityEngine;
using CompanionAI_v3.Core;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Data;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Analysis
{
    /// <summary>
    /// ★ v3.0.57: 시퀀스 최적화기
    ///
    /// 핵심 역할:
    /// 1. 여러 가능한 행동 시퀀스 생성
    /// 2. 각 시퀀스의 점수 계산
    /// 3. 최적 시퀀스 선택
    ///
    /// 주요 비교 시나리오:
    /// - "현재 위치에서 공격" vs "후퇴 → 공격"
    /// - "고데미지 위험 공격" vs "안전한 저데미지 공격"
    /// - "버프 → 공격" vs "직접 공격"
    ///
    /// 사용법:
    /// var optimizer = new SequenceOptimizer(situation);
    /// var bestSequence = optimizer.OptimizeAttackSequence(attacks, target);
    /// </summary>
    public class SequenceOptimizer
    {
        private readonly Situation _situation;
        private readonly float _roleSafetyWeight;
        private readonly string _logPrefix;

        public SequenceOptimizer(Situation situation, string logPrefix = "SequenceOpt")
        {
            _situation = situation ?? throw new ArgumentNullException(nameof(situation));
            _roleSafetyWeight = UtilityScorer.GetRoleSafetyWeight(situation);
            _logPrefix = logPrefix;
        }

        #region Public Optimization Methods

        /// <summary>
        /// ★ v3.0.59: 공격 시퀀스 최적화 (공격 스킵 옵션 추가)
        ///
        /// 가능한 옵션들:
        /// 1. 현재 위치에서 직접 공격
        /// 2. 후퇴 후 공격 (ClearMP 능력일 때 특히 중요)
        /// 3. 접근 후 공격 (근접 공격)
        /// 4. ★ NEW: 공격 스킵 (위험한 공격보다 안전 유지가 나을 때)
        /// </summary>
        public ActionSequence OptimizeAttackSequence(
            List<AbilityData> availableAttacks,
            BaseUnitEntity target,
            float remainingAP,
            float remainingMP)
        {
            if (availableAttacks == null || availableAttacks.Count == 0 || target == null)
                return null;

            var sequences = new List<ActionSequence>();

            // 1. 사용 가능한 공격들 필터링
            var usableAttacks = availableAttacks
                .Where(a => CombatAPI.GetAbilityAPCost(a) <= remainingAP)
                .Where(a => CanUseAttackOn(a, target))
                .ToList();

            if (usableAttacks.Count == 0)
                return null;

            // ★ v3.0.59: "공격 안 함" 옵션 추가 (ClearMP 능력이 있거나 Support 역할일 때)
            bool hasClearMPAbility = usableAttacks.Any(a => CombatAPI.AbilityClearsMPAfterUse(a));
            if (hasClearMPAbility || _roleSafetyWeight >= 0.6f)
            {
                var skipSequence = CreateSkipAttackSequence(remainingMP);
                if (skipSequence != null)
                {
                    sequences.Add(skipSequence);
                }
            }

            // 2. 각 공격에 대해 시퀀스 생성
            foreach (var attack in usableAttacks)
            {
                // Option A: 현재 위치에서 공격
                var directAttack = CreateDirectAttackSequence(attack, target);
                if (directAttack != null)
                {
                    sequences.Add(directAttack);
                }

                // Option B: 후퇴 후 공격 (ClearMP 능력이거나 위험 상황)
                if (ShouldConsiderRetreatFirst(attack))
                {
                    var retreatThenAttack = CreateRetreatThenAttackSequence(attack, target, remainingMP);
                    if (retreatThenAttack != null)
                    {
                        sequences.Add(retreatThenAttack);
                    }
                }
            }

            // 3. 점수 계산 및 비교
            if (sequences.Count == 0)
                return null;

            foreach (var seq in sequences)
            {
                seq.SimulateFinalState(_situation);
                seq.CalculateScore(_situation, _roleSafetyWeight);
            }

            // 4. 로깅 및 최적 시퀀스 선택
            LogSequenceComparison(sequences);

            var best = sequences.OrderByDescending(s => s.TotalScore).First();

            // ★ v3.0.59: "공격 안 함"이 선택되면 null 반환 (공격 스킵)
            if (best.Description == "Skip attack")
            {
                Main.Log($"[{_logPrefix}] Decision: Skip attack (safety priority, score={best.TotalScore:F0})");
                return null;
            }

            return best;
        }

        /// <summary>
        /// 여러 타겟에 대한 공격 시퀀스 최적화
        /// </summary>
        public ActionSequence OptimizeMultiTargetAttack(
            List<AbilityData> availableAttacks,
            List<BaseUnitEntity> targets,
            float remainingAP,
            float remainingMP)
        {
            if (targets == null || targets.Count == 0)
                return null;

            var allSequences = new List<ActionSequence>();

            // 각 타겟에 대해 최적 시퀀스 계산
            foreach (var target in targets.Where(t => t != null && !t.LifeState.IsDead))
            {
                var seq = OptimizeAttackSequence(availableAttacks, target, remainingAP, remainingMP);
                if (seq != null)
                {
                    allSequences.Add(seq);
                }
            }

            if (allSequences.Count == 0)
                return null;

            return allSequences.OrderByDescending(s => s.TotalScore).First();
        }

        /// <summary>
        /// "이동 필요" 상황에서 최적 접근 시퀀스
        /// </summary>
        public ActionSequence OptimizeApproachSequence(
            List<AbilityData> availableAttacks,
            BaseUnitEntity target,
            float remainingAP,
            float remainingMP)
        {
            if (target == null || remainingMP <= 0)
                return null;

            var sequences = new List<ActionSequence>();

            // 접근 후 공격 가능한 능력들
            var meleeAttacks = availableAttacks
                .Where(a => a.IsMelee)
                .Where(a => CombatAPI.GetAbilityAPCost(a) <= remainingAP)
                .ToList();

            var rangedAttacks = availableAttacks
                .Where(a => !a.IsMelee)
                .Where(a => CombatAPI.GetAbilityAPCost(a) <= remainingAP)
                .ToList();

            // Option A: 접근 → 근접 공격
            foreach (var melee in meleeAttacks)
            {
                var approachMelee = CreateApproachThenAttackSequence(melee, target, remainingMP, true);
                if (approachMelee != null)
                {
                    sequences.Add(approachMelee);
                }
            }

            // Option B: 적정 거리로 이동 → 원거리 공격
            foreach (var ranged in rangedAttacks)
            {
                var approachRanged = CreateApproachThenAttackSequence(ranged, target, remainingMP, false);
                if (approachRanged != null)
                {
                    sequences.Add(approachRanged);
                }
            }

            if (sequences.Count == 0)
                return null;

            foreach (var seq in sequences)
            {
                seq.SimulateFinalState(_situation);
                seq.CalculateScore(_situation, _roleSafetyWeight);
            }

            LogSequenceComparison(sequences);
            return sequences.OrderByDescending(s => s.TotalScore).First();
        }

        #endregion

        #region Sequence Creation Methods

        /// <summary>
        /// ★ v3.0.59: "공격 안 함" 시퀀스 생성
        ///
        /// 위험한 공격(ClearMP 등)을 스킵하고 안전을 유지하는 옵션.
        /// 점수 = Safety 점수만 (Offense = 0)
        /// </summary>
        private ActionSequence CreateSkipAttackSequence(float remainingMP)
        {
            var seq = new ActionSequence();
            seq.Description = "Skip attack";
            seq.ExpectedFinalPosition = _situation.Unit.Position;
            seq.ExpectedRemainingMP = remainingMP;  // MP 유지
            // 행동 없음 - Actions는 빈 리스트

            return seq;
        }

        private ActionSequence CreateDirectAttackSequence(AbilityData attack, BaseUnitEntity target)
        {
            var seq = new ActionSequence();
            seq.Description = "Direct attack";
            seq.AddAttack(attack, target, $"Direct attack on {target.CharacterName}");
            seq.ExpectedFinalPosition = _situation.Unit.Position;  // 이동 없음

            return seq;
        }

        /// <summary>
        /// ★ v3.0.60: MovementAPI 기반 실제 도달 가능한 타일 사용
        /// 기존 PositionEvaluator는 단순 Vector3 계산으로 벽/장애물 무시 문제
        /// </summary>
        private ActionSequence CreateRetreatThenAttackSequence(AbilityData attack, BaseUnitEntity target, float remainingMP)
        {
            if (remainingMP <= 0 || !_situation.CanMove)
                return null;

            // ★ v3.0.60: PathfindingService 기반 실제 도달 가능 위치
            var retreatScore = MovementAPI.FindRetreatPositionSync(
                _situation.Unit,
                _situation.Enemies,
                _situation.MinSafeDistance
            );

            if (retreatScore == null)
            {
                Main.LogDebug($"[{_logPrefix}] Retreat position not found (no reachable safe tiles)");
                return null;
            }

            var retreatPos = retreatScore.Position;

            // 후퇴 후에도 타겟 공격 가능한지 확인
            float distAfterRetreat = Vector3.Distance(retreatPos, target.Position);

            if (attack.IsMelee && distAfterRetreat > 3f)
            {
                Main.LogDebug($"[{_logPrefix}] Retreat rejected: melee attack, distance={distAfterRetreat:F1}m > 3m");
                return null;
            }

            // ★ v3.5.98: 실제 능력 사거리 사용 (타일 단위)
            float abilityRange = CombatAPI.GetAbilityRangeInTiles(attack);
            if (abilityRange <= 0) abilityRange = 15f;  // 폴백: 기본 15타일

            // ★ v3.5.98: 타일 단위로 비교
            float distAfterRetreatTiles = CombatAPI.MetersToTiles(distAfterRetreat);
            if (!attack.IsMelee && distAfterRetreatTiles > abilityRange)
            {
                Main.LogDebug($"[{_logPrefix}] Retreat rejected: {attack.Name} range={abilityRange:F0} tiles, distance after retreat={distAfterRetreatTiles:F1} tiles");
                return null;
            }

            // ★ v3.0.60: 실제 경로 비용 사용 (PathfindingService가 계산한 APCost)
            float mpCost = retreatScore.APCost;

            if (mpCost > remainingMP)
            {
                Main.LogDebug($"[{_logPrefix}] Retreat rejected: MP cost={mpCost:F1} > remaining={remainingMP:F1}");
                return null;
            }

            var seq = new ActionSequence();
            seq.Description = "Retreat then attack";
            seq.AddMove(retreatPos, mpCost, "Preemptive retreat for safety");
            seq.AddAttack(attack, target, $"Attack {target.CharacterName} from safe position");
            seq.ExpectedFinalPosition = retreatPos;

            return seq;
        }

        private ActionSequence CreateApproachThenAttackSequence(AbilityData attack, BaseUnitEntity target, float remainingMP, bool isMelee)
        {
            if (remainingMP <= 0)
                return null;

            float targetDistance = Vector3.Distance(_situation.Unit.Position, target.Position);
            float requiredDistance = isMelee ? 2f : Math.Max(5f, _situation.MinSafeDistance);

            if (targetDistance <= requiredDistance)
            {
                // 이미 공격 범위 - 이동 불필요
                return CreateDirectAttackSequence(attack, target);
            }

            // 적에게 접근할 위치 계산
            Vector3 direction = (target.Position - _situation.Unit.Position).normalized;
            float moveDistance = targetDistance - requiredDistance;
            Vector3 approachPos = _situation.Unit.Position + direction * moveDistance;

            float mpCost = moveDistance / 1.5f;
            if (mpCost > remainingMP)
            {
                // MP 부족 - 가능한 만큼만 이동
                moveDistance = remainingMP * 1.5f;
                approachPos = _situation.Unit.Position + direction * moveDistance;
                mpCost = remainingMP;
            }

            var seq = new ActionSequence();
            seq.Description = isMelee ? "Approach melee" : "Reposition ranged";
            seq.AddMove(approachPos, mpCost, isMelee ? "Close distance for melee" : "Move to optimal range");
            seq.AddAttack(attack, target, $"Attack {target.CharacterName}");
            seq.ExpectedFinalPosition = approachPos;

            return seq;
        }

        #endregion

        #region Helper Methods

        private bool ShouldConsiderRetreatFirst(AbilityData attack)
        {
            // 1. ClearMP 능력이면 무조건 고려
            if (CombatAPI.AbilityClearsMPAfterUse(attack))
                return true;

            // 2. 역할이 Support/원거리 선호이고 위험 상황이면 고려
            if (_roleSafetyWeight >= 0.6f && _situation.IsInDanger)
                return true;

            // 3. 적이 안전 거리보다 가까우면 고려
            if (_roleSafetyWeight >= 0.4f && _situation.NearestEnemyDistance < _situation.MinSafeDistance * 0.8f)
                return true;

            return false;
        }

        private bool CanUseAttackOn(AbilityData attack, BaseUnitEntity target)
        {
            if (attack == null || target == null) return false;

            try
            {
                // ★ v3.6.10: Point 타겟 AOE의 높이 체크 추가
                // SequenceOptimizer에서 공격-타겟 조합 필터링 시 높이 차이 검증
                if (CombatAPI.IsPointTargetAbility(attack))
                {
                    if (!CombatAPI.IsAoEHeightInRange(attack, _situation.Unit, target))
                    {
                        Main.LogDebug($"[SequenceOptimizer] AOE height failed: {attack.Name} -> {target.CharacterName}");
                        return false;
                    }
                }

                var targetWrapper = new Kingmaker.Utility.TargetWrapper(target);
                string reason;
                return CombatAPI.CanUseAbilityOn(attack, targetWrapper, out reason);
            }
            catch
            {
                return false;
            }
        }

        private void LogSequenceComparison(List<ActionSequence> sequences)
        {
            if (sequences == null || sequences.Count == 0) return;

            Main.Log($"[{_logPrefix}] Comparing {sequences.Count} sequences:");

            var sorted = sequences.OrderByDescending(s => s.TotalScore).ToList();
            for (int i = 0; i < sorted.Count && i < 5; i++)
            {
                var seq = sorted[i];
                string marker = i == 0 ? "★ BEST" : $"  #{i + 1}";
                Main.Log($"[{_logPrefix}] {marker}: {seq}");
            }
        }

        #endregion

        #region Static Factory Methods

        /// <summary>
        /// ★ v3.0.59: 공격 행동에 대해 최적 시퀀스 선택
        ///
        /// 기존 Plan들에서 쉽게 호출할 수 있는 정적 메서드
        ///
        /// 반환값:
        /// - null: 최적화 실패 (폴백 로직 실행 필요)
        /// - 빈 리스트: "Skip attack" 결정 (폴백 실행 금지!)
        /// - 행동 리스트: 최적 시퀀스
        /// </summary>
        public static List<PlannedAction> GetOptimalAttackActions(
            Situation situation,
            List<AbilityData> attacks,
            BaseUnitEntity target,
            ref float remainingAP,
            ref float remainingMP,
            string logPrefix = "SeqOpt")
        {
            if (situation == null || attacks == null || attacks.Count == 0 || target == null)
                return null;  // ★ 최적화 불가 → 폴백 허용

            try
            {
                var optimizer = new SequenceOptimizer(situation, logPrefix);
                var bestSeq = optimizer.OptimizeAttackSequence(attacks, target, remainingAP, remainingMP);

                // ★ v3.0.59: null = Skip attack 결정 → 빈 리스트 반환 (폴백 금지)
                if (bestSeq == null)
                    return new List<PlannedAction>();  // 빈 리스트 = 의도적 스킵

                if (bestSeq.Actions.Count == 0)
                    return new List<PlannedAction>();  // 빈 리스트 = 의도적 스킵

                // AP/MP 차감
                remainingAP -= bestSeq.TotalAPCost;
                remainingMP = bestSeq.ExpectedRemainingMP;

                Main.Log($"[{logPrefix}] Selected sequence: {bestSeq.Description} (score={bestSeq.TotalScore:F0})");

                return bestSeq.Actions;
            }
            catch (Exception ex)
            {
                Main.LogError($"[{logPrefix}] Error in sequence optimization: {ex.Message}");
                return null;  // ★ 에러 → 폴백 허용
            }
        }

        /// <summary>
        /// ClearMPAfterUse 능력에 대한 최적 시퀀스 판단
        ///
        /// 반환값:
        /// - true: 이동 먼저 필요 (후퇴 → 공격)
        /// - false: 현재 위치에서 공격 OK
        /// </summary>
        public static bool ShouldRetreatBeforeClearMPAbility(Situation situation, AbilityData attack, BaseUnitEntity target)
        {
            if (situation == null || attack == null || !CombatAPI.AbilityClearsMPAfterUse(attack))
                return false;

            if (!situation.CanMove || situation.CurrentMP <= 0)
                return false;

            var optimizer = new SequenceOptimizer(situation, "ClearMP-Check");

            // 두 옵션 비교
            var directSeq = optimizer.CreateDirectAttackSequence(attack, target);
            var retreatSeq = optimizer.CreateRetreatThenAttackSequence(attack, target, situation.CurrentMP);

            if (directSeq == null) return false;
            if (retreatSeq == null) return false;  // 후퇴 불가능하면 직접 공격

            directSeq.SimulateFinalState(situation);
            directSeq.CalculateScore(situation, optimizer._roleSafetyWeight);

            retreatSeq.SimulateFinalState(situation);
            retreatSeq.CalculateScore(situation, optimizer._roleSafetyWeight);

            Main.Log($"[ClearMP-Check] Direct: {directSeq.TotalScore:F0}, Retreat: {retreatSeq.TotalScore:F0}");

            return retreatSeq.TotalScore > directSeq.TotalScore;
        }

        #endregion
    }
}
