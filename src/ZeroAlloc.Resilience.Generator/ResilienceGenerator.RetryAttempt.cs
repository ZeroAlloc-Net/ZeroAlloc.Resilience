namespace ZeroAlloc.Resilience.Generator;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Globalization;
using System.Linq;
using System.Threading;

// [RetryAttempt] (#198): an int or int? parameter of a method under [Retry] that receives the
// retry number of the current attempt instead of the caller's argument.
public sealed partial class ResilienceGenerator
{
    private const string RetryAttemptFqn = "ZeroAlloc.Resilience.RetryAttemptAttribute";

    // The diagnostics for one [RetryAttempt] parameter. A record of Diagnostics, which compare by
    // value, so an unchanged result does not count as a change for the incremental pipeline.
    private sealed record RetryAttemptFindings(Diagnostic? Unsupported, Diagnostic? WithoutRetry);

    // ZR0011 and ZR0012 run on every [RetryAttempt] parameter, not only on the interfaces the
    // proxy pipeline parses: an interface with no policy at all, where the attribute is most
    // likely a mistake, never reaches that pipeline.
    private static void RegisterRetryAttemptDiagnostics(IncrementalGeneratorInitializationContext context)
    {
        var findings = context.SyntaxProvider.ForAttributeWithMetadataName(
            RetryAttemptFqn,
            predicate: static (node, _) => node is ParameterSyntax,
            transform: static (ctx, ct) => RetryAttemptDiagnostics(ctx, ct));

        context.RegisterSourceOutput(findings, static (ctx, found) =>
        {
            if (found.Unsupported is not null)
                ctx.ReportDiagnostic(found.Unsupported);
            if (found.WithoutRetry is not null)
                ctx.ReportDiagnostic(found.WithoutRetry);
        });
    }

    private static RetryAttemptFindings RetryAttemptDiagnostics(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ctx.TargetSymbol is not IParameterSymbol parameter)
            return new RetryAttemptFindings(null, null);

        // The whole parameter, attributes included, so a #pragma around it covers the diagnostic.
        var location = ctx.TargetNode.GetLocation();
        var name = parameter.Name;
        var method = parameter.ContainingSymbol as IMethodSymbol;
        var methodName = method?.Name ?? parameter.ContainingSymbol.Name;

        var unsupported = IsRetryAttemptType(parameter)
            ? null
            : Diagnostic.Create(ResilienceDiagnostics.RetryAttemptUnsupportedParameter, location,
                name, methodName, RefKindPrefix(parameter.RefKind) + parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));

        Diagnostic? withoutRetry = null;
        if (method is not { MethodKind: MethodKind.Ordinary, ContainingType.TypeKind: TypeKind.Interface })
        {
            withoutRetry = Diagnostic.Create(ResilienceDiagnostics.RetryAttemptWithoutRetry, location,
                name, methodName,
                $"'{methodName}' is not a method of an interface, and only the resilience proxy of an interface passes the retry attempt",
                "Put [RetryAttempt] on the interface method, or remove it");
        }
        else if (IsResilienceTarget(method) && GetAttribute(method, RetryFqn) is null
                 && GetAttribute(method.ContainingType, RetryFqn) is null)
        {
            withoutRetry = Diagnostic.Create(ResilienceDiagnostics.RetryAttemptWithoutRetry, location,
                name, methodName,
                $"neither '{methodName}' nor '{method.ContainingType.Name}' has [Retry]",
                "Add [Retry] to the method or the interface, or remove [RetryAttempt]");
        }

        return new RetryAttemptFindings(unsupported, withoutRetry);
    }

    // Only an interface the generator builds a proxy for is checked: one with a policy attribute
    // on the interface or on a method it declares, the same test the proxy pipeline uses. Its
    // proxy runs a method without [Retry] once, so [RetryAttempt] there certainly has no effect.
    // A plain interface is typically a base meant to be inherited: an interface with [Retry] that
    // inherits it, in this project or another one, retries the method in its own proxy, where
    // [RetryAttempt] takes effect. Nothing here can see every such interface, so a plain
    // interface is never reported.
    private static bool IsResilienceTarget(IMethodSymbol method) =>
        HasPolicyAttribute(method.ContainingType) || HasOwnMethodPolicy(method.ContainingType);

    private static bool HasPolicyAttribute(ISymbol symbol) =>
        GetAttribute(symbol, RetryFqn) is not null
        || GetAttribute(symbol, TimeoutFqn) is not null
        || GetAttribute(symbol, RateLimitFqn) is not null
        || GetAttribute(symbol, CircuitBreakerFqn) is not null;

    // An int or int? passed by value: the only parameters the retry number can be passed to.
    // Anything else is ZR0012, and the proxy passes the caller's argument to it unchanged.
    private static bool IsRetryAttemptType(IParameterSymbol parameter) =>
        parameter.RefKind == RefKind.None
        && (parameter.Type.SpecialType == SpecialType.System_Int32
            || parameter.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
               && nullable.TypeArguments[0].SpecialType == SpecialType.System_Int32);

    private static bool IsRetryAttemptParameter(IParameterSymbol parameter) =>
        IsRetryAttemptType(parameter) && GetAttribute(parameter, RetryAttemptFqn) is not null;

    // "1,3": the ordinals of the [RetryAttempt] parameters, for the identity of a declaration.
    private static string RetryAttemptOrdinals(IMethodSymbol method) =>
        string.Join(",", method.Parameters.Where(IsRetryAttemptParameter).Select(static p => p.Ordinal.ToString(CultureInfo.InvariantCulture)));

    // The arguments of the inner call inside the retry loop, with the retry number for every
    // [RetryAttempt] parameter; null when the method has none. int? gets null on the first attempt
    // and then 1, 2 and so on; int gets 0, 1, 2 and so on. __attempt is the loop's own local, so
    // the number belongs to this call alone.
    private static string? RetryArgumentList(IMethodSymbol method, bool replaceCancellationToken) =>
        method.Parameters.Any(IsRetryAttemptParameter)
            ? Arguments(method.Parameters, replaceCancellationToken, p => IsRetryAttemptParameter(p)
                ? p.Type.SpecialType == SpecialType.System_Int32 ? "__attempt" : "__attempt == 0 ? default(int?) : __attempt"
                : null)
            : null;

    // The arguments of the fallback call, with the first attempt's value for every [RetryAttempt]
    // parameter; null when the method has none. The fallback runs instead of any attempt, and the
    // caller's argument is never passed on.
    private static string? FallbackArgumentList(IMethodSymbol method) =>
        method.Parameters.Any(IsRetryAttemptParameter)
            ? Arguments(method.Parameters, replaceCancellationToken: false, p => IsRetryAttemptParameter(p)
                ? p.Type.SpecialType == SpecialType.System_Int32 ? "0" : "default(int?)"
                : null)
            : null;
}
