// Planning/LLM/CompactBattlefieldEncoder.cs
// ★ LLM-as-Scorer: 전투 상태를 ~150-180 토큰으로 압축 인코딩.
// LLMScorer의 user message로 사용. 최소 토큰으로 최대 전술 정보 전달.
using System.Collections.Generic;
using System.Text;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;
using CompanionAI_v3.MachineSpirit.Knowledge;

namespace CompanionAI_v3.Planning.LLM
{
    /// <summary>
    /// ★ 전투 상태를 ~150-180 토큰으로 압축 인코딩.
    ///
    /// 출력 형식:
    /// U:Argenta,DPS,HP85,AP4,MP10,Wpn:Bolter/12
    /// A:Abelard,Tank,HP93,d5|Heinrix,Sup,HP25!,d3
    /// E:0:Psyker,HP40,d5,HI|1:Cult,HP100,d8|2:Cult,HP100,d8,CL|3:Heavy,HP90,d15
    /// K:0 finishable|1,2 clustered|Heinrix critical
    ///
    /// Zero allocation: static StringBuilder 재사용.
    /// </summary>
    public static class CompactBattlefieldEncoder
    {
        // 재사용 StringBuilder (GC 방지)
        private static readonly StringBuilder _sb = new StringBuilder(512);

        // 클러스터 감지용 임시 리스트 (GC 방지)
        private static readonly List<BaseUnitEntity> _tempEnemyList = new List<BaseUnitEntity>(16);

        // 클러스터 결과 임시 저장 (GC 방지 — HashSet 대신 bool 배열)
        private static readonly bool[] _clusteredFlags = new bool[16];

        /// <summary>최대 표시 아군 수</summary>
        private const int MAX_ALLIES = 5;

        /// <summary>최대 표시 적 수</summary>
        private const int MAX_ENEMIES = 8;

        /// <summary>
        /// 전투 상태를 ~150-180 토큰 compact 형식으로 인코딩.
        /// </summary>
        /// <param name="unit">현재 유닛</param>
        /// <param name="situation">전투 상황 스냅샷</param>
        /// <param name="roleName">역할 이름 (DPS, Tank, Support, Overseer)</param>
        public static string Encode(BaseUnitEntity unit, Situation situation, string roleName)
        {
            if (unit == null || situation == null) return "(no data)";

            _sb.Clear();

            AppendUnitLine(unit, situation, roleName);
            AppendAlliesLine(unit, situation);
            AppendEnemiesLine(unit, situation);
            AppendKeyFactorsLine(situation);
            AppendSkillsLine(situation);
            AppendKnowledgeContext(unit, situation);
            AppendCommanderLine();
            AppendMemoryLine();

            return _sb.ToString();
        }

        // ════════════════════════════════════════════════════════════
        // U: 현재 유닛
        // U:Argenta,DPS,HP85,AP4,MP10,Wpn:Bolter/12
        // ════════════════════════════════════════════════════════════

        private static void AppendUnitLine(BaseUnitEntity unit, Situation situation, string roleName)
        {
            string name = unit.CharacterName ?? "Unit";
            string roleAbbr = AbbreviateRole(roleName);

            _sb.Append("U:").Append(name)
               .Append(',').Append(roleAbbr)
               .Append(",HP").Append((int)situation.HPPercent)
               .Append(",AP").Append((int)situation.CurrentAP)
               .Append(",MP").Append((int)situation.CurrentMP);

            // 무기 정보
            var wp = situation.WeaponRange;
            string weaponName = CombatAPI.GetWeaponSetPrimaryName(unit, situation.CurrentWeaponSetIndex);
            if (!string.IsNullOrEmpty(weaponName))
            {
                _sb.Append(",Wpn:").Append(ShortenName(weaponName));
                if (!wp.IsMelee)
                    _sb.Append('/').Append((int)wp.EffectiveRange);
            }
            else
            {
                _sb.Append(wp.IsMelee ? ",Wpn:Melee" : ",Wpn:Ranged");
                if (!wp.IsMelee)
                    _sb.Append('/').Append((int)wp.EffectiveRange);
            }

            if (situation.NeedsReload) _sb.Append(",RELOAD");

            _sb.Append('\n');
        }

