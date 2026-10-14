using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Promaker.ViewModels;

/// <summary>
/// 커스텀 JSON 디바이스 모델 레지스트리.
/// %AppData%\Dualsoft\Promaker\custom-models\ 에 *.device.json 파일을 관리한다.
/// 모든 프로젝트에서 공유되는 공통 저장소.
/// </summary>
public class CustomModelRegistry
{
    private const string CompanyFolder = "Dualsoft";
    private const string AppFolder = "Promaker";
    private const string ModelFolder = "custom-models";
    private const string FilePattern = "*.device.json";

    private readonly string _modelsDir;

    /// <summary>현재 등록된 모델 목록 (이름 → JSON 텍스트). 파일 이름이 키라 Windows 파일 시스템처럼 대소문자를 구분하지
    /// 않는다 — 구분하면 "Robot_A" 가 있는데 "ROBOT_A" 로 등록할 때 덮어쓰기 확인 없이 같은 파일을 덮어썼다.</summary>
    public Dictionary<string, string> Models { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>현재 등록된 모델 이름 목록</summary>
    public IReadOnlyList<string> ModelNames => Models.Keys.OrderBy(k => k).ToList();

    /// <summary>모델 저장 폴더 경로</summary>
    public string ModelsDirectory => _modelsDir;

    public CustomModelRegistry()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            CompanyFolder, AppFolder, ModelFolder))
    {
    }

    /// <summary>모델 폴더를 지정한다 — 테스트가 임시 폴더로 쓴다.</summary>
    internal CustomModelRegistry(string modelsDir)
    {
        _modelsDir = modelsDir;
    }

    /// <summary>
    /// 모델 폴더에서 모든 *.device.json 파일 로드
    /// </summary>
    public void LoadAll()
    {
        Models.Clear();

        if (!Directory.Exists(_modelsDir))
            return;

        foreach (var file in Directory.GetFiles(_modelsDir, FilePattern))
        {
            try
            {
                var json = File.ReadAllText(file);
                var name = ExtractNameFromFileName(file);
                if (!string.IsNullOrEmpty(name))
                    Models[name] = json;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CustomModel] Failed to load {file}: {ex.Message}");
            }
        }

        System.Diagnostics.Debug.WriteLine(
            $"[CustomModel] Loaded {Models.Count} custom models from {_modelsDir}");
    }

    /// <summary>
    /// 모델 저장 (파일명 = systemType.device.json)
    /// </summary>
    public bool TrySave(string systemType, string jsonText, out string? error)
    {
        error = ValidateName(systemType);
        if (error is not null) return false;
        try
        {
            EnsureDirectory();
            File.WriteAllText(GetFilePath(systemType), jsonText);
            Models[systemType] = jsonText;
            return true;
        }
        catch (Exception ex)
        {
            error = $"모델 파일을 저장하지 못했습니다: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 모델 삭제. 파일이 읽기 전용이거나 다른 프로그램이 잡고 있으면 false 와 사유를 돌려준다.
    /// </summary>
    public bool TryDelete(string systemType, out string? error)
    {
        error = null;
        try
        {
            var filePath = GetFilePath(systemType);
            if (File.Exists(filePath))
                File.Delete(filePath);
            Models.Remove(systemType);
            return true;
        }
        catch (Exception ex)
        {
            error = $"모델 파일을 삭제하지 못했습니다: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 편집 중 이름을 바꿔 저장 — 새 이름으로 저장이 성공한 뒤에만 옛 파일을 지운다. 예전엔 옛 파일을 먼저 지워, 저장이
    /// 실패하거나 확인창에서 취소하면 모델이 통째로 사라졌다. 옛 파일 삭제만 실패하면 저장은 유지하고 사유를 돌려준다.
    /// </summary>
    public bool TryRename(string oldSystemType, string newSystemType, string jsonText, out string? error)
    {
        if (!TrySave(newSystemType, jsonText, out error)) return false;
        if (string.Equals(oldSystemType, newSystemType, StringComparison.OrdinalIgnoreCase)) return true;
        if (!TryDelete(oldSystemType, out var deleteError))
            error = $"새 이름으로 저장했지만 이전 파일은 지우지 못했습니다. {deleteError}";
        return true;
    }

    /// <summary>SystemType 이 모델 파일 이름으로 쓸 수 있는지. 쓸 수 없으면 사유, 쓸 수 있으면 null.</summary>
    public static string? ValidateName(string? systemType)
    {
        if (string.IsNullOrWhiteSpace(systemType)) return "SystemType 이 비어 있습니다.";
        var invalid = systemType.Where(c => Path.GetInvalidFileNameChars().Contains(c)).Distinct().ToArray();
        return invalid.Length == 0
            ? null
            : $"SystemType 에 파일 이름으로 쓸 수 없는 문자가 있습니다: {string.Join(" ", invalid.Select(c => $"'{c}'"))}";
    }

    /// <summary>
    /// JSON 유효성 검증 (파싱 가능 + name/height/parts|chain 존재)
    /// </summary>
    public static (bool isValid, string error) Validate(string jsonText)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            // 최상위가 객체가 아니면(배열·숫자·문자열) TryGetProperty 가 InvalidOperationException 을 던진다 — 입력하는 도중
            // 잠깐 "[" 나 숫자만 남아도 TextChanged 검증에서 앱이 종료됐다.
            if (root.ValueKind != JsonValueKind.Object)
                return (false, "최상위는 { } 객체여야 합니다.");

            if (!root.TryGetProperty("name", out _))
                return (false, "\"name\" 필드가 없습니다.");

            if (!root.TryGetProperty("height", out _))
                return (false, "\"height\" 필드가 없습니다.");

            var hasParts = root.TryGetProperty("parts", out _);
            var hasChain = root.TryGetProperty("chain", out _);
            if (!hasParts && !hasChain)
                return (false, "\"parts\" 또는 \"chain\" 필드가 필요합니다.");

            return (true, string.Empty);
        }
        catch (JsonException ex)
        {
            return (false, $"JSON 구문 오류: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"JSON 검증 실패: {ex.Message}");
        }
    }

    /// <summary>
    /// 모든 모델을 System.Text.Json 직렬화 가능한 딕셔너리로 반환
    /// (ThreeDViewState.SendInitMessage에서 WebView2로 전달용)
    /// </summary>
    public Dictionary<string, object> ToSerializableDictionary()
    {
        var result = new Dictionary<string, object>();
        foreach (var (name, json) in Models)
        {
            try
            {
                var obj = JsonSerializer.Deserialize<JsonElement>(json);
                result[name] = obj;
            }
            catch
            {
                // 파싱 실패한 모델은 건너뜀
            }
        }
        return result;
    }

    /// <summary>
    /// 등록된 모든 커스텀 모델 이름 (F# DevicePresets.registerCustomNames용)
    /// </summary>
    public IEnumerable<string> GetRegisteredNames() => Models.Keys;

    /// <summary>
    /// device.json의 "apiDefs" 필드에서 ApiDef 이름 목록을 추출.
    /// 필드가 없거나 파싱 실패 시 빈 리스트.
    /// </summary>
    public List<string> GetApiDefs(string systemType)
    {
        if (!Models.TryGetValue(systemType, out var json)) return new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("apiDefs", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return new List<string>();

            var result = new List<string>();
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) result.Add(s);
                }
            }
            return result;
        }
        catch { return new List<string>(); }
    }

    // ── Private Helpers ──────────────────────────────────────────

    private void EnsureDirectory()
    {
        if (!Directory.Exists(_modelsDir))
            Directory.CreateDirectory(_modelsDir);
    }

    private string GetFilePath(string systemType)
        => Path.Combine(_modelsDir, $"{systemType}.device.json");

    private static string ExtractNameFromFileName(string filePath)
        => Path.GetFileNameWithoutExtension(filePath).Replace(".device", "");
}
