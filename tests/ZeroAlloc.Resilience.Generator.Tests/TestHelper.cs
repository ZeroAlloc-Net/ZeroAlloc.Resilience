using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ZeroAlloc.Resilience.Generator.Tests;

internal static class TestHelper
{
    private static readonly CSharpParseOptions ParseOptions =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

    public static Task Verify<TGenerator>(string source)
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

        return VerifyXunit.Verifier.Verify(driver).UseDirectory("Snapshots");
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
}
