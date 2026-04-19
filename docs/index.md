---
id: index
title: ZeroAlloc.Resilience
sidebar_position: 1
---

# ZeroAlloc.Resilience

Source-generated, zero-allocation resilience policies for .NET.

Add `[Retry]`, `[Timeout]`, `[RateLimit]`, and `[CircuitBreaker]` to an interface; the generator emits a proxy composing all policies in declaration order with no heap allocation on the happy path.

## Quick example

```csharp
[Retry(MaxAttempts = 3, BackoffMs = 200, Jitter = true)]
[Timeout(Ms = 5_000)]
[CircuitBreaker(MaxFailures = 5, ResetMs = 1_000, HalfOpenProbes = 1, Fallback = nameof(FetchFallback))]
public interface IExternalService
{
    ValueTask<string> FetchAsync(string id, CancellationToken ct);
    ValueTask<string> FetchFallback(string id, CancellationToken ct);
}

// Register — one line
services.AddExternalServiceResilience<ExternalServiceImpl>();

// Inject IExternalService — all policies are wired automatically
```

---

## Contents

| Page | Description |
|---|---|
| [Getting Started](getting-started.md) | Install, annotate an interface, register with DI |
| [Attributes](attributes.md) | `[Retry]`, `[Timeout]`, `[RateLimit]`, `[CircuitBreaker]` reference |
| [Source Generator](source-generator.md) | What the generator emits — annotated proxy example |
| [Performance](performance.md) | Benchmark results and allocation profile |

### Core Concepts

| Page | Description |
|---|---|
| [Retry](core-concepts/retry.md) | Exponential backoff, jitter, per-attempt timeout, exhaustion behaviour |
| [Timeout](core-concepts/timeout.md) | Total timeout vs. per-attempt timeout, CTS lifecycle |
| [Rate Limit](core-concepts/rate-limit.md) | Token bucket, lock-free acquire, `Shared` vs `Instance` scope |
| [Circuit Breaker](core-concepts/circuit-breaker.md) | State machine, failure tracking, half-open probing, fallback |
| [Execution Order](core-concepts/execution-order.md) | How policies compose: RateLimit → CB → Timeout → Retry |

### Guides

| Page | Description |
|---|---|
| [Fallback](guides/fallback.md) | Declare and implement circuit-breaker fallback methods |
| [Method-Level Overrides](guides/method-level-overrides.md) | Per-method policy configuration |
| [Result Return Types](guides/result-return-types.md) | Non-throwing failure path with `Result<T>` |
| [DI Registration](guides/di-registration.md) | Generated extension method, overrides, manual construction |

### Diagnostics

| ID | Severity | Description |
|---|---|---|
| [ZR0001](diagnostics/ZR0001.md) | Error | Fallback method not found or signature mismatch |
| [ZR0002](diagnostics/ZR0002.md) | Warning | Timeout configured but method has no `CancellationToken` |
