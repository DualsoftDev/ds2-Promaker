using System;
using System.Windows;
using Ds2.Editor;
using Microsoft.Win32;
using Promaker.Presentation;

namespace Promaker.Services;

/// <summary>
/// 다이얼로그 표시를 담당하는 서비스 구현
/// </summary>
public class DialogService : IDialogService
{
    public string? PromptName(string title, string defaultName)
    {
        return Dialogs.DialogHelpers.PromptName(title, defaultName);
    }

    public bool Confirm(string message, string title)
        => Dialogs.DialogHelpers.Confirm(Application.Current.MainWindow, message, title);

    public void ShowWarning(string message)
    {
        Dialogs.DialogHelpers.Warn(message);
    }

    public bool WarnSimulationEditBlocked(string message)
        => Dialogs.DialogHelpers.ShowSimulationStopOptionDialog(message);

    public void ShowError(string message)
        => Dialogs.DialogHelpers.Error(Application.Current.MainWindow, message);

    public void ShowInfo(string message)
    {
        Dialogs.DialogHelpers.Info(message);
    }

    public MessageBoxResult AskSaveChanges()
    {
        return Dialogs.DialogHelpers.AskSaveChanges();
    }

    public string? ShowOpenFileDialog(string filter)
    {
        var dialog = new OpenFileDialog
        {
            Filter = filter
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? ShowSaveFileDialog(string filter, string? defaultFileName = null)
    {
        var dialog = new SaveFileDialog
        {
            Filter = filter,
            DefaultExt = FileExtensions.Sdf
        };

        if (!string.IsNullOrWhiteSpace(defaultFileName))
            dialog.FileName = defaultFileName;

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public T? ShowDialog<T>(Window dialog) where T : class
    {
        if (Application.Current.MainWindow is { } owner)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        return dialog.ShowDialog() == true ? dialog.DataContext as T : null;
    }

    public bool? ShowDialog(Window dialog)
    {
        if (Application.Current.MainWindow is { } owner)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        return dialog.ShowDialog();
    }

    public CrossFlowDeviceMode? PromptCrossFlowDeviceMode(CrossFlowDeviceModePromptContext context)
    {
        var dlg = new Dialogs.CallMoveAcrossFlowsDialog(context);
        if (Application.Current.MainWindow is { } owner)
        {
            dlg.Owner = owner;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        return dlg.ShowDialog() == true ? dlg.SelectedMode : null;
    }

    public DeviceNameConflictMode? PromptDeviceNameConflict(System.Collections.Generic.IReadOnlyList<string> deviceNames)
    {
        const int shown = 10;
        var list = string.Join("\n", deviceNames.Take(shown).Select(name => "  " + name));
        var more = deviceNames.Count > shown ? $"\n  … 외 {deviceNames.Count - shown}개" : "";
        var answer = Dialogs.DialogHelpers.ShowThemedMessageBox(
            $"같은 이름의 Device 가 이미 {deviceNames.Count}개 있습니다.\n{list}{more}\n\n"
            + "예: 건너뛰기 — 그 Device 의 줄은 불러오지 않고, 기존 Device 는 그대로 둡니다.\n"
            + "아니요: 새 이름으로 추가 — 예) Cyl → Cyl_1 로 새 Device 를 만듭니다.\n"
            + "취소: 불러오지 않습니다.",
            "Device CSV 불러오기", MessageBoxButton.YesNoCancel, "❓");
        return answer switch
        {
            MessageBoxResult.Yes => DeviceNameConflictMode.Skip,
            MessageBoxResult.No => DeviceNameConflictMode.Rename,
            _ => null
        };
    }
}
