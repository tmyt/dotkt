// Extension-function/property battery (feature fixture) — migrates the extension family of cases/il-* onto the in-process
// NUnit suite. Each old case's `main` + stdout-golden diff becomes one @TestAttribute method with typed asserts;
// every asserted value is preserved 1:1. Top-level declarations are `Extension`-prefixed (one assembly = one namespace).
//
// Coverage preserved (old case -> method):
//   il-ext        -> extension_ext         user-defined extension functions (receiver -> __self first param)
//   il-extprop    -> extension_extprop     C7 top-level extension-property getters route to get_<name>(receiver): a GENERIC
//                                    getter (List<T>.lastIndex) + non-generic (CharSequence.lastIndex, Int.absoluteValue/.sign)
//   il-extpropref -> extension_extpropref  #21 bound/unbound + mutable extension-property references, including generic
//                                    receiver/value slots (`List<T>`, `List<T>` return, `ExtensionRefBox<T>` get/set)
//
// The ext properties were `mySimpleName`/`tag`; renamed `extensionMySimpleName`/`extensionTag` for collision-freedom, so a
// reference's `.name` now reads the new name — the property name is incidental to the subject (that the bound/unbound
// `::` reference resolves and its get()/set() invoke the static ext accessor with the captured receiver).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import kotlin.math.absoluteValue
import kotlin.math.sign

// ---- il-ext : user-defined extension functions ------------------------------------------------------------------
fun Int.extensionTriple(): Int = this * 3
fun String.extensionShout(): String = this.uppercase()

// ---- il-extpropref : #21 bound/unbound references to a top-level EXTENSION property ------------------------------
val Any.extensionMySimpleName: String get() = "Foo"
private var extensionStore = "init"
var Any.extensionTag: String
    get() = extensionStore
    set(value) { extensionStore = value }
class ExtensionRefBox<T>(var value: T)
val <T> List<T>.extensionAuditLast: T get() = this[lastIndex]
val <T> T.extensionAuditSingleton: List<T> get() = listOf(this)
var <T> ExtensionRefBox<T>.extensionAuditValue: T
    get() = value
    set(newValue) { value = newValue }
class ExtensionFoo {
    override fun toString(): String {
        val p = this::extensionMySimpleName
        return "${p.name}:${p.get()}"
    }
}

class ExtensionFunctionTests {
    @TestAttribute
    fun ext() {
        assertEquals(21, 7.extensionTriple())       // 21
        assertEquals("HI", "hi".extensionShout())   // HI
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
        assertEquals("extensionMySimpleName:Foo", ExtensionFoo().toString())     // bound ref: .name + .get() invokes the ext getter
        val u = String::extensionMySimpleName                              // UNBOUND ext-property ref -> KProperty1<String,String>
        assertEquals("extensionMySimpleName=Foo", "${u.name}=${u.get("x")}")
        val m = ExtensionFoo()::extensionTag                                      // BOUND mutable ext-property ref -> KMutableProperty0
        m.set("hi")                                                 // set() invokes the static set_ accessor w/ captured receiver
        assertEquals("extensionTag=hi", "${m.name}=${m.get()}")

        val genericUnbound = List<String>::extensionAuditLast
        assertEquals("b", genericUnbound.get(listOf("a", "b")))
        val genericBound = listOf(10, 20)::extensionAuditLast
        assertEquals(20, genericBound.get())
        val genericCollectionValue = String::extensionAuditSingleton
        assertEquals("[one]", genericCollectionValue.get("one").toString())

        val genericBox = ExtensionRefBox("before")
        val genericMutableUnbound = ExtensionRefBox<String>::extensionAuditValue
        genericMutableUnbound.set(genericBox, "unbound")
        assertEquals("unbound", genericMutableUnbound.get(genericBox))
        val genericMutableBound = genericBox::extensionAuditValue
        genericMutableBound.set("bound")
        assertEquals("bound", genericMutableBound.get())
    }
}
