---
id: migrating-to-v2
title: Migrating to 2.0
sidebar_position: 5
---

# Migrating to 2.0

2.0 changes how policies reach the generated proxy. Each interface now has a generated
`{Name}ResiliencePolicies` class, and the proxy reads every value from it at call time.

## If you only call `Add{Name}Resilience<TImpl>()` and never resolve a policy type yourself

Nothing to change. Your policies are now isolated per interface and configurable.

## If you registered policy objects yourself

In 1.x the proxy factory resolved the unkeyed `RetryPolicy`, `TimeoutPolicy`, `RateLimiter` and
`CircuitBreakerPolicy` from the container, so an application that registered its own instance saw
it used. In 2.0 the proxy never reads those types from the container — it only reads its
generated `{Name}ResiliencePolicies` singleton. Code that resolves the policy types directly, for
example `GetRequiredService<RetryPolicy>()` after calling `Add{Name}Resilience`, now throws
because nothing registers them.

```csharp
// 1.x — the proxy picked this up from the container
services.AddSingleton(new RetryPolicy(5, 100, false, 0));
services.AddJevApiResilience<JevApi>();

// 2.0
services.AddJevApiResilience<JevApi>((sp, p) => p.Retry = new RetryPolicy(5, 100, false, 0));
```

Resolving `RetryPolicy`, `TimeoutPolicy`, `RateLimiter` or `CircuitBreakerPolicy` from the
container now fails unless you register them yourself; the proxy never reads them.

## Configuring values at runtime

```csharp
services.AddJevApiResilience<JevApi>((sp, p) =>
    p.Retry = new RetryPolicy(maxAttempts: 6, backoffMs: 250, jitter: true, perAttemptTimeoutMs: 0));
```

## Constructing the proxy yourself

```csharp
// 1.x
var proxy = new IJevApiResilienceProxy(inner, retryPolicy, timeoutPolicy);

// 2.0
var proxy = new IJevApiResilienceProxy(inner, new JevApiResiliencePolicies
{
    Retry = retryPolicy,
    Timeout = timeoutPolicy,
});
```

## ZeroAlloc.Rest `resilienceFactory`

```csharp
// 1.x
services.AddSingleton(new RetryPolicy(3, 200, false, 0));
services.AddRestResilience<IPaymentApi, PaymentApiClient, IPaymentApiResilienceProxy>(
    (client, sp) => new IPaymentApiResilienceProxy(client, sp.GetRequiredService<RetryPolicy>()));

// 2.0
services.AddPaymentApiResiliencePolicies();
services.AddRestResilience<IPaymentApi, PaymentApiClient, IPaymentApiResilienceProxy>(
    (client, sp) => new IPaymentApiResilienceProxy(client, sp.GetRequiredService<PaymentApiResiliencePolicies>()));
```

## Hosts that activate the proxy through DI

ZeroAlloc.Outbox's `WithResilience` and ZeroAlloc.Scheduling's `WithResilience` resolve the
proxy's constructor from the container. Replace your `RetryPolicy`/`CircuitBreakerPolicy`
registrations with `Add{Name}ResiliencePolicies()`.

## Behaviour changes

| Change | 1.x | 2.0 |
|---|---|---|
| Unkeyed `RetryPolicy`, `TimeoutPolicy`, `RateLimiter`, `CircuitBreakerPolicy` registrations | registered by `Add{Name}Resilience`; the last interface registered won for every proxy | not registered |
| Two interfaces with `[CircuitBreaker]` | shared one circuit | independent circuits |
| Method-level `[CircuitBreaker]`/`[RateLimit]` | shared the interface instance and its settings | own instance, own settings, own state |
| `RateLimitScope.Instance` | behaved like `Shared` | one limiter per proxy instance |
| Invalid policy values, for example `MaxAttempts = 0` | the proxy never called the inner service | in an attribute: error ZR0004 at build time; set in `configure`: `ArgumentOutOfRangeException` when the policies are built |
| Name derivation for interfaces like IInvoiceApi | every leading I stripped: AddnvoiceApiResilience | one I stripped before an uppercase letter: AddInvoiceApiResilience |
