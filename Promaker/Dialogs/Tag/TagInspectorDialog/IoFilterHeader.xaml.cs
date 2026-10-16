using System.Windows.Controls;

namespace Promaker.Dialogs;

/// <summary>IO 신호 그리드의 컬럼 필터 헤더. 필터 입력은 원래 sender(Tag 로 컬럼을 가리키는 TextBox) 그대로 다이얼로그에 넘긴다.</summary>
public partial class TagInspectorIoFilterHeader : UserControl
{
    public event TextChangedEventHandler? FilterTextChanged;

    public TagInspectorIoFilterHeader()
    {
        InitializeComponent();
    }

    private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e) => FilterTextChanged?.Invoke(sender, e);
}
