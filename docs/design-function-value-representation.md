# Function-value representation

Status: current implementation record. Ordinary Kotlin function values use identity-preserving
carriers; native CLR delegates are projected as nominal SAM types.

## Ordinary Kotlin function values

A non-suspend function value uses one delegate carrier per arity. Boxable parameter and result slots
use `object`, including a Unit result, whose value is the Kotlin Unit singleton. Conversion happens
at invocation and typed consumption. Assignments, parameter/result variance, nullable storage,
generic containers, and `Any` views retain the same function object.

For example, these functions return the original object without allocating a new delegate:

```kotlin
fun <T> narrow(f: (Any?) -> Any?): (T) -> Any? = f
fun <T, R> widen(f: (T) -> R): (T) -> Any? = f
```

The common carrier avoids relying on CLR delegate variance between value-type instantiations.
Kotlin function identity, equality, and hash behavior remain consistent across ordinary value flow
and DLL boundaries. Nullable functions preserve null. Byref-like callback slots that cannot be boxed
retain their exact physical types.

Arities 0–16 use BCL `Func` carriers. Arities 17–22 use the stdlib's canonical `KFunc` family;
its accompanying `KAction` definitions remain part of the compiler-owned wide-delegate surface.
An extension or context receiver counts toward the arity. Non-suspend arities above 22 are currently
refused by `bir2cir`; suspend function values use object state-machine carriers and have no delegate
arity limit. See [dotkt-semantics.md §8e-bis](dotkt-semantics.md#8e-bis-ordinary-kotlin-function-values-use-identity-preserving-delegate-carriers).

## Native CLR delegate boundary

Every imported CLR delegate, including `System.Func` and `System.Action`, is a nominal Kotlin
callable SAM type. Imported declarations and overrides keep that exact identity. For example,
a native `Func<int,int>` parameter is projected as `System.Func2<Int, Int>`, and a Kotlin override
names that nominal type. Distinct delegate types remain distinct overload parameter types even
when their `Invoke` signatures match.

Lambda literals and stored Kotlin function values can cross this boundary through SAM conversion.
The native delegate uses its declared parameter and result types; a native conversion does not make
it the same source type as an ordinary Kotlin function value. When nominal overloads are ambiguous,
an explicit SAM constructor selects the delegate type.

A literal bound directly to a native callback keeps its exact signature, including Span parameters
and byref-like results. Stored ordinary function operands enter their carrier representation before
SAM adaptation. Erasing native Span slots to `object` would require illegal boxing and is not valid.
See [dotkt-semantics.md §8e](dotkt-semantics.md#8e-a-net-delegate-is-a-nominal-kotlin-fun-interface).

## Metadata and layer ownership

`kotc` preserves source function types. `bir2cir` selects carriers and adapters and records the original
function-containing declaration slots in `KotlinType` metadata before changing their representation.
`ilemit` emits concrete CIR only. `dll2klib` restores the source signatures from the metadata, including
nested/nullable functions, receiver/context functions, and generic containers. Inline and default
payloads retain the same source facts.

Common library sources require no special cases for individual functions or libraries.
`FunctionValueRepresentation` and `BirTypeLowering` own ordinary storage; native delegate projection
and resolution preserve the exact CLR declaration separately.

## Verification coverage

- `tests/basic/fixtures/FunctionVarianceTests.kt` covers ordinary value flow, invocation, identity,
  equality, and hash behavior.
- `tests/roundtrip/producer/FunctionVariance.kt` and `tests/roundtrip/consumer/FunctionVarianceTests.kt`
  cover source signatures, identity, and inline/default behavior across a DLL boundary.
- `tests/interop/consumer/fixtures/NominalFunctionDelegateTests.kt` and
  `tests/interop/consumer/fixtures/StoredDelegateConversionTests.kt` cover native SAMs and conversions.
- `tests/interop/consumer/fixtures/UnitDelegateAdapterTests.kt` covers exact callback signatures,
  generic frames, captured functions, and Span callbacks.

The ILVerify harness classifies the specific Span-return `ReturnPtrToStack` findings reproduced by
C# controls as `SPAN-VERIFIER-LIMIT`, and retains their runtime tests. Other diagnostics and incomplete
verifier runs still fail. Current validation outcomes belong to gate output and CI; this design record
does not preserve point-in-time review results or an implementation checklist.
