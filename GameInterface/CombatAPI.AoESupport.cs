using System;
using System.Collections.Generic;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Abilities.Components.Base;
using Kingmaker.UnitLogic.Abilities.Components.Patterns;
using Kingmaker.Utility;
using Pathfinding;
using UnityEngine;
using CompanionAI_v3.Data;

namespace CompanionAI_v3.GameInterface
{
    public static partial class CombatAPI
    {
        // ★ v3.9.10: Pattern counting zero-alloc 풀 (new HashSet<> 제거)
        // ★ v3.111.29: residual header → AoESupport partial 로 동반 이동 (used exclusively by Game Pattern API region below)
        private static readonly HashSet<BaseUnitEntity> _sharedUnitSet = new HashSet<BaseUnitEntity>();
        private static readonly HashSet<BaseUnitEntity> _sharedAllySet = new HashSet<BaseUnitEntity>();

        #region AOE Support (v3.1.16)

        /// <summary>
        /// ★ v3.1.16: AOE 패턴 설정 조회
        /// </summary>
        public static Kingmaker.UnitLogic.Abilities.Components.Base.IAbilityAoEPatternProvider GetPatternSettings(AbilityData ability)
        {
            try
            {
                return ability?.GetPatternSettings();
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetPatternSettings failed for {ability?.Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ★ v3.1.16: AOE 반경 조회 (타일 단위)
        /// </summary>
        public static float GetAoERadius(AbilityData ability)
        {
            try
            {
                var pattern = ability?.GetPatternSettings()?.Pattern;
                if (pattern != null)
                    return pattern.Radius;

                return ability?.Blueprint?.AoERadius ?? 0f;
            }
            // ★ v3.13.0: 로깅 추가 (기본값 0f는 이미 보수적 — AoE 무시)
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetAoERadius failed for {ability?.Name}: {ex.Message}");
                return 0f;
            }
        }

        /// <summary>
        /// ★ v3.1.16: AOE 패턴 타입 조회
        /// </summary>
        public static Kingmaker.Blueprints.PatternType? GetPatternType(AbilityData ability)
        {
            try
            {
                return ability?.GetPatternSettings()?.Pattern?.Type;
            }
            // ★ v3.13.0: 로깅 추가 (기본값 null은 이미 보수적 — 패턴 불명)
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetPatternType failed for {ability?.Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ★ v3.1.16: AOE 대상 타입 조회 (Enemy/Ally/Any)
        /// </summary>
        public static Kingmaker.UnitLogic.Abilities.Components.TargetType GetAoETargetType(AbilityData ability)
        {
            try
            {
                return ability?.GetPatternSettings()?.Targets ?? Kingmaker.UnitLogic.Abilities.Components.TargetType.Enemy;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAoETargetType failed for {ability?.Name}: {ex.Message}");
                return Kingmaker.UnitLogic.Abilities.Components.TargetType.Enemy;
            }
        }

        /// <summary>
        /// ★ v3.5.74: Point 타겟 능력인지 확인 (게임 API 우선)
        /// 게임 네이티브 IsAOE 먼저 체크 + 기존 로직 폴백
        /// </summary>
        public static bool IsPointTargetAbility(AbilityData ability)
        {
            try
            {
                if (ability == null) return false;

                // ★ v3.5.74: 게임 네이티브 IsAOE 먼저 체크
                if (ability.IsAOE) return true;

                var bp = ability.Blueprint;
                if (bp == null || !bp.CanTargetPoint) return false;

                // 패턴 설정에서 실제 반경 확인
                var pattern = ability.GetPatternSettings()?.Pattern;
                if (pattern != null)
                    return pattern.Radius > 0;

                // Blueprint AOE 반경 폴백
                return bp.AoERadius > 0;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsPointTargetAbility failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.1.16: Point 타겟에 능력 사용 가능 검증
        /// </summary>
        public static bool CanUseAbilityOnPoint(AbilityData ability, Vector3 point, out string reason)
        {
            reason = null;
            if (ability == null) { reason = "Null ability"; return false; }

            try
            {
                var target = new TargetWrapper(point);
                AbilityData.UnavailabilityReasonType? unavailable;
                bool canTarget = ability.CanTarget(target, out unavailable);

                if (!canTarget && unavailable.HasValue)
                    reason = unavailable.Value.ToString();

                return canTarget;
            }
            catch (Exception ex)
            {
                reason = $"Exception: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// ★ v3.1.19: AOE 패턴 각도 조회 (Cone/Sector용, 단위: degree)
        /// Reflection 제거 - pattern.Angle 프로퍼티 직접 사용
        /// </summary>
        public static float GetPatternAngle(AbilityData ability)
        {
            try
            {
                var pattern = ability?.GetPatternSettings()?.Pattern;
                if (pattern == null) return 90f;

                // ★ v3.1.19: 게임 API 직접 사용 (AoEPattern.Angle 프로퍼티)
                // Reflection 대신 public 프로퍼티 사용 - 이미 full-angle
                return pattern.Angle;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetPatternAngle error: {ex.Message}");
                return 90f;
            }
        }

        /// <summary>
        /// ★ v3.1.18: 패턴이 방향성 패턴인지 확인 (Cone/Ray/Sector)
        /// ★ v3.8.09: 이 함수는 PatternType만 체크 - CanBeDirectional과 동일
        /// 실제 IsDirectional 판정은 GetActualIsDirectional() 사용!
        /// </summary>
        public static bool IsDirectionalPattern(Kingmaker.Blueprints.PatternType? patternType)
        {
            if (!patternType.HasValue) return false;

            switch (patternType.Value)
            {
                case Kingmaker.Blueprints.PatternType.Cone:
                case Kingmaker.Blueprints.PatternType.Ray:
                case Kingmaker.Blueprints.PatternType.Sector:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// ★ v3.8.09: 게임의 실제 IsDirectional 로직 구현
        /// - Non-Custom 패턴: AbilityAoEPatternSettings.m_Directional 필드
        /// - Custom 패턴: AoEPattern.IsDirectional → BlueprintAttackPattern.IsDirectional
        /// </summary>
        public static bool GetActualIsDirectional(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                var patternSettings = ability.GetPatternSettings();
                if (patternSettings == null) return false;

                var pattern = patternSettings.Pattern;
                if (pattern == null) return false;

                // Custom 패턴: AoEPattern.IsDirectional 프로퍼티 직접 사용
                if (pattern.IsCustom)
                {
                    try
                    {
                        return pattern.IsDirectional;  // BlueprintAttackPattern.IsDirectional
                    }
                    catch (Exception ex)
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetActualIsDirectional(custom) failed for {ability?.Name}: {ex.Message}");
                        return false;
                    }
                }

                // Non-Custom 패턴: m_Directional 필드 (Reflection)
                if (!pattern.CanBeDirectional) return false;  // Ray/Cone/Sector만 가능

                // AbilityAoEPatternSettings에서 m_Directional 필드 가져오기
                var settingsType = patternSettings.GetType();
                var directionalField = settingsType.GetField("m_Directional",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (directionalField != null)
                {
                    bool result = (bool)directionalField.GetValue(patternSettings);
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] {ability.Name}: m_Directional field = {result}");
                    return result;
                }

                // 필드를 찾지 못하면 타입 기반 폴백 (CanBeDirectional이면 true 가정)
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] {ability.Name}: m_Directional field not found, using CanBeDirectional fallback");
                return pattern.CanBeDirectional;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetActualIsDirectional error for {ability?.Name}: {ex.Message}");
                return IsDirectionalPattern(GetPatternType(ability));  // 폴백
            }
        }

        /// <summary>
        /// ★ v3.8.09: AbilityCustomRam 컴포넌트 사용 여부 (Slash 공격 등)
        /// AbilityCustomRam은 Pattern이 null이지만 동적으로 Ray 패턴 생성
        /// </summary>
        public static bool IsRamAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                var bp = ability.Blueprint;
                if (bp == null) return false;

                // AbilityCustomRam 컴포넌트 체크
                return bp.GetComponent<Kingmaker.UnitLogic.Abilities.Components.AbilityCustomRam>() != null;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsRamAbility failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.8.09: Ram 능력의 관통 여부 (m_RamThrough)
        /// true면 경로의 모든 적 타격, false면 첫 적에서 멈춤
        /// </summary>
        public static bool IsRamThroughAbility(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                var bp = ability.Blueprint;
                if (bp == null) return false;

                var ramComponent = bp.GetComponent<Kingmaker.UnitLogic.Abilities.Components.AbilityCustomRam>();
                if (ramComponent == null) return false;

                // m_RamThrough 필드 (Reflection)
                var ramThroughField = ramComponent.GetType().GetField("m_RamThrough",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (ramThroughField != null)
                {
                    return (bool)ramThroughField.GetValue(ramComponent);
                }

                return false;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsRamThroughAbility failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Self-Targeted AOE (v3.1.23)

        /// <summary>
        /// ★ v3.1.23: 자신 타겟 AOE 공격인지 확인
        /// Bladedance 같은 능력: Range=Personal, CanTargetSelf, 인접 유닛 공격
        /// </summary>
        public static bool IsSelfTargetedAoEAttack(AbilityData ability)
        {
            if (ability == null) return false;

            try
            {
                var bp = ability.Blueprint;
                if (bp == null) return false;

                // Range=Personal + CanTargetSelf 체크
                if (bp.Range != AbilityRange.Personal) return false;
                if (!bp.CanTargetSelf) return false;

                // DangerousAoE로 분류된 능력만
                return AbilityDatabase.IsDangerousAoE(ability);
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsSelfTargetedAoEAttack failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.8.50: 근접 AOE 능력 감지 (유닛 타겟형)
        /// BladeDance(Self-Target)는 제외 — 적을 직접 타겟하는 근접 AOE만 감지
        /// 게임 AbilityMeleeBurst + Pattern 기반 근접 스플래시 공격
        /// </summary>
        public static bool IsMeleeAoEAbility(AbilityData ability)
        {
            if (ability == null) return false;
            try
            {
                // Self-Target AOE는 이미 Phase 4.3에서 별도 처리
                if (IsSelfTargetedAoEAttack(ability)) return false;

                // 근접 능력이어야 함
                if (!ability.IsMelee) return false;

                // AOE 패턴이 있어야 함 (게임 네이티브 + 커스텀 감지)
                if (CombatHelpers.IsAoEAbility(ability)) return true;

                // 패턴 설정 직접 확인 (IsAoEAbility에서 놓칠 수 있는 케이스)
                if (ability.GetPatternSettings() != null) return true;

                return false;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsMeleeAoEAbility failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.6.3: 인접 아군 수 계산 (Self-Targeted AOE 안전성 체크)
        /// radius는 타일 단위 (기본 2타일 ≈ 2.7m)
        /// </summary>
        public static int CountAdjacentAllies(BaseUnitEntity unit, float radius = 2f)  // 타일
        {
            if (unit == null) return 0;

            try
            {
                int count = 0;
                var allUnits = Game.Instance?.State?.AllBaseAwakeUnits;
                if (allUnits == null) return 0;

                foreach (var other in allUnits)
                {
                    if (other == null || other == unit) continue;
                    if (other.LifeState.IsDead) continue;

                    // 아군 판별
                    bool isAlly = unit.IsPlayerFaction == other.IsPlayerFaction;
                    if (!isAlly) continue;

                    // ★ v3.6.3: 타일 단위로 변환
                    float distTiles = MetersToTiles(Vector3.Distance(unit.Position, other.Position));
                    if (distTiles <= radius)
                        count++;
                }

                return count;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CountAdjacentAllies failed for {unit?.CharacterName}: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// ★ v3.6.3: 인접 적 수 계산 (Self-Targeted AOE 효율성 체크)
        /// radius는 타일 단위 (기본 2타일 ≈ 2.7m)
        /// </summary>
        public static int CountAdjacentEnemies(BaseUnitEntity unit, float radius = 2f)  // 타일
        {
            if (unit == null) return 0;

            try
            {
                int count = 0;
                var allUnits = Game.Instance?.State?.AllBaseAwakeUnits;
                if (allUnits == null) return 0;

                foreach (var other in allUnits)
                {
                    if (other == null || other == unit) continue;
                    if (other.LifeState.IsDead) continue;

                    // 적 판별
                    bool isEnemy = (unit.IsPlayerFaction && other.IsPlayerEnemy) ||
                                   (!unit.IsPlayerFaction && !other.IsPlayerEnemy);
                    if (!isEnemy) continue;

                    // ★ v3.6.3: 타일 단위로 변환
                    float distTiles = MetersToTiles(Vector3.Distance(unit.Position, other.Position));
                    if (distTiles <= radius)
                        count++;
                }

                return count;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CountAdjacentEnemies failed for {unit?.CharacterName}: {ex.Message}");
                return 0;
            }
        }

        #endregion

        #region Pattern Info Cache (v3.1.19)

        /// <summary>
        /// ★ v3.1.19: AOE 패턴 정보 통합 클래스
        /// ★ v3.8.09: IsRamAbility, IsRamThrough 추가
        /// </summary>
        public class PatternInfo
        {
            public Kingmaker.Blueprints.PatternType? Type { get; set; }
            public float Radius { get; set; }
            public float Angle { get; set; }
            public Kingmaker.UnitLogic.Abilities.Components.TargetType TargetType { get; set; }
            public bool IsDirectional { get; set; }
            public bool CanBeDirectional { get; set; }  // ★ v3.8.09: Type만으로 판단
            public bool IsRamAbility { get; set; }      // ★ v3.8.09: AbilityCustomRam 사용
            public bool IsRamThrough { get; set; }      // ★ v3.8.09: 관통 여부
            public bool IsValid => Radius > 0 || IsRamAbility;
        }

        private static Dictionary<string, PatternInfo> PatternCache = new Dictionary<string, PatternInfo>();

        /// <summary>
        /// ★ v3.1.19: 패턴 정보 조회 (캐싱)
        /// ★ v3.8.09: GetActualIsDirectional() 사용으로 정확한 IsDirectional 판정
        /// </summary>
        public static PatternInfo GetPatternInfo(AbilityData ability)
        {
            try
            {
                var guid = ability?.Blueprint?.AssetGuid?.ToString();
                if (string.IsNullOrEmpty(guid)) return null;

                if (PatternCache.TryGetValue(guid, out var cached))
                    return cached;

                var patternType = GetPatternType(ability);
                bool canBeDirectional = IsDirectionalPattern(patternType);  // Type 기반 (Ray/Cone/Sector)
                bool actualIsDirectional = GetActualIsDirectional(ability); // 게임 실제 로직

                // ★ v3.8.09: Ram 능력 체크
                bool isRam = IsRamAbility(ability);
                bool isRamThrough = isRam && IsRamThroughAbility(ability);

                var info = new PatternInfo
                {
                    Type = patternType,
                    Radius = GetAoERadius(ability),
                    Angle = GetPatternAngle(ability),
                    TargetType = GetAoETargetType(ability),
                    CanBeDirectional = canBeDirectional,
                    IsDirectional = actualIsDirectional,
                    IsRamAbility = isRam,
                    IsRamThrough = isRamThrough
                };

                // ★ v3.8.09: 디버그 로그 (새 능력일 때만)
                if (actualIsDirectional != canBeDirectional || isRam)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] PatternInfo for {ability.Name}: Type={patternType}, " +
                        $"CanBeDirectional={canBeDirectional}, IsDirectional={actualIsDirectional}, " +
                        $"IsRam={isRam}, RamThrough={isRamThrough}, Radius={info.Radius}");
                }

                PatternCache[guid] = info;
                return info;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// ★ v3.1.19: 패턴 캐시 클리어 (전투 종료 시 호출)
        /// </summary>
        public static void ClearPatternCache()
        {
            PatternCache.Clear();
            Main.LogDebug("[CombatAPI] Pattern cache cleared");
        }

        #endregion

        #region Game Pattern API (v3.5.39)

        /// <summary>
        /// ★ v3.5.39: 게임 API를 통해 AOE 패턴의 영향받는 노드들 조회
        /// 게임과 동일한 정확한 타일 기반 계산
        /// </summary>
        public static OrientedPatternData GetAffectedNodes(
            AbilityData ability,
            Vector3 targetPosition,
            Vector3 casterPosition)
        {
            try
            {
                if (ability == null) return OrientedPatternData.Empty;

                var target = new TargetWrapper(targetPosition);
                return ability.GetPattern(target, casterPosition);
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetAffectedNodes error: {ex.Message}");
                return OrientedPatternData.Empty;
            }
        }

        /// <summary>
        /// ★ v3.5.39: 게임 API를 통해 패턴 내 적 수 계산
        /// Circle, Cone, Ray 모든 패턴에서 정확하게 작동
        /// </summary>
        public static int CountEnemiesInPattern(
            AbilityData ability,
            Vector3 targetPosition,
            Vector3 casterPosition,
            List<BaseUnitEntity> enemies)
        {
            try
            {
                if (ability == null || enemies == null || enemies.Count == 0)
                    return 0;

                var pattern = GetAffectedNodes(ability, targetPosition, casterPosition);
                if (pattern.IsEmpty) return 0;

                // ★ v3.9.10: new HashSet<> 제거 → 정적 풀 재사용
                _sharedUnitSet.Clear();
                for (int i = 0; i < enemies.Count; i++)
                    _sharedUnitSet.Add(enemies[i]);

                // ★ v3.9.22: Remove로 중복 방지 — 대형 유닛(4x4)이 여러 타일 점유 시 1회만 카운트
                int count = 0;
                foreach (var node in pattern.Nodes)
                {
                    if (node.TryGetUnit(out var unit) &&
                        unit is BaseUnitEntity baseUnit &&
                        _sharedUnitSet.Remove(baseUnit))
                    {
                        count++;
                    }
                }

                return count;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CountEnemiesInPattern error: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// ★ v3.5.39: 게임 API를 통해 패턴 내 아군 수 계산 (자신 제외)
        /// </summary>
        public static int CountAlliesInPattern(
            AbilityData ability,
            Vector3 targetPosition,
            Vector3 casterPosition,
            BaseUnitEntity caster,
            List<BaseUnitEntity> allies)
        {
            try
            {
                if (ability == null || allies == null || allies.Count == 0)
                    return 0;

                var pattern = GetAffectedNodes(ability, targetPosition, casterPosition);
                if (pattern.IsEmpty) return 0;

                // ★ v3.9.10: new HashSet<> 제거 → 정적 풀 재사용
                _sharedAllySet.Clear();
                for (int i = 0; i < allies.Count; i++)
                    _sharedAllySet.Add(allies[i]);

                // ★ v3.9.22: Remove로 중복 방지 — 대형 유닛 다중 타일 점유 시 1회만 카운트
                int count = 0;
                foreach (var node in pattern.Nodes)
                {
                    if (node.TryGetUnit(out var unit) &&
                        unit is BaseUnitEntity baseUnit &&
                        baseUnit != caster &&
                        _sharedAllySet.Remove(baseUnit))
                    {
                        count++;
                    }
                }

                return count;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CountAlliesInPattern error: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// ★ v3.9.10: 패턴 1회 계산으로 적/아군 수 동시 카운트
        /// GetAffectedNodes 중복 호출 제거 — AttackPlanner 이중 호출 최적화
        /// </summary>
        public static void CountUnitsInPattern(
            AbilityData ability,
            Vector3 targetPosition,
            Vector3 casterPosition,
            BaseUnitEntity caster,
            List<BaseUnitEntity> enemies,
            List<BaseUnitEntity> allies,
            out int enemyCount,
            out int allyCount)
        {
            enemyCount = 0;
            allyCount = 0;

            try
            {
                if (ability == null) return;

                var pattern = GetAffectedNodes(ability, targetPosition, casterPosition);
                if (pattern.IsEmpty) return;

                _sharedUnitSet.Clear();
                if (enemies != null)
                    for (int i = 0; i < enemies.Count; i++)
                        _sharedUnitSet.Add(enemies[i]);

                _sharedAllySet.Clear();
                if (allies != null)
                    for (int i = 0; i < allies.Count; i++)
                        _sharedAllySet.Add(allies[i]);

                // ★ v3.9.22: Remove로 중복 방지 — 대형 유닛 다중 타일 점유 시 1회만 카운트
                foreach (var node in pattern.Nodes)
                {
                    if (node.TryGetUnit(out var unit) && unit is BaseUnitEntity baseUnit)
                    {
                        if (_sharedUnitSet.Remove(baseUnit))
                            enemyCount++;
                        if (baseUnit != caster && _sharedAllySet.Remove(baseUnit))
                            allyCount++;
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] CountUnitsInPattern error: {ex.Message}");
            }
        }

        /// <summary>
        /// ★ v3.5.39: 특정 유닛이 패턴 내에 있는지 확인
        /// </summary>
        public static bool IsUnitInPattern(
            AbilityData ability,
            Vector3 targetPosition,
            Vector3 casterPosition,
            BaseUnitEntity unit)
        {
            try
            {
                if (ability == null || unit == null) return false;

                var pattern = GetAffectedNodes(ability, targetPosition, casterPosition);
                if (pattern.IsEmpty) return false;

                // 유닛이 점유한 모든 노드 확인
                foreach (var occupiedNode in unit.GetOccupiedNodes())
                {
                    if (pattern.Contains(occupiedNode))
                        return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsUnitInPattern error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.5.39: 패턴 내 모든 유닛 조회 (적/아군 구분 없이)
        /// </summary>
        public static List<BaseUnitEntity> GetUnitsInPattern(
            AbilityData ability,
            Vector3 targetPosition,
            Vector3 casterPosition)
        {
            var result = new List<BaseUnitEntity>();
            try
            {
                if (ability == null) return result;

                var pattern = GetAffectedNodes(ability, targetPosition, casterPosition);
                if (pattern.IsEmpty) return result;

                var seen = new HashSet<BaseUnitEntity>();
                foreach (var node in pattern.Nodes)
                {
                    if (node.TryGetUnit(out var unit) &&
                        unit is BaseUnitEntity baseUnit &&
                        !seen.Contains(baseUnit))
                    {
                        seen.Add(baseUnit);
                        result.Add(baseUnit);
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] GetUnitsInPattern error: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// ★ v3.5.39: AOE 평가 - 적 점수와 아군 피해를 함께 계산
        /// </summary>
        public static (int enemyHits, int allyHits, int playerPartyHits) EvaluateAoEPosition(
            AbilityData ability,
            Vector3 targetPosition,
            Vector3 casterPosition,
            BaseUnitEntity caster,
            List<BaseUnitEntity> enemies,
            List<BaseUnitEntity> allies)
        {
            try
            {
                if (ability == null) return (0, 0, 0);

                var pattern = GetAffectedNodes(ability, targetPosition, casterPosition);
                if (pattern.IsEmpty) return (0, 0, 0);

                int enemyHits = 0;
                int allyHits = 0;
                int playerPartyHits = 0;

                var enemySet = new HashSet<BaseUnitEntity>(enemies ?? new List<BaseUnitEntity>());
                var allySet = new HashSet<BaseUnitEntity>(allies ?? new List<BaseUnitEntity>());
                var counted = new HashSet<BaseUnitEntity>();

                foreach (var node in pattern.Nodes)
                {
                    if (!node.TryGetUnit(out var unit) || !(unit is BaseUnitEntity baseUnit))
                        continue;

                    if (counted.Contains(baseUnit)) continue;
                    counted.Add(baseUnit);

                    if (enemySet.Contains(baseUnit))
                    {
                        enemyHits++;
                    }
                    else if (baseUnit != caster && allySet.Contains(baseUnit))
                    {
                        allyHits++;
                        if (baseUnit.IsInPlayerParty)
                            playerPartyHits++;
                    }
                }

                return (enemyHits, allyHits, playerPartyHits);
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] EvaluateAoEPosition error: {ex.Message}");
                return (0, 0, 0);
            }
        }

        /// <summary>
        /// ★ v3.6.9: AOE 높이 차이 체크 - 게임 로직 참조
        /// Circle 패턴: 1.6m 이상 차이 시 효과 없음
        /// ★ v3.7.15: Directional 패턴도 1.6m로 통일
        /// 이유: 게임은 기울기(slope)를 계산하여 더 복잡한 검증을 함
        ///       우리 AI가 0.3m로 너무 엄격하게 필터링하면 공격 기회 상실
        ///       게임이 최종 검증을 하므로 사전 필터링은 관대하게
        /// </summary>
        public const float AoELevelDiffCircle = 1.6f;      // AoEPattern.SameLevelDiff
        public const float AoELevelDiffDirectional = 1.6f; // ★ v3.7.15: 0.3f → 1.6f (게임이 기울기 계산으로 검증)

        /// <summary>
        /// ★ v3.6.9: AOE 높이 차이로 인해 적에게 효과가 닿을 수 있는지 확인
        /// </summary>
        /// <param name="ability">AOE 능력</param>
        /// <param name="casterPosition">시전자 위치</param>
        /// <param name="targetPosition">타겟 위치</param>
        /// <returns>높이 차이가 허용 범위 내면 true</returns>
        public static bool IsAoEHeightInRange(AbilityData ability, Vector3 casterPosition, Vector3 targetPosition)
        {
            try
            {
                if (ability == null) return true;  // 안전 폴백

                // 패턴 타입 확인
                var patternType = GetPatternType(ability);

                // ★ v3.6.9 fix: 패턴 타입이 없으면 AOE 여부 확인 후 Circle로 처리
                // ★ v3.8.09: GetActualIsDirectional() 사용으로 정확한 판정
                bool isDirectional = false;
                if (patternType.HasValue)
                {
                    isDirectional = GetActualIsDirectional(ability);  // ★ v3.8.09: 게임 실제 로직
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] AOE height: {ability.Name} PatternType={patternType.Value}, IsDirectional={isDirectional}");
                }
                else
                {
                    // 패턴 타입이 없으면 AOE 반경으로 Circle 여부 판단
                    float aoERadius = GetAoERadius(ability);
                    if (aoERadius > 0)
                    {
                        if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] AOE height: {ability.Name} PatternType=null but AOE r={aoERadius}, treating as Circle");
                    }
                    // isDirectional = false → Circle 임계값(1.6m) 사용
                }

                // 높이 차이 계산 (절대값)
                float heightDiff = Mathf.Abs(casterPosition.y - targetPosition.y);

                // 패턴 타입에 따른 임계값 선택
                float threshold = isDirectional ? AoELevelDiffDirectional : AoELevelDiffCircle;

                bool inRange = heightDiff <= threshold;

                if (!inRange)
                {
                    if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] AOE height check failed: {ability.Name} " +
                        $"heightDiff={heightDiff:F2}m > threshold={threshold:F2}m ({(isDirectional ? "Directional" : "Circle")})");
                }

                return inRange;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsAoEHeightInRange error: {ex.Message}");
                return true;  // 에러 시 안전하게 허용
            }
        }

        /// <summary>
        /// ★ v3.6.9: AOE 높이 차이로 인해 적에게 효과가 닿을 수 있는지 확인 (유닛 버전)
        /// </summary>
        public static bool IsAoEHeightInRange(AbilityData ability, BaseUnitEntity caster, BaseUnitEntity target)
        {
            if (caster == null || target == null) return true;
            return IsAoEHeightInRange(ability, caster.Position, target.Position);
        }

        /// <summary>
        /// ★ v3.6.10: AOE 범위 내에 유닛이 있는지 확인 (2D 거리 + 높이 체크 통합)
        /// AoESafetyChecker, ClusterDetector에서 사용
        /// </summary>
        /// <param name="ability">AOE 능력 (null이면 Circle로 처리)</param>
        /// <param name="center">AOE 중심 (시전자 또는 타겟 위치)</param>
        /// <param name="unit">체크할 유닛</param>
        /// <param name="aoERadius">AOE 반경 (타일 단위)</param>
        /// <returns>유닛이 AOE 효과 범위 내에 있으면 true</returns>
        public static bool IsUnitInAoERange(AbilityData ability, Vector3 center, BaseUnitEntity unit, float aoERadius)
        {
            if (unit == null) return false;

            // ★ v3.8.66: 대형 유닛은 가장 가까운 경계 셀 기준 (SizeRect 반영)
            float dist2D = (float)WarhammerGeometryUtils.DistanceToInCells(
                center, new IntRect(0, 0, 0, 0),  // AoE 중심은 점
                unit.Position, unit.SizeRect);
            if (dist2D > aoERadius) return false;

            // 2. 높이 차이 체크
            float heightDiff = Mathf.Abs(center.y - unit.Position.y);

            // ★ v3.8.09: 패턴 타입에 따른 높이 임계값 - GetActualIsDirectional 사용
            bool isDirectional = false;
            if (ability != null)
            {
                isDirectional = GetActualIsDirectional(ability);  // ★ v3.8.09: 게임 실제 로직
            }

            float heightThreshold = isDirectional ? AoELevelDiffDirectional : AoELevelDiffCircle;
            return heightDiff <= heightThreshold;
        }

        /// <summary>
        /// ★ v3.6.10: 방향성 AOE(Cone/Ray/Sector) 범위 내에 유닛이 있는지 확인
        /// ★ v3.8.09: Custom/Circle 패턴 지원 추가
        /// </summary>
        public static bool IsUnitInDirectionalAoERange(
            Vector3 casterPosition,
            Vector3 direction,
            BaseUnitEntity unit,
            float radius,  // 타일
            float angle,
            Kingmaker.Blueprints.PatternType patternType)
        {
            if (unit == null) return false;

            Vector3 toUnit = unit.Position - casterPosition;

            // 1. 2D 거리 체크
            float dist2D = MetersToTiles(new Vector3(toUnit.x, 0, toUnit.z).magnitude);
            if (dist2D > radius) return false;
            if (dist2D < 0.5f) return false;  // 캐스터 위치 제외

            // 2. 높이 차이 체크 (Directional은 0.3m)
            float heightDiff = Mathf.Abs(toUnit.y);
            if (heightDiff > AoELevelDiffDirectional) return false;

            // 3. 각도 체크
            Vector3 toUnit2D = new Vector3(toUnit.x, 0, toUnit.z);
            Vector3 direction2D = new Vector3(direction.x, 0, direction.z);
            float unitAngle = Vector3.Angle(direction2D, toUnit2D);

            switch (patternType)
            {
                case Kingmaker.Blueprints.PatternType.Ray:
                    // ★ v3.8.65: 게임 검증 — Ray = Bresenham 1-cell 직선 (AoEPattern.Angle=0)
                    // 각도가 아닌 수직 거리 1타일 이내로 판정
                    {
                        Vector3 dirNorm2D = direction2D.normalized;
                        float perpMeters = Vector3.Cross(dirNorm2D, toUnit2D).magnitude;
                        float perpTiles = MetersToTiles(perpMeters);
                        return perpTiles <= 1f;
                    }

                case Kingmaker.Blueprints.PatternType.Cone:
                case Kingmaker.Blueprints.PatternType.Sector:
                    return unitAngle <= angle / 2f;

                case Kingmaker.Blueprints.PatternType.Custom:
                    // ★ v3.8.09: Custom 패턴 - 각도가 설정되어 있으면 사용
                    // 360도면 전방향 (거리만 체크)
                    if (angle >= 360f) return true;
                    return unitAngle <= angle / 2f;

                case Kingmaker.Blueprints.PatternType.Circle:
                    // ★ v3.8.09: Circle은 거리만 체크 (방향 무관)
                    return true;

                default:
                    return false;
            }
        }

        // ★ v3.112.0: Phase E.1 — 게임 내장 OrientedPatternData 기반 영향 타일 반환
        // 반환: directional (Ray/Cone/Sector) AOE의 정확한 타일 집합. null = 비AOE / 패턴 추출 실패 / 노드 확보 실패 → 호출부 폴백.
        // 주의: `_sharedUnitSet` 재사용 — 호출부는 결과를 즉시 소비하거나 복사해야 함 (다른 AoESupport 메서드가 덮어씀).
        public static HashSet<BaseUnitEntity> GetAffectedUnitsNative(
            BaseUnitEntity caster,
            BaseUnitEntity target,
            AbilityData ability,
            out int affectedTileCount)
        {
            affectedTileCount = 0;
            if (caster == null || target == null || ability == null) return null;

            var pattern = TryExtractAoEPattern(ability);
            if (pattern == null) return null;

            var casterNode = caster.Position.GetNearestNodeXZ();
            var targetNode = target.Position.GetNearestNodeXZ();
            if (casterNode == null || targetNode == null) return null;

            var direction = AoEPattern.GetCastDirection(pattern.Type, casterNode, targetNode, targetNode);
            bool isDirectional = pattern.CanBeDirectional;

            OrientedPatternData oriented;
            try
            {
                oriented = pattern.GetOriented(
                    checkLosFromNode: casterNode,
                    applicationNode: targetNode,
                    direction: direction,
                    isIgnoreLos: false,
                    isIgnoreLevelDifference: false,
                    isDirectional: isDirectional,
                    coveredTargetsOnly: false,
                    useMeleeLos: false,
                    excludeUnwalkable: false);
            }
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI][Native] GetOriented failed for {ability.Name}: {ex.Message}");
                return null;
            }

            var result = _sharedUnitSet;
            result.Clear();
            foreach (var tuple in oriented.NodesWithExtraData)
            {
                affectedTileCount++;
                var node = tuple.Item1;
                if (node == null) continue;
                if (!node.ContainsUnit()) continue;
                var unit = node.GetUnit();
                if (unit == null || unit.LifeState.IsDead) continue;
                result.Add(unit);
            }
            return result;
        }

        // ★ v3.112.0: Phase E.1 — AbilityData 에서 AoEPattern 추출.
        // 경로: ability.GetPatternSettings() (IAbilityAoEPatternProvider).Pattern
        // 동일 경로를 GetPatternType (line 72) / GetAoERadius (line 51) 가 이미 사용 — 검증된 canonical API.
        // Pattern 속성은 [CanBeNull] (인터페이스 정의) — null 폴백 정상 동작.
        private static AoEPattern TryExtractAoEPattern(AbilityData ability)
        {
            try
            {
                return ability?.GetPatternSettings()?.Pattern;
            }
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI][Native] TryExtractAoEPattern failed for {ability?.Name}: {ex.Message}");
                return null;
            }
        }

        #endregion
    }
}
