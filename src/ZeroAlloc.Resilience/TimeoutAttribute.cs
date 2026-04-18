namespace ZeroAlloc.Resilience;

/// <summary>
/// Applies a total operation timeout wrapping all retry attempts and backoff delays.
/// Method-level declarations shadow interface-level ones entirely for that method.
/// </summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Method, AllowMultiple = false)]
public sealed class TimeoutAttribute : Attribute
{
    /// <summary>Total operation timeout in milliseconds.</summary>
    public required int Ms { get; init; }
}
