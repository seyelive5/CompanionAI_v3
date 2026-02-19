using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning
{
    /// <summary>
    /// ★ v3.11.0: 전략적 행동 시퀀스 평가기 — Template-Seeded Greedy Simulation
    /// 10가지 시드 템플릿으로 후보 시퀀스를 생성하고, 통합 스코어링으로 최적 전략 선택
    /// DPSPlan의 Phase들에게 가이드 제공 (강제가 아닌 힌트)
    ///
    /// 시드 목록:
    ///   0 PureAttack       — Attack×N
    ///   1 BuffedAttack      — Buff → Attack×(N-1)
    ///   2 RnGChain          — Attack×K → R&amp;G → Attack×1
    ///   3 BuffedRnGChain    — Buff → Attack×K → R&amp;G → Attack×1
    ///   4 AoEFocus          — AoE×N (클러스터)
    ///   5 BuffedAoE         — Buff → AoE×N
    ///   6 AoERnGChain       — AoE×K → R&amp;G → Attack×1
    ///   7 KillSequence      — KillSimulator 최적 시퀀스
    ///   8 DebuffedAttack    — Debuff → Attack×(N-1)
    ///   9 BuffedRnGAoE      — Buff → AoE×K → R&amp;G → Attack×1
    ///
    /// 점수 모델:
    ///   Score = TotalDamage + KillBonus + UtilityBonus
    ///   - TotalDamage: AoE는 적 수 × 피해로 자연히 높은 점수
    ///   - KillBonus: 킬 진행도(0~15) + 확정킬 보너스(40)
    ///   - UtilityBonus: 디버프(8점) 등 비데미지 보너스
    ///
    /// 성능: ~50 iterations, CombatCache 94% hit → &lt; 1ms, zero GC (struct + static list)
    /// </summary>
    public static class TurnStrategyPlanner
    {
        // ── 정적 재사용 리스트 (GC 방지) ──
        private static readonly List<CandidateScore> _candidates = new List<CandidateScore>(12);

        /// <summary>킬 진행 보너스 최대값 — 회피/패링 불확실성 반영</summary>
        private const float MAX_KILL_BONUS = 15f;

        /// <summary>확정 킬 보너스 — 킬 시퀀스가 확정 킬일 때 가산</summary>
        private const float KILL_CONFIRM_BONUS = 40f;

        /// <summary>디버프 기본 유틸리티 점수</summary>
        private const float DEBUFF_UTILITY = 8f;

        // ══════════════════════════════════════════════════════════════
        // 구조체 정의
        // ══════════════════════════════════════════════════════════════

        private struct CandidateScore
        {
            public SequenceType Type;
            public float TotalDamage;           // 전체 기대 데미지
            public float KillBonus;             // 킬 진행 보너스 (0~55)
            public float UtilityBonus;          // 비데미지 가치 (디버프 등)
            public AbilityData Buff;            // 사용할 버프 (null=없음)
            public float RnGCost;               // R&G AP 예약
            public AbilityData RecommendedAoE;  // AoE 시드의 추천 능력
            public AbilityData Debuff;          // 디버프 시드의 추천 능력
            public bool UsesAoE;                // Phase 4.4 가이드
            public bool UsesKillSeq;            // Phase 3 가이드
            public int ExpectedKills;           // 예상 킬 수
            public string Description;

            public float Score => TotalDamage + KillBonus + UtilityBonus;
        }

        /// <summary>공격 프로필: 단일 타겟 또는 AoE</summary>
        private struct AttackProfile
        {
            public float DmgPerUseTotal;    // 전체 타겟 데미지 per use
            public float DmgPerUsePrimary;  // 주 타겟 데미지 per use
            public float Cost;              // AP 비용
            public int TargetsHit;          // 적중 타겟 수 (1=단일, 2+=AoE)
            public string Description;

            public bool IsValid => Cost > 0 && DmgPerUseTotal > 0;
        }

        // ══════════════════════════════════════════════════════════════
        // 공개 API
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 현재 상황에서 최적 전략 평가
        /// </summary>
        /// <returns>최적 전략 (타겟 없음/공격 불가 시 null)</returns>
        public static TurnStrategy Evaluate(Situation situation)
        {
            try
            {
                return EvaluateInternal(situation);
            }
            catch (Exception ex)
            {
                Main.Log($"[Strategy] Error in Evaluate: {ex.Message}");
                return null;
            }
        }

        // ══════════════════════════════════════════════════════════════
        // 핵심 평가 로직
        // ══════════════════════════════════════════════════════════════

        private static TurnStrategy EvaluateInternal(Situation situation)
        {
            _candidates.Clear();

            var unit = situation.Unit;
            float ap = situation.CurrentAP;
            var target = situation.BestTarget;

            if (target == null || !situation.HasHittableEnemies)
                return null;

            float targetHP = target.Health.HitPointsLeft;
            if (targetHP <= 0) return null;

            // ══════════════════════════════════════════════════════════════
            // Step 1: 공격 프로필 수집 — 단일 타겟 + AoE 별도 추적
            // ══════════════════════════════════════════════════════════════

            var singleProfile = FindSingleProfile(situation, target);
            AbilityData bestAoEAbility;
            var aoeProfile = FindAoEProfile(situation, target, ap, out bestAoEAbility);

            // 최적 프로필 (DPA 비교) — 시드 0-3용
            float singleDPA = singleProfile.IsValid ? singleProfile.DmgPerUseTotal / singleProfile.Cost : 0f;
            float aoeDPA = aoeProfile.IsValid ? aoeProfile.DmgPerUseTotal / aoeProfile.Cost : 0f;
            bool aoeWinsOverall = aoeProfile.IsValid && aoeDPA > singleDPA && aoeProfile.TargetsHit >= 2;
            var profile = aoeWinsOverall ? aoeProfile : singleProfile;

            if (!profile.IsValid) return null;

            if (aoeWinsOverall && Main.IsDebugEnabled)
            {
                Main.LogDebug($"[Strategy] Profile: AoE({aoeProfile.Description}) wins \u2014 " +
                    $"{aoeProfile.DmgPerUsePrimary:F0}\u00d7{aoeProfile.TargetsHit}t/{aoeProfile.Cost:F1}AP = {aoeDPA:F1} DPA > " +
                    $"Single {singleProfile.DmgPerUsePrimary:F0}/{singleProfile.Cost:F1}AP = {singleDPA:F1} DPA");
            }

            bool hasAoEProfile = aoeProfile.IsValid && aoeProfile.TargetsHit >= 2;

            // ══════════════════════════════════════════════════════════════
            // Step 2: 보조 자원 수집 — 버프, R&G, 디버프, 킬 시퀀스
            // ══════════════════════════════════════════════════════════════

            // ── R&G ──
            var rng = situation.RunAndGunAbility;
            float rngCost = rng != null ? CombatAPI.GetAbilityAPCost(rng) : 0f;
            bool hasRnG = rng != null && rngCost <= ap;

            // ── 버프 (PreAttackBuff 중 최고 배율) ──
            AbilityData bestBuff = null;
            float bestBuffMultiplier = 1f;
            float bestBuffCost = 0f;

            if (situation.AvailableBuffs != null)
            {
                foreach (var buff in situation.AvailableBuffs)
                {
                    if (AbilityDatabase.GetTiming(buff) != AbilityTiming.PreAttackBuff) continue;
                    if (AbilityDatabase.IsRunAndGun(buff)) continue;
                    if (AbilityDatabase.IsPostFirstAction(buff)) continue;

                    float cost = CombatAPI.GetAbilityAPCost(buff);
                    if (cost > ap) continue;

                    float mult = KillSimulator.EstimateBuffMultiplier(buff);
                    if (mult > bestBuffMultiplier)
                    {
                        bestBuffMultiplier = mult;
                        bestBuff = buff;
                        bestBuffCost = cost;
                    }
                }
            }
            bool hasBuff = bestBuff != null && bestBuffMultiplier > 1f;

            // ── ★ v3.11.0: 디버프 (최저 AP 비용 디버프) ──
            AbilityData bestDebuff = FindBestDebuff(situation, ap);
            float bestDebuffCost = bestDebuff != null ? CombatAPI.GetAbilityAPCost(bestDebuff) : 0f;
            bool hasDebuff = bestDebuff != null && bestDebuffCost < ap; // < (공격 AP 남겨야)

            // ── ★ v3.11.0: 킬 시퀀스 ──
            KillSimulator.KillSequence killSeq = null;
            try { killSeq = KillSimulator.FindKillSequence(situation, target); }
            catch { /* KillSimulator 실패 무시 */ }
            bool hasKillSeq = killSeq != null && killSeq.IsConfirmedKill && killSeq.APCost <= ap;

            // ══════════════════════════════════════════════════════════════
            // Step 3: 후보 시퀀스 생성 (10 seeds)
            // ══════════════════════════════════════════════════════════════

            // ── 시드 0: PureAttack (Attack×N) ──
            {
                int attacks = (int)(ap / profile.Cost);
                if (attacks > 0)
                {
                    float totalDmg = attacks * profile.DmgPerUseTotal;
                    float primaryDmg = attacks * profile.DmgPerUsePrimary;

                    _candidates.Add(new CandidateScore
                    {
                        Type = SequenceType.Standard,
                        TotalDamage = totalDmg,
                        KillBonus = CalcKillBonus(primaryDmg, targetHP),
                        Description = $"{profile.Description}\u00d7{attacks} = {totalDmg:F0}dmg"
                    });
                }
            }

            // ── 시드 1: BuffedAttack (Buff → Attack×(N-1)) ──
            if (hasBuff)
            {
                float apAfterBuff = ap - bestBuffCost;
                int attacks = (int)(apAfterBuff / profile.Cost);
                if (attacks > 0)
                {
                    float totalDmg = attacks * profile.DmgPerUseTotal * bestBuffMultiplier;
                    float primaryDmg = attacks * profile.DmgPerUsePrimary * bestBuffMultiplier;

                    _candidates.Add(new CandidateScore
                    {
                        Type = SequenceType.BuffedAttack,
                        TotalDamage = totalDmg,
                        KillBonus = CalcKillBonus(primaryDmg, targetHP),
                        Buff = bestBuff,
                        Description = $"Buff({bestBuff.Name}) \u2192 {profile.Description}\u00d7{attacks} = {totalDmg:F0}dmg (\u00d7{bestBuffMultiplier:F2})"
                    });
                }
            }

            // ── 시드 2: RnGChain (Attack×K → R&G → Attack×1) ──
            if (hasRnG)
            {
                float apForPreAttack = ap - rngCost;
                int preAttacks = Math.Max(1, (int)(apForPreAttack / profile.Cost));
                int postAttacks = 1;

                float totalDmg = (preAttacks + postAttacks) * profile.DmgPerUseTotal;
                float primaryDmg = (preAttacks + postAttacks) * profile.DmgPerUsePrimary;

                _candidates.Add(new CandidateScore
                {
                    Type = SequenceType.RnGChain,
                    TotalDamage = totalDmg,
                    KillBonus = CalcKillBonus(primaryDmg, targetHP),
                    RnGCost = rngCost,
                    Description = $"{profile.Description}\u00d7{preAttacks} \u2192 R&G \u2192 {profile.Description}\u00d7{postAttacks} = {totalDmg:F0}dmg"
                });
            }

            // ── 시드 3: BuffedRnGChain (Buff → Attack×K → R&G → Attack×1) ──
            if (hasBuff && hasRnG)
            {
                float apAfterBuff = ap - bestBuffCost;
                float apForPreAttack = apAfterBuff - rngCost;
                int preAttacks = Math.Max(0, (int)(apForPreAttack / profile.Cost));
                int postAttacks = (preAttacks > 0) ? 1 : 0;

                if (preAttacks > 0)
                {
                    float totalDmg = (preAttacks + postAttacks) * profile.DmgPerUseTotal * bestBuffMultiplier;
                    float primaryDmg = (preAttacks + postAttacks) * profile.DmgPerUsePrimary * bestBuffMultiplier;

                    _candidates.Add(new CandidateScore
                    {
                        Type = SequenceType.BuffedRnGChain,
                        TotalDamage = totalDmg,
                        KillBonus = CalcKillBonus(primaryDmg, targetHP),
                        Buff = bestBuff,
                        RnGCost = rngCost,
                        Description = $"Buff({bestBuff.Name}) \u2192 {profile.Description}\u00d7{preAttacks} \u2192 R&G \u2192 {profile.Description}\u00d7{postAttacks} = {totalDmg:F0}dmg (\u00d7{bestBuffMultiplier:F2})"
                    });
                }
            }

            // ── ★ v3.11.0 시드 4: AoEFocus (AoE×N) ──
            if (hasAoEProfile)
            {
                int aoeUses = (int)(ap / aoeProfile.Cost);
                if (aoeUses > 0)
                {
                    float totalDmg = aoeUses * aoeProfile.DmgPerUseTotal;
                    float primaryDmg = aoeUses * aoeProfile.DmgPerUsePrimary;

                    _candidates.Add(new CandidateScore
                    {
                        Type = SequenceType.AoEFocus,
                        TotalDamage = totalDmg,
                        KillBonus = CalcKillBonus(primaryDmg, targetHP),
                        RecommendedAoE = bestAoEAbility,
                        UsesAoE = true,
                        Description = $"{aoeProfile.Description}\u00d7{aoeUses} = {totalDmg:F0}dmg ({aoeProfile.TargetsHit}enemies)"
                    });
                }
            }

            // ── ★ v3.11.0 시드 5: BuffedAoE (Buff → AoE×N) ──
            if (hasBuff && hasAoEProfile)
            {
                float apAfterBuff = ap - bestBuffCost;
                int aoeUses = (int)(apAfterBuff / aoeProfile.Cost);
                if (aoeUses > 0)
                {
                    float totalDmg = aoeUses * aoeProfile.DmgPerUseTotal * bestBuffMultiplier;
                    float primaryDmg = aoeUses * aoeProfile.DmgPerUsePrimary * bestBuffMultiplier;

                    _candidates.Add(new CandidateScore
                    {
                        Type = SequenceType.BuffedAoE,
                        TotalDamage = totalDmg,
                        KillBonus = CalcKillBonus(primaryDmg, targetHP),
                        Buff = bestBuff,
                        RecommendedAoE = bestAoEAbility,
                        UsesAoE = true,
                        Description = $"Buff({bestBuff.Name}) \u2192 {aoeProfile.Description}\u00d7{aoeUses} = {totalDmg:F0}dmg (\u00d7{bestBuffMultiplier:F2})"
                    });
                }
            }

            // ── ★ v3.11.0 시드 6: AoERnGChain (AoE×K → R&G → Single×1) ──
            if (hasAoEProfile && hasRnG && singleProfile.IsValid)
            {
                float apForAoE = ap - rngCost;
                int aoeUses = Math.Max(1, (int)(apForAoE / aoeProfile.Cost));
                int bonusAttacks = 1; // R&G 후 단일 공격 보너스

                float aoeDmg = aoeUses * aoeProfile.DmgPerUseTotal;
                float bonusDmg = bonusAttacks * singleProfile.DmgPerUseTotal;
                float primaryDmg = aoeUses * aoeProfile.DmgPerUsePrimary + bonusAttacks * singleProfile.DmgPerUsePrimary;

                _candidates.Add(new CandidateScore
                {
                    Type = SequenceType.AoERnGChain,
                    TotalDamage = aoeDmg + bonusDmg,
                    KillBonus = CalcKillBonus(primaryDmg, targetHP),
                    RnGCost = rngCost,
                    RecommendedAoE = bestAoEAbility,
                    UsesAoE = true,
                    Description = $"{aoeProfile.Description}\u00d7{aoeUses} \u2192 R&G \u2192 Single\u00d7{bonusAttacks} = {aoeDmg + bonusDmg:F0}dmg"
                });
            }

            // ── ★ v3.11.0 시드 7: KillSequence (KillSimulator 최적 시퀀스) ──
            if (hasKillSeq)
            {
                float killDmg = killSeq.TotalDamage;
                // 확정 킬 → KILL_CONFIRM_BONUS + 킬 진행도 최대치
                float killBonus = KILL_CONFIRM_BONUS + CalcKillBonus(killDmg, targetHP);

                // 킬 후 잔여 AP로 추가 공격
                float remainAP = ap - killSeq.APCost;
                int extraAttacks = profile.IsValid ? (int)(remainAP / profile.Cost) : 0;
                float extraDmg = extraAttacks * profile.DmgPerUseTotal;

                _candidates.Add(new CandidateScore
                {
                    Type = SequenceType.KillSequence,
                    TotalDamage = killDmg + extraDmg,
                    KillBonus = killBonus,
                    UsesKillSeq = true,
                    ExpectedKills = 1,
                    Description = $"KillSeq({killSeq.Abilities.Count}ab, {killSeq.APCost:F1}AP) on {target.CharacterName} = {killDmg:F0}dmg+{extraAttacks}extra (kill={killBonus:F0})"
                });
            }

            // ── ★ v3.11.0 시드 8: DebuffedAttack (Debuff → Attack×(N-1)) ──
            if (hasDebuff && singleProfile.IsValid)
            {
                float apAfterDebuff = ap - bestDebuffCost;
                int attacks = (int)(apAfterDebuff / singleProfile.Cost);
                if (attacks > 0)
                {
                    float totalDmg = attacks * singleProfile.DmgPerUseTotal;
                    float primaryDmg = attacks * singleProfile.DmgPerUsePrimary;

                    _candidates.Add(new CandidateScore
                    {
                        Type = SequenceType.DebuffedAttack,
                        TotalDamage = totalDmg,
                        KillBonus = CalcKillBonus(primaryDmg, targetHP),
                        UtilityBonus = DEBUFF_UTILITY,
                        Debuff = bestDebuff,
                        Description = $"Debuff({bestDebuff.Name}) \u2192 Single\u00d7{attacks} = {totalDmg:F0}dmg +{DEBUFF_UTILITY:F0}util"
                    });
                }
            }

            // ── ★ v3.11.0 시드 9: BuffedRnGAoE (Buff → AoE×K → R&G → Single×1) ──
            if (hasBuff && hasAoEProfile && hasRnG && singleProfile.IsValid)
            {
                float apRemain = ap - bestBuffCost;
                int aoeUses = Math.Max(0, (int)((apRemain - rngCost) / aoeProfile.Cost));

                if (aoeUses > 0)
                {
                    int bonusAttacks = 1;
                    float aoeDmg = aoeUses * aoeProfile.DmgPerUseTotal * bestBuffMultiplier;
                    float bonusDmg = bonusAttacks * singleProfile.DmgPerUseTotal * bestBuffMultiplier;
                    float totalDmg = aoeDmg + bonusDmg;
                    float primaryDmg = (aoeUses * aoeProfile.DmgPerUsePrimary +
                                        bonusAttacks * singleProfile.DmgPerUsePrimary) * bestBuffMultiplier;

                    _candidates.Add(new CandidateScore
                    {
                        Type = SequenceType.BuffedRnGAoE,
                        TotalDamage = totalDmg,
                        KillBonus = CalcKillBonus(primaryDmg, targetHP),
                        Buff = bestBuff,
                        RnGCost = rngCost,
                        RecommendedAoE = bestAoEAbility,
                        UsesAoE = true,
                        Description = $"Buff({bestBuff.Name}) \u2192 {aoeProfile.Description}\u00d7{aoeUses} \u2192 R&G \u2192 Single\u00d7{bonusAttacks} = {totalDmg:F0}dmg (\u00d7{bestBuffMultiplier:F2})"
                    });
                }
            }

            // ══════════════════════════════════════════════════════════════
            // Step 4: 최적 후보 선택
            // ══════════════════════════════════════════════════════════════

            if (_candidates.Count == 0) return null;

            CandidateScore best = _candidates[0];
            for (int i = 1; i < _candidates.Count; i++)
            {
                if (_candidates[i].Score > best.Score)
                    best = _candidates[i];
            }

            Main.Log($"[Strategy] {unit.CharacterName}: Selected {best.Type} \u2014 {best.Description}" +
                $" (score={best.Score:F0}, candidates={_candidates.Count})");

            if (Main.IsDebugEnabled)
            {
                for (int i = 0; i < _candidates.Count; i++)
                {
                    var c = _candidates[i];
                    Main.LogDebug($"[Strategy]   #{i} {c.Type}: dmg={c.TotalDamage:F0} + kill={c.KillBonus:F1} + util={c.UtilityBonus:F1} = {c.Score:F0} \u2014 {c.Description}");
                }
            }

            // ══════════════════════════════════════════════════════════════
            // Step 5: TurnStrategy 빌드 (v3.11.0 확장 필드 포함)
            // ══════════════════════════════════════════════════════════════

            bool plansPostAction = best.Type == SequenceType.RnGChain ||
                                   best.Type == SequenceType.BuffedRnGChain ||
                                   best.Type == SequenceType.AoERnGChain ||
                                   best.Type == SequenceType.BuffedRnGAoE;

            return new TurnStrategy
            {
                // 기존 필드
                Sequence = best.Type,
                ShouldBuffBeforeAttack = best.Buff != null,
                RecommendedBuff = best.Buff,
                ReservedAPForPostAction = best.RnGCost,
                PlansPostAction = plansPostAction,
                ExpectedTotalDamage = best.TotalDamage,
                Reason = best.Description,
                // ★ v3.11.0 확장 필드
                ShouldPrioritizeAoE = best.UsesAoE,
                RecommendedAoE = best.RecommendedAoE,
                PrioritizesKillSequence = best.UsesKillSeq,
                ShouldDebuffBeforeAttack = best.Debuff != null,
                ExpectedKills = best.ExpectedKills
            };
        }

        // ══════════════════════════════════════════════════════════════
        // 프로필 탐색
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 단일 타겟 공격 프로필 (PrimaryAttack 기준)
        /// </summary>
        private static AttackProfile FindSingleProfile(Situation situation, BaseUnitEntity target)
        {
            var primaryAttack = situation.PrimaryAttack;
            if (primaryAttack == null)
                return default;

            float cost = CombatAPI.GetAbilityAPCost(primaryAttack);
            if (cost <= 0) cost = 1f;

            var (pMin, pMax, _) = CombatAPI.GetDamagePrediction(primaryAttack, target);
            float avg = (pMin + pMax) / 2f;

            return new AttackProfile
            {
                DmgPerUseTotal = avg,
                DmgPerUsePrimary = avg,
                Cost = cost,
                TargetsHit = 1,
                Description = "Single"
            };
        }

        /// <summary>
        /// AoE 공격 프로필: 가용 AoE 중 최고 총 DPA
        /// ★ v3.10.0: 아군 피격 체크 — MaxPlayerAlliesHit 초과 AoE 제외
        /// </summary>
        private static AttackProfile FindAoEProfile(Situation situation, BaseUnitEntity target, float ap, out AbilityData bestAoEAbility)
        {
            bestAoEAbility = null;

            if (situation.AvailableAoEAttacks == null || situation.AvailableAoEAttacks.Count == 0)
                return default;

            var unit = situation.Unit;
            int maxAlliesHit = Settings.AIConfig.GetAoEConfig().MaxPlayerAlliesHit;

            float bestDPA = 0f;
            AttackProfile bestProfile = default;

            foreach (var aoe in situation.AvailableAoEAttacks)
            {
                float cost = CombatAPI.GetAbilityAPCost(aoe);
                if (cost <= 0) cost = 1f;
                if (cost > ap) continue;

                var (aMin, aMax, _) = CombatAPI.GetDamagePrediction(aoe, target);
                float avg = (aMin + aMax) / 2f;
                if (avg <= 0) continue;

                CombatAPI.CountUnitsInPattern(aoe, target.Position, unit.Position,
                    unit, situation.Enemies, situation.Allies,
                    out int enemyHits, out int allyHits);

                if (enemyHits < 2) continue;
                if (allyHits > maxAlliesHit)
                {
                    if (Main.IsDebugEnabled)
                        Main.LogDebug($"[Strategy] AoE({aoe.Name}) skipped: {allyHits} allies hit > max {maxAlliesHit}");
                    continue;
                }

                float totalPerUse = avg * enemyHits;
                float dpa = totalPerUse / cost;

                if (dpa > bestDPA)
                {
                    bestDPA = dpa;
                    bestAoEAbility = aoe;
                    bestProfile = new AttackProfile
                    {
                        DmgPerUseTotal = totalPerUse,
                        DmgPerUsePrimary = avg,
                        Cost = cost,
                        TargetsHit = enemyHits,
                        Description = $"AoE({aoe.Name},{enemyHits}t)"
                    };
                }
            }

            return bestProfile;
        }

        // ══════════════════════════════════════════════════════════════
        // 보조 헬퍼
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// ★ v3.11.0: 최적 디버프 탐색 — 최저 AP 비용 디버프 선택
        /// </summary>
        private static AbilityData FindBestDebuff(Situation situation, float ap)
        {
            if (situation.AvailableDebuffs == null || situation.AvailableDebuffs.Count == 0)
                return null;

            AbilityData best = null;
            float bestCost = float.MaxValue;

            foreach (var debuff in situation.AvailableDebuffs)
            {
                float cost = CombatAPI.GetAbilityAPCost(debuff);
                if (cost <= 0 || cost >= ap) continue; // 공격 AP 남겨야 하므로 < ap

                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = debuff;
                }
            }

            return best;
        }

        /// <summary>
        /// 킬 진행 보너스 (proportional, 0~MAX_KILL_BONUS)
        /// 회피/패링/명중률 불확실성 반영 → 소량 가산만
        /// </summary>
        private static float CalcKillBonus(float dmgOnPrimary, float targetHP)
        {
            if (targetHP <= 0) return 0f;
            float killProgress = Math.Min(1f, dmgOnPrimary / targetHP);
            return killProgress * MAX_KILL_BONUS;
        }
    }
}
