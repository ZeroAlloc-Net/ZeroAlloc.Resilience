# Result-aware retry: design

**Issues:** #142 (retry a failed `Result`, with a predicate choosing which failures are transient) and #143 (take the retry delay from the failure, such as `Retry-After`). **Release:** ZeroAlloc.Resilience 3.2.0, a minor. **Status:** approved 2026-09-26.

This is sub-project B of the Result-integration cluster:
- Sub-project A, ZeroAlloc.Rest 2.1.0, added `HttpError.Body`, `[ErrorMapper]` and `HttpError.GetRetryAfter()`. `DelayHint` here is designed to be a one-line call to that helper.
- Sub-project C, ZeroAlloc.Telemetry, adds metrics read from the result.

## Problem

- **Failed Results are never retried.** The generated retry loop reacts only to thrown exceptions. A method returning `Result<T, E>` whose inner call returns a failure, such as an HTTP 429, gets that failure back at once, with no retry.
- **Every exception is retried.** There is no way to say which failures are transient, so a non-transient 422 thrown as an exception is retried too.
- **The server's delay can't be used.** Backoff is `BackoffMs * 2^attempt` plus optional jitter. When the server says how long to wait, for example with `Retry-After`, there is no way to use that.
- **The breaker counts failed Results as successes.** The circuit breaker counts every returned value, including a failed Result, as a success. A storm of 529 "Overloaded" responses resets the breaker instead of opening it.

## Current state, 3.1.0

- **`RetryAttribute`** has `MaxAttempts`, `BackoffMs`, `Jitter`, `PerAttemptTimeoutMs` and `NonThrowing`. `NonThrowing` only asserts the return type.
- **Runtime policy objects.** Values flow into a runtime `RetryPolicy(maxAttempts, backoffMs, jitter, perAttemptTimeoutMs)` held in a generated `{Name}ResiliencePolicies` set. They can be overridden per slot at runtime. Anything decided in generated code is fixed at compile time.
- **The generated loop** (`ResilienceWriter.cs`) wraps the inner call in `try`. A returned value, whether it succeeded or failed, calls `breaker.OnSuccess()` and returns. The `catch (Exception)` records `__lastEx`, calls `breaker.OnFailure(ex)`, and delays with `Task.Delay(_retry.GetBackoffMs(attempt), __totalCts.Token)`. The token is passed only when `[Timeout]` is present; sync methods use `Thread.Sleep`.
- **When every attempt fails,** a `ResilienceError` or `string` Result gets a Failure, and everything else throws `ResilienceException(ResiliencePolicy.Retry, …, __lastEx)`.
- **`CircuitBreakerPolicy.OnFailure(Exception)`** ignores its argument. There is no overload without an exception.
- **An existing bug:** with no `[Timeout]`, the backoff delay ignores the caller's `CancellationToken`. The `catch (Exception)` also catches the caller's own `OperationCanceledException`. A cancelled caller therefore waits out the backoff, and the loop retries with a cancelled token until the attempts run out. It ends with `ResilienceException` instead of `OperationCanceledException`.
- **`Fallback`** is the one existing string-named member reference. It is resolved by `FindFallback` across the interface and its base interfaces, and ZR0001 reports a miss.
- **Diagnostics:** ZR0001 to ZR0008 are used, and ZR0005 is retired.

## Decisions

- **Predicates are static methods named on the attribute,** found at compile time the way `Fallback` is. They see the typed `E`, which a non-generic `RetryPolicy` can't carry. The generator can check them, and they allocate nothing.
- **`MaxDelayMs` lives on `RetryPolicy`,** through a new constructor overload, so it can be overridden at runtime like the other values. The existing constructor keeps its exact signature, because adding an optional parameter to a shipped signature breaks binary compatibility.
- **The breaker sees what the predicate sees.** When `RetryWhen` is set, a failed Result it calls transient counts as a breaker failure, and a non-transient failed Result counts as a success, because the service answered.
- **One PR** closes #142 and #143. #143's Result path needs #142's loop.

