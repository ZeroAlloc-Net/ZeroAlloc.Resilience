---
id: circuit-breaker
title: Circuit Breaker
sidebar_position: 4
---

# Circuit Breaker

The `[CircuitBreaker]` policy prevents calls to a failing dependency from making things worse. After a configurable number of consecutive failures, the circuit opens and calls are rejected immediately. After a reset period, the circuit half-opens and a probe attempt is allowed through. A successful probe closes the circuit; a failed probe re-opens it.

---

## Configuration

```csharp
[CircuitBreaker(MaxFailures = 5, ResetMs = 1_000, HalfOpenProbes = 1, Fallback = nameof(FetchFallback))]
public interface IExternalService
{
    ValueTask<string> FetchAsync(string id, CancellationToken ct);
    ValueTask<string> FetchFallback(string id, CancellationToken ct);
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaxFailures` | `int` | `5` | Consecutive failures that trip Closed → Open |
| `ResetMs` | `int` | `1_000` | Milliseconds before Open → HalfOpen |
| `HalfOpenProbes` | `int` | `1` | Successes required to close from HalfOpen |
| `Fallback` | `string?` | `null` | Method name to call when circuit is Open |

---

## State machine

The circuit breaker is backed by `CircuitBreakerFsm` — a `ZeroAlloc.StateMachine` concurrent partial class:

```
Closed ──(MaxFailures failures)──▶ Open ──(ResetMs elapsed)──▶ HalfOpen
  ▲                                                                  │
  └──(HalfOpenProbes successes)──────────────────────────────────────┘
  
HalfOpen ──(any failure)──▶ Open
```

All state transitions use `Interlocked.CompareExchange` — safe for concurrent callers, zero allocation.

| State | Behaviour |
|-------|-----------|
| **Closed** | Normal — calls pass through; `CanExecute()` returns `true` |
| **Open** | Rejected — `CanExecute()` returns `false`; fallback or exception |
| **HalfOpen** | Probing — calls pass through; first failure re-opens |

---

## CanExecute

The proxy checks `CanExecute()` before every call:

```csharp
if (!_circuitBreaker.CanExecute())
{
    return await _inner.FetchFallback(id, ct).ConfigureAwait(false); // fallback configured
    // or: throw new ResilienceException(ResiliencePolicy.CircuitBreaker, "Circuit breaker is open.");
}
```

`CanExecute()` is a `Volatile.Read` on a single `long` — no lock, no allocation, sub-nanosecond on the fast-reject path.

---

## Failure and success tracking

The proxy calls `OnFailure` after each failed inner invocation and `OnSuccess` after each success:

```csharp
// Generated inside retry loop:
try
{
    var __result = await _inner.FetchAsync(id, __ct).ConfigureAwait(false);
    _circuitBreaker.OnSuccess();
    return __result;
}
catch (Exception __ex)
{
    _circuitBreaker.OnFailure(__ex);
    // ... retry logic
}
```

`OnSuccess` in Closed state resets the failure counter to zero. `OnSuccess` in HalfOpen state increments the probe success counter; when it reaches `HalfOpenProbes`, the circuit closes.

`OnFailure` in Closed state increments the failure counter; when it reaches `MaxFailures`, the circuit opens and a reset timer is scheduled. `OnFailure` in HalfOpen state immediately re-opens.

---

## Fallback

When `Fallback = nameof(SomeMethod)` is set, the generator emits a call to that method instead of throwing when the circuit is open:

```csharp
if (!_circuitBreaker.CanExecute())
    return await _inner.FetchFallback(id, ct).ConfigureAwait(false);
```

The fallback method is delegated as-is — it is not itself a resilience boundary. It is a passthrough method: called directly on `_inner` without any policy wrapping.

The fallback must have the same parameter types and return type as the annotated method. The generator validates this at compile time and emits **ZR0001** if it does not match.

---

## Without fallback

If no `Fallback` is configured and the circuit is open:

```csharp
throw new ResilienceException(ResiliencePolicy.CircuitBreaker, "Circuit breaker is open.");
```

For `Result<T>` return types, `Result.Failure("Circuit breaker is open.")` is returned instead.

---

## Thread safety

`CircuitBreakerPolicy` is thread-safe. All mutable state uses `Interlocked` operations:
- `_failureCount` — `Interlocked.Increment` / `Interlocked.Exchange`
- `_probeSuccessCount` — `Interlocked.Increment` / `Interlocked.Exchange`
- `_resetTimer` — `Interlocked.Exchange` for atomic timer swap
- FSM state — CAS loop in `CircuitBreakerFsm` (concurrent mode)

Register `CircuitBreakerPolicy` as a **singleton** so state is shared across all proxy instances and callers see a consistent circuit state.

---

## Tuning

| Scenario | Guidance |
|----------|----------|
| Aggressive tripping | Lower `MaxFailures` (e.g. 2–3) |
| Tolerant of flakiness | Higher `MaxFailures` (e.g. 10–20) |
| Fast recovery | Lower `ResetMs` (e.g. 100–500 ms) |
| Cautious recovery | Higher `ResetMs` (e.g. 30_000 ms) + higher `HalfOpenProbes` |
| Gradual warmup | `HalfOpenProbes = 3` — requires 3 consecutive successes to close |
