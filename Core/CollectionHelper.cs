// ★ v3.8.48: Zero-allocation collection utilities replacing LINQ hot paths
using System;
using System.Collections.Generic;

namespace CompanionAI_v3.Core
{
    /// <summary>
    /// GC 압박 없는 컬렉션 유틸리티. LINQ의 OrderBy/Select/FirstOrDefault 체인을 대체.
    /// 모든 메서드는 for 루프 기반으로 IEnumerator 할당 없이 O(n)에 동작.
    /// </summary>
    public static class CollectionHelper
    {
        /// <summary>
        /// 리스트에서 scorer가 가장 높은 요소를 반환. (0 할당, O(n))
        /// LINQ: items.OrderByDescending(scorer).FirstOrDefault() 대체
        /// </summary>
        public static T MaxBy<T>(IList<T> items, Func<T, float> scorer) where T : class
        {
            if (items == null || items.Count == 0) return null;

            T best = null;
            float bestScore = float.MinValue;

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null) continue;
                float score = scorer(item);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = item;
                }
            }
            return best;
        }

        /// <summary>
        /// 리스트에서 scorer가 가장 높은 요소와 그 점수를 반환. (0 할당, O(n))
        /// </summary>
        public static T MaxBy<T>(IList<T> items, Func<T, float> scorer, out float bestScore) where T : class
        {
            bestScore = float.MinValue;
            if (items == null || items.Count == 0) return null;

            T best = null;

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null) continue;
                float score = scorer(item);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = item;
                }
            }
            return best;
        }

        /// <summary>
        /// 리스트에서 scorer가 가장 낮은 요소를 반환. (0 할당, O(n))
        /// LINQ: items.OrderBy(scorer).FirstOrDefault() 대체
        /// </summary>
        public static T MinBy<T>(IList<T> items, Func<T, float> scorer) where T : class
        {
            if (items == null || items.Count == 0) return null;

            T best = null;
            float bestScore = float.MaxValue;

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null) continue;
                float score = scorer(item);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = item;
                }
            }
            return best;
        }

        /// <summary>
        /// minScore 이상인 요소 중 scorer가 가장 높은 요소를 반환. (0 할당, O(n))
        /// LINQ: items.Where(s > minScore).OrderByDescending(scorer).FirstOrDefault() 대체
        /// </summary>
        public static T MaxByWithThreshold<T>(IList<T> items, Func<T, float> scorer, float minScore) where T : class
        {
            if (items == null || items.Count == 0) return null;

            T best = null;
            float bestScore = float.MinValue;

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null) continue;
                float score = scorer(item);
                if (score >= minScore && score > bestScore)
                {
                    bestScore = score;
                    best = item;
                }
            }
            return best;
        }

        /// <summary>
        /// 필터 조건을 만족하는 요소 중 scorer가 가장 높은 요소를 반환. (0 할당, O(n))
        /// LINQ: items.Where(predicate).OrderByDescending(scorer).FirstOrDefault() 대체
        /// </summary>
        public static T MaxByWhere<T>(IList<T> items, Func<T, bool> predicate, Func<T, float> scorer) where T : class
        {
            if (items == null || items.Count == 0) return null;

            T best = null;
            float bestScore = float.MinValue;

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null) continue;
                if (!predicate(item)) continue;
                float score = scorer(item);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = item;
                }
            }
            return best;
        }

        /// <summary>
        /// 필터 조건을 만족하는 요소 중 scorer가 가장 높은 요소와 그 점수를 반환. (0 할당, O(n))
        /// </summary>
        public static T MaxByWhere<T>(IList<T> items, Func<T, bool> predicate, Func<T, float> scorer, out float bestScore) where T : class
        {
            bestScore = float.MinValue;
            if (items == null || items.Count == 0) return null;

            T best = null;

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null) continue;
                if (!predicate(item)) continue;
                float score = scorer(item);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = item;
                }
            }
            return best;
        }

        /// <summary>
        /// 필터 조건을 만족하는 요소 중 scorer가 가장 낮은 요소를 반환. (0 할당, O(n))
        /// LINQ: items.Where(predicate).OrderBy(scorer).FirstOrDefault() 대체
        /// </summary>
        public static T MinByWhere<T>(IList<T> items, Func<T, bool> predicate, Func<T, float> scorer) where T : class
        {
            if (items == null || items.Count == 0) return null;

            T best = null;
            float bestScore = float.MaxValue;

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null) continue;
                if (!predicate(item)) continue;
                float score = scorer(item);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = item;
                }
            }
            return best;
        }

        /// <summary>
        /// 필터 조건을 만족하는 요소 중 scorer가 가장 낮은 요소와 그 점수를 반환. (0 할당, O(n))
        /// </summary>
        public static T MinByWhere<T>(IList<T> items, Func<T, bool> predicate, Func<T, float> scorer, out float bestScore) where T : class
        {
            bestScore = float.MaxValue;
            if (items == null || items.Count == 0) return null;

            T best = null;

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null) continue;
                if (!predicate(item)) continue;
                float score = scorer(item);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = item;
                }
            }
            return best;
        }

        /// <summary>
        /// 리스트에서 조건을 만족하는 첫 번째 요소를 반환. (0 할당, O(n))
        /// LINQ: items.FirstOrDefault(predicate) 대체
        /// </summary>
        public static T FirstOrDefault<T>(IList<T> items, Func<T, bool> predicate) where T : class
        {
            if (items == null) return null;

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item != null && predicate(item))
                    return item;
            }
            return null;
        }

        /// <summary>
        /// 리스트에서 조건을 만족하는 요소의 수를 반환. (0 할당, O(n))
        /// LINQ: items.Count(predicate) 대체
        /// </summary>
        public static int CountWhere<T>(IList<T> items, Func<T, bool> predicate)
        {
            if (items == null) return 0;

            int count = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] != null && predicate(items[i]))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// 리스트에서 조건을 만족하는 요소가 있는지 확인. (0 할당, O(n))
        /// LINQ: items.Any(predicate) 대체
        /// </summary>
        public static bool Any<T>(IList<T> items, Func<T, bool> predicate)
        {
            if (items == null) return false;

            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] != null && predicate(items[i]))
                    return true;
            }
            return false;
        }
    }
}
