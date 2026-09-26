# Result-aware Retry Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Retry a failed `Result` that a user-named static predicate calls transient, take the retry delay from the failure, cap every delay with `MaxDelayMs`, and make the retry loop propagate the caller's cancellation. Ships as ZeroAlloc.Resilience 3.2.0 and closes #142 and #143.

**Architecture:** The runtime gains a five-argument `RetryPolicy` constructor with `MaxDelayMs`, `RetryPolicy.GetDelayMs(attempt, hint)` and `CircuitBreakerPolicy.OnFailure()`. `RetryAttribute` gains four properties. The generator resolves `RetryWhen`, `RetryOnException` and `DelayHint` to static methods on the interface or its base interfaces, the way `FindFallback` resolves `Fallback`, and stores fully qualified call targets on `MethodModel`. The writer emits a new Result-aware loop for a method whose `RetryWhen` applies; every other retry method keeps today's loop, with the caller-cancellation fix and, when named, the exception predicate and exception hint.

**Tech Stack:** C# Roslyn incremental source generator targeting netstandard2.0; runtime library targeting net8.0, net9.0 and net10.0; ZeroAlloc.Results 1.2.2; xUnit 2.9 with AwesomeAssertions; `GeneratorSnapshot` from ZeroAlloc.TestHelpers; NativeAOT for the smoke sample.

**Spec:** `docs/plans/2026-09-26-result-aware-retry-design.md`. It is binding. Read it before Task 1; this plan implements it and does not repeat its rationale.

## Global Constraints

From the spec, verbatim:

- **Release:** ZeroAlloc.Resilience 3.2.0, a minor.
- **`MaxDelayMs` lives on `RetryPolicy`,** through a new constructor overload, so it can be overridden at runtime like the other values. The existing constructor keeps its exact signature, because adding an optional parameter to a shipped signature breaks binary compatibility.
- **The breaker sees what the predicate sees.** When `RetryWhen` is set, a failed Result it calls transient counts as a breaker failure, and a non-transient failed Result counts as a success, because the service answered.
- `public int GetDelayMs(int attempt, TimeSpan? hint);   // min(hint ?? GetBackoffMs(attempt), MaxDelayMs), hint < 0 → 0`
- **Validation.** The new constructor validates `maxDelayMs >= 0`, like the other constructor arguments. The attribute's `MaxDelayMs` gets a ZR0004 rule for the same bound.
- **Hint delays.** `GetDelayMs` adds no jitter to a hint, because the server asked for that exact wait.
- **Where the methods are looked up.** Each name refers to a static method on the interface or one of its base interfaces. It must be accessible from the generated proxy.
- **Predicates and hints run outside the `try`.** An exception thrown by `RetryWhen`, `RetryOnException` or `DelayHint` propagates to the caller unchanged. It is never caught and retried as if it were a failure of the inner call.
- **Where the new properties don't apply,** the loop keeps today's shape. Without `RetryWhen`, a returned Result is passed through and counts as a breaker success, exactly as today. `RetryOnException` and the `Exception` overload of `DelayHint` apply to every method with retry, Result-returning or not.
- The caller-cancellation fix "is the only change to methods that don't use the new properties, and it ships as a `fix:`."
- **Diagnostics:** ZR0001 to ZR0008 are used, and ZR0005 is retired. ZR0009 is an Error. ZR0010 is an Error on a method-level `[Retry]`, reported at the method, and a Warning for each method an interface-level `[Retry]` skips, like ZR0006.
- **One PR** closes #142 and #143, released as 3.2.0.
- **Changelog.** The PR body carries a `BEGIN_COMMIT_OVERRIDE` block listing: `feat:` retry a failed Result chosen by `RetryWhen`, #142; `feat:` take the retry delay from the failure, #143; `fix:` propagate caller cancellation from the retry loop.
- **Files.** New public API goes in `PublicAPI.Unshipped.txt`. api-compat must pass with no suppressions.
- **Commit bodies.** Lines are at most 100 characters, with no nested parentheses.
- **After merge,** confirm the release PR lists all three entries.

Repo rules:

- Worktree `C:\wt\resilience-retry`, branch `feat/result-aware-retry`, based on `origin/main` at 3.1.0. All paths below are relative to the worktree root.
- `TreatWarningsAsErrors` is on for every project, and `RS0016`/`RS0017` are errors. **No analyzer suppressions of any kind**: no `#pragma warning disable`, no `[SuppressMessage]`, no `NoWarn` additions, no `.editorconfig` severity changes, in source, tests or samples alike. Fix every warning with real code.
- Line numbers in a task's **Files** list and steps refer to the file as it is before that task's edits. Match on the quoted text, which is authoritative when an earlier step in the same task has shifted the lines.
- Generated code is fully qualified with `global::` and keeps the `// <auto-generated />` header. The generator is netstandard2.0: no `Index`/`Range` syntax (`[^1]`, `[..^3]`) in generator code.
- Commit header: lowercase conventional type, optional scope from `core`, `generator`, `benchmarks`, `ci`, `deps`, at most 100 characters, subject not sentence-case (`.commitlintrc.yml`). Body lines at most 100 characters. **No nested parentheses in a body** and never `typeof(X)` or `nameof(X)` written with parentheses: write `typeof X`, `nameof X`, or restructure. Trailer, last line: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. **Never** a `claude.ai/code/session_*` URL in a commit or the PR body.
- Snapshot acceptance, the only way: set `ZA_SNAPSHOT_UPDATE=1` for one test run, then review **every** changed or new `.verified.cs` by reading it, never by bulk-accepting. Delete any `*.received.*` before committing. Exact commands are under "Commands".

## Commands

Run all commands from `C:\wt\resilience-retry`. Git Bash syntax; the PowerShell form is given where it differs.

| Purpose | Command |
|---|---|
| Build everything, as CI does | `dotnet build ZeroAlloc.Resilience.slnx -c Release` |
| Runtime tests, filtered | `dotnet test tests/ZeroAlloc.Resilience.Tests/ZeroAlloc.Resilience.Tests.csproj -c Release --filter "FullyQualifiedName~<Class>"` |
| Generator tests, filtered | `dotnet test tests/ZeroAlloc.Resilience.Generator.Tests/ZeroAlloc.Resilience.Generator.Tests.csproj -c Release --filter "FullyQualifiedName~<Class>"` |
| Accept snapshots, bash | `ZA_SNAPSHOT_UPDATE=1 dotnet test tests/ZeroAlloc.Resilience.Generator.Tests/ZeroAlloc.Resilience.Generator.Tests.csproj -c Release --filter "FullyQualifiedName~SnapshotTests"` |
| Accept snapshots, PowerShell | `$env:ZA_SNAPSHOT_UPDATE='1'; dotnet test tests/ZeroAlloc.Resilience.Generator.Tests/ZeroAlloc.Resilience.Generator.Tests.csproj -c Release --filter "FullyQualifiedName~SnapshotTests"; Remove-Item Env:ZA_SNAPSHOT_UPDATE` |
| Review snapshot changes | `git status --short -- tests/ZeroAlloc.Resilience.Generator.Tests/Snapshots` then `git diff -- tests/ZeroAlloc.Resilience.Generator.Tests/Snapshots`, and open every new file |
| Full suite, mirrors CI | `dotnet restore ZeroAlloc.Resilience.slnx && dotnet build ZeroAlloc.Resilience.slnx --configuration Release --no-restore && dotnet test ZeroAlloc.Resilience.slnx --configuration Release --no-build` |
| AOT smoke, Windows | `dotnet publish samples/ZeroAlloc.Resilience.AotSmoke/ZeroAlloc.Resilience.AotSmoke.csproj -r win-x64 -c Release -o ./aot-out` then `./aot-out/ZeroAlloc.Resilience.AotSmoke.exe` |
| AOT smoke, Linux as CI | `dotnet publish samples/ZeroAlloc.Resilience.AotSmoke/ZeroAlloc.Resilience.AotSmoke.csproj -r linux-x64 -c Release -o ./aot-out` then `./aot-out/ZeroAlloc.Resilience.AotSmoke` |

A passing `dotnet test` run ends with `Passed!` and `Failed: 0`. A passing build ends with `Build succeeded.` and `0 Warning(s)`. Any warning in `src/`, `tests/` or `samples/` is a failure.

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `src/ZeroAlloc.Resilience/RetryPolicy.cs` | Five-argument constructor, `MaxDelayMs`, `GetDelayMs`; `GetBackoffMs` honours `MaxDelayMs` | 1 |
| `src/ZeroAlloc.Resilience/CircuitBreakerPolicy.cs` | `OnFailure()` without an exception | 1 |
| `src/ZeroAlloc.Resilience/RetryAttribute.cs` | `RetryWhen`, `RetryOnException`, `DelayHint`, `MaxDelayMs` | 1 |
| `src/ZeroAlloc.Resilience/PublicAPI.Unshipped.txt` | The twelve new public API lines | 1 |
| `src/ZeroAlloc.Resilience.Generator/ResilienceWriter.cs` | Caller-cancellation catch, token-aware waits (2); Result-aware loop, exception predicate and hint (4) | 2, 4 |
| `src/ZeroAlloc.Resilience.Generator/ResilienceGenerator.cs` | Escaped token name (2); `partial`, parsing, ZR0004 rule, calls into member resolution (3) | 2, 3 |
| `src/ZeroAlloc.Resilience.Generator/ResilienceGenerator.RetryMembers.cs` (new) | Static member lookup, ZR0009, ZR0010, `RetryMembers` | 3 |
| `src/ZeroAlloc.Resilience.Generator/ResilienceModel.cs` | `RetryConfig` new fields; `MethodModel` call targets | 3 |
| `src/ZeroAlloc.Resilience.Generator/PolicySlots.cs` | Five-argument default when `MaxDelayMs` is set | 3 |
| `src/ZeroAlloc.Resilience.Generator/ResilienceDiagnostics.cs` | ZR0009, ZR0010 | 3 |
| `tests/ZeroAlloc.Resilience.Tests/RetryPolicyTests.cs`, `PolicyValidationTests.cs`, `CircuitBreakerPolicyTests.cs` | Runtime API | 1 |
| `tests/ZeroAlloc.Resilience.Tests/CallerCancellationIntegrationTests.cs` (new) | The `fix:` at runtime | 2 |
| `tests/ZeroAlloc.Resilience.Generator.Tests/CallerCancellationTests.cs` (new) | Escaped token name | 2 |
| `tests/ZeroAlloc.Resilience.Generator.Tests/SnapshotTests.cs`, `Snapshots/` | Changed and new snapshots | 2, 4 |
| `.gitignore` | Ignore `*.received.*` | 2 |
| `tests/ZeroAlloc.Resilience.Generator.Tests/TestHelper.cs` | `GeneratorDiagnostics`; explicit ZeroAlloc.Results reference | 3 |
| `tests/ZeroAlloc.Resilience.Generator.Tests/RetryMemberDiagnosticTests.cs` (new) | ZR0009, ZR0010 | 3 |
| `tests/ZeroAlloc.Resilience.Generator.Tests/InvalidAttributeValueTests.cs`, `PolicySetGenerationTests.cs` | ZR0004 `MaxDelayMs`; five-argument default | 3 |
| `tests/ZeroAlloc.Resilience.Generator.Tests/ResultAwareRetryTests.cs` (new) | Emission shape, compile matrix | 4 |
| `tests/ZeroAlloc.Resilience.Tests/ResultAwareRetryIntegrationTests.cs` (new) | Runtime behaviour | 4 |
| `tests/ZeroAlloc.Resilience.Tests/ResultReturnTypeIntegrationTests.cs` | Stale "see #142" wording | 4 |
| `samples/ZeroAlloc.Resilience.AotSmoke/*` | AOT smoke case | 5 |
| `docs/**`, `README.md` | Documentation | 5 |

---

### Task 1: Runtime API

**Files:**
- Modify: `src/ZeroAlloc.Resilience/RetryPolicy.cs:7-58` (whole class body)
- Modify: `src/ZeroAlloc.Resilience/CircuitBreakerPolicy.cs:61-78` (`OnFailure`)
- Modify: `src/ZeroAlloc.Resilience/RetryAttribute.cs:27` (append after `NonThrowing`)
- Modify: `src/ZeroAlloc.Resilience/PublicAPI.Unshipped.txt`
- Test: `tests/ZeroAlloc.Resilience.Tests/RetryPolicyTests.cs`, `tests/ZeroAlloc.Resilience.Tests/PolicyValidationTests.cs`, `tests/ZeroAlloc.Resilience.Tests/CircuitBreakerPolicyTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces, used by Tasks 3 and 4 and by generated code:
  - `public RetryPolicy(int maxAttempts, int backoffMs, bool jitter, int perAttemptTimeoutMs, int maxDelayMs)`; the four-argument constructor chains to it with `maxDelayMs = RetryPolicy.MaxBackoffMs`.
  - `public int RetryPolicy.MaxDelayMs { get; }`
  - `public int RetryPolicy.GetDelayMs(int attempt, TimeSpan? hint)`
  - `RetryPolicy.GetBackoffMs(int attempt)` keeps its signature and now caps at `MaxDelayMs`.
  - `public void CircuitBreakerPolicy.OnFailure()`
  - `RetryAttribute`: `string? RetryWhen`, `string? RetryOnException`, `string? DelayHint`, `int MaxDelayMs = RetryPolicy.MaxBackoffMs`, all `{ get; init; }`.

- [ ] **Step 1: Write the failing tests**

Append to the class in `tests/ZeroAlloc.Resilience.Tests/RetryPolicyTests.cs`, before the closing brace:

```csharp
    [Fact]
    public void FourArgumentConstructor_HasNoDelayCap() =>
        new RetryPolicy(3, 100, false, 0).MaxDelayMs.Should().Be(RetryPolicy.MaxBackoffMs);

    [Fact]
    public void FiveArgumentConstructor_SetsMaxDelayMs() =>
        new RetryPolicy(3, 100, false, 0, maxDelayMs: 250).MaxDelayMs.Should().Be(250);

    [Fact]
    public void GetDelayMs_WithoutHint_IsTheBackoff() =>
        new RetryPolicy(3, 100, false, 0).GetDelayMs(2, null).Should().Be(400);

    [Fact]
    public void GetDelayMs_Hint_OverridesTheBackoff() =>
        new RetryPolicy(3, 100, false, 0).GetDelayMs(0, TimeSpan.FromSeconds(2)).Should().Be(2_000);

    [Fact]
    public void GetDelayMs_Hint_GetsNoJitter()
    {
        var policy = new RetryPolicy(3, 100, jitter: true, 0);
        for (var i = 0; i < 100; i++)
            policy.GetDelayMs(0, TimeSpan.FromMilliseconds(300)).Should().Be(300);
    }

    [Fact]
    public void GetDelayMs_Hint_IsCappedByMaxDelayMs() =>
        new RetryPolicy(3, 100, false, 0, maxDelayMs: 1_000)
            .GetDelayMs(0, TimeSpan.FromMinutes(5)).Should().Be(1_000);

    [Fact]
    public void GetDelayMs_Backoff_IsCappedByMaxDelayMs() =>
        new RetryPolicy(10, 100, false, 0, maxDelayMs: 1_000).GetDelayMs(5, null).Should().Be(1_000);

    [Fact]
    public void GetBackoffMs_IsCappedByMaxDelayMs()
    {
        var policy = new RetryPolicy(10, 100, jitter: true, 0, maxDelayMs: 1_000);
        for (var i = 0; i < 100; i++)
            policy.GetBackoffMs(5).Should().Be(1_000); // 3200 plus jitter, capped
    }

    [Theory]
    [InlineData(-5_000)]
    [InlineData(0)]
    public void GetDelayMs_NegativeOrZeroHint_IsZero(int hintMs) =>
        new RetryPolicy(3, 100, false, 0).GetDelayMs(0, TimeSpan.FromMilliseconds(hintMs)).Should().Be(0);

    [Fact]
    public void GetDelayMs_FractionalHint_RoundsUp() =>
        new RetryPolicy(3, 100, false, 0).GetDelayMs(0, TimeSpan.FromTicks(10_001)).Should().Be(2);

    [Fact]
    public void GetDelayMs_HugeHint_NeverOverflows() =>
        new RetryPolicy(3, 100, false, 0, maxDelayMs: int.MaxValue)
            .GetDelayMs(0, TimeSpan.MaxValue).Should().Be(RetryPolicy.MaxBackoffMs);

    [Fact]
    public void RetryAttribute_NewPropertiesDefaultToOff()
    {
        var attribute = new RetryAttribute();
        attribute.RetryWhen.Should().BeNull();
        attribute.RetryOnException.Should().BeNull();
        attribute.DelayHint.Should().BeNull();
        attribute.MaxDelayMs.Should().Be(RetryPolicy.MaxBackoffMs);
    }
```

Append to `tests/ZeroAlloc.Resilience.Tests/PolicyValidationTests.cs`, after `RetryPolicy_AcceptsMinimums`:

```csharp
    [Theory]
    [InlineData(0, 0, 0, 0, "maxAttempts")]
    [InlineData(1, -1, 0, 0, "backoffMs")]
    [InlineData(1, 0, -1, 0, "perAttemptTimeoutMs")]
    [InlineData(1, 0, 0, -1, "maxDelayMs")]
    public void RetryPolicy_FiveArguments_RejectsOutOfRangeArguments(
        int maxAttempts, int backoffMs, int perAttemptTimeoutMs, int maxDelayMs, string parameter)
    {
        var act = () => new RetryPolicy(maxAttempts, backoffMs, jitter: false, perAttemptTimeoutMs, maxDelayMs);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName(parameter);
    }

    [Fact]
    public void RetryPolicy_AcceptsZeroMaxDelayMs() =>
        new RetryPolicy(1, 0, jitter: false, 0, maxDelayMs: 0).MaxDelayMs.Should().Be(0);
