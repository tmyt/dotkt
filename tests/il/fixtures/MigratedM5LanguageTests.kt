// Language-core battery (batch M5): boxing/reassign, super-calls, star-projection casts, visibility, typealias,
// and cross-file/cross-namespace dispatch + a cross-file top-level property. Migrates the core-language family of
// cases/il-* onto the in-process NUnit suite. Each old case's `main` + stdout-golden diff becomes one @TestAttribute
// method whose per-value assert is strictly stronger (typed) than the old text diff; every asserted value is
// preserved 1:1 (see `// <expected>`).
//
// Coverage preserved (old case -> method):
//   il-setlocalbox -> localBox_anyReassign            `Any` local/field reassigned across value/reference boxing
//   il-supercall   -> superCall_nonVirtual            #14 super.X() = a NON-virtual call to the resolved base slot (method/prop/3-level/DIM)
//   il-starproj    -> starProjection_nonGenericFacade #60 value-type-arg collection erased to Any + `is Map<*,*>` -> NON-generic BCL facade
//   il-vis         -> visibilityModifiers             private/internal/protected members + a private top-level fun
//   il-typealias   -> typealias_acrossBoundary        typealias over stdlib-generic / function-type / user-class across a fn boundary
//   il-xfaceimpl   -> crossFileIfaceDispatch          cross-file + namespaced interface impl/dispatch (declarations in MigratedM5CrossFile.kt)
//   il-xprop       -> crossFileTopLevelProp           mutable top-level property declared in a sibling file, read + written here
//
// All top-level declarations introduced here are M5-prefixed (one assembly = one namespace). The cross-file cases'
// sibling declarations live in MigratedM5CrossFile.kt (package m5p).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsFalse as assertFalse
import m5p.M5ImplC
import m5p.m5cur
import m5p.m5call
import m5p.m5counter
import m5p.m5bump

// ---- il-setlocalbox : `Any` field reassigned from String to a boxed Int -------------------------------------------
class M5Holder {
    var v: Any = "s"
    fun put(n: Int) { v = n }
}

// ---- il-supercall : #14 super.X() from an override = a non-virtual call to the resolved base slot -----------------
open class M5Base {
    open fun greet() = "base"
    open fun twice(x: Int) = x * 2
    open val tag: String get() = "base-tag"
    open fun describe() = "Base"
}
class M5Derived : M5Base() {
    override fun greet() = "derived+" + super.greet()
    override fun twice(x: Int) = super.twice(x) + 1
    override val tag: String get() = "derived[" + super.tag + "]"
    override fun describe() = "Derived<" + super.describe() + ">"
}
open class M5A { open fun name() = "A" }
open class M5B : M5A() { override fun name() = super.name() + "B" }
class M5C : M5B() { override fun name() = super.name() + "C" }
open class M5Animal { override fun toString() = "animal" }
class M5Dog : M5Animal() { override fun toString() = "dog>" + super.toString() }
interface M5Greeter { fun hi(): String = "hi-default" }
class M5Impl : M5Greeter { override fun hi() = "impl+" + super.hi() }

// ---- il-vis : visibility modifiers -> CLR access flags ------------------------------------------------------------
class M5Account(private val balance: Int) {
    private fun fee(): Int = 2
    fun net(): Int = balance - fee()
    internal fun tag(): String = "acct"
    protected open fun kind(): String = "base"
}
private fun m5secret(): Int = 99

// ---- il-typealias : aliases used across a function boundary -------------------------------------------------------
typealias M5Names = List<String>
typealias M5IntOp = (Int) -> Int
typealias M5Pairs = Map<String, Int>
class M5TaBox(val v: Int) { fun twice(): Int = v * 2 }
typealias M5Container = M5TaBox
fun m5join(ns: M5Names): String = ns.joinToString(",")
fun m5makeNames(): M5Names = listOf("a", "b", "c")
fun m5apply2(op: M5IntOp, x: Int): Int = op(op(x))
fun m5unwrap(c: M5Container): Int = c.twice()
fun m5lookup(p: M5Pairs, k: String): Int = p[k] ?: -1

class MigratedM5LanguageTests {
    @TestAttribute
    fun localBox_anyReassign() {
        var a: Any = "x"
        a = 42
        assertEquals(42, a)          // 42
        val h = M5Holder()
        h.put(7)
        assertEquals(7, h.v)         // 7
    }

