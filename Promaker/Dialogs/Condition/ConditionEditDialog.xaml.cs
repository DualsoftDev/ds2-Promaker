using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using AAStoPLC.Ir;
using AAStoPLC.LadderEditor.Adapters;
using AAStoPLC.LadderEditor.Models;
using AAStoPLC.LadderEditor.Rendering;
using Ds2.Core;
using Ds2.Core.Store;
using Microsoft.FSharp.Core;
using Promaker.Presentation;
using Promaker.ViewModels;

namespace Promaker.Dialogs;

/// <summary>
/// Call 조건 편집 다이얼로그 — 트리 입력 UI 제거 후 LadderEditor 단독 화면.
/// 현재는 표시 + 인터랙션 만 — store 로의 역반영(save-back) 은 후속 작업.
/// 변경 진입점은 그대로 Call drag-drop / 별도 ApiCall 추가 다이얼로그 사용.
/// </summary>
public partial class ConditionEditDialog : Window
{
    private readonly DsStore _store;
    private readonly MainViewModel.PropertyPanelHost _host;
    private readonly Guid _callId;
    private readonly EntityKind _ownerKind;
    private readonly ConditionType _condType;
    private readonly ObservableCollection<RungViewModel> _rungs = new();
    private readonly EditorContext _ctx = new() { GridCols = 14 };
    private CoilRungViewModel? _rung;
    private bool _isDirty;  // 사용자가 LadderEditor 에서 한 번이라도 편집했는지.
    private bool _suppressTextSync; // 텍스트박스 자동 갱신 재진입 차단.
    private string _lastCommittedText = ""; // dirty / commit 추적용.
    private readonly System.Windows.Threading.DispatcherTimer _validateTimer;
    private System.Windows.Media.Brush? _normalBorder;

