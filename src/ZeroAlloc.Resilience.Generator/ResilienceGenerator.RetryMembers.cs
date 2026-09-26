namespace ZeroAlloc.Resilience.Generator;

using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
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

    // Where retry members are looked up, and the types their signatures are checked against,
    // resolved once per interface. Types are compared as symbols, so a nullable annotation such
    // as Exception? matches Exception.
    private sealed class RetryMemberLookup
    {
        private readonly INamedTypeSymbol _iface;
        private readonly Compilation _compilation;
        private readonly INamedTypeSymbol? _exception;
        private readonly INamedTypeSymbol? _timeSpan;

        public RetryMemberLookup(INamedTypeSymbol iface, Compilation compilation)
        {
            _iface = iface;
            _compilation = compilation;
            _exception = compilation.GetTypeByMetadataName("System.Exception");
            _timeSpan = compilation.GetTypeByMetadataName("System.TimeSpan");
        }

        public string InterfaceName => _iface.Name;

        public bool IsException(ITypeSymbol type) => SymbolEqualityComparer.Default.Equals(type, _exception);

        // A class derived from Exception, other than Exception itself.
        public bool IsExceptionSubclass(ITypeSymbol type)
        {
            for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
            {
                if (IsException(baseType)) return true;
            }
            return false;
        }

        public bool IsNullableTimeSpan(ITypeSymbol type) =>
            type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, TypeArguments.Length: 1 } nullable
            && SymbolEqualityComparer.Default.Equals(nullable.TypeArguments[0], _timeSpan);

        // Looked up like FindFallback: the interface itself first, then every base interface in
        // AllInterfaces order; the first match wins.
        public IMethodSymbol? Find(string name, Func<ITypeSymbol, bool> returns, Func<ITypeSymbol, bool> takes)
        {
            foreach (var method in FindAll(name, returns, takes))
                return method;
            return null;
        }

        // Every match, in lookup order.
        private IEnumerable<IMethodSymbol> FindAll(string name, Func<ITypeSymbol, bool> returns, Func<ITypeSymbol, bool> takes)
        {
            foreach (var method in FindIn(_iface, name, returns, takes))
                yield return method;
            foreach (var baseInterface in _iface.AllInterfaces)
            {
                foreach (var method in FindIn(baseInterface, name, returns, takes))
                    yield return method;
            }
        }

        private IEnumerable<IMethodSymbol> FindIn(INamedTypeSymbol type, string name, Func<ITypeSymbol, bool> returns, Func<ITypeSymbol, bool> takes)
        {
            foreach (var candidate in type.GetMembers(name))
            {
                if (candidate is IMethodSymbol method
                    && IsRetryMemberShape(method)
                    && returns(method.ReturnType)
                    && takes(method.Parameters[0].Type))
                    yield return method;
            }
        }

        // A method a [Retry] name can refer to: an ordinary static method with a body, so neither
        // static abstract nor static virtual, which could only be called through a constrained
        // type parameter the proxy does not have; not generic, since nothing could infer its type
        // arguments; one by-value parameter; not returning by reference; and callable from the
        // generated proxy, a top-level class in this compilation. That rules out private and
        // protected members, and internal ones of another assembly without InternalsVisibleTo.
        private bool IsRetryMemberShape(IMethodSymbol method) =>
            method is { MethodKind: MethodKind.Ordinary, IsStatic: true, IsAbstract: false, IsVirtual: false, Arity: 0,
                        ReturnsByRef: false, ReturnsByRefReadonly: false, Parameters.Length: 1 }
            && method.Parameters[0].RefKind == RefKind.None
            && _compilation.IsSymbolAccessibleWithin(method, _compilation.Assembly);
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
        RetryMemberLookup lookup,
        string? errorTypeDisplay,
        Location? fallbackLocation)
    {
        if (attr is null) return;

        var location = AttributeLocation(attr, fallbackLocation);
        var errorParameter = errorTypeDisplay ?? "E";
        var errorWhere = errorTypeDisplay is null ? ", where E is the error type of the method's Result" : "";

        if (GetString(attr, "RetryWhen") is { } retryWhen
            && lookup.Find(retryWhen, IsBoolean, static _ => true) is null)
        {
            Report("RetryWhen", retryWhen, $"'static bool {retryWhen}({errorParameter} error)'{errorWhere}");
        }

        if (GetString(attr, "RetryOnException") is { } retryOnException
            && lookup.Find(retryOnException, IsBoolean, lookup.IsException) is null)
        {
            Report("RetryOnException", retryOnException, $"'static bool {retryOnException}(Exception exception)'");
        }

        if (GetString(attr, "DelayHint") is { } delayHint
            && lookup.Find(delayHint, lookup.IsNullableTimeSpan, static _ => true) is null)
        {
            Report("DelayHint", delayHint,
                $"'static TimeSpan? {delayHint}({errorParameter} error)' or 'static TimeSpan? {delayHint}(Exception exception)'{errorWhere}");
        }

        void Report(string property, string name, string signature) =>
            diagnostics.Add(Diagnostic.Create(ResilienceDiagnostics.RetryMemberNotFound, location,
                property, name, lookup.InterfaceName, signature));
    }

    // Resolves the names of the method's effective [Retry] for this method. A name ZR0009 already
    // rejected resolves to nothing and is not reported again. ZR0010 is reported once per method,
    // naming every property that cannot apply: an Error when the [Retry] is the method's own, a
    // Warning when it is the interface's, and not at all when report is false.
    private static RetryMembers ResolveRetryMembers(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        RetryMemberLookup lookup,
        IMethodSymbol method,
        RetryConfig retry,
        ITypeSymbol? errorType,
        bool methodLevel,
        bool report,
        Location? location)
    {
        var retryOnException = retry.RetryOnException is { } onExceptionName
            ? lookup.Find(onExceptionName, IsBoolean, lookup.IsException)
            : null;
        var exceptionHint = retry.DelayHint is { } exceptionHintName
            ? lookup.Find(exceptionHintName, lookup.IsNullableTimeSpan, lookup.IsException)
            : null;

        // RetryWhen: does an overload of the right shape take this method's error type?
        IMethodSymbol? retryWhen = null;
        var retryWhenHasShape = false;
        string? retryWhenLabel = null;
        if (retry.RetryWhen is { } retryWhenName
            && lookup.Find(retryWhenName, IsBoolean, static _ => true) is not null)
        {
            retryWhenHasShape = true;
            retryWhen = errorType is { } error
                ? lookup.Find(retryWhenName, IsBoolean, t => SameType(t, error))
                : null;
            if (retryWhen is null)
                retryWhenLabel = $"RetryWhen = \"{retryWhenName}\"";
        }

        // DelayHint may name an overload set: the overload for the error type serves failed
        // Results, and only with RetryWhen; the Exception overload serves thrown exceptions on
        // every method. Either may be absent, so DelayHint is reported only when no overload of
        // the set applies to this method. Skipped when RetryWhen is set but is ZR0009's.
        IMethodSymbol? resultHint = null;
        string? hintLabel = null;
        (IMethodSymbol Overload, ITypeSymbol Parameter)? hintSubclass = null;
        if (retry.DelayHint is { } hintName
            && (retry.RetryWhen is null || retryWhenHasShape))
        {
            // The overload for this method's error type, which is the Exception overload when the
            // error type is Exception itself.
            var hintForError = errorType is { } error
                ? lookup.Find(hintName, lookup.IsNullableTimeSpan, t => SameType(t, error))
                : null;
            if (retryWhen is not null && hintForError is not null)
            {
                resultHint = hintForError;
            }
            // A hint overload that does take the error type starts working once RetryWhen does,
            // so it is not reported beside a failing RetryWhen.
            else if (exceptionHint is null
                && (retryWhenLabel is null || hintForError is null)
                && lookup.Find(hintName, lookup.IsNullableTimeSpan, static _ => true) is { } unused)
            {
                hintLabel = $"DelayHint = \"{hintName}\"";
                var unusedParameter = unused.Parameters[0].Type;
                if (lookup.IsExceptionSubclass(unusedParameter)
                    && !(errorType is { } subclassError && SameType(unusedParameter, subclassError)))
                {
                    hintSubclass = (unused, unusedParameter);
                }
            }
        }

        if (report && (retryWhenLabel is not null || hintLabel is not null))
        {
            var (reason, advice) = NotApplicableReason(retry, errorType, retryWhenLabel, hintLabel, hintSubclass);
            diagnostics.Add(Diagnostic.Create(
                ResilienceDiagnostics.RetryMemberNotApplicable,
                location,
                methodLevel ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                null,
                null,
                retryWhenLabel is not null && hintLabel is not null ? $"{retryWhenLabel} and {hintLabel}" : retryWhenLabel ?? hintLabel,
                method.Name,
                reason,
                methodLevel ? advice : $"'{method.Name}' keeps exception-only retry"));
        }

        return new RetryMembers(
            retryWhen is null ? null : CallTarget(retryWhen),
            retryOnException is null ? null : CallTarget(retryOnException),
            resultHint is null ? null : CallTarget(resultHint),
            exceptionHint is null ? null : CallTarget(exceptionHint));
    }

    // One ZR0010 per method. RetryWhen and DelayHint share one reason when they fail for the
    // same cause: the method returns no Result, or no overload of either takes its error type.
    // A DelayHint overload taking an Exception subclass fails for its own reason, so beside a
    // failing RetryWhen the diagnostic states both reasons and both pieces of advice.
    private static (string Reason, string Advice) NotApplicableReason(
        RetryConfig retry,
        ITypeSymbol? errorType,
        string? retryWhenLabel,
        string? hintLabel,
        (IMethodSymbol Overload, ITypeSymbol Parameter)? hintSubclass)
    {
        var names = new List<string>(2);
        if (retryWhenLabel is not null) names.Add(retry.RetryWhen!);

        if (hintLabel is not null && hintSubclass is { } subclass)
        {
            var subclassReason = ExceptionSubclass(subclass.Overload, subclass.Parameter);
            if (names.Count == 0) return subclassReason;
            var retryWhenReason = errorType is { } type ? NoOverloadFor(names, type, "RetryWhen") : NotAResult("RetryWhen");
            return ($"{retryWhenReason.Reason}; and {subclassReason.Reason}",
                    $"{retryWhenReason.Advice}. {subclassReason.Advice}");
        }

        if (hintLabel is not null)
        {
            if (errorType is not null && retry.RetryWhen is null) return NoRetryWhen();
            names.Add(retry.DelayHint!);
        }

        var subject = names.Count == 1 ? "it" : "them";
        return errorType is { } errorTypeSymbol ? NoOverloadFor(names, errorTypeSymbol, subject) : NotAResult(subject);

        static (string Reason, string Advice) NotAResult(string subject) =>
            ("it does not return a ZeroAlloc.Results Result, so there is no failed Result to pass to it",
             $"Remove {subject} from this method's [Retry]");

        static (string Reason, string Advice) NoRetryWhen() =>
            ("[Retry] has no RetryWhen, so a failed Result is never retried and its delay hint is never read",
             "Set RetryWhen, or remove the DelayHint overload that takes the Result error type");

        static (string Reason, string Advice) NoOverloadFor(List<string> names, ITypeSymbol type, string subject)
        {
            var display = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            return names.Count == 1
                ? ($"its Result error type is '{display}', and no '{names[0]}' overload takes it",
                   $"Declare an overload of '{names[0]}' that takes '{display}', or remove {subject} from this method's [Retry]")
                : ($"its Result error type is '{display}', and no '{names[0]}' or '{names[1]}' overload takes it",
                   $"Declare overloads of '{names[0]}' and '{names[1]}' that take '{display}', or remove {subject} from this method's [Retry]");
        }

        static (string Reason, string Advice) ExceptionSubclass(IMethodSymbol overload, ITypeSymbol parameter)
        {
            var display = parameter.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            return ($"'{OverloadDisplay(overload)}' takes '{display}', and a DelayHint overload for thrown exceptions must take System.Exception",
                    $"Change the parameter of '{OverloadDisplay(overload)}' to Exception, and check for '{display}' in its body");
        }
    }

    // "RetryAfter(HttpError)".
    private static string OverloadDisplay(IMethodSymbol method) =>
        $"{method.Name}({method.Parameters[0].Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)})";

    // Called through its declaring interface, fully qualified, so a base interface's method binds
    // to exactly that declaration.
    private static string CallTarget(IMethodSymbol method) =>
        $"{method.ContainingType.ToDisplayString(FqnFormat)}.{EscapeIdentifier(method.Name)}";

    private static bool IsBoolean(ITypeSymbol type) => type.SpecialType == SpecialType.System_Boolean;

    // E matched by type equality; SymbolEqualityComparer.Default ignores nullable annotations, so
    // a predicate taking HttpError? accepts an HttpError error type.
    private static bool SameType(ITypeSymbol candidate, ITypeSymbol errorType) =>
        SymbolEqualityComparer.Default.Equals(candidate, errorType);
}
