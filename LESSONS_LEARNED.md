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

---

## 16. Canonical 게임 API 먼저 찾기 (v3.111.12 Phase B.1)

### 문제

v3.111.8~10에서 ExtraTurn(임시턴) 감지를 Harmony patch + AP/MP threshold hybrid로 구현.
- v3.111.8: `TurnController.StartUnitTurnInternal` Postfix로 `InterruptionData.AsExtraTurn` 캡처
- v3.111.9: 50% false positive 발견 (GrantedAP/MP가 실제 게임 API 값과 불일치)
- v3.111.10: AP/MP threshold(`AP<=2 && MP<=5`) 추가 hybrid로 false positive 해결
- **결과**: 3번 iteration, Harmony patch + static cache + threshold 편향 잔존

### 원인

게임에 이미 `Initiative.InterruptingOrder` (public property)가 있었고, `TurnController.GetInterruptingOrder`가 squad-aware로 이 값을 사용. 디컴파일 5분 grep으로 찾을 수 있었음.

### 해결 (Phase B.1)

```csharp
// ✅ Canonical API 직접 조회 — Harmony 불필요, threshold 불필요
public static bool IsExtraTurn(BaseUnitEntity unit)
{
    if (unit.IsInSquad) { var s = unit.GetSquadOptional()?.Squad; return s?.Initiative?.InterruptingOrder > 0; }
    return unit.Initiative.InterruptingOrder > 0;
}
```

- Harmony patch 완전 삭제 (ExtraTurnPatch.cs, 106줄)
- threshold bias 완전 제거 (false positive 0%)
- `AP=1, MP=14` 같은 high-resource ExtraTurn도 정확 감지 (hybrid는 false negative였을 케이스)

### 교훈

1. **게임 API 우선**: 모드 구현 전 반드시 디컴파일에서 동일 개념 API 찾기. `private static`이면 로직 mirror, `public`이면 직접 사용.
2. **Harmony는 최후 수단**: API가 없거나 callback이 필요한 경우에만.
3. **threshold보다 boolean 신호**: 게임이 명시적 flag(`InterruptingOrder > 0`)를 쓰면 우리도 그걸 쓰자. AP/MP threshold는 edge case에서 깨짐.

---

## 17. 런타임 로그 증거 없이 "완료" 선언 금지 (v3.111.19 Phase D.4)

### 문제

v3.111.0 Phase 5 ("async enemy move prediction"): 빌드 클린 + 코드 리뷰 통과 → "완료" 선언 → 배포 → 인게임 테스트에서 발견:
- `task.Wait` 데드락으로 **0% 효과** (AI thread가 block)
- 턴당 ~750ms stutter (메인 스레드 대기)
- 빌드 성공 ≠ 작동

v3.111.3에서 `EnemyMoveCache` Harmony 방식으로 재구현하여 실제 완료.

### 원인

"빌드 클린 + 로직 검토"를 "완료"의 충분 조건으로 간주. 실제 동작은 미확인.

### 해결 (Phase D.4)

WORK_TRACKER.md의 "작업 완료 판정 기준"에 6번 항목 추가:

> **런타임 로그 증거 확인**: 기대 동작이 `GameLogFull.txt`에 증거로 찍히는가?
> 예: `[Analyzer] Extra turn CONFIRMED`, `Hide=33.6(F0.93/A0.93)`, `StayAway=0.70(17.6)`
> 빌드 클린 ≠ 실행 증명. 로그에 증거 없으면 완료 아님.

### 교훈

1. **로그 = 증명**: 기능이 "실제로 실행됨"을 로그로 증명. 예상 메시지 미리 정의 후 확인.
2. **배포 ≠ 완료**: 최소 한 번 인게임 세션으로 돌려보고 로그 확인.
3. **선언-현실 gap 방지**: 완료 선언 전 검증 체크리스트 필수.
---

## 18. god-file partial class 분할 방법론 (v3.111.20-30 Phase D.2)

### 배경

`GameInterface/CombatAPI.cs` 6,765줄 / 31 region god-file 을 9개 partial class 파일 + 1 residual 로 분할. 기계적 refactor (행동 변화 0, 코드 로직 유지). 8 세션 / 16 commit 로 완료. 재사용 가능한 방법론.

