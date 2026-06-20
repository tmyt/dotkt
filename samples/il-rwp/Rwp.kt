import kotlin.properties.ReadWriteProperty
import kotlin.reflect.KProperty

class Trace(var v: Int) : ReadWriteProperty<Any?, Int> {
    override fun getValue(thisRef: Any?, property: KProperty<*>): Int {
        println("get " + property.name)
        return v
    }
    override fun setValue(thisRef: Any?, property: KProperty<*>, value: Int) {
        println("set " + property.name + " = " + value)
        v = value
    }
}

class Box {
    var n: Int by Trace(0)
}

fun main() {
    val b = Box()
    b.n = 5
    println(b.n)
}
