using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using UnityEngine;
using CompanionAI_v3.Core;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Data;

namespace CompanionAI_v3.Analysis
{
    /// <summary>
    /// ★ v3.0.57: 행동 시퀀스 클래스
    ///
    /// 여러 행동의 조합을 나타내며, 전체 시퀀스의 유용성 점수를 계산.
    /// "이동 → 공격" vs "현재 위치에서 공격" 같은 대안들을 비교 가능.
    ///
    /// 핵심 개념:
    /// - 각 시퀀스는 일련의 행동(이동, 공격, 버프 등)으로 구성
    /// - 시퀀스 완료 후 예상 상태(HP, MP, 위치 등)를 시뮬레이션
    /// - 최종 점수 = 공격 가치 + 안전 가치 + 효율 가치
    /// </summary>
    public class ActionSequence
    {
        #region Properties

        /// <summary>시퀀스 ID (디버깅용)</summary>
        public string Id { get; }

        /// <summary>포함된 행동들</summary>
        public List<PlannedAction> Actions { get; } = new List<PlannedAction>();

        /// <summary>총 AP 비용</summary>
        public float TotalAPCost => Actions.Sum(a => a.APCost);

        /// <summary>총 MP 비용 (이동 포함)</summary>
        public float TotalMPCost { get; set; }

        /// <summary>시퀀스 완료 후 예상 MP</summary>
        public float ExpectedRemainingMP { get; set; }

        /// <summary>시퀀스 완료 후 예상 위치</summary>
        public Vector3? ExpectedFinalPosition { get; set; }

        /// <summary>예상 총 데미지</summary>
        public float ExpectedDamage { get; set; }

        /// <summary>예상 적 처치 수</summary>
        public int ExpectedKills { get; set; }

        /// <summary>시퀀스 완료 후 예상 안전도 (0~100)</summary>
        public float ExpectedSafety { get; set; }

        /// <summary>시퀀스에 ClearMPAfterUse 능력이 포함되어 있는가</summary>
        public bool ContainsClearMPAbility { get; set; }

        /// <summary>시퀀스에 이동이 포함되어 있는가</summary>
        public bool ContainsMove => Actions.Any(a => a.Type == ActionType.Move);

        /// <summary>시퀀스 설명</summary>
        public string Description { get; set; }

        #endregion

        #region Scoring

        /// <summary>
        /// 최종 점수 (모든 요소 합산)
        /// </summary>
        public float TotalScore { get; private set; }

        /// <summary>공격 가치 점수</summary>
        public float OffenseScore { get; private set; }

        /// <summary>안전 가치 점수</summary>
        public float SafetyScore { get; private set; }

        /// <summary>AP 효율 점수</summary>
        public float EfficiencyScore { get; private set; }

        /// <summary>역할 적합성 점수</summary>
        public float RoleFitScore { get; private set; }

        #endregion

        #region Constructor

        public ActionSequence(string id = null)
        {
            Id = id ?? Guid.NewGuid().ToString().Substring(0, 8);
        }

        #endregion

        #region Methods

        /// <summary>
        /// 행동 추가
        /// </summary>
        public ActionSequence Add(PlannedAction action)
        {
            if (action != null)
            {
                Actions.Add(action);

                // ClearMP 능력 체크
                if (action.Ability != null && CombatAPI.AbilityClearsMPAfterUse(action.Ability))
                {
                    ContainsClearMPAbility = true;
                }
            }
            return this;
        }

        /// <summary>
        /// 이동 행동 추가
        /// </summary>
        public ActionSequence AddMove(Vector3 destination, float mpCost, string reason = "Move")
        {
            Actions.Add(PlannedAction.Move(destination, reason));
            TotalMPCost += mpCost;
            ExpectedFinalPosition = destination;
            return this;
        }

