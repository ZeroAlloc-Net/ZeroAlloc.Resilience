namespace ZeroAlloc.Resilience;

/// <summary>
/// Configures retry behaviour for a resilient interface or individual method.
/// Method-level declarations shadow interface-level ones entirely for that method.
/// </summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RetryAttribute : Attribute
{
    /// <summary>Total number of attempts (initial + retries). Default: 3.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Base backoff in milliseconds between attempts. Backoff is exponential (BackoffMs * 2^attempt). Default: 200.</summary>
    public int BackoffMs { get; init; } = 200;

    /// <summary>Add random jitter up to 50% of the base backoff. Default: false.</summary>
    public bool Jitter { get; init; } = false;

    /// <summary>Per-attempt timeout in milliseconds. 0 = no per-attempt timeout. Default: 0.</summary>
    public int PerAttemptTimeoutMs { get; init; } = 0;

    /// <summary>
    /// When <see langword="true"/>, the method must return <c>Result&lt;T, ResilienceError&gt;</c>
    /// or <c>UnitResult&lt;ResilienceError&gt;</c>. Methods returning those types already get a
    /// failure instead of <see cref="ResilienceException"/> without this flag, so it only
    /// asserts the return type.
    /// </summary>
    public bool NonThrowing { get; init; } = false;

    /// <summary>
    /// The name of a static method <c>bool M(E error)</c> on the interface or a base interface,
    /// where <c>E</c> is the error type of the method's Result. A failed Result it returns
    /// <see langword="true"/> for is retried, and counts as a circuit-breaker failure; one it
    /// returns <see langword="false"/> for is returned at once. Default: <see langword="null"/>,
    /// a returned Result is never retried.
    /// </summary>
    public string? RetryWhen { get; init; }

    /// <summary>
    /// The name of a static method <c>bool M(Exception exception)</c> on the interface or a base
    /// interface. A thrown exception it returns <see langword="false"/> for ends the retries.
    /// Default: <see langword="null"/>, every exception is retried.
    /// </summary>
    public string? RetryOnException { get; init; }

    /// <summary>
    /// The name of a static method, or overload set, on the interface or a base interface:
    /// <c>TimeSpan? M(E error)</c> for a failed Result and <c>TimeSpan? M(Exception exception)</c>
    /// for a thrown exception. A non-null result replaces the backoff before the next attempt,
    /// without jitter. Default: <see langword="null"/>.
    /// </summary>
    public string? DelayHint { get; init; }

    /// <summary>
    /// The longest wait between attempts in milliseconds, for the backoff and a delay hint alike.
    /// Default: <see cref="RetryPolicy.MaxBackoffMs"/>, no cap.
    /// </summary>
    public int MaxDelayMs { get; init; } = RetryPolicy.MaxBackoffMs;
}
