namespace ZeroAlloc.Resilience.Generator.Tests;

// Regression coverage for #156: methods returning void, ValueTask or Task must produce a proxy
// that compiles under every policy.
public class VoidReturnTests
{
    [Theory]
    [InlineData("[Retry(MaxAttempts = 2, BackoffMs = 1)]")]
    [InlineData("[Retry(MaxAttempts = 2, BackoffMs = 1, PerAttemptTimeoutMs = 50)]")]
    [InlineData("[Timeout(Ms = 100)]")]
    [InlineData("[RateLimit(MaxPerSecond = 1)]")]
    [InlineData("[CircuitBreaker(MaxFailures = 2)]")]
    [InlineData("[Retry(MaxAttempts = 2)][Timeout(Ms = 100)][RateLimit(MaxPerSecond = 1)][CircuitBreaker(MaxFailures = 2)]")]
    public void VoidShapedMethods_Compile(string attributes)
    {
        var source = $$"""
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            {{attributes}}
            public interface IApi
            {
                ValueTask PostAsync(string body, CancellationToken ct);
                Task SendAsync(CancellationToken ct);
                void Fire(CancellationToken ct);
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void VoidShapedMethods_WithFallback_Compile()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IApi
            {
                [CircuitBreaker(MaxFailures = 2, Fallback = nameof(PostFallback))]
                ValueTask PostAsync(string body, CancellationToken ct);
                ValueTask PostFallback(string body, CancellationToken ct);

                [CircuitBreaker(MaxFailures = 2, Fallback = nameof(FireFallback))]
                void Fire();
                void FireFallback();
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
    }
}