        /// <summary>
        /// 공격 행동 추가
        /// </summary>
        public ActionSequence AddAttack(AbilityData attack, BaseUnitEntity target, string reason = null)
        {
            float cost = CombatAPI.GetAbilityAPCost(attack);
            Actions.Add(PlannedAction.Attack(attack, target, reason ?? $"Attack {target?.CharacterName}", cost));

            // ClearMP 체크
            if (CombatAPI.AbilityClearsMPAfterUse(attack))
            {
                ContainsClearMPAbility = true;
            }

            // 예상 데미지 추가
            if (target != null)
            {
                ExpectedDamage += CombatAPI.EstimateDamage(attack, target);
                float targetHP = CombatAPI.GetActualHP(target);
                if (ExpectedDamage >= targetHP)
                {
                    ExpectedKills++;
                }
            }

            return this;
        }

        /// <summary>
        /// 시퀀스 점수 계산
        /// </summary>
        public void CalculateScore(Situation situation, float roleSafetyWeight)
        {
            // 1. 공격 가치 점수
            OffenseScore = CalculateOffenseScore(situation);

            // 2. 안전 가치 점수 (역할 가중치 적용)
            SafetyScore = CalculateSafetyScore(situation, roleSafetyWeight);

            // 3. AP 효율 점수
            EfficiencyScore = CalculateEfficiencyScore(situation);

            // 4. 역할 적합성 점수
            RoleFitScore = CalculateRoleFitScore(situation);

            // 총점
            TotalScore = OffenseScore + SafetyScore + EfficiencyScore + RoleFitScore;
        }

        private float CalculateOffenseScore(Situation situation)
        {
            float score = 0f;

            // 예상 데미지 기반
            score += ExpectedDamage * 0.5f;

            // 킬 보너스 (높은 가중치)
            score += ExpectedKills * 80f;

            // 공격 행동 수
            int attackCount = Actions.Count(a => a.Type == ActionType.Attack);
            score += attackCount * 20f;

            return score;
        }

        private float CalculateSafetyScore(Situation situation, float roleSafetyWeight)
        {
            float score = 0f;

            // 시퀀스 완료 후 안전도
            score += ExpectedSafety * roleSafetyWeight;

            // ClearMP 능력 + 위험 위치 = 큰 감점
            if (ContainsClearMPAbility)
            {
                // 시퀀스 완료 후 MP가 0이 될 것
                if (ExpectedSafety < 50f)
                {
                    score -= 60f * roleSafetyWeight;  // Support는 -48점
                }
                else if (ExpectedSafety < 70f)
                {
                    score -= 30f * roleSafetyWeight;
                }
            }

            // 이동으로 안전도 개선 보너스
            if (ContainsMove && ExpectedSafety > 70f)
            {
                score += 20f * roleSafetyWeight;
            }

            return score;
        }

        private float CalculateEfficiencyScore(Situation situation)
        {
            float score = 0f;

            // AP 효율 (데미지/AP)
            if (TotalAPCost > 0)
            {
                float damagePerAP = ExpectedDamage / TotalAPCost;
                score += Math.Min(damagePerAP * 2f, 30f);
            }

            // 불필요한 이동 감점
            if (ContainsMove && situation.HasHittableEnemies && !ContainsClearMPAbility)
            {
                // 현재 위치에서 공격 가능한데 이동하면 약간 감점
                score -= 5f;
            }

            return score;
        }

