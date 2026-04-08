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
    /// 게임의 TurnController 큐를 현재 유닛 기준 적-T번호 매핑으로 변환.
    ///
    /// T번호 의미:
    ///   T1, T2, T3... = 자신의 다음 차례 전에 행동하는 적 (1=가장 먼저)
    ///   미포함 (호출자가 T+R로 처리) = 자신의 다음 차례 이후에 행동
    ///
    /// 사용 큐:
    ///   CurrentRoundUnitsOrder = 이번 라운드에 아직 행동 안 한 유닛 (self 포함, index 0)
    ///   NextRoundUnitsOrder    = 이번 라운드에 이미 행동한 유닛 (next 라운드 시작에 다시 행동)
    ///
    /// 알고리즘:
    ///   Phase 1: CurrentRoundUnitsOrder의 self 이후 (이번 라운드 잔여 적)
    ///   Phase 2: NextRoundUnitsOrder 전체 (다음 라운드 시작 ~ self 다음 차례 전)
    ///
    /// 주의: NextRoundUnitsOrder에 self는 포함되지 않음 (아직 행동 안 했으므로).
    /// 모든 next-round 적을 카운트하는 것은 약간의 over-counting이지만,
    /// 게임 UI의 initiative tracker가 보여주는 것과 일치하므로 LLM에게 자연스러움.
    /// </summary>
    public static class InitiativeTracker
    {
        /// <summary>
        /// 현재 유닛 다음 차례 전에 행동하는 적들의 T번호 매핑.
        /// 큐가 비어있거나 self를 못 찾거나 예외 발생 시 빈 Dictionary 반환 (fresh allocation).
        /// </summary>
        public static Dictionary<BaseUnitEntity, int> GetEnemiesBeforeNextTurn(BaseUnitEntity self)
        {
            var result = new Dictionary<BaseUnitEntity, int>();

            if (self == null) return result;

            try
            {
                var turnController = Game.Instance?.TurnController;
                if (turnController == null) return result;

                // 두 큐 모두 가져오기
                var currentEnumerable = turnController.UnitsAndSquadsByInitiativeForCurrentTurn;
                var nextEnumerable = turnController.UnitsAndSquadsByInitiativeForNextTurn;

                int counter = 1;

                // Phase 1: 이번 라운드 잔여 — self 이후의 적들
                // Filter to real units — skips UnitSquad containers, InitiativePlaceholderEntity, etc.
                if (currentEnumerable != null)
                {
                    var current = currentEnumerable.OfType<BaseUnitEntity>().ToList();

                    // self는 일반적으로 current[0] (현재 행동 중인 유닛)
                    // 안전을 위해 IndexOf로 위치 확인 후, 그 이후만 처리
                    int selfIdx = current.IndexOf(self);
                    int startIdx = (selfIdx >= 0) ? selfIdx + 1 : 0;

                    for (int i = startIdx; i < current.Count; i++)
                    {
                        if (IsEnemy(current[i], self))
                        {
                            result[current[i]] = counter++;
                        }
                    }
                }

                // Phase 2: 다음 라운드 — 이미 행동한 유닛들이 다음 라운드 시작 시 행동
                // self는 이 큐에 없음 (아직 행동 안 했으므로)
                if (nextEnumerable != null)
                {
                    foreach (var entity in nextEnumerable)
                    {
                        if (entity is BaseUnitEntity unit && IsEnemy(unit, self))
                        {
                            // 중복 방지 (Phase 1과 Phase 2 사이에 같은 유닛이 있을 수 없지만 안전장치)
                            if (!result.ContainsKey(unit))
                            {
                                result[unit] = counter++;
                            }
                        }
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"[InitiativeTracker] Failed: {ex.Message}");
                return new Dictionary<BaseUnitEntity, int>();
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
