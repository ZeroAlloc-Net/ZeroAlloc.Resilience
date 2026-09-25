using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Resilience.Generator.Tests;

public class DiagnosticTests
{
    [Fact]
    public async Task FallbackNotFound_ZR0001_Reported()
    {
        var source = """
            using ZeroAlloc.Resilience;
            using System.Threading;
            using System.Threading.Tasks;
            namespace T;
            [CircuitBreaker(MaxFailures = 3, ResetMs = 500, Fallback = "NonExistent")]
            public interface IMyService
            {
                ValueTask<string> FetchAsync(string id, CancellationToken ct);
            }
            """;
        var diags = await TestHelper.GetDiagnostics<ResilienceGenerator>(source);
        diags.Should().Contain(d => d.Id == "ZR0001" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task FallbackSignatureMismatch_ZR0001_Reported()
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
                ValueTask<int> FetchFallback(string id, CancellationToken ct);
            }
            """;
        var diags = await TestHelper.GetDiagnostics<ResilienceGenerator>(source);
        diags.Should().Contain(d => d.Id == "ZR0001" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task TimeoutWithoutCancellationToken_ZR0002_Reported()
    {
        var source = """
            using ZeroAlloc.Resilience;
            using System.Threading.Tasks;
            namespace T;
            [Timeout(Ms = 5000)]
            public interface IMyService
            {
                ValueTask<string> GetAsync(string id);
            }
            """;
        var diags = await TestHelper.GetDiagnostics<ResilienceGenerator>(source);
        diags.Should().Contain(d => d.Id == "ZR0002" && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task ValidInterface_NoDiagnostics()
    {
        var source = """
            using ZeroAlloc.Resilience;
            using System.Threading;
            using System.Threading.Tasks;
            namespace T;
            [Retry(MaxAttempts = 3)]
            public interface IMyService
            {
                ValueTask<string> GetAsync(string id, CancellationToken ct);
            }
            """;
        var diags = await TestHelper.GetDiagnostics<ResilienceGenerator>(source);
        diags.Should().NotContain(d => d.Id.StartsWith("ZR"));
    }

    // #152: an invalid ZeroAllocGeneratedAccessibility value is a build error, not a silently
    // ignored setting, and never depends on there being a candidate interface in the compilation.
    [Fact]
    public async Task InvalidGeneratedAccessibilityValue_ZR0008_Reported()
    {
        var source = """
            namespace T;
            public class NothingToGenerateFor;
            """;
        var diags = await TestHelper.GetDiagnostics<ResilienceGenerator>(source, generatedAccessibility: "Priv4te");
        diags.Should().Contain(d => d.Id == "ZR0008" && d.Severity == DiagnosticSeverity.Error
            && d.GetMessage().Contains("Priv4te"));
    }

    [Fact]
    public async Task ValidGeneratedAccessibilityValue_NoZR0008()
    {
        var source = """
            using ZeroAlloc.Resilience;
            using System.Threading;
            using System.Threading.Tasks;
            namespace T;
            [Retry(MaxAttempts = 3)]
            public interface IMyService
            {
                ValueTask<string> GetAsync(string id, CancellationToken ct);
            }
            """;
        var diags = await TestHelper.GetDiagnostics<ResilienceGenerator>(source, generatedAccessibility: "Internal");
        diags.Should().NotContain(d => d.Id == "ZR0008");
    }
}
