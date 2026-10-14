using System;
using System.Linq;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// 편집 경로의 C# 배선 회귀 — F# 규칙이 막은 동작을 사용자에게 알리는지, 손상된 모델 선택이 앱을 죽이지 않는지.
/// </summary>
public sealed class EditorWiringRegressionTests
{
    private static (MainViewModel vm, DsStore store, TestDialogService dialogs, Guid projectId, Guid systemId, Guid flowId)
        NewProject()
    {
        var vm = new MainViewModel();
        var dialogs = new TestDialogService();
        TestReflection.Set(vm, "_dialogService", dialogs);
        vm.NewProjectCommand.Execute(null);
        StaTestRunner.PumpPendingUi();
        var store = TestReflection.Get<DsStore>(vm, "_store");
        return (vm, store, dialogs, Assert.Single(store.Projects.Keys), Assert.Single(store.Systems.Keys),
                Assert.Single(store.Flows.Keys));
    }

    /// <summary>Call 화살표를 뒤집어 순환이 생기면 거절하고 이유를 알려야 한다 — 예전엔 그대로 뒤집혀, 그 Work 를 클릭하면
    /// 무한 재귀로 앱이 흔적 없이 죽었다.</summary>
    [Fact]
    public void Reversing_a_call_arrow_into_a_cycle_warns_and_keeps_the_arrow()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, store, dialogs, projectId, _, flowId) = NewProject();
            var workId = store.AddWork("W", flowId);
            store.AddCallsWithDevice(projectId, workId, new[] { "D.A1", "D.A2", "D.A3" }, true, null);
            var calls = Queries.callsOf(workId, store).ToList();
            store.ConnectSelectionInOrder(new[] { calls[0].Id, calls[1].Id, calls[2].Id }, ArrowType.Start);
            store.ConnectSelectionInOrder(new[] { calls[0].Id, calls[2].Id }, ArrowType.Start);
            var shortcut = store.ArrowCalls.Values.Single(a => a.SourceId == calls[0].Id && a.TargetId == calls[2].Id);

            Assert.False(vm.TryReverseArrow(shortcut.Id));

            Assert.Contains(dialogs.Warnings, w => w.Contains("순환"));
            Assert.Equal(calls[0].Id, store.ArrowCalls[shortcut.Id].SourceId);
        });
    }

    /// <summary>Start 로 이어 둔 두 Work 를 다시 골라도 연결 메뉴가 떠야 한다 — 예전엔 메뉴 판단이 Start 로만 따져
    /// "화살표 연결" 이 꺼졌다. 이제 Start 만 꺼지고, StartReset 을 고르면 기존 Start 를 대체한다.</summary>
    [Fact]
    public void Two_works_joined_by_start_open_the_connect_menu_and_start_reset_replaces_start()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, store, _, _, _, flowId) = NewProject();
            var work1Id = store.AddWork("W1", flowId);
            var work2Id = store.AddWork("W2", flowId);
            store.ConnectSelectionInOrder(new[] { work1Id, work2Id }, ArrowType.Start);

            vm.Selection.SelectNodeFromCanvas(new EntityNode(work1Id, EntityKind.Work, "W1"), ctrlPressed: false, shiftPressed: false);
            vm.Selection.SelectNodeFromCanvas(new EntityNode(work2Id, EntityKind.Work, "W2"), ctrlPressed: true, shiftPressed: false);

            Assert.True(vm.Selection.TryGetOrderedSelectionConnectEntityType(out var kind));
            Assert.Equal(EntityKind.Work, kind);
            Assert.True(vm.ConnectSelectedNodesCommand.CanExecute(null));
            var types = vm.Selection.ConnectableArrowTypes();
            Assert.DoesNotContain(ArrowType.Start, types);
            Assert.Contains(ArrowType.StartReset, types);

            Assert.True(vm.Selection.ConnectSelectedNodesInOrder(ArrowType.StartReset));
            var arrow = Assert.Single(store.ArrowWorks.Values.Where(a => a.SourceId == work1Id && a.TargetId == work2Id));
            Assert.Equal(ArrowType.StartReset, arrow.ArrowType);
        });
    }

    /// <summary>Reference 가 걸린 Work 를 다른 Flow 로 옮기면 막고 이유를 알려야 한다 — 예전엔 원본이 옮겨지고 Reference 가
    /// 경고 없이 지워졌다.</summary>
    [Fact]
    public void Moving_a_work_with_references_to_another_flow_is_refused_with_a_reason()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, store, dialogs, _, systemId, flowId) = NewProject();
            var workId = store.AddWork("W", flowId);
            var referenceId = store.AddReferenceWork(workId);
            var otherFlowId = store.AddFlow("Other", systemId);

            Assert.False(vm.TryMoveWorksToFlowFromTree(new[] { workId }, otherFlowId, copyMode: false));

            Assert.Contains(dialogs.Warnings, w => w.Contains("Reference"));
            Assert.Equal(flowId, store.Works[workId].ParentId);
            Assert.True(store.Works.ContainsKey(referenceId));
        });
    }

    /// <summary>원본이 없는 Reference Call(외부 도구가 만든 파일 등)을 선택해도 앱이 죽지 않아야 한다 — 속성 패널 갱신의
    /// 예외가 선택 변경 처리에서 그대로 올라가 "Fatal Error" 로 종료됐다.</summary>
    [Fact]
    public void Selecting_a_reference_call_whose_original_is_missing_does_not_throw()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, store, _, projectId, _, flowId) = NewProject();
            var workId = store.AddWork("W", flowId);
            store.AddCallsWithDevice(projectId, workId, new[] { "D.A1" }, true, null);
            var original = Queries.callsOf(workId, store).Single();
            var referenceId = store.AddReferenceCall(original.Id);
            store.Calls.Remove(original.Id);   // 손상된 파일 재현 — 원본 없는 Reference

            var ex = Record.Exception(() => vm.SelectedNode = new EntityNode(referenceId, EntityKind.Call, "ref"));

            Assert.Null(ex);
        });
    }
}
