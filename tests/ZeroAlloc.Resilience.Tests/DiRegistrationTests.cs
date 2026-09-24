#pragma warning disable ZR0002

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Resilience;

namespace ZeroAlloc.Resilience.Tests;

// Interface for DI test — needs to be at namespace level for generator
[Retry(MaxAttempts = 2, BackoffMs = 1)]
public interface IDiTestService
{
    ValueTask<string> PingAsync(CancellationToken ct);
}

public sealed class DiTestImpl : IDiTestService
{
    public ValueTask<string> PingAsync(CancellationToken ct) => ValueTask.FromResult("pong");
}

[CircuitBreaker(MaxFailures = 1, ResetMs = 60_000)]
public interface IDiFailingService
{
    ValueTask<string> GoAsync(CancellationToken ct);
}

[CircuitBreaker(MaxFailures = 100, ResetMs = 60_000)]
public interface IDiHealthyService
{
    ValueTask<string> GoAsync(CancellationToken ct);
}

public sealed class DiFailingImpl : IDiFailingService
{
    public ValueTask<string> GoAsync(CancellationToken ct) => throw new InvalidOperationException("down");
}

public sealed class DiHealthyImpl : IDiHealthyService
{
    public ValueTask<string> GoAsync(CancellationToken ct) => ValueTask.FromResult("ok");
}

// Registered as a singleton so the test can read the count; DiCountingImpl itself is transient.
public sealed class CallCounter
{
    public int Calls { get; set; }
}

public sealed class DiCountingImpl(CallCounter counter) : IDiTestService
{
    public ValueTask<string> PingAsync(CancellationToken ct)
    {
        counter.Calls++;
        throw new InvalidOperationException("fail");
    }
}

public class DiRegistrationTests
{
    [Fact]
    public async Task AddResilience_ResolvesProxyAndCallsInner()
    {
        var services = new ServiceCollection();
        services.AddDiTestServiceResilience<DiTestImpl>();

        await using var sp = services.BuildServiceProvider();
        var svc = sp.GetRequiredService<IDiTestService>();

        svc.Should().NotBeOfType<DiTestImpl>("proxy should wrap the impl");
        var result = await svc.PingAsync(CancellationToken.None);
        result.Should().Be("pong");
    }

    [Fact]
    public async Task TwoInterfaces_DoNotShareCircuitState()
    {
        // Before 2.0 both proxies resolved the last-registered CircuitBreakerPolicy, so the
        // failing service opened the healthy service's circuit.
        var services = new ServiceCollection();
        services.AddDiHealthyServiceResilience<DiHealthyImpl>();
        services.AddDiFailingServiceResilience<DiFailingImpl>();
        await using var sp = services.BuildServiceProvider();

        var failing = sp.GetRequiredService<IDiFailingService>();
        for (var i = 0; i < 3; i++)
        {
            try { await failing.GoAsync(CancellationToken.None); }
            catch (InvalidOperationException) { }
            catch (ResilienceException) { }
        }

        (await sp.GetRequiredService<IDiHealthyService>().GoAsync(CancellationToken.None)).Should().Be("ok");
    }

    [Fact]
    public async Task Configure_ValuesAreHonoured()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new CallCounter());
        services.AddDiTestServiceResilience<DiCountingImpl>((sp, p) =>
            p.Retry = new RetryPolicy(maxAttempts: 6, backoffMs: 0, jitter: false, perAttemptTimeoutMs: 0));
        await using var sp = services.BuildServiceProvider();

        var act = async () => await sp.GetRequiredService<IDiTestService>().PingAsync(CancellationToken.None);

        await act.Should().ThrowAsync<ResilienceException>();
        sp.GetRequiredService<CallCounter>().Calls.Should().Be(6);
    }

    [Fact]
    public async Task PreRegisteredPolicies_WinAndConfigureDoesNotRun()
    {
        var services = new ServiceCollection();
        var mine = new DiTestServiceResiliencePolicies();
        services.AddSingleton(mine);
        var configureRan = false;
        services.AddDiTestServiceResilience<DiTestImpl>((_, _) => configureRan = true);
        await using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<DiTestServiceResiliencePolicies>().Should().BeSameAs(mine);
        await sp.GetRequiredService<IDiTestService>().PingAsync(CancellationToken.None);
        configureRan.Should().BeFalse();
    }

    [Fact]
    public async Task PoliciesOnly_RegistersPoliciesButNotTheInterface()
    {
        var services = new ServiceCollection();
        services.AddDiTestServiceResiliencePolicies((_, p) => p.Retry = new RetryPolicy(5, 0, false, 0));
        await using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<DiTestServiceResiliencePolicies>().Retry.MaxAttempts.Should().Be(5);
        sp.GetService<IDiTestService>().Should().BeNull();
    }

    [Fact]
    public async Task InvalidConfiguredValue_ThrowsWhenPoliciesAreResolved()
    {
        var services = new ServiceCollection();
        services.AddDiTestServiceResilience<DiTestImpl>((_, p) => p.Retry = new RetryPolicy(0, 0, false, 0));
        await using var sp = services.BuildServiceProvider();

        var act = () => sp.GetRequiredService<IDiTestService>();

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("maxAttempts");
    }

    [Fact]
    public void Configure_Null_Throws()
    {
        var act = () => new ServiceCollection().AddDiTestServiceResilience<DiTestImpl>(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("configure");
    }

    [Fact]
    public async Task Configure_RunsOnceAcrossResolutions()
    {
        var calls = 0;
        var services = new ServiceCollection();
        services.AddDiTestServiceResilience<DiTestImpl>((_, _) => calls++);
        await using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IDiTestService>();
        sp.GetRequiredService<IDiTestService>();
        sp.GetRequiredService<IDiTestService>();

        calls.Should().Be(1);
    }
}
