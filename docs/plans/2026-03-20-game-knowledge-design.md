# Game Knowledge System — Machine Spirit 지식 기반 컨텍스트

## 목표

Machine Spirit이 "연기"가 아니라 **실제 게임 데이터를 알고** 말하도록 한다.
적의 특성, 무기 스탯, 퀘스트 배경, 지역 정보를 Blueprint에서 직접 읽어
LLM 프롬프트에 자동 주입한다.

## Phase A: 직접 Blueprint 조회 (이번 구현)

### GameKnowledge.cs (신규)

정적 클래스. 게임 오브젝트의 Blueprint 참조에서 직접 텍스트를 읽는다.

| 메서드 | 데이터 소스 | 출력 |
|--------|-----------|------|
| `GetEnemyInfo(BlueprintUnit)` | unit.Blueprint.Description, Encyclopedia BestiaryUnit | "Sslyth: 독 공격, 높은 회피" |
| `GetWeaponInfo(BlueprintItemWeapon)` | .DamageType, .AttackRange, .Description | "Bolter: 18-24 dmg, pen 4" |
| `GetAbilityInfo(BlueprintAbility)` | .RawDescription, .Range, .AoERadius | "Melta Shot: 열 30-40, AP 2" |
| `GetQuestInfo(Quest)` | .GetDescription(), .Objectives | "목표: 유적 조사" |
| `GetAreaInfo()` | CurrentlyLoadedArea.AreaDisplayName, Encyclopedia 매칭 | "Footfall: 무역 허브" |

### ContextBuilder 통합

상황별 자동 주입:

**전투 중** → `[TACTICAL INTEL]`
```
Enemies:
- Sslyth Warrior: Xenos, poison attacks, high dodge
Party Weapons:
- Argenta: Godwyn Bolter (18-24 dmg, pen 4, burst capable)
```

**대화 중** → `[ACTIVE QUEST]`
```
Quest: "Shadows of the Warp" — investigate anomalous signals
Current Objective: Speak with Navigator about warp disturbances
```

**탐험 중** → `[LOCATION INTEL]`
```
Area: Footfall — lawless void station, major trade hub
Active Quests: 2 pending objectives in this area
```

### 데이터 접근 경로

- 적 유닛: `unit.Blueprint` → `.Description` (LocalizedString.Text)
- 무기: `weapon.Blueprint` → `.DamageType`, `.AttackRange`
- 능력: `ability.Blueprint` → `.RawDescription`, `.Range`
- 퀘스트: `Game.Instance.Player.QuestBook.Quests` → `.GetDescription()`
- 지역: `Game.Instance.CurrentlyLoadedArea` → `.AreaDisplayName`
- 백과사전: `UIConfig.Instance.ChapterList` → BlockText/BlockBestiaryUnit

### RAG 불필요 근거

자동 코멘트 시점에 관련 게임 오브젝트의 Blueprint 참조를 이미 가지고 있으므로
검색이 불필요. 직접 조회가 정확도 100%, 지연 0ms.

## Phase B: RAG 검색 (나중에 확장)

사용자 자유 질문 지원을 위한 확장.

### 인터페이스

```csharp
interface IKnowledgeSearch
{
    List<KnowledgeEntry> Search(string query, int topK = 5);
}
```

### 확장 경로

1. `KeywordSearch` — BM25 키워드 매칭 (C# 50줄)
2. `VectorSearch` — nomic-embed-text 임베딩 + 코사인 유사도
3. `HybridSearch` — BM25 + Vector + RRF 결합

### Phase A에서 Phase B 준비물

- GameKnowledge의 텍스트 생성 메서드가 이미 KnowledgeEntry 데이터를 생산
- 인덱싱하면 그대로 검색 가능
- KnowledgeEntry에 `float[] Embedding` 필드 추가로 벡터 검색 지원

### Phase B에서 구현할 것

- BM25/벡터 검색 엔진
- Encyclopedia 전체 인덱싱
- 사용자 질문 인텐트 분류
- SQLite DB (오프라인 빌드, 선택적)

## 변경 파일

| 파일 | 변경 |
|------|------|
| `MachineSpirit/GameKnowledge.cs` | **신규** |
| `MachineSpirit/ContextBuilder.cs` | 3개 지식 섹션 주입 |
