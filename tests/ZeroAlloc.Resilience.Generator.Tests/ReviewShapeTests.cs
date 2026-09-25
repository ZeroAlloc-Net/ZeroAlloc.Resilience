using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Resilience.Generator.Tests;

// Shapes from the second 3.0 review, by the reviewer's shape id. Each asserts its intended
// outcome: a valid proxy with no compiler diagnostics, or a specific ZR diagnostic.
public class ReviewShapeTests
{
    private static async Task<INamedTypeSymbol> AssertCleanProxy(string source, string proxyName, string? allowedZr = null)
    {
        var (compilation, errors) = TestHelper.RunAndCompile(source);
        errors.Should().BeEmpty();
        compilation.GetDiagnostics().Should().NotContain(static d => d.Severity >= DiagnosticSeverity.Warning,
            "the proxy compiles with no compiler diagnostics");
        var generatorDiagnostics = await TestHelper.GetDiagnostics<ResilienceGenerator>(source);
        generatorDiagnostics.Should().NotContain(d => d.Id.StartsWith("ZR", System.StringComparison.Ordinal)
            && !string.Equals(d.Id, allowedZr, System.StringComparison.Ordinal));
        if (allowedZr is not null)
            generatorDiagnostics.Should().ContainSingle(d => d.Id == allowedZr);

        var proxy = compilation.GetTypeByMetadataName($"N.{proxyName}ResilienceProxy");
        proxy.Should().NotBeNull();
        return proxy!;
    }

    private static void AssertOnlyZR(string source, string id, string fragment)
    {
        var (compilation, errors) = TestHelper.RunAndCompile(source);
        errors.Should().ContainSingle(d => d.Id == id)
            .Which.GetMessage(CultureInfo.InvariantCulture).Should().Contain(fragment);
        errors.Should().OnlyContain(d => d.Id == id);
        compilation.GetTypeByMetadataName("N.ICResilienceProxy").Should().BeNull();
    }

    // ── C1 and I5: async methods with ref-like parameters ────────────────────────

    [Fact]
    public async Task S39_InheritedDefaultAsyncSpanParameter_UnderPolicy_ZR0006_AndForwarded()
    {
        await AssertCleanProxy("""
            using System;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IBase { ValueTask<int> Parse(ReadOnlySpan<char> s) => new ValueTask<int>(s.Length); }
            [Retry(MaxAttempts = 2)]
            public interface IC : IBase { void Own(); }
            """, "IC", allowedZr: "ZR0006");
    }

    [Fact]
    public async Task S40_InheritedDefaultAsyncSpanParameter_NoPolicyApplies_Forwarded()
    {
        await AssertCleanProxy("""
            using System;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IBase { ValueTask<int> Parse(ReadOnlySpan<char> s) => new ValueTask<int>(s.Length); }
            public interface IC : IBase
            {
                [Retry(MaxAttempts = 2)]
                void Own();
            }
            """, "IC");
    }

    [Fact]
    public async Task U04_InheritedDefaultAsyncSpanParameter_OnlyATimeoutOnOwnMethod_Forwarded()
    {
        await AssertCleanProxy("""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IBase { ValueTask<int> Parse(ReadOnlySpan<byte> s) => new ValueTask<int>(s.Length); }
            public interface IC : IBase
            {
                [Timeout(Ms = 100)]
                ValueTask OwnAsync(CancellationToken ct);
            }
            """, "IC");
    }

    [Fact]
    public void T03_InheritedAbstractAsyncSpanParameter_UnderPolicy_ZR0007()
    {
        AssertOnlyZR("""
            using System;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IBase { ValueTask<int> Parse(ReadOnlySpan<char> s); }
            [Retry(MaxAttempts = 2)]
            public interface IC : IBase { }
            """, "ZR0007", "Parse");
    }

    [Fact]
    public void T02b_OwnAsyncSpanParameter_UnderPolicy_ZR0007()
    {
        AssertOnlyZR("""
            using System;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace N;
            [Retry(MaxAttempts = 2)]
            public interface IC { ValueTask<int> Parse(ReadOnlySpan<char> s); }
            """, "ZR0007", "Parse");
    }

    [Fact]
    public async Task T02_OwnAsyncSpanParameter_Passthrough_Forwarded()
    {
        await AssertCleanProxy("""
            using System;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IC
            {
                ValueTask<int> Parse(ReadOnlySpan<char> s);
                [Retry(MaxAttempts = 2)]
                void Own();
            }
            """, "IC");
    }

