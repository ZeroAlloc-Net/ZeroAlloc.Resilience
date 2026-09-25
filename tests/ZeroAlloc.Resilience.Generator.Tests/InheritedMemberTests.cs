using System.Threading.Tasks;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Resilience.Generator.Tests;

// #169: the proxy forwards members inherited from base interfaces and default-implemented
// members, not only the interface's own abstract ones. The five "must still compile" tests are
// interface shapes that compiled on 2.0.0 and that an earlier attempt at #169 broke.
public class InheritedMemberTests
{
    private static INamedTypeSymbol Proxy(Compilation compilation, string name)
    {
        var proxy = compilation.GetTypeByMetadataName($"Repro.{name}ResilienceProxy");
        proxy.Should().NotBeNull($"a proxy for {name} must be generated");
        return proxy!;
    }

    private static string GeneratedSource(Compilation compilation, string name) =>
        compilation.SyntaxTrees
            .First(t => t.FilePath.EndsWith($"Repro_{name}.Resilience.g.cs", System.StringComparison.Ordinal))
            .ToString();

    [Fact]
    public void InheritedMethodAndProperty_AreForwarded_AndCompile()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IBase
            {
                ValueTask PingAsync(CancellationToken ct);
                string Name { get; }
            }
            [Retry(MaxAttempts = 2)]
            public interface IDerived : IBase
            {
                ValueTask<int> CountAsync(CancellationToken ct);
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        var proxy = Proxy(compilation, "IDerived");
        proxy.GetMembers("PingAsync").Should().ContainSingle();
        proxy.GetMembers("Name").Should().ContainSingle();
        proxy.GetMembers("CountAsync").Should().ContainSingle();
    }

