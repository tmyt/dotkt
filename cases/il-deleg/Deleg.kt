import kotlin.reflect.KProperty

class Logged(var backing: Int) {
    operator fun getValue(thisRef: Any?, property: KProperty<*>): Int {
        println("get " + property.name)
        return backing
    }
    operator fun setValue(thisRef: Any?, property: KProperty<*>, value: Int) {
        println("set " + property.name + " = " + value)
        backing = value
    }
}

class Box {
    var count: Int by Logged(0)
}

fun main() {
    val b = Box()
    b.count = 7
    println(b.count)
}
