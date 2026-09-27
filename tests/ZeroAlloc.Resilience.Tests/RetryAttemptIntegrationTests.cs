using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Results;

namespace ZeroAlloc.Resilience.Tests;

// #198: a [RetryAttempt] parameter receives the retry number of the current attempt instead of
// the caller's argument: null, 1, 2 for int?, and 0, 1, 2 for int.

[Retry(MaxAttempts = 3, BackoffMs = 1)]
public interface IRetryAttemptService
{
    ValueTask<string> GetAsync(string id, [RetryAttempt] int? retryCount, CancellationToken ct);
    ValueTask SendAsync([RetryAttempt] int attempt, CancellationToken ct);
    string Get(string id, [RetryAttempt] int? retryCount);
    void Run([RetryAttempt] int attempt);
    Result<string, ResilienceError> GetTyped([RetryAttempt] int? retryCount);
}

[Retry(MaxAttempts = 3, BackoffMs = 1, RetryWhen = nameof(IsTransient))]
public interface IRetryAttemptResultService
{
    ValueTask<Result<string, HttpError>> GetAsync([RetryAttempt] int? retryCount, CancellationToken ct);
    Result<string, HttpError> Get([RetryAttempt] int attempt);

    static bool IsTransient(HttpError error) => error.StatusCode == 503;
}

// Retry combined with a total timeout and a circuit breaker: the breaker is checked once per call
// and the timeout wraps the whole loop, so the attempt number counts the attempts of the call.
[Retry(MaxAttempts = 3, BackoffMs = 1, PerAttemptTimeoutMs = 5_000)]
[Timeout(Ms = 30_000)]
[CircuitBreaker(MaxFailures = 100, ResetMs = 60_000, HalfOpenProbes = 1, Fallback = nameof(FallbackAsync))]
public interface IRetryAttemptComposedService
{
    ValueTask<string> GetAsync(string id, [RetryAttempt] int? retryCount, CancellationToken ct);
    ValueTask<string> FallbackAsync(string id, int? retryCount, CancellationToken ct);
}

// Two declarations of one method, from two base interfaces, that differ only in [RetryAttempt]:
// each keeps its own, so a call through either interface passes the attempt only where marked.
public interface IRetryAttemptBaseA
{
    ValueTask<string> GetAsync([RetryAttempt] int? retryCount, CancellationToken ct);
}

public interface IRetryAttemptBaseB
{
    ValueTask<string> GetAsync(int? retryCount, CancellationToken ct);
}

[Retry(MaxAttempts = 3, BackoffMs = 1)]
public interface IRetryAttemptDiamond : IRetryAttemptBaseA, IRetryAttemptBaseB
{
}

// Without [Retry] the attribute has no effect: the proxy passes the caller's argument. ZR0011 says
// so, and this project builds with TreatWarningsAsErrors, so the build itself proves that
// #pragma suppresses ZR0011 for the method it wraps.
[Timeout(Ms = 30_000)]
public interface IRetryAttemptWithoutRetryService
{
#pragma warning disable ZR0011 // intentional: the test asserts the argument passes through unchanged
    ValueTask<string> GetAsync([RetryAttempt] int? retryCount, CancellationToken ct);
#pragma warning restore ZR0011
}

