using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.GameInterface;

namespace CompanionAI_v3.Core
{
    /// <summary>
    /// ★ v3.2.10: 팀 전술 정보 공유 시스템 (Blackboard 패턴)
    ///
    /// 각 유닛의 상황과 계획을 공유하여 조율된 팀 전술을 가능하게 함.
    /// - 타겟 공유: DPS들이 같은 적 집중 공격
    /// - 전술 신호: 팀 HP에 따라 Attack/Defend/Retreat 전환
    /// - 역할 조율: Tank 도발 시 DPS가 후방 공격
    /// </summary>
    public class TeamBlackboard
    {
        #region Singleton

        private static TeamBlackboard _instance;
        public static TeamBlackboard Instance => _instance ??= new TeamBlackboard();

        private TeamBlackboard() { }

        #endregion

        #region Fields

        /// <summary>유닛별 상황 캐시 (UniqueId → Situation)</summary>
        private readonly Dictionary<string, Situation> _unitSituations = new Dictionary<string, Situation>();

        /// <summary>유닛별 계획 캐시 (UniqueId → TurnPlan)</summary>
        private readonly Dictionary<string, TurnPlan> _unitPlans = new Dictionary<string, TurnPlan>();

        /// <summary>현재 라운드 (캐시 무효화용)</summary>
        private int _currentRound = -1;

        #endregion

        #region ★ v3.5.00: Kill/Damage Tracking (PDF 방법론)

        /// <summary>현재 라운드 킬 카운트</summary>
        private int _roundKillCount = 0;

        /// <summary>현재 라운드 가한 피해량</summary>
        private float _roundDamageDealt = 0f;

        /// <summary>현재 라운드 받은 피해량</summary>
        private float _roundDamageTaken = 0f;

        /// <summary>전투 전체 킬 카운트</summary>
        private int _combatKillCount = 0;

        /// <summary>전투 전체 가한 피해량</summary>
        private float _combatDamageDealt = 0f;

        /// <summary>킬 모멘텀 (0.0 ~ 1.0) - 최근 킬 성과 반영</summary>
        public float KillMomentum { get; private set; } = 0f;

        /// <summary>데미지 비율 (0.0 ~ 1.0) - 가한 피해 / (가한 + 받은)</summary>
        public float DamageRatio { get; private set; } = 0.5f;

        #endregion

        #region ★ v3.5.10: Action Reservation System (역할 선점)

        /// <summary>도발 예약된 적 ID 집합</summary>
        private readonly HashSet<string> _reservedTauntTargets = new HashSet<string>();

        /// <summary>힐 예약된 아군 ID 집합</summary>
        private readonly HashSet<string> _reservedHealTargets = new HashSet<string>();

        #endregion

        #region ★ v3.10.0: Position Reservation System (이동 위치 선점)

        /// <summary>이번 라운드에 유닛들이 예약한 이동 목적지 (밀집 방지)</summary>
        private readonly List<UnityEngine.Vector3> _reservedMovePositions = new List<UnityEngine.Vector3>(6);

        #endregion

        #region ★ v3.7.87: Round Action Tracking (보너스 턴 대응)

        /// <summary>이번 라운드에 이미 행동한 유닛 ID 집합</summary>
        private readonly HashSet<string> _actedThisRound = new HashSet<string>();

        #endregion

        #region ★ v3.8.46: Target Inertia (타겟 관성)

        /// <summary>유닛별 이전 턴 공격 타겟 (UniqueId → 타겟)</summary>
        private readonly Dictionary<string, BaseUnitEntity> _previousTargets = new Dictionary<string, BaseUnitEntity>();

        #endregion

        #region Team Tactical State

        /// <summary>팀 공유 타겟 (가장 많이 지정된 적)</summary>
        public BaseUnitEntity SharedTarget { get; private set; }

        /// <summary>팀 평균 HP (%)</summary>
        public float AverageAllyHP { get; private set; }

