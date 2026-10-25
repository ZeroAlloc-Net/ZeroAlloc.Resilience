using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroAlloc.Resilience.AotSmoke;

public sealed class FlakyImpl : IFlakyService
{
    private int _callCount;
    private int _streamCallCount;
    public int FailTimes { get; set; }
    public int CallCount => _callCount;

    // The first StreamFailTimes streams throw before their first item.
    public int StreamFailTimes { get; set; }
    public int StreamCallCount => _streamCallCount;

    public ValueTask<string> GetAsync(string id, CancellationToken ct)
    {
        _callCount++;
        if (_callCount <= FailTimes)
            throw new InvalidOperationException($"Simulated failure #{_callCount}");
        return ValueTask.FromResult($"ok:{id}");
    }

    public async IAsyncEnumerable<int> StreamAsync(int count, [EnumeratorCancellation] CancellationToken ct)
    {
        _streamCallCount++;
        if (_streamCallCount <= StreamFailTimes)
            throw new InvalidOperationException($"Simulated stream failure #{_streamCallCount}");
        for (var i = 1; i <= count; i++)
        {
            await Task.Yield();
            yield return i;
        }
    }
}
