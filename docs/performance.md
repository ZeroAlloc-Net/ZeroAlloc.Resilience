---
id: performance
title: Performance
sidebar_position: 4
---

# Performance

ZeroAlloc.Resilience is designed so that the proxy adds **zero heap allocation** on the happy path for methods without `[Timeout]`. All benchmarks are measured with [BenchmarkDotNet](https://benchmarkdotnet.org/) (.NET 10, Release mode, `[MemoryDiagnoser]`).

## Head-to-head vs Polly v8

<!-- BENCH:START -->
_Last refreshed: 2026-05-18_

[Polly](https://github.com/App-vNext/Polly) v8 (`ResiliencePipeline`) is the de-facto resilience library in .NET. ZA.Resilience's source-generated proxy beats it on both throughput and allocation for the policies both libraries support apples-to-apples.

| Operation | Polly v8 | ZA.Resilience | Speedup |
|---|---:|---:|---:|
| Retry, happy path | 600 ns / 64 B | **23 ns / 0 B** | **26× faster, 0 B alloc** |
| CircuitBreaker, closed | 776 ns / 64 B | **17 ns / 0 B** | **45× faster, 0 B alloc** |
| Retry with 2/3 failures | 22.86 ms / 3,134 B | 27.89 ms / 948 B | 22% slower wall-clock, **3.3× less alloc** |
| Retry with 2/3 failures, **backoff=0** (isolates loop overhead) | 12.80 µs / 1,984 B | **7.31 µs / 576 B** | **43% faster, 71% less alloc** |
| All-policies stacked, happy path | 1,283 ns / 104 B | **126 ns / 144 B** | **10× faster** |
| All-policies stacked, retry triggers (2/3 fail) | 29.31 ms | 28.83 ms | parity (Task.Delay floor) |
| All-policies stacked, CB open (fast-reject) | **3.97 µs / 40 B** | 5.16 µs / 912 B | Polly wins — see narrative below |

The happy-path gap is driven by Polly's `ResiliencePipeline.ExecuteAsync` walking the strategy chain via delegate dispatch and allocating a `ResilienceContext` per call (64 B). ZA emits one direct method per interface — the retry/CB checks are inline `if` statements and `Volatile.Read` calls. No context object, no closure, no delegate.

The retry-with-failures row at 1 ms backoff shows ZA 22% slower wall-clock. **Phase-1 investigation (backoff=0 micro-bench, 2026-05-18) confirms the retry loop itself is competitive — at `Task.Delay(0)` ZA is *faster* than Polly (7.31 µs vs 12.80 µs, 43% lower) with 71% less allocation.** The 22% gap on the 1 ms-backoff row is `Task.Delay` Windows timer-tick alignment (each `Task.Delay(1ms)` wakes ~16 ms later due to system timer resolution) plus scheduler-thread continuation handoff — both framework-bound, not ZA loop overhead.

**All-policies stacked comparison.** Three rows compare a 4-policy stack (Retry + Timeout + RateLimit + CircuitBreaker). **Happy path** measures cumulative dispatch overhead — ZA wins by ~10× because the generator emits one flat method with inline policy checks (`Volatile.Read` + integer comparisons), while Polly's `ResiliencePipeline.ExecuteAsync` walks the strategy chain with delegate dispatch. **Retry triggers** measures the most realistic prod failure mode — inner fails 2/3, retry recovers; both libraries hit the same Windows-timer-tick floor on `Task.Delay(1ms)` so wall-clock is at parity. **CB open (fast-reject)** measures the steady-state cost when the circuit is open: **Polly wins** because we explicitly excluded `BrokenCircuitException` from Polly's retry `ShouldHandle` — Polly does a single fast-reject per call. ZA's generated retry catches `ResilienceException` (which the CB raises) and retries 3× through the still-Open circuit, accumulating 912 B of state-machine allocations across the attempts. This is a real cost difference, not a measurement artifact — and arguably a correctness consideration: applications using ZA where the CB-open scenario matters should disable retry-on-CB-broken in user code, which the current generator doesn't surface as a knob (tracked for follow-up).

Rate-limit and timeout limits in the all-policies harness are set to `int.MaxValue` permits / 60s ResetMs so neither policy trips during measurement. The rate-limiter apples-to-apples comparison is deferred because the two libraries' rate-limiter implementations differ (Polly wraps `System.Threading.RateLimiting.ConcurrencyLimiter`; ZA has its own throughput-based impl).
<!-- BENCH:END -->

## Self-benchmark (all ZA scenarios)

| Benchmark | Mean | Allocated |
|---|---:|---:|
| Direct call (no proxy) | ~2 ns | 0 B |
| Retry proxy — first attempt succeeds | ~8 ns | 0 B |
| CircuitBreaker proxy — Closed state | ~12 ns | 0 B |
| RateLimit proxy — token available | ~18 ns | 0 B |
| All-policies proxy — happy path | ~35 ns | 96 B † |
| CircuitBreaker proxy — Open (fast-reject) | ~4 ns | 0 B |
| RateLimit proxy — exhausted (fast-reject) | ~3 ns | 0 B |
| Retry proxy — 2 failures then success | ~1.2 ms ‡ | 0 B |

† The 96 B in the all-policies benchmark comes from the `CancellationTokenSource` created by `[Timeout]`. Without `[Timeout]`, the proxy allocates 0 bytes.

‡ The 2-failure retry benchmark includes real `Task.Delay(backoff)` pauses (`BackoffMs = 1`). Mean reflects wall-clock delay, not CPU overhead.

## What drives each result

**Direct call** — `ValueTask.FromResult("ok")` from the inner implementation. Baseline reference.

**Retry — first attempt succeeds** — enters the retry loop, runs the `for` iteration once, calls the inner method, returns. Zero allocation.

**CircuitBreaker — Closed** — `CanExecute()` is a `Volatile.Read` on a `long` field. Call passes through. Zero allocation.

**RateLimit — token available** — `TryAcquire()` is a `Volatile.Read` + `Interlocked.CompareExchange`. Token decremented, call passes through. Zero allocation.

**All-policies — happy path** — rate-limit check, CB check, `CancellationTokenSource.CreateLinkedTokenSource` (96 B), `CancelAfter`, per-attempt linked CTS (linked to total), inner call, `OnSuccess`, return. The 96 B is the `CancellationTokenSource` — unavoidable for timeout.

**CB — Open (fast-reject)** — `CanExecute()` returns `false` immediately. The check itself allocates nothing. The benchmark's catch block allocates the `ResilienceException` object — that allocation is the caller's, not the proxy check.

**RateLimit — exhausted** — `TryAcquire()` returns `false` after seeing zero tokens. Instantaneous reject. Zero allocation on the proxy path.

## Design invariants

- **No boxing** — `CircuitBreakerFsm` and `RateLimiter` use `long` fields; enum values cast to/from `long` is a no-op at the CPU level.
- **No closures, no delegates** — the proxy is a concrete class with a concrete method. Nothing is captured.
- **No LINQ on the hot path** — branching is compiled into `if` checks and a `for` loop with literal values.
- **`CancellationTokenSource` is the only unavoidable allocation** — one per call when `[Timeout]` is configured. Methods without `[Timeout]` allocate nothing.

## Running the benchmarks yourself

```bash
cd benchmarks/ZeroAlloc.Resilience.Benchmarks
dotnet run -c Release
```

To run a specific benchmark:

```bash
dotnet run -c Release --filter "*HappyPath*"
```
