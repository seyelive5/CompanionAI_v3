// Planning/LLM/InitiativeTracker.cs
// ★ Initiative Awareness: 적의 행동 순서를 현재 유닛 기준 상대 인덱스로 변환.
// LLM이 "이 적이 내 다음 행동 전에 나를 공격하는가?" 판단할 수 있도록 함.
using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;

namespace CompanionAI_v3.Planning.LLM
{
    /// <summary>
    /// 게임의 TurnController.UnitsAndSquadsByInitiativeForCurrentTurn 큐를
    /// 현재 유닛 기준 적-T번호 매핑으로 변환.
    ///
    /// T번호 의미:
    ///   T1, T2, T3... = 자신의 다음 차례 전에 행동하는 적 (1=가장 먼저)
    ///   미포함 (호출자가 T+R로 처리) = 자신의 다음 차례 이후에 행동
    ///
    /// 라운드 경계: 자신 다음 차례까지의 거리는 (현재 라운드 잔여) + (다음 라운드 시작~자신 직전).
    /// 큐는 현재 라운드만 있으므로 wrap-around 처리.
    /// </summary>
    public static class InitiativeTracker
    {
        private static readonly Dictionary<BaseUnitEntity, int> EmptyResult
            = new Dictionary<BaseUnitEntity, int>();

        /// <summary>
        /// 현재 유닛 다음 차례 전에 행동하는 적들의 T번호 매핑.
        /// 큐가 비어있거나 self를 못 찾거나 예외 발생 시 빈 Dictionary 반환.
        /// </summary>
        public static Dictionary<BaseUnitEntity, int> GetEnemiesBeforeNextTurn(BaseUnitEntity self)
        {
            if (self == null) return EmptyResult;

            try
            {
                var turnController = Game.Instance?.TurnController;
                if (turnController == null) return EmptyResult;

                var queueEnumerable = turnController.UnitsAndSquadsByInitiativeForCurrentTurn;
                if (queueEnumerable == null) return EmptyResult;

                // BaseUnitEntity로 필터링 (UnitSquad 등 비유닛 제외)
                var queue = queueEnumerable
                    .OfType<BaseUnitEntity>()
                    .ToList();

                if (queue.Count == 0) return EmptyResult;

                int selfIdx = queue.IndexOf(self);
                if (selfIdx < 0) return EmptyResult;

                var result = new Dictionary<BaseUnitEntity, int>();
                int counter = 1;

                // 1단계: 자신 이후 ~ 큐 끝
                for (int i = selfIdx + 1; i < queue.Count; i++)
                {
                    if (IsEnemy(queue[i], self))
                    {
                        result[queue[i]] = counter++;
                    }
                }

                // 2단계: 큐 시작 ~ 자신 직전 (다음 라운드 wrap-around)
                for (int i = 0; i < selfIdx; i++)
                {
                    if (IsEnemy(queue[i], self))
                    {
                        result[queue[i]] = counter++;
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[InitiativeTracker] Failed: {ex.Message}");
                return EmptyResult;
            }
        }

        /// <summary>적 판별 — 현재 유닛 기준 적대 관계인지</summary>
        private static bool IsEnemy(BaseUnitEntity candidate, BaseUnitEntity self)
        {
            if (candidate == null || self == null) return false;
            if (candidate == self) return false;
            try
            {
                return candidate.IsEnemy(self);
            }
            catch
            {
                return false;
            }
        }
    }
}
