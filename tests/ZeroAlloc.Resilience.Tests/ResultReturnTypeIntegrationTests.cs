using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Results;

namespace ZeroAlloc.Resilience.Tests;

// Regression coverage for #141: Result-returning methods get a failure value of their own type.

public readonly record struct HttpError(int StatusCode);

[Retry(MaxAttempts = 3, BackoffMs = 1)]
public interface IResultRetryService
{
    ValueTask<Result<string>> GetAsync(string id, CancellationToken ct);
    Task<Result<string>> GetTaskAsync(string id, CancellationToken ct);
    ValueTask<Result> DoAsync(CancellationToken ct);
    ValueTask<Result<string, ResilienceError>> GetTypedAsync(string id, CancellationToken ct);
    Task<Result<string, ResilienceError>> GetTypedTaskAsync(string id, CancellationToken ct);
}

[RateLimit(MaxPerSecond = 1, BurstSize = 1)]
public interface IResultRateLimitedService
{
    ValueTask<Result<string>> GetAsync(string id, CancellationToken ct);
    ValueTask<Result<string, ResilienceError>> GetTypedAsync(string id, CancellationToken ct);
}

[CircuitBreaker(MaxFailures = 1, ResetMs = 60_000, HalfOpenProbes = 1)]
public interface IResultCircuitService
{
    ValueTask<Result<string>> GetAsync(string id, CancellationToken ct);
}

[Retry(MaxAttempts = 3, BackoffMs = 1)]
public interface IForeignErrorService
{
    ValueTask<Result<string, HttpError>> GetAsync(string id, CancellationToken ct);
}

public sealed class ThrowingResultImpl : IResultRetryService, IResultRateLimitedService, IResultCircuitService
{
    public int CallCount { get; private set; }

    private InvalidOperationException Fail() => new($"boom #{++CallCount}");

    public ValueTask<Result<string>> GetAsync(string id, CancellationToken ct) => throw Fail();
    public Task<Result<string>> GetTaskAsync(string id, CancellationToken ct) => throw Fail();
    public ValueTask<Result> DoAsync(CancellationToken ct) => throw Fail();
    public ValueTask<Result<string, ResilienceError>> GetTypedAsync(string id, CancellationToken ct) => throw Fail();
    public Task<Result<string, ResilienceError>> GetTypedTaskAsync(string id, CancellationToken ct) => throw Fail();
}

public sealed class SucceedingResultImpl : IResultRateLimitedService
{
    public ValueTask<Result<string>> GetAsync(string id, CancellationToken ct)
        => ValueTask.FromResult(Result<string>.Success($"ok:{id}"));
    public ValueTask<Result<string, ResilienceError>> GetTypedAsync(string id, CancellationToken ct)
        => ValueTask.FromResult(Result<string, ResilienceError>.Success($"ok:{id}"));
}

public sealed class ForeignErrorImpl : IForeignErrorService
{
    public int CallCount { get; private set; }
    public int ThrowTimes { get; init; }
    public Result<string, HttpError> Returns { get; init; }

    public ValueTask<Result<string, HttpError>> GetAsync(string id, CancellationToken ct)
    {
        CallCount++;
        if (CallCount <= ThrowTimes) throw new InvalidOperationException($"boom #{CallCount}");
        return ValueTask.FromResult(Returns);
    }
}

public class ResultReturnTypeIntegrationTests
{
    private static RetryPolicy Retry3() => new(maxAttempts: 3, backoffMs: 1, jitter: false, perAttemptTimeoutMs: 0);

