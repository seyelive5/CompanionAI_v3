# LESSONS_LEARNED.md

CompanionAI 개발 과정에서 얻은 교훈들. 같은 실수를 반복하지 않기 위한 기록.

---

## 1. 절대 AP로 턴 시작을 감지하지 마라 (v3.0.76)

### 문제
```csharp
// ❌ 잘못된 방법 - AP 기반 턴 시작 감지
if (currentAP >= state.StartingAP) {
    // 새 턴 시작으로 판단
}
```

**왜 실패하는가:**
- `전투 트랜스` 같은 버프가 AP를 증가시킴 (5→6)
- AP 증가를 "새 턴 시작"으로 오인
- TurnState가 반복 재생성 → **무한 루프**

### 해결
```csharp
// ✅ 올바른 방법 - 게임 이벤트 구독
public class TurnEventHandler : ITurnStartHandler, ITurnEndHandler
{
    public void HandleUnitStartTurn(bool isTurnBased) {
        TurnOrchestrator.Instance.OnTurnStart(unit);
    }
}
```

### 게임 턴 API

| API | 용도 |
|-----|------|
| `ITurnStartHandler` | **턴 상태 초기화** |
| `ITurnEndHandler` | 상태 정리 |
| `TurnController.CombatRound` | 현재 전투 라운드 |
| `TurnController.GameRound` | ❌ CombatRound와 혼동 금지 |

---

## 2. AP/MP 소스 규칙 (v3.0.77)

### 단일 진실 소스: `CombatAPI.GetCurrentAP()` / `GetCurrentMP()`

```csharp
// ✅ 올바른 방법
float ap = CombatAPI.GetCurrentAP(unit);
float ap = situation.CurrentAP;

// ❌ 사용 금지 - 레거시
float ap = turnState.RemainingAP;  // 버프 효과 미반영!
```

**왜?**
- **버프 효과**: `전투 트랜스`가 AP 5→6 증가
- **TurnState.RemainingAP**: 턴 시작 시 설정, 이후 업데이트 안 됨

---

## 3. Hittable 계산 규칙 (v3.0.78)

### 문제
```csharp
// ❌ 단일 참조 능력으로 Hittable 계산
var attackAbility = CombatAPI.FindAnyAttackAbility(unit, preference);
// 이 능력이 쿨다운이면 → HittableEnemies = 0 → 공격 스킵!
```

### 해결
```csharp
// ✅ 모든 AvailableAttacks 기준
foreach (var attack in situation.AvailableAttacks)
{
    if (CombatAPI.CanUseAbilityOn(attack, targetWrapper, out _))
    {
        situation.HittableEnemies.Add(enemy);
        break;  // 하나라도 공격 가능하면 Hittable
    }
}
```

---

## 4. RangeFilter 폴백 규칙 (v3.0.79)

### 문제
```
설정: PreferMelee
상황: 일격(근접) 쿨다운, 죽음의 속삭임(원거리) 사용 가능
결과: 원거리 스킬 필터링됨 → Hittable=0 → 턴 종료
```

### 해결
```csharp
// 필터링된 공격으로 못 맞추면 전체 공격으로 재시도
if (situation.HittableEnemies.Count == 0 && allUnfilteredAttacks.Count > filteredAttacks.Count)
{
    // 폴백: 모든 공격으로 재검사
}
```

---

## 5. 능력 Available 체크 (v3.0.94)

### 문제
```csharp
// ❌ IsAvailable만 체크
if (data.IsAvailable)  // 쿨다운 필터링 안 됨!
```

### 해결
```csharp
// ✅ GetUnavailabilityReasons() 사용
var unavailabilityReasons = data.GetUnavailabilityReasons();
if (unavailabilityReasons.Count > 0) continue;  // 쿨다운, 탄약 부족 등
```

| 체크 방식 | 쿨다운 | 탄약 | 충전 |
|----------|--------|------|------|
| `IsAvailable` | ❌ | ❌ | ❌ |
| `GetUnavailabilityReasons()` | ✅ | ✅ | ✅ |

---

## 6. 리소스 회복 예측 (v3.0.98~v3.1.02)

### 문제 체인

**v3.0.98**: MP 회복 능력을 계획하지만 예측 안 함
```csharp
var postAction = PlanPostAction(...);  // 무모한 돌진 계획
// remainingMP는 여전히 0 → Phase 8 이동 계획 안 함
```

