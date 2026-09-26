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
| `NonThrowing` | `bool` | `false` | Asserts the method returns `Result<T, ResilienceError>` or `UnitResult<ResilienceError>` |
| `RetryWhen` | `string?` | `null` | A static `bool M(E error)`: which failed Results to retry. See [Retrying failed Results](#retrying-failed-results) |
| `RetryOnException` | `string?` | `null` | A static `bool M(Exception exception)`: which exceptions to retry |
| `DelayHint` | `string?` | `null` | A static `TimeSpan? M(E error)` and/or `TimeSpan? M(Exception exception)`: the wait before the next attempt. See [Delay hints](#delay-hints-and-maxdelayms) |
| `MaxDelayMs` | `int` | `RetryPolicy.MaxBackoffMs`, no cap | The longest wait between attempts, for the backoff and a hint alike |

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

`InnerException` is the last exception thrown by the inner method. For async `Result` and `Result<T>` return types, a `Failure(lastException.Message)` of that type is returned instead, and `Result<T, ResilienceError>` gets a `ResilienceError` with `PolicyType = "Retry"` and the last exception. A `Result<T, E>` with any other `E` still throws here, because no inner Result exists; a Result the inner call returns is passed through unchanged. See [Result Return Types](../guides/result-return-types.md).

With `RetryWhen`, a method whose retries end on a failed Result returns that Result unchanged: the real final error, not a `ResilienceException` or a `ResilienceError`. The exhaustion above applies when the last attempt threw, or when `RetryOnException` declined the exception.

---

## What triggers a retry

Without `RetryWhen`, `RetryOnException` or `DelayHint`, every exception thrown by the inner call triggers a retry, except the caller's own cancellation. A Result the inner call returns, failed or not, is returned as is.

- **`RetryOnException`** names a static `bool M(Exception exception)`. When it returns `false`, the retries stop and the exhaustion behaviour applies at once.
- **`RetryWhen`** names a static `bool M(E error)`, where `E` is the error type of the method's Result. When it returns `true` for a failed Result, that Result is retried like an exception. See [Retrying failed Results](#retrying-failed-results).

The named methods are static methods of the interface or a base interface, checked at build time: [ZR0009](../diagnostics/ZR0009.md) when one is missing or has the wrong signature, [ZR0010](../diagnostics/ZR0010.md) when `RetryWhen` cannot apply to a method. They run outside the `try` that guards the inner call, so an exception they throw reaches the caller unchanged and is never retried.

If the total timeout fires, how the loop exits depends on when it fires:

- **During the inner call**, or **during a wait that follows a thrown exception**, the loop always exits through exhaustion: a `Failure`, a `ResilienceError` Result, or `ResilienceException`, built from the last exception.
- **During a wait that follows a failed Result** — only possible with `RetryWhen` — the loop returns that last failed Result unchanged, instead of going through exhaustion. See [Delay hints and `MaxDelayMs`](#delay-hints-and-maxdelayms).

In 3.1.0 and earlier, the exception-only loop let `TaskCanceledException` escape from the wait when the total timeout fired mid-backoff, instead of exiting through exhaustion. **This is a behaviour change**: a method with `[Retry]` and `[Timeout]` that uses none of the new properties now returns a `Failure` or throws `ResilienceException` where 3.1.0 threw `TaskCanceledException`.

```csharp
catch (Exception __ex)
{
    if (__totalCts.IsCancellationRequested)
    {
        ct.ThrowIfCancellationRequested(); // the caller's own cancellation wins over a fired timeout
        break; // total timeout expired — give up, exhaustion follows
    }
    // ... else retry
}
```

`ct.ThrowIfCancellationRequested()` is emitted only when the method has a `CancellationToken` parameter; without one, the block is just `break;`.

---

## Retrying failed Results

`RetryWhen` retries the failed Results it calls transient, such as an HTTP 429 or 503, and returns every other Result at once:

```csharp
[Retry(MaxAttempts = 4, BackoffMs = 500, MaxDelayMs = 30_000,
       RetryWhen = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
public interface IJevApi
{
    ValueTask<Result<ModelList, HttpError>> ModelsAsync(CancellationToken ct);

    static bool IsTransient(HttpError error) =>
        error.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;

    static TimeSpan? RetryAfter(HttpError error) => error.GetRetryAfter();
}
```

- A successful Result is returned.
- A failed Result `RetryWhen` returns `false` for, such as a 422, is returned after one attempt.
- A failed Result `RetryWhen` returns `true` for is retried. If every attempt ends that way, the last failed Result is returned unchanged.
- A thrown exception is handled as without `RetryWhen`.

`RetryWhen` applies to methods returning `Result`, `Result<T>`, `Result<T, E>` or `UnitResult<E>`, sync or async. On an interface-level `[Retry]`, a method it cannot apply to keeps exception-only retry, with a [ZR0010](../diagnostics/ZR0010.md) warning.

---

## Delay hints and `MaxDelayMs`

`DelayHint` names a static method, or an overload pair, that returns the wait before the next attempt:

- `TimeSpan? M(E error)` for a failed Result. It needs `RetryWhen`.
- `TimeSpan? M(Exception exception)` for a thrown exception. It applies to every method with retry.
- Nullable annotations are ignored when matching the parameter type, so an overload taking `HttpError?` or `Exception?` is accepted the same as one taking `HttpError` or `Exception`.

A non-null hint replaces the exponential backoff for that attempt. It gets no jitter, since the server asked for that exact wait, and it is rounded up to a whole millisecond; a negative hint means no wait. A `null` hint falls back to the backoff.

The hint runs on every failed attempt, including the last, before the loop checks whether attempts are exhausted. A hint that throws propagates unchanged, exactly like `RetryWhen` or `RetryOnException` — on the last attempt, that exception replaces the final Result or exhaustion the loop would otherwise have produced.

`MaxDelayMs` caps every wait, backoff and hint alike. It lives on `RetryPolicy`, so it can be changed at runtime like the other values:

```csharp
services.AddJevApiResilience<JevApi>((sp, p) =>
    p.Retry = new RetryPolicy(maxAttempts: 4, backoffMs: 500, jitter: false, perAttemptTimeoutMs: 0, maxDelayMs: 10_000));
```

Set `MaxDelayMs` whenever `DelayHint` reads a value from the server, such as a `Retry-After` header: `HttpError.GetRetryAfter()` (ZeroAlloc.Rest) clamps only at `int.MaxValue` seconds, far longer than any sane wait, so an explicit, smaller `MaxDelayMs` is the real cap.

The wait observes the total-timeout token when `[Timeout]` is present, and the caller's token otherwise. When the total timeout fires during a wait, the loop ends: a Result-aware method returns its last failed Result, as described in [What triggers a retry](#what-triggers-a-retry).

---

## Caller cancellation

When the caller's `CancellationToken` is cancelled, the proxy throws `OperationCanceledException`. The caller's cancellation is not retried, not counted as a circuit-breaker failure, and not wrapped in `ResilienceException`. The backoff wait observes the caller's token, so a cancelled caller does not wait out the backoff. A per-attempt or total timeout is not the caller's cancellation: it is still retried, or ends the loop, as described above.

In 3.1.0 and earlier, without `[Timeout]`, a cancelled caller waited out every backoff, the loop retried with a cancelled token until the attempts ran out, and the call ended with `ResilienceException`, or, for a Result return type, a `Failure` built from the cancellation. With `[Timeout]`, because the total-timeout token is linked to the caller's, cancelling the caller looked exactly like the total timeout firing: the loop broke at once, without waiting out the backoff, and still ended the same way — `ResilienceException` or a `Failure`.

---

## Interaction with other policies

- **Rate limit** — checked before the retry loop. If the bucket is empty, no attempt is made.
- **Circuit breaker** — `OnFailure` is called after each failed attempt; `OnSuccess` after each success. With `RetryWhen`, a failed Result it calls transient counts as a failure, and a failed Result it does not as a success, because the service answered. The circuit may open mid-retry if `MaxFailures` is reached.
- **Total timeout** — wraps the entire retry loop including backoff delays. `CancelAfter` is set before the first attempt; if it fires, the loop breaks.
- **Per-attempt timeout** — nested inside the retry loop, reset per attempt.

---

## Sync methods

For synchronous methods the retry loop has the same structure without `await`. The wait blocks on the delay token's `WaitHandle`, the total-timeout token under `[Timeout]` or else the caller's, so cancellation interrupts it. `Thread.Sleep` is used only when there is no token that can be cancelled.

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
