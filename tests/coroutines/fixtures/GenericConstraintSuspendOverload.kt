// Regression: Kotlin distinguishes these overloads by their generic bounds. Suspend lowering must retain both
// declarations even though their value-parameter TypeNodes are otherwise identical.
interface SuspendConstraintA
interface SuspendConstraintB

suspend fun <T : SuspendConstraintA> genericConstraintSuspendOverload(value: T): T = value
suspend fun <T : SuspendConstraintB> genericConstraintSuspendOverload(value: T): T = value
