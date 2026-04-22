using System;
using Kingmaker;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;

namespace CompanionAI_v3.GameInterface
{
    public static partial class CombatAPI
    {
        #region Veil & Psychic - v3.6.0 Enhanced

        // ★ v3.6.0: Veil Degradation 상수
        public const int VEIL_WARNING_THRESHOLD = 10;   // 주의 시작
        public const int VEIL_DANGER_THRESHOLD = 15;    // 위험 (Major 차단)
        public const int VEIL_MAXIMUM = 20;             // 최대치

        /// <summary>
        /// 사이킥 안전 등급
        /// </summary>
        public enum PsychicSafetyLevel
        {
            Safe,       // 안전하게 사용 가능
            Caution,    // 주의 필요 (Veil 10~14)
            Dangerous,  // 위험 (예상 Veil이 15+ 도달)
            Blocked     // 차단 (현재 Veil이 이미 위험 수준)
        }

        /// <summary>
        /// 현재 Veil Thickness 값
        /// </summary>
        public static int GetVeilThickness()
        {
            try
            {
                return Game.Instance?.TurnController?.VeilThicknessCounter?.Value ?? 0;
            }
            // ★ v3.13.0: 안전한 기본값 — VEIL_DANGER_THRESHOLD (위험 가정 → 사이킥 차단)
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetVeilThickness failed: {ex.Message}");
                return VEIL_DANGER_THRESHOLD;
            }
        }

        /// <summary>
        /// 능력이 사이킥 파워인지 확인
        /// </summary>
        public static bool IsPsychicAbility(AbilityData ability)
        {
            if (ability == null) return false;
            try
            {
                return ability.Blueprint?.AbilityParamsSource == WarhammerAbilityParamsSource.PsychicPower;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsPsychicAbility failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.6.0: Major 사이킥 (Veil +3 이상) 여부
        /// Major: Perils of Warp 발생 가능, 더 강력한 효과
        /// </summary>
        public static bool IsMajorPsychicAbility(AbilityData ability)
        {
            if (ability == null) return false;
            try
            {
                var bp = ability.Blueprint;
                return bp != null && IsPsychicAbility(ability) && bp.VeilThicknessPointsToAdd >= 3;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsMajorPsychicAbility failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.6.0: Minor 사이킥 (Veil +1~2) 여부
        /// </summary>
        public static bool IsMinorPsychicAbility(AbilityData ability)
        {
            if (ability == null) return false;
            try
            {
                var bp = ability.Blueprint;
                if (bp == null || !IsPsychicAbility(ability)) return false;
                int veilAdd = bp.VeilThicknessPointsToAdd;
                return veilAdd > 0 && veilAdd < 3;
            }
            catch (Exception ex)
            {
                if (Main.IsDebugEnabled) Main.LogDebug($"[CombatAPI] IsMinorPsychicAbility failed for {ability?.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ★ v3.6.0: 사이킥 사용 시 Veil 증가량 (음수 = Veil 감소 스킬)
        /// </summary>
        public static int GetVeilIncrease(AbilityData ability)
        {
            if (ability == null || !IsPsychicAbility(ability)) return 0;
            try
            {
                return ability.Blueprint?.VeilThicknessPointsToAdd ?? 0;
            }
            // ★ v3.13.0: 안전한 기본값 — 3 (Major psychic 수준 → 사용 억제)
            catch (Exception ex)
            {
                Main.LogWarning($"[CombatAPI] GetVeilIncrease failed for {ability?.Name}: {ex.Message}");
                return 3;
            }
        }

        /// <summary>
        /// ★ v3.6.0: Veil을 낮추는 스킬인지 확인
        /// </summary>
        public static bool IsVeilReducingAbility(AbilityData ability)
        {
            return GetVeilIncrease(ability) < 0;
        }

        /// <summary>
        /// ★ v3.6.0: 사이킥 안전 등급 평가
        ///
        /// 로직:
        /// - Veil 감소 스킬: 항상 Safe (위험 상황에서 오히려 도움)
        /// - Major (Veil +3): 현재 Veil >= 15면 Blocked, 예상 >= 15면 Dangerous
        /// - Minor (Veil +1~2): 예상 Veil >= 20이면 Dangerous
        /// - Veil >= 10: Caution (경고만)
        /// </summary>
        public static PsychicSafetyLevel EvaluatePsychicSafety(AbilityData ability)
        {
            if (!IsPsychicAbility(ability))
                return PsychicSafetyLevel.Safe;

            int currentVeil = GetVeilThickness();
            int veilIncrease = GetVeilIncrease(ability);

            // Veil 감소 스킬은 항상 허용 (위험할수록 필요!)
            if (veilIncrease < 0)
                return PsychicSafetyLevel.Safe;

            int projectedVeil = currentVeil + veilIncrease;

            // Major 사이킥 (Veil +3+)
            if (IsMajorPsychicAbility(ability))
            {
                // 이미 위험 수준이면 차단 (Perils of Warp 위험)
                if (currentVeil >= VEIL_DANGER_THRESHOLD)
                    return PsychicSafetyLevel.Blocked;

                // 사용 후 위험 수준 도달 예상
                if (projectedVeil >= VEIL_DANGER_THRESHOLD)
                    return PsychicSafetyLevel.Dangerous;

                // 주의 수준
                if (currentVeil >= VEIL_WARNING_THRESHOLD)
                    return PsychicSafetyLevel.Caution;
            }
            // Minor 사이킥 (Veil +1~2)
            else
            {
                // 최대치 초과 예상시만 위험
                if (projectedVeil >= VEIL_MAXIMUM)
                    return PsychicSafetyLevel.Dangerous;

                // 위험 수준 도달 시 주의
                if (projectedVeil >= VEIL_DANGER_THRESHOLD)
                    return PsychicSafetyLevel.Caution;
            }

            return PsychicSafetyLevel.Safe;
        }

        /// <summary>
        /// ★ v3.6.0: 사이킥 사용 가능 여부 (Blocked만 차단)
        /// Caution/Dangerous는 허용하되 로그 출력
        /// </summary>
        public static bool IsPsychicSafeToUse(AbilityData ability)
        {
            if (!IsPsychicAbility(ability)) return true;

            var safety = EvaluatePsychicSafety(ability);
            int veil = GetVeilThickness();
            int veilAdd = GetVeilIncrease(ability);

            switch (safety)
            {
                case PsychicSafetyLevel.Blocked:
                    if (Main.IsDebugEnabled) Main.LogDebug($"[Veil] BLOCKED: {ability.Name} (Veil={veil}, +{veilAdd})");
                    return false;

                case PsychicSafetyLevel.Dangerous:
                    if (Main.IsDebugEnabled) Main.LogDebug($"[Veil] DANGEROUS but allowed: {ability.Name} (Veil={veil}→{veil + veilAdd})");
                    return true;  // 위험하지만 허용 (Minor는 사용 가능하도록)

                case PsychicSafetyLevel.Caution:
                    if (Main.IsDebugEnabled) Main.LogDebug($"[Veil] Caution: {ability.Name} (Veil={veil})");
                    return true;

                default:
                    return true;
            }
        }

        /// <summary>
        /// ★ v3.6.0: Veil 상태 문자열 (로그/디버그용)
        /// </summary>
        public static string GetVeilStatusString()
        {
            int veil = GetVeilThickness();
            string status = veil >= VEIL_DANGER_THRESHOLD ? "DANGER" :
                           veil >= VEIL_WARNING_THRESHOLD ? "WARNING" : "SAFE";
            return $"Veil={veil}/{VEIL_MAXIMUM} ({status})";
        }

        #endregion
    }
}
