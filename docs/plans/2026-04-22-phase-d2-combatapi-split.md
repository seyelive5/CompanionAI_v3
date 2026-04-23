# Phase D.2 — CombatAPI.cs god-file 분리 Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** `GameInterface/CombatAPI.cs` (6,765줄 / 31 region) 를 C# `partial class` 로 응집도 기반 9 개 파일로 분할. 호출부 0 변경.

**Architecture:** `public static class CombatAPI` → `public static partial class CombatAPI` 로 선언 교체 후, region 단위를 새 partial 파일로 **기계적 이동**. 각 추출 후 MSBuild Rebuild 로 클린 검증. 다 세션 작업, 세션당 1-3 파일.

**Tech Stack:** C# 7.3 / .NET Framework 4.8.1 / SDK-style csproj (자동 include — 파일 추가만으로 컴파일 대상 편입).

---

## 현재 상태 (2026-04-22, HEAD b3d481d)

- 파일: [GameInterface/CombatAPI.cs](../../GameInterface/CombatAPI.cs) — **6,765 줄 / 31 region**
- 클래스: `public static class CombatAPI` (L55-6764)
- 공유 프레임 캐시 필드: L59-65 (GetAvailableAbilities 캐시 + sharedUnitSet/sharedAllySet)
- region-local 필드: 모두 각 region 내부 (cross-region 의존 없음 — 이동 시 함께 이사하면 안전)

## 추출 매핑 (9 partial + 1 residual)

| 순위 | 파일 | 흡수 region (line 범위) | 추정 줄수 | 난이도 |
|:--:|---|---|:--:|:--:|
| 1 | `CombatAPI.VeilPsychic.cs` | Veil & Psychic (L3162-3374) | ~212 | ★ 최소 |
| 2 | `CombatAPI.WeaponSystem.cs` | Weapon & Ammo (L1263-1385) + Weapon Set (L1387-1478) + Range Profile (L1480-1765) | ~502 | ★ |
| 3 | `CombatAPI.TacticalQueries.cs` | AOO (L1120-1261) + Retreat (L3789-3878) + Momentum (L3880-3949) + Resource Prediction (L4175-4284) + SpringAttack (L5603-5666) + Strategist Zones (L5668-5796) | ~592 | ★★ |
| 4 | `CombatAPI.UnitQueries.cs` | Unit State (L770-1118) + Unit Lists (L1767-1850) + Unit Stat (L6497-6553) + Dodge/Parry (L6555-6645) + Archetype (L6684-6763) | ~727 | ★★ |
| 5 | `CombatAPI.AbilityChecks.cs` | Ability Checks (L67-768) + Ability Filtering (L4122-4173) | ~752 | ★★ |
| 6 | `CombatAPI.AbilityDetection.cs` | Ability Type Detection (L3951-4120) + Detection API (L5257-5384) + Classification (L5386-5515) + Damaging AoE (L5798-6078) | ~706 | ★★ |
| 7 | `CombatAPI.TargetingAPI.cs` | Target Scoring + Damage Prediction (L3376-3787) + Targeting Detection (L5517-5601) + Hit Chance (L6272-6495) + Flanking (L6647-6682) | ~731 | ★★★ |
| 8 | `CombatAPI.AoESupport.cs` | AOE Support (L4286-4571) + Self-Targeted AOE (L4573-4710) + Pattern Info Cache (L4712-4793) + Game Pattern API (L4795-5255) | ~969 | ★★★ |
| 9 | `CombatAPI.Abilities.cs` | Abilities (L1852-3160) — 1,308 줄 단일 region (분할 여부는 내용 조사 후 결정) | ~1308 | ★★★★ |
| - | `CombatAPI.cs` (residual) | Unit Conversion (L6080-6270) + shared frame cache 필드 (L59-65) | ~200 | - |

**추출 순위 원칙**: 크기 작고 응집도 높은 것부터 (세션 1 은 의도적으로 쉬운 것부터 시작해서 메커니즘 검증).

---

## Session 1 — VeilPsychic + WeaponSystem 추출

**목표**: partial 분할 메커니즘 검증 + 작은 파일 2 개 추출.

