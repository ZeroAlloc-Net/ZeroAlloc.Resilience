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

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var models = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) =>
                node is InterfaceDeclarationSyntax { AttributeLists.Count: > 0 },
            transform: static (ctx, ct) => TryParse(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

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

    private static ResilienceModel? TryParse(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is not INamedTypeSymbol iface)
            return null;

        var classRetry          = ParseRetry(GetAttribute(iface, RetryFqn));
        var classTimeout        = ParseTimeout(GetAttribute(iface, TimeoutFqn));
        var classRateLimit      = ParseRateLimit(GetAttribute(iface, RateLimitFqn));
        var classCircuitBreaker = ParseCircuitBreaker(GetAttribute(iface, CircuitBreakerFqn));

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

        foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
        {
            if (member.MethodKind != MethodKind.Ordinary) continue;

            // Effective config: method-level ?? class-level
            var retry     = ParseRetry(GetAttribute(member, RetryFqn)) ?? classRetry;
            var timeout   = ParseTimeout(GetAttribute(member, TimeoutFqn)) ?? classTimeout;
            var rateLimit = ParseRateLimit(GetAttribute(member, RateLimitFqn)) ?? classRateLimit;
            var cbConfig  = ParseCircuitBreaker(GetAttribute(member, CircuitBreakerFqn)) ?? classCircuitBreaker;

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
            {
                // Re-query raw AttributeData: Fallback is intentionally excluded from CircuitBreakerConfig
                // (it is a generator-time string reference, not a runtime config value).
                // We cannot read it from cbConfig, so we go back to the attribute directly.
                var cbAttr = GetAttribute(member, CircuitBreakerFqn) ?? GetAttribute(iface, CircuitBreakerFqn);
                if (cbAttr is not null)
                {
                    fallbackName = GetString(cbAttr, "Fallback");
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

            var returnTypeFqn = member.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var (innerType, returnsResult) = UnwrapReturnType(member.ReturnType);
            var isAsync = IsAsyncType(member.ReturnType);

            var paramList = string.Join(", ", member.Parameters.Select(static p =>
                $"{p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {p.Name}"));
            var argList = string.Join(", ", member.Parameters.Select(static p => p.Name));
            var argListWithToken = string.Join(", ", member.Parameters.Select(static p =>
                string.Equals(p.Type.ToDisplayString(), "System.Threading.CancellationToken", StringComparison.Ordinal)
                    ? "__ct" : p.Name));

            methodsBuilder.Add(new MethodModel(
                Name: member.Name,
                ReturnTypeFqn: returnTypeFqn,
                InnerReturnType: innerType,
                ReturnsResult: returnsResult,
                IsAsync: isAsync,
                HasCancellationToken: hasCt,
                CancellationTokenParamName: ctParamName,
                ParameterList: paramList,
                ArgumentList: argList,
                ArgumentListWithToken: argListWithToken,
                FallbackMethodName: fallbackName,
                Retry: retry,
                Timeout: timeout,
                RateLimit: rateLimit,
                CircuitBreaker: cbConfig));
        }

        if (methodsBuilder.Count == 0 && diagnosticsBuilder.Count == 0)
            return null;

        // Collect passthrough methods (interface methods that have no policy applied)
        var passthroughBuilder = ImmutableArray.CreateBuilder<PassthroughMethodModel>();
        var policyMethodNames = new System.Collections.Generic.HashSet<string>(
            methodsBuilder.Select(static m => m.Name), StringComparer.Ordinal);

        foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
        {
            if (member.MethodKind != MethodKind.Ordinary) continue;
            if (policyMethodNames.Contains(member.Name)) continue; // already in policy methods

            var ptReturnTypeFqn = member.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var ptIsAsync = IsAsyncType(member.ReturnType);
            var ptParamList = string.Join(", ", member.Parameters.Select(static p =>
                $"{p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {p.Name}"));
            var ptArgList = string.Join(", ", member.Parameters.Select(static p => p.Name));

            passthroughBuilder.Add(new PassthroughMethodModel(
                Name: member.Name,
                ReturnTypeFqn: ptReturnTypeFqn,
                IsAsync: ptIsAsync,
                ParameterList: ptParamList,
                ArgumentList: ptArgList));
        }

        var interfaceFqn = iface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        return new ResilienceModel(
            Namespace: ns,
            InterfaceName: iface.Name,
            InterfaceFqn: interfaceFqn,
            ClassRetry: classRetry,
            ClassTimeout: classTimeout,
            ClassRateLimit: classRateLimit,
            ClassCircuitBreaker: classCircuitBreaker,
            Methods: methodsBuilder.ToImmutable(),
            PassthroughMethods: passthroughBuilder.ToImmutable(),
            Diagnostics: diagnosticsBuilder.ToImmutable());
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
            PerAttemptTimeoutMs: GetInt(attr, "PerAttemptTimeoutMs", 0));
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

    // ── Return type helpers ────────────────────────────────────────────────────

    private static bool IsAsyncType(ITypeSymbol type)
    {
        var name = type.OriginalDefinition.ToDisplayString();
        return name is "System.Threading.Tasks.ValueTask"
                   or "System.Threading.Tasks.ValueTask<TResult>"
                   or "System.Threading.Tasks.Task"
                   or "System.Threading.Tasks.Task<TResult>";
    }

    private static (string innerType, bool isResult) UnwrapReturnType(ITypeSymbol returnType)
    {
        if (returnType is INamedTypeSymbol named && named.TypeArguments.Length == 1)
        {
            var inner = named.TypeArguments[0];
            var isResult = inner.OriginalDefinition.ToDisplayString()
                .StartsWith("ZeroAlloc.Results.Result", StringComparison.Ordinal);
            return (inner.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), isResult);
        }
        return ("void", false);
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
