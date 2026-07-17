// #70: a TOP-LEVEL delegated property with an ARBITRARY getValue/setValue provider (`var counter by Store(0)`). Its
// storage is a static `counter$delegate` field on the file class; the access routes to the delegate's getValue/setValue
// with a NULL thisRef + a materialized KProperty — not a raw static-field load (there is no `counter` field). Was a
// whole-compile abort (only member/local delegated properties were routed).
import kotlin.reflect.KProperty

class Store(var backing: Int) {
    operator fun getValue(thisRef: Any?, prop: KProperty<*>): Int = backing
    operator fun setValue(thisRef: Any?, prop: KProperty<*>, v: Int) { backing = v }
}

class ReadOnlyStore(val s: String) {
    operator fun getValue(thisRef: Any?, prop: KProperty<*>): String = s
}

var counter by Store(0)
val label by ReadOnlyStore("init")

fun main() {
    println(counter)                  // 0   (getValue)
    counter = 42
    println(counter)                  // 42  (setValue then getValue)
    println(label)                    // init (read-only getValue)
}