### 검증된 10대 원칙

1. **Byte-identity 우선 검증** — `diff <(git show <parent>:file | sed -n '<range>p') <new file's region>` → 기대값 0. 대규모 diff 리뷰 scalability 의 유일한 해법.
2. **2-commit-per-session 패턴** — 매 세션: (a) Pre-flight commit — `#region` line 재측정 후 플랜 업데이트, (b) Extraction commit — 실제 이동. 세션 간 line 시프트를 line 측정 스냅샷으로 추적.
3. **Region-local `private static` 동반 이동** — 필드/메서드 모두 region 내부면 함께. 원칙 2 `private static` 필드는 한 파일에만.
4. **Residual header 필드 동반 이동** (Session 7 정립) — 필드 사용이 단일 region 에 100% 국한되면 (grep 선행 확인) 새 partial header 로 이동. 원본 주석 보존 + 이동 annotation 추가.
5. **Cross-partial `private static` 호출 허용** — `partial class` 는 컴파일러에게 단일 클래스. 응집도가 cross-partial "미관 의식" 보다 우선. 호출 사이트에 `// Helper: CombatAPI.<file>.cs` marker 주석 추가 (가독성).
6. **Fully-qualified names > 새 `using`** — one-off Reflection/generic 참조 (e.g. `System.Reflection.FieldInfo`). using 막대하게 늘리지 않음.
7. **MSBuild catch-and-fix cheap** — using 과잉 제거는 rebuild 로 즉시 CS0246 발견. 1-2 iteration 정상 예산. 사전 grep 으로 prevention 가능하나 추론 불필요.
8. **Nested `#region` 단위 이동** — outer 추출 시 inner region 자동 동반 (Session 6 Target Scoring / Accurate Damage Prediction). C# + VS IDE 모두 지원.
9. **연속 region = 단일 chunk** — `#endregion` + blank + `#region` 인접 시 한 sed 범위로 삭제 (Session 2/6/7).
10. **Deletion order high-to-low** — 비연속 region 삭제 시 low line 먼저 삭제하면 high line 번호가 시프트 되어 두 번째 삭제 위치 계산이 깨짐. 항상 뒤에서부터.

### 부차 원칙

- **Orphan using 은 같은 커밋에서 정리** — 별도 cleanup commit 회피 (Session 1 예외는 학습 전).
- **Pre-existing deadwood 발견 시 opportunistic 정리** — 커밋 body 에 audit scope 명시.
- **Marker 규율**: `private static` cross-partial 호출에만 marker. `public static` 호출은 정상 class API — marker 불필요.
- **Byte-identity 기대 delta = marker count** — 의도적 marker 추가 시 정확히 해당 줄수만 +, 나머지 0-diff.

### 통계 (8 세션 결과)

- 원본 6,765줄 → 최종 6,906줄 (9 partial + residual)
- Scaffold overhead: **+141줄 / 2.08%** (~15.7줄/partial 평균)
- 16 commits, 0 MSBuild warning/error, 0 behavior change
- External caller (~25+ 사이트) 0 수정 필요 (partial class 투명)

### 재사용 조건

