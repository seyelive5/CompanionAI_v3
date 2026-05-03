# Code Hygiene Metrics

| Field | Value |
|---|---|
| Date | 2026-05-03 |
| Git rev | b5c491c |

| Metric | Count | Notes |
|---|---|---|
| C# files | 137 | |
| Total LOC | 78227 | |
| Files > 1,000 LOC | 22 | godfile 후보 |
| Files > 2,000 LOC | 2 | |
| Files > 4,000 LOC | 1 | 분해 최우선 |
| catch (Exception) total | 289 | |
| Silent catch (LogDebug+ex.Message) | 7 | Phase 1 타깃 |
| ★ vX.Y inline markers | 3497 | Phase 4 자연 소멸 |
| Indented if (16+ spaces) | 4059 | Phase 5 점진 — 향후 20+ 임계값 검토 |
| Main.Log* flat calls | 0 | Phase 2 카테고리화 타깃 |
