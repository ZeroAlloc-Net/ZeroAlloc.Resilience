using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Results;

namespace ZeroAlloc.Resilience.Tests;

// A [Timeout] that fires while an attempt runs ends the call as the Timeout policy, not as an
// exhausted Retry, even though the attempt's own failure is an OperationCanceledException. The
// caller can then tell the two apart from PolicyType alone.

[Retry(MaxAttempts = 3, BackoffMs = 1)]
[Timeout(Ms = 100)]
public interface IHangingAttemptApi
{
    ValueTask<Result<string, ResilienceError>> RunAsync(CancellationToken ct);
}

public sealed class HangingAttemptApi : IHangingAttemptApi
{
    public async ValueTask<Result<string, ResilienceError>> RunAsync(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return Result<string, ResilienceError>.Success("unreachable");
    }
}

public class TotalTimeoutDuringAttemptIntegrationTests
{
    [Fact(Timeout = 10_000)]
    public async Task TypedResult_TimeoutDuringAttempt_ReportsTimeout()
    {
        var proxy = new IHangingAttemptApiResilienceProxy(new HangingAttemptApi(), new HangingAttemptApiResiliencePolicies());

        var result = await proxy.RunAsync(CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.PolicyType.Should().Be(nameof(ResiliencePolicy.Timeout));
        result.Error.Reason.Should().Be("Total timeout elapsed.");
        result.Error.InnerException.Should().BeAssignableTo<OperationCanceledException>();
    }
}
