using System;
using System.Threading;

namespace ZeroAlloc.Resilience;

/// <summary>
/// Thread-safe circuit breaker backed by <see cref="CircuitBreakerFsm"/> (ZeroAlloc.StateMachine, concurrent mode).
/// All state transitions use <see cref="Interlocked.CompareExchange"/> on a single <c>long</c> field.
/// </summary>
public sealed class CircuitBreakerPolicy : IDisposable
{
    private readonly CircuitBreakerFsm _fsm = new();
    private readonly int _maxFailures;
    private readonly int _resetMs;
    private readonly int _halfOpenProbes;
    private long _failureCount;
    private long _probeSuccessCount;
    private Timer? _resetTimer;

    /// <param name="maxFailures">Consecutive failures that trip Closed → Open.</param>
    /// <param name="resetMs">Milliseconds before Open → HalfOpen probe.</param>
    /// <param name="halfOpenProbes">Successes required to close from HalfOpen.</param>
    public CircuitBreakerPolicy(int maxFailures, int resetMs, int halfOpenProbes)
    {
        _maxFailures = maxFailures;
        _resetMs = resetMs;
        _halfOpenProbes = halfOpenProbes;
    }

    /// <summary>Current FSM state.</summary>
    public CircuitBreakerState State => _fsm.Current;

    /// <summary>Returns <c>false</c> when the circuit is Open (fast-reject path, no allocation).</summary>
    public bool CanExecute() => _fsm.Current != CircuitBreakerState.Open;

    /// <summary>Call after a successful inner invocation.</summary>
    public void OnSuccess()
    {
        if (_fsm.Current == CircuitBreakerState.HalfOpen)
        {
            var count = Interlocked.Increment(ref _probeSuccessCount);
            if (count >= _halfOpenProbes)
            {
                Interlocked.Exchange(ref _failureCount, 0);
                Interlocked.Exchange(ref _probeSuccessCount, 0);
                CancelProbe();
                _fsm.TryFire(CircuitBreakerTrigger.Success);
            }
        }
        else
        {
            Interlocked.Exchange(ref _failureCount, 0);
        }
    }

    /// <summary>Call after a failed inner invocation.</summary>
    public void OnFailure(Exception _)
    {
        var state = _fsm.Current;

        if (state == CircuitBreakerState.HalfOpen)
        {
            if (_fsm.TryFire(CircuitBreakerTrigger.FailInHalfOpen))
                ScheduleProbe();
            return;
        }

        if (state == CircuitBreakerState.Closed)
        {
            var failures = Interlocked.Increment(ref _failureCount);
            if (failures >= _maxFailures && _fsm.TryFire(CircuitBreakerTrigger.Trip))
                ScheduleProbe();
        }
    }

    private void ScheduleProbe()
    {
        // Create the new timer first, then atomically swap and dispose the old one.
        var newTimer = new Timer(static s =>
        {
            var self = (CircuitBreakerPolicy)s!;
            Interlocked.Exchange(ref self._probeSuccessCount, 0);
            self._fsm.TryFire(CircuitBreakerTrigger.Probe);
        }, this, _resetMs, Timeout.Infinite);

        Interlocked.Exchange(ref _resetTimer, newTimer)?.Dispose();
    }

    private void CancelProbe()
    {
        Interlocked.Exchange(ref _resetTimer, null)?.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Interlocked.Exchange(ref _resetTimer, null)?.Dispose();
    }
}