    [Fact]
    public void BridgeShape_NoOwnMembers_GetsProxy_WithInheritedMethodUnderInterfaceRetry()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            public sealed class Msg { }
            public interface IBaseDispatcher<TMessage>
            {
                ValueTask DispatchAsync(TMessage m, CancellationToken ct);
            }
            [Retry(MaxAttempts = 3)]
            public interface IMyDispatcher : IBaseDispatcher<Msg> { }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        Proxy(compilation, "IMyDispatcher").GetMembers("DispatchAsync").Should().ContainSingle();
        GeneratedSource(compilation, "IMyDispatcher").Should().Contain("_retry.MaxAttempts",
            "the inherited DispatchAsync must be wrapped in the interface-level [Retry]");
    }

    // ── The five shapes that compile on 2.0.0 ───────────────────────────────────

    [Fact]
    public void Shape1_ExplicitOverrideInDerivedInterface_Compiles()
    {
        var source = """
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IBase
            {
                string Name { get; }
            }
            [Retry(MaxAttempts = 2)]
            public interface IDerived : IBase
            {
                string IBase.Name => "x";
                void Own();
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        Proxy(compilation, "IDerived").GetMembers("Name").Should().BeEmpty(
            "IBase.Name is already implemented by IDerived, so the proxy must not forward it");
    }

    [Fact]
    public void Shape2_PrivateHelperInBaseInterface_Compiles()
    {
        var source = """
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IBase
            {
                string Describe() => Helper();
                private string Helper() => "h";
            }
            [Retry(MaxAttempts = 2)]
            public interface IDerived : IBase
            {
                void Own();
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        var proxy = Proxy(compilation, "IDerived");
        proxy.GetMembers("Helper").Should().BeEmpty();
        proxy.GetMembers("Describe").Should().ContainSingle();
    }

    [Fact]
    public void Shape3_ProtectedMemberInBaseInterface_Compiles()
    {
        var source = """
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IBase
            {
                protected string Describe() => "d";
                protected int Level => 1;
            }
            [Retry(MaxAttempts = 2)]
            public interface IDerived : IBase
            {
                void Own();
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        var proxy = Proxy(compilation, "IDerived");
        proxy.GetMembers("Describe").Should().BeEmpty();
        proxy.GetMembers("Level").Should().BeEmpty();
    }

    [Fact]
    public void Shape4_NewMemberWithExplicitReimplementationOfBase_Compiles_WithOnlyTheNewMember()
    {
        var source = """
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IRepo
            {
                object Get(int id);
            }
            [Retry(MaxAttempts = 2)]
            public interface IRepo2 : IRepo
            {
                new string Get(int id);
                object IRepo.Get(int id) => Get(id);
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        var get = Proxy(compilation, "IRepo2").GetMembers("Get").Should().ContainSingle().Subject;
        ((IMethodSymbol)get).ReturnType.SpecialType.Should().Be(SpecialType.System_String);
    }

    [Fact]
    public void Shape5_InheritedDefaultMethod_WithInterfaceLevelFallbackForOwnMethods_Compiles_NoZR0001()
    {
        // GetFallbackAsync matches every own method the interface-level circuit breaker applies
        // to. The inherited default CountAsync does not match it and compiled on 2.0.0, where the
        // proxy did not forward it: it gets the circuit breaker without the fallback instead of
        // a ZR0001 that would turn a compiling interface into an error.
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IBase
            {
                ValueTask<int> CountAsync(CancellationToken ct) => new ValueTask<int>(0);
            }
            [CircuitBreaker(Fallback = "GetFallbackAsync")]
            public interface IDerived : IBase
            {
                ValueTask<string> GetAsync(string id, CancellationToken ct);
                ValueTask<string> GetFallbackAsync(string id, CancellationToken ct);
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        Proxy(compilation, "IDerived").GetMembers("CountAsync").Should().ContainSingle();
        var generated = GeneratedSource(compilation, "IDerived");
        generated.Should().Contain("return await _inner.GetFallbackAsync(id, ct)");
        generated.Should().NotContain("GetFallbackAsync(ct)");
    }

    [Fact]
    public void InterfaceLevelFallback_OwnMethodSignatureMismatch_StillReportsZR0001()
    {
        // The own-method rule is unchanged from 2.0.x: an interface-level Fallback must match
        // every method the interface itself declares.
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IBase
            {
                ValueTask<int> CountAsync(CancellationToken ct) => new ValueTask<int>(0);
            }
            [CircuitBreaker(Fallback = "GetFallbackAsync")]
            public interface IDerived : IBase
            {
                ValueTask<string> GetAsync(string id, CancellationToken ct);
                ValueTask<int> OtherAsync(CancellationToken ct);
                ValueTask<string> GetFallbackAsync(string id, CancellationToken ct);
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().ContainSingle(static d => d.Id == "ZR0001")
            .Which.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Should().Contain("OtherAsync");
    }

    // ── Diamonds ────────────────────────────────────────────────────────────────

    [Fact]
    public void Diamond_IdenticalMembers_CollapseToOneForwardingMember()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IBaseA
            {
                string Name { get; }
                ValueTask PingAsync(CancellationToken ct);
            }
            public interface IBaseB
            {
                string Name { get; }
                ValueTask PingAsync(CancellationToken ct);
            }
            [Retry(MaxAttempts = 2)]
            public interface ICompatible : IBaseA, IBaseB
            {
                void Get();
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        var proxy = Proxy(compilation, "ICompatible");
        proxy.GetMembers("Name").Should().ContainSingle();
        proxy.GetMembers("PingAsync").Should().ContainSingle();
    }

    [Fact]
    public void Diamond_IdenticalNullableMembers_Collapse()
    {
        var source = """
            #nullable enable
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IBaseA
            {
                string? Name { get; }
                string? Find(string? key);
            }
            public interface IBaseB
            {
                string? Name { get; }
                string? Find(string? key);
            }
            [Retry(MaxAttempts = 2)]
            public interface ICompatible : IBaseA, IBaseB
            {
                void Get();
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        compilation.GetDiagnostics().Should().NotContain(static d => d.Id == "CS8766" || d.Id == "CS8767");
        var proxy = Proxy(compilation, "ICompatible");
        proxy.GetMembers("Name").Should().ContainSingle();
        proxy.GetMembers("Find").Should().ContainSingle();
    }

    [Fact]
    public void OwnMethodHidingInheritedDefaultPropertyOfSameName_Compiles()
    {
        // Compiled on 2.0.x: the inherited property has a default body and keeps it.
        var source = """
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IBase
            {
                string Name => "base";
            }
            [Retry(MaxAttempts = 2)]
            public interface IDerived : IBase
            {
                new string Name();
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        Proxy(compilation, "IDerived").GetMembers("Name").Should().ContainSingle()
            .Which.Should().BeAssignableTo<IMethodSymbol>();
    }

    // ── Which interfaces get a proxy ────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("[Marker]")]
    public void DerivedInterface_WithoutOwnPolicy_GetsNoProxy_ButBaseDoes(string derivedAttribute)
    {
        // Only a policy attribute on the interface itself or one of its own members makes a
        // proxy; an unrelated attribute does not, and neither does an inherited policy.
        var source = $$"""
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            [System.AttributeUsage(System.AttributeTargets.Interface)]
            public sealed class MarkerAttribute : System.Attribute { }
            public interface IBase
            {
                [Retry(MaxAttempts = 2)]
                ValueTask PingAsync(CancellationToken ct);
            }
            {{derivedAttribute}}
            public interface IDerived : IBase
            {
                ValueTask OtherAsync(CancellationToken ct);
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        compilation.GetTypeByMetadataName("Repro.IBaseResilienceProxy").Should().NotBeNull();
        compilation.GetTypeByMetadataName("Repro.IDerivedResilienceProxy").Should().BeNull();
    }

    // ── ZR0006: policy not applied to an inherited default method ───────────────

    [Fact]
    public async Task InheritedDefaultMethod_RateLimitOnForeignError_ReportsZR0006_AndStillRetries()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            using ZeroAlloc.Results;
            namespace Repro;
            public sealed class MyError { }
            public interface IBase
            {
                ValueTask<Result<int, MyError>> GetAsync(CancellationToken ct) => default;
            }
            [Retry(MaxAttempts = 2)]
            [RateLimit(MaxPerSecond = 10)]
            public interface IDerived : IBase
            {
                ValueTask OwnAsync(CancellationToken ct);
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);
        var diagnostics = await TestHelper.GetDiagnostics<ResilienceGenerator>(source);

        errors.Should().BeEmpty();
        var zr0006 = diagnostics.Should().ContainSingle(static d => d.Id == "ZR0006").Subject;
        zr0006.Severity.Should().Be(DiagnosticSeverity.Warning);
        var message = zr0006.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        message.Should().Contain("GetAsync").And.Contain("[RateLimit]").And.Contain("MyError");
        var generated = GeneratedSource(compilation, "IDerived");
        generated.Split("TryAcquire()").Length.Should().Be(2, "only OwnAsync is rate-limited");
        generated.Should().Contain("((global::Repro.IBase)_inner).GetAsync(__ct)", "the retry still applies");
    }

    [Fact]
    public async Task InheritedDefaultMethod_NonThrowingOnNonResult_ReportsZR0006_AndCompiles()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            using ZeroAlloc.Results;
            namespace Repro;
            public interface IBase
            {
                string Describe() => "d";
            }
            [Retry(MaxAttempts = 2, NonThrowing = true)]
            public interface IDerived : IBase
            {
                ValueTask<Result<int, ResilienceError>> OwnAsync(CancellationToken ct);
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);
        var diagnostics = await TestHelper.GetDiagnostics<ResilienceGenerator>(source);

        errors.Should().BeEmpty();
        diagnostics.Should().ContainSingle(static d => d.Id == "ZR0006")
            .Which.GetMessage(System.Globalization.CultureInfo.InvariantCulture)
            .Should().Contain("Describe").And.Contain("NonThrowing");
        GeneratedSource(compilation, "IDerived").Should().Contain("=> ((global::Repro.IBase)_inner).Describe();");
    }

    // ── Fallback lookup ─────────────────────────────────────────────────────────

    [Fact]
    public void FallbackDeclaredInBaseInterface_Resolves()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IBase
            {
                ValueTask<string> GetFallbackAsync(string id, CancellationToken ct);
            }
            [CircuitBreaker(Fallback = "GetFallbackAsync")]
            public interface IDerived : IBase
            {
                ValueTask<string> GetAsync(string id, CancellationToken ct);
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        GeneratedSource(compilation, "IDerived").Should().Contain(".GetFallbackAsync(id, ct)");
    }

    [Fact]
    public void MethodLevelPolicyOnInheritedMethod_Applies_AfterOwnSlots()
    {
        // Own methods keep the slot names they had before #169; inherited ones come after them.
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IBase
            {
                [Retry(MaxAttempts = 5)]
                ValueTask GetAsync(int id, CancellationToken ct);
            }
            public interface IDerived : IBase
            {
                [Retry(MaxAttempts = 4)]
                ValueTask GetAsync(string id, CancellationToken ct);
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        var policies = compilation.GetTypeByMetadataName("Repro.DerivedResiliencePolicies");
        policies.Should().NotBeNull();
        policies!.GetMembers().OfType<IPropertySymbol>().Select(static p => p.Name)
            .Should().Equal("GetAsyncRetry", "GetAsync2Retry");
        GeneratedSource(compilation, "IDerived").Should().Contain("RetryPolicy(5,");
    }
}
