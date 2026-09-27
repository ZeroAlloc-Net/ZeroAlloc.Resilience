namespace ZeroAlloc.Resilience.Generator;

using Microsoft.CodeAnalysis;
using System;
using System.Collections.Immutable;
using System.Linq;

// The state TryParse builds one interface's model from. Symbols never leave it: the model the
// incremental pipeline caches holds only strings and value records.
public sealed partial class ResilienceGenerator
{
    // Interface-wide: its policy attributes, the builders every member adds to, and the slots.
    private sealed class InterfaceParse
    {
        public InterfaceParse(INamedTypeSymbol iface, Compilation compilation)
        {
            Iface = iface;
            Compilation = compilation;
            IfaceLocation = iface.Locations.FirstOrDefault();
            RetryLookup = new RetryMemberLookup(iface, compilation);

            RetryAttr          = GetAttribute(iface, RetryFqn);
            TimeoutAttr        = GetAttribute(iface, TimeoutFqn);
            RateLimitAttr      = GetAttribute(iface, RateLimitFqn);
            CircuitBreakerAttr = GetAttribute(iface, CircuitBreakerFqn);

            ClassRetry          = ParseRetry(RetryAttr);
            ClassTimeout        = ParseTimeout(TimeoutAttr);
            ClassRateLimit      = ParseRateLimit(RateLimitAttr);
            ClassCircuitBreaker = ParseCircuitBreaker(CircuitBreakerAttr);

            InterfacePolicies = ImmutableArray.Create(RetryAttr, TimeoutAttr, RateLimitAttr, CircuitBreakerAttr);
        }

        public INamedTypeSymbol Iface { get; }
        public Compilation Compilation { get; }
        public Location? IfaceLocation { get; }
        public RetryMemberLookup RetryLookup { get; }

        public AttributeData? RetryAttr { get; }
        public AttributeData? TimeoutAttr { get; }
        public AttributeData? RateLimitAttr { get; }
        public AttributeData? CircuitBreakerAttr { get; }
        public ImmutableArray<AttributeData?> InterfacePolicies { get; }

        public RetryConfig? ClassRetry { get; }
        public TimeoutConfig? ClassTimeout { get; }
        public RateLimitConfig? ClassRateLimit { get; }
        public CircuitBreakerConfig? ClassCircuitBreaker { get; }

        public bool HasInterfacePolicy =>
            ClassRetry is not null || ClassTimeout is not null || ClassRateLimit is not null || ClassCircuitBreaker is not null;

        public PolicySlotBuilder Slots { get; } = new();
        public PolicySlot? ClassRetrySlot { get; private set; }
        public PolicySlot? ClassTimeoutSlot { get; private set; }
        public PolicySlot? ClassRateSlot { get; private set; }
        public PolicySlot? ClassCbSlot { get; private set; }

        public ImmutableArray<Diagnostic>.Builder Diagnostics { get; } = ImmutableArray.CreateBuilder<Diagnostic>();
        public ImmutableArray<MethodModel>.Builder Methods { get; } = ImmutableArray.CreateBuilder<MethodModel>();
        public ImmutableArray<PassthroughMethodModel>.Builder Passthroughs { get; } = ImmutableArray.CreateBuilder<PassthroughMethodModel>();

        // ZR0004: interface-level attribute values, reported once (not once per method).
        // ZR0009: the interface-level [Retry] names, reported once at the attribute.
        public void ValidateInterfaceAttributes()
        {
            ValidateAttributeValues(Diagnostics, RetryAttr, RetryRules, Iface.Name, IfaceLocation);
            ValidateAttributeValues(Diagnostics, TimeoutAttr, TimeoutRules, Iface.Name, IfaceLocation);
            ValidateAttributeValues(Diagnostics, RateLimitAttr, RateLimitRules, Iface.Name, IfaceLocation);
            ValidateAttributeValues(Diagnostics, CircuitBreakerAttr, CircuitBreakerRules, Iface.Name, IfaceLocation);
            ValidateRetryMemberNames(Diagnostics, RetryAttr, RetryLookup, errorTypeDisplay: null, IfaceLocation);
            if (ClassRetry is { RethrowDeclined: true, RetryOnException: null })
                ReportRethrowDeclinedHasNoEffect(Diagnostics, RetryAttr!, IfaceLocation, Iface.Name, NoRetryOnException);
        }

