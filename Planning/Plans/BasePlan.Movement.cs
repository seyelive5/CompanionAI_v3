using System;
using System.Collections.Generic;
using System.Linq;  // v3.117.63: GetUnavailabilityReasons() 반환 IEnumerable.Any() 사용
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Enums;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using Pathfinding;
using UnityEngine;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;
using CompanionAI_v3.Planning.Planners;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning.Plans
{
    public abstract partial class BasePlan
    {
        #region Movement - Delegates to MovementPlanner

        // ★ v3.8.44: AttackPhaseContext 전달 - 능력 사거리 기반 이동 위치 계산
        // ★ v3.11.2: 미사용 오버로드 4개 제거 (0~4-param) — 6-param 버전만 유지
        protected PlannedAction PlanMoveOrGapCloser(Situation situation, ref float remainingAP, bool forceMove, bool bypassCanMoveCheck, float predictedMP, AttackPhaseContext attackContext)
            => MovementPlanner.PlanMoveOrGapCloser(situation, ref remainingAP, RoleName, forceMove, bypassCanMoveCheck, predictedMP, attackContext);

        protected PlannedAction PlanGapCloser(Situation situation, BaseUnitEntity target, ref float remainingAP)
            => MovementPlanner.PlanGapCloser(situation, target, ref remainingAP, RoleName);

        // ★ v3.5.34: MP 비용 예측 버전 추가
        protected PlannedAction PlanGapCloser(Situation situation, BaseUnitEntity target, ref float remainingAP, ref float remainingMP)
            => MovementPlanner.PlanGapCloser(situation, target, ref remainingAP, ref remainingMP, RoleName);

        // ★ v3.16.6: Walk+Jump 콤보 지원 버전 (preMoveAction 출력)
        protected PlannedAction PlanGapCloser(Situation situation, BaseUnitEntity target, ref float remainingAP, ref float remainingMP, out PlannedAction preMoveAction)
            => MovementPlanner.PlanGapCloser(situation, target, ref remainingAP, ref remainingMP, RoleName, out preMoveAction);

        /// <summary>
        /// ★ v3.40.2: 밀어내기(Push) 공격 후 갭클로저 삽입 — 적 추격
        /// 근접 무기 공격이 적을 밀어내면, 후속 공격을 위해 갭클로저 계획
        /// AP 예산이 후속 공격까지 감당 가능할 때만 삽입 (낭비 방지)
        /// </summary>
        protected PlannedAction TryPlanPushRecoveryGapCloser(
            Situation situation, PlannedAction attackAction,
            ref float remainingAP, ref float remainingMP, APBudget budget)
        {
            // 무기 공격이 아니면 스킵
            if (attackAction?.Ability?.Weapon == null) return null;
            // 근접이 아니면 스킵
            if (!attackAction.Ability.Weapon.Blueprint.IsMelee) return null;
            // 밀어내기 가능한 유닛이 아니면 스킵
            if (!CombatAPI.CanMeleeAttackCausePush(situation.Unit)) return null;
            // 후속 공격할 AP가 없으면 갭클로저 불필요
            if (!budget.CanAfford(0, remainingAP)) return null;

            var pushTarget = attackAction.Target?.Entity as BaseUnitEntity;
            if (pushTarget == null) return null;

            var gapCloser = PlanGapCloser(situation, pushTarget, ref remainingAP, ref remainingMP);
            if (gapCloser != null)
            {
                Log.Planning.Info($"[{RoleName}] Push recovery: {gapCloser.Ability?.Name} → {pushTarget.CharacterName} (melee push detected)");
            }
            return gapCloser;
        }

        /// <summary>
        /// ★ v3.16.0: 갭클로저를 MoveToAttack 대안으로 평가
        /// ★ v3.16.6: Walk+Jump 콤보 지원 — preMoveAction 출력 추가
        /// TacticalEval이 MoveToAttack을 선택했을 때, 갭클로저가 더 효율적인지 비교
        /// </summary>
        protected PlannedAction EvaluateGapCloserAsAttack(
            Situation situation,
            ref float remainingAP,
            ref float remainingMP,
            out PlannedAction preMoveAction)
        {
            preMoveAction = null;

            // 원거리 선호 유닛은 갭클로저 불필요
            if (situation.PrefersRanged) return null;

            var attacks = situation.AvailableAttacks;
            if (attacks == null || attacks.Count == 0) return null;

            BaseUnitEntity bestTarget = situation.BestTarget ?? situation.NearestEnemy;
            if (bestTarget == null) return null;

            AbilityData bestGapCloser = null;
            float bestScore = 0f;  // 양수만 허용

            for (int i = 0; i < attacks.Count; i++)
            {
                var attack = attacks[i];
                if (!AbilityDatabase.IsGapCloser(attack)) continue;

                float apCost = CombatAPI.GetAbilityAPCost(attack);
                if (apCost > remainingAP) continue;

                // ScoreAttack으로 점수 산출 (10-A의 갭클로저 aware 스코어링 적용)
                float score = UtilityScorer.ScoreAttack(attack, bestTarget, situation);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestGapCloser = attack;
                }
            }

            if (bestGapCloser == null) return null;

            // MovementPlanner.PlanGapCloser()에 위임 — 경로 검증, CanUseAbilityOn 등 전부 처리
            // ★ v3.16.6: Walk+Jump 콤보 지원 (preMoveAction 출력)
            float tempAP = remainingAP;
            float tempMP = remainingMP;
            PlannedAction preMove;
            var action = MovementPlanner.PlanGapCloser(
                situation, bestTarget, ref tempAP, ref tempMP, RoleName, out preMove);

            if (action == null) return null;

            // AP/MP 차감
            remainingAP = tempAP;
            remainingMP = tempMP;
            preMoveAction = preMove;

            Log.Planning.Info($"[{RoleName}] ★ GapCloser as attack: {bestGapCloser.Name} -> " +
                $"{bestTarget.CharacterName} (score={bestScore:F0}, dist={CombatCache.GetDistanceInTiles(situation.Unit, bestTarget):F1}" +
                (preMove != null ? ", walk+jump combo" : "") + ")");

            return action;
        }

        protected PlannedAction PlanMoveToEnemy(Situation situation)
            => MovementPlanner.PlanMoveToEnemy(situation, RoleName);

        protected PlannedAction PlanRetreat(Situation situation)
            => MovementPlanner.PlanRetreat(situation);

        protected PlannedAction PlanPostActionSafeRetreat(Situation situation)
            => MovementPlanner.PlanPostActionSafeRetreat(situation);

        // ★ v3.8.74: Tactical Reposition delegate
        protected PlannedAction PlanTacticalReposition(Situation situation, float remainingMP)
            => MovementPlanner.PlanTacticalReposition(situation, remainingMP);

        protected bool ShouldRetreat(Situation situation)
            => MovementPlanner.ShouldRetreat(situation);

        /// <summary>
        /// ★ v3.34.0: 이동 전 MP 버프 사용 — 적이 사거리 밖이고 MP 부족 시 선제 MP 회복
        /// RecklessRush 등 PostFirstAction MP 회복 스킬을 이동 전에 사용
        /// </summary>
        protected PlannedAction PlanMPBuffBeforeMove(Situation situation, ref float remainingAP, ref float remainingMP)
        {
            if (situation.MPBuffAbility == null) return null;
            if (situation.HasHittableEnemies) return null;  // 이미 공격 가능하면 불필요
            if (!situation.HasLivingEnemies) return null;
            if (situation.CurrentMP > 3f) return null;  // MP 충분하면 불필요

            var ability = situation.MPBuffAbility;
            float cost = CombatAPI.GetAbilityAPCost(ability);
            if (remainingAP < cost + 1f) return null;  // AP가 버프+최소공격 비용 미만이면 스킵

            // 능력 사용 가능 여부 확인
            // v3.117.63: 게임 업데이트로 GetUnavailabilityReasons() 반환 List → IEnumerable. .Count → .Any()
            if (ability.GetUnavailabilityReasons().Any()) return null;
            if (ability.IsRestricted) return null;

            remainingAP -= cost;
            remainingMP += situation.MPBuffExpectedRecovery;

            Log.Planning.Info($"[{RoleName}] MPBuff before move: {ability.Name} (cost={cost:F1} AP, +{situation.MPBuffExpectedRecovery:F0} MP, predicted MP={remainingMP:F1})");

            return new PlannedAction
            {
                Type = ActionType.Buff,
                Ability = ability,
                Target = new TargetWrapper(situation.Unit),
                Priority = 18  // Move(20) 직전
            };
        }

        /// <summary>
        /// ★ v3.36.0: 모든 0 AP PreAttackBuff를 일괄 사용 (Phase 4.05)
        /// 0 AP 버프는 무조건 사용하는 게 이득 — 한 번에 하나씩이 아닌 모두 사용
        /// PlanAttackBuffWithReservation이 최고 점수 1개만 선택하므로, 나머지 무료 버프를 여기서 소진
        /// </summary>
        protected void PlanFreeAttackBuffs(List<PlannedAction> actions, Situation situation)
        {
            if (!situation.HasHittableEnemies) return;
            if (situation.AvailableBuffs == null || situation.AvailableBuffs.Count == 0) return;

            var selfTarget = new TargetWrapper(situation.Unit);

            foreach (var buff in situation.AvailableBuffs)
            {
                var timing = AbilityDatabase.GetTiming(buff);
                if (timing != AbilityTiming.PreAttackBuff && timing != AbilityTiming.RighteousFury
                    && timing != AbilityTiming.SelfDamage)  // ★ v3.40.2: 0 AP 자해 버프도 포함
                    continue;

                float cost = CombatAPI.GetAbilityAPCost(buff);
                if (cost > 0f) continue; // 0 AP만

                if (AbilityDatabase.IsRunAndGun(buff)) continue;
                if (AbilityDatabase.IsPostFirstAction(buff)) continue;
                if (AllyStateCache.HasBuff(situation.Unit, buff)) continue;

                // ★ v3.104.0: 통합 dedup (Phase 2/4/4.05/4.7 모든 버프 경로 공유)
                string buffGuid = buff.Blueprint?.AssetGuid?.ToString() ?? buff.Name ?? "";
                if (_plannedBuffGuids.Contains(buffGuid)) continue;

                string reason;
                if (!CombatAPI.CanUseAbilityOn(buff, selfTarget, out reason)) continue;

                actions.Add(PlannedAction.Buff(buff, situation.Unit, $"Free attack buff: {buff.Name}", 0f));
                _plannedBuffGuids.Add(buffGuid);  // ★ v3.104.0: dedup 등록
                Log.Planning.Info($"[{RoleName}] Phase 4.05: Free buff {buff.Name}");
            }
        }

        /// <summary>
        /// ★ v3.36.0: 0 AP 공격 일괄 계획 (Phase 5.8 / 6.5)
        /// AP 예산과 무관하게 사용 가능한 0 AP 공격을 모두 계획
        /// Kick, Death Whisper, Break Through 후속 Slash 등
        /// </summary>
        protected void PlanZeroAPAttacks(List<PlannedAction> actions, Situation situation,
            HashSet<string> plannedAbilityGuids = null, int maxAttacks = 3)
        {
            if (!situation.HasHittableEnemies) return;

            // F14: planning 은 유닛 실제 preference 전달 → PreferRanged 유닛의 0-AP 근접 누수 차단.
            //   (스냅샷/replan count 호출자는 기본 Adaptive 유지 → 상호 일관성 = F2/F12 보존)
            var zeroAPAttacks = CombatAPI.GetZeroAPAttacks(situation.Unit, situation.RangePreference);
            if (zeroAPAttacks.Count == 0) return;

            int planned = 0;
            foreach (var attack in zeroAPAttacks)
            {
                if (planned >= maxAttacks) break;

                // 이미 메인 공격 루프에서 계획된 능력은 스킵
                var guid = attack.Blueprint?.AssetGuid?.ToString();
                if (plannedAbilityGuids != null && !string.IsNullOrEmpty(guid) && plannedAbilityGuids.Contains(guid))
                    continue;

                // 이미 actions에 동일 능력이 있으면 스킵
                bool alreadyPlanned = false;
                for (int i = 0; i < actions.Count; i++)
                {
                    if (actions[i].Ability == attack) { alreadyPlanned = true; break; }
                }
                if (alreadyPlanned) continue;

                // 사용 가능 여부 검증
                // v3.117.63: GetUnavailabilityReasons() 반환 IEnumerable → .Any() 사용
                if (attack.GetUnavailabilityReasons().Any()) continue;
                if (attack.IsRestricted) continue;

                // Hittable 적 중 사용 가능한 타겟 찾기
                foreach (var enemy in situation.HittableEnemies)
                {
                    if (enemy == null || enemy.LifeState.IsDead) continue;

                    var targetWrapper = new TargetWrapper(enemy);
                    string reason;
                    if (!CombatAPI.CanUseAbilityOn(attack, targetWrapper, out reason)) continue;

                    // ★ v3.117.8 (옵션 B): caller guard 제거 — AoESafetyChecker 가 단일 진실 source.
                    //   비-AoE 단발은 IsChainAbilitySafeForTarget 로 즉시 true 반환 (거의 0 비용).
                    //   AoE/burst/scatter/chain 모두 IsAoESafeForUnitTarget 내부에서 분류 + 검사.
                    //   장점: 우리 caller guard 가 새 능력 분류 누락하는 위험 0.
                    if (situation.Allies != null
                        && !AoESafetyChecker.IsAoESafeForUnitTarget(attack, situation.Unit, enemy, situation.Allies))
                    {
                        if (Main.IsDebugEnabled)
                            Log.Planning.Debug($"[{RoleName}] 0-AP attack BLOCKED by ally safety: {attack.Name} -> {enemy.CharacterName}");
                        continue;  // 이 적 스킵, 다른 적 시도
                    }

                    actions.Add(PlannedAction.Attack(attack, enemy,
                        $"0-AP attack: {attack.Name}", 0f));
                    Log.Planning.Info($"[{RoleName}] 0-AP attack: {attack.Name} -> {enemy.CharacterName}");
                    planned++;
                    break;
                }
            }
        }

        /// <summary>
        /// ★ v3.9.70: 긴급 AoE 대피 — 현재 위치가 피해 AoE 안이면 가장 가까운 안전 타일로 이동
        /// Phase 0.5 (Emergency Heal 전)에서 호출
        /// </summary>
        protected PlannedAction PlanAoEEvacuation(Situation situation)
        {
            if (!situation.NeedsAoEEvacuation) return null;
            if (!situation.CanMove || situation.CurrentMP <= 0) return null;

            var unit = situation.Unit;
            bool inDamage = situation.IsInDamagingAoE;
            bool inPsychicNull = situation.IsInPsychicNullZone;
            string reason = inDamage && inPsychicNull ? "damaging AoE + psychic null zone"
                          : inDamage ? "damaging AoE"
                          : "psychic null zone";
            Log.Planning.Info($"[{RoleName}] ★ AoE Evacuation: {unit.CharacterName} is in {reason}, searching for safe tile");

            try
            {
                // 도달 가능 타일 조회 (캐시 활용)
                var reachableTiles = MovementAPI.FindAllReachableTilesWithThreatsSync(unit);
                if (reachableTiles == null || reachableTiles.Count == 0)
                {
                    Log.Planning.Info($"[{RoleName}] AoE Evacuation: No reachable tiles");
                    return null;
                }

                // 가장 가까운 안전 타일 탐색 (위험 구역 밖 + 정지 가능 + 최단 경로비용)
                GraphNode bestNode = null;
                float bestCost = float.MaxValue;

                // 그리드 노드 비교로 "Already at destination" 루프 방지
                var currentNode = unit.Position.GetNearestNodeXZ();

                foreach (var kvp in reachableTiles)
                {
                    var node = kvp.Key as CustomGridNodeBase;
                    if (node == null) continue;

                    // 같은 그리드 노드면 스킵
                    if (node == currentNode) continue;

                    // 점유/정지 불가 셀 제외 — 게임 실행기 CanStopAtNode(=cells[node].IsCanStand)와 동일 기준.
                    // 미검사 시 게임이 경로를 트림해 AoE 안에 착지하거나 이동 자체가 실패함 (턴 사멸).
                    if (!kvp.Value.IsCanStand) continue;

                    var pos = node.Vector3Position;

                    // 이 타일이 모든 위험 구역 밖인지 확인
                    if (inDamage && CombatAPI.IsPositionInDamagingAoE(pos, unit)) continue;
                    if (inPsychicNull && CombatAPI.IsPositionInPsychicNullZone(pos)) continue;

                    // 실경로 비용(MP) 기준 최단 — 직선거리는 벽/우회 경로를 왜곡
                    float pathCost = kvp.Value.Length;
                    if (pathCost < bestCost)
                    {
                        bestCost = pathCost;
                        bestNode = kvp.Key;
                    }
                }

                if (bestNode == null)
                {
                    Log.Planning.Info($"[{RoleName}] AoE Evacuation: No safe tile found within movement range!");
                    return null;
                }

                var safePos = ((CustomGridNodeBase)bestNode).Vector3Position;
                Log.Planning.Info($"[{RoleName}] ★ AoE Evacuation: Moving to ({safePos.x:F1},{safePos.z:F1}), pathCost={bestCost:F1}MP");

                var evacMove = PlannedAction.Move(safePos, $"Emergency evacuation ({reason})");
                evacMove.IsEvacuationMove = true;  // F3: TurnState.HasEvacuatedThisTurn 표식 (재대피 게이트 분리)
                return evacMove;
            }
            catch (Exception ex)
            {
                Log.Planning.Error($"[{RoleName}] AoE Evacuation error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Situation 스냅샷을 채운 TurnPlan 생성 — situation 을 가진 모든 플랜 생성의 단일 진입점.
        /// 짧은 생성자(스냅샷 인자 생략)를 쓰면 InitialAP/MP/zeroAP 가 0 으로 남아 NeedsReplan Section 3
        /// 이 "AP 증가(0→N)" / "새 0-AP 공격(0→N)"으로 오판, 같은 틱 replan 루프를 만든다(F2/F12).
        /// zeroAP 카운트까지 풀플랜 생성부(예: DPSPlan)와 동일 소스로 채워 이 오판을 원천 차단.
        /// </summary>
        protected TurnPlan CreatePlanWithSnapshot(List<PlannedAction> actions, TurnPriority priority, string reasoning, Situation situation)
        {
            return new TurnPlan(actions, priority, reasoning, situation.HPPercent, situation.NearestEnemyDistance,
                situation.NormalHittableCount, situation.CurrentAP, situation.CurrentMP,
                CombatAPI.GetZeroAPAttacks(situation.Unit).Count);
        }

        /// <summary>
        /// ★ v3.12.0: 공통 초기 Phase 실행 (Phase 0 ~ 1.5)
        /// Phase 0: Ultimate (잠재력 초월) — early return 가능
        /// Phase 0.5: AoE 대피 — early return 가능
        /// Phase 1: 긴급 자기 힐 — early return 가능
        /// Phase 1.5: 재장전 — actions에 추가만 (early return 없음)
        /// </summary>
        /// <returns>early return이 필요하면 TurnPlan, 아니면 null (계속 진행)</returns>
        protected TurnPlan ExecuteCommonEarlyPhases(
            List<PlannedAction> actions,
            Situation situation,
            ref float remainingAP)
        {
            // Phase 0: Ultimate (잠재력 초월)
            if (CombatAPI.HasFreeUltimateBuff(situation.Unit))
            {
                var ultimateAction = PlanUltimate(situation, ref remainingAP);
                if (ultimateAction != null)
                {
                    actions.Add(ultimateAction);
                    return CreatePlanWithSnapshot(actions, TurnPriority.Critical,
                        $"{RoleName} ultimate (Transcend Potential)", situation);
                }
                Log.Planning.Info($"[{RoleName}] Ultimate failed during Transcend Potential - ending turn");
                actions.Add(PlannedAction.EndTurn($"{RoleName} no ultimate available"));
                return CreatePlanWithSnapshot(actions, TurnPriority.EndTurn,
                    $"{RoleName} ultimate failed (Transcend Potential)", situation);
            }

            // Phase 0.5: AoE Evacuation
            // F3: HasEvacuatedThisTurn 가드 (HasMovedThisTurn 아님).
            //   막으려는 것은 "대피→여전히 AoE→재대피" 루프뿐. 그런데 HasMovedThisTurn 은 공격 이동에도
            //   set 되므로, 공격 위치로 이동한 뒤 그 자리가 위험지대(또는 이동 후 새 마커)면 대피가
            //   영구 차단됐다(Run and Gun 으로 MP 회복 후 재대피도 차단). 대피 시도 자체를 추적하는
            //   HasEvacuatedThisTurn 으로 게이트해 "이미 이번 턴 대피 시도함"일 때만 스킵.
            if (situation.NeedsAoEEvacuation && situation.CanMove && !situation.HasEvacuatedThisTurn)
            {
                var evacAction = PlanAoEEvacuation(situation);
                if (evacAction != null)
                {
                    actions.Add(evacAction);
                    return CreatePlanWithSnapshot(actions, TurnPriority.Emergency,
                        $"{RoleName} AoE evacuation", situation);
                }
            }

            // Phase 1: Emergency Heal
            var healAction = PlanEmergencyHeal(situation, ref remainingAP);
            if (healAction != null)
            {
                actions.Add(healAction);
                return CreatePlanWithSnapshot(actions, TurnPriority.Emergency,
                    $"{RoleName} emergency heal", situation);
            }

            // Phase 1.5: Reload (early return 없음 — 이후 Phase 계속 진행)
            var reloadAction = PlanReload(situation, ref remainingAP);
            if (reloadAction != null)
                actions.Add(reloadAction);

            return null; // 계속 진행
        }

        #region ★ Phase 0.2: Common Early Phase Methods (PlanContext 기반, Template Method 준비)

        /// <summary>
        /// ★ Phase 0.2: PlanContext 기반 공통 초기 Phase 실행 (Phase 0 ~ 1.8).
        /// DPS/Tank/Support/Overseer 4개 Plan에서 동일한 초기 Phase들을 통합.
        ///
        /// Phase 0:    Ultimate (잠재력 초월) — early return 가능
        /// Phase 0.5:  AoE 대피 — early return 가능
        /// Phase 1:    긴급 자기 힐 — early return 가능
        /// Phase 1.5:  재장전
        /// Phase 1.55: 무기 전환 (virtual — DPS는 override로 bonusWeaponSwitch 처리)
        /// Phase 1.8:  Approach Stance
        /// </summary>
        /// <returns>early return이 필요하면 TurnPlan, 아니면 null (계속 진행)</returns>
        protected TurnPlan ExecuteCommonEarlyPhases(PlanContext ctx)
        {
            // Phase 0 ~ 1.5: 기존 메서드에 위임
            var earlyReturn = ExecuteCommonEarlyPhases(ctx.Actions, ctx.Situation, ref ctx.RemainingAP);
            if (earlyReturn != null) return earlyReturn;

            // Phase 1.55: 무기 전환 (virtual — DPS는 override)
            var switchReturn = ExecuteWeaponSwitchPhase(ctx);
            if (switchReturn != null) return switchReturn;

            // Phase 1.8: Approach Stance
            ExecuteApproachStancePhase(ctx);

            return null; // 계속 진행
        }

        /// <summary>
        /// ★ Phase 0.2: Phase 1.55 — 무기 전환.
        /// Tank/Support/Overseer 공통: 현재 무기 무용/비효율 시 즉시 전환.
        /// DPS는 이 메서드를 override하여 bonusWeaponSwitch 억제 + Phase 1.56 처리.
        /// </summary>
        /// <returns>무기 전환으로 early return 필요 시 TurnPlan, 아니면 null</returns>
        protected virtual TurnPlan ExecuteWeaponSwitchPhase(PlanContext ctx)
        {
            var situation = ctx.Situation;
            if (situation.WeaponRotationAvailable
                && (!situation.HasHittableEnemies || ShouldSwitchForEffectiveness(situation))
                && ShouldSwitchFirst(situation))
            {
                var switchActions = PlanWeaponSetRotationAttack(situation, ref ctx.RemainingAP);
                if (switchActions.Count > 0)
                {
                    ctx.Actions.AddRange(switchActions);
                    Log.Planning.Info($"[{ctx.RoleName}] Phase 1.55: Switch-First — switching weapon for better effectiveness");
                    return CreatePlanWithSnapshot(ctx.Actions, TurnPriority.DirectAttack,
                        $"{ctx.RoleName} weapon switch-first", ctx.Situation);
                }
            }

            return null;
        }

        /// <summary>
        /// ★ Phase 0.2: Phase 1.8 — Cautious/Confident Approach 스탠스 선택.
        /// DPS/Overseer는 preferOffensive=true, Tank/Support는 false.
        /// </summary>
        protected void ExecuteApproachStancePhase(PlanContext ctx)
        {
            bool preferOffensive = ctx.Role == AIRole.DPS || ctx.Role == AIRole.Overseer;
            var approachStance = PlanApproachStance(ctx.Situation, preferOffensive: preferOffensive);
            if (approachStance != null)
                ctx.Actions.Add(approachStance);
        }

        #endregion

        /// <summary>
        /// ★ v3.12.0: 공통 Familiar 능력 Phase (Phase 1.75)
        /// DPS/Tank/Support에서 동일한 사역마 능력 시퀀스 실행
        /// supportMode=true: 추가 보호 능력(Protect, Screen) + 키스톤 GUID 추적
        /// </summary>
        /// <param name="supportMode">true면 Support 전용 능력 + GUID 추적 활성화</param>
        /// <param name="usedKeystoneGuids">supportMode=true일 때 사용된 키스톤 GUID 반환</param>
        /// <returns>usedWarpRelay 여부</returns>
        protected bool ExecuteFamiliarSupportPhase(
            List<PlannedAction> actions,
            Situation situation,
            TurnState turnState,
            ref float remainingAP,
            bool supportMode,
            out HashSet<string> usedKeystoneGuids)
        {
            usedKeystoneGuids = supportMode ? new HashSet<string>() : null;
            // F7: relay 사용 여부는 replan 을 넘겨 살아남아야 함 — 플랜-로컬 false 가 아니라
            //   이번 턴 실행 기록(TurnState)에서 relay 대상 능력 사용을 조회해 초기화.
            //   (relay 시전 → replan → 새 플랜의 keystone 루프 공백 → Cycle 유실 방지)
            bool usedWarpRelay = situation.FamiliarType == PetType.Raven
                && (turnState?.HasExecutedAbilityMatching(FamiliarAbilities.IsWarpRelayTarget) ?? false);

            if (!situation.HasFamiliar) return false;

            // 1. Servo-Skull Priority Signal (선제 버프)
            var prioritySignal = PlanFamiliarPrioritySignal(situation, ref remainingAP);
            if (prioritySignal != null)
                actions.Add(prioritySignal);

            // ★ v3.22.6: 마스티프 Apprehend 상태 확인 — 활성 대상 생존 시 마스티프 명령 전부 스킵
            bool mastiffApprehendActive = false;
            if (situation.FamiliarType == PetType.Mastiff)
            {
                string apprehendTargetId = TeamBlackboard.Instance.GetMastiffApprehendTarget(situation.Unit.UniqueId);
                if (apprehendTargetId != null)
                {
                    var apprehendTarget = CollectionHelper.FirstOrDefault(situation.Enemies,
                        e => e.IsConscious && e.UniqueId == apprehendTargetId);
                    mastiffApprehendActive = apprehendTarget != null;
                    if (mastiffApprehendActive && Main.IsDebugEnabled)
                        Log.Planning.Debug($"[{RoleName}] Phase 1.75: Mastiff Apprehend active — skipping mastiff commands (AP saved)");
                }
            }

            // 2. Mastiff Fast (Apprehend 전 이동 버프 — 새 Apprehend 발행 시에만)
            if (!mastiffApprehendActive)
            {
                var mastiffFast = PlanFamiliarFast(situation, ref remainingAP);
                if (mastiffFast != null)
                    actions.Add(mastiffFast);
            }

            // 3. Relocate: 사역마 최적 위치로 이동 (Mastiff 제외)
            var familiarRelocate = PlanFamiliarRelocate(situation, ref remainingAP);
            if (familiarRelocate != null)
                actions.Add(familiarRelocate);

            // 4. 키스톤 버프/디버프 루프 (Servo-Skull/Raven)
            var keystoneActions = PlanAllFamiliarKeystoneBuffs(situation, ref remainingAP);
            if (keystoneActions.Count > 0)
            {
                actions.AddRange(keystoneActions);
                Log.Planning.Info($"[{RoleName}] Phase 1.75: {keystoneActions.Count} keystone abilities planned");
                usedWarpRelay = situation.FamiliarType == PetType.Raven;

                // Support: 사용된 GUID 추적 (Phase 4 중복 방지용)
                if (supportMode)
                {
                    foreach (var action in keystoneActions)
                    {
                        if (action.Ability?.Blueprint != null)
                        {
                            string guid = action.Ability.Blueprint.AssetGuid?.ToString();
                            if (!string.IsNullOrEmpty(guid))
                                usedKeystoneGuids.Add(guid);
                        }
                    }
                }
            }

            // 5. Raven Cycle (Warp Relay 후 재시전)
            if (usedWarpRelay)
            {
                var cycle = PlanFamiliarCycle(situation, ref remainingAP, usedWarpRelay);
                if (cycle != null)
                    actions.Add(cycle);
            }

            // 6. Raven Hex (적 디버프)
            var hex = PlanFamiliarHex(situation, ref remainingAP);
            if (hex != null)
                actions.Add(hex);

            // 7. Mastiff: Apprehend → JumpClaws → Claws → Roam (폴백 체인)
            // ★ v3.22.6: Apprehend 활성 시 전부 스킵 (AP 절약 → 마스터 공격 강화)
            if (!mastiffApprehendActive)
            {
                var familiarApprehend = PlanFamiliarApprehend(situation, ref remainingAP);
                if (familiarApprehend != null)
                    actions.Add(familiarApprehend);
                else
                {
                    var jumpClaws = PlanFamiliarJumpClaws(situation, ref remainingAP);
                    if (jumpClaws != null)
                        actions.Add(jumpClaws);
                    else
                    {
                        var mastiffClaws = PlanFamiliarClaws(situation, ref remainingAP);
                        if (mastiffClaws != null)
                            actions.Add(mastiffClaws);
                        else
                        {
                            var roam = PlanFamiliarRoam(situation, ref remainingAP);
                            if (roam != null)
                                actions.Add(roam);
                        }
                    }
                }
            }

            // ★ v3.22.6: Mastiff Protect — Apprehend 비활성 + 위협받는 아군 존재 시에만
            // (PlanFamiliarProtect 내부에서 Apprehend 배타 체크도 수행)
            if (supportMode)
            {
                var familiarProtect = PlanFamiliarProtect(situation, ref remainingAP);
                if (familiarProtect != null)
                    actions.Add(familiarProtect);
            }

            // 8. Eagle Obstruct (적 시야 방해)
            var familiarObstruct = PlanFamiliarObstruct(situation, ref remainingAP);
            if (familiarObstruct != null)
                actions.Add(familiarObstruct);

            // 9. Eagle Blinding Dive (이동+실명 공격)
            var blindingDive = PlanFamiliarBlindingDive(situation, ref remainingAP);
            if (blindingDive != null)
                actions.Add(blindingDive);

            // Support 전용: Eagle Screen (HP 낮은 아군 보호)
            if (supportMode)
            {
                var familiarScreen = PlanFamiliarScreen(situation, ref remainingAP);
                if (familiarScreen != null)
                    actions.Add(familiarScreen);
            }

            // 10. Eagle Aerial Rush (돌진 공격)
            var aerialRush = PlanFamiliarAerialRush(situation, ref remainingAP);
            if (aerialRush != null)
                actions.Add(aerialRush);

            // 11. Eagle Claws (폴백 근접 공격)
            if (blindingDive == null && aerialRush == null)
            {
                var eagleClaws = PlanFamiliarClaws(situation, ref remainingAP);
                if (eagleClaws != null)
                    actions.Add(eagleClaws);
            }

            return usedWarpRelay;
        }

        /// <summary>
        /// ★ v3.14.0: 공통 위치 버프 Phase — DPS/Tank/Support/Overseer 동일 while 루프
        /// MAX_POSITIONAL_BUFFS 만큼 반복하며 PlanPositionalBuff 호출
        /// </summary>
        /// <returns>계획된 위치 버프 수</returns>
        protected int ExecutePositionalBuffPhase(
            List<PlannedAction> actions,
            Situation situation,
            ref float remainingAP,
            HashSet<string> usedBuffGuids = null)
        {
            // ★ v3.104.0: 명시적 HashSet 없으면 BasePlan 통합 field 사용 → Phase 2/4 버프와 dedup 공유
            if (usedBuffGuids == null)
                usedBuffGuids = _plannedBuffGuids;

            int positionalBuffCount = 0;
            while (positionalBuffCount < MAX_POSITIONAL_BUFFS && remainingAP >= 1f)
            {
                var positionalBuffAction = PlanPositionalBuff(situation, ref remainingAP, usedBuffGuids);
                if (positionalBuffAction == null) break;
                actions.Add(positionalBuffAction);
                positionalBuffCount++;
            }

            return positionalBuffCount;
        }

        /// <summary>
        /// ★ v3.14.0: 공통 Fallback Buffs Phase — 공격 불가 시 남은 버프 소진
        /// DPS/Tank: tryAllyBuffFirst=true (아군 우선 시도)
        /// Support: tryAllyBuffFirst=false (자기 방어 우선)
        /// </summary>
        /// <param name="tryAllyBuffFirst">true면 CanTargetFriends 아군 우선 시도</param>
        /// <param name="includeFallbackDebuff">true면 공격 불가 시 디버프도 시도 (DPS)</param>
        protected void ExecuteFallbackBuffsPhase(
            List<PlannedAction> actions,
            Situation situation,
            ref float remainingAP,
            bool didPlanAttack,
            TacticalEvaluation tacticalEval,
            bool tryAllyBuffFirst = true,
            bool includeFallbackDebuff = false)
        {
            // ★ v3.8.98: 근접 MoveOnly 전략 시 fallback 버프 스킵
            bool skipFallbackForMelee = !situation.PrefersRanged &&
                tacticalEval?.ChosenStrategy == TacticalStrategy.MoveOnly &&
                situation.HasLivingEnemies;

            if (skipFallbackForMelee)
            {
                Log.Planning.Info($"[{RoleName}] Fallback buffs: Skipping (melee MoveOnly — save for post-move attack)");
            }
            else if (!didPlanAttack && remainingAP >= 1f && situation.AvailableBuffs.Count > 0)
            {
                Log.Planning.Info($"[{RoleName}] Fallback buffs: No attack possible, using remaining buffs (AP={remainingAP:F1})");

                foreach (var buff in situation.AvailableBuffs)
                {
                    if (remainingAP < 1f) break;

                    // 공격 전 버프는 공격이 없으면 의미 없음
                    var timing = AbilityDatabase.GetTiming(buff);
                    if (timing == AbilityTiming.PreAttackBuff ||
                        timing == AbilityTiming.HeroicAct ||
                        timing == AbilityTiming.RighteousFury ||
                        timing == AbilityTiming.TurnEnding)
                    {
                        if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Fallback buffs: Skip {buff.Name} (timing={timing})");
                        continue;
                    }

                    if (AbilityDatabase.IsSpringAttackAbility(buff))
                    {
                        if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Fallback buffs: Skip {buff.Name} (SpringAttack)");
                        continue;
                    }

                    // ★ v3.104.0: 이미 이 플랜에서 선택된 버프면 스킵 (Tank 인내 4× 중복 방지)
                    string buffGuid = buff.Blueprint?.AssetGuid?.ToString() ?? buff.Name ?? "";
                    if (_plannedBuffGuids.Contains(buffGuid))
                    {
                        if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Fallback buffs: Skip {buff.Name} (already planned this turn)");
                        continue;
                    }

                    float cost = CombatAPI.GetAbilityAPCost(buff);
                    if (cost > remainingAP) continue;

                    if (AllyStateCache.HasBuff(situation.Unit, buff)) continue;

                    var bp = buff.Blueprint;
                    if (bp?.CanTargetSelf != true && bp?.CanTargetFriends != true) continue;

                    // 아군 우선 시도 (DPS/Tank)
                    bool usedOnAlly = false;
                    // ★ v3.18.4: CombatantAllies 사용 (사역마 제외)
                    if (tryAllyBuffFirst && bp?.CanTargetFriends == true && situation.CombatantAllies != null)
                    {
                        foreach (var ally in situation.CombatantAllies)
                        {
                            if (ally == null || ally.LifeState.IsDead || ally == situation.Unit) continue;
                            if (AllyStateCache.HasBuff(ally, buff)) continue;
                            if (!CombatAPI.NeedsBuffRefresh(ally, buff)) continue;

                            var allyTarget = new TargetWrapper(ally);
                            string allyReason;
                            if (CombatAPI.CanUseAbilityOn(buff, allyTarget, out allyReason))
                            {
                                remainingAP -= cost;
                                actions.Add(PlannedAction.Buff(buff, ally, $"Fallback buff ally: {buff.Name}", cost));
                                _plannedBuffGuids.Add(buffGuid);  // ★ v3.104.0: dedup 등록
                                Log.Planning.Info($"[{RoleName}] Fallback buff (ally): {buff.Name} -> {ally.CharacterName}");
                                usedOnAlly = true;
                                break;
                            }
                        }
                    }

                    if (!usedOnAlly)
                    {
                        var target = new TargetWrapper(situation.Unit);
                        string reason;
                        if (CombatAPI.CanUseAbilityOn(buff, target, out reason))
                        {
                            remainingAP -= cost;
                            actions.Add(PlannedAction.Buff(buff, situation.Unit, "Fallback buff - no attack available", cost));
                            _plannedBuffGuids.Add(buffGuid);  // ★ v3.104.0: dedup 등록
                            Log.Planning.Info($"[{RoleName}] Fallback buff: {buff.Name}");
                        }
                    }
                }
            }

            // DPS 전용: 공격 불가 시 유틸리티 디버프
            // ★ v3.40.8: 면역 적에게 디버프 낭비 방지
            if (includeFallbackDebuff && !didPlanAttack && remainingAP >= 1f
                && situation.AvailableDebuffs.Count > 0 && situation.NearestEnemy != null
                && !CombatAPI.IsTargetImmuneToDamage(situation.NearestEnemy, situation.Unit))
            {
                var debuffAction = PlanDebuff(situation, situation.NearestEnemy, ref remainingAP);
                if (debuffAction != null)
                {
                    actions.Add(debuffAction);
                    Log.Planning.Info($"[{RoleName}] Fallback debuff: {debuffAction.Ability?.Name}");
                }
            }
        }

        #endregion
    }
}
