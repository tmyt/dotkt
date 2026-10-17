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

The pass handles the local declaration/call boundary. External stdlib extension signatures and
higher-order delegate contracts can still require the synthetic interface, so collapsing every
CharSequence occurrence to string would be incorrect.

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

`kotc` projects Kotlin CharSequence identities and operations into BIR. In `bir2cir`,
`SubstituteCharSeqIdentity` establishes the synthetic identity, the emission-unit scan detects
user implementers, `CharSeqStringLowering` selects eligible string slots, and
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
