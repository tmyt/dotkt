// #57: a property REFERENCE to `length` on a USER class implementing CharSequence lifts faithfully — its
// accessor resolves on the user class's OWN emitted get_length slot (its synthesized dotkt$CharSequence
// implementation), whether the override is DIRECT (A) or INHERITED through an intermediate (B : A). The
// deferral now keys on the accessor's RESOLVED declaring owner (getterFn.parent), so a user owner is
// enabled while a .NET-mapped owner (String/StringBuilder/bare CharSequence, whose slot bir2cir renames)
// stays deferred — replacing the retired override-chain walk that over-deferred DIRECT + missed INDIRECT.
import kotlin.reflect.KProperty0
import kotlin.reflect.KProperty1
open class A(val s: String) : CharSequence {
    override val length: Int get() = s.length
    override fun get(index: Int): Char = s[index]
    override fun subSequence(startIndex: Int, endIndex: Int): CharSequence = A(s.substring(startIndex, endIndex))
}
class B(s: String) : A(s)   // B inherits length INDIRECTLY through A
fun main() {
    val a = A("hello")
    val b = B("worldd")
    val da: KProperty0<Int> = a::length        // DIRECT, bound
    val db: KProperty0<Int> = b::length        // INDIRECT, bound
    val ua: KProperty1<A, Int> = A::length     // DIRECT, unbound
    val ub: KProperty1<B, Int> = B::length     // INDIRECT, unbound
    println(da.get())    // 5
    println(db.get())    // 6
    println(ua.get(a))   // 5
    println(ub.get(b))   // 6
    println(da.name)     // length
}
