using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using AAStoPLC.TagWizard;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.Win32;
using Promaker.Services;
using Promaker.Services.Io;

namespace Promaker.Dialogs;

/// <summary>
/// IO·태그 확인 다이얼로그 — 읽기 전용 검증 도구. 편집은 TAG Wizard / PropertyPanel.
/// store 를 받아 QueryService 로 직접 행을 생성/새로고침하며, 표시·필터링·CSV 내보내기·진단만 담당한다.
/// 탭: IO 신호 / Dummy 신호 / 사용자 태그.
/// </summary>
public partial class TagInspectorDialog : Window
{
    private readonly DsStore _store;
    private readonly Action<string?>? _openFBTagMapEdit;
    private readonly ObservableCollection<IoBatchRow> _rows = new();
    private readonly ObservableCollection<DummySignalRow> _dummyRows = new();
    private readonly ObservableCollection<DiagnosticItemViewModel> _diagnostics = new();
    private readonly ObservableCollection<ProjectUserTagRow> _userTagRows = new();
    private readonly ICollectionView _view;
    private readonly ICollectionView _userTagView;
    private readonly RowFilterDebouncer _filterDebouncer;
    private string _userTagSearch = string.Empty;

    /// <summary>System(PLC) 필터 — null 이면 전체. 멀티 PLC 에서 주소의 네임스페이스는 System 이다.</summary>
    private Guid? _systemFilterId;

    /// <summary>콤보 인덱스 → SystemId 매핑 (0 = 전체).</summary>
    private readonly List<Guid?> _systemFilterItems = new();

    private bool _suppressSystemFilterEvent;

    private bool _showOnlyUnmatched;
    private int _unmatchedCount;
    private int _errorCount;
    private int _warningCount;

