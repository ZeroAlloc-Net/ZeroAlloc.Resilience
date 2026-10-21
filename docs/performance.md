---
id: performance
title: Performance
sidebar_position: 4
---

# Performance

ZeroAlloc.Resilience is designed so that the proxy adds **zero heap allocation** on the happy path for methods without `[Timeout]`. All benchmarks are measured with [BenchmarkDotNet](https://benchmarkdotnet.org/) (.NET 10, Release mode, `[MemoryDiagnoser]`).

## Head-to-head vs Polly v8

<!-- BENCH:START -->
_Last refreshed: 2026-10-21. Polly.Core 8.8.0, BenchmarkDotNet 0.15.8, .NET 10.0.13 on Windows 11, i9-12900HK, on an otherwise idle machine. Every benchmark ran out of process in three launches, pinned to two performance cores. Allocations are exact. Speed is shown as same-run ratios; absolute timings are not published._

[Polly](https://github.com/App-vNext/Polly) v8 (`ResiliencePipeline`) is the de-facto resilience library in .NET. Every row was measured side by side in one BenchmarkDotNet run. "ZA vs Polly" divides the Polly row's mean by the ZA row's for the same operation; the brackets give the lowest and highest of the three launches.

| Operation | ZA vs Polly | Launch range | Polly alloc | ZA alloc |
|---|---:|---:|---:|---:|
| Retry, happy path | **20.0× faster** | 18.8–21.0 | 64 B | **0 B** |
| CircuitBreaker, closed | **23.3× faster** | 21.6–24.7 | 64 B | **0 B** |
| Retry with 2/3 failures | parity, 1.00× | 0.99–1.01 | 3,108 B ¶ | **952 B** ¶ |
| Retry with 2/3 failures, **backoff=0** (isolates loop overhead) | **2.8× faster** | 2.78–2.80 | 1,984 B | **576 B** |
| All-policies stacked, happy path | **7.7× faster** | 6.95–8.21 | 104 B | 144 B |
| All-policies stacked, retry triggers (2/3 fail) | parity, 1.01× † | 1.01–1.02 | 3,322 B ¶ | **1,320 B** ¶ |
| All-policies stacked, CB open (fast-reject) | **1.30× faster** | 1.26–1.33 | 1,352 B | **912 B** |

† These two rows need a per-iteration reset, so BenchmarkDotNet ran them as a separate job with one invocation per iteration; the comparison divides their means from the same run.

¶ These four rows wait on `Task.Delay`, so their timers and continuations allocate on other threads, and BenchmarkDotNet's figure moved between runs: the previous capture reported 7,904 B and 6,008 B for the all-policies pair, and this run 4,464 B for Polly's. They were re-measured with the precise process-wide counter, `GC.GetTotalAllocatedBytes(true)`, over 100 calls each after warm-up, and the table shows those figures.

The happy-path gap is driven by Polly's `ResiliencePipeline.ExecuteAsync` walking the strategy chain via delegate dispatch and allocating a `ResilienceContext` per call (64 B). ZA emits one direct method per interface — the retry/CB checks are inline `if` statements and `Volatile.Read` calls. No context object, no closure, no delegate.

**Retry with failures.** With a 1 ms backoff both libraries spend almost all of each call in two `Task.Delay(1)` waits, which on Windows each last a timer tick of about 15.6 ms, so they are level and the timer, not the retry loop, sets the figure. At backoff 0 the loop itself is measured, and ZA is 2.8× faster with 71% less allocation.

**All-policies stacked comparison.** Three rows compare a 4-policy stack (Retry + Timeout + RateLimit + CircuitBreaker). **Happy path** measures cumulative dispatch overhead — ZA wins because the generator emits one flat method with inline policy checks, while Polly walks the strategy chain with delegate dispatch. ZA allocates 40 B more: the linked `CancellationTokenSource` and timer that `[Timeout]` creates, 144 B. **Retry triggers** measures the most realistic prod failure mode — inner fails 2/3, retry recovers; both libraries are bound by the same `Task.Delay(1ms)` floor, and ZA allocates 60% less.

**CB open (fast-reject)** measures the steady-state cost when the circuit is open. The generated proxy checks `CanExecute()` once, before it creates the timeout's `CancellationTokenSource` or enters the retry loop, and throws `ResilienceException` straight away, so an open circuit is never retried; see [Execution Order](core-concepts/execution-order.md). Polly's retry is configured not to handle `BrokenCircuitException`, so it too rejects once per call. The 912 B ZA allocates is the cost of throwing that one `ResilienceException` and catching it through the awaited `ValueTask`: the single-policy CircuitBreaker-open and RateLimit-exhausted rows in the self-benchmark below have no retry at all and allocate the same 912 B. Polly's rejection costs 1,352 B.

Rate-limit and timeout limits in the all-policies harness are set to `int.MaxValue` permits / 60s ResetMs so neither policy trips during measurement. The rate-limiter apples-to-apples comparison is deferred because the two libraries' rate-limiter implementations differ (Polly wraps `System.Threading.RateLimiting.ConcurrencyLimiter`; ZA has its own throughput-based impl).
<!-- BENCH:END -->

## Self-benchmark (all ZA scenarios)

Measured in the same session as the Polly comparison, in one BenchmarkDotNet run. Allocations are exact. Speed is shown as same-run ratios; absolute timings are not published. `Ratio` is each row's mean relative to the retry proxy's happy path: the direct call takes well under a nanosecond, too little to divide by.

| Benchmark | Ratio to retry proxy | Launch range | Allocated |
|---|---:|---:|---:|
| Direct call (no proxy) | 0.06 | 0.04–0.07 | 0 B |
| Retry proxy — first attempt succeeds | 1.00 | | 0 B |
| CircuitBreaker proxy — Closed state | 1.00 | 0.85–1.09 | 0 B |
| RateLimit proxy — token available | 2.67 | 2.29–2.91 | 0 B |
| All-policies proxy — happy path | 9.54 | 8.11–10.54 | 144 B † |
| CircuitBreaker proxy — Open (fast-reject) | 329 | 285–353 | 912 B § |
| RateLimit proxy — exhausted (fast-reject) | 321 | 277–347 | 912 B § |
| Retry proxy — 2 failures then success | ‡ | | 952 B ‡ |

† The 144 B in the all-policies benchmark is the linked `CancellationTokenSource` created by `[Timeout]` plus the timer its `CancelAfter` starts. Without `[Timeout]`, the proxy allocates 0 bytes. `HappyPathAllocationTests` asserts both budgets on every test run.

‡ The 2-failure retry benchmark includes real `Task.Delay(backoff)` pauses (`BackoffMs = 1`), two Windows timer ticks of about 15.6 ms each, so its time measures the timer, not the proxy, and no ratio is given. The allocation includes the two exceptions the inner implementation throws, re-measured process-wide like the ¶ rows above.

§ The rejection check allocates nothing; the 912 B is the `ResilienceException` the proxy throws and the benchmark catches through the awaited `ValueTask`. Both fast-reject paths throw the same exception, so they allocate the same.

## What drives each result

**Direct call** — `ValueTask.FromResult("ok")` from the inner implementation. Baseline reference.

**Retry — first attempt succeeds** — enters the retry loop, runs the `for` iteration once, calls the inner method, returns. Zero allocation.

**CircuitBreaker — Closed** — `CanExecute()` is a `Volatile.Read` on a `long` field. Call passes through. Zero allocation.

**RateLimit — token available** — `TryAcquire()` is a `Volatile.Read` + `Interlocked.CompareExchange`. Token decremented, call passes through. Zero allocation.

**All-policies — happy path** — rate-limit check, CB check, `CancellationTokenSource.CreateLinkedTokenSource`, `CancelAfter`, inner call, `OnSuccess`, return. The 144 B is the `CancellationTokenSource` and its `CancelAfter` timer — unavoidable for timeout. A per-attempt linked CTS is added only when `PerAttemptTimeoutMs` is set.

**CB — Open (fast-reject)** — `CanExecute()` returns `false` immediately and the proxy throws `ResilienceException`. The check itself allocates nothing; throwing and catching the exception costs 912 B and is why this row's ratio, 705.49, is far above the closed-circuit call's 4.29.

**RateLimit — exhausted** — `TryAcquire()` returns `false` after seeing zero tokens and the proxy throws `ResilienceException`. The check allocates nothing; the exception costs the same 912 B as the open circuit.

## Design invariants

- **No boxing** — `CircuitBreakerFsm` and `RateLimiter` use `long` fields; enum values cast to/from `long` is a no-op at the CPU level.
- **No closures, no delegates** — the proxy is a concrete class with a concrete method. Nothing is captured.
- **No LINQ on the hot path** — branching is compiled into `if` checks and a `for` loop. Values are read from `readonly` fields holding sealed policy objects; no allocation per call, and the per-attempt `CancellationTokenSource` is created only when a per-attempt timeout is set.
- **`CancellationTokenSource` is the only unavoidable allocation** — one per call when `[Timeout]` is configured. Methods without `[Timeout]` allocate nothing.

## Running the benchmarks yourself

```bash
cd benchmarks/ZeroAlloc.Resilience.Benchmarks
dotnet run -c Release -- --filter '*PollyComparisonBenchmark*' '*ResilienceBenchmarks*' --launchCount 3
```

To run a specific benchmark:

```bash
dotnet run -c Release -- --filter "*HappyPath*" --inProcess
```
