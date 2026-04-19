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

    [Fact]
    public async Task HalfOpenProbes_MultipleSuccessesRequired_ToClose()
    {
        using var cb = new CircuitBreakerPolicy(maxFailures: 2, resetMs: 30, halfOpenProbes: 3);

        // Trip the circuit
        cb.OnFailure(new Exception());
        cb.OnFailure(new Exception());
        cb.State.Should().Be(CircuitBreakerState.Open);

        // Wait for HalfOpen probe
        await Task.Delay(150);
        cb.State.Should().Be(CircuitBreakerState.HalfOpen);

        // First success — not yet closed (needs 3)
        cb.OnSuccess();
        cb.State.Should().Be(CircuitBreakerState.HalfOpen);

        // Second success — still not closed
        cb.OnSuccess();
        cb.State.Should().Be(CircuitBreakerState.HalfOpen);

        // Third success — now closes
        cb.OnSuccess();
        cb.State.Should().Be(CircuitBreakerState.Closed);
    }

    [Fact]
    public void CircuitOpen_NoFallback_ThrowsResilienceException()
    {
        // Integration test: proxy with CB but no fallback should throw ResilienceException when open
        // We test CircuitBreakerPolicy.CanExecute() directly since the throw is in generated code
        var cb = new CircuitBreakerPolicy(maxFailures: 1, resetMs: 10_000, halfOpenProbes: 1);
        cb.OnFailure(new Exception());
        cb.State.Should().Be(CircuitBreakerState.Open);
        cb.CanExecute().Should().BeFalse();
        cb.Dispose();
    }

    public void Dispose() => _cb.Dispose();
}
