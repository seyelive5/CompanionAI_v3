# Tactical Narrator v2 Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 각 동료 캐릭터의 턴 시작 시 전장 상황+행동 계획+개성 대사를 IMGUI 오버레이에 표시하는 시스템 구축

**Architecture:** TacticalNarrator(진입점) → TacticalDialogueDB(JSON 대사 로드+선택) → TacticalOverlayUI(IMGUI 렌더링). 기존 DecisionNarrator/NarrativeBuilder를 완전 교체하고, v3.46.0에서 추가한 Strategic Directive 시스템을 롤백.

**Tech Stack:** C# (.NET 4.8.1), Unity IMGUI, Newtonsoft.Json, Harmony patches

---

## Task 1: Strategic Directive 시스템 롤백

v3.46.0에서 추가한 UserDirective/DirectiveManager 전체를 제거하고 관련 코드를 원복.

**Files:**
- Delete: `Core/UserDirective.cs`
- Modify: `Core/TurnState.cs` — `CurrentDirective` 프로퍼티 제거
- Modify: `Planning/TurnPlanner.cs` — directive 읽기/Hold 처리 제거
- Modify: `Planning/TurnStrategyPlanner.cs` — `directive` 파라미터, `GetDirectiveWeight()` 제거
- Modify: `Planning/Plans/BasePlan.cs` — `ShouldRetreat(directive)`, `EvaluateTacticalOptions(directive)`, `ExecuteCommonEarlyPhases(directive)` directive 파라미터 제거
- Modify: `Planning/Plans/DPSPlan.cs` — directive 전달 제거
- Modify: `Planning/Plans/TankPlan.cs` — directive 전달 제거
- Modify: `Planning/Plans/SupportPlan.cs` — directive 전달 제거
- Modify: `Planning/Plans/OverseerPlan.cs` — directive 전달 제거
- Modify: `GameInterface/TurnEventHandler.cs` — `DirectiveManager.OnCombatEnd()` 제거

**Step 1:** `Core/UserDirective.cs` 파일 삭제

**Step 2:** `Core/TurnState.cs`에서 `CurrentDirective` 프로퍼티 제거
```csharp
// REMOVE:
public UserDirective CurrentDirective { get; set; } = UserDirective.Auto;
```

**Step 3:** `Planning/TurnPlanner.cs`에서 directive 관련 코드 제거
- `DirectiveManager.GetDirective()` 호출 제거
- `turnState.CurrentDirective = directive` 제거
- Hold directive 분기 제거
- `TurnStrategyPlanner.Evaluate()` 호출에서 `directive` 인자 제거

**Step 4:** `Planning/TurnStrategyPlanner.cs`에서 directive 관련 코드 제거
- `Evaluate()`, `EvaluateInternal()` 시그니처에서 `directive` 파라미터 제거
- scoring loop에서 `GetDirectiveWeight()` 호출 제거
- `GetDirectiveWeight()` 메서드 전체 삭제
- `directiveTag` 로깅 제거

**Step 5:** `Planning/Plans/BasePlan.cs`에서 directive 파라미터 제거
- `ShouldRetreat(situation, directive)` → `ShouldRetreat(situation)`
- `EvaluateTacticalOptions(... directive)` → `EvaluateTacticalOptions(...)`
- `ExecuteCommonEarlyPhases(... directive)` → `ExecuteCommonEarlyPhases(...)`
- `PlanEmergencyHeal(... healThresholdOverride)` — healThresholdOverride 파라미터 자체는 유지 (Support role이 아닌 directive가 사용했으므로), 하지만 directive에서만 호출하던 오버라이드 로직 제거

