using System;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Resilience.Generator.Tests;

// An IAsyncEnumerable<T> method takes [Retry] alone, retried until its first item. A policy that
// cannot apply to a stream is an Error on the method itself, ZR0014, and a Warning, ZR0015, when
// it comes from the interface, which leaves it off the stream method and keeps it on the others.
public class AsyncEnumerableTests
{
    private const string Usings = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using ZeroAlloc.Resilience;
        using ZeroAlloc.Results;
        namespace Repro;

        """;

    private static string SourceAt(string source, Diagnostic diagnostic) =>
        source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length);

    private static Diagnostic[] WithId(string source, string id) =>
        TestHelper.GeneratorDiagnostics(source)
            .Where(d => string.Equals(d.Id, id, StringComparison.Ordinal))
            .ToArray();

    // Red, wherever it is called: emit the stream iterator without `async`, so its yields and
    // awaits do not compile.
    private static void ShouldCompileWithoutErrors(string source)
    {
        var (_, errors) = TestHelper.RunAndCompile(source);
        errors.Should().BeEmpty();
    }

    // ── ZR0014: a refused policy on the stream method itself ───────────────────

    [Theory]
    [InlineData("[Timeout(Ms = 1000)]", "[Timeout]", "Timeout(")]
    [InlineData("[CircuitBreaker(MaxFailures = 3)]", "[CircuitBreaker]", "CircuitBreaker(")]
    [InlineData("[RateLimit(MaxPerSecond = 10)]", "[RateLimit]", "RateLimit(")]
    [InlineData("[Retry(PerAttemptTimeoutMs = 100)]", "[Retry] PerAttemptTimeoutMs", "Retry(")]
    public void A_refused_policy_on_a_stream_method_reports_ZR0014(string attribute, string policy, string attributeStart)
    {
        var source = Usings + $$"""
            [Retry(MaxAttempts = 2)]
            public interface IApi
            {
                {{attribute}}
                IAsyncEnumerable<int> StreamAsync(CancellationToken ct);
            }
            """;

        // Red: skip the stream rules in ParseMethod, so the stream is wrapped like a sync method.
        var diagnostic = WithId(source, "ZR0014").Should().ContainSingle().Subject;
        // Red: report ZR0014 with the effective severity Warning.
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
        // Red: drop " from 'StreamAsync'" from the advice in the message format.
        diagnostic.GetMessage(CultureInfo.InvariantCulture).Should().Be(
            $"{policy} cannot apply to 'StreamAsync', because it returns IAsyncEnumerable<T>: a stream method takes [Retry] alone, " +
            $"without PerAttemptTimeoutMs, and is retried only until its first item. Remove {policy} from 'StreamAsync'.");
        // Red: report at the method's location in place of the attribute's.
        SourceAt(source, diagnostic).Should().StartWith(attributeStart);
        // Red: report ZR0015 as well as ZR0014 for a method-level attribute.
        WithId(source, "ZR0015").Should().BeEmpty();
    }

    [Fact]
    public void PerAttemptTimeoutMs_0_on_a_stream_method_is_not_reported()
    {
        var source = Usings + """
            public interface IApi
            {
                [Retry(PerAttemptTimeoutMs = 0)]
                IAsyncEnumerable<int> StreamAsync(CancellationToken ct);
            }
            """;

        // Red: report PerAttemptTimeoutMs whenever the property is set, in place of when it is > 0.
        WithId(source, "ZR0014").Should().BeEmpty();
        ShouldCompileWithoutErrors(source);
    }

    // ── ZR0015: a refused policy from the interface ────────────────────────────

    [Theory]
    [InlineData("[Timeout(Ms = 1000)]", "[Timeout]")]
    [InlineData("[CircuitBreaker(MaxFailures = 3)]", "[CircuitBreaker]")]
    [InlineData("[RateLimit(MaxPerSecond = 10)]", "[RateLimit]")]
    public void An_interface_level_policy_skips_the_stream_method_with_ZR0015(string attribute, string policy)
    {
        var source = Usings + $$"""
            [Retry(MaxAttempts = 2)]
            {{attribute}}
            public interface IApi
            {
                ValueTask<int> GetAsync(CancellationToken ct);
                IAsyncEnumerable<int> StreamAsync(CancellationToken ct);
            }
            """;

        // Red: skip the stream rules in ParseMethod.
        var diagnostic = WithId(source, "ZR0015").Should().ContainSingle().Subject;
        // Red: report ZR0015 with the effective severity Error.
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        // Red: word the advice "Remove {policy} from 'IApi'".
        diagnostic.GetMessage(CultureInfo.InvariantCulture).Should().Be(
            $"{policy} from 'IApi' is not applied to 'StreamAsync', because it returns IAsyncEnumerable<T>: a stream method takes " +
            $"[Retry] alone, without PerAttemptTimeoutMs, and is retried only until its first item. Move {policy} from 'IApi' " +
            "onto the methods that need it.");
        // Red: report at the interface's location in place of the stream method's.
        SourceAt(source, diagnostic).Should().Be("StreamAsync");
        // Red: report an interface-level attribute as ZR0014, as if it were the method's own.
        WithId(source, "ZR0014").Should().BeEmpty();
        ShouldCompileWithoutErrors(source);
    }

    [Fact]
    public void An_interface_level_Timeout_skips_the_stream_method_with_ZR0015()
    {
        var source = Usings + """
            [Retry(MaxAttempts = 2)]
            [Timeout(Ms = 1000)]
            public interface IApi
            {
                ValueTask<int> GetAsync(CancellationToken ct);
                IAsyncEnumerable<int> StreamAsync(CancellationToken ct);
            }
            """;

        // Red: skip the stream rules in ParseMethod.
        WithId(source, "ZR0015").Should().ContainSingle();
        var methods = TestHelper.Models(source).Should().ContainSingle().Subject.Methods;
        var stream = methods.Should().ContainSingle(static m => m.Name == "StreamAsync").Subject;
        // Red: report ZR0015 but keep the interface's [Timeout] on the stream method.
        stream.Timeout.Should().BeNull();
        // Red: add the method model without WithStream.
        stream.IsAsyncEnumerable.Should().BeTrue();
        // Red: drop the interface's [Timeout] from the methods that are not streams as well.
        methods.Should().ContainSingle(static m => m.Name == "GetAsync").Which.Timeout.Should().NotBeNull();
        ShouldCompileWithoutErrors(source);
    }

    [Fact]
    public void An_interface_level_PerAttemptTimeoutMs_is_reported_with_ZR0015()
    {
        var source = Usings + """
            [Retry(MaxAttempts = 2, PerAttemptTimeoutMs = 100)]
            public interface IApi
            {
                IAsyncEnumerable<int> StreamAsync(CancellationToken ct);
            }
            """;

        // Red: check PerAttemptTimeoutMs on the method's own [Retry] only.
        var diagnostic = WithId(source, "ZR0015").Should().ContainSingle().Subject;
        // Red: name the policy "[Retry]" in place of "[Retry] PerAttemptTimeoutMs".
        diagnostic.GetMessage(CultureInfo.InvariantCulture).Should().StartWith(
            "[Retry] PerAttemptTimeoutMs from 'IApi' is not applied to 'StreamAsync'");
        // The retry itself still applies. Red: drop the [Retry] along with its PerAttemptTimeoutMs.
        TestHelper.Models(source).Single().Methods.Should().ContainSingle().Which.Retry.Should().NotBeNull();
        ShouldCompileWithoutErrors(source);
    }

    [Fact]
    public void A_stream_method_left_with_no_policy_is_forwarded_unchanged()
    {
        var source = Usings + """
            [Timeout(Ms = 1000)]
            public interface IApi
            {
                ValueTask<int> GetAsync(CancellationToken ct);
                IAsyncEnumerable<int> StreamAsync(CancellationToken ct);
            }
            """;

        var model = TestHelper.Models(source).Single();
        // Red, for both: report ZR0015 but keep the interface's [Timeout] on the stream method,
        // and add its model without WithStream, so it is wrapped like a sync method.
        model.Methods.Should().ContainSingle().Which.Name.Should().Be("GetAsync");
        model.PassthroughMethods.Should().ContainSingle().Which.Name.Should().Be("StreamAsync");
        // Red: skip the stream rules in ParseMethod.
        WithId(source, "ZR0015").Should().ContainSingle();
        ShouldCompileWithoutErrors(source);
    }

    // ── ZR0010: RetryWhen has no Result to read ────────────────────────────────

    [Fact]
    public void RetryWhen_on_a_stream_method_reports_ZR0010()
    {
        var source = Usings + """
            public sealed class HttpError { }
            public interface IApi
            {
                [Retry(RetryWhen = nameof(IsTransient))]
                IAsyncEnumerable<Result<int, HttpError>> StreamAsync(CancellationToken ct);

