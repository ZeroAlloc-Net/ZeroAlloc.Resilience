using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Results;

namespace ZeroAlloc.Resilience.Tests;

// #142 and #143: a failed Result that RetryWhen calls transient is retried, and DelayHint takes
// the wait from the failure. The tests that prove a hint set the wait use a backoff of
// RetryPolicy.MaxBackoffMs and a 10 s test timeout, so a wait the hint did not set fails the test
// through the timeout rather than a wall-clock threshold.

[StructLayout(LayoutKind.Auto)]
public readonly record struct ApiError(int Status, int RetryAfterMs = -1, int Attempt = 0);

public sealed class HintedException(int retryAfterMs) : Exception("hinted")
{
    public int RetryAfterMs { get; } = retryAfterMs;
}

// 429 and 503 are transient; 418 makes the predicate itself throw.
public static class ApiRetryRules
{
    public static bool IsTransient(ApiError error) => error.Status switch
    {
        418 => throw new InvalidOperationException("RetryWhen threw"),
        429 or 503 => true,
        _ => false,
    };

    public static bool IsTransientException(Exception exception) => exception is not ArgumentException;

    public static TimeSpan? RetryAfter(ApiError error) =>
        error.RetryAfterMs >= 0 ? TimeSpan.FromMilliseconds(error.RetryAfterMs) : null;

    public static TimeSpan? RetryAfter(Exception exception) =>
        exception is HintedException hinted ? TimeSpan.FromMilliseconds(hinted.RetryAfterMs) : null;
}

[Retry(MaxAttempts = 3, BackoffMs = 10_000, RetryWhen = nameof(IsTransient),
       RetryOnException = nameof(IsTransientException), DelayHint = nameof(RetryAfter))]
public interface IResultAwareApi
{
    ValueTask<Result<string, ApiError>> GetAsync(CancellationToken ct);
    Result<string, ApiError> Get(CancellationToken ct);
    ValueTask<UnitResult<ApiError>> SendAsync(CancellationToken ct);

    static bool IsTransient(ApiError error) => ApiRetryRules.IsTransient(error);
    static bool IsTransientException(Exception exception) => ApiRetryRules.IsTransientException(exception);
    static TimeSpan? RetryAfter(ApiError error) => ApiRetryRules.RetryAfter(error);
    static TimeSpan? RetryAfter(Exception exception) => ApiRetryRules.RetryAfter(exception);
}

