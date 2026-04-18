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
        var limiter = new RateLimiter(maxPerSecond: 100, burstSize: 1, scope: RateLimitScope.Shared);
        limiter.TryAcquire().Should().BeTrue();
        limiter.TryAcquire().Should().BeFalse();

        await Task.Delay(50); // ~5 tokens at 100/s
        limiter.TryAcquire().Should().BeTrue();
    }

    [Fact]
    public void Scope_Property_ReflectsConstructorArg()
    {
        new RateLimiter(10, 1, RateLimitScope.Shared).Scope.Should().Be(RateLimitScope.Shared);
        new RateLimiter(10, 1, RateLimitScope.Instance).Scope.Should().Be(RateLimitScope.Instance);
    }
}