**Pre-flight:**
- `git status` 로 작업 디렉토리 clean 확인
- HEAD `b3d481d` 검증

---

### Task 1: CombatAPI 를 partial class 로 전환

**Files:**
- Modify: `GameInterface/CombatAPI.cs:55`

**Step 1: 클래스 선언에 `partial` 추가**

[GameInterface/CombatAPI.cs:55](../../GameInterface/CombatAPI.cs#L55) 변경:

```csharp
// Before
public static class CombatAPI

// After
public static partial class CombatAPI
```

**Step 2: 빌드 검증**

실행:
```powershell
"C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe" CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo
```

기대: **Build succeeded, 0 Warning, 0 Error**.

**Step 3: 커밋**

```bash
git add GameInterface/CombatAPI.cs
git commit -m "refactor(v3.111.20): CombatAPI → partial class 선언 전환 (Phase D.2 준비)"
```

---

### Task 2: `CombatAPI.VeilPsychic.cs` 추출 (212 줄, 1 region)

**Files:**
- Create: `GameInterface/CombatAPI.VeilPsychic.cs`
- Modify: `GameInterface/CombatAPI.cs` (L3162-3374 삭제)

**Step 1: 원본 범위 확인**

[GameInterface/CombatAPI.cs:3162-3374](../../GameInterface/CombatAPI.cs#L3162-L3374) 읽기.
- 시작: `#region Veil & Psychic - v3.6.0 Enhanced` (L3162)
- 끝: `#endregion` (L3374)
- 내용에서 사용되는 non-기본 namespace 식별 (ability, blueprint, units, buffs 등)

**Step 2: 새 파일 스캐폴딩**

`GameInterface/CombatAPI.VeilPsychic.cs` 생성:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Parts;
using CompanionAI_v3.Settings;
// 실제 region 내용에서 필요한 using 만 포함. 불필요한 것은 IDE/컴파일러 경고로 제거.

namespace CompanionAI_v3.GameInterface
{
    public static partial class CombatAPI
    {
        #region Veil & Psychic - v3.6.0 Enhanced

        // ... 원본 L3163-3373 내용 그대로 복사 ...

        #endregion
    }
}
```

**주의**: 실제 using 은 원본 region 코드에서 사용된 타입에 맞춰 최소 집합만 포함. "모든 using 복붙" 지양.

**Step 3: 원본 파일에서 삭제**

[GameInterface/CombatAPI.cs](../../GameInterface/CombatAPI.cs) 에서 L3162-3374 (빈 줄 포함) 전체 제거.

**Step 4: 빌드 검증**

```powershell
"C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe" CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo
```

기대: **0 Error, 0 Warning**. 경고 발생 시:
- `CS0246` (타입 없음) → using 누락
- `CS0102` (중복 정의) → 원본에서 안 지워짐
- 기타 → 조사 후 수정

**Step 5: region 경계 및 줄수 재확인**

```bash
# 원본 파일 줄수 감소 확인
wc -l GameInterface/CombatAPI.cs
# 기대: ~6553 (6765 - 212)

# 새 파일 검증
wc -l GameInterface/CombatAPI.VeilPsychic.cs
# 기대: ~220 (region 212 + scaffolding ~8)
```

**Step 6: 커밋**

```bash
git add GameInterface/CombatAPI.cs GameInterface/CombatAPI.VeilPsychic.cs
git commit -m "refactor(v3.111.21): Veil & Psychic region → CombatAPI.VeilPsychic.cs 추출"
```

---

### Task 3: `CombatAPI.WeaponSystem.cs` 추출 (~502 줄, 3 연속 region)

**Files:**
- Create: `GameInterface/CombatAPI.WeaponSystem.cs`
- Modify: `GameInterface/CombatAPI.cs` (Task 2 이후 재조정된 line 에서 Weapon 3-region 삭제)

**주의**: Task 2 완료 후 Veil region 이 사라졌으므로 Weapon region 의 line 번호가 **그대로 유지됨** (Weapon 은 Veil 보다 앞 L1263-1765). 재조회 생략 가능.

**Step 1: 원본 범위 확인**

[GameInterface/CombatAPI.cs:1263-1765](../../GameInterface/CombatAPI.cs#L1263-L1765):
- Weapon & Ammo (L1263-1385)
- Weapon Set Management (L1387-1478)  
- Weapon Range Profile (L1480-1765)  — **`_weaponRangeCache` 필드 L1511 포함**

region 간 빈 줄은 1-2 줄. 연속 region 이라 그대로 세 region 모두 이동.

**Step 2: 새 파일 스캐폴딩**

`GameInterface/CombatAPI.WeaponSystem.cs` 생성:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Items;
using Kingmaker.Items.Slots;   // 필요 시
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Parts;
using Kingmaker.GameCommands;   // SwitchHandEquipment (v3.9.72)
using Kingmaker.Designers.Mechanics.Facts;  // WeaponSetChangedTrigger (v3.9.88)
using CompanionAI_v3.Settings;
// 실제 필요한 using 만.

namespace CompanionAI_v3.GameInterface
{
    public static partial class CombatAPI
    {
        #region Weapon & Ammo
        // ... 원본 내용 ...
        #endregion

        #region Weapon Set Management (v3.9.72)
        // ... 원본 내용 ...
        #endregion

        #region Weapon Range Profile (v3.9.24)
        // _weaponRangeCache 포함
        // ... 원본 내용 ...
        #endregion
    }
}
```

**Step 3: 원본 파일에서 삭제**

L1263-1765 전체 제거. region 간 빈 줄 정리.

**Step 4: 빌드 검증 + 줄수 확인 + 커밋**

```powershell
"C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe" CompanionAI_v3.csproj -p:Configuration=Release -t:Rebuild -v:minimal -nologo
```

```bash
wc -l GameInterface/CombatAPI.cs GameInterface/CombatAPI.VeilPsychic.cs GameInterface/CombatAPI.WeaponSystem.cs
# 합계 ≈ 원본 6765 (± scaffolding)
```

```bash
git add GameInterface/CombatAPI.cs GameInterface/CombatAPI.WeaponSystem.cs Info.json
git commit -m "refactor(v3.111.22): Weapon (Ammo/Set/Range) region → CombatAPI.WeaponSystem.cs 추출"
```

---

### Session 1 종료 기준

- [ ] 빌드 0 Error / 0 Warning
- [ ] 3 커밋 (partial 전환 + VeilPsychic + WeaponSystem)
- [ ] 원본 `CombatAPI.cs` 줄수 ≈ 6051 (6765 − 212 − 502)
- [ ] 새 파일 2 개 + 스캐폴딩 합계 ≈ 730 줄
- [ ] 메모리 업데이트: Session 1 완료, 남은 세션 7 개

**Session 1 이후 인게임 실행 검증 불필요** (빌드 클린 = 기계적 이동 성공. 행동 변화 0 기대).

---

## Session 2 — TacticalQueries 추출 (실측 line 기반, HEAD 69c1a4b)

**목표**: `CombatAPI.TacticalQueries.cs` 신설 + 6 region 이동. 4 개 삭제 청크, 총 ~605 줄.

**현재 line 번호** (Session 1 이후 재조회 완료 — AOO 는 -1 shift, 나머지 4 region 은 -719 shift):

| # | region | 현재 line | 줄수 | 청크 |
|:--:|---|---|:--:|:--:|
| 1 | AOO API | L1119-1260 | 142 | 청크 A (단독) |
| 2 | Retreat & Cover System | L3070-3159 | 90 | 청크 B (연속 ②③) |
| 3 | Momentum System | L3161-3230 | 70 | 청크 B (연속 ②③) |
| 4 | Resource Prediction | L3456-3565 | 110 | 청크 C (단독) |
| 5 | SpringAttack (v3.5.22) | L4884-4947 | 64 | 청크 D (연속 ⑤⑥) |
| 6 | Strategist Zones (v3.5.23) | L4949-5077 | 129 | 청크 D (연속 ⑤⑥) |

**청크 4 개 모두 한 커밋에서 이동** (region 간 의존성 없음 확인 시). Order 는 low-to-high line (삭제 순서는 high-to-low 로 해야 위 청크 line 번호가 안 밀림 — **청크 D → C → B → A 역순 삭제**).

**추출 후 예상**:
- `CombatAPI.cs`: 6046 → ~5441 (−605)
- `CombatAPI.TacticalQueries.cs`: ~620 줄 (605 region + ~15 scaffold)

**`private static` 필드 확인 필요**: region 내 사용되는 cross-region 필드 없는지 grep. AOO/Retreat/Momentum/Resource Prediction/SpringAttack/Strategist Zones region 에는 region-local 필드가 없을 것으로 예상 (Session 1 과 달리 WeaponSystem 같은 캐시 없음) — 확인 후 진행.

**난이도**: ★★ (비연속 4 청크 + cross-region 상호 호출 많을 수 있음). 무리라 판단되면 2a(AOO+청크 B) / 2b(청크 C+D) 로 분할.

---

## Session 3 — UnitQueries 추출 (실측 line 기반, HEAD 4484714)

**목표**: `CombatAPI.UnitQueries.cs` 신설 + 5 region 이동. 3 청크, 총 ~663 줄.

**현재 line 번호** (Session 2 이후 재조회 완료):

| # | region | 현재 line | 줄수 | 청크 | 이동할 private static |
|:--:|---|---|:--:|:--:|---|
| 1 | Unit State | L769-1117 | 349 | A (연속 ①②) | `_enemyThreatRangeCache` L842, `_priorityTargetsField` L930, `_priorityTargetsFieldLookupAttempted` L931 |
| 2 | Unit Lists | L1119-1202 | 84 | A (연속 ①②) | — |
| 3 | Unit Stat Query API | L5167-5223 | 57 | B (연속 ③④) | — |
| 4 | Dodge/Parry Estimation | L5225-5315 | 91 | B (연속 ③④) | `EstimateDodgeFromStats` L5257, `EstimateParryFromStats` L5295, `CalculateEffectiveHitChance` L5307 |
| 5 | Archetype Detection API | L5354-5433 | 80 | C (단독) | `_archetypeCache` L5367 |

**삭제 순서 (high-to-low)**: 청크 C → B → A. 청크 A 가 가장 크고 shared-field 밀집도 높음 — 신중.

**주의**:
- Chunk B (L5167-5315) 와 Chunk C (L5354-5433) 사이에 Flanking region (L5317-5352) 이 **잔존** — extraction 대상 아님. 삭제 시 Flanking 경계 보존 필수
- Unit State 의 3 private static 필드 + Archetype 의 1 캐시 = 총 4 필드 이동. **모두 각 region 내부**. cross-region 참조 없음 재확인
- Dodge/Parry 의 3 private static 메서드 = TacticalQueries 의 `EvaluateRetreatPosition` 패턴과 유사. region 내부만 사용 검증 필요

**추출 후 예상**: `CombatAPI.cs`: 5435 → ~4772 (−663), `UnitQueries.cs` ~680 줄.

**난이도**: ★★ (5 region / 3 청크 / 4 필드 + 3 메서드). Session 2 수준. 가장 큰 Unit State 청크가 Reflection / LINQ 등 복잡한 namespace 사용 가능성 높음.

---

## Session 4 — AbilityChecks 추출 (실측 line 기반, HEAD 647ecab)

**목표**: `CombatAPI.AbilityChecks.cs` 신설 + 2 region 이동. 2 청크 비연속, 총 ~754 줄.

**현재 line 번호** (Session 3 이후 재조회 완료):

| # | region | 현재 line | 줄수 | 청크 | 이동할 private static |
|:--:|---|---|:--:|:--:|---|
| 1 | Ability Checks | L59-760 | 702 | A (단독) | `GetRestrictionReason` L180, `GetUnavailabilityReason` L320 |
| 2 | Ability Filtering (Timing-Aware) | L2656-2707 | 52 | B (단독) | — |

**삭제 순서 (high-to-low)**: 청크 B → A.

**주의**:
- **청크 A 는 파일 최상단 region** (L59 = 공유 캐시 필드 L50-57 바로 다음). 삭제 시 상단 공유 필드 (frame cache 5개) 가 잔존 필수
- 청크 A 와 B 사이에 거대한 **Abilities region (L762-2070, 1309줄)** 잔존 — 삭제 순서 엄수 (B 먼저 → A 나중)
- 2 private static method 이동 (`GetRestrictionReason`, `GetUnavailabilityReason`) — 각 Ability Checks 내부. cross-region 참조 재확인 필요

**추출 후 예상**: `CombatAPI.cs`: 4762 → ~4008 (−754), `AbilityChecks.cs` ~770 줄.

**난이도**: ★ (2 비연속, 단순 structure, cache field 0). Session 1 Task 3 (WeaponSystem) 에 근접. Session 3 (UnitQueries) 보다 단순.

---

## Session 5 — AbilityDetection 추출 (실측 line 기반, HEAD e7ab87a)

**목표**: `CombatAPI.AbilityDetection.cs` 신설 + 4 region 이동. 3 청크, 총 ~709 줄.

**현재 line 번호** (Session 4 이후 재조회 완료):

| # | region | 현재 line | 줄수 | 청크 | 이동할 private static |
|:--:|---|---|:--:|:--:|---|
| 1 | Ability Type Detection | L1782-1951 | 170 | A (단독) | — |
| 2 | Ability Type Detection API (v3.5.73) | L2924-3051 | 128 | B (연속 ②③) | `AbilityTypeCache` L2965 |
| 3 | Ability Classification Data (v3.7.73) | L3053-3182 | 130 | B (연속 ②③) | `ClassificationCache` L3056 |
| 4 | Damaging AoE Detection (v3.9.70) | L3270-3550 | 281 | C (단독) | `_damagingAoECache` L3273, `_lastHazardCheckUnit` L3452, `_lastHazardCheckIsPsychic` L3453, `HasDamagingComponents` L3365 method, `ContainsDamageAction` L3428 method |

**삭제 순서 (high-to-low)**: 청크 C → B → A.

**주의**:
- **청크 A 와 청크 B 사이**에 AOE Support/Self-Targeted AOE/Pattern Info Cache/Game Pattern API (L1953-2922) 잔존 (Session 7 예정) — 삭제 순서 엄수
- **청크 B 와 청크 C 사이**에 Targeting Detection (L3184-3268) 잔존 (Session 6 예정) — boundary 보존
- **Chunk B 청크 내부**: Type Detection API (L2924-3051) + Classification Data (L3053-3182) 사이 blank (L3052) — 인접 region. 추출 후 new file 에서 단일 blank 구분자 예상 (Session 3 Unit Stat/Dodge 정밀 패턴)
- **외부 public caller**: `HasPsychicAbilities` → `Analysis/SituationAnalyzer.cs:733` 에서 호출. partial class 유지로 투명. **선행 grep 권장**

**추출 후 예상**: `CombatAPI.cs`: 4006 → ~3297 (−709), `AbilityDetection.cs` ~730 줄.

**난이도**: ★★ (4 region / 3 청크 / 2 캐시 + 2 hazard 필드 + 2 메서드). Session 2 수준.

---

## Session 6 — TargetingAPI 추출 (실측 line 기반, HEAD 74d9ff7)

**목표**: `CombatAPI.TargetingAPI.cs` 신설 + 4 region 이동 (+ nested Accurate Damage Prediction 자동 동반). 3 청크, 총 ~758 줄.

**현재 line 번호** (Session 5 이후 재조회 완료):

| # | region | 현재 line | 줄수 | 청크 | 이동할 private static |
|:--:|---|---|:--:|:--:|---|
| 1 | Target Scoring System (nested: Accurate Damage Prediction L1386-1584) | L1364-1775 | 412 | A (단독, nested sub-region 포함) | 2× `CalculateTargetScore` overloads (L1675, L1747) |
| 2 | Targeting Detection (v3.1.25) | L2748-2832 | 85 | B (단독) | — |
| 3 | Hit Chance API (v3.6.7) | L3026-3249 | 224 | C (연속 ③④) | — |
| 4 | Flanking API (v3.28.0) | L3251-3286 | 36 | C (연속 ③④) | — |

**삭제 순서 (high-to-low)**: 청크 C → B → A.

**주의**:
- **Target Scoring System 은 nested region 구조** — `#region Accurate Damage Prediction` (L1386) 이 `#region Target Scoring System` 내부에 nested. byte-identical 복사 시 자동 동반. new file 내 nested 구조 보존
- **청크 A 와 청크 B 사이**: AOE Support/Self-Targeted AOE/Pattern Info Cache/Game Pattern API (L1777-2746) 잔존 — Session 7 예정
- **청크 B 와 청크 C 사이**: Unit Conversion (L2834-3024) 잔존 — 최종 residual
- **청크 A 앞**: Abilities region (L54-1362) 잔존 — Session 8 예정
- **청크 C 내부**: Hit Chance (L3026-3249) endregion 후 L3250 blank, L3251 Flanking region — 인접. 추출 후 new file 내 단일 blank 구분자 예상 (Session 3 precedent)

**Cross-partial 주의**:
- `CalculateEffectiveHitChance` 는 `UnitQueries.cs` 에 있으나 Hit Chance region 에서 호출 (`CombatAPI.cs:3174/3189` 현재 line). Session 6 후 TargetingAPI 로 이동 → **UnitQueries ↔ TargetingAPI cross-partial** 변경. Session 3 precedent 에 따라 TargetingAPI 내 호출 사이트에 `// Helper: CombatAPI.UnitQueries.cs` 마커 주석 추가 권장

**외부 caller 주의**:
- Target Scoring region 내 public method (예: `CalculateTargetScoreForXxx`) 가 외부에서 호출되는지 전체 트리 grep 필요 (Session 5 의 Hazard Zone 교훈 — public API ~25 사이트 존재 가능성)

**추출 후 예상**: `CombatAPI.cs`: 3288 → ~2530 (−758), `TargetingAPI.cs` ~775 줄.

**난이도**: ★★★ (4 region / 3 청크 / nested region / 2 메서드 이동 / cross-partial marker 주석). Session 5 수준 + nested 구조.

---

## Session 7+ 개요 (후속 세션)

**Session 7**:
- `CombatAPI.AoESupport.cs` (AOE Support + Self-Targeted AOE + Pattern Info Cache + Game Pattern API; `PatternCache` 이동)

**Session 8 (최종)**:
- `CombatAPI.Abilities.cs` (1,308 줄 단일 region — `PreyAbilityGuids` + 2× 헬퍼 메서드 포함) + **shared frame cache 필드 (`_cachedAbilitiesUnitId/Frame/List`, L46-48) 동반 이동 검토**. 내용 스캔 후 추가 분할 가능성 평가.
- 잔존 `CombatAPI.cs` 는 Unit Conversion + `_sharedUnitSet/AllySet` (Pattern counting 전용) 만 남음 (~200 줄).

---

## 원칙

1. **기계적 이동** — 코드 로직 변경 0. 행동 변화 0.
2. **`private static` 필드는 한 파일에만** — region-local 필드는 해당 region 과 함께 이동. 공유 필드 (L59-65) 는 residual 에 유지.
3. **`using` 최소화** — 각 partial 파일은 자체 사용 타입에 필요한 것만. "복붙 후 필요 없는 것 제거" 보다 "원본 region 내 사용 타입 스캔 후 최소 집합" 선호.
4. **빌드 검증 필수** — 각 추출 후 MSBuild Rebuild. Error 0 + Warning 0 확인.
5. **한 번에 한 파일** — 한 커밋 = 한 파일 추출 (+ partial 전환은 별도 커밋).
6. **Session 1 특히 보수적** — 메커니즘이 처음이므로 작은 파일로 검증.

## 금지 사항

- 로직 리팩토링 (DRY/naming 개선 등) — 별도 작업.
- region 내용 재편성 (메서드 순서 변경 등) — 별도 작업.
- using 한꺼번에 정리 — 추출 중 발견된 불필요 using 만 제거.
- 공유 private static 필드를 추출 파일로 이동 — cross-partial reference 로 복잡도 증가.
