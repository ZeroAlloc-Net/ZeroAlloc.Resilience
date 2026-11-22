---
id: getting-started
title: Getting Started
sidebar_position: 2
---

# Getting Started

## Installation

```bash
dotnet add package ZeroAlloc.Resilience
```

The source generator is bundled in the package — no separate install required.

The generator needs the .NET 10 SDK 10.0.100 or later, or Visual Studio 2026 or later. It is built against Roslyn 5.0, and an older compiler skips it with warning CS9057, after which the generated proxies and registrations are missing. Version 3.5.1 was built against Roslyn 5.9 and loads only in SDK 10.0.4xx or later; on an older .NET 10 SDK, use 3.5.2 or later. ZeroAlloc.StateMachine 1.5.3, a dependency, ships a generator built against Roslyn 5.9. That generator currently needs SDK 10.0.4xx, which runs Roslyn 5.9. On older .NET 10 SDKs the build warns with CS9057 and its generated methods are missing. This is tracked in ZeroAlloc-Net/.github#77.

## Annotate your interface

Place policy attributes on the interface declaration (applies to all methods) or on individual methods (overrides interface-level config for that method):

```csharp
[Retry(MaxAttempts = 3, BackoffMs = 200, Jitter = true, PerAttemptTimeoutMs = 1_000)]
[Timeout(Ms = 5_000)]
[RateLimit(MaxPerSecond = 100, BurstSize = 10)]
[CircuitBreaker(MaxFailures = 5, ResetMs = 1_000, HalfOpenProbes = 1)]
public interface IExternalService
{
    ValueTask<string> FetchAsync(string id, CancellationToken ct);

    [Retry(MaxAttempts = 1)]   // override — POST is not idempotent
    ValueTask PostAsync(string data, CancellationToken ct);
}
```

## Register with DI

```csharp
builder.Services.AddExternalServiceResilience<ExternalServiceImpl>();
```

This registers `ExternalServiceImpl`, all policy objects, and binds `IExternalService` to the generated proxy.

## Failure surface

If a method returns `ValueTask` or `Task` of `Result`, `Result<T>` or `Result<T, ResilienceError>`, policy failures are returned as a failure of that type, with no exception thrown. A `Result<T, E>` with any other `E` gets the inner call's own Result back; see [Result Return Types](guides/result-return-types.md). Otherwise `ResilienceException` is thrown with a `Policy` property identifying the cause.

```csharp
try
{
    var result = await service.FetchAsync("id", ct);
}
catch (ResilienceException ex) when (ex.Policy == ResiliencePolicy.CircuitBreaker)
{
    // circuit was open and no fallback was configured
}
```
