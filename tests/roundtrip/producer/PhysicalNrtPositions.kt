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

// Native constructions retain physical generic positions, unlike Kotlin value carriers.
fun nativeNonNull(): System.Tuple2<Comparable<Any?>?, String> = System.Tuple2(null, "value")
fun nativeNullable(): System.Tuple2<Comparable<Any?>?, String?> = System.Tuple2(null, null)
fun nativeRetained(): System.Tuple2<Comparable<String>?, String> = System.Tuple2(null, "value")
fun nativeNested(): System.Tuple2<System.Tuple2<Comparable<Any?>?, String>, String?> =
    System.Tuple2(nativeNonNull(), null)
fun nativeEcho(value: System.Tuple2<Comparable<Any?>?, String>): System.Tuple2<Comparable<Any?>?, String> = value

class NativeNrtSlots(initial: System.Tuple2<Comparable<Any?>?, String>) {
    @ClrField var fieldSlot: System.Tuple2<Comparable<Any?>?, String> = initial
    @ClrField var nullableFieldSlot: System.Tuple2<Comparable<Any?>?, String?> = nativeNullable()
    var propertySlot: System.Tuple2<Comparable<Any?>?, String> = initial
    var nullablePropertySlot: System.Tuple2<Comparable<Any?>?, String?> = nativeNullable()
}

interface NativeNrtExchange<T> {
    fun exchange(value: System.Tuple3<T?, Comparable<Any?>?, String>): System.Tuple3<T?, Comparable<Any?>?, String>
}
class NativeStringNrtExchange : NativeNrtExchange<String> {
    override fun exchange(value: System.Tuple3<String?, Comparable<Any?>?, String>):
        System.Tuple3<String?, Comparable<Any?>?, String> = value
}