**v3.1.00**: `situation.CanMove` 직접 체크
```csharp
if (!situation.CanMove) return null;  // 예측된 remainingMP 무시!
```

**v3.1.01**: MovementAPI가 실제 게임 MP 사용
```csharp
float ap = unit.CombatState?.ActionPointsBlue ?? 0f;  // 게임 MP = 0!
```

**v3.1.02**: AP=0이면 즉시 턴 종료
```csharp
if (gameAP <= 0) return ExecutionResult.EndTurn(...);  // Move는 MP 사용하는데!
```

### 해결
1. Blueprint에서 MP/AP 회복량 직접 읽기
2. `bypassCanMoveCheck` 파라미터 추가
3. `predictedMP` 파라미터 체인 전달
4. AP=0이지만 보류 중인 Move + MP 있으면 계속 진행

---

## 7. 인위적인 제한 금지 (v3.5.25)

### 문제
```csharp
// ❌ 인위적인 숫자 제한
public const int MaxActionsPerTurn = 15;  // 왜 15? 근거 없음
```

**실제 발생한 문제:**
```
Action #15: Move → 적 접근
→ "Max actions reached (15)" 강제 종료
→ 플랜에 남아있던 Attack 실행 못함!
→ AP=2.0, MP=9.0 남아있는데 강제 종료
```

### 해결
```csharp
// ✅ 사실상 무제한 - 게임 메커니즘이 알아서 제한
public const int MaxActionsPerTurn = 9999;
```

**게임의 자연스러운 종료 조건:**
- AP=0 AND 공격/스킬 불가
- MP=0 AND 이동 불필요
- 모든 스킬 쿨다운

---

## 8. 캐시 히트율 검증 (v3.5.31)

### 교훈
LOS 캐시를 구현했지만 **0% 히트율** → 제거

**왜 실패했는가:**
- MovementAPI에서 각 타일 평가 시 같은 노드쌍이 재조회되지 않음
- 한 번 계산된 LOS는 다시 필요하지 않음

**성공한 캐시:**
- 거리 캐시: 94% 히트율 (여러 곳에서 같은 유닛쌍 거리 조회)
- 타겟팅 캐시: 46-82% 히트율 (RangeFilter 폴백 등에서 재조회)

### 교훈
- 캐시 구현 전 실제 조회 패턴 분석 필요
- 구현 후 반드시 히트율 측정

---

## 9. AOE 패턴 및 타일 범위 API (v3.5.76)

### 핵심 API: 정확한 AOE 타일/유닛 감지

추측이나 추정이 아닌 **게임 API를 직접 사용**해야 함.

#### 1. 패턴 설정 가져오기
```csharp
// AbilityData에서 패턴 설정 획득
IAbilityAoEPatternProvider patternProvider = ability.GetPatternSettings();

// null이면 AOE 아님
if (patternProvider == null) return;
```

#### 2. 특정 위치에서 영향받는 노드 계산
```csharp
// 시전자 노드와 타겟 노드 획득
CustomGridNodeBase casterNode = caster.Position.GetNearestNodeXZUnwalkable();
CustomGridNodeBase targetNode = target.Position.GetNearestNodeXZUnwalkable();

// 패턴 데이터 획득 (실제 영향받는 타일 목록)
OrientedPatternData pattern = patternProvider.GetOrientedPattern(
    ability,                    // IAbilityDataProviderForPattern
    casterNode,                 // 시전 위치
    targetNode,                 // 타겟 위치
    coveredTargetsOnly: true);  // 유닛 있는 노드만
```

#### 3. 영향받는 유닛 열거
```csharp
int enemyHitCount = 0;
int allyHitCount = 0;

foreach (CustomGridNodeBase node in pattern.Nodes)
{
    if (node.TryGetUnit(out var unit))
    {
        if (unit.IsEnemy(caster))
            enemyHitCount++;
        else if (unit.IsAlly(caster))
            allyHitCount++;
    }
}
```

### 핵심 클래스 정리

| 클래스 | 역할 | 위치 |
|--------|------|------|
| `IAbilityAoEPatternProvider` | 패턴 설정 인터페이스 | `Abilities\Components\Base` |
| `AoEPattern` | 패턴 정의 (타입, 반경, 각도) | `Abilities\Components\Patterns` |
| `OrientedPatternData` | 실제 영향받는 노드 목록 | `Abilities\Components\Patterns` |
| `PatternGridData` | 상대적 타일 오프셋 | `Pathfinding` |
| `AoEPatternHelper` | 유틸리티 메서드 | `Abilities\Components\Patterns` |

