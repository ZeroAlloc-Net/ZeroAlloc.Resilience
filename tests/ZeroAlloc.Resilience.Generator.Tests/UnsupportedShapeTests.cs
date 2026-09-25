using System.Globalization;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Resilience.Generator.Tests;

// ZR0007: interface shapes the generator cannot build a valid proxy for are reported as a
// ZeroAlloc diagnostic, never left to fail as a compiler error in broken generated code.
public class UnsupportedShapeTests
{
    private static void AssertOnlyZR0007(string source, string proxyMetadataName, string reasonFragment)
    {
        var (compilation, errors) = TestHelper.RunAndCompile(source);

        var zr0007 = errors.Should().ContainSingle(static d => d.Id == "ZR0007").Subject;
        zr0007.Severity.Should().Be(DiagnosticSeverity.Error);
        zr0007.GetMessage(CultureInfo.InvariantCulture).Should().Contain(reasonFragment);
        errors.Should().OnlyContain(static d => d.Id == "ZR0007", "no proxy is emitted, so no compiler error follows");
        compilation.GetTypeByMetadataName(proxyMetadataName).Should().BeNull();
    }

    [Fact]
    public void GenericInterface_WithOwnMethods_ReportsZR0007()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            [Retry(MaxAttempts = 2)]
            public interface IRepository<T>
            {
                ValueTask<T> GetAsync(int id, CancellationToken ct);
            }
            """;

        AssertOnlyZR0007(source, "Repro.IRepositoryResilienceProxy", "generic");
    }

    [Fact]
    public void GenericDerivedInterface_NoOwnMembers_ReportsZR0007()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IDispatcher<TMessage>
            {
                ValueTask DispatchAsync(TMessage message, CancellationToken ct);
            }
            [Retry(MaxAttempts = 2)]
            public interface IRetryingDispatcher<TMessage> : IDispatcher<TMessage> { }
            """;

        AssertOnlyZR0007(source, "Repro.IRetryingDispatcherResilienceProxy", "generic");
    }

    [Fact]
    public void PrivateNestedInterface_ReportsZR0007()
    {
        var source = """
            using ZeroAlloc.Resilience;
            namespace Repro;
            public class Outer
            {
                [Retry(MaxAttempts = 2)]
                private interface IHidden
                {
                    void Run();
                }
            }
            """;

        AssertOnlyZR0007(source, "Repro.IHiddenResilienceProxy", "private");
    }

    [Fact]
    public void ProtectedNestedInterface_ReportsZR0007()
    {
        var source = """
            using ZeroAlloc.Resilience;
            namespace Repro;
            public class Outer
            {
                [Retry(MaxAttempts = 2)]
                protected interface IHidden
                {
                    void Run();
                }
            }
            """;

        AssertOnlyZR0007(source, "Repro.IHiddenResilienceProxy", "protected");
    }

    [Fact]
    public void InternalNestedInterface_StillGetsProxy()
    {
        var source = """
            using ZeroAlloc.Resilience;
            namespace Repro;
            public class Outer
            {
                [Retry(MaxAttempts = 2)]
                internal interface IVisible
                {
                    void Run();
                }
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        compilation.GetTypeByMetadataName("Repro.IVisibleResilienceProxy").Should().NotBeNull();
    }

    [Fact]
    public void StaticAbstractMemberInBaseInterface_ReportsZR0007()
    {
        var source = """
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IFactory
            {
                static abstract int Create();
            }
            [Retry(MaxAttempts = 2)]
            public interface IThing : IFactory
            {
                void Run();
            }
            """;

        AssertOnlyZR0007(source, "Repro.IThingResilienceProxy", "Create");
    }

    [Fact]
    public void StaticVirtualMemberOnInterface_ReportsZR0007()
    {
        var source = """
            using ZeroAlloc.Resilience;
            namespace Repro;
            [Retry(MaxAttempts = 2)]
            public interface IThing
            {
                static virtual int Create() => 1;
                void Run();
            }
            """;

        AssertOnlyZR0007(source, "Repro.IThingResilienceProxy", "Create");
    }

    [Fact]
    public void AsyncMethodWithRefParameter_UnderPolicy_ReportsZR0007()
    {
        var source = """
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            [Retry(MaxAttempts = 2)]
            public interface IThing
            {
                ValueTask FillAsync(ref int value);
            }
            """;

        AssertOnlyZR0007(source, "Repro.IThingResilienceProxy", "FillAsync");
    }

    [Fact]
    public void AsyncMethodWithRefParameter_WithoutPolicy_IsForwarded()
    {
        var source = """
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IThing
            {
                ValueTask FillAsync(ref int value);
                [Retry(MaxAttempts = 2)]
                void Run();
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        compilation.GetTypeByMetadataName("Repro.IThingResilienceProxy").Should().NotBeNull();
    }

    // NonThrowing on a method that does not return Result<T, ResilienceError> was a raw #error
    // directive, CS1029. It is ZR0003 now, own or inherited.
    [Fact]
    public void NonThrowing_OwnNonResultMethod_ReportsZR0003_NotCS1029()
    {
        var source = """
            using ZeroAlloc.Resilience;
            namespace Repro;
            [Retry(MaxAttempts = 2, NonThrowing = true)]
            public interface IThing
            {
                string Describe();
            }
            """;

        var (compilation, errors) = TestHelper.RunAndCompile(source);

        errors.Should().ContainSingle(static d => d.Id == "ZR0003")
            .Which.GetMessage(CultureInfo.InvariantCulture).Should().Contain("Describe").And.Contain("NonThrowing");
        errors.Should().OnlyContain(static d => d.Id == "ZR0003");
        compilation.GetTypeByMetadataName("Repro.IThingResilienceProxy").Should().BeNull();
    }

    [Fact]
    public void NonThrowing_OwnResultOfStringMethod_ReportsZR0003_NotCS1029()
    {
        var source = """
            using ZeroAlloc.Resilience;
            using ZeroAlloc.Results;
            namespace Repro;
            [Retry(MaxAttempts = 2, NonThrowing = true)]
            public interface IThing
            {
                Result<int> Get();
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().ContainSingle(static d => d.Id == "ZR0003");
        errors.Should().OnlyContain(static d => d.Id == "ZR0003");
    }

    [Fact]
    public void NonThrowing_InheritedAbstractNonResultMethod_NoOwnMembers_ReportsZR0003()
    {
        var source = """
            using ZeroAlloc.Resilience;
            namespace Repro;
            public interface IBase
            {
                string Describe();
            }
            [Retry(MaxAttempts = 2, NonThrowing = true)]
            public interface IDerived : IBase { }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().ContainSingle(static d => d.Id == "ZR0003");
        errors.Should().OnlyContain(static d => d.Id == "ZR0003");
    }
}
