using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;

namespace ZeroAlloc.Resilience.PackTests;

/// <summary>
/// Packs ZeroAlloc.Resilience once per test class and reads the nuspec inside the .nupkg, so the
/// dependency tests check what a consumer restores. Reading the csproj alone would miss a
/// PackageReference added through Directory.Build.props, an imported .props file or a
/// ProjectReference.
/// </summary>
public sealed class PackedPackage : IDisposable
{
    private const string PackageId = "ZeroAlloc.Resilience";

    private static readonly TimeSpan s_packTimeout = TimeSpan.FromMinutes(10);

    private readonly string _workDir;

    public PackedPackage()
    {
        // A version no release can carry, so the pack never meets a real package in a NuGet
        // cache, and a private artifacts path, so the rebuild stays out of the repository's own
        // bin and obj that a later pack with --no-build would ship. -p:Version, not
        // -p:PackageVersion: the pack rebuilds, and the version guard in Directory.Build.props
        // rejects a package whose assembly was not stamped with its version.
        var version = $"9.9.9-packtests-{Guid.NewGuid():N}";
        _workDir = Path.Combine(Path.GetTempPath(), $"za-resilience-packtests-{Guid.NewGuid():N}");
        var output = Path.Combine(_workDir, "feed");
        var artifacts = Path.Combine(_workDir, "artifacts");
        Directory.CreateDirectory(output);

        var project = PackageProjectPath();
        var (exitCode, log) = RunDotnet(
            $"pack \"{project}\" -c Release -p:Version={version} --artifacts-path \"{artifacts}\" -o \"{output}\" --disable-build-servers",
            Path.GetDirectoryName(project)!);
        if (exitCode != 0)
            throw new InvalidOperationException($"Packing {project} failed.{Environment.NewLine}{log}");

        var nupkg = Path.Combine(output, $"{PackageId}.{version}.nupkg");
        using var archive = ZipFile.OpenRead(nupkg);
        var entry = archive.GetEntry(PackageId + ".nuspec")
            ?? throw new InvalidOperationException($"{PackageId}.nuspec not found inside {nupkg}.");
        using var stream = entry.Open();
        Nuspec = XDocument.Load(stream);
    }

    /// <summary>The nuspec of the packed package.</summary>
    public XDocument Nuspec { get; }

    /// <summary>
    /// The id of every dependency the nuspec declares, in any target framework group. The nuspec
    /// XML namespace varies with the schema version the SDK stamps, so elements are matched by
    /// local name.
    /// </summary>
    public string[] DependencyIds() =>
        Nuspec.Descendants()
            .Where(static e => string.Equals(e.Name.LocalName, "dependency", StringComparison.Ordinal))
            .Select(static e => (string)e.Attribute("id")!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>The target framework of every dependency group the nuspec declares.</summary>
    public string[] DependencyGroupFrameworks() =>
        Nuspec.Descendants()
            .Where(static e => string.Equals(e.Name.LocalName, "group", StringComparison.Ordinal))
            .Select(static e => (string)e.Attribute("targetFramework")!)
            .Order(StringComparer.Ordinal)
            .ToArray();

    public static string PackageProjectPath() =>
        Path.GetFullPath(typeof(PackedPackage).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(static a => string.Equals(a.Key, "PackageProject", StringComparison.Ordinal))
            .Value!);

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); }
        catch (IOException ex) { Debug.WriteLine($"Cleanup of {_workDir} failed: {ex}"); }
        catch (UnauthorizedAccessException ex) { Debug.WriteLine($"Cleanup of {_workDir} failed: {ex}"); }
    }

    // A pack that does not finish in time is killed and reported with its output, rather than
    // stalling the test run.
    private static (int ExitCode, string Output) RunDotnet(string arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(s_packTimeout))
        {
            process.Kill(entireProcessTree: true);
            lock (output)
                return (-1, $"dotnet {arguments} timed out after {s_packTimeout}.{Environment.NewLine}{output}");
        }

        // The parameterless overload also waits for the redirected output to be drained.
        process.WaitForExit();

        lock (output)
            return (process.ExitCode, output.ToString());
    }
}
