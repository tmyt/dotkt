// #21: a bound reference to a top-level EXTENSION property (`this::extProp`) — the frontend models the ext
// receiver in the getter's ExtensionReceiver slot; the bound ref resolves to KProperty0<V>. kotc lowers it to
// a KProperty0 lift whose get() invokes the static ext getter with the captured receiver (BirEmitterLifts).
// Using the VALUE always worked; only the bound `::` REFERENCE was rejected ("KProperty2 has no lowering").
val Any.mySimpleName: String get() = "Foo"

class Foo {
    // The exact failing construct from the issue: a bound ext-property reference inside a string template.
    override fun toString(): String {
        val p = this::mySimpleName
        return "${p.name}:${p.get()}"
    }
}

fun main() {
    println(Foo())                     // mySimpleName:Foo  (bound ref: .name from ClrPropertyStub, .get() invokes the ext getter)
    val u = String::mySimpleName       // UNBOUND ext-property ref -> KProperty1<String,String>
    println("${u.name}=${u.get("x")}") // mySimpleName=Foo
}
