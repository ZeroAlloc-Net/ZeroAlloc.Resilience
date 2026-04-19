---
id: fallback
title: Fallback
sidebar_position: 1
---

# Fallback

A fallback method is called automatically when the circuit breaker is open, providing a degraded-but-useful response instead of throwing an exception.

---

## Declaring a fallback

Set `Fallback = nameof(YourMethod)` on `[CircuitBreaker]`:

```csharp
[CircuitBreaker(MaxFailures = 5, ResetMs = 1_000, HalfOpenProbes = 1, Fallback = nameof(FetchFallback))]
public interface IExternalService
{
    ValueTask<string> FetchAsync(string id, CancellationToken ct);

    // Fallback — same signature, no policy attributes
    ValueTask<string> FetchFallback(string id, CancellationToken ct);
}
```

When the circuit is open, the proxy calls `FetchFallback` directly on the inner implementation instead of throwing.

---

## Generated code

```csharp
// Generated for FetchAsync:
if (!_circuitBreaker.CanExecute())
    return await _inner.FetchFallback(id, ct).ConfigureAwait(false);
```

The fallback is a passthrough — it calls the inner implementation directly, with no policy wrapping. It is not retried, not rate-limited, and not circuit-breaker-tracked.

---

## Signature requirements

The fallback method must have:
- **The same parameter types** (in the same order) as the annotated method
- **The same return type**

The generator validates this at compile time. If the signature does not match, it emits **ZR0001** (error):

```
error ZR0001: Fallback method 'FetchFallback' on 'IExternalService' was not found
or its signature does not match method 'FetchAsync'.
```

---

## Fallback on method vs. interface level

`Fallback` is always specified on `[CircuitBreaker]`, which can be at either the interface level (applies to all policy methods) or method level (applies only to that method):

```csharp
// Interface-level: all methods use the same fallback
[CircuitBreaker(MaxFailures = 3, ResetMs = 500, HalfOpenProbes = 1, Fallback = nameof(GetFallback))]
public interface IService
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);
    ValueTask<string> GetFallback(string id, CancellationToken ct);
}

// Method-level: each method has its own fallback
[CircuitBreaker(MaxFailures = 3, ResetMs = 500, HalfOpenProbes = 1)]
public interface IService
{
    [CircuitBreaker(MaxFailures = 3, ResetMs = 500, HalfOpenProbes = 1, Fallback = nameof(FetchFallback))]
    ValueTask<string> FetchAsync(string id, CancellationToken ct);
    ValueTask<string> FetchFallback(string id, CancellationToken ct);

    [CircuitBreaker(MaxFailures = 3, ResetMs = 500, HalfOpenProbes = 1, Fallback = nameof(PostFallback))]
    ValueTask PostAsync(string data, CancellationToken ct);
    ValueTask PostFallback(string data, CancellationToken ct);
}
```

---

## Implementing the fallback

The fallback method is a normal interface method — implement it in your inner class:

```csharp
public sealed class ExternalServiceImpl : IExternalService
{
    public async ValueTask<string> FetchAsync(string id, CancellationToken ct)
    {
        // real call to the dependency
        return await _httpClient.GetStringAsync($"/api/{id}", ct);
    }

    public ValueTask<string> FetchFallback(string id, CancellationToken ct)
    {
        // degraded response from cache, local data, or default value
        return ValueTask.FromResult(_cache.GetOrDefault(id, "unavailable"));
    }
}
```

---

## What if no fallback is configured?

When the circuit is open and no `Fallback` is configured, the proxy throws:

```csharp
throw new ResilienceException(ResiliencePolicy.CircuitBreaker, "Circuit breaker is open.");
```

For `Result<T>` return types it returns `Result.Failure("Circuit breaker is open.")`.

---

## Fallback is not retriable

The fallback is called once. It is not wrapped in the retry loop, does not contribute to circuit breaker failure counts, and is not rate-limited. If the fallback itself throws, the exception propagates directly to the caller.

If you need a resilient fallback (e.g. a cache that might also fail), put the resilience policy on that dependency separately.
