using System;
using System.IO;
using Ds2.Core.Store;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// 프로젝트가 없는 JSON(DS2 모델이 아닌 아무 JSON) 을 열면 종전엔 빈 store 로 교체되고 제목에 그 파일명이 달렸다 —
/// "로딩이 안 된다" 로 보고된 증상. 지금은 Core 가 거절하고 편집기는 현재 프로젝트·파일명을 그대로 둔 채 경고만 띄운다.
/// </summary>
public sealed class OpenRejectsNonModelFileTests
{
    [Fact]
    public void Opening_a_json_without_projects_warns_and_keeps_the_current_project()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            var dialogs = new TestDialogService();
            TestReflection.Set(vm, "_dialogService", dialogs);
            vm.NewProjectCommand.Execute(null);
            StaTestRunner.PumpPendingUi();
            var store = TestReflection.Get<DsStore>(vm, "_store");
            var projectId = Assert.Single(store.Projects.Keys);

            var path = Path.Combine(Path.GetTempPath(), $"not-a-model-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, "{\"foo\": 1}");
            try
            {
                vm.OpenFilePath(path);
                StaTestRunner.PumpPendingUi();

                Assert.Contains(dialogs.Warnings, m => m.Contains("프로젝트가 없는"));
                Assert.True(vm.HasProject);
                Assert.Contains(projectId, store.Projects.Keys);
                Assert.Null(TestReflection.GetOrNull(vm, "_currentFilePath"));
                Assert.DoesNotContain(Path.GetFileName(path), vm.Title);
                Assert.False(vm.IsBusy);
            }
            finally
            {
                File.Delete(path);
            }
        });
    }
}