        /// <summary>위험 상태 아군 수 (HP < 50%)</summary>
        public int LowHPAlliesCount { get; private set; }

        /// <summary>치명적 상태 아군 수 (HP < 30%)</summary>
        public int CriticalHPAlliesCount { get; private set; }

        /// <summary>현재 팀 전술 신호</summary>
        public TacticalSignal CurrentTactic { get; private set; } = TacticalSignal.Attack;

        /// <summary>★ v3.2.20: 팀 신뢰도 (0.0=절망 ~ 1.0=압도)</summary>
        public float TeamConfidence { get; private set; } = 0.5f;

        /// <summary>
        /// ★ v3.5.36: 신뢰도 값을 상태로 변환
        /// PDF 방법론의 Heroic/Confident/Neutral/Worried/Panicked 상태 분류
        /// </summary>
        public ConfidenceState GetConfidenceState()
        {
            if (TeamConfidence > 0.8f) return ConfidenceState.Heroic;
            if (TeamConfidence > 0.6f) return ConfidenceState.Confident;
            if (TeamConfidence > 0.4f) return ConfidenceState.Neutral;
            if (TeamConfidence > 0.2f) return ConfidenceState.Worried;
            return ConfidenceState.Panicked;
        }

        /// <summary>전투 활성화 여부</summary>
        public bool IsCombatActive { get; private set; }

        #endregion

        #region Combat Lifecycle

        /// <summary>
        /// 전투 시작 시 호출 - Blackboard 초기화
        /// </summary>
        public void InitializeCombat()
        {
            Clear();
            IsCombatActive = true;
            _currentRound = 1;
            Main.Log("[TeamBlackboard] Combat initialized");
        }

        /// <summary>
        /// 전투 종료 시 호출 - 모든 데이터 정리
        /// </summary>
        public void Clear()
        {
            _unitSituations.Clear();
            _unitPlans.Clear();
            SharedTarget = null;
            AverageAllyHP = 100f;
            LowHPAlliesCount = 0;
            CriticalHPAlliesCount = 0;
            CurrentTactic = TacticalSignal.Attack;
            TeamConfidence = 0.5f;
            IsCombatActive = false;
            _currentRound = -1;

            // ★ v3.5.00: Kill/Damage 추적 초기화
            _roundKillCount = 0;
            _roundDamageDealt = 0f;
            _roundDamageTaken = 0f;
            _combatKillCount = 0;
            _combatDamageDealt = 0f;
            KillMomentum = 0f;
            DamageRatio = 0.5f;

            // ★ v3.5.10: 역할 예약 초기화
            ClearReservations();

            // ★ v3.7.87: 라운드 행동 기록 초기화
            _actedThisRound.Clear();

            // ★ v3.8.46: 타겟 관성 초기화 (전투 종료 시에만)
            _previousTargets.Clear();

            Main.LogDebug("[TeamBlackboard] Cleared");
        }

        /// <summary>
        /// ★ v3.5.00: 라운드 시작 시 호출 - 라운드별 통계 리셋 + 모멘텀 계산
        /// </summary>
        public void OnRoundStart(int roundNumber)
        {
            // 이전 라운드 킬 기반 모멘텀 계산 (킬당 0.2, 최대 1.0)
            if (_currentRound > 0)
            {
                KillMomentum = Math.Min(1f, _roundKillCount * 0.25f);
                Main.LogDebug($"[TeamBlackboard] Round {_currentRound} end: Kills={_roundKillCount}, Momentum={KillMomentum:F2}");
            }

            // 데미지 비율 업데이트
            float totalDamage = _combatDamageDealt + _roundDamageTaken + 1f; // +1 to avoid div/0
            DamageRatio = _combatDamageDealt / totalDamage;

            // 라운드 카운터 리셋
            _currentRound = roundNumber;
            _roundKillCount = 0;
            _roundDamageDealt = 0f;
            _roundDamageTaken = 0f;

            // ★ v3.5.10: 새 라운드 시작 시 역할 예약 초기화
            ClearReservations();

            // ★ v3.7.87: 라운드 행동 기록 초기화 (보너스 턴 대응)
            ClearActedThisRound();

            Main.Log($"[TeamBlackboard] Round {roundNumber} started. Combat kills={_combatKillCount}, DmgRatio={DamageRatio:F2}");
        }

