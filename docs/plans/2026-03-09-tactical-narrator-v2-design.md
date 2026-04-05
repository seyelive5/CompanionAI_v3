# Tactical Narrator v2 — Design Document

**Date**: 2026-03-09
**Version target**: v3.48.0
**Status**: Approved

---

## 1. Overview

각 동료 캐릭터의 턴 시작 시, 초기 TurnPlan 기반으로 **전장 상황 + 행동 계획 + 캐릭터 개성 대사**를 IMGUI 오버레이에 표시.
캐릭터가 전장을 읽고 자기 생각을 말하는 느낌을 연출.

**핵심 원칙:**
- 턴 시작 시 초기 TurnPlan 생성 직후 **1회만** 표시 (리플랜 무시)
- 게임 흐름을 막지 않음 (자동 소멸, 일시정지 없음)
- 12명 캐릭터 각각의 개성에 맞는 다양한 대사 (반복감 제거)

---

## 2. 3-Layer Text Structure

| Layer | Role | Variants | Scope |
|-------|------|----------|-------|
| **Situation** | 전장 팩트 묘사 | 카테고리당 2~3 | 공통 (캐릭터 무관) |
| **Plan** | AI가 뭘 할 건지 | 카테고리당 2~3 | 공통 (캐릭터 무관) |
| **Personality** | 캐릭터 고유 한마디 | 캐릭터×카테고리당 10 | 캐릭터별 |

- 3줄 고정이 아니라 **2~3줄을 자연스럽게 조합**
- 때로는 상황+개성만, 때로는 계획+개성만 등 유연하게 구성
- **Placeholders**: `{target}`, `{ally}`, `{enemyCount}`, `{hp}`

### Example (Abelard, KillTarget category, Korean)
```
적 3기 중 {target}이 가장 약합니다.          ← Situation
{target}을 이번 턴에 처치하겠습니다.           ← Plan
주군을 위해, 반드시 쓰러뜨리겠습니다.           ← Personality
```

---

## 3. Speech Categories (7)

| Category | TurnPlan Mapping | Trigger |
|----------|-----------------|---------|
| **Emergency** | Priority=Emergency | HP 위험, 긴급 힐 |
| **Retreat** | Priority=Retreat | 원거리 위협 회피 |
| **KillTarget** | CanKillBestTarget=true | 처치 가능 타겟 |
| **Attack** | Priority=DirectAttack/MoveAndAttack | 일반 공격 |
| **AoE** | Strategy.Sequence=AoE계열 | 범위 공격 |
| **Support** | Priority=Support/BuffedAttack, 힐/버프 위주 | 아군 지원 |
| **EndTurn** | Priority=EndTurn | 할 게 없음 |

**Priority order**: Emergency > Retreat > KillTarget > AoE > Support > Attack > EndTurn

---

## 4. Data Volume

- **Situation+Plan** (shared): 7 categories × ~3 variants × 2 types = **~42 lines**
- **Personality** (per-companion): 12 companions × 7 categories × 10 variants = **~840 lines**
- **Total per language**: ~880 lines
- **Languages**: Korean (primary), English (fallback)

---

## 5. Data Storage

### JSON External Files (기존 DialogueLocalization 패턴 재사용)
- `{ModPath}/Dialogue/tactical_ko.json` — 한국어 (primary)
- `{ModPath}/Dialogue/tactical_en.json` — 영어 (fallback)
- 추후: `tactical_ru.json`, `tactical_ja.json`

### JSON Structure
```json
{
  "situation": {
    "Emergency": [
      "아군 {ally}의 체력이 {hp}%까지 떨어졌습니다.",
      "상황이 위급합니다. 부상자가 있습니다.",
      "위험한 상황입니다. 치료가 시급합니다."
    ],
    "KillTarget": [...],
    ...
  },
  "plan": {
    "Emergency": [
      "치료를 최우선으로 합니다.",
      "우선 부상을 치료하겠습니다."
    ],
    ...
  },
  "personality": {
    "Abelard": {
      "Emergency": [
        "주군, 걱정 마십시오. 제가 처리하겠습니다.",
        "부상은 가볍지 않지만, 아직 싸울 수 있습니다.",
        ...  // 10 variants
      ],
      "KillTarget": [...],
      ...
    },
    "Heinrix": { ... },
    ...
  }
}
```

### Loading
- Mod 로드 시 JSON 파일 로드 시도
- 파일 없으면 C# 하드코딩 fallback (최소 영어 기본 대사)
- `DialogueLocalization.ReloadFromJson()` 패턴 재사용 가능

---

## 6. UI Overlay