        // ════════════════════════════════════════════════════════════
        // A: 아군 (자신 제외)
        // A:Abelard,Tank,HP93,d5|Heinrix,Sup,HP25!,d3
        // ════════════════════════════════════════════════════════════

        private static void AppendAlliesLine(BaseUnitEntity unit, Situation situation)
        {
            var allies = situation.CombatantAllies;
            if (allies == null || allies.Count == 0)
            {
                _sb.Append("A:none\n");
                return;
            }

            _sb.Append("A:");
            bool first = true;
            int count = 0;

            for (int i = 0; i < allies.Count && count < MAX_ALLIES; i++)
            {
                var ally = allies[i];
                if (ally == null || !ally.IsConscious) continue;
                if (ally == unit) continue;

                if (!first) _sb.Append('|');
                first = false;

                string aName = ShortenName(ally.CharacterName ?? "Ally");
                float aHP = CombatAPI.GetHPPercent(ally);
                float aDist = CombatAPI.GetDistanceInTiles(unit, ally);
                AIRole aRole = RoleDetector.DetectOptimalRole(ally);

                _sb.Append(aName)
                   .Append(',').Append(AbbreviateRole(aRole))
                   .Append(",HP").Append((int)aHP);

                // CRITICAL 마커 (HP < 30%)
                if (aHP < 30f) _sb.Append('!');

                _sb.Append(",d").Append((int)aDist);

                count++;
            }

            if (first) _sb.Append("none");
            _sb.Append('\n');
        }

        // ════════════════════════════════════════════════════════════
        // E: 적 (인덱스 부여)
        // E:0:Psyker,HP40,d5,HI|1:Cult,HP100,d8|2:Cult,HP100,d8,CL|3:Heavy,HP90,d15
        // ════════════════════════════════════════════════════════════

        private static void AppendEnemiesLine(BaseUnitEntity unit, Situation situation)
        {
            var enemies = situation.Enemies;
            if (enemies == null || enemies.Count == 0)
            {
                _sb.Append("E:none\n");
                return;
            }

            // 클러스터 감지: 3+ enemies within 3 tiles of each other
            // 인라인 경량 버전 (ClusterDetector 공유 버퍼 충돌 방지)
            _tempEnemyList.Clear();
            for (int i = 0; i < enemies.Count; i++)
            {
                if (enemies[i] != null && enemies[i].IsConscious)
                    _tempEnemyList.Add(enemies[i]);
            }

            // clustered 플래그 초기화
            int liveCount = _tempEnemyList.Count;
            for (int i = 0; i < _clusteredFlags.Length; i++)
                _clusteredFlags[i] = false;

            for (int i = 0; i < liveCount && i < _clusteredFlags.Length; i++)
            {
                int nearCount = 0;
                for (int j = 0; j < liveCount; j++)
                {
                    if (i == j) continue;
                    float d = CombatAPI.GetDistanceInTiles(_tempEnemyList[i], _tempEnemyList[j]);
                    if (d <= 3f) nearCount++;
                }
                if (nearCount >= 2) _clusteredFlags[i] = true;
            }

            _sb.Append("E:");
            int displayed = 0;

            for (int i = 0; i < enemies.Count && displayed < MAX_ENEMIES; i++)
            {
                var e = enemies[i];
                if (e == null || !e.IsConscious) continue;

                if (displayed > 0) _sb.Append('|');

                string eName = ShortenName(e.CharacterName ?? "Enemy");
                float eHP = CombatAPI.GetHPPercent(e);
                float eDist = CombatAPI.GetDistanceInTiles(unit, e);

                _sb.Append(i).Append(':').Append(eName)
                   .Append(",HP").Append((int)eHP)
                   .Append(",d").Append((int)eDist);

                // 태그
                bool isBestTarget = situation.BestTarget != null
                    && e.UniqueId == situation.BestTarget.UniqueId;
                if (isBestTarget) _sb.Append(",HI");

                // 클러스터 — _tempEnemyList 인덱스로 매핑
                int liveIdx = _tempEnemyList.IndexOf(e);
                if (liveIdx >= 0 && liveIdx < _clusteredFlags.Length && _clusteredFlags[liveIdx])
                    _sb.Append(",CL");

                // 처치 가능 (HP < 20%)
                if (eHP < 20f) _sb.Append(",FIN");

                displayed++;
            }

            if (displayed == 0) _sb.Append("none");
            _sb.Append('\n');
        }

