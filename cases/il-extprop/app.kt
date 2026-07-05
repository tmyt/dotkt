// C7: cross-module top-level extension-property getters must route to `get_<name>(receiver)`, not a
// dropped-receiver static-field read (`field <AppKt>.lastIndex not found`). Covers a GENERIC getter
// (List<T>.lastIndex — the resolved type args are carried so the generic get_lastIndex[T] instantiates)
// and non-generic getters (CharSequence.lastIndex, Int.absoluteValue/.sign).
import kotlin.math.absoluteValue
import kotlin.math.sign
fun main() {
    println(listOf(10, 20, 30).lastIndex)
    println(listOf("a", "b").lastIndex)
    println("hi".lastIndex)
    println((-3).absoluteValue)
    println((-3).sign)
    println(3.sign)
    println(0.sign)
}
