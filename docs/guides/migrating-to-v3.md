---
id: migrating-to-v3
title: Migrating to 3.0
sidebar_position: 6
---

# Migrating to 3.0

3.0 makes the generated proxy implement every public instance member an interface declares or
inherits, abstract or default-implemented. That is the main reason for the major version: some
interfaces that built without errors or warnings on 2.0 now get a proxy they did not get before,
behave differently at runtime, or report a diagnostic.

Most interfaces need no change. Read the table, then check the interfaces it applies to.

## Breaking changes

| Change | 2.0 | 3.0 |
|---|---|---|
| Interfaces whose methods are all inherited, such as `[Retry] interface IMyDispatcher : IOutboxDispatcher<MyMessage> { }`, and interfaces with only properties, indexers or events | no proxy and no `Add{Name}Resilience` method were generated, and the interface compiled | a proxy is generated, and the inherited methods get the interface-level policies |
| Unsupported shapes on such interfaces | no proxy, so nothing was reported | an error and no proxy: [ZR0003](../diagnostics/ZR0003.md) or [ZR0007](../diagnostics/ZR0007.md) |
| Unsupported shapes on interfaces that did get a proxy, such as a generic interface, a private nested interface, a `static abstract` member or `NonThrowing` on a method that returns no `Result` | a compiler error in the generated code, such as CS0246, CS0122, CS0535, CS8920 or CS1029 | a ZeroAlloc error with the reason, ZR0003 or ZR0007, and no proxy |
| Default-implemented members: inherited methods, and own or inherited properties, indexers and events | not implemented by the proxy, so a call through the proxy ran the interface's default body | forwarded to the inner instance, so an override in the implementation wins. An inherited default method gets the interface's policies, as an own default method already did in 2.0. A policy it cannot take is left off with [ZR0006](../diagnostics/ZR0006.md): on an async method with a `ref`, `out`, `in` or ref struct parameter such as `ReadOnlySpan<char>`, or a method that returns by reference |
| `sealed` interface members | an own `sealed` method got an unrelated public proxy method and policy slot, which a call through the interface never reached | not forwarded, since no class can implement them. The `sealed` method's own slot is gone; every other slot keeps its name. A policy attribute on it is reported with warning [ZR0006](../diagnostics/ZR0006.md) |
| `ToString()`, `Equals(object)` or `GetHashCode()` declared on the proxied interface itself | forwarded to the inner instance by a public proxy method that hid `object`'s, with warning CS0114 | not forwarded: `object`'s own member on the proxy implements it, as it already did for such members in a base interface, so the proxy keeps its own equality. Its slot is gone; every other slot keeps its name. A policy that applied to it in 2.0, its own or the interface's, is reported with warning [ZR0006](../diagnostics/ZR0006.md) |
| Inherited `IDisposable.Dispose` and `IAsyncDisposable.DisposeAsync` | not forwarded | forwarded, and wrapped in the interface-level policies like any inherited method |
| Warning [ZR0002](../diagnostics/ZR0002.md) | only for the interface's own methods | also for inherited methods, abstract or default, without a `CancellationToken`, such as `DisposeAsync` in `[Timeout(Ms = 500)] interface IClient : IAsyncDisposable` |
| Warning [ZR0006](../diagnostics/ZR0006.md) | did not exist | a policy an inherited default method cannot take is left off it, and reported; so is a policy on an own method the proxy does not implement |

With `TreatWarningsAsErrors`, the new warnings fail the build until they are addressed.

## What to check

**Interfaces with no methods of their own.** If one of them carries a policy attribute, it now gets
a proxy. If you relied on it having none, remove the attribute. If it now reports ZR0003 or ZR0007,
the diagnostic page describes the fix.

**Default-implemented members.** A call through the proxy now reaches the inner instance. If your
implementation overrides a default member and the proxy used to run the default body, the
override now runs. An inherited default method is also retried, timed out, rate-limited or
circuit-broken by the interface-level policies.

**Disposal.** If the proxied interface inherits `IDisposable` or `IAsyncDisposable`, the proxy's
`Dispose` or `DisposeAsync` calls the inner instance's, under the interface-level policies: a failing
`DisposeAsync` is retried, and `[Timeout]` reports ZR0002 for it. To keep the policies off disposal,
put them on the methods that need them instead of on the interface. When the container created both
the proxy and the inner instance, it disposes both, so the inner instance's `Dispose` can run twice;
`Dispose` should tolerate that, as the `IDisposable` contract requires.

## Fixed along the way

These shapes now get a valid proxy. On an interface with methods of its own, 2.0 generated a proxy
for them that failed to compile. On an interface without methods of its own, 2.0 generated no proxy
at all and skipped them silently.

- interfaces that inherit members from base interfaces, and the documented Outbox and Scheduling
  bridge interfaces
- generic methods and methods with `ref`, `out`, `in` or `params` parameters, #173. An `out`
  parameter under a policy is assigned `default` before the first attempt; see
  [Source Generator](../source-generator.md#generic-methods-and-by-reference-parameters)
- default-implemented properties with a `private set`, which now get only their `get` accessor
- methods and properties that return by `ref` or `ref readonly`, forwarded as `=> ref ...` when no
  policy applies to them; a policy on one is [ZR0007](../diagnostics/ZR0007.md), or
  [ZR0006](../diagnostics/ZR0006.md) for an inherited default method
- async methods with a ref struct parameter, such as `ReadOnlySpan<char>`, forwarded without
  `async` when no policy applies to them; a policy on one is ZR0007, or ZR0006 for an inherited
  default method
- generic methods that differ only in their type parameter names, such as `Foo<T>(T)` and
  `Foo<U>(U)`, which are one member
- declarations of one name that one public member cannot implement: identical members from two
  instances of a generic base, such as `IHandler<Foo>` and `IHandler<Msg>`, members that differ in
  type or nullability, `new`-hiding such as `IEnumerable<T>` over `IEnumerable`, and a property, a
  method or an indexer with the same name, such as a property `Item` and an indexer. One
  declaration is public and every other one gets an explicit interface implementation, so each
  reaches the inner instance's own implementation of it; see
  [Source Generator](../source-generator.md#declarations-with-the-same-name)

Policy slot names of the interface's own methods are unchanged, so code that sets
`{Name}ResiliencePolicies` properties keeps working. Inherited methods with their own attributes
get slots numbered after them.
