using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ZeroAlloc.Resilience.Generator.Tests;

// The policies class, the proxy and the Add...Resilience methods are named after the interface
// and emitted at the top of its namespace. Two interfaces that would get the same name there get
// names qualified with their containing types instead, such as First_FooResiliencePolicies; every
// other interface keeps its name (#209).
public class NameCollisionTests
{
    private const string Usings = """
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Extensions.DependencyInjection;
        using ZeroAlloc.Resilience;

        """;

    private const string Api = "{ ValueTask<int> GetAsync(CancellationToken ct); }";

    [Fact]
    public void SameNamedNestedInterfaces_GetQualifiedNames_AndEachResolvesItsOwnPolicies()
    {
        var source = Usings + $$"""
            namespace N;
            public class First
            {
                [Retry(MaxAttempts = 2)]
                public interface IFoo {{Api}}
            }
            public class Second
            {
                [Retry(MaxAttempts = 3)]
                public interface IFoo {{Api}}
            }
            public class Third
            {
                [Retry(MaxAttempts = 4)]
                public interface IBar {{Api}}
            }
            public sealed class FirstImpl : First.IFoo { public ValueTask<int> GetAsync(CancellationToken ct) => new(1); }
            public sealed class SecondImpl : Second.IFoo { public ValueTask<int> GetAsync(CancellationToken ct) => new(2); }
            public sealed class ThirdImpl : Third.IBar { public ValueTask<int> GetAsync(CancellationToken ct) => new(3); }
            public static class Use
            {
                public static void Register(IServiceCollection services)
                {
                    services.AddFirst_FooResilience<FirstImpl>((_, p) => { First_FooResiliencePolicies q = p; });
                    services.AddSecond_FooResilience<SecondImpl>((_, p) => { Second_FooResiliencePolicies q = p; });
                    services.AddFirst_FooResiliencePolicies();
                    services.AddSecond_FooResiliencePolicies();
                    services.AddBarResilience<ThirdImpl>((_, p) => { BarResiliencePolicies q = p; });
                }

                public static First.IFoo First(First.IFoo inner) => new First_IFooResilienceProxy(inner, new First_FooResiliencePolicies());
                public static Second.IFoo Second(Second.IFoo inner) => new Second_IFooResilienceProxy(inner, new Second_FooResiliencePolicies());
                public static Third.IBar Third(Third.IBar inner) => new IBarResilienceProxy(inner, new BarResiliencePolicies());
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        ProxyInterface(compilation, "N.First_IFooResilienceProxy").Should().Be("N.First.IFoo");
        ProxyInterface(compilation, "N.Second_IFooResilienceProxy").Should().Be("N.Second.IFoo");
        ProxyPolicies(compilation, "N.First_IFooResilienceProxy").Should().Be("N.First_FooResiliencePolicies");
        ProxyPolicies(compilation, "N.Second_IFooResilienceProxy").Should().Be("N.Second_FooResiliencePolicies");
        compilation.GetTypeByMetadataName("N.FooResiliencePolicies").Should().BeNull();
        compilation.GetTypeByMetadataName("N.IFooResilienceProxy").Should().BeNull();
    }

    [Fact]
    public void TopLevelInterface_KeepsItsNames_AndTheNestedOneIsQualified()
    {
        var source = Usings + $$"""
            namespace N;
            [Retry(MaxAttempts = 2)]
            public interface IFoo {{Api}}
            public class Outer
            {
                public class Inner
                {
                    [Retry(MaxAttempts = 3)]
                    public interface IFoo {{Api}}
                }
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        ProxyInterface(compilation, "N.IFooResilienceProxy").Should().Be("N.IFoo");
        ProxyInterface(compilation, "N.Outer_Inner_IFooResilienceProxy").Should().Be("N.Outer.Inner.IFoo");
        compilation.GetTypeByMetadataName("N.FooResiliencePolicies").Should().NotBeNull();
        compilation.GetTypeByMetadataName("N.Outer_Inner_FooResiliencePolicies").Should().NotBeNull();
        ExtensionMethods(compilation, "N.ResilienceServiceCollectionExtensions").Should().BeEquivalentTo(
            "AddFooResiliencePolicies", "AddFooResilience",
            "AddOuter_Inner_FooResiliencePolicies", "AddOuter_Inner_FooResilience");
    }

    // IFoo and Foo share the service name Foo, so their policies classes and methods collide,
    // though their proxies do not.
    [Fact]
    public void InterfacesSharingOnlyTheServiceName_AreQualified()
    {
        var source = Usings + $$"""
            namespace N;
            public class First { [Retry(MaxAttempts = 2)] public interface IFoo {{Api}} }
            public class Second { [Retry(MaxAttempts = 2)] public interface Foo {{Api}} }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        ProxyPolicies(compilation, "N.First_IFooResilienceProxy").Should().Be("N.First_FooResiliencePolicies");
        ProxyPolicies(compilation, "N.Second_FooResilienceProxy").Should().Be("N.Second_FooResiliencePolicies");
    }

    // A qualified name can equal another interface's own name. That interface is qualified too.
    [Fact]
    public void QualifiedNameMatchingAnotherInterfacesName_QualifiesThatInterfaceToo()
    {
        var source = Usings + $$"""
            namespace N;
            public class First { [Retry(MaxAttempts = 2)] public interface IFoo {{Api}} }
            public class Second { [Retry(MaxAttempts = 2)] public interface IFoo {{Api}} }
            public class X { [Retry(MaxAttempts = 2)] public interface IFirst_Foo {{Api}} }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        ProxyPolicies(compilation, "N.First_IFooResilienceProxy").Should().Be("N.First_FooResiliencePolicies");
        ProxyPolicies(compilation, "N.X_IFirst_FooResilienceProxy").Should().Be("N.X_First_FooResiliencePolicies");
    }

    [Fact]
    public void SameNamedNestedInterfacesInDifferentNamespaces_KeepTheirNames()
    {
        var source = Usings + $$"""
            namespace N1 { public class First { [Retry(MaxAttempts = 2)] public interface IFoo {{Api}} } }
            namespace N2 { public class Second { [Retry(MaxAttempts = 2)] public interface IFoo {{Api}} } }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        compilation.GetTypeByMetadataName("N1.FooResiliencePolicies").Should().NotBeNull();
        compilation.GetTypeByMetadataName("N2.FooResiliencePolicies").Should().NotBeNull();
    }

    // An interface that generates nothing, here ZR0007 for a generic one, takes no name.
    [Fact]
    public void InterfaceReportedAsZR0007_DoesNotQualifyTheOther()
    {
        var source = Usings + $$"""
            namespace N;
            public class First { [Retry(MaxAttempts = 2)] public interface IFoo {{Api}} }
            public class Second { [Retry(MaxAttempts = 2)] public interface IFoo<T> {{Api}} }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Select(static e => e.Id).Should().Equal("ZR0007");
        compilation.GetTypeByMetadataName("N.FooResiliencePolicies").Should().NotBeNull();
    }

    // Internal and public interfaces use different extension classes, but their policies classes
    // share the namespace.
    [Fact]
    public void PublicAndInternalSameNamedInterfaces_AreQualified()
    {
        var source = Usings + $$"""
            namespace N;
            public class First { [Retry(MaxAttempts = 2)] public interface IFoo {{Api}} }
            internal class Second { [Retry(MaxAttempts = 2)] public interface IFoo {{Api}} }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        ExtensionMethods(compilation, "N.ResilienceServiceCollectionExtensions").Should().BeEquivalentTo(
            "AddFirst_FooResiliencePolicies", "AddFirst_FooResilience");
        ExtensionMethods(compilation, "N.InternalResilienceServiceCollectionExtensions").Should().BeEquivalentTo(
            "AddSecond_FooResiliencePolicies", "AddSecond_FooResilience");
    }

    // The collect step keeps only names, so an edit that changes no name leaves it cached.
    [Fact]
    public void CollisionStep_IsCachedAcrossAnUnrelatedEdit()
    {
        var source = Usings + $$"""
            namespace N;
            public class First { [Retry(MaxAttempts = 2)] public interface IFoo {{Api}} }
            public class Second { [Retry(MaxAttempts = 2)] public interface IFoo {{Api}} }
            """;
        var compilation = TestHelper.CreateCompilation(source, referenceDependencyInjection: true);
        var parseOptions = (CSharpParseOptions)compilation.SyntaxTrees[0].Options;
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new ResilienceGenerator().AsSourceGenerator() },
            parseOptions: parseOptions,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation);

        driver = driver.RunGenerators(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText("namespace Other { public static class Unrelated { } }", parseOptions)));

        driver.GetRunResult().Results[0].TrackedSteps[ResilienceGenerator.NameCollisionsTrackingName]
            .SelectMany(static s => s.Outputs)
            .Select(static o => o.Reason)
            .Should().OnlyContain(static r => r == IncrementalStepRunReason.Cached || r == IncrementalStepRunReason.Unchanged);
    }

    private static string ProxyInterface(Compilation compilation, string proxy) =>
        compilation.GetTypeByMetadataName(proxy)!.Interfaces[0].ToDisplayString();

    private static string ProxyPolicies(Compilation compilation, string proxy) =>
        compilation.GetTypeByMetadataName(proxy)!.InstanceConstructors[0].Parameters[1].Type.ToDisplayString();

    private static string[] ExtensionMethods(Compilation compilation, string type) =>
        compilation.GetTypeByMetadataName(type)!.GetMembers().OfType<IMethodSymbol>()
            .Where(static m => m.IsExtensionMethod)
            .Select(static m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}