                static bool IsTransient(HttpError error) => true;
            }
            """;

        // Red: unwrap IAsyncEnumerable<T> to T as the method's Result type, as for a Task.
        var diagnostic = WithId(source, "ZR0010").Should().ContainSingle().Subject;
        // Red: pass `methodLevel: false` to ResolveRetryMembers.
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
        // Red: as for ContainSingle, unwrap IAsyncEnumerable<T> to T.
        diagnostic.GetMessage(CultureInfo.InvariantCulture).Should()
            .Contain("cannot apply to 'StreamAsync', because it does not return a ZeroAlloc.Results Result");
    }

    // ── The generated stream proxy compiles for every shape ────────────────────

    [Theory]
    [InlineData("IAsyncEnumerable<int> StreamAsync(string id, CancellationToken ct);")]
    [InlineData("IAsyncEnumerable<int> StreamAsync(string id);")]
    [InlineData("IAsyncEnumerable<string?> StreamAsync(CancellationToken @checked, CancellationToken other);")]
    [InlineData("IAsyncEnumerable<T> StreamAsync<T>(T seed, CancellationToken ct) where T : class;")]
    [InlineData("IAsyncEnumerable<int> StreamAsync([RetryAttempt] int attempt, CancellationToken ct);")]
    [InlineData("IAsyncEnumerable<int> StreamAsync(CancellationToken ct, params int[] values);")]
    [InlineData("IAsyncEnumerable<int> ToString();")]
    public void A_stream_method_proxy_compiles_without_warnings(string declaration)
    {
        var source = "#nullable enable\n" + Usings + $$"""
            [Retry(MaxAttempts = 2)]
            public interface IApi
            {
                {{declaration}}
            }
            """;

        var compilation = TestHelper.CreateCompilation(source, referenceDependencyInjection: true);
        Microsoft.CodeAnalysis.CSharp.CSharpGeneratorDriver
            .Create(new ResilienceGenerator())
            .WithUpdatedParseOptions((Microsoft.CodeAnalysis.CSharp.CSharpParseOptions)compilation.SyntaxTrees.First().Options)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);

        // Red: report every stream method as unwrappable, ZR0007.
        generatorDiagnostics.Should().BeEmpty();
        // CS8425 is the warning for an async iterator whose CancellationToken parameter lacks
        // [EnumeratorCancellation]. Red: drop the attribute from the iterator's token parameter.
        output.GetDiagnostics()
            .Where(static d => d.Severity >= DiagnosticSeverity.Warning)
            .Should().BeEmpty();
        // Red: add no source in the generator's source output.
        output.GetTypeByMetadataName("Repro.IApiResilienceProxy").Should().NotBeNull();
        // Red: treat the stream as passthrough: the proxy then has no iterator helper.
        output.SyntaxTrees.Last().ToString().Should().Contain("GetAsyncEnumerator(__ct)");
    }

    [Fact]
    public void Collapsed_stream_declarations_compile()
    {
        var source = Usings + """
            public interface IFirst { IAsyncEnumerable<int> StreamAsync(CancellationToken ct); }
            public interface ISecond { IAsyncEnumerable<int> StreamAsync(CancellationToken ct); }
            [Retry(MaxAttempts = 2)]
            public interface IApi : IFirst, ISecond { }
            """;

        // Red: emit the collapsed core helper without `async`, so its yields do not compile.
        ShouldCompileWithoutErrors(source);
    }

    [Fact]
    public void A_stream_method_with_a_ref_struct_parameter_reports_ZR0007()
    {
        var source = Usings + """
            [Retry(MaxAttempts = 2)]
            public interface IApi
            {
                IAsyncEnumerable<int> StreamAsync(ReadOnlySpan<char> id, CancellationToken ct);
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        // Red: pass `method.IsAsync` alone to UnwrappableReason, so the stream is wrapped.
        errors.Should().ContainSingle().Which.Id.Should().Be("ZR0007");
    }
}