        /// <summary>
        /// ★ v3.5.00: 킬 기록
        /// </summary>
        public void RecordKill(BaseUnitEntity enemy)
        {
            if (enemy == null) return;

            _roundKillCount++;
            _combatKillCount++;

            // 킬 시 즉시 모멘텀 보너스 (+0.15)
            KillMomentum = Math.Min(1f, KillMomentum + 0.15f);

            Main.Log($"[TeamBlackboard] Kill recorded: {enemy.CharacterName}. Round kills={_roundKillCount}, Momentum={KillMomentum:F2}");

            // 팀 상태 재평가
            UpdateTeamAssessment();
        }

        /// <summary>
        /// ★ v3.5.00: 가한 피해량 기록
        /// </summary>
        public void RecordDamageDealt(float damage)
        {
            if (damage <= 0) return;

            _roundDamageDealt += damage;
            _combatDamageDealt += damage;

            Main.LogDebug($"[TeamBlackboard] Damage dealt: {damage:F0}. Round total={_roundDamageDealt:F0}");
        }

        /// <summary>
        /// ★ v3.5.00: 받은 피해량 기록
        /// </summary>
        public void RecordDamageTaken(float damage)
        {
            if (damage <= 0) return;

            _roundDamageTaken += damage;

            Main.LogDebug($"[TeamBlackboard] Damage taken: {damage:F0}. Round total={_roundDamageTaken:F0}");
        }

        #endregion

        #region Registration Methods

        /// <summary>
        /// 유닛의 상황 분석 결과 등록
        /// </summary>
        public void RegisterUnitSituation(string unitId, Situation situation)
        {
            if (string.IsNullOrEmpty(unitId) || situation == null) return;

            _unitSituations[unitId] = situation;
            Main.LogDebug($"[TeamBlackboard] Registered situation for {situation.Unit?.CharacterName}");
        }

        /// <summary>
        /// 유닛의 턴 계획 등록 + 팀 상태 업데이트
        /// </summary>
        public void RegisterUnitPlan(string unitId, TurnPlan plan)
        {
            if (string.IsNullOrEmpty(unitId) || plan == null) return;

            _unitPlans[unitId] = plan;

            // 계획 등록 시마다 팀 상태 재계산
            UpdateTeamAssessment();

            Main.LogDebug($"[TeamBlackboard] Registered plan for {unitId}, Tactic={CurrentTactic}");
        }

        #endregion

        #region Team Assessment

        /// <summary>
        /// 팀 전체 상태 평가 및 전술 신호 결정
        /// </summary>
        public void UpdateTeamAssessment()
        {
            if (_unitSituations.Count == 0) return;

            // 1. 팀 HP 계산
            CalculateTeamHP();

            // 2. 공유 타겟 결정
            DetermineSharedTarget();

            // 3. 전술 신호 결정
            DetermineTacticalSignal();

            // 4. ★ v3.2.20: 팀 신뢰도 계산
            CalculateConfidence();

            // ★ v3.5.36: ConfidenceState 포함
            Main.LogDebug($"[TeamBlackboard] Team: AvgHP={AverageAllyHP:F0}%, " +
                $"LowHP={LowHPAlliesCount}, Critical={CriticalHPAlliesCount}, " +
                $"Tactic={CurrentTactic}, Confidence={TeamConfidence:F2} ({GetConfidenceState()}), Target={SharedTarget?.CharacterName ?? "None"}");
        }

