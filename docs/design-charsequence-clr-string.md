# CharSequence representation on the CLR

Status: current implementation record. The application string representation and the polymorphic
interface representation coexist at explicit boundaries owned by `bir2cir`.

## Application string slots

For application declarations in an emission unit without a user `CharSequence` implementation,
`CharSeqStringLowering` maps eligible CharSequence parameter, return, local, and field slots to
`System.String`. Nullable slots retain null. Member reads use string length, indexed character
access, and substring operations.

A non-string value entering one of these slots is converted through `.toString()`. This gives a
snapshot: later changes to a StringBuilder are not visible through the CharSequence value. A string
passes directly. Kotlin source types remain CharSequence; this is a physical representation choice.

An array slot is not a scalar string slot. If its representation is `string[]`, returning or
passing an existing array preserves that array, including for nullable and jagged arrays. A fresh
vector stores its elements using the selected component type; snapshot conversion applies to a
scalar element entering string storage, never to the enclosing array.

The pass handles the local declaration/call boundary. External stdlib extension signatures and
higher-order delegate contracts can still require the synthetic interface, so collapsing every
CharSequence occurrence to string would be incorrect.

For a non-overridable suspend function with a collapsed scalar result, the CLR entry returns
`Task<string>`. A return value saved across a suspending `finally` uses the same string storage.
An overridable `suspend fun read(): CharSequence?` instead retains the interface as the CLR
`Task<T>` argument: a derived implementation in another assembly can return a real sequence,
and CLR tasks are invariant. The erased Kotlin continuation can carry the original string or
sequence. The Task bridge converts a string to the interface view on completion (immediate or
resumed), passes through a real implementation, and preserves null. That conversion does not
change a string returned to a Kotlin caller into a different object merely to satisfy the CLR Task.

## Polymorphic interface slots

A user `class S : CharSequence` implements the synthetic `dotkt$CharSequence` interface.
`System.String` is sealed, so that implementation cannot inherit string. If an emission unit declares
such an implementation, its CharSequence declarations remain polymorphic throughout the unit;
`CharSeqStringLowering` is skipped. Reading length or characters therefore dispatches to the user's
implementation rather than to a snapshot of its default `toString()`.

The stdlib's CharSequence extension signatures also retain the synthetic interface. The
`StringCharSequenceBridge` pass adapts String/StringBuilder values at these interface boundaries.
StringBuilder inputs are snapshotted, preserving the same immutable-snapshot rule as string slots.
Lambda targets whose CharSequence parameters are fixed by a higher-order delegate contract retain
that contract; their parameters cannot be changed independently of the caller.

The synthetic `dotkt$StringCharSequence` adapter and shared synthetic interface declarations remain
necessary for these paths. The presence of an adapter does not imply a live StringBuilder view.

## Layer ownership

A Kotlin `T : CharSequence` upper bound is retained for Kotlin consumers in
metadata. It is not a CLR interface constraint on the executable generic
declaration: native strings do not implement that interface. A C# caller thus
sees a weaker constraint. A value transported as `T` keeps its identity; a
scalar interface slot obtains a view only when the value is used there.
Other CLR constraints on the same type parameter are retained.

`kotc` projects Kotlin CharSequence identities and operations into BIR. In `bir2cir`,
`TypedCharSequenceIdentity` establishes the synthetic identity before
`TypedModuleInventory` detects user implementers. `CharSeqStringLowering` selects eligible string slots, and
`StringCharSequenceBridge` closes the interface boundaries. `ilemit` emits the resulting CIR.

The driver coordinates the passes in `toolchain/bir2cir/Program.cs`. A stdlib signature change would
need corresponding cross-assembly call coercions; the current ABI must not be described as if all
stdlib CharSequence extensions already accepted string directly.

## Coverage and related contracts

`tests/basic/fixtures/StringsTests.kt` covers string operations and user CharSequence behavior.
`tests/roundtrip/producer/CharSequenceSupertypes.kt`,
`tests/roundtrip/consumer/fixtures/CharSequenceSupertypeTests.kt`, and the corresponding metadata
inspector cover restoration of source supertypes through a separately compiled assembly.

See [dotkt-semantics.md §5b](dotkt-semantics.md#5b-charsequence-is-string-on-the-clr--an-immutable-snapshot-not-a-live-view)
for the user-visible deviation and [architecture.md](architecture.md) for layer ownership.
