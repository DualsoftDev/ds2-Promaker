using System.Windows;
using System.Windows.Controls;

namespace Promaker.Controls;

/// <summary>System 속성 패널의 "사용자 태그" 섹션. 목록·편집 명령은 PropertyPanelState(UserTagPanel) 에 있다.</summary>
public partial class UserTagsSectionControl : UserControl
{
    public UserTagsSectionControl()
    {
        InitializeComponent();
    }

    /// <summary>UserTags CSV 메뉴 버튼 — 좌클릭 시 ContextMenu 를 펼친다.</summary>
    private void UserTagsCsvButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.ContextMenu is null) return;
        btn.ContextMenu.PlacementTarget = btn;
        btn.ContextMenu.IsOpen = true;
    }
}
