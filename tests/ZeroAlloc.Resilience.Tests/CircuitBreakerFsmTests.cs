using ZeroAlloc.Resilience;

namespace ZeroAlloc.Resilience.Tests;

public class CircuitBreakerFsmTests
{
    [Theory]
    [InlineData(CircuitBreakerState.Closed)]
    [InlineData(CircuitBreakerState.Open)]
    [InlineData(CircuitBreakerState.HalfOpen)]
    public void Restore_StartsTheMachineInThePersistedState(CircuitBreakerState state)
    {
        var fsm = CircuitBreakerFsm.Restore(state);

        Assert.Equal(state, fsm.Current);
    }

    [Fact]
    public void Restore_Open_FiresTheTransitionsOfThatState()
    {
        var fsm = CircuitBreakerFsm.Restore(CircuitBreakerState.Open);

        Assert.False(fsm.TryFire(CircuitBreakerTrigger.Trip));
        Assert.True(fsm.TryFire(CircuitBreakerTrigger.Probe));
        Assert.Equal(CircuitBreakerState.HalfOpen, fsm.Current);
    }
}
