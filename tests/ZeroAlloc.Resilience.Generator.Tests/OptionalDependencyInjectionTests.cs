using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ZeroAlloc.Resilience.Generator.Tests;

// #200: the DI extensions and their `using` are emitted only when the compilation references
// Microsoft.Extensions.DependencyInjection.Abstractions, so a consumer that builds the proxy
// by hand compiles without it.
public class OptionalDependencyInjectionTests
{
    private const string Source = """
        using System.Threading;
        using System.Threading.Tasks;
        using ZeroAlloc.Resilience;
        namespace Repro;
        [Retry(MaxAttempts = 3, BackoffMs = 100)]
        internal interface IJevApi
        {
            ValueTask<string> GetAsync(string id, CancellationToken ct);
        }

        internal sealed class JevApi : IJevApi
        {
            public ValueTask<string> GetAsync(string id, CancellationToken ct) => new(id);
        }

        internal static class Composition
        {
            public static IJevApi Create() =>
                new IJevApiResilienceProxy(new JevApi(), new JevApiResiliencePolicies());
        }
        """;

    private const string DependencyInjectionAssembly = "Microsoft.Extensions.DependencyInjection.Abstractions";

    [Fact]
    public void WithoutDependencyInjection_Compiles_AndEmitsNoDiCode()
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Source, referenceDependencyInjection: false);

        errors.Should().BeEmpty();
        compilation.ReferencedAssemblyNames.Select(static a => a.Name)
            .Should().NotContain(DependencyInjectionAssembly);

        var generated = compilation.SyntaxTrees
            .Where(static t => t.FilePath.EndsWith(".Resilience.g.cs", StringComparison.Ordinal))
            .ToList();
        generated.Should().ContainSingle();
        generated[0].ToString().Should().NotContain("Microsoft.Extensions.DependencyInjection");

        compilation.GetSymbolsWithName(static n => n.EndsWith("ServiceCollectionExtensions", StringComparison.Ordinal), SymbolFilter.Type)
            .Should().BeEmpty();
        compilation.GetTypeByMetadataName("Repro.JevApiResiliencePolicies").Should().NotBeNull();
        compilation.GetTypeByMetadataName("Repro.IJevApiResilienceProxy").Should().NotBeNull();
    }

    [Fact]
    public void WithDependencyInjection_EmitsDiExtensions()
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Source);

        errors.Should().BeEmpty();
        compilation.GetTypeByMetadataName("Repro.InternalResilienceServiceCollectionExtensions")!
            .GetMembers().Select(static m => m.Name)
            .Should().Contain(new[] { "AddJevApiResiliencePolicies", "AddJevApiResilience" });
    }

    // The detection is keyed on the references: a source edit leaves it cached, and only a
    // change to the references runs it again.
    [Fact]
    public void Detection_IsCachedAcrossSourceEdits_AndRerunsOnReferenceChange()
    {
        var compilation = TestHelper.CreateCompilation(Source, referenceDependencyInjection: true);
        var parseOptions = (CSharpParseOptions)compilation.SyntaxTrees[0].Options;
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new ResilienceGenerator().AsSourceGenerator() },
            parseOptions: parseOptions,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation);
        DetectionOutputs(driver).Should().Equal(true);

        var edited = compilation.ReplaceSyntaxTree(
            compilation.SyntaxTrees[0],
            CSharpSyntaxTree.ParseText(Source + "\n// edit\n", parseOptions));
        driver = driver.RunGenerators(edited);
        DetectionReasons(driver).Should().Equal(IncrementalStepRunReason.Cached);

        var withoutDi = edited.RemoveReferences(edited.References.Where(static r =>
            r.Display!.EndsWith(DependencyInjectionAssembly + ".dll", StringComparison.OrdinalIgnoreCase)));
        driver = driver.RunGenerators(withoutDi);
        DetectionReasons(driver).Should().Equal(IncrementalStepRunReason.Modified);
        DetectionOutputs(driver).Should().Equal(false);
    }

    private static IncrementalStepOutput[] Detection(GeneratorDriver driver) =>
        driver.GetRunResult().Results[0].TrackedSteps[ResilienceGenerator.DependencyInjectionTrackingName]
            .SelectMany(static s => s.Outputs)
            .Select(static o => new IncrementalStepOutput(o.Value, o.Reason))
            .ToArray();

    private static IncrementalStepRunReason[] DetectionReasons(GeneratorDriver driver) =>
        Detection(driver).Select(static o => o.Reason).ToArray();

    private static object[] DetectionOutputs(GeneratorDriver driver) =>
        Detection(driver).Select(static o => o.Value).ToArray();

    private sealed record IncrementalStepOutput(object Value, IncrementalStepRunReason Reason);
}
