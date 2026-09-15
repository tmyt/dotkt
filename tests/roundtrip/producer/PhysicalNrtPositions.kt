package roundtrip.physicalnrtpositions

import kotlin.clr.ClrField

class NrtPair<A, B>(val first: A, val second: B)

fun collapsedNonNull(): NrtPair<Comparable<Any?>?, String> = NrtPair(null, "value")
fun collapsedNullable(): NrtPair<Comparable<Any?>?, String?> = NrtPair(null, null)
fun collapsedHead(): Comparable<Any?>? = null
fun retainedGeneric(): NrtPair<Comparable<String>?, String> = NrtPair(null, "retained")
fun echoCollapsed(value: NrtPair<Comparable<Any?>?, String>): NrtPair<Comparable<Any?>?, String> = value
fun nestedCollapsed(): NrtPair<NrtPair<Comparable<Any?>?, String>, String?> = NrtPair(collapsedNonNull(), null)

class NrtSlots(initial: NrtPair<Comparable<Any?>?, String>) {
    @ClrField var fieldSlot: NrtPair<Comparable<Any?>?, String> = initial
    var propertySlot: NrtPair<Comparable<Any?>?, String> = initial
    @ClrField var nullableFieldSlot: NrtPair<Comparable<Any?>?, String?> = collapsedNullable()
    var nullablePropertySlot: NrtPair<Comparable<Any?>?, String?> = collapsedNullable()
}

class NrtTriple<A, B, C>(val first: A, val second: B, val third: C)
interface NrtExchange<T> {
    fun exchange(value: NrtTriple<T?, Comparable<Any?>?, String>): NrtTriple<T?, Comparable<Any?>?, String>
}
class StringNrtExchange : NrtExchange<String> {
    override fun exchange(value: NrtTriple<String?, Comparable<Any?>?, String>):
        NrtTriple<String?, Comparable<Any?>?, String> = value
}