        // The methods the interface-level [Retry] applies to, by whether they throw. ZR0013 for
        // a RethrowDeclined that none of them can use; see ReportUnusedInterfaceRethrowDeclined.
        public int ClassRetryThrowingMethods { get; set; }
        public int ClassRetryFailureMethods { get; set; }

        // ZR0013 for an interface-level [Retry(RethrowDeclined = true)] whose methods all return
        // failures instead of throwing. A mix is not reported: the property works on the throwing
        // methods, and the Result methods need no change.
        public void ReportUnusedInterfaceRethrowDeclined()
        {
            if (ClassRetry is not { RethrowDeclined: true, RetryOnException: not null }
                || ClassRetryThrowingMethods > 0 || ClassRetryFailureMethods == 0)
                return;
            ReportRethrowDeclinedHasNoEffect(Diagnostics, RetryAttr!, IfaceLocation, Iface.Name,
                ("every method it applies to returns a failure instead of throwing, for a declined exception too",
                 "Remove RethrowDeclined"));
        }

        // The interface-level slots come first, before any method's own.
        public void AddInterfaceSlots()
        {
            ClassRetrySlot   = ClassRetry is null ? null : Slots.AddInterfaceSlot(PolicyKind.Retry, PolicySlotBuilder.Default(ClassRetry));
            ClassTimeoutSlot = ClassTimeout is null ? null : Slots.AddInterfaceSlot(PolicyKind.Timeout, PolicySlotBuilder.Default(ClassTimeout));
            ClassRateSlot    = ClassRateLimit is null ? null : Slots.AddInterfaceSlot(PolicyKind.RateLimiter, PolicySlotBuilder.Default(ClassRateLimit));
            ClassCbSlot      = ClassCircuitBreaker is null ? null : Slots.AddInterfaceSlot(PolicyKind.CircuitBreaker, PolicySlotBuilder.Default(ClassCircuitBreaker));
        }
    }

    // One forwarded method: its own policy attributes, the effective policies, which the
    // parse may leave off, its rendered signature, and what its return type allows.
    private sealed class MethodParse
    {
        public MethodParse(InterfaceParse owner, ForwardedMember entry, IMethodSymbol member, int declarationIndex)
        {
            Entry = entry;
            Member = member;
            DeclarationIndex = declarationIndex;

            OwnRetryAttr     = GetAttribute(member, RetryFqn);
            OwnTimeoutAttr   = GetAttribute(member, TimeoutFqn);
            OwnRateLimitAttr = GetAttribute(member, RateLimitFqn);
            OwnCbAttr        = GetAttribute(member, CircuitBreakerFqn);
            OwnRetry     = ParseRetry(OwnRetryAttr);
            OwnTimeout   = ParseTimeout(OwnTimeoutAttr);
            OwnRateLimit = ParseRateLimit(OwnRateLimitAttr);
            OwnCb        = ParseCircuitBreaker(OwnCbAttr);

            // Effective config: method-level ?? class-level. For an inherited method the class
            // level is the interface being proxied, not the base interface that declares it.
            Retry          = OwnRetry ?? owner.ClassRetry;
            Timeout        = OwnTimeout ?? owner.ClassTimeout;
            RateLimit      = OwnRateLimit ?? owner.ClassRateLimit;
            CircuitBreaker = OwnCb ?? owner.ClassCircuitBreaker;

            // Diagnostics about the interface's own methods point at the method; those about an
            // inherited method point at the interface being proxied, since the base interface may
            // be in another assembly and, when it is in this one, reports its own findings.
            Location = entry.IsOwn ? member.Locations.FirstOrDefault() : owner.IfaceLocation;

            ReturnsByRef = member.ReturnsByRef || member.ReturnsByRefReadonly;
            IsAsync = IsAsyncType(member.ReturnType);

            // An async method cannot have ref, out, in or ref struct parameters, such as a
            // ReadOnlySpan<char>, so a method that has them is never wrapped in an async body:
            // forwarded, it returns the inner task directly.
            HasByRefOrRefLikeParameter = member.Parameters.Any(static p => p.RefKind != RefKind.None || p.Type.IsRefLikeType);

            // Escaped, like every emitted parameter reference: the generated code names it in
            // CreateLinkedTokenSource, the caller-cancellation filter and the backoff wait.
            CancellationTokenParamName = member.Parameters
                .FirstOrDefault(static p =>
                    string.Equals(p.Type.ToDisplayString(), "System.Threading.CancellationToken", StringComparison.Ordinal))
                is { } ctParam ? EscapedName(ctParam) : null;

            Passthrough = CreatePassthrough(entry, member, IsAsync && !HasByRefOrRefLikeParameter, ReturnsByRef);
        }

