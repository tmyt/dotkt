// Delegation / property-accessor / bound-ref battery (migration batch M1) — map-delegated properties, computed
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
// Batch-M1 collision rule: every top-level declaration is `M1`-prefixed (User -> M1User; topProp -> m1TopProp;
// Obj -> M1Obj; Host -> M1Host; String.shout -> String.m1Shout, etc.).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue

// ---- il-bymap : map-delegated properties -----------------------------------------------------------------------
class M1User(val data: Map<String, Any?>) {
    val name: String by data
    val age: Int by data
}

// ---- il-computedprop : #89 backing field + custom accessor must route through the accessor ---------------------
val m1TopProp: Int = 41
    get() = field + 1                       // read -> 42 (getter, not the raw 41)
var m1TopVar: Int = 0                       // DEFAULT getter (raw field read) + CUSTOM setter
    set(value) { field = value + 5 }
var m1TopGetVar: Int = 100                  // CUSTOM getter + DEFAULT setter (the reverse pairing)
    get() = field - 1
object M1Obj {
    val cProp: Int = 10
        get() = field * 2                   // control: object property getter already honored -> 20
}
class M1Host {
    companion object {
        val kProp: Int = 7
            get() = field + 100             // read -> 107 (getter, not the raw 7)
        var kVar: Int = 0                   // DEFAULT getter + CUSTOM setter
            set(value) { field = value * 2 }
    }
}

// ---- il-boundextref : bound ext-fn references (receiver captured eagerly into a capture class) -----------------
fun String.m1Shout(): String = this + "!"
fun String.m1RepeatBy(n: Int): String = repeat(n)
fun String.m1LogTo(sb: StringBuilder): Unit { sb.append("[").append(this).append("]") }

class MapAndInterfaceDelegationTests {
    @TestAttribute
    fun propertyByMap() {
        val u = M1User(mapOf("name" to "Alice", "age" to 30))
        assertEquals("Alice", u.name)  // Alice
        assertEquals(30, u.age)        // 30
    }

    @TestAttribute
    fun computedTopAndCompanion() {
        assertEquals(42, m1TopProp)         // 42
        assertEquals(107, M1Host.kProp)     // 107
        assertEquals(20, M1Obj.cProp)       // 20
        m1TopVar = 10
        assertEquals(15, m1TopVar)          // custom setter: field = 10 + 5 = 15
        M1Host.kVar = 3
        assertEquals(6, M1Host.kVar)        // custom setter: field = 3 * 2 = 6
        m1TopGetVar = 50                    // default setter: field = 50
        assertEquals(49, m1TopGetVar)       // custom getter: field - 1 = 49
    }

    @TestAttribute
    fun boundExtFnReferences() {
        // receiver-only bound ext ref, stored and invoked
        val f = "hi"::m1Shout
        assertEquals("hi!", f())            // hi!
        // bound ext ref with a regular param beyond the receiver: (Int) -> String
        val g = "ab"::m1RepeatBy
        assertEquals("ababab", g(3))        // ababab
        // the receiver is captured EAGERLY: mutate the var after taking the ref -> ref still uses "first"
        var s = "first"
        val h = s::m1Shout
        s = "second"
        assertEquals("first!", h())         // first!
        // a Unit-returning bound ext ref: the forwarder body is an exprStmt (not a return)
        val sb = StringBuilder()
        val u = "x"::m1LogTo
        u(sb); u(sb)
        assertEquals("[x][x]", sb.toString()) // [x][x]
        // #106: a BOUND ref to a CharSequence-declared stdlib ext — String receiver adapter-wrapped
        val nb = "  x "::isNotBlank
        assertTrue(nb())                    // True
        val bl = "   "::isBlank
        assertTrue(bl())                    // True
    }
}
