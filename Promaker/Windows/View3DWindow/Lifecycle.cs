using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Promaker.Presentation;
using Promaker.Services;

namespace Promaker.Windows;

public partial class View3DWindow
{
    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await WebView3D.EnsureCoreWebView2Async(await WebView2Host.CreateEnvironmentAsync());

            // 브라우저 프로세스가 죽으면(메모리 부족·강제 종료) 이후 CoreWebView2 접근이 예외를 던진다 — 표시해 두고 모든 접근을 막는다.
            WebView3D.CoreWebView2.ProcessFailed += OnWebViewProcessFailed;

            // wwwroot 폴더를 가상 호스트에 매핑
            var wwwroot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
            WebView3D.CoreWebView2.SetVirtualHostNameToFolderMapping(
                VirtualHostName,
                wwwroot,
                CoreWebView2HostResourceAccessKind.Allow);

            // JS → C# 콜백 수신 (선택 이벤트)
            WebView3D.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            // C# → JS 전송 델리게이트 주입
            _vm.SetWebViewSender(async json =>
            {
                if (Core is { } core)
                    await core.ExecuteScriptAsync($"handleFromCSharp({json})");
            });

            // 페이지 로드 완료 후 씬 빌드
            WebView3D.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

            // facility3d.html 로드
            WebView3D.Source = new Uri($"https://{VirtualHostName}/{FacilityHtmlPage}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[View3DWindow] WebView2 init failed: {ex.Message}");
            MessageBox.Show(
                $"3D 보기를 시작하지 못했습니다. WebView2 런타임이 설치돼 있는지 확인하세요.\n\n{ex.Message}",
                "3D 보기", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>브라우저 프로세스가 죽은 뒤 true — Core 가 null 을 돌려 모든 접근이 조용히 건너뛰어진다.</summary>
    private bool _browserProcessFailed;

    /// <summary>살아 있는 CoreWebView2. 초기화 전이거나 브라우저 프로세스가 죽었으면 null.</summary>
    private CoreWebView2? Core => _browserProcessFailed ? null : WebView2Host.TryGetCore(WebView3D);

    private void OnWebViewProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited
            && e.ProcessFailedKind != CoreWebView2ProcessFailedKind.RenderProcessExited
            && e.ProcessFailedKind != CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            return;
        _browserProcessFailed = true;
        _webViewReady = false;
        System.Diagnostics.Debug.WriteLine($"[View3DWindow] WebView2 process failed: {e.ProcessFailedKind}");
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (sender is CoreWebView2 navigated)
            navigated.NavigationCompleted -= OnNavigationCompleted;

        // 페이지 로드 직후 — init 메시지보다 먼저 테마를 적용해 흰 배경 깜빡임 방지.
        _webViewReady = true;
        PushThemeToWebView(ThemeManager.CurrentTheme);

        // three.js 는 CDN 에서 받는다 — 인터넷이 막힌 PC 에서는 빈 화면만 남으므로 이유를 알린다.
        try
        {
            if (Core is { } core && await core.ExecuteScriptAsync("typeof THREE !== 'undefined'") == "false")
                MessageBox.Show(
                    "3D 라이브러리(three.js)를 인터넷에서 불러오지 못해 3D 화면을 그릴 수 없습니다.\n인터넷 연결을 확인하세요.",
                    "3D 보기", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[View3DWindow] three.js probe failed: {ex.Message}");
        }

        if (_onReady != null)
        {
            try { await _onReady(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[View3DWindow] onReady failed: {ex.Message}");
            }
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var raw = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(raw)) return;

            var doc = JsonDocument.Parse(raw);
            var method = doc.RootElement.GetProperty("method").GetString() ?? "";
            var argsEl = doc.RootElement.GetProperty("args");
            var args = argsEl.EnumerateArray().ToArray();

            if (method == "RebuildWithAutoLayout")
            {
                if (_store != null)
                    _ = _vm.RebuildWithAutoLayout(_store, _projectId);
                return;
            }

            _vm.OnSelectionMessage(method, args);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[View3DWindow] WebMessage parse error: {ex.Message}");
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        ThemeManager.ThemeChanged -= OnPromakerThemeChanged;
        _webViewReady = false;
        if (Core is { } core)
        {
            try { _ = core.ExecuteScriptAsync("if(window.Ds2Sound)Ds2Sound.stopAll();"); }
            catch { }
            core.WebMessageReceived -= OnWebMessageReceived;
            core.ProcessFailed -= OnWebViewProcessFailed;
        }
        _vm.SetWebViewSender(null);
    }
}
