using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using UnityEngine;
using CompanionAI_v3.GameInterface;

namespace CompanionAI_v3.Core
{
    /// <summary>
    /// 계획된 단일 행동
    /// TurnPlanner가 생성하고, ActionExecutor가 실행
    /// </summary>
    public class PlannedAction
    {
        /// <summary>행동 유형</summary>
        public ActionType Type { get; set; }

        /// <summary>사용할 능력 (Attack, Buff, Heal 등)</summary>
        public AbilityData Ability { get; set; }

        /// <summary>능력의 타겟</summary>
        public TargetWrapper Target { get; set; }

        /// <summary>이동 목적지 (Move 타입일 때)</summary>
        public Vector3? MoveDestination { get; set; }

        /// <summary>이 행동의 AP 비용</summary>
        public float APCost { get; set; }

        /// <summary>이 행동을 선택한 이유</summary>
        public string Reason { get; set; }

        /// <summary>우선순위 (낮을수록 먼저 실행)</summary>
        public int Priority { get; set; }

        /// <summary>
        /// ★ v3.7.20: 사역마 타겟 플래그
        /// true일 경우 실행 시점에 FamiliarAPI.GetFamiliar()로 타겟 재해석
        /// 계획 시점의 엔티티 참조가 stale해지는 문제 방지
        /// </summary>
        public bool IsFamiliarTarget { get; set; }

        /// <summary>
        /// ★ v3.7.25: MultiTarget 능력용 타겟 리스트
        /// AerialRush 등 2개 이상의 Point 타겟이 필요한 능력에 사용
        /// </summary>
        public List<TargetWrapper> AllTargets { get; set; }

        /// <summary>실행 완료 여부</summary>
        public bool IsExecuted { get; set; }

        /// <summary>실행 결과</summary>
        public bool? WasSuccessful { get; set; }

        #region Factory Methods

        public static PlannedAction Buff(AbilityData ability, BaseUnitEntity self, string reason, float apCost)
        {
            return new PlannedAction
            {
                Type = ActionType.Buff,
                Ability = ability,
                Target = new TargetWrapper(self),
                APCost = apCost,
                Reason = reason,
                Priority = 10
            };
        }

        public static PlannedAction Attack(AbilityData ability, BaseUnitEntity target, string reason, float apCost)
        {
            // ★ v3.7.80: Point 타겟 능력은 계획 단계에서부터 위치 기반으로 생성
            // 사이킥 비명 등 Point AOE 능력이 Unit 타겟으로 잘못 생성되는 것 방지
            // ★ v3.8.08: Directional 패턴 (Ray/Cone/Sector)은 Unit 타겟 유지!
            // - Ray/Cone은 방향이 필요하므로 Unit 타겟으로 방향 결정
            // - Point 타겟 시 ContextConditionIsAlly 등이 "Target unit is missing" 에러 발생
            // ★ v3.8.09: GetActualIsDirectional() 사용으로 게임 실제 로직 적용
            var patternType = CombatAPI.GetPatternType(ability);
            bool isActuallyDirectional = CombatAPI.GetActualIsDirectional(ability);

            if (CombatAPI.IsPointTargetAbility(ability) && !isActuallyDirectional)
            {
                Main.LogDebug($"[PlannedAction] Attack auto-converted to PositionalAttack: {ability.Name} (Point-target ability, non-directional)");
                return PositionalAttack(ability, target.Position, reason, apCost);
            }

            if (isActuallyDirectional)
            {
                Main.LogDebug($"[PlannedAction] Directional pattern kept as unit target: {ability.Name} (PatternType={patternType}, IsDirectional=true)");
            }

            return new PlannedAction
            {
                Type = ActionType.Attack,
                Ability = ability,
                Target = new TargetWrapper(target),
                APCost = apCost,
                Reason = reason,
                Priority = 50
            };
        }

