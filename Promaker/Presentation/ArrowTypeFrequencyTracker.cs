using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ds2.Core;
using log4net;

namespace Promaker.Presentation;

/// <summary>
/// 화살표 타입 핀(즐겨찾기) 설정을 관리합니다.
/// 핀된 타입만 우클릭 컨텍스트 메뉴에 표시됩니다.
/// </summary>
public static class ArrowTypeFrequencyTracker
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ArrowTypeFrequencyTracker));

    private static readonly string PinnedFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dualsoft", "Promaker", "Settings", "arrow-pinned.txt");

    private static HashSet<ArrowType>? _pinned;

    private static readonly HashSet<ArrowType> DefaultPinned =
        [ArrowType.Start, ArrowType.StartReset, ArrowType.Group];

    private static readonly Dictionary<ArrowType, string> DisplayNames = new()
    {
        [ArrowType.Start] = "시작 인과",
        [ArrowType.Reset] = "리셋 인과",
        [ArrowType.StartReset] = "시작리셋 인과",
        [ArrowType.ResetReset] = "상호리셋 인과",
        [ArrowType.Group] = "그룹연결",
    };

    public static string DisplayName(ArrowType type) =>
        DisplayNames.TryGetValue(type, out var name) ? name : type.ToString();

    public static bool IsPinned(ArrowType type)
    {
        EnsurePinnedLoaded();
        return _pinned!.Contains(type);
    }

    public static void TogglePin(ArrowType type)
    {
        EnsurePinnedLoaded();
        if (!_pinned!.Remove(type))
            _pinned.Add(type);
        SavePinned();
    }

    /// <summary>
    /// available 중 핀된 타입만 반환합니다.
    /// </summary>
    public static List<ArrowType> GetPinnedTypes(IEnumerable<ArrowType> available)
    {
        EnsurePinnedLoaded();
        return available.Where(t => _pinned!.Contains(t)).ToList();
    }

    public static void RecordUsage(ArrowType type)
    {
        EnsurePinnedLoaded();
        _pinned!.Add(type);
        SavePinned();
    }

    private static void EnsurePinnedLoaded()
    {
        if (_pinned is not null) return;

        if (!File.Exists(PinnedFilePath))
        {
            _pinned = new HashSet<ArrowType>(DefaultPinned);
            return;
        }

        // 설정 파일을 못 읽으면(권한·잠금) 기본값으로 쓴다 — 다른 설정과 같이 앱 시작을 막지 않는다.
        try
        {
            var pinned = new HashSet<ArrowType>();
            foreach (var line in File.ReadAllLines(PinnedFilePath))
            {
                if (Enum.TryParse<ArrowType>(line.Trim(), out var type))
                    pinned.Add(type);
            }
            _pinned = pinned;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"화살표 고정 목록을 읽지 못해 기본값을 씁니다: {PinnedFilePath}", ex);
            _pinned = new HashSet<ArrowType>(DefaultPinned);
        }
    }

    /// <summary>고정 목록 저장. 실패해도(읽기 전용·다른 인스턴스가 쓰는 중) 화면의 고정 상태는 유지하고 로그만 남긴다 —
    /// 예전엔 핀 토글이나 우클릭 연결 메뉴에서 예외로 앱이 종료됐다.</summary>
    private static void SavePinned()
    {
        try
        {
            var dir = Path.GetDirectoryName(PinnedFilePath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllLines(PinnedFilePath, _pinned!.Select(t => t.ToString()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"화살표 고정 목록을 저장하지 못했습니다: {PinnedFilePath}", ex);
        }
    }
}
