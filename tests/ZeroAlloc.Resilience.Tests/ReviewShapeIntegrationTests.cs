using System;
using System.Threading.Tasks;
using ZeroAlloc.Resilience;

namespace ZeroAlloc.Resilience.Tests;

// Runtime behaviour for shapes from the second 3.0 review: object members redeclared in a base
// interface, by-reference returns and ref-like parameters.

// ── Test interfaces (generator runs on these) ─────────────────────────────────

public interface IDescribable
{
    string? ToString();

    bool Equals(object? other);

    int GetHashCode();
}

public interface ISlotSource
{
    ref int Slot();
}

public interface ISpanParser
{
    ValueTask<int> ParseAsync(ReadOnlySpan<char> text) => new ValueTask<int>(-1);
}

[Retry(MaxAttempts = 2, BackoffMs = 1)]
public interface IDescribedService : IDescribable
{
    void Run();
}

public interface ISlotService : ISlotSource, ISpanParser
{
    [Retry(MaxAttempts = 2, BackoffMs = 1)]
    void Run();
}

// ── Fake inner implementations ────────────────────────────────────────────────

public sealed class DescribedServiceImpl : IDescribedService
{
    public void Run() { }

    // Reference equality, like most services.
    public override string ToString() => "inner-description";
}

public sealed class SlotServiceImpl : ISlotService
{
    private int _slot = 5;

    public ref int Slot() => ref _slot;

    public ValueTask<int> ParseAsync(ReadOnlySpan<char> text) => new ValueTask<int>(text.Length);

    public void Run() { }

    public int Current => _slot;
}

// ── Tests ──────────────────────────────────────────────────────────────────────

public class ReviewShapeIntegrationTests
{
    // A redeclared object member is implemented by object's own member on the proxy, as in 2.0.1,
    // so the proxy keeps its own reference equality.
    [Fact]
    public void RedeclaredObjectMembers_ProxyEqualsItself()
    {
        var proxy = new IDescribedServiceResilienceProxy(new DescribedServiceImpl(), new DescribedServiceResiliencePolicies());

        proxy.Equals(proxy).Should().BeTrue();
        ((IDescribable)proxy).Equals(proxy).Should().BeTrue();
    }

    [Fact]
    public void RedeclaredObjectMembers_ProxyIsFoundInAHashSet()
    {
        var proxy = new IDescribedServiceResilienceProxy(new DescribedServiceImpl(), new DescribedServiceResiliencePolicies());
        var set = new System.Collections.Generic.HashSet<IDescribedService> { proxy };

        set.Contains(proxy).Should().BeTrue();
    }

    [Fact]
    public void RefReturns_AreForwardedByReference()
    {
        var inner = new SlotServiceImpl();
        var proxy = new ISlotServiceResilienceProxy(inner, new SlotServiceResiliencePolicies());

        proxy.Slot() = 9;

        inner.Current.Should().Be(9, "the proxy returned a reference to the inner field");
    }

    [Fact]
    public async Task DefaultAsyncMethodWithSpanParameter_IsForwarded()
    {
        var proxy = new ISlotServiceResilienceProxy(new SlotServiceImpl(), new SlotServiceResiliencePolicies());

        (await ((ISpanParser)proxy).ParseAsync("abc".AsSpan())).Should().Be(3);
    }
}
