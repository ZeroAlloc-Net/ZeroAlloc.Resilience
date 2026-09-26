using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Resilience.Generator.Tests;

// #142 and #143: the emitted Result-aware loop compiles for every Result shape and policy
// combination, and keeps the predicates and hints out of the try.
public class ResultAwareRetryTests
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

    [Theory]
    [InlineData("ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);", "")]
    [InlineData("Task<Result<string, HttpError>> GetAsync(CancellationToken ct);", "")]
    [InlineData("ValueTask<UnitResult<HttpError>> GetAsync(CancellationToken ct);", "")]
    [InlineData("Result<string, HttpError> Get(CancellationToken ct);", "")]
    [InlineData("UnitResult<HttpError> Get(CancellationToken ct);", "")]
    [InlineData("Result<string, HttpError> Get();", "")]
    [InlineData("ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);", "[Timeout(Ms = 1000)]")]
    [InlineData("Result<string, HttpError> Get(CancellationToken ct);", "[Timeout(Ms = 1000)]")]
    [InlineData("UnitResult<HttpError> Get(CancellationToken ct);", "[Timeout(Ms = 1000)] [CircuitBreaker(MaxFailures = 3)]")]
    [InlineData("Result<string, HttpError> Get();", "[Timeout(Ms = 1000)]")]
    [InlineData("ValueTask<Result<string, HttpError>> GetAsync();", "[Timeout(Ms = 1000)]")]
    public void ForeignErrorType_ResultAwareLoop_Compiles(string method, string policies)
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + $$"""
            [Retry(MaxAttempts = 3, BackoffMs = 10, RetryWhen = nameof(IsTransient),
                   RetryOnException = nameof(IsTransientException), DelayHint = nameof(RetryAfter))]
            {{policies}}
            public interface IApi
            {
                {{method}}
                static bool IsTransient(HttpError error) => error.Status == 429;
                static bool IsTransientException(Exception exception) => true;
                static TimeSpan? RetryAfter(HttpError error) => null;
                static TimeSpan? RetryAfter(Exception exception) => null;
            }
            """);

        errors.Should().BeEmpty();
        GeneratedSource(compilation).Should().Contain("__lastWasResult");
    }

    [Theory]
    [InlineData("ValueTask<Result<string>>", "string")]
    [InlineData("Task<Result>", "string")]
    [InlineData("ValueTask<Result<string, ResilienceError>>", "ResilienceError")]
    [InlineData("ValueTask<UnitResult<ResilienceError>>", "ResilienceError")]
    public void BuildableErrorType_WithEveryPolicy_Compiles(string returnType, string errorType)
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + $$"""
            [Retry(MaxAttempts = 3, BackoffMs = 10, RetryWhen = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
            [Timeout(Ms = 1000)]
            [RateLimit(MaxPerSecond = 10, BurstSize = 10)]
            [CircuitBreaker(MaxFailures = 3, ResetMs = 500)]
            public interface IApi
            {
                {{returnType}} GetAsync(CancellationToken ct);
                static bool IsTransient({{errorType}} error) => true;
                static TimeSpan? RetryAfter({{errorType}} error) => null;
            }
            """);

        errors.Should().BeEmpty();
        GeneratedSource(compilation).Should().Contain("return __lastResult;");
    }

    [Fact]
    public void Predicate_And_Hints_RunOutsideTheTry()
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + """
            [Retry(RetryWhen = nameof(IsTransient), RetryOnException = nameof(IsTransientException), DelayHint = nameof(RetryAfter))]
            public interface IApi
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
                static bool IsTransient(HttpError error) => true;
                static bool IsTransientException(Exception exception) => true;
                static TimeSpan? RetryAfter(HttpError error) => null;
                static TimeSpan? RetryAfter(Exception exception) => null;
            }
            """);

        errors.Should().BeEmpty();
        var generated = GeneratedSource(compilation);
        generated.Should().Contain("""
                        try
                        {
                            __lastResult = await _inner.GetAsync(__ct).ConfigureAwait(false);
                            __lastWasResult = true;
                        }
                        catch (global::System.OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (global::System.Exception __ex)
                        {
                            __lastEx = __ex;
                            __lastWasResult = false;
                            if (!global::Repro.IApi.IsTransientException(__ex)) break;
                            __hint = global::Repro.IApi.RetryAfter(__ex);
                        }
                        if (__lastWasResult)
                        {
                            if (__lastResult.IsSuccess || !global::Repro.IApi.IsTransient(__lastResult.Error))
                            {
                                return __lastResult;
                            }
                            __hint = global::Repro.IApi.RetryAfter(__lastResult.Error);
                        }
            """.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void ExceptionOnlyDelayHint_ResultAwareLoop_TakesTheHintFromExceptionsOnly()
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + """
            [Retry(RetryWhen = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
            public interface IApi
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
                static bool IsTransient(HttpError error) => true;
                static TimeSpan? RetryAfter(Exception exception) => null;
            }
            """);

        errors.Should().BeEmpty();
        GeneratedSource(compilation).Should()
            .Contain("__hint = global::Repro.IApi.RetryAfter(__ex);")
            .And.Contain("_retry.GetDelayMs(__attempt, __hint)")
            .And.NotContain("RetryAfter(__lastResult.Error)");
    }

    [Fact]
    public void MixedInterface_OnlyTheResultMethodGetsTheResultAwareLoop()
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + """
            [Retry(RetryWhen = nameof(IsTransient))]
            public interface IApi
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
                ValueTask<string> GetTextAsync(CancellationToken ct);
                static bool IsTransient(HttpError error) => true;
            }
            """);

        errors.Should().BeEmpty();
        GeneratedSource(compilation).Split("__lastWasResult = true;").Length.Should().Be(2);
    }

    [Fact]
    public void MembersOfABaseInterface_AreCalledThroughIt()
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + """
            public interface IRetryRules
            {
                static bool IsTransient(HttpError error) => error.Status == 429;
                static TimeSpan? RetryAfter(HttpError error) => null;
            }
            [Retry(RetryWhen = nameof(IRetryRules.IsTransient), DelayHint = nameof(IRetryRules.RetryAfter))]
            public interface IApi : IRetryRules
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
            }
            """);

        errors.Should().BeEmpty();
        GeneratedSource(compilation).Should()
            .Contain("global::Repro.IRetryRules.IsTransient(__lastResult.Error)")
            .And.Contain("__hint = global::Repro.IRetryRules.RetryAfter(__lastResult.Error);");
    }

    [Fact]
    public void NoNewProperty_KeepsTheExceptionOnlyLoop()
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + """
            [Retry(MaxAttempts = 3)]
            public interface IApi
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
            }
            """);

        errors.Should().BeEmpty();
        GeneratedSource(compilation).Should()
            .NotContain("__lastWasResult")
            .And.NotContain("GetDelayMs")
            .And.Contain("_retry.GetBackoffMs(__attempt)");
    }
}
