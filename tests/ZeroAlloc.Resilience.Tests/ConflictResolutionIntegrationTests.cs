using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Resilience;

[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "HLQ006:GetEnumerator returns a reference type",
    Scope = "member", Target = "~M:ZeroAlloc.Resilience.Tests.ResilientItemsImpl.GetEnumerator~System.Collections.Generic.IEnumerator{System.Int32}",
    Justification = "IEnumerable<T> dictates the reference-type enumerator this fake implements.")]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "HLQ006:GetEnumerator returns a reference type",
    Scope = "member", Target = "~M:ZeroAlloc.Resilience.Tests.IResilientItemsResilienceProxy.GetEnumerator~System.Collections.Generic.IEnumerator{System.Int32}",
    Justification = "IEnumerable<T> dictates the reference-type enumerator the generated proxy forwards.")]

namespace ZeroAlloc.Resilience.Tests;

// Conflicting declarations of one name are resolved with explicit interface implementations, so
// each declaration reaches the inner service's own implementation of it.

// ── Test interfaces (generator runs on these) ─────────────────────────────────

[Retry(MaxAttempts = 3, BackoffMs = 1)]
public interface IResilientItems : IEnumerable<int> { }

public interface ITextLabel
{
    string Label { get; }

    ValueTask<string> FetchAsync(CancellationToken ct);
}

public interface INumberLabel
{
    int Label { get; }

    ValueTask<int> FetchAsync(CancellationToken ct);
}

[Retry(MaxAttempts = 3, BackoffMs = 1)]
public interface IConflictingLabels : ITextLabel, INumberLabel { }

// ── Fake inner implementations ────────────────────────────────────────────────

public sealed class ResilientItemsImpl : IResilientItems
{
    public IEnumerator<int> GetEnumerator()
    {
        yield return 1;
        yield return 2;
    }

    // A different sequence, so the test sees which enumerator was reached.
    IEnumerator IEnumerable.GetEnumerator()
    {
        yield return 10;
        yield return 20;
    }
}

public sealed class ConflictingLabelsImpl : IConflictingLabels
{
    public int NumberFetchCalls { get; private set; }

    string ITextLabel.Label => "text";

    int INumberLabel.Label => 7;

    ValueTask<string> ITextLabel.FetchAsync(CancellationToken ct) => ValueTask.FromResult("fetched");

    ValueTask<int> INumberLabel.FetchAsync(CancellationToken ct)
    {
        NumberFetchCalls++;
        if (NumberFetchCalls == 1)
            throw new InvalidOperationException("Simulated failure");
        return ValueTask.FromResult(42);
    }
}

// ── Tests ──────────────────────────────────────────────────────────────────────

public class ConflictResolutionIntegrationTests
{
    [Fact]
    public void EnumerableBase_GenericAndNonGenericEnumeration_ReachTheInnersEnumerators()
    {
        var proxy = new IResilientItemsResilienceProxy(new ResilientItemsImpl(), new ResilientItemsResiliencePolicies());

        var generic = new List<int>();
        foreach (var item in (IEnumerable<int>)proxy)
            generic.Add(item);

        var nonGeneric = new List<object>();
        foreach (var item in (IEnumerable)proxy)
            nonGeneric.Add(item);

        generic.Should().Equal(1, 2);
        nonGeneric.Should().Equal(10, 20);
    }

    [Fact]
    public void ConflictingProperties_EachDeclarationReachesItsOwnInnerImplementation()
    {
        var proxy = new IConflictingLabelsResilienceProxy(new ConflictingLabelsImpl(), new ConflictingLabelsResiliencePolicies());

        ((ITextLabel)proxy).Label.Should().Be("text");
        ((INumberLabel)proxy).Label.Should().Be(7);
    }

    [Fact]
    public async Task ConflictingPolicyMethods_ExplicitDeclaration_GetsThePolicy()
    {
        var inner = new ConflictingLabelsImpl();
        var proxy = new IConflictingLabelsResilienceProxy(inner, new ConflictingLabelsResiliencePolicies());

        (await ((ITextLabel)proxy).FetchAsync(CancellationToken.None)).Should().Be("fetched");
        (await ((INumberLabel)proxy).FetchAsync(CancellationToken.None)).Should().Be(42);
        inner.NumberFetchCalls.Should().Be(2, "the explicit implementation is wrapped in the interface-level [Retry]");
    }
}
