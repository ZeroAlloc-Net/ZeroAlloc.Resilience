using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Resilience;
using ZeroAlloc.TestHelpers;

namespace ZeroAlloc.Resilience.Tests;

// The happy path of every generated proxy allocates nothing, except that [Timeout] creates one
// linked CancellationTokenSource per call, as docs/performance.md and the timeout guide document.
// Ceilings are int.MaxValue so no policy trips however many calls the measurement makes.

[Retry(MaxAttempts = 3, BackoffMs = 1)]
public interface IAllocRetryService
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);
}

[CircuitBreaker(MaxFailures = 5, ResetMs = 1_000, HalfOpenProbes = 1)]
public interface IAllocCircuitService
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);
}

[CircuitBreaker(MaxFailures = 5, ResetMs = 1_000, HalfOpenProbes = 1, Fallback = nameof(GetFallback))]
public interface IAllocCircuitFallbackService
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);
    ValueTask<string> GetFallback(string id, CancellationToken ct);
}

[RateLimit(MaxPerSecond = int.MaxValue, BurstSize = int.MaxValue)]
public interface IAllocRateLimitService
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);
}

[Retry(MaxAttempts = 3, BackoffMs = 1)]
[RateLimit(MaxPerSecond = int.MaxValue, BurstSize = int.MaxValue)]
[CircuitBreaker(MaxFailures = int.MaxValue, ResetMs = 60_000, HalfOpenProbes = 1)]
public interface IAllocNoTimeoutPoliciesService
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);
}

[Timeout(Ms = 5_000)]
public interface IAllocTimeoutService
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);
}

[Retry(MaxAttempts = 3, BackoffMs = 1)]
[Timeout(Ms = 5_000)]
[RateLimit(MaxPerSecond = int.MaxValue, BurstSize = int.MaxValue)]
[CircuitBreaker(MaxFailures = int.MaxValue, ResetMs = 60_000, HalfOpenProbes = 1)]
public interface IAllocAllPoliciesService
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);
}

public sealed class AllocSucceedsImpl
    : IAllocRetryService, IAllocCircuitService, IAllocCircuitFallbackService, IAllocRateLimitService,
      IAllocNoTimeoutPoliciesService, IAllocTimeoutService, IAllocAllPoliciesService
{
    public ValueTask<string> GetAsync(string id, CancellationToken ct) => ValueTask.FromResult("ok");
    public ValueTask<string> GetFallback(string id, CancellationToken ct) => ValueTask.FromResult("fallback");
}

public class HappyPathAllocationTests
{
    private const int Iterations = 1_000;

    // One linked CancellationTokenSource plus the timer CancelAfter registers, measured at 144 B
    // on .NET 10 x64. This is the per-call cost docs/performance.md documents for [Timeout]. The
    // tests assert it exactly, so a change that shrinks it fails too and the figure gets updated.
    private const long TimeoutBytesPerCall = 144;

    private static readonly AllocSucceedsImpl Inner = new();

    [OptimizedBuildFact]
    public void Retry_FirstAttemptSucceeds_AllocatesNothing()
    {
        var proxy = new IAllocRetryServiceResilienceProxy(Inner, new AllocRetryServiceResiliencePolicies());
        AssertAllocatesNothing(() => proxy.GetAsync("x", CancellationToken.None), "[Retry] first attempt succeeds");
    }

    [OptimizedBuildFact]
    public void CircuitBreaker_Closed_AllocatesNothing()
    {
        var proxy = new IAllocCircuitServiceResilienceProxy(Inner, new AllocCircuitServiceResiliencePolicies());
        AssertAllocatesNothing(() => proxy.GetAsync("x", CancellationToken.None), "[CircuitBreaker] closed");
    }

    [OptimizedBuildFact]
    public void CircuitBreakerWithFallback_Closed_AllocatesNothing()
    {
        var proxy = new IAllocCircuitFallbackServiceResilienceProxy(Inner, new AllocCircuitFallbackServiceResiliencePolicies());
        AssertAllocatesNothing(() => proxy.GetAsync("x", CancellationToken.None), "[CircuitBreaker] with fallback, closed");
    }

    [OptimizedBuildFact]
    public void RateLimit_TokenAvailable_AllocatesNothing()
    {
        var proxy = new IAllocRateLimitServiceResilienceProxy(Inner, new AllocRateLimitServiceResiliencePolicies());
        AssertAllocatesNothing(() => proxy.GetAsync("x", CancellationToken.None), "[RateLimit] token available");
    }

    [OptimizedBuildFact]
    public void RetryRateLimitAndCircuitBreaker_HappyPath_AllocatesNothing()
    {
        var proxy = new IAllocNoTimeoutPoliciesServiceResilienceProxy(Inner, new AllocNoTimeoutPoliciesServiceResiliencePolicies());
        AssertAllocatesNothing(() => proxy.GetAsync("x", CancellationToken.None), "[Retry] [RateLimit] [CircuitBreaker] happy path");
    }

    [OptimizedBuildFact]
    public void Timeout_HappyPath_AllocatesOnlyTheCancellationTokenSource()
    {
        var proxy = new IAllocTimeoutServiceResilienceProxy(Inner, new AllocTimeoutServiceResiliencePolicies());
        BytesPerCall(() => proxy.GetAsync("x", CancellationToken.None)).Should().Be(TimeoutBytesPerCall);
    }

    [OptimizedBuildFact]
    public void AllPolicies_HappyPath_AllocatesOnlyTheTimeoutCancellationTokenSource()
    {
        var proxy = new IAllocAllPoliciesServiceResilienceProxy(Inner, new AllocAllPoliciesServiceResiliencePolicies());
        BytesPerCall(() => proxy.GetAsync("x", CancellationToken.None)).Should().Be(TimeoutBytesPerCall);
    }

    // The whole proxy call, measured with AllocationGate: it settles the heap, warms up, and
    // throws if a call does not complete synchronously, so the measurement covers the proxy and
    // nothing the scheduler adds. Budget 0 fails on any allocation, because the gate compares the
    // total and never rounds a small one away.
    private static void AssertAllocatesNothing(Func<ValueTask<string>> call, string label)
    {
        Consume(call());
        AllocationGate.AssertBudgetValueTask(0, Iterations, call, label);
    }

    // The exact bytes per call, rounded up.
    private static long BytesPerCall(Func<ValueTask<string>> call)
    {
        Consume(call());
        return AllocationGate.MeasureBytesPerCallValueTask(Iterations, call);
    }

    // Checked once before measuring: the call takes the happy path and returns the inner result.
    private static void Consume(ValueTask<string> pending)
    {
        if (!pending.IsCompletedSuccessfully)
            throw new InvalidOperationException("The happy path must complete synchronously.");
        if (!string.Equals(pending.Result, "ok", StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected 'ok', got '{pending.Result}'.");
    }
}

// The zero-allocation guarantee holds for optimized builds. A Debug build compiles every async
// method's state machine as a class, so the generated proxy allocates it on each call there.
[AttributeUsage(AttributeTargets.Method)]
public sealed class OptimizedBuildFactAttribute : FactAttribute
{
#if DEBUG
    public OptimizedBuildFactAttribute() =>
        Skip = "Allocation budgets apply to Release builds; Debug compiles async state machines as classes.";
#endif
}
