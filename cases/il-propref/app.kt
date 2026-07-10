// #70: `::prop` callable references lower to a REAL kotlin.reflect.KProperty0/KMutableProperty0/KProperty1
// implementation (kotc's propertyRef), not the retired `dotkt$KProperty` synthetic name-bag.
// #73 Wave 6 (D3): the lifted class extends the real stdlib `kotlin.reflect.ClrPropertyStub<V>` for its
// name/annotations members. The generic-context and app-class references below witness the base-with-baseArgs
// shape for a `tv` and a TypeBuilder-arg vType (branches the plain `Int` references above do not exercise).
import kotlin.reflect.KProperty0
import kotlin.reflect.KMutableProperty1

var x: Int = 1

class Obj(var p: Int)

fun readK(kp: KProperty0<Int>): Int = kp.get()

class Box<T>(val value: T)
fun <T> refOf(b: Box<T>): KProperty0<T> = b::value   // generic context: vType is a `tv`, freeTps non-empty

class Payload(val tag: String)
class Holder(var pay: Payload)                        // vType is an app-declared TypeBuilder class

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

    println(refOf(Box("g")).get())            // g  — generic-lift `tv` vType, inherited get_name/get_annotations
    val hp: KMutableProperty1<Holder, Payload> = Holder::pay
    val h = Holder(Payload("t1"))
    hp.set(h, Payload("t2"))
    println(hp.get(h).tag)                     // t2 — app-class vType, unbound mutable ref
    println(hp.name)                           // pay
}
