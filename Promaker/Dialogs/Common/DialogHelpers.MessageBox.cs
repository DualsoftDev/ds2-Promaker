using System.Windows;
using System.Windows.Controls;

namespace Promaker.Dialogs;

internal static partial class DialogHelpers
{
    internal static MessageBoxResult ShowThemedMessageBox(
        string message, string title, MessageBoxButton buttons, string icon) =>
        ShowThemedMessageBox(Application.Current.MainWindow, message, title, buttons, icon, showDontShowAgain: false, out _);

    internal static MessageBoxResult ShowThemedMessageBox(
        Window? owner, string message, string title, MessageBoxButton buttons, string icon) =>
        ShowThemedMessageBox(owner, message, title, buttons, icon, showDontShowAgain: false, out _);

    internal static MessageBoxResult ShowThemedMessageBox(
        string message, string title, MessageBoxButton buttons, string icon,
        bool showDontShowAgain, out bool dontShowAgain) =>
        ShowThemedMessageBox(Application.Current.MainWindow, message, title, buttons, icon, showDontShowAgain, out dontShowAgain);

    /// <summary>"다시 보지 않기" 체크박스 포함 오버로드. <paramref name="dontShowAgainLabel"/>
    /// 미지정 시 기존 hardcode 라벨 ("다음 시뮬레이션까지 다시 보지 않기") — 시뮬 회기 scope 가 아닌
    /// 영구 persistence 가 필요한 호출자는 호출자 측에서 라벨 + AppSettingStore 결합.</summary>
    internal static MessageBoxResult ShowThemedMessageBox(
        Window? owner, string message, string title, MessageBoxButton buttons, string icon,
        bool showDontShowAgain, out bool dontShowAgain,
        string? dontShowAgainLabel = null)
    {
        var result = MessageBoxResult.None;
        dontShowAgain = false;

        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = (owner is { IsLoaded: true } ? owner : null),
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };

        var darkButtonStyle = Application.Current.TryFindResource("DarkButton") as Style;

        var iconBlock = new TextBlock
        {
            Text = icon,
            FontSize = 28,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 12, 0)
        };

        var messageBlock = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13
        };

        // 가변 길이 메시지(예: 예외 메시지 + log tail)가 화면 높이를 넘겨 버튼을 밀어내는 사고 차단 —
        // ScrollViewer 로 감싸고 MaxHeight 제한 (ShowGraphWarnings 와 동일 패턴). 짧은 메시지는 SizeToContent 로 그대로 작게,
        // 긴 메시지는 MaxHeight 에서 세로 스크롤 + 버튼 항상 노출. StackPanel(mainPanel) 안에서는 ScrollViewer 자체에 MaxHeight 필수
        // (StackPanel 이 자식에게 무한 높이를 주므로 dialog MaxHeight 만으로는 ScrollViewer 가 콘텐츠 전체로 측정돼 버튼이 다시 밀림).
        var messageScroller = new ScrollViewer
        {
            Content = messageBlock,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = SystemParameters.WorkArea.Height * 0.6,
            VerticalAlignment = VerticalAlignment.Center
        };

        var contentPanel = new Grid { Margin = new Thickness(16, 16, 16, 12) };
        contentPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        contentPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(iconBlock, 0);
        Grid.SetColumn(messageScroller, 1);
        contentPanel.Children.Add(iconBlock);
        contentPanel.Children.Add(messageScroller);

        CheckBox? dontShowCheck = null;
        if (showDontShowAgain)
        {
            dontShowCheck = new CheckBox
            {
                Content = dontShowAgainLabel ?? "다음 시뮬레이션까지 다시 보지 않기",
                IsChecked = true,
                Foreground = (System.Windows.Media.Brush?)Application.Current.TryFindResource("SecondaryTextBrush")
                             ?? System.Windows.SystemColors.ControlTextBrush,
                FontSize = 12,
                Margin = new Thickness(16, 0, 16, 10)
            };
        }

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 16)
        };

        Button MakeButton(string content, MessageBoxResult value, bool isDefault = false, bool isCancel = false)
        {
            var btn = new Button
            {
                Content = content,
                MinWidth = 70,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(4, 0, 0, 0),
                IsDefault = isDefault,
                IsCancel = isCancel
            };
            if (darkButtonStyle is not null) btn.Style = darkButtonStyle;
            btn.Click += (_, _) => { result = value; dialog.DialogResult = true; };
            return btn;
        }

        switch (buttons)
        {
            case MessageBoxButton.YesNoCancel:
                buttonPanel.Children.Add(MakeButton("예(Y)", MessageBoxResult.Yes, isDefault: true));
                buttonPanel.Children.Add(MakeButton("아니요(N)", MessageBoxResult.No));
                buttonPanel.Children.Add(MakeButton("취소", MessageBoxResult.Cancel, isCancel: true));
                break;
            case MessageBoxButton.YesNo:
                buttonPanel.Children.Add(MakeButton("예(Y)", MessageBoxResult.Yes, isDefault: true));
                buttonPanel.Children.Add(MakeButton("아니요(N)", MessageBoxResult.No, isCancel: true));
                break;
            case MessageBoxButton.OKCancel:
                buttonPanel.Children.Add(MakeButton("확인", MessageBoxResult.OK, isDefault: true));
                buttonPanel.Children.Add(MakeButton("취소", MessageBoxResult.Cancel, isCancel: true));
                break;
            default:
                buttonPanel.Children.Add(MakeButton("확인", MessageBoxResult.OK, isDefault: true, isCancel: true));
                break;
        }

        var root = new Border
        {
            Background = (System.Windows.Media.Brush?)Application.Current.TryFindResource("SecondaryBackgroundBrush")
                         ?? System.Windows.SystemColors.WindowBrush
        };
        var mainPanel = new StackPanel();
        mainPanel.Children.Add(contentPanel);
        if (dontShowCheck is not null)
            mainPanel.Children.Add(dontShowCheck);
        mainPanel.Children.Add(buttonPanel);
        root.Child = mainPanel;
        dialog.Content = root;

        if (dialog.ShowDialog() != true && result == MessageBoxResult.None)
            result = buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel;

        if (dontShowCheck is not null)
            dontShowAgain = dontShowCheck.IsChecked == true;

        return result;
    }
}
