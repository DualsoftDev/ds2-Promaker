using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using Ds2.Editor;
using Promaker.Dialogs;
using Promaker.Services;

namespace Promaker.Tests;

/// <summary>
/// 회귀 테스트용 대화상자. 경고 문구를 모으고, 시뮬 편집 차단·Flow 간 이동 모드 질문에는 정해 둔 답을 한다.
/// </summary>
internal sealed class TestDialogService : IDialogService
{
    public List<string> Warnings { get; } = [];
    public int SimulationBlockPrompts { get; private set; }

    /// <summary>"시뮬레이션 중에는 편집할 수 없다 — 종료할까요?" 에 대한 답.</summary>
    public bool StopSimulationWhenAsked { get; init; }

    public CrossFlowDeviceMode? CrossFlowMode { get; init; }

    /// <summary>파일 열기 대화상자가 돌려줄 경로(null = 취소).</summary>
    public string? OpenFilePath { get; init; }

    /// <summary>Device CSV 의 같은 이름 Device 질문에 대한 답(null = 취소). 받은 질문은 DeviceNameConflictPrompts 에 남는다.</summary>
    public DeviceNameConflictMode? DeviceNameConflictChoice { get; init; }
    public List<IReadOnlyList<string>> DeviceNameConflictPrompts { get; } = [];

    public string? PromptName(string title, string defaultName) => defaultName;
    public bool Confirm(string message, string title) => true;
    public void ShowWarning(string message) => Warnings.Add(message);
    public bool WarnSimulationEditBlocked(string message)
    {
        SimulationBlockPrompts++;
        return StopSimulationWhenAsked;
    }
    public void ShowError(string message) => Warnings.Add(message);
    public void ShowInfo(string message) { }
    public MessageBoxResult AskSaveChanges() => MessageBoxResult.No;
    public string? ShowOpenFileDialog(string filter) => OpenFilePath;
    public string? ShowSaveFileDialog(string filter, string? defaultFileName = null) => null;
    public T? ShowDialog<T>(Window dialog) where T : class => null;
    public bool? ShowDialog(Window dialog) => null;
    public CrossFlowDeviceMode? PromptCrossFlowDeviceMode(CrossFlowDeviceModePromptContext context) => CrossFlowMode;
    public DeviceNameConflictMode? PromptDeviceNameConflict(IReadOnlyList<string> deviceNames)
    {
        DeviceNameConflictPrompts.Add(deviceNames);
        return DeviceNameConflictChoice;
    }
}

/// <summary>뷰모델의 비공개 멤버에 접근하는 테스트 도우미.</summary>
internal static class TestReflection
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static T Get<T>(object target, string fieldName) =>
        (T)target.GetType().GetField(fieldName, Instance)!.GetValue(target)!;

    public static object? GetOrNull(object target, string fieldName) =>
        target.GetType().GetField(fieldName, Instance)!.GetValue(target);

    public static void Set(object target, string fieldName, object? value) =>
        target.GetType().GetField(fieldName, Instance)!.SetValue(target, value);

    public static object? Invoke(object target, string methodName, params object?[] args) =>
        target.GetType().GetMethod(methodName, Instance)!.Invoke(target, args);
}
