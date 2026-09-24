# Per-interface policy set — design

**Date:** 2026-09-24
**Issue:** [#144](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/issues/144)
**Release:** ZeroAlloc.Resilience 2.0.0 (breaking)

## Problem

#144 reports that the generated proxy takes `RetryPolicy` and `TimeoutPolicy` in its constructor but never reads them. Every value is an integer literal from the attributes, so a policy registered from configuration has no effect.

Investigating it turned up three more defects in the same wiring:

1. **Policies leak between interfaces.** `Add{Name}Resilience<TImpl>()` registers unkeyed singletons: `services.AddSingleton(new CircuitBreakerPolicy(...))`. Every interface resolves `GetRequiredService<CircuitBreakerPolicy>()`, which returns the last registration. A runtime probe confirmed it: with `IProbeA` (`MaxFailures = 1`, always throws) and `IProbeB` (`MaxFailures = 100`, always succeeds) registered in that order, one failure on A made B throw "Circuit breaker is open."
2. **Overrides lose.** `di-registration.md` says registering a policy before calling the extension overrides it. `AddSingleton` appends, so the extension's registration wins and the override is ignored. The same guide also says each interface gets independent policies, and the probe shows it does not.
3. **Method-level stateful policies are ignored.** The proxy has one `_circuitBreaker` and one `_rateLimiter`, built from the interface-level attribute, or from the first method's when there is none. A method-level `[CircuitBreaker(MaxFailures = 2)]` shares the interface instance and its thresholds. `RateLimitScope.Instance` is stored on `RateLimiter` but never acted on, so it behaves like `Shared`.

An additive fix, with keyed registrations beside the old unkeyed ones and literals kept for method overrides, would leave defects 2 and 3 in place and run two mechanisms side by side. This design takes a major version instead.

## Decisions

| Question | Decision |
|---|---|
| How does an application supply runtime values? | A `configure` callback with `IServiceProvider` access, so `IOptions` works. |
| How does the proxy receive its policies? | A generated policies class per interface, passed as one constructor argument. |
| Method-level overrides | Every override gets its own slot, which is configurable and has its own state. |
| Old unkeyed registrations | Removed. |
| Version | 2.0.0, as one `feat!:` PR. |

## Design

### 1. Generated policy set

For each interface the generator emits `{Name}ResiliencePolicies`, where `{Name}` is the interface name with a leading `I` trimmed, the same rule as `Add{Name}Resilience`. It goes in the interface's namespace and is `public` when the interface and every containing type are public, otherwise `internal`, which is the rule #146 introduced for the DI extension.

```csharp
public sealed class JevApiResiliencePolicies
{
    public global::ZeroAlloc.Resilience.RetryPolicy Retry { get; set; } = new(4, 500, false, 0);
    public global::ZeroAlloc.Resilience.TimeoutPolicy Timeout { get; set; } = new(30_000);
    public global::ZeroAlloc.Resilience.CircuitBreakerPolicy CircuitBreaker { get; set; } = new(5, 1_000, 1);
    public global::ZeroAlloc.Resilience.RetryPolicy ModelsAsyncRetry { get; set; } = new(1, 0, false, 0);
    public global::ZeroAlloc.Resilience.CircuitBreakerPolicy PostAsyncCircuitBreaker { get; set; } = new(2, 500, 1);
}
```

**Slots.**
- An interface-level slot named after the kind (`Retry`, `Timeout`, `RateLimiter`, `CircuitBreaker`) exists when the interface carries that attribute.
- A method slot named `{Method}{Kind}` exists for each method that carries its own attribute of that kind.
- A method uses its own slot when it has one, otherwise the interface slot.
- An interface with policies only on its methods has only method slots.
- Overloads: the first declaration of a name gets `{Method}{Kind}`, and later overloads get `{Method}2{Kind}`, `{Method}3{Kind}` and so on, in declaration order. If a name is already taken, for example by a method actually named `Get2`, the later slot takes the next free number.

**Defaults.** Every slot is initialised from its attribute values, so `new JevApiResiliencePolicies()` is a complete, valid configuration.

**Mutability.** Slots have public setters, so a `configure` callback can replace them. The proxy copies them at construction (section 3), so a change after that only affects proxies created later.

### 2. DI registration

The generator emits three extension methods in place of today's one:

```csharp
public static IServiceCollection AddJevApiResilience<TImpl>(this IServiceCollection services)
    where TImpl : class, IJevApi;

public static IServiceCollection AddJevApiResilience<TImpl>(
    this IServiceCollection services,
    Action<IServiceProvider, JevApiResiliencePolicies> configure)
    where TImpl : class, IJevApi;

// Registers only the policies, for hosts that build the proxy themselves: ZeroAlloc.Rest,
// Outbox, Scheduling.
public static IServiceCollection AddJevApiResiliencePolicies(
    this IServiceCollection services,
    Action<IServiceProvider, JevApiResiliencePolicies>? configure = null);
```

`AddJevApiResiliencePolicies` does:

```csharp
services.TryAddSingleton(sp =>
{
    var policies = new JevApiResiliencePolicies();
    configure?.Invoke(sp, policies);
    return policies;
});
```

`AddJevApiResilience<TImpl>` registers `TImpl` as transient, calls `AddJevApiResiliencePolicies(configure)`, and registers `IJevApi` as a transient factory: `new IJevApiResilienceProxy(sp.GetRequiredService<TImpl>(), sp.GetRequiredService<JevApiResiliencePolicies>())`. The parameterless overload forwards a no-op callback. It is a separate overload rather than an optional parameter, so no existing signature gains a parameter.

- `configure` runs once, when the singleton is first resolved.
- A `JevApiResiliencePolicies` instance the application registers itself takes precedence, because of `TryAdd`, and then `configure` does not run. The DI guide says so.
- No `RetryPolicy`, `TimeoutPolicy`, `RateLimiter` or `CircuitBreakerPolicy` is registered in the container.
- `AddJevApiResiliencePolicies` keeps an optional parameter because the method is new in 2.0.0.

The extension class keeps its #146 accessibility split: `ResilienceServiceCollectionExtensions` for public interfaces, and `InternalResilienceServiceCollectionExtensions` otherwise.

### 3. Proxy

```csharp
internal sealed class IJevApiResilienceProxy : IJevApi
{
    public IJevApiResilienceProxy(IJevApi inner, JevApiResiliencePolicies policies)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(policies);
        _inner = inner;
        _retry = policies.Retry ?? throw new ArgumentException("JevApiResiliencePolicies.Retry is null.", nameof(policies));
        // ... one readonly field per slot
    }
}
```

- **Snapshot.** Each slot is copied into a `readonly` field, and a null slot throws `ArgumentException` naming the slot.
- **`RateLimitScope.Instance`.** The constructor stores `slot.ForProxyInstance()`, a new public method on `RateLimiter`. It returns the limiter itself when `Scope == Shared`, and a new limiter with the same settings and the same `TimeProvider` when `Scope == Instance`, so each proxy gets its own budget. Two getters would not do here, because the copy would lose a custom `TimeProvider`.
- **Values at call time.** Every value is read from the field; nothing is a literal:
  - Retry: the loop bound is `_x.MaxAttempts`, the last-attempt check is `_x.MaxAttempts - 1`, and the delay is `_x.GetBackoffMs(__attempt)`. The generated jitter expression is removed.
  - Per-attempt timeout, emitted only for methods with a `CancellationToken`:
    ```csharp
    using var __attemptCts = _x.PerAttemptTimeoutMs > 0
        ? CancellationTokenSource.CreateLinkedTokenSource(__outer) : null;
    __attemptCts?.CancelAfter(_x.PerAttemptTimeoutMs);
    var __ct = __attemptCts?.Token ?? __outer;
    ```
    No CTS is allocated when the value is 0.
  - Total timeout: `__totalCts.CancelAfter(_x.TotalMs)`.
  - The circuit breaker and rate limiter calls are unchanged, but go to the method's own slot.
- **Structure comes from the attributes.** Whether a method gets a retry loop, a timeout CTS, a rate-limit check or a circuit check is decided at generation time from its attributes. Configuration tunes values; it cannot add or remove a policy.
- **Diagnostics.** ZR0002 keeps working from attribute data. ZR0001 and ZR0003 are unchanged.

### 4. Policy validation

Constructors throw `ArgumentOutOfRangeException` for:

| Type | Rule |
|---|---|
| `RetryPolicy` | `maxAttempts >= 1`, `backoffMs >= 0`, `perAttemptTimeoutMs >= 0` |
| `TimeoutPolicy` | `totalMs > 0` |
| `CircuitBreakerPolicy` | `maxFailures >= 1`, `resetMs >= 0`, `halfOpenProbes >= 1` |
| `RateLimiter` | `maxPerSecond >= 0`, `burstSize >= 0` |

Every value that is 0 in existing tests or packages stays legal. For example, ZeroAlloc.Saga's tests use `RateLimiter(maxPerSecond: 0, ...)`, and the benchmarks use `burstSize: 0` for a limiter that rejects every call. Invalid configuration fails when the policies are built: at `new JevApiResiliencePolicies()` for attribute values, and inside `configure` for configured ones. Today `MaxAttempts = 0` generates a loop that never calls the inner service, and `CancelAfter(0)` cancels immediately.

## Breaking changes (2.0.0)

1. The proxy constructor is `(inner, {Name}ResiliencePolicies)`.
2. `Add{Name}Resilience` no longer registers unkeyed policy singletons.
3. Policy constructors validate their arguments.
4. Method-level `[CircuitBreaker]` and `[RateLimit]` get their own instance and state, and `RateLimitScope.Instance` gives each proxy its own limiter.

The docs get a migration page with before and after for: plain `Add{Name}Resilience` (no change needed), manual proxy construction, the ZeroAlloc.Rest `resilienceFactory`, and hosts that activate the proxy through DI (Outbox, Scheduling).

## Docs to update

- `docs/guides/di-registration.md`: rewrite, removing the two false claims.
- `docs/guides/method-level-overrides.md`: replace "Baked as literals" with slots.
- `docs/source-generator.md`: generated output and example.
- `docs/core-concepts/*`: wherever values are described as literals.
- New `docs/guides/migrating-to-v2.md`.
- The XML docs on the policy types, for validation.

## Downstream follow-ups

Separate PRs, each after 2.0.0 is confirmed on NuGet. Confirm by reading the feed, not by a green release workflow.

| Repo | Change |
|---|---|
| ZeroAlloc.Rest | `docs/resilience.md` and `RestResilienceTests` use `sp.GetRequiredService<{Name}ResiliencePolicies>()` and `Add{Name}ResiliencePolicies()`. Folds into Rest #302. |
| ZeroAlloc.Outbox, ZeroAlloc.Scheduling | Docs and tests call `Add{Name}ResiliencePolicies()`; no source change, because the proxy is activated through DI. |
| ZeroAlloc.Templates (za-clean) | The `resilienceFactory` uses the policies type, and the hand-written `RetryPolicy`/`TimeoutPolicy` singletons are dropped. |
| ZeroAlloc.Mediator, ZeroAlloc.Saga | Bump the dependency and run the tests to confirm validation accepts their values. They construct policies but no proxies. |

## Testing

Runtime, in `ZeroAlloc.Resilience.Tests`:
- Two interfaces with `[CircuitBreaker]` do not share state (the probe above as a test).
- Values set in `configure` are honoured for `MaxAttempts`, backoff, per-attempt timeout and total timeout (the #144 probe as a test).
- A method-level `[CircuitBreaker]` opens independently of the interface-level one.
- `RateLimitScope.Instance` gives each resolved proxy its own budget.
- A pre-registered policies instance wins, and `configure` does not run.
- A proxy built without DI from `new {Name}ResiliencePolicies()` works.
- An invalid value in `configure` throws `ArgumentOutOfRangeException` when the policies are resolved.
- A null slot throws `ArgumentException` naming the slot.
- Policy constructor validation, one test per rule.

Generator, in `ZeroAlloc.Resilience.Generator.Tests`:
- The policy class's accessibility follows the interface's.
- Overloads get suffixed slot names, and the output compiles.
- A method-only interface gets only method slots.
- A partial interface compiles.
- All four snapshots are regenerated.

The existing Result, accessibility and method-level tests keep passing after mechanical updates to their constructor calls. `samples/ZeroAlloc.Resilience.AotSmoke` is updated and still publishes with no AOT warnings. The benchmarks are updated to the new constructor.

## Out of scope

- A generated options class bound directly from `IConfiguration`: `configure` with `IOptions` covers it.
- Keyed registrations for several differently configured instances of one interface.
- `Result<T, string>` and `UnitResult<string>` as string-error types (follow-up noted on #151).