        public static PlannedAction Reload(AbilityData ability, BaseUnitEntity self, float apCost)
        {
            return new PlannedAction
            {
                Type = ActionType.Reload,
                Ability = ability,
                Target = new TargetWrapper(self),
                APCost = apCost,
                Reason = "Reload weapon",
                Priority = 5
            };
        }

        public static PlannedAction Heal(AbilityData ability, BaseUnitEntity target, string reason, float apCost)
        {
            return new PlannedAction
            {
                Type = ActionType.Heal,
                Ability = ability,
                Target = new TargetWrapper(target),
                APCost = apCost,
                Reason = reason,
                Priority = 1
            };
        }

        public static PlannedAction Move(Vector3 destination, string reason)
        {
            return new PlannedAction
            {
                Type = ActionType.Move,
                MoveDestination = destination,
                APCost = 0,  // 이동은 MP 소모
                Reason = reason,
                Priority = 20
            };
        }

        public static PlannedAction Debuff(AbilityData ability, BaseUnitEntity target, string reason, float apCost)
        {
            // ★ v3.7.80: Point 타겟 능력은 계획 단계에서부터 위치 기반으로 생성
            if (CombatAPI.IsPointTargetAbility(ability))
            {
                Main.LogDebug($"[PlannedAction] Debuff auto-converted to PositionalDebuff: {ability.Name} (Point-target ability)");
                return PositionalDebuff(ability, target.Position, reason, apCost);
            }

            return new PlannedAction
            {
                Type = ActionType.Debuff,
                Ability = ability,
                Target = new TargetWrapper(target),
                APCost = apCost,
                Reason = reason,
                Priority = 30
            };
        }

        /// <summary>
        /// ★ v3.7.80: 위치 타겟 디버프 (Point AOE 디버프 능력)
        /// </summary>
        public static PlannedAction PositionalDebuff(AbilityData ability, Vector3 position, string reason, float apCost)
        {
            return new PlannedAction
            {
                Type = ActionType.Debuff,
                Ability = ability,
                Target = new TargetWrapper(position),
                APCost = apCost,
                Reason = reason,
                Priority = 28  // 일반 Debuff(30)보다 약간 우선
            };
        }

        public static PlannedAction Support(AbilityData ability, BaseUnitEntity target, string reason, float apCost)
        {
            return new PlannedAction
            {
                Type = ActionType.Support,
                Ability = ability,
                Target = new TargetWrapper(target),
                APCost = apCost,
                Reason = reason,
                Priority = 15
            };
        }

        /// <summary>
        /// ★ v3.0.21: 위치 타겟 버프 (전방/보조/후방 구역 등)
        /// </summary>
        public static PlannedAction PositionalBuff(AbilityData ability, Vector3 position, string reason, float apCost)
        {
            return new PlannedAction
            {
                Type = ActionType.Support,  // Support 타입 재사용 (실행 로직 동일)
                Ability = ability,
                Target = new TargetWrapper(position),
                APCost = apCost,
                Reason = reason,
                Priority = 12  // 일반 Support(15)보다 약간 우선
            };
        }

        /// <summary>
        /// ★ v3.0.81: 위치 타겟 공격 (Death from Above 등 갭클로저)
        /// </summary>
        public static PlannedAction PositionalAttack(AbilityData ability, Vector3 position, string reason, float apCost)
        {
            return new PlannedAction
            {
                Type = ActionType.Attack,
                Ability = ability,
                Target = new TargetWrapper(position),
                APCost = apCost,
                Reason = reason,
                Priority = 25  // 일반 Attack(50)보다 우선 (갭클로저 → 공격 연계)
            };
        }

        /// <summary>
        /// ★ v3.7.25: MultiTarget 공격 (AerialRush 등 2개 Point 필요)
        /// </summary>
        public static PlannedAction MultiTargetAttack(AbilityData ability, List<TargetWrapper> allTargets, string reason, float apCost)
        {
            return new PlannedAction
            {
                Type = ActionType.Attack,
                Ability = ability,
                Target = allTargets?.Count > 0 ? allTargets[0] : null,
                AllTargets = allTargets,
                APCost = apCost,
                Reason = reason,
                Priority = 25  // 갭클로저 수준 우선순위
            };
        }

