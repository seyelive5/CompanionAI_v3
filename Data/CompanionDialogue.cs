using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using UnityEngine;
using CompanionAI_v3.Core;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Data
{
    /// <summary>
    /// ★ v3.9.32: AI Speech — 동료 캐릭터 전술 대사 시스템
    /// 전투 중 AI가 행동을 결정하면 캐릭터 개성에 맞는 대사를 말풍선으로 표시
    /// BarkPlayer.Bark()를 사용하여 말풍선 + 전투 로그에 기록
    /// </summary>
    public static class CompanionDialogue
    {
        #region Enums

        public enum CompanionId
        {
            Unknown,
            Abelard,
            Heinrix,
            Argenta,
            Pasqal,
            Idira,
            Cassia,
            Yrliet,
            Jae,
            Marazhai,
            Ulfar,
            Kibellah,   // DLC
            Solomorne   // DLC
        }

        public enum SpeechCategory
        {
            Attack,
            MoveAndAttack,
            Heal,
            Buff,
            Support,
            Retreat,
            Reload,
            Taunt,
            EndTurn,  // Silent — no speech
            Victory   // ★ v3.9.80: 전투 승리 환호
        }

        #endregion

        #region Character Identification

        /// <summary>
        /// Blueprint.name 패턴 → CompanionId 매핑
        /// Blueprint.name은 Unity 내부 에셋 이름 (비로컬라이즈, 안정적)
        /// </summary>
        private static readonly (string pattern, CompanionId id)[] CompanionPatterns = new[]
        {
            ("Abelard",   CompanionId.Abelard),
            ("Heinrix",   CompanionId.Heinrix),
            ("Argenta",   CompanionId.Argenta),
            ("Pascal",    CompanionId.Pasqal),
            ("Idira",     CompanionId.Idira),
            ("Cassia",    CompanionId.Cassia),
            ("Yrliet",    CompanionId.Yrliet),
            ("Jae",       CompanionId.Jae),
            ("Marazhai",  CompanionId.Marazhai),
            ("Ulfar",     CompanionId.Ulfar),
            ("Kibellah",  CompanionId.Kibellah),
            ("Solomorne", CompanionId.Solomorne),
        };

        /// <summary>캐시: unitId → CompanionId (한 번 감지하면 재사용)</summary>
        private static readonly Dictionary<string, CompanionId> _companionCache
            = new Dictionary<string, CompanionId>();

        /// <summary>
        /// 유닛의 Blueprint.name에서 동료 캐릭터 식별
        /// FamiliarAbilities.cs와 동일한 Blueprint.name 패턴 매칭 방식
        /// </summary>
        public static CompanionId IdentifyCompanion(BaseUnitEntity unit)
        {
            if (unit == null) return CompanionId.Unknown;

            string unitId = unit.UniqueId;
            if (_companionCache.TryGetValue(unitId, out var cached))
                return cached;

            string bpName = unit.Blueprint?.name ?? "";
            CompanionId result = CompanionId.Unknown;

            for (int i = 0; i < CompanionPatterns.Length; i++)
            {
                if (bpName.IndexOf(CompanionPatterns[i].pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result = CompanionPatterns[i].id;
                    break;
                }
            }

            // ★ 진단 로그: Blueprint.name → CompanionId 매핑 결과
            if (result == CompanionId.Unknown)
                Main.LogDebug($"[Speech] Unknown companion: CharName=\"{unit.CharacterName}\", Blueprint=\"{bpName}\"");
            else
                Main.LogDebug($"[Speech] Identified: CharName=\"{unit.CharacterName}\", Blueprint=\"{bpName}\" → {result}");

            _companionCache[unitId] = result;
            return result;
        }

        #endregion

        #region Speech Category Determination

        /// <summary>
        /// TurnPlan의 Priority와 첫 액션 타입에서 SpeechCategory 결정
        /// </summary>
        public static SpeechCategory DetermineSpeechCategory(TurnPlan plan)
        {
            if (plan == null) return SpeechCategory.EndTurn;

            var priority = plan.Priority;
            var firstAction = plan.PeekNextAction();
            var firstType = firstAction?.Type ?? ActionType.EndTurn;

            // Priority 기반 (최우선)
            switch (priority)
            {
                case TurnPriority.Emergency:
                    return SpeechCategory.Heal;
                case TurnPriority.Retreat:
                    return SpeechCategory.Retreat;
                case TurnPriority.Reload:
                    return SpeechCategory.Reload;
                case TurnPriority.EndTurn:
                    return SpeechCategory.EndTurn;
            }

            // 첫 액션 타입 기반
            switch (firstType)
            {
                case ActionType.Heal:
                    return SpeechCategory.Heal;
                case ActionType.Buff:
                    // Taunt 감지: 능력명에 Taunt/Provoke 포함 시
                    if (firstAction?.Ability != null)
                    {
                        string abilityName = firstAction.Ability.Blueprint?.name ?? "";
                        if (abilityName.IndexOf("Taunt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            abilityName.IndexOf("Provoke", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return SpeechCategory.Taunt;
                        }
                    }
                    return SpeechCategory.Buff;
                case ActionType.Support:
                    return SpeechCategory.Support;
                case ActionType.EndTurn:
                    return SpeechCategory.EndTurn;
            }

            // 남은 Priority 기반
            switch (priority)
            {
                case TurnPriority.Critical:
                    return SpeechCategory.Buff;
                case TurnPriority.BuffedAttack:
                    return SpeechCategory.Buff;
                case TurnPriority.MoveAndAttack:
                    return SpeechCategory.MoveAndAttack;
                case TurnPriority.DirectAttack:
                    return SpeechCategory.Attack;
                case TurnPriority.Support:
                    return SpeechCategory.Support;
            }

            // 폴백
            return firstType == ActionType.Attack ? SpeechCategory.Attack : SpeechCategory.EndTurn;
        }

        #endregion

        #region Random Selection (No-Repeat)

        /// <summary>(unitId, category) → 마지막 사용 인덱스 추적</summary>
        private static readonly Dictionary<(string, SpeechCategory), int> _lastUsedIndex
            = new Dictionary<(string, SpeechCategory), int>();

        private static readonly System.Random _rng = new System.Random();

        /// <summary>
        /// 대사 배열에서 랜덤 선택 (직전 반복 방지)
        /// </summary>
        private static string SelectLine(string unitId, SpeechCategory category, string[] lines)
        {
            if (lines.Length == 1) return lines[0];

            var key = (unitId, category);
            _lastUsedIndex.TryGetValue(key, out int lastIdx);

            int idx;
            if (lines.Length == 2)
            {
                // 2개면 교대
                idx = (lastIdx == 0) ? 1 : 0;
            }
            else
            {
                // 3+개면 마지막 제외 랜덤
                do { idx = _rng.Next(lines.Length); } while (idx == lastIdx);
            }

            _lastUsedIndex[key] = idx;
            return lines[idx];
        }

        #endregion

        #region Placeholder Substitution

        /// <summary>
        /// [target], [ally] 플레이스홀더를 실제 캐릭터 이름으로 치환
        /// </summary>
        private static string SubstitutePlaceholders(string line, TurnPlan plan, BaseUnitEntity speaker)
        {
            if (line.IndexOf("[target]", StringComparison.Ordinal) >= 0)
            {
                string targetName = FindTargetName(plan);
                line = line.Replace("[target]", targetName);
            }

            if (line.IndexOf("[ally]", StringComparison.Ordinal) >= 0)
            {
                string allyName = FindAllyName(plan, speaker);
                line = line.Replace("[ally]", allyName);
            }

            return line;
        }

        private static string FindTargetName(TurnPlan plan)
        {
            var allActions = plan.AllActions;
            for (int i = 0; i < allActions.Count; i++)
            {
                var action = allActions[i];
                if ((action.Type == ActionType.Attack || action.Type == ActionType.Debuff)
                    && action.Target?.Entity is BaseUnitEntity target)
                {
                    return target.CharacterName;
                }
            }
            return "the enemy";
        }

        private static string FindAllyName(TurnPlan plan, BaseUnitEntity speaker)
        {
            var allActions = plan.AllActions;
            for (int i = 0; i < allActions.Count; i++)
            {
                var action = allActions[i];
                if ((action.Type == ActionType.Heal || action.Type == ActionType.Support
                     || action.Type == ActionType.Buff)
                    && action.Target?.Entity is BaseUnitEntity ally
                    && ally.UniqueId != speaker.UniqueId)
                {
                    return ally.CharacterName;
                }
            }
            return "ally";
        }

        #endregion

        #region Spam Prevention

        /// <summary>unitId → 이번 턴에 이미 말한 카테고리</summary>
        private static readonly Dictionary<string, HashSet<SpeechCategory>> _spokenThisTurn
            = new Dictionary<string, HashSet<SpeechCategory>>();

        /// <summary>턴 시작 시 해당 유닛의 대사 기록 초기화</summary>
        public static void ClearForUnit(string unitId)
        {
            _spokenThisTurn.Remove(unitId);
        }

        /// <summary>전투 종료 시 모든 대사 상태 초기화</summary>
        public static void ClearAll()
        {
            _spokenThisTurn.Clear();
            _companionCache.Clear();
            _lastUsedIndex.Clear();
        }

        #endregion

        #region Character Colors

        /// <summary>전투 로그에서 AI 대사 화자 이름 색상 (연한 시안 — 가독성 확보)</summary>
        private static readonly Color AISpeechNameColor = new Color(0.5f, 0.9f, 1.0f);  // #80E5FF

        /// <summary>
        /// 캐릭터별 말풍선 텍스트 색상 (Rich Text)
        /// 어두운 말풍선 배경 위에서 가독성 확보: 밝고 채도 낮은 색상 위주
        /// 게임이 TextMeshPro Rich Text를 지원하면 적용됨
        /// </summary>
        private static readonly Dictionary<CompanionId, string> CompanionTextColors
            = new Dictionary<CompanionId, string>
        {
            [CompanionId.Abelard]   = "#FFD700",  // 금색 — 충직한 집사관, 권위
            [CompanionId.Heinrix]   = "#B0C4DE",  // 연한 철청색 — 냉철한 심문관
            [CompanionId.Argenta]   = "#FF8C42",  // 따뜻한 주황 — 성스러운 불꽃
            [CompanionId.Pasqal]    = "#90EE90",  // 연한 초록 — 기계교/데이터
            [CompanionId.Idira]     = "#DDA0DD",  // 연보라 — 워프/사이킥
            [CompanionId.Cassia]    = "#FFB6C1",  // 연분홍 — 귀족/항해사
            [CompanionId.Yrliet]    = "#E0FFFF",  // 연한 시안 — 엘다리 우아함
            [CompanionId.Jae]       = "#FFEC8B",  // 연노랑 — 거래/실용적
            [CompanionId.Marazhai]  = "#FF6B6B",  // 연붉은 — 드루카리/쾌락
            [CompanionId.Ulfar]     = "#87CEEB",  // 하늘색 — 스페이스 울프/펜리스
            [CompanionId.Kibellah]  = "#C9A0DC",  // 연라벤더 — 죽음 교단/의례
            [CompanionId.Solomorne] = "#C0C0C0",  // 은색 — 아비테스/법 집행
            [CompanionId.Unknown]   = "#FFFFFF",  // 흰색 — 기본
        };

        /// <summary>
        /// 텍스트에 캐릭터별 색상 Rich Text 태그 적용
        /// </summary>
        private static string ApplyCharacterColor(string text, CompanionId companion)
        {
            if (!CompanionTextColors.TryGetValue(companion, out string colorHex))
                colorHex = "#FFFFFF";

            // 흰색이면 태그 불필요
            if (colorHex == "#FFFFFF") return text;

            return $"<color={colorHex}>{text}</color>";
        }

        #endregion

        #region Main Entry Point

        /// <summary>
        /// 메인 진입점 — TurnOrchestrator에서 플랜 생성 후 호출
        /// Non-blocking, fire-and-forget via BarkPlayer.Bark()
        /// </summary>
        public static void AnnouncePlan(BaseUnitEntity unit, TurnPlan plan)
        {
            // 1. Settings 체크
            if (!ModSettings.Instance?.EnableAISpeech ?? false) return;
            if (unit == null || plan == null) return;

            // 2. SpeechCategory 결정
            var category = DetermineSpeechCategory(plan);
            if (category == SpeechCategory.EndTurn) return;

            // 3. 스팸 체크 — 같은 턴 내 동일 카테고리 이미 말했으면 무시
            string unitId = unit.UniqueId;
            if (!_spokenThisTurn.TryGetValue(unitId, out var spoken))
            {
                spoken = new HashSet<SpeechCategory>();
                _spokenThisTurn[unitId] = spoken;
            }
            if (spoken.Contains(category)) return;
            spoken.Add(category);

            // 4. 캐릭터 식별
            var companion = IdentifyCompanion(unit);

            // 5. 대사 조회 (companion → 없으면 Unknown fallback)
            string[] lines = GetLines(companion, category);
            if (lines == null || lines.Length == 0) return;

            // 6. 랜덤 선택 (no-repeat)
            string line = SelectLine(unitId, category, lines);

            // 7. Placeholder 치환
            line = SubstitutePlaceholders(line, plan, unit);

            // 8. Duration 계산: 원본 텍스트 길이 기반 (Rich Text 태그 제외)
            float duration = Mathf.Clamp(line.Length * 0.06f, 2.5f, 5f);

            // 9. 캐릭터별 텍스트 색상 적용 (Rich Text)
            string coloredLine = ApplyCharacterColor(line, companion);

            // 10. BarkPlayer.Bark()
            try
            {
                Kingmaker.Code.UI.MVVM.VM.Bark.BarkPlayer.Bark(
                    unit, coloredLine, duration,
                    voiceOver: null, interactUser: null, synced: true,
                    overrideName: null, overrideNameColor: AISpeechNameColor);
                Main.LogDebug($"[Speech] {unit.CharacterName} ({companion}): \"{line}\" [{category}]");
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[Speech] Bark failed for {unit.CharacterName}: {ex.Message}");
            }
        }

        /// <summary>
        /// 대사 조회 — 현재 언어 설정에 맞는 DB에서 companion 전용 → Unknown fallback
        /// ★ v3.9.32: DialogueLocalization으로 다국어 지원
        /// </summary>
        private static string[] GetLines(CompanionId companion, SpeechCategory category)
        {
            var db = DialogueLocalization.GetDatabase(Localization.CurrentLanguage);

            if (db.TryGetValue(companion, out var categories))
            {
                if (categories.TryGetValue(category, out var lines))
                    return lines;
            }
            // Unknown fallback (같은 언어 DB 내)
            if (companion != CompanionId.Unknown && db.TryGetValue(CompanionId.Unknown, out var defaultCats))
            {
                if (defaultCats.TryGetValue(category, out var defaultLines))
                    return defaultLines;
            }
            // 최종 fallback: 영어 DB
            if (Localization.CurrentLanguage != Language.English)
            {
                var enDb = DialogueLocalization.GetDatabase(Language.English);
                if (enDb.TryGetValue(companion, out var enCats))
                {
                    if (enCats.TryGetValue(category, out var enLines))
                        return enLines;
                }
                if (companion != CompanionId.Unknown && enDb.TryGetValue(CompanionId.Unknown, out var enDefault))
                {
                    if (enDefault.TryGetValue(category, out var enDefaultLines))
                        return enDefaultLines;
                }
            }
            return null;
        }

        #endregion

        #region Victory Bark (v3.9.80)

        /// <summary>
        /// ★ v3.9.80: 전투 승리 시 환호 대사 — TurnEventHandler에서 호출
        /// 의식있는 파티원 중 랜덤 1명이 승리 대사를 말풍선으로 표시
        /// </summary>
        public static void AnnounceVictory(List<BaseUnitEntity> consciousParty)
        {
            if (!(ModSettings.Instance?.EnableVictoryBark ?? false)) return;
            if (consciousParty == null || consciousParty.Count == 0) return;

            var speaker = SelectVictorySpeaker(consciousParty);
            if (speaker == null) return;

            var companion = IdentifyCompanion(speaker);
            string[] lines = GetLines(companion, SpeechCategory.Victory);
            if (lines == null || lines.Length == 0) return;

            string line = SelectLine(speaker.UniqueId, SpeechCategory.Victory, lines);
            float duration = Mathf.Clamp(line.Length * 0.06f, 3f, 5f);
            string coloredLine = ApplyCharacterColor(line, companion);

            try
            {
                Kingmaker.Code.UI.MVVM.VM.Bark.BarkPlayer.Bark(
                    speaker, coloredLine, duration,
                    voiceOver: null, interactUser: null, synced: true,
                    overrideName: null, overrideNameColor: AISpeechNameColor);
                Main.Log($"[Speech] Victory bark: {speaker.CharacterName} ({companion}): \"{line}\"");
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[Speech] Victory bark failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 승리 환호 화자 선택: AI 제어 동료 중 랜덤 (주인공 제외)
        /// AI 동료가 없으면 아무 의식있는 파티원
        /// </summary>
        private static BaseUnitEntity SelectVictorySpeaker(List<BaseUnitEntity> party)
        {
            // AI 제어 동료 우선 (주인공 제외)
            var candidates = new List<BaseUnitEntity>();
            for (int i = 0; i < party.Count; i++)
            {
                if (party[i].IsPlayerFaction && !party[i].IsMainCharacter)
                    candidates.Add(party[i]);
            }
            if (candidates.Count == 0)
                candidates.AddRange(party);

            return candidates[_rng.Next(candidates.Count)];
        }

        #endregion

    }
}
