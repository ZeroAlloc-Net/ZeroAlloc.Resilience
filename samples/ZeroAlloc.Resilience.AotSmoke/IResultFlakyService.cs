using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Results;

namespace ZeroAlloc.Resilience.AotSmoke;

public readonly record struct SmokeError(int Status);

// BackoffMs is 10 s: the smoke run finishes quickly only if the delay hint replaces the backoff.
[Retry(MaxAttempts = 3, BackoffMs = 10_000, RetryWhen = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
public interface IResultFlakyService
{
    ValueTask<Result<string, SmokeError>> GetAsync(string id, CancellationToken ct);
    Result<string, SmokeError> Get(string id);

    static bool IsTransient(SmokeError error) => error.Status == 429;
    static TimeSpan? RetryAfter(SmokeError error) => TimeSpan.FromMilliseconds(1);
    static TimeSpan? RetryAfter(Exception exception) => TimeSpan.FromMilliseconds(1);
}
