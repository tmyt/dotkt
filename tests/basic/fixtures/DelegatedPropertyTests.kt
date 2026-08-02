// Property-delegation battery (feature fixture) — migrates the `by`-delegate family of cases/il-* onto the in-process
// NUnit suite. Ordered side-effecting `println`s inside getValue/setValue (the "get/set" proof, observable's block)
// become captured log-list state asserted directly — the STRUCTURE that was the actual subject (delegate dispatch,
// generic instantiation) is unchanged. Top-level declarations are `DelegatedProperty`-prefixed (one assembly = one namespace).
//
// Coverage preserved (old case -> method):
//   il-deleg  -> delegatedProperty_deleg   custom getValue/setValue delegate; property.name flows through the accessor dispatch
//   il-deleg2 -> delegatedProperty_deleg2  Delegates.observable / vetoable / notNull
//   il-gendp  -> delegatedProperty_gendp   #191 GENERIC user delegate `D<T>` backing member/local/top-level/generic-enclosing
//                           delegated props over a REFERENCE (String) and VALUE (Int) type arg (constructed owner)
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import kotlin.reflect.KProperty
import kotlin.properties.Delegates

// ---- il-deleg : custom getValue/setValue; the println side effects become a captured log --------------------------
val delegatedPropertyDelegLog = mutableListOf<String>()
class DelegatedPropertyLogged(var backing: Int) {
    operator fun getValue(thisRef: Any?, property: KProperty<*>): Int {
        delegatedPropertyDelegLog.add("get " + property.name)
        return backing
    }
    operator fun setValue(thisRef: Any?, property: KProperty<*>, value: Int) {
        delegatedPropertyDelegLog.add("set " + property.name + " = " + value)
        backing = value
    }
}
class DelegatedPropertyBox { var count: Int by DelegatedPropertyLogged(0) }

// ---- il-deleg2 : Delegates.observable / vetoable / notNull -------------------------------------------------------
val delegatedPropertyDeleg2Log = mutableListOf<String>()
class DelegatedPropertyC {
    var v: Int by Delegates.observable(0) { _, old, new -> delegatedPropertyDeleg2Log.add("$old -> $new") }
    var pos: Int by Delegates.vetoable(0) { _, _, n -> n >= 0 }
    var late: String by Delegates.notNull()
}

// ---- il-gendp : #191 a GENERIC user delegate `D<T>` — the constructed owner is stamped from the receiver type ----
class DelegatedPropertyD<T>(private var v: T) {
    operator fun getValue(r: Any?, p: KProperty<*>): T = v
    operator fun setValue(r: Any?, p: KProperty<*>, nv: T) { v = nv }
}
open class DelegatedPropertyViewModelBase { var raised = 0 }
fun <T> DelegatedPropertyViewModelBase.delegatedPropertyViewModelProperty(initial: T) = DelegatedPropertyD(initial)
class DelegatedPropertyPersonViewModel : DelegatedPropertyViewModelBase() {
    var name: String by delegatedPropertyViewModelProperty("John")   // member, reference type arg
    var age: Int by DelegatedPropertyD(30)                            // member, value type arg
}
var delegatedPropertyTopName: String by DelegatedPropertyD("top")                    // top-level, reference type arg
class DelegatedPropertyContainer<T>(init: T) { var item: T by DelegatedPropertyD(init) }  // generic enclosing class, constructed `$delegate` owner

class DelegatedPropertyTests {
    @TestAttribute
    fun deleg() {
        delegatedPropertyDelegLog.clear()
        val b = DelegatedPropertyBox()
        b.count = 7
        val v = b.count
        assertEquals(7, v)                            // 7
        assertEquals("set count = 7", delegatedPropertyDelegLog[0])  // set count = 7
        assertEquals("get count", delegatedPropertyDelegLog[1])      // get count
        assertEquals(2, delegatedPropertyDelegLog.size)              // exactly one set + one get
    }

    @TestAttribute
    fun deleg2() {
        delegatedPropertyDeleg2Log.clear()
        val c = DelegatedPropertyC()
        c.v = 1
        c.v = 2
        c.pos = 5
        c.pos = -3                          // vetoed (n >= 0 is false) -> no change
        assertEquals(5, c.pos)              // 5
        c.late = "hi"
        assertEquals("hi", c.late)          // hi
        assertEquals("0 -> 1", delegatedPropertyDeleg2Log[0])  // 0 -> 1
        assertEquals("1 -> 2", delegatedPropertyDeleg2Log[1])  // 1 -> 2
        assertEquals(2, delegatedPropertyDeleg2Log.size)       // observable fired exactly twice (vetoed change did not)
    }

    @TestAttribute
    fun gendp() {
        val pvm = DelegatedPropertyPersonViewModel()
        assertEquals("John", pvm.name)      // John
        pvm.name = "Jane"
        assertEquals("Jane", pvm.name)      // Jane
        assertEquals(30, pvm.age)           // 30
        pvm.age = 42
        assertEquals(42, pvm.age)           // 42
        assertEquals("top", delegatedPropertyTopName)      // top
        delegatedPropertyTopName = "changed"
        assertEquals("changed", delegatedPropertyTopName)  // changed
        var loc: Int by DelegatedPropertyD(7)              // local, value type arg
        assertEquals(7, loc)                // 7
        loc = 99
        assertEquals(99, loc)               // 99
        val box = DelegatedPropertyContainer<String>("boxed")  // generic enclosing class, cross-scope access
        assertEquals("boxed", box.item)     // boxed
        box.item = "rebox"
        assertEquals("rebox", box.item)     // rebox
    }
}
