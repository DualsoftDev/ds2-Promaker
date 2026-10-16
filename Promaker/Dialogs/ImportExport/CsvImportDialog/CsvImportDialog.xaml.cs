using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Ds2.CSV;
using Promaker.Presentation;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Microsoft.Win32;

namespace Promaker.Dialogs;

public partial class CsvImportDialog : Window
{
    private const string DefaultImportedName = "csv_import";
    private const string DefaultSourceText = "또는 아래에 CSV를 직접 붙여넣으세요.";
    private const string EmptyPreviewText = "CSV 내용을 붙여넣거나 CSV 파일 불러오기를 누르세요.";
    private const string PreviewFailureText = "미리보기를 생성하지 못했습니다.";

    private CsvFormat _detectedFormat = CsvFormat.Unknown;
    private CsvDocument? _document;
    private BasicCsvDocument? _basicDocument;
    private AiCsvDocument? _aiDocument;
    private string _lastErrorText = "";
    private string _autoProjectName = DefaultImportedName;
    private string _autoSystemName = DefaultImportedName;
    private string _sourceDisplayName = "붙여넣기";
    private bool _loadingFileContent;

    public CsvImportDialog()
    {
        InitializeComponent();

        ProjectNameBox.Text = DefaultImportedName;
        SystemNameBox.Text = DefaultImportedName;
        SourceText.Text = DefaultSourceText;
        ResetPreview(EmptyPreviewText);
        SetFormatBadgeIdle();

        Loaded += (_, _) => ContentBox.Focus();
    }

    public string ProjectName => ProjectNameBox.Text.Trim();

    public string SystemName => SystemNameBox.Text.Trim();

    public CsvDocument Document =>
        _document ?? throw new InvalidOperationException("CSV document is not loaded.");

    /// 헤더로 판별된 규격. 내용이 비었거나 판별에 실패했으면 직전 판별값(없으면 Unknown).
    public CsvFormat DetectedFormat => _detectedFormat;

    /// 기본 3열 모드에서 Start/Clear Work 를 자동 추가할지. 기본 켜짐.
    public bool AutoAddStartClear => AutoStartClearCheck?.IsChecked == true;

    public BasicCsvDocument BasicDocument =>
        _basicDocument ?? throw new InvalidOperationException("Basic CSV document is not loaded.");

    public AiCsvDocument AiDocument =>
        _aiDocument ?? throw new InvalidOperationException("AI CSV document is not loaded.");

    public string SourceDisplayName => _sourceDisplayName;

    private static bool ValidateRequired(Window owner, TextBox textBox, string label)
    {
        if (!string.IsNullOrWhiteSpace(textBox.Text?.Trim()))
            return true;

        DialogHelpers.Info(owner, $"{label} 이름을 입력하세요.", "CSV 불러오기");
        textBox.Focus();
        return false;
    }

    private static string OptionText(FSharpOption<string> value) =>
        value.GetOrDefault("");

    private void SetSourceDisplay(string displayName, string description)
    {
        _sourceDisplayName = displayName;
        SourceText.Text = description;
    }

    private void ResetDirectInputPreview()
    {
        SetSourceDisplay("붙여넣기", DefaultSourceText);
        SetFormatBadgeIdle();
        ResetPreview(EmptyPreviewText);
    }

    private void ShowInfo(string message, string title = "CSV 불러오기") =>
        DialogHelpers.Info(this, message, title);

    private void ShowError(string message, string title = "오류") =>
        DialogHelpers.Error(this, message, title);

    private void ApplyAutoNames(string defaultName)
    {
        var normalized = string.IsNullOrWhiteSpace(defaultName)
            ? DefaultImportedName
            : defaultName.Trim();

        UpdateAutoName(ProjectNameBox, ref _autoProjectName, normalized);
        UpdateAutoName(SystemNameBox, ref _autoSystemName, normalized);
    }

    private static void UpdateAutoName(TextBox textBox, ref string previousAutoName, string nextAutoName)
    {
        var current = textBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(current) || string.Equals(current, previousAutoName, StringComparison.Ordinal))
            textBox.Text = nextAutoName;