    [Fact]
    public async Task U05_InheritedDefaultSyncSpanParameter_UnderPolicy_Wrapped()
    {
        await AssertCleanProxy("""
            using System;
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IBase { int Parse(ReadOnlySpan<char> s) => s.Length; }
            [Retry(MaxAttempts = 2)]
            public interface IC : IBase { }
            """, "IC");
    }

    // ── I1: generic methods differing only in type-parameter names ─────────────

    [Fact]
    public async Task S36_GenericDiamond_DifferentTypeParameterNames_Merge()
    {
        var proxy = await AssertCleanProxy("""
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IA { T Foo<T>(T value); }
            public interface IB { U Foo<U>(U value); }
            [Retry(MaxAttempts = 2)]
            public interface IC : IA, IB { }
            """, "IC");
        proxy.GetMembers("Foo").Should().ContainSingle();
    }

    [Fact]
    public async Task T01_OwnGenericOverInheritedDefaultGeneric_DifferentTypeParameterNames_Merge()
    {
        var proxy = await AssertCleanProxy("""
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IBase { U Foo<U>(U value) => value; }
            [Retry(MaxAttempts = 2)]
            public interface IC : IBase { new T Foo<T>(T value); }
            """, "IC");
        proxy.GetMembers("Foo").Should().ContainSingle();
    }

    // ── I2: explicit implementations of generic methods with T? ────────────────

    [Fact]
    public async Task T13_ExplicitGenericWithClassConstrainedNullableT_Compiles()
    {
        await AssertCleanProxy("""
            #nullable enable
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IA { Task<int> Find<T>(); }
            public interface IB { Task<T?> Find<T>() where T : class; }
            [Retry(MaxAttempts = 2)]
            public interface IC : IA, IB { }
            """, "IC");
    }

    [Fact]
    public async Task S42_ExplicitGenericWithUnconstrainedNullableT_Compiles()
    {
        await AssertCleanProxy("""
            #nullable enable
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IA { Task<int> Find<T>(); }
            public interface IB { Task<T?> Find<T>(); }
            [Retry(MaxAttempts = 2)]
            public interface IC : IA, IB { }
            """, "IC");
    }

    [Fact]
    public async Task ExplicitGenericWithOtherConstraints_RepeatsNone_Compiles()
    {
        await AssertCleanProxy("""
            #nullable enable
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IA { int Make<T>() where T : new(); }
            public interface IB { string Make<T>() where T : struct, System.IComparable<T>; }
            [Retry(MaxAttempts = 2)]
            public interface IC : IA, IB { }
            """, "IC");
    }

    // ── I3: sealed members are not forwarded ───────────────────────────────────

    [Fact]
    public async Task T07_SealedMemberConflictingWithAbstract_SealedSkipped()
    {
        var proxy = await AssertCleanProxy("""
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IA { string Name(); }
            public interface IB { sealed int Name() => 1; }
            [Retry(MaxAttempts = 2)]
            public interface IC : IA, IB { }
            """, "IC");
        proxy.GetMembers("Name").Should().ContainSingle();
    }

    [Theory]
    [InlineData("IA, IB")]
    [InlineData("IB, IA")]
    public async Task T08_T09_SealedMemberCollapsed_EitherOrder_SealedSkipped(string bases)
    {
        await AssertCleanProxy($$"""
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IA { sealed string X() => "a"; }
            public interface IB { string X(); }
            [Retry(MaxAttempts = 2)]
            public interface IC : {{bases}} { }
            """, "IC");
    }

    [Fact]
    public async Task T10_InheritedSealedMember_NotForwarded()
    {
        var proxy = await AssertCleanProxy("""
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IBase
            {
                [Retry(MaxAttempts = 3)]
                sealed string Describe() => "d";
                string Name { get; }
            }
            [Retry(MaxAttempts = 2)]
            public interface IC : IBase { }
            """, "IC", allowedZr: "ZR0006");
        proxy.GetMembers("Describe").Should().BeEmpty();
    }

    // ── I4: ref returns ─────────────────────────────────────────────────────────

    [Fact]
    public void U01_InheritedAbstractRefReturnMethod_UnderPolicy_ZR0007()
    {
        AssertOnlyZR("""
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IBase { ref int Slot(); }
            [Retry(MaxAttempts = 2)]
            public interface IC : IBase { }
            """, "ZR0007", "Slot");
    }

    [Fact]
    public async Task U01b_InheritedAbstractRefReturnMethod_NoPolicy_ForwardedByRef()
    {
        var proxy = await AssertCleanProxy("""
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IBase { ref int Slot(); }
            public interface IC : IBase
            {
                [Retry(MaxAttempts = 2)]
                void Own();
            }
            """, "IC");
        ((IMethodSymbol)proxy.GetMembers("Slot")[0]).ReturnsByRef.Should().BeTrue();
    }

