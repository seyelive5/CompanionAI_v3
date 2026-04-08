// Planning/LLM/AbilityEffectExtractor.cs
// ★ Skill Effect Awareness: AbilityFlags + Timing → 짧은 영어 효과 라벨
// LLM 프롬프트에 주입되어 스킬 부가효과(Run and Gun의 보너스 MP 등)를 인식시킴
using System.Text;
using CompanionAI_v3.Data;
using Kingmaker.UnitLogic.Abilities.Blueprints;

namespace CompanionAI_v3.Planning.LLM
{
    /// <summary>
    /// AbilityInfo / BlueprintAbility 메타데이터를 LLM-친화적 영어 라벨로 변환.
    /// 순수 함수 — 게임 API 호출 없음, 캐시 가능.
    /// 결과는 AbilityEffectCache에 저장됨.
    /// </summary>
    public static class AbilityEffectExtractor
    {
        private const int MAX_LABEL_LENGTH = 60;

        /// <summary>
        /// AbilityInfo (hand-curated DB 항목) → 효과 라벨.
        /// Timing이 base, Flags가 modifier 역할.
        /// </summary>
        public static string ExtractFromInfo(AbilityDatabase.AbilityInfo info)
        {
            if (info == null) return "";

            var sb = new StringBuilder(64);

            // 1. Timing → base label
            string timingLabel = TimingToLabel(info.Timing);
            if (!string.IsNullOrEmpty(timingLabel))
                sb.Append(timingLabel);

            // 2. Flags → modifier suffixes (timing-aware to avoid redundancy)
            AppendFlagModifiers(sb, info.Flags, info.Timing);

            return Truncate(sb.ToString());
        }

        /// <summary>
        /// BlueprintAbility (게임 데이터) → 효과 라벨.
        /// AbilityDatabase에 없는 ability용 폴백.
        /// 게임 API 속성 (range, AbilityTags 등)에서 직접 추출.
        /// </summary>
        public static string ExtractFromBlueprint(Kingmaker.UnitLogic.Abilities.Blueprints.BlueprintAbility ability)
        {
            if (ability == null) return "";

            var sb = new StringBuilder(64);

            try
            {
                // AbilityTag 기반 분류 (가장 신뢰할 수 있는 게임 API)
                // 주: 이 게임 버전의 enum은 Kingmaker.UnitLogic.Abilities.Blueprints 네임스페이스에 위치
                //     사용 가능 값: None, Heal, ThrowingGrenade, UsingCombatDrug, StarshipShotAbility, StarshipUltimateAbility, Trap
                var tag = ability.AbilityTag;
                if (tag != Kingmaker.UnitLogic.Abilities.Blueprints.AbilityTag.None)
                {
                    string tagLabel = TagToLabel(tag);
                    if (!string.IsNullOrEmpty(tagLabel))
                        sb.Append(tagLabel);
                }

                // Range 정보 (있으면)
                if (ability.Range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Personal)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append("self");
                }
                else if (ability.Range == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityRange.Touch)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append("melee range");
                }

