using System.Linq;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Resilience.Generator.Tests;

// Regression coverage for #159: an unattributed overload of a method that has a policy on one of
// its other overloads must still get a forwarding (passthrough) method on the generated proxy.
public class OverloadTests
{
    [Fact]
    public void SyncOverload_OnlyOneAttributed_BothCompile()
    {
        var source = """
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IJevApi
            {
                [Retry(MaxAttempts = 2)]
                void Get();
                void Get(int id);
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void AsyncOverload_OnlyOneAttributed_BothCompile()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IJevApi
            {
                [Timeout(Ms = 100)]
                ValueTask<string> FetchAsync(CancellationToken ct);
                ValueTask<string> FetchAsync(int id, CancellationToken ct);
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void InterfaceLevelPolicy_OneOverloadHasItsOwn_SlotsAreNamedByDeclarationOrder()
    {
        var source = """
            using ZeroAlloc.Resilience;
            namespace Repro;
            [Retry(MaxAttempts = 3)]
            public interface IJevApi
            {
                void Get();

                [Retry(MaxAttempts = 5)]
                void Get(int id);
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();

        var policiesType = compilation.GetTypeByMetadataName("Repro.JevApiResiliencePolicies");
        policiesType.Should().NotBeNull();
        var propertyNames = policiesType!.GetMembers().OfType<IPropertySymbol>().Select(p => p.Name).ToArray();

        propertyNames.Should().Contain("Retry");
        propertyNames.Should().Contain("Get2Retry");
    }
}
