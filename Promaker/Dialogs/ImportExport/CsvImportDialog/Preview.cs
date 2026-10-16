using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ds2.CSV;

namespace Promaker.Dialogs;

public class CsvRowViewModel
{
    /// <summary>표준 11열의 ActiveSystem — 비면 대화상자에서 입력한 System 으로 간다(#250).</summary>
    public string ActiveSystemName { get; set; } = "";
    public string FlowName { get; set; } = "";
    public string WorkName { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string SystemName { get; set; } = "";
    public string ApiName { get; set; } = "";
    public string InName { get; set; } = "";
    public string InAddress { get; set; } = "";
    public string OutName { get; set; } = "";
    public string OutAddress { get; set; } = "";
    /// <summary>표준 11열의 ReferenceOf — 원본 Call 이 있는 "Flow.Work". 채워진 행은 Reference Call 이 된다(#254).</summary>
    public string ReferenceOf { get; set; } = "";
}

public class BasicCsvRowViewModel
{
    public string FlowName { get; set; } = "";
    public string WorkName { get; set; } = "";
    public int NodeCount { get; set; }
    public int EdgeCount { get; set; }
    public string CallSummary { get; set; } = "";
}

public partial class CsvImportDialog
{
    private static readonly Brush RecognizedBadgeBackground = CreateBadgeBrush(0x30, 0x40, 0xC0, 0x57);
    private static readonly Brush UnrecognizedBadgeBackground = CreateBadgeBrush(0x30, 0xFF, 0x50, 0x50);

