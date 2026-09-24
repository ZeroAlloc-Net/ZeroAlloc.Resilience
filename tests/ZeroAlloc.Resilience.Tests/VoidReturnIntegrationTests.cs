using System;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroAlloc.Resilience.Tests;

// Regression coverage for #156: void, ValueTask and Task methods run under every policy.

[Retry(MaxAttempts = 3, BackoffMs = 1)]
public interface IVoidRetryService
{
    ValueTask PostAsync(CancellationToken ct);
    Task SendAsync(CancellationToken ct);
    void Fire();
}

[CircuitBreaker(MaxFailures = 1, ResetMs = 60_000, HalfOpenProbes = 1)]
public interface IVoidCircuitService
{
    ValueTask PostAsync(CancellationToken ct);

    [CircuitBreaker(MaxFailures = 1, ResetMs = 60_000, HalfOpenProbes = 1, Fallback = nameof(FireFallback))]
    void Fire();
    void FireFallback();
}

public sealed class FlakyVoidImpl : IVoidRetryService, IVoidCircuitService
{
    public int FailTimes { get; init; }
    public int CallCount { get; private set; }
    public int FallbackCount { get; private set; }

    private void Call()
    {
        CallCount++;
        if (CallCount <= FailTimes) throw new InvalidOperationException($"boom #{CallCount}");
    }

    public ValueTask PostAsync(CancellationToken ct) { Call(); return ValueTask.CompletedTask; }
    public Task SendAsync(CancellationToken ct) { Call(); return Task.CompletedTask; }
    public void Fire() => Call();
    public void FireFallback() => FallbackCount++;
}

public class VoidReturnIntegrationTests
{
    private static RetryPolicy Retry3() => new(maxAttempts: 3, backoffMs: 1, jitter: false, perAttemptTimeoutMs: 0);

    [Fact]
    public async Task ValueTask_RetriesUntilSuccess()
    {
        var inner = new FlakyVoidImpl { FailTimes = 2 };
        var proxy = new IVoidRetryServiceResilienceProxy(inner, new VoidRetryServiceResiliencePolicies { Retry = Retry3() });

        await proxy.PostAsync(CancellationToken.None);

        inner.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task Task_ExhaustedRetries_ThrowsResilienceException()
    {
        var proxy = new IVoidRetryServiceResilienceProxy(new FlakyVoidImpl { FailTimes = 10 }, new VoidRetryServiceResiliencePolicies { Retry = Retry3() });

        var act = () => proxy.SendAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<ResilienceException>()).Which.Policy.Should().Be(ResiliencePolicy.Retry);
    }

    [Fact]
    public void Void_RetriesUntilSuccess()
    {
        var inner = new FlakyVoidImpl { FailTimes = 1 };
        var proxy = new IVoidRetryServiceResilienceProxy(inner, new VoidRetryServiceResiliencePolicies { Retry = Retry3() });

        proxy.Fire();

        inner.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task ValueTask_OpenCircuit_Throws()
    {
        var inner = new FlakyVoidImpl { FailTimes = 10 };
        var proxy = new IVoidCircuitServiceResilienceProxy(inner, new VoidCircuitServiceResiliencePolicies { CircuitBreaker = new CircuitBreakerPolicy(1, 60_000, 1) });

        var first = async () => await proxy.PostAsync(CancellationToken.None);
        var second = async () => await proxy.PostAsync(CancellationToken.None);

        await first.Should().ThrowAsync<InvalidOperationException>();
        (await second.Should().ThrowAsync<ResilienceException>()).Which.Policy.Should().Be(ResiliencePolicy.CircuitBreaker);
        inner.CallCount.Should().Be(1);
    }

    [Fact]
    public void Void_OpenCircuit_CallsFallback()
    {
        var inner = new FlakyVoidImpl { FailTimes = 10 };
        var proxy = new IVoidCircuitServiceResilienceProxy(inner, new VoidCircuitServiceResiliencePolicies { FireCircuitBreaker = new CircuitBreakerPolicy(1, 60_000, 1) });

        var first = () => proxy.Fire();
        first.Should().Throw<InvalidOperationException>();
        proxy.Fire();

        inner.FallbackCount.Should().Be(1);
    }
}
