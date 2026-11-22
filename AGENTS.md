# AGENTS.md: ZeroAlloc.Resilience

Source-generated resilience for .NET: `[Retry]`, `[Timeout]`, `[RateLimit]` and `[CircuitBreaker]` on an
interface compile into a proxy plus a DI extension, with no allocation on the happy path unless a timeout
is configured (`[Timeout]` or `[Retry(PerAttemptTimeoutMs = ...)]`). Consumers install
one NuGet package, `ZeroAlloc.Resilience`; the generator ships inside it.

## Layout

- `src/ZeroAlloc.Resilience` is the only package. It targets `net8.0;net9.0;net10.0;net11.0` and bundles the
  generator DLL under `analyzers/dotnet/cs` through the `IncludeGeneratorInPackage` target.
- `src/ZeroAlloc.Resilience.Generator` targets `netstandard2.0` and is not packable on its own
  (`IsPackable=false`). Never publish it as a separate package.
- Public API is locked by `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` (RS0016 and RS0017 are
  errors). New diagnostics go in `AnalyzerReleases.Unshipped.md` (RS2008). Both Unshipped files are moved to
  Shipped by the `ship-release-tracking` job on the release PR, not by hand.
- `build/` and `buildTransitive/ZeroAlloc.Resilience.props` ship in the package and make
  `ZeroAllocGeneratedAccessibility` visible to the generator. `tests/ZeroAlloc.Resilience.PackTests` checks
  the shipped file and the nuspec dependencies.
- `tests/ZeroAlloc.Resilience.Generator.Tests/Snapshots/*.verified.cs` are generator output snapshots. A
  generator change that alters emitted code needs the snapshots updated and reviewed in the same PR.
- `samples/ZeroAlloc.Resilience.AotSmoke` is the NativeAOT smoke app, not a demo.

## Build and test

Prerequisites: `global.json` pins SDK 11.0.100 (`rollForward: latestMinor`). The test projects target
`net10.0` and `net11.0` only, so they need the .NET 10 and .NET 11 runtimes; no .NET 8 or 9 runtime is
needed to run tests. The library itself also compiles for `net8.0` and `net9.0`.

CI job `build` in `.github/workflows/ci.yml`:

```bash
dotnet tool restore
VERSION=$(dotnet dotnet-gitversion /output json /showvariable SemVer)
dotnet restore ZeroAlloc.Resilience.slnx
dotnet build ZeroAlloc.Resilience.slnx --configuration Release --no-restore
dotnet test ZeroAlloc.Resilience.slnx --configuration Release --no-build --verbosity normal --logger "trx;LogFilePrefix=test-results" --collect "XPlat Code Coverage" --settings coverage.runsettings --results-directory TestResults
```

The same job then runs the `ci-baseline` coverage gate over `TestResults/**/coverage.cobertura.xml`. It is
an action from `MarcelRoozekrans/ci-baseline`, not a command; the `dotnet test` line above only produces its
input. Warnings are errors in every project, so a local build with a new warning fails CI too.

CI job `aot-smoke` (needs `clang` and `zlib1g-dev`):

```bash
dotnet restore samples/ZeroAlloc.Resilience.AotSmoke/ZeroAlloc.Resilience.AotSmoke.csproj
dotnet publish samples/ZeroAlloc.Resilience.AotSmoke/ZeroAlloc.Resilience.AotSmoke.csproj -r linux-x64 -c Release -o ./aot-out
./aot-out/ZeroAlloc.Resilience.AotSmoke
```

Benchmarks, `Benchmarks` workflow. The `smoke` job calls the shared `benchmark-smoke.yml` from
`ZeroAlloc-Net/.github` with `project: benchmarks/ZeroAlloc.Resilience.Benchmarks`. The manual `full` job runs:

```bash
dotnet build benchmarks/ZeroAlloc.Resilience.Benchmarks -c Release
dotnet run --project benchmarks/ZeroAlloc.Resilience.Benchmarks -c Release --no-build -- --filter "${FILTER:-*}"
```

Inner loop, not run by any CI job as written (verified locally, all pass):

```bash
dotnet build ZeroAlloc.Resilience.slnx -c Release
dotnet test tests/ZeroAlloc.Resilience.Generator.Tests -c Release --no-build -f net10.0
dotnet test tests/ZeroAlloc.Resilience.Tests -c Release --no-build -f net10.0 --filter "FullyQualifiedName~HappyPathAllocationTests"
```

CI job `sdk10-consumer` needs a 10.0.1xx SDK; `tests/ZeroAlloc.Resilience.Sdk10Smoke/global.json` pins
10.0.100 with `rollForward: latestPatch`, so a newer band cannot stand in for it. It builds and runs a
consumer of the packed packages with Roslyn 5.0:

