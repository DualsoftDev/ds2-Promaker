using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Promaker.Dialogs;

internal static partial class DialogHelpers
{
    internal const string IconWarn  = "⚠";
    internal const string IconInfo  = "ℹ";
    internal const string IconError = "✖";
    internal const string IconQuestion = "?";

    internal static bool ShowOwnedDialog(Window dialog)
    {
        dialog.Owner = Application.Current.MainWindow;
        dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        return dialog.ShowDialog() == true;
    }

    internal static void Warn(string message) =>
        Warn(Application.Current.MainWindow, message);

    internal static void Warn(Window? owner, string message, string title = "경고") =>
        ShowThemedMessageBox(owner, message, title, MessageBoxButton.OK, IconWarn);

    internal static void Info(string message) =>
        Info(Application.Current.MainWindow, message);

    internal static void Info(Window? owner, string message, string title = "알림") =>
        ShowThemedMessageBox(owner, message, title, MessageBoxButton.OK, IconInfo);

    internal static void Error(Window? owner, string message, string title = "오류") =>
        ShowThemedMessageBox(owner, message, title, MessageBoxButton.OK, IconError);

    internal static bool Confirm(Window? owner, string message, string title) =>
        ShowThemedMessageBox(owner, message, title, MessageBoxButton.YesNo, IconQuestion) == MessageBoxResult.Yes;

    internal static string? PromptName(string title, string defaultName)
    {
        var dialog = new Window
        {
            Title = title, Width = 350, Height = 150, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow, ResizeMode = ResizeMode.NoResize
        };
        var textBox = new TextBox { Text = defaultName, Margin = new Thickness(12, 12, 12, 0) };
        textBox.SelectAll();
        var okButton = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 8, 0, 12), IsDefault = true };
        var cancelButton = new Button { Content = "Cancel", Width = 80, Margin = new Thickness(8, 8, 12, 12), IsCancel = true };
        if (Application.Current.TryFindResource("DarkButton") is Style darkStyle)
        {
            okButton.Style = darkStyle;
            cancelButton.Style = darkStyle;
        }
        string? result = null;
        okButton.Click += (_, _) => { result = textBox.Text.Trim(); dialog.DialogResult = true; };
        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);
        var panel = new StackPanel();
        panel.Children.Add(textBox);
        panel.Children.Add(buttonPanel);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => { textBox.Focus(); };
        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(result) ? result : null;
    }

    internal static void PickCallFromList(string title, IReadOnlyList<(Guid Id, string Name)> calls,
        Action<Guid> onNavigate)
    {
        var dialog = new Window
        {
            Title = title, Width = 380, Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow, ResizeMode = ResizeMode.CanResize,
            MinWidth = 280, MinHeight = 200
        };

        var listBox = new ListBox
        {
            Margin = new Thickness(8),
            DisplayMemberPath = "Name",
            FontSize = 13
        };

        foreach (var call in calls)
            listBox.Items.Add(new { call.Id, call.Name });

        listBox.MouseDoubleClick += (_, e) =>
        {
            if (listBox.SelectedItem is not null)
            {
                var id = ((dynamic)listBox.SelectedItem).Id;
                onNavigate((Guid)id);
                dialog.Close();
            }
        };

        dialog.Content = listBox;
        dialog.ShowDialog();
    }

    internal static MessageBoxResult AskSaveChanges() =>
        ShowThemedMessageBox(
            Application.Current.MainWindow,
            "현재 프로젝트에 저장되지 않은 변경이 있습니다.\n저장하시겠습니까?",
            "저장 확인",
            MessageBoxButton.YesNoCancel,
            "?");

    /// <summary>
    /// 시뮬레이션 중 편집 차단 경고 + "시뮬레이션 종료" 옵션 다이얼로그.
    /// 버튼 순서는 [종료, 취소]. "취소" 가 default(Enter)/Esc 이며 거부(false),
    /// "시뮬레이션 종료" 는 명시적 클릭 시 true.
    /// </summary>
    /// <returns>사용자가 종료를 선택했으면 true, 그 외 false</returns>
    // TODO: 향후 동일한 커스텀 버튼 라벨 다이얼로그가 한 번 더 필요해지면
    //       ShowThemedMessageBox에 (string label, MessageBoxResult value)[] 오버로드를 추가하여 통합할 것.
    internal static bool ShowSimulationStopOptionDialog(string message)
    {
        const string stopLabel = "시뮬레이션 종료";
        var owner = Application.Current.MainWindow;
        var dialog = new Window
        {
            Title = "시뮬레이션 중 편집 차단",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };

        var darkButtonStyle = Application.Current.TryFindResource("DarkButton") as Style;

        var iconBlock = new TextBlock
        {
            Text = IconWarn,
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

        var contentPanel = new Grid { Margin = new Thickness(16, 16, 16, 12) };
        contentPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        contentPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(iconBlock, 0);
        Grid.SetColumn(messageBlock, 1);
        contentPanel.Children.Add(iconBlock);
        contentPanel.Children.Add(messageBlock);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 16)
        };

        var stopChosen = false;

        Button MakeButton(string content, bool isDefault, bool isCancel, Action onClick)
        {
            var btn = new Button
            {
                Content = content,
                MinWidth = 110,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(4, 0, 0, 0),
                IsDefault = isDefault,
                IsCancel = isCancel
            };
            if (darkButtonStyle is not null) btn.Style = darkButtonStyle;
            btn.Click += (_, _) => { onClick(); dialog.DialogResult = true; };
            return btn;
        }

        // 버튼 순서 [종료, 취소]. 의도치 않은 종료(mutation) 방지를 위해 default(Enter)/Esc 는 "취소".
        buttonPanel.Children.Add(MakeButton(stopLabel, isDefault: false, isCancel: false, () => stopChosen = true));
        buttonPanel.Children.Add(MakeButton("취소", isDefault: true, isCancel: true, () => stopChosen = false));

        var root = new Border
        {
            Background = (Brush?)Application.Current.TryFindResource("SecondaryBackgroundBrush")
                         ?? SystemColors.WindowBrush
        };
        var mainPanel = new StackPanel();
        mainPanel.Children.Add(contentPanel);
        mainPanel.Children.Add(buttonPanel);
        root.Child = mainPanel;
        dialog.Content = root;

        dialog.ShowDialog();
        return stopChosen;
    }
}
