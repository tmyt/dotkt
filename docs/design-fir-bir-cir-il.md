# FIR -> BIR -> CIR -> IL

> **状態 (2026-06-30 見直し)**: パイプラインは facadegen / kotc / bir2cir / ilemit の **4 層**。`--native-cir` が目標モード（`--compat-bir` は撤去予定、その byte-for-byte BIR 不変条件は放棄）。Milestone 0 の emit crash ブロッカーは解消済み（stdlib は今ビルドできる）。本書の「CIR v1 互換スケルトン」系の記述は移行期のもので、下記で現状に合わせて更新済み。現行の出荷スコープは [docs/ship-tasks.md](ship-tasks.md) §0 が正。

This is the target split for the Kotlin/CLR backend.

For resume-oriented operational notes, see [bir2cir-handoff.md](bir2cir-handoff.md).

## Layer Contract

The pipeline is **four** layers with strict responsibilities (see [ship-tasks.md](ship-tasks.md) §0):

- **facadegen** — reads a CLR DLL and generates kotlin metadata for SYMBOL RESOLUTION (restores TopLevelFunction/inline/infix/operator from the Roundtrip Attributes; does the `System.Int32 -> kotlin.Int` type re-mapping). It does NOT resolve `@ClrIntrinsic` bindings.
- **kotc** (`toolchain/kotc`) — user source -> FIR -> **BIR**. Symbol resolution uses `stdlib.jar` (the stdlib space) + the facadegen-generated meta (the .NET space). kotc does NOT know CLR; BIR preserves Kotlin semantic structure and metadata (Kotlin-level types, a `kotlin.math.sqrt` call). It must not decide CLR projection, inline bodies, suspend state machines, or physical CLR member references.
- **bir2cir** (`toolchain/bir2cir`) — **BIR -> CIR**, the first CLR-semantic lowering stage. It consumes BIR plus referenced-assembly metadata and produces CLR-resolved CIR. The Kotlin<->CLR mapping lives HERE: `kotlin.Int -> System.Int32`, `@ClrIntrinsic` resolution, the math-map, primitive/array mapping, byref, suspend lowering, inline lowering.
- **ilemit** (`toolchain/ilemit`) — **CIR -> IL**, emits already-lowered CIR with minimal policy. It knows ONLY the CLR representation; it does NOT know Kotlin.

> **In-flight**: much of the `bir2cir` Kotlin<->CLR lowering (inline, type-substitute, `@ClrIntrinsic` resolution) still physically lives in kotc's `BirEmitter` today — see [ship-tasks.md](ship-tasks.md) §6 ("current violation"). The contract above is the target; the migration is not complete.

### Three-reference model

Each stage references a DISTINCT stdlib artifact ([design-clr-stdlib-ref-runtime-split.md](design-clr-stdlib-ref-runtime-split.md), and the per-artifact emission policy):

- **kotc** references `stdlib.jar` (the DotKt frontend metadata jar).
- **bir2cir** references `DotKt.Private.Stdlib.dll` (the **ref.dll** — pure `kotlin.*`, keeps ALL attributes incl. `@ClrIntrinsic`). This is the SOURCE of `@ClrIntrinsic`.
- **ilemit** references `DotKt.Stdlib.dll` (the **rt.dll** — runtime impls, metadata stripped).

`@ClrIntrinsic` is the LABEL the ref.dll carries; **bir2cir consumes it** when emitting CIR (producing a plain BCL call). `@ClrIntrinsic` never appears in CIR and never reaches ilemit.

## CIR

`toolchain/bir2cir` accepts BIR JSON files and `--ref <dll>` inputs, validates the input, and writes `.cir.json`. The driver is structured as a real compiler stage:

1. `LoadBirFiles`
2. `BuildReferenceMetadataIndex`
3. `TransformFiles`
4. `WriteCirFiles`

`--native-cir` is the **target** output mode: it emits a CIR envelope (`cirVersion`, referenced-assembly identities, resolved call/type sites, and `cirDraft.executableCir`) which `ilemit` consumes directly. `--compat-bir` is the legacy mode that emits BIR-compatible JSON for the old `ilemit` path; it is being removed and its byte-for-byte BIR invariant is abandoned ([[break-for-elegance]]). The "Milestone 0" flip-to-native-cir was previously blocked by a DotKt.Stdlib emit crash; that blocker is **resolved** (the stdlib builds now), so the remaining work is the default flip + `--compat-bir` deletion.

The first non-identity pass is `SuspendShapeAnalyzer`. It does not rewrite bodies yet; it identifies BIR suspend functions and records:

- result type
- `coSuspend` await count
- `coSuspendIntrinsic` await count
- `coReturn` count
- CPS field count

This analysis is emitted in `--native-cir` as `analysis.suspendFunctions` and printed as an aggregate in the driver log. It is the insertion point for the future suspend-to-async transform.

`--native-cir` also emits `cirDraft.asyncFunctions`. This is not yet executable CIR, but it maps current coroutine BIR steps into the intended async vocabulary:

- `coSuspend` -> `clr.await`
- `coSuspendIntrinsic` -> `clr.awaitIntrinsic`
- `coReturn` -> `return`
- `var` in coroutine steps -> `clr.asyncLocalInit`
- `exprStmt` / `setLocal` -> `clr.exprStmt` / `clr.setLocal`
- `coLabel` / `coGoto` / `coCondGoto` -> `clr.label` / `clr.goto` / `clr.brfalse`
- `coTryBegin` / `coCatchBegin` / `coTryEnd` -> `clr.asyncTryBegin` / `clr.asyncCatchBegin` / `clr.asyncTryEnd`

Each draft async function also carries `loweringStatus`:

