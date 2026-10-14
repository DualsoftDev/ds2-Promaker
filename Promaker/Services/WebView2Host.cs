using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Promaker.Services;

/// <summary>
/// WebView2 공용 설정. 사용자 데이터 폴더를 %LOCALAPPDATA% 아래로 지정한다 — 기본값(exe 옆)은 Program Files 설치본에서
/// 쓰기가 막혀 초기화가 E_ACCESSDENIED 로 실패했고, 3D 보기와 모델 미리보기가 안내 없이 빈 화면이었다.
/// </summary>
internal static class WebView2Host
{
    public static string UserDataFolder =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Dualsoft", "Promaker", "WebView2");

    public static Task<CoreWebView2Environment> CreateEnvironmentAsync() =>
        CoreWebView2Environment.CreateAsync(null, UserDataFolder);

    /// <summary>CoreWebView2 를 꺼낸다. 브라우저 프로세스가 죽은 뒤에는 getter 자체가 InvalidOperationException 을 던지므로 null.</summary>
    public static CoreWebView2? TryGetCore(WebView2? view)
    {
        try { return view?.CoreWebView2; }
        catch (InvalidOperationException) { return null; }
    }
}
