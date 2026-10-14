using System;
using System.IO;
using Promaker.Services;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// 최근 파일 목록은 읽을 때 파일 존재를 검사하지 않는다. 종전엔 로드마다 File.Exists 를 돌려 끊긴 네트워크 경로에서
/// 시작이 수십 초 멈췄고, 잠시 빠진 드라이브의 항목은 다음 저장에서 영구히 사라졌다. 없는 파일은 여는 시점에
/// 안내하고 그때 지운다.
/// </summary>
public sealed class RecentFilesManagerTests
{
    [Fact]
    public void Loading_keeps_entries_whose_files_are_missing_and_removal_is_explicit()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"recent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var original = RecentFilesManager.SettingsPath;
        RecentFilesManager.SettingsPath = Path.Combine(dir, "recent_files.txt");
        try
        {
            var missing = Path.Combine(dir, "unplugged-drive.sdf");
            var present = Path.Combine(dir, "here.sdf");
            File.WriteAllText(present, "");

            RecentFilesManager.AddRecentFile(missing);
            RecentFilesManager.AddRecentFile(present);

            var loaded = RecentFilesManager.LoadRecentFiles();
            Assert.Contains(missing, loaded);
            Assert.Contains(present, loaded);

            RecentFilesManager.RemoveRecentFile(missing);

            var afterRemoval = RecentFilesManager.LoadRecentFiles();
            Assert.DoesNotContain(missing, afterRemoval);
            Assert.Contains(present, afterRemoval);
        }
        finally
        {
            RecentFilesManager.SettingsPath = original;
            Directory.Delete(dir, true);
        }
    }
}
