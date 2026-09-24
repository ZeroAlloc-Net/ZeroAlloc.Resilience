#pragma warning disable ZR0002 // IPolicySetInstanceLimited has no CancellationToken on purpose

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroAlloc.Resilience.Tests;

// 2.0: the proxy reads every value from its policy set at call time.

[Retry(MaxAttempts = 2, BackoffMs = 1)]
public interface IPolicySetRetryService
{
    ValueTask<string> GetAsync(CancellationToken ct);
}

[Timeout(Ms = 60_000)]
public interface IPolicySetTimeoutService
{
    ValueTask<string> GetAsync(CancellationToken ct);
}

[Retry(MaxAttempts = 2, BackoffMs = 1)]
public interface IPolicySetPerAttemptService
{
    ValueTask<string> GetAsync(CancellationToken ct);
}

[CircuitBreaker(MaxFailures = 100, ResetMs = 60_000)]
public interface IPolicySetCircuitService
{
    ValueTask<string> ReadAsync(CancellationToken ct);

    [CircuitBreaker(MaxFailures = 1, ResetMs = 60_000)]
    ValueTask<string> WriteAsync(CancellationToken ct);
}

[RateLimit(MaxPerSecond = 0, BurstSize = 1, Scope = RateLimitScope.Instance)]
public interface IPolicySetInstanceLimited
{
    string Get();
}

[RateLimit(MaxPerSecond = 0, BurstSize = 1)]
public interface IPolicySetMethodRateLimited
{
    string Read();

    [RateLimit(MaxPerSecond = 0, BurstSize = 1)]
    string Write();
}

public sealed class PolicySetImpl
    : IPolicySetRetryService, IPolicySetTimeoutService, IPolicySetPerAttemptService, IPolicySetCircuitService, IPolicySetInstanceLimited, IPolicySetMethodRateLimited
{
    public int Calls { get; private set; }
    public int FailTimes { get; init; }
    public int DelayMs { get; init; }

    public async ValueTask<string> GetAsync(CancellationToken ct)
    {
        Calls++;
        if (DelayMs > 0) await Task.Delay(DelayMs, ct);
        if (Calls <= FailTimes) throw new InvalidOperationException($"boom #{Calls}");
        return "ok";
    }

    public ValueTask<string> ReadAsync(CancellationToken ct) => ValueTask.FromResult("read");
    public ValueTask<string> WriteAsync(CancellationToken ct) => throw new InvalidOperationException("write");
    public string Get() => "ok";
    public string Read() => "read";
    public string Write() => "write";
}

public class PolicySetIntegrationTests
{
    [Fact]
    public async Task Retry_MaxAttemptsComesFromThePolicySet()
    {
        var inner = new PolicySetImpl { FailTimes = 5 };
        var policies = new PolicySetRetryServiceResiliencePolicies
        {
            Retry = new RetryPolicy(maxAttempts: 6, backoffMs: 0, jitter: false, perAttemptTimeoutMs: 0),
        };
        var proxy = new IPolicySetRetryServiceResilienceProxy(inner, policies);

        (await proxy.GetAsync(CancellationToken.None)).Should().Be("ok");
        inner.Calls.Should().Be(6);
    }

    [Fact]
    public async Task Retry_DefaultsToTheAttributeValues()
    {
        var inner = new PolicySetImpl { FailTimes = 5 };
        var proxy = new IPolicySetRetryServiceResilienceProxy(inner, new PolicySetRetryServiceResiliencePolicies());

        var act = async () => await proxy.GetAsync(CancellationToken.None);

        await act.Should().ThrowAsync<ResilienceException>();
        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Timeout_TotalComesFromThePolicySet()
    {
        var inner = new PolicySetImpl { DelayMs = 5_000 };
        var policies = new PolicySetTimeoutServiceResiliencePolicies { Timeout = new TimeoutPolicy(50) };
        var proxy = new IPolicySetTimeoutServiceResilienceProxy(inner, policies);

        var act = async () => await proxy.GetAsync(CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task PerAttemptTimeout_CanBeTurnedOnFromThePolicySet()
    {
        // The attribute has no per-attempt timeout; the policy set adds one.
        var inner = new PolicySetImpl { DelayMs = 5_000 };
        var policies = new PolicySetPerAttemptServiceResiliencePolicies
        {
            Retry = new RetryPolicy(maxAttempts: 2, backoffMs: 0, jitter: false, perAttemptTimeoutMs: 50),
        };
        var proxy = new IPolicySetPerAttemptServiceResilienceProxy(inner, policies);

        var act = async () => await proxy.GetAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<ResilienceException>())
            .WithInnerException<OperationCanceledException>();
        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task MethodLevelCircuitBreaker_HasItsOwnState()
    {
        var proxy = new IPolicySetCircuitServiceResilienceProxy(new PolicySetImpl(), new PolicySetCircuitServiceResiliencePolicies());

        var write = async () => await proxy.WriteAsync(CancellationToken.None);
        await write.Should().ThrowAsync<InvalidOperationException>();
        (await write.Should().ThrowAsync<ResilienceException>()).Which.Policy.Should().Be(ResiliencePolicy.CircuitBreaker);

        (await proxy.ReadAsync(CancellationToken.None)).Should().Be("read");
    }

    [Fact]
    public void InstanceScopedRateLimiter_GivesEachProxyItsOwnBudget()
    {
        var policies = new PolicySetInstanceLimitedResiliencePolicies();
        var first = new IPolicySetInstanceLimitedResilienceProxy(new PolicySetImpl(), policies);
        var second = new IPolicySetInstanceLimitedResilienceProxy(new PolicySetImpl(), policies);

        first.Get().Should().Be("ok");
        var firstAgain = () => first.Get();
        firstAgain.Should().Throw<ResilienceException>();

        second.Get().Should().Be("ok");
    }

    [Fact]
    public void MethodLevelRateLimiter_HasItsOwnBudget()
    {
        var proxy = new IPolicySetMethodRateLimitedResilienceProxy(new PolicySetImpl(), new PolicySetMethodRateLimitedResiliencePolicies());

        proxy.Write().Should().Be("write");
        var writeAgain = () => proxy.Write();
        writeAgain.Should().Throw<ResilienceException>();

        proxy.Read().Should().Be("read");
    }

    [Fact]
    public void NullSlot_ThrowsArgumentExceptionNamingIt()
    {
        var policies = new PolicySetRetryServiceResiliencePolicies { Retry = null! };

        var act = () => new IPolicySetRetryServiceResilienceProxy(new PolicySetImpl(), policies);

        act.Should().Throw<ArgumentException>().WithMessage("*PolicySetRetryServiceResiliencePolicies.Retry*");
    }
}
