using System;
using System.IO;
using Ds2.Core.StandardSubmodels;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.Services;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// "저장 안 됨"(IsDirty) 판정 회귀. 예전엔 undo 개수로 판정해 저장 후 되돌리면 "저장됨" 으로 보였고, 이력 밖 변경(PLC 접속)은
/// 다음 이력 갱신 때 표시가 꺼졌다. CSV 로 만든 모델·샘플·Mermaid 저장은 저장 안 된 문서를 저장된 것처럼 보이게 했다 —
/// 모두 닫을 때 저장 확인 없이 편집을 잃는 경로였다.
/// </summary>
public sealed class DirtyTrackingTests
{
    private static (MainViewModel vm, DsStore store, Guid flowId) NewProject(TestDialogService? dialogs = null)
    {
        var vm = new MainViewModel();
        TestReflection.Set(vm, "_dialogService", dialogs ?? new TestDialogService());
        vm.NewProjectCommand.Execute(null);
        StaTestRunner.PumpPendingUi();
        var store = TestReflection.Get<DsStore>(vm, "_store");
        var flowId = Assert.Single(store.Flows.Keys);
        return (vm, store, flowId);
    }

    private static string TempFile(string extension) =>
        Path.Combine(Path.GetTempPath(), $"dirty-{Guid.NewGuid():N}{extension}");

    [Fact]
    public void Undo_after_saving_is_unsaved_and_redo_returns_to_the_saved_state()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, store, flowId) = NewProject();
            store.AddWork("W1", flowId);
            store.AddWork("W2", flowId);
            var path = TempFile(".sdf");
            try
            {
                Assert.True((bool)TestReflection.Invoke(vm, "SaveToPath", path)!);
                Assert.False(vm.IsDirty);

                store.Undo();
                Assert.True(vm.IsDirty);

                store.Redo();
                Assert.False(vm.IsDirty);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        });
    }

    [Fact]
    public void A_change_outside_the_edit_history_stays_unsaved_after_a_later_undo()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, store, flowId) = NewProject();
            var systemId = Assert.Single(store.Systems.Keys);
            var profile = PlcVendorProfile.Defaults(PlcVendorChoice.LsXgi);
            profile.IpAddress = "192.168.0.50";
            Assert.True(vm.Simulation.SavePlcEndpointForSystem(systemId, PlcVendorChoice.LsXgi, profile, null, null));

            store.AddWork("W1", flowId);
            store.Undo();

            Assert.True(vm.IsDirty);
        });
    }

    [Fact]
    public void A_model_created_from_csv_is_unsaved_until_it_is_saved()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, _, _) = NewProject();
            var imported = new DsStore();
            var projectId = imported.AddProject("FromCsv");
            imported.AddSystem("S", projectId, true);

            TestReflection.Invoke(vm, "ImportCsvStore", imported, "붙여넣기");
            StaTestRunner.PumpPendingUi();

            Assert.True(vm.IsDirty);
            Assert.Null(TestReflection.GetOrNull(vm, "_currentFilePath"));
        });
    }

    [Fact]
    public void Opening_a_sample_as_an_untitled_copy_keeps_no_path_and_is_unsaved()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, _, _) = NewProject();
            var sample = new DsStore();
            var projectId = sample.AddProject("Sample");
            sample.AddSystem("S", projectId, true);
            var path = TempFile(".sdf");
            sample.SaveToFile(path);
            var recentOriginal = RecentFilesManager.SettingsPath;
            RecentFilesManager.SettingsPath = TempFile(".txt");
            try
            {
                vm.OpenFilePath(path, asUntitledCopy: true);
                StaTestRunner.PumpPendingUi();

                Assert.True(vm.HasProject);
                Assert.Null(TestReflection.GetOrNull(vm, "_currentFilePath"));
                Assert.True(vm.IsDirty);
                Assert.DoesNotContain(path, RecentFilesManager.LoadRecentFiles());
            }
            finally
            {
                if (File.Exists(RecentFilesManager.SettingsPath)) File.Delete(RecentFilesManager.SettingsPath);
                RecentFilesManager.SettingsPath = recentOriginal;
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void Saving_as_mermaid_is_an_export_and_leaves_the_document_unsaved()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, store, flowId) = NewProject();
            store.AddWork("W1", flowId);
            Assert.True(vm.IsDirty);
            var path = TempFile(".md");
            try
            {
                Assert.True((bool)TestReflection.Invoke(vm, "SaveToPath", path)!);

                Assert.True(File.Exists(path));
                Assert.True(vm.IsDirty);
                Assert.Null(TestReflection.GetOrNull(vm, "_currentFilePath"));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        });
    }

    /// <summary>History 패널은 마지막 저장 지점에 💾 를 표시한다(#255). 되돌려도 그 항목(취소선)에 남고, 파일이 없는
    /// 새 프로젝트에는 되돌아갈 저장 지점이 없으니 표시하지 않는다.</summary>
    [Fact]
    public void History_panel_marks_the_last_saved_point()
    {
        StaTestRunner.Run(() =>
        {
            var (vm, store, flowId) = NewProject();
            store.AddWork("W1", flowId);
            Assert.DoesNotContain(vm.HistoryItems, i => i.IsSavePoint);

            store.AddWork("W2", flowId);
            var path = TempFile(".sdf");
            try
            {
                Assert.True((bool)TestReflection.Invoke(vm, "SaveToPath", path)!);
                var savedIndex = vm.HistoryItems.Count - 1;
                Assert.True(vm.HistoryItems[savedIndex].IsSavePoint);

                store.Undo();
                Assert.True(vm.HistoryItems[savedIndex].IsRedo);
                Assert.True(vm.HistoryItems[savedIndex].IsSavePoint);
                Assert.Single(vm.HistoryItems, i => i.IsSavePoint);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        });
    }

    /// <summary>시뮬이 도는 중의 파일 열기(드롭·최근 파일·외부 변경 재로드)는 시뮬 종료를 확인받아야 한다 — 예전엔 가드 없이
    /// store 를 갈아 끼워, 엔진 스레드가 교체 중인 사전을 읽다 프로세스가 죽을 수 있었다.</summary>
    [Fact]
    public void Opening_a_file_while_simulating_is_refused_unless_the_user_stops_the_simulation()
    {
        StaTestRunner.Run(() =>
        {
            var dialogs = new TestDialogService { StopSimulationWhenAsked = false };
            var (vm, store, _) = NewProject(dialogs);
            var other = new DsStore();
            var projectId = other.AddProject("Other");
            other.AddSystem("S", projectId, true);
            var path = TempFile(".sdf");
            other.SaveToFile(path);
            var projectBefore = Assert.Single(store.Projects.Keys);
            try
            {
                vm.Simulation.IsSimulating = true;
                vm.OpenFilePath(path);
                StaTestRunner.PumpPendingUi();

                Assert.Equal(1, dialogs.SimulationBlockPrompts);
                Assert.Equal(projectBefore, Assert.Single(store.Projects.Keys));
                Assert.Null(TestReflection.GetOrNull(vm, "_currentFilePath"));
            }
            finally
            {
                vm.Simulation.IsSimulating = false;
                File.Delete(path);
            }
        });
    }
}
