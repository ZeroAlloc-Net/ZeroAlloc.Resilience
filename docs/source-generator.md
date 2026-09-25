---
id: source-generator
title: Source Generator
sidebar_position: 4
---

# Source Generator

ZeroAlloc.Resilience uses a Roslyn `IIncrementalGenerator` to emit a proxy class for every annotated interface. This page shows exactly what is generated and how to inspect it.

---

## What triggers generation

The generator activates on any `interface` that has at least one of `[Retry]`, `[Timeout]`, `[RateLimit]`, or `[CircuitBreaker]`, either on the interface itself or on one of its methods. Methods without an effective policy are forwarded to the inner service unchanged. It reads method signatures, collects effective policies (method-level shadows interface-level), validates fallback methods, and emits one file per annotated interface. The proxy implements every public instance member the interface declares or inherits from a base interface: methods, properties, indexers and events, abstract or default-implemented. Properties, indexers and events are always forwarded to the inner service unchanged, since no policy ever applies to them, and an `init`-only accessor throws `NotSupportedException` instead — see [Properties, indexers and events](#properties-indexers-and-events) below. An interface with a policy attribute of its own, on the interface or one of its own methods, and at least one member, own or inherited, gets a proxy, so an interface whose members are all inherited gets one too. A policy attribute on an inherited method alone does not make a proxy: the base interface that declares it gets its own — see [Inherited and default-implemented members](#inherited-and-default-implemented-members).

---

## Example input

```csharp
[Retry(MaxAttempts = 3, BackoffMs = 200, Jitter = true, PerAttemptTimeoutMs = 1000)]
[Timeout(Ms = 5000)]
[RateLimit(MaxPerSecond = 100, BurstSize = 10)]
[CircuitBreaker(MaxFailures = 5, ResetMs = 1000, HalfOpenProbes = 1)]
public interface IExternalService
{
    ValueTask<string> FetchAsync(string id, CancellationToken ct);
    ValueTask<string> FetchFallback(string id, CancellationToken ct);
}
```

---

## Generated types

For an interface carrying all four policies, the generator emits:
- a `{Name}ResiliencePolicies` class, with one settable slot per policy
- an `I{Name}ResilienceProxy` class, which implements the interface and reads every value from the policies passed to its constructor
- three DI extension methods: `Add{Name}ResiliencePolicies`, `Add{Name}Resilience<TImpl>()`, and `Add{Name}Resilience<TImpl>(configure)`

---

## Generated output

Copied from the generator's own snapshot tests:

```csharp
public sealed class ExternalServiceResiliencePolicies
{
    public global::ZeroAlloc.Resilience.RetryPolicy Retry { get; set; } = new global::ZeroAlloc.Resilience.RetryPolicy(3, 200, true, 1000);
    public global::ZeroAlloc.Resilience.TimeoutPolicy Timeout { get; set; } = new global::ZeroAlloc.Resilience.TimeoutPolicy(5000);
    public global::ZeroAlloc.Resilience.RateLimiter RateLimiter { get; set; } = new global::ZeroAlloc.Resilience.RateLimiter(100, 10, global::ZeroAlloc.Resilience.RateLimitScope.Shared);
    public global::ZeroAlloc.Resilience.CircuitBreakerPolicy CircuitBreaker { get; set; } = new global::ZeroAlloc.Resilience.CircuitBreakerPolicy(5, 1000, 1);
}

internal sealed class IExternalServiceResilienceProxy : global::T.IExternalService
{
    private readonly global::T.IExternalService _inner;
    private readonly global::ZeroAlloc.Resilience.RetryPolicy _retry;
    private readonly global::ZeroAlloc.Resilience.TimeoutPolicy _timeout;
    private readonly global::ZeroAlloc.Resilience.RateLimiter _rateLimiter;
    private readonly global::ZeroAlloc.Resilience.CircuitBreakerPolicy _circuitBreaker;

    public IExternalServiceResilienceProxy(global::T.IExternalService inner, ExternalServiceResiliencePolicies policies)
    {
        global::System.ArgumentNullException.ThrowIfNull(inner);
        global::System.ArgumentNullException.ThrowIfNull(policies);
        _inner = inner;
        _retry = (policies.Retry ?? throw new global::System.ArgumentException("ExternalServiceResiliencePolicies.Retry is null.", nameof(policies)));
        _timeout = (policies.Timeout ?? throw new global::System.ArgumentException("ExternalServiceResiliencePolicies.Timeout is null.", nameof(policies)));
        _rateLimiter = (policies.RateLimiter ?? throw new global::System.ArgumentException("ExternalServiceResiliencePolicies.RateLimiter is null.", nameof(policies))).ForProxyInstance();
        _circuitBreaker = (policies.CircuitBreaker ?? throw new global::System.ArgumentException("ExternalServiceResiliencePolicies.CircuitBreaker is null.", nameof(policies)));
    }

    public async global::System.Threading.Tasks.ValueTask<string> FetchAsync(string id, global::System.Threading.CancellationToken ct)
    {
        if (!_rateLimiter.TryAcquire())
            throw new global::ZeroAlloc.Resilience.ResilienceException(global::ZeroAlloc.Resilience.ResiliencePolicy.RateLimit, "Rate limit exceeded.");

        if (!_circuitBreaker.CanExecute())
        {
            throw new global::ZeroAlloc.Resilience.ResilienceException(global::ZeroAlloc.Resilience.ResiliencePolicy.CircuitBreaker, "Circuit breaker is open.");
        }

        using var __totalCts = global::System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
        __totalCts.CancelAfter(_timeout.TotalMs);

        global::System.Exception? __lastEx = null;
        for (int __attempt = 0; __attempt < _retry.MaxAttempts; __attempt++)
        {
            using var __attemptCts = _retry.PerAttemptTimeoutMs > 0
                ? global::System.Threading.CancellationTokenSource.CreateLinkedTokenSource(__totalCts.Token)
                : null;
            __attemptCts?.CancelAfter(_retry.PerAttemptTimeoutMs);
            var __ct = __attemptCts?.Token ?? __totalCts.Token;
            try
            {
                var __result = await _inner.FetchAsync(id, __ct).ConfigureAwait(false);
                _circuitBreaker.OnSuccess();
                return __result;
            }
            catch (global::System.Exception __ex)
            {
                __lastEx = __ex;
                _circuitBreaker.OnFailure(__ex);
                if (__totalCts.IsCancellationRequested) break;
                if (__attempt == _retry.MaxAttempts - 1) break;
                await global::System.Threading.Tasks.Task.Delay(_retry.GetBackoffMs(__attempt), __totalCts.Token).ConfigureAwait(false);
            }
        }
        // All attempts exhausted
        throw new global::ZeroAlloc.Resilience.ResilienceException(global::ZeroAlloc.Resilience.ResiliencePolicy.Retry, "All retry attempts failed.", __lastEx);
    }

    // FetchFallback carries no method-level attributes, so it uses the same interface-level
    // policies as FetchAsync — it is not a special "passthrough" method here, because the
    // interface-level [CircuitBreaker] on this example does not name a Fallback.
    public async global::System.Threading.Tasks.ValueTask<string> FetchFallback(string id, global::System.Threading.CancellationToken ct)
    {
        if (!_rateLimiter.TryAcquire())
            throw new global::ZeroAlloc.Resilience.ResilienceException(global::ZeroAlloc.Resilience.ResiliencePolicy.RateLimit, "Rate limit exceeded.");

        if (!_circuitBreaker.CanExecute())
        {
            throw new global::ZeroAlloc.Resilience.ResilienceException(global::ZeroAlloc.Resilience.ResiliencePolicy.CircuitBreaker, "Circuit breaker is open.");
        }

        using var __totalCts = global::System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
        __totalCts.CancelAfter(_timeout.TotalMs);

        global::System.Exception? __lastEx = null;
        for (int __attempt = 0; __attempt < _retry.MaxAttempts; __attempt++)
        {
            using var __attemptCts = _retry.PerAttemptTimeoutMs > 0
                ? global::System.Threading.CancellationTokenSource.CreateLinkedTokenSource(__totalCts.Token)
                : null;
            __attemptCts?.CancelAfter(_retry.PerAttemptTimeoutMs);
            var __ct = __attemptCts?.Token ?? __totalCts.Token;
            try
            {
                var __result = await _inner.FetchFallback(id, __ct).ConfigureAwait(false);
                _circuitBreaker.OnSuccess();
                return __result;
            }
            catch (global::System.Exception __ex)
            {
                __lastEx = __ex;
                _circuitBreaker.OnFailure(__ex);
                if (__totalCts.IsCancellationRequested) break;
                if (__attempt == _retry.MaxAttempts - 1) break;
                await global::System.Threading.Tasks.Task.Delay(_retry.GetBackoffMs(__attempt), __totalCts.Token).ConfigureAwait(false);
            }
        }
        // All attempts exhausted
        throw new global::ZeroAlloc.Resilience.ResilienceException(global::ZeroAlloc.Resilience.ResiliencePolicy.Retry, "All retry attempts failed.", __lastEx);
    }
}

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

Copied from `tests/ZeroAlloc.Resilience.Generator.Tests/Snapshots/SnapshotTests.AllPolicies_ClassLevel_GeneratesProxy#T_IExternalService.Resilience.g.verified.cs`, with the `//HintName:` line and the `using` block above it left out for brevity.

---

## Key generation decisions

### Values come from the policies instance, not literals

Every value the proxy reads — `MaxAttempts`, `BackoffMs`, `PerAttemptTimeoutMs`, `TotalMs`, and so on — comes from the `readonly` policy-object fields set in the constructor, not from an integer literal in the generated code. This is what lets `configure` change behaviour at runtime: the generator only decides, at generation time, *which* checks and loops a method gets; the values inside them are read at call time.

### Passthrough methods

A method with no effective policy at all — no interface-level attribute, no method-level attribute, and not itself carrying any resilience behaviour — is emitted as a direct delegation with no try/catch overhead:

```csharp
public async ValueTask<string> OtherAsync(string id, CancellationToken ct)
    => await _inner.OtherAsync(id, ct).ConfigureAwait(false);
```

`FetchAsync` and `FetchFallback` in the example above are not passthrough methods — both inherit the interface-level policies, because neither carries a method-level attribute of its own and the interface's `[CircuitBreaker]` does not name a `Fallback`.

### Properties, indexers and events

No policy attribute ever applies to a property, an indexer or an event — only methods can carry `[Retry]`, `[Timeout]`, `[RateLimit]` or `[CircuitBreaker]` — so every public one the interface declares or inherits is always forwarded to the inner service unchanged, the same shape as a passthrough method:

```csharp
public string TypeName => _inner.TypeName;

public string this[int index] => _inner[index];

public event EventHandler<string> Changed
{
    add => _inner.Changed += value;
    remove => _inner.Changed -= value;
}
```

Only the accessors a class can implement are forwarded: a default-implemented property with a `private set` gets only its `get` accessor in the proxy.

An `init`-only accessor is the one exception among the members that *are* forwarded: an `init` accessor can only be assigned from an object-initializer expression, and by the time the proxy's own `init` accessor would run, the inner instance is already fully constructed, so there is no method body that can forward the value to it. The generated accessor throws `NotSupportedException` with a message explaining this instead of silently doing nothing:

```csharp
public string Name
{
    get => _inner.Name;
    init => throw new global::System.NotSupportedException("...");
}
```

### Inherited and default-implemented members

Since 3.0, members inherited from base interfaces are forwarded like the interface's own, and so are members with a default implementation. An inherited method gets its own method-level attributes, and otherwise the interface-level attributes of the interface being proxied, not those of the base interface that declares it:

```csharp
public interface IOutboxDispatcher<TMessage>
{
    ValueTask DispatchAsync(TMessage message, CancellationToken ct);
}

// No members of its own: the proxy forwards the inherited DispatchAsync under [Retry].
[Retry(MaxAttempts = 3, BackoffMs = 200)]
public interface IOrderDispatcher : IOutboxDispatcher<OrderPlaced> { }
```

An inherited member is called through its declaring interface, so the call binds to exactly that declaration:

```csharp
// inside the retry loop of the generated DispatchAsync
await ((global::MyApp.IOutboxDispatcher<global::MyApp.OrderPlaced>)_inner).DispatchAsync(message, __ct).ConfigureAwait(false);
```

A default-implemented member is forwarded to the inner service, so an implementation that overrides the default wins over the interface's default body. A default-implemented method keeps its method-level attributes and gets the interface-level ones, exactly like an abstract method, except for a policy it cannot take: that one is left off with [ZR0006](diagnostics/ZR0006.md).

Some members are not forwarded:

- static, private and protected members, which a public proxy member cannot implement
- `sealed` members, which are neither abstract nor virtual, so no class can implement them
- explicit implementations declared in an interface, such as `string IBase.Name => "x";`
- an inherited member that a more derived interface already implements explicitly

Some members are forwarded without policies, because no policy body can wrap them:

- a method or property that returns by `ref` or `ref readonly`, forwarded as `=> ref ((IBase)_inner).Slot()`
- an async method with a `ref`, `out`, `in` or ref struct parameter, such as `ReadOnlySpan<char>`, forwarded without `async`, returning the inner task

A policy on one of these is [ZR0007](diagnostics/ZR0007.md), or ZR0006 for an inherited default method, which is forwarded without it.

`ToString()`, `Equals(object)` and `GetHashCode()` declared in the interface or a base interface are not forwarded: `object`'s own members implement them on the proxy, so the proxy keeps its own equality and hash code. A skipped method of the interface itself still counts for slot numbering, so the slots of its overloads keep their names; the same goes for a `sealed` method. A policy on such a method has no effect, and [ZR0006](diagnostics/ZR0006.md) reports it.

A public proxy member that only shares an `object` member's name and parameters, such as `object ToString()` from a base interface, is emitted with `new`, so it does not warn with CS0114 or CS0108.

Other rules for inherited members:

- **Policy slots.** The interface's own methods keep the slot names they had before inherited methods were forwarded; inherited methods are numbered after them.
- **Fallback.** A `Fallback` is looked up among the methods the interface declares and inherits. An interface-level `Fallback` must match every method the interface declares itself, or ZR0001 is reported as before. An inherited method it does not match gets the circuit breaker without a fallback.
- **Policies an inherited default method cannot take.** If a policy would give an inherited default-implemented method a build error, for example `NonThrowing` on a method that does not return `Result<T, ResilienceError>`, that policy is left off the method with warning [ZR0006](diagnostics/ZR0006.md). Its other policies still apply. On a method the proxy has to implement, an own or an abstract one, the same case is an error.
- **Declared more than once.** See [Declarations with the same name](#declarations-with-the-same-name).

### Declarations with the same name

An interface can inherit several declarations that one public member cannot implement: from two unrelated base interfaces, through `new`-hiding along one inheritance path, as `IEnumerable<T>.GetEnumerator` hides `IEnumerable.GetEnumerator`, or as a property, a method or an indexer with the same name; an indexer is named `Item`. Generic methods that differ only in their type parameter names, such as `Foo<T>(T)` and `Foo<U>(U)`, are the same declaration. The proxy implements every one of them. Each declaration reaches the inner service's own implementation of it, through its own interface:

- One declaration is the **public** member: the interface's own declaration if it has one; otherwise, along an inheritance path, the most-derived declaration, the one whose `new` hides the others; between unrelated bases, the first in `AllInterfaces` order. For a property and a method with the same name, the kind of that declaration stays public.
- Every other declaration gets an **explicit interface implementation** with its exact signature, nullable annotations included, which forwards through a cast of `_inner` to its own interface. A generic one states only the constraints C# requires there: `where T : class` or `where T : default` when its signature uses `T?`.
- Declarations that render exactly alike, nullable annotations and resilience attributes included, share one implementation: the public or explicit member for the first one, plus an explicit implementation for each of the others.
- A method's explicit implementation gets the same policies as a public one. Declarations with different signatures each get their own policy slots, named like overloads: `GetRetry`, `Get2Retry`. The interface's own methods keep their slot names.
- The interface's own declaration implements every inherited declaration with the same signature, as it did in 2.0.

```csharp
[Retry(MaxAttempts = 3)]
public interface IItems : IEnumerable<int> { }

// generated
public IEnumerator<int> GetEnumerator() { /* retry loop around ((IEnumerable<int>)_inner).GetEnumerator() */ }
IEnumerator global::System.Collections.IEnumerable.GetEnumerator() { /* retry loop around ((IEnumerable)_inner).GetEnumerator() */ }
```

Identical declarations from two instances of one generic base, such as `IMessageHandler<FooMessage>` and `IMessageHandler<BarMessage>` both declaring `string Name { get; }`, get one public member and one explicit implementation. For a method with policies, they share one policy-wrapped body and one set of policy slots:

```csharp
[Retry(MaxAttempts = 3)]
public interface IDualHandler : IMessageHandler<FooMessage>, IMessageHandler<BarMessage> { }

// generated
public string Name => ((global::MyApp.IMessageHandler<global::MyApp.FooMessage>)_inner).Name;
string global::MyApp.IMessageHandler<global::MyApp.BarMessage>.Name
    => ((global::MyApp.IMessageHandler<global::MyApp.BarMessage>)_inner).Name;

public ValueTask<string> DescribeAsync(CancellationToken ct) => __DescribeAsync_Resilient(0, ct);
ValueTask<string> global::MyApp.IMessageHandler<global::MyApp.BarMessage>.DescribeAsync(CancellationToken ct)
    => __DescribeAsync_Resilient(1, ct);
// __DescribeAsync_Resilient holds the retry loop; its inner call dispatches on the index.
```

### Generic methods and by-reference parameters

A generic method keeps its type parameters and constraint clauses, and every call passes the type arguments explicitly. `ref`, `out`, `in` and `params` parameters keep their modifiers in the parameter list, and `ref`, `out` and `in` are passed on in every argument list: the inner call inside a retry loop, a single call, a fallback call and a passthrough.

An `out` parameter under a policy is assigned `default` before the first attempt. Each attempt that reaches the inner service assigns it again, so a successful call returns the inner service's value. A path that returns without a successful inner call, such as a failure `Result` after the last retry or a rejected call, returns the parameter as `default`. A `ref` parameter is passed to every attempt as it is, so an attempt that changes it and then fails leaves the change for the next attempt.

An async method cannot have `ref`, `out`, `in` or ref struct parameters, so a policy cannot wrap one: that is [ZR0007](diagnostics/ZR0007.md), or [ZR0006](diagnostics/ZR0006.md) for an inherited method with a default body. Without a policy, such a method is forwarded and returns the inner task directly.

### Unsupported interface shapes

A generic interface, an interface the generated top-level classes cannot access, and an interface with a `static abstract` or `static virtual` member in itself or a base interface cannot get a valid proxy. The generator reports [ZR0007](diagnostics/ZR0007.md) for them and emits nothing.

### No try/catch when not needed

If a method has no circuit breaker and does not return a `Result` whose failure the generator builds, the generator emits a direct `return` instead of a wrapped try/catch — eliminating the exception-handler overhead entirely.

### Rate limit + circuit breaker are checks, not wrappers

Both are simple `if (!...)` checks before the inner call, not `try/catch` wrappers. A rejected call exits immediately with a throw (or fallback) — the inner implementation is never invoked.

---

## Inspecting the generated output

**Visual Studio / Rider:** Expand the project's **Analyzers** → **ZeroAlloc.Resilience.Generator** node in Solution Explorer.

**Command line:**

```bash
dotnet build
# Generated files are under:
# obj/Debug/{tfm}/generated/ZeroAlloc.Resilience.Generator/
#   ZeroAlloc.Resilience.Generator.ResilienceGenerator/
#   {Namespace}_{InterfaceName}.Resilience.g.cs
```

**Go to Definition:** Place your cursor on the generated proxy constructor or any method and press F12.

---

## Diagnostics

| ID | Severity | Trigger |
|----|----------|---------|
| ZR0001 | Error | Fallback method not found or its signature does not match |
| ZR0002 | Warning | `[Timeout]` or `PerAttemptTimeoutMs` configured but method has no `CancellationToken` parameter |
| ZR0003 | Error | `[RateLimit]`, `[CircuitBreaker]` without `Fallback`, or `NonThrowing` on a method returning `Result<T, E>` with an `E` the generator cannot construct |
| ZR0004 | Error | A policy attribute property is explicitly set to a value the runtime policy constructor would reject, for example `[Timeout(Ms = 0)]` |
| ZR0006 | Warning | A policy cannot be applied to a method: an inherited default-implemented method it cannot wrap, which is forwarded without it, or an own method the proxy does not implement, because it has an `object` member's signature or is `sealed`, static or not public |
| ZR0007 | Error | The interface is generic, not accessible to a top-level class, has a `static abstract` or `static virtual` member, or a policy applies to a method that returns by reference or is async with a `ref`, `out`, `in` or ref struct parameter |
