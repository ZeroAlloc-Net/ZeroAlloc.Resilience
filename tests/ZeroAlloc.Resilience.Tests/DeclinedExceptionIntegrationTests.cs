using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Results;

namespace ZeroAlloc.Resilience.Tests;

// #195: an exception RetryOnException declines was never retried, so it is rethrown unchanged:
// the original instance with its original stack, not a ResilienceException saying every retry
// attempt failed. An exception that is retried until the attempts run out is still wrapped.

public static class DeclineRules
{
    // ArgumentException is a programming error and is not retried; anything else is.
    public static bool IsRetryable(Exception exception) => exception is not ArgumentException;
}

[Retry(MaxAttempts = 3, BackoffMs = 1, RetryOnException = nameof(IsRetryable))]
public interface IDeclineApi
{
    ValueTask<string> GetAsync(CancellationToken ct);
    string Get(CancellationToken ct);
    Task SendAsync(CancellationToken ct);
    ValueTask<Result<string, ResilienceError>> TryGetAsync(CancellationToken ct);

    static bool IsRetryable(Exception exception) => DeclineRules.IsRetryable(exception);
}

[Retry(MaxAttempts = 3, BackoffMs = 1, RetryOnException = nameof(IsRetryable))]
[CircuitBreaker(MaxFailures = 10, ResetMs = 60_000)]
[Timeout(Ms = 60_000)]
public interface IGuardedDeclineApi
{
    ValueTask<string> GetAsync(CancellationToken ct);
    string Get(CancellationToken ct);

    static bool IsRetryable(Exception exception) => DeclineRules.IsRetryable(exception);
}

// Each call throws the exception the factory builds for call number n, or succeeds on null.
public sealed class ThrowingApi(Func<int, Exception?> script) : IDeclineApi, IGuardedDeclineApi
{
    public int Calls { get; private set; }

    public Exception? LastThrown { get; private set; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private string ThrowFromInner()
    {
        Calls++;
        if (script(Calls) is { } exception)
        {
            LastThrown = exception;
            throw exception;
        }
        return "ok";
    }

    public ValueTask<string> GetAsync(CancellationToken ct) => ValueTask.FromResult(ThrowFromInner());

    public string Get(CancellationToken ct) => ThrowFromInner();

    public Task SendAsync(CancellationToken ct)
    {
        ThrowFromInner();
        return Task.CompletedTask;
    }

    public ValueTask<Result<string, ResilienceError>> TryGetAsync(CancellationToken ct) =>
        ValueTask.FromResult(Result<string, ResilienceError>.Success(ThrowFromInner()));
}

public class DeclinedExceptionIntegrationTests
{
    private static Exception Permanent(int call) => new ArgumentException("permanent", nameof(call));

    private static Exception Transient(int call) => new InvalidOperationException($"transient {call}");

    private static IDeclineApi Proxy(ThrowingApi inner) =>
        new IDeclineApiResilienceProxy(inner, new DeclineApiResiliencePolicies());

    private static IGuardedDeclineApi GuardedProxy(ThrowingApi inner) =>
        new IGuardedDeclineApiResilienceProxy(inner, new GuardedDeclineApiResiliencePolicies());

    private static void ShouldBeTheOriginal(Exception thrown, ThrowingApi inner)
    {
        thrown.Should().BeSameAs(inner.LastThrown);
        // Rethrown with `throw;`, so the frame that threw it is still on its stack.
        thrown.StackTrace.Should().Contain(nameof(ThrowingApi) + ".ThrowFromInner");
    }