        private float CalculateRoleFitScore(Situation situation)
        {
            float score = 0f;
            var role = situation.CharacterSettings?.Role ?? Settings.AIRole.Auto;

            // ★ v3.0.59: "공격 안 함" 시퀀스 체크
            bool isSkipSequence = Actions.Count == 0;

            switch (role)
            {
                case Settings.AIRole.Support:
                    // ★ v3.0.59: Support가 위험한 공격 스킵 시 보너스
                    if (isSkipSequence && situation.IsInDanger)
                        score += 25f;  // 위험할 때 스킵 = 현명한 선택
                    else if (isSkipSequence && ExpectedRemainingMP > 0)
                        score += 10f;  // MP 유지 = 기동성 보존

                    // Support: 안전한 원거리 공격 보너스
                    if (ContainsMove && ExpectedSafety >= 70f)
                        score += 15f;
                    // 근접 상태에서 공격만 하면 감점
                    if (!ContainsMove && situation.IsInDanger && !isSkipSequence)
                        score -= 20f;
                    break;

                case Settings.AIRole.DPS:
                    // DPS: 최대 데미지 보너스
                    if (ExpectedKills > 0)
                        score += ExpectedKills * 10f;
                    break;

                case Settings.AIRole.Tank:
                    // Tank: 전선 유지 보너스, 후퇴 감점
                    if (ContainsMove && ExpectedFinalPosition.HasValue)
                    {
                        // 적에게서 멀어지는 이동이면 감점
                        float currentDist = situation.NearestEnemyDistance;
                        float newDist = Vector3.Distance(ExpectedFinalPosition.Value,
                            situation.NearestEnemy?.Position ?? Vector3.zero);
                        if (newDist > currentDist)
                            score -= 15f;
                    }
                    break;

                case Settings.AIRole.Auto:
                    // ★ v3.0.92: Auto는 RangePreference 기반 판단
                    if (situation.PrefersRanged)
                    {
                        // 원거리 선호: 위험한 공격 스킵 보너스
                        if (isSkipSequence && situation.IsInDanger)
                            score += 20f;
                        else if (isSkipSequence && ExpectedRemainingMP > 0)
                            score += 5f;

                        if (ContainsMove && ExpectedSafety >= 70f)
                            score += 10f;
                    }
                    break;
            }

            return score;
        }

        /// <summary>
        /// 최종 위치에서의 예상 안전도 계산
        /// </summary>
        public void SimulateFinalState(Situation situation)
        {
            // 예상 최종 위치
            Vector3 finalPos = ExpectedFinalPosition ?? situation.Unit.Position;

            // 시퀀스 완료 후 예상 MP
            if (ContainsClearMPAbility)
            {
                ExpectedRemainingMP = 0f;
            }
            else
            {
                ExpectedRemainingMP = situation.CurrentMP - TotalMPCost;
                if (ExpectedRemainingMP < 0) ExpectedRemainingMP = 0f;
            }

            // 예상 안전도 계산
            ExpectedSafety = EstimateSafetyAt(finalPos, situation);
        }

        private float EstimateSafetyAt(Vector3 position, Situation situation)
        {
            float safety = 50f;

            // 적과의 최소 거리
            float nearestDist = float.MaxValue;
            foreach (var enemy in situation.Enemies)
            {
                if (enemy == null || enemy.LifeState.IsDead) continue;
                float dist = Vector3.Distance(position, enemy.Position);
                if (dist < nearestDist) nearestDist = dist;
            }

            // 거리 기반 안전도
            if (nearestDist >= situation.MinSafeDistance * 1.5f)
                safety += 30f;
            else if (nearestDist >= situation.MinSafeDistance)
                safety += 10f;
            else if (nearestDist < situation.MinSafeDistance * 0.5f)
                safety -= 30f;
            else
                safety -= 10f;

            // MP 잔여량 기반 (이동 가능 여부)
            if (ExpectedRemainingMP > 0)
                safety += 10f;
            else
                safety -= 15f;

            return Math.Max(0f, Math.Min(100f, safety));
        }

        #endregion

        #region ToString

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append($"[Seq:{Id}] Score={TotalScore:F0}");
            sb.Append($" (Off={OffenseScore:F0}, Safe={SafetyScore:F0}, Eff={EfficiencyScore:F0}, Role={RoleFitScore:F0})");
            sb.Append($" | Actions: ");

            foreach (var action in Actions)
            {
                switch (action.Type)
                {
                    case ActionType.Move:
                        sb.Append("Move→");
                        break;
                    case ActionType.Attack:
                        sb.Append($"Atk({action.Ability?.Name})→");
                        break;
                    default:
                        sb.Append($"{action.Type}→");
                        break;
                }
            }

            sb.Append($" | Safety={ExpectedSafety:F0}, MP={ExpectedRemainingMP:F1}");

            if (!string.IsNullOrEmpty(Description))
            {
                sb.Append($" [{Description}]");
            }

            return sb.ToString();
        }

        #endregion
    }
}