### 패턴 타입 (PatternType)

```csharp
public enum PatternType
{
    Circle,   // 원형 폭발 (중심 기준)
    Cone,     // 전방 콘 (90-180도)
    Ray,      // 직선
    Sector,   // 회전 가능 부채꼴 (0-360도)
    Custom    // 블루프린트 정의 커스텀
}
```

### 속성 접근

```csharp
AoEPattern pattern = patternProvider.Pattern;

int radius = pattern.Radius;        // 반경 (셀 단위)
int angle = pattern.Angle;          // 각도 (Cone/Sector용)
PatternType type = pattern.Type;    // 패턴 타입
bool directional = pattern.CanBeDirectional;  // 방향성 여부
```

### 주의사항

1. **추측 금지**: 반경 5m = 몇 타일? → **게임 API가 직접 계산**
2. **LOS 고려**: `GetOrientedPattern`이 시야 차단 자동 처리
3. **높이 차이**: `IsIgnoreLevelDifference` 속성 확인
4. **타겟 타입**: `patternProvider.Targets` (Enemy, Ally, Any)

---

## 10. 거리/범위 단위 일관성 - 타일 기준 (v3.5.98)

### 문제: 미터와 타일 혼용으로 인한 버그

```csharp
// ❌ 단위 혼용 - 버그!
float effectiveRange = CombatAPI.GetAbilityRange(attack) + patternInfo.Radius;
//                      ↑ 미터                           ↑ 타일
float dist = Vector3.Distance(...);  // 미터
if (dist > aoERadius)  // aoERadius는 타일!
```

**발생한 문제:**
- "눈꺼풀 없는 응시" AOE 스킬이 범위 밖 적에게 사용됨
- 클러스터 감지가 부정확함
- AOE 안전성 체크가 잘못된 범위로 계산됨

### 해결: 타일 기준 통일

**변환 상수**: `1 타일 = 1.35 미터` (GraphParamsMechanicsCache.GridCellSize)

```csharp
// ✅ 올바른 방법: 모두 타일 단위로 통일

// 1. 거리 계산 시 타일로 변환
float dist = CombatAPI.MetersToTiles(Vector3.Distance(a, b));  // 타일

// 2. 능력 사거리는 게임 API 사용
int range = CombatAPI.GetAbilityRangeInTiles(ability);  // 타일 (ability.RangeCells)

// 3. AOE 반경은 이미 타일
float aoERadius = CombatAPI.GetAoERadius(ability);  // 타일
float patternRadius = patternInfo.Radius;            // 타일

// 4. 비교는 같은 단위끼리
if (dist > aoERadius) continue;  // 둘 다 타일!
```

### API 단위 정리

| API | 반환 단위 | 용도 |
|-----|----------|------|
| `CombatAPI.GetDistanceInTiles()` | 타일 | 모든 거리 비교에 사용 |
| `CombatAPI.GetAbilityRangeInTiles()` | 타일 | 능력 사거리 |
| `CombatAPI.GetAoERadius()` | 타일 | AOE 반경 |
| `patternInfo.Radius` | 타일 | 패턴 반경 |
| `ability.RangeCells` | 타일 | 게임 공식 API |
| `Vector3.Distance()` | 미터 | ⚠️ `MetersToTiles()`로 변환 필요 |
| `CombatAPI.GetAbilityRange()` | 미터 | ⚠️ 레거시, 가급적 사용 자제 |

### 유틸리티 함수 (v3.5.98 추가)

```csharp
public const float GridCellSize = 1.35f;  // 1 타일 = 1.35 미터

public static float MetersToTiles(float meters) => meters / GridCellSize;
public static float TilesToMeters(float tiles) => tiles * GridCellSize;

public static float GetDistanceInTiles(BaseUnitEntity a, BaseUnitEntity b)
{
    return Vector3.Distance(a.Position, b.Position) / GridCellSize;
}

public static int GetAbilityRangeInTiles(AbilityData ability)
{
    return ability.RangeCells;  // 게임 공식 API
}
```

### 교훈