    [Fact]
    public async Task U02_InheritedAbstractRefReadonlyProperty_ForwardedByRef()
    {
        var proxy = await AssertCleanProxy("""
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IBase { ref readonly int Slot { get; } }
            [Retry(MaxAttempts = 2)]
            public interface IC : IBase { }
            """, "IC");
        ((IPropertySymbol)proxy.GetMembers("Slot")[0]).ReturnsByRefReadonly.Should().BeTrue();
    }

    [Fact]
    public async Task U03_S37_InheritedDefaultRefReturnMethod_UnderPolicy_ZR0006_ForwardedByRef()
    {
        var proxy = await AssertCleanProxy("""
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IBase
            {
                private static int _slot;
                ref int Slot() => ref _slot;
            }
            [Retry(MaxAttempts = 2)]
            public interface IC : IBase { }
            """, "IC", allowedZr: "ZR0006");
        ((IMethodSymbol)proxy.GetMembers("Slot")[0]).ReturnsByRef.Should().BeTrue();
    }

    [Fact]
    public async Task S38_InheritedDefaultRefReturnProperty_ForwardedByRef()
    {
        var proxy = await AssertCleanProxy("""
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IBase
            {
                private static int _slot;
                ref int Slot => ref _slot;
            }
            [Retry(MaxAttempts = 2)]
            public interface IC : IBase { }
            """, "IC");
        ((IPropertySymbol)proxy.GetMembers("Slot")[0]).ReturnsByRef.Should().BeTrue();
    }

    [Fact]
    public void OwnRefReturnMethod_UnderPolicy_ZR0007()
    {
        AssertOnlyZR("""
            using ZeroAlloc.Resilience;
            namespace N;
            [Retry(MaxAttempts = 2)]
            public interface IC { ref int Slot(); }
            """, "ZR0007", "Slot");
    }

    // ── I6: a property named Item and an indexer ──────────────────────────────

    [Fact]
    public async Task T04_PropertyNamedItemAndIndexer_OneBecomesExplicit()
    {
        await AssertCleanProxy("""
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IA { string Item { get; } }
            public interface IB { string this[int index] { get; } }
            [Retry(MaxAttempts = 2)]
            public interface IC : IA, IB { }
            """, "IC");
    }

    // ── object members redeclared in an interface ──────────────────────────────
    // ToString(), Equals(object) and GetHashCode() with object's signatures are implemented by
    // object's own members, as in 2.0.1: the proxy emits nothing for them.

    [Fact]
    public async Task S45_InheritedToString_NotEmitted_NoWarning()
    {
        var proxy = await AssertCleanProxy("""
            #nullable enable
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IBase { string? ToString(); }
            [Retry(MaxAttempts = 2)]
            public interface IC : IBase { void Own(); }
            """, "IC");
        proxy.GetMembers("ToString").Should().BeEmpty();
    }

    [Fact]
    public async Task T11_OwnAndInheritedEqualsAndGetHashCode_NotEmitted_NoWarning()
    {
        var proxy = await AssertCleanProxy("""
            #nullable enable
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IBase { int GetHashCode(); }
            [Retry(MaxAttempts = 2)]
            public interface IC : IBase { bool Equals(object? o); }
            """, "IC", allowedZr: "ZR0006");
        proxy.GetMembers("Equals").Should().BeEmpty();
        proxy.GetMembers("GetHashCode").Should().BeEmpty();
    }

    [Fact]
    public async Task AllThreeObjectMembersRedeclared_NotEmitted_NoWarning()
    {
        var proxy = await AssertCleanProxy("""
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IBase
            {
                string ToString();
                bool Equals(object o);
                int GetHashCode();
            }
            [Retry(MaxAttempts = 2)]
            public interface IC : IBase { void Own(); }
            """, "IC");
        proxy.GetMembers("ToString").Should().BeEmpty();
        proxy.GetMembers("Equals").Should().BeEmpty();
        proxy.GetMembers("GetHashCode").Should().BeEmpty();
    }

    // ── ZR0006 for policies on own methods the proxy does not implement ────────

