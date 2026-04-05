# Machine Spirit v3.60.0 — Idle Commentary + Vision + Personality + Persistence

## Summary

Machine Spirit 대규모 기능 확장. Ollama 무제한 특성을 활용한 자율 발화, Gemma 3 비전 통합,
성격 프리셋, 지역 인식, 대화 영속성.

---

## 1. Idle Ambient Commentary (아이들 수다)

### 개요
탐색 중 Machine Spirit이 자율적으로 발화. 유저 설정 가능 빈도.

### 타이머 로직
```
마지막 활동(유저 메시지 or 이벤트 발화) 이후 경과 시간 추적
  → 텍스트 아이들 임계값 → 텍스트 수다 (파티 상태, 지역, 최근 이벤트 기반)
  → 비전 아이들 임계값 → 스크린샷 → Gemma 3 비전 코멘트
  → 전투 중 비활성화 (기존 spontaneous 활용)
```

### 빈도 설정

| Level | 텍스트 간격 | 비전 간격 |
|-------|-----------|----------|
| Off   | 비활성화   | 비활성화  |
| Low   | 5분       | 15분     |
| Medium| 3분       | 8분      |
| High  | 1.5분     | 5분      |

### 스마트 필터링
LLM에 "흥미로운 것이 없으면 [SKIP] 응답" 지시.
[SKIP] 수신 시 무시, 타이머만 리셋.

---

## 2. Vision Integration (Ollama 전용)

### 기술 흐름
```
ScreenCapture.CaptureScreenshotAsTexture2D()
  → 512x384 리사이즈
  → ImageConversion.EncodeToPNG() → Convert.ToBase64String()
  → Ollama /api/chat message.images 필드
  → 응답 텍스트만 보존, Texture2D 즉시 Destroy()
  → 센서 로그에 "Pict-capture — {1줄 요약}"
```

### 메모리 관리
- Texture2D: 요청 직후 Destroy()
- base64 string: 요청 완료 후 참조 해제
- 대화 기록에 응답 텍스트만 저장, 이미지 미저장
- Ollama 전용 — 클라우드 프로바이더 시 비전 자동 비활성화

### LLMClient 변경
- ChatMessage에 optional `List<string> Images` 필드 추가
- SendOllamaStreaming()에서 images 포함
- StreamHandler 변경 불필요 (응답 포맷 동일)

---

## 3. Personality Presets (성격 프리셋)

### 프리셋 목록

| 프리셋 | 컨셉 | 톤 |
|--------|------|-----|
| Sardonic (기본) | 냉소적 건조한 유머 | 비꼬는 충성, 노함선 정령 |
| Mechanicus | 옴니시아 숭배 기술 사제 | 경건하고 기술적 |
| Tactical | 전투 참모 / 분석관 | 간결 실용적, 데이터 중심 |
| Ancient | 태고의 함선 의지 | 시적이고 신비로운 |

### 구현
- `PersonalityType` enum
- `MachineSpiritConfig.Personality` 프로퍼티
- ContextBuilder.GetSystemPrompt() → 성격별 프롬프트 반환
- 4언어 x 4성격 = 16 프롬프트
- 핵심 규칙(캐릭터 롤플레이 금지 등) 모든 프리셋 공통

---

## 4. Area Awareness (지역 인식)

### 게임 API
```csharp
string areaName = Game.Instance.CurrentlyLoadedArea?.AreaDisplayName ?? "Unknown";
```

### ContextBuilder 통합
센서 데이터에 `[CURRENT LOCATION]\nArea: {areaName}` 추가.
아이들 수다 트리거의 힌트로 활용.

---

## 5. Conversation Persistence (대화 영속성)

### 저장/로드
- 파일: UMM 모드 폴더 `chat_history.json`
- 저장: 게임 세이브 시 + Machine Spirit 비활성화 시
- 로드: Machine Spirit 활성화 시 자동
- 최대 100 메시지 + conversationSummary 함께 저장

---

## 수정 파일 목록

| 파일 | 변경 |
|------|------|
| MachineSpiritConfig.cs | PersonalityType, IdleFrequency, EnableVision 추가 |
| MachineSpirit.cs | Idle timer 로직, Vision trigger |
| LLMClient.cs | ChatMessage.Images, SendOllamaStreaming 비전 지원 |
| ContextBuilder.cs | 성격별 프롬프트, 지역 센서 데이터 |
| ChatWindow.cs | 대화 저장/로드 |
| GameEventCollector.cs | VisionObservation 이벤트 타입 |
| MainUI.cs | 성격/아이들/비전 설정 UI |
| ModSettings.cs | 새 로컬라이제이션 키 |
| **NEW** VisionCapture.cs | 스크린샷 캡처 + 리사이즈 + base64 |
