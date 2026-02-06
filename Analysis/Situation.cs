using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Enums;
using Kingmaker.UnitLogic.Abilities;
using UnityEngine;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Analysis
{
    /// <summary>
    /// 현재 전투 상황 스냅샷
    /// SituationAnalyzer가 생성하고, TurnPlanner가 소비
    /// </summary>
    public class Situation
    {
        #region Unit State

        /// <summary>현재 유닛</summary>
        public BaseUnitEntity Unit { get; set; }

        /// <summary>HP 퍼센트</summary>
        public float HPPercent { get; set; }

        /// <summary>현재 AP</summary>
        public float CurrentAP { get; set; }

        /// <summary>현재 MP</summary>
        public float CurrentMP { get; set; }

        /// <summary>이동 가능 여부</summary>
        public bool CanMove { get; set; }

        /// <summary>행동 가능 여부</summary>
        public bool CanAct { get; set; }

        #endregion

        #region Settings

        /// <summary>유닛 설정</summary>
        public CharacterSettings CharacterSettings { get; set; }

        /// <summary>거리 선호도</summary>
        public RangePreference RangePreference { get; set; }

        /// <summary>안전 거리</summary>
        public float MinSafeDistance { get; set; }

        #endregion

        #region Weapon & Ammo

        /// <summary>재장전 필요 여부</summary>
        public bool NeedsReload { get; set; }

        /// <summary>원거리 무기 보유</summary>
        public bool HasRangedWeapon { get; set; }

        /// <summary>근접 무기 보유</summary>
        public bool HasMeleeWeapon { get; set; }

        /// <summary>현재 탄약</summary>
        public int CurrentAmmo { get; set; }

        /// <summary>최대 탄약</summary>
        public int MaxAmmo { get; set; }

        #endregion

        #region Battlefield

        /// <summary>모든 적</summary>
        public List<BaseUnitEntity> Enemies { get; set; } = new List<BaseUnitEntity>();

        /// <summary>모든 아군</summary>
        public List<BaseUnitEntity> Allies { get; set; } = new List<BaseUnitEntity>();

        /// <summary>가장 가까운 적과의 거리</summary>
        public float NearestEnemyDistance { get; set; }

        /// <summary>가장 가까운 적</summary>
        public BaseUnitEntity NearestEnemy { get; set; }

        /// <summary>가장 부상당한 아군</summary>
        public BaseUnitEntity MostWoundedAlly { get; set; }

        #endregion

        #region Target Analysis

        /// <summary>현재 위치에서 공격 가능한 적</summary>
        public List<BaseUnitEntity> HittableEnemies { get; set; } = new List<BaseUnitEntity>();

        /// <summary>★ v3.8.14: 근접 공격으로 공격 가능한 적 (폴백 제외)</summary>
        public List<BaseUnitEntity> MeleeHittableEnemies { get; set; } = new List<BaseUnitEntity>();

        /// <summary>최적 타겟</summary>
        public BaseUnitEntity BestTarget { get; set; }

        /// <summary>최적 타겟 처치 가능 여부</summary>
        public bool CanKillBestTarget { get; set; }

        #endregion

        #region Threat Analysis (v3.1.25)

        /// <summary>★ v3.1.25: 타겟팅 당하는 아군 수 (자신 제외)</summary>
        public int AlliesUnderThreat { get; set; }

        /// <summary>★ v3.1.25: 아군(자신 제외)을 타겟팅 중인 적 수</summary>
        public int EnemiesTargetingAllies { get; set; }

        #endregion

        #region Position Analysis

        /// <summary>위험 상태 (원거리인데 적이 가까움)</summary>
        public bool IsInDanger { get; set; }

        /// <summary>엄폐 확보 여부</summary>
        public bool HasCover { get; set; }

        /// <summary>더 나은 위치 존재</summary>
        public bool BetterPositionAvailable { get; set; }

        /// <summary>이동 필요 (공격 불가)</summary>
        public bool NeedsReposition { get; set; }

        /// <summary>★ v3.2.00: 전장 영향력 맵</summary>
        public BattlefieldInfluenceMap InfluenceMap { get; set; }

        /// <summary>★ v3.4.00: 예측적 위협 맵</summary>
        public PredictiveThreatMap PredictiveThreatMap { get; set; }

        #endregion

        #region Influence Map Helpers (v3.2.00)

        /// <summary>특정 위치의 적 위협도 조회</summary>
        public float GetThreatAtPosition(UnityEngine.Vector3 pos)
        {
            return InfluenceMap?.GetThreatAt(pos) ?? 0f;
        }

        /// <summary>특정 위치의 아군 통제력 조회</summary>
        public float GetControlAtPosition(UnityEngine.Vector3 pos)
        {
            return InfluenceMap?.GetControlAt(pos) ?? 0f;
        }

        /// <summary>전선까지의 거리 (양수=적 방향)</summary>
        public float GetFrontlineDistance(UnityEngine.Vector3 pos)
        {
            return InfluenceMap?.GetFrontlineDistance(pos) ?? 0f;
        }

        /// <summary>팀 전체 위협도 (0-1)</summary>
        public float TeamThreatLevel => InfluenceMap?.TeamThreatLevel ?? 0f;

        #endregion

        #region Predictive Threat Helpers (v3.4.00)

        /// <summary>★ v3.4.00: 특정 위치의 예측 위협도 조회 (0-1)</summary>
        public float GetPredictedThreatAt(UnityEngine.Vector3 pos)
        {
            return PredictiveThreatMap?.GetPredictedThreatAt(pos) ?? 0f;
        }

        /// <summary>★ v3.4.00: 특정 위치가 다음 턴에도 안전할지 확인</summary>
        public bool IsPositionSafeNextTurn(UnityEngine.Vector3 pos)
        {
            return PredictiveThreatMap?.IsPositionSafeNextTurn(pos) ?? true;
        }

        /// <summary>★ v3.4.00: 턴 안전도 점수 (0=위험, 1=안전)</summary>
        public float GetTurnSafetyScore(UnityEngine.Vector3 pos)
        {
            return PredictiveThreatMap?.GetTurnSafetyScore(pos) ?? 0.5f;
        }

        #endregion

        #region Available Abilities (분류됨)

        /// <summary>사용 가능한 버프</summary>
        public List<AbilityData> AvailableBuffs { get; set; } = new List<AbilityData>();

        /// <summary>사용 가능한 공격</summary>
        public List<AbilityData> AvailableAttacks { get; set; } = new List<AbilityData>();

        /// <summary>사용 가능한 힐</summary>
        public List<AbilityData> AvailableHeals { get; set; } = new List<AbilityData>();

        /// <summary>사용 가능한 디버프</summary>
        public List<AbilityData> AvailableDebuffs { get; set; } = new List<AbilityData>();

        /// <summary>사용 가능한 특수 능력 (DoT 강화, 연쇄 등)</summary>
        public List<AbilityData> AvailableSpecialAbilities { get; set; } = new List<AbilityData>();

        /// <summary>★ v3.0.21: 위치 타겟 버프 (전방/보조/후방 구역 등)</summary>
        public List<AbilityData> AvailablePositionalBuffs { get; set; } = new List<AbilityData>();

        /// <summary>★ v3.0.23: 구역 강화 스킬 (Stratagem) - Combat Tactics 구역 강화</summary>
        public List<AbilityData> AvailableStratagems { get; set; } = new List<AbilityData>();

        /// <summary>★ v3.0.33: 마킹 스킬 (공격 전 적 지정 - 보너스 획득)</summary>
        public List<AbilityData> AvailableMarkers { get; set; } = new List<AbilityData>();

        /// <summary>재장전 능력</summary>
        public AbilityData ReloadAbility { get; set; }

        /// <summary>주 공격 능력</summary>
        public AbilityData PrimaryAttack { get; set; }

        /// <summary>최적 버프</summary>
        public AbilityData BestBuff { get; set; }

        /// <summary>Run and Gun 능력</summary>
        public AbilityData RunAndGunAbility { get; set; }

        #endregion

        #region Turn State

        /// <summary>이번 턴 첫 행동 완료 여부</summary>
        public bool HasPerformedFirstAction { get; set; }

        /// <summary>이번 턴 버프 사용 여부</summary>
        public bool HasBuffedThisTurn { get; set; }

        /// <summary>이번 턴 공격 완료 여부</summary>
        public bool HasAttackedThisTurn { get; set; }

        /// <summary>이번 턴 힐 사용 여부</summary>
        public bool HasHealedThisTurn { get; set; }

        /// <summary>이번 턴 재장전 사용 여부</summary>
        public bool HasReloadedThisTurn { get; set; }

        /// <summary>★ v3.0.2: 이번 턴 이동 완료 여부 (중복 이동 방지)</summary>
        public bool HasMovedThisTurn { get; set; }

        /// <summary>★ v3.0.3: 이번 턴 이동 횟수</summary>
        public int MoveCount { get; set; }

        /// <summary>★ v3.0.3: 공격 후 추가 이동 허용 (이동→공격 후 Hittable=0이면 허용)</summary>
        public bool AllowPostAttackMove { get; set; }

        /// <summary>★ v3.0.7: 추격 이동 허용 (이동했지만 공격 못함, 적이 아직 멀리 있음)</summary>
        public bool AllowChaseMove { get; set; }

        #endregion

        #region Computed Properties

        /// <summary>공격 가능한 적이 있는가?</summary>
        public bool HasHittableEnemies => HittableEnemies?.Count > 0;

        /// <summary>★ v3.8.14: 근접 공격으로 공격 가능한 적이 있는가? (폴백 제외)</summary>
        public bool HasMeleeHittableEnemies => MeleeHittableEnemies?.Count > 0;

        /// <summary>살아있는 적이 있는가?</summary>
        public bool HasLivingEnemies => Enemies?.Count > 0;

        /// <summary>HP가 위험한가? (30% 미만)</summary>
        public bool IsHPCritical => HPPercent < 30f;

        /// <summary>HP가 낮은가? (50% 미만)</summary>
        public bool IsHPLow => HPPercent < 50f;

        /// <summary>
        /// 원거리 선호인가?
        /// ★ v3.6.5: Adaptive 모드에서 무기 타입 자동 감지
        /// - PreferRanged → true
        /// - PreferMelee → false
        /// - Adaptive → HasRangedWeapon 기준 (원거리 무기 보유 시 원거리 선호)
        /// </summary>
        public bool PrefersRanged =>
            RangePreference == RangePreference.PreferRanged ||
            (RangePreference == RangePreference.Adaptive && HasRangedWeapon);

        #endregion

        #region Familiar Support (v3.7.00)

        /// <summary>★ v3.7.00: 사역마 소유 여부</summary>
        public bool HasFamiliar { get; set; }

        /// <summary>★ v3.7.00: 사역마 유닛</summary>
        public BaseUnitEntity Familiar { get; set; }

        /// <summary>★ v3.7.00: 사역마 타입</summary>
        public PetType? FamiliarType { get; set; }

        /// <summary>★ v3.7.00: 사역마 현재 위치</summary>
        public Vector3 FamiliarPosition { get; set; }

        /// <summary>★ v3.7.00: 사역마 최적 위치</summary>
        public FamiliarPositioner.PositionScore OptimalFamiliarPosition { get; set; }

        /// <summary>★ v3.7.00: 사역마 관련 능력 목록 (Relocate, Keystone 등)</summary>
        public List<AbilityData> FamiliarAbilities { get; set; } = new List<AbilityData>();

        /// <summary>★ v3.7.00: 사역마 Relocate 필요 여부</summary>
        public bool NeedsFamiliarRelocate { get; set; }

        /// <summary>★ v3.7.00: 이 유닛이 사역마인지 (턴 스킵용)</summary>
        public bool IsFamiliarUnit { get; set; }

        #endregion

        public override string ToString()
        {
            // ★ v3.0.52: MP 추가 (이동 디버깅용)
            return $"[Situation] {Unit?.CharacterName}: HP={HPPercent:F0}%, AP={CurrentAP:F1}, MP={CurrentMP:F1}, " +
                   $"Enemies={Enemies?.Count ?? 0}, Hittable={HittableEnemies?.Count ?? 0}, " +
                   $"InDanger={IsInDanger}, NeedsReload={NeedsReload}";
        }
    }
}
