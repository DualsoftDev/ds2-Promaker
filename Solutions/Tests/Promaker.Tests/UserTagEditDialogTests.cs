using Ds2.Editor;
using Promaker.Dialogs;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// UserTag 편집 창의 확인 결과(#230). 아무 칸도 안 바꾸고 확인하면 기존 값을 그대로 돌려줘야 한다 — 창이 저장 정책대로
/// 로그 레벨을 Error 로 올리고 Edge 조건의 기준값을 비우는 바람에, 변경이 없어도 History 에 "사용자 태그 편집" 이 쌓였다.
/// </summary>
public sealed class UserTagEditDialogTests
{
    private static readonly UserTagPanelItem Legacy =
        new(0, "Alarm", "Info", "%MX0", "Bit", "RisingEdge", "1");

    [Fact]
    public void Confirming_without_changes_returns_the_existing_values()
    {
        var result = UserTagEditDialog.ResolveResult(Legacy, "Alarm", "%MX0", "Bit", "RisingEdge", "", needsValue: false);

        Assert.Equal(("Alarm", "Info", "%MX0", "Bit", "RisingEdge", "1"), result);
    }

    [Fact]
    public void Changing_a_visible_field_applies_the_save_policy()
    {
        var result = UserTagEditDialog.ResolveResult(Legacy, "Alarm", "%MX1", "Bit", "RisingEdge", "", needsValue: false);

        Assert.Equal(("Alarm", "Error", "%MX1", "Bit", "RisingEdge", ""), result);
    }
}
