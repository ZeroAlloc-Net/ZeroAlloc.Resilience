using System.Linq;

namespace ZeroAlloc.Resilience.Generator.Tests;

// The caller-cancellation catch names the CancellationToken parameter, so a parameter whose name
// is a keyword must be emitted escaped, as every other parameter already is.
public class CallerCancellationTests
{
    [Fact]
    public void CancellationTokenNamedWithAKeyword_IsEscaped()
    {
        var (_, errors) = TestHelper.RunAndCompile("""
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace Repro;
            [Retry(MaxAttempts = 2)]
            [Timeout(Ms = 1000)]
            public interface IApi
            {
                ValueTask<string> GetAsync(CancellationToken @checked);
                string Get(CancellationToken @checked);
            }
            """);

        errors.Should().BeEmpty();
    }

    // The single-call path emits the same caller-cancellation filter, by the escaped name too.
    [Fact]
    public void CancellationTokenNamedWithAKeyword_IsEscaped_InASingleCall()
    {
        var (_, errors) = TestHelper.RunAndCompile("""
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            using ZeroAlloc.Results;
            namespace Repro;
            [CircuitBreaker(MaxFailures = 2)]
            [Timeout(Ms = 1000)]
            public interface IApi
            {
                ValueTask<string> GetAsync(CancellationToken @checked);
                Result<string> Get(CancellationToken @checked);
            }
            """);

        errors.Should().BeEmpty();
    }

    // Without a CancellationToken parameter the timed async backoff has no caller token to check:
    // a fired total timeout only ends the loop.
    [Fact]
    public void TimedAsyncRetryWithoutToken_Compiles()
    {
        var (compilation, errors) = TestHelper.RunAndCompile("""
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            using ZeroAlloc.Results;
            namespace Repro;
            [Retry(MaxAttempts = 2)]
            [Timeout(Ms = 1000)]
            public interface IApi
            {
                ValueTask<string> GetAsync();
                ValueTask<Result<string>> GetResultAsync();
            }
            """);

        errors.Should().BeEmpty();
        var generated = compilation.SyntaxTrees
            .First(static t => t.FilePath.EndsWith("Repro_IApi.Resilience.g.cs", System.StringComparison.Ordinal))
            .ToString();
        generated.Should().Contain("ConfigureAwaitOptions.SuppressThrowing");
        generated.Should().NotContain("ThrowIfCancellationRequested");
    }
}
