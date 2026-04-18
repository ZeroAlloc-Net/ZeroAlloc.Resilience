namespace ZeroAlloc.Resilience;

/// <summary>
/// Applies a circuit breaker (Closed → Open → HalfOpen → Closed) to a resilient interface or individual method.
/// Method-level declarations shadow interface-level ones entirely for that method.
/// </summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Method, AllowMultiple = false)]
public sealed class CircuitBreakerAttribute : Attribute
{
    /// <summary>Number of consecutive failures that trip the circuit from Closed to Open. Default: 5.</summary>
    public int MaxFailures { get; init; } = 5;

    /// <summary>Milliseconds before the circuit probes from Open to HalfOpen. Default: 1000.</summary>
    public int ResetMs { get; init; } = 1_000;

    /// <summary>Number of probe successes required to close the circuit from HalfOpen. Default: 1.</summary>
    public int HalfOpenProbes { get; init; } = 1;

    /// <summary>
    /// Name of a fallback method on the same interface to invoke when the circuit is Open.
    /// The fallback must have the same parameter types and return type as the annotated method.
    /// The generator validates this at compile time (ZR0001).
    /// </summary>
    public string? Fallback { get; init; }
}
