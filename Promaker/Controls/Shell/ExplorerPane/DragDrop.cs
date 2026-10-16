using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ds2.Core.Store;
using Promaker.ViewModels;

namespace Promaker.Controls;

public partial class ExplorerPane
{
    private const string TreeMoveCallNodeDataFormat = "TreeMoveCallNode";
    private const string TreeMoveCallIdsDataFormat = "TreeMoveCallIds";
    private const string TreeMoveWorkIdsDataFormat = "TreeMoveWorkIds";  // 트리 Work → 다른 Flow 이동/복사
    private const string FlowDragIdsFormat = "FlowDragIds";          // 트리 Flow → 비활성화 섹션(=비활성화) 또는 타 System 노드(=이동/복사)
    private const string FlowRestoreIdsFormat = "FlowRestoreIds";    // 비활성화 섹션 Flow → 트리(=복원)

    private void TreeViewItem_PreviewMouseMove_Drag(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        // Flow 노드 드래그 — 비활성화 섹션(비활성화) 또는 타 System 노드(이동, Ctrl=복사)
        if (sender is TreeViewItem { DataContext: EntityNode { EntityType: EntityKind.Flow } flowNode } flowItem
            && ReferenceEquals(flowItem, FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)))
        {
            var fd = e.GetPosition(null) - _disabledDragStart;
            if (Math.Abs(fd.X) >= SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(fd.Y) >= SystemParameters.MinimumVerticalDragDistance)
            {
                DragDrop.DoDragDrop(flowItem, new DataObject(FlowDragIdsFormat, SelectedFlowIdsForDrag(flowNode)),
                    DragDropEffects.Copy | DragDropEffects.Move);
                ClearTreeMoveDropTarget();
            }
            return;
        }

        if (!_treeDragCandidate) return;

