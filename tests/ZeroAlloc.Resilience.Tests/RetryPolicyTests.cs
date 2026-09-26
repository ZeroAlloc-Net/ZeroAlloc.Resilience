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

    [Fact]
    public void FourArgumentConstructor_HasNoDelayCap() =>
        new RetryPolicy(3, 100, false, 0).MaxDelayMs.Should().Be(RetryPolicy.MaxBackoffMs);

    [Fact]
    public void FiveArgumentConstructor_SetsMaxDelayMs() =>
        new RetryPolicy(3, 100, false, 0, maxDelayMs: 250).MaxDelayMs.Should().Be(250);

    [Fact]
    public void GetDelayMs_WithoutHint_IsTheBackoff() =>
        new RetryPolicy(3, 100, false, 0).GetDelayMs(2, null).Should().Be(400);

    [Fact]
    public void GetDelayMs_Hint_OverridesTheBackoff() =>
        new RetryPolicy(3, 100, false, 0).GetDelayMs(0, TimeSpan.FromSeconds(2)).Should().Be(2_000);

    [Fact]
    public void GetDelayMs_Hint_GetsNoJitter()
    {
        var policy = new RetryPolicy(3, 100, jitter: true, 0);
        for (var i = 0; i < 100; i++)
            policy.GetDelayMs(0, TimeSpan.FromMilliseconds(300)).Should().Be(300);
    }

    [Fact]
    public void GetDelayMs_Hint_IsCappedByMaxDelayMs() =>
        new RetryPolicy(3, 100, false, 0, maxDelayMs: 1_000)
            .GetDelayMs(0, TimeSpan.FromMinutes(5)).Should().Be(1_000);

    [Fact]
    public void GetDelayMs_Backoff_IsCappedByMaxDelayMs() =>
        new RetryPolicy(10, 100, false, 0, maxDelayMs: 1_000).GetDelayMs(5, null).Should().Be(1_000);

    [Fact]
    public void GetBackoffMs_IsCappedByMaxDelayMs()
    {
        var policy = new RetryPolicy(10, 100, jitter: true, 0, maxDelayMs: 1_000);
        for (var i = 0; i < 100; i++)
            policy.GetBackoffMs(5).Should().Be(1_000); // 3200 plus jitter, capped
    }

    [Theory]
    [InlineData(-5_000)]
    [InlineData(0)]
    public void GetDelayMs_NegativeOrZeroHint_IsZero(int hintMs) =>
        new RetryPolicy(3, 100, false, 0).GetDelayMs(0, TimeSpan.FromMilliseconds(hintMs)).Should().Be(0);

    [Fact]
    public void GetDelayMs_FractionalHint_RoundsUp() =>
        new RetryPolicy(3, 100, false, 0).GetDelayMs(0, TimeSpan.FromTicks(10_001)).Should().Be(2);

    [Fact]
    public void GetDelayMs_HugeHint_NeverOverflows() =>
        new RetryPolicy(3, 100, false, 0, maxDelayMs: int.MaxValue)
            .GetDelayMs(0, TimeSpan.MaxValue).Should().Be(RetryPolicy.MaxBackoffMs);

    [Fact]
    public void RetryAttribute_NewPropertiesDefaultToOff()
    {
        var attribute = new RetryAttribute();
        attribute.RetryWhen.Should().BeNull();
        attribute.RetryOnException.Should().BeNull();
        attribute.DelayHint.Should().BeNull();
        attribute.MaxDelayMs.Should().Be(RetryPolicy.MaxBackoffMs);
    }
}