[Retry(MaxAttempts = 3, BackoffMs = 1, RetryWhen = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
[Timeout(Ms = 200)]
public interface ITimedResultAwareApi
{
    ValueTask<Result<string, ApiError>> GetAsync(CancellationToken ct);
    Result<string, ApiError> Get(CancellationToken ct);

    static bool IsTransient(ApiError error) => ApiRetryRules.IsTransient(error);
    static TimeSpan? RetryAfter(ApiError error) => ApiRetryRules.RetryAfter(error);
}

// The caller-cancellation shapes: the 60 s hint and the 120 s total timeout are both far beyond
// the 10 s test timeout, so only the caller's own cancellation can end these calls in time.
[Retry(MaxAttempts = 3, BackoffMs = 1, RetryWhen = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
[Timeout(Ms = 120_000)]
public interface ICancellableTimedResultAwareApi
{
    ValueTask<Result<string, ApiError>> GetAsync(CancellationToken ct);
    Result<string, ApiError> Get(CancellationToken ct);

    static bool IsTransient(ApiError error) => ApiRetryRules.IsTransient(error);
    static TimeSpan? RetryAfter(ApiError error) => ApiRetryRules.RetryAfter(error);
}

// Each attempt times out while the caller's token stays live: a per-attempt timeout is not the
// caller's cancellation, so it is retried, and exhaustion wraps the last cancellation.
[Retry(MaxAttempts = 3, BackoffMs = 1, PerAttemptTimeoutMs = 50, RetryWhen = nameof(IsTransient))]
public interface IPerAttemptTimeoutResultAwareApi
{
    ValueTask<Result<string, ApiError>> GetAsync(CancellationToken ct);
    Result<string, ApiError> Get(CancellationToken ct);

    static bool IsTransient(ApiError error) => ApiRetryRules.IsTransient(error);
}

[Retry(MaxAttempts = 1, RetryWhen = nameof(IsTransient))]
[CircuitBreaker(MaxFailures = 2, ResetMs = 60_000, Fallback = nameof(GetFallbackAsync))]
public interface IBreakerResultAwareApi
{
    ValueTask<Result<string, ApiError>> GetAsync(CancellationToken ct);
    ValueTask<Result<string, ApiError>> GetFallbackAsync(CancellationToken ct);

    static bool IsTransient(ApiError error) => ApiRetryRules.IsTransient(error);
}

[Retry(MaxAttempts = 2, MaxDelayMs = 250)]
public interface IMaxDelayApi
{
    ValueTask<string> GetAsync(CancellationToken ct);
}

// Each call asks the script for call number n: an error to fail with, null to succeed, or an
// exception it throws.
public sealed class ScriptedApi(Func<int, ApiError?> script)
    : IResultAwareApi, ITimedResultAwareApi, IBreakerResultAwareApi, ICancellableTimedResultAwareApi
{
    public int Calls { get; private set; }

    private Result<string, ApiError> Next()
    {
        Calls++;
        return script(Calls) is { } error
            ? Result<string, ApiError>.Failure(error with { Attempt = Calls })
            : Result<string, ApiError>.Success("ok");
    }

    public ValueTask<Result<string, ApiError>> GetAsync(CancellationToken ct) => ValueTask.FromResult(Next());

    public Result<string, ApiError> Get(CancellationToken ct) => Next();

    public ValueTask<UnitResult<ApiError>> SendAsync(CancellationToken ct)
    {
        var result = Next();
        return ValueTask.FromResult(result.IsSuccess ? UnitResult<ApiError>.Success() : UnitResult<ApiError>.Failure(result.Error));
    }

    public ValueTask<Result<string, ApiError>> GetFallbackAsync(CancellationToken ct) =>
        ValueTask.FromResult(Result<string, ApiError>.Success("fallback"));
}

// Every call outlasts the per-attempt timeout and ends in OperationCanceledException.
public sealed class SlowResultApi : IPerAttemptTimeoutResultAwareApi
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public async ValueTask<Result<string, ApiError>> GetAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        return Result<string, ApiError>.Success("late");
    }

    public Result<string, ApiError> Get(CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
        ct.ThrowIfCancellationRequested();
        return Result<string, ApiError>.Success("late");
    }
}

public class ResultAwareRetryIntegrationTests
{
    private static ApiError? FailFirst(int call, ApiError error) => call == 1 ? error : (ApiError?)null;

    private static IResultAwareApi Proxy(ScriptedApi inner, RetryPolicy? retry = null) =>
        new IResultAwareApiResilienceProxy(inner, retry is null
            ? new ResultAwareApiResiliencePolicies()
            : new ResultAwareApiResiliencePolicies { Retry = retry });

    [Fact]
    public async Task TransientFailureThenSuccess_TakesTwoAttempts()
    {
        var inner = new ScriptedApi(call => FailFirst(call, new ApiError(429, RetryAfterMs: 0)));

        var result = await Proxy(inner).GetAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task NonTransientFailure_IsReturnedAfterOneAttempt()
    {
        var inner = new ScriptedApi(static _ => new ApiError(422));

        var result = await Proxy(inner).GetAsync(CancellationToken.None);

        result.Error.Should().Be(new ApiError(422, Attempt: 1));
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task EveryAttemptTransient_ReturnsTheLastFailedResultUnchanged()
    {
        var inner = new ScriptedApi(static _ => new ApiError(429, RetryAfterMs: 0));

        var result = await Proxy(inner).GetAsync(CancellationToken.None);

        result.Error.Should().Be(new ApiError(429, RetryAfterMs: 0, Attempt: 3));
        inner.Calls.Should().Be(3);
    }

    [Fact]
    public async Task ThrowingRetryWhen_Propagates_AndIsNotRetried()
    {
        var inner = new ScriptedApi(static _ => new ApiError(418));

        var act = async () => await Proxy(inner).GetAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("RetryWhen threw");
        inner.Calls.Should().Be(1);
    }

    // The longest backoff there is: only a hint can make the wait short enough for the timeout.
    private static RetryPolicy EndlessBackoff(int maxDelayMs = RetryPolicy.MaxBackoffMs) =>
        new(3, RetryPolicy.MaxBackoffMs, false, 0, maxDelayMs);

    [Fact(Timeout = 10_000)]
    public async Task Hint_OverridesTheBackoff()
    {
        var inner = new ScriptedApi(call => FailFirst(call, new ApiError(429, RetryAfterMs: 1)));

        var result = await Proxy(inner, EndlessBackoff()).GetAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        inner.Calls.Should().Be(2);
    }

    [Fact(Timeout = 10_000)]
    public async Task Hint_IsCappedByMaxDelayMs()
    {
        var inner = new ScriptedApi(call => FailFirst(call, new ApiError(429, RetryAfterMs: 60_000)));

        var result = await Proxy(inner, EndlessBackoff(maxDelayMs: 20)).GetAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        inner.Calls.Should().Be(2);
    }

    [Fact(Timeout = 10_000)]
    public async Task ExceptionHint_IsUsedOnTheExceptionPath()
    {
        var inner = new ScriptedApi(static call => call == 1 ? throw new HintedException(1) : (ApiError?)null);

        var result = await Proxy(inner, EndlessBackoff()).GetAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        inner.Calls.Should().Be(2);
    }

    // Mixed sequences: the outcome follows the last attempt, whichever path the earlier ones took.

    [Fact(Timeout = 10_000)]
    public async Task TransientFailures_ThenAThrowOnTheLastAttempt_ThrowsResilienceException()
    {
        var inner = new ScriptedApi(static call =>
            call < 3 ? new ApiError(429, RetryAfterMs: 0) : throw new InvalidOperationException("last"));

        var act = async () => await Proxy(inner).GetAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<ResilienceException>()).WithInnerException<InvalidOperationException>();
        inner.Calls.Should().Be(3);
    }

    [Fact(Timeout = 10_000)]
    public async Task Throws_ThenATransientFailureOnTheLastAttempt_ReturnsThatResult()
    {
        var inner = new ScriptedApi(static call =>
            call < 3 ? throw new HintedException(0) : new ApiError(429, RetryAfterMs: 0));

        var result = await Proxy(inner).GetAsync(CancellationToken.None);

        result.Error.Should().Be(new ApiError(429, RetryAfterMs: 0, Attempt: 3));
        inner.Calls.Should().Be(3);
    }

    [Fact(Timeout = 10_000)]
    public async Task TransientFailure_ThenADeclinedException_ThrowsResilienceException()
    {
        var inner = new ScriptedApi(static call =>
            call == 1 ? new ApiError(429, RetryAfterMs: 0) : throw new ArgumentException("permanent", nameof(call)));

        var act = async () => await Proxy(inner).GetAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<ResilienceException>()).WithInnerException<ArgumentException>();
        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task RetryOnExceptionFalse_StopsRetrying()
    {
        var inner = new ScriptedApi(static call => throw new ArgumentException("permanent", nameof(call)));

        var act = async () => await Proxy(inner).GetAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<ResilienceException>()).WithInnerException<ArgumentException>();
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Breaker_OpensOnTransientFailedResults()
    {
        var inner = new ScriptedApi(static _ => new ApiError(429));
        using var cb = new CircuitBreakerPolicy(maxFailures: 2, resetMs: 60_000, halfOpenProbes: 1);
        var proxy = new IBreakerResultAwareApiResilienceProxy(inner, new BreakerResultAwareApiResiliencePolicies { CircuitBreaker = cb });

        (await proxy.GetAsync(CancellationToken.None)).Error.Status.Should().Be(429);
        (await proxy.GetAsync(CancellationToken.None)).Error.Status.Should().Be(429);
        var third = await proxy.GetAsync(CancellationToken.None);

        cb.State.Should().Be(CircuitBreakerState.Open);
        third.Value.Should().Be("fallback");
        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Breaker_StaysClosedOnNonTransientFailedResults()
    {
        var inner = new ScriptedApi(static _ => new ApiError(422));
        using var cb = new CircuitBreakerPolicy(maxFailures: 2, resetMs: 60_000, halfOpenProbes: 1);
        var proxy = new IBreakerResultAwareApiResilienceProxy(inner, new BreakerResultAwareApiResiliencePolicies { CircuitBreaker = cb });

        for (var i = 0; i < 3; i++)
            (await proxy.GetAsync(CancellationToken.None)).Error.Status.Should().Be(422);

        cb.State.Should().Be(CircuitBreakerState.Closed);
        inner.Calls.Should().Be(3);
    }

    [Fact]
    public async Task TotalTimeout_InterruptsALongHintedWait_AndReturnsTheLastResult()
    {
        var inner = new ScriptedApi(static _ => new ApiError(429, RetryAfterMs: 60_000));
        var proxy = new ITimedResultAwareApiResilienceProxy(inner, new TimedResultAwareApiResiliencePolicies());
        var started = Stopwatch.GetTimestamp();

        var result = await proxy.GetAsync(CancellationToken.None);

        result.Error.Should().Be(new ApiError(429, RetryAfterMs: 60_000, Attempt: 1));
        inner.Calls.Should().Be(1);
        Stopwatch.GetElapsedTime(started).Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Sync_TotalTimeout_InterruptsALongHintedWait_AndReturnsTheLastResult()
    {
        var inner = new ScriptedApi(static _ => new ApiError(429, RetryAfterMs: 60_000));
        var proxy = new ITimedResultAwareApiResilienceProxy(inner, new TimedResultAwareApiResiliencePolicies());
        var started = Stopwatch.GetTimestamp();

        var result = proxy.Get(CancellationToken.None);

        result.Error.Should().Be(new ApiError(429, RetryAfterMs: 60_000, Attempt: 1));
        Stopwatch.GetElapsedTime(started).Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Sync_TransientFailureThenSuccess_TakesTwoAttempts()
    {
        var inner = new ScriptedApi(call => FailFirst(call, new ApiError(503, RetryAfterMs: 0)));

        var result = Proxy(inner).Get(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task UnitResult_TransientFailureThenSuccess_TakesTwoAttempts()
    {
        var inner = new ScriptedApi(call => FailFirst(call, new ApiError(429, RetryAfterMs: 0)));

        var result = await Proxy(inner).SendAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        inner.Calls.Should().Be(2);
    }

    // The first call cancels the caller's token and then returns a transient failure with a 60 s
    // hint, so the cancellation always lands after the first attempt and before the wait.
    [Theory(Timeout = 10_000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CallerCancelledAfterATransientFailure_ThrowsOperationCanceledException(bool timed, bool sync)
    {
        using var cts = new CancellationTokenSource();
        var inner = new ScriptedApi(call =>
        {
            if (call == 1)
                cts.Cancel();
            return new ApiError(429, RetryAfterMs: 60_000);
        });

        Func<Task> act;
        if (timed)
        {
            var proxy = new ICancellableTimedResultAwareApiResilienceProxy(inner, new CancellableTimedResultAwareApiResiliencePolicies());
            act = sync ? () => Task.Run(() => proxy.Get(cts.Token)) : async () => await proxy.GetAsync(cts.Token);
        }
        else
        {
            var proxy = Proxy(inner);
            act = sync ? () => Task.Run(() => proxy.Get(cts.Token)) : async () => await proxy.GetAsync(cts.Token);
        }

        await act.Should().ThrowAsync<OperationCanceledException>();
        inner.Calls.Should().Be(1);
    }

    [Theory(Timeout = 10_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PerAttemptTimeout_IsRetried_AndExhaustionWrapsTheCancellation(bool sync)
    {
        var inner = new SlowResultApi();
        var proxy = new IPerAttemptTimeoutResultAwareApiResilienceProxy(inner, new PerAttemptTimeoutResultAwareApiResiliencePolicies());
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

    [Fact]
    public void MaxDelayMs_FromTheAttribute_IsThePolicyDefault() =>
        new MaxDelayApiResiliencePolicies().Retry.MaxDelayMs.Should().Be(250);
}