    private static readonly System.Windows.Media.Brush DirtyBorder = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0xB1, 0x34));
    private static readonly System.Windows.Media.Brush ErrorBorder = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE1, 0x5B, 0x5B));
    private static readonly System.Windows.Media.Brush ErrorText   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE1, 0x5B, 0x5B));
    private static readonly System.Windows.Media.Brush WarnText    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0xB1, 0x34));
    private static readonly System.Windows.Media.Brush OkText      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x57, 0xC0, 0x6D));

    public ConditionEditDialog(
        DsStore store,
        MainViewModel.PropertyPanelHost host,
        Guid ownerId,
        EntityKind ownerKind,
        ConditionType condType)
    {
        InitializeComponent();
        _store = store;
        _host = host;
        _callId = ownerId;   // Owner Id (Call/Work 공용 슬롯)
        _ownerKind = ownerKind;
        _condType = condType;
        SectionTitle.Text = $"{condType} 조건 편집";
        StatusText.Text   = "닫기 시 LadderEditor 변경 사항이 store 에 저장됩니다.";

        EditorView.Context = _ctx;
        EditorView.Rungs   = _rungs;
        SyncTheme(ThemeManager.CurrentTheme);
        ThemeManager.ThemeChanged += SyncTheme;
        // 닫을 때 적용 안 한 ST 수정까지 반영해 저장한다. ST 에 구문 오류가 있으면 버리고 닫을지 묻는다.
        Closing += (_, e) =>
        {
            if (!TryCommitPendingStText(askDiscardOnError: true)) { e.Cancel = true; return; }
            TrySaveBack();
        };
        Closed  += (_, _) => ThemeManager.ThemeChanged -= SyncTheme;

        Refresh();
        ApplyMode();

        _validateTimer = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(300) };
        _validateTimer.Tick += (_, _) => { _validateTimer.Stop(); LiveValidate(); };

        // 편집 발생 추적 — RungEdited / EditFailed 이벤트.
        Loaded += (_, _) =>
        {
            if (EditorView.Edit is { } edit)
            {
                edit.RungEdited += (_, _) => OnEdited();
                edit.EditFailed += (_, t) =>
                {
                    ShowStatus($"⚠ {t.Action} 실패: {t.Reason}", WarnText);
                    StatusText.Text = $"⚠ 편집 실패: {t.Reason}";
                };
            }
            if (TextBoxBorder is not null) _normalBorder = TextBoxBorder.BorderBrush;
        };
    }

    // ── Condition 트리 → CoilCondition 변환 (Call/Work 공용 — 외부 DLL buildPreview 우회) ───
    private CoilCondition BuildConditionPreview()
    {
        IEnumerable<Ds2.Core.Condition>? source =
            _ownerKind == EntityKind.Work
                ? (_store.Works.TryGetValue(_callId, out var work) ? work.Conditions : null)
                : (_store.Calls.TryGetValue(_callId, out var call) ? call.Conditions : null);
        if (source is null) return CoilCondition.AlwaysTrue;

        var matches = new List<Ds2.Core.Condition>();
        foreach (var c in source)
        {
            if (FSharpOption<ConditionType>.get_IsSome(c.Type) && c.Type.Value == _condType)
                matches.Add(c);
        }
        if (matches.Count == 0) return CoilCondition.AlwaysTrue;

        var roots = new List<CoilCondition>();
        foreach (var c in matches)
        {
            var built = BuildOneCondition(c);
            if (built is not null) roots.Add(built);
        }
        if (roots.Count == 0) return CoilCondition.AlwaysTrue;
        if (roots.Count == 1) return roots[0];
        return CoilCondition.NewAnd(Microsoft.FSharp.Collections.ListModule.OfSeq(roots));
    }

    private CoilCondition? BuildOneCondition(Ds2.Core.Condition cond)
    {
        var children = new List<CoilCondition>();
        foreach (var ac in cond.ApiCalls) children.Add(BuildLeafCondition(ac));
        foreach (var child in cond.Children)
        {
            var sub = BuildOneCondition(child);
            if (sub is not null) children.Add(sub);
        }
        if (children.Count == 0) return null;
        CoilCondition group;
        if (children.Count == 1) group = children[0];
        else if (cond.IsOR) group = CoilCondition.NewOr(Microsoft.FSharp.Collections.ListModule.OfSeq(children));
        else                group = CoilCondition.NewAnd(Microsoft.FSharp.Collections.ListModule.OfSeq(children));
        return cond.IsInverted ? CoilCondition.NewNot(group) : group;
    }

    private CoilCondition BuildLeafCondition(ApiCall ac)
    {
        if (ac.ContactKind == ContactKind.Inverter)
            return CoilCondition.NewNot(CoilCondition.NewRaw(""));
        string name = ac.Name;
        if (FSharpOption<Guid>.get_IsSome(ac.ApiDefId)
            && _store.ApiDefs.TryGetValue(ac.ApiDefId.Value, out var def)
            && _store.Systems.TryGetValue(def.ParentId, out var sys))
        {
            name = $"{sys.Name}.{def.Name}";
        }
        return ac.ContactKind switch
        {
            ContactKind.NcContact    => CoilCondition.NewNegVar(name),
            ContactKind.RisingPulse  => CoilCondition.NewRising(name),
            ContactKind.FallingPulse => CoilCondition.NewFalling(name),
            _                        => CoilCondition.NewVar(name)
        };
    }

    private void OnEdited()
    {
        _isDirty = true;
        if (_rung is not null)
        {
            UpdateSymbolProvider(_rung.Condition);
            SyncStTextFromRung();
        }
    }

    private void SyncTheme(AppTheme theme) =>
        EditorView.Theme = theme == AppTheme.Dark ? new DefaultDarkTheme() : new DefaultLightTheme();

    /// <summary>현재 store 상태 → CoilCondition → 단일 CoilRung 으로 표시.
    /// 외부 DLL(AAStoPLC) buildPreview 는 옛 Call.CallConditions 시그니처로 컴파일되어
    /// rename 이후 type load 가 실패한다. Call/Work 모두 C# 자체 변환으로 우회.</summary>
    private void Refresh()
    {
        CoilCondition cond = BuildConditionPreview();
        const string coilName = "OUT";

        if (_rung is null)
        {
            _rung = new CoilRungViewModel(cond, coilName);
            _rungs.Clear();
            _rungs.Add(_rung);
        }
        else
        {
            _rung.Condition = cond;
            _rung.CoilBit   = coilName;
        }
        UpdateSymbolProvider(cond);
        SyncStTextFromRung();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_rung is null) { StatusText.Text = "rung 없음"; return; }
        // ST 로 고치고 "적용" 없이 저장하면 래더 쪽 이전 내용이 저장됐다 — 먼저 반영한다(구문 오류면 저장하지 않는다).
        if (!TryCommitPendingStText(askDiscardOnError: false)) return;
        var nameToId = BuildDisplayNameToApiCallId();
        var leaves = CoilAst.Leaves(_rung.Condition).Select(l => l.Name).ToList();
        var matched = leaves.Where(n => nameToId.ContainsKey(n)).ToList();
        var unmatched = leaves.Where(n => !nameToId.ContainsKey(n)).ToList();

        _isDirty = true;
        TrySaveBack();
        _isDirty = false;

        var msg = $"✓ 저장 — leaf {leaves.Count}, 매핑 {matched.Count}, 미매핑 {unmatched.Count}";
        if (unmatched.Count > 0) msg += $" [미매핑: {string.Join(", ", unmatched.Take(3))}]";
        StatusText.Text = msg;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
