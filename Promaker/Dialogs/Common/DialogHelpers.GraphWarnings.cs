using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Promaker.Dialogs;

internal enum WarningSeverity { Red, Yellow }

internal record GraphWarningSection(
    string Title,
    WarningSeverity Severity,
    List<string> Lines,
    string? Detail = null);

internal static partial class DialogHelpers
{
    /// <summary>그래프 검증 경고를 심각도별 색상으로 표시합니다.</summary>
    internal static void ShowGraphWarnings(List<GraphWarningSection> sections)
    {
        if (sections.Count == 0) return;

        var bgBrush = (Brush?)Application.Current.TryFindResource("SecondaryBackgroundBrush")
                      ?? SystemColors.WindowBrush;
        var fgBrush = (Brush?)Application.Current.TryFindResource("PrimaryTextBrush")
                      ?? SystemColors.ControlTextBrush;
        var dialog = new Window
        {
            Title = "그래프 검증 경고",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = bgBrush,
            Foreground = fgBrush
        };

        var darkButtonStyle = Application.Current.TryFindResource("DarkButton") as Style;

        var textBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Margin = new Thickness(0)
        };

        var isDark = Presentation.ThemeManager.CurrentTheme == Presentation.AppTheme.Dark;
        var redColor = Brushes.OrangeRed;
        var yellowColor = isDark ? Brushes.Gold : Brushes.DarkOrange;

        // 화면 표시(textBlock)와 동일한 내용을 plain text 로도 누적 — "복사" 버튼용.
        var clipboardText = new StringBuilder();

        foreach (var section in sections)
        {
            var titleColor = section.Severity == WarningSeverity.Red
                ? redColor
                : yellowColor;

            clipboardText.AppendLine($"[{section.Title}]");
            textBlock.Inlines.Add(new Run($"[{section.Title}]") { Foreground = titleColor, FontWeight = FontWeights.Bold });
            textBlock.Inlines.Add(new LineBreak());
            foreach (var line in section.Lines)
            {
                clipboardText.AppendLine(line);
                textBlock.Inlines.Add(new Run(line) { Foreground = fgBrush });
                textBlock.Inlines.Add(new LineBreak());
            }
            if (!string.IsNullOrWhiteSpace(section.Detail))
            {
                clipboardText.AppendLine(section.Detail);
                textBlock.Inlines.Add(new Run(section.Detail) { Foreground = titleColor, FontSize = 11 });
                textBlock.Inlines.Add(new LineBreak());
            }
            clipboardText.AppendLine();
            textBlock.Inlines.Add(new LineBreak());
        }

        textBlock.Inlines.Add(new Run("시뮬레이션은 계속 진행됩니다.") { Foreground = fgBrush });

        var iconBlock = new TextBlock
        {
            Text = IconWarn,
            FontSize = 28,
            Foreground = Brushes.Gold,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 12, 0)
        };

        var scrollViewer = new ScrollViewer
        {
            Content = textBlock,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 350
        };

        var contentPanel = new Grid { Margin = new Thickness(16, 16, 16, 12) };
        contentPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        contentPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(iconBlock, 0);
        Grid.SetColumn(scrollViewer, 1);
        contentPanel.Children.Add(iconBlock);
        contentPanel.Children.Add(scrollViewer);

        var copyButton = new Button
        {
            Content = "복사", MinWidth = 70, Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 8, 0)
        };
        if (darkButtonStyle is not null) copyButton.Style = darkButtonStyle;
        copyButton.Click += (_, _) =>
        {
            // 다른 앱(원격 데스크톱 rdpclip, 클립보드 관리자)이 클립보드를 잡고 있으면 SetDataObject 도 재시도 뒤
            // COMException 을 던진다 — 복사 실패로 알리고 대화상자는 그대로 둔다.
            try
            {
                Clipboard.SetDataObject(clipboardText.ToString().TrimEnd(), true);
                copyButton.Content = "복사(됨)";
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                copyButton.Content = "복사 실패";
            }
        };

        var okButton = new Button
        {
            Content = "확인", MinWidth = 70, Padding = new Thickness(12, 4, 12, 4),
            IsDefault = true, IsCancel = true
        };
        if (darkButtonStyle is not null) okButton.Style = darkButtonStyle;
        okButton.Click += (_, _) => dialog.DialogResult = true;

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 16, 16)
        };
        buttonPanel.Children.Add(copyButton);
        buttonPanel.Children.Add(okButton);

        var root = new Border { Background = bgBrush };
        var mainPanel = new StackPanel();
        mainPanel.Children.Add(contentPanel);
        mainPanel.Children.Add(buttonPanel);
        root.Child = mainPanel;
        dialog.Content = root;
        dialog.ShowDialog();
    }
}
