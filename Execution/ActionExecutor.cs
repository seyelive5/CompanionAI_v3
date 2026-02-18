using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;

namespace CompanionAI_v3.Execution
{
    /// <summary>
    /// 행동 실행기 - 계획된 행동을 실행
    /// </summary>
    public class ActionExecutor
    {
        /// <summary>
        /// ★ v3.5.00: 킬 확인용 타겟 HP 캐시
        /// 공격 전 타겟 HP를 저장하여 공격 후 비교
        /// </summary>
        private readonly Dictionary<string, TargetSnapshot> _targetSnapshots = new Dictionary<string, TargetSnapshot>();

        private class TargetSnapshot
        {
            public BaseUnitEntity Target { get; set; }
            public float HPBefore { get; set; }
            public bool WasAlive { get; set; }
        }

        /// <summary>
        /// 계획된 행동 실행
        /// </summary>
        public ExecutionResult Execute(PlannedAction action, Situation situation)
        {
            if (action == null)
            {
                return ExecutionResult.EndTurn("No action");
            }

            Main.LogDebug($"[Executor] Executing: {action}");

            try
            {
                switch (action.Type)
                {
                    case ActionType.Buff:
                    case ActionType.Attack:
                    case ActionType.Heal:
                    case ActionType.Debuff:
                    case ActionType.Support:
                    case ActionType.Special:
                    case ActionType.Reload:
                        return ExecuteAbility(action, situation);

                    case ActionType.Move:
                        var moveResult = ExecuteMove(action, situation);
                        // ★ v3.8.98: Move 후 CombatCache 무효화
                        // 유닛이 이동하면 모든 거리/LOS/타겟팅 결과가 변함
                        // InvalidateCaster()가 존재하지만 호출되지 않던 버그 수정
                        // 이 수정 없이는 이동 후에도 구 위치 기준 거리가 캐시에 남아
                        // Hittable=0으로 판정 → 공격 불가 → EndTurn 발생
                        if (situation?.Unit != null)
                        {
                            CombatCache.InvalidateCaster(situation.Unit);
                        }
                        return moveResult;

                    case ActionType.WeaponSwitch:  // ★ v3.9.72
                        return ExecuteWeaponSwitch(action, situation);

                    case ActionType.EndTurn:
                        return ExecutionResult.EndTurn(action.Reason);

                    default:
                        return ExecutionResult.Failure($"Unknown action type: {action.Type}");
                }
            }
            catch (Exception ex)
            {
                Main.LogError($"[Executor] Error executing {action.Type}: {ex.Message}");
                return ExecutionResult.Failure($"Execution error: {ex.Message}");
            }
        }

        /// <summary>
        /// ★ v3.9.72: 무기 세트 전환 실행
        /// </summary>
        private ExecutionResult ExecuteWeaponSwitch(PlannedAction action, Situation situation)
        {
            var unit = situation?.Unit;
            int targetSet = action.WeaponSetIndex;

            if (unit == null || targetSet < 0 || targetSet > 1)
                return ExecutionResult.Failure("Invalid weapon switch parameters");

            if (unit.Body.CurrentHandEquipmentSetIndex == targetSet)
            {
                Main.Log($"[Executor] Weapon switch skipped — already on Set {targetSet}");
                return ExecutionResult.Continue();
            }

            CombatAPI.SwitchWeaponSet(unit, targetSet);

            // 캐시 전체 무효화 — 무기 변경 시 사거리/능력/타겟팅 모두 변함
            CombatCache.ClearAll();

            Main.Log($"[Executor] ★ Weapon switch executed: {unit.CharacterName} -> Set {targetSet}");
            // ★ v3.9.78: Waiting 반환 — GameCommand 비동기 처리 대기
            // Continue → goto executeNextAction → 같은 프레임에서 stale 데이터로 재분석 (버그)
            // Waiting → 게임에 제어 반환 → 다음 프레임 AnalyzePhase에서 fresh 분석
            return ExecutionResult.Waiting("Weapon switch queued");
        }

