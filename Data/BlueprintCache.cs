using Kingmaker.Blueprints;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Abilities.Components;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CompanionAI_v3.Data
{
    /// <summary>
    /// ★ v3.7.30: 능력 블루프린트 캐시
    /// - 블루프린트의 실제 속성을 분석하여 능력 유형 결정
    /// - GUID/키워드 하드코딩 없이 자동 분류
    /// </summary>
    public static class BlueprintCache
    {
        // ========================================
        // 능력 유형 분류 (블루프린트 속성 기반)
        // ========================================

        public enum AbilityCategory
        {
            Unknown,
            Attack,         // 적 공격 (단일/다중)
            AoEAttack,      // AOE 공격
            Burst,          // 버스트 공격
            Movement,       // 이동 능력 (차지, 점프 등)
            Heal,           // 아군 치유
            Buff,           // 아군 버프
            Debuff,         // 적 디버프
            SelfBuff,       // 자기 버프
            HeroicAct,      // 영웅적 행동
            DesperateMeasure, // 절망적 수단
            Psychic,        // 사이커 능력
            Grenade,        // 수류탄
            Utility,        // 비전투 유틸리티
            MultiTarget,    // 다중 타겟 (2+ points)
        }

        public class AbilityBlueprintInfo
        {
            public string GUID { get; set; }
            public string BlueprintName { get; set; }
            public string DisplayName { get; set; }

            // ========================================
            // 블루프린트 직접 속성 (팩트)
            // ========================================
            public bool IsAoE { get; set; }
            public bool IsAoEDamage { get; set; }
            public bool IsBurst { get; set; }
            public bool IsMoveUnit { get; set; }
            public bool IsCharge { get; set; }
            public bool IsHeroicAct { get; set; }
            public bool IsDesperateMeasure { get; set; }
            public bool IsMomentum { get; set; }
            public bool IsPsykerAbility { get; set; }
            public bool IsWeaponAbility { get; set; }
            public bool IsSpell { get; set; }
            public bool IsGrenade { get; set; }
            public bool NotOffensive { get; set; }
            public bool HasMultiTarget { get; set; }

            // 타겟팅
            public bool CanTargetPoint { get; set; }
            public bool CanTargetSelf { get; set; }
            public bool CanTargetFriends { get; set; }
            public bool CanTargetEnemies { get; set; }

            // 효과
            public string EffectOnAlly { get; set; }    // Helpful, Harmful, None
            public string EffectOnEnemy { get; set; }   // Helpful, Harmful, None
            public string AoETargets { get; set; }      // Enemy, Ally, Any

            // 수치
            public string Range { get; set; }
            public int AoERadius { get; set; }
            public int ActionPointCost { get; set; }
            public int CooldownRounds { get; set; }

            // ========================================
            // 계산된 분류
            // ========================================
            public AbilityCategory Category { get; set; }
        }

        // GUID -> Info
        private static Dictionary<string, AbilityBlueprintInfo> _guidCache
            = new Dictionary<string, AbilityBlueprintInfo>(StringComparer.OrdinalIgnoreCase);

        // BlueprintName -> GUID
        private static Dictionary<string, string> _nameToGuidCache
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly object _lock = new object();

        // ========================================
        // 능력 분석 및 캐시
        // ========================================

        /// <summary>
        /// AbilityData를 분석하고 캐시에 추가
        /// </summary>
        public static AbilityBlueprintInfo CacheAbility(AbilityData ability)
        {
            if (ability?.Blueprint == null) return null;

            try
            {
                var bp = ability.Blueprint;
                string guid = bp.AssetGuid?.ToString();

                if (string.IsNullOrEmpty(guid)) return null;

                // 이미 캐시에 있으면 반환
                lock (_lock)
                {
                    if (_guidCache.TryGetValue(guid, out var existing))
                        return existing;
                }

                // 블루프린트 속성 직접 읽기
                var info = new AbilityBlueprintInfo
                {
                    GUID = guid,
                    BlueprintName = bp.name ?? "Unknown",
                    DisplayName = ability.Name ?? bp.Name ?? bp.name ?? "Unknown",

                    // 블루프린트 직접 속성 (팩트)
                    IsAoE = bp.IsAoE,
                    IsAoEDamage = bp.IsAoEDamage,
                    IsBurst = bp.IsBurst,
                    IsMoveUnit = bp.IsMoveUnit,
                    IsCharge = bp.IsCharge,
                    IsHeroicAct = bp.IsHeroicAct,
                    IsDesperateMeasure = bp.IsDesperateMeasure,
                    IsMomentum = bp.IsMomentum,
                    IsPsykerAbility = bp.IsPsykerAbility,
                    IsWeaponAbility = bp.IsWeaponAbility,
                    IsSpell = bp.IsSpell,
                    IsGrenade = bp.IsGrenade,
                    NotOffensive = bp.NotOffensive,
                    HasMultiTarget = bp.GetComponent<AbilityMultiTarget>() != null,

                    // 타겟팅
                    CanTargetPoint = bp.CanTargetPoint,
                    CanTargetSelf = bp.CanTargetSelf,
                    CanTargetFriends = bp.CanTargetFriends,
                    CanTargetEnemies = bp.CanTargetEnemies,

                    // 효과
                    EffectOnAlly = bp.EffectOnAlly.ToString(),
                    EffectOnEnemy = bp.EffectOnEnemy.ToString(),
                    AoETargets = bp.AoETargets.ToString(),

                    // 수치
                    Range = bp.Range.ToString(),
                    AoERadius = bp.AoERadius,
                    ActionPointCost = bp.ActionPointCost,
                    CooldownRounds = bp.CooldownRounds,
                };

                // 카테고리 계산
                info.Category = DetermineCategory(info);

                // 캐시에 추가
                lock (_lock)
                {
                    _guidCache[info.GUID] = info;
                    if (!string.IsNullOrEmpty(info.BlueprintName))
                        _nameToGuidCache[info.BlueprintName] = info.GUID;
                }

                return info;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[BlueprintCache] Error caching ability: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 블루프린트 속성을 기반으로 카테고리 결정
        /// </summary>
        private static AbilityCategory DetermineCategory(AbilityBlueprintInfo info)
        {
            // 우선순위 기반 분류

            // 1. MultiTarget (2+ point 타겟)
            if (info.HasMultiTarget)
                return AbilityCategory.MultiTarget;

            // 2. 영웅적 행동 / 절망적 수단
            if (info.IsHeroicAct)
                return AbilityCategory.HeroicAct;
            if (info.IsDesperateMeasure)
                return AbilityCategory.DesperateMeasure;

            // 3. 이동 능력
            if (info.IsMoveUnit || info.IsCharge)
                return AbilityCategory.Movement;

            // 4. 수류탄
            if (info.IsGrenade)
                return AbilityCategory.Grenade;

            // 5. 사이커 능력
            if (info.IsPsykerAbility)
                return AbilityCategory.Psychic;

            // 6. 비공격적 능력
            if (info.NotOffensive)
            {
                // 아군 힐/버프
                if (info.EffectOnAlly == "Helpful")
                {
                    if (info.CanTargetFriends && !info.CanTargetSelf)
                        return AbilityCategory.Buff;
                    if (info.CanTargetSelf)
                        return AbilityCategory.SelfBuff;
                }
                return AbilityCategory.Utility;
            }

            // 7. AOE 공격
            if (info.IsAoE || info.IsAoEDamage)
                return AbilityCategory.AoEAttack;

            // 8. 버스트 공격
            if (info.IsBurst)
                return AbilityCategory.Burst;

            // 9. 적 디버프 (적에게 해롭고 공격이 아닌 경우)
            if (info.EffectOnEnemy == "Harmful" && !info.IsWeaponAbility)
            {
                // 아군에게도 적용 가능하면 버프일 수 있음
                if (!info.CanTargetFriends)
                    return AbilityCategory.Debuff;
            }

            // 10. 아군 치유/버프
            if (info.EffectOnAlly == "Helpful")
            {
                if (info.CanTargetFriends)
                    return AbilityCategory.Buff;
                if (info.CanTargetSelf && !info.CanTargetEnemies)
                    return AbilityCategory.SelfBuff;
            }

            // 11. 기본 공격
            if (info.CanTargetEnemies && info.EffectOnEnemy == "Harmful")
                return AbilityCategory.Attack;

            return AbilityCategory.Unknown;
        }

        // ========================================
        // 조회 API
        // ========================================

        public static AbilityBlueprintInfo GetByGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            lock (_lock)
            {
                return _guidCache.TryGetValue(guid, out var info) ? info : null;
            }
        }

        public static AbilityBlueprintInfo GetByName(string blueprintName)
        {
            if (string.IsNullOrEmpty(blueprintName)) return null;
            lock (_lock)
            {
                if (_nameToGuidCache.TryGetValue(blueprintName, out var guid))
                    return GetByGuid(guid);
            }
            return null;
        }

        public static string GetGuidByName(string blueprintName)
        {
            if (string.IsNullOrEmpty(blueprintName)) return null;
            lock (_lock)
            {
                return _nameToGuidCache.TryGetValue(blueprintName, out var guid) ? guid : null;
            }
        }

        public static bool IsMultiTarget(string guidOrName)
        {
            var info = GetByGuid(guidOrName) ?? GetByName(guidOrName);
            return info?.HasMultiTarget == true;
        }

        public static List<AbilityBlueprintInfo> GetByCategory(AbilityCategory category)
        {
            lock (_lock)
            {
                return _guidCache.Values.Where(i => i.Category == category).ToList();
            }
        }

        public static List<AbilityBlueprintInfo> Search(string keyword)
        {
            if (string.IsNullOrEmpty(keyword)) return new List<AbilityBlueprintInfo>();
            lock (_lock)
            {
                return _guidCache.Values
                    .Where(info =>
                        (info.BlueprintName?.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (info.DisplayName?.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0))
                    .ToList();
            }
        }

        // ========================================
        // 파일 덤프
        // ========================================

        public static void DumpToFile(string filename = null)
        {
            try
            {
                if (string.IsNullOrEmpty(filename))
                {
                    string logFolder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low",
                        "Owlcat Games", "Warhammer 40000 Rogue Trader", "UnityModManager", "CompanionAI_v3");

                    Directory.CreateDirectory(logFolder);
                    filename = Path.Combine(logFolder, "AbilityBlueprints.txt");
                }

                List<AbilityBlueprintInfo> snapshot;
                lock (_lock)
                {
                    snapshot = _guidCache.Values.ToList();
                }

                var sb = new StringBuilder();
                sb.AppendLine($"=== CompanionAI v3 Ability Blueprint Cache ===");
                sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Total Cached: {snapshot.Count}");
                sb.AppendLine();

                // 카테고리별 통계
                sb.AppendLine("=== Category Statistics ===");
                foreach (AbilityCategory cat in Enum.GetValues(typeof(AbilityCategory)))
                {
                    int count = snapshot.Count(i => i.Category == cat);
                    if (count > 0)
                        sb.AppendLine($"  {cat}: {count}");
                }
                sb.AppendLine();

                // MultiTarget 먼저
                var multiTargets = snapshot.Where(i => i.HasMultiTarget).OrderBy(i => i.BlueprintName).ToList();
                sb.AppendLine($"=== MultiTarget Abilities ({multiTargets.Count}) ===");
                foreach (var info in multiTargets)
                {
                    WriteAbilityInfo(sb, info);
                }

                // 카테고리별 출력
                foreach (AbilityCategory cat in Enum.GetValues(typeof(AbilityCategory)))
                {
                    if (cat == AbilityCategory.MultiTarget) continue; // 이미 출력됨

                    var abilities = snapshot.Where(i => i.Category == cat && !i.HasMultiTarget)
                        .OrderBy(i => i.BlueprintName).ToList();
                    if (abilities.Count == 0) continue;

                    sb.AppendLine();
                    sb.AppendLine($"=== {cat} ({abilities.Count}) ===");
                    foreach (var info in abilities)
                    {
                        WriteAbilityInfo(sb, info);
                    }
                }

                File.WriteAllText(filename, sb.ToString());
                Main.Log($"[BlueprintCache] Dumped {snapshot.Count} abilities to: {filename}");
            }
            catch (Exception ex)
            {
                Main.Log($"[BlueprintCache] ERROR dumping to file: {ex.Message}");
            }
        }

        private static void WriteAbilityInfo(StringBuilder sb, AbilityBlueprintInfo info)
        {
            sb.AppendLine($"[{info.GUID}] {info.BlueprintName}");
            sb.AppendLine($"  DisplayName: {info.DisplayName}");
            sb.AppendLine($"  Category: {info.Category}");
            sb.AppendLine($"  Target: Point={info.CanTargetPoint}, Self={info.CanTargetSelf}, " +
                $"Friends={info.CanTargetFriends}, Enemies={info.CanTargetEnemies}");
            sb.AppendLine($"  Effects: OnAlly={info.EffectOnAlly}, OnEnemy={info.EffectOnEnemy}, AoETargets={info.AoETargets}");
            sb.AppendLine($"  Flags: AoE={info.IsAoE}, Burst={info.IsBurst}, Move={info.IsMoveUnit}, " +
                $"Charge={info.IsCharge}, Heroic={info.IsHeroicAct}, Offensive={!info.NotOffensive}");
            sb.AppendLine($"  Stats: Range={info.Range}, AoERadius={info.AoERadius}, AP={info.ActionPointCost}, CD={info.CooldownRounds}");
            sb.AppendLine();
        }

        // ========================================
        // 통계
        // ========================================

        public static int CacheCount
        {
            get { lock (_lock) { return _guidCache.Count; } }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _guidCache.Clear();
                _nameToGuidCache.Clear();
            }
        }
    }
}
