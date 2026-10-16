using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Ds2.Core.Store;
using Promaker.ViewModels;

namespace Promaker.Controls;

public partial class ExplorerPane
{
    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        => HandleTreeSelectionChanged(ResolveTreePane(sender), e.NewValue);

    private void TreeViewItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem item) return;

        // Expander(셰브론) 클릭은 WPF 기본 토글에 맡긴다.
        // 선택된 부모 노드에서 e.Handled=true 가 걸리면 ToggleButton 이 클릭을 받지 못해 펼침/접힘이 실패한다.
        if (IsClickOnExpander(e.OriginalSource as DependencyObject, item))
        {
            ClearPendingTreeDragSelection();
            return;
        }

        var pane = ResolveTreePane(sender);
        var ctrlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (item.DataContext is EntityNode node
            && (node.EntityType == EntityKind.Call || node.EntityType == EntityKind.Work)
            && !ctrlPressed
            && !shiftPressed)
        {
            _treeDragCandidate = true;
            _pendingTreeSelectionNode = node;
            _pendingTreeSelectionPane = pane;
            try { _treeDragStartPoint = e.GetPosition(null); }
            catch { _treeDragStartPoint = default; }

            // 선택 확정은 drag 시작(다중 유지) 또는 mouse-up(드래그 안 했을 때 단일 선택)에 위임한다.
            // 여기서 HandleTreeItemMouseDown 으로 단일 리셋하면, 다중 선택해 둔 Call 을 끌려고
            // 누르는 순간 선택이 1개로 줄어들어 다중 드래그가 깨진다.
            ViewModel?.Selection.SetActiveTreePane(pane);
            return;
        }

        // Flow 노드: 비활성화 섹션으로 드래그하기 위한 시작점만 기록 (선택/candidate 는 기존 경로 유지).
        if (item.DataContext is EntityNode { EntityType: EntityKind.Flow } && !ctrlPressed && !shiftPressed)
        {
            try { _disabledDragStart = e.GetPosition(null); }
            catch { _disabledDragStart = default; }
        }

        ClearPendingTreeDragSelection();
        HandleTreeItemMouseDown(pane, sender, e, requireModifiers: true);
    }

    private static bool IsClickOnExpander(DependencyObject? source, DependencyObject stopAt)
    {
        var current = source;
        while (current is not null && !ReferenceEquals(current, stopAt))
        {
            if (current is ToggleButton) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void TreeViewItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        ClearPendingTreeDragSelection();

        // 이미 선택된 노드를 우클릭한 경우 선택 변경하지 않음 (다중 선택 유지)
        if (sender is TreeViewItem { DataContext: EntityNode node } && node.IsTreeSelected)
        {
            e.Handled = true;
            return;
        }
        HandleTreeItemMouseDown(ResolveTreePane(sender), sender, e, requireModifiers: false);
    }

    private void TreeViewItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_treeDragCandidate || _pendingTreeSelectionNode is null || ViewModel is null)
            return;

        if (sender is not TreeViewItem { DataContext: EntityNode node } item)
            return;
        if (!ReferenceEquals(item, FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)))
            return;
        if (!ReferenceEquals(node, _pendingTreeSelectionNode))
            return;

        ViewModel.Selection.SetActiveTreePane(_pendingTreeSelectionPane);
        // Call 은 drag-candidate 경로라 HandleTreeSelectionChanged 를 안 타므로 여기서 선택 확정 + 검색 동기화.
        SelectNodeFromTreeAndSyncSearch(node, ctrlPressed: false, shiftPressed: false);
        ClearPendingTreeDragSelection();
        e.Handled = true;
    }

    private void HandleTreeSelectionChanged(TreePaneKind pane, object? newValue)
    {
        if (ViewModel is null) return;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != ModifierKeys.None)
            return;

        if (newValue is not EntityNode node)
            return;

        if (_activeTreePane != pane)
            SetActiveTreePane(pane);

        ViewModel.Selection.SetActiveTreePane(pane);

        if (_treeDragCandidate
            && _pendingTreeSelectionNode is not null
            && ReferenceEquals(node, _pendingTreeSelectionNode))
            return;

        SelectNodeFromTreeAndSyncSearch(node, ctrlPressed: false, shiftPressed: false);

        if (HasActiveSearch && node.EntityType is EntityKind.System or EntityKind.Flow or EntityKind.Work)
            ViewModel.Canvas.OpenParentCanvasAndFocusNode(node.Id, node.EntityType);
    }

    private void HandleTreeItemMouseDown(TreePaneKind pane, object sender, MouseButtonEventArgs e, bool requireModifiers)
    {
        if (ViewModel is null) return;
        ViewModel.Selection.SetActiveTreePane(pane);
        if (sender is not TreeViewItem { DataContext: EntityNode node } item) return;
        if (!ReferenceEquals(item, FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject))) return;

        var ctrlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        if (requireModifiers && !ctrlPressed && !shiftPressed)
        {
            // ClickCount>=2 (더블클릭의 두 번째 다운)에서 Handled=true 가 걸리면
            // Control.HandleDoubleClick 클래스 핸들러가 MouseDoubleClick 을 발생시키지 못한다.
            if ((node.IsTreeSelected || item.IsSelected) && e.ClickCount == 1)
            {
                SelectNodeFromTreeAndSyncSearch(node, ctrlPressed: false, shiftPressed: false);
                e.Handled = true;
            }

            return;
        }

        SelectNodeFromTreeAndSyncSearch(node, ctrlPressed, shiftPressed);
        if (e.ClickCount == 1)
            e.Handled = true;
    }

    private TreePaneKind ResolveTreePane(object sender)
    {
        if (sender is TreeView treeView)
            return ReferenceEquals(treeView, DeviceTree) ? TreePaneKind.Device : TreePaneKind.Control;

        if (sender is DependencyObject dependencyObject && ReferenceEquals(FindAncestor<TreeView>(dependencyObject), DeviceTree))
            return TreePaneKind.Device;

        return TreePaneKind.Control;
    }

    private void ClearPendingTreeDragSelection()
    {
        _treeDragCandidate = false;
        _pendingTreeSelectionNode = null;
    }

    // 트리 선택을 적용하고, 검색 중이면 화면에 떠 있는 클론 트리에 선택 하이라이트를 동기화한다.
    // (검색 중에는 원본이 아니라 클론 노드가 표시되므로 IsTreeSelected 동기화가 없으면 하이라이트가 안 보임)
    private void SelectNodeFromTreeAndSyncSearch(EntityNode node, bool ctrlPressed, bool shiftPressed)
    {
        if (ViewModel is null) return;
        ViewModel.Selection.SelectNodeFromTree(node, ctrlPressed, shiftPressed);
        if (HasActiveSearch)
            RefreshFilteredSelectionState();
    }

    private void RefreshFilteredSelectionState()
    {
        if (_boundViewModel is null || !HasActiveSearch)
            return;

        var controlIndex = TreeNodeSearch.EnumerateNodes(_boundViewModel.ControlTreeRoots)
            .ToDictionary(NodeKey);
        var deviceIndex = TreeNodeSearch.EnumerateNodes(_boundViewModel.DeviceTreeRoots)
            .ToDictionary(NodeKey);

        SyncFilteredNodes(FilteredControlTreeRoots, controlIndex);
        SyncFilteredNodes(FilteredDeviceTreeRoots, deviceIndex);
    }

    // 선택·경고·시뮬 상태만 원본에서 옮긴다. 펼침은 검색어가 바뀔 때(CloneFilteredNode) 정하고 이후엔 사용자 조작을 따른다 —
    // 예전엔 여기서 자식 있는 결과를 매번 다시 펼쳐, 검색 중 전체 접기 뒤 하나만 선택해도 전부 펼쳐졌다(#251).
    private static void SyncFilteredNodes(
        IEnumerable<EntityNode> filteredNodes,
        IReadOnlyDictionary<string, EntityNode> sourceIndex)
    {
        foreach (var filteredNode in filteredNodes)
        {
            if (sourceIndex.TryGetValue(NodeKey(filteredNode), out var sourceNode))
            {
                filteredNode.IsTreeSelected = sourceNode.IsTreeSelected;
                filteredNode.SelectionOrder = sourceNode.SelectionOrder;
                filteredNode.IsWarning = sourceNode.IsWarning;
                filteredNode.SimState = sourceNode.SimState;
                filteredNode.SimTokenDisplay = sourceNode.SimTokenDisplay;
            }

            SyncFilteredNodes(filteredNode.Children, sourceIndex);
        }
    }

    private static string NodeKey(EntityNode node) =>
        $"{(int)node.EntityType}:{node.Id}";
}
