namespace ZeroAlloc.Resilience;

/// <summary>Controls whether a <see cref="RateLimitAttribute"/> token bucket is shared across all proxy instances or per instance.</summary>
public enum RateLimitScope
{
    /// <summary>One token bucket per interface type, registered as singleton in DI. Default.</summary>
    Shared,

    /// <summary>One token bucket per proxy instance.</summary>
    Instance
}
