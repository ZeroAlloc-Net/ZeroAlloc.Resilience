using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace ZeroAlloc.Resilience.PackTests;

// #197: every PackageReference of the package project that is not PrivateAssets="all" becomes a
// dependency in the packed nuspec, so each one must be something a consumer needs: either the
// runtime assembly references it, or the code the generator emits into the consumer does.
public class PackageDependencyTests
{
    // The generated proxies and DI extensions use these; the runtime assembly itself does not.
    private static readonly string[] GeneratedCodeDependencies =
    [
        // The generated Add{Service}Resilience extensions.
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        // The Result return types the generated proxies build failures of.
        "ZeroAlloc.Results",
    ];

    private static string PackageProjectPath() =>
        typeof(PackageDependencyTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(static a => string.Equals(a.Key, "PackageProject", StringComparison.Ordinal))
            .Value!;

    // The PackageReferences that flow into the nuspec: every one not marked PrivateAssets="all".
    private static string[] ShippedPackageReferences()
    {
        var project = XDocument.Load(PackageProjectPath());
        return project.Descendants("PackageReference")
            .Where(static reference =>
            {
                var privateAssets = (string?)reference.Attribute("PrivateAssets") ?? (string?)reference.Element("PrivateAssets");
                return !string.Equals(privateAssets?.Trim(), "all", StringComparison.OrdinalIgnoreCase);
            })
            .Select(static reference => (string)reference.Attribute("Include")!)
            .ToArray();
    }

    [Fact]
    public void PackageProject_IsFound() =>
        File.Exists(PackageProjectPath()).Should().BeTrue();

    [Fact]
    public void EveryShippedDependency_IsUsedByTheRuntimeOrTheGeneratedCode()
    {
        var runtimeReferences = typeof(RetryPolicy).Assembly.GetReferencedAssemblies()
            .Select(static name => name.Name!)
            .ToHashSet(StringComparer.Ordinal);

        var unused = ShippedPackageReferences()
            .Where(id => !runtimeReferences.Contains(id) && !GeneratedCodeDependencies.Contains(id, StringComparer.Ordinal))
            .ToArray();

        unused.Should().BeEmpty("a package dependency nothing uses is restored and deployed by every consumer for nothing, and these are unused: {0}", string.Join(", ", unused));
    }

    [Fact]
    public void RuntimeAssemblyReferences_AreAllShippedDependencies()
    {
        var shipped = ShippedPackageReferences().ToHashSet(StringComparer.Ordinal);

        var zeroAllocReferences = typeof(RetryPolicy).Assembly.GetReferencedAssemblies()
            .Select(static name => name.Name!)
            .Where(static name => name.StartsWith("ZeroAlloc.", StringComparison.Ordinal))
            .ToArray();

        zeroAllocReferences.Should().NotBeEmpty();
        zeroAllocReferences.Should().OnlyContain(name => shipped.Contains(name), "a package the runtime assembly loads must be a declared dependency");
    }
}
