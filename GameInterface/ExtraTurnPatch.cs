using System;
using System.Collections.Generic;
using HarmonyLib;
using Kingmaker.Controllers.TurnBased;
using Kingmaker.EntitySystem.Entities;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// ★ v3.111.8: 임시턴(ExtraTurn) 감지 — InterruptionData 캡처.
    ///
    /// 게임에서 "쳐부숴라" 같은 능력은 다른 캐릭터에게 임시턴(추가 턴)을 부여한다.
    /// 이 턴은 정상 턴이 아니며 GrantedAP/MP(보통 1-2 AP, 0 MP)로 제약된다.
    /// 정상 턴처럼 이동/aggressive relocate/이동 필요 도발을 시도하면 실패하거나 엉뚱한
    /// fallback 동작을 한다. → TurnController.StartUnitTurnInternal Postfix로
    /// InterruptionData.AsExtraTurn + GrantedAP/MP를 static cache에 저장.
    /// Situation이 AnalyzeUnitState에서 조회 → Plan이 저자원 가드에 사용.
    ///
    /// 디컴파일 참조: Kingmaker.Controllers.TurnBased.TurnController.StartUnitTurnInternal
    /// 시그니처: private void StartUnitTurnInternal(MechanicEntity entity, bool isTurnBased, InterruptionData interruptionData)
    /// 동기 메서드 (async 아님) — Phase 5 async hook과 달리 단순.
    /// </summary>
    public static class ExtraTurnCache
    {
        public struct ExtraTurnInfo
        {
            public bool IsExtraTurn;
            public int GrantedAP;
            public int GrantedMP;
            // RestrictionsOnInterrupt는 복잡한 타입이라 일단 보관 안 함 (Step B에서 추가 가능).
        }

        private static readonly Dictionary<BaseUnitEntity, ExtraTurnInfo> _cache
            = new Dictionary<BaseUnitEntity, ExtraTurnInfo>();
        private static readonly object _lock = new object();

        public static void Store(BaseUnitEntity unit, ExtraTurnInfo info)
        {
            if (unit == null) return;
            lock (_lock) _cache[unit] = info;
        }

        public static ExtraTurnInfo Get(BaseUnitEntity unit)
        {
            if (unit == null) return default;
            lock (_lock) return _cache.TryGetValue(unit, out var info) ? info : default;
        }

        public static void ClearUnit(BaseUnitEntity unit)
        {
            if (unit == null) return;
            lock (_lock) _cache.Remove(unit);
        }

        public static void Clear()
        {
            lock (_lock) _cache.Clear();
        }

        public static int Count { get { lock (_lock) return _cache.Count; } }
    }

    [HarmonyPatch]
    public static class ExtraTurnPatch
    {
        /// <summary>
        /// TurnController.StartUnitTurnInternal Postfix.
        /// 게임 원본: private void StartUnitTurnInternal(MechanicEntity entity, bool isTurnBased, InterruptionData interruptionData)
        ///
        /// AsExtraTurn == true이면 캐시에 저장, 아니면 이전 플래그 제거.
        /// 우리 모드는 BaseUnitEntity 단위로 Situation을 만드므로 MechanicEntity → BaseUnitEntity 캐스팅.
        /// </summary>
        [HarmonyPatch(typeof(TurnController), "StartUnitTurnInternal")]
        [HarmonyPostfix]
        public static void StartUnitTurnInternal_Postfix(MechanicEntity entity, bool isTurnBased, InterruptionData interruptionData)
        {
            try
            {
                var unit = entity as BaseUnitEntity;
                if (unit == null) return;

                if (interruptionData != null && interruptionData.AsExtraTurn)
                {
                    var info = new ExtraTurnCache.ExtraTurnInfo
                    {
                        IsExtraTurn = true,
                        GrantedAP = interruptionData.GrantedAP,
                        GrantedMP = interruptionData.GrantedMP,
                    };
                    ExtraTurnCache.Store(unit, info);

                    if (Main.IsDebugEnabled)
                        Main.LogDebug($"[ExtraTurn] Captured for {unit.CharacterName}: ExtraTurn={info.IsExtraTurn}, AP={info.GrantedAP}, MP={info.GrantedMP}");
                }
                else
                {
                    // 정상 턴으로 진입 → 이전 임시턴 플래그 제거 (같은 유닛이 나중에 정상 턴을 받을 때 오염 방지).
                    ExtraTurnCache.ClearUnit(unit);
                }
            }
            catch (Exception ex)
            {
                Main.LogError($"[ExtraTurnPatch] Postfix failed: {ex.Message}");
            }
        }
    }
}
