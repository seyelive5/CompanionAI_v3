// Planning/LLM/BattlefieldSummarizer.cs
// ★ Phase 3: 전장 상태를 LLM Judge 컨텍스트용 compact markdown으로 요약.
using System.Collections.Generic;
using System.Text;
using Kingmaker.EntitySystem.Entities;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning.LLM
{
    /// <summary>
    /// ★ Phase 3: 전장 상태를 LLM 컨텍스트용 자연어로 요약.
    /// ~300 토큰의 compact markdown key-value 형식.
    /// ASCII 그리드 맵이 아닌 핵심 전술 정보만 압축.
    /// </summary>
    public static class BattlefieldSummarizer
    {
        // 재사용 StringBuilder (GC 방지)
        private static readonly StringBuilder _sb = new StringBuilder(512);

        // 클러스터 감지용 임시 리스트 (GC 방지)
        private static readonly List<BaseUnitEntity> _tempClusterCheck = new List<BaseUnitEntity>(16);

        /// <summary>
        /// Situation → compact markdown 요약 (~300 tokens).
        /// </summary>
        public static string Summarize(Situation situation)
        {
            if (situation?.Unit == null) return "(no situation)";

            _sb.Clear();

            AppendUnitLine(situation);
            AppendWeaponLine(situation);
            AppendAlliesLine(situation);
            AppendEnemiesLine(situation);
            AppendSituationLine(situation);
            AppendKeyFactorsLine(situation);

            return _sb.ToString();
        }

        // ────────────────────────────────────────────
        // **Unit:** Argenta (DPS, HP:85%, AP:4, MP:10)
        // ────────────────────────────────────────────
        private static void AppendUnitLine(Situation situation)
        {
            string name = situation.Unit.CharacterName ?? "Unit";
            AIRole role = RoleDetector.DetectOptimalRole(situation.Unit);
            string roleName = role.ToString();

            _sb.Append("**Unit:** ").Append(name)
               .Append(" (").Append(roleName)
               .Append(", HP:").Append(situation.HPPercent.ToString("F0")).Append('%')
               .Append(", AP:").Append(situation.CurrentAP.ToString("F0"))
               .Append(", MP:").Append(situation.CurrentMP.ToString("F0"))
               .Append(")\n");
        }

        // ────────────────────────────────────────────
        // **Weapon:** Bolter (ranged, range 12)
        // ────────────────────────────────────────────
        private static void AppendWeaponLine(Situation situation)
        {
            _sb.Append("**Weapon:** ");

            var wp = situation.WeaponRange;
            if (wp.IsMelee)
            {
                _sb.Append("Melee");
            }
            else
            {
                _sb.Append("Ranged (range ").Append(wp.EffectiveRange.ToString("F0")).Append(')');
            }

            if (wp.IsScatter) _sb.Append(", scatter");
            if (wp.HasDirectionalPattern) _sb.Append(", directional");
            if (situation.NeedsReload) _sb.Append(", NEEDS RELOAD");

            _sb.Append('\n');
        }

        // ────────────────────────────────────────────
        // **Allies:** Abelard (Tank, HP:93%, 5 tiles), ...
        // ────────────────────────────────────────────
        private static void AppendAlliesLine(Situation situation)
        {
            var allies = situation.CombatantAllies;
            if (allies == null || allies.Count == 0)
            {
                _sb.Append("**Allies:** none\n");
                return;
            }

            _sb.Append("**Allies:** ");
            bool first = true;

            for (int i = 0; i < allies.Count; i++)
            {
                var ally = allies[i];
                if (ally == null || !ally.IsConscious) continue;
                // 자기 자신 제외
                if (ally == situation.Unit) continue;

                if (!first) _sb.Append(", ");
                first = false;

                string aName = ally.CharacterName ?? "Ally";
                float aHP = CombatAPI.GetHPPercent(ally);
                float aDist = CombatAPI.GetDistanceInTiles(situation.Unit, ally);
                AIRole aRole = RoleDetector.DetectOptimalRole(ally);

                _sb.Append(aName).Append(" (").Append(aRole.ToString())
                   .Append(", HP:").Append(aHP.ToString("F0")).Append('%')
                   .Append(", ").Append(aDist.ToString("F0")).Append(" tiles");

                if (aHP < 30f) _sb.Append(" CRITICAL");
                _sb.Append(')');
            }

            if (first) _sb.Append("none");
            _sb.Append('\n');
        }

        // ────────────────────────────────────────────
        // **Enemies:** Psyker (HP:40%, 5 tiles, HIGH PRIORITY), Cultist x2 (clustered, 8 tiles), ...
        // ────────────────────────────────────────────
        private static void AppendEnemiesLine(Situation situation)
        {
            var enemies = situation.Enemies;
            if (enemies == null || enemies.Count == 0)
            {
                _sb.Append("**Enemies:** none\n");
                return;
            }

            // 클러스터 감지: 3+ enemies within 3 tiles of each other
            _tempClusterCheck.Clear();
            for (int i = 0; i < enemies.Count; i++)
            {
                if (enemies[i] != null && enemies[i].IsConscious)
                    _tempClusterCheck.Add(enemies[i]);
            }

            // 간단한 클러스터 감지 — 어떤 적이 3타일 내에 2+ 다른 적이 있는지
            var clustered = new HashSet<BaseUnitEntity>();
            for (int i = 0; i < _tempClusterCheck.Count; i++)
            {
                int nearCount = 0;
                for (int j = 0; j < _tempClusterCheck.Count; j++)
                {
                    if (i == j) continue;
                    float d = CombatAPI.GetDistanceInTiles(_tempClusterCheck[i], _tempClusterCheck[j]);
                    if (d <= 3f) nearCount++;
                }
                if (nearCount >= 2) clustered.Add(_tempClusterCheck[i]);
            }

            // 이름 기반 그룹핑 — 같은 이름의 적 묶기
            var nameGroups = new Dictionary<string, List<BaseUnitEntity>>(8);
            for (int i = 0; i < _tempClusterCheck.Count; i++)
            {
                var e = _tempClusterCheck[i];
                string eName = e.CharacterName ?? "Enemy";

                // BestTarget이나 고유 적은 개별 표시
                bool isBestTarget = situation.BestTarget != null
                    && e.UniqueId == situation.BestTarget.UniqueId;
                bool isLowHP = CombatAPI.GetHPPercent(e) < 40f;

                if (isBestTarget || isLowHP)
                {
                    // 개별 표시 — 고유 키 사용
                    nameGroups["__unique_" + i] = new List<BaseUnitEntity> { e };
                }
                else
                {
                    if (!nameGroups.ContainsKey(eName))
                        nameGroups[eName] = new List<BaseUnitEntity>(4);
                    nameGroups[eName].Add(e);
                }
            }

            _sb.Append("**Enemies:** ");
            bool first = true;

            foreach (var kvp in nameGroups)
            {
                if (!first) _sb.Append(", ");
                first = false;

                var group = kvp.Value;
                if (group.Count == 1)
                {
                    // 개별 적
                    var e = group[0];
                    AppendSingleEnemy(e, situation, clustered);
                }
                else
                {
                    // 그룹화된 적 (예: "Cultist x3")
                    string gName = kvp.Key;
                    _sb.Append(gName).Append(" x").Append(group.Count);

                    // 평균 거리
                    float totalDist = 0f;
                    bool anyClust = false;
                    for (int i = 0; i < group.Count; i++)
                    {
                        totalDist += CombatAPI.GetDistanceInTiles(situation.Unit, group[i]);
                        if (clustered.Contains(group[i])) anyClust = true;
                    }
                    float avgDist = totalDist / group.Count;

                    _sb.Append(" (");
                    if (anyClust) _sb.Append("clustered, ");
                    _sb.Append(avgDist.ToString("F0")).Append(" tiles)");
                }
            }

            if (first) _sb.Append("none");
            _sb.Append('\n');
        }

        private static void AppendSingleEnemy(BaseUnitEntity enemy, Situation situation,
            HashSet<BaseUnitEntity> clustered)
        {
            string eName = enemy.CharacterName ?? "Enemy";
            float eHP = CombatAPI.GetHPPercent(enemy);
            float eDist = CombatAPI.GetDistanceInTiles(situation.Unit, enemy);

            _sb.Append(eName)
               .Append(" (HP:").Append(eHP.ToString("F0")).Append('%')
               .Append(", ").Append(eDist.ToString("F0")).Append(" tiles");

            bool isBestTarget = situation.BestTarget != null
                && enemy.UniqueId == situation.BestTarget.UniqueId;

            if (isBestTarget) _sb.Append(", HIGH PRIORITY");
            if (clustered.Contains(enemy)) _sb.Append(", clustered");
            if (situation.CanKillBestTarget && isBestTarget) _sb.Append(", finishable");

            _sb.Append(')');
        }

        // ────────────────────────────────────────────
        // **Situation:** Round 3, 6 vs 4, allies have advantage
        // ────────────────────────────────────────────
        private static void AppendSituationLine(Situation situation)
        {
            int round = GetCurrentRound();
            int allyCount = (situation.CombatantAllies?.Count ?? 0) + 1; // +1 for self
            int enemyCount = situation.Enemies?.Count ?? 0;

            _sb.Append("**Situation:** ");
            if (round > 0) _sb.Append("Round ").Append(round).Append(", ");
            _sb.Append(allyCount).Append(" vs ").Append(enemyCount);

            // 수적 우위 판단
            if (allyCount > enemyCount + 1)
                _sb.Append(", allies have advantage");
            else if (enemyCount > allyCount + 1)
                _sb.Append(", enemies have advantage");
            else
                _sb.Append(", balanced");

            if (situation.IsInDanger) _sb.Append(", UNIT IN DANGER");
            if (situation.IsInEnemyOverwatchZone) _sb.Append(", in overwatch zone");
            _sb.Append('\n');
        }

        // ────────────────────────────────────────────
        // **Key factors:** Psyker is finishable, 2 cultists clustered (AoE opportunity), ...
        // ────────────────────────────────────────────
        private static void AppendKeyFactorsLine(Situation situation)
        {
            _sb.Append("**Key factors:** ");
            bool first = true;

            // 킬 가능한 타겟
            if (situation.CanKillBestTarget && situation.BestTarget != null)
            {
                string tName = situation.BestTarget.CharacterName ?? "target";
                AppendFactor(ref first, $"{tName} is finishable");
            }

            // AoE 기회 — 클러스터 존재 시
            if (situation.HasAoEAttacks && situation.Enemies != null)
            {
                int clusteredCount = CountClusteredEnemies(situation.Enemies);
                if (clusteredCount >= 3)
                    AppendFactor(ref first, $"{clusteredCount} enemies clustered (AoE opportunity)");
            }

            // 아군 위험 (힐 필요)
            if (situation.MostWoundedAlly != null)
            {
                float mwHP = CombatAPI.GetHPPercent(situation.MostWoundedAlly);
                if (mwHP < 30f)
                {
                    string mwName = situation.MostWoundedAlly.CharacterName ?? "ally";
                    AppendFactor(ref first, $"{mwName} needs healing ({mwHP:F0}% HP)");
                }
            }

            // 자신 HP 위험
            if (situation.IsHPCritical)
                AppendFactor(ref first, "self HP critical");

            // 재장전 필요
            if (situation.NeedsReload)
                AppendFactor(ref first, "needs reload");

            // 적이 없으면 공격 불가
            if (!situation.HasHittableEnemies && situation.HasLivingEnemies)
                AppendFactor(ref first, "no enemies in range, must reposition");

            // 위치 위험 (AoE 구역)
            if (situation.NeedsAoEEvacuation)
                AppendFactor(ref first, "standing in hazardous zone");

            if (first) _sb.Append("none");
            _sb.Append('\n');
        }

        private static void AppendFactor(ref bool first, string factor)
        {
            if (!first) _sb.Append(", ");
            _sb.Append(factor);
            first = false;
        }

        /// <summary>
        /// 간단한 클러스터 카운트 — 3타일 내에 2+ 이웃이 있는 적 수.
        /// ClusterDetector.FindClusters는 정적 버퍼를 사용하므로
        /// 동시 호출 시 충돌 가능. 여기서는 경량 버전 사용.
        /// </summary>
        private static int CountClusteredEnemies(List<BaseUnitEntity> enemies)
        {
            int count = 0;
            for (int i = 0; i < enemies.Count; i++)
            {
                var e = enemies[i];
                if (e == null || !e.IsConscious) continue;

                int nearCount = 0;
                for (int j = 0; j < enemies.Count; j++)
                {
                    if (i == j) continue;
                    var o = enemies[j];
                    if (o == null || !o.IsConscious) continue;
                    if (CombatAPI.GetDistanceInTiles(e, o) <= 3f) nearCount++;
                }
                if (nearCount >= 2) count++;
            }
            return count;
        }

        private static int GetCurrentRound()
        {
            try
            {
                return Kingmaker.Game.Instance?.TurnController?.CombatRound ?? 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
