// #89: a TOP-LEVEL or COMPANION `val`/`var` with BOTH a backing field (initializer) AND a custom accessor must
// INVOKE the accessor, not read/write the raw static field (which skipped it). Covers read (custom getter),
// write (custom setter), and the two independent read/write pairings — since getter and setter defaultness are
// decided separately. A plain `object` property is the already-working control.

val topProp: Int = 41
    get() = field + 1                       // read -> 42 (getter, not the raw 41)

var topVar: Int = 0                         // DEFAULT getter (raw field read) + CUSTOM setter
    set(value) { field = value + 5 }

var topGetVar: Int = 100                    // CUSTOM getter + DEFAULT setter (the reverse pairing)
    get() = field - 1

object Obj {
    val cProp: Int = 10
        get() = field * 2                   // control: object property getter already honored -> 20
}

class Host {
    companion object {
        val kProp: Int = 7
            get() = field + 100             // read -> 107 (getter, not the raw 7)

        var kVar: Int = 0                   // DEFAULT getter + CUSTOM setter
            set(value) { field = value * 2 }
    }
}

fun main() {
    println(topProp)                        // 42
    println(Host.kProp)                     // 107
    println(Obj.cProp)                      // 20
    topVar = 10
    println(topVar)                         // custom setter: field = 10 + 5 = 15
    Host.kVar = 3
    println(Host.kVar)                      // custom setter: field = 3 * 2 = 6
    topGetVar = 50                          // default setter: field = 50
    println(topGetVar)                      // custom getter: field - 1 = 49
}