- `linear`: only local initialization, awaits, and return.
- `control-flow`: labels or branches are present.
- `try`: async try/catch/finally markers are present.
- `unsupported`: a step kind is not yet represented in the draft; `unknownSteps` lists the kinds.

The draft lets the async shape evolve independently from `ilemit`. (The legacy `--compat-bir` mode is being removed; its byte-for-byte BIR invariant no longer holds.)

## BIR -> CIR Lowering Order

BIR -> CIR is split into ordered lowering phases. The order matters because inline expansion, compiler intrinsics, explicit CLR projection, ordinary Kotlin calls, and async lowering are different mechanisms.

1. **Inline Lowering**
   - Expands inline function bodies at call sites before later physical lowering.
   - Substitutes reified type parameters with the call-site type arguments.
   - Preserves Kotlin-level BIR shape after expansion so the inserted body still passes through the same later phases as surrounding code.
   - Handles lambda placement, `noinline` / `crossinline`, and non-local return representation before basic/CLR lowering sees the body.
2. **Suspend Shape Normalization**
   - Normalizes suspend/coroutine-shaped BIR after inline expansion.
   - Keeps suspend semantics visible, but regularizes the shape so later expression lowering can run inside suspend bodies.
   - Records await/return/control-flow structure for the later async/await phase.
3. **Basic Type Lowering**
   - Lowers Kotlin built-in physical operations before projection lookup.
   - Handles primitive/value/string/array/nullability/type-test/runtime-type/enum basics that are necessary to represent Kotlin built-ins on CLR.
   - Does not consult `@ClrIntrinsic` metadata and is not a fallback lookup mechanism.
4. **CLR Projection**
   - Resolves explicit `@ClrIntrinsic` metadata from both the current source set and referenced assemblies.
   - Applies only to the declaration that carries the annotation.
   - Never infers member projection from an owner type's `@ClrIntrinsic`, CLR name equality, or a missing method body.
5. **Ordinary Call Lowering**
   - Emits ordinary Kotlin calls that were not consumed by Basic Type Lowering or CLR Projection.
   - Emits members declared on `@ClrIntrinsic` owner classes but not themselves annotated with `@ClrIntrinsic` as static forwarders/helpers with a physical receiver argument.
   - Leaves stdlib API behavior owned by the compiled stdlib rather than a compiler intrinsic catalog.
6. **Async/Await Lowering**
   - Lowers the normalized suspend shape to CLR async/await CIR.
   - Introduces async functions/lambdas, awaits, task result types, and await-surviving locals.
   - Runs after ordinary expression/member lowering so suspend bodies contain CLR-shaped operations rather than Kotlin symbol calls.

This means primitive/string basics are not "implicit `@ClrIntrinsic` lookup" exceptions. They are lowered by Basic Type Lowering before CLR Projection runs.

## Reference Metadata Index

`bir2cir` builds projection input from referenced assemblies and from projection metadata carried by the current BIR module. Current-module `@ClrIntrinsic` metadata is valid only as explicit declaration-level projection, not as implicit owner-based member lookup.

The reference index currently records a small DotKt metadata surface:

- `[KotlinFileClass]` facade types
- public constructors, fields, and methods on referenced types
- `[KotlinFunction]` flags
- whether a method has `[KotlinInline]`
- diagnostics for references that cannot be fully inspected

This data is emitted in `--native-cir` under `references[].dotkt`. It is the lookup source for referenced projection/type/inline lowering.

The current `resolutionDraft` implementation is still reference-oriented: it probes `kotlin-symbol` call sites against referenced metadata and reports `resolved-in-reference`, `ambiguous-in-references`, or `unresolved-in-references`. The design target is broader: explicit `@ClrIntrinsic` projection metadata from the current source set should participate in the same projection-resolution phase as referenced metadata, while ordinary non-projected current-module Kotlin calls remain ordinary Kotlin calls.

`cirDraft.resolvedCalls` is the first lowering-facing view over that data. For uniquely resolved reference symbols it emits draft CLR operations:

- `new` -> `clr.newobj` with `clr.constructorRef`
- `callStatic` / `callInstance` -> `clr.call` with `clr.methodRef`
- field reads/writes -> `clr.ldfld` / `clr.ldsfld` / `clr.stfld` with `clr.fieldRef`

This is still native-CIR-only and does not rewrite compatibility output. Its purpose is to make physical member references explicit before `ilemit` learns to consume native CIR.

`cirDraft.loweredBir` is an intermediate native-only tree used while the real CIR schema is still forming. It clones the original BIR payload and replaces uniquely resolved reference call/type sites at their JSON paths with draft CLR nodes and `clr.typeRef` objects. Unresolved and ambiguous sites are left in their original BIR form.

`cirDraft.executableCir` is the executable native-CIR payload for the current transition. It keeps the BIR file/method wrapper shape for now, but expression lowering uses native CLR nodes with `memberRef` metadata instead of Kotlin-symbol call nodes:

- `new` -> `clr.newobj`
- `callStatic` / `callInstance` -> `clr.call`
- `field` / `staticField` -> `clr.ldfld` / `clr.ldsfld`
- `setFieldExpr` / `staticFieldSet` -> `clr.stfld` / `clr.stsfld`
- `conv` -> `clr.conv`
- `isinst` / `cast` / `isinstRef` -> `clr.isinst` / `clr.castclass` / `clr.isinst.ref`
- `safeCastValue` -> `clr.safeCast.value`
- `nullableNull` / `nullableWrap` / `nullableHasValue` / `nullableValue` -> `clr.nullable.null` / `clr.nullable.wrap` / `clr.nullable.hasValue` / `clr.nullable.value`
- `classRef` / `getType` -> `clr.typeof` / `clr.getType`
- `enumValue` / `enumOrdinal` / `enumValues` / `enumParse` -> `clr.enum.value` / `clr.enum.ordinal` / `clr.enum.values` / `clr.enum.parse`
- `objEq` / `objMethod` -> `clr.obj.eq` / `clr.obj.method`

