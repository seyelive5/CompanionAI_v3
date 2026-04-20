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

        // ★ v3.101.0: Display → Original 매핑 (primacy bias 완화)
        // E 라인을 위협도 내림차순으로 정렬 후, display 인덱스 → situation.Enemies 원본 인덱스 역매핑용.
        // LLMScorer가 Encode() 직후 GetDisplayToOriginalMap()로 조회하여 priority_target 역매핑.
        private static readonly int[] _displayToOriginalIdx = new int[16];
        private static int _displayedCount = 0;

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
        // U:Argenta,DPS,HP85,AP4,MP10,Wpn:Bolter/12,ThisTurn:Atk1,Buf0,Mov0
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

            // ★ v3.97.0: 이번 턴 행동 플래그 — LLM이 "이미 사용함" 인지 가능
            _sb.Append(",ThisTurn:Atk").Append(situation.HasAttackedThisTurn ? '1' : '0')
               .Append(",Buf").Append(situation.HasBuffedThisTurn ? '1' : '0')
               .Append(",Mov").Append(situation.HasMovedThisTurn ? '1' : '0');

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
        // E: 적 (★ v3.101.0: 위협도 내림차순 정렬, display 인덱스를 라벨로)
        // E:0:Psyker,HP40,d5,HI,T1,melee|1:Cult,HP100,d8,T2,melee|2:Cult,HP100,d8,CL,T+R,melee|3:Heavy,HP90,d15,T3,ranged
        //   라벨 숫자 = display rank (0 = 최고 위협). priority_target은 이 숫자로 반환받음.
        //   T1/T2/...  = 자신 다음 차례 전 행동 순서 (1=가장 먼저)
        //   T+R        = 다음 라운드 이후 (멀어서 무시 가능)
        //   melee/ranged = 무기 유형 (둘 다 false면 라벨 생략)
        //
        // Primacy bias 활용: LLM은 첫 번째로 제시된 옵션을 선호함.
        // 위협도 정렬로 "첫 번째 = 최고 위협"이 되어 편향이 유리한 방향으로 작동.
        // ════════════════════════════════════════════════════════════

        private static void AppendEnemiesLine(BaseUnitEntity unit, Situation situation)
        {
            var enemies = situation.Enemies;
            _displayedCount = 0;  // ★ v3.101.0: 매번 리셋

            if (enemies == null || enemies.Count == 0)
            {
                _sb.Append("E:none\n");
                return;
            }

            // 1. Live 필터
            _tempEnemyList.Clear();
            for (int i = 0; i < enemies.Count; i++)
            {
                if (enemies[i] != null && enemies[i].IsConscious)
                    _tempEnemyList.Add(enemies[i]);
            }

            // 2. ★ v3.101.0: 위협도 내림차순 정렬 (primacy bias 완화)
            _tempEnemyList.Sort((a, b) =>
            {
                float sA = ComputeThreatScore(a, situation, unit);
                float sB = ComputeThreatScore(b, situation, unit);
                int cmp = sB.CompareTo(sA);  // desc
                if (cmp != 0) return cmp;
                // 동점 — 원본 인덱스 오름차순으로 결정적 정렬
                return FindEnemyIndex(a, enemies).CompareTo(FindEnemyIndex(b, enemies));
            });

            int liveCount = _tempEnemyList.Count;

            // 3. ★ v3.101.0: Display → Original 매핑 빌드 (MAX_ENEMIES까지만)
            int mapLimit = liveCount < _displayToOriginalIdx.Length ? liveCount : _displayToOriginalIdx.Length;
            if (mapLimit > MAX_ENEMIES) mapLimit = MAX_ENEMIES;
            for (int d = 0; d < mapLimit; d++)
            {
                _displayToOriginalIdx[d] = FindEnemyIndex(_tempEnemyList[d], enemies);
            }

            // 4. 클러스터 감지 (정렬 후, display 인덱스 기준)
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

            // ★ 이니셔티브 매핑 — 한 번만 빌드 (적별 lookup용)
            var initMap = InitiativeTracker.GetEnemiesBeforeNextTurn(unit);

            _sb.Append("E:");
            int displayed = 0;

            // 5. ★ v3.101.0: 정렬된 순서로 emit, 라벨 = display 인덱스 (rank)
            for (int d = 0; d < liveCount && displayed < MAX_ENEMIES; d++)
            {
                var e = _tempEnemyList[d];

                if (displayed > 0) _sb.Append('|');

                string eName = ShortenName(e.CharacterName ?? "Enemy");
                float eHP = CombatAPI.GetHPPercent(e);
                float eDist = CombatAPI.GetDistanceInTiles(unit, e);

                _sb.Append(d).Append(':').Append(eName)  // ★ display index as label
                   .Append(",HP").Append((int)eHP)
                   .Append(",d").Append((int)eDist);

                // 태그
                bool isBestTarget = situation.BestTarget != null
                    && e.UniqueId == situation.BestTarget.UniqueId;
                if (isBestTarget) _sb.Append(",HI");

                // 클러스터 — d는 이미 display 인덱스
                if (d < _clusteredFlags.Length && _clusteredFlags[d])
                    _sb.Append(",CL");

                // 처치 가능 (HP < 20%)
                if (eHP < 20f) _sb.Append(",FIN");

                // ★ 이니셔티브 라벨
                if (initMap.TryGetValue(e, out int tNum))
                    _sb.Append(",T").Append(tNum);
                else
                    _sb.Append(",T+R");

                // ★ 무기 유형
                if (CombatAPI.HasMeleeWeapon(e))
                    _sb.Append(",melee");
                else if (CombatAPI.HasRangedWeapon(e))
                    _sb.Append(",ranged");
                // 둘 다 false면 라벨 생략 (안전 폴백)

                displayed++;
            }

            _displayedCount = displayed;

            if (displayed == 0) _sb.Append("none");
            _sb.Append('\n');
        }

        /// <summary>
        /// ★ v3.101.0: E 라인 정렬용 위협 점수. 내림차순 정렬 → 최고 위협이 display index 0.
        /// BestTarget → finishable → HP 낮음 → 거리 가까움 순으로 가중.
        /// </summary>
        private static float ComputeThreatScore(BaseUnitEntity enemy, Situation situation, BaseUnitEntity unit)
        {
            if (enemy == null) return 0f;
            float score = 0f;
            float hp = CombatAPI.GetHPPercent(enemy);
            float dist = CombatAPI.GetDistanceInTiles(unit, enemy);

            // BestTarget 최우선 (시스템이 이미 선정한 최적 타겟)
            if (situation.BestTarget != null && enemy.UniqueId == situation.BestTarget.UniqueId)
                score += 1000f;

            // 처치 가능 (HP < 20%) — 기회비용 큼
            if (hp < 20f) score += 500f;
            else score += (100f - hp);  // HP 낮을수록 가중

            // 가까울수록 위협적 (20타일 이내만 가산)
            if (dist < 20f) score += (20f - dist) * 2f;

            return score;
        }

        /// <summary>
        /// ★ v3.101.0: 최근 Encode() 호출의 display index → situation.Enemies 원본 인덱스 매핑 반환.
        /// LLM이 반환한 priority_target(display idx)을 원본 idx로 역매핑하는 데 사용.
        /// 반드시 Encode() 직후에 호출 (다음 Encode() 호출 시 덮어쓰임).
        /// </summary>
        public static int[] GetDisplayToOriginalMap()
        {
            int len = _displayedCount;
            if (len <= 0) return new int[0];
            int[] result = new int[len];
            System.Array.Copy(_displayToOriginalIdx, result, len);
            return result;
        }

        // ════════════════════════════════════════════════════════════
        // K: 핵심 요인
        // K:0 finishable|1,2 clustered|Heinrix critical
        // ════════════════════════════════════════════════════════════

        private static void AppendKeyFactorsLine(Situation situation)
        {
            _sb.Append("K:");
            bool first = true;

            // 처치 가능 타겟 (★ v3.101.0: display 인덱스 사용)
            if (situation.CanKillBestTarget && situation.BestTarget != null)
            {
                int idx = FindDisplayIndex(situation.BestTarget);
                if (idx >= 0)
                {
                    AppendKeyFactor(ref first);
                    _sb.Append(idx).Append(" finishable");
                }
            }

            // 클러스터된 적 목록 (★ v3.101.0: display 인덱스 사용 — i가 이미 display idx)
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
                for (int i = 0; i < liveCount && i < _clusteredFlags.Length && i < MAX_ENEMIES; i++)
                {
                    if (!_clusteredFlags[i]) continue;
                    if (!firstCluster) _sb.Append(',');
                    _sb.Append(i);  // ★ display index
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
        /// 카테고리별 스킬 출력. 각 스킬에 AP/MP 비용 + 효과 라벨 부착.
        /// ★ v3.97.0: AP/MP 비용 포함 (LLM이 조합 가능 여부 판단 가능하도록)
        /// 형식:
        ///   Atk:
        ///   - 단발 사격 [2AP, single shot]
        ///   - 점사 사격 [4AP, burst, +offense]
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

                // ★ v3.97.0: AP/MP 비용 + 효과 라벨 합쳐서 [...] 표기
                float apCost = CombatAPI.GetEffectiveAPCost(ab);
                float mpCost = CombatAPI.GetAbilityMPCost(ab);
                string guid = AbilityDatabase.GetGuid(ab);
                string effectLabel = AbilityEffectCache.GetLabel(guid);

                bool hasAny = apCost > 0f || mpCost > 0f || !string.IsNullOrEmpty(effectLabel);
                if (hasAny)
                {
                    _sb.Append(" [");
                    bool first = true;
                    if (apCost > 0f)
                    {
                        _sb.Append(apCost.ToString("0.#")).Append("AP");
                        first = false;
                    }
                    if (mpCost > 0f)
                    {
                        if (!first) _sb.Append(", ");
                        _sb.Append(mpCost.ToString("0.#")).Append("MP");
                        first = false;
                    }
                    if (!string.IsNullOrEmpty(effectLabel))
                    {
                        if (!first) _sb.Append(", ");
                        _sb.Append(effectLabel);
                    }
                    _sb.Append(']');
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

        /// <summary>★ v3.101.0: _tempEnemyList(정렬 후) 내 display 인덱스 찾기. K 라인용.</summary>
        private static int FindDisplayIndex(BaseUnitEntity target)
        {
            if (target == null) return -1;
            for (int i = 0; i < _tempEnemyList.Count && i < MAX_ENEMIES; i++)
            {
                if (_tempEnemyList[i] != null && _tempEnemyList[i].UniqueId == target.UniqueId)
                    return i;
            }
            return -1;
        }

        // ════════════════════════════════════════════════════════════
        // CMD: Commander 지시 (팀 전략)
        // CMD:enc=boss,focus=0,form=aggressive,syn=tank_first
        // ════════════════════════════════════════════════════════════

        private static void AppendCommanderLine()
        {
            var cmd = Core.TeamBlackboard.Instance?.CommanderDirective;
            if (cmd == null || cmd.IsDefault) return;

            _sb.Append("CMD:");
            bool needComma = false;

            // ★ v3.110.4: EncounterType 토큰 — narration 파싱에 의존하지 않는 구조화 채널.
            // "normal"은 default라 스킵 (토큰 절약).
            if (!string.IsNullOrEmpty(cmd.EncounterType) && cmd.EncounterType != "normal")
            {
                _sb.Append("enc=").Append(cmd.EncounterType);
                needComma = true;
            }

            if (cmd.FocusTarget >= 0)
            {
                if (needComma) _sb.Append(',');
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