**Step 6:** `DPSPlan.cs`, `TankPlan.cs`, `SupportPlan.cs`, `OverseerPlan.cs`에서 `turnState.CurrentDirective` 전달 제거
- 모든 `ShouldRetreat(situation, turnState.CurrentDirective)` → `ShouldRetreat(situation)`
- 모든 `EvaluateTacticalOptions(..., turnState.CurrentDirective)` → `EvaluateTacticalOptions(...)`
- 모든 `ExecuteCommonEarlyPhases(..., turnState.CurrentDirective)` → `ExecuteCommonEarlyPhases(...)`
- Retreat directive 조기 반환 블록 제거

**Step 7:** `GameInterface/TurnEventHandler.cs`에서 `DirectiveManager.OnCombatEnd()` 호출 제거

**Step 8:** 빌드 확인
```powershell
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo
```
Expected: 빌드 성공 (경고만)

**Step 9:** 커밋
```bash
git add -A && git commit -m "refactor: remove Strategic Directive system (v3.46.0 rollback)"
```

---

## Task 2: 기존 Narrator 시스템 정리

DecisionNarrator, NarrativeBuilder, DecisionHistory를 제거하고, DirectiveOverlayUI를 TacticalOverlayUI로 교체 준비.

**Files:**
- Delete: `Diagnostics/DecisionNarrator.cs`
- Delete: `Diagnostics/NarrativeBuilder.cs`
- Delete: `Diagnostics/DecisionHistory.cs` (존재 시)
- Modify: `Core/TurnOrchestrator.cs` — DecisionNarrator 참조 제거 (이미 dead code이지만 import 정리)
- Modify: `Settings/ModSettings.cs` — narr_* 로컬라이제이션 키 이미 제거됨 확인, EnableDecisionOverlay 설명 업데이트

**Step 1:** `Diagnostics/DecisionNarrator.cs` 삭제

**Step 2:** `Diagnostics/NarrativeBuilder.cs` 삭제

**Step 3:** `Diagnostics/DecisionHistory.cs` 삭제 (존재 시)

**Step 4:** `Core/TurnOrchestrator.cs`에서 `using CompanionAI_v3.Diagnostics;`가 아직 필요한지 확인 (CombatReportCollector 때문에 유지 필요할 수 있음)

**Step 5:** `Settings/ModSettings.cs`에서 `EnableDecisionOverlay` 로컬라이제이션 업데이트:
```csharp
["EnableDecisionOverlay"] = new() {
    { Language.English, "Tactical Narrator" },
    { Language.Korean, "전술 내레이터" },
    { Language.Russian, "Тактический рассказчик" },
    { Language.Japanese, "戦術ナレーター" }
},
["EnableDecisionOverlayDesc"] = new() {
    { Language.English, "[Experimental] Show character narration during combat — battlefield assessment + tactical plan + personality" },
    { Language.Korean, "[실험적 기능] 전투 중 캐릭터별 전장 상황 인식 + 행동 계획 + 개성 대사를 표시합니다" },
    { Language.Russian, "[Экспериментально] Показывать повествование персонажей в бою" },
    { Language.Japanese, "[実験的] 戦闘中にキャラクターの戦術ナレーションを表示" }
},
```

**Step 6:** 빌드 확인

**Step 7:** 커밋
```bash
git add -A && git commit -m "refactor: remove old DecisionNarrator/NarrativeBuilder system"
```

---

## Task 3: TacticalOverlayUI 구현

IMGUI 기반 오버레이 — 캐릭터 이름 + 2~3줄 대사 + 5초 자동 페이드아웃.

**Files:**
- Rewrite: `UI/DecisionOverlayUI.cs` — DirectiveOverlayUI를 TacticalOverlayUI로 완전 교체
- Modify: `Main.cs` — Initialize/Destroy 호출 업데이트

**Step 1:** `UI/DecisionOverlayUI.cs`를 완전 재작성:

```csharp
using UnityEngine;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.UI
{
    public class DecisionOverlayBehaviour : MonoBehaviour
    {
        private void OnGUI() { TacticalOverlayUI.OnGUI(); }
    }

    public static class TacticalOverlayUI
    {
        private static GameObject _overlayGO;
        private static GUIStyle _nameStyle;
        private static GUIStyle _textStyle;
        private static float _lastScale;

        // 현재 표시 중인 내러티브
        private static string _unitName;
        private static string[] _lines;
        private static Color _nameColor = Color.white;
        private static float _showStartTime;
        private static float _duration;
        private static bool _active;

        public static void Initialize() { ... }  // 기존 패턴 유지
        public static void Destroy() { ... }

        public static void Show(string unitName, string[] lines, Color nameColor, float duration = 5f)
        {
            _unitName = unitName;
            _lines = lines;
            _nameColor = nameColor;
            _showStartTime = Time.unscaledTime;
            _duration = duration;
            _active = true;
        }

        public static void Clear() { _active = false; }

        public static void OnGUI()
        {
            if (!_active) return;
            if (!Main.Enabled) return;
            if (ModSettings.Instance?.EnableDecisionOverlay != true) return;

            float elapsed = Time.unscaledTime - _showStartTime;
            if (elapsed >= _duration) { _active = false; return; }

            // 페이드: 마지막 1초
            float alpha = elapsed > _duration - 1f ? (_duration - elapsed) : 1f;

            float scale = Mathf.Clamp(ModSettings.Instance?.DecisionOverlayScale ?? 1f, 0.8f, 2.0f);
            InitStyles(scale);

            // 레이아웃 — 화면 좌측
            float panelX = 20f * scale;
            float panelY = Screen.height * 0.3f;  // 화면 30% 지점
            float lineHeight = 22f * scale;
            float nameHeight = 26f * scale;
            int lineCount = _lines?.Length ?? 0;
            float panelWidth = 500f * scale;
            float panelHeight = nameHeight + (lineCount * lineHeight) + 12f;

            // 반투명 배경
            GUI.color = new Color(0f, 0f, 0f, 0.65f * alpha);
            GUI.Box(new Rect(panelX - 6f, panelY - 4f, panelWidth + 12f, panelHeight), "");

            // 캐릭터 이름
            GUI.color = new Color(_nameColor.r, _nameColor.g, _nameColor.b, alpha);
            GUI.Label(new Rect(panelX, panelY, panelWidth, nameHeight), _unitName, _nameStyle);

            // 대사 줄들
            GUI.color = new Color(1f, 1f, 1f, alpha * 0.9f);
            float y = panelY + nameHeight;
            for (int i = 0; i < lineCount; i++)
            {
                GUI.Label(new Rect(panelX + 8f, y, panelWidth - 16f, lineHeight), _lines[i], _textStyle);
                y += lineHeight;
            }

            GUI.color = Color.white;
        }

        private static void InitStyles(float scale) { ... }  // nameStyle + textStyle
    }
}
```

**Step 2:** `Main.cs` 업데이트:
```csharp
// DirectiveOverlayUI.Initialize() → TacticalOverlayUI.Initialize()
// DirectiveOverlayUI.Destroy() → TacticalOverlayUI.Destroy()
```

**Step 3:** 빌드 확인

**Step 4:** 커밋
```bash
git add -A && git commit -m "feat: TacticalOverlayUI — IMGUI overlay with fade-out"
```

---

## Task 4: TacticalNarrator 핵심 로직

카테고리 결정 + 3-layer 대사 조합 + UI 호출.

**Files:**
- Create: `Diagnostics/TacticalNarrator.cs`

**Step 1:** `Diagnostics/TacticalNarrator.cs` 구현:

```csharp
using Kingmaker.EntitySystem.Entities;
using CompanionAI_v3.Core;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Data;
using CompanionAI_v3.Settings;
using CompanionAI_v3.UI;

namespace CompanionAI_v3.Diagnostics
{
    public enum TacticalSpeechCategory
    {
        Emergency,    // HP 위험, 긴급 힐
        Retreat,      // 후퇴
        KillTarget,   // 처치 가능
        Attack,       // 일반 공격
        AoE,          // 범위 공격
        Support,      // 버프/힐/지원
        EndTurn       // 할 게 없음
    }

    public static class TacticalNarrator
    {
        public static bool IsEnabled =>
            Main.Enabled && (ModSettings.Instance?.EnableDecisionOverlay ?? false);

        public static void Narrate(BaseUnitEntity unit, TurnPlan plan,
            Situation situation, TurnStrategy strategy)
        {
            if (!IsEnabled) return;
            if (unit == null || plan == null || situation == null) return;

            // 1. 카테고리 결정
            var category = DetermineCategory(plan, situation, strategy);

            // 2. 캐릭터 식별
            var companion = CompanionDialogue.IdentifyCompanion(unit);
            var lang = ModSettings.Instance?.UILanguage ?? Language.English;

            // 3. 3-layer 대사 조합
            var lines = TacticalDialogueDB.BuildNarrative(
                category, companion, lang, unit.UniqueId, situation, plan);

            if (lines == null || lines.Length == 0) return;

            // 4. 캐릭터 색상
            Color nameColor = TacticalDialogueDB.GetCompanionColor(companion);

            // 5. 오버레이 표시
            TacticalOverlayUI.Show(unit.CharacterName, lines, nameColor, 5f);

            // 6. CombatReport 기록
            string summary = string.Join(" | ", lines);
            CombatReportCollector.Instance.LogPhase($"[Narrator] {unit.CharacterName}: {summary}");
        }

        public static void OnTurnEnd()
        {
            TacticalOverlayUI.Clear();
        }

        private static TacticalSpeechCategory DetermineCategory(
            TurnPlan plan, Situation situation, TurnStrategy strategy)
        {
            // Priority 기반 (최우선)
            switch (plan.Priority)
            {
                case TurnPriority.Emergency: return TacticalSpeechCategory.Emergency;
                case TurnPriority.Retreat: return TacticalSpeechCategory.Retreat;
                case TurnPriority.EndTurn: return TacticalSpeechCategory.EndTurn;
            }

            // KillTarget 체크
            if (situation.CanKillBestTarget)
                return TacticalSpeechCategory.KillTarget;

            // Strategy 기반 AoE
            if (strategy != null)
            {
                switch (strategy.Sequence)
                {
                    case SequenceType.AoEFocus:
                    case SequenceType.BuffedAoE:
                    case SequenceType.AoERnGChain:
                    case SequenceType.BuffedRnGAoE:
                        return TacticalSpeechCategory.AoE;
                }
            }

            // Support
            if (plan.Priority == TurnPriority.Support ||
                (plan.Priority == TurnPriority.BuffedAttack && !plan.HasAttackActions))
                return TacticalSpeechCategory.Support;

            // Attack (default)
            if (plan.HasAttackActions)
                return TacticalSpeechCategory.Attack;

            return TacticalSpeechCategory.EndTurn;
        }
    }
}
```

**Step 2:** 빌드 확인

**Step 3:** 커밋
```bash
git add -A && git commit -m "feat: TacticalNarrator — category detection + narration pipeline"
```

---

## Task 5: TacticalDialogueDB 구현

JSON 로딩 + 3-layer 대사 선택 + placeholder 치환.

**Files:**
- Create: `Data/TacticalDialogueDB.cs`

**Step 1:** `Data/TacticalDialogueDB.cs` 구현:

