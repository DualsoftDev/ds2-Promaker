using System.Collections.Generic;
using System.Linq;
using Ds2.Core;
using Ds2.Core.Store;

namespace Promaker.Services;

/// <summary>
/// 오프라인 편집기의 v10 검증 정책 — 파일 열기와 시뮬 시작이 같은 기준으로 판정한다.
/// 실 I/O 신호가 없어도 되는 환경이라 V1(Real⇒OutTag)/V2(Real⇒InTag) 는 면제한다. 면제하지 않으면
/// I/O 를 아직 안 붙인 모델이 열릴 때마다 오류 목록 모달이 떠 "로딩 실패" 처럼 보인다.
/// </summary>
internal static class V10OfflinePolicy
{
    private static readonly HashSet<string> ExemptRules = new(System.StringComparer.Ordinal) { "V1", "V2" };

    public sealed record Verdict(
        IReadOnlyList<V10Validation.ValidationIssue> Errors,
        IReadOnlyList<V10Validation.ValidationIssue> Warnings);

    public static Verdict Evaluate(DsStore store) =>
        Split(V10ValidationBatch.validateStore(store));

    /// <summary>검증 결과를 오프라인 기준으로 나눈다 — Error 중 면제 규칙은 버리고, Warning 은 그대로.</summary>
    public static Verdict Split(IEnumerable<V10Validation.ValidationIssue> issues)
    {
        var list = issues.ToList();
        return new Verdict(
            list.Where(i => i.Severity.IsError && !ExemptRules.Contains(i.Rule)).ToList(),
            list.Where(i => i.Severity.IsWarning).ToList());
    }

    public static string Format(V10Validation.ValidationIssue issue) => $"[v10 {issue.Rule}] {issue.Message}";
}
