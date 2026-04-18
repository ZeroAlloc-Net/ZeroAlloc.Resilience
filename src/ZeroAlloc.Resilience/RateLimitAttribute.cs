namespace ZeroAlloc.Resilience;

/// <summary>
/// Applies a lock-free token-bucket rate limit to a resilient interface or individual method.
/// Method-level declarations shadow interface-level ones entirely for that method.
/// </summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RateLimitAttribute : Attribute
{
    /// <summary>Maximum requests granted per second.</summary>
    public int MaxPerSecond { get; init; }

    /// <summary>Maximum burst size (initial and peak token count). Default: 1.</summary>
    public int BurstSize { get; init; } = 1;

    /// <summary>Whether the limiter is shared across all proxy instances or per instance. Default: <see cref="RateLimitScope.Shared"/>.</summary>
    public RateLimitScope Scope { get; init; } = RateLimitScope.Shared;
}
