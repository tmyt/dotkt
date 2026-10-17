# Primitive and Number representation on the CLR

Status: current implementation record. Kotlin source types are preserved in frontend metadata;
`bir2cir` selects their CLR storage and operations.

## Primitive storage

The CLR stdlib declarations bind Kotlin primitives to their native CLR types through `@ClrTypeAlias`.
In runtime and application builds, that binding applies to bare values and generic arguments:

| Kotlin position | Physical CLR representation |
|---|---|
| `Int` parameter, result, field, or local | `System.Int32` |
| `Box<Int>` for an ordinary invariant `Box<T>` | `Box<System.Int32>` |
| exact native collection element `Int` | `System.Int32` in the selected CLR generic interface |
| `Int` stored in `Any` or `Number` | native boxed `System.Int32` |
| direct `Int?` value slot | `System.Nullable<System.Int32>` |

Runtime and application builds do not emit wrapper classes such as `kotlin.Int` or `kotlin.Short`.
A generic argument does not by itself introduce a Kotlin wrapper or require boxing; CLR generics can
store native value types directly. Boxing is selected when a value actually crosses an object slot.

Other representation rules still apply around these primitives. Ordinary slots for declaration-variant
collection aliases such as `List<Int>` use `object`, while exact native declaration signatures retain
their CLR interface types. Nullable generic storage is also positional: an open `T?` uses `object`,
and a possibly-value nullable argument such as `Box<Int?>` uses an erased argument with its source
type retained in metadata. These are storage rules owned by `bir2cir`, not Kotlin source-type changes.
See [dotkt-semantics.md](dotkt-semantics.md) §§2, 5c, and 9c-bis.

## Number is a Kotlin classifier over native values

The CLR numeric value types cannot inherit a common Kotlin class. Ordinary `Number` and `Number?`
slots therefore use `System.Object`, including nested generic arguments. Numeric values retain their
native CLR boxes; passing an `Int` through `Number` does not create another wrapper object.

`Number` membership is determined from Kotlin semantics: the six signed numeric primitive types and
authored Number subclasses qualify. Boolean, Char, unsigned values, strings, and arbitrary
`IConvertible` objects do not acquire Number identity. Direct and reified checks use the same rule.

An authored `class N : Number()` has physical base `System.Object`. Compiler-owned nominal conversion
slots preserve its `toByte`/`toShort`/`toInt`/`toLong`/`toFloat`/`toDouble` overrides, including inherited
implementations, generic bounds, and calls across a DLL boundary. Native numeric boxes use their
corresponding conversions. Source-aware lookup preserves Kotlin overload selection before CLR erasure.
The physically impossible Number-class constraint is omitted from CLR generic constraints while the
original Kotlin bound is preserved in metadata. No public runtime `kotlin.Number` class is emitted.

`Number::class` and `T::class` describe physical CLR types; classifier-sensitive `is`/`as` operations
carry separate Kotlin facts. Array storage follows the array representation contract: for example,
`Array<Number>` uses `object[]`, while an `Array<out Number>` view can retain a native numeric array
through `System.Array`. The detailed classifier and array rules are in
[dotkt-semantics.md §5-number](dotkt-semantics.md#5-number-number-values-keep-native-clr-boxing-authored-subclasses-use-nominal-slots).

## Artifact and layer ownership

- `kotc` records Kotlin identities, types, bounds, and operations in BIR. It does not select CLR
  wrappers, boxing, or native collection signatures.
- The metadata stdlib build retains the pure Kotlin declaration surface and its binding annotations.
  Runtime and application builds apply the bindings in `bir2cir`.
- `bir2cir` owns type substitution, object-slot conversions, Number dispatch, classifier witnesses,
  and preservation of the original declaration facts in metadata.
- `ilemit` emits the resolved CIR signatures and conversions one-to-one.
- `dll2klib` restores source types and bounds from trusted metadata when a user library is consumed
  as Kotlin. Reference-body stripping must preserve these facts and the declaration constraints.

## Implementation and coverage

`toolchain/bir2cir/BirTypeLowering.cs` applies primitive aliases in both value and generic positions.
Number's trusted stdlib binding and nominal conversion contract are declared in
`libraries/stdlib/clr/builtins/Number.kt`.

Permanent coverage includes `tests/basic/fixtures/NumberTests.kt`,
`tests/interop/consumer/fixtures/NumberNativeTests.kt`, `tests/roundtrip/consumer/NumberTests.kt`,
and `tests/roundtrip/bidirectional/consumer/NumberTests.cs`. These cover Kotlin behavior, exact native
signatures, authored subclasses, and source restoration across separately compiled assemblies.
