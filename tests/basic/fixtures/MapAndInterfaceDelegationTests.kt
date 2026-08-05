// Delegation / property-accessor / bound-ref battery (feature fixture) — map-delegated properties, computed
// top-level/companion properties, and bound extension-function references. Migrates this family of cases/il-* onto the
// in-process NUnit suite. Each old case's `main` + stdout-golden diff becomes one @TestAttribute method whose per-value
// assert is strictly stronger (typed) than the old text diff. Every value the old il_check asserted is preserved 1:1.
//
// Coverage preserved (old case -> method):
//   il-bymap        -> propertyByMap            `val x: T by data` map-delegated properties
//   il-computedprop -> computedTopAndCompanion  #89 top-level/companion val/var with BOTH backing field AND custom
//                                               accessor must INVOKE the accessor (read + write + independent pairings)
//   il-boundextref  -> boundExtFnReferences     #91/#106 `expr::extFn` bound ext-fn refs -> capture-class lift (receiver
//                                               captured EAGERLY); Unit forwarder; bound CharSequence-ext ref adapter-wrap
//
// Assembly-wide collision rule: every top-level declaration is `MapDelegation`-prefixed (User -> MapDelegationUser; topProp -> mapDelegationTopProp;
// Obj -> MapDelegationObj; Host -> MapDelegationHost; String.shout -> String.mapDelegationShout, etc.).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue

// ---- il-bymap : map-delegated properties -----------------------------------------------------------------------
class MapDelegationUser(val data: Map<String, Any?>) {
    val name: String by data
    val age: Int by data
}

// ---- il-computedprop : #89 backing field + custom accessor must route through the accessor ---------------------
val mapDelegationTopProp: Int = 41
    get() = field + 1                       // read -> 42 (getter, not the raw 41)
var mapDelegationTopVar: Int = 0                       // DEFAULT getter (raw field read) + CUSTOM setter
    set(value) { field = value + 5 }
var mapDelegationTopGetVar: Int = 100                  // CUSTOM getter + DEFAULT setter (the reverse pairing)
    get() = field - 1
object MapDelegationObj {
    val cProp: Int = 10
        get() = field * 2                   // control: object property getter already honored -> 20
}
class MapDelegationHost {
    companion object {
        val kProp: Int = 7
            get() = field + 100             // read -> 107 (getter, not the raw 7)
        var kVar: Int = 0                   // DEFAULT getter + CUSTOM setter
            set(value) { field = value * 2 }
    }
}

// ---- il-boundextref : bound ext-fn references (receiver captured eagerly into a capture class) -----------------
fun String.mapDelegationShout(): String = this + "!"
fun String.mapDelegationRepeatBy(n: Int): String = repeat(n)
fun String.mapDelegationLogTo(sb: StringBuilder): Unit { sb.append("[").append(this).append("]") }

class MapAndInterfaceDelegationTests {
    @TestAttribute
    fun propertyByMap() {
        val u = MapDelegationUser(mapOf("name" to "Alice", "age" to 30))
        assertEquals("Alice", u.name)  // Alice
        assertEquals(30, u.age)        // 30
    }

    @TestAttribute
    fun computedTopAndCompanion() {
        assertEquals(42, mapDelegationTopProp)         // 42
        assertEquals(107, MapDelegationHost.kProp)     // 107
        assertEquals(20, MapDelegationObj.cProp)       // 20
        mapDelegationTopVar = 10
        assertEquals(15, mapDelegationTopVar)          // custom setter: field = 10 + 5 = 15
        MapDelegationHost.kVar = 3
        assertEquals(6, MapDelegationHost.kVar)        // custom setter: field = 3 * 2 = 6
        mapDelegationTopGetVar = 50                    // default setter: field = 50
        assertEquals(49, mapDelegationTopGetVar)       // custom getter: field - 1 = 49
    }

    @TestAttribute
    fun boundExtFnReferences() {
        // receiver-only bound ext ref, stored and invoked
        val f = "hi"::mapDelegationShout
        assertEquals("hi!", f())            // hi!
        // bound ext ref with a regular param beyond the receiver: (Int) -> String
        val g = "ab"::mapDelegationRepeatBy
        assertEquals("ababab", g(3))        // ababab
        // the receiver is captured EAGERLY: mutate the var after taking the ref -> ref still uses "first"
        var s = "first"
        val h = s::mapDelegationShout
        s = "second"
        assertEquals("first!", h())         // first!
        // a Unit-returning bound ext ref: the forwarder body is an exprStmt (not a return)
        val sb = StringBuilder()
        val u = "x"::mapDelegationLogTo
        u(sb); u(sb)
        assertEquals("[x][x]", sb.toString()) // [x][x]
        // #106: a BOUND ref to a CharSequence-declared stdlib ext — String receiver adapter-wrapped
        val nb = "  x "::isNotBlank
        assertTrue(nb())                    // True
        val bl = "   "::isBlank
        assertTrue(bl())                    // True
    }
}
