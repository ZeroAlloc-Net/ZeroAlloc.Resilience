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
}
