using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Resilience;
using ZeroAlloc.Resilience.AotSmoke;

// Exercise the generator-emitted IFlakyServiceResilienceProxy under
// PublishAot=true. The Retry policy should transparently swallow the first
// two simulated failures and return the third attempt's success.

var inner = new FlakyImpl { FailTimes = 2 };
var retry = new RetryPolicy(maxAttempts: 3, backoffMs: 1, jitter: false, perAttemptTimeoutMs: 0);
var proxy = new IFlakyServiceResilienceProxy(inner, retry);

var result = await proxy.GetAsync("x", CancellationToken.None).ConfigureAwait(false);
if (!string.Equals(result, "ok:x", StringComparison.Ordinal))
    return Fail($"Retry proxy expected 'ok:x', got '{result}'");
if (inner.CallCount != 3)
    return Fail($"Retry expected 3 inner calls (2 failures + 1 success), got {inner.CallCount}");

// Second invocation on a fresh impl: success on first try should NOT retry.
var innerHappy = new FlakyImpl();
var proxyHappy = new IFlakyServiceResilienceProxy(innerHappy, retry);
var happy = await proxyHappy.GetAsync("y", CancellationToken.None).ConfigureAwait(false);
if (!string.Equals(happy, "ok:y", StringComparison.Ordinal))
    return Fail($"Happy-path expected 'ok:y', got '{happy}'");
if (innerHappy.CallCount != 1)
    return Fail($"Happy-path expected 1 inner call, got {innerHappy.CallCount}");

Console.WriteLine("AOT smoke: PASS");
return 0;

static int Fail(string message)
{
    Console.Error.WriteLine($"AOT smoke: FAIL — {message}");
    return 1;
}
