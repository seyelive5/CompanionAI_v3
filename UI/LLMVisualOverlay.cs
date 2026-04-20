using System.Collections.Generic;
using UnityEngine;
using Kingmaker.EntitySystem.Entities;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.UI
{
    /// <summary>
    /// ★ v3.109.0: LLM 시각 오버레이 — "AI가 생각하는 것"을 전장 위에 시각화.
    ///
    /// 세 가지 시각화:
    ///   1. 위협 랭킹 마커 — LLM/Scorer가 평가한 적 우선순위 (1위=크라운, 2-3위=숫자)
    ///   2. Priority Target 하이라이트 — LLM이 선택한 집중 공격 대상
    ///   3. 액션 프리뷰 — 이번 턴 예정된 액션을 아이콘으로 미리 표시 (X-COM 스타일)
    ///
    /// 구현 요점:
    ///   - 게임 로직 무영향 (순수 렌더링)
    ///   - IMGUI OnGUI 매 프레임 렌더 (DecisionOverlayBehaviour 통해)
    ///   - Camera.main.WorldToScreenPoint로 월드 → 스크린 변환
    ///   - 텍스처는 런타임 프로시저럴 생성 (에셋 의존 없음)
    ///   - ModSettings.EnableLLMVisualOverlay 토글로 on/off
    /// </summary>
    public static class LLMVisualOverlay
    {
        // ═══════════════════════════════════════════════════════════
        // State (TurnOrchestrator가 SetContext로 갱신)
        // ═══════════════════════════════════════════════════════════
        private static BaseUnitEntity _actingUnit;
        private static TurnPlan _currentPlan;
        private static List<BaseUnitEntity> _rankedEnemies;  // 위협도 내림차순 정렬된 적 목록 (top 5)
        private static int _priorityOriginalIdx = -1;       // LLM priority_target → situation.Enemies 원본 인덱스

        // ═══════════════════════════════════════════════════════════
        // Textures (런타임 프로시저럴 생성)
        // ═══════════════════════════════════════════════════════════
        private static Texture2D _circleSoft;    // 부드러운 원 (랭킹 배경)
        private static Texture2D _circleRing;    // 외곽선만 (AoE 반경)
        private static Texture2D _pixelWhite;    // 1x1 흰색 픽셀 (레이블 배경)

        private static GUIStyle _rankStyle;
        private static GUIStyle _actionStyle;
        private static GUIStyle _labelStyle;      // ★ v3.109.1: 아이콘 아래 설명 라벨
        private static GUIStyle _unitLabelStyle;  // ★ v3.109.1: Acting unit 헤더 라벨
        private static bool _stylesInit;

        // ═══════════════════════════════════════════════════════════
        // Public API — TurnOrchestrator에서 호출
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 오버레이 컨텍스트 갱신. 플랜 확정 직후 호출.
        /// ★ v3.110.2: Plan의 실제 공격 대상을 랭킹 #1로 우선 사용 (LLM priority 인덱스가 아닌 실제 AI 행동 기준).
        /// priorityOriginalIdx 파라미터는 하위 호환 유지, 그러나 Plan 첫 공격 대상이 최우선.
        /// </summary>
        /// <param name="actingUnit">현재 턴 유닛</param>
        /// <param name="plan">확정된 TurnPlan — 첫 Attack 액션의 타겟이 rank #1로 사용됨</param>
        /// <param name="situation">현재 Situation (위협 랭킹 계산용)</param>
        /// <param name="priorityOriginalIdx">LLM 지정 타겟 힌트 (Plan에서 추론 불가 시 fallback)</param>
        public static void SetContext(
            BaseUnitEntity actingUnit,
            TurnPlan plan,
            Situation situation,
            int priorityOriginalIdx)
        {
            _actingUnit = actingUnit;
            _currentPlan = plan;
            _priorityOriginalIdx = priorityOriginalIdx;
            _rankedEnemies = ComputeRankedEnemies(actingUnit, situation, plan, priorityOriginalIdx);
        }

        /// <summary>
        /// ★ v3.110.2: 위협도 내림차순 랭킹 (top 3).
        /// 우선순위: Plan 첫 공격 대상 > LLM priority > Situation.BestTarget > 위협 점수 최고 적.
        /// Plan 우선 — UI의 "TOP THREAT"이 AI의 **실제 공격 대상**과 일치하도록 함.
        /// </summary>
        private static List<BaseUnitEntity> ComputeRankedEnemies(
            BaseUnitEntity unit, Situation situation, TurnPlan plan, int priorityOriginalIdx)
        {
            var result = new List<BaseUnitEntity>(3);
            if (situation?.Enemies == null) return result;

            var remaining = new List<BaseUnitEntity>();
            foreach (var e in situation.Enemies)
            {
                if (e == null || e.LifeState.IsDead) continue;
                remaining.Add(e);
            }
            if (remaining.Count == 0) return result;

            // ★ v3.110.2: 1위는 Plan의 실제 첫 공격 대상 (UI-실행 일치 보장)
            BaseUnitEntity top = InferFirstAttackTarget(plan);

            // 폴백 체인
            if (top == null || top.LifeState.IsDead)
            {
                if (priorityOriginalIdx >= 0 && priorityOriginalIdx < situation.Enemies.Count)
                    top = situation.Enemies[priorityOriginalIdx];
            }
            if (top == null || top.LifeState.IsDead) top = situation.BestTarget;
            if (top == null || top.LifeState.IsDead)
                top = FindHighestThreat(remaining, unit, situation);

            if (top != null && !top.LifeState.IsDead)
            {
                result.Add(top);
                remaining.Remove(top);
            }

            // 2-3위: 나머지 중 위협 높은 순
            while (result.Count < 3 && remaining.Count > 0)
            {
                var next = FindHighestThreat(remaining, unit, situation);
                if (next == null) break;
                result.Add(next);
                remaining.Remove(next);
            }

            return result;
        }

        /// <summary>★ v3.110.2: Plan의 첫 Attack 액션에서 실제 공격 대상 추출.</summary>
        private static BaseUnitEntity InferFirstAttackTarget(TurnPlan plan)
        {
            if (plan?.AllActions == null) return null;
            foreach (var action in plan.AllActions)
            {
                if (action == null) continue;
                if (action.Type != ActionType.Attack && action.Type != ActionType.Special) continue;
                var target = action.Target?.Entity as BaseUnitEntity;
                if (target != null && !target.LifeState.IsDead) return target;
            }
            return null;
        }

        private static BaseUnitEntity FindHighestThreat(List<BaseUnitEntity> list, BaseUnitEntity unit, Situation situation)
        {
            BaseUnitEntity best = null;
            float bestScore = -1f;
            foreach (var e in list)
            {
                if (e == null || e.LifeState.IsDead) continue;
                float score = ComputeThreatScore(e, unit, situation);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = e;
                }
            }
            return best;
        }

        private static float ComputeThreatScore(BaseUnitEntity enemy, BaseUnitEntity unit, Situation situation)
        {
            if (enemy == null) return 0f;
            float score = 0f;
            float hp = CombatAPI.GetHPPercent(enemy);
            float dist = unit != null ? CombatAPI.GetDistanceInTiles(unit, enemy) : 20f;

            // BestTarget 보너스
            if (situation?.BestTarget != null && enemy.UniqueId == situation.BestTarget.UniqueId)
                score += 1000f;

            if (hp < 20f) score += 500f;  // finishable
            else score += (100f - hp);

            if (dist < 20f) score += (20f - dist) * 2f;
            return score;
        }

        /// <summary>턴 종료 시 오버레이 클리어.</summary>
        public static void Clear()
        {
            _actingUnit = null;
            _currentPlan = null;
            _rankedEnemies = null;
            _priorityOriginalIdx = -1;
        }

        /// <summary>매 프레임 IMGUI 렌더링 (DecisionOverlayBehaviour.OnGUI에서 호출).</summary>
        public static void OnGUI()
        {
            if (!Main.Enabled) return;
            if (ModSettings.Instance?.EnableLLMVisualOverlay != true) return;
            if (_actingUnit == null) return;
            if (Camera.main == null) return;

            InitTexturesIfNeeded();
            InitStylesIfNeeded();

            DrawActingUnitLabel();
            DrawThreatRanking();
            DrawActionPreview();
        }

        // ═══════════════════════════════════════════════════════════
        // 0. Acting Unit 라벨 — 현재 턴 유닛 위에 "PLANNING" 표시
        // ═══════════════════════════════════════════════════════════

        private static void DrawActingUnitLabel()
        {
            if (_actingUnit == null || _actingUnit.LifeState.IsDead) return;
            if (!WorldToScreen(_actingUnit.Position, out Vector2 screen, out _)) return;

            string unitName = _actingUnit.CharacterName ?? "Unit";
            string label = $"{unitName}'s PLAN";

            DrawTextWithShadow(label, screen.x, screen.y - 90f, 220f, 18f, _unitLabelStyle);
        }

        // ═══════════════════════════════════════════════════════════
        // 1. 위협 랭킹 + Priority Target 마커
        // ═══════════════════════════════════════════════════════════

        private static void DrawThreatRanking()
        {
            if (_rankedEnemies == null || _rankedEnemies.Count == 0) return;

            int maxRanks = Mathf.Min(_rankedEnemies.Count, 3);
            for (int rank = 0; rank < maxRanks; rank++)
            {
                var enemy = _rankedEnemies[rank];
                if (enemy == null || enemy.LifeState.IsDead) continue;

                if (!WorldToScreen(enemy.Position, out Vector2 screen, out _)) continue;

                // 크기와 색상: 1위=크고 골드, 2위=중간 오렌지, 3위=작고 회색
                float size = rank == 0 ? 46f : (rank == 1 ? 32f : 24f);
                Color color = rank == 0 ? new Color(1f, 0.85f, 0.2f, 0.95f)
                            : rank == 1 ? new Color(1f, 0.55f, 0.1f, 0.85f)
                            : new Color(0.7f, 0.7f, 0.7f, 0.75f);

                // 머리 위로 약간 띄움 (y 스크린 좌표 - offset)
                float offsetY = 55f + rank * 4f;
                var rect = new Rect(screen.x - size / 2f, screen.y - offsetY - size / 2f, size, size);

                // 배경 원
                GUI.color = color;
                GUI.DrawTexture(rect, _circleSoft);
                GUI.color = Color.white;

                // 랭크 숫자
                string label = rank == 0 ? "★" : (rank + 1).ToString();
                _rankStyle.fontSize = rank == 0 ? 28 : 18;
                GUI.Label(rect, label, _rankStyle);

                // ★ v3.109.1: 의미 라벨 추가 — 아이콘 아래 설명 텍스트
                string descLabel = rank == 0 ? "TOP THREAT"
                                 : rank == 1 ? "THREAT #2"
                                             : "THREAT #3";
                DrawTextWithShadow(descLabel, screen.x, screen.y - offsetY + size / 2f + 2f, 150f, 14f, _labelStyle);
            }
        }

        /// <summary>그림자 있는 중앙 정렬 텍스트 (배경 대비 가독성).</summary>
        private static void DrawTextWithShadow(string text, float cx, float top, float width, float height, GUIStyle style)
        {
            var rect = new Rect(cx - width / 2f, top, width, height);
            // 그림자 (검은색 오프셋)
            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            var shadow = rect; shadow.x += 1f; shadow.y += 1f;
            GUI.Label(shadow, text, style);
            // 본문
            GUI.color = Color.white;
            GUI.Label(rect, text, style);
        }

        // ═══════════════════════════════════════════════════════════
        // 2. 액션 프리뷰 (X-COM 스타일)
        // ═══════════════════════════════════════════════════════════

        private static void DrawActionPreview()
        {
            if (_currentPlan == null) return;
            var actions = _currentPlan.AllActions;
            if (actions == null || actions.Count == 0) return;

            // ★ v3.109.2: Move가 발생하면 이후 액션의 원점이 이동된 위치로 바뀜 — X-COM 스타일 연결선
            Vector3 effectivePos = _actingUnit != null ? _actingUnit.Position : Vector3.zero;

            int step = 0;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action == null) continue;
                if (action.Type == ActionType.EndTurn) continue;

                step++;
                if (step > 6) break;  // 최대 6개 액션만 표시 (화면 혼잡 방지)

                // ★ v3.109.2: 연결선 먼저 (아이콘 아래 층) — 액션에 외부 타겟이 있는 경우만
                Vector3 targetWorldPos = GetActionWorldPos(action, effectivePos);
                if (ShouldDrawConnectionLine(action))
                {
                    Color lineColor = GetActionColor(action.Type);
                    lineColor.a = 0.75f;
                    DrawWorldLine(effectivePos, targetWorldPos, lineColor, action.Type == ActionType.Move ? 3f : 2f);
                }

                // ★ v3.110.3: effectivePos 전달 — Move 뒤 Self-액션(Buff/Reload/Swap)이 이동 후 위치에 찍히도록
                DrawSingleActionPreview(action, step, effectivePos, targetWorldPos);

                // Move 이후 effective 위치 업데이트 → 후속 액션의 원점 이동
                if (action.Type == ActionType.Move && action.MoveDestination.HasValue)
                    effectivePos = action.MoveDestination.Value;
            }
        }

        /// <summary>연결선을 그릴 액션 타입인지 판정 — Move/Attack/Heal/Debuff/Support는 외부 타겟 有.</summary>
        private static bool ShouldDrawConnectionLine(PlannedAction action)
        {
            switch (action.Type)
            {
                case ActionType.Move:
                case ActionType.Attack:
                case ActionType.Special:
                case ActionType.Heal:
                case ActionType.Support:
                case ActionType.Debuff:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>액션 타겟의 월드 좌표 결정 (단일 메서드로 통합).</summary>
        private static Vector3 GetActionWorldPos(PlannedAction action, Vector3 selfPos)
        {
            switch (action.Type)
            {
                case ActionType.Move:
                    return action.MoveDestination ?? selfPos;
                case ActionType.Buff:
                case ActionType.Reload:
                case ActionType.WeaponSwitch:
                    return selfPos;
                default:
                    return GetTargetPosition(action);
            }
        }

        /// <summary>액션 타입 → 색상 (연결선과 아이콘 일관).</summary>
        private static Color GetActionColor(ActionType type)
        {
            switch (type)
            {
                case ActionType.Move: return new Color(0.4f, 0.8f, 1f, 1f);
                case ActionType.Attack:
                case ActionType.Special: return new Color(1f, 0.35f, 0.3f, 1f);
                case ActionType.Buff: return new Color(0.7f, 1f, 0.5f, 1f);
                case ActionType.Heal: return new Color(0.3f, 1f, 0.6f, 1f);
                case ActionType.Support: return new Color(0.3f, 1f, 0.6f, 1f);
                case ActionType.Debuff: return new Color(0.9f, 0.5f, 1f, 1f);
                default: return new Color(0.8f, 0.8f, 0.8f, 1f);
            }
        }

        // ═══════════════════════════════════════════════════════════
        // 월드 → 스크린 연결선 (IMGUI 회전 사각형 트릭)
        // ═══════════════════════════════════════════════════════════

        /// <summary>두 월드 좌표를 이어주는 선. 카메라 뒤에 있는 끝점은 그리지 않음.</summary>
        private static void DrawWorldLine(Vector3 worldStart, Vector3 worldEnd, Color color, float thickness)
        {
            if (!WorldToScreen(worldStart, out Vector2 s, out _)) return;
            if (!WorldToScreen(worldEnd, out Vector2 e, out _)) return;
            DrawScreenLine(s, e, color, thickness);
        }

        /// <summary>스크린 좌표 두 점을 잇는 선 (회전된 사각형 방식).</summary>
        private static void DrawScreenLine(Vector2 start, Vector2 end, Color color, float thickness)
        {
            Matrix4x4 prevMatrix = GUI.matrix;
            Color prevColor = GUI.color;

            Vector2 delta = end - start;
            float length = delta.magnitude;
            if (length < 1f) return;

            float angleDeg = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;

            GUIUtility.RotateAroundPivot(angleDeg, start);
            GUI.color = color;
            GUI.DrawTexture(new Rect(start.x, start.y - thickness / 2f, length, thickness), _pixelWhite);

            GUI.color = prevColor;
            GUI.matrix = prevMatrix;
        }

        /// <summary>
        /// ★ v3.110.3: 아이콘 위치는 effectivePos/targetWorldPos로 결정 (연결선 계산과 동일 소스).
        /// Self-액션(Buff/Reload/Swap)은 effectivePos, 외부타겟 액션은 targetWorldPos. 중복 switch 제거.
        /// </summary>
        private static void DrawSingleActionPreview(PlannedAction action, int step, Vector3 effectivePos, Vector3 targetWorldPos)
        {
            Vector3 worldPos;
            Color iconColor;
            string actionLabel;

            switch (action.Type)
            {
                case ActionType.Move:
                    worldPos = targetWorldPos;  // MoveDestination (없으면 selfPos = effectivePos)
                    iconColor = new Color(0.4f, 0.8f, 1f, 0.85f);  // 밝은 파랑
                    actionLabel = "MOVE";
                    break;

                case ActionType.Attack:
                case ActionType.Special:
                    worldPos = targetWorldPos;
                    iconColor = new Color(1f, 0.35f, 0.3f, 0.9f);   // 빨강
                    actionLabel = "ATTACK";
                    break;

                case ActionType.Buff:
                    worldPos = effectivePos;  // ★ v3.110.3: Move 이후 위치 반영
                    iconColor = new Color(0.7f, 1f, 0.5f, 0.85f);   // 연두
                    actionLabel = "BUFF";
                    break;

                case ActionType.Heal:
                    worldPos = targetWorldPos;
                    iconColor = new Color(0.3f, 1f, 0.6f, 0.9f);    // 초록
                    actionLabel = "HEAL";
                    break;

                case ActionType.Support:
                    worldPos = targetWorldPos;
                    iconColor = new Color(0.3f, 1f, 0.6f, 0.9f);
                    actionLabel = "SUPPORT";
                    break;

                case ActionType.Debuff:
                    worldPos = targetWorldPos;
                    iconColor = new Color(0.9f, 0.5f, 1f, 0.9f);    // 자주
                    actionLabel = "DEBUFF";
                    break;

                case ActionType.Reload:
                    worldPos = effectivePos;  // ★ v3.110.3: Move 이후 위치 반영
                    iconColor = new Color(0.8f, 0.8f, 0.8f, 0.8f);  // 회색
                    actionLabel = "RELOAD";
                    break;

                case ActionType.WeaponSwitch:
                    worldPos = effectivePos;  // ★ v3.110.3: Move 이후 위치 반영
                    iconColor = new Color(0.8f, 0.8f, 0.8f, 0.8f);
                    actionLabel = "SWAP";
                    break;

                default:
                    return;
            }

            if (!WorldToScreen(worldPos, out Vector2 screen, out _)) return;

            // ★ v3.109.3: AoE 반경을 실제 능력의 radius로 표시 (Circle 패턴 한정)
            // 기존: 고정 80px 링. 현재: GetAoERadius(타일) × GridCellSize → 실제 월드 크기 → 스크린 픽셀
            bool isAoE = TryGetAoeCircleRadiusMeters(action, out float aoeRadiusMeters);
            if (!isAoE)
            {
                // Circle 이외의 AoE (Cone/Ray/Sector)거나 멀티타겟인 경우 — 라벨만 붙이고 반경 생략
                isAoE = (action.AllTargets != null && action.AllTargets.Count > 1)
                      || (action.Target != null && action.Target.Entity == null && action.Target.Point != default);
            }
            else
            {
                float pixelRadius = WorldRadiusToScreenPixels(worldPos, aoeRadiusMeters);
                if (pixelRadius >= 10f)  // 너무 작으면 생략
                {
                    float diameter = pixelRadius * 2f;
                    var aoeRect = new Rect(screen.x - pixelRadius, screen.y - pixelRadius, diameter, diameter);
                    Color aoeColor = iconColor; aoeColor.a *= 0.35f;
                    GUI.color = aoeColor;
                    GUI.DrawTexture(aoeRect, _circleRing);
                    GUI.color = Color.white;
                }
            }
            if (isAoE) actionLabel = actionLabel + " (AoE)";

            // 액션 아이콘 (단계 번호만 원 안에)
            float size = 30f;
            var rect = new Rect(screen.x - size / 2f, screen.y - size / 2f, size, size);
            GUI.color = iconColor;
            GUI.DrawTexture(rect, _circleSoft);
            GUI.color = Color.white;
            _actionStyle.fontSize = 16;
            GUI.Label(rect, step.ToString(), _actionStyle);

            // ★ v3.109.1: 영어 라벨을 아이콘 아래에 표시 — 의미 명시
            DrawTextWithShadow(actionLabel, screen.x, screen.y + size / 2f + 2f, 140f, 16f, _labelStyle);
        }

        private static Vector3 GetTargetPosition(PlannedAction action)
        {
            if (action?.Target == null) return Vector3.zero;
            var entity = action.Target.Entity as BaseUnitEntity;
            if (entity != null) return entity.Position;
            return action.Target.Point;
        }

        /// <summary>
        /// ★ v3.109.3: 능력의 AoE 반경 조회 (Circle 패턴만, 미터 단위).
        /// Cone/Ray/Sector는 방향성 패턴이라 원으로 표시 불가 → false.
        /// </summary>
        private static bool TryGetAoeCircleRadiusMeters(PlannedAction action, out float radiusMeters)
        {
            radiusMeters = 0f;
            if (action?.Ability == null) return false;

            var patternType = CombatAPI.GetPatternType(action.Ability);
            // Circle 패턴 또는 pattern 없음(= Blueprint.AoERadius 기반) 인 경우만 원 표시
            if (patternType.HasValue && patternType.Value != Kingmaker.Blueprints.PatternType.Circle)
                return false;

            float radiusTiles = CombatAPI.GetAoERadius(action.Ability);
            if (radiusTiles <= 0f) return false;

            radiusMeters = radiusTiles * CombatAPI.GridCellSize;
            return true;
        }

        /// <summary>
        /// ★ v3.109.3: 월드 반경 → 스크린 픽셀 변환.
        /// 카메라 right 벡터로 offset → WorldToScreen 두 점 거리 측정.
        /// </summary>
        private static float WorldRadiusToScreenPixels(Vector3 worldCenter, float worldRadius)
        {
            if (Camera.main == null) return 0f;
            Vector3 offset = worldCenter + Camera.main.transform.right * worldRadius;

            Vector3 spCenter = Camera.main.WorldToScreenPoint(worldCenter);
            Vector3 spOffset = Camera.main.WorldToScreenPoint(offset);
            if (spCenter.z <= 0f || spOffset.z <= 0f) return 0f;

            return Vector2.Distance(
                new Vector2(spCenter.x, spCenter.y),
                new Vector2(spOffset.x, spOffset.y));
        }

        // ═══════════════════════════════════════════════════════════
        // WorldToScreen 변환 (Y 반전 포함)
        // ═══════════════════════════════════════════════════════════

        private static bool WorldToScreen(Vector3 world, out Vector2 screen, out float depth)
        {
            screen = default;
            depth = 0f;
            if (Camera.main == null) return false;

            Vector3 sp = Camera.main.WorldToScreenPoint(world);
            depth = sp.z;
            if (sp.z <= 0f) return false;  // 카메라 뒤

            // Unity 스크린 원점(bottom-left) → IMGUI 좌표(top-left) 변환
            screen = new Vector2(sp.x, Screen.height - sp.y);
            return true;
        }

        // ═══════════════════════════════════════════════════════════
        // 프로시저럴 텍스처 생성 (에셋 의존 없음)
        // ═══════════════════════════════════════════════════════════

        private static void InitTexturesIfNeeded()
        {
            if (_circleSoft == null) _circleSoft = CreateSoftCircle(64, 2f);
            if (_circleRing == null) _circleRing = CreateRing(96, 3f);
            if (_pixelWhite == null) _pixelWhite = CreatePixel(Color.white);
        }

        private static Texture2D CreateSoftCircle(int size, float edgeSmooth)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float r = size / 2f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - r + 0.5f;
                    float dy = y - r + 0.5f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float alpha = Mathf.Clamp01((r - d) / edgeSmooth);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            tex.filterMode = FilterMode.Bilinear;
            tex.Apply();
            return tex;
        }

        private static Texture2D CreateRing(int size, float thickness)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float r = size / 2f;
            float inner = r - thickness;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - r + 0.5f;
                    float dy = y - r + 0.5f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float alpha = 0f;
                    if (d <= r && d >= inner)
                    {
                        // 외곽은 부드럽게
                        float edge = Mathf.Min(r - d, d - inner);
                        alpha = Mathf.Clamp01(edge);
                    }
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            tex.filterMode = FilterMode.Bilinear;
            tex.Apply();
            return tex;
        }

        private static Texture2D CreatePixel(Color color)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }

        // ═══════════════════════════════════════════════════════════
        // GUI 스타일
        // ═══════════════════════════════════════════════════════════

        private static void InitStylesIfNeeded()
        {
            if (_stylesInit) return;

            _rankStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.black }
            };

            _actionStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };

            _unitLabelStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1f, 0.9f, 0.4f) }  // 골드
            };

            _stylesInit = true;
        }
    }
}
