using System;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Resilience.Generator.Tests;

// #195: ZR0013 warns when [Retry(RethrowDeclined = true)] can have no effect: the [Retry] has no
// RetryOnException, so nothing is ever declined, or every method it applies to returns a Result
// whose failure the proxy builds, so nothing is ever thrown.
public class RethrowDeclinedDiagnosticTests
{
    private const string Usings = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using ZeroAlloc.Resilience;
        using ZeroAlloc.Results;
        namespace Repro;
        public sealed class HttpError { public int Status { get; init; } }

        """;

    private static string SourceAt(string source, Diagnostic diagnostic) =>
        source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length);

    private static Diagnostic[] Zr0013(string source) =>
        TestHelper.GeneratorDiagnostics(source)
            .Where(static d => string.Equals(d.Id, "ZR0013", StringComparison.Ordinal))
            .ToArray();

    private static void ShouldCompileWithoutErrors(string source)
    {
        var (_, errors) = TestHelper.RunAndCompile(source);
        errors.Should().BeEmpty();
    }

    // ── Positive: no RetryOnException ──────────────────────────────────────────

    [Fact]
    public void InterfaceLevel_WithoutRetryOnException_ReportsZR0013AtTheAttribute()
    {
        var source = Usings + """
            [Retry(RethrowDeclined = true)]
            public interface IApi
            {
                ValueTask<string> GetAsync(CancellationToken ct);
            }
            """;

        var diagnostic = Zr0013(source).Should().ContainSingle().Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.GetMessage(CultureInfo.InvariantCulture).Should()
            .Be("[Retry] RethrowDeclined = true has no effect on 'IApi', because the [Retry] has no RetryOnException, so no exception is ever declined. Set RetryOnException, or remove RethrowDeclined.");
        SourceAt(source, diagnostic).Should().StartWith("Retry(");
        ShouldCompileWithoutErrors(source);
    }

    [Fact]
    public void MethodLevel_WithoutRetryOnException_ReportsZR0013AtTheAttribute()
    {
        var source = Usings + """
            public interface IApi
            {
                [Retry(RethrowDeclined = true)]
                ValueTask<string> GetAsync(CancellationToken ct);
            }
            """;

        var diagnostic = Zr0013(source).Should().ContainSingle().Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.GetMessage(CultureInfo.InvariantCulture).Should()
            .Contain("has no effect on 'GetAsync', because the [Retry] has no RetryOnException");
        SourceAt(source, diagnostic).Should().StartWith("Retry(");
        ShouldCompileWithoutErrors(source);
    }

    // ── Positive: only methods that never throw ────────────────────────────────

    [Theory]
    [InlineData("ValueTask<Result<string, ResilienceError>> GetAsync(CancellationToken ct);")]
    [InlineData("ValueTask<UnitResult<ResilienceError>> GetAsync(CancellationToken ct);")]
    [InlineData("ValueTask<Result<string>> GetAsync(CancellationToken ct);")]
    [InlineData("Result GetResult(CancellationToken ct);")]
    public void MethodLevel_OnAMethodThatReturnsFailures_ReportsZR0013(string method)
    {
        var source = Usings + $$"""
            public interface IApi
            {
                [Retry(RetryOnException = nameof(IsRetryable), RethrowDeclined = true)]
                {{method}}
                static bool IsRetryable(Exception exception) => true;
            }
            """;

        var diagnostic = Zr0013(source).Should().ContainSingle().Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.GetMessage(CultureInfo.InvariantCulture).Should()
            .Contain("because it returns a failure instead of throwing, for a declined exception too")
            .And.EndWith("Remove RethrowDeclined.");
        SourceAt(source, diagnostic).Should().StartWith("Retry(");
        ShouldCompileWithoutErrors(source);
    }

    [Fact]
    public void InterfaceLevel_WhenEveryMethodReturnsFailures_ReportsZR0013Once()
    {
        var source = Usings + """
            [Retry(RetryOnException = nameof(IsRetryable), RethrowDeclined = true)]
            public interface IApi
            {
                ValueTask<Result<string, ResilienceError>> GetAsync(CancellationToken ct);
                ValueTask<Result<string>> FindAsync(CancellationToken ct);
                static bool IsRetryable(Exception exception) => true;
            }
            """;

        var diagnostic = Zr0013(source).Should().ContainSingle().Subject;
        diagnostic.GetMessage(CultureInfo.InvariantCulture).Should()
            .Contain("has no effect on 'IApi', because every method it applies to returns a failure instead of throwing");
        SourceAt(source, diagnostic).Should().StartWith("Retry(");
        ShouldCompileWithoutErrors(source);
    }

    // A method-level [Retry] shadows the interface's, so the interface's applies to no throwing
    // method here.
    [Fact]
    public void InterfaceLevel_WhenTheOnlyThrowingMethodHasItsOwnRetry_ReportsZR0013()
    {
        var source = Usings + """
            [Retry(RetryOnException = nameof(IsRetryable), RethrowDeclined = true)]
            public interface IApi
            {
                ValueTask<Result<string, ResilienceError>> GetAsync(CancellationToken ct);
                [Retry(RetryOnException = nameof(IsRetryable))]
                ValueTask<string> FetchAsync(CancellationToken ct);
                static bool IsRetryable(Exception exception) => true;
            }
            """;

        Zr0013(source).Should().ContainSingle()
            .Which.GetMessage(CultureInfo.InvariantCulture).Should().Contain("'IApi'");
    }

    // ── Negative ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ValueTask<string> GetAsync(CancellationToken ct);")]
    [InlineData("string Get(CancellationToken ct);")]
    [InlineData("Task SendAsync(CancellationToken ct);")]
    // A foreign error type cannot hold the exception, so the method throws it.
    [InlineData("ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);")]
    public void WithRetryOnException_OnAMethodThatThrows_ReportsNothing(string method)
    {
        var interfaceLevel = Usings + $$"""
            [Retry(RetryOnException = nameof(IsRetryable), RethrowDeclined = true)]
            public interface IApi
            {
                {{method}}
                static bool IsRetryable(Exception exception) => true;
            }
            """;
        var methodLevel = Usings + $$"""
            public interface IApi
            {
                [Retry(RetryOnException = nameof(IsRetryable), RethrowDeclined = true)]
                {{method}}
                static bool IsRetryable(Exception exception) => true;
            }
            """;

        Zr0013(interfaceLevel).Should().BeEmpty();
        Zr0013(methodLevel).Should().BeEmpty();
    }

    // An interface-level [Retry] over a mix of methods has an effect on the throwing ones. Its
    // Result methods are not reported: they need no change, and the warning could only be
    // silenced by moving the [Retry] onto every throwing method.
    [Fact]
    public void InterfaceLevel_OverAMixOfMethods_ReportsNothing()
    {
        var source = Usings + """
            [Retry(RetryOnException = nameof(IsRetryable), RethrowDeclined = true)]
            public interface IApi
            {
                ValueTask<string> GetAsync(CancellationToken ct);
                ValueTask<Result<string, ResilienceError>> TryGetAsync(CancellationToken ct);
                static bool IsRetryable(Exception exception) => true;
            }
            """;

        Zr0013(source).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(", RethrowDeclined = false")]
    public void WithoutRethrowDeclined_ReportsNothing(string rethrow)
    {
        var source = Usings + $$"""
            [Retry(MaxAttempts = 2{{rethrow}})]
            public interface IApi
            {
                ValueTask<string> GetAsync(CancellationToken ct);
                ValueTask<Result<string, ResilienceError>> TryGetAsync(CancellationToken ct);
                [Retry(MaxAttempts = 2{{rethrow}})]
                ValueTask<Result<string>> FindAsync(CancellationToken ct);
            }
            """;

        Zr0013(source).Should().BeEmpty();
    }

    // An interface whose methods all have their own [Retry] applies its own to none of them;
    // that is not what ZR0013 is about.
    [Fact]
    public void InterfaceLevel_ShadowedOnEveryMethod_ReportsNothing()
    {
        var source = Usings + """
            [Retry(RetryOnException = nameof(IsRetryable), RethrowDeclined = true)]
            public interface IApi
            {
                [Retry(RetryOnException = nameof(IsRetryable), RethrowDeclined = true)]
                ValueTask<string> GetAsync(CancellationToken ct);
                static bool IsRetryable(Exception exception) => true;
            }
            """;

        Zr0013(source).Should().BeEmpty();
    }
}
