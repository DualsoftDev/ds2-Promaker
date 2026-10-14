using System;
using System.Linq;
using System.Reflection;
using Ds2.Core.Store;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// ExternalDlls 의 미리 빌드된 어셈블리(AAStoPLC)는 특정 AssemblyVersion 의 Ds2.* 를 참조한다. 로드된 Ds2.* 가
/// 그보다 낮으면 .NET 이 FileNotFoundException 을 던지고 Promaker 는 시작 즉시 죽는다 — 0.1.32 배포본 사고
/// (ds2 의 AssemblyVersion 을 BuildVersion 0.1.x 로 내림). 참조를 실제로 바인딩해 본다.
/// </summary>
public sealed class ExternalDllBindingTests
{
    [Fact]
    public void AAStoPLC_references_to_Ds2_assemblies_bind_against_the_shipped_versions()
    {
        var external = Assembly.Load("AAStoPLC");
        var ds2References = external.GetReferencedAssemblies()
            .Where(name => name.Name!.StartsWith("Ds2.", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(ds2References);

        foreach (var reference in ds2References)
        {
            var loaded = Assembly.Load(reference);
            Assert.True(loaded.GetName().Version >= reference.Version,
                $"{reference.Name} {loaded.GetName().Version} < AAStoPLC 가 참조한 {reference.Version}");
        }

        Assert.Equal(new Version(2, 0, 0, 0), typeof(DsStore).Assembly.GetName().Version);
    }
}
