---
id: rate-limit
title: Rate Limit
sidebar_position: 3
---

# Rate Limit

The `[RateLimit]` policy limits how often the inner method can be called using a lock-free token-bucket algorithm. When the bucket is empty, the call is rejected immediately — no queuing, no waiting.

---

## Configuration

```csharp
[RateLimit(MaxPerSecond = 100, BurstSize = 10, Scope = RateLimitScope.Shared)]
public interface IExternalService
{
    ValueTask<string> FetchAsync(string id, CancellationToken ct);
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaxPerSecond` | `int` | required | Token refill rate — tokens added per second |
| `BurstSize` | `int` | `1` | Initial token count and maximum bucket size |
| `Scope` | `RateLimitScope` | `Shared` | `Shared` = one bucket per interface type; `Instance` = one bucket per proxy |

---

## Token bucket model

The bucket starts full (`BurstSize` tokens). Each call consumes one token. Tokens refill at `MaxPerSecond` tokens per second, up to `BurstSize`.

```
Initial:   [■■■■■■■■■■]  (BurstSize = 10 tokens)
After 10 calls: []  (empty)
After 100ms: [■]  (MaxPerSecond = 100 → 10 tokens/100ms)
```

If `TryAcquire()` finds zero tokens, the call is rejected:

```csharp
if (!_rateLimiter.TryAcquire())
    throw new ResilienceException(ResiliencePolicy.RateLimit, "Rate limit exceeded.");
```

The rejection is instantaneous — the inner method is never invoked.

---

## Lock-free implementation

`TryAcquire` uses `Interlocked.CompareExchange` on a single `long` token counter. No locks, no queues, no allocations:

1. Read the current token count with `Volatile.Read`.
2. If zero, return `false`.
3. CAS to decrement by one. If the CAS loses the race (another thread consumed a token concurrently), spin and retry.

Refill similarly uses CAS on a `lastRefillTick` field — only one thread wins the refill, preventing double-addition.

---

## Scope

### `RateLimitScope.Shared` (default)

One `RateLimiter` instance is registered as a **singleton** and shared by all proxy instances. This limits the combined call rate across your entire process:

```csharp
services.AddSingleton(new RateLimiter(100, 10, RateLimitScope.Shared));
```

Use this when you want a global cap on calls to the external service regardless of how many service consumers exist.

### `RateLimitScope.Instance`

The generated DI extension registers a **transient** `RateLimiter`. Each proxy instance gets its own bucket:

```csharp
services.AddTransient(sp => new RateLimiter(100, 10, RateLimitScope.Instance));
```

Use this when each consumer (e.g. each HTTP request) should get its own independent quota.

---

## Burst vs. steady-state

`BurstSize` determines how many calls can happen in an immediate burst before the rate limit kicks in.

```csharp
[RateLimit(MaxPerSecond = 10, BurstSize = 50)]
```

This allows up to 50 immediate calls, then replenishes at 10/s. Useful for handling short spikes without rejecting calls during normal usage.

Setting `BurstSize = MaxPerSecond` gives a fixed sliding window with no burst headroom. Setting `BurstSize = 1` is the strictest: at most one call per `1000/MaxPerSecond` ms.

---

## Failure on rejection

When the bucket is empty, `ResilienceException` is thrown with `Policy = ResiliencePolicy.RateLimit`. For `Result<T>` return types, `Result.Failure("Rate limit exceeded.")` is returned instead.

Rejections do not count toward the circuit breaker's failure counter — the inner call was never made.
