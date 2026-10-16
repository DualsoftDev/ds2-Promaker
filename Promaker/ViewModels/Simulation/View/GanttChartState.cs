using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Ds2.Core;
using Ds2.Core.Store;

namespace Promaker.ViewModels;

/// <summary>간트차트 전체 뷰모델</summary>
public partial class GanttChartState : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public ObservableCollection<GanttTimelineEntry> Entries { get; } = [];

    private DateTime _startTime = DateTime.Now;
    public DateTime StartTime
    {
        get => _startTime;
        set { _startTime = value; Notify(); Notify(nameof(TotalDuration)); Notify(nameof(TimelineDuration)); }
    }

    private DateTime _currentTime = DateTime.Now;
    public DateTime CurrentTime
    {
        get => _currentTime;
        set { _currentTime = value; Notify(); Notify(nameof(TotalDuration)); Notify(nameof(TimelineDuration)); Notify(nameof(ElapsedText)); }
    }

    public TimeSpan TotalDuration => CurrentTime - StartTime;
    public string ElapsedText => TotalDuration.ToString(@"hh\:mm\:ss\.f");

    /// <summary>표시 윈도우 길이(분) — 빨간 타임라인(현재시각) 기준 최근 N분만 간트에 보인다. PLC 설정
    /// 슬라이더로 5분~300분(5시간) 조정. 그보다 오래된 구간은 링버퍼 트림으로 스크롤해도 닿지 않는다.</summary>
    public int RenderWindowMinutes { get; set; } = 300;

    /// <summary>
    /// 렌더 origin(보이는 왼쪽 끝) — 빨간 타임라인(CurrentTime) 기준 최근 RenderWindowMinutes 분만 보이는
    /// 슬라이딩 윈도우의 시작점. origin = max(StartTime, CurrentTime - N분). 운전이 N분 미만이면 세션
    /// 시작부터(윈도우 아직 안 참), N분 넘으면 항상 최근 N분만 — 빨간선이 움직이면 윈도우도 같이 슬라이딩
    /// (예: 빨간선 14:00:00·윈도우 5h → 왼쪽 끝 9:00:00, 0~9h 는 스크롤해도 안 보임). X축 라벨은
    /// RenderTimeRuler 가 (origin - StartTime) 을 더해 세션 시작 기준 경과시각으로 표기(왼쪽 끝 9:00:00
    /// 식 — 0s 아님). 총 가동시간(ElapsedText)은 StartTime 기준 — 분리.
    /// </summary>
    public DateTime RenderStartTime
    {
        get
        {
            var cutoff = CurrentTime.AddMinutes(-RenderWindowMinutes);
            return cutoff < StartTime ? StartTime : cutoff;
        }
    }

    public TimeSpan TimelineDuration => GetTimelineEndTime() - RenderStartTime;

    private double _pixelsPerSecond = 50.0;
    public double PixelsPerSecond
    {
        get => _pixelsPerSecond;
        set { _pixelsPerSecond = Math.Clamp(value, MinPixelsPerSecond, MaxPixelsPerSecond); Notify(); }
    }

    public const double MinPixelsPerSecond = 5.0;
    public const double MaxPixelsPerSecond = 500.0;

    private double _horizontalOffset;
    public double HorizontalOffset
    {
        get => _horizontalOffset;
        set { _horizontalOffset = Math.Max(0, value); Notify(); }
    }

    private double _verticalOffset;
    public double VerticalOffset
    {
        get => _verticalOffset;
        set { _verticalOffset = Math.Max(0, value); Notify(); }
    }

    private bool _isRunning;
    private DateTime _pausedAt;
    private TimeSpan _totalPausedDuration = TimeSpan.Zero;

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value) return;
            if (!value)
                _pausedAt = DateTime.Now;
            else if (_pausedAt != default)
                _totalPausedDuration += DateTime.Now - _pausedAt;
            _isRunning = value;
            Notify();
        }
    }

    /// <summary>
    /// 현재 시각 source override.
    /// Simulation 모드에서는 sim clock 기반 (`_simStartTime + engine.State.Clock`) 으로 set 되어
    /// 빨간선과 노드 막대가 같은 시간 기준으로 일치. 그 외 모드 (Control/VP/Monitoring) 는 null —
    /// wall clock 기반 default 사용 (외부 신호 owner 라 sim clock 이 wall clock 따라가지 못함).
    /// </summary>
    public Func<DateTime>? NowOverride { get; set; }

    /// <summary>
    /// 열린(진행 중) 세그먼트의 렌더 끝 상한 — "증거(마지막 신호) 시각" provider.
    /// 신호 기반 모드에서 통신이 멎으면 blackout 확정(무소식 3초) 전에도 열린 바가
    /// 빨간 선까지 계속 자라다 동결 시 마지막 신호 시각으로 "챡" 되감기는 왜곡이 생긴다 —
    /// 렌더가 매 프레임 이 cap 으로 클립하면 바가 증거가 있는 데까지만 자란다.
    /// null(provider 없음/반환 null) 이면 기존 동작(현재 시각까지).
    /// </summary>
    public Func<DateTime?>? OpenSegmentEvidenceCap { get; set; }

    /// <summary>
    /// plan vs actual overlay 표시 여부 — 비-Simulation 모드(Control/Monitoring/VP)에서 true.
    /// Work/Call 행의 Going 구간에 plan(BaseDurationMs) 배경 바를 행 높이로 깔고,
    /// actual 상태 막대는 얇게 가운데 그린다. actual 이 배경보다 짧으면 빠른 것, 뚫고 나가면 느린 것.
    /// </summary>
    private bool _showPlanOverlay;
    public bool ShowPlanOverlay
    {
        get => _showPlanOverlay;
        set { if (_showPlanOverlay == value) return; _showPlanOverlay = value; Notify(); }
    }

    /// <summary>
    /// 신호 유추 모드(VP/Monitoring) — 워밍업 후 사이클 "중간"에 합류한 첫 Going 세그먼트는
    /// 시작점이 실제 사이클 시작이 아니라 유추 확정 시점이라, plan 틀을 그리면 다음 사이클까지
    /// 침범한다. true 면 entry 별 첫 Going 의 plan 틀을 생략하고 둘째 사이클부터 그린다.
    /// </summary>
    private bool _suppressFirstGoingPlanOverlay;
    public bool SuppressFirstGoingPlanOverlay
    {
        get => _suppressFirstGoingPlanOverlay;
        set { if (_suppressFirstGoingPlanOverlay == value) return; _suppressFirstGoingPlanOverlay = value; Notify(); }
    }

    /// <summary>Pause 누적 시간을 보정한 현재 시각 (Pause 중이면 고정).
    /// NowOverride 가 set 되어 있으면 그 provider 가 우선 — sim clock 기반에서는
    /// engine.Pause 시 sim clock 자체가 멈추므로 별도 _pausedAt 보정 불필요.</summary>
    public DateTime AdjustedNow
    {
        get
        {
            if (NowOverride is { } provider)
                return provider();
            if (!_isRunning && _pausedAt != default)
                return _pausedAt - _totalPausedDuration;
            return DateTime.Now - _totalPausedDuration;
        }
    }

    public void Reset(DateTime startTime)
    {
        StartTime = startTime;
        CurrentTime = startTime;
        _totalPausedDuration = TimeSpan.Zero;
        _pausedAt = default;
        Entries.Clear();
        ShadowWindows.Clear();
        _suppressNextGoingPlan.Clear();
        HorizontalOffset = 0;
        VerticalOffset = 0;
    }

    public GanttTimelineEntry? FindEntry(Guid nodeId)
    {
        foreach (var entry in Entries)
        {
            if (entry.Id == nodeId)
                return entry;
        }
        return null;
    }

    private DateTime GetTimelineEndTime()
    {
        var end = CurrentTime;
        foreach (var entry in Entries)
        {
            foreach (var segment in entry.Segments)
            {
                var segmentEnd = segment.EndTime ?? CurrentTime;
                if (segmentEnd > end) end = segmentEnd;

                if (entry.OutputAppendMs > 0 && segment.State == Status4.Going && segment.EndTime is { } finishedAt)
                {
                    var outputEnd = finishedAt.AddMilliseconds(entry.OutputAppendMs);
                    if (outputEnd > end) end = outputEnd;
                }
            }
        }
        return end;
    }

    public GanttTimelineEntry AddEntry(
        Guid id,
        string name,
        EntityKind kind,
        Guid? parentWorkId = null,
        string systemName = "",
        double? baseDurationMs = null,
        int virtualAppendMs = 0,
        int outputAppendMs = 0)
    {
        var entry = new GanttTimelineEntry
        {
            Id = id,
            Name = name,
            Kind = kind,
            RowKind = kind == EntityKind.Call ? GanttRowKind.Call : GanttRowKind.Work,
            ParentWorkId = parentWorkId,
            SystemName = systemName,
            BaseDurationMs = baseDurationMs,
            VirtualAppendMs = Math.Max(0, virtualAppendMs),
            OutputAppendMs = Math.Max(0, outputAppendMs),
            RowIndex = Entries.Count,
            IsExpanded = kind != EntityKind.Call   // Call 기본 접힘(자식 ApiCall 숨김), Work 는 펼침
        };

        // 모든 행(Work/Call) 초기 Ready segment로 시작 — R/G/F/H 전부 간트에 표기.
        entry.Segments.Add(new GanttStateSegment
        {
            State = Status4.Ready,
            StartTime = StartTime
        });

        Entries.Add(entry);
        return entry;
    }

    /// <summary>Call 밑 ApiCall 행 추가 — 한 행이 Plan(윗줄)/I/O(아랫줄) 2줄.</summary>
    public GanttTimelineEntry AddApiCallEntry(
        Guid apiCallId,
        string name,
        Guid parentWorkId,
        Guid parentCallId,
        string systemName = "",
        double? baseDurationMs = null,
        string outAddress = "",
        string inAddress = "",
        int outputAppendMs = 0)
    {
        var entry = new GanttTimelineEntry
        {
            Id = apiCallId,
            Name = name,
            Kind = EntityKind.Call,
            RowKind = GanttRowKind.ApiCall,
            ParentWorkId = parentWorkId,
            ParentCallId = parentCallId,
            SystemName = systemName,
            BaseDurationMs = baseDurationMs,
            OutAddress = outAddress,
            InAddress = inAddress,
            OutputAppendMs = Math.Max(0, outputAppendMs),   // device I/O 줄(Out 끝)에 timeAppend 빨간 점선 표기용
            RowIndex = Entries.Count
        };
        entry.Segments.Add(new GanttStateSegment { State = Status4.Ready, StartTime = StartTime });
        Entries.Add(entry);
        return entry;
    }

    /// <summary>Work/Call 접기-펴기 토글 → 자식 행 IsVisible 갱신.</summary>
    public void SetExpanded(Guid entryId, bool expanded)
    {
        var target = FindEntry(entryId);
        if (target is null || !target.HasChildren) return;
        target.IsExpanded = expanded;
        RefreshVisibility();
    }

    /// <summary>특정 RowKind(Work/Call) 전체 접기-펴기 — 우클릭 메뉴용.</summary>
    public void ExpandAll(GanttRowKind kind, bool expanded)
    {
        foreach (var e in Entries)
            if (e.RowKind == kind && e.HasChildren)
                e.IsExpanded = expanded;
        RefreshVisibility();
    }

    /// <summary>전체 행 가시성 재계산 — 부모(Work→Call) 의 IsExpanded 사슬을 따른다.</summary>
    public void RefreshVisibility()
    {
        foreach (var e in Entries)
        {
            bool visible = true;
            if (e.RowKind == GanttRowKind.Call)
            {
                var work = e.ParentWorkId.HasValue ? FindEntry(e.ParentWorkId.Value) : null;
                visible = work?.IsExpanded ?? true;
            }
            else if (e.IsApiCall)
            {
                var call = e.ParentCallId.HasValue ? FindEntry(e.ParentCallId.Value) : null;
                var work = e.ParentWorkId.HasValue ? FindEntry(e.ParentWorkId.Value) : null;
                visible = (call?.IsExpanded ?? true) && (work?.IsExpanded ?? true);
            }
            e.IsVisible = visible;
        }
    }
}
