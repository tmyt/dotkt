// Step-1 CLR stub mirroring the JVM `actual` declarations for this source set.
// Bodies are `TODO` pending the `@Clr`/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").
@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.time

@ExperimentalTime
internal actual fun systemClockNow(): Instant = TODO("clr binding should be implemented")

@ExperimentalTime
internal actual fun serializedInstant(instant: Instant): Any = TODO("clr binding should be implemented")
