using System.Collections.Generic;

namespace CompanionAI_v3.Diagnostics
{
    /// <summary>
    /// ★ v3.44.0: 턴별 결정 내러티브 히스토리 (최근 N턴 링 버퍼)
    /// DecisionOverlayUI에서 이전/다음 턴 탐색에 사용
    /// </summary>
    public class DecisionHistory
    {
        private const int MAX_ENTRIES = 20;
        private readonly List<NarrativeEntry> _entries = new List<NarrativeEntry>();
        private int _viewIndex = -1;  // -1 = 최신 (라이브)

        public int Count => _entries.Count;
        public int ViewIndex => _viewIndex;
        public bool IsViewingLive => _viewIndex < 0 || _viewIndex >= _entries.Count - 1;

        public void Add(NarrativeEntry entry)
        {
            if (entry == null) return;
            _entries.Add(entry);
            if (_entries.Count > MAX_ENTRIES)
                _entries.RemoveAt(0);
            _viewIndex = _entries.Count - 1;  // 새 항목 추가 시 최신으로 이동
        }

        public NarrativeEntry GetCurrent()
        {
            if (_entries.Count == 0) return null;
            int idx = _viewIndex >= 0 && _viewIndex < _entries.Count
                ? _viewIndex : _entries.Count - 1;
            return _entries[idx];
        }

        public void NavigatePrev()
        {
            if (_viewIndex > 0) _viewIndex--;
        }

        public void NavigateNext()
        {
            if (_viewIndex < _entries.Count - 1) _viewIndex++;
        }

        public void Clear()
        {
            _entries.Clear();
            _viewIndex = -1;
        }

        /// <summary>현재 보고 있는 위치 표시 문자열 (예: "3/10")</summary>
        public string GetPositionLabel()
        {
            if (_entries.Count == 0) return "";
            int display = (_viewIndex >= 0 ? _viewIndex : _entries.Count - 1) + 1;
            return $"{display}/{_entries.Count}";
        }
    }

    /// <summary>
    /// 한 턴의 내러티브 요약
    /// </summary>
    public class NarrativeEntry
    {
        public string UnitName { get; set; }
        public string Role { get; set; }
        public float HPPercent { get; set; }
        public List<string> Lines { get; set; } = new List<string>();
        public int Round { get; set; }
    }
}
