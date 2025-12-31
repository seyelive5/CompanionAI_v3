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

                // ★ v3.0.78: 순서 변경 - 능력 분류를 먼저 수행
                // 이유: AnalyzeTargets()에서 모든 AvailableAttacks를 기준으로 Hittable 계산
                AnalyzeAbilities(situation, unit);

                // 타겟 분석 (이제 AvailableAttacks 사용 가능)
                AnalyzeTargets(situation, unit);

                // 위치 분석
                AnalyzePosition(situation, unit);

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

            // ★ v3.0.78: 모든 AvailableAttacks를 기준으로 Hittable 계산
            // 이전: 단일 참조 능력 → 쿨다운이면 Hittable=0
            // 변경: 어떤 공격이든 사용 가능하면 Hittable
            var attacks = situation.AvailableAttacks;

            if (attacks == null || attacks.Count == 0)
            {
                // 폴백: 분류된 공격이 없으면 기존 방식
                var fallbackAbility = CombatAPI.FindAnyAttackAbility(unit, situation.RangePreference);
                if (fallbackAbility != null)
                {
                    attacks = new List<AbilityData> { fallbackAbility };
                }
                Main.LogDebug($"[Analyzer] No classified attacks, using fallback: {fallbackAbility?.Name ?? "none"}");
            }
            else
            {
                Main.LogDebug($"[Analyzer] Checking hittable with {attacks.Count} available attacks");
            }

            // 각 적에 대해 어떤 공격이든 사용 가능한지 확인
            foreach (var enemy in situation.Enemies)
            {
                if (enemy == null || enemy.LifeState.IsDead) continue;

                var targetWrapper = new TargetWrapper(enemy);
                bool isHittable = false;
                string hittableBy = null;

                foreach (var attack in attacks)
                {
                    if (attack == null) continue;
                    // 재장전, 턴 종료 스킬 제외
                    if (AbilityDatabase.IsReload(attack)) continue;
                    if (AbilityDatabase.IsTurnEnding(attack)) continue;
                    // ★ v3.0.83: GapCloser는 현재 위치에서 타격 불가 - 이동 용도만
                    // 죽음 강림 등은 PlanMoveOrGapCloser에서 사용
                    if (AbilityDatabase.IsGapCloser(attack)) continue;
                    // ★ v3.0.97: DOTIntensify/ChainEffect는 Hittable 계산에서 제외
                    // 이유: 이 능력만으로 Hittable=true가 되면 이동이 스킵됨
                    // 특수 능력은 PlanSpecialAbility()에서 별도로 타겟 검증
                    if (AbilityDatabase.IsDOTIntensify(attack)) continue;
                    if (AbilityDatabase.IsChainEffect(attack)) continue;

                    string reason;
                    if (CombatAPI.CanUseAbilityOn(attack, targetWrapper, out reason))
                    {
                        isHittable = true;
                        hittableBy = attack.Name;
                        break;
                    }
                }

                if (isHittable)
                {
                    situation.HittableEnemies.Add(enemy);
                    Main.LogDebug($"[Analyzer] {enemy.CharacterName} hittable by {hittableBy}");
                }
            }

            // Hittable 결과 로깅
            if (situation.HittableEnemies.Count == 0 && situation.Enemies?.Count > 0)
            {
                Main.LogDebug($"[Analyzer] No hittable enemies! (total={situation.Enemies.Count}, attacks={attacks?.Count ?? 0})");

                // ★ v3.0.79: RangeFilter 폴백 - 필터링된 공격으로 못 맞추면 전체 공격으로 재시도
                // 예: PreferMelee인데 일격이 쿨다운이면, 원거리 공격(죽음의 속삭임)도 허용
                var allAttacks = CombatAPI.GetAvailableAbilities(unit)
                    .Where(a => a.Blueprint?.CanTargetEnemies == true || a.Weapon != null)
                    .Where(a => !AbilityDatabase.IsReload(a))
                    .Where(a => !AbilityDatabase.IsTurnEnding(a))
                    .Where(a => !AbilityDatabase.IsHealing(a))
                    .Where(a => !AbilityDatabase.IsGapCloser(a))  // ★ v3.0.83: GapCloser 제외
                    .Where(a => !AbilityDatabase.IsMarker(a))     // ★ v3.0.84: Marker 제외 (실제 공격 아님)
                    .Where(a => {
                        var timing = AbilityDatabase.GetTiming(a);
                        return timing != AbilityTiming.PreCombatBuff &&
                               timing != AbilityTiming.PreAttackBuff &&
                               timing != AbilityTiming.PositionalBuff;
                    })
                    .ToList();

                if (allAttacks.Count > attacks?.Count)
                {
                    Main.Log($"[Analyzer] ★ RangeFilter fallback: trying {allAttacks.Count} unfiltered attacks");

                    foreach (var enemy in situation.Enemies)
                    {
                        if (enemy == null || enemy.LifeState.IsDead) continue;
                        if (situation.HittableEnemies.Contains(enemy)) continue;

                        var targetWrapper = new TargetWrapper(enemy);

                        foreach (var attack in allAttacks)
                        {
                            if (attack == null) continue;

                            string reason;
                            if (CombatAPI.CanUseAbilityOn(attack, targetWrapper, out reason))
                            {
                                situation.HittableEnemies.Add(enemy);
                                Main.LogDebug($"[Analyzer] {enemy.CharacterName} hittable by {attack.Name} (fallback)");

                                // ★ 폴백으로 찾은 공격을 AvailableAttacks에 추가
                                if (!situation.AvailableAttacks.Contains(attack))
                                {
                                    situation.AvailableAttacks.Add(attack);
                                    Main.Log($"[Analyzer] Added fallback attack: {attack.Name}");
                                }
                                break;
                            }
                        }
                    }

                    if (situation.HittableEnemies.Count > 0)
                    {
                        Main.Log($"[Analyzer] Fallback success! Hittable: {situation.HittableEnemies.Count}/{situation.Enemies.Count}");
                    }
                }
            }
            else
            {
                Main.LogDebug($"[Analyzer] Hittable: {situation.HittableEnemies.Count}/{situation.Enemies?.Count ?? 0}");
            }

            // ★ 최적 타겟 선택 - Hittable 적 중에서 선택
            situation.BestTarget = situation.HittableEnemies.Count > 0
                ? UtilityScorer.SelectBestTarget(situation.HittableEnemies, situation)
                : situation.NearestEnemy;  // 폴백: 가장 가까운 적

            // ★ v3.0.78: PrimaryAttack 선택 (BestTarget 설정 후)
            // 이전: AnalyzeAbilities()에서 선택 → BestTarget=null
            // 변경: AnalyzeTargets() 끝에서 선택 → BestTarget 활용 가능
            situation.PrimaryAttack = SelectPrimaryAttack(situation, unit);

            // 처치 가능 여부
            if (situation.BestTarget != null && situation.PrimaryAttack != null)
            {
                situation.CanKillBestTarget = CombatAPI.CanKillInOneHit(situation.PrimaryAttack, situation.BestTarget);
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
            // ★ v3.0.93: MP > 0 체크 추가 - MP 없으면 이동 불가능하므로 NeedsReposition=false
            situation.NeedsReposition = !situation.HasHittableEnemies && situation.HasLivingEnemies && situation.CurrentMP > 0;

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
                // ★ v3.0.88: CanTargetSelf, CanTargetPoint, Range 추가
                var bp = ability.Blueprint;
                Main.LogDebug($"[Analyzer] Ability: {ability.Name} -> Timing={timing}, " +
                    $"CanTargetSelf={bp?.CanTargetSelf}, CanTargetFriends={bp?.CanTargetFriends}, " +
                    $"CanTargetEnemies={bp?.CanTargetEnemies}, CanTargetPoint={bp?.CanTargetPoint}, " +
                    $"Range={bp?.Range}, Weapon={ability.Weapon != null}");

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

                    // ★ v3.0.96: 특수 능력 처리 (DOT 강화, 연쇄 효과)
                    // ★ 중요: AvailableAttacks에도 추가해야 Hittable 체크에 포함됨!
                    // 이전 버그: AvailableSpecialAbilities에만 추가 → Hittable=0 → 공격 스킵
                    case AbilityTiming.DOTIntensify:
                    case AbilityTiming.ChainEffect:
                        situation.AvailableSpecialAbilities.Add(ability);
                        situation.AvailableAttacks.Add(ability);  // ★ v3.0.96: Hittable 체크용
                        Main.LogDebug($"[Analyzer] Special attack added: {ability.Name} (Timing={timing})");
                        break;

                    // ★ v3.0.38: 명시적 타이밍 처리 추가
                    // HeroicAct, Taunt, RighteousFury -> AvailableBuffs
                    // (TurnPlanner에서 AbilityDatabase.IsXXX()로 필터링)
                    case AbilityTiming.HeroicAct:
                    case AbilityTiming.Taunt:
                    case AbilityTiming.RighteousFury:
                        situation.AvailableBuffs.Add(ability);
                        break;

                    // ★ v3.0.88: TurnEnding은 HP 임계값 체크 필요 (VeilOfBlades 등 HP 소모)
                    case AbilityTiming.TurnEnding:
                        {
                            float hpThreshold = AbilityDatabase.GetHPThreshold(ability);
                            if (hpThreshold > 0 && situation.HPPercent < hpThreshold)
                            {
                                Main.LogDebug($"[Analyzer] Blocked TurnEnding {ability.Name}: HP {situation.HPPercent:F0}% < threshold {hpThreshold}%");
                                break;  // HP 부족 - 추가하지 않음
                            }
                            situation.AvailableBuffs.Add(ability);
                        }
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

            // ★ v3.0.82: GapCloser는 RangePreference 필터에서 제외 (Death from Above 등)
            var gapClosers = situation.AvailableAttacks
                .Where(a => AbilityDatabase.IsGapCloser(a))
                .ToList();

            // ★ RangePreference 필터 적용 (CombatHelpers 사용)
            situation.AvailableAttacks = CombatHelpers.FilterAbilitiesByRangePreference(
                situation.AvailableAttacks, situation.RangePreference);

            // ★ v3.0.82: 필터링된 GapCloser 복원
            foreach (var gc in gapClosers)
            {
                if (!situation.AvailableAttacks.Contains(gc))
                {
                    situation.AvailableAttacks.Add(gc);
                    Main.LogDebug($"[Analyzer] Restored GapCloser after filter: {gc.Name}");
                }
            }

            // ★ v3.0.78: PrimaryAttack 선택은 AnalyzeTargets() 이후로 이동
            // 이유: BestTarget이 설정된 후 최적 공격 선택 가능
            // situation.PrimaryAttack = SelectPrimaryAttack(situation, unit);  // -> AnalyzeTargets()로 이동
            situation.BestBuff = SelectBestBuff(situation.AvailableBuffs, situation);

            // ★ v3.0.20: 분류 결과 요약 로깅
            // ★ v3.0.21: PositionalBuffs 추가
            // ★ v3.0.23: Stratagems 추가
            // ★ v3.0.33: Markers 추가
            // ★ v3.0.87: GapClosers 카운트 추가
            var finalGapClosers = situation.AvailableAttacks.Where(a => AbilityDatabase.IsGapCloser(a)).ToList();
            Main.Log($"[Analyzer] {unit.CharacterName} abilities: " +
                $"Buffs={situation.AvailableBuffs.Count}, " +
                $"Heals={situation.AvailableHeals.Count}, " +
                $"Debuffs={situation.AvailableDebuffs.Count}, " +
                $"Attacks={situation.AvailableAttacks.Count}, " +
                $"GapClosers={finalGapClosers.Count}, " +
                $"PositionalBuffs={situation.AvailablePositionalBuffs.Count}, " +
                $"Stratagems={situation.AvailableStratagems.Count}, " +
                $"Markers={situation.AvailableMarkers.Count}");

            // ★ v3.0.87: GapClosers 이름 로깅
            if (finalGapClosers.Count > 0)
            {
                Main.Log($"[Analyzer] GapClosers: {string.Join(", ", finalGapClosers.Select(g => g.Name))}");
            }

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
                // 공격 가능 적이 있거나, 이미 이동했거나, MP가 없으면 → 추가 이동 불필요
                // ★ v3.0.93: MP=0 체크 추가
                if (situation.HasHittableEnemies || turnState.HasMovedThisTurn || situation.CurrentMP <= 0)
                {
                    situation.NeedsReposition = false;
                    situation.AllowChaseMove = false;
                    Main.LogDebug($"[Analyzer] Ranged character: hittable={situation.HasHittableEnemies}, moved={turnState.HasMovedThisTurn}, MP={situation.CurrentMP:F1} - no reposition needed");
                }
                else
                {
                    // Hittable=0 && HasMovedThisTurn=false && MP>0 → 이동해서 공격 위치 찾아야 함
                    // NeedsReposition은 AnalyzePosition()에서 이미 올바르게 설정됨
                    Main.LogDebug($"[Analyzer] Ranged character acted but no hittable targets and hasn't moved - allow reposition (NeedsReposition={situation.NeedsReposition}, MP={situation.CurrentMP:F1})");
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
                if (preference == RangePreference.PreferRanged)
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
