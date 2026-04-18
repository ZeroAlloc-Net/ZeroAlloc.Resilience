namespace ZeroAlloc.Resilience;

/// <summary>Identifies which policy caused a <see cref="ResilienceException"/>.</summary>
public enum ResiliencePolicy
{
    /// <summary>The retry budget was exhausted.</summary>
    Retry,
    /// <summary>The total operation timeout elapsed.</summary>
    Timeout,
    /// <summary>The rate limit token bucket was empty.</summary>
    RateLimit,
    /// <summary>The circuit breaker was open.</summary>
    CircuitBreaker
}
