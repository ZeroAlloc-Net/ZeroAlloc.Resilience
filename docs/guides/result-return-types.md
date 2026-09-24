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

A Result returned by the inner call, failed or successful, is always passed through unchanged. Only a thrown exception is retried.

---

## Foreign error types

For `Result<T, E>` with an `E` other than `ResilienceError`, such as `HttpError` from a ZeroAlloc.Rest client, the generator never invents an `E`. It returns the inner call's own Result whenever one exists:

| Policy | Supported | Behaviour |
|---|---|---|
| `[Retry]` | yes | A returned Result, failed or successful, is returned unchanged. A thrown exception is retried. If every attempt throws, there is no Result to return, and `ResilienceException` with `Policy = Retry` is thrown, as for a non-Result method. |
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

Returned failures such as an HTTP 429 are not retried yet, because the retry loop reacts only to exceptions. Retrying on selected failed Results is tracked in [#142](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/issues/142).

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