```bash
v=9.9.9-local.sha$(git rev-parse --short HEAD).$(date +%s)
dotnet pack src/ZeroAlloc.Resilience -c Release -o artifacts -p:Version="$v"
cd tests/ZeroAlloc.Resilience.Sdk10Smoke/Consumer
dotnet run -c Release -p:ZeroAllocResilienceVersion="$v"
```

CI job `roslyn-floor` calls the shared `roslyn-floor.yml` from ZeroAlloc-Net/.github: it packs the
solution and fails when a packed DLL references Roslyn above 5.0.0.

## Conventions and gotchas
- The generator references `Microsoft.CodeAnalysis.CSharp` 5.0.0, the compiler of SDK band 10.0.1xx, the
  oldest supported SDK. A compiler older than the Roslyn the generator references skips it with `CS9057`,
  and nothing is generated. The shared Renovate preset holds it below 5.1. If a newer Roslyn API is
  needed, change the code; raise the floor only together with the minimum SDK. Test projects may use a
  newer Roslyn.
- The `ZeroAlloc.StateMachine` dependency ships its own generator, built against Roslyn 5.9 in 1.5.3.
  On a 10.0.1xx SDK that one is skipped with `CS9057` until the dependency is raised to a release built
  against 5.0.0; the `sdk10-consumer` job keeps `CS9057` a warning for that reason. Tracked in
  ZeroAlloc-Net/.github#77.

- Allocation budget: the happy path of `[Retry]`, `[CircuitBreaker]`, `[RateLimit]` and the policy
  stack without `[Timeout]` is exactly 0 B, asserted in `tests/ZeroAlloc.Resilience.Tests/HappyPathAllocationTests.cs`.
  Timeouts are the documented exception: `[Timeout]` creates one linked `CancellationTokenSource` per
  call, and `[Retry(PerAttemptTimeoutMs = ...)]` one per attempt (`docs/core-concepts/retry.md`). The
  allocation tests cover neither; do not claim 0 B for a timeout path.
- This repo's benchmark project targets `net10.0` only, unlike the org rule of targeting every LTS
  framework. It compares against Polly.Core, so published ratios come from that one runtime.
- The AOT smoke targets `net11.0` and makes `IL2026`, `IL2067`, `IL2075`, `IL2091`, `IL3050` and `IL3051`
  errors. Do not suppress one to get a green publish; remove the reflection. A new policy or attribute shape
  gets exercised in `samples/ZeroAlloc.Resilience.AotSmoke`.
- Generated code is judged by the snapshots, not by the library tests alone. Cover a new shape (async,
  `Result<T>` returns, async enumerables, inherited members, overloads, generic and by-ref methods) in both
  a generator test and an integration test in `ZeroAlloc.Resilience.Tests`.
- Diagnostics are `ZR0001` to `ZR0015`. A new one needs an entry in `AnalyzerReleases.Unshipped.md`, a test in
  `DiagnosticTests.cs` or a sibling, and a section under `docs/diagnostics`.
- The proxy applies policies in a fixed order, rate limit, circuit breaker, timeout, retry, whatever order
  the attributes are written in. Do not make order depend on attribute order.
- `Directory.Build.props` fails `dotnet pack` without `-p:Version=...` (ZAVERSION001) and when the built
  assembly does not carry that version (ZAVERSION002). A local pack must pass the version to the build step
  too.
- Packing and publishing run in the shared `publish.yml` workflow of `ZeroAlloc-Net/.github`; the nuget.org
  push is the `push` job in `release-please.yml`. Neither is reproducible from a local command sequence.

<!-- org-core:start sha256=8faf417fa86f11bd8ad9bc98dfa4683fba44e440291e84c6cb9b0ec3ca0c0e7e (generated from docker-nuc scripts/agents-md/org-core-zeroalloc.md; do not edit here) -->
## Org rules

These rules hold in every ZeroAlloc-Net repo. Each library makes two promises: it works under
NativeAOT and trimming, and its hot path allocates nothing. The 2026-10-11 sweep found 16 of 28 repos
allocating on the hot path, most with docs claiming 0 B.

### Hot path

The operation a consumer runs repeatedly: `Send`, a cache hit, `Validate`, `Map`, `Serialize`, a
resilience call, a state transition, an append, a worker poll, including the idle poll. Startup and
registration are not hot path. For a source generator, the hot path is the code it emits: judge the
snapshots and the generated output.

### Allocation traps the sweep found

