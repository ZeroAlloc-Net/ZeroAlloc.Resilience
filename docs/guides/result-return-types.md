---
id: result-return-types
title: Result Return Types
sidebar_position: 3
---

# Result Return Types

By default, policy failures throw `ResilienceException`. If your method returns `ValueTask<Result<T>>` or `Task<Result<T>>` (from `ZeroAlloc.Results`), the generator detects this and returns a failure result instead of throwing — giving you a non-throwing failure path.

---

## Opting in

Declare a `Result<T>` return type:

```csharp
using ZeroAlloc.Results;

[Retry(MaxAttempts = 3, BackoffMs = 200)]
public interface IExternalService
{
    // Non-Result: failures throw ResilienceException
    ValueTask<string> FetchAsync(string id, CancellationToken ct);

    // Result: failures returned as Result.Failure(...)
    ValueTask<Result<string>> FetchSafeAsync(string id, CancellationToken ct);
}
```

---

## Generated behaviour

For a `Result<T>` return type, each failure path emits a `Result.Failure(...)` instead of a `throw`:

```csharp
// Rate limit
if (!_rateLimiter.TryAcquire())
    return Result.Failure<string>("Rate limit exceeded.");

// Circuit breaker (when open and no fallback)
if (!_circuitBreaker.CanExecute())
    return Result.Failure<string>("Circuit breaker is open.");

// Retry exhaustion
return Result.Failure<string>(__lastEx?.Message ?? "All retry attempts failed.");

// Single-call failure (no retry)
catch (Exception __ex)
{
    return Result.Failure<string>(__ex.Message);
}
```

---

## Mixed interfaces

One interface can have both throwing and non-throwing methods:

```csharp
[Retry(MaxAttempts = 3, BackoffMs = 200)]
[CircuitBreaker(MaxFailures = 5, ResetMs = 1_000, HalfOpenProbes = 1)]
public interface IExternalService
{
    // Fire-and-forget style — exceptions are expected by the caller
    ValueTask<string> FetchAsync(string id, CancellationToken ct);

    // Result style — caller handles all outcomes explicitly
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
    { IsSuccess: true }  r => r.Value,
    { Error: var err }     => $"degraded: {err}"
};
```

---

## Fallback and Result

When `[CircuitBreaker(Fallback = ...)]` is set and the return type is `Result<T>`, the fallback path still calls `_inner.Fallback(...)` — the fallback method's return type must match. If the fallback is also `ValueTask<Result<T>>`, the result from the fallback is returned directly.

---

## Why not always use Result?

- **Interop**: libraries and frameworks expect exceptions. If the method is called by code you do not control, throwing `ResilienceException` is more compatible.
- **Stack traces**: exceptions carry stack traces that can be useful in logs. `Result.Failure` carries only a message.
- **Simplicity**: for fire-and-forget callers that want to crash loudly on failure, exceptions are simpler.

Use `Result<T>` when the caller is your own code and you want to express the outcome type in the signature rather than relying on exception handling for control flow.
