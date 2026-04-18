using System.Threading.Tasks;
using ZeroAlloc.Resilience.Generator;
using ZeroAlloc.Resilience.Generator.Tests;

public class SnapshotTests
{
    [Fact]
    public Task Retry_Only_GeneratesProxy()
    {
        var source = """
            using ZeroAlloc.Resilience;
            using System.Threading;
            using System.Threading.Tasks;
            namespace T;
            [Retry(MaxAttempts = 3, BackoffMs = 100)]
            public interface IMyService
            {
                ValueTask<string> GetAsync(string id, CancellationToken ct);
            }
            """;
        return TestHelper.Verify<ResilienceGenerator>(source);
    }

    [Fact]
    public Task AllPolicies_ClassLevel_GeneratesProxy()
    {
        var source = """
            using ZeroAlloc.Resilience;
            using System.Threading;
            using System.Threading.Tasks;
            namespace T;
            [Retry(MaxAttempts = 3, BackoffMs = 200, Jitter = true, PerAttemptTimeoutMs = 1000)]
            [Timeout(Ms = 5000)]
            [RateLimit(MaxPerSecond = 100, BurstSize = 10)]
            [CircuitBreaker(MaxFailures = 5, ResetMs = 1000, HalfOpenProbes = 1)]
            public interface IExternalService
            {
                ValueTask<string> FetchAsync(string id, CancellationToken ct);
                ValueTask<string> FetchFallback(string id, CancellationToken ct);
            }
            """;
        return TestHelper.Verify<ResilienceGenerator>(source);
    }

    [Fact]
    public Task MethodLevel_Override_GeneratesProxy()
    {
        var source = """
            using ZeroAlloc.Resilience;
            using System.Threading;
            using System.Threading.Tasks;
            namespace T;
            [Retry(MaxAttempts = 3, BackoffMs = 200)]
            [Timeout(Ms = 5000)]
            public interface IMyService
            {
                ValueTask<string> GetAsync(string id, CancellationToken ct);
                [Retry(MaxAttempts = 1)]
                [Timeout(Ms = 500)]
                ValueTask PostAsync(string data, CancellationToken ct);
            }
            """;
        return TestHelper.Verify<ResilienceGenerator>(source);
    }

    [Fact]
    public Task CircuitBreaker_WithFallback_GeneratesProxy()
    {
        var source = """
            using ZeroAlloc.Resilience;
            using System.Threading;
            using System.Threading.Tasks;
            namespace T;
            [CircuitBreaker(MaxFailures = 3, ResetMs = 500, Fallback = nameof(FetchFallback))]
            public interface IMyService
            {
                ValueTask<string> FetchAsync(string id, CancellationToken ct);
                ValueTask<string> FetchFallback(string id, CancellationToken ct);
            }
            """;
        return TestHelper.Verify<ResilienceGenerator>(source);
    }
}
