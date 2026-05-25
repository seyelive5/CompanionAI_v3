using System;
using System.Collections.Generic;
using System.Diagnostics;
using CompanionAI_v3.Logging;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.View.Covers;
using Pathfinding;
using UnityEngine;

namespace CompanionAI_v3.GameInterface
{
    public static partial class MovementAPI
    {
        #region Position Evaluation

        // Perf 진단. Stopwatch.GetTimestamp ~30ns/call → 500 LOS call 측정 오버헤드 ≈ 30μs (전체 50ms 의 0.06%).
        // Main thread 단일 호출 가정 — Unity coroutine 안 sequential 호출이므로 thread-safety 불요.
        private static long _perfLosTicks;
        private static int _perfLosCount;

        // Plan-scope dedup cache.
        // 진단 (v3.117.37-38) 결과: 한 plan 안에서 TacticalOptionEvaluator + MovementPlanner 가
        // 동일 인자 (key, unit, enemies, MP, ranges, goal) 로 EvaluateAllPositions 를 2-4회 호출.
        // 실측: 카시아 turn 1×335ms × 4회 = 1.4초 / turn. dedup 시 ~1초 절감.
        //
        // 안전 마진:
        //   1) hash 만으로는 충돌 risk → unit+enemies reference + value 비교로 false hit 차단
        //   2) caller mutation (TacticalOptionEvaluator.cs:335 `score.HittableEnemyCount=1`) 격리:
        //      cache 저장 시 deep clone snapshot, cache hit 시도 deep clone return
        //   3) invalidation: turn 시작 (TurnOrchestrator.ClearAll), caster 이동 (ActionExecutor.InvalidateCaster)
        private sealed class EvalSnapshot
        {
            public BaseUnitEntity Unit;
            // v3.117.42: List reference 대신 count 만 저장. SituationAnalyzer 가 같은 list 를
            //   재사용하며 Clear → Add 패턴이라 reference 보유 시 외부 mutation 노출 (Count=0 reject).
            //   적 set 동일성은 argKey 의 enemies content hash 가 보장.
            public int EnemiesCount;
            public MovementGoal Goal;
            public float TargetDistance;
            public float MinSafeDistance;
            public int ReachableTileCount;
            public List<PositionScore> Snapshot;
            public int Frame;
        }
        private static readonly Dictionary<long, EvalSnapshot> _evalCache = new Dictionary<long, EvalSnapshot>();

        // v3.117.52 진단: dedup cache plan 단위 hit/miss 카운터.
        public static int EvalCacheHits;
        public static int EvalCacheMisses;

        // FindRanged call counter (FindRangedAttackPositionSync cache HIT/MISS 모니터링용 동반 카운터).
        public static int FindRangedCalls;

        // v3.117.54: FindRangedAttackPositionSync 전체 결과 plan-scope 캐시 (후처리 포함).
        // 측정 (v3.117.53): 한 호출당 1.5초, 후처리 80% 차지. dedup cache 가 EvaluateAllPositions 만 잡고
        // 후처리 (LowHPTargetBonus / AoE coverage / HitChance 등) 는 매번 재계산. 결과 전체 캐싱으로 호출 2번 → 1번.
        // Key: 모든 인자 포함 (unit, enemies content, weaponRange, minSafeDistance, predictedMP, role, lastMoveOrigin, situation ref).
        // Cache hit 시 best PositionScore Clone 반환 — null 도 캐싱.
        // Invalidation: ClearEvaluationCache 와 동일 (turn 시작 + caster move 등).
        public struct BestPositionKey : System.IEquatable<BestPositionKey>
        {
            public int UnitId;
            public long EnemiesHash;
            public int WeaponRangeQ;
            public int MinSafeDistQ;
            public int PredictedMPQ;
            public int Role;
            public int LastMoveOriginQ;
            public int SituationId;

            public bool Equals(BestPositionKey other) =>
                UnitId == other.UnitId
                && EnemiesHash == other.EnemiesHash
                && WeaponRangeQ == other.WeaponRangeQ
                && MinSafeDistQ == other.MinSafeDistQ
                && PredictedMPQ == other.PredictedMPQ
                && Role == other.Role
                && LastMoveOriginQ == other.LastMoveOriginQ
                && SituationId == other.SituationId;

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = UnitId;
                    h = h * 31 + (int)EnemiesHash;
                    h = h * 31 + (int)(EnemiesHash >> 32);
                    h = h * 31 + WeaponRangeQ;
                    h = h * 31 + MinSafeDistQ;
                    h = h * 31 + PredictedMPQ;
                    h = h * 31 + Role;
                    h = h * 31 + LastMoveOriginQ;
                    h = h * 31 + SituationId;
                    return h;
                }
            }