        private void CalculateTeamHP()
        {
            float totalHP = 0f;
            int allyCount = 0;
            int lowHPCount = 0;
            int criticalCount = 0;

            foreach (var kvp in _unitSituations)
            {
                var situation = kvp.Value;
                if (situation?.Unit == null) continue;

                float hp = situation.HPPercent;
                totalHP += hp;
                allyCount++;

                if (hp < 50f) lowHPCount++;
                if (hp < 30f) criticalCount++;
            }

            AverageAllyHP = allyCount > 0 ? totalHP / allyCount : 100f;
            LowHPAlliesCount = lowHPCount;
            CriticalHPAlliesCount = criticalCount;
        }

        private void DetermineSharedTarget()
        {
            // 각 유닛의 BestTarget을 집계하여 가장 많이 지정된 적 선택
            var targetCounts = new Dictionary<BaseUnitEntity, int>();

            foreach (var kvp in _unitSituations)
            {
                var target = kvp.Value?.BestTarget;
                if (target == null || target.LifeState.IsDead) continue;

                if (!targetCounts.ContainsKey(target))
                    targetCounts[target] = 0;
                targetCounts[target]++;
            }

            if (targetCounts.Count == 0)
            {
                SharedTarget = null;
                return;
            }

            // 가장 많이 선택된 타겟 (동률 시 HP 낮은 적 우선)
            SharedTarget = targetCounts
                .OrderByDescending(kvp => kvp.Value)
                .ThenBy(kvp => CombatCache.GetHPPercent(kvp.Key))
                .First().Key;
        }

        private void DetermineTacticalSignal()
        {
            // 전술 결정 로직:
            // - Attack: 팀이 건강하고 위험 아군이 적음
            // - Defend: 일부 아군이 위험하지만 전투 가능
            // - Retreat: 팀이 위험 상태

            if (AverageAllyHP >= 60f && LowHPAlliesCount <= 1)
            {
                CurrentTactic = TacticalSignal.Attack;
            }
            else if (AverageAllyHP >= 40f && CriticalHPAlliesCount <= 1)
            {
                CurrentTactic = TacticalSignal.Defend;
            }
            else
            {
                CurrentTactic = TacticalSignal.Retreat;
            }
        }

        /// <summary>
        /// ★ v3.5.00: 강화된 팀 신뢰도 계산 (PDF 방법론)
        /// Confidence = AllyHP(30%) + EnemyDamage(25%) + NumberAdvantage(20%) + KillMomentum(15%) + DamageRatio(10%)
        /// 기존: HP 기반 단순 공식
        /// 개선: Kill Count, Damage 비율 반영
        /// </summary>
        private void CalculateConfidence()
        {
            if (_unitSituations.Count == 0)
            {
                TeamConfidence = 0.5f;
                return;
            }

            // 1. 아군 HP 비율 (0-1)
            float allyHPFactor = AverageAllyHP / 100f;

            // 2. 적 피해 비율 (0-1) - 적 HP가 낮을수록 높음
            float avgEnemyHP = GetAverageEnemyHP();
            float enemyDamageFactor = (100f - avgEnemyHP) / 100f;

            // 3. 수적 우위 (0-1)
            int allyCount = _unitSituations.Count;
            int enemyCount = GetTotalEnemyCount();
            float numberFactor = Math.Min(1f, Math.Max(0f, (float)allyCount / (enemyCount + 0.1f) * 0.5f));

            // ★ v3.5.00: 새 요소들
            // 4. 킬 모멘텀 (0-1) - 최근 킬 성과
            float momentumFactor = KillMomentum;

            // 5. 데미지 비율 (0-1) - 가한 피해 / 받은 피해
            float damageRatioFactor = DamageRatio;

            // 가중치 합산 (30% + 25% + 20% + 15% + 10% = 100%)
            TeamConfidence = Math.Min(1f, Math.Max(0f,
                allyHPFactor * 0.30f +
                enemyDamageFactor * 0.25f +
                numberFactor * 0.20f +
                momentumFactor * 0.15f +
                damageRatioFactor * 0.10f
            ));

            // ★ v3.5.36: ConfidenceState 포함
            Main.LogDebug($"[TeamBlackboard] Confidence={TeamConfidence:F2} ({GetConfidenceState()}) " +
                $"(AllyHP={allyHPFactor:F2}, EnemyDmg={enemyDamageFactor:F2}, Numbers={numberFactor:F2}, " +
                $"Momentum={momentumFactor:F2}, DmgRatio={damageRatioFactor:F2})");
        }

