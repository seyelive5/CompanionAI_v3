#!/usr/bin/env bash
# code-metrics.sh — CompanionAI_v3 코드 위생 메트릭 측정
# 사용: bash scripts/code-metrics.sh
#   결과를 베이스라인으로 저장하려면: bash scripts/code-metrics.sh > docs/metrics/baseline-YYYY-MM-DD.md
#
# 출력: stdout 에 메트릭 표 (Markdown).
# 의존: bash, find, grep, awk, wc.

set -eu
cd "$(git rev-parse --show-toplevel)"

DATE=$(date +%Y-%m-%d)
GIT_REV=$(git rev-parse --short HEAD)

# 측정 함수 ----------------------------------------------------------

# 제외 경로: bin/obj/.git/Tools (의존성, 빌드 산출물, 외부 스크립트).
# "$@" 로 호출자 args 전달 (e.g. -exec cat {} +).
find_cs() {
    find . -type f -name "*.cs" \
        -not -path "./bin/*" -not -path "./obj/*" \
        -not -path "*/bin/*" -not -path "*/obj/*" \
        -not -path "./.git/*" -not -path "./Tools/*" "$@"
}

count_files()    { find_cs | wc -l | tr -d ' '; }
count_total_loc() { find_cs -exec cat {} + | wc -l | tr -d ' '; }

count_files_over() {
    local threshold=$1
    find_cs -exec wc -l {} + \
        | awk -v t="$threshold" 'NF==2 && $2!~/total/ && $1>t {n++} END{print n+0}'
}

count_silent_catches() {
    # Phase 2 후: 'LogDebug.*ex.Message' (legacy Main.LogDebug) 또는
    # 'Log.<Cat>.Debug(...ex.Message)' (post-Phase-2 카테고리 형식) 둘 다 매칭.
    # 의도된 silent catch 7건은 후자 형식 — // intentional: 주석 동반.
    grep -rE "(LogDebug|Log\.[A-Za-z]+\.Debug).*ex\.Message" --include="*.cs" \
        --exclude-dir=bin --exclude-dir=obj --exclude-dir=.git . 2>/dev/null \
        | wc -l | tr -d ' '
}

count_total_catches() {
    grep -rE "catch\s*\(\s*Exception" --include="*.cs" \
        --exclude-dir=bin --exclude-dir=obj --exclude-dir=.git . 2>/dev/null \
        | wc -l | tr -d ' '
}

count_version_markers() {
    grep -rE "★\s*v[0-9]" --include="*.cs" \
        --exclude-dir=bin --exclude-dir=obj --exclude-dir=.git . 2>/dev/null \
        | wc -l | tr -d ' '
}

count_deep_nested_if() {
    grep -rE "^\s{16,}if\s*\(" --include="*.cs" \
        --exclude-dir=bin --exclude-dir=obj --exclude-dir=.git . 2>/dev/null \
        | wc -l | tr -d ' '
}

count_main_log_calls() {
    grep -rE "Main\.Log\w*\(" --include="*.cs" \
        --exclude-dir=bin --exclude-dir=obj --exclude-dir=.git . 2>/dev/null \
        | wc -l | tr -d ' '
}

# 측정 실행 ----------------------------------------------------------

FILES=$(count_files)
LOC=$(count_total_loc)
F1000=$(count_files_over 1000)
F2000=$(count_files_over 2000)
F4000=$(count_files_over 4000)
SILENT=$(count_silent_catches)
CATCH_TOTAL=$(count_total_catches)
MARKERS=$(count_version_markers)
DEEP_IF=$(count_deep_nested_if)
MAIN_LOG=$(count_main_log_calls)

# 출력 ---------------------------------------------------------------

cat <<EOF
# Code Hygiene Metrics

| Field | Value |
|---|---|
| Date | $DATE |
| Git rev | $GIT_REV |

| Metric | Count | Notes |
|---|---|---|
| C# files | $FILES | |
| Total LOC | $LOC | |
| Files > 1,000 LOC | $F1000 | godfile 후보 |
| Files > 2,000 LOC | $F2000 | |
| Files > 4,000 LOC | $F4000 | 분해 최우선 |
| catch (Exception) total | $CATCH_TOTAL | |
| Silent catch (LogDebug+ex.Message) | $SILENT | Phase 1 타깃 |
| ★ vX.Y inline markers | $MARKERS | Phase 4 자연 소멸 |
| Indented if (16+ spaces) | $DEEP_IF | Phase 5 점진 — 향후 20+ 임계값 검토 |
| Main.Log* flat calls | $MAIN_LOG | Phase 2 카테고리화 타깃 |
EOF