public sealed class RetryAttemptRecorder :
    IRetryAttemptService, IRetryAttemptResultService, IRetryAttemptComposedService,
    IRetryAttemptDiamond, IRetryAttemptWithoutRetryService
{
    private readonly Lock _gate = new();
    private readonly List<int?> _seen = new();
    private readonly List<int?> _fallbackSeen = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<int?>> _seenById = new(StringComparer.Ordinal);

    // Each call fails until this many attempts have been made, counted per id.
    public int FailTimes { get; init; } = 2;

    public IReadOnlyList<int?> Seen
    {
        get { lock (_gate) return _seen.ToArray(); }
    }

    public IReadOnlyList<int?> FallbackSeen
    {
        get { lock (_gate) return _fallbackSeen.ToArray(); }
    }

    public IReadOnlyList<int?> SeenFor(string id) => _seenById[id].ToArray();

    private bool Record(string id, int? value)
    {
        lock (_gate) _seen.Add(value);
        var queue = _seenById.GetOrAdd(id, static _ => new ConcurrentQueue<int?>());
        queue.Enqueue(value);
        return queue.Count > FailTimes;
    }

    private static InvalidOperationException Fail() => new("transient");

    public async ValueTask<string> GetAsync(string id, int? retryCount, CancellationToken ct)
    {
        // Yields first, so concurrent calls interleave their attempts.
        await Task.Yield();
        return Record(id, retryCount) ? $"ok:{id}" : throw Fail();
    }

    public ValueTask SendAsync(int attempt, CancellationToken ct) =>
        Record("send", attempt) ? ValueTask.CompletedTask : throw Fail();

    public string Get(string id, int? retryCount) => Record(id, retryCount) ? "ok" : throw Fail();

    public void Run(int attempt)
    {
        if (!Record("run", attempt)) throw Fail();
    }

    public Result<string, ResilienceError> GetTyped(int? retryCount) =>
        Record("typed", retryCount) ? Result<string, ResilienceError>.Success("ok") : throw Fail();

    // IRetryAttemptResultService: the first failures come back as a transient failed Result.
    ValueTask<Result<string, HttpError>> IRetryAttemptResultService.GetAsync(int? retryCount, CancellationToken ct) =>
        ValueTask.FromResult(Record("result-async", retryCount)
            ? Result<string, HttpError>.Success("ok")
            : Result<string, HttpError>.Failure(new HttpError(503)));

    Result<string, HttpError> IRetryAttemptResultService.Get(int attempt) =>
        Record("result-sync", attempt)
            ? Result<string, HttpError>.Success("ok")
            : Result<string, HttpError>.Failure(new HttpError(503));

    public ValueTask<string> FallbackAsync(string id, int? retryCount, CancellationToken ct)
    {
        lock (_gate) _fallbackSeen.Add(retryCount);
        return ValueTask.FromResult("fallback");
    }

    ValueTask<string> IRetryAttemptBaseA.GetAsync(int? retryCount, CancellationToken ct) =>
        Record("a", retryCount) ? ValueTask.FromResult("a") : throw Fail();

    ValueTask<string> IRetryAttemptBaseB.GetAsync(int? retryCount, CancellationToken ct) =>
        Record("b", retryCount) ? ValueTask.FromResult("b") : throw Fail();

    ValueTask<string> IRetryAttemptWithoutRetryService.GetAsync(int? retryCount, CancellationToken ct) =>
        Record("no-retry", retryCount) ? ValueTask.FromResult("ok") : throw Fail();
}

public class RetryAttemptIntegrationTests
{
    private static RetryAttemptServiceResiliencePolicies Policies() => new();

    [Fact]
    public async Task AsyncNullableInt_InnerSeesNullThenOneThenTwo()
    {
        var inner = new RetryAttemptRecorder();
        var proxy = new IRetryAttemptServiceResilienceProxy(inner, Policies());

        var result = await proxy.GetAsync("x", retryCount: 42, CancellationToken.None);

        result.Should().Be("ok:x");
        inner.Seen.Should().Equal(null, 1, 2);
    }

    [Fact]
    public async Task AsyncInt_InnerSeesZeroThenOneThenTwo()
    {
        var inner = new RetryAttemptRecorder();
        var proxy = new IRetryAttemptServiceResilienceProxy(inner, Policies());

        await proxy.SendAsync(attempt: 42, CancellationToken.None);

        inner.Seen.Should().Equal(0, 1, 2);
    }

    [Fact]
    public void SyncNullableInt_InnerSeesNullThenOneThenTwo()
    {
        var inner = new RetryAttemptRecorder();
        var proxy = new IRetryAttemptServiceResilienceProxy(inner, Policies());

        proxy.Get("x", retryCount: 42).Should().Be("ok");

        inner.Seen.Should().Equal(null, 1, 2);
    }

    [Fact]
    public void SyncVoidInt_InnerSeesZeroThenOneThenTwo()
    {
        var inner = new RetryAttemptRecorder();
        var proxy = new IRetryAttemptServiceResilienceProxy(inner, Policies());

        proxy.Run(attempt: 42);

        inner.Seen.Should().Equal(0, 1, 2);
    }

    [Fact]
    public void SyncResultOfResilienceError_InnerSeesNullThenOneThenTwo()
    {
        var inner = new RetryAttemptRecorder();
        var proxy = new IRetryAttemptServiceResilienceProxy(inner, Policies());

        proxy.GetTyped(retryCount: 42).IsSuccess.Should().BeTrue();

        inner.Seen.Should().Equal(null, 1, 2);
    }

