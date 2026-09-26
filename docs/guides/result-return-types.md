---
id: result-return-types
title: Result Return Types
sidebar_position: 3
---

# Result Return Types

By default, policy failures throw `ResilienceException`. If a method returns a `ZeroAlloc.Results` type, `Result`, `Result<T>`, `Result<T, E>` or `UnitResult<E>`, directly or inside `ValueTask<>` or `Task<>`, the generator detects it. Where it can build a failure of that type, it returns the failure instead of throwing. Synchronous and async methods follow the same rules.

What the generator can build depends on the error type:

| Return type | Error type | Policy failures |
|---|---|---|
| `Result` | `string` | returned as `Result.Failure(message)` |
| `Result<T>` | `string` | returned as `Result<T>.Failure(message)` |
| `Result<T, ResilienceError>` | `ResilienceError` | returned as `Result<T, ResilienceError>.Failure(new ResilienceError(...))` |
| `UnitResult<ResilienceError>` | `ResilienceError` | returned as `UnitResult<ResilienceError>.Failure(new ResilienceError(...))` |
| `Result<T, E>` or `UnitResult<E>` with any other `E` | your own type | the generator cannot build an `E`: see [Foreign error types](#foreign-error-types) |

In 1.3.6 and earlier, synchronous methods threw `ResilienceException` on every policy failure, and `UnitResult<E>` was not recognised at all. If you catch `ResilienceException` around a synchronous Result method, check the returned failure instead.

---

## Opting in

Declare a Result return type:

```csharp
using ZeroAlloc.Results;

[Retry(MaxAttempts = 3, BackoffMs = 200)]
public interface IExternalService
{
    // Non-Result: failures throw ResilienceException
    ValueTask<string> FetchAsync(string id, CancellationToken ct);

    // Result<T>: failures returned as Result<string>.Failure(message)
    ValueTask<Result<string>> FetchSafeAsync(string id, CancellationToken ct);

    // Result<T, ResilienceError>: failures carry the policy and the last exception
    ValueTask<Result<string, ResilienceError>> FetchTypedAsync(string id, CancellationToken ct);
}
```

---

## Generated behaviour

Every failure path returns a failure of the method's own Result type. For `ValueTask<Result<string>>`:

```csharp
// Rate limit
if (!_rateLimiter.TryAcquire())
    return global::ZeroAlloc.Results.Result<string>.Failure("Rate limit exceeded.");

// Circuit breaker, when open and no fallback is configured
if (!_circuitBreaker.CanExecute())
{
    return global::ZeroAlloc.Results.Result<string>.Failure("Circuit breaker is open.");
}

// Retry exhaustion
return global::ZeroAlloc.Results.Result<string>.Failure(__lastEx?.Message ?? "All retry attempts failed.");

// Single call, no retry: the catch records the exception, the failure is built after it
global::System.Exception __lastEx;
try
{
    var __result = await _inner.FetchSafeAsync(id, ct).ConfigureAwait(false);
    return __result;
}
catch (global::System.OperationCanceledException) when (ct.IsCancellationRequested)
{
    throw;
}
catch (global::System.Exception __ex)
{
    __lastEx = __ex;
}
return global::ZeroAlloc.Results.Result<string>.Failure(__lastEx.Message);
```

The non-generic `Result` gets the same code with `global::ZeroAlloc.Results.Result.Failure(...)`.

For `Result<string, ResilienceError>`, the failure carries the policy name and, when there is one, the exception:

```csharp
// Rate limit
return global::ZeroAlloc.Results.Result<string, global::ZeroAlloc.Resilience.ResilienceError>.Failure(
    new global::ZeroAlloc.Resilience.ResilienceError("RateLimit", "Rate limit exceeded."));

// Circuit breaker open
... new global::ZeroAlloc.Resilience.ResilienceError("CircuitBreaker", "Circuit breaker is open.")

// Retry exhaustion
... new global::ZeroAlloc.Resilience.ResilienceError("Retry", __lastEx?.Message ?? "All retry attempts failed.", __lastEx)

// Single call that threw
... new global::ZeroAlloc.Resilience.ResilienceError(__totalCts.IsCancellationRequested ? "Timeout" : "CircuitBreaker", __lastEx.Message, __lastEx)
```

`PolicyType` is the `ResiliencePolicy` member name. For a single call that threw, it is `Timeout` when the total timeout fired. Otherwise it names the policy guarding the call: `CircuitBreaker`, else `Timeout`, else `RateLimit`.

Without `RetryWhen`, a Result returned by the inner call, failed or successful, is passed through unchanged, and only a thrown exception is retried. With `RetryWhen`, see [Retrying failed Results](#retrying-failed-results).

---

## Caller cancellation

`OperationCanceledException` from the caller's own `CancellationToken` now propagates unchanged: on every method that has `[Retry]`, and on the single call, without `[Retry]`, that has `[Timeout]` or `[CircuitBreaker]`. It is not retried, not counted as a circuit-breaker failure, and not turned into a Result or `ResilienceException`.

This means a Result method under `[Timeout]` or `[CircuitBreaker]` that used to return `Failure("Timeout")` or `Failure("CircuitBreaker")` on caller cancellation now throws `OperationCanceledException` instead. **This is a behaviour change**: code that inspected the returned Result to detect caller cancellation must now catch the exception.

In 3.1.0 and earlier, without `[Timeout]`, a cancelled caller waited out every backoff, the loop retried with a cancelled token until the attempts ran out, and the call ended with `ResilienceException`, or, for a Result return type, a `Failure` built from the cancellation. With `[Timeout]`, because the total-timeout token is linked to the caller's, cancelling the caller looked exactly like the total timeout firing: the loop broke at once, without waiting out the backoff, and still ended the same way. A single call under `[Timeout]` or `[CircuitBreaker]` behaved identically, and the circuit breaker's `OnFailure` was called for the caller's own cancellation — it no longer is.

See [Retry: Caller cancellation](../core-concepts/retry.md#caller-cancellation) for the full rules.

---

## Foreign error types

For `Result<T, E>` with an `E` other than `ResilienceError`, such as `HttpError` from a ZeroAlloc.Rest client, the generator never invents an `E`. It returns the inner call's own Result whenever one exists:

| Policy | Supported | Behaviour |
|---|---|---|
| `[Retry]` | yes | A successful Result is returned unchanged. A failed Result is returned unchanged, unless `RetryWhen` calls it transient: then it is retried, and when every attempt ends that way the last failed Result is returned. A thrown exception is retried. If the last attempt threw, there is no Result to return, and `ResilienceException` with `Policy = Retry` is thrown, as for a non-Result method. |
| `[Timeout]` | yes | The timeout cancels the token passed to the inner call. Whatever the inner call returns is passed through. If it throws, the exception propagates, or is retried under `[Retry]`. |
| `[CircuitBreaker(Fallback = ...)]` | yes | While the circuit is open, the fallback's Result is returned. |
| `[CircuitBreaker]` without `Fallback` | no, [ZR0003](../diagnostics/ZR0003.md) | An open circuit has no Result to return. |
| `[RateLimit]` | no, [ZR0003](../diagnostics/ZR0003.md) | A rejected call has no Result to return. |
| `[Retry(NonThrowing = true)]` | no, [ZR0003](../diagnostics/ZR0003.md) | `NonThrowing` promises a `ResilienceError`, which `Result<T, E>` cannot hold. |

ZR0003 is an error, and no proxy is generated for that interface until it is fixed.

The `[RateLimit]` and `[CircuitBreaker]` rows apply to async `Result<T, E>`. A synchronous `Result<T, E>`, and a `UnitResult<E>` sync or async, keep throwing `ResilienceException` when the call is rejected, because they compiled before ZR0003 existed. `NonThrowing = true` reports ZR0003 for all of them.

```csharp
[Retry(MaxAttempts = 4, BackoffMs = 500)]
[Timeout(Ms = 30_000)]
public interface IJevApi
{
    // Supported: retry and timeout never need to build an HttpError
    ValueTask<Result<ModelList, HttpError>> ModelsAsync(CancellationToken ct);
}
```

---

## Retrying failed Results

A failed Result is retried when `[Retry]` names a `RetryWhen` predicate that calls it transient. `DelayHint` can take the wait from the failure, such as the server's `Retry-After`:

```csharp
using ZeroAlloc.Rest;
using ZeroAlloc.Results;

[Retry(MaxAttempts = 4, BackoffMs = 500, MaxDelayMs = 30_000,
       RetryWhen = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
[Timeout(Ms = 60_000)]
public interface IJevApi
{
    ValueTask<Result<ModelList, HttpError>> ModelsAsync(CancellationToken ct);

    static bool IsTransient(HttpError error) =>
        error.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;

    static TimeSpan? RetryAfter(HttpError error) => error.GetRetryAfter();
}
```

`HttpError.GetRetryAfter()` is in ZeroAlloc.Rest 2.1.0 and later. A 429 with `Retry-After: 2` waits two seconds, capped at `MaxDelayMs`, and a 422 is returned at once. When every attempt returns a transient failure, the last failed Result is returned unchanged. If the total timeout fires during a wait, the last failed Result is returned too. Always set `MaxDelayMs` when `DelayHint` reads a server header this way: `GetRetryAfter()` itself clamps only at `int.MaxValue` seconds.

A transient failed Result counts as a circuit-breaker failure, and a non-transient one as a success, because the service answered. A storm of 529 "Overloaded" responses therefore opens the circuit.

The predicate and the hints run outside the `try` that guards the inner call. An exception they throw reaches the caller unchanged and is never retried. See [Retry](../core-concepts/retry.md#retrying-failed-results) for the full rules.

---

## `NonThrowing`

`[Retry(NonThrowing = true)]` requires `Result<T, ResilienceError>` or `UnitResult<ResilienceError>`. It is now redundant, because every method returning one of those, sync or async, already returns failures instead of throwing.

---

## Mixed interfaces

One interface can have both throwing and non-throwing methods:

```csharp
[Retry(MaxAttempts = 3, BackoffMs = 200)]
[CircuitBreaker(MaxFailures = 5, ResetMs = 1_000, HalfOpenProbes = 1)]
public interface IExternalService
{
    // Exceptions are expected by the caller
    ValueTask<string> FetchAsync(string id, CancellationToken ct);

    // Result style: caller handles all outcomes explicitly
    ValueTask<Result<string>> TryFetchAsync(string id, CancellationToken ct);
}
```

---

## Handling results

```csharp
var result = await service.TryFetchAsync("id", ct);

if (result.IsSuccess)
{
    Console.WriteLine(result.Value);
}
else
{
    Console.WriteLine($"Failed: {result.Error}");
}
```

Or with pattern matching:

```csharp
var message = await service.TryFetchAsync("id", ct) switch
{
    { IsSuccess: true } r => r.Value,
    { Error: var err }    => $"degraded: {err}"
};
```

---

## Fallback and Result

When `[CircuitBreaker(Fallback = ...)]` is set, an open circuit calls `_inner.Fallback(...)` and returns its Result directly. The fallback must have the same signature, so its return type is the same Result type. This is the only way to use a circuit breaker with a foreign error type.

---

## Why not always use Result?

- **Interop**: libraries and frameworks expect exceptions. If the method is called by code you do not control, throwing `ResilienceException` is more compatible.
- **Stack traces**: exceptions carry stack traces that can be useful in logs. A `Result<T>` failure carries only a message. `ResilienceError` keeps the last exception in `InnerException`.
- **Simplicity**: for callers that want to crash loudly on failure, exceptions are simpler.

Use a Result type when the caller is your own code and you want to express the outcome in the signature rather than relying on exception handling for control flow.
