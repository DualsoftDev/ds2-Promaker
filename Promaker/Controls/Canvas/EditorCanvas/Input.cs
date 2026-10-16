using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.ViewModels;

namespace Promaker.Controls;

public partial class EditorCanvas
{
    internal static bool IsConnectShortcutKey(Key key) => key == Key.F3;

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();

        if (e.MiddleButton == MouseButtonState.Pressed)
        {
            _isPanning = true;
            _panStart = e.GetPosition(RootGrid);
            RootGrid.CaptureMouse();
            RootGrid.Cursor = Cursors.ScrollAll;
            e.Handled = true;
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed) return;

        // 캔버스에서 Shift는 Ctrl과 동일하게 토글 선택 (range 선택은 2D에서 무의미)
        var ctrlPressed = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != ModifierKeys.None;
        const bool shiftPressed = false;

        if (TryGetNodeFromElement(e.OriginalSource as DependencyObject, out var nodeId, out var border) && VM is not null)
        {
            if (_connectSource is { } sourceId && sourceId != nodeId)
            {
                CompleteConnect(nodeId);
                e.Handled = true;
                return;
            }

            var node = ActiveCanvasState!.CanvasNodes.FirstOrDefault(n => n.Id == nodeId);
            if (node is null) return;

            if (e.ClickCount == 2 && EntityKindRules.canOpenAsTab(node.EntityType))
            {
                var tabTargetId = node.ReferenceOfId ?? nodeId;
                ActiveCanvasState!.OpenCanvasTab(tabTargetId, EntityKind.Work, expandTree: true);
                e.Handled = true;
                return;
            }

            var dragNodes = VM.Selection.PrepareCanvasDragSelection(node, ctrlPressed, shiftPressed);
            VM.Selection.ClearArrowSelection();

            if (!ctrlPressed && !shiftPressed)
            {
                var canvasPos = e.GetPosition(MainCanvas);
                var screenPos = e.GetPosition(RootGrid);
                var dragItems = dragNodes
                    .Select(n => new DragItem(n, n.X, n.Y))
                    .ToList();
                var arrowSnapshots = BuildArrowSnapshots(dragItems);
                _drag = new DragState(canvasPos, screenPos, dragItems, arrowSnapshots);
                _dragElement = border;
                border.CaptureMouse();
            }

            e.Handled = true;
            return;
        }

        if (_connectSource is not null)
        {
            CancelConnect();
            e.Handled = true;
            return;
        }

