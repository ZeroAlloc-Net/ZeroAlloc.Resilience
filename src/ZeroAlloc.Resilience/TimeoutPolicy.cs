using System;

namespace ZeroAlloc.Resilience;

/// <summary>
/// Stateless total-timeout configuration. The <see cref="System.Threading.CancellationTokenSource"/>
/// wrapping the operation is created per-call in the generated proxy.
/// </summary>
public sealed class TimeoutPolicy
{
    /// <summary>Total operation timeout in milliseconds.</summary>
    public int TotalMs { get; }

    /// <param name="totalMs">Total operation timeout in milliseconds.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="totalMs"/> is zero or negative.</exception>
    public TimeoutPolicy(int totalMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalMs);
        TotalMs = totalMs;
    }
}