## Public API

These are all additive.

```csharp
public sealed class RetryAttribute
{
    // existing members unchanged
    public string? RetryWhen { get; init; }        // static bool M(E error)
    public string? RetryOnException { get; init; } // static bool M(Exception exception)
    public string? DelayHint { get; init; }        // static TimeSpan? M(E error) and/or static TimeSpan? M(Exception exception)
    public int MaxDelayMs { get; init; } = RetryPolicy.MaxBackoffMs;   // default: no cap
}

public sealed class RetryPolicy
{
    // existing constructor unchanged
    public RetryPolicy(int maxAttempts, int backoffMs, bool jitter, int perAttemptTimeoutMs, int maxDelayMs);
    public int MaxDelayMs { get; }
    public int GetDelayMs(int attempt, TimeSpan? hint);   // min(hint ?? GetBackoffMs(attempt), MaxDelayMs), hint < 0 → 0
}

public sealed class CircuitBreakerPolicy
{
    public void OnFailure();   // a failure with no exception
}
```

- **What `E` is.** It is the error type of the method's `Result<T, E>` or `UnitResult<E>`: a foreign type such as `HttpError`, or `string`, or `ResilienceError`.
- **Where the methods are looked up.** Each name refers to a static method on the interface or one of its base interfaces. It must be accessible from the generated proxy.
- **`DelayHint` overloads.** `DelayHint` may name an overload set. The `E` overload serves failed Results, and the `Exception` overload serves thrown exceptions. Either may be absent.
- **Validation.** The new constructor validates `maxDelayMs >= 0`, like the other constructor arguments. The attribute's `MaxDelayMs` gets a ZR0004 rule for the same bound.
- **Hint delays.** `GetDelayMs` adds no jitter to a hint, because the server asked for that exact wait.

## Generated loop

For a method returning `Result<T, E>` or `UnitResult<E>` whose effective `[Retry]` has a `RetryWhen` that applies:

```
for each attempt:
    set up the attempt token, as today
    try   { __result = await inner(..., __ct) }          only the inner call is inside the try
    catch (OperationCanceledException) when callerToken.IsCancellationRequested { throw; }
    catch (Exception ex) { exception path, below; then continue or exit }
    if __result.IsSuccess                   → breaker.OnSuccess(); return __result
    if !RetryWhen(__result.Error)           → breaker.OnSuccess(); return __result
    breaker.OnFailure()
    __lastResult = __result; __lastWasResult = true
    __hint = DelayHint(__result.Error)      only when an E overload exists
    if last attempt, or the total timeout has fired → exit
    wait _retry.GetDelayMs(attempt, __hint) on the delay token
exit:
    if __lastWasResult → return __lastResult     the real final error, unchanged
    else               → today's exhaustion: a Failure or ResilienceException
```

- **Predicates and hints run outside the `try`.** An exception thrown by `RetryWhen`, `RetryOnException` or `DelayHint` propagates to the caller unchanged. It is never caught and retried as if it were a failure of the inner call.
- **Exception path.** It records `__lastEx`, sets `__lastWasResult = false`, and calls `breaker.OnFailure(ex)` as today.
  - If `RetryOnException` is set and returns false, the loop exits through the exhaustion path immediately.
  - Otherwise `__hint` comes from the `Exception` overload of `DelayHint`, if there is one. The loop then continues to the same last-attempt, timeout and delay steps.
- **Delay token.** The wait observes the total-timeout token when `[Timeout]` is present, and the caller's token otherwise.
  - If the total timeout fires during a wait, the loop exits. A Result method returns `__lastResult`. The exception path keeps today's behaviour.
  - If the caller's token fires, `OperationCanceledException` propagates.