        _isBoxSelecting = true;
        _boxSelectAdditive = ctrlPressed;
        _boxStart = e.GetPosition(MainCanvas);
        UpdateSelectionRectangle(_boxStart, _boxStart);
        SelectionRect.Visibility = Visibility.Visible;
        RootGrid.CaptureMouse();
        VM?.Selection.ClearArrowSelection();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_isPanning)
        {
            var pos = e.GetPosition(RootGrid);
            PanTransform.X += pos.X - _panStart.X;
            PanTransform.Y += pos.Y - _panStart.Y;
            _panStart = pos;
            return;
        }

        if (_connectSource is not null)
        {
            var mp = e.GetPosition(MainCanvas);
            ConnectPreview.X2 = mp.X;
            ConnectPreview.Y2 = mp.Y;
            return;
        }

        if (_arrowReconnect is not null)
        {
            var mp = e.GetPosition(MainCanvas);
            ConnectPreview.X1 = _arrowReconnect.AnchorPoint.X;
            ConnectPreview.Y1 = _arrowReconnect.AnchorPoint.Y;
            ConnectPreview.X2 = mp.X;
            ConnectPreview.Y2 = mp.Y;
            return;
        }

        if (_isBoxSelecting)
        {
            var current = e.GetPosition(MainCanvas);
            UpdateSelectionRectangle(_boxStart, current);
            return;
        }

        if (_drag is null || _dragElement is null) return;

        var canvasPos = e.GetPosition(MainCanvas);
        var dx = canvasPos.X - _drag.StartPoint.X;
        var dy = canvasPos.Y - _drag.StartPoint.Y;

        if (!_drag.IsActive)
        {
            var screenPos = e.GetPosition(RootGrid);
            var screenDx = screenPos.X - _drag.StartScreenPoint.X;
            var screenDy = screenPos.Y - _drag.StartScreenPoint.Y;
            if (Math.Abs(screenDx) < ClickThreshold && Math.Abs(screenDy) < ClickThreshold)
                return;

            _drag.IsActive = true;
        }

        // 좌상단(0,0)만 고정. 우/하단은 캔버스가 동적으로 확장되므로 상한 클램프 없음.
        // 드래그된 노드들의 max right/bottom만 추적해서 경계 도달 시에만 캔버스 확장.
        double maxRight = 0, maxBottom = 0;
        foreach (var item in _drag.Items)
        {
            var newX = Math.Max(0, item.OriginX + dx);
            var newY = Math.Max(0, item.OriginY + dy);
            item.Node.X = newX;
            item.Node.Y = newY;

            var r = newX + item.Node.Width;
            var b = newY + item.Node.Height;
            if (r > maxRight) maxRight = r;
            if (b > maxBottom) maxBottom = b;
        }

        // 드래그 중에는 경계를 실제로 넘어설 때만 확장. 축소는 MouseUp에서 일괄 처리.
        EnsureCanvasFits(maxRight, maxBottom);
        UpdateDragArrows(_drag);
    }

    /// <summary>드래그 시작 시 영향받는 화살표를 골라 현재 경로를 스냅샷한다.
    /// 드래그 중에는 이 스냅샷을 평행이동만 시켜서 양방향 화살표가 갈라진 모양을 유지한다.</summary>
    private List<DragArrowSnapshot> BuildArrowSnapshots(IReadOnlyList<DragItem> dragItems)
    {
        var snapshots = new List<DragArrowSnapshot>();
        if (ActiveCanvasState is null) return snapshots;

        var byNodeId = dragItems.ToDictionary(d => d.Node.Id);

        foreach (var arrow in ActiveCanvasState.CanvasArrows)
        {
            var srcDragged = byNodeId.TryGetValue(arrow.SourceId, out var srcItem);
            var tgtDragged = byNodeId.TryGetValue(arrow.TargetId, out var tgtItem);
            if (!srcDragged && !tgtDragged) continue;

            arrow.BeginDragSnapshot();
            snapshots.Add(new DragArrowSnapshot(
                arrow,
                srcDragged ? srcItem : null,
                tgtDragged ? tgtItem : null));
        }

        return snapshots;
    }

    private void UpdateDragArrows(DragState drag)
    {
        if (VM is null) return;

        foreach (var snap in drag.ArrowSnapshots)
        {
            var srcDelta = ComputeDragDelta(snap.SourceDrag);
            var tgtDelta = ComputeDragDelta(snap.TargetDrag);
            snap.Arrow.ApplyDragTranslation(srcDelta, tgtDelta);
        }
    }

    private static Vector ComputeDragDelta(DragItem? item) =>
        item is null
            ? new Vector(0, 0)
            : new Vector(item.Node.X - item.OriginX, item.Node.Y - item.OriginY);

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isPanning && e.MiddleButton == MouseButtonState.Released)
        {
            _isPanning = false;
            RootGrid.ReleaseMouseCapture();
            RootGrid.Cursor = Cursors.Arrow;
            return;
        }

        if (_isBoxSelecting && e.ChangedButton == MouseButton.Left)
        {
            FinishBoxSelection(e.GetPosition(MainCanvas));
            e.Handled = true;
            return;
        }

        if (_arrowReconnect is not null && e.ChangedButton == MouseButton.Left)
        {
            FinishArrowReconnect(e.GetPosition(MainCanvas));
            e.Handled = true;
            return;
        }

        if (_drag is null) return;

        _dragElement?.ReleaseMouseCapture();

        if (VM is not null && _drag.IsActive)
        {
            var ctrlHeld = (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.None;
            var requests = new List<MoveEntityRequest>();

            foreach (var item in _drag.Items)
            {
                var moved =
                    Math.Abs(item.Node.X - item.OriginX) > 0.1 ||
                    Math.Abs(item.Node.Y - item.OriginY) > 0.1;

                if (!moved)
                    continue;

                var (finalX, finalY) = EntityKindRules.snapToGrid(item.Node.X, item.Node.Y, ctrlHeld);

                // 좌상단만 클램프. 우/하단은 캔버스가 함께 늘어남.
                finalX = Math.Max(0, finalX);
                finalY = Math.Max(0, finalY);

                item.Node.X = finalX;
                item.Node.Y = finalY;

                requests.Add(
                    new MoveEntityRequest(
                        item.Node.Id,
                        new Xywh((int)finalX, (int)finalY, (int)item.Node.Width, (int)item.Node.Height)));
            }

            if (requests.Count > 0)
                VM.TryMoveEntitiesFromCanvas(requests);
        }

        foreach (var snap in _drag.ArrowSnapshots)
            snap.Arrow.EndDragSnapshot();

        _drag = null;
        _dragElement = null;

        // 드래그 종료 후 비어있는 영역이 있으면 캔버스를 다시 줄임.
        RecalculateCanvasSize();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _arrowReconnect is not null)
        {
            CancelArrowReconnect();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _connectSource is not null)
        {
            CancelConnect();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete)
        {
            if (VM?.DeleteSelectedCommand.CanExecute(null) == true)
            {
                VM.DeleteSelectedCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }

        if (IsConnectShortcutKey(e.Key)
            && TryResolveConnectSourceEntityType(VM, out _))
        {
            StartConnectFromCurrentSelection();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (VM?.AutoLayoutCommand.CanExecute(null) == true)
                VM.AutoLayoutCommand.Execute(null);
            else if (VM is not null)
                VM.StatusText = "Open a canvas tab to run auto layout.";
            e.Handled = true;
        }
    }

    private EntityNode? FindNodeAt(Point point)
    {
        if (VM is null)
            return null;

        return ActiveCanvasState!.CanvasNodes.LastOrDefault(node =>
            point.X >= node.X && point.X <= node.X + node.Width
            && point.Y >= node.Y && point.Y <= node.Y + node.Height);
    }
}