```

Append to `tests/ZeroAlloc.Resilience.Tests/CircuitBreakerPolicyTests.cs`, before `public void Dispose()`:

```csharp
    [Fact]
    public void OnFailureWithoutException_CountsTowardOpening()
    {
        _cb.OnFailure();
        _cb.OnFailure();
        _cb.OnFailure();
        _cb.State.Should().Be(CircuitBreakerState.Open);
    }

    [Fact]
    public void OnFailureWithoutException_CountsTogetherWithExceptionFailures()
    {
        _cb.OnFailure(new Exception());
        _cb.OnFailure();
        _cb.OnFailure(new Exception());
        _cb.State.Should().Be(CircuitBreakerState.Open);
    }

    [Fact]
    public async Task OnFailureWithoutException_InHalfOpen_ReOpensCircuit()
    {
        _cb.OnFailure();
        _cb.OnFailure();
        _cb.OnFailure();
        await Task.Delay(200);
        _cb.State.Should().Be(CircuitBreakerState.HalfOpen);

        _cb.OnFailure();
        _cb.State.Should().Be(CircuitBreakerState.Open);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ZeroAlloc.Resilience.Tests/ZeroAlloc.Resilience.Tests.csproj -c Release --filter "FullyQualifiedName~RetryPolicyTests|FullyQualifiedName~PolicyValidationTests|FullyQualifiedName~CircuitBreakerPolicyTests"`
Expected: build FAILS with `CS1729` ('RetryPolicy' does not contain a constructor that takes 5 arguments), `CS1061` ('RetryPolicy' does not contain a definition for 'MaxDelayMs' / 'GetDelayMs'), `CS7036` (no argument for required parameter '_' of `OnFailure`) and `CS0117` ('RetryAttribute' does not contain 'RetryWhen').

- [ ] **Step 3: Implement `RetryPolicy`**

Replace everything from the `/// <summary>Per-attempt timeout` doc comment of `PerAttemptTimeoutMs` through the end of `GetBackoffMs` in `src/ZeroAlloc.Resilience/RetryPolicy.cs` with:

```csharp
    /// <summary>Per-attempt timeout in milliseconds. 0 = disabled.</summary>
    public int PerAttemptTimeoutMs { get; }

    /// <summary>
    /// The longest wait between attempts, in milliseconds, for the exponential backoff and a
    /// delay hint alike. <see cref="MaxBackoffMs"/>, the default, is no cap.
    /// </summary>
    public int MaxDelayMs { get; }

    /// <param name="maxAttempts">Total attempts including the initial call.</param>
    /// <param name="backoffMs">Base backoff milliseconds (exponential per attempt).</param>
    /// <param name="jitter">Add random jitter to prevent thundering herd.</param>
    /// <param name="perAttemptTimeoutMs">Per-attempt cancellation timeout. 0 = disabled.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxAttempts"/> is less than 1, or <paramref name="backoffMs"/> or
    /// <paramref name="perAttemptTimeoutMs"/> is negative.
    /// </exception>
    public RetryPolicy(int maxAttempts, int backoffMs, bool jitter, int perAttemptTimeoutMs)
        : this(maxAttempts, backoffMs, jitter, perAttemptTimeoutMs, MaxBackoffMs)
    {
    }

    /// <param name="maxAttempts">Total attempts including the initial call.</param>
    /// <param name="backoffMs">Base backoff milliseconds (exponential per attempt).</param>
    /// <param name="jitter">Add random jitter to prevent thundering herd.</param>
    /// <param name="perAttemptTimeoutMs">Per-attempt cancellation timeout. 0 = disabled.</param>
    /// <param name="maxDelayMs">The longest wait between attempts, backoff or hint.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxAttempts"/> is less than 1, or <paramref name="backoffMs"/>,
    /// <paramref name="perAttemptTimeoutMs"/> or <paramref name="maxDelayMs"/> is negative.
    /// </exception>
    public RetryPolicy(int maxAttempts, int backoffMs, bool jitter, int perAttemptTimeoutMs, int maxDelayMs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(backoffMs);
        ArgumentOutOfRangeException.ThrowIfNegative(perAttemptTimeoutMs);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDelayMs);
        MaxAttempts = maxAttempts;
        BackoffMs = backoffMs;
        Jitter = jitter;
        PerAttemptTimeoutMs = perAttemptTimeoutMs;
        MaxDelayMs = maxDelayMs;
    }

    // Every delay is capped here, so it is always a valid Task.Delay, WaitOne and Thread.Sleep
    // argument whatever MaxDelayMs is.
    private int DelayCap => Math.Min(MaxDelayMs, MaxBackoffMs);

    /// <summary>
    /// Computes the backoff delay for a given attempt index (0-based), capped at
    /// <see cref="MaxDelayMs"/> and never above <see cref="MaxBackoffMs"/>.
    /// </summary>
    /// <param name="attempt">Zero-based attempt index (0 = first retry delay).</param>
    public int GetBackoffMs(int attempt)
    {
        // Exponential: 200, 400, 800... Computed in long and capped, so a large attempt count can
        // never overflow into a negative or infinite delay.
        var exponent = Math.Min(Math.Max(attempt, 0), 30);
        var ms = Math.Min((long)BackoffMs << exponent, MaxBackoffMs);
        if (Jitter)
            ms += Random.Shared.NextInt64(0, Math.Max(1, ms / 2));
        return (int)Math.Min(ms, DelayCap);
    }

    /// <summary>
    /// The wait before the next attempt: <paramref name="hint"/> when there is one, otherwise
    /// <see cref="GetBackoffMs"/>, capped at <see cref="MaxDelayMs"/>. A hint gets no jitter,
    /// is rounded up to a whole millisecond, and counts as 0 when it is negative.
    /// </summary>
    /// <param name="attempt">Zero-based attempt index (0 = first retry delay).</param>
    /// <param name="hint">The delay the failure asked for, such as an HTTP Retry-After.</param>
    public int GetDelayMs(int attempt, TimeSpan? hint)
    {
        if (hint is not { } requested)
            return GetBackoffMs(attempt);

        var ticks = requested.Ticks;
        if (ticks <= 0)
            return 0;

        // Rounded up, so the wait is never shorter than the failure asked for. Division first,
        // so TimeSpan.MaxValue cannot overflow.
        var ms = ticks / TimeSpan.TicksPerMillisecond + (ticks % TimeSpan.TicksPerMillisecond == 0 ? 0 : 1);
        return (int)Math.Min(ms, DelayCap);
    }
```

- [ ] **Step 4: Implement `CircuitBreakerPolicy.OnFailure()`**

In `src/ZeroAlloc.Resilience/CircuitBreakerPolicy.cs`, replace the `OnFailure(Exception _)` method, lines 61-78, with:

```csharp
    /// <summary>Call after an inner invocation that threw.</summary>
    /// <param name="_">The exception the inner invocation threw. It is not inspected.</param>
    public void OnFailure(Exception _) => OnFailure();

    /// <summary>
    /// Call after an inner invocation that failed without throwing, such as a failed Result that
    /// the retry policy's RetryWhen calls transient.
    /// </summary>
    public void OnFailure()
    {
        var state = _fsm.Current;

        if (state == CircuitBreakerState.HalfOpen)
        {
            if (_fsm.TryFire(CircuitBreakerTrigger.FailInHalfOpen))
                ScheduleProbe();
            return;
        }

        if (state == CircuitBreakerState.Closed)
        {
            var failures = Interlocked.Increment(ref _failureCount);
            if (failures >= _maxFailures && _fsm.TryFire(CircuitBreakerTrigger.Trip))
                ScheduleProbe();
        }
    }
```

- [ ] **Step 5: Add the attribute properties**

In `src/ZeroAlloc.Resilience/RetryAttribute.cs`, after the `NonThrowing` property, add:

```csharp

    /// <summary>
    /// The name of a static method <c>bool M(E error)</c> on the interface or a base interface,
    /// where <c>E</c> is the error type of the method's Result. A failed Result it returns
    /// <see langword="true"/> for is retried, and counts as a circuit-breaker failure; one it
    /// returns <see langword="false"/> for is returned at once. Default: <see langword="null"/>,
    /// a returned Result is never retried.
    /// </summary>
    public string? RetryWhen { get; init; }

    /// <summary>
    /// The name of a static method <c>bool M(Exception exception)</c> on the interface or a base
    /// interface. A thrown exception it returns <see langword="false"/> for ends the retries.
    /// Default: <see langword="null"/>, every exception is retried.
    /// </summary>
    public string? RetryOnException { get; init; }

    /// <summary>
    /// The name of a static method, or overload set, on the interface or a base interface:
    /// <c>TimeSpan? M(E error)</c> for a failed Result and <c>TimeSpan? M(Exception exception)</c>
    /// for a thrown exception. A non-null result replaces the backoff before the next attempt,
    /// without jitter. Default: <see langword="null"/>.
    /// </summary>
    public string? DelayHint { get; init; }

    /// <summary>
    /// The longest wait between attempts in milliseconds, for the backoff and a delay hint alike.
    /// Default: <see cref="RetryPolicy.MaxBackoffMs"/>, no cap.
    /// </summary>
    public int MaxDelayMs { get; init; } = RetryPolicy.MaxBackoffMs;
```

- [ ] **Step 6: Record the public API**

Replace the contents of `src/ZeroAlloc.Resilience/PublicAPI.Unshipped.txt` with:

```text
#nullable enable
ZeroAlloc.Resilience.CircuitBreakerPolicy.OnFailure() -> void
ZeroAlloc.Resilience.RetryAttribute.DelayHint.get -> string?
ZeroAlloc.Resilience.RetryAttribute.DelayHint.init -> void
ZeroAlloc.Resilience.RetryAttribute.MaxDelayMs.get -> int
ZeroAlloc.Resilience.RetryAttribute.MaxDelayMs.init -> void
ZeroAlloc.Resilience.RetryAttribute.RetryOnException.get -> string?
ZeroAlloc.Resilience.RetryAttribute.RetryOnException.init -> void
ZeroAlloc.Resilience.RetryAttribute.RetryWhen.get -> string?
ZeroAlloc.Resilience.RetryAttribute.RetryWhen.init -> void
ZeroAlloc.Resilience.RetryPolicy.GetDelayMs(int attempt, System.TimeSpan? hint) -> int
ZeroAlloc.Resilience.RetryPolicy.MaxDelayMs.get -> int
ZeroAlloc.Resilience.RetryPolicy.RetryPolicy(int maxAttempts, int backoffMs, bool jitter, int perAttemptTimeoutMs, int maxDelayMs) -> void
```

If the build reports RS0016 or RS0017 for any line, the entry above is misspelled: apply the analyzer's code fix and diff it against this list, never suppress. `PublicAPI.Shipped.txt` is not touched.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/ZeroAlloc.Resilience.Tests/ZeroAlloc.Resilience.Tests.csproj -c Release --filter "FullyQualifiedName~RetryPolicyTests|FullyQualifiedName~PolicyValidationTests|FullyQualifiedName~CircuitBreakerPolicyTests"`
Expected: `Passed!`, `Failed: 0`.

- [ ] **Step 8: Run the full suite**

Run the "Full suite, mirrors CI" command.
Expected: `0 Warning(s)`, every test project `Passed!`. No snapshot changes: `git status --short -- tests/ZeroAlloc.Resilience.Generator.Tests/Snapshots` prints nothing.

- [ ] **Step 9: Commit**

```bash
git add src/ZeroAlloc.Resilience/RetryPolicy.cs src/ZeroAlloc.Resilience/CircuitBreakerPolicy.cs \
  src/ZeroAlloc.Resilience/RetryAttribute.cs src/ZeroAlloc.Resilience/PublicAPI.Unshipped.txt \
  tests/ZeroAlloc.Resilience.Tests/RetryPolicyTests.cs tests/ZeroAlloc.Resilience.Tests/PolicyValidationTests.cs \
  tests/ZeroAlloc.Resilience.Tests/CircuitBreakerPolicyTests.cs
git commit -F - <<'EOF'
feat(core): add MaxDelayMs, GetDelayMs and a breaker failure without an exception

RetryPolicy gains a five-argument constructor that takes maxDelayMs, the MaxDelayMs property,
and GetDelayMs, which takes a delay hint. The four-argument constructor keeps its exact
signature and means no cap. GetBackoffMs now also caps at MaxDelayMs.
CircuitBreakerPolicy gains OnFailure without an exception, for a failed Result.
RetryAttribute gains RetryWhen, RetryOnException, DelayHint and MaxDelayMs.

Refs #142, #143

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 2: Propagate caller cancellation from the retry loop (`fix:`)

**Files:**
- Modify: `src/ZeroAlloc.Resilience.Generator/ResilienceWriter.cs:248-316` (`WriteRetryLoop`), add `WriteAttemptToken`, `WriteCallerCancellationCatch`, `WriteBackoffWait` after it
- Modify: `src/ZeroAlloc.Resilience.Generator/ResilienceGenerator.cs:280-283` (`ctParamName`)
- Modify: `.gitignore`
- Modify: `tests/ZeroAlloc.Resilience.Generator.Tests/SnapshotTests.cs`
- Modify (accepted, reviewed): `tests/ZeroAlloc.Resilience.Generator.Tests/Snapshots/SnapshotTests.Retry_Only_GeneratesProxy#T_IMyService.Resilience.g.verified.cs`, `SnapshotTests.AllPolicies_ClassLevel_GeneratesProxy#T_IExternalService.Resilience.g.verified.cs`, `SnapshotTests.MethodLevel_Override_GeneratesProxy#T_IMyService.Resilience.g.verified.cs`
- Create (accepted, reviewed): `Snapshots/SnapshotTests.Retry_Sync_WithCancellationToken_GeneratesProxy#T_IMyService.Resilience.g.verified.cs`, `Snapshots/SnapshotTests.Retry_Sync_WithTimeout_GeneratesProxy#T_IMyService.Resilience.g.verified.cs`
- Create: `tests/ZeroAlloc.Resilience.Tests/CallerCancellationIntegrationTests.cs`, `tests/ZeroAlloc.Resilience.Generator.Tests/CallerCancellationTests.cs`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces, used by Task 4:
  - `private static void WriteAttemptToken(StringBuilder sb, MethodModel method, string retry, bool hasTotalTimeout)` emits the per-attempt CTS and `__ct` lines, indented 12 spaces.
  - `private static void WriteCallerCancellationCatch(StringBuilder sb, MethodModel method)` emits the caller-cancellation `catch ... when` clause, indented 12 spaces, only when the method has a `CancellationToken` parameter.
  - `private static void WriteBackoffWait(StringBuilder sb, MethodModel method, bool hasTotalTimeout, string delayExpr, string indent)` emits the wait. Task 4 adds a `bool timeoutEndsLoop` parameter.
  - `MethodModel.CancellationTokenParamName` is now the **escaped** name, such as `@checked`.

- [ ] **Step 1: Write the failing runtime tests**

Create `tests/ZeroAlloc.Resilience.Tests/CallerCancellationIntegrationTests.cs`:

```csharp
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroAlloc.Resilience.Tests;

// The caller's own cancellation ends the retry loop with OperationCanceledException: it is not
// retried, not wrapped in ResilienceException, and not counted by the circuit breaker. The
// backoff is 60 s, so a loop that ignored the caller's token would hit the 10 s test timeout.

[Retry(MaxAttempts = 5, BackoffMs = 60_000)]
public interface ICancellableRetryApi
{
    ValueTask<string> GetAsync(CancellationToken ct);
    string Get(CancellationToken ct);
}

[Retry(MaxAttempts = 5, BackoffMs = 60_000)]
[Timeout(Ms = 120_000)]
public interface ICancellableTimedRetryApi
{
    ValueTask<string> GetAsync(CancellationToken ct);
    string Get(CancellationToken ct);
}

[Retry(MaxAttempts = 2, BackoffMs = 1)]
[CircuitBreaker(MaxFailures = 1, ResetMs = 60_000)]
public interface ICancellableBreakerApi
{
    ValueTask<string> GetAsync(CancellationToken ct);
}

public sealed class FailingCancellableApi : ICancellableRetryApi, ICancellableTimedRetryApi, ICancellableBreakerApi
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public ValueTask<string> GetAsync(CancellationToken ct) => ValueTask.FromResult(Get(ct));

    public string Get(CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException("boom");
    }
}

public class CallerCancellationIntegrationTests
{
    private static Func<CancellationToken, Task> Call(FailingCancellableApi inner, bool timed, bool sync)
    {
        if (timed)
        {
            var proxy = new ICancellableTimedRetryApiResilienceProxy(inner, new CancellableTimedRetryApiResiliencePolicies());
            return sync ? ct => Task.Run(() => proxy.Get(ct)) : ct => proxy.GetAsync(ct).AsTask();
        }

        var plain = new ICancellableRetryApiResilienceProxy(inner, new CancellableRetryApiResiliencePolicies());
        return sync ? ct => Task.Run(() => plain.Get(ct)) : ct => plain.GetAsync(ct).AsTask();
    }

    [Theory(Timeout = 10_000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AlreadyCancelledCaller_IsNotRetried(bool timed, bool sync)
    {
        var inner = new FailingCancellableApi();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => Call(inner, timed, sync)(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        inner.Calls.Should().Be(1);
    }

    [Theory(Timeout = 10_000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CallerCancelledDuringBackoff_ThrowsOperationCanceledException(bool timed, bool sync)
    {
        var inner = new FailingCancellableApi();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var started = Stopwatch.GetTimestamp();

        var act = () => Call(inner, timed, sync)(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Stopwatch.GetElapsedTime(started).Should().BeLessThan(TimeSpan.FromSeconds(5));
        inner.Calls.Should().Be(1);
    }

    [Fact(Timeout = 10_000)]
    public async Task CallerCancellation_IsNotACircuitBreakerFailure()
    {
        var inner = new FailingCancellableApi();
        using var cb = new CircuitBreakerPolicy(maxFailures: 1, resetMs: 60_000, halfOpenProbes: 1);
        var proxy = new ICancellableBreakerApiResilienceProxy(inner, new CancellableBreakerApiResiliencePolicies { CircuitBreaker = cb });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await proxy.GetAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        cb.State.Should().Be(CircuitBreakerState.Closed);
    }
}
```

Create `tests/ZeroAlloc.Resilience.Generator.Tests/CallerCancellationTests.cs`:

```csharp
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
}
```

Append to the `SnapshotTests` class in `tests/ZeroAlloc.Resilience.Generator.Tests/SnapshotTests.cs`:

```csharp
    [Fact]
    public void Retry_Sync_WithCancellationToken_GeneratesProxy()
    {
        var source = """
            using ZeroAlloc.Resilience;
            using System.Threading;
            namespace T;
            [Retry(MaxAttempts = 3, BackoffMs = 100)]
            public interface IMyService
            {
                string Get(string id, CancellationToken ct);
                string GetWithoutToken(string id);
            }
            """;
        TestHelper.Verify<ResilienceGenerator>(source);
    }

    [Fact]
    public void Retry_Sync_WithTimeout_GeneratesProxy()
    {
        var source = """
            using ZeroAlloc.Resilience;
            using System.Threading;
            namespace T;
            [Retry(MaxAttempts = 3, BackoffMs = 100)]
            [Timeout(Ms = 5000)]
            public interface IMyService
            {
                string Get(string id, CancellationToken ct);
            }
            """;
        TestHelper.Verify<ResilienceGenerator>(source);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ZeroAlloc.Resilience.Tests/ZeroAlloc.Resilience.Tests.csproj -c Release --filter "FullyQualifiedName~CallerCancellationIntegrationTests"`
Expected: FAIL. The `timed` cases throw `ResilienceException` instead of `OperationCanceledException`; the untimed async cases fail with `Test execution timed out after 10000 milliseconds`; `CallerCancellation_IsNotACircuitBreakerFailure` throws `ResilienceException`.

Run: `dotnet test tests/ZeroAlloc.Resilience.Generator.Tests/ZeroAlloc.Resilience.Generator.Tests.csproj -c Release --filter "FullyQualifiedName~CallerCancellationTests|FullyQualifiedName~SnapshotTests"`
Expected: FAIL. `CancellationTokenNamedWithAKeyword_IsEscaped` reports compiler errors such as `CS1525` at `CreateLinkedTokenSource(checked)`; the two new snapshot tests fail with `missing snapshot`. The four existing snapshot tests pass.

- [ ] **Step 3: Escape the CancellationToken parameter name**

In `src/ZeroAlloc.Resilience.Generator/ResilienceGenerator.cs`, replace lines 280-283:

```csharp
            var ctParamName = member.Parameters
                .FirstOrDefault(static p =>
                    string.Equals(p.Type.ToDisplayString(), "System.Threading.CancellationToken", StringComparison.Ordinal))
                ?.Name;
```

with:

```csharp
            // Escaped, like every emitted parameter reference: the generated code names it in
            // CreateLinkedTokenSource, the caller-cancellation filter and the backoff wait.
            var ctParamName = member.Parameters
                .FirstOrDefault(static p =>
                    string.Equals(p.Type.ToDisplayString(), "System.Threading.CancellationToken", StringComparison.Ordinal))
                is { } ctParam ? EscapedName(ctParam) : null;
```

- [ ] **Step 4: Rewrite `WriteRetryLoop` and add its helpers**

In `src/ZeroAlloc.Resilience.Generator/ResilienceWriter.cs`, replace `WriteRetryLoop`, lines 248-316, with:

```csharp
    private static void WriteRetryLoop(StringBuilder sb, MethodModel method, bool hasTotalTimeout)
    {
        var retry = method.RetrySlot!.FieldName;
        var awaitKw = method.IsAsync ? "await " : "";
        var configKw = method.IsAsync ? ".ConfigureAwait(false)" : "";

        sb.AppendLine("        global::System.Exception? __lastEx = null;");
        sb.AppendLine($"        for (int __attempt = 0; __attempt < {retry}.MaxAttempts; __attempt++)");
        sb.AppendLine("        {");
        WriteAttemptToken(sb, method, retry, hasTotalTimeout);
        sb.AppendLine("            try");
        sb.AppendLine("            {");
        var callArgs = method.HasCancellationToken ? method.ArgumentListWithToken : method.ArgumentList;
        sb.AppendLine($"                {CaptureCall(method, $"{awaitKw}{InnerCall(method, callArgs)}{configKw}")}");
        if (method.CircuitBreaker is not null)
            sb.AppendLine($"                {method.CircuitBreakerSlot!.FieldName}.OnSuccess();");
        sb.AppendLine($"                {ReturnCaptured(method)}");
        sb.AppendLine("            }");
        WriteCallerCancellationCatch(sb, method);
        sb.AppendLine("            catch (global::System.Exception __ex)");
        sb.AppendLine("            {");
        sb.AppendLine("                __lastEx = __ex;");
        if (method.CircuitBreaker is not null)
            sb.AppendLine($"                {method.CircuitBreakerSlot!.FieldName}.OnFailure(__ex);");
        if (hasTotalTimeout)
            sb.AppendLine("                if (__totalCts.IsCancellationRequested) break;");
        sb.AppendLine($"                if (__attempt == {retry}.MaxAttempts - 1) break;");
        WriteBackoffWait(sb, method, hasTotalTimeout, $"{retry}.GetBackoffMs(__attempt)", "                ");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        // All attempts exhausted");
        // NonThrowing on a return type that cannot hold a ResilienceError never reaches the writer:
        // it is ZR0003, or ZR0006 with NonThrowing left off an inherited default method.
        if (method.ReturnsFailureResult)
        {
            // Result return types get a failure of their own type instead of an exception.
            sb.AppendLine($"        return {FailureExpression(method, "\"Retry\"", "__lastEx?.Message ?? \"All retry attempts failed.\"", "__lastEx")};");
        }
        else
        {
            // Also Result<T, E> with a foreign E: every attempt threw, so there is no inner
            // Result to return and no way to build an E.
            sb.AppendLine("        throw new global::ZeroAlloc.Resilience.ResilienceException(global::ZeroAlloc.Resilience.ResiliencePolicy.Retry, \"All retry attempts failed.\", __lastEx);");
        }
    }

    // The per-attempt timeout is a runtime value, so the CTS is created only when it is set.
    // Without a CancellationToken parameter there is nothing to propagate it to; ZR0002 warns
    // when the attribute asks for one.
    private static void WriteAttemptToken(StringBuilder sb, MethodModel method, string retry, bool hasTotalTimeout)
    {
        if (method.HasCancellationToken)
        {
            var outerToken = hasTotalTimeout ? "__totalCts.Token" : method.CancellationTokenParamName!;
            sb.AppendLine($"            using var __attemptCts = {retry}.PerAttemptTimeoutMs > 0");
            sb.AppendLine($"                ? global::System.Threading.CancellationTokenSource.CreateLinkedTokenSource({outerToken})");
            sb.AppendLine("                : null;");
            sb.AppendLine($"            __attemptCts?.CancelAfter({retry}.PerAttemptTimeoutMs);");
            sb.AppendLine($"            var __ct = __attemptCts?.Token ?? {outerToken};");
        }
        else if (hasTotalTimeout)
        {
            sb.AppendLine("            var __ct = __totalCts.Token;");
        }
    }

    // The caller's own cancellation is not a failure of the inner call. Caught before the catch
    // that retries, it is rethrown unchanged: not retried, not counted by the circuit breaker and
    // not wrapped in ResilienceException. A per-attempt or total timeout leaves the caller's token
    // untouched, so its OperationCanceledException still reaches the retrying catch.
    private static void WriteCallerCancellationCatch(StringBuilder sb, MethodModel method)
    {
        if (method.CancellationTokenParamName is not { } callerToken) return;
        sb.AppendLine($"            catch (global::System.OperationCanceledException) when ({callerToken}.IsCancellationRequested)");
        sb.AppendLine("            {");
        sb.AppendLine("                throw;");
        sb.AppendLine("            }");
    }

    // The wait before the next attempt. It observes the total-timeout token when [Timeout] is
    // present, and the caller's token otherwise. Async: Task.Delay throws when that token fires,
    // as before. Sync: the token's wait handle, so cancellation interrupts the wait, which
    // Thread.Sleep would not; a cancelled caller throws OperationCanceledException and a fired
    // total timeout ends the loop. Thread.Sleep remains only when no token can be cancelled.
    private static void WriteBackoffWait(StringBuilder sb, MethodModel method, bool hasTotalTimeout, string delayExpr, string indent)
    {
        var callerToken = method.CancellationTokenParamName;
        if (method.IsAsync)
        {
            var tokenArg = hasTotalTimeout ? ", __totalCts.Token" : callerToken is not null ? $", {callerToken}" : "";
            sb.AppendLine($"{indent}await global::System.Threading.Tasks.Task.Delay({delayExpr}{tokenArg}).ConfigureAwait(false);");
            return;
        }

        if (hasTotalTimeout)
        {
            sb.AppendLine($"{indent}if (__totalCts.Token.WaitHandle.WaitOne({delayExpr}))");
            sb.AppendLine($"{indent}{{");
            if (callerToken is not null)
                sb.AppendLine($"{indent}    {callerToken}.ThrowIfCancellationRequested();");
            sb.AppendLine($"{indent}    break;");
            sb.AppendLine($"{indent}}}");
            return;
        }

        if (callerToken is not null)
        {
            // CanBeCanceled: a token that can never fire has no wait handle worth creating.
            sb.AppendLine($"{indent}if ({callerToken}.CanBeCanceled)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    {callerToken}.WaitHandle.WaitOne({delayExpr});");
            sb.AppendLine($"{indent}    {callerToken}.ThrowIfCancellationRequested();");
            sb.AppendLine($"{indent}}}");
            sb.AppendLine($"{indent}else");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    global::System.Threading.Thread.Sleep({delayExpr});");
            sb.AppendLine($"{indent}}}");
            return;
        }

        sb.AppendLine($"{indent}global::System.Threading.Thread.Sleep({delayExpr});");
    }
```

- [ ] **Step 5: Ignore received snapshots**

Append to `.gitignore`:

```text

# GeneratorSnapshot writes these next to a snapshot that does not match
*.received.*
```

- [ ] **Step 6: Run the generator tests; accept and review the snapshots**

Run: `dotnet test tests/ZeroAlloc.Resilience.Generator.Tests/ZeroAlloc.Resilience.Generator.Tests.csproj -c Release --filter "FullyQualifiedName~CallerCancellationTests|FullyQualifiedName~SnapshotTests"`
Expected: `CancellationTokenNamedWithAKeyword_IsEscaped` passes. The two new snapshot tests fail with `missing snapshot`; `Retry_Only_GeneratesProxy`, `AllPolicies_ClassLevel_GeneratesProxy` and `MethodLevel_Override_GeneratesProxy` fail with `differs`. `CircuitBreaker_WithFallback_GeneratesProxy` passes: it has no retry.

Run the "Accept snapshots" command, then the "Review snapshot changes" command. Review each file by reading it; do not accept on the test result alone. Exactly these five files may appear, and nothing else:

1. `Retry_Only_GeneratesProxy`: the only change is inside `GetAsync`. Between the `try` block and `catch (global::System.Exception __ex)` there is a new clause, and the delay gains `, ct`:

   ```csharp
               catch (global::System.OperationCanceledException) when (ct.IsCancellationRequested)
               {
                   throw;
               }
               catch (global::System.Exception __ex)
               {
                   __lastEx = __ex;
                   if (__attempt == _retry.MaxAttempts - 1) break;
                   await global::System.Threading.Tasks.Task.Delay(_retry.GetBackoffMs(__attempt), ct).ConfigureAwait(false);
               }
   ```

2. `AllPolicies_ClassLevel_GeneratesProxy`: in `FetchAsync` and `FetchFallback`, only the same four-line `catch ... when (ct.IsCancellationRequested)` clause is added before `catch (global::System.Exception __ex)`. The `Task.Delay` line is unchanged: it already passes `__totalCts.Token`.
3. `MethodLevel_Override_GeneratesProxy`: the same four-line clause in `GetAsync` and in `PostAsync`, and nothing else.
4. New `Retry_Sync_WithCancellationToken_GeneratesProxy`: `Get` has the clause and this wait inside the catch; `GetWithoutToken` has no clause and keeps `global::System.Threading.Thread.Sleep(_retry.GetBackoffMs(__attempt));`:

   ```csharp
                   if (__attempt == _retry.MaxAttempts - 1) break;
                   if (ct.CanBeCanceled)
                   {
                       ct.WaitHandle.WaitOne(_retry.GetBackoffMs(__attempt));
                       ct.ThrowIfCancellationRequested();
                   }
                   else
                   {
                       global::System.Threading.Thread.Sleep(_retry.GetBackoffMs(__attempt));
                   }
   ```

5. New `Retry_Sync_WithTimeout_GeneratesProxy`: `Get` has the clause and this wait:

   ```csharp
                   if (__totalCts.IsCancellationRequested) break;
                   if (__attempt == _retry.MaxAttempts - 1) break;
                   if (__totalCts.Token.WaitHandle.WaitOne(_retry.GetBackoffMs(__attempt)))
                   {
                       ct.ThrowIfCancellationRequested();
                       break;
                   }
   ```

Any other difference, such as a changed policies class or a changed `new RetryPolicy(...)` default, is a bug: fix the writer and re-run, do not accept it. Delete any `*.received.*` file under `Snapshots/`.

Run the generator test command from the start of this step again, without the environment variable.
Expected: `Passed!`, `Failed: 0`.

- [ ] **Step 7: Run the runtime tests to verify they pass**

Run: `dotnet test tests/ZeroAlloc.Resilience.Tests/ZeroAlloc.Resilience.Tests.csproj -c Release --filter "FullyQualifiedName~CallerCancellationIntegrationTests|FullyQualifiedName~TimeoutIntegrationTests|FullyQualifiedName~PolicySetIntegrationTests"`
Expected: `Passed!`, `Failed: 0`.

- [ ] **Step 8: Run the full suite**

Run the "Full suite, mirrors CI" command.
Expected: `0 Warning(s)`, every test project `Passed!`.

- [ ] **Step 9: File the pre-existing edge cases this fix does not cover**

These are outside the spec's scope and each changes behaviour, so they need the maintainer's call. They are verified by reading the writer; group them in one issue. First check for an existing one:

```bash
gh issue list --repo ZeroAlloc-Net/ZeroAlloc.Resilience --state open --search "cancellation in:title,body"
gh issue list --repo ZeroAlloc-Net/ZeroAlloc.Resilience --state open --search "TaskCanceledException in:title,body"
```

If one covers either point, add a comment with the text below instead. Otherwise:

```bash
gh issue create --repo ZeroAlloc-Net/ZeroAlloc.Resilience \
  --title "Cancellation and total timeout outside the retry fix escape the Result and breaker contracts" \
  --body-file - <<'EOF'
Found while fixing caller cancellation in the retry loop, for #142. Both need a decision, since
each changes observable behaviour.

1. **Single call, no `[Retry]`.** `ResilienceWriter.WriteSingleCall`, around lines 335-368, catches
   every exception. A Result method with `[CircuitBreaker]` or `[Timeout]` therefore turns the
   caller's own `OperationCanceledException` into a `Failure`, and a non-Result method with
   `[CircuitBreaker]` charges the breaker with `OnFailure` for it before rethrowing. The retry
   loop now rethrows caller cancellation first; the single-call path does not.
2. **Total timeout during an async backoff.** In the exception-only retry loop the backoff
   `Task.Delay` observes `__totalCts.Token` inside the catch block. When `[Timeout]` fires during
   that wait, `TaskCanceledException` escapes the method, even for a method returning `Result`,
   which otherwise never throws for a policy failure. The Result-aware loop added for #142 exits
   through exhaustion instead.

Fix, if agreed: emit the same caller-cancellation `catch ... when` in the single-call path and
skip `OnFailure` for it; await the backoff with `ConfigureAwaitOptions.SuppressThrowing` and
`break` when the total timeout fired, as the Result-aware loop does.
EOF
```

- [ ] **Step 10: Commit**

```bash
git add .gitignore src/ZeroAlloc.Resilience.Generator/ResilienceWriter.cs src/ZeroAlloc.Resilience.Generator/ResilienceGenerator.cs \
  tests/ZeroAlloc.Resilience.Tests/CallerCancellationIntegrationTests.cs \
  tests/ZeroAlloc.Resilience.Generator.Tests/CallerCancellationTests.cs \
  tests/ZeroAlloc.Resilience.Generator.Tests/SnapshotTests.cs \
  tests/ZeroAlloc.Resilience.Generator.Tests/Snapshots
git commit -F - <<'EOF'
fix(generator): propagate caller cancellation from the retry loop

The caller's OperationCanceledException is now rethrown before the catch that retries, so it
is not retried, not counted by the circuit breaker and not wrapped in ResilienceException.
The backoff wait observes the caller's token when there is no [Timeout]. Sync methods wait on
the delay token's wait handle instead of Thread.Sleep, so cancellation interrupts the wait.
A CancellationToken parameter named with a keyword is now escaped in the generated code.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 3: Parse and resolve `RetryWhen`, `RetryOnException`, `DelayHint` and `MaxDelayMs`, with ZR0009, ZR0010 and ZR0004

**Files:**
- Modify: `src/ZeroAlloc.Resilience.Generator/ResilienceModel.cs:116` (`MethodModel` new fields), `:140` (`RetryConfig`)
- Modify: `src/ZeroAlloc.Resilience.Generator/ResilienceDiagnostics.cs` (append two descriptors)
- Modify: `src/ZeroAlloc.Resilience.Generator/PolicySlots.cs:72-73` (`Default(RetryConfig)`)
- Modify: `src/ZeroAlloc.Resilience.Generator/ResilienceGenerator.cs:13` (`partial`), `:116-131` (compilation local), `:176-180` (interface-level ZR0009), `:325-329` (error type, method-level ZR0009), `:422-424` (resolution), `:436-466` (model arguments), `:784-787` (`EscapedName`), `:1095-1104` (`ParseRetry`), `:1181-1184` (`RetryRules`), `:1201-1225` (extract `AttributeLocation`)
- Create: `src/ZeroAlloc.Resilience.Generator/ResilienceGenerator.RetryMembers.cs`
- Modify: `tests/ZeroAlloc.Resilience.Generator.Tests/TestHelper.cs`
- Create: `tests/ZeroAlloc.Resilience.Generator.Tests/RetryMemberDiagnosticTests.cs`
- Modify: `tests/ZeroAlloc.Resilience.Generator.Tests/InvalidAttributeValueTests.cs:18`, `tests/ZeroAlloc.Resilience.Generator.Tests/PolicySetGenerationTests.cs`

**Interfaces:**
- Consumes: Task 1's `RetryAttribute` properties and five-argument `RetryPolicy` constructor; Task 2's escaped `CancellationTokenParamName`.
- Produces, used by Task 4:
  - `RetryConfig(int MaxAttempts, int BackoffMs, bool Jitter, int PerAttemptTimeoutMs, bool NonThrowing = false, int? MaxDelayMs = null, string? RetryWhen = null, string? RetryOnException = null, string? DelayHint = null)`. `MaxDelayMs` is null when the attribute does not set it.
  - `MethodModel` new trailing parameters, each a call target such as `global::Ns.IApi.IsTransient` or null: `string? RetryWhenMethod`, `string? RetryOnExceptionMethod`, `string? ResultDelayHintMethod`, `string? ExceptionDelayHintMethod`. `RetryWhenMethod` non-null means the method gets the Result-aware loop. `ResultDelayHintMethod` is only ever non-null together with `RetryWhenMethod`.
  - `ResilienceDiagnostics.RetryMemberNotFound` (ZR0009) and `ResilienceDiagnostics.RetryMemberNotApplicable` (ZR0010).
  - `TestHelper.GeneratorDiagnostics(string source) -> ImmutableArray<Diagnostic>`: every generator diagnostic, any severity, with the same references as `RunAndCompile`.

- [ ] **Step 1: Add the test helper**

In `tests/ZeroAlloc.Resilience.Generator.Tests/TestHelper.cs`:

a. In `Verify<TGenerator>` and in `GetDiagnostics<TGenerator>`, the reference chain ends with `.Append(MetadataReference.CreateFromFile(typeof(RetryAttribute).Assembly.Location))`. Add after it, in both methods:

```csharp
            .Append(MetadataReference.CreateFromFile(typeof(ZeroAlloc.Results.Result).Assembly.Location))
```

Both helpers build references from the assemblies loaded in the test domain, so a Result type was only resolved when some earlier test had happened to load ZeroAlloc.Results. A snapshot or diagnostic test run on its own would otherwise see `Result<T, E>` as an error type and produce different output.

b. Append this method before `// Runtime framework references only`:

```csharp
    /// <summary>
    /// Runs the generator against <paramref name="source"/> with the same references as
    /// <see cref="RunAndCompile"/> and returns every diagnostic it reports, warnings included.
    /// </summary>
    public static ImmutableArray<Diagnostic> GeneratorDiagnostics(string source)
    {
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { CSharpSyntaxTree.ParseText(source, ParseOptions) },
            CompileReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        CSharpGeneratorDriver
            .Create(new ResilienceGenerator())
            .WithUpdatedParseOptions(ParseOptions)
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
        return diagnostics;
    }
```

`CompileReferences` is a static field declared further down the class; C# initializes static fields in textual order, but it is only read when the method runs, after type initialization, so the order is safe.

- [ ] **Step 2: Write the failing tests**

Create `tests/ZeroAlloc.Resilience.Generator.Tests/RetryMemberDiagnosticTests.cs`:

```csharp
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Resilience.Generator.Tests;

// #142 and #143: RetryWhen, RetryOnException and DelayHint name static methods on the interface
// or a base interface. ZR0009 reports a name with no method of the required shape; ZR0010 reports
// RetryWhen, or a DelayHint overload that takes the Result error type, on a method it cannot
// apply to.
public class RetryMemberDiagnosticTests
{
    private const string Usings = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using ZeroAlloc.Resilience;
        using ZeroAlloc.Results;
        namespace Repro;
        public sealed class HttpError { public int Status { get; init; } }
        public sealed class OtherError { }

        """;

    private static string SourceAt(string source, Diagnostic diagnostic) =>
        source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length);

    private static Diagnostic[] Zr(string source) =>
        TestHelper.GeneratorDiagnostics(source).Where(static d => d.Id.StartsWith("ZR", System.StringComparison.Ordinal)).ToArray();

    // ── ZR0009 ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("", "missing")]
    [InlineData("bool IsTransient(HttpError error) => true;", "not static")]
    [InlineData("static bool IsTransient(HttpError error, int attempt) => true;", "wrong parameter count")]
    [InlineData("static bool IsTransient(ref HttpError error) => true;", "by-reference parameter")]
    [InlineData("static int IsTransient(HttpError error) => 0;", "wrong return type")]
    [InlineData("private static bool IsTransient(HttpError error) => true;", "not accessible from the proxy")]
    [InlineData("static bool IsTransient<T>(HttpError error) => true;", "generic")]
    public void RetryWhen_WithoutAMatchingStaticMethod_ReportsZR0009AtTheAttribute(string member, string because)
    {
        var source = Usings + $$"""
            [Retry(RetryWhen = "IsTransient")]
            public interface IApi
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
                {{member}}
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        var zr0009 = errors.Should().ContainSingle(because).Subject;
        zr0009.Id.Should().Be("ZR0009");
        zr0009.GetMessage(CultureInfo.InvariantCulture).Should()
            .Contain("RetryWhen = \"IsTransient\"")
            .And.Contain("'static bool IsTransient(E error)', where E is the error type of the method's Result");
        SourceAt(source, zr0009).Should().StartWith("Retry(");
    }

    [Fact]
    public void MethodLevelRetryWhen_Missing_NamesTheErrorTypeInTheMessage()
    {
        var source = Usings + """
            public interface IApi
            {
                [Retry(RetryWhen = "IsTransient")]
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().ContainSingle().Which.GetMessage(CultureInfo.InvariantCulture)
            .Should().Contain("'static bool IsTransient(HttpError error)'");
    }

    [Theory]
    [InlineData("RetryOnException", "static bool Check(string message) => true;", "'static bool Check(Exception exception)'")]
    [InlineData("DelayHint", "static TimeSpan Check(HttpError error) => TimeSpan.Zero;", "'static TimeSpan? Check(E error)' or 'static TimeSpan? Check(Exception exception)'")]
    public void OtherNames_WithoutAMatchingStaticMethod_ReportZR0009(string property, string member, string expected)
    {
        var source = Usings + $$"""
            [Retry({{property}} = "Check")]
            public interface IApi
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
                {{member}}
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        var zr0009 = errors.Should().ContainSingle().Subject;
        zr0009.Id.Should().Be("ZR0009");
        zr0009.GetMessage(CultureInfo.InvariantCulture).Should().Contain(expected);
    }

    [Theory]
    [InlineData("public static bool IsTransient(HttpError error) => true;")]
    [InlineData("internal static bool IsTransient(HttpError error) => true;")]
    [InlineData("static bool IsTransient(HttpError error) => true;")]
    public void RetryWhen_AccessibleStaticMethod_HasNoDiagnostics(string member)
    {
        var source = Usings + $$"""
            [Retry(RetryWhen = nameof(IsTransient))]
            public interface IApi
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
                {{member}}
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        Zr(source).Should().BeEmpty();
    }

    [Fact]
    public void RetryMembers_DeclaredOnABaseInterface_AreFound()
    {
        var source = Usings + """
            public interface IRetryRules
            {
                static bool IsTransient(HttpError error) => error.Status == 429;
                static bool IsTransientException(Exception exception) => true;
                static TimeSpan? RetryAfter(HttpError error) => null;
                static TimeSpan? RetryAfter(Exception exception) => null;
            }
            [Retry(RetryWhen = nameof(IRetryRules.IsTransient), RetryOnException = nameof(IRetryRules.IsTransientException),
                   DelayHint = nameof(IRetryRules.RetryAfter))]
            public interface IApi : IRetryRules
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        Zr(source).Should().BeEmpty();
    }

    [Fact]
    public void StaticAbstractPredicate_IsZR0007_NotZR0009()
    {
        var source = Usings + """
            [Retry(RetryWhen = nameof(IsTransient))]
            public interface IApi
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
                static abstract bool IsTransient(HttpError error);
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().ContainSingle().Which.Id.Should().Be("ZR0007");
    }

    // ── ZR0010 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void MethodLevelRetryWhen_OnANonResultMethod_ReportsZR0010ErrorAtTheMethod()
    {
        var source = Usings + """
            public interface IApi
            {
                [Retry(RetryWhen = nameof(IsTransient))]
                ValueTask<string> GetAsync(CancellationToken ct);
                static bool IsTransient(HttpError error) => true;
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        var zr0010 = errors.Should().ContainSingle().Subject;
        zr0010.Id.Should().Be("ZR0010");
        zr0010.Severity.Should().Be(DiagnosticSeverity.Error);
        SourceAt(source, zr0010).Should().Be("GetAsync");
        zr0010.GetMessage(CultureInfo.InvariantCulture).Should()
            .Contain("RetryWhen = \"IsTransient\"")
            .And.Contain("does not return a ZeroAlloc.Results Result");
    }

    [Fact]
    public void MethodLevelRetryWhen_OnAnotherErrorType_ReportsZR0010Error()
    {
        var source = Usings + """
            public interface IApi
            {
                [Retry(RetryWhen = nameof(IsTransient))]
                ValueTask<Result<string, OtherError>> GetAsync(CancellationToken ct);
                static bool IsTransient(HttpError error) => true;
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        var zr0010 = errors.Should().ContainSingle().Subject;
        zr0010.Id.Should().Be("ZR0010");
        zr0010.GetMessage(CultureInfo.InvariantCulture).Should()
            .Contain("its Result error type is 'OtherError'")
            .And.Contain("no 'IsTransient' overload takes it");
    }

    [Fact]
    public void InterfaceLevelRetryWhen_OnAMixedInterface_WarnsForEachMethodItSkips()
    {
        var source = Usings + """
            [Retry(RetryWhen = nameof(IsTransient))]
            public interface IApi
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
                ValueTask<string> GetTextAsync(CancellationToken ct);
                string GetName();
                static bool IsTransient(HttpError error) => true;
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);
        var warnings = Zr(source);

        errors.Should().BeEmpty();
        warnings.Should().HaveCount(2).And.OnlyContain(static d => d.Id == "ZR0010" && d.Severity == DiagnosticSeverity.Warning);
        warnings.Select(d => SourceAt(source, d)).Should().BeEquivalentTo("GetTextAsync", "GetName");
        warnings[0].GetMessage(CultureInfo.InvariantCulture).Should().EndWith("keeps exception-only retry.");
    }

    [Fact]
    public void ErrorTypedDelayHint_WithoutRetryWhen_ReportsZR0010()
    {
        var source = Usings + """
            public interface IApi
            {
                [Retry(DelayHint = nameof(RetryAfter))]
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
                static TimeSpan? RetryAfter(HttpError error) => null;
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        var zr0010 = errors.Should().ContainSingle().Subject;
        zr0010.Id.Should().Be("ZR0010");
        zr0010.GetMessage(CultureInfo.InvariantCulture).Should()
            .Contain("DelayHint = \"RetryAfter\"")
            .And.Contain("has no RetryWhen");
    }

    [Fact]
    public void RetryWhenAndErrorTypedDelayHint_BothInapplicable_ReportOneZR0010()
    {
        var source = Usings + """
            public interface IApi
            {
                [Retry(RetryWhen = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
                ValueTask<string> GetAsync(CancellationToken ct);
                static bool IsTransient(HttpError error) => true;
                static TimeSpan? RetryAfter(HttpError error) => null;
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().ContainSingle().Which.GetMessage(CultureInfo.InvariantCulture).Should()
            .Contain("RetryWhen = \"IsTransient\" and DelayHint = \"RetryAfter\"");
    }

    [Fact]
    public void ExceptionOnlyNames_OnANonResultMethod_HaveNoDiagnostics()
    {
        var source = Usings + """
            [Retry(RetryOnException = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
            public interface IApi
            {
                ValueTask<string> GetAsync(CancellationToken ct);
                string Get();
                static bool IsTransient(Exception exception) => true;
                static TimeSpan? RetryAfter(Exception exception) => null;
            }
            """;

        var (_, errors) = TestHelper.RunAndCompile(source);

        errors.Should().BeEmpty();
        Zr(source).Should().BeEmpty();
    }
}
```

In `tests/ZeroAlloc.Resilience.Generator.Tests/InvalidAttributeValueTests.cs`, add after line 13, `[InlineData("[Retry(PerAttemptTimeoutMs = -1)]", "PerAttemptTimeoutMs")]`:

```csharp
    [InlineData("[Retry(MaxDelayMs = -1)]", "MaxDelayMs")]
```

Append to the `PolicySetGenerationTests` class in `tests/ZeroAlloc.Resilience.Generator.Tests/PolicySetGenerationTests.cs`:

```csharp
    [Fact]
    public void MaxDelayMs_WhenSet_EmitsTheFiveArgumentConstructor()
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + """
            [Retry(MaxAttempts = 4, BackoffMs = 500, MaxDelayMs = 2000)]
            public interface IJevApi
            {
                ValueTask<string> ModelsAsync(CancellationToken ct);
            }
            """);

        errors.Should().BeEmpty();
        string.Join("\n", compilation.SyntaxTrees.Select(static t => t.ToString())).Should()
            .Contain("Retry { get; set; } = new global::ZeroAlloc.Resilience.RetryPolicy(4, 500, false, 0, 2000);");
    }

    [Fact]
    public void MaxDelayMs_WhenUnset_KeepsTheFourArgumentConstructor()
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + """
            [Retry(MaxAttempts = 4, BackoffMs = 500)]
            public interface IJevApi
            {
                ValueTask<string> ModelsAsync(CancellationToken ct);
            }
            """);

        errors.Should().BeEmpty();
        string.Join("\n", compilation.SyntaxTrees.Select(static t => t.ToString())).Should()
            .Contain("Retry { get; set; } = new global::ZeroAlloc.Resilience.RetryPolicy(4, 500, false, 0);");
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/ZeroAlloc.Resilience.Generator.Tests/ZeroAlloc.Resilience.Generator.Tests.csproj -c Release --filter "FullyQualifiedName~RetryMemberDiagnosticTests|FullyQualifiedName~InvalidAttributeValueTests|FullyQualifiedName~PolicySetGenerationTests"`
Expected: FAIL. Every ZR0009 and ZR0010 test finds no diagnostic; `InvalidValue_ReportsExactlyOneZR0004WithPropertyName` with `MaxDelayMs` finds no ZR0004; `MaxDelayMs_WhenSet_EmitsTheFiveArgumentConstructor` does not find `, 2000)`. `StaticAbstractPredicate_IsZR0007_NotZR0009`, `RetryWhen_AccessibleStaticMethod_HasNoDiagnostics`, `RetryMembers_DeclaredOnABaseInterface_AreFound`, `ExceptionOnlyNames_OnANonResultMethod_HaveNoDiagnostics` and `MaxDelayMs_WhenUnset_KeepsTheFourArgumentConstructor` already pass; they lock behaviour in.

- [ ] **Step 4: Extend the model**

In `src/ZeroAlloc.Resilience.Generator/ResilienceModel.cs`, replace line 140:

```csharp
internal sealed record RetryConfig(int MaxAttempts, int BackoffMs, bool Jitter, int PerAttemptTimeoutMs, bool NonThrowing = false);
```

with:

```csharp
// MaxDelayMs is null when the attribute does not set it, so the policies class keeps the
// four-argument RetryPolicy constructor. The three names are the attribute's strings; what they
// resolve to is per method, on MethodModel. They are part of the record so that two inherited
// declarations with different names never collapse into one proxy member.
internal sealed record RetryConfig(
    int MaxAttempts,
    int BackoffMs,
    bool Jitter,
    int PerAttemptTimeoutMs,
    bool NonThrowing = false,
    int? MaxDelayMs = null,
    string? RetryWhen = null,
    string? RetryOnException = null,
    string? DelayHint = null);
```

In the same file, replace line 116, `    string HelperName = ""`, with:

```csharp
    string HelperName = "",
    // The static methods the retry loop calls, as fully qualified call targets such as
    // "global::Ns.IApi.IsTransient", or null. RetryWhenMethod resolved for this method's Result
    // error type switches it to the Result-aware loop; ResultDelayHintMethod is the DelayHint
    // overload taking that error type and is only set with RetryWhenMethod. The other two apply
    // to every method with retry.
    string? RetryWhenMethod = null,
    string? RetryOnExceptionMethod = null,
    string? ResultDelayHintMethod = null,
    string? ExceptionDelayHintMethod = null
```

- [ ] **Step 5: Add the descriptors**

Append to the `ResilienceDiagnostics` class in `src/ZeroAlloc.Resilience.Generator/ResilienceDiagnostics.cs`:

```csharp

    /// <summary>
    /// ZR0009 — A [Retry] RetryWhen, RetryOnException or DelayHint names no accessible static
    /// method with the required signature on the interface or its base interfaces (Error).
    /// </summary>
    public static readonly DiagnosticDescriptor RetryMemberNotFound = new(
        id: "ZR0009",
        title: "Retry member not found or signature mismatch",
        messageFormat: "[Retry] {0} = \"{1}\" names no accessible static method on '{2}' or its base interfaces with the signature {3}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// ZR0010 — RetryWhen, or a DelayHint overload that takes the Result error type, cannot apply
    /// to a method. An Error on a method-level [Retry]; reported with effective severity Warning
    /// for each method an interface-level [Retry] skips, which keeps exception-only retry.
    /// </summary>
    public static readonly DiagnosticDescriptor RetryMemberNotApplicable = new(
        id: "ZR0010",
        title: "Result-aware retry cannot apply to method",
        messageFormat: "[Retry] {0} cannot apply to '{1}', because {2}. {3}.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
```

- [ ] **Step 6: Emit the five-argument default when `MaxDelayMs` is set**

In `src/ZeroAlloc.Resilience.Generator/PolicySlots.cs`, replace `Default(RetryConfig c)`, lines 72-73, with:

```csharp
    // The four-argument constructor unless the attribute sets MaxDelayMs, so every interface that
    // does not use it generates exactly what it did in 3.1.
    public static string Default(RetryConfig c) =>
        $"new global::ZeroAlloc.Resilience.RetryPolicy({c.MaxAttempts.ToString(CultureInfo.InvariantCulture)}, {c.BackoffMs.ToString(CultureInfo.InvariantCulture)}, {(c.Jitter ? "true" : "false")}, {c.PerAttemptTimeoutMs.ToString(CultureInfo.InvariantCulture)}"
        + (c.MaxDelayMs is { } maxDelayMs ? $", {maxDelayMs.ToString(CultureInfo.InvariantCulture)})" : ")");
```

- [ ] **Step 7: Create the member resolution file**

Create `src/ZeroAlloc.Resilience.Generator/ResilienceGenerator.RetryMembers.cs`:

```csharp
namespace ZeroAlloc.Resilience.Generator;

using Microsoft.CodeAnalysis;
using System;
using System.Collections.Immutable;

// RetryWhen, RetryOnException and DelayHint: string references to static methods, resolved at
// generation time the way Fallback is, so the retry loop calls them directly.
public sealed partial class ResilienceGenerator
{
    // The call targets one method's retry loop uses, such as "global::Ns.IApi.IsTransient", or
    // null where it calls none.
    private sealed record RetryMembers(string? RetryWhen, string? RetryOnException, string? ResultDelayHint, string? ExceptionDelayHint)
    {
        public static readonly RetryMembers None = new(null, null, null, null);
    }

    // The error type E of the ZeroAlloc.Results type a method returns: string for Result and
    // Result<T>, the last type argument for Result<T, E> and UnitResult<E>; null for any other type.
    private static ITypeSymbol? ResultErrorType(ITypeSymbol? resultType, ResultKind kind, Compilation compilation)
    {
        if (kind == ResultKind.None) return null;
        if (kind == ResultKind.StringError) return compilation.GetSpecialType(SpecialType.System_String);
        return resultType is INamedTypeSymbol { TypeArguments.Length: > 0 } named
            ? named.TypeArguments[named.TypeArguments.Length - 1]
            : null;
    }

    // ZR0009: each name a [Retry] sets must match at least one accessible static method of the
    // shape its property needs, on the interface or a base interface. Whether that method also
    // takes the right error type depends on the method: ZR0010, in ResolveRetryMembers.
    // errorTypeDisplay is the error type of a method-level attribute's method, or null.
    private static void ValidateRetryMemberNames(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        AttributeData? attr,
        INamedTypeSymbol iface,
        Compilation compilation,
        string? errorTypeDisplay,
        Location? fallbackLocation)
    {
        if (attr is null) return;

        var location = AttributeLocation(attr, fallbackLocation);
        var errorParameter = errorTypeDisplay ?? "E";
        var errorWhere = errorTypeDisplay is null ? ", where E is the error type of the method's Result" : "";

        if (GetString(attr, "RetryWhen") is { } retryWhen
            && FindRetryMember(iface, compilation, retryWhen, IsBoolean, static _ => true) is null)
        {
            Report("RetryWhen", retryWhen, $"'static bool {retryWhen}({errorParameter} error)'{errorWhere}");
        }

        if (GetString(attr, "RetryOnException") is { } retryOnException
            && FindRetryMember(iface, compilation, retryOnException, IsBoolean, IsException) is null)
        {
            Report("RetryOnException", retryOnException, $"'static bool {retryOnException}(Exception exception)'");
        }

        if (GetString(attr, "DelayHint") is { } delayHint
            && FindRetryMember(iface, compilation, delayHint, IsNullableTimeSpan, static _ => true) is null)
        {
            Report("DelayHint", delayHint,
                $"'static TimeSpan? {delayHint}({errorParameter} error)' or 'static TimeSpan? {delayHint}(Exception exception)'{errorWhere}");
        }

        void Report(string property, string name, string signature) =>
            diagnostics.Add(Diagnostic.Create(ResilienceDiagnostics.RetryMemberNotFound, location,
                property, name, iface.Name, signature));
    }

    // Resolves the names of the method's effective [Retry] for this method. A name ZR0009 already
    // rejected resolves to nothing and is not reported again. ZR0010 is reported once per method,
    // naming every property that cannot apply: an Error when the [Retry] is the method's own, a
    // Warning when it is the interface's, and not at all when report is false.
    private static RetryMembers ResolveRetryMembers(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        INamedTypeSymbol iface,
        Compilation compilation,
        IMethodSymbol method,
        RetryConfig retry,
        ITypeSymbol? errorType,
        bool methodLevel,
        bool report,
        Location? location)
    {
        var retryOnException = retry.RetryOnException is { } onExceptionName
            ? FindRetryMember(iface, compilation, onExceptionName, IsBoolean, IsException)
            : null;
        var exceptionHint = retry.DelayHint is { } exceptionHintName
            ? FindRetryMember(iface, compilation, exceptionHintName, IsNullableTimeSpan, IsException)
            : null;

        var notApplicable = new System.Collections.Generic.List<string>(2);
        string? reason = null;
        string? advice = null;

        // RetryWhen: does an overload of the right shape take this method's error type?
        string? retryWhen = null;
        var retryWhenHasShape = false;
        if (retry.RetryWhen is { } retryWhenName
            && FindRetryMember(iface, compilation, retryWhenName, IsBoolean, static _ => true) is not null)
        {
            retryWhenHasShape = true;
            retryWhen = errorType is { } error
                ? FindRetryMember(iface, compilation, retryWhenName, IsBoolean, t => SameType(t, error))
                : null;
            if (retryWhen is null)
            {
                notApplicable.Add($"RetryWhen = \"{retryWhenName}\"");
                (reason, advice) = errorType is { } mismatched ? NoOverloadFor(retryWhenName, mismatched) : NotAResult();
            }
        }

        // DelayHint overloads that take an error type serve failed Results, so they need RetryWhen.
        // Skipped when RetryWhen is set but is ZR0009's.
        string? resultHint = null;
        if (retry.DelayHint is { } hintName
            && (retry.RetryWhen is null || retryWhenHasShape)
            && FindRetryMember(iface, compilation, hintName, IsNullableTimeSpan, static t => !IsException(t)) is not null)
        {
            var hintForError = errorType is { } error
                ? FindRetryMember(iface, compilation, hintName, IsNullableTimeSpan, t => SameType(t, error))
                : null;
            if (retryWhen is not null && hintForError is not null)
            {
                resultHint = hintForError;
            }
            else if (reason is null)
            {
                notApplicable.Add($"DelayHint = \"{hintName}\"");
                (reason, advice) = errorType is not { } hintError ? NotAResult()
                    : retry.RetryWhen is null ? NoRetryWhen()
                    : NoOverloadFor(hintName, hintError);
            }
            else if (hintForError is null)
            {
                // RetryWhen cannot apply either, for the same reason: one diagnostic names both.
                // A hint overload that does take the error type starts working once RetryWhen does.
                notApplicable.Add($"DelayHint = \"{hintName}\"");
            }
        }

        if (report && notApplicable.Count > 0)
        {
            diagnostics.Add(Diagnostic.Create(
                ResilienceDiagnostics.RetryMemberNotApplicable,
                location,
                methodLevel ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                null,
                null,
                string.Join(" and ", notApplicable),
                method.Name,
                reason,
                methodLevel ? advice : $"'{method.Name}' keeps exception-only retry"));
        }

        return new RetryMembers(retryWhen, retryOnException, resultHint, exceptionHint);

        static (string Reason, string Advice) NotAResult() =>
            ("it does not return a ZeroAlloc.Results Result, so there is no failed Result to pass to it",
             "Remove it from this method's [Retry]");

        static (string Reason, string Advice) NoRetryWhen() =>
            ("[Retry] has no RetryWhen, so a failed Result is never retried and its delay hint is never read",
             "Set RetryWhen, or remove the DelayHint overload that takes the Result error type");

        static (string Reason, string Advice) NoOverloadFor(string name, ITypeSymbol type)
        {
            var display = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            return ($"its Result error type is '{display}', and no '{name}' overload takes it",
                    $"Declare an overload of '{name}' that takes '{display}', or remove it from this method's [Retry]");
        }
    }

    // Looked up like FindFallback: the interface itself first, then every base interface in
    // AllInterfaces order; the first match wins. Returns the call target, or null.
    private static string? FindRetryMember(
        INamedTypeSymbol iface,
        Compilation compilation,
        string name,
        Func<ITypeSymbol, bool> returns,
        Func<ITypeSymbol, bool> takes)
    {
        var found = FindRetryMemberIn(iface, compilation, name, returns, takes);
        foreach (var baseInterface in iface.AllInterfaces)
            found ??= FindRetryMemberIn(baseInterface, compilation, name, returns, takes);
        return found is null ? null : CallTarget(found);
    }

    private static IMethodSymbol? FindRetryMemberIn(
        INamedTypeSymbol type,
        Compilation compilation,
        string name,
        Func<ITypeSymbol, bool> returns,
        Func<ITypeSymbol, bool> takes)
    {
        foreach (var candidate in type.GetMembers(name))
        {
            if (candidate is IMethodSymbol method
                && IsRetryMemberShape(method, compilation)
                && returns(method.ReturnType)
                && takes(method.Parameters[0].Type))
                return method;
        }
        return null;
    }

    // A method a [Retry] name can refer to: an ordinary static method with a body, so neither
    // static abstract nor static virtual, which could only be called through a constrained type
    // parameter the proxy does not have; not generic, since nothing could infer its type
    // arguments; one by-value parameter; not returning by reference; and callable from the
    // generated proxy, a top-level class in this compilation. That rules out private and
    // protected members, and internal ones of another assembly without InternalsVisibleTo.
    private static bool IsRetryMemberShape(IMethodSymbol method, Compilation compilation) =>
        method is { MethodKind: MethodKind.Ordinary, IsStatic: true, IsAbstract: false, IsVirtual: false, Arity: 0,
                    ReturnsByRef: false, ReturnsByRefReadonly: false, Parameters.Length: 1 }
        && method.Parameters[0].RefKind == RefKind.None
        && compilation.IsSymbolAccessibleWithin(method, compilation.Assembly);

    // Called through its declaring interface, fully qualified, so a base interface's method binds
    // to exactly that declaration.
    private static string CallTarget(IMethodSymbol method) =>
        $"{method.ContainingType.ToDisplayString(FqnFormat)}.{EscapeIdentifier(method.Name)}";

    private static bool IsBoolean(ITypeSymbol type) => type.SpecialType == SpecialType.System_Boolean;

    private static bool IsException(ITypeSymbol type) =>
        string.Equals(type.ToDisplayString(), "System.Exception", StringComparison.Ordinal);

    private static bool IsNullableTimeSpan(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, TypeArguments.Length: 1 } nullable
        && string.Equals(nullable.TypeArguments[0].ToDisplayString(), "System.TimeSpan", StringComparison.Ordinal);

    // E matched by type equality; SymbolEqualityComparer.Default ignores nullable annotations, so
    // a predicate taking HttpError? accepts an HttpError error type.
    private static bool SameType(ITypeSymbol candidate, ITypeSymbol errorType) =>
        SymbolEqualityComparer.Default.Equals(candidate, errorType);
}
```

- [ ] **Step 8: Wire it into `ResilienceGenerator.cs`**

a. Line 13: `public sealed class ResilienceGenerator : IIncrementalGenerator` becomes `public sealed partial class ResilienceGenerator : IIncrementalGenerator`.

b. In `TryParse`, after the `foreach` over `iface.DeclaringSyntaxReferences` that ends at line 129, add:

```csharp

        var compilation = ctx.SemanticModel.Compilation;
```

c. After line 180, `ValidateAttributeValues(diagnosticsBuilder, circuitBreakerAttr, CircuitBreakerRules, iface.Name, ifaceLocation);`, add:

```csharp
        // ZR0009: the interface-level [Retry] names, reported once at the attribute.
        ValidateRetryMemberNames(diagnosticsBuilder, retryAttr, iface, compilation, errorTypeDisplay: null, ifaceLocation);
```

d. Replace lines 325-329:

```csharp
            var resultType = isAsync ? UnwrapAsyncType(member.ReturnType) : member.ReturnType;
            var resultKind = ClassifyResult(resultType);
            var resultTypeFqn = resultKind == ResultKind.None
                ? null
                : resultType!.ToDisplayString(FqnFormat);
```

with:

```csharp
            var resultType = isAsync ? UnwrapAsyncType(member.ReturnType) : member.ReturnType;
            var resultKind = ClassifyResult(resultType);
            var resultTypeFqn = resultKind == ResultKind.None
                ? null
                : resultType!.ToDisplayString(FqnFormat);
            var errorType = ResultErrorType(resultType, resultKind, compilation);

            // ZR0009 for the method's own [Retry], at the attribute. An inherited method's
            // attribute belongs to the base interface, which validates it itself.
            if (entry.IsOwn)
            {
                ValidateRetryMemberNames(diagnosticsBuilder, ownRetryAttr, iface, compilation,
                    errorType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), location);
            }
```

e. Replace line 424, `            var argListWithToken = Arguments(member.Parameters, replaceCancellationToken: true);`, with:

```csharp
            // What RetryWhen, RetryOnException and DelayHint resolve to for this method. ZR0010 is
            // reported for the interface's own methods and for inherited ones under the
            // interface's [Retry]; an inherited method's own [Retry] is the base interface's to
            // report.
            var retryMembers = retry is null
                ? RetryMembers.None
                : ResolveRetryMembers(diagnosticsBuilder, iface, compilation, member, retry, errorType,
                    methodLevel: ownRetry is not null,
                    report: entry.IsOwn || ownRetry is null,
                    location);

            var argListWithToken = Arguments(member.Parameters, replaceCancellationToken: true);
```

f. In the `new MethodModel(...)` call, replace the last argument line, `HelperName: declarationIndex == 1 ? member.Name : member.Name + declarationIndex.ToString(CultureInfo.InvariantCulture)));`, with:

```csharp
                HelperName: declarationIndex == 1 ? member.Name : member.Name + declarationIndex.ToString(CultureInfo.InvariantCulture),
                RetryWhenMethod: retryMembers.RetryWhen,
                RetryOnExceptionMethod: retryMembers.RetryOnException,
                ResultDelayHintMethod: retryMembers.ResultDelayHint,
                ExceptionDelayHintMethod: retryMembers.ExceptionDelayHint));
```

g. Replace `EscapedName`, lines 784-787, with:

```csharp
    private static string EscapedName(IParameterSymbol parameter) => EscapeIdentifier(parameter.Name);

    // "@checked" for a name that is a C# keyword.
    private static string EscapeIdentifier(string name) =>
        Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(name) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None
            ? "@" + name
            : name;
```

h. Replace `ParseRetry`, lines 1095-1104, with:

```csharp
    private static RetryConfig? ParseRetry(AttributeData? attr)
    {
        if (attr is null) return null;
        return new RetryConfig(
            MaxAttempts: GetInt(attr, "MaxAttempts", 3),
            BackoffMs: GetInt(attr, "BackoffMs", 200),
            Jitter: GetBool(attr, "Jitter", false),
            PerAttemptTimeoutMs: GetInt(attr, "PerAttemptTimeoutMs", 0),
            NonThrowing: GetBool(attr, "NonThrowing", false),
            MaxDelayMs: TryGetInt(attr, "MaxDelayMs", out var maxDelayMs) ? maxDelayMs : null,
            RetryWhen: GetString(attr, "RetryWhen"),
            RetryOnException: GetString(attr, "RetryOnException"),
            DelayHint: GetString(attr, "DelayHint"));
    }
```

i. Replace `RetryRules`, lines 1181-1184, with:

```csharp
    private static readonly ImmutableArray<AttributeRule> RetryRules = ImmutableArray.Create(
        new AttributeRule("MaxAttempts", 1, Exclusive: false, "at least 1"),
        new AttributeRule("BackoffMs", 0, Exclusive: false, "at least 0"),
        new AttributeRule("PerAttemptTimeoutMs", 0, Exclusive: false, "at least 0"),
        new AttributeRule("MaxDelayMs", 0, Exclusive: false, "at least 0"));
```

j. In `ValidateAttributeValues`, replace

```csharp
        var location = attr.ApplicationSyntaxReference is { } syntaxRef
            ? Location.Create(syntaxRef.SyntaxTree, syntaxRef.Span)
            : fallbackLocation;
```

with `var location = AttributeLocation(attr, fallbackLocation);`, and add after the method:

```csharp
    // Where the attribute is written, falling back to the member's own location when there is no
    // syntax reference, such as for an attribute read from metadata.
    private static Location? AttributeLocation(AttributeData attr, Location? fallbackLocation) =>
        attr.ApplicationSyntaxReference is { } syntaxRef
            ? Location.Create(syntaxRef.SyntaxTree, syntaxRef.Span)
            : fallbackLocation;
```

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet test tests/ZeroAlloc.Resilience.Generator.Tests/ZeroAlloc.Resilience.Generator.Tests.csproj -c Release --filter "FullyQualifiedName~RetryMemberDiagnosticTests|FullyQualifiedName~InvalidAttributeValueTests|FullyQualifiedName~PolicySetGenerationTests"`
Expected: `Passed!`, `Failed: 0`.

- [ ] **Step 10: Run the full suite; snapshots must not change**

Run the "Full suite, mirrors CI" command.
Expected: `0 Warning(s)`, every test project `Passed!`, and `git status --short -- tests/ZeroAlloc.Resilience.Generator.Tests/Snapshots` prints nothing. The writer does not read the new `MethodModel` fields yet, so a `[Retry(RetryWhen = ...)]` compiles and keeps exception-only retry until Task 4.

- [ ] **Step 11: Commit**

```bash
git add src/ZeroAlloc.Resilience.Generator tests/ZeroAlloc.Resilience.Generator.Tests
git commit -F - <<'EOF'
feat(generator): resolve RetryWhen, RetryOnException and DelayHint, with ZR0009 and ZR0010

Each name resolves to a static method on the interface or a base interface, looked up like
Fallback, accessible from the proxy, and called through its declaring interface.
ZR0009 reports a name with no method of the required shape, at the attribute. ZR0010 reports
RetryWhen, or a DelayHint overload that takes the Result error type, on a method it cannot
apply to: an error for a method-level [Retry], a warning per method for an interface-level one.
MaxDelayMs gets a ZR0004 rule and, when set, the five-argument RetryPolicy default.
TestHelper references ZeroAlloc.Results explicitly, so Result types resolve in isolated runs.

Refs #142, #143

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 4: Emit the Result-aware loop, the exception predicate and the delay hints

**Files:**
- Modify: `src/ZeroAlloc.Resilience.Generator/ResilienceWriter.cs`: `WriteMethodBody` retry branch (lines 234-242 in 3.1.0), `WriteRetryLoop` (Task 2's version), `WriteBackoffWait` (add a parameter); add `WriteResultAwareRetryLoop` and `WriteRetryExhaustion`
- Modify: `tests/ZeroAlloc.Resilience.Generator.Tests/SnapshotTests.cs`
- Create (accepted, reviewed): four `Snapshots/SnapshotTests.ResultAwareRetry_*` and `SnapshotTests.RetryOnException_*` files listed in Step 6
- Create: `tests/ZeroAlloc.Resilience.Generator.Tests/ResultAwareRetryTests.cs`, `tests/ZeroAlloc.Resilience.Tests/ResultAwareRetryIntegrationTests.cs`
- Modify: `tests/ZeroAlloc.Resilience.Tests/ResultReturnTypeIntegrationTests.cs:198`

**Interfaces:**
- Consumes: Task 1's `RetryPolicy.GetDelayMs(int, TimeSpan?)`, `CircuitBreakerPolicy.OnFailure()`, `RetryPolicy.MaxDelayMs`; Task 2's `WriteAttemptToken`, `WriteCallerCancellationCatch`, `WriteBackoffWait`; Task 3's `MethodModel.RetryWhenMethod`, `RetryOnExceptionMethod`, `ResultDelayHintMethod`, `ExceptionDelayHintMethod`.
- Produces: the emitted code shown in Step 5. `WriteBackoffWait` becomes `WriteBackoffWait(StringBuilder sb, MethodModel method, bool hasTotalTimeout, string delayExpr, string indent, bool timeoutEndsLoop)`.

- [ ] **Step 1: Write the failing generator tests**

Create `tests/ZeroAlloc.Resilience.Generator.Tests/ResultAwareRetryTests.cs`:

```csharp
using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Resilience.Generator.Tests;

// #142 and #143: the emitted Result-aware loop compiles for every Result shape and policy
// combination, and keeps the predicates and hints out of the try.
public class ResultAwareRetryTests
{
    private const string Usings = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using ZeroAlloc.Resilience;
        using ZeroAlloc.Results;
        namespace Repro;
        public sealed class HttpError { public int Status { get; init; } }

        """;

    private static string GeneratedSource(Compilation compilation) =>
        string.Join("\n", compilation.SyntaxTrees
            .Where(static t => t.FilePath.EndsWith(".g.cs", StringComparison.Ordinal))
            .Select(static t => t.ToString()))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    [Theory]
    [InlineData("ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);", "")]
    [InlineData("Task<Result<string, HttpError>> GetAsync(CancellationToken ct);", "")]
    [InlineData("ValueTask<UnitResult<HttpError>> GetAsync(CancellationToken ct);", "")]
    [InlineData("Result<string, HttpError> Get(CancellationToken ct);", "")]
    [InlineData("UnitResult<HttpError> Get(CancellationToken ct);", "")]
    [InlineData("Result<string, HttpError> Get();", "")]
    [InlineData("ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);", "[Timeout(Ms = 1000)]")]
    [InlineData("Result<string, HttpError> Get(CancellationToken ct);", "[Timeout(Ms = 1000)]")]
    [InlineData("UnitResult<HttpError> Get(CancellationToken ct);", "[Timeout(Ms = 1000)] [CircuitBreaker(MaxFailures = 3)]")]
    public void ForeignErrorType_ResultAwareLoop_Compiles(string method, string policies)
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + $$"""
            [Retry(MaxAttempts = 3, BackoffMs = 10, RetryWhen = nameof(IsTransient),
                   RetryOnException = nameof(IsTransientException), DelayHint = nameof(RetryAfter))]
            {{policies}}
            public interface IApi
            {
                {{method}}
                static bool IsTransient(HttpError error) => error.Status == 429;
                static bool IsTransientException(Exception exception) => true;
                static TimeSpan? RetryAfter(HttpError error) => null;
                static TimeSpan? RetryAfter(Exception exception) => null;
            }
            """);

        errors.Should().BeEmpty();
        GeneratedSource(compilation).Should().Contain("__lastWasResult");
    }

    [Theory]
    [InlineData("ValueTask<Result<string>>", "string")]
    [InlineData("Task<Result>", "string")]
    [InlineData("ValueTask<Result<string, ResilienceError>>", "ResilienceError")]
    [InlineData("ValueTask<UnitResult<ResilienceError>>", "ResilienceError")]
    public void BuildableErrorType_WithEveryPolicy_Compiles(string returnType, string errorType)
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + $$"""
            [Retry(MaxAttempts = 3, BackoffMs = 10, RetryWhen = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
            [Timeout(Ms = 1000)]
            [RateLimit(MaxPerSecond = 10, BurstSize = 10)]
            [CircuitBreaker(MaxFailures = 3, ResetMs = 500)]
            public interface IApi
            {
                {{returnType}} GetAsync(CancellationToken ct);
                static bool IsTransient({{errorType}} error) => true;
                static TimeSpan? RetryAfter({{errorType}} error) => null;
            }
            """);

        errors.Should().BeEmpty();
        GeneratedSource(compilation).Should().Contain("return __lastResult;");
    }

    [Fact]
    public void Predicate_And_Hints_RunOutsideTheTry()
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + """
            [Retry(RetryWhen = nameof(IsTransient), RetryOnException = nameof(IsTransientException), DelayHint = nameof(RetryAfter))]
            public interface IApi
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
                static bool IsTransient(HttpError error) => true;
                static bool IsTransientException(Exception exception) => true;
                static TimeSpan? RetryAfter(HttpError error) => null;
                static TimeSpan? RetryAfter(Exception exception) => null;
            }
            """);

        errors.Should().BeEmpty();
        var generated = GeneratedSource(compilation);
        generated.Should().Contain("""
                        try
                        {
                            __lastResult = await _inner.GetAsync(__ct).ConfigureAwait(false);
                            __lastWasResult = true;
                        }
                        catch (global::System.OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (global::System.Exception __ex)
            """.Replace("\r\n", "\n", StringComparison.Ordinal));
        var tryBlockEnd = generated.IndexOf("__lastWasResult = true;", StringComparison.Ordinal);
        generated.IndexOf("global::Repro.IApi.IsTransient(__lastResult.Error)", StringComparison.Ordinal).Should().BeGreaterThan(tryBlockEnd);
        generated.IndexOf("global::Repro.IApi.IsTransientException(__ex)", StringComparison.Ordinal).Should().BeGreaterThan(tryBlockEnd);
        generated.IndexOf("global::Repro.IApi.RetryAfter(__ex)", StringComparison.Ordinal).Should().BeGreaterThan(tryBlockEnd);
    }

    [Fact]
    public void MixedInterface_OnlyTheResultMethodGetsTheResultAwareLoop()
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + """
            [Retry(RetryWhen = nameof(IsTransient))]
            public interface IApi
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
                ValueTask<string> GetTextAsync(CancellationToken ct);
                static bool IsTransient(HttpError error) => true;
            }
            """);

        errors.Should().BeEmpty();
        GeneratedSource(compilation).Split("__lastWasResult = true;").Length.Should().Be(2);
    }

    [Fact]
    public void MembersOfABaseInterface_AreCalledThroughIt()
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + """
            public interface IRetryRules
            {
                static bool IsTransient(HttpError error) => error.Status == 429;
                static TimeSpan? RetryAfter(HttpError error) => null;
            }
            [Retry(RetryWhen = nameof(IRetryRules.IsTransient), DelayHint = nameof(IRetryRules.RetryAfter))]
            public interface IApi : IRetryRules
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
            }
            """);

        errors.Should().BeEmpty();
        GeneratedSource(compilation).Should()
            .Contain("global::Repro.IRetryRules.IsTransient(__lastResult.Error)")
            .And.Contain("__hint = global::Repro.IRetryRules.RetryAfter(__lastResult.Error);");
    }

    [Fact]
    public void NoNewProperty_KeepsTheExceptionOnlyLoop()
    {
        var (compilation, errors) = TestHelper.RunAndCompile(Usings + """
            [Retry(MaxAttempts = 3)]
            public interface IApi
            {
                ValueTask<Result<string, HttpError>> GetAsync(CancellationToken ct);
            }
            """);

        errors.Should().BeEmpty();
        GeneratedSource(compilation).Should()
            .NotContain("__lastWasResult")
            .And.NotContain("GetDelayMs")
            .And.Contain("_retry.GetBackoffMs(__attempt)");
    }
}
```

Append to the `SnapshotTests` class in `tests/ZeroAlloc.Resilience.Generator.Tests/SnapshotTests.cs`:

```csharp
    [Fact]
    public void ResultAwareRetry_Async_DelayHintOverloads_GeneratesProxy()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            using ZeroAlloc.Results;
            namespace T;
            public sealed class HttpError { public int Status { get; init; } }
            [Retry(MaxAttempts = 3, BackoffMs = 100, RetryWhen = nameof(IsTransient),
                   RetryOnException = nameof(IsTransientException), DelayHint = nameof(RetryAfter))]
            public interface IApi
            {
                ValueTask<Result<string, HttpError>> GetAsync(string id, CancellationToken ct);
                static bool IsTransient(HttpError error) => error.Status is 429 or >= 500;
                static bool IsTransientException(Exception exception) => exception is not ArgumentException;
                static TimeSpan? RetryAfter(HttpError error) => null;
                static TimeSpan? RetryAfter(Exception exception) => null;
            }
            """;
        TestHelper.Verify<ResilienceGenerator>(source);
    }

    [Fact]
    public void ResultAwareRetry_Async_TimeoutAndCircuitBreaker_GeneratesProxy()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            using ZeroAlloc.Results;
            namespace T;
            [Retry(MaxAttempts = 3, BackoffMs = 100, RetryWhen = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
            [Timeout(Ms = 5000)]
            [CircuitBreaker(MaxFailures = 5, ResetMs = 1000)]
            public interface IApi
            {
                Task<Result<string>> GetAsync(string id, CancellationToken ct);
                static bool IsTransient(string error) => error.StartsWith("429", StringComparison.Ordinal);
                static TimeSpan? RetryAfter(string error) => null;
            }
            """;
        TestHelper.Verify<ResilienceGenerator>(source);
    }

    [Fact]
    public void ResultAwareRetry_Sync_UnitResult_GeneratesProxy()
    {
        var source = """
            using System;
            using System.Threading;
            using ZeroAlloc.Resilience;
            using ZeroAlloc.Results;
            namespace T;
            public sealed class HttpError { public int Status { get; init; } }
            [Retry(MaxAttempts = 3, BackoffMs = 100, RetryWhen = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
            [CircuitBreaker(MaxFailures = 5, ResetMs = 1000)]
            public interface IApi
            {
                [Timeout(Ms = 5000)]
                UnitResult<HttpError> Send(CancellationToken ct);
                Result<int, HttpError> Count();
                static bool IsTransient(HttpError error) => error.Status == 429;
                static TimeSpan? RetryAfter(HttpError error) => null;
                static TimeSpan? RetryAfter(Exception exception) => null;
            }
            """;
        TestHelper.Verify<ResilienceGenerator>(source);
    }

    [Fact]
    public void RetryOnException_ExceptionHint_NonResult_GeneratesProxy()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using ZeroAlloc.Resilience;
            namespace T;
            [Retry(MaxAttempts = 3, BackoffMs = 100, RetryOnException = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
            public interface IApi
            {
                ValueTask<string> GetAsync(string id, CancellationToken ct);
                string Get(string id);
                static bool IsTransient(Exception exception) => exception is not ArgumentException;
                static TimeSpan? RetryAfter(Exception exception) => null;
            }
            """;
        TestHelper.Verify<ResilienceGenerator>(source);
    }
```

- [ ] **Step 2: Write the failing runtime tests**

Create `tests/ZeroAlloc.Resilience.Tests/ResultAwareRetryIntegrationTests.cs`:

```csharp
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Results;

namespace ZeroAlloc.Resilience.Tests;

// #142 and #143: a failed Result that RetryWhen calls transient is retried, and DelayHint takes
// the wait from the failure. BackoffMs is 10 s on the main interface, so a test that finishes
// quickly proves the hint, not the backoff, set the wait.

public readonly record struct ApiError(int Status, int RetryAfterMs = -1, int Attempt = 0);

public sealed class HintedException(int retryAfterMs) : Exception("hinted")
{
    public int RetryAfterMs { get; } = retryAfterMs;
}

// 429 and 503 are transient; 418 makes the predicate itself throw.
public static class ApiRetryRules
{
    public static bool IsTransient(ApiError error) => error.Status switch
    {
        418 => throw new InvalidOperationException("RetryWhen threw"),
        429 or 503 => true,
        _ => false,
    };

    public static bool IsTransientException(Exception exception) => exception is not ArgumentException;

    public static TimeSpan? RetryAfter(ApiError error) =>
        error.RetryAfterMs >= 0 ? TimeSpan.FromMilliseconds(error.RetryAfterMs) : null;

    public static TimeSpan? RetryAfter(Exception exception) =>
        exception is HintedException hinted ? TimeSpan.FromMilliseconds(hinted.RetryAfterMs) : null;
}

[Retry(MaxAttempts = 3, BackoffMs = 10_000, RetryWhen = nameof(IsTransient),
       RetryOnException = nameof(IsTransientException), DelayHint = nameof(RetryAfter))]
public interface IResultAwareApi
{
    ValueTask<Result<string, ApiError>> GetAsync(CancellationToken ct);
    Result<string, ApiError> Get(CancellationToken ct);
    ValueTask<UnitResult<ApiError>> SendAsync(CancellationToken ct);

    static bool IsTransient(ApiError error) => ApiRetryRules.IsTransient(error);
    static bool IsTransientException(Exception exception) => ApiRetryRules.IsTransientException(exception);
    static TimeSpan? RetryAfter(ApiError error) => ApiRetryRules.RetryAfter(error);
    static TimeSpan? RetryAfter(Exception exception) => ApiRetryRules.RetryAfter(exception);
}

[Retry(MaxAttempts = 3, BackoffMs = 1, RetryWhen = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
[Timeout(Ms = 200)]
public interface ITimedResultAwareApi
{
    ValueTask<Result<string, ApiError>> GetAsync(CancellationToken ct);
    Result<string, ApiError> Get(CancellationToken ct);

    static bool IsTransient(ApiError error) => ApiRetryRules.IsTransient(error);
    static TimeSpan? RetryAfter(ApiError error) => ApiRetryRules.RetryAfter(error);
}

[Retry(MaxAttempts = 1, RetryWhen = nameof(IsTransient))]
[CircuitBreaker(MaxFailures = 2, ResetMs = 60_000, Fallback = nameof(GetFallbackAsync))]
public interface IBreakerResultAwareApi
{
    ValueTask<Result<string, ApiError>> GetAsync(CancellationToken ct);
    ValueTask<Result<string, ApiError>> GetFallbackAsync(CancellationToken ct);

    static bool IsTransient(ApiError error) => ApiRetryRules.IsTransient(error);
}

[Retry(MaxAttempts = 2, MaxDelayMs = 250)]
public interface IMaxDelayApi
{
    ValueTask<string> GetAsync(CancellationToken ct);
}

// Each call asks the script for call number n: an error to fail with, null to succeed, or an
// exception it throws.
public sealed class ScriptedApi(Func<int, ApiError?> script) : IResultAwareApi, ITimedResultAwareApi, IBreakerResultAwareApi
{
    public int Calls { get; private set; }

    private Result<string, ApiError> Next()
    {
        Calls++;
        return script(Calls) is { } error
            ? Result<string, ApiError>.Failure(error with { Attempt = Calls })
            : Result<string, ApiError>.Success("ok");
    }

    public ValueTask<Result<string, ApiError>> GetAsync(CancellationToken ct) => ValueTask.FromResult(Next());

    public Result<string, ApiError> Get(CancellationToken ct) => Next();

    public ValueTask<UnitResult<ApiError>> SendAsync(CancellationToken ct)
    {
        var result = Next();
        return ValueTask.FromResult(result.IsSuccess ? UnitResult<ApiError>.Success() : UnitResult<ApiError>.Failure(result.Error));
    }

    public ValueTask<Result<string, ApiError>> GetFallbackAsync(CancellationToken ct) =>
        ValueTask.FromResult(Result<string, ApiError>.Success("fallback"));
}

public class ResultAwareRetryIntegrationTests
{
    private static ApiError? FailFirst(int call, ApiError error) => call == 1 ? error : (ApiError?)null;

    private static IResultAwareApi Proxy(ScriptedApi inner, RetryPolicy? retry = null) =>
        new IResultAwareApiResilienceProxy(inner, retry is null
            ? new ResultAwareApiResiliencePolicies()
            : new ResultAwareApiResiliencePolicies { Retry = retry });

    [Fact]
    public async Task TransientFailureThenSuccess_TakesTwoAttempts()
    {
        var inner = new ScriptedApi(call => FailFirst(call, new ApiError(429, RetryAfterMs: 0)));

        var result = await Proxy(inner).GetAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task NonTransientFailure_IsReturnedAfterOneAttempt()
    {
        var inner = new ScriptedApi(static _ => new ApiError(422));

        var result = await Proxy(inner).GetAsync(CancellationToken.None);

        result.Error.Should().Be(new ApiError(422, Attempt: 1));
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task EveryAttemptTransient_ReturnsTheLastFailedResultUnchanged()
    {
        var inner = new ScriptedApi(static _ => new ApiError(429, RetryAfterMs: 0));

        var result = await Proxy(inner).GetAsync(CancellationToken.None);

        result.Error.Should().Be(new ApiError(429, RetryAfterMs: 0, Attempt: 3));
        inner.Calls.Should().Be(3);
    }

    [Fact]
    public async Task ThrowingRetryWhen_Propagates_AndIsNotRetried()
    {
        var inner = new ScriptedApi(static _ => new ApiError(418));

        var act = async () => await Proxy(inner).GetAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("RetryWhen threw");
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Hint_OverridesTheBackoff()
    {
        var inner = new ScriptedApi(call => FailFirst(call, new ApiError(429, RetryAfterMs: 1)));
        var started = Stopwatch.GetTimestamp();

        var result = await Proxy(inner).GetAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        Stopwatch.GetElapsedTime(started).Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Hint_IsCappedByMaxDelayMs()
    {
        var inner = new ScriptedApi(call => FailFirst(call, new ApiError(429, RetryAfterMs: 60_000)));
        var started = Stopwatch.GetTimestamp();

        var result = await Proxy(inner, new RetryPolicy(3, 10_000, false, 0, maxDelayMs: 20)).GetAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        Stopwatch.GetElapsedTime(started).Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExceptionHint_IsUsedOnTheExceptionPath()
    {
        var inner = new ScriptedApi(static call => call == 1 ? throw new HintedException(1) : (ApiError?)null);
        var started = Stopwatch.GetTimestamp();

        var result = await Proxy(inner).GetAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        inner.Calls.Should().Be(2);
        Stopwatch.GetElapsedTime(started).Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RetryOnExceptionFalse_StopsRetrying()
    {
        var inner = new ScriptedApi(static _ => throw new ArgumentException("permanent"));

        var act = async () => await Proxy(inner).GetAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<ResilienceException>()).WithInnerException<ArgumentException>();
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Breaker_OpensOnTransientFailedResults()
    {
        var inner = new ScriptedApi(static _ => new ApiError(429));
        using var cb = new CircuitBreakerPolicy(maxFailures: 2, resetMs: 60_000, halfOpenProbes: 1);
        var proxy = new IBreakerResultAwareApiResilienceProxy(inner, new BreakerResultAwareApiResiliencePolicies { CircuitBreaker = cb });

        await proxy.GetAsync(CancellationToken.None);
        await proxy.GetAsync(CancellationToken.None);
        var third = await proxy.GetAsync(CancellationToken.None);

        cb.State.Should().Be(CircuitBreakerState.Open);
        third.Value.Should().Be("fallback");
        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Breaker_StaysClosedOnNonTransientFailedResults()
    {
        var inner = new ScriptedApi(static _ => new ApiError(422));
        using var cb = new CircuitBreakerPolicy(maxFailures: 2, resetMs: 60_000, halfOpenProbes: 1);
        var proxy = new IBreakerResultAwareApiResilienceProxy(inner, new BreakerResultAwareApiResiliencePolicies { CircuitBreaker = cb });

        for (var i = 0; i < 3; i++)
            (await proxy.GetAsync(CancellationToken.None)).Error.Status.Should().Be(422);

        cb.State.Should().Be(CircuitBreakerState.Closed);
        inner.Calls.Should().Be(3);
    }

    [Fact]
    public async Task TotalTimeout_InterruptsALongHintedWait_AndReturnsTheLastResult()
    {
        var inner = new ScriptedApi(static _ => new ApiError(429, RetryAfterMs: 60_000));
        var proxy = new ITimedResultAwareApiResilienceProxy(inner, new TimedResultAwareApiResiliencePolicies());
        var started = Stopwatch.GetTimestamp();

        var result = await proxy.GetAsync(CancellationToken.None);

        result.Error.Should().Be(new ApiError(429, RetryAfterMs: 60_000, Attempt: 1));
        inner.Calls.Should().Be(1);
        Stopwatch.GetElapsedTime(started).Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Sync_TotalTimeout_InterruptsALongHintedWait_AndReturnsTheLastResult()
    {
        var inner = new ScriptedApi(static _ => new ApiError(429, RetryAfterMs: 60_000));
        var proxy = new ITimedResultAwareApiResilienceProxy(inner, new TimedResultAwareApiResiliencePolicies());
        var started = Stopwatch.GetTimestamp();

        var result = proxy.Get(CancellationToken.None);

        result.Error.Should().Be(new ApiError(429, RetryAfterMs: 60_000, Attempt: 1));
        Stopwatch.GetElapsedTime(started).Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Sync_TransientFailureThenSuccess_TakesTwoAttempts()
    {
        var inner = new ScriptedApi(call => FailFirst(call, new ApiError(503, RetryAfterMs: 0)));

        var result = Proxy(inner).Get(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task UnitResult_TransientFailureThenSuccess_TakesTwoAttempts()
    {
        var inner = new ScriptedApi(call => FailFirst(call, new ApiError(429, RetryAfterMs: 0)));

        var result = await Proxy(inner).SendAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        inner.Calls.Should().Be(2);
    }

    [Fact(Timeout = 10_000)]
    public async Task CallerCancellation_DuringAHintedWait_ThrowsOperationCanceledException()
    {
        var inner = new ScriptedApi(static _ => new ApiError(429, RetryAfterMs: 60_000));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = async () => await Proxy(inner).GetAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public void MaxDelayMs_FromTheAttribute_IsThePolicyDefault() =>
        new MaxDelayApiResiliencePolicies().Retry.MaxDelayMs.Should().Be(250);
}
```

In `tests/ZeroAlloc.Resilience.Tests/ResultReturnTypeIntegrationTests.cs`, line 198, the reason text is now wrong; change

```csharp
        inner.CallCount.Should().Be(1, "a returned failure is not retried until RetryWhen exists, see #142");
```

to

```csharp
        inner.CallCount.Should().Be(1, "without RetryWhen a returned failure is passed through, not retried");
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/ZeroAlloc.Resilience.Generator.Tests/ZeroAlloc.Resilience.Generator.Tests.csproj -c Release --filter "FullyQualifiedName~ResultAwareRetryTests|FullyQualifiedName~SnapshotTests"`
Expected: FAIL. The compile-matrix theories fail on `Contain("__lastWasResult")` or `Contain("return __lastResult;")`; `Predicate_And_Hints_RunOutsideTheTry`, `MembersOfABaseInterface_AreCalledThroughIt` and `MixedInterface_OnlyTheResultMethodGetsTheResultAwareLoop` fail on content; the four new snapshot tests fail with `missing snapshot`. `NoNewProperty_KeepsTheExceptionOnlyLoop` and the six existing snapshot tests already pass.

Run: `dotnet test tests/ZeroAlloc.Resilience.Tests/ZeroAlloc.Resilience.Tests.csproj -c Release --filter "FullyQualifiedName~ResultAwareRetryIntegrationTests"`
Expected: FAIL. `TransientFailureThenSuccess_TakesTwoAttempts` and the other retry tests see `Calls` 1; `ThrowingRetryWhen_Propagates_AndIsNotRetried` sees no exception; `ExceptionHint_IsUsedOnTheExceptionPath` exceeds 5 s; `RetryOnExceptionFalse_StopsRetrying` sees 3 calls. `NonTransientFailure_IsReturnedAfterOneAttempt`, `Breaker_StaysClosedOnNonTransientFailedResults` and `MaxDelayMs_FromTheAttribute_IsThePolicyDefault` already pass.

- [ ] **Step 4: Add the timeout parameter to `WriteBackoffWait`**

In `src/ZeroAlloc.Resilience.Generator/ResilienceWriter.cs`, change the signature to

```csharp
    private static void WriteBackoffWait(StringBuilder sb, MethodModel method, bool hasTotalTimeout, string delayExpr, string indent, bool timeoutEndsLoop)
```

add to its comment: `timeoutEndsLoop: async only. The Result-aware loop suppresses the Task.Delay exception and ends the loop when the total timeout fired, so it can return the last Result; the exception-only loop keeps the throwing Task.Delay it has always had.` and replace its `if (method.IsAsync)` block with:

```csharp
        if (method.IsAsync)
        {
            if (hasTotalTimeout && timeoutEndsLoop)
            {
                sb.AppendLine($"{indent}await global::System.Threading.Tasks.Task.Delay({delayExpr}, __totalCts.Token).ConfigureAwait(global::System.Threading.Tasks.ConfigureAwaitOptions.SuppressThrowing);");
                sb.AppendLine($"{indent}if (__totalCts.IsCancellationRequested)");
                sb.AppendLine($"{indent}{{");
                if (callerToken is not null)
                    sb.AppendLine($"{indent}    {callerToken}.ThrowIfCancellationRequested();");
                sb.AppendLine($"{indent}    break;");
                sb.AppendLine($"{indent}}}");
                return;
            }

            var tokenArg = hasTotalTimeout ? ", __totalCts.Token" : callerToken is not null ? $", {callerToken}" : "";
            sb.AppendLine($"{indent}await global::System.Threading.Tasks.Task.Delay({delayExpr}{tokenArg}).ConfigureAwait(false);");
            return;
        }
```

- [ ] **Step 5: Emit the exception predicate and hint, and the Result-aware loop**

a. In `WriteMethodBody`, replace

```csharp
        if (method.Retry is not null)
        {
            WriteRetryLoop(sb, method, hasTotalTimeout);
        }
```

with

```csharp
        if (method.Retry is not null)
        {
            if (method.RetryWhenMethod is not null)
                WriteResultAwareRetryLoop(sb, method, hasTotalTimeout);
            else
                WriteRetryLoop(sb, method, hasTotalTimeout);
        }
```

b. In `WriteRetryLoop`, replace the catch body lines from `if (method.CircuitBreaker is not null)` after `__lastEx = __ex;` through the `WriteBackoffWait(...)` call with:

```csharp
        if (method.CircuitBreaker is not null)
            sb.AppendLine($"                {method.CircuitBreakerSlot!.FieldName}.OnFailure(__ex);");
        // RetryOnException and the Exception overload of DelayHint run in the catch block, which
        // is outside the try it guards: an exception they throw reaches the caller unchanged.
        if (method.RetryOnExceptionMethod is not null)
            sb.AppendLine($"                if (!{method.RetryOnExceptionMethod}(__ex)) break;");
        var delay = $"{retry}.GetBackoffMs(__attempt)";
        if (method.ExceptionDelayHintMethod is not null)
        {
            sb.AppendLine($"                var __hint = {method.ExceptionDelayHintMethod}(__ex);");
            delay = $"{retry}.GetDelayMs(__attempt, __hint)";
        }
        if (hasTotalTimeout)
            sb.AppendLine("                if (__totalCts.IsCancellationRequested) break;");
        sb.AppendLine($"                if (__attempt == {retry}.MaxAttempts - 1) break;");
        WriteBackoffWait(sb, method, hasTotalTimeout, delay, "                ", timeoutEndsLoop: false);
```

c. In `WriteRetryLoop`, replace everything from `sb.AppendLine("        // All attempts exhausted");` to the end of the method with:

```csharp
        sb.AppendLine("        // All attempts exhausted");
        WriteRetryExhaustion(sb, method);
    }

    // Every attempt threw, or RetryOnException declined the last exception.
    private static void WriteRetryExhaustion(StringBuilder sb, MethodModel method)
    {
        // NonThrowing on a return type that cannot hold a ResilienceError never reaches the writer:
        // it is ZR0003, or ZR0006 with NonThrowing left off an inherited default method.
        if (method.ReturnsFailureResult)
        {
            // Result return types get a failure of their own type instead of an exception.
            sb.AppendLine($"        return {FailureExpression(method, "\"Retry\"", "__lastEx?.Message ?? \"All retry attempts failed.\"", "__lastEx")};");
        }
        else
        {
            // Also Result<T, E> with a foreign E: every attempt threw, so there is no inner
            // Result to return and no way to build an E.
            sb.AppendLine("        throw new global::ZeroAlloc.Resilience.ResilienceException(global::ZeroAlloc.Resilience.ResiliencePolicy.Retry, \"All retry attempts failed.\", __lastEx);");
        }
    }
```

d. Add after `WriteRetryExhaustion`:

```csharp
    // [Retry] with a RetryWhen that applies to this Result method. Only the inner call is inside
    // the try; RetryWhen and the Result DelayHint run after it, and RetryOnException and the
    // Exception DelayHint in the catch block, so an exception any of them throws reaches the
    // caller unchanged. A failed Result RetryWhen calls transient is a breaker failure and is
    // retried; any other returned Result is a breaker success and is returned. When the retries
    // end on a failed Result, that Result is returned unchanged.
    private static void WriteResultAwareRetryLoop(StringBuilder sb, MethodModel method, bool hasTotalTimeout)
    {
        var retry = method.RetrySlot!.FieldName;
        var awaitKw = method.IsAsync ? "await " : "";
        var configKw = method.IsAsync ? ".ConfigureAwait(false)" : "";
        var breaker = method.CircuitBreaker is null ? null : method.CircuitBreakerSlot!.FieldName;
        var hasHint = method.ResultDelayHintMethod is not null || method.ExceptionDelayHintMethod is not null;

        sb.AppendLine("        global::System.Exception? __lastEx = null;");
        sb.AppendLine($"        {method.ResultTypeFqn} __lastResult = default;");
        sb.AppendLine("        bool __lastWasResult = false;");
        sb.AppendLine($"        for (int __attempt = 0; __attempt < {retry}.MaxAttempts; __attempt++)");
        sb.AppendLine("        {");
        WriteAttemptToken(sb, method, retry, hasTotalTimeout);
        if (hasHint)
            sb.AppendLine("            global::System.TimeSpan? __hint = null;");
        sb.AppendLine("            try");
        sb.AppendLine("            {");
        var callArgs = method.HasCancellationToken ? method.ArgumentListWithToken : method.ArgumentList;
        sb.AppendLine($"                __lastResult = {awaitKw}{InnerCall(method, callArgs)}{configKw};");
        sb.AppendLine("                __lastWasResult = true;");
        sb.AppendLine("            }");
        WriteCallerCancellationCatch(sb, method);
        sb.AppendLine("            catch (global::System.Exception __ex)");
        sb.AppendLine("            {");
        sb.AppendLine("                __lastEx = __ex;");
        sb.AppendLine("                __lastWasResult = false;");
        if (breaker is not null)
            sb.AppendLine($"                {breaker}.OnFailure(__ex);");
        if (method.RetryOnExceptionMethod is not null)
            sb.AppendLine($"                if (!{method.RetryOnExceptionMethod}(__ex)) break;");
        if (method.ExceptionDelayHintMethod is not null)
            sb.AppendLine($"                __hint = {method.ExceptionDelayHintMethod}(__ex);");
        sb.AppendLine("            }");
        sb.AppendLine("            if (__lastWasResult)");
        sb.AppendLine("            {");
        sb.AppendLine($"                if (__lastResult.IsSuccess || !{method.RetryWhenMethod}(__lastResult.Error))");
        sb.AppendLine("                {");
        if (breaker is not null)
            sb.AppendLine($"                    {breaker}.OnSuccess();");
        sb.AppendLine("                    return __lastResult;");
        sb.AppendLine("                }");
        if (breaker is not null)
            sb.AppendLine($"                {breaker}.OnFailure();");
        if (method.ResultDelayHintMethod is not null)
            sb.AppendLine($"                __hint = {method.ResultDelayHintMethod}(__lastResult.Error);");
        sb.AppendLine("            }");
        if (hasTotalTimeout)
            sb.AppendLine("            if (__totalCts.IsCancellationRequested) break;");
        sb.AppendLine($"            if (__attempt == {retry}.MaxAttempts - 1) break;");
        var delay = hasHint ? $"{retry}.GetDelayMs(__attempt, __hint)" : $"{retry}.GetBackoffMs(__attempt)";
        WriteBackoffWait(sb, method, hasTotalTimeout, delay, "            ", timeoutEndsLoop: true);
        sb.AppendLine("        }");
        sb.AppendLine("        // All attempts exhausted, or a failure that is not retried");
        sb.AppendLine("        if (__lastWasResult) return __lastResult;");
        WriteRetryExhaustion(sb, method);
    }
```

- [ ] **Step 6: Run the generator tests; accept and review the four new snapshots**

Run: `dotnet test tests/ZeroAlloc.Resilience.Generator.Tests/ZeroAlloc.Resilience.Generator.Tests.csproj -c Release --filter "FullyQualifiedName~ResultAwareRetryTests|FullyQualifiedName~SnapshotTests"`
Expected: `ResultAwareRetryTests` all pass. Only the four new snapshot tests fail, with `missing snapshot`. The six existing snapshot tests pass: that is the spec's "unchanged apart from the cancellation fix when no new property is used".

Run the "Accept snapshots" command, then the "Review snapshot changes" command. `git status` must list exactly four new files and no modified one:

- `SnapshotTests.ResultAwareRetry_Async_DelayHintOverloads_GeneratesProxy#T_IApi.Resilience.g.verified.cs`
- `SnapshotTests.ResultAwareRetry_Async_TimeoutAndCircuitBreaker_GeneratesProxy#T_IApi.Resilience.g.verified.cs`
- `SnapshotTests.ResultAwareRetry_Sync_UnitResult_GeneratesProxy#T_IApi.Resilience.g.verified.cs`
- `SnapshotTests.RetryOnException_ExceptionHint_NonResult_GeneratesProxy#T_IApi.Resilience.g.verified.cs`

Read each file. The method bodies must match these exactly, apart from nothing:

**Async, `ResultAwareRetry_Async_DelayHintOverloads`:**

```csharp
    public async global::System.Threading.Tasks.ValueTask<global::ZeroAlloc.Results.Result<string, global::T.HttpError>> GetAsync(string id, global::System.Threading.CancellationToken ct)
    {
        global::System.Exception? __lastEx = null;
        global::ZeroAlloc.Results.Result<string, global::T.HttpError> __lastResult = default;
        bool __lastWasResult = false;
        for (int __attempt = 0; __attempt < _retry.MaxAttempts; __attempt++)
        {
            using var __attemptCts = _retry.PerAttemptTimeoutMs > 0
                ? global::System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            __attemptCts?.CancelAfter(_retry.PerAttemptTimeoutMs);
            var __ct = __attemptCts?.Token ?? ct;
            global::System.TimeSpan? __hint = null;
            try
            {
                __lastResult = await _inner.GetAsync(id, __ct).ConfigureAwait(false);
                __lastWasResult = true;
            }
            catch (global::System.OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (global::System.Exception __ex)
            {
                __lastEx = __ex;
                __lastWasResult = false;
                if (!global::T.IApi.IsTransientException(__ex)) break;
                __hint = global::T.IApi.RetryAfter(__ex);
            }
            if (__lastWasResult)
            {
                if (__lastResult.IsSuccess || !global::T.IApi.IsTransient(__lastResult.Error))
                {
                    return __lastResult;
                }
                __hint = global::T.IApi.RetryAfter(__lastResult.Error);
            }
            if (__attempt == _retry.MaxAttempts - 1) break;
            await global::System.Threading.Tasks.Task.Delay(_retry.GetDelayMs(__attempt, __hint), ct).ConfigureAwait(false);
        }
        // All attempts exhausted, or a failure that is not retried
        if (__lastWasResult) return __lastResult;
        throw new global::ZeroAlloc.Resilience.ResilienceException(global::ZeroAlloc.Resilience.ResiliencePolicy.Retry, "All retry attempts failed.", __lastEx);
    }
```

The policies class line must be `new global::ZeroAlloc.Resilience.RetryPolicy(3, 100, false, 0);`, the four-argument form.

**Async with `[Timeout]` and `[CircuitBreaker]`, `ResultAwareRetry_Async_TimeoutAndCircuitBreaker`:** the total timeout during the hinted wait ends the loop and returns the last Result.

```csharp
    public async global::System.Threading.Tasks.Task<global::ZeroAlloc.Results.Result<string>> GetAsync(string id, global::System.Threading.CancellationToken ct)
    {
        if (!_circuitBreaker.CanExecute())
        {
            return global::ZeroAlloc.Results.Result<string>.Failure("Circuit breaker is open.");
        }

        using var __totalCts = global::System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
        __totalCts.CancelAfter(_timeout.TotalMs);

        global::System.Exception? __lastEx = null;
        global::ZeroAlloc.Results.Result<string> __lastResult = default;
        bool __lastWasResult = false;
        for (int __attempt = 0; __attempt < _retry.MaxAttempts; __attempt++)
        {
            using var __attemptCts = _retry.PerAttemptTimeoutMs > 0
                ? global::System.Threading.CancellationTokenSource.CreateLinkedTokenSource(__totalCts.Token)
                : null;
            __attemptCts?.CancelAfter(_retry.PerAttemptTimeoutMs);
            var __ct = __attemptCts?.Token ?? __totalCts.Token;
            global::System.TimeSpan? __hint = null;
            try
            {
                __lastResult = await _inner.GetAsync(id, __ct).ConfigureAwait(false);
                __lastWasResult = true;
            }
            catch (global::System.OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (global::System.Exception __ex)
            {
                __lastEx = __ex;
                __lastWasResult = false;
                _circuitBreaker.OnFailure(__ex);
            }
            if (__lastWasResult)
            {
                if (__lastResult.IsSuccess || !global::T.IApi.IsTransient(__lastResult.Error))
                {
                    _circuitBreaker.OnSuccess();
                    return __lastResult;
                }
                _circuitBreaker.OnFailure();
                __hint = global::T.IApi.RetryAfter(__lastResult.Error);
            }
            if (__totalCts.IsCancellationRequested) break;
            if (__attempt == _retry.MaxAttempts - 1) break;
            await global::System.Threading.Tasks.Task.Delay(_retry.GetDelayMs(__attempt, __hint), __totalCts.Token).ConfigureAwait(global::System.Threading.Tasks.ConfigureAwaitOptions.SuppressThrowing);
            if (__totalCts.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
                break;
            }
        }
        // All attempts exhausted, or a failure that is not retried
        if (__lastWasResult) return __lastResult;
        return global::ZeroAlloc.Results.Result<string>.Failure(__lastEx?.Message ?? "All retry attempts failed.");
    }
```

**Sync, `ResultAwareRetry_Sync_UnitResult`:** `Send` has a method-level timeout slot `_sendTimeout` and waits on the total-timeout wait handle; `Count` has no token and sleeps.

```csharp
    public global::ZeroAlloc.Results.UnitResult<global::T.HttpError> Send(global::System.Threading.CancellationToken ct)
    {
        if (!_circuitBreaker.CanExecute())
        {
            throw new global::ZeroAlloc.Resilience.ResilienceException(global::ZeroAlloc.Resilience.ResiliencePolicy.CircuitBreaker, "Circuit breaker is open.");
        }

        using var __totalCts = global::System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
        __totalCts.CancelAfter(_sendTimeout.TotalMs);

        global::System.Exception? __lastEx = null;
        global::ZeroAlloc.Results.UnitResult<global::T.HttpError> __lastResult = default;
        bool __lastWasResult = false;
        for (int __attempt = 0; __attempt < _retry.MaxAttempts; __attempt++)
        {
            using var __attemptCts = _retry.PerAttemptTimeoutMs > 0
                ? global::System.Threading.CancellationTokenSource.CreateLinkedTokenSource(__totalCts.Token)
                : null;
            __attemptCts?.CancelAfter(_retry.PerAttemptTimeoutMs);
            var __ct = __attemptCts?.Token ?? __totalCts.Token;
            global::System.TimeSpan? __hint = null;
            try
            {
                __lastResult = _inner.Send(__ct);
                __lastWasResult = true;
            }
            catch (global::System.OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (global::System.Exception __ex)
            {
                __lastEx = __ex;
                __lastWasResult = false;
                _circuitBreaker.OnFailure(__ex);
                __hint = global::T.IApi.RetryAfter(__ex);
            }
            if (__lastWasResult)
            {
                if (__lastResult.IsSuccess || !global::T.IApi.IsTransient(__lastResult.Error))
                {
                    _circuitBreaker.OnSuccess();
                    return __lastResult;
                }
                _circuitBreaker.OnFailure();
                __hint = global::T.IApi.RetryAfter(__lastResult.Error);
            }
            if (__totalCts.IsCancellationRequested) break;
            if (__attempt == _retry.MaxAttempts - 1) break;
            if (__totalCts.Token.WaitHandle.WaitOne(_retry.GetDelayMs(__attempt, __hint)))
            {
                ct.ThrowIfCancellationRequested();
                break;
            }
        }
        // All attempts exhausted, or a failure that is not retried
        if (__lastWasResult) return __lastResult;
        throw new global::ZeroAlloc.Resilience.ResilienceException(global::ZeroAlloc.Resilience.ResiliencePolicy.Retry, "All retry attempts failed.", __lastEx);
    }
```

In `Count`, the loop has no attempt-token lines and no caller-cancellation clause, and the wait is `global::System.Threading.Thread.Sleep(_retry.GetDelayMs(__attempt, __hint));`.

**Exception-only, `RetryOnException_ExceptionHint_NonResult`:** today's loop shape; the catch of `GetAsync` reads

```csharp
            catch (global::System.OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (global::System.Exception __ex)
            {
                __lastEx = __ex;
                if (!global::T.IApi.IsTransient(__ex)) break;
                var __hint = global::T.IApi.RetryAfter(__ex);
                if (__attempt == _retry.MaxAttempts - 1) break;
                await global::System.Threading.Tasks.Task.Delay(_retry.GetDelayMs(__attempt, __hint), ct).ConfigureAwait(false);
            }
```

and `Get` ends its catch with `global::System.Threading.Thread.Sleep(_retry.GetDelayMs(__attempt, __hint));`. Neither method contains `__lastWasResult`.

Any difference from these bodies is a writer bug: fix the writer, never the snapshot. Delete any `*.received.*` file. Run the generator test command from the start of this step again without the environment variable.
Expected: `Passed!`, `Failed: 0`.

- [ ] **Step 7: Run the runtime tests to verify they pass**

Run: `dotnet test tests/ZeroAlloc.Resilience.Tests/ZeroAlloc.Resilience.Tests.csproj -c Release --filter "FullyQualifiedName~ResultAwareRetryIntegrationTests|FullyQualifiedName~ResultReturnTypeIntegrationTests|FullyQualifiedName~SyncAndUnitResultIntegrationTests|FullyQualifiedName~CallerCancellationIntegrationTests"`
Expected: `Passed!`, `Failed: 0`.

- [ ] **Step 8: Run the full suite**

Run the "Full suite, mirrors CI" command.
Expected: `0 Warning(s)`, every test project `Passed!`.

- [ ] **Step 9: Commit**

```bash
git add src/ZeroAlloc.Resilience.Generator/ResilienceWriter.cs \
  tests/ZeroAlloc.Resilience.Generator.Tests/ResultAwareRetryTests.cs \
  tests/ZeroAlloc.Resilience.Generator.Tests/SnapshotTests.cs \
  tests/ZeroAlloc.Resilience.Generator.Tests/Snapshots \
  tests/ZeroAlloc.Resilience.Tests/ResultAwareRetryIntegrationTests.cs \
  tests/ZeroAlloc.Resilience.Tests/ResultReturnTypeIntegrationTests.cs
git commit -F - <<'EOF'
feat(generator): retry a failed Result chosen by RetryWhen and take the delay from the failure

A method returning a Result whose [Retry] has a RetryWhen that applies gets a Result-aware loop.
A failed Result RetryWhen calls transient is retried and counts as a breaker failure; any other
Result is returned and counts as a success. When the retries end on a failed Result, that Result
is returned unchanged. DelayHint replaces the backoff for the next attempt, capped by
MaxDelayMs, and a total timeout during that wait returns the last Result.
RetryOnException and the Exception overload of DelayHint apply to every method with retry.
Predicates and hints run outside the try, so an exception they throw reaches the caller.

Closes #142
Closes #143

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

### Task 5: AOT smoke case and documentation

**Files:**
- Create: `samples/ZeroAlloc.Resilience.AotSmoke/IResultFlakyService.cs`, `samples/ZeroAlloc.Resilience.AotSmoke/ResultFlakyImpl.cs`
- Modify: `samples/ZeroAlloc.Resilience.AotSmoke/Program.cs:31` (before `Console.WriteLine("AOT smoke: PASS");`)
- Create: `docs/diagnostics/ZR0009.md`, `docs/diagnostics/ZR0010.md`
- Modify: `docs/core-concepts/retry.md`, `docs/attributes.md:11-18`, `docs/guides/result-return-types.md:101,111,132`, `docs/diagnostics/ZR0004.md:21`, `docs/source-generator.md:390`, `docs/index.md:70`, `README.md:67,186`

**Interfaces:**
- Consumes: everything from Tasks 1-4, through generated code only.
- Produces: nothing code depends on.

- [ ] **Step 1: Add the AOT smoke case**

Create `samples/ZeroAlloc.Resilience.AotSmoke/IResultFlakyService.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Results;

namespace ZeroAlloc.Resilience.AotSmoke;

public readonly record struct SmokeError(int Status);

// BackoffMs is 10 s: the smoke run finishes quickly only if the delay hint replaces the backoff.
[Retry(MaxAttempts = 3, BackoffMs = 10_000, RetryWhen = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
public interface IResultFlakyService
{
    ValueTask<Result<string, SmokeError>> GetAsync(string id, CancellationToken ct);
    Result<string, SmokeError> Get(string id);

    static bool IsTransient(SmokeError error) => error.Status == 429;
    static TimeSpan? RetryAfter(SmokeError error) => TimeSpan.FromMilliseconds(1);
    static TimeSpan? RetryAfter(Exception exception) => TimeSpan.FromMilliseconds(1);
}
```

Create `samples/ZeroAlloc.Resilience.AotSmoke/ResultFlakyImpl.cs`:

```csharp
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Results;

namespace ZeroAlloc.Resilience.AotSmoke;

public sealed class ResultFlakyImpl : IResultFlakyService
{
    public int CallCount { get; private set; }
    public int FailTimes { get; init; }
    public int Status { get; init; } = 429;

    private Result<string, SmokeError> Next(string id) =>
        ++CallCount <= FailTimes
            ? Result<string, SmokeError>.Failure(new SmokeError(Status))
            : Result<string, SmokeError>.Success($"ok:{id}");

    public ValueTask<Result<string, SmokeError>> GetAsync(string id, CancellationToken ct) => ValueTask.FromResult(Next(id));

    public Result<string, SmokeError> Get(string id) => Next(id);
}
```

In `samples/ZeroAlloc.Resilience.AotSmoke/Program.cs`, add `using System.Diagnostics;` to the usings, and insert before `Console.WriteLine("AOT smoke: PASS");`:

```csharp
// Result-aware retry: a transient failed Result is retried, with the wait taken from the failure.
var started = Stopwatch.GetTimestamp();

var transient = new ResultFlakyImpl { FailTimes = 1 };
var retried = await new IResultFlakyServiceResilienceProxy(transient, new ResultFlakyServiceResiliencePolicies())
    .GetAsync("r", CancellationToken.None).ConfigureAwait(false);
if (!retried.IsSuccess || transient.CallCount != 2)
    return Fail($"Result-aware retry expected success after 2 calls, got {retried.IsSuccess} after {transient.CallCount}");

var syncTransient = new ResultFlakyImpl { FailTimes = 1 };
var syncRetried = new IResultFlakyServiceResilienceProxy(syncTransient, new ResultFlakyServiceResiliencePolicies()).Get("s");
if (!syncRetried.IsSuccess || syncTransient.CallCount != 2)
    return Fail($"Sync Result-aware retry expected success after 2 calls, got {syncRetried.IsSuccess} after {syncTransient.CallCount}");

var permanent = new ResultFlakyImpl { FailTimes = 1, Status = 422 };
var notRetried = await new IResultFlakyServiceResilienceProxy(permanent, new ResultFlakyServiceResiliencePolicies())
    .GetAsync("p", CancellationToken.None).ConfigureAwait(false);
if (notRetried.IsSuccess || notRetried.Error.Status != 422 || permanent.CallCount != 1)
    return Fail($"A non-transient failure expected 1 call and status 422, got {permanent.CallCount} calls");

if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(5))
    return Fail("The delay hint was not used: the retries waited for the 10 s backoff");
```

- [ ] **Step 2: Run the smoke sample, JIT then AOT**

Run: `dotnet run --project samples/ZeroAlloc.Resilience.AotSmoke/ZeroAlloc.Resilience.AotSmoke.csproj -c Release`
Expected: `AOT smoke: PASS`, exit code 0.

Run the "AOT smoke" command for your platform.
Expected: the publish reports no `IL2026`, `IL3050` or other trim or AOT warning, and the binary prints `AOT smoke: PASS`. Delete `./aot-out` afterwards; it is not committed. If the local machine lacks the native toolchain, say so in the PR and rely on the CI `aot-smoke` job, which must pass.

- [ ] **Step 3: Commit the smoke case**

```bash
git add samples/ZeroAlloc.Resilience.AotSmoke
git commit -F - <<'EOF'
test: cover result-aware retry in the AOT smoke sample

The sample retries a transient failed Result async and sync, returns a non-transient one after a
single call, and fails if the delay hint did not replace the 10 s backoff.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

- [ ] **Step 4: Write `docs/diagnostics/ZR0009.md`**

```markdown
---
id: ZR0009
title: ZR0009 — Retry Member Not Found or Signature Mismatch
sidebar_position: 9
---

# ZR0009 — Retry Member Not Found or Signature Mismatch

**Severity:** Error

`RetryWhen`, `RetryOnException` and `DelayHint` on `[Retry]` name static methods. The generator looks each name up on the interface, then on its base interfaces, like `Fallback`, and calls the method directly from the generated proxy. ZR0009 is reported when no method of that name has the required shape:

| Property | Required method |
|---|---|
| `RetryWhen` | `static bool M(E error)` |
| `RetryOnException` | `static bool M(Exception exception)` |
| `DelayHint` | `static TimeSpan? M(E error)`, `static TimeSpan? M(Exception exception)`, or both as overloads |

`E` is the error type of the method's Result: `string` for `Result` and `Result<T>`, and `E` for `Result<T, E>` and `UnitResult<E>`.

The method must also:

- be `static` with a body. A `static abstract` or `static virtual` member makes the whole interface [ZR0007](ZR0007.md), because a proxy instance cannot implement it;
- take exactly one parameter, passed by value, with no `ref`, `in` or `out`;
- not be generic;
- be accessible from the generated proxy, a top-level class in the same assembly: `public` or `internal`, not `private` or `protected`.

The diagnostic is reported at the attribute, once. Whether the method's parameter type matches a particular method's error type is checked per method: see [ZR0010](ZR0010.md).

No proxy is generated for the interface until ZR0009 is fixed.

---

## Example

```csharp
// ZR0009: [Retry] RetryWhen = "IsTransient" names no accessible static method on 'IJevApi' or
// its base interfaces with the signature 'static bool IsTransient(E error)', where E is the
// error type of the method's Result
[Retry(RetryWhen = nameof(IsTransient))]
public interface IJevApi
{
    ValueTask<Result<ModelList, HttpError>> ModelsAsync(CancellationToken ct);

    bool IsTransient(HttpError error) => error.StatusCode == HttpStatusCode.TooManyRequests; // not static
}
```

---

## How to fix

Declare the method `static`, with the signature from the table:

```csharp
    static bool IsTransient(HttpError error) => error.StatusCode == HttpStatusCode.TooManyRequests;
```
```

- [ ] **Step 5: Write `docs/diagnostics/ZR0010.md`**

```markdown
---
id: ZR0010
title: ZR0010 — Result-aware Retry Cannot Apply to Method
sidebar_position: 10
---

# ZR0010 — Result-aware Retry Cannot Apply to Method

**Severity:** Error on a method-level `[Retry]`, Warning for an interface-level `[Retry]`

`RetryWhen`, and a `DelayHint` overload that takes the Result error type, only work on a method that returns a ZeroAlloc.Results Result whose error type they take. ZR0010 is reported when one of them cannot apply to a method:

| Reason | Example |
|---|---|
| The method returns no Result | `ValueTask<string> GetAsync(...)` with `RetryWhen` |
| No overload takes the method's error type | `Result<T, OtherError>` with `static bool IsTransient(HttpError error)` |
| `DelayHint` takes the error type, but `[Retry]` has no `RetryWhen` | a failed Result is never retried, so its hint is never read |

One diagnostic names every property that cannot apply to the method.

- **Method-level `[Retry]`:** an Error, reported at the method. The attribute was written for that method, so a property that cannot apply to it is a mistake.
- **Interface-level `[Retry]`:** a Warning for each method it skips. That method keeps exception-only retry, including `RetryOnException` and the `Exception` overload of `DelayHint`. This lets one interface-level policy serve a mix of Result and non-Result methods.

A `DelayHint` with only an `Exception` overload, and `RetryOnException`, apply to every method with retry and never cause ZR0010.

---

## Example

```csharp
// ZR0010 warning: [Retry] RetryWhen = "IsTransient" cannot apply to 'HealthAsync', because it
// does not return a ZeroAlloc.Results Result, so there is no failed Result to pass to it.
// 'HealthAsync' keeps exception-only retry.
[Retry(MaxAttempts = 3, RetryWhen = nameof(IsTransient))]
public interface IJevApi
{
    ValueTask<Result<ModelList, HttpError>> ModelsAsync(CancellationToken ct);
    ValueTask<string> HealthAsync(CancellationToken ct);

    static bool IsTransient(HttpError error) => error.StatusCode == HttpStatusCode.TooManyRequests;
}
```

---

## How to fix

- For a method-level `[Retry]`, remove the property, or declare an overload that takes the method's error type.
- For an interface-level `[Retry]`, the warning says the skipped method keeps exception-only retry. If that is intended, move `RetryWhen` to a method-level `[Retry]` on the Result methods, or give the non-Result methods their own `[Retry]`.
```

- [ ] **Step 6: Update `docs/core-concepts/retry.md`**

a. Replace the "Configuration" table, the block from `| Property | Type | Default | Description |` through the `PerAttemptTimeoutMs` row, with:

```markdown
| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaxAttempts` | `int` | `3` | Total attempts including the initial call |
| `BackoffMs` | `int` | `200` | Base backoff in ms; actual delay = `BackoffMs * 2^attempt` |
| `Jitter` | `bool` | `false` | Add random jitter of up to 50% of the base backoff |
| `PerAttemptTimeoutMs` | `int` | `0` | Cancel each attempt after this many ms; 0 = disabled |
| `NonThrowing` | `bool` | `false` | Asserts the method returns `Result<T, ResilienceError>` or `UnitResult<ResilienceError>` |
| `RetryWhen` | `string?` | `null` | A static `bool M(E error)`: which failed Results to retry. See [Retrying failed Results](#retrying-failed-results) |
| `RetryOnException` | `string?` | `null` | A static `bool M(Exception exception)`: which exceptions to retry |
| `DelayHint` | `string?` | `null` | A static `TimeSpan? M(E error)` and/or `TimeSpan? M(Exception exception)`: the wait before the next attempt. See [Delay hints](#delay-hints-and-maxdelayms) |
| `MaxDelayMs` | `int` | `RetryPolicy.MaxBackoffMs`, no cap | The longest wait between attempts, for the backoff and a hint alike |
```

b. In "Exhaustion behaviour", append this paragraph after the existing one:

```markdown
With `RetryWhen`, a method whose retries end on a failed Result returns that Result unchanged: the real final error, not a `ResilienceException` or a `ResilienceError`. The exhaustion above applies when the last attempt threw, or when `RetryOnException` declined the exception.
```

c. Replace the whole "What triggers a retry" section, from `## What triggers a retry` up to the `---` before `## Interaction with other policies`, with:

````markdown
## What triggers a retry

Without the new properties, every exception thrown by the inner call triggers a retry, except the caller's own cancellation. A Result the inner call returns, failed or not, is returned as is.

- **`RetryOnException`** names a static `bool M(Exception exception)`. When it returns `false`, the retries stop and the exhaustion behaviour applies at once.
- **`RetryWhen`** names a static `bool M(E error)`, where `E` is the error type of the method's Result. When it returns `true` for a failed Result, that Result is retried like an exception. See [Retrying failed Results](#retrying-failed-results).

The named methods are static methods of the interface or a base interface, checked at build time: [ZR0009](../diagnostics/ZR0009.md) when one is missing or has the wrong signature, [ZR0010](../diagnostics/ZR0010.md) when `RetryWhen` cannot apply to a method. They run outside the `try` that guards the inner call, so an exception they throw reaches the caller unchanged and is never retried.

If the total timeout fires mid-retry, the loop exits:

```csharp
if (__totalCts.IsCancellationRequested) break; // total timeout expired — give up
```

---

## Retrying failed Results

`RetryWhen` retries the failed Results it calls transient, such as an HTTP 429 or 503, and returns every other Result at once:

```csharp
[Retry(MaxAttempts = 4, BackoffMs = 500, MaxDelayMs = 30_000,
       RetryWhen = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
public interface IJevApi
{
    ValueTask<Result<ModelList, HttpError>> ModelsAsync(CancellationToken ct);

    static bool IsTransient(HttpError error) =>
        error.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;

    static TimeSpan? RetryAfter(HttpError error) => error.GetRetryAfter();
}
```

- A successful Result is returned.
- A failed Result `RetryWhen` returns `false` for, such as a 422, is returned after one attempt.
- A failed Result `RetryWhen` returns `true` for is retried. If every attempt ends that way, the last failed Result is returned unchanged.
- A thrown exception is handled as without `RetryWhen`.

`RetryWhen` applies to methods returning `Result`, `Result<T>`, `Result<T, E>` or `UnitResult<E>`, sync or async. On an interface-level `[Retry]`, a method it cannot apply to keeps exception-only retry, with a [ZR0010](../diagnostics/ZR0010.md) warning.

---

## Delay hints and `MaxDelayMs`

`DelayHint` names a static method, or an overload pair, that returns the wait before the next attempt:

- `TimeSpan? M(E error)` for a failed Result. It needs `RetryWhen`.
- `TimeSpan? M(Exception exception)` for a thrown exception. It applies to every method with retry.

A non-null hint replaces the exponential backoff for that attempt. It gets no jitter, since the server asked for that exact wait, and it is rounded up to a whole millisecond; a negative hint means no wait. A `null` hint falls back to the backoff.

`MaxDelayMs` caps every wait, backoff and hint alike. It lives on `RetryPolicy`, so it can be changed at runtime like the other values:

```csharp
services.AddJevApiResilience<JevApi>((sp, p) =>
    p.Retry = new RetryPolicy(maxAttempts: 4, backoffMs: 500, jitter: false, perAttemptTimeoutMs: 0, maxDelayMs: 10_000));
```

The wait observes the total-timeout token when `[Timeout]` is present, and the caller's token otherwise. When the total timeout fires during a wait, the loop ends: a Result-aware method returns its last failed Result.

---

## Caller cancellation

When the caller's `CancellationToken` is cancelled, the proxy throws `OperationCanceledException`. The caller's cancellation is not retried, not counted as a circuit-breaker failure, and not wrapped in `ResilienceException`. The backoff wait observes the caller's token, so a cancelled caller does not wait out the backoff. A per-attempt or total timeout is not the caller's cancellation: it is still retried, or ends the loop, as described above.

In 3.1.0 and earlier, without `[Timeout]`, a cancelled caller waited out every backoff, the loop retried with a cancelled token until the attempts ran out, and the call ended with `ResilienceException`.
````

d. In "Interaction with other policies", replace the circuit breaker bullet with:

```markdown
- **Circuit breaker** — `OnFailure` is called after each failed attempt; `OnSuccess` after each success. With `RetryWhen`, a failed Result it calls transient counts as a failure, and a failed Result it does not as a success, because the service answered. The circuit may open mid-retry if `MaxFailures` is reached.
```

e. Replace the "Sync methods" section body with:

```markdown
For synchronous methods the retry loop has the same structure without `await`. The wait blocks on the delay token's `WaitHandle`, the total-timeout token under `[Timeout]` or else the caller's, so cancellation interrupts it. `Thread.Sleep` is used only when there is no token that can be cancelled.
```

- [ ] **Step 7: Update `docs/attributes.md`**

Replace the `[Retry]` table, lines 13-18, with:

```markdown
| Property | Type | Default | Description |
|---|---|---|---|
| `MaxAttempts` | `int` | `3` | Total attempts (initial + retries) |
| `BackoffMs` | `int` | `200` | Base backoff (exponential: 200, 400, 800…) |
| `Jitter` | `bool` | `false` | Add random jitter up to 50% of base |
| `PerAttemptTimeoutMs` | `int` | `0` | Per-attempt timeout (0 = disabled) |
| `NonThrowing` | `bool` | `false` | Asserts a `ResilienceError` Result return type |
| `RetryWhen` | `string?` | `null` | Static `bool M(E error)`: retry the failed Results it returns `true` for; a transient one is a breaker failure, any other a success |
| `RetryOnException` | `string?` | `null` | Static `bool M(Exception exception)`: `false` stops the retries |
| `DelayHint` | `string?` | `null` | Static `TimeSpan? M(E error)` and/or `TimeSpan? M(Exception exception)`: the next wait, without jitter |
| `MaxDelayMs` | `int` | no cap | Longest wait between attempts, backoff or hint |

The named methods are static methods of the interface or a base interface; see [Retry](core-concepts/retry.md#what-triggers-a-retry).
```

- [ ] **Step 8: Update `docs/guides/result-return-types.md`**

a. Line 101, `A Result returned by the inner call, failed or successful, is always passed through unchanged. Only a thrown exception is retried.`, becomes:

```markdown
Without `RetryWhen`, a Result returned by the inner call, failed or successful, is passed through unchanged, and only a thrown exception is retried. With `RetryWhen`, see [Retrying failed Results](#retrying-failed-results).
```

b. In the "Foreign error types" table, replace the `[Retry]` row with:

```markdown
| `[Retry]` | yes | A successful Result is returned unchanged. A failed Result is returned unchanged, unless `RetryWhen` calls it transient: then it is retried, and when every attempt ends that way the last failed Result is returned. A thrown exception is retried. If the last attempt threw, there is no Result to return, and `ResilienceException` with `Policy = Retry` is thrown, as for a non-Result method. |
```

c. Replace the paragraph at line 132, `Returned failures such as an HTTP 429 are not retried yet, because the retry loop reacts only to exceptions. Retrying on selected failed Results is tracked in [#142](...).`, with:

````markdown
---

## Retrying failed Results

A failed Result is retried when `[Retry]` names a `RetryWhen` predicate that calls it transient. `DelayHint` can take the wait from the failure, such as the server's `Retry-After`:

```csharp
using ZeroAlloc.Rest;
using ZeroAlloc.Results;

[Retry(MaxAttempts = 4, BackoffMs = 500, MaxDelayMs = 30_000,
       RetryWhen = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
[Timeout(Ms = 60_000)]
public interface IJevApi
{
    ValueTask<Result<ModelList, HttpError>> ModelsAsync(CancellationToken ct);

    static bool IsTransient(HttpError error) =>
        error.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;

    static TimeSpan? RetryAfter(HttpError error) => error.GetRetryAfter();
}
```

`HttpError.GetRetryAfter()` is in ZeroAlloc.Rest 2.1.0 and later. A 429 with `Retry-After: 2` waits two seconds, capped at `MaxDelayMs`, and a 422 is returned at once. When every attempt returns a transient failure, the last failed Result is returned unchanged. If the total timeout fires during a wait, the last failed Result is returned too.

A transient failed Result counts as a circuit-breaker failure, and a non-transient one as a success, because the service answered. A storm of 529 "Overloaded" responses therefore opens the circuit.

The predicate and the hints run outside the `try` that guards the inner call. An exception they throw reaches the caller unchanged and is never retried. See [Retry](../core-concepts/retry.md#retrying-failed-results) for the full rules.
````

- [ ] **Step 9: Update the diagnostic tables and ZR0004**

a. `docs/diagnostics/ZR0004.md`: after the `| [Retry] | PerAttemptTimeoutMs | at least 0 |` row, add:

```markdown
| `[Retry]` | `MaxDelayMs` | at least 0 |
```

b. `docs/source-generator.md`: after the ZR0007 row at line 390, add the ZR0008 row the table was missing, and the two new ones:

```markdown
| ZR0008 | Error | The `ZeroAllocGeneratedAccessibility` MSBuild property is set to a value other than `Public` or `Internal` |
| ZR0009 | Error | `[Retry]` `RetryWhen`, `RetryOnException` or `DelayHint` names no accessible static method with the required signature on the interface or its base interfaces |
| ZR0010 | Error or Warning | `RetryWhen`, or a `DelayHint` overload that takes the Result error type, cannot apply to a method: an Error on a method-level `[Retry]`, a Warning for each method an interface-level `[Retry]` skips |
```

c. `docs/index.md`: after the ZR0008 row at line 70, add:

```markdown
| [ZR0009](diagnostics/ZR0009.md) | Error | Retry member not found or signature mismatch |
| [ZR0010](diagnostics/ZR0010.md) | Error / Warning | Result-aware retry cannot apply to method |
```

d. `README.md`: after the ZR0007 row of the Diagnostics table, line 186, add the ZR0008 row it was missing and the two new ones:

```markdown
| [ZR0008](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/blob/main/docs/diagnostics/ZR0008.md) | Error | Invalid `ZeroAllocGeneratedAccessibility` value |
| [ZR0009](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/blob/main/docs/diagnostics/ZR0009.md) | Error | Retry member not found or signature mismatch |
| [ZR0010](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/blob/main/docs/diagnostics/ZR0010.md) | Error / Warning | Result-aware retry cannot apply to method |
```

and after the `` `Result<T>` support `` row of the Features table, line 67, add:

```markdown
| Result-aware retry | `RetryWhen` retries the failed Results you call transient; `DelayHint` honours `Retry-After`, capped by `MaxDelayMs` |
```

- [ ] **Step 10: Check the docs**

Run: `git grep -n "#142\|not retried yet\|no exception filter or predicate" -- docs README.md ':!docs/plans'`
Expected: no output.

Run: `git grep -n "ZR0009\|ZR0010" -- docs README.md ':!docs/plans'`
Expected: hits in `docs/diagnostics/ZR0009.md`, `docs/diagnostics/ZR0010.md`, `docs/source-generator.md`, `docs/index.md`, `docs/core-concepts/retry.md`, `README.md`.

Run the "Full suite, mirrors CI" command once more.
Expected: `0 Warning(s)`, every test project `Passed!`.

- [ ] **Step 11: Commit the docs**

```bash
git add docs README.md
git commit -F - <<'EOF'
docs: document result-aware retry, delay hints, ZR0009 and ZR0010

Retry, the attribute reference and the Result guide describe RetryWhen, RetryOnException,
DelayHint and MaxDelayMs, the breaker interaction and caller cancellation. New pages for
ZR0009 and ZR0010; the diagnostic tables gain them and the ZR0008 rows they were missing.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

---

## Finishing the branch

- [ ] **Step 1: Review the commits**

Run: `git log --format='%h %s' origin/main..HEAD`
Expected, oldest last:

```text
<sha> docs: document result-aware retry, delay hints, ZR0009 and ZR0010
<sha> test: cover result-aware retry in the AOT smoke sample
<sha> feat(generator): retry a failed Result chosen by RetryWhen and take the delay from the failure
<sha> feat(generator): resolve RetryWhen, RetryOnException and DelayHint, with ZR0009 and ZR0010
<sha> fix(generator): propagate caller cancellation from the retry loop
<sha> feat(core): add MaxDelayMs, GetDelayMs and a breaker failure without an exception
<sha> docs: design result-aware retry and failure-driven delays
```

Run: `git log --format=%B origin/main..HEAD | awk 'length > 100'` — expected: no output.
Run: `git log --format=%B origin/main..HEAD | grep -n "claude.ai/code"` — expected: no output.
Run: `git log --format=%B origin/main..HEAD | grep -nE "\([^)]*\("` — expected: no output, so no nested parentheses.

- [ ] **Step 2: File the ZeroAlloc.Rest follow-up**

The spec's follow-up is tracked as an issue now, so it is not lost. Check first: `gh issue list --repo ZeroAlloc-Net/ZeroAlloc.Rest --state open --search "RetryWhen in:title,body"`. If none exists:

```bash
gh issue create --repo ZeroAlloc-Net/ZeroAlloc.Rest \
  --title "docs: [Retry] can retry failed Results once ZeroAlloc.Resilience 3.2.0 ships" \
  --body-file - <<'EOF'
ZeroAlloc.Resilience 3.2.0 adds `RetryWhen`, `DelayHint` and `MaxDelayMs` to `[Retry]`, see
ZeroAlloc-Net/ZeroAlloc.Resilience#142 and #143. After it is on NuGet:

- `docs/resilience.md`, the policy table says `[Retry]` passes Results through. With `RetryWhen`
  it retries the failed Results the predicate calls transient.
- The "Result Returns and Error Handling" section recommends `NonThrowing = true`, which only
  asserts a `ResilienceError` return type. Replace it with the recipe below.
- Add the recipe:

      [Retry(MaxAttempts = 4, BackoffMs = 500, MaxDelayMs = 30_000,
             RetryWhen = nameof(IsTransient), DelayHint = nameof(RetryAfter))]
      public interface IJevApi
      {
          ValueTask<Result<ModelList, HttpError>> ModelsAsync(CancellationToken ct);
          static bool IsTransient(HttpError error) =>
              error.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;
          static TimeSpan? RetryAfter(HttpError error) => error.GetRetryAfter();
      }
EOF
```

- [ ] **Step 3: Push and open the PR**

```bash
git push -u origin feat/result-aware-retry
gh pr create --repo ZeroAlloc-Net/ZeroAlloc.Resilience --base main --head feat/result-aware-retry \
  --title "feat: retry a failed Result chosen by RetryWhen and take the delay from the failure" \
  --body-file - <<'EOF'
Closes #142. Closes #143.

- `RetryWhen` retries the failed Results a static predicate calls transient; the breaker counts
  them as failures and every other returned Result as a success.
- `DelayHint` takes the next wait from the failed Result or the exception, capped by the new
  `MaxDelayMs`, which lives on `RetryPolicy` through a new constructor overload.
- `RetryOnException` stops retrying exceptions the predicate declines.
- ZR0009 and ZR0010 check the named methods at build time; ZR0004 checks `MaxDelayMs`.
- Fix: the caller's cancellation now ends the retry loop with `OperationCanceledException`.

The generated output of methods that use none of the new properties changes only by the
cancellation fix.

BEGIN_COMMIT_OVERRIDE
feat: retry a failed Result chosen by RetryWhen, #142
feat: take the retry delay from the failure, #143
fix: propagate caller cancellation from the retry loop
END_COMMIT_OVERRIDE

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
```

Every CI job must pass: `lint-commits`, `build`, `aot-smoke` and `api-compat`. api-compat must pass with no suppression file.

- [ ] **Step 4: After merge**

- Open the release-please PR and confirm its changelog lists all three entries from the override block. A green workflow does not prove a commit was counted.
- After the release, confirm `ZeroAlloc.Resilience` 3.2.0 is listed on NuGet, and that its `RetryPolicy` has the five-argument constructor, by restoring it into a scratch project. A green publish job does not prove the package shipped.

---

## Design decisions this plan resolves

The spec leaves these open; the plan decides them as follows.

1. **ZR0009 versus ZR0010.** ZR0009 is about the name alone: no method of the name has the required *shape*, independent of any method's error type. The shape is static with a body, not generic, one by-value parameter, not by-reference returning, accessible, and returning `bool` for `RetryWhen` and `RetryOnException` or `TimeSpan?` for `DelayHint`. `RetryOnException` also requires the parameter to be `Exception`, since no error type is involved. ZR0010 is about fit: a `RetryWhen` overload of the right shape exists but none takes this method's error type, or the method returns no Result. So the spec's "wrong parameter" ZR0009 case is a wrong parameter count or a `ref` parameter, and a wrong parameter *type* for `RetryWhen` is ZR0010. ZR0009 is reported once per attribute; ZR0010 per method.
2. **`static abstract` and `static virtual` members are not allowed.** The lookup requires `IsAbstract: false, IsVirtual: false`. Such a member cannot be called through the interface name, only through a type parameter constrained to the interface, and the proxy has none. The interface is already rejected with ZR0007 before any lookup, because a proxy instance cannot implement a static abstract member, so the user sees ZR0007 alone; a test locks that in.
3. **Accessibility** is `Compilation.IsSymbolAccessibleWithin(method, compilation.Assembly)`: `public` and `internal` pass, as does an `internal` member of another assembly with `InternalsVisibleTo`; `private` and `protected` fail with ZR0009.
4. **Lookup order and call target.** It mirrors `FindFallback`: the interface first, then `AllInterfaces` in order, first match wins. The call is emitted through the declaring interface, fully qualified, such as `global::Ns.IRetryRules.IsTransient(...)`, so a base interface's method binds to exactly that declaration. Keyword names are escaped.
5. **Error type matching** uses `SymbolEqualityComparer.Default`, which ignores nullable annotations, so `IsTransient(HttpError? error)` accepts an `HttpError` error type. `E` is `string` for `Result` and `Result<T>`.
6. **Generic predicates** are rejected with ZR0009: nothing could infer their type arguments. `in` parameters are rejected too; by-value only.
7. **An error-typed `DelayHint` without `RetryWhen` is ZR0010.** Its only use is to set the wait before retrying a failed Result, which never happens without `RetryWhen`; silently ignoring it would hide a mistake. The reason text says so. A `DelayHint` with only an `Exception` overload never causes ZR0010.
8. **One ZR0010 per method**, naming every property that cannot apply, such as `RetryWhen = "IsTransient" and DelayHint = "RetryAfter"`. When `RetryWhen` fails and the hint overload does take the error type, only `RetryWhen` is named, since fixing it fixes the hint.
9. **ZR0010 severity.** One descriptor, default severity Error. The interface-level case is created with `Diagnostic.Create(..., effectiveSeverity: DiagnosticSeverity.Warning, ...)`, so the generator's error gate still emits the proxy for it.
10. **Inherited methods.** A method inherited from a base interface with its own `[Retry]` is resolved from the proxied interface without reporting ZR0009 or ZR0010: the base interface validates its own attribute, as it already does for ZR0004. An inherited method under the proxied interface's `[Retry]` gets ZR0010 warnings at the interface, like ZR0006.
11. **`MaxDelayMs` also caps the plain backoff.** `GetBackoffMs` honours `MaxDelayMs`, so a runtime override of `MaxDelayMs` works for every retry method, and methods without hints keep emitting `GetBackoffMs(__attempt)`, which leaves their generated output unchanged. The spec's `GetDelayMs(attempt, null)` equals `GetBackoffMs(attempt)`. With the four-argument constructor `MaxDelayMs` is `MaxBackoffMs`, so 3.1 behaviour is identical.
12. **The five-argument default** is emitted only when the attribute explicitly sets `MaxDelayMs`: `RetryConfig.MaxDelayMs` is `int?`. Every existing snapshot and policies class stays byte-identical.
13. **Delay arithmetic.** A hint is rounded up to a whole millisecond, so the wait is never shorter than asked; `0` or negative means no wait; the result is capped at `min(MaxDelayMs, MaxBackoffMs)`, so `TimeSpan.MaxValue` or `maxDelayMs = int.MaxValue` can never overflow or exceed a valid wait argument. `MaxDelayMs` keeps the value passed; only the computation clamps.
14. **Sync waits.** `token.WaitHandle.WaitOne(ms)`, guarded by `CanBeCanceled` for the caller's token so a token that can never fire costs no wait handle; `Thread.Sleep` only when no token can be cancelled. `WaitOne` returning `true` means the token fired: the caller's cancellation throws, a total timeout ends the loop. It is AOT-safe. It allocates one `ManualResetEvent` per `CancellationTokenSource`, lazily and only on the retry path, never on the happy path.
15. **Async total timeout in the Result-aware loop** awaits `Task.Delay(...).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing)` and checks the token, so returning the last Result costs no exception. `ConfigureAwaitOptions` needs .NET 8, which every consumer of this net8.0+ package has. The exception-only loop keeps its throwing `Task.Delay`, as the spec's "keeps today's shape" requires.
16. **"The exception path keeps today's behaviour"** is read as today's exhaustion. In the Result-aware loop, a total timeout during a wait after a thrown exception exits through exhaustion, a `Failure` or `ResilienceException`, like today's `if (__totalCts.IsCancellationRequested) break;`. The exception-only loop still lets `TaskCanceledException` escape when the timeout fires mid-backoff; that pre-existing behaviour is filed in Task 2 Step 9 rather than changed here.
17. **Exception-path predicates run in the catch block.** It is outside the protected `try` region, so an exception from `RetryOnException` or the exception `DelayHint` propagates unchanged, as the spec requires. The Result predicate and hint run after the `try`/`catch`.
18. **Order within an attempt** follows the spec's pseudocode: breaker, predicate, hint, then the timeout and last-attempt exits, then the wait. The hint therefore runs on the last attempt too, and `OnFailure(ex)` is called even when `RetryOnException` then declines.
19. **The caller-cancellation catch** is emitted only when the method has a `CancellationToken` parameter; without one there is no caller token. It also means the circuit breaker no longer counts the caller's cancellation as a failure, which a test covers. The single-call path, with no `[Retry]`, is outside the spec's scope and is filed.
20. **Sync exception-only loop under `[Timeout]`** now waits on the total-timeout handle and breaks when it fires, where 3.1 slept through it and broke one attempt later. The outcome, exhaustion, is the same, only sooner. This is part of the `fix:`.
21. **Escaping the `CancellationToken` parameter name.** The generator emitted it unescaped, a latent compile error for a name like `@checked`. The fix emits it in two more places, so it is fixed in Task 2 with a test.
22. **Test infrastructure.** `TestHelper.Verify` and `GetDiagnostics` reference ZeroAlloc.Results explicitly, so Result types resolve whatever ran first; `GeneratorDiagnostics` returns warnings, which `RunAndCompile` drops. `*.received.*` is gitignored, as the org's snapshot standard expects.
23. **Commit types for Task 5** are `test:` for the AOT sample and `docs:` for the documentation; neither is a user-facing feature, and the squash merge takes its changelog from the override block anyway.
24. **Resolution lives in a new partial file**, `ResilienceGenerator.RetryMembers.cs`, to keep the 1,500-line generator file from growing further; `ResilienceGenerator` becomes `partial`. MA0048 is already disabled repo-wide, so no suppression is needed.
25. **No `AnalyzerReleases.*.md`.** The generator project predates this change with `RS2008` in `NoWarn`, and there are no release-tracking files. Adopting them is a separate decision unrelated to this feature; it is not a defect, so it is dropped rather than filed.

---

## Spec coverage

| Spec requirement | Task |
|---|---|
| `RetryAttribute.RetryWhen`, `RetryOnException`, `DelayHint`, `MaxDelayMs = RetryPolicy.MaxBackoffMs` | 1 |
| `RetryPolicy(…, int maxDelayMs)` overload; existing constructor unchanged | 1 |
| `RetryPolicy.MaxDelayMs`; `GetDelayMs`: `min(hint ?? backoff, MaxDelayMs)`, negative hint → 0, no jitter on a hint | 1 |
| New constructor validates `maxDelayMs >= 0` | 1 |
| `CircuitBreakerPolicy.OnFailure()` | 1 |
| New public API in `PublicAPI.Unshipped.txt`; api-compat passes without suppressions | 1, Finishing |
| Caller's `OperationCanceledException` rethrown, not retried or wrapped | 2 |
| Backoff observes the caller's token without `[Timeout]` | 2 |
| Sync waits on the delay token's wait handle | 2, 4 |
| Fix is the only change for methods without new properties, shipped as `fix:` | 2, 4 Step 6 |
| Predicates are static methods found like `Fallback`, across base interfaces, accessible from the proxy | 3 |
| `E` is the error type of `Result<T, E>`/`UnitResult<E>`, or `string`, or `ResilienceError` | 3 |
| `DelayHint` overload set: `E` overload for Results, `Exception` overload for exceptions | 3, 4 |
| ZR0009, Error, at the attribute, with the expected signature | 3 |
| ZR0010, Error at the method for method-level, Warning per skipped method for interface-level | 3 |
| ZR0004 rule `MaxDelayMs >= 0` | 3 |
| `MaxDelayMs` overridable at runtime through the policy set | 3, 4 |
| Result-aware loop: only the inner call in the `try`; success and non-transient return with `OnSuccess` | 4 |
| Transient failed Result: `OnFailure()`, `__lastResult`, `__lastWasResult`, `E` hint | 4 |
| Exit returns `__lastResult` when the last attempt was a Result, else today's exhaustion | 4 |
| Predicates and hints outside the `try`; their exceptions propagate | 4 |
| Exception path: `__lastEx`, `__lastWasResult = false`, `OnFailure(ex)`, `RetryOnException` false exits, `Exception` hint | 4 |
| Delay token: total timeout when present, caller's otherwise; total timeout during a wait returns the last Result | 4 |
| Without `RetryWhen`, today's loop shape; `RetryOnException` and `Exception` hint apply to every retry method | 4 |
| Tests, generator: snapshots async, sync, `UnitResult`, with and without `[Timeout]` and `[CircuitBreaker]`, with `DelayHint` overloads | 4 |
| Tests, generator: ZR0009 missing, not static, wrong parameter, wrong return type | 3 |
| Tests, generator: ZR0010 Error method-level and Warning interface-level | 3 |
| Tests, generator: ZR0004 negative `MaxDelayMs` | 3 |
| Tests, generator: output unchanged apart from the cancellation fix without new properties | 2 Step 6, 4 Step 6, 4 `NoNewProperty_KeepsTheExceptionOnlyLoop` |
| Tests, runtime: 429 then success takes two attempts; 422 after one | 4 |
| Tests, runtime: every attempt 429 returns the last failed Result unchanged | 4 |
| Tests, runtime: throwing `RetryWhen` propagates, not retried | 4 |
| Tests, runtime: hint overrides backoff and is capped by `MaxDelayMs` | 4 |
| Tests, runtime: `Exception` hint on the exception path; `RetryOnException` false stops | 4 |
| Tests, runtime: breaker opens on transient failed Results, not on non-transient | 4 |
| Tests, runtime: total timeout interrupts a long hinted wait and returns the last Result | 4 |
| Tests, runtime: caller cancellation → `OperationCanceledException`, with and without `[Timeout]`, async and sync | 2, 4 |
| Tests, runtime: `GetDelayMs` and `OnFailure()` unit tests | 1 |
| AOT smoke case | 5 |
| Docs: `result-return-types.md` note and per-policy table | 5 |
| Docs: `retry.md` and `attributes.md`, four properties, delay rules, breaker interaction | 5 |
| Docs: `ZR0009.md`, `ZR0010.md`, `source-generator.md` rows | 5 |
| Follow-up in ZeroAlloc.Rest `docs/resilience.md` with the `GetRetryAfter` recipe | Finishing, Step 2 |
| One PR closing #142 and #143; `BEGIN_COMMIT_OVERRIDE` with three entries | Finishing, Step 3 |
| Commit bodies ≤ 100 characters, no nested parentheses | every commit; Finishing, Step 1 |
| After merge, confirm the release PR lists all three entries | Finishing, Step 4 |
