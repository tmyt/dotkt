# CLR event model for Kotlin — consume / implement / raise (MVVM, `INotifyPropertyChanged`)

Status: design note (2026-07-20), **ABI-locked, user-approved (2026-07-20), implemented**. The
initial implementation closed #187 (Kotlin class implementing a CLR interface event) + #113
(EmitClrEvent hardening) — the MVVM spine (declare / implement / raise). The §4.4 / S4 follow-up
implements #186 class-delegation event forwarding for 0.9.9. The related general delegation-return
work in #174 was handled separately.

This note completes the `.NET event` story. DotKt can **consume**, **implement**, **raise**, and
**class-delegate** CLR events. The implementation prevents a Kotlin class that names a CLR event
interface from silently emitting an invalid type (missing `add_E`/`remove_E` → `TypeLoadException` /
ilverify `InterfaceMethodNotImplemented`) and provides the event shape needed by .NET data binding
(WPF / WinUI / Avalonia MVVM):

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
`toolchain/bir2cir/ClrEventSubscriptionBinding.cs`.

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

## 2. Three operations, one fiction — `ClrEvent<T>` never materializes

A .NET event is not a first-class value; it exposes only add / remove / raise. DotKt surfaces it as a
**compile-time-only** `kotlin.clr.ClrEvent<T>` handle (T = the handler's Kotlin function type), and
**every** use is lowered away before ilemit. There are exactly three operations:

| Op | Kotlin surface | Realized as |
|----|----------------|-------------|
| **CONSUME** | `x.E.subscribe(h)` | call `add_E`, then return an `EventSubscription<T>` that calls `remove_E` with the exact added handler on `close()` |
| **IMPLEMENT** | `override val E by clrEvent()` (implement an interface slot) **or** `val E: ClrEvent<D> by clrEvent()` (declare a NEW event) | a synthesized backing delegate field + real `add_E`/`remove_E`/`raise_E` on the emitted type |
| **RAISE** | `vm.E.invoke(sender, args)` **or** `vm.E(sender, args)` (same `operator fun invoke`) | invoke the backing delegate via the `raise_E` accessor |

`ClrEvent<T>` is never instantiated, never a field, never a return value. §3 makes that a **type-level
guarantee** by turning it abstract; §4 gives each op's node shape and per-layer lowering.

---

## 3. Decision 1 — `ClrEvent<T>` is an ABSTRACT MARKER (user-directed)

`kotlin.clr.ClrEvent<T>` becomes an **abstract** type. It has no runtime instance and no runtime
meaning; it is a compile-time lowering tag. Two consequences, both wanted:

1. **`ClrEvent()` is unconstructable.** An abstract type forbids `new ClrEvent()`, so no code path
   can accidentally materialize the fiction. Any node that still carries a live `ClrEvent<T>` value
   past bir2cir is a bug the type system now surfaces, instead of leaking `kotlin.clr.ClrEvent` to
   ilemit (the #186 failure signature: an unresolved `kotlin.clr.ClrEvent` member call).
2. **A direct interface implementer must provide the event (#187 diagnostic).** A Kotlin class that
   directly implements a .NET interface event without `override val E by clrEvent()` would emit an invalid
   type (missing `add_/remove_` → `TypeLoadException`). This is caught with a real compile error.
   **Correction to the original plan (implementation reality):** the interface event member is emitted
   **OPEN** (overridable but NOT abstract), NOT abstract. An abstract member seemed to give a free frontend
   obligation, but it **wrongly breaks the `class MyApp : Avalonia.Application` ELIDE case**: a .NET base
   (`AvaloniaObject`) explicitly implements `INotifyPropertyChanged.PropertyChanged` (handler
   `PropertyChangedEventHandler`) while ALSO exposing its **own** same-name public `PropertyChanged` event of a
   **different** signature (`EventHandler<AvaloniaPropertyChangedEventArgs>`). dll2klib can surface only ONE
   `PropertyChanged` property (the public one, different handler), which therefore does **not** override the
   abstract interface slot — so an abstract member is *unsatisfiable* and `MyApp` fails to compile ("not
   abstract and does not implement abstract members"). **OPEN** lets `override val E by clrEvent()` typecheck
   while imposing no obligation, so ELIDE compiles. The #187 obligation is enforced at **kotc emission time**
   (`BirEmitter.checkUnimplementedClrEvents`), which distinguishes the cases by *provider*: a `ClrEvent<T>`
   fake-override is unsatisfied (→ #187 error) only when provided by **neither** a base CLASS that declares it
   (a Kotlin base that synthesized it — e.g. `PersonViewModel : ViewModelBase()`) **nor** an external .NET base
   class (`MyApp : Avalonia.Application`). Never a false positive; the sole residual gap is a false-negative for
   `class X : UnrelatedNetBase(), IEvented` (kotc-purity forbids reading .NET metadata to know which interface a
   .NET base implements).
3. **PRIVATE primary ctor — abstract for the obligation, but non-subclassable.** `ClrEvent<T>` is given a
   `private` primary constructor. Abstractness was chosen ONLY for the member-obligation mechanism, which
   never constructs or subclasses `ClrEvent<T>`; but `abstract` alone would let a user write
   `class My<T> : ClrEvent<T>() { override … }` — an unintended side door that would force `ClrEvent<T>`
   to materialize as a real emitted base type (violating §9's "ClrEvent<T> never reaches ilemit") and
   ship a non-interop **fake event** (the CLR `.event` synthesis is keyed on `clrEvent()`, so a custom
   subclass produces NO real CLR event). A private primary ctor keeps abstract-for-the-obligation fully
   intact (nothing legitimately constructs it — the `MyApp : Avalonia.Application` fake-override and the
   `clrEvent()` marker both never call it) while making `: ClrEvent<T>()` fail at the FRONTEND (a subclass
   in another file cannot reach the private ctor).

> FUTURE HOOK (out of scope for 0.9.7): [#188](https://github.com/tmyt/dotkt/issues/188) will turn this
> marker into a sanctioned runtime contract for `override val E by someCustomEventImpl()`. Custom implementations
> supply protected add/remove policy hooks and raise behavior; consumers still see only `subscribe`, never public
> `+=` / `-=`. The compiler maps those policy bodies to real CLR accessors and `.event` metadata.

**Per appearance:**

- **The `kotlin.clr.ClrEvent` type** (CLR stdlib): an abstract compile-time handle. It carries
  abstract `fun subscribe(h: T): EventSubscription<T>` (consume), abstract
  `operator fun invoke(vararg args): R` (raise), and abstract `operator fun getValue(r: Any?, p: KProperty<*>): ClrEvent<T>`
  (so `by clrEvent()` typechecks under the delegate convention — see §5). None have bodies; none
  are ever executed.
- **The interface event member** (dll2klib event projection): emitted **OPEN** on the interface — overridable
  (so `override val E by
  clrEvent()` typechecks) but non-abstract (no frontend obligation; see the correction in consequence #2 — an
  abstract member breaks the ELIDE case of a .NET base that explicitly implements the event). A CLASS event
  member stays final. dll2klib owns the projected declaration shape.
- **The #187 obligation** (kotc `BirEmitterDeclarations.checkUnimplementedClrEvents`): a `ClrEvent<T>`
  FAKE-OVERRIDE with no provider (neither a base CLASS that declares it, nor an external .NET base class) is a
  compile error pointing at the missing `by clrEvent()`.
- **The fake-override elision** (kotc `BirEmitterDeclarations.isClrEventProperty`): stays for the
  **base-class-satisfies** case (`class MyApp : Avalonia.Application` — the .NET base implements the interface
  at the CLR level; the inherited `ClrEvent<T>` fake-override is elided, no synthesis, and
  `checkUnimplementedClrEvents` skips it because the class has an external .NET base). It is **not** applied to
  a member the user explicitly implements with `by clrEvent()` (§5), nor to a delegation forwarder (§4.4, S4).

> Why the interface event member is OPEN, not abstract (empirically verified). An abstract interface event
> member was the original plan, assuming a .NET base that implements the interface would satisfy the slot. It
> does NOT: `AvaloniaObject` explicitly implements `INotifyPropertyChanged.PropertyChanged`
> (`PropertyChangedEventHandler`) **and** exposes its own same-name public `PropertyChanged`
> (`EventHandler<AvaloniaPropertyChangedEventArgs>`) — dll2klib surfaces only the latter (different handler),
> which does not override the abstract interface slot, so `class MyApp : Avalonia.Application` fails to compile
> ("not abstract and does not implement abstract members" — the `ktproj-avalonia` regression that proved this).
> **OPEN** keeps `override val E by clrEvent()` typechecking (overridable) while imposing no obligation, so the
> ELIDE case compiles; the #187 obligation is a kotc emission-time check keyed on the event *provider* (base
> CLASS that declares it, or external .NET base). `ClrEvent<T>` the TYPE stays abstract + private-ctor
> regardless (§3.1/§3.3).

---

## 4. Decision 2 — the three lowerings (BIR/CIR node shapes)

`ClrEvent<T>` never reaches ilemit; every op is consumed by kotc + bir2cir. Existing consume nodes
(unchanged): `clrEventGet{type,name,static,recv}` (kotc-dialect handle read),
`clrEventAdd`/`clrEventRemove{type,event,static,recv,handler}` (bir2cir-produced accessor call).

### 4.1 CONSUME — `x.E.subscribe(h)`

Already implemented and covered by `tests/interop/consumer/fixtures/ObservableCollectionEventTests.kt`.
kotc emits `clrEventGet` for the handle read
and a plain `ClrEvent.subscribe` call; bir2cir's `ClrEventSubscriptionBinding` evaluates the receiver and handler
once, emits `clrEventAdd`,
then constructs the stdlib `EventSubscription<T>` with a synthesized remove callback that captures the receiver.
ilemit's `EmitClrEvent` links the ref.dll accessor and emits
the delegate-wrap + `callvirt add_E`. For a #186 use site, kotc produces `clrEventGet` whenever the
accessed member is a `ClrEvent<T>` property, **regardless of whether the receiver is a .NET type or a
Kotlin type that implements a .NET-event interface**. bir2cir resolves a delegating Kotlin receiver
through the module-wide forwarder relation from §4.4, then binds the accessor to the delegated CLR
interface slot. The wrapper remains the receiver, so CLR interface dispatch enters its synthesized
forwarder.

### 4.2 IMPLEMENT — `override val E by clrEvent()` (field-like event)

The member obligation (§3) plus the `clrEvent()` marker (§5) triggers synthesis of a field-like event.
Layer split mirrors the consume path (kotc declares + wires overrides; bir2cir supplies the CLR
relation; ilemit emits pure CLR codegen):

- **kotc** (`BirEmitter*`): on the implementing type, emit
  1. a backing field member `clrEventBacking{name:"<E>", handlerType:<Kotlin fn type>}` (kotc does not
     name `MulticastDelegate` — it carries the *handler Kotlin function type*; bir2cir resolves the
     concrete delegate);
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
  delegateType: clr:D}` carrying the resolved delegate type + the interface `MethodImpl` slot to bind.
  Also stamp a type-level `clrEventDecl{name, field, add, remove, raise, delegateType}` record so
  ilemit emits the `.event` metadata.
- **ilemit** (`Emitter.ClrInterop.cs`, `EmitClrEventAccessorImpl`): pure CLR codegen — emit the CAS
  loop of §1 (`Delegate.Combine`/`Remove` + `Interlocked.CompareExchange<D>`) for add/remove, the
  `field?.Invoke(args)` for raise, mark the accessors `specialname newslot virtual final` + wire the
  interface `MethodImpl`, and emit the `.event D <E> { .addon; .removeon; .fire }` row from
  `clrEventDecl`. ilemit knows no Kotlin — it consumes the resolved delegate type + field.

### 4.3 RAISE — `vm.E.invoke(sender, args)`

- **kotc**: `invoke` resolved on `kotlin.clr.ClrEvent<T>` (the abstract `operator fun invoke`) whose
  receiver is a `clrEventGet` → a dedicated dialect node `clrEventRaise{type,event,static,recv,args}`
  (parallel to `clrEventGet`; emitted in `BirEmitterCalls.kt` alongside the existing
  `subscribe` handling). The `ClrEvent<T>` value is consumed, never
  materialized. Because raise is `operator fun invoke`, **`vm.E.invoke(s, a)` and `vm.E(s, a)` desugar
  to the identical call** (Kotlin's invoke convention) → both produce `clrEventRaise`; the author
  writes whichever form (`.invoke` = explicit/C#-`Invoke`-parallel, `E(…)` = idiomatic sugar),
  ABI-identical.
- **bir2cir** (`ClrEventImplBinding`): bind `clrEventRaise` to
  a call of the declaring type's `raise_<E>` accessor: `clrRaiseCall{recv, accessor:"raise_<E>", args}`.
  **Guard:** raise is legal only for a **Kotlin-implemented** event (one that has a synthesized
  `raise_<E>`); `clrEventRaise` against a *consumed* foreign .NET event (no `raise_`) is a bir2cir
  hard error ("cannot raise a .NET event you do not declare") — the correct CLR rule (you raise on the
  declaring instance).
- **ilemit**: emit the call to `raise_<E>` (§4.2 emitted its body as `field?.Invoke(args)` — the
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
typecheck under Kotlin's delegate convention, so `ClrEvent<T>` carries an abstract
`operator fun getValue(r: Any?, p: KProperty<*>): ClrEvent<T>` (§3). But no `getValue` is ever called:
kotc **recognizes** that the property's delegate initializer is a call to `kotlin.clr.clrEvent` and,
instead of emitting a delegated property + `getValue`, **synthesizes the event impl** of §4.2. This is
a pure kotc intrinsic (like `byref`) — the `clrEvent()` call and the `ClrEvent<T>` delegate are
consumed, never emitted. (Chosen over a real `provideDelegate` because the fiction must not survive to
runtime; a recognized-marker keeps `ClrEvent<T>` genuinely abstract.)

**Detection — "direct implementation where the base does NOT already satisfy the slot".** Three cases,
one rule:

| Case | Base supplies `add_/remove_`? | Author writes `by clrEvent()`? | kotc action |
|------|-------------------------------|-------------------------------|-------------|
| `class ViewModelBase : INotifyPropertyChanged` (override an interface slot) | no (interface only) | **yes** (obligation forces it, §3) | **SYNTHESIZE** field-like (§4.2), handler type from the interface slot |
| `val clicked: ClrEvent<D> by clrEvent()` (declare a NEW event, no `override`) | n/a — brand-new member | **yes** | **SYNTHESIZE** field-like (§4.2), handler type `D` from the **explicit `ClrEvent<D>`** annotation |
| `class MyApp : Avalonia.Application` | **yes** (base class implements it) | no (nothing to write — concrete fake-override) | **ELIDE** (`isClrEventProperty`, unchanged) |
| `class A : B by c` *(deferred → 0.9.8, §4.4 / S4)* | no — delegate `c` supplies at runtime | no (`by c`) | **SYNTHESIZE forwarder** (§4.4) |

**`val`-only — `var E by clrEvent()` is a hard error.** An event is a read-only handle (you
subscribe/unsubscribe/raise, never reassign). `ClrEvent<T>` provides only `operator fun getValue`, no
`setValue`, so `var E by clrEvent()` already fails Kotlin's delegate convention ("missing `setValue`");
kotc additionally emits a **specific diagnostic** ("an event must be declared `val`, not `var`") when
it recognizes `var … by clrEvent()`, so the message points at the real constraint instead of the
generic delegate error.

**New-event handler type is explicit.** For an override (`override val E by clrEvent()`), kotc reads
the handler function type off the overridden interface slot (frontend-resolved; bir2cir maps it to the
concrete delegate). For a **new** declaration there is no slot to infer from, so the author annotates
`ClrEvent<D>` explicitly (`val clicked: ClrEvent<Action> by clrEvent()`, or `by clrEvent<Action>()`);
kotc carries `D` as the handler Kotlin function type and bir2cir resolves/synthesizes the concrete
delegate. A bare `val clicked by clrEvent()` with no inferable `T` is a diagnostic ("cannot infer the
event handler type; annotate `ClrEvent<…>`").

The discriminator is structural and needs no `@Clr*` read (kotc purity): the presence of a
`by clrEvent()` delegate initializer *is* the "synthesize field-like here" signal, and it only ever
appears where the frontend obligation (§3) demands it — i.e. where **no supertype already provides a
concrete `ClrEvent<T>` member**. If a base class already satisfies the slot, there is no abstract
obligation, the member is a concrete fake-override, and the author never writes `by clrEvent()` (kotc
diagnoses a redundant `clrEvent()` if they do). If the author omits it where the slot **is** abstract
(the bare `class KC : IB` of #187), the frontend emits the normal "class is not abstract and does not
implement abstract member `PropertyChanged`" diagnostic — the silent-invalid-emit of #187 becomes a
compile error pointing at the missing `by clrEvent()`.

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
*different* type. DotKt **relaxes the C# convention**: a `ClrEvent<T>` handle **exposes raise**. Concretely (§4.2/§4.3), the synthesized `raise_<E>` accessor is emitted **public** (the
`.event`'s `.fire`), and `handle.invoke(...)` lowers to a call of it. So `vm.PropertyChanged.invoke(...)`
from any type is legal and simply calls `vm.raise_PropertyChanged(...)`.

This passes all three conditions of the acceptance test (`docs/dotkt-semantics.md`): **consistent** (a
`ClrEvent<T>` handle uniformly supports `subscribe`/`invoke`), **documented** (§8d + this note), and
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

## 8. Decision 6 — sequenced implementation plan (0.9.7)

Each step is independently gate-runnable through the focused NUnit fixture and `make verify`.
Order minimizes cross-layer churn: land the type-level marker + node vocabulary first, then the two
synthesis flavors, then hardening.

| # | Layer | Work | Closes |
|---|-------|------|--------|
| **S0** | kotc + dll2klib | `ClrEvent<T>` → **abstract** (add `invoke`, `getValue`); define the projected interface-event modality in `toolchain/dll2klib/Program.cs`; register `kotlin.clr.clrEvent` intrinsic. Adds the frontend obligation + the #187 missing-override diagnostic. | #187 (diagnostic) |
| **S1** | kotc | Recognize `by clrEvent()`; synthesize the backing field + `add_/remove_/raise_` decls with tagged bodies + `overrides` closure (§4.2). Widen `clrEventGet` to any `ClrEvent<T>` member read (§4.1). Emit `clrEventRaise` for `handle.invoke(...)` (§4.3). | #187 |
| **S2** | bir2cir | New `ClrEventImplBinding.cs`: resolve interface event → `EventHandlerType` + slot names off ref.dll; expand tagged accessor bodies → `clrEventAccessorImpl` CIR (CAS for field-like, forward for delegation); emit type-level `clrEventDecl`; bind `clrEventRaise` → `raise_<E>` call with the "no raise on consumed event" guard (§4.2/§4.3/§6). | #187 |
| **S3** | ilemit | `EmitClrEventAccessorImpl` (CAS loop §1 for add/remove, `field?.Invoke` for raise) + `MethodImpl` wiring to the interface slots + `.event` metadata from `clrEventDecl`. | #187 |
| **S4** *(implemented for 0.9.9)* | kotc + bir2cir | Class-delegation forwarder: synthesize forwarding `add_/remove_` for a delegated CLR interface event (§4.4); use-site `a.E.subscribe(h)` via widened `clrEventGet`; preserve exact delegate identity across add/remove. | #186 |
| **S5** | ilemit | Route **all** event emit (consume, implement, raise, `.event`) through the guarded `LinkClrMethod`/`RequireDispatch`/null-checked `GetEvent` family; legible `ilemit:` breadcrumb on a missing/value-type/constructed-generic event owner instead of an opaque NRE. | #113 |
| **S6** | tests + docs | Add `ClrEventTests.kt` (§7); record the raise deviation in `docs/dotkt-semantics.md` §8d; run the focused NUnit fixture and full gate. | — |

Sequencing notes: **S0–S3 are the #187 spine** and must land together (a half-landed abstract marker
without synthesis would red the gate on every existing interface-event consumer). **The initial 0.9.7
implementation = S0–S3 + S5 + S6** (spine + #113 hardening + tests). **S4 followed in 0.9.9**,
reusing S1's widened `clrEventGet` and the S2 event-binding machinery. **S5** is independent hardening and can land any time after S3
introduces the new emit sites. **S6** is the final gate + doc pass. Keep each new
concern in its own file (bir2cir `ClrEventImplBinding.cs`; ilemit `EmitClrEventAccessorImpl` in the
`Emitter.ClrInterop.cs` part) per the one-concern-per-file rule.

---

## 9. Invariants this respects

- **kotc reads no CLR metadata.** kotc carries the handler as a *Kotlin function type* and names
  override slots by *Kotlin identity* (`{owner FQN, member name, event-add/remove kind}`); bir2cir
  resolves the concrete `EventHandlerType` delegate + `add_E`/`remove_E` names off ref.dll. The
  `.NET event` vocabulary (`clrEventGet`/`clrEventRaise`/`clrEvent()`) is reference-KLIB-projected CLR-only
  vocab kotc lowers as dialect — the sanctioned exception (like `byref`/`ClrRef<T>`), not a metadata read.
- **bir2cir owns the Kotlin↔CLR relation.** All delegate-type resolution, accessor-slot naming, CAS
  vs. forward body choice, and the raise binding live in bir2cir — the one layer that reads ref.dll.
- **ilemit knows no Kotlin.** It emits the CAS loop / `.event` / `MethodImpl` from a resolved CIR
  directive (`clrEventAccessorImpl` + `clrEventDecl`); it never sees `ClrEvent<T>` or `clrEvent()`.
- **`ClrEvent<T>` never reaches ilemit** — enforced now by its abstractness (§3): an unlowered handle
  is an unconstructable abstract value, not a silent `kotlin.clr.ClrEvent` leak.