핵심 구조:
```csharp
namespace CompanionAI_v3.Data
{
    public static class TacticalDialogueDB
    {
        // JSON 구조: situation/plan/personality 3-layer
        private static TacticalDialogueData _loadedKo, _loadedEn;

        // 하드코딩 fallback (최소 영어)
        private static TacticalDialogueData _fallbackEn;

        // No-repeat tracker
        private static readonly Dictionary<string, int> _lastUsedIndex = new();
        private static readonly System.Random _rng = new();

        public static void LoadFromJson(string modPath) { ... }

        public static string[] BuildNarrative(
            TacticalSpeechCategory category,
            CompanionDialogue.CompanionId companion,
            Language lang, string unitId,
            Situation situation, TurnPlan plan)
        {
            var db = GetDatabase(lang);
            var lines = new List<string>(3);

            // 조합 패턴 랜덤 (3가지 중 하나)
            int pattern = _rng.Next(3);
            // 0: situation + plan + personality
            // 1: situation + personality
            // 2: plan + personality

            string sit = SelectRandom(db.Situation, category, unitId + "_sit");
            string pln = SelectRandom(db.Plan, category, unitId + "_pln");
            string per = SelectPersonality(db, companion, category, unitId);

            // placeholder 치환
            sit = Substitute(sit, situation, plan);
            pln = Substitute(pln, situation, plan);
            per = Substitute(per, situation, plan);

            switch (pattern)
            {
                case 0: AddIfNotNull(lines, sit); AddIfNotNull(lines, pln); break;
                case 1: AddIfNotNull(lines, sit); break;
                case 2: AddIfNotNull(lines, pln); break;
            }
            AddIfNotNull(lines, per);  // personality 항상 포함

            return lines.ToArray();
        }

        public static Color GetCompanionColor(CompanionDialogue.CompanionId companion)
        {
            // 기존 CompanionDialogue.CompanionTextColors 색상 재활용
            ...
        }

        // placeholder: {target}, {ally}, {enemyCount}, {hp}
        private static string Substitute(string text, Situation sit, TurnPlan plan) { ... }
    }

    // JSON deserialization class
    public class TacticalDialogueData
    {
        public Dictionary<string, string[]> Situation { get; set; }
        public Dictionary<string, string[]> Plan { get; set; }
        public Dictionary<string, Dictionary<string, string[]>> Personality { get; set; }
    }
}
```

**Step 2:** 빌드 확인

**Step 3:** 커밋
```bash
git add -A && git commit -m "feat: TacticalDialogueDB — JSON loading + 3-layer dialogue selection"
```

---

## Task 6: TurnOrchestrator 연결

TacticalNarrator.Narrate()를 초기 플랜 생성 직후에 호출.

**Files:**
- Modify: `Core/TurnOrchestrator.cs:257` — TacticalNarrator 호출 추가
- Modify: `Core/TurnOrchestrator.cs` — 턴 종료 시 TacticalNarrator.OnTurnEnd() 호출
- Modify: `GameInterface/TurnEventHandler.cs` — 전투 종료 시 TacticalOverlayUI.Clear()

**Step 1:** `Core/TurnOrchestrator.cs` line 257 근처, 초기 플랜 생성 직후:
```csharp
turnState.Plan = _planner.CreatePlan(situation, turnState);
Data.CompanionDialogue.AnnouncePlan(unit, turnState.Plan);  // 기존 말풍선 유지

// ★ v3.48.0: Tactical Narrator — 초기 플랜만 (리플랜 시 호출하지 않음)
var strategy = turnState.GetContext<TurnStrategy>(
    StrategicContextKeys.TurnStrategyKey, default(TurnStrategy));
Diagnostics.TacticalNarrator.Narrate(unit, turnState.Plan, situation, strategy);
```

**Step 2:** TurnOrchestrator의 턴 종료 처리에서 `TacticalNarrator.OnTurnEnd()` 호출

**Step 3:** `GameInterface/TurnEventHandler.cs` 전투 종료에서 `TacticalOverlayUI.Clear()` 호출

**Step 4:** 빌드 확인

**Step 5:** 커밋
```bash
git add -A && git commit -m "feat: wire TacticalNarrator into TurnOrchestrator"
```

---

## Task 7: 한국어 대사 데이터 작성 (tactical_ko.json)

12명 캐릭터 × 7 카테고리 × 10 personality 변형 + 공통 situation/plan.

**Files:**
- Create: `Dialogue/tactical_ko.json`

