using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Resilience;

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
    // on .NET 10 x64. This is the per-call cost docs/performance.md documents for [Timeout].
    private const long TimeoutBytesPerCall = 144;

    private static readonly AllocSucceedsImpl Inner = new();

    [OptimizedBuildFact]
    public void Retry_FirstAttemptSucceeds_AllocatesNothing()
    {
        var proxy = new IAllocRetryServiceResilienceProxy(Inner, new AllocRetryServiceResiliencePolicies());
        BytesPerCall(() => proxy.GetAsync("x", CancellationToken.None)).Should().Be(0);
    }

    [OptimizedBuildFact]
    public void CircuitBreaker_Closed_AllocatesNothing()
    {
        var proxy = new IAllocCircuitServiceResilienceProxy(Inner, new AllocCircuitServiceResiliencePolicies());
        BytesPerCall(() => proxy.GetAsync("x", CancellationToken.None)).Should().Be(0);
    }

    [OptimizedBuildFact]
    public void CircuitBreakerWithFallback_Closed_AllocatesNothing()
    {
        var proxy = new IAllocCircuitFallbackServiceResilienceProxy(Inner, new AllocCircuitFallbackServiceResiliencePolicies());
        BytesPerCall(() => proxy.GetAsync("x", CancellationToken.None)).Should().Be(0);
    }

    [OptimizedBuildFact]
    public void RateLimit_TokenAvailable_AllocatesNothing()
    {
        var proxy = new IAllocRateLimitServiceResilienceProxy(Inner, new AllocRateLimitServiceResiliencePolicies());
        BytesPerCall(() => proxy.GetAsync("x", CancellationToken.None)).Should().Be(0);
    }

    [OptimizedBuildFact]
    public void RetryRateLimitAndCircuitBreaker_HappyPath_AllocatesNothing()
    {
        var proxy = new IAllocNoTimeoutPoliciesServiceResilienceProxy(Inner, new AllocNoTimeoutPoliciesServiceResiliencePolicies());
        BytesPerCall(() => proxy.GetAsync("x", CancellationToken.None)).Should().Be(0);
    }

    [OptimizedBuildFact]
    public void Timeout_HappyPath_AllocatesOnlyTheCancellationTokenSource()
    {
        var proxy = new IAllocTimeoutServiceResilienceProxy(Inner, new AllocTimeoutServiceResiliencePolicies());
        BytesPerCall(() => proxy.GetAsync("x", CancellationToken.None)).Should().BeLessThanOrEqualTo(TimeoutBytesPerCall);
    }

    [OptimizedBuildFact]
    public void AllPolicies_HappyPath_AllocatesOnlyTheTimeoutCancellationTokenSource()
    {
        var proxy = new IAllocAllPoliciesServiceResilienceProxy(Inner, new AllocAllPoliciesServiceResiliencePolicies());
        BytesPerCall(() => proxy.GetAsync("x", CancellationToken.None)).Should().BeLessThanOrEqualTo(TimeoutBytesPerCall);
    }

    // Each call must complete synchronously, so the measurement covers the proxy and nothing
    // the scheduler adds. Warm-up runs first so tiering and first-call initialisation are excluded.
    private static long BytesPerCall(Func<ValueTask<string>> call)
    {
        for (var i = 0; i < 100; i++)
            Consume(call());

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Iterations; i++)
            Consume(call());
        var after = GC.GetAllocatedBytesForCurrentThread();

        return (after - before) / Iterations;
    }

    // Plain checks rather than assertions: an assertion object would allocate inside the measured loop.
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