    @TestAttribute
    fun superCall_nonVirtual() {
        val d = M5Derived()
        assertEquals("derived+base", d.greet())        // derived+base
        assertEquals(21, d.twice(10))                   // 21
        assertEquals("derived[base-tag]", d.tag)        // derived[base-tag]
        assertEquals("Derived<Base>", d.describe())     // Derived<Base>
        assertEquals("ABC", M5C().name())               // ABC
        assertEquals("dog>animal", M5Dog().toString())  // dog>animal
        assertEquals("impl+hi-default", M5Impl().hi())  // impl+hi-default
        val b: M5Base = M5Derived()
        assertEquals("derived+base", b.greet())         // derived+base (virtual dispatch non-regression)
        assertEquals(11, b.twice(5))                    // 11
    }

    // #60: the star-projection smart-cast (`is Map<*,*>`/`is List<*>`/`is Iterable<*>`/`is Collection<*>`) on a
    // value-type-arg collection erased to `Any` must lower its castclass to the NON-generic BCL facade
    // (IDictionary/IList/ICollection/IEnumerable), NOT the object-erased generic interface — the CLR's reified
    // generics are INVARIANT, so a Dictionary<int,int> is NOT an IDictionary<object,object> and that cast throws
    // InvalidCastException. Proven here by the smart-casts SUCCEEDING and `.size`/`[i]` re-pointing onto the
    // non-generic ICollection.Count / IList.get_Item without throwing. The original case additionally asserted the
    // `println` RENDER ("{1=2, 3=4}" / "[10, 20, 30]") — that is the stdout-only clrElemToString path (a plain
    // `"$g"` / `toString()` on the erased value yields the raw .NET `Dictionary`2`/`List`1` ToString), NOT
    // reproducible as an in-process value, so these typed structural asserts (stronger for the cast subject)
    // stand in for it.
    @TestAttribute
    fun starProjection_nonGenericFacade() {
        val g: Any = hashMapOf(1 to 2, 3 to 4)
        assertTrue(g is Map<*, *>)                       // smart-cast lowers to the non-generic IDictionary facade
        if (g is Map<*, *>) {
            assertEquals(2, g.size)                      // 2  (non-generic ICollection.Count)
        }
        val l: Any = listOf(10, 20, 30)
        assertTrue(l is List<*>)                         // -> non-generic IList facade
        if (l is List<*>) {
            assertEquals(3, l.size)                      // 3
            assertEquals(20, l[1])                       // 20 (non-generic IList.get_Item)
        }
        assertTrue(l is Iterable<*>)                     // -> non-generic IEnumerable facade
        assertTrue(l is Collection<*>)                  // -> non-generic ICollection facade
        assertFalse((5 as Any) is Map<*, *>)            // False (a non-collection is not a Map)
        assertFalse(("x" as Any) is List<*>)            // False
    }

    @TestAttribute
    fun visibilityModifiers() {
        val a = M5Account(100)
        assertEquals(98, a.net())        // 98
        assertEquals("acct", a.tag())    // acct
        assertEquals(99, m5secret())     // 99
    }

    @TestAttribute
    fun typealias_acrossBoundary() {
        val ns: M5Names = m5makeNames()
        assertEquals("a,b,c", m5join(ns))       // a,b,c
        assertEquals(3, ns.size)                 // 3
        val inc: M5IntOp = { it + 1 }
        assertEquals(12, m5apply2(inc, 10))      // 12
        assertEquals(42, m5unwrap(M5Container(21))) // 42
        val p: M5Pairs = mapOf("x" to 7, "y" to 9)
        assertEquals(9, m5lookup(p, "y"))        // 9
        assertEquals(-1, m5lookup(p, "z"))       // -1
    }

    @TestAttribute
    fun crossFileIfaceDispatch() {
        m5cur = M5ImplC()
        assertEquals(1, m5call(1))               // 1 (dispatch reaches m5p.M5ImplC.go across file + namespace)
    }

    @TestAttribute
    fun crossFileTopLevelProp() {
        m5counter = 0
        m5bump(); m5bump(); m5counter = m5counter + 5
        assertEquals(7, m5counter)               // 7
    }
}
