using System;
using System.IO;

namespace Promaker.Services;

/// <summary>
/// AppData 의 Promaker 설정 파일 경로 단일 출처.
/// 이전엔 Dualsoft\Promaker 직속이었으나 Settings 하위 폴더로 이동.
/// 첫 호출 시 옛 위치에 파일이 있고 새 위치에는 없으면 자동 이동(1회) — 사용자 설정 보존.
/// </summary>
public static class SettingsPaths
{
    private static readonly string AppDataRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "Dualsoft", "Promaker");

    private static readonly string SettingsRoot = Path.Combine(AppDataRoot, "Settings");

    private static readonly string[] MovedFileNames =
    {
        "splitDeviceAasx.txt",
        "iriPrefix.txt",
        "createDefaultEntitiesOnEmptyAasx.txt",
    };

    private static bool _migrated;

    public static string Of(string fileName)
    {
        EnsureMigrated();
        return Path.Combine(SettingsRoot, fileName);
    }

    public static string SplitDeviceAasx                 => Of("splitDeviceAasx.txt");
    public static string IriPrefix                       => Of("iriPrefix.txt");
    public static string CreateDefaultEntitiesOnEmptyAasx => Of("createDefaultEntitiesOnEmptyAasx.txt");

    /// <summary>사용자 정의 AASX 템플릿 폴더 경로. 폴더 안의 *.aasx 의 모든 SM 이 export 시 자동 첨부.</summary>
    public static string AasxUserTemplatesFolder         => Of("aasxUserTemplatesFolder.txt");

    /// <summary>Log tab ComboBox 의 선택 LogLevelChoice (Debug/Info/Warn). AppLogState 가 세션 간 영속화.</summary>
    public static string LogFilterLevel                  => Of("logFilterLevel.txt");

    /// <summary>간트 표시 윈도우(분) — 간트 차트 헤더 드롭다운(5~300분). 순수 뷰 설정.</summary>
    public static string GanttWindowMinutes              => Of("ganttWindowMinutes.txt");

    /// <summary>AASX 사용자 템플릿 폴더 — 디폴트 위치 (AppData\Dualsoft\Promaker\AasxUserTemplates).</summary>
    public static string DefaultAasxUserTemplatesDir => Path.Combine(AppDataRoot, "AasxUserTemplates");

    /// <summary>PLC 템플릿 사용자 복사본 폴더 — AppData\Dualsoft\Promaker\PlcTemplate</summary>
    public static string PlcTemplateDir => Path.Combine(AppDataRoot, "PlcTemplate");

    /// <summary>인스턴스 간 시스템 복사의 파일 채널 스풀 — OS 클립보드가 막혀도 복사/붙여넣기가
    /// 성립하게 하는 정본 저장소. 사용자별 AppData 라 같은 계정의 두 인스턴스가 공유한다.</summary>
    public static string ClipboardSpoolDir => Path.Combine(AppDataRoot, "ClipboardSpool");

    /// <summary>IO 생성이 FB 포트 목록을 읽는 XGI_Template.xml 위치 — 없으면 동봉 리소스에서 추출해 둔다.</summary>
    public static string DefaultXgiTemplate => Path.Combine(PlcTemplateDir, "XGI_Template.xml");

    /// <summary>옛 경로(AppData\Dualsoft\Promaker\xxx) → 새 경로(Settings\xxx) 1회 이동.</summary>
    private static void EnsureMigrated()
    {
        if (_migrated) return;
        _migrated = true;

        try
        {
            Directory.CreateDirectory(SettingsRoot);
            foreach (var name in MovedFileNames)
            {
                var oldPath = Path.Combine(AppDataRoot, name);
                var newPath = Path.Combine(SettingsRoot, name);
                if (File.Exists(oldPath) && !File.Exists(newPath))
                    File.Move(oldPath, newPath);
            }
        }
        catch { /* 마이그레이션 실패해도 신규 경로는 그대로 사용 */ }
    }
}
