# Machine Spirit — 실시간 동반자 설계 (v3.66.0)

## 목표

Machine Spirit이 "지금 내 게임을 보고 있다"는 느낌을 강화.
비전투 상황(탐험, NPC 대화, 게임 시작)에서의 반응성 개선 + 메시지 시각 구분.

## 기능 4가지

### 1. 게임 시작 인사

- **트리거:** `MachineSpirit.Initialize()` → 최초 1회, 채팅 히스토리 로드 후
- **동작:**
  1. 채팅창 자동 열기 (`ChatWindow.SetVisible(true)`)
  2. "Cogitating..." 표시 (thinking indicator)
  3. LLM 호출 — 전용 프롬프트: "함선 시스템이 재가동되었다. 로드 캡틴에게 짧게 인사하라. (1-2문장)"
  4. 응답 도착 → 채팅 히스토리에 추가, thinking 해제
- **지연 대응:** 채팅창이 열리면서 "Cogitating..."이 보이므로 사용자는 Machine Spirit이 "깨어나는 중"으로 인식
- **타임아웃:** 60초 — 실패 시 조용히 무시 (에러 표시 안 함)
- **제약:** 세션 당 1회 (플래그), `Config.Enabled == true`일 때만

### 2. 지역 전환 반응

- **트리거:** `IAreaHandler.OnAreaDidLoad` EventBus 구독
- **동작:**
  1. 새 지역 이름 감지 (`Game.Instance.CurrentlyLoadedArea.AreaDisplayName`)
  2. 이전 지역과 비교 — 같으면 무시 (세이브 로드 등 중복 방지)
  3. `GameEventCollector`에 `AreaTransition` 이벤트 추가
  4. LLM 호출 — 전용 프롬프트: "함선 센서가 새 구역 진입을 감지했다. [지역명]에 대해 짧게 코멘트하라. (1-2문장)"
- **쿨다운:** 별도 30초
- **전투 중:** 스킵
- **새 GameEventType:** `AreaTransition`
- **센서 로그 포맷:** `Navigation — Entered [area name]`

### 3. 메시지 색상 구분

| 카테고리 | 트리거 소스 | 색상 |
|---------|-----------|------|
| Default | 사용자 질문 응답 | 밝은 회색 (현재 기본) |
| Combat | CombatStart, CombatEnd, UnitDeath, 대량 피해 | 빨강 `#FF6666` |
| Scan | AreaTransition, Idle 관측 | 청록 `#66CCCC` |
| Vox | Dialogue 반응 | 노랑 `#CCCC66` |
| Greeting | 세션 시작 인사 | 금색 (`UIStyles.Gold`) |

- **구현:** `ChatMessage`에 `MessageCategory` enum 필드 추가
- **렌더링:** `ChatWindow`에서 어시스턴트 메시지 색상을 카테고리별 매핑
- **사용자 메시지:** 색상 변경 없음
- **저장 호환:** 기존 `chat_history.json`에서 `Category` 미존재 시 `Default`로 역직렬화

### 4. NPC 대화 반응 (기 구현, 문서화)

- **현재 구현 상태** (v3.66.0 세션에서 코드 완료):
  - `GameEventCollector`에서 `Dialogue` 이벤트 → `MachineSpirit.OnDialogueEvent()` 트리거
  - 전용 30초 쿨다운 (`DIALOGUE_COOLDOWN`)
  - `[SKIP]` 메커니즘 — LLM이 무의미한 대화 자동 무시
  - 전용 프롬프트: "코기테이터가 대화를 가로챘다. 의견을 말하라"
  - 색상 카테고리: `Vox` (노랑 `#CCCC66`)
- **추가 변경 없음** — 문서 포함만

## 변경 파일

| 파일 | 변경 내용 |
|------|----------|
| `GameEventCollector.cs` | `AreaTransition` 이벤트 타입 + `IAreaHandler` 구독 |
| `MachineSpirit.cs` | `OnGreeting()`, `OnAreaTransition()` 추가 + `Initialize`에서 인사 트리거 |
| `ContextBuilder.cs` | `BuildForGreeting()`, `BuildForAreaTransition()` 프롬프트 (5개 언어) |
| `ChatWindow.cs` | `SetVisible()`, 카테고리별 색상 렌더링 |
| `ChatMessage` 구조체 | `MessageCategory` enum + `Category` 필드 |

## 포함하지 않는 것 (YAGNI)

- 기분(Mood) 시스템
- 전투 디브리핑
- 통계 누적/MVP
- 토스트 알림
- 메시지 태그 텍스트
- 대화 중 상태 인식/종합 코멘트
