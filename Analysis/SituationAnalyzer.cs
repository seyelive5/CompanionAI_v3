using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using CompanionAI_v3.Core;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Analysis
{
    /// <summary>
    /// 상황 분석기 - 전투 상황을 분석하여 Situation 객체 생성
    /// </summary>
    public class SituationAnalyzer
    {
        /// <summary>
        /// 현재 상황 분석
        /// </summary>
        public Situation Analyze(BaseUnitEntity unit, TurnState turnState)
        {
            var situation = new Situation
            {
                Unit = unit
            };

            try
            {
                // 기본 상태
                AnalyzeUnitState(situation, unit, turnState);

                // 설정
                LoadSettings(situation, unit);

                // 무기 & 탄약
                AnalyzeWeapons(situation, unit);

                // 전장 상황
                AnalyzeBattlefield(situation, unit);

                // 타겟 분석
                AnalyzeTargets(situation, unit);

                // 위치 분석
                AnalyzePosition(situation, unit);

                // 능력 분류
                AnalyzeAbilities(situation, unit);

                // 턴 상태 복사
                CopyTurnState(situation, turnState);

                Main.LogDebug($"[Analyzer] {situation}");
            }
            catch (Exception ex)
            {
                Main.LogError($"[Analyzer] Error analyzing situation: {ex.Message}");
            }

            return situation;
        }

        #region Analysis Methods

        private void AnalyzeUnitState(Situation situation, BaseUnitEntity unit, TurnState turnState)
        {
            situation.HPPercent = CombatAPI.GetHPPercent(unit);
            // ★ v3.0.10: Commands.Empty 체크로 게임 상태가 완전히 업데이트된 후 분석됨
            // 따라서 게임 API 값을 직접 사용 (TurnState 값은 참조용)
            situation.CurrentAP = CombatAPI.GetCurrentAP(unit);
            situation.CurrentMP = CombatAPI.GetCurrentMP(unit);
            situation.CanMove = situation.CurrentMP > 0 && CombatAPI.CanMove(unit);
            situation.CanAct = CombatAPI.CanAct(unit);
        }

        private void LoadSettings(Situation situation, BaseUnitEntity unit)
        {
            var settings = ModSettings.Instance;
            var charSettings = settings?.GetOrCreateSettings(unit.UniqueId, unit.CharacterName);

            situation.CharacterSettings = charSettings;
            situation.RangePreference = charSettings?.RangePreference ?? RangePreference.Adaptive;
            situation.MinSafeDistance = charSettings?.MinSafeDistance ?? 5f;
        }

        private void AnalyzeWeapons(Situation situation, BaseUnitEntity unit)
        {
            situation.NeedsReload = CombatAPI.NeedsReloadAnyRanged(unit);
            situation.HasRangedWeapon = CombatAPI.HasRangedWeapon(unit);
            situation.HasMeleeWeapon = CombatAPI.HasMeleeWeapon(unit);
            situation.CurrentAmmo = CombatAPI.GetCurrentAmmo(unit);
            situation.MaxAmmo = CombatAPI.GetMaxAmmo(unit);
        }

        private void AnalyzeBattlefield(Situation situation, BaseUnitEntity unit)
        {
            // 적 목록
            situation.Enemies = CombatAPI.GetEnemies(unit);

            // 아군 목록
            situation.Allies = CombatAPI.GetAllies(unit);

            // 가장 가까운 적
            situation.NearestEnemy = situation.Enemies
                .Where(e => e != null && !e.LifeState.IsDead)
                .OrderBy(e => CombatAPI.GetDistance(unit, e))
                .FirstOrDefault();

            situation.NearestEnemyDistance = situation.NearestEnemy != null
                ? CombatAPI.GetDistance(unit, situation.NearestEnemy)
                : float.MaxValue;

            // 가장 부상당한 아군
            situation.MostWoundedAlly = situation.Allies
                .Where(a => a != null && !a.LifeState.IsDead && a != unit)
                .OrderBy(a => CombatAPI.GetHPPercent(a))
                .FirstOrDefault();
        }

        private void AnalyzeTargets(Situation situation, BaseUnitEntity unit)
        {
            // 공격 가능한 적 찾기
            situation.HittableEnemies = new List<BaseUnitEntity>();

            // 공격 능력 찾기
            var attackAbility = CombatAPI.FindAnyAttackAbility(unit, situation.RangePreference);

            // ★ v3.0.14: attackAbility 로깅
            if (attackAbility == null)
            {
                Main.LogDebug($"[Analyzer] No attack ability found for {unit.CharacterName} (pref={situation.RangePreference})");
            }
            else
            {
                Main.LogDebug($"[Analyzer] Using attack ability: {attackAbility.Name} for targeting check");
            }

            // ★ Target Scoring System 사용
            var targetScores = CombatAPI.ScoreAllTargets(unit, situation.Enemies, attackAbility, situation.RangePreference);

            foreach (var score in targetScores)
            {
                if (score.IsHittable)
                {
                    situation.HittableEnemies.Add(score.Target);
                }
            }

            // ★ v3.0.14: Hittable 결과 로깅
            if (situation.HittableEnemies.Count == 0 && situation.Enemies?.Count > 0)
            {
                Main.LogDebug($"[Analyzer] No hittable enemies! (total={situation.Enemies.Count})");
            }

            // ★ 최적 타겟 선택 (CombatAPI.FindBestTarget 사용)
            situation.BestTarget = CombatAPI.FindBestTarget(unit, situation.Enemies, attackAbility, situation.RangePreference);

            // 처치 가능 여부
            if (situation.BestTarget != null)
            {
                int targetHP = situation.BestTarget.Health?.HitPointsLeft ?? 100;
                float estimatedDamage = EstimateDamage(unit) * (situation.CurrentAP / 1f);
                situation.CanKillBestTarget = estimatedDamage >= targetHP;
            }
        }

        private void AnalyzePosition(Situation situation, BaseUnitEntity unit)
        {
            // ★ CombatAPI.ShouldRetreat 사용
            situation.IsInDanger = CombatAPI.ShouldRetreat(
                unit,
                situation.RangePreference,
                situation.NearestEnemyDistance,
                situation.MinSafeDistance);

            // 이동 필요: 공격 가능한 적 없음
            situation.NeedsReposition = !situation.HasHittableEnemies && situation.HasLivingEnemies;

            // 엄폐 분석
            if (situation.NearestEnemy != null)
            {
                var cover = CombatAPI.GetCoverTypeAtPosition(unit.Position, situation.NearestEnemy);
                situation.HasCover = cover != CombatAPI.CoverLevel.None;
            }

            // ★ v3.0.60: 후퇴 위치 계산 - MovementAPI 기반 실제 도달 가능 타일 사용
            if (situation.IsInDanger && situation.CanMove)
            {
                var retreatScore = MovementAPI.FindRetreatPositionSync(
                    unit,
                    situation.Enemies,
                    situation.MinSafeDistance);
                situation.BetterPositionAvailable = retreatScore != null;
            }
        }

        private void AnalyzeAbilities(Situation situation, BaseUnitEntity unit)
        {
            var allAbilities = CombatAPI.GetAvailableAbilities(unit);

            // ★ v3.0.20: 디버그용 능력 분류 로깅
            Main.LogDebug($"[Analyzer] {unit.CharacterName}: Analyzing {allAbilities.Count} abilities");

            foreach (var ability in allAbilities)
            {
                // ★ LESSONS_LEARNED 10.3: Veil 체크 - 사이킥 능력 안전성
                if (CombatAPI.IsPsychicAbility(ability) && !CombatAPI.IsPsychicSafeToUse(ability))
                {
                    Main.LogDebug($"[Analyzer] Blocked psychic {ability.Name}: Veil too high ({CombatAPI.GetVeilThickness()})");
                    continue;  // 이 능력 스킵
                }

                var timing = AbilityDatabase.GetTiming(ability);

                // ★ v3.0.20: 분류 과정 디버그 로깅
                var bp = ability.Blueprint;
                Main.LogDebug($"[Analyzer] Ability: {ability.Name} -> Timing={timing}, " +
                    $"CanTargetFriends={bp?.CanTargetFriends}, CanTargetEnemies={bp?.CanTargetEnemies}, Weapon={ability.Weapon != null}");

                switch (timing)
                {
                    case AbilityTiming.Reload:
                        if (situation.ReloadAbility == null)
                            situation.ReloadAbility = ability;
                        break;

                    case AbilityTiming.Healing:
                        situation.AvailableHeals.Add(ability);
                        break;

                    case AbilityTiming.SelfDamage:
                        // ★ v3.0.39: 자해 스킬 - HP 임계값 이상이면 버프로 사용 가능
                        {
                            float hpThreshold = AbilityDatabase.GetHPThreshold(ability);
                            if (hpThreshold > 0 && situation.HPPercent < hpThreshold)
                            {
                                Main.LogDebug($"[Analyzer] Blocked SelfDamage {ability.Name}: HP {situation.HPPercent:F0}% < threshold {hpThreshold}%");
                                break;  // HP 부족 - 추가하지 않음
                            }
                            // HP 충분하면 버프로 추가 (TurnPlanner에서 상황에 맞게 사용)
                            if (!CombatAPI.HasActiveBuff(unit, ability))
                            {
                                situation.AvailableBuffs.Add(ability);
                                Main.LogDebug($"[Analyzer] SelfDamage available: {ability.Name} (HP={situation.HPPercent:F0}% >= {hpThreshold}%)");
                            }
                        }
                        break;

                    case AbilityTiming.PreCombatBuff:
                    case AbilityTiming.PreAttackBuff:
                        // 이미 활성화된 버프 제외
                        if (!CombatAPI.HasActiveBuff(unit, ability))
                        {
                            situation.AvailableBuffs.Add(ability);
                        }
                        break;

                    case AbilityTiming.PostFirstAction:
                        if (AbilityDatabase.IsRunAndGun(ability))
                        {
                            situation.RunAndGunAbility = ability;
                        }
                        break;

                    case AbilityTiming.Debuff:
                        situation.AvailableDebuffs.Add(ability);
                        break;

                    // ★ v3.0.21: 위치 타겟 버프 (전방/보조/후방 구역)
                    case AbilityTiming.PositionalBuff:
                        situation.AvailablePositionalBuffs.Add(ability);
                        break;

                    // ★ v3.0.23: 구역 강화 스킬 (Stratagem)
                    case AbilityTiming.Stratagem:
                        situation.AvailableStratagems.Add(ability);
                        break;

                    // ★ v3.0.33: 마킹 스킬 (공격 전 적 지정)
                    // ★ v3.0.42: HP 임계값 체크 추가 (BloodOath 등 HP 비용 마커)
                    case AbilityTiming.Marker:
                        {
                            float hpThreshold = AbilityDatabase.GetHPThreshold(ability);
                            if (hpThreshold > 0 && situation.HPPercent < hpThreshold)
                            {
                                Main.LogDebug($"[Analyzer] Blocked Marker {ability.Name}: HP {situation.HPPercent:F0}% < threshold {hpThreshold}%");
                                break;  // HP 부족 - 추가하지 않음
                            }
                            situation.AvailableMarkers.Add(ability);
                            Main.LogDebug($"[Analyzer] Marker available: {ability.Name}");
                        }
                        break;

                    // ★ 특수 능력 처리 (DOT 강화, 연쇄 효과)
                    case AbilityTiming.DOTIntensify:
                    case AbilityTiming.ChainEffect:
                        situation.AvailableSpecialAbilities.Add(ability);
                        break;

                    // ★ v3.0.38: 명시적 타이밍 처리 추가
                    // HeroicAct, Taunt, TurnEnding, RighteousFury -> AvailableBuffs
                    // (TurnPlanner에서 AbilityDatabase.IsXXX()로 필터링)
                    case AbilityTiming.HeroicAct:
                    case AbilityTiming.Taunt:
                    case AbilityTiming.TurnEnding:
                    case AbilityTiming.RighteousFury:
                        situation.AvailableBuffs.Add(ability);
                        break;

                    // Finisher, GapCloser -> AvailableAttacks
                    // (TurnPlanner에서 AbilityDatabase.IsXXX()로 필터링)
                    case AbilityTiming.Finisher:
                    case AbilityTiming.GapCloser:
                        situation.AvailableAttacks.Add(ability);
                        break;

                    // DangerousAoE -> AvailableAttacks (주의해서 사용)
                    case AbilityTiming.DangerousAoE:
                        Main.LogDebug($"[Analyzer] DangerousAoE: {ability.Name} - added with caution");
                        situation.AvailableAttacks.Add(ability);
                        break;

                    // Emergency -> AvailableHeals (긴급 힐)
                    case AbilityTiming.Emergency:
                        situation.AvailableHeals.Add(ability);
                        break;

                    default:
                        // Normal 등 기타 공격 능력 분류
                        if (IsAttackAbility(ability, situation.RangePreference))
                        {
                            situation.AvailableAttacks.Add(ability);
                        }
                        break;
                }
            }

            // ★ RangePreference 필터 적용 (CombatHelpers 사용)
            situation.AvailableAttacks = CombatHelpers.FilterAbilitiesByRangePreference(
                situation.AvailableAttacks, situation.RangePreference);

            // 주 공격 및 최적 버프 선택
            situation.PrimaryAttack = SelectPrimaryAttack(situation, unit);
            situation.BestBuff = SelectBestBuff(situation.AvailableBuffs, situation);

            // ★ v3.0.20: 분류 결과 요약 로깅
            // ★ v3.0.21: PositionalBuffs 추가
            // ★ v3.0.23: Stratagems 추가
            // ★ v3.0.33: Markers 추가
            Main.Log($"[Analyzer] {unit.CharacterName} abilities: " +
                $"Buffs={situation.AvailableBuffs.Count}, " +
                $"Heals={situation.AvailableHeals.Count}, " +
                $"Debuffs={situation.AvailableDebuffs.Count}, " +
                $"Attacks={situation.AvailableAttacks.Count}, " +
                $"PositionalBuffs={situation.AvailablePositionalBuffs.Count}, " +
                $"Stratagems={situation.AvailableStratagems.Count}, " +
                $"Markers={situation.AvailableMarkers.Count}");

            if (situation.AvailableBuffs.Count > 0)
            {
                var buffNames = string.Join(", ", situation.AvailableBuffs.Select(b => b.Name));
                Main.LogDebug($"[Analyzer] Available buffs: {buffNames}");
            }
        }

        private void CopyTurnState(Situation situation, TurnState turnState)
        {
            if (turnState == null) return;

            situation.HasPerformedFirstAction = turnState.HasPerformedFirstAction;
            situation.HasBuffedThisTurn = turnState.HasBuffedThisTurn;
            situation.HasAttackedThisTurn = turnState.HasAttackedThisTurn;
            situation.HasHealedThisTurn = turnState.HasHealedThisTurn;
            situation.HasReloadedThisTurn = turnState.HasReloadedThisTurn;
            situation.HasMovedThisTurn = turnState.HasMovedThisTurn;
            situation.MoveCount = turnState.MoveCount;  // ★ v3.0.3

            // ★ v3.0.3: 공격 후 추가 이동 허용 판단
            // 이동→공격 완료 후 Hittable=0이면 추가 이동 허용
            situation.AllowPostAttackMove = turnState.AllowPostAttackMove && !situation.HasHittableEnemies;

            // ★ v3.0.7: 추격 이동 허용 판단
            // 이동했지만 아직 공격 못함 (적이 너무 멀어서) + 공격 가능 적 없음 + MP 남음
            // ★ v3.0.10: Commands.Empty 체크로 중복 이동 방지 (게임 API MP 값 사용)
            situation.AllowChaseMove = turnState.AllowChaseMove && !situation.HasHittableEnemies && situation.CurrentMP > 0;

            // ★ v3.0.73: 원거리 캐릭터 이동 제어 로직 수정
            // v3.0.65 버그: 버프만 사용해도 NeedsReposition=false → Hittable=0인데 이동 불가
            //
            // 올바른 로직:
            // - HasHittableEnemies=true → 현재 위치에서 공격 가능 → 이동 불필요
            // - HasHittableEnemies=false → 공격 위치 찾아야 함 → 이동 필요 (NeedsReposition 유지)
            // - HasMovedThisTurn=true → 이미 이동함 → 추가 이동 불필요
            if (turnState.HasPerformedFirstAction && situation.PrefersRanged)
            {
                // 공격 가능 적이 있거나, 이미 이동했으면 → 추가 이동 불필요
                if (situation.HasHittableEnemies || turnState.HasMovedThisTurn)
                {
                    situation.NeedsReposition = false;
                    situation.AllowChaseMove = false;
                    Main.LogDebug($"[Analyzer] Ranged character: hittable={situation.HasHittableEnemies}, moved={turnState.HasMovedThisTurn} - no reposition needed");
                }
                else
                {
                    // Hittable=0 && HasMovedThisTurn=false → 이동해서 공격 위치 찾아야 함
                    // NeedsReposition은 AnalyzePosition()에서 이미 올바르게 설정됨 (line 179)
                    Main.LogDebug($"[Analyzer] Ranged character acted but no hittable targets and hasn't moved - allow reposition (NeedsReposition={situation.NeedsReposition})");
                }
            }
        }

        #endregion

        #region Helper Methods

        // ★ v3.0.46: 미사용 메서드 제거 - UtilityScorer.SelectBestTarget으로 대체됨
        // private BaseUnitEntity SelectBestTarget(Situation situation, BaseUnitEntity unit) { ... }

        private bool IsAttackAbility(AbilityData ability, RangePreference preference)
        {
            if (ability == null) return false;

            // ★ v3.0.28: 마킹 스킬 제외 (Hunt Down the Prey 등 - 데미지 없음)
            if (AbilityDatabase.IsMarker(ability)) return false;

            // ★ v3.0.28: 디버프도 공격이 아님
            if (AbilityDatabase.IsDebuff(ability)) return false;

            // 무기 공격
            if (ability.Weapon != null)
            {
                // 재장전 제외
                if (AbilityDatabase.IsReload(ability)) return false;

                // RangePreference 필터
                if (preference == RangePreference.PreferRanged || preference == RangePreference.MaintainRange)
                {
                    if (ability.IsMelee) return false;  // 근접 제외
                }
                else if (preference == RangePreference.PreferMelee)
                {
                    if (!ability.IsMelee) return false;  // 원거리 제외
                }

                return true;
            }

            // 공격성 능력 (무기 없는 스킬 - 사이킥 등)
            // ★ v3.0.28: 마킹/디버프는 위에서 이미 제외됨
            // ★ v3.0.50: CanTargetFriends 조건 제거 - AoE 능력도 공격으로 분류
            //           (아군 피해 방지는 PlanSafeRangedAttack에서 처리)
            try
            {
                return ability.Blueprint?.CanTargetEnemies == true;
            }
            catch { return false; }
        }

        private AbilityData SelectPrimaryAttack(Situation situation, BaseUnitEntity unit)
        {
            var attacks = situation.AvailableAttacks;
            if (attacks == null || attacks.Count == 0) return null;

            // ★ CombatHelpers.GetAttackPriority 사용 - 상황 기반 최적 공격 선택
            var bestTarget = situation.BestTarget;

            return attacks
                .Where(a => a.Weapon != null)
                .OrderBy(a => CombatHelpers.GetAttackPriority(a, unit, bestTarget, situation.Enemies, situation.Allies))
                .ThenBy(a => CombatAPI.GetAbilityAPCost(a))
                .FirstOrDefault();
        }

        /// <summary>
        /// ★ v3.0.44: Utility 스코어링 기반 최적 버프 선택
        /// </summary>
        private AbilityData SelectBestBuff(List<AbilityData> buffs, Situation situation)
        {
            if (buffs == null || buffs.Count == 0) return null;

            // ★ v3.0.44: 스코어링 시스템 사용
            return UtilityScorer.SelectBestBuff(buffs, situation);
        }

        private float EstimateDamage(BaseUnitEntity unit)
        {
            // 레벨 기반 추정
            int level = unit?.Progression?.CharacterLevel ?? 10;
            return level * 3f + 15f;
        }

        #endregion
    }
}