        /// <summary>
        /// 능력 실행
        /// ★ v3.8.55: situation 파라미터 추가 (Warp Relay 적 수 재확인용)
        /// </summary>
        private ExecutionResult ExecuteAbility(PlannedAction action, Situation situation)
        {
            var ability = action.Ability;
            var target = action.Target;

            if (ability == null)
            {
                return ExecutionResult.Failure("Ability is null");
            }

            // ★ v3.7.20: 사역마 타겟 재해석 - stale 참조 방지
            // 계획 시점에 저장된 사역마 엔티티 참조가 실행 시점에 유효하지 않을 수 있음
            // ★ v3.7.78: Point 타겟 능력은 사역마 위치로, Unit 타겟 능력은 사역마 유닛으로
            // - Point 타겟 (Psychic Scream 등 AOE): 사역마 위치에 캐스트 (Warp Relay)
            // - Unit 타겟: 사역마 유닛에 캐스트 (과충전/Momentum 이후에만 허용)
            if (action.IsFamiliarTarget)
            {
                var caster = ability.Caster as BaseUnitEntity;
                var freshFamiliar = FamiliarAPI.GetFamiliar(caster);
                if (freshFamiliar != null)
                {
                    // ★ v3.8.55: Warp Relay 디버프/공격 실행 전 Raven 주변 적 수 재확인
                    // 재배치 실패 시 Raven이 아군 근처에 머물 → 디버프 낭비 방지
                    // ★ v3.8.56: 적 1명이라도 있으면 실행 허용 (사람처럼 일단 뭐라도 하기)
                    if (action.Type == ActionType.Attack || action.Type == ActionType.Debuff)
                    {
                        var validEnemies = situation?.Enemies?.FindAll(e => e != null && e.IsConscious);
                        int enemiesNearRaven = validEnemies != null
                            ? FamiliarAPI.CountEnemiesInRadius(freshFamiliar.Position, FamiliarPositioner.EFFECT_RADIUS_TILES, validEnemies)
                            : 0;

                        if (enemiesNearRaven < 1)
                        {
                            Main.Log($"[Executor] ★ v3.8.56: Warp Relay debuff SKIPPED - Raven at ({freshFamiliar.Position.x:F1}, {freshFamiliar.Position.z:F1}) has 0 enemies nearby");
                            return ExecutionResult.Failure("No enemies near familiar for Warp Relay");
                        }
                        Main.LogDebug($"[Executor] Warp Relay check OK: {enemiesNearRaven} enemies near Raven");
                    }

                    // ★ v3.7.78: Point 타겟 능력 여부 체크
                    if (CombatAPI.IsPointTargetAbility(ability))
                    {
                        // AOE 능력은 사역마 위치에 캐스트
                        target = new TargetWrapper(freshFamiliar.Position);
                        Main.LogDebug($"[Executor] Familiar target re-resolved to POSITION: ({freshFamiliar.Position.x:F1}, {freshFamiliar.Position.y:F1}, {freshFamiliar.Position.z:F1})");
                    }
                    else
                    {
                        // Unit 타겟 능력은 사역마 유닛에 캐스트
                        target = new TargetWrapper(freshFamiliar);
                        Main.LogDebug($"[Executor] Familiar target re-resolved to UNIT: {freshFamiliar.CharacterName}");
                    }
                }
                else
                {
                    Main.LogWarning($"[Executor] Familiar target stale and cannot be re-resolved");
                    return ExecutionResult.Failure("Familiar not available");
                }
            }

            if (target == null)
            {
                return ExecutionResult.Failure("Target is null");
            }

            // ★ v3.5.15: 그룹 쿨다운 체크 (계획과 실행 사이에 쿨다운될 수 있음)
            // 계획 시점에서는 사용 가능했지만, 이전 액션 실행으로 그룹이 쿨다운될 수 있음
            if (CombatAPI.IsAbilityOnCooldownWithGroups(ability))
            {
                Main.LogWarning($"[Executor] Ability skipped (group cooldown): {ability.Name}");
                return ExecutionResult.Failure($"Group cooldown: {ability.Name}");
            }

            // ★ v3.0.93: 능력 자체가 사용 가능한지 먼저 체크 (쿨다운, 탄약, 충전 등)
            List<string> unavailableReasons;
            if (!CombatAPI.IsAbilityAvailable(ability, out unavailableReasons))
            {
                string reasons = string.Join(", ", unavailableReasons);
                Main.LogWarning($"[Executor] Ability unavailable: {ability.Name} - {reasons}");
                return ExecutionResult.Failure($"Ability unavailable: {reasons}");
            }

            // ★ v3.7.82: 실행 시점 도달 가능 검증 (플랜 연속 실행 대응)
            // 계획 생성 후 이동이 실행되면 캐스터 위치가 변함
            // 현재 위치에서 타겟에게 사거리+LOS 재검증 필수
            var casterUnit = ability.Caster as BaseUnitEntity;
            var targetUnit = target.Entity as BaseUnitEntity;

            // ★ v3.7.84: 타겟 생존 여부 검증
            // 이전 AOE 공격으로 타겟이 죽었을 수 있음
            if ((action.Type == ActionType.Attack || action.Type == ActionType.Debuff) && targetUnit != null)
            {
                if (targetUnit.LifeState.IsDead)
                {
                    Main.LogWarning($"[Executor] ★ Target already dead: {ability.Name} -> {targetUnit.CharacterName}");
                    return ExecutionResult.Failure($"Target is dead");
                }
                if (!targetUnit.IsConscious)
                {
                    Main.LogWarning($"[Executor] ★ Target unconscious: {ability.Name} -> {targetUnit.CharacterName}");
                    return ExecutionResult.Failure($"Target is unconscious");
                }
            }

            if ((action.Type == ActionType.Attack || action.Type == ActionType.Debuff)
                && casterUnit != null && targetUnit != null
                && (action.AllTargets == null || action.AllTargets.Count == 0))
            {
                if (!CombatAPI.CanReachTargetFromPosition(ability, casterUnit.Position, targetUnit))
                {
                    Main.LogWarning($"[Executor] ★ Execution-time reachability FAILED: {ability.Name} -> {targetUnit.CharacterName} from ({casterUnit.Position.x:F1}, {casterUnit.Position.z:F1})");
                    return ExecutionResult.Failure($"Target unreachable from current position");
                }
                Main.LogDebug($"[Executor] Execution-time reachability OK: {ability.Name} -> {targetUnit.CharacterName}");
            }

            // 최종 검증 - 타겟에게 사용 가능한지
            // ★ v3.7.25: MultiTarget 능력은 LOS 체크 스킵 (Point 타겟이므로)
            if (action.AllTargets == null || action.AllTargets.Count == 0)
            {
                string reason;
                if (!CombatAPI.CanUseAbilityOn(ability, target, out reason))
                {
                    Main.LogWarning($"[Executor] Ability blocked: {ability.Name} - {reason}");
                    return ExecutionResult.Failure(reason);
                }
            }

            // ★ v3.5.00: 공격 시 타겟 스냅샷 저장 + 예상 피해량 기록
            var targetEntity = target.Entity as BaseUnitEntity;
            if (action.Type == ActionType.Attack && targetEntity != null)
            {
                // 공격 전 타겟 상태 저장
                string targetId = targetEntity.UniqueId;
                _targetSnapshots[targetId] = new TargetSnapshot
                {
                    Target = targetEntity,
                    HPBefore = CombatCache.GetHPPercent(targetEntity),
                    WasAlive = !targetEntity.LifeState.IsDead
                };

                // 예상 피해량 기록 (Response Curve 기반 추정치)
                float estimatedDamage = CombatAPI.EstimateDamage(ability, targetEntity);
                if (estimatedDamage > 0)
                {
                    TeamBlackboard.Instance.RecordDamageDealt(estimatedDamage);
                    Main.LogDebug($"[Executor] Attack: {ability.Name} -> {targetEntity.CharacterName}, EstDmg={estimatedDamage:F0}");
                }

                // ★ v3.8.46: Target Inertia - 공격 타겟 기록 (다음 턴 관성 보너스용)
                if (casterUnit != null)
                {
                    TeamBlackboard.Instance.SetPreviousTarget(casterUnit.UniqueId, targetEntity);
                }
            }

            // ★ v3.5.29: 캐시 무효화 - 타겟 위치가 변할 수 있는 능력
            // Attack, Debuff 등 적에게 사용하는 능력은 밀치기/이동 효과가 있을 수 있음
            if (action.Type == ActionType.Attack || action.Type == ActionType.Debuff)
            {
                var cacheTarget = target.Entity as BaseUnitEntity;
                if (cacheTarget != null)
                {
                    CombatCache.InvalidateTarget(cacheTarget);
                }
            }

            // ★ v3.8.98: GapCloser 캐시 무효화 - 시전자가 이동하므로
            // GapCloser(돌진/텔레포트)는 시전자 위치가 변함 → 모든 거리/타겟팅 재계산 필요
            if (action.Ability != null && AbilityDatabase.IsGapCloser(action.Ability))
            {
                var caster = action.Ability.Caster as BaseUnitEntity;
                if (caster != null)
                {
                    CombatCache.InvalidateCaster(caster);
                }
            }

            // ★ v3.7.25: MultiTarget 능력 처리
            if (action.AllTargets != null && action.AllTargets.Count > 0)
            {
                Main.Log($"[Executor] Cast MultiTarget: {ability.Name} ({action.AllTargets.Count} targets)");
                return ExecutionResult.CastAbilityMultiTarget(ability, action.AllTargets);
            }

            // ★ v3.7.83: Point-target 능력 추가 로깅 (Relocate 등 실패 원인 파악)
            if (target.Point.sqrMagnitude > 0.001f && target.Entity == null)
            {
                var caster = ability.Caster as BaseUnitEntity;
                var targetNode = target.Point.GetNearestNodeXZ() as CustomGridNodeBase;
                Main.LogDebug($"[Executor] Point-target ability: {ability.Name}");
                Main.LogDebug($"[Executor]   Caster: {caster?.CharacterName} at ({caster?.Position.x:F1}, {caster?.Position.y:F1}, {caster?.Position.z:F1})");
                Main.LogDebug($"[Executor]   Target point: ({target.Point.x:F2}, {target.Point.y:F2}, {target.Point.z:F2})");
                if (targetNode != null)
                {
                    Main.LogDebug($"[Executor]   Target node: ({targetNode.XCoordinateInGrid}, {targetNode.ZCoordinateInGrid}), Walkable={targetNode.Walkable}");
                    Main.LogDebug($"[Executor]   Node position: ({targetNode.Vector3Position.x:F2}, {targetNode.Vector3Position.y:F2}, {targetNode.Vector3Position.z:F2})");

                    // 노드 점유 상태 확인
                    if (targetNode.TryGetUnit(out var occupant))
                    {
                        Main.LogDebug($"[Executor]   Node occupied by: {occupant?.CharacterName}");
                    }
                }
                else
                {
                    Main.LogDebug($"[Executor]   Target node: NULL (invalid position?)");
                }

                // Familiar 관련 추가 진단
                if (FamiliarAPI.HasFamiliar(caster))
                {
                    var familiar = FamiliarAPI.GetFamiliar(caster);
                    Main.LogDebug($"[Executor]   Familiar: {familiar?.CharacterName}, Conscious={familiar?.IsConscious}, Pos=({familiar?.Position.x:F1}, {familiar?.Position.y:F1}, {familiar?.Position.z:F1})");

                    // 현재 위치에서 타겟까지 거리
                    if (familiar != null)
                    {
                        float distToTarget = UnityEngine.Vector3.Distance(familiar.Position, target.Point);
                        // ★ v3.8.55: support range 대비 거리 표시
                        float supportRange = FamiliarAPI.GetRavenSupportRangeMeters(familiar);
                        bool withinRange = distToTarget <= supportRange;
                        Main.LogDebug($"[Executor]   Familiar distance to target: {distToTarget:F1}m ({CombatAPI.MetersToTiles(distToTarget):F1} tiles) " +
                            $"[support range={supportRange:F1}m, {(withinRange ? "OK" : "★ OUT OF RANGE")}]");
                    }
                }
            }

            // 일반 능력 실행 명령 반환
            Main.Log($"[Executor] Cast: {ability.Name} -> {GetTargetName(target)}");
            return ExecutionResult.CastAbility(ability, target);
        }

