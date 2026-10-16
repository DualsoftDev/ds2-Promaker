using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.FSharp.Core;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void DeleteSelected()
    {
        if (Selection.OrderedArrowSelection.Count > 0)
        {
            if (!GuardArrowEditByRuntimeMode("Arrow 삭제"))
                return;
            var count = Selection.OrderedArrowSelection.Count;
            if (TryEditorAction(() => _store.RemoveArrows(Selection.OrderedArrowSelection)))
            {
                Selection.ClearArrowSelection();
                StatusText = $"Deleted {count} arrow(s).";
            }
            return;
        }

        if (Selection.OrderedNodeSelection.Count > 0)
        {
            if (!GuardSimulationSemanticEdit("노드 삭제"))
                return;

            var selections = Selection.OrderedNodeSelection
                .Where(k => k.EntityKind != EntityKind.Project)
                .Select(k => Tuple.Create(k.EntityKind, k.Id))
                .ToList();
            if (selections.Count > 0 && TryEditorAction(() => _store.RemoveEntities(selections)))
                StatusText = $"Deleted {selections.Count} item(s).";
            return;
        }

        if (SelectedNode is { EntityType: not EntityKind.Project } node)
        {
            if (!GuardSimulationSemanticEdit("노드 삭제"))
                return;

            if (TryEditorAction(
                    () => _store.RemoveEntities(new[] { Tuple.Create(node.EntityType, node.Id) })))
                StatusText = "Deleted 1 item.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void AddReferenceWork()
    {
        if (!GuardSimulationSemanticEdit("레퍼런스 Work 추가"))
            return;

        if (SelectedNode is not { EntityType: EntityKind.Work } node) return;
        TryEditorAction(() => _store.AddReferenceWork(node.Id));
    }

    [RelayCommand(CanExecute = nameof(HasProject))]
    private void AddReferenceCall()
    {
        if (!GuardSimulationSemanticEdit("레퍼런스 Call 추가"))
            return;

        if (SelectedNode is not { EntityType: EntityKind.Call } node) return;
        TryEditorAction(() => _store.AddReferenceCall(node.Id));
    }

    [RelayCommand]
    private void RenameSelected(string newName)
    {
        if (SelectedNode is null)
            return;

        if (string.IsNullOrWhiteSpace(newName))
        {
            StatusText = "Name cannot be empty.";
            return;
        }

        // 이름 정책 관문 — 속성 패널 ApplyName·트리 rename 이 전부 이 커맨드로 모이므로 여기 한 곳.
        if (!Services.Naming.InputPolicy.TryConfirm(SelectedNode.EntityType, newName, out newName))
            return;

        if (!GuardSimulationSemanticEdit("이름 변경"))
            return;

        // #229: 디바이스(Passive) Action Work 의 이름 변경은 SSOT(ApiDef)로 cascade 되도록 라우팅.
        // (RenameEntitySmart 가 Work↔ApiDef 여부를 판정 — 일반 Work/기타 엔티티는 기존 RenameEntity 그대로.)
        if (TryEditorAction(
                () => _store.RenameEntitySmart(SelectedNode.Id, SelectedNode.EntityType, newName)))
            StatusText = $"Renamed to '{newName}'.";
    }

    /// <summary>Device 탭 System(디바이스) 노드 → 디바이스/Action 일괄 이름 변경 다이얼로그.
    /// [확인] 시 디바이스명 + 변경된 Action 이름들을 단일 트랜잭션(RenameDeviceBatch = Undo 1단계)으로
    /// cascade 적용한다. 영향 미리보기/검증은 P2 SSOT(CollectRenameImpact)를 다이얼로그가 공유한다.</summary>
    private void OpenDeviceRenameDialog(Guid systemId, string deviceName)
    {
        if (!GuardSimulationSemanticEdit("디바이스 이름 변경"))
            return;

        // ApiDef(Action) 공급원 = P2/Panel SSOT. FSharpList → IReadOnlyList 변환(다이얼로그 생성자 계약).
        if (!TryEditorFunc(
                () => _store.GetApiDefsForSystem(systemId),
                out var apiDefs,
                fallback: Microsoft.FSharp.Collections.ListModule.Empty<ApiDefPanelItem>()))
            return;

        var dialog = new Promaker.Dialogs.DeviceRenameDialog(_store, systemId, deviceName, apiDefs.ToList());
        if (_dialogService.ShowDialog(dialog) != true)
            return;

        var newDeviceName = dialog.NewDeviceNameOption;
        var apiRenames = dialog.BuildApiRenames();

        // 변경 사항이 전혀 없으면(디바이스명·Action명 모두 그대로) no-op.
        if (FSharpOption<string>.get_IsNone(newDeviceName) && apiRenames.IsEmpty)
        {
            StatusText = "변경된 이름이 없습니다.";
            return;
        }

        if (TryEditorFunc(
                () => _store.RenameDeviceBatch(systemId, newDeviceName, apiRenames),
                out int changedCount,
                fallback: 0))
        {
            StatusText = changedCount > 0
                ? $"디바이스 일괄 이름 변경: {changedCount}건 적용됨"
                : "적용할 변경이 없습니다.";
        }
    }

    /// <summary>Control 트리 Call 노드 → 그 Call 이 참조하는 디바이스의 일괄 이름변경 다이얼로그로 유도.
    /// (Call 이름 뒷부분=Action명 은 속성창에서 read-only 라 정식 변경 경로로 안내.)
    /// dummy/orphan(디바이스 미해결) Call 은 조용히 무시 — 현행 속성창 이동만 유효.</summary>
    private void OpenDeviceRenameDialogForCall(Guid callId)
    {
        if (!TryEditorFunc(
                () => _store.TryGetDeviceSystemOfCall(callId),
                out var deviceOpt,
                fallback: FSharpOption<DsSystem>.None))
            return;

        if (FSharpOption<DsSystem>.get_IsSome(deviceOpt))
            OpenDeviceRenameDialog(deviceOpt.Value.Id, deviceOpt.Value.Name);
    }

    [RelayCommand(CanExecute = nameof(CanCopySelected))]
    private void CopySelected()
    {
        var candidates = Selection.OrderedNodeSelection.Count > 0
            ? Selection.OrderedNodeSelection
            : SelectedNode is { } single
                ? [new SelectionKey(single.Id, single.EntityType)]
                : (IReadOnlyList<SelectionKey>)[];

        // System/디바이스 노드 → OS 클립보드 패키지(인스턴스 간 복사) 경로.
        // 내부 클립보드(Flow/Work/Call) 경로와 별개 — isCopyableEntityKind 는 그대로 유지(무회귀).
        if (candidates.Count > 0 && candidates.All(k => k.EntityKind == EntityKind.System))
        {
            CopySystemsToOsClipboard(candidates);
            return;
        }

        if (!TryEditorFunc(
                () => _store.ValidateCopySelection(candidates),
                out CopyValidationResult result,
                fallback: CopyValidationResult.NothingToCopy))
            return;

        if (result.IsMixedTypes)
        {
            _dialogService.ShowWarning("같은 종류의 항목만 복사할 수 있습니다.");
            return;
        }
        if (result.IsMixedParents)
        {
            _dialogService.ShowWarning("서로 다른 위치에 있는 항목은 함께 복사할 수 없습니다.");
            return;
        }
        if (result is not CopyValidationResult.Ok ok)
        {
            StatusText = "Nothing to copy.";
            return;
        }

        var validated = ok.Item;
        _clipboardSelection.Clear();
        _clipboardIsCut = false;
        _pasteCount = 0;
        foreach (var key in validated)
            _clipboardSelection.Add(key);
        RememberInternalCopy();

        // Copy 는 cut 아님 — 이전 cut visual 이 남아있다면 clear
        Selection.ApplyCutPendingVisuals([]);

        StatusText = $"Copied {_clipboardSelection.Count} {validated[0].EntityKind}(s).";
        RefreshEditorCommandStates();
    }

    private bool CanCutSelected()
    {
        if (!CanCopySelected())
            return false;
        // Cut(이동)은 Call/Work/Flow 지원. Flow 는 MoveFlowsToSystem 재부모화(id 보존)로 이동.
        var candidates = Selection.OrderedNodeSelection.Count > 0
            ? Selection.OrderedNodeSelection
            : SelectedNode is { } single
                ? new[] { new SelectionKey(single.Id, single.EntityType) }
                : Array.Empty<SelectionKey>();
        return candidates.Count > 0
            && candidates.All(k => k.EntityKind == EntityKind.Call
                                || k.EntityKind == EntityKind.Work
                                || k.EntityKind == EntityKind.Flow);
    }

    [RelayCommand(CanExecute = nameof(CanCutSelected))]
    private void CutSelected()
    {
        var candidates = Selection.OrderedNodeSelection.Count > 0
            ? Selection.OrderedNodeSelection
            : SelectedNode is { } single
                ? [new SelectionKey(single.Id, single.EntityType)]
                : (IReadOnlyList<SelectionKey>)[];

        if (!TryEditorFunc(
                () => _store.ValidateCopySelection(candidates),
                out CopyValidationResult result,
                fallback: CopyValidationResult.NothingToCopy))
            return;

        if (result.IsMixedTypes)
        {
            _dialogService.ShowWarning("같은 종류의 항목만 이동할 수 있습니다.");
            return;
        }
        if (result.IsMixedParents)
        {
            _dialogService.ShowWarning("서로 다른 위치에 있는 항목은 함께 이동할 수 없습니다.");
            return;
        }
        if (result is not CopyValidationResult.Ok ok)
        {
            StatusText = "Nothing to cut.";
            return;
        }

        var validated = ok.Item;
        if (validated[0].EntityKind != EntityKind.Call
            && validated[0].EntityKind != EntityKind.Work
            && validated[0].EntityKind != EntityKind.Flow)
        {
            _dialogService.ShowWarning("Cut(이동) 은 Call/Work/Flow 만 지원합니다.");
            return;
        }

        _clipboardSelection.Clear();
        _clipboardIsCut = true;
        _pasteCount = 0;
        foreach (var key in validated)
            _clipboardSelection.Add(key);
        RememberInternalCopy();

        // Cut visual: 클립보드 entry 들을 디밍/반투명 표시
        Selection.ApplyCutPendingVisuals(_clipboardSelection);

        StatusText = $"Cut {_clipboardSelection.Count} {validated[0].EntityKind}(s) — Ctrl+V to move.";
        RefreshEditorCommandStates();
    }

    private bool CanCancelCut() => _clipboardIsCut && _clipboardSelection.Count > 0;

    /// <summary>Cut(잘라내기) 대기 상태 취소 — clipboard 비우고 흐림(IsCutPending) 시각 복원.
    /// ESC 또는 (paste 전) Ctrl+Z 로 호출. Copy clipboard 는 visual 이 없어 대상 아님.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelCut))]
    private void CancelCut()
    {
        if (!_clipboardIsCut) return;
        _clipboardSelection.Clear();
        _clipboardIsCut = false;
        Selection.ApplyCutPendingVisuals([]);
        StatusText = "잘라내기 취소됨.";
        RefreshEditorCommandStates();
    }
}
