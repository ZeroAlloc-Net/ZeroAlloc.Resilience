using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Results;

namespace ZeroAlloc.Resilience.AotSmoke;

public sealed class ResultFlakyImpl : IResultFlakyService
{
    public int CallCount { get; private set; }
    public int ThrowTimes { get; init; }
    public int FailTimes { get; init; }
    public int Status { get; init; } = 429;

    // Throws for the first ThrowTimes calls, exercising the Exception overload of DelayHint, then
    // falls back to the Result-based FailTimes behaviour for the calls after that.
    private Result<string, SmokeError> Next(string id)
    {
        ++CallCount;
        if (CallCount <= ThrowTimes)
            throw new InvalidOperationException($"Simulated exception #{CallCount}");
        return CallCount <= ThrowTimes + FailTimes
            ? Result<string, SmokeError>.Failure(new SmokeError(Status))
            : Result<string, SmokeError>.Success($"ok:{id}");
    }

    public ValueTask<Result<string, SmokeError>> GetAsync(string id, CancellationToken ct) => ValueTask.FromResult(Next(id));

    public Result<string, SmokeError> Get(string id) => Next(id);
}
