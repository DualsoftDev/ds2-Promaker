using System.Linq;
using Ds2.Core;
using Promaker.Services;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// 파일 열기와 시뮬 시작은 같은 오프라인 정책으로 v10 검증 결과를 가른다. 종전엔 열기 쪽이 전체 위반을 모달로
/// 띄워 I/O 를 아직 안 붙인 모델(V1/V2 위반뿐)이 열릴 때마다 오류 목록이 떴고, 시뮬은 같은 모델을 그냥 돌렸다.
/// </summary>
public sealed class V10OfflinePolicyTests
{
    private static V10Validation.ValidationIssue Issue(string rule, V10Validation.Severity severity) =>
        new(rule, severity, $"{rule} message");

    [Fact]
    public void Split_exempts_only_V1_and_V2_errors_and_keeps_every_warning()
    {
        var issues = new[]
        {
            Issue("V1", V10Validation.Severity.Error),
            Issue("V2", V10Validation.Severity.Error),
            Issue("V3", V10Validation.Severity.Error),
            Issue("V5", V10Validation.Severity.Warning),
            Issue("V1", V10Validation.Severity.Warning),
        };

        var verdict = V10OfflinePolicy.Split(issues);

        Assert.Equal(new[] { "V3" }, verdict.Errors.Select(i => i.Rule));
        Assert.Equal(new[] { "V5", "V1" }, verdict.Warnings.Select(i => i.Rule));
    }

    [Fact]
    public void A_model_that_only_lacks_real_io_tags_is_not_an_error_offline()
    {
        var verdict = V10OfflinePolicy.Split(new[]
        {
            Issue("V1", V10Validation.Severity.Error),
            Issue("V2", V10Validation.Severity.Error),
        });

        Assert.Empty(verdict.Errors);
        Assert.Empty(verdict.Warnings);
    }
}
