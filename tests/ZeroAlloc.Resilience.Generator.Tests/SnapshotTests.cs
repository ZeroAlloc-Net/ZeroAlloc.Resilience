using System.Threading.Tasks;
using ZeroAlloc.Resilience.Generator;
using ZeroAlloc.Resilience.Generator.Tests;

public class SnapshotTests
{
    [Fact]
    public void Retry_Only_GeneratesProxy()
    {
        var source = """
            using ZeroAlloc.Resilience;
            using System.Threading;
            using System.Threading.Tasks;
            namespace T;
            [Retry(MaxAttempts = 3, BackoffMs = 100)]
            public interface IMyService
            {
                ValueTask<string> GetAsync(string id, CancellationToken ct);
            }
            """;
        TestHelper.Verify<ResilienceGenerator>(source);
    }

    [Fact]
    public void AllPolicies_ClassLevel_GeneratesProxy()
    {
        var source = """
            using ZeroAlloc.Resilience;
            using System.Threading;
            using System.Threading.Tasks;
            namespace T;
            [Retry(MaxAttempts = 3, BackoffMs = 200, Jitter = true, PerAttemptTimeoutMs = 1000)]
            [Timeout(Ms = 5000)]
            [RateLimit(MaxPerSecond = 100, BurstSize = 10)]
            [CircuitBreaker(MaxFailures = 5, ResetMs = 1000, HalfOpenProbes = 1)]
            public interface IExternalService
            {
                ValueTask<string> FetchAsync(string id, CancellationToken ct);
                ValueTask<string> FetchFallback(string id, CancellationToken ct);
            }
            """;
        TestHelper.Verify<ResilienceGenerator>(source);
    }

    [Fact]
    public void MethodLevel_Override_GeneratesProxy()
    {
        var source = """
            using ZeroAlloc.Resilience;
            using System.Threading;
            using System.Threading.Tasks;
            namespace T;
            [Retry(MaxAttempts = 3, BackoffMs = 200)]
            [Timeout(Ms = 5000)]
            public interface IMyService
            {
                ValueTask<string> GetAsync(string id, CancellationToken ct);
                [Retry(MaxAttempts = 1)]
                [Timeout(Ms = 500)]
                ValueTask PostAsync(string data, CancellationToken ct);
            }
            """;
        TestHelper.Verify<ResilienceGenerator>(source);
    }

    [Fact]
    public void CircuitBreaker_WithFallback_GeneratesProxy()
    {
        var source = """
            using ZeroAlloc.Resilience;
            using System.Threading;
            using System.Threading.Tasks;
            namespace T;
            [CircuitBreaker(MaxFailures = 3, ResetMs = 500, Fallback = nameof(FetchFallback))]
            public interface IMyService
            {
                ValueTask<string> FetchAsync(string id, CancellationToken ct);
                ValueTask<string> FetchFallback(string id, CancellationToken ct);
            }
            """;
        TestHelper.Verify<ResilienceGenerator>(source);
    }

    [Fact]
    public void Retry_Sync_WithCancellationToken_GeneratesProxy()
    {
        var source = """
            using ZeroAlloc.Resilience;
            using System.Threading;
            namespace T;
            [Retry(MaxAttempts = 3, BackoffMs = 100)]
            public interface IMyService
            {
                string Get(string id, CancellationToken ct);
                string GetWithoutToken(string id);
            }
            """;
        TestHelper.Verify<ResilienceGenerator>(source);
    }

    [Fact]
    public void Retry_Sync_WithTimeout_GeneratesProxy()
    {
        var source = """
            using ZeroAlloc.Resilience;
            using System.Threading;
            namespace T;
            [Retry(MaxAttempts = 3, BackoffMs = 100)]
            [Timeout(Ms = 5000)]
            public interface IMyService
            {
                string Get(string id, CancellationToken ct);
            }
            """;
        TestHelper.Verify<ResilienceGenerator>(source);
    }

    [Fact]
    public void ResultAwareRetry_Async_DelayHintOverloads_GeneratesProxy()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            using ZeroAlloc.Results;
            namespace T;
            public sealed class HttpError { public int Status { get; init; } }
            [Retry(MaxAttempts = 3, BackoffMs = 100, RetryWhen = nameof(IsTransient),
                   RetryOnException = nameof(IsTransientException), DelayHint = nameof(RetryAfter))]
            public interface IApi
            {
                ValueTask<Result<string, HttpError>> GetAsync(string id, CancellationToken ct);
                static bool IsTransient(HttpError error) => error.Status is 429 or >= 500;
                static bool IsTransientException(Exception exception) => exception is not ArgumentException;
                static TimeSpan? RetryAfter(HttpError error) => null;
                static TimeSpan? RetryAfter(Exception exception) => null;
            }
            """;
        TestHelper.Verify<ResilienceGenerator>(source);
    }

    [Fact]
    public void ResultAwareRetry_Async_TimeoutAndCircuitBreaker_GeneratesProxy()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            using ZeroAlloc.Results;
            namespace T;
            [Retry(MaxAttempts = 3, BackoffMs = 100, RetryWhen = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
            [Timeout(Ms = 5000)]
            [CircuitBreaker(MaxFailures = 5, ResetMs = 1000)]
            public interface IApi
            {
                Task<Result<string>> GetAsync(string id, CancellationToken ct);
                static bool IsTransient(string error) => error.StartsWith("429", StringComparison.Ordinal);
                static TimeSpan? RetryAfter(string error) => null;
            }
            """;
        TestHelper.Verify<ResilienceGenerator>(source);
    }

    [Fact]
    public void ResultAwareRetry_Sync_UnitResult_GeneratesProxy()
    {
        var source = """
            using System;
            using System.Threading;
            using ZeroAlloc.Resilience;
            using ZeroAlloc.Results;
            namespace T;
            public sealed class HttpError { public int Status { get; init; } }
            [Retry(MaxAttempts = 3, BackoffMs = 100, RetryWhen = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
            [CircuitBreaker(MaxFailures = 5, ResetMs = 1000)]
            public interface IApi
            {
                [Timeout(Ms = 5000)]
                UnitResult<HttpError> Send(CancellationToken ct);
                Result<int, HttpError> Count();
                static bool IsTransient(HttpError error) => error.Status == 429;
                static TimeSpan? RetryAfter(HttpError error) => null;
                static TimeSpan? RetryAfter(Exception exception) => null;
            }
            """;
        TestHelper.Verify<ResilienceGenerator>(source);
    }

    [Fact]
    public void RetryOnException_ExceptionHint_NonResult_GeneratesProxy()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace T;
            [Retry(MaxAttempts = 3, BackoffMs = 100, RetryOnException = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
            public interface IApi
            {
                ValueTask<string> GetAsync(string id, CancellationToken ct);
                string Get(string id);
                static bool IsTransient(Exception exception) => exception is not ArgumentException;
                static TimeSpan? RetryAfter(Exception exception) => null;
            }
            """;
        TestHelper.Verify<ResilienceGenerator>(source);
    }
}
