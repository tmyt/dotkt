// G-2: user-defined generic INTERFACE + classes implementing a constructed instantiation of it.
interface Container<T> {
    fun item(): T
    fun describe(): String
}

class IntBox(val n: Int) : Container<Int> {
    override fun item(): Int = n
    override fun describe(): String = "IntBox holding an Int"
}

class Named(val label: String) : Container<String> {
    override fun item(): String = label
    override fun describe(): String = "Named holding a String"
}

fun main() {
    val a: Container<Int> = IntBox(99)
    println(a.item())        // 99
    println(a.describe())    // IntBox holding an Int

    val b: Container<String> = Named("tag")
    println(b.item())        // tag
    println(b.describe())    // Named holding a String
}
