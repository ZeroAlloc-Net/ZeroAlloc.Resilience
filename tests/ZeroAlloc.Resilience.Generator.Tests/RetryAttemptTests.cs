using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Resilience.Generator.Tests;

// #198: [RetryAttempt] on an int or int? parameter of a method under [Retry] receives the retry
// number instead of the caller's argument. ZR0011 reports it where no [Retry] applies; ZR0012
// reports it on a parameter type the proxy cannot pass the number to.
public class RetryAttemptTests
{
    private const string Usings = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using ZeroAlloc.Resilience;
        using ZeroAlloc.Results;
        namespace Repro;

        """;

    private static string SourceAt(string source, Diagnostic diagnostic) =>
        source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length);

    private static Diagnostic[] Zr(string source) =>
        TestHelper.GeneratorDiagnostics(source)
            .Where(static d => d.Id.StartsWith("ZR", System.StringComparison.Ordinal))
            .ToArray();

    private static string GeneratedSource(string source)
    {
        var (compilation, errors) = TestHelper.RunAndCompile(source);
        errors.Should().BeEmpty();
        return string.Join("\n", compilation.SyntaxTrees.Skip(1).Select(static t => t.ToString()));
    }

    // ── Emission ───────────────────────────────────────────────────────────────

    [Fact]
    public void NullableInt_PassesNullOnTheFirstAttemptThenTheRetryNumber()
    {
        var generated = GeneratedSource(Usings + """
            [Retry]
            public interface IApi
            {
                ValueTask<string> GetAsync(string id, [RetryAttempt] int? retryCount, CancellationToken ct);
            }
            """);

        generated.Should().Contain("_inner.GetAsync(id, __attempt == 0 ? default(int?) : __attempt, __ct)");
    }

    [Fact]
    public void Int_PassesTheZeroBasedAttemptNumber()
    {
        var generated = GeneratedSource(Usings + """
            public interface IApi
            {
                [Retry]
                string Get([RetryAttempt] int attempt, string id);
            }
            """);

        generated.Should().Contain("_inner.Get(__attempt, id)");
    }

    [Fact]
    public void ResultAwareRetry_PassesTheRetryNumber()
    {
        var generated = GeneratedSource(Usings + """
            public sealed class HttpError { public int Status { get; init; } }
            [Retry(RetryWhen = nameof(IsTransient))]
            public interface IApi
            {
                ValueTask<Result<string, HttpError>> GetAsync([RetryAttempt] int? retryCount, CancellationToken ct);
                static bool IsTransient(HttpError error) => true;
            }
            """);

        generated.Should().Contain("__lastResult = await _inner.GetAsync(__attempt == 0 ? default(int?) : __attempt, __ct)");
    }

    [Fact]
    public void Fallback_GetsTheFirstAttemptValue()
    {
        var generated = GeneratedSource(Usings + """
            [Retry]
            [CircuitBreaker(Fallback = nameof(FallbackAsync))]
            public interface IApi
            {
                ValueTask<string> GetAsync([RetryAttempt] int? retryCount, [RetryAttempt] int attempt, CancellationToken ct);
                ValueTask<string> FallbackAsync(int? retryCount, int attempt, CancellationToken ct);
            }
            """);

        generated.Should().Contain("return await _inner.FallbackAsync(default(int?), 0, ct)");
        generated.Should().Contain("_inner.GetAsync(__attempt == 0 ? default(int?) : __attempt, __attempt, __ct)");
    }

    [Fact]
    public void WithoutRetry_PassesTheCallersArgument()
    {
        var source = Usings + """
            [Timeout(Ms = 1000)]
            public interface IApi
            {
                ValueTask<string> GetAsync([RetryAttempt] int? retryCount, CancellationToken ct);
            }
            """;

        var generated = string.Join("\n", TestHelper.RunAndCompile(source).Compilation.SyntaxTrees.Skip(1).Select(static t => t.ToString()));

        generated.Should().Contain("_inner.GetAsync(retryCount, __ct)");
        generated.Should().NotContain("__attempt");
    }

    [Fact]
    public void InheritedDeclarationsDifferingOnlyInRetryAttempt_DoNotCollapse()
    {
        var generated = GeneratedSource(Usings + """
            public interface IA { ValueTask<string> GetAsync([RetryAttempt] int? retryCount, CancellationToken ct); }
            public interface IB { ValueTask<string> GetAsync(int? retryCount, CancellationToken ct); }
            [Retry]
            public interface IApi : IA, IB { }
            """);

        generated.Should().Contain("((global::Repro.IA)_inner).GetAsync(__attempt == 0 ? default(int?) : __attempt, __ct)");
        generated.Should().Contain("((global::Repro.IB)_inner).GetAsync(retryCount, __ct)");
    }

    [Fact]
    public void UnsupportedType_IsNotReplaced()
    {
        var source = Usings + """
            [Retry]
            public interface IApi
            {
                ValueTask<string> GetAsync([RetryAttempt] long retryCount, CancellationToken ct);
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().ContainSingle().Which.Id.Should().Be("ZR0012");
        string.Join("\n", compilation.SyntaxTrees.Skip(1).Select(static t => t.ToString()))
            .Should().Contain("_inner.GetAsync(retryCount, __ct)");
    }

