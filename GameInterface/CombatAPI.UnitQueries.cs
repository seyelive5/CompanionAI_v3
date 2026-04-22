using System;
using System.Collections.Generic;
using Kingmaker;
using Kingmaker.Blueprints.Root;              // ProgressionRoot (Archetype)
using Kingmaker.Controllers.TurnBased;        // Initiative (IsExtraTurn)
using Kingmaker.EntitySystem;                  // EntityHelper.DistanceToInCells (GetDistance)
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Stats.Base;      // StatType
using Kingmaker.RuleSystem;                   // Rulebook
using Kingmaker.RuleSystem.Rules;             // RuleCalculateDodgeChance, RuleCalculateParryChance
using Kingmaker.UnitLogic.Abilities;          // AbilityData
using Kingmaker.UnitLogic.Progression.Paths;  // BlueprintCareerPath (Archetype)
using Kingmaker.UnitLogic.Squads;             // PartSquadExtension.GetSquadOptional (IsExtraTurn)
using Kingmaker.View.Covers;                  // LosCalculations.CoverType

namespace CompanionAI_v3.GameInterface
{
    public static partial class CombatAPI
    {
        #region Unit State

        /// <summary>
        /// HP 퍼센트 반환
        /// ★ v3.0.1: GetActualHP/GetActualMaxHP 기반으로 통합
        /// </summary>
        public static float GetHPPercent(BaseUnitEntity unit)
        {
            if (unit == null) return 0f;
            try
            {
                int current = GetActualHP(unit);
                int max = GetActualMaxHP(unit);
                if (max <= 0) return 100f;
                return (float)current / max * 100f;
            }
            // ★ v3.13.0: 안전한 기본값 — 0f (부상으로 판단 → 방어적 행동 유도)
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetHPPercent failed for {unit?.CharacterName}: {ex.Message}");
                return 0f;
            }
        }

