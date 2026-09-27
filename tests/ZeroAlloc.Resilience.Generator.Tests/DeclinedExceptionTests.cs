using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Resilience.Generator.Tests;

// #195: an exception RetryOnException declines is rethrown from the catch with `throw;`, keeping
// the original exception and its stack, instead of leaving the loop for the ResilienceException
// that stands for exhausted retries. A method that returns a ResilienceError-capable Result keeps
// turning it into a failure, because such a method never throws.
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

    private static string GeneratedSource(Compilation compilation) =>
        string.Join("\n", compilation.SyntaxTrees
            .Where(static t => t.FilePath.EndsWith(".g.cs", StringComparison.Ordinal))
            .Select(static t => t.ToString()))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Generate(string method, string policies = "", string retryWhen = "")
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + $$"""
            [Retry(MaxAttempts = 3, BackoffMs = 10, RetryOnException = nameof(IsRetryable){{retryWhen}})]
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

    [Theory]
    [InlineData("ValueTask<string> GetAsync(CancellationToken ct);", "")]
    [InlineData("Task GetAsync(CancellationToken ct);", "")]
    [InlineData("string Get(CancellationToken ct);", "")]
    [InlineData("void Run(CancellationToken ct);", "")]
    [InlineData("ValueTask<string> GetAsync(CancellationToken ct);", "[Timeout(Ms = 1000)] [CircuitBreaker(MaxFailures = 3)]")]
    [InlineData("string Get(CancellationToken ct);", "[Timeout(Ms = 1000)] [CircuitBreaker(MaxFailures = 3)]")]
    // A Result with a foreign error type cannot hold the exception, so it throws too.
    [InlineData("ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);", "")]
    [InlineData("Result<string, HttpError> Get(CancellationToken ct);", "")]
    public void ExceptionOnlyLoop_ThrowingMethod_RethrowsTheDeclinedException(string method, string policies)
    {
        var generated = Generate(method, policies);

        generated.Should().Contain("if (!global::Repro.IApi.IsRetryable(__ex)) throw;");
        generated.Should().NotContain("IsRetryable(__ex)) break;");
    }

    [Theory]
    [InlineData("ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);")]
    [InlineData("Result<string, HttpError> Get(CancellationToken ct);")]
    [InlineData("ValueTask<UnitResult<HttpError>> GetAsync(CancellationToken ct);")]
    public void ResultAwareLoop_ForeignErrorType_RethrowsTheDeclinedException(string method)
    {
        var generated = Generate(method, retryWhen: ", RetryWhen = nameof(IsTransient)");

        generated.Should().Contain("__lastWasResult");
        generated.Should().Contain("if (!global::Repro.IApi.IsRetryable(__ex)) throw;");
        generated.Should().NotContain("IsRetryable(__ex)) break;");
    }

    [Theory]
    [InlineData("ValueTask<Result<string, ResilienceError>> GetAsync(CancellationToken ct);", "")]
    [InlineData("ValueTask<UnitResult<ResilienceError>> GetAsync(CancellationToken ct);", "")]
    [InlineData("ValueTask<Result<string>> GetAsync(CancellationToken ct);", "")]
    [InlineData("ValueTask<Result<string, ResilienceError>> GetAsync(CancellationToken ct);", ", RetryWhen = nameof(IsTransient)")]
    [InlineData("ValueTask<Result<string>> GetAsync(CancellationToken ct);", ", RetryWhen = nameof(IsTransient)")]
    public void FailureResultMethod_KeepsReturningAFailure(string method, string retryWhen)
    {
        var generated = Generate(method, retryWhen: retryWhen);

        generated.Should().Contain("if (!global::Repro.IApi.IsRetryable(__ex)) break;");
        generated.Should().NotContain("IsRetryable(__ex)) throw;");
        generated.Should().Contain(".Failure(");
    }
}
