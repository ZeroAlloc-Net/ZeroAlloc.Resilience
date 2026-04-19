---
id: retry
title: Retry
sidebar_position: 1
---

# Retry

The `[Retry]` policy re-invokes the inner method on failure, up to a configurable number of attempts, with exponential backoff between each retry.

---

## Configuration

```csharp
[Retry(MaxAttempts = 3, BackoffMs = 200, Jitter = true, PerAttemptTimeoutMs = 1_000)]
public interface IExternalService
{
    ValueTask<string> FetchAsync(string id, CancellationToken ct);
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaxAttempts` | `int` | `3` | Total attempts including the initial call |
| `BackoffMs` | `int` | `200` | Base backoff in ms; actual delay = `BackoffMs * 2^attempt` |
| `Jitter` | `bool` | `false` | Add random jitter of up to 50% of the base backoff |
| `PerAttemptTimeoutMs` | `int` | `0` | Cancel each attempt after this many ms; 0 = disabled |

---

## Backoff schedule

With `BackoffMs = 200` and no jitter:

| Attempt | Delay before next |
|---------|------------------|
| 1 (initial) | — (no delay) |
| 2 (first retry) | 200 ms |
| 3 (second retry) | 400 ms |
| 4 (third retry) | 800 ms |

Formula: `BackoffMs * (1 << attempt)` where `attempt` is 0-based retry index.

With `Jitter = true`, each delay gains a random addition of `Random.Shared.Next(0, delay / 2)`. This spreads retries across time to prevent a "thundering herd" when many callers recover simultaneously.

---

## Per-attempt timeout

`PerAttemptTimeoutMs` creates a linked `CancellationTokenSource` per attempt. If the attempt does not complete within the limit, the CTS fires and the attempt is abandoned:

```csharp
// Generated pseudocode:
using var __attemptCts = CancellationTokenSource.CreateLinkedTokenSource(__totalCts.Token);
__attemptCts.CancelAfter(1_000);  // per-attempt timeout
var __ct = __attemptCts.Token;
var result = await _inner.FetchAsync(id, __ct);
```

The per-attempt CTS is linked to the total timeout CTS (if `[Timeout]` is also configured), so the earliest deadline wins.

---

## Exhaustion behaviour

When all attempts fail, the proxy throws:

```csharp
throw new ResilienceException(ResiliencePolicy.Retry, "All retry attempts failed.", lastException);
```

`InnerException` is the last exception thrown by the inner method. For `Result<T>` return types, a `Result.Failure(lastException.Message)` is returned instead.

---

## What triggers a retry

Everything. The generated catch block is unconditional — any `Exception` (except a `CancellationException` from a total-timeout cancellation) triggers a retry. There is no exception filter or predicate in v1.

If the total timeout CTS fires mid-retry, the loop exits immediately:

```csharp
catch (Exception __ex)
{
    if (__totalCts.IsCancellationRequested) break; // total timeout expired — give up
    // ... else retry
}
```

---

## Interaction with other policies

- **Rate limit** — checked before the retry loop. If the bucket is empty, no attempt is made.
- **Circuit breaker** — `OnFailure` is called after each failed attempt; `OnSuccess` after each success. The circuit may open mid-retry if `MaxFailures` is reached.
- **Total timeout** — wraps the entire retry loop including backoff delays. `CancelAfter` is set before the first attempt; if it fires, the loop breaks.
- **Per-attempt timeout** — nested inside the retry loop, reset per attempt.

---

## Sync methods

For synchronous methods (not `async`) the retry loop uses `Thread.Sleep` for backoff instead of `await Task.Delay`. The generated code is the same loop structure, just without `await`.

---

## Method-level override

```csharp
[Retry(MaxAttempts = 3, BackoffMs = 200)]
public interface IExternalService
{
    ValueTask<string> FetchAsync(string id, CancellationToken ct);

    [Retry(MaxAttempts = 1)]   // POST is not idempotent — one attempt only
    ValueTask PostAsync(string data, CancellationToken ct);
}
```

`PostAsync` gets `MaxAttempts = 1` (no retries), all other properties from the method-level attribute (defaults). The interface-level attribute is not merged.
