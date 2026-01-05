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
