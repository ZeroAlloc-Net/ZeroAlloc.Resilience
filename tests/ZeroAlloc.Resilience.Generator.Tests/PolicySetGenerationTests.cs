using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Resilience.Generator.Tests;

// 2.0: every interface gets a {Name}ResiliencePolicies class with one slot per interface-level
// policy and one per method-level override.
public class PolicySetGenerationTests
{
    private const string Usings = """
        using System.Threading;
        using System.Threading.Tasks;
        using ZeroAlloc.Resilience;
        namespace Repro;
        """;

    [Fact]
    public void InterfaceLevelPolicies_GetKindNamedSlots()
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + """
            [Retry(MaxAttempts = 4, BackoffMs = 500)]
            [Timeout(Ms = 30000)]
            [RateLimit(MaxPerSecond = 5, BurstSize = 2)]
            [CircuitBreaker(MaxFailures = 5, ResetMs = 1000)]
            public interface IJevApi
            {
                ValueTask<string> ModelsAsync(CancellationToken ct);
            }
            """);

        errors.Should().BeEmpty();
        Slots(compilation, "Repro.JevApiResiliencePolicies").Should().Equal(
            "CircuitBreaker: ZeroAlloc.Resilience.CircuitBreakerPolicy",
            "RateLimiter: ZeroAlloc.Resilience.RateLimiter",
            "Retry: ZeroAlloc.Resilience.RetryPolicy",
            "Timeout: ZeroAlloc.Resilience.TimeoutPolicy");
    }

    [Fact]
    public void MethodOverrides_GetMethodNamedSlots_NextToInterfaceSlots()
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + """
            [Retry(MaxAttempts = 4)]
            public interface IJevApi
            {
                ValueTask<string> ListAsync(CancellationToken ct);

                [Retry(MaxAttempts = 1)]
                ValueTask<string> ModelsAsync(CancellationToken ct);

                [CircuitBreaker(MaxFailures = 2)]
                ValueTask<string> PostAsync(CancellationToken ct);
            }
            """);

        errors.Should().BeEmpty();
        Slots(compilation, "Repro.JevApiResiliencePolicies").Should().Equal(
            "ModelsAsyncRetry: ZeroAlloc.Resilience.RetryPolicy",
            "PostAsyncCircuitBreaker: ZeroAlloc.Resilience.CircuitBreakerPolicy",
            "Retry: ZeroAlloc.Resilience.RetryPolicy");
    }

    [Fact]
    public void MethodOnlyInterface_HasOnlyMethodSlots()
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + """
            public interface IJevApi
            {
                [Timeout(Ms = 100)]
                ValueTask<string> ModelsAsync(CancellationToken ct);
            }
            """);

        errors.Should().BeEmpty();
        Slots(compilation, "Repro.JevApiResiliencePolicies").Should().Equal(
            "ModelsAsyncTimeout: ZeroAlloc.Resilience.TimeoutPolicy");
    }

    [Fact]
    public void Overloads_AndTakenNames_GetNumberedSlots()
    {
        // The second overload of Get takes Get2Retry, so the method actually named Get2, whose
        // own first-declaration name would also be Get2Retry, moves on to the next number: Get22Retry.
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + """
            public interface IJevApi
            {
                [Retry(MaxAttempts = 2)]
                void Get();

                [Retry(MaxAttempts = 3)]
                void Get(int id);

                [Retry(MaxAttempts = 4)]
                void Get2();
            }
            """);

        errors.Should().BeEmpty();
        Slots(compilation, "Repro.JevApiResiliencePolicies").Should().Equal(
            "Get22Retry: ZeroAlloc.Resilience.RetryPolicy",
            "Get2Retry: ZeroAlloc.Resilience.RetryPolicy",
            "GetRetry: ZeroAlloc.Resilience.RetryPolicy");
    }

    [Theory]
    [InlineData("public", Accessibility.Public)]
    [InlineData("internal", Accessibility.Internal)]
    public void PoliciesClass_FollowsInterfaceAccessibility(string modifier, Accessibility expected)
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + $$"""
            [Retry(MaxAttempts = 2)]
            {{modifier}} interface IJevApi
            {
                ValueTask<string> ModelsAsync(CancellationToken ct);
            }
            """);

        errors.Should().BeEmpty();
        compilation.GetTypeByMetadataName("Repro.JevApiResiliencePolicies")!
            .DeclaredAccessibility.Should().Be(expected);
    }

    [Fact]
    public void Slots_DefaultToAttributeValues()
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + """
            [Retry(MaxAttempts = 4, BackoffMs = 500, Jitter = true, PerAttemptTimeoutMs = 250)]
            [Timeout(Ms = 30000)]
            [RateLimit(MaxPerSecond = 5, BurstSize = 2, Scope = RateLimitScope.Instance)]
            [CircuitBreaker(MaxFailures = 5, ResetMs = 1000, HalfOpenProbes = 2)]
            public interface IJevApi
            {
                ValueTask<string> ModelsAsync(CancellationToken ct);
            }
            """);

        errors.Should().BeEmpty();
        var source = string.Join("\n", compilation.SyntaxTrees.Select(static t => t.ToString()));
        source.Should()
            .Contain("public global::ZeroAlloc.Resilience.RetryPolicy Retry { get; set; } = new global::ZeroAlloc.Resilience.RetryPolicy(4, 500, true, 250);")
            .And.Contain("public global::ZeroAlloc.Resilience.TimeoutPolicy Timeout { get; set; } = new global::ZeroAlloc.Resilience.TimeoutPolicy(30000);")
            .And.Contain("public global::ZeroAlloc.Resilience.RateLimiter RateLimiter { get; set; } = new global::ZeroAlloc.Resilience.RateLimiter(5, 2, global::ZeroAlloc.Resilience.RateLimitScope.Instance);")
            .And.Contain("public global::ZeroAlloc.Resilience.CircuitBreakerPolicy CircuitBreaker { get; set; } = new global::ZeroAlloc.Resilience.CircuitBreakerPolicy(5, 1000, 2);");
    }

    [Fact]
    public void InterfaceName_StripsOneLeadingI_BeforeUppercase()
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + """
            [Retry(MaxAttempts = 2)]
            public interface IInvoiceApi
            {
                ValueTask<string> GetAsync(CancellationToken ct);
            }
            """);

        errors.Should().BeEmpty();
        compilation.GetTypeByMetadataName("Repro.InvoiceApiResiliencePolicies").Should().NotBeNull();
        compilation.GetSymbolsWithName("AddInvoiceApiResilience", SymbolFilter.Member).Should().NotBeEmpty();
    }

    [Fact]
    public void InterfaceName_WithoutLeadingI_KeepsFullName()
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + """
            [Retry(MaxAttempts = 2)]
            public interface Item
            {
                ValueTask<string> GetAsync(CancellationToken ct);
            }
            """);

        errors.Should().BeEmpty();
        compilation.GetTypeByMetadataName("Repro.ItemResiliencePolicies").Should().NotBeNull();
        compilation.GetSymbolsWithName("AddItemResilience", SymbolFilter.Member).Should().NotBeEmpty();
    }

    [Fact]
    public void MaxDelayMs_WhenSet_EmitsTheFiveArgumentConstructor()
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + """
            [Retry(MaxAttempts = 4, BackoffMs = 500, MaxDelayMs = 2000)]
            public interface IJevApi
            {
                ValueTask<string> ModelsAsync(CancellationToken ct);
            }
            """);

        errors.Should().BeEmpty();
        string.Join("\n", compilation.SyntaxTrees.Select(static t => t.ToString())).Should()
            .Contain("Retry { get; set; } = new global::ZeroAlloc.Resilience.RetryPolicy(4, 500, false, 0, 2000);");
    }

    [Fact]
    public void MaxDelayMs_WhenUnset_KeepsTheFourArgumentConstructor()
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + """
            [Retry(MaxAttempts = 4, BackoffMs = 500)]
            public interface IJevApi
            {
                ValueTask<string> ModelsAsync(CancellationToken ct);
            }
            """);

        errors.Should().BeEmpty();
        string.Join("\n", compilation.SyntaxTrees.Select(static t => t.ToString())).Should()
            .Contain("Retry { get; set; } = new global::ZeroAlloc.Resilience.RetryPolicy(4, 500, false, 0);");
    }

    // "Name: Type" for every property, ordered by name.
    private static string[] Slots(Compilation compilation, string metadataName) =>
        compilation.GetTypeByMetadataName(metadataName)!
            .GetMembers().OfType<IPropertySymbol>()
            .Select(static p => $"{p.Name}: {p.Type.ToDisplayString()}")
            .OrderBy(static s => s, StringComparer.Ordinal)
            .ToArray();
}
