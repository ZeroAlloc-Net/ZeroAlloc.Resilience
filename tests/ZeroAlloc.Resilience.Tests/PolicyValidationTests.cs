using System;
using Microsoft.Extensions.Time.Testing;

namespace ZeroAlloc.Resilience.Tests;

// 2.0: policy constructors reject values that produced broken proxies, such as a retry loop
// that never calls the inner service or a timeout that cancels immediately.
public class PolicyValidationTests
{
    [Theory]
    [InlineData(0, 0, 0, "maxAttempts")]
    [InlineData(1, -1, 0, "backoffMs")]
    [InlineData(1, 0, -1, "perAttemptTimeoutMs")]
    public void RetryPolicy_RejectsOutOfRangeArguments(int maxAttempts, int backoffMs, int perAttemptTimeoutMs, string parameter)
    {
        var act = () => new RetryPolicy(maxAttempts, backoffMs, jitter: false, perAttemptTimeoutMs);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName(parameter);
    }

    [Fact]
    public void RetryPolicy_AcceptsMinimums() =>
        new RetryPolicy(1, 0, jitter: false, 0).MaxAttempts.Should().Be(1);

    [Theory]
    [InlineData(0, 0, 0, 0, "maxAttempts")]
    [InlineData(1, -1, 0, 0, "backoffMs")]
    [InlineData(1, 0, -1, 0, "perAttemptTimeoutMs")]
    [InlineData(1, 0, 0, -1, "maxDelayMs")]
    public void RetryPolicy_FiveArguments_RejectsOutOfRangeArguments(
        int maxAttempts, int backoffMs, int perAttemptTimeoutMs, int maxDelayMs, string parameter)
    {
        var act = () => new RetryPolicy(maxAttempts, backoffMs, jitter: false, perAttemptTimeoutMs, maxDelayMs);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName(parameter);
    }

    [Fact]
    public void RetryPolicy_AcceptsZeroMaxDelayMs() =>
        new RetryPolicy(1, 0, jitter: false, 0, maxDelayMs: 0).MaxDelayMs.Should().Be(0);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TimeoutPolicy_RejectsNonPositiveTotal(int totalMs)
    {
        var act = () => new TimeoutPolicy(totalMs);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("totalMs");
    }

    [Fact]
    public void TimeoutPolicy_AcceptsOne() => new TimeoutPolicy(1).TotalMs.Should().Be(1);

    [Theory]
    [InlineData(0, 0, 1, "maxFailures")]
    [InlineData(1, -1, 1, "resetMs")]
    [InlineData(1, 0, 0, "halfOpenProbes")]
    public void CircuitBreakerPolicy_RejectsOutOfRangeArguments(int maxFailures, int resetMs, int halfOpenProbes, string parameter)
    {
        var act = () => new CircuitBreakerPolicy(maxFailures, resetMs, halfOpenProbes).Dispose();

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName(parameter);
    }

    [Fact]
    public void CircuitBreakerPolicy_AcceptsMinimums()
    {
        using var policy = new CircuitBreakerPolicy(1, 0, 1);

        policy.CanExecute().Should().BeTrue();
    }

    [Theory]
    [InlineData(-1, 1, "maxPerSecond")]
    [InlineData(1, -1, "burstSize")]
    public void RateLimiter_RejectsNegativeArguments(int maxPerSecond, int burstSize, string parameter)
    {
        var act = () => new RateLimiter(maxPerSecond, burstSize, RateLimitScope.Shared);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName(parameter);
    }

    [Fact]
    public void RateLimiter_AcceptsZeroRateAndZeroBurst()
    {
        // burstSize 0 is a limiter that rejects every call; the benchmarks rely on it.
        var limiter = new RateLimiter(0, 0, RateLimitScope.Shared);

        limiter.TryAcquire().Should().BeFalse();
    }

    [Fact]
    public void ForProxyInstance_Shared_ReturnsSameLimiter()
    {
        var limiter = new RateLimiter(0, 1, RateLimitScope.Shared);

        limiter.ForProxyInstance().Should().BeSameAs(limiter);
    }

    [Fact]
    public void ForProxyInstance_Instance_ReturnsLimiterWithItsOwnBudget()
    {
        // maxPerSecond 0 never refills, so the budgets are deterministic.
        var template = new RateLimiter(0, 1, RateLimitScope.Instance);
        template.TryAcquire().Should().BeTrue();
        template.TryAcquire().Should().BeFalse();

        var own = template.ForProxyInstance();

        own.Should().NotBeSameAs(template);
        own.Scope.Should().Be(RateLimitScope.Instance);
        own.TryAcquire().Should().BeTrue();
        own.TryAcquire().Should().BeFalse();
    }

    [Fact]
    public void ForProxyInstance_Instance_KeepsTheTimeProvider()
    {
        var time = new FakeTimeProvider();
        var template = new RateLimiter(1, 1, RateLimitScope.Instance, time);

        var own = template.ForProxyInstance();
        own.TryAcquire().Should().BeTrue();
        own.TryAcquire().Should().BeFalse();

        time.Advance(TimeSpan.FromSeconds(1));

        own.TryAcquire().Should().BeTrue();
    }
}
