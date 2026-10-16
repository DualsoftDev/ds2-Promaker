using System;
using System.IO;
using System.Linq;
using System.Windows;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;

namespace Promaker.Dock;

public partial class DockHost
{
    /// <summary>
    /// PR-A3 — default 상태 캡쳐. 본 구현은 XmlLayoutSerializer 사용 안 함 — serializer 가 deserialize 시 새
    /// LayoutAnchorable/LayoutDocument 인스턴스를 생성해 DataContext 흐름(MainViewModel binding)이 깨지는 문제 회피.
    /// 대신 RegisterAnchor / RegisterDocument 호출 시 이미 default pane / 순서를 dictionary/list 에 박제했으므로
    /// 본 메서드는 그 박제가 유효함을 표시하는 단순 가드.
    /// </summary>
    public void CaptureDefaultLayout()
    {
        _defaultCaptured = true;
    }

    /// <summary>
    /// PR-A4 — layout 트리 자체를 default 구조로 재생성. 사용자가 anchor 를 옮겨 빈 pane 이
    /// AvalonDock 에 의해 garbage-collect 된 경우에도 정상 복원.
    /// <list type="number">
    /// <item>모든 등록 anchorable/document 를 현재 parent 에서 분리.</item>
    /// <item>XAML 박제와 동일한 LayoutPanel/LayoutAnchorablePane(Group)/LayoutDocumentPane(Group) 트리 새로 생성.</item>
    /// <item>x:Name 필드 (_leftPane, _documentPane 등) + <see cref="_defaultAnchorablePanes"/> 매핑을 새 pane 으로 재설정.</item>
    /// <item><see cref="_dockManager"/>.Layout 을 새 LayoutRoot 로 교체.</item>
    /// <item>등록된 anchor/document 를 default position 의 새 pane 에 재배치 (인스턴스 재사용 → DataContext 보존).</item>
    /// </list>
    /// </summary>
    public void ResetToDefaultLayout()
    {
        if (!_defaultCaptured)
        {
            System.Diagnostics.Trace.TraceWarning("DockHost.ResetToDefaultLayout: default not captured (CaptureDefaultLayout 미호출).");
            return;
        }

        // 1단계 — 모든 등록 item 을 현재 parent 에서 분리 (단일-parent 제약 회피).
        foreach (var item in _itemsByContentId.Values)
            DetachFromParent(item);

        // 2단계 — XAML 박제와 동일한 layout 트리 재생성 (PR-A6: 하단 단일 pane).
        var newLeftPane         = new LayoutAnchorablePane { DockWidth = new GridLength(320) };
        var newDocumentPane     = new LayoutDocumentPane();
        var newBottomPane       = new LayoutAnchorablePane { DockHeight = new GridLength(200) };
        var newRightTopPane     = new LayoutAnchorablePane();
        var newRightMiddlePane  = new LayoutAnchorablePane { DockHeight = new GridLength(220) };

        var docGroup = new LayoutDocumentPaneGroup();
        docGroup.Children.Add(newDocumentPane);

        var centerPanel = new LayoutPanel { Orientation = System.Windows.Controls.Orientation.Vertical };
        centerPanel.Children.Add(docGroup);
        centerPanel.Children.Add(newBottomPane);

        var rightGroup = new LayoutAnchorablePaneGroup
        {
            Orientation = System.Windows.Controls.Orientation.Vertical,
            DockWidth = new GridLength(280),
        };
        rightGroup.Children.Add(newRightTopPane);
        rightGroup.Children.Add(newRightMiddlePane);

        var rootPanel = new LayoutPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        rootPanel.Children.Add(newLeftPane);
        rootPanel.Children.Add(centerPanel);
        rootPanel.Children.Add(rightGroup);

        // 3단계 — x:Name 필드 + default mapping 을 새 pane 으로 갱신.
        _leftPane         = newLeftPane;
        _documentPane     = newDocumentPane;
        _bottomPane       = newBottomPane;
        _rightTopPane     = newRightTopPane;
        _rightMiddlePane  = newRightMiddlePane;

        foreach (var contentId in _defaultPositions.Keys.ToList())
            _defaultAnchorablePanes[contentId] = ResolveAnchorPane(_defaultPositions[contentId]);

        // 4단계 — DockingManager 에 새 LayoutRoot 설정 (visual 트리 즉시 갱신).
        _dockManager.Layout = new LayoutRoot { RootPanel = rootPanel };

        // 5단계 — 등록된 anchor/document 를 default 위치의 새 pane 에 재배치.
        foreach (var kvp in _defaultPositions)
        {
            if (_itemsByContentId.TryGetValue(kvp.Key, out var item) && item is LayoutAnchorable a)
                ResolveAnchorPane(kvp.Value).Children.Add(a);
        }
        foreach (var doc in _defaultDocumentOrder)
            _documentPane.Children.Add(doc);
    }

    /// <summary>
    /// PR-A4 — LayoutContent 를 현재 parent (LayoutAnchorablePane | LayoutDocumentPane) 에서 분리.
    /// parent 가 null 이거나 다른 type 이면 no-op (단일-parent 제약 회피 목적).
    /// </summary>
    private static void DetachFromParent(LayoutContent item)
    {
        switch (item)
        {
            case LayoutAnchorable a when a.Parent is LayoutAnchorablePane ap:
                ap.Children.Remove(a);
                break;
            case LayoutDocument d when d.Parent is LayoutDocumentPane dp:
                dp.Children.Remove(d);
                break;
        }
    }