Reference assembly type sites are normalized to `clr:<type>` strings so locals, parameters, and returns resolve as CLR types instead of same-assembly Kotlin types. `ilemit` reads `cirDraft.executableCir` first and only falls back to the older `cirDraft.ilemitCompatBir` transition payload if a native-CIR file lacks `executableCir`.

Generic method calls keep BIR `typeArgs` on the native `clr.call` node. Method generic parameters in referenced metadata are encoded as `gp:<name>` and treated as type-match wildcards during reference resolution; `ilemit` then resolves the method definition and applies `MakeGenericMethod` from the preserved native `typeArgs`.

Constructed generic owners are preserved on native nodes with `ownerType` such as `clrg:GenericBox[int]`. The `memberRef.owner` still names the open metadata owner, while `ownerType` tells `ilemit` which constructed CLR type to emit against.

Existing physical BIR property nodes are also normalized when reference metadata is available:

- `clrPropGet` -> native `clr.call` to the getter, or `clr.ldfld` for public fields surfaced as properties.
- `clrPropSet` -> native `clr.call` to the setter, or `clr.stfld` for public fields surfaced as properties.

Existing physical BIR event nodes are normalized as accessor calls:

- `clrEventAdd` -> native `clr.call` to `add_<event>`.
- `clrEventRemove` -> native `clr.call` to `remove_<event>`.

### CLR Event Interop

CLR events are a valid compiler-intrinsic surface. They are not ordinary Kotlin properties and should not be modeled as
stdlib collection-like objects. A CLR event is metadata that names an event plus its add/remove accessor methods and
handler delegate type. Kotlin needs a source-level endpoint that can participate in `+=` / `-=` resolution, while CIR
must ultimately call the CLR accessors.

Imported CLR events should be exposed to Kotlin as an event endpoint type, conceptually:

```kotlin
val Click: CLREvent<(sender: Any?, e: ClickEventArgs) -> Unit>
```

`CLREvent<T>` is a compiler-provided interop protocol, not a required runtime allocation. Its only source-level
contract is:

```kotlin
operator fun CLREvent<T>.plusAssign(handler: T)
operator fun CLREvent<T>.minusAssign(handler: T)
```

The frontend may synthesize this endpoint from reflected CLR event metadata, and user source can then write:

```kotlin
button.Click += { sender, e -> ... }
button.Click -= savedHandler
```

The current implementation exposes the same capability through synthesized `add_<Event>` / `remove_<Event>` methods
and rewrites those calls to `clrEventAdd` / `clrEventRemove`. That is an implementation-compatible v1 surface. The
preferred public Kotlin surface is the `CLREvent<T>` endpoint with `plusAssign` / `minusAssign`; the add/remove method
names should remain a lowering detail rather than the ergonomic API.

The lowering rule is explicit and metadata-driven:

```text
CLR metadata
  event Click : ClickEventHandler
  add_Click(ClickEventHandler)
  remove_Click(ClickEventHandler)

Kotlin surface
  Click: CLREvent<(sender: Any?, e: ClickEventArgs) -> Unit>

BIR
  event add/remove operation with receiver, event identity, and handler expression

CIR
  clr.call add_Click(receiver, handler-as-ClickEventHandler)
  clr.call remove_Click(receiver, handler-as-ClickEventHandler)
```

The handler is bound to the event's exact CLR delegate type, not to a generic `Func` / `Action` merely because the
Kotlin surface uses a function type. A handler literal may be emitted directly as that delegate. A stored function value
may need a stable wrapper so delegate identity works for `-=`. This is the same delegate-binding rule needed for ordinary
delegate parameters, but event unsubscribe makes identity observable.

Producing CLR events from Kotlin should be symmetrical. A Kotlin declaration that is explicitly intended to be a CLR
event should emit real CLR event metadata:

- a delegate-typed backing field or equivalent storage;
- specialname `add_<Event>` and `remove_<Event>` methods;
- an event map entry that points to those accessors;
- Kotlin metadata sufficient for a DotKt consumer to restore the `CLREvent<T>` endpoint.

The source declaration must be treated as an event declaration, not as a Kotlin property that happens to have an event
type. Kotlin syntax does not have a native `event` member, so kotc can use an annotated compiler-intrinsic declaration
shape:

```kotlin
class ViewModel : System.ComponentModel.INotifyPropertyChanged {
    @ClrEvent
    public val PropertyChanged: CLREvent<System.ComponentModel.PropertyChangedEventHandler>

    protected fun onPropertyChanged(name: String) {
        PropertyChanged.raise(this, System.ComponentModel.PropertyChangedEventArgs(name))
    }
}
```

This declaration is visible to the Kotlin frontend as an event endpoint so other code can type-check subscription and
unsubscription. It must not emit a public CLR property named `PropertyChanged`, and it must not emit a public field named
`PropertyChanged`. The public CLR surface is only:

```csharp
public event PropertyChangedEventHandler PropertyChanged;
```

The backing delegate storage is private and implementation-defined. The add/remove accessors update that storage through
the normal CLR delegate-combine/delegate-remove pattern, preferably with an atomic compare-exchange loop when thread-safe
event subscription semantics are required.

Event subscription and event raising must be different capabilities:

- `CLREvent<T>` is the public endpoint. It supports only `plusAssign` and `minusAssign`.
- `CLREvent<T>` must not expose `raise`, `invoke`, or the backing delegate value.
- event raising is exposed to Kotlin type checking through a private synthetic callable tied to the declaration site.

