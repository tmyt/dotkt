// #70: `::prop` callable references lower to a REAL kotlin.reflect.KProperty0/KMutableProperty0/KProperty1
// implementation (kotc's propertyRef), not the retired `dotkt$KProperty` synthetic name-bag.
import kotlin.reflect.KProperty0

var x: Int = 1

class Obj(var p: Int)

fun readK(kp: KProperty0<Int>): Int = kp.get()

fun main() {
    println(::x.name)
    println(::x.get())
    x = 2
    ::x.set(99)
    println(x)
    println((::x)())

    val obj = Obj(7)
    println(obj::p.get())
    println(Obj::p.get(obj))
    println(readK(::x))
}
