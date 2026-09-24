using System;

namespace ZeroAlloc.Resilience.Tests;

// Regression coverage for #159: an unattributed overload of a method that has a policy on one of
// its other overloads must still be forwarded to the inner implementation, not dropped.

public interface IJevOverloadApi
{
    [Retry(MaxAttempts = 3, BackoffMs = 1)]
    void Get();

    void Get(int id);
}

public sealed class FlakyJevOverloadImpl : IJevOverloadApi
{
    public int GetCallCount { get; private set; }
    public int GetByIdCallCount { get; private set; }
    public int LastId { get; private set; }
    public int FailTimes { get; init; }

    public void Get()
    {
        GetCallCount++;
        if (GetCallCount <= FailTimes) throw new InvalidOperationException($"boom #{GetCallCount}");
    }

    public void Get(int id)
    {
        GetByIdCallCount++;
        LastId = id;
    }
}

public class OverloadIntegrationTests
{
    [Fact]
    public void AttributedOverload_Retries()
    {
        var inner = new FlakyJevOverloadImpl { FailTimes = 2 };
        var proxy = new IJevOverloadApiResilienceProxy(inner, new JevOverloadApiResiliencePolicies { GetRetry = new RetryPolicy(3, 0, false, 0) });

        proxy.Get();

        inner.GetCallCount.Should().Be(3);
    }

    [Fact]
    public void UnattributedOverload_CalledOnceAndPassesArgumentThrough()
    {
        var inner = new FlakyJevOverloadImpl();
        var proxy = new IJevOverloadApiResilienceProxy(inner, new JevOverloadApiResiliencePolicies { GetRetry = new RetryPolicy(3, 0, false, 0) });

        proxy.Get(42);

        inner.GetByIdCallCount.Should().Be(1);
        inner.LastId.Should().Be(42);
    }
}
