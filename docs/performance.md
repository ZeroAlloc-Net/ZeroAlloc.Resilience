---
id: performance
title: Performance
sidebar_position: 4
---

# Performance

ZeroAlloc.Resilience is designed so that the proxy adds **zero heap allocation** on the happy path for methods without `[Timeout]`. All benchmarks are measured with [BenchmarkDotNet](https://benchmarkdotnet.org/) (.NET 10, Release mode, `[MemoryDiagnoser]`).

## Head-to-head vs Polly v8

<!-- BENCH:START -->
_Last refreshed: 2026-10-15. Polly.Core 8.8.0, BenchmarkDotNet 0.15.8 with `--inProcess`, .NET 10.0.13 on Debian 13. Timings are indicative — measured on a developer laptop (Core Ultra 7 255H, hybrid P/E cores) under background load; allocations are exact, ratios are same-run._

[Polly](https://github.com/App-vNext/Polly) v8 (`ResiliencePipeline`) is the de-facto resilience library in .NET. Each row pair was measured side by side in one BenchmarkDotNet run; the comparison column is Polly's mean divided by ZA's.

| Operation | ZA vs Polly | Polly v8 | ZA.Resilience |
|---|---:|---:|---:|
| Retry, happy path | **14.7× faster, 0 B alloc** | 193.33 ns / 64 B | **13.18 ns / 0 B** |
| CircuitBreaker, closed | **15.0× faster, 0 B alloc** | 217.99 ns / 64 B | **14.50 ns / 0 B** |
| Retry with 2/3 failures | 2.0% slower wall-clock, **2.6× less alloc** | 7,962,763.44 ns / 3,185 B | 8,124,601.61 ns / 1,225 B |
| Retry with 2/3 failures, **backoff=0** (isolates loop overhead) | **2.8× faster, 71% less alloc** | 5,868.66 ns / 1,984 B | **2,092.61 ns / 576 B** |
| All-policies stacked, happy path | **5.9× faster**, 40 B more alloc | 623.68 ns / 104 B | **105.87 ns / 144 B** |
| All-policies stacked, retry triggers (2/3 fail) | 4.5% slower wall-clock, 24% less alloc | 6,220,119.07 ns / 7,904 B | 6,498,320.74 ns / 6,008 B |
| All-policies stacked, CB open (fast-reject) | **1.35× faster, 33% less alloc** | 3,403.23 ns / 1,352 B | **2,512.78 ns / 912 B** |

The happy-path gap is driven by Polly's `ResiliencePipeline.ExecuteAsync` walking the strategy chain via delegate dispatch and allocating a `ResilienceContext` per call (64 B). ZA emits one direct method per interface — the retry/CB checks are inline `if` statements and `Volatile.Read` calls. No context object, no closure, no delegate.

**Retry with failures.** With a 1 ms backoff both libraries spend almost all of each call in two `Task.Delay(1)` waits, so wall-clock is within 2% and the timer, not the retry loop, sets the figure. At backoff 0 the loop itself is measured, and ZA is 2.8× faster with 71% less allocation.

**All-policies stacked comparison.** Three rows compare a 4-policy stack (Retry + Timeout + RateLimit + CircuitBreaker). **Happy path** measures cumulative dispatch overhead — ZA wins because the generator emits one flat method with inline policy checks, while Polly walks the strategy chain with delegate dispatch. ZA allocates 40 B more: the linked `CancellationTokenSource` and timer that `[Timeout]` creates, 144 B. **Retry triggers** measures the most realistic prod failure mode — inner fails 2/3, retry recovers; both libraries are bound by the same `Task.Delay(1ms)` floor. These two rows need a per-iteration reset, so BenchmarkDotNet ran them with one invocation per iteration and reports no ratio for them; the comparison is computed from their means.

**CB open (fast-reject)** measures the steady-state cost when the circuit is open. The generated proxy checks `CanExecute()` once, before it creates the timeout's `CancellationTokenSource` or enters the retry loop, and throws `ResilienceException` straight away, so an open circuit is never retried; see [Execution Order](core-concepts/execution-order.md). Polly's retry is configured not to handle `BrokenCircuitException`, so it too rejects once per call. The 912 B ZA allocates is the cost of throwing that one `ResilienceException` and catching it through the awaited `ValueTask`: the single-policy CircuitBreaker-open and RateLimit-exhausted rows in the self-benchmark below have no retry at all and allocate the same 912 B. Polly's rejection costs 1,352 B.

Rate-limit and timeout limits in the all-policies harness are set to `int.MaxValue` permits / 60s ResetMs so neither policy trips during measurement. The rate-limiter apples-to-apples comparison is deferred because the two libraries' rate-limiter implementations differ (Polly wraps `System.Threading.RateLimiting.ConcurrencyLimiter`; ZA has its own throughput-based impl).
<!-- BENCH:END -->

## Self-benchmark (all ZA scenarios)

Measured in the same session as the Polly comparison. Timings are indicative — measured on a developer laptop (Core Ultra 7 255H, hybrid P/E cores) under background load; allocations are exact, ratios are same-run.

| Benchmark | Mean | Ratio to direct call | Allocated |
|---|---:|---:|---:|
| Direct call (no proxy) | 3.346 ns | 1.00 | 0 B |
| Retry proxy — first attempt succeeds | 13.187 ns | 3.95 | 0 B |
| CircuitBreaker proxy — Closed state | 14.299 ns | 4.29 | 0 B |
| RateLimit proxy — token available | 36.841 ns | 11.04 | 0 B |
| All-policies proxy — happy path | 96.948 ns | 29.05 | 144 B † |
| CircuitBreaker proxy — Open (fast-reject) | 2,354.193 ns | 705.49 | 912 B § |
| RateLimit proxy — exhausted (fast-reject) | 2,453.881 ns | 735.36 | 912 B § |
| Retry proxy — 2 failures then success | 7,980,461.559 ns ‡ | 2,391,518.29 | 1,262 B ‡ |

† The 144 B in the all-policies benchmark is the linked `CancellationTokenSource` created by `[Timeout]` plus the timer its `CancelAfter` starts. Without `[Timeout]`, the proxy allocates 0 bytes. `HappyPathAllocationTests` asserts both budgets on every test run.

‡ The 2-failure retry benchmark includes real `Task.Delay(backoff)` pauses (`BackoffMs = 1`). Mean reflects wall-clock delay, not CPU overhead. The allocation includes the two exceptions the inner implementation throws.

§ The rejection check allocates nothing; the 912 B is the `ResilienceException` the proxy throws and the benchmark catches through the awaited `ValueTask`. Both fast-reject paths throw the same exception, so they allocate the same.

## What drives each result

**Direct call** — `ValueTask.FromResult("ok")` from the inner implementation. Baseline reference.

**Retry — first attempt succeeds** — enters the retry loop, runs the `for` iteration once, calls the inner method, returns. Zero allocation.

**CircuitBreaker — Closed** — `CanExecute()` is a `Volatile.Read` on a `long` field. Call passes through. Zero allocation.

**RateLimit — token available** — `TryAcquire()` is a `Volatile.Read` + `Interlocked.CompareExchange`. Token decremented, call passes through. Zero allocation.

**All-policies — happy path** — rate-limit check, CB check, `CancellationTokenSource.CreateLinkedTokenSource`, `CancelAfter`, inner call, `OnSuccess`, return. The 144 B is the `CancellationTokenSource` and its `CancelAfter` timer — unavoidable for timeout. A per-attempt linked CTS is added only when `PerAttemptTimeoutMs` is set.

**CB — Open (fast-reject)** — `CanExecute()` returns `false` immediately and the proxy throws `ResilienceException`. The check itself allocates nothing; throwing and catching the exception costs 912 B and about 2.4 µs, which is why this row is slower than the closed-circuit call.

**RateLimit — exhausted** — `TryAcquire()` returns `false` after seeing zero tokens and the proxy throws `ResilienceException`. The check allocates nothing; the exception costs the same 912 B as the open circuit.

## Design invariants

- **No boxing** — `CircuitBreakerFsm` and `RateLimiter` use `long` fields; enum values cast to/from `long` is a no-op at the CPU level.
- **No closures, no delegates** — the proxy is a concrete class with a concrete method. Nothing is captured.
- **No LINQ on the hot path** — branching is compiled into `if` checks and a `for` loop. Values are read from `readonly` fields holding sealed policy objects; no allocation per call, and the per-attempt `CancellationTokenSource` is created only when a per-attempt timeout is set.
- **`CancellationTokenSource` is the only unavoidable allocation** — one per call when `[Timeout]` is configured. Methods without `[Timeout]` allocate nothing.

## Running the benchmarks yourself

```bash
cd benchmarks/ZeroAlloc.Resilience.Benchmarks
dotnet run -c Release -- --filter '*PollyComparisonBenchmark*' '*ResilienceBenchmarks*' --job Default --inProcess
```

To run a specific benchmark:

```bash
dotnet run -c Release -- --filter "*HappyPath*" --inProcess
```