| Pattern | Instead |
|---|---|
| `async ValueTask` that only awaits and returns | `return new ValueTask<T>(inner.XAsync(ct));` when the method only forwards a `Task<T>` (return a forwarded `ValueTask<T>` directly). The wrapper stores a synchronous throw in the returned task; the direct return lets it, and the cancellation status of the callee, reach the caller. Keep the `async` wrapper when callers depend on either |
| `Task.WhenAll` over tasks that already completed | skip the await only when every task has `IsCompletedSuccessfully` (`Status == TaskStatus.RanToCompletion` on `netstandard2.0`); otherwise `await Task.WhenAll`, which also reports faults of completed tasks |
| `TagList` or log `params object[]` built before `Enabled`/`IsEnabled` | check first, or `LoggerMessage.Define` |
| event args allocated before checking for subscribers | copy the handler to a local, check, then allocate |
| `new Serializer()` / `new Utf8JsonWriter()` per call | cache stateless instances, reuse writers with `Reset` |
| `GetString()` / `ToString()` round-trips in JSON converters | parse and format through spans |
| `ToArray()` / `ToList()` copies between layers | pass `ReadOnlyMemory<T>` or spans, copy once at most |
| LINQ in emitted code | emit `for` loops |
| `yield` iterator behind a public `GetEnumerator` | public struct enumerator, from the first release |
| `Enum.IsDefined(typeof(E), v)` with a boxed value, `decimal.GetBits(v)` to read the scale | `Enum.IsDefined<E>(v)` when `v` has type `E` (.NET 5 or later, so not on .NET Framework, `netstandard2.0`, `netstandard2.1` or .NET Core 3.1); `v.Scale` for a scale read only (.NET 7 or later, never for the coefficient or sign). Keep the old call on older targets |
| configuration parsed or read per call | parse once and cache |
| an `AsyncLocal` write per call | pass state explicitly |
| a capturing lambda or method-group delegate per call | cache the delegate in a field, or a static lambda |
| a per-key object that is never removed | remove on release; otherwise it is a leak |

Inherent allocations are fine when documented: the consumer's own result, an exception on a failure
path, a one-time cache fill.

### Tests and benchmarks

- Every hot path has an allocation gate test (`AllocationGate` from ZeroAlloc.TestHelpers) that measures
  the whole public call, including any state setup.
- Budgets are exact. If a path allocates, assert that number and comment what it is.
- Test the generated code, not a hand-written stand-in, and cover every shape: each attribute, nested
  collections, and the asynchronous case with a fake that returns pending tasks.
- Benchmarks run in CI through the shared `benchmark-smoke.yml` and target the library's LTS
  frameworks. The benchmark body allocates nothing besides the library itself. Run locally with
  `--inProcess`.
- Publish exact allocations and same-run ratios against a `Baseline`, never absolute timings. Run with
  `--launchCount 3`, pin CPUs with `taskset`, give the spread, and label every table.

### NativeAOT and trimming

- Packable runtime projects, and projects consumers publish, set `IsAotCompatible=true` on modern
  target frameworks. Analyzers and source generators target `netstandard2.0`.
- No reflection or runtime code generation on a supported path. An opt-in reflection fallback carries
  honest `[RequiresUnreferencedCode]` / `[RequiresDynamicCode]`. Every suppression must be true;
  remove dead ones.
- The AOT smoke sample publishes with `TreatWarningsAsErrors=true`. If a third-party warning really
  blocks it, use a narrow, justified suppression for that warning. `WarningsNotAsErrors` selects codes,
  not assemblies, so it would also downgrade the same warning from generated code.
- The smoke runs the hot path of every package in the repo. A package without a smoke has unknown AOT
  status. Adapters run against real services in CI service containers; publish-only is a last resort.
  App and template smokes make authenticated requests.

### Docs and shipping

- Docs match measurements. After a hot-path change, re-measure and update `docs/performance.md` and the
  README. Never write "0 B" without a test that asserts it.
- New public API is `feat:`, even for a performance change. Public API changes are additive: never add
  an optional parameter to a shipped signature. A breaking end state goes in a `next-major` issue.
- A behaviour change gets a `## Behaviour change` section in the PR body.
- Conventional Commits with release-please. Subjects start lowercase, body lines stay within 100
  characters, and a body never contains nested parentheses: release-please drops the whole commit.
- Local packs use `9.9.9-local.<sha>`. After a release, verify the package on NuGet.
- Fix review findings in the PR; file an issue only for what cannot go in it.

Full rules, template wiring lessons and the reasons behind each rule: https://github.com/ZeroAlloc-Net/.github/blob/main/AGENTS.md
<!-- org-core:end -->