### Display
- **Position**: 화면 좌측 (기존 DecisionOverlay 영역 재활용)
- **Style**: 반투명 검정 배경 + 캐릭터 이름(캐릭터별 색상) + 텍스트
- **Lines**: 2~3줄 (situation + plan + personality 조합)

### Lifecycle
- **Show**: TurnOrchestrator에서 초기 TurnPlan 생성 직후
- **Duration**: 5초 자동 페이드아웃
- **Clear**: 턴 종료 시 즉시 소멸 (5초보다 빠르면 즉시)
- **Fade**: 마지막 1초에 알파 페이드아웃

### MonoBehaviour
- 기존 `DecisionOverlayBehaviour` 재활용 (OnGUI 매 프레임 호출)

---

## 7. Architecture

```
TurnOrchestrator.ProcessTurn()
  → TurnPlan 생성 직후 (WaitingForPlan phase)
  → TacticalNarrator.Narrate(unit, plan, situation, strategy)
      ├─ TacticalSpeechCategory 결정 (7개 중 1)
      ├─ TacticalDialogueDB.GetLines(category, companion, language)
      │   ├─ Situation line: 랜덤 선택 (no-repeat)
      │   ├─ Plan line: 랜덤 선택 (no-repeat)
      │   └─ Personality line: 랜덤 선택 (no-repeat)
      ├─ Placeholder 치환 ({target}, {ally}, {enemyCount}, {hp})
      ├─ 2~3줄 조합 (조합 패턴도 랜덤)
      └─ TacticalOverlayUI.Show(unitName, lines, companionColor, 5f)

TacticalOverlayUI.OnGUI()
  → _showTime > 0이면 렌더링
  → Time.unscaledTime 기반 타이머
  → 마지막 1초 알파 페이드아웃
```

---

## 8. Companion Character Profiles (대사 톤 가이드)

| Companion | Personality | Tone Keywords |
|-----------|------------|---------------|
| **Abelard** | 충직한 집사관/세네셜 | 경어, 주군 호칭, 헌신, 의무감 |
| **Heinrix** | 냉철한 심문관 | 분석적, 간결, 이단 심판, 위협 평가 |
| **Argenta** | 광신적 전투 수녀 | 황제 찬미, 성스러운 분노, 정화 |
| **Pasqal** | 기계교 테크프리스트 | 옴니시아, 확률 계산, 데이터 분석, 기계적 |
| **Idira** | 불안정한 사이커 | 워프 감지, 불안, 직감, 속삭임 |
| **Cassia** | 귀족 항해사 | 고압적, 귀족 어투, 자신감, 명령조 |
| **Yrliet** | 엘다리 레인저 | 냉소적, mon-keigh 언급, 우월감, 고독 |
| **Jae** | 실용적 거래상 | 비즈니스적, 효율 중시, 위험/보상 계산 |
| **Marazhai** | 쾌락주의 드루카리 | 잔인한 유머, 고통 즐김, 조롱, 전투 쾌감 |
| **Ulfar** | 스페이스 울프 | 호탕, 전사적, 펜리스/늑대 비유, 직설적 |
| **Kibellah** | 죽음 교단 사제 | 의례적, 죽음과 부활, 침착, 신비로운 |
| **Solomorne** | 아비테스 집행관 | 법 집행, 규율, 정의, 임무 수행 |

---

## 9. Systems to Modify/Remove

| System | Action |
|--------|--------|
| `DirectiveOverlayUI` (v3.46.0) | **제거** — 전략 지시 UI 롤백 |
| `UserDirective` + `DirectiveManager` (v3.46.0) | **제거** |
| `TurnStrategyPlanner` directive 가중치 (v3.46.0) | **롤백** |
| `BasePlan/DPSPlan/TankPlan/SupportPlan/OverseerPlan` directive 코드 (v3.46.0) | **롤백** |
| `TurnState.CurrentDirective` (v3.46.0) | **제거** |
| `DecisionNarrator` | **교체** → TacticalNarrator |
| `NarrativeBuilder` | **교체** → TacticalDialogueDB |
| `DecisionOverlayBehaviour` | **재활용** → TacticalOverlayUI 연결 |
| `CompanionDialogue` (말풍선) | **유지** — 독립 시스템 |

---

## 10. New Files

| File | Role |
|------|------|
| `Diagnostics/TacticalNarrator.cs` | 진입점 — category 결정 + 대사 조합 + UI 호출 |
| `Diagnostics/TacticalDialogueDB.cs` | JSON 로드 + 대사 선택 (no-repeat) + placeholder 치환 |
| `UI/TacticalOverlayUI.cs` | IMGUI 렌더링 + 타이머 + 페이드 |
| `Dialogue/tactical_ko.json` | 한국어 대사 데이터 |
| `Dialogue/tactical_en.json` | 영어 대사 데이터 |
