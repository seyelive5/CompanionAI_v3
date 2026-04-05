// Planning/LLM/LLMScorerCache.cs
// ★ v3.82.0: Semantic caching — 동일한 전술 상황(해시)에서 LLM 호출 재사용.
// ComputeHash()가 전투 상태를 이산 버킷으로 양자화하여 유사 상황 그룹핑.
using System.Collections.Generic;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Settings;
using CompanionAI_v3.GameInterface;

namespace CompanionAI_v3.Planning.LLM
{
    /// <summary>
    /// ★ v3.82.0: LLM Scorer 결과를 전술 상황 해시로 캐싱.
    /// 동일한 전술 상황(역할, HP 구간, 적 수, 타겟 수, 아군 위기, 재장전 등)이면
    /// 이전 LLM 응답을 재사용하여 HTTP 호출 절약.
    /// </summary>
    public static class LLMScorerCache
    {
        private static readonly Dictionary<long, ScorerWeights> _cache = new Dictionary<long, ScorerWeights>();

        /// <summary>캐시 히트 횟수 (디버그/모니터링)</summary>
        public static int HitCount { get; private set; }

        /// <summary>캐시 미스 횟수 (디버그/모니터링)</summary>
        public static int MissCount { get; private set; }

        /// <summary>현재 캐시 엔트리 수</summary>
        public static int CacheSize => _cache.Count;

        /// <summary>최대 캐시 엔트리 수 — 초과 시 전체 클리어</summary>
        private const int MAX_CACHE_SIZE = 100;

        /// <summary>
        /// 전투 상황을 이산 해시로 변환.
        /// 동일한 전술 상황(역할, HP 구간, 적 수 등)이면 동일 해시.
        ///
        /// 해시 구성 (총 ~30비트, long에 여유):
        /// - role:            3비트 (0-7, AIRole enum)
        /// - HP bracket:      4비트 (0-10, 10% 단위)
        /// - enemy count:     4비트 (0-15, 15+는 15로 클램프)
        /// - hittable count:  4비트 (0-15)
        /// - enemy HP sum:    4비트 (0-15, 전체 적 HP% 합계를 100% 단위)
        /// - ally critical:   1비트 (아군 중 HP &lt; 30% 존재 여부)
        /// - needs reload:    1비트
        /// - nearest enemy:   3비트 (0-7, 거리를 5타일 단위 버킷)
        /// </summary>
        public static long ComputeHash(Situation situation, AIRole role)
        {
            if (situation == null) return 0;

            long hash = 0;
            int shift = 0;

            // 1. Role (3 bits: 0-7)
            hash |= ((long)role & 0x7) << shift;
            shift += 3;

            // 2. HP bracket: 10% increments (4 bits: 0-10)
            int hpBracket = (int)(situation.HPPercent * 10f); // 0.0~1.0 → 0~10
            if (hpBracket < 0) hpBracket = 0;
            if (hpBracket > 10) hpBracket = 10;
            hash |= ((long)hpBracket & 0xF) << shift;
            shift += 4;

            // 3. Enemy count (4 bits: 0-15)
            int enemyCount = situation.Enemies?.Count ?? 0;
            if (enemyCount > 15) enemyCount = 15;
            hash |= ((long)enemyCount & 0xF) << shift;
            shift += 4;

            // 4. Hittable count (4 bits: 0-15)
            int hittable = situation.HittableEnemies?.Count ?? 0;
            if (hittable > 15) hittable = 15;
            hash |= ((long)hittable & 0xF) << shift;
            shift += 4;

            // 5. Enemy HP sum bracket (4 bits: 0-15)
            // 적 전체 HP%의 합을 100% 단위로 양자화
            float enemyHpSum = 0f;
            if (situation.Enemies != null)
            {
                for (int i = 0; i < situation.Enemies.Count; i++)
                {
                    var enemy = situation.Enemies[i];
                    if (enemy?.LifeState?.IsConscious == true)
                    {
                        float maxHp = enemy.Health?.MaxHitPoints ?? 1;
                        float curHp = enemy.Health?.HitPointsLeft ?? 0;
                        enemyHpSum += (maxHp > 0) ? (curHp / maxHp) : 0f;
                    }
                }
            }
            int enemyHpBracket = (int)enemyHpSum; // 각 적 1.0이 100%, 합산 후 정수화
            if (enemyHpBracket > 15) enemyHpBracket = 15;
            hash |= ((long)enemyHpBracket & 0xF) << shift;
            shift += 4;

            // 6. Ally critical: any ally HP < 30% (1 bit)
            bool allyCritical = false;
            if (situation.Allies != null)
            {
                for (int i = 0; i < situation.Allies.Count; i++)
                {
                    var ally = situation.Allies[i];
                    if (ally?.LifeState?.IsConscious == true)
                    {
                        float maxHp = ally.Health?.MaxHitPoints ?? 1;
                        float curHp = ally.Health?.HitPointsLeft ?? 0;
                        if (maxHp > 0 && (curHp / maxHp) < 0.3f)
                        {
                            allyCritical = true;
                            break;
                        }
                    }
                }
            }
            hash |= (allyCritical ? 1L : 0L) << shift;
            shift += 1;

            // 7. Needs reload (1 bit)
            hash |= (situation.NeedsReload ? 1L : 0L) << shift;
            shift += 1;

            // 8. Nearest enemy distance bucket (3 bits: 0-7, 5-tile increments)
            int distBucket = (int)(situation.NearestEnemyDistance / 5f);
            if (distBucket > 7) distBucket = 7;
            if (distBucket < 0) distBucket = 0;
            hash |= ((long)distBucket & 0x7) << shift;

            return hash;
        }

        /// <summary>
        /// 캐시에서 가중치 조회. 히트/미스 카운터 자동 업데이트.
        /// </summary>
        public static bool TryGet(long hash, out ScorerWeights weights)
        {
            if (_cache.TryGetValue(hash, out weights))
            {
                HitCount++;
                return true;
            }
            MissCount++;
            weights = null;
            return false;
        }

        /// <summary>
        /// 가중치를 캐시에 저장. MAX_CACHE_SIZE 초과 시 전체 클리어.
        /// </summary>
        public static void Store(long hash, ScorerWeights weights)
        {
            if (weights == null) return;
            if (_cache.Count >= MAX_CACHE_SIZE) _cache.Clear();
            _cache[hash] = weights;
        }

        /// <summary>전체 캐시 클리어 (전투 시작/종료 시)</summary>
        public static void Clear()
        {
            _cache.Clear();
            HitCount = 0;
            MissCount = 0;
        }
    }
}
