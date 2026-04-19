---
id: execution-order
title: Execution Order
sidebar_position: 5
---

# Execution Order

When multiple policies are stacked on an interface or method, they execute in a fixed order regardless of the order the attributes are written:

```
RateLimit → CircuitBreaker → Timeout → Retry (with PerAttemptTimeout inside)
```

---

## Why this order

Each policy is designed to guard the next:

1. **RateLimit first** — cheapest check. If the bucket is empty, reject immediately. No circuit breaker logic, no timeout allocation, no inner call.

2. **CircuitBreaker second** — `CanExecute()` is a single `Volatile.Read`. If the circuit is open, invoke the fallback or throw. No timeout CTS created, no retry loop entered.

3. **Timeout third** — creates a `CancellationTokenSource` that wraps the remainder of the operation. Placed before the retry loop so the deadline covers all attempts and backoff delays combined.

4. **Retry innermost** — the retry loop calls the inner method. PerAttemptTimeout (if configured) nests inside the loop as an additional linked CTS per attempt.

---

## Illustrated

```
call enters
    │
    ▼
[RateLimit.TryAcquire()] ── false ──▶ ResilienceException(RateLimit) or Result.Failure
    │ true
    ▼
[CircuitBreaker.CanExecute()] ── false ──▶ Fallback(args) or ResilienceException(CircuitBreaker)
    │ true
    ▼
[CancellationTokenSource.CancelAfter(totalMs)]  ← only if [Timeout] configured
    │
    ▼
for attempt in 0..MaxAttempts:
    │
    ├──[CancelAfter(perAttemptMs)]  ← only if PerAttemptTimeoutMs > 0
    │
    ├──[inner.Method(args, __ct)]
    │      │ success
    │      ├──[CircuitBreaker.OnSuccess()]
    │      └──▶ return result
    │      │ failure
    │      ├──[CircuitBreaker.OnFailure(ex)]
    │      ├── if total timeout expired → break
    │      ├── if last attempt → break
    │      └──[Task.Delay(backoff)]
    │
    └── (next attempt)

▼
throw ResilienceException(Retry, lastEx) or return Result.Failure
```

---

## Attribute order does not matter

The execution order is fixed by the generator regardless of the order attributes are written:

```csharp
// These two are identical in behaviour:
[Retry][CircuitBreaker][RateLimit][Timeout]
[RateLimit][CircuitBreaker][Retry][Timeout]
```

The generated code always produces: rate-limit check, then circuit-breaker check, then timeout CTS, then retry loop.

---

## Circuit breaker wraps the retry loop

`CircuitBreaker.OnFailure` is called inside the retry loop after each failed attempt. This means the circuit breaker counts individual attempt failures, not operation failures. With `MaxAttempts = 3` and `MaxFailures = 5`, two full retry-exhaustions (2 × 3 = 6 call attempts) will trip the circuit.

`CircuitBreaker.OnSuccess` is called on the first successful attempt. A successful retry resets the failure counter, regardless of how many preceding attempts failed.

---

## Timeout covers the entire operation

The total timeout CTS is created once before the retry loop and covers:
- All individual attempts
- All backoff delays between attempts
- The per-attempt timeout CTSes (via linking)

If the total timeout fires during a `Task.Delay(backoff)`, the delay is cancelled and the loop exits. If it fires during an inner call, the per-attempt CTS fires immediately (linked), which cancels the inner call.

---

## Policy combination examples

| Attributes | Generated behaviour |
|-----------|---------------------|
| `[Retry]` only | Retry loop, no timeout, no rate limit, no circuit |
| `[Timeout]` only | Single attempt with total deadline |
| `[CircuitBreaker]` only | Single attempt, fast-reject if open, CB tracking |
| `[Retry][Timeout]` | Retry loop with total deadline; per-attempt timeout if `PerAttemptTimeoutMs > 0` |
| `[Retry][CircuitBreaker]` | Retry loop, CB tracking per attempt, fast-reject if circuit opens mid-retry |
| `[RateLimit][CircuitBreaker]` | Rate-limit check → CB check → single attempt |
| All four | Full pipeline: rate-limit → CB → total-timeout CTS → per-attempt-timeout → retry loop |