    [Fact]
    public async Task DeclinedException_Async_IsRethrownUnwrapped()
    {
        var inner = new ThrowingApi(Permanent);

        var act = async () => await Proxy(inner).GetAsync(CancellationToken.None);

        var thrown = await act.Should().ThrowExactlyAsync<ArgumentException>();
        ShouldBeTheOriginal(thrown.Which, inner);
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public void DeclinedException_Sync_IsRethrownUnwrapped()
    {
        var inner = new ThrowingApi(Permanent);

        var act = () => Proxy(inner).Get(CancellationToken.None);

        var thrown = act.Should().ThrowExactly<ArgumentException>();
        ShouldBeTheOriginal(thrown.Which, inner);
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task DeclinedException_VoidTask_IsRethrownUnwrapped()
    {
        var inner = new ThrowingApi(Permanent);

        var act = async () => await Proxy(inner).SendAsync(CancellationToken.None);

        var thrown = await act.Should().ThrowExactlyAsync<ArgumentException>();
        ShouldBeTheOriginal(thrown.Which, inner);
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task DeclinedException_AfterARetriedOne_IsRethrownUnwrapped()
    {
        var inner = new ThrowingApi(static call => call == 1 ? Transient(call) : Permanent(call));

        var act = async () => await Proxy(inner).GetAsync(CancellationToken.None);

        var thrown = await act.Should().ThrowExactlyAsync<ArgumentException>();
        ShouldBeTheOriginal(thrown.Which, inner);
        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task RetriedExceptions_ThatExhaustTheAttempts_AreStillWrapped()
    {
        var inner = new ThrowingApi(Transient);

        var act = async () => await Proxy(inner).GetAsync(CancellationToken.None);

        var thrown = await act.Should().ThrowExactlyAsync<ResilienceException>();
        thrown.Which.Policy.Should().Be(ResiliencePolicy.Retry);
        thrown.Which.InnerException.Should().BeSameAs(inner.LastThrown);
        inner.Calls.Should().Be(3);
    }

    // A ResilienceError Result method returns failures instead of throwing, whatever the policy
    // does; a declined exception keeps becoming a failure that carries it.
    [Fact]
    public async Task DeclinedException_ResilienceErrorResult_IsReturnedAsAFailure()
    {
        var inner = new ThrowingApi(Permanent);

        var result = await Proxy(inner).TryGetAsync(CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.PolicyType.Should().Be("Retry");
        result.Error.InnerException.Should().BeSameAs(inner.LastThrown);
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task DeclinedException_WithBreakerAndTimeout_Async_IsRethrownUnwrapped()
    {
        var inner = new ThrowingApi(Permanent);

        var act = async () => await GuardedProxy(inner).GetAsync(CancellationToken.None);

        var thrown = await act.Should().ThrowExactlyAsync<ArgumentException>();
        ShouldBeTheOriginal(thrown.Which, inner);
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public void DeclinedException_WithBreakerAndTimeout_Sync_IsRethrownUnwrapped()
    {
        var inner = new ThrowingApi(Permanent);

        var act = () => GuardedProxy(inner).Get(CancellationToken.None);

        var thrown = act.Should().ThrowExactly<ArgumentException>();
        ShouldBeTheOriginal(thrown.Which, inner);
        inner.Calls.Should().Be(1);
    }

    // The breaker still counts a declined exception as a failure of the inner call.
    [Fact]
    public async Task DeclinedException_IsStillABreakerFailure()
    {
        var inner = new ThrowingApi(Permanent);
        var breaker = new CircuitBreakerPolicy(maxFailures: 1, resetMs: 60_000, halfOpenProbes: 1);
        var proxy = new IGuardedDeclineApiResilienceProxy(inner, new GuardedDeclineApiResiliencePolicies { CircuitBreaker = breaker });

        var first = async () => await proxy.GetAsync(CancellationToken.None);
        await first.Should().ThrowExactlyAsync<ArgumentException>();

        var second = async () => await proxy.GetAsync(CancellationToken.None);
        (await second.Should().ThrowExactlyAsync<ResilienceException>()).Which.Policy.Should().Be(ResiliencePolicy.CircuitBreaker);
        inner.Calls.Should().Be(1);
    }
}
