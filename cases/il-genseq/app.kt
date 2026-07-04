// Generic cold-sequence SM: a `sequence { yield(x) }` driven inside a GENERIC function. The suspend
// lambda SM is instantiated over the open type param T; the SequenceBuilderIterator drives its next().
// This is the minimal repro of the collops2 windowed(3) NRE (a generic windowedIterator<T> drive).
fun <T> wrap(x: T) = sequence { yield(x) }.toList()

fun main() {
    println(wrap(5))     // [5]
    println(wrap("hi"))  // [hi]
}
