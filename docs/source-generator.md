---
id: source-generator
title: Source Generator
sidebar_position: 4
---

# Source Generator

ZeroAlloc.Resilience uses a Roslyn `IIncrementalGenerator` to emit a proxy class for every annotated interface. This page shows exactly what is generated and how to inspect it.

---

## What triggers generation

The generator activates on any `interface` that has at least one of `[Retry]`, `[Timeout]`, `[RateLimit]`, or `[CircuitBreaker]`, either on the interface itself or on one of its methods. Methods without an effective policy are forwarded to the inner service unchanged. It reads method signatures, collects effective policies (method-level shadows interface-level), validates fallback methods, and emits one file per annotated interface.

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
