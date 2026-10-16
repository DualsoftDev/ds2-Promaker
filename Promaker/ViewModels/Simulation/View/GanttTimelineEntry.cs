using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Ds2.Core;
using Ds2.Core.Store;
using Promaker.Presentation;

namespace Promaker.ViewModels;

/// <summary>간트 행 종류 — Work ▸ Call ▸ ApiCall(한 행이 Plan 위/I/O 아래 2줄)</summary>
public enum GanttRowKind { Work, Call, ApiCall }

/// <summary>간트차트 상태 세그먼트 — 하나의 상태 구간</summary>
public class GanttStateSegment : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private Status4 _state = Status4.Ready;
    public Status4 State
    {
        get => _state;
        set { _state = value; Notify(); Notify(nameof(StateBrush)); }
    }

    private DateTime _startTime;
    public DateTime StartTime
    {
        get => _startTime;
        set { _startTime = value; Notify(); Notify(nameof(Duration)); }
    }

    private DateTime? _endTime;
    public DateTime? EndTime
    {
        get => _endTime;
        set { _endTime = value; Notify(); Notify(nameof(Duration)); }
    }

    public TimeSpan Duration => (EndTime ?? DateTime.Now) - StartTime;

    /// <summary>이 Going 의 plan 틀 생략 — 통신 재개 후 첫 Going 같은 "중간 합류" 세그먼트는
    /// 시작점이 실제 사이클 시작이 아니라 합류 시점이라, 틀을 그리면 다음 사이클까지 침범한다.</summary>
    public bool SuppressPlanOverlay { get; set; }

    private bool _isAbnormal;
    /// <summary>이 사이클에 진짜 abnormal 판정(이벤트)이 떨어졌는가 — 바를 경고색으로.
    /// "벗어남=참고(plan 틀), 색=이상(판정)" 시각 언어 — 판정된 사이클 바만 칠한다.</summary>
    public bool IsAbnormal
    {
        get => _isAbnormal;
        set { if (_isAbnormal == value) return; _isAbnormal = value; Notify(); Notify(nameof(StateBrush)); }
    }

    public Brush StateBrush => _isAbnormal
        ? Status4Visuals.ResolveGanttBarAbnormalBrush()
        : Status4Visuals.ResolveGanttBarBrush(State);

    public string StateFullName => Status4Visuals.DisplayName(State);
}

/// <summary>간트차트 타임라인 항목 — 하나의 Work 또는 Call 행</summary>
public class GanttTimelineEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public EntityKind Kind { get; init; } = EntityKind.Work;
    public GanttRowKind RowKind { get; init; } = GanttRowKind.Work;
    public Guid? ParentWorkId { get; init; }
    public Guid? ParentCallId { get; init; }
    public string SystemName { get; init; } = "";
    public int RowIndex { get; init; }
    /// <summary>plan 길이(ms) — PLAY 시 모델값으로 초기화, 라이브 학습 반영 시 갱신(set).</summary>
    public double? BaseDurationMs { get; set; }
    public int VirtualAppendMs { get; init; }
    public int OutputAppendMs { get; init; }
    /// <summary>ApiCall I/O \uc904 \u2014 \uc2e4\uc81c \uc1a1\uc2e0(Out)/\uc218\uc2e0(In) Tag \uc8fc\uc18c. OnHubTagChanged \ub9e4\ud551\uc6a9.</summary>
    public string OutAddress { get; init; } = "";
    public string InAddress { get; init; } = "";
    /// <summary>\uc717\uc904 \ub9c9\ub300 \u2014 Work/Call \uc0c1\ud0dc, ApiCall \uc740 Plan(\uacc4\ud68d) \uc904.</summary>
    public ObservableCollection<GanttStateSegment> Segments { get; } = [];
    /// <summary>\uc544\ub7ab\uc904 \ub9c9\ub300 \u2014 ApiCall \uc758 I/O(\uc2e4\uc81c Out\u00b7In) \uc904.</summary>
    public ObservableCollection<GanttStateSegment> OutSegments { get; } = [];
    /// <summary>아랫줄 막대 — device 실제 In(수신) high 구간. Out 과 독립 추적(겹쳐도 서로 안 닫힘).</summary>
    public ObservableCollection<GanttStateSegment> InSegments { get; } = [];

    private Status4 _currentState = Status4.Ready;
    public Status4 CurrentState
    {
        get => _currentState;
        set { _currentState = value; Notify(); }
    }

    public bool IsWork => RowKind == GanttRowKind.Work;
    public bool IsCall => RowKind == GanttRowKind.Call;
    public bool IsApiCall => RowKind == GanttRowKind.ApiCall;
    /// <summary>\uc811\uae30/\ud3b4\uae30 \ud1a0\uae00 \ub300\uc0c1 \u2014 Work/Call \ub9cc \uc790\uc2dd\uc744 \uac00\uc9c4\ub2e4.</summary>
    public bool HasChildren => IsWork || IsCall;
    public double YOffset { get; set; }
    /// <summary>ApiCall \uc740 Plan(\uc704)/I/O(\uc544\ub798) 2\uc904\uc774\ub77c \ub192\uc774 2\ubc30.</summary>
    public double RowHeight => IsApiCall ? 28 : 22;
    /// <summary>ApiCall \ud55c \uc904(Plan \ub610\ub294 I/O) \ub192\uc774.</summary>
    public double SubRowHeight => 14;
    public int IndentLevel => RowKind switch
    {
        GanttRowKind.Call => 1,
        GanttRowKind.ApiCall => 2,
        _ => 0
    };
    public Thickness IndentMargin => new(IndentLevel * 14, 0, 0, 0);
    public string DisplayName => RowKind switch
    {
        GanttRowKind.ApiCall => $"\u2514 {Name}",   // PLN/I/O \ud45c\uc2dd\uc740 XAML \uc6b0\uce21 \uce7c\ub7fc\uc5d0 \uc138\ub85c\ub85c
        _ => Name   // Work/Call \uc740 \uc774\ub984\ub9cc (Call \uc758 \u2514 \uc81c\uac70)
    };

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; Notify(); }
    }

    private bool _isVisible = true;
    public bool IsVisible
    {
        get => _isVisible;
        set { _isVisible = value; Notify(); }
    }

}
