// Cross-file declarations for the MigM cross-file battery (from cases/m-c1) — their use sites live in a SIBLING
// .kt of the SAME suite assembly (CrossFileDispatchTests.kt), preserving the original case's cross-FILE dimension:
// an open class + override and a plain class, DECLARED here and INSTANTIATED / virtually dispatched from the
// other file. Keeping them split is the exact shape m-c1 proved.
//
//   m-c1  -> MigMCPoint / MigMShape / MigMRect : cross-file class + method call, and an open-class `area()`
//            override reached through a non-open `label()` (virtual dispatch across the file boundary).
package migmc

class MigMCPoint(val x: Int, val y: Int) {
    fun distanceSquared(): Int = x * x + y * y
    fun plus(other: MigMCPoint): MigMCPoint = MigMCPoint(x + other.x, y + other.y)
    fun describe(): String = "($x, $y)"
}

open class MigMShape(val name: String) {
    open fun area(): Int = 0
    fun label(): String = "$name area=${area()}"   // non-open label() reaches the overridden area() virtually
}

class MigMRect(val w: Int, val h: Int) : MigMShape("rect") {
    override fun area(): Int = w * h
}
