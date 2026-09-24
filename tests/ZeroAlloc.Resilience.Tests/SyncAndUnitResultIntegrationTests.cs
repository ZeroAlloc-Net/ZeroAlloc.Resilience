using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Results;

namespace ZeroAlloc.Resilience.Tests;

// Regression coverage for #151: synchronous Result methods and UnitResult<E> follow the same
// rules as the async Result types from #141.

[Retry(MaxAttempts = 3, BackoffMs = 1)]
public interface ISyncResultRetryService
{
    Result<string> Get(string id);
    Result Do();
    Result<string, ResilienceError> GetTyped(string id);
    UnitResult<ResilienceError> Run();
    ValueTask<UnitResult<ResilienceError>> RunAsync(CancellationToken ct);
}

[RateLimit(MaxPerSecond = 1, BurstSize = 1)]
public interface ISyncResultRateLimitedService
{
    Result<string> Get(string id);
    UnitResult<ResilienceError> Run();
}

[CircuitBreaker(MaxFailures = 1, ResetMs = 60_000, HalfOpenProbes = 1)]
public interface ISyncResultCircuitService
{
    Result<string, ResilienceError> GetTyped(string id);
}

[Retry(MaxAttempts = 3, BackoffMs = 1)]
public interface IForeignUnitRetryService
{
    ValueTask<UnitResult<HttpError>> SendAsync(CancellationToken ct);
}

// A foreign error type under a policy that cannot build one keeps throwing, as before #151.
[RateLimit(MaxPerSecond = 1, BurstSize = 1)]
public interface IForeignUnitRateLimitedService
{
    ValueTask<UnitResult<HttpError>> SendAsync(CancellationToken ct);
    Result<string, HttpError> Get(string id);
}

public sealed class ThrowingSyncResultImpl : ISyncResultRetryService, ISyncResultRateLimitedService, ISyncResultCircuitService
{
    public int CallCount { get; private set; }

    private InvalidOperationException Fail() => new($"boom #{++CallCount}");

    public Result<string> Get(string id) => throw Fail();
    public Result Do() => throw Fail();
    public Result<string, ResilienceError> GetTyped(string id) => throw Fail();
    public UnitResult<ResilienceError> Run() => throw Fail();
    public ValueTask<UnitResult<ResilienceError>> RunAsync(CancellationToken ct) => throw Fail();
}

public sealed class SucceedingSyncResultImpl : ISyncResultRateLimitedService
{
    public Result<string> Get(string id) => Result<string>.Success($"ok:{id}");
    public UnitResult<ResilienceError> Run() => UnitResult<ResilienceError>.Success();
}

public sealed class ForeignUnitImpl : IForeignUnitRetryService, IForeignUnitRateLimitedService
{
    public int CallCount { get; private set; }
    public int ThrowTimes { get; init; }
    public UnitResult<HttpError> Returns { get; init; }

    public ValueTask<UnitResult<HttpError>> SendAsync(CancellationToken ct)
    {
        CallCount++;
        if (CallCount <= ThrowTimes) throw new InvalidOperationException($"boom #{CallCount}");
        return ValueTask.FromResult(Returns);
    }

    public Result<string, HttpError> Get(string id) => Result<string, HttpError>.Success(id);
}

public class SyncAndUnitResultIntegrationTests
{
    private static RetryPolicy Retry3() => new(maxAttempts: 3, backoffMs: 1, jitter: false, perAttemptTimeoutMs: 0);
    private static RateLimiter OnePerSecond() => new(maxPerSecond: 1, burstSize: 1, RateLimitScope.Shared);

    [Fact]
    public void SyncResultOfT_ExhaustedRetries_ReturnsFailure()
    {
        var inner = new ThrowingSyncResultImpl();
        var proxy = new ISyncResultRetryServiceResilienceProxy(inner, Retry3());

        var result = proxy.Get("x");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("boom #3");
        inner.CallCount.Should().Be(3);
    }

    [Fact]
    public void SyncNonGenericResult_ExhaustedRetries_ReturnsFailure()
    {
        var proxy = new ISyncResultRetryServiceResilienceProxy(new ThrowingSyncResultImpl(), Retry3());

        var result = proxy.Do();

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("boom #3");
    }