        // ★ v3.5.36: null 방어 강화 - Enemies 컬렉션이 null이 아니고 비어있지 않은 경우만 처리
        private float GetAverageEnemyHP()
        {
            var enemies = _unitSituations.Values
                .Where(s => s?.Enemies != null && s.Enemies.Count > 0)
                .SelectMany(s => s.Enemies)
                .Where(e => e != null && !e.LifeState.IsDead)
                .Distinct()
                .ToList();

            if (enemies.Count == 0) return 50f;
            return enemies.Average(e => CombatCache.GetHPPercent(e));
        }

        private int GetTotalEnemyCount()
        {
            return _unitSituations.Values
                .Where(s => s?.Enemies != null && s.Enemies.Count > 0)
                .SelectMany(s => s.Enemies)
                .Where(e => e != null && !e.LifeState.IsDead)
                .Distinct()
                .Count();
        }

        #endregion

        #region Query Methods

        /// <summary>
        /// 특정 유닛의 캐시된 상황 조회
        /// </summary>
        public Situation GetUnitSituation(string unitId)
        {
            return _unitSituations.TryGetValue(unitId, out var situation) ? situation : null;
        }

        /// <summary>
        /// 특정 유닛의 캐시된 계획 조회
        /// </summary>
        public TurnPlan GetUnitPlan(string unitId)
        {
            return _unitPlans.TryGetValue(unitId, out var plan) ? plan : null;
        }