        // ════════════════════════════════════════════════════════════
        // K: 핵심 요인
        // K:0 finishable|1,2 clustered|Heinrix critical
        // ════════════════════════════════════════════════════════════

        private static void AppendKeyFactorsLine(Situation situation)
        {
            _sb.Append("K:");
            bool first = true;

            // 처치 가능 타겟
            if (situation.CanKillBestTarget && situation.BestTarget != null)
            {
                // BestTarget의 인덱스 찾기
                int idx = FindEnemyIndex(situation.BestTarget, situation.Enemies);
                if (idx >= 0)
                {
                    AppendKeyFactor(ref first);
                    _sb.Append(idx).Append(" finishable");
                }
            }

            // 클러스터된 적 목록
            int liveCount = _tempEnemyList.Count;
            bool hasCluster = false;
            for (int i = 0; i < liveCount && i < _clusteredFlags.Length; i++)
            {
                if (_clusteredFlags[i]) { hasCluster = true; break; }
            }

            if (hasCluster)
            {
                AppendKeyFactor(ref first);
                bool firstCluster = true;
                for (int i = 0; i < liveCount && i < _clusteredFlags.Length; i++)
                {
                    if (!_clusteredFlags[i]) continue;
                    // _tempEnemyList[i] → enemies 원본 인덱스
                    int origIdx = FindEnemyIndex(_tempEnemyList[i], situation.Enemies);
                    if (origIdx < 0) continue;

                    if (!firstCluster) _sb.Append(',');
                    _sb.Append(origIdx);
                    firstCluster = false;
                }
                _sb.Append(" clustered");
            }

            // 위험 아군
            if (situation.MostWoundedAlly != null)
            {
                float mwHP = CombatAPI.GetHPPercent(situation.MostWoundedAlly);
                if (mwHP < 30f)
                {
                    string mwName = ShortenName(situation.MostWoundedAlly.CharacterName ?? "ally");
                    AppendKeyFactor(ref first);
                    _sb.Append(mwName).Append(" critical");
                }
            }

            // 재장전 필요
            if (situation.NeedsReload)
            {
                AppendKeyFactor(ref first);
                _sb.Append("reload needed");
            }

            // 자기 자신 위험
            if (situation.IsHPCritical)
            {
                AppendKeyFactor(ref first);
                _sb.Append("self critical");
            }

            // 타겟 없음 — 재배치 필요
            if (!situation.HasHittableEnemies && situation.HasLivingEnemies)
            {
                AppendKeyFactor(ref first);
                _sb.Append("no targets in range");
            }

            // 위험 구역
            if (situation.NeedsAoEEvacuation)
            {
                AppendKeyFactor(ref first);
                _sb.Append("in hazard zone");
            }

            if (first) _sb.Append("none");
            _sb.Append('\n');
        }

        // ════════════════════════════════════════════════════════════
        // SK: 유닛 스킬/어빌리티 목록 (효과 라벨 포함)
        // SK:
        // Atk:
        // - 단발 사격 [single shot]
        // - 점사 사격 [burst, +offense]
        // Buff:
        // - 황제의 말씀 [pre-attack buff — use before shooting]
        // ════════════════════════════════════════════════════════════

        private static void AppendSkillsLine(Situation situation)
        {
            _sb.Append("SK:\n");

            AppendSkillCategory(situation.AvailableAttacks, "Atk", 3);
            AppendSkillCategory(situation.AvailableAoEAttacks, "AoE", 2);
            AppendSkillCategory(situation.AvailableBuffs, "Buff", 3);
            AppendSkillCategory(situation.AvailableHeals, "Heal", 2);
            AppendSkillCategory(situation.AvailableDebuffs, "Dbf", 2);
        }

