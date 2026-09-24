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

    [Fact]
    public void GetBackoffMs_SmallAttempts_Unchanged()
    {
        var policy = new RetryPolicy(3, 200, false, 0);
        policy.GetBackoffMs(0).Should().Be(200);  // 200 * 2^0
        policy.GetBackoffMs(1).Should().Be(400);  // 200 * 2^1
        policy.GetBackoffMs(2).Should().Be(800);  // 200 * 2^2
    }

    [Theory]
    [InlineData(24)]
    [InlineData(30)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(40)]
    [InlineData(1000)]
    public void GetBackoffMs_LargeAttempts_NeverOverflows(int attempt)
    {
        var policy = new RetryPolicy(1001, 200, false, 0);
        var result = policy.GetBackoffMs(attempt);

        result.Should().BeGreaterThanOrEqualTo(0);
        result.Should().BeLessThanOrEqualTo(RetryPolicy.MaxBackoffMs);
        result.Should().NotBe(-1);

        // For attempt >= 40, should equal MaxBackoffMs
        if (attempt >= 40)
            result.Should().Be(RetryPolicy.MaxBackoffMs);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(30)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(40)]
    [InlineData(1000)]
    public void GetBackoffMs_LargeAttemptsWithJitter_NeverOverflows(int attempt)
    {
        var policy = new RetryPolicy(1001, 200, true, 0);
        var result = policy.GetBackoffMs(attempt);

        result.Should().BeGreaterThanOrEqualTo(0);
        result.Should().BeLessThanOrEqualTo(RetryPolicy.MaxBackoffMs);
        result.Should().NotBe(-1);
    }

    [Fact]
    public void GetBackoffMs_ZeroBackoffMs_ReturnsZero()
    {
        var policy = new RetryPolicy(1001, 0, false, 0);
        policy.GetBackoffMs(1000).Should().Be(0);
    }
}
