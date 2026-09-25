using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Resilience;

namespace ZeroAlloc.Resilience.Tests;

// Regression coverage for #168: the generated proxy now forwards the interface's own properties
// and events in addition to methods. These tests exercise the runtime behaviour of that
// forwarding — that a property read/write and an event subscription on the proxy really do reach
// the inner instance, and that a policied method on the same interface still applies its policy.

// ── Test interface (generator runs on this) ───────────────────────────────────

[Retry(MaxAttempts = 3, BackoffMs = 1)]
public interface IWidgetService
{
    string Name { get; set; }

    event EventHandler? Updated;

    ValueTask<string> GetAsync(string id, CancellationToken ct);
}

// ── Fake inner implementation ─────────────────────────────────────────────────

public sealed class WidgetServiceImpl : IWidgetService
{
    private int _callCount;

    public string Name { get; set; } = "initial";

    public event EventHandler? Updated;

    public int FailTimes { get; set; }

    public void RaiseUpdated() => Updated?.Invoke(this, EventArgs.Empty);

    public ValueTask<string> GetAsync(string id, CancellationToken ct)
    {
        _callCount++;
        if (_callCount <= FailTimes)
            throw new InvalidOperationException($"Simulated failure #{_callCount}");
        return ValueTask.FromResult($"ok:{id}");
    }
}

// ── Tests ──────────────────────────────────────────────────────────────────────

public class InterfaceMemberIntegrationTests
{
    [Fact]
    public void Property_ReadAndWrite_ReachInnerInstance()
    {
        var inner = new WidgetServiceImpl { Name = "a" };
        var proxy = new IWidgetServiceResilienceProxy(inner, new WidgetServiceResiliencePolicies());

        proxy.Name.Should().Be("a");

        proxy.Name = "b";

        inner.Name.Should().Be("b", "a write through the proxy must reach the inner instance");
        proxy.Name.Should().Be("b", "reading back through the proxy must observe the inner instance's state");
    }

    [Fact]
    public void Event_SubscribedThroughProxy_FiresWhenInnerRaisesIt()
    {
        var inner = new WidgetServiceImpl();
        var proxy = new IWidgetServiceResilienceProxy(inner, new WidgetServiceResiliencePolicies());

        var raised = false;
        proxy.Updated += (_, _) => raised = true;

        inner.RaiseUpdated();

        raised.Should().BeTrue("subscribing through the proxy must add the handler to the inner instance's event");
    }

    [Fact]
    public async Task Method_OnAnInterfaceWithProperties_StillRetries()
    {
        var inner = new WidgetServiceImpl { FailTimes = 2 };
        var retry = new RetryPolicy(maxAttempts: 3, backoffMs: 1, jitter: false, perAttemptTimeoutMs: 0);
        var proxy = new IWidgetServiceResilienceProxy(inner, new WidgetServiceResiliencePolicies { Retry = retry });

        var result = await proxy.GetAsync("x", CancellationToken.None);

        result.Should().Be("ok:x");
    }
}
