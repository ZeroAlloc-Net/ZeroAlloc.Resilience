---
id: method-level-overrides
title: Method-Level Overrides
sidebar_position: 2
---

# Method-Level Overrides

Policy attributes on an individual method **shadow** the interface-level config entirely for that method — they are not merged or additive. This lets each method have a different policy posture within the same interface.

---

## Basic override

```csharp
[Retry(MaxAttempts = 3, BackoffMs = 200, Jitter = true)]
[CircuitBreaker(MaxFailures = 5, ResetMs = 1_000, HalfOpenProbes = 1)]
public interface IExternalService
{
    // Uses interface-level Retry (3 attempts) and CircuitBreaker
    ValueTask<string> FetchAsync(string id, CancellationToken ct);

    // Overrides Retry — POST is not idempotent, so one attempt only
    // Still uses interface-level CircuitBreaker
    [Retry(MaxAttempts = 1)]
    ValueTask PostAsync(string data, CancellationToken ct);
}
```

`PostAsync` gets `MaxAttempts = 1` with all other `[Retry]` properties at their defaults (`BackoffMs = 200`, `Jitter = false`, `PerAttemptTimeoutMs = 0`). The interface-level `[Retry]` is not consulted for `PostAsync`.

---

## Per-policy override

Each policy is overridden independently. You can override only `[Retry]` while keeping the interface-level `[CircuitBreaker]`, or override all policies on a specific method:

```csharp
[Retry(MaxAttempts = 3, BackoffMs = 200)]
[Timeout(Ms = 5_000)]
[RateLimit(MaxPerSecond = 100, BurstSize = 10)]
[CircuitBreaker(MaxFailures = 5, ResetMs = 1_000, HalfOpenProbes = 1)]
public interface IExternalService
{
    // Uses all interface-level policies
    ValueTask<string> FetchAsync(string id, CancellationToken ct);

    // Override Retry and Timeout only — still gets RateLimit and CircuitBreaker from interface
    [Retry(MaxAttempts = 1)]
    [Timeout(Ms = 500)]
    ValueTask PostAsync(string data, CancellationToken ct);

    // Override everything
    [Retry(MaxAttempts = 5, BackoffMs = 50)]
    [Timeout(Ms = 10_000)]
    [RateLimit(MaxPerSecond = 10, BurstSize = 1)]
    [CircuitBreaker(MaxFailures = 2, ResetMs = 500, HalfOpenProbes = 2)]
    ValueTask<string> SlowFetchAsync(string id, CancellationToken ct);
}
```

---

## Removing a policy for one method

To remove a policy for a specific method, you cannot "un-apply" an interface attribute. Instead, use method-level attributes to express the desired config — and leave out any policy you do not want:

```csharp
[Retry(MaxAttempts = 3)]
[RateLimit(MaxPerSecond = 100, BurstSize = 10)]
public interface IExternalService
{
    // Both Retry and RateLimit applied
    ValueTask<string> FetchAsync(string id, CancellationToken ct);

    // Only RateLimit — no Retry on this method
    // (declare no [Retry] at method level; interface-level is not applied if method has
    //  no [Retry], because methods without ANY attributes get the interface-level config)
}
```

> **Note:** Method-level attributes shadow on a **per-policy basis**. A method that has no attributes at all inherits all interface-level policies. A method with `[Retry(MaxAttempts = 1)]` only gets its own Retry; it still inherits interface-level `[RateLimit]`, `[CircuitBreaker]`, etc.

---

## Baked as literals

Method-level policy values are baked as integer literals in the generated proxy code. This means each method's retry loop uses its own `MaxAttempts` and `BackoffMs` values directly — not a shared policy object:

```csharp
// PostAsync — MaxAttempts = 1, no loop
var __result = await _inner.PostAsync(data, __ct).ConfigureAwait(false);

// FetchAsync — MaxAttempts = 3
for (int __attempt = 0; __attempt < 3; __attempt++) { ... }
```

This also means that changing policy values in the DI registration does not affect generated code — the values come from the attributes, not the injected objects. The injected `RetryPolicy` object is used for type-keying in DI, not for reading values at call time.

---

## Common patterns

### Read vs. write

```csharp
[Retry(MaxAttempts = 3, Jitter = true)]
public interface IDataService
{
    ValueTask<string> ReadAsync(string key, CancellationToken ct);

    // Writes are not idempotent — no retry
    [Retry(MaxAttempts = 1)]
    ValueTask WriteAsync(string key, string value, CancellationToken ct);
}
```

### Different timeouts per method

```csharp
[Timeout(Ms = 5_000)]
public interface IApiService
{
    ValueTask<string> FastQueryAsync(string q, CancellationToken ct);

    // Long-running export — much longer timeout
    [Timeout(Ms = 60_000)]
    ValueTask<string> ExportAsync(string format, CancellationToken ct);
}
```

### Separate fallbacks per method

```csharp
[CircuitBreaker(MaxFailures = 3, ResetMs = 1_000, HalfOpenProbes = 1)]
public interface ICatalogService
{
    [CircuitBreaker(MaxFailures = 3, ResetMs = 1_000, HalfOpenProbes = 1, Fallback = nameof(SearchFallback))]
    ValueTask<string[]> SearchAsync(string query, CancellationToken ct);
    ValueTask<string[]> SearchFallback(string query, CancellationToken ct);

    [CircuitBreaker(MaxFailures = 3, ResetMs = 1_000, HalfOpenProbes = 1, Fallback = nameof(DetailFallback))]
    ValueTask<string> DetailAsync(string id, CancellationToken ct);
    ValueTask<string> DetailFallback(string id, CancellationToken ct);
}
```
