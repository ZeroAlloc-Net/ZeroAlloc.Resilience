namespace ZeroAlloc.Resilience.Generator;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

[Generator]
public sealed class ResilienceGenerator : IIncrementalGenerator
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

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
#pragma warning disable EPS06 // IncrementalValuesProvider is a struct; hidden copies are unavoidable in the incremental pipeline API
        var candidates = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) => IsCandidate(node),
            transform: static (ctx, ct) => TryParse(ctx, ct));
        var filtered = candidates.Where(static m => m is not null);
        var models   = filtered.Select(static (m, _) => m!);
#pragma warning restore EPS06

        context.RegisterSourceOutput(models, static (ctx, model) =>
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

        // Several parts of a partial interface can pass the predicate; only the first one emits,
        // otherwise every part adds a source with the same hint name.
        foreach (var reference in iface.DeclaringSyntaxReferences)
        {
            var declaration = reference.GetSyntax(ct);
            if (!IsCandidate(declaration)) continue;
            if (declaration != ctx.Node) return null;
            break;
        }

        var retryAttr          = GetAttribute(iface, RetryFqn);
        var timeoutAttr        = GetAttribute(iface, TimeoutFqn);
        var rateLimitAttr      = GetAttribute(iface, RateLimitFqn);
        var circuitBreakerAttr = GetAttribute(iface, CircuitBreakerFqn);

        var classRetry          = ParseRetry(retryAttr);
        var classTimeout        = ParseTimeout(timeoutAttr);
        var classRateLimit      = ParseRateLimit(rateLimitAttr);
        var classCircuitBreaker = ParseCircuitBreaker(circuitBreakerAttr);

        // Skip interfaces with no policy attributes at all
        if (classRetry is null && classTimeout is null && classRateLimit is null && classCircuitBreaker is null)
        {
            var anyMethodPolicy = iface.GetMembers()
                .OfType<IMethodSymbol>()
                .Any(m => GetAttribute(m, RetryFqn) is not null
                       || GetAttribute(m, TimeoutFqn) is not null
                       || GetAttribute(m, RateLimitFqn) is not null
                       || GetAttribute(m, CircuitBreakerFqn) is not null);
            if (!anyMethodPolicy) return null;
        }

        ct.ThrowIfCancellationRequested();

        var ns = iface.ContainingNamespace.IsGlobalNamespace
            ? null
            : iface.ContainingNamespace.ToDisplayString();

        var diagnosticsBuilder = ImmutableArray.CreateBuilder<Diagnostic>();
        var methodsBuilder     = ImmutableArray.CreateBuilder<MethodModel>();
        var policyMethods = new System.Collections.Generic.HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        // ZR0004: interface-level attribute values, reported once (not once per method).
        var ifaceLocation = iface.Locations.FirstOrDefault();
        ValidateAttributeValues(diagnosticsBuilder, retryAttr, RetryRules, iface.Name, ifaceLocation);
        ValidateAttributeValues(diagnosticsBuilder, timeoutAttr, TimeoutRules, iface.Name, ifaceLocation);
        ValidateAttributeValues(diagnosticsBuilder, rateLimitAttr, RateLimitRules, iface.Name, ifaceLocation);
        ValidateAttributeValues(diagnosticsBuilder, circuitBreakerAttr, CircuitBreakerRules, iface.Name, ifaceLocation);

        var slots = new PolicySlotBuilder();
        var classRetrySlot  = classRetry is null ? null : slots.AddInterfaceSlot(PolicyKind.Retry, PolicySlotBuilder.Default(classRetry));
        var classTimeoutSlot = classTimeout is null ? null : slots.AddInterfaceSlot(PolicyKind.Timeout, PolicySlotBuilder.Default(classTimeout));
        var classRateSlot   = classRateLimit is null ? null : slots.AddInterfaceSlot(PolicyKind.RateLimiter, PolicySlotBuilder.Default(classRateLimit));
        var classCbSlot     = classCircuitBreaker is null ? null : slots.AddInterfaceSlot(PolicyKind.CircuitBreaker, PolicySlotBuilder.Default(classCircuitBreaker));

        foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
        {
            if (member.MethodKind != MethodKind.Ordinary) continue;

            var declarationIndex = slots.NextDeclarationIndex(member.Name);

            // Effective config: method-level ?? class-level
            var ownRetryAttr     = GetAttribute(member, RetryFqn);
            var ownTimeoutAttr   = GetAttribute(member, TimeoutFqn);
            var ownRateLimitAttr = GetAttribute(member, RateLimitFqn);
            var ownCbAttr        = GetAttribute(member, CircuitBreakerFqn);
            var ownRetry     = ParseRetry(ownRetryAttr);
            var ownTimeout   = ParseTimeout(ownTimeoutAttr);
            var ownRateLimit = ParseRateLimit(ownRateLimitAttr);
            var ownCb        = ParseCircuitBreaker(ownCbAttr);
            var retry     = ownRetry ?? classRetry;
            var timeout   = ownTimeout ?? classTimeout;
            var rateLimit = ownRateLimit ?? classRateLimit;
            var cbConfig  = ownCb ?? classCircuitBreaker;

            // ZR0004: method-level attribute values, reported once per method (not per interface).
            ValidateAttributeValues(diagnosticsBuilder, ownRetryAttr, RetryRules, member.Name, member.Locations.FirstOrDefault());
            ValidateAttributeValues(diagnosticsBuilder, ownTimeoutAttr, TimeoutRules, member.Name, member.Locations.FirstOrDefault());
            ValidateAttributeValues(diagnosticsBuilder, ownRateLimitAttr, RateLimitRules, member.Name, member.Locations.FirstOrDefault());
            ValidateAttributeValues(diagnosticsBuilder, ownCbAttr, CircuitBreakerRules, member.Name, member.Locations.FirstOrDefault());

            if (retry is null && timeout is null && rateLimit is null && cbConfig is null)
                continue; // no policy on this method

            var hasCt = member.Parameters.Any(static p =>
                string.Equals(p.Type.ToDisplayString(), "System.Threading.CancellationToken", StringComparison.Ordinal));

            var ctParamName = member.Parameters
                .FirstOrDefault(static p =>
                    string.Equals(p.Type.ToDisplayString(), "System.Threading.CancellationToken", StringComparison.Ordinal))
                ?.Name;

            // ZR0002: timeout but no CancellationToken
            if ((timeout is not null || retry?.PerAttemptTimeoutMs > 0) && !hasCt)
            {
                diagnosticsBuilder.Add(Diagnostic.Create(
                    ResilienceDiagnostics.NoCancellationToken,
                    member.Locations.FirstOrDefault(),
                    member.Name));
            }

            // Validate fallback (read from class-level CircuitBreaker attr, or method-level)
            // The fallback name comes from the CircuitBreakerAttribute on the method or class
            string? fallbackName = null;
            var fallbackConfigured = false;
            {
                // Fallback is intentionally excluded from CircuitBreakerConfig (it is a
                // generator-time string reference, not a runtime config value), so it is read
                // directly from the attribute already fetched above.
                var cbAttr = ownCbAttr ?? circuitBreakerAttr;
                if (cbAttr is not null)
                {
                    fallbackName = GetString(cbAttr, "Fallback");
                    fallbackConfigured = fallbackName is not null;
                    if (fallbackName is not null)
                    {
                        IMethodSymbol? fallback = null;
                        foreach (var fb in iface.GetMembers(fallbackName))
                        {
                            if (fb is IMethodSymbol fbMethod) { fallback = fbMethod; break; }
                        }
                        if (fallback is null || !SignaturesMatch(member, fallback))
                        {
                            diagnosticsBuilder.Add(Diagnostic.Create(
                                ResilienceDiagnostics.FallbackNotFound,
                                member.Locations.FirstOrDefault(),
                                fallbackName, iface.Name, member.Name));
                            fallbackName = null; // suppress emission
                        }
                    }
                }
            }

            var returnTypeFqn = member.ReturnType.ToDisplayString(FqnFormat);
            var isAsync = IsAsyncType(member.ReturnType);
            var resultType = isAsync ? UnwrapAsyncType(member.ReturnType) : member.ReturnType;
            var resultKind = ClassifyResult(resultType);
            var resultTypeFqn = resultKind == ResultKind.None
                ? null
                : resultType!.ToDisplayString(FqnFormat);

            // ZR0003: a policy that rejects a call without calling the inner service has to
            // return a failure, and it cannot build one for a foreign error type. Rejections are
            // only reported for async Result<T, E>: sync methods and UnitResult<E> compiled and
            // threw ResilienceException before #151, and keep doing so.
            if (resultKind == ResultKind.ForeignError)
            {
                var reportsRejections = isAsync && string.Equals(resultType!.Name, "Result", StringComparison.Ordinal);
                ReportUnconstructibleError(diagnosticsBuilder, member, (INamedTypeSymbol)resultType!,
                    reportsRejections && rateLimit is not null,
                    reportsRejections && cbConfig is not null && !fallbackConfigured,
                    retry?.NonThrowing == true);
            }

            var paramList = string.Join(", ", member.Parameters.Select(static p =>
                $"{p.Type.ToDisplayString(FqnFormat)} {p.Name}"));
            var argList = string.Join(", ", member.Parameters.Select(static p => p.Name));
            var argListWithToken = string.Join(", ", member.Parameters.Select(static p =>
                string.Equals(p.Type.ToDisplayString(), "System.Threading.CancellationToken", StringComparison.Ordinal)
                    ? "__ct" : p.Name));

            var retrySlot = ownRetry is null ? classRetrySlot
                : slots.AddMethodSlot(member.Name, declarationIndex, PolicyKind.Retry, PolicySlotBuilder.Default(ownRetry));
            var timeoutSlot = ownTimeout is null ? classTimeoutSlot
                : slots.AddMethodSlot(member.Name, declarationIndex, PolicyKind.Timeout, PolicySlotBuilder.Default(ownTimeout));
            var rateSlot = ownRateLimit is null ? classRateSlot
                : slots.AddMethodSlot(member.Name, declarationIndex, PolicyKind.RateLimiter, PolicySlotBuilder.Default(ownRateLimit));
            var cbSlot = ownCb is null ? classCbSlot
                : slots.AddMethodSlot(member.Name, declarationIndex, PolicyKind.CircuitBreaker, PolicySlotBuilder.Default(ownCb));

            policyMethods.Add(member);

            methodsBuilder.Add(new MethodModel(
                Name: member.Name,
                ReturnTypeFqn: returnTypeFqn,
                ResultKind: resultKind,
                ResultTypeFqn: resultTypeFqn,
                IsAsync: isAsync,
                ReturnsValue: isAsync ? resultType is not null : !member.ReturnsVoid,
                HasCancellationToken: hasCt,
                CancellationTokenParamName: ctParamName,
                ParameterList: paramList,
                ArgumentList: argList,
                ArgumentListWithToken: argListWithToken,
                FallbackMethodName: fallbackName,
                Retry: retry,
                Timeout: timeout,
                RateLimit: rateLimit,
                CircuitBreaker: cbConfig,
                RetrySlot: retrySlot,
                TimeoutSlot: timeoutSlot,
                RateLimiterSlot: rateSlot,
                CircuitBreakerSlot: cbSlot));
        }

        if (methodsBuilder.Count == 0 && diagnosticsBuilder.Count == 0)
            return null;

        // Collect passthrough methods (interface methods that have no policy applied). A method is
        // excluded only when that exact symbol got a policy above; an unattributed overload of a
        // method whose other overload has a policy still needs its own forwarding method.
        var passthroughBuilder = ImmutableArray.CreateBuilder<PassthroughMethodModel>();

        foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
        {
            if (member.MethodKind != MethodKind.Ordinary) continue;
            if (policyMethods.Contains(member)) continue; // already in policy methods

            var ptReturnTypeFqn = member.ReturnType.ToDisplayString(FqnFormat);
            var ptIsAsync = IsAsyncType(member.ReturnType);
            var ptParamList = string.Join(", ", member.Parameters.Select(static p =>
                $"{p.Type.ToDisplayString(FqnFormat)} {p.Name}"));
            var ptArgList = string.Join(", ", member.Parameters.Select(static p => p.Name));

            passthroughBuilder.Add(new PassthroughMethodModel(
                Name: member.Name,
                ReturnTypeFqn: ptReturnTypeFqn,
                IsAsync: ptIsAsync,
                ParameterList: ptParamList,
                ArgumentList: ptArgList));
        }

        // Properties, indexers and events never carry a resilience policy — only methods do — so
        // every one the interface itself declares is always a plain forwarding member. Only the
        // interface's own members (iface.GetMembers()): forwarding a member inherited from a base
        // interface is #169, not this patch. A member is included only when it is public,
        // non-static and abstract — a non-public or default-implemented member stays unimplemented
        // by the proxy, exactly as a default-implemented method already did on 2.0.0.
        var passthroughMembersBuilder = ImmutableArray.CreateBuilder<PassthroughMemberModel>();

        foreach (var property in iface.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.IsStatic || !property.IsAbstract) continue;
            if (property.DeclaredAccessibility != Accessibility.Public) continue;

            var hasGet = property.GetMethod is not null;
            var hasInit = property.SetMethod is { IsInitOnly: true };
            var hasSet = property.SetMethod is { IsInitOnly: false };
            var typeFqn = property.Type.ToDisplayString(FqnFormat);

            if (property.IsIndexer)
            {
                var idxParamList = string.Join(", ", property.Parameters.Select(static p =>
                    $"{p.Type.ToDisplayString(FqnFormat)} {p.Name}"));
                var idxArgList = string.Join(", ", property.Parameters.Select(static p => p.Name));

                passthroughMembersBuilder.Add(new PassthroughMemberModel(
                    Kind: PassthroughMemberKind.Indexer,
                    Name: "this",
                    TypeFqn: typeFqn,
                    HasGet: hasGet,
                    HasSet: hasSet,
                    HasInit: hasInit,
                    ParameterList: idxParamList,
                    ArgumentList: idxArgList));
            }
            else
            {
                passthroughMembersBuilder.Add(new PassthroughMemberModel(
                    Kind: PassthroughMemberKind.Property,
                    Name: property.Name,
                    TypeFqn: typeFqn,
                    HasGet: hasGet,
                    HasSet: hasSet,
                    HasInit: hasInit));
            }
        }

        foreach (var evt in iface.GetMembers().OfType<IEventSymbol>())
        {
            if (evt.IsStatic || !evt.IsAbstract) continue;
            if (evt.DeclaredAccessibility != Accessibility.Public) continue;

            passthroughMembersBuilder.Add(new PassthroughMemberModel(
                Kind: PassthroughMemberKind.Event,
                Name: evt.Name,
                TypeFqn: evt.Type.ToDisplayString(FqnFormat),
                HasGet: false,
                HasSet: false,
                HasInit: false));
        }

        var interfaceFqn = iface.ToDisplayString(FqnFormat);

        return new ResilienceModel(
            Namespace: ns,
            InterfaceName: iface.Name,
            InterfaceFqn: interfaceFqn,
            IsPublic: IsEffectivelyPublic(iface),
            PoliciesClassName: ServiceName(iface.Name) + "ResiliencePolicies",
            Slots: slots.ToImmutable(),
            ClassRetry: classRetry,
            ClassTimeout: classTimeout,
            ClassRateLimit: classRateLimit,
            ClassCircuitBreaker: classCircuitBreaker,
            Methods: methodsBuilder.ToImmutable(),
            PassthroughMethods: passthroughBuilder.ToImmutable(),
            PassthroughMembers: passthroughMembersBuilder.ToImmutable(),
            Diagnostics: diagnosticsBuilder.ToImmutable());
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
            NonThrowing: GetBool(attr, "NonThrowing", false));
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
        new AttributeRule("PerAttemptTimeoutMs", 0, Exclusive: false, "at least 0"));

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

        var location = attr.ApplicationSyntaxReference is { } syntaxRef
            ? Location.Create(syntaxRef.SyntaxTree, syntaxRef.Span)
            : fallbackLocation;
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
        INamedTypeSymbol resultType,
        bool rateLimitRejects,
        bool openCircuitRejects,
        bool nonThrowing)
    {
        var location = method.Locations.FirstOrDefault();
        var resultDisplay = resultType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var errorDisplay = resultType.TypeArguments[resultType.TypeArguments.Length - 1]
            .ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        if (rateLimitRejects)
            diagnostics.Add(Diagnostic.Create(ResilienceDiagnostics.UnconstructibleResultError, location,
                method.Name, resultDisplay, "[RateLimit]", errorDisplay,
                "Remove [RateLimit] from this method, or return Result<T, ResilienceError>"));
        if (openCircuitRejects)
            diagnostics.Add(Diagnostic.Create(ResilienceDiagnostics.UnconstructibleResultError, location,
                method.Name, resultDisplay, "[CircuitBreaker] without a Fallback", errorDisplay,
                "Set Fallback to a method with the same signature, or return Result<T, ResilienceError>"));
        if (nonThrowing)
            diagnostics.Add(Diagnostic.Create(ResilienceDiagnostics.UnconstructibleResultError, location,
                method.Name, resultDisplay, "[Retry] with NonThrowing = true", errorDisplay,
                "Remove NonThrowing, since retry already passes the inner Result through, or return Result<T, ResilienceError>"));
    }

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
