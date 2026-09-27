using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Resilience.Generator.Tests;

// #195: by default an exception RetryOnException declines leaves the loop for exhaustion, as
// documented. With RethrowDeclined, a method that throws rethrows it from the catch with `throw;`,
// keeping the original exception and its stack. A method that returns a ResilienceError-capable
// Result turns it into a failure either way, because such a method never throws.
public class DeclinedExceptionTests
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

    private const string Rethrow = "if (!global::Repro.IApi.IsRetryable(__ex)) throw;";
    private const string Exhaust = "if (!global::Repro.IApi.IsRetryable(__ex)) break;";

    private static string GeneratedSource(Compilation compilation) =>
        string.Join("\n", compilation.SyntaxTrees
            .Where(static t => t.FilePath.EndsWith(".g.cs", StringComparison.Ordinal))
            .Select(static t => t.ToString()))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Generate(string method, string retryArguments, string policies = "")
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + $$"""
            [Retry(MaxAttempts = 3, BackoffMs = 10, RetryOnException = nameof(IsRetryable){{retryArguments}})]
            {{policies}}
            public interface IApi
            {
                {{method}}
                static bool IsRetryable(Exception exception) => true;
                static bool IsTransient(HttpError error) => true;
                static bool IsTransient(string error) => true;
                static bool IsTransient(ResilienceError error) => true;
            }
            """);

        errors.Should().BeEmpty();
        return GeneratedSource(compilation);
    }

    public static TheoryData<string, string, string> ThrowingMethods => new()
    {
        { "ValueTask<string> GetAsync(CancellationToken ct);", "", "" },
        { "Task GetAsync(CancellationToken ct);", "", "" },
        { "string Get(CancellationToken ct);", "", "" },
        { "void Run(CancellationToken ct);", "", "" },
        { "ValueTask<string> GetAsync(CancellationToken ct);", "", "[Timeout(Ms = 1000)] [CircuitBreaker(MaxFailures = 3)]" },
        { "string Get(CancellationToken ct);", "", "[Timeout(Ms = 1000)] [CircuitBreaker(MaxFailures = 3)]" },
        // A Result with a foreign error type cannot hold the exception, so it throws too, from
        // the exception-only loop and from the Result-aware loop alike.
        { "ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);", "", "" },
        { "Result<string, HttpError> Get(CancellationToken ct);", "", "" },
        { "ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);", ", RetryWhen = nameof(IsTransient)", "" },
        { "Result<string, HttpError> Get(CancellationToken ct);", ", RetryWhen = nameof(IsTransient)", "" },
        { "ValueTask<UnitResult<HttpError>> GetAsync(CancellationToken ct);", ", RetryWhen = nameof(IsTransient)", "" },
    };

    public static TheoryData<string, string> FailureResultMethods => new()
    {
        { "ValueTask<Result<string, ResilienceError>> GetAsync(CancellationToken ct);", "" },
        { "ValueTask<UnitResult<ResilienceError>> GetAsync(CancellationToken ct);", "" },
        { "ValueTask<Result<string>> GetAsync(CancellationToken ct);", "" },
        { "ValueTask<Result<string, ResilienceError>> GetAsync(CancellationToken ct);", ", RetryWhen = nameof(IsTransient)" },
        { "ValueTask<Result<string>> GetAsync(CancellationToken ct);", ", RetryWhen = nameof(IsTransient)" },
    };

    [Theory]
    [MemberData(nameof(ThrowingMethods))]
    public void ThrowingMethod_ByDefault_DeclinedExceptionGoesThroughExhaustion(string method, string retryWhen, string policies)
    {
        var generated = Generate(method, retryWhen, policies);

        generated.Should().Contain(Exhaust);
        generated.Should().NotContain(Rethrow);
        generated.Should().Contain("\"All retry attempts failed.\"");
    }

    [Theory]
    [MemberData(nameof(ThrowingMethods))]
    public void ThrowingMethod_WithRethrowDeclined_RethrowsTheDeclinedException(string method, string retryWhen, string policies)
    {
        var generated = Generate(method, retryWhen + ", RethrowDeclined = true", policies);

        generated.Should().Contain(Rethrow);
        generated.Should().NotContain(Exhaust);
    }

    [Theory]
    [MemberData(nameof(ThrowingMethods))]
    public void ThrowingMethod_WithRethrowDeclinedFalse_DeclinedExceptionGoesThroughExhaustion(string method, string retryWhen, string policies)
    {
        var generated = Generate(method, retryWhen + ", RethrowDeclined = false", policies);

        generated.Should().Contain(Exhaust);
        generated.Should().NotContain(Rethrow);
    }

    [Theory]
    [MemberData(nameof(FailureResultMethods))]
    public void FailureResultMethod_ByDefault_ReturnsAFailure(string method, string retryWhen)
    {
        var generated = Generate(method, retryWhen);

        generated.Should().Contain(Exhaust);
        generated.Should().NotContain(Rethrow);
        generated.Should().Contain(".Failure(");
    }

    [Theory]
    [MemberData(nameof(FailureResultMethods))]
    public void FailureResultMethod_WithRethrowDeclined_StillReturnsAFailure(string method, string retryWhen)
    {
        var generated = Generate(method, retryWhen + ", RethrowDeclined = true");

        generated.Should().Contain(Exhaust);
        generated.Should().NotContain(Rethrow);
        generated.Should().Contain(".Failure(");
    }

    // A method-level [Retry] shadows the interface-level one entirely, RethrowDeclined included.
    [Fact]
    public void MethodLevelRetry_DecidesRethrowDeclined_ForItsOwnMethod()
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + """
            [Retry(RetryOnException = nameof(IsRetryable), RethrowDeclined = true)]
            public interface IApi
            {
                ValueTask<string> RethrowsAsync(CancellationToken ct);
                [Retry(RetryOnException = nameof(IsRetryable))]
                ValueTask<string> ExhaustsAsync(CancellationToken ct);
                static bool IsRetryable(Exception exception) => true;
            }
            """);

        errors.Should().BeEmpty();
        var generated = GeneratedSource(compilation);
        generated.Should().Contain(Rethrow);
        generated.Should().Contain(Exhaust);
    }
}
