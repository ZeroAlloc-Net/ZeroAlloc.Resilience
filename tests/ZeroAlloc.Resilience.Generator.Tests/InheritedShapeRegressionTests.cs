using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Resilience.Generator.Tests;

// Shapes that compiled on 2.0.1, mostly because an interface with no ordinary methods of its own
// got no proxy there. Since 3.0 they get a proxy, so each one has a defined outcome: a valid proxy
// or a specific ZR diagnostic, never a raw compiler error from broken generated code.
public class InheritedShapeRegressionTests
{
    private static void AssertOnly(string source, string id, string proxyMetadataName)
    {
        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().ContainSingle(d => d.Id == id);
        errors.Should().OnlyContain(d => d.Id == id);
        compilation.GetTypeByMetadataName(proxyMetadataName).Should().BeNull();
    }

    private static Compilation AssertProxy(string source, string proxyMetadataName)
    {
        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        compilation.GetTypeByMetadataName(proxyMetadataName).Should().NotBeNull();
        return compilation;
    }

    // ── Name conflicts: see ConflictResolutionTests ────────────────────────────

    [Fact]
    public void EnumerableBase_WithExplicitNonGenericImplementation_GetsProxy()
    {
        AssertProxy("""
            using System.Collections.Generic;
            using ZeroAlloc.Resilience;
            namespace Repro;
            [Retry(MaxAttempts = 2)]
            public interface INumbers : IEnumerable<int>
            {
                System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
            }
            """, "Repro.INumbersResilienceProxy");
    }

    // ── Foreign Result, no own members ──────────────────────────────────────────

    [Fact]
    public void RateLimit_OverInheritedAbstractForeignResult_NoOwnMembers_ReportsZR0003()
    {
        AssertOnly("""
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            using ZeroAlloc.Results;
            namespace Repro;
            public sealed class MyError { }
            public interface IBase
            {
                ValueTask<Result<int, MyError>> GetAsync(CancellationToken ct);
            }
            [RateLimit(MaxPerSecond = 5)]
            public interface IDerived : IBase { }
            """, "ZR0003", "Repro.IDerivedResilienceProxy");
    }

    [Fact]
    public void CircuitBreakerFallback_OverInheritedForeignResult_NoOwnMembers_GetsProxy()
    {
        var compilation = AssertProxy("""
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            using ZeroAlloc.Results;
            namespace Repro;
            public sealed class MyError { }
            public interface IBase
            {
                ValueTask<Result<int, MyError>> GetAsync(CancellationToken ct);
                ValueTask<Result<int, MyError>> GetFallbackAsync(CancellationToken ct);
            }
            [CircuitBreaker(Fallback = "GetFallbackAsync")]
            public interface IDerived : IBase { }
            """, "Repro.IDerivedResilienceProxy");

        string.Join("\n", compilation.SyntaxTrees.Select(static t => t.ToString()))
            .Should().Contain("((global::Repro.IBase)_inner).GetFallbackAsync(ct)");
    }

    [Fact]
    public async Task NonThrowing_OverInheritedDefaultNonResultMethod_NoOwnMembers_ReportsZR0006()
    {
        var source = """
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IBase
            {
                string Describe() => "d";
            }
            [Retry(MaxAttempts = 2, NonThrowing = true)]
            public interface IDerived : IBase { }
            """;

        AssertProxy(source, "Repro.IDerivedResilienceProxy");
        var diagnostics = await TestHelper.GetDiagnostics<ResilienceGenerator>(source);
        diagnostics.Should().ContainSingle(static d => d.Id == "ZR0006");
    }

    // ── ZR0006 wording ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ZR0006_CircuitBreakerWithoutFallback_ReadsCorrectly()
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
            [CircuitBreaker(MaxFailures = 3)]
            public interface IDerived : IBase
            {
                ValueTask OwnAsync(CancellationToken ct);
            }
            """;

        var diagnostics = await TestHelper.GetDiagnostics<ResilienceGenerator>(source);

        var message = diagnostics.Should().ContainSingle(static d => d.Id == "ZR0006").Subject
            .GetMessage(CultureInfo.InvariantCulture);
        message.Should().Contain("without [CircuitBreaker]").And.Contain("Fallback").And.Contain("MyError");
        message.Should().NotContain("without [CircuitBreaker] without");
        message.Should().Contain("from 'IDerived'", "the policy is interface-level, so the fix is on IDerived");
    }

    [Fact]
    public async Task ZR0006_NonThrowing_ReadsCorrectly()
    {
        var source = """
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IBase
            {
                string Describe() => "d";
            }
            [Retry(MaxAttempts = 2, NonThrowing = true)]
            public interface IDerived : IBase { }
            """;

        var diagnostics = await TestHelper.GetDiagnostics<ResilienceGenerator>(source);

        var message = diagnostics.Should().ContainSingle(static d => d.Id == "ZR0006").Subject
            .GetMessage(CultureInfo.InvariantCulture);
        message.Should().Contain("without [Retry(NonThrowing = true)]").And.Contain("'string'");
        message.Should().NotContain("with NonThrowing = true:");
    }

    [Fact]
    public async Task ZR0006_PolicyFromBaseMethodsOwnAttribute_PointsAtTheBaseMethod()
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
                [RateLimit(MaxPerSecond = 5)]
                ValueTask<Result<int, MyError>> GetAsync(CancellationToken ct) => default;
            }
            [Retry(MaxAttempts = 2)]
            public interface IDerived : IBase
            {
                ValueTask OwnAsync(CancellationToken ct);
            }
            """;

