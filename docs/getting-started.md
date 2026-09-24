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
