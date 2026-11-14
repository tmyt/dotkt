# CLR event model for Kotlin — consume / implement / raise (MVVM, `INotifyPropertyChanged`)

Status: implemented event declaration/raise/delegation model (#187, #113, #186), extended by #216
with retained consumer handles and #188 with explicit custom storage/dispatch policies.

DotKt supports consuming, implementing, raising, and class-delegating CLR events. The declaration path provides
the accessor slots required by CLR interfaces; the value path lets ordinary Kotlin code retain a subscription
source without depending on a compiler-recognized adapter name. For example, the MVVM declaration/raise path is:

```kotlin
class ViewModelBase : INotifyPropertyChanged {
    override val PropertyChanged by clrEvent()                 // IMPLEMENT — synthesize add_/remove_/raise_
}
class ViewModelProperty<T>(private val vm: ViewModelBase, initial: T) {
    private var value = initial
    operator fun getValue(r: Any?, p: KProperty<*>): T = value
    operator fun setValue(r: Any?, p: KProperty<*>, nv: T) {
        if (value != nv) { value = nv; vm.PropertyChanged.invoke(vm, PropertyChangedEventArgs(p.name)) }  // RAISE from outside
    }
}
fun <T> ViewModelBase.viewModelProperty(initial: T) = ViewModelProperty(this, initial)
class PersonViewModel : ViewModelBase() { val name by viewModelProperty("John Doe") }
```

Related reading: `docs/dotkt-semantics.md` §8d (the consume side + the raise deviation this note
adds), the layer table in `docs/architecture.md` §0, and the existing consume pass
`toolchain/bir2cir/ClrEventSubscriptionBinding.Materialization.cs`.

---

## 1. The ECMA-335 event contract (what the runtime actually requires)

Confirmed against ECMA-335 (Partition I §12, Partition II §22.11–13; Codex read-only pass, verified
against the spec PDF):

- **An event is `add_` + `remove_`; a metadata `.event` row is reflection sugar.** The `Event`
  table row associates accessor methods through `MethodSemantics` (`AddOn 0x0008`, `RemoveOn 0x0010`,
  `Fire 0x0020`, `Other 0x0040`). A row **requires exactly one AddOn and one RemoveOn** and permits
  **zero or one Fire**. But `Event`/`EventMap` metadata **does not affect runtime dispatch** — it
  serves reflection (`Type.GetEvent`) and language tooling only.
- **Interface satisfaction is by the `add_E`/`remove_E` methods, not by `.event`.** A type that
  implements an interface declaring `event Action E` loads iff it supplies **virtual method
  implementations for the interface's `add_E`/`remove_E` slots** (name/signature match to a public
  virtual, or an explicit `MethodImpl`). The `.event` directive on the implementing type is
  **optional** for loading; its absence is exactly why kotc's silent emit fails — the accessors are
  missing, not the `.event` row. **We emit both** (accessors for correctness, `.event` for a clean
  reflectable type).
- **`raise_`/`.fire` is never required by the runtime.** Event invocation is ordinary code invoking
  the stored delegate. We synthesize a `raise_` accessor **by choice** (§5) to expose raise-from-
  outside cleanly and to give the `.event` a `.fire` association.
