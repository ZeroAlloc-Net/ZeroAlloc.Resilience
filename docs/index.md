---
id: index
title: ZeroAlloc.Resilience
sidebar_position: 1
---

# ZeroAlloc.Resilience

Source-generated, zero-allocation resilience policies for .NET.

Add `[Retry]`, `[Timeout]`, `[RateLimit]`, and `[CircuitBreaker]` to an interface; the generator emits a proxy composing all policies in declaration order with no heap allocation on the happy path.

## Quick example

```csharp
[Retry(MaxAttempts = 3, BackoffMs = 200, Jitter = true)]
[Timeout(Ms = 5_000)]
[CircuitBreaker(MaxFailures = 5, ResetMs = 1_000, HalfOpenProbes = 1, Fallback = nameof(FetchFallback))]
public interface IExternalService
{
    ValueTask<string> FetchAsync(string id, CancellationToken ct);
    ValueTask<string> FetchFallback(string id, CancellationToken ct);
}

// Register
services.AddExternalServiceResilience<ExternalServiceImpl>();

// Inject IExternalService — all policies are wired automatically
```
