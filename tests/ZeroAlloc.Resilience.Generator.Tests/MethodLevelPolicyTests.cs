using System.Linq;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Resilience.Generator.Tests;

// Regression coverage for #150: an interface whose policy attributes are only on its methods
// must get a proxy and a DI extension, like one with an interface-level attribute.
public class MethodLevelPolicyTests
{
    [Fact]
    public void MethodOnlyPolicy_GeneratesProxyAndExtension()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IOrdersApi
            {
                [Retry(MaxAttempts = 4, BackoffMs = 50)]
                ValueTask<string> GetAsync(string id, CancellationToken ct);

                ValueTask<string> PingAsync(CancellationToken ct);
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        compilation.GetTypeByMetadataName("Repro.IOrdersApiResilienceProxy").Should().NotBeNull();
        compilation.GetSymbolsWithName("AddOrdersApiResilience", SymbolFilter.Member)
            .OfType<IMethodSymbol>().Should().HaveCount(2, "one overload without configure, one with configure");
    }

    [Fact]
    public void MethodOnlyTimeout_OnInternalInterface_GeneratesInternalExtension()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            internal interface IStockApi
            {
                [Timeout(Ms = 500)]
                ValueTask<int> CountAsync(CancellationToken ct);
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        var methods = compilation.GetSymbolsWithName("AddStockApiResilience", SymbolFilter.Member)
            .OfType<IMethodSymbol>().ToArray();
        methods.Should().HaveCount(2, "one overload without configure, one with configure");
        methods[0].ContainingType.DeclaredAccessibility.Should().Be(Accessibility.Internal);
    }

    [Fact]
    public void PartialInterface_WithPoliciesOnSeveralParts_GeneratesOneProxy()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            [Timeout(Ms = 500)]
            public partial interface ISplitApi
            {
                ValueTask<string> GetAsync(string id, CancellationToken ct);
            }
            public partial interface ISplitApi
            {
                [Retry(MaxAttempts = 2)]
                ValueTask<int> CountAsync(CancellationToken ct);
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        compilation.GetTypeByMetadataName("Repro.ISplitApiResilienceProxy").Should().NotBeNull();
    }

    [Fact]
    public void InterfaceWithoutAnyPolicy_GeneratesNothing()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            namespace Repro;
            public interface IPlainApi
            {
                [System.Obsolete]
                ValueTask<string> GetAsync(string id, CancellationToken ct);
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        compilation.GetTypeByMetadataName("Repro.IPlainApiResilienceProxy").Should().BeNull();
    }
}
