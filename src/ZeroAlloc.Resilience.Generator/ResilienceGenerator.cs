namespace ZeroAlloc.Resilience.Generator;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;

[Generator]
public sealed partial class ResilienceGenerator : IIncrementalGenerator
{
    private const string RetryFqn          = "ZeroAlloc.Resilience.RetryAttribute";
    private const string TimeoutFqn        = "ZeroAlloc.Resilience.TimeoutAttribute";
    private const string RateLimitFqn      = "ZeroAlloc.Resilience.RateLimitAttribute";
    private const string CircuitBreakerFqn = "ZeroAlloc.Resilience.CircuitBreakerAttribute";

    // FullyQualifiedFormat alone drops the `?` on a nullable reference type (its
    // MiscellaneousOptions doesn't include IncludeNullableReferenceTypeModifier), so a `string?`
    // property or parameter would render as `string` and the emitted member would mismatch the
    // interface's nullability, producing CS8766/CS8767. Every type rendered into generated code
    // uses this format instead.
    private static readonly SymbolDisplayFormat FqnFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    // "IJevApi" -> "JevApi", "IInvoiceApi" -> "InvoiceApi", "Item" -> "Item", "I" -> "I"
    internal static string ServiceName(string interfaceName) =>
        interfaceName.Length > 1 && interfaceName[0] == 'I' && char.IsUpper(interfaceName[1])
            ? interfaceName.Substring(1)
            : interfaceName;

