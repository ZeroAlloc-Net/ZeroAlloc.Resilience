---
id: migrating-to-v4
title: Migrating to 4.0
sidebar_position: 7
---

# Migrating to 4.0

4.0 has three breaking changes. Most projects need at most a package reference or a one-line attribute
change.

## Breaking changes

| Change | 3.x | 4.0 | What to do |
|---|---|---|---|
| Target frameworks | `net8.0`, `net9.0`, `net10.0`, `net11.0` | `net10.0`, `net11.0` | Move the project to .NET 10 or .NET 11, or stay on 3.x. .NET 8 and .NET 9 are out of support. |
| An exception `RetryOnException` declines | wrapped in `ResilienceException(ResiliencePolicy.Retry, "All retry attempts failed.", exception)` unless `RethrowDeclined = true` | rethrown unchanged, with its original stack trace, unless `RethrowDeclined = false` | Catch the exception type itself instead of `ResilienceException` and its `InnerException`. To keep the 3.x wrapper, set `RethrowDeclined = false` on the `[Retry]`. |
| Package dependency on `Microsoft.Extensions.DependencyInjection.Abstractions` | declared by `ZeroAlloc.Resilience` | not declared | A project that calls the generated `Add{Name}Resilience` or `Add{Name}ResiliencePolicies` and got the abstractions only through this package adds `<PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />`. ASP.NET Core and generic-host apps already have it. |

## Declined exceptions

```csharp
[Retry(RetryOnException = nameof(IsTransient))]
public interface IOrdersApi
{
    ValueTask<Order> GetAsync(string id, CancellationToken ct);

    static bool IsTransient(Exception exception) => exception is not ArgumentException;
}

// 3.x
catch (ResilienceException ex) when (ex.InnerException is ArgumentException argument) { ... }

// 4.0
catch (ArgumentException argument) { ... }
```

Exceptions that are retried until the attempts run out still throw `ResilienceException`, and methods
returning `Result`, `Result<T>`, `Result<T, ResilienceError>` or `UnitResult<ResilienceError>` still return
a failure. See [Declined exceptions and `RethrowDeclined`](../core-concepts/retry.md#declined-exceptions-and-rethrowdeclined).

[ZR0013](../diagnostics/ZR0013.md) now reports a `RethrowDeclined` set to either value where it has no
effect. An explicit `RethrowDeclined = true` that 3.x accepted keeps compiling and keeps its meaning; it can
be removed, since it is the default.

## The DI abstractions

The generator emits the `IServiceCollection` extensions only when the project references
`Microsoft.Extensions.DependencyInjection.Abstractions`. Without the reference the proxy and the policies
class are still generated, but a call to `Add{Name}Resilience` no longer compiles. Add the
package reference, or construct the proxy by hand as [DI Registration](di-registration.md#without-di)
shows.
