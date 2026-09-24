using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using ZeroAlloc.TestHelpers;

namespace ZeroAlloc.Resilience.Generator.Tests;

internal static class TestHelper
{
    private static readonly CSharpParseOptions ParseOptions =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

    public static void Verify<TGenerator>(string source)
        where TGenerator : IIncrementalGenerator, new()
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .Append(MetadataReference.CreateFromFile(typeof(RetryAttribute).Assembly.Location))
            .ToList();

        var compilation = CSharpCompilation.Create(
            "Tests",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new TGenerator();
        var driver = CSharpGeneratorDriver
            .Create(generator)
            .WithUpdatedParseOptions(ParseOptions)
            .RunGenerators(compilation);

        GeneratorSnapshot.Verify(driver);
    }

    public static Task<IReadOnlyList<Diagnostic>> GetDiagnostics<TGenerator>(string source)
        where TGenerator : IIncrementalGenerator, new()
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .Append(MetadataReference.CreateFromFile(typeof(RetryAttribute).Assembly.Location))
            .ToList();

        var compilation = CSharpCompilation.Create(
            "Tests",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new TGenerator();
        var driver = CSharpGeneratorDriver.Create(generator)
            .WithUpdatedParseOptions(ParseOptions)
            .RunGenerators(compilation);

        var result = driver.GetRunResult();
        var updated = compilation.AddSyntaxTrees(result.GeneratedTrees);
        var diags = result.Diagnostics
            .Concat(updated.GetDiagnostics())
            .ToList();

        return Task.FromResult<IReadOnlyList<Diagnostic>>(diags);
    }

    // Runtime framework references only, instead of every assembly loaded in the test domain:
    // anything the test host happens to load could otherwise hide a missing reference.
    private static readonly MetadataReference[] CompileReferences =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(static p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal)
                            || string.Equals(Path.GetFileName(p), "mscorlib.dll", StringComparison.Ordinal)
                            || string.Equals(Path.GetFileName(p), "netstandard.dll", StringComparison.Ordinal))
            .Select(static p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(RetryAttribute).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(ZeroAlloc.Results.Result).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.Location))
            .ToArray();

    /// <summary>
    /// Runs the generator and compiles its output together with <paramref name="source"/>.
    /// Returns the updated compilation and every error, from the generator or the compiler.
    /// </summary>
    public static (Compilation Compilation, ImmutableArray<Diagnostic> Errors) RunAndCompile(string source)
    {
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { CSharpSyntaxTree.ParseText(source, ParseOptions) },
            CompileReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        CSharpGeneratorDriver
            .Create(new ResilienceGenerator())
            .WithUpdatedParseOptions(ParseOptions)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);

        var errors = generatorDiagnostics
            .Concat(output.GetDiagnostics())
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();
        return (output, errors);
    }
}
