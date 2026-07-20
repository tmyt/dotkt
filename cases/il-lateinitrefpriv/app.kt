// #155: a PRIVATE `lateinit var` referenced via `this::name` INSIDE its own class. The bound KMutableProperty0 is
// lifted into a SEPARATE top-level CLR PropRef class that reads/writes the backing field DIRECTLY (lateinitGet on
// get, setFieldExpr on set), not through a get_/set_ accessor. Because the field is PRIVATE, that cross-class field
// access is illegal on the CLR (System.FieldAccessException at runtime) unless bir2cir CrossClassPrivateWidening
// widens the lateinitGet/setFieldExpr node kinds (it previously covered only field/setField). Sibling of the #66
// il-lateinitref case, which uses a PUBLIC lateinit and so never needed the widening.
class Box {
    private lateinit var name: String
    fun makeRef(): kotlin.reflect.KMutableProperty0<String> {
        name = "init"
        return this::name              // bound KMutableProperty0 over a PRIVATE lateinit backing field
    }
}

fun main() {
    val b = Box()
    val ref = b.makeRef()
    println(ref.get())                 // init      (lateinitGet through the lifted PropRef class)
    ref.set("changed")                 // setFieldExpr through the lifted PropRef class
    println(ref.get())                 // changed
    println(ref.name)                  // name      (KProperty.name)
}
