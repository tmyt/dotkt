# CLR event model for Kotlin — consume / implement / raise (MVVM, `INotifyPropertyChanged`)

Status: design note (2026-07-20), **ABI-locked, not yet implemented**. Targets **0.9.7**. Closes
#187 (Kotlin class implementing a CLR interface event), #186 (class-delegation event forwarding),
#113 (EmitClrEvent hardening), and folds in #174 (delegation-forwarder return covariance).

This note completes the `.NET event` story. Today DotKt can **consume** a CLR event
(`c.CollectionChanged += h`) but cannot **provide** one from Kotlin: a Kotlin class that names a
CLR interface carrying an event (`class KC : IB`, `class ViewModelBase : INotifyPropertyChanged`)
compiles with no diagnostic and emits an invalid type (missing `add_E`/`remove_E` → `TypeLoadException`
/ ilverify `InterfaceMethodNotImplemented`). That gap is the last thing between Kotlin-on-CLR and
first-class .NET data binding (WPF / WinUI / Avalonia MVVM). This note locks the full model so a
Kotlin class can **implement** and **raise** a CLR event, realizing the user's target API:

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
adds), the layer table in `docs/ship-tasks.md` §0, and the existing consume pass
`toolchain/bir2cir/ClrEventOperatorBinding.cs`.

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
| **CONSUME** | `x.E += h` / `x.E -= h` | the event's `add_E`/`remove_E` accessor (works today) |
| **IMPLEMENT** | `override val E by clrEvent()` | a synthesized backing delegate field + real `add_E`/`remove_E`/`raise_E` on the emitted type |
| **RAISE** | `vm.E.invoke(sender, args)` | invoke the backing delegate via the `raise_E` accessor |

`ClrEvent<T>` is never instantiated, never a field, never a return value. §3 makes that a **type-level
guarantee** by turning it abstract; §4 gives each op's node shape and per-layer lowering.

---

## 3. Decision 1 — `ClrEvent<T>` is an ABSTRACT MARKER (user-directed)

`kotlin.clr.ClrEvent<T>` becomes an **abstract** type. It has no runtime instance and no runtime
meaning; it is a compile-time lowering tag. Two consequences, both wanted:

1. **`ClrEvent()` is unconstructable.** An abstract type forbids `new ClrEvent()`, so no code path
   can accidentally materialize the fiction. Any node that still carries a live `ClrEvent<T>` value
   past bir2cir is a bug the type system now surfaces, instead of leaking `kotlin.clr.ClrEvent` to
   ilemit (the #186 failure signature `kotlin.clr.ClrEvent.plusAssign() not found`).
2. **An interface event member becomes a member OBLIGATION.** The interface event is surfaced as an
   **abstract** `ClrEvent<T>` member. A Kotlin class directly implementing that interface now inherits
   an *unsatisfied abstract member* — the Kotlin frontend itself forces the author to write
   `override val E by clrEvent()`, converting "kotc silently emits an invalid type" (#187) into a
   normal, frontend-enforced override obligation with a real diagnostic when omitted.

**Per appearance:**

- **The `kotlin.clr.ClrEvent` type** (kotc `ClrTypeInjection.generateTopLevelClassLikeDeclaration`,
  `ClrTypeInjection.kt:620-624`): created with `ClassKind.CLASS` + `Modality.ABSTRACT`. It carries
  abstract `operator fun plusAssign(h: T)` / `minusAssign(h: T)` (consume), abstract
  `operator fun invoke(vararg args): R` (raise), and abstract `operator fun getValue(r: Any?, p: KProperty<*>): ClrEvent<T>`
  (so `by clrEvent()` typechecks under the delegate convention — see §5). None have bodies; none
  are ever executed.
- **The interface event member** (facadegen `Program.cs:755-763` → kotc
  `ClrTypeInjection.generateProperties`, `ClrTypeInjection.kt:770-780`): emitted **ABSTRACT** on the
  interface, reverting the current deliberate "emitted NON-abstract" choice at `ClrTypeInjection.kt:774`.
  facadegen already surfaces the member (`EventObj`, N6); no facadegen shape change is needed beyond
  keeping the interface event abstract — the abstractness is set at the kotc injection site. (facadegen
  owns *whether* the type is `ClrEvent<T>`; kotc owns the *modality* of the injected member.)
- **The fake-override elision** (kotc `BirEmitterDeclarations.isClrEventProperty`,
  `BirEmitterDeclarations.kt:414`): stays for the **base-class-satisfies** case (`class MyApp :
  Avalonia.Application` — the .NET base already implements the interface, fir2ir synthesizes a
  *concrete* `ClrEvent<T>` fake-override that satisfies the abstract slot → elide, no synthesis). It
  is **not** applied to a member the user explicitly implements with `by clrEvent()` (§5), nor to a
  delegation forwarder (§4.4). See §5 for the discriminator.

> Why abstract is safe for `class MyApp : Avalonia.Application`. The worry behind the current
> non-abstract choice was that an abstract interface event would impose an unsatisfiable obligation on
> a subclass whose *base class* supplies the accessors. It does not: fir2ir produces a **concrete**
> fake-override of the inherited `ClrEvent<T>` member (the base class's implementation), which
> satisfies the abstract interface slot exactly as a normal inherited concrete member satisfies an
> abstract one. Only a **direct** interface implementation with **no** intervening base-class
> implementation leaves the slot abstract — precisely the case that must synthesize.

---

## 4. Decision 2 — the three lowerings (BIR/CIR node shapes)

`ClrEvent<T>` never reaches ilemit; every op is consumed by kotc + bir2cir. Existing consume nodes
(unchanged): `clrEventGet{type,name,static,recv}` (kotc-dialect handle read),
`clrEventAdd`/`clrEventRemove{type,event,static,recv,handler}` (bir2cir-produced accessor call).

### 4.1 CONSUME — `x.E += h` (unchanged)

Already implemented and green (`cases/il-ifaceevent`). kotc emits `clrEventGet` for the handle read
and a plain `ClrEvent.plusAssign/minusAssign` call; bir2cir's `ClrEventOperatorBinding` rewrites the
pair to `clrEventAdd`/`clrEventRemove`; ilemit's `EmitClrEvent` links the ref.dll accessor and emits
the delegate-wrap + `callvirt add_E`. **One kotc widening** (for #186 use-site): produce `clrEventGet`
whenever the accessed member is a `ClrEvent<T>` property, **regardless of whether the receiver is a
.NET type or a Kotlin type that implements a .NET-event interface** (`BirEmitterCalls.kt:870`). Today
it only fires for a direct .NET member; a `ClrEvent` member reached through a Kotlin implementer never
becomes `clrEventGet` and the fiction leaks. bir2cir then binds the accessor on the *receiver's* type
(the synthesized forwarder in §4.4, or the interface slot).

### 4.2 IMPLEMENT — `override val E by clrEvent()` (field-like event)

The member obligation (§3) plus the `clrEvent()` marker (§5) triggers synthesis of a field-like event.
Layer split mirrors the consume path (kotc declares + wires overrides; bir2cir supplies the CLR
relation; ilemit emits pure CLR codegen):

- **kotc** (`ClrTypeInjection` / `BirEmitter*`): on the implementing type, emit
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
- **bir2cir** (new pass `ClrEventImplBinding.cs`, sibling of `ClrEventOperatorBinding.cs`): this is
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
  `plusAssign/minusAssign` handling at `:409-414`). The `ClrEvent<T>` value is consumed, never
  materialized.
- **bir2cir** (`ClrEventOperatorBinding` extended, or `ClrEventImplBinding`): bind `clrEventRaise` to
  a call of the declaring type's `raise_<E>` accessor: `clrRaiseCall{recv, accessor:"raise_<E>", args}`.
  **Guard:** raise is legal only for a **Kotlin-implemented** event (one that has a synthesized
  `raise_<E>`); `clrEventRaise` against a *consumed* foreign .NET event (no `raise_`) is a bir2cir
  hard error ("cannot raise a .NET event you do not declare") — the correct CLR rule (you raise on the
  declaring instance).
- **ilemit**: emit the call to `raise_<E>` (§4.2 emitted its body as `field?.Invoke(args)`).

### 4.4 DELEGATION — `class A : B by c` (#186)

A third IMPLEMENT flavor: forwarding, not field-like. kotc's class-delegation forwarder currently
**skips** the `ClrEvent<T>` member (`A.methods=["Ping"]`, no `add_E1`). Fix: for a delegated CLR
interface event, synthesize `add_<E>`/`remove_<E>` **forwarder** accessors whose bodies are
`clrEventAdd`/`clrEventRemove` on the delegate field `$$delegate_0` (`c`) — reusing the **consume**
lowering (§4.1). No backing field, no `raise_` (you raise on `c`). At the use site, `a.E += h` on the
Kotlin delegating class goes through the widened `clrEventGet` (§4.1) → `add_<E>` on `A` → forwards to
`c.E += h`. This is the same synthesis machinery as §4.2 with a forwarding body instead of a CAS body.

> #174 (delegation forwarder narrows `MutableList.iterator()` return to the read-only `Iterator`) is
> the same forwarder-synthesis site. Fold its fix in here: the delegation forwarder must carry the
> **overridden slot's** declared return type (the `Mutable*` iterator), not the delegate expression's
> read-only static type. The event forwarders and the covariant-return fix land in one bir2cir/kotc
> forwarder pass.

---

## 5. Decision 3 — the `clrEvent()` intrinsic contract

`clrEvent()` is the author-written marker meaning "**synthesize the field-like event impl here**". It
is a `kotlin.clr` top-level intrinsic (registered in `ClrTypeInjection.getTopLevelCallableIds`, beside
`byref`/`stackBuffer`):

```kotlin
package kotlin.clr
fun <T> clrEvent(): ClrEvent<T>          // pure kotc intrinsic; the returned value is never real
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
| `class ViewModelBase : INotifyPropertyChanged` | no (interface only) | **yes** (obligation forces it, §3) | **SYNTHESIZE** field-like (§4.2) |
| `class MyApp : Avalonia.Application` | **yes** (base class implements it) | no (nothing to write — concrete fake-override) | **ELIDE** (`isClrEventProperty`, unchanged) |
| `class A : B by c` | no — delegate `c` supplies at runtime | no (`by c`) | **SYNTHESIZE forwarder** (§4.4) |

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

C# permits raising an event **only from within the declaring type** (`E?.Invoke(...)` is legal only
inside `C`); the backing delegate field is private and there is no public raise. The user's MVVM
pattern raises `vm.PropertyChanged.invoke(vm, args)` from **outside** — inside `ViewModelProperty.setValue`,
a *different* type. DotKt **relaxes** the declaring-type-only rule: a `ClrEvent<T>` handle **exposes
raise**. Concretely (§4.2/§4.3), the synthesized `raise_<E>` accessor is emitted **public** (the
`.event`'s `.fire`), and `handle.invoke(...)` lowers to a call of it. So `vm.PropertyChanged.invoke(...)`
from any type is legal and simply calls `vm.raise_PropertyChanged(...)`.

This passes all three conditions of the acceptance test (`docs/dotkt-semantics.md`): **consistent** (a
`ClrEvent<T>` handle uniformly supports `+=`/`-=`/`invoke`), **documented** (§8d + this note), and
**convincingly explainable** (it is exactly the general-purpose event pattern .NET libraries hand-roll
with a `protected virtual void OnPropertyChanged(...)` raiser — DotKt makes the raiser a first-class
part of the event handle rather than boilerplate the author must write, which is what enables the
`ViewModelProperty` delegate pattern to raise a base class's event). Recorded as an interop-first
deviation in `docs/dotkt-semantics.md` §8d. (A *consumed* foreign event has no synthesized `raise_` and
`invoke` on it stays an error — you still cannot raise someone else's event; the deviation is scoped to
Kotlin-declared events.)

---

## 7. Decision 5 — the canonical conformance case (NUnit)

The user's `ViewModelBase`/`PersonViewModel` is the acceptance test, added as an NUnit fixture under
the migration (`tests/nunit-pilot/fixtures/ClrEventTests.kt`, the `@TestAttribute` + `ClassicAssert`
shape of `InterfaceDispatchTests.kt`). It exercises IMPLEMENT (`by clrEvent()`), the property-delegate
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
        val h: (Any?, PropertyChangedEventArgs) -> Unit = { _, e -> fired++; lastName = e.PropertyName }
        (vm as INotifyPropertyChanged).PropertyChanged += h          // CONSUME through the interface slot
        vm.name = "Jane Doe"
        ClassicAssert.AreEqual(1, fired)                             // raised exactly once
        ClassicAssert.AreEqual("name", lastName)                     // args carry the KProperty name
        vm.name = "Jane Doe"                                         // unchanged value -> no raise
        ClassicAssert.AreEqual(1, fired)
        (vm as INotifyPropertyChanged).PropertyChanged -= h
        vm.name = "Bob"
        ClassicAssert.AreEqual(1, fired)                             // unsubscribed -> no raise
    }
}
```

Pass criteria: the type **loads** (no `TypeLoadException`), **ilverify-clean** (`add_/remove_` satisfy
the `INotifyPropertyChanged` slots), subscribe/unsubscribe by delegate equality works, and the raise
carries `"name"`. A pure-CLR control (a C# `INotifyPropertyChanged` implementer subscribed from Kotlin)
already passes via the consume path and stays a control.

---

## 8. Decision 6 — sequenced implementation plan (0.9.7)

Each step is independently gate-runnable (`./scripts/verify-il.sh`; the NUnit fixture via the pilot).
Order minimizes cross-layer churn: land the type-level marker + node vocabulary first, then the two
synthesis flavors, then hardening.

| # | Layer | Work | Closes |
|---|-------|------|--------|
| **S0** | kotc + facadegen | `ClrEvent<T>` → **abstract** (add `invoke`, `getValue`); interface event member emitted **abstract** (revert `ClrTypeInjection.kt:774`); register `kotlin.clr.clrEvent` intrinsic. Adds the frontend obligation + the #187 missing-override diagnostic. | #187 (diagnostic) |
| **S1** | kotc | Recognize `by clrEvent()`; synthesize the backing field + `add_/remove_/raise_` decls with tagged bodies + `overrides` closure (§4.2). Widen `clrEventGet` to any `ClrEvent<T>` member read (§4.1). Emit `clrEventRaise` for `handle.invoke(...)` (§4.3). | #187 |
| **S2** | bir2cir | New `ClrEventImplBinding.cs`: resolve interface event → `EventHandlerType` + slot names off ref.dll; expand tagged accessor bodies → `clrEventAccessorImpl` CIR (CAS for field-like, forward for delegation); emit type-level `clrEventDecl`; bind `clrEventRaise` → `raise_<E>` call with the "no raise on consumed event" guard (§4.2/§4.3/§6). | #187 |
| **S3** | ilemit | `EmitClrEventAccessorImpl` (CAS loop §1 for add/remove, `field?.Invoke` for raise) + `MethodImpl` wiring to the interface slots + `.event` metadata from `clrEventDecl`. | #187 |
| **S4** | kotc + bir2cir | Class-delegation forwarder: synthesize forwarding `add_/remove_` for a delegated CLR interface event (§4.4); use-site `a.E += h` via widened `clrEventGet`. Carry the overridden slot's `Mutable*` return type in the forwarder (fold #174). | #186, #174 |
| **S5** | ilemit | Route **all** event emit (consume, implement, raise, `.event`) through the guarded `LinkClrMethod`/`RequireDispatch`/null-checked `GetEvent` family; legible `ilemit:` breadcrumb on a missing/value-type/constructed-generic event owner instead of an opaque NRE. | #113 |
| **S6** | tests + docs | Add `ClrEventTests.kt` (§7); record the raise deviation in `docs/dotkt-semantics.md` §8d; correct the stale "interface events not yet surfaced" note (§8d:897-902). Run `verify-il.sh` + the NUnit pilot; prune any FIXED XFAIL. | — |

Sequencing notes: **S0–S3 are the #187 spine** and must land together (a half-landed abstract marker
without synthesis would red the gate on every existing interface-event consumer). **S4** depends only
on S1's widened `clrEventGet` + S2's forwarder-body expansion. **S5** is independent hardening and can
land any time after S3 introduces the new emit sites. **S6** is the final gate + doc pass. Keep each new
concern in its own file (bir2cir `ClrEventImplBinding.cs`; ilemit `EmitClrEventAccessorImpl` in the
`Emitter.ClrInterop.cs` part) per the one-concern-per-file rule.

---

## 9. Invariants this respects

- **kotc reads no CLR metadata.** kotc carries the handler as a *Kotlin function type* and names
  override slots by *Kotlin identity* (`{owner FQN, member name, event-add/remove kind}`); bir2cir
  resolves the concrete `EventHandlerType` delegate + `add_E`/`remove_E` names off ref.dll. The
  `.NET event` vocabulary (`clrEventGet`/`clrEventRaise`/`clrEvent()`) is facadegen-injected CLR-only
  vocab kotc lowers as dialect — the sanctioned exception (like `byref`/`ClrRef<T>`), not a metadata read.
- **bir2cir owns the Kotlin↔CLR relation.** All delegate-type resolution, accessor-slot naming, CAS
  vs. forward body choice, and the raise binding live in bir2cir — the one layer that reads ref.dll.
- **ilemit knows no Kotlin.** It emits the CAS loop / `.event` / `MethodImpl` from a resolved CIR
  directive (`clrEventAccessorImpl` + `clrEventDecl`); it never sees `ClrEvent<T>` or `clrEvent()`.
- **`ClrEvent<T>` never reaches ilemit** — enforced now by its abstractness (§3): an unlowered handle
  is an unconstructable abstract value, not a silent `kotlin.clr.ClrEvent` leak.
