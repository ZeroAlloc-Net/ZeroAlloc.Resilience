---
id: di-registration
title: DI Registration
sidebar_position: 4
---

# DI Registration

The generator emits three extension methods on `IServiceCollection` for each annotated interface: `Add{Name}Resilience<TImpl>()`, an overload that takes a `configure` callback, and `Add{Name}ResiliencePolicies()` for hosts that build the proxy themselves.

---

## Basic registration

```csharp
builder.Services.AddExternalServiceResilience<ExternalServiceImpl>();
```

This registers:
- `ExternalServiceImpl` as **transient**
- `ExternalServiceResiliencePolicies` as a **singleton**
- `IExternalService` → `IExternalServiceResilienceProxy` as **transient**

No `RetryPolicy`, `TimeoutPolicy`, `RateLimiter` or `CircuitBreakerPolicy` is registered in the container on its own. The proxy reads every value from the `ExternalServiceResiliencePolicies` singleton instead.

---

## Generated code

For an interface carrying all four policies, the generator emits these three methods (copied from the generator's own snapshot tests):

```csharp
public static partial class ResilienceServiceCollectionExtensions
{
    public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddExternalServiceResiliencePolicies(
        this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services,
        global::System.Action<global::System.IServiceProvider, ExternalServiceResiliencePolicies>? configure = null)
    {
        global::Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<ExternalServiceResiliencePolicies>(services, sp =>
        {
            var policies = new ExternalServiceResiliencePolicies();
            configure?.Invoke(sp, policies);
            return policies;
        });
        return services;
    }

    public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddExternalServiceResilience<
        [global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)]
        TImpl>(
        this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)
        where TImpl : class, global::T.IExternalService
    {
        services.AddTransient<TImpl>();
        services.AddExternalServiceResiliencePolicies();
        services.AddTransient<global::T.IExternalService>(sp => new IExternalServiceResilienceProxy(
            sp.GetRequiredService<TImpl>(), sp.GetRequiredService<ExternalServiceResiliencePolicies>()));
        return services;
    }

    public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddExternalServiceResilience<
        [global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)]
        TImpl>(
        this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services,
        global::System.Action<global::System.IServiceProvider, ExternalServiceResiliencePolicies> configure)
        where TImpl : class, global::T.IExternalService
    {
        global::System.ArgumentNullException.ThrowIfNull(configure);
        services.AddTransient<TImpl>();
        services.AddExternalServiceResiliencePolicies(configure);
        services.AddTransient<global::T.IExternalService>(sp => new IExternalServiceResilienceProxy(
            sp.GetRequiredService<TImpl>(), sp.GetRequiredService<ExternalServiceResiliencePolicies>()));
        return services;
    }
}
```

`Add{Name}ResiliencePolicies` uses `TryAddSingleton`, so it never overwrites a `{Name}ResiliencePolicies` the application registered first.

---

## Naming convention

The generator strips one leading `I` from the interface name, but only when an uppercase letter
follows it — the common "interface I-prefix" convention — and uses the result to name the
extension method `Add{ServiceName}Resilience<TImpl>` and the policies class
`{ServiceName}ResiliencePolicies`. An `I` that is not followed by an uppercase letter, such as the
`I` in `Item`, is kept:

| Interface | Extension method | Policies class |
|-----------|-----------------|-----------------|
| `IExternalService` | `AddExternalServiceResilience<TImpl>()` | `ExternalServiceResiliencePolicies` |
| `IInvoiceApi` | `AddInvoiceApiResilience<TImpl>()` | `InvoiceApiResiliencePolicies` |
| `IPaymentGateway` | `AddPaymentGatewayResilience<TImpl>()` | `PaymentGatewayResiliencePolicies` |
| `DataStore` (no leading I) | `AddDataStoreResilience<TImpl>()` | `DataStoreResiliencePolicies` |
| `Item` (I not followed by an uppercase letter) | `AddItemResilience<TImpl>()` | `ItemResiliencePolicies` |

---

## Configuring policies

Pass a `configure` callback to set values from configuration or any other service:

```csharp
builder.Services.AddExternalServiceResilience<ExternalServiceImpl>((sp, p) =>
{
    var o = sp.GetRequiredService<IOptions<ExternalOptions>>().Value;
    p.Retry = new RetryPolicy(o.MaxAttempts, o.BackoffMs, jitter: true, perAttemptTimeoutMs: 0);
});
```

`configure` runs once, the first time `ExternalServiceResiliencePolicies` is resolved from the container. Every proxy built after that shares the same policies instance; the proxy copies each slot into a `readonly` field when it is constructed, so a value changed later has no effect on proxies already built.

`configure` can only change the value of a slot the interface already declares. It cannot add a policy the attributes do not declare — whether a method has a retry loop, a timeout, a rate-limit check or a circuit check is fixed when the generator runs, not by `configure`.

A per-attempt timeout (`RetryPolicy.PerAttemptTimeoutMs`) set only through `configure` has no effect on a method with no `CancellationToken` parameter — the generator only emits the per-attempt cancellation source for methods that can take a token. ZR0002 warns about a per-attempt timeout with no `CancellationToken` parameter, but only when the attribute itself asks for one; it does not know what `configure` will set at runtime, so it cannot warn about that case.

Calling `Add{Name}Resilience` or `Add{Name}ResiliencePolicies` a second time with another `configure` has no effect, because the policies are registered once with `TryAdd`; put all configuration in one callback.

---

## Registering your own policies instance

Register a `{Name}ResiliencePolicies` instance yourself before calling `Add{Name}Resilience`, and it takes precedence:

```csharp
builder.Services.AddSingleton(new ExternalServiceResiliencePolicies
{
    Retry = new RetryPolicy(maxAttempts: 5, backoffMs: 100, jitter: true, perAttemptTimeoutMs: 0),
});
builder.Services.AddExternalServiceResilience<ExternalServiceImpl>();
```

Because `Add{Name}ResiliencePolicies` uses `TryAddSingleton`, the registration above wins. If you also pass a `configure` callback to `AddExternalServiceResilience`, it does not run — the policies instance already exists.

---

## Isolation

Each interface gets its own `{Name}ResiliencePolicies` singleton. Two interfaces with `[CircuitBreaker]` never share circuit state, even when both use the same implementation type:

```csharp
builder.Services.AddExternalServiceResilience<ExternalServiceImpl>();
builder.Services.AddPaymentGatewayResilience<PaymentGatewayImpl>();
// ExternalServiceResiliencePolicies.CircuitBreaker and PaymentGatewayResiliencePolicies.CircuitBreaker
// are independent circuit breakers.
```

---

## Generated accessibility

An `internal` interface always gets its `Add{Name}Resilience` and `Add{Name}ResiliencePolicies` extension methods, and its `{Name}ResiliencePolicies` class, emitted `internal` too — into `internal static partial class InternalResilienceServiceCollectionExtensions` instead of the public `ResilienceServiceCollectionExtensions`. This keeps an internal interface from forcing public members onto your library's surface just because it needs DI registration.

A **public** interface normally still gets a public extension and a public policies class, since some libraries expose the interface and want callers to register it directly. If instead you expose a public interface but wire its resilience internally — never asking a caller to call `Add{Name}Resilience` themselves — that public extension is unwanted API surface, and tools like `Microsoft.CodeAnalysis.PublicApiAnalyzers` flag it as RS0016.

Opt in project-wide with `ZeroAllocGeneratedAccessibility`:

```xml
<PropertyGroup>
  <ZeroAllocGeneratedAccessibility>Internal</ZeroAllocGeneratedAccessibility>
</PropertyGroup>
```

This applies to every annotated interface in the project, public or already-internal:
- the `{Name}ResiliencePolicies` class is emitted `internal`
- `Add{Name}Resilience` and `Add{Name}ResiliencePolicies` are emitted into `InternalResilienceServiceCollectionExtensions` instead of `ResilienceServiceCollectionExtensions`
- the generated proxy is unaffected — it is always `internal`, regardless of this property

The interface itself is never touched: a public interface stays public. Only the entry points the generator emits change accessibility, and only downward — the property can never make anything *more* visible than it already is.

The property name is shared, unqualified, across every ZeroAlloc generator package (ZeroAlloc.Validation, ZeroAlloc.Inject, …), so setting it once in a project covers all of them. The allowed values are `Public` (the default, unchanged output) and `Internal`, compared case-insensitively; any other value is [ZR0008](../diagnostics/ZR0008.md).

With the property unset or `Public`, generated output is byte-identical to before this property existed.

---

## Hosts that build the proxy

Some hosts construct the generated proxy themselves rather than calling `Add{Name}Resilience<TImpl>()` — ZeroAlloc.Rest, ZeroAlloc.Outbox and ZeroAlloc.Scheduling all do this. Register only the policies, and resolve them where the proxy is built:

```csharp
services.AddPaymentApiResiliencePolicies();
services.AddRestResilience<IPaymentApi, PaymentApiClient, IPaymentApiResilienceProxy>(
    (client, sp) => new IPaymentApiResilienceProxy(client, sp.GetRequiredService<PaymentApiResiliencePolicies>()));
```

---

## Scoped lifetime

The proxy is registered as **transient** because all of its state is copied into `readonly` fields at construction; the proxy itself holds nothing mutable beyond those references. You can safely inject `IExternalService` into scoped or transient services.

If you need the proxy to be scoped, register it manually:

```csharp
builder.Services.AddScoped<ExternalServiceImpl>();
builder.Services.AddExternalServiceResiliencePolicies();
builder.Services.AddScoped<IExternalService>(sp =>
    new IExternalServiceResilienceProxy(
        sp.GetRequiredService<ExternalServiceImpl>(),
        sp.GetRequiredService<ExternalServiceResiliencePolicies>()));
```

---

## Without DI

The proxy is a plain class with a two-argument constructor — the inner implementation and a policies instance:

```csharp
var inner = new ExternalServiceImpl();
IExternalService proxy = new IExternalServiceResilienceProxy(inner, new ExternalServiceResiliencePolicies());
```

`new ExternalServiceResiliencePolicies()` is a complete, valid configuration — every slot defaults to its attribute value. Set individual slots on the instance before passing it to the constructor to change values:

```csharp
var policies = new ExternalServiceResiliencePolicies
{
    Retry = new RetryPolicy(5, 100, jitter: true, perAttemptTimeoutMs: 0),
};
IExternalService proxy = new IExternalServiceResilienceProxy(inner, policies);
```
