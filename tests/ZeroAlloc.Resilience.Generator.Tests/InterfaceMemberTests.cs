namespace ZeroAlloc.Resilience.Generator.Tests;

// Regression coverage for #168: the generated proxy only forwarded ordinary methods, so any
// interface property, indexer or event on an interface the generator touched failed with CS0535.
// Scoped to the interface's own members for the 2.0.1 patch — forwarding a member inherited from a
// base interface is #169, not this release.
public class InterfaceMemberTests
{
    [Fact]
    public void GetOnlyProperty_NextToMethodLevelRetry_Compiles()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IOutboxTypeDispatcher
            {
                string TypeName { get; }

                [Retry(MaxAttempts = 3, BackoffMs = 200)]
                ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct);
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void SchedulingShape_TypeNameAndMaxAttemptsProperties_NextToMethodLevelRetry_Compiles()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IJobTypeExecutor
            {
                string TypeName { get; }
                int MaxAttempts { get; }

                [Retry(MaxAttempts = 3, BackoffMs = 200)]
                ValueTask ExecuteAsync(CancellationToken ct);
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void GetAndSetProperty_Compiles()
    {
        var source = """
            using ZeroAlloc.Resilience;
            namespace Repro;
            [Retry(MaxAttempts = 2)]
            public interface IJevApi
            {
                int Counter { get; set; }
                void Get();
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void NullableStringProperty_Compiles_NoNullabilityWarning()
    {
        var source = """
            #nullable enable
            using ZeroAlloc.Resilience;
            namespace Repro;
            [Retry(MaxAttempts = 2)]
            public interface IJevApi
            {
                string? Description { get; set; }
                void Get();
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();

        // CS8766/CS8767: nullability of a reference type in the return/parameter type of the
        // implementing member doesn't match the interface member.
        compilation.GetDiagnostics().Should().NotContain(static d => d.Id == "CS8766" || d.Id == "CS8767");
    }

    [Fact]
    public void InitProperty_ThrowsNotSupported_AndCompiles()
    {
        var source = """
            using ZeroAlloc.Resilience;
            namespace Repro;
            [Retry(MaxAttempts = 2)]
            public interface IJevApi
            {
                string Name { get; init; }
                void Get();
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Indexer_Compiles()
    {
        var source = """
            using ZeroAlloc.Resilience;
            namespace Repro;
            [Retry(MaxAttempts = 2)]
            public interface IJevApi
            {
                string this[int index] { get; set; }
                void Get();
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Event_Compiles()
    {
        var source = """
            using System;
            using ZeroAlloc.Resilience;
            namespace Repro;
            [Retry(MaxAttempts = 2)]
            public interface IJevApi
            {
                event EventHandler? Changed;
                void Get();
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void InterfaceLevelRetry_WithProperty_Compiles()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            [Retry(MaxAttempts = 3, BackoffMs = 200)]
            public interface IJobTypeExecutor
            {
                string TypeName { get; }
                int MaxAttempts { get; }
                ValueTask ExecuteAsync(CancellationToken ct);
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void InterfaceLevelRetry_PropertiesOnly_NoMethods_GeneratesNoProxy()
    {
        // Exactly the 2.0.0 shape: an interface-level policy attribute with zero ordinary methods
        // on the interface produced no proxy at all on 2.0.0 (methodsBuilder stays empty and there
        // are no diagnostics, so TryParse returns null before any source is emitted). Adding
        // property/indexer/event forwarding must not change that — there is nothing for a policy
        // to wrap, so generating a proxy that does nothing but forward properties would be new
        // behaviour this patch does not introduce.
        var source = """
            using ZeroAlloc.Resilience;
            namespace Repro;
            [Retry(MaxAttempts = 3, BackoffMs = 200)]
            public interface IPropertiesOnly
            {
                string TypeName { get; }
                int MaxAttempts { get; }
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        compilation.GetTypeByMetadataName("Repro.IPropertiesOnlyResilienceProxy").Should().BeNull(
            "an interface with no ordinary method must still get no proxy, exactly as on 2.0.0");
    }
}
