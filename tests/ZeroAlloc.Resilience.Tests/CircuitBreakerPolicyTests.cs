using System;
using System.Threading.Tasks;
using ZeroAlloc.Resilience;

namespace ZeroAlloc.Resilience.Tests;

public class CircuitBreakerPolicyTests : IDisposable
{
    private readonly CircuitBreakerPolicy _cb = new(maxFailures: 3, resetMs: 50, halfOpenProbes: 1);

    [Fact]
    public void InitialState_IsClosed_AndCanExecute()
    {
        _cb.State.Should().Be(CircuitBreakerState.Closed);
        _cb.CanExecute().Should().BeTrue();
    }

    [Fact]
    public void AfterMaxFailures_CircuitOpens()
    {
        _cb.OnFailure(new Exception());
        _cb.OnFailure(new Exception());
        _cb.OnFailure(new Exception()); // 3rd failure
        _cb.State.Should().Be(CircuitBreakerState.Open);
        _cb.CanExecute().Should().BeFalse();
    }

    [Fact]
    public async Task AfterResetMs_CircuitTransitionsToHalfOpen()
    {
        _cb.OnFailure(new Exception());
        _cb.OnFailure(new Exception());
        _cb.OnFailure(new Exception());
        _cb.State.Should().Be(CircuitBreakerState.Open);

        await Task.Delay(200); // wait for probe timer (resetMs = 50)
        _cb.State.Should().Be(CircuitBreakerState.HalfOpen);
        _cb.CanExecute().Should().BeTrue();
    }

    [Fact]
    public async Task SuccessInHalfOpen_ClosesCircuit()
    {
        _cb.OnFailure(new Exception());
        _cb.OnFailure(new Exception());
        _cb.OnFailure(new Exception());
        await Task.Delay(200);
        _cb.State.Should().Be(CircuitBreakerState.HalfOpen);

        _cb.OnSuccess();
        _cb.State.Should().Be(CircuitBreakerState.Closed);
        _cb.CanExecute().Should().BeTrue();
    }

    [Fact]
    public async Task FailureInHalfOpen_ReOpensCircuit()
    {
        _cb.OnFailure(new Exception());
        _cb.OnFailure(new Exception());
        _cb.OnFailure(new Exception());
        await Task.Delay(200);
        _cb.State.Should().Be(CircuitBreakerState.HalfOpen);

        _cb.OnFailure(new Exception());
        _cb.State.Should().Be(CircuitBreakerState.Open);
    }

    [Fact]
    public void SuccessInClosed_ResetsFailureCount()
    {
        _cb.OnFailure(new Exception());
        _cb.OnFailure(new Exception());
        _cb.OnSuccess(); // resets count
        _cb.OnFailure(new Exception());
        _cb.OnFailure(new Exception()); // only 2 failures — should stay closed
        _cb.State.Should().Be(CircuitBreakerState.Closed);
    }

    public void Dispose() => _cb.Dispose();
}
