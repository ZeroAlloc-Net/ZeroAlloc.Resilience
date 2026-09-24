using System.Linq;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Resilience.Generator.Tests;

// Regression coverage for #145: the generated DI extension must take the interface's
// accessibility instead of always being public.
public class GeneratorAccessibilityTests
{
    [Fact]
    public void InternalInterface_Compiles_WithInternalExtensions()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            [Retry(MaxAttempts = 3, BackoffMs = 100)]
            internal interface IJevApi
            {
                ValueTask<string> GetAsync(string id, CancellationToken ct);
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        var extensions = FindAddMethod(compilation, "AddJevApiResilience").ContainingType;
        extensions.Name.Should().Be("InternalResilienceServiceCollectionExtensions");
        extensions.DeclaredAccessibility.Should().Be(Accessibility.Internal);
    }

    [Fact]
    public void PublicInterface_KeepsPublicExtensions()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            [Retry(MaxAttempts = 3, BackoffMs = 100)]
            public interface IUserApi
            {
                ValueTask<string> GetAsync(string id, CancellationToken ct);
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        var extensions = FindAddMethod(compilation, "AddUserApiResilience").ContainingType;
        extensions.Name.Should().Be("ResilienceServiceCollectionExtensions");
        extensions.DeclaredAccessibility.Should().Be(Accessibility.Public);
    }

    [Fact]
    public void MixedPublicAndInternalInterfaces_InOneNamespace_Compile()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            [Retry(MaxAttempts = 3, BackoffMs = 100)]
            public interface IPublicApi
            {
                ValueTask<string> GetAsync(string id, CancellationToken ct);
            }
            [Timeout(Ms = 500)]
            internal interface ISecretApi
            {
                ValueTask<string> GetAsync(string id, CancellationToken ct);
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        FindAddMethod(compilation, "AddPublicApiResilience").ContainingType.DeclaredAccessibility
            .Should().Be(Accessibility.Public);
        FindAddMethod(compilation, "AddSecretApiResilience").ContainingType.DeclaredAccessibility
            .Should().Be(Accessibility.Internal);
    }

    private static IMethodSymbol FindAddMethod(Compilation compilation, string name)
    {
        var methods = compilation.GetSymbolsWithName(name, SymbolFilter.Member).OfType<IMethodSymbol>().ToArray();
        methods.Should().ContainSingle();
        return methods[0];
    }
}
