<!-- FROZEN BASELINE — DO NOT EDIT. Captured after Phase 2 (category logging migration).
     Previous baseline: baseline-2026-04-28-phase1.md (post-Phase-1, silent catch=7).
     This baseline: post-Phase-2 (Main.Log* removed, ~1,720 calls migrated to Log.<Cat>.<Lvl>).
     To capture a new baseline, write to a new dated file (baseline-YYYY-MM-DD[-phase].md)
     and update docs/metrics/baseline.md to point to it. -->

# Code Hygiene Metrics

| Field | Value |
|---|---|
| Date | 2026-04-30 |
| Git rev | 63210fa |

| Metric | Count | Notes |
|---|---|---|
| C# files | 122 | |
| Total LOC | 78003 | |
| Files > 1,000 LOC | 22 | godfile 후보 |
| Files > 2,000 LOC | 4 | |
| Files > 4,000 LOC | 2 | 분해 최우선 |
| catch (Exception) total | 289 | |
| Silent catch (LogDebug+ex.Message) | 7 | Phase 1 타깃 |
| ★ vX.Y inline markers | 3497 | Phase 4 자연 소멸 |
| Indented if (16+ spaces) | 4059 | Phase 5 점진 — 향후 20+ 임계값 검토 |
| Main.Log* flat calls | 0 | Phase 2 카테고리화 타깃 |
