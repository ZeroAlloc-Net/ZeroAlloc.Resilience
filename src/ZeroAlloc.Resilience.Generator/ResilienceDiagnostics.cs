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
        messageFormat: "Method '{0}' returns '{1}'. {2}. {3}.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// ZR0004 — A policy attribute property is explicitly set to a value the runtime policy
    /// constructor rejects with ArgumentOutOfRangeException (Error).
    /// </summary>
    public static readonly DiagnosticDescriptor InvalidAttributeValue = new(
        id: "ZR0004",
        title: "Invalid policy attribute value",
        messageFormat: "{0} on '{1}'. {2} must be {3}, but is {4}.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// ZR0006 — A policy cannot be applied to a method, so it runs without it (Warning): an
    /// inherited default-implemented method the policy, from the proxied interface or the method's
    /// own attribute, cannot wrap; or an own method the proxy does not implement, because it has
    /// an object member's signature, is sealed, static or not public.
    /// </summary>
    public static readonly DiagnosticDescriptor PolicyNotAppliedToInheritedMethod = new(
        id: "ZR0006",
        title: "Policy not applied to method",
        messageFormat: "{0} '{1}' on '{2}' {3} without {4}, because {5}. {6}.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// ZR0007 — The interface has a shape the generator cannot build a valid proxy for (Error).
    /// </summary>
    public static readonly DiagnosticDescriptor UnsupportedInterfaceShape = new(
        id: "ZR0007",
        title: "Interface shape not supported by the resilience generator",
        messageFormat: "'{0}' is not supported by the resilience generator: {1}. No proxy is generated for it.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