        public ForwardedMember Entry { get; }
        public IMethodSymbol Member { get; }
        public int DeclarationIndex { get; }
        public Location? Location { get; }

        public AttributeData? OwnRetryAttr { get; }
        public AttributeData? OwnTimeoutAttr { get; }
        public AttributeData? OwnRateLimitAttr { get; }
        public AttributeData? OwnCbAttr { get; }
        public RetryConfig? OwnRetry { get; }
        public TimeoutConfig? OwnTimeout { get; }
        public RateLimitConfig? OwnRateLimit { get; }
        public CircuitBreakerConfig? OwnCb { get; }

        public RetryConfig? Retry { get; set; }
        public TimeoutConfig? Timeout { get; set; }
        public RateLimitConfig? RateLimit { get; set; }
        public CircuitBreakerConfig? CircuitBreaker { get; set; }

        public bool HasPolicy => Retry is not null || Timeout is not null || RateLimit is not null || CircuitBreaker is not null;

        public bool ReturnsByRef { get; }
        public bool IsAsync { get; }
        public bool HasByRefOrRefLikeParameter { get; }
        public string? CancellationTokenParamName { get; }

        // The method forwarded to the inner service unchanged, when no policy applies to it.
        public PassthroughMethodModel Passthrough { get; }

        public string? FallbackName { get; set; }
        public string FallbackReceiver { get; set; } = "_inner";
        public bool FallbackConfigured { get; set; }

        public ITypeSymbol? ResultType { get; set; }
        public ResultKind ResultKind { get; set; }
        public ITypeSymbol? ErrorType { get; set; }

        public bool RateLimitRejects { get; set; }
        public bool OpenCircuitRejects { get; set; }
        public bool NonThrowingUnbuildable { get; set; }
        public string? Unwrappable { get; set; }

        private static PassthroughMethodModel CreatePassthrough(ForwardedMember entry, IMethodSymbol member, bool isAsync, bool returnsByRef) =>
            new(
                Name: member.Name,
                ReturnTypeFqn: ReturnRefPrefix(member.ReturnsByRef, member.ReturnsByRefReadonly) + member.ReturnType.ToDisplayString(FqnFormat),
                IsAsync: isAsync,
                ParameterList: ParameterDeclarations(member.Parameters),
                ArgumentList: Arguments(member.Parameters, replaceCancellationToken: false),
                InnerReceiver: entry.Receiver,
                TypeParameterList: TypeParameterList(member),
                ConstraintClauses: ConstraintClauses(member),
                ExplicitImplementations: entry.ExplicitImplementations,
                ExplicitInterface: entry.ExplicitInterface,
                ExplicitConstraintClauses: ExplicitConstraintClauses(member),
                ReturnsByRef: returnsByRef,
                // A public member with an object member's name and parameters but another
                // signature, such as `object ToString()`, hides it: emitted with `new`, as
                // CS0114 and CS0108 ask.
                HidesObjectMember: entry.ExplicitInterface.Length == 0 && HidesObjectMember(member));
    }
}
