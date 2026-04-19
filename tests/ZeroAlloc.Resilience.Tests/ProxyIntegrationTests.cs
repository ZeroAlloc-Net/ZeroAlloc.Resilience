#pragma warning disable ZR0002 // intentional — some test interfaces omit CancellationToken

using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Resilience;

namespace ZeroAlloc.Resilience.Tests;

// ── Test interfaces (generator runs on these) ─────────────────────────────────

[Retry(MaxAttempts = 3, BackoffMs = 1)]
public interface IFlakyService
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);
}

[CircuitBreaker(MaxFailures = 2, ResetMs = 50, HalfOpenProbes = 1, Fallback = nameof(GetFallback))]
public interface ICircuitService
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);
    ValueTask<string> GetFallback(string id, CancellationToken ct);
}

[RateLimit(MaxPerSecond = 2, BurstSize = 2)]
public interface IRateLimitedService
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);
}

// ── Fake inner implementations ──────────────────────────────────────────────────

public sealed class FlakyImpl : IFlakyService
{
    private int _callCount;
    public int FailTimes { get; set; }

    public ValueTask<string> GetAsync(string id, CancellationToken ct)
    {
        _callCount++;
        if (_callCount <= FailTimes)
            throw new InvalidOperationException($"Simulated failure #{_callCount}");
        return ValueTask.FromResult($"ok:{id}");
    }
}

public sealed class CircuitImpl : ICircuitService
{
    public bool ShouldFail { get; set; }

    public ValueTask<string> GetAsync(string id, CancellationToken ct)
    {
        if (ShouldFail) throw new Exception("inner failure");
        return ValueTask.FromResult($"ok:{id}");
    }

    public ValueTask<string> GetFallback(string id, CancellationToken ct)
        => ValueTask.FromResult($"fallback:{id}");
}

public sealed class RateLimitedImpl : IRateLimitedService
{
    public ValueTask<string> GetAsync(string id, CancellationToken ct)
        => ValueTask.FromResult($"ok:{id}");
}

// ── Tests ──────────────────────────────────────────────────────────────────────

public class ProxyIntegrationTests
{
    [Fact]
    public async Task Retry_SucceedsAfterTransientFailures()
    {
        var inner = new FlakyImpl { FailTimes = 2 };
        var retry = new RetryPolicy(maxAttempts: 3, backoffMs: 1, jitter: false, perAttemptTimeoutMs: 0);
        var proxy = new IFlakyServiceResilienceProxy(inner, retry);

        var result = await proxy.GetAsync("x", CancellationToken.None);
        result.Should().Be("ok:x");
    }

    [Fact]
    public async Task Retry_ThrowsAfterExhaustion()
    {
        var inner = new FlakyImpl { FailTimes = 10 }; // more than MaxAttempts
        var retry = new RetryPolicy(maxAttempts: 3, backoffMs: 1, jitter: false, perAttemptTimeoutMs: 0);
        var proxy = new IFlakyServiceResilienceProxy(inner, retry);

        var act = async () => await proxy.GetAsync("x", CancellationToken.None);
        await act.Should().ThrowAsync<ResilienceException>()
            .Where(e => e.Policy == ResiliencePolicy.Retry);
    }

    [Fact]
    public async Task CircuitBreaker_OpenAfterFailures_CallsFallback()
    {
        var inner = new CircuitImpl { ShouldFail = true };
        var cb = new CircuitBreakerPolicy(maxFailures: 2, resetMs: 1000, halfOpenProbes: 1);
        var proxy = new ICircuitServiceResilienceProxy(inner, cb);

        // Trip the circuit (2 failures needed)
        try { await proxy.GetAsync("x", CancellationToken.None); } catch { }
        try { await proxy.GetAsync("x", CancellationToken.None); } catch { }

        // Circuit is now open — should invoke fallback
        var result = await proxy.GetAsync("x", CancellationToken.None);
        result.Should().Be("fallback:x");
    }

    [Fact]
    public async Task RateLimit_BlocksWhenBucketEmpty()
    {
        var inner = new RateLimitedImpl();
        var limiter = new RateLimiter(maxPerSecond: 2, burstSize: 2, scope: RateLimitScope.Shared);
        var proxy = new IRateLimitedServiceResilienceProxy(inner, limiter);

        // Consume burst
        (await proxy.GetAsync("1", CancellationToken.None)).Should().Be("ok:1");
        (await proxy.GetAsync("2", CancellationToken.None)).Should().Be("ok:2");

        // Bucket empty — should throw
        var act = async () => await proxy.GetAsync("3", CancellationToken.None);
        await act.Should().ThrowAsync<ResilienceException>()
            .Where(e => e.Policy == ResiliencePolicy.RateLimit);
    }
}
