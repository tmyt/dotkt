# Function-value variance: contract investigation

Status: investigation for #909. The user permits nominal SAM projection of CLR Func/Action; the ordinary
Kotlin function representation still needs implementation and validation. This document does not itself
establish a proven representation.

The initial implementation prototype uses one object-argument/object-result delegate shape per ordinary
function arity, with conversions at compiler-generated target entry rather than ordinary value-flow edges.
Five local focused cases now pass strict ILVerify and execution, including Unit and identity after Any views.
The prototype now also preserves authored function-containing declaration slots in KotlinType metadata before
representation changes, and explicitly narrows erased invocation results. The permanent cross-DLL fixture covers
source signatures and identity, nullable/nested functions, receiver/context functions, generic containers, and
inline/default arguments. Native Func/Action projection is nominal, using the existing arity-disambiguated names
(for example `System.Func2<Int, Int>`). Focused native tests cover literal/stored-function SAM conversions,
CLR override dispatch, nominal delegate identity, and a Span callback returning Unit as an object.
Literal constructions already bound to a native delegate keep their exact function signature; ordinary Kotlin
function operands erase before a stored-value SAM conversion. This prevents illegal boxing of native Span slots.
This is not yet the completed contract: wide arities, broader existing interop and function regression suites,
the complete toolchain build, independent reviews, and actual coroutines validation remain outstanding.

## Reproduced defect

Ordinary Kotlin function variance currently crosses incompatible physical delegate types:

```kotlin
fun <T> narrow(f: (Any?) -> Any?): (T) -> Any? = f
fun <T, R> widen(f: (T) -> R): (T) -> Any? = f
```

The complete SDK at `236f7bb4` builds these declarations but emits invalid IL. Expanded regression tests also
find the mismatch in nullable function returns, argument passing, and property storage. The same tests run
successfully on Kotlin/JVM 2.4.10, including identity comparisons after upcasting the functions to `Any`.

`BirTypeLowering.LowerFnDelegate` currently chooses typed `Func`/`Action` slots. CLR generic variance does not
relate arbitrary value-type instantiations, so it cannot implement this Kotlin rule. A cast does not repair the
runtime relation. Creating a fresh delegate at each ordinary Kotlin value-flow edge instead would break `===`.

## Required behavior

- Preserve ordinary Kotlin function invocation and identity across argument/result variance, assignments,
  fields/properties, nullable values, generic containers, `Any` views, and DLL boundaries.
- Keep common library sources unchanged. No Flow/Distinct or other library-specific special cases.
- Preserve the full Kotlin function signature in declaration metadata when the physical signature no longer
  describes it; a consumer must not reconstruct missing semantics from a physical delegate or method body.
- Keep CLR calls and overrides bound to exact native delegate signatures. Preserve single evaluation and
  exception propagation at adaptation boundaries.
- Preserve existing supported byref-like callback parameters, notably `UnitDelegateAdapterTests`' Span case.
  Blanket boxing of every callback argument is not a valid general solution.
- Keep function equality/hash behavior consistent with identity through collections; rewriting only `===`
  while leaving unrelated wrapper objects in ordinary storage would not satisfy this contract.
- bir2cir owns representation and adapters; kotc retains Kotlin facts and ilemit emits concrete CIR only.

## Native boundary and approved direction

The current documented contract in `dotkt-semantics.md` sections 8e/8e-bis treats `System.Func`/`Action` as
structural Kotlin function types, unlike other CLR delegates, which are nominal callable SAM types.

A `Func<object, int>` cannot itself be the `Func<int, int>` object required by an exact native slot. A bridge
can invoke the original object, but the bridge is another CLR object. Thus adapting a function for a native
round trip and preserving the original object's identity are separate requirements, not an ordinary cast.

Two boundaries need evaluation before implementation:

1. Keep the existing structural projection for canonical native delegates. Native crossings then need an
   explicit adaptation/identity contract distinct from ordinary Kotlin function variance, including values
   returned through `object` and repeated reads of the same native callback. An unexamined cache is not a
   proof that all aliases preserve identity.
2. Project `System.Func`/`Action` as nominal SAMs too, keeping ordinary Kotlin function values separate from
   native delegate values. This matches the existing custom-delegate boundary but changes Kotlin source
   signatures for CLR overrides. For example, an override's `(Int) -> Int` parameter becomes
   `System.Func<Int, Int>`. Automatic lambda/SAM conversion and stored-function conversion must be tested;
   they must not be assumed to make every existing consumer source-compatible.

The user explicitly permits option 2, including changing CLR override parameters to their nominal delegate
types. Preserve lambda and stored-function SAM conversion, ordinary Kotlin function identity, and upstream
common sources. Do not interpret approval as evidence that an implementation satisfies those requirements.

The overarching priority is natural CLR integration as experienced from Kotlin. Public CLR signatures of
Kotlin-produced APIs still need an impact assessment, but somewhat unusual C# consumption is acceptable
case by case; preserving ordinary-looking C# signatures must not force unnatural Kotlin usage. Valid CLR
binaries and correct dispatch remain required. The separate array design must not introduce the proposed
source-facing ClrArray split or require explicit conversions for ordinary CLR array arguments/results.

## Implementation checkpoints after the boundary decision

### Current implementation seams to audit

- `BirTypeLowering.LowerFnDelegate` currently lowers every ordinary function's parameter/result slots
  directly, while `PhysicalDelegate` also constructs exact native delegate signatures. A representation
  change must distinguish Kotlin function values from these already-physical delegates; changing the shared
  helper indiscriminately would change native ABI as well as ordinary value storage.
- `dll2klib` has both local and referenced delegate projection paths. The catalog's
  `IsCanonicalFunctionDefinition` is not the only structural projection gate: `GetGenericInstantiation`,
  `GetTypeFromReference`, and the local canonical-function helpers also participate. Nominal projection
  must be consistent across declarations, references, and generic instantiations.
- `tests/interop/consumer/fixtures/UnitDelegateAdapterTests.kt` exercises a real Span callback alongside
  capturing lambdas, callable references, generic frames, properties, and fields. Keep this supported
  callback path: erasing every argument into an object slot would require illegal boxing of Span.
- The ordinary Kotlin value carrier and native SAM adapters are separate design obligations. Native
  nominal projection alone cannot repair the invalid ordinary Kotlin variance reproduced above.

1. Establish the ordinary value representation and prove identity, null behavior, argument/result conversion,
   and exception propagation in focused tests. Include value/reference/nullable generic slots and Unit.
2. Preserve/restore declaration facts across DLL-to-KLIB projection and inline/default carriers; add a
   producer/consumer regression rather than relying only on one assembly.
3. Exercise native callbacks, exact overrides, nominal SAM construction, byref-like parameters, receiver/context
   functions and wide arities. Do not silently discard supported cases to make the minimal reproduction pass.
4. Run the budgeted independent reviews, canonical gate, and the real coroutines inventory. The three
   DistinctKt findings have this mismatch shape; the BufferedChannelIterator findings are not yet proven to
   have the same cause. Runtime #854 remains separate.
