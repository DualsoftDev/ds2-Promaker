using System;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Promaker.Dialogs;
using Ds2.Core.Store;
using Ds2.CSV;
using Ds2.Editor;

namespace Promaker.ViewModels;

/// <summary>
/// Device CSV 불러오기(#253) — Device·Api 선언(Device,Api,ActionType,SensingType,Description)을 기존 프로젝트에 더한다.
/// 표준 CSV 는 Call 한 줄이 기준이라 Device·Api 만 적을 수 없었다. 같은 이름의 Device(System)가 이미 있으면
/// 그 Device 를 건너뛸지 새 이름(Cyl_1 …)으로 만들지 묻는다.
/// 해석은 Ds2.CSV(DeviceCsv), 모델 변경 규칙은 F#(ImportDevices) — 여기서는 파일·질문·상태줄만 다룬다.
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

    /// <summary>Device CSV 내보내기 — 지금 프로젝트의 Device·Api 를 같은 형식으로 저장한다. 고쳐서 다른 프로젝트에 불러오거나
    /// 양식으로 쓴다. 예전엔 불러오기만 있어서 이 형식을 손으로 처음부터 적어야 했다.</summary>
    [RelayCommand(CanExecute = nameof(HasProject))]
    private void ExportDeviceCsv()
    {
        var path = _dialogService.ShowSaveFileDialog("CSV 파일 (*.csv)|*.csv|모든 파일 (*.*)|*.*", "devices.csv");
        if (path is null)
            return;

        if (ExportDeviceCsvToPath(path))
            CsvFileHelper.PromptOpenAfterExport(path, title: "Device CSV 내보내기");
    }

    internal bool ExportDeviceCsvToPath(string path)
    {
        var project = Queries.allProjects(_store).FirstOrDefault();
        if (project is null)
            return false;

        try
        {
            // 표준 CSV 내보내기와 같은 UTF-8(BOM) — 엑셀에서 한글이 깨지지 않는다.
            File.WriteAllText(path, DeviceCsv.projectToCsv(_store, project.Id), System.Text.Encoding.UTF8);
            StatusText = $"Device CSV 내보내기 완료 ({path})";
            return true;
        }
        catch (Exception ex)
        {
            _dialogService.ShowWarning($"Device CSV 를 저장할 수 없습니다: {ex.Message}");
            return false;
        }
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

        if (!TryEditorRef(() => _store.FindExistingDeviceNames(project.Id, specs), out var existingNames))
            return;

        var mode = DeviceNameConflictMode.Skip;
        if (!existingNames.IsEmpty)
        {
            var choice = _dialogService.PromptDeviceNameConflict(existingNames.ToList());
            if (choice is null)
                return;
            mode = choice.Value;
        }

        if (!TryEditorRef(() => _store.ImportDevices(project.Id, specs, mode), out var result))
            return;

        var status = $"Device CSV: Device {result.AddedDevices}개, Api {result.AddedApis}개 추가";
        if (!result.SkippedDevices.IsEmpty)
            status += $", Device {result.SkippedDevices.Length}개 건너뜀";
        if (!result.RenamedDevices.IsEmpty)
            status += ", 새 이름 " + string.Join(", ", result.RenamedDevices.Select(r => $"{r.Item1} → {r.Item2}"));
        StatusText = status;
    }
}