                // Free action (BlueprintAbility.IsFreeAction)
                // 주: 이 게임 버전에는 BlueprintAbility.ActionType 속성이 없음, IsFreeAction bool 사용
                if (ability.IsFreeAction)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append("free action");
                }
            }
            catch
            {
                // Game API 호출 실패 시 빈 라벨 반환 (안전 폴백)
                return "";
            }

            return Truncate(sb.ToString());
        }

        private static string TagToLabel(Kingmaker.UnitLogic.Abilities.Blueprints.AbilityTag tag)
        {
            switch (tag)
            {
                case Kingmaker.UnitLogic.Abilities.Blueprints.AbilityTag.Heal:
                    return "heals ally";
                // TODO: AbilityTag.Damage not in this game version
                // case Kingmaker.UnitLogic.Abilities.Blueprints.AbilityTag.Damage:
                //     return "deals damage";
                // TODO: AbilityTag.Buff not in this game version
                // case Kingmaker.UnitLogic.Abilities.Blueprints.AbilityTag.Buff:
                //     return "buff effect";
                // TODO: AbilityTag.Debuff not in this game version
                // case Kingmaker.UnitLogic.Abilities.Blueprints.AbilityTag.Debuff:
                //     return "debuff effect";
                case Kingmaker.UnitLogic.Abilities.Blueprints.AbilityTag.ThrowingGrenade:
                    return "grenade — thrown explosive";
                case Kingmaker.UnitLogic.Abilities.Blueprints.AbilityTag.UsingCombatDrug:
                    return "combat drug — temporary boost";
                case Kingmaker.UnitLogic.Abilities.Blueprints.AbilityTag.Trap:
                    return "trap — placed device";
                default:
                    return "";
            }
        }

        // ═══════════════════════════════════════════════════════════
        // Timing 변환
        // ═══════════════════════════════════════════════════════════

        private static string TimingToLabel(AbilityTiming timing)
        {
            switch (timing)
            {
                case AbilityTiming.PostFirstAction:
                    return "bonus action — use after attacking";
                case AbilityTiming.PreCombatBuff:
                    return "pre-combat — use before engaging";
                case AbilityTiming.PreAttackBuff:
                    return "pre-attack buff — use before shooting";
                case AbilityTiming.TurnEnding:
                    return "ends turn — use last";
                case AbilityTiming.Finisher:
                    return "finisher — use on low-HP enemies";
                case AbilityTiming.GapCloser:
                    return "gap closer — closes distance";
                case AbilityTiming.HeroicAct:
                    return "heroic act — needs Momentum 175+";
                case AbilityTiming.DesperateMeasure:
                    return "desperate measure — needs Momentum 25";
                case AbilityTiming.Reload:
                    return "reload weapon";
                case AbilityTiming.Taunt:
                    return "taunt — pulls enemy attacks";
                case AbilityTiming.Healing:
                    return "heals ally";
                case AbilityTiming.Debuff:
                    return "debuff — apply before attacking";
                case AbilityTiming.Emergency:
                    return "emergency — use when low HP";
                case AbilityTiming.SelfDamage:
                    return "self-damage — costs HP";
                case AbilityTiming.DangerousAoE:
                    return "AoE — ⚠ may hit allies";
                case AbilityTiming.RighteousFury:
                    return "righteous fury — after killing enemy";
                case AbilityTiming.DOTIntensify:
                    return "intensifies damage-over-time";
                case AbilityTiming.ChainEffect:
                    return "chain effect";
                case AbilityTiming.PositionalBuff:
                    return "positional buff — frontline/support/rear zone";
                case AbilityTiming.Stratagem:
                    return "tactic zone enhancement";
                case AbilityTiming.Marker:
                    return "marks target — no damage";
                case AbilityTiming.CrowdControl:
                    return "crowd control — stun/paralysis";
                case AbilityTiming.Grenade:
                    return "grenade — thrown explosive";
                default:
                    return ""; // Normal: no special timing label
            }
        }

        // ═══════════════════════════════════════════════════════════
        // Flag 변환 (suffix로 추가)
        // ═══════════════════════════════════════════════════════════

        private static void AppendFlagModifiers(StringBuilder sb, AbilityFlags flags, AbilityTiming timing)
        {
            if (flags == AbilityFlags.None) return;

            // Suppress redundant flags when the timing already conveys the same info
            bool suppressAoE = (timing == AbilityTiming.DangerousAoE);
            bool suppressDangerous = (timing == AbilityTiming.DangerousAoE);
            bool suppressCC = (timing == AbilityTiming.CrowdControl);
            bool suppressDOT = (timing == AbilityTiming.DOTIntensify);

            // 가장 중요한 플래그만 선택 (토큰 절약)
            if ((flags & AbilityFlags.IsRetreatCapable) != 0)
                Append(sb, "grants retreat movement");

            if ((flags & AbilityFlags.IsCautiousApproach) != 0)
                Append(sb, "defensive stance");

            if ((flags & AbilityFlags.IsConfidentApproach) != 0)
                Append(sb, "aggressive stance");

            if ((flags & AbilityFlags.IsDefensiveBuff) != 0)
                Append(sb, "+defense");

            if ((flags & AbilityFlags.IsOffensiveBuff) != 0)
                Append(sb, "+offense");

            if (!suppressCC && (flags & AbilityFlags.HasCC) != 0)
                Append(sb, "stuns/paralyzes");

            if (!suppressDOT && (flags & AbilityFlags.HasDOT) != 0)
                Append(sb, "damage over time");

            if ((flags & AbilityFlags.IsBurst) != 0)
                Append(sb, "burst fire");

            if ((flags & AbilityFlags.IsScatter) != 0)
                Append(sb, "scatter");

            if ((flags & AbilityFlags.IsMelee) != 0)
                Append(sb, "melee");

            if (!suppressAoE && (flags & AbilityFlags.IsAoE) != 0)
                Append(sb, "AoE");

            if (!suppressDangerous && (flags & AbilityFlags.Dangerous) != 0)
                Append(sb, "⚠ may hit allies");

            if ((flags & AbilityFlags.IsFreeAction) != 0)
                Append(sb, "free action");

            if ((flags & AbilityFlags.OncePerTurn) != 0)
                Append(sb, "1/turn");

            if ((flags & AbilityFlags.SingleUse) != 0)
                Append(sb, "1/combat");
        }

        private static void Append(StringBuilder sb, string text)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(text);
        }

        private static string Truncate(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length <= MAX_LABEL_LENGTH) return s;
            return s.Substring(0, MAX_LABEL_LENGTH - 3) + "...";
        }
    }
}
