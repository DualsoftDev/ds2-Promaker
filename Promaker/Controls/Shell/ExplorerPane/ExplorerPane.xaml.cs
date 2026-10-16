using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Ds2.Editor;
using Promaker.ViewModels;

namespace Promaker.Controls;

public partial class ExplorerPane : UserControl
{
    private Point _treeDragStartPoint;
    private Point _disabledDragStart;
    private bool _treeDragCandidate;
    private EntityNode? _pendingTreeSelectionNode;
    private EntityNode? _treeMoveDropTarget;
    private TreePaneKind _pendingTreeSelectionPane = TreePaneKind.Control;
    private MainViewModel? _boundViewModel;
    private TreePaneKind _activeTreePane = TreePaneKind.Control;
    private readonly DispatcherTimer _searchRefreshTimer;

    public ObservableCollection<EntityNode> FilteredControlTreeRoots { get; } = [];
    public ObservableCollection<EntityNode> FilteredDeviceTreeRoots { get; } = [];
    public ObservableCollection<EntityNode> FilteredDisabledFlows { get; } = [];

    public ExplorerPane()
    {
        InitializeComponent();
        _searchRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _searchRefreshTimer.Tick += SearchRefreshTimer_Tick;
        DataContextChanged += ExplorerPane_DataContextChanged;
        Loaded   += ExplorerPane_Loaded;
        Unloaded += ExplorerPane_Unloaded;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private bool HasActiveSearch => !string.IsNullOrWhiteSpace(SearchBox.Text);

    private void SetActiveTreePane(TreePaneKind pane)
    {
        _activeTreePane = pane;
        ControlTree.Visibility = pane == TreePaneKind.Control ? Visibility.Visible : Visibility.Collapsed;
        DeviceTree.Visibility = pane == TreePaneKind.Device ? Visibility.Visible : Visibility.Collapsed;
        ControlTreeButton.IsChecked = pane == TreePaneKind.Control;
        DeviceTreeButton.IsChecked = pane == TreePaneKind.Device;

        if (ViewModel is not null)
            ViewModel.Selection.SetActiveTreePane(pane);
    }

    private void ExplorerPane_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        RebindViewModel(e.OldValue as MainViewModel, e.NewValue as MainViewModel);
    }

    private void ExplorerPane_Unloaded(object sender, RoutedEventArgs e) =>
        RebindViewModel(_boundViewModel, null);

    private void ExplorerPane_Loaded(object sender, RoutedEventArgs e)
    {
        var currentVm = ViewModel;
        if (currentVm is null) return;
        if (!ReferenceEquals(_boundViewModel, currentVm) || ControlTree.ItemsSource is null)
            RebindViewModel(_boundViewModel, currentVm);
    }

    private void HandleExplorerRebindRequested()
    {
        if (_boundViewModel is not null)
            RebindViewModel(_boundViewModel, _boundViewModel);
    }

    private void RebindViewModel(MainViewModel? oldVm, MainViewModel? newVm)
    {
        _searchRefreshTimer.Stop();

        if (oldVm is not null)
        {
            oldVm.ControlTreeRoots.CollectionChanged -= TreeRoots_CollectionChanged;
            oldVm.DeviceTreeRoots.CollectionChanged -= TreeRoots_CollectionChanged;
            oldVm.SearchResetRequested -= ClearSearch;
            oldVm.ExplorerRebindRequested -= HandleExplorerRebindRequested;
        }

        _boundViewModel = newVm;

        if (newVm is not null)
        {
            newVm.ControlTreeRoots.CollectionChanged += TreeRoots_CollectionChanged;
            newVm.DeviceTreeRoots.CollectionChanged += TreeRoots_CollectionChanged;
            newVm.SearchResetRequested += ClearSearch;
            newVm.ExplorerRebindRequested += HandleExplorerRebindRequested;
        }

        RefreshTreeItemsSource();
    }

    private void ClearSearch() => SearchBox.Clear();

    private void TreeRoots_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RefreshTreeItemsSource();

    private void ControlTreeButton_Click(object sender, RoutedEventArgs e) =>
        SetActiveTreePane(TreePaneKind.Control);

    private void DeviceTreeButton_Click(object sender, RoutedEventArgs e) =>
        SetActiveTreePane(TreePaneKind.Device);

