// Extension-function/property battery (M2 batch) — migrates the extension family of cases/il-* onto the in-process
// NUnit suite. Each old case's `main` + stdout-golden diff becomes one @TestAttribute method with typed asserts;
// every asserted value is preserved 1:1. Top-level declarations are `M2`-prefixed (one assembly = one namespace).
//
// Coverage preserved (old case -> method):
//   il-ext        -> m2_ext         user-defined extension functions (receiver -> __self first param)
//   il-extprop    -> m2_extprop     C7 top-level extension-property getters route to get_<name>(receiver): a GENERIC
//                                    getter (List<T>.lastIndex) + non-generic (CharSequence.lastIndex, Int.absoluteValue/.sign)
//   il-extpropref -> m2_extpropref  #21 bound/unbound + mutable extension-property references, including generic
//                                    receiver/value slots (`List<T>`, `List<T>` return, `M2RefBox<T>` get/set)
//
// The ext properties were `mySimpleName`/`tag`; renamed `m2MySimpleName`/`m2Tag` for collision-freedom, so a
// reference's `.name` now reads the new name — the property name is incidental to the subject (that the bound/unbound
// `::` reference resolves and its get()/set() invoke the static ext accessor with the captured receiver).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import kotlin.math.absoluteValue
import kotlin.math.sign

// ---- il-ext : user-defined extension functions ------------------------------------------------------------------
fun Int.m2Triple(): Int = this * 3
fun String.m2Shout(): String = this.uppercase()

// ---- il-extpropref : #21 bound/unbound references to a top-level EXTENSION property ------------------------------
val Any.m2MySimpleName: String get() = "Foo"
private var m2Store = "init"
var Any.m2Tag: String
    get() = m2Store
    set(value) { m2Store = value }
class M2RefBox<T>(var value: T)
val <T> List<T>.m2AuditLast: T get() = this[lastIndex]
val <T> T.m2AuditSingleton: List<T> get() = listOf(this)
var <T> M2RefBox<T>.m2AuditValue: T
    get() = value
    set(newValue) { value = newValue }
class M2Foo {
    override fun toString(): String {
        val p = this::m2MySimpleName
        return "${p.name}:${p.get()}"
    }
}

class ExtensionFunctionTests {
    @TestAttribute
    fun ext() {
        assertEquals(21, 7.m2Triple())       // 21
        assertEquals("HI", "hi".m2Shout())   // HI
    }

    @TestAttribute
    fun extprop() {
        assertEquals(2, listOf(10, 20, 30).lastIndex)  // 2  (generic List<T>.lastIndex, type args carried)
        assertEquals(1, listOf("a", "b").lastIndex)    // 1
        assertEquals(1, "hi".lastIndex)                // 1  (CharSequence.lastIndex)
        assertEquals(3, (-3).absoluteValue)            // 3
        assertEquals(-1, (-3).sign)                    // -1
        assertEquals(1, 3.sign)                        // 1
        assertEquals(0, 0.sign)                        // 0
    }

    @TestAttribute
    fun extpropref() {
        assertEquals("m2MySimpleName:Foo", M2Foo().toString())     // bound ref: .name + .get() invokes the ext getter
        val u = String::m2MySimpleName                              // UNBOUND ext-property ref -> KProperty1<String,String>
        assertEquals("m2MySimpleName=Foo", "${u.name}=${u.get("x")}")
        val m = M2Foo()::m2Tag                                      // BOUND mutable ext-property ref -> KMutableProperty0
        m.set("hi")                                                 // set() invokes the static set_ accessor w/ captured receiver
        assertEquals("m2Tag=hi", "${m.name}=${m.get()}")

        val genericUnbound = List<String>::m2AuditLast
        assertEquals("b", genericUnbound.get(listOf("a", "b")))
        val genericBound = listOf(10, 20)::m2AuditLast
        assertEquals(20, genericBound.get())
        val genericCollectionValue = String::m2AuditSingleton
        assertEquals("[one]", genericCollectionValue.get("one").toString())

        val genericBox = M2RefBox("before")
        val genericMutableUnbound = M2RefBox<String>::m2AuditValue
        genericMutableUnbound.set(genericBox, "unbound")
        assertEquals("unbound", genericMutableUnbound.get(genericBox))
        val genericMutableBound = genericBox::m2AuditValue
        genericMutableBound.set("bound")
        assertEquals("bound", genericMutableBound.get())
    }
}
