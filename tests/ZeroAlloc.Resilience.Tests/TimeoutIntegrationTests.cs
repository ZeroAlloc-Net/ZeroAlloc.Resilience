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

// Inner impl that never completes on its own: a call ends only when its token is cancelled, so a
// timeout test asserts the timeout path rather than a race between the inner delay's timer and
// the timeout's timer, which a starved thread pool can fire together.
public sealed class NeverCompletesImpl : ISlowService, ISlowRetryService, IPerAttemptTimeoutService
{
    private int _calls;
    private int _cancelledCalls;

    public int Calls => Volatile.Read(ref _calls);
    public int CancelledCalls => Volatile.Read(ref _cancelledCalls);

    public async ValueTask<string> GetAsync(string id, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        try
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Interlocked.Increment(ref _cancelledCalls);
            throw;
        }
        throw new InvalidOperationException("An infinite delay completed without cancellation.");
    }
}

public class TimeoutIntegrationTests
{
    [Fact]
    public async Task TotalTimeout_CancelsSlowCall()
    {
        // Proxy has [Timeout(Ms = 100)]; the inner call never completes on its own, so only the
        // total timeout can end it, and its cancellation reaches the caller unchanged.
        var inner = new NeverCompletesImpl();
        var timeout = new TimeoutPolicy(totalMs: 100);
        var proxy = new ISlowServiceResilienceProxy(inner, new SlowServiceResiliencePolicies { Timeout = timeout });

        var act = async () => await proxy.GetAsync("x", CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        inner.Calls.Should().Be(1);
        inner.CancelledCalls.Should().Be(1);
    }

    [Fact]
    public async Task TotalTimeout_DoesNotCancelFastCall()
    {
        // Proxy has [Timeout(Ms = 100)]; inner returns instantly
        var inner = new SlowImpl(delayMs: 0);
        var timeout = new TimeoutPolicy(totalMs: 100);
        var proxy = new ISlowServiceResilienceProxy(inner, new SlowServiceResiliencePolicies { Timeout = timeout });

        var result = await proxy.GetAsync("x", CancellationToken.None);
        result.Should().Be("ok:x");
    }

    [Fact]
    public async Task TotalTimeout_CutsRetryLoopShort()
    {
        // Proxy: 5 attempts, BackoffMs=1, TotalTimeout=80ms. The first attempt never completes on
        // its own, so the total timeout ends it and the loop stops there instead of retrying.
        var inner = new NeverCompletesImpl();
        var retry = new RetryPolicy(maxAttempts: 5, backoffMs: 1, jitter: false, perAttemptTimeoutMs: 0);
        var timeout = new TimeoutPolicy(totalMs: 80);
        var proxy = new ISlowRetryServiceResilienceProxy(inner, new SlowRetryServiceResiliencePolicies { Retry = retry, Timeout = timeout });

        var act = async () => await proxy.GetAsync("x", CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<ResilienceException>();
        thrown.Which.Policy.Should().Be(ResiliencePolicy.Retry);
        inner.Calls.Should().Be(1);
        inner.CancelledCalls.Should().Be(1);
    }

    [Fact]
    public async Task PerAttemptTimeout_CancelsSlowAttempt()
    {
        // Each attempt has a 50 ms timeout and the inner call never completes on its own, so every
        // attempt can end only through the per-attempt token. A timer-bound inner delay raced the
        // timeout timer instead, and lost on a starved thread pool: see #188.
        var inner = new NeverCompletesImpl();
        var retry = new RetryPolicy(maxAttempts: 3, backoffMs: 1, jitter: false, perAttemptTimeoutMs: 50);
        var proxy = new IPerAttemptTimeoutServiceResilienceProxy(inner, new PerAttemptTimeoutServiceResiliencePolicies { Retry = retry });

        var act = async () => await proxy.GetAsync("x", CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<ResilienceException>();
        thrown.Which.Policy.Should().Be(ResiliencePolicy.Retry);
        thrown.Which.InnerException.Should().BeAssignableTo<OperationCanceledException>();
        inner.Calls.Should().Be(3);
        inner.CancelledCalls.Should().Be(3);
    }
}
