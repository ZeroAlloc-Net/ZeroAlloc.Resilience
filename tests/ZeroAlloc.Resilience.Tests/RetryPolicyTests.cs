using ZeroAlloc.Resilience;

namespace ZeroAlloc.Resilience.Tests;

public class RetryPolicyTests
{
    [Fact]
    public void GetBackoffMs_ExponentialWithoutJitter()
    {
        var policy = new RetryPolicy(maxAttempts: 3, backoffMs: 100, jitter: false, perAttemptTimeoutMs: 0);
        policy.GetBackoffMs(0).Should().Be(100);  // 100 * 2^0
        policy.GetBackoffMs(1).Should().Be(200);  // 100 * 2^1
        policy.GetBackoffMs(2).Should().Be(400);  // 100 * 2^2
    }

    [Fact]
    public void GetBackoffMs_WithJitter_IsWithinRange()
    {
        var policy = new RetryPolicy(maxAttempts: 3, backoffMs: 100, jitter: true, perAttemptTimeoutMs: 0);
        var backoff = policy.GetBackoffMs(0); // base=100, jitter up to +50
        backoff.Should().BeGreaterThanOrEqualTo(100).And.BeLessThanOrEqualTo(150);
    }

    [Fact]
    public void Properties_MatchConstructorArgs()
    {
        var policy = new RetryPolicy(5, 300, true, 1000);
        policy.MaxAttempts.Should().Be(5);
        policy.BackoffMs.Should().Be(300);
        policy.Jitter.Should().BeTrue();
        policy.PerAttemptTimeoutMs.Should().Be(1000);
    }
}
