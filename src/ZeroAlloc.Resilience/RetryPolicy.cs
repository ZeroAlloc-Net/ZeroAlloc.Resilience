using System;

namespace ZeroAlloc.Resilience;

/// <summary>
/// Stateless retry configuration. All retry loop logic is emitted in the generated proxy;
/// this object is injected as a constructor dependency and read on each call.
/// </summary>
public sealed class RetryPolicy
{
    /// <summary>
    /// The longest delay GetBackoffMs returns, so the value is always a valid Task.Delay argument.
    /// </summary>
    public const int MaxBackoffMs = int.MaxValue - 1;

    /// <summary>Total number of attempts (initial + retries).</summary>
    public int MaxAttempts { get; }

    /// <summary>Base backoff in milliseconds. The actual backoff per attempt is <c>BackoffMs * 2^attempt</c>.</summary>
    public int BackoffMs { get; }

    /// <summary>Whether random jitter (up to 50% of base) is added to the backoff.</summary>
    public bool Jitter { get; }

    /// <summary>Per-attempt timeout in milliseconds. 0 = disabled.</summary>
    public int PerAttemptTimeoutMs { get; }

    /// <summary>
    /// The longest wait between attempts, in milliseconds, for the exponential backoff and a
    /// delay hint alike. <see cref="MaxBackoffMs"/>, the default, is no cap.
    /// </summary>
    public int MaxDelayMs { get; }

    /// <param name="maxAttempts">Total attempts including the initial call.</param>
    /// <param name="backoffMs">Base backoff milliseconds (exponential per attempt).</param>
    /// <param name="jitter">Add random jitter to prevent thundering herd.</param>
    /// <param name="perAttemptTimeoutMs">Per-attempt cancellation timeout. 0 = disabled.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxAttempts"/> is less than 1, or <paramref name="backoffMs"/> or
    /// <paramref name="perAttemptTimeoutMs"/> is negative.
    /// </exception>
    public RetryPolicy(int maxAttempts, int backoffMs, bool jitter, int perAttemptTimeoutMs)
        : this(maxAttempts, backoffMs, jitter, perAttemptTimeoutMs, MaxBackoffMs)
    {
    }

    /// <param name="maxAttempts">Total attempts including the initial call.</param>
    /// <param name="backoffMs">Base backoff milliseconds (exponential per attempt).</param>
    /// <param name="jitter">Add random jitter to prevent thundering herd.</param>
    /// <param name="perAttemptTimeoutMs">Per-attempt cancellation timeout. 0 = disabled.</param>
    /// <param name="maxDelayMs">The longest wait between attempts, backoff or hint.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxAttempts"/> is less than 1, or <paramref name="backoffMs"/>,
    /// <paramref name="perAttemptTimeoutMs"/> or <paramref name="maxDelayMs"/> is negative.
    /// </exception>
    public RetryPolicy(int maxAttempts, int backoffMs, bool jitter, int perAttemptTimeoutMs, int maxDelayMs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(backoffMs);
        ArgumentOutOfRangeException.ThrowIfNegative(perAttemptTimeoutMs);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDelayMs);
        MaxAttempts = maxAttempts;
        BackoffMs = backoffMs;
        Jitter = jitter;
        PerAttemptTimeoutMs = perAttemptTimeoutMs;
        MaxDelayMs = maxDelayMs;
    }

    // Every delay is capped here, so it is always a valid Task.Delay, WaitOne and Thread.Sleep
    // argument whatever MaxDelayMs is.
    private int DelayCap => Math.Min(MaxDelayMs, MaxBackoffMs);

    /// <summary>
    /// Computes the backoff delay for a given attempt index (0-based), capped at
    /// <see cref="MaxDelayMs"/> and never above <see cref="MaxBackoffMs"/>.
    /// </summary>
    /// <param name="attempt">Zero-based attempt index (0 = first retry delay).</param>
    public int GetBackoffMs(int attempt)
    {
        // Exponential: 200, 400, 800... Computed in long and capped, so a large attempt count can
        // never overflow into a negative or infinite delay.
        var exponent = Math.Min(Math.Max(attempt, 0), 30);
        var ms = Math.Min((long)BackoffMs << exponent, MaxBackoffMs);
        if (Jitter)
            ms += Random.Shared.NextInt64(0, Math.Max(1, ms / 2));
        return (int)Math.Min(ms, DelayCap);
    }

    /// <summary>
    /// The wait before the next attempt: <paramref name="hint"/> when there is one, otherwise
    /// <see cref="GetBackoffMs"/>, capped at <see cref="MaxDelayMs"/>. A hint gets no jitter,
    /// is rounded up to a whole millisecond, and counts as 0 when it is negative.
    /// </summary>
    /// <param name="attempt">Zero-based attempt index (0 = first retry delay).</param>
    /// <param name="hint">The delay the failure asked for, such as an HTTP Retry-After.</param>
    public int GetDelayMs(int attempt, TimeSpan? hint)
    {
        if (hint is not { } requested)
            return GetBackoffMs(attempt);

        var ticks = requested.Ticks;
        if (ticks <= 0)
            return 0;

        // Rounded up, so the wait is never shorter than the failure asked for. Division first,
        // so TimeSpan.MaxValue cannot overflow.
        var ms = ticks / TimeSpan.TicksPerMillisecond + (ticks % TimeSpan.TicksPerMillisecond == 0 ? 0 : 1);
        return (int)Math.Min(ms, DelayCap);
    }
}