This matches CLR rules: external code can subscribe and unsubscribe to a public event, but it cannot read or invoke the
event's delegate. Even derived classes cannot directly raise a base class event unless the base class exposes a protected
raiser method. Kotlin should model that by requiring the declaring class to provide an ordinary protected method such as
`onPropertyChanged` when subclasses need to trigger the event.

The frontend should not accept an unresolved magic name called `raise`. Instead, a `@ClrEvent` declaration synthesizes a
private FIR-only member extension inside the declaring class:

```kotlin
private fun CLREvent<System.ComponentModel.PropertyChangedEventHandler>.raise(
    sender: Any?,
    e: System.ComponentModel.PropertyChangedEventArgs
): Unit
```

This synthetic callable is not emitted as a CLR method. It exists so ordinary Kotlin name lookup, overload resolution,
visibility, and argument checking can validate the raising operation. Since it is private to the declaring class, outside
code can still see `PropertyChanged += handler`, but cannot resolve `PropertyChanged.raise(...)`.

The synthetic `raise` callable is only valid when all of these are true:

- the event declaration is in the current declaring type;
- the caller is inside that declaring type's implementation, not merely an arbitrary consumer of the public endpoint;
- the argument list matches the event's exact CLR delegate `Invoke` signature.

BIR emission marks calls to that synthetic callable as event-raise operations. Lowering for event raise loads the private
backing delegate, null-checks it, and invokes the exact event delegate type. It does not call the public add/remove
accessors and it is not available through reflected or imported `CLREvent<T>` endpoints.

Kotlin interface delegation adds a second valid event implementation shape:

```kotlin
class ViewModel(
    private val propertyChanged: PropertyChangedImpl = PropertyChangedImpl()
) : System.ComponentModel.INotifyPropertyChanged by propertyChanged {
    var name by ViewModelProperty<String>("")
}
```

The outer class still implements `INotifyPropertyChanged`, so its CLR surface must still satisfy the interface event.
kotc should emit `ViewModel.PropertyChanged` as a real CLR event on `ViewModel`, but its add/remove accessors may forward
to the delegated implementation:

```text
ViewModel.add_PropertyChanged(handler)
  -> this.propertyChanged.add_PropertyChanged(handler)

ViewModel.remove_PropertyChanged(handler)
  -> this.propertyChanged.remove_PropertyChanged(handler)
```

In this form, the outer class owns the public interface implementation, while `PropertyChangedImpl` owns the backing
delegate storage and the event-raise capability. The forwarded event on the outer class must not expose a backing
delegate or `raise` capability for `ViewModel.PropertyChanged`; it only implements subscription compatibility for CLR and
Kotlin consumers.

Property delegates that are meant to participate in change notification should communicate with the delegated event
source through an explicit protocol, not by invoking the public event endpoint. A property delegate such as
`ViewModelProperty<T>` can use Kotlin's normal delegated-property receiver (`thisRef`) plus a compiler-recognized or
ordinary interface implemented by `PropertyChangedImpl`:

```kotlin
interface PropertyChangedSource {
    fun raisePropertyChanged(sender: Any?, name: String)
}
```

The compiler may synthesize a private accessor from `ViewModel` to the interface-delegation field when the delegated
property lowering needs the event source:

```text
ViewModel.name.set(value)
  -> ViewModelProperty.setValue(this, ::name, value, this.propertyChanged)
  -> propertyChanged.raisePropertyChanged(this, "name")
```

The exact property-delegate protocol can evolve, but the boundary rule is fixed:

- interface delegation may forward CLR event add/remove from the outer type to the delegate object;
- event raising remains owned by the object that owns the backing delegate storage;
- delegated properties must notify through an explicit source/capability, not by calling `CLREvent<T>.raise`;
- the emitted CLR surface of the outer type remains a normal public event, so C# and reflection see the expected
  `INotifyPropertyChanged.PropertyChanged` implementation.

If the declaration names an existing CLR delegate type, that delegate type is used. If it starts from a Kotlin function
type, kotc may synthesize a delegate using the same delegate ABI as function types, including the wide `KFunc` / `KAction`
path when the handler arity exceeds BCL `Func` / `Action`. The resulting CLR event must still expose a concrete delegate
type in metadata, because CLR events cannot be typed as a structural Kotlin function.

This intrinsic belongs in the interop/projection part of the pipeline:

- facadegen / FIR injection restore CLR event metadata as an event endpoint;
- Kotlin frontend resolution turns `+=` / `-=` into the endpoint operations;
- BIR records event add/remove as a distinct operation or an equivalent compiler-intrinsic call;
- CLR Projection lowers it to the add/remove accessor call;
- Ordinary Call Lowering should not infer events from method names alone.

Physical BIR type-operation nodes are normalized without reference metadata. Numeric conversions become `clr.conv`; reference/value casts and type tests become `clr.castclass`, `clr.isinst`, and `clr.isinst.ref`. Nullable-producing value safe casts become `clr.safeCast.value`, which emits the boxed value test plus `Nullable<T>` construction. Value-nullable helpers become `clr.nullable.*` nodes for empty `Nullable<T>`, wrapping, `HasValue`, and `Value`. Type reflection helpers become `clr.typeof` for `T::class` and `clr.getType` for runtime `x::class`. Enum helpers become `clr.enum.*` nodes for literal values, ordinal conversion, values arrays, and parse/valueOf. Object-identity helpers (the `Any` operations `equals` / `hashCode` / `toString` and Kotlin `==`) become `clr.obj.eq` for null-safe equality and `clr.obj.method` for the boxed `System.Object` virtual call; both are Basic Lowering and do not consult reference metadata.