        /// <summary>
        /// 카테고리별 스킬 출력. 각 스킬에 효과 라벨 부착.
        /// 형식:
        ///   Atk:
        ///   - 단발 사격 [single shot]
        ///   - 점사 사격 [burst, +offense]
        /// </summary>
        private static void AppendSkillCategory(
            List<AbilityData> abilities, string label, int maxItems)
        {
            if (abilities == null || abilities.Count == 0) return;

            _sb.Append(label).Append(":\n");

            int count = System.Math.Min(abilities.Count, maxItems);
            for (int i = 0; i < count; i++)
            {
                var ab = abilities[i];
                if (ab == null) continue;

                _sb.Append("- ");
                _sb.Append(ab.Name ?? "?");

                // 효과 라벨 조회 — 캐시 히트 시 [...] 추가
                string guid = AbilityDatabase.GetGuid(ab);
                string effectLabel = AbilityEffectCache.GetLabel(guid);
                if (!string.IsNullOrEmpty(effectLabel))
                {
                    _sb.Append(" [").Append(effectLabel).Append(']');
                }

                _sb.Append('\n');
            }
        }

        // ════════════════════════════════════════════════════════════
        // KB: 지식 베이스 컨텍스트 (게임 DB에서 검색)
        // KB:Psyker:Warp-powered enemy, uses psychic...|MyAoE:Deals damage in a radius...
        // ════════════════════════════════════════════════════════════

        /// <summary>적 위협 정보 + 유닛 능력 정보를 지식 DB에서 검색하여 추가</summary>
        private static void AppendKnowledgeContext(BaseUnitEntity unit, Situation situation)
        {
            if (!KnowledgeIndex.IsReady) return;

            bool hasContent = false;

            // 1. Enemy threat info — search for each unique enemy name (max 5)
            var enemies = situation.Enemies;
            if (enemies != null)
            {
                _seenNames.Clear();
                for (int i = 0; i < enemies.Count && i < 5; i++)
                {
                    var enemy = enemies[i];
                    if (enemy == null || !enemy.IsConscious) continue;
                    string name = enemy.CharacterName;
                    if (string.IsNullOrEmpty(name) || _seenNames.Contains(name)) continue;
                    _seenNames.Add(name);

                    var results = KnowledgeIndex.Search(name, 1);
                    if (results == null || results.Count == 0 || results[0].Score < 0.3f) continue;

                    string desc = StripHtml(results[0].Entry.Text);
                    if (string.IsNullOrEmpty(desc)) continue;
                    if (desc.Length > 50) desc = desc.Substring(0, 50);

                    if (!hasContent) _sb.Append("KB:");
                    else _sb.Append('|');
                    _sb.Append(ShortenName(name)).Append(':').Append(desc);
                    hasContent = true;
                }
            }

            // 2. Unit's notable AoE ability — search for first AoE name
            if (situation.AvailableAoEAttacks?.Count > 0)
            {
                var aoe = situation.AvailableAoEAttacks[0];
                string aName = aoe?.Name;
                if (!string.IsNullOrEmpty(aName))
                {
                    var results = KnowledgeIndex.Search(aName, 1);
                    if (results?.Count > 0 && results[0].Score > 0.3f)
                    {
                        string desc = StripHtml(results[0].Entry.Text);
                        if (!string.IsNullOrEmpty(desc))
                        {
                            if (desc.Length > 50) desc = desc.Substring(0, 50);
                            if (!hasContent) _sb.Append("KB:");
                            else _sb.Append('|');
                            _sb.Append("MyAoE:").Append(desc);
                            hasContent = true;
                        }
                    }
                }
            }

            if (hasContent) _sb.Append('\n');
        }

        // KB용 중복 이름 체크 (GC 방지)
        private static readonly HashSet<string> _seenNames = new HashSet<string>();

