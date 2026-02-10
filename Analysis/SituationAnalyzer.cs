using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.Blueprints;  // ★ v3.7.29: GetComponent<T>() 확장 메서드
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Enums;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Components;  // ★ v3.7.29: AbilityMultiTarget
using Kingmaker.UnitLogic.Parts;  // ★ v3.8.90: PartOverwatch
using Kingmaker.Utility;
using UnityEngine;
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
        // ★ v3.8.48: per-unit Situation 풀 — 턴당 0 할당 (기존: 1 Situation + 13 List per turn)
        private readonly Dictionary<string, Situation> _situationPool = new Dictionary<string, Situation>();

        // ★ v3.8.80: continuation 턴 캐시 — 같은 턴 내 InfluenceMap/PredictiveThreatMap 재사용
        // 적은 이동하지 않았으므로 영향력 맵과 위협 맵은 동일
        private string _cachedMapUnitId;
        private int _cachedMapRound;
        private BattlefieldInfluenceMap _cachedInfluenceMap;
        private PredictiveThreatMap _cachedPredictiveThreatMap;

        /// <summary>
        /// ★ v3.8.48: 전투 종료 시 풀 정리
        /// </summary>
        public void ClearPool()
        {
            _situationPool.Clear();
            _cachedMapUnitId = null;
            _cachedMapRound = -1;
            _cachedInfluenceMap = null;
            _cachedPredictiveThreatMap = null;
        }

        /// <summary>
        /// 현재 상황 분석
        /// </summary>
        public Situation Analyze(BaseUnitEntity unit, TurnState turnState)
        {
            // ★ v3.8.48: 객체 풀링 — new Situation() 대신 재사용
            string unitId = unit.UniqueId;
            Situation situation;
            if (!_situationPool.TryGetValue(unitId, out situation))
            {
                situation = new Situation();
                _situationPool[unitId] = situation;
            }
            else
            {
                situation.Reset();
            }
            situation.Unit = unit;

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
                AnalyzePosition(situation, unit, turnState);

                // 턴 상태 복사
                CopyTurnState(situation, turnState);

                // ★ v3.7.00: 사역마 분석 (Overseer 아키타입 지원)
                AnalyzeFamiliar(situation, unit);

                Main.LogDebug($"[Analyzer] {situation}");
            }
            catch (Exception ex)
            {
                // ★ v3.5.36: 분석 실패 시 null 반환 - TurnOrchestrator에서 처리
                // ★ v3.8.24: InnerException 출력 (TypeInitializationException 디버깅)
                Main.LogError($"[Analyzer] Error analyzing situation: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Main.LogError($"[Analyzer] Inner exception: {ex.InnerException.Message}");
                    Main.LogError($"[Analyzer] Inner stack: {ex.InnerException.StackTrace}");
                }
                Main.LogError($"[Analyzer] Stack: {ex.StackTrace}");
                return null;
            }

            return situation;
        }

        #region Analysis Methods

        private void AnalyzeUnitState(Situation situation, BaseUnitEntity unit, TurnState turnState)
        {
            situation.HPPercent = CombatCache.GetHPPercent(unit);
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

            // ★ v3.8.48: LINQ → CollectionHelper (0 할당, O(n))
            // ★ v3.5.29: 캐시된 거리 사용
            situation.NearestEnemy = CollectionHelper.MinByWhere(situation.Enemies,
                e => !e.LifeState.IsDead,
                e => CombatCache.GetDistance(unit, e));

            situation.NearestEnemyDistance = situation.NearestEnemy != null
                ? CombatCache.GetDistance(unit, situation.NearestEnemy)
                : float.MaxValue;

            // ★ v3.8.48: LINQ → CollectionHelper (0 할당, O(n))
            // 가장 부상당한 아군
            situation.MostWoundedAlly = CollectionHelper.MinByWhere(situation.Allies,
                a => !a.LifeState.IsDead && a != unit,
                a => CombatCache.GetHPPercent(a));

            // ★ v3.1.25: 위협 분석 (아군 타겟팅 적)
            AnalyzeThreats(situation, unit);
        }

        /// <summary>
        /// ★ v3.1.25: 위협 분석 - 아군을 타겟팅하는 적 파악
        /// </summary>
        private void AnalyzeThreats(Situation situation, BaseUnitEntity unit)
        {
            int alliesUnderThreat = 0;
            int enemiesTargetingAllies = 0;

            var threatenedAllies = new HashSet<BaseUnitEntity>();

            foreach (var ally in situation.Allies)
            {
                if (ally == null || ally == unit) continue;  // 자신 제외

                foreach (var enemy in situation.Enemies)
                {
                    if (enemy == null) continue;
                    if (CombatAPI.IsTargeting(enemy, ally))
                    {
                        enemiesTargetingAllies++;
                        threatenedAllies.Add(ally);
                    }
                }
            }

            alliesUnderThreat = threatenedAllies.Count;

            situation.AlliesUnderThreat = alliesUnderThreat;
            situation.EnemiesTargetingAllies = enemiesTargetingAllies;

            if (enemiesTargetingAllies > 0)
            {
                Main.LogDebug($"[Analyzer] Threat: {enemiesTargetingAllies} enemies targeting {alliesUnderThreat} allies");
            }

            // ★ v3.8.90: 적 오버워치 구역 감지
            // PartOverwatch.Contains(unit)는 유닛의 모든 점유 노드를 확인 (대형 유닛 지원)
            try
            {
                int overwatchCount = 0;
                foreach (var enemy in situation.Enemies)
                {
                    if (enemy == null || enemy.LifeState.IsDead) continue;
                    var ow = enemy.GetOptional<PartOverwatch>();
                    if (ow != null && !ow.IsStopped && ow.Contains(unit))
                    {
                        overwatchCount++;
                    }
                }
                situation.EnemyOverwatchCount = overwatchCount;
                situation.IsInEnemyOverwatchZone = overwatchCount > 0;
                if (overwatchCount > 0 && Main.IsDebugEnabled)
                    Main.LogDebug($"[Analyzer] {unit.CharacterName}: In {overwatchCount} enemy overwatch zone(s)");
            }
            catch { }
        }

        private void AnalyzeTargets(Situation situation, BaseUnitEntity unit)
        {
            // 공격 가능한 적 찾기
            // ★ v3.8.48: new List → Clear() (풀링된 리스트 재사용)
            situation.HittableEnemies.Clear();
            situation.MeleeHittableEnemies.Clear();  // ★ v3.8.14: 근접 공격으로 가능한 적

            // ★ v3.0.78: 모든 AvailableAttacks를 기준으로 Hittable 계산
            // 이전: 단일 참조 능력 → 쿨다운이면 Hittable=0
            // 변경: 어떤 공격이든 사용 가능하면 Hittable
            var attacks = situation.AvailableAttacks;

            // ★ v3.5.12: 폴백 로직 제거 - 디버프가 Hittable에 포함되는 문제 해결
            // 문제: FindAnyAttackAbility()가 디버프(적 분석, 빈틈 노리기)를 반환
            // 결과: Hittable=true이지만 AvailableAttacks=0 → "DPS no targets"
            // 해결: AvailableAttacks가 비어있으면 Hittable=0 (디버프는 디버프 Phase에서 사용)
            if (attacks == null || attacks.Count == 0)
            {
                Main.LogDebug($"[Analyzer] No classified attacks, Hittable will be 0");
                attacks = new List<AbilityData>();  // 빈 리스트로 진행
            }
            else
            {
                Main.LogDebug($"[Analyzer] Checking hittable with {attacks.Count} available attacks");
            }

            // ★ v3.8.70: 위협 범위 체크 (CombatHelpers 공통 필터에서 사용)
            bool isInThreatArea = CombatAPI.IsInThreateningArea(unit);

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
                    // ★ v3.8.70: 공통 필터 (AttackPlanner와 동기화 — CombatHelpers 중앙집중)
                    if (CombatHelpers.ShouldExcludeFromAttack(attack, isInThreatArea)) continue;
                    // Analyzer 전용 필터 (Hittable 계산 특화)
                    if (AbilityDatabase.IsGapCloser(attack)) continue;
                    if (AbilityDatabase.IsDOTIntensify(attack)) continue;
                    if (AbilityDatabase.IsChainEffect(attack)) continue;
                    if (AbilityDatabase.IsDangerousAoE(attack)) continue;

                    // ★ v3.1.19: Point 타겟 AOE 처리 개선
                    if (CombatAPI.IsPointTargetAbility(attack))
                    {
                        var patternInfo = CombatAPI.GetPatternInfo(attack);
                        if (patternInfo == null || !patternInfo.IsValid) continue;

                        // ★ v3.5.98: 타일 단위로 통일
                        int castRange = CombatAPI.GetAbilityRangeInTiles(attack);  // 타일
                        float patternRadius = patternInfo.Radius;                   // 타일
                        float effectiveRange;

                        // ★ v3.8.08 -> v3.8.32: Ray/Cone/Sector 패턴은 시전자에서 뻗어나가는 패턴
                        // - 무기 사거리로 "타겟 지점"을 지정할 수 있지만
                        // - 실제 효과는 patternRadius만큼만 닿음 (caster에서 시작)
                        // - ★ v3.8.32 FIX: IsDirectional 대신 CanBeDirectional 사용
                        //   IsDirectional은 "방향 지정 여부"이고, CanBeDirectional은 "패턴 타입"
                        //   Ray는 IsDirectional=false여도 caster에서 발사됨!
                        bool canBeDirectionalPattern = patternInfo.CanBeDirectional;  // Ray/Cone/Sector

                        if (canBeDirectionalPattern)
                        {
                            // Ray/Cone/Sector: caster에서 시작하여 patternRadius만큼 뻗음
                            // 적이 patternRadius 내에 있어야 실제로 맞음
                            effectiveRange = patternRadius;
                            Main.LogDebug($"[Analyzer] {attack.Name}: Ray/Cone/Sector pattern, effective range={patternRadius:F0} tiles (not weapon range!)");
                        }
                        else if (castRange >= 1000)  // Unlimited range (Circle AOE)
                        {
                            // ★ v3.5.99: Unlimited = 맵 어디든 타겟 가능
                            // 적 위치를 직접 타겟하면 AOE 반경 내에 항상 포함됨 (거리 0)
                            // 따라서 거리 제한 없이 모든 적이 Hittable
                            effectiveRange = 10000f;  // 사실상 무제한
                            Main.LogDebug($"[Analyzer] {attack.Name}: Unlimited range, AOE r={patternRadius:F0} tiles");
                        }
                        else
                        {
                            effectiveRange = castRange + patternRadius;
                        }

                        // ★ v3.5.98: 타일 단위 거리 비교
                        float dist = CombatCache.GetDistanceInTiles(unit, enemy);  // 타일
                        if (dist > effectiveRange)
                        {
                            Main.LogDebug($"[Analyzer] {attack.Name}: Too far ({dist:F1} > {effectiveRange:F1} tiles)");
                            continue;
                        }

                        // ★ v3.8.70: IsAoESafe → IsAttackSafeForTarget (반경+scatter 통합)
                        if (!CombatHelpers.IsAttackSafeForTarget(attack, unit, enemy, situation.Allies))
                        {
                            Main.LogDebug($"[Analyzer] Attack unsafe: {attack.Name} -> {enemy.CharacterName}");
                            continue;
                        }

                        // ★ v3.6.9: AOE 높이 차이 체크 추가
                        // 게임 로직: Circle=1.6m, Directional=0.3m 초과 시 효과 없음
                        if (!CombatAPI.IsAoEHeightInRange(attack, unit, enemy))
                        {
                            Main.LogDebug($"[Analyzer] AOE height failed: {attack.Name} -> {enemy.CharacterName}");
                            continue;
                        }

                        // ★ v3.5.13: Point AOE에 대해 LOS 체크 추가
                        // 문제: 거리/안전성 체크만 하면 Hittable=true지만 실제 시전 시 HasNoLosToTarget
                        // 해결: 적 위치를 타겟 포인트로 CanUseAbilityOn 체크
                        // ★ v3.5.29: 캐시 사용
                        var pointTarget = new TargetWrapper(enemy.Position);
                        string aoeReason;
                        if (!CombatCache.CanUseAbilityOn(attack, pointTarget, out aoeReason))
                        {
                            Main.LogDebug($"[Analyzer] AOE LOS failed: {attack.Name} -> {enemy.CharacterName} ({aoeReason})");
                            continue;
                        }

                        isHittable = true;
                        hittableBy = $"{attack.Name} (AOE r={patternInfo.Radius:F0})";
                        break;
                    }

                    // ★ v3.5.29: 캐시 사용
                    // ★ v3.6.13: 실패 시 이유 로깅 추가
                    string reason;
                    if (CombatCache.CanUseAbilityOn(attack, targetWrapper, out reason))
                    {
                        // ★ v3.8.70: 안전성 체크 (scatter safety 포함)
                        if (!CombatHelpers.IsAttackSafeForTarget(attack, unit, enemy, situation.Allies))
                        {
                            Main.LogDebug($"[Analyzer] Attack unsafe: {attack.Name} -> {enemy.CharacterName}");
                            continue;
                        }
                        isHittable = true;
                        hittableBy = attack.Name;
                        break;
                    }
                    else
                    {
                        // ★ v3.6.13: 원거리 공격 실패 시 원인 로깅 (디버깅용)
                        float distTiles = CombatCache.GetDistanceInTiles(unit, enemy);
                        float rangeTiles = CombatAPI.GetAbilityRangeInTiles(attack);
                        Main.LogDebug($"[Analyzer] CanUse failed: {attack.Name} -> {enemy.CharacterName} ({reason}), dist={distTiles:F1} tiles, range={rangeTiles:F0} tiles");
                    }
                }

                if (isHittable)
                {
                    situation.HittableEnemies.Add(enemy);
                    Main.LogDebug($"[Analyzer] {enemy.CharacterName} hittable by {hittableBy}");
                }
            }

            // ★ v3.8.14: 근접 선호 캐릭터의 경우, 폴백 전에 근접 공격으로 타격 가능한 적 저장
            // 폴백이 원거리 공격을 추가하면, 근접 캐릭터가 "공격 가능"으로 판단하여 접근 안 함
            // MeleeHittableEnemies는 "실제 근접 공격이 닿는 적"을 추적
            if (situation.RangePreference == RangePreference.PreferMelee)
            {
                // ★ v3.8.48: new List → Clear+AddRange (풀링된 리스트 재사용)
                situation.MeleeHittableEnemies.Clear();
                for (int i = 0; i < situation.HittableEnemies.Count; i++)
                    situation.MeleeHittableEnemies.Add(situation.HittableEnemies[i]);
            }

            // Hittable 결과 로깅
            if (situation.HittableEnemies.Count == 0 && situation.Enemies?.Count > 0)
            {
                Main.LogDebug($"[Analyzer] No hittable enemies! (total={situation.Enemies.Count}, attacks={attacks?.Count ?? 0})");

                // ★ v3.0.79: RangeFilter 폴백 - 필터링된 공격으로 못 맞추면 전체 공격으로 재시도
                // 예: PreferMelee인데 일격이 쿨다운이면, 원거리 공격(죽음의 속삭임)도 허용
                // ★ v3.5.12: Debuff 제외 - 빈틈 노리기 같은 디버프가 Hittable 계산에 포함되는 문제
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
                               timing != AbilityTiming.PositionalBuff &&
                               timing != AbilityTiming.Debuff;  // ★ v3.5.12: Debuff 제외
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
                            // ★ v3.8.70: fallback에도 공통 필터 + 안전 체크 적용
                            if (CombatHelpers.ShouldExcludeFromAttack(attack, isInThreatArea)) continue;

                            // ★ v3.5.29: 캐시 사용
                            string reason;
                            if (CombatCache.CanUseAbilityOn(attack, targetWrapper, out reason))
                            {
                                // ★ v3.8.70: 안전성 체크 (scatter safety 포함)
                                if (!CombatHelpers.IsAttackSafeForTarget(attack, unit, enemy, situation.Allies))
                                    continue;

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

            // ★ v3.1.21: 최적 타겟 선택 - TargetScorer 기반 Role별 가중치 적용
            var role = situation.CharacterSettings?.Role ?? AIRole.Auto;
            var effectiveRole = role == AIRole.Auto ? AIRole.DPS : role;
            situation.BestTarget = situation.HittableEnemies.Count > 0
                ? TargetScorer.SelectBestEnemy(situation.HittableEnemies, situation, effectiveRole)
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

        private void AnalyzePosition(Situation situation, BaseUnitEntity unit, TurnState turnState)
        {
            // ★ v3.8.80: continuation 턴 캐시 - 같은 유닛/라운드에서 이미 계산한 맵 재사용
            // 적은 아군 턴 도중 이동하지 않으므로 영향력/위협 맵은 동일
            string unitId = unit.UniqueId;
            int currentRound = turnState?.CombatRound ?? -1;
            bool isContinuation = turnState != null && turnState.ActionCount > 0
                                  && unitId == _cachedMapUnitId
                                  && currentRound == _cachedMapRound
                                  && _cachedInfluenceMap != null;

            if (isContinuation)
            {
                // 캐시된 맵 재사용
                situation.InfluenceMap = _cachedInfluenceMap;
                situation.PredictiveThreatMap = _cachedPredictiveThreatMap;
                if (Main.IsDebugEnabled)
                    Main.LogDebug($"[Analyzer] Continuation turn — reusing cached InfluenceMap/PredictiveThreatMap");
            }
            else
            {
                // ★ v3.2.00: 영향력 맵 계산 (위치 평가에 사용)
                situation.InfluenceMap = BattlefieldInfluenceMap.Compute(
                    situation.Enemies,
                    situation.Allies);
                if (Main.IsDebugEnabled && situation.InfluenceMap.IsValid)
                {
                    Main.LogDebug($"[Analyzer] {situation.InfluenceMap}");
                }

                // ★ v3.4.00: 예측적 위협 맵 계산 (적 이동 예측)
                bool usePredictiveMovement = situation.CharacterSettings?.UsePredictiveMovement ?? true;
                if (usePredictiveMovement && situation.Enemies.Count >= 2)
                {
                    var mobilities = EnemyMobilityAnalyzer.AnalyzeAllEnemies(situation.Enemies);
                    situation.PredictiveThreatMap = PredictiveThreatMap.Compute(
                        situation.Enemies,
                        mobilities,
                        situation.InfluenceMap);

                    if (Main.IsDebugEnabled && situation.PredictiveThreatMap.IsValid)
                    {
                        Main.LogDebug($"[Analyzer] PredictiveThreat: {situation.PredictiveThreatMap.EnemyCount} enemies analyzed, " +
                                      $"{situation.PredictiveThreatMap.PredictedSafeZones.Count} safe zones");
                    }
                }

                // 캐시 저장
                _cachedMapUnitId = unitId;
                _cachedMapRound = currentRound;
                _cachedInfluenceMap = situation.InfluenceMap;
                _cachedPredictiveThreatMap = situation.PredictiveThreatMap;
            }

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
                // ★ v3.5.98: AOE 반경 정보 추가 (타일 단위)
                var bp = ability.Blueprint;
                float aoERadius = CombatAPI.GetAoERadius(ability);  // 타일 단위
                string rangeInfo = bp?.Range.ToString() ?? "Unknown";
                if (aoERadius > 0)
                    rangeInfo += $" (AOE r={aoERadius:F0} tiles)";

                // ★ v3.7.30: 블루프린트 캐시에 자동 추가 + GUID 로깅
                Data.BlueprintCache.CacheAbility(ability);
                string guid = bp?.AssetGuid?.ToString() ?? "null";
                Main.LogDebug($"[Analyzer] Ability: {ability.Name} [{guid}] -> Timing={timing}, " +
                    $"CanTargetSelf={bp?.CanTargetSelf}, CanTargetFriends={bp?.CanTargetFriends}, " +
                    $"CanTargetEnemies={bp?.CanTargetEnemies}, CanTargetPoint={bp?.CanTargetPoint}, " +
                    $"Range={rangeInfo}, Weapon={ability.Weapon != null}");

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
                            if (!AllyStateCache.HasBuff(unit, ability))
                            {
                                situation.AvailableBuffs.Add(ability);
                                Main.LogDebug($"[Analyzer] SelfDamage available: {ability.Name} (HP={situation.HPPercent:F0}% >= {hpThreshold}%)");
                            }
                        }
                        break;

                    case AbilityTiming.PreCombatBuff:
                    case AbilityTiming.PreAttackBuff:
                        // 이미 활성화된 버프 제외
                        if (!AllyStateCache.HasBuff(unit, ability))
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
                        // ★ v3.7.30: AbilityMultiTarget 컴포넌트가 있으면 제외 (실명 공격 — 활공 등)
                        // MultiTarget 능력은 FamiliarAbilities에서만 처리됨
                        // ★ v3.8.62: BlueprintCache 캐시 사용 (GetComponent O(n) → O(1))
                        if (Data.BlueprintCache.IsMultiTarget(ability))
                        {
                            Main.LogDebug($"[Analyzer] Excluded MultiTarget from PositionalBuff: {ability.Name}");
                            break;
                        }
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
                        situation.AvailableAttacks.Add(ability);
                        break;

                    case AbilityTiming.GapCloser:
                        // ★ v3.7.29: AbilityMultiTarget 컴포넌트가 있으면 제외 (GUID 매칭보다 확실)
                        // MultiTarget 능력은 FamiliarAbilities에서만 처리됨
                        // ★ v3.8.62: BlueprintCache 캐시 사용
                        if (Data.BlueprintCache.IsMultiTarget(ability))
                        {
                            Main.LogDebug($"[Analyzer] Excluded MultiTarget ability (component): {ability.Name}");
                            break;
                        }
                        // ★ v3.7.27: 명시적 체크도 유지 (이름/GUID 기반)
                        if (FamiliarAbilities.IsMultiTargetFamiliarAbility(ability))
                        {
                            Main.LogDebug($"[Analyzer] Excluded MultiTarget familiar ability: {ability.Name}");
                            break;
                        }
                        situation.AvailableAttacks.Add(ability);
                        break;

                    // ★ v3.7.27: FamiliarOnly -> FamiliarAbilities에서만 처리 (AvailableAttacks에 추가 안 함)
                    case AbilityTiming.FamiliarOnly:
                        Main.LogDebug($"[Analyzer] FamiliarOnly ability skipped: {ability.Name}");
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
                            // ★ v3.7.29: MultiTarget 능력은 제외 (단일 타겟 경로로 계획되면 예외 발생)
                            // ★ v3.8.62: BlueprintCache 캐시 사용
                            if (Data.BlueprintCache.IsMultiTarget(ability))
                            {
                                Main.LogDebug($"[Analyzer] Excluded MultiTarget from default attacks: {ability.Name}");
                                break;
                            }
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

            // ★ v3.8.96: AoE 분류 — 모든 AvailableAttacks에서 AoE 타입 추출
            // CombatAPI.GetAttackCategory() + 기존 감지 API 활용
            // 8가지 AoE 타입 전부 감지: Point, Cone, Ray, Sector, Burst, Scatter, Self, Melee
            foreach (var attack in situation.AvailableAttacks)
            {
                var category = CombatAPI.GetAttackCategory(attack);

                bool isAoE = false;
                switch (category)
                {
                    case Data.AttackCategory.AoE:      // Point AoE, Pattern, Self-AoE
                    case Data.AttackCategory.Burst:    // 점사 (Burst Fire)
                    case Data.AttackCategory.Scatter:  // 산탄 (Scatter)
                        isAoE = true;
                        break;
                }

                // 카테고리 Normal이지만 실제 AoE인 경우 보강
                if (!isAoE)
                {
                    if (CombatAPI.IsPointTargetAbility(attack))
                        isAoE = true;
                    else if (AbilityDatabase.IsAoE(attack))
                        isAoE = true;
                    else if (CombatAPI.IsSelfTargetedAoEAttack(attack))
                        isAoE = true;
                    else if (CombatAPI.IsMeleeAoEAbility(attack))
                        isAoE = true;
                }

                if (isAoE)
                {
                    situation.AvailableAoEAttacks.Add(attack);
                }
            }

            // ★ v3.0.20: 분류 결과 요약 로깅
            // ★ v3.0.21: PositionalBuffs 추가
            // ★ v3.0.23: Stratagems 추가
            // ★ v3.0.33: Markers 추가
            // ★ v3.0.87: GapClosers 카운트 추가
            // ★ v3.8.96: AoE 카운트 추가
            var finalGapClosers = situation.AvailableAttacks.Where(a => AbilityDatabase.IsGapCloser(a)).ToList();
            Main.Log($"[Analyzer] {unit.CharacterName} abilities: " +
                $"Buffs={situation.AvailableBuffs.Count}, " +
                $"Heals={situation.AvailableHeals.Count}, " +
                $"Debuffs={situation.AvailableDebuffs.Count}, " +
                $"Attacks={situation.AvailableAttacks.Count}, " +
                $"AoE={situation.AvailableAoEAttacks.Count}, " +
                $"GapClosers={finalGapClosers.Count}, " +
                $"PositionalBuffs={situation.AvailablePositionalBuffs.Count}, " +
                $"Stratagems={situation.AvailableStratagems.Count}, " +
                $"Markers={situation.AvailableMarkers.Count}");

            // ★ v3.8.96: AoE 능력 상세 로깅
            if (situation.AvailableAoEAttacks.Count > 0)
            {
                foreach (var aoe in situation.AvailableAoEAttacks)
                {
                    var cat = CombatAPI.GetAttackCategory(aoe);
                    Main.LogDebug($"[Analyzer]   AoE: {aoe.Name} (Category={cat})");
                }
            }

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
            // ★ v3.1.29: IsInDanger=true면 위의 조건과 관계없이 후퇴 이동 허용
            if (turnState.HasPerformedFirstAction && situation.PrefersRanged)
            {
                // ★ v3.1.29: 위험 상황이면 재배치 허용 (공격 가능해도 후퇴 필요)
                if (situation.IsInDanger)
                {
                    // 위험 거리 내에 있으면 NeedsReposition 유지하여 후퇴 허용
                    Main.LogDebug($"[Analyzer] Ranged character in danger (dist={situation.NearestEnemyDistance:F1}m < {situation.MinSafeDistance}m) - allowing reposition for retreat");
                }
                // 공격 가능 적이 있거나, 이미 이동했거나, MP가 없으면 → 추가 이동 불필요
                // ★ v3.0.93: MP=0 체크 추가
                else if (situation.HasHittableEnemies || turnState.HasMovedThisTurn || situation.CurrentMP <= 0)
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

        /// <summary>
        /// ★ v3.7.00: 사역마 분석 (Overseer 아키타입 지원)
        /// </summary>
        private void AnalyzeFamiliar(Situation situation, BaseUnitEntity unit)
        {
            try
            {
                // 1. 이 유닛이 사역마인지 확인 (턴 스킵용)
                situation.IsFamiliarUnit = FamiliarAPI.IsFamiliar(unit);
                if (situation.IsFamiliarUnit)
                {
                    var master = FamiliarAPI.GetMaster(unit);
                    Main.LogDebug($"[Analyzer] {unit.CharacterName}: Is Familiar (Master={master?.CharacterName ?? "None"})");
                    return;  // 사역마는 추가 분석 불필요
                }

                // 2. 사역마 소유 여부 확인
                situation.HasFamiliar = FamiliarAPI.HasFamiliar(unit);
                if (!situation.HasFamiliar)
                {
                    return;  // 사역마 없음
                }

                // 3. 사역마 정보 수집
                situation.Familiar = FamiliarAPI.GetFamiliar(unit);
                situation.FamiliarType = FamiliarAPI.GetFamiliarType(unit);
                situation.FamiliarPosition = FamiliarAPI.GetFamiliarPosition(unit);

                if (situation.Familiar == null || !situation.FamiliarType.HasValue)
                {
                    situation.HasFamiliar = false;  // 유효하지 않은 사역마
                    return;
                }

                // 4. 사역마 의식 여부 확인
                if (!FamiliarAPI.IsFamiliarConscious(unit))
                {
                    Main.LogDebug($"[Analyzer] {unit.CharacterName}: Familiar {FamiliarAPI.GetFamiliarTypeName(situation.FamiliarType)} is unconscious");
                    // 의식 없으면 Reactivate만 가능
                    situation.FamiliarAbilities = FamiliarAbilities.CollectFamiliarAbilities(unit, situation.FamiliarType.Value)
                        .Where(a => FamiliarAbilities.IsReactivateAbility(a))
                        .ToList();
                    return;
                }

                // ★ v3.7.22: 순서 변경 - 먼저 능력 수집 후 Relocate 범위 확인
                // 5. 사역마 관련 능력 수집 (먼저!)
                situation.FamiliarAbilities = FamiliarAbilities.CollectFamiliarAbilities(
                    unit, situation.FamiliarType.Value);

                // 6. Relocate 능력 범위 확인 (최적 위치 계산에 사용)
                float relocateRangeMeters = 0f;
                var relocateAbility = situation.FamiliarAbilities
                    .FirstOrDefault(a => FamiliarAbilities.IsRelocateAbility(a, situation.FamiliarType));
                if (relocateAbility != null)
                {
                    // 타일 → 미터 변환 (1 타일 ≈ 1.35m)
                    int rangeTiles = GameInterface.CombatAPI.GetAbilityRangeInTiles(relocateAbility);
                    relocateRangeMeters = rangeTiles * CombatAPI.GridCellSize;
                    Main.LogDebug($"[Analyzer] Relocate range: {rangeTiles} tiles ({relocateRangeMeters:F1}m)");
                }

                // ★ v3.8.58: 아군 상태 캐시 갱신 (Raven/Servo-Skull 버프 확산 커버리지 정확 계산)
                if (situation.FamiliarType == PetType.Raven || situation.FamiliarType == PetType.ServoskullSwarm)
                {
                    AllyStateCache.Refresh(unit, situation.Allies);
                }

                // 7. 최적 위치 계산 (Relocate 범위 제한 적용)
                situation.OptimalFamiliarPosition = FamiliarPositioner.FindOptimalPosition(
                    unit,
                    situation.FamiliarType.Value,
                    situation.Allies,
                    situation.Enemies,
                    relocateRangeMeters);

                // 8. ★ v3.7.22: 현재 위치의 커버리지 계산 (ShouldRelocate에 전달)
                var validAllies = situation.Allies?.Where(a => a != null && a.IsConscious && !FamiliarAPI.IsFamiliar(a)).ToList()
                    ?? new List<BaseUnitEntity>();
                var validEnemies = situation.Enemies?.Where(e => e != null && e.IsConscious).ToList()
                    ?? new List<BaseUnitEntity>();
                int currentAlliesInRange = FamiliarAPI.CountAlliesInRadius(
                    situation.FamiliarPosition, FamiliarPositioner.EFFECT_RADIUS_TILES, validAllies);
                int currentEnemiesInRange = FamiliarAPI.CountEnemiesInRadius(
                    situation.FamiliarPosition, FamiliarPositioner.EFFECT_RADIUS_TILES, validEnemies);

                // 9. Relocate 필요 여부 판단 (커버리지 비교 기반)
                float currentDist = Vector3.Distance(
                    situation.FamiliarPosition,
                    situation.OptimalFamiliarPosition.Position);
                situation.NeedsFamiliarRelocate = FamiliarPositioner.ShouldRelocate(
                    situation.Familiar,
                    situation.OptimalFamiliarPosition,
                    currentDist,
                    currentAlliesInRange,
                    currentEnemiesInRange);

                // ★ v3.7.05: Relocate 필요하지만 능력이 쿨다운이면 false로 설정
                if (situation.NeedsFamiliarRelocate)
                {
                    if (relocateAbility == null)
                    {
                        situation.NeedsFamiliarRelocate = false;
                        Main.LogDebug($"[Analyzer] NeedsFamiliarRelocate=false: No relocate ability found");
                    }
                    else
                    {
                        // 쿨다운 체크
                        var unavailableReasons = relocateAbility.GetUnavailabilityReasons();
                        if (unavailableReasons != null && unavailableReasons.Any())
                        {
                            situation.NeedsFamiliarRelocate = false;
                            string reasons = string.Join(", ", unavailableReasons);
                            Main.LogDebug($"[Analyzer] NeedsFamiliarRelocate=false: Relocate unavailable ({reasons})");
                        }
                    }
                }

                // 10. 로깅
                var typeName = FamiliarAPI.GetFamiliarTypeName(situation.FamiliarType);
                Main.Log($"[Analyzer] {unit.CharacterName}: Has {typeName}, " +
                    $"Optimal=({situation.OptimalFamiliarPosition.AlliesInRange} allies, {situation.OptimalFamiliarPosition.EnemiesInRange} enemies), " +
                    $"NeedsRelocate={situation.NeedsFamiliarRelocate}, " +
                    $"FamiliarAbilities={situation.FamiliarAbilities.Count}");
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[Analyzer] AnalyzeFamiliar error: {ex.Message}");
                situation.HasFamiliar = false;
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

            // ★ v3.1.16: Point 타겟 AOE 공격 추가
            if (CombatAPI.IsPointTargetAbility(ability))
            {
                try
                {
                    var targetType = CombatAPI.GetAoETargetType(ability);
                    if (targetType == Kingmaker.UnitLogic.Abilities.Components.TargetType.Enemy ||
                        targetType == Kingmaker.UnitLogic.Abilities.Components.TargetType.Any)
                    {
                        // 적에게 해로운 AOE → 공격으로 분류
                        if (ability.Blueprint?.EffectOnEnemy == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityEffectOnUnit.Harmful)
                        {
                            Main.LogDebug($"[Analyzer] Point AOE attack detected: {ability.Name}");
                            return true;
                        }
                    }
                }
                catch (Exception ex) { Main.LogDebug($"[Analyzer] {ex.Message}"); }
            }

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

            // ★ v3.8.48: LINQ → CollectionHelper (0 할당, O(n))
            // Priority(int, 낮을수록 좋음) * 1000 + APCost(float)로 합산하여 MinBy 사용
            return CollectionHelper.MinByWhere(attacks,
                a => a.Weapon != null,
                a => CombatHelpers.GetAttackPriority(a, unit, bestTarget, situation.Enemies, situation.Allies) * 1000f
                    + CombatAPI.GetAbilityAPCost(a));
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
