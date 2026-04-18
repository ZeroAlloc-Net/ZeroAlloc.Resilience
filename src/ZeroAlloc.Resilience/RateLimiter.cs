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
    private long _lastRefillTick;
    private readonly int _maxPerSecond;
    private readonly long _burstSize;

    /// <summary>The configured scope for this limiter.</summary>
    public RateLimitScope Scope { get; }

    /// <param name="maxPerSecond">Tokens added per second.</param>
    /// <param name="burstSize">Initial and maximum token count.</param>
    /// <param name="scope">Whether this limiter is shared or per-instance.</param>
    public RateLimiter(int maxPerSecond, int burstSize, RateLimitScope scope)
    {
        _maxPerSecond = maxPerSecond;
        _burstSize = burstSize;
        Scope = scope;
        _tokens = burstSize;
        _lastRefillTick = Environment.TickCount64;
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
        var now = Environment.TickCount64;
        var last = Volatile.Read(ref _lastRefillTick);
        var elapsed = now - last;
        if (elapsed <= 0) return;

        var toAdd = elapsed * _maxPerSecond / 1_000L;
        if (toAdd <= 0) return;

        if (Interlocked.CompareExchange(ref _lastRefillTick, now, last) != last) return;
        var current = Volatile.Read(ref _tokens);
        var next = Math.Min(current + toAdd, _burstSize);
        Volatile.Write(ref _tokens, next);
    }
}
