// Local delegated properties (`val x by ...` INSIDE a function) — IrLocalDelegatedProperty.
import kotlin.reflect.KProperty

// An arbitrary (duck-typed) delegate class: getValue uppercases, setValue stores.
class UpperDelegate(private var v: String) {
    operator fun getValue(thisRef: Any?, property: KProperty<*>): String = v.uppercase()
    operator fun setValue(thisRef: Any?, property: KProperty<*>, value: String) { v = value }
}

fun main() {
    // `by lazy` as a LOCAL property (memoized).
    val lazyVal: Int by lazy { 40 + 2 }
    println(lazyVal)        // 42
    println(lazyVal)        // 42

    // A custom delegate class on a local `var`.
    var upper: String by UpperDelegate("hi")
    println(upper)          // HI
    upper = "world"
    println(upper)          // WORLD
}
