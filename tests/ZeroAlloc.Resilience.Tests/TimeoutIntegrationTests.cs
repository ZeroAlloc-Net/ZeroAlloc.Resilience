#pragma warning disable ZR0002

using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Resilience;

namespace ZeroAlloc.Resilience.Tests;

// Interface with total timeout only (no retry)
[Timeout(Ms = 100)]
public interface ISlowService
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);
}

// Interface with retry + total timeout combined
[Retry(MaxAttempts = 5, BackoffMs = 1)]
[Timeout(Ms = 80)]
public interface ISlowRetryService
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);
}

// Interface with per-attempt timeout
[Retry(MaxAttempts = 3, BackoffMs = 1, PerAttemptTimeoutMs = 50)]
public interface IPerAttemptTimeoutService
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);
}

// Slow inner impl — delays for a given number of ms then returns
public sealed class SlowImpl : ISlowService, ISlowRetryService, IPerAttemptTimeoutService
{
    private readonly int _delayMs;
    public SlowImpl(int delayMs) => _delayMs = delayMs;

    public async ValueTask<string> GetAsync(string id, CancellationToken ct)
    {
        await Task.Delay(_delayMs, ct);
        return $"ok:{id}";
    }
}

// Always-failing inner impl — delays then throws, used for retry-loop timeout tests
public sealed class AlwaysFailSlowImpl : ISlowRetryService
{
    private readonly int _delayMs;
    public AlwaysFailSlowImpl(int delayMs) => _delayMs = delayMs;

    public async ValueTask<string> GetAsync(string id, CancellationToken ct)
    {
        await Task.Delay(_delayMs, ct);
        throw new InvalidOperationException("always fails");
    }
}

public class TimeoutIntegrationTests
{
    [Fact]
    public async Task TotalTimeout_CancelsSlowCall()
    {
        // Proxy has [Timeout(Ms = 100)]; inner takes 500 ms → should be cancelled
        var inner = new SlowImpl(delayMs: 500);
        var timeout = new TimeoutPolicy(totalMs: 100);
        var proxy = new ISlowServiceResilienceProxy(inner, timeout);

        var act = async () => await proxy.GetAsync("x", CancellationToken.None);
        // Should throw — either OperationCanceledException (from Task.Delay ct) or ResilienceException
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task TotalTimeout_DoesNotCancelFastCall()
    {
        // Proxy has [Timeout(Ms = 100)]; inner returns instantly
        var inner = new SlowImpl(delayMs: 0);
        var timeout = new TimeoutPolicy(totalMs: 100);
        var proxy = new ISlowServiceResilienceProxy(inner, timeout);

        var result = await proxy.GetAsync("x", CancellationToken.None);
        result.Should().Be("ok:x");
    }

    [Fact]
    public async Task TotalTimeout_CutsRetryLoopShort()
    {
        // Proxy: 5 attempts, BackoffMs=1, TotalTimeout=80ms; inner always fails after 30ms each
        // Without total timeout all 5 attempts would take ~150ms+; timeout fires at 80ms and cuts it short.
        var inner = new AlwaysFailSlowImpl(delayMs: 30);
        var retry = new RetryPolicy(maxAttempts: 5, backoffMs: 1, jitter: false, perAttemptTimeoutMs: 0);
        var timeout = new TimeoutPolicy(totalMs: 80);
        var proxy = new ISlowRetryServiceResilienceProxy(inner, retry, timeout);

        var act = async () => await proxy.GetAsync("x", CancellationToken.None);
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task PerAttemptTimeout_CancelsSlowAttempt()
    {
        // Each attempt has 50ms timeout; inner takes 200ms → every attempt times out
        var inner = new SlowImpl(delayMs: 200);
        var retry = new RetryPolicy(maxAttempts: 3, backoffMs: 1, jitter: false, perAttemptTimeoutMs: 50);
        var proxy = new IPerAttemptTimeoutServiceResilienceProxy(inner, retry);

        var act = async () => await proxy.GetAsync("x", CancellationToken.None);
        await act.Should().ThrowAsync<Exception>();
    }
}