이 방법론은 다음 경우에 재사용 가능:
- 단일 static class 가 region 으로 구획된 god-file
- 기계적 이동만 원하는 경우 (logic 변경 없이)
- `partial class` 지원 언어 (C#, VB.NET)
- **abstract class** 도 동일하게 적용 가능 (Phase 3 BasePlan.cs 검증 — instance 메서드, protected/virtual 멤버, abstract 멤버 모두 정상)

전체 검증 기록:
- [docs/plans/2026-04-22-phase-d2-combatapi-split.md](docs/plans/2026-04-22-phase-d2-combatapi-split.md) — Phase D.2 (CombatAPI static class)
- [docs/plans/2026-05-03-phase-3-baseplan-split.md](docs/plans/2026-05-03-phase-3-baseplan-split.md) — Phase 3 (BasePlan abstract class)

### Phase 3 추가 검증 사항 (v3.115.0-8, 2026-05-03)

**BasePlan.cs**: 4,396줄 / 14 region → 135줄 residual + 7 partial (총 9 commits 추출, 1 partial 변환).

**static class vs abstract class 차이점**:
- abstract class 의 `protected` instance 메서드는 partial 간 자유 호출 가능 (cross-partial 마커 § 5 동일 적용)
- abstract class 의 `private static` 필드 (`_tempAbilities`/`_tempUnits`/`_plannedBuffGuids`) 는 multi-region 사용 시 residual 유지 (§ 3 동일)
- abstract 멤버 (`RoleName` property) 는 residual 의 `abstract` 키워드 그대로 유지, 모든 partial 에서 자유 참조

**nested region 단위 이동 추가 검증** (§ 8 확장):
- Movement region (L135-L900) 내부에 `#region Phase 0.2: Common Early Phase` (L523-L591) nested 존재
- Movement endregion (L900) 안의 orphan Familiar Phase 공통 메서드 (L592-L899) 도 단일 청크로 동반 추출
- 의미적 재배치는 mechanical refactor 의 범위 밖 — region 경계 그대로 유지

**class close brace 보존 (Phase 3 신규 교훈)**:
- 마지막 region 추출 시 sed `start,endp` 의 end 가 `#endregion + 빈줄 + class close brace` 까지 포함되는 경우 클래스 닫기 brace 가 함께 삭제됨
- **검증 절차**: 추출 후 `tail -5` 로 파일 끝 구조 확인 (`#endregion` → `    }` (class) → `}` (namespace) 순서)
- 누락 시 Edit 으로 복구

**Phase 3 통계** (8 추출 + 1 partial 변환 = 9 commits):
- 원본 4,396줄 → 최종 4,541줄 (residual 135 + 7 partial 4,406)
- Scaffold overhead: **+147줄 / 3.34%** (~21줄/partial 평균, Phase D.2 의 2.08% 보다 약간 높음 — partial 수가 적고 using 다양성이 높아서)
- Lesson 18 §7 catch-and-fix: 평균 **0.5 iteration/세션** (8 추출 중 4번 iteration 발생: Session 5 Pathfinding, Session 6 TargetWrapper×2, Session 8 SC + Designers.Mechanics.Facts)

**`Kingmaker` namespace 발견 사례** (Phase 3 신규 매핑):
- `TargetWrapper` → `Kingmaker.Utility` (직관: `Kingmaker` 또는 `UnitLogic.Mechanics.Actions` 추측 모두 빗나감)
- `WarhammerOverrideAbilityCasterPositionByPet/Contextual` → `Kingmaker.Designers.Mechanics.Facts`
- `PetType` → `Kingmaker.Enums`
- `CustomGridNodeBase` → `Kingmaker.Pathfinding` (AStar `Pathfinding` 과 분리)

---

## 19. 플랜 premise 실증 원칙 (v3.112.0 Phase E)

### 문제

Phase E 플랜(2026-04-24) 이 "게임 내장 3대 API 미활용 — 전면 채택하자" 를 전제로 3 서브 페이즈(E.1 AOE / E.2 위협범위 / E.3 AI 경로) 10 Task 를 설계. 하지만 실행 중 확인:

- `GetAffectedNodes` (v3.5.39, commit `4ee6995`) — `OrientedPatternData` 이미 반환, 6+ callsite 에서 활용 중
- `GetEnemyThreatRangeInTiles` (v3.110.20, commit `079865b`) — `AiCollectedDataStorage[unit].AttackDataCollection.GetThreatRange()` 이미 호출
- `FindAllReachableTilesWithThreatsSync` (v3.8.42, commit `8747255`) — `WarhammerPathAiCell` 이미 반환, 2-슬롯 LRU 캐시까지 완비

**결과**: E.2 전체 + E.3 전체 = 100% 중복 구현. E.1 도 Task 2-3 `GetAffectedUnitsNative` 헬퍼가 중복 → revert 2건 (`2a634b0`, `bd9a16a`). **플랜 10 Task 중 실제 가치 4 Task** (Pilot + Session 3ra/3rb/3rc: 14 callsite 를 이미 있는 `GetAffectedNodes` 로 통합).

### 원인

- **플랜 연구 단계의 문서/에이전트 의존**: Explore 에이전트의 gap_analysis 결과 ("미활용 API") 를 실증 없이 플랜 전제로 채택
- **git 히스토리 미검토**: v3.5.39, v3.8.42, v3.110.20 등 장기간에 걸친 선행 구현을 확인 안 함
- **`SC.UseNativeX` flag 의 함정**: flag 로 A/B 비교는 기존 구현이 없을 때만 의미. 있는데 flag 를 도입하면 순수 중복

### 해결 (Phase E 조기 발견)

1. Task 2-3 혁 실행 후 `GetAffectedNodes` 와 중복 발견 → 즉시 revert
2. E.2 착수 전 `GetEnemyThreatRangeInTiles` 코드 grep → 이미 native 사용 확인 → 메모만 남기고 스킵(`227ff1b`)
3. E.3 착수 전 `FindAllReachableTilesWithThreatsSync` grep → 이미 완성 확인 → 전체 스킵
4. 실제 가치 있는 부분만 추출: 14 callsite 를 `GetAffectedNodes` 로 일관 통합 (Session 3ra/3rb/3rc)

### 30초 실증 체크리스트

플랜 작성/실행 전 **모든 "미활용/미구현" 주장**에 적용:

```
1. 대상 심볼 3-5개 선정 (예: GetAffectedNodes, AiCollectedDataStorage, WarhammerPathAiCell)
2. Grep pattern="<심볼>" output_mode="files_with_matches" 실행
3. 발견 시 Read 로 주석 확인 — "★ vX.Y.Z:" 버전 태그가 프로젝트 관례
4. git log --all -S "<심볼>" --reverse | head -3  — 도입 커밋/시점 확인
5. 이미 있으면 플랜에서 해당 항목 제거 또는 "callsite 통합" 으로 재정의
```

### 교훈

1. **"미활용 API" 는 검증 대상** — 에이전트 gap_analysis 는 참고, 권위 아님
2. **`SC.UseNativeX` flag 제안 = 의심 신호** — 기존 구현 가능성 의심, grep 선행 필수
3. **플랜 실행 시 각 Task 첫 step 에서 재검증** — 플랜 자체가 premise 를 놓쳤을 수 있음
4. **조기 발견 > 플랜 맹신** — Phase E 는 Task 2-3 revert 로 E.2/E.3 까지 선제 스킵, 수십 커밋의 중복 작업 회피
5. **실제 가치는 "신규 구현" 아닌 "callsite 통합"** 일 수도 — 기존 베스트 API 로 일관화는 플랜보다 작지만 진짜 개선

### 재사용 조건

이 원칙은 다음 상황에 반드시 적용:
- 플랜이 "게임 API 채택" / "미활용 전환" / "네이티브 전환" 프레임 사용
- Explore/general-purpose 에이전트의 gap_analysis 기반 플랜
- 프로젝트가 장기 반복 리팩토링 상태 (CompanionAI v3 처럼 200+ 버전)
- flag-based A/B 전환 제안이 포함된 플랜

전체 검증 기록: [docs/plans/2026-04-24-phase-e-game-api-battlefield.md](docs/plans/2026-04-24-phase-e-game-api-battlefield.md)

---

## 20. 정책 필터·검증 게이트는 우회 경로/오분류 전수 점검과 함께 (v3.118.0-6 리뷰)

### 문제

v3.118.0-6 릴리즈 전수 리뷰(2026-07-10, 파인더 10각도 + 적대적 검증 5 + 갭스윕)에서 확정 결함 14건 + PLAUSIBLE 1건 발견 — 대부분이 릴리즈의 수정 3건(Fix E 빈자리AoE / Fix F 자해게이트 / Fix B 근접누수) **자체가 만든 것**. 전체 목록·수정 방향: [docs/reviews/2026-07-10-v3.118-code-review.md](docs/reviews/2026-07-10-v3.118-code-review.md).

반복 패턴 3개:

1. **필터를 수집 지점에만 걸고 raw 재조회 우회 방치** (4건): Fix F/B 필터는 SituationAnalyzer 수집에만 적용 → `FindAnyAttackAbility`(RawFacts 직조회)가 자해 게이트를, `PlanSelfTargetedAoE`("전체에서 다시 찾기" 재수집)·`GetZeroAPAttacks`(파라미터 없음)가 선호 필터를 우회. 필터가 벗겨낸 상태(선호 공격 없음)가 정확히 폴백 진입 조건이라 **게이트가 스스로 우회를 유발**.
2. **게이트 술어가 ActionType+타겟 모양으로 의도 추측** (2건): Fix E의 점유 게이트(`Attack + Entity==null + 포인트`)가 "데미지 AoE"를 의도했으나 — 과잉: Overwatch/Veil(TurnEnding 포인트 시전, 적 0이 정상)을 3중 차단해 기능 사멸. 누락: AllTargets 멀티포인트(AerialRush)는 4중 레이어 전부에서 제외돼 스테일 시전 무검증. **같은 게이트에서 양방향 오류 동시 발생.**
3. **replan이 버리는 상태에 턴 사실 저장** (3건): 플랜-로컬 `hasUsedWarpRelayThisTurn`(replan 후 Cycle 유실), `initialZeroAPAttacks=0` 하드코딩(대피 replan 루프 → 위험지대 턴엔드), 짧은 생성자 9곳(스냅샷 0 → 허위 replan). replan 생존 표면(`TurnState.HasUsedAbility` — 호출자 0, StrategicContext)이 이미 있는데 미사용.

### 원인

- 수정을 "차단 지점 1곳 추가"로 설계하고 **우회 경로 호출 그래프를 그리지 않음**
- 게이트 면제/포함 판정을 기존 타입 시스템(ActionType, TargetWrapper 모양)에 의존 — 의도(적 점유 필요? 스탠스? 멀티포인트?)를 표현하는 명시 플래그 부재
- replan의 "모든 플랜 상태 폐기" 계약을 개별 수정에서 반복적으로 망각

### 규칙 (CLAUDE.md "AI 플래닝 코드 함정" 섹션과 동일 — 신규 코드 즉시 적용)

1. 정책 필터는 CombatAPI 조회 함수 **내부**에. 플래너 raw 재조회 신설 금지.
2. 턴 내 "이미 했음"은 TurnState/StrategicContext에. 플랜-로컬/생성자 인자 금지.
3. TurnPlan 스냅샷 인자 생략·0 하드코딩 금지.
4. 게이트 신설 시 명시 의도 플래그 + 전 factory 과잉차단/누락 양방향 점검.
5. 능력 등록 시 형제 변형(New/Mob/Legacy) 전수 확인 (Extermination Mark New 미등록 사례).
6. 폴백 의미 변경 시 호출부 전수 grep (하드코딩 `PreferRanged` 4곳이 조용히 회귀한 사례).

### 메타 교훈 — 기존 가드 3중 실패

이번 위반 중 3건은 **기존 가드가 이미 커버하는 영역**이었다:

- ★ 마커 "절대 금지" 규칙 존재 → 날짜 스탬프 변종(`★ 2026-07-07:`)이 metrics 정규식(`★\s*v[0-9]`)을 회피
- catch 표준 존재 → `Warn + ex.Message` 변종이 silent-catch 정규식(`.Debug`만)을 회피
- 완료 판정 기준 §2/§3("Replan/폴백 경로 확인") 존재 → 추상 문구는 grep 절차로 번역되지 않음

**규칙은 변종에 약하고, 체크리스트는 추상성에 약하다.** 가드 강도 순서: ① 구조적 제거(필터를 API 안으로 → 규칙 자체가 불필요해짐) > ② 기계 검증(정규식은 변종을 포함하게 광범위하게) > ③ 트리거 조건이 달린 명령형 규칙 > ④ 추상 원칙. 이번 세션에서 ②를 수선(metrics 정규식 확장 + ★ 전수 카운트 추가)했고, ①은 수정 작업(그룹 A)의 목표.

### 재사용 조건

- 정책성 필터/게이트(선호·자해·안전·점유) 추가·변경하는 모든 작업
- NeedsReplan / ActionExecutor에 검증 레이어 추가
- "하드 제약" 시맨틱 도입 (불가능 설정 조합의 무경고 침묵 — PreferRanged+근접전용 사례 — 탐지/경고 필수)
- 릴리즈 리뷰 시: 수정이 만든 신규 차단/필터에 대해 "이 게이트를 우회하는 경로"와 "이 게이트가 잘못 잡는 정상 케이스"를 각각 적대적 질문으로
