# Machine Spirit 컨텍스트 강화 설계

**목표:** Machine Spirit이 게임 상태를 더 풍부하게 인지하고, 토큰 효율적으로 관리하여 소형 로컬 모델(gemma3:4b)에서도 높은 몰입감 제공

**접근법:** 프로액티브 컨텍스트 주입 (Function Calling 없이, 시스템 프롬프트에 게임 데이터 자동 주입)

**대상 프로바이더:** Ollama 위주 (모든 프로바이더에서 동작하나 Ollama 소형 모델 최적화 중심)

---

## 섹션 1: 전투 컨텍스트 강화

### 현재 상태
- 파티 명단 (이름, 아키타입, HP 상태)
- 적 목록 (이름, HP 상태, 최대 10명)
- 센서 로그 (최근 20개 이벤트: 데미지/힐/사망/전투 시작·종료)

### 추가 데이터

| 데이터 | 소스 | 예시 출력 |
|--------|------|-----------|
| 활성 버프/디버프 | `unit.Buffs.RawFacts` | `[BUFFS] Argenta: Shield of Faith, Inspired` |
| 전투 라운드 | `TurnController` | `[ROUND 4]` |
| 전투 흐름 (승세/열세) | 파티 총 HP% vs 적 총 HP% | `[BATTLE MOMENTUM] Favorable — Party 78% / Hostiles 34%` |
| 유닛 위협 상태 | `unit.CombatState` | `Cassia is ENGAGED in melee (threatened by 2 enemies)` |
| 장비 요약 | `unit.Body` | `Argenta: wielding Bolt Pistol + Power Sword` |
| 전투 킬 트래커 | GameEventCollector 확장 | `Kill log: Argenta eliminated 2, Heinrix eliminated 1` |

### 동작 방식
- 전투 중일 때만 전투 전용 컨텍스트 추가
- 토큰 예산 ~300-500 토큰 이내
- `BuildCombatContext()` 메서드 확장

---

## 섹션 2: 탐험 컨텍스트 강화

### 현재 상태
- 현재 위치 이름
- 파티 명단 (이름, 아키타입, HP)
- 센서 로그 (대화, 바크 등)

### 추가 데이터

| 데이터 | 소스 | 예시 출력 |
|--------|------|-----------|
| 파티원 상세 스탯 | `unit.Stats` | `Argenta — BS:58 WS:35 T:40 (Warrior archetype)` |
| 장착 무기 | `unit.Body.PrimaryHand/SecondaryHand` | `wielding Bolt Pistol + Combat Knife` |
| 장착 방어구 | `unit.Body.Armor` | `wearing Carapace Armour` |
| 파티 전체 건강 요약 | HP% 집계 | `[PARTY HEALTH] All crew operational (avg 92%)` |
| 활성 버프 | `unit.Buffs` | `Heinrix: Prescience active` |

### 핵심 원칙
- 간결함 우선 — ~200 토큰
- HP 전원 100%면 한 줄 축약
- 버프 없으면 섹션 생략

---

## 섹션 3: 프롬프트 압축 개선

### 현재 문제
- 요약이 Ollama 전용
- 센서 로그 20개 항상 전체 주입
- gemma3:4b 컨텍스트 윈도우 4096 토큰에서 빡빡

### 개선

| 항목 | 현재 | 개선 |
|------|------|------|
| 클라우드 요약 | 비활성화 | 모든 프로바이더에서 활성화 |
| 대화 윈도우 | 고정 20개 | 모델 크기별 동적 조절 (4b→12개, 12b+→20개) |
| 센서 로그 | 항상 20개 | 우선순위 기반 축약 |
| 게임 컨텍스트 | 항상 전체 | 상황별 선택적 주입 |
| 토큰 예산 | 없음 | 글자 수 기반 추정 + 상한선 |

### 토큰 예산 체계
- 시스템 프롬프트: ~800 토큰 (고정)
- 게임 컨텍스트: ~500 토큰 (상한)
- 대화 요약: ~200 토큰 (상한)
- 대화 히스토리: 나머지
- 총 예산: 모델 컨텍스트의 ~70%
- 초과 시 축소 순서: 센서 로그 → 대화 히스토리 → 컨텍스트

### 토큰 추정
- 정확한 토큰 카운팅 대신 글자 수 기반
- 한글 1자 ≈ 2토큰, 영문 4자 ≈ 1토큰

---

## 수정 파일 (예상)

| 파일 | 변경 내용 |
|------|-----------|
| `MachineSpirit/ContextBuilder.cs` | 주요 변경 — 전투/탐험 컨텍스트 확장, 토큰 예산 시스템 |
| `MachineSpirit/GameEventCollector.cs` | 킬 트래커 추가 |
| `MachineSpirit/MachineSpirit.cs` | 클라우드 요약 활성화, 대화 윈도우 동적 크기 |
| `MachineSpirit/LLMClient.cs` | 클라우드 요약 API 호출 허용 |