Overload resolution is intentionally conservative at this stage. The resolver records call-site argument count plus BIR `sig` / expression type hints, then filters referenced constructors/methods by parameter count and by exact normalized type matches when all argument types are known. The normalizer understands primitive aliases, `array:`, `func:`, `nullable:`, `byref:`, `gp:`, and constructed `clrg:` encodings. Full lowered generic/member signature matching should be completed before this output becomes executable CIR.

## Companion Object ABI

Kotlin companion objects have two different CLR shapes depending on whether the companion object itself must exist as a
runtime value.

Most companions are only static-member namespaces:

```kotlin
class Box {
    companion object {
        fun parse(s: String): Box = ...
        val empty: Box = ...
    }
}
```

These should be flattened to static members on the parent CLR type:

```text
Box.parse(string)
Box.empty
```

This shape is important for interop and for referenced-assembly consumption because `facadegen` / FIR injection cannot
reliably reconstruct full Kotlin companion-object semantics from CLR metadata. Consumers should not need a real
`Box.Companion` declaration merely to call static companion members.

However, a companion object with a meaningful supertype is not just a static namespace. It is a singleton value:

```kotlin
abstract class Random {
    companion object Default : Random()
}
```

Kotlin source uses that singleton as a value:

```kotlin
random(Random)
```

For this case, flattening all companion semantics into parent static methods is insufficient. kotc should emit a concrete
singleton field or property on the parent CLR type whose type is the companion's effective value type, normally the first
non-`Any` class supertype or the emitted companion class when necessary:

```text
Random.Default : Random
```

The exact CLR member name may be `Default` for a named companion or a compiler-chosen stable name for an unnamed
companion, but it must not be modeled as `Random.INSTANCE`. `INSTANCE` belongs to a real `object Foo` singleton type;
`Random` is the parent class, not the companion object type.

The lowering rule is:

- companion with no meaningful supertype: flatten members to parent static fields/methods/properties;
- companion with a meaningful supertype or otherwise used as a value: emit a parent static singleton field/property for
  the companion value;
- accesses to companion members may still be flattened to parent static members where that preserves semantics;
- `IrGetObjectValue` for a companion must load the companion singleton field/property, not `<parent>.INSTANCE`.

Referenced DotKt assemblies should expose enough metadata for consumers to restore this surface without reconstructing a
full companion declaration. For static-member companions, `facadegen` can report ordinary static members on the parent
type. For value companions, it must also report the parent static singleton member so BIR -> CIR can lower object-value
uses such as `Random` to the correct CLR field/property.

## Function Delegate ABI

Kotlin function types are represented in BIR and CIR with the structural encoding:

```text
func:<return-type>:<arg1>,<arg2>,...
```

The CLR ABI uses existing BCL delegates while the function type fits:

- `func:void:` maps to `System.Action`.
- `func:void:<args>` maps to `System.Action<T1,...,TN>` for supported BCL arities.
- non-`void` `func:<R>:<args>` maps to `System.Func<T1,...,TN,R>` for supported BCL arities.

When the Kotlin function arity exceeds the BCL `Func` / `Action` family, `ilemit` synthesizes a public module-local delegate instead of truncating or encoding the signature indirectly. This covers the `Func\`18`-style case: C# and the CLR are satisfied as long as a delegate type with the required `Invoke` signature exists.

Synthetic delegates are emitted under `DotKt.Runtime.CompilerServices`:

- ``KFunc`N`` for value-returning functions. The last generic parameter is named `TResult`.
- ``KAction`N`` for `Unit`/`void` returning functions.

Each synthetic delegate:

- derives from `System.MulticastDelegate`;
- has the standard runtime `(object, IntPtr)` constructor;
- has a runtime-managed `Invoke` method whose parameters match the Kotlin function arguments;
- is marked `[CompilerGenerated]`;
- carries DotKt metadata so `facadegen` and `bir2cir` read it back as the original structural `func:` type instead of exposing the synthetic CLR name as source-level ABI.

This keeps Kotlin metadata structural and stable while still producing ordinary CLR delegate types for IL emission, overload resolution, event binding, reflection, and C# interop. The synthetic delegate name is an implementation detail of the generated assembly; cross-module compiler stages should prefer the `func:` metadata projection whenever it is available.

## Kotlin Modifier Roundtrip

Some Kotlin callable modifiers affect source-level resolution but do not have a direct CLR metadata concept. kotc must round-trip these modifiers through DotKt metadata so a referenced DotKt assembly can be consumed as Kotlin again.

The implemented roundtrip carriers are:

- `[KotlinFunction(flags)]`: callable modifiers with no direct CLR representation.
- `[KotlinFileClass]`: marks a file facade so static methods restore as top-level Kotlin functions.
- `[KotlinInline(body)]`: carries BIR for inline-with-lambda functions that must be spliced across module boundaries.
- `[KotlinReadOnly]`: marks a public backing field whose Kotlin property should restore as `val`.
- `.NET` nullable-reference metadata, `[Nullable]` / `[NullableContext]`: carries reference-type nullability for both Kotlin and C# consumers.

`[KotlinFunction(flags)]` currently carries:

- `Infix`
- `Operator`
- `Suspend`

These function flags are not CLR call targets by themselves. They restore Kotlin frontend semantics when `facadegen` / FIR injection reconstructs callable declarations from a referenced assembly.

### Operator Functions

`operator` is a Kotlin source-level convention, but many Kotlin operator names have a direct CLR operator ABI equivalent. kotc should use the CLR operator ABI when it can do so without changing Kotlin semantics.

```kotlin
class Vec {
    operator fun plus(other: Vec): Vec
}
```

For a user-defined Kotlin type, this should be emitted as a CLR operator method:

```text
public static specialname Vec op_Addition(Vec self, Vec other)
```

