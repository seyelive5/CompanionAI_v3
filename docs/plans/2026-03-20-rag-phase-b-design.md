# RAG Phase B — 사용자 자유 질문 지원

## 목표

사용자가 Machine Spirit에게 게임 관련 자유 질문을 하면, 게임 내부 데이터
(Blueprint + Encyclopedia)를 검색하여 정확한 답변을 생성한다.
성격 톤을 유지하면서 데이터 기반으로 답한다.

## 지식 소스

게임 내부 데이터만 사용 (외부 위키는 나중에 확장):
- BlueprintAbility — 이름, 설명, 사거리, AoE
- BlueprintItemWeapon — 이름, 데미지, 관통, 사거리, 무기 타입
- BlueprintItem — 이름, 설명, 희귀도
- BlueprintUnit — 이름, 설명 (적/NPC)
- BlueprintQuest — 이름, 설명, 목표
- BlueprintEncyclopediaPage — 제목, 텍스트 블록 내용

## 아키텍처

### 1. KnowledgeIndex — 백그라운드 인덱싱

MachineSpirit.Initialize() 후 코루틴으로 점진적 인덱싱:
- 매 프레임 5-10개 Blueprint 처리 (프레임 드롭 방지)
- Utilities.GetBlueprintGuids<T>()로 GUID 열거
- Encyclopedia는 UIConfig.Instance.ChapterList 트리 순회
- 완료까지 30-60초 (사용자 인식 불가)

데이터 구조:
```csharp
struct KnowledgeEntry
{
    string Id;          // Blueprint GUID
    string Title;       // 이름
    string Text;        // 설명/내용
    string Category;    // weapon, ability, quest, lore, enemy
    string[] Tokens;    // BM25용 사전 토큰화
    float[] Embedding;  // 벡터 검색용 (null = 미계산)
}
```

### 2. HybridSearch — BM25 + 벡터 + RRF

BM25 (키워드):
- 인덱싱 시 텍스트 토큰화 → Tokens 저장
- IDF * TF 점수 (k1=1.2, b=0.75)
- 고유명사 매칭에 강함

벡터 (시맨틱):
- Ollama /api/embed (nomic-embed-text)
- 쿼리 임베딩 → 내적으로 코사인 유사도
- 의미적 검색에 강함

Lazy 전략:
- BM25 즉시 가용 (CPU만)
- 벡터 임베딩은 Ollama 가동 시 백그라운드 점진 계산
- 임베딩 미완료 → BM25 단독 검색 (graceful degradation)
- 임베딩 완료 → 하이브리드 자동 전환

결합: RRF (k=60)
- BM25 top-20 + Vector top-20 → RRF → top-5 반환

### 3. 질문 감지 + 응답 흐름

질문 감지 (휴리스틱):
- ? 포함
- 질문 키워드: 뭐/어떻게/추천/최적/what/how/best/recommend
- 검색 히트 1개 이상

응답 흐름:
```
사용자 질문 → 질문 감지 → Search(query, topK=5)
  → [REFERENCE DATA] 프롬프트 주입
  → 성격 프롬프트 유지 + "데이터 기반으로 답하되 성격 유지" 지시
  → LLM 호출 → 응답
```

프롬프트:
```
[REFERENCE DATA]
Source 1: "Plasma Gun" (weapon) — 18-24 damage, penetration 4...
Source 2: "Argenta" (companion) — ranged specialist...
[/REFERENCE DATA]
Answer based on the reference data above. Be accurate. Keep your personality.
If the data doesn't contain the answer, say you don't know.
```

## 변경 파일

| 파일 | 변경 |
|------|------|
| MachineSpirit/Knowledge/KnowledgeEntry.cs | 신규 — 데이터 구조체 |
| MachineSpirit/Knowledge/KnowledgeIndex.cs | 신규 — 인덱스 구축 코루틴 |
| MachineSpirit/Knowledge/BM25Search.cs | 신규 — BM25 키워드 검색 |
| MachineSpirit/Knowledge/VectorSearch.cs | 신규 — 임베딩 + 코사인 |
| MachineSpirit/Knowledge/HybridSearch.cs | 신규 — RRF 결합 |
| MachineSpirit/MachineSpirit.cs | 인덱싱 시작 + 질문 감지 |
| MachineSpirit/ContextBuilder.cs | BuildForKnowledgeQuery() |
| MachineSpirit/LLMClient.cs | /api/embed 호출 |

## 포함하지 않는 것
- SQLite DB (인메모리 충분)
- 외부 위키 데이터 (게임 내부 먼저)
- 시맨틱 캐시 (첫 버전 불필요)
