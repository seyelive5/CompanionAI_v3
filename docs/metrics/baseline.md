# Baseline Index

현재 활성 베이스라인: [baseline-2026-05-03-phase3-3.md](baseline-2026-05-03-phase3-3.md) (Phase 3.3 후, MovementAPI.cs 2,448→197줄, 6 partial 분할)

이전 베이스라인 (archive — 회귀 추적용):
- [baseline-2026-05-03-phase3.md](baseline-2026-05-03-phase3.md) (Phase 3 후, BasePlan.cs 4,396→135줄, 8 partial 분할)
- [baseline-2026-04-29-phase2.md](baseline-2026-04-29-phase2.md) (Phase 2 후, Main.Log* 제거)
- [baseline-2026-04-28-phase1.md](baseline-2026-04-28-phase1.md) (Phase 1 후, silent catch 7 intentional)
- [baseline-2026-04-28.md](baseline-2026-04-28.md) (v3.114.0, Phase 1 진입 전, silent catch 205)

새 베이스라인 갱신 시:

1. `bash scripts/code-metrics.sh > docs/metrics/baseline-YYYY-MM-DD.md`
2. 본 인덱스의 "현재 활성" 링크를 새 파일로 업데이트.
3. 이전 베이스라인은 archive로 보존 (회귀 추적용).

## 사용법

```bash
# 현재 메트릭 출력
bash scripts/code-metrics.sh

# 베이스라인과 diff
diff docs/metrics/baseline-2026-04-28.md <(bash scripts/code-metrics.sh)
```

(노트: Phase 0+6 마스터 플랜 — [2026-04-28-code-hygiene-master-plan.md](../plans/2026-04-28-code-hygiene-master-plan.md))
