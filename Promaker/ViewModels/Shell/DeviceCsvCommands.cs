using System;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core.Store;
using Ds2.CSV;
using Ds2.Editor;

namespace Promaker.ViewModels;

/// <summary>
/// Device CSV 불러오기(#253) — Device·Api 선언(Device,Api,ActionType,SensingType,Description)을 기존 프로젝트에 더한다.
/// 표준 CSV 는 Call 한 줄이 기준이라 Device·Api 만 적을 수 없었다. 같은 이름의 Api 가 이미 있으면 건너뛸지 새 이름으로 넣을지 묻는다.
/// 해석은 Ds2.CSV(DeviceCsv), 모델 변경 규칙은 F#(ImportDeviceApis) — 여기서는 파일·질문·상태줄만 다룬다.
/// </summary>
public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(HasProject))]
    private void ImportDeviceCsv()
    {
        if (!GuardSimulationSemanticEdit("Device CSV 불러오기"))
            return;

        var path = _dialogService.ShowOpenFileDialog("CSV 파일 (*.csv)|*.csv|모든 파일 (*.*)|*.*");
        if (path is null)
            return;

        ImportDeviceCsvFile(path);
    }

    internal void ImportDeviceCsvFile(string path)
    {
        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            _dialogService.ShowWarning($"파일을 읽을 수 없습니다: {ex.Message}");
            return;
        }

        var parsed = DeviceCsv.parse(content);
        if (parsed.IsError)
        {
            var lines = parsed.ErrorValue.Take(10)
                .Select(e => e.LineNumber > 0 ? $"{e.LineNumber}행: {e.Message}" : e.Message);
            _dialogService.ShowWarning(
                "Device CSV 를 읽을 수 없습니다.\n" + string.Join("\n", lines) + $"\n\n형식: {DeviceCsv.headerText}");
            return;
        }

        var project = Queries.allProjects(_store).FirstOrDefault();
        if (project is null)
            return;

        var specs = parsed.ResultValue
            .Select(row => new DeviceApiSpec(row.Device, row.Api, row.ActionType, row.SensingType, row.Description?.Value ?? ""))
            .ToList();
        if (specs.Count == 0)
        {
            StatusText = "Device CSV 에 불러올 행이 없습니다.";
            return;
        }

        if (!TryEditorRef(() => _store.FindDeviceApiConflicts(project.Id, specs), out var conflicts))
            return;

        var mode = DeviceApiConflictMode.Skip;
        if (!conflicts.IsEmpty)
        {
            var choice = _dialogService.PromptDeviceApiConflict(conflicts.Select(c => c.FullName).ToList());
            if (choice is null)
                return;
            mode = choice.Value;
        }

        if (!TryEditorRef(() => _store.ImportDeviceApis(project.Id, specs, mode), out var result))
            return;

        var status = $"Device CSV: Device {result.AddedDevices}개, Api {result.AddedApis}개 추가";
        if (!result.Skipped.IsEmpty) status += $", {result.Skipped.Length}개 건너뜀";
        if (!result.Renamed.IsEmpty) status += $", {result.Renamed.Length}개 새 이름";
        StatusText = status;
    }
}