**Step 1:** JSON 파일 작성 — 전체 구조:
```json
{
  "situation": {
    "Emergency": ["...", "...", "..."],
    "Retreat": ["...", "...", "..."],
    "KillTarget": ["...", "...", "..."],
    "Attack": ["...", "...", "..."],
    "AoE": ["...", "...", "..."],
    "Support": ["...", "...", "..."],
    "EndTurn": ["...", "..."]
  },
  "plan": { ... },
  "personality": {
    "Abelard": { "Emergency": ["...", ...10개], ... },
    "Heinrix": { ... },
    ...12명 전부
  }
}
```

각 캐릭터의 톤 가이드:
- **Abelard**: 경어, "주군" 호칭, 충직, 의무감
- **Heinrix**: 분석적, 간결, "이단", "위협 평가"
- **Argenta**: "황제 폐하", 성스러운 분노, 정화
- **Pasqal**: "옴니시아", 확률/데이터, 기계적
- **Idira**: 불안, 직감, "워프의 속삭임"
- **Cassia**: 고압적, 귀족 어투, 명령조
- **Yrliet**: 냉소, "mon-keigh", 우월감
- **Jae**: 비즈니스적, "위험 대비 수익", 효율
- **Marazhai**: 잔인한 유머, 고통/쾌감, 조롱
- **Ulfar**: 호탕, "펜리스", 늑대 비유, 직설
- **Kibellah**: 의례적, 죽음/부활, 침착
- **Solomorne**: 법 집행, "렉스 임페리알리스", 규율

**Step 2:** 커밋
```bash
git add Dialogue/tactical_ko.json && git commit -m "content: Korean tactical narrator dialogue (12 companions × 7 categories)"
```

---

## Task 8: 영어 대사 데이터 작성 (tactical_en.json)

한국어와 동일한 구조, 영어 번역.

**Files:**
- Create: `Dialogue/tactical_en.json`

**Step 1:** JSON 파일 작성 — 한국어 대응 영어 번역

**Step 2:** 커밋
```bash
git add Dialogue/tactical_en.json && git commit -m "content: English tactical narrator dialogue"
```

---

## Task 9: ModSettings UI 정리 + JSON 로드 연결

모드 로드 시 JSON 파일 자동 로드 + UI 설명 정리.

**Files:**
- Modify: `Main.cs` — TacticalDialogueDB.LoadFromJson() 호출 추가
- Modify: `UI/MainUI.cs` — Directive 관련 UI 제거 확인, 설명 정리

**Step 1:** `Main.cs`의 `Load()` 메서드에서:
```csharp
TacticalDialogueDB.LoadFromJson(modEntry.Path);
```

**Step 2:** `UI/MainUI.cs`에서 DrawDebugSettings의 EnableDecisionOverlay 설명이 Tactical Narrator 용으로 업데이트되었는지 확인

**Step 3:** 빌드 확인

**Step 4:** 커밋
```bash
git add -A && git commit -m "feat: JSON dialogue loading on mod init + UI description update"
```

---

## Task 10: Info.json 버전 업데이트 + 최종 빌드

**Files:**
- Modify: `Info.json` — Version → 3.48.0

**Step 1:** Info.json 버전 업데이트: `3.46.2` → `3.48.0`

**Step 2:** 전체 빌드 + 실행 테스트 시나리오:
```
1. 모드 로드 → JSON 파일 정상 로드 로그 확인
2. 전투 진입 → AI 턴 시작 시 오버레이 텍스트 표시 확인
3. 5초 후 자동 소멸 확인
4. 턴 종료 시 즉시 소멸 확인
5. 캐릭터별로 다른 색상/대사 확인
6. 모드 설정에서 토글 OFF → 오버레이 미표시 확인
```

**Step 3:** 최종 커밋
```bash
git add -A && git commit -m "v3.48.0: Tactical Narrator v2 — character-driven battlefield narration"
```
