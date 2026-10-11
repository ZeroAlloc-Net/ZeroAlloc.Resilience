using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Resilience;
using ZeroAlloc.Resilience.AotSmoke;

// Exercise the generator-emitted proxies under PublishAot=true: Retry, Result-aware retry,
// Timeout, RateLimit, CircuitBreaker and the CircuitBreaker fallback. The Retry policy should
// transparently swallow the first two simulated failures and return the third attempt's success.

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

// Timeout: a fast call passes through; a hung call is cancelled by the total timeout and the
// cancellation reaches the caller.
var timeoutImpl = new PolicyImpl();
var timeoutProxy = new ITimeoutServiceResilienceProxy(timeoutImpl, new TimeoutServiceResiliencePolicies());
var inTime = await timeoutProxy.GetAsync("t", CancellationToken.None).ConfigureAwait(false);
if (!string.Equals(inTime, "ok:t", StringComparison.Ordinal))
    return Fail($"Timeout happy path expected 'ok:t', got '{inTime}'");
timeoutImpl.Hang = true;
try
{
    _ = await timeoutProxy.GetAsync("h", CancellationToken.None).ConfigureAwait(false);
    return Fail("Timeout expected the hung call to be cancelled");
}
catch (OperationCanceledException) { /* expected: the timeout fired */ }

// RateLimit: the one-token burst admits the first call and rejects the second.
var rateImpl = new PolicyImpl();
var rateProxy = new IRateLimitServiceResilienceProxy(rateImpl, new RateLimitServiceResiliencePolicies());
var admitted = await rateProxy.GetAsync("r", CancellationToken.None).ConfigureAwait(false);
if (!string.Equals(admitted, "ok:r", StringComparison.Ordinal))
    return Fail($"RateLimit expected the first call to pass, got '{admitted}'");
try
{
    _ = await rateProxy.GetAsync("r", CancellationToken.None).ConfigureAwait(false);
    return Fail("RateLimit expected the second call to be rejected");
}
catch (ResilienceException ex) when (ex.Policy == ResiliencePolicy.RateLimit) { /* expected */ }
if (rateImpl.CallCount != 1)
    return Fail($"RateLimit expected 1 inner call, got {rateImpl.CallCount}");

// CircuitBreaker: one failure opens the circuit, after which calls are rejected without
// reaching the inner implementation.
var circuitImpl = new PolicyImpl();
var circuitProxy = new ICircuitServiceResilienceProxy(circuitImpl, new CircuitServiceResiliencePolicies());
var closed = await circuitProxy.GetAsync("c", CancellationToken.None).ConfigureAwait(false);
if (!string.Equals(closed, "ok:c", StringComparison.Ordinal))
    return Fail($"CircuitBreaker closed expected 'ok:c', got '{closed}'");
circuitImpl.ShouldFail = true;
try
{
    _ = await circuitProxy.GetAsync("c", CancellationToken.None).ConfigureAwait(false);
    return Fail("CircuitBreaker expected the inner failure to propagate");
}
catch (InvalidOperationException) { /* expected: the failure that opens the circuit */ }
try
{
    _ = await circuitProxy.GetAsync("c", CancellationToken.None).ConfigureAwait(false);
    return Fail("CircuitBreaker expected the open circuit to reject the call");
}
catch (ResilienceException ex) when (ex.Policy == ResiliencePolicy.CircuitBreaker) { /* expected */ }
if (circuitImpl.CallCount != 2)
    return Fail($"CircuitBreaker expected 2 inner calls, got {circuitImpl.CallCount}");

// Fallback: once the circuit is open, the proxy calls the fallback method instead of throwing.
var fallbackImpl = new PolicyImpl { ShouldFail = true };
var fallbackProxy = new IFallbackServiceResilienceProxy(fallbackImpl, new FallbackServiceResiliencePolicies());
try
{
    _ = await fallbackProxy.GetAsync("f", CancellationToken.None).ConfigureAwait(false);
    return Fail("Fallback expected the inner failure that opens the circuit to propagate");
}
catch (InvalidOperationException) { /* expected: the failure that opens the circuit */ }
var fellBack = await fallbackProxy.GetAsync("f", CancellationToken.None).ConfigureAwait(false);
if (!string.Equals(fellBack, "fallback:f", StringComparison.Ordinal) || fallbackImpl.FallbackCount != 1)
    return Fail($"Fallback expected 'fallback:f' from 1 fallback call, got '{fellBack}' from {fallbackImpl.FallbackCount}");

Console.WriteLine("AOT smoke: PASS");
return 0;

static int Fail(string message)
{
    Console.Error.WriteLine($"AOT smoke: FAIL — {message}");
    return 1;
}