1. **타일 기반 게임은 타일 단위가 기본** - 미터는 내부 변환용
2. **Unity 미터 거리는 항상 변환**: `/ 1.35f` 또는 `MetersToTiles()`
3. **게임 API를 적극 활용**: `ability.RangeCells`는 이미 타일 단위
4. **주석으로 단위 명시**: 파라미터에 `// 타일` 주석 추가
5. **혼용 의심 시 즉시 확인**: 버그의 원인이 단위 불일치일 가능성 높음

---

## 11. AOE 패턴 타입 검증 필수 (v3.6.9)

### 문제

**"AOE니까 Circle이겠지"라는 가정으로 버그 발생**

```
상황: 영혼 소각(Immolate the Soul) - 직선 레이저 형태
로그: "AOE r=10 tiles" → Circle로 오인
실제: Ray 패턴 (Directional) - 로그에 "(Directional)" 표시됨!
```

**발생한 버그:**
- Cassia가 높은 곳에서 아래 적에게 영혼 소각 시전
- Circle이면 1.6m 높이 차이까지 허용
- **실제 Ray는 0.3m 높이 차이만 허용**
- 높이 차이 4m → 스킬이 아무 효과 없음

### AOE 패턴별 높이 제한 (AoEPattern.cs)

| 패턴 타입 | 높이 제한 | 특징 |
|----------|----------|------|
| **Circle** | 1.6m | 원형 폭발, 중심 기준 |
| **Cone** | 0.3m | 전방 콘 (Directional) |
| **Ray** | 0.3m | 직선 레이저 (Directional) |
| **Sector** | 0.3m | 부채꼴 (Directional) |

```csharp
// 게임 소스 (AoEPattern.cs)
public const float SameLevelDiff = 1.6f;     // Circle
public const float RayConeThickness = 0.3f;  // Ray, Cone, Sector (Directional)
```

### 검증 방법

```csharp
// ✅ 게임 API로 정확한 패턴 타입 확인
var patternType = CombatAPI.GetPatternType(ability);

if (patternType.HasValue)
{
    bool isDirectional = patternType == PatternType.Cone
                      || patternType == PatternType.Ray
                      || patternType == PatternType.Sector;

    float threshold = isDirectional ? 0.3f : 1.6f;
}
```

```csharp
// ❌ 추측 금지
if (CombatAPI.GetAoERadius(ability) > 0)
{
    // "반경 있으니까 Circle이겠지" - 틀림!
    // Ray도 반경(길이) 값을 가짐
}
```

### 패턴 타입별 외형

| 타입 | 인게임 외형 | 흔히 혼동되는 것 |
|------|------------|----------------|
| Circle | 원형 범위 표시 | - |
| **Ray** | 직선 레이저/빔 | "긴 범위니까 Circle?" ❌ |
| Cone | 전방 부채꼴 | - |
| Sector | 회전 가능한 부채꼴 | Cone과 유사 |

### 교훈

1. **AOE 패턴은 다양하다** - Circle만 있는 게 아님
2. **로그를 정확히 읽어라** - `(Directional)` 표시는 Ray/Cone/Sector
3. **게임 API로 검증** - `GetPatternType()` 사용
4. **높이 제한은 패턴별로 다름** - Circle(1.6m) vs Directional(0.3m)
5. **추측 금지** - "AOE니까 Circle" 같은 가정하지 말 것

---

## 12. IsAvailable vs GetUnavailabilityReasons() 불일치 (v3.6.20)

### 문제

```csharp
// ❌ 불일치 문제 발생
var reasons = ability.GetUnavailabilityReasons();
if (reasons.Count == 0) {
    // "사용 가능!"이라고 판단
}

// 하지만...
if (!data.IsAvailable) {
    // "사용 불가!"
}
```

**실제 로그:**
```
[CombatAPI] Filtered out 일반 공격: IsAvailable=false (no explicit reason)
[CombatAPI] Analyzing 0 abilities ← 모든 능력이 필터링됨!
```

### 원인 분석 (게임 소스: AbilityData.cs)

```csharp
// GetUnavailabilityReasons()가 체크하는 것:
// - 쿨다운 (IsOnCooldown)
// - 탄약 (HasEnoughAmmo)
// - 지역 제한 (BlueprintAbilityAreaEffect)

// IsAvailable이 **추가로** 체크하는 것:
public bool IsAvailable {
    get {
        if (GetAvailableForCastCount() != 0
            && HasEnoughActionPoint
            && HasEnoughAmmo
            && !IsRestricted)  // ★ 이것!
        {
            if (IsOnCooldown) return IsBonusUsage;
            return true;
        }
        return false;
    }
}
```

