using Microsoft.CodeAnalysis;
using System.Collections.Immutable;

namespace ZeroAlloc.Resilience.Generator;

internal sealed record ResilienceModel(
    string? Namespace,
    string InterfaceName,
    string InterfaceFqn,            // e.g. global::MyApp.IExternalService
    RetryConfig? ClassRetry,
    TimeoutConfig? ClassTimeout,
    RateLimitConfig? ClassRateLimit,
    CircuitBreakerConfig? ClassCircuitBreaker,
    ImmutableArray<MethodModel> Methods,
    ImmutableArray<PassthroughMethodModel> PassthroughMethods,
    ImmutableArray<Diagnostic> Diagnostics
);

internal sealed record PassthroughMethodModel(
    string Name,
    string ReturnTypeFqn,
    bool IsAsync,
    string ParameterList,
    string ArgumentList
);

internal sealed record MethodModel(
    string Name,
    string ReturnTypeFqn,           // e.g. global::System.Threading.Tasks.ValueTask<string>
    string InnerReturnType,         // e.g. string (for await unwrapping)
    bool ReturnsResult,             // true if return wraps Result<T,E>
    bool IsAsync,                   // true if ValueTask or Task
    bool HasCancellationToken,
    string? CancellationTokenParamName, // name of the CancellationToken parameter, if any
    string ParameterList,           // "string id, CancellationToken ct"
    string ArgumentList,            // "id, ct"
    string ArgumentListWithToken,   // "id, __ct" (replaced CancellationToken arg)
    string? FallbackMethodName,
    RetryConfig? Retry,
    TimeoutConfig? Timeout,
    RateLimitConfig? RateLimit,
    CircuitBreakerConfig? CircuitBreaker
);

// Effective config = method-level ?? class-level
internal sealed record RetryConfig(int MaxAttempts, int BackoffMs, bool Jitter, int PerAttemptTimeoutMs);
internal sealed record TimeoutConfig(int TotalMs);
internal enum RateLimitScope { Shared, Instance }
internal sealed record RateLimitConfig(int MaxPerSecond, int BurstSize, RateLimitScope Scope);
internal sealed record CircuitBreakerConfig(int MaxFailures, int ResetMs, int HalfOpenProbes);