            public override bool Equals(object obj) => obj is BestPositionKey k && Equals(k);
        }

        // CacheEntry — null 결과도 명시적으로 캐싱하기 위해 wrapper (Dictionary 가 null value 허용하지만 명시).
        public sealed class BestPositionCacheEntry { public PositionScore Result; }
        public static readonly Dictionary<BestPositionKey, BestPositionCacheEntry> FindRangedCache = new Dictionary<BestPositionKey, BestPositionCacheEntry>();
        public static int FindRangedCacheHits;
        public static int FindRangedCacheMisses;

        // v3.117.56: FindMeleeAttackPositionSync 전체 결과 plan-scope 캐시.
        // FindRanged 와 동일 패턴 — 같은 plan 안에서 TacticalOpt (Option B 평가) + MovementPlanner (PlanMoveToEnemy 근접 경로)
        // + MovementPlanner (gap closer approach/landing) 가 자주 같은 인자로 호출. 단일 target 기반이라
        // EnemiesHash 외에 TargetId 별도 키 필드.
        public struct MeleePositionKey : System.IEquatable<MeleePositionKey>
        {
            public int UnitId;
            public int TargetId;
            public int MeleeRangeQ;
            public int PredictedMPQ;
            public int Role;
            public int MeleeAoEAbilityId;
            public long EnemiesHash;
            public int LastMoveOriginQ;

            public bool Equals(MeleePositionKey other) =>
                UnitId == other.UnitId
                && TargetId == other.TargetId
                && MeleeRangeQ == other.MeleeRangeQ
                && PredictedMPQ == other.PredictedMPQ
                && Role == other.Role
                && MeleeAoEAbilityId == other.MeleeAoEAbilityId
                && EnemiesHash == other.EnemiesHash
                && LastMoveOriginQ == other.LastMoveOriginQ;

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = UnitId;
                    h = h * 31 + TargetId;
                    h = h * 31 + MeleeRangeQ;
                    h = h * 31 + PredictedMPQ;
                    h = h * 31 + Role;
                    h = h * 31 + MeleeAoEAbilityId;
                    h = h * 31 + (int)EnemiesHash;
                    h = h * 31 + (int)(EnemiesHash >> 32);
                    h = h * 31 + LastMoveOriginQ;
                    return h;
                }
            }

            public override bool Equals(object obj) => obj is MeleePositionKey k && Equals(k);
        }

        public static readonly Dictionary<MeleePositionKey, BestPositionCacheEntry> FindMeleeCache = new Dictionary<MeleePositionKey, BestPositionCacheEntry>();
        public static int FindMeleeCalls;
        public static int FindMeleeCacheHits;
        public static int FindMeleeCacheMisses;

        // v3.117.56: FindRetreatPositionSync (8-arg overload) 전체 결과 plan-scope 캐시.
        // 5-arg overload 는 8-arg 를 default arg 로 호출하므로 동일 캐시 적용. lastMoveOrigin 파라미터 없음 (의도된 설계).
        public struct RetreatPositionKey : System.IEquatable<RetreatPositionKey>
        {
            public int UnitId;
            public long EnemiesHash;
            public int MinSafeDistQ;
            public int MaxSafeDistQ;
            public int PredictedMPQ;
            public int Role;
            public int FamiliarPositionQ;
            public int MaxFamiliarDistQ;

            public bool Equals(RetreatPositionKey other) =>
                UnitId == other.UnitId
                && EnemiesHash == other.EnemiesHash
                && MinSafeDistQ == other.MinSafeDistQ
                && MaxSafeDistQ == other.MaxSafeDistQ
                && PredictedMPQ == other.PredictedMPQ
                && Role == other.Role
                && FamiliarPositionQ == other.FamiliarPositionQ
                && MaxFamiliarDistQ == other.MaxFamiliarDistQ;

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = UnitId;
                    h = h * 31 + (int)EnemiesHash;
                    h = h * 31 + (int)(EnemiesHash >> 32);
                    h = h * 31 + MinSafeDistQ;
                    h = h * 31 + MaxSafeDistQ;
                    h = h * 31 + PredictedMPQ;
                    h = h * 31 + Role;
                    h = h * 31 + FamiliarPositionQ;
                    h = h * 31 + MaxFamiliarDistQ;
                    return h;
                }
            }

            public override bool Equals(object obj) => obj is RetreatPositionKey k && Equals(k);
        }

        public static readonly Dictionary<RetreatPositionKey, BestPositionCacheEntry> FindRetreatCache = new Dictionary<RetreatPositionKey, BestPositionCacheEntry>();
        public static int FindRetreatCalls;
        public static int FindRetreatCacheHits;
        public static int FindRetreatCacheMisses;

        /// <summary>
        /// enemies 의 content hash — list reference 가 변해도 같은 적 set 이면 같은 hash.
        /// dedup cache 의 hash 함수와 동일 (재사용 패턴).
        /// </summary>
        public static long ComputeEnemiesContentHash(System.Collections.Generic.List<BaseUnitEntity> enemies)
        {
            unchecked
            {
                long h = 17;
                if (enemies != null)
                {
                    h = h * 31 + enemies.Count;
                    for (int i = 0; i < enemies.Count; i++)
                        h = h * 31 + (enemies[i]?.GetHashCode() ?? 0);
                }
                return h;
            }
        }

        /// <summary>Plan-scope dedup cache 무효화. 턴 시작 + caster 이동 후 호출.</summary>
        public static void ClearEvaluationCache()
        {
            int beforeSize = _evalCache.Count;
            if (beforeSize == 0) { _evalCache.Clear(); return; }  // 빈 cache clear 는 로그 skip
            int frame = UnityEngine.Time.frameCount;
            string callers;
            try
            {
                var st = new StackTrace(1, false);
                var sb = new System.Text.StringBuilder(64);
                int n = Math.Min(3, st.FrameCount);
                for (int i = 0; i < n; i++)
                {
                    if (i > 0) sb.Append(" ← ");
                    var m = st.GetFrame(i).GetMethod();
                    sb.Append(m?.DeclaringType?.Name ?? "?").Append('.').Append(m?.Name ?? "?");
                }
                callers = sb.ToString();
            }
            catch { callers = "?"; }
            Log.Engine.Debug($"[Perf] ClearEvaluationCache: cleared {beforeSize} eval + {FindRangedCache.Count} ranged + {FindMeleeCache.Count} melee + {FindRetreatCache.Count} retreat entries " +
                $"(rangedH={FindRangedCacheHits} rangedM={FindRangedCacheMisses} meleeH={FindMeleeCacheHits} meleeM={FindMeleeCacheMisses} retreatH={FindRetreatCacheHits} retreatM={FindRetreatCacheMisses}), frame={frame}, callers={callers}");
            _evalCache.Clear();
            FindRangedCache.Clear();
            FindRangedCacheHits = 0;
            FindRangedCacheMisses = 0;
            FindMeleeCache.Clear();
            FindMeleeCacheHits = 0;
            FindMeleeCacheMisses = 0;
            FindRetreatCache.Clear();
            FindRetreatCacheHits = 0;
            FindRetreatCacheMisses = 0;
        }

        public static List<PositionScore> EvaluateAllPositions(
            BaseUnitEntity unit,
            Dictionary<GraphNode, WarhammerPathAiCell> reachableTiles,
            List<BaseUnitEntity> enemies,
            MovementGoal goal,
            float targetDistance = 10f,
            float minSafeDistance = 5f)
        {
            var scores = new List<PositionScore>();
            if (unit == null || reachableTiles == null || reachableTiles.Count == 0)
                return scores;

            long totalStartTicks = Stopwatch.GetTimestamp();
            _perfLosTicks = 0;
            _perfLosCount = 0;
            int tileCount = 0;
            int enemyCount = enemies?.Count ?? 0;

            // 인자 hash — 같은 key 면 같은 인자, 다른 key 면 인자 변동.
            // v3.117.40: enemies 전체 hashCode 포함 (이전엔 첫 3개만 — SituationAnalyzer 가 매
            //   Analyze 마다 새 list 를 만들어 ReferenceEquals false reject 발생, 실측 cache hit rate 50%).
            //   content hash 로 같은 적 set 이면 같은 key → ReferenceEquals 없이 안전한 매칭.
            long argKey;
            unchecked
            {
                long h = 17;
                h = h * 31 + (unit?.GetHashCode() ?? 0);
                h = h * 31 + reachableTiles.Count;
                h = h * 31 + enemyCount;
                h = h * 31 + (int)goal;
                h = h * 31 + (int)(targetDistance * 100);
                h = h * 31 + (int)(minSafeDistance * 100);
                if (enemies != null)
                {
                    for (int i = 0; i < enemies.Count; i++)
                        h = h * 31 + (enemies[i]?.GetHashCode() ?? 0);
                }
                argKey = h;
            }

            // 진단: caller stack (3 depth) — replan/dedup 식별
            string callers;
            try
            {
                var st = new StackTrace(1, false);
                var sb = new System.Text.StringBuilder(64);
                int n = Math.Min(3, st.FrameCount);
                for (int i = 0; i < n; i++)
                {
                    if (i > 0) sb.Append(" ← ");
                    var m = st.GetFrame(i).GetMethod();
                    sb.Append(m?.DeclaringType?.Name ?? "?").Append('.').Append(m?.Name ?? "?");
                }
                callers = sb.ToString();
            }
            catch (Exception ex)
            {
                Log.Engine.Debug($"[Perf] caller stack parse failed: {ex.Message}");
                callers = "?";
            }
            int frame = UnityEngine.Time.frameCount;

            // Cache lookup — hash + value verification (false hit 차단).
            // v3.117.40: ReferenceEquals(EnemiesRef) 제거 — SituationAnalyzer 가 매 Analyze 마다
            //   새 list reference 를 생성하여 같은 적 set 인데도 false reject 되던 문제.
            //   argKey 가 모든 enemies content hash 포함하므로 length + Unit reference + scalar 값으로 충분.
            // v3.117.41 진단: cache miss/reject 사유 출력 — 진짜 invalidation 원인 추적.
            if (_evalCache.TryGetValue(argKey, out var snap))
            {
                string rejectReason = null;
                if (!ReferenceEquals(snap.Unit, unit))
                    rejectReason = $"unit-ref ({snap.Unit?.CharacterName}→{unit?.CharacterName})";
                else if (snap.Goal != goal)
                    rejectReason = $"goal ({snap.Goal}→{goal})";
                else if (snap.TargetDistance != targetDistance)
                    rejectReason = $"targetDist ({snap.TargetDistance}→{targetDistance})";
                else if (snap.MinSafeDistance != minSafeDistance)
                    rejectReason = $"minSafe ({snap.MinSafeDistance}→{minSafeDistance})";
                else if (snap.ReachableTileCount != reachableTiles.Count)
                    rejectReason = $"tileCount ({snap.ReachableTileCount}→{reachableTiles.Count})";
                else if (snap.EnemiesCount != enemyCount)
                    rejectReason = $"enemyCount ({snap.EnemiesCount}→{enemyCount})";

                if (rejectReason == null)
                {
                    EvalCacheHits++;
                    // Deep clone snapshot → caller mutation 격리
                    var copy = new List<PositionScore>(snap.Snapshot.Count);
                    foreach (var s in snap.Snapshot) copy.Add(s.Clone());
                    long hitTicks = Stopwatch.GetTimestamp() - totalStartTicks;
                    double hitMs = hitTicks * 1000.0 / Stopwatch.Frequency;
                    Log.Engine.Info($"[Perf] EvaluateAllPositions: CACHE HIT key=0x{argKey:X}, {copy.Count} scores cloned in {hitMs:F2}ms, frame={frame}, callers={callers}");
                    return copy;
                }
                else
                {
                    EvalCacheMisses++;
                    Log.Engine.Debug($"[Perf] EvaluateAllPositions: cache REJECT key=0x{argKey:X} reason={rejectReason}, frame={frame}, callers={callers}");
                }
            }
            else
            {
                EvalCacheMisses++;
                Log.Engine.Debug($"[Perf] EvaluateAllPositions: cache MISS key=0x{argKey:X} (cache size={_evalCache.Count}), frame={frame}, callers={callers}");
            }

            foreach (var kvp in reachableTiles)
            {
                var node = kvp.Key as CustomGridNodeBase;
                var cell = kvp.Value;

                if (node == null || !cell.IsCanStand)
                    continue;

                var score = EvaluatePosition(unit, node, cell, enemies, goal, targetDistance, minSafeDistance);
                scores.Add(score);
                tileCount++;
            }

            long totalTicks = Stopwatch.GetTimestamp() - totalStartTicks;
            double totalMs = totalTicks * 1000.0 / Stopwatch.Frequency;
            double losMs = _perfLosTicks * 1000.0 / Stopwatch.Frequency;
            Log.Engine.Debug($"[Perf] EvaluateAllPositions: {tileCount}t × {enemyCount}e, LOS {_perfLosCount}×{losMs:F1}ms, other {totalMs - losMs:F1}ms, total {totalMs:F1}ms, goal={goal}, unit={unit?.CharacterName}, key=0x{argKey:X}, frame={frame}, callers={callers}");

            // Cache 저장 — immutable snapshot (deep clone)
            {
                var snapshot = new List<PositionScore>(scores.Count);
                foreach (var s in scores) snapshot.Add(s.Clone());
                _evalCache[argKey] = new EvalSnapshot
                {
                    Unit = unit,
                    EnemiesCount = enemyCount,
                    Goal = goal,
                    TargetDistance = targetDistance,
                    MinSafeDistance = minSafeDistance,
                    ReachableTileCount = reachableTiles.Count,
                    Snapshot = snapshot,
                    Frame = frame
                };
            }

            // ★ v3.8.48: 정렬 제거 (호출자가 필요 시 직접 정렬)
            return scores;
        }

        public static PositionScore EvaluatePosition(
            BaseUnitEntity unit,
            CustomGridNodeBase node,
            WarhammerPathAiCell cell,
            List<BaseUnitEntity> enemies,
            MovementGoal goal,
            float targetDistance = 10f,
            float minSafeDistance = 5f)
        {
            var score = new PositionScore
            {
                Node = node,
                CanStand = cell.IsCanStand,
                APCost = cell.Length,
                ProvokedAttacks = cell.ProvokedAttacks,
                BestCover = LosCalculations.CoverType.None
            };

            if (enemies == null || enemies.Count == 0)
                return score;

            // ★ v3.111.1 Phase 6: CoverScore 공격자 관점 재설계.
            // 기존 [None=0, Half=15, Full=30, Invisible=40] 방어 aggregate → HideScore와 중복.
            // 신: 게임 fireCoverValues [None=1.0, Half=0.02, Full=0.0004, Invisible=0] 반영 — 공격 효율.
            // 적의 cover가 높을수록 우리 공격 효율 ↓. 평균 × 30으로 스케일 (0~30 범위).
            float fireEfficiencySum = 0f;
            float nearestEnemyDist = float.MaxValue;
            bool hasAnyLos = false;
            int hittableFromLos = 0;  // ★ v3.8.78: LOS 기반 hittable count (CountHittable 중복 제거)
            int validEnemyCount = 0;  // ★ v3.9.26: 유효 적 수 (dead/null 제외)

            foreach (var enemy in enemies)
            {
                if (enemy == null || enemy.LifeState.IsDead) continue;

                var enemyNode = enemy.Position.GetNearestNodeXZ() as CustomGridNodeBase;
                if (enemyNode == null) continue;

                validEnemyCount++;

                // ★ v3.6.1: 타일 단위로 변환 (minSafeDistance가 타일 단위)
                float dist = CombatAPI.MetersToTiles(Vector3.Distance(node.Vector3Position, enemy.Position));
                if (dist < nearestEnemyDist) nearestEnemyDist = dist;

                try
                {
                    long losStart = Stopwatch.GetTimestamp();
                    var los = LosCalculations.GetWarhammerLos(enemyNode, enemy.SizeRect, node, unit.SizeRect);
                    _perfLosTicks += Stopwatch.GetTimestamp() - losStart;
                    _perfLosCount++;
                    var coverType = los.CoverType;

                    if (coverType != LosCalculations.CoverType.Invisible)
                    {
                        hasAnyLos = true;
                        hittableFromLos++;  // ★ v3.8.78: LOS 있으면 hittable 카운트
                    }

                    // coverType 기반 fire efficiency 누적 (LOS 대칭 가정 — 벽 기반 cover는 양방향 동일)
                    float fireEff = 0f;
                    switch (coverType)
                    {
                        case LosCalculations.CoverType.None:      fireEff = 1.0f;    break;
                        case LosCalculations.CoverType.Half:      fireEff = 0.02f;   break;
                        case LosCalculations.CoverType.Full:      fireEff = 0.0004f; break;
                        case LosCalculations.CoverType.Invisible: fireEff = 0f;      break;
                    }
                    fireEfficiencySum += fireEff;

                    if (coverType > score.BestCover)
                        score.BestCover = coverType;
                }
                catch { }
            }

            // ★ v3.111.1 Phase 6: 공격자 관점 fire efficiency 평균 × 30.
            // 0~30 범위 (모두 완전 노출 = 30, 모두 Full cover = ~0.01).
            float avgFireEff = validEnemyCount > 0 ? fireEfficiencySum / validEnemyCount : 0f;
            score.CoverScore = avgFireEff * 30f;
            score.HasLosToEnemy = hasAnyLos;
            score.HittableEnemyCount = hittableFromLos;  // ★ v3.8.78: LOS 기반 hittable count

            // ★ v3.111.0 Phase 5: predictedMoves 주어지면 ensured cover (적 예상 이동 후에도 유지되는 엄폐),
            //   없으면 Phase 1a fallback (적 현재 위치 기반).
            // _currentPredictedMoves는 SituationAnalyzer가 턴 시작 시 SetPredictedMoves로 설정.
            var pm = _currentPredictedMoves;
            var hideComponents = pm != null
                ? TileScorerPort.GetEnsuredCoverComponents(node, unit.SizeRect, enemies, pm)
                : TileScorerPort.GetHideScoreComponents(node, unit.SizeRect, enemies);
            score.ApplyHideComponents(hideComponents);

            // ★ v3.110.20 Phase 2: 적별 Turn Threat Score 합산.
            // 게임 EnemyThreatScore 패턴 — 각 적이 이 턴에 이 위치를 공격 가능한가.
            // threatRange (게임 학습 + 무기 사거리) + AP_Blue 기반.
            float turnThreatSum = 0f;
            foreach (var enemy in enemies)
            {
                if (enemy == null || enemy.LifeState.IsDead) continue;
                turnThreatSum += CombatAPI.GetEnemyTurnThreatScore(enemy, node.Vector3Position);
            }
            score.EnemyTurnThreatSum = turnThreatSum;

            // ★ v3.110.15: ExposureScore — "이 위치를 공격 가능한 적 수"를 페널티화.
            // hittableFromLos는 대칭 LOS(enemyNode → node) 계산이라 "적→자신 LOS 수"와 동일.
            //
            // 공식: sqrt(exposed) × 10
            //   1명: 10, 3명: 17, 5명: 22, 8명: 28, 10명: 32, 15명: 39, 20명: 45
            // 이전 v3.110.15a 공식 `min(hittable, 5) × 5`는 대부분 전장에서 5+ 노출이라 모두 cap=25로
            // saturate → 변별력 0. 실증 로그 27건 전부 -25.0 동일.
            //
            // sqrt 감쇠로 cap 제거. 많은 적에 노출될수록 더 큰 페널티지만 증가율 감소.
            // Attack score(53 평균, 83 최대)와 균형 — 충분히 유의미하되 공격 기회를 완전히 포기시키진 않음.
            // InfluenceMap threat축의 원래 의도를 게임 API로 정확히 구현 (벽/고저차/엄폐 자동 반영).
            score.ExposureScore = hittableFromLos > 0
                ? Mathf.Sqrt(hittableFromLos) * 10f
                : 0f;

            // ★ v3.110.22 Phase 4: StayingAwayScore — 적 이동능력 반영 안전거리.
            //   goal별 가중치: Retreat/FindCover 적극적 거리 유지, 근접 approach는 낮음.
            score.StayingAwayScore = TileScorerPort.GetStayingAwayScore(node, unit, enemies);
            float stayingWeight;
            switch (goal)
            {
                case MovementGoal.Retreat:              stayingWeight = 40f; break;
                case MovementGoal.FindCover:            stayingWeight = 30f; break;
                case MovementGoal.RangedAttackPosition: stayingWeight = 25f; break;
                case MovementGoal.MaintainDistance:     stayingWeight = 20f; break;
                default:                                 stayingWeight = 10f; break;  // Approach/AttackPosition 등 근접 계열
            }
            score.StayingAwayBonus = score.StayingAwayScore * stayingWeight;

            switch (goal)
            {
                case MovementGoal.FindCover:
                case MovementGoal.Retreat:
                    score.DistanceScore = Math.Min(30f, nearestEnemyDist * 2f);
                    break;

                case MovementGoal.MaintainDistance:
                    float distDiff = Math.Abs(nearestEnemyDist - targetDistance);
                    score.DistanceScore = Math.Max(0f, 20f - distDiff * 2f);
                    break;

                case MovementGoal.ApproachEnemy:
                    score.DistanceScore = Math.Max(0f, 30f - nearestEnemyDist * 2f);
                    break;

                case MovementGoal.AttackPosition:
                    if (nearestEnemyDist <= targetDistance && nearestEnemyDist >= 3f)
                        score.DistanceScore = 25f;
                    else if (nearestEnemyDist <= targetDistance)
                        score.DistanceScore = 15f;
                    else
                        score.DistanceScore = 0f;
                    break;

                case MovementGoal.RangedAttackPosition:
                    float weaponRange = targetDistance;

                    if (nearestEnemyDist < minSafeDistance)
                    {
                        // 안전 거리 미만 = 위험 (변경 없음)
                        score.DistanceScore = -50f + nearestEnemyDist * 5f;
                    }
                    else if (nearestEnemyDist <= weaponRange)
                    {
                        // ★ v3.110.8: 게임 공식 일치 — RuleCalculateAbilityDistanceFactor에 따르면
                        //   d ≤ MaxD/2 → DistanceFactor 1.0 (풀 명중률, hitChance ≈ (BS+30)×1.0)
                        //   d ≤ MaxD   → DistanceFactor 0.5 (반토막, hitChance ≈ (BS+30)×0.5)
                        // optimalRatio = 0.5 (game MaxD/2) + weaponRange 기준 정규화 (minSafe 배제).
                        // 이전 (v3.9.48 ~ v3.110.7): optimalRatio=0.6 + minSafe 정규화 → optimal d = minSafe + 0.6×(MaxD-minSafe).
                        // 예: MaxD=15, minSafe=5 → optimal d=11. 그러나 게임 공식 optimal = MaxD/2 = 7.5타일.
                        // 즉 이전 공식은 게임 공식보다 3.5타일 멀리 최적점 설정 → DistanceFactor 0.5 영역(반토막 명중률) 선호 버그.
                        // 이차 감쇠 형태는 유지 (tie-break 연속성).
                        float distRatio = weaponRange > 0.1f ? (nearestEnemyDist / weaponRange) : 0.5f;
                        float optimalRatio = 0.5f;
                        float deviation = Math.Abs(distRatio - optimalRatio);
                        score.DistanceScore = 25f - (deviation * deviation) * 60f;
                    }
                    else
                    {
                        // 무기 사거리 초과 = 접근 필요
                        score.DistanceScore = Math.Max(0f, 10f - (nearestEnemyDist - weaponRange) * 2f);
                    }
                    break;
            }

            score.ThreatScore = cell.ProvokedAttacks * WEIGHT_AOO + cell.EnteredAoE * WEIGHT_AOE_ENTRY;

            if (hasAnyLos && nearestEnemyDist <= targetDistance)
                score.AttackScore = 20f;

            // ★ v3.9.50: Hittable 적 수 보너스 — 공격 가능 위치에 적극적 보너스
            // 방어 패널티만 있고 공격 기회 보너스가 없으면 항상 후퇴가 유리해짐
            //
            // ★ v3.110.9: 포화 곡선으로 변경. 이전 hittable × 8 선형 → hittable 16명이면 +128점
            //   단일 축이 총점의 35~45% 지배 → "멀리서 많이 보이는 위치" 절대 선호 (근거리 소수 커버 밀림).
            // 현재: 1~3명 강보상(+10/명), 4명+부터 sqrt 감쇠.
            //   1명→10, 3명→30, 6명→44, 16명→59.
            //   16명 대 1명 비중 128:8 = 16배 → 60:10 = 6배로 축소.
            //   AoE multi-hit 기회는 여전히 선호하되 과도한 독주는 완화.
            if (hittableFromLos > 0)
            {
                int hc = hittableFromLos;
                float baseBonus = Math.Min(hc, 3) * 10f;
                float extraBonus = hc > 3 ? (float)Math.Sqrt(hc - 3) * 8f : 0f;
                score.AttackScore += baseBonus + extraBonus;
            }

            return score;
        }

        #endregion
    }
}
