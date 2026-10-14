using System;
using System.Diagnostics;
using log4net;

namespace Promaker.Services;

/// <summary>
/// 외부 URL 을 기본 브라우저로 연다. 셸 실행 실패(연결 프로그램 없음, 정책 차단 등)는 앱을 죽일 일이 아니라
/// 경고 로그로 남긴다 — 도움말·정보 창의 링크 클릭이 DispatcherUnhandledException(Handled=false) 로
/// 프로세스를 종료시키던 경로를 막는다.
/// </summary>
internal static class ExternalLink
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ExternalLink));

    /// <returns>브라우저 실행을 요청했으면 true, 실패했으면 false(로그에 사유).</returns>
    public static bool Open(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"외부 링크 열기 실패: {url}", ex);
            return false;
        }
    }
}
