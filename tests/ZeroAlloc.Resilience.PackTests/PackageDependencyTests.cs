using System;
using System.IO;
using System.Linq;

namespace ZeroAlloc.Resilience.PackTests;

// #197: every dependency the packed nuspec declares is restored and deployed by every consumer,
// so each one must be something a consumer needs: either the runtime assembly references it, or
// the code the generator emits into the consumer does. The tests read the nuspec of a real pack,
// so a dependency that reaches the package through an imported .props file or a ProjectReference
// counts too, not only the PackageReferences written in the csproj.
public class PackageDependencyTests : IClassFixture<PackedPackage>
{
    // The generated proxies use these; the runtime assembly itself does not.
    private static readonly string[] GeneratedCodeDependencies =
    [
        // The Result return types the generated proxies build failures of.
        "ZeroAlloc.Results",
    ];

    private readonly PackedPackage _package;

    public PackageDependencyTests(PackedPackage package) => _package = package;

    [Fact]
    public void PackageProject_IsFound() =>
        File.Exists(PackedPackage.PackageProjectPath()).Should().BeTrue();

    [Fact]
    public void Nuspec_HasADependencyGroupPerTargetFramework() =>
        _package.DependencyGroupFrameworks().Should().Equal("net10.0", "net11.0");

    [Fact]
    public void EveryShippedDependency_IsUsedByTheRuntimeOrTheGeneratedCode()
    {
        var runtimeReferences = typeof(RetryPolicy).Assembly.GetReferencedAssemblies()
            .Select(static name => name.Name!)
            .ToHashSet(StringComparer.Ordinal);

        var unused = _package.DependencyIds()
            .Where(id => !runtimeReferences.Contains(id) && !GeneratedCodeDependencies.Contains(id, StringComparer.Ordinal))
            .ToArray();

        unused.Should().BeEmpty("a package dependency nothing uses is restored and deployed by every consumer for nothing, and these are unused: {0}", string.Join(", ", unused));
    }

    // #205: the generated Add{Service}Resilience extensions are emitted only when the consumer
    // references IServiceCollection itself, so the package must not bring the abstractions into
    // every consumer's dependency graph, including those that construct the proxies by hand.
    [Fact]
    public void DependencyInjectionAbstractions_IsNotAShippedDependency() =>
        _package.DependencyIds().Should().NotContain(
            static id => id.StartsWith("Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal));

    [Fact]
    public void RuntimeAssemblyReferences_AreAllShippedDependencies()
    {
        var shipped = _package.DependencyIds().ToHashSet(StringComparer.Ordinal);

        var zeroAllocReferences = typeof(RetryPolicy).Assembly.GetReferencedAssemblies()
            .Select(static name => name.Name!)
            .Where(static name => name.StartsWith("ZeroAlloc.", StringComparison.Ordinal))
            .ToArray();

        zeroAllocReferences.Should().NotBeEmpty();
        zeroAllocReferences.Should().OnlyContain(name => shipped.Contains(name), "a package the runtime assembly loads must be a declared dependency");
    }
}