    // 그리드 헤더 내장 필터 — TextChanged 에서 Tag 키로 Map 업데이트 후 디바운스 Refresh.
    private readonly Dictionary<string, string> _filters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Flow"] = "", ["Work"] = "", ["Device"] = "", ["Api"] = "",
        ["InName"] = "", ["InType"] = "", ["InAddress"] = "",
        ["OutName"] = "", ["OutType"] = "", ["OutAddress"] = "",
    };

    /// <summary>
    /// store 1개만으로 다이얼로그 생성. 행은 QueryService 가 만든다.
    /// <paramref name="openFBTagMapEdit"/> 가 주어지면 진단 카드의 "FBTagMap 편집" 버튼이 활성화되어
    /// SystemType 식별자와 함께 호출자에게 전달한다 (TAG Wizard 진입 등). 호출 후 자동 새로고침된다.
    /// </summary>
    public TagInspectorDialog(DsStore store, Action<string?>? openFBTagMapEdit = null, Guid? initialSystemId = null)
    {
        InitializeComponent();
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _openFBTagMapEdit = openFBTagMapEdit;
        _systemFilterId = initialSystemId;

        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = FilterRow;
        IoGrid.ItemsSource = _view;
        DummyGrid.ItemsSource = _dummyRows;
        DiagnosticsPanel.DiagnosticsList.ItemsSource = _diagnostics;

        _userTagView = CollectionViewSource.GetDefaultView(_userTagRows);
        _userTagView.Filter = FilterUserTagRow;
        UserTagsGrid.ItemsSource = _userTagView;

        _filterDebouncer = new RowFilterDebouncer(() => _view.Refresh());

        DataContext = this;
        Closing += TagInspectorDialog_Closing;

        LoadFromStore();
    }

    // ── 데이터 로드 / 새로고침 ────────────────────────────────────────────

    private void LoadFromStore()
    {
        var prev = Cursor;
        try
        {
            Cursor = Cursors.Wait;

            RebuildSystemFilterItems();

            var qr = QueryService.Generate(_store);

            // 행 갱신 — 기존 PropertyChanged 핸들러 정리 후 재구독.
            foreach (var r in _rows) r.PropertyChanged -= Row_PropertyChanged;
            _rows.Clear();
            foreach (var r in qr.Rows)
            {
                r.PropertyChanged += Row_PropertyChanged;
                _rows.Add(r);
            }

            // Dummy 신호 행 갱신.
            _dummyRows.Clear();
            foreach (var d in qr.DummyRows) _dummyRows.Add(d);

            // UserTags 행 갱신 — 프로젝트 전체 평탄화.
            _userTagRows.Clear();
            foreach (var u in _store.GetAllUserTagsForProject())
                _userTagRows.Add(u);

            // 탭 헤더에 카운트 부착.
            IoTab.Header       = $"IO 신호 ({_rows.Count})";
            DummyTab.Header    = $"Dummy 신호 ({_dummyRows.Count})";
            UserTagsTab.Header = $"사용자 태그 ({_userTagRows.Count})";

            _unmatchedCount = qr.Unmatched.Count;
            _errorCount = qr.ErrorCount;
            _warningCount = qr.WarningCount;
            ShowOnlyUnmatchedCheckBox.Visibility =
                _unmatchedCount > 0 ? Visibility.Visible : Visibility.Collapsed;

            ApplyDiagnostics(qr.Diagnostics);
            UpdateStatusChips();

            BatchDialogHelper.UpdateSelectedCount(_rows, SelectedCountText);
            _view.Refresh();
            _userTagView.Refresh();
        }
        finally
        {
            Cursor = prev;
        }
    }

    // ── System(PLC) 필터 ──────────────────────────────────────────────────

    /// <summary>active System 목록으로 콤보 재구성. 기존 선택(_systemFilterId)이 살아 있으면 유지.</summary>
    private void RebuildSystemFilterItems()
    {
        _suppressSystemFilterEvent = true;
        try
        {
            _systemFilterItems.Clear();
            SystemFilterCombo.Items.Clear();

            _systemFilterItems.Add(null);
            SystemFilterCombo.Items.Add("전체 System");

            var project = _store.Projects.Values.FirstOrDefault();
            if (project is not null)
            {
                foreach (var sys in Queries.activeSystemsOf(project.Id, _store))
                {
                    _systemFilterItems.Add(sys.Id);
                    SystemFilterCombo.Items.Add(sys.Name);
                }
            }

            // System 이 1개뿐이면 필터가 무의미 — 콤보는 남기되 '전체'로 고정해 소음 제거.
            var selectedIndex = _systemFilterId is { } sid ? _systemFilterItems.IndexOf(sid) : 0;
            if (selectedIndex < 0) { _systemFilterId = null; selectedIndex = 0; }
            SystemFilterCombo.SelectedIndex = selectedIndex;
            SystemFilterCombo.IsEnabled = _systemFilterItems.Count > 2;
        }
        finally
        {
            _suppressSystemFilterEvent = false;
        }
    }

    private void SystemFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSystemFilterEvent) return;
        var index = SystemFilterCombo.SelectedIndex;
        _systemFilterId = index >= 0 && index < _systemFilterItems.Count ? _systemFilterItems[index] : null;
        _view.Refresh();
        _userTagView.Refresh();
        UpdateStatusChips();
    }

    // ── UserTags 탭 ───────────────────────────────────────────────────────

    private bool FilterUserTagRow(object obj)
    {
        if (obj is not ProjectUserTagRow r) return false;
        if (_systemFilterId is { } systemId && r.SystemId != systemId)
            return false;
        if (string.IsNullOrWhiteSpace(_userTagSearch)) return true;
        var q = _userTagSearch;
        return (r.SystemName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (r.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (r.TagAddress?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (r.LogLevel?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (r.ValueType?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void UserTagSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        _userTagSearch = tb.Text?.Trim() ?? string.Empty;
        _userTagView.Refresh();
    }

    private void UserTagCsvExport_Click(object sender, RoutedEventArgs e)
    {
        if (_userTagRows.Count == 0)
        {
            MessageBox.Show(this, "내보낼 사용자 태그가 없습니다.", "CSV 내보내기",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "사용자 태그 CSV 내보내기",
            FileName = "UserTags.csv",
            Filter = "CSV 파일 (*.csv)|*.csv|모든 파일 (*.*)|*.*",
            DefaultExt = "csv"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("System,이름,로그 레벨,태그 주소,값 타입");
            foreach (var r in _userTagRows)
                sb.AppendLine($"{CsvEscape(r.SystemName)},{CsvEscape(r.Name)},{CsvEscape(r.LogLevel)},{CsvEscape(r.TagAddress)},{CsvEscape(r.ValueType)}");

            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            DialogHelpers.Warn(this, $"CSV 내보내기 실패: {ex.Message}", "오류");
        }
    }

    private static string CsvEscape(string? value)
    {
        var v = value ?? string.Empty;
        return v.Contains(',') || v.Contains('"') || v.Contains('\n')
            ? $"\"{v.Replace("\"", "\"\"")}\""
            : v;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var pending = PendingIoEditCount;
        if (pending > 0
            && DialogHelpers.ShowThemedMessageBox(
                   $"적용하지 않은 I/O 변경 {pending}건을 버리고 새로고침할까요?",
                   "새로고침", MessageBoxButton.OKCancel, "❓") != MessageBoxResult.OK)
            return;
        LoadFromStore();
    }

    private void IoGrid_LayoutUpdated(object? sender, EventArgs e)
    {
        if (IoFilterHeader.IoHeaderGrid.ColumnDefinitions.Count == 0 || IoGrid.Columns.Count == 0)
            return;

        var count = Math.Min(IoFilterHeader.IoHeaderGrid.ColumnDefinitions.Count, IoGrid.Columns.Count);
        for (var i = 0; i < count; i++)
        {
            var actualWidth = IoGrid.Columns[i].ActualWidth;
            if (actualWidth <= 0d)
                continue;

            var currentWidth = IoFilterHeader.IoHeaderGrid.ColumnDefinitions[i].Width;
            if (currentWidth.IsAbsolute && Math.Abs(currentWidth.Value - actualWidth) < 0.5d)
                continue;

            IoFilterHeader.IoHeaderGrid.ColumnDefinitions[i].Width = new GridLength(actualWidth, GridUnitType.Pixel);
        }
    }


    // ── 필터링 ────────────────────────────────────────────────────────────

    private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb || tb.Tag is not string key) return;
        _filters[key] = tb.Text ?? "";
        _filterDebouncer.Bump();
    }

    private bool FilterRow(object obj)
    {
        if (obj is not IoBatchRow row) return false;

        if (_systemFilterId is { } systemId && row.SystemId != systemId)
            return false;

        if (_showOnlyUnmatched && !row.IsUnmatched)
            return false;

        return Match(row.Flow,        F("Flow"))
            && Match(row.Work,        F("Work"))
            && Match(row.Device,      F("Device"))
            && Match(row.Api,         F("Api"))
            && Match(row.InSymbol,    F("InName"))
            && Match(row.InDataType,  F("InType"))
            && Match(row.InAddress,   F("InAddress"))
            && Match(row.OutSymbol,   F("OutName"))
            && Match(row.OutDataType, F("OutType"))
            && Match(row.OutAddress,  F("OutAddress"));
    }

    private string F(string key) => _filters.TryGetValue(key, out var v) ? v : "";

    private static bool Match(string value, string filter) =>
        string.IsNullOrEmpty(filter)
        || (value != null && value.Contains(filter, StringComparison.OrdinalIgnoreCase));

    private void ShowOnlyUnmatched_Changed(object sender, RoutedEventArgs e)
    {
        _showOnlyUnmatched = ShowOnlyUnmatchedCheckBox.IsChecked == true;
        _view.Refresh();
    }

    // ── 선택/체크박스 ──────────────────────────────────────────────────────

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IoBatchRow.IsSelected))
            BatchDialogHelper.UpdateSelectedCount(_rows, SelectedCountText);
        else if (e.PropertyName == nameof(IoBatchRow.IsChanged))
            RefreshApplyIoState();
    }

    // ── I/O 직접 편집 (#212) ────────────────────────────────────────────────
    // 4/28 이후 읽기 전용이라 I/O 하나 고치려면 Properties 에서 Call 을 찾아가야 했다. 표에서 고치고 한 번에 적용한다.

    private int PendingIoEditCount => _rows.Count(r => r.IsChanged);

    private void RefreshApplyIoState()
    {
        var pending = PendingIoEditCount;
        ApplyIoButton.IsEnabled = pending > 0;
        ApplyIoButton.Content = pending > 0 ? $"변경 적용 ({pending})" : "변경 적용";
    }

    /// <summary>Reference Call·매핑 없는 행은 고칠 ApiCall 이 없으니 편집을 막는다.</summary>
    private void IoGrid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is IoBatchRow { IsEditable: false })
            e.Cancel = true;
    }

    private void ApplyIo_Click(object sender, RoutedEventArgs e) => ApplyIoEdits();

    /// <summary>표에서 고친 행만 모델에 반영한다 — UpdateApiCallIoTagsBatch 로 Undo 한 번. 반환 = 실패 없이 끝났는지.</summary>
    internal bool ApplyIoEdits()
    {
        IoGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        IoGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var changed = _rows.Where(r => r.IsChanged).ToList();
        if (changed.Count == 0)
            return true;

        var result = TagApplier.Apply(_store, changed);
        LoadFromStore();
        if (!result.AnyFailed)
            return true;

        DialogHelpers.ShowThemedMessageBox(
            $"{result.SuccessCount}개 적용, {result.FailedCount}개 실패:\n"
            + string.Join("\n", result.FailedItems.Take(10)),
            "I/O 변경 적용", MessageBoxButton.OK, "⚠");
        return false;
    }

    /// <summary>적용하지 않은 편집이 있으면 닫기 전에 묻는다 — 말없이 버려지지 않게.</summary>
    private void TagInspectorDialog_Closing(object? sender, CancelEventArgs e)
    {
        IoGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        IoGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var pending = PendingIoEditCount;
        if (pending == 0)
            return;

        var answer = DialogHelpers.ShowThemedMessageBox(
            $"적용하지 않은 I/O 변경이 {pending}건 있습니다. 적용할까요?",
            "I/O 변경 적용", MessageBoxButton.YesNoCancel, "❓");
        if (answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.Yes && !ApplyIoEdits()))
            e.Cancel = true;
    }

    private void CheckSelected_Click(object sender, RoutedEventArgs e) =>
        BatchDialogHelper.CheckGridSelected<IoBatchRow>(IoGrid);

    private void UncheckSelected_Click(object sender, RoutedEventArgs e) =>
        BatchDialogHelper.UncheckGridSelected<IoBatchRow>(IoGrid);

    private void CheckAll_Click(object sender, RoutedEventArgs e) =>
        BatchDialogHelper.CheckAll(_rows);

    private void UncheckAll_Click(object sender, RoutedEventArgs e) =>
        BatchDialogHelper.UncheckAll(_rows);

    private void Grid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        BatchDialogHelper.DeselectOnEmptyAreaClick(sender, e);

    private void RowCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: IoBatchRow row } cb) return;
        BatchDialogHelper.ApplyCheckStateToSelectedRows(IoGrid, row, cb.IsChecked == true);
    }

    // Delete 키 — 우클릭 메뉴와 동일하게 체크된 항목 I/O 삭제. (IsReadOnly 그리드라 기본 행삭제는 동작 안 함)
    private void IoGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        // 셀을 고치는 중의 Delete 는 글자 지우기다 — I/O 삭제로 가로채지 않는다.
        if (e.OriginalSource is TextBox) return;
        DeleteSelectedIo_Click(sender, e);
        e.Handled = true;
    }

    // ── 선택 항목 I/O 삭제 ──────────────────────────────────────────────────
    // 체크한 행의 ApiCall(=I/O 매핑)을 store 에서 제거. Call 자체는 유지된다.
    // 참조 Call / 매핑 없는 행(CallId·ApiCallId Empty = dangling)은 건너뛴다.
    private void DeleteSelectedIo_Click(object sender, RoutedEventArgs e)
    {
        if (PendingIoEditCount > 0)
        {
            DialogHelpers.ShowThemedMessageBox("적용하지 않은 I/O 변경이 있습니다. 먼저 '변경 적용' 을 누르거나 새로고침하세요.",
                "I/O 삭제", MessageBoxButton.OK, "ℹ");
            return;
        }

        var targets = _rows.Where(r => r.IsSelected).ToList();
        if (targets.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox("체크된 항목이 없습니다.",
                "I/O 삭제", MessageBoxButton.OK, "ℹ");
            return;
        }

        var res = DialogHelpers.ShowThemedMessageBox(
            $"체크한 {targets.Count}개 항목의 I/O(ApiCall)를 삭제하시겠습니까?\nCall 자체는 유지되고 매핑만 제거됩니다.",
            "I/O 삭제", MessageBoxButton.OKCancel, "❓");
        if (res != MessageBoxResult.OK) return;

        int removed = 0, skipped = 0;
        foreach (var r in targets)
        {
            if (r.CallId == Guid.Empty || r.ApiCallId == Guid.Empty) { skipped++; continue; }
            try { _store.RemoveApiCallFromCall(r.CallId, r.ApiCallId); removed++; }
            catch { skipped++; }
        }

        LoadFromStore();

        DialogHelpers.ShowThemedMessageBox(
            skipped > 0
                ? $"{removed}개 삭제, {skipped}개 건너뜀(참조 Call / 매핑 없음)."
                : $"{removed}개 항목의 I/O를 삭제했습니다.",
            "I/O 삭제", MessageBoxButton.OK, "✓");
    }

}
