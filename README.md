# ZeroAlloc.Resilience

Source-generated, zero-allocation resilience policies for .NET.

Add `[Retry]`, `[Timeout]`, `[RateLimit]`, and `[CircuitBreaker]` to an interface. A Roslyn source generator emits a proxy class that composes all policies in declaration order with **no heap allocation on the happy path** (beyond the unavoidable `CancellationTokenSource` for timeout). AOT-safe.

[![NuGet](https://img.shields.io/nuget/v/ZeroAlloc.Resilience.svg)](https://www.nuget.org/packages/ZeroAlloc.Resilience)
[![Build](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/actions/workflows/ci.yml/badge.svg)](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/actions)

---

## Quick start

```bash
dotnet add package ZeroAlloc.Resilience
```

```csharp
[Retry(MaxAttempts = 3, BackoffMs = 200, Jitter = true, PerAttemptTimeoutMs = 1_000)]
[Timeout(Ms = 5_000)]
[RateLimit(MaxPerSecond = 100, BurstSize = 10)]
[CircuitBreaker(MaxFailures = 5, ResetMs = 1_000, HalfOpenProbes = 1, Fallback = nameof(FetchFallback))]
public interface IExternalService
{
    ValueTask<string> FetchAsync(string id, CancellationToken ct);
    ValueTask<string> FetchFallback(string id, CancellationToken ct);
}

// Register — one line wires everything
builder.Services.AddExternalServiceResilience<ExternalServiceImpl>();
```

Inject `IExternalService` anywhere — all policies are transparent to the caller.

---

## Features

| Feature | Notes |
|---------|-------|
| Zero allocation on happy path | Policy checks are integer comparisons and CAS operations |
| AOT / trimmer safe | Generated proxy is concrete; no reflection at runtime |
| Retry with exponential backoff | Jitter, per-attempt timeout, total timeout all configurable |
| Timeout | Total operation timeout wrapping all retries and backoff delays |
| Rate limiting | Lock-free token bucket; `Shared` (singleton) or `Instance` (per-proxy) scope |
| Circuit breaker | Closed → Open → HalfOpen FSM backed by `ZeroAlloc.StateMachine` (concurrent CAS) |
| Fallback | Method called when circuit is open — same signature, no allocation |
| `Result<T>` support | Return `Result<T>` to get failures without exceptions |
| Method-level overrides | Any attribute on a method shadows the interface-level config for that method |
| DI integration | Generated `Add{Service}Resilience<TImpl>()` extension registers everything |

---

## Policy execution order

Policies execute in this order on every call:

```
RateLimit → CircuitBreaker → Timeout → Retry (with PerAttemptTimeout inside)
```

Each policy runs before the inner call is even attempted. If the rate limiter rejects, the circuit breaker and retry logic are never reached.

---

## Attribute overview

### `[Retry]`

```csharp
[Retry(MaxAttempts = 3, BackoffMs = 200, Jitter = true, PerAttemptTimeoutMs = 1_000)]
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaxAttempts` | `int` | `3` | Total attempts (initial + retries) |
| `BackoffMs` | `int` | `200` | Base backoff ms; actual = `BackoffMs * 2^attempt` |
| `Jitter` | `bool` | `false` | Add up to 50% random jitter to prevent thundering-herd |
| `PerAttemptTimeoutMs` | `int` | `0` | Per-attempt cancellation timeout; 0 = disabled |

### `[Timeout]`

```csharp
[Timeout(Ms = 5_000)]
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Ms` | `int` | required | Total operation timeout wrapping all retries and backoff |

### `[RateLimit]`

```csharp
[RateLimit(MaxPerSecond = 100, BurstSize = 10, Scope = RateLimitScope.Shared)]
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaxPerSecond` | `int` | required | Token refill rate |
| `BurstSize` | `int` | `1` | Initial and peak token count |
| `Scope` | `RateLimitScope` | `Shared` | `Shared` = singleton per interface; `Instance` = per proxy |

### `[CircuitBreaker]`

```csharp
[CircuitBreaker(MaxFailures = 5, ResetMs = 1_000, HalfOpenProbes = 1, Fallback = nameof(FetchFallback))]
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaxFailures` | `int` | `5` | Consecutive failures that trip Closed → Open |
| `ResetMs` | `int` | `1_000` | Delay before Open → HalfOpen |
| `HalfOpenProbes` | `int` | `1` | Successes required to close from HalfOpen |
| `Fallback` | `string?` | `null` | Fallback method name — called when circuit is Open |

---

## Method-level overrides

Apply attributes directly to methods to override the interface-level config for that method only:

```csharp
[Retry(MaxAttempts = 3, BackoffMs = 200)]
public interface IExternalService
{
    ValueTask<string> FetchAsync(string id, CancellationToken ct); // uses interface-level

    [Retry(MaxAttempts = 1)]   // POST is not idempotent — one attempt only
    ValueTask PostAsync(string data, CancellationToken ct);
}
```

Method-level attributes shadow interface-level ones entirely for that method — they are not additive.

---

## Failure surface

| Return type | On failure |
|-------------|-----------|
| `ValueTask<T>` / `Task<T>` | `ResilienceException` thrown with `Policy` property |
| `ValueTask<Result<T>>` / `Task<Result<T>>` | `Result.Failure(...)` returned — no throw |

```csharp
try
{
    var result = await service.FetchAsync("id", ct);
}
catch (ResilienceException ex) when (ex.Policy == ResiliencePolicy.CircuitBreaker)
{
    // circuit was open and no fallback was configured
}
```

---

## Diagnostics

| ID | Severity | Description |
|----|----------|-------------|
| [ZR0001](docs/diagnostics/ZR0001.md) | Error | Fallback method not found or signature mismatch |
| [ZR0002](docs/diagnostics/ZR0002.md) | Warning | Timeout configured but method has no `CancellationToken` |

---

## Documentation

Full docs live in [`docs/`](docs/index.md):

- [Getting Started](docs/getting-started.md)
- [Attribute Reference](docs/attributes.md)
- [Source Generator](docs/source-generator.md)
- [Performance](docs/performance.md)
- Core concepts: [Retry](docs/core-concepts/retry.md) · [Timeout](docs/core-concepts/timeout.md) · [Rate Limit](docs/core-concepts/rate-limit.md) · [Circuit Breaker](docs/core-concepts/circuit-breaker.md) · [Execution Order](docs/core-concepts/execution-order.md)
- Guides: [Fallback](docs/guides/fallback.md) · [Method-Level Overrides](docs/guides/method-level-overrides.md) · [Result Return Types](docs/guides/result-return-types.md) · [DI Registration](docs/guides/di-registration.md)

---

## License

MIT
