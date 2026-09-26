using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Results;

namespace ZeroAlloc.Resilience.Tests;

// The caller's own cancellation ends the retry loop with OperationCanceledException: it is not
// retried, not wrapped in ResilienceException, and not counted by the circuit breaker. The
// backoff is 60 s, so a loop that ignored the caller's token would hit the 10 s test timeout.

[Retry(MaxAttempts = 5, BackoffMs = 60_000)]
public interface ICancellableRetryApi
{
    ValueTask<string> GetAsync(CancellationToken ct);
    string Get(CancellationToken ct);
}

[Retry(MaxAttempts = 5, BackoffMs = 60_000)]
[Timeout(Ms = 120_000)]
public interface ICancellableTimedRetryApi
{
    ValueTask<string> GetAsync(CancellationToken ct);
    string Get(CancellationToken ct);
}

[Retry(MaxAttempts = 2, BackoffMs = 1)]
[CircuitBreaker(MaxFailures = 1, ResetMs = 60_000)]
public interface ICancellableBreakerApi
{
    ValueTask<string> GetAsync(CancellationToken ct);
}

public sealed class FailingCancellableApi : ICancellableRetryApi, ICancellableTimedRetryApi, ICancellableBreakerApi
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    // Cancelled by the first call itself, just before it fails and ignoring the token it was
    // given, so the caller's cancellation always lands after the first failure.
    public CancellationTokenSource? CancelOnFirstCall { get; init; }

    public ValueTask<string> GetAsync(CancellationToken ct) => ValueTask.FromResult(Get(ct));

    public string Get(CancellationToken ct)
    {
        var call = Interlocked.Increment(ref _calls);
        ct.ThrowIfCancellationRequested();
        if (call == 1)
            CancelOnFirstCall?.Cancel();
        throw new InvalidOperationException("boom");
    }
}

public class CallerCancellationIntegrationTests
{
    private static Func<CancellationToken, Task> Call(FailingCancellableApi inner, bool timed, bool sync)
    {
        if (timed)
        {
            var proxy = new ICancellableTimedRetryApiResilienceProxy(inner, new CancellableTimedRetryApiResiliencePolicies());
            return sync ? ct => Task.Run(() => proxy.Get(ct)) : ct => proxy.GetAsync(ct).AsTask();
        }

        var plain = new ICancellableRetryApiResilienceProxy(inner, new CancellableRetryApiResiliencePolicies());
        return sync ? ct => Task.Run(() => plain.Get(ct)) : ct => plain.GetAsync(ct).AsTask();
    }

