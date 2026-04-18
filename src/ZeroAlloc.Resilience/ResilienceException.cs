namespace ZeroAlloc.Resilience;

/// <summary>
/// Thrown when a resilience policy exhausts its budget and the method does not return
/// a <c>Result&lt;T, E&gt;</c> type. Inspect <see cref="Policy"/> to determine the cause.
/// </summary>
public sealed class ResilienceException : Exception
{
    /// <summary>The policy that triggered this exception.</summary>
    public ResiliencePolicy Policy { get; }

    /// <param name="policy">The policy that triggered this exception.</param>
    /// <param name="message">Error message.</param>
    /// <param name="inner">The inner exception, if any.</param>
    public ResilienceException(ResiliencePolicy policy, string message, Exception? inner = null)
        : base(message, inner)
    {
        Policy = policy;
    }
}