    // ── ZR0011 ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("[Retry]\npublic interface IApi\n{\n    ValueTask<string> GetAsync([RetryAttempt] int? retryCount, CancellationToken ct);\n}", "interface-level")]
    [InlineData("public interface IApi\n{\n    [Retry]\n    ValueTask<string> GetAsync([RetryAttempt] int? retryCount, CancellationToken ct);\n}", "method-level")]
    public void UnderRetry_ReportsNothing(string declaration, string because)
    {
        Zr(Usings + declaration).Should().BeEmpty(because);
    }

    [Fact]
    public void MethodWithoutRetry_ReportsZR0011AtTheParameter()
    {
        var source = Usings + """
            [Timeout(Ms = 1000)]
            public interface IApi
            {
                [Retry]
                ValueTask<string> GetAsync(CancellationToken ct);
                ValueTask<string> SendAsync([RetryAttempt] int? retryCount, CancellationToken ct);
            }
            """;

        var zr0011 = Zr(source).Should().ContainSingle().Subject;
        zr0011.Id.Should().Be("ZR0011");
        zr0011.Severity.Should().Be(DiagnosticSeverity.Warning);
        zr0011.GetMessage(CultureInfo.InvariantCulture).Should().Be(
            "[RetryAttempt] on parameter 'retryCount' of 'SendAsync' has no effect, because neither 'SendAsync' nor 'IApi' has [Retry]. " +
            "Add [Retry] to the method or the interface, or remove [RetryAttempt].");
        SourceAt(source, zr0011).Should().Be("[RetryAttempt] int? retryCount");
    }

    [Fact]
    public void BaseInterfaceMethod_UnderADerivedInterfacesRetry_ReportsNothing()
    {
        var source = Usings + """
            public interface IBase<T>
            {
                ValueTask<T> GetAsync([RetryAttempt] int? retryCount, CancellationToken ct);
            }
            [Retry]
            public interface IApi : IBase<string> { }
            """;

        Zr(source).Should().BeEmpty();
    }

    // A plain base interface is not a resilience target: an interface with [Retry] that inherits
    // it, possibly in another project, retries the method, so ZR0011 would be a false positive.
    [Fact]
    public void BaseInterfaceWithoutAttributes_ReportsNothing()
    {
        var source = Usings + """
            public interface IBase
            {
                ValueTask<string> GetAsync([RetryAttempt] int? retryCount, CancellationToken ct);
            }
            """;

        Zr(source).Should().BeEmpty();
    }

