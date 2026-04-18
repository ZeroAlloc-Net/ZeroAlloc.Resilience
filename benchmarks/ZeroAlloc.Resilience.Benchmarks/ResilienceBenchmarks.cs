#pragma warning disable ZR0002

using BenchmarkDotNet.Attributes;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Resilience;

namespace ZeroAlloc.Resilience.Benchmarks;

// ── Benchmark interfaces (generator produces proxies for these) ─────────────────

[Retry(MaxAttempts = 3, BackoffMs = 1)]
public interface IRetryService
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);
}

[CircuitBreaker(MaxFailures = 5, ResetMs = 1000, HalfOpenProbes = 1)]
public interface ICircuitService
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);
}

[RateLimit(MaxPerSecond = 1_000_000, BurstSize = 1_000_000)]
public interface IRateLimitService
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);
}

// ── Inner impl (always succeeds) ───────────────────────────────────────────────

public sealed class AlwaysSucceedsImpl : IRetryService, ICircuitService, IRateLimitService
{
    public ValueTask<string> GetAsync(string id, CancellationToken ct)
        => ValueTask.FromResult("ok");
}

// ── Benchmarks ─────────────────────────────────────────────────────────────────

[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class ResilienceBenchmarks
{
    private AlwaysSucceedsImpl _inner = null!;
    private IRetryService _retryProxy = null!;
    private ICircuitService _cbProxy = null!;
    private IRateLimitService _rateLimitProxy = null!;

    [GlobalSetup]
    public void Setup()
    {
        _inner = new AlwaysSucceedsImpl();
        var retry = new RetryPolicy(3, 1, false, 0);
        var cb = new CircuitBreakerPolicy(5, 1_000, 1);
        var rl = new RateLimiter(1_000_000, 1_000_000, RateLimitScope.Shared);

        _retryProxy     = new IRetryServiceResilienceProxy(_inner, retry);
        _cbProxy        = new ICircuitServiceResilienceProxy(_inner, cb);
        _rateLimitProxy = new IRateLimitServiceResilienceProxy(_inner, rl);
    }

    [Benchmark(Baseline = true, Description = "Direct call (no proxy)")]
    public ValueTask<string> Direct()
        => _inner.GetAsync("x", CancellationToken.None);

    [Benchmark(Description = "Retry proxy — first attempt succeeds")]
    public ValueTask<string> RetryProxy_HappyPath()
        => _retryProxy.GetAsync("x", CancellationToken.None);

    [Benchmark(Description = "CircuitBreaker proxy — Closed state")]
    public ValueTask<string> CircuitBreaker_Closed()
        => _cbProxy.GetAsync("x", CancellationToken.None);

    [Benchmark(Description = "RateLimit proxy — token available")]
    public ValueTask<string> RateLimit_TokenAvailable()
        => _rateLimitProxy.GetAsync("x", CancellationToken.None);
}