    [Fact]
    public void EveryAttemptFails_InnerSeesEveryRetryNumberOnce()
    {
        var inner = new RetryAttemptRecorder { FailTimes = int.MaxValue };
        var proxy = new IRetryAttemptServiceResilienceProxy(inner, Policies());

        proxy.GetTyped(retryCount: null).IsFailure.Should().BeTrue();

        inner.Seen.Should().Equal(null, 1, 2);
    }

    [Fact]
    public async Task ResultAwareRetry_Async_InnerSeesNullThenOneThenTwo()
    {
        var inner = new RetryAttemptRecorder();
        IRetryAttemptResultService proxy = new IRetryAttemptResultServiceResilienceProxy(inner, new RetryAttemptResultServiceResiliencePolicies());

        var result = await proxy.GetAsync(retryCount: 42, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        inner.Seen.Should().Equal(null, 1, 2);
    }

    [Fact]
    public void ResultAwareRetry_Sync_InnerSeesZeroThenOneThenTwo()
    {
        var inner = new RetryAttemptRecorder();
        IRetryAttemptResultService proxy = new IRetryAttemptResultServiceResilienceProxy(inner, new RetryAttemptResultServiceResiliencePolicies());

        proxy.Get(attempt: 42).IsSuccess.Should().BeTrue();

        inner.Seen.Should().Equal(0, 1, 2);
    }

    [Fact]
    public async Task ConcurrentCalls_EachCountsItsOwnAttempts()
    {
        var inner = new RetryAttemptRecorder();
        var proxy = new IRetryAttemptServiceResilienceProxy(inner, Policies());
        var ids = Enumerable.Range(0, 32).Select(static i => $"call-{i}").ToArray();

        var results = await Task.WhenAll(ids.Select(id => proxy.GetAsync(id, retryCount: 7, CancellationToken.None).AsTask()));

        results.Should().Equal(ids.Select(static id => $"ok:{id}"));
        foreach (var id in ids)
            inner.SeenFor(id).Should().Equal(new int?[] { null, 1, 2 }, $"call {id} counts only its own attempts");
    }

    [Fact]
    public async Task WithTimeoutAndCircuitBreaker_CountsTheAttemptsOfTheCall()
    {
        var inner = new RetryAttemptRecorder();
        var proxy = new IRetryAttemptComposedServiceResilienceProxy(inner, new RetryAttemptComposedServiceResiliencePolicies());

        var result = await proxy.GetAsync("x", retryCount: 42, CancellationToken.None);

        result.Should().Be("ok:x");
        inner.Seen.Should().Equal(null, 1, 2);
    }

    [Fact]
    public async Task OpenCircuit_FallbackGetsTheFirstAttemptValue_NotTheCallersArgument()
    {
        var inner = new RetryAttemptRecorder();
        var breaker = new CircuitBreakerPolicy(maxFailures: 1, resetMs: 60_000, halfOpenProbes: 1);
        breaker.OnFailure();
        var proxy = new IRetryAttemptComposedServiceResilienceProxy(inner,
            new RetryAttemptComposedServiceResiliencePolicies { CircuitBreaker = breaker });

        var result = await proxy.GetAsync("x", retryCount: 42, CancellationToken.None);

        result.Should().Be("fallback");
        inner.Seen.Should().BeEmpty();
        inner.FallbackSeen.Should().Equal(new int?[] { null });
    }

    [Fact]
    public async Task CollapsedDeclarations_KeepTheirOwnRetryAttempt()
    {
        var inner = new RetryAttemptRecorder();
        var proxy = new IRetryAttemptDiamondResilienceProxy(inner, new RetryAttemptDiamondResiliencePolicies());

        (await ((IRetryAttemptBaseA)proxy).GetAsync(retryCount: 42, CancellationToken.None)).Should().Be("a");
        (await ((IRetryAttemptBaseB)proxy).GetAsync(retryCount: 42, CancellationToken.None)).Should().Be("b");

        inner.SeenFor("a").Should().Equal(new int?[] { null, 1, 2 });
        inner.SeenFor("b").Should().Equal(new int?[] { 42, 42, 42 });
    }

    [Fact]
    public async Task WithoutRetry_PassesTheCallersArgumentUnchanged()
    {
        var inner = new RetryAttemptRecorder { FailTimes = 0 };
        IRetryAttemptWithoutRetryService proxy = new IRetryAttemptWithoutRetryServiceResilienceProxy(inner, new RetryAttemptWithoutRetryServiceResiliencePolicies());

        (await proxy.GetAsync(retryCount: 42, CancellationToken.None)).Should().Be("ok");

        inner.Seen.Should().Equal(42);
    }
}
