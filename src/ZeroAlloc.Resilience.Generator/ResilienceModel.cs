using Microsoft.CodeAnalysis;
using System.Collections.Immutable;

namespace ZeroAlloc.Resilience.Generator;

internal sealed record ResilienceModel(
    string? Namespace,
    string InterfaceName,
    string InterfaceFqn,            // e.g. global::MyApp.IExternalService
    bool IsPublic,                  // interface and every containing type are public
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
    ResultKind ResultKind,          // shape of the ZeroAlloc.Results type the method returns, if any
    string? ResultTypeFqn,          // e.g. global::ZeroAlloc.Results.Result<string>; null when ResultKind is None
    bool IsAsync,                   // true if ValueTask or Task
    bool ReturnsValue,              // false for void, ValueTask and Task: the inner call is a statement
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
)
{
    // Methods returning a Result whose failure the generator can build, sync or async, return
    // that failure from every policy failure site instead of throwing.
    public bool ReturnsFailureResult =>
        ResultKind is ResultKind.StringError or ResultKind.ResilienceError;
}

// The ZeroAlloc.Results return shapes the generator distinguishes.
internal enum ResultKind
{
    // Not a ZeroAlloc.Results type.
    None,
    // Result or Result<T>: the error is a string, built with Failure(string).
    StringError,
    // Result<T, ResilienceError> or UnitResult<ResilienceError>: built with Failure(new ResilienceError(...)).
    ResilienceError,
    // Result<T, E> or UnitResult<E> for any other E: the generator cannot build an E, so it only
    // passes Results returned by the inner call through.
    ForeignError,
}

// Effective config = method-level ?? class-level
internal sealed record RetryConfig(int MaxAttempts, int BackoffMs, bool Jitter, int PerAttemptTimeoutMs, bool NonThrowing = false);
internal sealed record TimeoutConfig(int TotalMs);
internal enum RateLimitScope { Shared, Instance }
internal sealed record RateLimitConfig(int MaxPerSecond, int BurstSize, RateLimitScope Scope);
internal sealed record CircuitBreakerConfig(int MaxFailures, int ResetMs, int HalfOpenProbes);
