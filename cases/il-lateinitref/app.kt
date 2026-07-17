// #66: a callable reference to a `lateinit var` property (`b::name`, `Box::name`) — its storage is a plain backing
// field (no get_/set_ accessor slot), so the lifted KProperty class reads/writes the field directly
// (lateinitGet/setFieldExpr) rather than an accessor call. Was a whole-compile abort. Uses a PUBLIC lateinit (a
// private one referenced via `this::name` additionally needs bir2cir CrossClassPrivateWidening to cover
// lateinitGet/setFieldExpr — reported separately).
class Box {
    lateinit var name: String
}

fun main() {
    val b = Box()
    b.name = "hello"
    val ref = b::name                 // bound KMutableProperty0 over a lateinit backing field
    println(ref.get())                // hello
    ref.set("world")
    println(b.name)                   // world
    println(ref.get())                // world

    val uref = Box::name              // unbound KMutableProperty1 (receiver supplied at get/set)
    val b2 = Box()
    uref.set(b2, "unbound")
    println(uref.get(b2))             // unbound
    println(uref.name)                // name  (KProperty.name)
}
