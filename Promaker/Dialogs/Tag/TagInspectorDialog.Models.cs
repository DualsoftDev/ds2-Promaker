using System;
using System.Collections.Generic;
using System.Windows.Media;
using AAStoPLC.TagWizard;
using Ds2.Editor;

namespace Promaker.Dialogs;

/// <summary>
/// IO·태그 확인용 IO 행. In/Out 이름·주소는 표에서 바로 고칠 수 있고(#212), 처음 값과 달라진 행(IsChanged)만 적용한다.
/// IoQueryService.Generate 이 채워서 반환한다.
/// </summary>
public sealed class IoBatchRow : BatchRowBase
{
    public IoBatchRow(Guid callId, Guid apiCallId, string flow, string work, string device, string api,
                      string inAddress, string inSymbol, string outAddress, string outSymbol,
                      string outDataType = "BOOL", string inDataType = "BOOL",
                      Guid systemId = default, string systemName = "")
    {
        CallId = callId;
        ApiCallId = apiCallId;
        SystemId = systemId;
        SystemName = systemName;
        Flow = flow;
        Work = work;
        Device = device;
        Api = api;
        _inAddress = _originalInAddress = inAddress ?? "";
        _inSymbol = _originalInSymbol = inSymbol ?? "";
        _outAddress = _originalOutAddress = outAddress ?? "";
        _outSymbol = _originalOutSymbol = outSymbol ?? "";
        OutDataType = outDataType;
        InDataType = inDataType;
    }

    public Guid CallId { get; }
    public Guid ApiCallId { get; }

    /// <summary>소속 active System(PLC) — 멀티 PLC 에서 System 필터 축.</summary>
    public Guid SystemId { get; }
    public string SystemName { get; }

    public string Flow { get; }
    public string Work { get; }
    public string Device { get; }
    public string Api { get; }

    // 읽어 온 값 — 고친 행만 골라 적용하는 기준.
    private readonly string _originalInAddress, _originalInSymbol, _originalOutAddress, _originalOutSymbol;
    private string _inAddress, _inSymbol, _outAddress, _outSymbol;

    public string InAddress  { get => _inAddress;  set => SetEdited(ref _inAddress, value); }
    public string InSymbol   { get => _inSymbol;   set => SetEdited(ref _inSymbol, value); }
    public string OutAddress { get => _outAddress; set => SetEdited(ref _outAddress, value); }
    public string OutSymbol  { get => _outSymbol;  set => SetEdited(ref _outSymbol, value); }
    public string OutDataType { get; }
    public string InDataType  { get; }

    /// <summary>In/Out 이름·주소 중 하나라도 읽어 온 값과 다르다.</summary>
    public bool IsChanged =>
        _inAddress != _originalInAddress || _inSymbol != _originalInSymbol
        || _outAddress != _originalOutAddress || _outSymbol != _originalOutSymbol;

    /// <summary>Reference Call·매핑 없는 행은 고칠 대상(ApiCall)이 없다.</summary>
    public bool IsEditable => CallId != Guid.Empty && ApiCallId != Guid.Empty;

    private void SetEdited(ref string field, string? value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (SetField(ref field, value ?? "", propertyName))
            OnPropertyChanged(nameof(IsChanged));
    }
}

/// <summary>
/// 진단 패널 항목 표시용 ViewModel.
/// XAML 바인딩에 필요한 표시 속성(Icon/AccentBrush/HasRawMessage 등)을 노출하고,
/// "해당 행 보기" 클릭 시 점프할 행 목록을 들고 있다.
/// </summary>
public sealed class DiagnosticItemViewModel
{
    private static readonly Brush ErrorBrush   = MakeBrush(0xE1, 0x5B, 0x5B);
    private static readonly Brush WarningBrush = MakeBrush(0xF2, 0xB1, 0x34);
    private static readonly Brush InfoBrush    = MakeBrush(0x57, 0xC0, 0x6D);

    public DiagnosticItemViewModel(
        DiagnosticItem source,
        IReadOnlyList<IoBatchRow> matchedRows,
        bool fbTagMapEditAvailable = false)
    {
        Source = source;
        MatchedRows = matchedRows;
        FBTagMapEditAvailable = fbTagMapEditAvailable;
    }

    public DiagnosticItem Source { get; }
    public IReadOnlyList<IoBatchRow> MatchedRows { get; }
    public bool FBTagMapEditAvailable { get; }
    public string? SystemType => Source.SystemType;

    public string Icon => Source.Severity switch
    {
        DiagnosticSeverity.Error   => "❌",
        DiagnosticSeverity.Warning => "⚠",
        _                          => "ℹ",
    };

    public Brush AccentBrush => Source.Severity switch
    {
        DiagnosticSeverity.Error   => ErrorBrush,
        DiagnosticSeverity.Warning => WarningBrush,
        _                          => InfoBrush,
    };

    public string Title       => Source.Title;
    public string Detail      => Source.Detail;
    public string? RawMessage => Source.RawMessage;

    public bool HasRawMessage    => !string.IsNullOrEmpty(Source.RawMessage);
    public bool HasAffectedRows  => MatchedRows.Count > 0;

    /// <summary>SystemType 이 식별되고 호출자가 편집 액션을 제공했을 때만 버튼 노출.</summary>
    public bool CanOpenFBTagMap  => FBTagMapEditAvailable && !string.IsNullOrEmpty(SystemType);

    private static Brush MakeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