        var pos = e.GetPosition(null);
        var diff = pos - _treeDragStartPoint;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (sender is not TreeViewItem { DataContext: EntityNode node } item) return;
        if (node.EntityType != EntityKind.Call && node.EntityType != EntityKind.Work) return;
        // PreviewMouseMove 는 터널링이라 부모 TreeViewItem 들도 이 핸들러를 받는다. 포인터 아래
        // 최근접 노드일 때만 drag 를 시작해야 부모 Work 가 자식 Call 의 drag 를 가로채지 않는다
        // (Work drag 추가 전에는 node!=Call return 이 부모를 걸렀으나, Work 허용 후 보호가 깨졌음).
        if (!ReferenceEquals(item, FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject))) return;

        ClearPendingTreeDragSelection();

        // 다중 선택 지원: selection 에 들어 있는 같은 종류 노드 모두 + (없으면) drag 시작 노드
        var selectedIds = ViewModel?.Selection.OrderedNodeSelection
            .Where(k => k.EntityKind == node.EntityType)
            .Select(k => k.Id)
            .ToList() ?? new System.Collections.Generic.List<Guid>();
        if (!selectedIds.Contains(node.Id))
            selectedIds = new System.Collections.Generic.List<Guid> { node.Id };

        var data = new DataObject();
        if (node.EntityType == EntityKind.Call)
        {
            data.SetData(ConditionDropHelper.DataFormat, node);
            data.SetData(TreeMoveCallNodeDataFormat, node);
            data.SetData(TreeMoveCallIdsDataFormat, selectedIds.ToArray());
        }
        else // Work → 다른 Flow 이동/복사
        {
            data.SetData(TreeMoveWorkIdsDataFormat, selectedIds.ToArray());
        }
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move);
        ClearTreeMoveDropTarget();
    }

    private void TreeViewItem_DragOver(object sender, DragEventArgs e)
    {
        // 비활성화 섹션에서 끌어온 Flow → 트리에 놓으면 복원
        if (e.Data.GetDataPresent(FlowRestoreIdsFormat))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        if (TryResolveCallMoveTarget(sender, e, out var sourceCallIds, out var targetWork)
            && ViewModel?.CanMoveCallsToWorkFromTree(sourceCallIds, targetWork.Id) == true)
        {
            SetTreeMoveDropTarget(targetWork);
            var copyMode = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            e.Effects = copyMode ? DragDropEffects.Copy : DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        // Work → 다른 Flow 이동/복사 (drop 대상 = Flow 노드)
        if (TryResolveWorkMoveTarget(sender, e, out var sourceWorkIds, out var targetFlow)
            && ViewModel?.CanMoveWorksToFlowFromTree(sourceWorkIds, targetFlow.Id) == true)
        {
            SetTreeMoveDropTarget(targetFlow);
            var copyMode = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            e.Effects = copyMode ? DragDropEffects.Copy : DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        // Flow → 다른 System 이동/복사 (drop 대상 = System 노드) — 멀티 PLC
        if (TryResolveFlowMoveTarget(sender, e, out var sourceFlowIds, out var targetSystem)
            && ViewModel?.CanMoveFlowsToSystemFromTree(sourceFlowIds, targetSystem.Id) == true)
        {
            SetTreeMoveDropTarget(targetSystem);
            var flowCopyMode = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            e.Effects = flowCopyMode ? DragDropEffects.Copy : DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        ClearTreeMoveDropTarget();
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void TreeViewItem_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is TreeViewItem { DataContext: EntityNode node } && ReferenceEquals(node, _treeMoveDropTarget))
            ClearTreeMoveDropTarget();
    }

    private void TreeViewItem_Drop(object sender, DragEventArgs e)
    {
        // 비활성화 섹션에서 끌어온 Flow → 복원
        if (e.Data.GetDataPresent(FlowRestoreIdsFormat) && e.Data.GetData(FlowRestoreIdsFormat) is Guid[] restoreIds)
        {
            ViewModel?.RestoreFlows(restoreIds);
            e.Handled = true;
            return;
        }

        // Work → 다른 Flow 이동/복사
        if (TryResolveWorkMoveTarget(sender, e, out var sourceWorkIds, out var targetFlow))
        {
            ClearTreeMoveDropTarget();
            var workCopyMode = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            if (ViewModel?.TryMoveWorksToFlowFromTree(sourceWorkIds, targetFlow.Id, workCopyMode) == true)
            {
                e.Effects = workCopyMode ? DragDropEffects.Copy : DragDropEffects.Move;
                e.Handled = true;
            }
            return;
        }

        // Flow → 다른 System 이동/복사
        if (TryResolveFlowMoveTarget(sender, e, out var sourceFlowIds, out var targetSystem))
        {
            ClearTreeMoveDropTarget();
            var flowCopyMode = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            if (ViewModel?.TryMoveFlowsToSystemFromTree(sourceFlowIds, targetSystem.Id, flowCopyMode) == true)
            {
                e.Effects = flowCopyMode ? DragDropEffects.Copy : DragDropEffects.Move;
                e.Handled = true;
            }
            return;
        }

        if (!TryResolveCallMoveTarget(sender, e, out var sourceCallIds, out var targetWork))
            return;

        ClearTreeMoveDropTarget();
        var copyMode = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (ViewModel?.TryMoveCallsToWorkFromTree(sourceCallIds, targetWork.Id, copyMode) == true)
        {
            e.Effects = copyMode ? DragDropEffects.Copy : DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private static bool TryResolveCallMoveTarget(
        object sender,
        DragEventArgs e,
        out IReadOnlyList<Guid> sourceCallIds,
        out EntityNode targetWork)
    {
        sourceCallIds = Array.Empty<Guid>();
        targetWork = null!;

        if (sender is not TreeViewItem { DataContext: EntityNode { EntityType: EntityKind.Work } workNode } item)
            return false;
        if (!ReferenceEquals(item, FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)))
            return false;

        // 다중: TreeMoveCallIdsDataFormat 우선, 없으면 단일 fallback
        if (e.Data.GetDataPresent(TreeMoveCallIdsDataFormat)
            && e.Data.GetData(TreeMoveCallIdsDataFormat) is Guid[] ids && ids.Length > 0)
        {
            sourceCallIds = ids;
            targetWork = workNode;
            return true;
        }
        if (e.Data.GetDataPresent(TreeMoveCallNodeDataFormat)
            && e.Data.GetData(TreeMoveCallNodeDataFormat) is EntityNode { EntityType: EntityKind.Call } draggedCall)
        {
            sourceCallIds = new[] { draggedCall.Id };
            targetWork = workNode;
            return true;
        }
        return false;
    }

    /// <summary>Work 드래그의 drop 대상은 Flow 노드 — sender 가 Flow 이고 TreeMoveWorkIds 페이로드가 있으면 resolve.</summary>
    private static bool TryResolveWorkMoveTarget(
        object sender,
        DragEventArgs e,
        out IReadOnlyList<Guid> sourceWorkIds,
        out EntityNode targetFlow)
    {
        sourceWorkIds = Array.Empty<Guid>();
        targetFlow = null!;

        if (sender is not TreeViewItem { DataContext: EntityNode { EntityType: EntityKind.Flow } flowNode } item)
            return false;
        if (!ReferenceEquals(item, FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)))
            return false;

        if (e.Data.GetDataPresent(TreeMoveWorkIdsDataFormat)
            && e.Data.GetData(TreeMoveWorkIdsDataFormat) is Guid[] ids && ids.Length > 0)
        {
            sourceWorkIds = ids;
            targetFlow = flowNode;
            return true;
        }
        return false;
    }

    /// <summary>Flow 드래그의 drop 대상은 System 노드 — sender 가 System 이고 FlowDragIds 페이로드가 있으면 resolve.</summary>
    private static bool TryResolveFlowMoveTarget(
        object sender,
        DragEventArgs e,
        out IReadOnlyList<Guid> sourceFlowIds,
        out EntityNode targetSystem)
    {
        sourceFlowIds = Array.Empty<Guid>();
        targetSystem = null!;

        if (sender is not TreeViewItem { DataContext: EntityNode { EntityType: EntityKind.System } systemNode } item)
            return false;
        if (!ReferenceEquals(item, FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)))
            return false;

        if (e.Data.GetDataPresent(FlowDragIdsFormat)
            && e.Data.GetData(FlowDragIdsFormat) is Guid[] ids && ids.Length > 0)
        {
            sourceFlowIds = ids;
            targetSystem = systemNode;
            return true;
        }
        return false;
    }

    private void SetTreeMoveDropTarget(EntityNode node)
    {
        if (ReferenceEquals(_treeMoveDropTarget, node))
            return;
        ClearTreeMoveDropTarget();
        _treeMoveDropTarget = node;
        node.IsDropTarget = true;
    }

    private void ClearTreeMoveDropTarget()
    {
        if (_treeMoveDropTarget is not null)
            _treeMoveDropTarget.IsDropTarget = false;
        _treeMoveDropTarget = null;
    }

    // ─── 비활성화 섹션 드래그&드롭 ───────────────────────────────────────────
    // 트리 Flow → 섹션 = 비활성화 / 섹션 Flow → 트리 = 복원. 편집 의미는 ViewModel(F#) 위임.
    private Guid[] SelectedFlowIdsForDrag(EntityNode node)
    {
        var ids = ViewModel?.Selection.OrderedNodeSelection
            .Where(k => k.EntityKind == EntityKind.Flow)
            .Select(k => k.Id)
            .ToList() ?? new List<Guid>();
        if (!ids.Contains(node.Id))
            ids = new List<Guid> { node.Id };
        return ids.ToArray();
    }

    private void DisabledSection_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(FlowDragIdsFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void DisabledSection_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(FlowDragIdsFormat) && e.Data.GetData(FlowDragIdsFormat) is Guid[] ids)
        {
            ViewModel?.DisableFlows(ids);
            e.Handled = true;
        }
    }

    private void DisabledList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { _disabledDragStart = e.GetPosition(null); }
        catch { _disabledDragStart = default; }
    }

    private void DisabledList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(null) - _disabledDragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        var ids = DisabledList.SelectedItems.OfType<EntityNode>().Select(n => n.Id).ToArray();
        if (ids.Length == 0) return;
        DragDrop.DoDragDrop(DisabledList, new DataObject(FlowRestoreIdsFormat, ids), DragDropEffects.Move);
    }
}
