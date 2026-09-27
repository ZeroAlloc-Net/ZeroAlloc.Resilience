using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using ZeroAlloc.Resilience;

namespace ZeroAlloc.Resilience.Tests;

public class CircuitBreakerPolicyTests : IDisposable
{
    // The policy reads this clock, so the Open -> HalfOpen probe fires exactly when the test
    // advances it. The earlier tests slept 200 ms and hoped the real timer had fired, which
    // failed on loaded runners; see #192.
    private readonly FakeTimeProvider _time = new();
    private readonly CircuitBreakerPolicy _cb;

    public CircuitBreakerPolicyTests()
    {
        _cb = new CircuitBreakerPolicy(maxFailures: 3, resetMs: 50, halfOpenProbes: 1, _time);
    }

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
    public void AfterResetMs_CircuitTransitionsToHalfOpen()
    {
        _cb.OnFailure(new Exception());
        _cb.OnFailure(new Exception());
        _cb.OnFailure(new Exception());
        _cb.State.Should().Be(CircuitBreakerState.Open);

        _time.Advance(TimeSpan.FromMilliseconds(50)); // resetMs = 50
        _cb.State.Should().Be(CircuitBreakerState.HalfOpen);
        _cb.CanExecute().Should().BeTrue();
    }

    [Fact]
    public void BeforeResetMs_CircuitStaysOpen()
    {
        // The other half of the contract, which a real timer could never assert.
        _cb.OnFailure(new Exception());
        _cb.OnFailure(new Exception());
        _cb.OnFailure(new Exception());

        _time.Advance(TimeSpan.FromMilliseconds(49));
        _cb.State.Should().Be(CircuitBreakerState.Open);
        _cb.CanExecute().Should().BeFalse();
    }

    [Fact]
    public void FailureInHalfOpen_RestartsTheResetInterval()
    {
        _cb.OnFailure(new Exception());
        _cb.OnFailure(new Exception());
        _cb.OnFailure(new Exception());
        _time.Advance(TimeSpan.FromMilliseconds(50));
        _cb.OnFailure(new Exception()); // back to Open, with a fresh 50 ms probe timer

        _time.Advance(TimeSpan.FromMilliseconds(49));
        _cb.State.Should().Be(CircuitBreakerState.Open);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        _cb.State.Should().Be(CircuitBreakerState.HalfOpen);
    }

    [Fact]
    public async Task DefaultConstructor_TransitionsWithRealTime()
    {
        // The provider-less constructor must keep using the system clock.
        using var cb = new CircuitBreakerPolicy(maxFailures: 1, resetMs: 1, halfOpenProbes: 1);
        cb.OnFailure(new Exception());
        cb.State.Should().Be(CircuitBreakerState.Open);

        // Poll rather than sleep a fixed time, so a loaded runner only makes this slower.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (cb.State == CircuitBreakerState.Open && !timeout.IsCancellationRequested)
            await Task.Delay(5, CancellationToken.None);

        cb.State.Should().Be(CircuitBreakerState.HalfOpen);
    }

    [Fact]
    public void Constructor_NullTimeProvider_Throws()
    {
        var act = () => new CircuitBreakerPolicy(1, 1, 1, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("timeProvider");
    }

    [Fact]
    public void SuccessInHalfOpen_ClosesCircuit()
    {
        _cb.OnFailure(new Exception());
        _cb.OnFailure(new Exception());
        _cb.OnFailure(new Exception());
        _time.Advance(TimeSpan.FromMilliseconds(50));
        _cb.State.Should().Be(CircuitBreakerState.HalfOpen);

        _cb.OnSuccess();
        _cb.State.Should().Be(CircuitBreakerState.Closed);
        _cb.CanExecute().Should().BeTrue();
    }

    [Fact]
    public void FailureInHalfOpen_ReOpensCircuit()
    {
        _cb.OnFailure(new Exception());
        _cb.OnFailure(new Exception());
        _cb.OnFailure(new Exception());
        _time.Advance(TimeSpan.FromMilliseconds(50));
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
    public void HalfOpenProbes_MultipleSuccessesRequired_ToClose()
    {
        using var cb = new CircuitBreakerPolicy(maxFailures: 2, resetMs: 30, halfOpenProbes: 3, _time);

        // Trip the circuit
        cb.OnFailure(new Exception());
        cb.OnFailure(new Exception());
        cb.State.Should().Be(CircuitBreakerState.Open);

        // Reach the HalfOpen probe
        _time.Advance(TimeSpan.FromMilliseconds(30));
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

    [Fact]
    public void OnFailureWithoutException_CountsTowardOpening()
    {
        _cb.OnFailure();
        _cb.OnFailure();
        _cb.OnFailure();
        _cb.State.Should().Be(CircuitBreakerState.Open);
    }

    [Fact]
    public void OnFailureWithoutException_CountsTogetherWithExceptionFailures()
    {
        _cb.OnFailure(new Exception());
        _cb.OnFailure();
        _cb.OnFailure(new Exception());
        _cb.State.Should().Be(CircuitBreakerState.Open);
    }

    [Fact]
    public void OnFailureWithoutException_InHalfOpen_ReOpensCircuit()
    {
        _cb.OnFailure();
        _cb.OnFailure();
        _cb.OnFailure();
        _time.Advance(TimeSpan.FromMilliseconds(50));
        _cb.State.Should().Be(CircuitBreakerState.HalfOpen);

        _cb.OnFailure();
        _cb.State.Should().Be(CircuitBreakerState.Open);
    }

    public void Dispose() => _cb.Dispose();
}
