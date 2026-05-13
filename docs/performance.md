---
id: performance
title: Performance
sidebar_position: 4
---

# Performance

ZeroAlloc.Resilience is designed so that the proxy adds **zero heap allocation** on the happy path for methods without `[Timeout]`. All benchmarks are measured with [BenchmarkDotNet](https://benchmarkdotnet.org/) (.NET 10, Release mode, `[MemoryDiagnoser]`).

## Head-to-head vs Polly v8

<!-- BENCH:START -->
_Last refreshed: 2026-05-13_

[Polly](https://github.com/App-vNext/Polly) v8 (`ResiliencePipeline`) is the de-facto resilience library in .NET. ZA.Resilience's source-generated proxy beats it on both throughput and allocation for the policies both libraries support apples-to-apples.

| Operation | Polly v8 | ZA.Resilience | Speedup |
|---|---:|---:|---:|
| Retry, happy path | 600 ns / 64 B | **23 ns / 0 B** | **26× faster, 0 B alloc** |
| CircuitBreaker, closed | 776 ns / 64 B | **17 ns / 0 B** | **45× faster, 0 B alloc** |
| Retry with 2/3 failures | 22.86 ms / 3,134 B | 27.89 ms / 948 B | 22% slower wall-clock, **3.3× less alloc** |

The happy-path gap is driven by Polly's `ResiliencePipeline.ExecuteAsync` walking the strategy chain via delegate dispatch and allocating a `ResilienceContext` per call (64 B). ZA emits one direct method per interface — the retry/CB checks are inline `if` statements and `Volatile.Read` calls. No context object, no closure, no delegate.

The retry-with-failures row is dominated by `Task.Delay(BackoffMs)` (2× 1 ms = 2 ms minimum); the residual 22% wall-clock gap is ZA's `for`-loop retry scheduling — measurable, but mostly invisible against I/O latency in real workloads. Allocation is **3.3× lower** at 948 B vs 3,134 B.

**Note on all-policies stacked comparison**: deferred. The two libraries' rate-limiter policies have different surface (Polly.RateLimiting is a separate package, ZA's RateLimit is part of the main package), so an apples-to-apples 4-policy comparison requires a custom harness. The Retry + CB pairings above are the most-cited isolated scenarios; see the self-benchmark table for ZA's all-policies stack.
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
