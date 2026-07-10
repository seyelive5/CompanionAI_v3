using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.Blueprints;                    // GetComponent extension
using Kingmaker.Blueprints.Root;              // ProgressionRoot (Archetype)
using Kingmaker.Controllers.TurnBased;        // Initiative (IsExtraTurn)
using Kingmaker.EntitySystem;                  // EntityHelper.DistanceToInCells (GetDistance)
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Stats.Base;      // StatType
using Kingmaker.RuleSystem;                   // Rulebook
using Kingmaker.RuleSystem.Rules;             // RuleCalculateDodgeChance, RuleCalculateParryChance
using Kingmaker.UnitLogic.Abilities;          // AbilityData
using Kingmaker.UnitLogic.Abilities.Components;  // AbilityDeliverChain, TargetType
using Kingmaker.UnitLogic.Mechanics.Actions;     // ContextActionCastSpell
using Kingmaker.UnitLogic.Progression.Paths;  // BlueprintCareerPath (Archetype)
using Kingmaker.UnitLogic.Squads;             // PartSquadExtension.GetSquadOptional (IsExtraTurn)
using Kingmaker.View.Covers;                  // LosCalculations.CoverType
using Pathfinding;                             // GraphNode (EnemyMoveCache)
using Kingmaker.Pathfinding;                   // CustomGridNodeBase (Vector3Position)
using CompanionAI_v3.Logging;

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
                Log.Engine.Warn($"[CombatAPI] GetHPPercent failed for {unit?.CharacterName}: {ex.Message}");
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
                Log.Engine.Warn($"[CombatAPI] GetCurrentAP failed for {unit?.CharacterName}: {ex.Message}");
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
                Log.Engine.Warn($"[CombatAPI] GetCurrentMP failed for {unit?.CharacterName}: {ex.Message}");
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
                    Log.Engine.Warn($"[CombatAPI] GetEnemyThreatRange learned failed for {enemy?.CharacterName}: {ex.Message}");
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
                    Log.Engine.Warn($"[CombatAPI] GetEnemyThreatRange weapon failed for {enemy?.CharacterName}: {ex.Message}");
            }

            int result = System.Math.Max(learnedRange, weaponRange);
            _enemyThreatRangeCache[enemy] = result;
            return result;
        }

        /// <summary>
        /// 적이 다음 턴에 targetPos 를 위협할 수 있는지 점수화 (0 / 0.5 / 1).
        ///
        /// 1순위 (정확): EnemyMoveCache — 게임의 AsyncUpdateEnemyMoveVariants 가
        ///   사전 계산한 도달 가능 노드 리스트. 그 어떤 노드에서도 무기 사거리 안에
        ///   targetPos 가 들어오면 위협 1.0, 못 들어오면 0.
        ///
        /// 2순위 (폴백 — 캐시 미스): 게임 자체 ProtectionTileScorer 공식 모방.
        ///   다음 턴 시작 시 받을 MP (WarhammerInitialAPBlue.ModifiedValue) ÷
        ///   타일당 MP 비용 (WarhammerMovementApPerCell) → 도달 가능 타일 수.
        ///   현재 잔여 ActionPointsBlue 가 아님 — 턴제 게임에서 잔여값은 의미 없음.
        ///
        /// 이전 버그: ActionPointsBlue (현재 잔여) 를 타일처럼 더해서 사용 →
        ///   적이 자기 턴 끝낸 후엔 항상 위협 0 으로 평가 → 다음 턴 둘러싸일 위치를
        ///   "안전" 으로 평가하는 결정적 결함. 실세션 검증 (Argenta Hide=6.7 케이스).
        /// </summary>
        public static float GetEnemyTurnThreatScore(BaseUnitEntity enemy, UnityEngine.Vector3 targetPos)
        {
            if (enemy == null) return 0f;
            int threatRange = GetEnemyThreatRangeInTiles(enemy);

            // 1순위: 게임이 사전 계산한 도달 가능 노드 (정답 데이터)
            var moveVariants = EnemyMoveCache.Get(enemy);
            if (moveVariants != null && moveVariants.Count > 0)
            {
                foreach (var node in moveVariants)
                {
                    if (node == null) continue;
                    UnityEngine.Vector3 nodePos;
                    if (node is Kingmaker.Pathfinding.CustomGridNodeBase custom)
                        nodePos = custom.Vector3Position;
                    else
                        nodePos = (UnityEngine.Vector3)node.position;

                    float dist = GetDistanceInTiles(targetPos, nodePos);
                    if (dist <= threatRange) return 1.0f;
                }
                return 0f;
            }

            // 2순위 폴백: 다음 턴 시작 MP 기준 (게임 ProtectionTileScorer 공식)
            float maxMP = 0f;
            float costPerCell = 1f;
            try
            {
                var initialAP = enemy.CombatState?.WarhammerInitialAPBlue;
                if (initialAP != null) maxMP = (float)initialAP.ModifiedValue;
                else if (enemy.Blueprint != null) maxMP = (float)enemy.Blueprint.WarhammerInitialAPBlue;

                if (enemy.Blueprint != null)
                    costPerCell = enemy.Blueprint.WarhammerMovementApPerCell;
            }
            catch (Exception ex)
            {
                Log.Engine.Error(ex, $"[CombatAPI] GetEnemyTurnThreatScore stat read failed for {enemy?.CharacterName}");
            }

            int reachTiles = (int)(maxMP / UnityEngine.Mathf.Max(1f, costPerCell));
            int distCells = (int)System.Math.Ceiling(GetDistanceInTiles(targetPos, enemy));
            if (distCells <= threatRange) return 1.0f;
            if (distCells <= threatRange + reachTiles) return 0.5f;
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
                        Log.Engine.Warn("[CombatAPI] IsPriorityTargetFor: m_PriorityTargets field not found via reflection. Priority target detection disabled.");
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
                    Log.Engine.Warn($"[CombatAPI] IsPriorityTargetFor reflection failed: {ex.Message}");
            }

            return false;
        }

        public static bool CanMove(BaseUnitEntity unit)
        {
            if (unit == null) return false;
            try { return unit.State.CanMove; }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Log.Engine.Error(ex, $"[CombatAPI] CanMove failed for {unit?.CharacterName}");
                return false;
            }
        }

        public static bool CanAct(BaseUnitEntity unit)
        {
            if (unit == null) return false;
            try { return unit.State.CanActInTurnBased; }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Log.Engine.Error(ex, $"[CombatAPI] CanAct failed for {unit?.CharacterName}");
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
                if (Main.IsDebugEnabled) Log.Engine.Error(ex, $"[CombatAPI] IsExtraTurn failed for {unit?.CharacterName}");
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
                if (Main.IsDebugEnabled) Log.Engine.Error(ex, $"[CombatAPI] IsCommandQueueEmpty failed for {unit?.CharacterName}");
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
                if (Main.IsDebugEnabled) Log.Engine.Error(ex, $"[CombatAPI] IsReadyForNextAction failed for {unit?.CharacterName}");
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
                if (Main.IsDebugEnabled) Log.Engine.Error(ex, $"[CombatAPI] GetDistance failed");
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

                if (Main.IsDebugEnabled) Log.Engine.Debug($"[CombatAPI] GetEnemies: {enemies.Count} enemies (filtered {skippedNonCombat} non-combat units)");
            }
            catch (Exception ex)
            {
                // ★ v3.4.01: P1-2 예외 상세 로깅
                if (Main.IsDebugEnabled) Log.Engine.Error(ex, $"[CombatAPI] GetEnemies error");
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
                if (Main.IsDebugEnabled) Log.Engine.Error(ex, $"[CombatAPI] GetAllies error");
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
                        Log.Engine.Debug($"[CombatAPI] DetectArchetype({unit.CharacterName}): {result} (path={bestPath.name}, rank={maxRank})");
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

        #region Chain-Trigger Weapon Detection (v3.117.30)

        /// <summary>
        /// v3.117.30: Ability 의 OnHit 액션에서 chain trigger ability 검출 (component-based).
        ///
        /// 게임 native 메커니즘 (디컴파일 검증):
        ///   AbilityData.AdditionalEffects (yields BlueprintAbilityAdditionalEffect — weapon.OnHitActions + weaponAbility.OnHitActions)
        ///   → BlueprintAbilityAdditionalEffect.OnHitActions.Actions (ActionList)
        ///   → ContextActionCastSpell.Spell (BlueprintAbility) — 자동 트리거 ability
        ///   → BlueprintAbility.GetComponent&lt;AbilityDeliverChain&gt;() — chain delivery
        ///
        /// 무기 예시: ArcRifleT2_Item.OnHitActions = [ContextActionCastSpell(Spell=ArcRifleT2Chain_Ability)]
        /// 검출 시 chain 의 Radius (셀), TargetType (Enemy/Ally/Any), TargetsCount 추출.
        ///
        /// 반환 true: chain trigger 검출. radius/targetType/maxChain 채워짐.
        /// 반환 false: chain 없음 또는 오류.
        /// </summary>
        // v3.117.34: chain detect 로그 throttling — ability GUID 별 1회만 출력 (이전: 매 호출마다)
        private static readonly HashSet<string> _loggedChainAbilities = new HashSet<string>();
        private static readonly HashSet<string> _loggedNonChainSpells = new HashSet<string>();

        public static bool TryGetWeaponOnHitChain(
            AbilityData ability,
            out int radius, out TargetType targetType, out int maxChain)
        {
            radius = 0;
            targetType = TargetType.Enemy;
            maxChain = 0;

            if (ability == null) return false;

            try
            {
                foreach (var addEffect in ability.AdditionalEffects)
                {
                    if (addEffect?.OnHitActions?.Actions == null) continue;

                    foreach (var action in addEffect.OnHitActions.Actions)
                    {
                        var castSpell = action as ContextActionCastSpell;
                        if (castSpell?.Spell == null) continue;

                        // 1. AbilityDeliverChain (Psyker ChainLightning 류)
                        var chain = castSpell.Spell.GetComponent<AbilityDeliverChain>();
                        if (chain != null)
                        {
                            radius = chain.Radius;
                            targetType = chain.TargetType;
                            try
                            {
                                int countVal = chain.TargetsCount?.Value ?? 0;
                                maxChain = countVal > 0 ? countVal : 4;
                            }
                            catch { maxChain = 4; }

                            if (Main.IsDebugEnabled)
                            {
                                string key = ability.Blueprint?.AssetGuid?.ToString() ?? ability.Name;
                                if (_loggedChainAbilities.Add(key))
                                    Log.Engine.Debug($"[ChainDetect] DeliverChain: {ability.Name} → {castSpell.Spell.name} (radius={radius}, type={targetType}, max={maxChain})");
                            }
                            return true;
                        }

                        // 2. v3.117.33: AbilityTargetsInPatternTrail (Arc 무기 류 — 패턴 안 모든 타겟)
                        var trail = castSpell.Spell.GetComponent<AbilityTargetsInPatternTrail>();
                        if (trail != null)
                        {
                            int trailRadius = 0;
                            try { trailRadius = trail.Pattern?.Radius ?? 0; } catch { }
                            radius = trailRadius > 0 ? trailRadius : 5;
                            targetType = trail.Targets;
                            maxChain = 99;

                            if (Main.IsDebugEnabled)
                            {
                                string key = ability.Blueprint?.AssetGuid?.ToString() ?? ability.Name;
                                if (_loggedChainAbilities.Add(key))
                                    Log.Engine.Debug($"[ChainDetect] PatternTrail: {ability.Name} → {castSpell.Spell.name} (radius={radius}, type={targetType})");
                            }
                            return true;
                        }

                        // 검출 실패 — *새 spell* 만 1회 진단 (회귀/신규 무기 식별용)
                        if (Main.IsDebugEnabled)
                        {
                            string spellKey = castSpell.Spell.AssetGuid?.ToString() ?? castSpell.Spell.name;
                            if (_loggedNonChainSpells.Add(spellKey))
                            {
                                var spellComps = castSpell.Spell.ComponentsArray;
                                string compNames = spellComps == null ? "(null)" : string.Join(",", spellComps.Select(c => c?.GetType().Name ?? "null"));
                                Log.Engine.Debug($"[ChainDetect] non-chain spell '{castSpell.Spell.name}' components: [{compNames}]");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Log.Engine.Warn($"[CombatAPI] TryGetWeaponOnHitChain failed for {ability?.Name}: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Warp Relay 로 릴레이되는 능력의 리다이렉트 타겟 종류. null = AbilityRedirect 컴포넌트 없음
        /// (릴레이가 아니거나 point-AoE 방식). 게임 AbilityData.IsValidTargetForRedirect 는 이 값으로
        /// 재조준 대상을 필터 — Enemy 면 적만, Ally 면 아군만, Any 면 무제한(아군도 타격).
        /// </summary>
        public static TargetType? GetRelayRedirectTargetType(AbilityData ability)
        {
            try
            {
                var redirect = ability?.Blueprint?.GetComponent<AbilityRedirect>();
                return redirect != null ? redirect.RedirectTargetType : (TargetType?)null;
            }
            catch (Exception ex)
            {
                // 무음 실패 금지: 기본 로그 레벨에서도 스택과 함께 남긴다. 반환 null 은 "재조준 없음"과
                // 동일하므로, 오류가 무음으로 진단(Warp Relay friendly-fire 판정)을 오염시키지 않도록
                // Error 로 가시화 — 진단 로그에 redirect=none 이 찍혀도 이 Error 로 오류였음을 식별 가능.
                Log.Engine.Error(ex, $"[CombatAPI] GetRelayRedirectTargetType failed for {ability?.Name}");
                return null;
            }
        }

        /// <summary>릴레이 재조준이 적만 대상으로 하는지 (Enemy) — 그렇다면 아군 안전.</summary>
        public static bool IsRelayRedirectEnemyOnly(AbilityData ability)
            => GetRelayRedirectTargetType(ability) == TargetType.Enemy;

        /// <summary>릴레이 재조준 종류 로그용 문자열 (없으면 "none/point-AoE").</summary>
        public static string GetRelayRedirectTypeName(AbilityData ability)
            => GetRelayRedirectTargetType(ability)?.ToString() ?? "none/point-AoE";

        /// <summary>
        /// v3.117.30: Unit 이 친선 사격 자동 회피 (immunity) 보유 여부.
        ///
        /// 게임 native 메커니즘 (디컴파일 검증):
        ///   PartMechanicFeatures.AutoDodgeFriendlyFire (FeatureCountableFlag) — 친선 사격 한정
        ///   PartMechanicFeatures.AutoDodge (FeatureCountableFlag) — 모든 공격 (강력)
        ///   FeatureCountableFlag → bool implicit cast (count > 0 = active)
        ///
        /// 활용: AoE/chain 시뮬레이션에서 immunity 보유 ally 는 친선 사격 카운트 제외 →
        /// AI 가 자유롭게 공격 (게임이 자동 dodge 처리).
        /// </summary>
        public static bool IsImmuneToFriendlyFire(BaseUnitEntity unit)
        {
            if (unit?.Features == null) return false;
            try
            {
                if ((bool)unit.Features.AutoDodgeFriendlyFire) return true;
                if ((bool)unit.Features.AutoDodge) return true;
                return false;
            }
            catch { return false; }
        }

        // v3.117.35: caster 에게 Arc 무기 친선 사격 면역 buff 가 있는지 검사용 GUID list.
        //   사용자 보고: LiturgyOfInsulation_Buff (b8b7d40d3471498dbe991ee1003894ec) 가 활성 시
        //   Arc 무기 chain 이 아군 자동 면역 처리. AI 는 이 buff 활성 시 chain check 우회 가능.
        //   향후 다른 비슷한 buff 발견 시 list 확장.
        private static readonly HashSet<string> _arcInsulationBuffGuids = new HashSet<string>
        {
            "b8b7d40d3471498dbe991ee1003894ec",  // LiturgyOfInsulation_Buff (Adept passive)
        };

        /// <summary>
        /// v3.117.35: caster 가 Arc 무기 친선 사격 면역 buff 활성 보유 여부.
        ///
        /// 활용: chain weapon 검사 시 caster 가 Arc-insulation 보유 → 게임이 자동으로 ally chain 차단 →
        /// AI 가 chain check skip 가능 (false positive 차단 방지).
        /// </summary>
        public static bool HasArcInsulationBuff(BaseUnitEntity caster)
        {
            if (caster?.Buffs == null) return false;
            try
            {
                foreach (var buff in caster.Buffs)
                {
                    string guid = buff?.Blueprint?.AssetGuid?.ToString();
                    if (guid != null && _arcInsulationBuffGuids.Contains(guid))
                        return true;
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Log.Engine.Warn($"[CombatAPI] HasArcInsulationBuff failed for {caster?.CharacterName}: {ex.Message}");
            }
            return false;
        }

        #endregion
    }
}