| 체크 항목 | GetUnavailabilityReasons() | IsAvailable |
|----------|---------------------------|-------------|
| 쿨다운 | ✅ | ✅ |
| 탄약 | ✅ | ✅ |
| 지역 제한 | ✅ | ✅ |
| **IsRestricted** | ❌ | ✅ |
| **GetAvailableForCastCount()** | ❌ | ✅ |
| **HasEnoughActionPoint** | ❌ | ✅ |

### 해결

```csharp
// ✅ 올바른 방법: 통합 함수 사용
public static bool IsAbilityAvailable(AbilityData data, out List<string> reasons)
{
    reasons = new List<string>();

    // 1. GetUnavailabilityReasons() 체크
    var unavailReasons = data.GetUnavailabilityReasons();
    if (unavailReasons.Count > 0) {
        reasons.AddRange(unavailReasons.Select(r => r.ToString()));
        return false;
    }

    // 2. 추가 체크 (IsRestricted 등)
    // 게임의 IsAvailable 로직을 따름
    ...
}
```

### 교훈

1. **게임 API 두 개가 같은 걸 체크한다고 가정하지 말 것**
2. **"no explicit reason"이면 숨겨진 조건이 있다는 뜻**
3. **디컴파일 소스로 실제 로직 검증 필수**

---

## 13. 명령 완료 대기 타임아웃 (v3.6.21)

### 문제

```csharp
// 기존: 2초 타임아웃
public const int COMMAND_WAIT_TIMEOUT_FRAMES = 120;  // 2초 @ 60fps
```

**발생한 문제:**
```
상황: AI가 사이킥 폭풍 사용 (긴 애니메이션 3초)
2초 시점: "Wait timeout" → 강제 턴 종료
결과: AP가 남아있는데 턴이 끝남
```

### 스킬별 예상 애니메이션 시간

| 스킬 유형 | 예상 시간 | 2초 타임아웃 |
|----------|----------|-------------|
| 일반 공격 | 0.3~0.5초 | ✅ |
| 연사 (Burst) | 1.0~1.5초 | ✅ |
| 다중 타격 AOE | 2.0~3.0초 | ⚠️ |
| 사이킥 연출 | 2.0~4.0초 | ❌ |
| 넉백 + 낙하 | 2.5~3.5초 | ❌ |

### 해결

```csharp
// ✅ 30초로 증가 - 어떤 애니메이션도 충분히 완료
public const int COMMAND_WAIT_TIMEOUT_FRAMES = 1800;  // 30초 @ 60fps
```

### 타임아웃의 목적

```
타임아웃 = 무한 대기 방지용 안전장치

정상 상황:
  명령 실행 → 애니메이션 → Commands.Empty = true → 다음 액션

버그 상황 (타임아웃 필요):
  명령 실행 → 게임 버그로 완료 신호 안옴 → 무한 대기
  → 30초 후 강제 턴 종료 → 다음 유닛 진행
```

### 교훈

1. **타임아웃은 정상 동작이 아닌 예외 처리용**
2. **가장 긴 애니메이션보다 충분히 길게 설정**
3. **너무 짧으면 정상 동작을 방해함**

---

## 14. 무기 세트 - 비활성 세트 능력 접근 불가 (v3.7.00 계획)

### 핵심 발견

**비활성 무기 세트의 능력은 AbilityCollection에서 완전히 제거됨!**

```
세트 0 활성 시:
  unit.Abilities.RawFacts = [볼터 공격, 볼터 AOE, 클래스 능력들...]

세트 1로 전환 후:
  unit.Abilities.RawFacts = [화염방사기 공격, 화염방사기 AOE, 클래스 능력들...]

→ 볼터 능력들이 완전히 사라짐!
```

### 게임 메커니즘 (ItemEntityWeapon.cs)

```csharp
// 무기 장착 시
public void ReapplyAbilitiesImpl()
{
    // 무기가 부여하는 능력들을 AbilityCollection에 추가
    foreach (var ability in weapon.GrantedAbilities)
        unit.Abilities.Add(ability);
}

// 무기 해제 시
// → ReapplyAbilitiesImpl()가 반대로 능력 제거
```

### 영향