    private void Tree_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeView tree)
            tree.Focus();
    }

    private void Tree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || ViewModel is null)
            return;

        if (ViewModel.DeleteSelectedCommand.CanExecute(null))
        {
            ViewModel.DeleteSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void Tree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not TreeView tree || tree.ContextMenu is not { } menu) return;
        var vm = ViewModel;
        var kind = ResolveContextMenuNode(tree, e.OriginalSource as DependencyObject)?.EntityType
                   ?? vm?.SelectedNode?.EntityType;
        var isDeviceTree = ReferenceEquals(tree, DeviceTree);
        var hasProject = vm?.HasProject == true;

        // 1차 노드 타입단계 메뉴 항목 표시/숨김
        foreach (var item in menu.Items)
        {
            var tag = item switch
            {
                MenuItem mi => mi.Tag as string,
                Separator sep => sep.Tag as string,
                _ => null
            };
            if (tag is null) continue;

            var visible = EntityKindRules.isMenuOperationAllowed(kind, tag, hasProject, isDeviceTree);

            if (item is FrameworkElement fe)
                fe.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        // 2차 연속/선행/후행 구분선 제거
        CollapseDuplicateSeparators(menu);
    }


    private static EntityNode? ResolveContextMenuNode(TreeView tree, DependencyObject? originalSource)
    {
        if (FindAncestor<TreeViewItem>(originalSource) is { DataContext: EntityNode node })
            return node;

        if (Mouse.DirectlyOver is DependencyObject hovered
            && ReferenceEquals(FindAncestor<TreeView>(hovered), tree)
            && FindAncestor<TreeViewItem>(hovered) is { DataContext: EntityNode hoveredNode })
            return hoveredNode;

        return null;
    }

    private static void CollapseDuplicateSeparators(ContextMenu menu)
    {
        FrameworkElement? lastVisibleSep = null;
        var hasVisibleItemSinceLastSep = false;

        foreach (var item in menu.Items)
        {
            if (item is not FrameworkElement fe) continue;
            if (fe.Visibility != Visibility.Visible) continue;

            if (item is Separator sep)
            {
                if (!hasVisibleItemSinceLastSep)
                    sep.Visibility = Visibility.Collapsed;
                else
                    lastVisibleSep = sep;
                hasVisibleItemSinceLastSep = false;
            }
            else
            {
                hasVisibleItemSinceLastSep = true;
                lastVisibleSep = null;
            }
        }

        // 마지막이 구분선이면 제거
        if (lastVisibleSep is not null)
            lastVisibleSep.Visibility = Visibility.Collapsed;
    }

    private void TreeViewItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is null) return;
        if (sender is not TreeViewItem item) return;
        if (!ReferenceEquals(item, FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject))) return;
        if (item.DataContext is not EntityNode node) return;

        const double doubleClickZoom = 0.52;

        // 더블클릭 → action 분류는 F# EditorNavigation 위임. UI dispatch 만 C# 에 남김.
        var action = EditorNavigation.ClassifyTreeDoubleClick(node.EntityType);
        if (action.IsFocusInParentCanvas)
            ViewModel.Canvas.OpenParentCanvasAndFocusNode(node.Id, node.EntityType, zoomOverride: 1.0);
        else if (action.IsOpenCanvasTab)
        {
            ViewModel.Canvas.OpenCanvasTab(node.Id, node.EntityType);
            Dispatcher.InvokeAsync(
                () => ViewModel?.Canvas.ApplyZoomTopLeftRequested?.Invoke(doubleClickZoom),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else if (action.IsEditApiDef)
            ViewModel.EditApiDefNode(node.Id);

        e.Handled = true;
    }

    private void ExpandAll_Click(object sender, RoutedEventArgs e)
        => SetExpandedRecursive(GetActiveTreeRoots(), true);

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
        => SetExpandedRecursive(GetActiveTreeRoots(), false);

    private IEnumerable<EntityNode>? GetActiveTreeRoots()
    {
        var vm = ViewModel;
        if (vm is null) return null;
        // 검색 중에는 화면에 filtered(클론) 트리가 떠 있다. 원본 roots 의 IsExpanded 를 바꿔봐야
        // 화면에 반영 안 되므로(펼치기/접기 먹통), 검색 중엔 filtered roots 를 대상으로 한다.
        if (HasActiveSearch)
            return _activeTreePane == TreePaneKind.Device
                ? FilteredDeviceTreeRoots
                : FilteredControlTreeRoots;
        return _activeTreePane == TreePaneKind.Device
            ? vm.DeviceTreeRoots
            : vm.ControlTreeRoots;
    }

    private static void SetExpandedRecursive(IEnumerable<EntityNode>? roots, bool expanded)
    {
        if (roots is null) return;
        foreach (var node in roots)
            SetExpandedNode(node, expanded);
    }

    private static void SetExpandedNode(EntityNode node, bool expanded)
    {
        node.IsExpanded = expanded;
        foreach (var child in node.Children)
            SetExpandedNode(child, expanded);
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T result) return result;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
