using Microsoft.CodeAnalysis;
using System.Collections.Immutable;

namespace ZeroAlloc.Resilience.Generator;

internal sealed record ResilienceModel(
    string? Namespace,
    string InterfaceName,
    string InterfaceFqn,            // e.g. global::MyApp.IExternalService
    bool IsPublic,                  // interface and every containing type are public
    string PoliciesClassName,       // e.g. JevApiResiliencePolicies
    ImmutableArray<PolicySlot> Slots,
    RetryConfig? ClassRetry,
    TimeoutConfig? ClassTimeout,
    RateLimitConfig? ClassRateLimit,
    CircuitBreakerConfig? ClassCircuitBreaker,
    ImmutableArray<MethodModel> Methods,
    ImmutableArray<PassthroughMethodModel> PassthroughMethods,
    ImmutableArray<PassthroughMemberModel> PassthroughMembers,
    ImmutableArray<Diagnostic> Diagnostics
);

internal sealed record PassthroughMethodModel(
    string Name,
    string ReturnTypeFqn,
    bool IsAsync,
    string ParameterList,
    string ArgumentList,
    string InnerReceiver = "_inner", // see MethodModel.InnerReceiver
    string TypeParameterList = "",   // see MethodModel.TypeParameterList
    string ConstraintClauses = "",   // see MethodModel.ConstraintClauses
    string ExplicitImplementations = "", // see MethodModel.ExplicitImplementations
    string ExplicitInterface = "",   // see MethodModel.ExplicitInterface
    string ExplicitConstraintClauses = "", // see MethodModel.ExplicitConstraintClauses
    bool ReturnsByRef = false,       // `ref` or `ref readonly` return: forwarded as `=> ref ...`
    bool HidesObjectMember = false   // see MethodModel.HidesObjectMember
);

// A non-method interface member forwarded to the inner service unchanged: no policy ever applies
// to a property, indexer or event, so unlike PassthroughMethodModel there is only one shape. Own
// and inherited members alike, abstract or default-implemented.
internal enum PassthroughMemberKind { Property, Indexer, Event }

internal sealed record PassthroughMemberModel(
    PassthroughMemberKind Kind,
    string Name,              // property/event name; "this" for an indexer (display only)
    string TypeFqn,
    bool HasGet,               // property/indexer: a get accessor is declared
    bool HasSet,                // property/indexer: a set (non-init) accessor is declared
    bool HasInit,                // property/indexer: an init accessor is declared
    string? ParameterList = null, // indexer only: "int index"
    string? ArgumentList = null,  // indexer only: "index"
    string InnerReceiver = "_inner", // see MethodModel.InnerReceiver
    string ExplicitImplementations = "", // see MethodModel.ExplicitImplementations
    string ExplicitInterface = "",   // see MethodModel.ExplicitInterface
    bool ReturnsByRef = false        // `ref` or `ref readonly` type: forwarded as `=> ref ...`
);

internal sealed record MethodModel(
    string Name,
    string ReturnTypeFqn,           // e.g. global::System.Threading.Tasks.ValueTask<string>
    ResultKind ResultKind,          // shape of the ZeroAlloc.Results type the method returns, if any
    string? ResultTypeFqn,          // e.g. global::ZeroAlloc.Results.Result<string>; null when ResultKind is None
    bool IsAsync,                   // true if ValueTask or Task
    bool ReturnsValue,              // false for void, ValueTask and Task: the inner call is a statement
    bool HasCancellationToken,
    string? CancellationTokenParamName, // name of the CancellationToken parameter, if any
    string ParameterList,           // "string id, CancellationToken ct"
    string ArgumentList,            // "id, ct"
    string ArgumentListWithToken,   // "id, __ct" (replaced CancellationToken arg)
    string? FallbackMethodName,
    RetryConfig? Retry,
    TimeoutConfig? Timeout,
    RateLimitConfig? RateLimit,
    CircuitBreakerConfig? CircuitBreaker,
    // The slot this method reads for each kind: its own when it has the attribute, otherwise the
    // interface slot; null when the method has no such policy.
    PolicySlot? RetrySlot = null,
    PolicySlot? TimeoutSlot = null,
    PolicySlot? RateLimiterSlot = null,
    PolicySlot? CircuitBreakerSlot = null,
    // The expression the call is made on: "_inner" for the interface's own members, and
    // "((global::Ns.IBase)_inner)" for an inherited one, so the call binds to that exact
    // declaration even where two base interfaces declare the same member.
    string InnerReceiver = "_inner",
    // The same for the fallback method, which may be declared in a base interface.
    string FallbackReceiver = "_inner",
    // "<T, TOut>" for a generic method, "" otherwise; also the type argument list of every call.
    string TypeParameterList = "",
    // " where T : class, new()" for a generic method with constraints, "" otherwise.
    string ConstraintClauses = "",
    // "value,other": the out parameters, assigned default before the first attempt, so they are
    // definitely assigned on every path that returns without a successful inner call.
    string OutParameterNames = "",
    // "global::Ns.IHandler<global::Ns.Msg>|...": when inherited declarations collapsed into this
    // member, every declaration after the first. Each gets an explicit interface implementation
    // that forwards through its own interface, so it reaches the inner service's implementation
    // of that declaration. Empty when there is only one declaration.
    string ExplicitImplementations = "",
    // "global::Ns.IBase": this declaration conflicts with the public member of its name, so it is
    // emitted as an explicit implementation of that interface. Empty for a public member.
    string ExplicitInterface = "",
    // " where T : class" or " where T : default": the constraints an explicit implementation must
    // state when its signature uses T?. Empty otherwise.
    string ExplicitConstraintClauses = "",
    // The public member has an object member's name and parameters, such as `object ToString()`,
    // so it is emitted with `new`.
    bool HidesObjectMember = false,
    // The method name, with the declaration number for a later declaration of the same name:
    // names the private helpers of a collapsed member, which must not collide between them.
    string HelperName = ""
)
{
    // Methods returning a Result whose failure the generator can build, sync or async, return
    // that failure from every policy failure site instead of throwing.
    public bool ReturnsFailureResult =>
        ResultKind is ResultKind.StringError or ResultKind.ResilienceError;
}

// The ZeroAlloc.Results return shapes the generator distinguishes.
internal enum ResultKind
{
    // Not a ZeroAlloc.Results type.
    None,
    // Result or Result<T>: the error is a string, built with Failure(string).
    StringError,
    // Result<T, ResilienceError> or UnitResult<ResilienceError>: built with Failure(new ResilienceError(...)).
    ResilienceError,
    // Result<T, E> or UnitResult<E> for any other E: the generator cannot build an E, so it only
    // passes Results returned by the inner call through.
    ForeignError,
}

// Effective config = method-level ?? class-level
internal sealed record RetryConfig(int MaxAttempts, int BackoffMs, bool Jitter, int PerAttemptTimeoutMs, bool NonThrowing = false);
internal sealed record TimeoutConfig(int TotalMs);
internal enum RateLimitScope { Shared, Instance }
internal sealed record RateLimitConfig(int MaxPerSecond, int BurstSize, RateLimitScope Scope);
internal sealed record CircuitBreakerConfig(int MaxFailures, int ResetMs, int HalfOpenProbes);
