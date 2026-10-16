using System.Windows;
using System.Windows.Input;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;

namespace Promaker.Controls;

public partial class EditorCanvas
{
    private void Arrow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid arrowId } || VM is null) return;

        var ctrlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        VM.Selection.ClearNodeSelection();
        var arrow = ActiveCanvasState!.CanvasArrows.FirstOrDefault(a => a.Id == arrowId);
        if (arrow is not null)
            VM.Selection.SelectArrowFromCanvas(arrow, ctrlPressed: ctrlPressed || shiftPressed);

        e.Handled = true;
    }

    private void Arrow_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid arrowId } || VM is null) return;

        var arrow = ActiveCanvasState!.CanvasArrows.FirstOrDefault(a => a.Id == arrowId);
        if (arrow is null) return;

        // 다중 선택 보존: 우클릭한 화살표가 이미 다중 선택의 일부면 그대로 유지
        var current = VM.Selection.OrderedArrowSelection;
        var keepMultiSelection = current.Count >= 2 && current.Contains(arrowId);

        if (!keepMultiSelection)
        {
            VM.Selection.ClearNodeSelection();
            VM.Selection.SelectArrowFromCanvas(arrow, ctrlPressed: false);
        }

        var isWorkMode = ActiveCanvasState!.ActiveTab is { } tab && EntityKindRules.isWorkArrowModeForTab(tab.Kind);
        var menu = new System.Windows.Controls.ContextMenu();
        var selectedIds = VM.Selection.OrderedArrowSelection.ToList();
        var isMulti = selectedIds.Count >= 2;

        // Call 모드에서 선택된 화살표가 모두 동일한 타입이면 다이얼로그 우회 후 토글
        ArrowType? uniformCallType = null;
        if (!isWorkMode)
        {
            var selectedArrows = ActiveCanvasState!.CanvasArrows
                .Where(a => selectedIds.Contains(a.Id))
                .ToList();
            if (selectedArrows.Count > 0)
            {
                var first = selectedArrows[0].ArrowType;
                if (selectedArrows.All(a => a.ArrowType == first))
                    uniformCallType = first;
            }
        }

        string changeTypeHeader;
        if (uniformCallType is { } uct)
        {
            var nextType = EntityKindRules.toggleCallArrowType(uct);
            var label = nextType == ArrowType.Start ? "Start" : "Group";
            changeTypeHeader = isMulti
                ? $"{label}(으)로 변경 ({selectedIds.Count}개)"
                : $"{label}(으)로 변경";
        }
        else
            changeTypeHeader = isMulti ? $"타입 변경 ({selectedIds.Count}개)" : "타입 변경";

        var changeType = new System.Windows.Controls.MenuItem { Header = changeTypeHeader };
        changeType.Click += (_, _) =>
        {
            ArrowType newType;
            if (uniformCallType is { } current)
            {
                newType = EntityKindRules.toggleCallArrowType(current);
            }
            else
            {
                if (!TryPromptArrowType(isWorkMode ? EntityKind.Work : EntityKind.Call, out newType))
                    return;
            }

            if (isMulti)
            {
                var changed = VM.TryUpdateArrowTypesBatch(selectedIds, newType);
                if (changed > 0)
                    VM.StatusText = $"화살표 타입 변경: {changed}/{selectedIds.Count}개";
            }
            else
            {
                VM.TryUpdateArrowType(arrowId, newType);
            }
        };
        menu.Items.Add(changeType);

        var reverse = new System.Windows.Controls.MenuItem
        {
            Header = isMulti ? $"방향 전환 ({selectedIds.Count}개)" : "방향 전환"
        };
        reverse.Click += (_, _) =>
        {
            if (isMulti)
            {
                var changed = VM.TryReverseArrowsBatch(selectedIds);
                if (changed > 0)
                    VM.StatusText = $"화살표 방향 전환: {changed}/{selectedIds.Count}개";
            }
            else
            {
                VM.TryReverseArrow(arrowId);
            }
        };
        menu.Items.Add(reverse);

        var remove = new System.Windows.Controls.MenuItem { Header = "삭제" };
        remove.Click += (_, _) => VM.DeleteSelectedCommand.Execute(null);
        menu.Items.Add(remove);

        menu.IsOpen = true;
        e.Handled = true;
    }

    private void ArrowStartHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        BeginArrowReconnect(sender, replaceSource: true, e);

    private void ArrowEndHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        BeginArrowReconnect(sender, replaceSource: false, e);

    private void BeginArrowReconnect(object sender, bool replaceSource, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid arrowId } || VM is null)
            return;

        var arrow = ActiveCanvasState!.CanvasArrows.FirstOrDefault(a => a.Id == arrowId);
        if (arrow is null)
            return;

        if (_connectSource is not null)
            CancelConnect();

        var anchorPoint = replaceSource
            ? new Point(arrow.EndX, arrow.EndY)
            : new Point(arrow.StartX, arrow.StartY);

        _arrowReconnect = new ArrowReconnectState(arrowId, replaceSource, anchorPoint);
        ConnectPreview.X1 = anchorPoint.X;
        ConnectPreview.Y1 = anchorPoint.Y;
        ConnectPreview.X2 = anchorPoint.X;
        ConnectPreview.Y2 = anchorPoint.Y;
        ConnectPreview.Visibility = Visibility.Visible;
        RootGrid.Cursor = Cursors.Cross;
        RootGrid.CaptureMouse();
        Focus();
        e.Handled = true;
    }

    private void FinishArrowReconnect(Point dropPoint)
    {
        if (_arrowReconnect is not { } reconnect)
            return;

        var vm = VM;
        CancelArrowReconnect();
        if (vm is null)
            return;

        var target = FindNodeAt(dropPoint);
        if (target is null)
            return;

        vm.TryReconnectArrowFromCanvas(reconnect.ArrowId, reconnect.ReplaceSource, target.Id);
    }

    private void CancelArrowReconnect()
    {
        _arrowReconnect = null;
        ConnectPreview.Visibility = Visibility.Collapsed;
        RootGrid.Cursor = Cursors.Arrow;
        RootGrid.ReleaseMouseCapture();
    }
}
