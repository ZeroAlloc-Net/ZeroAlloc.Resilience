using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Results;

namespace ZeroAlloc.Resilience.Tests;

// A [Timeout] that fires while the retry loop waits out its backoff ends the loop through
// exhaustion: a Failure for a Result method, a ResilienceException otherwise. It never lets the
// wait's own TaskCanceledException escape. The backoff is 60 s, so a wait that ignored the total
// timeout would hit the 10 s test timeout. The caller's own cancellation still propagates.

[Retry(MaxAttempts = 5, BackoffMs = 60_000)]
[Timeout(Ms = 200)]
public interface ITimedOutBackoffApi
{
    ValueTask<string> GetAsync(CancellationToken ct);
    string Get(CancellationToken ct);
    ValueTask<Result<string>> GetResultAsync(CancellationToken ct);
    Result<string> GetResult(CancellationToken ct);
    ValueTask<Result<string, ResilienceError>> GetTypedAsync(CancellationToken ct);
}

[Retry(MaxAttempts = 5, BackoffMs = 60_000)]
[Timeout(Ms = 200)]
[CircuitBreaker(MaxFailures = 10, ResetMs = 60_000)]
public interface ITimedOutBackoffBreakerApi
{
    ValueTask<string> GetAsync(CancellationToken ct);
    string Get(CancellationToken ct);
    ValueTask<Result<string>> GetResultAsync(CancellationToken ct);
    Result<string> GetResult(CancellationToken ct);
    ValueTask<Result<string, ResilienceError>> GetTypedAsync(CancellationToken ct);
}

public sealed class FailingBackoffApi : ITimedOutBackoffApi, ITimedOutBackoffBreakerApi
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

    public ValueTask<Result<string>> GetResultAsync(CancellationToken ct) => ValueTask.FromResult(GetResult(ct));

    public Result<string> GetResult(CancellationToken ct) => Result<string>.Success(Get(ct));

    public ValueTask<Result<string, ResilienceError>> GetTypedAsync(CancellationToken ct)
        => ValueTask.FromResult(Result<string, ResilienceError>.Success(Get(ct)));
}

public class TotalTimeoutDuringBackoffIntegrationTests
{
    private static ITimedOutBackoffApi Proxy(FailingBackoffApi inner, bool breaker, CircuitBreakerPolicy cb) =>
        breaker
            ? new BreakerAdapter(new ITimedOutBackoffBreakerApiResilienceProxy(
                inner, new TimedOutBackoffBreakerApiResiliencePolicies { CircuitBreaker = cb }))
            : new ITimedOutBackoffApiResilienceProxy(inner, new TimedOutBackoffApiResiliencePolicies());

    // Presents the breaker proxy through the plain interface so both share one set of tests.
    private sealed class BreakerAdapter(ITimedOutBackoffBreakerApi proxy) : ITimedOutBackoffApi
    {
        public ValueTask<string> GetAsync(CancellationToken ct) => proxy.GetAsync(ct);
        public string Get(CancellationToken ct) => proxy.Get(ct);
        public ValueTask<Result<string>> GetResultAsync(CancellationToken ct) => proxy.GetResultAsync(ct);
        public Result<string> GetResult(CancellationToken ct) => proxy.GetResult(ct);
        public ValueTask<Result<string, ResilienceError>> GetTypedAsync(CancellationToken ct) => proxy.GetTypedAsync(ct);
    }

    [Theory(Timeout = 10_000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NonResult_TimeoutDuringBackoff_ThrowsResilienceException(bool breaker, bool sync)
    {
        var inner = new FailingBackoffApi();
        using var cb = new CircuitBreakerPolicy(maxFailures: 10, resetMs: 60_000, halfOpenProbes: 1);
        var proxy = Proxy(inner, breaker, cb);
        var started = Stopwatch.GetTimestamp();

        Func<Task> act = sync
            ? () => Task.Run(() => proxy.Get(CancellationToken.None))
            : async () => await proxy.GetAsync(CancellationToken.None);

        var thrown = await act.Should().ThrowExactlyAsync<ResilienceException>();
        thrown.Which.Policy.Should().Be(ResiliencePolicy.Retry);
        thrown.Which.InnerException.Should().BeOfType<InvalidOperationException>();
        Stopwatch.GetElapsedTime(started).Should().BeLessThan(TimeSpan.FromSeconds(5));
        inner.Calls.Should().Be(1);
    }

    [Theory(Timeout = 10_000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Result_TimeoutDuringBackoff_ReturnsFailure(bool breaker, bool sync)
    {
        var inner = new FailingBackoffApi();
        using var cb = new CircuitBreakerPolicy(maxFailures: 10, resetMs: 60_000, halfOpenProbes: 1);
        var proxy = Proxy(inner, breaker, cb);
        var started = Stopwatch.GetTimestamp();

        var result = sync
            ? await Task.Run(() => proxy.GetResult(CancellationToken.None))
            : await proxy.GetResultAsync(CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("boom");
        Stopwatch.GetElapsedTime(started).Should().BeLessThan(TimeSpan.FromSeconds(5));
        inner.Calls.Should().Be(1);
    }

    [Theory(Timeout = 10_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedResult_TimeoutDuringBackoff_ReturnsRetryFailure(bool breaker)
    {
        var inner = new FailingBackoffApi();
        using var cb = new CircuitBreakerPolicy(maxFailures: 10, resetMs: 60_000, halfOpenProbes: 1);
        var proxy = Proxy(inner, breaker, cb);

        var result = await proxy.GetTypedAsync(CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.PolicyType.Should().Be(nameof(ResiliencePolicy.Retry));
        result.Error.InnerException.Should().BeOfType<InvalidOperationException>();
        inner.Calls.Should().Be(1);
    }

    // The total timeout is linked to the caller's token, so a cancelled caller also fires it. When
    // the inner call ignores the token and throws something else, the caller's cancellation must
    // still propagate, as it does without [Timeout], rather than end the loop as a timeout would.
    [Theory(Timeout = 10_000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CallerCancelledAfterFirstFailure_WithTimeout_ThrowsOperationCanceledException(bool breaker, bool sync)
    {
        using var cts = new CancellationTokenSource();
        var inner = new FailingBackoffApi { CancelOnFirstCall = cts };
        using var cb = new CircuitBreakerPolicy(maxFailures: 10, resetMs: 60_000, halfOpenProbes: 1);
        var proxy = Proxy(inner, breaker, cb);

        Func<Task> act = sync
            ? () => Task.Run(() => proxy.GetResult(cts.Token))
            : async () => await proxy.GetResultAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        inner.Calls.Should().Be(1);
    }
}
