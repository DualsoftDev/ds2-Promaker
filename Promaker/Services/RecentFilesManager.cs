using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Promaker.Services;

/// <summary>
/// 최근에 열었던 파일 목록 관리
/// </summary>
public static class RecentFilesManager
{
    /// <summary>목록 파일 경로. 테스트가 임시 경로로 바꿔 쓴다.</summary>
    internal static string SettingsPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dualsoft", "Promaker", "RecentFiles", "recent_files.txt");

    private const int MaxRecentFiles = 10;

    /// <summary>
    /// 저장된 최근 파일 목록 로드. 항목의 존재 여부는 여기서 확인하지 않는다 — 끊긴 네트워크 경로의
    /// File.Exists 는 UI 스레드를 수십 초 멈추고, 잠시 빠진 드라이브의 파일을 목록에서 영구히 지워 버린다.
    /// 없는 파일은 여는 시점(OpenRecentFile)에 안내하고 RemoveRecentFile 로 정리한다.
    /// </summary>
    public static List<string> LoadRecentFiles()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new List<string>();

            return File.ReadAllLines(SettingsPath)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Trim())
                .Take(MaxRecentFiles)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// 파일 경로를 최근 목록에 추가하고 저장
    /// </summary>
    public static void AddRecentFile(string filePath)
    {
        try
        {
            var recentFiles = LoadRecentFiles();

            // 이미 존재하면 제거 (맨 앞으로 이동하기 위해)
            recentFiles.Remove(filePath);

            // 맨 앞에 추가
            recentFiles.Insert(0, filePath);

            // 최대 개수 제한
            if (recentFiles.Count > MaxRecentFiles)
                recentFiles = recentFiles.Take(MaxRecentFiles).ToList();

            SaveRecentFiles(recentFiles);
        }
        catch
        {
            // Ignore failures
        }
    }

    /// <summary>
    /// 목록에서 한 항목 제거(없는 파일을 열려 했을 때).
    /// </summary>
    public static void RemoveRecentFile(string filePath)
    {
        try
        {
            var recentFiles = LoadRecentFiles();
            if (recentFiles.Remove(filePath))
                SaveRecentFiles(recentFiles);
        }
        catch
        {
            // Ignore failures
        }
    }

    /// <summary>
    /// 최근 파일 목록 저장
    /// </summary>
    private static void SaveRecentFiles(List<string> files)
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllLines(SettingsPath, files);
        }
        catch
        {
            // Ignore persistence failures
        }
    }

    /// <summary>
    /// 최근 파일 목록 초기화
    /// </summary>
    public static void ClearRecentFiles()
    {
        try
        {
            if (File.Exists(SettingsPath))
                File.Delete(SettingsPath);
        }
        catch
        {
            // Ignore failures
        }
    }
}
