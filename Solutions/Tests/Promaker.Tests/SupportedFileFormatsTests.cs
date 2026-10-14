using System;
using System.Linq;
using Promaker.Presentation;
using Promaker.Services;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// 열 수 있는 형식의 정의가 두 곳(열기 대화상자 필터, 드래그 앤 드롭)에 있다. 둘이 어긋나면 한쪽에선 열리고
/// 한쪽에선 거부되는 파일이 생긴다 — YAML 이 그랬다(필터·드롭엔 남고 열기 분기는 없어 "열리는 척" 실패).
/// </summary>
public sealed class SupportedFileFormatsTests
{
    private static readonly string[] Removed = [".yaml", ".yml"];

    [Fact]
    public void Open_dialog_filter_and_drag_drop_accept_the_same_extensions()
    {
        StaTestRunner.Run(() =>
        {
            var allSupported = MainViewModel.FileFilter.Split('|')[1]
                .Split(';')
                .Select(pattern => pattern.Trim().TrimStart('*'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var dropped = MainWindow.SupportedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.Equal(dropped, allSupported);
            Assert.Contains(FileExtensions.Sdf, allSupported);
            Assert.Contains(FileExtensions.Json, allSupported);
            Assert.Contains(FileExtensions.Aasx, allSupported);
            Assert.Contains(FileExtensions.Mermaid, allSupported);
            foreach (var ext in Removed)
                Assert.DoesNotContain(ext, allSupported);
        });
    }

    [Fact]
    public void Every_accepted_extension_has_an_open_path()
    {
        StaTestRunner.Run(() =>
        {
            // 열기 분기는 Mermaid / AASX / 그 외(JSON·SDF 모델 파일) 세 갈래다 — 어느 갈래에도 속하지 않는 확장자가
            // 목록에 있으면 그 파일은 모델 파일로 역직렬화돼 "프로젝트가 없는 파일" 로 거절된다.
            foreach (var ext in MainWindow.SupportedExtensions)
            {
                var probe = "x" + ext;
                var isModel = ext.Equals(FileExtensions.Sdf, StringComparison.OrdinalIgnoreCase)
                              || ext.Equals(FileExtensions.Json, StringComparison.OrdinalIgnoreCase);
                Assert.True(FileTypeProbe.IsMermaid(probe) || FileTypeProbe.IsAasx(probe) || isModel,
                    $"{ext} 는 열기 분기가 없다");
            }
        });
    }
}
