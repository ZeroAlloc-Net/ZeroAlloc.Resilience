using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Resilience;

namespace ZeroAlloc.Resilience.AotSmoke;

[Retry(MaxAttempts = 3, BackoffMs = 1)]
public interface IFlakyService
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);
}
