using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ZeroAlloc.Resilience.Generator.Tests;

// Hint names are unique within the compilation: the namespace, then the containing types and the
// interface joined by '+', each with its generic arity. Before, the hint name was the namespace
// and the interface name joined by '_', so namespace A_B with IC and namespace A with B_IC, or
// two interfaces of the same name nested in different types, made AddSource throw and the
// generator produced nothing at all (#208).
public class HintNameTests
{
    private static string[] HintNames(string source)
    {
        var compilation = TestHelper.CreateCompilation(source, referenceDependencyInjection: true);
        var result = CSharpGeneratorDriver
            .Create(new ResilienceGenerator())
            .WithUpdatedParseOptions(CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest))
            .RunGenerators(compilation)
            .GetRunResult()
            .Results[0];

        result.Exception.Should().BeNull();
        return result.GeneratedSources.Select(static s => s.HintName).OrderBy(static h => h, StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public void UnderscoreInNamespaceAndInterfaceName_BothGenerateAndCompile()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace A_B
            {
                [Retry(MaxAttempts = 2)]
                public interface IC { ValueTask<int> GetAsync(CancellationToken ct); }
            }
            namespace A
            {
                [Retry(MaxAttempts = 2)]
                public interface B_IC { ValueTask<int> GetAsync(CancellationToken ct); }
            }
            """;

        HintNames(source).Should().Equal("A.B_IC.Resilience.g.cs", "A_B.IC.Resilience.g.cs");

        var (compilation, errors) = TestHelper.RunAndCompile(source);
        errors.Should().BeEmpty();
        compilation.GetTypeByMetadataName("A_B.ICResilienceProxy").Should().NotBeNull();
        compilation.GetTypeByMetadataName("A.B_ICResilienceProxy").Should().NotBeNull();
    }

    // Only the file names are asserted: the generated policies and proxy classes of the two
    // interfaces still share a name and do not compile together, which is #209.
    [Fact]
    public void SameNamedNestedInterfaces_EachGetTheirOwnFile()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace N;
            public class First
            {
                [Retry(MaxAttempts = 2)]
                public interface IFoo { ValueTask<int> GetAsync(CancellationToken ct); }
            }
            public class Second
            {
                [Retry(MaxAttempts = 2)]
                public interface IFoo { ValueTask<int> GetAsync(CancellationToken ct); }
            }
            """;

        HintNames(source).Should().Equal("N.First+IFoo.Resilience.g.cs", "N.Second+IFoo.Resilience.g.cs");
    }

    [Fact]
    public void NestedInterface_AndTopLevelInterfaceInNamespaceOfTheSameName_DoNotCollide()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace N
            {
                public class Outer
                {
                    [Retry(MaxAttempts = 2)]
                    public interface IFoo { ValueTask<int> GetAsync(CancellationToken ct); }
                }
            }
            namespace N.Outer
            {
                [Retry(MaxAttempts = 2)]
                public interface IFoo { ValueTask<int> GetAsync(CancellationToken ct); }
            }
            """;

        HintNames(source).Should().Equal("N.Outer+IFoo.Resilience.g.cs", "N.Outer.IFoo.Resilience.g.cs");
    }

    [Fact]
    public void GlobalNamespace_HasNoNamespacePart()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            [Retry(MaxAttempts = 2)]
            public interface IFoo { ValueTask<int> GetAsync(CancellationToken ct); }
            """;

        HintNames(source).Should().Equal("IFoo.Resilience.g.cs");
    }

    [Theory]
    [InlineData("App.Outer`1+IFoo", "App.Outer`1+IFoo")]
    [InlineData("Café", "Café")]
    [InlineData("a<b>", "a-u003Cb-u003E")]
    [InlineData("a/b", "a-u002Fb")]
    [InlineData("𐐀", "𐐀")]
    public void Sanitize_KeepsIdentifierCharacters_AndEscapesTheRest(string name, string expected) =>
        ZeroAlloc.Resilience.Generator.HintNames.Sanitize(name).Should().Be(expected);

    // A lone surrogate cannot round-trip through InlineData, so it is built here.
    [Fact]
    public void Sanitize_EscapesALoneSurrogate() =>
        ZeroAlloc.Resilience.Generator.HintNames.Sanitize(new string(new[] { 'x', (char)0xD800, 'y' })).Should().Be("x-uD800y");
}
