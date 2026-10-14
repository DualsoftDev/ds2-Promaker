using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using Ds2.Core.Store;
using Promaker.Dialogs;
using Promaker.Services;
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
            var dialogs = new RecordingDialogService();
            SetField(vm, "_dialogService", dialogs);
            vm.NewProjectCommand.Execute(null);
            StaTestRunner.PumpPendingUi();
            var store = (DsStore)GetField(vm, "_store")!;
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
                Assert.Null(GetField(vm, "_currentFilePath"));
                Assert.DoesNotContain(Path.GetFileName(path), vm.Title);
                Assert.False(vm.IsBusy);
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static object? GetField(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private sealed class RecordingDialogService : IDialogService
    {
        public List<string> Warnings { get; } = [];

        public string? PromptName(string title, string defaultName) => defaultName;
        public bool Confirm(string message, string title) => true;
        public void ShowWarning(string message) => Warnings.Add(message);
        public bool WarnSimulationEditBlocked(string message) => false;
        public void ShowError(string message) => Warnings.Add(message);
        public void ShowInfo(string message) { }
        public MessageBoxResult AskSaveChanges() => MessageBoxResult.No;
        public string? ShowOpenFileDialog(string filter) => null;
        public string? ShowSaveFileDialog(string filter, string? defaultFileName = null) => null;
        public T? ShowDialog<T>(Window dialog) where T : class => null;
        public bool? ShowDialog(Window dialog) => null;
        public Ds2.Editor.CrossFlowDeviceMode? PromptCrossFlowDeviceMode(CrossFlowDeviceModePromptContext context) => null;
    }
}