        /// <summary>HTML 태그 제거 — &lt;b&gt;, &lt;color&gt;, &lt;link&gt; 등</summary>
        private static string StripHtml(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            // 재사용 StringBuilder (_sb는 인코딩 중이므로 별도 사용)
            var sb = new StringBuilder(html.Length);
            bool inTag = false;
            for (int i = 0; i < html.Length; i++)
            {
                char c = html[i];
                if (c == '<') inTag = true;
                else if (c == '>') inTag = false;
                else if (!inTag) sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        // ════════════════════════════════════════════════════════════
        // 헬퍼
        // ════════════════════════════════════════════════════════════

        private static void AppendKeyFactor(ref bool first)
        {
            if (!first) _sb.Append('|');
            first = false;
        }

        /// <summary>AIRole enum → 축약 문자열</summary>
        private static string AbbreviateRole(AIRole role)
        {
            switch (role)
            {
                case AIRole.DPS: return "DPS";
                case AIRole.Tank: return "Tank";
                case AIRole.Support: return "Sup";
                case AIRole.Overseer: return "Ovr";
                default: return "DPS";
            }
        }

        /// <summary>역할 이름 문자열 → 축약 문자열</summary>
        private static string AbbreviateRole(string roleName)
        {
            if (string.IsNullOrEmpty(roleName)) return "DPS";
            switch (roleName)
            {
                case "DPS": return "DPS";
                case "Tank": return "Tank";
                case "Support": return "Sup";
                case "Overseer": return "Ovr";
                default: return roleName.Length > 4 ? roleName.Substring(0, 3) : roleName;
            }
        }

        /// <summary>
        /// 이름 단축 — 첫 단어만 사용 (최대 12자).
        /// "Chaos Cultist" → "Chaos", "Argenta" → "Argenta"
        /// </summary>
        private static string ShortenName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";

            // 첫 단어 추출
            int spaceIdx = name.IndexOf(' ');
            string word = spaceIdx > 0 ? name.Substring(0, spaceIdx) : name;

            // 최대 12자
            return word.Length > 12 ? word.Substring(0, 12) : word;
        }

        /// <summary>적 목록에서 특정 유닛의 원본 인덱스 찾기</summary>
        private static int FindEnemyIndex(BaseUnitEntity target, List<BaseUnitEntity> enemies)
        {
            if (target == null || enemies == null) return -1;
            for (int i = 0; i < enemies.Count; i++)
            {
                if (enemies[i] != null && enemies[i].UniqueId == target.UniqueId)
                    return i;
            }
            return -1;
        }

        // ════════════════════════════════════════════════════════════
        // CMD: Commander 지시 (팀 전략)
        // CMD:focus=0,form=aggressive,syn=tank_first
        // ════════════════════════════════════════════════════════════

        private static void AppendCommanderLine()
        {
            var cmd = Core.TeamBlackboard.Instance?.CommanderDirective;
            if (cmd == null || cmd.IsDefault) return;

            _sb.Append("CMD:");
            bool needComma = false;

            if (cmd.FocusTarget >= 0)
            {
                _sb.Append("focus=").Append(cmd.FocusTarget);
                needComma = true;
            }

            if (!string.IsNullOrEmpty(cmd.Formation) && cmd.Formation != "balanced")
            {
                if (needComma) _sb.Append(',');
                _sb.Append("form=").Append(cmd.Formation);
                needComma = true;
            }

            if (!string.IsNullOrEmpty(cmd.Synergy))
            {
                if (needComma) _sb.Append(',');
                _sb.Append("syn=").Append(cmd.Synergy);
            }

            _sb.Append('\n');
        }

        // ════════════════════════════════════════════════════════════
        // PAST: 전투 간 전술 기억
        // PAST: vs 2xCultist,1xPsyker, focus_fire=2.0 effective (3 rounds)
        // ════════════════════════════════════════════════════════════

        private static void AppendMemoryLine()
        {
            var memory = Core.TeamBlackboard.Instance?.TacticalMemoryContext;
            if (string.IsNullOrEmpty(memory)) return;
            _sb.Append(memory).Append('\n');
        }
    }
}