    [Fact]
    public async Task ResultOfT_ExhaustedRetries_ReturnsFailure_WithLastExceptionMessage()
    {
        var inner = new ThrowingResultImpl();
        var proxy = new IResultRetryServiceResilienceProxy(inner, new ResultRetryServiceResiliencePolicies { Retry = Retry3() });

        var result = await proxy.GetAsync("x", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("boom #3");
        inner.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task TaskOfResultOfT_ExhaustedRetries_ReturnsFailure()
    {
        var inner = new ThrowingResultImpl();
        var proxy = new IResultRetryServiceResilienceProxy(inner, new ResultRetryServiceResiliencePolicies { Retry = Retry3() });

        var result = await proxy.GetTaskAsync("x", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("boom #3");
    }

    [Fact]
    public async Task NonGenericResult_ExhaustedRetries_ReturnsFailure()
    {
        var inner = new ThrowingResultImpl();
        var proxy = new IResultRetryServiceResilienceProxy(inner, new ResultRetryServiceResiliencePolicies { Retry = Retry3() });

        var result = await proxy.DoAsync(CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("boom #3");
    }

    [Fact]
    public async Task ResultOfTResilienceError_ExhaustedRetries_ReturnsResilienceError()
    {
        var inner = new ThrowingResultImpl();
        var proxy = new IResultRetryServiceResilienceProxy(inner, new ResultRetryServiceResiliencePolicies { Retry = Retry3() });

        var result = await proxy.GetTypedAsync("x", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.PolicyType.Should().Be(nameof(ResiliencePolicy.Retry));
        result.Error.Reason.Should().Be("boom #3");
        result.Error.InnerException.Should().BeOfType<InvalidOperationException>();
        inner.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task TaskOfResultOfTResilienceError_ExhaustedRetries_ReturnsResilienceError()
    {
        var inner = new ThrowingResultImpl();
        var proxy = new IResultRetryServiceResilienceProxy(inner, new ResultRetryServiceResiliencePolicies { Retry = Retry3() });

        var result = await proxy.GetTypedTaskAsync("x", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.PolicyType.Should().Be(nameof(ResiliencePolicy.Retry));
    }

    [Fact]
    public async Task ResultOfT_RateLimitRejection_ReturnsFailure()
    {
        var proxy = new IResultRateLimitedServiceResilienceProxy(
            new SucceedingResultImpl(), new ResultRateLimitedServiceResiliencePolicies { RateLimiter = new RateLimiter(maxPerSecond: 1, burstSize: 1, scope: RateLimitScope.Instance) });

        (await proxy.GetAsync("1", CancellationToken.None)).IsSuccess.Should().BeTrue();
        var rejected = await proxy.GetAsync("2", CancellationToken.None);

        rejected.IsFailure.Should().BeTrue();
        rejected.Error.Should().Be("Rate limit exceeded.");
    }

    [Fact]
    public async Task ResultOfTResilienceError_RateLimitRejection_ReturnsResilienceError()
    {
        var proxy = new IResultRateLimitedServiceResilienceProxy(
            new SucceedingResultImpl(), new ResultRateLimitedServiceResiliencePolicies { RateLimiter = new RateLimiter(maxPerSecond: 1, burstSize: 1, scope: RateLimitScope.Instance) });

        (await proxy.GetTypedAsync("1", CancellationToken.None)).IsSuccess.Should().BeTrue();
        var rejected = await proxy.GetTypedAsync("2", CancellationToken.None);

        rejected.IsFailure.Should().BeTrue();
        rejected.Error.PolicyType.Should().Be(nameof(ResiliencePolicy.RateLimit));
        rejected.Error.InnerException.Should().BeNull();
    }

    [Fact]
    public async Task ResultOfT_SingleCallFailure_ThenOpenCircuit_ReturnFailures()
    {
        var inner = new ThrowingResultImpl();
        using var cb = new CircuitBreakerPolicy(maxFailures: 1, resetMs: 60_000, halfOpenProbes: 1);
        var proxy = new IResultCircuitServiceResilienceProxy(inner, new ResultCircuitServiceResiliencePolicies { CircuitBreaker = cb });

        var first = await proxy.GetAsync("x", CancellationToken.None);
        first.IsFailure.Should().BeTrue();
        first.Error.Should().Be("boom #1");

        var second = await proxy.GetAsync("x", CancellationToken.None);
        second.IsFailure.Should().BeTrue();
        second.Error.Should().Be("Circuit breaker is open.");
        inner.CallCount.Should().Be(1, "an open circuit must not call the inner service");
    }

    [Fact]
    public async Task ForeignError_ReturnedFailure_IsPassedThroughUnchanged()
    {
        var inner = new ForeignErrorImpl { Returns = Result<string, HttpError>.Failure(new HttpError(429)) };
        var proxy = new IForeignErrorServiceResilienceProxy(inner, new ForeignErrorServiceResiliencePolicies { Retry = Retry3() });

        var result = await proxy.GetAsync("x", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(new HttpError(429));
        inner.CallCount.Should().Be(1, "without RetryWhen a returned failure is passed through, not retried");
    }

    [Fact]
    public async Task ForeignError_ThrowsThenReturns_RetriesAndReturnsInnerResult()
    {
        var inner = new ForeignErrorImpl { ThrowTimes = 2, Returns = Result<string, HttpError>.Success("ok") };
        var proxy = new IForeignErrorServiceResilienceProxy(inner, new ForeignErrorServiceResiliencePolicies { Retry = Retry3() });

        var result = await proxy.GetAsync("x", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("ok");
        inner.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task ForeignError_EveryAttemptThrows_ThrowsResilienceException()
    {
        var inner = new ForeignErrorImpl { ThrowTimes = int.MaxValue };
        var proxy = new IForeignErrorServiceResilienceProxy(inner, new ForeignErrorServiceResiliencePolicies { Retry = Retry3() });

        var act = async () => await proxy.GetAsync("x", CancellationToken.None);

        (await act.Should().ThrowAsync<ResilienceException>())
            .Where(e => e.Policy == ResiliencePolicy.Retry)
            .WithInnerException<InvalidOperationException>();
        inner.CallCount.Should().Be(3);
    }
}
