using System.Linq;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Resilience.Generator.Tests;

// #173: generic methods need their type parameters and constraints, and ref, out, in and params
// parameters need their modifiers, in the parameter list and in every argument list.
public class GenericAndByRefMethodTests
{
    private const string Members = """
            T Get<T>(string key) where T : class, new();
            TOut Map<TIn, TOut>(TIn value) where TIn : struct where TOut : notnull;
            T? Find<T>(string key) where T : class;
            bool TryGet(string key, out int value);
            void Bump(ref int counter);
            int Measure(in long value);
            int Sum(params int[] values);
            ValueTask<T> LoadAsync<T>(string key, CancellationToken ct) where T : class;
        """;

    private static string Source(string layout) => layout switch
    {
        "own-policy" => $$"""
            #nullable enable
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            [Retry(MaxAttempts = 2)]
            public interface IOps
            {
            {{Members}}
            }
            """,
        "own-no-policy" => $$"""
            #nullable enable
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IOps
            {
            {{Members}}
                [Retry(MaxAttempts = 2)]
                void Ping();
            }
            """,
        "inherited-policy" => $$"""
            #nullable enable
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IBase
            {
            {{Members}}
            }
            [Retry(MaxAttempts = 2)]
            public interface IOps : IBase { }
            """,
        _ => $$"""
            #nullable enable
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IBase
            {
            {{Members}}
            }
            public interface IOps : IBase
            {
                [Retry(MaxAttempts = 2)]
                void Ping();
            }
            """,
    };

    [Theory]
    [InlineData("own-policy")]
    [InlineData("own-no-policy")]
    [InlineData("inherited-policy")]
    [InlineData("inherited-no-policy")]
    public void GenericAndByRefMethods_Compile(string layout)
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Source(layout));

        errors.Should().BeEmpty();
        compilation.GetDiagnostics().Should().NotContain(static d => d.Id.StartsWith("CS86", System.StringComparison.Ordinal)
            || d.Id.StartsWith("CS87", System.StringComparison.Ordinal), "no nullability mismatch");
        var proxy = compilation.GetTypeByMetadataName("Repro.IOpsResilienceProxy");
        proxy.Should().NotBeNull();
        var get = (IMethodSymbol)proxy!.GetMembers("Get")[0];
        get.TypeParameters.Should().ContainSingle();
        var tryGet = (IMethodSymbol)proxy.GetMembers("TryGet")[0];
        tryGet.Parameters[1].RefKind.Should().Be(RefKind.Out);
    }

    [Fact]
    public void OutParameter_UnderRetry_IsDefaultAssignedBeforeTheLoop()
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Source("own-policy"));

        errors.Should().BeEmpty();
        var generated = string.Join("\n", compilation.SyntaxTrees.Select(static t => t.ToString()));
        generated.Should().Contain("value = default!;");
        generated.Should().Contain("_inner.TryGet(key, out value)");
        generated.Should().Contain("_inner.Bump(ref counter)");
        generated.Should().Contain("_inner.Measure(in value)");
        generated.Should().Contain("_inner.Get<T>(key)");
    }

    [Fact]
    public void InheritedDefaultMethod_WithOutParameter_Compiles()
    {
        var source = """
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IBase
            {
                bool TryGet(string key, out int value)
                {
                    value = 0;
                    return false;
                }
            }
            [Retry(MaxAttempts = 2)]
            public interface IDerived : IBase { }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        compilation.GetTypeByMetadataName("Repro.IDerivedResilienceProxy").Should().NotBeNull();
    }

    [Fact]
    public void InheritedDefaultGenericMethod_Compiles()
    {
        var source = """
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IBase
            {
                T Echo<T>(T value) where T : notnull => value;
            }
            [Retry(MaxAttempts = 2)]
            public interface IDerived : IBase { }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        compilation.GetTypeByMetadataName("Repro.IDerivedResilienceProxy").Should().NotBeNull();
    }

    [Fact]
    public async Task InheritedDefaultAsyncMethodWithRefParameter_UnderPolicy_ReportsZR0006_AndCompiles()
    {
        var source = """
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IBase
            {
                ValueTask FillAsync(ref int value) => default;
            }
            [Retry(MaxAttempts = 2)]
            public interface IDerived : IBase
            {
                void Run();
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);
        var diagnostics = await TestHelper.GetDiagnostics<ResilienceGenerator>(source);

        errors.Should().BeEmpty();
        diagnostics.Should().ContainSingle(static d => d.Id == "ZR0006")
            .Which.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Should().Contain("FillAsync");
    }
}
