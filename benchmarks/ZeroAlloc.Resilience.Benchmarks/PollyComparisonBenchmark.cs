#pragma warning disable ZR0002

using System;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using ZeroAlloc.Resilience;

namespace ZeroAlloc.Resilience.Benchmarks;

// Polly v8 (ResiliencePipeline) is the de-facto resilience library for .NET.
// Compared against ZA.Resilience's generator-emitted proxy across four
// idiomatic scenarios — retry happy path, circuit-breaker closed,
// retry-on-actual-failure, all-policies stacked.
//
// Polly v8 ResiliencePipeline is a builder pattern; the pipeline is built
// once in GlobalSetup. ZA proxies are also built once. Per-call cost is
// what gets measured.

[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class PollyComparisonBenchmark
{
    // ZA proxies (reused from the existing self-benchmark setup)
    private AlwaysSucceedsImpl _inner = null!;
    private IRetryService _zaRetry = null!;
    private ICircuitService _zaCb = null!;
    private IRetryService _zaRetryFailTwice = null!;
    private IAllPoliciesService _zaAllPolicies = null!;

    // Polly pipelines
    private ResiliencePipeline _pollyRetry = null!;
    private ResiliencePipeline _pollyCb = null!;
    private ResiliencePipeline _pollyAllPolicies = null!;
    private RetryWith2FailuresImpl _pollyFailImpl = null!;

    [GlobalSetup]
    public void Setup()
    {
        _inner = new AlwaysSucceedsImpl();
        var retry = new RetryPolicy(3, 1, false, 0);
        var cb = new CircuitBreakerPolicy(5, 1_000, 1);
        var rl = new RateLimiter(1_000_000, 1_000_000, RateLimitScope.Shared);

        _zaRetry = new IRetryServiceResilienceProxy(_inner, retry);
        _zaCb = new ICircuitServiceResilienceProxy(_inner, cb);
        // RetryWith2FailuresImpl carries an Interlocked counter — the two pipelines
        // MUST get separate instances; do not "dedupe" this allocation.
        _zaRetryFailTwice = new IRetryServiceResilienceProxy(new RetryWith2FailuresImpl(), retry);
        _zaAllPolicies = new IAllPoliciesServiceResilienceProxy(
            _inner, retry, new TimeoutPolicy(5_000), rl, cb);

        // Polly v8: ResiliencePipelineBuilder
        _pollyRetry = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(1),
                BackoffType = DelayBackoffType.Constant,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(),
            })
            .Build();

        _pollyCb = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 1.0,
                MinimumThroughput = 5,
                SamplingDuration = TimeSpan.FromMilliseconds(1_000),
                BreakDuration = TimeSpan.FromMilliseconds(1_000),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(),
            })
            .Build();

        _pollyAllPolicies = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(1),
                BackoffType = DelayBackoffType.Constant,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(),
            })
            .AddTimeout(TimeSpan.FromMilliseconds(5_000))
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 1.0,
                MinimumThroughput = 5,
                SamplingDuration = TimeSpan.FromMilliseconds(1_000),
                BreakDuration = TimeSpan.FromMilliseconds(1_000),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(),
            })
            .Build();

        _pollyFailImpl = new RetryWith2FailuresImpl();
    }

    // --- Retry, no failure (happy path) ---

    [Benchmark(Baseline = true, Description = "Polly: Retry pipeline, happy path")]
    [BenchmarkCategory("RetryHappy")]
    public async ValueTask<string> Polly_RetryHappy()
        => await _pollyRetry.ExecuteAsync(async ct => await _inner.GetAsync("x", ct), CancellationToken.None);

    [Benchmark(Description = "ZA.Resilience: Retry proxy, happy path")]
    [BenchmarkCategory("RetryHappy")]
    public ValueTask<string> Za_RetryHappy()
        => _zaRetry.GetAsync("x", CancellationToken.None);

    // --- CircuitBreaker, closed state ---

    [Benchmark(Description = "Polly: CircuitBreaker pipeline, closed")]
    [BenchmarkCategory("CircuitBreakerClosed")]
    public async ValueTask<string> Polly_CbClosed()
        => await _pollyCb.ExecuteAsync(async ct => await _inner.GetAsync("x", ct), CancellationToken.None);

    [Benchmark(Description = "ZA.Resilience: CircuitBreaker proxy, closed")]
    [BenchmarkCategory("CircuitBreakerClosed")]
    public ValueTask<string> Za_CbClosed()
        => _zaCb.GetAsync("x", CancellationToken.None);

    // --- Retry with 2-in-3 failure rate (real retry exercise) ---

    [Benchmark(Description = "Polly: Retry with 2/3 failures")]
    [BenchmarkCategory("RetryWithFailures")]
    public async ValueTask<string> Polly_RetryFailTwice()
        => await _pollyRetry.ExecuteAsync(async ct => await _pollyFailImpl.GetAsync("x", ct), CancellationToken.None);

    [Benchmark(Description = "ZA.Resilience: Retry proxy with 2/3 failures")]
    [BenchmarkCategory("RetryWithFailures")]
    public ValueTask<string> Za_RetryFailTwice()
        => _zaRetryFailTwice.GetAsync("x", CancellationToken.None);

    // --- All policies stacked: REMOVED ---
    //
    // The ZA IAllPoliciesService interface includes RateLimit with
    // BurstSize = 1,000,000 — under BDN's pilot phase the cumulative
    // call rate exceeds the bucket capacity and the rate limiter
    // (correctly) throws. Polly v8's rate limiter lives in a separate
    // package (Polly.RateLimiting) with a different surface; an
    // apples-to-apples all-policies comparison requires a custom
    // interface that pairs each library's policy stack one-for-one.
    // Tracked in c:/Projects/Prive/ZeroAlloc/docs/COMPARISON-SWEEP-BACKLOG.md
    // (search "All-policies stacked comparison deferred").
}
