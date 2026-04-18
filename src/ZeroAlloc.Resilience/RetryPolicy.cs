using System;

namespace ZeroAlloc.Resilience;

/// <summary>
/// Stateless retry configuration. All retry loop logic is emitted in the generated proxy;
/// this object is injected as a constructor dependency and read on each call.
/// </summary>
public sealed class RetryPolicy
{
    /// <summary>Total number of attempts (initial + retries).</summary>
    public int MaxAttempts { get; }

    /// <summary>Base backoff in milliseconds. The actual backoff per attempt is <c>BackoffMs * 2^attempt</c>.</summary>
    public int BackoffMs { get; }

    /// <summary>Whether random jitter (up to 50% of base) is added to the backoff.</summary>
    public bool Jitter { get; }

    /// <summary>Per-attempt timeout in milliseconds. 0 = disabled.</summary>
    public int PerAttemptTimeoutMs { get; }

    /// <param name="maxAttempts">Total attempts including the initial call.</param>
    /// <param name="backoffMs">Base backoff milliseconds (exponential per attempt).</param>
    /// <param name="jitter">Add random jitter to prevent thundering herd.</param>
    /// <param name="perAttemptTimeoutMs">Per-attempt cancellation timeout. 0 = disabled.</param>
    public RetryPolicy(int maxAttempts, int backoffMs, bool jitter, int perAttemptTimeoutMs)
    {
        MaxAttempts = maxAttempts;
        BackoffMs = backoffMs;
        Jitter = jitter;
        PerAttemptTimeoutMs = perAttemptTimeoutMs;
    }

    /// <summary>Computes the backoff delay for a given attempt index (0-based).</summary>
    /// <param name="attempt">Zero-based attempt index (0 = first retry delay).</param>
    public int GetBackoffMs(int attempt)
    {
        var ms = BackoffMs * (1 << attempt); // exponential: 200, 400, 800…
        if (Jitter)
            ms += Random.Shared.Next(0, Math.Max(1, ms / 2));
        return ms;
    }
}