        /// <summary>
        /// ★ v3.5.00: 이전 공격 타겟의 킬 여부 확인
        /// TurnOrchestrator에서 명령 완료 후 호출
        /// </summary>
        public void CheckForKills()
        {
            foreach (var kvp in _targetSnapshots)
            {
                var snapshot = kvp.Value;
                if (snapshot.Target == null) continue;

                // 공격 전에 살아있었는데 지금 죽었으면 킬
                if (snapshot.WasAlive && snapshot.Target.LifeState.IsDead)
                {
                    TeamBlackboard.Instance.RecordKill(snapshot.Target);
                    Main.Log($"[Executor] ★ Kill confirmed: {snapshot.Target.CharacterName}");
                }
            }

            _targetSnapshots.Clear();
        }

        /// <summary>
        /// ★ v3.5.00: 스냅샷 초기화 (턴 시작 시)
        /// </summary>
        public void ClearSnapshots()
        {
            _targetSnapshots.Clear();
        }

        /// <summary>
        /// 이동 실행
        /// </summary>
        private ExecutionResult ExecuteMove(PlannedAction action, Situation situation)
        {
            if (!action.MoveDestination.HasValue)
            {
                return ExecutionResult.Failure("Move destination is null");
            }

            var destination = action.MoveDestination.Value;

            // ★ v3.9.28: 이미 목적지에 있으면 이동 스킵 → 다음 액션 즉시 실행
            // 이동 후 replan 시 동일 위치로 Move 계획 → "Already at destination" → 턴 강제 종료 방지
            if (situation?.Unit != null)
            {
                try
                {
                    var currentNode = situation.Unit.Position.GetNearestNodeXZ();
                    var destNode = destination.GetNearestNodeXZ();
                    if (currentNode != null && destNode != null && currentNode == destNode)
                    {
                        Main.Log($"[Executor] Already at destination — skipping move");
                        return ExecutionResult.Continue();
                    }
                }
                catch { }
            }

            Main.Log($"[Executor] Move to: {destination}");
            return ExecutionResult.MoveTo(destination);
        }

        /// <summary>
        /// 타겟 이름 추출 (로깅용)
        /// </summary>
        private string GetTargetName(TargetWrapper target)
        {
            if (target == null) return "null";

            if (target.Entity is Kingmaker.EntitySystem.Entities.BaseUnitEntity unit)
            {
                return unit.CharacterName;
            }

            // ★ v3.0.46: 부동소수점 비교 개선
            if (target.Point.sqrMagnitude > 0.001f)
            {
                return $"Point({target.Point})";
            }

            return "unknown";
        }
    }
}