- **A field-like event lowers to a backing `MulticastDelegate` field + CAS accessors.** The C#
  field-like event (`public event D E;`) is a private `D E` field plus `add_/remove_` accessors that
  do a lock-free `Interlocked.CompareExchange` loop over `Delegate.Combine`/`Delegate.Remove`. This
  is the canonical, thread-safe shape we reproduce. Accessor IL skeleton (add; remove swaps
  `Combine`→`Remove`):

  ```il
  .method public hidebysig specialname newslot virtual final
          instance void add_E(class D 'value') cil managed {
    .locals init ([0] class D cur, [1] class D cmp, [2] class D upd)
      ldarg.0  ldfld class D C::E  stloc.0
    retry:
      ldloc.0  stloc.1
      ldloc.1  ldarg.1  call class System.Delegate System.Delegate::Combine(class System.Delegate, class System.Delegate)
      castclass D  stloc.2
      ldarg.0  ldflda class D C::E
      ldloc.2  ldloc.1
      call !!0 System.Threading.Interlocked::CompareExchange<class D>(!!0&, !!0, !!0)  stloc.0
      ldloc.0  ldloc.1  bne.un.s retry
      ret
  }
  ```

  The interface-slot accessors carry `newslot virtual final specialname`; `.fire` (`raise_E`) is a
  plain method. (The exact flag set is conventional — slot mapping, not the flags, is what prevents
  the missing-implementation `TypeLoadException`; we use the C# flag set for a clean reflectable type.)

---

## 2. Consumer values and declaration operations

`ClrEvent<H>` is a real stdlib class. H is the entire handler shape, including all arguments and the return.
For a foreign event, H is its nominal delegate type; ordinary SAM conversion supplies that delegate. Kotlin
events may also declare a Kotlin function handler shape. No layer selects an application payload or discards a sender.

| Operation | Kotlin surface | Representation |
|-----------|----------------|----------------|
| Read | `val event = source.Changed` | one retained handle with add/remove callbacks capturing the source once |
| Consume | `event.subscribe(handler)` | ordinary stdlib call, add the exact handler and return its close token |
| Implement | `override val E by clrEvent()` or `val E: ClrEvent<D> by clrEvent()` | synthesized backing delegate and add/remove/raise accessors |
| Raise | `source.E(args)` or `source.E.invoke(args)` | declaration-bound call of the synthesized raise accessor |

Handles are ordinary parameters, results, fields, locals and captures, including across DLLs. Reading a handle
does not subscribe. Separate subscriptions through a handle retain independent tokens. Raising is still a
declaration operation; a retained foreign handle does not gain a raise capability.

The runtime constructor takes the two ordinary `(H) -> Unit` callbacks. It is compiler-facing
(`@PublishedApi internal`), not a supported custom-event construction API. `subscribe` and
`EventSubscription.close` are ordinary Kotlin implementation bodies. Close atomically claims removal before
calling the remove callback, so concurrent or reentrant calls remove once, even if removal throws. It does not
wait for an already in-flight native callback or serialize a foreign event's own add/remove implementation.

A user library can implement a cold Flow adapter by retaining this handle, calling `subscribe` inside
`callbackFlow`, and closing the token in `awaitClose`. The handler factory chooses E from H, not the compiler.
The stdlib acquires no kotlinx.coroutines dependency and no special `asFlow` path.

## 3. Event declaration identity is not a handle-valued property

dll2klib marks projected event properties with compiler-owned `ClrEventDeclaration`. kotc follows that role
through overrides and delegation, and recognizes a local `by clrEvent()` initializer as an event declaration.
The return type alone is not a declaration role: `class Box<H>(var event: ClrEvent<H>)` must retain its ordinary
field, getter and setter rather than synthesize another native event.

An interface event projection remains overridable without imposing a frontend abstract-property obligation.
A CLR base can satisfy an interface event with a private explicit implementation while exposing a same-named
public event with another handler type; an abstract Kotlin property would incorrectly require a replacement.
A Kotlin class inheriting that implementation elides the event fake override. A direct implementer supplies
`override val E by clrEvent()`; the declaration emission check still reports a missing provider. This is
independent of whether ClrEvent is a materialized runtime class.

The retained handle is not subclassed. Custom storage and dispatch use the separate `ClrEventPolicy<H>`
contract described in §5. Retaining a consumed event does not change its declaration's storage policy.

---

## 4. Decision 2 — the three lowerings (BIR/CIR node shapes)

Event-member reads use BIR `clrEventGet{type,name,static,recv,sty}`. The result stamp carries the complete
`ClrEvent<H>` type, distinct from the event owner's type. Accessor calls use CIR
`clrEventAdd`/`clrEventRemove{type,event,static,recv,handler}`.

Restricted reads also carry the Kotlin `memberVisibility:"private"|"protected"|"internal"` fact into their
callbacks, including inline/default carriers. Accessor shells retain the property's source visibility
and `PublishedApi` annotation. bir2cir applies normal PublishedApi promotion while preserving the
source visibility carrier for KLIB reimport. Generated callbacks use ordinary exact member calls and
the existing lexical-access bridge machinery when direct CLR access is illegal; neither the native
event nor its backing storage is widened. Public events retain their direct accessor path.

### 4.1 CONSUME — retained handles

kotc projects event declaration identity, the source expression and H, with no access-mode restriction on the
read. Ordinary `subscribe` calls use normal Kotlin call emission, including ordinary declaration identity and
generic signatures.

bir2cir's `ClrEventSubscriptionBinding` materializes each read: evaluate the source into one local, construct
two ordinary closures capturing that local, then construct the stdlib handle. Static callbacks have no source
capture. The callbacks pass their exact H to the event's add/remove accessors. Generic source constraints and
complete handler applications are carried into each callback's CLR generic frame. Native static/instance and
virtual/interface/explicit-interface dispatch are resolved from the actual event metadata. A local declaration
binds to its synthesized accessors; class delegation uses the module-wide forwarder relation from §4.4.

The normal closure, declaration-frame, function representation and slot-alignment machinery handles these newly
materialized bodies. ilemit receives ordinary runtime handle construction and fully bound accessor calls, never
`clrEventGet`. Handler SAM conversion happens at the normal `subscribe(H)` call boundary; the installed object
is also the token's stored handler, so remove never reconstructs a delegate.

### 4.2 IMPLEMENT — `override val E by clrEvent()` (field-like event)

The member obligation (§3) plus the `clrEvent()` marker (§5) triggers synthesis of a field-like event.
Layer split mirrors the consume path (kotc declares + wires overrides; bir2cir supplies the CLR
relation; ilemit emits pure CLR codegen):

- **kotc** (`BirEmitter*`): on the implementing type, emit
  1. a backing field member `clrEventBacking{name:"<E>", static:<Boolean>, handlerType:<Kotlin fn type>}` (kotc does not
     name `MulticastDelegate` — it carries the *handler Kotlin function type*; bir2cir resolves the
     concrete delegate). The backing also carries the source `modality` and, for class overrides,
     `classOverrideOwner` with the frontend-selected constructed declaring supertype;
  2. three accessor method declarations `add_<E>`/`remove_<E>`/`raise_<E>`, each with an empty body
     tagged `{"k":"clrEventAccessor","kind":"add"|"remove"|"raise","event":"<E>"}` and an `overrides`
     closure (the existing `overridesJson` shape, `BirEmitterDeclarations.kt:423`) naming the interface
     event slot as `{owner:<iface FQN>, member:"<E>", kind:"event-add"|"event-remove", arity:1}`.
     Override wiring is frontend-resolved (per the no-re-resolution invariant); bir2cir derives the
     concrete `add_E`/`remove_E` slot names off ref.dll from these refs, exactly as it does for
     property accessors.
- **bir2cir** (new pass `ClrEventImplBinding.cs`, sibling of `ClrEventSubscriptionBinding.cs`): this is
  the Kotlin↔CLR relation. Resolve the interface event off ref.dll → its concrete `EventHandlerType`
  (the delegate `D`) and the interface accessor slot names. Rewrite the backing field to `<E>$delegate : D`,
  and rewrite each accessor's tagged body into a CIR directive `clrEventAccessorImpl{kind, field:"<E>$delegate",
  delegateType: clr:D, static:<Boolean>}` carrying the resolved delegate type + the interface `MethodImpl` slot to bind.
  Also stamp a type-level `clrEventDecl{name, field, add, remove, raise, delegateType}` record so
  ilemit emits the `.event` metadata.
- **ilemit** (`Emitter.ClrInterop.cs`, `EmitClrEventAccessorImpl`): pure CLR codegen — emit the CAS
  loop of §1 (`Delegate.Combine`/`Remove` + `Interlocked.CompareExchange<D>`) for add/remove, the
  `field?.Invoke(args)` for raise, emit bir2cir's resolved slot flags + wire the
  interface `MethodImpl`, and emit the `.event D <E> { .addon; .removeon; .fire }` row from
  `clrEventDecl`. ilemit knows no Kotlin — it consumes the resolved delegate type + field.

New open declarations introduce virtual slots; class overrides reuse the base slots rather than
introducing new ones. Final overrides seal those slots. Interface implementations introduce their own
slots, sealed only when the source property is final. A synthesized raise follows the same source
modality, but only overrides a referenced event's raise slot when its MethodSemantics actually declares
an accessible virtual non-final class slot. Final new add/remove accessors remain virtual-final so a later
subclass can satisfy an interface with that inherited event. Local class overrides use the base declaration's
delegate ABI and function-signature discriminator, substituted through the constructed owner, rather than
allocating a delegate again from a covariant or inferred handler type. Foreign add/remove/raise slots are
selected by EventInfo and carried as exact MethodImpl operands, not inferred from accessor naming conventions.

Companion-block declarations carry the frontend's receiverless static fact on the backing recipe and
accessor shells. Their accessors are static and non-virtual, with static delegate or policy storage.
For a generic Kotlin owner, bir2cir relocates the event relation together with its accessor declarations
to the same non-generic static carrier, before materializing consumer callbacks. ilemit follows the
explicit static CIR directive: `ldsfld`/`ldsflda` and parameter offsets without a `this` argument.

### 4.3 RAISE — `vm.E.invoke(sender, args)`

- **kotc**: `invoke` resolved on `kotlin.clr.ClrEvent<T>` (`operator fun invoke`) whose
  receiver is a `clrEventGet` → a dedicated dialect node `clrEventRaise{type,event,static,super,recv,args}`
  (parallel to `clrEventGet`; emitted in `BirEmitterCalls.kt`). This declaration-bound raise does not
  construct a consumer handle. Because raise is `operator fun invoke`, **`vm.E.invoke(s, a)` and `vm.E(s, a)` desugar
  to the identical call** (Kotlin's invoke convention) → both produce `clrEventRaise`; the author
  writes whichever form (`.invoke` = explicit/C#-`Invoke`-parallel, `E(…)` = idiomatic sugar),
  ABI-identical. The owner is the frontend-selected event declaration instantiated through the receiver,
  not an inheriting receiver's class. The receiver and arguments retain their ordinary evaluation order.
- **bir2cir** (`ClrEventImplBinding`): bind `clrEventRaise` to
  an ordinary static or instance call of its actual raise accessor. A local event record identifies its compiler-authored
  MethodDef; a referenced Kotlin event supplies its exact EventInfo raise method and constructed owner.
  The reference call carries the already-selected scalar `memberRef`, not a generated-name lookup in ilemit.
  `super` remains non-virtual, including through lifted callbacks. Restricted accessor calls retain their
  visibility facts for the ordinary lexical-access lowering; neither the event nor its storage is widened.
  **Guard:** raise is legal only for a **Kotlin-implemented** event, whether local or from a DLL.
  A consumed foreign .NET event remains non-raisable; a similarly named method or a native `.fire`
  association alone does not authorize it.
- **ilemit**: emit the resolved ordinary call (§4.2 emitted its body as `field?.Invoke(args)` — the
  null-conditional makes **raise with zero subscribers a safe no-op**, matching a C# field-like event;
  the backing delegate is null until the first subscription, so `vm.E(s, a)` on an unsubscribed event does
  nothing rather than NPE-ing).

### 4.4 DELEGATION — `class A : B by c` (#186)

A third IMPLEMENT flavor: forwarding, not field-like. For a delegated CLR interface event, kotc
synthesizes `add_<E>`/`remove_<E>` declaration shells plus a `clrEventForwarders` fact containing the
frontend-resolved delegated receiver expression and overridden event slot. bir2cir resolves that slot's
concrete delegate type, writes forwarding `clrEventAdd`/`clrEventRemove` bodies against `$$delegate_0`
(`c`), and emits the event metadata. No backing event field and no `raise_` are synthesized (the event
is raised by `c`). At the use site, `a.E.subscribe(h)` goes through the widened `clrEventGet` (§4.1);
CLR interface dispatch calls `add_<E>` on `A`, and closing the token calls `remove_<E>` on `A`. Both
forward the **same concrete delegate instance** to `c`. The declaration/use relation is collected
module-wide, so the delegating class and subscription may live in different Kotlin source files.

> #174 touched the same general class-delegation synthesis area, but its return-covariance correction
> was completed separately and is not part of the event-forwarding implementation.

---

## 5. Decision 3 — the `clrEvent()` intrinsic contract

`clrEvent()` is the author-written marker meaning "**synthesize the field-like event impl here**". It
is declared by the CLR stdlib beside `byref`/`stackBuffer` and recognized by kotc as a top-level intrinsic:

```kotlin
package kotlin.clr
fun clrEvent(): ClrEvent<Nothing>        // covariant handle; the returned value is never real
```

**Shape — a recognized property delegate, not a real one.** `override val E by clrEvent()` must
typecheck under Kotlin's delegate convention, so `ClrEvent<T>` carries an
`operator fun getValue(r: Any?, p: KProperty<*>): ClrEvent<T>` (§3). But no `getValue` is ever called:
kotc **recognizes** that the property's delegate initializer is a call to `kotlin.clr.clrEvent` and,
instead of emitting a delegated property + `getValue`, **synthesizes the event impl** of §4.2. This is
a pure kotc intrinsic (like `byref`) — the `clrEvent()` call and the `ClrEvent<T>` delegate are
consumed, never emitted. This declaration marker is separate from materializing an event read.

**Detection — "direct implementation where the base does NOT already satisfy the slot".** Three cases,
one rule:

| Case | Base supplies `add_/remove_`? | Author writes `by clrEvent()`? | kotc action |
|------|-------------------------------|-------------------------------|-------------|
| `class ViewModelBase : INotifyPropertyChanged` (override an interface slot) | no (interface only) | **yes** (obligation forces it, §3) | **SYNTHESIZE** field-like (§4.2), handler type from the interface slot |
| `val clicked: ClrEvent<D> by clrEvent()` (declare a NEW event, no `override`) | n/a — brand-new member | **yes** | **SYNTHESIZE** field-like (§4.2), handler type `D` from the **explicit `ClrEvent<D>`** annotation |
| `class MyApp : Avalonia.Application` | **yes** (base class implements it) | no (nothing to write — concrete fake-override) | **ELIDE** (event declaration role) |
| `class A : B by c` | no — delegate `c` supplies at runtime | no (`by c`) | **SYNTHESIZE forwarder** (§4.4) |

**`val`-only — `var E by clrEvent()` is a hard error.** An event is a read-only handle (you
subscribe/unsubscribe/raise, never reassign). `ClrEvent<T>` provides only `operator fun getValue`, no
`setValue`, so `var E by clrEvent()` already fails Kotlin's delegate convention ("missing `setValue`");
kotc additionally emits a **specific diagnostic** ("an event must be declared `val`, not `var`") when
it recognizes `var … by clrEvent()`, so the message points at the real constraint instead of the
generic delegate error.

**New-event handler type is explicit.** For an override (`override val E by clrEvent()`), kotc reads
the handler function type off the overridden interface slot (frontend-resolved; bir2cir maps it to the
concrete delegate). For a **new** declaration there is no slot to infer from, so the author annotates
`ClrEvent<D>` explicitly (`val clicked: ClrEvent<Action> by clrEvent()`);
kotc carries `D` as the handler Kotlin function type and bir2cir resolves/synthesizes the concrete
delegate. A bare `val clicked by clrEvent()` with no inferable `T` is a diagnostic ("cannot infer the
event handler type; annotate `ClrEvent<…>`").

The direct `by clrEvent()` initializer is the field-like synthesis signal. Projected event metadata and
overridden declarations distinguish other event members from ordinary handle storage; a handle's type alone
does not grant declaration or synthesis behavior.

### Custom storage and dispatch

`by clrEvent(policy)` explicitly declares the same native event with a user-defined
`ClrEventPolicy<H>`. The retained `ClrEvent<H>` handle remains final and unchanged:

```kotlin
abstract class ClrEventPolicy<out H> protected constructor() {
    protected abstract fun add(handler: @UnsafeVariance H)
    protected abstract fun remove(handler: @UnsafeVariance H)
    abstract operator fun invoke(vararg args: Any?)
}

