// Property-delegation battery (M2 batch) — migrates the `by`-delegate family of cases/il-* onto the in-process
// NUnit suite. Ordered side-effecting `println`s inside getValue/setValue (the "get/set" proof, observable's block)
// become captured log-list state asserted directly — the STRUCTURE that was the actual subject (delegate dispatch,
// generic instantiation) is unchanged. Top-level declarations are `M2`-prefixed (one assembly = one namespace).
//
// Coverage preserved (old case -> method):
//   il-deleg  -> m2_deleg   custom getValue/setValue delegate; property.name flows through the accessor dispatch
//   il-deleg2 -> m2_deleg2  Delegates.observable / vetoable / notNull
//   il-gendp  -> m2_gendp   #191 GENERIC user delegate `D<T>` backing member/local/top-level/generic-enclosing
//                           delegated props over a REFERENCE (String) and VALUE (Int) type arg (constructed owner)
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import kotlin.reflect.KProperty
import kotlin.properties.Delegates

// ---- il-deleg : custom getValue/setValue; the println side effects become a captured log --------------------------
val m2DelegLog = mutableListOf<String>()
class M2Logged(var backing: Int) {
    operator fun getValue(thisRef: Any?, property: KProperty<*>): Int {
        m2DelegLog.add("get " + property.name)
        return backing
    }
    operator fun setValue(thisRef: Any?, property: KProperty<*>, value: Int) {
        m2DelegLog.add("set " + property.name + " = " + value)
        backing = value
    }
}
class M2Box { var count: Int by M2Logged(0) }

// ---- il-deleg2 : Delegates.observable / vetoable / notNull -------------------------------------------------------
val m2Deleg2Log = mutableListOf<String>()
class M2C {
    var v: Int by Delegates.observable(0) { _, old, new -> m2Deleg2Log.add("$old -> $new") }
    var pos: Int by Delegates.vetoable(0) { _, _, n -> n >= 0 }
    var late: String by Delegates.notNull()
}

// ---- il-gendp : #191 a GENERIC user delegate `D<T>` — the constructed owner is stamped from the receiver type ----
class M2D<T>(private var v: T) {
    operator fun getValue(r: Any?, p: KProperty<*>): T = v
    operator fun setValue(r: Any?, p: KProperty<*>, nv: T) { v = nv }
}
open class M2ViewModelBase { var raised = 0 }
fun <T> M2ViewModelBase.m2ViewModelProperty(initial: T) = M2D(initial)
class M2PersonViewModel : M2ViewModelBase() {
    var name: String by m2ViewModelProperty("John")   // member, reference type arg
    var age: Int by M2D(30)                            // member, value type arg
}
var m2TopName: String by M2D("top")                    // top-level, reference type arg
class M2Container<T>(init: T) { var item: T by M2D(init) }  // generic enclosing class, constructed `$delegate` owner

class DelegatedPropertyTests {
    @TestAttribute
    fun deleg() {
        m2DelegLog.clear()
        val b = M2Box()
        b.count = 7
        val v = b.count
        assertEquals(7, v)                            // 7
        assertEquals("set count = 7", m2DelegLog[0])  // set count = 7
        assertEquals("get count", m2DelegLog[1])      // get count
        assertEquals(2, m2DelegLog.size)              // exactly one set + one get
    }

    @TestAttribute
    fun deleg2() {
        m2Deleg2Log.clear()
        val c = M2C()
        c.v = 1
        c.v = 2
        c.pos = 5
        c.pos = -3                          // vetoed (n >= 0 is false) -> no change
        assertEquals(5, c.pos)              // 5
        c.late = "hi"
        assertEquals("hi", c.late)          // hi
        assertEquals("0 -> 1", m2Deleg2Log[0])  // 0 -> 1
        assertEquals("1 -> 2", m2Deleg2Log[1])  // 1 -> 2
        assertEquals(2, m2Deleg2Log.size)       // observable fired exactly twice (vetoed change did not)
    }

    @TestAttribute
    fun gendp() {
        val pvm = M2PersonViewModel()
        assertEquals("John", pvm.name)      // John
        pvm.name = "Jane"
        assertEquals("Jane", pvm.name)      // Jane
        assertEquals(30, pvm.age)           // 30
        pvm.age = 42
        assertEquals(42, pvm.age)           // 42
        assertEquals("top", m2TopName)      // top
        m2TopName = "changed"
        assertEquals("changed", m2TopName)  // changed
        var loc: Int by M2D(7)              // local, value type arg
        assertEquals(7, loc)                // 7
        loc = 99
        assertEquals(99, loc)               // 99
        val box = M2Container<String>("boxed")  // generic enclosing class, cross-scope access
        assertEquals("boxed", box.item)     // boxed
        box.item = "rebox"
        assertEquals("rebox", box.item)     // rebox
    }
}
