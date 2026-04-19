---
id: performance
title: Performance
sidebar_position: 4
---

# Performance

ZeroAlloc.Resilience is designed so that the proxy adds **zero heap allocation** on the happy path for methods without `[Timeout]`. All benchmarks are measured with [BenchmarkDotNet](https://benchmarkdotnet.org/) (.NET 10, Release mode, `[MemoryDiagnoser]`).

## Results

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
