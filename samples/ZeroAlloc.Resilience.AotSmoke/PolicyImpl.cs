using System;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroAlloc.Resilience.AotSmoke;

public sealed class PolicyImpl : ITimeoutService, IRateLimitService, ICircuitService, IFallbackService
{
    public bool Hang { get; set; }
    public bool ShouldFail { get; set; }
    public int CallCount { get; private set; }
    public int FallbackCount { get; private set; }

    public async ValueTask<string> GetAsync(string id, CancellationToken ct)
    {
        CallCount++;
        if (ShouldFail)
            throw new InvalidOperationException("Simulated failure");
        if (Hang)
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        return $"ok:{id}";
    }

    public ValueTask<string> GetFallback(string id, CancellationToken ct)
    {
        FallbackCount++;
        return ValueTask.FromResult($"fallback:{id}");
    }
}
