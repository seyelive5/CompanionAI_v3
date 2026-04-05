# UI Redesign — Imperial Dark Tab System

## Overview

MainUI를 탭 기반 레이아웃으로 전면 리디자인. Texture2D 기반 커스텀 스타일링으로 Warhammer 40K 임페리얼 다크 톤 적용.

## Layout

```
┌─────────────────────────────────────────────────────────────┐
│  COMPANION AI                                       v3.x.x │
│  TurnPlanner-based Tactical AI System                       │
├────────┬───────────┬────────┬────────┬────────┬─────────────┤
│ ★ 파티 │ 게임플레이 │  전투  │  성능  │  언어  │   디버그    │
├────────┴───────────┴────────┴────────┴────────┴─────────────┤
│                                                             │
│  (선택된 탭의 콘텐츠)                                         │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

## Color Palette (Imperial Dark)

| Purpose             | Color         | Hex       |
|---------------------|---------------|-----------|
| Background          | Near-black    | `#1A1A1E` |
| Active tab          | Imperial gold | `#8B7332` |
| Inactive tab        | Dark grey     | `#2D2D32` |
| Section box         | Slightly lit  | `#222228` |
| Gold text (titles)  | Bright gold   | `#D4A947` |
| Normal text         | Light grey    | `#C8C8C8` |
| Secondary text      | Mid grey      | `#888888` |
| Active check/value  | Gold          | `#D4A947` |
| Warning/danger      | Red-orange    | `#FF6347` |

## Tabs

1. **Party** (default) — 캐릭터 목록 + 캐릭터별 설정 (역할/거리/무기로테이션/고급)
2. **Gameplay** — AI 대사, 승리 환호, 아군 NPC AI, 함선전투 AI
3. **Combat** — AoE 설정, 무기 로테이션 전역 설정
4. **Performance** — MaxEnemies, MaxPositions, MaxClusters, MaxTiles + 리셋
5. **Language** — 4개 언어 버튼
6. **Debug** — 디버그 로깅, AI 결정 로그, 전투 리포트, Decision Overlay

## Technical Approach

### New File: `UI/UIStyles.cs`
- Static class, `InitOnce()` for one-time Texture2D + GUIStyle creation
- `MakeTex(Color)` helper — 1x1 Texture2D
- All color constants as `static readonly Color`
- Tab styles: `TabActive`, `TabInactive`, `TabHover`
- Section styles: `SectionBox`, `Header`, `SubHeader`, `Description`
- Widget styles: `Checkbox`, `SliderLabel`, `Button`, `DangerButton`

### Modified File: `UI/MainUI.cs`
- `_activeTab` enum: `UITab { Party, Gameplay, Combat, Performance, Language, Debug }`
- `OnGUI()` → `DrawHeader()` → `DrawTabBar()` → `DrawTabContent()`
- Each tab: `DrawPartyTab()`, `DrawGameplayTab()`, etc.
- Existing Draw methods refactored into tab-specific methods
- Remove per-section fold/unfold toggles (tabs replace them)

### Tab System
```csharp
enum UITab { Party, Gameplay, Combat, Performance, Language, Debug }
static UITab _activeTab = UITab.Party;
```

### Texture2D Lifecycle
- Created once in `InitOnce()` (guarded by null check)
- No explicit Destroy needed (lives for mod lifetime)
- ~10 Texture2D instances total (minimal memory)
