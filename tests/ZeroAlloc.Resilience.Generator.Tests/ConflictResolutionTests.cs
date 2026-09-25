using System.Linq;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Resilience.Generator.Tests;

// Declarations of one name that one public member cannot implement are resolved with explicit
// interface implementations: one declaration is the public member, the most-derived one along an
// inheritance path, else the first in AllInterfaces order, and every other declaration gets an
// explicit implementation with its exact signature that forwards through its own interface.
public class ConflictResolutionTests
{
    private static async Task<(Compilation Compilation, INamedTypeSymbol Proxy)> AssertCleanProxy(string source, string proxyName)
    {
        var (compilation, errors) = TestHelper.RunAndCompile(source);
        errors.Should().BeEmpty();

        compilation.GetDiagnostics().Should().NotContain(static d => d.Severity >= DiagnosticSeverity.Warning,
            "the proxy compiles with no compiler diagnostics");
        var generatorDiagnostics = await TestHelper.GetDiagnostics<ResilienceGenerator>(source);
        generatorDiagnostics.Should().NotContain(static d => d.Id.StartsWith("ZR", System.StringComparison.Ordinal),
            "the generator reports nothing");

        var proxy = compilation.GetTypeByMetadataName($"Repro.{proxyName}ResilienceProxy");
        proxy.Should().NotBeNull();
        return (compilation, proxy!);
    }

    private static System.Collections.Generic.List<string> ExplicitTargets(INamedTypeSymbol proxy) =>
        proxy.GetMembers()
            .SelectMany(static m => m switch
            {
                IPropertySymbol p => p.ExplicitInterfaceImplementations.Cast<ISymbol>(),
                IMethodSymbol { MethodKind: MethodKind.ExplicitInterfaceImplementation } mm => mm.ExplicitInterfaceImplementations.Cast<ISymbol>(),
                IEventSymbol e => e.ExplicitInterfaceImplementations.Cast<ISymbol>(),
                _ => Enumerable.Empty<ISymbol>(),
            })
            .Select(static s => s.ContainingType.ToDisplayString() + "." + s.Name)
            .ToList();

    [Fact]
    public async Task EnumerableBase_NoOwnMembers_GenericGetEnumeratorIsPublic_NonGenericIsExplicit()
    {
        var (_, proxy) = await AssertCleanProxy("""
            using System.Collections.Generic;
            using ZeroAlloc.Resilience;
            namespace Repro;
            [Retry(MaxAttempts = 2)]
            public interface IItems : IEnumerable<int> { }
            """, "IItems");

        var publicGetEnumerator = (IMethodSymbol)proxy.GetMembers("GetEnumerator")[0];
        publicGetEnumerator.ReturnType.ToDisplayString().Should().Be("System.Collections.Generic.IEnumerator<int>",
            "IEnumerable<int> hides IEnumerable along the inheritance path, so its declaration is the public one");
        ExplicitTargets(proxy).Should().Contain("System.Collections.IEnumerable.GetEnumerator");
    }

    [Fact]
    public async Task NewHidingAlongPath_Property_NoOwnMembers_Compiles()
    {
        var (_, proxy) = await AssertCleanProxy("""
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IA { object Value { get; } }
            public interface IB : IA { new string Value { get; } }
            [Retry(MaxAttempts = 2)]
            public interface IC : IB { }
            """, "IC");

        ((IPropertySymbol)proxy.GetMembers("Value")[0]).Type.SpecialType.Should().Be(SpecialType.System_String);
        ExplicitTargets(proxy).Should().Contain("Repro.IA.Value");
    }

    [Fact]
    public async Task NewHidingAlongPath_Method_NoOwnMembers_Compiles()
    {
        var (_, proxy) = await AssertCleanProxy("""
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IA { object Get(int id); }
            public interface IB : IA { new string Get(int id); }
            [Retry(MaxAttempts = 2)]
            public interface IC : IB { }
            """, "IC");

        ((IMethodSymbol)proxy.GetMembers("Get")[0]).ReturnType.SpecialType.Should().Be(SpecialType.System_String);
        ExplicitTargets(proxy).Should().Contain("Repro.IA.Get");
    }

    [Fact]
    public async Task ConflictingTypesBetweenUnrelatedBases_Compiles()
    {
        var (_, proxy) = await AssertCleanProxy("""
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IBaseA { string Name { get; } }
            public interface IBaseB { int Name { get; } }
            [Retry(MaxAttempts = 2)]
            public interface IConflicting : IBaseA, IBaseB
            {
                void Get();
            }
            """, "IConflicting");

        ((IPropertySymbol)proxy.GetMembers("Name")[0]).Type.SpecialType.Should().Be(SpecialType.System_String,
            "between unrelated bases the first in AllInterfaces order is public");
        ExplicitTargets(proxy).Should().Contain("Repro.IBaseB.Name");
    }

    [Fact]
    public async Task DifferingNullability_Compiles_WithoutCS8766()
    {
        var (compilation, proxy) = await AssertCleanProxy("""
            #nullable enable
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IBaseA
            {
                string? Name { get; }
                string Find(string? key);
            }
            public interface IBaseB
            {
                string Name { get; }
                string Find(string key);
            }
            [Retry(MaxAttempts = 2)]
            public interface IMixed : IBaseA, IBaseB { }
            """, "IMixed");

        compilation.GetDiagnostics().Should().NotContain(static d => d.Id == "CS8766" || d.Id == "CS8767");
        ExplicitTargets(proxy).Should().Contain("Repro.IBaseB.Name").And.Contain("Repro.IBaseB.Find");
    }

    [Fact]
    public async Task PropertyAndMethodWithSameName_Compiles()
    {
        var (_, proxy) = await AssertCleanProxy("""
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IBaseA { string Name { get; } }
            public interface IBaseB { string Name(); }
            [Retry(MaxAttempts = 2)]
            public interface IMixedKinds : IBaseA, IBaseB { }
            """, "IMixedKinds");

        proxy.GetMembers("Name").Should().ContainSingle().Which.Should().BeAssignableTo<IPropertySymbol>();
        ExplicitTargets(proxy).Should().Contain("Repro.IBaseB.Name");
    }

    [Fact]
    public async Task ConflictingPolicyMethods_EachGetTheirOwnSlot_OwnNumberingUnchanged()
    {
        var (compilation, _) = await AssertCleanProxy("""
            using System.Threading;
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IA
            {
                [Retry(MaxAttempts = 5)]
                object Get(CancellationToken ct);
            }
            public interface IB : IA
            {
                [Retry(MaxAttempts = 4)]
                new string Get(CancellationToken ct);
            }
            public interface IC : IB
            {
                [Retry(MaxAttempts = 3)]
                void Get(int id, CancellationToken ct);
            }
            """, "IC");

        var policies = compilation.GetTypeByMetadataName("Repro.CResiliencePolicies")!;
        policies.GetMembers().OfType<IPropertySymbol>().Select(static p => p.Name)
            .Should().Equal("GetRetry", "Get2Retry", "Get3Retry");
        string.Join("\n", compilation.SyntaxTrees.Select(static t => t.ToString()))
            .Should().Contain("RetryPolicy(3,").And.Contain("RetryPolicy(4,").And.Contain("RetryPolicy(5,");
    }
}
