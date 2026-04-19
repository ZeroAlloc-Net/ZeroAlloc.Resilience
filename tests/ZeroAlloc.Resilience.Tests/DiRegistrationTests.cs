#pragma warning disable ZR0002

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Resilience;

namespace ZeroAlloc.Resilience.Tests;

// Interface for DI test — needs to be at namespace level for generator
[Retry(MaxAttempts = 2, BackoffMs = 1)]
public interface IDiTestService
{
    ValueTask<string> PingAsync(CancellationToken ct);
}

public sealed class DiTestImpl : IDiTestService
{
    public ValueTask<string> PingAsync(CancellationToken ct) => ValueTask.FromResult("pong");
}

public class DiRegistrationTests
{
    [Fact]
    public async Task AddResilience_ResolvesProxyAndCallsInner()
    {
        var services = new ServiceCollection();
        services.AddDiTestServiceResilience<DiTestImpl>();

        await using var sp = services.BuildServiceProvider();
        var svc = sp.GetRequiredService<IDiTestService>();

        svc.Should().NotBeOfType<DiTestImpl>("proxy should wrap the impl");
        var result = await svc.PingAsync(CancellationToken.None);
        result.Should().Be("pong");
    }
}
