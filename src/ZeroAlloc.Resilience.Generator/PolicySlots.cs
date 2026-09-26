namespace ZeroAlloc.Resilience.Generator;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;

internal enum PolicyKind { Retry, Timeout, RateLimiter, CircuitBreaker }

// One property of the generated {Name}ResiliencePolicies class, and the proxy field it is copied to.
internal sealed record PolicySlot(
    PolicyKind Kind,
    string PropertyName,      // "Retry", "ModelsAsyncRetry"
    string FieldName,         // "_retry", "_modelsAsyncRetry"
    string TypeFqn,           // "global::ZeroAlloc.Resilience.RetryPolicy"
    string DefaultExpression  // "new global::ZeroAlloc.Resilience.RetryPolicy(3, 200, false, 0)"
);

// Names the slots: interface-level ones after their kind, method-level ones {Method}{Kind}, with
// a number after the method name for later overloads or when a name is already taken. Names are
// compared ignoring case, so the derived field names can never collide either.
internal sealed class PolicySlotBuilder
{
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _declarations = new(StringComparer.Ordinal);
    private readonly ImmutableArray<PolicySlot>.Builder _slots = ImmutableArray.CreateBuilder<PolicySlot>();

    public ImmutableArray<PolicySlot> ToImmutable() => _slots.ToImmutable();

    public PolicySlot AddInterfaceSlot(PolicyKind kind, string defaultExpression)
    {
        var name = kind.ToString();
        _used.Add(name);
        return Add(kind, name, defaultExpression);
    }

    // Call once per ordinary method, in declaration order, whether or not it has a policy:
    // 1 for the first declaration of a name, 2 for the next overload, and so on.
    public int NextDeclarationIndex(string methodName)
    {
        _declarations.TryGetValue(methodName, out var seen);
        _declarations[methodName] = seen + 1;
        return seen + 1;
    }

    public PolicySlot AddMethodSlot(string methodName, int declarationIndex, PolicyKind kind, string defaultExpression)
    {
        for (var n = declarationIndex; ; n++)
        {
            var prefix = n == 1 ? methodName : methodName + n.ToString(CultureInfo.InvariantCulture);
            var name = $"{prefix}{kind}";
            if (_used.Add(name)) return Add(kind, name, defaultExpression);
        }
    }

    private PolicySlot Add(PolicyKind kind, string propertyName, string defaultExpression)
    {
        var fieldName = $"_{char.ToLowerInvariant(propertyName[0])}{propertyName.Substring(1)}";
        var slot = new PolicySlot(kind, propertyName, fieldName, TypeFqn(kind), defaultExpression);
        _slots.Add(slot);
        return slot;
    }

    private static string TypeFqn(PolicyKind kind) => kind switch
    {
        PolicyKind.Retry          => "global::ZeroAlloc.Resilience.RetryPolicy",
        PolicyKind.Timeout        => "global::ZeroAlloc.Resilience.TimeoutPolicy",
        PolicyKind.RateLimiter    => "global::ZeroAlloc.Resilience.RateLimiter",
        _                         => "global::ZeroAlloc.Resilience.CircuitBreakerPolicy",
    };

    // The four-argument constructor unless the attribute sets MaxDelayMs, so every interface that
    // does not use it generates exactly what it did in 3.1.
    public static string Default(RetryConfig c) =>
        $"new global::ZeroAlloc.Resilience.RetryPolicy({c.MaxAttempts.ToString(CultureInfo.InvariantCulture)}, {c.BackoffMs.ToString(CultureInfo.InvariantCulture)}, {(c.Jitter ? "true" : "false")}, {c.PerAttemptTimeoutMs.ToString(CultureInfo.InvariantCulture)}"
        + (c.MaxDelayMs is { } maxDelayMs ? $", {maxDelayMs.ToString(CultureInfo.InvariantCulture)})" : ")");

    public static string Default(TimeoutConfig c) =>
        $"new global::ZeroAlloc.Resilience.TimeoutPolicy({c.TotalMs.ToString(CultureInfo.InvariantCulture)})";

    public static string Default(RateLimitConfig c) =>
        $"new global::ZeroAlloc.Resilience.RateLimiter({c.MaxPerSecond.ToString(CultureInfo.InvariantCulture)}, {c.BurstSize.ToString(CultureInfo.InvariantCulture)}, global::ZeroAlloc.Resilience.RateLimitScope.{c.Scope})";

    public static string Default(CircuitBreakerConfig c) =>
        $"new global::ZeroAlloc.Resilience.CircuitBreakerPolicy({c.MaxFailures.ToString(CultureInfo.InvariantCulture)}, {c.ResetMs.ToString(CultureInfo.InvariantCulture)}, {c.HalfOpenProbes.ToString(CultureInfo.InvariantCulture)})";
}
