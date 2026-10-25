using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroAlloc.Resilience.Tests;

// An IAsyncEnumerable<T> method with [Retry] is retried while the failure comes before its first
// item. Once an item has reached the consumer, a failure propagates unchanged: a retry would
// replay items the consumer already has.

[Retry(MaxAttempts = 3, BackoffMs = 1, RetryOnException = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
public interface IStreamApi
{
    IAsyncEnumerable<int> StreamAsync(string id, CancellationToken ct);

    IAsyncEnumerable<int> StreamWithoutTokenAsync(string id);

    static bool IsTransient(Exception exception) => exception is not ArgumentException;

    static TimeSpan? RetryAfter(Exception exception) => exception is HintedException hinted ? TimeSpan.FromMilliseconds(hinted.RetryAfterMs) : null;
}

[Retry(MaxAttempts = 3, BackoffMs = 1, RetryOnException = nameof(IsTransient), RethrowDeclined = true)]
public interface IRethrowingStreamApi
{
    IAsyncEnumerable<int> StreamAsync(string id, CancellationToken ct);

    static bool IsTransient(Exception exception) => exception is not ArgumentException;
}

// A long backoff, so a wait that ignored the caller's token would end in ResilienceException
// only after two seconds, not in OperationCanceledException at once.
[Retry(MaxAttempts = 2, BackoffMs = 2_000)]
public interface ISlowStreamApi
{
    IAsyncEnumerable<int> StreamAsync(string id, CancellationToken ct);

    IAsyncEnumerable<int> StreamWithoutTokenAsync(string id);
}

// What one call of the inner method does: throw from the call itself, or return a stream that
// yields Items and then throws Error, or ends.
public sealed record StreamAttempt(int[] Items, Exception? Error = null, bool ThrowOnCall = false)
{
    public static StreamAttempt Failing(Exception error) => new(Array.Empty<int>(), error);
}

public sealed class ScriptedEnumerator(StreamAttempt attempt, Action onFailure) : IAsyncEnumerator<int>
{
    private int _index;

    public bool Disposed { get; private set; }

    public int Current { get; private set; }

    public ValueTask<bool> MoveNextAsync()
    {
        if (_index < attempt.Items.Length)
        {
            Current = attempt.Items[_index++];
            return ValueTask.FromResult(true);
        }
        if (attempt.Error is { } error)
        {
            onFailure();
            throw error;
        }
        return ValueTask.FromResult(false);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

public sealed class ScriptedStreamApi(params StreamAttempt[] attempts) : IStreamApi, IRethrowingStreamApi, ISlowStreamApi
{
    private readonly List<ScriptedEnumerator> _enumerators = new();

    public int Calls { get; private set; }

    public IReadOnlyList<ScriptedEnumerator> Enumerators => _enumerators;

    // Stopwatch timestamps: when each call started, and when each failure was thrown.
    public IList<long> CallStarted { get; } = new List<long>();

    public IList<long> Failed { get; } = new List<long>();

    // The token each call was given, and the token each GetAsyncEnumerator was given.
    public IList<CancellationToken> CallTokens { get; } = new List<CancellationToken>();

    public IList<CancellationToken> EnumeratorTokens { get; } = new List<CancellationToken>();

    // Cancelled just after the first failure, so the caller's cancellation lands in the backoff.
    public CancellationTokenSource? CancelOnFirstFailure { get; init; }

    public IAsyncEnumerable<int> StreamAsync(string id, CancellationToken ct) => Call(ct);

    public IAsyncEnumerable<int> StreamWithoutTokenAsync(string id) => Call(default);

    private IAsyncEnumerable<int> Call(CancellationToken ct)
    {
        CallStarted.Add(Stopwatch.GetTimestamp());
        CallTokens.Add(ct);
        var attempt = attempts[Math.Min(Calls, attempts.Length - 1)];
        Calls++;
        if (attempt.ThrowOnCall)
        {
            OnFailure();
            throw attempt.Error!;
        }
        return new Stream(this, attempt);
    }

    private void OnFailure()
    {
        Failed.Add(Stopwatch.GetTimestamp());
        if (Failed.Count == 1)
            CancelOnFirstFailure?.Cancel();
    }

    private sealed class Stream(ScriptedStreamApi owner, StreamAttempt attempt) : IAsyncEnumerable<int>
    {
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            owner.EnumeratorTokens.Add(cancellationToken);
            var enumerator = new ScriptedEnumerator(attempt, owner.OnFailure);
            owner._enumerators.Add(enumerator);
            return enumerator;
        }
    }
}

public class AsyncEnumerableIntegrationTests
{
    private static IStreamApi Proxy(ScriptedStreamApi inner) =>
        new IStreamApiResilienceProxy(inner, new StreamApiResiliencePolicies());

    private static async Task<List<int>> Collect(IAsyncEnumerable<int> stream, CancellationToken ct = default)
    {
        var items = new List<int>();
        await foreach (var item in stream.WithCancellation(ct))
            items.Add(item);
        return items;
    }

    private static StreamAttempt Yields(params int[] items) => new(items);

    private static ArgumentException Permanent(string id) => new("permanent", nameof(id));

    // Every Calls assertion: Red: call the inner method, and GetAsyncEnumerator, once more after
    // the attempt loop; or, where the loop ends by throwing, twice per attempt.

    [Fact]
    public async Task A_stream_that_throws_before_its_first_item_is_retried()
    {
        var inner = new ScriptedStreamApi(
            StreamAttempt.Failing(new HttpRequestException("1")),
            StreamAttempt.Failing(new HttpRequestException("2")),
            Yields(1, 2, 3));

        var items = await Collect(Proxy(inner).StreamAsync("a", CancellationToken.None));

        // Red: treat the stream as passthrough, and the first failure reaches the consumer; or
        // drop the yield of the first item.
        items.Should().Equal(1, 2, 3);
        inner.Calls.Should().Be(3);
    }

    [Fact]
    public async Task A_stream_whose_call_throws_is_retried()
    {
        var inner = new ScriptedStreamApi(
            new StreamAttempt(Array.Empty<int>(), new HttpRequestException("call"), ThrowOnCall: true),
            Yields(7));

        var items = await Collect(Proxy(inner).StreamAsync("a", CancellationToken.None));

        // Red: make the inner call before the try, and its exception reaches the consumer.
        items.Should().Equal(7);
        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task A_stream_that_throws_after_its_first_item_is_not_retried()
    {
        var error = new HttpRequestException("mid-stream");
        var inner = new ScriptedStreamApi(new StreamAttempt(new[] { 1 }, error), Yields(9, 9, 9));
        var received = new List<int>();

        var act = async () =>
        {
            await foreach (var item in Proxy(inner).StreamAsync("a", CancellationToken.None))
                received.Add(item);
        };

        // Red: drain the inner stream inside the attempt's try, so every MoveNextAsync is caught.
        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        // Red: wrap a failure after the first item in a new HttpRequestException.
        thrown.Which.Should().BeSameAs(error);
        // Red: drop the yield of the first item.
        received.Should().Equal(1);
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task An_empty_stream_ends_without_a_retry()
    {
        var inner = new ScriptedStreamApi(Yields(), Yields(5));

        var items = await Collect(Proxy(inner).StreamAsync("a", CancellationToken.None));

        // Red: throw when the first MoveNextAsync returns false, so an empty stream is retried.
        items.Should().BeEmpty();
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Exhausted_retries_throw_ResilienceException_with_the_last_failure()
    {
        var last = new HttpRequestException("3");
        var inner = new ScriptedStreamApi(
            StreamAttempt.Failing(new HttpRequestException("1")),
            StreamAttempt.Failing(new HttpRequestException("2")),
            StreamAttempt.Failing(last));

        var act = () => Collect(Proxy(inner).StreamAsync("a", CancellationToken.None));

        // Red: throw `__lastEx` in place of the exhaustion's ResilienceException.
        var thrown = await act.Should().ThrowExactlyAsync<ResilienceException>();
        // Red: pass ResiliencePolicy.Timeout to the exhaustion's ResilienceException.
        thrown.Which.Policy.Should().Be(ResiliencePolicy.Retry);
        // Red: pass `null` in place of `__lastEx` to the exhaustion's ResilienceException.
        thrown.Which.InnerException.Should().BeSameAs(last);
        inner.Calls.Should().Be(3);
    }

    [Fact]
    public async Task RetryOnException_declining_ends_the_retries_of_a_stream()
    {
        var declined = Permanent("a");
        var inner = new ScriptedStreamApi(StreamAttempt.Failing(declined), Yields(1));

        var act = () => Collect(Proxy(inner).StreamAsync("a", CancellationToken.None));

        // Without RethrowDeclined, a declined exception goes through exhaustion, as for a Task.
        // Red: drop the RetryOnException check from the stream's catch.
        var thrown = await act.Should().ThrowExactlyAsync<ResilienceException>();
        // Red: pass `null` in place of `__lastEx` to the exhaustion's ResilienceException.
        thrown.Which.InnerException.Should().BeSameAs(declined);
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task RethrowDeclined_rethrows_the_declined_exception_of_a_stream()
    {
        var declined = Permanent("a");
        var inner = new ScriptedStreamApi(StreamAttempt.Failing(declined), StreamAttempt.Failing(declined), Yields(1));
        IRethrowingStreamApi proxy = new IRethrowingStreamApiResilienceProxy(inner, new RethrowingStreamApiResiliencePolicies());

        var act = () => Collect(proxy.StreamAsync("a", CancellationToken.None));

        // Red: emit `break;` in place of `throw;` for RethrowDeclined.
        var thrown = await act.Should().ThrowExactlyAsync<ArgumentException>();
        // Red: throw a new ArgumentException wrapping the declined one in place of `throw;`.
        thrown.Which.Should().BeSameAs(declined);
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task DelayHint_from_the_exception_sets_the_wait()
    {
        var inner = new ScriptedStreamApi(
            StreamAttempt.Failing(new HintedException(50)),
            Yields(1));

        var items = await Collect(Proxy(inner).StreamAsync("a", CancellationToken.None));

        // Red: drop the yield of the first item.
        items.Should().Equal(1);
        inner.CallStarted.Should().HaveCount(2);
        // BackoffMs is 1, so only the hint makes the wait 50 ms.
        // Red: wait `_retry.GetBackoffMs(__attempt)`, ignoring the hint.
        Stopwatch.GetElapsedTime(inner.Failed[0], inner.CallStarted[1])
            .Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(50));
    }

    // Two failures, so a proxy that called the inner method twice per attempt still fails, and
    // cancels, on its first attempt.
    private static ScriptedStreamApi CancelledInBackoff(CancellationTokenSource cts) =>
        new(StreamAttempt.Failing(new HttpRequestException("1")), StreamAttempt.Failing(new HttpRequestException("2")), Yields(1))
        {
            CancelOnFirstFailure = cts,
        };

    private static ISlowStreamApi SlowProxy(ScriptedStreamApi inner) =>
        new ISlowStreamApiResilienceProxy(inner, new SlowStreamApiResiliencePolicies());

    [Fact]
    public async Task Caller_cancellation_during_backoff_throws_OperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        var inner = CancelledInBackoff(cts);

        var act = () => Collect(SlowProxy(inner).StreamAsync("a", cts.Token));

        // Red: wait with `Task.Delay(delay)`, without the token: the wait runs out and attempt 2
        // fails again, so the stream ends in ResilienceException.
        await act.Should().ThrowAsync<OperationCanceledException>();
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task The_enumerator_token_cancels_the_backoff_too()
    {
        using var cts = new CancellationTokenSource();
        var inner = CancelledInBackoff(cts);

        // The method's token cannot be cancelled; only WithCancellation's can.
        var act = () => Collect(SlowProxy(inner).StreamAsync("a", CancellationToken.None), cts.Token);

        // Red: move [EnumeratorCancellation] from the iterator's __ct to an added, unused
        // parameter. Dropping it alone fails the build with CS8425.
        await act.Should().ThrowAsync<OperationCanceledException>();
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task The_enumerator_token_cancels_a_stream_method_without_a_token()
    {
        using var cts = new CancellationTokenSource();
        var inner = CancelledInBackoff(cts);

        var act = () => Collect(SlowProxy(inner).StreamWithoutTokenAsync("a"), cts.Token);

        // Red: move [EnumeratorCancellation] from the appended __ct to an added, unused parameter.
        await act.Should().ThrowAsync<OperationCanceledException>();
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task The_caller_token_reaches_the_inner_call_and_its_enumerator()
    {
        using var cts = new CancellationTokenSource();
        var inner = new ScriptedStreamApi(StreamAttempt.Failing(new HttpRequestException("1")), Yields(1));

        await Collect(Proxy(inner).StreamAsync("a", cts.Token));

        // Red: pass `default` in place of `__ct` to GetAsyncEnumerator.
        inner.EnumeratorTokens.Should().AllSatisfy(token => token.Should().Be(cts.Token));
        // Red: pass `default` in place of `__ct` as the inner call's token argument.
        inner.CallTokens.Should().AllSatisfy(token => token.Should().Be(cts.Token));
        inner.CallTokens.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_enumerator_of_a_failed_attempt_is_disposed()
    {
        var inner = new ScriptedStreamApi(
            StreamAttempt.Failing(new HttpRequestException("1")),
            StreamAttempt.Failing(new HttpRequestException("2")),
            Yields(1, 2, 3));

        await Collect(Proxy(inner).StreamAsync("a", CancellationToken.None));

        // Red: call the inner method twice per attempt, enumerating only the second.
        inner.Enumerators.Should().HaveCount(3);
        // Red: drop the DisposeAsync of the failed attempt's enumerator from the catch.
        inner.Enumerators[0].Disposed.Should().BeTrue();
        inner.Enumerators[1].Disposed.Should().BeTrue();
        // Red: drop the DisposeAsync from the finally around the yields.
        inner.Enumerators[2].Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task The_enumerator_is_disposed_when_the_consumer_stops_early()
    {
        var inner = new ScriptedStreamApi(Yields(1, 2, 3));

        await foreach (var item in Proxy(inner).StreamAsync("a", CancellationToken.None))
        {
            if (item == 1) break;
        }

        // ContainSingle: Red as the Calls assertions, once more after the loop. Disposed: Red:
        // drop the DisposeAsync from the finally around the yields.
        inner.Enumerators.Should().ContainSingle().Which.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task A_stream_method_without_a_token_is_retried()
    {
        var inner = new ScriptedStreamApi(StreamAttempt.Failing(new HttpRequestException("1")), Yields(4));

        var items = await Collect(Proxy(inner).StreamWithoutTokenAsync("a"));

        // Red: drop the yield of the first item.
        items.Should().Equal(4);
        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task A_runtime_RetryPolicy_drives_the_stream_retry()
    {
        // The values a generated REST client passes at runtime; perAttemptTimeoutMs 0 is unset.
        var policies = new StreamApiResiliencePolicies
        {
            Retry = new RetryPolicy(maxAttempts: 2, backoffMs: 1, jitter: false, perAttemptTimeoutMs: 0, maxDelayMs: 60_000),
        };
        var inner = new ScriptedStreamApi(
            StreamAttempt.Failing(new HttpRequestException("1")),
            StreamAttempt.Failing(new HttpRequestException("2")),
            Yields(1));
        IStreamApi proxy = new IStreamApiResilienceProxy(inner, policies);

        var act = () => Collect(proxy.StreamAsync("a", CancellationToken.None));

        // The attribute says 3 attempts, which would reach Yields(1); the runtime policy says 2.
        // Red: read the attribute's MaxAttempts, a literal 3, in place of `_retry.MaxAttempts` in
        // the stream loop and its last-attempt check.
        await act.Should().ThrowExactlyAsync<ResilienceException>();
        inner.Calls.Should().Be(2);
    }
}