The Kotlin receiver becomes the first CLR argument. The method should still carry the existing DotKt metadata so Kotlin re-consumption restores the source-level modifier. There is no separate roundtrip attribute for the original Kotlin operator name; for CLR operator methods the Kotlin name is recovered from the `op_*` method name by the standard operator map:

```kotlin
operator fun plus(other: Vec): Vec
```

That restoration is what lets the consumer write:

```kotlin
a + b
```

instead of only:

```kotlin
a.plus(b)
```

The roundtrip rule is:

```text
BIR producer
  method carries "operator": true

IL producer
  CLR op_* method when the Kotlin operator has a CLR operator equivalent
  + [KotlinFunction(Operator)]

metadata reader / facadegen
  maps CLR op_* to the Kotlin operator name
  restores operator status from [KotlinFunction(Operator)]

consumer frontend
  resolves operator syntax to that restored callable

BIR -> CIR
  lowers the resolved call like any other call
```

For Kotlin operators that do not have a CLR operator ABI equivalent, kotc emits an ordinary method and relies on `[KotlinFunction(Operator)]` for Kotlin roundtrip. A method named `plus` without Kotlin operator metadata is just a method named `plus`; Kotlin operator status must not be rediscovered from an ordinary method name alone. CLR `specialname` `op_*` methods are different: the CLR ABI itself identifies them as operators, and the metadata reader maps them through the standard operator-name table.

Kotlin operators with direct CLR operator equivalents include:

- `unaryPlus` -> `op_UnaryPlus`
- `unaryMinus` -> `op_UnaryNegation`
- `inc` -> `op_Increment`
- `dec` -> `op_Decrement`
- `plus` -> `op_Addition`
- `minus` -> `op_Subtraction`
- `times` -> `op_Multiply`
- `div` -> `op_Division`
- `rem` -> `op_Modulus`
- equality operators, when explicitly emitted as CLR equality, -> `op_Equality` / `op_Inequality`
- ordered comparison operators, when explicitly emitted as CLR relational operators, -> `op_LessThan` / `op_GreaterThan` / `op_LessThanOrEqual` / `op_GreaterThanOrEqual`

Kotlin operators without a direct CLR operator method equivalent stay as Kotlin ABI methods plus metadata:

- `get` / `set`: CLR indexer property shape when projected to a CLR indexer, otherwise ordinary Kotlin methods.
- `invoke`: ordinary `Invoke`-named method or another explicit projection, not a CLR operator.
- `contains`: ordinary method; CLR has no `in` operator method.
- `iterator`: ordinary protocol method.
- `getValue` / `setValue`: Kotlin delegate protocol methods.

`compareTo` needs care. Kotlin relational syntax resolves through `compareTo`, while CLR relational operators are separate boolean-returning `op_LessThan` / `op_GreaterThan` methods. kotc should not silently turn every `compareTo` into the full CLR relational operator set. Emitting those operators is only valid when the declaration or a dedicated lowering explicitly requests that CLR ABI surface.

The existing roundtrip carrier set remains sufficient:

- `[KotlinFunction(Operator)]` says the restored callable is a Kotlin operator.
- The CLR method name `op_Addition`, `op_UnaryNegation`, etc. determines the restored Kotlin convention name when the method is a CLR operator.
- Ordinary Kotlin operators without CLR ABI equivalents keep their Kotlin method name and use the same `[KotlinFunction(Operator)]` flag.
- No new roundtrip attribute should be introduced just to carry `plus` / `minus` names for CLR operator methods.

### CLR Operators

.NET operator overloads from external CLR types are the same ABI surface: static CLR methods named `op_*`, such as:

- `op_Addition`
- `op_Subtraction`
- `op_Equality`
- `op_Implicit`
- `op_Explicit`

When a Kotlin declaration projects to such an external member, the projection must be explicit:

```kotlin
@ClrIntrinsic("op_Addition")
operator fun plus(other: Vec2): Vec2
```

The Kotlin declaration is modeled as an instance-style operator for source ergonomics, but the CLR target is static. CIR lowering must therefore prepend the receiver as the first CLR argument:

```text
Kotlin source
  a + b

resolved Kotlin callable
  @ClrIntrinsic("op_Addition") operator fun Vec2.plus(other: Vec2): Vec2

CIR
  clr.call static Vec2.op_Addition(a, b)
```

This is explicit `@ClrIntrinsic` projection, not operator-name guessing. A Kotlin `operator fun plus` on an `@ClrIntrinsic` owner does not automatically become `op_Addition`; the member must carry the projection unless it is a user-defined Kotlin operator that kotc itself is emitting as a CLR operator method.

### Indexers

CLR indexers are also explicit projection surface. A reflected CLR indexer may be restored as Kotlin `operator fun get` / `operator fun set`, but that restoration is metadata-driven by the reflected property/indexer shape, not by name fallback. Once restored, Kotlin frontend resolution turns `x[i]` into calls to those operator functions, and BIR -> CIR lowers those calls through the normal projection path.

### Top-level and Extension Operators

Top-level and extension operator functions round-trip the same way:

- the produced method carries `[KotlinFunction(Operator)]`;
- file-facade metadata restores top-level ownership;
- extension receiver metadata restores the receiver position;
- the consumer frontend resolves operator syntax against the restored symbol.

BIR -> CIR should not need a special "operator lookup" pass. By the time BIR exists, Kotlin frontend resolution has already selected a callable. BIR -> CIR only needs to preserve the resolved call and apply Basic Type Lowering or explicit CLR Projection according to the selected declaration.

## Built-in Types vs Stdlib APIs

Kotlin built-in type representation is a compiler responsibility. Stdlib API behavior is a stdlib responsibility.

BIR may mention source-level Kotlin types such as:

- `kotlin.Int`
- `kotlin.Long`
- `kotlin.Boolean`
- `kotlin.Char`
- `kotlin.String`
- `kotlin.Unit`
- primitive arrays such as `kotlin.IntArray`
- nullable value types such as `kotlin.Int?`

BIR -> CIR must lower these to CLR physical representations without consulting `DotKt.Stdlib.dll` as a type provider:

- `kotlin.Int` -> `System.Int32`
- `kotlin.Long` -> `System.Int64`
- `kotlin.Boolean` -> `System.Boolean`
- `kotlin.Char` -> `System.Char`
- `kotlin.String` -> `System.String`
- `kotlin.Unit` -> `void` in return position, and a real value representation only where a value is required
- primitive arrays -> CLR primitive arrays such as `System.Int32[]`
- nullable value types -> `System.Nullable<T>`

This direct mapping is required even when the stdlib assembly also exposes Kotlin symbols and metadata for these types. The frontend needs those symbols for source compatibility and member resolution, but CIR must use the CLR physical type so that literals, boxing, arrays, overload resolution, reflection, C# interop, and BCL calls have the expected runtime identity. For example, a Kotlin string literal must become a `System.String`, not an instance of a wrapper class from `DotKt.Stdlib`.

This does not mean the compiler owns the behavior of the Kotlin stdlib. A member or extension API on a built-in type is still provided by compiled stdlib IL unless it is one of the minimal operations required to express the physical type itself. These minimal operations are handled by Basic Lowering before `@ClrIntrinsic` projection lookup.

Basic Lowering handles built-in physical operations such as:

- primitive arithmetic, comparison, bitwise operations, and numeric conversions;
- boxing and unboxing;
- value-nullable construction and access;
- primitive array construction/access/length;
- string literal, length, indexing, and concatenation when represented as primitive CLR string operations;
- `Any`/object identity operations needed for `equals`, `hashCode`, and `toString`;
- casts, type tests, `T::class`, runtime `x::class`, and enum primitive helpers.

Stdlib APIs must not be reimplemented as compiler intrinsics merely because their receiver is a built-in physical type. Examples that belong in stdlib IL are:

- `String.format`;
- text helpers such as `replace`, `substring`, `trim`, and case conversion;
- collection helpers such as `map`, `filter`, `firstOrNull`, and `joinToString`;
- range/progression helper APIs;
- platform convenience wrappers over BCL methods.

For such APIs, user code should call the compiled stdlib method. The stdlib method may then be implemented in Kotlin, or projected to a BCL member through explicit CLR actuals / `@ClrIntrinsic` metadata on the declaration itself. Conceptually:

```kotlin
package kotlin.text

actual fun String.format(vararg args: Any?): String =
    System.String.Format(this, args)
```

or, where the projection model is expressive enough:

```kotlin
@ClrIntrinsic("System.String.Format")
actual fun String.format(vararg args: Any?): String
```

The exact projection form may need receiver and vararg metadata, because an extension receiver and `vararg Any?` do not always map to a CLR target by name alone. The important rule is that user-code lowering must not special-case `String.format` directly into `System.String.Format`. Instead:

```text
user source call
  "%s".format(x)

BIR
  call kotlin.text.format(receiver = "%s", args = [x])

CIR
  call compiled DotKt.Stdlib method, or a CLR member resolved from that method's explicit projection metadata

IL
  ordinary CLR call
```

`@ClrIntrinsic` projection metadata is active wherever it is available: declarations in the current source set and declarations loaded from referenced assemblies are both valid projection sources. The lookup is explicit only. A class-level `@ClrIntrinsic` controls the physical type of that class. A member-level `@ClrIntrinsic` controls only that member. No member projection is inferred from an owner `@ClrIntrinsic`, CLR name equality, or a missing body.

This avoids a bootstrap cycle: built-in type representation does not require a stdlib reference, while stdlib API behavior is still provided by the stdlib assembly. During stdlib compilation, explicit `@ClrIntrinsic` member declarations may project to BCL members, while unannotated members on `@ClrIntrinsic` owner classes are emitted as static stdlib forwarders/helpers.

### `@ClrIntrinsic` Class vs Member Projection

Class-level `@ClrIntrinsic` and member-level `@ClrIntrinsic` have different meanings.

A class-level annotation binds the Kotlin type's physical receiver/storage representation:

```kotlin
@ClrIntrinsic("System.String")
actual class String
```

This means values of that Kotlin type are represented as `System.String` in CIR/IL. It does not automatically mean every Kotlin member declared inside the class maps to a same-named CLR instance member.

A member-level annotation binds that specific Kotlin member to a CLR member:

```kotlin
@ClrIntrinsic("System.Text.StringBuilder")
actual class StringBuilder {
    @ClrIntrinsic("Append")
    actual fun append(value: String): StringBuilder
}
```

Here, `append` is projected to the `System.Text.StringBuilder.Append` instance member. The member annotation supplies the CLR member name; if absent, there is no implicit same-name CLR member projection just because the owner class has `@ClrIntrinsic`.

A member declared inside an `@ClrIntrinsic` class but not itself annotated with `@ClrIntrinsic` is still an explicit Kotlin/stdlib API. It is not projected to a CLR member. Because the physical owner type is external, the compiler cannot add an instance method to that owner; therefore the member body must be emitted into the Kotlin assembly as a static forwarder/helper with the physical receiver passed explicitly.

For example:

```kotlin
@ClrIntrinsic("System.String")
actual class String {
    actual fun format(vararg args: Any?): String =
        System.String.Format(this, args)
}
```

