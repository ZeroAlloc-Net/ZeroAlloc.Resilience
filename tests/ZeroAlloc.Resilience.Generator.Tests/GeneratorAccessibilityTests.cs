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
        methods.Should().HaveCount(2, "one overload without configure, one with configure");
        // Return the one without the configure parameter (the first overload)
        return methods.First(m => m.Parameters.Length == 1);
    }

    private static INamedTypeSymbol FindType(Compilation compilation, string name) =>
        compilation.GetSymbolsWithName(name, SymbolFilter.Type).OfType<INamedTypeSymbol>().First();

    private const string PublicApiSource = """
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

    // Coverage for #152: ZeroAllocGeneratedAccessibility=Internal forces the generated policies
    // class and DI extension methods internal, even for a public interface. Watches for the
    // accessibility conflict the issue calls out: nothing PUBLIC may be left referencing an
    // internal generated type (CS0051/CS0053), so the source must still compile with zero errors.
    [Fact]
    public void PublicInterface_GeneratedAccessibilityInternal_EmitsInternalPoliciesAndExtensions()
    {
        var (compilation, errors) = TestHelper.RunAndCompile(PublicApiSource, generatedAccessibility: "Internal");

        errors.Should().BeEmpty("nothing public may reference an internal generated type (CS0051/CS0053)");

        var extensions = FindAddMethod(compilation, "AddUserApiResilience").ContainingType;
        extensions.Name.Should().Be("InternalResilienceServiceCollectionExtensions");
        extensions.DeclaredAccessibility.Should().Be(Accessibility.Internal);

        var policies = FindType(compilation, "UserApiResiliencePolicies");
        policies.DeclaredAccessibility.Should().Be(Accessibility.Internal);

        // The interface itself is untouched: only the generated entry points move internal.
        var iface = FindType(compilation, "IUserApi");
        iface.DeclaredAccessibility.Should().Be(Accessibility.Public);
    }

    // The property is compared case-insensitively, per the shared design.
    [Theory]
    [InlineData("internal")]
    [InlineData("INTERNAL")]
    [InlineData("InTeRnAl")]
    public void GeneratedAccessibilityInternal_IsCaseInsensitive(string value)
    {
        var (compilation, errors) = TestHelper.RunAndCompile(PublicApiSource, generatedAccessibility: value);

        errors.Should().BeEmpty();
        FindAddMethod(compilation, "AddUserApiResilience").ContainingType.DeclaredAccessibility
            .Should().Be(Accessibility.Internal);
    }

    [Theory]
    [InlineData("Public")]
    [InlineData("public")]
    [InlineData("")]
    [InlineData(null)]
    public void PublicInterface_GeneratedAccessibilityPublicOrUnset_KeepsPublicExtensions(string? value)
    {
        var (compilation, errors) = TestHelper.RunAndCompile(PublicApiSource, generatedAccessibility: value);

        errors.Should().BeEmpty();
        var extensions = FindAddMethod(compilation, "AddUserApiResilience").ContainingType;
        extensions.Name.Should().Be("ResilienceServiceCollectionExtensions");
        extensions.DeclaredAccessibility.Should().Be(Accessibility.Public);

        var policies = FindType(compilation, "UserApiResiliencePolicies");
        policies.DeclaredAccessibility.Should().Be(Accessibility.Public);
    }

    // An already-internal interface (#146) is unaffected either way: nothing becomes MORE visible,
    // and it was already emitting internal entry points before #152.
    [Fact]
    public void InternalInterface_GeneratedAccessibilityInternal_StillInternal()
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

        var (compilation, errors) = TestHelper.RunAndCompile(source, generatedAccessibility: "Internal");

        errors.Should().BeEmpty();
        var extensions = FindAddMethod(compilation, "AddJevApiResilience").ContainingType;
        extensions.Name.Should().Be("InternalResilienceServiceCollectionExtensions");
        extensions.DeclaredAccessibility.Should().Be(Accessibility.Internal);
    }
}
