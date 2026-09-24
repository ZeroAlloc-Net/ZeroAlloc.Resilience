using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Resilience.Generator;

internal static class ResilienceDiagnostics
{
    private const string Category = "ZeroAlloc.Resilience";

    /// <summary>ZR0001 — Fallback method not found or signature mismatch (Error).</summary>
    public static readonly DiagnosticDescriptor FallbackNotFound = new(
        id: "ZR0001",
        title: "Fallback method not found or signature mismatch",
        messageFormat: "Fallback method '{0}' on '{1}' was not found or its signature does not match method '{2}'. The fallback must have the same parameters and return type.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>ZR0002 — Timeout configured but method has no CancellationToken (Warning).</summary>
    public static readonly DiagnosticDescriptor NoCancellationToken = new(
        id: "ZR0002",
        title: "Timeout configured but method has no CancellationToken",
        messageFormat: "Method '{0}' has a timeout configured but no CancellationToken parameter — the timeout cannot be propagated. Add a CancellationToken parameter.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// ZR0003 — A policy has to return a failure with no inner Result to hand back, but the method
    /// returns Result&lt;T, E&gt; with an error type the generator cannot construct (Error).
    /// </summary>
    public static readonly DiagnosticDescriptor UnconstructibleResultError = new(
        id: "ZR0003",
        title: "Policy cannot build a failure for this Result error type",
        messageFormat: "Method '{0}' returns '{1}'. {2} must return a failure when there is no inner Result to return, but the generator cannot construct the error type '{3}'. {4}.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
