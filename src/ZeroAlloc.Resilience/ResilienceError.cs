namespace ZeroAlloc.Resilience;

/// <summary>
/// Describes a resilience-policy failure on the non-throwing path.
/// Returned as the error value of <c>Result&lt;T, ResilienceError&gt;</c> when
/// <c>[Retry(NonThrowing = true)]</c> is used and all retry attempts are exhausted.
/// </summary>
/// <param name="PolicyType">
/// The name of the policy that failed (e.g. <c>"Retry"</c>).
/// Corresponds to the <see cref="ResiliencePolicy"/> enum member name.
/// </param>
/// <param name="Reason">Human-readable failure description.</param>
/// <param name="InnerException">The last exception thrown by the inner operation, if any.</param>
public readonly record struct ResilienceError(
    string PolicyType,
    string Reason,
    Exception? InnerException = null)
{
    public override string ToString() => $"{PolicyType}: {Reason}";
}