        /// <summary>
        /// ★ v3.0.13 Fix: AP/MP 수정
        /// Yellow = Action Points (스킬/공격용)
        /// Blue = Movement Points (이동용)
        /// </summary>
        public static float GetCurrentAP(BaseUnitEntity unit)
        {
            if (unit == null) return 0f;
            try
            {
                // ★ Yellow Action Points = 액션 포인트 (능력/공격)
                // ★ v3.13.0: ?? 0f (기존 3f → AP 없으면 EndTurn이 안전)
                return unit.CombatState?.ActionPointsYellow ?? 0f;
            }
            // ★ v3.13.0: 안전한 기본값 — 0f (AP 없음 → EndTurn)
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetCurrentAP failed for {unit?.CharacterName}: {ex.Message}");
                return 0f;
            }
        }

        /// <summary>
        /// ★ v3.0.13 Fix: AP/MP 수정
        /// Blue = Movement Points (이동용)
        /// </summary>
        public static float GetCurrentMP(BaseUnitEntity unit)
        {
            if (unit == null) return 0f;
            try
            {
                // ★ Blue Action Points = 이동 포인트 (Movement Points)
                return unit.CombatState?.ActionPointsBlue ?? 0f;
            }
            // ★ v3.13.0: 로깅 추가 (기본값 0f는 이미 안전)
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetCurrentMP failed for {unit?.CharacterName}: {ex.Message}");
                return 0f;
            }
        }

        /// <summary>
        /// ★ v3.111.18 Phase C.4: 적별 threat range 턴별 캐시.
        ///   reflection 호출 (AiCollectedDataStorage + weapon blueprint)이 비싸서
        ///   EvaluatePosition이 tile × enemies 반복 호출 시 3,200회/scan 핫스팟.
        ///   threat range는 한 턴 동안 불변(무기/학습 데이터 턴 중 안 바뀜) → 캐시 안전.
        ///   무효화: CombatCache.ClearAll() (턴 시작).
        /// </summary>
        private static readonly Dictionary<BaseUnitEntity, int> _enemyThreatRangeCache
            = new Dictionary<BaseUnitEntity, int>();

        /// <summary>★ v3.111.18: 턴 시작 시 CombatCache.ClearAll()에서 호출.</summary>
        public static void ClearEnemyThreatRangeCache() => _enemyThreatRangeCache.Clear();

        /// <summary>
        /// ★ v3.110.20: 적의 위협 사거리 (타일 단위).
        /// 게임 AI 학습 데이터 (GetThreatRange) + 현재 장비 무기 사거리 중 큰 값.
        /// 게임 학습이 없는 신규 유닛은 무기 사거리로 폴백.
        /// 게임 패턴: AttackEffectivenessTileScorer.CalculateEnemyTargetThreatScore
        /// ★ v3.111.18 Phase C.4: 턴별 캐시 적용.
        /// </summary>
        public static int GetEnemyThreatRangeInTiles(BaseUnitEntity enemy)
        {
            if (enemy == null) return 0;

            // ★ v3.111.18: 턴 내 캐시 체크
            if (_enemyThreatRangeCache.TryGetValue(enemy, out int cached))
                return cached;

            int learnedRange = 0;
            try
            {
                var dataStorage = Kingmaker.Game.Instance?.Player?.AiCollectedDataStorage;
                if (dataStorage != null)
                {
                    var unitData = dataStorage[enemy];
                    if (unitData != null && unitData.AttackDataCollection != null)
                        learnedRange = unitData.AttackDataCollection.GetThreatRange();
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled)
                    Main.LogWarning($"[CombatAPI] GetEnemyThreatRange learned failed for {enemy?.CharacterName}: {ex.Message}");
            }

            int weaponRange = 0;
            try
            {
                var weapon = enemy.GetFirstWeapon();
                if (weapon != null && weapon.Blueprint != null)
                    weaponRange = weapon.Blueprint.AttackRange;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled)
                    Main.LogWarning($"[CombatAPI] GetEnemyThreatRange weapon failed for {enemy?.CharacterName}: {ex.Message}");
            }

            int result = System.Math.Max(learnedRange, weaponRange);
            _enemyThreatRangeCache[enemy] = result;
            return result;
        }

        /// <summary>
        /// ★ v3.110.20: 적이 이 턴에 특정 위치를 공격 가능한 확률 (0 / 0.5 / 1).
        /// 게임 패턴: threatRange + AP_Blue 기준.
        ///   dist ≤ threatRange        → 1.0 (즉시 공격 가능)
        ///   dist ≤ threatRange + AP   → 0.5 (이동 후 공격 가능)
        ///   dist > threatRange + AP   → 0   (이 턴 안전)
        /// 참조: AttackEffectivenessTileScorer.CalculateEnemyTargetThreatScore (decompile 195-215)
        /// </summary>
        public static float GetEnemyTurnThreatScore(BaseUnitEntity enemy, UnityEngine.Vector3 targetPos)
        {
            if (enemy == null) return 0f;

            int threatRange = GetEnemyThreatRangeInTiles(enemy);
            float apBlue = 0f;
            try
            {
                apBlue = enemy.CombatState?.ActionPointsBlue ?? 0f;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled)
                    Main.LogWarning($"[CombatAPI] GetEnemyTurnThreatScore AP read failed for {enemy?.CharacterName}: {ex.Message}");
            }

            int distCells = (int)System.Math.Ceiling(GetDistanceInTiles(targetPos, enemy));
            if (distCells <= threatRange) return 1.0f;
            if (distCells <= threatRange + apBlue) return 0.5f;
            return 0f;
        }

        // ★ v3.110.21 Phase 3: UnitPartPriorityTarget 리플렉션 캐시.
        // m_PriorityTargets가 private이라 FieldInfo 캐싱 필수.
        private static System.Reflection.FieldInfo _priorityTargetsField;
        private static bool _priorityTargetsFieldLookupAttempted;

        /// <summary>
        /// ★ v3.110.21: 이 타겟이 공격자의 "우선 공격 대상" 여부.
        /// 도발/마크/겨냥 능력으로 UnitPartPriorityTarget.AddTarget된 Buff 리스트 순회.
        /// Buff.Owner == target이면 priority target.
        ///
        /// 게임 API: UnitPartPriorityTarget.GetPriorityTarget(BlueprintBuff)은 forward 전용.
        /// 역방향 조회 ("이 타겟이 우선인가") 위해 m_PriorityTargets 리플렉션 접근.
        /// FieldInfo 캐시로 성능 부담 최소화 (조회 1회당 ~마이크로초).
        /// </summary>
        public static bool IsPriorityTargetFor(BaseUnitEntity target, BaseUnitEntity attacker)
        {
            if (target == null || attacker == null) return false;

            try
            {
                var priorityPart = attacker.GetOptional<Kingmaker.UnitLogic.Parts.UnitPartPriorityTarget>();
                if (priorityPart == null) return false;

                // FieldInfo 1회 lookup + 캐싱
                if (!_priorityTargetsFieldLookupAttempted)
                {
                    _priorityTargetsFieldLookupAttempted = true;
                    _priorityTargetsField = typeof(Kingmaker.UnitLogic.Parts.UnitPartPriorityTarget)
                        .GetField("m_PriorityTargets",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (_priorityTargetsField == null)
                    {
                        Main.LogWarning("[CombatAPI] IsPriorityTargetFor: m_PriorityTargets field not found via reflection. Priority target detection disabled.");
                    }
                }

                if (_priorityTargetsField == null) return false;

                var typedList = _priorityTargetsField.GetValue(priorityPart)
                    as System.Collections.Generic.List<Kingmaker.EntitySystem.EntityFactRef<Kingmaker.UnitLogic.Buffs.Buff>>;
                if (typedList == null) return false;

                foreach (var entityFactRef in typedList)
                {
                    var buff = entityFactRef.Fact;
                    if (buff?.Owner == target) return true;
                }
            }
            catch (System.Exception ex)
            {
                if (Main.IsDebugEnabled)
                    Main.LogWarning($"[CombatAPI] IsPriorityTargetFor reflection failed: {ex.Message}");
            }

            return false;
        }

        public static bool CanMove(BaseUnitEntity unit)
        {
            if (unit == null) return false;
            try { return unit.State.CanMove; }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CanMove failed for {unit?.CharacterName}: {ex.Message}");
                return false;
            }
        }

        public static bool CanAct(BaseUnitEntity unit)
        {
            if (unit == null) return false;
            try { return unit.State.CanActInTurnBased; }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CanAct failed for {unit?.CharacterName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.111.14: 능력 표시명 안전 조회 — LocalizedString 예외 격리.
        /// ability.Name(대문자 N)은 LocalizedString 경유 → 번역 key 누락/깨진 asset reference 시 예외.
        /// bp.name(소문자 n)은 Unity ScriptableObject 내부 이름 → 번역 비경유, 항상 안전.
        /// 로그/디버그 문자열 interpolation에서 사용 (매칭 용도 아님 — 매칭은 GUID 기반 유지).
        /// </summary>
        public static string GetAbilityDisplayName(AbilityData ability)
        {
            if (ability == null) return "null";
            try
            {
                var name = ability.Name;
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch { /* LocalizedString 예외 → fallback */ }

            try
            {
                var bp = ability.Blueprint;
                return bp?.name ?? "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }

        /// <summary>
        /// ★ v3.111.12: 게임 canonical API 기반 ExtraTurn(임시턴) 감지.
        /// 디컴파일 참조: TurnController.GetInterruptingOrder (private static helper).
        ///   - 일반 유닛: unit.Initiative.InterruptingOrder > 0
        ///   - Squad 유닛: squad.Initiative.InterruptingOrder > 0 (companions는 squad 아니지만 safety)
        /// 게임이 TurnOrderQueue.InterruptCurrentUnit에서 셋업, TurnController.EndUnitTurn에서 0 리셋.
        /// v3.111.8 ~ 10의 Harmony hybrid를 대체 — 결정적, 즉시성, 레이싱 없음.
        /// </summary>
        public static bool IsExtraTurn(BaseUnitEntity unit)
        {
            if (unit == null) return false;
            try
            {
                if (unit.Initiative == null) return false;

                // Squad 경로 (defense-in-depth — companions는 not-in-squad지만 enemy mob에 섞일 가능성)
                if (unit.IsInSquad)
                {
                    var squadPart = unit.GetSquadOptional();
                    var squad = squadPart?.Squad;
                    return squad?.Initiative != null && squad.Initiative.InterruptingOrder > 0;
                }

                return unit.Initiative.InterruptingOrder > 0;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsExtraTurn failed for {unit?.CharacterName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.0.10: 명령 큐가 비어있는지 확인 (이전 명령 완료 여부)
        /// 게임의 TaskNodeWaitCommandsDone과 동일한 체크
        /// </summary>
        public static bool IsCommandQueueEmpty(BaseUnitEntity unit)
        {
            if (unit == null) return true;
            try
            {
                return unit.Commands.Empty;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsCommandQueueEmpty failed for {unit?.CharacterName}: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// ★ v3.0.10: 유닛이 다음 행동을 할 준비가 되었는지 확인
        /// Commands.Empty && CanActInTurnBased
        /// </summary>
        public static bool IsReadyForNextAction(BaseUnitEntity unit)
        {
            if (unit == null) return false;
            try
            {
                return unit.Commands.Empty && unit.State.CanActInTurnBased;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsReadyForNextAction failed for {unit?.CharacterName}: {ex.Message}");
                return false;
            }
        }

        public static float GetDistance(BaseUnitEntity from, BaseUnitEntity to)
        {
            if (from == null || to == null) return float.MaxValue;
            try
            {
                // ★ v3.8.66: 게임 API 기반 (SizeRect 반영) — 미터 단위
                return (float)from.DistanceToInCells(to) * GridCellSize;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetDistance failed: {ex.Message}");
                return float.MaxValue;
            }
        }

        #endregion

        #region Unit Lists

        public static List<BaseUnitEntity> GetEnemies(BaseUnitEntity unit)
        {
            var enemies = new List<BaseUnitEntity>();
            if (unit == null) return enemies;

            try
            {
                // ★ v3.9.40: IsInCombat 필터로 현재 전투 참가자만 포함
                // 기존: AllBaseAwakeUnits 전체 → 맵 전체의 모든 적 포함 (비전투 적까지 타겟팅)
                // 수정: IsInCombat 플래그로 현재 전투에 참가 중인 유닛만 필터링
                var allUnits = Game.Instance?.State?.AllBaseAwakeUnits;
                if (allUnits == null) return enemies;

                bool inTurnBasedCombat = Game.Instance?.TurnController?.TurnBasedModeActive == true;
                int skippedNonCombat = 0;

                foreach (var other in allUnits)
                {
                    if (other == null || other == unit) continue;
                    if (other.LifeState.IsDead) continue;

                    // ★ v3.9.40: 턴제 전투 중이면 전투 참가자만 포함
                    if (inTurnBasedCombat && !other.IsInCombat)
                    {
                        skippedNonCombat++;
                        continue;
                    }

                    bool isEnemy = (unit.IsPlayerFaction && other.IsPlayerEnemy) ||
                                   (!unit.IsPlayerFaction && !other.IsPlayerEnemy);

                    if (isEnemy)
                    {
                        enemies.Add(other);
                    }
                }

                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetEnemies: {enemies.Count} enemies (filtered {skippedNonCombat} non-combat units)");
            }
            catch (Exception ex)
            {
                // ★ v3.4.01: P1-2 예외 상세 로깅
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetEnemies error: {ex.Message}");
            }

            return enemies;
        }

        public static List<BaseUnitEntity> GetAllies(BaseUnitEntity unit)
        {
            var allies = new List<BaseUnitEntity>();
            if (unit == null) return allies;

            try
            {
                var allUnits = Game.Instance?.State?.AllBaseAwakeUnits;
                if (allUnits == null) return allies;

                foreach (var other in allUnits)
                {
                    if (other == null || other == unit) continue;
                    if (other.LifeState.IsDead) continue;

                    // 아군 판별
                    bool isAlly = unit.IsPlayerFaction == other.IsPlayerFaction;

                    if (isAlly)
                    {
                        allies.Add(other);
                    }
                }
            }
            catch (Exception ex)
            {
                // ★ v3.4.01: P1-2 예외 상세 로깅
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAllies error: {ex.Message}");
            }

            return allies;
        }

        #endregion

        #region Unit Stat Query API (v3.26.0)

        // ─── ★ v3.26.0: 유닛 스탯 조회 API ──────────────────────────────────
        // 적/아군 동일 API — BaseUnitEntity.GetStatOptional()

        /// <summary>스탯 최종값 (모든 버프/디버프 적용 후)</summary>
        public static int GetStatValue(BaseUnitEntity unit, StatType stat)
        {
            try
            {
                return unit?.GetStatOptional(stat)?.ModifiedValue ?? 0;
            }
            catch { return 0; }
        }

        /// <summary>방어구 흡수값 (장비 + 스탯 보너스)</summary>
        public static int GetArmorAbsorption(BaseUnitEntity unit)
        {
            try
            {
                int equipArmor = unit?.Body?.Armor?.MaybeArmor?.Blueprint?.DamageAbsorption ?? 0;
                int statBonus = GetStatValue(unit, StatType.DamageAbsorption);
                return equipArmor + statBonus;
            }
            catch { return 0; }
        }

        /// <summary>편향값 (장비 + 스탯)</summary>
        public static int GetDeflection(BaseUnitEntity unit)
        {
            try
            {
                int equipDeflect = unit?.Body?.Armor?.MaybeArmor?.Blueprint?.DamageDeflection ?? 0;
                int statBonus = GetStatValue(unit, StatType.DamageDeflection);
                return equipDeflect + statBonus;
            }
            catch { return 0; }
        }

        /// <summary>
        /// ★ v3.26.0: CC 저항력 추정 (0-100)
        /// 높을수록 CC에 강함. Toughness + Willpower 기반 간이 추정.
        /// </summary>
        public static float EstimateCCResistance(BaseUnitEntity target)
        {
            try
            {
                int tgh = GetStatValue(target, StatType.WarhammerToughness);
                int wp = GetStatValue(target, StatType.WarhammerWillpower);
                int dominantStat = Math.Max(tgh, wp);
                float resistance = Math.Min(95f, 30f + dominantStat);
                return resistance;
            }
            catch { return 50f; }
        }

        #endregion

        #region Dodge/Parry Estimation (v3.26.0)

        // ─── ★ v3.26.0: Dodge/Parry 추정 → Effective Hit Chance ─────────────

        /// <summary>
        /// Dodge 확률 추정 (RuleCalculateDodgeChance 트리거)
        /// 디컴파일 확인: 계산 전용 Rule (사이드이펙트 없음)
        /// </summary>
        public static int EstimateDodgeChance(BaseUnitEntity target, BaseUnitEntity attacker, AbilityData ability)
        {
            try
            {
                var targetUnit = target as UnitEntity;
                if (targetUnit == null) return 0;

                var dodgeRule = new RuleCalculateDodgeChance(
                    targetUnit,
                    attacker,    // MechanicEntity (BaseUnitEntity 상속)
                    ability,
                    LosCalculations.CoverType.None,
                    0            // burstIndex
                );
                Rulebook.Trigger(dodgeRule);
                return dodgeRule.Result;  // 0-95 (게임 자동 클램핑)
            }
            catch
            {
                return EstimateDodgeFromStats(target, attacker);
            }
        }

        /// <summary>스탯 기반 Dodge 폴백 추정</summary>
        private static int EstimateDodgeFromStats(BaseUnitEntity target, BaseUnitEntity attacker)
        {
            int targetAgi = GetStatValue(target, StatType.WarhammerAgility);
            int attackerPer = attacker != null ? GetStatValue(attacker, StatType.WarhammerPerception) : 0;
            int dodge = 30 + targetAgi - attackerPer / 2;
            return Math.Max(0, Math.Min(95, dodge));
        }

        /// <summary>
        /// Parry 확률 추정 (근접 전용, RuleCalculateParryChance 트리거)
        /// </summary>
        public static int EstimateParryChance(BaseUnitEntity target, BaseUnitEntity attacker, AbilityData ability)
        {
            try
            {
                if (ability == null || !ability.IsMelee) return 0;

                var targetUnit = target as UnitEntity;
                if (targetUnit == null) return 0;

                var parryRule = new RuleCalculateParryChance(
                    targetUnit,
                    attacker,    // MechanicEntity
                    ability,
                    0,           // resultSuperiorityNumber
                    false,       // isRangedParry
                    0            // attackerWeaponSkillOverride
                );
                Rulebook.Trigger(parryRule);
                return parryRule.Result;  // 0-95
            }
            catch
            {
                return EstimateParryFromStats(target, attacker, ability);
            }
        }

        /// <summary>스탯 기반 Parry 폴백 추정</summary>
        private static int EstimateParryFromStats(BaseUnitEntity target, BaseUnitEntity attacker, AbilityData ability)
        {
            if (ability == null || !ability.IsMelee) return 0;
            int targetWS = GetStatValue(target, StatType.WarhammerWeaponSkill);
            int attackerWS = attacker != null ? GetStatValue(attacker, StatType.WarhammerWeaponSkill) : 0;
            int parry = 20 + targetWS - attackerWS;
            return Math.Max(0, Math.Min(95, parry));
        }

        /// <summary>
        /// 실질 명중률 계산 (BS × (1-Dodge) × (1-Parry))
        /// </summary>
        private static int CalculateEffectiveHitChance(int rawHitChance, int dodgeChance, int parryChance)
        {
            float effective = rawHitChance / 100f;
            effective *= (1f - dodgeChance / 100f);
            effective *= (1f - parryChance / 100f);
            return Math.Max(1, Math.Min(95, (int)(effective * 100f)));
        }

        #endregion

        #region Archetype Detection API (v3.28.0)

        // ─── ★ v3.28.0: 유닛 아키타입 감지 ─────────────────────────────────
        // ProgressionRoot.CareerPaths + GetPathRank()로 주 아키타입 감지

        /// <summary>유닛 아키타입 열거형</summary>
        public enum UnitArchetype
        {
            Unknown, Officer, Operative, ArchMilitant,
            Soldier, Assassin, Psyker, Navigator
        }

        // 아키타입 캐시 (유닛별, 전투 중 변경 없음)
        private static readonly Dictionary<string, UnitArchetype> _archetypeCache = new Dictionary<string, UnitArchetype>();

        /// <summary>아키타입 캐시 클리어 (전투 시작 시)</summary>
        public static void ClearArchetypeCache() => _archetypeCache.Clear();

        /// <summary>
        /// 유닛의 주 아키타입 감지 (캐시됨)
        /// ProgressionRoot.CareerPaths에서 가장 높은 PathRank를 가진 경로의 이름으로 판정
        /// </summary>
        public static UnitArchetype DetectArchetype(BaseUnitEntity unit)
        {
            if (unit == null) return UnitArchetype.Unknown;

            string unitId = unit.UniqueId;
            if (_archetypeCache.TryGetValue(unitId, out var cached))
                return cached;

            try
            {
                var progression = ProgressionRoot.Instance;
                if (progression == null)
                {
                    _archetypeCache[unitId] = UnitArchetype.Unknown;
                    return UnitArchetype.Unknown;
                }

                BlueprintCareerPath bestPath = null;
                int maxRank = 0;

                foreach (var cp in progression.CareerPaths)
                {
                    if (cp == null) continue;
                    int rank = unit.Progression.GetPathRank(cp);
                    if (rank > maxRank)
                    {
                        maxRank = rank;
                        bestPath = cp;
                    }
                }

                UnitArchetype result = UnitArchetype.Unknown;
                if (bestPath != null)
                {
                    string pathName = bestPath.name?.ToLowerInvariant() ?? "";
                    if (pathName.Contains("officer")) result = UnitArchetype.Officer;
                    else if (pathName.Contains("operative")) result = UnitArchetype.Operative;
                    else if (pathName.Contains("militant")) result = UnitArchetype.ArchMilitant;
                    else if (pathName.Contains("soldier")) result = UnitArchetype.Soldier;
                    else if (pathName.Contains("assassin")) result = UnitArchetype.Assassin;
                    else if (pathName.Contains("psyker")) result = UnitArchetype.Psyker;
                    else if (pathName.Contains("navigator")) result = UnitArchetype.Navigator;

                    if (Main.IsDebugEnabled && result != UnitArchetype.Unknown)
                        Main.LogDebug($"[CombatAPI] DetectArchetype({unit.CharacterName}): {result} (path={bestPath.name}, rank={maxRank})");
                }

                _archetypeCache[unitId] = result;
                return result;
            }
            catch
            {
                _archetypeCache[unitId] = UnitArchetype.Unknown;
                return UnitArchetype.Unknown;
            }
        }

        #endregion
    }
}