        previousAutoName = nextAutoName;
    }

    private bool TryLoadDocument()
    {
        var content = ContentBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            SetFormatBadgeIdle();
            ResetPreview(EmptyPreviewText);
            return false;
        }

        // 헤더가 곧 형식 선언이다. 세 규격의 헤더는 서로소라 판별이 결정적이므로 사용자에게 묻지 않는다.
        var header = CsvFormatDetector.detect(content);
        ShowFormatBadge(header);

        if (!header.IsRecognized)
        {
            // 판별 실패면 본문은 파싱하지 않는다. 가까워 보이는 파서에 먹이면 헤더 한 글자 문제가
            // 행마다 쏟아지는 데이터 오류로 둔갑해 진짜 원인을 가린다.
            // _detectedFormat 은 직전 값을 유지한다 — 타이핑 도중 미리보기 격자가 튀지 않도록.
            ShowErrors(new[] { header.Diagnostic });
            return false;
        }

        _detectedFormat = header.Format;
        UpdateFormatDependentUi();

        if (_detectedFormat == CsvFormat.Basic3)
        {
            var basicResult = CsvImporter.parseBasicContent(content);
            if (basicResult.IsError)
            {
                ShowErrors(basicResult.ErrorValue);
                return false;
            }

            var basicDocument = basicResult.ResultValue;
            ApplyBasicPreview(basicDocument, CsvImporter.previewBasic(basicDocument));
            return true;
        }

        if (_detectedFormat == CsvFormat.AiModel)
        {
            var aiResult = CsvImporter.parseAiContent(content);
            if (aiResult.IsError)
            {
                ShowErrors(aiResult.ErrorValue);
                return false;
            }

            var aiDocument = aiResult.ResultValue;
            ApplyAiPreview(aiDocument, CsvImporter.previewAi(aiDocument));
            return true;
        }

        if (!TryGetDocument(CsvImporter.parseContent(content), out var document))
            return false;

        ApplyPreview(document, CsvImporter.preview(document));
        return true;
    }

    private bool TryGetDocument(FSharpResult<CsvDocument, FSharpList<string>> result, out CsvDocument document)
    {
        if (result.IsError)
        {
            ShowErrors(result.ErrorValue);
            document = default!;
            return false;
        }

        document = result.ResultValue;
        return true;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*"
        };

        if (picker.ShowDialog() != true)
            return;

        try
        {
            _loadingFileContent = true;
            ContentBox.Text = CsvFileHelper.ReadAllTextShared(picker.FileName);
            SetSourceDisplay(Path.GetFileName(picker.FileName), $"원본: {Path.GetFileName(picker.FileName)}");
            ApplyAutoNames(Path.GetFileNameWithoutExtension(picker.FileName));
        }
        catch (Exception ex)
        {
            ShowPreviewFailure($"파일 읽기 실패: {ex.Message}");
        }
        finally
        {
            _loadingFileContent = false;
        }
    }

    private void ContentBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
            return;

        if (string.IsNullOrWhiteSpace(ContentBox.Text))
        {
            ResetDirectInputPreview();
            return;
        }

        if (!_loadingFileContent)
            SetSourceDisplay("붙여넣기", "원본: 직접 입력");

        // 붙여넣은 텍스트에 짝 없는 서로게이트 같은 깨진 문자가 있으면 정규화 단계에서 예외가 난다 — 미리보기 실패로 끝낸다.
        try
        {
            TryLoadDocument();
        }
        catch (Exception ex)
        {
            ResetPreview($"미리보기를 만들 수 없습니다: {ex.Message}");
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateRequired(this, ProjectNameBox, "Project"))
            return;

        if (!TryLoadDocument())
        {
            var detail = string.IsNullOrWhiteSpace(_lastErrorText) ? "" : $"\n\n{_lastErrorText}";
            ShowInfo($"유효한 CSV 내용을 먼저 입력하세요.{detail}");
            ContentBox.Focus();
            return;
        }

        // 모든 행에 ActiveSystem 이 적혀 있으면 입력한 System 이름은 쓰이지 않는다(#250).
        if (!EveryRowNamesActiveSystem() && !ValidateRequired(this, SystemNameBox, "Active System"))
            return;

        DialogResult = true;
    }

    private bool EveryRowNamesActiveSystem() =>
        _document is { } document
        && document.Entries.Length > 0
        && document.Entries.All(entry => !string.IsNullOrWhiteSpace(OptionText(entry.ActiveSystemName)));
}
