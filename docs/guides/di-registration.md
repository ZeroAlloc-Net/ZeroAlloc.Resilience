---
id: di-registration
title: DI Registration
sidebar_position: 4
---

# DI Registration

The generator emits a `Add{ServiceName}Resilience<TImpl>()` extension method on `IServiceCollection` that registers everything — the implementation, all policy objects, and the proxy — in one call.

---

## Basic registration

```csharp
builder.Services.AddExternalServiceResilience<ExternalServiceImpl>();
```

This registers:
- `ExternalServiceImpl` as **transient**
- `RetryPolicy` as **singleton** (stateless — all state is in the proxy loop)
- `TimeoutPolicy` as **singleton** (stateless)
- `RateLimiter` as **singleton** (or transient if `Scope = RateLimitScope.Instance`)
- `CircuitBreakerPolicy` as **singleton** (stateful — circuit state is shared)
- `IExternalService` → `IExternalServiceResilienceProxy` as **transient**

---

## Generated extension method

```csharp
public static partial class ResilienceServiceCollectionExtensions
{
    public static IServiceCollection AddExternalServiceResilience<TImpl>(
        this IServiceCollection services)
        where TImpl : class, IExternalService
    {
        services.AddTransient<TImpl>();
        services.AddSingleton(new RetryPolicy(3, 200, true, 1000));
        services.AddSingleton(new TimeoutPolicy(5000));
        services.AddSingleton(new RateLimiter(100, 10, RateLimitScope.Shared));
        services.AddSingleton(new CircuitBreakerPolicy(5, 1000, 1));
        services.AddTransient<IExternalService>(sp =>
            new IExternalServiceResilienceProxy(
                sp.GetRequiredService<TImpl>(),
                sp.GetRequiredService<RetryPolicy>(),
                sp.GetRequiredService<TimeoutPolicy>(),
                sp.GetRequiredService<RateLimiter>(),
                sp.GetRequiredService<CircuitBreakerPolicy>()));
        return services;
    }
}
```

---

## Naming convention

The extension method name is `Add{InterfaceName.TrimStart('I')}Resilience<TImpl>`:

| Interface | Extension method |
|-----------|-----------------|
| `IExternalService` | `AddExternalServiceResilience<TImpl>()` |
| `IPaymentGateway` | `AddPaymentGatewayResilience<TImpl>()` |
| `DataStore` (no leading I) | `AddDataStoreResilience<TImpl>()` |

---

## Overriding policy values at registration time

The generated extension method bakes the attribute values. To override at registration time, register the policy objects manually before calling the extension method — DI will not register duplicates if you register first:

```csharp
// Override CircuitBreakerPolicy — shorter reset for testing
builder.Services.AddSingleton(new CircuitBreakerPolicy(maxFailures: 2, resetMs: 100, halfOpenProbes: 1));
builder.Services.AddExternalServiceResilience<ExternalServiceImpl>();
// The AddSingleton in the extension method is a no-op — singleton already registered
```

> Note: `IServiceCollection.AddSingleton` registers an additional instance if called twice; use `TryAddSingleton` semantics by registering first.

Alternatively, register the full pipeline manually:

```csharp
builder.Services.AddTransient<ExternalServiceImpl>();
builder.Services.AddSingleton(new RetryPolicy(maxAttempts: 5, backoffMs: 100, jitter: true, perAttemptTimeoutMs: 0));
builder.Services.AddSingleton(new CircuitBreakerPolicy(maxFailures: 3, resetMs: 500, halfOpenProbes: 2));
builder.Services.AddTransient<IExternalService>(sp =>
    new IExternalServiceResilienceProxy(
        sp.GetRequiredService<ExternalServiceImpl>(),
        sp.GetRequiredService<RetryPolicy>(),
        sp.GetRequiredService<CircuitBreakerPolicy>()));
```

---

## Multiple interfaces sharing the same implementation

Each interface gets its own set of singleton policy objects, keyed by type. Two interfaces with `[CircuitBreaker]` get two independent `CircuitBreakerPolicy` singletons — they do not share circuit state:

```csharp
builder.Services.AddExternalServiceResilience<ExternalServiceImpl>();
builder.Services.AddPaymentGatewayResilience<PaymentGatewayImpl>();
// Independent CircuitBreakerPolicy for each interface
```

---

## Scoped lifetime

The proxy itself is registered as **transient** because it is stateless — all state lives in the injected singleton policy objects. You can safely inject `IExternalService` into scoped or transient services.

If you need the proxy to be scoped (e.g. to share a request-scoped implementation), register manually:

```csharp
builder.Services.AddScoped<ExternalServiceImpl>();
builder.Services.AddScoped<IExternalService>(sp =>
    new IExternalServiceResilienceProxy(
        sp.GetRequiredService<ExternalServiceImpl>(),
        sp.GetRequiredService<RetryPolicy>(),
        sp.GetRequiredService<CircuitBreakerPolicy>()));
```

---

## Keyed services (.NET 8+)

The generated extension method uses unkeyed registration. If you need keyed services (e.g. two proxies for the same interface with different configs), register manually using `AddKeyedTransient` / `AddKeyedSingleton`.

---

## Without DI

The proxy can be constructed directly — it is a plain class with a constructor:

```csharp
var inner  = new ExternalServiceImpl();
var retry  = new RetryPolicy(3, 200, false, 0);
var cbPolicy = new CircuitBreakerPolicy(5, 1_000, 1);
IExternalService proxy = new IExternalServiceResilienceProxy(inner, retry, cbPolicy);
```

Pass only the policy objects that the proxy requires — if the interface only has `[Retry]` and `[CircuitBreaker]`, the constructor only takes those two plus the inner implementation.