        var diagnostics = await TestHelper.GetDiagnostics<ResilienceGenerator>(source);

        var message = diagnostics.Should().ContainSingle(static d => d.Id == "ZR0006").Subject
            .GetMessage(CultureInfo.InvariantCulture);
        message.Should().Contain("'IBase.GetAsync'");
        message.Should().NotContain("from 'IDerived'").And.NotContain("interface-level");
    }

    // ── Private setters ─────────────────────────────────────────────────────────

    [Fact]
    public void OwnDefaultPropertyWithPrivateSetter_GetsOnlyTheGetter()
    {
        var compilation = AssertProxy("""
            using ZeroAlloc.Resilience;
            namespace Repro;
            [Retry(MaxAttempts = 2)]
            public interface IThing
            {
                string Name { get => "n"; private set { } }
                void Run();
            }
            """, "Repro.IThingResilienceProxy");

        var name = (IPropertySymbol)compilation.GetTypeByMetadataName("Repro.IThingResilienceProxy")!.GetMembers("Name")[0];
        name.SetMethod.Should().BeNull();
        name.GetMethod.Should().NotBeNull();
    }

    [Fact]
    public void InheritedDefaultPropertyWithPrivateSetter_NoOwnMembers_GetsOnlyTheGetter()
    {
        var compilation = AssertProxy("""
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IBase
            {
                string Name { get => "n"; private set { } }
            }
            [Retry(MaxAttempts = 2)]
            public interface IDerived : IBase { }
            """, "Repro.IDerivedResilienceProxy");

        var name = (IPropertySymbol)compilation.GetTypeByMetadataName("Repro.IDerivedResilienceProxy")!.GetMembers("Name")[0];
        name.SetMethod.Should().BeNull();
    }

    // ── Collapsed diamonds route each declaration to its own inner implementation ─

    [Fact]
    public void CollapsedDiamond_EmitsExplicitImplementationForEachOtherDeclaration()
    {
        var compilation = AssertProxy("""
            #nullable enable
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            public sealed class Foo { }
            public sealed class Msg { }
            public interface IHandler<T>
            {
                string Name { get; }
                ValueTask<string> DescribeAsync(CancellationToken ct);
                string Plain(int x);
                event System.EventHandler? Changed;
                string this[int i] { get; }
            }
            [Retry(MaxAttempts = 2)]
            public interface IBoth : IHandler<Foo>, IHandler<Msg> { }
            """, "Repro.IBothResilienceProxy");

        var proxy = compilation.GetTypeByMetadataName("Repro.IBothResilienceProxy")!;
        var explicitTargets = proxy.GetMembers()
            .SelectMany(static m => m switch
            {
                IPropertySymbol p => p.ExplicitInterfaceImplementations.Cast<ISymbol>(),
                IMethodSymbol { MethodKind: MethodKind.ExplicitInterfaceImplementation } mm => mm.ExplicitInterfaceImplementations.Cast<ISymbol>(),
                IEventSymbol e => e.ExplicitInterfaceImplementations.Cast<ISymbol>(),
                _ => Enumerable.Empty<ISymbol>(),
            })
            .Select(static s => s.ContainingType.ToDisplayString() + "." + s.Name)
            .ToList();
        explicitTargets.Should().Contain("Repro.IHandler<Repro.Msg>.Name")
            .And.Contain("Repro.IHandler<Repro.Msg>.DescribeAsync")
            .And.Contain("Repro.IHandler<Repro.Msg>.Plain")
            .And.Contain("Repro.IHandler<Repro.Msg>.Changed")
            .And.Contain("Repro.IHandler<Repro.Msg>.this[]");
    }
}
