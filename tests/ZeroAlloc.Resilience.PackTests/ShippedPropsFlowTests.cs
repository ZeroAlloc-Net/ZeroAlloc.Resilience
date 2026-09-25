using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Resilience;

namespace ZeroAlloc.Resilience.PackTests;

// This project sets ZeroAllocGeneratedAccessibility=Internal and imports
// src/ZeroAlloc.Resilience/build/ZeroAlloc.Resilience.props directly — the exact file the package
// ships at build/ZeroAlloc.Resilience.props, which NuGet auto-imports for any project with a direct
// PackageReference to ZeroAlloc.Resilience. This is the integration/pack test #152 asks for: it
// proves the shipped .props file, not a re-implementation of it, is what makes
// ZeroAllocGeneratedAccessibility reach the generator in a real consumer build.
public class ShippedPropsFlowTests
{
    [Fact]
    public void PublicInterface_CompilesWithInternalGeneratedEntryPoints()
    {
        // If build/ZeroAlloc.Resilience.props had not made ZeroAllocGeneratedAccessibility
        // CompilerVisible, the generator would never see it and this interface would generate a
        // PUBLIC AddPublicMarkerResilience extension and PublicMarkerResiliencePolicies class, same
        // as before #152. Reflection below proves both are internal instead.
        var assembly = typeof(ShippedPropsFlowTests).Assembly;

        var extensions = assembly.GetType("ZeroAlloc.Resilience.PackTests.InternalResilienceServiceCollectionExtensions", throwOnError: false);
        extensions.Should().NotBeNull("the shipped props file should route a public interface's DI extension into the internal partial class");
        extensions!.IsVisible.Should().BeFalse("the class must be internal, not public");

        var addMethod = extensions.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(m => string.Equals(m.Name, "AddPublicMarkerResilience", System.StringComparison.Ordinal) && m.GetParameters().Length == 1);
        addMethod.Should().NotBeNull();

        var policies = assembly.GetType("ZeroAlloc.Resilience.PackTests.PublicMarkerResiliencePolicies", throwOnError: false);
        policies.Should().NotBeNull();
        policies!.IsVisible.Should().BeFalse("the policies class must be internal too, not just the extension method");

        // Confirms there is no left-behind PUBLIC ResilienceServiceCollectionExtensions carrying
        // these members for this interface — that would be the CS0051/CS0053-adjacent case #152
        // calls out: a public member is never left referencing an internal generated type.
        var publicExtensions = assembly.GetType("ZeroAlloc.Resilience.PackTests.ResilienceServiceCollectionExtensions", throwOnError: false);
        if (publicExtensions is not null)
        {
            publicExtensions.GetMethods(BindingFlags.Static | BindingFlags.Public)
                .Any(m => string.Equals(m.Name, "AddPublicMarkerResilience", System.StringComparison.Ordinal))
                .Should().BeFalse();
        }

        // IPublicMarker itself is untouched: still public.
        typeof(IPublicMarker).IsPublic.Should().BeTrue();
    }
}

[Retry(MaxAttempts = 3, BackoffMs = 100)]
public interface IPublicMarker
{
    ValueTask<string> GetAsync(string id, CancellationToken ct);
}
