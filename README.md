# ZeroAlloc.Resilience

[![NuGet](https://img.shields.io/nuget/v/ZeroAlloc.Resilience.svg)](https://www.nuget.org/packages/ZeroAlloc.Resilience)
[![Build](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/actions/workflows/ci.yml/badge.svg)](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/ZeroAlloc-Net/ZeroAlloc.Resilience/graph/badge.svg)](https://codecov.io/gh/ZeroAlloc-Net/ZeroAlloc.Resilience)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/blob/main/LICENSE)
[![AOT](https://img.shields.io/badge/AOT--Compatible-passing-brightgreen)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?style=flat&logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

Retry, timeout, rate limiting and circuit breaking for .NET, declared as attributes on an interface and compiled into a proxy: a source-generated alternative to Polly with no allocation on the happy path.

## Why ZeroAlloc.Resilience

- **0 B per successful call** through `[Retry]` and `[CircuitBreaker]`, asserted by tests on every run. Polly v8 allocates a 64 B `ResilienceContext` per call.
- **14.7× and 15.0× faster than Polly v8** on the retry and circuit-breaker happy paths, and 5.9× faster with all four policies stacked, measured in the same run.
- **Works under NativeAOT and trimming.** The proxy is plain generated code with no reflection, published as a NativeAOT app in CI.
- **Mistakes surface at compile time.** A misspelled fallback, an invalid attribute value or a timeout on a method without a `CancellationToken` is a compiler diagnostic.

## Install

```bash
dotnet add package ZeroAlloc.Resilience
```

## Quick start

```csharp
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Resilience;

var services = new ServiceCollection();
services.AddWeatherClientResilience<FlakyWeatherClient>(); // generated: registers the proxy

var client = services.BuildServiceProvider().GetRequiredService<IWeatherClient>();
Console.WriteLine(await client.GetForecastAsync("Amsterdam", CancellationToken.None));
// Amsterdam: sunny, after 3 attempts

[Retry(MaxAttempts = 3, BackoffMs = 100, Jitter = true)]
[Timeout(Ms = 5_000)]
[CircuitBreaker(MaxFailures = 5, ResetMs = 30_000)]
public interface IWeatherClient
{
    ValueTask<string> GetForecastAsync(string city, CancellationToken ct);
}

public sealed class FlakyWeatherClient : IWeatherClient
{
    private int _calls;

    public ValueTask<string> GetForecastAsync(string city, CancellationToken ct)
        => ++_calls < 3
            ? throw new HttpRequestException($"attempt {_calls} failed")
            : ValueTask.FromResult($"{city}: sunny, after {_calls} attempts");
}
```

The first two calls throw, the proxy retries, and the caller only sees the result. Callers inject `IWeatherClient` as usual.

## What you get

- **Retry** with exponential backoff, jitter, a per-attempt timeout and a cap on the delay.
- **Timeout** over the whole operation, retries and backoff included.
- **Rate limiting** with a lock-free token bucket, shared per interface or per proxy.
- **Circuit breaker** with half-open probing and an optional fallback method.
- **A fixed, predictable order**: rate limit, circuit breaker, timeout, retry, whatever order you write the attributes in.
- **Exceptions or Results**: return a [ZeroAlloc.Results](https://github.com/ZeroAlloc-Net/ZeroAlloc.Results) `Result<T>` and policy failures come back as a failure instead of a throw.
- **Result-aware retry**: retry only the failures you call transient, and honour `Retry-After` hints.
- **Per-method overrides** of the interface-level policies, for example one attempt for a non-idempotent POST.
- **One-line DI registration** through a generated `Add{Service}Resilience<TImpl>()` extension.
- **Compile-time diagnostics** ZR0001–ZR0013: [the full list](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/blob/main/docs/index.md#diagnostics). Every attribute property: [attribute reference](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/blob/main/docs/attributes.md).

## Performance

Against Polly.Core 8.8.0 on .NET 10.0.13, measured 2026-10-15. Allocations are exact; speed is a same-run ratio, because the benchmark host is a shared VM.

| Operation | ZA vs Polly | Polly alloc | ZA alloc |
|---|---:|---:|---:|
| Retry, happy path | **14.7× faster** | 64 B | **0 B** |
| CircuitBreaker, closed | **15.0× faster** | 64 B | **0 B** |
| All-policies stacked, happy path | **5.9× faster** | 104 B | 144 B |

With all policies stacked, the 144 B is the linked `CancellationTokenSource` and timer that `[Timeout]` needs; without `[Timeout]` the proxy allocates nothing. Polly walks a delegate chain per call; ZA emits one method with the policy checks inline. Failure paths, an open circuit and the full method: [docs/performance.md](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/blob/main/docs/performance.md).

## Learn more

- Documentation: [resilience.zeroalloc.net](https://resilience.zeroalloc.net)
- [Getting started](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/blob/main/docs/getting-started.md), [execution order](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/blob/main/docs/core-concepts/execution-order.md), [Result return types](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/blob/main/docs/guides/result-return-types.md) and [migrating to 3.0](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/blob/main/docs/guides/migrating-to-v3.md)
- Related packages: [ZeroAlloc.Rest](https://github.com/ZeroAlloc-Net/ZeroAlloc.Rest) for source-generated HTTP clients, [ZeroAlloc.Cache](https://github.com/ZeroAlloc-Net/ZeroAlloc.Cache) for caching proxies, [ZeroAlloc.Telemetry](https://github.com/ZeroAlloc-Net/ZeroAlloc.Telemetry) for tracing and metrics proxies
- Contributing: issues and pull requests are welcome on [GitHub](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/issues)
- License: [MIT](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/blob/main/LICENSE)
