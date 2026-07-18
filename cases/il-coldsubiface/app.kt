// R1 (#90) — an INTERFACE suspend member called through a SUBTYPE static receiver. `drive`'s param is typed
// as the concrete `NumberProducer` (the subtype), not the `Producer` interface; the suspend call `p.produce()`
// is a callInstance keyed on NumberProducer, resolved to its override cold entry. Exercises R1's unconditional
// cold-entry declaration + virtual dispatch through the cold slot from a subtype receiver.
import dotkt.support.blockOn

interface Producer { suspend fun produce(): Int }
class NumberProducer(val base: Int) : Producer {
    override suspend fun produce(): Int = base + 1
}
suspend fun drive(p: NumberProducer): Int = p.produce()

fun main() {
    println(blockOn { drive(NumberProducer(41)) })   // 42
}
