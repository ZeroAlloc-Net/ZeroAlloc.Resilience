using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Resilience;

namespace ZeroAlloc.Resilience.Tests;

// A collapsed diamond routes each declaration to the inner service's own implementation of it,
// and by-ref and generic methods keep their modifiers and type parameters (#173).

// ── Test interfaces (generator runs on these) ─────────────────────────────────

public sealed class FooMessage { }

public sealed class BarMessage { }

public interface IMessageHandler<TMessage>
{
    string Name { get; }

    ValueTask<string> DescribeAsync(CancellationToken ct);
}

[Retry(MaxAttempts = 3, BackoffMs = 1)]
public interface IDualHandler : IMessageHandler<FooMessage>, IMessageHandler<BarMessage> { }

[Retry(MaxAttempts = 3, BackoffMs = 1)]
public interface IByRefService
{
    bool TryLookup(string key, out int value);

    void Increment(ref int counter);

    T Echo<T>(T value) where T : notnull;
}

// ── Fake inner implementations ────────────────────────────────────────────────

public sealed class DualHandlerImpl : IDualHandler
{
    public int BarDescribeCalls { get; private set; }

    string IMessageHandler<FooMessage>.Name => "foo";

    string IMessageHandler<BarMessage>.Name => "bar";

    ValueTask<string> IMessageHandler<FooMessage>.DescribeAsync(CancellationToken ct) => ValueTask.FromResult("foo-description");

    ValueTask<string> IMessageHandler<BarMessage>.DescribeAsync(CancellationToken ct)
    {
        BarDescribeCalls++;
        if (BarDescribeCalls == 1)
            throw new InvalidOperationException("Simulated failure");
        return ValueTask.FromResult("bar-description");
    }
}

public sealed class FlakyByRefImpl : IByRefService
{
    public int LookupCalls { get; private set; }

    public bool TryLookup(string key, out int value)
    {
        LookupCalls++;
        if (LookupCalls == 1)
            throw new InvalidOperationException("Simulated failure");
        value = 42;
        return true;
    }

    public void Increment(ref int counter) => counter++;

    public T Echo<T>(T value) where T : notnull => value;
}

// ── Tests ──────────────────────────────────────────────────────────────────────

public class CollapsedDiamondAndByRefIntegrationTests
{
    [Fact]
    public void CollapsedDiamondProperty_EachDeclarationReachesItsOwnInnerImplementation()
    {
        var proxy = new IDualHandlerResilienceProxy(new DualHandlerImpl(), new DualHandlerResiliencePolicies());

        ((IMessageHandler<FooMessage>)proxy).Name.Should().Be("foo");
        ((IMessageHandler<BarMessage>)proxy).Name.Should().Be("bar");
    }

    [Fact]
    public async Task CollapsedDiamondMethod_ExplicitDeclaration_GetsTheSamePolicy()
    {
        var inner = new DualHandlerImpl();
        var proxy = new IDualHandlerResilienceProxy(inner, new DualHandlerResiliencePolicies());

        (await ((IMessageHandler<FooMessage>)proxy).DescribeAsync(CancellationToken.None)).Should().Be("foo-description");
        (await ((IMessageHandler<BarMessage>)proxy).DescribeAsync(CancellationToken.None)).Should().Be("bar-description");
        inner.BarDescribeCalls.Should().Be(2, "the first call failed and the interface-level [Retry] retried it");
    }

    [Fact]
    public void OutParameter_UnderRetry_IsAssignedByTheSuccessfulAttempt()
    {
        var inner = new FlakyByRefImpl();
        var proxy = new IByRefServiceResilienceProxy(inner, new ByRefServiceResiliencePolicies());

        var found = proxy.TryLookup("k", out var value);

        found.Should().BeTrue();
        value.Should().Be(42);
        inner.LookupCalls.Should().Be(2);
    }

    [Fact]
    public void RefParameter_And_GenericMethod_AreForwarded()
    {
        var proxy = new IByRefServiceResilienceProxy(new FlakyByRefImpl(), new ByRefServiceResiliencePolicies());

        var counter = 1;
        proxy.Increment(ref counter);

        counter.Should().Be(2);
        proxy.Echo("hello").Should().Be("hello");
    }
}
