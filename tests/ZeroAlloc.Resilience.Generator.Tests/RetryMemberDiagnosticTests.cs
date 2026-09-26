using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Resilience.Generator.Tests;

// #142 and #143: RetryWhen, RetryOnException and DelayHint name static methods on the interface
// or a base interface. ZR0009 reports a name with no method of the required shape; ZR0010 reports
// RetryWhen, or a DelayHint overload that takes the Result error type, on a method it cannot
// apply to.
public class RetryMemberDiagnosticTests
{
    private const string Usings = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using ZeroAlloc.Resilience;
        using ZeroAlloc.Results;
        namespace Repro;
        public sealed class HttpError { public int Status { get; init; } }
        public sealed class OtherError { }
        public sealed class TransientException : Exception { }

        """;

    private static string SourceAt(string source, Diagnostic diagnostic) =>
        source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length);

    private static Diagnostic[] Zr(string source)
    {
        var zr = new System.Collections.Generic.List<Diagnostic>();
        foreach (var diagnostic in TestHelper.GeneratorDiagnostics(source))
        {
            if (diagnostic.Id.StartsWith("ZR", System.StringComparison.Ordinal))
                zr.Add(diagnostic);
        }
        return zr.ToArray();
    }

    // ── ZR0009 ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("", "missing")]
    [InlineData("bool IsTransient(HttpError error) => true;", "not static")]
    [InlineData("static bool IsTransient(HttpError error, int attempt) => true;", "wrong parameter count")]
    [InlineData("static bool IsTransient(ref HttpError error) => true;", "by-reference parameter")]
    [InlineData("static bool IsTransient(in HttpError error) => true;", "in parameter")]
    [InlineData("static int IsTransient(HttpError error) => 0;", "wrong return type")]
    [InlineData("private static bool IsTransient(HttpError error) => true;", "not accessible from the proxy")]
    [InlineData("static bool IsTransient<T>(HttpError error) => true;", "generic")]
    public void RetryWhen_WithoutAMatchingStaticMethod_ReportsZR0009AtTheAttribute(string member, string because)
    {
        var source = Usings + $$"""
            [Retry(RetryWhen = "IsTransient")]
            public interface IApi
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
                {{member}}
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        var zr0009 = errors.Should().ContainSingle(because).Subject;
        zr0009.Id.Should().Be("ZR0009");
        zr0009.Severity.Should().Be(DiagnosticSeverity.Error);
        zr0009.GetMessage(CultureInfo.InvariantCulture).Should()
            .Contain("RetryWhen = \"IsTransient\"")
            .And.Contain("'static bool IsTransient(E error)', where E is the error type of the method's Result");
        SourceAt(source, zr0009).Should().StartWith("Retry(");
    }

    [Fact]
    public void MethodLevelRetryWhen_Missing_NamesTheErrorTypeInTheMessage()
    {
        var source = Usings + """
            public interface IApi
            {
                [Retry(RetryWhen = "IsTransient")]
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        var zr0009 = errors.Should().ContainSingle().Subject;
        zr0009.Id.Should().Be("ZR0009");
        zr0009.Severity.Should().Be(DiagnosticSeverity.Error);
        SourceAt(source, zr0009).Should().StartWith("Retry(");
        zr0009.GetMessage(CultureInfo.InvariantCulture)
            .Should().Contain("'static bool IsTransient(HttpError error)'");
    }

    [Theory]
    [InlineData("RetryOnException", "static bool Check(string message) => true;", "'static bool Check(Exception exception)'")]
    [InlineData("DelayHint", "static TimeSpan Check(HttpError error) => TimeSpan.Zero;", "'static TimeSpan? Check(E error)' or 'static TimeSpan? Check(Exception exception)'")]
    public void OtherNames_WithoutAMatchingStaticMethod_ReportZR0009(string property, string member, string expected)
    {
        var source = Usings + $$"""
            [Retry({{property}} = "Check")]
            public interface IApi
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
                {{member}}
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        var zr0009 = errors.Should().ContainSingle().Subject;
        zr0009.Id.Should().Be("ZR0009");
        zr0009.Severity.Should().Be(DiagnosticSeverity.Error);
        SourceAt(source, zr0009).Should().StartWith("Retry(");
        zr0009.GetMessage(CultureInfo.InvariantCulture).Should().Contain(expected);
    }

    [Theory]
    [InlineData("public static bool IsTransient(HttpError error) => true;")]
    [InlineData("internal static bool IsTransient(HttpError error) => true;")]
    [InlineData("static bool IsTransient(HttpError error) => true;")]
    public void RetryWhen_AccessibleStaticMethod_HasNoDiagnostics(string member)
    {
        var source = Usings + $$"""
            [Retry(RetryWhen = nameof(IsTransient))]
            public interface IApi
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
                {{member}}
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        Zr(source).Should().BeEmpty();
    }

    [Fact]
    public void RetryMembers_DeclaredOnABaseInterface_AreFound()
    {
        var source = Usings + """
            public interface IRetryRules
            {
                static bool IsTransient(HttpError error) => error.Status == 429;
                static bool IsTransientException(Exception exception) => true;
                static TimeSpan? RetryAfter(HttpError error) => null;
                static TimeSpan? RetryAfter(Exception exception) => null;
            }
            [Retry(RetryWhen = nameof(IRetryRules.IsTransient), RetryOnException = nameof(IRetryRules.IsTransientException),
                   DelayHint = nameof(IRetryRules.RetryAfter))]
            public interface IApi : IRetryRules
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        Zr(source).Should().BeEmpty();
    }

    [Fact]
    public void StaticAbstractPredicate_IsZR0007_NotZR0009()
    {
        var source = Usings + """
            [Retry(RetryWhen = nameof(IsTransient))]
            public interface IApi
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
                static abstract bool IsTransient(HttpError error);
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        var zr0007 = errors.Should().ContainSingle().Subject;
        zr0007.Id.Should().Be("ZR0007");
        zr0007.Severity.Should().Be(DiagnosticSeverity.Error);
        SourceAt(source, zr0007).Should().Be("IApi");
        zr0007.GetMessage(CultureInfo.InvariantCulture).Should().Be(
            "'IApi' is not supported by the resilience generator: 'IApi.IsTransient' is a static abstract or static virtual member, "
            + "which a proxy instance cannot implement. No proxy is generated for it.");
    }

    // ── ZR0010 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void MethodLevelRetryWhen_OnANonResultMethod_ReportsZR0010ErrorAtTheMethod()
    {
        var source = Usings + """
            public interface IApi
            {
                [Retry(RetryWhen = nameof(IsTransient))]
                ValueTask<string> GetAsync(CancellationToken ct);
                static bool IsTransient(HttpError error) => true;
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        var zr0010 = errors.Should().ContainSingle().Subject;
        zr0010.Id.Should().Be("ZR0010");
        zr0010.Severity.Should().Be(DiagnosticSeverity.Error);
        SourceAt(source, zr0010).Should().Be("GetAsync");
        zr0010.GetMessage(CultureInfo.InvariantCulture).Should()
            .Contain("RetryWhen = \"IsTransient\"")
            .And.Contain("does not return a ZeroAlloc.Results Result");
    }

    [Fact]
    public void MethodLevelRetryWhen_OnAnotherErrorType_ReportsZR0010Error()
    {
        var source = Usings + """
            public interface IApi
            {
                [Retry(RetryWhen = nameof(IsTransient))]
                ValueTask<Result<string, OtherError>> GetAsync(CancellationToken ct);
                static bool IsTransient(HttpError error) => true;
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        var zr0010 = errors.Should().ContainSingle().Subject;
        zr0010.Id.Should().Be("ZR0010");
        zr0010.Severity.Should().Be(DiagnosticSeverity.Error);
        SourceAt(source, zr0010).Should().Be("GetAsync");
        zr0010.GetMessage(CultureInfo.InvariantCulture).Should()
            .Contain("its Result error type is 'OtherError'")
            .And.Contain("no 'IsTransient' overload takes it");
    }

    [Fact]
    public void InterfaceLevelRetryWhen_OnAMixedInterface_WarnsForEachMethodItSkips()
    {
        var source = Usings + """
            [Retry(RetryWhen = nameof(IsTransient))]
            public interface IApi
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
                ValueTask<string> GetTextAsync(CancellationToken ct);
                string GetName();
                static bool IsTransient(HttpError error) => true;
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);
        var warnings = Zr(source);

        errors.Should().BeEmpty();
        warnings.Should().HaveCount(2).And.OnlyContain(static d => d.Id == "ZR0010" && d.Severity == DiagnosticSeverity.Warning);
        warnings.Select(d => (SourceAt(source, d), d.GetMessage(CultureInfo.InvariantCulture))).Should().BeEquivalentTo(new[]
        {
            ("GetTextAsync",
             "[Retry] RetryWhen = \"IsTransient\" cannot apply to 'GetTextAsync', because it does not return a ZeroAlloc.Results "
             + "Result, so there is no failed Result to pass to it. 'GetTextAsync' keeps exception-only retry."),
            ("GetName",
             "[Retry] RetryWhen = \"IsTransient\" cannot apply to 'GetName', because it does not return a ZeroAlloc.Results "
             + "Result, so there is no failed Result to pass to it. 'GetName' keeps exception-only retry."),
        });
    }

    [Fact]
    public void ErrorTypedDelayHint_WithoutRetryWhen_ReportsZR0010()
    {
        var source = Usings + """
            public interface IApi
            {
                [Retry(DelayHint = nameof(RetryAfter))]
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
                static TimeSpan? RetryAfter(HttpError error) => null;
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        var zr0010 = errors.Should().ContainSingle().Subject;
        zr0010.Id.Should().Be("ZR0010");
        zr0010.Severity.Should().Be(DiagnosticSeverity.Error);
        SourceAt(source, zr0010).Should().Be("GetAsync");
        zr0010.GetMessage(CultureInfo.InvariantCulture).Should()
            .Contain("[Retry] DelayHint = \"RetryAfter\" cannot apply")
            .And.Contain("has no RetryWhen");
    }

    [Fact]
    public void RetryWhenAndErrorTypedDelayHint_BothInapplicable_ReportOneZR0010()
    {
        var source = Usings + """
            public interface IApi
            {
                [Retry(RetryWhen = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
                ValueTask<string> GetAsync(CancellationToken ct);
                static bool IsTransient(HttpError error) => true;
                static TimeSpan? RetryAfter(HttpError error) => null;
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        var zr0010 = errors.Should().ContainSingle().Subject;
        zr0010.Id.Should().Be("ZR0010");
        zr0010.Severity.Should().Be(DiagnosticSeverity.Error);
        SourceAt(source, zr0010).Should().Be("GetAsync");
        zr0010.GetMessage(CultureInfo.InvariantCulture).Should()
            .Contain("RetryWhen = \"IsTransient\" and DelayHint = \"RetryAfter\"");
    }

    [Fact]
    public void ExceptionOnlyNames_OnANonResultMethod_HaveNoDiagnostics()
    {
        var source = Usings + """
            [Retry(RetryOnException = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
            public interface IApi
            {
                ValueTask<string> GetAsync(CancellationToken ct);
                string Get();
                static bool IsTransient(Exception exception) => true;
                static TimeSpan? RetryAfter(Exception exception) => null;
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        Zr(source).Should().BeEmpty();
    }

    [Fact]
    public void InterfaceLevelRetryWhen_OnAnInheritedMethod_WarnsAtTheInterface()
    {
        var source = Usings + """
            public interface IBase
            {
                ValueTask<string> GetTextAsync(CancellationToken ct);
            }
            [Retry(RetryWhen = nameof(IsTransient))]
            public interface IApi : IBase
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
                static bool IsTransient(HttpError error) => true;
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        var zr0010 = Zr(source).Should().ContainSingle().Subject;
        zr0010.Id.Should().Be("ZR0010");
        zr0010.Severity.Should().Be(DiagnosticSeverity.Warning);
        SourceAt(source, zr0010).Should().Be("IApi");
        zr0010.GetMessage(CultureInfo.InvariantCulture).Should()
            .Contain("[Retry] RetryWhen = \"IsTransient\" cannot apply to 'GetTextAsync'")
            .And.EndWith("'GetTextAsync' keeps exception-only retry.");
    }

    [Fact]
    public void InheritedMethodWithItsOwnRetry_IsReportedByTheBaseInterfaceOnly()
    {
        var source = Usings + """
            public interface IBase
            {
                [Retry(RetryWhen = nameof(IsTransient))]
                ValueTask<string> GetTextAsync(CancellationToken ct);
                static bool IsTransient(HttpError error) => true;
            }
            [Retry]
            public interface IApi : IBase
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
            }
            """;

        var zr0010 = Zr(source).Should().ContainSingle().Subject;

        zr0010.Id.Should().Be("ZR0010");
        zr0010.Severity.Should().Be(DiagnosticSeverity.Error);
        SourceAt(source, zr0010).Should().Be("GetTextAsync");
        zr0010.GetMessage(CultureInfo.InvariantCulture).Should()
            .Contain("[Retry] RetryWhen = \"IsTransient\" cannot apply to 'GetTextAsync'");
    }

    // ── Nullable annotations and overload sets ─────────────────────────────────

    // TestHelper compiles with nullable disabled, so each source enables it itself.
    [Theory]
    [InlineData("RetryOnException = nameof(IsTransient)", "static bool IsTransient(Exception? exception) => true;")]
    [InlineData("DelayHint = nameof(RetryAfter)", "static TimeSpan? RetryAfter(Exception? exception) => null;")]
    [InlineData("RetryWhen = nameof(IsTransient)", "static bool IsTransient(HttpError? error) => true;")]
    public void NullableAnnotatedParameters_MatchLikeTheirUnannotatedTypes(string property, string member)
    {
        var source = "#nullable enable\n" + Usings + $$"""
            [Retry({{property}})]
            public interface IApi
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
                {{member}}
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        Zr(source).Should().BeEmpty();
    }

    [Fact]
    public void NullableExceptionOverloads_OnANonResultMethod_HaveNoDiagnostics()
    {
        var source = "#nullable enable\n" + Usings + """
            [Retry(RetryOnException = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
            public interface IApi
            {
                ValueTask<string> GetAsync(CancellationToken ct);
                static bool IsTransient(Exception? exception) => true;
                static TimeSpan? RetryAfter(Exception? exception) => null;
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        Zr(source).Should().BeEmpty();
    }

    // DelayHint names an overload set; either overload may be absent. On a method the
    // error-typed overload cannot serve, a resolved Exception overload is all it needs.
    [Fact]
    public void ErrorTypedDelayHintOverload_BesideAnExceptionOverload_OnANonResultMethod_HasNoDiagnostics()
    {
        var source = Usings + """
            public interface IApi
            {
                [Retry(DelayHint = nameof(RetryAfter))]
                ValueTask<string> GetAsync(CancellationToken ct);
                static TimeSpan? RetryAfter(HttpError error) => null;
                static TimeSpan? RetryAfter(Exception exception) => null;
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        Zr(source).Should().BeEmpty();
        var method = TestHelper.Models(source).Should().ContainSingle().Subject.Methods.Should().ContainSingle().Subject;
        method.ResultDelayHintMethod.Should().BeNull();
        method.ExceptionDelayHintMethod.Should().Be("global::Repro.IApi.RetryAfter");
    }

    [Fact]
    public void ErrorTypedDelayHintOverload_BesideAnExceptionOverload_OnAMixedInterface_WarnsForRetryWhenOnly()
    {
        var source = Usings + """
            [Retry(RetryWhen = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
            public interface IApi
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
                ValueTask<string> GetTextAsync(CancellationToken ct);
                static bool IsTransient(HttpError error) => true;
                static TimeSpan? RetryAfter(HttpError error) => null;
                static TimeSpan? RetryAfter(Exception exception) => null;
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        var zr0010 = Zr(source).Should().ContainSingle().Subject;
        zr0010.Id.Should().Be("ZR0010");
        zr0010.Severity.Should().Be(DiagnosticSeverity.Warning);
        SourceAt(source, zr0010).Should().Be("GetTextAsync");
        zr0010.GetMessage(CultureInfo.InvariantCulture).Should()
            .StartWith("[Retry] RetryWhen = \"IsTransient\" cannot apply to 'GetTextAsync'")
            .And.NotContain("DelayHint")
            .And.EndWith("'GetTextAsync' keeps exception-only retry.");
    }

    [Fact]
    public void ErrorTypedDelayHintOverload_BesideAnExceptionOverload_WithoutRetryWhen_HasNoDiagnostics()
    {
        var source = Usings + """
            [Retry(DelayHint = nameof(RetryAfter))]
            public interface IApi
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
                ValueTask<string> GetTextAsync(CancellationToken ct);
                static TimeSpan? RetryAfter(HttpError error) => null;
                static TimeSpan? RetryAfter(Exception exception) => null;
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        Zr(source).Should().BeEmpty();
    }

    [Fact]
    public void ExceptionErrorType_ResolvesTheExceptionOverloadsForTheResultToo()
    {
        var source = Usings + """
            [Retry(RetryWhen = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
            public interface IApi
            {
                ValueTask<Result<string, Exception>> GetAsync(CancellationToken ct);
                static bool IsTransient(Exception error) => true;
                static TimeSpan? RetryAfter(Exception error) => null;
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        Zr(source).Should().BeEmpty();
        var method = TestHelper.Models(source).Should().ContainSingle().Subject.Methods.Should().ContainSingle().Subject;
        method.RetryWhenMethod.Should().Be("global::Repro.IApi.IsTransient");
        method.ResultDelayHintMethod.Should().Be("global::Repro.IApi.RetryAfter");
        method.ExceptionDelayHintMethod.Should().Be("global::Repro.IApi.RetryAfter");
    }

    [Fact]
    public void ResolvedMembers_AreCalledThroughTheirDeclaringInterface()
    {
        var source = Usings + """
            public interface IRetryRules
            {
                static bool IsTransient(HttpError error) => true;
                static bool IsTransientException(Exception exception) => true;
                static TimeSpan? RetryAfter(HttpError error) => null;
                static TimeSpan? RetryAfter(Exception exception) => null;
            }
            [Retry(RetryWhen = "IsTransient", RetryOnException = "IsTransientException", DelayHint = "RetryAfter")]
            public interface IApi : IRetryRules
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
            }
            """;

        var method = TestHelper.Models(source).Should().ContainSingle().Subject.Methods.Should().ContainSingle().Subject;

        method.RetryWhenMethod.Should().Be("global::Repro.IRetryRules.IsTransient");
        method.RetryOnExceptionMethod.Should().Be("global::Repro.IRetryRules.IsTransientException");
        method.ResultDelayHintMethod.Should().Be("global::Repro.IRetryRules.RetryAfter");
        method.ExceptionDelayHintMethod.Should().Be("global::Repro.IRetryRules.RetryAfter");
    }

    [Fact]
    public void ExceptionSubclassDelayHint_BesideAFailingRetryWhen_StatesBothReasons()
    {
        var source = Usings + """
            public interface IApi
            {
                [Retry(RetryWhen = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
                ValueTask<string> GetAsync(CancellationToken ct);
                static bool IsTransient(HttpError error) => true;
                static TimeSpan? RetryAfter(TransientException exception) => null;
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        var zr0010 = errors.Should().ContainSingle().Subject;
        zr0010.Id.Should().Be("ZR0010");
        zr0010.Severity.Should().Be(DiagnosticSeverity.Error);
        SourceAt(source, zr0010).Should().Be("GetAsync");
        zr0010.GetMessage(CultureInfo.InvariantCulture).Should().Be(
            "[Retry] RetryWhen = \"IsTransient\" and DelayHint = \"RetryAfter\" cannot apply to 'GetAsync', because it does not "
            + "return a ZeroAlloc.Results Result, so there is no failed Result to pass to it; and 'RetryAfter(TransientException)' "
            + "takes 'TransientException', and a DelayHint overload for thrown exceptions must take System.Exception. "
            + "Remove RetryWhen from this method's [Retry]. Change the parameter of 'RetryAfter(TransientException)' to "
            + "Exception, and check for 'TransientException' in its body.");
    }

    [Fact]
    public void RetryWhenAndDelayHint_WithoutOverloadsForTheErrorType_ShareOneReason()
    {
        var source = Usings + """
            public interface IApi
            {
                [Retry(RetryWhen = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
                ValueTask<Result<string, OtherError>> GetAsync(CancellationToken ct);
                static bool IsTransient(HttpError error) => true;
                static TimeSpan? RetryAfter(HttpError error) => null;
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        var zr0010 = errors.Should().ContainSingle().Subject;
        zr0010.Id.Should().Be("ZR0010");
        zr0010.Severity.Should().Be(DiagnosticSeverity.Error);
        SourceAt(source, zr0010).Should().Be("GetAsync");
        zr0010.GetMessage(CultureInfo.InvariantCulture).Should().Be(
            "[Retry] RetryWhen = \"IsTransient\" and DelayHint = \"RetryAfter\" cannot apply to 'GetAsync', because its Result "
            + "error type is 'OtherError', and no 'IsTransient' or 'RetryAfter' overload takes it. Declare overloads of "
            + "'IsTransient' and 'RetryAfter' that take 'OtherError', or remove them from this method's [Retry].");
    }

    [Fact]
    public void DelayHintOverload_TakingAnExceptionSubclass_SaysItMustTakeException()
    {
        var source = Usings + """
            public interface IApi
            {
                [Retry(DelayHint = nameof(RetryAfter))]
                ValueTask<string> GetAsync(CancellationToken ct);
                static TimeSpan? RetryAfter(TransientException exception) => null;
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        var zr0010 = errors.Should().ContainSingle().Subject;
        zr0010.Id.Should().Be("ZR0010");
        zr0010.Severity.Should().Be(DiagnosticSeverity.Error);
        SourceAt(source, zr0010).Should().Be("GetAsync");
        zr0010.GetMessage(CultureInfo.InvariantCulture).Should()
            .Contain("'RetryAfter(TransientException)' takes 'TransientException', and a DelayHint overload for thrown exceptions must take System.Exception")
            .And.NotContain("does not return");
    }
}
