using System;
using System.Threading;

namespace ZeroAlloc.Resilience;

/// <summary>
/// Lock-free token-bucket rate limiter.
/// Uses <see cref="Interlocked.CompareExchange"/> — zero allocation on <see cref="TryAcquire"/>.
/// </summary>
public sealed class RateLimiter
{
    private long _tokens;
    private long _lastRefillTimestamp;
    private readonly int _maxPerSecond;
    private readonly long _burstSize;
    private readonly TimeProvider _timeProvider;

    /// <summary>The configured scope for this limiter.</summary>
    public RateLimitScope Scope { get; }

    /// <param name="maxPerSecond">Tokens added per second.</param>
    /// <param name="burstSize">Initial and maximum token count.</param>
    /// <param name="scope">Whether this limiter is shared or per-instance.</param>
    public RateLimiter(int maxPerSecond, int burstSize, RateLimitScope scope)
        : this(maxPerSecond, burstSize, scope, TimeProvider.System)
    {
    }

    /// <param name="maxPerSecond">Tokens added per second.</param>
    /// <param name="burstSize">Initial and maximum token count.</param>
    /// <param name="scope">Whether this limiter is shared or per-instance.</param>
    /// <param name="timeProvider">
    /// Clock used to measure refill intervals. Pass a controlled provider to make refill
    /// behaviour deterministic; the default reads the system clock.
    /// </param>
    /// <remarks>
    /// Refill is measured with <see cref="TimeProvider.GetTimestamp"/> rather than a wall clock,
    /// so it is monotonic and unaffected by system time changes.
    /// </remarks>
    public RateLimiter(int maxPerSecond, int burstSize, RateLimitScope scope, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _maxPerSecond = maxPerSecond;
        _burstSize = burstSize;
        Scope = scope;
        _tokens = burstSize;
        _lastRefillTimestamp = _timeProvider.GetTimestamp();
    }

    /// <summary>Attempts to consume one token. Returns <c>false</c> if the bucket is empty.</summary>
    public bool TryAcquire()
    {
        Refill();
        while (true)
        {
            var current = Volatile.Read(ref _tokens);
            if (current <= 0) return false;
            if (Interlocked.CompareExchange(ref _tokens, current - 1, current) == current)
                return true;
        }
    }

    private void Refill()
    {
        var now = _timeProvider.GetTimestamp();
        var last = Volatile.Read(ref _lastRefillTimestamp);
        // Timestamps are in TimestampFrequency units, so convert to milliseconds before the
        // token maths. Leaving _lastRefillTimestamp untouched when this rounds down to zero is
        // deliberate: sub-millisecond elapsed time accumulates instead of being discarded.
        var elapsedMs = (now - last) * 1_000L / _timeProvider.TimestampFrequency;
        if (elapsedMs <= 0) return;

        var toAdd = elapsedMs * _maxPerSecond / 1_000L;
        if (toAdd <= 0) return;

        // Only one thread wins the CAS on _lastRefillTimestamp — prevents double-refill
        if (Interlocked.CompareExchange(ref _lastRefillTimestamp, now, last) != last) return;

        // CAS loop on _tokens so concurrent TryAcquire() consumptions are not overwritten
        long current, next;
        do
        {
            current = Volatile.Read(ref _tokens);
            next = Math.Min(current + toAdd, _burstSize);
        }
        while (Interlocked.CompareExchange(ref _tokens, next, current) != current);
    }
}