val Changed: ClrEvent<MyHandler> by clrEvent(myPolicy)
```

The policy receives the complete registered handler. It owns storage, filtering and dispatch;
the compiler does not implement weak references or infer a payload type. Its protected hooks are
connected by stdlib-internal bridges to native `add_Changed`/`remove_Changed`; `raise_Changed`
packs the native delegate's arguments into the policy's argument array. A policy exception propagates
normally. Consumer subscription remains exclusively `subscribe`/`EventSubscription.close`.
Hooks are protected in the Kotlin API and on the policy's class declaration. Compiler-generated
existential views are internal ABI, not additional supported subscription APIs or a security boundary
against C# callers deliberately inspecting representation carriers or using reflection.
The policy's argument-array contract requires boxable native argument types. Managed references,
unmanaged pointers/function pointers and byref-like values cannot enter that array; a custom-policy
declaration with such an argument is a compilation error, not a copied-reference approximation or
undefined behavior. This restriction does not change the existing field-like event path.
Like field-like declarations, custom-policy declarations currently use instance accessors. Ordinary
objects and companion objects are instances and work normally. Kotlin 2.4 receiverless companion-block
event declarations are not implemented and are diagnosed for either factory overload; this does not
restrict consuming static events imported from CLR libraries.
Top-level native event declarations are likewise diagnosed rather than silently emitted as ordinary
delegated properties. An ordinary `val handle by policy` remains supported at top level.

kotc retains the real Kotlin delegate field and its original initializer, including source order and
single evaluation. `clrEventBacking.policyField` explicitly identifies that declaration. bir2cir
resolves the native delegate and creates ordinary calls to the policy using the field's actual
generic frame. It emits the same `.event` row and interface slots as a field-like event, but no
native multicast backing field. ilemit sees ordinary calls plus native event metadata, not policies.

Delegating directly to a policy (`val handle by myPolicy`), without `clrEvent`, creates an ordinary
handle-valued property. Neither a policy type nor a matching `getValue`/`invoke` API makes a property
an event. Subscription tokens intentionally retain their exact handler strongly until the token
itself is no longer reachable; a weak-event test must drop that token as well as its subscriber.

---

## 6. Decision 4 — RAISE-from-outside is a deliberate CLR-native deviation

**The declaring-type-only rule is C#'s, not the CLR's.** C# permits raising an event only from within
the declaring type (`E?.Invoke(...)` is legal only inside `C`) — but that is a *C# language*
convention, a consequence of C# lowering a field-like event to a **private** backing delegate field
plus a name-as-field shorthand scoped to the declaring class. **The CLR imposes no such restriction:**
ECMA-335 has a first-class `Fire` MethodSemantics slot (§1, `0x0020`) for the raise accessor, and
accessor *accessibility* is emitter-controlled — a public raise method is entirely spec-legal (VB.NET's
`RaiseEvent` uses exactly such a Fire method). So DotKt exposing raise is **not a broken CLR rule**; it
is choosing an accessibility C# declines to expose. The user's MVVM pattern raises
`vm.PropertyChanged.invoke(vm, args)` from **outside** — inside `ViewModelProperty.setValue`, a
*different* type. DotKt **relaxes the C# convention** for event declarations. Concretely (§4.2/§4.3), the synthesized
`raise_<E>` accessor retains the event's visibility (the `.event`'s `.fire`), and declaration-bound
`vm.E.invoke(...)` lowers to it. A public event therefore exposes a public raiser; private, protected,
and internal events keep their corresponding access restrictions. The operation is not available on an
arbitrary saved handle. Thus a public `vm.PropertyChanged.invoke(...)` from another type is legal
and simply calls `vm.raise_PropertyChanged(...)`.

This passes all three conditions of the acceptance test (`docs/dotkt-semantics.md`): **consistent** (consumer handles
subscribe; event declarations may raise), **documented** (§8d + this note), and
**convincingly explainable** (it is exactly the general-purpose event pattern .NET libraries hand-roll
with a `protected virtual void OnPropertyChanged(...)` raiser — DotKt makes the raiser a first-class
part of the event handle rather than boilerplate the author must write, which is what enables the
`ViewModelProperty` delegate pattern to raise a base class's event). Recorded as an interop-first
deviation in `docs/dotkt-semantics.md` §8d. (A *consumed* foreign event has no synthesized `raise_` and
`invoke` on it stays an error — you still cannot raise someone else's event; the deviation is scoped to
Kotlin-declared events.)

> Interop note: the `raise_<E>` accessor is linked to the `.event` as its `.fire` (§1), so a **C#**
> consumer sees it hidden behind the event abstraction (like `add_/remove_`, C# won't let you call
> `raise_E` by name) — harmless, because raising *another* assembly's event is not a real cross-language
> use case, and DotKt's own `handle.invoke(...)` lowers **directly** to the accessor regardless of C#'s
> accessor-hiding. The public accessibility matters for the in-language raise-from-outside (the
> `ViewModelProperty` delegate raising the ViewModel's event), which is the pattern this enables.

---

## 7. Decision 5 — the canonical conformance case (NUnit)

The user's `ViewModelBase`/`PersonViewModel` is the acceptance test, added as an NUnit fixture under
the migration (`tests/interop/consumer/fixtures/ClrEventTests.kt`, the `@TestAttribute` + `ClassicAssert`
shape of the migrated `*Tests.kt` batteries). It exercises IMPLEMENT (`by clrEvent()`), the property-delegate
RAISE-from-outside, and CONSUME via the `INotifyPropertyChanged` interface slot:

```kotlin
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert
import System.ComponentModel.INotifyPropertyChanged
import System.ComponentModel.PropertyChangedEventArgs
import kotlin.clr.clrEvent
import kotlin.reflect.KProperty

