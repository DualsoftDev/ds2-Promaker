using System;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// 붙여넣기는 가장 최근 복사를 쓴다(#249). 예전엔 붙여넣을 창에 일반 복사(Flow/Work/Call)가 남아 있으면, 그 뒤 다른 창에서
/// System 을 복사해도 일반 복사분이 붙거나 "Flow 를 선택하세요" 가 떴다. OS 클립보드·스풀 파일은 전역 상태라 판단만 확인한다.
/// </summary>
public sealed class SystemPackagePasteOrderTests
{
    private static readonly DateTime InternalCopiedAt = new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);
    private const uint InternalSequence = 10;

    [Fact]
    public void A_system_written_to_the_file_channel_after_the_internal_copy_wins()
    {
        Assert.True(MainViewModel.SystemPackageIsNewer(
            InternalCopiedAt, InternalSequence, InternalCopiedAt.AddSeconds(5), InternalSequence, clipboardHasPackage: false));
    }

    [Fact]
    public void A_system_put_on_the_clipboard_after_the_internal_copy_wins_without_the_file_channel()
    {
        Assert.True(MainViewModel.SystemPackageIsNewer(
            InternalCopiedAt, InternalSequence, null, InternalSequence + 1, clipboardHasPackage: true));
    }

    [Fact]
    public void An_older_system_copy_does_not_override_the_internal_copy()
    {
        // 스풀이 더 오래됐고 클립보드는 그대로 — 클립보드의 패키지도 일반 복사 전 것이다.
        Assert.False(MainViewModel.SystemPackageIsNewer(
            InternalCopiedAt, InternalSequence, InternalCopiedAt.AddMinutes(-5), InternalSequence, clipboardHasPackage: true));
        // 클립보드는 바뀌었지만 System 패키지가 아니다(다른 프로그램의 글자 복사).
        Assert.False(MainViewModel.SystemPackageIsNewer(
            InternalCopiedAt, InternalSequence, InternalCopiedAt.AddMinutes(-5), InternalSequence + 1, clipboardHasPackage: false));
    }
}