        /// <summary>
        /// ★ v3.8.25: MultiTarget 서포트 (Familiar Relocate 등)
        /// PropertyCalculatorComponent.ForMainTarget 컨텍스트가 필요한 능력에 사용
        /// - AllTargets 경로 사용으로 UnitUseAbilityParams 직접 실행 (BehaviourTree 컨텍스트 우회)
        /// - MyPet 평가기가 올바른 Caster 컨텍스트 획득
        /// </summary>
        public static PlannedAction MultiTargetSupport(AbilityData ability, List<TargetWrapper> allTargets, string reason, float apCost)
        {
            return new PlannedAction
            {
                Type = ActionType.Support,
                Ability = ability,
                Target = allTargets?.Count > 0 ? allTargets[0] : null,
                AllTargets = allTargets,
                APCost = apCost,
                Reason = reason,
                Priority = 12  // PositionalBuff와 동일한 우선순위
            };
        }

        /// <summary>
        /// ★ v3.7.45: 이동 후 MultiTarget 공격
        /// Master가 먼저 이동한 후 Familiar 능력 사용 (Overseer 핵심 패턴)
        /// </summary>
        public static PlannedAction MoveThenMultiTargetAttack(Vector3 moveDestination, AbilityData ability, List<TargetWrapper> allTargets, string reason, float apCost)
        {
            return new PlannedAction
            {
                Type = ActionType.Attack,
                Ability = ability,
                Target = allTargets?.Count > 0 ? allTargets[0] : null,
                AllTargets = allTargets,
                MoveDestination = moveDestination,  // 이동 목적지 설정
                APCost = apCost,
                Reason = reason,
                Priority = 20  // Move보다 우선, 갭클로저보다 약간 우선
            };
        }

        /// <summary>
        /// ★ v3.1.17: 위치 타겟 힐 (AOE 힐 등)
        /// </summary>
        public static PlannedAction PositionalHeal(AbilityData ability, Vector3 position, string reason, float apCost)
        {
            return new PlannedAction
            {
                Type = ActionType.Heal,
                Ability = ability,
                Target = new TargetWrapper(position),
                APCost = apCost,
                Reason = reason,
                Priority = 2  // 일반 Heal(1)보다 약간 후순위
            };
        }

        public static PlannedAction EndTurn(string reason = "No more actions available")
        {
            return new PlannedAction
            {
                Type = ActionType.EndTurn,
                Reason = reason,
                Priority = 100
            };
        }

        #endregion

        public override string ToString()
        {
            // ★ EndTurn은 별도 처리
            if (Type == ActionType.EndTurn)
            {
                return $"[EndTurn] ({Reason})";
            }

            // Move도 별도 처리
            if (Type == ActionType.Move)
            {
                return $"[Move] -> {MoveDestination?.ToString() ?? "unknown"} ({Reason})";
            }

            string targetName = Target?.Entity is BaseUnitEntity unit ? unit.CharacterName : "point";
            return $"[{Type}] {Ability?.Name ?? "?"} -> {targetName} ({Reason})";
        }
    }

    /// <summary>
    /// 행동 유형
    /// </summary>
    public enum ActionType
    {
        /// <summary>자기 버프</summary>
        Buff,

        /// <summary>이동</summary>
        Move,

        /// <summary>공격</summary>
        Attack,

        /// <summary>재장전</summary>
        Reload,

        /// <summary>힐링 (자신 또는 아군)</summary>
        Heal,

        /// <summary>아군 지원 버프</summary>
        Support,

        /// <summary>적 디버프</summary>
        Debuff,

        /// <summary>특수 능력 (DoT 콤보, 연쇄 등)</summary>
        Special,

        /// <summary>턴 종료</summary>
        EndTurn
    }
}
