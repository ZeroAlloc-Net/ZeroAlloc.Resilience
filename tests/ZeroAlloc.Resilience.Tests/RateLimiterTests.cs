using Microsoft.Extensions.Time.Testing;
using System.Threading.Tasks;
using ZeroAlloc.Resilience;

namespace ZeroAlloc.Resilience.Tests;

public class RateLimiterTests
{
    [Fact]
    public void BurstTokens_AllAcquirable_Immediately()
    {
        var limiter = new RateLimiter(maxPerSecond: 10, burstSize: 3, scope: RateLimitScope.Shared);
        limiter.TryAcquire().Should().BeTrue();
        limiter.TryAcquire().Should().BeTrue();
        limiter.TryAcquire().Should().BeTrue();
        limiter.TryAcquire().Should().BeFalse(); // bucket empty
    }

    [Fact]
    public void AfterElapsedTime_TokensRefill()
    {
        // Deterministic: the limiter reads this clock, so no wall-clock timing is involved.
        // The previous version ran at 100/s against Environment.TickCount64, whose ~15ms
        // granularity exceeded the 10ms refill interval -- a single tick handed back a whole
        // token and the "bucket is empty" assertion failed roughly two runs in five.
        var time = new FakeTimeProvider();
        var limiter = new RateLimiter(maxPerSecond: 100, burstSize: 1, RateLimitScope.Shared, time);

        limiter.TryAcquire().Should().BeTrue();   // consumes the burst token
        limiter.TryAcquire().Should().BeFalse();  // empty, and the clock cannot have moved

        time.Advance(TimeSpan.FromMilliseconds(50)); // exactly 5 tokens at 100/s
        limiter.TryAcquire().Should().BeTrue();
    }

    [Fact]
    public void BeforeRefillInterval_StaysEmpty()
    {
        // The other half of the contract, which wall-clock timing could never assert:
        // just under one token's worth of time must refill nothing.
        var time = new FakeTimeProvider();
        var limiter = new RateLimiter(maxPerSecond: 100, burstSize: 1, RateLimitScope.Shared, time);

        limiter.TryAcquire().Should().BeTrue();
        time.Advance(TimeSpan.FromMilliseconds(9)); // one token needs 10ms
        limiter.TryAcquire().Should().BeFalse();
    }

    [Fact]
    public void Scope_Property_ReflectsConstructorArg()
    {
        new RateLimiter(10, 1, RateLimitScope.Shared).Scope.Should().Be(RateLimitScope.Shared);
        new RateLimiter(10, 1, RateLimitScope.Instance).Scope.Should().Be(RateLimitScope.Instance);
    }
}
