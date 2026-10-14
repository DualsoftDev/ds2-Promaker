using System;
using System.IO;
using Promaker.Services;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// 도움말·정보 창의 링크는 셸 실행에 실패해도 앱을 죽이지 않는다. 종전엔 Process.Start 의 예외가 Click 핸들러를
/// 타고 DispatcherUnhandledException(Handled=false) 까지 올라가 "Fatal Error" 로 종료됐다.
/// </summary>
public sealed class ExternalLinkTests
{
    [Fact]
    public void Open_reports_failure_instead_of_throwing_when_the_target_cannot_be_opened()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.html");

        Assert.False(ExternalLink.Open(missing));
        Assert.False(ExternalLink.Open(""));
        Assert.False(ExternalLink.Open("   "));
    }
}
