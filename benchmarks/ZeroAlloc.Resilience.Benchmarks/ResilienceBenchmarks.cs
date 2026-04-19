#pragma warning disable ZR0002

using BenchmarkDotNet.Attributes;
using System;
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

[Retry(MaxAttempts = 3, BackoffMs = 1)]
[Timeout(Ms = 5_000)]
[RateLimit(MaxPerSecond = 1_000_000, BurstSize = 1_000_000)]
[CircuitBreaker(MaxFailures = 5, ResetMs = 1_000, HalfOpenProbes = 1)]
public interface IAllPoliciesService
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);
}

// ── Inner impls ────────────────────────────────────────────────────────────────

public sealed class AlwaysSucceedsImpl : IRetryService, ICircuitService, IRateLimitService, IAllPoliciesService
{
    public ValueTask<string> GetAsync(string id, CancellationToken ct)
        => ValueTask.FromResult("ok");
}

public sealed class AlwaysFailsImpl : ICircuitService
{
    public ValueTask<string> GetAsync(string id, CancellationToken ct)
        => throw new InvalidOperationException("always fails");
}

public sealed class RetryWith2FailuresImpl : IRetryService
{
    private int _callCount;
    public ValueTask<string> GetAsync(string id, CancellationToken ct)
    {
        var n = System.Threading.Interlocked.Increment(ref _callCount) % 3;
        if (n != 0) throw new InvalidOperationException("simulated failure");
        return ValueTask.FromResult("ok");
    }
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
    private IAllPoliciesService _allPoliciesProxy = null!;
    private ICircuitService _cbOpenProxy = null!;
    private IRateLimitService _rateLimitExhaustedProxy = null!;
    private IRetryService _retryWith2FailuresProxy = null!;

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

        _allPoliciesProxy = new IAllPoliciesServiceResilienceProxy(
            _inner, retry, new TimeoutPolicy(5_000), rl, cb);

        var cbOpen = new CircuitBreakerPolicy(1, 60_000, 1); // trip on 1 failure, long reset
        var cbOpenImpl = new AlwaysFailsImpl();
        _cbOpenProxy = new ICircuitServiceResilienceProxy(cbOpenImpl, cbOpen);
        // Pre-trip the circuit
        try { _cbOpenProxy.GetAsync("x", CancellationToken.None).GetAwaiter().GetResult(); } catch { }

        // BurstSize = 0 means immediately exhausted
        var rlExhausted = new RateLimiter(1, 0, RateLimitScope.Shared);
        _rateLimitExhaustedProxy = new IRateLimitServiceResilienceProxy(_inner, rlExhausted);

        var failTwice = new RetryWith2FailuresImpl();
        _retryWith2FailuresProxy = new IRetryServiceResilienceProxy(failTwice, retry);
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

    [Benchmark(Description = "All-policies proxy — happy path")]
    public ValueTask<string> AllPolicies_HappyPath()
        => _allPoliciesProxy.GetAsync("x", CancellationToken.None);

    [Benchmark(Description = "CircuitBreaker proxy — Open (fast-reject)")]
    public async ValueTask CircuitBreaker_Open_FastReject()
    {
        try { await _cbOpenProxy.GetAsync("x", CancellationToken.None).ConfigureAwait(false); }
        catch (ResilienceException) { }
    }

    [Benchmark(Description = "RateLimit proxy — exhausted (fast-reject)")]
    public async ValueTask RateLimit_Exhausted()
    {
        try { await _rateLimitExhaustedProxy.GetAsync("x", CancellationToken.None).ConfigureAwait(false); }
        catch (ResilienceException) { }
    }

    [Benchmark(Description = "Retry proxy — 2 failures then success")]
    public ValueTask<string> RetryProxy_2FailuresThenSuccess()
        => _retryWith2FailuresProxy.GetAsync("x", CancellationToken.None);
}
