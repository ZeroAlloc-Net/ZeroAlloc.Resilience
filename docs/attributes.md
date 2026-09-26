---
id: attributes
title: Attributes
sidebar_position: 3
---

# Attributes

All attributes target `Interface` and `Method`. Method-level declarations shadow interface-level ones entirely for that method — they are not additive.

## [Retry]

| Property | Type | Default | Description |
|---|---|---|---|
| `MaxAttempts` | `int` | `3` | Total attempts (initial + retries) |
| `BackoffMs` | `int` | `200` | Base backoff (exponential: 200, 400, 800…) |
| `Jitter` | `bool` | `false` | Add random jitter up to 50% of base |
| `PerAttemptTimeoutMs` | `int` | `0` | Per-attempt timeout (0 = disabled) |
| `NonThrowing` | `bool` | `false` | Asserts a `ResilienceError` Result return type |
| `RetryWhen` | `string?` | `null` | Static `bool M(E error)`: retry the failed Results it returns `true` for; a transient one is a breaker failure, any other a success |
| `RetryOnException` | `string?` | `null` | Static `bool M(Exception exception)`: `false` stops the retries |
| `DelayHint` | `string?` | `null` | Static `TimeSpan? M(E error)` and/or `TimeSpan? M(Exception exception)`: the next wait, without jitter |
| `MaxDelayMs` | `int` | no cap | Longest wait between attempts, backoff or hint |

The named methods are static methods of the interface or a base interface; see [Retry](core-concepts/retry.md#what-triggers-a-retry).

## [Timeout]

| Property | Type | Default | Description |
|---|---|---|---|
| `Ms` | `int` | required | Total operation timeout (wraps all retries + backoff) |

## [RateLimit]

| Property | Type | Default | Description |
|---|---|---|---|
| `MaxPerSecond` | `int` | required | Tokens added per second |
| `BurstSize` | `int` | `1` | Initial and maximum token count |
| `Scope` | `RateLimitScope` | `Shared` | `Shared` = singleton per interface type; `Instance` = per proxy |

## [CircuitBreaker]

| Property | Type | Default | Description |
|---|---|---|---|
| `MaxFailures` | `int` | `5` | Consecutive failures that trip to Open |
| `ResetMs` | `int` | `1000` | Delay before Open → HalfOpen probe |
| `HalfOpenProbes` | `int` | `1` | Successes required to close from HalfOpen |
| `Fallback` | `string?` | `null` | Method name to call when circuit is Open |
