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

One `RateLimiter` is shared by every proxy built from the same `{Name}ResiliencePolicies` singleton — one bucket per interface, or one bucket per method when a method carries its own `[RateLimit]` override. The proxy constructor stores the `RateLimiter` from the slot directly.

Use this when you want a global cap on calls to the external service regardless of how many proxy instances exist.

### `RateLimitScope.Instance`

Each proxy instance gets its own bucket. The proxy constructor calls `ForProxyInstance()` on the slot's `RateLimiter`: for `Shared` it returns the same instance, and for `Instance` it returns a new `RateLimiter` with the same `MaxPerSecond`, `BurstSize` and `TimeProvider`, so a custom `TimeProvider` is preserved. This happens once, when the proxy is constructed — resolving the same `IExternalService` from DI twice (transient registration) gives two proxies with two independent buckets, even though both read from the same policies singleton.

Use this when each consumer (e.g. each HTTP request, if the proxy is resolved per request) should get its own independent quota.

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

When the bucket is empty, `ResilienceException` is thrown with `Policy = ResiliencePolicy.RateLimit`. For async `Result` and `Result<T>` return types, a `Failure("Rate limit exceeded.")` of that type is returned instead, and `Result<T, ResilienceError>` gets a `ResilienceError` with `PolicyType = "RateLimit"`. A `Result<T, E>` with any other `E` cannot be rate limited; see [Result Return Types](../guides/result-return-types.md).

Rejections do not count toward the circuit breaker's failure counter — the inner call was never made.
