---
id: timeout
title: Timeout
sidebar_position: 2
---

# Timeout

The `[Timeout]` policy sets a wall-clock deadline for the entire operation — including all retry attempts and the backoff delays between them. If the deadline expires, the operation is cancelled regardless of which attempt is currently running.

---

## Configuration

```csharp
[Timeout(Ms = 5_000)]
public interface IExternalService
{
    ValueTask<string> FetchAsync(string id, CancellationToken ct);
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Ms` | `int` | required | Total operation timeout in milliseconds |

---

## How it works

The generator emits a `CancellationTokenSource` that is linked to the caller's `CancellationToken` and cancelled after `Ms` milliseconds:

```csharp
// Generated
using var __totalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
__totalCts.CancelAfter(5_000);
```

The linked token `__totalCts.Token` is passed as the effective `CancellationToken` to all inner calls. This means:

1. The inner method can observe the timeout normally — it receives a cancellation token that fires at the deadline.
2. The caller's own cancellation token also fires the linked source — whichever expires first wins.

---

## Total vs. per-attempt timeout

Two timeout concepts are available and can be combined:

| Concept | Set via | Scope |
|---------|---------|-------|
| Total timeout | `[Timeout(Ms = 5_000)]` | Wraps the entire operation including all retries and backoff |
| Per-attempt timeout | `PerAttemptTimeoutMs` on `[Retry]` | Cancels each individual attempt independently |

When both are configured, the per-attempt CTS is linked to the total CTS:

```csharp
// Generated — per-attempt is linked to total
using var __attemptCts = CancellationTokenSource.CreateLinkedTokenSource(__totalCts.Token);
__attemptCts.CancelAfter(perAttemptMs);
var __ct = __attemptCts.Token;
```

The earliest deadline always wins. If the total budget expires mid-attempt, the attempt CTS fires immediately (because they are linked).

---

## Cancellation token requirement

A timeout only propagates if the method accepts a `CancellationToken` parameter. If a method has `[Timeout]` but no `CancellationToken`, the generator emits diagnostic **ZR0002** (warning) — the timeout cannot cancel the inner call.

```csharp
// ZR0002: no CancellationToken — timeout cannot propagate
[Timeout(Ms = 1_000)]
ValueTask<string> LegacyCallAsync(string id);
```

Add a `CancellationToken` parameter to fix this:

```csharp
ValueTask<string> LegacyCallAsync(string id, CancellationToken ct);
```

---

## Allocation note

Each call to a timeout-configured method allocates one `CancellationTokenSource` (the linked source). This is unavoidable — `CancellationTokenSource` is a heap object. For non-timeout methods, no allocation occurs on the happy path.

If you need zero allocation even for timeout paths, consider managing the `CancellationTokenSource` externally and passing a pre-cancelled token; or remove `[Timeout]` and rely on the caller to supply a pre-deadlined token.

---

## Timeout without retry

`[Timeout]` can be used without `[Retry]`. The generated code creates the total CTS, makes a single inner call, and rethrows or returns failures:

```csharp
[Timeout(Ms = 500)]
public interface IFastService
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);
}
```

Generated:
```csharp
using var __totalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
__totalCts.CancelAfter(500);
var __ct = __totalCts.Token;
return await _inner.GetAsync(id, __ct).ConfigureAwait(false);
```