```csharp
// ❌ 다른 세트 능력 접근 불가
var abilities = CombatAPI.GetAvailableAbilities(unit);
// → 현재 세트 능력만 반환됨

// ❌ 다른 세트 무기 정보도 능력에서 접근 불가
var weapon = ability.Weapon;
// → 현재 세트 무기만
```

### 해결 방안 (무기 세트 로테이션 구현 시)

```csharp
// ✅ 임시 전환으로 양쪽 세트 능력 캐시
int originalSet = unit.Body.CurrentHandEquipmentSetIndex;

try
{
    // 세트 0 능력 수집
    unit.Body.CurrentHandEquipmentSetIndex = 0;
    var set0Abilities = GetAvailableAbilities(unit);

    // 세트 1 능력 수집
    unit.Body.CurrentHandEquipmentSetIndex = 1;
    var set1Abilities = GetAvailableAbilities(unit);
}
finally
{
    // 원래 세트 복원
    unit.Body.CurrentHandEquipmentSetIndex = originalSet;
}
```

### 무기 세트 전환 비용

| 항목 | 값 |
|-----|---|
| AP 비용 | **0** (무료) |
| MP 비용 | **0** |
| 쿨다운 | 없음 |
| 제한 | 인덱스 0-1만 |

### 교훈

1. **게임의 AbilityCollection은 현재 장비 기준**
2. **다른 세트 정보는 직접 접근 필요** (Body.HandsEquipmentSets[])
3. **임시 전환은 안전** - 게임 커맨드 큐 사용 안하면 UI 영향 없음
4. **계획(Planning) 단계에서 캐시 필수**

---

## 15. 명중률 계산 시스템 (RuleCalculateHitChances)

### 핵심 공식

```
명중률 = (사격술 + 30) × 거리계수 + 보정치들
```

### 거리 계수 (RuleCalculateAbilityDistanceFactor.cs)

```csharp
float distance = 현재 거리;
float maxRange = 무기 최대 사거리;

if (distance <= maxRange / 2)
    Result = 1.0f;      // 유효 사거리: 100%
else if (distance <= maxRange)
    Result = 0.5f;      // 장거리: 50%
else
    Result = 0.0f;      // 사거리 초과: 자동 빗나감
```

### 실전 계산 예시

```
아이들풀 (BS 55) → 적 (20칸 거리) / 무기 사거리 24칸

1. 거리 판정: 20 > 24/2(12) → 거리계수 = 0.5
2. 기본 명중률: (55 + 30) × 0.5 = 42.5%
3. 무기 보정: +10% (정밀 조준기)
4. 엄폐 페널티: -15% (반엄폐)
5. 최종: 42.5 + 10 - 15 = 37.5%

만약 10칸으로 이동하면:
1. 거리계수 = 1.0 (유효 사거리)
2. 기본: (55 + 30) × 1.0 = 85%
3. 보정: +10 - 15 = -5%
4. 최종: 80% (약 2배 향상!)
```

### 특수 케이스

| 공격 유형 | 명중률 | 이유 |
|----------|-------|------|
| 근접 (Melee) | 100% | 회피는 별도 WS vs WS 판정 |
| 산탄 (Scatter) | 100% | 넓은 범위 → 자동 명중 |
| 파괴물 | 100% | 배럴, 장애물 등 |

### 명중률 상한 (HitChanceOverkillBorder)

```csharp
// 통상 95%가 상한
ResultHitChance = Mathf.Clamp(RawResult, 0, hitChanceOverkillBorder);

// 95% 초과분은 크리티컬 보너스로 전환
if (RawResult > 95)
{
    int overkill = RawResult - 95;
    RighteousFuryChanceRule.Add(overkill);  // 크리티컬 확률 증가
}
```

### AI 활용

```csharp
// 이동 위치 평가 시 명중률 고려
float hitChanceBonus = CalculateHitChanceBonus(unit, position, ability);

// 유효 사거리(거리계수 1.0) 내로 이동하면 명중률 2배!
// → 이동 점수에 반영하여 공격 위치 최적화
```

### 교훈

1. **거리계수가 핵심** - 유효 사거리 내 이동이 매우 중요
2. **Scatter/Melee는 명중률 계산 불필요** - 항상 100%
3. **95% 상한 존재** - 초과분은 크리티컬로 전환
4. **위치 평가 시 명중률 고려 필수**