    // The exact MSBuild property name shared, unqualified, across every ZeroAlloc generator
    // package (#152). CompilerVisibleProperty in the package's build/buildTransitive props makes
    // it available here as build_property.<name>.
    private const string GeneratedAccessibilityProperty = "build_property.ZeroAllocGeneratedAccessibility";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var candidates = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) => IsCandidate(node),
            transform: static (ctx, ct) => TryParse(ctx, ct));
        var filtered = candidates.Where(static m => m is not null);
        var models   = filtered.Select(static (m, _) => m!);

        // ZR0008: read once per compilation, independent of whether any interface is a candidate,
        // so an invalid value is reported even in a project with nothing to generate for.
        var accessibility = context.AnalyzerConfigOptionsProvider
            .Select(static (provider, _) => ParseGeneratedAccessibility(provider));

        var modelsWithAccessibility = models
            .Combine(accessibility.Select(static (result, _) => result.Mode))
            .Select(static (pair, _) => pair.Left with
            {
                EmitPublicEntryPoints = pair.Left.IsPublic && pair.Right == GeneratedAccessibilityMode.Public,
            });

        RegisterRetryAttemptDiagnostics(context);

        context.RegisterSourceOutput(accessibility, static (ctx, result) =>
        {
            if (result.Diagnostic is not null)
                ctx.ReportDiagnostic(result.Diagnostic);
        });

        context.RegisterSourceOutput(modelsWithAccessibility, static (ctx, model) =>
        {
            foreach (var diag in model.Diagnostics)
                ctx.ReportDiagnostic(diag);

            var hasError = false;
            foreach (var d in model.Diagnostics)
                if (d.Severity == DiagnosticSeverity.Error) { hasError = true; break; }
            if (hasError) return;

            var source = ResilienceWriter.Write(model);
            var hintName = model.Namespace is null
                ? $"{model.InterfaceName}.Resilience.g.cs"
                : $"{model.Namespace}_{model.InterfaceName}.Resilience.g.cs";
            ctx.AddSource(hintName, source);
        });
    }

    // ZR0008: "Public" and "Internal" are the only allowed values, compared case-insensitively; an
    // unset or empty property defaults to Public, unchanged since before #152. Any other value is
    // an error, and generation for every interface in the compilation falls back to Public so the
    // rest of the build still reflects today's behavior instead of silently going internal.
    private static GeneratedAccessibilityResult ParseGeneratedAccessibility(AnalyzerConfigOptionsProvider provider)
    {
        if (!provider.GlobalOptions.TryGetValue(GeneratedAccessibilityProperty, out var raw) || raw.Length == 0)
            return new GeneratedAccessibilityResult(GeneratedAccessibilityMode.Public, null);

        if (string.Equals(raw, "Public", StringComparison.OrdinalIgnoreCase))
            return new GeneratedAccessibilityResult(GeneratedAccessibilityMode.Public, null);
        if (string.Equals(raw, "Internal", StringComparison.OrdinalIgnoreCase))
            return new GeneratedAccessibilityResult(GeneratedAccessibilityMode.Internal, null);

        var diagnostic = Diagnostic.Create(ResilienceDiagnostics.InvalidGeneratedAccessibilityValue, Location.None, raw);
        return new GeneratedAccessibilityResult(GeneratedAccessibilityMode.Public, diagnostic);
    }

    // Policies may sit on the interface or only on its methods; TryParse does the semantic check.
    private static bool IsCandidate(SyntaxNode node)
    {
        if (node is not InterfaceDeclarationSyntax iface) return false;
        if (iface.AttributeLists.Count > 0) return true;
        foreach (var member in iface.Members)
            if (member is MethodDeclarationSyntax { AttributeLists.Count: > 0 }) return true;
        return false;
    }

    private static ResilienceModel? TryParse(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is not INamedTypeSymbol iface)
            return null;

        if (!IsFirstCandidatePart(iface, ctx.Node, ct))
            return null;

        var state = new InterfaceParse(iface, ctx.SemanticModel.Compilation);

        // Every member the proxy forwards: the interface's own members first, in declaration
        // order, then the inherited ones, then the explicit implementations.
        var forwarded = CollectForwardedMembers(iface);

        // Skip interfaces with no policy attributes of their own: none on the interface and none
        // on a method it declares. A policy on an inherited method alone does not make a proxy:
        // the base interface that declares it gets its own.
        if (!state.HasInterfacePolicy && !HasOwnMethodPolicy(iface))
            return null;

        ct.ThrowIfCancellationRequested();

        var ns = iface.ContainingNamespace.IsGlobalNamespace
            ? null
            : iface.ContainingNamespace.ToDisplayString();

        // ZR0007: shapes no valid proxy can be generated for. Reported instead of emitting code
        // that fails to compile.
        var unsupported = UnsupportedShapeDiagnostics(iface);
        if (unsupported.Length > 0)
            return DiagnosticsOnly(iface, ns, unsupported);

        state.ValidateInterfaceAttributes();
        state.AddInterfaceSlots();

        // Own methods come first, so their slot names are the ones they had before inherited
        // methods were forwarded; inherited methods are numbered after them.
        foreach (var entry in forwarded)
        {
            if (entry.Symbol is IMethodSymbol member)
                ParseMethod(state, entry, member);
        }
        state.ReportUnusedInterfaceRethrowDeclined();

        var passthroughMembers = CollectPassthroughMembers(forwarded);

        // Nothing to emit: no member, own or inherited, and nothing to report. An own method the
        // proxy does not forward, such as a redeclared Equals, still counts, as it did in 2.0.1:
        // the proxy then implements the interface through object's members and default bodies.
        if (state.Methods.Count == 0 && state.Passthroughs.Count == 0
            && passthroughMembers.Length == 0 && state.Diagnostics.Count == 0
            && !forwarded.Any(static f => f.SlotOnly))
            return null;

        return BuildModel(state, ns, passthroughMembers);
    }

    // Several parts of a partial interface can pass the predicate; only the first one emits,
    // otherwise every part adds a source with the same hint name.
    private static bool IsFirstCandidatePart(INamedTypeSymbol iface, SyntaxNode node, CancellationToken ct)
    {
        foreach (var reference in iface.DeclaringSyntaxReferences)
        {
            var declaration = reference.GetSyntax(ct);
            if (!IsCandidate(declaration)) continue;
            return declaration == node;
        }
        return true;
    }

    private static bool HasOwnMethodPolicy(INamedTypeSymbol iface) =>
        iface.GetMembers()
            .OfType<IMethodSymbol>()
            .Any(m => GetAttribute(m, RetryFqn) is not null
                   || GetAttribute(m, TimeoutFqn) is not null
                   || GetAttribute(m, RateLimitFqn) is not null
                   || GetAttribute(m, CircuitBreakerFqn) is not null);

    private static ResilienceModel BuildModel(
        InterfaceParse state,
        string? ns,
        ImmutableArray<PassthroughMemberModel> passthroughMembers)
    {
        var iface = state.Iface;
        var isPublic = IsEffectivelyPublic(iface);
        return new ResilienceModel(
            Namespace: ns,
            InterfaceName: iface.Name,
            InterfaceFqn: iface.ToDisplayString(FqnFormat),
            IsPublic: isPublic,
            // Overwritten once the accessibility option is combined in, in Initialize; this default
            // matches today's behavior (ZeroAllocGeneratedAccessibility unset == Public) for any
            // code path that reads the model before that combine runs.
            EmitPublicEntryPoints: isPublic,
            PoliciesClassName: ServiceName(iface.Name) + "ResiliencePolicies",
            Slots: state.Slots.ToImmutable(),
            ClassRetry: state.ClassRetry,
            ClassTimeout: state.ClassTimeout,
            ClassRateLimit: state.ClassRateLimit,
            ClassCircuitBreaker: state.ClassCircuitBreaker,
            Methods: state.Methods.ToImmutable(),
            PassthroughMethods: state.Passthroughs.ToImmutable(),
            PassthroughMembers: passthroughMembers,
            Diagnostics: state.Diagnostics.ToImmutable());
    }

    // ── Methods ────────────────────────────────────────────────────────────────

    // One forwarded method: a slot-only entry, a passthrough, or a policy-wrapped method.
    private static void ParseMethod(InterfaceParse state, ForwardedMember entry, IMethodSymbol member)
    {
        var declarationIndex = state.Slots.NextDeclarationIndex(member.Name);

        // An own method the proxy does not forward still takes its declaration number, as in
        // 2.0.1, so the slot names of the overloads declared after it do not change. A policy
        // on it does not apply, and ZR0006 says so.
        if (entry.SlotOnly)
        {
            ReportSkippedMethodPolicies(state.Diagnostics, state.Iface, member, state.InterfacePolicies);
            return;
        }

        var method = new MethodParse(state, entry, member, declarationIndex);

        // ZR0004: method-level attribute values, reported once per method (not per interface).
        // An inherited method's attribute values belong to the base interface, which
        // validates them itself.
        if (entry.IsOwn)
        {
            ValidateAttributeValues(state.Diagnostics, method.OwnRetryAttr, RetryRules, member.Name, method.Location);
            ValidateAttributeValues(state.Diagnostics, method.OwnTimeoutAttr, TimeoutRules, member.Name, method.Location);
            ValidateAttributeValues(state.Diagnostics, method.OwnRateLimitAttr, RateLimitRules, member.Name, method.Location);
            ValidateAttributeValues(state.Diagnostics, method.OwnCbAttr, CircuitBreakerRules, member.Name, method.Location);
        }

        // A method no policy applies to is forwarded to the inner service unchanged.
        if (!method.HasPolicy)
        {
            state.Passthroughs.Add(method.Passthrough);
            return;
        }

        ResolveFallback(state, method);
        ClassifyMethodResult(state, method);

        if (LeaveOffInheritedDefaultPolicies(state, method))
        {
            state.Passthroughs.Add(method.Passthrough);
            return;
        }

        if (!ReportMethodDiagnostics(state, method))
            return;

        state.Methods.Add(BuildMethodModel(state, method));
    }

    // The fallback name comes from the CircuitBreakerAttribute on the method or the
    // interface. Fallback is intentionally excluded from CircuitBreakerConfig (it is a
    // generator-time string reference, not a runtime config value), so it is read
    // directly from the attribute already fetched. It is looked up among every
    // public instance method the interface declares or inherits.
    private static void ResolveFallback(InterfaceParse state, MethodParse method)
    {
        var cbAttr = method.OwnCbAttr ?? state.CircuitBreakerAttr;
        if (cbAttr is null) return;

        method.FallbackName = GetString(cbAttr, "Fallback");
        if (method.FallbackName is null) return;

        var fallback = FindFallback(state.Iface, method.FallbackName, method.Member);
        if (fallback is not null)
        {
            method.FallbackConfigured = true;
            method.FallbackReceiver = InnerReceiver(state.Iface, fallback);
        }
        else if (!method.Entry.IsOwn && method.OwnCbAttr is null)
        {
            // An interface-level Fallback is written for the methods the interface
            // itself declares. An inherited method it does not match gets the circuit
            // breaker without a fallback: reporting ZR0001 would turn interfaces that
            // compiled before inherited methods were forwarded into errors.
            method.FallbackName = null;
        }
        else
        {
            state.Diagnostics.Add(Diagnostic.Create(
                ResilienceDiagnostics.FallbackNotFound,
                method.Location,
                method.FallbackName, state.Iface.Name, method.Member.Name));
            method.FallbackName = null; // suppress emission
            method.FallbackConfigured = true; // ZR0001 already covers the open circuit
        }
    }

    // The Result the method returns, its error type, and which policies cannot build a failure
    // of it. Also ZR0009 for the method's own [Retry], which needs the error type.
    private static void ClassifyMethodResult(InterfaceParse state, MethodParse method)
    {
        var member = method.Member;
        method.ResultType = method.IsAsync ? UnwrapAsyncType(member.ReturnType) : member.ReturnType;
        method.ResultKind = ClassifyResult(method.ResultType);
        method.ErrorType = ResultErrorType(method.ResultType, method.ResultKind, state.Compilation);

        // ZR0009 for the method's own [Retry], at the attribute. An inherited method's
        // attribute belongs to the base interface, which validates it itself.
        if (method.Entry.IsOwn)
        {
            ValidateRetryMemberNames(state.Diagnostics, method.OwnRetryAttr, state.RetryLookup,
                method.ErrorType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), method.Location);
            ValidateOwnRethrowDeclined(state, method);
        }

        // ZR0003: a policy that rejects a call without calling the inner service has to
        // return a failure, and it cannot build one for a foreign error type. Rejections are
        // only reported for async Result<T, E>: sync methods and UnitResult<E> compiled and
        // threw ResilienceException before #151, and keep doing so.
        var reportsRejections = method.ResultKind == ResultKind.ForeignError && method.IsAsync
            && string.Equals(method.ResultType!.Name, "Result", StringComparison.Ordinal);
        method.RateLimitRejects = reportsRejections && method.RateLimit is not null;
        method.OpenCircuitRejects = reportsRejections && method.CircuitBreaker is not null && !method.FallbackConfigured;
        method.NonThrowingUnbuildable = method.Retry?.NonThrowing == true && method.ResultKind != ResultKind.ResilienceError;
        method.Unwrappable = UnwrappableReason(method.IsAsync, method.HasByRefOrRefLikeParameter, method.ReturnsByRef);
    }

    // ZR0013 for the method's own [Retry(RethrowDeclined = true)], at the attribute: without
    // RetryOnException nothing is declined, and a method that returns failures never throws.
    private static void ValidateOwnRethrowDeclined(InterfaceParse state, MethodParse method)
    {
        if (method.OwnRetry is not { RethrowDeclined: true } own) return;
        var name = method.Member.Name;
        if (own.RetryOnException is null)
            ReportRethrowDeclinedHasNoEffect(state.Diagnostics, method.OwnRetryAttr!, method.Location, name, NoRetryOnException);
        else if (ReturnsFailures(method.ResultKind))
            ReportRethrowDeclinedHasNoEffect(state.Diagnostics, method.OwnRetryAttr!, method.Location, name,
                ("it returns a failure instead of throwing, for a declined exception too", "Remove RethrowDeclined"));
    }

    private static readonly (string Reason, string Advice) NoRetryOnException =
        ("the [Retry] has no RetryOnException, so no exception is ever declined",
         "Set RetryOnException, or remove RethrowDeclined");

    // The Result shapes whose failure the proxy builds, so it never throws; the same test as
    // MethodModel.ReturnsFailureResult.
    private static bool ReturnsFailures(ResultKind kind) =>
        kind is ResultKind.StringError or ResultKind.ResilienceError;

    private static void ReportRethrowDeclinedHasNoEffect(ImmutableArray<Diagnostic>.Builder diagnostics,
        AttributeData attr, Location? fallbackLocation, string target, (string Reason, string Advice) why) =>
        diagnostics.Add(Diagnostic.Create(ResilienceDiagnostics.RethrowDeclinedHasNoEffect,
            AttributeLocation(attr, fallbackLocation), target, why.Reason, why.Advice));

    // Before 3.0 an inherited method with a default body was not forwarded: its default
    // body ran. A policy that cannot be applied to it without an error is left off, with a
    // ZR0006 warning, so the interface keeps compiling and the user learns the policy does
    // not apply. On an own or an abstract method the same policies are errors: ZR0003, or
    // ZR0007 for a method no policy can wrap at all. True when no policy is left, so the
    // method is forwarded unchanged.
    private static bool LeaveOffInheritedDefaultPolicies(InterfaceParse state, MethodParse method)
    {
        var member = method.Member;
        if (method.Entry.IsOwn || member.IsAbstract
            || !(method.RateLimitRejects || method.OpenCircuitRejects || method.NonThrowingUnbuildable || method.Unwrappable is not null))
            return false;

        if (method.Unwrappable is not null)
        {
            ReportPolicyNotApplied(state.Diagnostics, state.IfaceLocation, state.Iface, member, "its policies", method.Unwrappable,
                $"Remove the policy attributes that apply to it, or declare '{member.Name}' on '{state.Iface.Name}' with a signature a policy can wrap");
            return true;
        }
        if (method.RateLimitRejects)
        {
            ReportPolicyNotApplied(state.Diagnostics, state.IfaceLocation, state.Iface, member, "[RateLimit]",
                $"a rejected call must return a failure, and {UnbuildableError(method.ResultType)}",
                PolicyNotAppliedAdvice(state.Iface, member, "[RateLimit]", fromOwnAttribute: method.OwnRateLimit is not null));
            method.RateLimit = null;
            method.RateLimitRejects = false;
        }
        if (method.OpenCircuitRejects)
        {
            ReportPolicyNotApplied(state.Diagnostics, state.IfaceLocation, state.Iface, member, "[CircuitBreaker]",
                $"no Fallback matches it, so an open circuit must return a failure, and {UnbuildableError(method.ResultType)}",
                PolicyNotAppliedAdvice(state.Iface, member, "[CircuitBreaker]", fromOwnAttribute: method.OwnCb is not null));
            method.CircuitBreaker = null;
            method.FallbackName = null;
            method.OpenCircuitRejects = false;
        }
        if (method.NonThrowingUnbuildable)
        {
            ReportPolicyNotApplied(state.Diagnostics, state.IfaceLocation, state.Iface, member, "[Retry(NonThrowing = true)]",
                $"exhausted retries must return a ResilienceError failure, but the method returns '{member.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}'",
                PolicyNotAppliedAdvice(state.Iface, member, "[Retry(NonThrowing = true)]", fromOwnAttribute: method.OwnRetry is not null));
            method.Retry = null;
        }

        return !method.HasPolicy;
    }

    // ZR0007, ZR0002 and ZR0003 for a policy-wrapped method. False when no policy can wrap it,
    // so it gets no proxy member.
    private static bool ReportMethodDiagnostics(InterfaceParse state, MethodParse method)
    {
        var member = method.Member;

        // ZR0007: a method no policy can wrap: async with a ref, out, in or ref struct
        // parameter, or returning by reference.
        if (method.Unwrappable is not null)
        {
            state.Diagnostics.Add(Diagnostic.Create(
                ResilienceDiagnostics.UnsupportedInterfaceShape,
                method.Location,
                state.Iface.Name,
                $"'{member.Name}' has a policy, but {method.Unwrappable}"));
            return false;
        }

        // ZR0002: timeout but no CancellationToken
        if ((method.Timeout is not null || method.Retry?.PerAttemptTimeoutMs > 0) && method.CancellationTokenParamName is null)
        {
            state.Diagnostics.Add(Diagnostic.Create(
                ResilienceDiagnostics.NoCancellationToken,
                method.Location,
                member.Name));
        }

        if (method.ResultKind == ResultKind.ForeignError)
        {
            ReportUnconstructibleError(state.Diagnostics, member, method.Location, (INamedTypeSymbol)method.ResultType!,
                method.RateLimitRejects, method.OpenCircuitRejects, method.Retry?.NonThrowing == true);
        }
        else if (method.NonThrowingUnbuildable)
        {
            // Was a #error directive in the generated code, CS1029. The same check as ZR0003's
            // NonThrowing case for a foreign error type: the failure cannot be built.
            state.Diagnostics.Add(Diagnostic.Create(ResilienceDiagnostics.UnconstructibleResultError, method.Location,
                member.Name, member.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                "[Retry] with NonThrowing = true must return a ResilienceError failure when every attempt fails, and this return type cannot hold one",
                "Remove NonThrowing, or return Result<T, ResilienceError> or UnitResult<ResilienceError>"));
        }
        return true;
    }

    // Counts a method the interface-level [Retry] applies to, by whether it throws, for ZR0013.
    private static void CountInterfaceRetryMethod(InterfaceParse state, MethodParse method)
    {
        if (method.OwnRetry is not null || method.Retry is null) return;
        if (ReturnsFailures(method.ResultKind)) state.ClassRetryFailureMethods++;
        else state.ClassRetryThrowingMethods++;
    }

    private static MethodModel BuildMethodModel(InterfaceParse state, MethodParse method)
    {
        var member = method.Member;

        // What RetryWhen, RetryOnException and DelayHint resolve to for this method. ZR0010 is
        // reported for the interface's own methods and for inherited ones under the
        // interface's [Retry]; an inherited method's own [Retry] is the base interface's to
        // report.
        CountInterfaceRetryMethod(state, method);

        var retryMembers = method.Retry is null
            ? RetryMembers.None
            : ResolveRetryMembers(state.Diagnostics, state.RetryLookup, member, method.Retry, method.ErrorType,
                methodLevel: method.OwnRetry is not null,
                report: method.Entry.IsOwn || method.OwnRetry is null,
                method.Location);

        var argListWithToken = Arguments(member.Parameters, replaceCancellationToken: true);
        var slots = MethodSlots(state, method);
        var passthrough = method.Passthrough;

        return new MethodModel(
            Name: member.Name,
            ReturnTypeFqn: passthrough.ReturnTypeFqn,
            ResultKind: method.ResultKind,
            ResultTypeFqn: method.ResultKind == ResultKind.None ? null : method.ResultType!.ToDisplayString(FqnFormat),
            IsAsync: method.IsAsync,
            ReturnsValue: method.IsAsync ? method.ResultType is not null : !member.ReturnsVoid,
            HasCancellationToken: method.CancellationTokenParamName is not null,
            CancellationTokenParamName: method.CancellationTokenParamName,
            ParameterList: passthrough.ParameterList,
            ArgumentList: passthrough.ArgumentList,
            ArgumentListWithToken: argListWithToken,
            FallbackMethodName: method.FallbackName,
            Retry: method.Retry,
            Timeout: method.Timeout,
            RateLimit: method.RateLimit,
            CircuitBreaker: method.CircuitBreaker,
            RetrySlot: slots.Retry,
            TimeoutSlot: slots.Timeout,
            RateLimiterSlot: slots.RateLimiter,
            CircuitBreakerSlot: slots.CircuitBreaker,
            InnerReceiver: passthrough.InnerReceiver,
            FallbackReceiver: method.FallbackReceiver,
            TypeParameterList: passthrough.TypeParameterList,
            ConstraintClauses: passthrough.ConstraintClauses,
            OutParameterNames: OutParameterNames(member.Parameters),
            ExplicitImplementations: passthrough.ExplicitImplementations,
            ExplicitInterface: passthrough.ExplicitInterface,
            ExplicitConstraintClauses: passthrough.ExplicitConstraintClauses,
            HidesObjectMember: passthrough.HidesObjectMember,
            HelperName: method.DeclarationIndex == 1 ? member.Name : member.Name + method.DeclarationIndex.ToString(CultureInfo.InvariantCulture),
            RetryWhenMethod: retryMembers.RetryWhen,
            RetryOnExceptionMethod: retryMembers.RetryOnException,
            ResultDelayHintMethod: retryMembers.ResultDelayHint,
            ExceptionDelayHintMethod: retryMembers.ExceptionDelayHint,
            // [RetryAttempt] takes effect only inside the retry loop; without [Retry] the caller's
            // argument is passed, and ZR0011 says so.
            RetryArgumentList: method.Retry is null ? null
                : RetryArgumentList(member, replaceCancellationToken: method.CancellationTokenParamName is not null),
            FallbackArgumentList: method.Retry is null || method.FallbackName is null ? null : FallbackArgumentList(member));
    }

    // The slot each policy reads: the interface's, or the method's own when it has its own
    // attribute. No slot for a policy left off. Allocated retry, timeout, rate limiter, circuit
    // breaker, the order the slot names are numbered in.
    private static (PolicySlot? Retry, PolicySlot? Timeout, PolicySlot? RateLimiter, PolicySlot? CircuitBreaker) MethodSlots(
        InterfaceParse state, MethodParse method)
    {
        var name = method.Member.Name;
        var index = method.DeclarationIndex;
        var retrySlot = method.Retry is null ? null : method.OwnRetry is null ? state.ClassRetrySlot
            : state.Slots.AddMethodSlot(name, index, PolicyKind.Retry, PolicySlotBuilder.Default(method.OwnRetry));
        var timeoutSlot = method.Timeout is null ? null : method.OwnTimeout is null ? state.ClassTimeoutSlot
            : state.Slots.AddMethodSlot(name, index, PolicyKind.Timeout, PolicySlotBuilder.Default(method.OwnTimeout));
        var rateSlot = method.RateLimit is null ? null : method.OwnRateLimit is null ? state.ClassRateSlot
            : state.Slots.AddMethodSlot(name, index, PolicyKind.RateLimiter, PolicySlotBuilder.Default(method.OwnRateLimit));
        var cbSlot = method.CircuitBreaker is null ? null : method.OwnCb is null ? state.ClassCbSlot
            : state.Slots.AddMethodSlot(name, index, PolicyKind.CircuitBreaker, PolicySlotBuilder.Default(method.OwnCb));
        return (retrySlot, timeoutSlot, rateSlot, cbSlot);
    }

    // Properties, indexers and events never carry a resilience policy — only methods do — so
    // every one the interface declares or inherits is a plain forwarding member.
    private static ImmutableArray<PassthroughMemberModel> CollectPassthroughMembers(ImmutableArray<ForwardedMember> forwarded)
    {
        var passthroughMembersBuilder = ImmutableArray.CreateBuilder<PassthroughMemberModel>();

        foreach (var entry in forwarded)
        {
            if (entry.Symbol is IPropertySymbol property)
            {
                passthroughMembersBuilder.Add(PropertyPassthrough(entry, property));
            }
            else if (entry.Symbol is IEventSymbol evt)
            {
                passthroughMembersBuilder.Add(new PassthroughMemberModel(
                    Kind: PassthroughMemberKind.Event,
                    Name: evt.Name,
                    TypeFqn: evt.Type.ToDisplayString(FqnFormat),
                    HasGet: false,
                    HasSet: false,
                    HasInit: false,
                    InnerReceiver: entry.Receiver,
                    ExplicitImplementations: entry.ExplicitImplementations,
                    ExplicitInterface: entry.ExplicitInterface));
            }
        }

        return passthroughMembersBuilder.ToImmutable();
    }

    private static PassthroughMemberModel PropertyPassthrough(ForwardedMember entry, IPropertySymbol property)
    {
        // Only the accessors a class can implement and call: a private or protected
        // accessor, such as a default property's `private set`, is left out.
        var hasGet = HasPublicGetter(property);
        var hasInit = HasPublicSetter(property, initOnly: true);
        var hasSet = HasPublicSetter(property, initOnly: false);
        var typeFqn = ReturnRefPrefix(property.ReturnsByRef, property.ReturnsByRefReadonly) + property.Type.ToDisplayString(FqnFormat);
        var propertyReturnsByRef = property.ReturnsByRef || property.ReturnsByRefReadonly;

        if (property.IsIndexer)
        {
            return new PassthroughMemberModel(
                Kind: PassthroughMemberKind.Indexer,
                Name: "this",
                TypeFqn: typeFqn,
                HasGet: hasGet,
                HasSet: hasSet,
                HasInit: hasInit,
                ParameterList: ParameterDeclarations(property.Parameters),
                ArgumentList: Arguments(property.Parameters, replaceCancellationToken: false),
                InnerReceiver: entry.Receiver,
                ExplicitImplementations: entry.ExplicitImplementations,
                ExplicitInterface: entry.ExplicitInterface,
                ReturnsByRef: propertyReturnsByRef);
        }

        return new PassthroughMemberModel(
            Kind: PassthroughMemberKind.Property,
            Name: property.Name,
            TypeFqn: typeFqn,
            HasGet: hasGet,
            HasSet: hasSet,
            HasInit: hasInit,
            InnerReceiver: entry.Receiver,
            ExplicitImplementations: entry.ExplicitImplementations,
            ExplicitInterface: entry.ExplicitInterface,
            ReturnsByRef: propertyReturnsByRef);
    }

    // An internal interface, or a public one nested in a non-public type, cannot appear in a
    // public signature: the DI extension would fail with CS0703.
    private static bool IsEffectivelyPublic(INamedTypeSymbol symbol)
    {
        for (ISymbol? current = symbol; current is INamedTypeSymbol; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public) return false;
        }
        return true;
    }

    // ── Interface member collection ────────────────────────────────────────────

    // A member the proxy forwards. Receiver is the expression the forwarding call is made on.
    // Symbols never reach the incremental model: TryParse turns these into strings.
    // ExplicitImplementations: see MethodModel.ExplicitImplementations. ExplicitInterface: set
    // when this declaration is emitted as an explicit implementation of that interface.
    // SlotOnly: an own ordinary method the proxy does not forward, kept only for slot numbering.
    private sealed record ForwardedMember(ISymbol Symbol, bool IsOwn, string Receiver, string ExplicitImplementations = "", string ExplicitInterface = "", bool SlotOnly = false);

    // The interface's own members, then those of iface.AllInterfaces, deduplicated by symbol and
    // grouped by identity: declarations the proxy would implement with one public member. See
    // IsForwardable for what is skipped, and ResolveGroup for identities declared more than once.
    // Public members come first, own ones in declaration order, then the explicit
    // implementations, so the slot names of the interface's own methods never change.
    private static ImmutableArray<ForwardedMember> CollectForwardedMembers(INamedTypeSymbol iface)
    {
        var seen = new System.Collections.Generic.HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var groups = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<ForwardedMember>>(StringComparer.Ordinal);
        var order = new System.Collections.Generic.List<string>();

        AddFrom(iface, isOwn: true);
        foreach (var baseInterface in iface.AllInterfaces)
            AddFrom(baseInterface, isOwn: false);

        var publicMembers = new System.Collections.Generic.List<ForwardedMember>(order.Count);
        var explicitMembers = new System.Collections.Generic.List<ForwardedMember>();
        for (var i = 0; i < order.Count; i++)
            ResolveGroup(groups[order[i]], publicMembers, explicitMembers);
        ResolveKindClashes(publicMembers, explicitMembers);

        var result = ImmutableArray.CreateBuilder<ForwardedMember>(publicMembers.Count + explicitMembers.Count);
        result.AddRange(publicMembers);
        result.AddRange(explicitMembers);
        return result.ToImmutable();

        void AddFrom(INamedTypeSymbol type, bool isOwn)
        {
            foreach (var member in type.GetMembers())
            {
                if (!IsForwardable(iface, member, isOwn))
                {
                    // 2.0.1 numbered every own ordinary method, forwarded or not: a sealed method
                    // or a redeclared object member still takes its declaration number.
                    if (isOwn && member is IMethodSymbol { MethodKind: MethodKind.Ordinary })
                    {
                        var slotKey = "S:" + order.Count.ToString(CultureInfo.InvariantCulture);
                        groups.Add(slotKey, new System.Collections.Generic.List<ForwardedMember>
                        {
                            new(member, IsOwn: true, Receiver: "_inner", SlotOnly: true),
                        });
                        order.Add(slotKey);
                    }
                    continue;
                }
                if (!seen.Add(member)) continue;

                var key = IdentityKey(member);
                if (!groups.TryGetValue(key, out var group))
                {
                    group = new System.Collections.Generic.List<ForwardedMember>();
                    groups.Add(key, group);
                    order.Add(key);
                }
                group.Add(new ForwardedMember(member, isOwn, isOwn ? "_inner" : InnerReceiver(iface, member)));
            }
        }
    }

    // Whether the proxy implements this member. Default-implemented members are forwarded like
    // abstract ones, so an override in the inner service wins over the interface's default body.
    private static bool IsForwardable(INamedTypeSymbol iface, ISymbol member, bool isOwn)
    {
        var kindForwarded = member switch
        {
            IMethodSymbol method => method.MethodKind == MethodKind.Ordinary,
            IPropertySymbol or IEventSymbol => true,
            _ => false,
        };
        if (!kindForwarded || member.IsStatic) return false;

        // A sealed interface member is neither abstract nor virtual: no class can implement it,
        // so there is nothing to forward.
        if (!member.IsAbstract && !member.IsVirtual) return false;

        // ToString(), Equals(object) and GetHashCode() with object's signatures are implemented
        // by object's own members on the proxy, as in 2.0.1. Forwarding them would break the
        // proxy's own equality: an inner with reference equality is not equal to the proxy.
        if (member is IMethodSymbol objectMember && IsObjectMemberSignature(objectMember)) return false;

        // Private and protected interface members cannot be implemented by a public class member.
        if (member.DeclaredAccessibility != Accessibility.Public) return false;

        // An explicit implementation declared in an interface, such as `string IBase.Name => "x";`.
        if (HasExplicitImplementations(member)) return false;

        // An inherited member a more derived interface already implements, such as
        // `object IRepo.Get(int id) => Get(id);` in IRepo2 : IRepo, is satisfied and must not be
        // forwarded. OriginalDefinition, so a member of a constructed generic base still matches
        // its own declaration.
        if (!isOwn
            && iface.FindImplementationForInterfaceMember(member) is { } implementation
            && !SymbolEqualityComparer.Default.Equals(
                implementation.ContainingType.OriginalDefinition, member.ContainingType.OriginalDefinition))
            return false;

        return true;
    }

    private static bool HasExplicitImplementations(ISymbol member) => member switch
    {
        IMethodSymbol method => method.ExplicitInterfaceImplementations.Length > 0,
        IPropertySymbol property => property.ExplicitInterfaceImplementations.Length > 0,
        IEventSymbol evt => evt.ExplicitInterfaceImplementations.Length > 0,
        _ => false,
    };

    // "_inner" for the interface's own members; a cast to the declaring interface for an
    // inherited one, so the call binds to exactly that declaration: never ambiguous between two
    // base interfaces, and never to a member of the same name that a derived interface declares.
    private static string InnerReceiver(INamedTypeSymbol iface, ISymbol member) =>
        SymbolEqualityComparer.Default.Equals(member.ContainingType, iface)
            ? "_inner"
            : $"(({member.ContainingType.ToDisplayString(FqnFormat)})_inner)";

    // The emitted identity: two declarations with the same key are implemented by one proxy
    // member. Nullability-agnostic, so `string?` and `string` are the same identity.
    private static string IdentityKey(ISymbol member) => member switch
    {
        IMethodSymbol method => $"M:{method.Name}`{method.Arity.ToString(CultureInfo.InvariantCulture)}({ParameterTypes(method.Parameters)})",
        IPropertySymbol { IsIndexer: true } indexer => $"I:[{ParameterTypes(indexer.Parameters)}]",
        IPropertySymbol property => $"P:{property.Name}",
        _ => $"E:{member.Name}",
    };

    private static string ParameterTypes(ImmutableArray<IParameterSymbol> parameters) =>
        string.Join(",", parameters.Select(static p =>
            RefKindPrefix(p.RefKind) + TypeKey(p.Type, SymbolDisplayFormat.FullyQualifiedFormat)));

    // A type rendered for comparison, with a method's type parameters by ordinal: `!!0`. Foo<T>(T)
    // and Foo<U>(U) are one identity, and one member implements both.
    private static string TypeKey(ITypeSymbol type, SymbolDisplayFormat format)
    {
        var key = new System.Text.StringBuilder();
        foreach (var part in type.ToDisplayParts(format))
        {
            if (part.Symbol is ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Method } typeParameter)
                key.Append("!!").Append(typeParameter.Ordinal.ToString(CultureInfo.InvariantCulture));
            else
                key.Append(part.ToString());
        }
        return key.ToString();
    }

    // The constraints of a method's type parameters, by ordinal, for comparison.
    private static string ConstraintKey(IMethodSymbol method)
    {
        var key = new System.Text.StringBuilder();
        key.Append('`').Append(method.Arity.ToString(CultureInfo.InvariantCulture));
        foreach (var typeParameter in method.TypeParameters)
        {
            key.Append(" !!").Append(typeParameter.Ordinal.ToString(CultureInfo.InvariantCulture)).Append(':');
            if (typeParameter.HasReferenceTypeConstraint)
                key.Append(typeParameter.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated ? "class?," : "class,");
            if (typeParameter.HasUnmanagedTypeConstraint) key.Append("unmanaged,");
            else if (typeParameter.HasValueTypeConstraint) key.Append("struct,");
            if (typeParameter.HasNotNullConstraint) key.Append("notnull,");
            foreach (var constraintType in typeParameter.ConstraintTypes)
                key.Append(TypeKey(constraintType, FqnFormat)).Append(',');
            if (typeParameter.HasConstructorConstraint) key.Append("new(),");
            if (typeParameter.AllowsRefLikeType) key.Append("allows ref struct,");
        }
        return key.ToString();
    }

    private static string ReturnRefPrefix(bool returnsByRef, bool returnsByRefReadonly) =>
        returnsByRefReadonly ? "ref readonly " : returnsByRef ? "ref " : "";

    private static bool HasPublicGetter(IPropertySymbol property) =>
        property.GetMethod is { DeclaredAccessibility: Accessibility.Public };

    private static bool HasPublicSetter(IPropertySymbol property, bool initOnly) =>
        property.SetMethod is { DeclaredAccessibility: Accessibility.Public } setter && setter.IsInitOnly == initOnly;

    // ── Signatures: parameters, arguments, type parameters ─────────────────────

    // "ref int counter, out string value, params int[] values"
    private static string ParameterDeclarations(ImmutableArray<IParameterSymbol> parameters) =>
        string.Join(", ", parameters.Select(static p =>
            (p.IsParams ? "params " : RefKindPrefix(p.RefKind)) + $"{p.Type.ToDisplayString(FqnFormat)} {EscapedName(p)}"));

    // "ref counter, out value, values"; with replaceCancellationToken, the CancellationToken
    // argument is "__ct", the token the policies link to.
    // substitute, when given, returns the argument for a parameter, or null for the default one.
    private static string Arguments(ImmutableArray<IParameterSymbol> parameters, bool replaceCancellationToken,
        Func<IParameterSymbol, string?>? substitute = null) =>
        string.Join(", ", parameters.Select(p =>
            ArgumentPrefix(p.RefKind)
            + (substitute?.Invoke(p)
               ?? (replaceCancellationToken
                   && string.Equals(p.Type.ToDisplayString(), "System.Threading.CancellationToken", StringComparison.Ordinal)
                    ? "__ct" : EscapedName(p)))));

    private static string ArgumentPrefix(RefKind refKind) => refKind switch
    {
        RefKind.Ref => "ref ",
        RefKind.Out => "out ",
        RefKind.In or RefKind.RefReadOnlyParameter => "in ",
        _ => "",
    };

    private static string EscapedName(IParameterSymbol parameter) => EscapeIdentifier(parameter.Name);

    // "@checked" for a name that is a C# keyword.
    private static string EscapeIdentifier(string name) =>
        Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(name) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None
            ? "@" + name
            : name;

    // "<T, TOut>" or "".
    private static string TypeParameterList(IMethodSymbol method) =>
        method.TypeParameters.Length == 0
            ? ""
            : "<" + string.Join(", ", method.TypeParameters.Select(static t => t.Name)) + ">";

    // " where T : class, new() where TOut : notnull" or "". Copied from the interface method, since
    // an implicit implementation must repeat its constraints.
    private static string ConstraintClauses(IMethodSymbol method)
    {
        var clauses = new System.Text.StringBuilder();
        foreach (var typeParameter in method.TypeParameters)
        {
            var constraints = new System.Collections.Generic.List<string>();
            if (typeParameter.HasReferenceTypeConstraint)
                constraints.Add(typeParameter.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated ? "class?" : "class");
            else if (typeParameter.HasUnmanagedTypeConstraint)
                constraints.Add("unmanaged");
            else if (typeParameter.HasValueTypeConstraint)
                constraints.Add("struct");
            else if (typeParameter.HasNotNullConstraint)
                constraints.Add("notnull");
            foreach (var constraintType in typeParameter.ConstraintTypes)
                constraints.Add(constraintType.ToDisplayString(FqnFormat));
            if (typeParameter.HasConstructorConstraint)
                constraints.Add("new()");
            if (typeParameter.AllowsRefLikeType)
                constraints.Add("allows ref struct");
            if (constraints.Count > 0)
                clauses.Append(" where ").Append(typeParameter.Name).Append(" : ").Append(string.Join(", ", constraints));
        }
        return clauses.ToString();
    }

    // The only constraints an explicit implementation may, and for `T?` must, state: `class` for a
    // reference-type T and `default` for an unconstrained one, where the signature uses `T?`.
    // Without them `T?` would mean Nullable<T>. Every other constraint is inherited.
    private static string ExplicitConstraintClauses(IMethodSymbol method)
    {
        var clauses = new System.Text.StringBuilder();
        foreach (var typeParameter in method.TypeParameters)
        {
            var annotated = UsesAnnotated(method.ReturnType, typeParameter);
            foreach (var parameter in method.Parameters)
                annotated |= UsesAnnotated(parameter.Type, typeParameter);
            if (!annotated) continue;
            if (typeParameter.IsReferenceType)
                clauses.Append(" where ").Append(typeParameter.Name).Append(" : class");
            else if (!typeParameter.IsValueType)
                clauses.Append(" where ").Append(typeParameter.Name).Append(" : default");
        }
        return clauses.ToString();
    }

    private static bool UsesAnnotated(ITypeSymbol type, ITypeParameterSymbol typeParameter) => type switch
    {
        ITypeParameterSymbol candidate => candidate.NullableAnnotation == NullableAnnotation.Annotated
            && SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, typeParameter.OriginalDefinition),
        IArrayTypeSymbol array => UsesAnnotated(array.ElementType, typeParameter),
        INamedTypeSymbol named => named.TypeArguments.Any(argument => UsesAnnotated(argument, typeParameter)),
        _ => false,
    };

    // ToString(), GetHashCode() and Equals(object) redeclared in an interface: object's own
    // members implement them.
    private static bool IsObjectMemberSignature(IMethodSymbol method) =>
        method.Arity == 0 && !method.ReturnsByRef && !method.ReturnsByRefReadonly && method.Name switch
        {
            "ToString" => method.Parameters.Length == 0 && method.ReturnType.SpecialType == SpecialType.System_String,
            "GetHashCode" => method.Parameters.Length == 0 && method.ReturnType.SpecialType == SpecialType.System_Int32,
            "Equals" => method.Parameters.Length == 1
                && method.Parameters[0].RefKind == RefKind.None
                && method.Parameters[0].Type.SpecialType == SpecialType.System_Object
                && method.ReturnType.SpecialType == SpecialType.System_Boolean,
            _ => false,
        };

    // Why no policy can wrap a method, or null when one can.
    private static string? UnwrappableReason(bool isAsync, bool hasByRefOrRefLikeParameter, bool returnsByRef)
    {
        if (returnsByRef)
            return "it returns by reference, and a policy-wrapped method cannot return a reference to the inner result";
        if (isAsync && hasByRefOrRefLikeParameter)
            return "it is async and has a ref, out, in or ref struct parameter, which a policy-wrapped async method cannot have";
        return null;
    }

    private static string RenderedParameters(ImmutableArray<IParameterSymbol> parameters) =>
        string.Join(",", parameters.Select(static p => RefKindPrefix(p.RefKind) + TypeKey(p.Type, FqnFormat)));

    private static string RefKindPrefix(RefKind refKind) => refKind switch
    {
        RefKind.Ref => "ref ",
        RefKind.Out => "out ",
        RefKind.In => "in ",
        RefKind.RefReadOnlyParameter => "ref readonly ",
        _ => "",
    };

    // What the one proxy member for an identity has to agree on: its type and accessors, and for
    // a method its own resilience attributes. Nullability-agnostic, like IdentityKey.
    private static string Shape(ISymbol member) => member switch
    {
        IMethodSymbol method => ReturnRefPrefix(method.ReturnsByRef, method.ReturnsByRefReadonly)
            + TypeKey(method.ReturnType, SymbolDisplayFormat.FullyQualifiedFormat),
        IPropertySymbol property =>
            ReturnRefPrefix(property.ReturnsByRef, property.ReturnsByRefReadonly)
            + $"{property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}"
            + (HasPublicGetter(property) ? " get" : "")
            + (HasPublicSetter(property, initOnly: false) ? " set" : "")
            + (HasPublicSetter(property, initOnly: true) ? " init" : ""),
        IEventSymbol evt => evt.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        _ => "",
    };

    // What two inherited declarations must agree on to collapse: the exact rendering, nullable
    // annotations included, and for a method its own resilience attributes, [RetryAttempt] included.
    private static string RenderingAndPolicies(ISymbol member)
    {
        var rendering = member switch
        {
            IPropertySymbol p =>
                ReturnRefPrefix(p.ReturnsByRef, p.ReturnsByRefReadonly) + $"{p.Type.ToDisplayString(FqnFormat)}[{RenderedParameters(p.Parameters)}]"
                + (HasPublicGetter(p) ? " get" : "")
                + (HasPublicSetter(p, initOnly: false) ? " set" : "")
                + (HasPublicSetter(p, initOnly: true) ? " init" : ""),
            IMethodSymbol m => ReturnRefPrefix(m.ReturnsByRef, m.ReturnsByRefReadonly)
                + $"{TypeKey(m.ReturnType, FqnFormat)}{ConstraintKey(m)}({RenderedParameters(m.Parameters)})",
            IEventSymbol e => e.Type.ToDisplayString(FqnFormat),
            _ => "",
        };
        if (member is not IMethodSymbol method) return rendering;
        var cb = GetAttribute(method, CircuitBreakerFqn);
        return rendering
            + "|" + ParseRetry(GetAttribute(method, RetryFqn))?.ToString()
            + "|" + ParseTimeout(GetAttribute(method, TimeoutFqn))?.ToString()
            + "|" + ParseRateLimit(GetAttribute(method, RateLimitFqn))?.ToString()
            + "|" + ParseCircuitBreaker(cb)?.ToString()
            + "|" + (cb is null ? null : GetString(cb, "Fallback"))
            + "|" + RetryAttemptOrdinals(method);
    }

    // One identity declared more than once: by the interface itself and a base it hides, by two
    // unrelated base interfaces, or by two interfaces along one inheritance path through `new`.
    // One declaration becomes the public member. Declarations that render exactly like it,
    // nullable annotations and resilience attributes included, collapse into it. Every other
    // declaration gets an explicit interface implementation with its own exact signature, again
    // collapsing the ones that render identically, so a correct proxy exists for every conflict.
    private static void ResolveGroup(
        System.Collections.Generic.List<ForwardedMember> group,
        System.Collections.Generic.List<ForwardedMember> publicMembers,
        System.Collections.Generic.List<ForwardedMember> explicitMembers)
    {
        if (group.Count == 1)
        {
            publicMembers.Add(group[0]);
            return;
        }

        System.Collections.Generic.List<ForwardedMember> remaining;
        var own = group.Find(static m => m.IsOwn);
        if (own is not null)
        {
            // The interface's own declaration is public and, as before inherited members were
            // forwarded, implements every inherited declaration with the same shape.
            publicMembers.Add(own);
            var ownShape = Shape(own.Symbol);
            remaining = group.FindAll(m => !m.IsOwn && !string.Equals(Shape(m.Symbol), ownShape, StringComparison.Ordinal));
        }
        else
        {
            var primary = MostDerived(group);
            var primaryRendering = RenderingAndPolicies(primary.Symbol);
            publicMembers.Add(Collapse(primary, group.FindAll(m =>
                string.Equals(RenderingAndPolicies(m.Symbol), primaryRendering, StringComparison.Ordinal))));
            remaining = group.FindAll(m =>
                !string.Equals(RenderingAndPolicies(m.Symbol), primaryRendering, StringComparison.Ordinal));
        }

        while (remaining.Count > 0)
        {
            var first = remaining[0];
            var rendering = RenderingAndPolicies(first.Symbol);
            var same = remaining.FindAll(m => string.Equals(RenderingAndPolicies(m.Symbol), rendering, StringComparison.Ordinal));
            explicitMembers.Add(Collapse(first, same) with { ExplicitInterface = DeclaringInterface(first) });
            remaining.RemoveAll(m => string.Equals(RenderingAndPolicies(m.Symbol), rendering, StringComparison.Ordinal));
        }
    }

    // A property, an event and a method with the same name cannot all be public members of one
    // class: CS0102. The kind of the interface's own member, else of the most-derived declaration,
    // stays public; the members of the other kinds become explicit implementations.
    private static void ResolveKindClashes(
        System.Collections.Generic.List<ForwardedMember> publicMembers,
        System.Collections.Generic.List<ForwardedMember> explicitMembers)
    {
        var byName = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<ForwardedMember>>(StringComparer.Ordinal);
        for (var i = 0; i < publicMembers.Count; i++)
        {
            var member = publicMembers[i];
            if (member.SlotOnly) continue;
            var name = ClashName(member.Symbol);
            if (!byName.TryGetValue(name, out var sameName))
            {
                sameName = new System.Collections.Generic.List<ForwardedMember>();
                byName.Add(name, sameName);
            }
            sameName.Add(member);
        }

        foreach (var sameName in byName.Values)
        {
            var firstKind = ClashKind(sameName[0].Symbol);
            if (sameName.TrueForAll(m => ClashKind(m.Symbol) == firstKind)) continue;

            var winner = sameName.Find(static m => m.IsOwn) ?? MostDerived(sameName);
            var winnerKind = ClashKind(winner.Symbol);
            for (var i = 0; i < sameName.Count; i++)
            {
                var member = sameName[i];
                if (ClashKind(member.Symbol) == winnerKind) continue;
                publicMembers.Remove(member);
                explicitMembers.Add(member with { ExplicitInterface = DeclaringInterface(member) });
            }
        }
    }

    // An indexer is named Item in metadata, so it clashes with a property or method named Item.
    private static string ClashName(ISymbol member) =>
        member is IPropertySymbol { IsIndexer: true } indexer ? indexer.MetadataName : member.Name;

    // 0 method, 1 property, 2 indexer, 3 event: indexers overload each other, but not a property.
    private static int ClashKind(ISymbol member) => member switch
    {
        IMethodSymbol => 0,
        IPropertySymbol { IsIndexer: true } => 2,
        IPropertySymbol => 1,
        _ => 3,
    };

    // The declaration no other one hides: along an inheritance path the most-derived one, whose
    // `new` hides the others; between unrelated bases the first in AllInterfaces order.
    private static ForwardedMember MostDerived(System.Collections.Generic.List<ForwardedMember> declarations)
    {
        for (var i = 0; i < declarations.Count; i++)
        {
            var declaringType = declarations[i].Symbol.ContainingType;
            var hidden = false;
            for (var j = 0; j < declarations.Count && !hidden; j++)
            {
                if (i == j) continue;
                foreach (var baseInterface in declarations[j].Symbol.ContainingType.AllInterfaces)
                {
                    if (!SymbolEqualityComparer.Default.Equals(baseInterface, declaringType)) continue;
                    hidden = true;
                    break;
                }
            }
            if (!hidden) return declarations[i];
        }
        return declarations[0];
    }

    // The first declaration, forwarding the others that render identically through explicit
    // implementations of their own interfaces.
    private static ForwardedMember Collapse(ForwardedMember primary, System.Collections.Generic.List<ForwardedMember> same)
    {
        var others = new System.Text.StringBuilder();
        for (var i = 0; i < same.Count; i++)
        {
            if (ReferenceEquals(same[i], primary)) continue;
            if (others.Length > 0) others.Append('|');
            others.Append(DeclaringInterface(same[i]));
        }
        return primary with { ExplicitImplementations = others.ToString() };
    }

    private static string DeclaringInterface(ForwardedMember member) =>
        member.Symbol.ContainingType.ToDisplayString(FqnFormat);

    // The fallback for `method`: a public instance method of that name, declared or inherited,
    // whose signature matches. Every overload of the name is considered.
    private static IMethodSymbol? FindFallback(INamedTypeSymbol iface, string name, IMethodSymbol method)
    {
        var fallback = FindFallbackIn(iface, name, method);
        foreach (var baseInterface in iface.AllInterfaces)
            fallback ??= FindFallbackIn(baseInterface, name, method);
        return fallback;
    }

    private static IMethodSymbol? FindFallbackIn(INamedTypeSymbol type, string name, IMethodSymbol method)
    {
        foreach (var candidate in type.GetMembers(name))
        {
            if (candidate is IMethodSymbol { MethodKind: MethodKind.Ordinary, IsStatic: false, DeclaredAccessibility: Accessibility.Public } fallback
                && SignaturesMatch(method, fallback))
                return fallback;
        }
        return null;
    }

    // ── Attribute helpers ──────────────────────────────────────────────────────

    private static AttributeData? GetAttribute(ISymbol symbol, string fqn) =>
        symbol.GetAttributes().FirstOrDefault(a =>
            string.Equals(a.AttributeClass?.ToDisplayString(), fqn, StringComparison.Ordinal));

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
            DelayHint: GetString(attr, "DelayHint"),
            RethrowDeclined: GetBool(attr, "RethrowDeclined", false));
    }

    private static TimeoutConfig? ParseTimeout(AttributeData? attr)
    {
        if (attr is null) return null;
        return new TimeoutConfig(TotalMs: GetInt(attr, "Ms", 0));
    }

    private static RateLimitConfig? ParseRateLimit(AttributeData? attr)
    {
        if (attr is null) return null;
        var scopeInt = GetInt(attr, "Scope", 0);
        var scope = scopeInt == 1 ? RateLimitScope.Instance : RateLimitScope.Shared;
        return new RateLimitConfig(
            MaxPerSecond: GetInt(attr, "MaxPerSecond", 0),
            BurstSize: GetInt(attr, "BurstSize", 1),
            Scope: scope);
    }

    private static CircuitBreakerConfig? ParseCircuitBreaker(AttributeData? attr)
    {
        if (attr is null) return null;
        return new CircuitBreakerConfig(
            MaxFailures: GetInt(attr, "MaxFailures", 5),
            ResetMs: GetInt(attr, "ResetMs", 1_000),
            HalfOpenProbes: GetInt(attr, "HalfOpenProbes", 1));
        // Note: FallbackMethod is NOT on CircuitBreakerConfig — it's read directly from the attribute in the method loop
    }

    private static int GetInt(AttributeData attr, string name, int defaultValue)
    {
        foreach (var kv in attr.NamedArguments)
            if (string.Equals(kv.Key, name, StringComparison.Ordinal) && kv.Value.Value is int v)
                return v;
        return defaultValue;
    }

    private static bool GetBool(AttributeData attr, string name, bool defaultValue)
    {
        foreach (var kv in attr.NamedArguments)
            if (string.Equals(kv.Key, name, StringComparison.Ordinal) && kv.Value.Value is bool v)
                return v;
        return defaultValue;
    }

    private static string? GetString(AttributeData attr, string name)
    {
        foreach (var kv in attr.NamedArguments)
            if (string.Equals(kv.Key, name, StringComparison.Ordinal))
                return kv.Value.Value as string;
        return null;
    }

    private static bool TryGetInt(AttributeData attr, string name, out int value)
    {
        foreach (var kv in attr.NamedArguments)
        {
            if (string.Equals(kv.Key, name, StringComparison.Ordinal) && kv.Value.Value is int v)
            {
                value = v;
                return true;
            }
        }
        value = default;
        return false;
    }

    // ── ZR0004: invalid policy attribute values ────────────────────────────────
    // One rule per property the runtime policy constructor validates with
    // ArgumentOutOfRangeException. A property that is never explicitly set gets the
    // constructor's own default, which is always valid, so unset properties are skipped.

    private readonly record struct AttributeRule(string Property, int Minimum, bool Exclusive, string RuleText)
    {
        public bool IsValid(int value) => Exclusive ? value > Minimum : value >= Minimum;
    }

    private static readonly ImmutableArray<AttributeRule> RetryRules = ImmutableArray.Create(
        new AttributeRule("MaxAttempts", 1, Exclusive: false, "at least 1"),
        new AttributeRule("BackoffMs", 0, Exclusive: false, "at least 0"),
        new AttributeRule("PerAttemptTimeoutMs", 0, Exclusive: false, "at least 0"),
        new AttributeRule("MaxDelayMs", 0, Exclusive: false, "at least 0"));

    private static readonly ImmutableArray<AttributeRule> TimeoutRules = ImmutableArray.Create(
        new AttributeRule("Ms", 0, Exclusive: true, "greater than 0"));

    private static readonly ImmutableArray<AttributeRule> CircuitBreakerRules = ImmutableArray.Create(
        new AttributeRule("MaxFailures", 1, Exclusive: false, "at least 1"),
        new AttributeRule("ResetMs", 0, Exclusive: false, "at least 0"),
        new AttributeRule("HalfOpenProbes", 1, Exclusive: false, "at least 1"));

    private static readonly ImmutableArray<AttributeRule> RateLimitRules = ImmutableArray.Create(
        new AttributeRule("MaxPerSecond", 0, Exclusive: false, "at least 0"),
        new AttributeRule("BurstSize", 0, Exclusive: false, "at least 0"));

    // Reports one ZR0004 per invalid, explicitly-set property. Location prefers where the
    // attribute is written (its ApplicationSyntaxReference), falling back to the member's own
    // location when no syntax reference is available (e.g. attributes from metadata).
    private static void ValidateAttributeValues(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        AttributeData? attr,
        ImmutableArray<AttributeRule> rules,
        string memberName,
        Location? fallbackLocation)
    {
        if (attr is null) return;

        var location = AttributeLocation(attr, fallbackLocation);
        var attributeDisplay = AttributeDisplayName(attr);

        foreach (var rule in rules)
        {
            if (!TryGetInt(attr, rule.Property, out var value)) continue;
            if (rule.IsValid(value)) continue;

            diagnostics.Add(Diagnostic.Create(
                ResilienceDiagnostics.InvalidAttributeValue,
                location,
                attributeDisplay, memberName, rule.Property, rule.RuleText, value));
        }
    }

    // Where the attribute is written, falling back to the member's own location when there is no
    // syntax reference, such as for an attribute read from metadata.
    private static Location? AttributeLocation(AttributeData attr, Location? fallbackLocation) =>
        attr.ApplicationSyntaxReference is { } syntaxRef
            ? Location.Create(syntaxRef.SyntaxTree, syntaxRef.Span)
            : fallbackLocation;

    // "RetryAttribute" -> "[Retry]"
    private static string AttributeDisplayName(AttributeData attr)
    {
        var name = attr.AttributeClass?.Name ?? string.Empty;
        if (name.EndsWith("Attribute", StringComparison.Ordinal))
            name = name.Substring(0, name.Length - "Attribute".Length);
        return $"[{name}]";
    }

    // ── Return type helpers ────────────────────────────────────────────────────

    private static bool IsAsyncType(ITypeSymbol type)
    {
        var name = type.OriginalDefinition.ToDisplayString();
        return name is "System.Threading.Tasks.ValueTask"
                   or "System.Threading.Tasks.ValueTask<TResult>"
                   or "System.Threading.Tasks.Task"
                   or "System.Threading.Tasks.Task<TResult>";
    }

    private static ITypeSymbol? UnwrapAsyncType(ITypeSymbol returnType) =>
        returnType is INamedTypeSymbol { TypeArguments.Length: 1 } named ? named.TypeArguments[0] : null;

    // Only the Result types of ZeroAlloc.Results: Result, Result<T>, Result<T, E> and UnitResult<E>.
    private static ResultKind ClassifyResult(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named
            || !string.Equals(named.ContainingNamespace?.ToDisplayString(), "ZeroAlloc.Results", StringComparison.Ordinal))
            return ResultKind.None;

        return (named.Name, named.TypeArguments.Length) switch
        {
            ("Result", 0 or 1) => ResultKind.StringError,
            ("Result", 2) or ("UnitResult", 1) => IsResilienceError(named.TypeArguments[named.TypeArguments.Length - 1])
                ? ResultKind.ResilienceError
                : ResultKind.ForeignError,
            _ => ResultKind.None,
        };
    }

    private static bool IsResilienceError(ITypeSymbol type) =>
        string.Equals(type.ToDisplayString(), "ZeroAlloc.Resilience.ResilienceError", StringComparison.Ordinal);

    private static void ReportUnconstructibleError(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        IMethodSymbol method,
        Location? location,
        INamedTypeSymbol resultType,
        bool rateLimitRejects,
        bool openCircuitRejects,
        bool nonThrowing)
    {
        var resultDisplay = resultType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var errorDisplay = resultType.TypeArguments[resultType.TypeArguments.Length - 1]
            .ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        if (rateLimitRejects)
            diagnostics.Add(Diagnostic.Create(ResilienceDiagnostics.UnconstructibleResultError, location,
                method.Name, resultDisplay, CannotConstruct("[RateLimit]", errorDisplay),
                "Remove [RateLimit] from this method, or return Result<T, ResilienceError>"));
        if (openCircuitRejects)
            diagnostics.Add(Diagnostic.Create(ResilienceDiagnostics.UnconstructibleResultError, location,
                method.Name, resultDisplay, CannotConstruct("[CircuitBreaker] without a Fallback", errorDisplay),
                "Set Fallback to a method with the same signature, or return Result<T, ResilienceError>"));
        if (nonThrowing)
            diagnostics.Add(Diagnostic.Create(ResilienceDiagnostics.UnconstructibleResultError, location,
                method.Name, resultDisplay, CannotConstruct("[Retry] with NonThrowing = true", errorDisplay),
                "Remove NonThrowing, since retry already passes the inner Result through, or return Result<T, ResilienceError>"));

    }

    private static void ReportPolicyNotApplied(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        Location? location,
        INamedTypeSymbol iface,
        IMethodSymbol method,
        string policy,
        string reason,
        string advice)
    {
        diagnostics.Add(Diagnostic.Create(
            ResilienceDiagnostics.PolicyNotAppliedToInheritedMethod,
            location,
            "Inherited method", method.Name, iface.Name, "is forwarded to the inner service", policy, reason, advice));
    }

    // ZR0006 for an own method the proxy does not implement, which 2.0.1 did forward: its
    // method-level policies, and for an object member also the interface-level ones, which 2.0.1
    // applied to it, do not apply.
    private static void ReportSkippedMethodPolicies(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        INamedTypeSymbol iface,
        IMethodSymbol method,
        ImmutableArray<AttributeData?> interfacePolicies)
    {
        var isObjectMember = IsObjectMemberSignature(method);
        var fromOwnAttribute = false;
        var policies = new System.Collections.Generic.List<string>();
        var kinds = new[] { RetryFqn, TimeoutFqn, RateLimitFqn, CircuitBreakerFqn };
        for (var i = 0; i < kinds.Length; i++)
        {
            var own = GetAttribute(method, kinds[i]);
            var applied = own ?? (isObjectMember ? interfacePolicies[i] : null);
            if (applied is null) continue;
            fromOwnAttribute |= own is not null;
            policies.Add(AttributeDisplayName(applied));
        }
        if (policies.Count == 0) return;

        var policy = string.Join(", ", policies);
        var reason = method.IsStatic ? "it is static, and a proxy instance cannot implement a static member"
            : method.DeclaredAccessibility != Accessibility.Public ? "it is not public, so the proxy cannot implement it"
            : isObjectMember ? $"it has the signature of object.{method.Name}, so object's own {method.Name} implements it on the proxy"
            : "it is sealed, so no class can implement it and a call runs its interface body";
        var advice = fromOwnAttribute
            ? $"Remove {policy} from '{iface.Name}.{method.Name}'"
            : $"Give '{method.Name}' another name if it should have the policies of '{iface.Name}'";
        diagnostics.Add(Diagnostic.Create(
            ResilienceDiagnostics.PolicyNotAppliedToInheritedMethod,
            method.Locations.FirstOrDefault(),
            "Method", method.Name, iface.Name, "is not implemented by the proxy, so it runs", policy, reason, advice));
    }

    // Instance methods of object that a public proxy method with the same name and parameters
    // hides: ToString, GetHashCode, GetType, MemberwiseClone, Equals(object), and the static
    // Equals(object, object) and ReferenceEquals(object, object).
    private static bool HidesObjectMember(IMethodSymbol method)
    {
        if (method.Arity != 0) return false;
        foreach (var parameter in method.Parameters)
        {
            if (parameter.RefKind != RefKind.None || parameter.Type.SpecialType != SpecialType.System_Object)
                return false;
        }
        return (method.Name, method.Parameters.Length) switch
        {
            ("ToString" or "GetHashCode" or "GetType" or "MemberwiseClone", 0) => true,
            ("Equals", 1) => true,
            ("Equals" or "ReferenceEquals", 2) => true,
            _ => false,
        };
    }

    private static string CannotConstruct(string policy, string errorDisplay) =>
        $"{policy} must return a failure when there is no inner Result to return, but the generator cannot construct the error type '{errorDisplay}'";

    // "the generator cannot build the error type 'MyError' of 'Result<int, MyError>'"
    private static string UnbuildableError(ITypeSymbol? resultType)
    {
        if (resultType is not INamedTypeSymbol { TypeArguments.Length: > 0 } named)
            return "the generator cannot build a failure of this return type";
        var errorDisplay = named.TypeArguments[named.TypeArguments.Length - 1]
            .ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        return $"the generator cannot build the error type '{errorDisplay}' of '{named.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}'";
    }

    // Where the fix goes: the base interface's method when the policy is that method's own
    // attribute, the proxied interface when it is an interface-level attribute.
    private static string PolicyNotAppliedAdvice(INamedTypeSymbol iface, IMethodSymbol method, string policy, bool fromOwnAttribute) =>
        fromOwnAttribute
            ? $"{policy} comes from the attribute on '{method.ContainingType.Name}.{method.Name}': remove it there, or declare '{method.Name}' on '{iface.Name}' with a return type the policy supports"
            : $"Remove {policy} from '{iface.Name}' and put it on the methods that need it, or declare '{method.Name}' on '{iface.Name}' with a return type the policy supports";

    // ── ZR0007: unsupported interface shapes ───────────────────────────────────

    private static ImmutableArray<Diagnostic> UnsupportedShapeDiagnostics(INamedTypeSymbol iface)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var location = iface.Locations.FirstOrDefault();

        // The proxy and policies classes are not generic, so they cannot name T.
        for (var type = iface; type is not null; type = type.ContainingType)
        {
            if (type.Arity == 0) continue;
            diagnostics.Add(Diagnostic.Create(ResilienceDiagnostics.UnsupportedInterfaceShape, location, iface.Name,
                SymbolEqualityComparer.Default.Equals(type, iface)
                    ? "it is generic, and the generated proxy and policies classes are not"
                    : $"it is nested in the generic type '{type.Name}', and the generated proxy and policies classes are not generic"));
            break;
        }

        // The generated classes are top-level, so they must be able to see the interface.
        for (var type = iface; type is not null; type = type.ContainingType)
        {
            var accessibility = type.DeclaredAccessibility switch
            {
                Accessibility.Private => "private",
                Accessibility.Protected => "protected",
                Accessibility.ProtectedAndInternal => "private protected",
                _ => null,
            };
            if (accessibility is null) continue;
            diagnostics.Add(Diagnostic.Create(ResilienceDiagnostics.UnsupportedInterfaceShape, location, iface.Name,
                SymbolEqualityComparer.Default.Equals(type, iface)
                    ? $"it is {accessibility}, so the generated top-level proxy class cannot access it"
                    : $"it is nested in '{type.Name}', which is {accessibility}, so the generated top-level proxy class cannot access it"));
            break;
        }

        // A proxy instance cannot implement a static abstract member, and an interface with one
        // cannot be the type argument the DI registration needs.
        if (!ReportStaticAbstractMember(iface))
        {
            foreach (var type in iface.AllInterfaces)
            {
                if (ReportStaticAbstractMember(type)) break;
            }
        }

        return diagnostics.ToImmutable();

        bool ReportStaticAbstractMember(INamedTypeSymbol type)
        {
            var staticMember = type.GetMembers().FirstOrDefault(static m => m.IsStatic && (m.IsAbstract || m.IsVirtual));
            if (staticMember is null) return false;
            diagnostics.Add(Diagnostic.Create(ResilienceDiagnostics.UnsupportedInterfaceShape, location, iface.Name,
                $"'{type.Name}.{staticMember.Name}' is a static abstract or static virtual member, which a proxy instance cannot implement"));
            return true;
        }
    }

    // "value,other": the out parameters, by their emitted names.
    private static string OutParameterNames(ImmutableArray<IParameterSymbol> parameters)
    {
        var names = new System.Text.StringBuilder();
        foreach (var parameter in parameters)
        {
            if (parameter.RefKind != RefKind.Out) continue;
            if (names.Length > 0) names.Append(',');
            names.Append(EscapedName(parameter));
        }
        return names.ToString();
    }

    private static ResilienceModel DiagnosticsOnly(INamedTypeSymbol iface, string? ns, ImmutableArray<Diagnostic> diagnostics) =>
        new(
            Namespace: ns,
            InterfaceName: iface.Name,
            InterfaceFqn: iface.ToDisplayString(FqnFormat),
            IsPublic: false,
            EmitPublicEntryPoints: false,
            PoliciesClassName: ServiceName(iface.Name) + "ResiliencePolicies",
            Slots: ImmutableArray<PolicySlot>.Empty,
            ClassRetry: null,
            ClassTimeout: null,
            ClassRateLimit: null,
            ClassCircuitBreaker: null,
            Methods: ImmutableArray<MethodModel>.Empty,
            PassthroughMethods: ImmutableArray<PassthroughMethodModel>.Empty,
            PassthroughMembers: ImmutableArray<PassthroughMemberModel>.Empty,
            Diagnostics: diagnostics);

    private static bool SignaturesMatch(IMethodSymbol method, IMethodSymbol fallback)
    {
        if (!string.Equals(
            method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            fallback.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            StringComparison.Ordinal)) return false;

        if (method.Parameters.Length != fallback.Parameters.Length) return false;

        for (int i = 0; i < method.Parameters.Length; i++)
        {
            if (!string.Equals(
                method.Parameters[i].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                fallback.Parameters[i].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                StringComparison.Ordinal)) return false;
        }
        return true;
    }
}