    [Fact]
    public void BaseInterfaceWithoutAttributes_UnderADerivedInterfaceWithoutRetry_ReportsNothing()
    {
        var source = Usings + """
            public interface IBase
            {
                ValueTask<string> GetAsync([RetryAttempt] int? retryCount, CancellationToken ct);
            }
            [Timeout(Ms = 1000)]
            public interface IApi : IBase { }
            """;

        Zr(source).Should().BeEmpty();
    }

    [Theory]
    [InlineData("[Timeout(Ms = 1000)]", "", "", "interface-level timeout")]
    [InlineData("[CircuitBreaker]", "", "", "interface-level circuit breaker")]
    [InlineData("", "[RateLimit(MaxPerSecond = 10)]", "", "method-level policy")]
    [InlineData("", "", "[Retry] ValueTask<string> OtherAsync(CancellationToken ct);", "another method has [Retry]")]
    public void ResilienceInterfaceMissingRetry_ReportsZR0011(string interfaceAttribute, string methodAttribute, string otherMember, string because)
    {
        var source = Usings + $$"""
            {{interfaceAttribute}}
            public interface IApi
            {
                {{otherMember}}
                {{methodAttribute}}
                ValueTask<string> GetAsync([RetryAttempt] int? retryCount, CancellationToken ct);
            }
            """;

        Zr(source).Should().ContainSingle(because).Which.Id.Should().Be("ZR0011");
    }

    [Fact]
    public void ClassMethod_ReportsZR0011()
    {
        var source = Usings + """
            public class Api
            {
                [Retry]
                public string Get([RetryAttempt] int? retryCount) => "";
            }
            """;

        var zr0011 = Zr(source).Should().ContainSingle().Subject;
        zr0011.Id.Should().Be("ZR0011");
        zr0011.GetMessage(CultureInfo.InvariantCulture).Should().Be(
            "[RetryAttempt] on parameter 'retryCount' of 'Get' has no effect, because 'Get' is not a method of an interface, " +
            "and only the resilience proxy of an interface passes the retry attempt. Put [RetryAttempt] on the interface method, or remove it.");
    }

    // #pragma suppression is proven by a real compiler run, not here: Roslyn applies #pragma to
    // generator diagnostics only inside the compiler, and no public API exposes that filtering.
    // IRetryAttemptWithoutRetryService in ZeroAlloc.Resilience.Tests wraps a ZR0011 in #pragma,
    // and that project builds with TreatWarningsAsErrors, so it would not compile otherwise.

    // ── ZR0012 ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("long retryCount")]
    [InlineData("string retryCount")]
    [InlineData("short retryCount")]
    [InlineData("long? retryCount")]
    [InlineData("ref int retryCount")]
    [InlineData("in int retryCount")]
    [InlineData("out int retryCount")]
    public void UnsupportedParameter_ReportsZR0012AtTheParameter(string parameter)
    {
        var source = Usings + $$"""
            [Retry]
            public interface IApi
            {
                string Get([RetryAttempt] {{parameter}});
            }
            """;

        var zr0012 = Zr(source).Should().ContainSingle(parameter).Subject;
        zr0012.Id.Should().Be("ZR0012");
        zr0012.Severity.Should().Be(DiagnosticSeverity.Error);
        zr0012.GetMessage(CultureInfo.InvariantCulture).Should().Be(
            $"[RetryAttempt] on parameter 'retryCount' of 'Get' requires an 'int' or 'int?' parameter passed by value, but it is '{parameter.Replace(" retryCount", "", System.StringComparison.Ordinal)}'");
        SourceAt(source, zr0012).Should().Be($"[RetryAttempt] {parameter}");
    }

    [Fact]
    public void UnsupportedParameterWithoutRetry_ReportsBoth()
    {
        var source = Usings + """
            [Timeout(Ms = 1000)]
            public interface IApi
            {
                string Get([RetryAttempt] string retryCount, CancellationToken ct);
            }
            """;

        Zr(source).Select(static d => d.Id).Should().BeEquivalentTo("ZR0011", "ZR0012");
    }
}