- **Sync methods** follow the same logic. The wait blocks on the delay token's wait handle for the delay time, so cancellation interrupts it too. `Thread.Sleep` would ignore cancellation.
- **Where the new properties don't apply,** the loop keeps today's shape. Without `RetryWhen`, a returned Result is passed through and counts as a breaker success, exactly as today. `RetryOnException` and the `Exception` overload of `DelayHint` apply to every method with retry, Result-returning or not.

### Caller-cancellation fix, applies to every method with retry

- The caller's `OperationCanceledException` is rethrown at once. It is not retried, and it is not wrapped in `ResilienceException`.
- The backoff wait observes the caller's token even when there is no `[Timeout]`.
- This is the only change to methods that don't use the new properties, and it ships as a `fix:`.

## Diagnostics

- **ZR0009, Error.** `RetryWhen`, `RetryOnException` or `DelayHint` names no accessible static method with the required signature on the interface or its base interfaces. The message states the expected signature, for example `static bool IsTransient(HttpError)`. It is reported at the attribute.
- **ZR0010.** `RetryWhen`, or an `E`-typed `DelayHint`, can't apply to a method, because the method returns no Result, or its `E` doesn't match the method's parameter type.
  - On a method-level `[Retry]` it is an **Error**, reported at the method.
  - On an interface-level `[Retry]` it is a **Warning** for each method it skips, like ZR0006. Those methods keep exception-only retry. One interface-level policy can then serve a mix of Result and non-Result methods.
- **ZR0004** gains a rule: `MaxDelayMs >= 0`.

## Tests

**Generator:**
- Snapshots of the new loop: async, sync and `UnitResult`; with and without `[Timeout]` and `[CircuitBreaker]`; with `DelayHint` overloads.
- ZR0009, one case per signature mistake: missing, not static, wrong parameter, wrong return type.
- ZR0010 as an Error on a method-level `[Retry]` and as a Warning on an interface-level one.
- ZR0004 for a negative `MaxDelayMs`.
- The generated output is unchanged apart from the cancellation fix when no new property is used.

**Runtime:**
- A 429 followed by a success takes two attempts.
- A 422 is returned after one attempt.
- When every attempt returns 429, the last failed Result is returned unchanged.
- A throwing `RetryWhen` propagates and is not retried.
- A hint overrides the backoff and is capped by `MaxDelayMs`.
- The `Exception` hint overload is used on the exception path.
- `RetryOnException` returning false stops the retries.
- The breaker opens on transient failed Results but not on non-transient ones.
- The total timeout interrupts a long hinted wait and returns the last Result.
- Caller cancellation propagates as `OperationCanceledException`, with and without `[Timeout]`, async and sync.
- The `GetDelayMs` and `OnFailure()` unit tests.
- An AOT smoke case.

## Docs

- `docs/guides/result-return-types.md`: replace the "returned failures are not retried yet, #142" note with Result-aware retry, and update the per-policy table.
- `docs/core-concepts/retry.md` and `docs/attributes.md`: document the four new properties, the delay rules and the breaker interaction.
- New pages `docs/diagnostics/ZR0009.md` and `ZR0010.md`, plus their rows in the `docs/source-generator.md` table.
- **Follow-up in ZeroAlloc.Rest after this ships:** its `docs/resilience.md` table says `[Retry]` passes Results through. Update it, and add the `DelayHint` + `HttpError.GetRetryAfter()` recipe.

## Delivery

- **One PR** closes #142 and #143, released as 3.2.0.
- **Changelog.** The PR body carries a `BEGIN_COMMIT_OVERRIDE` block listing:
  - `feat:` retry a failed Result chosen by `RetryWhen`, #142;
  - `feat:` take the retry delay from the failure, #143;
  - `fix:` propagate caller cancellation from the retry loop.
- **Files.** New public API goes in `PublicAPI.Unshipped.txt`. api-compat must pass with no suppressions.
- **Commit bodies.** Lines are at most 100 characters, with no nested parentheses.
- **After merge,** confirm the release PR lists all three entries.
