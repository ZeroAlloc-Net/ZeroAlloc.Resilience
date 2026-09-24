using System.Linq;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Resilience.Generator.Tests;

// Regression coverage for #141: every ZeroAlloc.Results return type must produce a proxy that
// compiles. Each test compiles the generated output and asserts that there are no errors.
public class ResultReturnTypeTests
{
    // Method A carries every policy, so it reaches the rate-limit, circuit-breaker and
    // retry-exhaustion failure sites. Method B has no retry, so it reaches the single-call catch.
    private static string AllFailureSites(string wrapper, string resultType) => $$"""
        using System.Threading;
        using System.Threading.Tasks;
        using ZeroAlloc.Resilience;
        using ZeroAlloc.Results;
        namespace Repro;
        [Timeout(Ms = 1000)]
        public interface IApi
        {
            [Retry(MaxAttempts = 3, BackoffMs = 10)]
            [RateLimit(MaxPerSecond = 10, BurstSize = 10)]
            [CircuitBreaker(MaxFailures = 3, ResetMs = 500)]
            {{wrapper}}<{{resultType}}> AAsync(string id, CancellationToken ct);

            [CircuitBreaker(MaxFailures = 3, ResetMs = 500)]
            {{wrapper}}<{{resultType}}> BAsync(string id, CancellationToken ct);
        }
        """;

    [Theory]
    [InlineData("ValueTask")]
    [InlineData("Task")]
    public void ResultOfT_Compiles(string wrapper)
    {
        var (compilation, errors) = TestHelper.RunAndCompile(AllFailureSites(wrapper, "Result<string>"));

        errors.Should().BeEmpty();
        GeneratedSource(compilation).Should().Contain("global::ZeroAlloc.Results.Result<string>.Failure(");
    }

    [Theory]
    [InlineData("ValueTask")]
    [InlineData("Task")]
    public void NonGenericResult_Compiles(string wrapper)
    {
        var (compilation, errors) = TestHelper.RunAndCompile(AllFailureSites(wrapper, "Result"));

        errors.Should().BeEmpty();
        GeneratedSource(compilation).Should().Contain("global::ZeroAlloc.Results.Result.Failure(");
    }

    [Theory]
    [InlineData("ValueTask")]
    [InlineData("Task")]
    public void ResultOfTResilienceError_Compiles(string wrapper)
    {
        var (compilation, errors) = TestHelper.RunAndCompile(
            AllFailureSites(wrapper, "Result<string, ResilienceError>"));

        errors.Should().BeEmpty();
        GeneratedSource(compilation).Should()
            .Contain("new global::ZeroAlloc.Resilience.ResilienceError(\"RateLimit\"")
            .And.Contain("new global::ZeroAlloc.Resilience.ResilienceError(\"CircuitBreaker\"")
            .And.Contain("new global::ZeroAlloc.Resilience.ResilienceError(\"Retry\"");
    }

    [Theory]
    [InlineData("ValueTask")]
    [InlineData("Task")]
    public void NonThrowing_ResultOfTResilienceError_WithEveryPolicy_Compiles(string wrapper)
    {
        var source = $$"""
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            using ZeroAlloc.Results;
            namespace Repro;
            [Retry(MaxAttempts = 3, BackoffMs = 10, NonThrowing = true)]
            [Timeout(Ms = 1000)]
            [RateLimit(MaxPerSecond = 10, BurstSize = 10)]
            [CircuitBreaker(MaxFailures = 3, ResetMs = 500)]
            public interface IApi
            {
                {{wrapper}}<Result<string, ResilienceError>> GetAsync(string id, CancellationToken ct);
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void NonThrowing_SyncResultOfTResilienceError_Compiles()
    {
        var source = """
            using ZeroAlloc.Resilience;
            using ZeroAlloc.Results;
            namespace Repro;
            [Retry(MaxAttempts = 3, BackoffMs = 10, NonThrowing = true)]
            public interface IApi
            {
                Result<string, ResilienceError> Get(string id);
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
    }

    // A foreign error type under the policies that never have to invent an E: retry and timeout
    // hand back whatever Result the inner call returned, and an open circuit calls the fallback.
    [Theory]
    [InlineData("ValueTask")]
    [InlineData("Task")]
    public void ForeignError_UnderSupportedPolicies_Compiles(string wrapper)
    {
        var source = $$"""
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            using ZeroAlloc.Results;
            namespace Repro;
            public readonly record struct HttpError(int StatusCode);
            [Retry(MaxAttempts = 3, BackoffMs = 10)]
            [Timeout(Ms = 1000)]
            [CircuitBreaker(MaxFailures = 3, ResetMs = 500, Fallback = nameof(ModelsFallback))]
            public interface IApi
            {
                {{wrapper}}<Result<string, HttpError>> ModelsAsync(CancellationToken ct);
                {{wrapper}}<Result<string, HttpError>> ModelsFallback(CancellationToken ct);
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        GeneratedSource(compilation).Should().NotContain(".Failure(");
    }

    [Theory]
    [InlineData("ValueTask")]
    [InlineData("Task")]
    public void ForeignError_SingleCallWithTimeout_Compiles(string wrapper)
    {
        var source = $$"""
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            using ZeroAlloc.Results;
            namespace Repro;
            public readonly record struct HttpError(int StatusCode);
            [Timeout(Ms = 1000)]
            public interface IApi
            {
                {{wrapper}}<Result<string, HttpError>> ModelsAsync(CancellationToken ct);
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("ValueTask", "[RateLimit(MaxPerSecond = 10, BurstSize = 10)]")]
    [InlineData("Task", "[RateLimit(MaxPerSecond = 10, BurstSize = 10)]")]
    [InlineData("ValueTask", "[CircuitBreaker(MaxFailures = 3, ResetMs = 500)]")]
    [InlineData("Task", "[CircuitBreaker(MaxFailures = 3, ResetMs = 500)]")]
    [InlineData("ValueTask", "[Retry(MaxAttempts = 3, BackoffMs = 10, NonThrowing = true)]")]
    [InlineData("Task", "[Retry(MaxAttempts = 3, BackoffMs = 10, NonThrowing = true)]")]
    public void ForeignError_UnderUnsupportedPolicy_ReportsZR0003_AndNoCompilerErrors(string wrapper, string policy)
    {
        var source = $$"""
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            using ZeroAlloc.Results;
            namespace Repro;
            public readonly record struct HttpError(int StatusCode);
            {{policy}}
            public interface IApi
            {
                {{wrapper}}<Result<string, HttpError>> ModelsAsync(CancellationToken ct);
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().ContainSingle();
        errors[0].Id.Should().Be("ZR0003");
        errors[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)
            .Should().Contain("ModelsAsync").And.Contain("HttpError");
    }

    [Fact]
    public void ForeignError_SyncNonThrowing_ReportsZR0003()
    {
        var source = """
            using ZeroAlloc.Resilience;
            using ZeroAlloc.Results;
            namespace Repro;
            public readonly record struct HttpError(int StatusCode);
            [Retry(MaxAttempts = 3, BackoffMs = 10, NonThrowing = true)]
            public interface IApi
            {
                Result<string, HttpError> Models();
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().ContainSingle().Which.Id.Should().Be("ZR0003");
    }

    private static string GeneratedSource(Compilation compilation) =>
        string.Join("\n", compilation.SyntaxTrees
            .Where(static t => t.FilePath.EndsWith(".g.cs", System.StringComparison.Ordinal))
            .Select(static t => t.ToString()));
}
