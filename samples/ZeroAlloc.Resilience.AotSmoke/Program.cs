using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Resilience;
using ZeroAlloc.Resilience.AotSmoke;

// Exercise the generator-emitted IFlakyServiceResilienceProxy under
// PublishAot=true. The Retry policy should transparently swallow the first
// two simulated failures and return the third attempt's success.

var inner = new FlakyImpl { FailTimes = 2 };
var retry = new RetryPolicy(maxAttempts: 3, backoffMs: 1, jitter: false, perAttemptTimeoutMs: 0);
var proxy = new IFlakyServiceResilienceProxy(inner, new FlakyServiceResiliencePolicies { Retry = retry });

var result = await proxy.GetAsync("x", CancellationToken.None).ConfigureAwait(false);
if (!string.Equals(result, "ok:x", StringComparison.Ordinal))
    return Fail($"Retry proxy expected 'ok:x', got '{result}'");
if (inner.CallCount != 3)
    return Fail($"Retry expected 3 inner calls (2 failures + 1 success), got {inner.CallCount}");

// Second invocation on a fresh impl: success on first try should NOT retry.
var innerHappy = new FlakyImpl();
var proxyHappy = new IFlakyServiceResilienceProxy(innerHappy, new FlakyServiceResiliencePolicies { Retry = retry });
var happy = await proxyHappy.GetAsync("y", CancellationToken.None).ConfigureAwait(false);
if (!string.Equals(happy, "ok:y", StringComparison.Ordinal))
    return Fail($"Happy-path expected 'ok:y', got '{happy}'");
if (innerHappy.CallCount != 1)
    return Fail($"Happy-path expected 1 inner call, got {innerHappy.CallCount}");

// Result-aware retry: a transient failed Result is retried, with the wait taken from the failure.
var started = Stopwatch.GetTimestamp();

var transient = new ResultFlakyImpl { FailTimes = 1 };
var retried = await new IResultFlakyServiceResilienceProxy(transient, new ResultFlakyServiceResiliencePolicies())
    .GetAsync("r", CancellationToken.None).ConfigureAwait(false);
if (!retried.IsSuccess || transient.CallCount != 2)
    return Fail($"Result-aware retry expected success after 2 calls, got {retried.IsSuccess} after {transient.CallCount}");

var syncTransient = new ResultFlakyImpl { FailTimes = 1 };
var syncRetried = new IResultFlakyServiceResilienceProxy(syncTransient, new ResultFlakyServiceResiliencePolicies()).Get("s");
if (!syncRetried.IsSuccess || syncTransient.CallCount != 2)
    return Fail($"Sync Result-aware retry expected success after 2 calls, got {syncRetried.IsSuccess} after {syncTransient.CallCount}");

var permanent = new ResultFlakyImpl { FailTimes = 1, Status = 422 };
var notRetried = await new IResultFlakyServiceResilienceProxy(permanent, new ResultFlakyServiceResiliencePolicies())
    .GetAsync("p", CancellationToken.None).ConfigureAwait(false);
if (notRetried.IsSuccess || notRetried.Error.Status != 422 || permanent.CallCount != 1)
    return Fail($"A non-transient failure expected 1 call and status 422, got {permanent.CallCount} calls");

// A thrown exception is retried too, using the Exception overload of DelayHint under AOT.
var throwing = new ResultFlakyImpl { ThrowTimes = 1 };
var afterThrow = await new IResultFlakyServiceResilienceProxy(throwing, new ResultFlakyServiceResiliencePolicies())
    .GetAsync("t", CancellationToken.None).ConfigureAwait(false);
if (!afterThrow.IsSuccess || throwing.CallCount != 2)
    return Fail($"Exception-hint retry expected success after 2 calls, got {afterThrow.IsSuccess} after {throwing.CallCount}");

if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(5))
    return Fail("The delay hint was not used: the retries waited for the 10 s backoff");

Console.WriteLine("AOT smoke: PASS");
return 0;

static int Fail(string message)
{
    Console.Error.WriteLine($"AOT smoke: FAIL — {message}");
    return 1;
}
