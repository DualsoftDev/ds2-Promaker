using System.Windows;
using System.Windows.Controls;

namespace Promaker.Dialogs;

/// <summary>IO·태그 확인 다이얼로그의 진단 패널. 버튼 클릭은 원래 sender 그대로 다이얼로그 핸들러에 넘긴다.</summary>
public partial class TagInspectorDiagnosticsPanel : UserControl
{
    public event RoutedEventHandler? CloseClick;
    public event RoutedEventHandler? OpenFBTagMapClick;
    public event RoutedEventHandler? GoToRowClick;

    public TagInspectorDiagnosticsPanel()
    {
        InitializeComponent();
    }

    private void DiagnosticsClose_Click(object sender, RoutedEventArgs e) => CloseClick?.Invoke(sender, e);

    private void DiagnosticOpenFBTagMap_Click(object sender, RoutedEventArgs e) => OpenFBTagMapClick?.Invoke(sender, e);

    private void DiagnosticGoToRow_Click(object sender, RoutedEventArgs e) => GoToRowClick?.Invoke(sender, e);
}
