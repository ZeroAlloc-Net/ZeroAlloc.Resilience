namespace ZeroAlloc.Resilience;

/// <summary>Identifies which policy caused a <see cref="ResilienceException"/>.</summary>
public enum ResiliencePolicy { Retry, Timeout, RateLimit, CircuitBreaker }
