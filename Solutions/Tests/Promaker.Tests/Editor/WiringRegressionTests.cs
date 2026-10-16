using System;
using System.Linq;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests.Editor;

/// <summary>
/// 편집 경로의 C# 배선 회귀 — F# 규칙이 막은 동작을 사용자에게 알리는지, 손상된 모델 선택이 앱을 죽이지 않는지.
/// </summary>
public sealed class WiringRegressionTests
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

    /// <summary>복사한 Call 을 Flow 에 붙이면 Work 를 고르라고 알려야 한다(#248) — 예전엔 경고 없이 0개가 붙었고,
    /// System 에 붙일 때는 Call 인데도 "Flow 를 선택하세요" 가 떴다.</summary>
    [Fact]
    public void Pasting_copied_calls_onto_a_flow_asks_for_a_work()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, store, dialogs, projectId, _, flowId) = NewProject();
            var workId = store.AddWork("W", flowId);
            store.AddCallsWithDevice(projectId, workId, new[] { "D.A1" }, true, null);
            var callId = Queries.callsOf(workId, store).Single().Id;

            vm.Selection.SelectNodeFromCanvas(new EntityNode(callId, EntityKind.Call, "D.A1"), ctrlPressed: false, shiftPressed: false);
            vm.CopySelectedCommand.Execute(null);
            vm.SelectedNode = new EntityNode(flowId, EntityKind.Flow, "F");
            vm.PasteCopiedCommand.Execute(null);

            Assert.Contains(dialogs.Warnings, w => w.StartsWith("Work "));
            Assert.Single(Queries.callsOf(workId, store));
        });
    }

    /// <summary>캔버스 화살표를 한 번에 고를 수 있어야 한다(#257). 노드 선택은 비워서 이어서 누른 Delete 가 화살표만 지운다.</summary>
    [Fact]
    public void Selecting_all_canvas_arrows_selects_every_arrow_and_delete_removes_only_arrows()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, store, _, _, _, flowId) = NewProject();
            var work1Id = store.AddWork("W1", flowId);
            var work2Id = store.AddWork("W2", flowId);
            var work3Id = store.AddWork("W3", flowId);
            store.ConnectSelectionInOrder(new[] { work1Id, work2Id, work3Id }, ArrowType.Start);
            StaTestRunner.PumpPendingUi();
            foreach (var a in store.ArrowWorks.Values.Where(a => vm.Canvas.CanvasArrows.All(x => x.Id != a.Id)))
                vm.Canvas.CanvasArrows.Add(new ArrowNode(a.Id, a.SourceId, a.TargetId, a.ArrowType));
            vm.Selection.SelectNodeFromCanvas(new EntityNode(work1Id, EntityKind.Work, "W1"), ctrlPressed: false, shiftPressed: false);

            Assert.Equal(2, vm.Selection.SelectAllCanvasArrows());

            Assert.Empty(vm.Selection.OrderedNodeSelection);
            Assert.All(vm.Canvas.CanvasArrows, a => Assert.True(a.IsSelected));
            vm.DeleteSelectedCommand.Execute(null);
            Assert.Empty(store.ArrowWorks);
            Assert.True(store.Works.ContainsKey(work1Id));
        });
    }

    /// <summary>Device 를 고르면 속성 창에 사용 현황이 나오고, Device 트리에는 사용처 툴팁과 미사용 표시가 붙는다(#263).
    /// 예전엔 Device 가 어디에 쓰이는지 알 길이 없었다.</summary>
    [Fact]
    public void Device_usage_is_shown_in_the_property_panel_and_the_device_tree()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, store, _, projectId, _, flowId) = NewProject();
            var workId = store.AddWork("W", flowId);
            store.AddCallsWithDevice(projectId, workId, new[] { "Cyl.Adv" }, true, null);
            var call = Queries.callsOf(workId, store).Single();
            var deviceId = store.ApiDefs[call.ApiCalls[0].ApiDefId.Value].ParentId;
            StaTestRunner.PumpPendingUi();

            var deviceNode = TreeNodeSearch.EnumerateNodes(vm.DeviceTreeRoots).Single(n => n.Id == deviceId);
            Assert.False(deviceNode.IsUnused);
            Assert.Contains("1곳", deviceNode.UsageToolTip);

            vm.Selection.SelectNodeFromTree(deviceNode, ctrlPressed: false, shiftPressed: false);
            StaTestRunner.PumpPendingUi();
            Assert.True(vm.PropertyPanel.ShowDeviceUsage);
            Assert.Equal(call.Id, Assert.Single(vm.PropertyPanel.DeviceUsages).OwnerId);

            store.RemoveEntities(new[] { Tuple.Create(EntityKind.Call, call.Id) });
            StaTestRunner.PumpPendingUi();
            Assert.True(TreeNodeSearch.EnumerateNodes(vm.DeviceTreeRoots).Single(n => n.Id == deviceId).IsUnused);
        });
    }

    /// <summary>Device CSV 를 불러오면 이미 있는 Device 이름만 묻고, 고른 대로 새 이름의 Device 로 넣는다(#253).
    /// 기존 Device 안에는 Api 를 섞어 넣지 않는다.</summary>
    [Fact]
    public void Importing_a_device_csv_asks_about_existing_device_names_and_adds_a_renamed_device()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"devices-{Guid.NewGuid():N}.csv");
        System.IO.File.WriteAllText(path, "Device,Api,ActionType,SensingType\nCyl,Adv,,\nCyl,Hold,Pulse,\nLamp,On,,\n");
        try
        {
            StaTestRunner.Run(() =>
            {
                var vm = new MainViewModel();
                var dialogs = new TestDialogService
                {
                    OpenFilePath = path,
                    DeviceNameConflictChoice = DeviceNameConflictMode.Rename
                };
                TestReflection.Set(vm, "_dialogService", dialogs);
                vm.NewProjectCommand.Execute(null);
                StaTestRunner.PumpPendingUi();
                var store = TestReflection.Get<DsStore>(vm, "_store");
                var projectId = Assert.Single(store.Projects.Keys);
                store.ImportDevices(projectId, new[]
                {
                    new DeviceApiSpec("Cyl", "Adv", ActionType.NewNormal(null), SensingType.NewNormal(null), "")
                }, DeviceNameConflictMode.Skip);

                vm.ImportDeviceCsvCommand.Execute(null);

                Assert.Equal(new[] { "Cyl" }, Assert.Single(dialogs.DeviceNameConflictPrompts));
                string[] ApiNamesOf(string device) =>
                    Queries.apiDefsOf(Queries.passiveSystemsOf(projectId, store).Single(s => s.Name == device).Id, store)
                        .Select(d => d.Name).OrderBy(n => n).ToArray();
                Assert.Equal(new[] { "Cyl", "Cyl_1", "Lamp" },
                    Queries.passiveSystemsOf(projectId, store).Select(s => s.Name).OrderBy(n => n).ToArray());
                Assert.Equal(new[] { "Adv" }, ApiNamesOf("Cyl"));
                Assert.Equal(new[] { "Adv", "Hold" }, ApiNamesOf("Cyl_1"));
            });
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>Device CSV 는 내보낼 수도 있어야 한다(#253) — 불러오기만 있어 형식을 손으로 처음부터 적어야 했다.
    /// 내보낸 파일을 빈 프로젝트에 불러오면 같은 Device·Api 가 생긴다.</summary>
    [Fact]
    public void An_exported_device_csv_imports_the_same_devices_into_another_project()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"devices-{Guid.NewGuid():N}.csv");
        try
        {
            StaTestRunner.Run(() =>
            {
                var (source, sourceStore, _, sourceProjectId, _, _) = NewProject();
                sourceStore.ImportDevices(sourceProjectId, new[]
                {
                    new DeviceApiSpec("Cyl", "Adv", ActionType.NewPulse(200), SensingType.NewLatch(300), "전진"),
                    new DeviceApiSpec("Cyl", "Ret", ActionType.NewNormal(null), SensingType.NewNormal(null), "")
                }, DeviceNameConflictMode.Skip);
                Assert.True(source.ExportDeviceCsvToPath(path));

                var (target, targetStore, _, targetProjectId, _, _) = NewProject();
                target.ImportDeviceCsvFile(path);

                var device = Queries.passiveSystemsOf(targetProjectId, targetStore).Single(s => s.Name == "Cyl");
                var adv = Queries.apiDefsOf(device.Id, targetStore).Single(d => d.Name == "Adv");
                Assert.Equal(ActionType.NewPulse(200), adv.ActionType);
                Assert.Equal(SensingType.NewLatch(300), adv.SensingType);
                Assert.Equal(2, Queries.apiDefsOf(device.Id, targetStore).Length);
            });
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
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