    [Fact]
    public async Task V08_OwnToStringWithRetry_ReportsZR0006()
    {
        var source = """
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IC
            {
                [Retry(MaxAttempts = 3)]
                string ToString();
                void Run();
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);
        var diagnostics = await TestHelper.GetDiagnostics<ResilienceGenerator>(source);

        errors.Should().BeEmpty();
        var zr0006 = diagnostics.Should().ContainSingle(static d => d.Id == "ZR0006").Subject;
        zr0006.Severity.Should().Be(DiagnosticSeverity.Warning);
        zr0006.GetMessage(CultureInfo.InvariantCulture).Should().Contain("'ToString'").And.Contain("[Retry]").And.Contain("object");
    }

    [Fact]
    public async Task OwnToString_UnderInterfaceLevelRetry_ReportsZR0006()
    {
        var source = """
            #nullable enable
            using ZeroAlloc.Resilience;
            namespace N;
            [Retry(MaxAttempts = 3)]
            public interface IC
            {
                string? ToString();
                void Run();
            }
            """;

        var diagnostics = await TestHelper.GetDiagnostics<ResilienceGenerator>(source);

        diagnostics.Should().ContainSingle(static d => d.Id == "ZR0006")
            .Which.GetMessage(CultureInfo.InvariantCulture).Should().Contain("'ToString'").And.Contain("[Retry]");
    }

    [Fact]
    public async Task OwnSealedMethodWithRetry_ReportsZR0006()
    {
        var source = """
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IC
            {
                [Retry(MaxAttempts = 3)]
                sealed string Describe() => "d";
                [Retry(MaxAttempts = 2)]
                void Run();
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);
        var diagnostics = await TestHelper.GetDiagnostics<ResilienceGenerator>(source);

        errors.Should().BeEmpty();
        diagnostics.Should().ContainSingle(static d => d.Id == "ZR0006")
            .Which.GetMessage(CultureInfo.InvariantCulture).Should().Contain("'Describe'").And.Contain("sealed");
    }

    [Fact]
    public async Task OwnSealedMethodWithoutPolicy_UnderMethodLevelPoliciesOnly_NoZR0006()
    {
        await AssertCleanProxy("""
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IC
            {
                sealed string Describe() => "d";
                [Retry(MaxAttempts = 2)]
                void Run();
            }
            """, "IC");
    }

    // ── V09: members hiding an object member by name and parameters ────────────

    [Theory]
    [InlineData("public interface IA { string ToString(); } public interface IB { object ToString(); }")]
    [InlineData("public interface IA { int GetType(); } public interface IB { }")]
    [InlineData("public interface IA { string Equals(object o); } public interface IB { }")]
    [InlineData("public interface IA { long GetHashCode(); } public interface IB { }")]
    [InlineData("public interface IA { bool Equals(object a, object b); } public interface IB { }")]
    public async Task V09_MemberHidingAnObjectMember_EmittedWithNew_NoWarning(string bases)
    {
        await AssertCleanProxy($$"""
            using ZeroAlloc.Resilience;
            namespace N;
            {{bases}}
            [Retry(MaxAttempts = 2)]
            public interface IC : IA, IB { }
            """, "IC");
    }

    // ── V10: skipped own methods keep the slot numbering of their overloads ────

    [Fact]
    public void V10_OwnSealedOverloadDeclaredFirst_OtherOverloadKeepsGet2Retry()
    {
        var (compilation, errors) = TestHelper.RunAndCompile("""
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IC
            {
                [Retry(MaxAttempts = 7)]
                sealed Task<int> Get(int id) => Task.FromResult(id);

                [Retry(MaxAttempts = 3)]
                Task<int> Get(string key, CancellationToken ct);
            }
            """);

        errors.Should().BeEmpty();
        var policies = compilation.GetTypeByMetadataName("N.CResiliencePolicies")!;
        policies.GetMembers().OfType<IPropertySymbol>().Select(static p => p.Name).Should().Equal("Get2Retry");
        string.Join("\n", compilation.SyntaxTrees.Select(static t => t.ToString()))
            .Should().Contain("Get2Retry { get; set; } = new global::ZeroAlloc.Resilience.RetryPolicy(3,");
    }

    [Fact]
    public void OwnObjectMemberOverloadDeclaredFirst_OtherOverloadKeepsItsSlotNumber()
    {
        var (compilation, errors) = TestHelper.RunAndCompile("""
            using ZeroAlloc.Resilience;
            namespace N;
            public interface IC
            {
                [Retry(MaxAttempts = 7)]
                string ToString();

                [Retry(MaxAttempts = 3)]
                string ToString(string format);
            }
            """);

        errors.Should().BeEmpty();
        var policies = compilation.GetTypeByMetadataName("N.CResiliencePolicies")!;
        policies.GetMembers().OfType<IPropertySymbol>().Select(static p => p.Name).Should().Equal("ToString2Retry");
    }
}
