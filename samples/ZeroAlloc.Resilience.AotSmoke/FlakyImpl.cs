using System;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroAlloc.Resilience.AotSmoke;

public sealed class FlakyImpl : IFlakyService
{
    private int _callCount;
    public int FailTimes { get; set; }
    public int CallCount => _callCount;

    public ValueTask<string> GetAsync(string id, CancellationToken ct)
    {
        _callCount++;
        if (_callCount <= FailTimes)
            throw new InvalidOperationException($"Simulated failure #{_callCount}");
        return ValueTask.FromResult($"ok:{id}");
    }
}