The owner `String` is physically `System.String`, but `format` is not assumed to be a `System.String.Format` instance member. It is a stdlib API implemented by the stdlib assembly. If the method should be projected directly to a BCL member instead, the projection must be expressed on the member itself, with whatever receiver/vararg metadata the projection model requires.

The emitted shape is conceptually:

```text
DotKt.Stdlib.kotlin.String_format(self: System.String, args: object[]): System.String
  => body of kotlin.String.format
```

Calls to that Kotlin member lower to the static forwarder unless the member declaration itself has `@ClrIntrinsic` metadata. The owner class's `@ClrIntrinsic` affects the receiver type of the forwarder (`System.String` here), not the call target selection.

This rule prevents class-level CLR binding from recreating the old compiler-owned stdlib lowering catalog. `@ClrIntrinsic("System.X") actual class K` decides the physical type. `@ClrIntrinsic("Member") actual fun f` decides a physical member projection. An unannotated `actual fun f` remains Kotlin code owned by the stdlib.

## Call Site Inventory

`--native-cir` emits `callSites` as an observation aid for the TypeLowering migration. It scans BIR expressions and classifies call/member/type sites as:

- `already-clr`: a physical CLR-ish node already emitted by FIR -> BIR, such as `clrStatic`, `clrNew`, or a `clr:` / `clrg:` owner.
- `kotlin-symbol`: a Kotlin symbol that still needs BIR -> CIR resolution, such as `callStatic`, `callInstance`, `new`, or `field`.

Each site carries a stable JSON path into the original BIR payload. The path is the rewrite anchor for later native CIR transforms and lets `cirDraft.resolvedCalls` point back to the exact expression that can become a CLR node.

`typeSites` performs the same inventory for BIR type strings such as `type`, `ownerType`, `ret`, `resultType`, `base`, and `interfaces`. `typeResolutionDraft` and `cirDraft.resolvedTypes` resolve only against referenced assembly types and emit draft `clr.typeRef` entries for unique matches.

## Native CIR Direction

Native CIR should make CLR decisions explicit. The stable shape is still open, but v1 nodes should be named around CLR concepts rather than Kotlin frontend concepts:

- `clr.typeRef`: physical CLR type identity, including assembly identity where needed.
- `clr.methodRef`: physical CLR method identity, including owner, name, generic arity, lowered parameter types, and return type.
- `clr.fieldRef`: physical CLR field identity.
- `clr.local`: lowered local slot with a CLR type.
- `clr.call`: resolved static or instance method call.
- `clr.newobj`: resolved constructor call.
- `clr.ldfld` / `clr.stfld`: resolved field access.
- `clr.conv`: CLR numeric conversion.
- `clr.castclass` / `clr.isinst` / `clr.isinst.ref`: CLR casts and type tests.
- `clr.safeCast.value`: value-type `as?` lowering to `Nullable<T>`.
- `clr.nullable.null` / `clr.nullable.wrap` / `clr.nullable.hasValue` / `clr.nullable.value`: value-nullable operations over `Nullable<T>`.
- `clr.typeof` / `clr.getType`: type token and runtime type retrieval.
- `clr.enum.value` / `clr.enum.ordinal` / `clr.enum.values` / `clr.enum.parse`: CLR enum helpers.
- `clr.obj.eq` / `clr.obj.method`: `Any`/object identity helpers — null-safe `==` and the boxed `System.Object` virtual call for `equals` / `hashCode` / `toString`.

## Suspend Lowering Target

Suspend lowering should move into `bir2cir`, but the first CLR shape should be an async/await-level CIR representation rather than raw IL state-machine instructions.

That means BIR keeps Kotlin suspend semantics, then CIR introduces CLR async concepts such as:

- `clr.asyncFunction`: a lowered CLR async method with a `Task<T>` or `Task` return type.
- `clr.asyncLambda`: a lowered async delegate/closure body.
- `clr.await`: an await expression over `Task<T>`, `Task`, `ValueTask<T>`, or future supported awaitables.
- `clr.task<T>` / `clr.taskUnit`: normalized task result types.
- `clr.asyncLocal`: a local that must survive across await points.
- `clr.asyncTry`: try/catch/finally regions containing await points.

The initial public ABI remains compatible with the existing `Task<T>`-based behavior. The important responsibility shift is that `ilemit` should eventually emit a lowered CLR async/state-machine CIR form instead of discovering Kotlin suspend semantics itself.

The migration order for suspend is:

1. Detect and index current BIR coroutine shapes (`suspend`, `steps`, `coSuspend`, `coSuspendIntrinsic`, `coReturn`, `cpsFields`).
2. Emit native CIR analysis alongside the original BIR payload.
3. Emit a native CIR draft for simple linear suspend functions: `steps` -> `clr.asyncFunction` + `clr.await` + `return`.
4. Extend to executable branches, loops, and try/finally around await.
5. Teach `ilemit` to consume native async/state-machine CIR, then remove its Kotlin coroutine discovery.

## Projection Lookup Rule

`@ClrIntrinsic` projection is valid from any source that participates in BIR -> CIR: declarations in the current source set and declarations restored from referenced assemblies are both lookup inputs.

The rule is explicit declaration-level projection only:

- `@ClrIntrinsic` on a class controls that class's physical CLR type.
- `@ClrIntrinsic` on a function controls that function's physical CLR method projection.
- `@ClrIntrinsic` on a property controls that property's physical CLR property/field/accessor projection.
- A class-level `@ClrIntrinsic` never implies member projection for unannotated members.
- CLR name equality is never used as a fallback projection rule.
- Built-in primitive/string/array/nullability/type-test/runtime-type basics are not projection lookups; Basic Lowering handles them before projection resolution.

This applies equally while compiling the stdlib and while compiling a consumer of the stdlib. The difference is only where the metadata came from: current source declarations or referenced assembly metadata.
