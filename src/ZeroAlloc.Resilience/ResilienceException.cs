namespace ZeroAlloc.Resilience;

/// <summary>
/// Thrown when a resilience policy exhausts its budget and the method does not return
/// a <c>Result&lt;T, E&gt;</c> type. Inspect <see cref="Policy"/> to determine the cause.
/// </summary>
public sealed class ResilienceException : Exception
{
    /// <summary>The policy that triggered this exception.</summary>
    public ResiliencePolicy Policy { get; }

    public ResilienceException(ResiliencePolicy policy, string message, Exception? inner = null)
        : base(message, inner)
    {
        Policy = policy;
    }
}