    private static Brush CreateBadgeBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static string BuildPreviewSummary(CsvImportPreview preview, int entryCount)
    {
        var sb = new StringBuilder()
            .AppendLine($"✓ Flow: {preview.FlowNames.Length}개")
            .AppendLine($"✓ Work: {preview.WorkNames.Length}개")
            .AppendLine($"✓ Call: {preview.CallNames.Length}개")
            .AppendLine($"✓ Passive Device System: {preview.PassiveSystemNames.Length}개");
        if (preview.ActiveSystemNames.Length > 0)
            sb.AppendLine($"✓ Active System: {preview.ActiveSystemNames.Length}개 ({string.Join(", ", preview.ActiveSystemNames)})");
        if (preview.ReferenceCallCount > 0)
            sb.AppendLine($"✓ Reference Call: {preview.ReferenceCallCount}개");
        sb.AppendLine();

        AppendSample(sb, "Flow 샘플", preview.FlowNames, 5);
        AppendSample(sb, "Work 샘플", preview.WorkNames, 5);
        AppendSample(sb, "Call 샘플", preview.CallNames, 5);

        if (entryCount > 100)
        {
            sb.AppendLine();
            sb.AppendLine($"※ 총 {entryCount}개 항목 중 100개만 미리보기에 표시됩니다.");
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildSyntheticWarningText(int syntheticApiCount) =>
        syntheticApiCount > 0
            ? $"⚠ Api 열이 비어 있는 {syntheticApiCount}개 항목은 Signal_<addr> 형식으로 자동 생성됩니다."
            : "";

    private static void AppendSample(StringBuilder sb, string label, IEnumerable<string> items, int take)
    {
        var sample = string.Join(", ", items.Take(take));
        if (!string.IsNullOrWhiteSpace(sample))
            sb.AppendLine($"{label}: {sample}");
    }

    private static CsvRowViewModel ToRowViewModel(CsvEntry entry) =>
        new()
        {
            FlowName = entry.FlowName,
            WorkName = entry.WorkName,
            DeviceName = entry.DeviceName,
            SystemName = entry.SystemName,
            ApiName = entry.ApiName,
            InName = OptionText(entry.InName),
            InAddress = OptionText(entry.InAddress),
            OutName = OptionText(entry.OutName),
            OutAddress = OptionText(entry.OutAddress),
            ActiveSystemName = OptionText(entry.ActiveSystemName),
            ReferenceOf = OptionText(entry.ReferenceOf)
        };

    private void SetPreviewState(
        CsvDocument? document,
        IEnumerable<CsvRowViewModel>? rows,
        string previewText,
        string? errorText = null,
        string? warningText = null)
    {
        _document = document;
        _basicDocument = null;
        _aiDocument = null;
        _lastErrorText = errorText ?? "";
        PreviewGrid.ItemsSource = rows?.ToList();
        BasicPreviewGrid.ItemsSource = null;
        PreviewText.Text = previewText;
        ErrorBorder.Visibility = string.IsNullOrWhiteSpace(errorText) ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = errorText ?? "";
        WarningBorder.Visibility = string.IsNullOrWhiteSpace(warningText) ? Visibility.Collapsed : Visibility.Visible;
        WarningText.Text = warningText ?? "";
    }

    private void ShowPreviewFailure(string message)
    {
        SetPreviewState(null, null, PreviewFailureText, errorText: message);
    }

    private void ApplyPreview(CsvDocument document, CsvImportPreview preview)
    {
        SetPreviewState(
            document,
            document.Entries.Take(100).Select(ToRowViewModel),
            BuildPreviewSummary(preview, document.Entries.Length),
            warningText: BuildSyntheticWarningText(preview.SyntheticApiCount));
    }

    private void SetBasicPreviewState(
        BasicCsvDocument? document,
        IEnumerable<BasicCsvRowViewModel>? rows,
        string previewText,
        string? errorText = null,
        string? warningText = null)
    {
        _basicDocument = document;
        _document = null;
        _aiDocument = null;
        _lastErrorText = errorText ?? "";
        BasicPreviewGrid.ItemsSource = rows?.ToList();
        PreviewGrid.ItemsSource = null;
        PreviewText.Text = previewText;
        ErrorBorder.Visibility = string.IsNullOrWhiteSpace(errorText) ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = errorText ?? "";
        WarningBorder.Visibility = string.IsNullOrWhiteSpace(warningText) ? Visibility.Collapsed : Visibility.Visible;
        WarningText.Text = warningText ?? "";
    }

    private void ApplyBasicPreview(BasicCsvDocument document, BasicCsvPreview preview)
    {
        var warnings = document.Warnings.ToList();
        SetBasicPreviewState(
            document,
            document.Works.Take(100).Select(ToBasicRowViewModel),
            BuildBasicPreviewSummary(preview, document.Works.Length, document.Durations.Length),
            warningText: warnings.Count > 0 ? string.Join("\n", warnings) : null);
    }

    private static BasicCsvRowViewModel ToBasicRowViewModel(BasicCsvWork work)
    {
        var nodeKeys = work.Nodes.Select(node => node.Item1).ToList();
        var summary = string.Join(" · ", nodeKeys.Take(8));
        if (nodeKeys.Count > 8)
            summary += " …";

        return new BasicCsvRowViewModel
        {
            FlowName = work.FlowName,
            WorkName = work.WorkName,
            NodeCount = nodeKeys.Count,
            EdgeCount = work.Edges.Length,
            CallSummary = summary
        };
    }

    private static string BuildBasicPreviewSummary(BasicCsvPreview preview, int workCount, int durationCount)
    {
        var sb = new StringBuilder()
            .AppendLine($"✓ Flow: {preview.FlowNames.Length}개")
            .AppendLine($"✓ Work: {preview.WorkNames.Length}개 — 행 순서 StartReset 체인 {preview.WorkArrowCount}개")
            .AppendLine($"✓ Call 노드: {preview.CallNodeCount}개 / Start 엣지: {preview.CallEdgeCount}개")
            .AppendLine($"✓ Passive Device System: {preview.PassiveSystemNames.Length}개");

        // 동작 시간이 실제로 읽혔는지 확인할 수 있어야 한다 — 미지정은 기본 500ms 로 들어간다.
        sb.AppendLine(durationCount > 0
            ? $"✓ 동작 시간 지정: {durationCount}개 (나머지는 기본 500ms)"
            : "· 동작 시간 지정 없음 — 모든 동작이 기본 500ms (지정: 디바이스.액션=1000MS)");
        sb.AppendLine();

        AppendSample(sb, "Flow 샘플", preview.FlowNames, 5);
        AppendSample(sb, "Work 샘플", preview.WorkNames, 5);
        AppendSample(sb, "Device 샘플", preview.PassiveSystemNames, 5);

        if (workCount > 100)
        {
            sb.AppendLine();
            sb.AppendLine($"※ 총 {workCount}개 Work 중 100개만 미리보기에 표시됩니다.");
        }

        return sb.ToString().TrimEnd();
    }

    private void UpdateFormatDependentUi()
    {
        var basic = _detectedFormat == CsvFormat.Basic3;
        var ai = _detectedFormat == CsvFormat.AiModel;
        // 7열은 관계를 전부 명시하므로 표준 격자를 그대로 쓴다 — 별도 미리보기 격자를 만들지 않는다.
        PreviewBorderOf(basic ? Visibility.Collapsed : Visibility.Visible,
                        basic ? Visibility.Visible : Visibility.Collapsed);
        // Start/Clear 자동 추가는 기본 3열 매퍼에만 적용된다.
        // 7열은 토큰 역할을 WORK 행에 직접 적으므로 이 옵션이 의미를 갖지 않는다.
        if (AutoStartClearCheck != null)
            AutoStartClearCheck.Visibility = basic ? Visibility.Visible : Visibility.Collapsed;
        // 지침 버튼 문구는 형식을 따라간다 — 3열 지침을 7열에 쓰면 LLM 이 엉뚱한 것을 만든다.
        if (CopyPromptButton != null)
            CopyPromptButton.Content = ai ? "LLM 지침 복사 (7열)" : "LLM 지침 복사";
    }

    /// 아직 판별할 내용이 없는 상태. 오류가 아니므로 중립 색으로 둔다.
    private void SetFormatBadgeIdle()
    {
        _detectedFormat = CsvFormat.Unknown;
        FormatBadgeText.Text = "CSV 내용을 입력하면 형식(기본 3열 / 표준 8열 / 표준 9열 / 표준 11열)을 자동으로 인식합니다.";
        FormatBadge.SetResourceReference(Border.BackgroundProperty, "TertiaryBackgroundBrush");
        FormatBadgeText.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        UpdateFormatDependentUi();
    }

    /// 어느 규격으로 해석했는지는 반드시 보여야 한다 — 두 규격은 만드는 모델이 다르다
    /// (3열은 CALL DAG 해석·Start/Clear 자동 추가, 9열은 I/O 주소 배선).
    private void ShowFormatBadge(CsvHeaderInfo header)
    {
        if (header.IsRecognized)
        {
            var note = CsvFormatDetector.formatNote(header.Format);
            FormatBadgeText.Text =
                $"감지: {CsvFormatDetector.formatName(header.Format)}"
                + $" · {CsvFormatDetector.separatorName(header.Separator)} 구분 · {header.FieldCount}개 열"
                + (string.IsNullOrEmpty(note) ? "" : $" · {note}");
            FormatBadge.Background = RecognizedBadgeBackground;
            FormatBadgeText.SetResourceReference(TextBlock.ForegroundProperty, "GreenAccentBrush");
        }
        else
        {
            FormatBadgeText.Text = "형식을 인식하지 못했습니다 — 아래 오류의 헤더 비교를 확인하세요.";
            FormatBadge.Background = UnrecognizedBadgeBackground;
            FormatBadgeText.SetResourceReference(TextBlock.ForegroundProperty, "RedAccentBrush");
        }
    }

    private void PreviewBorderOf(Visibility standard, Visibility basic)
    {
        if (PreviewGrid?.Parent is FrameworkElement standardBorder)
            standardBorder.Visibility = standard;
        if (BasicPreviewBorder != null)
            BasicPreviewBorder.Visibility = basic;
    }

    private void ResetPreview(string message)
    {
        SetPreviewState(null, null, message);
    }

    private void ShowErrors(IEnumerable<string> errors)
    {
        SetPreviewState(null, null, PreviewFailureText, errorText: string.Join("\n", errors));
    }

    /// 7열 미리보기. **Capa 를 반드시 먼저 보여 준다** — Flow 개수를 공정 단계 수로 오해하는 것이
    /// 이 형식에서 가장 흔한 모델링 실수이고, 잘못 세면 동시에 물릴 제품 수가 달라진다.
    private void ApplyAiPreview(AiCsvDocument document, AiCsvPreview preview)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Active System : {preview.ActiveSystemName}");
        sb.AppendLine($"Capa          : {preview.Capa}  (= FLOW 행 개수 · 동시에 보유하는 고유 제품 수)");
        sb.AppendLine($"Work          : {preview.WorkCount}");
        sb.AppendLine($"화살표        : {preview.ArrowCount}");
        sb.AppendLine($"API           : {preview.ApiCount}");
        sb.AppendLine($"조건          : {preview.CondCount}");
        if (preview.PassiveSystemNames.Any())
            sb.AppendLine($"디바이스      : {string.Join(", ", preview.PassiveSystemNames)}");
        if (preview.UnspecifiedTimes > 0)
            sb.AppendLine($"시간 미기입   : {preview.UnspecifiedTimes}건 (기본 500ms 적용)");
        foreach (var w in preview.Warnings)
            sb.AppendLine($"경고 · {w}");

        ResetPreview(sb.ToString().TrimEnd());
        // ResetPreview → SetPreviewState 가 세 문서를 모두 비운다. 보관은 반드시 그 뒤에 한다.
        _aiDocument = document;
        _document = null;
        _basicDocument = null;
    }
}
