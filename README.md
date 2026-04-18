# ZeroAlloc.Resilience

Source-generated, zero-allocation resilience policies for .NET.

[![NuGet](https://img.shields.io/nuget/v/ZeroAlloc.Resilience.svg)](https://www.nuget.org/packages/ZeroAlloc.Resilience)

Add `[Retry]`, `[Timeout]`, `[RateLimit]`, and `[CircuitBreaker]` to an interface; the Roslyn source generator emits a proxy that composes all policies in declaration order with **no heap allocation on the happy path** (beyond the unavoidable `CancellationTokenSource` for timeout).

## Quick start

```bash
dotnet add package ZeroAlloc.Resilience
```

```csharp
[Retry(MaxAttempts = 3, BackoffMs = 200, Jitter = true)]
[Timeout(Ms = 5_000)]
[CircuitBreaker(MaxFailures = 5, ResetMs = 1_000, HalfOpenProbes = 1)]
public interface IExternalService
{
    ValueTask<string> FetchAsync(string id, CancellationToken ct);
}

services.AddExternalServiceResilience<ExternalServiceImpl>();
```

See the [documentation](https://zeroalloc-net.github.io/ZeroAlloc.Resilience) for full details.
