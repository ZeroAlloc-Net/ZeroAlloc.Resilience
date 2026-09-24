using System.Globalization;

namespace ZeroAlloc.Resilience.Generator.Tests;

// Issue #161: the runtime policy constructors throw ArgumentOutOfRangeException for invalid
// values, so an attribute that would produce one should fail the build with ZR0004 instead of
// compiling into a proxy that throws the first time it is constructed.
public class InvalidAttributeValueTests
{
    [Theory]
    [InlineData("[Retry(MaxAttempts = 0)]", "MaxAttempts")]
    [InlineData("[Retry(BackoffMs = -1)]", "BackoffMs")]
    [InlineData("[Retry(PerAttemptTimeoutMs = -1)]", "PerAttemptTimeoutMs")]
    [InlineData("[Timeout(Ms = 0)]", "Ms")]
    [InlineData("[CircuitBreaker(MaxFailures = 0)]", "MaxFailures")]
    [InlineData("[CircuitBreaker(ResetMs = -1)]", "ResetMs")]
    [InlineData("[CircuitBreaker(HalfOpenProbes = 0)]", "HalfOpenProbes")]
    [InlineData("[RateLimit(MaxPerSecond = -1)]", "MaxPerSecond")]
    [InlineData("[RateLimit(MaxPerSecond = 0, BurstSize = -1)]", "BurstSize")]
    public void InvalidValue_ReportsExactlyOneZR0004WithPropertyName(string attribute, string property)
    {
        var source = $$"""
            using ZeroAlloc.Resilience;
            using System.Threading;
            using System.Threading.Tasks;
            namespace T;
            {{attribute}}
            public interface IMyService
            {
                ValueTask<string> GetAsync(string id, CancellationToken ct);
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().ContainSingle();
        errors[0].Id.Should().Be("ZR0004");
        errors[0].GetMessage(CultureInfo.InvariantCulture).Should().Contain(property);
    }

    [Fact]
    public void InterfaceLevelInvalidValue_WithTwoMethods_ReportsExactlyOnce()
    {
        var source = """
            using ZeroAlloc.Resilience;
            using System.Threading;
            using System.Threading.Tasks;
            namespace T;
            [Retry(MaxAttempts = 0)]
            public interface IMyService
            {
                ValueTask<string> GetAsync(string id, CancellationToken ct);
                ValueTask<string> PostAsync(string id, CancellationToken ct);
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().ContainSingle();
        errors[0].Id.Should().Be("ZR0004");
    }

    [Fact]
    public void MethodLevelInvalidValue_IsReportedOnTheMethod()
    {
        var source = """
            using ZeroAlloc.Resilience;
            using System.Threading;
            using System.Threading.Tasks;
            namespace T;
            public interface IMyService
            {
                [Retry(MaxAttempts = 0)]
                ValueTask<string> GetAsync(string id, CancellationToken ct);

                ValueTask<string> PostAsync(string id, CancellationToken ct);
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().ContainSingle();
        errors[0].Id.Should().Be("ZR0004");
        errors[0].GetMessage(CultureInfo.InvariantCulture).Should().Contain("GetAsync");
    }

    [Fact]
    public void TwoInvalidPropertiesInOneAttribute_ReportsTwoZR0004()
    {
        var source = """
            using ZeroAlloc.Resilience;
            using System.Threading;
            using System.Threading.Tasks;
            namespace T;
            [Retry(MaxAttempts = 0, BackoffMs = -1)]
            public interface IMyService
            {
                ValueTask<string> GetAsync(string id, CancellationToken ct);
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().HaveCount(2);
        errors[0].Id.Should().Be("ZR0004");
        errors[1].Id.Should().Be("ZR0004");
        // RetryRules checks MaxAttempts before BackoffMs, so the diagnostic order is deterministic.
        errors[0].GetMessage(CultureInfo.InvariantCulture).Should().Contain("MaxAttempts");
        errors[1].GetMessage(CultureInfo.InvariantCulture).Should().Contain("BackoffMs");
    }

    [Fact]
    public void BoundaryValues_AreValid_NoDiagnostics()
    {
        var source = """
            using ZeroAlloc.Resilience;
            using System.Threading;
            using System.Threading.Tasks;
            namespace T;
            [Retry(MaxAttempts = 1, BackoffMs = 0, PerAttemptTimeoutMs = 0)]
            [Timeout(Ms = 1)]
            [CircuitBreaker(MaxFailures = 1, ResetMs = 0, HalfOpenProbes = 1)]
            [RateLimit(MaxPerSecond = 0, BurstSize = 0)]
            public interface IMyService
            {
                ValueTask<string> GetAsync(string id, CancellationToken ct);
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void InvalidValue_NoProxyGenerated()
    {
        var source = """
            using ZeroAlloc.Resilience;
            using System.Threading;
            using System.Threading.Tasks;
            namespace T;
            [Retry(MaxAttempts = 0)]
            public interface IMyService
            {
                ValueTask<string> GetAsync(string id, CancellationToken ct);
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().ContainSingle();
        errors[0].Id.Should().Be("ZR0004");
        compilation.GetTypeByMetadataName("T.IMyServiceResilienceProxy").Should().BeNull();
    }
}
