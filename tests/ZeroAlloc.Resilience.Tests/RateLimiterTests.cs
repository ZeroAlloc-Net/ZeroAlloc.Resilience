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
    public async Task AfterDelay_TokensRefill()
    {
        // 10/s, so one token per 100ms. NOT 100/s: that refills every 10ms, which is inside
        // the granularity of Environment.TickCount64 (~15ms on Windows) that RateLimiter reads.
        // A single clock tick then refilled a whole token, so the "bucket is now empty"
        // assertion below raced the clock and failed roughly two runs in five.
        var limiter = new RateLimiter(maxPerSecond: 10, burstSize: 1, scope: RateLimitScope.Shared);

        limiter.TryAcquire().Should().BeTrue();   // consumes the burst token
        limiter.TryAcquire().Should().BeFalse();  // empty — has 100ms of slack, not 10ms

        await Task.Delay(200); // >= 2 tokens at 10/s, so the wait cannot be marginal either
        limiter.TryAcquire().Should().BeTrue();
    }

    [Fact]
    public void Scope_Property_ReflectsConstructorArg()
    {
        new RateLimiter(10, 1, RateLimitScope.Shared).Scope.Should().Be(RateLimitScope.Shared);
        new RateLimiter(10, 1, RateLimitScope.Instance).Scope.Should().Be(RateLimitScope.Instance);
    }
}
