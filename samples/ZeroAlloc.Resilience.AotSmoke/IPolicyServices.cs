using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Resilience;

namespace ZeroAlloc.Resilience.AotSmoke;

// One interface per policy, so each generated proxy shape is compiled and run under NativeAOT.

[Timeout(Ms = 50)]
public interface ITimeoutService
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);
}

[RateLimit(MaxPerSecond = 1, BurstSize = 1)]
public interface IRateLimitService
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);
}

[CircuitBreaker(MaxFailures = 1, ResetMs = 60_000, HalfOpenProbes = 1)]
public interface ICircuitService
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);
}

[CircuitBreaker(MaxFailures = 1, ResetMs = 60_000, HalfOpenProbes = 1, Fallback = nameof(GetFallback))]
public interface IFallbackService
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);
    ValueTask<string> GetFallback(string id, CancellationToken ct);
}
