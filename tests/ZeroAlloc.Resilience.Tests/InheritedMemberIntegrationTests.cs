using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Resilience;

namespace ZeroAlloc.Resilience.Tests;

// #169: the proxy forwards members inherited from base interfaces and default-implemented
// members to the inner service. These tests check the runtime behaviour of that forwarding.

// ── Test interfaces (generator runs on these) ─────────────────────────────────

public interface IInheritedBaseService
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);

    string Name { get; set; }
}

[Retry(MaxAttempts = 3, BackoffMs = 1)]
public interface IInheritedDerivedService : IInheritedBaseService
{
    ValueTask<int> CountAsync(CancellationToken ct);
}

public interface IDefaultMemberBase
{
    string Describe() => "base-default";

    string Category => "base-category";
}

[Retry(MaxAttempts = 2, BackoffMs = 1)]
public interface IDefaultMemberService : IDefaultMemberBase
{
    ValueTask<string> OwnDefaultAsync(CancellationToken ct) => ValueTask.FromResult("own-default");

    string Label => "own-label";
}

public sealed class BridgeMessage
{
    public string Id { get; init; } = "";
}

public interface IBridgeDispatcher<TMessage>
{
    ValueTask DispatchAsync(TMessage message, CancellationToken ct);
}

// The documented Outbox/Scheduling bridge shape: no members of its own.
[Retry(MaxAttempts = 3, BackoffMs = 1)]
public interface IBridgeMessageDispatcher : IBridgeDispatcher<BridgeMessage> { }

// ── Fake inner implementations ────────────────────────────────────────────────

public sealed class InheritedDerivedImpl : IInheritedDerivedService
{
    private int _callCount;

    public int FailTimes { get; set; }

    public string Name { get; set; } = "initial";

    public ValueTask<string> GetAsync(string id, CancellationToken ct)
    {
        _callCount++;
        if (_callCount <= FailTimes)
            throw new InvalidOperationException($"Simulated failure #{_callCount}");
        return ValueTask.FromResult($"ok:{id}");
    }

    public ValueTask<int> CountAsync(CancellationToken ct) => ValueTask.FromResult(_callCount);
}

// Overrides every default body; a call through the proxy must reach these, not the defaults.
public sealed class DefaultMemberOverrideImpl : IDefaultMemberService
{
    public string Describe() => "inner-describe";

    public string Category => "inner-category";

    public ValueTask<string> OwnDefaultAsync(CancellationToken ct) => ValueTask.FromResult("inner-own");

    public string Label => "inner-label";
}

public sealed class BridgeCallLog
{
    public int Calls { get; set; }
}

public sealed class FlakyBridgeDispatcher(BridgeCallLog log) : IBridgeMessageDispatcher
{
    public ValueTask DispatchAsync(BridgeMessage message, CancellationToken ct)
    {
        log.Calls++;
        if (log.Calls < 3)
            throw new InvalidOperationException($"Simulated failure #{log.Calls}");
        return ValueTask.CompletedTask;
    }
}

// ── Tests ──────────────────────────────────────────────────────────────────────

public class InheritedMemberIntegrationTests
{
    [Fact]
    public async Task InheritedMethod_WithInterfaceLevelRetry_Retries()
    {
        var inner = new InheritedDerivedImpl { FailTimes = 2 };
        IInheritedDerivedService proxy = new IInheritedDerivedServiceResilienceProxy(inner, new InheritedDerivedServiceResiliencePolicies());

        var result = await proxy.GetAsync("x", CancellationToken.None);

        result.Should().Be("ok:x");
        (await inner.CountAsync(CancellationToken.None)).Should().Be(3);
    }

    [Fact]
    public void InheritedProperty_ReadAndWrite_ReachInnerInstance()
    {
        var inner = new InheritedDerivedImpl { Name = "a" };
        IInheritedBaseService proxy = new IInheritedDerivedServiceResilienceProxy(inner, new InheritedDerivedServiceResiliencePolicies());

        proxy.Name.Should().Be("a");
        proxy.Name = "b";

        inner.Name.Should().Be("b");
    }

    [Fact]
    public void DefaultImplementedMethod_OverriddenByInner_ProxyReturnsInnerValue()
    {
        IDefaultMemberService proxy = new IDefaultMemberServiceResilienceProxy(
            new DefaultMemberOverrideImpl(), new DefaultMemberServiceResiliencePolicies());

        proxy.Describe().Should().Be("inner-describe", "an inherited default method is forwarded to the inner service");
    }

    [Fact]
    public async Task OwnDefaultImplementedMethod_OverriddenByInner_ProxyReturnsInnerValue()
    {
        IDefaultMemberService proxy = new IDefaultMemberServiceResilienceProxy(
            new DefaultMemberOverrideImpl(), new DefaultMemberServiceResiliencePolicies());

        (await proxy.OwnDefaultAsync(CancellationToken.None)).Should().Be("inner-own");
    }

    [Fact]
    public void DefaultImplementedProperty_OverriddenByInner_ProxyReturnsInnerValue()
    {
        IDefaultMemberService proxy = new IDefaultMemberServiceResilienceProxy(
            new DefaultMemberOverrideImpl(), new DefaultMemberServiceResiliencePolicies());

        proxy.Category.Should().Be("inner-category", "an inherited default property is forwarded to the inner service");
        proxy.Label.Should().Be("inner-label", "an own default property is forwarded to the inner service");
    }

    [Fact]
    public async Task BridgeShape_ResolvedThroughDi_RetriesInheritedDispatchAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<BridgeCallLog>();
        services.AddBridgeMessageDispatcherResilience<FlakyBridgeDispatcher>();
        await using var provider = services.BuildServiceProvider();

        var dispatcher = provider.GetRequiredService<IBridgeMessageDispatcher>();
        await dispatcher.DispatchAsync(new BridgeMessage { Id = "m1" }, CancellationToken.None);

        dispatcher.Should().BeOfType<IBridgeMessageDispatcherResilienceProxy>();
        provider.GetRequiredService<BridgeCallLog>().Calls.Should().Be(3, "two failures are retried by the interface-level [Retry]");
    }
}
