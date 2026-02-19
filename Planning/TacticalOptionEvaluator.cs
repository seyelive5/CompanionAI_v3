using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.View.Covers;
using UnityEngine;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning
{
    /// <summary>
    /// ★ v3.8.76: 전략 옵션 유형
    /// Phase 실행 전 4가지 공격-이동 조합을 평가하여 최적 전략 선택
    /// </summary>
    public enum TacticalStrategy
    {
        /// <summary>현재 위치에서 공격, 이동 없음</summary>
        AttackFromCurrent,

        /// <summary>먼저 이동 → 새 위치에서 공격</summary>
        MoveToAttack,

        /// <summary>현재 위치에서 공격 → 후퇴/재배치</summary>
        AttackThenRetreat,

        /// <summary>이동만 (공격 불가)</summary>
        MoveOnly
    }

    /// <summary>
    /// ★ v3.8.76: 단일 전략 옵션 평가 결과 (struct - GC 없음)
    /// </summary>
    public struct TacticalOption
    {
        public TacticalStrategy Strategy;
        public float Score;
        public bool IsViable;
        public int HittableEnemyCount;
        public CustomGridNodeBase DestinationNode;
        public Vector3 DestinationPosition;
        public string Reason;

        public override string ToString()
        {
            return $"{Strategy}(score={Score:F0}, hittable={HittableEnemyCount}, viable={IsViable}, {Reason})";
        }
    }

    /// <summary>
    /// ★ v3.8.76: 전략 평가 결과 - 선택된 전략 + 모든 옵션 정보
    /// </summary>
    public class TacticalEvaluation
    {
        public TacticalStrategy ChosenStrategy;
        public TacticalOption BestOption;
        public TacticalOption[] AllOptions;  // 고정 크기 4

        /// <summary>MoveToAttack이면 공격 전에 이동해야 함</summary>
        public bool ShouldMoveFirst => ChosenStrategy == TacticalStrategy.MoveToAttack;

        /// <summary>이동 목적지 (MoveToAttack/MoveOnly)</summary>
        public Vector3? MoveDestination =>
            BestOption.DestinationNode != null ? (Vector3?)BestOption.DestinationPosition : null;

        /// <summary>예상 공격 가능 적 수</summary>
        public int ExpectedHittableCount => BestOption.HittableEnemyCount;

        /// <summary>평가가 실행되었는가?</summary>
        public bool WasEvaluated;

        public override string ToString()
        {
            if (!WasEvaluated) return "[TacticalEval] Not evaluated";
            return $"Chosen={ChosenStrategy}, Score={BestOption.Score:F0}, " +
                   $"Hittable={ExpectedHittableCount}, MoveFirst={ShouldMoveFirst}";
        }
    }

    /// <summary>
    /// ★ v3.8.76: 전략 옵션 평가기
    ///
    /// 핵심 문제 해결:
    /// - 기존: Phase 순차 실행 → 각 Phase가 현재 위치에서만 독립 판단 → 공격-이동 불일치
    /// - 신규: Phase 실행 전에 4가지 전략을 미리 평가하고 최적 선택
    ///
    /// 4가지 옵션:
    /// A. AttackFromCurrent - 현재 위치에서 공격 (이동 불필요)
    /// B. MoveToAttack - 이동 후 공격 (이동하면 더 많은 적 공격 가능)
    /// C. AttackThenRetreat - 공격 후 후퇴 (Run&Gun 등)
    /// D. MoveOnly - 이동만 (공격 불가, 다음 턴 대비)
    ///
    /// 성능: 턴당 유닛당 ~5-13ms (캐시된 reachable tiles 활용)
    /// </summary>
    public static class TacticalOptionEvaluator
    {
        #region Score Weights

        // 공격 가능 적 1명당 가중치 (가장 중요)
        private const float W_HITTABLE = 40f;
        // 현재 대비 개선분 보너스
        private const float W_HITTABLE_IMPROVEMENT = 25f;
        // 안전도 가중치 (InfluenceMap 기반)
        private const float W_SAFETY = 15f;
        // 이동 비용 페널티
        private const float W_MOVE_COST = 5f;
        // 공격 가능 기본 보너스
        private const float W_ATTACK_BASE = 30f;
        // ★ v3.9.26: 0.3f → 0.5f — 위치 품질(커버, 거리, 위협)이 MoveToAttack 전략에 더 크게 반영
        private const float W_POSITION_QUALITY = 0.5f;

        #endregion

        /// <summary>
        /// ★ 전략 평가 실행 전 사전 체크
        /// 평가 불필요: 적 없음, 공격 없음, HP 위험 (Emergency Heal이 모든 것 오버라이드)
        /// </summary>
        public static bool ShouldEvaluate(Situation situation)
        {
            if (!situation.HasLivingEnemies) return false;
            if (situation.AvailableAttacks == null || situation.AvailableAttacks.Count == 0) return false;
            if (situation.IsHPCritical) return false;
            return true;
        }

        /// <summary>
        /// ★ 메인 진입점: 4가지 전략 평가 → 최적 선택
        /// </summary>
        public static TacticalEvaluation Evaluate(
            Situation situation,
            bool needsRetreat,
            string roleName)
        {
            var result = new TacticalEvaluation
            {
                AllOptions = new TacticalOption[4],
                WasEvaluated = true
            };

            // ★ v3.9.26: NormalHittableCount 사용 — DangerousAoE 부풀림 방지
            // DangerousAoE(Cone/Ray)가 hittable에 포함되면서 currentHittable이 과대평가되어
            // AttackFromCurrent 점수가 높아지고 MoveToAttack(더 나은 위치)이 선택되지 않음
            int currentHittable = situation.NormalHittableCount;

            // 4가지 옵션 평가
            result.AllOptions[0] = EvaluateAttackFromCurrent(situation, currentHittable, needsRetreat);
            result.AllOptions[1] = EvaluateMoveToAttack(situation, currentHittable);
            result.AllOptions[2] = EvaluateAttackThenRetreat(situation, currentHittable, needsRetreat);
            result.AllOptions[3] = EvaluateMoveOnly(situation);

            // 최고 점수 viable 옵션 선택
            float bestScore = float.MinValue;
            int bestIdx = 3; // 기본 MoveOnly
            for (int i = 0; i < 4; i++)
            {
                if (result.AllOptions[i].IsViable && result.AllOptions[i].Score > bestScore)
                {
                    bestScore = result.AllOptions[i].Score;
                    bestIdx = i;
                }
            }

            result.BestOption = result.AllOptions[bestIdx];
            result.ChosenStrategy = result.BestOption.Strategy;

            // 로깅
            Main.Log($"[{roleName}] ★ TacticalEval: {result}");
            for (int i = 0; i < 4; i++)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[{roleName}]   Option {i}: {result.AllOptions[i]}");
            }

            return result;
        }

        #region Option A: AttackFromCurrent

        /// <summary>
        /// 현재 위치에서 공격, 이동 없음
        /// Viable: HittableEnemies > 0
        /// </summary>
        private static TacticalOption EvaluateAttackFromCurrent(
            Situation situation, int currentHittable, bool needsRetreat)
        {
            var option = new TacticalOption
            {
                Strategy = TacticalStrategy.AttackFromCurrent,
                HittableEnemyCount = currentHittable
            };

            if (currentHittable == 0)
            {
                option.IsViable = false;
                option.Score = -1000f;
                option.Reason = "No hittable from current";
                return option;
            }

            option.IsViable = true;

            // 스코어 = 공격 가능 적 × 가중치 + 기본 공격 보너스
            float score = currentHittable * W_HITTABLE + W_ATTACK_BASE;

            // ★ v3.9.40: 명중 품질 가중치 (power 1.5 커브)
            // 낮은 명중률에 더 강한 감점 → 이동 후 공격(MoveToAttack) 유도
            // avgHitChance 100% → factor 1.0, 80% → 0.72, 60% → 0.46
            // avgHitChance 52% → 0.37, 40% → 0.25, 20% → 0.09, 0% → 0.00
            float hitQualityFactor = 1.0f;
            if (situation.PrimaryAttack != null && situation.HittableEnemies != null
                && situation.HittableEnemies.Count > 0)
            {
                float totalHitChance = 0f;
                int hitChecked = 0;
                foreach (var enemy in situation.HittableEnemies)
                {
                    var hitInfo = CombatCache.GetHitChance(
                        situation.PrimaryAttack, situation.Unit, enemy);
                    if (hitInfo != null)
                    {
                        totalHitChance += hitInfo.HitChance;
                        hitChecked++;
                    }
                }
                if (hitChecked > 0)
                {
                    float avgHitChance = totalHitChance / hitChecked;
                    float normalized = avgHitChance / 100f;
                    hitQualityFactor = normalized * (float)Math.Sqrt(normalized);
                    score *= hitQualityFactor;
                }
            }

            // 안전도 반영
            if (situation.InfluenceMap != null && situation.InfluenceMap.IsValid)
            {
                float threat = situation.InfluenceMap.GetThreatAt(situation.Unit.Position);
                float control = situation.InfluenceMap.GetControlAt(situation.Unit.Position);
                score += (control - threat) * W_SAFETY;
            }

            // 원거리인데 후퇴 필요하면 페널티 (여기서 공격하면 위험한 위치에 머무름)
            if (needsRetreat && situation.PrefersRanged)
            {
                score -= 20f;
            }

            // ★ v3.9.26: 현재 위치의 엄폐 품질 평가
            // 엄폐 없이 노출되면 이동 동기 생성 (MoveToAttack이 더 매력적)
            float coverQuality = EvaluateCoverQualityAtPosition(situation);
            score += coverQuality;

            option.Score = score;
            option.Reason = $"hittable={currentHittable}, hitQ={hitQualityFactor:F2}, cover={coverQuality:F0}";
            return option;
        }

        #endregion

        #region Option B: MoveToAttack

        /// <summary>
        /// 이동 후 공격 - FindRangedAttackPositionSync / FindMeleeAttackPositionSync 사용
        /// ★ v3.8.98: 근접 유닛은 FindMeleeAttackPositionSync로 적 인접 위치 탐색
        /// Viable: 이동 가능 + 목적지에서 공격 가능한 적 > 0
        /// </summary>
        private static TacticalOption EvaluateMoveToAttack(
            Situation situation, int currentHittable)
        {
            var option = new TacticalOption
            {
                Strategy = TacticalStrategy.MoveToAttack
            };

            // 이동 불가 → non-viable
            if (!situation.CanMove && situation.CurrentMP <= 0)
            {
                option.IsViable = false;
                option.Score = -1000f;
                option.Reason = "Cannot move";
                return option;
            }

            if (!situation.HasLivingEnemies)
            {
                option.IsViable = false;
                option.Score = -1000f;
                option.Reason = "No enemies";
                return option;
            }

            var unit = situation.Unit;
            AIRole role = situation.CharacterSettings?.Role ?? AIRole.Auto;
            MovementAPI.PositionScore bestPosition = null;

            // ★ v3.8.98: 근접 유닛은 FindMeleeAttackPositionSync 사용
            if (!situation.PrefersRanged && situation.NearestEnemy != null)
            {
                float meleeRange = GetMeleeRange(unit);

                bestPosition = MovementAPI.FindMeleeAttackPositionSync(
                    unit,
                    situation.NearestEnemy,
                    meleeRange,
                    0f,  // predictedMP=0 (현재 MP 기반)
                    situation.InfluenceMap,
                    role,
                    situation.PredictiveThreatMap,
                    null,  // meleeAoEAbility
                    situation.Enemies
                );

                // FindMeleeAttackPositionSync는 HittableEnemyCount를 설정하지 않음
                // 위치가 적의 근접 사거리 내이므로 최소 1명 공격 가능
                if (bestPosition != null && bestPosition.HittableEnemyCount == 0)
                {
                    bestPosition.HittableEnemyCount = 1;
                }

                if (Main.IsDebugEnabled)
                    Main.LogDebug($"[TacticalEval] Melee MoveToAttack: " +
                        $"target={situation.NearestEnemy.CharacterName}, meleeRange={meleeRange:F1}, " +
                        $"result={(bestPosition != null ? $"pos=({bestPosition.Position.x:F1},{bestPosition.Position.z:F1})" : "null")}");
            }
            else
            {
                // ★ v3.9.56: BlendedAttackRange 사용 (모든 유한 사거리 스킬 고려)
                // 무제한 사거리 스킬은 제외되어 포지셔닝에 영향 안 줌
                float weaponRange = situation.BlendedAttackRange > 0
                    ? situation.BlendedAttackRange
                    : situation.WeaponRange.EffectiveRange;
                if (weaponRange <= 0f) weaponRange = 15f;  // 안전 폴백

                // ★ v3.9.74: 무기 로테이션 시 짧은 사거리 무기 기준 포지셔닝
                // ★ v3.9.78: 동일 타입(원거리+원거리)에만 적용 — 혼합(원거리+근접) 시 현재 무기 사거리 유지
                // ★ v3.9.88: HasWeaponSwitchBonus 조건 추가 — 보너스 공격 없으면 양쪽 무기 고려 불필요
                //   PrimaryHandAbilityGroup 공유 쿨다운 때문에 전환만으로는 추가 공격 불가
                // ★ v3.9.92: 공격 전에만 사거리 조정 (공격 후엔 전환할 이유 없음)
                if (situation.WeaponRotationAvailable && situation.HasWeaponSwitchBonus && situation.WeaponSetData != null
                    && !situation.HasAttackedThisTurn)
                {
                    int currentIdx = situation.CurrentWeaponSetIndex;
                    int altIdx = currentIdx == 0 ? 1 : 0;
                    if (altIdx < situation.WeaponSetData.Length && currentIdx < situation.WeaponSetData.Length)
                    {
                        var currentSet = situation.WeaponSetData[currentIdx];
                        var altSet = situation.WeaponSetData[altIdx];
                        float altRange = altSet.PrimaryWeaponRange;

                        bool bothRanged = currentSet.HasRangedWeapon && altSet.HasRangedWeapon;
                        bool bothMelee = currentSet.HasMeleeWeapon && altSet.HasMeleeWeapon;
                        if ((bothRanged || bothMelee) && altRange > 0 && altRange < weaponRange)
                        {
                            if (Main.IsDebugEnabled) Main.LogDebug($"[TacticalEval] MoveToAttack range: {weaponRange:F1} → {altRange:F0} (same-type rotation: shorter weapon)");
                            weaponRange = altRange;
                        }
                    }
                }

                bestPosition = MovementAPI.FindRangedAttackPositionSync(
                    unit,
                    situation.Enemies,
                    weaponRange,
                    situation.MinSafeDistance,
                    0f,  // predictedMP=0 (현재 MP 기반)
                    situation.InfluenceMap,
                    role,
                    situation.PredictiveThreatMap
                );
            }

            if (bestPosition == null || bestPosition.HittableEnemyCount == 0)
            {
                option.IsViable = false;
                option.Score = -1000f;
                option.Reason = "No hittable position found";
                return option;
            }

            option.DestinationNode = bestPosition.Node;
            option.DestinationPosition = bestPosition.Position;
            option.HittableEnemyCount = bestPosition.HittableEnemyCount;
            option.IsViable = true;

            // 스코어 계산
            // ★ v3.9.50: MoveToAttack 점수 개선
            // 이전: 같은 hittable 수 → -10 페널티, 위치 품질 ×0.5 → 이동이 항상 불리
            // 수정: 같은 수 → 0 (중립), 위치 품질 ×0.8 → 더 나은 위치로의 이동 장려
            float hittableScore = bestPosition.HittableEnemyCount * W_HITTABLE;
            float improvementBonus = (bestPosition.HittableEnemyCount - currentHittable) * W_HITTABLE_IMPROVEMENT;
            float positionQuality = bestPosition.TotalScore * 0.8f;
            float moveCost = -W_MOVE_COST;

            // hittable 감소 시에만 페널티 (같으면 중립 - 위치 개선 이동 허용)
            if (bestPosition.HittableEnemyCount < currentHittable)
            {
                improvementBonus = -10f;
            }
            else if (bestPosition.HittableEnemyCount == currentHittable)
            {
                improvementBonus = 0f;  // 중립 (위치 품질로 판단)
            }

            option.Score = hittableScore + improvementBonus + positionQuality + moveCost;
            option.Reason = $"dest={bestPosition.HittableEnemyCount}, current={currentHittable}, posScore={bestPosition.TotalScore:F0}";
            return option;
        }

        #endregion

        #region Option C: AttackThenRetreat

        /// <summary>
        /// 현재 위치에서 공격 → 후퇴
        /// Viable: 현재 위치에서 공격 가능 + 이동 가능 + 후퇴 필요/유리
        /// </summary>
        private static TacticalOption EvaluateAttackThenRetreat(
            Situation situation, int currentHittable, bool needsRetreat)
        {
            var option = new TacticalOption
            {
                Strategy = TacticalStrategy.AttackThenRetreat,
                HittableEnemyCount = currentHittable
            };

            // 현재 위치에서 공격 불가 → non-viable
            if (currentHittable == 0)
            {
                option.IsViable = false;
                option.Score = -1000f;
                option.Reason = "No hittable from current";
                return option;
            }

            // 후퇴 필요성 확인
            bool wantsPostAttackMove = needsRetreat ||
                (situation.PrefersRanged && situation.IsInDanger);

            if (!wantsPostAttackMove)
            {
                // 후퇴 필요 없으면 이 전략은 의미 없음
                option.IsViable = false;
                option.Score = -1000f;
                option.Reason = "No retreat need";
                return option;
            }

            // ★ PostAction MP 회복 능력 체크 (Run&Gun 등)
            // 공격 후 MP가 회복되므로 현재 MP=0이어도 후퇴 가능
            bool hasPostActionMPRecovery = false;
            float mpRecoveryBonus = 0f;
            if (situation.AvailableBuffs != null)
            {
                for (int i = 0; i < situation.AvailableBuffs.Count; i++)
                {
                    var buff = situation.AvailableBuffs[i];
                    if (AbilityDatabase.GetTiming(buff) == AbilityTiming.PostFirstAction &&
                        AbilityDatabase.GetExpectedMPRecovery(buff) > 0)
                    {
                        hasPostActionMPRecovery = true;
                        mpRecoveryBonus = 30f;
                        break;
                    }
                }
            }

            // 이동 불가 → non-viable (후퇴할 수 없음)
            // ★ v3.8.76 fix: Run&Gun 등 PostAction MP 회복이 있으면 현재 MP=0이어도 viable
            // 기존 DPSPlan의 deferRetreat 로직 복원: 공격 → PostAction MP 회복 → 후퇴
            if (!situation.CanMove && situation.CurrentMP <= 0 && !hasPostActionMPRecovery)
            {
                option.IsViable = false;
                option.Score = -1000f;
                option.Reason = "Cannot move after attack (no MP recovery)";
                return option;
            }

            option.IsViable = true;

            // 스코어 = 공격 가치 + 후퇴 안전 이득
            float attackScore = currentHittable * W_HITTABLE + W_ATTACK_BASE;

            // 후퇴 시 안전도 이득 추정
            float retreatSafetyGain = 0f;
            if (situation.InfluenceMap != null && situation.InfluenceMap.IsValid)
            {
                float currentThreat = situation.InfluenceMap.GetThreatAt(situation.Unit.Position);
                // 후퇴하면 위협이 줄어들 것으로 추정 (정확한 계산은 비용 높으므로 추정)
                retreatSafetyGain = currentThreat * W_SAFETY * 0.5f;
            }

            option.Score = attackScore + retreatSafetyGain + mpRecoveryBonus;
            option.Reason = $"hittable={currentHittable}, safetyGain={retreatSafetyGain:F0}, mpRecov={hasPostActionMPRecovery}";
            return option;
        }

        #endregion

        #region Option D: MoveOnly

        /// <summary>
        /// 이동만 (공격 불가) - 최저 우선순위
        /// Viable: 이동 가능 + 적 존재
        /// </summary>
        private static TacticalOption EvaluateMoveOnly(Situation situation)
        {
            var option = new TacticalOption
            {
                Strategy = TacticalStrategy.MoveOnly,
                HittableEnemyCount = 0
            };

            if ((!situation.CanMove && situation.CurrentMP <= 0) || !situation.HasLivingEnemies)
            {
                option.IsViable = false;
                option.Score = -2000f;
                option.Reason = "Cannot move or no enemies";
                return option;
            }

            option.IsViable = true;

            // 항상 낮은 점수 - 다른 옵션이 모두 non-viable일 때만 선택
            float distanceFactor = Mathf.Clamp01(situation.NearestEnemyDistance / 30f) * 10f;
            option.Score = -50f + distanceFactor;
            option.Reason = "Positioning only";
            return option;
        }

        #endregion

        #region Helpers

        // ★ v3.9.24: GetWeaponRange() 삭제 — CombatAPI.GetWeaponRangeProfile()로 중앙집중화

        /// <summary>
        /// ★ v3.8.98: 근접 무기 사거리 조회 (타일 단위)
        /// 기본 근접 사거리 = 2 타일 (대부분의 근접 무기)
        /// </summary>
        private static float GetMeleeRange(BaseUnitEntity unit)
        {
            try
            {
                var primaryHand = unit.Body?.PrimaryHand;
                if (primaryHand?.HasWeapon == true && primaryHand.Weapon.Blueprint.IsMelee)
                {
                    int attackRange = primaryHand.Weapon.AttackRange;
                    if (attackRange > 0 && attackRange < 100)
                        return attackRange;
                }
            }
            catch { }
            return 2f;  // 기본 근접 사거리
        }

        /// <summary>
        /// ★ v3.9.26: 현재 위치의 엄폐 품질 평가
        /// 적으로부터의 LOS/Cover를 평가하여 -15 (완전 노출) ~ +25 (우수한 엄폐)
        /// Max-weighted: 가장 좋은 단일 엄폐를 중시 (평균이면 희석됨)
        /// </summary>
        private static float EvaluateCoverQualityAtPosition(Situation situation)
        {
            var unit = situation.Unit;
            var enemies = situation.Enemies;
            if (enemies == null || enemies.Count == 0) return 0f;

            var unitNode = unit.Position.GetNearestNodeXZ() as CustomGridNodeBase;
            if (unitNode == null) return 0f;

            float maxCoverScore = 0f;
            float totalCoverScore = 0f;
            int validCount = 0;

            for (int i = 0; i < enemies.Count; i++)
            {
                var enemy = enemies[i];
                if (enemy == null || enemy.LifeState.IsDead) continue;

                var enemyNode = enemy.Position.GetNearestNodeXZ() as CustomGridNodeBase;
                if (enemyNode == null) continue;

                try
                {
                    var los = LosCalculations.GetWarhammerLos(enemyNode, enemy.SizeRect, unitNode, unit.SizeRect);
                    float coverVal;
                    switch (los.CoverType)
                    {
                        case LosCalculations.CoverType.Invisible:
                            coverVal = 40f;
                            break;
                        case LosCalculations.CoverType.Full:
                            coverVal = 30f;
                            break;
                        case LosCalculations.CoverType.Half:
                            coverVal = 15f;
                            break;
                        default: // None
                            coverVal = 0f;
                            break;
                    }

                    if (coverVal > maxCoverScore) maxCoverScore = coverVal;
                    totalCoverScore += coverVal;
                    validCount++;
                }
                catch { }
            }

            if (validCount == 0) return 0f;

            // Max-weighted: 최대 커버 60% + 평균 40%
            float avgCover = totalCoverScore / validCount;
            float weightedCover = maxCoverScore * 0.6f + avgCover * 0.4f;

            // 0~40 범위를 -15~+25 범위로 매핑
            // 0 (노출) → -15, 15 (Half) → +0, 30 (Full) → +15, 40 (Invisible) → +25
            return weightedCover - 15f;
        }

        #endregion
    }
}
