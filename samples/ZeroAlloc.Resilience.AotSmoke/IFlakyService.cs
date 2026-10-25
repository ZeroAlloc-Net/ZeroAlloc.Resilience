using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Resilience;

namespace ZeroAlloc.Resilience.AotSmoke;

[Retry(MaxAttempts = 3, BackoffMs = 1)]
public interface IFlakyService
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);

    // Retried until its first item: the proxy is an async iterator driving the inner enumerator.
    IAsyncEnumerable<int> StreamAsync(int count, CancellationToken ct);
}
