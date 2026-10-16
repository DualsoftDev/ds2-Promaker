using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.Services;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// target Work 의 Flow 가 clipboard Call 들의 *어느 source Flow 와도 다른지*. 다르면 cross-flow paste/move 분기 → 다이얼로그 필요.
    /// </summary>
    private bool IsTargetInDifferentFlow((EntityKind EntityType, Guid EntityId) target)
    {
        var targetWorkOpt = StoreHierarchyQueries.resolveTarget(_store, EntityKind.Work, target.EntityType, target.EntityId);
        if (targetWorkOpt is null) return false;
        var targetWorkId = targetWorkOpt.Value;
        if (!_store.WorksReadOnly.TryGetValue(targetWorkId, out var targetWork)) return false;
        var targetFlowId = targetWork.ParentId;
        foreach (var key in _clipboardSelection)
        {
            if (!_store.CallsReadOnly.TryGetValue(key.Id, out var call)) continue;
            if (!_store.WorksReadOnly.TryGetValue(call.ParentId, out var sourceWork)) continue;
            if (sourceWork.ParentId != targetFlowId) return true;
        }
        return false;
    }

    [RelayCommand(CanExecute = nameof(CanPasteCopied))]
    private void PasteCopied()
    {
        if (!GuardSimulationSemanticEdit("붙여넣기"))
            return;

        // 내부 클립보드가 비었거나, 그 뒤에 다른 창에서 System 을 복사했으면 시스템 패키지를 붙인다(#249).
        if (_clipboardSelection.Count == 0 || IsSystemPackageNewerThanInternalCopy())
        {
            if (TryPasteSystemPackage())
                return;
            if (_clipboardSelection.Count == 0)
            {
                StatusText = "Clipboard is empty.";
                return;
            }
        }

        // Cut clipboard: source 엔티티들이 그 동안 삭제되었는지 검증. 사라졌으면 자동 clear + 안내.
        // Cut 은 Call/Work/Flow 지원 — 종류에 맞는 store 맵으로 확인 (Work 를 CallsReadOnly 로
        // 보면 항상 missing 으로 판정돼 Work cut/paste 가 통째로 막힌다 — 회귀 가드).
        if (_clipboardIsCut)
        {
            var cutKind = _clipboardSelection[0].EntityKind;
            var missing = _clipboardSelection.Where(k =>
                cutKind == EntityKind.Work ? !_store.WorksReadOnly.ContainsKey(k.Id)
                : cutKind == EntityKind.Flow ? !_store.FlowsReadOnly.ContainsKey(k.Id)
                : !_store.CallsReadOnly.ContainsKey(k.Id)).ToList();
            if (missing.Count > 0)
            {
                _clipboardSelection.Clear();
                _clipboardIsCut = false;
                Selection.ApplyCutPendingVisuals([]);
                StatusText = "Cut 대상이 그 사이 삭제되어 클립보드를 비웠습니다.";
                RefreshEditorCommandStates();
                return;
            }
        }

        var target = ResolvePasteTarget();
        if (!target.HasValue)
        {
            StatusText = "Select a paste target or open a canvas tab.";
            return;
        }

        var batchType = _clipboardSelection[0].EntityKind;
        // 붙일 수 없는 대상이면 무엇을 골라야 하는지 알린다 — 종류별 규칙은 F#(PasteTargetRequirement).
        var requiredTarget = _store.PasteTargetRequirement(batchType, target.Value.EntityType, target.Value.EntityId);
        if (requiredTarget is not null)
        {
            _dialogService.ShowWarning($"{requiredTarget} 또는 그 아래 항목을 붙여넣기 대상으로 선택하세요.");
            return;
        }

        if (batchType == EntityKind.Flow)
        {
            if (_clipboardIsCut)
                _ = RunBusyAsync("Flow 이동 중...", () => DispatchFlowMove(target.Value));
            else
                PasteFlowsWithRename(target.Value);   // 이름 프롬프트 후 내부에서 busy 래핑
            return;
        }

        // Call 분기 — cross-flow 일 때는 device system 처리 모드 다이얼로그 분기
        if (batchType == EntityKind.Call && IsTargetInDifferentFlow(target.Value))
        {
            DispatchCallAcrossFlows(target.Value);
            return;
        }

        // Cut + same flow (다른 Work) → MoveCallsToWork (1 undo step 일괄 이동)
        if (batchType == EntityKind.Call && _clipboardIsCut)
        {
            _ = RunBusyAsync("Call 이동 중...", () => DispatchCallMoveSameFlow(target.Value));
            return;
        }

        // Cut Work → target Flow 로 이동 (MoveWorksToFlow, 1 undo step)
        if (batchType == EntityKind.Work && _clipboardIsCut)
        {
            DispatchWorkMove(target.Value);
            return;
        }

        var pasteIndex = _pasteCount * _clipboardSelection.Count;
        _ = RunBusyAsync($"{batchType} 붙여넣는 중...", () =>
        {
            if (!TryEditorFunc(
                    () => _store.PasteEntities(
                        batchType,
                        _clipboardSelection.Select(k => k.Id),
                        target.Value.EntityType,
                        target.Value.EntityId,
                        pasteIndex),
                    out PasteResult pasteResult,
                    fallback: PasteResult.NewOk(Microsoft.FSharp.Collections.ListModule.Empty<Guid>())))
                return;

            if (pasteResult is PasteResult.Blocked blocked)
            {
                if (blocked.Item.IsSameWorkPaste)
                    _dialogService.ShowWarning("같은 Work 내의 Call은 복사할 수 없습니다.");
                else if (blocked.Item.IsDuplicateCallInWork)
                    _dialogService.ShowWarning("대상 Work에 이미 동일한 이름의 Call이 존재합니다.");
                return;
            }

            var pastedIds = ((PasteResult.Ok)pasteResult).Item;
            _pasteCount++;
            ApplyPasteSelection(pastedIds, $"Pasted {pastedIds.Length} {batchType}(s).");
        });
    }

    /// <summary>
    /// Cross-flow Call paste/move — device system 처리 모드 다이얼로그 → F# 호출.
    /// Cut clipboard 면 MoveCallsAcrossFlow, copy 면 PasteEntitiesWithMode.
    /// </summary>
    private void DispatchCallAcrossFlows((EntityKind EntityType, Guid EntityId) target)
    {
        var targetWorkOpt = StoreHierarchyQueries.resolveTarget(_store, EntityKind.Work, target.EntityType, target.EntityId);
        if (targetWorkOpt is null) return;
        var targetWorkId = targetWorkOpt.Value;

        var mode = _dialogService.PromptCrossFlowDeviceMode(
            new CrossFlowDeviceModePromptContext(
                CallCount: _clipboardSelection.Count,
                IsCut: _clipboardIsCut,
                Store: _store,
                SourceCallIds: _clipboardSelection.Select(k => k.Id).ToArray(),
                TargetFlowId: FlowOfWork(targetWorkId)));
        if (mode is null) return;  // cancelled

        var resolvedMode = mode;
        // 디바이스 모드 다이얼로그를 받은 뒤의 store 작업만 busy — 오버레이/모달 겹침 방지.
        _ = RunBusyAsync("Call 붙여넣는 중...", () => ExecuteCallAcrossFlows(target, resolvedMode));
    }

    private void ExecuteCallAcrossFlows(
        (EntityKind EntityType, Guid EntityId) target, CrossFlowDeviceMode resolvedMode)
    {
        var targetWorkOpt = StoreHierarchyQueries.resolveTarget(_store, EntityKind.Work, target.EntityType, target.EntityId);
        if (targetWorkOpt is null) return;
        var targetWorkId = targetWorkOpt.Value;

        if (_clipboardIsCut)
        {
            if (!TryEditorFunc(
                    () => _store.MoveCallsAcrossFlow(
                        _clipboardSelection.Select(k => k.Id), targetWorkId, resolvedMode),
                    out CrossFlowMoveResult result,
                    fallback: CrossFlowMoveResult.NewMoved(Microsoft.FSharp.Collections.ListModule.Empty<Guid>())))
                return;
            if (result is CrossFlowMoveResult.Blocked blocked)
            {
                ShowMoveValidationBlocked(blocked.Item);
                return;
            }
            var movedIds = ((CrossFlowMoveResult.Moved)result).newCallIds;
            _clipboardSelection.Clear();
            _clipboardIsCut = false;
            Selection.ApplyCutPendingVisuals([]);
            ApplyPasteSelection(movedIds, $"Moved {movedIds.Length} Call(s) across Flow ({resolvedMode}).");
        }
        else
        {
            var pasteIndex = _pasteCount * _clipboardSelection.Count;
            if (!TryEditorFunc(
                    () => _store.PasteEntitiesWithMode(
                        _clipboardSelection.Select(k => k.Id), target.EntityType, target.EntityId, pasteIndex, resolvedMode),
                    out PasteResult result,
                    fallback: PasteResult.NewOk(Microsoft.FSharp.Collections.ListModule.Empty<Guid>())))
                return;
            if (result is PasteResult.Blocked blocked)
            {
                if (blocked.Item.IsSameWorkPaste)
                    _dialogService.ShowWarning("같은 Work 내의 Call은 복사할 수 없습니다.");
                else if (blocked.Item.IsDuplicateCallInWork)
                    _dialogService.ShowWarning("대상 Work에 이미 동일한 이름의 Call이 존재합니다.");
                return;
            }
            var pastedIds = ((PasteResult.Ok)result).Item;
            _pasteCount++;
            ApplyPasteSelection(pastedIds, $"Pasted {pastedIds.Length} Call(s) across Flow ({resolvedMode}).");
        }
    }

    /// <summary>Cut Call + same flow 의 다른 Work — same-flow MoveCallsToWork 로 1 undo step 일괄 이동.</summary>
    private void DispatchCallMoveSameFlow((EntityKind EntityType, Guid EntityId) target)
    {
        var targetWorkOpt = StoreHierarchyQueries.resolveTarget(_store, EntityKind.Work, target.EntityType, target.EntityId);
        if (targetWorkOpt is null) return;
        var targetWorkId = targetWorkOpt.Value;

        TryEditorFunc(
            () => _store.MoveCallsToWork(_clipboardSelection.Select(k => k.Id).ToArray(), targetWorkId),
            out int moved, fallback: 0);
        if (moved > 0)
        {
            _clipboardSelection.Clear();
            _clipboardIsCut = false;
            Selection.ApplyCutPendingVisuals([]);
            StatusText = $"Moved {moved} Call(s) within Flow.";
            RefreshEditorCommandStates();
        }
        else
        {
            StatusText = "Nothing moved (duplicate name or invalid target).";
        }
    }

    /// <summary>Work 들의 내부 Call 을 모아 cross-flow device mode 다이얼로그를 띄운다.
    /// Work cross-flow = 내부 Call 전부 cross-flow 이므로 Call 이동과 동일한 device 처리 선택이 필요.
    /// Call 이 없으면 device 무관 → 다이얼로그 없이 CloneSystem. 사용자가 취소하면 false.</summary>
    private Guid FlowOfWork(Guid workId) =>
        _store.WorksReadOnly.TryGetValue(workId, out var work) ? work.ParentId : Guid.Empty;

    /// <summary>Work cross-flow 이동 사전 검증 — Reference 묶음·device 이름 충돌이면 이유를 보여 주고 false.</summary>
    private bool CheckWorkMoveAllowed(IReadOnlyList<Guid> workIds, Guid targetFlowId, CrossFlowDeviceMode mode)
    {
        var validation = _store.CanMoveWorksAcrossFlow(workIds, targetFlowId, mode);
        if (validation.IsMovable) return true;
        ShowMoveValidationBlocked(validation);
        return false;
    }

    private bool TryResolveWorkMoveDeviceMode(IReadOnlyList<Guid> workIds, Guid targetFlowId, out CrossFlowDeviceMode mode)
    {
        mode = CrossFlowDeviceMode.CloneSystem;
        var callIds = workIds
            .SelectMany(wid => Queries.callsOf(wid, _store))
            .Select(c => c.Id)
            .ToArray();
        if (callIds.Length == 0) return true;
        var m = _dialogService.PromptCrossFlowDeviceMode(
            new CrossFlowDeviceModePromptContext(
                CallCount: callIds.Length,
                IsCut: true,
                Store: _store,
                SourceCallIds: callIds,
                TargetFlowId: targetFlowId));
        if (m is null) return false;          // 다이얼로그 취소
        mode = m;
        return true;
    }

    /// <summary>Cut Work → target Flow 로 cross-flow 이동. 내부 Call device 처리 모드 선택 후
    /// MoveWorksAcrossFlow(paste+원본 cascade-remove). no-op 은 F# 가 제외.</summary>
    private void DispatchWorkMove((EntityKind EntityType, Guid EntityId) target)
    {
        var targetFlowOpt = StoreHierarchyQueries.resolveTarget(_store, EntityKind.Flow, target.EntityType, target.EntityId);
        if (targetFlowOpt is null) return;

        var workIds = _clipboardSelection.Select(k => k.Id).ToArray();
        var targetFlowId = targetFlowOpt.Value;
        if (!TryResolveWorkMoveDeviceMode(workIds, targetFlowId, out var mode)) return;
        if (!CheckWorkMoveAllowed(workIds, targetFlowId, mode)) return;

        // 디바이스 모드 다이얼로그를 받은 뒤의 store 작업만 busy — 오버레이/모달 겹침 방지.
        var resolvedMode = mode;
        _ = RunBusyAsync("Work 이동 중...", () =>
        {
            TryEditorFunc(
                () => _store.MoveWorksAcrossFlow(workIds, targetFlowId, resolvedMode),
                out var movedIds,
                fallback: Microsoft.FSharp.Collections.ListModule.Empty<Guid>());
            var moved = movedIds?.ToList() ?? new List<Guid>();
            if (moved.Count > 0)
            {
                _clipboardSelection.Clear();
                _clipboardIsCut = false;
                Selection.ApplyCutPendingVisuals([]);
                ApplyPasteSelection(moved, $"Moved {moved.Count} Work(s) across Flow ({resolvedMode}).");
                RefreshEditorCommandStates();
            }
            else
            {
                StatusText = "Nothing moved (invalid target).";
            }
        });
    }

    private void ShowMoveValidationBlocked(CrossFlowMoveValidation v)
    {
        if (v.IsSameWorkMove)
            _dialogService.ShowWarning("이미 같은 Work 안에 있는 Call 은 이동할 수 없습니다.");
        else if (v is CrossFlowMoveValidation.DuplicateNamesInTarget dup)
            _dialogService.ShowWarning($"대상 Work 에 이미 동일한 이름의 Call 이 있습니다 ({dup.Item.Length}개).");
        else if (v is CrossFlowMoveValidation.RenameModeConflicts conflicts)
            _dialogService.ShowWarning($"Prefix 교체 모드 충돌: {conflicts.Item.Length}개 device. Device 복사 모드를 사용하세요.");
        else if (v is CrossFlowMoveValidation.ReferencesInvolved referenced)
            _dialogService.ShowWarning(
                $"Reference 로 묶인 항목({referenced.Item.Length}개)은 다른 Flow 로 옮길 수 없습니다.\n" +
                "이동은 원본을 옮기고 원래 자리의 묶음을 지웁니다 — Reference 를 먼저 정리하거나 복사·붙여넣기를 쓰세요.");
        else
            _dialogService.ShowWarning("이동할 수 없습니다.");
    }

    /// <summary>Cut Flow → target System 으로 재부모화 이동 (MoveFlowsToSystem, 1 undo step).
    /// Work/Call id 가 보존되므로 디바이스 모드 다이얼로그가 필요 없다.</summary>
    private void DispatchFlowMove((EntityKind EntityType, Guid EntityId) target)
    {
        var targetSystemIdOpt = StoreHierarchyQueries.resolveTarget(
            _store, EntityKind.System, target.EntityType, target.EntityId);
        if (targetSystemIdOpt is null)
        {
            StatusText = "이동 대상 System 을 선택하세요.";
            return;
        }

        TryEditorFunc(
            () => _store.MoveFlowsToSystem(_clipboardSelection.Select(k => k.Id), targetSystemIdOpt.Value),
            out int moved, fallback: 0);
        if (moved > 0)
        {
            _clipboardSelection.Clear();
            _clipboardIsCut = false;
            Selection.ApplyCutPendingVisuals([]);
            StatusText = $"Moved {moved} Flow(s) to System.";
            RefreshEditorCommandStates();
        }
        else
        {
            StatusText = "Nothing moved (이미 그 System 이거나 잘못된 대상).";
        }
    }

    private void PasteFlowsWithRename((EntityKind EntityType, Guid EntityId) target)
    {
        var targetSystemIdOpt = StoreHierarchyQueries.resolveTarget(
            _store, EntityKind.System, target.EntityType, target.EntityId);

        // 1) 이름 프롬프트를 먼저 전부 수집 — busy 오버레이와 모달이 겹치지 않게.
        //    같은 배치에서 이미 정한 이름도 제안 목록에 반영 (코어 nextUniqueName 이 최종 가드).
        var plans = new List<(Guid FlowId, Guid SysId, string NewName)>();
        var skippedMissingFlows = 0;
        foreach (var key in _clipboardSelection)
        {
            if (!_store.FlowsReadOnly.TryGetValue(key.Id, out var srcFlow))
            {
                skippedMissingFlows++;
                continue;
            }

            var sysId = targetSystemIdOpt != null ? targetSystemIdOpt.Value : srcFlow.ParentId;
            var existingNames = Queries.flowsOf(sysId, _store).Select(f => f.Name)
                .Concat(plans.Where(p => p.SysId == sysId).Select(p => p.NewName))
                .ToList();
            var suggestedName = GetUniqueName(srcFlow.Name, existingNames, "_");
            var newName = _dialogService.PromptName("Flow 복사 — 새 이름", suggestedName);
            if (newName is null) return;
            if (!Services.Naming.InputPolicy.TryConfirm(EntityKind.Flow, newName, out newName)) return;
            plans.Add((key.Id, sysId, newName));
        }

        // 2) 실제 붙여넣기(트리 복제 + 리빌드)는 busy 아래에서.
        var skipped = skippedMissingFlows;
        _ = RunBusyAsync("Flow 붙여넣는 중...", () =>
        {
            var pastedFlowIds = new List<Guid>();
            foreach (var (flowId, sysId, newName) in plans)
            {
                if (!TryEditorRef(
                        () => _store.PasteFlowWithRename(flowId, sysId, newName),
                        out var resultOpt))
                    return;

                if (resultOpt != null)
                    pastedFlowIds.Add(resultOpt.Value);
            }

            var workIds = pastedFlowIds
                .SelectMany(fId => Queries.worksOf(fId, _store))
                .Select(w => w.Id)
                .ToList();

            var status = skipped > 0
                ? $"Pasted {pastedFlowIds.Count} Flow(s); skipped {skipped} missing Flow(s)."
                : $"Pasted {pastedFlowIds.Count} Flow(s).";
            ApplyPasteSelection(workIds, status);
        });
    }
}