    [Theory(Timeout = 10_000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AlreadyCancelledCaller_IsNotRetried(bool timed, bool sync)
    {
        var inner = new FailingCancellableApi();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => Call(inner, timed, sync)(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        inner.Calls.Should().Be(1);
    }

    [Theory(Timeout = 10_000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CallerCancelledAfterFirstFailure_ThrowsOperationCanceledException(bool timed, bool sync)
    {
        using var cts = new CancellationTokenSource();
        var inner = new FailingCancellableApi { CancelOnFirstCall = cts };
        var started = Stopwatch.GetTimestamp();

        var act = () => Call(inner, timed, sync)(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Stopwatch.GetElapsedTime(started).Should().BeLessThan(TimeSpan.FromSeconds(5));
        inner.Calls.Should().Be(1);
    }

    [Fact(Timeout = 10_000)]
    public async Task CallerCancellation_IsNotACircuitBreakerFailure()
    {
        var inner = new FailingCancellableApi();
        using var cb = new CircuitBreakerPolicy(maxFailures: 1, resetMs: 60_000, halfOpenProbes: 1);
        var proxy = new ICancellableBreakerApiResilienceProxy(inner, new CancellableBreakerApiResiliencePolicies { CircuitBreaker = cb });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await proxy.GetAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        cb.State.Should().Be(CircuitBreakerState.Closed);
    }
}

// A per-attempt timeout is not the caller's cancellation, even with a live caller token: each
// timed-out attempt is retried, and exhaustion wraps the last OperationCanceledException.

[Retry(MaxAttempts = 3, BackoffMs = 1, PerAttemptTimeoutMs = 50)]
public interface IPerAttemptTimeoutLiveTokenApi
{
    ValueTask<string> GetAsync(CancellationToken ct);
    string Get(CancellationToken ct);
}

public sealed class SlowCancellableApi : IPerAttemptTimeoutLiveTokenApi
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public async ValueTask<string> GetAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        return "late";
    }

    public string Get(CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
        ct.ThrowIfCancellationRequested();
        return "late";
    }
}

public class PerAttemptTimeoutWithLiveCallerTokenIntegrationTests
{
    [Theory(Timeout = 10_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PerAttemptTimeout_IsRetried_AndExhaustionWrapsTheCancellation(bool sync)
    {
        var inner = new SlowCancellableApi();
        var proxy = new IPerAttemptTimeoutLiveTokenApiResilienceProxy(inner, new PerAttemptTimeoutLiveTokenApiResiliencePolicies());
        using var cts = new CancellationTokenSource();

        Func<Task> act = sync
            ? () => Task.Run(() => proxy.Get(cts.Token))
            : async () => await proxy.GetAsync(cts.Token);

        var thrown = await act.Should().ThrowExactlyAsync<ResilienceException>();
        thrown.Which.Policy.Should().Be(ResiliencePolicy.Retry);
        thrown.Which.InnerException.Should().BeAssignableTo<OperationCanceledException>();
        inner.Calls.Should().Be(3);
        cts.IsCancellationRequested.Should().BeFalse();
    }
}

// A single guarded call, without [Retry], treats the caller's own cancellation the same way: a
// Result method does not turn it into a Failure, and the circuit breaker does not count it.

[CircuitBreaker(MaxFailures = 1, ResetMs = 60_000)]
public interface ICancellableResultBreakerApi
{
    ValueTask<Result<string>> GetResultAsync(CancellationToken ct);
    Result<string> GetResult(CancellationToken ct);
}

[Timeout(Ms = 60_000)]
public interface ICancellableResultTimeoutApi
{
    ValueTask<Result<string>> GetResultAsync(CancellationToken ct);
    Result<string> GetResult(CancellationToken ct);
}

[Timeout(Ms = 60_000)]
[CircuitBreaker(MaxFailures = 1, ResetMs = 60_000)]
public interface ICancellableResultTimedBreakerApi
{
    ValueTask<Result<string>> GetResultAsync(CancellationToken ct);
    Result<string> GetResult(CancellationToken ct);
}

[CircuitBreaker(MaxFailures = 1, ResetMs = 60_000)]
public interface ICancellablePlainBreakerApi
{
    ValueTask<string> GetAsync(CancellationToken ct);
    string Get(CancellationToken ct);
}

public sealed class CancellableSingleCallApi
    : ICancellableResultBreakerApi, ICancellableResultTimeoutApi, ICancellableResultTimedBreakerApi, ICancellablePlainBreakerApi
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public ValueTask<Result<string>> GetResultAsync(CancellationToken ct) => ValueTask.FromResult(GetResult(ct));

    public Result<string> GetResult(CancellationToken ct) => Result<string>.Success(Get(ct));

    public ValueTask<string> GetAsync(CancellationToken ct) => ValueTask.FromResult(Get(ct));

    public string Get(CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        ct.ThrowIfCancellationRequested();
        return "ok";
    }
}

public enum SingleCallGuard
{
    ResultBreaker,
    ResultTimeout,
    ResultTimedBreaker,
    PlainBreaker,
}

public class SingleCallCallerCancellationIntegrationTests
{
    private static Func<CancellationToken, Task> Call(
        CancellableSingleCallApi inner, SingleCallGuard guard, CircuitBreakerPolicy cb, bool sync)
    {
        switch (guard)
        {
            case SingleCallGuard.ResultBreaker:
            {
                var proxy = new ICancellableResultBreakerApiResilienceProxy(
                    inner, new CancellableResultBreakerApiResiliencePolicies { CircuitBreaker = cb });
                return sync ? ct => Task.Run(() => proxy.GetResult(ct)) : ct => proxy.GetResultAsync(ct).AsTask();
            }
            case SingleCallGuard.ResultTimeout:
            {
                var proxy = new ICancellableResultTimeoutApiResilienceProxy(
                    inner, new CancellableResultTimeoutApiResiliencePolicies());
                return sync ? ct => Task.Run(() => proxy.GetResult(ct)) : ct => proxy.GetResultAsync(ct).AsTask();
            }
            case SingleCallGuard.ResultTimedBreaker:
            {
                var proxy = new ICancellableResultTimedBreakerApiResilienceProxy(
                    inner, new CancellableResultTimedBreakerApiResiliencePolicies { CircuitBreaker = cb });
                return sync ? ct => Task.Run(() => proxy.GetResult(ct)) : ct => proxy.GetResultAsync(ct).AsTask();
            }
            default:
            {
                var proxy = new ICancellablePlainBreakerApiResilienceProxy(
                    inner, new CancellablePlainBreakerApiResiliencePolicies { CircuitBreaker = cb });
                return sync ? ct => Task.Run(() => proxy.Get(ct)) : ct => proxy.GetAsync(ct).AsTask();
            }
        }
    }

    [Theory(Timeout = 10_000)]
    [InlineData(SingleCallGuard.ResultBreaker, false)]
    [InlineData(SingleCallGuard.ResultBreaker, true)]
    [InlineData(SingleCallGuard.ResultTimeout, false)]
    [InlineData(SingleCallGuard.ResultTimeout, true)]
    [InlineData(SingleCallGuard.ResultTimedBreaker, false)]
    [InlineData(SingleCallGuard.ResultTimedBreaker, true)]
    [InlineData(SingleCallGuard.PlainBreaker, false)]
    [InlineData(SingleCallGuard.PlainBreaker, true)]
    public async Task CallerCancellation_Propagates_AndIsNotACircuitBreakerFailure(SingleCallGuard guard, bool sync)
    {
        var inner = new CancellableSingleCallApi();
        using var cb = new CircuitBreakerPolicy(maxFailures: 1, resetMs: 60_000, halfOpenProbes: 1);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => Call(inner, guard, cb, sync)(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        inner.Calls.Should().Be(1);
        cb.State.Should().Be(CircuitBreakerState.Closed);
    }
}