    /// <summary>
    /// AvalonDock <see cref="XmlLayoutSerializer"/> wrapping. ContentId 매칭으로 기존 LayoutContent 인스턴스 재연결.
    /// 상위 디렉토리 미존재 시 생성.
    /// <para>
    /// 쓰기는 메모리에서 완성 → 임시 파일 → <see cref="File.Move(string, string, bool)"/> 로 처리한다.
    /// 대상 파일을 곧바로 <c>FileMode.Create</c> 로 열면 두 인스턴스가 동시에 종료할 때 서로의 절반 쓴
    /// 내용이 남아, 다음 실행이 깨진 layout 을 읽는다. 임시 파일명에 pid 를 넣어 임시 파일끼리의 충돌도 막는다.
    /// IO 실패는 삼키지 않고 호출 측으로 던진다 — 종료 경로에서의 처리는 MainWindow.Window_Closing 참조.
    /// </para>
    /// </summary>
    public void SaveLayout(string filepath)
    {
        if (string.IsNullOrEmpty(filepath)) throw new ArgumentException("filepath is required.", nameof(filepath));
        var dir = Path.GetDirectoryName(filepath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // 직렬화 도중 실패해도 기존 파일이 온전하도록 메모리에서 먼저 완성한다.
        var buffer = new MemoryStream();
        new XmlLayoutSerializer(_dockManager).Serialize(buffer);

        var tempPath = $"{filepath}.{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllBytes(tempPath, buffer.ToArray());
            File.Move(tempPath, filepath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* 임시 파일 정리 실패는 원인 예외를 가리지 않도록 무시 */ }
            throw;
        }
    }

    /// <summary>
    /// AvalonDock <see cref="XmlLayoutSerializer"/> 로 layout 복원. 파일 미존재 시 default layout 유지.
    /// parse / restore 실패 시에도 default 유지 (fail-safe — 외부 환경 예외).
    /// LayoutSerializationCallback 에서 ContentId 매칭으로 기존 등록된 Content 재연결.
    /// </summary>
    public void RestoreLayout(string filepath)
    {
        if (string.IsNullOrEmpty(filepath)) throw new ArgumentException("filepath is required.", nameof(filepath));
        if (!File.Exists(filepath)) return;

        try
        {
            var serializer = new XmlLayoutSerializer(_dockManager);
            serializer.LayoutSerializationCallback += (s, e) =>
            {
                // XML 안의 ContentId 와 매핑된 기존 인스턴스의 Content / 이벤트 hookup 을 그대로 재사용.
                if (e.Model is LayoutContent lc &&
                    !string.IsNullOrEmpty(lc.ContentId) &&
                    _itemsByContentId.TryGetValue(lc.ContentId, out var existing))
                {
                    e.Content = existing.Content;
                }
                else
                {
                    // 등록되지 않은 ContentId 는 skip (외부 환경 예외 — 구버전 layout xml 잔재 가능).
                    e.Cancel = true;
                }
            };
            using var fs = new FileStream(filepath, FileMode.Open, FileAccess.Read);
            serializer.Deserialize(fs);

            // Deserialize 는 layout 트리를 **새 인스턴스들로 교체**한다 (callback 은 Content 만 재연결).
            // 내부 레지스트리가 교체 전 XAML 인스턴스(고아)를 계속 가리키면 이후 SetAnchorVisible 이
            // 화면에 안 보이는 객체만 조작하게 된다(Welcome 박제 버그) — 새 트리 기준으로 재캡처.
            RecaptureFromRestoredLayout();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"DockHost.RestoreLayout failed for '{filepath}': {ex.Message}");
        }
    }

    /// <summary>
    /// RestoreLayout 직후 — 복원된 라이브 트리에서 ContentId 로 항목을 다시 찾아
    /// <see cref="_itemsByContentId"/>/<see cref="_defaultAnchorablePanes"/>/pane 필드를 재캡처.
    /// XML 에 없던 항목(저장 당시 hidden)은 기존 인스턴스를 유지 — show 시
    /// <see cref="EnsureLivePaneAt"/> fallback 이 라이브 pane 을 확보한다.
    /// </summary>
    private void RecaptureFromRestoredLayout()
    {
        var root = _dockManager.Layout;

        var liveById = root.Descendents()
            .OfType<LayoutContent>()
            .Concat(root.Hidden)
            .Where(lc => !string.IsNullOrEmpty(lc.ContentId))
            .GroupBy(lc => lc.ContentId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var contentId in _itemsByContentId.Keys.ToList())
        {
            if (!liveById.TryGetValue(contentId, out var live)) continue;

            var old = _itemsByContentId[contentId];
            _itemsByContentId[contentId] = live;

            // document 재배치 순서 보존용 리스트도 새 인스턴스로 교체.
            if (old is LayoutDocument oldDoc && live is LayoutDocument liveDoc)
            {
                var idx = _defaultDocumentOrder.IndexOf(oldDoc);
                if (idx >= 0) _defaultDocumentOrder[idx] = liveDoc;
            }

            // anchorable 이 라이브 pane 에 붙어 있으면 default pane 매핑 + 위치 필드 재캡처.
            if (live is LayoutAnchorable { Parent: LayoutAnchorablePane livePane })
            {
                _defaultAnchorablePanes[contentId] = livePane;
                if (_defaultPositions.TryGetValue(contentId, out var pos))
                {
                    switch (pos)
                    {
                        case DockAnchorPosition.Left: _leftPane = livePane; break;
                        case DockAnchorPosition.Bottom: _bottomPane = livePane; break;
                        case DockAnchorPosition.RightTop: _rightTopPane = livePane; break;
                        case DockAnchorPosition.RightMiddle: _rightMiddlePane = livePane; break;
                    }
                }
            }
        }

        // document pane 필드도 라이브 트리 기준으로.
        var liveDocPane = root.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault();
        if (liveDocPane is not null)
            _documentPane = liveDocPane;
    }
}
