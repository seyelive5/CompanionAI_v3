using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Items;  // ★ v3.9.72: ItemEntityWeapon
using Kingmaker.View.Mechadendrites;  // ★ v3.9.78: HasMechadendrites
using Kingmaker.UnitLogic.Abilities;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Data
{
    /// <summary>
    /// ★ v3.9.72: 양쪽 무기 세트 분석 — 무기 아이템 직접 읽기 (임시 전환 없음)
    ///
    /// 핵심 설계:
    /// - HandsEquipmentSets에서 무기 아이템을 직접 읽어 정보 수집 (이름, 타입)
    /// - 능력(AbilityData)은 수집하지 않음 — 비활성 세트의 능력은 Fact가 비활성이므로 사용 불가
    /// - 실제 무기 전환 후, TurnOrchestrator의 자연 re-analysis 사이클에서 새 능력 자동 수집
    ///
    /// 이전 설계(임시 전환)의 문제:
    /// - unit.Body.CurrentHandEquipmentSetIndex 직접 변경 시 UpdateActive() 호출됨
    /// - 원복 후에도 무기 Fact.Active 상태 복원이 불완전 → IsRestricted=true 전파
    /// </summary>
    public static class WeaponSetAnalyzer
    {
        /// <summary>
        /// 한 무기 세트의 분석 결과
        /// </summary>
        public class WeaponSetAbilities
        {
            public int SetIndex { get; set; }
            public List<AbilityData> AttackAbilities { get; set; } = new List<AbilityData>();
            public List<AbilityData> AoEAbilities { get; set; } = new List<AbilityData>();
            public bool HasRangedWeapon { get; set; }
            public bool HasMeleeWeapon { get; set; }
            public string PrimaryWeaponName { get; set; }

            /// <summary>★ v3.9.74: 주 무기 공격 사거리 (타일 단위, 0이면 미장착)</summary>
            public float PrimaryWeaponRange { get; set; }

            /// <summary>무기 존재 여부 (원거리 또는 근거리)</summary>
            public bool HasWeapons => HasRangedWeapon || HasMeleeWeapon;
        }

        // ★ 정적 버퍼 — GC 할당 방지
        private static readonly WeaponSetAbilities[] _cachedResults = new WeaponSetAbilities[2]
        {
            new WeaponSetAbilities(),
            new WeaponSetAbilities()
        };

        /// <summary>
        /// 양쪽 무기 세트의 무기 정보 분석
        ///
        /// ★ v3.9.72 리팩토링: 임시 무기 전환 제거
        /// - HandsEquipmentSets에서 무기 아이템을 직접 읽음
        /// - 능력(AbilityData)은 수집하지 않음 (비활성 세트 능력은 Fact 비활성)
        /// - 실제 전환 후 TurnOrchestrator re-analysis 사이클에서 능력 자동 수집
        /// </summary>
        public static WeaponSetAbilities[] AnalyzeBothSets(BaseUnitEntity unit)
        {
            if (unit?.Body?.HandsEquipmentSets == null || unit.Body.HandsEquipmentSets.Count < 2)
                return null;

            int currentSet = unit.Body.CurrentHandEquipmentSetIndex;

            // 결과 초기화
            for (int i = 0; i < 2; i++)
            {
                _cachedResults[i].SetIndex = i;
                _cachedResults[i].AttackAbilities.Clear();
                _cachedResults[i].AoEAbilities.Clear();
                _cachedResults[i].HasRangedWeapon = false;
                _cachedResults[i].HasMeleeWeapon = false;
                _cachedResults[i].PrimaryWeaponName = null;
                _cachedResults[i].PrimaryWeaponRange = 0;
            }

            try
            {
                var sets = unit.Body.HandsEquipmentSets;

                for (int setIndex = 0; setIndex < 2; setIndex++)
                {
                    if (setIndex >= sets.Count) break;

                    var result = _cachedResults[setIndex];
                    var equipSet = sets[setIndex];
                    if (equipSet == null) continue;

                    // 무기 아이템 직접 읽기 (임시 전환 없음)
                    // ★ MaybeItem 사용 — MaybeWeapon은 비활성 세트에서 Active 체크로 null 반환
                    var primaryWeapon = equipSet.PrimaryHand?.MaybeItem as ItemEntityWeapon;
                    var secondaryWeapon = equipSet.SecondaryHand?.MaybeItem as ItemEntityWeapon;

                    result.PrimaryWeaponName = primaryWeapon?.Name;

                    // 원거리/근거리 판정 + 사거리
                    if (primaryWeapon != null)
                    {
                        result.PrimaryWeaponRange = primaryWeapon.AttackRange;  // ★ v3.9.74: 타일 단위 사거리
                        if (primaryWeapon.Blueprint.IsMelee)
                            result.HasMeleeWeapon = true;
                        else
                            result.HasRangedWeapon = true;
                    }
                    if (secondaryWeapon != null)
                    {
                        if (secondaryWeapon.Blueprint.IsMelee)
                            result.HasMeleeWeapon = true;
                        else
                            result.HasRangedWeapon = true;
                    }

                    if (Main.IsDebugEnabled)
                        Main.LogDebug($"[WeaponSetAnalyzer] Set {setIndex} ({result.PrimaryWeaponName ?? "empty"}): " +
                            $"Ranged={result.HasRangedWeapon}, Melee={result.HasMeleeWeapon}, Range={result.PrimaryWeaponRange:F0}" +
                            (setIndex == currentSet ? " [ACTIVE]" : " [INACTIVE]"));
                }
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[WeaponSetAnalyzer] Error analyzing sets: {ex.Message}");
                return null;
            }

            return _cachedResults;
        }

        /// <summary>
        /// 무기 세트 전환이 유익한지 판단
        /// 조건:
        /// 1. EnableWeaponSetRotation 설정 ON
        /// 2. 양쪽 세트에 무기 장착
        /// 3. 양쪽 세트 무기가 다름
        /// 4. 대체 세트에 무기 존재
        /// </summary>
        public static bool ShouldConsiderWeaponSwitch(BaseUnitEntity unit, Situation situation)
        {
            if (unit == null || situation == null) return false;

            // 설정 체크
            var charSettings = situation.CharacterSettings;
            if (charSettings?.EnableWeaponSetRotation != true) return false;

            // ★ v3.9.78: 메카덴드라이트 유닛은 무기 전환 불가 (게임 제한)
            try { if (unit.HasMechadendrites()) return false; }
            catch { /* 확장 메서드 접근 실패 시 무시 */ }

            // 양쪽 세트 장착 확인
            if (!CombatAPI.HasMultipleWeaponSets(unit)) return false;

            // 같은 무기면 전환 의미 없음
            if (CombatAPI.AreBothWeaponSetsSame(unit)) return false;

            // 전환 횟수 상한 체크
            var rotationConfig = AIConfig.GetWeaponRotationConfig();
            var turnState = Core.TurnOrchestrator.Instance?.GetCurrentTurnState();
            if (turnState != null && turnState.WeaponSwitchCount >= rotationConfig.MaxSwitchesPerTurn)
                return false;

            // 대체 세트에 무기가 있는지 (능력이 아닌 무기 존재 확인)
            int alternateIndex = CombatAPI.GetCurrentWeaponSetIndex(unit) == 0 ? 1 : 0;
            if (situation.WeaponSetData == null || situation.WeaponSetData.Length <= alternateIndex)
                return false;

            var alternateSet = situation.WeaponSetData[alternateIndex];
            return alternateSet.HasWeapons;
        }
    }
}