        /// <summary>
        /// 팀에 힐러가 있는지 확인
        /// </summary>
        public bool HasActiveHealer()
        {
            foreach (var kvp in _unitSituations)
            {
                var situation = kvp.Value;
                if (situation?.AvailableHeals?.Count > 0 && situation.HPPercent > 30f)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 가장 위험한 아군 조회 (힐 우선순위)
        /// </summary>
        public BaseUnitEntity GetMostWoundedAlly()
        {
            BaseUnitEntity mostWounded = null;
            float lowestHP = float.MaxValue;

            foreach (var kvp in _unitSituations)
            {
                var situation = kvp.Value;
                if (situation?.Unit == null) continue;

                if (situation.HPPercent < lowestHP)
                {
                    lowestHP = situation.HPPercent;
                    mostWounded = situation.Unit;
                }
            }

            return mostWounded;
        }

        /// <summary>
        /// 특정 적을 타겟으로 지정한 아군 수
        /// </summary>
        public int CountAlliesTargeting(BaseUnitEntity enemy)
        {
            if (enemy == null) return 0;

            int count = 0;
            foreach (var kvp in _unitSituations)
            {
                if (kvp.Value?.BestTarget == enemy)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// 팀 공유 타겟 강제 설정 (Tank 도발 등)
        /// </summary>
        public void SetSharedTarget(BaseUnitEntity target)
        {
            if (target == null || target.LifeState.IsDead) return;
            SharedTarget = target;
            Main.Log($"[TeamBlackboard] Shared target set: {target.CharacterName}");
        }

        #endregion

        #region ★ v3.7.87: Round Action Tracking API

        /// <summary>
        /// 유닛이 이번 라운드에 행동했음을 기록
        /// 보너스 턴(쳐부숴라 등)으로 행동한 경우에도 호출
        /// </summary>
        public void RecordUnitActed(BaseUnitEntity unit)
        {
            if (unit == null) return;

            string id = unit.UniqueId ?? unit.CharacterName ?? "unknown";
            if (_actedThisRound.Add(id))
            {
                Main.Log($"[Blackboard] ★ Unit acted this round: {unit.CharacterName}");
            }
        }

        /// <summary>
        /// ★ v3.7.94: 유닛이 이번 라운드에 이미 행동했는지 확인
        /// 게임 공식 API 사용 (수동 추적 대신)
        /// </summary>
        public bool HasActedThisRound(BaseUnitEntity unit)
        {
            if (unit == null) return false;

            try
            {
                // ★ v3.7.94: 게임 공식 API - Initiative.ActedThisRound
                // LastTurn == Game.Instance.TurnController.GameRound 로 계산됨
                bool acted = unit.Initiative?.ActedThisRound ?? false;

                if (acted)
                {
                    Main.LogDebug($"[Blackboard] Unit already acted this round (Game API): {unit.CharacterName}");
                }

                return acted;
            }
            catch (Exception ex)
            {
                // API 접근 실패 시 폴백 (레거시 방식)
                Main.LogDebug($"[Blackboard] Initiative API failed, using fallback: {ex.Message}");
                string id = unit.UniqueId ?? unit.CharacterName ?? "unknown";
                return _actedThisRound.Contains(id);
            }
        }

        /// <summary>
        /// 라운드 행동 기록 초기화 (라운드 시작 시 자동 호출)
        /// </summary>
        private void ClearActedThisRound()
        {
            int count = _actedThisRound.Count;
            _actedThisRound.Clear();

            if (count > 0)
            {
                Main.LogDebug($"[Blackboard] Cleared {count} acted units for new round");
            }
        }

        #endregion

        #region ★ v3.8.46: Target Inertia API

        /// <summary>
        /// 공격 실행 후 타겟 기록 (다음 턴 관성 보너스용)
        /// 라운드 간 유지, 전투 종료 시에만 초기화
        /// </summary>
        public void SetPreviousTarget(string unitId, BaseUnitEntity target)
        {
            if (string.IsNullOrEmpty(unitId) || target == null) return;
            _previousTargets[unitId] = target;
            Main.LogDebug($"[Blackboard] Previous target set: {unitId} -> {target.CharacterName}");
        }

        /// <summary>
        /// 이전 턴 공격 타겟 조회 (사망/무효 시 자동 제거)
        /// </summary>
        public BaseUnitEntity GetPreviousTarget(string unitId)
        {
            if (string.IsNullOrEmpty(unitId)) return null;
            if (!_previousTargets.TryGetValue(unitId, out var target)) return null;

            try
            {
                // 사망한 타겟은 관성 대상에서 제거
                if (target.LifeState?.IsDead == true)
                {
                    _previousTargets.Remove(unitId);
                    return null;
                }
            }
            catch
            {
                // Stale reference 처리
                _previousTargets.Remove(unitId);
                return null;
            }

            return target;
        }

        #endregion

        #region ★ v3.5.10: Action Reservation API

        /// <summary>
        /// 도발 대상 예약 (중복 도발 방지)
        /// </summary>
        /// <param name="target">도발할 적</param>
        /// <returns>예약 성공 여부 (이미 예약된 경우 false)</returns>
        public bool ReserveTaunt(BaseUnitEntity target)
        {
            if (target == null) return false;

            string id = target.UniqueId ?? target.CharacterName ?? "unknown";
            if (_reservedTauntTargets.Contains(id))
            {
                Main.LogDebug($"[Blackboard] Taunt already reserved: {target.CharacterName}");
                return false;
            }

            _reservedTauntTargets.Add(id);
            Main.Log($"[Blackboard] Taunt reserved: {target.CharacterName}");
            return true;
        }

        /// <summary>
        /// 힐 대상 예약 (중복 힐 방지)
        /// </summary>
        /// <param name="target">힐할 아군</param>
        /// <returns>예약 성공 여부 (이미 예약된 경우 false)</returns>
        public bool ReserveHeal(BaseUnitEntity target)
        {
            if (target == null) return false;

            string id = target.UniqueId ?? target.CharacterName ?? "unknown";
            if (_reservedHealTargets.Contains(id))
            {
                Main.LogDebug($"[Blackboard] Heal already reserved: {target.CharacterName}");
                return false;
            }

            _reservedHealTargets.Add(id);
            Main.Log($"[Blackboard] Heal reserved: {target.CharacterName}");
            return true;
        }

        /// <summary>
        /// 도발 예약 여부 확인
        /// </summary>
        public bool IsTauntReserved(BaseUnitEntity target)
        {
            if (target == null) return false;
            string id = target.UniqueId ?? target.CharacterName ?? "unknown";
            return _reservedTauntTargets.Contains(id);
        }

        /// <summary>
        /// 힐 예약 여부 확인
        /// </summary>
        public bool IsHealReserved(BaseUnitEntity target)
        {
            if (target == null) return false;
            string id = target.UniqueId ?? target.CharacterName ?? "unknown";
            return _reservedHealTargets.Contains(id);
        }

        /// <summary>
        /// 모든 예약 초기화 (라운드 시작 시)
        /// </summary>
        public void ClearReservations()
        {
            int tauntCount = _reservedTauntTargets.Count;
            int healCount = _reservedHealTargets.Count;
            int posCount = _reservedMovePositions.Count;

            _reservedTauntTargets.Clear();
            _reservedHealTargets.Clear();
            _reservedMovePositions.Clear();

            if (tauntCount > 0 || healCount > 0 || posCount > 0)
            {
                Main.LogDebug($"[Blackboard] Reservations cleared: {tauntCount} taunts, {healCount} heals, {posCount} positions");
            }
        }

        #endregion

        #region ★ v3.10.0: Position Reservation API (이동 밀집 방지)

        /// <summary>
        /// 이동 목적지 예약 (다른 유닛이 같은 위치 선택 방지)
        /// 계획 단계에서 이동 목적지가 확정되면 호출
        /// </summary>
        public void ReserveMovePosition(UnityEngine.Vector3 position)
        {
            _reservedMovePositions.Add(position);
            Main.LogDebug($"[Blackboard] Position reserved: ({position.x:F1}, {position.z:F1}). Total={_reservedMovePositions.Count}");
        }

        /// <summary>
        /// 예약된 이동 목적지 목록 조회 (밀집 패널티 계산용)
        /// </summary>
        public List<UnityEngine.Vector3> GetReservedMovePositions()
        {
            return _reservedMovePositions;
        }

        #endregion

        #region Debug

        public override string ToString()
        {
            return $"[TeamBlackboard] Units={_unitSituations.Count}, " +
                   $"AvgHP={AverageAllyHP:F0}%, Tactic={CurrentTactic}, " +
                   $"Target={SharedTarget?.CharacterName ?? "None"}";
        }

        #endregion
    }

    /// <summary>
    /// 팀 전술 신호
    /// </summary>
    public enum TacticalSignal
    {
        /// <summary>공격적 - 버프 스킵, 즉시 공격</summary>
        Attack,

        /// <summary>방어적 - 힐/버프 우선, 신중한 공격</summary>
        Defend,

        /// <summary>철수 - 힐/후퇴 우선, 생존 최우선</summary>
        Retreat
    }

    /// <summary>
    /// ★ v3.5.36: 팀 신뢰도 상태 (PDF 방법론)
    /// TeamConfidence 값에 따른 전술 상태 분류
    /// </summary>
    public enum ConfidenceState
    {
        /// <summary>영웅적 (>0.8) - 측면 공격, 공격적 포지셔닝, 적극 추격</summary>
        Heroic,

        /// <summary>자신감 (0.6~0.8) - 지속 공격, 이니셔티브 유지</summary>
        Confident,

        /// <summary>중립 (0.4~0.6) - 현 위치 유지, 기회주의적 공격</summary>
        Neutral,

        /// <summary>우려 (0.2~0.4) - 후퇴 고려, 방어적 행동, 엄폐 우선</summary>
        Worried,

        /// <summary>공황 (≤0.2) - 즉시 엄폐/후퇴, 생존 최우선</summary>
        Panicked
    }
}