    [Fact]
    public void SyncResultOfTResilienceError_ExhaustedRetries_ReturnsResilienceError()
    {
        var proxy = new ISyncResultRetryServiceResilienceProxy(new ThrowingSyncResultImpl(), Retry3());

        var result = proxy.GetTyped("x");

        result.IsFailure.Should().BeTrue();
        result.Error.PolicyType.Should().Be("Retry");
        result.Error.InnerException.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public void SyncUnitResultOfResilienceError_ExhaustedRetries_ReturnsResilienceError()
    {
        var inner = new ThrowingSyncResultImpl();
        var proxy = new ISyncResultRetryServiceResilienceProxy(inner, Retry3());

        var result = proxy.Run();

        result.IsFailure.Should().BeTrue();
        result.Error.PolicyType.Should().Be("Retry");
        result.Error.Reason.Should().Be("boom #3");
        inner.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task AsyncUnitResultOfResilienceError_ExhaustedRetries_ReturnsResilienceError()
    {
        var proxy = new ISyncResultRetryServiceResilienceProxy(new ThrowingSyncResultImpl(), Retry3());

        var result = await proxy.RunAsync(CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.PolicyType.Should().Be("Retry");
    }

    [Fact]
    public void SyncResultOfT_RateLimitRejection_ReturnsFailure()
    {
        var proxy = new ISyncResultRateLimitedServiceResilienceProxy(new SucceedingSyncResultImpl(), OnePerSecond());

        proxy.Get("a").IsSuccess.Should().BeTrue();
        var rejected = proxy.Get("b");

        rejected.IsFailure.Should().BeTrue();
        rejected.Error.Should().Be("Rate limit exceeded.");
    }

    [Fact]
    public void SyncUnitResult_RateLimitRejection_ReturnsResilienceError()
    {
        var proxy = new ISyncResultRateLimitedServiceResilienceProxy(new SucceedingSyncResultImpl(), OnePerSecond());

        proxy.Run().IsSuccess.Should().BeTrue();
        var rejected = proxy.Run();

        rejected.IsFailure.Should().BeTrue();
        rejected.Error.PolicyType.Should().Be("RateLimit");
    }

    [Fact]
    public void SyncResult_SingleCallFailure_ThenOpenCircuit_ReturnFailures()
    {
        var inner = new ThrowingSyncResultImpl();
        var proxy = new ISyncResultCircuitServiceResilienceProxy(inner, new CircuitBreakerPolicy(1, 60_000, 1));

        var first = proxy.GetTyped("x");
        var second = proxy.GetTyped("x");

        first.Error.PolicyType.Should().Be("CircuitBreaker");
        first.Error.InnerException.Should().BeOfType<InvalidOperationException>();
        second.Error.PolicyType.Should().Be("CircuitBreaker");
        second.Error.Reason.Should().Be("Circuit breaker is open.");
        inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task ForeignUnitResult_ReturnedFailure_IsPassedThroughUnchanged()
    {
        var inner = new ForeignUnitImpl { Returns = UnitResult<HttpError>.Failure(new HttpError(429)) };
        var proxy = new IForeignUnitRetryServiceResilienceProxy(inner, Retry3());

        var result = await proxy.SendAsync(CancellationToken.None);

        result.Error.Should().Be(new HttpError(429));
        inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task ForeignUnitResult_EveryAttemptThrows_ThrowsResilienceException()
    {
        var proxy = new IForeignUnitRetryServiceResilienceProxy(new ForeignUnitImpl { ThrowTimes = 10 }, Retry3());

        var act = async () => await proxy.SendAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<ResilienceException>()).Which.Policy.Should().Be(ResiliencePolicy.Retry);
    }

    [Fact]
    public async Task ForeignError_RateLimitRejection_KeepsThrowing()
    {
        var proxy = new IForeignUnitRateLimitedServiceResilienceProxy(
            new ForeignUnitImpl { Returns = UnitResult<HttpError>.Success() }, OnePerSecond());

        (await proxy.SendAsync(CancellationToken.None)).IsSuccess.Should().BeTrue();
        var asyncAct = async () => await proxy.SendAsync(CancellationToken.None);
        var syncAct = () => proxy.Get("x");

        (await asyncAct.Should().ThrowAsync<ResilienceException>()).Which.Policy.Should().Be(ResiliencePolicy.RateLimit);
        syncAct.Should().Throw<ResilienceException>().Which.Policy.Should().Be(ResiliencePolicy.RateLimit);
    }
}
