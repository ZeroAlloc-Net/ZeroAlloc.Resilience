#pragma warning disable ZR0002

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace ZeroAlloc.Resilience.Tests;

// Two interfaces named ICollidingService nested in different types: their generated names are
// qualified with the containing type, as in CollisionFirst_CollidingServiceResiliencePolicies (#209).
public static class CollisionFirst
{
    [Retry(MaxAttempts = 2, BackoffMs = 1)]
    public interface ICollidingService
    {
        ValueTask<string> GoAsync(CancellationToken ct);
    }
}

public static class CollisionSecond
{
    [Retry(MaxAttempts = 3, BackoffMs = 1)]
    public interface ICollidingService
    {
        ValueTask<string> GoAsync(CancellationToken ct);
    }
}

public sealed class CollisionCounter
{
    public int First { get; set; }
    public int Second { get; set; }
}

public sealed class CollisionFirstImpl(CollisionCounter counter) : CollisionFirst.ICollidingService
{
    public ValueTask<string> GoAsync(CancellationToken ct)
    {
        counter.First++;
        throw new InvalidOperationException("first");
    }
}

public sealed class CollisionSecondImpl(CollisionCounter counter) : CollisionSecond.ICollidingService
{
    public ValueTask<string> GoAsync(CancellationToken ct)
    {
        counter.Second++;
        throw new InvalidOperationException("second");
    }
}

public class NestedInterfaceNameCollisionTests
{
    [Fact]
    public async Task SameNamedNestedInterfaces_EachResolveTheirOwnPolicies()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new CollisionCounter());
        services.AddCollisionFirst_CollidingServiceResilience<CollisionFirstImpl>();
        services.AddCollisionSecond_CollidingServiceResilience<CollisionSecondImpl>();
        await using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<CollisionFirst_CollidingServiceResiliencePolicies>().Retry!.MaxAttempts.Should().Be(2);
        sp.GetRequiredService<CollisionSecond_CollidingServiceResiliencePolicies>().Retry!.MaxAttempts.Should().Be(3);

        var first = async () => await sp.GetRequiredService<CollisionFirst.ICollidingService>().GoAsync(CancellationToken.None);
        var second = async () => await sp.GetRequiredService<CollisionSecond.ICollidingService>().GoAsync(CancellationToken.None);
        await first.Should().ThrowAsync<ResilienceException>();
        await second.Should().ThrowAsync<ResilienceException>();

        var counter = sp.GetRequiredService<CollisionCounter>();
        counter.First.Should().Be(2);
        counter.Second.Should().Be(3);
    }
}
