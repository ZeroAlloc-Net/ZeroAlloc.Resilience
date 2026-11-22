using ZeroAlloc.Resilience;
using ZeroAlloc.Resilience.Sdk10Smoke;

// IFlakyServiceResilienceProxy exists only if the compiler loaded the generator.
var inner = new FlakyImpl { FailTimes = 2 };
var retry = new RetryPolicy(maxAttempts: 3, backoffMs: 1, jitter: false, perAttemptTimeoutMs: 0);
var proxy = new IFlakyServiceResilienceProxy(inner, new FlakyServiceResiliencePolicies { Retry = retry });

var result = await proxy.GetAsync("x", CancellationToken.None);
if (result != "ok:x" || inner.CallCount != 3)
{
    Console.Error.WriteLine($"FAIL: got '{result}' after {inner.CallCount} calls, expected 'ok:x' after 3");
    return 1;
}

Console.WriteLine("SDK 10 consumer: generator loaded, failing calls retried through the generated proxy.");
return 0;

namespace ZeroAlloc.Resilience.Sdk10Smoke
{
    [Retry(MaxAttempts = 3, BackoffMs = 1)]
    public interface IFlakyService
    {
        ValueTask<string> GetAsync(string id, CancellationToken ct);
    }

    public sealed class FlakyImpl : IFlakyService
    {
        public int FailTimes { get; set; }

        public int CallCount { get; private set; }

        public ValueTask<string> GetAsync(string id, CancellationToken ct)
        {
            CallCount++;
            if (CallCount <= FailTimes)
                throw new InvalidOperationException($"Simulated failure #{CallCount}");
            return ValueTask.FromResult($"ok:{id}");
        }
    }
}
