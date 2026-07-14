// #21: a bound reference to a top-level EXTENSION property (`this::extProp`) — the frontend models the ext
// receiver in the getter's ExtensionReceiver slot; the bound ref resolves to KProperty0<V>. kotc lowers it to
// a KProperty0 lift whose get() invokes the static ext getter with the captured receiver (BirEmitterLifts).
// Using the VALUE always worked; only the bound `::` REFERENCE was rejected ("KProperty2 has no lowering").
val Any.mySimpleName: String get() = "Foo"

// A MUTABLE (var) top-level extension property -> a bound `this::tag` resolves to KMutableProperty0<String>; the
// lift's set() must invoke the static set_ accessor with the captured receiver + value (setter-sig path).
private var store = "init"
var Any.tag: String
    get() = store
    set(value) { store = value }

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
    val m = Foo()::tag                 // BOUND mutable ext-property ref -> KMutableProperty0<String>
    m.set("hi")                        // set() invokes the static set_tag accessor with the captured receiver + value
    println("${m.name}=${m.get()}")    // tag=hi
}