open class ViewModelBase : INotifyPropertyChanged {
    override val PropertyChanged by clrEvent()                        // IMPLEMENT
}
class ViewModelProperty<T>(private val vm: ViewModelBase, initial: T) {
    private var value = initial
    operator fun getValue(r: Any?, p: KProperty<*>): T = value
    operator fun setValue(r: Any?, p: KProperty<*>, nv: T) {
        if (value != nv) { value = nv; vm.PropertyChanged.invoke(vm, PropertyChangedEventArgs(p.name)) }  // RAISE
    }
}
fun <T> ViewModelBase.viewModelProperty(initial: T) = ViewModelProperty(this, initial)
class PersonViewModel : ViewModelBase() { var name by viewModelProperty("John Doe") }

class ClrEventTests {
    @TestAttribute
    fun propertyChangedFiresWithPropertyName() {
        val vm = PersonViewModel()
        var fired = 0
        var lastName: String? = null
        val subscription = (vm as INotifyPropertyChanged).PropertyChanged.subscribe { _, e ->
            fired++; lastName = e.PropertyName
        }                                                            // CONSUME through the interface slot
        vm.name = "Jane Doe"
        ClassicAssert.AreEqual(1, fired)                             // raised exactly once
        ClassicAssert.AreEqual("name", lastName)                     // args carry the KProperty name
        vm.name = "Jane Doe"                                         // unchanged value -> no raise
        ClassicAssert.AreEqual(1, fired)
        subscription.close()
        vm.name = "Bob"
        ClassicAssert.AreEqual(1, fired)                             // unsubscribed -> no raise
    }
}
```

Pass criteria: the type **loads** (no `TypeLoadException`), **ilverify-clean** (`add_/remove_` satisfy
the `INotifyPropertyChanged` slots), closing the subscription removes the exact handler, and the raise
carries `"name"`. A pure-CLR control (a C# `INotifyPropertyChanged` implementer subscribed from Kotlin)
already passes via the consume path and stays a control.

---

## 8. Invariants this respects

- **kotc reads no CLR metadata.** kotc carries the handler as a *Kotlin function type* and names
  override slots by *Kotlin identity* (`{owner FQN, member name, event-add/remove kind}`); bir2cir
  resolves the concrete `EventHandlerType` delegate + `add_E`/`remove_E` names off ref.dll. The
  `.NET event` vocabulary (`clrEventGet`/`clrEventRaise`/`clrEvent()`) is reference-KLIB-projected CLR-only
  vocab kotc lowers as dialect — the sanctioned exception (like `byref`/`ClrRef<T>`), not a metadata read.
- **bir2cir owns the Kotlin↔CLR relation.** All delegate-type resolution, accessor-slot naming, CAS
  vs. forward body choice, and the raise binding live in bir2cir — the one layer that reads ref.dll.
- **ilemit knows no Kotlin.** It emits the CAS loop / `.event` / `MethodImpl` from a resolved CIR
  directive (`clrEventAccessorImpl` + `clrEventDecl`). Runtime ClrEvent construction is ordinary class construction;
  the declaration marker `clrEvent()` and event-read dialect node never reach it.
- **Consumer representation is lossless.** H remains the exact complete handler; no payload projection, coroutine
  dependency or adapter-name recognition belongs in the compiler or the stdlib handle.
